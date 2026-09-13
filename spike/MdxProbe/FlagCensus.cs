using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Census of the layer shading flag <see cref="MdxShadingFlags.Unlit"/> (0x100): which stock
/// models carry it, on which geosets, and where those geosets sit relative to the rest of the mesh.
/// </summary>
public static class FlagCensus
{
    public static int Run(string install, int limit, string? filter, MdxShadingFlags flag = MdxShadingFlags.Unlit, Wc3ArtSet art = Wc3ArtSet.Definitive)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var models = index.Models.Where(m => !m.IsPortrait && m.ArtSet == art);
        if (filter is not null) models = models.Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
        int seen = 0, hits = 0;
        var geosetNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in models.Take(limit))
        {
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            seen++;
            bool any = false;
            foreach (var g in model.Geosets)
            {
                if (g.MaterialId < 0 || g.MaterialId >= model.Materials.Count) continue;
                var mat = model.Materials[g.MaterialId];
                if (!mat.Layers.Any(l => (l.ShadingFlags & flag) != 0)) continue;
                if (g.LodId != 0) continue;
                any = true;
                var l = mat.Layers[0];
                string tex = l.TextureId >= 0 && l.TextureId < model.Textures.Count ? model.Textures[l.TextureId].ToString() : "?";
                string leaf = g.LodName.Contains(':') ? g.LodName[(g.LodName.LastIndexOf(':') + 1)..] : g.LodName;
                geosetNames[leaf] = geosetNames.GetValueOrDefault(leaf) + 1;
                Console.WriteLine($"{entry.RelativePath}");
                Console.WriteLine($"    geoset[{g.Index}] '{g.LodName}' verts={g.VertexCount} z=[{g.Positions.Min(v => v.Z):F1},{g.Positions.Max(v => v.Z):F1}] sel={g.SectionGroupId}/{g.SectionGroupType} pbr={l.IsPbr} filter={l.FilterMode} flags={l.ShadingFlags} tex={tex}");
                var others = model.Geosets.Where(o => o.LodId == 0 && o != g && o.VertexCount > 0).ToList();
                foreach (var o in others.Where(o => (uint)o.MaterialId < (uint)model.Materials.Count))
                    Console.WriteLine($"      other '{o.LodName}' mat={o.MaterialId} matFlags=0x{model.Materials[o.MaterialId].Flags:X} prio={model.Materials[o.MaterialId].PriorityPlane} flags={model.Materials[o.MaterialId].Layers[0].ShadingFlags} filter={model.Materials[o.MaterialId].Layers[0].FilterMode}");
                if (others.Count > 0)
                    Console.WriteLine($"    other lod0 geosets z=[{others.Min(o => o.Positions.Min(v => v.Z)):F1},{others.Max(o => o.Positions.Max(v => v.Z)):F1}] sel={string.Join(",", others.Select(o => o.SectionGroupId + "/" + o.SectionGroupType))} ({others.Count})");
            }
            if (any && filter is not null)
                foreach (var g in model.Geosets.Where(g => g.LodId == 0 && (g.LodName.Contains("glide", StringComparison.OrdinalIgnoreCase) || g.LodName.Contains("walk", StringComparison.OrdinalIgnoreCase))))
                {
                    // Fit the walk plane from its first triangle, then histogram the signed distance of
                    // every other-geoset vertex whose XY lies inside the plane's shrunken XY box.
                    var a = g.Positions[g.Indices[0]]; var b = g.Positions[g.Indices[1]]; var c = g.Positions[g.Indices[2]];
                    var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(b - a, c - a));
                    if (n.Z < 0) n = -n;
                    float minX = g.Positions.Min(v => v.X), maxX = g.Positions.Max(v => v.X);
                    float minY = g.Positions.Min(v => v.Y), maxY = g.Positions.Max(v => v.Y);
                    float sx = (maxX - minX) * 0.25f, sy = (maxY - minY) * 0.25f;
                    var hist = new SortedDictionary<int, int>();
                    int inside = 0;
                    foreach (var o in model.Geosets.Where(o => o.LodId == 0 && o != g))
                        foreach (var v in o.Positions)
                        {
                            if (v.X < minX + sx || v.X > maxX - sx || v.Y < minY + sy || v.Y > maxY - sy) continue;
                            float d = System.Numerics.Vector3.Dot(v - a, n);
                            if (MathF.Abs(d) > 80) continue;
                            inside++;
                            int bucket = (int)MathF.Floor(d / 5) * 5;
                            hist[bucket] = hist.GetValueOrDefault(bucket) + 1;
                        }
                    Console.WriteLine($"    plane '{g.LodName}' normal=({n.X:F2},{n.Y:F2},{n.Z:F2}); {inside} deck verts within the plane's inner box, signed distance (5-unit buckets, + = above):");
                    Console.WriteLine("      " + string.Join("  ", hist.Select(kv => $"{kv.Key,4}:{kv.Value}")));
                }
            if (any) hits++;
        }
        Console.WriteLine($"\n{hits}/{seen} models carry a Unlit layer on a LOD0 geoset");
        foreach (var (name, n) in geosetNames.OrderByDescending(kv => kv.Value)) Console.WriteLine($"  {n,4}  {name}");
        return 0;
    }
}

