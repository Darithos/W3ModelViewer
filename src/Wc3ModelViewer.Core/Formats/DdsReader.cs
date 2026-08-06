namespace Wc3ModelViewer.Core.Formats;

/// <summary>A decoded texture: tightly-packed 32-bit BGRA pixels, top-down.</summary>
public sealed class RgbaImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>BGRA byte order (the WPF/Windows-native layout), row-major, no padding.</summary>
    public required byte[] Pixels { get; init; }

    public static RgbaImage Solid(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = a; }
        return new RgbaImage { Width = width, Height = height, Pixels = px };
    }

    /// <summary>True when any pixel's alpha is meaningfully below opaque.</summary>
    public bool HasTransparency()
    {
        for (int i = 3; i < Pixels.Length; i += 4)
            if (Pixels[i] < 250) return true;
        return false;
    }
}

/// <summary>
/// Decodes the DDS containers Warcraft III Reforged actually ships — DXT1 (BC1), DXT5 (BC3) and
/// ATI2 (BC5) — plus plain uncompressed BGRA, to <see cref="RgbaImage"/>.
/// </summary>
/// <remarks>
/// Build 2.0.4 contains no BLP files at all: even classic-art texture references (<c>*.blp</c> in
/// TEXS) resolve to <c>.dds</c> in the storage, so these three codecs cover the whole game.
/// ATI2 normal maps carry only X and Y (in the two BC4 halves); Z is reconstructed at decode so the
/// viewer can treat every texture uniformly.
/// </remarks>
public static class DdsReader
{
    private const uint FourCcFlag = 0x4;      // DDPF_FOURCC
    private const uint AlphaFlag = 0x1;       // DDPF_ALPHAPIXELS

    public static bool LooksLikeDds(byte[] d) => d.Length > 128 && d[0] == 'D' && d[1] == 'D' && d[2] == 'S' && d[3] == ' ';

    public static RgbaImage Decode(byte[] d)
    {
        if (!LooksLikeDds(d)) throw new InvalidDataException("Not a DDS file.");

        int height = BitConverter.ToInt32(d, 12);
        int width = BitConverter.ToInt32(d, 16);
        uint pfFlags = BitConverter.ToUInt32(d, 80);
        string fourCc = System.Text.Encoding.ASCII.GetString(d, 84, 4);
        uint rgbBits = BitConverter.ToUInt32(d, 88);

        int dataStart = 128;
        if ((pfFlags & FourCcFlag) != 0 && fourCc == "DX10")
        {
            uint dxgi = BitConverter.ToUInt32(d, 128);
            dataStart = 148;
            fourCc = dxgi switch
            {
                71 or 72 => "DXT1",
                77 or 78 => "DXT5",
                83 or 84 => "ATI2",
                28 or 87 => "RGBA",
                _ => throw new NotSupportedException($"DDS DXGI format {dxgi} is not supported."),
            };
        }

        if ((pfFlags & FourCcFlag) == 0)
        {
            // Uncompressed. Warcraft III's are 32-bit BGRA when they occur at all.
            if (rgbBits != 32) throw new NotSupportedException($"Uncompressed DDS with {rgbBits} bpp is not supported.");
            var px = new byte[width * height * 4];
            Array.Copy(d, dataStart, px, 0, Math.Min(px.Length, d.Length - dataStart));
            if ((BitConverter.ToUInt32(d, 76) & AlphaFlag) == 0)
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            return new RgbaImage { Width = width, Height = height, Pixels = px };
        }

        return fourCc switch
        {
            "DXT1" => DecodeBlocks(d, dataStart, width, height, 8, DecodeBc1Block),
            "DXT5" => DecodeBlocks(d, dataStart, width, height, 16, DecodeBc3Block),
            "ATI2" or "BC5U" => DecodeBlocks(d, dataStart, width, height, 16, DecodeBc5Block),
            "DXT3" => DecodeBlocks(d, dataStart, width, height, 16, DecodeBc2Block),
            "RGBA" => DecodeRgba(d, dataStart, width, height),
            _ => throw new NotSupportedException($"DDS fourCC '{fourCc}' is not supported."),
        };
    }

    private static RgbaImage DecodeRgba(byte[] d, int start, int width, int height)
    {
        var px = new byte[width * height * 4];
        Array.Copy(d, start, px, 0, Math.Min(px.Length, d.Length - start));
        return new RgbaImage { Width = width, Height = height, Pixels = px };
    }

    private delegate void BlockDecoder(byte[] d, int at, Span<byte> block);   // block = 64 BGRA bytes

    private static RgbaImage DecodeBlocks(byte[] d, int start, int width, int height, int blockSize, BlockDecoder decode)
    {
        var px = new byte[width * height * 4];
        int bw = Math.Max(1, (width + 3) / 4);
        int bh = Math.Max(1, (height + 3) / 4);
        Span<byte> block = stackalloc byte[64];

        for (int by = 0; by < bh; by++)
        {
            for (int bx = 0; bx < bw; bx++)
            {
                int at = start + (by * bw + bx) * blockSize;
                if (at + blockSize > d.Length) return new RgbaImage { Width = width, Height = height, Pixels = px };
                decode(d, at, block);

                // Scatter the 4x4 block, clipping at non-multiple-of-4 edges.
                for (int y = 0; y < 4; y++)
                {
                    int py = by * 4 + y;
                    if (py >= height) break;
                    for (int x = 0; x < 4; x++)
                    {
                        int pxx = bx * 4 + x;
                        if (pxx >= width) break;
                        int src = (y * 4 + x) * 4;
                        int dst = (py * width + pxx) * 4;
                        px[dst] = block[src];
                        px[dst + 1] = block[src + 1];
                        px[dst + 2] = block[src + 2];
                        px[dst + 3] = block[src + 3];
                    }
                }
            }
        }
        return new RgbaImage { Width = width, Height = height, Pixels = px };
    }

