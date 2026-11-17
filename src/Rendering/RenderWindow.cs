namespace WinGLSLScrSaver;

using System.Numerics;
using System.Reflection;
using Pie;
using Pie.ShaderCompiler;
using D3D = Vortice.Direct3D11;
using DXGI = Vortice.DXGI;

internal partial class RenderWindow : Form
{
    public readonly bool IsPreview;
    public readonly bool AllowInputExit;

    private readonly Size _master;          // master render resolution
    private readonly Rectangle _groupBbox;  // group bounding box (virtual canvas)
    private readonly Point _offsetInGroup;  // this monitor's offset in the group

    private readonly SizeF _fitted;         // fitted image size in group canvas
    private readonly PointF _letterbox;     // fitted image offset in group canvas

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
    private int _rtW, _rtH;
    private bool _disposed;

    // ---- Shared canvas architecture (normal runs, diag=0): window 0 hosts
    // the Pie device and renders the shader ONCE per frame into a master-size
    // canvas RT; every window blits its crop-to-fit slice on that device.
    internal bool IsCanvasOwner;
    private bool _isRawWindow;
    private DXGI.IDXGISwapChain1 _rawSwap;
    private IntPtr _frameLatencyWaitable;   // DXGI 1.3 frame latency waitable
    private D3D.ID3D11Buffer _rawVb;
    private int _rawBuiltW, _rawBuiltH;

    internal object PieDevice => _device;
    internal object CanvasTexture => _rt;
    internal int CanvasW => _rtW;
    internal int CanvasH => _rtH;
    internal float ScaleBlend => _scaleBlend;
    internal DXGI.IDXGISwapChain1 RawSwap => _rawSwap;
    // Raw windows blit into their own swapchain's backbuffer. Flip model:
    // CanvasBlit.Draw fetches the current back buffer (GetBuffer(0)) and
    // creates its RTV per frame - no cached RTV is possible.
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

    // Fullscreen blit quad (TR, BR, BL, TL) - shared by the diag-6 control
    // buffer, the scaled-path static blit quad and the diag 1/4/6 fills.
    private static readonly VPosUv[] FullscreenQuad = new VPosUv[]
    {
        new(new Vector2( 1f,  1f), new Vector2(1f, 0f)), // top-right
        new(new Vector2( 1f, -1f), new Vector2(1f, 1f)), // bottom-right
        new(new Vector2(-1f, -1f), new Vector2(0f, 1f)), // bottom-left
        new(new Vector2(-1f,  1f), new Vector2(0f, 0f)), // top-left
    };

    private double _lastMouseY = -1, _lastMouseX = -1;
    private int _frameLogs;

    // DIAG 4 state.
    private object _d3dDevice;
    private D3D.ID3D11Texture2D _backTex;
    private D3D.ID3D11Texture2D _rtRT;
    private int _diagFrames;
    private bool _diagDone;

    public bool ShutdownRequested { get; private set; }

    // 1 = present with vsync (auto mode); 0 = present without blocking (FPS
    // cap set - the loop is paced by the cap). Set by RunLoop before the loop.
    public int PresentInterval = 1;

    // ---- Per-monitor present pacing (fullscreen canvas runs) ---------------
    internal double RefreshHz = 60.0;   // this monitor's actual refresh rate
    internal bool PresentGating;        // set by RunLoop for fullscreen canvas runs
    private double _nextPresentDue;     // due time in loop-stopwatch seconds
    public long PresentFrames;          // presents actually handed to DWM
    private bool _dwnLogged;            // WasStillDrawing warning (once)

    // ---- Present thread ----------------------------------------------------
    // Raw Present(0) costs ~one DWM composition period even with DO_NOT_WAIT;
    // running that wait on a dedicated thread takes it out of the frame
    // budget. Only swapchain presents run there - the device context stays
    // on the main thread (D3D11 immediate context is not thread-safe).
    internal bool UsePresentThread;
    // True while the present thread is inside a swapchain operation on this
    // window. The main thread must not touch the same swapchain concurrently:
    // GetBuffer / RTV creation / ResizeBuffers during an in-flight Present is
    // driver-level UB (the ~1-in-5 launch AV with present threads on).
    private volatile bool _presentInFlight;
    internal bool PresentInFlight => _presentInFlight;
    private Thread _presentThread;
    private readonly AutoResetEvent _presentSignal = new(false);
    private readonly ManualResetEvent _presentExit = new(false);
    private volatile float _presentThreadTime;
    private volatile bool _presentQueued;

