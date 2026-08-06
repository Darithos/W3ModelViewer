using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Reports which texture container formats Warcraft III actually ships, so the decoders written are
/// the ones that are needed. Resolves every texture referenced by a set of models and dumps the DDS
/// pixel format / BLP compression of each.
/// </summary>
public static class TexProbe
{
    public static int Run(string install)
    {
        using var storage = new Wc3Storage(install);

        (string Label, string Name)[] models =
        [
            ("SD knight", @"war3.w3mod:units\human\knight\knight.mdx"),
            ("HD knight", @"war3.w3mod:_hd.w3mod:units\human\knight\knight.mdx"),
            ("SD grunt", @"war3.w3mod:units\orc\grunt\grunt.mdx"),
            ("HD grunt", @"war3.w3mod:_hd.w3mod:units\orc\grunt\grunt.mdx"),
            ("HD townhall", @"war3.w3mod:_hd.w3mod:buildings\human\townhall\townhall.mdx"),
            ("SD archer", @"war3.w3mod:units\nightelf\archer\archer.mdx"),
        ];

        var formatCensus = new Dictionary<string, int>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var (label, name) in models)
        {
            var bytes = storage.TryReadFile(name);
            if (bytes is null) { Console.WriteLine($"\n### {label}: not found"); continue; }
            var model = MdxReader.Read(bytes);

            Console.WriteLine($"\n### {label} — {model.Textures.Count} texture refs");
            string prefix = ArchivePrefix(name);

            foreach (var (tex, i) in model.Textures.Select((t, i) => (t, i)))
            {
                if (tex.IsReplaceable && tex.FileName.Length == 0)
                {
                    Console.WriteLine($"  [{i,2}] <replaceable {tex.ReplaceableId}>  (engine-supplied)");
                    continue;
                }

                var (resolved, data) = Resolve(storage, prefix, tex.FileName);
                if (data is null)
                {
                    Console.WriteLine($"  [{i,2}] UNRESOLVED  '{tex.FileName}'");
                    unresolved.Add($"{label}: {tex.FileName}");
                    continue;
                }

                string desc = Describe(data);
                formatCensus[desc] = formatCensus.GetValueOrDefault(desc) + 1;
                Console.WriteLine($"  [{i,2}] {desc,-34} {data.Length,9:N0} B  {resolved}");
            }
        }

        Console.WriteLine("\n==== container/format census ====");
        foreach (var kv in formatCensus.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Value,4}x  {kv.Key}");
        if (unresolved.Count > 0)
        {
            Console.WriteLine($"\n==== {unresolved.Count} unresolved references ====");
            foreach (string u in unresolved.Take(20)) Console.WriteLine($"  {u}");
        }
        return 0;
    }

    /// <summary>Everything up to and including the final colon: the archive chain the model came from.</summary>
    private static string ArchivePrefix(string cascName)
    {
        int i = cascName.LastIndexOf(':');
        return i >= 0 ? cascName[..(i + 1)] : "";
    }

    /// <summary>
    /// Model texture paths do not match what the storage holds: HD models reference <c>.tif</c> with
    /// forward slashes while the CASC stores <c>.dds</c>. Try the plausible spellings.
    /// </summary>
    private static (string Resolved, byte[]? Data) Resolve(Wc3Storage storage, string prefix, string fileName)
    {
        string norm = fileName.Replace('/', '\\');
        string noExt = Path.ChangeExtension(norm, null) ?? norm;

        IEnumerable<string> candidates =
        [
            prefix + norm,
            prefix + noExt + ".dds",
            prefix + noExt + ".blp",
            "war3.w3mod:_hd.w3mod:" + noExt + ".dds",
            "war3.w3mod:" + norm,
            "war3.w3mod:" + noExt + ".blp",
            "war3.w3mod:" + noExt + ".dds",
        ];

        foreach (string c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var d = storage.TryReadFile(c);
            if (d is not null && d.Length > 4) return (c, d);
        }
        return (fileName, null);
    }

    /// <summary>Identifies the container and, for DDS, the exact pixel format.</summary>
    private static string Describe(byte[] d)
    {
        string magic = Encoding.ASCII.GetString(d, 0, 4);
        switch (magic)
        {
            case "BLP1":
            {
                uint compression = BitConverter.ToUInt32(d, 4);
                uint alphaBits = BitConverter.ToUInt32(d, 8);
                string kind = compression switch { 0 => "JPEG", 1 => "palettised", _ => $"mode{compression}" };
                return $"BLP1 {kind} alpha{alphaBits}";
            }
            case "BLP2":
            {
                uint type = BitConverter.ToUInt32(d, 4);
                byte encoding = d[8], alphaDepth = d[9], alphaEncoding = d[10];
                string kind = encoding switch { 1 => "palettised", 2 => "DXT", 3 => "raw BGRA", _ => $"enc{encoding}" };
                return $"BLP2 {kind} alpha{alphaDepth}/{alphaEncoding} type{type}";
            }
            case "DDS ":
            {
                uint pfFlags = BitConverter.ToUInt32(d, 80);
                string fourCc = Encoding.ASCII.GetString(d, 84, 4);
                uint rgbBits = BitConverter.ToUInt32(d, 88);
                if ((pfFlags & 0x4) != 0)   // DDPF_FOURCC
                {
                    if (fourCc != "DX10") return $"DDS {fourCc}";
                    uint dxgi = BitConverter.ToUInt32(d, 128);
                    return $"DDS DX10 dxgi={DxgiName(dxgi)}";
                }
                return $"DDS uncompressed {rgbBits}bpp";
            }
            default:
                return $"unknown '{new string(magic.Select(c => char.IsControl(c) ? '.' : c).ToArray())}'";
        }
    }

    private static string DxgiName(uint f) => f switch
    {
        71 => "BC1_UNORM", 72 => "BC1_UNORM_SRGB",
        74 => "BC2_UNORM", 75 => "BC2_UNORM_SRGB",
        77 => "BC3_UNORM", 78 => "BC3_UNORM_SRGB",
        80 => "BC4_UNORM", 81 => "BC4_SNORM",
        83 => "BC5_UNORM", 84 => "BC5_SNORM",
        98 => "BC7_UNORM", 99 => "BC7_UNORM_SRGB",
        28 => "R8G8B8A8_UNORM", 87 => "B8G8R8A8_UNORM",
        _ => $"dxgi{f}",
    };
}
