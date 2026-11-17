namespace WinGLSLScrSaver;


internal static class Log
{
    private static readonly object _lock = new();
    private static string _path;

    // Logging is a debug aid: enabled in DEBUG builds or with an explicit
    // --diag flag. Release runs stay silent (no per-call file I/O).
    internal static bool Verbose =
#if DEBUG
        true;
#else
        false;
#endif

    public static string Path
    {
        get
        {
            if (_path != null) return _path;
            // Logs live in %localappdata%\WinGLSLScrSaver\ - never next to
            // the exe (a screensaver's working dir is wherever Windows
            // launched it from, e.g. System32).
            try
            {
                Directory.CreateDirectory(Settings.AppDataDir);
                _path = System.IO.Path.Combine(Settings.AppDataDir, "winglslscrsaver.log");
                return _path;
            }
            catch { }
            _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "winglslscrsaver.log");
            return _path;
        }
    }

    public static void W(string msg)
    {
        if (!Verbose) return;
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        lock (_lock)
        {
            try { File.AppendAllText(Path, line + Environment.NewLine); } catch { }
            try { Console.WriteLine(line); } catch { }
        }
    }

    public static void Ex(string what, Exception ex)
    {
        W($"EXCEPTION in {what}: {ex.GetType().Name}: {ex.Message}");
        W($"  stack: {ex.StackTrace?.Replace(Environment.NewLine, " | ")}");
    }
}
