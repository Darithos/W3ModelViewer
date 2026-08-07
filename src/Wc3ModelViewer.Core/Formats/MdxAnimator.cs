using System.Numerics;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// Evaluates a model's animation at a point in time: node world matrices, skinned vertices, and
/// geoset visibility.
/// </summary>
/// <remarks>
/// Warcraft III bones have no rest rotation or scale — a node's rest pose is just its pivot point,
/// and every key is an offset applied <i>about that pivot</i>:
/// <c>local = T(-pivot) · S · R · T(pivot + translation)</c> (row-vector order). With no keys this
/// collapses to identity, which is why the stored vertices are already in model space and world
/// matrices at rest are identity — no inverse-bind matrices exist anywhere in the format.
/// Getting this wrong (treating keys as absolute local transforms) is the classic cause of exploded
/// meshes in WC3 converters.
/// <para>
/// Keys live on one global millisecond timeline; a sequence samples only keys inside its
/// [start,end] window, clamping to the nearest inside key. Tracks with a global-sequence id ignore
/// the window entirely and loop on their own <c>GLBS</c> period.
/// </para>
/// </remarks>
public sealed class MdxAnimator
{
    private readonly MdxModel _model;
    private readonly int[] _parent;            // Nodes index -> Nodes index of parent, -1 = root
    private readonly Matrix4x4[] _world;       // per Nodes index, written by Evaluate
    private readonly int[] _boneChunkToNode;   // SKIN bone index (BONE-chunk order) -> Nodes index
    private readonly Dictionary<int, int> _nodeIndexByObjectId = [];

    // Scratch: blended matrices for classic matrix groups, sized per geoset on demand.
    private readonly Dictionary<int, Matrix4x4[]> _groupScratch = [];

    public MdxAnimator(MdxModel model)
    {
        _model = model;
        _world = new Matrix4x4[model.Nodes.Count];
        Array.Fill(_world, Matrix4x4.Identity);

        for (int i = 0; i < model.Nodes.Count; i++)
            _nodeIndexByObjectId.TryAdd(model.Nodes[i].ObjectId, i);

        _parent = new int[model.Nodes.Count];
        for (int i = 0; i < model.Nodes.Count; i++)
            _parent[i] = model.Nodes[i].ParentId >= 0 && _nodeIndexByObjectId.TryGetValue(model.Nodes[i].ParentId, out int pi)
                ? pi : -1;

        // Reforged SKIN indices address bones by their position in the BONE chunk, not by objectId.
        // Node insertion preserves chunk order, so the bones' order here is the SKIN index space.
        _boneChunkToNode = Enumerable.Range(0, model.Nodes.Count)
                                     .Where(i => model.Nodes[i].Kind == MdxNodeKind.Bone)
                                     .ToArray();
    }

    public IReadOnlyList<Matrix4x4> WorldMatrices => _world;

    /// <summary>World matrix of one node by its index in <see cref="MdxModel.Nodes"/>.</summary>
    public Matrix4x4 World(int nodeIndex) => _world[nodeIndex];

    /// <summary>
    /// Computes every node's world matrix for a time inside a sequence.
    /// </summary>
    /// <param name="seq">Sequence whose window keys are sampled from; null = rest pose.</param>
    /// <param name="timeMs">Absolute time on the model timeline (already inside the window).</param>
    /// <param name="wallMs">Monotonic clock for global-sequence tracks; defaults to <paramref name="timeMs"/>.</param>
    public void Evaluate(MdxSequence? seq, int timeMs, long? wallMs = null)
    {
        long wall = wallMs ?? timeMs;

        // Parents precede children in file order for every Blizzard model, but user maps break the
        // rule, so resolve out of order by walking up on demand.
        Span<bool> done = _world.Length <= 512 ? stackalloc bool[_world.Length] : new bool[_world.Length];
        done.Clear();
        for (int i = 0; i < _world.Length; i++)
            Resolve(i, seq, timeMs, wall, done);
    }

    private void Resolve(int i, MdxSequence? seq, int timeMs, long wall, Span<bool> done)
    {
        if (done[i]) return;
        done[i] = true;                        // set before recursing: breaks parent cycles

        var node = _model.Nodes[i];
        int p = _parent[i];
        if (p >= 0) Resolve(p, seq, timeMs, wall, done);

        var local = LocalMatrix(node, seq, timeMs, wall);
        _world[i] = p >= 0 ? local * _world[p] : local;
    }

