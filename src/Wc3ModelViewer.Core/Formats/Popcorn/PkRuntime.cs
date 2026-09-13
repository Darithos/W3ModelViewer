using System.Numerics;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>What an effect instance knows about the world around it. Distances are metres.</summary>
public sealed class PkEnvironment
{
    /// <summary>Effect local space (metres) to simulation world space (metres).</summary>
    public Matrix4x4 LocalToWorld { get; set; } = Matrix4x4.Identity;
    public Vector3 CameraPosition { get; set; } = new(0, -20, 10);
    /// <summary>True while the host wants the effect to keep emitting (the CORN node is gated on).</summary>
    public bool Running { get; set; } = true;
    public float EmissionRateMultiplier { get; set; } = 1;
    public Vector4 ColorMultiplier { get; set; } = Vector4.One;
    /// <summary><c>__a_Game.TeamColor</c>: the player's colour for team-coloured effects, white otherwise.</summary>
    public Vector4 TeamColor { get; set; } = Vector4.One;
    public float Time { get; set; }
}

/// <summary>The particles of one layer slot, stored field by field.</summary>
public sealed class PkLayerState
{
    public required PkLayerDef Def { get; init; }
    public required int SlotIndex { get; init; }
    public PkValue[][] Fields { get; private set; } = [];
    public bool[] Newborn { get; private set; } = [];
    public bool[] Killed { get; private set; } = [];
    /// <summary>A serial per particle, stable across compaction — lets a measurement follow one particle through its life.</summary>
    public long[] Ids { get; private set; } = [];
    private long _nextId;
    public int Count { get; private set; }
    public int Capacity => Newborn.Length;

    public int LifeRatioField { get; init; } = -1;
    public int InvLifeField { get; init; } = -1;

    public int Allocate()
    {
        if (Count == Capacity)
        {
            int cap = Math.Max(16, Capacity * 2);
            var f = new PkValue[Def.Fields.Count][];
            for (int i = 0; i < f.Length; i++)
            {
                f[i] = new PkValue[cap];
                if (i < Fields.Length) Array.Copy(Fields[i], f[i], Count);
            }
            Fields = f;
            var nb = new bool[cap]; Array.Copy(Newborn, nb, Count); Newborn = nb;
            var k = new bool[cap]; Array.Copy(Killed, k, Count); Killed = k;
            var ids = new long[cap]; Array.Copy(Ids, ids, Count); Ids = ids;
        }
        int p = Count++;
        for (int i = 0; i < Fields.Length; i++) Fields[i][p] = default;
        Ids[p] = _nextId++;
        Newborn[p] = true;
        Killed[p] = false;
        return p;
    }

    /// <summary>Removes killed particles, keeping the survivors' order.</summary>
    public void Compact()
    {
        int w = 0;
        for (int r = 0; r < Count; r++)
        {
            if (Killed[r]) continue;
            if (w != r)
            {
                for (int i = 0; i < Fields.Length; i++) Fields[i][w] = Fields[i][r];
                Newborn[w] = Newborn[r];
                Ids[w] = Ids[r];
            }
            Killed[w] = false;
            w++;
        }
        Count = w;
    }

    public void Clear() => Count = 0;
}

/// <summary>
/// One running PopcornFX effect: executes the bake's compiled spawn and evolve scripts over each
/// layer's particles and routes their events through the layer graph.
/// </summary>
/// <remarks>
/// The instruction set, operand spaces, swizzle encoding and graph wiring were decoded from
/// Warcraft III's own bakes; see <c>mdxres/research/popcornfx-vm.md</c>. Built-in functions are
/// reimplemented from their names and how the scripts use them. Scene queries (ray casts, spatial
/// layers) answer "nothing there", which is what an effect previewed on its own would see.
/// </remarks>
public sealed class PkEffectInstance
{
    public const int MaxParticlesPerSlot = 4096;

    private readonly PkEffectDef _def;
    private readonly Random _rng;
    private readonly PkLayerState?[] _slots;
    private readonly Dictionary<PkScript, ScriptPlan> _plans = [];
    private readonly List<PendingSpawn> _pending = [];
    private readonly List<PayloadElement> _elements = [];
    private float _age;
    private bool _started;

    public PkEnvironment Env { get; }
    public float Age => _age;
    public IReadOnlyList<PkLayerState?> Slots => _slots;
    public HashSet<string> Unsupported { get; } = [];

    public PkEffectInstance(PkEffectDef def, PkEnvironment env, int seed)
    {
        _def = def;
        Env = env;
        _rng = new Random(seed);
        _slots = new PkLayerState?[def.Slots.Count];
        for (int s = 0; s < def.Slots.Count; s++)
        {
            int li = def.Slots[s].Layer;
            if ((uint)li >= (uint)def.Layers.Count) continue;
            var layer = def.Layers[li];
            _slots[s] = new PkLayerState
            {
                Def = layer, SlotIndex = s,
                LifeRatioField = layer.Field("self.lifeRatio"),
                InvLifeField = layer.Field("self.invLife"),
            };
        }
    }

    /// <summary>True until every particle of every layer has died (and the effect has started).</summary>
    public bool IsAlive => !_started || _slots.Any(s => s is { Count: > 0 }) || _pending.Count > 0;

