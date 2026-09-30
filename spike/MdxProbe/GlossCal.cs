using System.Numerics;
using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// A row of spheres that measures how StarCraft II turns a gloss value into a highlight: nothing
/// documents it and no shader source ships, so the only honest answer is to render it. One sphere
/// is exported through the real exporter (so its material is exactly what an HD unit gets), then
/// cloned with the gloss texture, MAT_.specularity, simulate_roughness and the gloss layer itself
/// patched per variant. Placed side by side under the same light, the no-gloss spheres at known
/// exponents are the ruler the gloss spheres are read against.
/// </summary>
public static class GlossCal
{
    /// <summary>One sphere: gloss (null = no gloss layer), specularity, simulate_roughness, spec grey.</summary>
    public sealed record Variant(string Id, float? Gloss, float Specularity, bool Simulate, byte Spec = 255, float HdrSpec = 8f);

    /// <summary>Is specular reaching the screen at all: blown-out spec, the same with gloss, and none.</summary>
    public static readonly Variant[] Probe =
    [
        new("x", null, 2, false, 255, 50f),
        new("y", 0.50f, 2, true, 255, 50f),
        new("z", null, 2, false, 255, 0f),
    ];

    public static readonly Variant[] Row =
    [
        new("a", 0.25f, 512, true),
        new("b", 0.50f, 512, true),
        new("c", 0.75f, 512, true),
        new("d", 1.00f, 512, true),
        new("e", 0.50f, 512, false),
        new("f", null, 8, false),
        new("g", null, 32, false),
        new("h", null, 128, false),
        new("i", null, 512, false),
        new("j", 0.50f, 64, true),
    ];

