using System.Runtime.InteropServices;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>
/// One VM value: four 32-bit lanes, read as floats or integers by the operand's kind. Booleans are
/// all-ones masks. Scalars live in lane 0.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public struct PkValue
{
    [FieldOffset(0)] public float X;
    [FieldOffset(4)] public float Y;
    [FieldOffset(8)] public float Z;
    [FieldOffset(12)] public float W;
    [FieldOffset(0)] public int I0;
    [FieldOffset(4)] public int I1;
    [FieldOffset(8)] public int I2;
    [FieldOffset(12)] public int I3;

    public PkValue(float x, float y = 0, float z = 0, float w = 0) : this() { X = x; Y = y; Z = z; W = w; }

    public float this[int lane]
    {
        readonly get => lane switch { 0 => X, 1 => Y, 2 => Z, _ => W };
        set { switch (lane) { case 0: X = value; break; case 1: Y = value; break; case 2: Z = value; break; default: W = value; break; } }
    }

    public int Int(int lane) => lane switch { 0 => I0, 1 => I1, 2 => I2, _ => I3 };

    public void SetInt(int lane, int v)
    {
        switch (lane) { case 0: I0 = v; break; case 1: I1 = v; break; case 2: I2 = v; break; default: I3 = v; break; }
    }

    public readonly System.Numerics.Vector3 Xyz => new(X, Y, Z);
    public readonly System.Numerics.Vector4 Xyzw => new(X, Y, Z, W);
    public static PkValue From(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);
    public static PkValue From(System.Numerics.Vector4 v) => new(v.X, v.Y, v.Z, v.W);

    public override readonly string ToString() => $"({X:0.###},{Y:0.###},{Z:0.###},{W:0.###})";
}

/// <summary>Where an operand lives. Measured over 54,716 scripts: see <c>mdxres/research/popcornfx-vm.md</c>.</summary>
public enum PkSpace : byte
{
    Const = 0x00,        // the blob's constant table (8 SIMD lanes; the first four are used)
    Reg1 = 0x01,         // register spaces: the compiler splits temporaries by how they vary
    Reg2 = 0x02,
    Reg3 = 0x03,
    Zero = 0x10,         // a typed zero (default rotation, zero offset, invLife 0 = immortal)
    None = 0xFF,         // no value (void call result, unary operand padding)
}

/// <summary>Value kinds carried by every operand. Bool masks, ints and floats of 1–4 lanes.</summary>
public static class PkKind
{
    public const byte Ptr = 0x00;
    public const byte Bool = 0x02, Bool2 = 0x03, Bool3 = 0x04, Bool4 = 0x05;
    public const byte Int = 0x1A, Int2 = 0x1B, Int3 = 0x1C, Int4 = 0x1D;
    public const byte Float = 0x20, Float2 = 0x21, Float3 = 0x22, Float4 = 0x23;
    public const byte Quaternion = 0x25;

    public static bool IsInt(byte k) => k is >= Int and <= Int4;
    public static bool IsBool(byte k) => k is >= Bool and <= Bool4;

    public static int Lanes(byte k) => k switch
    {
        >= Bool and <= Bool4 => k - Bool + 1,
        >= Int and <= Int4 => k - Int + 1,
        >= Float and <= Float4 => k - Float + 1,
        0x24 or Quaternion => 4,
        _ => 1,
    };
}

public readonly record struct PkOperand(ushort Index, PkSpace Space, byte Kind)
{
    public int Lanes => PkKind.Lanes(Kind);
    public bool IsNone => Space == PkSpace.None;
}

public enum PkOp : byte
{
    Load = 0x43,     // dst <- external slot
    Store = 0x44,    // external slot <- src
    CastA = 0x4A,    // float4 <-> quaternion (bit copy)
    CastB = 0x4B,    // bool/int/float conversions
    Vector = 0x4C,   // dst <- (c0, c1, ...)
    Swizzle = 0x4D,  // dst <- src lanes / 0 / 1
    Binary = 0x4E,   // dst <- op(a, b)
    Unary = 0x4F,    // dst <- op(a)
    Binary2 = 0x50,  // min, max, dot, cross, pow
    Ternary = 0x51,  // lerp
    Select = 0x52,   // dst <- c ? b : a
    Call = 0x53,
}

