using System.Numerics;
using System.Text;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>
/// Random access to the records of a PopcornFX 2 <c>.pkb</c> bake, with typed field reads.
/// </summary>
/// <remarks>
/// <para>
/// Container layout (see <c>mdxres/research/popcornfx-bake.md</c>): header, class table of
/// <c>{stringIndex, instanceCount}</c>, records of <c>{u32 size; u8 0x20; u32 class; u16 fieldCount;
/// (u16 fieldIndex, value)*}</c>, then a string table. Object references are 1-based.
/// </para>
/// <para>
/// Field value types are not stored. <see cref="Schema"/> gives the encoding of every field the
/// runtime reads, measured over all 2.8 million records of the 2,165 bakes Warcraft III ships; a field
/// outside the schema (editor-only data the runtime ignores) is skipped by inferring the encoding under
/// which the rest of the record still parses — that combination parses every record in the archive.
/// </para>
/// </remarks>
public sealed class PkBakeFile
{
    private const uint Magic = 0xCA000B11;

    private enum Enc : byte { U8, U16, U32, U64, V3, V4, A1, A2, A4, A8, A12, A16 }

    private readonly record struct Field(Enc Encoding, int Start, int End);

    private sealed class Rec
    {
        public int Class;
        public int FieldCount;
        public byte[] Body = [];
        public Dictionary<int, Field>? Fields;
    }

    private readonly List<Rec> _records = [];
    private readonly List<int> _classNameIndex = [];

    public List<string> Strings { get; } = [];
    public int Count => _records.Count;

    public static bool LooksLikeBake(byte[] data)
        => data.Length > 0x20 && BitConverter.ToUInt32(data, 0) == Magic;

