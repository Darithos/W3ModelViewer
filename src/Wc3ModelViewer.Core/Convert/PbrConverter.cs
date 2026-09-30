using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>The three StarCraft II-style maps derived from one Reforged PBR texture set.</summary>
public sealed class SpecularSet
{
    public required RgbaImage Diffuse { get; init; }
    public required RgbaImage Specular { get; init; }
    public RgbaImage? Normal { get; init; }
    public RgbaImage? Emissive { get; init; }

    /// <summary>True when <see cref="Specular"/>'s alpha is a gloss map rather than solid.</summary>
    public bool HasGloss { get; init; }
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
/// <item><b>Normal</b>: repacked to SC2's two-channel convention — X in alpha, Y in green, with Y
/// the other way up from Reforged — which the engine reads regardless of container compression.</item>
/// </list>
/// The diffuse <b>alpha channel passes through untouched</b>: on an opaque Reforged material it is
/// the team-colour mask, on a blend material it is coverage, and both meanings must survive into
/// the exported file where the material's blend mode decides how it is read.
/// </remarks>
public static class PbrConverter
{
    /// <param name="gloss">
    /// Write a gloss map into the specular's alpha, from the ORM roughness. The roughness then sets
    /// how wide the highlight is instead of only how bright, so it must not also dim the spec.
    /// </param>
    public static SpecularSet Convert(RgbaImage diffuse, RgbaImage? normal, RgbaImage? orm, RgbaImage? emissive,
                                      bool gloss = false)
    {
        gloss &= orm is not null;
        var (albedo, spec) = SplitMetallic(diffuse, orm, gloss);
        return new SpecularSet
        {
            Diffuse = albedo,
            Specular = spec,
            Normal = normal is null ? null : RepackNormal(normal),
            Emissive = emissive,
            HasGloss = gloss,
        };
    }

    private static (RgbaImage Diffuse, RgbaImage Specular) SplitMetallic(RgbaImage diffuse, RgbaImage? orm, bool gloss)
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
                float dim = gloss ? GlossDim(roughness) : 0.35f + shine * 0.65f;
                float sB = (0.04f + (b - 0.04f) * metallic) * dim;
                float sG = (0.04f + (g - 0.04f) * metallic) * dim;
                float sR = (0.04f + (r - 0.04f) * metallic) * dim;
                specPx[i] = Pack(sB);
                specPx[i + 1] = Pack(sG);
                specPx[i + 2] = Pack(sR);
                specPx[i + 3] = gloss ? Pack(Gloss(roughness)) : (byte)255;
            }
        }

        return (new RgbaImage { Width = w, Height = h, Pixels = diffPx },
                new RgbaImage { Width = w, Height = h, Pixels = specPx });
    }

    /// <summary>
    /// StarCraft II gloss (0 = matte, 1 = glossy) for a Reforged roughness: the gloss whose SC2
    /// highlight is as wide as Reforged's. Reforged packs its ORM the glTF way, so its roughness is
    /// glTF's (GGX alpha = roughness squared), and a GGX lobe is matched at its peak by a Blinn
    /// lobe of exponent 2/alpha^2 - 2. SC2's side was measured (<c>MdxProbe --glosscal --fine</c>,
    /// Agria light, lobes fitted per pixel through the display's 2.2 gamma): at specularity 512 with
    /// simulate_roughness, the exponent is <see cref="GlossExponentAtZero"/> x e^(<see cref="GlossLogSlope"/>
    /// x gloss) — 103, 143, 199, 274, 365, 486 at gloss 0.375 to 1 in eighths, residuals under 3% —
    /// while the no-gloss ruler read back 17.9, 34.9, 66.8, 129, 252, 488 for specularity 16 to 512,
    /// so specularity is exactly the N.H exponent. The range is 41 to ~500, which spans roughness
    /// 0.47 to 0.25: the Reforged knight's plate (median 0.28) lands at gloss 0.7-1, its cloth
    /// (0.75-1.0) at 0, where Blizzard's own foliage (22-40 of 255) and metal kits (130-155) sit too.
    /// </summary>
    public static float Gloss(float roughness)
    {
        float alpha = MathF.Max(roughness * roughness, 0.01f);
        float exponent = 2f / (alpha * alpha) - 2f;
        return Math.Clamp(MathF.Log(exponent / GlossExponentAtZero) / GlossLogSlope, 0f, 1f);
    }

    /// <summary>SC2's exponent at gloss 0 (specularity 512, simulate_roughness) — see <see cref="Gloss"/>.</summary>
    public const float GlossExponentAtZero = 41.3f;

    /// <summary>ln(exponent) gained per unit of gloss (specularity 512, simulate_roughness) — see <see cref="Gloss"/>.</summary>
    public const float GlossLogSlope = 2.489f;

    /// <summary>
    /// What remains of the spec brightness once the gloss map carries the width: all of it.
    /// simulate_roughness already dims a wide lobe — the same fit read the peak at 1.00, 1.00, 0.87,
    /// 0.68, 0.46, 0.26 of the ruler's from gloss 1 down to 0.375, peak / exponent within a factor
    /// 1.7 throughout, which is GGX's own energy-conserving falloff. The no-gloss path's roughness
    /// dim on top of it would count roughness twice.
    /// </summary>
    private static float GlossDim(float roughness) => 1f;

    /// <summary>
    /// SC2 samples normals from (alpha, green); X goes to A, Y to G, R/B zeroed. Y is also negated:
    /// StarCraft II's own normal maps store it the other way up from Reforged's (a curl test on the
    /// maps reads Reforged's sources 1.3–2.1 and 29 of 30 HotS and 5 of 6 Liberty maps 0.3–0.8), and
    /// the exporter now writes the vertex bitangent sign the way Blizzard's models do to match.
    /// </summary>
    private static RgbaImage RepackNormal(RgbaImage normal)
    {
        var px = new byte[normal.Pixels.Length];
        var s = normal.Pixels;
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 0;                 // B unused
            px[i + 1] = (byte)(255 - s[i + 1]);   // G = -Y (Y is G after BC5 decode)
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
