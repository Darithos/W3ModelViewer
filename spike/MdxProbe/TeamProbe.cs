using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Censuses where Warcraft III's team-colour signal actually lives, SD and HD, so the exporter can
/// carry it into StarCraft II's Team Color Emissive Add channel instead of baking a fixed red.
/// </summary>
public static class TeamProbe
{
    /// <summary>Per-slot alpha histogram for one model — is any channel a team mask?</summary>
    public static int Slots(string install, string[] names)
    {
        using var storage = new Wc3Storage(install);
        var cache = new Wc3TextureCache(storage);
        foreach (var name in names)
        {
            var raw = storage.TryReadFile(name);
            if (raw is null) { Console.WriteLine("### " + name + ": NOT FOUND"); continue; }
            var model = MdxReader.Read(raw);
            Console.WriteLine("");
            Console.WriteLine("### " + name);
            for (int mi = 0; mi < model.Materials.Count; mi++)
            {
                foreach (var layer in model.Materials[mi].Layers)
                {
                    Console.WriteLine($"  mat {mi} filter={layer.FilterMode} pbr={layer.IsPbr} tcMult={layer.TeamColorMultiplier:0.###}");
                    foreach (MdxTextureSlot slot in Enum.GetValues<MdxTextureSlot>())
                    {
                        int id = layer.Slot(slot);
                        if (id < 0 || (uint)id >= (uint)model.Textures.Count) continue;
                        var tex = model.Textures[id];
                        if (tex.IsReplaceable) { Console.WriteLine($"    {slot,-14} <repl {tex.ReplaceableId}>"); continue; }
                        var img = cache.Load(name, tex);
                        string file = Path.GetFileName(tex.FileName.Replace((char)92, '/'));
                        if (img is null) { Console.WriteLine($"    {slot,-14} {file,-46} UNRESOLVED"); continue; }
                        Console.WriteLine($"    {slot,-14} {file,-46} {AlphaStats(img)}");
                    }
                }
            }
        }
        return 0;
    }

