using System.Diagnostics;
using System.Globalization;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;
using Wc3ModelViewer.Core.Formats.Popcorn;

namespace MdxProbe;

/// <summary>
/// <c>--bake &lt;cascName&gt; &lt;outDir&gt;</c>: bakes every CORN effect of a model into its impostor
/// sheet and writes the sheets (and, with <c>--frames</c>, every cell) as PNG, with the numbers to
/// compare against the Warcraft III editor's recording. The iteration loop for the tone map: no
/// export, no StarCraft II.
/// </summary>
internal static class BakeProbe
{
    public static int Run(string install, string cascName, string outDir, string[] args)
    {
        using var storage = new Wc3Storage(install);
        var raw = storage.TryReadFile(cascName) ?? throw new FileNotFoundException(cascName);
        var mdl = MdxReader.Read(raw);
        foreach (string l in PopcornApproximation.Attach(mdl, storage.TryReadFile, cascName)) Console.WriteLine("  attach | " + l);
        var textures = new Wc3TextureCache(storage) { PreferHd = mdl.IsReforged };
        var o = Options(args, keepFrames: true);
        bool stats = args.Contains("--stats");
        Directory.CreateDirectory(outDir);

        foreach (var corn in mdl.PopcornEmitters)
        {
            if (corn.Runtime is null || corn.Stats.Count == 0) { Console.WriteLine($"'{corn.Name}': nothing to bake (no runtime or no measured layers)"); continue; }
            if (args.Contains("--layerstats"))
                foreach (var s in corn.Stats)
                    Console.WriteLine($"  layer {s.LayerName,-6} {Path.GetFileName(s.Renderer.Texture),-28} {s.Renderer.Blend,-14} {s.Renderer.Billboard,-14}" +
                                      $" births {s.Births,4} {s.FirstBirth,5:0.00}..{s.LastBirth,5:0.00}s life {s.Life,5:0.00}" +
                                      $" {(s.Continuous ? "cont " : "")}{(s.Immortal ? "immortal " : "")}{(s.FollowsEmitter ? "follows " : "")}" +
                                      $"{(s.TrailRate > 0 ? $"trail {s.TrailRate:0.0}/s " : "")}{(s.TeamColoured ? "team " : "")}" +
                                      $"size {s.Size.Start * 50:0.0}/{s.Size.Middle * 50:0.0}/{s.Size.End * 50:0.0}u@{s.Size.MiddleTime:0.00}" +
                                      $" colour ({s.Colour.Start.X:0.0},{s.Colour.Start.Y:0.0},{s.Colour.Start.Z:0.0},{s.Colour.Start.W:0.00})" +
                                      $"->({s.Colour.Middle.X:0.0},{s.Colour.Middle.Y:0.0},{s.Colour.Middle.Z:0.0},{s.Colour.Middle.W:0.00})" +
                                      $"->({s.Colour.End.X:0.0},{s.Colour.End.Y:0.0},{s.Colour.End.Z:0.0},{s.Colour.End.W:0.00})" +
                                      $" speed {s.Speed * 50:0}u/s spread {s.SpreadDegrees:0}° spawn ±({s.SpawnHalfExtents.X * 50:0},{s.SpawnHalfExtents.Y * 50:0},{s.SpawnHalfExtents.Z * 50:0})u" +
                                      $" dir ({s.Direction.X:0.0},{s.Direction.Y:0.0},{s.Direction.Z:0.0}) material {s.Renderer.Material} inputs [{string.Join(",", s.Renderer.Inputs.Keys)}]");
            RgbaImage? Load(string p) => textures.Load(cascName, new MdxTexture { ReplaceableId = 0, FileName = PopcornApproximation.TextureRelPath(p), Flags = 0 });
            string stem = Stem(corn.Name);
            var sw = Stopwatch.StartNew();
            var bake = PkImpostorBaker.Bake(corn.Runtime, corn.Stats, Load, o, corn.ColorMultiplier);
            if (bake is null) Console.WriteLine($"'{corn.Name}': nothing bakeable drew into a sheet");
            else
            {
                Console.WriteLine($"'{corn.Name}' ({Path.GetFileName(corn.EffectPath)}) baked in {sw.Elapsed.TotalSeconds:0.0} s");
                foreach (string l in bake.Log) Console.WriteLine("  | " + l);

                File.WriteAllBytes(Path.Combine(outDir, stem + "_atlas.png"), PngWriter.Write(bake.Atlas));
                if (args.Contains("--frames") && bake.Frames is not null)
                    for (int k = 0; k < bake.Frames.Count; k++)
                        File.WriteAllBytes(Path.Combine(outDir, $"{stem}_f{k:00}.png"), PngWriter.Write(bake.Frames[k]));
                if (stats && bake.Frames is not null)
                {
                    Console.WriteLine("  frame    t(s)  mean   p50   p95   p99   max  sat%  | head: mean  p99   max");
                    for (int k = 0; k < bake.Frames.Count; k++)
                    {
                        var all = Luminance(bake.Frames[k], centreOnly: false);
                        var head = Luminance(bake.Frames[k], centreOnly: true);
                        float t = (k + 0.5f) * bake.Duration / bake.Frames.Count;
                        Console.WriteLine($"  {k,5} {t,7:0.000} {all.Mean,5:0.00} {all.P50,5:0.00} {all.P95,5:0.00} {all.P99,5:0.00} {all.Max,5:0.00} {all.Saturated,5:P0} | {head.Mean,9:0.00} {head.P99,5:0.00} {head.Max,5:0.00}");
                    }
                }
                Console.WriteLine($"-> {Path.Combine(outDir, stem + "_atlas.png")}");
            }

            // The trail: what the effect leaves behind it in flight, as one ribbon strip.
            sw.Restart();
            var trail = PkImpostorBaker.BakeTrail(corn.Runtime, corn.Stats, Load, o, corn.ColorMultiplier);
            if (trail is null) Console.WriteLine($"'{corn.Name}': no trail (no per-distance layer or ribbon drew)");
            else
            {
                Console.WriteLine($"'{corn.Name}' trail baked in {sw.Elapsed.TotalSeconds:0.0} s");
                foreach (string l in trail.Log) Console.WriteLine("  | " + l);
                File.WriteAllBytes(Path.Combine(outDir, stem + "_trail.png"), PngWriter.Write(trail.Texture));
                Console.WriteLine($"-> {Path.Combine(outDir, stem + "_trail.png")}");
            }
        }
        return 0;
    }

