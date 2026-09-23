using System.Numerics;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>
/// What one PopcornFX renderer's particles actually do over a headless run, reduced to the handful
/// of numbers a fixed-function particle system (StarCraft II's <c>PAR_</c>) can hold. All lengths are
/// metres in the effect's own Z-up frame; times are seconds from the effect's start.
/// </summary>
/// <remarks>
/// The export used to guess these from constants the compiler left in each layer's pool, and every
/// guess put the emitter at the effect origin with no motion: Holy Light's rune, 3.85 m up in the
/// game, sat on the ground in StarCraft II with its eight shafts piled onto it. Running the scripts
/// and measuring the particles replaces all of that with what the effect really does.
/// </remarks>
public sealed class PkRendererStats
{
    public required PkRendererDef Renderer { get; init; }
    public required string LayerName { get; init; }

    public int Births { get; set; }
    public float FirstBirth { get; set; }
    public float LastBirth { get; set; }
    /// <summary>Still being born at the end of the run: a steady emitter rather than a burst.</summary>
    public bool Continuous { get; set; }
    /// <summary>Particles that never age (<c>invLife = 0</c>), renewed only when the effect restarts.</summary>
    public bool Immortal { get; set; }

    public float Life { get; set; }
    /// <summary>Mean birth position, and the half extents that hold nine in ten births around it.</summary>
    public Vector3 SpawnCentre { get; set; }
    public Vector3 SpawnHalfExtents { get; set; }

    /// <summary>Mean velocity direction (unit; +Z when the particles scatter evenly or stay put).</summary>
    public Vector3 Direction { get; set; } = Vector3.UnitZ;
    public float Speed { get; set; }
    public float SpeedVariation { get; set; }
    /// <summary>Half-angle, in degrees, of the cone holding nine in ten velocities about <see cref="Direction"/>.</summary>
    public float SpreadDegrees { get; set; }

    /// <summary>Radius over life at start, middle and end, and when the middle falls (0..1).</summary>
    public (float Start, float Middle, float End, float MiddleTime) Size { get; set; }
    /// <summary>Colour (HDR RGB, alpha in W) over life at start, middle and end.</summary>
    public (Vector4 Start, Vector4 Middle, Vector4 End, float MiddleTime) Colour { get; set; }

    /// <summary>Axis-aligned renderers: mean axis length and direction.</summary>
    public float AxisLength { get; set; }
    public Vector3 AxisDirection { get; set; } = Vector3.UnitZ;
    /// <summary>
    /// How much the axes agree, 0..1 (length of the mean unit axis). Near 1: parallel beams, like
    /// Holy Light's shafts. Low: streaks flying every way, each stretched along its own motion.
    /// </summary>
    public float AxisCoherence { get; set; } = 1f;
    /// <summary>Plane-aligned renderers: whether the cards mostly lie flat (normal within ~45° of Z).</summary>
    public bool LiesFlat { get; set; }
    /// <summary>Plane-aligned renderers: the mean card normal in the effect's frame (unit), facing outward from the Z axis.</summary>
    public Vector3 CardNormal { get; set; } = Vector3.UnitZ;
    /// <summary>
    /// The same normal in the frame that turns with the particle about the effect's Z axis:
    /// (radial, tangential, up). What an orbiting card keeps while <see cref="CardNormal"/> averages away.
    /// </summary>
    public Vector3 CardNormalRadial { get; set; } = Vector3.UnitZ;

    /// <summary>
    /// Particles that circle the effect's origin about its Z axis: the signed angular velocity in
    /// radians per second (positive counter-clockwise seen from above), or 0 when they do not orbit.
    /// Unholy Aura's runes settle onto a ring and circle it for as long as the effect lives.
    /// </summary>
    public float OrbitAngularVelocity { get; set; }
    /// <summary>Mean distance from the Z axis while orbiting, metres.</summary>
    public float OrbitRadius { get; set; }

