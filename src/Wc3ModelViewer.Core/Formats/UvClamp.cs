using System.Numerics;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// Clamp-to-edge sampling for a renderer that can only repeat a texture — WPF's <c>ImageBrush</c>
/// has <c>TileMode.Tile</c> and <c>None</c> (transparent outside), and nothing that holds the edge.
/// </summary>
/// <remarks>
/// Clamping cannot be done on the UVs alone: a triangle running from u = 0.5 to u = 1.28 must show
/// the right half of the texture over 64% of its width and the edge column over the rest, and
/// pinning the vertex at 1 would instead stretch that half across the whole face — a glow ring
/// mapped wider than its card (Thunder Clap, War Stomp) would draw 1.56 times too large. So the
/// texture is padded with copies of its edge texels out to the range the UVs actually reach, and
/// the UVs are moved affinely into the padded image, which leaves interpolation untouched.
/// <para>
/// The padding is bounded. A handful of classic geosets carry a stray vertex hundreds of textures
/// away (a Magnataur body vertex at u = 120), whose triangle is edge colour almost end to end;
/// beyond <see cref="MaxReach"/> such a vertex is pinned instead, which only shifts where along that
/// sliver the texture ends.
/// </para>
/// </remarks>
public sealed class UvClamp
{
    /// <summary>How far past each edge, in texture widths, the padding reaches before UVs are pinned.</summary>
    public const float MaxReach = 3f;

    /// <summary>The largest padded side, in texels, before the padding stops following the source's density.</summary>
    public const int MaxSide = 4096;

    private readonly bool _clampU, _clampV;
    private readonly float _loU, _hiU, _loV, _hiV;

    private UvClamp(bool clampU, float loU, float hiU, bool clampV, float loV, float hiV)
    {
        (_clampU, _loU, _hiU, _clampV, _loV, _hiV) = (clampU, loU, hiU, clampV, loV, hiV);
    }

    /// <summary>
    /// The remap for a geoset's UVs, or null when every UV already lies inside the texture on each
    /// axis that clamps — the common case, and then nothing needs to change.
    /// </summary>
    /// <param name="width">Width of the texture the padding is sized for, in texels.</param>
    /// <param name="height">Height of that texture.</param>
    public static UvClamp? For(Vector2[] uvs, bool wrapU, bool wrapV, int width, int height)
    {
        if (uvs.Length == 0 || (wrapU && wrapV)) return null;
        float minU = 0, maxU = 1, minV = 0, maxV = 1;
        foreach (var uv in uvs)
        {
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y)) continue;
            minU = Math.Min(minU, uv.X); maxU = Math.Max(maxU, uv.X);
            minV = Math.Min(minV, uv.Y); maxV = Math.Max(maxV, uv.Y);
        }
        var (loU, hiU) = Reach(minU, maxU, width);
        var (loV, hiV) = Reach(minV, maxV, height);
        bool clampU = !wrapU && (loU < 0 || hiU > 1);
        bool clampV = !wrapV && (loV < 0 || hiV > 1);
        return clampU || clampV ? new UvClamp(clampU, loU, hiU, clampV, loV, hiV) : null;
    }

    /// <summary>
    /// The span one axis is padded to: the UVs' reach, capped at <see cref="MaxReach"/> and at what
    /// fits in <see cref="MaxSide"/> texels, then widened to whole texels so the source grid lands
    /// exactly on the padded one and the interior is copied, not resampled.
    /// </summary>
    private static (float Lo, float Hi) Reach(float min, float max, int size)
    {
        float lo = Math.Max(min, -MaxReach), hi = Math.Min(max, 1 + MaxReach);
        float budget = Math.Max(0, (float)MaxSide / Math.Max(size, 1) - 1);
        float over = -lo + (hi - 1);
        if (over > budget)
        {
            float k = budget / over;
            lo *= k;
            hi = 1 + (hi - 1) * k;
        }
        return (MathF.Floor(lo * size) / size, MathF.Ceiling(hi * size) / size);
    }

    /// <summary>A UV moved into the padded texture.</summary>
    public Vector2 Map(Vector2 uv) => new(
        _clampU ? (Math.Clamp(uv.X, _loU, _hiU) - _loU) / (_hiU - _loU) : uv.X,
        _clampV ? (Math.Clamp(uv.Y, _loV, _hiV) - _loV) / (_hiV - _loV) : uv.Y);

    /// <summary>
    /// <paramref name="image"/> padded with its own edge texels to the span <see cref="Map"/> assumes.
    /// Any image works, not only the one the remap was sized for, so every frame of a flipbook can
    /// share one set of mapped UVs.
    /// </summary>
    public RgbaImage Apply(RgbaImage image)
    {
        int w = image.Width, h = image.Height;
        int pw = _clampU ? Math.Clamp((int)MathF.Round(w * (_hiU - _loU)), 1, MaxSide) : w;
        int ph = _clampV ? Math.Clamp((int)MathF.Round(h * (_hiV - _loV)), 1, MaxSide) : h;

        // Source column for each padded column, and row for each padded row: nearest texel of the
        // clamped coordinate, so the copied interior is exact and the border repeats the edge.
        var sx = new int[pw];
        for (int x = 0; x < pw; x++)
            sx[x] = _clampU ? Math.Clamp((int)MathF.Floor((_loU + (x + 0.5f) / pw * (_hiU - _loU)) * w), 0, w - 1) : x;
        var px = new byte[pw * ph * 4];
        for (int y = 0; y < ph; y++)
        {
            int sy = _clampV ? Math.Clamp((int)MathF.Floor((_loV + (y + 0.5f) / ph * (_hiV - _loV)) * h), 0, h - 1) : y;
            for (int x = 0; x < pw; x++)
                Buffer.BlockCopy(image.Pixels, (sy * w + sx[x]) * 4, px, (y * pw + x) * 4, 4);
        }
        return new RgbaImage { Width = pw, Height = ph, Pixels = px };
    }
}
