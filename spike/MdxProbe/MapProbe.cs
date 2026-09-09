using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Exercises the viewer's texture-mapping path headlessly: resolve every reference, then point one
/// at a chosen file and resolve again. Verifies both that the override wins and that the cached
/// decode of the old file is actually dropped — a stale cache entry would leave the viewport
/// showing the previous texture and look like the mapping had no effect.
/// </summary>
public static class MapProbe
{
    public static int Run(string install, string modelPath, string? reference, string? file)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var model = MdxReader.Read(File.ReadAllBytes(modelPath));
        var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
        cache.LocalRoots.Add(Path.GetDirectoryName(Path.GetFullPath(modelPath))!);

        Console.WriteLine("as resolved:");
        Report(model, cache);

        if (reference is null || file is null) return 0;
        if (!File.Exists(file)) { Console.WriteLine($"\nno such file: {file}"); return 1; }

        Console.WriteLine($"\nmapping '{reference}' -> {file}");
        cache.SetOverride(reference, file);
        Console.WriteLine("after mapping:");
        Report(model, cache);

        // The mapping is made in the viewer, but the export has to honour it too. The viewer and the
        // exporter share one cache; this is what proves the chosen pixels travel that far.
        var opts = new Wc3ModelViewer.Core.Convert.M3ExportOptions
        {
            Lod = model.LodLevels.FirstOrDefault(),
            ModelName = Path.GetFileNameWithoutExtension(modelPath),
        };
        var exported = new Wc3ModelViewer.Core.Convert.M3Exporter(model, opts).Export(cache, "");
        Console.WriteLine("\nexported textures:");
        foreach (var t in exported.Textures)
        {
            var dds = DdsReader.Decode(t.Data);
            Console.WriteLine($"   {t.FileName,-40} {dds.Width}x{dds.Height}");
        }

        Console.WriteLine("\nclearing every mapping");
        cache.ClearOverrides();
        Report(model, cache);
        return 0;
    }

    private static void Report(MdxModel model, Wc3TextureCache cache)
    {
        foreach (var tex in model.Textures)
        {
            if (tex.IsTeamColor || tex.IsTeamGlow || tex.FileName.Length == 0) continue;
            var img = cache.Load("", tex, 0);
            var origin = cache.OriginOf("", tex);
            string state = img is null || origin is null
                ? "MISSING — draws magenta"
                : $"{origin.Value.Source} <- {origin.Value.From}";
            Console.WriteLine($"   {tex.FileName,-45} {state}");
        }
    }
}
