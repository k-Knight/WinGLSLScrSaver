// WinGLSLScrSaver - GLSL shader screensaver for Windows (.scr).
// Renders a GLSL fragment shader on the GPU (D3D11 via Pie) with /s /p /c support.
namespace WinGLSLScrSaver;

using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

internal partial class Program
{
    private static bool _running = true;

    // Loaded once at startup; the /c dialog edits and saves this object.
    internal static AppSettings Cfg;

    internal static int DiagMode = 0;

    // Shared-canvas pipeline: normal runs plus diag-7 (verbose logging only).
    internal static bool CanvasPipeline => DiagMode is 0 or 7;

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Ex("UnhandledException", (Exception) e.ExceptionObject);

        Log.W("=================================================");
        Log.W($"WinGLSLScrSaver starting. args=[{string.Join(" ", args)}]");
        if (Log.Verbose) Log.W($"Log file: {Log.Path}");
        Log.W($"exe: {Environment.ProcessPath ?? AppContext.BaseDirectory}");

        try
        {
            Native.SetProcessDPIAware();
            Log.W("SetProcessDPIAware OK");
        }
        catch (Exception ex) { Log.Ex("SetProcessDPIAware", ex); }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Cfg = AppSettings.Load();

        // Parse arguments (case-insensitive; "/p12345" and "/p 12345" both ok).
        string mode = "";
        IntPtr previewHwnd = IntPtr.Zero;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].Trim();
            if (a.Equals("/s", StringComparison.OrdinalIgnoreCase) || a.Equals("-s", StringComparison.OrdinalIgnoreCase))
                mode = "s";
            else if (a.StartsWith("/p", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-p", StringComparison.OrdinalIgnoreCase))
            {
                mode = "p";
                string rest = a.Length > 2 ? a.Substring(2) : (i + 1 < args.Length ? args[++i] : "");
                Log.W($"preview hwnd raw string: '{rest}'");
                long.TryParse(rest.Trim().TrimStart('"', '\'').TrimEnd('"', '\''), out long hwndVal);
                previewHwnd = new IntPtr(hwndVal);
            }
            else if (a.StartsWith("/c", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-c", StringComparison.OrdinalIgnoreCase)
                     || a.StartsWith("/l", StringComparison.OrdinalIgnoreCase))
                mode = "c";
        }
        if (mode.Length == 0) mode = "c";
        // A preview hwnd in the args means preview mode, period.
        if (previewHwnd != IntPtr.Zero) mode = "p";
        Log.W($"mode={mode} previewHwnd=0x{previewHwnd.ToInt64():X}");

        foreach (string a in args)
        {
            Match dm = Regex.Match(a, @"(?i)^[/\-]{1,2}diag[:=](\d)$");
            if (dm.Success)
            {
                DiagMode = dm.Groups[1].Value[0] - '0';
                Log.Verbose = true; // diag re-enables logging even in Release
                Log.W($"DIAG MODE {DiagMode} enabled (auto-exit after 6s)");
            }
        }

        try
        {
            switch (mode)
            {
                case "c":
                    ShowConfig();
                    return;
                case "p":
                    if (previewHwnd == IntPtr.Zero || !Native.IsWindow(previewHwnd))
                    {
                        Log.W("preview requested but hwnd invalid -> config dialog");
                        ShowConfig();
                        return;
                    }
                    RunPreview(previewHwnd);
                    return;
                default:
                    RunFullscreen();
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Ex("Main", ex);
            MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\nDetails in log:\n{Log.Path}",
                "WinGLSLScrSaver error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void ShowConfig()
    {
        Log.W("Showing config dialog");
        // ShowDialog, not Application.Run - modeless buttons never closed the dialog.
        new ConfigDialog().ShowDialog();
        Log.W("Config dialog closed");
    }

    // Random .fs/.glsl/.frag from the configured folder (fallback: app data
    // dir, then the embedded default).
    private static string LoadShaderText()
    {
        string folder = Cfg.ShaderFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            folder = Settings.AppDataDir;

        string[] candidates = ListShaders(folder);
        if (candidates.Length > 0)
        {
            string picked = candidates[Random.Shared.Next(candidates.Length)];
            Log.W($"Shader source: '{picked}' (random pick from {candidates.Length} in {folder})");
            return File.ReadAllText(picked);
        }

        Log.W($"Shader source: EMBEDDED default swirl (no shader found in {folder})");
        return ShaderAdapter.DefaultShader;
    }

    private static string[] ListShaders(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            return Directory.GetFiles(folder, "*.fs")
                .Concat(Directory.GetFiles(folder, "*.glsl"))
                .Concat(Directory.GetFiles(folder, "*.frag"))
                .ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static List<RenderWindow> CreateWindows(string shaderText, bool preview, Rectangle previewBounds,
        out string vertexShader, out string fragmentShader)
    {
        vertexShader = ShaderAdapter.VertexShader;
        fragmentShader = Program.DiagMode == 2
            ? @"#version 450
layout (location = 0) out vec4 finalColor;
void main()
{
    finalColor = vec4(1.0, 0.0, 0.0, 1.0);
}"
            : Program.DiagMode is 5 or 6
            ? @"#version 450
layout (location = 0) in vec2 fragTexCoord;
layout (location = 0) out vec4 finalColor;
void main()
{
    finalColor = vec4(fragTexCoord.x, fragTexCoord.y, 0.2, 1.0);
}"
            : ShaderAdapter.FragmentHeader + ShaderAdapter.Adapt(shaderText);
        Log.W($"Fragment shader prepared: {fragmentShader.Length} chars total (diagMode={Program.DiagMode})");

        var windows = new List<RenderWindow>();

        if (preview)
        {
            Log.W($"Preview window bounds: {previewBounds}");
            var win = new RenderWindow(previewBounds, "WinGLSLScrSaver Preview",
                new Size(previewBounds.Width, previewBounds.Height),
                new Rectangle(0, 0, previewBounds.Width, previewBounds.Height),
                new Point(0, 0),
                isPreview: true, inputExit: false,
                resMult: Cfg.ResMult, scaleBlend: Cfg.ScaleBlend);
            windows.Add(win);
            return windows;
        }

        var screens = Screen.AllScreens;
        Log.W($"Screens found: {screens.Length}");
        var rects = new List<Rectangle>();
        foreach (var s in screens)
        {
            Log.W($"  screen '{s.DeviceName}' bounds={s.Bounds} primary={s.Primary}");
            rects.Add(s.Bounds);
        }

        var groups = ScreenGroup.ComputeGroups(rects.ToArray());
        Log.W($"Groups computed: {groups.Count}");
        foreach (var g in groups)
            Log.W($"  group bbox={g.Bbox} screens=[{string.Join("; ", g.Screens)}]");

        int masterW = groups.Max(g => g.Bbox.Width);
        int masterH = groups.Max(g => g.Bbox.Height);
        Log.W($"Master resolution: {masterW}x{masterH}");

        foreach (var group in groups)
        {
            foreach (var rect in group.Screens)
            {
                var win = new RenderWindow(rect, "WinGLSLScrSaver",
                    new Size(masterW, masterH), group.Bbox,
                    new Point(rect.X - group.Bbox.X, rect.Y - group.Bbox.Y),
                    isPreview: false, inputExit: true,
                    resMult: Cfg.ResMult, scaleBlend: Cfg.ScaleBlend);
                var scr = screens.FirstOrDefault(s => s.Bounds == rect);
                if (scr != null) win.RefreshHz = GetScreenRefreshHz(scr);
                Log.W($"  window bounds={rect}: refresh={win.RefreshHz:0.#} Hz");
                windows.Add(win);
            }
        }
        Log.W($"Windows created: {windows.Count}");
        return windows;
    }

    // One display's current refresh rate (query failure -> 60 Hz fallback).
    internal static double GetScreenRefreshHz(Screen s)
    {
        var dm = new Native.DEVMODE();
        dm.dmSize = (ushort) Marshal.SizeOf<Native.DEVMODE>();
        if (Native.EnumDisplaySettings(s.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref dm)
            && dm.dmDisplayFrequency > 0)
            return dm.dmDisplayFrequency;
        return 60.0;
    }

    private static int GetFastestRefresh(bool preview)
    {
        if (preview) return 60;
        int best = 60;
        foreach (Screen s in Screen.AllScreens)
        {
            double hz = GetScreenRefreshHz(s);
            Log.W($"  refresh of {s.DeviceName}: {hz:0.#} Hz");
            if (hz > best) best = (int) hz;
        }
        Log.W($"Target FPS (fastest display): {best}");
        return best;
    }

    // fps_cap 0 (auto) = uncapped; otherwise clamp to 1..max(fastest refresh, 120).
    private static int EffectiveFpsCap(int fastestRefresh)
    {
        int cap = Cfg.FpsCap;
        if (cap <= 0) return 0;
        int upper = Math.Max(fastestRefresh, 120);
        int eff = Math.Clamp(cap, 1, upper);
        if (eff != cap) Log.W($"FPS cap {cap} clamped to {eff} (1..{upper})");
        Log.W($"FPS cap: {eff} (setting={cap}, max={upper})");
        RenderWindow.RawPresentFlagMode = Cfg.RawPresentFlags;
        Log.W($"Raw present flag mode: {Cfg.RawPresentFlags}");
        return eff;
    }

    // Shader sources for RunLoop (created in CreateWindows).
    internal static string VertexShaderStatic;
    internal static string FragmentShaderStatic;

    // ---- /s ----
    private static void RunFullscreen()
    {
        using var mutex = new Mutex(true, @"Local\WinGLSLScrSaverRunning", out bool createdNew);
        Log.W($"Fullscreen: mutex createdNew={createdNew}");
        if (!createdNew)
        {
            Log.W("Another instance already running -> exit");
            return;
        }

        // Keep the displays on for the whole run (OS power plan can blank a
        // monitor mid-run otherwise).
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED);
        Log.W("Fullscreen: display keep-awake set (ES_DISPLAY_REQUIRED)");

        try
        {
            var windows = CreateWindows(LoadShaderText(), preview: false, Rectangle.Empty,
                out VertexShaderStatic, out FragmentShaderStatic);
            if (windows.Count == 0)
            {
                Log.W("ABORT: no windows created");
                return;
            }
            int fps = GetFastestRefresh(preview: false);
            RunLoop(windows, fps, EffectiveFpsCap(fps));
        }
        finally
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    // ---- /p <hwnd> ----
    private static void RunPreview(IntPtr parentHwnd)
    {
        Log.W($"Preview: parent hwnd=0x{parentHwnd.ToInt64():X}");
        // Single instance: preview and fullscreen runs must never coexist.
        using var mutex = new Mutex(true, @"Local\WinGLSLScrSaverRunning", out bool previewMutexNew);
        if (!previewMutexNew)
        {
            Log.W("Preview: another instance already running -> exit");
            return;
        }
        if (!Native.GetClientRect(parentHwnd, out Native.RECT r) || r.Right <= r.Left || r.Bottom <= r.Top)
        {
            Log.W("Preview: GetClientRect FAILED or empty rect");
            return;
        }
        Log.W($"Preview: client rect L={r.Left} T={r.Top} R={r.Right} B={r.Bottom}");

        var bounds = new Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
        var windows = CreateWindows(LoadShaderText(), preview: true, bounds,
            out VertexShaderStatic, out FragmentShaderStatic);
        if (windows.Count == 0) return;

        var win = windows[0];
        win.Show();
        _ = win.Handle; // force handle creation
        Log.W($"Preview: child hwnd=0x{win.Handle.ToInt64():X}");

        IntPtr setParentResult = Native.SetParent(win.Handle, parentHwnd);
        Log.W($"Preview: SetParent result=0x{setParentResult.ToInt64():X} (nonzero = ok)");
        IntPtr styleResult = Native.SetWindowLong(win.Handle, Native.GWL_STYLE,
            new IntPtr(Native.WS_CHILD | Native.WS_VISIBLE));
        Log.W($"Preview: SetWindowLong result={styleResult.ToInt64()}");

        RunLoop(windows, GetFastestRefresh(preview: true), 0);
    }
}
