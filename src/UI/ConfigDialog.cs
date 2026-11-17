namespace WinGLSLScrSaver;


internal class ConfigDialog : Form
{
    private readonly TextBox _folderBox;
    private readonly NumericUpDown _fpsCap;
    private readonly TrackBar _resMult;
    private readonly Label _resMultLabel;
    private readonly TrackBar _scaleBlend;
    private readonly Label _scaleBlendLabel;
    private readonly CheckBox _showFps;
    private readonly CheckBox _tearFix;
    private readonly ComboBox _fpsPace;
    private readonly CheckBox _presentThreads;
    private readonly ComboBox _presentLanes;
    private readonly ComboBox _swapBlt;
    private readonly NumericUpDown _swapBuffers;
    private readonly NumericUpDown _maxLatency;
    private readonly ComboBox _presentFlags;
    private readonly ToolTip _tips = new();

    public ConfigDialog()
    {
        AppSettings s = Program.Cfg;

        Text = "WinGLSLScrSaver Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; // keep fixed-pixel layout intact at 125%/150% scaling
        ClientSize = new Size(484, 610);

        // ---- row 1: shader folder ----
        var folderLabel = new Label { Text = "Shader folder (a random shader is picked each start):",
            Location = new Point(40, 17), AutoSize = true };
        var folderHelp = NewHelpButton(12, 12,
            "Where the screensaver loads shaders from.",
            "Shader folder",
            "Folder that is scanned for fragment shaders (.fs); a random one is picked each time the saver starts.\n\n" +
            "Empty = %LocalAppData%\\WinGLSLScrSaver (the default shader location).\n\n" +
            "Use Browse... to pick the folder instead of typing the path.");
        _folderBox = new TextBox
        {
            Location = new Point(40, 40), Width = 320,
            Text = string.IsNullOrWhiteSpace(s.ShaderFolder) ? Settings.AppDataDir : s.ShaderFolder,
        };
        var browse = new Button { Text = "Browse...", Location = new Point(368, 38), Width = 92 };
        browse.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Pick the folder to draw random shaders from",
                SelectedPath = _folderBox.Text,
                ShowNewFolderButton = true,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _folderBox.Text = dlg.SelectedPath;
        };

        // ---- row 2: FPS cap ----
        var fpsLabel = new Label { Text = "FPS cap (0 = auto: run at the fastest display's refresh):",
            Location = new Point(40, 77), AutoSize = true };
        var fpsHelp = NewHelpButton(12, 72,
            "Limits how many frames per second the render loop produces.",
            "FPS cap",
            "Render loop FPS cap (default 120).\n\n" +
            "0 = auto: no cap, present with vsync, effectively running at the fastest monitor's refresh rate.\n\n" +
            "When set (> 0) the loop paces itself to that rate; it is clamped to 1..max(highest monitor refresh, 120).\n\n" +
            "Lower values save GPU/CPU and battery; higher values only make sense for multi-monitor tuning (see FPS pacing and Present lanes).");
        _fpsCap = new NumericUpDown
        {
            Location = new Point(40, 100), Width = 100,
            Minimum = 0, Maximum = 1000, Increment = 5,
            Value = Math.Clamp(s.FpsCap, 0, 1000),
        };
        var fpsHint = new Label
        {
            Text = "Clamped to 1..max(highest monitor refresh, 120) when set.",
            Location = new Point(150, 103), AutoSize = true, ForeColor = SystemColors.GrayText,
        };

        // ---- row 3: resolution multiplier (trackbar = % of native) ----
        var resLabel = new Label { Text = "Resolution (% of native; below 100% renders smaller and upscales):",
            Location = new Point(40, 139), AutoSize = true };
        var resHelp = NewHelpButton(12, 134,
            "Internal render resolution as a percentage of native monitor resolution.",
            "Resolution (% of native)",
            "Internal render resolution as a percentage of the native monitor resolution (5%..100%).\n\n" +
            "Below 100% the shader renders into a smaller shared canvas that is upscaled to each window; the pipeline is:\n" +
            "shader -> one common canvas at (max width x max height) of all monitor groups x this % -> per-window slice + upscale.\n\n" +
            "Lower = much faster (fewer pixels to shade), image gets softer/blurrier. 100% = native, sharpest.");
        _resMult = new TrackBar
        {
            Location = new Point(40, 160), Width = 320,
            Minimum = 5, Maximum = 100, TickFrequency = 5,
            Value = (int) Math.Round(s.ResMult * 100f),
        };
        _resMultLabel = new Label { Location = new Point(368, 166), AutoSize = true };
        _resMult.ValueChanged += (_, _) => _resMultLabel.Text = $"{_resMult.Value}%";
        _resMultLabel.Text = $"{_resMult.Value}%";