    // ---- Present lane (present_lanes=2) ------------------------------------
    // This window's swapchain lives on a SECOND D3D11 device (a fully
    // parallel present lane); the canvas crosses devices via a keyed-mutex
    // shared texture.
    internal bool UseLane;
    private DXGI.IDXGISwapChain1 _laneSwap;
    private IntPtr _laneWaitable;
    private D3D.ID3D11Buffer _laneVb;
    private int _laneBuiltW, _laneBuiltH;
    internal DXGI.IDXGISwapChain1 LaneSwap => _laneSwap;
    internal D3D.ID3D11Buffer LaneVb => _laneVb;

    internal void StartPresentThread()
    {
        if (_presentThread != null) return;
        _presentThread = new Thread(PresentThreadLoop)
        {
            IsBackground = true,
            Name = "present-thread",
        };
        _presentThread.Start();
        Log.W("Present thread started");
    }

    internal void ShutdownPresentThread(int joinMs)
    {
        var t = _presentThread;
        if (t == null) return;
        _presentThread = null;
        _presentExit.Set();
        _presentSignal.Set();
        if (!t.Join(joinMs))
        {
            Log.W("Present thread did not stop in time (aborting join)");
            return;
        }
        _presentExit.Reset();
    }

    // Hand the present off. Coalescing: if the thread is still busy with the
    // previous present, the event just latches (natural backpressure).
    private void RequestPresent(float time)
    {
        _presentThreadTime = time;
        _presentQueued = true;
        _presentSignal.Set();
    }

    private void PresentThreadLoop()
    {
        WaitHandle[] handles = { _presentExit, _presentSignal };
        while (_presentThread != null)
        {
            int which = WaitHandle.WaitAny(handles);
            if (which == 0 || _presentThread == null) return;
            if (_presentQueued)
            {
                _presentQueued = false;
                try { PresentCore(_presentThreadTime); }
                catch (Exception ex) { Log.Ex("PresentThread", ex); }
            }
        }
    }

    private const int DXGI_PRESENT_DO_NOT_WAIT = 0x00000008;
    private const int DXGI_PRESENT_ALLOW_TEARING = 0x00000200;
    private const int DXGI_ERROR_WAS_STILL_DRAWING = unchecked((int) 0x887A000A);

    // Per-frame timing probes (read+reset by RunLoop's FPS log).
    internal static double OwnerPresentTotalMs;
    internal static double RawPresentTotalMs;
    internal static long RawWasStillCount;
    internal static double RawPresentMinMs = double.MaxValue;
    internal static double RawPresentMaxMs;
    internal static double OwnerPresentMinMs = double.MaxValue;
    internal static double OwnerPresentMaxMs;
    // Waitable wait times: ~0ms when the queue drains; sustained >0 = queue-limited.
    internal static double FrameLatencyWaitTotalMs;
    internal static double FrameLatencyWaitMaxMs;
    internal static long FrameLatencyWaitCount;
    internal static int RawPresentFlagMode = 2;

    // show_fps=1: per-window fps overlay counting ACTUAL presented frames
    // (post gating), not render ticks.
    private Label _fpsLabel;
    private long _fpsLabelFrames;
    private double _fpsLabelT;

    // tear_fix_child=1 (with show_fps off): a fully transparent child window
    // over the flip swapchain - the same DWM-composition hack as the fps
    // label, but invisible (layered child with alpha 0).
    private Control _tearFixChild;

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

