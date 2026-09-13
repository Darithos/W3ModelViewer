using System.Numerics;
using System.Text;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>How a PopcornFX billboard faces the viewer. Values are PopcornFX v2's <c>EBillboardMode</c>.</summary>
public enum PopcornBillboardMode
{
    ScreenAligned = 0,
    ViewposAligned = 1,
    AxisAligned = 2,
    AxisAlignedSpheroid = 3,
    AxisAlignedCapsule = 4,
    PlaneAligned = 5,
}

/// <summary>How a PopcornFX layer blends. Values are PopcornFX v2's <c>ETransparentType</c>.</summary>
public enum PopcornBlend
{
    Additive = 0,
    AdditiveNoAlpha = 1,
    AlphaBlend = 2,
    PremultipliedAlpha = 3,
}

/// <summary>A sampled curve from a bake: knots in 0..1 with a value of <see cref="Dimension"/> floats each.</summary>
public sealed class PopcornCurve
{
    public required int Dimension { get; init; }
    public required float[] Times { get; init; }
    /// <summary><c>Times.Length x Dimension</c> values, knot-major.</summary>
    public required float[] Values { get; init; }

    public int Count => Times.Length;

    public Vector4 At(int knot)
    {
        int o = knot * Dimension;
        return new Vector4(Values[o],
                           Dimension > 1 ? Values[o + 1] : Values[o],
                           Dimension > 2 ? Values[o + 2] : Values[o],
                           Dimension > 3 ? Values[o + 3] : 1f);
    }

    /// <summary>Linear sample at a life fraction.</summary>
    public Vector4 Sample(float t)
    {
        if (Count == 0) return Vector4.One;
        if (t <= Times[0]) return At(0);
        if (t >= Times[^1]) return At(Count - 1);
        int k = 0;
        while (k + 1 < Count && Times[k + 1] <= t) k++;
        float span = Times[k + 1] - Times[k];
        float f = span > 0 ? (t - Times[k]) / span : 0;
        return Vector4.Lerp(At(k), At(k + 1), f);
    }

    /// <summary>Knot with the largest first component (the peak of a size curve, the brightest colour).</summary>
    public int PeakKnot()
    {
        int best = 0;
        float bestV = float.NegativeInfinity;
        for (int k = 0; k < Count; k++)
        {
            var v = At(k);
            float m = Dimension >= 3 ? v.X + v.Y + v.Z : v.X;
            if (m > bestV) { bestV = m; best = k; }
        }
        return best;
    }

    public float MaxScalar()
    {
        float m = 0;
        foreach (float v in Values) if (float.IsFinite(v)) m = MathF.Max(m, v);
        return m;
    }
}

/// <summary>One rendered layer of a baked effect — what a single StarCraft II particle system can stand in for.</summary>
public sealed class PopcornLayer
{
    /// <summary>The graph node the layer's particle fields are named after (<c>n20_8</c>), or the layer's record index.</summary>
    public required string Name { get; init; }

    /// <summary>Texture path as the bake spells it (<c>_HD.w3mod/Textures/FX/Flare/Flare_BW.tif</c>), or "".</summary>
    public string TexturePath { get; init; } = "";

    public PopcornBillboardMode Billboard { get; init; }
    public PopcornBlend Blend { get; init; }
    public bool SoftParticles { get; init; }

    /// <summary>Colour (and alpha) over life, when the layer samples one.</summary>
    public PopcornCurve? ColorCurve { get; init; }

    /// <summary>A one-dimensional curve over life — size, when the layer samples one.</summary>
    public PopcornCurve? ScalarCurve { get; init; }

    /// <summary>Particle fields the layer's script writes (Position, Size, Axis, Color, Rotation, Enabled …).</summary>
    public List<string> Fields { get; init; } = [];

    /// <summary>Every finite scalar the layer's constant pool broadcasts to all four lanes, in pool order.</summary>
    public List<float> PoolScalars { get; init; } = [];

    /// <summary>Pool entries with exactly one non-zero, finite lane — positions, axes, offsets.</summary>
    public List<Vector4> PoolAxes { get; init; } = [];

    /// <summary>Pool entries that look like a colour: three or four finite non-negative lanes, not all equal.</summary>
    public List<Vector4> PoolColors { get; init; } = [];

