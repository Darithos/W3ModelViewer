using System.Numerics;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>One drawable particle, in model units, ready for a renderer to turn into a quad.</summary>
public struct PkSprite
{
    public Vector3 Position;
    /// <summary>Half extents across and along the card (PopcornFX sizes are radii).</summary>
    public Vector2 HalfSize;
    /// <summary>Axis-aligned modes: the full axis vector. Plane-aligned: the in-plane up vector.</summary>
    public Vector3 Axis;
    /// <summary>Plane-aligned: the card's normal.</summary>
    public Vector3 Normal;
    /// <summary>Screen-plane rotation, radians.</summary>
    public float Rotation;
    public Vector4 Color;
    public int Frame;
}

/// <summary>What one renderer of one layer draws this frame.</summary>
public sealed class PkRenderBatch
{
    public required PkRendererDef Renderer { get; init; }
    public required string LayerName { get; init; }
    public List<PkSprite> Sprites { get; } = [];
}

/// <summary>
/// Plays a model's CORN emitter: runs <see cref="PkEffectInstance"/>s at the node's transform, starts
/// one when the sequence gates the effect on, keeps emitting while it stays on, and lets particles die
/// out after it turns off.
/// </summary>
/// <remarks>
/// PopcornFX works in metres; Warcraft III's HD art sits at 50 units per metre (see
/// <see cref="PopcornApproximation.MetresToWc3"/>). The simulation runs in model-space metres, so
/// positions only need scaling on the way in (the node transform) and out (the sprites).
/// </remarks>
public sealed class PkCornPlayer
{
    private readonly MdxModel _model;
    private readonly MdxPopcornEmitter _corn;
    private readonly MdxTrack<float>? _gate;
    private readonly List<PkEffectInstance> _instances = [];
    private bool _wasOn;
    private int _lastTimeMs = int.MinValue;
    private int _seed = 1;

    public PkCornPlayer(MdxModel model, MdxPopcornEmitter corn)
    {
        _model = model;
        _corn = corn;
        _gate = corn.VisibilityTrack ?? PopcornApproximation.GateTrack(model, corn.PopcornFlags);
    }

    /// <summary>The player colour fed to team-coloured effects, 0..1 RGB.</summary>
    public Vector3 PlayerColor { get; set; } = new(1f, 0.01f, 0.01f);

    public PkEffectDef Definition => _corn.Runtime!;

    /// <summary>
    /// The space an effect's sprites fill over its first <paramref name="seconds"/>, in metres around
    /// the effect origin, from a headless run — what a camera framing an effect-only model needs.
    /// </summary>
    public static (Vector3 Min, Vector3 Max)? EstimateBounds(PkEffectDef def, float seconds = 2f)
    {
        var fx = new PkEffectInstance(def, new PkEnvironment(), 99);
        // Extents from percentiles of every sprite's reach, not the absolute min and max: a stray
        // spark flung far out would otherwise frame the effect as a dot.
        var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
        for (float t = 0; t < seconds && fx.IsAlive; t += 1f / 30f)
        {
            fx.Update(1f / 30f);
            foreach (var st in fx.Slots)
            {
                if (st is null) continue;
                foreach (var r in st.Def.Renderers)
                {
                    int fp = r.Input("Position"), fs = r.Input("Size"), fa = r.Input("Axis");
                    if (fp < 0) continue;
                    for (int p = 0; p < st.Count; p++)
                    {
                        var pos = st.Fields[fp][p].Xyz;
                        if (!float.IsFinite(pos.X) || pos.LengthSquared() > 1e6f) continue;
                        float s = fs >= 0 ? MathF.Min(MathF.Abs(st.Fields[fs][p].X), 50f) : 0.5f;
                        var ext = new Vector3(s);
                        if (fa >= 0 && r.Billboard is PopcornBillboardMode.AxisAligned or PopcornBillboardMode.AxisAlignedSpheroid or PopcornBillboardMode.AxisAlignedCapsule)
                            ext += Vector3.Abs(st.Fields[fa][p].Xyz) * 0.5f;
                        xs.Add(pos.X - ext.X); xs.Add(pos.X + ext.X);
                        ys.Add(pos.Y - ext.Y); ys.Add(pos.Y + ext.Y);
                        zs.Add(pos.Z - ext.Z); zs.Add(pos.Z + ext.Z);
                    }
                }
            }
        }
        if (xs.Count == 0) return null;
        xs.Sort(); ys.Sort(); zs.Sort();
        static float Pct(List<float> v, float q) => v[Math.Clamp((int)(q * (v.Count - 1)), 0, v.Count - 1)];
        return (new Vector3(Pct(xs, 0.03f), Pct(ys, 0.03f), Pct(zs, 0.03f)),
                new Vector3(Pct(xs, 0.97f), Pct(ys, 0.97f), Pct(zs, 0.97f)));
    }
    public MdxPopcornEmitter Corn => _corn;
    public IReadOnlyList<PkEffectInstance> Instances => _instances;
    public int LiveParticles => _instances.Sum(i => i.Slots.Where(s => s is not null && s.Def.Renderers.Count > 0).Sum(s => s!.Count));

    public void Reset()
    {
        _instances.Clear();
        _wasOn = false;
        _lastTimeMs = int.MinValue;
    }