public static class SlotMismatch
{
    /// <summary>How often an HD layer's header textureId disagrees with its slot table's Diffuse.</summary>
    public static int Run(string install)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        int layers = 0, mismatch = 0, headerZero = 0;
        var examples = new List<string>();
        foreach (var entry in index.Models.Where(m => !m.IsPortrait && m.ArtSet == Wc3ArtSet.Definitive))
        {
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            foreach (var mat in model.Materials)
                foreach (var l in mat.Layers)
                {
                    if (!l.IsPbr) continue;
                    int d = l.Slot(MdxTextureSlot.Diffuse);
                    if (d < 0) continue;
                    layers++;
                    if (d == l.TextureId) continue;
                    mismatch++;
                    if (l.TextureId == 0) headerZero++;
                    if (examples.Count < 15) examples.Add($"{entry.RelativePath}: header={l.TextureId} slotDiffuse={d}");
                }
        }
        Console.WriteLine($"{layers} HD layers, {mismatch} header!=slot, of which header==0: {headerZero}");
        foreach (var e in examples) Console.WriteLine("  " + e);
        return 0;
    }
}

public static class Unresolved
{
    /// <summary>Every texture reference across the archive that resolves to nothing, by file name.</summary>
    public static int Run(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var missing = new Dictionary<string, (int Models, string Example)>(StringComparer.OrdinalIgnoreCase);
        var replaceables = new Dictionary<uint, int>();
        int models = 0, magentaModels = 0;
        foreach (var entry in index.Models.Where(m => !m.IsPortrait).Take(limit))
        {
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            models++;
            bool any = false;
            // One cache per model: a shared one would hold every decoded 2048-square in memory.
            var textures = new Wc3TextureCache(storage, index) { PreferHd = entry.IsHd };
            foreach (var tex in model.Textures)
            {
                if (tex.IsTeamColor || tex.IsTeamGlow) continue;
                if (tex.FileName.Length == 0) { replaceables[tex.ReplaceableId] = replaceables.GetValueOrDefault(tex.ReplaceableId) + 1; any = true; continue; }
                if (textures.Load(entry.CascName, tex) is not null) continue;
                any = true;
                var cur = missing.TryGetValue(tex.FileName, out var prev) ? prev : (Models: 0, Example: entry.RelativePath);
                missing[tex.FileName] = (cur.Models + 1, cur.Example);
            }
            if (any) magentaModels++;
        }
        Console.WriteLine($"{models} models, {magentaModels} with an unresolved or bare replaceable reference");
        Console.WriteLine("bare replaceable IDs still without a file:");
        foreach (var (id, n) in replaceables.OrderBy(kv => kv.Key)) Console.WriteLine($"  id {id}: {n} entries");
        Console.WriteLine($"unresolved references ({missing.Count}):");
        foreach (var (name, (n, ex)) in missing.OrderByDescending(kv => kv.Value.Models).Take(80))
            Console.WriteLine($"  {n,4}  {name}   e.g. {ex}");
        return 0;
    }
}
