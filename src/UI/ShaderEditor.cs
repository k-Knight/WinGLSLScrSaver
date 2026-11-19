namespace WinGLSLScrSaver;

// /e [path]: live shader editor - Scintilla code pane with GLSL highlighting
// and an embedded live preview. Edits recompile debounced (~800 ms); errors
// land in the status bar and never kill the preview.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ScintillaNET;
using WinTimer = System.Windows.Forms.Timer;

internal static class ShaderEditor
{
        // Thread-level per-monitor DPI v2 (the single-file apphost manifest
        // already fixes the process context, so SetHighDpiMode returns false).
        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

        private static readonly IntPtr DpiAwarenessPerMonitorV2 = new IntPtr(-4);

        public static void Run(string path)
        {
        Log.Verbose = true;
        Program.DiagMode = -1; // per-window render path (no shared canvas, no diag fills)
        Log.W($"Shader editor starting (path='{path ?? "<none>"}')");

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            string folder = !string.IsNullOrWhiteSpace(Program.Cfg.ShaderFolder)
                            && Directory.Exists(Program.Cfg.ShaderFolder)
                ? Program.Cfg.ShaderFolder : Settings.AppDataDir;
            if (string.IsNullOrWhiteSpace(path))
            {
                string[] pick = Program.ListShaders(folder);
                if (pick.Length > 0) path = pick[Random.Shared.Next(pick.Length)];
            }
        }

        Program.ActiveShaderPath = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
        Log.W($"Editor shader file: '{Program.ActiveShaderPath ?? "<embedded default; Ctrl+S to save>"}'");

        // UI-thread exceptions never reach AppDomain.UnhandledException.
        Application.ThreadException += (_, e) => Log.Ex("UI thread", e.Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // Must run before the first window handle exists.
        try
        {
            IntPtr prev = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
            Log.W($"DPI: thread context -> PerMonitorV2 (prev=0x{prev.ToInt64():X})");
        }
        catch (Exception ex) { Log.Ex("DPI thread context", ex); }

        Application.Run(new EditorForm(path));
    }

    internal sealed class EditorForm : Form
    {
        // ---- theme ---------------------------------------------------------
        static readonly Color Back = Color.FromArgb(30, 30, 30);      // #1E1E1E editor bg
        static readonly Color Fore = Color.FromArgb(220, 220, 220);   // #DCDCDC default text
        static readonly Color GutterFore = Color.FromArgb(133, 133, 133);
        static readonly Color CommentCol = Color.FromArgb(106, 153, 85);    // #6A9955
        static readonly Color KeywordCol = Color.FromArgb(86, 156, 214);    // #569CD6
        static readonly Color FunctionCol = Color.FromArgb(220, 220, 170);  // #DCDCAA
        static readonly Color NumberCol = Color.FromArgb(181, 206, 168);    // #B5CEA8
        static readonly Color PreprocCol = Color.FromArgb(155, 155, 155);
        static readonly Color OkCol = Color.FromArgb(120, 200, 120);
        static readonly Color ErrCol = Color.FromArgb(240, 110, 110);

        // ---- highlighting patterns ----------------------------------------
        static readonly Regex CommentRx = new(@"//[^\n]*|/\*[\s\S]*?\*/", RegexOptions.Compiled);
        static readonly Regex PreprocRx = new(@"(?m)^[ \t]*#[^\n]*", RegexOptions.Compiled);
        static readonly Regex NumberRx = new(@"(?<![\w.])\d+\.?\d*(?:[eE][+-]?\d+)?f?", RegexOptions.Compiled);
        static readonly Regex KeywordRx = new(
            @"\b(?:void|bool|int|uint|float|double|vec2|vec3|vec4|bvec[234]|ivec[234]|uvec[234]|" +
            @"mat[234]|mat2x2|mat3x3|mat4x4|sampler2D|sampler3D|samplerCube|usampler2D|isampler2D|" +
            @"uniform|in|out|inout|const|layout|binding|location|struct|return|if|else|for|while|do|" +
            @"break|continue|discard|true|false|precision|highp|mediump|lowp|flat|smooth)\b",
            RegexOptions.Compiled);
        static readonly Regex FunctionRx = new(
            @"\b(?:mix|clamp|smoothstep|step|length|distance|dot|cross|normalize|reflect|refract|" +
            @"pow|exp|exp2|log|log2|sqrt|inversesqrt|abs|sign|floor|ceil|fract|mod|min|max|" +
            @"sin|cos|tan|asin|acos|atan|sinh|cosh|tanh|" +
            @"texture|texture2D|textureLod|texelFetch|imageLoad|imageStore)\b",
            RegexOptions.Compiled);
        static readonly Regex ErrorLineRx = new(@"(?:^|[:\s])(\d+)(?::(\d+))?", RegexOptions.Compiled); // line, optional column

        // Scintilla user styles (0-31 are predefined).
        const int StyComment = 32;
        const int StyPreproc = 33;
        const int StyKeyword = 34;
        const int StyFunction = 35;
        const int StyNumber = 36;
        const int ErrMarker = 0;           // margin marker: the compile-error line
        const int ErrIndicator = 8;        // squiggle underline: the whole error line

        // ---- controls ------------------------------------------------------
        private readonly Scintilla _code;   // native per-byte styling, own line-number margin; styling never scrolls
        private readonly SplitContainer _split;
        private readonly Panel _previewHost;
        private readonly Label _statusLeft;   // compile + texture state
        private readonly Label _statusPath;   // file path
        private readonly Label _statusPos;    // Ln/Col
        private readonly ToolStrip _toolbar;
        private WinTimer _compileTimer;
        private WinTimer _highlightTimer;
        private WinTimer _fitTimer;
        private WinTimer _renderTimer;
        private WinTimer _watchTimer;              // debounces external file changes
        private WinTimer _pollTimer;               // polling fallback for external changes
        private DateTime _lastPollWrite;           // last mtime the poller saw
        private long _lastPollLen = -1;            // last size the poller saw
        private FileSystemWatcher _watcher;        // reloads the file when another program writes it
        private string _watchPath;                 // full path the watcher is aimed at
        private volatile string _pendingWatchPath; // set from watcher thread, read on UI thread
        private int _watchRetries;                 // locked-file retry budget for external changes
        private string _lastSavedText;             // what our own Save wrote (watcher self-match)
        private string _diskText;                  // content currently on disk - the up-to-date check

