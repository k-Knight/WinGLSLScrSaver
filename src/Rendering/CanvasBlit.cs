namespace WinGLSLScrSaver;

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using D3D = Vortice.Direct3D11;
using Vortice.D3DCompiler;
using Vortice.DXGI;

// Raw D3D11 machinery for the shared-canvas architecture (normal runs only).
// Window 0 hosts the Pie device and renders the shader once per frame into
// the canvas RT; EVERY window (window 0 included) then blits its crop-to-fit
// slice of that canvas with plain D3D11 calls on the SAME device, so the
// shader runs one time per frame no matter how many monitors there are.
// Pie 0.10.1 cannot create a second swapchain on a device, so windows 1..N
// get plain DXGI swapchains, and window 0 draws into the backbuffer texture
// of the Pie swapchain it already has. The primitive topology is left alone:
// the owner's Pie pass A sets TriangleList every frame before these draws.
internal partial class CanvasBlit
{
    // Vortice.DXGI does not map IDXGIKeyedMutex - declare it manually.
    // FIXED 2026-11-16: old GUID was a different dxgi interface, NOT IDXGIResource. Correct IID from Windows 10 SDK dxgi.h: MIDL_INTERFACE("9d8e1289-d7b3-465f-8126-250e349af85d").
    // Vtable: IDXGIObject (SetPrivateData, SetPrivateDataInterface,
    // GetPrivateData, GetParent) + IDXGIDeviceSubObject (GetDevice) +
    // AcquireSync + ReleaseSync. Return int = HRESULT (0 = S_OK).
    // Raw keyed-mutex access WITHOUT an RCW: GetObjectForIUnknown + cast does
    // a context QI that fails cross-thread (E_NOINTERFACE on the present
    // thread), so hold the raw interface pointer and call its vtable directly.
    // Vtable slots: IUnknown(0..2) + IDXGIObject(3..6) + GetDevice(7) +
    // AcquireSync(8) + ReleaseSync(9).
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
    // declare it manually. FIXED 2026-11-16: old GUID was a different dxgi interface, NOT IDXGIResource. Correct IID from Windows 10 SDK dxgi.h:
    // MIDL_INTERFACE("035f3ab4-482e-4e50-b41f-8a7f8bd8960b").
    // Vtable: IDXGIObject (4) + IDXGIDeviceSubObject (GetDevice) +
    // IDXGIResource (GetUsage, SetEvictionPriority, GetEvictionPriority,
    // GetSharedHandle).
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
    private static D3D.ID3D11Texture2D _canvasTex2d;   // the raw D3D11 canvas (for lane CopyResource)
    private static D3D.ID3D11VertexShader _vs;
    private static D3D.ID3D11PixelShader _ps;
    private static D3D.ID3D11InputLayout _layout;
    private static D3D.ID3D11Buffer _ib;
    private static D3D.ID3D11SamplerState _samplerLinear;
    private static D3D.ID3D11SamplerState _samplerPoint;
    private static int _canvasW, _canvasH;

    // Same blit as the Pie-side shader in RenderWindow, as plain HLSL. The
    // vertex layout matches Pie's VPosUv packing: float2 pos + float2 uv in
    // one Vector4 (16 bytes). POSITION is 2 floats - declaring R32G32B32 here
    // made position.z read uvX and left SV_Position.w undefined, which the
    // rasterizer divides by (the monitor-2 distortion bug). The blend is
    // baked in at compile time. NOTE: it is formatted with "0.0###" - both
    // GLSL and HLSL reject a bare "0f"/"1f" float literal.
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
    // and context out of the Pie wrapper (same reflection the diag modes
    // use), wrap the canvas texture, compile the blit.
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

    // One-time diagnostics: what model is the OWNER's own (Pie-managed)
    // swapchain? If it is legacy bitblt, the owner's Present is the one
    // path that still does a synchronous DWM-synced copy per frame and it
    // cannot get ALLOW_TEARING/waitable treatment from our side.
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

    // Plain DXGI swapchain for a non-owner window (Pie has no API for a
    // second swapchain on one device).
    // FRAME_LATENCY_WAITABLE_OBJECT + SetMaximumFrameLatency(3): moves any
    // "too many frames queued" blocking OUT of Present into a waitable
    // handle the loop checks at the start of each frame (Microsoft DXGI 1.3
    // latency model). With consumption (>=144 Hz) faster than production
    // (<=120 fps) the handle is always signaled, so waits return instantly
    // and Present becomes a pure queue push.
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
            // Flip model with TWO buffers: presents become cheap queue
            // operations (the old 1-buffer bitblt model did a synchronous
            // copy that stalled the CPU ~6ms/frame on DWM sync). DWM picks
            // each buffer up at THIS monitor's own vblank. The caller must
            // create one RTV per buffer and render into the CURRENT one
            // (IDXGISwapChain3.CurrentBackBufferIndex) - flip rotates
            // buffers, a single cached RTV would present stale frames.
            // 4 buffers (default): with 2, each Present(0) blocks until the
            // previous frame's flip completes at a vblank (buffer release
            // sync) - measured as a constant ~5.9ms stall per present. A
            // deeper queue lets presents return immediately while scanout
            // drains it. Configurable via swap_buffers (2..8) for the
            // "fake vsync with extra buffers" experiment.
            BufferCount = buffers,
            // Latency 3 (one below the 4-buffer queue): queued presents may
            // reach 3 before anything blocks, and that block is our explicit
            // waitable wait, not a hidden one inside Present.
            // Blt model (raw_swap_blt experiment): Scaling.None requires the
            // flip model; windowed blt needs Stretch. Buffer count comes from
            // the caller (1 or 2 - 1-buffer blt measured ~6ms release sync).
            Scaling = blt ? Scaling.Stretch : Scaling.None,
            SwapEffect = blt ? SwapEffect.Discard : SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            // AllowTearing: enables "independent flip" - presents go straight
            // to scanout without waiting on DWM's compose pass. Without it the
            // per-present DWM buffer-release sync cost ~6ms/frame on this
            // setup (one 170Hz compose period per Present(0, DO_NOT_WAIT)).
            // Blt model: neither flag is supported (AllowTearing throws,
            // waitable needs the flip queue); windowed blt cannot tear anyway
            // (DWM composes from its own copy of the buffer).
            Flags = blt ? 0
                : SwapChainFlags.AllowTearing | SwapChainFlags.FrameLatencyWaitableObject,
        };
        var sc = factory.CreateSwapChainForHwnd(device, hwnd, desc);
        waitable = IntPtr.Zero;
        if (!blt)
        {
            try
            {
                // Vortice exposes the DXGI 1.3 latency API as C# properties on
                // IDXGISwapChain2 (get_/set_MaximumFrameLatency and
                // get_FrameLatencyWaitableObject in the assembly metadata).
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

    // One RTV per frame, fetched from GetBuffer(0): in D3D11 flip-model
    // swapchains, buffer 0 ALWAYS refers to the current back buffer (the
    // runtime rotates buffers underneath), and GetBuffer(index > 0) is
    // invalid. Creating the RTV per frame costs microseconds - far below
    // the ~6ms/frame the old blt-model present used to stall.
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
