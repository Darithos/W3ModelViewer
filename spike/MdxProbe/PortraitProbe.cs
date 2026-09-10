using System.Numerics;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Dumps a portrait model's geometry, materials and camera the way SC2 will see them: where each
/// geoset sits relative to the camera, and which of them the camera is actually inside.
/// </summary>
public static class PortraitProbe
{
    public static int Run(string file)
    {
        var model = MdxReader.Read(File.ReadAllBytes(file));
        Console.WriteLine($"=== {Path.GetFileName(file)} ===");
        Console.WriteLine($"extent min={model.Min} max={model.Max} radius={model.BoundsRadius}");

        foreach (var cam in model.Cameras)
        {
            Console.WriteLine($"\ncamera '{cam.Name}' pos={cam.Position} target={cam.TargetPosition} " +
                              $"fov={cam.FieldOfView:F4}rad ({cam.FieldOfView * 180 / MathF.PI:F1}deg) " +
                              $"near={cam.NearClip} far={cam.FarClip} " +
                              $"dist={Vector3.Distance(cam.Position, cam.TargetPosition):F1}");
        }

        Console.WriteLine("\ngeosets:");
        foreach (var g in model.Geosets)
        {
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var p in g.Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            var size = max - min;
            Console.WriteLine($"  geoset {g.Index,2} mat={g.MaterialId} lod={g.LodId} '{g.LodName}' " +
                              $"verts={g.Positions.Length,5} tris={g.Indices.Length / 3,5} " +
                              $"size=({size.X:F0},{size.Y:F0},{size.Z:F0}) centre=({(min.X + max.X) / 2:F0},{(min.Y + max.Y) / 2:F0},{(min.Z + max.Z) / 2:F0})");
            foreach (var cam in model.Cameras)
            {
                float d = Vector3.Distance(cam.Position, (min + max) / 2);
                bool inside = cam.Position.X >= min.X && cam.Position.X <= max.X
                           && cam.Position.Y >= min.Y && cam.Position.Y <= max.Y
                           && cam.Position.Z >= min.Z && cam.Position.Z <= max.Z;
                Console.WriteLine($"        vs '{cam.Name}': centre {d:F0} away{(inside ? "  *** CAMERA IS INSIDE THIS GEOSET'S BOX ***" : "")}");
            }
        }

        Console.WriteLine("\nmaterials:");
        for (int i = 0; i < model.Materials.Count; i++)
        {
            var m = model.Materials[i];
            Console.WriteLine($"  material {i}: {m.Layers.Count} layer(s)");
            foreach (var l in m.Layers)
            {
                string tex = (uint)l.TextureId < (uint)model.Textures.Count
                    ? $"repl={model.Textures[l.TextureId].ReplaceableId} '{model.Textures[l.TextureId].FileName}'"
                    : $"texId={l.TextureId}";
                Console.WriteLine($"      filter={l.FilterMode} shading={l.ShadingFlags} alpha={l.Alpha:F2} {tex}");
            }
        }

        Console.WriteLine("\nsequences:");
        foreach (var s in model.Sequences)
            Console.WriteLine($"  '{s.Name}' {s.IntervalStart}..{s.IntervalEnd}");
        return 0;
    }
}

/// <summary>Dumps a model's PRE2 emitters — the fields that decide what a converted SC2 particle looks like.</summary>
public static class EmitterProbe
{
    public static int Run(string file)
    {
        var model = MdxReader.Read(File.ReadAllBytes(file));
        Console.WriteLine($"=== {Path.GetFileName(file)} — {model.ParticleEmitters.Count} PRE2 emitter(s) ===");
        foreach (var e in model.ParticleEmitters)
        {
            string tex = (uint)e.TextureId < (uint)model.Textures.Count
                ? $"'{model.Textures[e.TextureId].FileName}' repl={model.Textures[e.TextureId].ReplaceableId}"
                : $"texId={e.TextureId}";
            Console.WriteLine($"\n  '{e.Name}'  node={e.NodeIndex}  {tex}");
            Console.WriteLine($"    atlas      rows={e.Rows} cols={e.Columns} -> {Math.Max(e.Rows, 1) * Math.Max(e.Columns, 1)} cell(s), " +
                              $"head cells {e.HeadCellStart}..{e.HeadCellEnd} repeat {e.HeadCellRepeat}");
            Console.WriteLine($"    scale      start={e.StartScale} middle={e.MiddleScale} end={e.EndScale}");
            Console.WriteLine($"    colour     start={e.StartColor}/{e.StartAlpha} middle={e.MiddleColor}/{e.MiddleAlpha} end={e.EndColor}/{e.EndAlpha}");
            Console.WriteLine($"    emission   rate={e.EmissionRate} speed={e.Speed} var={e.Variation} lat={e.Latitude} life={e.Life} gravity={e.Gravity}");
            Console.WriteLine($"    shape      width={e.Width} length={e.Length} blend={e.Blend} type={e.ParticleType} tail={e.TailLength} squirt={e.Squirt}");

            // KP2V decides which animations the emitter is alive for. Sampled across each sequence
            // because an emitter that is off for the whole of Stand must not emit while standing.
            if (e.VisibilityTrack is null || e.VisibilityTrack.Count == 0)
            {
                Console.WriteLine("    visibility no KP2V track — always emitting");
                continue;
            }
            var vt = e.VisibilityTrack;
            Console.WriteLine($"    KP2V       {vt.Count} key(s), interp={vt.Interpolation}, globalSeq={vt.GlobalSequenceId}, " +
                              $"times {vt.Times[0]}..{vt.Times[^1]}, first={vt.Values[0]}");
            var on = new List<string>();
            var off = new List<string>();
            foreach (var seq in model.Sequences)
            {
                float peak = 0;
                for (int t = seq.IntervalStart; t <= seq.IntervalEnd; t += 33)
                    peak = Math.Max(peak, Sample(e.VisibilityTrack, t));
                (peak > 0.01f ? on : off).Add($"{seq.Name}={peak:0.##}");
            }
            Console.WriteLine($"    visibility ON in : {string.Join(", ", on)}");
            Console.WriteLine($"               OFF in: {string.Join(", ", off)}");
        }
        return 0;
    }

