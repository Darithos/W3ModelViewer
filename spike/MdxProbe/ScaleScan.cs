using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Counts bones whose scale track lives entirely inside ONE sequence and starts and ends at zero —
/// the "show this effect in one animation only" idiom. Every such bone is a claim about what the
/// engine does with a sequence that has no keys: if it reset to unit scale, all of these would be
/// visible at full size in every other animation, so Blizzard's own art settles the question.
/// </summary>
public static class ScaleScan
{
    public static int Run(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var models = index.AllNames
            .Where(n => n.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToList();

        int scanned = 0, hits = 0, modelsWithHits = 0, confined = 0;
        foreach (string name in models)
        {
            var raw = storage.TryReadFile(name);
            if (raw is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(raw); } catch { continue; }
            if (model.Sequences.Count == 0) continue;
            scanned++;

            int here = 0;
            foreach (var node in model.Nodes)
            {
                var sc = node.Scale;
                if (sc is null || sc.Count < 2 || sc.GlobalSequenceId >= 0) continue;

                // All keys inside one sequence?
                var owner = model.Sequences.FirstOrDefault(s =>
                    sc.Times[0] >= s.IntervalStart && sc.Times[^1] <= s.IntervalEnd);
                if (owner is null) continue;
                if (model.Sequences.Count < 2) continue;

                confined++;
                bool zeroEnds = sc.Values[0].Length() < 0.01f && sc.Values[^1].Length() < 0.01f;
                if (!zeroEnds) continue;

                if (here == 0) Console.WriteLine($"{name}");
                Console.WriteLine($"    bone '{node.Name}': {sc.Count} keys, all inside '{owner.Name}', "
                                  + $"ends at zero — hidden in the other {model.Sequences.Count - 1} sequence(s)");
                here++; hits++;
            }
            if (here > 0) modelsWithHits++;
        }

        Console.WriteLine($"\n==== {hits} such bone(s) across {modelsWithHits} of {scanned} models scanned ====");
        return 0;
    }
}