    public static int Run(string outDir, string prefix)
    {
        Directory.CreateDirectory(outDir);
        string src = Path.Combine(outDir, "src");
        Directory.CreateDirectory(src);

        // Source textures: mid-grey diffuse, flat normal, and an ORM whose roughness is mid-range so
        // the base export writes a gloss layer at all; every variant replaces the spec bitmap anyway.
        WriteDds(Path.Combine(src, "diff.dds"), Solid(90, 90, 90, 255));
        WriteDds(Path.Combine(src, "norm.dds"), Solid(255, 128, 128, 255));
        WriteDds(Path.Combine(src, "orm.dds"), Solid(0, 128, 255, 0));      // B metal 0, G rough .5, R ao 1

        var model = Sphere(40f, 48, 32);
        var cache = new Wc3TextureCache(null);
        cache.SetOverride(@"cal\diff.tif", Path.Combine(src, "diff.dds"));
        cache.SetOverride(@"cal\norm.tif", Path.Combine(src, "norm.dds"));
        cache.SetOverride(@"cal\orm.tif", Path.Combine(src, "orm.dds"));

        var opts = new M3ExportOptions { ModelName = prefix, Scale = 0.025f, ReduceKeys = false, ExportEffects = false, GlossMap = true };
        var res = new M3Exporter(model, opts).Export(cache, "");
        string spec = res.Textures.Select(t => t.FileName).Single(n => n.Contains("_spec_"));
        Console.WriteLine($"base: {res.M3.Length} bytes, textures {string.Join(", ", res.Textures.Select(t => t.FileName))}");

        foreach (var v in Environment.GetCommandLineArgs().Contains("--probe") ? Probe : Row)
        {
            string id = prefix + v.Id;
            string dir = Path.Combine(outDir, id);
            string texDir = Path.Combine(dir, "textures");
            Directory.CreateDirectory(texDir);
            foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(texDir, t.FileName), t.Data);

            // Same length as the exporter's name, so the path strings can be swapped in place.
            string vSpec = spec[..^14] + $"v{v.Id}{new string('0', 8)}.dds";
            if (vSpec.Length != spec.Length) throw new InvalidOperationException(vSpec);
            byte g = (byte)Math.Clamp(MathF.Round((v.Gloss ?? 1f) * 255f), 0, 255);
            WriteDds(Path.Combine(texDir, vSpec), Solid(v.Spec, v.Spec, v.Spec, g));

            var m3 = (byte[])res.M3.Clone();
            Replace(m3, Encoding.ASCII.GetBytes(spec), Encoding.ASCII.GetBytes(vSpec));
            PatchMaterial(m3, v);
            File.WriteAllBytes(Path.Combine(dir, id + ".m3"), m3);
            Console.WriteLine($"{id}: gloss {(v.Gloss is { } gl ? gl.ToString("0.00") : "none")} specularity {v.Specularity} simulate {v.Simulate} -> {dir}");
        }
        return 0;
    }

    /// <summary>Rewrites MAT_[0]'s flags, specularity and gloss reference (v20 offsets: 16, 32, 64 + 3*12).</summary>
    private static void PatchMaterial(byte[] m3, Variant v)
    {
        uint indexOffset = BitConverter.ToUInt32(m3, 4), indexCount = BitConverter.ToUInt32(m3, 8);
        for (int i = 0; i < indexCount; i++)
        {
            int e = (int)indexOffset + 16 * i;
            if (Encoding.ASCII.GetString(m3, e, 4) != "_TAM") continue;         // MAT_, stored reversed
            int mat = (int)BitConverter.ToUInt32(m3, e + 4);
            uint flags = BitConverter.ToUInt32(m3, mat + 16);
            flags = v.Simulate ? flags | 0x800 : flags & ~0x800u;
            BitConverter.TryWriteBytes(m3.AsSpan(mat + 16), flags);
            BitConverter.TryWriteBytes(m3.AsSpan(mat + 32), v.Specularity);
            BitConverter.TryWriteBytes(m3.AsSpan(mat + 44), v.HdrSpec);          // hdr_spec: loud enough to measure
            if (v.Gloss is null)                                                   // point gloss at decal's null layer
                Buffer.BlockCopy(m3, mat + 64 + 12 * 1, m3, mat + 64 + 12 * 3, 12);
            return;
        }
        throw new InvalidOperationException("no MAT_");
    }

    private static void Replace(byte[] hay, byte[] from, byte[] to)
    {
        int hits = 0;
        for (int i = 0; i + from.Length <= hay.Length; i++)
            if (hay.AsSpan(i, from.Length).SequenceEqual(from)) { Buffer.BlockCopy(to, 0, hay, i, to.Length); hits++; }
        if (hits == 0) throw new InvalidOperationException("spec path not found in the m3");
    }

    private static RgbaImage Solid(byte b, byte g, byte r, byte a) => RgbaImage.Solid(64, 64, b, g, r, a);

    private static void WriteDds(string path, RgbaImage img) => File.WriteAllBytes(path, DdsWriter.Write(img, 0, alphaIsCoverage: false));

    /// <summary>A lat-long sphere on one bone, centred one radius above the ground, with one HD PBR material.</summary>
    private static MdxModel Sphere(float radius, int segments, int rings)
    {
        var pos = new List<Vector3>(); var nrm = new List<Vector3>(); var uv = new List<Vector2>(); var tan = new List<Vector4>();
        var centre = new Vector3(0, 0, radius * 1.1f);
        for (int r = 0; r <= rings; r++)
        {
            float v = (float)r / rings, theta = v * MathF.PI;
            for (int s = 0; s <= segments; s++)
            {
                float u = (float)s / segments, phi = u * 2 * MathF.PI;
                var n = new Vector3(MathF.Sin(theta) * MathF.Cos(phi), MathF.Sin(theta) * MathF.Sin(phi), MathF.Cos(theta));
                pos.Add(centre + n * radius); nrm.Add(n); uv.Add(new Vector2(u, v));
                tan.Add(new Vector4(-MathF.Sin(phi), MathF.Cos(phi), 0, 1));
            }
        }
        var idx = new List<int>();
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < segments; s++)
            {
                int a = r * (segments + 1) + s, b = a + segments + 1;
                idx.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }

        int n0 = pos.Count;
        var model = new MdxModel { Version = 1100, Name = "SphereCal", Min = centre - new Vector3(radius), Max = centre + new Vector3(radius), BoundsRadius = radius };
        model.Sequences.Add(new MdxSequence { Name = "Stand", IntervalStart = 0, IntervalEnd = 1000 });
        model.Textures.Add(new MdxTexture { FileName = @"cal\diff.tif", Flags = 3 });
        model.Textures.Add(new MdxTexture { FileName = @"cal\norm.tif", Flags = 3 });
        model.Textures.Add(new MdxTexture { FileName = @"cal\orm.tif", Flags = 3 });
        model.Materials.Add(new MdxMaterial
        {
            Layers =
            [
                new MdxLayer
                {
                    FilterMode = MdxFilterMode.None, TextureId = 0, Alpha = 1f, EmissiveMultiplier = 1f,
                    TextureSlots = new Dictionary<MdxTextureSlot, int>
                    {
                        [MdxTextureSlot.Diffuse] = 0, [MdxTextureSlot.Normal] = 1, [MdxTextureSlot.Orm] = 2,
                    },
                },
            ],
        });
        model.Pivots.Add(Vector3.Zero);
        model.Nodes.Add(new MdxNode { Name = "root", ObjectId = 0, ParentId = -1, Kind = MdxNodeKind.Bone, Flags = MdxNodeFlags.Bone, Pivot = Vector3.Zero });
        model.Geosets.Add(new MdxGeoset
        {
            Index = 0, Positions = [.. pos], Normals = [.. nrm], Indices = [.. idx], UvLayers = [uv.ToArray()],
            Tangents = [.. tan],
            SkinBoneIndices = new int[n0 * 4],
            SkinBoneWeights = Enumerable.Range(0, n0 * 4).Select(k => k % 4 == 0 ? (byte)255 : (byte)0).ToArray(),
            MaterialId = 0, Min = model.Min, Max = model.Max, BoundsRadius = radius,
        });
        return model;
    }
}