    /// <summary>Writes every slot texture of a model to raw {w,h,RGBA} files for offline eyeballing.</summary>
    public static int DumpSlots(string install, string outDir, string name)
    {
        using var storage = new Wc3Storage(install);
        var cache = new Wc3TextureCache(storage);
        Directory.CreateDirectory(outDir);
        var raw = storage.TryReadFile(name);
        if (raw is null) { Console.WriteLine("NOT FOUND " + name); return 1; }
        var model = MdxReader.Read(raw);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int mi = 0; mi < model.Materials.Count; mi++)
        foreach (var layer in model.Materials[mi].Layers)
        foreach (MdxTextureSlot slot in Enum.GetValues<MdxTextureSlot>())
        {
            int id = layer.Slot(slot);
            if (id < 0 || (uint)id >= (uint)model.Textures.Count) continue;
            var tex = model.Textures[id];
            if (tex.IsReplaceable) continue;
            string file = Path.GetFileNameWithoutExtension(tex.FileName.Replace((char)92, '/'));
            if (!seen.Add(file + "|" + slot)) continue;
            var img = cache.Load(name, tex);
            if (img is null) continue;
            string dest = Path.Combine(outDir, $"{slot}_{file}.raw");
            using var fs = File.Create(dest);
            using var bw = new BinaryWriter(fs);
            bw.Write(img.Width); bw.Write(img.Height); bw.Write(img.Pixels);
            Console.WriteLine("  wrote " + dest);
        }
        return 0;
    }

    /// <summary>Dumps what the viewport would draw: Compose's output per material.</summary>
    public static int Composites(string install, string outDir, string name, int slot = 0)
    {
        using var storage = new Wc3Storage(install);
        var cache = new Wc3TextureCache(storage);
        Directory.CreateDirectory(outDir);
        var raw = storage.TryReadFile(name);
        if (raw is null) { Console.WriteLine("NOT FOUND " + name); return 1; }
        var model = MdxReader.Read(raw);
        for (int mi = 0; mi < model.Materials.Count; mi++)
        {
            var c = MaterialCompositor.Compose(model, model.Materials[mi], cache, name, slot);
            var mask = MaterialCompositor.TeamMaskOf(model, model.Materials[mi], cache, name);
            string dest = Path.Combine(outDir, $"mat{mi}.raw");
            using var fs = File.Create(dest);
            using var bw = new BinaryWriter(fs);
            bw.Write(c.Texture.Width); bw.Write(c.Texture.Height); bw.Write(c.Texture.Pixels);
            Console.WriteLine($"  mat {mi}: {c.Blend,-10} {c.Texture.Width}x{c.Texture.Height} "
                            + $"teamMask={(mask is null ? "none" : $"{mask.Coverage * 100:0.0}%")}  {dest}");
        }
        return 0;
    }

    /// <summary>Exports many models in memory and reports how the player-colour channel landed.</summary>
    public static int Sweep(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = Wc3AssetIndex.FromNames(storage.EnumerateAll());
        int ok = 0, failed = 0;
        var perSet = new Dictionary<Wc3ArtSet, (int Models, int WithTeam, int Textures)>();

        foreach (var set in new[] { Wc3ArtSet.Classic, Wc3ArtSet.Reforged })
        {
            int models = 0, withTeam = 0, teamTex = 0;
            foreach (var entry in index.Models
                         .Where(m => !m.IsPortrait && m.ArtSet == set
                                  && m.RelativePath.StartsWith("units", StringComparison.OrdinalIgnoreCase))
                         .Take(limit))
            {
                var raw = storage.TryReadFile(entry.CascName);
                if (raw is null) continue;
                var textures = new Wc3TextureCache(storage) { PreferHd = set == Wc3ArtSet.Reforged };
                try
                {
                    var mdl = MdxReader.Read(raw);
                    var opt = new Wc3ModelViewer.Core.Convert.M3ExportOptions { Lod = 0, ModelName = entry.Name };
                    var res = new Wc3ModelViewer.Core.Convert.M3Exporter(mdl, opt).Export(textures, entry.CascName);
                    int t = res.Textures.Count(x => x.FileName.EndsWith("_team.dds", StringComparison.OrdinalIgnoreCase));
                    models++; ok++;
                    if (t > 0) { withTeam++; teamTex += t; }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  FAILED {entry.Name}: {ex.GetType().Name} {ex.Message}");
                }
            }
            perSet[set] = (models, withTeam, teamTex);
        }
        Console.WriteLine("");
        foreach (var (set, v) in perSet)
            Console.WriteLine($"{set,-9} {v.Models,4} models exported, {v.WithTeam,4} carry a player-colour channel "
                            + $"({v.Textures} team masks)");
        Console.WriteLine($"total: {ok} exported, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Census of the team-GLOW slot (replaceable 2): who uses it, on what, and how.</summary>
    public static int Glow(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = Wc3AssetIndex.FromNames(storage.EnumerateAll());

        foreach (string name in storage.EnumerateAll()
                     .Where(n => n.Contains("teamglow", StringComparison.OrdinalIgnoreCase))
                     .Take(8))
            Console.WriteLine("  archive art: " + name);

        foreach (var set in new[] { Wc3ArtSet.Classic, Wc3ArtSet.Reforged })
        {
            int parsed = 0, models = 0, layers = 0, emitters = 0, multiLayer = 0;
            var filters = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var examples = new List<string>();
            foreach (var entry in index.Models
                         .Where(m => !m.IsPortrait && m.ArtSet == set
                                  && m.RelativePath.StartsWith("units", StringComparison.OrdinalIgnoreCase))
                         .Take(limit))
            {
                var raw = storage.TryReadFile(entry.CascName);
                if (raw is null) continue;
                MdxModel model;
                try { model = MdxReader.Read(raw); } catch { continue; }
                parsed++;
                bool any = false;
                for (int mi = 0; mi < model.Materials.Count; mi++)
                {
                    var mat = model.Materials[mi];
                    foreach (var layer in mat.Layers)
                    {
                        int d = layer.DiffuseTextureId;
                        if ((uint)d >= (uint)model.Textures.Count || !model.Textures[d].IsTeamGlow) continue;
                        layers++; any = true;
                        string k = layer.FilterMode.ToString();
                        filters[k] = filters.GetValueOrDefault(k) + 1;
                        if (mat.Layers.Count > 1) multiLayer++;
                        if (examples.Count < 6)
                            examples.Add($"    {entry.Name} mat {mi} ({mat.Layers.Count} layer(s)): "
                                + string.Join(" + ", mat.Layers.Select(l =>
                                {
                                    int di = l.DiffuseTextureId;
                                    string t = (uint)di < (uint)model.Textures.Count
                                        ? (model.Textures[di].IsReplaceable ? $"<repl {model.Textures[di].ReplaceableId}>"
                                           : Path.GetFileName(model.Textures[di].FileName.Replace((char)92, '/')))
                                        : "?";
                                    return $"{t}[{l.FilterMode}]";
                                })));
                    }
                }
                foreach (var e in model.ParticleEmitters)
                    if (e.ReplaceableId == 2
                        || ((uint)e.TextureId < (uint)model.Textures.Count && model.Textures[e.TextureId].IsTeamGlow))
                    { emitters++; any = true; }
                if (any) models++;
            }
            Console.WriteLine("");
            Console.WriteLine($"=== {set}: {parsed} unit models, {models} use the team-GLOW slot ===");
            Console.WriteLine($"  {layers} geoset layers ({multiLayer} of them inside a multi-layer material), {emitters} particle emitters");
            Console.WriteLine("  filter modes: " + string.Join(", ", filters.Select(kv => $"{kv.Key} x{kv.Value}")));
            foreach (var e in examples) Console.WriteLine(e);
        }
        return 0;
    }

    /// <summary>Dumps one texture straight out of the archive by name, for offline eyeballing.</summary>
    public static int RawTex(string install, string cascName, string outFile)
    {
        using var storage = new Wc3Storage(install);
        var cache = new Wc3TextureCache(storage);
        var img = cache.Load("", new MdxTexture { FileName = cascName });
        if (img is null) { Console.WriteLine("UNRESOLVED " + cascName); return 1; }
        using var fs = File.Create(outFile);
        using var bw = new BinaryWriter(fs);
        bw.Write(img.Width); bw.Write(img.Height); bw.Write(img.Pixels);
        Console.WriteLine($"  {cascName} -> {img.Width}x{img.Height}  {AlphaStats(img)}");
        return 0;
    }

    private static string AlphaStats(RgbaImage img)
    {
        int n = img.Pixels.Length / 4;
        int zero = 0, full = 0; long sum = 0;
        var buckets = new int[8];
        for (int i = 3; i < img.Pixels.Length; i += 4)
        {
            int a = img.Pixels[i];
            sum += a;
            if (a == 0) zero++; else if (a == 255) full++;
            buckets[a * 8 / 256]++;
        }
        string hist = string.Join(" ", buckets.Select(b => $"{b * 100.0 / n:0}"));
        return $"{img.Width}x{img.Height} a mean={sum / (double)n:0} zero={zero * 100.0 / n:0.0}% full={full * 100.0 / n:0.0}% oct[{hist}]";
    }

    /// <summary>Classifies every HD ORM texture's alpha across the unit tree: mask, empty, or flat.</summary>
    public static int Orm(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = Wc3AssetIndex.FromNames(storage.EnumerateAll());
        var cache = new Wc3TextureCache(storage);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int mask = 0, empty = 0, flatFull = 0, other = 0;
        var flats = new List<string>();
        var masks = new List<string>();

        foreach (var entry in index.Models.Where(m => !m.IsPortrait && m.ArtSet == Wc3ArtSet.Reforged
                     && m.RelativePath.StartsWith("units", StringComparison.OrdinalIgnoreCase)).Take(limit))
        {
            var raw = storage.TryReadFile(entry.CascName);
            if (raw is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(raw); } catch { continue; }
            foreach (var mat in model.Materials)
            foreach (var layer in mat.Layers)
            {
                int id = layer.Slot(MdxTextureSlot.Orm);
                if (id < 0 || (uint)id >= (uint)model.Textures.Count) continue;
                var tex = model.Textures[id];
                if (tex.IsReplaceable || !seen.Add(tex.FileName)) continue;
                var img = cache.Load(entry.CascName, tex);
                if (img is null) continue;
                int n = img.Pixels.Length / 4, lo = 0, hi = 0;
                for (int i = 3; i < img.Pixels.Length; i += 4)
                {
                    if (img.Pixels[i] < 64) lo++; else if (img.Pixels[i] > 192) hi++;
                }
                string file = Path.GetFileName(tex.FileName.Replace((char)92, '/'));
                double hiPct = hi * 100.0 / n, loPct = lo * 100.0 / n;
                if (hiPct < 0.1) empty++;
                else if (loPct < 0.1) { flatFull++; if (flats.Count < 12) flats.Add($"{file} hi={hiPct:0.0}%"); }
                else if (hiPct + loPct > 95) { mask++; if (masks.Count < 12) masks.Add($"{file} team={hiPct:0.0}%"); }
                else other++;
            }
        }
        Console.WriteLine("");
        Console.WriteLine($"HD ORM textures: {mask} bimodal mask, {empty} all-empty (no team), {flatFull} all-full (suspect), {other} other");
        Console.WriteLine("  masks:"); foreach (var m in masks) Console.WriteLine("    " + m);
        Console.WriteLine("  all-full:"); foreach (var f in flats) Console.WriteLine("    " + f);
        return 0;
    }

    public static int Run(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = Wc3AssetIndex.FromNames(storage.EnumerateAll());

        foreach (var set in new[] { Wc3ArtSet.Classic, Wc3ArtSet.Reforged })
        {
            var models = index.Models
                .Where(m => !m.IsPortrait && m.ArtSet == set
                         && m.RelativePath.StartsWith("units", StringComparison.OrdinalIgnoreCase))
                .Take(limit).ToList();

            int parsed = 0, withTeam = 0;
            int matsTotal = 0, matsWithTeamLayer = 0, matsTeamAsDiffuse = 0, matsStack = 0;
            int layersPbr = 0, layersSlot4 = 0, multNonZero = 0;
            var examples = new List<string>();

            foreach (var entry in models)
            {
                var raw = storage.TryReadFile(entry.CascName);
                if (raw is null) continue;
                MdxModel model;
                try { model = MdxReader.Read(raw); } catch { continue; }
                parsed++;
                bool modelHasTeam = false;

                for (int mi = 0; mi < model.Materials.Count; mi++)
                {
                    var mat = model.Materials[mi];
                    matsTotal++;
                    bool teamLayer = false, teamDiffuse = false;
                    foreach (var layer in mat.Layers)
                    {
                        if (layer.IsPbr)
                        {
                            layersPbr++;
                            if (layer.Slot(MdxTextureSlot.TeamColor) >= 0) layersSlot4++;
                            if (layer.TeamColorMultiplier != 0) multNonZero++;
                        }
                        int d = layer.DiffuseTextureId;
                        if ((uint)d < (uint)model.Textures.Count && model.Textures[d].IsTeamColor)
                        { teamLayer = true; if (mat.Layers.Count == 1) teamDiffuse = true; }
                    }
                    if (teamLayer)
                    {
                        matsWithTeamLayer++;
                        modelHasTeam = true;
                        if (teamDiffuse) matsTeamAsDiffuse++;
                        if (mat.Layers.Count > 1) matsStack++;
                        if (examples.Count < 8)
                            examples.Add($"    {entry.Name} mat {mi}: " + string.Join(" + ", mat.Layers.Select(l =>
                            {
                                int di = l.DiffuseTextureId;
                                string t = (uint)di < (uint)model.Textures.Count
                                    ? (model.Textures[di].IsReplaceable ? $"<repl {model.Textures[di].ReplaceableId}>"
                                       : Path.GetFileName(model.Textures[di].FileName.Replace((char)92, '/')))
                                    : "?";
                                return $"{t}[{l.FilterMode}{(l.IsPbr ? ",pbr" : "")}]";
                            })));
                    }
                }
                if (modelHasTeam) withTeam++;
            }

            Console.WriteLine("");
            Console.WriteLine($"=== {set} unit models: {parsed} parsed, {withTeam} carry a team-colour texture ===");
            Console.WriteLine($"  materials: {matsTotal} total, {matsWithTeamLayer} reference replaceable 1 "
                            + $"({matsTeamAsDiffuse} as a single-layer diffuse, {matsStack} as a multi-layer stack)");
            if (layersPbr > 0)
                Console.WriteLine($"  HD layers: {layersPbr}, slot-4 bound {layersSlot4}, teamColorMultiplier != 0 on {multNonZero}");
            foreach (var e in examples) Console.WriteLine(e);
        }
        return 0;
    }
}