        // Compiles: thread pool -> UI thread apply; Present: preview's present thread.
        private int _compileGen;                        // invalidates stale compiles
        private string _lastCompiledSrc;                // skip recompile when unchanged
        private string _lastCompilePath;                // path the last successful compile ran with
        private bool _lastCompileOk;
        private string _lastHighlightText;              // skip recolor when unchanged
        private int _highlightGen;                      // invalidates stale highlight scans
        private volatile bool _highlightInFlight;       // one token scan at a time
        // Diagnostics: 1 Hz viewport watcher.
        private WinTimer _diagTimer;
        private int _lastDiagFirstLine = -1;            // last state the watcher logged
        private int _highlightTraceLogs;                // rate-limit caller diagnostics
        private int _textChangedLogs;
        private int _slowRenderLogs;                    // rate-limit RenderTick diagnostics
        private double _lastSlowRenderLog;              // Stopwatch seconds of the last one
        private RenderWindow _preview;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private double _animTime;                       // preview clock - pauses when frames queue up
        private double _lastTick;                       // Stopwatch seconds at the last RenderTick
        private long _lastStallMs;                      // stall already reported to the status bar
        private string _savePath;
        private bool _loading;          // programmatic text set (no dirty flag, no timers)
        private bool _dirty;
        private bool _firstCompile = true;
        private int _errorUserLine = -1; // parsed from the last compile error
        private string _errorText;        // full message for the dwell tooltip
        private int _errRangeStart, _errRangeLen; // underlined error range
        private Rectangle _restoredBounds;  // editor_bounds from config (pre-DPI-scale)
        private bool _hasRestoredBounds;

        public EditorForm(string path)
        {
            Text = "WinGLSLScrSaver - Shader Editor";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1280, 720);
            MinimumSize = new Size(860, 520);
            // Fixed pixel values below scale with the monitor DPI.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Log.W($"Editor form DPI: DeviceDpi={DeviceDpi}");

            // editor_bounds="x,y,w,h" in physical px of editor_dpi; the
            // DPI rescale happens in Shown (real per-monitor DPI there).
            // Only when on a connected screen.
            Match mb = Regex.Match(Program.Cfg.EditorBounds, @"^(-?\d+),(-?\d+),(\d+),(\d+)$");
            if (mb.Success)
            {
                var b = new Rectangle(int.Parse(mb.Groups[1].Value), int.Parse(mb.Groups[2].Value),
                    int.Parse(mb.Groups[3].Value), int.Parse(mb.Groups[4].Value));
                if (b.Width >= MinimumSize.Width && b.Height >= MinimumSize.Height
                    && SystemInformation.VirtualScreen.IntersectsWith(b))
                {
                    StartPosition = FormStartPosition.Manual;
                    _restoredBounds = b;
                    _hasRestoredBounds = true;
                }
            }
            BackColor = Back;
            KeyPreview = true;
            Font = new Font("Segoe UI", 9f);

            // ---- toolbar ---------------------------------------------------
            _toolbar = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                BackColor = Color.FromArgb(37, 37, 37),
                Renderer = new ToolStripProfessionalRenderer(new DarkColorTable()),
                Padding = new Padding(4, 2, 4, 2),
            };
            _toolbar.Items.Add(Button("Open… (Ctrl+O)", (_, _) => OpenFile()));
            _toolbar.Items.Add(Button("Save (Ctrl+S)", (_, _) => Save()));
            _toolbar.Items.Add(Button("Save As…", (_, _) => SaveAs()));
            _toolbar.Items.Add(Button("Reload", (_, _) => Reload()));
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(Button("Apply (F5)", (_, _) => Recompile(true)));
            _toolbar.Items.Add(new ToolStripSeparator());
            _toolbar.Items.Add(Button("Insert texture example", (_, _) => InsertSnippet()));

            // ---- editor pane: Scintilla (per-byte styling, never scrolls) ----
            _code = new Scintilla { Dock = DockStyle.Fill };
            _code.HandleCreated += (_, _) =>
            {
                InitEditorStyles(); // needs the native handle (re-created on DPI rescale)
                _lastHighlightText = null;
                Restart(_highlightTimer);
                Log.W("Scintilla handle (re)created -> styles applied, re-highlight armed");
            };
            _code.TextChanged += (_, _) =>
            {
                UpdateLineNumberMargin();
                if (_loading) return;
                if (_textChangedLogs < 40)
                {
                    _textChangedLogs++;
                    Log.W($"TextChanged fired (nobody typing?) len={_code.TextLength}");
                }
                // Full compare only when the length matches (undo back to
                // the saved text clears the dirty flag).
                if (_savePath == null || _code.TextLength != _diskText.Length)
                    _dirty = true;
                else
                    _dirty = _code.Text != _diskText;
                UpdateTitle();
                Restart(_highlightTimer);
                Restart(_compileTimer);
            };
            _code.UpdateUI += (_, _) => UpdateCaretStatus();
            _code.DwellStart += (s2, e2) =>
            {
                if (e2.Position < 0) return; // not over text
                if (e2.Position >= _errRangeStart && _errorText != null)
                    _code.CallTipShow(e2.Position, _errorText);
            };
            _code.DwellEnd += (_, _) => _code.CallTipCancel();

            var codePane = new Panel { Dock = DockStyle.Fill, BackColor = Back };
            codePane.Controls.Add(_code);

