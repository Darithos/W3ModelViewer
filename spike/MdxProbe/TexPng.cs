using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Writes every TEXS texture a model references to PNG (capped at 512 px) with its mean RGBA, so a
/// "this surface draws black" question starts from what each slot actually holds.
/// </summary>
public static class TexPng
{
    public static int Run(string install, string cascName, string outDir)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var model = MdxReader.Read(storage.TryReadFile(cascName) ?? throw new FileNotFoundException(cascName));
        var cache = new Wc3TextureCache(storage, index) { PreferHd = cascName.Contains("_hd.w3mod") || cascName.Contains("_de.w3mod") };
        Directory.CreateDirectory(outDir);
        for (int i = 0; i < model.Textures.Count; i++)
        {
            var tex = model.Textures[i];
            var img = cache.Load(cascName, tex, 0);
            if (img is null) { Console.WriteLine($"tex{i} {tex} flags={tex.Flags}: UNRESOLVED"); continue; }
            double r = 0, g = 0, b = 0, a = 0; int n = img.Pixels.Length / 4;
            for (int p = 0; p < img.Pixels.Length; p += 4) { r += img.Pixels[p]; g += img.Pixels[p + 1]; b += img.Pixels[p + 2]; a += img.Pixels[p + 3]; }
            var small = Shrink(img, 512);
            string leaf = Path.GetFileNameWithoutExtension(tex.FileName.Replace('/', Path.DirectorySeparatorChar));
            string name = $"tex{i}_{leaf}.png";
            File.WriteAllBytes(Path.Combine(outDir, name), PngWriter.Write(small));
            Console.WriteLine($"tex{i} {tex} flags={tex.Flags} {img.Width}x{img.Height} mean rgba {r / n:0} {g / n:0} {b / n:0} {a / n:0} -> {name}");
        }
        return 0;
    }

    private static RgbaImage Shrink(RgbaImage src, int max)
    {
        int f = Math.Max(1, (Math.Max(src.Width, src.Height) + max - 1) / max);
        if (f == 1) return src;
        int w = src.Width / f, h = src.Height / f;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                Array.Copy(src.Pixels, ((y * f) * src.Width + x * f) * 4, px, (y * w + x) * 4, 4);
        return new RgbaImage { Width = w, Height = h, Pixels = px };
    }
}
