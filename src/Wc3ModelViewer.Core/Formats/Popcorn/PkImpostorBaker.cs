using System.Numerics;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>How baked light comes down from HDR to the screen — the operator Warcraft III's renderer is stood in for.</summary>
public enum PkToneMap
{
    /// <summary>Clamp at 1 per channel: what StarCraft II itself does.</summary>
    Clip,
    /// <summary><c>x / (1 + x)</c> per channel: never saturates, soft everywhere.</summary>
    Reinhard,
    /// <summary>The ACES filmic fit (Narkowicz), per channel: a bright core, a shoulder, a hue that clips like Warcraft III's.</summary>
    Aces,
}

/// <summary>What the baker is told about the sheet, the camera and the tone.</summary>
public sealed class PkImpostorOptions
{
    public int Columns { get; init; } = 8;
    public int Rows { get; init; } = 8;
    /// <summary>
    /// The smallest cell, in pixels. 128 is Blizzard's own 8x8 sheets at 1024²; an effect whose
    /// footprint would spread more than <see cref="TargetUnitsPerPixel"/> over it doubles the cell,
    /// up to <see cref="MaxCellSize"/>.
    /// </summary>
    public int CellSize { get; init; } = 128;
    public int MaxCellSize { get; init; } = 256;
    /// <summary>Model units one atlas pixel may cover before the cell is doubled; 2.5 keeps a 600-unit spell on 256-pixel cells.</summary>
    public float TargetUnitsPerPixel { get; init; } = 2.5f;
    /// <summary>The raster runs at this multiple of the cell and is box-filtered down.</summary>
    public int Supersample { get; init; } = 2;
    /// <summary>Camera pitch above the horizontal. Warcraft III's default angle of attack is 304°, 56° down.</summary>
    public float PitchDegrees { get; init; } = 56f;
    /// <summary>Camera heading; 0 puts the camera on the model's +X side, the face a unit at its default 270° facing shows the editor.</summary>
    public float YawDegrees { get; init; } = 0f;
    /// <summary>Where scripts that read the view position find the camera, metres from the effect.</summary>
    public float CameraDistanceMetres { get; init; } = 20f;
    public float Exposure { get; init; } = 1f;
    /// <summary>
    /// Clip, measured against the Warcraft III editor (2026-09-25). The editor's renderer has no
    /// filmic curve — it blends additively and saturates at 1 — and the recordings say so: over the
    /// parked fireball of <c>VfxFireball2-wc3editor</c>, matched on the radius where the added light
    /// falls to a tenth, Clip at exposure 1 fits the reference's radial profile to an rms of 0.010
    /// and its ring colours to a few levels, where ACES fits to 0.045 and runs too bright through
    /// the shoulder. On <c>VfxUnholyAuraA</c>, a ring rather than a ball and so compared by the
    /// distribution of drawn brightness instead, ACES is the worst operator at every exposure
    /// (0.29–0.41 total variation) and Clip the best (0.20–0.27).
    /// </summary>
    public PkToneMap ToneMap { get; init; } = PkToneMap.Clip;
    /// <summary>
    /// A one-shot effect longer than this is cut here and faded out over <see cref="TailFade"/> of
    /// its frames; one still drawing at three times this is a steady state instead.
    /// </summary>
    public float MaxDuration { get; init; } = 6f;
    public float TailFade { get; init; } = 0.1f;
    /// <summary>The period a continuous effect is baked over; an orbiting one takes its own period instead.</summary>
    public float LoopPeriod { get; init; } = 2f;
    /// <summary>Padding round the effect's footprint, as a fraction of its side, each side.</summary>
    public float Margin { get; init; } = 0.10f;
    /// <summary>Alpha ramps to zero over this fraction of the cell at its border, so cells never bleed into their neighbours.</summary>
    public float EdgeFeather { get; init; } = 0.04f;
    public float Dt { get; init; } = 1f / 60f;
    public int Seed { get; init; } = 7;
    /// <summary>Keep every frame as its own image, for looking at.</summary>
    public bool KeepFrames { get; init; }
    /// <summary>Build the sheet for an additive card (alpha = brightness) rather than an alpha-blended one.</summary>
    public bool AlphaAdd { get; init; }
    public int MaxSpritesPerFrame { get; init; } = 4096;

    /// <summary>
    /// Metres per second the trail bake flies the effect at; 18 (900 Warcraft III units) is the
    /// missile speed the stand-ins' trail rates are measured at. The strip is a picture of the
    /// trail at this speed, and StarCraft II stretches it over whatever length the model draws.
    /// </summary>
    public float TrailSpeedMetres { get; init; } = PkRendererStats.MissileSpeed;
    /// <summary>Moments of the flight averaged into the strip, a thirtieth of a second apart. 1 is one moment, licks and all.</summary>
    public int TrailFrames { get; init; } = 1;
    /// <summary>Model units per texel of the strip, unless the size caps below force coarser.</summary>
    public float TrailUnitsPerPixel { get; init; } = 0.5f;
    public int TrailMaxLengthPixels { get; init; } = 2048;
    public int TrailMaxWidthPixels { get; init; } = 512;
    /// <summary>
    /// Whether an additive ribbon's light is scaled by its points' colour alpha, as a billboard's
    /// is. Off by default: Warcraft III's fireball comet is saturated white-yellow along nearly its
    /// whole 300 units and 55 units wide, which its two ribbons — (11,6,2.7) and (4.7,1.8,0) under a
    /// wedge texture, but alpha 0.26 at most — only produce with the alpha left out; with it they
    /// are a faint orange haze, which is what the per-layer stand-ins drew in StarCraft II.
    /// </summary>
    public bool RibbonColourAlpha { get; init; }

    /// <summary>
    /// Diagnostic: bake only these layers (by <see cref="PkRendererStats.LayerName"/>), so one
    /// layer's contribution to the sheet can be seen on its own. Null bakes every bakeable layer,
    /// which is what an export does.
    /// </summary>
    public IReadOnlyCollection<string>? OnlyLayers { get; init; }

    /// <summary>
    /// Diagnostic: keep the footprint and the frame timing of the full bake even when
    /// <see cref="OnlyLayers"/> narrows what is drawn, so isolated layers line up with each other
    /// and with the whole. Set by the probe; an export never uses it.
    /// </summary>
    public (float CentreX, float CentreY, float Side)? ForceFootprint { get; init; }
}

/// <summary>
/// The picture of an effect's trail as it flies — the layers spawned per distance and its ribbons,
/// seen from beside the flight line — for one StarCraft II ribbon to lay behind the model.
/// </summary>
public sealed class PkTrailBake
{
    /// <summary>The strip's texture: x runs along the strip from the head at the left edge, y across it.</summary>
    public required RgbaImage Texture { get; init; }
    /// <summary>Head to tail, model units, when flying at <see cref="Speed"/>.</summary>
    public required float Length { get; init; }
    /// <summary>Full width, model units.</summary>
    public required float Width { get; init; }
    /// <summary>Seconds a point of the strip lives: <see cref="Length"/> at <see cref="Speed"/>.</summary>
    public required float Duration { get; init; }
    /// <summary>Model units per second the bake flew at.</summary>
    public required float Speed { get; init; }
    /// <summary>Every baked layer adds light, so the strip is built for an additive ribbon (alpha = brightness).</summary>
    public bool Additive { get; init; }
    /// <summary>The renderers whose sprites and points went into the strip; their stand-ins are redundant now.</summary>
    public required IReadOnlySet<PkRendererDef> Renderers { get; init; }
    public required IReadOnlyList<string> Layers { get; init; }
    public float PeakLinear { get; init; }
    public List<string> Log { get; } = [];
}