            // ---- preview pane ----------------------------------------------
            _previewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(37, 37, 37),
            };
            // Panel1MinSize / Panel2MinSize / SplitterDistance are set in
            // Shown() - assigning them here (container still narrow) throws.
            _split.Panel1.Controls.Add(codePane);
            _split.Panel2.Controls.Add(_previewHost);
            _split.Panel2.BackColor = Color.Black;
            // Keep the preview width on form resize: Panel1 absorbs the change.
            _split.FixedPanel = FixedPanel.Panel2;
            _split.SplitterMoved += (_, _) => Restart(_fitTimer);
            _previewHost.Resize += (_, _) => Restart(_fitTimer);

            // ---- status bar (bottom of the preview pane) --------------------
            // Two rows: compile state / errors (full width, click = jump to the
            // error line) above file path + Ln/Col. Added after _previewHost so
            // it docks first (WinForms docks the last-added control first).
            var status = new Panel { Dock = DockStyle.Bottom, Height = 48, BackColor = Color.FromArgb(37, 37, 37) };
            status.Paint += (_, e) =>
            {
                using var pen = new Pen(Color.FromArgb(60, 60, 60));
                e.Graphics.DrawLine(pen, 0, 0, status.Width, 0);
                e.Graphics.DrawLine(pen, 0, 23, status.Width, 23);
            };
            _statusLeft = new Label   // row 1: errors + compile state
            {
                Dock = DockStyle.Top, Height = 23, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = OkCol, BackColor = Color.FromArgb(37, 37, 37),
                Padding = new Padding(8, 0, 8, 0), AutoSize = false, AutoEllipsis = true,
            };
            var row2 = new Panel   // row 2: file path | Ln/Col
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(37, 37, 37), Padding = new Padding(0, 1, 0, 0),
            };
            _statusPath = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = GutterFore,
                BackColor = Color.FromArgb(37, 37, 37), Padding = new Padding(8, 0, 8, 0),
                AutoSize = false, AutoEllipsis = true,
            };
            _statusPos = new Label
            {
                Dock = DockStyle.Right, Width = 150, TextAlign = ContentAlignment.MiddleRight, ForeColor = GutterFore,
                BackColor = Color.FromArgb(37, 37, 37), Padding = new Padding(8, 0, 8, 0),
                AutoSize = false,
            };
            _statusLeft.Click += (_, _) => JumpToError();
            row2.Controls.Add(_statusPath);
            row2.Controls.Add(_statusPos);
            status.Controls.Add(row2);   // fill first...
            status.Controls.Add(_statusLeft);  // ...then the top strip

            Controls.Add(_split);
            Controls.Add(_toolbar);
            _split.Panel2.Controls.Add(status);

            // ---- timers ----------------------------------------------------
            _compileTimer = Make(800, () =>
            {
                if (_textChangedLogs < 40) Log.W("CompileTimerTick");
                _compileTimer.Stop();
                Recompile();
            });
            _highlightTimer = Make(300, () =>
            {
                if (_textChangedLogs < 40) Log.W("HighlightTimerTick");
                _highlightTimer.Stop();
                Highlight();
            });
            _fitTimer = Make(200, () => { _fitTimer.Stop(); FitPreview(); });
            _renderTimer = new WinTimer { Interval = 15 };
            _renderTimer.Tick += (_, _) => RenderTick();
            _watchTimer = Make(250, CheckExternalChange);
            // FSW misses events (buffer overflow, temp-file+rename saves) -
            // 1 s mtime/size poll feeds the same debounced check.
            _pollTimer = Make(1000, PollDiskChange);
            _pollTimer.Start();
            // 1 Hz viewport watcher: logs scroll/selection state whenever it
            // changed - catches resets that happen WITHOUT a VScroll event
            // and timestamps them (the "resets every second" case).
            _diagTimer = Make(1000, DiagScrollTick);
            _diagTimer.Start(); // 1 Hz viewport watcher

            // ---- keys ------------------------------------------------------
            KeyDown += (_, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; Save(); }
                else if (e.Control && e.KeyCode == Keys.O) { e.SuppressKeyPress = true; OpenFile(); }
                else if (e.KeyCode == Keys.F5) { e.SuppressKeyPress = true; Recompile(true); }
            };

            // ---- initial content -------------------------------------------
            _loading = true;
            if (path != null && File.Exists(path))
            {
                try { _code.Text = File.ReadAllText(path); }
                catch (Exception ex) { Log.Ex("Editor load", ex); }
                _savePath = path;
            }
            else
            {
                _code.Text = ShaderAdapter.DefaultShader;
                _savePath = null;
            }
            _diskText = _code.Text;
            _loading = false;
            UpdateTitle();
            Highlight();

            Shown += (_, _) =>
            {
                // Window bounds FIRST - the splitter clamp below must see
                // the final width, or it clamps against the default 1280.
                if (_hasRestoredBounds)
                {
                    try
                    {
                        int savedDpi = Program.Cfg.EditorDpi > 0 ? Program.Cfg.EditorDpi : 96;
                        float dpiScale = (float) DeviceDpi / savedDpi;
                        var b = _restoredBounds;
                        if (dpiScale != 1f)
                            b = new Rectangle(
                                (int) Math.Round(b.X * dpiScale), (int) Math.Round(b.Y * dpiScale),
                                (int) Math.Round(b.Width * dpiScale), (int) Math.Round(b.Height * dpiScale));
                        if (b.Width >= MinimumSize.Width && b.Height >= MinimumSize.Height
                            && SystemInformation.VirtualScreen.IntersectsWith(b))
                            DesktopBounds = b;
                    }
                    catch { /* best effort */ }
                }
                // Splitter 1:1 restore - only clamp when it truly cannot fit.
                try
                {
                    _split.Panel1MinSize = 240;
                    _split.Panel2MinSize = 200;
                    int savedSplit = Program.Cfg.EditorSplitter > 0
                        ? Program.Cfg.EditorSplitter : 700;
                    if (Program.Cfg.EditorDpi > 0 && Program.Cfg.EditorDpi != DeviceDpi)
                        savedSplit = (int) Math.Round(
                            savedSplit * (float) DeviceDpi / Program.Cfg.EditorDpi);
                    if (savedSplit >= _split.Panel1MinSize
                        && savedSplit <= _split.Width - _split.Panel2MinSize - _split.SplitterWidth)
                        _split.SplitterDistance = savedSplit; // exact restore
                    else
                        _split.SplitterDistance = Math.Max(_split.Panel1MinSize,
                            Math.Min(savedSplit, _split.Width - _split.Panel2MinSize - _split.SplitterWidth));
                }
                catch { /* never let the layout nicety kill the editor */ }
                // After handle creation - the watcher callback needs BeginInvoke.
                if (_savePath != null) AimWatcher(_savePath);
                StartPreview();
            };
            FormClosed += (_, _) =>
            {
                // Persist window position/size + splitter.
                try
                {
                    var b = DesktopBounds;
                    if (b.Width >= MinimumSize.Width && b.Height >= MinimumSize.Height
                        && SystemInformation.VirtualScreen.IntersectsWith(b))
                        Program.Cfg.EditorBounds = $"{b.X},{b.Y},{b.Width},{b.Height}";
                    Program.Cfg.EditorSplitter = _split.SplitterDistance;
                    Program.Cfg.EditorDpi = DeviceDpi;
                    Program.Cfg.Save();
                }
                catch { /* persistence is best effort */ }
                try { _code.Dispose(); } catch { } // before form teardown
                _watcher?.Dispose();
                ShutdownPreview();
            };
        }

        private bool Dirty => _dirty;

        private void UpdateTitle()
        {
            string name = _savePath != null ? Path.GetFileName(_savePath) : "(unsaved)";
            Text = $"{name}{(_dirty ? " *" : "")} - WinGLSLScrSaver Shader Editor";
            _statusPath.Text = _savePath ?? "not saved yet - Ctrl+S";
        }

        private int CaretLine => _code.CurrentLine;
        private int CaretCol => _code.CurrentPosition - _code.Lines[_code.CurrentLine].Position;

        private void UpdateCaretStatus()
        {
            _statusPos.Text = $"Ln {CaretLine + 1}, Col {CaretCol + 1}";
        }

        // ---- toolbar helpers ----------------------------------------------
        private static ToolStripButton Button(string text, EventHandler onClick)
        {
            var b = new ToolStripButton(text)
            {
                ForeColor = Fore,
                DisplayStyle = ToolStripItemDisplayStyle.Text,
                ToolTipText = text,
            };
            b.Click += onClick;
            return b;
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Color.FromArgb(37, 37, 37);
            public override Color ImageMarginGradientBegin => Color.FromArgb(37, 37, 37);
            public override Color ImageMarginGradientMiddle => Color.FromArgb(37, 37, 37);
            public override Color ImageMarginGradientEnd => Color.FromArgb(37, 37, 37);
            public override Color MenuBorder => Color.FromArgb(60, 60, 60);
            public override Color MenuItemBorder => Color.FromArgb(60, 60, 60);
            public override Color ButtonSelectedHighlight => Color.FromArgb(60, 60, 60);
            public override Color ButtonSelectedGradientBegin => Color.FromArgb(60, 60, 60);
            public override Color ButtonSelectedGradientMiddle => Color.FromArgb(60, 60, 60);
            public override Color ButtonSelectedGradientEnd => Color.FromArgb(60, 60, 60);
            public override Color ButtonPressedGradientBegin => Color.FromArgb(80, 80, 80);
            public override Color ButtonPressedGradientMiddle => Color.FromArgb(80, 80, 80);
            public override Color ButtonPressedGradientEnd => Color.FromArgb(80, 80, 80);
        }

        private static string MonoFontName()
        {
            try
            {
                using var fonts = new System.Drawing.Text.InstalledFontCollection();
                foreach (var f in fonts.Families)
                    if (f.Name is "Cascadia Mono" or "Cascadia Code")
                        return f.Name;
            }
            catch { }
            return "Consolas";
        }

        private static WinTimer Make(int interval, Action tick)
        {
            var t = new WinTimer { Interval = interval };
            t.Tick += (_, _) => tick();
            return t;
        }

        // SplitterMoved fires before the timers exist - null-tolerant Restart.
        private static void Restart(WinTimer t) { if (t == null) return; t.Stop(); t.Start(); }

        // ---- code styles / line-number margin ------------------------------
        private void InitEditorStyles() // needs the native handle
        {
            try
            {
                var def = _code.Styles[Style.Default];
                def.Font = MonoFontName();
                def.SizeF = 8.5f;
                def.ForeColor = Fore;
                def.BackColor = Back;
                _code.StyleClearAll();

                _code.Styles[StyComment].ForeColor = CommentCol;
                _code.Styles[StyPreproc].ForeColor = PreprocCol;
                _code.Styles[StyKeyword].ForeColor = KeywordCol;
                _code.Styles[StyFunction].ForeColor = FunctionCol;
                _code.Styles[StyNumber].ForeColor = NumberCol;

                // Opaque selection color close to the theme - every token color stays readable.
                _code.SetSelectionBackColor(true, Color.FromArgb(70, 90, 120));
                _code.SetSelectionForeColor(true, Color.White);

                var ln = _code.Styles[Style.LineNumber];
                ln.Font = MonoFontName();
                ln.SizeF = 7.5f;
                ln.ForeColor = GutterFore;
                ln.BackColor = Color.FromArgb(37, 37, 37);

                var mk = _code.Markers[ErrMarker];
                mk.Symbol = MarkerSymbol.Circle;
                mk.SetForeColor(Color.Black);
                mk.SetBackColor(ErrCol);
                _code.Margins[1].Type = MarginType.Symbol; // marker gutter next to the line numbers
                _code.Margins[1].Mask = 1u << ErrMarker;    // show only our marker there
                _code.Margins[1].Width = 16;

                var ind = _code.Indicators[ErrIndicator]; // 8+: lexer-free index
                ind.Style = IndicatorStyle.Squiggle;
                ind.ForeColor = ErrCol;
                ind.HoverStyle = IndicatorStyle.SquiggleLow;
                ind.HoverForeColor = ErrCol;
                _code.MouseDwellTime = 400; // hover delay for the error tooltip

                _code.CaretForeColor = Fore;
                _code.WrapMode = WrapMode.None;
                _code.TabWidth = 4;
                UpdateLineNumberMargin();
            }
            catch (Exception ex) { Log.Ex("InitEditorStyles", ex); }
        }

        private void UpdateLineNumberMargin()
        {
            try
            {
                _code.Margins[0].Type = MarginType.Number;
                int digits = Math.Max(2, (int) Math.Floor(Math.Log10(Math.Max(_code.Lines.Count, 1))) + 1);
                int w;
                try { w = _code.TextWidth(Style.LineNumber, new string('8', digits)) + 12; }
                catch { w = digits * 10 + 16; }
                _code.Margins[0].Width = w;
            }
            catch { /* margin width is a nicety - never break on it */ }
        }

        private bool IsHandleAvailable()
        {
            try { return _code.IsHandleCreated; } catch { return false; }
        }

        // Logs scroll state only on change; flags upward jumps explicitly.
        private void DiagScrollTick()
        {
            if (!IsHandleAvailable()) return;
            try
            {
                int first = _code.FirstVisibleLine;
                if (first == _lastDiagFirstLine) return;
                bool jumpedUp = _lastDiagFirstLine > 0 && first < _lastDiagFirstLine;
                _lastDiagFirstLine = first;
                Log.W($"SCROLL WATCH: firstLine={first} caret={_code.CurrentPosition} dirty={_dirty}"
                      + (jumpedUp ? "  <<< VIEWPORT JUMPED UP" : ""));
            }
            catch { /* diagnostics must never break the editor */ }
        }

        // A DPI change may re-create the native handle - re-arm styles + highlight.
        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED && _highlightTimer != null)
            {
                if (IsHandleAvailable()) InitEditorStyles();
                _lastHighlightText = null;
                Restart(_highlightTimer);
                Log.W($"WM_DPICHANGED -> styles re-applied, re-highlight armed (newDpi={m.WParam.ToInt64() >> 16})");
            }
            base.WndProc(ref m);
        }

        // ---- highlighting --------------------------------------------------
        // Thread-pool regex scan -> byte[] style map -> native styling apply.
        // Never touches the selection, never scrolls. Files above 50 000
        // chars get comments only.
        private void Highlight()
        {
            string text = _code.Text;
            if (text.Length == 0)
            {
                if (_highlightTraceLogs < 40)
                {
                    _highlightTraceLogs++;
                    Log.W("Highlight skipped: _code.Text is EMPTY");
                }
                return;
            }

            if (text == _lastHighlightText) // nothing to recolor
            {
                if (_highlightTraceLogs < 40)
                {
                    _highlightTraceLogs++;
                    Log.W("Highlight skipped: text unchanged");
                }
                return;
            }

            if (_highlightInFlight) return; // a scan is running; its apply re-checks the text

            if (_highlightTraceLogs < 40) // rate-limited caller trace
            {
                _highlightTraceLogs++;
                try
                {
                    var st = new System.Diagnostics.StackTrace(1, false);
                    var names = (st.GetFrames() ?? [])
                        .Take(6)
                        .Select(f => f.GetMethod()?.Name ?? "?");
                    Log.W("Highlight entered from: " + string.Join(" <- ", names));
                }
                catch { /* diagnostics must never break the editor */ }
            }

            _highlightInFlight = true;
            int gen = ++_highlightGen;
            bool big = text.Length > 50_000;
            Task.Run(() =>
            {
                var styleMap = ScanTokens(text, big);
                try { BeginInvoke(new Action(() => ApplyHighlight(gen, text, styleMap))); }
                catch { /* form closing */ }
            });
        }

        // Pure regex computation, thread safe. One style per char (0 = default);
        // precedence: comments < preproc < keywords < functions < numbers.
        private static byte[] ScanTokens(string text, bool big)
        {
            var map = new byte[text.Length];
            var sw = Stopwatch.StartNew();
            try
            {
                // O(n) comment mask - O(1) comment lookup per match.
                var inComment = new bool[text.Length];
                foreach (Match m in CommentRx.Matches(text))
                {
                    for (int i = m.Index, end = m.Index + m.Length; i < end; i++)
                    {
                        inComment[i] = true;
                        map[i] = StyComment;
                    }
                }

                if (!big)
                {
                    bool InComment(int pos) => pos >= 0 && pos < inComment.Length && inComment[pos];

                    void Paint(Regex rx, byte sty)
                    {
                        foreach (Match m in rx.Matches(text))
                        {
                            if (InComment(m.Index)) continue;
                            for (int i = m.Index, end = m.Index + m.Length; i < end; i++) map[i] = sty;
                        }
                    }

                    Paint(PreprocRx, StyPreproc);
                    Paint(KeywordRx, StyKeyword);
                    Paint(FunctionRx, StyFunction);
                    Paint(NumberRx, StyNumber);
                }
            }
            catch (Exception ex) { Log.Ex("Highlight scan", ex); }
            Log.W($"HILITE scan: {text.Length} chars{(big ? " (comments only)" : "")} in {sw.ElapsedMilliseconds} ms");
            return map;
        }

        // UI-thread apply: contiguous styled runs via the native API.
        private void ApplyHighlight(int gen, string text, byte[] styleMap)
        {
            _highlightInFlight = false;
            if (gen != _highlightGen) return;   // a newer scan was scheduled
            if (_code.Text != text)             // user typed during the scan
            {
                Restart(_highlightTimer);       // rescan the new text
                return;
            }
            _lastHighlightText = text;
            if (!IsHandleAvailable()) return;
            var sw = Stopwatch.StartNew();
            try
            {
                _code.StartStyling(0);
                int runStart = 0;
                byte runStyle = styleMap.Length > 0 ? styleMap[0] : (byte) 0;
                for (int i = 1; i < styleMap.Length; i++)
                {
                    if (styleMap[i] == runStyle) continue;
                    _code.SetStyling(i - runStart, runStyle);
                    runStart = i;
                    runStyle = styleMap[i];
                }
                if (styleMap.Length > runStart)
                    _code.SetStyling(styleMap.Length - runStart, runStyle);
            }
            catch (Exception ex) { Log.Ex("Highlight apply", ex); }
            Log.W($"HILITE apply: {text.Length} chars in {sw.ElapsedMilliseconds} ms");
        }

        // ---- preview -------------------------------------------------------
        private void StartPreview()
        {
            if (_preview != null && !_preview.IsDisposed) return; // Shown can re-fire
            try
            {
                var rect = _previewHost.ClientRectangle;
                if (rect.Width < 40 || rect.Height < 40) rect = new Rectangle(0, 0, 480, 360);

                _preview = new RenderWindow(rect, "EditorPreview",
                    new Size(rect.Width, rect.Height),
                    new Rectangle(0, 0, rect.Width, rect.Height), new Point(0, 0),
                    isPreview: true, inputExit: false,
                    resMult: Program.Cfg.ResMult, scaleBlend: Program.Cfg.ScaleBlend);
                _preview.Show();
                _ = _preview.Handle; // force handle creation
                IntPtr setParentResult = Native.SetParent(_preview.Handle, _previewHost.Handle);
                Native.SetWindowLong(_preview.Handle, Native.GWL_STYLE,
                    new IntPtr(Native.WS_CHILD | Native.WS_VISIBLE));
                Log.W($"Editor preview embedded (SetParent=0x{setParentResult.ToInt64():X})");

                SetStatus("Compiling shader...", GutterFore); // shaderc off the UI thread
                string src = _code.Text;
                int gen = ++_compileGen;
                Task.Run(() =>
                {
                    var r = _preview.CompileFragmentSource(src);
                    r.Gen = gen;
                    try { BeginInvoke(new Action(() => FinishPreviewInit(r, src, gen))); }
                    catch { /* form closing */ }
                });
            }
            catch (Exception ex)
            {
                Log.Ex("Editor StartPreview", ex);
                SetStatus("Preview failed: " + ex.Message, ErrCol);
            }
        }

        // Black fallback so the pipeline initializes even on first-compile failure.
        private static readonly string FallbackFragment =
            ShaderAdapter.ComposeFragment("void main() { finalColor = vec4(0.0, 0.0, 0.0, 1.0); }");

        private void FinishPreviewInit(RenderWindow.FragmentCompile r, string src, int gen)
        {
            if (_preview == null || _preview.IsDisposed) return;
            bool ok = r.Spirv != null
                ? _preview.InitGraphics(ShaderAdapter.VertexShader, r.Spirv, r.Frag)
                : _preview.InitGraphics(ShaderAdapter.VertexShader, null, FallbackFragment);
            if (!ok)
            {
                SetStatus("Preview failed to initialize (see log).", ErrCol);
                return;
            }

            // vsync flip on the present thread; the UI thread only draws.
            _preview.UseEditorPresentThread = true;
            _preview.StartPresentThread();
            _renderTimer.Start();
            _firstCompile = false;
            if (r.Error != null)
            {
                ReportCompileError(r.Error, src);
                StopPreviewForError();
            }
            else
            {
                ClearErrorMarks(); // recovered from an error - no stale marks
                SetStatus("Preview running - edits recompile in the background.", OkCol);
                TextureStatusAsync(src, gen, "Preview running - edits recompile in the background.");
            }
        }

        private void RenderTick()
        {
            if (_preview == null || _preview.IsDisposed) return;
            // Retry a resize the present thread blocked (non-blocking).
            _preview.ApplyPendingEditorResize();
            if (!_preview.MainShaderReady) { _lastTick = _sw.Elapsed.TotalSeconds; return; }

            // At most one frame ahead of the present thread; the clock freezes
            // while queued so a stall pauses instead of jumping forward.
            double now = _sw.Elapsed.TotalSeconds;
            if (_preview.PresentPending >= 1) { _lastTick = now; return; }
            _animTime += now - _lastTick;
            _lastTick = now;

            var sw = Stopwatch.StartNew();
            try { _preview.Render((float) _animTime); }
            catch (Exception ex) { Log.Ex("Editor render", ex); }
            if (sw.ElapsedMilliseconds > 300)
            {
                if (_slowRenderLogs >= 20 && now - _lastSlowRenderLog > 60)
                    _slowRenderLogs = 0; // quiet minute - budget available again
                if (_slowRenderLogs < 20)
                {
                    _slowRenderLogs++;
                    _lastSlowRenderLog = now;
                    Log.W($"RenderTick: Render took {sw.ElapsedMilliseconds} ms on the UI thread "
                          + "(driver queue full or software rendering)");
                }
            }

            // A paused preview is only explained by a present stall.
            if (_preview.LastPresentStallMs != _lastStallMs)
            {
                _lastStallMs = _preview.LastPresentStallMs;
                SetStatus($"Preview stalled {_lastStallMs} ms (GPU/DWM) - animation paused.",
                    GutterFore);
            }
        }

        private void FitPreview()
        {
            if (_preview == null || _preview.IsDisposed || !Native.IsWindow(_preview.Handle)) return;
            try
            {
                var rect = _previewHost.ClientRectangle;
                if (rect.Width < 20 || rect.Height < 20) return;
                _preview.Bounds = rect;
            }
            catch { /* window mid-teardown */ }
        }

        // ---- compile / status ---------------------------------------------
        // Async: shaderc on the thread pool, device swap on the UI thread.
        // force=true (F5 / Save) re-resolves textures.
        private void Recompile(bool force = false)
        {
            if (_preview == null || _preview.IsDisposed) { ClearErrorMarks(); StartPreview(); return; }
            string src = _code.Text;
            ClearErrorMarks();
            if (!force && src == _lastCompiledSrc && _lastCompileOk)
            {
                SetStatus("Unchanged - compile skipped.", GutterFore);
                return;
            }
            int gen = ++_compileGen;
            _errorUserLine = -1;
            SetStatus(_firstCompile ? "Compiling..." : $"Compiling... ({DateTime.Now:HH:mm:ss})", GutterFore);
            Task.Run(() =>
            {
                var r = _preview.CompileFragmentSource(src);
                r.Gen = gen;
                try { BeginInvoke(new Action(() => ApplyCompile(r, src))); }
                catch { /* form closing */ }
            });
        }

        private void ApplyCompile(RenderWindow.FragmentCompile r, string src)
        {
            if (_preview == null || _preview.IsDisposed)
            {
                if (r.Error == null) StartPreview(); // stopped by an earlier error - recreate
                return;
            }
            if (r.Gen != _compileGen) return; // a newer compile is already queued
            string aerr = r.Error ?? _preview.ApplyCompiledFragment(r.Spirv, r.Frag);
            _lastCompiledSrc = src;
            _lastCompileOk = aerr == null;
            if (aerr == null) _lastCompilePath = _savePath;
            if (aerr != null)
            {
                ReportCompileError(aerr, src);
                StopPreviewForError();
            }
            else
            {
                ClearErrorMarks(); // build is clean - no stale markers/underline
                // Texture info does disk I/O + PNG decode - off the UI thread.
                string baseMsg = _firstCompile
                    ? "Compiled OK."
                    : $"Compiled OK ({DateTime.Now:HH:mm:ss}).";
                SetStatus(baseMsg, OkCol);
                TextureStatusAsync(src, r.Gen, baseMsg);
            }
            _firstCompile = false;
        }

        // Remove the error marker, squiggle underline and tooltip text.
        private void ClearErrorMarks()
        {
            try { _code.Markers[ErrMarker].DeleteAll(); } catch { }
            try { _code.IndicatorClearRange(0, _code.TextLength); } catch { }
            _errorText = null; _errRangeStart = 0; _errRangeLen = 0;
        }

        private void ReportCompileError(string err, string src)
        {
            string first = FirstLine(err);
            _errorText = FirstLines(err, 5);
            SetStatus($"COMPILE ERROR: {first}  (click here to jump to the error line)", ErrCol);
            // shaderc lines are in the composed source - subtract the header lines.
            Match m = ErrorLineRx.Match(err);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int composed))
            {
                int offset = ShaderAdapter.FragmentHeader.Count(c => c == '\n')
                           + ShaderAdapter.TextureDeclarations(
                                 ShaderAdapter.AllSamplerNames(src)).Count(c => c == '\n');
                _errorUserLine = composed - offset - 1; // shaderc is 1-based, Lines[] 0-based
            }
            try
            {
                _code.Markers[ErrMarker].DeleteAll();
                _code.IndicatorClearRange(0, _code.TextLength);
                _errRangeStart = 0; _errRangeLen = 0;
                if (_errorUserLine >= 0 && _code.Lines.Count > 0)
                {
                    int line = Math.Clamp(_errorUserLine, 0, _code.Lines.Count - 1);
                    _code.Lines[line].MarkerAdd(ErrMarker);
                    var ln = _code.Lines[line];
                    int len = ln.EndPosition - ln.Position;
                    if (len > 0)
                    {
                        _code.IndicatorCurrent = ErrIndicator;
                        _code.IndicatorFillRange(ln.Position, len); // underline the whole line
                        _errRangeStart = ln.Position; _errRangeLen = len;
                    }
                    Log.W($"Error marker: composed={_errorUserLine} -> line {line} of {_code.Lines.Count}, underlined {len} chars");
                }
                else
                    Log.W($"Error marker: none (errorUserLine={_errorUserLine}, lines={_code.Lines.Count})");
            }
            catch (Exception mex) { Log.Ex("Error marker", mex); }
        }

        // Compile failed: stop the shader and empty the viewer (the host
        // panel's black background shows); the next edit / F5 re-creates it.
        private void StopPreviewForError()
        {
            _renderTimer.Stop();
            try { _preview?.ShutdownPresentThread(500); } catch { }
            try { _preview?.Close(); } catch { }
            _preview = null;
        }

        private void SetStatus(string text, Color color)
        {
            _statusLeft.Text = text;
            _statusLeft.ForeColor = color;
            Log.W("Editor status: " + text);
        }

        private void JumpToError()
        {
            if (_errorUserLine < 0 || _code.Lines.Count == 0) return;
            int line = Math.Clamp(_errorUserLine, 0, _code.Lines.Count - 1);
            _code.Focus();
            _code.SelectionStart = _code.Lines[line].Position;
            _code.SelectionEnd = _code.Lines[line].Position;
            _code.ScrollCaret();
        }

        // PNG scan/decode on the thread pool; result applied if not superseded.
        private void TextureStatusAsync(string src, int gen, string baseMsg)
        {
            string shaderPath = Program.ActiveShaderPath;
            Task.Run(() =>
            {
                string ts = ComputeTextureStatus(src, shaderPath);
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (gen == _compileGen)
                            SetStatus($"{baseMsg} {ts}", OkCol);
                    }));
                }
                catch { /* form closing */ }
            });
        }

        private static string ComputeTextureStatus(string src, string shaderPath)
        {
            var names = ShaderAdapter.AllSamplerNames(src);
            if (names.Count == 0) return "textures: none declared";
            var parts = new List<string>();
            foreach (string n in names)
            {
                string png = ShaderTextures.FindPng(n, shaderPath);
                if (png == null)
                    parts.Add($"{n}: MISSING ({n}.png next to the shader file)");
                else
                    try
                    {
                        using var b = new Bitmap(png);
                        parts.Add($"{n}: {Path.GetFileName(png)} {b.Width}x{b.Height} OK");
                    }
                    catch { parts.Add($"{n}: {Path.GetFileName(png)} (unreadable)"); }
            }
            return "textures: " + string.Join("; ", parts);
        }

        private static string FirstLines(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "(empty error)";
            var lines = s.Replace("\r", "").Split('\n').Take(max).ToArray();
            string j = string.Join("\n", lines);
            return j.Length > 400 ? j[..400] + "\n..." : j;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(empty error)";
            int nl = s.IndexOfAny(new[] { '\r', '\n' });
            string line = nl > 0 ? s[..nl] : s;
            return line.Length > 260 ? line[..260] + "..." : line;
        }

        // ---- file operations ----------------------------------------------
        private string DefaultDir =>
            !string.IsNullOrWhiteSpace(Program.Cfg.ShaderFolder) && Directory.Exists(Program.Cfg.ShaderFolder)
                ? Program.Cfg.ShaderFolder
                : Program.ActiveShaderPath != null
                    ? Path.GetDirectoryName(Program.ActiveShaderPath)
                    : Settings.AppDataDir;

        private void LoadPath(string path)
        {
            try
            {
                _loading = true;
                _code.Text = File.ReadAllText(path);
                _loading = false;
                _savePath = path;
                _lastSavedText = null;
                _diskText = _code.Text;
                Program.ActiveShaderPath = path;
                _dirty = false;
                UpdateTitle();
                AimWatcher(path);
                Highlight();
                _firstCompile = true;
                Recompile();
                Log.W($"Editor: opened '{path}'");
            }
            catch (Exception ex)
            {
                _loading = false;
                Log.Ex("Editor open", ex);
                SetStatus("Open failed: " + ex.Message, ErrCol);
            }
        }

        private void OpenFile()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Open shader",
                Filter = "Shader files|*.fs;*.glsl;*.frag|All files|*.*",
                InitialDirectory = DefaultDir,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) LoadPath(dlg.FileName);
        }

        // ---- external file changes ----------------------------------------
        // Reload when another program writes the file, viewport preserved;
        // own saves never reload; unsaved edits are kept.
        private void AimWatcher(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (_watcher != null && _watchPath == full) return;
                _watcher?.Dispose();
                _watchPath = full;
                _watcher = MakeWatcher(full);
                _watcher.EnableRaisingEvents = true;
                Log.W($"Editor: watching '{_watchPath}' for external changes");
            }
            catch (Exception ex) { Log.Ex("Editor watcher start", ex); }
        }

        private FileSystemWatcher MakeWatcher(string full)
        {
            var w = new FileSystemWatcher(Path.GetDirectoryName(full), Path.GetFileName(full))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                // 16 KB: the 4 KB default overflows on save bursts and FSW
                // then silently stops raising events.
                InternalBufferSize = 16_384,
            };
            w.Changed += (_, e) => QueueWatch(e.FullPath);
            w.Created += (_, e) => QueueWatch(e.FullPath);
            w.Renamed += (_, e) => QueueWatch(e.FullPath);
            w.Error += (_, e) =>
            {
                Log.Ex("Editor watcher", e.GetException());
                // FSW stops after an error - re-arm on the UI thread.
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            _watcher?.Dispose();
                            _watcher = null;
                            if (_watchPath == null) return;
                            _watcher = MakeWatcher(_watchPath);
                            _watcher.EnableRaisingEvents = true;
                            Log.W($"Editor: watcher re-armed on '{_watchPath}'");
                        }
                        catch (Exception ex) { Log.Ex("Editor watcher re-arm", ex); }
                    }));
                }
                catch { /* form closing */ }
            };
            return w;
        }

        // Events arrive in bursts - debounce via _watchTimer on the UI thread.
        private void QueueWatch(string fullPath)
        {
            _pendingWatchPath = fullPath;
            try { BeginInvoke(new Action(() => Restart(_watchTimer))); }
            catch { /* form closing */ }
        }

        private void CheckExternalChange()
        {
            _watchTimer.Stop();
            string path = _pendingWatchPath;
            _pendingWatchPath = null;
            if (path == null || _savePath == null) return;
            try
            {
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(_savePath),
                        StringComparison.OrdinalIgnoreCase)) return;
                string disk;
                try { disk = File.ReadAllText(_savePath); }
                catch (IOException)
                {
                    // Writer still holds the file - retry briefly, then give up.
                    if (++_watchRetries <= 20)
                    {
                        _pendingWatchPath = path;
                        Restart(_watchTimer);
                    }
                    else
                    {
                        Log.W("Editor: external change dropped - file stayed locked for 5s");
                        _watchRetries = 0;
                    }
                    return;
                }
                _watchRetries = 0;
                if (disk == _code.Text)
                {
                    // Up to date - sync bookkeeping, clear a stale dirty flag,
                    // never reload or move the view.
                    _diskText = disk;
                    if (_dirty) { _dirty = false; UpdateTitle(); }
                    return;
                }
                if (disk == _lastSavedText) { _diskText = disk; return; } // own save
                if (_dirty)
                {
                    _diskText = disk; // remember what disk holds for the dirty check
                    SetStatus("File changed on disk - your unsaved edits are kept; Reload discards them.", GutterFore);
                    Log.W("Editor: external change detected (editor dirty - not reloaded)");
                    return;
                }
                SetTextPreservingView(disk);
                _diskText = disk;
                _dirty = false;
                _lastHighlightText = null; // force a recolor even if the text coincides
                Highlight();
                Recompile();
                SetStatus($"Reloaded '{Path.GetFileName(_savePath)}' (changed on disk; position kept).", OkCol);
                Log.W("Editor: external change reloaded (scroll/caret preserved)");
            }
            catch (Exception ex) { Log.Ex("Editor watcher", ex); }
        }

        // mtime/size poll - safety net for missed FSW events.
        private void PollDiskChange()
        {
            if (_savePath == null || _pendingWatchPath != null) return;
            try
            {
                var fi = new FileInfo(_savePath);
                if (!fi.Exists) return;
                DateTime wt = fi.LastWriteTimeUtc;
                long len = fi.Length;
                if (wt == _lastPollWrite && len == _lastPollLen) return;
                _lastPollWrite = wt;
                _lastPollLen = len;
                _pendingWatchPath = _savePath;
                Restart(_watchTimer);
            }
            catch { /* file mid-write - the next poll re-checks */ }
        }

        // Replace the text, restore first-visible-line + selection (no
        // ScrollCaret). _loading keeps TextChange inert.
        private void SetTextPreservingView(string newText)
        {
            _loading = true;
            try
            {
                int first = 0, selStart = 0, selEnd = 0;
                try
                {
                    first = _code.FirstVisibleLine;
                    selStart = _code.SelectionStart;
                    selEnd = _code.SelectionEnd;
                }
                catch { }
                _code.Text = newText;
                try
                {
                    _code.FirstVisibleLine = Math.Min(first, Math.Max(_code.Lines.Count - 1, 0));
                    selStart = Math.Clamp(selStart, 0, Math.Max(newText.Length - 1, 0));
                    selEnd = Math.Clamp(selEnd, selStart, Math.Max(newText.Length - 1, 0));
                    _code.SelectionStart = selStart;
                    _code.SelectionEnd = selEnd;
                }
                catch { }
            }
            finally { _loading = false; }
            UpdateTitle();
            UpdateCaretStatus();
        }

        private void Reload()
        {
            if (_savePath == null || !File.Exists(_savePath))
            {
                SetStatus("Nothing to reload - the file has not been saved yet.", GutterFore);
                return;
            }
            try
            {
                string disk = File.ReadAllText(_savePath);
                if (disk == _code.Text)
                {
                    _diskText = disk; // up to date - just sync
                    if (_dirty) { _dirty = false; UpdateTitle(); }
                    SetStatus("File on disk matches the editor - nothing to reload.", GutterFore);
                    return;
                }
                SetTextPreservingView(disk);
                _diskText = disk;
                _lastHighlightText = null;
                Highlight();
                Recompile();
                SetStatus($"Reloaded '{Path.GetFileName(_savePath)}' (position kept).", OkCol);
                Log.W("Editor: manual reload (scroll/caret preserved)");
            }
            catch (Exception ex)
            {
                Log.Ex("Editor reload", ex);
                SetStatus("Reload failed: " + ex.Message, ErrCol);
            }
        }

        private void Save()
        {
            try
            {
                if (_savePath == null) { SaveAs(); return; }
                File.WriteAllText(_savePath, _code.Text);
                _lastSavedText = _code.Text;
                _diskText = _code.Text; // disk now matches the editor
                Program.ActiveShaderPath = _savePath; // textures resolve relative to it
                _dirty = false;
                UpdateTitle();
                AimWatcher(_savePath); // SaveAs may have moved the file
                if (_savePath == _lastCompilePath && _code.Text == _lastCompiledSrc && _lastCompileOk)
                {
                    SetStatus($"Saved '{_savePath}'. Shader unchanged - recompile skipped.", OkCol);
                    Log.W($"Editor: saved '{_savePath}' (shader unchanged, recompile skipped)");
                    return;
                }
                SetStatus($"Saved '{_savePath}'.", OkCol);
                Log.W($"Editor: saved '{_savePath}'");
                Recompile(true); // path or source changed: textures re-resolve
            }
            catch (Exception ex)
            {
                Log.Ex("Editor save", ex);
                SetStatus("Save failed: " + ex.Message, ErrCol);
            }
        }

        private void SaveAs()
        {
            using var dlg = new SaveFileDialog
            {
                Title = "Save shader as",
                Filter = "Shader files|*.fs;*.glsl;*.frag|All files|*.*",
                InitialDirectory = DefaultDir,
                FileName = _savePath != null ? Path.GetFileName(_savePath) : "shader.fs",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _savePath = dlg.FileName;
            Save();
        }

        private void InsertSnippet()
        {
            const string snippet =
                "\n// --- texture example (Shadertoy style) -------------------------\n" +
                "// Declare the channel (or just reference iChannel0..3 like on Shadertoy -\n" +
                "// the saver auto-declares them) and put iChannel0.png next to this file.\n" +
                "// uniform sampler2D iChannel0;\n" +
                "// void main() {\n" +
                "//     vec2 uv = fragTexCoord;              // 0..1 across the screen\n" +
                "//     vec4 tex = texture(iChannel0, uv);   // sample the PNG\n" +
                "//     finalColor = tex;                    // show it\n" +
                "// }\n";
            int pos = _code.SelectionStart;
            // SelectedText is read-only - insert via the target API.
            _code.TargetStart = pos;
            _code.TargetEnd = _code.SelectionEnd;
            _code.ReplaceTarget(snippet);
            _code.SelectionStart = pos;
            _code.SelectionEnd = pos + snippet.Length;
        }

        private void ShutdownPreview()
        {
            _renderTimer?.Stop();
            _compileTimer?.Stop();
            _highlightTimer?.Stop();
            _fitTimer?.Stop();
            _diagTimer?.Stop();
            try { _preview?.ShutdownPresentThread(500); } catch { }
            try { _preview?.Close(); } catch { }
            _preview = null;
        }
    }
}