    /// <summary>
    /// This renderer's layer reads <c>__a_Game.TeamColor</c>: its colour is the player's, and the
    /// measured colour above is only what it comes to under the white the run was given. The
    /// export hands such a renderer to StarCraft II's own player-colour channel instead.
    /// </summary>
    public bool TeamColoured { get; set; }

    /// <summary>
    /// Runs <paramref name="def"/> for up to <paramref name="seconds"/> and measures every drawable
    /// billboard renderer. Renderers that never draw a particle are left out.
    /// </summary>
    /// <param name="colourMultiplier">
    /// The CORN emitter's colour multiplier, handed to the scripts as <c>__a_Game.ColorMultiplier</c>
    /// exactly as the viewer hands it in. Scripts that read it apply it themselves; scripts that do not
    /// are unaffected — the hero glow's is (1,1,1,0) and applying it afterwards zeroed the glow.
    /// </param>
    public static List<PkRendererStats> Measure(PkEffectDef def, float seconds = 5f, int seed = 7, Vector4? colourMultiplier = null)
    {
        const float dt = 1f / 30f;
        var fx = new PkEffectInstance(def, new PkEnvironment { ColorMultiplier = colourMultiplier ?? Vector4.One }, seed);
        var tracks = new Dictionary<(PkRendererDef, long), Track>();
        var order = new List<(PkRendererDef Renderer, string Layer)>();
        var teamLayers = new HashSet<string>(StringComparer.Ordinal);

        float t = 0;
        for (; t < seconds && fx.IsAlive; t += dt)
        {
            fx.Update(dt);
            foreach (var st in fx.Slots)
            {
                if (st is null) continue;
                // Checked before the empty-slot skip: a layer that has already run and died still
                // told us it wanted the player's colour.
                if (st.ReadsTeamColor) teamLayers.Add(st.Def.Name);
                if (st.Count == 0) continue;
                int fl = st.LifeRatioField, fi = st.InvLifeField;
                foreach (var r in st.Def.Renderers)
                {
                    if (r.Kind != PkRendererKind.Billboard) continue;
                    if (!order.Any(o => ReferenceEquals(o.Renderer, r))) order.Add((r, st.Def.Name));
                    int fp = r.Input("Position"), fs = r.Input("Size"), fs2 = r.Input("Size2"), fc = r.Input("Color"),
                        fa = r.Input("Axis"), fn = r.Input("NormalAxis"), fe = r.Input("Enabled");
                    for (int p = 0; p < st.Count; p++)
                    {
                        if (fe >= 0 && st.Fields[fe][p].I0 == 0) continue;
                        var key = (r, st.Ids[p]);
                        var pos = fp >= 0 ? st.Fields[fp][p].Xyz : Vector3.Zero;
                        if (!float.IsFinite(pos.X) || pos.LengthSquared() > 1e6f) continue;
                        if (!tracks.TryGetValue(key, out var tr))
                        {
                            float inv = fi >= 0 ? st.Fields[fi][p].X : -1;
                            tr = new Track { Born = t, BirthPos = pos, InvLife = inv };
                            tracks[key] = tr;
                        }
                        else if (tr.Samples.Count == 1 && tr.BirthPos == Vector3.Zero)
                        {
                            // Many layers write Position only in their evolve script, which a particle
                            // first runs the frame after it is born; until then it reads the origin.
                            tr.BirthPos = pos;
                        }
                        tr.LastSeen = t;
                        tr.LastPos = pos;
                        float radius = r.Size2D && fs2 >= 0 ? (st.Fields[fs2][p].X + st.Fields[fs2][p].Y) * 0.5f
                                     : fs >= 0 ? st.Fields[fs][p].X : 1f;
                        var colour = fc >= 0 ? st.Fields[fc][p].Xyzw : Vector4.One;
                        float ratio = fl >= 0 && tr.InvLife > 0 ? st.Fields[fl][p].X : float.NaN;
                        tr.Samples.Add((t - tr.Born, ratio, MathF.Abs(radius), colour, pos));
                        if (fa >= 0)
                        {
                            var axis = st.Fields[fa][p].Xyz;
                            tr.AxisSum += axis; tr.AxisCount++;
                            if (axis.LengthSquared() > 1e-10f) tr.AxisUnitSum += Vector3.Normalize(axis);
                        }
                        if (fn >= 0)
                        {
                            var nrm = SafeNormal(st.Fields[fn][p].Xyz);
                            tr.NormalZ += MathF.Abs(nrm.Z); tr.NormalCount++;
                            // The normal in a frame that turns with the particle about the effect's Z
                            // axis (radial, tangential, up), signed to face outward, so an orbiting
                            // card's facing survives averaging over the orbit.
                            var radial = new Vector3(pos.X, pos.Y, 0);
                            if (radial.LengthSquared() > 1e-6f)
                            {
                                radial = Vector3.Normalize(radial);
                                var tangent = Vector3.Cross(Vector3.UnitZ, radial);
                                var local = new Vector3(Vector3.Dot(nrm, radial), Vector3.Dot(nrm, tangent), nrm.Z);
                                if (local.X < 0) local = -local;
                                tr.NormalLocalSum += local;
                                tr.NormalSum += local.X == Vector3.Dot(nrm, radial) ? nrm : -nrm;
                            }
                            else { tr.NormalLocalSum += nrm.Z < 0 ? -nrm : nrm; tr.NormalSum += nrm.Z < 0 ? -nrm : nrm; }
                        }
                    }
                }
            }
        }

        float end = t;
        var result = new List<PkRendererStats>();
        foreach (var (renderer, layer) in order)
        {
            var list = tracks.Where(kv => ReferenceEquals(kv.Key.Item1, renderer)).Select(kv => kv.Value).ToList();
            if (list.Count == 0) continue;
            var stats = Reduce(renderer, layer, list, end, dt);
            stats.TeamColoured = teamLayers.Contains(layer);
            result.Add(stats);
        }
        return result;
    }

