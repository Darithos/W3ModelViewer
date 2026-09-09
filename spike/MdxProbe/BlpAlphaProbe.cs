using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Decodes a BLP and writes its colour and its alpha channel as two PNGs, so the alpha can be
/// looked at rather than inferred from a histogram. A histogram cannot tell an inverted mask from
/// a texture that really is mostly transparent; the picture can.
/// </summary>
public static class BlpAlphaProbe
{
    public static int Run(string target, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.blp", SearchOption.AllDirectories)
            : [target];

        foreach (string file in files)
        {
            var img = BlpReader.Decode(File.ReadAllBytes(file));
            if (img is null) { Console.WriteLine($"{Path.GetFileName(file)}: could not decode"); continue; }

            string stem = Path.GetFileNameWithoutExtension(file);
            var colour = new RgbaImage { Width = img.Width, Height = img.Height, Pixels = (byte[])img.Pixels.Clone() };
            var alpha = new RgbaImage { Width = img.Width, Height = img.Height, Pixels = new byte[img.Pixels.Length] };
            for (int i = 0; i < img.Pixels.Length; i += 4)
            {
                colour.Pixels[i + 3] = 255;                     // colour only, so it is visible
                byte a = img.Pixels[i + 3];
                alpha.Pixels[i] = alpha.Pixels[i + 1] = alpha.Pixels[i + 2] = a;
                alpha.Pixels[i + 3] = 255;                      // alpha as greyscale: white = opaque
            }
            // The same colour with R and B exchanged: PngWriter treats Pixels as BGRA, so this pair
            // shows which order the decoder actually produced.
            var swapped = new RgbaImage { Width = img.Width, Height = img.Height, Pixels = (byte[])colour.Pixels.Clone() };
            for (int i = 0; i < swapped.Pixels.Length; i += 4)
                (swapped.Pixels[i], swapped.Pixels[i + 2]) = (swapped.Pixels[i + 2], swapped.Pixels[i]);
            File.WriteAllBytes(Path.Combine(outDir, stem + "_rgb_swapped.png"), PngWriter.Write(swapped));

            File.WriteAllBytes(Path.Combine(outDir, stem + "_rgb.png"), PngWriter.Write(colour));
            File.WriteAllBytes(Path.Combine(outDir, stem + "_alpha.png"), PngWriter.Write(alpha));
            Console.WriteLine($"{stem}: {img.Width}x{img.Height} -> {stem}_rgb.png + {stem}_alpha.png");
        }
        return 0;
    }
}