        // Never paint white: any window surface exposed before the first GPU
        // frame must be black (white startup flash report).
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);

        // Hide the cursor on EVERY window, not just the canvas owner: raw
        // windows 1..N never run InitGraphics. Child controls (fps label,
        // tear-fix child) inherit the parent cursor via Control.Cursor.
        if (!isPreview) Cursor = HiddenCursor();

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

            // Debug=true is FORBIDDEN - Pie 0.10.1's DebugGraphicsDevice throws
            // SEHException natively on SetDepthStencilState here.
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

            // Blit fragment with the interpolation blend BAKED IN (a spec
            // constant would have to survive shaderc -> SPIRV-Cross -> HLSL).
            // Formatted with "0.0###": GLSL forbids a bare "0f"/"1f" literal.
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

            // RT / blit machinery: diag modes AND the scaled path. At
            // res_mult == 1.0 with no diag the normal path renders straight
            // to the backbuffer.
            if (!Program.CanvasPipeline || IsCanvasOwner)
            {
                // Normal runs: the blit VB holds this window's canvas-slice
                // quad; diag modes: the fullscreen letterbox blit quad.
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer,
                    Program.CanvasPipeline ? ComputeCanvasVerts() : ComputeBlitVerts());
                _blitVbStatic = _device.CreateBuffer(BufferType.VertexBuffer, FullscreenQuad);

                // RT sizes: diag 5/6 need the master-size RT for readbacks;
                // other diag modes use client x mult; a normal run uses the
                // SHARED CANVAS at master size x mult.
                int rtW, rtH;
                if (Program.DiagMode is 5 or 6) { rtW = _master.Width; rtH = _master.Height; }
                else if (Program.CanvasPipeline)
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
                _blitQuadStatic = _device.CreateBuffer(BufferType.VertexBuffer, FullscreenQuad);
                Log.W($"InitGraphics: {(Program.CanvasPipeline ? "canvas" : "scaled path")} RT {rtW}x{rtH} (master={_master.Width}x{_master.Height}, mult={_resMult:0.###}, blend={_scaleBlend:0.###})");
            }

            // (Cursor hiding moved to the constructor: raw windows 1..N never
            // reach InitGraphics.)

            // Normal runs: this window hosts the shared device.
            if (Program.CanvasPipeline && IsCanvasOwner)
                CanvasBlit.EnsureShared(this);

            Log.W("InitGraphics: all GPU resources created OK");

            // ---- DIAG 1/4/5/6: grab raw D3D11 objects for pixel readback.
            if (Program.DiagMode is 1 or 4 or 5 or 6)
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
            _rawSwap?.Dispose(); _rawVb?.Dispose();
            _laneSwap?.Dispose(); _laneVb?.Dispose(); _laneWaitable = IntPtr.Zero;
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
        if (Program.CanvasPipeline)
        {
            // Shared canvas path: DRAW phase only (presents happen in Present()).
            CanvasDraw(time);
            return;
        }
        if (_device == null) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        // ---- DIAG 5: (a) gradient into the RT + readback, (b) magenta clear
        // on the backbuffer, no draw, + readback. Decides: broken draw
        // pipeline vs broken swapchain/present path.
        if (Program.DiagMode == 5)
        {
            _device.SetFramebuffer(_fb);
            _device.Viewport = new Rectangle(0, 0, _master.Width, _master.Height);
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

            _diagFrames++;
            if (!_diagDone && _diagFrames >= 5)
            {
                _diagDone = true;
                DumpD3DTex(_rtRT, "RT");

                _device.SetFramebuffer(null);
                _device.Viewport = new Rectangle(0, 0, w, h);
                _device.ClearColorBuffer(1f, 0f, 1f, 1f); // MAGENTA
                DumpD3DTex(_backTex, "BACKBUFFER");
            }

            _device.Present(1);
            return;
        }

        // ---- DIAG 6: isolate the broken blit component. A=main path (control),
        // B=blit with DYNAMIC VB via UpdateBuffer, C=blit with STATIC VB.
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
                VPosUv[] gv = FullscreenQuad;
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

                _device.SetFramebuffer(null);
                _device.Viewport = new Rectangle(0, 0, w, h);
            }
            _device.Present(1);
            return;
        }

        // ---- DIAG 1/4: gradient straight to the backbuffer (4 adds readback).
        if (Program.DiagMode is 1 or 4)
        {
            _device.SetFramebuffer(null);
            _device.Viewport = new Rectangle(0, 0, w, h);
            _device.ClearColorBuffer(0f, 0f, 0f, 1f);

            VPosUv[] gv = FullscreenQuad;
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

        // ---- Scaled path (res_mult < 1.0): Pass A renders into the small RT,
        // Pass B upscales to the backbuffer with the blended sampler. Both
        // VBs are STATIC - Pie 0.10.1's UpdateBuffer on dynamic VBs fails.
        if (ScaledPath)
        {
            int rtW = _rtW, rtH = _rtH;

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

        // ---- Single-pass direct rendering (res_mult == 1.0): the shader
        // renders at THIS monitor's native resolution, uv 0..1 spans the
        // whole screen.
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

        _device.Present(PresentInterval); // auto mode = 1 (vsync): Present(0)+
                            // AllowTearing silently fails on some drivers
                            // (Pie ignores the DXGI result), leaving black
                            // screens. With an FPS cap the interval is 0.
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
        e.Graphics.Clear(Color.Black); // never the default light background
    }

    private const int WM_DISPLAYCHANGE = 0x007E;

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
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer,
                    Program.CanvasPipeline ? ComputeCanvasVerts() : ComputeBlitVerts());
                Log.W($"OnResize: blit VB rebuilt for {ClientSize.Width}x{ClientSize.Height}");
            }
            // Scaled path only: rebuild the RT at client size x multiplier.
            // The shared canvas RT is master sized and must NOT follow resizes.
            if (_device != null && _rt != null && _resMult < 1f && !Program.CanvasPipeline)
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