    /// <summary>Step/linear sample of a float track at a global-timeline millisecond.</summary>
    private static float Sample(MdxTrack<float> track, int timeMs)
    {
        if (track.Count == 0) return 1;
        if (timeMs <= track.Times[0]) return track.Values[0];
        if (timeMs >= track.Times[^1]) return track.Values[^1];
        int i = 0;
        while (i + 1 < track.Count && track.Times[i + 1] <= timeMs) i++;
        if (track.Interpolation == MdxInterpolation.None) return track.Values[i];
        int span = track.Times[i + 1] - track.Times[i];
        float f = span <= 0 ? 0 : (timeMs - track.Times[i]) / (float)span;
        return track.Values[i] + (track.Values[i + 1] - track.Values[i]) * f;
    }
}

/// <summary>
/// Why a geoset is or is not visible in each sequence: its GEOA alpha, and the scale of the bones
/// its vertices ride. Warcraft III hides effect geometry both ways, and only one of them survives
/// an export as an animated track.
/// </summary>
public static class GeosetVisProbe
{
    public static int Run(string file, int[] wanted)
    {
        var model = MdxReader.Read(File.ReadAllBytes(file));
        var animator = new MdxAnimator(model);
        Console.WriteLine($"=== {Path.GetFileName(file)} ===");

        foreach (var g in model.Geosets)
        {
            if (wanted.Length > 0 && !wanted.Contains(g.Index)) continue;
            var anim = model.GeosetAnims.Find(a => a.GeosetId == g.Index);
            Console.WriteLine($"\ngeoset {g.Index} (mat {g.MaterialId}, {g.Positions.Length} verts) " +
                              $"GEOA: {(anim is null ? "none" : $"static alpha {anim.Alpha}, track {(anim.AlphaTrack is null ? "none" : $"{anim.AlphaTrack.Count} keys")}")}");

            // Every bone this geoset's vertices are weighted to.
            var bones = new SortedSet<int>();
            foreach (int mi in g.MatrixIndices) bones.Add(mi);
            foreach (byte bi in g.SkinBoneIndices) bones.Add(bi);

            foreach (var seq in model.Sequences)
            {
                float alpha = animator.GeosetAlpha(g.Index, seq, seq.IntervalStart + seq.DurationMs / 2);
                float peakScale = 0;
                for (int t = seq.IntervalStart; t <= seq.IntervalEnd; t += 33)
                {
                    animator.Evaluate(seq, t, t);
                    foreach (int b in bones)
                    {
                        int node = model.Nodes.FindIndex(n => n.ObjectId == b);
                        if (node < 0) continue;
                        var s = animator.LocalTrs(node).Scale;
                        peakScale = Math.Max(peakScale, Math.Max(s.X, Math.Max(s.Y, s.Z)));
                    }
                }
                string verdict = alpha < 0.01f ? "hidden by GEOA"
                    : peakScale < 0.01f ? "hidden by bone scale"
                    : "VISIBLE";
                Console.WriteLine($"    {seq.Name,-20} alpha={alpha:0.##}  peak bone scale={peakScale:0.###}  -> {verdict}");
            }
        }
        return 0;
    }
}
