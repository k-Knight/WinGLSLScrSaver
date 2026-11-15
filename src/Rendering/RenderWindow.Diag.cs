namespace WinGLSLScrSaver;

// Partial of RenderWindow: the --diag=4/5/6 pixel-readback helpers (sampled pixel logs).
// Split from RenderWindow.cs for maintainability - same class, zero
// behavior change. Core lifecycle, fields and Render() live in RenderWindow.cs.

using System.Reflection;
using D3D = Vortice.Direct3D11;

internal partial class RenderWindow
{
    // ---- DIAG 4 helpers ------------------------------------------------------

    // Both readback helpers pull Pie's raw immediate context out the same way.
    private D3D.ID3D11DeviceContext DiagCtx() =>
        (D3D.ID3D11DeviceContext) _device.GetType().GetField("_context",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_device);

    private unsafe void DumpBackbufferPixels()
    {
        try
        {
            var ctx = DiagCtx();
            var dev = (D3D.ID3D11Device) _d3dDevice;

            // Fresh staging texture per dump - same pattern as DumpD3DTex.
            // (The old shared _stagingTex field was never assigned, so this
            // readback used to copy from a null texture and always failed.)
            var sdesc = _backTex.Description;
            sdesc.Usage = D3D.ResourceUsage.Staging;
            sdesc.BindFlags = D3D.BindFlags.None;
            sdesc.CPUAccessFlags = D3D.CpuAccessFlags.Read;
            sdesc.MiscFlags = D3D.ResourceOptionFlags.None;
            using D3D.ID3D11Texture2D staging = dev.CreateTexture2D(sdesc);
            ctx.CopyResource(staging, _backTex);

            D3D.MappedSubresource map = ctx.Map(staging, 0, D3D.MapMode.Read, D3D.MapFlags.None);
            {
                int w = _backTex.Description.Width;
                int h = _backTex.Description.Height;
                int rowPitch = (int) map.RowPitch;
                Log.W($"DIAG4 READBACK {w}x{h} rowPitch={rowPitch}:");
                foreach ((int fx, string nm) in new[] { (w / 10, "left"), (w / 2, "mid"), (w * 9 / 10, "right") })
                {
                    byte* p = (byte*) map.DataPointer + (h / 2) * rowPitch + fx * 4;
                    Log.W($"DIAG4 pixel {nm} ({fx},{h / 2}) = R{p[0]} G{p[1]} B{p[2]} A{p[3]}");
                }
                byte* tl = (byte*) map.DataPointer + 5 * rowPitch + 5 * 4;
                byte* br = (byte*) map.DataPointer + (h - 6) * rowPitch + (w - 6) * 4;
                Log.W($"DIAG4 pixel topleft = R{tl[0]} G{tl[1]} B{tl[2]} A{tl[3]}");
                Log.W($"DIAG4 pixel botright = R{br[0]} G{br[1]} B{br[2]} A{br[3]}");
            }
            ctx.Unmap(staging, 0);
        }
        catch (Exception ex) { Log.Ex("DIAG4 readback", ex); }
    }

    // Generic readback: copies any D3D texture to a fresh staging texture and
    // logs sample pixels.
    private unsafe void DumpD3DTex(D3D.ID3D11Texture2D tex, string label)
    {
        try
        {
            var ctx = DiagCtx();
            var dev = (D3D.ID3D11Device) _d3dDevice;

            var sdesc = tex.Description;
            sdesc.Usage = D3D.ResourceUsage.Staging;
            sdesc.BindFlags = D3D.BindFlags.None;
            sdesc.CPUAccessFlags = D3D.CpuAccessFlags.Read;
            sdesc.MiscFlags = D3D.ResourceOptionFlags.None;
            using D3D.ID3D11Texture2D staging = dev.CreateTexture2D(sdesc);
            ctx.CopyResource(staging, tex);

            D3D.MappedSubresource map = ctx.Map(staging, 0, D3D.MapMode.Read, D3D.MapFlags.None);
            {
                int w = sdesc.Width, h = sdesc.Height, rowPitch = (int) map.RowPitch;
                Log.W($"DIAG5 READBACK {label} {w}x{h} rowPitch={rowPitch}:");
                foreach ((int fx, string nm) in new[] { (w / 10, "left"), (w / 2, "mid"), (w * 9 / 10, "right") })
                {
                    byte* p = (byte*) map.DataPointer + (h / 2) * rowPitch + fx * 4;
                    Log.W($"DIAG5 {label} pixel {nm} ({fx},{h / 2}) = R{p[0]} G{p[1]} B{p[2]} A{p[3]}");
                }
            }
            ctx.Unmap(staging, 0);
        }
        catch (Exception ex) { Log.Ex($"DIAG5 readback {label}", ex); }
    }

}
