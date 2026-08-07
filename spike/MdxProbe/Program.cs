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

// --validate [n] parses many real models and asserts the results are self-consistent.
if (args.Contains("--validate"))
{
    int limit = args.Select((a, i) => (a, i)).Where(t => t.a == "--validate")
                    .Select(t => t.i + 1 < args.Length && int.TryParse(args[t.i + 1], out int n) ? n : 60)
                    .First();
    return MdxProbe.Validate.Run(install, limit);
}

// --tex resolves and identifies every texture a set of models references.
if (args.Contains("--tex")) return MdxProbe.TexProbe.Run(install);

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

// --layers <cascPath> dumps every material's layers: filter mode, shading flags, slot table and
// the diffuse alpha histogram — the evidence for what a layer's alpha channel actually means.
if (args.Contains("--layers"))
{
    int li = Array.IndexOf(args, "--layers");
    string path = args[li + 1];
    using var s = new Wc3Storage(install);
    var tc = new Wc3ModelViewer.Core.Casc.Wc3TextureCache(s);
    var raw = s.TryReadFile(path);
    if (raw is null) { Console.WriteLine("not found"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
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
            Console.WriteLine($"   filter={layer.FilterMode} shading={layer.ShadingFlags} pbr={layer.IsPbr} alphaTrack={(layer.AlphaTrack is not null ? "yes" : "no")} staticAlpha={layer.Alpha:0.###} teamColorMult={layer.TeamColorMultiplier:0.###}");
            Console.WriteLine($"   slots: {slots}");
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
                    Console.WriteLine($"   diffuse {img.Width}x{img.Height}: alpha zero={100.0 * zero / n:0}% partial={100.0 * low / n:0}% opaque={100.0 * full / n:0}%");
                }
            }
        }
    }
    return 0;
}

// --geosets <cascPath> lists every geoset with its LOD, size, material and GEOA visibility —
// the census that answers "is a geoset being dropped, or hidden, and why".
if (args.Contains("--geosets"))
{
    int gi = Array.IndexOf(args, "--geosets");
    string path = args[gi + 1];
    using var s = new Wc3Storage(install);
    var raw = s.TryReadFile(path);
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
    var raw = s.TryReadFile(path);
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
    Console.WriteLine(bad == 0
        ? "\n  every emitter field is within a sane range — layout confirmed"
        : $"\n  {bad:N0} implausible values:");
    foreach (string c in complaints) Console.WriteLine(c);
    return bad == 0 ? 0 : 1;

    static bool Sane(System.Numerics.Vector3 c) =>
        c.X is >= -0.01f and <= 1.01f && c.Y is >= -0.01f and <= 1.01f && c.Z is >= -0.01f and <= 1.01f;
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
    using var s = new Wc3Storage(install);
    var tex = new Wc3ModelViewer.Core.Casc.Wc3TextureCache(s);
    var raw = s.TryReadFile(cascPath);
    if (raw is null) { Console.WriteLine($"not found: {cascPath}"); return 1; }
    var mdl = Wc3ModelViewer.Core.Formats.MdxReader.Read(raw);
    var opts = new Wc3ModelViewer.Core.Convert.M3ExportOptions { Lod = 0, ModelName = mname, Scale = sc };
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