    public static PkBakeFile Load(byte[] d)
    {
        if (!LooksLikeBake(d)) throw new InvalidDataException("Not a PopcornFX bake (missing the 0xCA000B11 magic).");
        int recordCount = BitConverter.ToInt32(d, 0x0C);
        int classCount = BitConverter.ToInt32(d, 0x10);
        int strTab = BitConverter.ToInt32(d, 0x14);
        if (strTab <= 0x1C || strTab >= d.Length) throw new InvalidDataException("Bake string table offset is out of range.");

        var b = new PkBakeFile();
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
            b._classNameIndex.Add(BitConverter.ToInt32(d, at));
        }
        p = 0x1C + 8 * classCount;
        while (p + 4 <= strTab && b._records.Count < recordCount)
        {
            int size = BitConverter.ToInt32(d, p);
            if (size < 7 || p + 4 + size > strTab) throw new InvalidDataException($"Bake record at {p} overruns the object stream.");
            if (d[p + 4] != 0x20) throw new InvalidDataException($"Bake record at {p} lacks the 0x20 marker.");
            var body = new byte[size - 7];
            Array.Copy(d, p + 11, body, 0, body.Length);
            b._records.Add(new Rec { Class = BitConverter.ToInt32(d, p + 5), FieldCount = BitConverter.ToUInt16(d, p + 9), Body = body });
            p += 4 + size;
        }
        if (b._records.Count != recordCount)
            throw new InvalidDataException($"Bake declares {recordCount} records but {b._records.Count} parse.");
        return b;
    }

    public string ClassOf(int rec)
        => (uint)rec < (uint)_records.Count && (uint)_records[rec].Class < (uint)_classNameIndex.Count
            ? Str(_classNameIndex[_records[rec].Class]) : "";

    public string Str(int index) => (uint)index < (uint)Strings.Count ? Strings[index] : "";

    public IEnumerable<int> OfClass(string name)
    {
        for (int i = 0; i < _records.Count; i++) if (ClassOf(i) == name) yield return i;
    }

    // ---------------------------------------------------------------- typed reads

    public bool Has(int rec, int field) => TryField(rec, field, out _);

    public uint U32(int rec, int field, uint fallback = 0)
        => TryField(rec, field, out var f) && f.End - f.Start >= 4 ? BitConverter.ToUInt32(_records[rec].Body, f.Start) : fallback;

    public int I32(int rec, int field, int fallback = 0) => (int)U32(rec, field, (uint)fallback);

    public float F32(int rec, int field, float fallback = 0)
        => TryField(rec, field, out var f) && f.End - f.Start >= 4 ? BitConverter.ToSingle(_records[rec].Body, f.Start) : fallback;

    public byte U8(int rec, int field, byte fallback = 0)
        => TryField(rec, field, out var f) && f.End > f.Start ? _records[rec].Body[f.Start] : fallback;

    /// <summary>A field holding a string-table index, as the string; "" when absent.</summary>
    public string String(int rec, int field) => TryField(rec, field, out _) ? Str(I32(rec, field)) : "";

    public Vector3 V3(int rec, int field, Vector3 fallback = default)
    {
        if (!TryField(rec, field, out var f) || f.End - f.Start < 12) return fallback;
        var body = _records[rec].Body;
        return new Vector3(BitConverter.ToSingle(body, f.Start), BitConverter.ToSingle(body, f.Start + 4), BitConverter.ToSingle(body, f.Start + 8));
    }

    /// <summary>The raw bytes of a fixed-size field (a 16-byte property value, say).</summary>
    public byte[] Raw(int rec, int field)
        => TryField(rec, field, out var f) ? _records[rec].Body[f.Start..f.End] : [];

    /// <summary>An array field's element bytes (without the count prefix); empty when absent.</summary>
    public byte[] ArrayBytes(int rec, int field)
        => TryField(rec, field, out var f) && f.End - f.Start >= 4 ? _records[rec].Body[(f.Start + 4)..f.End] : [];

    public uint[] U32s(int rec, int field)
    {
        var raw = ArrayBytes(rec, field);
        var a = new uint[raw.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BitConverter.ToUInt32(raw, i * 4);
        return a;
    }

    public float[] F32s(int rec, int field)
    {
        var raw = ArrayBytes(rec, field);
        var a = new float[raw.Length / 4];
        for (int i = 0; i < a.Length; i++) a[i] = BitConverter.ToSingle(raw, i * 4);
        return a;
    }

    /// <summary>An array of 1-based record references, as 0-based indices; a null reference becomes -1.</summary>
    public int[] Refs(int rec, int field) => U32s(rec, field).Select(u => u == 0 || u > int.MaxValue ? -1 : (int)u - 1).ToArray();

    /// <summary>A single 1-based record reference, 0-based; -1 when absent or null.</summary>
    public int Ref(int rec, int field)
    {
        uint u = U32(rec, field);
        return u == 0 || u > (uint)_records.Count ? -1 : (int)u - 1;
    }

    /// <summary>Every field of a record with the encoding it parsed under, for probes studying unknown classes.</summary>
    public IEnumerable<(int Field, string Encoding, byte[] Bytes)> FieldsOf(int rec)
    {
        if ((uint)rec >= (uint)_records.Count) yield break;
        TryField(rec, -1, out _);      // walks the record
        foreach (var (fi, f) in _records[rec].Fields!.OrderBy(kv => kv.Key))
            yield return (fi, f.Encoding.ToString(), _records[rec].Body[f.Start..f.End]);
    }

    // ---------------------------------------------------------------- field walking

    private static readonly Dictionary<string, Dictionary<int, Enc>> Schema = new()
    {
        ["CParticleEffect"] = new() { [1] = Enc.U32, [3] = Enc.U32 },
        ["CLayerGraphCompileCache"] = new() { [1] = Enc.A4, [2] = Enc.A4, [3] = Enc.A4, [5] = Enc.A12 },
        ["CLayerGraphCompileCache_EntrySlot"] = new() { [0] = Enc.U32, [1] = Enc.U32 },
        ["CLayerGraphCompileCache_EventSlot"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.A4 },
        ["CLayerGraphCompileCache_LayerSlot"] = new() { [0] = Enc.U32, [1] = Enc.A4, [2] = Enc.A4 },
        ["CLayerCompileCache"] = new()
        {
            [0] = Enc.A16, [1] = Enc.A16, [2] = Enc.A4, [3] = Enc.A4, [5] = Enc.A4, [6] = Enc.A4, [7] = Enc.A4, [8] = Enc.A4,
            [9] = Enc.U32, [10] = Enc.A4, [12] = Enc.U32, [13] = Enc.U32, [14] = Enc.U8, [15] = Enc.U8,
            [20] = Enc.U32, [22] = Enc.U32, [23] = Enc.U32,
        },
        ["CLayerCompileCacheField"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32, [3] = Enc.U32, [4] = Enc.U32, [5] = Enc.U32 },
        ["CLayerCompileCacheSampler"] = new() { [0] = Enc.U32, [1] = Enc.U32, [3] = Enc.U32, [4] = Enc.U32 },
        ["CParticleNodeSamplerData_Curve"] = new()
        {
            [0] = Enc.A8, [1] = Enc.A8, [7] = Enc.U64, [9] = Enc.U32, [10] = Enc.U32, [11] = Enc.V4, [14] = Enc.U32, [15] = Enc.U8,
            [16] = Enc.A4, [17] = Enc.A4, [18] = Enc.A4,
        },
        ["CParticleNodeSamplerData_Shape"] = new()
        {
            [0] = Enc.A8, [1] = Enc.A8, [3] = Enc.U32, [7] = Enc.U64, [9] = Enc.U32, [10] = Enc.U8, [11] = Enc.U8, [12] = Enc.U32,
            [13] = Enc.V3, [14] = Enc.V3, [15] = Enc.U32, [16] = Enc.V3, [17] = Enc.U32, [18] = Enc.U32, [20] = Enc.U32,
            [21] = Enc.U8, [22] = Enc.V3, [23] = Enc.U32, [24] = Enc.V3, [25] = Enc.U32,
        },
        ["CParticleNodeSamplerData_EventStream"] = new() { [10] = Enc.A4 },
        ["CLayerCompileCacheEvent"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.A4, [3] = Enc.U32 },
        ["CLayerCompileCacheEventPayload"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32, [3] = Enc.U32, [4] = Enc.U32, [5] = Enc.U32 },
        ["CCompilerBlobCache"] = new() { [1] = Enc.U32, [2] = Enc.A4, [3] = Enc.A4, [4] = Enc.A4, [5] = Enc.U32 },
        ["CCompilerBlobCacheExternal"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32, [3] = Enc.U32, [4] = Enc.U32, [5] = Enc.U32, [6] = Enc.U32, [7] = Enc.U32 },
        ["CCompilerBlobCacheEntryPoint"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32 },
        ["CCompilerBlobCacheFunctionDef"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32, [3] = Enc.A4 },
        ["CCompilerBlobCacheFunctionArg"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32 },
        ["CLayerCompileCacheRenderer"] = new() { [0] = Enc.U32, [1] = Enc.A4, [2] = Enc.A4, [3] = Enc.U32, [4] = Enc.U32 },
        ["CLayerCompileCacheRendererParticleInput"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32, [3] = Enc.U32 },
        ["CLayerCompileCacheRendererProperty"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.V4, [3] = Enc.U32 },
        ["CLayerCompileCacheSpatialLayer"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32, [3] = Enc.U32, [4] = Enc.A4 },
        ["CLayerCompileCacheSpatialLayerPayload"] = new() { [0] = Enc.U32, [1] = Enc.U32, [2] = Enc.U32 },
    };

    /// <summary>Encodings tried, in order, for a field the schema does not name.</summary>
    private static readonly Enc[] Fallback = [Enc.U32, Enc.A4, Enc.V3, Enc.V4, Enc.U8, Enc.U16, Enc.U64, Enc.A1, Enc.A2, Enc.A8, Enc.A12, Enc.A16];

    private bool TryField(int rec, int field, out Field f)
    {
        f = default;
        if ((uint)rec >= (uint)_records.Count) return false;
        var r = _records[rec];
        r.Fields ??= Walk(ClassOf(rec), r) ?? [];
        return r.Fields.TryGetValue(field, out f);
    }

    private static Dictionary<int, Field>? Walk(string className, Rec r)
    {
        Schema.TryGetValue(className, out var schema);
        var body = r.Body;
        var acc = new Dictionary<int, Field>();
        int budget = 200_000;

        bool Step(int k, int p, int last)
        {
            if (--budget < 0) return false;
            if (k == r.FieldCount) return p == body.Length;
            if (p + 2 > body.Length) return false;
            int fi = BitConverter.ToUInt16(body, p);
            if (fi <= last) return false;
            int q = p + 2;
            ReadOnlySpan<Enc> encs = schema is not null && schema.TryGetValue(fi, out var known) ? [known] : Fallback;
            foreach (var enc in encs)
            {
                int e = End(body, q, enc);
                if (e < 0) continue;
                bool isLast = k == r.FieldCount - 1;
                if (!isLast && !(e + 2 <= body.Length && BitConverter.ToUInt16(body, e) > fi)) continue;
                acc[fi] = new Field(enc, q, e);
                if (Step(k + 1, e, fi)) return true;
                acc.Remove(fi);
            }
            return false;
        }
        return Step(0, 0, -1) ? acc : null;
    }

    private static int End(byte[] body, int p, Enc enc)
    {
        int fixedLen = enc switch { Enc.U8 => 1, Enc.U16 => 2, Enc.U32 => 4, Enc.U64 => 8, Enc.V3 => 12, Enc.V4 => 16, _ => 0 };
        if (fixedLen > 0) return p + fixedLen <= body.Length ? p + fixedLen : -1;
        if (p + 4 > body.Length) return -1;
        int n = BitConverter.ToInt32(body, p);
        if (n < 0 || n > 1_000_000) return -1;
        int elem = enc switch { Enc.A1 => 1, Enc.A2 => 2, Enc.A4 => 4, Enc.A8 => 8, Enc.A12 => 12, _ => 16 };
        long e = p + 4 + (long)n * elem;
        return e <= body.Length ? (int)e : -1;
    }
}
