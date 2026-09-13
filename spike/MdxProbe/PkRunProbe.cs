using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats.Popcorn;

namespace MdxProbe;

/// <summary>
/// Runs PopcornFX bakes through <see cref="PkEffectInstance"/> headlessly. <c>--pkrun</c> prints what
/// one effect's layers produce over time; <c>--pksweep</c> runs every bake in the archive and reports
/// failures and the functions the runtime does not implement.
/// </summary>
public static class PkRunProbe
{
    public static int Run(string install, string target, float seconds)
    {
        byte[]? raw;
        if (File.Exists(target)) raw = File.ReadAllBytes(target);
        else
        {
            using var storage = new Wc3Storage(install);
            raw = storage.TryReadFile(target);
        }
        if (raw is null) { Console.WriteLine($"not found: {target}"); return 1; }

        var def = PkEffectDef.Load(raw);
        Console.WriteLine($"=== {target}: {def.Layers.Count} layers, {def.Slots.Count} slots, {def.Events.Count} event slots");
        foreach (string w in def.Warnings) Console.WriteLine("  ! " + w);
        for (int s = 0; s < def.Slots.Count; s++)
        {
            var slot = def.Slots[s];
            var layer = (uint)slot.Layer < (uint)def.Layers.Count ? def.Layers[slot.Layer] : null;
            string outs = string.Join(" ", slot.OutputEvents.Select(e => (uint)e < (uint)def.Events.Count
                ? $"{def.Events[e].Name}->[{string.Join(",", def.Events[e].TargetSlots)}]" : $"?{e}"));
            Console.WriteLine($"  slot {s}: {layer?.Name ?? "-"}  spawn={layer?.Spawn is not null} evolveOnSpawn={layer?.EvolveOnSpawn is not null} evolve={layer?.Evolve is not null}  {outs}");
            if (layer is null) continue;
            if (layer.InputPayloads.Length > 0) Console.WriteLine($"      inputs: [{string.Join(", ", layer.InputPayloads)}]");
            foreach (var (ev, names) in layer.OutputPayloads) if (names.Length > 0) Console.WriteLine($"      {ev} payloads: [{string.Join(", ", names)}]");
            foreach (var r in layer.Renderers)
                Console.WriteLine($"      renderer {r.Kind} {Path.GetFileName(r.Texture)} {r.Billboard} {r.Blend} atlas {r.AtlasColumns}x{r.AtlasRows}  inputs: {string.Join(", ", r.Inputs.Select(kv => $"{kv.Key}={layer.Fields[kv.Value].Name}"))}");
            foreach (var smp in layer.Samplers)
            {
                string d = smp.Kind switch
                {
                    PkSamplerKind.Shape => $"shape type {smp.Shape!.Type} r {smp.Shape.Radius} inner {smp.Shape.InnerRadius} h {smp.Shape.Height} offset {smp.Shape.Offset}",
                    PkSamplerKind.Curve => $"curve dim {smp.Curve!.Dimension} knots {smp.Curve.Times.Length}",
                    PkSamplerKind.EventStream => $"events at {string.Join(",", smp.EventTimes)}",
                    _ => smp.Kind.ToString(),
                };
                Console.WriteLine($"      sampler {smp.Name}: {d}");
            }
        }

        if (PkCornPlayer.EstimateBounds(def) is var (bmin, bmax)) Console.WriteLine($"  estimated bounds (m): {bmin} .. {bmax}");
        var env = new PkEnvironment();
        var fx = new PkEffectInstance(def, env, 1234);
        float dt = 1f / 60f;
        float nextReport = 0;
        for (float t = 0; t <= seconds + 1e-4f; t += dt)
        {
            fx.Update(dt);
            if (t + 1e-4f < nextReport) continue;
            nextReport += 0.1f;
            Console.WriteLine($"\n t={fx.Age:0.00}s");
            for (int s = 0; s < fx.Slots.Count; s++)
            {
                var st = fx.Slots[s];
                if (st is null || st.Count == 0) continue;
                var layer = st.Def;
                string line = $"   slot {s,2} {layer.Name,-14} n={st.Count,4}";
                foreach (var r in layer.Renderers)
                {
                    line += Stat(st, r.Input("Position"), "pos", v => $"z {Min(st, r.Input("Position"), 2):0.##}..{Max(st, r.Input("Position"), 2):0.##} xy±{MaxAbsXY(st, r.Input("Position")):0.##}");
                    line += Stat(st, r.Input("Size"), "size", _ => $"{Min(st, r.Input("Size"), 0):0.###}..{Max(st, r.Input("Size"), 0):0.###}");
                    line += Stat(st, r.Input("Axis"), "axis", _ => $"|{MinLen(st, r.Input("Axis")):0.##}..{MaxLen(st, r.Input("Axis")):0.##}|");
                    int c = r.Input("Color");
                    if (c >= 0) line += $"  col ({Mean(st, c, 0):0.##},{Mean(st, c, 1):0.##},{Mean(st, c, 2):0.##},{Mean(st, c, 3):0.##})";
                }
                if (layer.Renderers.Count == 0 && st.LifeRatioField >= 0) line += $"  life {Min(st, st.LifeRatioField, 0):0.##}..{Max(st, st.LifeRatioField, 0):0.##}";
                Console.WriteLine(line);
            }
            if (!fx.IsAlive) { Console.WriteLine("   (effect finished)"); break; }
        }
        if (fx.Unsupported.Count > 0) Console.WriteLine("\nunsupported: " + string.Join("; ", fx.Unsupported));
        return 0;

        static string Stat(PkLayerState st, int field, string label, Func<int, string> f) => field < 0 ? "" : $"  {label} {f(field)}";
    }