/// <summary>One baked sheet: the atlas, what it covers, and how long it plays.</summary>
public sealed class PkImpostorBake
{
    public required RgbaImage Atlas { get; init; }
    public required int Columns { get; init; }
    public required int Rows { get; init; }
    /// <summary>Seconds the frames span — the StarCraft II particle's lifespan.</summary>
    public required float Duration { get; init; }
    /// <summary>A steady-state effect baked over one <see cref="Duration"/>-long period, to be cross-faded.</summary>
    public required bool Loops { get; init; }
    /// <summary>The effect never changes: the sheet is one cell, and one card holds it.</summary>
    public bool Static { get; init; }
    /// <summary>Every baked layer adds light, so the sheet is built for an additive card (alpha = brightness).</summary>
    public bool Additive { get; init; }
    /// <summary>Centre of the square the cell covers, model units, relative to the effect's node.</summary>
    public required Vector3 Centre { get; init; }
    /// <summary>Half the side of that square, model units.</summary>
    public required float HalfSize { get; init; }
    /// <summary>The renderers whose sprites went into the sheet; their stand-ins are redundant now.</summary>
    public required IReadOnlySet<PkRendererDef> Renderers { get; init; }
    public required IReadOnlyList<string> Layers { get; init; }
    /// <summary>The brightest linear value any frame reached before tone mapping.</summary>
    public float PeakLinear { get; init; }
    /// <summary>Share of the sheet's drawn texels that came out fully bright and fully opaque.</summary>
    public float SaturatedShare { get; init; }
    public List<RgbaImage>? Frames { get; init; }
    public List<string> Log { get; } = [];
}

/// <summary>
/// Renders a PopcornFX effect, as the VM plays it, into a flipbook sheet — an impostor that a single
/// StarCraft II particle plays end to end. Warcraft III tone-maps its HDR particles and StarCraft II
/// does not, so the sheet carries the picture the Warcraft III renderer would show rather than the
/// numbers that made it.
/// </summary>
public static class PkImpostorBaker
{
    /// <summary>
    /// A layer the sheet can stand in for: a billboard with a sprite, not a ribbon (StarCraft II has
    /// its own), not one spawned per distance travelled (only a moving emitter draws it) and not one
    /// in the player's colour (the sheet would freeze one player's).
    /// </summary>
    public static bool IsBakeable(PkRendererStats s)
        => s.Renderer.Kind == PkRendererKind.Billboard && !s.TeamColoured && s.TrailRate == 0 && s.Renderer.Texture.Length > 0;

    /// <summary>Null when nothing bakeable draws, or no sprite texture loads.</summary>
    public static PkImpostorBake? Bake(PkEffectDef def, IReadOnlyList<PkRendererStats> stats,
                                       Func<string, RgbaImage?> loadTexture, PkImpostorOptions o,
                                       Vector4? colourMultiplier = null)
    {
        var log = new List<string>();
        int cols = Math.Max(1, o.Columns), rows = Math.Max(1, o.Rows);
        int cells = cols * rows;

        // ---- which layers, and their sprites ----
        var sprites = new Dictionary<PkRendererDef, LinearSprite>(ReferenceEqualityComparer.Instance);
        var byPath = new Dictionary<string, LinearSprite?>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var s in stats)
        {
            if (!IsBakeable(s)) { kept.Add($"{s.LayerName} ({Reason(s)})"); continue; }
            if (o.OnlyLayers is not null && !o.OnlyLayers.Contains(s.LayerName)) { kept.Add($"{s.LayerName} (not in --onlylayer)"); continue; }
            if (sprites.ContainsKey(s.Renderer)) continue;
            if (!byPath.TryGetValue(s.Renderer.Texture, out var sprite))
            {
                var sheet = loadTexture(s.Renderer.Texture);
                sprite = sheet is null ? null : new LinearSprite(sheet);
                byPath[s.Renderer.Texture] = sprite;
            }
            if (sprite is null) { kept.Add($"{s.LayerName} (sprite {Path.GetFileName(s.Renderer.Texture)} not found)"); continue; }
            sprites[s.Renderer] = sprite;
        }
        if (sprites.Count == 0) return null;
        var renderers = new HashSet<PkRendererDef>(sprites.Keys, ReferenceEqualityComparer.Instance);
        var bakeable = stats.Where(s => renderers.Contains(s.Renderer)).ToList();
        var layers = bakeable.Select(s => s.LayerName).Distinct().ToList();
        // Light that only adds is drawn by an additive card, where a cross-fade of two cards is
        // exact and nothing darkens the ground; a sheet with anything laid over needs the blended
        // card and its matte.
        bool additive = o.AlphaAdd || bakeable.All(s => s.Renderer.Blend is PopcornBlend.Additive or PopcornBlend.AdditiveNoAlpha);

        var basis = Basis(o);
        PkEnvironment Env() => new()
        {
            ColorMultiplier = colourMultiplier ?? Vector4.One,
            CameraPosition = -basis.Look * o.CameraDistanceMetres,
        };

        // ---- when: one run of a one-shot, or one period of a steady state ----
        bool loops = bakeable.Any(s => s.Continuous || s.Immortal);
        float start, duration;
        bool capped = false;
        float last = -1;
        if (!loops)
        {
            // An effect still drawing long after the cap is not a one-shot, whatever the stats said
            // of its layers: a glow that lasts as long as its sequence loops. One that merely
            // outlives the cap a little (the angel's 4.4 s glow) is cut and faded.
            last = LastVisible(def, Env(), renderers, o);
            if (last < 0) return null;
            if (last >= 3 * o.MaxDuration - 2 * o.Dt) loops = true;
        }
        if (loops)
        {
            duration = o.LoopPeriod;
            // An orbit's own period, so the cross-fade lands on the same pose — the period of the
            // ring's symmetry, as PopcornApproximation reads it: four runes sharing one angular
            // velocity repeat every quarter turn.
            var orbiting = bakeable.Where(s => MathF.Abs(s.OrbitAngularVelocity) > 1e-3f).OrderByDescending(s => MathF.Abs(s.OrbitAngularVelocity)).FirstOrDefault();
            if (orbiting is not null)
            {
                float w = MathF.Abs(orbiting.OrbitAngularVelocity);
                int symmetry = Math.Max(1, bakeable.Count(s => s.LayerName == orbiting.LayerName
                                                            && MathF.Abs(MathF.Abs(s.OrbitAngularVelocity) - w) < 0.05f * w));
                duration = Math.Clamp(2 * MathF.PI / (w * symmetry), 0.5f, 3f);
            }
            // Warm up to a steady state first — a period or a lifetime, whichever is longer, but
            // an immortal layer's "life" is the run's length and would only cost time.
            start = Math.Clamp(MathF.Max(duration, bakeable.Max(s => s.Life)), 1f, 3f);
        }
        else
        {
            capped = last + o.Dt > o.MaxDuration;
            duration = MathF.Min(last + o.Dt, o.MaxDuration);
            start = 0;
        }
        float dt = MathF.Min(o.Dt, duration / cells);

        // ---- what: every frame's sprites ----
        var frames = Sample(def, Env(), renderers, start, duration, cells, dt, o);
        var (cx, cy, side, depth) = Footprint(frames, basis, o.Margin);
        if (o.ForceFootprint is { } ff) (cx, cy, side) = (ff.CentreX, ff.CentreY, ff.Side);
        if (side <= 1e-3f) return null;

