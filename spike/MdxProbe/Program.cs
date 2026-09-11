using System.Text;
using Wc3ModelViewer.Core.Casc;

// Both the SD and HD knight report VERS 1200, so the file version alone cannot say which geoset /
// material encoding a model uses. This probe dumps the raw MTLS and GEOS bytes of a matched SD/HD
// pair so the parser's structural detection can be written against fact rather than assumption.
//
//   MdxProbe [installPath]

string install = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : @"C:\games\Warcraft III";
Console.OutputEncoding = Encoding.UTF8;
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Wc3ModelViewer.BlpJpegCodec.Install();      // as the app does; without it every BLP1-JPEG reads as missing

// --validate [n] parses many real models and asserts the results are self-consistent.
if (args.Contains("--validate"))
{
    int limit = args.Select((a, i) => (a, i)).Where(t => t.a == "--validate")
                    .Select(t => t.i + 1 < args.Length && int.TryParse(args[t.i + 1], out int n) ? n : 60)
                    .First();
    return MdxProbe.Validate.Run(install, limit);
}

// --portrait <file> dumps a portrait model as SC2 will see it.
if (args.Contains("--portrait"))
{
    int pj = Array.IndexOf(args, "--portrait");
    if (pj + 1 >= args.Length) { Console.WriteLine("usage: --portrait <file>"); return 1; }
    return MdxProbe.PortraitProbe.Run(args[pj + 1]);
}

// --extract <nameFilter> <outDir> writes matching archive models out as loose .mdx files.
if (args.Contains("--extract"))
{
    int xi = Array.IndexOf(args, "--extract");
    if (xi + 2 >= args.Length) { Console.WriteLine("usage: --extract <nameFilter> <outDir>"); return 1; }
    using var xs = new Wc3Storage(install);
    var xindex = xs.BuildIndex();
    Directory.CreateDirectory(args[xi + 2]);
    foreach (var e in xindex.Models.Where(m => m.RelativePath.Contains(args[xi + 1], StringComparison.OrdinalIgnoreCase)))
    {
        var raw = xs.TryReadFile(e.CascName);
        if (raw is null) continue;
        string dest = Path.Combine(args[xi + 2], $"{e.ArtSet}_{Path.GetFileName(e.RelativePath)}");
        File.WriteAllBytes(dest, raw);
        Console.WriteLine($"  {e.RelativePath} ({e.ArtSet}) -> {dest}");
    }
    return 0;
}

// --symmetry [n] reports which horizontal axis Warcraft III unit models mirror across.
if (args.Contains("--symmetry"))
{
    int si = Array.IndexOf(args, "--symmetry");
    int n = si + 1 < args.Length && int.TryParse(args[si + 1], out int sn) ? sn : 200;
    return MdxProbe.SymmetryProbe.Run(install, n);
}

// --teamcolor [n] censuses where the team-colour signal lives across n models, SD and HD.
if (args.Contains("--teamcolor"))
{
    int ti = Array.IndexOf(args, "--teamcolor");
    var rest = args.Skip(ti + 1).Where(a => !a.StartsWith("--")).ToArray();
    if (args.Contains("--dumpslots")) return MdxProbe.TeamProbe.DumpSlots(install, rest[0], rest[1]);
    if (args.Contains("--orm")) return MdxProbe.TeamProbe.Orm(install, rest.Length > 0 ? int.Parse(rest[0]) : 400);
    if (args.Contains("--composites"))
        return MdxProbe.TeamProbe.Composites(install, rest[0], rest[1],
                                             rest.Length > 2 && int.TryParse(rest[2], out int cs) ? cs : 0);
    if (args.Contains("--sweep")) return MdxProbe.TeamProbe.Sweep(install, rest.Length > 0 ? int.Parse(rest[0]) : 25);
    if (args.Contains("--glow")) return MdxProbe.TeamProbe.Glow(install, rest.Length > 0 ? int.Parse(rest[0]) : 400);
    if (args.Contains("--rawtex")) return MdxProbe.TeamProbe.RawTex(install, rest[0], rest[1]);
    if (rest.Length > 0 && !int.TryParse(rest[0], out _)) return MdxProbe.TeamProbe.Slots(install, rest);
    int n = rest.Length > 0 ? int.Parse(rest[0]) : 200;
    return MdxProbe.TeamProbe.Run(install, n);
}

// --cloud <file.mdx> <out.txt> dumps LOD 0 bind-pose vertex positions for offline comparison.
if (args.Contains("--cloud"))
{
    int ci = Array.IndexOf(args, "--cloud");
    if (ci + 2 >= args.Length) { Console.WriteLine("usage: --cloud <file.mdx> <out.txt>"); return 1; }
    var cm = Wc3ModelViewer.Core.Formats.MdxReader.Read(File.ReadAllBytes(args[ci + 1]));
    using var cw = new StreamWriter(args[ci + 2]);
    foreach (var gg in cm.Geosets.Where(g => g.LodId == 0))
        foreach (var pp in gg.Positions)
            cw.WriteLine($"{pp.X} {pp.Y} {pp.Z}");
    Console.WriteLine($"wrote {args[ci + 2]}");
    return 0;
}

// --emitters <file.mdx> dumps the PRE2 emitters that drive the SC2 particle conversion.
if (args.Contains("--emitters"))
{
    int ei = Array.IndexOf(args, "--emitters");
    if (ei + 1 >= args.Length) { Console.WriteLine("usage: --emitters <file.mdx>"); return 1; }
    return MdxProbe.EmitterProbe.Run(args[ei + 1]);
}

// --geosetvis <file.mdx> [indices...] says what hides each geoset in each sequence.
if (args.Contains("--geosetvis"))
{
    int gi = Array.IndexOf(args, "--geosetvis");
    if (gi + 1 >= args.Length) { Console.WriteLine("usage: --geosetvis <file.mdx> [geoset indices]"); return 1; }
    var want = args.Skip(gi + 2).TakeWhile(a => int.TryParse(a, out _)).Select(int.Parse).ToArray();
    return MdxProbe.GeosetVisProbe.Run(args[gi + 1], want);
}

// --tex resolves and identifies every texture a set of models references.
if (args.Contains("--tex")) return MdxProbe.TexProbe.Run(install);

// --pack <nameFilter> <outDir> exports models the way the app does and audits the package layout.
if (args.Contains("--pack"))
{
    int ki = Array.IndexOf(args, "--pack");
    if (ki + 2 >= args.Length) { Console.WriteLine("usage: --pack <nameFilter> <outDir>"); return 1; }
    return MdxProbe.TextureDiag.Pack(install, args[ki + 1], args[ki + 2]);
}

// --audit <m3file> lists the texture paths baked into an exported model.
if (args.Contains("--audit"))
{
    int ai = Array.IndexOf(args, "--audit");
    if (ai + 1 >= args.Length) { Console.WriteLine("usage: --audit <m3file>"); return 1; }
    foreach (string f in Directory.Exists(args[ai + 1])
                 ? Directory.GetFiles(args[ai + 1], "*.m3", SearchOption.AllDirectories)
                 : [args[ai + 1]])
    {
        Console.WriteLine($"=== {f} ===");
        foreach (var r in Wc3ModelViewer.Core.Convert.M3TextureAudit.Verify(f))
            Console.WriteLine($"  {r}");
    }
    return 0;
}

// --dds2png <file-or-dir> <outDir> decodes exported textures back to png for inspection.
if (args.Contains("--dds2png"))
{
    int pi = Array.IndexOf(args, "--dds2png");
    if (pi + 2 >= args.Length) { Console.WriteLine("usage: --dds2png <file-or-dir> <outDir>"); return 1; }
    return MdxProbe.TextureDiag.Dump(args[pi + 1], args[pi + 2]);
}

// --diag <nameFilter> [outDir] dumps every texture decision for models matching a name.
if (args.Contains("--diag"))
{
    int di = Array.IndexOf(args, "--diag");
    if (di + 1 >= args.Length) { Console.WriteLine("usage: --diag <nameFilter> [outDir]"); return 1; }
    string dOut = di + 2 < args.Length && !args[di + 2].StartsWith("--")
        ? args[di + 2]
        : Path.Combine(Path.GetTempPath(), "wc3-diag");
    return MdxProbe.TextureDiag.Run(install, args[di + 1], dOut);
}

// --loose <file-or-dir> [outDir] [--mangle] resolves and exports LOOSE models — the custom-model
// case, where the .mdx has no archive prefix and its textures are stock game art the author never
// shipped. --mangle re-spells every reference the way real custom models do and checks each still
// resolves to the same file.
if (args.Contains("--loose"))
{
    int li = Array.IndexOf(args, "--loose");
    if (li + 1 >= args.Length) { Console.WriteLine("usage: --loose <file-or-dir> [outDir] [--mangle]"); return 1; }
    string outDir = li + 2 < args.Length && !args[li + 2].StartsWith("--") ? args[li + 2] : null!;
    return MdxProbe.LooseProbe.Run(install, args[li + 1], outDir, args.Contains("--mangle"));
}

// --export runs the full m3 export for a spread of models and verifies the output structurally.
if (args.Contains("--export"))
{
    int i = Array.IndexOf(args, "--export");
    string outDir = i + 1 < args.Length && !args[i + 1].StartsWith("--")
        ? args[i + 1]
        : Path.Combine(Path.GetTempPath(), "wc3-export-probe");
    return MdxProbe.ExportProbe.Run(install, outDir);
}

