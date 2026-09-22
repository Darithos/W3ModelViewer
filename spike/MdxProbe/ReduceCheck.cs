using System.Numerics;
using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// <c>--reducecheck &lt;cascName&gt; [--scale s]</c>: exports a model twice — every baked key, and
/// key-reduced — then replays BOTH files' skeletons from the bytes written and reports how far
/// apart they are.
/// </summary>
/// <remarks>
/// The reducer bounds each track on its own; this measures what a consumer of the file sees:
/// bone worlds composed down the hierarchy, so the errors of a chain of bones add up, plus a
/// probe point 50 WC3 units out along each bone axis so a rotation error is charged at the length
/// of a limb. The reduced file is interpolated the way a naive engine must — linearly between the
/// keys it holds, nlerp and slerp both tried for rotations, the worse one counted — at every
/// frame of the full file, where the full file holds an exact key.
/// </remarks>
public static class ReduceCheck
{
    private const float ProbeUnits = 50f;

    public static int Run(string install, string cascName, float scale, float tolDeg = 0.25f, float tolUnits = 0.05f)
    {
        using var es = new Wc3Storage(install);
        var raw = es.TryReadFile(cascName) ?? throw new FileNotFoundException(cascName);
        var mdl = MdxReader.Read(raw);
        var cache = new Wc3TextureCache(es) { PreferHd = mdl.IsReforged };
        string name = Path.GetFileNameWithoutExtension(cascName);

        byte[] Export(bool reduce) => new M3Exporter(mdl, new M3ExportOptions
        {
            Lod = mdl.LodLevels.FirstOrDefault(), ModelName = name, Scale = scale, ReduceKeys = reduce, MaxTextureSize = 256,
            KeyToleranceDeg = tolDeg, KeyToleranceUnits = tolUnits,
        }).Export(cache, cascName).M3;

        var full = new M3File(Export(false));
        var red = new M3File(Export(true));
        Console.WriteLine($"{name} @ {tolDeg} deg / {tolUnits} units: full {full.Bytes.Length / 1024} KB, reduced {red.Bytes.Length / 1024} KB, {full.Bones.Count} bones, {full.Stcs.Count} sequences");
        if (full.Bones.Count != red.Bones.Count || full.Stcs.Count != red.Stcs.Count)
        {
            Console.WriteLine("STRUCTURE DIFFERS — bone or sequence count changed");
            return 1;
        }

        float worst = 0; string worstWhere = "";
        long frames = 0;
        for (int s = 0; s < full.Stcs.Count; s++)
        {
            var fs = full.Stcs[s]; var rs = red.Stcs[s];
            // Every distinct frame any full track holds: the dense grid plus the exact end.
            var times = new SortedSet<int>();
            foreach (var t in fs.Vec3.Values) times.UnionWith(t.Frames);
            foreach (var t in fs.Quat.Values) times.UnionWith(t.Frames);
            float seqWorst = 0; string seqWhere = "";
            foreach (int t in times)
            {
                frames++;
                var wf = Worlds(full, fs, t, naive: 0);
                var wn = Worlds(red, rs, t, naive: 1);
                var ws = Worlds(red, rs, t, naive: 2);
                for (int b = 0; b < wf.Length; b++)
                {
                    float d = MathF.Max(Deviation(wf[b], wn[b], scale), Deviation(wf[b], ws[b], scale)) / scale;
                    if (d > seqWorst) { seqWorst = d; seqWhere = $"bone {b} '{full.Bones[b].Name}' at {t} ms"; }
                }
            }
            Console.WriteLine($"  {fs.Name,-28} keys {fs.KeyCount,7} -> {rs.KeyCount,6}   worst {seqWorst,7:0.000} units  ({seqWhere})");
            if (seqWorst > worst) { worst = seqWorst; worstWhere = $"{fs.Name}: {seqWhere}"; }
        }
        Console.WriteLine($"\n{frames} frames replayed. WORST {worst:0.000} WC3 units at a {ProbeUnits:0}-unit lever — {worstWhere}");
        Console.WriteLine($"({full.Stcs.Sum(s => s.KeyCount):N0} keys -> {red.Stcs.Sum(s => s.KeyCount):N0})");
        return 0;
    }

