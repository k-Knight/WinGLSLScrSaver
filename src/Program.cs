// ===========================================================================
// WinGLSLScrSaver - a GLSL shader screensaver for Windows (.scr)
// ---------------------------------------------------------------------------
// Renders a GLSL fragment shader on the GPU (Direct3D 11 via Pie) as a real
// Windows screensaver with full /s /p /c support.
//
// DEBUG BUILD NOTES:
//   * DEBUG builds and --diag runs log to winglslscrsaver.log next to the
//     working directory (falls back to %TEMP%). Release builds are silent:
//     no log file, no fragment dump, zero per-call file I/O.
//   * Fatal exceptions are logged AND shown in a MessageBox.
//   * GPU device creation is deferred until AFTER the form is shown and
//     (for preview) reparented - WinForms recreates window handles when
//     properties change, which would orphan a device created too early.
// ===========================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Pie;
using Pie.ShaderCompiler;
using D3D = Vortice.Direct3D11;

// ---------------------------------------------------------------------------
// Debug logging: appends to a file next to the exe, mirrors to console.
// ---------------------------------------------------------------------------
internal static class Log
{
    private static readonly object _lock = new();
    private static string _path;

    // Logging is a debug aid: enabled in DEBUG builds or when a --diag mode
    // was requested explicitly. Release runs stay silent - no log file and
    // no per-call file I/O (startup slowdown report).
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
            // Logs live in %localappdata%\WinGLSLScrSaver\ next to the config
            // file - never next to the exe (a screensaver's working directory
            // is wherever Windows launched it from, e.g. System32).
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

// ---------------------------------------------------------------------------
// Win32 interop.
// ---------------------------------------------------------------------------
internal static class Native
{
    [DllImport("user32.dll")]
    internal static extern IntPtr CreateCursor(IntPtr hInst, int xHotspot, int yHotspot, int nWidth, int nHeight, byte[] andPlane, byte[] xorPlane);

    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    internal static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    internal const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] internal static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    // Display power management: while the screensaver runs, tell Windows the
    // display is in use so the OS power plan cannot blank a monitor mid-run
    // (user report: a monitor occasionally switched off and back on).
    internal const uint ES_CONTINUOUS = 0x80000000;
    internal const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    internal static extern uint SetThreadExecutionState(uint esFlags);

    [DllImport("user32.dll")] internal static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr newLong);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static extern int SetWindowLong32(IntPtr hwnd, int index, int newLong);
    internal const int GWL_STYLE = -16;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_VISIBLE = 0x10000000;

    internal static IntPtr SetWindowLong(IntPtr hwnd, int index, IntPtr value)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : (IntPtr)SetWindowLong32(hwnd, index, (int)value);
}

// ---------------------------------------------------------------------------
// Screen grouping: screens that fit perfectly together (flush edges, no gap,
// no overlap, fully aligned shared edge) form a group whose union is a
// rectangle. Connected components are the groups.
// ---------------------------------------------------------------------------
internal class ScreenGroup
{
    public List<Rectangle> Screens = new();
    public Rectangle Bbox;

    public static List<ScreenGroup> ComputeGroups(Rectangle[] screenRects)
    {
        var remaining = new List<Rectangle>(screenRects);
        var groups = new List<ScreenGroup>();

        while (remaining.Count > 0)
        {
            var g = new ScreenGroup { Screens = { remaining[0] }, Bbox = remaining[0] };
            remaining.RemoveAt(0);

            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    if (FlushAdjacent(remaining[i], g.Bbox))
                    {
                        g.Screens.Add(remaining[i]);
                        g.Bbox = Rectangle.Union(g.Bbox, remaining[i]);
                        remaining.RemoveAt(i);
                        grew = true;
                    }
                }
            }
            groups.Add(g);
        }
        return groups;
    }

    private static bool FlushAdjacent(Rectangle a, Rectangle b)
    {
        if (a.Right == b.Left || b.Right == a.Left)          // side by side
            return a.Top == b.Top && a.Bottom == b.Bottom;
        if (a.Bottom == b.Top || b.Bottom == a.Top)          // stacked
            return a.Left == b.Left && a.Right == b.Right;
        return false;
    }
}

// ---------------------------------------------------------------------------
// Blit vertex: one vec4 attribute = clip-space xy + uv zw.
// ---------------------------------------------------------------------------
internal struct VPosUv
{
    public Vector4 Data;
    public VPosUv(Vector2 pos, Vector2 uv) { Data = new Vector4(pos.X, pos.Y, uv.X, uv.Y); }
}

