using Wc3ModelViewer.Core.Casc;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>How a composited material should be drawn.</summary>
public enum CompositeBlend
{
    Opaque,
    AlphaTest,     // cutout at the engine's 0.75 threshold
    AlphaBlend,
    Additive,
}

/// <summary>One material flattened to a single texture plus a draw mode.</summary>
public sealed class CompositeMaterial
{
    public required RgbaImage Texture { get; init; }
    public required CompositeBlend Blend { get; init; }
    public bool TwoSided { get; init; }
    public bool Unshaded { get; init; }

    /// <summary>The first real texture path that fed this composite — used to name exports.</summary>
    public string PrimaryTexturePath { get; init; } = "";

    /// <summary>The same composite drawn a different way — see <see cref="MaterialCompositor.CutoutCoverage"/>.</summary>
    public CompositeMaterial WithBlend(CompositeBlend blend) => new()
    {
        Texture = Texture, Blend = blend, TwoSided = TwoSided, Unshaded = Unshaded,
        PrimaryTexturePath = PrimaryTexturePath,
    };
}

/// <summary>
/// Flattens an MDX material's layer stack into one texture the viewport (or an exporter that only
/// supports one diffuse map) can use.
/// </summary>
/// <remarks>
/// This is where Warcraft III's two alpha conventions are untangled — the "alpha mapping" problem:
/// <list type="bullet">
/// <item>On an <b>opaque</b> HD material, the diffuse alpha channel is the <b>team-colour mask</b>
/// (alpha 0 = show team colour). The texture is flattened to team colour under diffuse and emitted
/// fully opaque.</item>
/// <item>On a <b>transparent/blend</b> layer (hair, foliage), the same channel is real coverage and
/// must stay alpha — no team colour is composited, because one channel cannot mean both.</item>
/// <item>Classic SD materials do it with layers instead: an opaque team-colour layer (replaceable 1)
/// underneath, then the diffuse cutout on top. Painter-compositing the stack reproduces it.</item>
/// </list>
/// </remarks>
public static class MaterialCompositor
{
    /// <summary>The engine's alpha-test threshold for filter mode Transparent.</summary>
    public const float CutoutThreshold = 0.75f;

    public static CompositeMaterial Compose(MdxModel model, MdxMaterial material,
                                            Wc3TextureCache textures, string modelCascName, int teamColor = 0)
    {
        // Layers whose textures we can resolve, with their images.
        var loaded = new List<(MdxLayer Layer, RgbaImage Image)>();
        foreach (var layer in material.Layers)
        {
            int texId = layer.DiffuseTextureId;
            if ((uint)texId >= (uint)model.Textures.Count) continue;
            var img = textures.Load(modelCascName, model.Textures[texId], teamColor);
            if (img is not null) loaded.Add((layer, img));
        }
        if (loaded.Count == 0)
            return new CompositeMaterial
            {
                Texture = RgbaImage.Solid(4, 4, 200, 60, 220),      // unmistakable "missing" magenta
                Blend = CompositeBlend.Opaque,
            };

        // The stack's overall draw mode comes from its most transparent layer.
        var blend = CompositeBlend.Opaque;
        bool twoSided = false, unshaded = false;
        foreach (var (layer, _) in loaded)
        {
            blend = Max(blend, layer.FilterMode switch
            {
                MdxFilterMode.Transparent => CompositeBlend.AlphaTest,
                MdxFilterMode.Blend or MdxFilterMode.AddAlpha => CompositeBlend.AlphaBlend,
                MdxFilterMode.Additive or MdxFilterMode.Modulate or MdxFilterMode.Modulate2x => CompositeBlend.Additive,
                _ => CompositeBlend.Opaque,
            });
            twoSided |= (layer.ShadingFlags & MdxShadingFlags.TwoSided) != 0;
            unshaded |= (layer.ShadingFlags & MdxShadingFlags.Unshaded) != 0;
        }

        string primaryPath = "";
        foreach (var (layer, _) in loaded)
        {
            int texId = layer.DiffuseTextureId;
            if ((uint)texId < (uint)model.Textures.Count && !model.Textures[texId].IsReplaceable)
            {
                primaryPath = model.Textures[texId].FileName;
                break;
            }
        }

        // Single-layer fast path — HD materials and simple SD ones.
        if (loaded.Count == 1)
        {
            var (layer, image) = loaded[0];
            var result = image;

            // Reforged authors *every* HD layer with FilterMode.Transparent — it is the format's
            // default, not a statement that this material is a cutout, and its diffuse alpha is
            // coverage (team colour is a per-material scalar in Reforged, not a texel mask). So
            // trust the filter mode only when the texture really has transparent texels;
            // otherwise the material is solid and must not be alpha-tested at all.
            if (blend == CompositeBlend.AlphaTest && !image.HasTransparency())
                blend = CompositeBlend.Opaque;

            // Classic SD still uses the alpha channel as a team-colour mask on opaque materials.
            if (blend == CompositeBlend.Opaque && !layer.IsPbr
                && layer.Slot(MdxTextureSlot.TeamColor) >= 0 && image.HasTransparency())
                result = LerpTeamColorUnder(image, textures, modelCascName, model, layer, teamColor);

            return new CompositeMaterial
            {
                Texture = result, Blend = blend, TwoSided = twoSided, Unshaded = unshaded,
                PrimaryTexturePath = primaryPath,
            };
        }

        // Multi-layer SD stack: painter-composite into the largest layer's size.
        int w = loaded.Max(l => l.Image.Width);
        int h = loaded.Max(l => l.Image.Height);
        var canvas = new byte[w * h * 4];

        bool first = true;
        foreach (var (layer, image) in loaded)
        {
            var src = image.Width == w && image.Height == h ? image : Resize(image, w, h);
            BlendOnto(canvas, src.Pixels, layer.FilterMode, first);
            first = false;
        }

        // A composited stack is opaque unless the whole thing floats (pure additive glows etc.).
        var stackBlend = blend == CompositeBlend.Additive && loaded.All(l => l.Layer.FilterMode is MdxFilterMode.Additive or MdxFilterMode.AddAlpha)
            ? CompositeBlend.Additive
            : CompositeBlend.Opaque;

        return new CompositeMaterial
        {
            Texture = new RgbaImage { Width = w, Height = h, Pixels = canvas },
            Blend = stackBlend, TwoSided = twoSided, Unshaded = unshaded,
            PrimaryTexturePath = primaryPath,
        };
    }

