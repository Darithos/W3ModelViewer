using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Per geoset over one sequence: how far its vertices travel, how its GEOA alpha varies, and
/// whether its material animates its texture at all. Answers "is this thing supposed to move, and
/// through which mechanism" without needing to watch the viewport.
/// </summary>
/// <remarks>
/// A card that is motionless on every axis AND has a flat alpha AND no texture animation is static
/// in the file — whatever motion it has in game comes from somewhere the geoset cannot express.
/// </remarks>
public static class MotionProbe
{
    public static int Run(string install, string target, string? sequenceName)
    {
        byte[]? raw;
        if (File.Exists(target)) raw = File.ReadAllBytes(target);
        else { using var s = new Wc3Storage(install); raw = s.TryReadFile(target); }
        if (raw is null) { Console.WriteLine("not found"); return 1; }

        var model = MdxReader.Read(raw);
        var animator = new MdxAnimator(model);
        var seq = (sequenceName is null
                      ? model.Sequences.FirstOrDefault()
                      : model.Sequences.FirstOrDefault(s => s.Name.Contains(sequenceName, StringComparison.OrdinalIgnoreCase)))
                  ?? model.Sequences.FirstOrDefault();
        if (seq is null) { Console.WriteLine("model has no sequences"); return 1; }

        Console.WriteLine($"sequence '{seq.Name}' ({seq.DurationMs} ms), {model.Sequences.Count} in total");
        Console.WriteLine($"skipped chunks: {(model.SkippedChunks.Count == 0 ? "(none)" : string.Join(", ", model.SkippedChunks))}");
        Console.WriteLine($"emitters: {model.ParticleEmitters.Count} particle, {model.RibbonEmitters.Count} ribbon, "
                          + $"{model.Lights.Count} light, {model.PopcornEmitterCount} PopcornFX\n");

        const int Steps = 16;
        foreach (var g in model.Geosets.Where(g => g.LodId == model.LodLevels.FirstOrDefault()))
        {
            var pos = new Vector3[g.VertexCount];
            var first = new Vector3[g.VertexCount];
            double maxMove = 0;
            float aMin = float.MaxValue, aMax = float.MinValue;
            // A geoset WC3 shows only in one sequence is usually collapsed by its bone's scale
            // track the rest of the time, not hidden by GEOA. Tracking the bounding-box diagonal
            // per step is what tells the two apart: a collapsed card has near-zero extent.
            double sizeMin = double.MaxValue, sizeMax = 0;

            for (int step = 0; step <= Steps; step++)
            {
                int t = seq.IntervalStart + seq.DurationMs * step / Steps;
                animator.Evaluate(seq, t, t);
                animator.SkinGeoset(g, pos);

                float a = animator.GeosetAlpha(g.Index, seq, t, t);
                aMin = Math.Min(aMin, a);
                aMax = Math.Max(aMax, a);

                if (pos.Length > 0)
                {
                    var lo = pos[0]; var hi = pos[0];
                    foreach (var q in pos) { lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                    double diag = (hi - lo).Length();
                    sizeMin = Math.Min(sizeMin, diag);
                    sizeMax = Math.Max(sizeMax, diag);
                }

                if (step == 0) pos.CopyTo(first, 0);
                else
                    for (int v = 0; v < pos.Length; v++)
                        maxMove = Math.Max(maxMove, (pos[v] - first[v]).Length());
            }

            var mat = (uint)g.MaterialId < (uint)model.Materials.Count ? model.Materials[g.MaterialId] : null;
            bool flipbook = mat?.Layers.Any(l => l.TextureIdTrack is { Count: > 0 }) ?? false;
            bool texAnim = mat?.Layers.Any(l => l.TextureAnimationId >= 0) ?? false;
            bool layerAlpha = mat?.Layers.Any(l => l.AlphaTrack is { Count: > 0 }) ?? false;

            var how = new List<string>();
            if (maxMove > 0.5) how.Add($"moves {maxMove:0.0}u");
            if (sizeMax > 0.001 && sizeMin / sizeMax < 0.05)
                how.Add($"COLLAPSED for part of it (size {sizeMin:0.##}..{sizeMax:0.#}u)");
            else if (sizeMax > 0.001)
                how.Add($"size {sizeMin:0.#}..{sizeMax:0.#}u");
            // Always report alpha, never only when it varies: a geoset held at a constant alpha 0
            // is INVISIBLE, and staying silent about it was the difference between "this card is
            // enormous" and "this card is enormous and nobody can see it".
            if (aMax < 0.01f) how.Add("HIDDEN (alpha 0 throughout)");
            else if (aMax - aMin > 0.01f) how.Add($"GEOA alpha {aMin:0.##}..{aMax:0.##}");
            else if (aMax < 0.99f) how.Add($"GEOA alpha {aMax:0.##} (constant)");
            if (layerAlpha) how.Add("KMTA layer alpha");
            if (flipbook) how.Add("KMTF flipbook");
            if (texAnim) how.Add("TXAN texture animation");

            Console.WriteLine($"geoset {g.Index,2} mat {g.MaterialId,2} ({g.VertexCount,4}v): "
                              + (how.Count > 0 ? string.Join(", ", how) : "STATIC — nothing animates it"));
        }
        return 0;
    }
}
