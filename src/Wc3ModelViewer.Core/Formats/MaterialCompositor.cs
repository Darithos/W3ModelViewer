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

    /// <summary>The same composite drawn from another image — see <see cref="UvClamp.Apply"/>.</summary>
    public CompositeMaterial WithTexture(RgbaImage texture) => new()
    {
        Texture = texture, Blend = Blend, TwoSided = TwoSided, Unshaded = Unshaded,
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
    /// <summary>
    /// The image behind one of a layer's texture slots, or null when the slot is empty or is a
    /// generated one. Team-colour and team-glow slots have no file behind them — StarCraft II and
    /// glTF both carry the player's colour themselves — and a tileset tree or cliff (replaceable
    /// 11, 31-37) has already been given a real file name at load, so it resolves like any other.
    /// </summary>
    public static RgbaImage? LoadSlot(MdxModel mdx, MdxLayer layer, MdxTextureSlot slot,
                                      Casc.Wc3TextureCache textures, string modelCascName, int teamColor)
    {
        int texId = layer.Slot(slot);
        if ((uint)texId >= (uint)mdx.Textures.Count) return null;
        var tex = mdx.Textures[texId];
        if (tex.IsTeamColor || tex.IsTeamGlow || tex.FileName.Length == 0) return null;
        return textures.Load(modelCascName, tex, teamColor);
    }

    /// <summary>The engine's alpha-test threshold for filter mode Transparent.</summary>
    public const float CutoutThreshold = 0.75f;

    /// <summary>
    /// True when every layer of a material is statically transparent — the layer's own
    /// <see cref="MdxLayer.Alpha"/> is 0 and no <c>KMTA</c> track ever raises it.
    /// </summary>
    /// <remarks>
    /// This is how Warcraft III carries helper geometry that must never be drawn: the smooth ramp a
    /// unit walks up instead of the visible steps (Blizzard names its bone <c>ultraGlide_geo</c>),
    /// the sphere a cyclone is bound to (<c>CycloneBound_Diffuse</c>), a portal's empty carrier.
    /// The panel is deliberately bigger than the art it sits on, so drawing it swallows the model —
    /// most Reforged and Definitive staircases rendered as one flat bridge-textured slab until this
    /// was honoured. 1,417 layers across the archive carry the flag; it is not an edge case.
    /// <para>
    /// The geoset-level equivalent, a <c>GEOA</c> whose static alpha is 0, is handled separately by
    /// each caller: both mean "not drawn", but they are different chunks and either can occur alone.
    /// </para>
    /// </remarks>
    public static bool IsInvisible(MdxMaterial material)
        => material.Layers.Count > 0
           && material.Layers.All(l => l.AlphaTrack is null && l.Alpha <= 0.001f);

    /// <summary>
    /// <see cref="IsInvisible(MdxMaterial)"/> for the material a geoset draws with. A geoset whose
    /// material index is out of range is left to the caller's own bounds check, so it reads false.
    /// </summary>
    public static bool IsInvisible(MdxModel model, MdxGeoset geoset)
        => (uint)geoset.MaterialId < (uint)model.Materials.Count
           && IsInvisible(model.Materials[geoset.MaterialId]);

    /// <param name="bakeTeam">
    /// True to paint the player's colour into the result, which is what a preview wants. False
    /// substitutes <b>black</b> for it, which leaves exactly the part of the surface that is not
    /// player-coloured — what an exporter must hand StarCraft II alongside
    /// <see cref="TeamMaskOf"/>, so the engine can add the live player colour back itself.
    /// </param>
    /// <param name="tintHdTeam">
    /// False leaves a Reforged layer's masked region exactly as painted, neither tinted nor cut —
    /// for an exporter that hands StarCraft II the mask in the diffuse alpha, where the engine
    /// applies the same tint Reforged does. Classic team layers are unaffected.
    /// </param>
    public static CompositeMaterial Compose(MdxModel model, MdxMaterial material,
                                            Wc3TextureCache textures, string modelCascName, int teamColor = 0,
                                            bool bakeTeam = true, bool tintHdTeam = true)
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
            if ((uint)texId < (uint)model.Textures.Count
                && !model.Textures[texId].IsTeamColor && !model.Textures[texId].IsTeamGlow
                && model.Textures[texId].FileName.Length > 0)
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
            if (layer.IsPbr && tintHdTeam && TeamMaskOf(model, material, textures, modelCascName) is { } hdMask)
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
        if (pbr is not null && tintHdTeam && TeamMaskOf(model, material, textures, modelCascName) is { } stackMask)
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
    /// Reforged's HD pixel shader (<c>shaders\ps\hd.bls</c>, disassembled) reads that alpha
    /// straight into its colour mix with no gate: hue = lerp(diffuse, team, sqrt(a)), brightness =
    /// lerp(max(diffuse), min(max(diffuse), max(team)), 0.95 a^2). So an ORM whose alpha is 255
    /// everywhere — the footman's helmet plume (<c>Human_Footman_Hair_ORM</c>), ship sails, dragon
    /// wings, 11 of 837 — is a surface that is <i>entirely</i> the player's, and the game draws it
    /// so. These used to be rejected as unauthored because their RGB is a flat constant too; that
    /// left the plume white in every player colour, here and in StarCraft II. Only an alpha that is
    /// 0 throughout (330 of 837) means "no player colour".
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

        // Classic: a replaceable-1 fill somewhere in the stack. The mask is read off the stack
        // itself, whichever way the author built it — see StackMask.
        bool hasTeamLayer = material.Layers.Any(l => (uint)l.DiffuseTextureId < (uint)model.Textures.Count
                                                  && model.Textures[l.DiffuseTextureId].IsTeamColor);
        if (hasTeamLayer) return StackMask(model, material, textures, modelCascName);

        // A single classic layer that names a team-colour slot: its diffuse alpha is the mask.
        foreach (var layer in material.Layers)
        {
            int texId = layer.DiffuseTextureId;
            if ((uint)texId >= (uint)model.Textures.Count) continue;
            var tex = model.Textures[texId];
            if (tex.IsTeamColor || tex.IsTeamGlow || tex.FileName.Length == 0) continue;
            if (layer.Slot(MdxTextureSlot.TeamColor) < 0) continue;
            var img = textures.Load(modelCascName, tex);
            if (img is null) continue;
            return FromAlpha(img, invert: true);
        }
        return null;
    }

    /// <summary>
    /// A classic stack's mask, read off the stack: the material composited with the player's fill
    /// at full white, minus the same stack composited over black, per texel, in the diffuse's grid.
    /// </summary>
    /// <remarks>
    /// This answers the one question StarCraft II needs — how much of the player's colour reaches
    /// this texel — without assuming where in the stack the fill sits or how it is blended, which
    /// the old reading (<c>1 - alpha</c> of the first diffuse) did assume:
    /// <list type="bullet">
    /// <item>The stock stack, fill underneath and the diffuse over it with <c>Blend</c>, comes out
    /// as <c>1 - diffuse.a</c>, exactly as before.</item>
    /// <item>A fill drawn <b>on top</b> with <c>Modulate</c> — DarkHordeGruntV2's pauldrons, an
    /// opaque plate texture multiplied by the player's colour — comes out as the plate's
    /// brightness. The exporter's diffuse for that material is black (the stack over a black fill),
    /// and StarCraft II adds <c>colour x brightness</c> on top, which is what Warcraft III drew. The
    /// old reading took <c>1 - a</c> of the opaque plate, found nothing, and the pauldrons reached
    /// StarCraft II black in every player colour while the viewer showed them red.</item>
    /// <item>A fill on top with <c>Blend</c> or <c>Transparent</c> covers the surface completely
    /// (its texture is opaque), and so does a fill with nothing drawn over it — a banner, a flag
    /// panel, a decal: 255 throughout. Null there would be the worst of both worlds, since the
    /// composite has already replaced the fill with black.</item>
    /// <item>A fill added with <c>Additive</c> reaches every texel at full strength; the sum is kept
    /// unclamped so that reads as 255 rather than as what the diffuse left below white.</item>
    /// </list>
    /// </remarks>
    private static TeamMask? StackMask(MdxModel model, MdxMaterial material,
                                       Wc3TextureCache textures, string modelCascName)
    {
        var layers = new List<(MdxFilterMode Mode, RgbaImage? Image)>();     // null image = the fill
        foreach (var layer in material.Layers)
        {
            int texId = layer.DiffuseTextureId;
            if ((uint)texId >= (uint)model.Textures.Count) continue;
            var tex = model.Textures[texId];
            if (tex.IsTeamColor) { layers.Add((layer.FilterMode, null)); continue; }
            var img = tex.IsTeamGlow ? null : textures.Load(modelCascName, tex);
            if (img is not null) layers.Add((layer.FilterMode, img));
        }
        if (layers.All(l => l.Image is not null)) return null;

        int w = Math.Max(4, layers.Max(l => l.Image?.Width ?? 0));
        int h = Math.Max(4, layers.Max(l => l.Image?.Height ?? 0));
        var white = Stack(layers, w, h, 1f);
        var black = Stack(layers, w, h, 0f);
        var values = new byte[w * h];
        int any = 0;
        for (int i = 0; i < values.Length; i++)
        {
            float d = Math.Max(white[i * 3] - black[i * 3],
                      Math.Max(white[i * 3 + 1] - black[i * 3 + 1], white[i * 3 + 2] - black[i * 3 + 2]));
            byte v = (byte)Math.Clamp(d * 255f + 0.5f, 0, 255);
            values[i] = v;
            if (v > 8) any++;
        }
        return any == 0 ? null : new TeamMask { Width = w, Height = h, Values = values };
    }

    /// <summary>
    /// <see cref="BlendOnto"/> in floats, with the fill at <paramref name="fill"/> (alpha 1) and
    /// no clamp on Additive, so that <see cref="StackMask"/> can subtract two of these.
    /// </summary>
    private static float[] Stack(List<(MdxFilterMode Mode, RgbaImage? Image)> layers, int w, int h, float fill)
    {
        var canvas = new float[w * h * 3];
        bool first = true;
        foreach (var (mode, image) in layers)
        {
            var src = image is null ? null : image.Width == w && image.Height == h ? image : Resize(image, w, h);
            for (int i = 0; i < w * h; i++)
            {
                float r, g, b, a;
                if (src is null) { r = g = b = fill; a = 1f; }
                else
                {
                    int o = i * 4;
                    b = src.Pixels[o] / 255f; g = src.Pixels[o + 1] / 255f; r = src.Pixels[o + 2] / 255f;
                    a = src.Pixels[o + 3] / 255f;
                }
                int c = i * 3;
                if (first) { canvas[c] = b; canvas[c + 1] = g; canvas[c + 2] = r; continue; }
                switch (mode)
                {
                    case MdxFilterMode.Transparent:
                        if (a >= CutoutThreshold) { canvas[c] = b; canvas[c + 1] = g; canvas[c + 2] = r; }
                        break;
                    case MdxFilterMode.Blend or MdxFilterMode.AddAlpha:
                        canvas[c] += (b - canvas[c]) * a;
                        canvas[c + 1] += (g - canvas[c + 1]) * a;
                        canvas[c + 2] += (r - canvas[c + 2]) * a;
                        break;
                    case MdxFilterMode.Additive:
                        canvas[c] += b; canvas[c + 1] += g; canvas[c + 2] += r;
                        break;
                    case MdxFilterMode.Modulate or MdxFilterMode.Modulate2x:
                    {
                        float k = mode == MdxFilterMode.Modulate2x ? 2f : 1f;
                        canvas[c] *= b * k; canvas[c + 1] *= g * k; canvas[c + 2] *= r * k;
                        break;
                    }
                    default:
                        canvas[c] = b; canvas[c + 1] = g; canvas[c + 2] = r;
                        break;
                }
            }
            first = false;
        }
        return canvas;
    }

    /// <summary>
    /// The emissive map a Reforged layer adds on top of its lit surface, with the surface's coverage
    /// copied into its alpha, and the layer whose gain scales it — or null when nothing would glow.
    /// </summary>
    /// <remarks>
    /// Reforged's shader adds this texel after lighting (<c>color += emissive</c>, mdx-m3-viewer's
    /// hd.frag), so a surface can be authored as black diffuse and lit entirely from here. Blizzard
    /// did exactly that to the Definitive Edition fountains: the pool's UVs sit on a pure black
    /// octagon in the diffuse atlas (1,1,1) and on mana blue (156,60,253) in the emissive, so a
    /// renderer that ignores the slot draws a black hole where the water is. Across the DE set 2,043
    /// layers carry real emissive art (the rest bind <c>Black32.blp</c>): 1,404 at gain 1, 509
    /// animated by <c>KMTE</c>, and 98 at a static gain of 0 — birth and death variants that switch
    /// the glow off — which are left out here the way the game leaves them dark.
    /// </remarks>
    /// <param name="coverage">
    /// The composited surface when it is not opaque. A cut-out or blended surface cannot glow where
    /// it is not drawn, so its alpha becomes the emissive's; null leaves the emissive solid.
    /// </param>
    public static (RgbaImage Image, MdxLayer Layer)? EmissiveOf(MdxModel model, MdxMaterial material,
                                                               Wc3TextureCache textures, string modelCascName,
                                                               RgbaImage? coverage = null)
    {
        var layer = material.Layers.FirstOrDefault(l => l.IsPbr);
        if (layer is null) return null;
        if (layer.EmissiveTrack is null && layer.EmissiveMultiplier <= 0) return null;
        var emissive = LoadSlot(model, layer, MdxTextureSlot.Emissive, textures, modelCascName, 0);
        if (emissive is null || IsBlack(emissive)) return null;

        var px = (byte[])emissive.Pixels.Clone();
        int w = emissive.Width, h = emissive.Height;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (coverage is null) { px[i + 3] = 255; continue; }
                int sx = x * coverage.Width / w, sy = y * coverage.Height / h;
                px[i + 3] = coverage.Pixels[(sy * coverage.Width + sx) * 4 + 3];
            }
        return (new RgbaImage { Width = w, Height = h, Pixels = px }, layer);
    }

    /// <summary>
    /// Whether a material's texture repeats across each axis (<c>TEXS</c> flags &amp;1 width,
    /// &amp;2 height), or clamps to its edge texels.
    /// </summary>
    /// <remarks>
    /// A clear bit means clamp: mdx-m3-viewer binds CLAMP_TO_EDGE for it, and modellers set Wrap
    /// Width/Height precisely to make a texture tile. It is not a formality in classic art — 84% of
    /// SD textures (7,958 of 9,504) leave both bits clear, and 247 SD models have UVs past the edge
    /// of an axis that does not wrap, a glow ring mapped wider than its card or a gore strip hanging
    /// off the atlas. Reforged and DE set both bits on 99% of their textures, so there it rarely
    /// changes anything (MdxProbe --wrapscan). The texture that decides is the one the surface
    /// shows: the first layer that is not the flat team-colour fill, whose tiling cannot be seen.
    /// </remarks>
    public static (bool U, bool V) WrapOf(MdxModel model, MdxMaterial material)
    {
        MdxTexture? decisive = null;
        foreach (var layer in material.Layers)
        {
            int id = layer.DiffuseTextureId;
            if ((uint)id >= (uint)model.Textures.Count) continue;
            decisive ??= model.Textures[id];
            if (!model.Textures[id].IsTeamColor) { decisive = model.Textures[id]; break; }
        }
        return decisive is null ? (true, true) : ((decisive.Flags & 1) != 0, (decisive.Flags & 2) != 0);
    }

    /// <summary>True when no texel carries visible colour — Blizzard's <c>Black32.blp</c> placeholder.</summary>
    private static bool IsBlack(RgbaImage image)
    {
        var px = image.Pixels;
        for (int i = 0; i < px.Length; i += 4)
            if (px[i] > 3 || px[i + 1] > 3 || px[i + 2] > 3) return false;
        return true;
    }

    /// <summary>
    /// A uniform mask — the whole surface at one strength. One 4x4 block, because that is the unit
    /// a block-compressed DDS is made of and the export writes every mask as one.
    /// </summary>
    private static TeamMask Solid(byte value) => new()
    {
        Width = 4, Height = 4, Values = Enumerable.Repeat(value, 16).ToArray(),
    };

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RgbaImage, TeamMask?[]> MaskCache = new();

    /// <summary>
    /// An image's alpha channel as a mask, or null when it carries no usable signal.
    /// </summary>
    /// <param name="invert">
    /// True for the <b>classic</b> reading, where the mask is <c>1 - alpha</c> of the diffuse drawn
    /// over a replaceable-1 fill; false for the <b>Reforged</b> one, where it is the ORM's alpha.
    /// </param>
    /// <remarks>
    /// One acceptance rule for both: the mask is usable when any texel carries player colour at
    /// all, and <c>TeamMask.Coverage</c> throws out the ones too small to be worth a texture. Both
    /// engines apply the channel <i>continuously</i> — Warcraft III's HD shader mixes the team hue
    /// in by <c>sqrt(a)</c> with no gate, and the classic path blends <c>team x (1 - a)</c> — so a
    /// soft, midtone or even uniform mask is ordinary art, not a defect.
    /// <para>
    /// Two stricter rules were tried here and each cost real art. Demanding both dark and bright
    /// texels (a "painted-on" selection) threw out the Reforged ORMs whose alpha is 255 throughout —
    /// the footman's helmet plume, ship sails — which the game draws entirely in the player's
    /// colour. Applied to the classic reading it rejected any mask whose team region never reaches
    /// alpha 63: 23 of 705 classic team materials across 580 stock models, <c>altarofkings</c> and
    /// both Pandaren Brewmasters among them, and because <see cref="Compose"/> has by then already
    /// subtracted the player's contribution to black, those surfaces exported <b>black</b> rather
    /// than merely uncoloured. Any future "mask rejected" path must ask what the composite did.
    /// </para>
    /// </remarks>
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
        int any = 0;
        for (int i = 0; i < n; i++)
        {
            byte a = img.Pixels[i * 4 + 3];
            byte v = invert ? (byte)(255 - a) : a;
            values[i] = v;
            if (v > 8) any++;
        }
        if (any == 0) return null;
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

    /// <summary>
    /// A particle sprite as a player-colour mask: how much light the card puts on screen at each
    /// texel, which is what the live colour has to be multiplied by.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="GlowMask"/> this folds the alpha in as well. A glow card's art is the
    /// colour itself and its alpha is flat; an ordinary sprite carries its shape in whichever
    /// channel its blend mode reads — an additive beam in its RGB, an alpha-blended one in its
    /// alpha — and a mask that ignored either would spill colour into the card's empty corners.
    /// <para>
    /// Null when the card is blank, and null when the art carries <b>colour of its own</b>. A mask
    /// is a single channel, so handing the player's colour the whole card only reproduces what the
    /// effect drew if the art was greyscale to begin with — which is what the effects that ask for
    /// a player colour use, 24 of the 32 sprites involved saying so in their file name
    /// (<c>FlareSimple_BW</c>, <c>HeroGlow_BW</c>). A sprite with its own hue would come out
    /// stripped of it, so it keeps its colours and forgoes the live channel.
    /// </para>
    /// <para>
    /// <paramref name="foldAlpha"/> false leaves the alpha out, for a card drawn alpha-blended in
    /// StarCraft II too: there the sprite's alpha is the card's coverage, applied by the blend
    /// itself, and folding it into the mask as well would apply it twice.
    /// </para>
    /// </remarks>
    public static TeamMask? SpriteMask(RgbaImage sprite, bool foldAlpha = true)
    {
        int n = sprite.Pixels.Length / 4;
        if (n == 0) return null;
        var values = new byte[n];
        int any = 0;
        long satSum = 0, satWeight = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            int b = sprite.Pixels[o], g = sprite.Pixels[o + 1], r = sprite.Pixels[o + 2];
            int lum = Math.Max(b, Math.Max(g, r));
            byte v = (byte)(lum * sprite.Pixels[o + 3] / 255);
            values[i] = foldAlpha ? v : (byte)lum;
            if (v > 8) any++;
            // Saturation weighted by how much the texel contributes: the transparent margin of a
            // card is arbitrary and must not decide whether its art is coloured.
            if (lum > 16)
            {
                satSum += (long)(lum - Math.Min(b, Math.Min(g, r))) * v;
                satWeight += v;
            }
        }
        if (any == 0) return null;
        if (satWeight > 0 && satSum / satWeight > 40) return null;      // ~16% saturation
        return new TeamMask { Width = sprite.Width, Height = sprite.Height, Values = values };
    }

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