    /// <summary>Distinct floats the layer's bytecode broadcasts as SIMD-8 constants, in blob order.</summary>
    public List<float> BlobScalars { get; init; } = [];

    /// <summary>True when the pool broadcasts +infinity — a layer with no lifetime clamps against it.</summary>
    public bool PoolHasInfinity { get; init; }

    public bool HasField(string suffix) => Fields.Any(f => f.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name}: {Path.GetFileName(TexturePath)} {Billboard} {Blend}";
}

/// <summary>A parsed PopcornFX <c>.pkb</c> bake: its renderer layers and what they sample.</summary>
public sealed class PopcornEffect
{
    public List<PopcornLayer> Layers { get; } = [];
    public List<string> Strings { get; } = [];
    public int RecordCount { get; init; }
    public List<string> Warnings { get; } = [];

    public override string ToString() => $"{Layers.Count} layer(s), {RecordCount} records";
}

/// <summary>
/// Reads a PopcornFX 2 baked effect (<c>.pkb</c>) far enough to describe each renderer layer.
/// </summary>
/// <remarks>
/// <para>
/// The bake is a serialised object graph with the layout established from the real files
/// (<c>holyboltspecialart.pkb</c> and others, 2026-09-13): a header, a class table of
/// <c>{stringIndex, instanceCount}</c> pairs, one record per object, then a string table that runs
/// to the end of the file. A record is <c>{u32 size; u8 0x20; u32 classStringIndex; u16 fieldCount;
/// (u16 fieldIndex, value)*}</c> and object references are <b>1-based</b> (0 is null). The value types
/// are not stored — the runtime knows them from its class definitions — so each record is parsed by
/// trying the small set of encodings PopcornFX uses (4-byte scalar, 8/12/16-byte vectors, and
/// count-prefixed arrays of 1/2/4/8/12/16-byte elements) and keeping the one parse under which
/// every following field index is larger than the last and the record ends exactly on its size.
/// A per-class preference table settles the rare ambiguity.
/// </para>
/// <para>
/// What is declarative — renderer properties (texture, billboarding, blend), curve samplers, shape
/// samplers, particle field names and each layer's constant pool — is read. Per-particle behaviour is
/// compiled bytecode (<c>CCompilerBlobCache</c>) and is not; only the float constants it broadcasts
/// are skimmed, because lifetimes and sizes show up there. Anything built on this is an
/// approximation of the effect, never a reproduction.
/// </para>
/// </remarks>
public static class PopcornBake
{
    private const uint Magic = 0xCA000B11;

    private enum Enc { U32, U8, U16, V2, V3, V4, Arr1, Arr2, Arr4, Arr8, Arr12, Arr16 }

    private static readonly Enc[] AllEncs = Enum.GetValues<Enc>();

    private readonly record struct Field(int Index, Enc Encoding, int Start, int End);

    private sealed class Record
    {
        public int Class;
        public int FieldCount;
        public byte[] Body = [];
        public Dictionary<int, Field>? Fields;
    }

    private sealed class Bake
    {
        public byte[] Data = [];
        public List<string> Strings = [];
        public List<(int StringIndex, int Count)> Classes = [];
        public List<Record> Records = [];

        public string ClassName(Record r) => Str(Classes[r.Class].StringIndex);
        public string Str(int i) => (uint)i < (uint)Strings.Count ? Strings[i] : $"<str {i}>";
    }

    public static bool LooksLikeBake(byte[] data)
        => data.Length > 0x20 && BitConverter.ToUInt32(data, 0) == Magic;

