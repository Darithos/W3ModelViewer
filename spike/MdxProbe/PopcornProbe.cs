using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Shows what the PopcornFX pipeline makes of an effect: the bake's layers as parsed, and the
/// stand-in emitters synthesised from them. Takes a loose .pkb, a loose .mdx, or an archive name.
/// A number that looks wrong here is wrong in the viewer and in StarCraft II alike, so this is
/// the place to check before either.
/// </summary>
public static class PopcornProbe
{
    /// <summary>
    /// Parses every .pkb bake in the archive and reports how many parse, how many layers each
    /// yields, and which fail — the check that the record walker generalises beyond the handful of
    /// effects it was written against.
    /// </summary>
    public static int Sweep(string install, int limit)
    {
        using var storage = new Wc3Storage(install);
        var names = storage.EnumerateAll()
            .Where(n => n.EndsWith(".pkb", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
        int ok = 0, failed = 0, noLayers = 0, layers = 0, withWarnings = 0;
        var modes = new Dictionary<PopcornBillboardMode, int>();
        var blends = new Dictionary<PopcornBlend, int>();
        int withColour = 0, withScalar = 0, noTexture = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (string n in names)
        {
            var raw = storage.TryReadFile(n);
            if (raw is null || !PopcornBake.LooksLikeBake(raw)) { Console.WriteLine($"  not a bake: {n}"); failed++; continue; }
            try
            {
                var fx = PopcornBake.Parse(raw);
                ok++;
                if (fx.Layers.Count == 0) { noLayers++; Console.WriteLine($"  no layers ({fx.RecordCount} records): {n}"); }
                if (fx.Warnings.Count > 0) { withWarnings++; foreach (string w in fx.Warnings.Take(2)) Console.WriteLine($"  warn {Path.GetFileName(n)}: {w}"); }
                layers += fx.Layers.Count;
                foreach (var l in fx.Layers)
                {
                    modes[l.Billboard] = modes.GetValueOrDefault(l.Billboard) + 1;
                    blends[l.Blend] = blends.GetValueOrDefault(l.Blend) + 1;
                    if (l.ColorCurve is not null) withColour++;
                    if (l.ScalarCurve is not null) withScalar++;
                    if (l.TexturePath.Length == 0) noTexture++;
                }
            }
            catch (Exception e)
            {
                failed++;
                Console.WriteLine($"  FAIL {n}: {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine($"\n{names.Count} bakes in {sw.Elapsed.TotalSeconds:0.0}s: {ok} parsed, {failed} failed, {noLayers} with no renderer layers, {withWarnings} with layer warnings");
        Console.WriteLine($"{layers} layers: {withColour} with a colour curve, {withScalar} with a scalar curve, {noTexture} without a texture");
        Console.WriteLine("billboard modes: " + string.Join(", ", modes.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
        Console.WriteLine("blends: " + string.Join(", ", blends.OrderByDescending(k => k.Value).Select(k => $"{k.Key}={k.Value}")));
        return failed == 0 ? 0 : 1;
    }

    public static int Run(string install, string target)
    {
        if (target.EndsWith(".pkb", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            DumpEffect(PopcornBake.Parse(File.ReadAllBytes(target)), Path.GetFileName(target));
            return 0;
        }

        using var storage = new Wc3Storage(install);
        byte[]? raw = File.Exists(target) ? File.ReadAllBytes(target) : storage.TryReadFile(target);
        if (raw is null) { Console.WriteLine($"not found: {target}"); return 1; }

        if (PopcornBake.LooksLikeBake(raw))
        {
            DumpEffect(PopcornBake.Parse(raw), target);
            return 0;
        }

        var model = MdxReader.Read(raw);
        string cascName = File.Exists(target) ? "" : target;
        var log = PopcornApproximation.Attach(model, storage.TryReadFile, cascName);
        Console.WriteLine($"=== {target}: {model.PopcornEmitters.Count} CORN emitter(s), {model.Sequences.Count} sequence(s) ===");
        foreach (string line in log) Console.WriteLine("  | " + line);

        foreach (var corn in model.PopcornEmitters)
        {
            Console.WriteLine($"\n{corn}");
            Console.WriteLine($"  node {corn.NodeIndex} pivot {model.Nodes[corn.NodeIndex].Pivot}  colour x{corn.ColorMultiplier}  team {corn.TeamColor}");
            Console.WriteLine($"  tracks: " + string.Join(", ", new[]
            {
                corn.AlphaTrack is { } a ? $"KPPA[{a.Count}]" : null,
                corn.ColorTrack is { } c ? $"KPPC[{c.Count}]" : null,
                corn.EmissionRateTrack is { } e ? $"KPPE[{e.Count}]" : null,
                corn.LifespanTrack is { } l ? $"KPPL[{l.Count}]" : null,
                corn.SpeedTrack is { } s ? $"KPPS[{s.Count}]" : null,
                corn.VisibilityTrack is { } v ? $"KPPV[{v.Count}]" : null,
            }.Where(t => t is not null)!) is { Length: > 0 } tr ? tr : "none");
            var gates = PopcornApproximation.ParseFlags(corn.PopcornFlags);
            Console.WriteLine("  gates: " + string.Join(", ", model.Sequences.Select(s => $"{s.Name}={(PopcornApproximation.IsOn(s.Name, gates) ? "on" : "off")}")));
            if (corn.Effect is not null) { Console.WriteLine($"  bake {corn.BakeName}"); DumpEffect(corn.Effect, null); }
        }

        Console.WriteLine($"\n=== {model.ParticleEmitters.Count(e => e.IsPopcorn)} stand-in emitter(s) ===");
        var textures = new Wc3TextureCache(storage);
        foreach (var e in model.ParticleEmitters.Where(e => e.IsPopcorn))
        {
            string tex = (uint)e.TextureId < (uint)model.Textures.Count ? model.Textures[e.TextureId].FileName : $"texId={e.TextureId}";
            var img = (uint)e.TextureId < (uint)model.Textures.Count ? textures.Load(cascName, model.Textures[e.TextureId]) : null;
            Console.WriteLine($"\n  '{e.Name}'  {e.Orientation}  {e.Blend}  '{tex}' {(img is null ? "TEXTURE MISSING" : $"{img.Width}x{img.Height}")}");
            Console.WriteLine($"    life {e.Life:0.###}s  rate {e.EmissionRate:0.##}/s  speed {e.Speed:0.#} (lat {e.Latitude:0})  midTime {e.MiddleTime:0.##}"
                              + (e.BeamLength > 0 ? $"  beam {e.BeamLength:0.#} units long (fixed)" : ""));
            Console.WriteLine($"    size  {e.StartScale:0.#} -> {e.MiddleScale:0.#} -> {e.EndScale:0.#}  (WC3 units)");
            Console.WriteLine($"    colour {F(e.StartColor)}/{e.StartAlpha} -> {F(e.MiddleColor)}/{e.MiddleAlpha} -> {F(e.EndColor)}/{e.EndAlpha}");
            if (e.VisibilityTrack is { } vt)
                Console.WriteLine($"    visibility {vt.Tag}: " + string.Join(" ", vt.Times.Zip(vt.Values, (t, v) => $"{t}={v}")));
            if (e.EmissionRateTrack is { } rt)
                Console.WriteLine($"    rate track {rt.Tag}[{rt.Count}] {rt.Values.Min():0.##}..{rt.Values.Max():0.##}");
        }
        return 0;

        static string F(System.Numerics.Vector3 v) => $"({v.X:0.00},{v.Y:0.00},{v.Z:0.00})";
    }

    private static void DumpEffect(PopcornEffect fx, string? title)
    {
        if (title is not null) Console.WriteLine($"=== {title}: {fx} ===");
        foreach (string w in fx.Warnings) Console.WriteLine("  ! " + w);
        foreach (var l in fx.Layers)
        {
            Console.WriteLine($"  layer {l.Name}: {Path.GetFileName(l.TexturePath)}  {l.Billboard}  {l.Blend}{(l.SoftParticles ? " soft" : "")}{(l.PoolHasInfinity ? " (pool has inf)" : "")}");
            Console.WriteLine($"     fields: {string.Join(", ", l.Fields.Where(f => f.Contains("__")))}");
            if (l.ColorCurve is { } c)
                Console.WriteLine($"     colour curve: " + string.Join("  ", Enumerable.Range(0, c.Count).Select(k => $"t{c.Times[k]:0.00}={V(c.At(k))}")));
            if (l.ScalarCurve is { } s)
                Console.WriteLine($"     scalar curve: " + string.Join("  ", Enumerable.Range(0, s.Count).Select(k => $"t{s.Times[k]:0.00}={s.At(k).X:0.###}")));
            Console.WriteLine($"     pool scalars: {string.Join(" ", l.PoolScalars.Select(v => v.ToString("0.###")))}");
            if (l.PoolAxes.Count > 0) Console.WriteLine($"     pool axes: {string.Join(" ", l.PoolAxes.Select(V))}");
            if (l.PoolColors.Count > 0) Console.WriteLine($"     pool colours: {string.Join(" ", l.PoolColors.Select(V))}");
            Console.WriteLine($"     blob scalars: {string.Join(" ", l.BlobScalars.Select(v => v.ToString("0.###")))}");
        }

        static string V(System.Numerics.Vector4 v) => $"({v.X:0.###},{v.Y:0.###},{v.Z:0.###},{v.W:0.###})";
    }
}
