using System.Numerics;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>
/// The card a PopcornFX sprite draws as, for anything that turns sprites into quads: the viewer's
/// meshes and the impostor baker's rasteriser build the same corners from the same rule.
/// </summary>
public static class PkSpriteGeometry
{
    /// <summary>
    /// The card's half-extent vectors: its corners are <c>centre ± r ± u</c>, and the texture cell
    /// maps (u0,v1) to <c>centre − r − u</c>, (u1,v1) to <c>+r − u</c>, (u1,v0) to <c>+r + u</c> and
    /// (u0,v0) to <c>−r + u</c>.
    /// </summary>
    /// <param name="right">Camera right, unit.</param>
    /// <param name="up">Camera up, unit.</param>
    /// <param name="look">Camera look, <c>Cross(up, right)</c>.</param>
    public static void Quad(in PkSprite s, PopcornBillboardMode mode, Vector3 right, Vector3 up, Vector3 look,
                            out Vector3 r, out Vector3 u)
    {
        switch (mode)
        {
            case PopcornBillboardMode.AxisAligned:
            case PopcornBillboardMode.AxisAlignedSpheroid:
            case PopcornBillboardMode.AxisAlignedCapsule:
            {
                // A card along its axis, turned about it to face the camera; the spheroid and
                // capsule modes round the ends off with the radius.
                var axis = s.Axis;
                float len = axis.Length();
                var dir = len > 1e-5f ? axis / len : Vector3.UnitZ;
                var side = Vector3.Cross(dir, look);
                side = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) : right;
                r = side * s.HalfSize.X;
                u = dir * (len * 0.5f + (mode == PopcornBillboardMode.AxisAligned ? 0 : s.HalfSize.X));
                break;
            }
            case PopcornBillboardMode.PlaneAligned:
            {
                // A card lying in the plane of its normal, its up along the axis projected into it.
                var n = s.Normal.LengthSquared() > 1e-8f ? Vector3.Normalize(s.Normal) : Vector3.UnitZ;
                var a = s.Axis - n * Vector3.Dot(s.Axis, n);
                if (a.LengthSquared() < 1e-8f) a = MathF.Abs(n.Z) < 0.9f ? Vector3.Cross(n, Vector3.UnitZ) : Vector3.Cross(n, Vector3.UnitX);
                a = Vector3.Normalize(a);
                var side = Vector3.Normalize(Vector3.Cross(a, n));
                float cos = MathF.Cos(s.Rotation), sin = MathF.Sin(s.Rotation);
                r = (side * cos + a * sin) * s.HalfSize.X;
                u = (a * cos - side * sin) * s.HalfSize.Y;
                break;
            }
            default:
            {
                // Screen- or viewpos-aligned: in the camera plane, turned by the sprite's rotation.
                float cos = MathF.Cos(s.Rotation), sin = MathF.Sin(s.Rotation);
                r = (right * cos + up * sin) * s.HalfSize.X;
                u = (up * cos - right * sin) * s.HalfSize.Y;
                break;
            }
        }
    }

    /// <summary>The sheet cell a frame index picks, row-major from the top left: (u0, v0, u1, v1).</summary>
    public static (float U0, float V0, float U1, float V1) CellUv(int frame, int cols, int rows)
    {
        cols = Math.Max(1, cols); rows = Math.Max(1, rows);
        int cells = cols * rows;
        frame %= cells;
        if (frame < 0) frame += cells;
        float u0 = (frame % cols) / (float)cols, v0 = (frame / cols) / (float)rows;
        return (u0, v0, u0 + 1f / cols, v0 + 1f / rows);
    }
}