    /// <summary>The bake knobs from the command line, shared by <c>--bake</c> and <c>--export1</c>.</summary>
    public static PkImpostorOptions Options(string[] args, bool keepFrames = false)
    {
        float F(string flag, float fallback) => Array.IndexOf(args, flag) is int i && i >= 0 && i + 1 < args.Length
            ? float.Parse(args[i + 1], CultureInfo.InvariantCulture) : fallback;
        int atlas = (int)F("--atlas", 0);                          // 0: 1024, or 2048 for an effect wider than 320 units
        var tone = Array.IndexOf(args, "--tonemap") is int ti && ti >= 0 && ti + 1 < args.Length
            ? args[ti + 1].ToLowerInvariant() switch { "aces" => PkToneMap.Aces, "reinhard" => PkToneMap.Reinhard, _ => PkToneMap.Clip }
            : PkToneMap.Clip;
        return new PkImpostorOptions
        {
            CellSize = atlas <= 0 ? 128 : Math.Max(16, atlas / 8),
            MaxCellSize = atlas <= 0 ? 256 : Math.Max(16, atlas / 8),
            Supersample = (int)F("--supersample", 2),
            Exposure = F("--exposure", 1f),
            ToneMap = tone,
            PitchDegrees = F("--pitch", 56f),
            YawDegrees = F("--yaw", 0f),
            LoopPeriod = F("--loop", 2f),
            MaxDuration = F("--maxdur", 6f),
            Margin = F("--margin", 0.10f),
            AlphaAdd = args.Contains("--alphaadd"),
            TrailSpeedMetres = F("--speed", 900f) / PopcornApproximation.MetresToWc3,   // --speed in Warcraft III units per second
            TrailFrames = (int)F("--trailframes", 1),
            RibbonColourAlpha = args.Contains("--ribbonalpha"),
            OnlyLayers = Array.IndexOf(args, "--onlylayer") is int oi && oi >= 0 && oi + 1 < args.Length
                ? args[oi + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : null,
            ForceFootprint = Array.IndexOf(args, "--footprint") is int pi && pi >= 0 && pi + 3 < args.Length
                ? (float.Parse(args[pi + 1], CultureInfo.InvariantCulture),
                   float.Parse(args[pi + 2], CultureInfo.InvariantCulture),
                   float.Parse(args[pi + 3], CultureInfo.InvariantCulture))
                : null,
            KeepFrames = keepFrames,
        };
    }

    public static string Stem(string name)
    {
        var chars = name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        return new string(chars);
    }

    /// <summary>Display luminance of what the card adds over black (colour × alpha) across a frame's texels.</summary>
    private static (float Mean, float P50, float P95, float P99, float Max, float Saturated) Luminance(RgbaImage frame, bool centreOnly)
    {
        var px = frame.Pixels;
        int w = frame.Width, h = frame.Height;
        int x0 = centreOnly ? w / 4 : 0, x1 = centreOnly ? 3 * w / 4 : w, y0 = centreOnly ? h / 4 : 0, y1 = centreOnly ? 3 * h / 4 : h;
        var values = new List<float>();
        int saturated = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                float a = px[i + 3] / 255f;
                float lum = (0.2126f * px[i + 2] + 0.7152f * px[i + 1] + 0.0722f * px[i]) / 255f * a;
                values.Add(lum);
                if (a >= 0.98f && Math.Max(px[i], Math.Max(px[i + 1], px[i + 2])) >= 250) saturated++;
            }
        if (values.Count == 0) return (0, 0, 0, 0, 0, 0);
        var sorted = values.OrderBy(v => v).ToList();
        float Pct(float q) => sorted[Math.Clamp((int)(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
        return (values.Average(), Pct(0.5f), Pct(0.95f), Pct(0.99f), sorted[^1], saturated / (float)values.Count);
    }
}
