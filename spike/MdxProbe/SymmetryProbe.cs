using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Answers which horizontal axis a model is mirrored across, which is the same as asking which way
/// it faces. A biped or quadruped is symmetric left-to-right and asymmetric front-to-back, so the
/// plane its vertex cloud mirrors across is square to its facing direction.
/// </summary>
public static class SymmetryProbe
{
    public static int Run(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var models = index.Models.Where(m => !m.IsPortrait
                                          && m.RelativePath.StartsWith("units", StringComparison.OrdinalIgnoreCase))
                                 .ToList();
        int step = Math.Max(1, models.Count / limit);

        int mirroredAboutX = 0, mirroredAboutY = 0, unclear = 0, read = 0;
        for (int i = 0; i < models.Count && read < limit; i += step)
        {
            var raw = storage.TryReadFile(models[i].CascName);
            if (raw is null) continue;
            MdxModel m;
            try { m = MdxReader.Read(raw); } catch (Exception) { continue; }

            var pts = m.Geosets.Where(g => g.LodId == 0).SelectMany(g => g.Positions).ToList();
            if (pts.Count < 50) continue;
            read++;

            var (dx, dy) = Score(pts);
            if (Math.Abs(dx - dy) < 0.03f) unclear++;
            else if (dx < dy) mirroredAboutX++;
            else mirroredAboutY++;
        }

        Console.WriteLine($"{read} Warcraft III unit models sampled");
        Console.WriteLine($"  mirror plane X=0 (model faces along Y): {mirroredAboutX}");
        Console.WriteLine($"  mirror plane Y=0 (model faces along X): {mirroredAboutY}");
        Console.WriteLine($"  unclear:                                {unclear}");
        return 0;
    }

    /// <summary>Grid-histogram mismatch between the cloud and its reflection, about X then about Y.</summary>
    private static (float X, float Y) Score(List<Vector3> pts)
    {
        const int g = 24;
        float cx = pts.Average(p => p.X), cy = pts.Average(p => p.Y);
        var xs = pts.Select(p => p.X - cx).ToArray();
        var ys = pts.Select(p => p.Y - cy).ToArray();
        float sx = MathF.Sqrt(xs.Sum(v => v * v) / xs.Length), sy = MathF.Sqrt(ys.Sum(v => v * v) / ys.Length);
        if (sx <= 0 || sy <= 0) return (0, 0);

        int[,] Grid(bool flipX, bool flipY)
        {
            var h = new int[g, g];
            for (int i = 0; i < xs.Length; i++)
            {
                float x = (flipX ? -xs[i] : xs[i]) / sx, y = (flipY ? -ys[i] : ys[i]) / sy;
                int a = Math.Clamp((int)((x + 3) / 6 * g), 0, g - 1);
                int b = Math.Clamp((int)((y + 3) / 6 * g), 0, g - 1);
                h[a, b]++;
            }
            return h;
        }

        var self = Grid(false, false);
        float Diff(int[,] other)
        {
            int d = 0;
            for (int a = 0; a < g; a++) for (int b = 0; b < g; b++) d += Math.Abs(self[a, b] - other[a, b]);
            return d / (2f * xs.Length);
        }
        return (Diff(Grid(true, false)), Diff(Grid(false, true)));
    }
}
