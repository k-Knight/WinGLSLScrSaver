namespace WinGLSLScrSaver;

using System;
using System.Globalization;
using System.IO;

internal static class Settings
{
    // %localappdata%\WinGLSLScrSaver holds the config file, the log, the
    // fragment dump and the default shader location - no machine-specific
    // path is ever baked in.
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
// A legacy config.txt that is a bare folder path (no '=') is still honoured.
internal sealed class AppSettings
{
    public const float MinResMult = 0.05f;
    public const float MaxResMult = 1.0f;

    public string ShaderFolder = "";
    public int FpsCap;              // 0 = auto
    public float ResMult = 1.0f;
    public float ScaleBlend = 0.5f;

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
                }
            }
        }
        catch { /* missing/corrupt config = defaults */ }

        s.ResMult = Math.Clamp(s.ResMult, MinResMult, MaxResMult);
        s.ScaleBlend = Math.Clamp(s.ScaleBlend, 0f, 1f);
        if (s.FpsCap < 0) s.FpsCap = 0;
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
                $"scale_blend={ScaleBlend.ToString("0.####", CultureInfo.InvariantCulture)}\n");
        }
        catch { /* best effort */ }
    }
}