// --battery <outDir> exports the SD footman four ways (rest-only / locations / rotations / full)
// through the full m3studio pipeline — one SC2 preview session then isolates which synthesized
// data SC2 interprets differently.
if (args.Contains("--battery"))
{
    int bi = Array.IndexOf(args, "--battery");
    string outRoot = bi + 1 < args.Length ? args[bi + 1] : Path.Combine(Path.GetTempPath(), "wc3-battery");
    using var s = new Wc3Storage(install);
    var textures = new Wc3ModelViewer.Core.Casc.Wc3TextureCache(s);
    var bytes = s.TryReadFile(@"war3.w3mod:units\human\footman\footman.mdx")!;
    var model = Wc3ModelViewer.Core.Formats.MdxReader.Read(bytes);

    (string Folder, Wc3ModelViewer.Core.Convert.M3AnimTestData Data)[] variants =
    [
        ("A_rest_only", Wc3ModelViewer.Core.Convert.M3AnimTestData.None),
        ("B_locations_only", Wc3ModelViewer.Core.Convert.M3AnimTestData.LocationsOnly),
        ("C_rotations_only", Wc3ModelViewer.Core.Convert.M3AnimTestData.RotationsOnly),
        ("D_full", Wc3ModelViewer.Core.Convert.M3AnimTestData.Full),
    ];
    foreach (var (folder, data) in variants)
    {
        var options = new Wc3ModelViewer.Core.Convert.M3ExportOptions
        {
            Lod = 0,
            ModelName = folder,
            AnimData = data,
        };
        var result = new Wc3ModelViewer.Core.Convert.M3Exporter(model, options).Export(textures, @"war3.w3mod:units\human\footman\footman.mdx");
        string dir = Path.Combine(outRoot, folder);
        string texDir = Path.Combine(dir, options.TextureFolder);
        Directory.CreateDirectory(texDir);
        foreach (var tex in result.Textures)
            File.WriteAllBytes(Path.Combine(texDir, tex.FileName), tex.Data);
        string bridge = Path.Combine(dir, folder + ".viewer.m3");
        string final = Path.Combine(dir, folder + ".m3");
        File.WriteAllBytes(bridge, result.M3);
        var (ok, log) = Wc3ModelViewer.Core.Convert.BlenderM3Studio.Convert(bridge, final);
        if (ok) File.Delete(bridge); else File.Move(bridge, final, overwrite: true);
        Console.WriteLine($"{folder}: {(ok ? "pipeline OK" : "pipeline FAILED (direct file kept) — " + log)}");
    }
    Console.WriteLine($"battery at {outRoot}");
    return 0;
}

// --blend <file|dir|cascPath> lists geosets whose material declares a blend or additive layer but
// which the compositor collapses to opaque — the "solid red card" symptom.
if (args.Contains("--blend"))
{
    int bi = Array.IndexOf(args, "--blend");
    if (bi + 1 >= args.Length) { Console.WriteLine("usage: --blend <file|dir|cascPath>"); return 1; }
    return MdxProbe.BlendProbe.Run(install, args[bi + 1]);
}

// --blp <file|dir> <outDir> writes each BLP's colour and alpha channel as PNGs, so an alpha
// channel can be inspected instead of guessed at from a histogram.
if (args.Contains("--blp"))
{
    int pi = Array.IndexOf(args, "--blp");
    if (pi + 2 >= args.Length) { Console.WriteLine("usage: --blp <file|dir> <outDir>"); return 1; }
    return MdxProbe.BlpAlphaProbe.Run(args[pi + 1], args[pi + 2]);
}

// --motion <file|cascPath> [sequence] reports, per geoset, what actually animates it over a
// sequence: vertex travel, GEOA alpha, layer alpha, flipbook or texture animation.
if (args.Contains("--motion"))
{
    int mi = Array.IndexOf(args, "--motion");
    if (mi + 1 >= args.Length) { Console.WriteLine("usage: --motion <file|cascPath> [sequence]"); return 1; }
    string? seqName = mi + 2 < args.Length && !args[mi + 2].StartsWith("--") ? args[mi + 2] : null;
    return MdxProbe.MotionProbe.Run(install, args[mi + 1], seqName);
}

// --tracks <file|cascPath> <geoset> prints sequence intervals and the scale keys of the bones a
// geoset is skinned to — how WC3 hides a geoset outside the one animation it belongs to.
if (args.Contains("--tracks"))
{
    int ti = Array.IndexOf(args, "--tracks");
    if (ti + 2 >= args.Length) { Console.WriteLine("usage: --tracks <file|cascPath> <geosetIndex>"); return 1; }
    return MdxProbe.TrackProbe.Run(install, args[ti + 1], int.Parse(args[ti + 2]));
}

// --scalescan [n] counts Blizzard's own bones that are hidden by a scale track confined to one
// sequence — the evidence for what the engine does with a sequence that has no keys.
if (args.Contains("--scalescan"))
{
    int si = Array.IndexOf(args, "--scalescan");
    int n = si + 1 < args.Length && int.TryParse(args[si + 1], out int v) ? v : 400;
    return MdxProbe.ScaleScan.Run(install, n);
}

// --wpfblend renders the viewer's unshaded material offscreen: does its matte follow opacity and alpha?
if (args.Contains("--wpfblend")) return MdxProbe.WpfBlendProbe.Run();

// --geoascan [n] [looseDir...] censuses the static alpha a GEOA keeps beside its track, and the
// sequences where that fallback decides whether a geoset is visible.
if (args.Contains("--geoascan"))
{
    int gi = Array.IndexOf(args, "--geoascan");
    int n = gi + 1 < args.Length && int.TryParse(args[gi + 1], out int v) ? v : 100_000;
    return MdxProbe.GeoaScan.Run(install, n, args.Skip(gi + 1).Where(a => !int.TryParse(a, out _)));
}

// --sizescan [n] digests every geoset's animated extent across every sequence, for A/B diffing a
// change to the track sampler.
if (args.Contains("--sizescan"))
{
    int zi = Array.IndexOf(args, "--sizescan");
    int n = zi + 1 < args.Length && int.TryParse(args[zi + 1], out int v) ? v : 200;
    return MdxProbe.SizeScan.Run(install, n);
}

// --map <model.mdx> [reference file] resolves every texture reference, optionally remaps one to a
// chosen file, and resolves again - the viewer's Textures panel, headless.
if (args.Contains("--map"))
{
    int mi = Array.IndexOf(args, "--map");
    if (mi + 1 >= args.Length) { Console.WriteLine("usage: --map <model.mdx> [reference file]"); return 1; }
    string? refName = mi + 3 < args.Length ? args[mi + 2] : null;
    string? refFile = mi + 3 < args.Length ? args[mi + 3] : null;
    return MdxProbe.MapProbe.Run(install, args[mi + 1], refName, refFile);
}

// --uv <model.mdx> <geoset|-1> <outDir> draws a geoset's UV triangles over its composited
// texture, so a mis-mapped face can be seen rather than deduced.
if (args.Contains("--uv"))
{
    int ui = Array.IndexOf(args, "--uv");
    if (ui + 3 >= args.Length) { Console.WriteLine("usage: --uv <model.mdx> <geoset|-1> <outDir>"); return 1; }
    return MdxProbe.UvProbe.Run(install, args[ui + 1], int.Parse(args[ui + 2]), args[ui + 3]);
}

