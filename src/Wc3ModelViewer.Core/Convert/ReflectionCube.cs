using System.Numerics;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>
/// Turns Warcraft III's image-based lighting into the cube map a StarCraft II envi layer reads, so an
/// exported unit reflects the sky Warcraft III lights it with, turned to StarCraft II's sun.
/// </summary>
/// <remarks>
/// Both halves were read rather than assumed.
/// <list type="bullet">
/// <item><b>Warcraft III</b> (its HD pixel shader, <c>shaders\ps\hd.bls</c>, disassembled): the
/// specular IBL is element 1 of a DX10 cube array, sampled by the reflected ray at mip
/// <c>roughness x constant</c>. The ray first goes Y-up, <c>(x, z, -y)</c>, and is then expressed in a
/// frame built on the sun: +Z is the direction to the sun, +X is <c>up x sun</c>, +Y is <c>sun x X</c>.
/// The environment turns with the light, so its bright sky always sits behind the sun.</item>
/// <item><b>StarCraft II</b> (the <c>MdxProbe --envical</c> spheres): an envi layer with uv_source 2
/// looks the cube up by the world-space reflected ray itself, faces in D3D order with +Z up. A cube
/// whose texels were coloured by their own direction read back as the identity, and its fit put the
/// Agria sun where the lightdata puts it.</item>
/// </list>
/// </remarks>
public static class ReflectionCube
{
    /// <summary>
    /// Direction to the sun in Agria, the daylight the SC2 checks are made under: the negated Key
    /// light of Liberty's lightdata.xml (0.724693, -0.124265, -0.677775 is the way the light travels),
    /// confirmed by the sphere highlights. Warcraft III aligns its environment to its own sun; a unit
    /// cannot know a map's light, so the export aligns to this one.
    /// </summary>
    public static readonly Vector3 AgriaSun = Vector3.Normalize(new Vector3(-0.724693f, 0.124265f, 0.677775f));

    /// <summary>
    /// Warcraft III's daytime sky. The HD shader reads whichever IBL the map's lighting binds, and the
    /// day/night models name none (they carry no textures), so the choice is made in code; of the
    /// six the game ships — Lordaeron Summer day/night/water, Northrend sunset, dungeon night,
    /// portraits — Lordaeron Summer day is the one an outdoor daytime unit is lit by.
    /// </summary>
    public const string WarcraftDaySky = @"war3.w3mod:_hd.w3mod:environment\environmentmap\lordaeronsummer\day_ibl.dds";

    /// <summary>
    /// layer_envi colour_multiply for <see cref="Warcraft"/>'s cube. Chosen on the DE knight against
    /// the World Editor's render, bind pose, one spot, one camera — the summed gap of its gold and
    /// steel brightness percentiles: 0.326 today, 0.314 at x1, 0.126 at x1.3, 0.128 at x1.5, 0.161
    /// at x1.7, 0.188 at x2 (gold best at 1.3, steel at 1.5). StarCraft II applies the multiply as
    /// its square in texel value (x0.5 on grey 128 displays exactly as grey 32).
    /// </summary>
    public const float WarcraftMultiply = 1.4f;

    private static readonly object CacheLock = new();
    private static (byte[] Hash, ReflectionMap Map)? _cached;

    /// <summary>
    /// The reflection every exported Reforged material gets: <see cref="WarcraftDaySky"/> at 256px
    /// (WC3 mip k+1 in mip k, the same texel size) turned to <see cref="AgriaSun"/>, built once per
    /// sky. Null when the storage has no such file, e.g. a loose model opened without the game.
    /// </summary>
    public static ReflectionMap? Warcraft(Casc.Wc3TextureCache textures)
    {
        var ibl = textures.TryReadGameFile(WarcraftDaySky);
        if (ibl is null) return null;
        var hash = System.Security.Cryptography.SHA256.HashData(ibl);
        lock (CacheLock)
        {
            if (_cached is { } c && c.Hash.AsSpan().SequenceEqual(hash)) return c.Map;
            var map = new ReflectionMap
            {
                Cube = FromWarcraftIbl(ibl, 256, AgriaSun, k => (k + 1) / (float)(DdsReader.MipCount(ibl) - 1)),
                Name = "wc3_reflection",
                Multiply = WarcraftMultiply,
            };
            _cached = (hash, map);
            return map;
        }
    }

