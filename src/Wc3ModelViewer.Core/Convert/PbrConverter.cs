using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>The three StarCraft II-style maps derived from one Reforged PBR texture set.</summary>
public sealed class SpecularSet
{
    public required RgbaImage Diffuse { get; init; }
    public required RgbaImage Specular { get; init; }
    public RgbaImage? Normal { get; init; }
    public RgbaImage? Emissive { get; init; }
}

/// <summary>
/// Converts Reforged's metallic-roughness ("ORM") texture workflow into the specular workflow
/// StarCraft II shades with — the job the community pipeline did in Photoshop via reforged.jsx.
/// </summary>
/// <remarks>
/// Standard metallic→specular split with the dielectric F0 = 0.04 constant, tuned the way the
/// Heroes of the Storm sets are (the closest shipping m3 content):
/// <list type="bullet">
/// <item><b>Diffuse</b>: albedo with a quarter of the metallic part pulled out, and ambient
/// occlusion folded in lightly — SC2 has no AO input of its own. Physically a metal has no diffuse,
/// but SC2 has no image-based reflection to light it with, so an honest metal renders black; these
/// constants are set so an exported surface reads at the brightness the viewer shows, which is what
/// people compare it against.</item>
/// <item><b>Specular</b>: <c>lerp(0.04, albedo, metallic)</c> dimmed by roughness, since the m3
/// standard material has a single specular intensity rather than a gloss map.</item>
/// <item><b>Normal</b>: repacked to SC2's two-channel convention — X in alpha, Y in green — which
/// the engine reads regardless of container compression.</item>
/// </list>
/// The diffuse <b>alpha channel passes through untouched</b>: on an opaque Reforged material it is
/// the team-colour mask, on a blend material it is coverage, and both meanings must survive into
/// the exported file where the material's blend mode decides how it is read.
/// </remarks>
public static class PbrConverter
{
    public static SpecularSet Convert(RgbaImage diffuse, RgbaImage? normal, RgbaImage? orm, RgbaImage? emissive)
    {
        var (albedo, spec) = SplitMetallic(diffuse, orm);
        return new SpecularSet
        {
            Diffuse = albedo,
            Specular = spec,
            Normal = normal is null ? null : RepackNormal(normal),
            Emissive = emissive,
        };
    }

    private static (RgbaImage Diffuse, RgbaImage Specular) SplitMetallic(RgbaImage diffuse, RgbaImage? orm)
    {
        int w = diffuse.Width, h = diffuse.Height;
        var diffPx = new byte[w * h * 4];
        var specPx = new byte[w * h * 4];
        var src = diffuse.Pixels;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;

                float occlusion = 1, roughness = 0.7f, metallic = 0;
                if (orm is not null)
                {
                    int o = SampleIndex(orm, x, y, w, h);
                    occlusion = orm.Pixels[o + 2] / 255f;      // R
                    roughness = orm.Pixels[o + 1] / 255f;      // G
                    metallic = orm.Pixels[o] / 255f;           // B
                }

                float b = src[i] / 255f, g = src[i + 1] / 255f, r = src[i + 2] / 255f;

                // Diffuse: metals keep most of their tint, and occlusion is folded in only lightly.
                // Both constants were measured rather than chosen. Taking three quarters of the
                // albedo away on metal and multiplying by 0.5 + occlusion/2 left the knight's
                // pauldron at 21% of its Warcraft III brightness and his sword at 19% — the whole
                // metal half of every Reforged unit, which is what "exported models look really
                // dark, especially the metal" was. StarCraft II cannot pay that back: it has one
                // specular intensity and no image-based reflection, so a physically-correct metal
                // (no diffuse at all, lit only by reflection) simply reads black away from the
                // highlight. Reforged's albedo also already has ambient shading painted into it, so
                // the ORM's occlusion darkens a second time. `MdxProbe --pbr` prints these ratios.
                float ao = 0.85f + occlusion * 0.15f;
                float keep = 1 - metallic * 0.25f;
                diffPx[i] = Pack(b * keep * ao);
                diffPx[i + 1] = Pack(g * keep * ao);
                diffPx[i + 2] = Pack(r * keep * ao);
                diffPx[i + 3] = src[i + 3];                    // alpha passes through — mask or coverage

                // Specular: F0 for dielectrics, albedo-tinted for metals, dimmed by roughness. The
                // floor is what keeps a rough metal reading as metal rather than as painted stone —
                // the sheen users recognise as "the metallic look" is this, not the diffuse.
                float shine = (1 - roughness) * (1 - roughness);   // perceptual-ish falloff
                float sB = (0.04f + (b - 0.04f) * metallic) * (0.35f + shine * 0.65f);
                float sG = (0.04f + (g - 0.04f) * metallic) * (0.35f + shine * 0.65f);
                float sR = (0.04f + (r - 0.04f) * metallic) * (0.35f + shine * 0.65f);
                specPx[i] = Pack(sB);
                specPx[i + 1] = Pack(sG);
                specPx[i + 2] = Pack(sR);
                specPx[i + 3] = 255;
            }
        }

        return (new RgbaImage { Width = w, Height = h, Pixels = diffPx },
                new RgbaImage { Width = w, Height = h, Pixels = specPx });
    }

    /// <summary>SC2 samples normals from (alpha, green); X goes to A, Y to G, R/B zeroed.</summary>
    private static RgbaImage RepackNormal(RgbaImage normal)
    {
        var px = new byte[normal.Pixels.Length];
        var s = normal.Pixels;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0;                 // B unused
            px[i + 1] = s[i + 1];      // G = Y (already G after BC5 decode)
            px[i + 2] = 0;             // R unused
            px[i + 3] = s[i + 2];      // A = X (R after BC5 decode)
        }
        return new RgbaImage { Width = normal.Width, Height = normal.Height, Pixels = px };
    }

    /// <summary>Nearest sample for when the ORM is a different resolution than the diffuse.</summary>
    private static int SampleIndex(RgbaImage img, int x, int y, int refW, int refH)
    {
        int sx = refW == img.Width ? x : x * img.Width / refW;
        int sy = refH == img.Height ? y : y * img.Height / refH;
        return (sy * img.Width + sx) * 4;
    }

    private static byte Pack(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);
}
