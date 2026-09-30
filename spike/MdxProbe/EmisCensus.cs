using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// How HD layers scale their emissive map: the static EmissiveMultiplier and whether a KMTE track
/// animates it, split by whether the emissive slot holds real art or Blizzard's Black32 placeholder.
/// </summary>
public static class EmisCensus
{
    public static int Run(string install, Wc3ArtSet art)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var gains = new SortedDictionary<string, int>();
        int layers = 0, art_ = 0, tracked = 0;
        var examples = new List<string>();
        foreach (var entry in index.Models.Where(m => !m.IsPortrait && m.ArtSet == art))
        {
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            foreach (var mat in model.Materials)
                foreach (var l in mat.Layers)
                {
                    int e = l.Slot(MdxTextureSlot.Emissive);
                    if ((uint)e >= (uint)model.Textures.Count) continue;
                    layers++;
                    string name = model.Textures[e].FileName;
                    if (name.Contains("Black32", StringComparison.OrdinalIgnoreCase) || name.Length == 0) continue;
                    art_++;
                    if (l.EmissiveTrack is not null) tracked++;
                    string key = l.EmissiveTrack is not null ? "track" : $"{l.EmissiveMultiplier:0.##}";
                    gains[key] = gains.GetValueOrDefault(key) + 1;
                    if (examples.Count < 25 && l.EmissiveMultiplier is not 1f)
                        examples.Add($"{entry.RelativePath}: gain={l.EmissiveMultiplier:0.###} track={(l.EmissiveTrack is { } t ? $"{t.Count} keys {t.Values.Min():0.##}..{t.Values.Max():0.##}" : "no")} {Path.GetFileName(name)}");
                }
        }
        Console.WriteLine($"{art}: {layers} layers bind an emissive slot, {art_} with real emissive art, {tracked} animate it (KMTE)");
        foreach (var (k, n) in gains) Console.WriteLine($"  gain {k}: {n}");
        foreach (var x in examples) Console.WriteLine("  " + x);
        return 0;
    }
}
