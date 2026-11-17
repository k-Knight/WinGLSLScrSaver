namespace WinGLSLScrSaver;

using System.Runtime.InteropServices;
using D3D = Vortice.Direct3D11;
using Vortice.Mathematics;
using Vortice.D3DCompiler;
using Vortice.DXGI;

// Partial of CanvasBlit: the present-lane machinery (present_lanes=2) -
// second D3D11 device, keyed-mutex shared canvas, LaneCopy/LaneDraw.

internal partial class CanvasBlit
{
    // ---- Present lanes (present_lanes=2) ----------------------------------
    // One D3D11 device is ONE present lane: Present calls on a device
    // serialize at DWM composition granularity (~5.9ms each), capping
    // multi-window setups around 83 fps. A second device on the same adapter
    // is a second, fully parallel present lane for the non-owner windows.
    // The canvas crosses devices through a keyed-mutex shared texture:
    // device1 copies the rendered canvas into it once per frame
    // (Acquire 0 -> CopyResource -> Release 1), each lane device acquires
    // the same mutex, blits its slice and presents - all on its own thread.
    private static D3D.ID3D11Device _laneDev;
    private static D3D.ID3D11DeviceContext _laneCtx;
    private static D3D.ID3D11Texture2D _sharedTex;      // created on device1
    private static KeyedMutexRaw _ownerMutex;           // device1 side of the mutex
    private static KeyedMutexRaw _laneMutex;            // device2 side
    private static D3D.ID3D11ShaderResourceView _laneSrv;
    private static D3D.ID3D11VertexShader _laneVs;
    private static D3D.ID3D11PixelShader _lanePs;
    private static D3D.ID3D11InputLayout _laneLayout;
    private static D3D.ID3D11Buffer _laneIb;
    private static D3D.ID3D11SamplerState _laneSamplerLinear;
    private static D3D.ID3D11SamplerState _laneSamplerPoint;
    private static bool _sharedFrameCopied;             // one copy per loop tick
    private static bool _laneFailed;                    // lane init failed -> disabled

    internal static D3D.ID3D11Device LaneDev => _laneDev;

    // Main thread (device1 ctx), once per tick, ordered after the owner's
    // pass A draw. Finite Acquire timeout: if a lane device still holds the
    // mutex, skip this tick's copy (lane shows the previous canvas copy).
    internal static void LaneCopy()
    {
        if (_laneFailed || _sharedTex == null) return;
        if (_sharedFrameCopied) return;
        _sharedFrameCopied = true;
        if (_ownerMutex.AcquireSync(0, 16) != 0)
            return;
        _ctx.CopyResource(_sharedTex, _canvasTex2d);
        _ctx.Flush();
        _ownerMutex.ReleaseSync(0);
    }

    // Owner calls this at the start of every tick's pass A.
    internal static void BeginFrame() => _sharedFrameCopied = false;

