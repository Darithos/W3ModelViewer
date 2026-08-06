using System.Globalization;
using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Emits a .m3 plus a JSON reference of what the viewer renders for one sequence, so
/// <c>spike/m3verify/m3sim.py</c> can replay the .m3's own animation data and compare. This is the
/// test that distinguishes "the m3 encodes what we intend" from "the m3 encodes something else":
/// everything else in the suite only proves the file is well-formed.
/// </summary>
public static class SimRef
{
    public static int Run(string install, string dir, string modelPath)
    {
        using var storage = new Wc3Storage(install);
        var textures = new Wc3TextureCache(storage);
        Directory.CreateDirectory(dir);

        var bytes = storage.TryReadFile(modelPath);
        if (bytes is null) { Console.WriteLine($"not in storage: {modelPath}"); return 1; }
        var model = MdxReader.Read(bytes);

        const int lod = 0;
        string name = Path.GetFileNameWithoutExtension(modelPath.Split(':')[^1].Split('\\')[^1]);
        var options = new M3ExportOptions { Lod = lod, ModelName = name };

        var result = new M3Exporter(model, options).Export(textures, modelPath);
        string m3Path = Path.Combine(dir, name + ".m3");
        File.WriteAllBytes(m3Path, result.M3);
        Console.WriteLine($"wrote {m3Path} ({result.M3.Length:N0} B)");

        // Region 0 = the first geoset BuildRegions keeps, under the same filter.
        var regionGeosets = model.Geosets
            .Where(g => g.LodId == lod && g.VertexCount > 0 && g.Indices.Length >= 3
                        && (uint)g.MaterialId < (uint)model.Materials.Count)
            .ToList();
        if (regionGeosets.Count == 0) { Console.WriteLine("no regions"); return 1; }
        var geoset = regionGeosets[0];

        // Prefer a moving sequence; Walk shows errors far more clearly than Stand.
        var seq = model.Sequences.FirstOrDefault(s => s.Name.Contains("Walk", StringComparison.OrdinalIgnoreCase))
                  ?? model.Sequences.FirstOrDefault();
        if (seq is null) { Console.WriteLine("no sequences"); return 1; }
        string m3SeqName = M3Exporter.MapSequenceName(seq.Name);

        // Exactly the sample grid BuildSequences bakes on, so frames line up key-for-key.
        int step = Math.Max(1000 / 30, 10);
        var times = new List<int>();
        for (int t = seq.IntervalStart; t < seq.IntervalEnd; t += step) times.Add(t);
        times.Add(seq.IntervalEnd);

        int[] sampleFrames = [1, times.Count / 3, times.Count / 2, Math.Max(0, times.Count - 2)];
        sampleFrames = sampleFrames.Distinct().Where(k => k >= 0 && k < times.Count).ToArray();

        var animator = new MdxAnimator(model);
        var positions = new System.Numerics.Vector3[geoset.VertexCount];
        int vertexLimit = Math.Min(geoset.VertexCount, 400);

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"model\": {J(modelPath)},\n");
        sb.Append($"  \"m3\": {J(name + ".m3")},\n");
        sb.Append($"  \"sequence_mdx\": {J(seq.Name)},\n");
        sb.Append($"  \"sequence_m3\": {J(m3SeqName)},\n");
        sb.Append($"  \"region\": 0,\n");
        sb.Append($"  \"has_skin\": {(geoset.HasSkin ? "true" : "false")},\n");
        sb.Append($"  \"vertex_count\": {geoset.VertexCount},\n");

        // Rest positions key the comparison: regions renumber (and may split) vertices, so the
        // simulator matches m3 vertices back to viewer vertices by their bind-pose coordinates.
        sb.Append("  \"rest\": [");
        for (int v = 0; v < vertexLimit; v++)
        {
            var p = geoset.Positions[v];
            if (v > 0) sb.Append(',');
            sb.Append('[').Append(F(p.X)).Append(',').Append(F(p.Y)).Append(',').Append(F(p.Z)).Append(']');
        }
        sb.Append("],\n");
        sb.Append("  \"samples\": [\n");
        for (int i = 0; i < sampleFrames.Length; i++)
        {
            int k = sampleFrames[i];
            animator.Evaluate(seq, times[k], times[k]);
            animator.SkinGeoset(geoset, positions);

            sb.Append("    { \"frame_index\": ").Append(k)
              .Append(", \"frame_ms\": ").Append(times[k] - seq.IntervalStart)
              .Append(", \"positions\": [");
            for (int v = 0; v < vertexLimit; v++)
            {
                var p = positions[v];
                if (v > 0) sb.Append(',');
                sb.Append('[').Append(F(p.X)).Append(',').Append(F(p.Y)).Append(',').Append(F(p.Z)).Append(']');
            }
            sb.Append("] }");
            if (i < sampleFrames.Length - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append("  ]\n}\n");

        string jsonPath = Path.Combine(dir, name + ".ref.json");
        File.WriteAllText(jsonPath, sb.ToString());
        Console.WriteLine($"wrote {jsonPath}: sequence '{seq.Name}' -> '{m3SeqName}', " +
                          $"{sampleFrames.Length} frames, {vertexLimit} verts, skin={(geoset.HasSkin ? "HD" : "classic")}");
        return 0;
    }

    private static string F(float f) => f.ToString("0.####", CultureInfo.InvariantCulture);
    private static string J(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
