using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Measures what the Reforged PBR → StarCraft II specular conversion does to a model's brightness.
/// </summary>
/// <remarks>
/// Users reported exported Reforged models reading much darker in StarCraft II than in the
/// Warcraft III viewer, "especially on metal". The conversion pulls the metallic part out of the
/// albedo and folds occlusion in, both of which darken; StarCraft II's single specular intensity
/// only pays that back at the highlight angle, so most of the surface keeps the loss. This prints
/// the ratio, whole-texture and restricted to the metallic texels, which is the number to tune
/// against — the viewer draws the albedo itself, so a ratio of 1.0 is "as the viewer shows it".
/// </remarks>
public static class PbrProbe
{
    public static int Run(string install, string[] names)
    {
        using var storage = new Wc3Storage(install);
        var cache = new Wc3TextureCache(storage) { PreferHd = true };

        Console.WriteLine($"{"model",-34} {"texture",-30} {"occl",6} {"rough",6} {"metal",6} "
                          + $"{"albedo",7} {"diffuse",7} {"ratio",6} | {"metal-only alb/diff/ratio",26} {"spec",6}");
        foreach (string name in names)
        {
            var raw = File.Exists(name) ? File.ReadAllBytes(name) : storage.TryReadFile(name);
            if (raw is null) { Console.WriteLine($"{Short(name),-34} NOT FOUND"); continue; }
            if (File.Exists(name)) cache.LocalRoots.Add(Path.GetDirectoryName(Path.GetFullPath(name))!);
            var model = MdxReader.Read(raw);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var mat in model.Materials)
            {
                var layer = mat.Layers.FirstOrDefault(l => l.IsPbr);
                if (layer is null) continue;
                int did = layer.Slot(MdxTextureSlot.Diffuse);
                if ((uint)did >= (uint)model.Textures.Count) continue;
                string file = Path.GetFileName(model.Textures[did].FileName.Replace('\\', '/'));
                if (!seen.Add(file)) continue;

                var diffuse = cache.Load(File.Exists(name) ? "" : name, model.Textures[did]);
                if (diffuse is null) continue;
                RgbaImage? orm = null;
                int oid = layer.Slot(MdxTextureSlot.Orm);
                if ((uint)oid < (uint)model.Textures.Count) orm = cache.Load(File.Exists(name) ? "" : name, model.Textures[oid]);

                var set = PbrConverter.Convert(diffuse, null, orm, null);
                Report(Short(name), file, diffuse, orm, set);
            }
        }
        return 0;
    }

    private static void Report(string model, string file, RgbaImage albedo, RgbaImage? orm, SpecularSet set)
    {
        int n = albedo.Pixels.Length / 4;
        double sumO = 0, sumR = 0, sumM = 0, sumA = 0, sumD = 0, sumS = 0;
        double metalA = 0, metalD = 0; int metalN = 0;

        for (int i = 0; i < n; i++)
        {
            int p = i * 4;
            double o = 1, r = 0.7, m = 0;
            if (orm is not null)
            {
                int q = Sample(orm, i, albedo.Width, albedo.Height);
                o = orm.Pixels[q + 2] / 255.0;      // R
                r = orm.Pixels[q + 1] / 255.0;      // G
                m = orm.Pixels[q] / 255.0;          // B
            }
            sumO += o; sumR += r; sumM += m;
            double a = Lum(albedo.Pixels, p), d = Lum(set.Diffuse.Pixels, p);
            sumA += a; sumD += d; sumS += Lum(set.Specular.Pixels, p);
            if (m > 0.5) { metalA += a; metalD += d; metalN++; }
        }

        string metal = metalN == 0 ? $"{"(no metal texels)",26}"
            : $"{metalA / metalN,8:0.000}{metalD / metalN,9:0.000}{metalD / Math.Max(metalA, 1e-9),9:0.00}";
        Console.WriteLine($"{model,-34} {Trim(file, 30),-30} {sumO / n,6:0.00} {sumR / n,6:0.00} {sumM / n,6:0.00} "
                          + $"{sumA / n,7:0.000} {sumD / n,7:0.000} {sumD / Math.Max(sumA, 1e-9),6:0.00} | {metal} {sumS / n,6:0.000}");
    }

    /// <summary>
    /// Which particle sprites are greyscale enough to hand to StarCraft II's player-colour channel
    /// whole — <see cref="MaterialCompositor.SpriteMask"/>'s own answer, so this reports exactly
    /// what the export will do rather than a lookalike of it.
    /// </summary>
    public static int Sprites(string install, string[] names)
    {
        using var storage = new Wc3Storage(install);
        var cache = new Wc3TextureCache(storage);
        var all = storage.EnumerateAll().ToList();
        int taken = 0, kept = 0;
        foreach (string want in names)
        {
            string stem = Path.GetFileNameWithoutExtension(want);
            // Extension-filtered: the archive holds non-art files under these stems too, and
            // `bandit` matches a facial-animation set before it matches the texture.
            string? path = all.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                                                       .Equals(stem, StringComparison.OrdinalIgnoreCase)
                                                && (p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                                                 || p.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)));
            if (path is null) { Console.WriteLine($"  {stem,-30} NOT FOUND"); continue; }
            // Straight off the archive: these are bare texture paths, not references a model made,
            // so the cache's model-relative resolution has nothing to work with.
            var bytes = storage.TryReadFile(path);
            RgbaImage? img = null;
            try
            {
                img = bytes is null ? null
                    : path.EndsWith(".blp", StringComparison.OrdinalIgnoreCase) ? BlpReader.Decode(bytes)
                    : DdsReader.Decode(bytes);
            }
            catch (InvalidDataException) { }
            if (img is null) { Console.WriteLine($"  {stem,-30} UNREADABLE ({path})"); continue; }
            var mask = MaterialCompositor.SpriteMask(img);
            if (mask is null) kept++; else taken++;
            Console.WriteLine($"  {stem,-30} {img.Width,4}x{img.Height,-4} -> "
                              + (mask is null ? "keeps its own colour" : $"player colour (coverage {mask.Coverage * 100:0}%)"));
        }
        Console.WriteLine($"{taken} sprite(s) go through the live channel, {kept} keep their own colour");
        return 0;
    }

    /// <summary>Perceptual luminance, 0..1, from a BGRA texel.</summary>
    private static double Lum(byte[] px, int p) => (px[p + 2] * 0.2126 + px[p + 1] * 0.7152 + px[p] * 0.0722) / 255.0;

    private static int Sample(RgbaImage img, int i, int refW, int refH)
    {
        int x = i % refW, y = i / refW;
        int sx = refW == img.Width ? x : x * img.Width / refW;
        int sy = refH == img.Height ? y : y * img.Height / refH;
        return (sy * img.Width + sx) * 4;
    }

    private static string Short(string name) => Trim(Path.GetFileNameWithoutExtension(name.Replace('\\', '/')), 34);
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