    public static PopcornEffect Parse(byte[] data)
    {
        var b = Load(data);
        var effect = new PopcornEffect { RecordCount = b.Records.Count };
        effect.Strings.AddRange(b.Strings);

        for (int ri = 0; ri < b.Records.Count; ri++)
        {
            var rec = b.Records[ri];
            if (b.ClassName(rec) != "CLayerCompileCache") continue;
            var f = Fields(b, rec);
            if (f is null) { effect.Warnings.Add($"layer #{ri}: could not parse its fields"); continue; }

            var renderers = RefArray(b, rec, f, 8);
            if (renderers.Count == 0) continue;               // spawner / event layers draw nothing

            var fieldNames = new List<string>();
            foreach (int fi in RefArray(b, rec, f, 2))
            {
                var fr = b.Records[fi];
                if (b.ClassName(fr) != "CLayerCompileCacheField") continue;
                var ff = Fields(b, fr);
                if (ff is not null && ff.TryGetValue(0, out var nameF)) fieldNames.Add(b.Str(I32(fr, nameF)));
            }

            PopcornCurve? color = null, scalar = null;
            foreach (int si in RefArray(b, rec, f, 7))
            {
                var sr = b.Records[si];
                if (b.ClassName(sr) != "CLayerCompileCacheSampler") continue;
                var sf = Fields(b, sr);
                if (sf is null || !sf.TryGetValue(1, out var dataRef)) continue;
                int di = I32(sr, dataRef) - 1;
                if ((uint)di >= (uint)b.Records.Count) continue;
                var dr = b.Records[di];
                if (b.ClassName(dr) != "CParticleNodeSamplerData_Curve") continue;
                var curve = ReadCurve(b, dr);
                if (curve is null) continue;
                if (curve.Dimension >= 3) color = PreferColourLike(color, curve);
                else if (curve.Dimension == 1) scalar = PreferSizeLike(scalar, curve);
            }

            var (poolScalars, poolAxes, poolColors, infinite) = ReadPool(b, rec, f);
            var blobScalars = new List<float>();
            foreach (int bi in RefArray(b, rec, f, 10)) SkimBlob(b.Records[bi].Body, blobScalars);

            string layerName = fieldNames.Select(n => n.Split("__")[0]).FirstOrDefault(n => n.StartsWith('n') && n.Length > 1 && char.IsDigit(n[1]))
                               ?? $"layer{ri}";

            // One renderer per layer is the norm; a layer with two draws the same particles twice
            // (the hero glow draws a screen-aligned glow and a ground disc from one stream).
            foreach (int rr in renderers)
            {
                var rrec = b.Records[rr];
                if (b.ClassName(rrec) != "CLayerCompileCacheRenderer") continue;
                var rf = Fields(b, rrec);
                if (rf is null) continue;

                string texture = "";
                var mode = PopcornBillboardMode.ScreenAligned;
                var blend = PopcornBlend.Additive;
                bool soft = false;
                foreach (int pi in RefArray(b, rrec, rf, 2))
                {
                    var prec = b.Records[pi];
                    if (b.ClassName(prec) != "CLayerCompileCacheRendererProperty") continue;
                    var pf = Fields(b, prec);
                    if (pf is null || !pf.TryGetValue(0, out var nameF)) continue;
                    string name = b.Str(I32(prec, nameF));
                    // Field 2 holds the value (four lanes); an absent field is the default, zero.
                    int enumValue = pf.TryGetValue(2, out var vf) ? BitConverter.ToInt32(prec.Body, vf.Start) : 0;
                    switch (name)
                    {
                        case "Diffuse.DiffuseMap":
                            if (pf.TryGetValue(3, out var sf2)) texture = b.Str(I32(prec, sf2));
                            break;
                        case "BillboardingMode": mode = (PopcornBillboardMode)Math.Clamp(enumValue, 0, 5); break;
                        case "Transparent.Type": blend = (PopcornBlend)Math.Clamp(enumValue, 0, 3); break;
                        case "SoftParticles":
                            // Present as an unset marker on layers that enable it; absent otherwise.
                            soft = pf.ContainsKey(2);
                            break;
                    }
                }

                effect.Layers.Add(new PopcornLayer
                {
                    Name = renderers.Count > 1 ? $"{layerName}.{effect.Layers.Count}" : layerName,
                    TexturePath = texture,
                    Billboard = mode,
                    Blend = blend,
                    SoftParticles = soft,
                    ColorCurve = color,
                    ScalarCurve = scalar,
                    Fields = fieldNames,
                    PoolScalars = poolScalars,
                    PoolAxes = poolAxes,
                    PoolColors = poolColors,
                    BlobScalars = blobScalars,
                    PoolHasInfinity = infinite,
                });
            }
        }
        return effect;
    }