// --nodes <cascPath|file> lists every node with its kind, parent and flags, plus which geosets are
// skinned to it — how a billboarded card (a staff orb, a halo) is found and who draws it.
if (args.Contains("--nodes"))
{
    int ni = Array.IndexOf(args, "--nodes");
    string path = args[ni + 1];
    using var s = File.Exists(path) ? null : new Wc3Storage(install);
    var raw = File.Exists(path) ? File.ReadAllBytes(path) : s!.TryReadFile(path);
    if (raw is null) { Console.WriteLine("not found"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    Console.WriteLine($"VERS {mdl.Version}, {mdl.Nodes.Count} nodes, {mdl.Geosets.Count} geosets");
    // SKIN indexes the BONE chunk; matrix groups index by ObjectId — the same mapping PackRegions uses.
    var boneChunk = Enumerable.Range(0, mdl.Nodes.Count).Where(i => mdl.Nodes[i].Kind == Wc3ModelViewer.Core.Formats.MdxNodeKind.Bone).ToArray();
    for (int n = 0; n < mdl.Nodes.Count; n++)
    {
        var node = mdl.Nodes[n];
        var users = new List<string>();
        foreach (var g in mdl.Geosets)
        {
            int verts = 0;
            if (g.HasSkin)
            {
                for (int v = 0; v < g.VertexCount; v++)
                    for (int k = 0; k < 4; k++)
                    {
                        int ci = g.SkinBoneIndices[v * 4 + k];
                        if (ci < boneChunk.Length && boneChunk[ci] == n && g.SkinBoneWeights[v * 4 + k] > 0) { verts++; break; }
                    }
            }
            else
            {
                var groupHas = new bool[g.MatrixGroupSizes.Length];
                for (int gi = 0, mi = 0; gi < g.MatrixGroupSizes.Length; mi += g.MatrixGroupSizes[gi++])
                    for (int k = 0; k < g.MatrixGroupSizes[gi]; k++)
                        if (mi + k < g.MatrixIndices.Length && g.MatrixIndices[mi + k] == node.ObjectId) groupHas[gi] = true;
                foreach (byte vg in g.VertexGroups) if (vg < groupHas.Length && groupHas[vg]) verts++;
            }
            if (verts > 0) users.Add($"g{g.Index}:{verts}/{g.VertexCount}");
        }
        string flags = (node.Flags & ~(Wc3ModelViewer.Core.Formats.MdxNodeFlags.Bone)).ToString();
        Console.WriteLine($"  [{n,3}] {node.Kind,-16} '{node.Name}' obj={node.ObjectId} parent={node.ParentId} "
                          + $"flags={flags} pivot=({node.Pivot.X:0.#},{node.Pivot.Y:0.#},{node.Pivot.Z:0.#})"
                          + (users.Count > 0 ? "  skins " + string.Join(" ", users) : ""));
    }
    return 0;
}

// --billboards [n] [sd|hd] censuses billboarded nodes across the game: which billboard flags ship,
// and for every card a node carries, the node-local axis it lies flat on and the side its texture
// is drawn on (right = where u grows, up = where v shrinks, front = right x up). That front is the
// axis Warcraft III turns toward the camera, and it has to land on the axis SC2's BBSC turns.
if (args.Contains("--billboards"))
{
    int bi = Array.IndexOf(args, "--billboards");
    int limit = bi + 1 < args.Length && int.TryParse(args[bi + 1], out int bn) ? bn : 100000;
    string set = args.Contains("hd") ? "hd" : args.Contains("sd") ? "sd" : "all";
    using var s = new Wc3Storage(install);
    var index = Wc3AssetIndex.FromNames(s.EnumerateAll());
    var models = index.Models.Where(m => set == "all" || (set == "hd") == (m.ArtSet == Wc3ArtSet.Reforged))
        .Take(limit).ToList();

    var flagCount = new Dictionary<string, int>();
    var faceCount = new Dictionary<string, int>();
    var examples = new Dictionary<string, List<string>>();
    int parsed = 0, carriers = 0;
    foreach (var entry in models)
    {
        var raw = s.TryReadFile(entry.CascName);
        if (raw is null) continue;
        Wc3ModelViewer.Core.Formats.MdxModel mdl;
        try { mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw); } catch { continue; }
        parsed++;
        const Wc3ModelViewer.Core.Formats.MdxNodeFlags anyBb = Wc3ModelViewer.Core.Formats.MdxNodeFlags.Billboarded
            | Wc3ModelViewer.Core.Formats.MdxNodeFlags.BillboardLockX | Wc3ModelViewer.Core.Formats.MdxNodeFlags.BillboardLockY
            | Wc3ModelViewer.Core.Formats.MdxNodeFlags.BillboardLockZ;
        if (!mdl.Nodes.Exists(n => (n.Flags & anyBb) != 0)) continue;
        carriers++;
        var boneChunk = Enumerable.Range(0, mdl.Nodes.Count).Where(i => mdl.Nodes[i].Kind == Wc3ModelViewer.Core.Formats.MdxNodeKind.Bone).ToArray();
        var byObj = new Dictionary<int, int>();
        for (int i = 0; i < mdl.Nodes.Count; i++) byObj.TryAdd(mdl.Nodes[i].ObjectId, i);

        for (int ni = 0; ni < mdl.Nodes.Count; ni++)
        {
            var node = mdl.Nodes[ni];
            var bb = node.Flags & anyBb;
            if (bb == 0) continue;
            string fk = $"{node.Kind}:{bb}";
            flagCount[fk] = flagCount.GetValueOrDefault(fk) + 1;

            // Vertices whose dominant influence is this node.
            var pts = new List<(System.Numerics.Vector3 P, System.Numerics.Vector2 Uv)>();
            foreach (var g in mdl.Geosets)
            {
                if (g.Uvs.Length < g.VertexCount) continue;
                for (int v = 0; v < g.VertexCount; v++)
                {
                    int dom = -1;
                    if (g.HasSkin)
                    {
                        int bw = 0;
                        for (int k = 0; k < 4; k++)
                        {
                            int w = g.SkinBoneWeights[v * 4 + k], ci = g.SkinBoneIndices[v * 4 + k];
                            if (w > bw && ci < boneChunk.Length) { bw = w; dom = boneChunk[ci]; }
                        }
                    }
                    else if (v < g.VertexGroups.Length && g.MatrixGroupSizes.Length > 0)
                    {
                        int grp = Math.Min(g.VertexGroups[v], g.MatrixGroupSizes.Length - 1), at = 0;
                        for (int q = 0; q < grp; q++) at += g.MatrixGroupSizes[q];
                        if (g.MatrixGroupSizes[grp] == 1 && at < g.MatrixIndices.Length && byObj.TryGetValue(g.MatrixIndices[at], out int d)) dom = d;
                    }
                    if (dom == ni) pts.Add((g.Positions[v] - node.Pivot, g.Uvs[v]));
                }
            }
            if (pts.Count < 3) { Bump("no card", entry.RelativePath, node.Name); continue; }

            var mn = new System.Numerics.Vector3(float.MaxValue); var mx = new System.Numerics.Vector3(float.MinValue);
            foreach (var p in pts) { mn = System.Numerics.Vector3.Min(mn, p.P); mx = System.Numerics.Vector3.Max(mx, p.P); }
            var e = mx - mn; float big = Math.Max(e.X, Math.Max(e.Y, e.Z));
            var flatAxes = new[] { e.X, e.Y, e.Z }.Select((x, i) => (x, i)).Where(t => t.x < 0.02f * big).Select(t => "xyz"[t.i]).ToArray();
            if (flatAxes.Length != 1) { Bump($"{bb} not flat", entry.RelativePath, node.Name); continue; }

            var mu = System.Numerics.Vector3.Zero; var muUv = System.Numerics.Vector2.Zero;
            foreach (var p in pts) { mu += p.P; muUv += p.Uv; }
            mu /= pts.Count; muUv /= pts.Count;
            var cu = System.Numerics.Vector3.Zero; var cv = System.Numerics.Vector3.Zero;
            foreach (var p in pts) { cu += (p.P - mu) * (p.Uv.X - muUv.X); cv -= (p.P - mu) * (p.Uv.Y - muUv.Y); }
            static System.Numerics.Vector3 Dominant(System.Numerics.Vector3 c)
            {
                var a = System.Numerics.Vector3.Abs(c);
                return a.X >= a.Y && a.X >= a.Z ? new(Math.Sign(c.X), 0, 0)
                     : a.Y >= a.Z ? new(0, Math.Sign(c.Y), 0) : new(0, 0, Math.Sign(c.Z));
            }
            static string Name(System.Numerics.Vector3 a) =>
                a.X != 0 ? (a.X > 0 ? "+x" : "-x") : a.Y != 0 ? (a.Y > 0 ? "+y" : "-y") : a.Z > 0 ? "+z" : a.Z < 0 ? "-z" : "0";
            var right = Dominant(cu); var up = Dominant(cv);
            string face = right == up ? "degenerate uv" : $"right={Name(right)} up={Name(up)} front={Name(System.Numerics.Vector3.Cross(right, up))}";
            Bump($"{bb} flat={flatAxes[0]} {face}", entry.RelativePath, node.Name);
        }

        void Bump(string key, string model, string nodeName)
        {
            faceCount[key] = faceCount.GetValueOrDefault(key) + 1;
            if (!examples.TryGetValue(key, out var l)) examples[key] = l = [];
            if (l.Count < 3) l.Add($"{model} :: {nodeName}");
        }
    }

    Console.WriteLine($"--- {carriers:N0} of {parsed:N0} {set} models carry billboarded nodes ---");
    foreach (var kv in flagCount.OrderByDescending(k => k.Value)) Console.WriteLine($"  {kv.Value,6:N0}  {kv.Key}");
    Console.WriteLine("\n--- cards by flag, flat axis and texture handedness ---");
    foreach (var kv in faceCount.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Value,6:N0}  {kv.Key}\n          e.g. {string.Join("; ", examples[kv.Key])}");
    return 0;
}

// --bbcheck <cascPath|file> [sequence] runs the viewer's animator with a camera and measures whether
// each billboarded card actually faces it: for several camera bearings and times through the
// sequence, the skinned card's texture-right and texture-up directions are compared with the
// camera's right and up. A card that faces the camera reads ~+1.00 on both; one frozen in its
// animated pose (the staff orb lying flat as a disc) reads anything.
if (args.Contains("--bbcheck"))
{
    int ci = Array.IndexOf(args, "--bbcheck");
    string path = args[ci + 1];
    string? seqName = ci + 2 < args.Length && !args[ci + 2].StartsWith("--") ? args[ci + 2] : null;
    using var s = File.Exists(path) ? null : new Wc3Storage(install);
    var raw = File.Exists(path) ? File.ReadAllBytes(path) : s!.TryReadFile(path);
    if (raw is null) { Console.WriteLine("not found"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    var anim = new Wc3ModelViewer.Core.Formats.MdxAnimator(mdl);
    var seq = mdl.Sequences.FirstOrDefault(q => seqName is null ? q.Name.StartsWith("Stand", StringComparison.OrdinalIgnoreCase)
                                                               : q.Name.Equals(seqName, StringComparison.OrdinalIgnoreCase))
              ?? mdl.Sequences.FirstOrDefault();
    Console.WriteLine($"sequence: {seq?.Name ?? "(rest)"}; HasBillboards={anim.HasBillboards}");

    // Card vertices per billboarded node: geoset + vertex indices whose single influence is the node.
    const Wc3ModelViewer.Core.Formats.MdxNodeFlags bbFlags =
        Wc3ModelViewer.Core.Formats.MdxNodeFlags.Billboarded | Wc3ModelViewer.Core.Formats.MdxNodeFlags.BillboardLockZ;
    var byObj = new Dictionary<int, int>();
    for (int i = 0; i < mdl.Nodes.Count; i++) byObj.TryAdd(mdl.Nodes[i].ObjectId, i);
    var boneChunk = Enumerable.Range(0, mdl.Nodes.Count).Where(i => mdl.Nodes[i].Kind == Wc3ModelViewer.Core.Formats.MdxNodeKind.Bone).ToArray();
    var cards = new List<(int Node, Wc3ModelViewer.Core.Formats.MdxGeoset G, List<int> Verts)>();
    foreach (var g in mdl.Geosets)
    {
        var perNode = new Dictionary<int, List<int>>();
        for (int v = 0; v < g.VertexCount; v++)
        {
            int dom = -1;
            if (g.HasSkin)
            {
                int bw = 0;
                for (int k = 0; k < 4; k++)
                {
                    int w = g.SkinBoneWeights[v * 4 + k], c = g.SkinBoneIndices[v * 4 + k];
                    if (w > bw && c < boneChunk.Length) { bw = w; dom = boneChunk[c]; }
                }
            }
            else if (v < g.VertexGroups.Length && g.MatrixGroupSizes.Length > 0)
            {
                int grp = Math.Min(g.VertexGroups[v], g.MatrixGroupSizes.Length - 1), at = 0;
                for (int q = 0; q < grp; q++) at += g.MatrixGroupSizes[q];
                if (g.MatrixGroupSizes[grp] == 1 && at < g.MatrixIndices.Length && byObj.TryGetValue(g.MatrixIndices[at], out int d)) dom = d;
            }
            if (dom >= 0 && (mdl.Nodes[dom].Flags & bbFlags) != 0)
            {
                if (!perNode.TryGetValue(dom, out var l)) perNode[dom] = l = [];
                l.Add(v);
            }
        }
        foreach (var kv in perNode) if (kv.Value.Count >= 3) cards.Add((kv.Key, g, kv.Value));
    }

    var bearings = new[] { 0f, 60f, 135f, 210f, 300f };
    var elevations = new[] { 10f, 45f };
    var pos = new Dictionary<int, System.Numerics.Vector3[]>();
    foreach (var card in cards)
    {
        var node = mdl.Nodes[card.Node];
        double worstFront = 1, sumRight = 0, sumUp = 0; int n = 0;
        foreach (bool withCamera in new[] { false, true })
        {
            if (!withCamera) continue;
            foreach (float bearing in bearings)
            foreach (float elev in elevations)
            foreach (float frac in new[] { 0f, 0.37f, 0.71f })
            {
                double b = bearing * Math.PI / 180, e = elev * Math.PI / 180;
                // Camera sits out along (bearing, elevation) and looks back at the model.
                var toCam = new System.Numerics.Vector3((float)(Math.Cos(e) * Math.Cos(b)), (float)(Math.Cos(e) * Math.Sin(b)), (float)Math.Sin(e));
                var look = -toCam;
                var right = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(look, System.Numerics.Vector3.UnitZ));
                var up = System.Numerics.Vector3.Cross(right, look);
                anim.Camera = args.Contains("--nocamera") ? null : (look, System.Numerics.Vector3.UnitZ);
                int t = seq is null ? 0 : seq.IntervalStart + (int)(frac * seq.DurationMs);
                anim.Evaluate(seq, t, t);
                if (!pos.TryGetValue(card.G.Index, out var buf)) pos[card.G.Index] = buf = new System.Numerics.Vector3[card.G.VertexCount];
                anim.SkinGeoset(card.G, buf);

                // Texture right/up directions of the skinned card, by UV correlation.
                var mu = System.Numerics.Vector3.Zero; var muUv = System.Numerics.Vector2.Zero;
                foreach (int v in card.Verts) { mu += buf[v]; muUv += card.G.Uvs[v]; }
                mu /= card.Verts.Count; muUv /= card.Verts.Count;
                var cu = System.Numerics.Vector3.Zero; var cv = System.Numerics.Vector3.Zero;
                foreach (int v in card.Verts) { cu += (buf[v] - mu) * (card.G.Uvs[v].X - muUv.X); cv -= (buf[v] - mu) * (card.G.Uvs[v].Y - muUv.Y); }
                if (cu.LengthSquared() < 1e-12f || cv.LengthSquared() < 1e-12f) continue;
                var tr = System.Numerics.Vector3.Normalize(cu); var tu = System.Numerics.Vector3.Normalize(cv);
                var front = System.Numerics.Vector3.Cross(tr, tu);
                if (front.LengthSquared() < 1e-12f) continue;
                front = System.Numerics.Vector3.Normalize(front);
                var facing = (node.Flags & Wc3ModelViewer.Core.Formats.MdxNodeFlags.Billboarded) != 0
                    ? toCam : System.Numerics.Vector3.Normalize(toCam with { Z = 0 });
                worstFront = Math.Min(worstFront, Math.Abs(System.Numerics.Vector3.Dot(front, facing)));
                sumRight += System.Numerics.Vector3.Dot(tr, right);
                sumUp += System.Numerics.Vector3.Dot(tu, (node.Flags & Wc3ModelViewer.Core.Formats.MdxNodeFlags.Billboarded) != 0 ? up : System.Numerics.Vector3.UnitZ);
                n++;
            }
        }
        // Same card without a camera, for contrast: how far the animated pose is from facing.
        anim.Camera = null;
        Console.WriteLine($"  {node.Name,-14} ({(node.Flags & bbFlags)}) g{card.G.Index} {card.Verts.Count}v: "
                          + $"worst |front.toCamera|={worstFront:0.000}  mean right.camRight={sumRight / Math.Max(1, n):+0.000;-0.000}  "
                          + $"mean up.camUp={sumUp / Math.Max(1, n):+0.000;-0.000}  over {n} views");
    }
    return 0;
}

// --blppairs lists assets shipped as both .blp and .dds - ground truth for the BLP decoder.
if (args.Contains("--blppairs")) return MdxProbe.BlpPairs.Run(install);

// --layers <cascPath> dumps every material's layers: filter mode, shading flags, slot table and
// the diffuse alpha histogram — the evidence for what a layer's alpha channel actually means.
if (args.Contains("--layers"))
{
    int li = Array.IndexOf(args, "--layers");
    string path = args[li + 1];
    using var s = new Wc3Storage(install);
    var tc = new Wc3ModelViewer.Core.Casc.Wc3TextureCache(s);
    // A path that exists on disk is a loose custom model; it also needs its own folder searched.
    var raw = File.Exists(path) ? File.ReadAllBytes(path) : s.TryReadFile(path);
    if (File.Exists(path)) { tc.LocalRoots.Add(Path.GetDirectoryName(Path.GetFullPath(path))!); path = ""; }
    if (raw is null) { Console.WriteLine("not found"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    Console.WriteLine($"skipped chunks: {(mdl.SkippedChunks.Count == 0 ? "(none)" : string.Join(", ", mdl.SkippedChunks))}");
    Console.WriteLine("UV layers per geoset: " + string.Join(", ", mdl.Geosets.Select(g => $"g{g.Index}={g.UvLayers.Count}")));
    for (int mi = 0; mi < mdl.Materials.Count; mi++)
    {
        var mat = mdl.Materials[mi];
        Console.WriteLine($"MAT {mi}: {mat.Layers.Count} layer(s), priority {mat.PriorityPlane}");
        foreach (var layer in mat.Layers)
        {
            string slots = string.Join(", ", layer.TextureSlots.Select(kv =>
                $"{kv.Key}=tex{kv.Value}" + ((uint)kv.Value < (uint)mdl.Textures.Count
                    ? $"({System.IO.Path.GetFileName(mdl.Textures[kv.Value].FileName)}{(mdl.Textures[kv.Value].IsReplaceable ? ",REPL" + mdl.Textures[kv.Value].ReplaceableId : "")})"
                    : "")));
            Console.WriteLine($"   filter={layer.FilterMode} shading={layer.ShadingFlags} pbr={layer.IsPbr} alphaTrack={(layer.AlphaTrack is not null ? "yes" : "no")} staticAlpha={layer.Alpha:0.###} teamColorMult={layer.TeamColorMultiplier:0.###} texAnimId={layer.TextureAnimationId} coordId={layer.CoordId}");
            Console.WriteLine($"   slots: {slots}");
            if (layer.TextureIdTrack is { Count: > 0 } fb)
            {
                var ids = fb.Values.Select(v => (int)v).ToList();
                string first = (uint)ids[0] < (uint)mdl.Textures.Count
                    ? System.IO.Path.GetFileName(mdl.Textures[ids[0]].FileName) : "?";
                Console.WriteLine($"   FLIPBOOK: {fb.Count} keys over {fb.Times[^1]}ms, "
                                  + $"tex {ids[0]}..{ids[^1]} starting {first}");
            }
            int did = layer.DiffuseTextureId;
            if ((uint)did < (uint)mdl.Textures.Count)
            {
                var img = tc.Load(path, mdl.Textures[did], 0);
                if (img is not null)
                {
                    int n = img.Pixels.Length / 4, zero = 0, low = 0, full = 0;
                    for (int i = 3; i < img.Pixels.Length; i += 4)
                    {
                        byte a = img.Pixels[i];
                        if (a == 0) zero++; else if (a < 192) low++; else if (a == 255) full++;
                    }
                    Console.WriteLine($"   diffuse {System.IO.Path.GetFileName(mdl.Textures[did].FileName)} {img.Width}x{img.Height}: alpha zero={100.0 * zero / n:0}% partial={100.0 * low / n:0}% opaque={100.0 * full / n:0}%");
                }
            }
        }
    }
    return 0;
}

// --geosets <cascPath|file> lists every geoset with its LOD, size, material and GEOA visibility —
// the census that answers "is a geoset being dropped, or hidden, and why". A path that exists on
// disk is read directly, so hand-made models outside the archives can be censused too.
if (args.Contains("--geosets"))
{
    int gi = Array.IndexOf(args, "--geosets");
    string path = args[gi + 1];
    using var s = File.Exists(path) ? null : new Wc3Storage(install);
    var raw = File.Exists(path) ? File.ReadAllBytes(path) : s!.TryReadFile(path);
    if (raw is null) { Console.WriteLine("not found"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    Console.WriteLine($"LOD levels present: {string.Join(", ", mdl.LodLevels)}");
    int lod0 = mdl.LodLevels.Count > 0 ? mdl.LodLevels[0] : 0;
    int lod0v = 0, lod0t = 0;
    foreach (var g in mdl.Geosets.OrderBy(g => g.Index))
    {
        var anim = mdl.GeosetAnims.FirstOrDefault(a => a.GeosetId == g.Index);
        string vis;
        if (anim is null) vis = "no GEOA (always visible)";
        else if (anim.AlphaTrack is null) vis = $"static alpha={anim.Alpha:0.###}";
        else
        {
            var v = anim.AlphaTrack.Values;
            vis = $"track {anim.AlphaTrack.Count} keys, min={v.Min():0.##} max={v.Max():0.##} first={v[0]:0.##}";
        }
        string mark = g.LodId == lod0 ? "" : "   (not LOD0)";
        if (g.LodId == lod0) { lod0v += g.VertexCount; lod0t += g.Indices.Length / 3; }
        // Centroid + Z range locate the head (top of the model) among unnamed geosets.
        var c = System.Numerics.Vector3.Zero;
        float zmin = float.MaxValue, zmax = float.MinValue;
        foreach (var p in g.Positions) { c += p; zmin = Math.Min(zmin, p.Z); zmax = Math.Max(zmax, p.Z); }
        if (g.Positions.Length > 0) c /= g.Positions.Length;
        Console.WriteLine($"geoset {g.Index,2}: lod={g.LodId} verts={g.VertexCount,5} tris={g.Indices.Length / 3,5} mat={g.MaterialId,2}  z[{zmin,6:0}..{zmax,6:0}] cz={c.Z,6:0}  {vis}{mark}");
    }
    Console.WriteLine($"LOD{lod0} totals: {mdl.Geosets.Count(g => g.LodId == lod0)} geosets, {lod0v} verts, {lod0t} tris");
    return 0;
}

// --cutouts <cascPath> [lod] runs the exact Compose + CutoutCoverage path the viewer uses, so the
// opaque/cutout classification (and the draw-order fix that depends on it) can be checked per LOD.
if (args.Contains("--cutouts"))
{
    int ci = Array.IndexOf(args, "--cutouts");
    string path = args[ci + 1];
    int lod = ci + 2 < args.Length && int.TryParse(args[ci + 2], out int l) ? l : 1;
    using var s = new Wc3Storage(install);
    var tc = new Wc3ModelViewer.Core.Casc.Wc3TextureCache(s);
    // A path that exists on disk is a loose custom model; it also needs its own folder searched.
    var raw = File.Exists(path) ? File.ReadAllBytes(path) : s.TryReadFile(path);
    if (File.Exists(path)) { tc.LocalRoots.Add(Path.GetDirectoryName(Path.GetFullPath(path))!); path = ""; }
    if (raw is null) { Console.WriteLine("not found"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    Console.WriteLine($"LOD {lod} geosets — Compose blend, cutout coverage, viewer classification:");
    foreach (var g in mdl.Geosets.Where(g => g.LodId == lod).OrderBy(g => g.Index))
    {
        if ((uint)g.MaterialId >= (uint)mdl.Materials.Count) continue;
        var comp = Wc3ModelViewer.Core.Formats.MaterialCompositor.Compose(mdl, mdl.Materials[g.MaterialId], tc, path, 0);
        float cov = Wc3ModelViewer.Core.Formats.MaterialCompositor.CutoutCoverage(comp.Texture, g);
        string cls = comp.Blend == Wc3ModelViewer.Core.Formats.CompositeBlend.AlphaTest
            ? (cov >= 0.005f ? "CUTOUT (drawn after opaque)" : "opaque (downgraded)")
            : comp.Blend.ToString();
        Console.WriteLine($"  geoset {g.Index,2} mat {g.MaterialId,2}: Compose={comp.Blend,-9} coverage={cov * 100,6:0.00}%  -> {cls}");
    }
    return 0;
}

// --effects [n] censuses the chunks the reader currently skips across n real models. Effects work
// starts here rather than from the spec: it says which emitter chunks actually ship, how often, and
// on which models — so the export path is built for the data that exists, not the format's full
// surface. Prints per-chunk model counts and the worst offenders.
if (args.Contains("--effects"))
{
    int ei = Array.IndexOf(args, "--effects");
    int limit = ei + 1 < args.Length && int.TryParse(args[ei + 1], out int n) ? n : 400;
    using var s = new Wc3Storage(install);
    var index = Wc3AssetIndex.FromNames(s.EnumerateAll());
    var models = index.Models.Where(m => !m.IsPortrait).Take(limit).ToList();

    var chunkModels = new Dictionary<string, int>(StringComparer.Ordinal);
    var chunkBytes = new Dictionary<string, long>(StringComparer.Ordinal);
    var carriers = new List<(string Name, string Chunks)>();
    int parsed = 0;
    foreach (var entry in models)
    {
        var raw = s.TryReadFile(entry.CascName);
        if (raw is null) continue;
        Wc3ModelViewer.Core.Formats.MdxModel mdl;
        try { mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw); } catch { continue; }
        parsed++;
        if (mdl.SkippedChunks.Count == 0) continue;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entryText in mdl.SkippedChunks)
        {
            int paren = entryText.IndexOf('(');
            string tag = paren < 0 ? entryText : entryText[..paren];
            long bytes = paren < 0 ? 0 : long.Parse(entryText[(paren + 1)..^1]);
            if (seen.Add(tag)) chunkModels[tag] = chunkModels.GetValueOrDefault(tag) + 1;
            chunkBytes[tag] = chunkBytes.GetValueOrDefault(tag) + bytes;
        }
        carriers.Add((entry.RelativePath, string.Join(" ", mdl.SkippedChunks)));
    }

    Console.WriteLine($"--- skipped-chunk census over {parsed:N0} parsed models ---");
    Console.WriteLine($"  {"chunk",-6} {"models",8} {"% of set",9} {"total bytes",14}");
    foreach (var kv in chunkModels.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kv.Key,-6} {kv.Value,8:N0} {kv.Value * 100.0 / parsed,8:0.0}% {chunkBytes[kv.Key],14:N0}");

    Console.WriteLine("\n--- models carrying the most effect data ---");
    foreach (var c in carriers.OrderByDescending(c => c.Chunks.Length).Take(12))
        Console.WriteLine($"  {c.Name}\n      {c.Chunks}");
    return 0;
}

// --corn [n] answers one question with bytes: is Reforged's PopcornFX convertible at all? It reads
// what every CORN emitter references, then checks whether the archive even contains the baked
// effects those paths name. An emitter we cannot resolve to data is not a conversion problem.
if (args.Contains("--corn"))
{
    int ci = Array.IndexOf(args, "--corn");
    int limit = ci + 1 < args.Length && int.TryParse(args[ci + 1], out int cn) ? cn : 400;
    using var s = new Wc3Storage(install);
    var names = s.EnumerateAll();
    var index = Wc3AssetIndex.FromNames(names);

    var popcornAssets = names
        .Where(n => Path.GetExtension(n).StartsWith(".pk", StringComparison.OrdinalIgnoreCase))
        .ToList();
    Console.WriteLine($"--- archive: {names.Count:N0} files, {popcornAssets.Count:N0} with a .pk* extension ---");
    foreach (var grp in popcornAssets.GroupBy(n => Path.GetExtension(n).ToLowerInvariant())
                                     .OrderByDescending(g => g.Count()))
        Console.WriteLine($"  {grp.Key,-8} {grp.Count(),6:N0}   e.g. {grp.First()}");

    var refs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    var flagSet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    int emitters = 0, models = 0, dumped = 0;
    foreach (var entry in index.Models.Where(m => !m.IsPortrait).Take(limit))
    {
        var raw = s.TryReadFile(entry.CascName);
        if (raw is null) continue;
        var corn = Chunks(raw).FirstOrDefault(c => c.Tag == "CORN");
        if (corn.Tag is null) continue;
        models++;

        int p = corn.Start, end = corn.Start + corn.Size;
        while (p + 8 <= end)
        {
            int inclusive = BitConverter.ToInt32(raw, p);
            if (inclusive < 8 || p + inclusive > end) break;
            int nodeSize = BitConverter.ToInt32(raw, p + 4);
            int payload = p + 4 + nodeSize;                     // node is self-sizing; payload follows

            // Locate the .pkb path by content rather than by a spec offset: the two C4Colors ahead
            // of it are floats that could be anything, and a wrong offset here would read garbage
            // that still looks like a string.
            int at = -1;
            for (int q = payload; q + 4 < p + inclusive; q++)
                if (raw[q] == '.' && raw[q + 1] is (byte)'p' or (byte)'P'
                    && raw[q + 2] is (byte)'k' or (byte)'K') { at = q; break; }
            if (at < 0) { p += inclusive; continue; }
            int start = at;
            while (start > payload && raw[start - 1] >= 0x20 && raw[start - 1] < 0x7f) start--;
            int stop = at;
            while (stop < p + inclusive && raw[stop] >= 0x20 && raw[stop] < 0x7f) stop++;
            string path = Encoding.ASCII.GetString(raw, start, stop - start);

            // The flags string follows the 260-byte path field.
            int flagsAt = start + 260;
            string flags = "";
            if (flagsAt < p + inclusive)
            {
                int fe = flagsAt;
                while (fe < p + inclusive && raw[fe] >= 0x20 && raw[fe] < 0x7f) fe++;
                flags = Encoding.ASCII.GetString(raw, flagsAt, fe - flagsAt);
            }

            emitters++;
            refs[path] = refs.GetValueOrDefault(path) + 1;
            if (flags.Length > 0) flagSet[flags] = flagSet.GetValueOrDefault(flags) + 1;
            if (dumped < 4)
            {
                dumped++;
                Console.WriteLine($"\n  {entry.RelativePath}");
                Console.WriteLine($"    payload starts {start - payload} B before the path "
                                + $"(spec says 32 = two C4Colors)");
                Console.WriteLine($"    path  = {path}");
                Console.WriteLine($"    flags = {flags}");
            }
            p += inclusive;
        }
    }

    Console.WriteLine($"\n--- {emitters:N0} CORN emitters across {models:N0} models "
                    + $"referencing {refs.Count:N0} distinct effects ---");
    int present = 0;
    foreach (var kv in refs.OrderByDescending(k => k.Value).Take(25))
    {
        // Models name the .pkfx *source*; the archive ships the .pkb *bake* of it.
        string leaf = Path.GetFileNameWithoutExtension(kv.Key.Replace('/', '\\')) + ".pkb";
        bool found = names.Any(n => n.EndsWith(leaf, StringComparison.OrdinalIgnoreCase));
        if (found) present++;
        Console.WriteLine($"  {(found ? "IN ARCHIVE" : "not found ")} {kv.Value,4}x  {kv.Key}");
    }
    Console.WriteLine($"\n  {present} of the top {Math.Min(25, refs.Count)} effects exist as files in the archive");
    // What a bake actually contains decides whether "bake it in" is engineering or reverse
    // engineering. Dump the head of one so the answer rests on bytes.
    string? sample = names.FirstOrDefault(n => n.EndsWith(".pkb", StringComparison.OrdinalIgnoreCase));
    if (sample is not null && s.TryReadFile(sample) is { } pkb)
    {
        Console.WriteLine($"\n--- {sample} ({pkb.Length:N0} bytes) ---");
        for (int row = 0; row < 6; row++)
        {
            int off = row * 16;
            if (off >= pkb.Length) break;
            int n = Math.Min(16, pkb.Length - off);
            string hex = string.Join(" ", Enumerable.Range(0, n).Select(k => pkb[off + k].ToString("x2")));
            string txt = string.Concat(Enumerable.Range(0, n)
                .Select(k => pkb[off + k] >= 0x20 && pkb[off + k] < 0x7f ? (char)pkb[off + k] : '.'));
            Console.WriteLine($"  {off:x4}  {hex,-47}  {txt}");
        }
        var strings = new List<string>();
        for (int q = 0, run = 0; q < pkb.Length; q++)
        {
            if (pkb[q] >= 0x20 && pkb[q] < 0x7f) run++;
            else { if (run >= 6) strings.Add(Encoding.ASCII.GetString(pkb, q - run, run)); run = 0; }
        }
        // Float tables dominate the printable runs, so filter to runs that look like identifiers:
        // a bake that names its node types and attributes is readable; one that does not is not.
        var idents = strings.Where(t => t.Count(char.IsLetter) >= t.Length * 0.7)
                            .Distinct(StringComparer.Ordinal).ToList();
        Console.WriteLine($"  {strings.Count} printable runs, {idents.Count} identifier-like:");
        foreach (string t in idents) Console.WriteLine($"    {t}");
        var assets = strings.Where(t => t.Contains('/') || t.Contains('.', StringComparison.Ordinal)
                                        && t.Any(char.IsLetter) && t.Count(char.IsLetter) > 4)
                            .Distinct(StringComparer.Ordinal).ToList();
        Console.WriteLine($"  {assets.Count} asset-like strings:");
        foreach (string t in assets.Take(40)) Console.WriteLine($"    {t}");

        // A converted effect is only as good as the sprites it draws. Sweep every bake for texture
        // references and check them against the archive: if the art is missing, nothing else matters.
        Console.WriteLine("\n--- texture references across all bakes ---");
        var texRefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int bakes = 0;
        foreach (string pk in popcornAssets.Where(n => n.EndsWith(".pkb", StringComparison.OrdinalIgnoreCase)))
        {
            var data = s.TryReadFile(pk);
            if (data is null) continue;
            bakes++;
            for (int q = 0, run = 0; q < data.Length; q++)
            {
                if (data[q] >= 0x20 && data[q] < 0x7f) { run++; continue; }
                if (run >= 8)
                {
                    string t = Encoding.ASCII.GetString(data, q - run, run);
                    foreach (string ext in (string[])[".tif", ".dds", ".tga", ".png"])
                    {
                        int e = t.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                        if (e < 0) continue;
                        string tp = t[..(e + ext.Length)];
                        int cut = tp.LastIndexOfAny([':', '>', '<', '=', '"', '\'', '$', '!', '&']);
                        if (cut >= 0) tp = tp[(cut + 1)..];
                        texRefs[tp] = texRefs.GetValueOrDefault(tp) + 1;
                    }
                }
                run = 0;
            }
        }
        var lookup = new HashSet<string>(names.Select(n => n.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
        int resolved = texRefs.Keys.Count(t => Resolves(t, lookup));
        Console.WriteLine($"  {bakes:N0} bakes -> {texRefs.Count:N0} distinct textures, "
                        + $"{resolved:N0} resolve in the archive ({resolved * 100.0 / Math.Max(1, texRefs.Count):0.0}%)");
        foreach (var kv in texRefs.OrderByDescending(k => k.Value).Take(12))
            Console.WriteLine($"  {(Resolves(kv.Key, lookup) ? "ok     " : "MISSING")} {kv.Value,4}x  {kv.Key}");

        static bool Resolves(string t, HashSet<string> lookup)
        {
            string leaf = t.Replace('\\', '/');
            int slash = leaf.LastIndexOf('/');
            string file = slash < 0 ? leaf : leaf[(slash + 1)..];
            string stem = Path.GetFileNameWithoutExtension(file);
            // WC3 stores these as .dds under war3.w3mod:_hd.w3mod:, whatever the bake calls them.
            return lookup.Any(n => n.EndsWith("/" + stem + ".dds", StringComparison.OrdinalIgnoreCase)
                                || n.EndsWith(":" + stem + ".dds", StringComparison.OrdinalIgnoreCase)
                                || n.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase));
        }
    }

    Console.WriteLine("\n--- popcornFlags seen ---");
    foreach (var kv in flagSet.OrderByDescending(k => k.Value).Take(15))
        Console.WriteLine($"  {kv.Value,5}x  {kv.Key}");
    return 0;
}

// --fx [n] parses n models and asserts every emitter field decodes to something physically sane.
// A struct read at the wrong offset still produces numbers; the only way to know the layout is
// right is to check those numbers mean something across the whole game rather than one model.
if (args.Contains("--fx"))
{
    int fi = Array.IndexOf(args, "--fx");
    int limit = fi + 1 < args.Length && int.TryParse(args[fi + 1], out int fn) ? fn : 600;
    using var s = new Wc3Storage(install);
    var index = Wc3AssetIndex.FromNames(s.EnumerateAll());

    int parsed = 0, withFx = 0, par = 0, rib = 0, lit = 0, corn = 0, bad = 0;
    int flipModels = 0, flipLayers = 0, flipFrames = 0;
    var flipExamples = new List<string>();
    var complaints = new List<string>();
    void Check(bool ok, string model, string what)
    {
        if (ok) return;
        bad++;
        if (complaints.Count < 25) complaints.Add($"  {model}: {what}");
    }

    foreach (var entry in index.Models.Where(m => !m.IsPortrait).Take(limit))
    {
        var raw = s.TryReadFile(entry.CascName);
        if (raw is null) continue;
        Wc3ModelViewer.Core.Formats.MdxModel m;
        try { m = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw); } catch { continue; }
        parsed++;

        // Flipbook (KMTF) layers ride here too: a texture animation is an effect in everything but
        // name, and the fountains' water is the case that proved the slot table must be terminated
        // by a track tag rather than by its declared capacity.
        int layersHere = m.Materials.Sum(mm => mm.Layers.Count(l => l.TextureIdTrack is { Count: > 0 }));
        if (layersHere > 0)
        {
            flipModels++;
            flipLayers += layersHere;
            flipFrames += m.Materials.Sum(mm => mm.Layers.Sum(l => l.FlipbookTextureIds.Count()));
            if (flipExamples.Count < 6) flipExamples.Add(entry.RelativePath);
        }

        if (!m.HasEffects) continue;
        withFx++;
        par += m.ParticleEmitters.Count; rib += m.RibbonEmitters.Count;
        lit += m.Lights.Count; corn += m.PopcornEmitterCount;
        string name = entry.RelativePath;

        foreach (var e in m.ParticleEmitters)
        {
            Check(e.NodeIndex >= 0 && e.NodeIndex < m.Nodes.Count, name, $"{e.Name} node index {e.NodeIndex}");
            Check(e.Rows is >= 1 and <= 64 && e.Columns is >= 1 and <= 64, name, $"{e.Name} sheet {e.Rows}x{e.Columns}");
            Check(Enum.IsDefined(e.Blend), name, $"{e.Name} blend {(int)e.Blend}");
            Check(Enum.IsDefined(e.ParticleType), name, $"{e.Name} type {(int)e.ParticleType}");
            Check(e.TextureId >= -1 && e.TextureId < Math.Max(1, m.Textures.Count), name, $"{e.Name} texture {e.TextureId} of {m.Textures.Count}");
            Check(e.Life is >= 0 and < 1000, name, $"{e.Name} life {e.Life}");
            Check(e.EmissionRate is >= 0 and < 100000, name, $"{e.Name} rate {e.EmissionRate}");
            Check(Sane(e.StartColor) && Sane(e.MiddleColor) && Sane(e.EndColor), name, $"{e.Name} colours out of 0..1");
        }
        foreach (var e in m.RibbonEmitters)
        {
            Check(e.MaterialId >= -1 && e.MaterialId < Math.Max(1, m.Materials.Count), name, $"{e.Name} material {e.MaterialId} of {m.Materials.Count}");
            Check(e.EdgeLifetime is >= 0 and < 1000, name, $"{e.Name} edge life {e.EdgeLifetime}");
            Check(e.Rows is >= 1 and <= 64 && e.Columns is >= 1 and <= 64, name, $"{e.Name} sheet {e.Rows}x{e.Columns}");
            Check(Sane(e.Color), name, $"{e.Name} colour out of 0..1");
        }
        foreach (var e in m.Lights)
        {
            Check(Enum.IsDefined(e.LightType), name, $"{e.Name} light type {(int)e.LightType}");
            Check(e.AttenuationEnd >= 0 && e.AttenuationEnd < 100000, name, $"{e.Name} atten end {e.AttenuationEnd}");
            Check(Sane(e.Color), name, $"{e.Name} colour out of 0..1");
        }
    }

    Console.WriteLine($"--- effect parse over {parsed:N0} models ({withFx:N0} carry effects) ---");
    Console.WriteLine($"  particle emitters : {par:N0}");
    Console.WriteLine($"  ribbon emitters   : {rib:N0}");
    Console.WriteLine($"  lights            : {lit:N0}");
    Console.WriteLine($"  popcorn (dropped) : {corn:N0}");
    Console.WriteLine($"  flipbook layers   : {flipLayers:N0} across {flipModels:N0} models, {flipFrames:N0} frames");
    foreach (string e in flipExamples) Console.WriteLine($"      {e}");
    Console.WriteLine(bad == 0
        ? "\n  every emitter field is within a sane range — layout confirmed"
        : $"\n  {bad:N0} implausible values:");
    foreach (string c in complaints) Console.WriteLine(c);
    return bad == 0 ? 0 : 1;

    static bool Sane(System.Numerics.Vector3 c) =>
        c.X is >= -0.01f and <= 1.01f && c.Y is >= -0.01f and <= 1.01f && c.Z is >= -0.01f and <= 1.01f;
}

// --simfx <cascPath> [seconds] runs the viewer's effect simulation headlessly and reports what it
// produced. Emitters are easy to "render" as nothing at all — a wrong cone, a zero rate or a
// visibility track read backwards all just show an empty screen — so the numbers are checked here
// rather than by squinting at the viewport.
if (args.Contains("--simfx"))
{
    int si = Array.IndexOf(args, "--simfx");
    string path = args[si + 1];
    float seconds = si + 2 < args.Length && float.TryParse(args[si + 2], out float sec) ? sec : 3f;
    using var s = new Wc3Storage(install);
    var raw = s.TryReadFile(path);
    if (raw is null) { Console.WriteLine($"not found: {path}"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    Console.WriteLine($"{path}\n  {mdl.ParticleEmitters.Count} particle emitters, {mdl.RibbonEmitters.Count} ribbons, "
                      + $"{mdl.Lights.Count} lights, {mdl.PopcornEmitterCount} popcorn (dropped)");
    if (mdl.Sequences.Count == 0) { Console.WriteLine("  no sequences"); return 0; }

    var animator = new Wc3ModelViewer.Core.Formats.MdxAnimator(mdl);
    foreach (var seq in mdl.Sequences.Take(3))
    {
        var sim = new Wc3ModelViewer.Core.Formats.MdxEffectSimulator(mdl);
        const float step = 1f / 30f;
        int frames = Math.Max(1, (int)(seconds / step));
        int peak = 0, peakEdges = 0;
        double totalScale = 0; int sampled = 0;
        for (int f = 0; f < frames; f++)
        {
            int t = seq.IntervalStart + (int)(f * step * 1000) % Math.Max(1, seq.DurationMs);
            animator.Evaluate(seq, t, (long)(f * step * 1000));
            sim.Update(step, animator, seq, t, (long)(f * step * 1000));
            peak = Math.Max(peak, sim.Particles.Count);
            foreach (var tr in sim.Trails) peakEdges = Math.Max(peakEdges, tr.Edges.Count);
            foreach (var p in sim.Particles) { totalScale += sim.Appearance(p).Scale; sampled++; }
        }
        Console.WriteLine($"  [{seq.Name,-16}] peak {peak,5} particles, {peakEdges,4} ribbon edges, "
                          + $"mean scale {(sampled == 0 ? 0 : totalScale / sampled),7:0.00}");
    }
    return 0;
}

// --audit <m3Path> re-reads a written .m3's texture references and reports whether each resolves
// on disk next to the model (with the Assets/ head stripped, matching the paste-into-Assets layout).
if (args.Contains("--audit"))
{
    string m3Path = args[Array.IndexOf(args, "--audit") + 1];
    var audit = Wc3ModelViewer.Core.Convert.M3TextureAudit.Verify(m3Path);
    foreach (var r in audit) Console.WriteLine($"  {(r.Resolved ? "ok     " : "MISSING")} {r.Path}");
    Console.WriteLine(Wc3ModelViewer.Core.Convert.M3TextureAudit.Summarize(audit));
    return audit.All(r => r.Resolved) ? 0 : 1;
}

// --exportto <outRoot> <cascPath> <name> [scale] writes one model in the shipping layout.
if (args.Contains("--exportto"))
{
    int ei = Array.IndexOf(args, "--exportto");
    string outRoot = args[ei + 1], cascPath = args[ei + 2], mname = args[ei + 3];
    float sc = ei + 4 < args.Length && float.TryParse(args[ei + 4], out float f) ? f : 1f;
    int slot = ei + 5 < args.Length && int.TryParse(args[ei + 5], out int sl) ? sl : 0;
    using var s = new Wc3Storage(install);
    var tex = new Wc3ModelViewer.Core.Casc.Wc3TextureCache(s);
    var raw = s.TryReadFile(cascPath);
    if (raw is null) { Console.WriteLine($"not found: {cascPath}"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    var opts = new Wc3ModelViewer.Core.Convert.M3ExportOptions { Lod = 0, ModelName = mname, Scale = sc, TeamColor = slot };
    var res = new Wc3ModelViewer.Core.Convert.M3Exporter(mdl, opts).Export(tex, cascPath);
    string d = Path.Combine(outRoot, mname);
    string td = Path.Combine(d, opts.TextureFolder);
    Directory.CreateDirectory(td);
    File.WriteAllBytes(Path.Combine(d, mname + ".m3"), res.M3);
    foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(td, t.FileName), t.Data);
    Console.WriteLine($"{mname}: {res.M3.Length:N0} B, {res.Textures.Count} textures -> {d}");
    foreach (string line in res.Log) Console.WriteLine("   | " + line);
    return 0;
}

// --simref <dir> [modelPath] exports a model to .m3 and, alongside it, a JSON reference of what
// the VIEWER renders for one sequence (skinned vertex positions at exact baked frames). m3sim.py
// then composes the .m3's own bone keys and skins with them; any divergence is an encoding bug.
if (args.Contains("--simref"))
{
    int si = Array.IndexOf(args, "--simref");
    string dir = si + 1 < args.Length ? args[si + 1] : Path.Combine(Path.GetTempPath(), "wc3-simref");
    string modelPath = si + 2 < args.Length && !args[si + 2].StartsWith("--")
        ? args[si + 2]
        : @"war3.w3mod:_hd.w3mod:units\human\footman\footman.mdx";
    return MdxProbe.SimRef.Run(install, dir, modelPath);
}

// --blender <src.m3> <dst.m3> runs the m3studio serialisation pipeline on an existing export.
if (args.Contains("--blender"))
{
    int i = Array.IndexOf(args, "--blender");
    if (i + 2 >= args.Length) { Console.WriteLine("usage: --blender <src.m3> <dst.m3>"); return 1; }
    Console.WriteLine(Wc3ModelViewer.Core.Convert.BlenderM3Studio.IsAvailable(out string? blender, out string? addon)
        ? $"blender = {blender}\naddon   = {addon}"
        : "pipeline NOT available");
    var (ok, log) = Wc3ModelViewer.Core.Convert.BlenderM3Studio.Convert(args[i + 1], args[i + 2]);
    Console.WriteLine(log);
    Console.WriteLine(ok ? "PIPELINE OK" : "PIPELINE FAILED");
    return ok ? 0 : 1;
}

using var storage = new Wc3Storage(install);

(string Label, string Name)[] targets =
[
    ("SD knight", @"war3.w3mod:units\human\knight\knight.mdx"),
    ("HD knight", @"war3.w3mod:_hd.w3mod:units\human\knight\knight.mdx"),
    ("SD footman", @"war3.w3mod:units\human\footman\footman.mdx"),
    ("HD footman", @"war3.w3mod:_hd.w3mod:units\human\footman\footman.mdx"),
];

foreach (var (label, name) in targets)
{
    var b = storage.TryReadFile(name);
    if (b is null) { Console.WriteLine($"\n##### {label}: NOT FOUND\n"); continue; }

    Console.WriteLine($"\n########## {label} — {name} ({b.Length:N0} bytes) ##########");
    var chunks = Chunks(b);
    Console.WriteLine("chunks: " + string.Join(", ", chunks.Select(c => $"{c.Tag}({c.Size:N0})")));

    if (chunks.FirstOrDefault(c => c.Tag == "VERS") is { Tag: not null } v)
        Console.WriteLine($"VERS = {BitConverter.ToUInt32(b, v.Start)}");

    if (chunks.FirstOrDefault(c => c.Tag == "MTLS") is { Tag: not null } m) DumpMtls(b, m.Start, m.Size);
    if (chunks.FirstOrDefault(c => c.Tag == "GEOS") is { Tag: not null } g) DumpGeos(b, g.Start, g.Size);
    if (chunks.FirstOrDefault(c => c.Tag == "TEXS") is { Tag: not null } t) DumpTexs(b, t.Start, t.Size);
}

return 0;

static List<(string Tag, int Start, int Size)> Chunks(byte[] b)
{
    var list = new List<(string, int, int)>();
    int p = 4;
    while (p + 8 <= b.Length)
    {
        string tag = Encoding.ASCII.GetString(b, p, 4);
        int size = BitConverter.ToInt32(b, p + 4);
        if (size < 0 || p + 8 + size > b.Length) break;
        list.Add((tag, p + 8, size));
        p += 8 + size;
    }
    return list;
}

static void Hex(byte[] b, int start, int count, string indent = "    ")
{
    for (int i = 0; i < count; i += 16)
    {
        int n = Math.Min(16, count - i);
        var hex = string.Join(' ', Enumerable.Range(0, n).Select(k => b[start + i + k].ToString("x2")));
        var asc = new string(Enumerable.Range(0, n).Select(k => b[start + i + k] is >= 32 and < 127 ? (char)b[start + i + k] : '.').ToArray());
        Console.WriteLine($"{indent}{i:x4}  {hex,-47}  {asc}");
    }
}

// Classic MTLS entry: uint32 inclusiveSize, int32 priorityPlane, uint32 flags, then "LAYS".
// Reforged MTLS entry: the same three, then char[80] shaderName, then "LAYS".
// Which one it is shows up immediately in where the "LAYS" tag lands.
static void DumpMtls(byte[] b, int start, int size)
{
    Console.WriteLine($"\n-- MTLS ({size:N0} bytes) --");
    int p = start, end = start + size, mat = 0;
    while (p + 12 <= end && mat < 3)
    {
        int inclusive = BitConverter.ToInt32(b, p);
        int priority = BitConverter.ToInt32(b, p + 4);
        uint flags = BitConverter.ToUInt32(b, p + 8);
        string at12 = Encoding.ASCII.GetString(b, p + 12, 4);
        string at92 = p + 96 <= end ? Encoding.ASCII.GetString(b, p + 92, 4) : "----";
        Console.WriteLine($"  material[{mat}] inclusiveSize={inclusive} priorityPlane={priority} flags=0x{flags:X}");
        Console.WriteLine($"    tag@+12 = '{Escape(at12)}'   tag@+92 = '{Escape(at92)}'");
        if (at12 == "LAYS") Console.WriteLine("    => CLASSIC material layout (LAYS immediately after flags, no shader name)");
        else if (at92 == "LAYS") Console.WriteLine($"    => REFORGED material layout (char[80] shaderName = '{FixedStr(b, p + 12, 80)}')");
        else Console.WriteLine("    => UNRECOGNISED layout");
        Console.WriteLine("    first 128 bytes:");
        Hex(b, p, Math.Min(128, end - p), "      ");
        if (inclusive <= 0 || p + inclusive > end) break;
        p += inclusive;
        mat++;
    }
}

// The interesting question for GEOS is whether a geoset is led by an inclusive size and which
// sub-chunk tags it carries: GNDX/MTGC/MATS (classic skinning) vs TANG/SKIN (Reforged skinning).
static void DumpGeos(byte[] b, int start, int size)
{
    Console.WriteLine($"\n-- GEOS ({size:N0} bytes) --");
    Console.WriteLine($"  first 32 bytes of chunk payload:");
    Hex(b, start, Math.Min(32, size), "      ");

    int inclusive = BitConverter.ToInt32(b, start);
    Console.WriteLine($"  int32@0 = {inclusive:N0}  (an inclusive geoset size, or a geoset COUNT?)");
    string tagAt4 = Encoding.ASCII.GetString(b, start + 4, 4);
    Console.WriteLine($"  tag@+4  = '{Escape(tagAt4)}'");

    // Walk sub-chunk tags of the first geoset by trusting the leading inclusive size.
    int p = start + 4, end = start + Math.Min(inclusive, size);
    var seen = new List<string>();
    while (p + 8 <= end)
    {
        string tag = Encoding.ASCII.GetString(b, p, 4);
        if (!tag.All(c => c is >= 'A' and <= 'Z')) break;
        int count = BitConverter.ToInt32(b, p + 4);
        seen.Add($"{tag}[{count:N0}]");
        int bytes = tag switch
        {
            "VRTX" or "NRMS" => count * 12,
            "PTYP" or "PCNT" or "MTGC" or "MATS" => count * 4,
            "PVTX" => count * 2,
            "GNDX" => count,
            "TANG" => count * 16,
            "SKIN" => count,          // SKIN's header value is a BYTE count, not an element count
            "UVAS" => 0,              // followed by UVBS sub-chunks
            "UVBS" => count * 8,
            _ => -1,
        };
        if (bytes < 0) { seen.Add($"<unknown {tag}, stopping>"); break; }
        p += 8 + bytes;   // UVAS contributes only its header; its UVBS blocks follow immediately
    }
    Console.WriteLine("  geoset[0] sub-chunks (walked): " + string.Join(" ", seen));

    // The walk stops at the geoset's trailing fixed fields, which carry no tag. Scan the rest of
    // geoset[0] for known tags so TANG/SKIN/UVAS are found wherever they sit.
    Console.WriteLine("  tag scan across geoset[0]:");
    string[] known = ["VRTX", "NRMS", "PTYP", "PCNT", "PVTX", "GNDX", "MTGC", "MATS", "TANG", "SKIN", "UVAS", "UVBS", "BIDX", "BWGT"];
    int geoEnd = start + Math.Min(inclusive, size);
    for (int q = start; q + 8 <= geoEnd; q++)
    {
        string tag = Encoding.ASCII.GetString(b, q, 4);
        if (Array.IndexOf(known, tag) < 0) continue;
        int count = BitConverter.ToInt32(b, q + 4);
        if (count < 0 || count > 4_000_000) continue;
        Console.WriteLine($"      +{q - start,-8:N0} {tag} count={count:N0}");
    }

    bool hasSkin = false;
    for (int q = start; q + 8 <= geoEnd; q++)
        if (Encoding.ASCII.GetString(b, q, 4) == "SKIN") { hasSkin = true; break; }
    Console.WriteLine(hasSkin ? "  => REFORGED skinning (SKIN present)" : "  => CLASSIC skinning (no SKIN chunk)");
}

static void DumpTexs(byte[] b, int start, int size)
{
    Console.WriteLine($"\n-- TEXS ({size:N0} bytes, {size / 268} entries of 268) --");
    for (int i = 0; i < Math.Min(size / 268, 8); i++)
    {
        int p = start + i * 268;
        uint replaceableId = BitConverter.ToUInt32(b, p);
        string file = FixedStr(b, p + 4, 260);
        uint flags = BitConverter.ToUInt32(b, p + 264);
        Console.WriteLine($"  [{i}] replaceableId={replaceableId,-3} flags={flags}  '{file}'");
    }
}

static string FixedStr(byte[] b, int start, int len)
{
    int n = 0;
    while (n < len && start + n < b.Length && b[start + n] != 0) n++;
    return Encoding.UTF8.GetString(b, start, n);
}

static string Escape(string s) => new(s.Select(c => c is >= (char)32 and < (char)127 ? c : '.').ToArray());
