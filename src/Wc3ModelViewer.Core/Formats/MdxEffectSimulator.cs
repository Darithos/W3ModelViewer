using System.Numerics;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>One live particle, in world space.</summary>
public struct MdxParticle
{
    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>Seconds lived so far, and the total this particle was given.</summary>
    public float Age;
    public float Lifespan;

    /// <summary>Which emitter spawned it — indexes <see cref="MdxModel.ParticleEmitters"/>.</summary>
    public int Emitter;

    public readonly float Fraction => Lifespan <= 0 ? 1f : Math.Clamp(Age / Lifespan, 0f, 1f);
}

/// <summary>A ribbon's trailing chain of emitted points, newest last.</summary>
public sealed class MdxRibbonTrail
{
    public int Emitter { get; init; }

    /// <summary>Emitted positions with the age of each, newest last.</summary>
    public List<(Vector3 Above, Vector3 Below, float Age)> Edges { get; } = [];
}

/// <summary>
/// Runs Warcraft III's particle and ribbon emitters forward in time so they can be drawn.
/// </summary>
/// <remarks>
/// This is a *viewer* simulation, not a port of the game's solver: it reproduces what the emitter
/// parameters describe — spawn rate, a cone of initial velocity, gravity, and a three-stage colour,
/// alpha and scale ramp over each particle's life — which is enough to judge whether an effect was
/// read correctly and what it will look like. The engine's finer behaviour (tumble, drag, wind,
/// squirt bursts) is deliberately absent, matching what the reader actually parses.
///
/// Randomness is drawn from a per-emitter seeded generator rather than a shared one, so an emitter's
/// look does not change when an unrelated emitter is added, removed, or hidden.
/// </remarks>
public sealed class MdxEffectSimulator(MdxModel model)
{
    private readonly MdxModel _model = model;
    private readonly List<MdxParticle> _particles = [];
    private readonly List<MdxRibbonTrail> _trails = [];
    private readonly Dictionary<int, Random> _rng = [];
    private readonly Dictionary<int, float> _spawnCredit = [];

    /// <summary>Hard ceiling on live particles, so a pathological emission rate cannot stall the UI.</summary>
    public int MaxParticles { get; init; } = 4000;

    public IReadOnlyList<MdxParticle> Particles => _particles;
    public IReadOnlyList<MdxRibbonTrail> Trails => _trails;

    /// <summary>Emitters the caller has switched off. Indexes <see cref="MdxModel.ParticleEmitters"/>.</summary>
    public HashSet<int> HiddenParticleEmitters { get; } = [];

    public void Reset()
    {
        _particles.Clear();
        _trails.Clear();
        _spawnCredit.Clear();
        _rng.Clear();
    }

    /// <summary>
    /// Advances every emitter by <paramref name="dt"/> seconds. The animator must already have been
    /// evaluated for this frame — emitters read their node's world transform from it.
    /// </summary>
    public void Update(float dt, MdxAnimator animator, MdxSequence? sequence, int timeMs, long? wallMs = null)
    {
        dt = Math.Clamp(dt, 0f, 0.1f);                        // a long stall must not spawn a burst
        if (dt <= 0) return;

        AgeParticles(dt);
        for (int i = 0; i < _model.ParticleEmitters.Count; i++)
        {
            if (HiddenParticleEmitters.Contains(i)) continue;
            UpdateParticleEmitter(i, _model.ParticleEmitters[i], dt, animator, sequence, timeMs, wallMs);
        }
        UpdateRibbons(dt, animator, sequence, timeMs, wallMs);
    }

