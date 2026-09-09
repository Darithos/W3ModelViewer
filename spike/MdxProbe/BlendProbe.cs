using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Per geoset: the filter modes its material's layers actually declare, and the single draw mode
/// <see cref="MaterialCompositor"/> collapses them to. The gap between the two is where an effect
/// card — a glow, a flare, a banner's shadow — stops floating and starts being a solid quad.
/// </summary>
/// <remarks>
/// Flat cards are called out because they are the ones that look catastrophic when mis-classified:
/// a two-triangle plane drawn opaque is a rectangle across the model, while the same mistake on a
/// body part is invisible. Takes a loose file or directory, or a CASC path.
/// </remarks>
public static class BlendProbe
{
    public static int Run(string install, string target)
    {
        using var storage = new Wc3Storage(install);
        var textures = new Wc3TextureCache(storage);

        var files = Directory.Exists(target)
            ? Directory.GetFiles(target, "*.mdx", SearchOption.AllDirectories)
            : [target];

        int suspects = 0, leakTotal = 0;
        foreach (string file in files)
        {
            byte[]? raw = File.Exists(file) ? File.ReadAllBytes(file) : storage.TryReadFile(file);
            if (raw is null) { Console.WriteLine($"{file}: not found"); continue; }

            var model = MdxReader.Read(raw);
            var cache = new Wc3TextureCache(storage) { PreferHd = model.IsReforged };
            if (File.Exists(file)) cache.LocalRoots.Add(Path.GetDirectoryName(Path.GetFullPath(file))!);
            string cascName = File.Exists(file) ? "" : file;

            var lines = new List<string>();
            var additive = new List<string>();
            var leaked = new List<string>();
            foreach (var g in model.Geosets.Where(g => g.LodId == model.LodLevels.FirstOrDefault()))
            {
                if ((uint)g.MaterialId >= (uint)model.Materials.Count) continue;
                var mat = model.Materials[g.MaterialId];
                var comp = MaterialCompositor.Compose(model, mat, cache, cascName, 0);

                string declared = string.Join("+", mat.Layers.Select(l =>
                {
                    int t = l.DiffuseTextureId;
                    string name = (uint)t < (uint)model.Textures.Count
                        ? (model.Textures[t].IsReplaceable
                            ? "REPL" + model.Textures[t].ReplaceableId
                            : Path.GetFileNameWithoutExtension(model.Textures[t].FileName))
                        : "?";
                    return $"{l.FilterMode}({name})";
                }));
                // An additive card carries its shape in brightness: the viewer turns max(r,g,b) into
                // alpha, so a mostly-black texture disappears. Reporting that fraction predicts what
                // the card will actually cover before anyone has to look at a screenshot.
                if (comp.Blend == CompositeBlend.Additive)
                {
                    var px = comp.Texture.Pixels;
                    int n = px.Length / 4, dark = 0;
                    for (int i = 0; i < px.Length; i += 4)
                        if (Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) < 16) dark++;
                    additive.Add($"  geoset {g.Index,2} mat {g.MaterialId,2}: {g.TriangleCount,4} tris, "
                                 + $"ADDITIVE [{declared}] — {100.0 * dark / n:0.0}% of texels become transparent");
                }

                bool wantsBlend = mat.Layers.Any(l => l.FilterMode
                    is MdxFilterMode.Blend or MdxFilterMode.Additive or MdxFilterMode.AddAlpha
                    or MdxFilterMode.Modulate or MdxFilterMode.Modulate2x);
                // An opaque material whose SOURCE texture carries transparency is one the viewer used
                // to draw see-through: WC3 ignores alpha on FilterMode.None, so any hole there was
                // the tool's, not the model's.
                if (comp.Blend == CompositeBlend.Opaque && mat.Layers.Count > 0)
                {
                    int t0 = mat.Layers[0].DiffuseTextureId;
                    var srcImage = (uint)t0 < (uint)model.Textures.Count
                        ? cache.Load(cascName, model.Textures[t0], 0) : null;
                    if (srcImage is not null && srcImage.HasTransparency())
                        leaked.Add($"  geoset {g.Index,2} mat {g.MaterialId,2}: opaque, but its texture "
                                   + $"[{declared}] carries alpha — used to draw see-through");
                }

                bool flattened = wantsBlend && comp.Blend == CompositeBlend.Opaque;
                if (!flattened) continue;

                suspects++;
                string card = g.TriangleCount <= 4 ? "  <-- FLAT CARD" : "";
                lines.Add($"  geoset {g.Index,2} mat {g.MaterialId,2}: {g.TriangleCount,5} tris, "
                          + $"{mat.Layers.Count} layer(s) [{declared}] -> {comp.Blend}{card}");
            }

            if (leaked.Count > 0)
            {
                Console.WriteLine($"{Path.GetFileName(file)}");
                leaked.ForEach(Console.WriteLine);
                leakTotal += leaked.Count;
            }
        }

        Console.WriteLine($"\n==== {suspects} geoset(s) whose material declares a blend but draws opaque ====");
        return 0;
    }
}
