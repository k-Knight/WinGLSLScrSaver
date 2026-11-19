namespace WinGLSLScrSaver;


internal static class Log
{
    private static readonly object _lock = new();
    private static string _path;

    // Release builds are silent by default (no per-event file I/O); Debug
    // builds are verbose; --diag:N forces verbose in any build.
#if DEBUG
    internal static bool Verbose = true;
#else
    internal static bool Verbose = false;
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

    // Walks the whole InnerException chain - wrapper exceptions like
    // TypeInitializationException hide the real cause (the native DLL
    // load failure inside ScintillaNET.Scintilla's static constructor).
    public static void Ex(string what, Exception ex)
    {
        W($"EXCEPTION in {what}: {ex.GetType().Name}: {ex.Message}");
        W($"  stack: {ex.StackTrace?.Replace(Environment.NewLine, " | ")}");
        Exception cur = ex;
        int depth = 0;
        while (cur.InnerException != null && depth < 8)
        {
            cur = cur.InnerException;
            depth++;
            W($"  INNER {depth}: {cur.GetType().FullName}: {cur.Message}");
            W($"    stack: {cur.StackTrace?.Replace(Environment.NewLine, " | ")}");
        }
    }
}