    public void Update(float dt)
    {
        if (!_started)
        {
            _started = true;
            if (_slots.Length > 0 && _slots[0] is { } root)
            {
                var entry = new PendingSpawn { Count = 1 };
                SpawnInto(0, entry);
            }
        }
        if (dt <= 0) return;
        _age += dt;
        Env.Time += dt;
        foreach (var t in _spatial.Values) { (t.Previous, t.Current) = (t.Current, t.Previous); t.Current.Clear(); }
        _spatialPending.Clear();

        for (int s = 0; s < _slots.Length; s++)
        {
            var st = _slots[s];
            if (st is null || st.Count == 0) continue;
            var script = st.Def.Evolve;
            // A layer with neither an evolve script nor a renderer only relays events from its spawn
            // script; once that has run there is nothing left for its particles to do.
            if (script is null && st.Def.Renderers.Count == 0)
            {
                for (int p = 0; p < st.Count; p++) if (!st.Newborn[p]) st.Killed[p] = true;
                st.Compact();
                continue;
            }
            for (int p = 0; p < st.Count; p++)
            {
                if (st.Newborn[p]) continue;
                if (st.LifeRatioField >= 0 && st.InvLifeField >= 0)
                {
                    ref var lr = ref st.Fields[st.LifeRatioField][p];
                    float inv = st.Fields[st.InvLifeField][p].X;
                    if (inv != 0) lr.X += dt * inv;
                }
                if (script is not null) Run(script, st, p, dt, spawn: null, 0, 0);
                if (st.LifeRatioField >= 0 && !(st.Fields[st.LifeRatioField][p].X < 1f)) st.Killed[p] = true;
            }
            st.Compact();
            FlushPending();
        }
        FlushPending();
        foreach (var st in _slots) if (st is not null) for (int p = 0; p < st.Count; p++) st.Newborn[p] = false;
    }

    // ------------------------------------------------------------------ spawning

    private sealed class PendingSpawn
    {
        public int EventSlot = -1;
        public int Count;
        public int FirstIndex;
        /// <summary>The firing layer's payload names for this event, in append order.</summary>
        public string[] Names = [];
        /// <summary>Payload name -> index into <see cref="_elements"/>.</summary>
        public readonly Dictionary<string, int> Elements = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A named spatial layer: particles insert a value under a key each frame and other particles look
    /// the nearest key up (Lightning Shield's orbs publish their position; the arcs find their orb).
    /// Last frame's entries stay readable so a reader earlier in the layer order still finds them.
    /// </summary>
    private sealed class SpatialTable
    {
        public List<(Vector3 Key, PkValue[] Values)> Current = [];
        public List<(Vector3 Key, PkValue[] Values)> Previous = [];
    }

    private readonly Dictionary<string, SpatialTable> _spatial = new(StringComparer.Ordinal);
    private readonly List<PkValue[]> _spatialPending = [];

    private struct PayloadElement
    {
        public PkValue From, To;
        public int Flags;
        public int BaseIndex;
    }

    private void FlushPending()
    {
        // Spawn scripts do not kick events, so one pass drains what the evolve pass queued.
        while (_pending.Count > 0)
        {
            var batch = _pending.ToArray();
            _pending.Clear();
            foreach (var p in batch)
            {
                if ((uint)p.EventSlot >= (uint)_def.Events.Count) continue;
                foreach (int target in _def.Events[p.EventSlot].TargetSlots) SpawnInto(target, p);
            }
        }
        _elements.Clear();
    }

    private void SpawnInto(int slot, PendingSpawn batch)
    {
        if ((uint)slot >= (uint)_slots.Length || _slots[slot] is not { } st) return;
        int count = Math.Min(batch.Count, MaxParticlesPerSlot - st.Count);
        for (int k = 0; k < count; k++)
        {
            int p = st.Allocate();
            if (st.Def.Spawn is { } spawn) Run(spawn, st, p, 0, batch, k, count);
            if (st.Def.EvolveOnSpawn is { } eos) Run(eos, st, p, 0, batch, k, count);
        }
    }

    // ------------------------------------------------------------------ script plans

    private enum Bind : byte
    {
        Zero, Field, SceneDt, SceneTime, Pointer, Sampler, Event, Spatial,
        AttrEmission, AttrColor, AttrTeamColor, AttrOne,
    }

    private enum Fn : byte
    {
        Unknown, Rand, VRand, SamplePosition, SampleNormal, SampleCurve, SampleTurbulence,
        XformL2WF, XformL2WD, XformW2LF, XformW2LD, XformW2W,
        Rgb2Hsv, Hsv2Rgb, RadiansRotate, Rotate, AxisSide, AxisUp, AxisForward, OrientationMult,
        EffectPosition, EffectAxisUp, EffectAxisSide, EffectAxisForward, EffectAge, EffectIsRunning,
        SceneOrientation, ViewPosition, ViewDistance, SceneIntersect,
        Kill, Generate, EventStreamGenerate, EventStreamDuration, Trigger,
        InitPayload, AppendPayload, BuildPayloadElement, Kick,
        ExtractF, ExtractI, ExtractO, False, Zero,
        SpatialAllocate, SpatialAppend, SpatialInsert, SpatialClosest,
    }

    private sealed class ScriptPlan
    {
        public required (Bind Kind, int Index)[] Externals;
        public required Fn[] Calls;          // per instruction index (Unknown for non-calls)
        public required PkValue[] Reg1, Reg2, Reg3;
    }

    private readonly List<string> _spatialNames = [];

    private int AddSpatialName(string name)
    {
        _spatialNames.Add(name);
        return _spatialNames.Count - 1;
    }