    private Matrix4x4 LocalMatrix(MdxNode node, MdxSequence? seq, int timeMs, long wall)
    {
        var tr = Sample(node.Translation, seq, timeMs, wall, Vector3.Zero, Vector3.Lerp, Hermite, Bezier);
        var q = SampleQuat(node.Rotation, seq, timeMs, wall);
        var s = Sample(node.Scale, seq, timeMs, wall, Vector3.One, Vector3.Lerp, Hermite, Bezier);

        bool hasTr = tr != Vector3.Zero;
        bool hasRot = !q.IsIdentity;
        bool hasScale = s != Vector3.One;
        if (!hasTr && !hasRot && !hasScale) return Matrix4x4.Identity;

        // Row-vector composition of T(pivot + tr) · R · S · T(-pivot) (column form), i.e. the key
        // scales and rotates about the pivot, then offsets by the translation key.
        var m = Matrix4x4.CreateTranslation(-node.Pivot);
        if (hasScale) m *= Matrix4x4.CreateScale(s);
        if (hasRot) m *= Matrix4x4.CreateFromQuaternion(q);
        m *= Matrix4x4.CreateTranslation(node.Pivot + tr);
        return m;
    }

    // ---------------------------------------------------------------- track sampling

    private delegate T Blend<T>(T a, T b, float t);
    private delegate T Curve<T>(T a, T outTanA, T inTanB, T b, float t);

    /// <summary>Finds the key window and interpolates. Shared by every value type.</summary>
    /// <summary>
    /// Samples a float track the same way node transforms are sampled, honouring interpolation mode
    /// and global sequences. Exposed for the emitter parameters that ride their own tracks
    /// (emission rate, speed, ribbon heights) — see <see cref="MdxEffectSimulator"/>.
    /// </summary>
    public float SampleFloat(MdxTrack<float>? track, MdxSequence? seq, int timeMs, float rest, long? wallMs = null)
        => Sample(track, seq, timeMs, wallMs ?? timeMs, rest, float.Lerp, HermiteF, BezierF);

    /// <summary>Samples a vector track — emitter and ribbon colour tracks.</summary>
    public Vector3 SampleVector(MdxTrack<Vector3>? track, MdxSequence? seq, int timeMs, Vector3 rest, long? wallMs = null)
        => Sample(track, seq, timeMs, wallMs ?? timeMs, rest, Vector3.Lerp, Hermite, Bezier);

    private T Sample<T>(MdxTrack<T>? track, MdxSequence? seq, int timeMs, long wall,
                        T rest, Blend<T> lerp, Curve<T> hermite, Curve<T> bezier)
    {
        if (track is null || track.Count == 0) return rest;

        int lo, hi, t;
        if (track.GlobalSequenceId >= 0 && track.GlobalSequenceId < _model.GlobalSequences.Count)
        {
            uint period = _model.GlobalSequences[track.GlobalSequenceId];
            t = period > 0 ? (int)(wall % period) : 0;
            lo = 0;
            hi = track.Count - 1;
        }
        else
        {
            if (seq is null) return rest;
            t = timeMs;
            (lo, hi) = WindowOf(track.Times, seq.IntervalStart, seq.IntervalEnd);
            if (lo > hi) return rest;          // no keys inside this sequence
        }

        var times = track.Times;
        if (t <= times[lo]) return track.Values[lo];
        if (t >= times[hi]) return track.Values[hi];

        int k = lo;
        while (k < hi && times[k + 1] <= t) k++;
        int span = times[k + 1] - times[k];
        float f = span > 0 ? (t - times[k]) / (float)span : 0;

        return track.Interpolation switch
        {
            MdxInterpolation.None => track.Values[k],
            MdxInterpolation.Linear => lerp(track.Values[k], track.Values[k + 1], f),
            MdxInterpolation.Hermite => hermite(track.Values[k], track.OutTangents![k], track.InTangents![k + 1], track.Values[k + 1], f),
            MdxInterpolation.Bezier => bezier(track.Values[k], track.OutTangents![k], track.InTangents![k + 1], track.Values[k + 1], f),
            _ => track.Values[k],
        };
    }

