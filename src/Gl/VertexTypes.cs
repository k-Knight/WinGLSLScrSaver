namespace WinGLSLScrSaver;

using System.Numerics;

// Shared fullscreen-quad vertex format: clip position (xy) + uv (zw) packed
// in one Vector4 - 16 bytes, matching both the Pie input layout
// (R32G32B32A32_Float) and the raw D3D11 blit layout (float2 pos + float2 uv).
internal struct VPosUv
{
    public Vector4 Data;
    public VPosUv(Vector2 pos, Vector2 uv) { Data = new Vector4(pos.X, pos.Y, uv.X, uv.Y); }
}