    private ScriptPlan Plan(PkScript s, PkLayerState st)
    {
        if (_plans.TryGetValue(s, out var plan)) return plan;
        var layer = st.Def;
        var slot = _def.Slots[st.SlotIndex];
        var ext = new (Bind, int)[s.ExternalNames.Length];
        for (int i = 0; i < ext.Length; i++)
        {
            string n = s.ExternalNames[i];
            string t = s.ExternalTypes[i];
            int field = layer.Field(n);
            if (field >= 0) { ext[i] = (Bind.Field, field); continue; }
            if (n == "scene.dt") { ext[i] = (Bind.SceneDt, 0); continue; }
            if (n == "scene.time") { ext[i] = (Bind.SceneTime, 0); continue; }
            if (t.StartsWith("pCtx", StringComparison.Ordinal) || t is "RandCtx" or "SceneCtx") { ext[i] = (Bind.Pointer, 0); continue; }
            if (t.StartsWith("sampler", StringComparison.Ordinal))
            {
                int si = layer.Samplers.FindIndex(x => x.Name == n);
                ext[i] = si >= 0 ? (Bind.Sampler, si) : (Bind.Zero, 0);
                continue;
            }
            if (t == "SpatialLayerType" && n.StartsWith("__spatialLayer_", StringComparison.Ordinal)
                && int.TryParse(n.AsSpan("__spatialLayer_".Length), out int sl) && (uint)sl < (uint)layer.SpatialLayers.Length)
            {
                string tableName = layer.SpatialLayers[sl];
                if (!_spatial.ContainsKey(tableName)) _spatial[tableName] = new SpatialTable();
                ext[i] = (Bind.Spatial, _spatialNames.IndexOf(tableName) is int idx && idx >= 0 ? idx : AddSpatialName(tableName));
                continue;
            }
            if (t == "particleEvent")
            {
                int ev = slot.OutputEvents.FirstOrDefault(e => (uint)e < (uint)_def.Events.Count && _def.Events[e].Name == n, -1);
                ext[i] = (Bind.Event, ev);
                continue;
            }
            if (n.StartsWith("__a_", StringComparison.Ordinal))
            {
                ext[i] = n switch
                {
                    "__a_Game.EmissionRateMultiplier" => (Bind.AttrEmission, 0),
                    "__a_Game.ColorMultiplier" => (Bind.AttrColor, 0),
                    "__a_Game.TeamColor" => (Bind.AttrTeamColor, 0),
                    _ when n.Contains("Position", StringComparison.Ordinal) || n.Contains("Center", StringComparison.Ordinal) => (Bind.Zero, 0),
                    _ => (Bind.AttrOne, 0),
                };
                continue;
            }
            ext[i] = (Bind.Zero, 0);
            Unsupported.Add($"external '{n}' ({t})");
        }

        var calls = new Fn[s.Code.Length];
        for (int i = 0; i < s.Code.Length; i++)
        {
            ref readonly var ins = ref s.Code[i];
            if (ins.Op != PkOp.Call) continue;
            string name = ins.Slot < s.FunctionNames.Length ? s.FunctionNames[ins.Slot] : "";
            string objType = ins.This != 0xFFFF && ins.This < s.ExternalTypes.Length ? s.ExternalTypes[ins.This] : "";
            calls[i] = name switch
            {
                "rand" => Fn.Rand,
                "vrand" => Fn.VRand,
                "samplePosition" => Fn.SamplePosition,
                "sampleNormal" => Fn.SampleNormal,
                "sample" when objType.StartsWith("samplerCurve", StringComparison.Ordinal) => Fn.SampleCurve,
                "sample" when objType.StartsWith("samplerTurbulence", StringComparison.Ordinal) => Fn.SampleTurbulence,
                "xform_l2w_f_masked" => Fn.XformL2WF,
                "xform_l2w_d_masked" => Fn.XformL2WD,
                "xform_w2l_f_masked" => Fn.XformW2LF,
                "xform_w2l_d_masked" => Fn.XformW2LD,
                "xform_w2w_f_masked" or "xform_w2w_d_masked" => Fn.XformW2W,
                "rgb2hsv" => Fn.Rgb2Hsv,
                "hsv2rgb" => Fn.Hsv2Rgb,
                "radians.rotate" => Fn.RadiansRotate,
                "rotate" => Fn.Rotate,
                "orientation_axisSide" => Fn.AxisSide,
                "orientation_axisUp" => Fn.AxisUp,
                "orientation_axisForward" => Fn.AxisForward,
                "orientation_mult" => Fn.OrientationMult,
                "effect.position" => Fn.EffectPosition,
                "effect.axisUp" => Fn.EffectAxisUp,
                "effect.axisSide" => Fn.EffectAxisSide,
                "effect.axisForward" => Fn.EffectAxisForward,
                "effect.age" => Fn.EffectAge,
                "effect.isRunning" => Fn.EffectIsRunning,
                "scene.orientation_f_norm" => Fn.SceneOrientation,
                "view.position" => Fn.ViewPosition,
                "view.distance" => Fn.ViewDistance,
                "scene.intersect" => Fn.SceneIntersect,
                "self.kill" => Fn.Kill,
                "generate" when objType == "samplerEventStreamC" => Fn.EventStreamGenerate,
                "generate" => Fn.Generate,
                "duration" => Fn.EventStreamDuration,
                "trigger" => Fn.Trigger,
                "initPayload" when objType == "particleEvent" => Fn.InitPayload,
                "appendPayload" when objType == "particleEvent" => Fn.AppendPayload,
                "kick" when objType == "particleEvent" => Fn.Kick,
                "buildPayloadElement" => Fn.BuildPayloadElement,
                "extractPayloadElementF1" or "extractPayloadElementF2" or "extractPayloadElementF3" or "extractPayloadElementF4" => Fn.ExtractF,
                "extractPayloadElementI1" => Fn.ExtractI,
                "extractPayloadElementO" => Fn.ExtractO,
                "allocatePayload" when objType == "SpatialLayerType" => Fn.SpatialAllocate,
                "appendPayload" when objType == "SpatialLayerType" => Fn.SpatialAppend,
                "insert" when objType == "SpatialLayerType" => Fn.SpatialInsert,
                "closestF3" when objType == "SpatialLayerType" => Fn.SpatialClosest,
                "_pksi_War3.HasTeleported_SI" or "contains" => Fn.False,
                _ => Fn.Zero,
            };
            if (calls[i] == Fn.Zero) Unsupported.Add($"function '{(objType.Length > 0 ? objType + "." : "")}{name}'");
        }

        plan = new ScriptPlan
        {
            Externals = ext, Calls = calls,
            Reg1 = new PkValue[s.Reg1Count], Reg2 = new PkValue[s.Reg2Count], Reg3 = new PkValue[s.Reg3Count],
        };
        _plans[s] = plan;
        return plan;
    }

