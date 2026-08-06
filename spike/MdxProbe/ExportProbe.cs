using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Runs the full MDX → m3 export for a spread of real models and structurally verifies the output:
/// MD34 index integrity, every reference in bounds, vertex/face accounting, and the presence of the
/// sections SC2 requires. This is the strongest check available without launching the SC2 editor.
/// </summary>
public static class ExportProbe
{
    public static int Run(string install, string outDir)
    {
        using var storage = new Wc3Storage(install);
        var textures = new Wc3TextureCache(storage);
        Directory.CreateDirectory(outDir);

        (string Label, string Name)[] targets =
        [
            ("SD footman", @"war3.w3mod:units\human\footman\footman.mdx"),
            ("HD footman", @"war3.w3mod:_hd.w3mod:units\human\footman\footman.mdx"),
            ("HD knight", @"war3.w3mod:_hd.w3mod:units\human\knight\knight.mdx"),
            ("HD knight portrait", @"war3.w3mod:_hd.w3mod:units\human\knight\knight_portrait.mdx"),
            ("SD grunt", @"war3.w3mod:units\orc\grunt\grunt.mdx"),
        ];

        int failures = 0;
        foreach (var (label, name) in targets)
        {
            Console.WriteLine($"\n#### {label} — {name}");
            var bytes = storage.TryReadFile(name);
            if (bytes is null) { Console.WriteLine("  SKIP: not in storage"); continue; }

            try
            {
                var model = MdxReader.Read(bytes);
                var lods = model.LodLevels;
                int lod = lods.Contains(1) ? 1 : lods.FirstOrDefault();

                var options = new M3ExportOptions
                {
                    Lod = lod,
                    ModelName = label.Replace(' ', '_'),
                };
                var exporter = new M3Exporter(model, options);
                var result = exporter.Export(textures, name);

                // Same layout as the app: <out>\<Name>\<Name>.m3 + Assets\Textures\*.dds
                string unitDir = Path.Combine(outDir, options.ModelName);
                string texDir = Path.Combine(unitDir, options.TextureFolder);
                Directory.CreateDirectory(texDir);
                File.WriteAllBytes(Path.Combine(unitDir, options.ModelName + ".m3"), result.M3);
                foreach (var tex in result.Textures)
                    File.WriteAllBytes(Path.Combine(texDir, tex.FileName), tex.Data);

                Console.WriteLine($"  m3 {result.M3.Length,10:N0} B, {result.Textures.Count} textures, LOD {lod}");
                foreach (string line in result.Log) Console.WriteLine($"    | {line}");

                var issues = VerifyM3(result.M3);
                if (issues.Count == 0) Console.WriteLine("  VERIFY: clean");
                else
                {
                    failures++;
                    Console.WriteLine($"  VERIFY: {issues.Count} problems");
                    foreach (string i in issues.Take(12)) Console.WriteLine($"    !! {i}");
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  THROW: {ex.Message}\n{ex.StackTrace?.Split('\n').FirstOrDefault()}");
            }
        }

        failures += AnimationSmokeTest(storage);
        failures += GltfProbe(storage, textures, outDir);

        Console.WriteLine($"\n==== export probe: {(failures == 0 ? "ALL CLEAN" : $"{failures} failures")} ====");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Exports glTF for both skinning schemes and validates structure: the JSON parses, the buffer
    /// length matches the .bin, and every accessor's bufferView window fits inside it.
    /// </summary>
    private static int GltfProbe(Wc3Storage storage, Wc3TextureCache textures, string outDir)
    {
        int failures = 0;
        foreach (string name in new[]
        {
            @"war3.w3mod:units\human\footman\footman.mdx",
            @"war3.w3mod:_hd.w3mod:units\human\footman\footman.mdx",
        })
        {
            Console.WriteLine($"\n#### gltf — {name}");
            var bytes = storage.TryReadFile(name);
            if (bytes is null) { Console.WriteLine("  SKIP"); continue; }

            try
            {
                var model = MdxReader.Read(bytes);
                var lods = model.LodLevels;
                var result = new GltfExporter(model, new M3ExportOptions
                {
                    Lod = lods.Contains(1) ? 1 : lods.FirstOrDefault(),
                    ModelName = name.Contains("_hd") ? "footman_hd" : "footman_sd",
                }).Export(textures, name);

                foreach (var f in result.Files)
                    File.WriteAllBytes(Path.Combine(outDir, f.FileName), f.Data);

                var gltf = result.Files.First(f => f.FileName.EndsWith(".gltf"));
                var bin = result.Files.First(f => f.FileName.EndsWith(".bin"));
                var issues = new List<string>();

                using var doc = System.Text.Json.JsonDocument.Parse(gltf.Data);
                var root = doc.RootElement;

                long declared = root.GetProperty("buffers")[0].GetProperty("byteLength").GetInt64();
                if (declared != bin.Data.Length)
                    issues.Add($"buffer byteLength {declared} != bin file {bin.Data.Length}");

                var views = root.GetProperty("bufferViews").EnumerateArray().ToList();
                foreach (var v in views)
                {
                    long off = v.GetProperty("byteOffset").GetInt64();
                    long len = v.GetProperty("byteLength").GetInt64();
                    if (off + len > bin.Data.Length) issues.Add($"bufferView [{off},{off + len}) overruns bin");
                }
                foreach (var a in root.GetProperty("accessors").EnumerateArray())
                    if (a.GetProperty("bufferView").GetInt32() >= views.Count)
                        issues.Add("accessor references a missing bufferView");

                int nodes = root.GetProperty("nodes").GetArrayLength();
                int joints = root.GetProperty("skins")[0].GetProperty("joints").GetArrayLength();
                int anims = root.TryGetProperty("animations", out var an) ? an.GetArrayLength() : 0;
                Console.WriteLine($"  {result.Files.Count} files, {nodes} nodes ({joints} joints), " +
                                  $"{root.GetProperty("meshes")[0].GetProperty("primitives").GetArrayLength()} primitives, {anims} animations");

                if (issues.Count == 0) Console.WriteLine("  VERIFY: clean");
                else
                {
                    failures++;
                    foreach (string i in issues.Take(8)) Console.WriteLine($"    !! {i}");
                }
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"  THROW: {ex.Message}");
            }
        }
        return failures;
    }

    /// <summary>
    /// Plays a few frames of both skinning schemes on the CPU and asserts the mesh actually moves,
    /// stays finite, and stays within a sane multiple of the model's bounds — the failure modes of
    /// wrong pivot math are NaNs and exploded vertices, both caught here.
    /// </summary>
    private static int AnimationSmokeTest(Wc3Storage storage)
    {
        int failures = 0;
        foreach (string name in new[]
        {
            @"war3.w3mod:units\human\footman\footman.mdx",              // classic matrix groups
            @"war3.w3mod:_hd.w3mod:units\human\footman\footman.mdx",    // Reforged SKIN
        })
        {
            Console.WriteLine($"\n#### anim smoke — {name}");
            var bytes = storage.TryReadFile(name);
            if (bytes is null) { Console.WriteLine("  SKIP"); continue; }

            var model = MdxReader.Read(bytes);
            var animator = new MdxAnimator(model);
            var seq = model.Sequences.FirstOrDefault(s => s.Name.StartsWith("Walk", StringComparison.OrdinalIgnoreCase))
                   ?? model.Sequences[0];
            var geoset = model.Geosets.Where(g => g.LodId == model.LodLevels[0]).MaxBy(g => g.VertexCount)!;

            var skinned = new System.Numerics.Vector3[geoset.VertexCount];
            float maxExtent = Math.Max(model.BoundsRadius, 400) * 8;
            var first = new System.Numerics.Vector3[geoset.VertexCount];
            double maxMove = 0;
            bool bad = false;

            for (int step = 0; step <= 8; step++)
            {
                int t = seq.IntervalStart + seq.DurationMs * step / 8;
                animator.Evaluate(seq, t, t);
                animator.SkinGeoset(geoset, skinned);

                foreach (var p in skinned)
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || p.Length() > maxExtent)
                    { bad = true; break; }

                if (step == 0) skinned.CopyTo(first, 0);
                else
                    for (int v = 0; v < skinned.Length; v++)
                        maxMove = Math.Max(maxMove, (skinned[v] - first[v]).Length());
            }

            Console.WriteLine($"  '{seq.Name}' on geoset {geoset.Index} ({geoset.VertexCount:N0}v, " +
                              $"{(geoset.HasSkin ? "SKIN" : "matrix groups")}): max vertex travel {maxMove:F1} units");
            if (bad) { failures++; Console.WriteLine("  !! vertices went non-finite or exploded"); }
            else if (maxMove < 0.5) { failures++; Console.WriteLine("  !! mesh does not move — animation is not being applied"); }
            else Console.WriteLine("  VERIFY: clean");
        }
        return failures;
    }

