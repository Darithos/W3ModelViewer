using System.Numerics;
using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Spheres that measure StarCraft II's environment layer before any unit relies on it: which way
/// its cube map faces (a cube whose every texel is coloured by its own direction), whether gloss
/// blurs the reflection (a checker cube at two glosses, with and without simulate_roughness), and
/// how its brightness adds up (uniform cubes, a half mask, a doubled multiply, diffuse under it).
/// One sphere goes through the real exporter with a reflection; each variant then swaps the cube,
/// diffuse and spec bitmaps for same-length names and patches MAT_ and the envi layer.
/// </summary>
public static class EnviCal
{
    /// <summary>Cube (null = no envi layer), diffuse grey, spec grey and gloss alpha, gloss on, simulate_roughness, multiply.</summary>
    public sealed record Variant(string Id, string? Cube, byte Diffuse, byte Spec, byte GlossAlpha, bool Gloss, bool Simulate, float Multiply = 1f);

    public static readonly Variant[] Row =
    [
        new("a", "axes", 0, 255, 255, false, false),
        new("b", "checker", 0, 255, 255, true, true),
        new("c", "checker", 0, 255, 64, true, true),
        new("d", "checker", 0, 255, 64, true, false),
        new("e", "grey", 0, 255, 255, false, false),
        new("f", "white", 0, 255, 255, false, false),
        new("g", "grey", 0, 128, 255, false, false),
        new("h", "grey", 0, 255, 255, false, false, 2f),
        new("i", "ibl", 0, 255, 255, false, false),
        new("j", "grey", 90, 255, 255, false, false),
        new("k", null, 90, 255, 255, false, false),
    ];

    /// <summary>
    /// Round two, once the first showed gloss blurs the reflection only with simulate_roughness and
    /// that the envi output is not linear in the texel (grey 128 displays 127, white 195): the mip each
    /// gloss selects (a cube whose mip k is uniform grey 24k, so the displayed grey reads the level),
    /// the display curve (uniform cubes and multipliers), and an orientation cube kept below 128.
    /// </summary>
    public static readonly Variant[] Round2 =
    [
        new("a", "mipramp", 0, 255, 255, true, true), new("b", "mipramp", 0, 255, 223, true, true),
        new("c", "mipramp", 0, 255, 191, true, true), new("d", "mipramp", 0, 255, 159, true, true),
        new("e", "mipramp", 0, 255, 128, true, true), new("f", "mipramp", 0, 255, 96, true, true),
        new("g", "mipramp", 0, 255, 64, true, true), new("h", "mipramp", 0, 255, 32, true, true),
        new("i", "mipramp", 0, 255, 0, true, true), new("j", "mipramp", 0, 255, 255, false, false),
        new("k", "axes2", 0, 255, 255, false, false),
        new("l", "u32", 0, 255, 255, false, false), new("m", "u64", 0, 255, 255, false, false),
        new("n", "u96", 0, 255, 255, false, false), new("o", "u160", 0, 255, 255, false, false),
        new("p", "u192", 0, 255, 255, false, false), new("q", "u224", 0, 255, 255, false, false),
        new("r", "grey", 0, 255, 255, false, false, 0.5f), new("s", "grey", 0, 255, 255, false, false, 1.5f),
        new("t", "grey", 0, 255, 255, false, false, 3f),
    ];