    // ------------------------------------------------------------------ interpreter

    private void Run(PkScript s, PkLayerState st, int particle, float dt, PendingSpawn? spawn, int spawnIndex, int spawnCount)
    {
        var plan = Plan(s, st);
        var code = s.Code;
        for (int pc = 0; pc < code.Length; pc++)
        {
            ref readonly var ins = ref code[pc];
            switch (ins.Op)
            {
                case PkOp.Load:
                {
                    var (kind, index) = ins.Slot < plan.Externals.Length ? plan.Externals[ins.Slot] : (Bind.Zero, 0);
                    PkValue v = kind switch
                    {
                        Bind.Field => st.Fields[index][particle],
                        Bind.SceneDt => new PkValue(dt),
                        Bind.SceneTime => new PkValue(Env.Time),
                        Bind.AttrEmission => new PkValue(Env.EmissionRateMultiplier),
                        Bind.AttrColor => PkValue.From(Env.ColorMultiplier),
                        Bind.AttrTeamColor => PkValue.From(Env.TeamColor),
                        Bind.AttrOne => new PkValue(1, 1, 1, 1),
                        _ => default,
                    };
                    Set(plan, ins.Dst, v);
                    break;
                }
                case PkOp.Store:
                {
                    var (kind, index) = ins.Slot < plan.Externals.Length ? plan.Externals[ins.Slot] : (Bind.Zero, 0);
                    if (kind == Bind.Field) st.Fields[index][particle] = Get(s, plan, ins.Dst);
                    break;
                }
                case PkOp.Binary:
                    Set(plan, ins.Dst, Binary(ins.Sub, Get(s, plan, ins.A), ins.A.Kind, Get(s, plan, ins.B), ins.Dst));
                    break;
                case PkOp.Unary:
                    Set(plan, ins.Dst, Unary(ins.Sub, Get(s, plan, ins.A), ins.A, ins.Dst));
                    break;
                case PkOp.Binary2:
                    Set(plan, ins.Dst, Binary2(ins.Sub, Get(s, plan, ins.A), Get(s, plan, ins.B), ins.A, ins.Dst));
                    break;
                case PkOp.Ternary:
                {
                    var a = Get(s, plan, ins.A); var b = Get(s, plan, ins.B); var t = Get(s, plan, ins.C);
                    var r = new PkValue();
                    int n = ins.Dst.Lanes;
                    for (int l = 0; l < n; l++)
                    {
                        float tl = ins.C.Lanes == 1 ? t.X : t[l];
                        r[l] = a[l] + (b[l] - a[l]) * tl;
                    }
                    Set(plan, ins.Dst, r);
                    break;
                }
                case PkOp.Select:
                {
                    var a = Get(s, plan, ins.A); var b = Get(s, plan, ins.B); var c = Get(s, plan, ins.C);
                    var r = new PkValue();
                    for (int l = 0; l < 4; l++)
                    {
                        bool cond = (ins.C.Lanes == 1 ? c.I0 : c.Int(l)) != 0;
                        r.SetInt(l, cond ? b.Int(l) : a.Int(l));
                    }
                    Set(plan, ins.Dst, r);
                    break;
                }
                case PkOp.Swizzle:
                {
                    var a = Get(s, plan, ins.A);
                    bool scalarSource = ins.A.Lanes == 1;
                    bool isInt = PkKind.IsInt(ins.Dst.Kind) || PkKind.IsBool(ins.Dst.Kind);
                    var r = new PkValue();
                    for (int l = 0; l < ins.Dst.Lanes; l++)
                    {
                        int code2 = PkScript.LaneCode(ins.Swizzle, l);
                        if (code2 <= 3) r.SetInt(l, a.Int(scalarSource ? 0 : code2));
                        else if (isInt) r.SetInt(l, code2 == 5 ? 1 : 0);
                        else r[l] = code2 == 5 ? 1f : 0f;
                    }
                    Set(plan, ins.Dst, r);
                    break;
                }
                case PkOp.Vector:
                {
                    var r = new PkValue();
                    int lane = 0;
                    foreach (var c in ins.Components!)
                    {
                        var v = Get(s, plan, c);
                        for (int l = 0; l < c.Lanes && lane < 4; l++) r.SetInt(lane++, v.Int(l));
                    }
                    Set(plan, ins.Dst, r);
                    break;
                }
                case PkOp.CastA:
                    Set(plan, ins.Dst, Get(s, plan, ins.A));
                    break;
                case PkOp.CastB:
                    Set(plan, ins.Dst, Cast(Get(s, plan, ins.A), ins.A.Kind, ins.Dst.Kind));
                    break;
                case PkOp.Call:
                    Call(s, plan, plan.Calls[pc], ins, st, particle, dt, spawn, spawnIndex, spawnCount);
                    break;
            }
        }
    }

