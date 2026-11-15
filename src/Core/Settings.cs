namespace WinGLSLScrSaver;

using System.Globalization;

internal static class Settings
{
    // %localappdata%\WinGLSLScrSaver holds the config file, the log and
    // the default shader location - no machine-specific path is ever baked in.
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGLSLScrSaver");

    internal static string ConfigPath => Path.Combine(AppDataDir, "config.txt");
}

// Persisted settings. config.txt is key=value lines:
//   folder=...      shader folder (empty = not configured)
//   fps_cap=0       render loop FPS cap; 0 = auto (present with vsync, today's behavior)
//   res_mult=1.0    resolution multiplier 0.05..1.0 (below 1.0 renders into a
//                   smaller target that is upscaled on display)
//   scale_blend=0.5 upscale interpolation: 0 = nearest neighbour, 1 = bilinear,
//                   between = per-pixel weighted mix
//   show_fps=0     per-window fps overlay; counts frames actually handed to
//                  the display (post gating), not render ticks
//   raw_present_flags=3  present flags for raw/lane windows (gated runs):
//                  0 = none, 1 = ALLOW_TEARING, 2 = DO_NOT_WAIT|ALLOW_TEARING
//                  3 = DO_NOT_WAIT only (default). Modes 1/2 add ALLOW_TEARING:
//                  fps-investigation only - with SyncInterval 0 they present
//                  straight to scanout and visibly tear on non-primary
//                  monitors. The show_fps label hides the tearing (a child
//                  window over the flip swapchain forces DWM-composed,
//                  vblank-synced presentation).
//   fps_pace=rel    how the FPS cap paces the loop (only when fps_cap>0 or
//                  fullscreen canvas runs):
//                  grid = absolute 120Hz-style grid, ceil-quantized (old
//                         behavior; beats with composition sync when per-
//                         frame work sits just under one cap slot -> the
//                         measured 80 fps instead of 120)
//                  rel  = relative pacer: next due = previous due + 1/cap,
//                         overrun-safe (reset when far behind). No grid
//                         aliasing -> the cap is actually reached.
//                  pi   = rel + a small proportional-integral trim of the
//                         wait from the measured frame-interval error
//                  (default rel: deterministic, strictly better than grid)
//   present_threads=0  1 = each non-owner window presents from its own
//                  thread, so the ~5.9ms DWM composition-period sync inside
//                  Present(0) runs in parallel instead of eating the render
//                  loop's frame budget. Owner stays on the main thread (Pie
//                  device context is not thread-safe).
//   raw_swap_blt=0  raw-window swapchain model experiment for the Present
//                  stall investigation:
//                  0 = flip model (default; on this driver each Present(0)
//                      syncs one DWM composition period ~5.9ms)
//                  1 = bitblt model, 2 buffers (Present becomes a copy into
//                      DWM's surface, expected to return without the
//                      compose-period sync; 1 buffer was measured at ~6ms
//                      stall - that was the single-buffer release sync)
//                  2 = bitblt model, 1 buffer (the old measured variant)
//                  Blt mode disables the waitable object and tearing flags
//                  (not supported by the blt model) and presents with
//                  flags=0; windowed blt cannot tear (DWM composes from its
//                  own copy), so no vsync is faked or needed.
//   swap_buffers=4  raw swapchain buffer count 2..8 (deep queue = presents
//                  return immediately; scanout drains at each monitor's own
//                  refresh). "Fake vsync at the cost of extra buffers".
//   max_latency=3   DXGI MaximumFrameLatency 1..(swap_buffers-1); lower =
//                  less queued latency, higher = more present slack.
//   present_lanes=1 1 = all swapchains on the owner device (one present
//                  lane; Present calls serialize at DWM composition
//                  granularity, capping multi-window setups at ~83 fps here).
//                  2 = each non-owner window gets its own D3D11 device as a
//                  second, fully parallel present lane; the shared canvas
//                  crosses devices via a keyed-mutex shared texture. This is
//                  the "extra buffer/lane per window" fix - no tearing (DWM
//                  still composes) and no fps reduction.
// A legacy config.txt that is a bare folder path (no '=') is still honoured.
internal sealed class AppSettings
{
    public const float MinResMult = 0.05f;
    public const float MaxResMult = 1.0f;

    public string ShaderFolder = "";
    public int FpsCap = 120;        // default 120; 0 = auto (vsync at fastest refresh)
    public float ResMult = 0.5f;
    public float ScaleBlend = 0.5f;
    public bool ShowFps = true;     // per-window fps overlay
    public int RawPresentFlags = 3; // see header comment (3 = DO_NOT_WAIT only - ALLOW_TEARING was the fps-test flag; it visibly tears)
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
