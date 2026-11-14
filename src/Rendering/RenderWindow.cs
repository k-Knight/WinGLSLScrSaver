namespace WinGLSLScrSaver;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Pie;
using Pie.ShaderCompiler;
using D3D = Vortice.Direct3D11;

internal class RenderWindow : Form
{
    public readonly bool IsPreview;
    public readonly bool AllowInputExit;

    private readonly Size _master;          // master render resolution
    private readonly Rectangle _groupBbox;  // group bounding box (virtual canvas)
    private readonly Point _offsetInGroup;  // this monitor's offset in the group

    private readonly SizeF _fitted;         // fitted image size in group canvas
    private readonly PointF _letterbox;     // fitted image offset in group canvas

    private GraphicsDevice _device;
    private Shader _mainShader;
    private Shader _blitShader;
    private InputLayout _mainLayout;
    private InputLayout _blitLayout;
    private DepthStencilState _depthDisabled;
    private GraphicsBuffer _quadVb;
    private GraphicsBuffer _indexBuffer;
    private GraphicsBuffer _blitVb;
    private GraphicsBuffer _blitVbStatic;
    private GraphicsBuffer _uniform;
    private Texture _rt;
    private Framebuffer _fb;
    private SamplerState _sampler;
    private bool _disposed;

    private static readonly Vector2[] QuadVertices =
    {
        new( 1f,  1f), new( 1f, -1f), new(-1f, -1f), new(-1f,  1f),
    };
    private static readonly uint[] QuadIndices = { 0u, 1u, 3u, 1u, 2u, 3u };

    private double _lastMouseY = -1, _lastMouseX = -1;
    private int _frameLogs;

    // DIAG 4 state.
    private object _d3dDevice;
    private D3D.ID3D11Texture2D _backTex;
    private D3D.ID3D11Texture2D _stagingTex;
    private D3D.ID3D11Texture2D _rtRT;
    private int _diagFrames;
    private bool _diagDone;

    public bool ShutdownRequested { get; private set; }

    private static Cursor _hiddenCursor;

    private static Cursor HiddenCursor()
    {
        if (_hiddenCursor != null) return _hiddenCursor;
        byte[] andPlane = Enumerable.Repeat((byte) 0xFF, 128).ToArray();
        byte[] xorPlane = new byte[128];
        _hiddenCursor = new Cursor(Native.CreateCursor(IntPtr.Zero, 0, 0, 32, 32, andPlane, xorPlane));
        return _hiddenCursor;
    }

    // Constructor does ONLY window setup - no Handle access, no GPU work.
    public RenderWindow(Rectangle bounds, string title, Size master,
                        Rectangle groupBbox, Point offsetInGroup,
                        bool isPreview, bool inputExit)
    {
        IsPreview = isPreview;
        AllowInputExit = inputExit && !isPreview;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        ShowInTaskbar = false;
        Text = title;
        KeyPreview = true;
        TopMost = !isPreview;

        // Never paint white: even before the first GPU frame, any window
        // surface exposed must be black (white startup flash report).
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);

        _master = master;
        _groupBbox = groupBbox;
        _offsetInGroup = offsetInGroup;

        float scale = Math.Min((float) groupBbox.Width / master.Width, (float) groupBbox.Height / master.Height);
        _fitted = new SizeF(master.Width * scale, master.Height * scale);
        _letterbox = new PointF((groupBbox.Width - _fitted.Width) / 2f, (groupBbox.Height - _fitted.Height) / 2f);

