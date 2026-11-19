namespace WinGLSLScrSaver;

// Portable native deps: the two Scintilla native DLLs (Scintilla.dll,
// Lexilla.dll) are embedded in the .scr as resources and written to an
// x64\ folder next to the executable on the very first launch. The OS
// loader cannot load native DLLs from inside the single-file bundle, so
// they must exist as real files on disk - this class makes that happen
// automatically, so deleting x64\ no longer crashes the app: it is simply
// recreated on the next start. DLLs already on disk are never overwritten
// (an existing, possibly patched x64\ is left alone).
internal static class NativeDeps
{
    public static void Ensure()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "x64");
        foreach (var name in new[] { "Scintilla.dll", "Lexilla.dll" })
        {
            var target = System.IO.Path.Combine(dir, name);
            if (System.IO.File.Exists(target)) continue;
            try
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s == null) { Log.W($"NativeDeps: resource '{name}' missing from the bundle"); continue; }
                System.IO.Directory.CreateDirectory(dir);
                var tmp = target + ".tmp";
                using (var f = System.IO.File.Create(tmp))
                    s.CopyTo(f);
                System.IO.File.Move(tmp, target);
                Log.W($"NativeDeps: extracted {name} ({s.Length} bytes)");
            }
            catch (Exception ex) { Log.Ex($"NativeDeps: extracting {name}", ex); }
        }
    }
}
