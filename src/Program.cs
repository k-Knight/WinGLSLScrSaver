// ===========================================================================
// WinGLSLScrSaver - a GLSL shader screensaver for Windows (.scr)
// ---------------------------------------------------------------------------
// Renders a GLSL fragment shader on the GPU (Direct3D 11 via Pie) as a real
// Windows screensaver with full /s /p /c support. A random shader is drawn
// from the configured folder on every start.
//
// DEBUG BUILD NOTES:
//   * DEBUG builds and --diag runs log to %localappdata%\WinGLSLScrSaver\
//     (falls back to %TEMP%). Release builds are silent: no log file, no
//     fragment dump, zero per-call file I/O.
//   * GPU device creation is deferred until AFTER the form is shown and
//     (for preview) reparented - WinForms recreates window handles when
//     properties change, which would orphan a device created too early.
// ===========================================================================
namespace WinGLSLScrSaver;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal static class Program
{
    private static bool _running = true;

    // --diag=1: gradient to backbuffer (blit path)   --diag=2: solid red into
    // RT, blitted normally (RT path)   --diag=3: full pipeline + frame logs.
    // --diag=4: gradient + debug layer + info queue dump + pixel readback.
    internal static int DiagMode = 0;

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Ex("UnhandledException", (Exception) e.ExceptionObject);

        // NOTE: never interpolate Log.Path into a W() call unconditionally -
        // the argument is evaluated even when logging is off, which used to
        // create an empty log file next to the exe in Release.
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
        Log.W($"mode={mode} previewHwnd=0x{previewHwnd.ToInt64():X}");

        // Optional diagnostic flag: --diag=N or /diag:N
        foreach (string a in args)
        {
            Match dm = Regex.Match(a, @"(?i)^[/\-]{1,2}diag[:=](\d)$");
            if (dm.Success)
            {
                DiagMode = dm.Groups[1].Value[0] - '0';
                Log.Verbose = true; // an explicit diag request re-enables logging even in Release
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
                    if (previewHwnd == IntPtr.Zero)
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
        // ShowDialog (not Application.Run): with a modeless main form the
        // OK/Cancel buttons never closed the dialog and DialogResult stayed
        // None, so no config file was ever written. ShowDialog gives the
        // buttons their normal semantics; saving still happens OnFormClosed.
        new ConfigDialog().ShowDialog();
        Log.W("Config dialog closed");
    }

    // Pick this run's shader: a random .fs/.glsl/.frag from the configured
    // folder (falling back to the local app data dir when not configured or
    // the folder is gone/empty), else the embedded default.
    private static string LoadShaderText()
    {
        string folder = Settings.LoadShaderFolder();
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
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.fs")
                    .Concat(Directory.GetFiles(folder, "*.glsl"))
                    .Concat(Directory.GetFiles(folder, "*.frag"))
                    .ToArray()
                : Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    // ---- shared setup ----
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

        // Dump the EXACT fragment source for inspection (debug aid only -
        // skipped in silent Release runs). Lands in the local app data dir.
        if (Log.Verbose)
        {
            try
            {
                Directory.CreateDirectory(Settings.AppDataDir);
                File.WriteAllText(System.IO.Path.Combine(Settings.AppDataDir, "ss_fragment_dump.fs"), fragmentShader);
                Log.W($"Fragment dump written to {Path.Combine(Settings.AppDataDir, "ss_fragment_dump.fs")}");
            }
            catch (Exception ex) { Log.W($"Fragment dump failed: {ex.Message}"); }
        }

        var windows = new List<RenderWindow>();

        if (preview)
        {
            Log.W($"Preview window bounds: {previewBounds}");
            var win = new RenderWindow(previewBounds, "WinGLSLScrSaver Preview",
                new Size(previewBounds.Width, previewBounds.Height),
                new Rectangle(0, 0, previewBounds.Width, previewBounds.Height),
                new Point(0, 0),
                isPreview: true, inputExit: false);
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
                windows.Add(new RenderWindow(rect, "WinGLSLScrSaver",
                    new Size(masterW, masterH), group.Bbox,
                    new Point(rect.X - group.Bbox.X, rect.Y - group.Bbox.Y),
                    isPreview: false, inputExit: true));
            }
        }
        Log.W($"Windows created: {windows.Count}");
        return windows;
    }

