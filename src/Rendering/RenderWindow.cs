namespace WinGLSLScrSaver;

using System.Diagnostics;
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
    private List<ShaderTextures.BoundTexture> _userTextures = new(); // R2: shader PNG textures
    private GraphicsBuffer _blitQuadStatic; // scaled path: static fullscreen blit quad
    private int _rtW, _rtH;
    private bool _disposed;

    // Shared canvas (normal runs): window 0 hosts the Pie device and renders
    // the shader once per frame into a master-size canvas RT; every window
    // blits its crop-to-fit slice.
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
    // Raw windows blit into their own flip-model backbuffer: GetBuffer(0)
    // returns the *current* back buffer, so CanvasBlit.Draw creates its RTV
    // per frame - no cached RTV is possible.
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

    // Fullscreen blit quad (TR, BR, BL, TL) - shared by diag 1/4/6 and the
    // scaled path.
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

    // 1 = vsync (auto mode); 0 = unblocked (FPS cap set - the loop paces
    // itself). Set by RunLoop before the loop.
    public int PresentInterval = 1;

    // ---- Per-monitor present pacing (fullscreen canvas runs) ---------------
    internal double RefreshHz = 60.0;   // this monitor's actual refresh rate
    internal bool PresentGating;        // set by RunLoop for fullscreen canvas runs
    private double _nextPresentDue;     // due time in loop-stopwatch seconds
    public long PresentFrames;          // presents actually handed to DWM
    private bool _dwnLogged;            // WasStillDrawing warning (once)

    // ---- Present thread: raw Present(0) costs ~one DWM composition period
    // even with DO_NOT_WAIT; a dedicated thread keeps that wait out of the
    // frame budget. Only swapchain presents run there - the immediate context
    // stays on the main thread (not thread-safe).
    internal bool UsePresentThread;

    // ---- Editor preview render thread (raw path only): drives this window's
    // Render() from a dedicated thread so the vsync Present wait never blocks
    // the WinForms UI thread. UI-thread device access (OnResize) takes the
    // same lock.
    internal readonly object EditorLock = new object();

    // When set, Render() queues the swapchain flip to the present thread
    // instead of blocking the UI thread inside the vsync wait.
    internal volatile bool UseEditorPresentThread;
    private volatile bool _editorPresentInFlight;
    internal bool EditorPresentInFlight => _editorPresentInFlight;

    // ---- Editor preview: deferred swapchain/RT resize ----------------------
    // OnResize (UI thread) and the present thread must never overlap on the
    // swapchain: ResizeBuffers during an in-flight Present is driver-level UB
    // (the silent AV after a while). Protocol: each side sets its flag
    // (_editorResizeActive / _editorPresentInFlight) FIRST, then checks the
    // other's flag and backs off - no overlap is possible. A resize that
    // cannot start stays pending; RenderTick retries until applied.
    private volatile bool _editorResizePending;   // an OnResize is waiting
    private volatile bool _editorResizeActive;    // resize being applied now
    // Present thread inside a swapchain operation: the main thread must not
    // touch the same swapchain concurrently (GetBuffer / RTV / ResizeBuffers
    // during an in-flight Present is driver-level UB).
    private volatile bool _presentInFlight;
    internal bool PresentInFlight => _presentInFlight;
    private Thread _presentThread;
    private readonly AutoResetEvent _presentSignal = new(false);
    private readonly ManualResetEvent _presentExit = new(false);
    private volatile float _presentThreadTime;
    private volatile bool _presentQueued;
    // Submitted-but-not-presented frames; the editor's render timer uses
    // this as backpressure instead of piling up driver work on the UI thread.
    private int _presentPending;
    internal int PresentPending => _presentPending;
    private int _slowPresentLogs;
    private double _lastSlowPresentLog;   // Stopwatch seconds of the last slow-present log

    // Last Present that took >500 ms (0 = none). The editor's status bar
    // surfaces this - a stall freezes the preview by design (backpressure),
    // and without this there is nothing on screen to say why.
    public long LastPresentStallMs;

    // ---- Shadertoy compatibility inputs (uniform buffer slots 1..3) ------
    private Vector4 _stMouse = Vector4.Zero;  // ctor fills this on preview windows
    private float _stLastTime = -1f;          // for iTimeDelta
    private int _stFrame;                     // iFrame
    private readonly Vector4[] _stUniformData = new Vector4[4];

    // One place updates the whole uniform buffer: iResolution/iTime (slot 0,
    // existing behaviour) plus iMouse/iFrame/iTimeDelta/iDate. Canvas owner,
    // scaled and direct paths all call this.
    private void UpdateShaderUniforms(float time, float resW, float resH)
    {
        float dt = _stLastTime < 0f ? 0f : time - _stLastTime;
        _stLastTime = time;
        if (dt < 0f || dt > 0.25f) dt = 0f; // first frame or a present stall - no physics spike
        _stFrame++;
        DateTime now = DateTime.Now;
        _stUniformData[0] = new Vector4(resW, resH, 1f, time);
        _stUniformData[1] = _stMouse;
        _stUniformData[2] = new Vector4(_stFrame, dt, 0f, 0f);
        _stUniformData[3] = new Vector4(now.Year, now.Month, now.Day,
            (float) now.TimeOfDay.TotalSeconds);
        _device.UpdateBuffer(_uniform, 0, _stUniformData);
    }

    // ---- Present lane (present_lanes=2): swapchain on a second D3D11
    // device; the canvas crosses devices via a keyed-mutex shared texture.
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

    // Coalescing: if the thread is still presenting, the event just latches.
    private void RequestPresent(float time)
    {
        Interlocked.Increment(ref _presentPending);
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
                var sw = Stopwatch.StartNew();
                try { PresentCore(_presentThreadTime); }
                catch (Exception ex) { Log.Ex("PresentThread", ex); }
                Interlocked.Decrement(ref _presentPending);
                // Sustained multi-second presents = GPU/WARP-bound; the
                // render timer's backpressure throttles the preview.
                double ms = sw.ElapsedMilliseconds;
                if (ms > 500)
                {
                    LastPresentStallMs = (long) ms;
                    double sinceLast = (Stopwatch.GetTimestamp() - _lastSlowPresentLog) * 1000.0
                                       / Stopwatch.Frequency;
                    if (_slowPresentLogs >= 20 && sinceLast > 60_000)
                        _slowPresentLogs = 0; // quiet minute - budget available again
                    if (_slowPresentLogs < 20)
                    {
                        _slowPresentLogs++;
                        _lastSlowPresentLog = Stopwatch.GetTimestamp();
                        Log.W($"Present took {ms:F0} ms - GPU-bound; render timer throttled to GPU speed");
                    }
                }
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

    // show_fps=1 overlay: counts presented frames (post gating), not ticks.
    private Label _fpsLabel;
    private long _fpsLabelFrames;
    private double _fpsLabelT;

    // tear_fix_child=1 (with show_fps off): invisible layered child window
    // over the flip swapchain (DWM-composition anti-tear hack).
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

        // Any surface exposed before the first GPU frame must be black.
        BackColor = Color.Black;
        SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint, true);

        // Hide the cursor on every window: raw windows never run
        // InitGraphics, and child controls inherit it.
        if (!isPreview) Cursor = HiddenCursor();

        // Shadertoy iMouse reporting: preview windows only (/t, /e, /p) -
        // screensaver windows get zeros so a stray move never animates the
        // saver. Y is flipped to Shadertoy's bottom-left origin.
        if (isPreview)
        {
            MouseMove += (_, e) => _stMouse = new Vector4(e.X, ClientSize.Height - e.Y, _stMouse.Z, _stMouse.W);
            MouseDown += (_, e) => _stMouse = new Vector4(_stMouse.X, _stMouse.Y, e.X, ClientSize.Height - e.Y);
            MouseUp   += (_, _) => _stMouse = new Vector4(_stMouse.X, _stMouse.Y, _stMouse.Z, -Math.Abs(_stMouse.W));
        }

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

            // Pie 0.10.1's DebugGraphicsDevice throws SEHException on
            // SetDepthStencilState - Debug=true is forbidden.
            _device = GraphicsDevice.CreateD3D11(hwnd, cs, new GraphicsDeviceOptions { Debug = false });
            string adapterName = _device.Adapter.ToString() ?? "";
            Log.W($"InitGraphics: D3D11 device created, adapter='{adapterName}'"
                + (adapterName.IndexOf("Software", StringComparison.OrdinalIgnoreCase) >= 0
                    || adapterName.IndexOf("WARP", StringComparison.OrdinalIgnoreCase) >= 0
                    || adapterName.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "  (SOFTWARE RENDERING - heavy shaders will run at seconds per frame)" : ""));

            // Cache hit: the fragment arrives as precompiled SPIR-V and
            // shaderc is skipped (Pie still runs SPIRV-Cross -> HLSL).
            _mainShader = _device.CreateShader(new[]
            {
                new ShaderAttachment(ShaderStage.Vertex, vertexShader),
                Program.FragmentSpirv != null
                    ? new ShaderAttachment(ShaderStage.Fragment, Program.FragmentSpirv, "main")
                    : new ShaderAttachment(ShaderStage.Fragment, fragmentShader),
            });
            Log.W($"InitGraphics: main shader compiled ({(Program.FragmentSpirv != null ? "cached SPIR-V" : "GLSL")} -> HLSL)");

            // Load + bind the PNG textures the shader references.
            _userTextures = ShaderTextures.CreateAll(_device, fragmentShader, Program.ActiveShaderPath);

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

            // Blend baked into the source (a spec constant would not survive
            // shaderc -> SPIRV-Cross -> HLSL); "0.0###" because GLSL forbids
            // a bare "0f"/"1f" literal.
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
            _uniform = _device.CreateBuffer(BufferType.UniformBuffer, new Vector4[4], true);

            // RT / blit machinery: diag modes and the scaled path; at
            // res_mult == 1.0 with no diag the normal path renders straight
            // to the backbuffer.
            if (!Program.CanvasPipeline || IsCanvasOwner)
            {
                // Normal runs: this window's canvas-slice quad; diag: the
                // fullscreen letterbox quad.
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer,
                    Program.CanvasPipeline ? ComputeCanvasVerts() : ComputeBlitVerts());
                _blitVbStatic = _device.CreateBuffer(BufferType.VertexBuffer, FullscreenQuad);

                // diag 5/6 need the master-size RT for readbacks; a normal
                // run uses the shared canvas at master size x mult.
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
            foreach (var t in _userTextures) t.Dispose();
            _userTextures.Clear();
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

    internal bool MainShaderReady => _mainShader != null;

    // Bind the shader's PNG textures (bindings 1..N) in every main-shader
    // draw path - the blit pass overwrites PS slots 0/1 right after.
    private void BindUserTextures()
    {
        if (_userTextures.Count == 0 || _sampler == null) return;
        foreach (var t in _userTextures)
            _device.SetTexture((uint) t.Slot, t.Tex, _sampler);
    }

    // Runtime shader swap (editor live preview), split so the shaderc part
    // can run off the UI thread: CompileFragmentSource is device-free,
    // ApplyCompiledFragment does the device swap.

    public sealed class FragmentCompile
    {
        public int Gen;             // editor-side generation (stale-discard)
        public byte[] Spirv;        // compiled fragment SPIR-V (null on error)
        public string Frag;         // composed GLSL source (drives texture load)
        public string Error;        // null on success
    }

    // CPU-only part: compose + shaderc, no device access - safe on any
    // thread and before InitGraphics.
    public FragmentCompile CompileFragmentSource(string userSrc)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string frag = ShaderAdapter.ComposeFragment(userSrc);
            var res = Compiler.ToSpirv(ShaderStage.Fragment, Language.GLSL,
                System.Text.Encoding.UTF8.GetBytes(frag), "main");
            Log.W($"CompileFragmentSource: {sw.ElapsedMilliseconds} ms, {userSrc.Length} chars, {(res.IsSuccess ? "OK" : "error")}");
            return res.IsSuccess
                ? new FragmentCompile { Spirv = res.Result, Frag = frag }
                : new FragmentCompile { Error = res.Error };
        }
        catch (Exception ex)
        {
            Log.Ex("CompileFragmentSource", ex);
            return new FragmentCompile { Error = ex.Message };
        }
    }

    // Device-side swap, UI thread. Returns null on success, else error text.
    public string ApplyCompiledFragment(byte[] spirv, string frag)
    {
        if (_device == null) return "preview not initialized yet";
        lock (EditorLock)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                Shader next = _device.CreateShader(new[]
                {
                    new ShaderAttachment(ShaderStage.Vertex, ShaderAdapter.VertexShader),
                    new ShaderAttachment(ShaderStage.Fragment, spirv, "main"),
                });

                // Swap the shader first; dispose the old texture set only
                // after the new one is created on the same device.
                _mainShader?.Dispose();
                _mainShader = next;
                var nextTextures = ShaderTextures.CreateAll(_device, frag, Program.ActiveShaderPath);
                foreach (var t in _userTextures) t.Dispose();
                _userTextures = nextTextures;

                Log.W($"ApplyCompiledFragment: shader swapped OK in {sw.ElapsedMilliseconds} ms");
                return null;
            }
            catch (Exception ex)
            {
                Log.Ex("ApplyCompiledFragment", ex);
                return ex.Message;
            }
        }
    }

    // Convenience wrapper (synchronous, UI thread) - kept for fallback use.
    public string RecompileFragment(string userSrc)
    {
        var r = CompileFragmentSource(userSrc);
        if (r.Error != null) return r.Error;
        return ApplyCompiledFragment(r.Spirv, r.Frag);
    }

    // Editor init with an off-thread compiled fragment (avoids the shaderc
    // freeze on the UI thread); spirv=null inits with a GLSL fallback source
    // (compile-error display path).
    public bool InitGraphics(string vertexShader, byte[] fragSpirv, string fragSource)
    {
        Program.FragmentSpirv = fragSpirv;
        try { return InitGraphics(vertexShader, fragSource); }
        finally { Program.FragmentSpirv = null; }
    }

    public void Render(float time)
    {
        if (_disposed) return;
        if (Program.CanvasPipeline)
        {
            // DRAW phase only (presents happen in Present()).
            CanvasDraw(time);
            return;
        }
        if (_device == null) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;

        // DIAG 5: (a) gradient into the RT + readback, (b) magenta backbuffer
        // clear + readback - draw pipeline vs swapchain/present path.
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

        // DIAG 6: A=main path (control), B=blit with dynamic VB
        // (UpdateBuffer), C=blit with static VB.
        if (Program.DiagMode == 6)
        {
            _diagFrames++;
            if (!_diagDone && _diagFrames >= 5)
            {
                _diagDone = true;
                _device.SetFramebuffer(_fb);
                _device.Viewport = new Rectangle(0, 0, _master.Width, _master.Height);

                // A: main path.
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

                // B: dynamic VB (UpdateBuffer-filled).
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

                // C: static VB (initial data).
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

        // DIAG 1/4: gradient straight to the backbuffer (4 adds readback).
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

        // Scaled path (res_mult < 1.0): render into the small RT, upscale
        // with the blended sampler. Static VBs - Pie 0.10.1's UpdateBuffer on
        // dynamic VBs fails.
        if (ScaledPath)
        {
            int rtW = _rtW, rtH = _rtH;

            _device.SetFramebuffer(_fb);
            _device.Viewport = new Rectangle(0, 0, rtW, rtH);
            UpdateShaderUniforms(time, rtW, rtH);
            _device.SetShader(_mainShader);
            _device.SetUniformBuffer(0u, _uniform);
            BindUserTextures();
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

            if (UseEditorPresentThread) { RequestPresent(time); return; }
            _device.Present(PresentInterval);
            return;
        }

        // Direct path (res_mult == 1.0): the shader renders at this
        // monitor's native resolution.
        _device.SetFramebuffer(null);
        _device.Viewport = new Rectangle(0, 0, w, h);
        Vector4 resolutionTime = new Vector4(w, h, 1f, time);
        UpdateShaderUniforms(time, w, h);

        if (Program.DiagMode == 3 && _frameLogs < 10)
        {
            _frameLogs++;
            Log.W($"DIAG3 frame {_frameLogs} client={w}x{h}: uniform={resolutionTime} (direct single-pass)");
        }

        _device.SetShader(_mainShader);
        _device.SetUniformBuffer(0u, _uniform);
        BindUserTextures();
        _device.SetDepthStencilState(_depthDisabled);
        _device.SetPrimitiveType(PrimitiveType.TriangleList);
        _device.SetInputLayout(_mainLayout);
        _device.SetVertexBuffer(0u, _quadVb, 8u);
        _device.SetIndexBuffer(_indexBuffer, IndexType.UInt);
        _device.DrawIndexed(6u);

        if (UseEditorPresentThread) { RequestPresent(time); return; }
        // vsync: Present(0)+AllowTearing silently fails on some drivers
        // (black screens); with an FPS cap the interval is 0.
        _device.Present(PresentInterval);
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
    private const int WM_DPICHANGED = 0x02E0;

    protected override void WndProc(ref Message m)
    {
        // Per-monitor DPI (editor process): never let WinForms rescale the
        // render surface - our size is driven by the editor's host panel in
        // physical pixels, and a framework rescale would fight the swapchain
        // sizing. Ignoring the message is fine for a child window.
        if (m.Msg == WM_DPICHANGED) return;
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
        // Log real size changes only - a spurious resize coinciding with a
        // monitor blink is a diagnostic clue.
        if (ClientSize.Width != _lastLoggedW || ClientSize.Height != _lastLoggedH)
        {
            _lastLoggedW = ClientSize.Width; _lastLoggedH = ClientSize.Height;
            Log.W($"OnResize: client now {ClientSize.Width}x{ClientSize.Height}");
        }
        // Editor: coalesce resizes, apply when the present thread is idle
        // (ApplyEditorResize).
        if (UseEditorPresentThread)
        {
            _editorResizePending = true;
            ApplyEditorResize(100);
            return;
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
            // Scaled path only: rebuild the RT; the shared canvas RT must
            // NOT follow resizes.
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

    // Called every tick so a pending resize is applied at the newest size
    // once the present thread is idle.
    internal void ApplyPendingEditorResize()
    {
        if (_editorResizePending) ApplyEditorResize(0);
    }

    private void ApplyEditorResize(int waitMs)
    {
        if (_device == null || _disposed)
        {
            _editorResizePending = false;
            return;
        }
        if (_editorResizeActive) return; // already applying - the pending flag survives
        _editorResizeActive = true;      // FIRST: present thread now skips its Present
        try
        {
            // Drain an in-flight Present, bounded (a hung Present must not
            // hang the UI thread).
            if (_editorPresentInFlight)
            {
                var swIdle = Stopwatch.StartNew();
                while (_editorPresentInFlight && swIdle.ElapsedMilliseconds < waitMs)
                    Thread.Yield();
                if (_editorPresentInFlight) return; // still busy - stay pending
            }

            _device.ResizeSwapchain(new Size(ClientSize.Width, ClientSize.Height));
            // Rebuild the static blit VB for the new client geometry.
            if (_blitVb != null)
            {
                _blitVb.Dispose();
                _blitVb = _device.CreateBuffer(BufferType.VertexBuffer,
                    Program.CanvasPipeline ? ComputeCanvasVerts() : ComputeBlitVerts());
            }
            // Scaled path only: rebuild the RT; the shared canvas RT must
            // NOT follow resizes.
            if (_rt != null && _resMult < 1f && !Program.CanvasPipeline)
            {
                int rtW = Math.Max(1, (int) Math.Round(ClientSize.Width * _resMult));
                int rtH = Math.Max(1, (int) Math.Round(ClientSize.Height * _resMult));
                if (rtW != _rtW || rtH != _rtH)
                {
                    _rt.Dispose();
                    _fb.Dispose();
                    _rt = _device.CreateTexture(TextureDescription.Texture2D(rtW, rtH,
                        Format.R8G8B8A8_UNorm, 1, 1, TextureUsage.Framebuffer | TextureUsage.ShaderResource));
                    _fb = _device.CreateFramebuffer(new FramebufferAttachment(_rt));
                    _rtW = rtW; _rtH = rtH;
                }
            }
            _editorResizePending = false;
            Log.W($"OnResize: editor swapchain applied {ClientSize.Width}x{ClientSize.Height} (RT {_rtW}x{_rtH})");
        }
        catch (Exception ex) { Log.Ex("OnResize/editor", ex); }
        finally { _editorResizeActive = false; }
    }
}
