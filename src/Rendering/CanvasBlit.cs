namespace WinGLSLScrSaver;

using System;
using System.Globalization;
using System.Reflection;
using System.Numerics;
using D3D = Vortice.Direct3D11;
using Vortice.Mathematics;
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
internal static class CanvasBlit
{
    private static D3D.ID3D11Device _dev;
    private static D3D.ID3D11DeviceContext _ctx;
    private static D3D.ID3D11ShaderResourceView _canvasSrv;
    private static D3D.ID3D11RenderTargetView _canvasRtv;
    private static D3D.ID3D11VertexShader _vs;
    private static D3D.ID3D11PixelShader _ps;
    private static D3D.ID3D11InputLayout _layout;
    private static D3D.ID3D11Buffer _ib;
    private static D3D.ID3D11SamplerState _samplerLinear;
    private static D3D.ID3D11SamplerState _samplerPoint;
    private static int _canvasW, _canvasH;

    // Raw device/context access for diag 7 backbuffer readbacks.
    internal static D3D.ID3D11Device Dev => _dev;
    internal static D3D.ID3D11DeviceContext Ctx => _ctx;

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
        _canvasW = owner.CanvasW;
        _canvasH = owner.CanvasH;
        _canvasSrv = _dev.CreateShaderResourceView(tex2d);
        _canvasRtv = _dev.CreateRenderTargetView(tex2d);

        float blend = owner.ScaleBlend;
        ReadOnlyMemory<byte> vsCode = Compiler.Compile(BuildHlsl(blend), "VS", "blit.hlsl", "vs_5_0");
        ReadOnlyMemory<byte> psCode = Compiler.Compile(BuildHlsl(blend), "PS", "blit.hlsl", "ps_5_0");
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
    }

    // Plain DXGI swapchain for a non-owner window (Pie has no API for a
    // second swapchain on one device).
    internal static IDXGISwapChain1 CreateSwapchain(IntPtr hwnd, int w, int h)
    {
        using var dxgiDev = _dev.QueryInterface<IDXGIDevice>();
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
            // Bitblt model with ONE buffer: GetBuffer(0) is always the one
            // true backbuffer, so the cached RTV stays valid across Presents
            // (flip-model buffer rotation would silently break it).
            BufferCount = 1,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.Discard,
            AlphaMode = AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };
        return factory.CreateSwapChainForHwnd(_dev, hwnd, desc);
    }

    internal static D3D.ID3D11RenderTargetView CreateBackbufferRtv(IDXGISwapChain1 swap)
    {
        using var tex = swap.GetBuffer<D3D.ID3D11Texture2D>(0);
        return _dev.CreateRenderTargetView(tex);
    }

    internal static D3D.ID3D11Buffer CreateVb(VPosUv[] verts)
    {
        return _dev.CreateBuffer(Array.ConvertAll(verts, v => v.Data), D3D.BindFlags.VertexBuffer);
    }

    // Draw this window's slice of the canvas into its own swapchain, then put
    // the device back into the state the Pie shader pass expects (OM bound to
    // the canvas RT): Pie may skip rebinding state it believes is still set,
    // and raw draws have changed it behind its back.
    internal static void Draw(RenderWindow w)
    {
        _ctx.OMSetRenderTargets(w.RawRtv, null);
        _ctx.RSSetViewport(0f, 0f, w.RawW, w.RawH);
        _ctx.IASetInputLayout(_layout);
        _ctx.IASetVertexBuffer(0, w.RawVb, 16, 0);
        _ctx.IASetIndexBuffer(_ib, Format.R32_UInt, 0);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.PSSetShaderResources(0, new[] { _canvasSrv, _canvasSrv });
        _ctx.PSSetSamplers(0, new[] { _samplerLinear, _samplerPoint });
        if (w.SliceEmpty)
            _ctx.ClearRenderTargetView(w.RawRtv, new Color4(0f, 0f, 0f, 1f));
        else
            _ctx.DrawIndexed(6, 0, 0);

        _ctx.OMSetRenderTargets(_canvasRtv, null);
        _ctx.RSSetViewport(0f, 0f, _canvasW, _canvasH);
    }
}