public struct PkInstr
{
    public PkOp Op;
    public byte Sub;
    public PkOperand Dst, A, B, C;
    public ushort Slot;          // Load/Store external slot; Call function index
    public ushort This;          // Call: external slot of the object (0xFFFF = free function)
    public int ArgStart;         // Call: first argument in PkScript.Args
    public byte ArgCount;
    public int Swizzle;          // Swizzle: packed lane codes
    public PkOperand[]? Components;
}

/// <summary>A compiled PopcornFX script (one <c>CCompilerBlobCache</c>) decoded into instructions.</summary>
public sealed class PkScript
{
    /// <summary>Field 1 of the blob: null = spawn, 3 = evolve on the frame of spawn, 4 = evolve.</summary>
    public int? Kind { get; init; }

    public PkValue[] Consts { get; init; } = [];
    public PkInstr[] Code { get; init; } = [];
    public PkOperand[] Args { get; init; } = [];
    public byte[] ArgFlags { get; init; } = [];

    public string[] ExternalNames { get; init; } = [];
    public string[] ExternalTypes { get; init; } = [];
    public string[] FunctionNames { get; init; } = [];

    public int Reg1Count { get; init; }
    public int Reg2Count { get; init; }
    public int Reg3Count { get; init; }

    public static PkScript Decode(PkBakeFile b, int blobRec)
    {
        var words = b.ArrayBytes(blobRec, 2);
        if (words.Length < 36) throw new InvalidDataException($"blob #{blobRec} is too short");
        int constLen = BitConverter.ToInt32(words, 8);
        int codeLen = BitConverter.ToInt32(words, 12);
        if (36 + constLen + codeLen > words.Length) throw new InvalidDataException($"blob #{blobRec} sections overrun");

        var consts = new PkValue[constLen / 32];
        for (int i = 0; i < consts.Length; i++)
            consts[i] = MemoryMarshal.Read<PkValue>(words.AsSpan(36 + i * 32, 16));

        var extNames = new List<string>();
        var extTypes = new List<string>();
        foreach (int e in b.Refs(blobRec, 3))
        {
            extNames.Add(e < 0 ? "" : b.String(e, 0));
            extTypes.Add(e < 0 ? "" : b.String(e, 1));
        }
        var fnNames = b.Refs(blobRec, 4).Select(f => f < 0 ? "" : b.String(f, 0)).ToArray();

        var code = words.AsSpan(36 + constLen, codeLen);
        var instrs = new List<PkInstr>();
        var args = new List<PkOperand>();
        var argFlags = new List<byte>();
        int max1 = 0, max2 = 0, max3 = 0;

        PkOperand Opnd(ReadOnlySpan<byte> c, int at)
        {
            var o = new PkOperand(BitConverter.ToUInt16(c.Slice(at, 2)), (PkSpace)c[at + 2], c[at + 3]);
            switch (o.Space)
            {
                case PkSpace.Reg1: max1 = Math.Max(max1, o.Index + 1); break;
                case PkSpace.Reg2: max2 = Math.Max(max2, o.Index + 1); break;
                case PkSpace.Reg3: max3 = Math.Max(max3, o.Index + 1); break;
            }
            return o;
        }

        int p = 0;
        while (p < code.Length)
        {
            var op = (PkOp)code[p];
            var ins = new PkInstr { Op = op };
            int len;
            switch (op)
            {
                case PkOp.Load:
                case PkOp.Store:
                    ins.Dst = Opnd(code, p + 1);
                    ins.Slot = BitConverter.ToUInt16(code.Slice(p + 5, 2));
                    len = 7;
                    break;
                case PkOp.CastA:
                case PkOp.CastB:
                    ins.Dst = Opnd(code, p + 1); ins.A = Opnd(code, p + 5);
                    len = 9;
                    break;
                case PkOp.Vector:
                {
                    int n = code[p + 1] + 1;
                    ins.Dst = Opnd(code, p + 2);
                    ins.Components = new PkOperand[n];
                    for (int k = 0; k < n; k++) ins.Components[k] = Opnd(code, p + 6 + 4 * k);
                    len = 2 + 4 * (n + 1);
                    break;
                }
                case PkOp.Swizzle:
                    ins.Swizzle = code[p + 1] | code[p + 2] << 8 | code[p + 3] << 16;
                    ins.Dst = Opnd(code, p + 4); ins.A = Opnd(code, p + 8);
                    len = 12;
                    break;
                case PkOp.Binary:
                case PkOp.Binary2:
                    ins.Sub = code[p + 1];
                    ins.Dst = Opnd(code, p + 2); ins.A = Opnd(code, p + 6); ins.B = Opnd(code, p + 10);
                    len = 14;
                    break;
                case PkOp.Unary:
                    ins.Sub = code[p + 1];
                    ins.Dst = Opnd(code, p + 2); ins.A = Opnd(code, p + 6);
                    len = 10;
                    break;
                case PkOp.Ternary:
                    ins.Sub = code[p + 1];
                    ins.Dst = Opnd(code, p + 2); ins.A = Opnd(code, p + 6); ins.B = Opnd(code, p + 10); ins.C = Opnd(code, p + 14);
                    len = 18;
                    break;
                case PkOp.Select:
                    ins.Dst = Opnd(code, p + 1); ins.A = Opnd(code, p + 5); ins.B = Opnd(code, p + 9); ins.C = Opnd(code, p + 13);
                    len = 17;
                    break;
                case PkOp.Call:
                {
                    ins.Sub = code[p + 1];
                    ins.This = BitConverter.ToUInt16(code.Slice(p + 2, 2));
                    ins.Slot = BitConverter.ToUInt16(code.Slice(p + 4, 2));
                    ins.ArgCount = code[p + 6];
                    ins.Dst = Opnd(code, p + 7);
                    ins.ArgStart = args.Count;
                    for (int k = 0; k < ins.ArgCount; k++)
                    {
                        argFlags.Add(code[p + 11 + 5 * k]);
                        args.Add(Opnd(code, p + 12 + 5 * k));
                    }
                    len = 11 + 5 * ins.ArgCount;
                    break;
                }
                default:
                    throw new InvalidDataException($"blob #{blobRec}: unknown opcode 0x{(byte)op:X2} at {p}");
            }
            if (p + len > code.Length) throw new InvalidDataException($"blob #{blobRec}: instruction at {p} overruns the code");
            instrs.Add(ins);
            p += len;
        }

        return new PkScript
        {
            Kind = b.Has(blobRec, 1) ? b.I32(blobRec, 1) : null,
            Consts = consts,
            Code = [.. instrs],
            Args = [.. args],
            ArgFlags = [.. argFlags],
            ExternalNames = [.. extNames],
            ExternalTypes = [.. extTypes],
            FunctionNames = fnNames,
            Reg1Count = max1,
            Reg2Count = max2,
            Reg3Count = max3,
        };
    }

    /// <summary>
    /// Decodes a swizzle's packed lane codes: lanes 0 and 1 take 3 bits at bits 8 and 11, lane 2 takes
    /// 2 bits at bit 14 with a "constant" flag at bit 20, lane 3 takes 3 bits at bit 21. Codes 0–3
    /// select a source lane, 4 is 0.0 and 5 is 1.0 (lane 2 with its flag set is 0.0 or 1.0 by its
    /// 2-bit code). Settled against the scene-intersect and quaternion idioms: www, xyz, (0,0,w),
    /// (0,0,x,y), (1,1,1,x), (x,x,x,1) and the axis masks all decode.
    /// </summary>
    public static int LaneCode(int packed, int lane) => lane switch
    {
        0 => (packed >> 8) & 7,
        1 => (packed >> 11) & 7,
        2 => (packed >> 20 & 1) != 0 ? 4 + ((packed >> 14) & 3) : (packed >> 14) & 3,
        _ => (packed >> 21) & 7,
    };
}