// ---------------------------------------------------------------------------
// One render window = one monitor (or the preview window). The Pie D3D11
// device is created on the form's handle, but ONLY after the handle is final
// (after Show + reparenting). All GPU resources live in InitGraphics().
// ---------------------------------------------------------------------------
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

// ---------------------------------------------------------------------------
// Config dialog (/c and double-click): pick a shader file, persist it.
// ---------------------------------------------------------------------------
internal class ConfigDialog : Form
{
    private readonly TextBox _pathBox;

    public ConfigDialog()
    {
        Text = "WinGLSLScrSaver Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 120);

        var label = new Label { Text = "Fragment shader file:", Location = new Point(12, 12), AutoSize = true };
        // Default to the %localappdata%\WinGLSLScrSaver folder (where shaders
        // are searched when nothing is configured) instead of an empty box.
        string configured = Settings.LoadShaderPath();
        _pathBox = new TextBox
        {
            Location = new Point(12, 34), Width = 340,
            Text = string.IsNullOrWhiteSpace(configured) ? Settings.AppDataDir : configured,
        };
        var browse = new Button { Text = "Browse...", Location = new Point(360, 32), Width = 85 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(270, 78), Width = 85 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(360, 78), Width = 85 };

        browse.Click += (_, _) =>
        {
            // Default the file dialog to the folder the .scr itself lives in.
            // NOTE: AppContext.BaseDirectory would be the %TEMP% extraction dir
            // for a single-file published app - Environment.ProcessPath gives
            // the real .scr location.
            string scrDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
            using var dlg = new OpenFileDialog
            {
                Title = "Pick a GLSL fragment shader",
                Filter = "Fragment shaders (*.fs;*.glsl;*.frag)|*.fs;*.glsl;*.frag|All files (*.*)|*.*",
                InitialDirectory = scrDir,
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _pathBox.Text = dlg.FileName;
        };

        Controls.AddRange(new Control[] { label, _pathBox, browse, ok, cancel });
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (DialogResult == DialogResult.OK)
            Settings.SaveShaderPath(_pathBox.Text);
        base.OnFormClosed(e);
    }
}

// ---------------------------------------------------------------------------
// Settings persistence: a plain config file in %localappdata%\WinGLSLScrSaver\.
// That folder is also the default shader location when nothing is configured,
// so no machine-specific path is ever baked in.
// ---------------------------------------------------------------------------
internal static class Settings
{
    public static string AppDataDir => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGLSLScrSaver");

    private static string ConfigPath => System.IO.Path.Combine(AppDataDir, "config.txt");

    public static string LoadShaderPath()
    {
        try { return File.ReadAllText(ConfigPath).Trim(); }
        catch { return ""; }
    }

    public static void SaveShaderPath(string path)
    {
        Directory.CreateDirectory(AppDataDir);
        File.WriteAllText(ConfigPath, path ?? "");
    }
}

// ---------------------------------------------------------------------------
// GLSL adaptation (same approach as ShaderRunner).
// ---------------------------------------------------------------------------
internal static class ShaderAdapter
{
    public const string VertexShader = @"
#version 450
layout (location = 0) in vec2 aPosition;
layout (location = 0) out vec2 fragTexCoord;
void main()
{
    fragTexCoord = aPosition * 0.5 + 0.5;
    gl_Position = vec4(aPosition, 0.0, 1.0);
}";

    public const string FragmentHeader = @"
#version 450
layout (location = 0) in vec2 fragTexCoord;

layout (binding = 0) uniform Uniforms
{
    vec4 uResolutionTime; // xyz = iResolution (z = aspect), w = iTime seconds
};

#define iResolution uResolutionTime.xyz
#define iTime uResolutionTime.w

layout (location = 0) out vec4 finalColor;
";

