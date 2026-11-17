namespace WinGLSLScrSaver;

using System.Globalization;

internal static class Settings
{
    // %localappdata%\WinGLSLScrSaver holds the config file, log and default shaders.
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGLSLScrSaver");

    internal static string ConfigPath => Path.Combine(AppDataDir, "config.txt");
}

// Persisted settings. config.txt is key=value lines:
//   folder=...          shader folder (empty = not configured)
//   fps_cap=120         render loop FPS cap; 0 = auto (fastest display's refresh)
//   res_mult=1.0        render resolution multiplier 0.05..1.0 (<1 = render small, upscale)
//   scale_blend=0.5     upscale interpolation 0 = nearest, 1 = bilinear
//   show_fps=1          per-window fps overlay (counts frames actually displayed)
//   tear_fix_child=0    invisible child window tearing fix (only when show_fps off);
//                       same DWM-composition hack as the fps label, but nothing visible
//   raw_present_flags=3 present flags for raw windows (gated runs): 0 = none,
//                       1 = ALLOW_TEARING, 2 = DO_NOT_WAIT|ALLOW_TEARING, 3 = DO_NOT_WAIT (default).
//                       1/2 tear visibly; a child window over the flip swapchain masks it
//   fps_pace=rel        grid = absolute ceil-quantized grid (old, aliases with DWM);
//                       rel = relative pacer, overrun-safe (default); pi = rel + PI trim
//   present_threads=0   1 = each non-owner window presents from its own thread
//   raw_swap_blt=0      0 = flip model (default), 1 = blt 2 buffers, 2 = blt 1 buffer;
//                       blt disables waitable/tearing flags and presents with flags=0
//   swap_buffers=4      raw swapchain buffers 2..8 (deep queue = presents return instantly)
//   max_latency=3       DXGI MaximumFrameLatency 1..(swap_buffers-1)
//   present_lanes=2     1 = one device lane (presents serialize, ~83 fps multi-mon cap);
//                       2 = per-window device via keyed-mutex shared canvas (default)
// A legacy config.txt that is a bare folder path (no '=') is still honoured.
internal sealed class AppSettings
{
    public const float MinResMult = 0.05f;
    public const float MaxResMult = 1.0f;

    public string ShaderFolder = "";
    public int FpsCap = 120;        // 0 = auto (vsync at fastest refresh)
    public float ResMult = 0.5f;
    public float ScaleBlend = 0.5f;
    public bool ShowFps = true;     // per-window fps overlay
    public bool TearFixChild;       // invisible child window tearing fix (only when ShowFps is off)
    public int RawPresentFlags = 3; // see header comment (3 = DO_NOT_WAIT only)
    public string FpsPace = "rel";  // grid | rel | pi
    public bool PresentThreads;     // per-window present threads (raw windows)
    public int SwapBuffers = 4;     // raw swapchain buffer count 2..8
    public int MaxLatency = 3;      // DXGI MaximumFrameLatency
    public int PresentLanes = 2;    // 1 = one device lane, 2 = per-window lanes (default 2)
    public int RawSwapBlt;          // 0 = flip, 1 = blt 2 buffers, 2 = blt 1 buffer

    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            string text = File.ReadAllText(Settings.ConfigPath).Trim();
            if (text.Length == 0) return s;

            if (!text.Contains('='))
            {
                // Legacy single-line config: the shader folder only.
                s.ShaderFolder = text;
                return s;
            }

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant();
                string val = line[(eq + 1)..].Trim();

                switch (key)
                {
                    case "folder":
                        s.ShaderFolder = val;
                        break;
                    case "fps_cap":
                        if (int.TryParse(val, out int cap)) s.FpsCap = cap;
                        break;
                    case "res_mult":
                        if (float.TryParse(val, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out float rm))
                            s.ResMult = rm;
                        break;
                    case "scale_blend":
                        if (float.TryParse(val, NumberStyles.Float,
                                CultureInfo.InvariantCulture, out float sb))
                            s.ScaleBlend = sb;
                        break;
                    case "show_fps":
                        s.ShowFps = val is "1" or "true" or "on";
                        break;
                    case "tear_fix_child":
                        s.TearFixChild = val is "1" or "true" or "on";
                        break;
                    case "raw_present_flags":
                        if (int.TryParse(val, out int rpf)) s.RawPresentFlags = rpf;
                        break;
                    case "fps_pace":
                        s.FpsPace = val is "grid" or "rel" or "pi" ? val : "rel";
                        break;
                    case "present_threads":
                        s.PresentThreads = val is "1" or "true" or "on";
                        break;
                    case "swap_buffers":
                        if (int.TryParse(val, out int sb2)) s.SwapBuffers = sb2;
                        break;
                    case "max_latency":
                        if (int.TryParse(val, out int ml)) s.MaxLatency = ml;
                        break;
                    case "present_lanes":
                        if (int.TryParse(val, out int pl) && pl is 1 or 2) s.PresentLanes = pl;
                        break;
                    case "raw_swap_blt":
                        if (int.TryParse(val, out int rsb) && rsb is >= 0 and <= 2) s.RawSwapBlt = rsb;
                        break;
                }
            }
        }
        catch { /* missing/corrupt config = defaults */ }

        s.ResMult = Math.Clamp(s.ResMult, MinResMult, MaxResMult);
        s.ScaleBlend = Math.Clamp(s.ScaleBlend, 0f, 1f);
        if (s.FpsCap < 0) s.FpsCap = 0;
        s.SwapBuffers = Math.Clamp(s.SwapBuffers, 2, 8);
        s.MaxLatency = Math.Clamp(s.MaxLatency, 1, s.SwapBuffers - 1);
        if (s.PresentLanes is < 1 or > 2) s.PresentLanes = 1;
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Settings.AppDataDir);
            File.WriteAllText(Settings.ConfigPath,
                $"folder={ShaderFolder}\n" +
                $"fps_cap={FpsCap}\n" +
                $"res_mult={ResMult.ToString("0.####", CultureInfo.InvariantCulture)}\n" +
                $"scale_blend={ScaleBlend.ToString("0.####", CultureInfo.InvariantCulture)}\n" +
                $"show_fps={(ShowFps ? 1 : 0)}\n" +
                $"tear_fix_child={(TearFixChild ? 1 : 0)}\n" +
                $"raw_present_flags={RawPresentFlags}\n" +
                $"fps_pace={FpsPace}\n" +
                $"present_threads={(PresentThreads ? 1 : 0)}\n" +
                $"swap_buffers={SwapBuffers}\n" +
                $"max_latency={MaxLatency}\n" +
                $"present_lanes={PresentLanes}\n" +
                $"raw_swap_blt={RawSwapBlt}\n");
        }
        catch { /* best effort */ }
    }
}