    /// <summary>
    /// A cube of <paramref name="faceSize"/> px whose mip k holds Warcraft III's specular IBL at the
    /// roughness <paramref name="roughnessForMip"/>(k) — a prefiltered chain, not a downsample, so
    /// StarCraft II's gloss-driven mip choice (simulate_roughness) lands on Warcraft III's own blur.
    /// </summary>
    public static byte[] FromWarcraftIbl(byte[] iblDds, int faceSize, Vector3 sunToward, Func<int, float> roughnessForMip)
    {
        int wc3Mips = DdsReader.MipCount(iblDds);
        // Element 1 (surfaces 6-11) is the specular; element 0 the irradiance.
        var src = new RgbaImage[6][];
        for (int f = 0; f < 6; f++)
        {
            src[f] = new RgbaImage[wc3Mips];
            for (int m = 0; m < wc3Mips; m++) src[f][m] = DdsReader.DecodeSurface(iblDds, 6 + f, m);
        }

        // Warcraft III's sun frame, in its Y-up space.
        var s = Vector3.Normalize(YUp(sunToward));
        var x = Vector3.Cross(Vector3.UnitY, s);
        x = x.LengthSquared() < 1e-4f ? Vector3.UnitX : Vector3.Normalize(x);
        var y = Vector3.Normalize(Vector3.Cross(s, x));

        int levels = 1 + (int)Math.Log2(faceSize);
        var chains = new List<IReadOnlyList<RgbaImage>>();
        for (int face = 0; face < 6; face++)
        {
            var chain = new List<RgbaImage>();
            for (int k = 0; k < levels; k++)
            {
                int size = Math.Max(1, faceSize >> k);
                float lod = Math.Clamp(roughnessForMip(k), 0f, 1f) * (wc3Mips - 1);
                var px = new byte[size * size * 4];
                for (int ty = 0; ty < size; ty++)
                    for (int tx = 0; tx < size; tx++)
                    {
                        var w = Direction(face, 2f * (tx + 0.5f) / size - 1, 2f * (ty + 0.5f) / size - 1);
                        var r = YUp(w);
                        var d = new Vector3(Vector3.Dot(r, x), Vector3.Dot(r, y), Vector3.Dot(r, s));
                        var c = SampleTrilinear(src, d, lod);
                        int o = (ty * size + tx) * 4;
                        px[o] = Pack(c.Z); px[o + 1] = Pack(c.Y); px[o + 2] = Pack(c.X); px[o + 3] = 255;
                    }
                chain.Add(new RgbaImage { Width = size, Height = size, Pixels = px });
            }
            chains.Add(chain);
        }
        return DdsWriter.WriteCube(chains);
    }

    /// <summary>Z-up world to Warcraft III's Y-up IBL space, as its shader swizzles (x, z, -y).</summary>
    private static Vector3 YUp(Vector3 v) => new(v.X, v.Z, -v.Y);

    /// <summary>D3D cube face directions: face 0..5 = +X, -X, +Y, -Y, +Z, -Z; s, t in -1..1 across and down the face.</summary>
    public static Vector3 Direction(int face, float s, float t) => Vector3.Normalize(face switch
    {
        0 => new Vector3(1, -t, -s),
        1 => new Vector3(-1, -t, s),
        2 => new Vector3(s, 1, t),
        3 => new Vector3(s, -1, -t),
        4 => new Vector3(s, -t, 1),
        _ => new Vector3(-s, -t, -1),
    });

    /// <summary>The inverse of <see cref="Direction"/>: face and 0..1 texture coordinates.</summary>
    public static (int Face, float U, float V) Locate(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        int face; float sc, tc, ma;
        if (ax >= ay && ax >= az) { ma = ax; face = d.X > 0 ? 0 : 1; sc = d.X > 0 ? -d.Z : d.Z; tc = -d.Y; }
        else if (ay >= az) { ma = ay; face = d.Y > 0 ? 2 : 3; sc = d.X; tc = d.Y > 0 ? d.Z : -d.Z; }
        else { ma = az; face = d.Z > 0 ? 4 : 5; sc = d.Z > 0 ? d.X : -d.X; tc = -d.Y; }
        return (face, (sc / ma + 1) * 0.5f, (tc / ma + 1) * 0.5f);
    }

    private static Vector3 SampleTrilinear(RgbaImage[][] faces, Vector3 d, float lod)
    {
        int lo = (int)MathF.Floor(lod), hi = Math.Min(lo + 1, faces[0].Length - 1);
        float t = lod - lo;
        var (f, u, v) = Locate(d);
        var a = Bilinear(faces[f][Math.Min(lo, faces[0].Length - 1)], u, v);
        return t <= 0 || hi == lo ? a : Vector3.Lerp(a, Bilinear(faces[f][hi], u, v), t);
    }

    /// <summary>Bilinear within one face, clamped at its edges (seams are a texel wide at most).</summary>
    private static Vector3 Bilinear(RgbaImage img, float u, float v)
    {
        float fx = Math.Clamp(u * img.Width - 0.5f, 0, img.Width - 1), fy = Math.Clamp(v * img.Height - 0.5f, 0, img.Height - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
        float tx = fx - x0, ty = fy - y0;
        Vector3 P(int px, int py) { int o = (py * img.Width + px) * 4; return new Vector3(img.Pixels[o + 2], img.Pixels[o + 1], img.Pixels[o]) / 255f; }
        return Vector3.Lerp(Vector3.Lerp(P(x0, y0), P(x1, y0), tx), Vector3.Lerp(P(x0, y1), P(x1, y1), tx), ty);
    }

    private static byte Pack(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);
}
