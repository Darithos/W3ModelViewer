using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// A compact digest of every geoset's animated extent across every sequence of many models, for
/// A/B-ing a change to the track sampler. The dangerous regression is a geoset that had size and
/// now collapses (a body part vanishing), so extents are what this prints — diff two runs.
/// </summary>
public static class SizeScan
{
    public static int Run(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var names = index.AllNames
            .Where(n => n.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
            .Take(limit).ToList();

        foreach (string name in names)
        {
            var raw = storage.TryReadFile(name);
            if (raw is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(raw); } catch { continue; }
            if (model.Sequences.Count == 0 || model.Geosets.Count == 0) continue;

            var animator = new MdxAnimator(model);
            int lod = model.LodLevels.FirstOrDefault();
            foreach (var seq in model.Sequences)
                foreach (var g in model.Geosets.Where(g => g.LodId == lod && g.VertexCount > 0))
                {
                    var pos = new Vector3[g.VertexCount];
                    double lo = double.MaxValue, hi = 0;
                    float aMax = 0;
                    for (int step = 0; step <= 4; step++)
                    {
                        int tm = seq.IntervalStart + seq.DurationMs * step / 4;
                        animator.Evaluate(seq, tm, 0);
                        animator.SkinGeoset(g, pos);
                        aMax = Math.Max(aMax, animator.GeosetAlpha(g.Index, seq, tm, 0));
                        var a = pos[0]; var b = pos[0];
                        foreach (var q in pos) { a = Vector3.Min(a, q); b = Vector3.Max(b, q); }
                        double d = (b - a).Length();
                        lo = Math.Min(lo, d); hi = Math.Max(hi, d);
                    }
                    Console.WriteLine($"{name}|{seq.Name}|{g.Index}|{lo:0.0}|{hi:0.0}|{aMax:0.###}");
                }
        }
        return 0;
    }
}
