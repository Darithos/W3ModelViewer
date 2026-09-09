using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Every sequence's interval alongside the raw scale keys of the bones a geoset is skinned to.
/// MDX keyframe times are global across the whole model, so "which sequence owns this key" is a
/// question about intervals, not about the track — and a geoset that WC3 shows in only one
/// animation is usually one whose bone is scaled to zero everywhere else.
/// </summary>
public static class TrackProbe
{
    public static int Run(string install, string target, int geosetIndex)
    {
        byte[]? raw;
        if (File.Exists(target)) raw = File.ReadAllBytes(target);
        else { using var s = new Wc3Storage(install); raw = s.TryReadFile(target); }
        if (raw is null) { Console.WriteLine("not found"); return 1; }

        var model = MdxReader.Read(raw);
        var geoset = model.Geosets.FirstOrDefault(g => g.Index == geosetIndex);
        if (geoset is null) { Console.WriteLine($"no geoset {geosetIndex}"); return 1; }

        Console.WriteLine("sequences:");
        foreach (var s in model.Sequences)
            Console.WriteLine($"  [{s.IntervalStart,6} .. {s.IntervalEnd,6}]  {s.Name}");

        // Classic geosets address bones through the matrix-group table; Reforged ones through SKIN.
        var bones = new SortedSet<int>();
        if (geoset.HasSkin)
            foreach (byte b in geoset.SkinBoneIndices) bones.Add(b);
        else
            foreach (int m in geoset.MatrixIndices) bones.Add(m);

        Console.WriteLine($"\ngeoset {geosetIndex} is skinned to {bones.Count} bone(s):");
        foreach (int id in bones)
        {
            var node = model.Nodes.FirstOrDefault(n => n.ObjectId == id);
            if (node is null) { Console.WriteLine($"  node id {id}: NOT FOUND"); continue; }

            var sc = node.Scale;
            if (sc is null || sc.Count == 0)
            {
                Console.WriteLine($"  {node.Kind} '{node.Name}' (id {id}): no scale track");
                continue;
            }

            Console.WriteLine($"  {node.Kind} '{node.Name}' (id {id}): {sc.Count} scale keys, "
                              + $"interp {sc.Interpolation}, globalSeq {sc.GlobalSequenceId}");
            for (int k = 0; k < sc.Count; k++)
            {
                var v = sc.Values[k];
                var owner = model.Sequences.FirstOrDefault(s => sc.Times[k] >= s.IntervalStart && sc.Times[k] <= s.IntervalEnd);
                Console.WriteLine($"      t={sc.Times[k],6}  scale=({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})"
                                  + $"   {(owner is null ? "*** outside every sequence interval ***" : owner.Name)}");
            }
        }
        return 0;
    }
}
