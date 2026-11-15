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
using DXGI = Vortice.DXGI;

internal class RenderWindow : Form
{
    public readonly bool IsPreview;
    public readonly bool AllowInputExit;

    private readonly Size _master;          // master render resolution
    private readonly Rectangle _groupBbox;  // group bounding box (virtual canvas)
    private readonly Point _offsetInGroup;  // this monitor's offset in the group

    private readonly SizeF _fitted;         // fitted image size in group canvas
    private readonly PointF _letterbox;     // fitted image offset in group canvas

    // Scaled-path settings (Program-level config, per-window copy).
    private readonly float _resMult;        // 0.05..1.0; < 1 = render small, upscale
    private readonly float _scaleBlend;     // 0 = nearest, 1 = bilinear, between = mix
    private bool ScaledPath => _resMult < 1f && _rt != null;

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
    private SamplerState _pointSampler;   // scaled path: nearest neighbour
    private GraphicsBuffer _blitQuadStatic; // scaled path: static fullscreen blit quad
    private int _rtW, _rtH;               // scaled path RT size (Texture has no size query)
    private bool _disposed;

    // ---- Shared canvas architecture (normal runs, diag=0): window 0 hosts
    // the Pie device and renders the shader ONCE per frame into a master-size
    // canvas RT; every window (window 0 included) blits its crop-to-fit slice
    // with raw D3D11 calls on that same device (CanvasBlit).
    internal bool IsCanvasOwner;
    private bool _isRawWindow;
    private DXGI.IDXGISwapChain1 _rawSwap;
    private D3D.ID3D11RenderTargetView _rawRtv;
    private D3D.ID3D11Buffer _rawVb;
    private int _rawBuiltW, _rawBuiltH;

    internal object PieDevice => _device;
    internal object CanvasTexture => _rt;
    internal int CanvasW => _rtW;
    internal int CanvasH => _rtH;
    internal float ScaleBlend => _scaleBlend;
    internal DXGI.IDXGISwapChain1 RawSwap => _rawSwap;
    // Raw windows blit into their own swapchain's backbuffer. The owner never
    // does a raw blit - it blits its slice through Pie (framebuffer null).
    internal D3D.ID3D11RenderTargetView RawRtv => _rawRtv;
    internal D3D.ID3D11Buffer RawVb => _rawVb;
    internal int RawW => ClientSize.Width;
    internal int RawH => ClientSize.Height;
    internal bool SliceEmpty => _sliceEmpty;
    private bool _sliceEmpty;

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

    // 1 = present with vsync (auto mode, today's behavior); 0 = present
    // immediately without blocking (used when an FPS cap is set, so the
    // render loop is paced purely by the cap - monitors sample the latest
    // buffer at their own refresh). Set by RunLoop before the loop starts.
    public int PresentInterval = 1;

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
                        bool isPreview, bool inputExit,
                        float resMult = 1f, float scaleBlend = 0.5f)
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
        _resMult = Math.Clamp(resMult, AppSettings.MinResMult, AppSettings.MaxResMult);
        _scaleBlend = Math.Clamp(scaleBlend, 0f, 1f);

        float scale = Math.Min((float) groupBbox.Width / master.Width, (float) groupBbox.Height / master.Height);
        _fitted = new SizeF(master.Width * scale, master.Height * scale);
        _letterbox = new PointF((groupBbox.Width - _fitted.Width) / 2f, (groupBbox.Height - _fitted.Height) / 2f);

        Log.W($"RenderWindow created: bounds={bounds} master={master} groupBbox={groupBbox} " +
              $"offset={offsetInGroup} fitted={_fitted} letterbox={_letterbox} preview={isPreview} " +
              $"resMult={_resMult:0.###} scaleBlend={_scaleBlend:0.###}");
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

            // Blit fragment with the interpolation blend BAKED IN as a compile
            // constant (the setting is fixed for the whole run, and a spec
            // constant would have to survive shaderc -> SPIRV-Cross -> HLSL -
            // not worth the risk). 0 = pure nearest neighbour, 1 = pure
            // bilinear; between = per-pixel weighted mix of the two.
            // NOTE: the blend is formatted with "0.0###" (not "0.####") - GLSL
            // forbids a bare "0f"/"1f"; a float literal needs a decimal point
            // or exponent, so 0 and 1 must come out as 0.0 / 1.0.
            string BlitFragment = $@"
