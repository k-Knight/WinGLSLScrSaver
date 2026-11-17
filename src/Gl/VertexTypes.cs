namespace WinGLSLScrSaver;

using System.Numerics;

// Fullscreen-quad vertex: clip position (xy) + uv (zw) in one 16-byte
// Vector4, matching both the Pie input layout and the raw D3D11 blit layout.
internal struct VPosUv
{
    public Vector4 Data;
    public VPosUv(Vector2 pos, Vector2 uv) { Data = new Vector4(pos.X, pos.Y, uv.X, uv.Y); }
}