    // One-time: lane device + shared canvas + lane blit pipeline. Fail-safe:
    // any failure is logged and lanes are disabled (fallback to same-device
    // presents) instead of killing the screensaver. Capability probe FIRST:
    // some drivers do not expose IDXGIResource on a MISC_SHARED_KEYEDMUTEX
    // texture; the probe uses a tiny throwaway texture so a failed init never
    // leaves half-built lane COM objects behind (they once raced the fallback
    // path and caused a native AV).
    internal static bool EnsureLaneDevice(RenderWindow owner)
    {
        if (_laneDev != null) return true;
        if (_laneFailed) return false;
        try
        {
            var probeDesc = new D3D.Texture2DDescription
            {
                Width = 4, Height = 4, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = D3D.ResourceUsage.Default,
                BindFlags = D3D.BindFlags.ShaderResource,
                CPUAccessFlags = D3D.CpuAccessFlags.None,
                MiscFlags = D3D.ResourceOptionFlags.SharedKeyedMutex,
            };
            using (var probe = _dev.CreateTexture2D(probeDesc))
            {
                var pres = QIResourceRaw(probe); // throws on unsupported drivers
                pres.GetSharedHandle(out _);
                Marshal.FinalReleaseComObject((object) pres);
            }

            using var dxgiDev = _dev.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDev.GetAdapter();
            SharpGen.Runtime.Result r = D3D.D3D11.D3D11CreateDevice(adapter,
                Vortice.Direct3D.DriverType.Unknown, D3D.DeviceCreationFlags.BgraSupport,
                new[] { Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0 },
                out _laneDev);
            if (r.Failure)
                throw new InvalidOperationException($"Lane device creation failed: {r.Code}");
            _laneCtx = _laneDev.ImmediateContext;

            // Shared canvas texture on device1, opened on device2. Same
            // dimensions/format as the Pie canvas so CopyResource is valid.
            var cd = _canvasTex2d.Description;
            Log.W($"Canvas desc for sharing: {cd.Width}x{cd.Height} fmt={cd.Format} mip={cd.MipLevels} arr={cd.ArraySize} msaa={cd.SampleDescription.Count}/{cd.SampleDescription.Quality} bind={cd.BindFlags} misc={cd.MiscFlags} usage={cd.Usage}");
            var sdesc = cd;
            sdesc.MiscFlags = D3D.ResourceOptionFlags.SharedKeyedMutex | D3D.ResourceOptionFlags.SharedNTHandle;
            _sharedTex = _dev.CreateTexture2D(sdesc);
            _ownerMutex = QIKeyedMutex(_sharedTex);
            D3D.ID3D11Texture2D laneTex;
            {
                using var res1 = _sharedTex.QueryInterface<Vortice.DXGI.IDXGIResource1>();
                IntPtr handle = res1.CreateSharedHandle(null, Vortice.DXGI.SharedResourceFlags.Read | Vortice.DXGI.SharedResourceFlags.Write, null);
                Log.W($"CreateSharedHandle -> handle={handle}");
                if (handle == IntPtr.Zero)
                    throw new InvalidOperationException("CreateSharedHandle returned null handle");
                using var laneDev1 = _laneDev.QueryInterface<D3D.ID3D11Device1>();
                laneTex = laneDev1.OpenSharedResource1<D3D.ID3D11Texture2D>(handle);
                Log.W("OpenSharedResource1 OK");
            }


            _laneMutex = QIKeyedMutex(laneTex);
            _laneSrv = _laneDev.CreateShaderResourceView(laneTex);

            // Same blit pipeline as device1, compiled/created on device2.
            float blend = owner.ScaleBlend;
            string hlsl = BuildHlsl(blend);
            ReadOnlyMemory<byte> vsCode = Compiler.Compile(hlsl, "VS", "blit.hlsl", "vs_5_0");
            ReadOnlyMemory<byte> psCode = Compiler.Compile(hlsl, "PS", "blit.hlsl", "ps_5_0");
            _laneVs = _laneDev.CreateVertexShader(vsCode.ToArray(), null);
            _lanePs = _laneDev.CreatePixelShader(psCode.ToArray(), null);
            _laneLayout = _laneDev.CreateInputLayout(new[]
            {
                new D3D.InputElementDescription("POSITION", 0, Format.R32G32_Float, 0, 0),
                new D3D.InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
            }, vsCode.ToArray());
            _laneIb = _laneDev.CreateBuffer(new uint[] { 0u, 1u, 2u, 0u, 2u, 3u }, D3D.BindFlags.IndexBuffer);
            _laneSamplerLinear = _laneDev.CreateSamplerState(D3D.SamplerDescription.LinearClamp);
            _laneSamplerPoint = _laneDev.CreateSamplerState(D3D.SamplerDescription.PointClamp);

            Log.W($"Present lane device ready (adapter {adapter.Description.Description}), shared canvas {cd.Width}x{cd.Height} via keyed mutex");
            return true;
        }
        catch (Exception ex)
        {
            Log.Ex("EnsureLaneDevice", ex);
            Log.W("Present lanes DISABLED - falling back to same-device presents");
            _laneFailed = true;
            CleanupLanePartials();
            return false;
        }
    }