    private static int GetFastestRefresh(bool preview)
    {
        if (preview) return 60;
        int best = 60;
        foreach (Screen s in Screen.AllScreens)
        {
            var dm = new Native.DEVMODE();
            dm.dmSize = (ushort) Marshal.SizeOf<Native.DEVMODE>();
            if (Native.EnumDisplaySettings(s.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref dm))
            {
                Log.W($"  refresh of {s.DeviceName}: {dm.dmDisplayFrequency} Hz");
                if (dm.dmDisplayFrequency > best) best = dm.dmDisplayFrequency;
            }
        }
        Log.W($"Target FPS (fastest display): {best}");
        return best;
    }

    private static void RunLoop(List<RenderWindow> windows, int targetFps)
    {
        // Init graphics BEFORE showing: the device still binds to the final
        // window handle (created on demand here; preview reparenting already
        // happened in RunPreview), and the first frame is on the swapchain by
        // the time the window becomes visible - no white flash at startup.
        int okCount = 0;
        foreach (var w in windows)
            if (w.InitGraphics(VertexShaderStatic, FragmentShaderStatic))
                okCount++;
        Log.W($"Graphics initialized on {okCount}/{windows.Count} windows");

        if (okCount == 0)
        {
            Log.W("ABORT: no window initialized graphics");
            _running = false;
        }

        foreach (var w in windows) w.Show();
        if (windows.Count > 0 && !windows[0].IsPreview)
            windows[0].Activate();
        Log.W("All windows shown");

        var sw = Stopwatch.StartNew();
        var frame = TimeSpan.FromMilliseconds(1000.0 / targetFps);
        double diagSeconds = Program.DiagMode > 0 ? 6.0 : double.PositiveInfinity;
        long frames = 0;

        while (_running)
        {
            Application.DoEvents();
            if (!_running) break;

            if (sw.Elapsed.TotalSeconds > diagSeconds)
            {
                Log.W("DIAG auto-exit reached");
                break;
            }

            if (windows.Any(w => w.ShutdownRequested))
            {
                Log.W("Exit: input shutdown requested");
                break;
            }
            if (windows.All(w => w.IsDisposed))
            {
                Log.W("Exit: all windows closed");
                break;
            }

            float t = (float) sw.Elapsed.TotalSeconds;
            foreach (var w in windows)
                w.Render(t);

            frames++;
            if (frames == 1) Log.W("First frame rendered");
            if (frames % 600 == 0) Log.W($"Heartbeat: {frames} frames, t={t:F1}s");

            var elapsed = sw.Elapsed;
            var nextFrame = TimeSpan.FromTicks((long) Math.Ceiling(elapsed.Ticks / (double) frame.Ticks) * frame.Ticks);
            var remain = nextFrame - elapsed;
            while (remain > TimeSpan.Zero && _running)
            {
                if (remain > TimeSpan.FromMilliseconds(2))
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(50);
                elapsed = sw.Elapsed;
                remain = nextFrame - elapsed;
            }
        }

        Log.W($"Render loop ended after {frames} frames");
        foreach (var w in windows)
        {
            try { w.Close(); } catch { }
        }
        Log.W("All windows closed, exiting");
    }

    // Shader source strings stashed for RunLoop (created in CreateWindows).
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

        // Keep the displays on for the whole run: without this, the Windows
        // power plan can blank a monitor while the screensaver is animating
        // (reported: second monitor occasionally switched off, then back on).
        // Note this only prevents OS power management; a real cable/hardware
        // dropout would still be visible in the Event Viewer diagnostics.
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
            RunLoop(windows, fps);
        }
        finally
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS); // clear keep-awake
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    // ---- /p <hwnd> ----
    private static void RunPreview(IntPtr parentHwnd)
    {
        Log.W($"Preview: parent hwnd=0x{parentHwnd.ToInt64():X}");
        if (!Native.GetClientRect(parentHwnd, out Native.RECT r))
        {
            Log.W("Preview: GetClientRect FAILED");
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

        RunLoop(windows, GetFastestRefresh(preview: true));
    }
}