        Log.W($"RenderWindow created: bounds={bounds} master={master} groupBbox={groupBbox} " +
              $"offset={offsetInGroup} fitted={_fitted} letterbox={_letterbox} preview={isPreview}");
    }

    // Call AFTER the form is shown (and reparented, for preview).
    public bool InitGraphics(string vertexShader, string fragmentShader)
    {
        try
        {
            IntPtr hwnd = Handle;
            Size cs = ClientSize;
            Log.W($"InitGraphics: hwnd=0x{hwnd.ToInt64():X} clientSize={cs}");

            if (cs.Width <= 0 || cs.Height <= 0)
            {
                Log.W("InitGraphics ABORT: client size is empty");
                return false;
            }

            // NOTE: Debug=true is FORBIDDEN - Pie 0.10.1's DebugGraphicsDevice
            // wrapper throws SEHException natively on SetDepthStencilState here.
            _device = GraphicsDevice.CreateD3D11(hwnd, cs, new GraphicsDeviceOptions { Debug = false });
            Log.W("InitGraphics: D3D11 device created");

            _mainShader = _device.CreateShader(new[]
            {
                new ShaderAttachment(ShaderStage.Vertex, vertexShader),
                new ShaderAttachment(ShaderStage.Fragment, fragmentShader),
            });
            Log.W("InitGraphics: main shader compiled (GLSL -> SPIR-V -> HLSL)");

            _mainLayout = _device.CreateInputLayout(
                new InputLayoutDescription(Format.R32G32_Float, 0, 0, InputType.PerVertex));

            const string BlitVertex = @"
#version 450
layout (location = 0) in vec4 aPosUv; // xy = clip position, zw = uv
layout (location = 0) out vec2 vUv;
void main()
{
    vUv = aPosUv.zw;
    gl_Position = vec4(aPosUv.xy, 0.0, 1.0);
}";

            const string BlitFragment = @"
#version 450
layout (location = 0) in vec2 vUv;
layout (location = 0) out vec4 oColor;
layout (binding = 0) uniform sampler2D uTex;
void main()
{
    oColor = texture(uTex, vUv);
}";

            const string GradientBlitFragment = @"
#version 450
layout (location = 0) in vec2 vUv;
layout (location = 0) out vec4 oColor;
void main()
{
    oColor = vec4(vUv.x, vUv.y, 0.2, 1.0);
}";
            string blitFrag = Program.DiagMode is 1 or 4 or 6 ? GradientBlitFragment : BlitFragment;
            Log.W($"InitGraphics: blit shader = {(Program.DiagMode is 1 or 4 or 6 ? "GRADIENT (diag1/4/6)" : "texture blit")}");

            _blitShader = _device.CreateShader(new[]
            {
                new ShaderAttachment(ShaderStage.Vertex, BlitVertex),
                new ShaderAttachment(ShaderStage.Fragment, blitFrag),
            });
            _blitLayout = _device.CreateInputLayout(
                new InputLayoutDescription(Format.R32G32B32A32_Float, 0, 0, InputType.PerVertex));

            _depthDisabled = _device.CreateDepthStencilState(DepthStencilStateDescription.Disabled);
            _quadVb = _device.CreateBuffer(BufferType.VertexBuffer, QuadVertices);
            _indexBuffer = _device.CreateBuffer(BufferType.IndexBuffer, QuadIndices);
            _uniform = _device.CreateBuffer(BufferType.UniformBuffer, Vector4.Zero, true);

            // RT / blit machinery is only needed by --diag modes now; the normal
            // path renders the shader straight to the backbuffer (single pass).
            if (Program.DiagMode != 0)
            {
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer, ComputeBlitVerts());
                // DIAG 6: static control buffer (kept for repro runs).
                _blitVbStatic = _device.CreateBuffer(BufferType.VertexBuffer, new VPosUv[4]
                {
                    new(new Vector2( 1f,  1f), new Vector2(1f, 0f)),
                    new(new Vector2( 1f, -1f), new Vector2(1f, 1f)),
                    new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
                    new(new Vector2(-1f,  1f), new Vector2(0f, 0f)),
                });

                _rt = _device.CreateTexture(TextureDescription.Texture2D(_master.Width, _master.Height,
                    Format.R8G8B8A8_UNorm, 1, 1, TextureUsage.Framebuffer | TextureUsage.ShaderResource));
                _fb = _device.CreateFramebuffer(new FramebufferAttachment(_rt));
                _sampler = _device.CreateSamplerState(SamplerStateDescription.LinearClamp);
            }

            // Hide the cursor only after everything initialized (fullscreen).
            Cursor = IsPreview ? Cursors.Default : HiddenCursor();

            Log.W("InitGraphics: all GPU resources created OK");

            // ---- DIAG 4/5/6: grab raw D3D11 objects for pixel readback.
            if (Program.DiagMode is 4 or 5 or 6)
            {
                try
                {
                    _d3dDevice = (D3D.ID3D11Device) _device.GetType().GetField("_device",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_device);
                    _backTex = (D3D.ID3D11Texture2D) _device.GetType().GetField("_colorTexture",
                        BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_device);
                    Log.W($"DIAG{Program.DiagMode}: adapter = '{_device.Adapter}'");

                    if (Program.DiagMode is 5 or 6)
                    {
                        object rtRes = _rt.GetType().GetField("Texture")!.GetValue(_rt);
                        _rtRT = (D3D.ID3D11Texture2D) rtRes;
                    }
                }
                catch (Exception ex) { Log.Ex($"DIAG{Program.DiagMode} setup", ex); }
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Ex("InitGraphics", ex);
            return false;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        DisposeGraphics();
        base.OnFormClosed(e);
    }

    private void DisposeGraphics()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _rt?.Dispose(); _fb?.Dispose(); _sampler?.Dispose();
            _blitVb?.Dispose(); _blitVbStatic?.Dispose();
            _quadVb?.Dispose(); _indexBuffer?.Dispose(); _uniform?.Dispose();
            _mainLayout?.Dispose(); _blitLayout?.Dispose();
            _mainShader?.Dispose(); _blitShader?.Dispose(); _depthDisabled?.Dispose();
            _device?.Dispose();
            Log.W($"Graphics disposed for window bounds={Bounds} preview={IsPreview}");
        }
        catch (Exception ex) { Log.Ex("DisposeGraphics", ex); }
    }

    public void Render(float time)
    {
        if (_device == null || _disposed) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        // ---- DIAG 5: (a) gradient into the RT (plain texture) + readback,
        // (b) magenta clear on the backbuffer, no draw, + readback.
        // Decides: broken draw pipeline vs broken swapchain/present path.
        if (Program.DiagMode == 5)
        {
            // (a) RT pass with the gradient fragment + quadVb/mainLayout.
            _device.SetFramebuffer(_fb);
            _device.Viewport = new Rectangle(0, 0, _master.Width, _master.Height);
            _device.ClearColorBuffer(0f, 1f, 0f, 1f); // green background in RT
            _device.UpdateBuffer(_uniform, 0, new Vector4(_master.Width, _master.Height, 1f, time));
            _device.SetShader(_mainShader);
            _device.SetUniformBuffer(0u, _uniform);
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_mainLayout);
            _device.SetVertexBuffer(0u, _quadVb, 8u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);

            _diagFrames++;
            if (!_diagDone && _diagFrames >= 5)
            {
                _diagDone = true;
                DumpD3DTex(_rtRT, "RT");

                // (b) backbuffer: magenta clear ONLY, no draw.
                _device.SetFramebuffer(null);
                _device.Viewport = new Rectangle(0, 0, w, h);
                _device.ClearColorBuffer(1f, 0f, 1f, 1f); // MAGENTA
                DumpD3DTex(_backTex, "BACKBUFFER");
            }

            _device.Present(1);
            return;
        }

        // ---- DIAG 6: isolate the broken blit component. All draws go into the
        // RT (readable): A=main path (control), B=blit with DYNAMIC VB filled
        // via UpdateBuffer, C=blit with STATIC VB (initial data).
        if (Program.DiagMode == 6)
        {
            _diagFrames++;
            if (!_diagDone && _diagFrames >= 5)
            {
                _diagDone = true;
                _device.SetFramebuffer(_fb);
                _device.Viewport = new Rectangle(0, 0, _master.Width, _master.Height);

                // A: main path control.
                _device.ClearColorBuffer(0f, 1f, 0f, 1f);
                _device.UpdateBuffer(_uniform, 0, new Vector4(_master.Width, _master.Height, 1f, time));
                _device.SetShader(_mainShader);
                _device.SetUniformBuffer(0u, _uniform);
                _device.SetDepthStencilState(_depthDisabled);
                _device.SetPrimitiveType(PrimitiveType.TriangleList);
                _device.SetInputLayout(_mainLayout);
                _device.SetVertexBuffer(0u, _quadVb, 8u);
                _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
                _device.DrawIndexed(6u);
                DumpD3DTex(_rtRT, "RT-A-mainpath");

                // B: blit path with dynamic VB (UpdateBuffer-filled).
                _device.ClearColorBuffer(0f, 1f, 0f, 1f);
                VPosUv[] gv =
                {
                    new(new Vector2( 1f,  1f), new Vector2(1f, 0f)),
                    new(new Vector2( 1f, -1f), new Vector2(1f, 1f)),
                    new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
                    new(new Vector2(-1f,  1f), new Vector2(0f, 0f)),
                };
                _device.UpdateBuffer(_blitVb, 0, gv);
                _device.SetShader(_blitShader);
                _device.SetDepthStencilState(_depthDisabled);
                _device.SetPrimitiveType(PrimitiveType.TriangleList);
                _device.SetInputLayout(_blitLayout);
                _device.SetVertexBuffer(0u, _blitVb, 16u);
                _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
                _device.DrawIndexed(6u);
                DumpD3DTex(_rtRT, "RT-B-blit-dynamic");

                // C: blit path with static VB (initial data, no UpdateBuffer).
                _device.ClearColorBuffer(0f, 1f, 0f, 1f);
                _device.SetVertexBuffer(0u, _blitVbStatic, 16u);
                _device.DrawIndexed(6u);
                DumpD3DTex(_rtRT, "RT-C-blit-static");

                // Restore backbuffer OM state.
                _device.SetFramebuffer(null);
                _device.Viewport = new Rectangle(0, 0, w, h);
            }
            _device.Present(1);
            return;
        }

        // ---- DIAG 1/4: gradient straight to the backbuffer (proves the blit
        // geometry / rasterizer path without touching the RT or user shader).
        // Diag 4 additionally reads pixels back and dumps the D3D info queue.
        if (Program.DiagMode is 1 or 4)
        {
            _device.SetFramebuffer(null);
            _device.Viewport = new Rectangle(0, 0, w, h);
            _device.ClearColorBuffer(0f, 0f, 0f, 1f);

            VPosUv[] gv =
            {
                new(new Vector2( 1f,  1f), new Vector2(1f, 0f)), // top-right
                new(new Vector2( 1f, -1f), new Vector2(1f, 1f)), // bottom-right
                new(new Vector2(-1f, -1f), new Vector2(0f, 1f)), // bottom-left
                new(new Vector2(-1f,  1f), new Vector2(0f, 0f)), // top-left
            };
            _device.UpdateBuffer(_blitVb, 0, gv);
            _device.SetShader(_blitShader);
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_blitLayout);
            _device.SetVertexBuffer(0u, _blitVb, 16u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);

            _diagFrames++;
            if (!_diagDone && _diagFrames >= 5)
            {
                _diagDone = true;
                DumpBackbufferPixels();
            }

            _device.Present(1);
            return;
        }

        // ---- Single-pass direct rendering (crop-to-fill).
        // The shader renders at THIS monitor's native resolution and fills it
        // edge-to-edge - uv 0..1 spans the whole screen, exactly like
        // ShaderRunner fullscreen. No shared max-resolution canvas, no
        // letterbox bars. (The old master-canvas + slice blit is only used by
        // --diag modes now.)
        _device.SetFramebuffer(null);
        _device.Viewport = new Rectangle(0, 0, w, h);
        Vector4 resolutionTime = new Vector4(w, h, 1f, time);
        _device.UpdateBuffer(_uniform, 0, resolutionTime);

        if (Program.DiagMode == 3 && _frameLogs < 10)
        {
            _frameLogs++;
            Log.W($"DIAG3 frame {_frameLogs} client={w}x{h}: uniform={resolutionTime} (direct single-pass)");
        }

        _device.SetShader(_mainShader);
        _device.SetUniformBuffer(0u, _uniform);
        _device.SetDepthStencilState(_depthDisabled);
        _device.SetPrimitiveType(PrimitiveType.TriangleList);
        _device.SetInputLayout(_mainLayout);
        _device.SetVertexBuffer(0u, _quadVb, 8u);
        _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
        _device.DrawIndexed(6u);

        _device.Present(1); // VSYNC - Present(0)+AllowTearing silently fails on some
                            // drivers (Pie ignores the DXGI result), leaving black screens.
                            // ShaderRunner always used Present(1) and always displayed.
    }

    // Builds the blit quad vertices for the current client size and letterbox
    // layout. Winding MUST be clockwise (D3D11 default rasterizer treats CW as
    // front and culls the rest). Vertex order mirrors the proven fullscreen
    // quad in ShaderRunner: TR, BR, BL, TL. Top of the quad samples v0 (top
    // row of the RT) - no vertical flip. Clip space has +Y up (D3D11).
    private VPosUv[] ComputeBlitVerts()
    {
        float w = ClientSize.Width, h = ClientSize.Height;
        float mx = _offsetInGroup.X, my = _offsetInGroup.Y;

        float ix0 = Math.Max(_letterbox.X, mx);
        float iy0 = Math.Max(_letterbox.Y, my);
        float ix1 = Math.Min(_letterbox.X + _fitted.Width, mx + w);
        float iy1 = Math.Min(_letterbox.Y + _fitted.Height, my + h);

        if (!(ix0 < ix1 && iy0 < iy1))
            ix0 = iy0 = ix1 = iy1 = 0; // no visible slice; Pass B skips the draw

        float px = ix0 - mx, py = iy0 - my;
        float pw = ix1 - ix0, ph = iy1 - iy0;

        float u0 = (ix0 - _letterbox.X) / _fitted.Width, v0 = (iy0 - _letterbox.Y) / _fitted.Height;
        float u1 = (ix1 - _letterbox.X) / _fitted.Width, v1 = (iy1 - _letterbox.Y) / _fitted.Height;

        float cx0 = px / w * 2f - 1f, cx1 = (px + pw) / w * 2f - 1f;
        float cy0 = 1f - py / h * 2f, cy1 = 1f - (py + ph) / h * 2f;

        return new VPosUv[4]
        {
            new(new Vector2(cx1, cy0), new Vector2(u1, v0)), // top-right
            new(new Vector2(cx1, cy1), new Vector2(u1, v1)), // bottom-right
            new(new Vector2(cx0, cy1), new Vector2(u0, v1)), // bottom-left
            new(new Vector2(cx0, cy0), new Vector2(u0, v0)), // top-left
        };
    }

    // ---- DIAG 4 helpers ------------------------------------------------------

    private unsafe void DumpBackbufferPixels()
    {
        try
        {
            var ctx = (D3D.ID3D11DeviceContext) _device.GetType().GetField("_context",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_device);

            ctx.CopyResource(_stagingTex, _backTex);

            D3D.MappedSubresource map = ctx.Map(_stagingTex, 0, D3D.MapMode.Read, D3D.MapFlags.None);
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
            ctx.Unmap(_stagingTex, 0);
        }
        catch (Exception ex) { Log.Ex("DIAG4 readback", ex); }
    }

    // Generic readback: copies any D3D texture to a fresh staging texture and
    // logs sample pixels.
    private unsafe void DumpD3DTex(D3D.ID3D11Texture2D tex, string label)
    {
        try
        {
            var ctx = (D3D.ID3D11DeviceContext) _device.GetType().GetField("_context",
                BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_device);
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

    private void RequestShutdown()
    {
        if (AllowInputExit) ShutdownRequested = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_lastMouseX >= 0 && (Math.Abs(e.X - _lastMouseX) > 4 || Math.Abs(e.Y - _lastMouseY) > 4))
            RequestShutdown();
        _lastMouseX = e.X; _lastMouseY = e.Y;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e) { RequestShutdown(); base.OnMouseDown(e); }
    protected override void OnKeyDown(KeyEventArgs e) { RequestShutdown(); base.OnKeyDown(e); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Black, never the default light background (white flash report).
        e.Graphics.Clear(Color.Black);
    }

    private const int WM_DISPLAYCHANGE = 0x007E; // display mode / topology changed

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DISPLAYCHANGE)
        {
            int w = (short) ((long) m.LParam & 0xFFFF);
            int h = (short) (((long) m.LParam >> 16) & 0xFFFF);
            Log.W($"WndProc: WM_DISPLAYCHANGE bpp={m.WParam.ToInt64()} new={w}x{h} " +
                  "(a monitor retrain / resolution change happened right now)");
        }
        base.WndProc(ref m);
    }

    private int _lastLoggedW = -1, _lastLoggedH = -1;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Log real size changes only - a spurious resize that coincides with
        // a monitor blink is a valuable diagnostic clue.
        if (ClientSize.Width != _lastLoggedW || ClientSize.Height != _lastLoggedH)
        {
            _lastLoggedW = ClientSize.Width; _lastLoggedH = ClientSize.Height;
            Log.W($"OnResize: client now {ClientSize.Width}x{ClientSize.Height}");
        }
        try
        {
            _device?.ResizeSwapchain(new Size(ClientSize.Width, ClientSize.Height));
            // Rebuild the static blit VB for the new client geometry.
            if (_device != null && _blitVb != null)
            {
                _blitVb.Dispose();
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer, ComputeBlitVerts());
                Log.W($"OnResize: blit VB rebuilt for {ClientSize.Width}x{ClientSize.Height}");
            }
        }
        catch (Exception ex) { Log.Ex("OnResize/ResizeSwapchain", ex); }
    }
}