    /// <summary>
    /// Between two one-dimensional curves keep the one that reads as a size: the one that stays
    /// above zero at more of its knots. A curve that sits at zero most of its life is a window or
    /// a trigger (the hero glow's 1 → 0 → 0 fade-in), not a shape.
    /// </summary>
    private static PopcornCurve PreferSizeLike(PopcornCurve? have, PopcornCurve candidate)
    {
        if (have is null) return candidate;
        static float Alive(PopcornCurve c) => c.Values.Count(v => v > 0.05f) / (float)Math.Max(c.Values.Length, 1);
        return Alive(candidate) > Alive(have) ? candidate : have;
    }

    /// <summary>Between two colour curves keep the brighter one over its life — a fade-out helper curve loses to the colour itself.</summary>
    private static PopcornCurve PreferColourLike(PopcornCurve? have, PopcornCurve candidate)
    {
        if (have is null) return candidate;
        static float Mean(PopcornCurve c)
        {
            float sum = 0;
            for (int k = 0; k < c.Count; k++) { var v = c.At(k); sum += Math.Clamp(v.W, 0, 1) * (v.X + v.Y + v.Z) / 3f; }
            return c.Count == 0 ? 0 : sum / c.Count;
        }
        return Mean(candidate) > Mean(have) ? candidate : have;
    }

    // ---------------------------------------------------------------- container

    private static Bake Load(byte[] d)
    {
        if (!LooksLikeBake(d)) throw new InvalidDataException("Not a PopcornFX bake (missing the 0xCA000B11 magic).");
        int recordCount = BitConverter.ToInt32(d, 0x0C);
        int classCount = BitConverter.ToInt32(d, 0x10);
        int strTab = BitConverter.ToInt32(d, 0x14);
        if (strTab <= 0x1C || strTab >= d.Length) throw new InvalidDataException("Bake string table offset is out of range.");

        var b = new Bake { Data = d };
        int n = BitConverter.ToInt32(d, strTab);
        int p = strTab + 4;
        for (int i = 0; i < n && p < d.Length; i++)
        {
            int len = d[p++];
            if (p + len > d.Length) break;
            b.Strings.Add(Encoding.Latin1.GetString(d, p, len));
            p += len;
        }

        for (int i = 0; i < classCount; i++)
        {
            int at = 0x1C + 8 * i;
            if (at + 8 > strTab) break;
            b.Classes.Add((BitConverter.ToInt32(d, at), BitConverter.ToInt32(d, at + 4)));
        }

        p = 0x1C + 8 * classCount;
        while (p + 4 <= strTab && b.Records.Count < recordCount)
        {
            int size = BitConverter.ToInt32(d, p);
            if (size < 7 || p + 4 + size > strTab) throw new InvalidDataException($"Bake record at {p} overruns the object stream.");
            if (d[p + 4] != 0x20) throw new InvalidDataException($"Bake record at {p} lacks the 0x20 marker.");
            int cls = BitConverter.ToInt32(d, p + 5);
            int nf = BitConverter.ToUInt16(d, p + 9);
            var body = new byte[size - 7];
            Array.Copy(d, p + 11, body, 0, body.Length);
            b.Records.Add(new Record { Class = cls, FieldCount = nf, Body = body });
            p += 4 + size;
        }
        if (b.Records.Count != recordCount)
            throw new InvalidDataException($"Bake declares {recordCount} records but {b.Records.Count} parse.");
        return b;
    }

    // ---------------------------------------------------------------- record fields

    /// <summary>Encodings each class is known to use, from the Python survey; other fields are inferred.</summary>
    private static readonly Dictionary<string, Dictionary<int, Enc>> Preferred = new()
    {
        ["CLayerCompileCache"] = new() { [0] = Enc.Arr16, [1] = Enc.Arr16, [2] = Enc.Arr4, [7] = Enc.Arr4, [8] = Enc.Arr4, [9] = Enc.U32, [10] = Enc.Arr4, [12] = Enc.U32, [13] = Enc.U32, [20] = Enc.U32, [22] = Enc.U32, [23] = Enc.U32 },
        ["CLayerCompileCacheRenderer"] = new() { [1] = Enc.Arr4, [2] = Enc.Arr4, [3] = Enc.U32 },
        ["CLayerCompileCacheRendererProperty"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.V4, [3] = Enc.U32 },
        ["CLayerCompileCacheSampler"] = new() { [0] = Enc.U32, [1] = Enc.U32, [3] = Enc.U32, [4] = Enc.U32 },
        ["CParticleNodeSamplerData_Curve"] = new() { [0] = Enc.Arr16, [7] = Enc.V2, [9] = Enc.U32, [16] = Enc.Arr4, [17] = Enc.Arr4, [18] = Enc.Arr4 },
        ["CParticleNodeSamplerData_Shape"] = new() { [0] = Enc.Arr16, [7] = Enc.V2, [9] = Enc.U32, [13] = Enc.V3, [15] = Enc.U32, [17] = Enc.U32, [20] = Enc.U32 },
        ["CLayerCompileCacheField"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32, [3] = Enc.U32, [4] = Enc.U32, [5] = Enc.U32 },
    };

