using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;
using Wc3ModelViewer.Core.Formats.Popcorn;

namespace MdxProbe;

/// <summary>
/// Where the viewer draws each effect against where its node is: plays one sequence for a second,
/// then prints every CORN effect's mean sprite position and every classic emitter's mean spawn point
/// beside the node's pivot carried through its animated world matrix. An effect far from its node
/// is drawn in the wrong place — the DE Paladin's hammer glow landed on the ground beside him.
/// </summary>
public static class EffectPlacementProbe
{
    public static int Run(string install, string cascName, string? sequence)
    {
        using var storage = new Wc3Storage(install);
        var raw = storage.TryReadFile(cascName);
        if (raw is null) { Console.WriteLine("not found: " + cascName); return 1; }
        var model = MdxReader.Read(raw);
        PopcornApproximation.Attach(model, storage.TryReadFile, cascName);
        var seq = sequence is null ? model.Sequences.FirstOrDefault()
                : model.Sequences.FirstOrDefault(s => s.Name.Equals(sequence, StringComparison.OrdinalIgnoreCase));
        if (seq is null) { Console.WriteLine("no such sequence"); return 1; }

        var animator = new MdxAnimator(model);
        var players = model.PopcornEmitters.Where(c => c.Runtime is not null).Select(c => (Corn: c, Player: new PkCornPlayer(model, c))).ToList();
        var sim = new MdxEffectSimulator(model);
        const float step = 1f / 30f;
        int t = seq.IntervalStart;
        for (int f = 0; f < 30; f++)
        {
            t = seq.IntervalStart + (int)(f * step * 1000) % Math.Max(1, seq.DurationMs);
            animator.Evaluate(seq, t, (long)(f * step * 1000));
            foreach (var (_, p) in players) p.Update(step, animator, seq, t, (long)(f * step * 1000), new Vector3(0, -500, 300));
            sim.Update(step, animator, seq, t, (long)(f * step * 1000));
        }

        Console.WriteLine($"{cascName}  '{seq.Name}' at {t} ms");
        Vector3 NodeAt(int node) => (uint)node < (uint)model.Nodes.Count
            ? Vector3.Transform(model.Nodes[node].Pivot, animator.World(node)) : Vector3.Zero;
        foreach (var (corn, p) in players)
        {
            var sprites = p.CollectSprites().SelectMany(b => b.Sprites).ToList();
            string drawn = sprites.Count == 0 ? "no sprites"
                : $"sprites at {Mean(sprites.Select(s => s.Position)):F1} (n={sprites.Count})";
            Console.WriteLine($"  CORN {corn.Name,-24} node at {NodeAt(corn.NodeIndex):F1}  {drawn}");
        }
        for (int i = 0; i < model.ParticleEmitters.Count; i++)
        {
            var e = model.ParticleEmitters[i];
            if (e.IsPopcorn) continue;
            var born = sim.Particles.Where(q => q.Emitter == i).Select(q => q.Origin).ToList();
            if (born.Count == 0) continue;
            Console.WriteLine($"  PRE2 {e.Name,-24} node at {NodeAt(e.NodeIndex):F1}  spawns at {Mean(born):F1} (n={born.Count})");
        }
        return 0;
    }

    private static Vector3 Mean(IEnumerable<Vector3> v)
    {
        var list = v.ToList();
        return list.Count == 0 ? Vector3.Zero : list.Aggregate(Vector3.Zero, (a, b) => a + b) / list.Count;
    }
}