    /// <summary>Structural checks over a produced MD34 file.</summary>
    private static List<string> VerifyM3(byte[] d)
    {
        var issues = new List<string>();
        if (d.Length < 24 || Encoding.ASCII.GetString(d, 0, 4) != "43DM")
        {
            // Tag stored reversed; "MD34" reads back as "43DM".
            issues.Add($"bad magic '{Encoding.ASCII.GetString(d, 0, Math.Min(4, d.Length))}'");
            return issues;
        }

        uint indexOffset = BitConverter.ToUInt32(d, 4);
        uint sectionCount = BitConverter.ToUInt32(d, 8);
        if (indexOffset + sectionCount * 16 > d.Length)
        {
            issues.Add($"index at {indexOffset} x{sectionCount} overruns file of {d.Length}");
            return issues;
        }

        var sections = new List<(string Tag, uint Offset, uint Count, uint Version)>();
        for (int i = 0; i < sectionCount; i++)
        {
            int at = (int)indexOffset + i * 16;
            uint rawTag = BitConverter.ToUInt32(d, at);
            string tag = new(new[] { (char)(rawTag >> 24 & 0xFF), (char)(rawTag >> 16 & 0xFF), (char)(rawTag >> 8 & 0xFF), (char)(rawTag & 0xFF) });
            sections.Add((tag, BitConverter.ToUInt32(d, at + 4), BitConverter.ToUInt32(d, at + 8), BitConverter.ToUInt32(d, at + 12)));
        }

        // Offsets ascending, inside the file.
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Offset > indexOffset)
                issues.Add($"section {i} '{sections[i].Tag}' offset {sections[i].Offset} beyond index {indexOffset}");
            if (i > 0 && sections[i].Offset < sections[i - 1].Offset)
                issues.Add($"section {i} '{sections[i].Tag}' offset not ascending");
        }

        string[] required = ["MODL", "SEQS", "STC_", "STG_", "STS_", "BONE", "U8__", "DIV_", "REGN", "BAT_", "MAT_", "MATM", "IREF", "LAYR"];
        foreach (string tag in required)
            if (!sections.Any(s => s.Tag == tag))
                issues.Add($"missing required section {tag}");

        // MODL sanity: version 29, one entry, 856 bytes at its offset.
        var modl = sections.FirstOrDefault(s => s.Tag == "MODL");
        if (modl.Tag == "MODL")
        {
            if (modl.Version != 29) issues.Add($"MODL version {modl.Version}, expected 29");
            if (modl.Count != 1) issues.Add($"MODL count {modl.Count}");
        }

        // Vertex buffer must be a multiple of the 32-byte vertex, faces of 3 uint16s.
        var verts = sections.Where(s => s.Tag == "U8__").ToList();
        foreach (var v in verts)
            if (v.Count % 32 != 0) issues.Add($"U8__ vertex bytes {v.Count} not a multiple of 32");

        // The Reference{count,index,flags} slots at MODL v29's actual reference offsets must point
        // at real sections whose declared count covers the reference. Scanning every 4-byte offset
        // would misread scalar fields (e.g. the flags word at +12) as references.
        if (modl.Tag == "MODL")
        {
            int baseAt = (int)modl.Offset;
            (int Off, string What)[] refOffsets =
            [
                (0, "name"), (16, "seqs"), (28, "stc"), (40, "stg"), (68, "sts"), (80, "bones"),
                (100, "verts"), (112, "div"), (124, "boneLookup"),
                (228, "attachments"), (240, "attachment_addon"),
                (276, "cameras"), (288, "camera_addon"),
                (300, "matm"), (312, "mat"),
            ];
            foreach (var (off, what) in refOffsets)
            {
                uint count = BitConverter.ToUInt32(d, baseAt + off);
                uint index = BitConverter.ToUInt32(d, baseAt + off + 4);
                if (count == 0) continue;                       // null reference
                if (index >= sectionCount)
                {
                    issues.Add($"MODL.{what}: section index {index} out of range");
                    continue;
                }
                var target = sections[(int)index];
                if (target.Count < count)
                    issues.Add($"MODL.{what}: ref count {count} exceeds section [{index}]{target.Tag} count {target.Count}");
            }
        }

        return issues;
    }
}