    private static Dictionary<int, Field>? Fields(Bake b, Record r)
    {
        if (r.Fields is not null) return r.Fields;
        Preferred.TryGetValue(b.ClassName(r), out var prefer);
        var best = new List<Field>();
        int bestScore = -1;
        int explored = 0;

        void Walk(int k, int p, int last, List<Field> acc)
        {
            // A layer record can carry a dozen fields the preference table does not know, each
            // tried under twelve encodings; the lookahead prunes almost all of them, so the cap
            // only guards against a pathological record.
            if (explored > 400_000) return;
            var body = r.Body;
            if (k == r.FieldCount)
            {
                if (p != body.Length) return;
                int score = prefer is null ? 0 : acc.Count(f => prefer.TryGetValue(f.Index, out var e) && e == f.Encoding);
                if (score > bestScore) { bestScore = score; best = new List<Field>(acc); }
                return;
            }
            if (p + 2 > body.Length) return;
            int fi = BitConverter.ToUInt16(body, p);
            if (fi <= last) return;
            int q = p + 2;

            // Try the preferred encoding first so the common case never explores alternatives.
            IEnumerable<Enc> order = prefer is not null && prefer.TryGetValue(fi, out var pe)
                ? [pe, .. AllEncs.Where(e => e != pe)] : AllEncs;
            foreach (var enc in order)
            {
                explored++;
                int e = End(body, q, enc);
                if (e < 0) continue;
                bool lastField = k == r.FieldCount - 1;
                if (lastField ? e != body.Length : !(e + 2 <= body.Length && BitConverter.ToUInt16(body, e) > fi)) continue;
                acc.Add(new Field(fi, enc, q, e));
                Walk(k + 1, e, fi, acc);
                acc.RemoveAt(acc.Count - 1);
                if (bestScore >= 0 && prefer is null) return;   // any full parse will do without a preference table
                if (prefer is not null && bestScore == prefer.Count) return;
            }
        }
        Walk(0, 0, -1, []);
        if (bestScore < 0) return null;
        r.Fields = best.ToDictionary(f => f.Index);
        return r.Fields;
    }

    private static int End(byte[] body, int p, Enc enc)
    {
        int fixedLen = enc switch
        {
            Enc.U32 => 4, Enc.U8 => 1, Enc.U16 => 2, Enc.V2 => 8, Enc.V3 => 12, Enc.V4 => 16, _ => 0,
        };
        if (fixedLen > 0) return p + fixedLen <= body.Length ? p + fixedLen : -1;
        if (p + 4 > body.Length) return -1;
        int n = BitConverter.ToInt32(body, p);
        if (n < 0 || n > 1_000_000) return -1;
        int elem = enc switch { Enc.Arr1 => 1, Enc.Arr2 => 2, Enc.Arr4 => 4, Enc.Arr8 => 8, Enc.Arr12 => 12, _ => 16 };
        long e = p + 4 + (long)n * elem;
        return e <= body.Length ? (int)e : -1;
    }

    private static int I32(Record r, Field f) => BitConverter.ToInt32(r.Body, f.Start);

    /// <summary>A field holding an array of 1-based record references, as 0-based indices; empty when absent.</summary>
    private static List<int> RefArray(Bake b, Record r, Dictionary<int, Field> fields, int index)
    {
        var refs = new List<int>();
        if (!fields.TryGetValue(index, out var f) || f.Encoding != Enc.Arr4) return refs;
        int n = BitConverter.ToInt32(r.Body, f.Start);
        for (int i = 0; i < n; i++)
        {
            int v = BitConverter.ToInt32(r.Body, f.Start + 4 + 4 * i) - 1;
            if ((uint)v < (uint)b.Records.Count) refs.Add(v);
        }
        return refs;
    }

