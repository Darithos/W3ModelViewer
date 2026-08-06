using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Core stays imaging-free; the platform JPEG codec decodes the JPEG payload of BLP1
        // textures (classic custom models). WC3 stores the components as B,G,R,A, so a Cmyk32
        // frame is taken raw; 3-component frames convert to opaque BGRA normally.
        BlpReader.JpegDecode = jpeg =>
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
        };
    }
}