#version 450
layout (location = 0) in vec2 vUv;
layout (location = 0) out vec4 oColor;
layout (binding = 0) uniform sampler2D uTexLinear;  // bilinear filtered
layout (binding = 1) uniform sampler2D uTexPoint;   // nearest neighbour
void main()
{{
    vec4 lin = texture(uTexLinear, vUv);
    vec4 nn  = texture(uTexPoint, vUv);
    oColor = mix(nn, lin, {_scaleBlend.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)}f);
}}";

            const string GradientBlitFragment = @"
#version 450
layout (location = 0) in vec2 vUv;
layout (location = 0) out vec4 oColor;
void main()
{
    oColor = vec4(vUv.x, vUv.y, 0.2, 1.0);
}";
            string blitFrag = Program.DiagMode is 1 or 4 or 6 ? GradientBlitFragment : BlitFragment;
            Log.W($"InitGraphics: blit shader = {(Program.DiagMode is 1 or 4 or 6 ? "GRADIENT (diag1/4/6)" : $"texture blit (blend={_scaleBlend:0.###})")}");

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

            // RT / blit machinery is needed by --diag modes AND by the scaled
            // path (res_mult < 1.0: shader renders into a smaller RT that is
            // upscaled to the screen by the blit pass). At res_mult == 1.0
            // with no diag the normal path renders straight to the backbuffer.
            if (Program.DiagMode is not (0 or 7) || IsCanvasOwner)
            {
                // Normal runs: the blit VB holds this window's canvas-slice
                // quad (crop-to-fit UV window into the shared canvas).
                // Diag modes: the fullscreen letterbox blit quad.
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer,
                    Program.DiagMode is 0 or 7 ? ComputeCanvasVerts() : ComputeBlitVerts());
                // DIAG 6: static control buffer (kept for repro runs).
                _blitVbStatic = _device.CreateBuffer(BufferType.VertexBuffer, new VPosUv[4]
                {
                    new(new Vector2( 1f,  1f), new Vector2(1f, 0f)),
                    new(new Vector2( 1f, -1f), new Vector2(1f, 1f)),
                    new(new Vector2(-1f, -1f), new Vector2(0f, 1f)),
                    new(new Vector2(-1f,  1f), new Vector2(0f, 0f)),
                });

                // RT sizes: diag 5/6 need the master-size RT for their pixel
                // readbacks; other diag modes use client x mult; a normal run
                // (diag=0, owner window) uses the SHARED CANVAS at master size
                // x mult - the shader renders into it once per frame.
                int rtW, rtH;
                if (Program.DiagMode is 5 or 6) { rtW = _master.Width; rtH = _master.Height; }
                else if (Program.DiagMode is 0 or 7)
                {
                    rtW = Math.Max(1, (int) Math.Round(_master.Width * _resMult));
                    rtH = Math.Max(1, (int) Math.Round(_master.Height * _resMult));
                }
                else
                {
                    rtW = Math.Max(1, (int) Math.Round(ClientSize.Width * _resMult));
                    rtH = Math.Max(1, (int) Math.Round(ClientSize.Height * _resMult));
                }
                _rtW = rtW; _rtH = rtH;
                _rt = _device.CreateTexture(TextureDescription.Texture2D(rtW, rtH,
                    Format.R8G8B8A8_UNorm, 1, 1, TextureUsage.Framebuffer | TextureUsage.ShaderResource));
                _fb = _device.CreateFramebuffer(new FramebufferAttachment(_rt));
                _sampler = _device.CreateSamplerState(SamplerStateDescription.LinearClamp);
                _pointSampler = _device.CreateSamplerState(SamplerStateDescription.PointClamp);
                _blitQuadStatic = _device.CreateBuffer(BufferType.VertexBuffer, new VPosUv[4]
                {
                    new(new Vector2( 1f,  1f), new Vector2(1f, 0f)), // top-right
                    new(new Vector2( 1f, -1f), new Vector2(1f, 1f)), // bottom-right
                    new(new Vector2(-1f, -1f), new Vector2(0f, 1f)), // bottom-left
                    new(new Vector2(-1f,  1f), new Vector2(0f, 0f)), // top-left
                });
                Log.W($"InitGraphics: {(Program.DiagMode is 0 or 7 ? "canvas" : "scaled path")} RT {rtW}x{rtH} (master={_master.Width}x{_master.Height}, mult={_resMult:0.###}, blend={_scaleBlend:0.###})");
            }

            // Hide the cursor only after everything initialized (fullscreen).
            Cursor = IsPreview ? Cursors.Default : HiddenCursor();

            // Normal runs: this window hosts the shared device - set up the
            // canvas wrap + raw blit that ALL windows (itself included) use.
            if (Program.DiagMode is 0 or 7 && IsCanvasOwner)
                CanvasBlit.EnsureShared(this);

            Log.W("InitGraphics: all GPU resources created OK");

            // ---- DIAG 4/5/6/7: grab raw D3D11 objects for pixel readback.
            if (Program.DiagMode is 4 or 5 or 6 or 7)
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
            _rt?.Dispose(); _fb?.Dispose(); _sampler?.Dispose(); _pointSampler?.Dispose();
            _blitVb?.Dispose(); _blitVbStatic?.Dispose(); _blitQuadStatic?.Dispose();
            _rawSwap?.Dispose(); _rawRtv?.Dispose(); _rawVb?.Dispose();
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
        if (_disposed) return;
        if (Program.DiagMode is 0 or 7)
        {
            // Shared canvas path: raw windows have no Pie device of their own,
            // so this branch runs BEFORE the _device guard below.
            CanvasFrame(time);
            return;
        }
        if (_device == null) return;

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

        // ---- Scaled path (res_mult < 1.0): Pass A renders the shader into the
        // small RT, Pass B upscales it to the backbuffer with the blended
        // sampler (nearest <-> bilinear). Both vertex buffers are STATIC -
        // Pie 0.10.1's UpdateBuffer on dynamic VBs silently fails.
        if (ScaledPath)
        {
            int rtW = _rtW, rtH = _rtH;

            // Pass A: shader into RT at the monitor's own aspect ratio.
            _device.SetFramebuffer(_fb);
            _device.Viewport = new Rectangle(0, 0, rtW, rtH);
            _device.UpdateBuffer(_uniform, 0, new Vector4(rtW, rtH, 1f, time));
            _device.SetShader(_mainShader);
            _device.SetUniformBuffer(0u, _uniform);
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_mainLayout);
            _device.SetVertexBuffer(0u, _quadVb, 8u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);

            // Pass B: upscale RT to the screen with both samplers blended.
            _device.SetFramebuffer(null);
            _device.Viewport = new Rectangle(0, 0, w, h);
            _device.SetShader(_blitShader);
            _device.SetTexture(0u, _rt, _sampler);        // bilinear
            _device.SetTexture(1u, _rt, _pointSampler);   // nearest neighbour
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_blitLayout);
            _device.SetVertexBuffer(0u, _blitQuadStatic, 16u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);

            _device.Present(PresentInterval);
            return;
        }

        // ---- Single-pass direct rendering (crop-to-fill, res_mult == 1.0).
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

        _device.Present(PresentInterval); // Auto mode = 1 (vsync): Present(0)+AllowTearing
                            // silently fails on some drivers (Pie ignores the DXGI
                            // result), leaving black screens. With an FPS cap set the
                            // interval is 0 and pacing is the loop's job - tearing is
                            // accepted there by design.
    }

    // ---- Shared canvas path (normal runs, diag=0) -------------------------

    // Raw window (windows 1..N in a normal run): no Pie device of its own -
    // a plain DXGI swapchain on the owner's shared device, blitting its
    // slice of the owner's canvas.
    public bool InitRawGraphics(RenderWindow owner)
    {
        try
        {
            _isRawWindow = true;
            CanvasBlit.EnsureShared(owner);
            Log.W($"Raw window initialized: bounds={Bounds} (shared device, canvas slice blit)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Ex("InitRawGraphics", ex);
            return false;
        }
    }

    // One normal-run frame: the owner renders the shader into the shared
    // canvas ONCE, then every window blits its crop-to-fit slice and
    // presents. Canvas iResolution = canvas size (master x mult).
    private void CanvasFrame(float time)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        if (IsCanvasOwner && _device != null)
        {
            // Pass A: the shader renders ONCE per frame into the shared
            // canvas (master size x resMult). iResolution = canvas size.
            _device.SetFramebuffer(_fb);
            _device.Viewport = new Rectangle(0, 0, _rtW, _rtH);
            _device.UpdateBuffer(_uniform, 0, new Vector4(_rtW, _rtH, 1f, time));
            _device.SetShader(_mainShader);
            _device.SetUniformBuffer(0u, _uniform);
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_mainLayout);
            _device.SetVertexBuffer(0u, _quadVb, 8u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);

            // Pass B: this window's slice blit - done through Pie itself
            // (framebuffer null = Pie's own current backbuffer), so Pie keeps
            // handling buffer rotation and resize. Never cache an RTV over
            // the swapchain: the backbuffer is recreated on resize and the
            // first version of this blit distorted exactly that way.
            _device.SetFramebuffer(null);
            _device.Viewport = new Rectangle(0, 0, w, h);
            _device.SetShader(_blitShader);
            _device.SetTexture(0u, _rt, _sampler);
            _device.SetTexture(1u, _rt, _pointSampler);
            _device.SetDepthStencilState(_depthDisabled);
            _device.SetPrimitiveType(PrimitiveType.TriangleList);
            _device.SetInputLayout(_blitLayout);
            _device.SetVertexBuffer(0u, _blitVb, 16u);
            _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
            _device.DrawIndexed(6u);

            Diag7Dump();
            _device.Present(PresentInterval);
            return;
        }

        EnsureRawTarget();
        CanvasBlit.Draw(this);
        Diag7Dump();
        _rawSwap.Present(PresentInterval, 0);
    }

    // DIAG 7: once per window, read the actual backbuffer back and dump it
    // as a base64 JPEG next to the log. Proves the real per-monitor output
    // (crop-to-fit geometry) without screen capture, which Defender blocks.
    private void Diag7Dump()
    {
        if (Program.DiagMode != 7) return;
        _diagFrames++;
        if (_diagDone || _diagFrames < 30) return;
        _diagDone = true;
        try
        {
            D3D.ID3D11Texture2D tex;
            if (_isRawWindow)
            {
                tex = _rawSwap.GetBuffer<D3D.ID3D11Texture2D>(0);
            }
            else
            {
                // Owner: Pie's swapchain backbuffer, grabbed in InitGraphics.
                if (_backTex == null) { Log.W("DIAG7: no backtex"); return; }
                tex = _backTex;
            }
            DumpTextureJpeg(tex, $"{ClientSize.Width}x{ClientSize.Height}");
            if (_isRawWindow) tex.Dispose();
        }
        catch (Exception ex) { Log.Ex("DIAG7 dump", ex); }
    }

    // Copies a D3D texture into a staging texture, converts it to a Bitmap,
    // downscales to 50% and writes a base64 JPEG into the app data folder.
    private unsafe void DumpTextureJpeg(D3D.ID3D11Texture2D tex, string tag)
    {
        var dev = CanvasBlit.Dev;
        var ctx = CanvasBlit.Ctx;
        var desc = tex.Description;
        var sdesc = desc;
        sdesc.Usage = D3D.ResourceUsage.Staging;
        sdesc.BindFlags = D3D.BindFlags.None;
        sdesc.CPUAccessFlags = D3D.CpuAccessFlags.Read;
        sdesc.MiscFlags = D3D.ResourceOptionFlags.None;
        using D3D.ID3D11Texture2D staging = dev.CreateTexture2D(sdesc);
        ctx.CopyResource(staging, tex);

        int w = desc.Width, h = desc.Height, bpp = 4;
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        D3D.MappedSubresource map = ctx.Map(staging, 0, D3D.MapMode.Read, D3D.MapFlags.None);
        try
        {
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                // B8G8R8A8 bytes map 1:1 onto GDI+ Format32bppArgb (little
                // endian ARGB), except for the stride padding between rows.
                for (int y = 0; y < h; y++)
                    Marshal.Copy(new ReadOnlySpan<byte>((byte*) map.DataPointer + y * (long) map.RowPitch, w * bpp).ToArray(),
                        0, IntPtr.Add(bd.Scan0, y * bd.Stride), w * bpp);
            }
            finally { bmp.UnlockBits(bd); }
        }
        finally { ctx.Unmap(staging, 0); }

        int dw = Math.Max(1, w / 2), dh = Math.Max(1, h / 2);
        using var small = new Bitmap(dw, dh);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, 0, 0, dw, dh);
        }
        using var ms = new MemoryStream();
        var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.MimeType == "image/jpeg");
        var ep = new System.Drawing.Imaging.EncoderParameters(1);
        ep.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, 60L);
        small.Save(ms, enc, ep);
        string b64 = Convert.ToBase64String(ms.ToArray());
        string path = Path.Combine(Settings.AppDataDir, $"diag7_{tag}.b64");
        File.WriteAllText(path, b64);
        bmp.Dispose();
        Log.W($"DIAG7 dump {tag}: {w}x{h} -> {dw}x{dh} jpeg ({b64.Length} b64 chars) -> {path}");
    }

    // Raw-window swapchain + slice VB management (lazy, after the window
    // settles). The owner has no raw target at all - it blits through Pie.
    private void EnsureRawTarget()
    {
        if (!_isRawWindow) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        if (_rawVb != null && _rawBuiltW == w && _rawBuiltH == h) return;

        if (_rawSwap == null)
        {
            _rawSwap = CanvasBlit.CreateSwapchain(Handle, w, h);
            _rawRtv = CanvasBlit.CreateBackbufferRtv(_rawSwap);
            Log.W($"Raw swapchain created: {w}x{h}");
        }
        else if (_rawBuiltW != w || _rawBuiltH != h)
        {
            _rawRtv?.Dispose();
            _rawSwap.ResizeBuffers(1, w, h, DXGI.Format.B8G8R8A8_UNorm, DXGI.SwapChainFlags.None);
            _rawRtv = CanvasBlit.CreateBackbufferRtv(_rawSwap);
            Log.W($"Raw swapchain resized: {w}x{h}");
        }

        _rawVb?.Dispose();
        _rawVb = CanvasBlit.CreateVb(ComputeCanvasVerts());
        _rawBuiltW = w;
        _rawBuiltH = h;
    }

    // Maps this screen's share of the shared canvas: the canvas (master size)
    // is scaled uniformly to COVER the group bbox (centered, overflow
    // cropped) and the screen samples the canvas window it shows. Returns a
    // static quad whose vertices place the visible intersection on screen
    // (TR, BR, BL, TL - CW winding, no vertical flip).
    private VPosUv[] ComputeCanvasVerts()
    {
        float w = ClientSize.Width, h = ClientSize.Height;
        float mx = _offsetInGroup.X, my = _offsetInGroup.Y;
        float gw = _groupBbox.Width, gh = _groupBbox.Height;

        float scale = Math.Max(gw / _master.Width, gh / _master.Height); // cover
        float dw = _master.Width * scale, dh = _master.Height * scale;
        float ox = (gw - dw) / 2f, oy = (gh - dh) / 2f;

        float ix0 = Math.Max(ox, mx), iy0 = Math.Max(oy, my);
        float ix1 = Math.Min(ox + dw, mx + w), iy1 = Math.Min(oy + dh, my + h);
        if (!(ix0 < ix1 && iy0 < iy1))
        {
            _sliceEmpty = true;
            return new VPosUv[4]
            {
                new(new Vector2(), new Vector2()), new(new Vector2(), new Vector2()),
                new(new Vector2(), new Vector2()), new(new Vector2(), new Vector2()),
            };
        }
        _sliceEmpty = false;

        float u0 = (ix0 - ox) / dw, v0 = (iy0 - oy) / dh;
        float u1 = (ix1 - ox) / dw, v1 = (iy1 - oy) / dh;

        float px = ix0 - mx, py = iy0 - my;
        float pw = ix1 - ix0, ph = iy1 - iy0;
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
            // Rebuild the static blit VB for the new client geometry (canvas
            // slice quad in normal runs, fullscreen letterbox quad in diag).
            if (_device != null && _blitVb != null)
            {
                _blitVb.Dispose();
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer,
                    Program.DiagMode is 0 or 7 ? ComputeCanvasVerts() : ComputeBlitVerts());
                Log.W($"OnResize: blit VB rebuilt for {ClientSize.Width}x{ClientSize.Height}");
            }
            // Scaled path only (per-window RTs, diag 3 style): rebuild the RT
            // at client size x multiplier. The shared canvas RT is master
            // sized and must NOT follow window resizes.
            if (_device != null && _rt != null && _resMult < 1f && Program.DiagMode is not (0 or 7))
            {
                int rtW = Math.Max(1, (int) Math.Round(ClientSize.Width * _resMult));
                int rtH = Math.Max(1, (int) Math.Round(ClientSize.Height * _resMult));
                _rt.Dispose();
                _fb.Dispose();
                _rt = _device.CreateTexture(TextureDescription.Texture2D(rtW, rtH,
                    Format.R8G8B8A8_UNorm, 1, 1, TextureUsage.Framebuffer | TextureUsage.ShaderResource));
                _fb = _device.CreateFramebuffer(new FramebufferAttachment(_rt));
                _rtW = rtW; _rtH = rtH;
                Log.W($"OnResize: scaled RT rebuilt {rtW}x{rtH}");
            }
        }
        catch (Exception ex) { Log.Ex("OnResize/ResizeSwapchain", ex); }
    }
}