    public const string DefaultShader = @"
#define pi 3.14159265

float getV(vec2 uv) {
    float l = length(uv) * pi * 2.0;
    float c = ceil(l);
    float t = iTime;
    float o = t * 4.0 / c - t * c * 0.001;
    float ox = sin(o);
    float oy = cos(o);
    uv = vec2(uv.x * ox - uv.y * oy, uv.x * oy + uv.y * ox);
    float theta = 0.5 * atan(uv.x, uv.y) / pi + 0.5;
    float q = mod(mod(theta * c, c), 1.0);
    return q;
}

void main()
{
    vec2 fragCoord = fragTexCoord.xy * iResolution.xy;
    float s = 5.0;
    float b = min(iResolution.x, iResolution.y) / s;
    vec2 r = iResolution.xy / b;
    float o = 0.3333;
    vec2 uv1 = (fragCoord + vec2(-o, -o)) * 2.0 / b - r;
    vec2 uv2 = (fragCoord + vec2(o, -o)) * 2.0 / b - r;
    vec2 uv3 = (fragCoord + vec2(o, o)) * 2.0 / b - r;
    vec2 uv4 = (fragCoord + vec2(-o, o)) * 2.0 / b - r;
    vec3 col = vec3((getV(uv1) + getV(uv2) + getV(uv3) + getV(uv4)) * 0.25);
    finalColor = vec4(col, 1.0);
}";

    public static string Adapt(string src)
    {
        src = Regex.Replace(src, @"^\s*#version.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*in\s+vec2\s+fragTexCoord\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*in\s+vec4\s+fragColor\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*layout\s*\(\s*location\s*=\s*\d+\s*\)\s*out\s+vec4\s+finalColor\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*out\s+vec4\s+finalColor\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*uniform\s+vec3\s+iResolution\s*;.*$", "", RegexOptions.Multiline);
        src = Regex.Replace(src, @"^\s*uniform\s+float\s+iTime\s*;.*$", "", RegexOptions.Multiline);

        Match mainMatch = Regex.Match(src, @"void\s+main\s*\(\s*\)");
        if (!mainMatch.Success && Regex.IsMatch(src, @"\bmainImage\s*\("))
        {
            src += "\nvoid main()\n{\n    vec2 fragCoord = fragTexCoord.xy * iResolution.xy;\n    mainImage(finalColor, fragCoord);\n}\n";
            mainMatch = Regex.Match(src, @"void\s+main\s*\(\s*\)");
        }
        if (mainMatch.Success)
        {
            Match m = Regex.Match(src.Substring(0, mainMatch.Index), @"vec2\s+fragCoord\s*=\s*([^;]+);");
            if (m.Success)
            {
                string expr = m.Groups[1].Value;
                src = Regex.Replace(src, @"^\s*vec2\s+fragCoord\s*=\s*[^;]+;\s*(?://.*)?\s*$", "", RegexOptions.Multiline);
                src = Regex.Replace(src, @"void\s+main\s*\(\s*\)\s*\{", "void main()\n{\n    vec2 fragCoord = " + expr + ";",
                    RegexOptions.Multiline, TimeSpan.FromSeconds(2));
            }
        }

        return src;
    }
}

// ===========================================================================
// Entry point: the classic .scr command line protocol.
// ===========================================================================
internal static class Program
{
    private static bool _running = true;

    // --diag=1: gradient to backbuffer (blit path)   --diag=2: solid red into
    // RT, blitted normally (RT path)   --diag=3: full pipeline + frame logs.
    // --diag=4: gradient + debug layer + info queue dump + pixel readback.
    internal static int DiagMode = 0;

    [STAThread]
    private static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Ex("UnhandledException", (Exception) e.ExceptionObject);

        // NOTE: never interpolate Log.Path into a W() call unconditionally -
        // the argument is evaluated even when logging is off, which used to
        // create an empty log file next to the exe in Release.
        Log.W("=================================================");
        Log.W($"WinGLSLScrSaver starting. args=[{string.Join(" ", args)}]");
        if (Log.Verbose) Log.W($"Log file: {Log.Path}");
        Log.W($"exe: {Environment.ProcessPath ?? AppContext.BaseDirectory}");

