namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// A baseline JPEG decoder that returns the raw component planes and applies <b>no colour
/// transform whatsoever</b>.
/// </summary>
/// <remarks>
/// This exists because of what Warcraft III's BLP1 textures actually contain: a four-component
/// SOF0 stream, every component sampled 1x1, <b>no Adobe APP14 marker and no JFIF header</b>. The
/// four channels are plain B, G, R and A planes that Blizzard's encoder DCT-coded independently —
/// there is no colour space involved at all.
/// <para>
/// A general-purpose decoder cannot know that. Handed four components with no marker to identify
/// them, the platform codec has to guess, and Windows Imaging Component guesses a CMYK-family
/// space and colour-transforms the planes on the way out. The result is close enough to look like
/// a real texture — which is why it survived so long — but every skin and hair tone is wrong, and
/// the channels come back in an order that needs an unexplainable swap to look plausible.
/// </para>
/// <para>
/// Decoding it here removes the guess: the planes come out exactly as they went in. Only the
/// profile Warcraft III actually uses is supported (SOF0, 8-bit, no subsampling); anything else
/// returns null so the caller can fall back.
/// </para>
/// </remarks>
public static class JpegBaseline
{
    /// <summary>Component planes, each <c>Width * Height</c> bytes, in the file's own order.</summary>
    public sealed class Planes
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[][] Components { get; init; }
    }

    private sealed class HuffTable
    {
        // Canonical JPEG Huffman: for each code length 1..16, the smallest code and the index of
        // its first symbol. Decoding walks lengths, which is enough for baseline's short codes.
        public readonly int[] MinCode = new int[17];
        public readonly int[] MaxCode = new int[17];
        public readonly int[] ValPtr = new int[17];
        public byte[] Values = [];
    }

    private sealed class Component
    {
        public int Id, H, V, QuantTable, DcTable, AcTable, Dc;
        public byte[] Pixels = [];
        public int BlocksWide, BlocksHigh;
    }

    public static Planes? Decode(byte[] d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;

        var quant = new int[4][];
        var dcTables = new HuffTable[4];
        var acTables = new HuffTable[4];
        Component[] comps = [];
        int width = 0, height = 0, restartInterval = 0;

        int p = 2;
        while (p + 3 < d.Length)
        {
            if (d[p] != 0xFF) { p++; continue; }
            byte marker = d[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
            if (marker == 0xD9) break;

            int length = (d[p + 2] << 8) | d[p + 3];
            int seg = p + 4, segEnd = p + 2 + length;
            if (segEnd > d.Length) return null;

            switch (marker)
            {
                case 0xDB:                                  // DQT
                    while (seg < segEnd)
                    {
                        int precision = d[seg] >> 4, id = d[seg] & 15;
                        seg++;
                        if (id > 3) return null;
                        var table = new int[64];
                        for (int i = 0; i < 64; i++)
                        {
                            table[i] = precision == 0 ? d[seg] : (d[seg] << 8) | d[seg + 1];
                            seg += precision == 0 ? 1 : 2;
                        }
                        quant[id] = table;
                    }
                    break;

                case 0xC4:                                  // DHT
                    while (seg < segEnd)
                    {
                        int cls = d[seg] >> 4, id = d[seg] & 15;
                        seg++;
                        if (id > 3) return null;
                        var counts = new int[17];
                        int total = 0;
                        for (int i = 1; i <= 16; i++) { counts[i] = d[seg + i - 1]; total += counts[i]; }
                        seg += 16;
                        var values = new byte[total];
                        Array.Copy(d, seg, values, 0, total);
                        seg += total;

                        var t = new HuffTable { Values = values };
                        int code = 0, k = 0;
                        for (int len = 1; len <= 16; len++)
                        {
                            t.ValPtr[len] = k;
                            t.MinCode[len] = code;
                            code += counts[len];
                            k += counts[len];
                            t.MaxCode[len] = counts[len] > 0 ? code - 1 : -1;
                            code <<= 1;
                        }
                        if (cls == 0) dcTables[id] = t; else acTables[id] = t;
                    }
                    break;

                case 0xC0:                                  // SOF0 — the only profile supported
                    if (d[seg] != 8) return null;           // 8-bit samples only
                    height = (d[seg + 1] << 8) | d[seg + 2];
                    width = (d[seg + 3] << 8) | d[seg + 4];
                    int n = d[seg + 5];
                    if (n is < 1 or > 4 || width <= 0 || height <= 0) return null;
                    comps = new Component[n];
                    for (int i = 0; i < n; i++)
                    {
                        int at = seg + 6 + i * 3;
                        comps[i] = new Component
                        {
                            Id = d[at], H = d[at + 1] >> 4, V = d[at + 1] & 15, QuantTable = d[at + 2],
                        };
                        // Subsampling never occurs in Warcraft III's BLPs; refusing it is better
                        // than shipping an untested upsampler.
                        if (comps[i].H != 1 || comps[i].V != 1) return null;
                    }
                    break;

                case 0xC1: case 0xC2: case 0xC3: case 0xC5: case 0xC6: case 0xC7:
                case 0xC9: case 0xCA: case 0xCB: case 0xCD: case 0xCE: case 0xCF:
                    return null;                            // progressive / arithmetic / lossless

                case 0xDD:                                  // DRI
                    restartInterval = (d[seg] << 8) | d[seg + 1];
                    break;

                case 0xDA:                                  // SOS — entropy-coded data follows
                {
                    if (comps.Length == 0) return null;
                    int ns = d[seg];
                    for (int i = 0; i < ns; i++)
                    {
                        int id = d[seg + 1 + i * 2], tables = d[seg + 2 + i * 2];
                        var c = Array.Find(comps, x => x.Id == id);
                        if (c is null) return null;
                        c.DcTable = tables >> 4;
                        c.AcTable = tables & 15;
                    }
                    return Scan(d, segEnd, comps, quant, dcTables, acTables, width, height, restartInterval);
                }
            }
            p = segEnd;
        }
        return null;
    }

    private static Planes? Scan(byte[] d, int start, Component[] comps, int[]?[] quant,
                                HuffTable?[] dcTables, HuffTable?[] acTables,
                                int width, int height, int restartInterval)
    {
        int blocksWide = (width + 7) / 8, blocksHigh = (height + 7) / 8;
        foreach (var c in comps)
        {
            c.BlocksWide = blocksWide;
            c.BlocksHigh = blocksHigh;
            c.Pixels = new byte[blocksWide * 8 * blocksHigh * 8];
        }

        var reader = new BitReader(d, start);
        var block = new int[64];
        var pixels = new byte[64];
        int mcu = 0, sinceRestart = 0;

        for (int by = 0; by < blocksHigh; by++)
            for (int bx = 0; bx < blocksWide; bx++, mcu++)
            {
                if (restartInterval > 0 && sinceRestart == restartInterval)
                {
                    if (!reader.RestartMarker()) return null;
                    foreach (var c in comps) c.Dc = 0;
                    sinceRestart = 0;
                }
                sinceRestart++;

                foreach (var c in comps)
                {
                    var dc = dcTables[c.DcTable];
                    var ac = acTables[c.AcTable];
                    var q = quant[c.QuantTable];
                    if (dc is null || ac is null || q is null) return null;

                    Array.Clear(block);

                    int s = Decode(reader, dc);
                    if (s < 0) return null;
                    c.Dc += s == 0 ? 0 : reader.Receive(s);
                    block[0] = c.Dc * q[0];

                    for (int k = 1; k < 64;)
                    {
                        int rs = Decode(reader, ac);
                        if (rs < 0) return null;
                        int run = rs >> 4, size = rs & 15;
                        if (size == 0)
                        {
                            if (run != 15) break;            // EOB
                            k += 16;
                            continue;
                        }
                        k += run;
                        if (k > 63) break;
                        block[ZigZag[k]] = reader.Receive(size) * q[k];
                        k++;
                    }

                    Idct(block, pixels);
                    int rowStride = blocksWide * 8;
                    for (int y = 0; y < 8; y++)
                        Array.Copy(pixels, y * 8, c.Pixels, (by * 8 + y) * rowStride + bx * 8, 8);
                }
            }

        // Trim the 8-aligned planes back to the declared size.
        var planes = new byte[comps.Length][];
        for (int i = 0; i < comps.Length; i++)
        {
            int stride = blocksWide * 8;
            var outPlane = new byte[width * height];
            for (int y = 0; y < height; y++)
                Array.Copy(comps[i].Pixels, y * stride, outPlane, y * width, width);
            planes[i] = outPlane;
        }
        return new Planes { Width = width, Height = height, Components = planes };
    }

    private static int Decode(BitReader r, HuffTable t)
    {
        int code = 0;
        for (int len = 1; len <= 16; len++)
        {
            code = (code << 1) | r.Bit();
            if (t.MaxCode[len] >= 0 && code <= t.MaxCode[len])
            {
                int index = t.ValPtr[len] + code - t.MinCode[len];
                return (uint)index < (uint)t.Values.Length ? t.Values[index] : -1;
            }
        }
        return -1;
    }

    private static readonly int[] ZigZag =
    [
         0,  1,  8, 16,  9,  2,  3, 10, 17, 24, 32, 25, 18, 11,  4,  5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13,  6,  7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Separable float IDCT, then the +128 level shift and a clamp to 0..255.</summary>
    private static void Idct(int[] block, byte[] outPixels)
    {
        Span<double> tmp = stackalloc double[64];

        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                double sum = 0;
                for (int u = 0; u < 8; u++)
                    sum += Cu[u] * block[y * 8 + u] * CosTable[x * 8 + u];
                tmp[y * 8 + x] = sum;
            }

        for (int x = 0; x < 8; x++)
            for (int y = 0; y < 8; y++)
            {
                double sum = 0;
                for (int v = 0; v < 8; v++)
                    sum += Cu[v] * tmp[v * 8 + x] * CosTable[y * 8 + v];
                int value = (int)Math.Round(sum / 4 + 128);
                outPixels[y * 8 + x] = (byte)Math.Clamp(value, 0, 255);
            }
    }

    private static readonly double[] CosTable = BuildCos();
    private static readonly double[] Cu = BuildCu();

    private static double[] BuildCos()
    {
        var t = new double[64];
        for (int x = 0; x < 8; x++)
            for (int u = 0; u < 8; u++)
                t[x * 8 + u] = Math.Cos((2 * x + 1) * u * Math.PI / 16);
        return t;
    }

    private static double[] BuildCu()
    {
        var c = new double[8];
        c[0] = 1 / Math.Sqrt(2);
        for (int i = 1; i < 8; i++) c[i] = 1;
        return c;
    }

    /// <summary>MSB-first bit reader that unstuffs <c>FF 00</c> and stops at any other marker.</summary>
    private sealed class BitReader(byte[] data, int position)
    {
        private int _p = position;
        private int _bits, _count;

        public int Bit()
        {
            if (_count == 0)
            {
                if (_p >= data.Length) return 0;            // ran dry: feed zeros, caller clamps
                byte b = data[_p++];
                if (b == 0xFF)
                {
                    byte next = _p < data.Length ? data[_p] : (byte)0;
                    if (next == 0x00) _p++;                  // stuffed byte
                    else { _p--; return 0; }                 // a real marker: stop consuming
                }
                _bits = b;
                _count = 8;
            }
            _count--;
            return (_bits >> _count) & 1;
        }

        /// <summary>Reads <paramref name="size"/> bits as a signed JPEG coefficient.</summary>
        public int Receive(int size)
        {
            int v = 0;
            for (int i = 0; i < size; i++) v = (v << 1) | Bit();
            return v < (1 << (size - 1)) ? v - (1 << size) + 1 : v;
        }

        /// <summary>Consumes an RSTn marker, discarding any partial byte before it.</summary>
        public bool RestartMarker()
        {
            _count = 0;
            while (_p + 1 < data.Length)
            {
                if (data[_p] == 0xFF && data[_p + 1] >= 0xD0 && data[_p + 1] <= 0xD7)
                {
                    _p += 2;
                    return true;
                }
                _p++;
            }
            return false;
        }
    }
}
