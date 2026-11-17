namespace WinGLSLScrSaver;

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using D3D = Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;

// Raw D3D11 machinery for the shared-canvas architecture: window 0 renders
// the shader once per frame into the canvas RT; every window blits its
// crop-to-fit slice on the SAME device. Pie 0.10.1 cannot create a second
// swapchain on a device, so windows 1..N get plain DXGI swapchains.
internal partial class CanvasBlit
{
    // Vortice.DXGI does not map IDXGIKeyedMutex - declare it manually.
    // Raw keyed-mutex access WITHOUT an RCW: GetObjectForIUnknown + cast does
    // a context QI that fails cross-thread (E_NOINTERFACE on the present
    // thread), so hold the raw interface pointer and call its vtable directly.
    // Slots: IUnknown(0..2) + IDXGIObject(3..6) + GetDevice(7) + AcquireSync(8)
    // + ReleaseSync(9). IID from Win10 SDK dxgi.h.
    internal sealed class KeyedMutexRaw
    {
        private readonly IntPtr _p;
        private readonly AcquireSyncD _acq;
        private readonly ReleaseSyncD _rel;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int AcquireSyncD(IntPtr self, uint key, uint timeout);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseSyncD(IntPtr self, uint key);
        internal KeyedMutexRaw(IntPtr p)
        {
            _p = p;
            IntPtr vt = Marshal.ReadIntPtr(p);
            _acq = Marshal.GetDelegateForFunctionPointer<AcquireSyncD>(Marshal.ReadIntPtr(vt, 8 * IntPtr.Size));
            _rel = Marshal.GetDelegateForFunctionPointer<ReleaseSyncD>(Marshal.ReadIntPtr(vt, 9 * IntPtr.Size));
        }
        internal int AcquireSync(uint key, uint timeout) => _acq(_p, key, timeout);
        internal int ReleaseSync(uint key) => _rel(_p, key);
        internal void Dispose() => Marshal.Release(_p);
    }

    private static KeyedMutexRaw QIKeyedMutex(SharpGen.Runtime.ComObject obj)
    {
        IntPtr unk = obj.NativePointer;
        var iid = new Guid("9d8e1289-d7b3-465f-8126-250e349af85d");
        Marshal.QueryInterface(unk, ref iid, out IntPtr p);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException(
                $"QueryInterface(IDXGIKeyedMutex) FAILED on {obj.GetType().Name} - driver rejects keyed mutex QI");
        return new KeyedMutexRaw(p);
    }

    // Vortice.DXGI 3.3.4 does not map IDXGIResource::GetSharedHandle either -
    // declare it manually (IID from Win10 SDK dxgi.h).
    [ComImport, Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIResourceRaw
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr data);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
        [PreserveSig] int GetDevice(ref Guid riid, out IntPtr device);
        [PreserveSig] int GetUsage(out uint usage);
        [PreserveSig] int SetEvictionPriority(int priority);
        [PreserveSig] int GetEvictionPriority(out int priority);
        [PreserveSig] int GetSharedHandle(out IntPtr handle);
    }

    internal static IDXGIResourceRaw QIResourceRaw(SharpGen.Runtime.ComObject obj)
    {
        IntPtr unk = obj.NativePointer;
        var iid = new Guid("035f3ab4-482e-4e50-b41f-8a7f8bd8960b");
        Marshal.QueryInterface(unk, ref iid, out IntPtr p);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException(
                $"QueryInterface(IDXGIResource) FAILED on {obj.GetType().Name} - shared texture not a DXGI resource?");
        try { return (IDXGIResourceRaw) Marshal.GetObjectForIUnknown(p); }
        finally { Marshal.Release(p); }
    }

    private static D3D.ID3D11Device _dev;
    private static D3D.ID3D11DeviceContext _ctx;
    private static D3D.ID3D11ShaderResourceView _canvasSrv;
    private static D3D.ID3D11RenderTargetView _canvasRtv;
    private static D3D.ID3D11Texture2D _canvasTex2d;   // the raw canvas (for lane CopyResource)
    private static D3D.ID3D11VertexShader _vs;
    private static D3D.ID3D11PixelShader _ps;
    private static D3D.ID3D11InputLayout _layout;
    private static D3D.ID3D11Buffer _ib;
    private static D3D.ID3D11SamplerState _samplerLinear;
    private static D3D.ID3D11SamplerState _samplerPoint;
    private static int _canvasW, _canvasH;

    // The Pie-side blit shader as plain HLSL. POSITION is 2 floats - declaring
    // R32G32B32 here made position.z read uvX and left SV_Position.w undefined
    // (the monitor-2 distortion bug). The blend is baked in at compile time
    // and formatted with "0.0###" - HLSL rejects a bare "0f"/"1f" literal.
    internal static string BuildHlsl(float blend) => $@"
