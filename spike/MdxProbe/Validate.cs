using System.Numerics;
using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Parses many real models and checks the results are internally consistent. A parser bug on a
/// binary format usually shows up as nonsense values rather than an exception, so this asserts on
/// the meaning of what came back, not merely that reading finished.
/// </summary>
public static class Validate
{
    public static int Run(string install, int limit)
    {
        using var storage = new Wc3Storage(install);

        var paths = UnitPaths().ToList();
        Console.WriteLine($"Validating up to {limit} models from {paths.Count} candidate unit paths\n");

        int okCount = 0, failCount = 0, missing = 0, tested = 0;
        var problems = new List<string>();

        foreach (string relative in paths)
        {
            if (tested >= limit) break;
            foreach (string prefix in new[] { "war3.w3mod:", "war3.w3mod:_hd.w3mod:" })
            {
                if (tested >= limit) break;
                string name = prefix + relative;
                var bytes = storage.TryReadFile(name);
                if (bytes is null) { missing++; continue; }

                tested++;
                string label = $"{(prefix.Contains("_hd") ? "HD" : "SD")} {relative}";
                try
                {
                    var m = MdxReader.Read(bytes);
                    var issues = Check(m);
                    if (issues.Count == 0)
                    {
                        okCount++;
                        int hdLayers = m.Materials.Sum(x => x.Layers.Count(l => l.IsPbr));
                        Console.WriteLine($"  ok    {label,-52} v{m.Version} {(m.IsReforged ? "HD" : "SD")} " +
                                          $"geo={m.Geosets.Count,3} verts={m.TotalVertices,7:N0} tris={m.TotalTriangles,7:N0} " +
                                          $"nodes={m.Nodes.Count,4} seq={m.Sequences.Count,3} mat={m.Materials.Count,3} " +
                                          $"tex={m.Textures.Count,3} pbr={hdLayers,3} lods=[{string.Join(",", m.LodLevels)}]");
                    }
                    else
                    {
                        failCount++;
                        Console.WriteLine($"  BAD   {label}");
                        foreach (string i in issues.Take(6)) Console.WriteLine($"          - {i}");
                        problems.AddRange(issues.Select(i => $"{label}: {i}"));
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    Console.WriteLine($"  THROW {label}\n          {ex.GetType().Name}: {ex.Message}");
                    problems.Add($"{label}: {ex.Message}");
                }
            }
        }

        Console.WriteLine($"\n==== {okCount} clean, {failCount} with problems, {missing} paths absent ====");
        if (problems.Count > 0)
        {
            // Group by the problem text alone (drop the "<model>: " prefix) to see which defects recur.
            Console.WriteLine("\ndistinct problems:");
            foreach (var g in problems.GroupBy(Describe).OrderByDescending(g => g.Count()).Take(20))
                Console.WriteLine($"  {g.Count(),4}x  {g.Key}");
        }
        return failCount == 0 ? 0 : 1;

        static string Describe(string problem)
        {
            int i = problem.IndexOf(": ", StringComparison.Ordinal);
            return i > 0 ? problem[(i + 2)..] : problem;
        }
    }

    /// <summary>Consistency checks over a parsed model. Each returned string is a real defect.</summary>
    private static List<string> Check(MdxModel m)
    {
        var issues = new List<string>();

        if (m.Version == 0) issues.Add("no VERS chunk");
        if (m.Geosets.Count == 0) issues.Add("no geosets");
        // Skipped chunks (PRE2/CORN/RIBB/LITE/FAFX/BPOS) are a deliberate scope decision, not a defect.

        foreach (var g in m.Geosets)
        {
            string at = $"geoset {g.Index}";
            if (g.VertexCount == 0) { issues.Add($"{at}: no vertices"); continue; }
            if (g.Normals.Length != g.VertexCount) issues.Add($"{at}: {g.Normals.Length} normals for {g.VertexCount} vertices");
            if (g.Indices.Length == 0) issues.Add($"{at}: no indices");
            if (g.Indices.Length % 3 != 0) issues.Add($"{at}: index count {g.Indices.Length} is not a multiple of 3");

            int maxIndex = g.Indices.Length > 0 ? g.Indices.Max() : -1;
            if (maxIndex >= g.VertexCount) issues.Add($"{at}: index {maxIndex} exceeds vertex count {g.VertexCount}");

            if (g.UvLayers.Count == 0) issues.Add($"{at}: no UV layer");
            else foreach (var (uv, i) in g.UvLayers.Select((u, i) => (u, i)))
                if (uv.Length != g.VertexCount) issues.Add($"{at}: UV layer {i} has {uv.Length} entries for {g.VertexCount} vertices");

            if ((uint)g.MaterialId >= (uint)m.Materials.Count) issues.Add($"{at}: materialId {g.MaterialId} outside 0..{m.Materials.Count - 1}");

            // Geometry must be finite and of a plausible magnitude. Warcraft III units are on the
            // order of 100 units tall, so anything past 1e5 means the read head has drifted.
            // (The geosets' own declared bounds are NOT checked: Blizzard's per-geoset extents are
            // routinely tighter than the geometry, so a mismatch says nothing about the parse.)
            int insane = g.Positions.Count(p => !float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)
                                             || Math.Abs(p.X) > 1e5f || Math.Abs(p.Y) > 1e5f || Math.Abs(p.Z) > 1e5f);
            if (insane > 0) issues.Add($"{at}: {insane} vertices are non-finite or absurdly large");

            int badNormals = g.Normals.Count(n => !float.IsFinite(n.X) || !float.IsFinite(n.Y) || !float.IsFinite(n.Z) || n.Length() > 4f);
            if (badNormals > g.VertexCount / 20) issues.Add($"{at}: {badNormals} normals are non-finite or not unit length");

            int badUvs = g.Uvs.Count(u => !float.IsFinite(u.X) || !float.IsFinite(u.Y) || Math.Abs(u.X) > 1e3f || Math.Abs(u.Y) > 1e3f);
            if (badUvs > 0) issues.Add($"{at}: {badUvs} UVs are non-finite or wildly out of range");

            if (g.HasSkin)
            {
                int bad = 0, zeroWeight = 0;
                for (int v = 0; v < g.VertexCount; v++)
                {
                    int sum = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        sum += g.SkinBoneWeights[v * 4 + k];
                        if (g.SkinBoneIndices[v * 4 + k] >= m.Nodes.Count && g.SkinBoneWeights[v * 4 + k] > 0) bad++;
                    }
                    if (sum == 0) zeroWeight++;
                }
                if (bad > 0) issues.Add($"{at}: {bad} skin influences reference a node beyond {m.Nodes.Count}");
                if (zeroWeight > 0) issues.Add($"{at}: {zeroWeight} vertices have zero total skin weight");
            }
            else
            {
                if (g.VertexGroups.Length != g.VertexCount) issues.Add($"{at}: {g.VertexGroups.Length} vertex groups for {g.VertexCount} vertices");
                if (g.MatrixGroupSizes.Length == 0) issues.Add($"{at}: no matrix groups");
                if (g.MatrixGroupSizes.Sum() != g.MatrixIndices.Length)
                    issues.Add($"{at}: matrix group sizes total {g.MatrixGroupSizes.Sum()} but MATS has {g.MatrixIndices.Length}");
                int maxGroup = g.VertexGroups.Length > 0 ? g.VertexGroups.Max() : 0;
                if (g.MatrixGroupSizes.Length > 0 && maxGroup >= g.MatrixGroupSizes.Length)
                    issues.Add($"{at}: vertex group {maxGroup} exceeds {g.MatrixGroupSizes.Length} groups");
            }
        }

