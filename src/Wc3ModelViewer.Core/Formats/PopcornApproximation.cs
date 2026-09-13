using System.Numerics;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// Turns a model's CORN emitters into stand-in <see cref="MdxParticleEmitter2"/> entries so the
/// viewer and the StarCraft II exporter can treat a PopcornFX effect like any other emitter.
/// </summary>
/// <remarks>
/// <para>
/// This is an approximation by construction. A bake's renderer layers, textures, blend modes,
/// billboard modes and colour/size curves are plain data and come across faithfully; everything a
/// PopcornFX script computes per particle is compiled bytecode with no counterpart in a
/// fixed-field emitter. The numbers that matter most — lifetime, spawn count, size, beam length —
/// are recovered from the constants the compiler left in the layer's pool and bytecode, using rules
/// checked against Holy Light, Thunder Clap, the hero glow, the priest's attack and the paladin's
/// spell (2026-09-13). They are rules, not a decoder: expect the shape of the effect, not its exact
/// look.
/// </para>
/// <para>
/// <b>Units.</b> PopcornFX works in metres and Reforged's HD art is authored at 1 unit = 2 cm: the
/// Holy Light bake's beam axis of 5.4–13.2 lands on the SD model's 270–650 unit tall beam, and
/// Blizzard's own War3 mod converts to StarCraft II at 0.02, i.e. one metre per SC2 unit. So every
/// bake length is multiplied by <see cref="MetresToWc3"/> to sit in the model's own space.
/// </para>
/// </remarks>
public static class PopcornApproximation
{
    public const float MetresToWc3 = 50f;

    /// <summary>Shortest emission window a measured burst is given, seconds.</summary>
    public const float MinBurstSeconds = 0.25f;

    /// <summary>
    /// Whether measured bursts emit only in their window (true, the default) or at a constant rate
    /// whenever the effect is on (false). The burst-windowed export of Holy Light drew nothing in the
    /// StarCraft II editor (2026-09-13 A/B/C check) and was blamed on the rate track itself, but the
    /// cause was the exporter's synthesised Stand sequence, which carried no float tracks at all and so
    /// played every burst emitter at its rest rate of zero. Kept as a switch for further checks.
    /// </summary>
    public static bool EmitBursts { get; set; } = true;

    /// <summary>
    /// Whether a measured burst is written as StarCraft II's <c>emit_count</c> (true, the default;
    /// Holy Light's rune and flares drew correctly this way in the SC2 editor, 2026-09-13) — the
    /// births fired at once on one key, the way Blizzard's own art bursts — or as a window on
    /// <c>emit_rate</c> (false). In the HotS effects corpus 14,464 particle systems burst through
    /// <c>emit_count</c> against 4,014 through a rate window; the count is edge-triggered, not per
    /// frame: the median key holds it for one 33 ms frame, and a system holding 15 for two seconds
    /// caps its live particles at 31.
    /// </summary>
    public static bool CountBursts { get; set; } = true;

    /// <summary>
    /// Longest spread of births, seconds, a count burst may stand in for. Births spread wider than
    /// this keep the rate window, since one key fires them all at the same instant.
    /// </summary>
    public const float CountBurstMaxSeconds = 0.1f;

    /// <summary>Whether measured emitters keep their spawn offset and direction (true) or all emit at the node. Also a check switch.</summary>
    public static bool PlaceEmitters { get; set; } = true;

    /// <summary>HD-class archive prefixes a bake might sit under, tried in order after the model's own.</summary>
    private static readonly string[] HdPrefixes = ["war3.w3mod:_hd.w3mod:", "war3.w3mod:_de.w3mod:"];

