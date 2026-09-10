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
