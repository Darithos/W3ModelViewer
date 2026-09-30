using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Lists the layers of an art set that animate their texture — a TXAN reference, a KMTF flipbook,
/// or a texture whose name says water — so "water looks wrong" can be narrowed to a mechanism.
/// </summary>
public static class WaterScan
{
    public static int Run(string install, Wc3ArtSet art, string? filter)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        int txan = 0, flip = 0, both = 0, water = 0;
        foreach (var entry in index.Models.Where(m => !m.IsPortrait && m.ArtSet == art))
        {
            if (filter is not null && !entry.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            var lines = new List<string>();
            for (int mi = 0; mi < model.Materials.Count; mi++)
                foreach (var l in model.Materials[mi].Layers)
                {
                    int d = l.DiffuseTextureId;
                    string tex = (uint)d < (uint)model.Textures.Count ? model.Textures[d].ToString() : "?";
                    bool isFlip = l.TextureIdTrack is { Count: > 0 };
                    bool isWater = tex.Contains("water", StringComparison.OrdinalIgnoreCase);
                    if (l.TextureAnimationId < 0 && !isFlip && !isWater) continue;
                    if (l.TextureAnimationId >= 0) txan++;
                    if (isFlip) flip++;
                    if (isFlip && l.TextureAnimationId >= 0) both++;
                    if (isWater) water++;
                    var geos = model.Geosets.Where(g => g.MaterialId == mi && g.LodId == 0).Select(g => g.Index);
                    lines.Add($"    mat{mi} g[{string.Join(",", geos)}] {l.FilterMode} {l.ShadingFlags} txan={l.TextureAnimationId} flip={(isFlip ? l.TextureIdTrack!.Count : 0)} pbr={l.IsPbr} tex '{tex}'"
                              + (l.IsPbr ? $" nrm={l.Slot(MdxTextureSlot.Normal)} orm={l.Slot(MdxTextureSlot.Orm)} flags={((uint)d < (uint)model.Textures.Count ? model.Textures[d].Flags : 0)}" : ""));
                }
            if (lines.Count == 0) continue;
            Console.WriteLine(entry.RelativePath + (model.SkippedChunks.Count > 0 ? $"  (skipped {string.Join(",", model.SkippedChunks)})" : ""));
            foreach (var s in lines) Console.WriteLine(s);
        }
        Console.WriteLine($"\n{art}: layers with TXAN {txan}, flipbook {flip}, both {both}, water-named texture {water}");
        return 0;
    }
}
