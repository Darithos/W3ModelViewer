using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// The control for texture clamping: whatever a real model does, these two quads differ only in
/// their TEXS wrap flags, so a difference between them in StarCraft II is the uv_wrap bits alone.
/// </summary>
public static class WrapQuad
{
    /// <summary>
    /// Two flat quads UV-mapped from -1 to 2, carrying War Stomp's own LightningBall.blp — one with
    /// TEXS flags 0 (clamp), one with 3 (wrap). What the exporter's uv_wrap bits do in StarCraft II,
    /// with nothing else in the picture.
    /// </summary>
    public static int Run(string install, string outDir, string prefix)
    {
        using var storage = new Wc3Storage(install);
        var cache = new Wc3TextureCache(storage);
        foreach (var (id, flags) in new[] { ("clamp", 0u), ("wrap", 3u) })
        {
            var model = Quad(60f, flags);
            string name = prefix + id;
            var res = new M3Exporter(model, new M3ExportOptions { ModelName = name, Scale = 0.025f, ReduceKeys = false, ExportEffects = false })
                .Export(cache, @"war3.w3mod:abilities\spells\orc\warstomp\warstompcaster.mdx");
            string dir = Path.Combine(outDir, name), texDir = Path.Combine(dir, "textures");
            Directory.CreateDirectory(texDir);
            File.WriteAllBytes(Path.Combine(dir, name + ".m3"), res.M3);
            foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(texDir, t.FileName), t.Data);
            Console.WriteLine($"{name}: TEXS flags {flags} -> {dir}");
        }
        return 0;
    }

    private static MdxModel Quad(float half, uint texFlags)
    {
        var model = new MdxModel { Version = 800, Name = "WrapQuad", Min = new Vector3(-half, -half, 0), Max = new Vector3(half, half, 1), BoundsRadius = half * 1.5f };
        model.Sequences.Add(new MdxSequence { Name = "Stand", IntervalStart = 0, IntervalEnd = 1000 });
        model.Textures.Add(new MdxTexture { FileName = @"Textures\LightningBall.blp", Flags = texFlags });
        model.Materials.Add(new MdxMaterial { Layers = [new MdxLayer { FilterMode = MdxFilterMode.None, TextureId = 0, Alpha = 1f, TextureSlots = new Dictionary<MdxTextureSlot, int>() }] });
        model.Pivots.Add(Vector3.Zero);
        model.Nodes.Add(new MdxNode { Name = "root", ObjectId = 0, ParentId = -1, Kind = MdxNodeKind.Bone, Flags = MdxNodeFlags.Bone, Pivot = Vector3.Zero });
        Vector3[] pos = [new(-half, -half, 2), new(half, -half, 2), new(half, half, 2), new(-half, half, 2)];
        Vector2[] uv = [new(-1, 2), new(2, 2), new(2, -1), new(-1, -1)];
        model.Geosets.Add(new MdxGeoset
        {
            Index = 0, Positions = pos, Normals = [.. Enumerable.Repeat(Vector3.UnitZ, 4)], Indices = [0, 1, 2, 0, 2, 3],
            UvLayers = [uv], VertexGroups = new byte[4], MatrixGroupSizes = [1], MatrixIndices = [0],
            MaterialId = 0, Min = model.Min, Max = model.Max, BoundsRadius = half * 1.5f,
        });
        return model;
    }
}