struct VI {{ float2 pos : POSITION; float2 uv : TEXCOORD0; }};
struct VO {{ float4 pos : SV_Position; float2 uv : TEXCOORD0; }};
VO VS(VI i) {{ VO o; o.pos = float4(i.pos, 0.0, 1.0); o.uv = i.uv; return o; }}
Texture2D uTexLinear : register(t0);
Texture2D uTexPoint  : register(t1);
SamplerState sLin : register(s0);
SamplerState sPnt : register(s1);
float4 PS(VO i) : SV_Target
{{
    float4 lin = uTexLinear.Sample(sLin, i.uv);
    float4 nn  = uTexPoint.Sample(sPnt, i.uv);
    return lerp(nn, lin, {blend.ToString("0.0###", CultureInfo.InvariantCulture)});
}}";

    // One-time setup on the owner window's device: pull the raw D3D11 device
    // and context out of the Pie wrapper, wrap the canvas texture, compile
    // the blit.
    internal static void EnsureShared(RenderWindow owner)
    {
        if (_dev != null) return;

        object pieDev = owner.PieDevice;
        _dev = (D3D.ID3D11Device) pieDev.GetType().GetField("_device",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pieDev);
        _ctx = (D3D.ID3D11DeviceContext) pieDev.GetType().GetField("_context",
            BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pieDev);

        object canvas = owner.CanvasTexture;
        var tex2d = (D3D.ID3D11Texture2D) canvas.GetType().GetField("Texture")!.GetValue(canvas);
        _canvasTex2d = tex2d;
        _canvasW = owner.CanvasW;
        _canvasH = owner.CanvasH;
        _canvasSrv = _dev.CreateShaderResourceView(tex2d);
        _canvasRtv = _dev.CreateRenderTargetView(tex2d);

        float blend = owner.ScaleBlend;
        string hlsl = BuildHlsl(blend);
        ReadOnlyMemory<byte> vsCode = Compiler.Compile(hlsl, "VS", "blit.hlsl", "vs_5_0");
        ReadOnlyMemory<byte> psCode = Compiler.Compile(hlsl, "PS", "blit.hlsl", "ps_5_0");
        _vs = _dev.CreateVertexShader(vsCode.ToArray(), null);
        _ps = _dev.CreatePixelShader(psCode.ToArray(), null);
        _layout = _dev.CreateInputLayout(new[]
        {
            new D3D.InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
            new D3D.InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
        }, vsCode.ToArray());

        _ib = _dev.CreateBuffer(new uint[] { 0u, 1u, 2u, 0u, 2u, 3u }, D3D.BindFlags.IndexBuffer);

        _samplerLinear = _dev.CreateSamplerState(D3D.SamplerDescription.LinearClamp);
        _samplerPoint = _dev.CreateSamplerState(D3D.SamplerDescription.PointClamp);

        Log.W($"CanvasBlit ready: canvas {_canvasW}x{_canvasH}, blend={blend:0.###} (raw D3D11 on shared device)");
        LogOwnerSwapchain(pieDev);
    }

    // Diagnostic: what model is the owner's own (Pie-managed) swapchain?
    private static void LogOwnerSwapchain(object pieDev)
    {
        try
        {
            var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
            foreach (var f in pieDev.GetType().GetFields(flags))
            {
                if (!f.Name.ToLowerInvariant().Contains("swap")) continue;
                object val = f.GetValue(pieDev);
                if (val == null) { Log.W($"Owner Pie field '{f.Name}' = null"); continue; }
                try
                {
                    using var s1 = ((SharpGen.Runtime.ComObject)val).QueryInterface<IDXGISwapChain1>();
                    SwapChainDescription1 d = s1.Description1;
                    Log.W($"Owner Pie swapchain '{f.Name}': effect={d.SwapEffect} buffers={d.BufferCount} flags={d.Flags}");
                }
                catch (Exception qe)
                {
                    Log.W($"Owner Pie field '{f.Name}' ({val.GetType().Name}) not a swapchain: {qe.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.W($"Owner swapchain introspection failed: {ex.Message}");
        }
    }

    // Plain DXGI swapchain for a non-owner window. Flip model + deep queue +
    // FRAME_LATENCY_WAITABLE_OBJECT (DXGI 1.3 latency model): the waitable
    // wait happens explicitly at frame start, Present stays a pure queue push.
    // AllowTearing enables independent flip (presents go straight to scanout).
    // Blt model: neither flag is supported; Scaling.Stretch is required for
    // windowed blt; windowed blt cannot tear anyway.
    internal static IDXGISwapChain1 CreateSwapchain(IntPtr hwnd, int w, int h,
        out IntPtr waitable, int buffers = 4, int latency = 3, D3D.ID3D11Device dev = null,
        bool blt = false)
    {
        D3D.ID3D11Device device = dev ?? _dev;
        using var dxgiDev = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDev.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();
        var desc = new SwapChainDescription1
        {
            Width = w,
            Height = h,
            Format = Format.B8G8R8A8_UNorm,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            // Caller must create one RTV per buffer and render into the
            // CURRENT one (GetBuffer(0)) - flip rotates buffers, a cached RTV
            // would present stale frames.
            BufferCount = buffers,
            Scaling = blt ? Scaling.Stretch : Scaling.None,
            SwapEffect = blt ? SwapEffect.Discard : SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = blt ? 0
                : SwapChainFlags.AllowTearing | SwapChainFlags.FrameLatencyWaitableObject,
        };
        var sc = factory.CreateSwapChainForHwnd(device, hwnd, desc);
        waitable = IntPtr.Zero;
        if (!blt)
        {
            try
            {
                using var sc2 = sc.QueryInterface<IDXGISwapChain2>();
                sc2.MaximumFrameLatency = latency;
                waitable = sc2.FrameLatencyWaitableObject;
            }
            catch (Exception ex)
            {
                Log.W($"Frame latency waitable object unavailable: {ex.Message}");
            }
        }
        return sc;
    }

    // One RTV per frame from GetBuffer(0): in flip-model swapchains buffer 0
    // ALWAYS refers to the current back buffer (buffers rotate underneath).
    internal static D3D.ID3D11RenderTargetView CreateCurrentBackbufferRtv(IDXGISwapChain1 swap, D3D.ID3D11Device dev = null)
    {
        using var tex = swap.GetBuffer<D3D.ID3D11Texture2D>(0);
        return (dev ?? _dev).CreateRenderTargetView(tex);
    }

    internal static D3D.ID3D11Buffer CreateVb(VPosUv[] verts)
    {
        return _dev.CreateBuffer(Array.ConvertAll(verts, v => v.Data), D3D.BindFlags.VertexBuffer);
    }
}