    /// <summary>Prints every layer's scripts as readable instructions.</summary>
    public static int Disassemble(string install, string target, string? layerFilter)
    {
        byte[]? raw = File.Exists(target) ? File.ReadAllBytes(target) : new Wc3Storage(install).TryReadFile(target);
        if (raw is null) { Console.WriteLine($"not found: {target}"); return 1; }
        var def = PkEffectDef.Load(raw);
        foreach (var layer in def.Layers)
        {
            if (layerFilter is not null && !layer.Name.Contains(layerFilter, StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"\n==== layer {layer.Name}: fields {string.Join(", ", layer.Fields.Select(f => f.Name))}");
            foreach (var (label, s) in new[] { ("spawn", layer.Spawn), ("evolve-on-spawn", layer.EvolveOnSpawn), ("evolve", layer.Evolve) })
            {
                if (s is null) continue;
                Console.WriteLine($"  -- {label}: externals [{string.Join(", ", s.ExternalNames)}]");
                foreach (string line in Lines(s)) Console.WriteLine("     " + line);
            }
        }
        return 0;
    }

    private static IEnumerable<string> Lines(PkScript s)
    {
        string K(byte k) => k switch
        {
            0x00 => "ptr", 0x02 => "b", 0x03 => "b2", 0x04 => "b3", 0x05 => "b4", 0x1A => "i", 0x1B => "i2", 0x1C => "i3", 0x1D => "i4",
            0x20 => "f", 0x21 => "f2", 0x22 => "f3", 0x23 => "f4", 0x25 => "q", _ => $"k{k:x2}",
        };
        string O(PkOperand o) => o.Space switch
        {
            PkSpace.None => "_",
            PkSpace.Const => $"#{C(o)}:{K(o.Kind)}",
            PkSpace.Zero => $"0:{K(o.Kind)}",
            _ => $"{(o.Space == PkSpace.Reg1 ? 'q' : o.Space == PkSpace.Reg2 ? 'u' : 'r')}{o.Index}:{K(o.Kind)}",
        };
        string C(PkOperand o)
        {
            if (o.Index >= s.Consts.Length) return "?";
            var v = s.Consts[o.Index];
            if (PkKind.IsInt(o.Kind) || PkKind.IsBool(o.Kind)) return o.Lanes == 1 ? $"{v.I0}" : $"({v.I0},{v.I1},{v.I2},{v.I3})";
            return o.Lanes == 1 ? $"{v.X:0.#####}" : $"({v.X:0.###},{v.Y:0.###},{v.Z:0.###},{v.W:0.###})";
        }
        string E(int slot) => slot < s.ExternalNames.Length ? s.ExternalNames[slot] : $"?{slot}";
        foreach (var i in s.Code)
        {
            yield return i.Op switch
            {
                PkOp.Load => $"{O(i.Dst)} = {E(i.Slot)}",
                PkOp.Store => $"{E(i.Slot)} <- {O(i.Dst)}",
                PkOp.Binary => $"{O(i.Dst)} = bin{i.Sub:x2}({O(i.A)}, {O(i.B)})",
                PkOp.Binary2 => $"{O(i.Dst)} = op50_{i.Sub:x2}({O(i.A)}, {O(i.B)})",
                PkOp.Unary => $"{O(i.Dst)} = un{i.Sub:x2}({O(i.A)})",
                PkOp.Ternary => $"{O(i.Dst)} = lerp({O(i.A)}, {O(i.B)}, {O(i.C)})",
                PkOp.Select => $"{O(i.Dst)} = {O(i.C)} ? {O(i.B)} : {O(i.A)}",
                PkOp.Swizzle => $"{O(i.Dst)} = swz[{string.Concat(Enumerable.Range(0, i.Dst.Lanes).Select(l => "xyzw01??"[PkScript.LaneCode(i.Swizzle, l)]))}]({O(i.A)})",
                PkOp.Vector => $"{O(i.Dst)} = vec({string.Join(", ", i.Components!.Select(O))})",
                PkOp.CastA or PkOp.CastB => $"{O(i.Dst)} = cast({O(i.A)})",
                PkOp.Call => $"{O(i.Dst)} = {(i.This != 0xFFFF ? E(i.This) + "." : "")}{(i.Slot < s.FunctionNames.Length ? s.FunctionNames[i.Slot] : "?")}({string.Join(", ", Enumerable.Range(0, i.ArgCount).Select(k => O(s.Args[i.ArgStart + k])))})",
                _ => i.Op.ToString(),
            };
        }
    }

    public static int Sweep(string bakeDir, int limit)
    {
        var files = Directory.GetFiles(bakeDir, "*.pkb").OrderBy(f => f).Take(limit).ToList();
        int ok = 0, failed = 0, empty = 0;
        var unsupported = new Dictionary<string, int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long totalParticles = 0;
        foreach (string f in files)
        {
            try
            {
                var def = PkEffectDef.Load(File.ReadAllBytes(f));
                var fx = new PkEffectInstance(def, new PkEnvironment(), 7);
                int peak = 0;
                for (int i = 0; i < 180; i++)
                {
                    fx.Update(1f / 60f);
                    int n = fx.Slots.Where(s => s is not null && s.Def.Renderers.Count > 0).Sum(s => s!.Count);
                    peak = Math.Max(peak, n);
                }
                totalParticles += peak;
                if (peak == 0)
                {
                    empty++;
                    bool hasRenderer = def.Layers.Any(l => l.Renderers.Count > 0);
                    if (Environment.GetEnvironmentVariable("PK_LIST_EMPTY") is not null)
                        Console.WriteLine($"  empty: {Path.GetFileName(f)}  renderers={hasRenderer} slots={def.Slots.Count} warnings={def.Warnings.Count}");
                }
                foreach (string u in fx.Unsupported) unsupported[u] = unsupported.GetValueOrDefault(u) + 1;
                ok++;
            }
            catch (Exception e)
            {
                failed++;
                if (failed <= 20) Console.WriteLine($"  FAIL {Path.GetFileName(f)}: {e.GetType().Name}: {e.Message}");
            }
        }
        Console.WriteLine($"\n{files.Count} bakes in {sw.Elapsed.TotalSeconds:0.0}s: {ok} ran, {failed} failed, {empty} drew nothing in 3 s; mean peak {totalParticles / Math.Max(ok, 1)} particles");
        foreach (var kv in unsupported.OrderByDescending(k => k.Value).Take(30)) Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
        return failed == 0 ? 0 : 1;
    }

    private static float Min(PkLayerState st, int f, int lane) { float m = float.MaxValue; for (int i = 0; i < st.Count; i++) m = MathF.Min(m, st.Fields[f][i][lane]); return m; }
    private static float Max(PkLayerState st, int f, int lane) { float m = float.MinValue; for (int i = 0; i < st.Count; i++) m = MathF.Max(m, st.Fields[f][i][lane]); return m; }
    private static float Mean(PkLayerState st, int f, int lane) { float m = 0; for (int i = 0; i < st.Count; i++) m += st.Fields[f][i][lane]; return st.Count == 0 ? 0 : m / st.Count; }
    private static float MaxAbsXY(PkLayerState st, int f) { float m = 0; for (int i = 0; i < st.Count; i++) m = MathF.Max(m, MathF.Max(MathF.Abs(st.Fields[f][i].X), MathF.Abs(st.Fields[f][i].Y))); return m; }
    private static float MinLen(PkLayerState st, int f) { float m = float.MaxValue; for (int i = 0; i < st.Count; i++) m = MathF.Min(m, st.Fields[f][i].Xyz.Length()); return m; }
    private static float MaxLen(PkLayerState st, int f) { float m = 0; for (int i = 0; i < st.Count; i++) m = MathF.Max(m, st.Fields[f][i].Xyz.Length()); return m; }
}