        // ---- row 4: upscale interpolation blend ----
        var blendLabel = new Label { Text = "Upscale filtering (left = nearest/sharp, right = bilinear/smooth):",
            Location = new Point(40, 203), AutoSize = true };
        var blendHelp = NewHelpButton(12, 198,
            "Filtering used when upscaling the render canvas to the window.",
            "Upscale filtering",
            "Filtering used when the canvas (Resolution below 100%) is upscaled to each window.\n\n" +
            "0 = nearest neighbour: blocky/sharp pixels, fastest.\n" +
            "1 (100%) = bilinear: smooth interpolation, softer.\n" +
            "In between = per-pixel weighted mix of both.\n\n" +
            "Only has an effect when Resolution is below 100%.");
        _scaleBlend = new TrackBar
        {
            Location = new Point(40, 224), Width = 320,
            Minimum = 0, Maximum = 100, TickFrequency = 10,
            Value = (int) Math.Round(s.ScaleBlend * 100f),
        };
        _scaleBlendLabel = new Label { Location = new Point(368, 230), AutoSize = true };
        _scaleBlendLabel.Text = _scaleBlend.Value switch
        {
            0 => "nearest",
            100 => "bilinear",
            _ => $"{_scaleBlend.Value}% blend",
        };
        _scaleBlend.ValueChanged += (_, _) => _scaleBlendLabel.Text = _scaleBlend.Value switch
        {
            0 => "nearest",
            100 => "bilinear",
            _ => $"{_scaleBlend.Value}% blend",
        };

        // ---- row 5: per-window fps overlay ----
        var fpsOverlayHelp = NewHelpButton(12, 260,
            "Show an FPS counter on each monitor window.",
            "Show FPS counters",
            "Shows a small FPS counter on each monitor window.\n\n" +
            "It counts frames actually handed to the display (after present gating), not render ticks - so on a multi-GPU setup each monitor can show a different number if its present lane misses ticks.\n\n" +
            "0/unchecked = off, 1/checked = on.");
        _showFps = new CheckBox
        {
            Text = "Show FPS counters (per monitor, counts frames actually displayed)",
            Location = new Point(40, 262), AutoSize = true,
            Checked = s.ShowFps,
        };

        // ---- row 6: transparent child-window tearing fix ----
        var tearFixHelp = NewHelpButton(12, 286,
            "Create an invisible child window that fixes DWM tearing when the FPS counters are off.",
            "Transparent child window tearing fix",
            "The FPS counter label is a child window over the flip-model swapchain, and a child window over the swapchain forces DWM-composed, vblank-synced presentation - which is why tearing disappears while the counter is shown.\n\n" +
            "With this option, when Show FPS counters is OFF, an invisible (layered, fully transparent) child window is created over each swapchain instead - the same tearing fix, but nothing is visible.\n\n" +
            "Has no effect while the FPS counters are on (the counter already does the job). config key: tear_fix_child=1.");
        _tearFix = new CheckBox
        {
            Text = "Transparent child window tearing fix (only when FPS counters are off)",
            Location = new Point(40, 288), AutoSize = true,
            Checked = s.TearFixChild,
        };

        // ---- advanced group: raw pipeline tuning ----
        var advanced = new GroupBox
        {
            Text = "Advanced render pipeline (leave defaults unless tuning)",
            Location = new Point(12, 318), Size = new Size(460, 240),
        };