    private Quaternion SampleQuat(MdxTrack<Quaternion>? track, MdxSequence? seq, int timeMs, long wall)
        => Sample(track, seq, timeMs, wall, Quaternion.Identity,
                  (a, b, t) => Quaternion.Slerp(a, b, t),
                  Squad, Squad);

    /// <summary>First and last key indices whose time lies inside [start, end]; (1,0) when none.</summary>
    private static (int Lo, int Hi) WindowOf(int[] times, int start, int end)
    {
        int lo = LowerBound(times, start);
        int hi = UpperBound(times, end) - 1;
        return (lo, hi);
    }

    private static int LowerBound(int[] a, int v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] < v) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static int UpperBound(int[] a, int v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] <= v) lo = mid + 1; else hi = mid; }
        return lo;
    }

    private static Vector3 Hermite(Vector3 a, Vector3 outA, Vector3 inB, Vector3 b, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return a * (2 * t3 - 3 * t2 + 1) + b * (-2 * t3 + 3 * t2) + outA * (t3 - 2 * t2 + t) + inB * (t3 - t2);
    }

    private static Vector3 Bezier(Vector3 a, Vector3 outA, Vector3 inB, Vector3 b, float t)
    {
        float u = 1 - t;
        return a * (u * u * u) + outA * (3 * u * u * t) + inB * (3 * u * t * t) + b * (t * t * t);
    }

    /// <summary>Spherical cubic blend — how the game interpolates hermite/bezier rotation keys.</summary>
    private static Quaternion Squad(Quaternion a, Quaternion outA, Quaternion inB, Quaternion b, float t)
        => Quaternion.Slerp(Quaternion.Slerp(a, b, t), Quaternion.Slerp(outA, inB, t), 2 * t * (1 - t));

    // ---------------------------------------------------------------- baked-export support

    /// <summary>Index into <see cref="MdxModel.Nodes"/> of a node's parent, or -1.</summary>
    public int ParentIndex(int nodeIndex) => _parent[nodeIndex];

    /// <summary>
    /// Extracts one node's local TRS in the exported-skeleton convention from the matrices of the
    /// last <see cref="Evaluate"/>: rest world sits at the pivot, so
    /// <c>L = T(pivot_c) · W_c · W_p⁻¹ · T(-pivot_p)</c>. This is what m3 and glTF joints store —
    /// at rest it collapses to translation = pivot − parentPivot, identity rotation, unit scale.
    /// </summary>
    public (Vector3 Loc, Quaternion Rot, Vector3 Scale) LocalTrs(int nodeIndex)
    {
        var pivot = _model.Nodes[nodeIndex].Pivot;
        int p = _parent[nodeIndex];
        var parentPivot = p >= 0 ? _model.Nodes[p].Pivot : Vector3.Zero;

        Matrix4x4 parentInv = Matrix4x4.Identity;
        if (p >= 0) Matrix4x4.Invert(_world[p], out parentInv);

        var local = Matrix4x4.CreateTranslation(pivot) * _world[nodeIndex] * parentInv
                  * Matrix4x4.CreateTranslation(-parentPivot);
        if (!Matrix4x4.Decompose(local, out var scale, out var rot, out var loc))
        {
            // Degenerate (zero-scale key): keep translation, neutral rotation.
            scale = new Vector3(1e-5f);
            rot = Quaternion.Identity;
            loc = local.Translation;
        }
        return (loc, rot, scale);
    }

    // ---------------------------------------------------------------- skinning

    /// <summary>
    /// Skins one geoset's positions (and optionally normals) using the matrices from the last
    /// <see cref="Evaluate"/>. Output arrays must be at least <c>VertexCount</c> long.
    /// </summary>
    public void SkinGeoset(MdxGeoset g, Vector3[] outPositions, Vector3[]? outNormals = null)
    {
        if (g.HasSkin) SkinHd(g, outPositions, outNormals);
        else SkinClassic(g, outPositions, outNormals);
    }

    /// <summary>Reforged: four weighted bones per vertex, indices in BONE-chunk order.</summary>
    private void SkinHd(MdxGeoset g, Vector3[] outPos, Vector3[]? outNrm)
    {
        int n = g.VertexCount;
        for (int v = 0; v < n; v++)
        {
            var pos = Vector3.Zero;
            var nrm = Vector3.Zero;
            float total = 0;
            for (int k = 0; k < 4; k++)
            {
                byte w8 = g.SkinBoneWeights[v * 4 + k];
                if (w8 == 0) continue;
                int bi = g.SkinBoneIndices[v * 4 + k];
                if (bi >= _boneChunkToNode.Length) continue;

                int nodeIndex = _boneChunkToNode[bi];
                float w = w8 / 255f;
                ref readonly var m = ref _world[nodeIndex];
                pos += Vector3.Transform(g.Positions[v], m) * w;
                if (outNrm is not null) nrm += Vector3.TransformNormal(g.Normals[v], m) * w;
                total += w;
            }
            if (total < 1e-4f) { pos = g.Positions[v]; nrm = g.Normals.Length > v ? g.Normals[v] : Vector3.UnitZ; }
            outPos[v] = pos;
            if (outNrm is not null)
                outNrm[v] = nrm.LengthSquared() > 1e-10f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
        }
    }

    /// <summary>
    /// Classic: a vertex names a matrix group; the group's MATS entries are node objectIds sharing
    /// equal weight. Group matrices are blended once per group, not per vertex.
    /// </summary>
    private void SkinClassic(MdxGeoset g, Vector3[] outPos, Vector3[]? outNrm)
    {
        int groupCount = g.MatrixGroupSizes.Length;
        if (groupCount == 0 || g.VertexGroups.Length < g.VertexCount)
        {
            Array.Copy(g.Positions, outPos, g.VertexCount);
            if (outNrm is not null) Array.Copy(g.Normals, outNrm, Math.Min(g.Normals.Length, g.VertexCount));
            return;
        }

        if (!_groupScratch.TryGetValue(g.Index, out var groupMats) || groupMats.Length < groupCount)
            _groupScratch[g.Index] = groupMats = new Matrix4x4[groupCount];

        int at = 0;
        for (int grp = 0; grp < groupCount; grp++)
        {
            int size = g.MatrixGroupSizes[grp];
            var sum = default(Matrix4x4);
            int used = 0;
            for (int k = 0; k < size && at + k < g.MatrixIndices.Length; k++)
            {
                if (!_nodeIndexByObjectId.TryGetValue(g.MatrixIndices[at + k], out int ni)) continue;
                sum += _world[ni];
                used++;
            }
            groupMats[grp] = used > 0 ? sum * (1f / used) : Matrix4x4.Identity;
            at += size;
        }

        for (int v = 0; v < g.VertexCount; v++)
        {
            int grp = g.VertexGroups[v];
            ref readonly var m = ref groupMats[grp < groupCount ? grp : 0];
            outPos[v] = Vector3.Transform(g.Positions[v], m);
            if (outNrm is not null)
            {
                var nrm = Vector3.TransformNormal(g.Normals[v], m);
                outNrm[v] = nrm.LengthSquared() > 1e-10f ? Vector3.Normalize(nrm) : Vector3.UnitZ;
            }
        }
    }

    // ---------------------------------------------------------------- geoset visibility

    /// <summary>
    /// The geoset's animated alpha at a time — how models hide weapons, corpses and alternate
    /// forms per sequence. 1 when the geoset has no animation entry.
    /// </summary>
    public float GeosetAlpha(int geosetIndex, MdxSequence? seq, int timeMs, long? wallMs = null)
    {
        float alpha = 1f;
        foreach (var ga in _model.GeosetAnims)
        {
            if (ga.GeosetId != geosetIndex) continue;
            alpha = ga.AlphaTrack is not null
                ? Sample(ga.AlphaTrack, seq, timeMs, wallMs ?? timeMs, ga.Alpha,
                         float.Lerp, HermiteF, BezierF)
                : ga.Alpha;
            break;
        }
        return Math.Clamp(alpha, 0, 1);
    }

    private static float HermiteF(float a, float outA, float inB, float b, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return a * (2 * t3 - 3 * t2 + 1) + b * (-2 * t3 + 3 * t2) + outA * (t3 - 2 * t2 + t) + inB * (t3 - t2);
    }

    private static float BezierF(float a, float outA, float inB, float b, float t)
    {
        float u = 1 - t;
        return a * (u * u * u) + outA * (3 * u * u * t) + inB * (3 * u * t * t) + b * (t * t * t);
    }
}