        // A picture that never changes (a hero's glow) is one cell held by one card, not 64 of the
        // same thing and a cross-fade between them.
        bool isStatic = IsStatic(frames, side);
        if (isStatic)
        {
            frames = [frames[0]];
            cols = rows = 1; cells = 1;
            loops = true; capped = false;
            duration = o.LoopPeriod;
        }

        // ---- the picture ----
        int cell = Math.Max(16, o.CellSize);
        while (cell < o.MaxCellSize && side / cell > o.TargetUnitsPerPixel) cell *= 2;
        int n = cell * o.Supersample;
        var black = new float[n * n * 3];
        var white = new float[n * n * 3];
        int atlasW = cols * cell, atlasH = rows * cell;
        var atlas = new byte[atlasW * atlasH * 4];
        float peak = 0;
        long drawn = 0, saturated = 0;
        int fadeFrames = capped ? Math.Max(1, (int)MathF.Round(cells * o.TailFade)) : 0;
        for (int k = 0; k < cells; k++)
        {
            Array.Fill(black, 0f);
            Array.Fill(white, 1f);
            Rasterise(frames[k], basis, cx, cy, side, n, sprites, black, white, ref peak);
            float tail = fadeFrames > 0 && k >= cells - fadeFrames ? (cells - k) / (float)(fadeFrames + 1) : 1f;
            Resolve(black, white, n, cell, cols, o, additive, k, tail, atlas, atlasW, ref drawn, ref saturated);
        }
        var image = new RgbaImage { Width = atlasW, Height = atlasH, Pixels = atlas };

