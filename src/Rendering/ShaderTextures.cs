namespace WinGLSLScrSaver;

using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Pie;

// Shader-referenced PNG textures: for every declared sampler a PNG named
// name.png is looked up next to the shader file, then in the shader folder,
// then in the app data dir. Rows are flipped at load so uv.y = up matches
// the quad's bottom-up fragTexCoord (D3D11 texture origin is top-left).

internal static class ShaderTextures
{
    public sealed class BoundTexture
    {
        public string Name;
        public int Slot;
        public Texture Tex;
        public string PngPath;
        public void Dispose()
        {
            Tex?.Dispose();
            Tex = null;
        }
    }

    // (binding, name) pairs extracted from the FINAL fragment source - i.e.
    // the declarations TextureDeclarations emitted. Nothing else in the final
    // source uses `layout (binding = N)` for sampler2D, and slot 0 is never
    // generated (it is the UBO's).
    private static List<(int Slot, string Name)> Declared(string finalFragmentSrc)
    {
        var list = new List<(int, string)>();
        foreach (Match m in Regex.Matches(finalFragmentSrc,
                     @"layout\s*\(\s*binding\s*=\s*(\d+)\s*\)\s*uniform\s+sampler2D\s+(\w+)\s*;"))
            list.Add((int.Parse(m.Groups[1].Value), m.Groups[2].Value));
        return list;
    }

    public static string FindPng(string name, string sourcePath)
    {
        string[] dirs =
        {
            string.IsNullOrWhiteSpace(sourcePath)
                ? null : Path.GetDirectoryName(Path.GetFullPath(sourcePath)),
            string.IsNullOrWhiteSpace(Program.Cfg.ShaderFolder)
                ? null : Program.Cfg.ShaderFolder,
            Settings.AppDataDir,
        };
        foreach (string dir in dirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            try
            {
                if (!Directory.Exists(dir)) continue;
                string hit = Directory.GetFiles(dir, "*.png")
                    .FirstOrDefault(p => string.Equals(Path.GetFileNameWithoutExtension(p),
                        name, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            catch { /* unreadable dir -> try the next root */ }
        }
        return null;
    }

    // Decode a PNG to RGBA with rows flipped bottom-up.
    public static void DecodePngFlipped(string pngPath, out int w, out int h, out byte[] rgba)
    {
        using var bmp = new Bitmap(pngPath);
        w = bmp.Width;
        h = bmp.Height;
        rgba = new byte[w * h * 4];
        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                IntPtr srcRow = IntPtr.Add(bd.Scan0, (h - 1 - y) * bd.Stride); // flip
                Marshal.Copy(srcRow, row, 0, w * 4);
                int dst = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    rgba[dst + x * 4 + 0] = row[x * 4 + 2]; // BGRA -> RGBA
                    rgba[dst + x * 4 + 1] = row[x * 4 + 1];
                    rgba[dst + x * 4 + 2] = row[x * 4 + 0];
                    rgba[dst + x * 4 + 3] = row[x * 4 + 3];
                }
            }
        }
        finally
        {
            bmp.UnlockBits(bd);
        }
    }

    public static Texture CreateTexture(GraphicsDevice device, int w, int h, byte[] rgba) =>
        device.CreateTexture(TextureDescription.Texture2D(w, h,
            Format.R8G8B8A8_UNorm, 1, 1, TextureUsage.ShaderResource), rgba);

    // Load + bind every declared sampler that has a PNG. Samplers without a
    // PNG are left unbound (D3D11 samples a null SRV as black) and logged.
    public static List<BoundTexture> CreateAll(GraphicsDevice device,
        string finalFragmentSrc, string sourcePath)
    {
        var result = new List<BoundTexture>();
        foreach (var (slot, name) in Declared(finalFragmentSrc))
        {
            try
            {
                string png = FindPng(name, sourcePath);
                if (png == null)
                {
                    Log.W($"Texture '{name}' (binding {slot}): no matching PNG found - left unbound");
                    continue;
                }
                DecodePngFlipped(png, out int w, out int h, out byte[] rgba);
                Texture tex = CreateTexture(device, w, h, rgba);
                result.Add(new BoundTexture { Name = name, Slot = slot, Tex = tex, PngPath = png });
                Log.W($"Texture '{name}' bound at binding {slot}: '{png}' ({w}x{h})");
            }
            catch (Exception ex)
            {
                Log.Ex($"Texture '{name}' load", ex);
            }
        }
        return result;
    }
}
