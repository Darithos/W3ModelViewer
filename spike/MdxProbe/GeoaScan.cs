using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// What a GEOA alpha track falls back to in a sequence that carries none of its keys. The reader
/// keeps the chunk's static alpha beside the track; this censuses that static value over stock and
/// loose models, and counts the (geoset, sequence) rows where the fallback actually decides
/// visibility — the rows a change to the fallback would move.
/// </summary>
public static class GeoaScan
{
    public static int Run(string install, int limit, IEnumerable<string> looseDirs)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var stock = index.AllNames
            .Where(n => n.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(n => (Name: n, Raw: storage.TryReadFile(n)));
        Tally("stock", stock);

        var loose = looseDirs.Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.mdx", SearchOption.AllDirectories))
            .Select(f => (Name: f, Raw: (byte[]?)File.ReadAllBytes(f)));
        Tally("loose", loose);
        return 0;
    }

    private static void Tally(string label, IEnumerable<(string Name, byte[]? Raw)> models)
    {
        int parsed = 0, tracked = 0, staticOne = 0, staticZero = 0, staticOther = 0;
        int rowsNoKeys = 0, rowsNoKeysStaticNotOne = 0;
        var examples = new List<string>();
        foreach (var (name, raw) in models)
        {
            if (raw is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(raw); } catch { continue; }
            parsed++;
            foreach (var ga in model.GeosetAnims)
            {
                if (ga.AlphaTrack is not { Count: > 0 } track || track.GlobalSequenceId >= 0) continue;
                tracked++;
                if (ga.Alpha == 1f) staticOne++; else if (ga.Alpha == 0f) staticZero++; else staticOther++;
                foreach (var seq in model.Sequences)
                {
                    bool keyed = track.Times.Any(t => t >= seq.IntervalStart && t <= seq.IntervalEnd);
                    if (keyed) continue;
                    rowsNoKeys++;
                    if (ga.Alpha != 1f)
                    {
                        rowsNoKeysStaticNotOne++;
                        if (examples.Count < 25)
                            examples.Add($"{Path.GetFileName(name)} geoset {ga.GeosetId} seq '{seq.Name}' static={ga.Alpha:0.###} keys=[{string.Join(" ", track.Times.Zip(track.Values, (t, v) => $"{t}:{v:0.##}"))}]");
                    }
                }
            }
        }
        Console.WriteLine($"--- {label}: {parsed} models, {tracked} GEOA alpha tracks (non-global)");
        Console.WriteLine($"    static alpha beside a track: 1.0 x{staticOne}, 0.0 x{staticZero}, other x{staticOther}");
        Console.WriteLine($"    (geoset, sequence) rows with no key in the sequence: {rowsNoKeys}, of which static != 1: {rowsNoKeysStaticNotOne}");
        foreach (var e in examples) Console.WriteLine("      " + e);
    }
}