    private sealed class Track
    {
        public float Born, LastSeen, InvLife;
        public Vector3 BirthPos, LastPos, AxisSum, AxisUnitSum, NormalSum, NormalLocalSum;
        public int AxisCount, NormalCount;
        public float NormalZ;
        public List<(float Age, float Ratio, float Radius, Vector4 Colour, Vector3 Pos)> Samples { get; } = [];
    }

    /// <summary>
    /// Angular velocity about the origin's Z axis and the ring radius, from the second half of each
    /// particle's samples (Unholy Aura's runes first drop onto their ring), when every particle
    /// circles at the same rate on a steady radius. (0, 0) otherwise.
    /// </summary>
    private static (float Omega, float Radius) Orbit(List<Track> tracks)
    {
        var omegas = new List<float>(); var radii = new List<float>();
        foreach (var k in tracks)
        {
            var s = k.Samples;
            if (s.Count < 12) return (0, 0);
            int from = s.Count / 2;
            float prev = MathF.Atan2(s[from].Pos.Y, s[from].Pos.X), total = 0;
            float rSum = 0, rSq = 0; int n = 0;
            for (int i = from; i < s.Count; i++)
            {
                float r = MathF.Sqrt(s[i].Pos.X * s[i].Pos.X + s[i].Pos.Y * s[i].Pos.Y);
                if (r < 0.05f) return (0, 0);
                rSum += r; rSq += r * r; n++;
                if (i == from) continue;
                float a = MathF.Atan2(s[i].Pos.Y, s[i].Pos.X);
                float d = a - prev;
                if (d > MathF.PI) d -= 2 * MathF.PI; else if (d < -MathF.PI) d += 2 * MathF.PI;
                total += d; prev = a;
            }
            float span = s[^1].Age - s[from].Age;
            if (span < 0.3f) return (0, 0);
            float mean = rSum / n, sd = MathF.Sqrt(MathF.Max(rSq / n - mean * mean, 0));
            if (sd > 0.15f * mean) return (0, 0);                     // spiralling or drifting, not a ring
            omegas.Add(total / span); radii.Add(mean);
        }
        if (omegas.Count == 0) return (0, 0);
        float omega = omegas.Average();
        if (MathF.Abs(omega) < 0.1f) return (0, 0);
        if (omegas.Any(o => MathF.Abs(o - omega) > 0.2f * MathF.Abs(omega))) return (0, 0);
        return (omega, radii.Average());
    }