        try
        {
            Native.SetProcessDPIAware();
            Log.W("SetProcessDPIAware OK");
        }
        catch (Exception ex) { Log.Ex("SetProcessDPIAware", ex); }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Parse arguments (case-insensitive; "/p12345" and "/p 12345" both ok).
        string mode = "";
        IntPtr previewHwnd = IntPtr.Zero;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].Trim();
            if (a.Equals("/s", StringComparison.OrdinalIgnoreCase) || a.Equals("-s", StringComparison.OrdinalIgnoreCase))
                mode = "s";
            else if (a.StartsWith("/p", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-p", StringComparison.OrdinalIgnoreCase))
            {
                mode = "p";
                string rest = a.Length > 2 ? a.Substring(2) : (i + 1 < args.Length ? args[++i] : "");
                Log.W($"preview hwnd raw string: '{rest}'");
                long.TryParse(rest.Trim().TrimStart('"', '\'').TrimEnd('"', '\''), out long hwndVal);
                previewHwnd = new IntPtr(hwndVal);
            }
            else if (a.StartsWith("/c", StringComparison.OrdinalIgnoreCase) || a.StartsWith("-c", StringComparison.OrdinalIgnoreCase)
                     || a.StartsWith("/l", StringComparison.OrdinalIgnoreCase))
                mode = "c";
        }
        if (mode.Length == 0) mode = "c";
        Log.W($"mode={mode} previewHwnd=0x{previewHwnd.ToInt64():X}");

        // Optional diagnostic flag: --diag=N or /diag:N
        foreach (string a in args)
        {
            Match dm = Regex.Match(a, @"(?i)^[/\-]{1,2}diag[:=](\d)$");
            if (dm.Success)
            {
                DiagMode = dm.Groups[1].Value[0] - '0';
                Log.Verbose = true; // an explicit diag request re-enables logging even in Release
                Log.W($"DIAG MODE {DiagMode} enabled (auto-exit after 6s)");
            }
        }

        try
        {
            switch (mode)
            {
                case "c":
                    ShowConfig();
                    return;
                case "p":
                    if (previewHwnd == IntPtr.Zero)
                    {
                        Log.W("preview requested but hwnd invalid -> config dialog");
                        ShowConfig();
                        return;
                    }
                    RunPreview(previewHwnd);
                    return;
                default:
                    RunFullscreen();
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Ex("Main", ex);
            MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\nDetails in log:\n{Log.Path}",
                "WinGLSLScrSaver error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void ShowConfig()
    {
        Log.W("Showing config dialog");
        // ShowDialog (not Application.Run): with a modeless main form the
        // OK/Cancel buttons never closed the dialog and DialogResult stayed
        // None, so no config file was ever written. ShowDialog gives the
        // buttons their normal semantics; saving still happens OnFormClosed.
        new ConfigDialog().ShowDialog();
        Log.W("Config dialog closed");
    }

    // ---- shader text lookup: registry -> exe dir shader.fs -> any .fs -> embedded ----
    private static string LoadShaderText()
    {
        string saved = Settings.LoadShaderPath();
        Log.W($"Shader lookup: config='{saved}'");
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
        {
            Log.W($"Shader source: configured path ({new FileInfo(saved).Length} bytes)");
            return File.ReadAllText(saved);
        }

        // Nothing configured: search %localappdata%\WinGLSLScrSaver\ - the same
        // folder the config file lives in. The exe's own folder is intentionally
        // NOT searched (single-file apps extract to %TEMP%; that path is not
        // meaningful to end users).
        try
        {
            string dir = Settings.AppDataDir;
            Log.W($"Shader lookup: not configured, searching {dir}");
            string first = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.fs").FirstOrDefault()
                  ?? Directory.GetFiles(dir, "*.glsl").FirstOrDefault()
                  ?? Directory.GetFiles(dir, "*.frag").FirstOrDefault()
                : null;
            if (first != null)
            {
                Log.W($"Shader source: found in local app data ({new FileInfo(first).Length} bytes)");
                return File.ReadAllText(first);
            }
        }
        catch { }

        Log.W("Shader source: EMBEDDED default swirl (no file found)");
        return ShaderAdapter.DefaultShader;
    }

    // ---- shared setup ----
    private static List<RenderWindow> CreateWindows(string shaderText, bool preview, Rectangle previewBounds,
        out string vertexShader, out string fragmentShader)
    {
        vertexShader = ShaderAdapter.VertexShader;
        fragmentShader = Program.DiagMode == 2
            ? @"#version 450
layout (location = 0) out vec4 finalColor;
void main()
{
    finalColor = vec4(1.0, 0.0, 0.0, 1.0);
}"
            : Program.DiagMode is 5 or 6
            ? @"#version 450
layout (location = 0) in vec2 fragTexCoord;
layout (location = 0) out vec4 finalColor;
void main()
{
    finalColor = vec4(fragTexCoord.x, fragTexCoord.y, 0.2, 1.0);
}"
            : ShaderAdapter.FragmentHeader + ShaderAdapter.Adapt(shaderText);
        Log.W($"Fragment shader prepared: {fragmentShader.Length} chars total (diagMode={Program.DiagMode})");

        // Dump the EXACT fragment source for inspection (debug aid only -
        // skipped in silent Release runs). Goes to %localappdata% too.
        if (Log.Verbose)
        {
            try
            {
                Directory.CreateDirectory(Settings.AppDataDir);
                File.WriteAllText(System.IO.Path.Combine(Settings.AppDataDir, "ss_fragment_dump.fs"), fragmentShader);
                Log.W("Fragment dump written to %localappdata%\\WinGLSLScrSaver\\ss_fragment_dump.fs");
            }
            catch (Exception ex) { Log.W($"Fragment dump failed: {ex.Message}"); }
        }

        var windows = new List<RenderWindow>();

        if (preview)
        {
            Log.W($"Preview window bounds: {previewBounds}");
            var win = new RenderWindow(previewBounds, "WinGLSLScrSaver Preview",
                new Size(previewBounds.Width, previewBounds.Height),
                new Rectangle(0, 0, previewBounds.Width, previewBounds.Height),
                new Point(0, 0),
                isPreview: true, inputExit: false);
            windows.Add(win);
            return windows;
        }

        var screens = Screen.AllScreens;
        Log.W($"Screens found: {screens.Length}");
        var rects = new List<Rectangle>();
        foreach (var s in screens)
        {
            Log.W($"  screen '{s.DeviceName}' bounds={s.Bounds} primary={s.Primary}");
            rects.Add(s.Bounds);
        }

        var groups = ScreenGroup.ComputeGroups(rects.ToArray());
        Log.W($"Groups computed: {groups.Count}");
        foreach (var g in groups)
            Log.W($"  group bbox={g.Bbox} screens=[{string.Join("; ", g.Screens)}]");

        int masterW = groups.Max(g => g.Bbox.Width);
        int masterH = groups.Max(g => g.Bbox.Height);
        Log.W($"Master resolution: {masterW}x{masterH}");

        foreach (var group in groups)
        {
            foreach (var rect in group.Screens)
            {
                windows.Add(new RenderWindow(rect, "WinGLSLScrSaver",
                    new Size(masterW, masterH), group.Bbox,
                    new Point(rect.X - group.Bbox.X, rect.Y - group.Bbox.Y),
                    isPreview: false, inputExit: true));
            }
        }
        Log.W($"Windows created: {windows.Count}");
        return windows;
    }

    private static int GetFastestRefresh(bool preview)
    {
        if (preview) return 60;
        int best = 60;
        foreach (Screen s in Screen.AllScreens)
        {
            var dm = new Native.DEVMODE();
            dm.dmSize = (ushort) Marshal.SizeOf<Native.DEVMODE>();
            if (Native.EnumDisplaySettings(s.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref dm))
            {
                Log.W($"  refresh of {s.DeviceName}: {dm.dmDisplayFrequency} Hz");
                if (dm.dmDisplayFrequency > best) best = dm.dmDisplayFrequency;
            }
        }
        Log.W($"Target FPS (fastest display): {best}");
        return best;
    }

    private static void RunLoop(List<RenderWindow> windows, int targetFps)
    {
        // Init graphics BEFORE showing: the device still binds to the final
        // window handle (created on demand here; preview reparenting already
        // happened in RunPreview), and the first frame is on the swapchain by
        // the time the window becomes visible - no white flash at startup.
        int okCount = 0;
        foreach (var w in windows)
            if (w.InitGraphics(VertexShaderStatic, FragmentShaderStatic))
                okCount++;
        Log.W($"Graphics initialized on {okCount}/{windows.Count} windows");

        if (okCount == 0)
        {
            Log.W("ABORT: no window initialized graphics");
            _running = false;
        }

        foreach (var w in windows) w.Show();
        if (windows.Count > 0 && !windows[0].IsPreview)
            windows[0].Activate();
        Log.W("All windows shown");

        var sw = Stopwatch.StartNew();
        var frame = TimeSpan.FromMilliseconds(1000.0 / targetFps);
        double diagSeconds = Program.DiagMode > 0 ? 6.0 : double.PositiveInfinity;
        long frames = 0;

        while (_running)
        {
            Application.DoEvents();
            if (!_running) break;

            if (sw.Elapsed.TotalSeconds > diagSeconds)
            {
                Log.W("DIAG auto-exit reached");
                break;
            }

            if (windows.Any(w => w.ShutdownRequested))
            {
                Log.W("Exit: input shutdown requested");
                break;
            }
            if (windows.All(w => w.IsDisposed))
            {
                Log.W("Exit: all windows closed");
                break;
            }

            float t = (float) sw.Elapsed.TotalSeconds;
            foreach (var w in windows)
                w.Render(t);

            frames++;
            if (frames == 1) Log.W("First frame rendered");
            if (frames % 600 == 0) Log.W($"Heartbeat: {frames} frames, t={t:F1}s");

            var elapsed = sw.Elapsed;
            var nextFrame = TimeSpan.FromTicks((long) Math.Ceiling(elapsed.Ticks / (double) frame.Ticks) * frame.Ticks);
            var remain = nextFrame - elapsed;
            while (remain > TimeSpan.Zero && _running)
            {
                if (remain > TimeSpan.FromMilliseconds(2))
                    Thread.Sleep(1);
                else
                    Thread.SpinWait(50);
                elapsed = sw.Elapsed;
                remain = nextFrame - elapsed;
            }
        }

        Log.W($"Render loop ended after {frames} frames");
        foreach (var w in windows)
        {
            try { w.Close(); } catch { }
        }
        Log.W("All windows closed, exiting");
    }

    // Shader source strings stashed for RunLoop (created in CreateWindows).
    internal static string VertexShaderStatic;
    internal static string FragmentShaderStatic;

    // ---- /s ----
    private static void RunFullscreen()
    {
        using var mutex = new Mutex(true, @"Local\WinGLSLScrSaverRunning", out bool createdNew);
        Log.W($"Fullscreen: mutex createdNew={createdNew}");
        if (!createdNew)
        {
            Log.W("Another instance already running -> exit");
            return;
        }

        // Keep the displays on for the whole run: without this, the Windows
        // power plan can blank a monitor while the screensaver is animating
        // (reported: second monitor occasionally switched off, then back on).
        // Note this only prevents OS power management; a real cable/hardware
        // dropout would still be visible in the Event Viewer diagnostics.
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_DISPLAY_REQUIRED);
        Log.W("Fullscreen: display keep-awake set (ES_DISPLAY_REQUIRED)");

        try
        {
            var windows = CreateWindows(LoadShaderText(), preview: false, Rectangle.Empty,
                out VertexShaderStatic, out FragmentShaderStatic);
            if (windows.Count == 0)
            {
                Log.W("ABORT: no windows created");
                return;
            }
            int fps = GetFastestRefresh(preview: false);
            RunLoop(windows, fps);
        }
        finally
        {
            Native.SetThreadExecutionState(Native.ES_CONTINUOUS); // clear keep-awake
            try { mutex.ReleaseMutex(); } catch { }
        }
    }

    // ---- /p <hwnd> ----
    private static void RunPreview(IntPtr parentHwnd)
    {
        Log.W($"Preview: parent hwnd=0x{parentHwnd.ToInt64():X}");
        if (!Native.GetClientRect(parentHwnd, out Native.RECT r))
        {
            Log.W("Preview: GetClientRect FAILED");
            return;
        }
        Log.W($"Preview: client rect L={r.Left} T={r.Top} R={r.Right} B={r.Bottom}");

        var bounds = new Rectangle(0, 0, r.Right - r.Left, r.Bottom - r.Top);
        var windows = CreateWindows(LoadShaderText(), preview: true, bounds,
            out VertexShaderStatic, out FragmentShaderStatic);
        if (windows.Count == 0) return;

        var win = windows[0];
        win.Show();
        _ = win.Handle; // force handle creation
        Log.W($"Preview: child hwnd=0x{win.Handle.ToInt64():X}");

        IntPtr setParentResult = Native.SetParent(win.Handle, parentHwnd);
        Log.W($"Preview: SetParent result=0x{setParentResult.ToInt64():X} (nonzero = ok)");
        IntPtr styleResult = Native.SetWindowLong(win.Handle, Native.GWL_STYLE,
            new IntPtr(Native.WS_CHILD | Native.WS_VISIBLE));
        Log.W($"Preview: SetWindowLong result={styleResult.ToInt64()}");

        RunLoop(windows, GetFastestRefresh(preview: true));
    }
}