        var bake = new PkImpostorBake
        {
            Atlas = image, Columns = cols, Rows = rows,
            Duration = duration, Loops = loops, Static = isStatic, Additive = additive,
            Centre = basis.Right * cx + basis.Up * cy + basis.Look * depth,
            HalfSize = side / 2,
            Renderers = renderers, Layers = layers,
            PeakLinear = peak,
            SaturatedShare = drawn == 0 ? 0 : saturated / (float)drawn,
            Frames = o.KeepFrames ? Cells(image, cols, rows, cell) : null,
        };
        bake.Log.Add($"baked {layers.Count} layer(s) for an {(additive ? "additive" : "alpha-blended")} card: {string.Join(", ", layers)}");
        if (kept.Count > 0) bake.Log.Add($"kept as stand-ins: {string.Join(", ", kept)}");
        bake.Log.Add(isStatic ? $"static: one cell, one card renewed every {duration:0.00} s"
                   : loops ? $"steady state: one {duration:0.00} s period after {start:0.0} s, to be cross-faded"
                           : $"one-shot: {duration:0.00} s{(capped ? " (cut at the cap, tail faded)" : "")}");
        bake.Log.Add($"footprint {side:0.#} units square at ({bake.Centre.X:0.#}, {bake.Centre.Y:0.#}, {bake.Centre.Z:0.#}) [screen {cx:0.###} {cy:0.###} {side:0.###}]; "
                     + $"{cells} frame(s) of {cell} px ({side / cell:0.0} units/px); peak linear {peak:0.0}; {bake.SaturatedShare:P0} of drawn texels saturated");
        bake.Log.AddRange(log);
        return bake;
    }

    private static string Reason(PkRendererStats s) => s.Renderer.Kind switch
    {
        PkRendererKind.Ribbon => "ribbon",
        PkRendererKind.Billboard when s.TeamColoured => "team colour",
        PkRendererKind.Billboard when s.TrailRate > 0 => "spawned per distance",
        PkRendererKind.Billboard => "no sprite",
        var k => k.ToString().ToLowerInvariant(),
    };

    // ---- the trail ------------------------------------------------------------------------------

    /// <summary>
    /// A layer the trail strip stands in for: a ribbon, or a sprite layer spawned per distance
    /// travelled — what a moving effect leaves behind it. Player-coloured layers stay out, as they
    /// do of the sheet.
    /// </summary>
    public static bool IsTrailLayer(PkRendererStats s)
        => !s.TeamColoured && s.Renderer.Texture.Length > 0
           && (s.Renderer.Kind == PkRendererKind.Ribbon || (s.Renderer.Kind == PkRendererKind.Billboard && s.TrailRate > 0));

    /// <summary>
    /// Flies the effect at <see cref="PkImpostorOptions.TrailSpeedMetres"/> until its trail is fully
    /// formed and renders what trails behind — every per-distance sprite and every ribbon point,
    /// in the effect's own frame, from beside the flight line — into one strip texture with the
    /// head at its left edge. Warcraft III's fireball is a comet because 165 flame sprites a second
    /// pile up along 400 units of path and its tone mapper fuses them; drawn one by one in
    /// StarCraft II the same layers were a line of beads. Null when the effect has no trail layers,
    /// none of their textures load, or nothing trails.
    /// </summary>
    public static PkTrailBake? BakeTrail(PkEffectDef def, IReadOnlyList<PkRendererStats> stats,
                                         Func<string, RgbaImage?> loadTexture, PkImpostorOptions o,
                                         Vector4? colourMultiplier = null)
    {
        // ---- which layers, and their textures ----
        var sprites = new Dictionary<PkRendererDef, LinearSprite>(ReferenceEqualityComparer.Instance);
        var byPath = new Dictionary<string, LinearSprite?>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var s in stats)
        {
            if (!IsTrailLayer(s) || sprites.ContainsKey(s.Renderer)) continue;
            if (!byPath.TryGetValue(s.Renderer.Texture, out var sprite))
            {
                var sheet = loadTexture(s.Renderer.Texture);
                sprite = sheet is null ? null : new LinearSprite(sheet);
                byPath[s.Renderer.Texture] = sprite;
            }
            if (sprite is null) { missing.Add($"{s.LayerName} ({Path.GetFileName(s.Renderer.Texture)} not found)"); continue; }
            sprites[s.Renderer] = sprite;
        }
        if (sprites.Count == 0) return null;
        var renderers = new HashSet<PkRendererDef>(sprites.Keys, ReferenceEqualityComparer.Instance);
        var billboards = new HashSet<PkRendererDef>(renderers.Where(r => r.Kind == PkRendererKind.Billboard), ReferenceEqualityComparer.Instance);
        var ribbons = new HashSet<PkRendererDef>(renderers.Where(r => r.Kind == PkRendererKind.Ribbon), ReferenceEqualityComparer.Instance);
        var trailStats = stats.Where(s => renderers.Contains(s.Renderer)).ToList();
        var layers = trailStats.Select(s => s.LayerName).Distinct().ToList();
        bool additive = o.AlphaAdd || trailStats.All(s => s.Renderer.Blend is PopcornBlend.Additive or PopcornBlend.AdditiveNoAlpha);

        // ---- the camera, beside the flight line ----
        // The effect flies toward -X, so its trail lies along +X; the camera stands on the -Y side
        // at the sheet's pitch and sees the strip left to right, head first. A StarCraft II ribbon
        // is a planar billboard, so it too is always seen across its full width.
        var basis = Basis(o.PitchDegrees, o.YawDegrees - 90f);
        float m = PopcornApproximation.MetresToWc3;
        float speedUnits = o.TrailSpeedMetres * m;
        var env = new PkEnvironment
        {
            ColorMultiplier = colourMultiplier ?? Vector4.One,
            CameraPosition = -basis.Look * o.CameraDistanceMetres,
        };

        // ---- fly it until the trail is whole, then take the moment(s) ----
        var fx = new PkEffectInstance(def, env, o.Seed);
        var batches = Batches(def, billboards);
        float life = trailStats.Max(s => s.Life);
        float warm = Math.Clamp(life + 0.5f, 0.75f, 4f);
        var frames = new List<TrailFrame>();
        float t = 0;
        for (int k = 0; k < Math.Max(1, o.TrailFrames); k++)
        {
            float target = warm + k / 30f;
            while (t < target && fx.IsAlive)
            {
                t += o.Dt;
                env.LocalToWorld = Matrix4x4.CreateTranslation(-o.TrailSpeedMetres * t, 0, 0);
                fx.Update(o.Dt);
            }
            if (!fx.IsAlive) break;
            var origin = new Vector3(-o.TrailSpeedMetres * t * m, 0, 0);
            foreach (var b in batches) b.Sprites.Clear();
            PkCornPlayer.CollectSprites(fx, batches, m);
            var frame = new TrailFrame();
            foreach (var b in batches)
                foreach (var s in b.Sprites)
                {
                    if (frame.Sprites.Count >= o.MaxSpritesPerFrame) break;
                    var rel = s; rel.Position -= origin;
                    frame.Sprites.Add((b.Renderer, rel));
                }
            frame.Strips = CollectStrips(fx, ribbons, origin, m);
            frames.Add(frame);
        }
        if (frames.Count == 0 || frames.All(f => f.Sprites.Count == 0 && f.Strips.Count == 0)) return null;

        // ---- how long and how wide: the 98th percentile behind the head, across the line ----
        var xs = new List<float>(); var ys = new List<float>();
        foreach (var f in frames)
        {
            foreach (var (r, s) in f.Sprites)
            {
                PkSpriteGeometry.Quad(in s, r.Billboard, basis.Right, basis.Up, basis.Look, out var rv, out var uv);
                foreach (var corner in new[] { s.Position - rv - uv, s.Position + rv - uv, s.Position + rv + uv, s.Position - rv + uv })
                {
                    xs.Add(Vector3.Dot(corner, basis.Right));
                    ys.Add(MathF.Abs(Vector3.Dot(corner, basis.Up)));
                }
            }
            foreach (var strip in f.Strips)
                foreach (var p in strip.Points)
                {
                    xs.Add(Vector3.Dot(p.Position, basis.Right) + p.Half);
                    ys.Add(MathF.Abs(Vector3.Dot(p.Position, basis.Up)) + p.Half);
                }
        }
        xs.Sort(); ys.Sort();
        float length = Pct(xs, 0.98f) * (1 + o.Margin);
        float width = 2 * Pct(ys, 0.98f) * (1 + o.Margin);
        if (length < 8f || width < 2f) return null;

        // The strip fills a power-of-two texture at one scale along and across; the caps set the
        // scale when a long trail would outgrow them.
        float upp = MathF.Max(o.TrailUnitsPerPixel, MathF.Max(length / o.TrailMaxLengthPixels, width / o.TrailMaxWidthPixels));
        int nx = Pow2(MathF.Ceiling(length / upp), 64, o.TrailMaxLengthPixels);
        int ny = Pow2(MathF.Ceiling(width / upp), 16, o.TrailMaxWidthPixels);
        length = nx * upp; width = ny * upp;

        // ---- the picture ----
        int ss = Math.Max(1, o.Supersample);
        int wx = nx * ss, wy = ny * ss;
        var black = new float[wx * wy * 3];
        var white = new float[wx * wy * 3];
        float[]? sumB = frames.Count > 1 ? new float[black.Length] : null;
        float[]? sumW = frames.Count > 1 ? new float[white.Length] : null;
        float peak = 0, scale = wx / length;
        foreach (var f in frames)
        {
            Array.Fill(black, 0f);
            Array.Fill(white, 1f);
            // Smoke under fire: Warcraft III's comet is saturated flame with the smoke showing only
            // where the flame has faded, so what is laid over goes down first and the light on top.
            RasteriseInto(f.Sprites.Where(s => s.Renderer.Blend is PopcornBlend.AlphaBlend or PopcornBlend.PremultipliedAlpha).ToList(),
                          basis, 0f, width / 2, scale, wx, wy, sprites, black, white, ref peak);
            RasteriseStrips(f.Strips, sprites, basis, 0f, width / 2, scale, wx, wy, black, white, ref peak, o.RibbonColourAlpha);
            RasteriseInto(f.Sprites.Where(s => s.Renderer.Blend is not (PopcornBlend.AlphaBlend or PopcornBlend.PremultipliedAlpha)).ToList(),
                          basis, 0f, width / 2, scale, wx, wy, sprites, black, white, ref peak);
            if (sumB is not null && sumW is not null)
                for (int i = 0; i < black.Length; i++) { sumB[i] += black[i]; sumW[i] += white[i]; }
        }
        if (sumB is not null && sumW is not null)
        {
            float k = 1f / frames.Count;
            for (int i = 0; i < black.Length; i++) { black[i] = sumB[i] * k; white[i] = sumW[i] * k; }
        }

        var px = new byte[nx * ny * 4];
        float feather = o.EdgeFeather * ny;
        float inv = 1f / (ss * ss);
        long drawn = 0, saturated = 0;
        Span<float> b3 = stackalloc float[3];
        Span<float> w3 = stackalloc float[3];
        Span<float> db = stackalloc float[3];
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
            {
                b3.Clear(); w3.Clear();
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        int idx = ((y * ss + sy) * wx + x * ss + sx) * 3;
                        b3[0] += black[idx]; b3[1] += black[idx + 1]; b3[2] += black[idx + 2];
                        w3[0] += white[idx]; w3[1] += white[idx + 1]; w3[2] += white[idx + 2];
                    }
                var (alpha, bright) = ResolveTexel(b3, w3, inv, o, additive, db);
                // The head end joins the head card and keeps its edge; the sides and the tail feather out.
                float d = MathF.Min(MathF.Min(y + 0.5f, ny - y - 0.5f), nx - x - 0.5f);
                float edge = feather <= 0 ? 1f : Smooth(Math.Clamp(d / feather, 0f, 1f));
                float outAlpha = alpha * edge;
                int at = (y * nx + x) * 4;
                if (alpha > 1f / 255)
                {
                    px[at] = ToByte(db[2] / alpha); px[at + 1] = ToByte(db[1] / alpha); px[at + 2] = ToByte(db[0] / alpha);
                }
                px[at + 3] = ToByte(outAlpha);
                if (outAlpha > 1f / 255)
                {
                    drawn++;
                    if (outAlpha >= 0.98f && bright >= 0.98f) saturated++;
                }
            }

        // Crop to what actually drew, then fill the sheet with it. The footprint is measured from
        // sprite corners, which a wide faint smoke puff stretches far past the flame: the fireball's
        // strip came out 512 x 128 units with a mean alpha of 5%, and a ribbon that samples a
        // reduced mip of that is too faint to see at all — swapping in an opaque sheet made the same
        // ribbon draw plainly, which is what proved the geometry was never the problem. Cropping
        // keeps the head at x=0 and only trims empty tail and margins, so the picture is unchanged;
        // resampling back up spends every texel on flame instead of emptiness.
        var (cropX, cropY0, cropY1) = AlphaExtent(px, nx, ny);
        if (cropX > 8 && cropY1 > cropY0)
        {
            int cw = cropX + 1, ch = cropY1 - cropY0 + 1;
            px = Resample(px, nx, ny, 0, cropY0, cw, ch, nx, ny);
            length = cw * upp;
            width = ch * upp;
        }

        var bake = new PkTrailBake
        {
            Texture = new RgbaImage { Width = nx, Height = ny, Pixels = px },
            Length = length, Width = width,
            Duration = length / speedUnits, Speed = speedUnits,
            Additive = additive,
            Renderers = renderers, Layers = layers,
            PeakLinear = peak,
        };
        int points = frames[0].Strips.Sum(s => s.Points.Count);
        bake.Log.Add($"trail: {layers.Count} layer(s) baked into one {(additive ? "additive" : "alpha-blended")} strip: {string.Join(", ", layers)}"
                     + (missing.Count > 0 ? $"; not found: {string.Join(", ", missing)}" : ""));
        foreach (var strip in frames[0].Strips)
        {
            var head = strip.Points[0]; var tail = strip.Points[^1];
            bake.Log.Add($"ribbon {Path.GetFileName(strip.Renderer.Texture)}: {strip.Points.Count} points, U {(strip.ScriptedU ? "scripted" : "by index")} "
                         + $"{head.U:0.00} at the head ({Vector3.Dot(head.Position, basis.Right):0} u, half-width {head.Half:0.0}) to {tail.U:0.00} at the tail "
                         + $"({Vector3.Dot(tail.Position, basis.Right):0} u, half-width {tail.Half:0.0}); head colour ({head.Colour.X:0.0},{head.Colour.Y:0.0},{head.Colour.Z:0.0},{head.Colour.W:0.00})");
        }
        bake.Log.Add($"flown at {speedUnits:0} units/s for {t:0.00} s ({frames.Count} moment(s), {frames[0].Sprites.Count} sprite(s), "
                     + $"{frames[0].Strips.Count} ribbon(s) of {points} point(s)); strip {length:0} x {width:0} units, {bake.Duration:0.00} s of flight, "
                     + $"{nx}x{ny} texels; peak linear {peak:0.0}; {(drawn == 0 ? 0 : saturated / (double)drawn):P0} of drawn texels saturated; "
                     + $"mean alpha {MeanAlpha(bake.Texture):P0}");
        return bake;
    }

    private sealed class TrailFrame
    {
        public List<(PkRendererDef Renderer, PkSprite Sprite)> Sprites { get; } = [];
        public List<Strip> Strips { get; set; } = [];
    }

    /// <summary>
    /// The points of one ribbon this frame, newest first, in model units relative to the effect.
    /// <c>U</c> is the texture coordinate along the strip at each point: the layer's own
    /// <c>TextureU</c> input where its scripts write one, else 0 at the head to 1 at the tail.
    /// </summary>
    private sealed class Strip
    {
        public required PkRendererDef Renderer { get; init; }
        public bool ScriptedU { get; init; }
        public List<(Vector3 Position, float Half, Vector4 Colour, float U)> Points { get; } = [];
    }

    /// <summary>
    /// Reads every ribbon renderer's particles straight from the layer slots — the sprite collector
    /// skips ribbons, as the viewer has nothing to draw them with — ordered from the newest point
    /// (the head) to the oldest (the tail), by life ratio where the layer keeps one and by birth
    /// order otherwise.
    /// </summary>
    private static List<Strip> CollectStrips(PkEffectInstance fx, HashSet<PkRendererDef> ribbons, Vector3 origin, float m)
    {
        var strips = new List<Strip>();
        if (ribbons.Count == 0) return strips;
        foreach (var st in fx.Slots)
        {
            if (st is null || st.Count == 0) continue;
            foreach (var r in st.Def.Renderers)
            {
                if (!ribbons.Contains(r)) continue;
                int fPos = r.Input("Position"), fSize = r.Input("Size"), fSize2 = r.Input("Size2"), fCol = r.Input("Color"), fEn = r.Input("Enabled");
                int fU = r.Input("TextureU");
                int fl = st.LifeRatioField;
                var points = new List<(float Ratio, long Id, Vector3 P, float Half, Vector4 C, float U)>();
                for (int p = 0; p < st.Count; p++)
                {
                    if (fEn >= 0 && st.Fields[fEn][p].I0 == 0) continue;
                    var pos = fPos >= 0 ? st.Fields[fPos][p].Xyz : Vector3.Zero;
                    if (!float.IsFinite(pos.X) || pos.LengthSquared() > 1e6f) continue;
                    float half = r.Size2D && fSize2 >= 0 ? st.Fields[fSize2][p].X : fSize >= 0 ? st.Fields[fSize][p].X : 1f;
                    var col = fCol >= 0 ? st.Fields[fCol][p].Xyzw : Vector4.One;
                    float ratio = fl >= 0 ? st.Fields[fl][p].X : float.NaN;
                    float u = fU >= 0 ? st.Fields[fU][p].X : float.NaN;
                    points.Add((float.IsFinite(ratio) ? ratio : 0f, st.Ids[p], pos * m - origin, MathF.Abs(half) * m, col, u));
                }
                if (points.Count < 2) continue;
                bool scripted = fU >= 0 && points.All(q => float.IsFinite(q.U));
                var strip = new Strip { Renderer = r, ScriptedU = scripted };
                int n = points.Count, i = 0;
                // Newest first. Two points born in one frame share a life ratio but not a place —
                // the one spawned first in the frame is further back — so ties fall to the serial,
                // newest first; ordered by ratio alone they zig-zagged and drew each other's segment twice.
                foreach (var q in points.OrderBy(q => q.Ratio).ThenByDescending(q => q.Id))
                    strip.Points.Add((q.P, q.Half, q.C, scripted ? q.U : i++ / (float)(n - 1)));
                strips.Add(strip);
            }
        }
        return strips;
    }

    /// <summary>
    /// Draws each ribbon as a strip of quads through its points, the texture's U running along it
    /// from 0 at the head to 1 at the tail (a PopcornFX ribbon lays its texture that way: the
    /// fireball's FlareShot_Trail2 is bright at U=0) and V across it, width and colour interpolated
    /// point to point. The strip is a planar billboard, so it is laid flat in the screen plane.
    /// </summary>
    private static void RasteriseStrips(List<Strip> strips, Dictionary<PkRendererDef, LinearSprite> sprites, CameraBasis c,
                                        float x0, float yTop, float scale, int nx, int ny, float[] black, float[] white, ref float peak,
                                        bool colourAlpha)
    {
        foreach (var strip in strips)
        {
            if (!sprites.TryGetValue(strip.Renderer, out var tex)) continue;
            var r = strip.Renderer;
            bool additive = r.Blend is PopcornBlend.Additive or PopcornBlend.AdditiveNoAlpha;
            // An additive ribbon adds colour times texture; see PkImpostorOptions.RibbonColourAlpha.
            bool noAlpha = additive && (!colourAlpha || r.Blend == PopcornBlend.AdditiveNoAlpha);
            int n = strip.Points.Count;
            var screen = new Vector2[n];
            for (int i = 0; i < n; i++)
                screen[i] = new Vector2(Vector3.Dot(strip.Points[i].Position, c.Right), Vector3.Dot(strip.Points[i].Position, c.Up));
            for (int i = 0; i + 1 < n; i++)
            {
                var p0 = screen[i]; var p1 = screen[i + 1];
                var d = p1 - p0;
                float len = d.Length();
                if (len < 1e-4f) continue;
                d /= len;
                var nrm = new Vector2(-d.Y, d.X);
                float h0 = strip.Points[i].Half, h1 = strip.Points[i + 1].Half, hm = MathF.Max(h0, h1);
                float u0 = strip.Points[i].U, u1 = strip.Points[i + 1].U;
                var c0 = strip.Points[i].Colour; var c1 = strip.Points[i + 1].Colour;
                int minX = Math.Max(0, (int)MathF.Floor((MathF.Min(p0.X, p1.X) - hm - x0) * scale));
                int maxX = Math.Min(nx - 1, (int)MathF.Ceiling((MathF.Max(p0.X, p1.X) + hm - x0) * scale));
                int minY = Math.Max(0, (int)MathF.Floor((yTop - (MathF.Max(p0.Y, p1.Y) + hm)) * scale));
                int maxY = Math.Min(ny - 1, (int)MathF.Ceiling((yTop - (MathF.Min(p0.Y, p1.Y) - hm)) * scale));
                if (minX > maxX || minY > maxY) continue;
                for (int j = minY; j <= maxY; j++)
                {
                    float y = yTop - (j + 0.5f) / scale;
                    for (int k = minX; k <= maxX; k++)
                    {
                        float x = x0 + (k + 0.5f) / scale;
                        var q = new Vector2(x, y) - p0;
                        float along = Vector2.Dot(q, d) / len;
                        if (along < 0 || along > 1) continue;
                        float half = h0 + (h1 - h0) * along;
                        if (half <= 1e-6f) continue;
                        float across = Vector2.Dot(q, nrm) / half;
                        if (across < -1 || across > 1) continue;
                        var t = tex.Sample(u0 + (u1 - u0) * along, (across + 1) * 0.5f);
                        var col = c0 + (c1 - c0) * along;
                        float ta = noAlpha ? 1f : t.W * col.W;
                        float rr = t.X * col.X, gg = t.Y * col.Y, bb = t.Z * col.Z;
                        int idx = (j * nx + k) * 3;
                        if (additive)
                        {
                            black[idx] += rr * ta; black[idx + 1] += gg * ta; black[idx + 2] += bb * ta;
                            white[idx] += rr * ta; white[idx + 1] += gg * ta; white[idx + 2] += bb * ta;
                        }
                        else
                        {
                            ta = Math.Clamp(ta, 0f, 1f);
                            float keep = 1 - ta;
                            black[idx] = black[idx] * keep + rr * ta; black[idx + 1] = black[idx + 1] * keep + gg * ta; black[idx + 2] = black[idx + 2] * keep + bb * ta;
                            white[idx] = white[idx] * keep + rr * ta; white[idx + 1] = white[idx + 1] * keep + gg * ta; white[idx + 2] = white[idx + 2] * keep + bb * ta;
                        }
                        peak = MathF.Max(peak, MathF.Max(black[idx], MathF.Max(black[idx + 1], black[idx + 2])));
                    }
                }
            }
        }
    }

    /// <summary>
    /// How far along the strip anything is drawn, and the rows it lies between — the last column and
    /// the first/last row whose alpha rises above a fiftieth. (-1, 0, -1) when nothing drew.
    /// </summary>
    private static (int LastX, int FirstY, int LastY) AlphaExtent(byte[] px, int nx, int ny)
    {
        const byte floor = 5;              // ~2%: below this a texel contributes nothing visible
        int lastX = -1, firstY = int.MaxValue, lastY = -1;
        for (int y = 0; y < ny; y++)
            for (int x = 0; x < nx; x++)
                if (px[(y * nx + x) * 4 + 3] > floor)
                {
                    if (x > lastX) lastX = x;
                    if (y < firstY) firstY = y;
                    if (y > lastY) lastY = y;
                }
        return (lastX, firstY == int.MaxValue ? 0 : firstY, lastY);
    }

    /// <summary>Box-resamples the rectangle (sx, sy, sw, sh) of a BGRA image to <paramref name="dw"/> x <paramref name="dh"/>.</summary>
    private static byte[] Resample(byte[] src, int srcW, int srcH, int sx, int sy, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int y = 0; y < dh; y++)
        {
            int y0 = sy + y * sh / dh, y1 = Math.Max(y0 + 1, sy + (y + 1) * sh / dh);
            for (int x = 0; x < dw; x++)
            {
                int x0 = sx + x * sw / dw, x1 = Math.Max(x0 + 1, sx + (x + 1) * sw / dw);
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int yy = y0; yy < y1 && yy < srcH; yy++)
                    for (int xx = x0; xx < x1 && xx < srcW; xx++)
                    {
                        int i = (yy * srcW + xx) * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                        n++;
                    }
                int o = (y * dw + x) * 4;
                if (n == 0) continue;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
            }
        }
        return dst;
    }

    private static int Pow2(float value, int min, int max)
    {
        int n = min;
        while (n < value && n < max) n *= 2;
        return Math.Min(n, max);
    }

    private static float Pct(List<float> sorted, float q) => sorted[Math.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];

    /// <summary>How much of the sheet is actually drawn — the number that decides whether a reduced mip still shows.</summary>
    private static float MeanAlpha(RgbaImage image)
    {
        long sum = 0;
        for (int i = 3; i < image.Pixels.Length; i += 4) sum += image.Pixels[i];
        return image.Pixels.Length == 0 ? 0 : sum / (255f * (image.Pixels.Length / 4));
    }

    // ---- camera -------------------------------------------------------------------------------

    private readonly record struct CameraBasis(Vector3 Right, Vector3 Up, Vector3 Look);

    /// <summary>
    /// An orthographic camera pitched <see cref="PkImpostorOptions.PitchDegrees"/> below the
    /// horizontal, on the side <see cref="PkImpostorOptions.YawDegrees"/> names (0 = +X). Its look is
    /// <c>Cross(up, right)</c>, the convention the viewer's sprite quads use.
    /// </summary>
    private static CameraBasis Basis(PkImpostorOptions o) => Basis(o.PitchDegrees, o.YawDegrees);

    private static CameraBasis Basis(float pitchDegrees, float yawDegrees)
    {
        float p = float.DegreesToRadians(pitchDegrees), y = float.DegreesToRadians(yawDegrees);
        var look = new Vector3(-MathF.Cos(p) * MathF.Cos(y), -MathF.Cos(p) * MathF.Sin(y), -MathF.Sin(p));
        var right = new Vector3(-MathF.Sin(y), MathF.Cos(y), 0);
        var up = Vector3.Normalize(Vector3.Cross(right, look));
        return new CameraBasis(right, up, look);
    }

    // ---- running the effect --------------------------------------------------------------------

    private static List<PkRenderBatch> Batches(PkEffectDef def, HashSet<PkRendererDef> renderers)
    {
        var batches = new List<PkRenderBatch>();
        foreach (var layer in def.Layers)
            foreach (var r in layer.Renderers)
                if (renderers.Contains(r)) batches.Add(new PkRenderBatch { Renderer = r, LayerName = layer.Name });
        return batches;
    }

    /// <summary>The last moment a bakeable sprite is on screen, or -1 when none ever is.</summary>
    private static float LastVisible(PkEffectDef def, PkEnvironment env, HashSet<PkRendererDef> renderers, PkImpostorOptions o)
    {
        var fx = new PkEffectInstance(def, env, o.Seed);
        var batches = Batches(def, renderers);
        float last = -1;
        for (float t = 0; t < 3 * o.MaxDuration && fx.IsAlive; t += o.Dt)
        {
            fx.Update(o.Dt);
            foreach (var b in batches) b.Sprites.Clear();
            PkCornPlayer.CollectSprites(fx, batches, PopcornApproximation.MetresToWc3);
            if (batches.Any(b => b.Sprites.Count > 0)) last = t + o.Dt;
        }
        return last;
    }

    private static List<List<(PkRendererDef Renderer, PkSprite Sprite)>> Sample(PkEffectDef def, PkEnvironment env, HashSet<PkRendererDef> renderers,
                                                                              float start, float duration, int cells, float dt, PkImpostorOptions o)
    {
        var fx = new PkEffectInstance(def, env, o.Seed);
        var batches = Batches(def, renderers);
        var frames = new List<List<(PkRendererDef, PkSprite)>>(cells);
        float t = 0;
        for (int k = 0; k < cells; k++)
        {
            float target = start + (k + 0.5f) * duration / cells;
            while (t < target && fx.IsAlive) { fx.Update(dt); t += dt; }
            foreach (var b in batches) b.Sprites.Clear();
            if (fx.IsAlive) PkCornPlayer.CollectSprites(fx, batches, PopcornApproximation.MetresToWc3);
            var frame = new List<(PkRendererDef, PkSprite)>();
            foreach (var b in batches)
                foreach (var s in b.Sprites)
                {
                    if (frame.Count >= o.MaxSpritesPerFrame) break;
                    frame.Add((b.Renderer, s));
                }
            frames.Add(frame);
        }
        return frames;
    }

    /// <summary>
    /// Whether every frame shows the same cards in the same places: the same count, positions
    /// within half a percent of the footprint, sizes within two percent, colours within a fiftieth,
    /// the same sheet cell. Sprites are compared in collection order, which the VM keeps stable
    /// for particles that live on.
    /// </summary>
    private static bool IsStatic(List<List<(PkRendererDef Renderer, PkSprite Sprite)>> frames, float side)
    {
        var first = frames[0];
        if (first.Count == 0) return false;
        float posTol = 0.005f * side;
        for (int k = 1; k < frames.Count; k++)
        {
            var f = frames[k];
            if (f.Count != first.Count) return false;
            for (int i = 0; i < f.Count; i++)
            {
                var a = first[i].Sprite; var b = f[i].Sprite;
                if (!ReferenceEquals(first[i].Renderer, f[i].Renderer) || a.Frame != b.Frame) return false;
                if (Vector3.Distance(a.Position, b.Position) > posTol) return false;
                if (Vector2.Distance(a.HalfSize, b.HalfSize) > 0.02f * MathF.Max(a.HalfSize.X, 1e-3f)) return false;
                if (Vector4.Distance(a.Color, b.Color) > 0.02f * MathF.Max(1f, a.Color.Length())) return false;
                if (MathF.Abs(a.Rotation - b.Rotation) > 0.01f) return false;
            }
        }
        return true;
    }

    // ---- framing ------------------------------------------------------------------------------

    /// <summary>
    /// The square, in the camera's screen plane, that holds the effect over every frame: the 2nd to
    /// 98th percentile of every card corner, so one spark flung far out does not shrink the rest to
    /// a dot. Depth is the mean along the look, which places the card among its sprites.
    /// </summary>
    private static (float Cx, float Cy, float Side, float Depth) Footprint(List<List<(PkRendererDef Renderer, PkSprite Sprite)>> frames,
                                                                          CameraBasis c, float margin)
    {
        var xs = new List<float>(); var ys = new List<float>();
        double depth = 0; long count = 0;
        foreach (var frame in frames)
            foreach (var (r, s) in frame)
            {
                PkSpriteGeometry.Quad(in s, r.Billboard, c.Right, c.Up, c.Look, out var rv, out var uv);
                foreach (var corner in new[] { s.Position - rv - uv, s.Position + rv - uv, s.Position + rv + uv, s.Position - rv + uv })
                {
                    xs.Add(Vector3.Dot(corner, c.Right));
                    ys.Add(Vector3.Dot(corner, c.Up));
                }
                depth += Vector3.Dot(s.Position, c.Look);
                count++;
            }
        if (count == 0) return (0, 0, 0, 0);
        xs.Sort(); ys.Sort();
        float x0 = Pct(xs, 0.02f), x1 = Pct(xs, 0.98f), y0 = Pct(ys, 0.02f), y1 = Pct(ys, 0.98f);
        float side = MathF.Max(x1 - x0, y1 - y0) * (1 + 2 * margin);
        return ((x0 + x1) / 2, (y0 + y1) / 2, side, (float)(depth / count));

        static float Pct(List<float> v, float q) => v[Math.Clamp((int)(q * (v.Count - 1)), 0, v.Count - 1)];
    }

    // ---- drawing ------------------------------------------------------------------------------

    /// <summary>
    /// Draws one frame's cards into the two linear buffers, far to near, each blended the way its
    /// renderer blends: light added, or laid over. The same frame over black and over white is what
    /// the matte is read from.
    /// </summary>
    private static void Rasterise(List<(PkRendererDef Renderer, PkSprite Sprite)> frame, CameraBasis c, float cx, float cy, float side, int n,
                                  Dictionary<PkRendererDef, LinearSprite> sprites, float[] black, float[] white, ref float peak)
        => RasteriseInto(frame, c, cx - side / 2, cy + side / 2, n / side, n, n, sprites, black, white, ref peak);

    /// <summary>
    /// The same into a rectangle of <paramref name="nx"/> by <paramref name="ny"/> texels whose left
    /// and top edges sit at <paramref name="x0"/> and <paramref name="yTop"/> in screen units, at
    /// <paramref name="scale"/> texels per unit.
    /// </summary>
    private static void RasteriseInto(List<(PkRendererDef Renderer, PkSprite Sprite)> frame, CameraBasis c, float x0, float yTop, float scale, int nx, int ny,
                                      Dictionary<PkRendererDef, LinearSprite> sprites, float[] black, float[] white, ref float peak)
    {
        foreach (var (r, s) in frame.OrderByDescending(f => Vector3.Dot(f.Sprite.Position, c.Look)))
        {
            if (!sprites.TryGetValue(r, out var tex)) continue;
            PkSpriteGeometry.Quad(in s, r.Billboard, c.Right, c.Up, c.Look, out var rv, out var uv);
            var c2 = new Vector2(Vector3.Dot(s.Position, c.Right), Vector3.Dot(s.Position, c.Up));
            var r2 = new Vector2(Vector3.Dot(rv, c.Right), Vector3.Dot(rv, c.Up));
            var u2 = new Vector2(Vector3.Dot(uv, c.Right), Vector3.Dot(uv, c.Up));
            float det = r2.X * u2.Y - r2.Y * u2.X;
            if (MathF.Abs(det) < 1e-9f) continue;

            float ex = MathF.Abs(r2.X) + MathF.Abs(u2.X), ey = MathF.Abs(r2.Y) + MathF.Abs(u2.Y);
            int minX = Math.Max(0, (int)MathF.Floor((c2.X - ex - x0) * scale));
            int maxX = Math.Min(nx - 1, (int)MathF.Ceiling((c2.X + ex - x0) * scale));
            int minY = Math.Max(0, (int)MathF.Floor((yTop - (c2.Y + ey)) * scale));
            int maxY = Math.Min(ny - 1, (int)MathF.Ceiling((yTop - (c2.Y - ey)) * scale));
            if (minX > maxX || minY > maxY) continue;

            var (u0, v0, u1, v1) = PkSpriteGeometry.CellUv(s.Frame, r.AtlasColumns, r.AtlasRows);
            bool additive = r.Blend is PopcornBlend.Additive or PopcornBlend.AdditiveNoAlpha;
            bool noAlpha = r.Blend == PopcornBlend.AdditiveNoAlpha;
            var col = s.Color;
            for (int j = minY; j <= maxY; j++)
            {
                float y = yTop - (j + 0.5f) / scale;
                for (int i = minX; i <= maxX; i++)
                {
                    float x = x0 + (i + 0.5f) / scale;
                    float dx = x - c2.X, dy = y - c2.Y;
                    float a = (dx * u2.Y - dy * u2.X) / det, b = (r2.X * dy - r2.Y * dx) / det;
                    if (a < -1 || a > 1 || b < -1 || b > 1) continue;
                    var t = tex.Sample(u0 + (u1 - u0) * (a + 1) * 0.5f, v1 + (v0 - v1) * (b + 1) * 0.5f);
                    float ta = noAlpha ? 1f : t.W * col.W;
                    float rr = t.X * col.X, gg = t.Y * col.Y, bb = t.Z * col.Z;
                    int idx = (j * nx + i) * 3;
                    if (additive)
                    {
                        black[idx] += rr * ta; black[idx + 1] += gg * ta; black[idx + 2] += bb * ta;
                        white[idx] += rr * ta; white[idx + 1] += gg * ta; white[idx + 2] += bb * ta;
                    }
                    else
                    {
                        ta = Math.Clamp(ta, 0f, 1f);
                        float keep = 1 - ta;
                        black[idx] = black[idx] * keep + rr * ta; black[idx + 1] = black[idx + 1] * keep + gg * ta; black[idx + 2] = black[idx + 2] * keep + bb * ta;
                        white[idx] = white[idx] * keep + rr * ta; white[idx + 1] = white[idx + 1] * keep + gg * ta; white[idx + 2] = white[idx + 2] * keep + bb * ta;
                    }
                    peak = MathF.Max(peak, MathF.Max(black[idx], MathF.Max(black[idx + 1], black[idx + 2])));
                }
            }
        }
    }

    /// <summary>
    /// Box-filters the supersampled frame down, tone-maps it, and reads the card's straight-alpha
    /// colour off the black and white renders in display space: coverage is what the white lost,
    /// and light with no coverage still needs alpha to be drawn at all, so alpha is the larger of
    /// coverage and brightness — exact over black and over white, and the best a single blended
    /// card can do in between.
    /// </summary>
    private static void Resolve(float[] black, float[] white, int n, int cs, int cols, PkImpostorOptions o, bool additive, int cell, float tail,
                                byte[] atlas, int atlasW, ref long drawn, ref long saturated)
    {
        int ss = o.Supersample;
        int col = cell % cols, row = cell / cols;
        float feather = o.EdgeFeather * cs;
        float inv = 1f / (ss * ss);
        Span<float> b = stackalloc float[3];
        Span<float> w = stackalloc float[3];
        Span<float> db = stackalloc float[3];
        for (int y = 0; y < cs; y++)
            for (int x = 0; x < cs; x++)
            {
                b.Clear(); w.Clear();
                for (int sy = 0; sy < ss; sy++)
                    for (int sx = 0; sx < ss; sx++)
                    {
                        int idx = ((y * ss + sy) * n + x * ss + sx) * 3;
                        b[0] += black[idx]; b[1] += black[idx + 1]; b[2] += black[idx + 2];
                        w[0] += white[idx]; w[1] += white[idx + 1]; w[2] += white[idx + 2];
                    }
                var (alpha, bright) = ResolveTexel(b, w, inv, o, additive, db);
                float d = MathF.Min(MathF.Min(x + 0.5f, y + 0.5f), MathF.Min(cs - x - 0.5f, cs - y - 0.5f));
                float edge = feather <= 0 ? 1f : Smooth(Math.Clamp(d / feather, 0f, 1f));
                float outAlpha = alpha * edge * tail;

                int at = ((row * cs + y) * atlasW + col * cs + x) * 4;
                if (alpha > 1f / 255)
                {
                    atlas[at] = ToByte(db[2] / alpha); atlas[at + 1] = ToByte(db[1] / alpha); atlas[at + 2] = ToByte(db[0] / alpha);
                }
                atlas[at + 3] = ToByte(outAlpha);
                if (outAlpha > 1f / 255)
                {
                    drawn++;
                    if (outAlpha >= 0.98f && bright >= 0.98f) saturated++;
                }
            }
    }

    /// <summary>
    /// One box-filtered texel (<paramref name="b"/> and <paramref name="w"/> are sums over
    /// <c>1/inv</c> samples) tone-mapped into <paramref name="db"/> and given its straight alpha.
    /// Coverage is read in linear light, where it is exact: light added contributes the same to
    /// both renders and leaves their difference alone, while a card laid over scales the
    /// difference by what it lets through. Read after tone mapping instead, untouched white came
    /// back as 0.9 and every empty texel wore a 9% grey haze.
    /// </summary>
    private static (float Alpha, float Bright) ResolveTexel(ReadOnlySpan<float> b, ReadOnlySpan<float> w, float inv, PkImpostorOptions o, bool additive, Span<float> db)
    {
        float coverage = 0, bright = 0;
        for (int ch = 0; ch < 3; ch++)
        {
            coverage += Math.Clamp(1 - (w[ch] - b[ch]) * inv, 0f, 1f);
            db[ch] = Encode(Tone(b[ch] * inv * o.Exposure, o.ToneMap));
            bright = MathF.Max(bright, db[ch]);
        }
        coverage /= 3;
        return (additive ? bright : MathF.Max(coverage, bright), bright);
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);
    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);

    private static float Tone(float v, PkToneMap map)
    {
        v = MathF.Max(v, 0f);
        return map switch
        {
            PkToneMap.Clip => MathF.Min(v, 1f),
            PkToneMap.Reinhard => v / (1 + v),
            _ => Math.Clamp(v * (2.51f * v + 0.03f) / (v * (2.43f * v + 0.59f) + 0.14f), 0f, 1f),
        };
    }

    /// <summary>Linear to sRGB.</summary>
    private static float Encode(float c) => c <= 0.0031308f ? 12.92f * c : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

    private static List<RgbaImage> Cells(RgbaImage atlas, int cols, int rows, int cs)
    {
        var frames = new List<RgbaImage>();
        for (int k = 0; k < cols * rows; k++)
        {
            int col = k % cols, row = k / cols;
            var px = new byte[cs * cs * 4];
            for (int y = 0; y < cs; y++)
                Buffer.BlockCopy(atlas.Pixels, ((row * cs + y) * atlas.Width + col * cs) * 4, px, y * cs * 4, cs * 4);
            frames.Add(new RgbaImage { Width = cs, Height = cs, Pixels = px });
        }
        return frames;
    }

    // ---- sprite textures ------------------------------------------------------------------------

    /// <summary>A sprite sheet decoded to linear light, sampled bilinearly.</summary>
    private sealed class LinearSprite
    {
        private static readonly float[] SrgbToLinear = BuildLut();
        private readonly int _w, _h;
        private readonly float[] _px;    // RGBA, linear, row-major

        public LinearSprite(RgbaImage image)
        {
            _w = image.Width; _h = image.Height;
            _px = new float[_w * _h * 4];
            var src = image.Pixels;
            for (int i = 0, o = 0; i < src.Length; i += 4, o += 4)
            {
                _px[o] = SrgbToLinear[src[i + 2]];
                _px[o + 1] = SrgbToLinear[src[i + 1]];
                _px[o + 2] = SrgbToLinear[src[i]];
                _px[o + 3] = src[i + 3] / 255f;
            }
        }

        public Vector4 Sample(float u, float v)
        {
            float fx = Math.Clamp(u * _w - 0.5f, 0, _w - 1), fy = Math.Clamp(v * _h - 0.5f, 0, _h - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, _w - 1), y1 = Math.Min(y0 + 1, _h - 1);
            float tx = fx - x0, ty = fy - y0;
            return Lerp(Lerp(At(x0, y0), At(x1, y0), tx), Lerp(At(x0, y1), At(x1, y1), tx), ty);
        }

        private Vector4 At(int x, int y)
        {
            int i = (y * _w + x) * 4;
            return new Vector4(_px[i], _px[i + 1], _px[i + 2], _px[i + 3]);
        }

        private static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

        private static float[] BuildLut()
        {
            var lut = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float c = i / 255f;
                lut[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            }
            return lut;
        }
    }
}