    /// <summary>
    /// Round three: does gloss's mip reach scale with the cube (7 levels of blur over gloss on the
    /// 128px cube — its mip count less one — and a stop at the 4px mip)? Mip ramps at 512 and 256,
    /// Warcraft III's own sizes; the multiply once more with its second float left at 1; and
    /// Warcraft III's sky turned to the Agria sun, sharp and at gloss 0.5.
    /// </summary>
    public static readonly Variant[] Round3 =
    [
        new("a", "mip512", 0, 255, 255, true, true), new("b", "mip512", 0, 255, 191, true, true),
        new("c", "mip512", 0, 255, 128, true, true), new("d", "mip512", 0, 255, 64, true, true),
        new("e", "mip512", 0, 255, 0, true, true),
        new("f", "mip256", 0, 255, 255, true, true), new("g", "mip256", 0, 255, 191, true, true),
        new("h", "mip256", 0, 255, 128, true, true), new("i", "mip256", 0, 255, 64, true, true),
        new("j", "mip256", 0, 255, 0, true, true),
        new("k", "grey", 0, 255, 255, false, false, 0.5f), new("l", "grey", 0, 255, 255, false, false, 1.5f),
        new("m", "grey", 0, 255, 255, false, false, 2f),
        new("n", "wc3", 0, 255, 255, true, true), new("o", "wc3", 0, 255, 128, true, true),
    ];

    /// <summary>Warcraft III's Lordaeron Summer day sky as the 256px StarCraft II cube the export ships.</summary>
    public static byte[] Wc3Cube(Wc3Storage storage)
    {
        var ibl = storage.TryReadFile(@"war3.w3mod:_de.w3mod:environment\environmentmap\lordaeronsummer\day_ibl.dds")
                  ?? throw new FileNotFoundException("day_ibl.dds");
        return ReflectionCube.FromWarcraftIbl(ibl, 256, ReflectionCube.AgriaSun, k => (k + 1) / 9f);
    }