    private static float[] FloatArray(Record r, Dictionary<int, Field> fields, int index)
    {
        if (!fields.TryGetValue(index, out var f) || f.Encoding != Enc.Arr4) return [];
        int n = BitConverter.ToInt32(r.Body, f.Start);
        var a = new float[n];
        for (int i = 0; i < n; i++) a[i] = BitConverter.ToSingle(r.Body, f.Start + 4 + 4 * i);
        return a;
    }

    private static PopcornCurve? ReadCurve(Bake b, Record r)
    {
        var f = Fields(b, r);
        if (f is null) return null;
        int dim = f.TryGetValue(9, out var df) ? BitConverter.ToInt32(r.Body, df.Start) : 1;
        var times = FloatArray(r, f, 16);
        var values = FloatArray(r, f, 17);
        if (dim < 1 || dim > 4 || times.Length == 0 || values.Length < times.Length * dim) return null;
        return new PopcornCurve { Dimension = dim, Times = times, Values = values[..(times.Length * dim)] };
    }

    /// <summary>
    /// The layer's constant pool (field 1): float4 entries. Broadcast scalars, single-lane axes
    /// and colour-like entries are separated out; the interleaved integer-one entries are skipped.
    /// </summary>
    private static (List<float> Scalars, List<Vector4> Axes, List<Vector4> Colors, bool Infinite) ReadPool(Bake b, Record r, Dictionary<int, Field> f)
    {
        var scalars = new List<float>();
        var axes = new List<Vector4>();
        var colors = new List<Vector4>();
        bool infinite = false;
        if (!f.TryGetValue(1, out var pf) || pf.Encoding != Enc.Arr16) return (scalars, axes, colors, infinite);

        int n = BitConverter.ToInt32(r.Body, pf.Start);
        for (int i = 0; i < n; i++)
        {
            int at = pf.Start + 4 + 16 * i;
            var u = new uint[4];
            var v = new float[4];
            for (int k = 0; k < 4; k++) { u[k] = BitConverter.ToUInt32(r.Body, at + 4 * k); v[k] = BitConverter.ToSingle(r.Body, at + 4 * k); }

            if (u.All(x => x == 1) || u.All(x => x == 0)) continue;          // lane masks and zero
            if (u.All(x => x == u[0]))
            {
                if (float.IsPositiveInfinity(v[0])) { infinite = true; continue; }
                if (float.IsFinite(v[0]) && v[0] != 0 && MathF.Abs(v[0]) < 1e6f && MathF.Abs(v[0]) > 1e-6f) scalars.Add(v[0]);
                continue;
            }
            int nonZero = v.Count(x => x != 0);
            bool allFinite = v.All(float.IsFinite);
            if (nonZero == 1 && allFinite && v.Take(3).Any(x => x != 0)) { axes.Add(new Vector4(v[0], v[1], v[2], v[3])); continue; }
            if (allFinite && v.Take(3).All(x => x >= 0) && v.Take(3).Count(x => x > 0) >= 2 && v.Take(3).Any(x => x <= 20f) && u.Take(3).All(x => x != 1))
                colors.Add(new Vector4(v[0], v[1], v[2], v[3]));
        }
        return (scalars, axes, colors, infinite);
    }

    /// <summary>
    /// PopcornFX compiles for 8-wide SIMD, so a script constant sits in the bytecode blob as the
    /// same 32-bit word repeated eight times. Those words are the only readable part of the script.
    /// </summary>
    private static void SkimBlob(byte[] blob, List<float> into)
    {
        for (int o = 0; o + 32 <= blob.Length; o += 4)
        {
            uint w = BitConverter.ToUInt32(blob, o);
            bool broadcast = true;
            for (int k = 1; k < 8 && broadcast; k++) broadcast = BitConverter.ToUInt32(blob, o + 4 * k) == w;
            if (!broadcast) continue;
            float f = BitConverter.ToSingle(blob, o);
            if (float.IsFinite(f) && MathF.Abs(f) > 1e-5f && MathF.Abs(f) < 1e5f && (into.Count == 0 || into[^1] != f))
                into.Add(f);
            o += 28;
        }
    }
}