    private static PkValue Get(PkScript s, ScriptPlan plan, PkOperand o) => o.Space switch
    {
        PkSpace.Const => o.Index < s.Consts.Length ? s.Consts[o.Index] : default,
        PkSpace.Reg1 => plan.Reg1[o.Index],
        PkSpace.Reg2 => plan.Reg2[o.Index],
        PkSpace.Reg3 => plan.Reg3[o.Index],
        _ => default,
    };

    private static void Set(ScriptPlan plan, PkOperand o, PkValue v)
    {
        switch (o.Space)
        {
            case PkSpace.Reg1: plan.Reg1[o.Index] = v; break;
            case PkSpace.Reg2: plan.Reg2[o.Index] = v; break;
            case PkSpace.Reg3: plan.Reg3[o.Index] = v; break;
        }
    }

    private static PkValue Binary(byte sub, PkValue a, byte aKind, PkValue b, PkOperand dst)
    {
        var r = new PkValue();
        int n = Math.Max(dst.Lanes, PkKind.Lanes(aKind));
        bool ints = PkKind.IsInt(aKind) || PkKind.IsBool(aKind);
        for (int l = 0; l < n && l < 4; l++)
        {
            if (ints)
            {
                int x = a.Int(l), y = b.Int(l);
                int v = sub switch
                {
                    0x00 => x + y, 0x01 => x - y, 0x02 => x * y, 0x03 => y != 0 ? x / y : 0, 0x04 => y != 0 ? x % y : 0,
                    0x05 => -x, 0x08 => x & y, 0x09 => x | y, 0x0B => ~x,
                    0x0C => x < y ? -1 : 0, 0x0D => x <= y ? -1 : 0, 0x0E => x > y ? -1 : 0, 0x0F => x >= y ? -1 : 0,
                    0x10 => x == y ? -1 : 0, 0x11 => x != y ? -1 : 0,
                    _ => 0,
                };
                r.SetInt(l, v);
            }
            else
            {
                float x = a[l], y = b[l];
                switch (sub)
                {
                    case 0x00: r[l] = x + y; break;
                    case 0x01: r[l] = x - y; break;
                    case 0x02: r[l] = x * y; break;
                    case 0x03: r[l] = x / y; break;
                    case 0x04: r[l] = y != 0 ? x % y : 0; break;
                    case 0x05: r[l] = -x; break;
                    case 0x0C: r.SetInt(l, x < y ? -1 : 0); break;
                    case 0x0D: r.SetInt(l, x <= y ? -1 : 0); break;
                    case 0x0E: r.SetInt(l, x > y ? -1 : 0); break;
                    case 0x0F: r.SetInt(l, x >= y ? -1 : 0); break;
                    case 0x10: r.SetInt(l, x == y ? -1 : 0); break;
                    case 0x11: r.SetInt(l, x != y ? -1 : 0); break;
                    default: r[l] = 0; break;
                }
            }
        }
        return r;
    }

    private static PkValue Unary(byte sub, PkValue a, PkOperand src, PkOperand dst)
    {
        var r = new PkValue();
        switch (sub)
        {
            case 0x07:                                            // angle -> (sin, cos)
                r.X = MathF.Sin(a.X); r.Y = MathF.Cos(a.X);
                return r;
            case 0x24:                                            // float3 normalize
            {
                var v = a.Xyz;
                float len = v.Length();
                return len > 1e-12f ? PkValue.From(v / len) : default;
            }
            case 0x31:                                            // any lane set
            {
                bool any = false;
                for (int l = 0; l < src.Lanes; l++) any |= a.Int(l) != 0;
                r.I0 = any ? -1 : 0;
                return r;
            }
            case 0x33:                                            // lane is non-zero
                for (int l = 0; l < dst.Lanes; l++) r.SetInt(l, a[l] != 0 && float.IsFinite(a[l]) ? -1 : 0);
                return r;
        }
        for (int l = 0; l < Math.Max(dst.Lanes, src.Lanes) && l < 4; l++)
        {
            float x = a[l];
            r[l] = sub switch
            {
                0x00 => MathF.Sqrt(MathF.Max(x, 0)),
                0x01 => x > 0 ? 1f / MathF.Sqrt(x) : 0f,
                0x0D or 0x2C => MathF.Exp(x),
                0x11 => 1f / x,
                0x12 => MathF.Abs(x),
                0x13 => x > 0 ? 1 : x < 0 ? -1 : 0,
                // Both feed curve samples with an ever-growing age (effect.age straight into a
                // colour curve; the hero glow's 3-second pulse), which only makes sense as a wrap.
                0x16 or 0x17 => x - MathF.Floor(x),
                0x18 => Math.Clamp(x, 0f, 1f),
                _ => x,
            };
        }
        return r;
    }

    private static PkValue Binary2(byte sub, PkValue a, PkValue b, PkOperand aOp, PkOperand dst)
    {
        switch (sub)
        {
            case 0x1D:
            {
                float d = 0;
                for (int l = 0; l < aOp.Lanes; l++) d += a[l] * b[l];
                return new PkValue(d);
            }
            case 0x1E:
                return PkValue.From(Vector3.Cross(a.Xyz, b.Xyz));
        }
        var r = new PkValue();
        bool ints = PkKind.IsInt(aOp.Kind);
        for (int l = 0; l < dst.Lanes; l++)
        {
            if (ints)
            {
                int x = a.Int(l), y = b.Int(l);
                r.SetInt(l, sub == 0x1B ? Math.Min(x, y) : sub == 0x1C ? Math.Max(x, y) : x);
            }
            else
            {
                float x = a[l], y = b[l];
                r[l] = sub switch
                {
                    0x19 => MathF.Pow(MathF.Max(x, 0), y),
                    0x1B => MathF.Min(x, y),
                    0x1C => MathF.Max(x, y),
                    _ => x,
                };
            }
        }
        return r;
    }

