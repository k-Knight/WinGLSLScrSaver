namespace WinGLSLScrSaver;

// Compiled-shader binary cache: the SPIR-V shaderc produces for the final
// adapted fragment shader, keyed on the shader file's mtime + length. Any
// edit invalidates the entry and the next start recompiles and re-stores.
// On a hit shaderc never runs (SPIRV-Cross -> HLSL inside Pie still runs;
// that part is not cacheable from outside).

internal static class ShaderCache
{
    private const string Magic = "WGLSCRCACHE1";

    private static string CacheDir => Path.Combine(Settings.AppDataDir, "cache");

    // Entry file name: FNV-1a 64 of the full (lowercased) source path - stable
    // across runs, and distinct per shader file.
    private static string EntryPath(string sourcePath)
    {
        ulong h = 14695981039346656037;
        foreach (char c in sourcePath.ToLowerInvariant())
        {
            h ^= c;
            h *= 1099511628211;
        }
        return Path.Combine(CacheDir, h.ToString("x16") + ".spv");
    }

    // A hit requires the SAME mtime and length as when the entry was written.
    public static bool TryLoad(string sourcePath, out byte[] spirv)
    {
        spirv = null;
        try
        {
            string path = EntryPath(sourcePath);
            if (!File.Exists(path)) return false;
            byte[] all = File.ReadAllBytes(path);
            int nl = Array.IndexOf(all, (byte) '\n');
            if (nl <= 0) return false;
            string[] parts = System.Text.Encoding.ASCII.GetString(all, 0, nl).Split('|');
            if (parts.Length != 4 || parts[0] != Magic) return false;

            var info = new FileInfo(sourcePath);
            if (!info.Exists) return false;
            if (parts[1] != info.LastWriteTimeUtc.Ticks.ToString()) return false;
            if (parts[2] != info.Length.ToString()) return false;
            if (parts[3] != (all.Length - nl - 1).ToString()) return false;

            spirv = all[(nl + 1)..];
            Log.W($"Shader cache HIT for '{sourcePath}' ({spirv.Length} bytes SPIR-V, mtime matched)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Ex("ShaderCache.TryLoad", ex);
            return false;
        }
    }

    public static void Store(string sourcePath, byte[] spirv)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var info = new FileInfo(sourcePath);
            string header = $"{Magic}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{spirv.Length}";
            byte[] head = System.Text.Encoding.ASCII.GetBytes(header + "\n");
            File.WriteAllBytes(EntryPath(sourcePath), head.Concat(spirv).ToArray());
            Log.W($"Shader cache stored for '{sourcePath}' ({spirv.Length} bytes SPIR-V)");
        }
        catch (Exception ex)
        {
            Log.Ex("ShaderCache.Store", ex);
        }
    }
}