    /// <summary>
    /// Resolves and parses every CORN emitter's bake and appends the resulting stand-in emitters to
    /// <paramref name="model"/>. Safe to call once per model; a second call does nothing.
    /// </summary>
    /// <param name="read">Reads an archive file by CASC name, or null when absent — <c>Wc3Storage.TryReadFile</c>.</param>
    /// <param name="modelCascName">The model's own archive name, whose prefix anchors the bake lookup; "" for a loose file.</param>
    /// <returns>Human-readable notes, one per emitter, for logs and the status bar.</returns>
    public static List<string> Attach(MdxModel model, Func<string, byte[]?> read, string modelCascName)
    {
        var log = new List<string>();
        if (model.PopcornEmitters.Count == 0) return log;
        if (model.ParticleEmitters.Any(e => e.IsPopcorn)) return log;      // already attached

        string ownPrefix = modelCascName.LastIndexOf(':') is int c && c >= 0 ? modelCascName[..(c + 1)] : "";

        foreach (var corn in model.PopcornEmitters)
        {
            if (corn.Effect is null)
            {
                foreach (string name in BakeCandidates(corn.EffectPath, ownPrefix))
                {
                    var bytes = read(name);
                    if (bytes is null || !PopcornBake.LooksLikeBake(bytes)) continue;
                    try
                    {
                        corn.Effect = PopcornBake.Parse(bytes);
                        corn.BakeName = name;
                    }
                    catch (InvalidDataException e)
                    {
                        log.Add($"'{corn.Name}': bake {name} did not parse — {e.Message}");
                        continue;
                    }
                    // The executable definition drives the viewer's simulation; the stand-ins below
                    // remain for export. A bake the runtime cannot load still gets its stand-ins.
                    try { corn.Runtime = Popcorn.PkEffectDef.Load(bytes); }
                    catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
                    {
                        log.Add($"'{corn.Name}': bake {name} scripts did not load — {e.Message}");
                    }
                    break;
                }
            }
            if (corn.Effect is null)
            {
                log.Add($"'{corn.Name}': bake for {corn.EffectPath} not found in the archive — effect dropped");
                continue;
            }

            int before = model.ParticleEmitters.Count;
            if (corn.Runtime is not null)
            {
                // The scripts run: measure what each renderer's particles really do.
                List<Popcorn.PkRendererStats> stats;
                try { stats = Popcorn.PkRendererStats.Measure(corn.Runtime, colourMultiplier: corn.ColorMultiplier); }
                catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or InvalidOperationException)
                {
                    log.Add($"'{corn.Name}': measuring {Path.GetFileName(corn.EffectPath)} failed — {e.Message}; using recovered constants");
                    stats = [];
                }
                foreach (var s in stats)
                {
                    int symmetry = s.OrbitAngularVelocity == 0 ? 1
                        : stats.Count(o => o.LayerName == s.LayerName && MathF.Abs(o.OrbitAngularVelocity - s.OrbitAngularVelocity) < 0.05f * MathF.Abs(s.OrbitAngularVelocity));
                    var e = SynthesizeMeasured(model, corn, s, symmetry);
                    if (e is not null) model.ParticleEmitters.Add(e);
                }
                if (stats.Count > 0)
                {
                    log.Add($"'{corn.Name}': {Path.GetFileName(corn.EffectPath)} -> {model.ParticleEmitters.Count - before} renderer(s) measured from a simulated run");
                    continue;
                }
            }
            foreach (var layer in corn.Effect.Layers)
            {
                var e = Synthesize(model, corn, layer);
                if (e is not null) model.ParticleEmitters.Add(e);
            }
            int added = model.ParticleEmitters.Count - before;
            log.Add($"'{corn.Name}': {Path.GetFileName(corn.EffectPath)} -> {added} of {corn.Effect.Layers.Count} layer(s) approximated"
                    + (corn.Effect.Layers.Count == 0 ? " (no renderer layers in the bake)" : ""));
        }
        return log;
    }

    /// <summary>Archive names to try for a <c>.pkfx</c> reference: the <c>.pkb</c> bake under the model's tree, then the other HD-class trees.</summary>
    public static IEnumerable<string> BakeCandidates(string effectPath, string ownPrefix)
    {
        string rel = effectPath.Replace('/', '\\');
        rel = Path.ChangeExtension(rel, ".pkb");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ownPrefix.Length > 0 && seen.Add(ownPrefix)) yield return ownPrefix + rel;
        foreach (string p in HdPrefixes) if (seen.Add(p)) yield return p + rel;
    }

    // ---------------------------------------------------------------- one layer -> one emitter

    private static MdxParticleEmitter2? Synthesize(MdxModel model, MdxPopcornEmitter corn, PopcornLayer layer)
    {
        if (layer.TexturePath.Length == 0) return null;             // nothing to draw with

        int textureId = TextureIndex(model, layer.TexturePath);
        var (life, lifeSpread, endless) = Lifetime(layer);
        var (spawn, spawnerDuration) = SpawnCount(layer, life, lifeSpread);

        // A card's size, in metres: the layer's size curve when it has one, else pool constants.
        var (s0, s1, s2, mid) = SizeRamp(layer, life, lifeSpread);

        var (c0, c1, c2, a0, a1, a2, colourMid) = ColourRamp(layer, corn.ColorMultiplier);
        if (layer.ColorCurve is not null) mid = colourMid;         // one middle time per emitter; colour's peak is the salient one

        var orientation = layer.Billboard switch
        {
            PopcornBillboardMode.AxisAligned or PopcornBillboardMode.AxisAlignedSpheroid or PopcornBillboardMode.AxisAlignedCapsule
                => MdxParticleOrientation.Ray,
            PopcornBillboardMode.PlaneAligned => MdxParticleOrientation.Ground,
            _ => MdxParticleOrientation.CameraFacing,
        };

        // Motion. A PopcornFX beam has its whole axis from birth and stays put, so it becomes a
        // stationary fixed-length card (StarCraft II's fixed tail). A flare whose pool holds a
        // symmetric ± offset scatters at that speed; everything else stays put, which is what most
        // flares, discs and runes do.
        float speed = 0, latitude = 0, beam = 0;
        if (orientation == MdxParticleOrientation.Ray)
        {
            beam = BeamLength(layer);
            if (beam <= 0) beam = MathF.Max(s1, 0.5f) * 4;   // a beam four times as long as it is wide
        }
        else if (orientation == MdxParticleOrientation.CameraFacing && ScatterSpeed(layer) is > 0 and var scatter)
        {
            speed = scatter;
            latitude = 180f;
        }

        // Emission rate. The spawner fires `spawn` particles over its own duration, each living
        // `life`; a steady rate that puts the same number of cards on screen at once is the closest
        // thing a StarCraft II system offers to that burst. A layer that never dies (the hero glow)
        // is one card renewed exactly as it expires: spawn credit accrues deterministically, so a
        // rate of one per lifetime hands over without a gap.
        float rate = endless ? 1f / life : spawn / MathF.Max(spawnerDuration + life, 0.2f);

        var blend = layer.Blend is PopcornBlend.AlphaBlend or PopcornBlend.PremultipliedAlpha
            ? MdxParticleBlend.Blend : MdxParticleBlend.Add;

        float m = MetresToWc3;
        var vis = corn.VisibilityTrack ?? GateTrack(model, corn.PopcornFlags);
        var rateTrack = ScaledTrack(corn.EmissionRateTrack, rate);

        return new MdxParticleEmitter2
        {
            Name = $"{corn.Name}/{layer.Name}",
            NodeIndex = corn.NodeIndex,
            PopcornSource = $"{Path.GetFileNameWithoutExtension(corn.EffectPath)}:{layer.Name}",
            Orientation = orientation,
            BeamLength = beam * m,
            SpawnImmediately = endless,
            Speed = speed * m, Variation = 0.2f, Latitude = latitude,
            Gravity = 0, Life = life, EmissionRate = rate,
            Length = 0, Width = 0,
            Blend = blend, Rows = 1, Columns = 1,
            ParticleType = MdxParticleType.Head, TailLength = 0, MiddleTime = Math.Clamp(mid, 0.05f, 0.95f),
            StartColor = c0, MiddleColor = c1, EndColor = c2,
            StartAlpha = a0, MiddleAlpha = a1, EndAlpha = a2,
            StartScale = s0 * m, MiddleScale = s1 * m, EndScale = s2 * m,
            TextureId = textureId, PriorityPlane = 0, ReplaceableId = 0,
            Squirt = false, HeadCellStart = 0, HeadCellEnd = 0, HeadCellRepeat = 1,
            Unshaded = true, Unfogged = false, ModelSpace = false, LineEmitter = false,
            EmissionRateTrack = rateTrack,
            LifeTrack = ScaledTrack(corn.LifespanTrack, life),
            SpeedTrack = ScaledTrack(corn.SpeedTrack, speed * m),
            VisibilityTrack = vis,
        };
    }

    // ---------------------------------------------------------------- one measured renderer -> one emitter

    /// <summary>
    /// A stand-in emitter from what a renderer's particles did in a simulated run: born where they
    /// were born, moving as they moved, sized and coloured as they were over their lives, and emitting
    /// only in the window the effect really spawns them.
    /// </summary>
    private static MdxParticleEmitter2? SynthesizeMeasured(MdxModel model, MdxPopcornEmitter corn, Popcorn.PkRendererStats s, int orbitSymmetry = 1)
    {
        var r = s.Renderer;
        if (r.Texture.Length == 0) return null;
        const float m = MetresToWc3;

        var orientation = r.Billboard switch
        {
            PopcornBillboardMode.AxisAligned or PopcornBillboardMode.AxisAlignedSpheroid or PopcornBillboardMode.AxisAlignedCapsule
                => MdxParticleOrientation.Ray,
            PopcornBillboardMode.PlaneAligned => MdxParticleOrientation.Ground,
            _ => MdxParticleOrientation.CameraFacing,
        };
        // A plane-aligned card that does not lie flat keeps its own facing: the emitter bone's Z
        // becomes the measured normal. For an orbiting card the normal is rebuilt at the spawn point
        // from its radial-frame measurement, so it turns with the ring.
        Vector3? face = null;
        if (orientation == MdxParticleOrientation.Ground && !s.LiesFlat)
        {
            var radial = new Vector3(s.SpawnCentre.X, s.SpawnCentre.Y, 0);
            if (s.OrbitAngularVelocity != 0 && radial.LengthSquared() > 1e-6f)
            {
                radial = Vector3.Normalize(radial);
                var tangent = Vector3.Cross(Vector3.UnitZ, radial);
                var n = s.CardNormalRadial;
                face = Vector3.Normalize(radial * n.X + tangent * n.Y + Vector3.UnitZ * n.Z);
            }
            else face = s.CardNormal;
        }

        var offset = s.SpawnCentre;
        var direction = s.Direction;
        float beam = 0, speed = s.Speed, spread = s.SpreadDegrees;
        bool streaks = orientation == MdxParticleOrientation.Ray && s.AxisCoherence < 0.9f;
        if (streaks)
        {
            // Cards stretched along axes that point every way — sparks flung out, each drawn along
            // its own flight. StarCraft II stretches a tail along each particle's velocity, so the
            // measured spread and speed carry the scatter and the tail keeps the streak's length.
            bool rounded = r.Billboard != PopcornBillboardMode.AxisAligned;
            beam = s.AxisLength + (rounded ? 2 * s.Size.Middle : 0);
            speed = MathF.Max(speed, 0.01f);
        }
        else if (orientation == MdxParticleOrientation.Ray)
        {
            // A PopcornFX beam card spans Position ± Axis/2 (the spheroid and capsule modes add the
            // radius at both ends). StarCraft II's fixed tail runs from the particle along its travel
            // direction, so the emitter sits at the beam's near end and emits along the axis.
            bool rounded = r.Billboard != PopcornBillboardMode.AxisAligned;
            beam = s.AxisLength + (rounded ? 2 * s.Size.Middle : 0);
            direction = s.AxisDirection;
            // A PopcornFX axis-aligned card is centred on its particle, and so is StarCraft II's
            // fixed tail: checked both other ways in the SC2 editor on Holy Light (2026-09-13) —
            // emitters at the near end hung the shafts entirely below the impact glow, emitters at
            // the far end stood them entirely above the rune. So the emitter sits at the beam's centre.
            offset = s.SpawnCentre;
            // The tail needs a travel direction to stretch along; a stationary beam gets a crawl
            // too slow to see (a centimetre a second) rather than none.
            speed = MathF.Max(speed, 0.01f);
            spread = 0;
        }

        // PopcornFX sizes are radii, which is what a Warcraft III scale is too; the writer doubles it
        // into StarCraft II's full width.
        // The CORN multiplier went into the measurement as the scripts' __a_Game.ColorMultiplier,
        // so it is not applied again here: the hero glow's is (1,1,1,0), which its script ignores.
        var (c0, c1, c2, cMid) = s.Colour;
        var (sc0, a0) = SplitColour(c0, Vector4.One);
        var (sc1, a1) = SplitColour(c1, Vector4.One);
        var (sc2, a2) = SplitColour(c2, Vector4.One);

        // Emission: a steady emitter at its measured rate; a burst at the rate that fits its births
        // into the window they happened in, switched off either side of it.
        // The window is never shorter than MinBurstSeconds: a rate is integrated frame by frame, and
        // Holy Light's rune — one particle in one 34 ms frame — accrues barely one particle's worth.
        float window = MathF.Max(MathF.Max(s.LastBirth - s.FirstBirth, 0) + 1f / 30f, MinBurstSeconds);
        // Half a particle of headroom, so a one-particle burst is not lost to rounding at the window's end.
        float rate = s.Immortal ? 1f / s.Life : s.Continuous ? s.Births / MathF.Max(s.LastBirth - s.FirstBirth, 0.2f) : (s.Births + 0.5f) / window;
        var vis = corn.VisibilityTrack ?? GateTrack(model, corn.PopcornFlags);
        bool burst = corn.EmissionRateTrack is null && !s.Immortal && !s.Continuous && EmitBursts;
        bool asCount = burst && CountBursts && s.LastBirth - s.FirstBirth <= CountBurstMaxSeconds;
        var rateTrack = corn.EmissionRateTrack is not null
            ? ScaledTrack(corn.EmissionRateTrack, rate)
            : burst && !asCount ? BurstTrack(model, vis, s.FirstBirth, s.FirstBirth + window, rate) : null;
        // A count burst fires every birth on one key and leaves the rate at zero; the key is held
        // one frame, as Blizzard's are, and the next key resets it so a replay fires again.
        MdxTrack<float>? countTrack = null;
        if (asCount)
        {
            countTrack = BurstTrack(model, vis, s.FirstBirth, s.FirstBirth + 1f / 30f, s.Births);
            rate = 0;
        }
        // Without burst windows the burst is spread over the particles' life, as the constant-rate
        // stand-ins always were.
        if (!EmitBursts && !s.Immortal && !s.Continuous) rate = s.Births / MathF.Max(window + s.Life, 0.2f);

        var blend = r.Blend is PopcornBlend.AlphaBlend or PopcornBlend.PremultipliedAlpha ? MdxParticleBlend.Blend : MdxParticleBlend.Add;
        return new MdxParticleEmitter2
        {
            Name = $"{corn.Name}/{s.LayerName}",
            NodeIndex = corn.NodeIndex,
            PopcornSource = $"{Path.GetFileNameWithoutExtension(corn.EffectPath)}:{s.LayerName} (measured)",
            Orientation = orientation,
            BeamLength = beam * m,
            SpawnImmediately = s.Immortal,
            SpawnOffset = PlaceEmitters ? offset * m : Vector3.Zero,
            SpawnHalfExtents = orientation == MdxParticleOrientation.Ray && !streaks ? Vector3.Zero : s.SpawnHalfExtents * m,
            EmitDirection = PlaceEmitters ? direction : Vector3.UnitZ,
            Speed = speed * m, Variation = s.SpeedVariation, Latitude = spread,
            Gravity = 0, Life = s.Life, EmissionRate = rate,
            Length = 0, Width = 0,
            Blend = blend, Rows = Math.Max(1, r.AtlasRows), Columns = Math.Max(1, r.AtlasColumns),
            ParticleType = MdxParticleType.Head, TailLength = 0, MiddleTime = Math.Clamp(cMid, 0.05f, 0.95f),
            StartColor = sc0, MiddleColor = sc1, EndColor = sc2,
            StartAlpha = a0, MiddleAlpha = a1, EndAlpha = a2,
            StartScale = s.Size.Start * m, MiddleScale = s.Size.Middle * m, EndScale = s.Size.End * m,
            TextureId = TextureIndex(model, r.Texture), PriorityPlane = 0, ReplaceableId = 0,
            Squirt = false, HeadCellStart = 0, HeadCellEnd = 0, HeadCellRepeat = 1,
            // An orbiting particle is hosted to its (spinning) emitter bone rather than left in
            // world space, so the bone carries it round.
            Unshaded = true, Unfogged = false, ModelSpace = s.OrbitAngularVelocity != 0, LineEmitter = false,
            OrbitAngularVelocity = s.OrbitAngularVelocity, OrbitSymmetry = Math.Max(1, orbitSymmetry),
            FaceDirection = face,
            EmissionRateTrack = rateTrack,
            EmitCountTrack = countTrack,
            LifeTrack = ScaledTrack(corn.LifespanTrack, s.Life),
            SpeedTrack = ScaledTrack(corn.SpeedTrack, speed * m),
            VisibilityTrack = vis,
        };
    }

    /// <summary>
    /// An emission-rate track that is <paramref name="rate"/> only from <paramref name="from"/> to
    /// <paramref name="to"/> seconds after the effect switches on in each sequence, and 0 elsewhere —
    /// how a fixed-rate system reproduces a burst.
    /// </summary>
    private static MdxTrack<float>? BurstTrack(MdxModel model, MdxTrack<float>? gate, float from, float to, float rate)
    {
        if (model.Sequences.Count == 0) return null;
        var animator = new MdxAnimator(model);
        var times = new List<int>(); var values = new List<float>();
        foreach (var seq in model.Sequences.OrderBy(q => q.IntervalStart))
        {
            // When the effect switches on inside this sequence (the gate's first "on" sample).
            int on = -1;
            for (int t = seq.IntervalStart; t <= seq.IntervalEnd; t += 33)
                if (animator.SampleFloat(gate, seq, t, 1f) >= 0.5f) { on = t; break; }
            void Key(int t, float v)
            {
                t = Math.Clamp(t, seq.IntervalStart, seq.IntervalEnd);
                if (times.Count > 0 && times[^1] >= t) { values[^1] = v; return; }
                times.Add(t); values.Add(v);
            }
            Key(seq.IntervalStart, 0);
            if (on < 0) continue;
            int start = on + (int)(from * 1000), stop = on + (int)MathF.Ceiling(to * 1000);
            if (start > seq.IntervalEnd) continue;
            Key(start, rate);
            if (stop < seq.IntervalEnd) Key(stop, 0);
        }
        return new MdxTrack<float> { Tag = "KP2E", Interpolation = MdxInterpolation.None, Times = [.. times], Values = [.. values] };
    }

    /// <summary>
    /// An HDR PopcornFX colour tinted by the emitter's multiplier, as a clamped colour and an alpha.
    /// A channel above one is scaled back and the overshoot pushed into alpha, which for an additive
    /// card is the same brightness.
    /// </summary>
    private static (Vector3 Rgb, byte Alpha) SplitColour(Vector4 v, Vector4 tint)
    {
        var rgb = new Vector3(v.X * tint.X, v.Y * tint.Y, v.Z * tint.Z);
        float alpha = v.W * tint.W;
        float over = MathF.Max(MathF.Max(rgb.X, rgb.Y), MathF.Max(rgb.Z, 1f));
        if (over > 1f) { rgb /= over; alpha *= MathF.Min(over, 2f); }
        return (Vector3.Clamp(rgb, Vector3.Zero, Vector3.One), (byte)Math.Clamp(alpha * 255f + 0.5f, 0, 255));
    }

    /// <summary>Finds or adds the TEXS entry for a bake texture, spelled the way HD models spell theirs.</summary>
    private static int TextureIndex(MdxModel model, string bakePath)
    {
        // "_HD.w3mod/Textures/FX/Flare/Flare_BW.tif" -> "Textures\FX\Flare\Flare_BW.tif": the tree
        // prefix is the model's own, and the texture cache maps .tif to the .dds the archive holds.
        string rel = bakePath.Replace('/', '\\');
        int cut = rel.IndexOf(".w3mod\\", StringComparison.OrdinalIgnoreCase);
        if (cut >= 0) rel = rel[(cut + ".w3mod\\".Length)..];

        for (int i = 0; i < model.Textures.Count; i++)
            if (model.Textures[i].ReplaceableId == 0 && string.Equals(model.Textures[i].FileName, rel, StringComparison.OrdinalIgnoreCase))
                return i;
        model.Textures.Add(new MdxTexture { ReplaceableId = 0, FileName = rel, Flags = 0 });
        return model.Textures.Count - 1;
    }

    // ---------------------------------------------------------------- constant recovery

    /// <summary>
    /// Lifetime range in seconds.
    /// </summary>
    /// <remarks>
    /// Two facts pin it down. The layer's spawn script sets the lifetime first, so the first float
    /// the bytecode broadcasts is the life (or its lower bound, with the upper bound next). And the
    /// compiler derives <c>1/life</c> for <c>lifeRatio</c> and keeps it in the layer's constant
    /// pool — so a blob float whose reciprocal sits in the pool is confirmed as a life. Checked on
    /// every layer of Holy Light, Thunder Clap, the priest's attack and the paladin's spell.
    /// </remarks>
    private static (float Life, float Spread, bool Endless) Lifetime(PopcornLayer layer)
    {
        var pool = layer.PoolScalars;
        var blob = layer.BlobScalars;
        bool Confirmed(float l) => l is >= 0.05f and <= 60f && pool.Any(p => Near(p, 1f / l));

        if (blob.Count > 0 && Confirmed(blob[0]))
        {
            float lo = blob[0], hi = lo;
            if (blob.Count > 1 && Confirmed(blob[1]) && blob[1] <= lo * 3 && blob[1] >= lo / 3) hi = blob[1];
            if (hi < lo) (lo, hi) = (hi, lo);
            return ((lo + hi) / 2, hi - lo, false);
        }

        // Fallback: any blob float other than 1 whose reciprocal is in the pool.
        var lives = blob.Where(l => l != 1f && Confirmed(l)).Distinct().OrderBy(l => l).ToList();
        if (lives.Count > 0)
        {
            float lo = lives[0], hi = MathF.Min(lives[^1], lo * 3);
            return ((lo + hi) / 2, hi - lo, false);
        }
        // No lifetime anywhere: the layer lives as long as the effect does (clamped against the
        // infinity its pool carries). Ten seconds keeps the card renewed without a visible seam.
        return layer.PoolHasInfinity ? (10f, 0f, true) : (0.6f, 0f, false);
    }

    /// <summary>
    /// Particles the spawner fires, and how long it takes to fire them. The spawner's count and its
    /// own duration are copied into the layer's pool as equal pairs: the duration is the pair that
    /// reads as seconds, the count the largest remaining pair that is not 1 and not a lifetime or
    /// its reciprocal.
    /// </summary>
    private static (float Count, float Duration) SpawnCount(PopcornLayer layer, float life, float spread)
    {
        var pool = layer.PoolScalars;
        float loLife = life - spread / 2, hiLife = life + spread / 2;
        bool IsLifeLike(float v) =>
            Near(v, loLife) || Near(v, hiLife) || Near(v, life) || Near(v, 1f / loLife) || Near(v, 1f / hiLife) || Near(v, 1f / life);

        var pairs = new List<float>();
        for (int i = 0; i + 1 < pool.Count; i++)
        {
            float v = pool[i];
            if (pool[i + 1] == v && v != 1f && v > 0 && v <= 500f && !IsLifeLike(v)) { pairs.Add(v); i++; }
        }
        // Spawners run for well under three seconds; when the spawner lasts exactly as long as its
        // particles the pair was already excluded as life-like, and the life stands in for it.
        float duration = pairs.FirstOrDefault(v => v is >= 0.05f and <= 3f, life);
        var counts = pairs.Where(v => v >= 1f && v != duration).ToList();
        float count = counts.Count > 0 ? counts.Max() : 2f;
        return (count, duration);
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) <= 0.02f * MathF.Max(MathF.Abs(b), 1e-3f);

    /// <summary>Size over life in metres as (start, middle, end, middle time).</summary>
    private static (float S0, float S1, float S2, float Mid) SizeRamp(PopcornLayer layer, float life, float spread)
    {
        var curve = layer.ScalarCurve;
        if (curve is not null && curve.MaxScalar() is > 0.02f and < 100f)
        {
            int peak = curve.PeakKnot();
            float s0 = curve.At(0).X, s1 = curve.At(peak).X, s2 = curve.At(curve.Count - 1).X;
            float mid = curve.Times[peak];
            if (peak == 0 || peak == curve.Count - 1) { s1 = curve.Sample(0.5f).X; mid = 0.5f; }
            return (MathF.Max(s0, 0), MathF.Max(s1, 0), MathF.Max(s2, 0), mid);
        }

        // No curve: a size from the pool. An ascending pair of plausible metres (a rand(min, max))
        // that is not the life, its reciprocal or a spawner pair is the best sign; failing that the
        // median plausible scalar; failing that half a metre.
        var pool = layer.PoolScalars;
        float loLife = MathF.Max(life - spread / 2, 0.01f), hiLife = life + spread / 2;
        bool Plausible(float v) => v is >= 0.05f and <= 20f && v != 1f
                                   && !Near(v, life) && !Near(v, loLife) && !Near(v, hiLife)
                                   && !Near(v, 1f / life) && !Near(v, 1f / loLife) && !Near(v, 1f / hiLife);
        for (int i = 0; i + 1 < pool.Count; i++)
        {
            float a = pool[i], b = pool[i + 1];
            if (Plausible(a) && Plausible(b) && a < b && b <= a * 6 && !(i + 2 < pool.Count && pool[i + 2] == b))
            {
                float mean = (a + b) / 2;
                return (mean, mean, mean, 0.5f);
            }
        }
        var candidates = pool.Where(Plausible).OrderBy(v => v).ToList();
        float size = candidates.Count > 0 ? candidates[candidates.Count / 2] : 0.5f;
        return (size, size, size, 0.5f);
    }

    /// <summary>
    /// Colour and alpha at start, peak and end from the layer's colour curve, tinted by the
    /// emitter's multiplier. PopcornFX colours are HDR; a channel above one is clamped and the
    /// overshoot pushed into alpha, which for an additive card is the same brightness.
    /// </summary>
    private static (Vector3 C0, Vector3 C1, Vector3 C2, byte A0, byte A1, byte A2, float Mid)
        ColourRamp(PopcornLayer layer, Vector4 tint)
    {
        var curve = layer.ColorCurve;
        if (curve is null)
        {
            var w = new Vector3(tint.X, tint.Y, tint.Z);
            return (w, w, w, (byte)(255 * Math.Clamp(tint.W, 0, 1)), (byte)(255 * Math.Clamp(tint.W, 0, 1)), 0, 0.5f);
        }
        int peak = curve.PeakKnot();
        float mid = curve.Times[peak];
        if (peak == 0 || peak == curve.Count - 1) mid = 0.5f;
        var (c0, a0) = Split(curve.At(0), tint);
        var (c1, a1) = Split(peak == 0 || peak == curve.Count - 1 ? curve.Sample(0.5f) : curve.At(peak), tint);
        var (c2, a2) = Split(curve.At(curve.Count - 1), tint);
        return (c0, c1, c2, a0, a1, a2, mid);

        static (Vector3, byte) Split(Vector4 v, Vector4 tint)
        {
            var rgb = new Vector3(v.X * tint.X, v.Y * tint.Y, v.Z * tint.Z);
            float alpha = v.W * tint.W;
            float over = MathF.Max(MathF.Max(rgb.X, rgb.Y), MathF.Max(rgb.Z, 1f));
            if (over > 1f) { rgb /= over; alpha *= MathF.Min(over, 2f); }
            return (Vector3.Clamp(rgb, Vector3.Zero, Vector3.One), (byte)Math.Clamp(alpha * 255f + 0.5f, 0, 255));
        }
    }

    /// <summary>
    /// A beam's length in metres: the mean of the Z-only pool constants between 0.1 and 20 m, or 0.
    /// A layer that draws its length at random stores the two bounds as two such constants (Holy
    /// Light: 5.4 and 13.2 m), and the mean is what a uniform draw averages to. X-only entries are
    /// the bounds of the layer's scalar curve and say nothing about direction; a Z value in the
    /// tens is a gravity (Thunder Clap's sparks fall at −30 to −40 m/s²), not a beam.
    /// </summary>
    private static float BeamLength(PopcornLayer layer)
    {
        float sum = 0; int n = 0;
        foreach (var a in layer.PoolAxes)
            if (a.X == 0 && a.Y == 0 && MathF.Abs(a.Z) is >= 0.1f and <= 20f) { sum += MathF.Abs(a.Z); n++; }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Speed in metres per second for a flare whose pool holds a symmetric ± offset pair, else 0.</summary>
    private static float ScatterSpeed(PopcornLayer layer)
    {
        foreach (var a in layer.PoolAxes)
        {
            if (a.Z == 0 || a.X != 0 || a.Y != 0) continue;
            if (layer.PoolAxes.Any(b => b.X == 0 && b.Y == 0 && MathF.Abs(b.Z + a.Z) < 1e-4f) && MathF.Abs(a.Z) is >= 0.1f and <= 20f)
                return MathF.Abs(a.Z);
        }
        return 0;
    }

    // ---------------------------------------------------------------- sequence gating

    /// <summary>
    /// A stepped visibility track built from <c>popcornFlags</c>: one key at the start of every
    /// sequence, 1 where the effect runs and 0 where it does not, so the animator's per-sequence
    /// window rule reads exactly one key in each sequence.
    /// </summary>
    public static MdxTrack<float>? GateTrack(MdxModel model, string flags)
    {
        var gates = ParseFlags(flags);
        if (gates.Count == 0 || model.Sequences.Count == 0) return null;

        var times = new int[model.Sequences.Count];
        var values = new float[model.Sequences.Count];
        var order = model.Sequences.Select((s, i) => (s, i)).OrderBy(t => t.s.IntervalStart).ToList();
        for (int k = 0; k < order.Count; k++)
        {
            times[k] = order[k].s.IntervalStart;
            values[k] = IsOn(order[k].s.Name, gates) ? 1f : 0f;
        }
        if (values.All(v => v >= 0.5f)) return null;                // always on: no track needed
        return new MdxTrack<float> { Tag = "KPPV", Interpolation = MdxInterpolation.None, Times = times, Values = values };
    }

    /// <summary>Parses <c>Always=on, Death=off, Attack Spell=on</c> into ordered (name, on) pairs, forgiving spaces and case.</summary>
    public static List<(string Name, bool On)> ParseFlags(string flags)
    {
        var list = new List<(string, bool)>();
        foreach (string part in flags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            string name = part[..eq].Trim();
            string value = part[(eq + 1)..].Trim();
            if (name.Length == 0) continue;
            bool on = value.StartsWith("on", StringComparison.OrdinalIgnoreCase) || value == "1"
                      || value.StartsWith("true", StringComparison.OrdinalIgnoreCase);
            list.Add((name, on));
        }
        return list;
    }

    /// <summary>
    /// Whether a sequence runs the effect. The most specific gate whose words open the sequence's
    /// name wins (<c>Stand Channel</c> beats <c>Stand</c>; <c>Decay</c> covers <c>Decay Flesh</c>),
    /// then <c>Always</c> (or its typo), then on.
    /// </summary>
    public static bool IsOn(string sequenceName, List<(string Name, bool On)> gates)
    {
        var seqWords = Words(sequenceName);
        int bestLen = 0; bool best = true; bool found = false;
        bool? always = null;
        foreach (var (name, on) in gates)
        {
            var w = Words(name);
            if (w.Length == 1 && IsAlways(w[0])) { always = on; continue; }
            if (w.Length == 0 || w.Length > seqWords.Length) continue;
            bool match = true;
            for (int i = 0; i < w.Length && match; i++) match = string.Equals(w[i], seqWords[i], StringComparison.OrdinalIgnoreCase);
            if (match && w.Length > bestLen) { bestLen = w.Length; best = on; found = true; }
        }
        if (found) return best;
        return always ?? true;

        static string[] Words(string s) => s.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
                                            .Where(t => !t.All(char.IsDigit)).ToArray();
        static bool IsAlways(string w) => w.Equals("Always", StringComparison.OrdinalIgnoreCase)
                                          || w.Equals("Alwyas", StringComparison.OrdinalIgnoreCase)
                                          || w.Equals("Allways", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A copy of a multiplier track with every value scaled, or null when there is none.</summary>
    private static MdxTrack<float>? ScaledTrack(MdxTrack<float>? track, float scale)
    {
        if (track is null || track.Count == 0) return null;
        return new MdxTrack<float>
        {
            Tag = track.Tag, Interpolation = track.Interpolation, GlobalSequenceId = track.GlobalSequenceId,
            Times = track.Times, Values = track.Values.Select(v => v * scale).ToArray(),
            InTangents = track.InTangents?.Select(v => v * scale).ToArray(),
            OutTangents = track.OutTangents?.Select(v => v * scale).ToArray(),
        };
    }
}