    // ---- BC1: two RGB565 endpoints + 2-bit indices; the 1-bit-alpha mode when c0 <= c1 ----
    private static void DecodeBc1Block(byte[] d, int at, Span<byte> block)
    {
        ushort c0 = BitConverter.ToUInt16(d, at);
        ushort c1 = BitConverter.ToUInt16(d, at + 2);
        uint bits = BitConverter.ToUInt32(d, at + 4);

        Span<byte> colors = stackalloc byte[16];            // 4x BGRA
        Rgb565(c0, colors);
        Rgb565(c1, colors[4..]);

        if (c0 > c1)
        {
            for (int i = 0; i < 3; i++)
            {
                colors[8 + i] = (byte)((2 * colors[i] + colors[4 + i]) / 3);
                colors[12 + i] = (byte)((colors[i] + 2 * colors[4 + i]) / 3);
            }
            colors[11] = 255; colors[15] = 255;
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                colors[8 + i] = (byte)((colors[i] + colors[4 + i]) / 2);
                colors[12 + i] = 0;
            }
            colors[11] = 255; colors[15] = 0;               // index 3 = transparent black
        }

        for (int p = 0; p < 16; p++)
        {
            int idx = (int)(bits >> (p * 2)) & 3;
            colors.Slice(idx * 4, 4).CopyTo(block.Slice(p * 4, 4));
        }
    }

    // ---- BC2: BC1 colors + 4-bit explicit alpha ----
    private static void DecodeBc2Block(byte[] d, int at, Span<byte> block)
    {
        DecodeBc1ColorsOnly(d, at + 8, block);
        for (int p = 0; p < 16; p++)
        {
            int nibble = (d[at + p / 2] >> ((p & 1) * 4)) & 0xF;
            block[p * 4 + 3] = (byte)(nibble * 17);
        }
    }

    // ---- BC3: BC4 alpha block + BC1 color block ----
    private static void DecodeBc3Block(byte[] d, int at, Span<byte> block)
    {
        DecodeBc1ColorsOnly(d, at + 8, block);
        Span<byte> alpha = stackalloc byte[16];
        DecodeBc4Half(d, at, alpha);
        for (int p = 0; p < 16; p++) block[p * 4 + 3] = alpha[p];
    }

    // ---- BC5: two BC4 halves = X and Y of a normal; reconstruct Z ----
    private static void DecodeBc5Block(byte[] d, int at, Span<byte> block)
    {
        Span<byte> xs = stackalloc byte[16];
        Span<byte> ys = stackalloc byte[16];
        DecodeBc4Half(d, at, xs);
        DecodeBc4Half(d, at + 8, ys);
        for (int p = 0; p < 16; p++)
        {
            float x = xs[p] / 255f * 2 - 1;
            float y = ys[p] / 255f * 2 - 1;
            float z = MathF.Sqrt(Math.Max(0, 1 - x * x - y * y));
            block[p * 4] = (byte)((z * 0.5f + 0.5f) * 255);     // B = Z
            block[p * 4 + 1] = ys[p];                            // G = Y
            block[p * 4 + 2] = xs[p];                            // R = X
            block[p * 4 + 3] = 255;
        }
    }

    /// <summary>The color half of a BC1/BC3 block, opaque-mode palette, alpha forced 255.</summary>
    private static void DecodeBc1ColorsOnly(byte[] d, int at, Span<byte> block)
    {
        ushort c0 = BitConverter.ToUInt16(d, at);
        ushort c1 = BitConverter.ToUInt16(d, at + 2);
        uint bits = BitConverter.ToUInt32(d, at + 4);

        Span<byte> colors = stackalloc byte[16];
        Rgb565(c0, colors);
        Rgb565(c1, colors[4..]);
        for (int i = 0; i < 3; i++)
        {
            colors[8 + i] = (byte)((2 * colors[i] + colors[4 + i]) / 3);
            colors[12 + i] = (byte)((colors[i] + 2 * colors[4 + i]) / 3);
        }
        colors[11] = 255; colors[15] = 255;

        for (int p = 0; p < 16; p++)
        {
            int idx = (int)(bits >> (p * 2)) & 3;
            colors.Slice(idx * 4, 4).CopyTo(block.Slice(p * 4, 4));
            block[p * 4 + 3] = 255;
        }
    }

    /// <summary>One 8-byte BC4 block: two endpoints + 3-bit indices, 16 single-channel values.</summary>
    private static void DecodeBc4Half(byte[] d, int at, Span<byte> outValues)
    {
        byte a0 = d[at], a1 = d[at + 1];
        Span<byte> pal = stackalloc byte[8];
        pal[0] = a0; pal[1] = a1;
        if (a0 > a1)
            for (int i = 1; i < 7; i++) pal[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
        else
        {
            for (int i = 1; i < 5; i++) pal[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            pal[6] = 0; pal[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)d[at + 2 + i] << (i * 8);
        for (int p = 0; p < 16; p++) outValues[p] = pal[(int)(bits >> (p * 3)) & 7];
    }

    private static void Rgb565(ushort c, Span<byte> bgra)
    {
        int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
        bgra[0] = (byte)(b << 3 | b >> 2);
        bgra[1] = (byte)(g << 2 | g >> 4);
        bgra[2] = (byte)(r << 3 | r >> 2);
        bgra[3] = 255;
    }
}