        foreach (var mat in m.Materials)
            foreach (var layer in mat.Layers)
            {
                if (layer.TextureId >= 0 && layer.TextureId >= m.Textures.Count)
                    issues.Add($"layer textureId {layer.TextureId} outside 0..{m.Textures.Count - 1}");
                foreach (var (slot, texId) in layer.TextureSlots)
                    if ((uint)texId >= (uint)m.Textures.Count)
                        issues.Add($"layer slot {slot} -> texture {texId} outside 0..{m.Textures.Count - 1}");
            }

        // Node hierarchy must be acyclic and reference real parents.
        foreach (var n in m.Nodes)
        {
            if (n.ParentId == -1) continue;
            if (m.Nodes.All(o => o.ObjectId != n.ParentId))
                issues.Add($"node '{n.Name}' has parent {n.ParentId} which no node declares");
        }
        if (m.Pivots.Count > 0)
            foreach (var n in m.Nodes)
                if ((uint)n.ObjectId >= (uint)m.Pivots.Count)
                    issues.Add($"node '{n.Name}' objectId {n.ObjectId} outside {m.Pivots.Count} pivots");

        foreach (var s in m.Sequences)
            if (s.IntervalEnd < s.IntervalStart) issues.Add($"sequence '{s.Name}' ends before it starts");

        foreach (var ga in m.GeosetAnims)
            if (ga.GeosetId >= m.Geosets.Count) issues.Add($"geoset anim references geoset {ga.GeosetId} of {m.Geosets.Count}");

        return issues;
    }

    /// <summary>A spread of real unit/building/hero models across every race.</summary>
    private static IEnumerable<string> UnitPaths()
    {
        (string Race, string[] Units)[] sets =
        [
            ("human", ["knight", "footman", "rifleman", "peasant", "sorceress", "priest", "militia",
                       "gyrocopter", "mortarteam", "phoenix", "arthas", "jaina", "muradin", "uther", "kael"]),
            ("orc", ["grunt", "peon", "raider", "shaman", "witchdoctor", "taurenchieftain", "farseer",
                     "blademaster", "kodobeast", "wyvern", "troll", "headhunter"]),
            ("undead", ["ghoul", "acolyte", "abomination", "necromancer", "banshee", "crypfiend",
                        "gargoyle", "frostwyrm", "lich", "dreadlord", "deathknight"]),
            ("nightelf", ["archer", "huntress", "druidoftheclaw", "dryad", "wisp", "demonhunter",
                          "keeperofthegrove", "priestessofthemoon", "chimaera", "hippogryph"]),
            ("creeps", ["murloc", "kobold", "gnoll", "bandit", "furbolg", "satyr"]),
        ];

        foreach (var (race, units) in sets)
            foreach (string u in units)
                yield return $@"units\{race}\{u}\{u}.mdx";

        foreach (string b in new[] { "townhall", "farm", "barracks", "blacksmith" })
            yield return $@"buildings\human\{b}\{b}.mdx";
    }
}
