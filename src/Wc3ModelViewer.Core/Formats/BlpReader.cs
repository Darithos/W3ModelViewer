namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// Decodes Warcraft III <c>BLP1</c> textures — the format loose custom models (Hive Workshop etc.)
/// ship, which never occurs in the Reforged CASC itself (that stores only DDS).
/// </summary>
/// <remarks>
/// Palettized content decodes natively. JPEG content (the majority of classic unit skins) is a
/// shared JPEG header + per-mip JPEG payloads whose decoded channels are already B,G,R,(A);
/// decoding the JPEG itself is delegated to <see cref="JpegDecode"/>, which the application wires
/// to the platform codec — Core stays free of imaging dependencies.
/// </remarks>
public static class BlpReader
{
    /// <summary>
    /// Decodes one complete JPEG stream to BGRA. Wired up by the app (WPF's JpegBitmapDecoder);
    /// when null, JPEG-content BLPs return null and callers fall back to a placeholder.
    /// </summary>
    public static Func<byte[], RgbaImage?>? JpegDecode { get; set; }

    public static bool LooksLikeBlp(byte[] d) =>
        d.Length > 16 && d[0] == 'B' && d[1] == 'L' && d[2] == 'P' && (d[3] == '1' || d[3] == '2');

    public static RgbaImage? Decode(byte[] d)
    {
        if (!LooksLikeBlp(d)) return null;
        return d[3] == '1' ? DecodeBlp1(d) : null;      // BLP2 is WoW-era; not seen on WC3 models
    }

    /// <summary>
    /// Assembles decoded component planes into BGRA. Warcraft III writes the components in the
    /// order <b>B, G, R, A</b>; a three-component file carries no alpha and is opaque.
    /// </summary>
    private static RgbaImage? FromPlanes(JpegBaseline.Planes? planes)
    {
        if (planes is null || planes.Components.Length < 3) return null;

        int n = planes.Width * planes.Height;
        var px = new byte[n * 4];
        byte[] b = planes.Components[0], g = planes.Components[1], r = planes.Components[2];
        byte[]? a = planes.Components.Length >= 4 ? planes.Components[3] : null;

        for (int i = 0; i < n; i++)
        {
            px[i * 4] = b[i];
            px[i * 4 + 1] = g[i];
            px[i * 4 + 2] = r[i];
            px[i * 4 + 3] = a?[i] ?? (byte)255;
        }
        return new RgbaImage { Width = planes.Width, Height = planes.Height, Pixels = px };
    }

    private static RgbaImage? DecodeBlp1(byte[] d)
    {
        uint compression = BitConverter.ToUInt32(d, 4);   // 0 = JPEG, 1 = palettized
        uint alphaBits = BitConverter.ToUInt32(d, 8);
        int width = BitConverter.ToInt32(d, 12);
        int height = BitConverter.ToInt32(d, 16);
        // +20 pictureType, +24 pictureSubType.
        int mip0Offset = BitConverter.ToInt32(d, 28);
        int mip0Size = BitConverter.ToInt32(d, 28 + 64);

        if (width <= 0 || height <= 0 || mip0Offset <= 0 || mip0Offset + mip0Size > d.Length) return null;

        if (compression == 0)
        {
            // Shared JPEG header immediately after the mip tables, then each mip is header + payload.
            int headerSizeAt = 28 + 128;
            if (headerSizeAt + 4 > d.Length) return null;
            int jpegHeaderSize = BitConverter.ToInt32(d, headerSizeAt);
            if (jpegHeaderSize < 0 || headerSizeAt + 4 + jpegHeaderSize > d.Length) return null;

            var jpeg = new byte[jpegHeaderSize + mip0Size];
            Array.Copy(d, headerSizeAt + 4, jpeg, 0, jpegHeaderSize);
            Array.Copy(d, mip0Offset, jpeg, jpegHeaderSize, mip0Size);

            // Decode it ourselves first. These streams carry four components with no marker saying
            // what they are, so a general codec has to guess a colour space and transforms the
            // planes; the planes are really just B, G, R and A. See JpegBaseline.
            var img = FromPlanes(JpegBaseline.Decode(jpeg));
            bool ownDecode = img is not null;
            img ??= JpegDecode?.Invoke(jpeg);
            if (img is null) return null;

            var px = img.Pixels;
            if (alphaBits == 0)
            {
                // No alpha channel in the file at all — whatever the fourth plane holds is noise.
                for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            }
            else if (!ownDecode)
            {
                // Only the platform fallback needs this. It reads the stream as a CMYK-family image
                // and hands back the fourth plane inverted, so a solid skin arrives as "99% alpha
                // zero" — invisible. Our own decoder returns the plane as written, which is already
                // opacity, and inverting it there would punch the model full of holes instead.
                for (int i = 3; i < px.Length; i += 4) px[i] = (byte)(255 - px[i]);
            }
            return img;
        }

        if (compression == 1)
        {
            // 256-entry BGRA palette right after the header block, then width*height indices per mip,
            // then the alpha plane when alphaBits > 0.
            int paletteAt = 28 + 128;
            if (paletteAt + 1024 > d.Length) return null;

            int pixels = width * height;
            var outPx = new byte[pixels * 4];
            int alphaAt = mip0Offset + pixels;

            for (int p = 0; p < pixels; p++)
            {
                int idx = d[mip0Offset + p];
                int pal = paletteAt + idx * 4;
                outPx[p * 4] = d[pal];
                outPx[p * 4 + 1] = d[pal + 1];
                outPx[p * 4 + 2] = d[pal + 2];
                outPx[p * 4 + 3] = alphaBits switch
                {
                    8 when alphaAt + p < d.Length => d[alphaAt + p],
                    1 when alphaAt + p / 8 < d.Length => (byte)((d[alphaAt + p / 8] >> (p % 8) & 1) * 255),
                    4 when alphaAt + p / 2 < d.Length => (byte)(((d[alphaAt + p / 2] >> (p % 2 * 4)) & 0xF) * 17),
                    0 => 255,
                    _ => 255,
                };
            }
            return new RgbaImage { Width = width, Height = height, Pixels = outPx };
        }

        return null;
    }
}