    /// <param name="cameraPosition">Camera position in model units.</param>
    public void Update(float dt, MdxAnimator animator, MdxSequence? sequence, int timeMs, long? wallMs, Vector3 cameraPosition)
    {
        if (_corn.Runtime is null) return;
        dt = Math.Clamp(dt, 0f, 0.1f);

        bool on = animator.SampleFloat(_gate, sequence, timeMs, 1f, wallMs) >= 0.5f;
        bool wrapped = timeMs < _lastTimeMs;
        _lastTimeMs = timeMs;

        // A new instance when the effect switches on, or when a looping sequence comes round again
        // and the previous run has stopped emitting. An effect that emits for as long as it is on
        // (a hero glow) keeps its instance across the loop instead of stacking a second one.
        if (on && (!_wasOn || (wrapped && !_instances.Any(StillEmitting))))
            _instances.Add(new PkEffectInstance(_corn.Runtime, new PkEnvironment(), _seed++));
        _wasOn = on;

        const float m = PopcornApproximation.MetresToWc3;
        var world = (uint)_corn.NodeIndex < (uint)_model.Nodes.Count ? animator.World(_corn.NodeIndex) : Matrix4x4.Identity;
        var local = Matrix4x4.CreateScale(m) * world * Matrix4x4.CreateScale(1f / m);
        var colour = _corn.ColorMultiplier;
        var tint = animator.SampleVector(_corn.ColorTrack, sequence, timeMs, Vector3.One, wallMs);
        float alpha = animator.SampleFloat(_corn.AlphaTrack, sequence, timeMs, 1f, wallMs);
        float rate = animator.SampleFloat(_corn.EmissionRateTrack, sequence, timeMs, 1f, wallMs);

        for (int i = _instances.Count - 1; i >= 0; i--)
        {
            var fx = _instances[i];
            var env = fx.Env;
            env.LocalToWorld = local;
            env.CameraPosition = cameraPosition / m;
            env.Running = on && i == _instances.Count - 1;
            env.ColorMultiplier = new Vector4(colour.X * tint.X, colour.Y * tint.Y, colour.Z * tint.Z, colour.W * alpha);
            // Only scripts that want the player's colour read __a_Game.TeamColor (the hero glow
            // multiplies its colour by it), so it always carries the player colour.
            env.TeamColor = new Vector4(PlayerColor, 1f);
            env.EmissionRateMultiplier = rate;
            fx.Update(dt);
            if (!fx.IsAlive) _instances.RemoveAt(i);
        }
    }

    private static bool StillEmitting(PkEffectInstance fx)
        => fx.Slots.Skip(1).Any(s => s is { Count: > 0 } && s.Def.Renderers.Count == 0);

    /// <summary>Collects every renderer's sprites from every running instance, in model units.</summary>
    public List<PkRenderBatch> CollectSprites()
    {
        var batches = new List<PkRenderBatch>();
        if (_corn.Runtime is null) return batches;
        const float m = PopcornApproximation.MetresToWc3;
        var colourMul = _corn.ColorMultiplier;

        foreach (var layer in _corn.Runtime.Layers)
            foreach (var r in layer.Renderers)
                batches.Add(new PkRenderBatch { Renderer = r, LayerName = layer.Name });

        foreach (var fx in _instances)
        {
            foreach (var st in fx.Slots)
            {
                if (st is null || st.Count == 0) continue;
                foreach (var r in st.Def.Renderers)
                {
                    if (r.Kind is not (PkRendererKind.Billboard or PkRendererKind.Distortion)) continue;
                    var batch = batches.First(b => ReferenceEquals(b.Renderer, r));
                    int fPos = r.Input("Position"), fSize = r.Input("Size"), fSize2 = r.Input("Size2"), fAxis = r.Input("Axis"),
                        fNormal = r.Input("NormalAxis"), fRot = r.Input("Rotation"), fCol = r.Input("Color"), fEn = r.Input("Enabled"),
                        fTex = r.Input("TextureID");
                    for (int p = 0; p < st.Count; p++)
                    {
                        if (fEn >= 0 && st.Fields[fEn][p].I0 == 0) continue;
                        var col = fCol >= 0 ? st.Fields[fCol][p].Xyzw : Vector4.One;
                        if (r.Kind == PkRendererKind.Distortion) col = new Vector4(1, 1, 1, 0.15f);
                        if (col.W <= 0.002f && r.Blend != PopcornBlend.AdditiveNoAlpha) continue;
                        Vector2 half;
                        if (r.Size2D && fSize2 >= 0) { var s2 = st.Fields[fSize2][p]; half = new Vector2(s2.X, s2.Y); }
                        else { float s = fSize >= 0 ? st.Fields[fSize][p].X : 1f; half = new Vector2(s, s); }
                        if (!(half.X > 0) && !(half.Y > 0)) continue;
                        batch.Sprites.Add(new PkSprite
                        {
                            Position = (fPos >= 0 ? st.Fields[fPos][p].Xyz : Vector3.Zero) * m,
                            HalfSize = half * m,
                            Axis = (fAxis >= 0 ? st.Fields[fAxis][p].Xyz : Vector3.UnitZ) * m,
                            Normal = fNormal >= 0 ? st.Fields[fNormal][p].Xyz : Vector3.UnitZ,
                            Rotation = fRot >= 0 ? float.DegreesToRadians(st.Fields[fRot][p].X) : 0f,
                            Color = col,
                            Frame = fTex >= 0 ? (int)MathF.Max(0, st.Fields[fTex][p].X) : 0,
                        });
                    }
                }
            }
        }
        _ = colourMul;
        return batches;
    }
}