    // Dispose everything a partial lane init may have created, BEFORE the
    // fallback path starts presenting (no dangling COM objects race it).
    private static void CleanupLanePartials()
    {
        try { _laneSamplerLinear?.Dispose(); } catch { }
        try { _laneSamplerPoint?.Dispose(); } catch { }
        try { _laneIb?.Dispose(); } catch { }
        try { _laneLayout?.Dispose(); } catch { }
        try { _lanePs?.Dispose(); } catch { }
        try { _laneVs?.Dispose(); } catch { }
        try { _laneSrv?.Dispose(); } catch { }
        try { _laneMutex?.Dispose(); } catch { }
        try { _sharedTex?.Dispose(); } catch { }
        try { _ownerMutex?.Dispose(); } catch { }
        try { _laneCtx?.Dispose(); } catch { }
        try { _laneDev?.Dispose(); } catch { }
        _laneSamplerLinear = null;
        _laneSamplerPoint = null;
        _laneIb = null;
        _laneLayout = null;
        _lanePs = null;
        _laneVs = null;
        _laneSrv = null;
        _sharedTex = null;
        _laneMutex = _ownerMutex = null;
        _laneCtx = null;
        _laneDev = null;
    }

    internal static D3D.ID3D11Buffer LaneCreateVb(VPosUv[] verts)
    {
        return _laneDev.CreateBuffer(Array.ConvertAll(verts, v => v.Data), D3D.BindFlags.VertexBuffer);
    }

    // Present thread (device2): acquire mutex, blit this window's slice from
    // the shared canvas into its own swapchain, release, present. Returns
    // false when the mutex was busy - the caller skips this tick's present.
    internal static bool LaneDraw(RenderWindow w)
    {
        if (_laneFailed || _laneMutex == null) return false;
        if (_laneMutex.AcquireSync(0, 50) != 0)
            return false;
        using var rtv = CreateCurrentBackbufferRtv(w.LaneSwap, _laneDev);
        _laneCtx.OMSetRenderTargets(rtv, null);
        _laneCtx.RSSetViewport(0f, 0f, w.RawW, w.RawH);
        _laneCtx.IASetInputLayout(_laneLayout);
        _laneCtx.IASetVertexBuffer(0, w.LaneVb, 16, 0);
        _laneCtx.IASetIndexBuffer(_laneIb, Format.R32_UInt, 0);
        _laneCtx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);
        _laneCtx.VSSetShader(_laneVs);
        _laneCtx.PSSetShader(_lanePs);
        _laneCtx.PSSetShaderResources(0, new[] { _laneSrv, _laneSrv });
        _laneCtx.PSSetSamplers(0, new[] { _laneSamplerLinear, _laneSamplerPoint });
        if (w.SliceEmpty)
            _laneCtx.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));
        else
            _laneCtx.DrawIndexed(6, 0, 0);
        _laneCtx.Flush();
        _laneMutex.ReleaseSync(0);
        return true;
    }

    // Draw this window's slice of the canvas into its own swapchain, then put
    // the device back into the state the Pie shader pass expects (OM bound to
    // the canvas RT) - Pie may skip rebinding state it believes is still set.
    internal static void Draw(RenderWindow w)
    {
        // The present thread may be inside Present()/GetBuffer on this very
        // swapchain right now - touching it concurrently is the 1-in-5 launch
        // AV. Wait briefly, then SKIP this tick (one stale frame).
        if (w.WaitForPresentIdle(8)) return;

        // Flip model: the current back buffer must be re-queried every frame.
        using var rtv = CreateCurrentBackbufferRtv(w.RawSwap);
        _ctx.OMSetRenderTargets(rtv, null);
        _ctx.RSSetViewport(0f, 0f, w.RawW, w.RawH);
        _ctx.IASetInputLayout(_layout);
        _ctx.IASetVertexBuffer(0, w.RawVb, 16, 0);
        _ctx.IASetIndexBuffer(_ib, Format.R32_UInt, 0);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResources(0, new[] { _canvasSrv, _canvasSrv });
        _ctx.PSSetSamplers(0, new[] { _samplerLinear, _samplerPoint });
        if (w.SliceEmpty)
            _ctx.ClearRenderTargetView(rtv, new Color4(0f, 0f, 0f, 1f));
        else
            _ctx.DrawIndexed(6, 0, 0);

        _ctx.OMSetRenderTargets(_canvasRtv, null);
        _ctx.RSSetViewport(0f, 0f, _canvasW, _canvasH);
    }
}
