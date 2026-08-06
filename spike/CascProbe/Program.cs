using System.Diagnostics;
using System.Text;
using Wc3ModelViewer.Core.Casc;

// Probes a local Warcraft III install so the storage layer can be written against what CascLib
// ACTUALLY accepts, rather than against an assumed path spelling.
//
// Enumerating the whole storage turned out to be impractically slow, so the primary test is a direct
// open of known assets under every plausible spelling — that settles the question in milliseconds.
// Enumeration still runs afterwards, but time-capped and purely for reconnaissance.
//
//   CascProbe [installPath] [enumSeconds]

string install = args.Length > 0 ? args[0] : @"C:\games\Warcraft III";
int enumSeconds = args.Length > 1 && int.TryParse(args[1], out int es) ? es : 25;

Console.OutputEncoding = Encoding.UTF8;
var flush = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
Console.SetOut(flush);

// Utility modes for any CASC product (works on the StarCraft II storage too):
//   CascProbe <install> --find <substring> [max]     list matching file names
//   CascProbe <install> --extract <name> <outFile>   extract one file
if (args.Length >= 3 && args[1] == "--find")
{
    using var s = new Wc3Storage(install);
    int max = args.Length > 3 && int.TryParse(args[3], out int m) ? m : 40;
    string needle = args[2];
    int shown = 0;
    foreach (string name in s.EnumerateAll())
    {
        if (!name.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
        Console.WriteLine(name);
        if (++shown >= max) break;
    }
    Console.WriteLine($"({shown} shown)");
    return 0;
}
// --extractall <substring> <outDir> [max] pulls many matching files in one storage session.
if (args.Length >= 4 && args[1] == "--extractall")
{
    using var s = new Wc3Storage(install);
    string needle = args[2], outDir = args[3];
    int max = args.Length > 4 && int.TryParse(args[4], out int mm) ? mm : 30;
    Directory.CreateDirectory(outDir);
    int got = 0, seen = 0;
    foreach (string nm in s.EnumerateAll())
    {
        if (!nm.EndsWith(".m3", StringComparison.OrdinalIgnoreCase)) continue;
        if (!nm.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
        if (nm.Contains("death", StringComparison.OrdinalIgnoreCase)
            || nm.Contains("portrait", StringComparison.OrdinalIgnoreCase)
            || nm.Contains("placement", StringComparison.OrdinalIgnoreCase)
            || nm.Contains("hologram", StringComparison.OrdinalIgnoreCase)) continue;
        if (seen++ % 5 != 0) continue;                  // spread the sample
        var data = s.TryReadFile(nm);
        if (data is null || data.Length < 4096) continue;
        string leaf = Path.GetFileName(nm.Replace('\\', '/'));
        File.WriteAllBytes(Path.Combine(outDir, leaf), data);
        if (++got >= max) break;
    }
    Console.WriteLine($"extracted {got} files to {outDir}");
    return 0;
}

if (args.Length >= 4 && args[1] == "--extract")
{
    using var s = new Wc3Storage(install);
    var data = s.TryReadFile(args[2]);
    if (data is null) { Console.WriteLine("NOT FOUND"); return 1; }
    File.WriteAllBytes(args[3], data);
    Console.WriteLine($"extracted {data.Length:N0} bytes -> {args[3]}");
    return 0;
}

Console.WriteLine($"Opening CASC storage: {install}");
var sw = Stopwatch.StartNew();
using var storage = new Wc3Storage(install);
Console.WriteLine($"  opened in {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"  product          = {storage.GetInfoString(CascInfo.Product)}");
Console.WriteLine($"  local file count = {storage.GetInfoInt(CascInfo.LocalFileCount):N0}");
Console.WriteLine($"  total file count = {storage.GetInfoInt(CascInfo.TotalFileCount):N0}");

// ---- direct-open battery -------------------------------------------------------------------
// Assets that certainly exist in Reforged, in the spellings CascView / CascLib might expect.
string[] assets =
[
    @"units\human\knight\knight.mdx",
    @"units\human\footman\footman.mdx",
    @"ReplaceableTextures\TeamColor\TeamColor00.blp",
    @"textures\portrait_bg_diffuse.dds",
    @"replaceabletextures\environmentmap.dds",
];

string[] prefixes =
[
    "",
    "war3.w3mod:",
    "war3.w3mod:_hd.w3mod:",
    "war3.w3mod\\",
    "war3.w3mod\\_hd.w3mod\\",
    "_hd.w3mod:",
    "war3.w3mod:_locales:enus.w3mod:",
];

Console.WriteLine("\n--- direct CascOpenFile battery ---");
var working = new List<string>();
foreach (string asset in assets)
{
    foreach (string prefix in prefixes)
    {
        foreach (string variant in Variants(prefix + asset))
        {
            var data = storage.TryReadFile(variant);
            if (data is null) continue;
            Console.WriteLine($"  OK  {data.Length,10:N0} B   {variant}");
            working.Add(variant);
        }
    }
}
if (working.Count == 0)
    Console.WriteLine("  (nothing opened by name — the storage exposes no usable name namespace)");

// ---- time-capped enumeration --------------------------------------------------------------
Console.WriteLine($"\n--- enumeration (capped at {enumSeconds}s) ---");
sw.Restart();
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(enumSeconds));
var names = storage.EnumerateAll(
    new Progress<int>(n => Console.WriteLine($"  ... {n:N0} names @ {sw.ElapsedMilliseconds:N0} ms")),
    cts.Token);
Console.WriteLine($"  got {names.Count:N0} names in {sw.ElapsedMilliseconds:N0} ms" +
                  (cts.IsCancellationRequested ? " (cut short by the cap)" : " (walk finished on its own)"));

// Does the truncated catalog still contain the assets we care about?
foreach (string probe in new[] { @"knight.mdx", @"footman.mdx", @"_hd.w3mod:units\human\knight" })
    Console.WriteLine($"  names containing \"{probe}\": {names.Count(n => n.Contains(probe, StringComparison.OrdinalIgnoreCase)):N0}");

if (names.Count > 0)
{
    Console.WriteLine("\n--- 25 raw names verbatim ---");
    foreach (string n in names.Take(25)) Console.WriteLine($"  {n}");

    int withColon = names.Count(n => n.Contains(':'));
    int hexish = names.Count(n => n.Length is 16 or 32 && n.All(Uri.IsHexDigit));
    Console.WriteLine($"\n  contain ':' = {withColon:N0}    pure-hex (nameless) = {hexish:N0}");

    Console.WriteLine("\n--- leading segment census ---");
    foreach (var g in names.Select(n => { int i = n.IndexOfAny([':', '\\', '/']); return i < 0 ? "(none)" : n[..i]; })
                           .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                           .OrderByDescending(g => g.Count()).Take(15))
        Console.WriteLine($"  {g.Count(),9:N0}  {g.Key}");

    Console.WriteLine("\n--- extension census ---");
    foreach (var g in names.Select(n => Path.GetExtension(n).ToLowerInvariant())
                           .GroupBy(s => s).OrderByDescending(g => g.Count()).Take(12))
        Console.WriteLine($"  {g.Count(),9:N0}  {(g.Key.Length == 0 ? "(none)" : g.Key)}");

    // ---- the catalog the browser will actually be built on ----
    var index = Wc3AssetIndex.FromNames(names);
    Console.WriteLine($"\n--- asset index: {index.Models.Count:N0} models, {index.Textures.Count:N0} texture refs ---");
    foreach (var g in index.Models.GroupBy(m => m.ArtSet).OrderBy(g => g.Key))
        Console.WriteLine($"  {g.Key,-8} {g.Count(),6:N0} models ({g.Count(m => m.IsPortrait):N0} portraits)");

    Console.WriteLine("\n  top-level folders:");
    foreach (var g in index.Models.Select(m => m.Folder.Split('\\')[0])
                                  .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                                  .OrderByDescending(g => g.Count()).Take(10))
        Console.WriteLine($"    {g.Count(),6:N0}  {g.Key}");

    Console.WriteLine("\n  knight entries (prefix stripped):");
    foreach (var m in index.Models.Where(m => m.Name.Contains("knight", StringComparison.OrdinalIgnoreCase)).Take(8))
        Console.WriteLine($"    [{m.ArtSet,-8}] {m.RelativePath}");
}

// ---- chunk census of whatever models we managed to open -------------------------------------
foreach (string name in working.Where(w => w.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase)).Take(4))
{
    var bytes = storage.TryReadFile(name);
    if (bytes is null || bytes.Length < 12) continue;
    Console.WriteLine($"\n=== {name} ===");
    Console.WriteLine($"  {bytes.Length:N0} bytes, magic '{Encoding.ASCII.GetString(bytes, 0, 4)}'");
    DumpChunks(bytes);
}

return working.Count > 0 ? 0 : 2;

// Case and separator variants — CASC name lookup is normally case-insensitive, but prove it.
static IEnumerable<string> Variants(string path)
{
    yield return path;
    string lower = path.ToLowerInvariant();
    if (lower != path) yield return lower;
}

// Walks the top-level MDLX chunk list: char[4] tag + uint32 size (size EXCLUDES the 8-byte header).
static void DumpChunks(byte[] b)
{
    if (Encoding.ASCII.GetString(b, 0, 4) != "MDLX") { Console.WriteLine("  not MDLX"); return; }
    int p = 4;
    while (p + 8 <= b.Length)
    {
        string tag = Encoding.ASCII.GetString(b, p, 4);
        int size = BitConverter.ToInt32(b, p + 4);
        if (size < 0 || p + 8 + size > b.Length) { Console.WriteLine($"  {tag} size {size:N0} OVERRUNS — stopping"); break; }
        string extra = tag == "VERS" && size >= 4 ? $"   => VERSION {BitConverter.ToUInt32(b, p + 8)}" : "";
        Console.WriteLine($"  {tag}  {size,9:N0} bytes{extra}");
        p += 8 + size;
    }
}
