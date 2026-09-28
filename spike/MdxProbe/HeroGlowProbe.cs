using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Census of how each art set draws the hero glow: which PRE2 emitters, PopcornFX (CORN) emitters
/// and geosets carry the player's team glow on hero models, with the fields that decide whether a
/// StarCraft II particle system stays with the unit (model space, life, rate, node).
/// </summary>
public static class HeroGlowProbe
{
    public static int Run(string install, Wc3ArtSet art, string? filter, int limit, bool follow = false)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var models = index.Models.Where(m => !m.IsPortrait && m.ArtSet == art
                                             && m.Name.Contains(filter ?? "hero", StringComparison.OrdinalIgnoreCase));
        int seen = 0;
        var tally = new Dictionary<string, int>();
        foreach (var entry in models.Take(limit))
        {
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            seen++;
            var lines = new List<string>();
            foreach (var e in model.ParticleEmitters)
            {
                bool glow = e.ReplaceableId == 2 || ((uint)e.TextureId < (uint)model.Textures.Count && model.Textures[e.TextureId].IsTeamGlow);
                if (!glow) continue;
                string node = (uint)e.NodeIndex < (uint)model.Nodes.Count ? Chain(model, e.NodeIndex) : "?";
                lines.Add($"  PRE2 '{e.Name}' node {node} modelSpace={e.ModelSpace} life={e.Life:0.##} rate={e.EmissionRate:0.##} speed={e.Speed:0.#} " +
                          $"scale {e.StartScale:0}/{e.MiddleScale:0}/{e.EndScale:0} alpha {e.StartAlpha}/{e.MiddleAlpha}/{e.EndAlpha} " +
                          $"w/l {e.Width:0}/{e.Length:0} squirt={e.Squirt} kp2v={(e.VisibilityTrack?.Count ?? 0)} rateKeys={(e.EmissionRateTrack?.Count ?? 0)}");
                tally[$"PRE2 modelSpace={e.ModelSpace}"] = tally.GetValueOrDefault($"PRE2 modelSpace={e.ModelSpace}") + 1;
                tally[$"PRE2 name '{e.Name}'"] = tally.GetValueOrDefault($"PRE2 name '{e.Name}'") + 1;
            }
            foreach (var c in model.PopcornEmitters)
            {
                string node = (uint)c.NodeIndex < (uint)model.Nodes.Count ? Chain(model, c.NodeIndex) : "?";
                bool glowish = c.EffectPath.Contains("glow", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("glow", StringComparison.OrdinalIgnoreCase);
                if (!glowish) continue;
                lines.Add($"  CORN '{c.Name}' {c.EffectPath} node {node} flags '{c.PopcornFlags}' team {c.TeamColor}");
                tally[$"CORN name '{c.Name}' path '{Path.GetFileName(c.EffectPath)}'"] = tally.GetValueOrDefault($"CORN name '{c.Name}' path '{Path.GetFileName(c.EffectPath)}'") + 1;
            }
            // --follow: attach the PopcornFX stand-ins as an export would and report the space each
            // hero glow layer is written in — world space is the "stain" left behind a walking hero.
            if (follow && model.PopcornEmitters.Any(c => c.IsHeroGlow))
            {
                PopcornApproximation.Attach(model, storage.TryReadFile, entry.CascName);
                foreach (var pe in model.ParticleEmitters.Where(p => p.PopcornEmitter is { IsHeroGlow: true }))
                {
                    lines.Add($"    stand-in {pe.Name,-24} {pe.Orientation,-12} modelSpace={pe.ModelSpace} life={pe.Life:0.##} rate={pe.EmissionRate:0.###} team={pe.TeamColoured} " +
                              $"alpha {pe.StartAlpha}/{pe.MiddleAlpha}/{pe.EndAlpha} scale {pe.StartScale:0}/{pe.MiddleScale:0}/{pe.EndScale:0} spawnNow={pe.SpawnImmediately}");
                    tally[$"stand-in modelSpace={pe.ModelSpace}"] = tally.GetValueOrDefault($"stand-in modelSpace={pe.ModelSpace}") + 1;
                }
            }
            foreach (var g in model.Geosets)
            {
                if ((uint)g.MaterialId >= (uint)model.Materials.Count) continue;
                var mat = model.Materials[g.MaterialId];
                if (!mat.Layers.Any(l => (uint)l.TextureId < (uint)model.Textures.Count && model.Textures[l.TextureId].IsTeamGlow)) continue;
                lines.Add($"  GEOSET {g.Index} '{g.LodName}' lod {g.LodId} verts {g.VertexCount} z=[{g.Positions.Min(v => v.Z):F0},{g.Positions.Max(v => v.Z):F0}]");
                tally["GEOSET team glow"] = tally.GetValueOrDefault("GEOSET team glow") + 1;
            }
            if (lines.Count == 0) tally["(no glow found)"] = tally.GetValueOrDefault("(no glow found)") + 1;
            Console.WriteLine($"{entry.RelativePath}{(lines.Count == 0 ? "   -- no glow" : "")}");
            foreach (string l in lines) Console.WriteLine(l);
        }
        Console.WriteLine($"--- {seen} {art} model(s) ---");
        foreach (var (k, v) in tally.OrderByDescending(p => p.Value)) Console.WriteLine($"  {v,4}  {k}");
        return 0;
    }