    private void AgeParticles(float dt)
    {
        int write = 0;
        for (int i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.Lifespan) continue;                // dead: dropped by not being copied down

            var e = _model.ParticleEmitters[p.Emitter];
            p.Velocity -= new Vector3(0, 0, e.Gravity * dt);  // gravity is a plain -Z acceleration
            p.Position += p.Velocity * dt;
            _particles[write++] = p;
        }
        _particles.RemoveRange(write, _particles.Count - write);
    }

    private void UpdateParticleEmitter(int index, MdxParticleEmitter2 e, float dt, MdxAnimator animator,
                                       MdxSequence? sequence, int timeMs, long? wallMs)
    {
        if (e.NodeIndex < 0 || e.NodeIndex >= _model.Nodes.Count) return;
        if (Visibility(animator, e.VisibilityTrack, sequence, timeMs, wallMs) < 0.5f) return;

        float rate = Sample(animator, e.EmissionRateTrack, e.EmissionRate, sequence, timeMs, wallMs);
        if (rate <= 0) return;

        float credit = _spawnCredit.GetValueOrDefault(index) + rate * dt;
        int spawn = (int)credit;
        _spawnCredit[index] = credit - spawn;
        if (spawn <= 0) return;

        var world = animator.World(e.NodeIndex);
        var origin = new Vector3(world.M41, world.M42, world.M43);
        var rng = Rng(index);

        float speed = Sample(animator, e.SpeedTrack, e.Speed, sequence, timeMs, wallMs);
        float variation = Sample(animator, e.VariationTrack, e.Variation, sequence, timeMs, wallMs);
        float latitude = Sample(animator, e.LatitudeTrack, e.Latitude, sequence, timeMs, wallMs);
        float life = Sample(animator, e.LifeTrack, e.Life, sequence, timeMs, wallMs);
        float width = Sample(animator, e.WidthTrack, e.Width, sequence, timeMs, wallMs);
        float length = Sample(animator, e.LengthTrack, e.Length, sequence, timeMs, wallMs);

        for (int n = 0; n < spawn && _particles.Count < MaxParticles; n++)
        {
            // Emission is a cone about the node's -Z, opened by `latitude` degrees. A plane emitter
            // additionally spreads the spawn point over its width and length.
            float lat = float.DegreesToRadians(latitude) * (float)rng.NextDouble();
            float azimuth = (float)(rng.NextDouble() * Math.Tau);
            var dir = new Vector3(
                MathF.Sin(lat) * MathF.Cos(azimuth),
                MathF.Sin(lat) * MathF.Sin(azimuth),
                MathF.Cos(lat));

            // The emitter's own rotation orients the cone; translation is already in `origin`.
            dir = Vector3.TransformNormal(dir, world);
            if (dir.LengthSquared() > 1e-8f) dir = Vector3.Normalize(dir);

            var spawnAt = origin;
            if (width > 0 || length > 0)
            {
                var offset = new Vector3(((float)rng.NextDouble() - 0.5f) * width,
                                         ((float)rng.NextDouble() - 0.5f) * length, 0);
                spawnAt += Vector3.TransformNormal(offset, world);
            }

            float v = speed * (1f + variation * (float)(rng.NextDouble() * 2 - 1));
            _particles.Add(new MdxParticle
            {
                Position = spawnAt,
                Velocity = dir * v,
                Age = 0,
                Lifespan = MathF.Max(life, 0.01f),
                Emitter = index,
            });
        }
    }

    private void UpdateRibbons(float dt, MdxAnimator animator, MdxSequence? sequence, int timeMs, long? wallMs)
    {
        while (_trails.Count < _model.RibbonEmitters.Count)
            _trails.Add(new MdxRibbonTrail { Emitter = _trails.Count });

        for (int i = 0; i < _model.RibbonEmitters.Count; i++)
        {
            var e = _model.RibbonEmitters[i];
            var trail = _trails[i];

            foreach (int k in Enumerable.Range(0, trail.Edges.Count))
                trail.Edges[k] = trail.Edges[k] with { Age = trail.Edges[k].Age + dt };
            float maxAge = MathF.Max(e.EdgeLifetime, 0.25f);  // the client enforces this same floor
            trail.Edges.RemoveAll(x => x.Age > maxAge);

            if (e.NodeIndex < 0 || e.NodeIndex >= _model.Nodes.Count) continue;
            if (Visibility(animator, e.VisibilityTrack, sequence, timeMs, wallMs) < 0.5f) continue;

            var world = animator.World(e.NodeIndex);
            var origin = new Vector3(world.M41, world.M42, world.M43);
            float above = Sample(animator, e.HeightAboveTrack, e.HeightAbove, sequence, timeMs, wallMs);
            float below = Sample(animator, e.HeightBelowTrack, e.HeightBelow, sequence, timeMs, wallMs);
            var up = Vector3.TransformNormal(Vector3.UnitZ, world);
            if (up.LengthSquared() > 1e-8f) up = Vector3.Normalize(up); else up = Vector3.UnitZ;

            trail.Edges.Add((origin + up * above, origin - up * below, 0f));

            // Cap the chain: edgesPerSecond x lifetime is the steady-state length, and a stalled
            // frame must not let it grow without bound.
            int cap = Math.Max(4, (int)(e.EdgesPerSecond * maxAge) + 2);
            if (trail.Edges.Count > cap) trail.Edges.RemoveRange(0, trail.Edges.Count - cap);
        }
    }

    /// <summary>Colour, alpha and scale of a particle at its current age, from the three-stage ramp.</summary>
    public (Vector3 Color, float Alpha, float Scale) Appearance(in MdxParticle p)
    {
        var e = _model.ParticleEmitters[p.Emitter];
        float f = p.Fraction;
        float mid = Math.Clamp(e.MiddleTime, 0.001f, 0.999f);

        // Two segments, start->middle->end, with `middleTime` as the join.
        (Vector3 ca, Vector3 cb, float aa, float ab, float sa, float sb, float local) = f <= mid
            ? (e.StartColor, e.MiddleColor, e.StartAlpha / 255f, e.MiddleAlpha / 255f, e.StartScale, e.MiddleScale, f / mid)
            : (e.MiddleColor, e.EndColor, e.MiddleAlpha / 255f, e.EndAlpha / 255f, e.MiddleScale, e.EndScale, (f - mid) / (1 - mid));

        return (Vector3.Lerp(ca, cb, local),
                float.Lerp(aa, ab, local),
                float.Lerp(sa, sb, local));
    }

    /// <summary>
    /// The sprite-sheet cell a particle shows now, as a UV rectangle. Cells are numbered row-major
    /// over <see cref="MdxParticleEmitter2.Rows"/> x <see cref="MdxParticleEmitter2.Columns"/>.
    /// </summary>
    public (float U0, float V0, float U1, float V1) CellUv(in MdxParticle p)
    {
        var e = _model.ParticleEmitters[p.Emitter];
        int cells = Math.Max(1, e.Rows * e.Columns);
        int first = Math.Clamp(e.HeadCellStart, 0, cells - 1);
        int last = Math.Clamp(e.HeadCellEnd, first, cells - 1);

        int cell = first;
        if (last > first)
        {
            float walked = p.Fraction * e.HeadCellRepeat;
            cell = first + (int)((walked - MathF.Floor(walked)) * (last - first + 1));
            cell = Math.Clamp(cell, first, last);
        }

        float du = 1f / e.Columns, dv = 1f / e.Rows;
        int col = cell % e.Columns, row = cell / e.Columns;
        return (col * du, row * dv, (col + 1) * du, (row + 1) * dv);
    }

    private Random Rng(int emitter)
    {
        if (_rng.TryGetValue(emitter, out var r)) return r;
        // Seeded per emitter so playback is repeatable and one emitter's look never depends on
        // how many others are running.
        r = new Random(unchecked(emitter * 2654435761u).GetHashCode());
        _rng[emitter] = r;
        return r;
    }

    private static float Sample(MdxAnimator animator, MdxTrack<float>? track, float fallback,
                                MdxSequence? seq, int timeMs, long? wallMs)
        => animator.SampleFloat(track, seq, timeMs, fallback, wallMs);

    /// <summary>
    /// An emitter with no visibility track is always on. Warcraft III drives these per sequence, so
    /// a model's idle emitters stay dark until the sequence that uses them plays.
    /// </summary>
    private static float Visibility(MdxAnimator animator, MdxTrack<float>? track,
                                    MdxSequence? seq, int timeMs, long? wallMs)
        => animator.SampleFloat(track, seq, timeMs, 1f, wallMs);
}
