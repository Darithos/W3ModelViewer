using System.Buffers.Binary;
using System.IO.Compression;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// Minimal PNG encoder: 8-bit RGBA, filter 0 scanlines, one zlib IDAT. No dependencies, so the
/// Core exporters can emit glTF texture files without touching WPF.
/// </summary>
public static class PngWriter
{
    public static byte[] Write(RgbaImage image)
    {
        using var ms = new MemoryStream();
        ms.Write("\x89PNG\r\n\x1a\n"u8);

        // IHDR: width, height, bit depth 8, color type 6 (RGBA), deflate, filter 0, no interlace.
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], image.Height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR", ihdr);

        // Scanlines: filter byte 0 + RGBA (source pixels are BGRA — swap R/B).
        var raw = new byte[image.Height * (1 + image.Width * 4)];
        int at = 0;
        var px = image.Pixels;
        for (int y = 0; y < image.Height; y++)
        {
            raw[at++] = 0;
            int row = y * image.Width * 4;
            for (int x = 0; x < image.Width; x++)
            {
                int s = row + x * 4;
                raw[at++] = px[s + 2];      // R
                raw[at++] = px[s + 1];      // G
                raw[at++] = px[s];          // B
                raw[at++] = px[s + 3];      // A
            }
        }

        using (var idat = new MemoryStream())
        {
            using (var z = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(raw);
            Chunk(ms, "IDAT", idat.ToArray());
        }

        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);

        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes);
        s.Write(data);

        uint crc = Crc32(typeBytes, Crc32Init);
        crc = Crc32(data, crc);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, ~crc);
        s.Write(crcBytes);
    }

    private const uint Crc32Init = 0xFFFFFFFFu;
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
