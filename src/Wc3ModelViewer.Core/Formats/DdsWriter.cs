namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// Writes an <see cref="RgbaImage"/> as a BC3 (DXT5) block-compressed DDS with a full mip chain —
/// the format the StarCraft II editor expects for model textures.
/// </summary>
/// <remarks>
/// This used to emit uncompressed 32-bit BGRA on the assumption that the SC2 editor re-encodes on
/// import. It does not — Blizzard's own model textures are DXT5 (e.g. marine_diffuse /
/// marine_normal), so we match that. BC3 keeps the alpha channel the diffuse uses for cut-out
/// coverage, and cuts size to a quarter (a 2048×2048 diffuse goes from ~21&#160;MB to ~5&#160;MB).
///
/// Correction to an earlier note here: the editor's ACCESS_VIOLATION was blamed on a large
/// uncompressed surface failing to allocate in the Archive Browser preview. That diagnosis was
/// wrong. The crash was in the model, not the texture — the <c>MAT_</c> flag pair
/// <c>geometry_visible | unfogged</c> the exporter wrote on every material (see
/// <see cref="Convert.M3Exporter"/>). Size and format conformance are the reasons to keep BC3;
/// avoiding a crash is not.
/// </remarks>
public static class DdsWriter
{
    public static byte[] Write(RgbaImage image)
    {
        var mips = BuildMipChain(image);
        int dataSize = mips.Sum(m => Blocks(m.Width) * Blocks(m.Height) * 16);   // BC3 = 16 B / 4×4 block

        using var ms = new MemoryStream(128 + dataSize);
        using var w = new BinaryWriter(ms);

        const uint DDSD_CAPS = 0x1, DDSD_HEIGHT = 0x2, DDSD_WIDTH = 0x4, DDSD_PIXELFORMAT = 0x1000,
                   DDSD_MIPMAPCOUNT = 0x20000, DDSD_LINEARSIZE = 0x80000;
        const uint DDPF_FOURCC = 0x4;
        const uint DDSCAPS_COMPLEX = 0x8, DDSCAPS_TEXTURE = 0x1000, DDSCAPS_MIPMAP = 0x400000;

        w.Write("DDS "u8);
        w.Write(124);                                          // dwSize
        w.Write(DDSD_CAPS | DDSD_HEIGHT | DDSD_WIDTH | DDSD_PIXELFORMAT | DDSD_MIPMAPCOUNT | DDSD_LINEARSIZE);
        w.Write(image.Height);
        w.Write(image.Width);
        w.Write(Blocks(image.Width) * Blocks(image.Height) * 16);  // linear size of the top mip
        w.Write(0);                                            // depth
        w.Write(mips.Count);
        for (int i = 0; i < 11; i++) w.Write(0);               // reserved

        w.Write(32);                                           // pixel format dwSize
        w.Write(DDPF_FOURCC);
        w.Write("DXT5"u8);                                     // fourCC
        w.Write(0);                                            // bit count (unused for FOURCC)
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);        // RGBA masks (unused for FOURCC)

        w.Write(DDSCAPS_TEXTURE | DDSCAPS_COMPLEX | DDSCAPS_MIPMAP);
        w.Write(0); w.Write(0); w.Write(0); w.Write(0);        // caps2..reserved

