using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Answers what happens to a <b>loose</b> model's textures — the custom-model case, where the .mdx
/// comes off disk with no archive prefix and its TEXS entries name stock game art the author never
/// shipped. It reports, per texture, whether the reference resolved and from where, then runs the
/// real export and counts what actually landed in the package.
/// </summary>
/// <remarks>
/// The interesting failure is silent: an unresolved reference exports as magenta rather than as an
/// error, so counting exported files is not enough — the provenance of each one has to be named.
/// <para>
/// <c>--mangle</c> is the part that tests the real world. Stock models spell their texture paths
/// exactly, so they prove nothing about a downloaded model; the manglings below are the spellings
/// custom models actually carry, applied to references known to exist, so a failure is unambiguous.
/// </para>
/// </remarks>
public static class LooseProbe
{
    /// <summary>
    /// How custom models spell a stock texture path, and why. Each takes the model's real reference
    /// and returns the author's version of it — every one of these must still resolve.
    /// </summary>
    private static readonly (string Name, Func<string, string> Apply)[] Manglings =
    [
        ("as authored",       p => p),
        ("bare file name",    p => Leaf(p)),                                   // re-linked in Magos/Mdlvis
        ("forward slashes",   p => p.Replace('\\', '/')),                      // exported from a Mac/Blender chain
        ("upper case",        p => p.ToUpperInvariant()),
        ("map import path",   p => @"war3mapImported\" + Leaf(p)),             // imported into a map, then re-exported
        ("author's desktop",  p => @"C:\Users\someone\Desktop\wip\" + Leaf(p)),// absolute path baked in by the exporter
        ("leading separator", p => "\\" + p),
        ("tga extension",     p => Path.ChangeExtension(p, ".tga")),           // authored against the source art
        ("no extension",      p => p[..Math.Max(0, p.LastIndexOf('.'))]),
        // The realistic mixed case: an author re-imports some of a model's textures and leaves the
        // rest alone, so a few paths survive to hint at where the flattened ones belong.
        ("half re-imported",  p => p.Length % 2 == 0 ? @"war3mapImported\" + Leaf(p) : p),
    ];

    private static string Leaf(string p)
    {
        int i = p.LastIndexOfAny(['\\', '/']);
        return i < 0 ? p : p[(i + 1)..];
    }

    public static int Run(string install, string target, string? outDir, bool mangle)
    {
        using var storage = new Wc3Storage(install);
        Console.WriteLine("enumerating storage…");
        var index = storage.BuildIndex();
        Console.WriteLine($"  {index.AllNames.Count:N0} names, {index.TextureLookup.Count:N0} distinct texture paths\n");

        // "casc:<n>" runs the sweep against n archive models read as if they were loose files. Five
        // hand-picked models cannot show how often two stock textures share a file name; a spread
        // across the whole archive can.
        List<(string File, MdxModel Model)> models;
        if (target.StartsWith("casc:", StringComparison.OrdinalIgnoreCase))
        {
            int want = int.TryParse(target[5..], out int n) ? n : 400;
            var all = index.Models.Where(m => !m.IsPortrait).ToList();
            int step = Math.Max(1, all.Count / want);
            models = [];
            for (int i = 0; i < all.Count && models.Count < want; i += step)
            {
                var raw = storage.TryReadFile(all[i].CascName);
                if (raw is null) continue;
                try { models.Add((all[i].RelativePath, MdxReader.Read(raw))); }
                catch (Exception) { /* a model this probe cannot read says nothing about textures */ }
            }
            Console.WriteLine($"  {models.Count} archive models read as loose files\n");
        }
        else
        {
            var files = Directory.Exists(target)
                ? Directory.GetFiles(target, "*.mdx", SearchOption.AllDirectories).OrderBy(f => f).ToList()
                : [target];
            if (files.Count == 0) { Console.WriteLine($"no .mdx under {target}"); return 1; }
            if (!mangle) return RunPlain(storage, index, files, outDir);
            models = files.Select(f => (f, MdxReader.Read(File.ReadAllBytes(f)))).ToList();
        }

        return mangle ? RunMangled(storage, index, models) : RunPlain(storage, index, [target], outDir);
    }

    private static int RunPlain(Wc3Storage storage, Wc3AssetIndex index, List<string> files, string? outDir)
    {
        int totalRefs = 0, totalResolved = 0, modelsWithMisses = 0;
        foreach (string file in files)
        {
            var model = MdxReader.Read(File.ReadAllBytes(file));
            Console.WriteLine($"{Path.GetFileName(file)}  ({(model.IsReforged ? "HD" : "SD")}, {model.Textures.Count} TEXS)");

            var cache = LooseCache(storage, index, model, file);
            int misses = 0;
            foreach (var tex in model.Textures)
            {
                if (tex.IsTeamColor || tex.IsTeamGlow) continue;
                totalRefs++;
                var img = cache.Load("", tex);
                if (img is null)
                {
                    misses++;
                    Console.WriteLine($"    MISSING {tex.FileName}"
                                      + (tex.IsReplaceable ? $"  <replaceable {tex.ReplaceableId}>" : ""));
                }
                else
                {
                    totalResolved++;
                    var o = cache.OriginOf("", tex)!.Value;
                    Console.WriteLine($"    {Tag(o.Source)} {o}");
                }
            }
            if (misses > 0) modelsWithMisses++;

            if (outDir is not null) ExportOne(model, cache, file, outDir);
            Console.WriteLine();
        }

        Console.WriteLine($"{totalResolved}/{totalRefs} references resolved; "
                          + $"{modelsWithMisses}/{files.Count} models have at least one miss");
        return totalResolved == totalRefs ? 0 : 1;
    }

    /// <summary>
    /// Re-spells each model's texture references the way a custom model would and re-resolves them.
    /// A mangling that loses a reference is a model the converter would export as magenta.
    /// </summary>
    private static int RunMangled(Wc3Storage storage, Wc3AssetIndex index,
                                  List<(string File, MdxModel Model)> models)
    {
        int failed = 0;

        // What each reference resolves to unmangled, computed once — this is the answer key, and
        // recomputing it per mangling would be 9x the archive reads for the same result.
        var truth = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, model) in models)
        {
            var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
            foreach (var tex in model.Textures)
            {
                if (tex.IsTeamColor || tex.IsTeamGlow || tex.FileName.Length == 0) continue;
                if (truth.ContainsKey(tex.FileName)) continue;
                if (cache.Load("", tex) is not null) truth[tex.FileName] = cache.OriginOf("", tex)!.Value.From;
            }
        }
        Console.WriteLine($"  {truth.Count} distinct texture references resolve as authored\n");

        foreach (var (name, apply) in Manglings)
        {
            int refs = 0, ok = 0, byName = 0;
            var lost = new List<string>();
            var wrongFile = new List<string>();

            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (_, model) in models)
            {
                // A fresh cache per mangling, with no local root: only the game install can answer.
                var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
                foreach (var tex in model.Textures)
                {
                    if (tex.IsTeamColor || tex.IsTeamGlow || tex.FileName.Length == 0) continue;
                    if (!truth.TryGetValue(tex.FileName, out string? expect)) continue;   // never resolved anyway
                    string mangled = apply(tex.FileName);
                    if (mangled.Length == 0) continue;

                    // Always resolve, so the cache accumulates this model's folder context exactly
                    // as a real export would; count each distinct reference only once.
                    var probe = new MdxTexture { FileName = mangled, ReplaceableId = tex.ReplaceableId };
                    var got = cache.Load("", probe);
                    if (!done.Add(tex.FileName)) continue;
                    refs++;
                    if (got is null) { lost.Add(mangled); continue; }
                    ok++;

                    var o = cache.OriginOf("", probe)!.Value;
                    if (o.Source == TextureSource.GameInstallByName) byName++;

                    // Resolving is not enough — it has to resolve to the same ART the unmangled
                    // reference does, or the export quietly ships someone else's texture. Path
                    // equality is too strict a test: Warcraft publishes the same texture under
                    // several paths, so a different path with identical bytes is not a defect.
                    if (!SameAsset(expect, o.From) && !SameBytes(storage, expect, o.From))
                        wrongFile.Add($"{mangled}: got {o.From}, want {expect}");
                }
            }

            string verdict = lost.Count == 0 && wrongFile.Count == 0 ? "OK  " : "FAIL";
            if (verdict == "FAIL") failed++;
            Console.WriteLine($"  {verdict} {name,-18} {ok}/{refs} resolved"
                              + (byName > 0 ? $" ({byName} by file name)" : ""));
            foreach (string l in lost.Distinct().Take(4)) Console.WriteLine($"         lost: {l}");
            if (lost.Distinct().Count() > 4) Console.WriteLine($"         lost: (+{lost.Distinct().Count() - 4} more)");
            foreach (string w in wrongFile.Distinct().Take(4)) Console.WriteLine($"         wrong: {w}");
            if (wrongFile.Distinct().Count() > 4) Console.WriteLine($"         wrong: (+{wrongFile.Distinct().Count() - 4} more)");
        }

        Console.WriteLine($"\n{Manglings.Length - failed}/{Manglings.Length} manglings resolve to the right file");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Two CASC names name the same asset when their paths after the archive prefix agree.</summary>
    private static bool SameAsset(string a, string b) =>
        Wc3AssetIndex.StripArchivePrefix(a).Relative
            .Equals(Wc3AssetIndex.StripArchivePrefix(b).Relative, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when two archive files hold identical bytes — Warcraft republishes textures.</summary>
    private static bool SameBytes(Wc3Storage storage, string a, string b) => Hash(storage, a) == Hash(storage, b);

    private static string Hash(Wc3Storage storage, string cascName)
    {
        if (HashCache.TryGetValue(cascName, out string? h)) return h;
        var data = storage.TryReadFile(cascName);
        h = data is null ? "?" + cascName : System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        HashCache[cascName] = h;
        return h;
    }

    /// <summary>Exactly what MainWindow.OpenLooseFileAsync builds for a model opened off disk.</summary>
    private static Wc3TextureCache LooseCache(Wc3Storage storage, Wc3AssetIndex index, MdxModel model, string file)
    {
        var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
        cache.LocalRoots.Add(Path.GetDirectoryName(Path.GetFullPath(file))!);
        return cache;
    }

    private static void ExportOne(MdxModel model, Wc3TextureCache cache, string file, string outDir)
    {
        string mname = Path.GetFileNameWithoutExtension(file);
        var opts = new M3ExportOptions { Lod = 0, ModelName = mname };
        var res = new M3Exporter(model, opts).Export(cache, "");
        string d = Path.Combine(outDir, mname);
        string td = Path.Combine(d, opts.TextureFolder);
        Directory.CreateDirectory(td);
        File.WriteAllBytes(Path.Combine(d, mname + ".m3"), res.M3);
        foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(td, t.FileName), t.Data);
        Console.WriteLine($"    export: {res.M3.Length:N0} B, {res.Textures.Count} textures -> {d}");
        foreach (string line in res.Log) Console.WriteLine("      | " + line);
    }

    private static string Tag(TextureSource s) => s switch
    {
        TextureSource.BesideModel => "local ",
        TextureSource.GameInstallByName => "byname",
        _ => "game  ",
    };
}