    private static PkRendererStats Reduce(PkRendererDef r, string layer, List<Track> tracks, float end, float dt)
    {
        var births = tracks.Select(k => k.Born).OrderBy(b => b).ToList();
        bool immortal = tracks.All(k => k.InvLife == 0);

        // Life: the script's own 1/invLife where it has one; otherwise how long particles were seen.
        var lives = tracks.Select(k => k.InvLife > 0 ? 1f / k.InvLife : k.LastSeen - k.Born + dt)
                          .Where(l => float.IsFinite(l) && l > 0).ToList();
        float life = immortal ? 10f : lives.Count > 0 ? lives.Average() : 0.5f;

        // An immortal particle is placed where it settles rather than where it appeared: Unholy
        // Aura's runes drift down from 2.4 m onto their orbit, and a still card can only keep one of
        // those. Net motion over a run says nothing about a particle that circles, so it gets none.
        Vector3 Where(Track k) => immortal ? k.LastPos : k.BirthPos;
        var centre = Mean(tracks.Select(Where));
        var half = new Vector3(
            Percentile(tracks.Select(k => MathF.Abs(Where(k).X - centre.X)), 0.9f),
            Percentile(tracks.Select(k => MathF.Abs(Where(k).Y - centre.Y)), 0.9f),
            Percentile(tracks.Select(k => MathF.Abs(Where(k).Z - centre.Z)), 0.9f));

        // Motion from each particle's net displacement over the time it was watched.
        var velocities = immortal ? [] : tracks.Where(k => k.LastSeen - k.Born >= 0.05f)
                               .Select(k => (k.LastPos - k.BirthPos) / (k.LastSeen - k.Born)).ToList();
        var dir = Vector3.UnitZ; float speed = 0, variation = 0, spread = 0;
        if (velocities.Count > 0)
        {
            var speeds = velocities.Select(v => v.Length()).ToList();
            speed = speeds.Average();
            if (speed > 0.05f)
            {
                variation = speeds.Count > 1 ? MathF.Sqrt(speeds.Select(s => (s - speed) * (s - speed)).Average()) / speed : 0;
                var mean = Mean(velocities);
                // A mean much shorter than the average speed means the particles fly apart in every
                // direction: a scatter, emitted over the whole sphere.
                if (mean.Length() < 0.35f * speed) { dir = Vector3.UnitZ; spread = 180f; }
                else
                {
                    dir = Vector3.Normalize(mean);
                    spread = Percentile(velocities.Where(v => v.LengthSquared() > 1e-8f)
                        .Select(v => float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(v), dir), -1f, 1f)))), 0.9f);
                }
            }
            else speed = 0;
        }

        // Size and colour over life, averaged in eleven bins of life ratio.
        const int Bins = 11;
        var sizeSum = new float[Bins]; var colSum = new Vector4[Bins]; var n = new int[Bins];
        foreach (var k in tracks)
            foreach (var s in k.Samples)
            {
                float ratio = float.IsNaN(s.Ratio) ? s.Age / MathF.Max(life, 1e-3f) : s.Ratio;
                int bin = Math.Clamp((int)MathF.Round(ratio * (Bins - 1)), 0, Bins - 1);
                sizeSum[bin] += s.Radius; colSum[bin] += s.Colour; n[bin]++;
            }
        var filled = Enumerable.Range(0, Bins).Where(i => n[i] > 0).ToList();
        float SizeAt(int i) => sizeSum[i] / n[i];
        Vector4 ColAt(int i) => colSum[i] / n[i];
        int first = filled[0], last = filled[^1];
        int sizePeak = filled.OrderByDescending(SizeAt).First();
        int colPeak = filled.OrderByDescending(i => ColAt(i).W * MathF.Max(ColAt(i).X, MathF.Max(ColAt(i).Y, ColAt(i).Z))).First();
        int sizeMid = sizePeak == first || sizePeak == last ? filled[filled.Count / 2] : sizePeak;
        int colMid = colPeak == first || colPeak == last ? filled[filled.Count / 2] : colPeak;

        var axisTracks = tracks.Where(k => k.AxisCount > 0).ToList();
        var axisMean = axisTracks.Count > 0 ? Mean(axisTracks.Select(k => k.AxisSum / k.AxisCount)) : Vector3.Zero;
        float axisLength = axisTracks.Count > 0 ? axisTracks.Average(k => (k.AxisSum / k.AxisCount).Length()) : 0;
        var unitSum = axisTracks.Aggregate(Vector3.Zero, (acc, k) => acc + k.AxisUnitSum);
        int unitCount = axisTracks.Sum(k => k.AxisCount);
        var normalTracks = tracks.Where(k => k.NormalCount > 0).ToList();
        var (orbitOmega, orbitRadius) = Orbit(tracks);
        var normalMean = normalTracks.Count > 0 ? Mean(normalTracks.Select(k => k.NormalSum / k.NormalCount)) : Vector3.Zero;
        var normalLocal = normalTracks.Count > 0 ? Mean(normalTracks.Select(k => k.NormalLocalSum / k.NormalCount)) : Vector3.Zero;

        return new PkRendererStats
        {
            Renderer = r, LayerName = layer,
            Births = tracks.Count, FirstBirth = births[0], LastBirth = births[^1],
            Continuous = !immortal && births[^1] >= end - MathF.Max(0.5f, 2 * dt) && tracks.Count > 2,
            Immortal = immortal,
            Life = life,
            SpawnCentre = centre, SpawnHalfExtents = half,
            Direction = dir, Speed = speed, SpeedVariation = variation, SpreadDegrees = spread,
            Size = (SizeAt(first), SizeAt(sizeMid), SizeAt(last), (float)sizeMid / (Bins - 1)),
            Colour = (ColAt(first), ColAt(colMid), ColAt(last), (float)colMid / (Bins - 1)),
            AxisLength = axisLength,
            AxisDirection = axisMean.LengthSquared() > 1e-8f ? Vector3.Normalize(axisMean) : Vector3.UnitZ,
            AxisCoherence = unitCount > 0 ? unitSum.Length() / unitCount : 1f,
            LiesFlat = normalTracks.Count == 0 || normalTracks.Average(k => k.NormalZ / k.NormalCount) > 0.7f,
            OrbitAngularVelocity = orbitOmega, OrbitRadius = orbitRadius,
            CardNormal = SafeNormal(normalMean), CardNormalRadial = SafeNormal(normalLocal),
        };
    }

    private static Vector3 SafeNormal(Vector3 v) => v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : Vector3.UnitZ;

    private static Vector3 Mean(IEnumerable<Vector3> values)
    {
        Vector3 sum = Vector3.Zero; int n = 0;
        foreach (var v in values) { sum += v; n++; }
        return n == 0 ? Vector3.Zero : sum / n;
    }

    private static float Percentile(IEnumerable<float> values, float q)
    {
        var list = values.OrderBy(v => v).ToList();
        return list.Count == 0 ? 0 : list[Math.Clamp((int)(q * (list.Count - 1)), 0, list.Count - 1)];
    }
}