    private static CompositeBlend Max(CompositeBlend a, CompositeBlend b) => (CompositeBlend)Math.Max((int)a, (int)b);

    /// <summary>
    /// Fraction of the texels <paramref name="geoset"/> actually samples whose alpha is below
    /// <paramref name="threshold"/> — 0 when this geoset never touches a transparent texel.
    /// </summary>
    /// <remarks>
    /// Reforged packs solid body parts and cut-out cards into one atlas and marks the whole
    /// material transparent, so the texture as a whole says nothing about any single geoset: the
    /// grunt's body atlas is 5% transparent, but every one of those texels belongs to his straps.
    /// The only honest question is what <em>this</em> geoset's UVs land on, so rasterise its
    /// triangles into the texture and look only inside that footprint.
    /// </remarks>
    public static float CutoutCoverage(RgbaImage image, MdxGeoset geoset, byte threshold = 128)
    {
        var uvs = geoset.Uvs;
        int w = image.Width, h = image.Height;
        if (uvs.Length < geoset.VertexCount || w <= 0 || h <= 0) return 0f;

        var covered = new bool[w * h];
        var idx = geoset.Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
            if ((uint)i0 >= (uint)uvs.Length || (uint)i1 >= (uint)uvs.Length || (uint)i2 >= (uint)uvs.Length)
                continue;

            // MDX V is top-down and RgbaImage rows are top-down, so UV maps straight to (x, y).
            float ax = uvs[i0].X * w, ay = uvs[i0].Y * h;
            float bx = uvs[i1].X * w, by = uvs[i1].Y * h;
            float cx = uvs[i2].X * w, cy = uvs[i2].Y * h;

            float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (MathF.Abs(den) < 1e-9f) continue;

            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));

            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f;
                    float l1 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / den;
                    float l2 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / den;
                    if (l1 >= -0.001f && l2 >= -0.001f && l1 + l2 <= 1.001f)
                        covered[y * w + x] = true;
                }
            }
        }

        int total = 0, cut = 0;
        for (int i = 0; i < covered.Length; i++)
        {
            if (!covered[i]) continue;
            total++;
            if (image.Pixels[i * 4 + 3] < threshold) cut++;
        }
        return total == 0 ? 0f : (float)cut / total;
    }

    /// <summary>final = lerp(teamColour, diffuse, diffuse.a), emitted opaque.</summary>
    private static RgbaImage LerpTeamColorUnder(RgbaImage diffuse, Wc3TextureCache textures,
                                                string modelCascName, MdxModel model, MdxLayer layer, int teamColor)
    {
        int tcId = layer.Slot(MdxTextureSlot.TeamColor);
        var tc = (uint)tcId < (uint)model.Textures.Count
            ? textures.Load(modelCascName, model.Textures[tcId], teamColor)
            : null;
        byte tb = tc?.Pixels[0] ?? 18, tg = tc?.Pixels[1] ?? 3, tr = tc?.Pixels[2] ?? 255;

        var px = new byte[diffuse.Pixels.Length];
        var s = diffuse.Pixels;
        for (int i = 0; i < px.Length; i += 4)
        {
            int a = s[i + 3];
            px[i] = (byte)((s[i] * a + tb * (255 - a)) / 255);
            px[i + 1] = (byte)((s[i + 1] * a + tg * (255 - a)) / 255);
            px[i + 2] = (byte)((s[i + 2] * a + tr * (255 - a)) / 255);
            px[i + 3] = 255;
        }
        return new RgbaImage { Width = diffuse.Width, Height = diffuse.Height, Pixels = px };
    }

    private static void BlendOnto(byte[] canvas, byte[] src, MdxFilterMode mode, bool first)
    {
        for (int i = 0; i < canvas.Length && i < src.Length; i += 4)
        {
            if (first)
            {
                canvas[i] = src[i]; canvas[i + 1] = src[i + 1]; canvas[i + 2] = src[i + 2];
                canvas[i + 3] = 255;
                continue;
            }
            switch (mode)
            {
                case MdxFilterMode.Transparent:
                {
                    if (src[i + 3] >= CutoutThreshold * 255)
                    { canvas[i] = src[i]; canvas[i + 1] = src[i + 1]; canvas[i + 2] = src[i + 2]; }
                    break;
                }
                case MdxFilterMode.Blend or MdxFilterMode.AddAlpha:
                {
                    int a = src[i + 3];
                    canvas[i] = (byte)((src[i] * a + canvas[i] * (255 - a)) / 255);
                    canvas[i + 1] = (byte)((src[i + 1] * a + canvas[i + 1] * (255 - a)) / 255);
                    canvas[i + 2] = (byte)((src[i + 2] * a + canvas[i + 2] * (255 - a)) / 255);
                    break;
                }
                case MdxFilterMode.Additive:
                {
                    canvas[i] = (byte)Math.Min(255, canvas[i] + src[i]);
                    canvas[i + 1] = (byte)Math.Min(255, canvas[i + 1] + src[i + 1]);
                    canvas[i + 2] = (byte)Math.Min(255, canvas[i + 2] + src[i + 2]);
                    break;
                }
                case MdxFilterMode.Modulate or MdxFilterMode.Modulate2x:
                {
                    int mul = mode == MdxFilterMode.Modulate2x ? 2 : 1;
                    canvas[i] = (byte)Math.Min(255, canvas[i] * src[i] * mul / 255);
                    canvas[i + 1] = (byte)Math.Min(255, canvas[i + 1] * src[i + 1] * mul / 255);
                    canvas[i + 2] = (byte)Math.Min(255, canvas[i + 2] * src[i + 2] * mul / 255);
                    break;
                }
                default:
                {
                    canvas[i] = src[i]; canvas[i + 1] = src[i + 1]; canvas[i + 2] = src[i + 2];
                    break;
                }
            }
        }
    }

    /// <summary>Nearest-neighbour resize — layers rarely mismatch, and exactness does not matter here.</summary>
    private static RgbaImage Resize(RgbaImage src, int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = y * src.Height / h;
            for (int x = 0; x < w; x++)
            {
                int sx = x * src.Width / w;
                int s = (sy * src.Width + sx) * 4;
                int d = (y * w + x) * 4;
                px[d] = src.Pixels[s]; px[d + 1] = src.Pixels[s + 1];
                px[d + 2] = src.Pixels[s + 2]; px[d + 3] = src.Pixels[s + 3];
            }
        }
        return new RgbaImage { Width = w, Height = h, Pixels = px };
    }
}