    /// <summary>
    /// A unit exported today (prefix + "a") and with the reflection at the given multiplies and
    /// hdr_spec (+ "b", "c", ...), for the same-spot side-by-side against Warcraft III. All in the
    /// bind pose: units play Stand in the editor, and two captures of one export differed by 8.9/255
    /// along every edge from the pose alone.
    /// </summary>
    public static int KnightAb(string install, string cascName, string outDir, string prefix, (float Multiply, float HdrSpec)[] settings)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var model = MdxReader.Read(storage.TryReadFile(cascName) ?? throw new FileNotFoundException(cascName));
        var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
        var cube = Wc3Cube(storage);
        var runs = new List<(string Tag, M3ExportOptions Opts)>
        {
            ("a", new M3ExportOptions { Lod = 0, ModelName = prefix + "a", Scale = 0.025f, AnimData = M3AnimTestData.None, ReflectSky = false }),
        };
        for (int i = 0; i < settings.Length; i++)
            runs.Add(((char)('b' + i)).ToString() is var tag
                ? (tag, new M3ExportOptions
                {
                    Lod = 0, ModelName = prefix + tag, Scale = 0.025f, SpecularGain = settings[i].HdrSpec, AnimData = M3AnimTestData.None,
                    Reflection = new ReflectionMap { Cube = cube, Multiply = settings[i].Multiply },
                })
                : default);
        foreach (var (tag, opts) in runs)
        {
            var res = new M3Exporter(model, opts).Export(cache, cascName);
            string dir = Path.Combine(outDir, opts.ModelName), texDir = Path.Combine(dir, "textures");
            Directory.CreateDirectory(texDir);
            File.WriteAllBytes(Path.Combine(dir, opts.ModelName + ".m3"), res.M3);
            foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(texDir, t.FileName), t.Data);
            Console.WriteLine($"{opts.ModelName}: reflection x{opts.Reflection?.Multiply.ToString() ?? "-"} hdr_spec {opts.SpecularGain} -> {res.M3.Length:N0} B, {res.Textures.Count} textures");
        }
        return 0;
    }

    /// <summary>
    /// A unit exported with the options the app uses by default (scale 0.025, optionally the bind
    /// pose), next to the same export with <see cref="M3ExportOptions.ReflectSky"/> off, and whether
    /// the two differ at all — an SD unit must come out byte for byte the same.
    /// </summary>
    public static int AppExport(string install, string cascName, string outDir, string name, bool bindPose)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var model = MdxReader.Read(storage.TryReadFile(cascName) ?? throw new FileNotFoundException(cascName));
        var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
        byte[]? previous = null;
        foreach (bool sky in new[] { true, false })
        {
            string n = name + (sky ? "s" : "n");       // equal lengths, so the two files line up byte for byte
            var opts = new M3ExportOptions { Lod = 0, ModelName = n, Scale = 0.025f, ReflectSky = sky, AnimData = bindPose ? M3AnimTestData.None : M3AnimTestData.Full };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = new M3Exporter(model, opts).Export(cache, cascName);
            string dir = Path.Combine(outDir, n), texDir = Path.Combine(dir, "textures");
            Directory.CreateDirectory(texDir);
            File.WriteAllBytes(Path.Combine(dir, n + ".m3"), res.M3);
            foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(texDir, t.FileName), t.Data);
            Console.WriteLine($"{n}: {res.M3.Length:N0} B, textures {string.Join(", ", res.Textures.Select(t => $"{t.FileName} {t.Data.Length / 1024}K"))} ({sw.ElapsedMilliseconds} ms)");
            // The names differ (ModelName is in the file), so compare with each name blanked out.
            var body = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(res.M3).Replace(n, new string('#', n.Length)));
            if (previous is not null)
                Console.WriteLine(body.Length == previous.Length && body.AsSpan().SequenceEqual(previous) ? "  identical with and without the sky" : "  differs with the sky");
            previous = body;
        }
        return 0;
    }

    /// <summary>The six faces (+X -X +Y -Y +Z -Z) of one element and mip of a cube DDS, side by side as a PNG.</summary>
    public static int DumpFaces(string dds, string png, int element, int mip)
    {
        var d = File.ReadAllBytes(dds);
        var faces = Enumerable.Range(0, 6).Select(f => DdsReader.DecodeSurface(d, element * 6 + f, mip)).ToList();
        int s = faces[0].Width;
        var px = new byte[6 * s * s * 4];
        for (int f = 0; f < 6; f++)
            for (int y = 0; y < s; y++)
                Buffer.BlockCopy(faces[f].Pixels, y * s * 4, px, (y * 6 * s + f * s) * 4, s * 4);
        File.WriteAllBytes(png, PngWriter.Write(new RgbaImage { Width = 6 * s, Height = s, Pixels = px }));
        Console.WriteLine($"{Path.GetFileName(dds)} element {element} mip {mip}: {s}px faces -> {png}");
        return 0;
    }

    private static byte[] MipRamp(int size) => DdsWriter.WriteCube(Enumerable.Range(0, 6).Select(_ =>
        (IReadOnlyList<RgbaImage>)Enumerable.Range(0, 1 + (int)Math.Log2(size)).Select(k =>
            RgbaImage.Solid(size >> k, size >> k, (byte)Math.Min(255, 24 * k), (byte)Math.Min(255, 24 * k), (byte)Math.Min(255, 24 * k))).ToList()).ToList());

    public static int Run(string install, string outDir, string prefix)
    {
        Directory.CreateDirectory(outDir);
        string src = Path.Combine(outDir, "src");
        Directory.CreateDirectory(src);
        GlossCal.WriteDds(Path.Combine(src, "diff.dds"), GlossCal.Solid(90, 90, 90, 255));
        GlossCal.WriteDds(Path.Combine(src, "norm.dds"), GlossCal.Solid(255, 128, 128, 255));
        GlossCal.WriteDds(Path.Combine(src, "orm.dds"), GlossCal.Solid(0, 128, 255, 0));

        var cubes = new Dictionary<string, byte[]>
        {
            ["axes"] = DdsWriter.WriteCube(Faces(64, d => Color((d + Vector3.One) * 0.5f))),
            ["checker"] = DdsWriter.WriteCube(Faces(128, (face, x, y) => ((x / 16 + y / 16) & 1) == 0 ? (byte)255 : (byte)0)),
            ["grey"] = DdsWriter.WriteCube(Faces(64, (_, _, _) => (byte)128)),
            ["white"] = DdsWriter.WriteCube(Faces(64, (_, _, _) => (byte)255)),
            ["axes2"] = DdsWriter.WriteCube(Faces(64, d => Color((d + Vector3.One) * 0.25f))),
            ["mipramp"] = DdsWriter.WriteCube(Enumerable.Range(0, 6).Select(_ =>
                (IReadOnlyList<RgbaImage>)Enumerable.Range(0, 8).Select(k =>
                    RgbaImage.Solid(128 >> k, 128 >> k, (byte)(24 * k), (byte)(24 * k), (byte)(24 * k))).ToList()).ToList()),
        };
        foreach (int u in new[] { 32, 64, 96, 160, 192, 224 })
            cubes[$"u{u}"] = DdsWriter.WriteCube(Faces(64, (_, _, _) => (byte)u));
        using (var storage = new Wc3Storage(install))
        {
            var ibl = storage.TryReadFile(@"war3.w3mod:_de.w3mod:environment\environmentmap\lordaeronsummer\day_ibl.dds")
                      ?? throw new FileNotFoundException("day_ibl.dds");
            // Element 1 (surfaces 6-11) is the specular, element 0 the irradiance; mip 1 = 256px.
            cubes["ibl"] = DdsWriter.WriteCube(Enumerable.Range(6, 6).Select(s => DdsReader.DecodeSurface(ibl, s, 1)).ToList());
            cubes["wc3"] = ReflectionCube.FromWarcraftIbl(ibl, 256, ReflectionCube.AgriaSun, k => k / 8f);
        }
        cubes["mip512"] = MipRamp(512);
        cubes["mip256"] = MipRamp(256);

        var model = GlossCal.Sphere(40f, 48, 32);
        var cache = new Wc3TextureCache(null);
        cache.SetOverride(@"cal\diff.tif", Path.Combine(src, "diff.dds"));
        cache.SetOverride(@"cal\norm.tif", Path.Combine(src, "norm.dds"));
        cache.SetOverride(@"cal\orm.tif", Path.Combine(src, "orm.dds"));
        var opts = new M3ExportOptions
        {
            ModelName = prefix, Scale = 0.025f, ReduceKeys = false, ExportEffects = false, GlossMap = true,
            Reflection = new ReflectionMap { Cube = cubes["grey"], Name = "envtest" },
        };
        var res = new M3Exporter(model, opts).Export(cache, "");
        string Named(string part) => res.Textures.Select(t => t.FileName).Single(n => n.Contains(part));
        string envName = Named("envtest_"), diffName = Named("_diff_"), specName = Named("_spec_");
        Console.WriteLine($"base: {res.M3.Length} bytes, textures {string.Join(", ", res.Textures.Select(t => t.FileName))}");

        var argv = Environment.GetCommandLineArgs();
        foreach (var v in argv.Contains("--round3") ? Round3 : argv.Contains("--round2") ? Round2 : Row)
        {
            string id = prefix + v.Id, dir = Path.Combine(outDir, id), texDir = Path.Combine(dir, "textures");
            Directory.CreateDirectory(texDir);
            var m3 = (byte[])res.M3.Clone();
            foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(texDir, t.FileName), t.Data);
            string Swap(string name, byte[] data)
            {
                // The prefix is hashed in: the editor caches textures by name for the whole session, and
                // a plain "v" + id let round two's cubes come back as round one's.
                string vName = name[..^14] + GlossCal.VariantTag(id, name) + ".dds";
                File.WriteAllBytes(Path.Combine(texDir, vName), data);
                GlossCal.Replace(m3, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(vName));
                return vName;
            }
            if (v.Cube is not null) Swap(envName, cubes[v.Cube]);
            Swap(diffName, DdsWriter.Write(RgbaImage.Solid(64, 64, v.Diffuse, v.Diffuse, v.Diffuse, 255), 0, alphaIsCoverage: false));
            Swap(specName, DdsWriter.Write(RgbaImage.Solid(64, 64, v.Spec, v.Spec, v.Spec, v.GlossAlpha), 0, alphaIsCoverage: false));
            Patch(m3, v);
            File.WriteAllBytes(Path.Combine(dir, id + ".m3"), m3);
            Console.WriteLine($"{id}: {v}");
        }
        return 0;
    }

    /// <summary>
    /// MAT_ (v20): hdr_spec 0 so the sun highlight cannot mix in, simulate_roughness and the gloss
    /// layer per variant, the envi layer dropped for a no-cube variant; envi LAYR colour_multiply.
    /// </summary>
    private static void Patch(byte[] m3, Variant v)
    {
        uint indexOffset = BitConverter.ToUInt32(m3, 4), indexCount = BitConverter.ToUInt32(m3, 8);
        int Section(int index) => (int)BitConverter.ToUInt32(m3, (int)indexOffset + 16 * index + 4);
        for (int i = 0; i < indexCount; i++)
        {
            int e = (int)indexOffset + 16 * i;
            if (Encoding.ASCII.GetString(m3, e, 4) != "_TAM") continue;
            int mat = (int)BitConverter.ToUInt32(m3, e + 4);
            uint flags = BitConverter.ToUInt32(m3, mat + 16);
            flags = v.Simulate ? flags | 0x800 : flags & ~0x800u;
            BitConverter.TryWriteBytes(m3.AsSpan(mat + 16), flags);
            BitConverter.TryWriteBytes(m3.AsSpan(mat + 32), 512f);                  // specularity
            BitConverter.TryWriteBytes(m3.AsSpan(mat + 44), 0f);                    // hdr_spec
            int Slot(int k) => mat + 64 + 12 * k;
            if (!v.Gloss) Buffer.BlockCopy(m3, Slot(1), m3, Slot(3), 12);           // gloss -> decal's null layer
            if (v.Cube is null) { Buffer.BlockCopy(m3, Slot(1), m3, Slot(6), 12); Buffer.BlockCopy(m3, Slot(1), m3, Slot(7), 12); }
            else
            {
                int layr = Section((int)BitConverter.ToUInt32(m3, Slot(6) + 4));
                BitConverter.TryWriteBytes(m3.AsSpan(layr + 56), v.Multiply);
            }
            return;
        }
        throw new InvalidOperationException("no MAT_");
    }

    /// <summary>D3D cube face directions: face 0..5 = +X, -X, +Y, -Y, +Z, -Z; s, t in -1..1 across and down the face.</summary>
    public static Vector3 Direction(int face, float s, float t) => Vector3.Normalize(face switch
    {
        0 => new Vector3(1, -t, -s),
        1 => new Vector3(-1, -t, s),
        2 => new Vector3(s, 1, t),
        3 => new Vector3(s, -1, -t),
        4 => new Vector3(s, -t, 1),
        _ => new Vector3(-s, -t, -1),
    });

    private static (byte B, byte G, byte R) Color(Vector3 c) =>
        ((byte)Math.Clamp(c.Z * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.Y * 255f + 0.5f, 0, 255), (byte)Math.Clamp(c.X * 255f + 0.5f, 0, 255));

    private static List<RgbaImage> Faces(int size, Func<Vector3, (byte B, byte G, byte R)> colour) =>
        Enumerable.Range(0, 6).Select(face =>
        {
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var (b, g, r) = colour(Direction(face, 2f * (x + 0.5f) / size - 1, 2f * (y + 0.5f) / size - 1));
                    int o = (y * size + x) * 4;
                    px[o] = b; px[o + 1] = g; px[o + 2] = r; px[o + 3] = 255;
                }
            return new RgbaImage { Width = size, Height = size, Pixels = px };
        }).ToList();

    private static List<RgbaImage> Faces(int size, Func<int, int, int, byte> grey) =>
        Enumerable.Range(0, 6).Select(face =>
        {
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    byte g = grey(face, x, y); int o = (y * size + x) * 4;
                    px[o] = g; px[o + 1] = g; px[o + 2] = g; px[o + 3] = 255;
                }
            return new RgbaImage { Width = size, Height = size, Pixels = px };
        }).ToList();
}
