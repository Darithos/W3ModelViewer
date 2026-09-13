using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Parses and animates every Reforged model in the storage — not just the curated unit sample
/// <see cref="Validate"/> uses — to find whatever else Patch 3.0 ("Definitive Edition", VERS 1800)
/// broke beyond the HD skin/UV encoding already fixed. Static structural checks catch a wrong parse
/// even when nothing throws; the animation smoke test additionally catches a model that parses fine
/// but goes non-finite or never moves once a sequence actually plays.
/// </summary>
public static class FullSweep
{
    public static int Run(string install, int limit, bool skipAnim)
    {
        using var storage = new Wc3Storage(install);
        Console.WriteLine("Building full asset index...");
        var index = storage.BuildIndex();
        var targets = index.Models
            .Where(m => m.IsHd)
            .Take(limit)
            .ToList();
        Console.WriteLine($"Sweeping {targets.Count:N0} HD-class models (of {index.Models.Count(m => m.IsHd):N0} DE + Reforged)\n");

        int ok = 0, structBad = 0, animBad = 0, threw = 0;
        var structProblems = new List<string>();
        var animProblems = new List<string>();

        int done = 0;
        foreach (var entry in targets)
        {
            done++;
            if (done % 500 == 0) Console.WriteLine($"  ... {done:N0}/{targets.Count:N0}");

            byte[]? bytes;
            try { bytes = storage.TryReadFile(entry.CascName); }
            catch { bytes = null; }
            if (bytes is null) continue;

            MdxModel model;
            try { model = MdxReader.Read(bytes); }
            catch (Exception ex)
            {
                threw++;
                structProblems.Add($"THROW: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"  THROW {entry.RelativePath}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var issues = Validate.Check(model);
            bool structOk = issues.Count == 0;
            if (!structOk)
            {
                structBad++;
                structProblems.AddRange(issues);
            }

            bool animOk = true;
            if (!skipAnim && model.Sequences.Count > 0 && model.Geosets.Count > 0)
            {
                var (animIssue, ok2) = AnimSmoke(model);
                animOk = ok2;
                if (!animOk)
                {
                    animBad++;
                    animProblems.Add(animIssue!);
                    Console.WriteLine($"  ANIM-BAD {entry.RelativePath}: {animIssue}");
                }
            }

            if (structOk && animOk) ok++;
            else if (!structOk)
                Console.WriteLine($"  BAD {entry.RelativePath}: {string.Join("; ", issues.Take(3))}");
        }

        Console.WriteLine($"\n==== {ok:N0} clean, {structBad:N0} structurally bad, {animBad:N0} animation-bad, {threw:N0} threw (of {targets.Count:N0}) ====");

        if (structProblems.Count > 0)
        {
            Console.WriteLine("\ndistinct structural problems:");
            foreach (var g in structProblems.GroupBy(Describe).OrderByDescending(g => g.Count()).Take(25))
                Console.WriteLine($"  {g.Count(),6:N0}x  {g.Key}");
        }
        if (animProblems.Count > 0)
        {
            Console.WriteLine("\ndistinct animation problems:");
            foreach (var g in animProblems.GroupBy(Describe).OrderByDescending(g => g.Count()).Take(25))
                Console.WriteLine($"  {g.Count(),6:N0}x  {g.Key}");
        }
        return structBad + animBad + threw == 0 ? 0 : 1;

        static string Describe(string problem)
        {
            // Strip leading digits/specific ids so "geoset 3: no UV layer" and "geoset 7: no UV
            // layer" collapse into one bucket.
            var parts = problem.Split(':', 2);
            string tail = parts.Length > 1 ? parts[1].Trim() : problem;
            return System.Text.RegularExpressions.Regex.Replace(tail, @"\b\d+\b", "N");
        }
    }

    /// <summary>Plays every sequence a few steps on the model's largest default-LOD geoset.</summary>
    private static (string? Issue, bool Ok) AnimSmoke(MdxModel model)
    {
        var geoset = model.Geosets
            .Where(g => model.LodLevels.Count == 0 || g.LodId == model.LodLevels[0])
            .DefaultIfEmpty(model.Geosets[0])
            .MaxBy(g => g.VertexCount)!;
        if (geoset.VertexCount == 0) return (null, true);

        var animator = new MdxAnimator(model);
        var skinned = new Vector3[geoset.VertexCount];
        bool anyMoved = false;

        foreach (var seq in model.Sequences)
        {
            Vector3[]? first = null;
            double maxMove = 0;
            for (int step = 0; step <= 4; step++)
            {
                int t = seq.IntervalStart + seq.DurationMs * step / 4;
                animator.Evaluate(seq, t, t);
                animator.SkinGeoset(geoset, skinned);

                // Only non-finite output is a real defect. A raw distance cap cannot tell a wrong-
                // pivot explosion apart from a "death"/destruction sequence legitimately flinging
                // debris far from rest (the woodbridge* death animations do exactly that, reaching
                // 50x their rest extent with no NaN anywhere) -- so it is not checked here.
                foreach (var p in skinned)
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
                        return ($"sequence '{seq.Name}' produces non-finite vertex positions", false);

                if (step == 0) { first = (Vector3[])skinned.Clone(); }
                else
                    for (int v = 0; v < skinned.Length; v++)
                        maxMove = Math.Max(maxMove, (skinned[v] - first![v]).Length());
            }
            if (maxMove > 0.5) anyMoved = true;
        }

        // A genuinely rigid prop (one bone, or none) is expected to sit still under every sequence —
        // only flag "never moves" for a model that actually has the articulation to move.
        int boneCount = model.Nodes.Count(n => n.Kind == MdxNodeKind.Bone);
        if (!anyMoved && boneCount > 1)
            return ($"no sequence moves geoset {geoset.Index} at all ({geoset.VertexCount} verts, {boneCount} bones)", false);
        return (null, true);
    }
}
