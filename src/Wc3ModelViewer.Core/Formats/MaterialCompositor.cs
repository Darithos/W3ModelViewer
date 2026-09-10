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

/// <summary>
/// Where one material shows player colour, as a per-texel scalar in its diffuse's UV space.
/// </summary>
/// <remarks>
/// Warcraft III writes this signal down twice, in two unrelated places, and the exporter needs it
/// as one thing — see <see cref="MaterialCompositor.TeamMaskOf"/>.
/// </remarks>
public sealed class TeamMask
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>One byte per texel: 0 = no player colour, 255 = fully player-coloured.</summary>
    public required byte[] Values { get; init; }

    /// <summary>Fraction of texels carrying any player colour — 0 means the mask is dead weight.</summary>
    public float Coverage
    {
        get
        {
            int hit = 0;
            foreach (byte v in Values) if (v > 8) hit++;
            return Values.Length == 0 ? 0f : (float)hit / Values.Length;
        }
    }

    /// <summary>The mask value at a texel of a <paramref name="w"/> x <paramref name="h"/> image.</summary>
    public byte At(int x, int y, int w, int h)
    {
        int sx = w == Width ? x : x * Width / w;
        int sy = h == Height ? y : y * Height / h;
        return Values[sy * Width + sx];
    }
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

    /// <param name="bakeTeam">
    /// True to paint the player's colour into the result, which is what a preview wants. False
    /// substitutes <b>black</b> for it, which leaves exactly the part of the surface that is not
    /// player-coloured — what an exporter must hand StarCraft II alongside
    /// <see cref="TeamMaskOf"/>, so the engine can add the live player colour back itself.
    /// </param>
    public static CompositeMaterial Compose(MdxModel model, MdxMaterial material,
                                            Wc3TextureCache textures, string modelCascName, int teamColor = 0,
                                            bool bakeTeam = true)
    {
        // Layers whose textures we can resolve, with their images.
        var loaded = new List<(MdxLayer Layer, RgbaImage Image)>();
        foreach (var layer in material.Layers)
        {
            int texId = layer.DiffuseTextureId;
            if ((uint)texId >= (uint)model.Textures.Count) continue;
            var img = textures.Load(modelCascName, model.Textures[texId], teamColor);
            // The classic team layer is a flat player-colour fill, and a team-glow card is that same
            // colour shaped by a falloff. An exporter wants what is left when the player's
            // contribution is taken away — for both of those, nothing — so it composites over
            // black and lets StarCraft II add the colour back through the mask.
            if (img is not null && !bakeTeam
                && (model.Textures[texId].IsTeamColor || model.Textures[texId].IsTeamGlow))
                img = RgbaImage.Solid(8, 8, 0, 0, 0);
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
                result = LerpTeamColorUnder(image, TeamRgb(textures, modelCascName, model, layer, teamColor, bakeTeam));

            // Reforged HD keeps the mask out of the diffuse entirely — it is the ORM's alpha, and
            // the shader tints (multiplies) the diffuse there rather than replacing it, which is
            // why the footman's tabard keeps its folds and wear in every player colour.
            if (layer.IsPbr && TeamMaskOf(model, material, textures, modelCascName) is { } hdMask)
                result = TintTeamColor(result, hdMask,
                                       TeamRgb(textures, modelCascName, model, layer, teamColor, bakeTeam));

            // An opaque material must not carry transparency into the draw. FilterMode.None means
            // "ignore alpha" in Warcraft III, and a classic skin's alpha channel is a team-colour
            // mask, not coverage — so whatever it holds, the surface is solid. Letting it through
            // punched holes wherever a mask texel happened to be dark: Drenden's face samples six
            // such texels and rendered see-through over them. The multi-layer path below has always
            // forced this on its base layer; the single-layer path silently did not.
            if (blend == CompositeBlend.Opaque && result.HasTransparency())
                result = WithOpaqueAlpha(result);

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

        // The stack's coverage is its BOTTOM layer's: the layers above blend or add light onto what
        // the base already covers, and cannot extend it. Requiring every layer to be additive before
        // the stack may float classified the common effect card — a glow over a cutout, a flare over
        // an alpha ramp — as opaque, and an opaque card is a solid rectangle standing through the
        // model. Only a base that is genuinely opaque (FilterMode.None) makes the stack opaque.
        var stackBlend = loaded[0].Layer.FilterMode switch
        {
            MdxFilterMode.Transparent => CompositeBlend.AlphaTest,
            MdxFilterMode.Blend or MdxFilterMode.AddAlpha => CompositeBlend.AlphaBlend,
            MdxFilterMode.Additive or MdxFilterMode.Modulate or MdxFilterMode.Modulate2x => CompositeBlend.Additive,
            _ => CompositeBlend.Opaque,
        };

        var stacked = new RgbaImage { Width = w, Height = h, Pixels = canvas };

        // An HD layer inside a stack keeps its ORM mask; the exporter reads the mask either way, so
        // this path has to remove the player's contribution here too or the two disagree.
        var pbr = loaded.FirstOrDefault(l => l.Layer.IsPbr).Layer;
        if (pbr is not null && TeamMaskOf(model, material, textures, modelCascName) is { } stackMask)
            stacked = TintTeamColor(stacked, stackMask,
                                    TeamRgb(textures, modelCascName, model, pbr, teamColor, bakeTeam));

        return new CompositeMaterial
        {
            Texture = stacked,
            Blend = stackBlend, TwoSided = twoSided, Unshaded = unshaded,
            PrimaryTexturePath = primaryPath,
        };
    }

    private static CompositeBlend Max(CompositeBlend a, CompositeBlend b) => (CompositeBlend)Math.Max((int)a, (int)b);

    /// <summary>
    /// A copy with every texel opaque. Copies rather than writes in place: the image belongs to the
    /// texture cache and is shared with every other material that references the same file.
    /// </summary>
    private static RgbaImage WithOpaqueAlpha(RgbaImage image)
    {
        var px = (byte[])image.Pixels.Clone();
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return new RgbaImage { Width = image.Width, Height = image.Height, Pixels = px };
    }

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

    /// <summary>
    /// Where <paramref name="material"/> shows player colour, or null when it never does.
    /// </summary>
    /// <remarks>
    /// Warcraft III records this twice, in two places that share nothing:
    /// <list type="bullet">
    /// <item><b>Classic SD</b> stacks an opaque <c>replaceableId 1</c> layer under the real diffuse
    /// drawn with <c>Blend</c>, so the player's colour shows through wherever the diffuse's alpha
    /// is low — the mask is <c>1 - diffuse.a</c>. 342 of 1,511 materials across 400 SD unit models
    /// are built this way, 337 of them as that two-layer stack.</item>
    /// <item><b>Reforged HD</b> puts it in the <b>alpha channel of the ORM map</b> — measured, not
    /// assumed: the footman's <c>Main_ORM</c> alpha is exactly his tabard panels, his
    /// <c>Shield_ORM</c> alpha exactly the crest, and his helmet and sword ORMs are alpha-empty.
    /// Across 400 HD unit models, 486 ORM textures carry such a mask and 330 are empty. The
    /// per-layer <c>teamColorMultiplier</c> is 0 on every shipping HD layer, and the diffuse alpha
    /// is coverage, so neither of those is the signal.</item>
    /// </list>
    /// The 11 ORM textures (of 837) whose alpha is uniformly opaque are unauthored placeholders —
    /// hair, dragon wings, ship sails, whose RGB is a degenerate constant too. Taking them at face
    /// value would team-colour a whole head of hair, so a mask with no dark texels is rejected.
    /// </remarks>
    public static TeamMask? TeamMaskOf(MdxModel model, MdxMaterial material,
                                       Wc3TextureCache textures, string modelCascName)
    {
        foreach (var layer in material.Layers)
        {
            if (!layer.IsPbr) continue;
            int ormId = layer.Slot(MdxTextureSlot.Orm);
            if ((uint)ormId >= (uint)model.Textures.Count) continue;
            var orm = textures.Load(modelCascName, model.Textures[ormId]);
            if (orm is null) continue;
            return FromAlpha(orm, invert: false);
        }

        // Team glow (replaceable 2): the whole card is the player's colour, shaped by the art's
        // own falloff, so the mask *is* that falloff. 102 of 400 SD unit models use it — auras,
        // weapon glows, the disc under a hero — against 1 of 400 HD ones.
        foreach (var layer in material.Layers)
        {
            int glowId = layer.DiffuseTextureId;
            if ((uint)glowId >= (uint)model.Textures.Count || !model.Textures[glowId].IsTeamGlow) continue;
            var glow = textures.Load(modelCascName, model.Textures[glowId]);
            if (glow is not null) return FromBrightness(glow);
        }

        // Classic: the mask lives in the diffuse that is drawn over the team layer.
        bool hasTeamLayer = material.Layers.Any(l => (uint)l.DiffuseTextureId < (uint)model.Textures.Count
                                                  && model.Textures[l.DiffuseTextureId].IsTeamColor);
        foreach (var layer in material.Layers)
        {
            int texId = layer.DiffuseTextureId;
            if ((uint)texId >= (uint)model.Textures.Count) continue;
            var tex = model.Textures[texId];
            if (tex.IsReplaceable) continue;
            if (!hasTeamLayer && layer.Slot(MdxTextureSlot.TeamColor) < 0) continue;
            var img = textures.Load(modelCascName, tex);
            if (img is null) continue;
            return FromAlpha(img, invert: true);
        }
        return null;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RgbaImage, TeamMask?[]> MaskCache = new();

    /// <summary>
    /// An image's alpha channel as a mask, or null when it carries no usable signal — all-empty
    /// (nothing is player-coloured) or all-opaque (an unauthored placeholder map).
    /// </summary>
    private static TeamMask? FromAlpha(RgbaImage img, bool invert)
    {
        // Scanning a 2048-square ORM per geoset per rebuild is real time, and the answer only
        // changes when the texture cache hands back a different image — which it does on reload and
        // on a texture override, so keying the cache on the image itself is enough to stay honest.
        var slot = MaskCache.GetValue(img, static _ => new TeamMask?[2]);
        int k = invert ? 1 : 0;
        if (slot[k] is { } hit) return hit;

        int n = img.Pixels.Length / 4;
        if (n == 0) return null;
        var values = new byte[n];
        int lo = 0, hi = 0;
        for (int i = 0; i < n; i++)
        {
            byte a = img.Pixels[i * 4 + 3];
            byte v = invert ? (byte)(255 - a) : a;
            values[i] = v;
            if (v < 64) lo++; else if (v > 192) hi++;
        }
        if (hi * 1000 < n || lo * 1000 < n) return null;
        return slot[k] = new TeamMask { Width = img.Width, Height = img.Height, Values = values };
    }

    /// <summary>
    /// A glow card's falloff, read straight off its art as the brightest channel per texel.
    /// </summary>
    /// <remarks>
    /// The art is the player's colour times the falloff, and player 0's colour has a 255 channel
    /// (<c>TeamColor00</c> is 255,4,2), so the brightest channel of <c>TeamGlow00</c> <i>is</i> the
    /// falloff in bytes — no normalising, and it comes out right for the synthetic fallback too.
    /// Unlike an ORM mask this one is not bimodal: the real art peaks at 123/255 and fades to 0, so
    /// it would fail <see cref="FromAlpha"/>'s "needs bright texels" test. All it must not be is
    /// empty.
    /// </remarks>
    public static TeamMask? GlowMask(RgbaImage glow) => FromBrightness(glow);

    private static TeamMask? FromBrightness(RgbaImage img)
    {
        int n = img.Pixels.Length / 4;
        if (n == 0) return null;
        var values = new byte[n];
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            byte v = Math.Max(img.Pixels[i * 4], Math.Max(img.Pixels[i * 4 + 1], img.Pixels[i * 4 + 2]));
            values[i] = v;
            any |= v > 8;
        }
        return any ? new TeamMask { Width = img.Width, Height = img.Height, Values = values } : null;
    }

    /// <summary>The flat player colour this layer's team slot resolves to; black when not baking.</summary>
    private static (byte B, byte G, byte R) TeamRgb(Wc3TextureCache textures, string modelCascName,
                                                    MdxModel model, MdxLayer layer, int teamColor, bool bakeTeam)
    {
        if (!bakeTeam) return (0, 0, 0);
        int tcId = layer.Slot(MdxTextureSlot.TeamColor);
        var tc = (uint)tcId < (uint)model.Textures.Count
            ? textures.Load(modelCascName, model.Textures[tcId], teamColor)
            : null;
        return (tc?.Pixels[0] ?? 18, tc?.Pixels[1] ?? 3, tc?.Pixels[2] ?? 255);
    }

    /// <summary>
    /// <c>lerp(diffuse, diffuse * team, mask)</c> — a tint, not a replacement, so the art's detail
    /// survives inside the masked region. With a black team colour this reduces to
    /// <c>diffuse * (1 - mask)</c>, i.e. the diffuse with the player's contribution removed.
    /// </summary>
    private static RgbaImage TintTeamColor(RgbaImage diffuse, TeamMask mask, (byte B, byte G, byte R) team)
    {
        int w = diffuse.Width, h = diffuse.Height;
        var px = (byte[])diffuse.Pixels.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int m = mask.At(x, y, w, h);
                if (m == 0) continue;
                int i = (y * w + x) * 4;
                px[i] = Mix(px[i], team.B, m);
                px[i + 1] = Mix(px[i + 1], team.G, m);
                px[i + 2] = Mix(px[i + 2], team.R, m);
            }
        return new RgbaImage { Width = w, Height = h, Pixels = px };

        static byte Mix(byte d, byte t, int m) => (byte)((d * (255 - m) + d * t / 255 * m) / 255);
    }

    /// <summary>final = lerp(teamColour, diffuse, diffuse.a), emitted opaque.</summary>
    private static RgbaImage LerpTeamColorUnder(RgbaImage diffuse, (byte B, byte G, byte R) team)
    {
        byte tb = team.B, tg = team.G, tr = team.R;

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
                // A classic opaque base layer's alpha is the team-colour mask, not coverage, so it
                // must not survive as transparency. Every other base layer's alpha IS the card's
                // coverage — discarding it is what made effect cards draw as solid rectangles.
                canvas[i + 3] = mode == MdxFilterMode.None ? (byte)255 : src[i + 3];
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
