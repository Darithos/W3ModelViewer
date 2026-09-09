using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer;

/// <summary>
/// Wires the platform JPEG codec into <see cref="BlpReader"/>. Core stays imaging-free, so the
/// JPEG payload of a BLP1 texture — the format most classic custom models ship — can only be
/// decoded once a host supplies a codec.
/// </summary>
/// <remarks>
/// This file is compiled into the MdxProbe spike as well as the app. Left unwired, BlpReader
/// returns null for every JPEG-content BLP and the caller reports the texture as missing, so a
/// probe without it would claim custom models ship no textures at all.
/// </remarks>
internal static class BlpJpegCodec
{
    public static void Install() => BlpReader.JpegDecode = Decode;

    // Every alpha-carrying BLP1 decodes as a four-component Cmyk32 frame, whose bytes arrive as
    // R,G,B,A — NOT the B,G,R,A the format is usually described as storing. Taking them raw as BGRA
    // exchanged red and blue on every custom skin: elf faces came out blue, gold trim came out
    // steel. Verified by dumping both orders (MdxProbe --blp) — only the swapped one is a plausible
    // skin. Three-component frames have no sample among real custom models, so they are left to the
    // platform conversion rather than guessed at.
    private static RgbaImage? Decode(byte[] jpeg)
    {
        try
        {
            var decoder = new JpegBitmapDecoder(new MemoryStream(jpeg),
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            int w = frame.PixelWidth, h = frame.PixelHeight;

            if (frame.Format == PixelFormats.Cmyk32)
            {
                var raw = new byte[w * h * 4];
                frame.CopyPixels(raw, w * 4, 0);
                for (int i = 0; i < raw.Length; i += 4)
                    (raw[i], raw[i + 2]) = (raw[i + 2], raw[i]);     // R,G,B,A -> B,G,R,A
                return new RgbaImage { Width = w, Height = h, Pixels = raw };
            }

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var px = new byte[w * h * 4];
            converted.CopyPixels(px, w * 4, 0);
            return new RgbaImage { Width = w, Height = h, Pixels = px };
        }
        catch
        {
            return null;    // undecodable -> caller falls back to a placeholder
        }
    }
}