        foreach (var mip in mips) CompressBc3(mip, w);
        return ms.ToArray();
    }

    private static int Blocks(int dim) => Math.Max(1, (dim + 3) / 4);

    // ---------------------------------------------------------------- BC3 (DXT5)

    /// <summary>Compresses one image level to BC3, block by block. Pixels are BGRA in memory.</summary>
    private static void CompressBc3(RgbaImage img, BinaryWriter w)
    {
        int bx = Blocks(img.Width), by = Blocks(img.Height);
        Span<byte> r = stackalloc byte[16], g = stackalloc byte[16], b = stackalloc byte[16], a = stackalloc byte[16];
        for (int byi = 0; byi < by; byi++)
        {
            for (int bxi = 0; bxi < bx; bxi++)
            {
                for (int py = 0; py < 4; py++)
                {
                    int sy = Math.Min(byi * 4 + py, img.Height - 1);
                    for (int px = 0; px < 4; px++)
                    {
                        int sx = Math.Min(bxi * 4 + px, img.Width - 1);
                        int s = (sy * img.Width + sx) * 4;
                        int t = py * 4 + px;
                        b[t] = img.Pixels[s]; g[t] = img.Pixels[s + 1]; r[t] = img.Pixels[s + 2]; a[t] = img.Pixels[s + 3];
                    }
                }
                WriteAlphaBlock(a, w);
                WriteColorBlock(r, g, b, w);
            }
        }
    }

    /// <summary>DXT5 alpha: two 8-bit endpoints + 16 three-bit indices into an 8-value ramp.</summary>
    private static void WriteAlphaBlock(ReadOnlySpan<byte> a, BinaryWriter w)
    {
        byte lo = 255, hi = 0;
        for (int i = 0; i < 16; i++) { if (a[i] < lo) lo = a[i]; if (a[i] > hi) hi = a[i]; }

        // 8-value interpolation mode requires endpoint0 > endpoint1. When alpha is flat the two are
        // equal; every index then resolves to endpoint0 = the exact value, so the block stays correct.
        byte a0 = hi, a1 = lo;
        Span<int> ramp = stackalloc int[8];
        ramp[0] = a0; ramp[1] = a1;
        for (int i = 2; i < 8; i++) ramp[i] = ((8 - i) * a0 + (i - 1) * a1) / 7;

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestErr = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int e = a[i] - ramp[k]; e *= e;
                if (e < bestErr) { bestErr = e; best = k; }
            }
            bits |= (ulong)best << (i * 3);
        }

        w.Write(a0); w.Write(a1);
        for (int i = 0; i < 6; i++) w.Write((byte)(bits >> (i * 8)));   // 48 index bits, little-endian
    }

    /// <summary>DXT5 colour: two RGB565 endpoints (4-colour mode) + 16 two-bit indices.</summary>
    private static void WriteColorBlock(ReadOnlySpan<byte> r, ReadOnlySpan<byte> g, ReadOnlySpan<byte> b, BinaryWriter w)
    {
        byte rlo = 255, glo = 255, blo = 255, rhi = 0, ghi = 0, bhi = 0;
        for (int i = 0; i < 16; i++)
        {
            if (r[i] < rlo) rlo = r[i]; if (r[i] > rhi) rhi = r[i];
            if (g[i] < glo) glo = g[i]; if (g[i] > ghi) ghi = g[i];
            if (b[i] < blo) blo = b[i]; if (b[i] > bhi) bhi = b[i];
        }

        ushort e0 = Pack565(rhi, ghi, bhi), e1 = Pack565(rlo, glo, blo);
        if (e0 < e1) (e0, e1) = (e1, e0);   // endpoint0 ≥ endpoint1 selects the opaque 4-colour ramp

        (int pr0, int pg0, int pb0) = Unpack565(e0);
        (int pr1, int pg1, int pb1) = Unpack565(e1);
        Span<int> pr = stackalloc int[4], pg = stackalloc int[4], pb = stackalloc int[4];
        pr[0] = pr0; pg[0] = pg0; pb[0] = pb0;
        pr[1] = pr1; pg[1] = pg1; pb[1] = pb1;
        pr[2] = (2 * pr0 + pr1) / 3; pg[2] = (2 * pg0 + pg1) / 3; pb[2] = (2 * pb0 + pb1) / 3;
        pr[3] = (pr0 + 2 * pr1) / 3; pg[3] = (pg0 + 2 * pg1) / 3; pb[3] = (pb0 + 2 * pb1) / 3;

        uint bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bestErr = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int dr = r[i] - pr[k], dg = g[i] - pg[k], db = b[i] - pb[k];
                int e = dr * dr + dg * dg + db * db;
                if (e < bestErr) { bestErr = e; best = k; }
            }
            bits |= (uint)best << (i * 2);
        }

        w.Write(e0); w.Write(e1); w.Write(bits);
    }

    private static ushort Pack565(int r, int g, int b) => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    private static (int R, int G, int B) Unpack565(ushort c)
    {
        int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, b = c & 0x1F;
        return ((r << 3) | (r >> 2), (g << 2) | (g >> 4), (b << 3) | (b >> 2));   // replicate high bits
    }

    // ---------------------------------------------------------------- mip chain

    private static List<RgbaImage> BuildMipChain(RgbaImage top)
    {
        var mips = new List<RgbaImage> { top };
        var current = top;
        while (current.Width > 1 || current.Height > 1)
        {
            current = Halve(current);
            mips.Add(current);
        }
        return mips;
    }

    /// <summary>Box-filter downsample by 2, alpha-weighted so cutout edges do not darken.</summary>
    private static RgbaImage Halve(RgbaImage src)
    {
        int w = Math.Max(1, src.Width / 2);
        int h = Math.Max(1, src.Height / 2);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int b = 0, g = 0, r = 0, a = 0, wsum = 0, n = 0;
                for (int dy = 0; dy < 2; dy++)
                {
                    int sy = Math.Min(y * 2 + dy, src.Height - 1);
                    for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = Math.Min(x * 2 + dx, src.Width - 1);
                        int s = (sy * src.Width + sx) * 4;
                        int sa = src.Pixels[s + 3];
                        b += src.Pixels[s] * sa; g += src.Pixels[s + 1] * sa; r += src.Pixels[s + 2] * sa;
                        a += sa; wsum += sa; n++;
                    }
                }
                int d = (y * w + x) * 4;
                if (wsum > 0)
                {
                    px[d] = (byte)(b / wsum); px[d + 1] = (byte)(g / wsum); px[d + 2] = (byte)(r / wsum);
                }
                px[d + 3] = (byte)(a / n);
            }
        }
        return new RgbaImage { Width = w, Height = h, Pixels = px };
    }
}