    private static string Chain(MdxModel model, int node)
    {
        var names = new List<string>();
        for (int n = node, guard = 0; (uint)n < (uint)model.Nodes.Count && guard < 16; guard++)
        {
            var x = model.Nodes[n];
            names.Add($"{x.Name}{(x.Translation is { Count: > 0 } ? "*" : "")}");
            n = x.ParentId < 0 ? -1 : model.Nodes.FindIndex(p => p.ObjectId == x.ParentId);
        }
        return string.Join(" <- ", names);
    }
}

/// <summary>
/// Every PopcornFX stand-in whose particles never die (<c>invLife = 0</c>) — the layers an export
/// renews once per lifetime — with the blend, player colour and measured ramps that decide how that
/// renewal looks.
/// </summary>
public static class ImmortalProbe
{
    public static int Run(string install, Wc3ArtSet art, string? filter, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var models = index.Models.Where(m => !m.IsPortrait && m.ArtSet == art
                                             && (filter is null || m.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        int seen = 0, withCorn = 0;
        var tally = new Dictionary<string, int>();
        foreach (var entry in models)
        {
            if (withCorn >= limit) break;
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            seen++;
            if (model.PopcornEmitters.Count == 0) continue;
            withCorn++;
            try { PopcornApproximation.Attach(model, storage.TryReadFile, entry.CascName); } catch (Exception ex) { Console.WriteLine($"{entry.RelativePath}: attach failed {ex.Message}"); continue; }
            foreach (var pe in model.ParticleEmitters.Where(p => p.IsPopcorn && p.SpawnImmediately))
            {
                string effect = Path.GetFileNameWithoutExtension(pe.PopcornEmitter?.EffectPath ?? "?");
                bool additive = pe.Blend != MdxParticleBlend.Blend || pe.TeamColoured;
                Console.WriteLine($"{entry.RelativePath,-70} {effect,-28} {pe.Name.Split('/')[^1],-8} {pe.Orientation,-12} {(additive ? "add" : "blend"),-5} team={pe.TeamColoured,-5} " +
                                  $"follow={pe.ModelSpace,-5} alpha {pe.StartAlpha}/{pe.MiddleAlpha}/{pe.EndAlpha} scale {pe.StartScale:0}/{pe.MiddleScale:0}/{pe.EndScale:0} life {pe.Life:0.##} rate {pe.EmissionRate:0.###}");
                string k = $"{(additive ? "add" : "blend")} team={pe.TeamColoured} follow={pe.ModelSpace} {(pe.StartAlpha < 0.8f * pe.EndAlpha ? "fades-in" : "flat-start")}";
                tally[k] = tally.GetValueOrDefault(k) + 1;
                tally["effect " + effect] = tally.GetValueOrDefault("effect " + effect) + 1;
            }
        }
        Console.WriteLine($"--- {withCorn} CORN model(s) of {seen} read ---");
        foreach (var (k, v) in tally.OrderByDescending(p => p.Value)) Console.WriteLine($"  {v,4}  {k}");
        return 0;
    }
}