    /// <summary>Largest displacement of the bone origin or of a probe point along each axis.</summary>
    private static float Deviation(Matrix4x4 a, Matrix4x4 b, float scale)
    {
        float lever = ProbeUnits * scale;
        float d = (a.Translation - b.Translation).Length();
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var pa = Vector3.Transform(axis * lever, a);
            var pb = Vector3.Transform(axis * lever, b);
            d = MathF.Max(d, (pa - pb).Length());
        }
        return d;
    }

    /// <summary>Bone world matrices at one frame. naive 0 = exact keys, 1 = nlerp, 2 = slerp.</summary>
    private static Matrix4x4[] Worlds(M3File f, Stc stc, int t, int naive)
    {
        var worlds = new Matrix4x4[f.Bones.Count];
        for (int b = 0; b < f.Bones.Count; b++)
        {
            var bone = f.Bones[b];
            var loc = stc.Vec3.TryGetValue(bone.LocId, out var lt) ? SampleVec3(lt, t) : bone.RestLoc;
            var rot = stc.Quat.TryGetValue(bone.RotId, out var rt) ? SampleQuat(rt, t, naive) : bone.RestRot;
            var scl = stc.Vec3.TryGetValue(bone.SclId, out var st) ? SampleVec3(st, t) : Vector3.One;
            var local = Matrix4x4.CreateScale(scl) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(rot)) * Matrix4x4.CreateTranslation(loc);
            worlds[b] = bone.Parent >= 0 ? local * worlds[bone.Parent] : local;
        }
        return worlds;
    }

    private static (int lo, int hi, float f) Bracket(int[] frames, int t)
    {
        if (t <= frames[0]) return (0, 0, 0);
        if (t >= frames[^1]) return (frames.Length - 1, frames.Length - 1, 0);
        int hi = Array.BinarySearch(frames, t);
        if (hi >= 0) return (hi, hi, 0);
        hi = ~hi;
        int lo = hi - 1;
        return (lo, hi, (t - frames[lo]) / (float)(frames[hi] - frames[lo]));
    }

    private static Vector3 SampleVec3(Track<Vector3> tr, int t)
    {
        var (lo, hi, f) = Bracket(tr.Frames, t);
        return Vector3.Lerp(tr.Vals[lo], tr.Vals[hi], f);
    }

    private static Quaternion SampleQuat(Track<Quaternion> tr, int t, int naive)
    {
        var (lo, hi, f) = Bracket(tr.Frames, t);
        if (lo == hi) return tr.Vals[lo];
        Quaternion a = tr.Vals[lo], b = tr.Vals[hi];
        if (naive == 0) throw new InvalidOperationException($"full file has no key at {t} ms");
        if (naive == 1)
            return new Quaternion(a.X + (b.X - a.X) * f, a.Y + (b.Y - a.Y) * f, a.Z + (b.Z - a.Z) * f, a.W + (b.W - a.W) * f);
        float dot = Quaternion.Dot(a, b);
        if (dot > 0.9995f) return SampleQuat(tr, t, 1);
        float theta = MathF.Acos(Math.Clamp(dot, -1, 1)), sin = MathF.Sin(theta);
        float wa = MathF.Sin((1 - f) * theta) / sin, wb = MathF.Sin(f * theta) / sin;
        return new Quaternion(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
    }

    // ---------------------------------------------------------------- the file, read back

    private sealed record Track<T>(int[] Frames, T[] Vals);

    private sealed record Bone(string Name, int Parent, uint LocId, uint RotId, uint SclId, Vector3 RestLoc, Quaternion RestRot);

    private sealed class Stc
    {
        public string Name = "";
        public Dictionary<uint, Track<Vector3>> Vec3 = [];
        public Dictionary<uint, Track<Quaternion>> Quat = [];
        public int KeyCount => Vec3.Values.Sum(t => t.Frames.Length) + Quat.Values.Sum(t => t.Frames.Length);
    }

    /// <summary>Just enough of the section index, BONE and STC_ to replay the skeleton.</summary>
    private sealed class M3File
    {
        public byte[] Bytes;
        public List<Bone> Bones = [];
        public List<Stc> Stcs = [];
        private readonly (string Tag, int Offset, int Count)[] _sections;

        public M3File(byte[] m3)
        {
            Bytes = m3;
            int indexOffset = BitConverter.ToInt32(m3, 4), sectionCount = BitConverter.ToInt32(m3, 8);
            _sections = new (string, int, int)[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                int e = indexOffset + i * 16;
                uint tag = BitConverter.ToUInt32(m3, e);
                _sections[i] = (new string([(char)(tag >> 24), (char)((tag >> 16) & 255), (char)((tag >> 8) & 255), (char)(tag & 255)]),
                                BitConverter.ToInt32(m3, e + 4), BitConverter.ToInt32(m3, e + 8));
            }

            int boneSec = Array.FindIndex(_sections, s => s.Tag == "BONE");
            for (int i = 0; i < _sections[boneSec].Count; i++)
            {
                int o = _sections[boneSec].Offset + i * 160;
                Bones.Add(new Bone(
                    Str(o + 4),
                    BitConverter.ToInt16(m3, o + 20),
                    BitConverter.ToUInt32(m3, o + 28),
                    BitConverter.ToUInt32(m3, o + 64),
                    BitConverter.ToUInt32(m3, o + 108),
                    Vec3(o + 32),
                    new Quaternion(F(o + 68), F(o + 72), F(o + 76), F(o + 80))));
            }

            int stcSec = Array.FindIndex(_sections, s => s.Tag == "STC_");
            for (int i = 0; i < _sections[stcSec].Count; i++)
            {
                int o = _sections[stcSec].Offset + i * 204;
                var stc = new Stc { Name = Str(o) };
                var ids = U32s(o + 20);
                var refs = U32s(o + 32);
                var (sd3vOff, sd3vCount) = Ref(o + 72);
                var (sd4qOff, sd4qCount) = Ref(o + 84);
                for (int k = 0; k < ids.Length; k++)
                {
                    int kind = (int)(refs[k] >> 16), ti = (int)(refs[k] & 0xFFFF);
                    if (kind == 2 && ti < sd3vCount)
                    {
                        int sd = sd3vOff + ti * 32;
                        var frames = I32s(sd);
                        var (ko, kc) = Ref(sd + 20);
                        var vals = new Vector3[kc];
                        for (int v = 0; v < kc; v++) vals[v] = Vec3(ko + v * 12);
                        stc.Vec3[ids[k]] = new Track<Vector3>(frames, vals);
                    }
                    else if (kind == 3 && ti < sd4qCount)
                    {
                        int sd = sd4qOff + ti * 32;
                        var frames = I32s(sd);
                        var (ko, kc) = Ref(sd + 20);
                        var vals = new Quaternion[kc];
                        for (int v = 0; v < kc; v++) vals[v] = new Quaternion(F(ko + v * 16), F(ko + v * 16 + 4), F(ko + v * 16 + 8), F(ko + v * 16 + 12));
                        stc.Quat[ids[k]] = new Track<Quaternion>(frames, vals);
                    }
                }
                Stcs.Add(stc);
            }
        }

        private float F(int o) => BitConverter.ToSingle(Bytes, o);
        private Vector3 Vec3(int o) => new(F(o), F(o + 4), F(o + 8));

        /// <summary>A Reference {entries, index, flags} → the section's byte offset and entry count.</summary>
        private (int Offset, int Count) Ref(int o)
        {
            int entries = BitConverter.ToInt32(Bytes, o), index = BitConverter.ToInt32(Bytes, o + 4);
            return entries == 0 ? (0, 0) : (_sections[index].Offset, Math.Min(entries, _sections[index].Count));
        }

        private string Str(int refAt)
        {
            var (o, n) = Ref(refAt);
            return Encoding.UTF8.GetString(Bytes, o, n).TrimEnd('\0');
        }

        private uint[] U32s(int refAt)
        {
            var (o, n) = Ref(refAt);
            var r = new uint[n];
            for (int i = 0; i < n; i++) r[i] = BitConverter.ToUInt32(Bytes, o + i * 4);
            return r;
        }

        private int[] I32s(int refAt)
        {
            var (o, n) = Ref(refAt);
            var r = new int[n];
            for (int i = 0; i < n; i++) r[i] = BitConverter.ToInt32(Bytes, o + i * 4);
            return r;
        }
    }
}
