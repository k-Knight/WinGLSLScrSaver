namespace WinGLSLScrSaver;

using System;
using System.IO;

internal static class Settings
{
    // %localappdata%\WinGLSLScrSaver holds the config file, the log, the
    // fragment dump and the default shader location - no machine-specific
    // path is ever baked in.
    public static string AppDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGLSLScrSaver");

    private static string ConfigPath => Path.Combine(AppDataDir, "config.txt");

    // config.txt holds the shader FOLDER; empty string = not configured.
    public static string LoadShaderFolder()
    {
        try { return File.ReadAllText(ConfigPath).Trim(); }
        catch { return ""; }
    }

    public static void SaveShaderFolder(string folder)
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(ConfigPath, folder ?? "");
    }
}