    private static PkValue Cast(PkValue a, byte from, byte to)
    {
        var r = new PkValue();
        int n = PkKind.Lanes(to);
        for (int l = 0; l < n; l++)
        {
            if (PkKind.IsBool(from))
            {
                bool v = (PkKind.Lanes(from) == 1 ? a.I0 : a.Int(l)) != 0;
                if (PkKind.IsInt(to)) r.SetInt(l, v ? 1 : 0); else if (PkKind.IsBool(to)) r.SetInt(l, v ? -1 : 0); else r[l] = v ? 1 : 0;
            }
            else if (PkKind.IsInt(from))
            {
                if (PkKind.IsInt(to)) r.SetInt(l, a.Int(l)); else if (PkKind.IsBool(to)) r.SetInt(l, a.Int(l) != 0 ? -1 : 0); else r[l] = a.Int(l);
            }
            else
            {
                if (PkKind.IsInt(to)) r.SetInt(l, (int)a[l]); else if (PkKind.IsBool(to)) r.SetInt(l, a[l] != 0 ? -1 : 0); else r[l] = a[l];
            }
        }
        return r;
    }

    // ------------------------------------------------------------------ built-in functions

    private void Call(PkScript s, ScriptPlan plan, Fn fn, in PkInstr instr, PkLayerState st, int particle, float dt,
                      PendingSpawn? spawn, int spawnIndex, int spawnCount)
    {
        var ins = instr;
        PkValue Arg(int k) => k < ins.ArgCount ? Get(s, plan, s.Args[ins.ArgStart + k]) : default;
        PkSampler? Obj() => ins.This < plan.Externals.Length && plan.Externals[ins.This] is (Bind.Sampler, int si) && si < st.Def.Samplers.Count
            ? st.Def.Samplers[si] : null;
        int EventOf() => ins.This < plan.Externals.Length && plan.Externals[ins.This] is (Bind.Event, int ev) ? ev : -1;
        SpatialTable? SpatialOf() => ins.This < plan.Externals.Length && plan.Externals[ins.This] is (Bind.Spatial, int sp)
                                     && (uint)sp < (uint)_spatialNames.Count ? _spatial[_spatialNames[sp]] : null;

        var r = new PkValue();
        var world = SpawnFrame(st, spawn, spawnIndex, spawnCount) ?? Env.LocalToWorld;
        switch (fn)
        {
            case Fn.Rand:
            {
                var lo = Arg(0); var hi = Arg(1);
                int n = Math.Max(1, ins.Dst.Lanes);
                if (PkKind.IsInt(ins.Dst.Kind))
                    for (int l = 0; l < n; l++) r.SetInt(l, lo.Int(l) + (int)((hi.Int(l) - lo.Int(l)) * _rng.NextDouble()));
                else
                    for (int l = 0; l < n; l++) r[l] = lo[l] + (hi[l] - lo[l]) * (float)_rng.NextDouble();
                break;
            }
            case Fn.VRand:
            {
                var u = PkShape.RandomUnit(_rng);
                if (ins.ArgCount >= 3)
                {
                    float a = Arg(0).X, b = Arg(1).X;
                    u *= a + (b - a) * MathF.Cbrt((float)_rng.NextDouble());
                }
                r = PkValue.From(u);
                break;
            }
            case Fn.SamplePosition:
                r = Obj()?.Shape is { } shape ? PkValue.From(shape.SamplePosition(_rng)) : default;
                break;
            case Fn.SampleNormal:
            {
                var p = Obj()?.Shape?.SamplePosition(_rng) ?? Vector3.UnitZ;
                r = PkValue.From(p.LengthSquared() > 1e-12f ? Vector3.Normalize(p) : Vector3.UnitZ);
                break;
            }
            case Fn.SampleCurve:
                r = Obj()?.Curve is { } curve ? curve.Sample(Arg(0).X) : default;
                break;
            case Fn.SampleTurbulence:
            {
                var smp = Obj();
                float wavelength = smp is { Wavelength: > 1e-4f } ? smp.Wavelength : 1f, strength = smp?.Strength ?? 1f;
                var p = Arg(0).Xyz / wavelength;
                float t = Env.Time;
                r = new PkValue(strength * MathF.Sin(p.Y * 1.7f + t * 0.9f) * MathF.Cos(p.Z * 1.3f),
                                strength * MathF.Sin(p.Z * 1.9f + t * 0.7f) * MathF.Cos(p.X * 1.1f),
                                strength * MathF.Sin(p.X * 1.5f + t * 0.8f) * MathF.Cos(p.Y * 1.6f));
                break;
            }
            case Fn.XformL2WF: r = PkValue.From(Vector3.Transform(Arg(0).Xyz, world)); break;
            case Fn.XformL2WD: r = PkValue.From(Vector3.TransformNormal(Arg(0).Xyz, world)); break;
            case Fn.XformW2LF: r = PkValue.From(Matrix4x4.Invert(world, out var inv) ? Vector3.Transform(Arg(0).Xyz, inv) : Arg(0).Xyz); break;
            case Fn.XformW2LD: r = PkValue.From(Matrix4x4.Invert(world, out var inv2) ? Vector3.TransformNormal(Arg(0).Xyz, inv2) : Arg(0).Xyz); break;
            case Fn.XformW2W: r = Arg(0); break;
            case Fn.Rgb2Hsv: r = Rgb2Hsv(Arg(0)); break;
            case Fn.Hsv2Rgb: r = Hsv2Rgb(Arg(0)); break;
            case Fn.RadiansRotate:
            {
                var axis = Arg(1).Xyz;
                r = axis.LengthSquared() > 1e-12f
                    ? PkValue.From(Vector3.Transform(Arg(0).Xyz, Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), Arg(2).X)))
                    : Arg(0);
                break;
            }
            case Fn.Rotate:
            {
                var q = Arg(1);
                r = PkValue.From(Vector3.Transform(Arg(0).Xyz, new Quaternion(q.X, q.Y, q.Z, q.W)));
                break;
            }
            case Fn.AxisSide: case Fn.AxisUp: case Fn.AxisForward:
            {
                var q = Arg(0);
                var axis = fn == Fn.AxisSide ? Vector3.UnitX : fn == Fn.AxisForward ? Vector3.UnitY : Vector3.UnitZ;
                var quat = new Quaternion(q.X, q.Y, q.Z, q.W);
                r = PkValue.From(quat.LengthSquared() > 1e-12f ? Vector3.Transform(axis, Quaternion.Normalize(quat)) : axis);
                break;
            }
            case Fn.OrientationMult:
            {
                var a = Arg(0); var b = Arg(1);
                var q = new Quaternion(a.X, a.Y, a.Z, a.W) * new Quaternion(b.X, b.Y, b.Z, b.W);
                r = new PkValue(q.X, q.Y, q.Z, q.W);
                break;
            }
            case Fn.EffectPosition: r = PkValue.From(world.Translation); break;
            case Fn.EffectAxisUp: r = PkValue.From(SafeNormal(Vector3.TransformNormal(Vector3.UnitZ, world))); break;
            case Fn.EffectAxisSide: r = PkValue.From(SafeNormal(Vector3.TransformNormal(Vector3.UnitX, world))); break;
            case Fn.EffectAxisForward: r = PkValue.From(SafeNormal(Vector3.TransformNormal(Vector3.UnitY, world))); break;
            case Fn.EffectAge: r = new PkValue(_age); break;
            case Fn.EffectIsRunning: r.I0 = Env.Running ? -1 : 0; break;
            case Fn.SceneOrientation:
                r = ins.Dst.Lanes >= 4 ? new PkValue(0, 0, 0, 1) : new PkValue(0, 0, 1);
                break;
            case Fn.ViewPosition: r = PkValue.From(Env.CameraPosition); break;
            case Fn.ViewDistance: r = new PkValue(Vector3.Distance(Env.CameraPosition, Arg(0).Xyz)); break;
            case Fn.SceneIntersect: r = default; break;
            case Fn.Kill:
                if (Arg(0).I0 != 0) st.Killed[particle] = true;
                return;
            case Fn.Generate:
            {
                // generate(state, amount, step, ...): one particle per `step` of accumulated amount.
                // Spawners pass dt x rate with a step of 1; Lightning Shield's orbs pass dt with a
                // step of 0.025 (one arc every 25 ms). The state keeps the fraction of a step left
                // over — seeded at 0.99999 so the first particle comes on the first frame.
                var state = Arg(0);
                float step = Arg(2).X;
                if (!(step > 1e-6f)) step = 1f;
                float acc = state.X + Arg(1).X / step;
                if (!float.IsFinite(acc)) acc = 0;
                float count = MathF.Floor(MathF.Max(acc, 0));
                r = new PkValue(acc - count, state.Y + count, count);
                break;
            }
            case Fn.EventStreamGenerate:
            {
                var state = Arg(0);
                float age = Arg(2).X;
                var times = Obj()?.EventTimes ?? [];
                int done = (int)MathF.Max(0, state.X);
                int fire = 0;
                while (done + fire < times.Length && times[done + fire] <= age + 1e-6f) fire++;
                r = new PkValue(done + fire, 0, fire);
                break;
            }
            case Fn.EventStreamDuration:
            {
                var times = Obj()?.EventTimes ?? [];
                r = new PkValue(times.Length == 0 ? 0 : times.Max());
                break;
            }
            case Fn.Trigger:
                r = new PkValue(0, 0, Arg(0).I0 != 0 ? MathF.Max(1, Arg(1).X) : 0);
                break;
            case Fn.InitPayload:
            {
                var gen = Arg(1);
                int count = (int)MathF.Max(0, MathF.Min(gen.Z, MaxParticlesPerSlot));
                int ev = EventOf();
                string evName = (uint)ev < (uint)_def.Events.Count ? _def.Events[ev].Name : "";
                _pending.Add(new PendingSpawn
                {
                    EventSlot = ev, Count = count, FirstIndex = (int)MathF.Max(0, gen.Y - gen.Z),
                    Names = st.Def.OutputPayloads.TryGetValue(evName, out var names) ? names : [],
                });
                r.I0 = _pending.Count;                               // 1-based handle
                break;
            }
            case Fn.BuildPayloadElement:
            {
                var gen = Arg(0);
                _elements.Add(new PayloadElement { From = Arg(1), To = Arg(2), Flags = Arg(3).I0, BaseIndex = (int)MathF.Max(0, gen.Y - gen.Z) });
                r.I0 = _elements.Count;
                break;
            }
            case Fn.AppendPayload:
            {
                int h = Arg(0).I0 - 1, idx = Arg(1).I0, el = Arg(2).I0 - 1;
                if ((uint)h < (uint)_pending.Count)
                {
                    var p = _pending[h];
                    string name = (uint)idx < (uint)p.Names.Length ? p.Names[idx] : $"#{idx}";
                    p.Elements[name] = el;
                }
                r = Arg(0);
                break;
            }
            case Fn.Kick:
            {
                int h = Arg(0).I0 - 1;
                if ((uint)h < (uint)_pending.Count && _pending[h].Count <= 0) _pending[h].EventSlot = -1;
                return;
            }
            case Fn.ExtractF: case Fn.ExtractI: case Fn.ExtractO:
            {
                int idx = Arg(0).I0;
                string name = (uint)idx < (uint)st.Def.InputPayloads.Length ? st.Def.InputPayloads[idx] : $"#{idx}";
                if (spawn is not null && spawn.Elements.TryGetValue(name, out int el) && (uint)el < (uint)_elements.Count)
                {
                    var e = _elements[el];
                    if (fn == Fn.ExtractI) r.I0 = e.BaseIndex + spawnIndex;
                    else
                    {
                        float t = spawnCount > 1 ? (spawnIndex + 1f) / spawnCount : 1f;
                        for (int l = 0; l < 4; l++) r[l] = e.From[l] + (e.To[l] - e.From[l]) * t;
                    }
                }
                else if (fn == Fn.ExtractO) r = new PkValue(0, 0, 0, 1);
                break;
            }
            case Fn.False: r.I0 = 0; break;
            case Fn.SpatialAllocate:
                _spatialPending.Add(new PkValue[4]);
                r.I0 = _spatialPending.Count;
                break;
            case Fn.SpatialAppend:
            {
                // appendPayload(handle, value, index)
                int h = Arg(0).I0 - 1, idx = Arg(2).I0;
                if ((uint)h < (uint)_spatialPending.Count && (uint)idx < 4u) _spatialPending[h][idx] = Arg(1);
                r = Arg(0);
                break;
            }
            case Fn.SpatialInsert:
            {
                int h = Arg(0).I0 - 1;
                if ((uint)h < (uint)_spatialPending.Count && SpatialOf() is { } table)
                    table.Current.Add((Arg(1).Xyz, _spatialPending[h]));
                return;
            }
            case Fn.SpatialClosest:
            {
                // closestF3(key, radius, payloadIndex, ...): the value published under the nearest key.
                var key = Arg(0).Xyz;
                int idx = Math.Clamp(Arg(2).I0, 0, 3);
                if (SpatialOf() is { } table)
                {
                    float best = float.MaxValue;
                    foreach (var list in new[] { table.Current, table.Previous })
                    {
                        foreach (var (k, values) in list)
                        {
                            float d = Vector3.DistanceSquared(k, key);
                            if (d < best) { best = d; r = values[idx]; }
                        }
                        if (best < float.MaxValue) break;
                    }
                }
                break;
            }
            default: r = default; break;
        }
        if (!ins.Dst.IsNone) Set(plan, ins.Dst, r);
    }

    private static Vector3 SafeNormal(Vector3 v) => v.LengthSquared() > 1e-12f ? Vector3.Normalize(v) : Vector3.UnitZ;

    /// <summary>
    /// While a spawn script runs for an event that carries a <c>Position</c> payload (and optionally
    /// an <c>Orientation</c>), local space is the parent particle's frame: Lightning Shield's sparks
    /// sample their shape around the orb that fired them. Everything else is in the effect's frame.
    /// </summary>
    private Matrix4x4? SpawnFrame(PkLayerState st, PendingSpawn? spawn, int spawnIndex, int spawnCount)
    {
        if (spawn is null) return null;
        var names = st.Def.InputPayloads;
        if (!spawn.Elements.TryGetValue("Position", out int pe) || (uint)pe >= (uint)_elements.Count) return null;
        float t = spawnCount > 1 ? (spawnIndex + 1f) / spawnCount : 1f;
        var e = _elements[pe];
        var pos = Vector3.Lerp(e.From.Xyz, e.To.Xyz, t);
        var rot = Quaternion.Identity;
        if (spawn.Elements.TryGetValue("Orientation", out int oe) && (uint)oe < (uint)_elements.Count)
        {
            var o = _elements[oe];
            var q = new Quaternion(o.To.X, o.To.Y, o.To.Z, o.To.W);
            if (q.LengthSquared() > 1e-8f) rot = Quaternion.Normalize(q);
        }
        _ = names;
        return Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
    }

    private static PkValue Rgb2Hsv(PkValue c)
    {
        float r = c.X, g = c.Y, b = c.Z;
        float max = MathF.Max(r, MathF.Max(g, b)), min = MathF.Min(r, MathF.Min(g, b));
        float d = max - min, h = 0;
        if (d > 1e-9f)
        {
            if (max == r) h = (g - b) / d % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
            if (h < 0) h += 1;
        }
        float s = max > 1e-9f ? d / max : 0;
        return new PkValue(h, s, max, c.W);
    }

    private static PkValue Hsv2Rgb(PkValue c)
    {
        float h = c.X - MathF.Floor(c.X), s = Math.Clamp(c.Y, 0, 1), v = c.Z;
        float f = h * 6, k = MathF.Floor(f), fr = f - k;
        float p = v * (1 - s), q = v * (1 - s * fr), t = v * (1 - s * (1 - fr));
        var (r, g, b) = ((int)k % 6) switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        return new PkValue(r, g, b, c.W);
    }
}