        int rowY = 22;
        var paceHelp = HelpButtonIn(advanced, 14, rowY,
            "How the FPS cap waits between frames.",
            "FPS pacing (fps_pace)",
            "How the loop waits when an FPS cap is active (fps_cap > 0, or fullscreen canvas pacing).\n\n" +
            "rel (default) - relative pacer: next frame due = previous due + 1/cap, overrun-safe. No grid aliasing, so the cap is actually reached.\n\n" +
            "grid - old behavior: absolute 120Hz-style time grid, ceil-quantized. Beats with DWM composition sync when per-frame work sits just under one cap slot (measured ~80 fps instead of 120).\n\n" +
            "pi - rel plus a small proportional-integral trim of the wait from the measured frame-interval error.\n\n" +
            "config values: rel | grid | pi");
        var paceLabel = new Label { Text = "FPS pacing:", Location = new Point(42, rowY + 5), AutoSize = true };
        _fpsPace = new ComboBox
        {
            Location = new Point(232, rowY), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _fpsPace.Items.AddRange(new object[] { "rel - relative (default)", "grid - absolute grid (old)", "pi - relative + PI trim" });
        _fpsPace.SelectedIndex = s.FpsPace switch { "grid" => 1, "pi" => 2, _ => 0 };

        rowY += 30;
        var threadsHelp = HelpButtonIn(advanced, 14, rowY,
            "Present non-owner windows from their own threads.",
            "Per-window present threads (present_threads)",
            "1 = each non-owner window presents from its own thread, so the ~5.9 ms DWM composition-period sync inside Present(0) runs in parallel instead of eating the render loop's frame budget. The owner window stays on the main thread (the device context is not thread-safe).\n\n" +
            "0 (default) = all presents happen on the main render thread.\n\n" +
            "config values: 0 | 1");
        _presentThreads = new CheckBox
        {
            Text = "Per-window present threads", Location = new Point(42, rowY + 3), AutoSize = true,
            Checked = s.PresentThreads,
        };

        rowY += 30;
        var lanesHelp = HelpButtonIn(advanced, 14, rowY,
            "How many D3D11 present lanes (devices) are used.",
            "Present lanes (present_lanes)",
            "1 = all swapchains live on the owner device: one present lane. Present calls serialize at DWM composition granularity (~5.9 ms each), capping multi-window setups around 83 fps here.\n\n" +
            "2 = each non-owner window gets its own D3D11 device as a second, fully parallel present lane; the shared render canvas crosses devices via a keyed-mutex shared texture. This is the multi-monitor FPS fix - no tearing (DWM still composes) and no fps reduction.\n\n" +
            "config values: 1 | 2 (default 2)");
        var lanesLabel = new Label { Text = "Present lanes:", Location = new Point(42, rowY + 5), AutoSize = true };
        _presentLanes = new ComboBox
        {
            Location = new Point(232, rowY), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _presentLanes.Items.AddRange(new object[] { "1 - one device (slower multi-mon)", "2 - device per monitor (default, fast)" });
        _presentLanes.SelectedIndex = s.PresentLanes - 1;

        rowY += 30;
        var bltHelp = HelpButtonIn(advanced, 14, rowY,
            "Swapchain presentation model for raw windows.",
            "Swapchain model (raw_swap_blt)",
            "0 = flip model (default). On this driver each Present(0) syncs one DWM composition period (~5.9 ms).\n\n" +
            "1 = bitblt model, 2 buffers: Present copies into DWM's surface and returns without the compose-period sync.\n\n" +
            "2 = bitblt model, 1 buffer (the old measured variant, ~6 ms stall).\n\n" +
            "Blt mode disables the waitable object and tearing flags (not supported by the blt model) and presents with flags=0; windowed blt cannot tear (DWM composes from its own copy), so no vsync is faked or needed.\n\n" +
            "config values: 0 | 1 | 2");
        var bltLabel = new Label { Text = "Swapchain model:", Location = new Point(42, rowY + 5), AutoSize = true };
        _swapBlt = new ComboBox
        {
            Location = new Point(232, rowY), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _swapBlt.Items.AddRange(new object[] { "0 - flip (default)", "1 - bitblt, 2 buffers", "2 - bitblt, 1 buffer" });
        _swapBlt.SelectedIndex = s.RawSwapBlt;

        rowY += 30;
        var buffersHelp = HelpButtonIn(advanced, 14, rowY,
            "Number of swapchain back buffers (2..8).",
            "Swapchain buffers (swap_buffers)",
            "Raw swapchain buffer count, 2..8.\n\n" +
            "A deep queue = presents return immediately; scanout drains at each monitor's own refresh. Think of it as fake vsync at the cost of extra buffers (VRAM) and latency.\n\n" +
            "Default 4.");
        var buffersLabel = new Label { Text = "Swapchain buffers:", Location = new Point(42, rowY + 5), AutoSize = true };
        _swapBuffers = new NumericUpDown
        {
            Location = new Point(232, rowY), Width = 60,
            Minimum = 2, Maximum = 8, Value = s.SwapBuffers,
        };
        _swapBuffers.ValueChanged += (_, _) =>
            _maxLatency.Maximum = Math.Max(1, _swapBuffers.Value - 1);

        rowY += 30;
        var latencyHelp = HelpButtonIn(advanced, 14, rowY,
            "DXGI MaximumFrameLatency (1..buffers-1).",
            "Max frame latency (max_latency)",
            "DXGI MaximumFrameLatency, 1..(swap_buffers - 1).\n\n" +
            "Lower = fewer frames queued ahead, less input-to-display latency; higher = more slack for the present queue.\n\n" +
            "Default 3.");
        var latencyLabel = new Label { Text = "Max frame latency:", Location = new Point(42, rowY + 5), AutoSize = true };
        _maxLatency = new NumericUpDown
        {
            Location = new Point(232, rowY), Width = 60,
            Minimum = 1, Maximum = Math.Max(1, s.SwapBuffers - 1),
            Value = Math.Clamp(s.MaxLatency, 1, Math.Max(1, s.SwapBuffers - 1)),
        };

        rowY += 30;
        var flagsHelp = HelpButtonIn(advanced, 14, rowY,
            "Present flags used by the flip-model raw swapchain (diagnostic).",
            "Present flags (raw_present_flags)",
            "Present flags used by the flip-model raw swapchain. Diagnostics from the FPS investigation:\n\n" +
            "0 = no flags\n" +
            "1 = ALLOW_TEARING\n" +
            "2 = DO_NOT_WAIT | ALLOW_TEARING (default)\n" +
            "3 = DO_NOT_WAIT only\n\n" +
            "Leave at 2 unless experimenting; tearing flags are ignored in blt mode.");
        var flagsLabel = new Label { Text = "Present flags (flip):", Location = new Point(42, rowY + 5), AutoSize = true };
        _presentFlags = new ComboBox
        {
            Location = new Point(232, rowY), Width = 210, DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _presentFlags.Items.AddRange(new object[]
        {
            "0 - none", "1 - ALLOW_TEARING", "2 - DO_NOT_WAIT | TEARING (default)", "3 - DO_NOT_WAIT",
        });
        _presentFlags.SelectedIndex = Math.Clamp(s.RawPresentFlags, 0, 3);

        advanced.Controls.AddRange(new Control[]
        {
            paceHelp, paceLabel, _fpsPace,
            threadsHelp, _presentThreads,
            lanesHelp, lanesLabel, _presentLanes,
            bltHelp, bltLabel, _swapBlt,
            buffersHelp, buffersLabel, _swapBuffers,
            latencyHelp, latencyLabel, _maxLatency,
            flagsHelp, flagsLabel, _presentFlags,
        });

        // ---- buttons ----
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(290, 568), Width = 85 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(382, 568), Width = 85 };

        Controls.AddRange(new Control[]
        {
            folderHelp, folderLabel, _folderBox, browse,
            fpsHelp, fpsLabel, _fpsCap, fpsHint,
            resHelp, resLabel, _resMult, _resMultLabel,
            blendHelp, blendLabel, _scaleBlend, _scaleBlendLabel,
            fpsOverlayHelp, _showFps,
            tearFixHelp, _tearFix,
            advanced,
            ok, cancel,
        });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    // A small square "?" button next to a label; click = help popup,
    // hover = one-line tooltip.
    private Button NewHelpButton(int x, int y, string tip, string title, string body)
    {
        var b = new Button { Text = "?", Location = new Point(x, y), Size = new Size(22, 22) };
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) => MessageBox.Show(this, body, title + " - help",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return b;
    }

    private Button HelpButtonIn(Control parent, int x, int y, string tip, string title, string body)
    {
        var b = new Button { Text = "?", Location = new Point(x, y), Size = new Size(22, 22) };
        _tips.SetToolTip(b, tip);
        b.Click += (_, _) => MessageBox.Show(parent, body, title + " - help",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        return b;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
        {
            var s = Program.Cfg;
            s.ShaderFolder = _folderBox.Text;
            s.FpsCap = (int) _fpsCap.Value;
            s.ResMult = Math.Clamp(_resMult.Value / 100f, AppSettings.MinResMult, AppSettings.MaxResMult);
            s.ScaleBlend = _scaleBlend.Value / 100f;
            s.ShowFps = _showFps.Checked;
            s.TearFixChild = _tearFix.Checked;
            s.FpsPace = _fpsPace.SelectedIndex switch { 1 => "grid", 2 => "pi", _ => "rel" };
            s.PresentThreads = _presentThreads.Checked;
            s.PresentLanes = _presentLanes.SelectedIndex + 1;
            s.RawSwapBlt = _swapBlt.SelectedIndex;
            s.SwapBuffers = (int) _swapBuffers.Value;
            s.MaxLatency = (int) _maxLatency.Value;
            s.RawPresentFlags = _presentFlags.SelectedIndex;
            s.Save();
        }
        base.OnFormClosed(e);
    }
}
