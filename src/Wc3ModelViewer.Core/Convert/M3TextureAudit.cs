using System.Text;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>One texture path an .m3 asks the engine to load, and whether it is actually there.</summary>
public sealed record M3TextureReference(string Path, bool Resolved);

/// <summary>
/// Reads every texture path back out of a written <c>.m3</c> and checks each one resolves next to
/// the model.
/// </summary>
/// <remarks>
/// This parses the produced bytes rather than asking the exporter what it meant to write, so it
/// catches the class of defect that is invisible from the writer's side — a reference pointing at
/// the wrong section, a path assembled with the wrong prefix or separator, a texture the
/// conversion silently failed to emit. In StarCraft II an unresolved path is not an error: the
/// model loads with that layer black, which reads as a shading problem and sends you looking in
/// the wrong place.
/// </remarks>
public static class M3TextureAudit
{
    private const int LayrStride = 464;                 // LAYR V26, as written by M3Exporter

    /// <summary>Every non-empty bitmap path referenced by a LAYR, in section order, deduplicated.</summary>
    public static List<string> ReferencedPaths(byte[] m3)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (m3.Length < 12) return paths;

        uint indexOffset = BitConverter.ToUInt32(m3, 4);
        uint sectionCount = BitConverter.ToUInt32(m3, 8);
        if (indexOffset + (long)sectionCount * 16 > m3.Length) return paths;

        var tags = new string[sectionCount];
        var offsets = new uint[sectionCount];
        var counts = new uint[sectionCount];
        for (int i = 0; i < sectionCount; i++)
        {
            int e = (int)indexOffset + i * 16;
            uint tag = BitConverter.ToUInt32(m3, e);
            // M3Builder packs the tag as Tag[0]<<24 … Tag[3], so it reads back MSB-first.
            tags[i] = new string([(char)(tag >> 24), (char)((tag >> 16) & 0xFF),
                                  (char)((tag >> 8) & 0xFF), (char)(tag & 0xFF)]);
            offsets[i] = BitConverter.ToUInt32(m3, e + 4);
            counts[i] = BitConverter.ToUInt32(m3, e + 8);
        }

        for (int s = 0; s < sectionCount; s++)
        {
            if (tags[s] != "LAYR") continue;
            for (int n = 0; n < counts[s]; n++)
            {
                long entry = offsets[s] + (long)n * LayrStride;
                if (entry + 12 > m3.Length) break;

                // color_bitmap is the first Reference in the struct: { entries, index, flags }.
                uint entries = BitConverter.ToUInt32(m3, (int)entry + 4);
                uint index = BitConverter.ToUInt32(m3, (int)entry + 8);
                if (entries == 0 || index >= sectionCount || tags[index] != "CHAR") continue;

                long start = offsets[index];
                long len = Math.Min(entries, counts[index]);
                if (start + len > m3.Length) continue;

                string path = Encoding.UTF8.GetString(m3, (int)start, (int)len).TrimEnd('\0');
                if (path.Length > 0 && seen.Add(path)) paths.Add(path);
            }
        }
        return paths;
    }

    /// <summary>
    /// Resolves every referenced path against <paramref name="modelDirectory"/>. A reference
    /// under <c>Assets/</c> also resolves with that head stripped: the export folder holds what
    /// gets pasted INTO a map's <c>Assets\</c>, so on disk the same file sits one level higher
    /// than the archive path says (<see cref="M3ExportOptions.TextureFolder"/>).
    /// </summary>
    public static List<M3TextureReference> Verify(byte[] m3, string modelDirectory)
    {
        var result = new List<M3TextureReference>();
        foreach (string p in ReferencedPaths(m3))
        {
            string rel = p.Replace('/', Path.DirectorySeparatorChar);
            bool found = File.Exists(Path.Combine(modelDirectory, rel));
            const string head = "Assets/";
            if (!found && p.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                found = File.Exists(Path.Combine(modelDirectory,
                    p[head.Length..].Replace('/', Path.DirectorySeparatorChar)));
            result.Add(new M3TextureReference(p, found));
        }
        return result;
    }

    /// <summary>Verifies a written model in place. Empty result means nothing is referenced.</summary>
    public static List<M3TextureReference> Verify(string m3Path) =>
        Verify(File.ReadAllBytes(m3Path), Path.GetDirectoryName(Path.GetFullPath(m3Path)) ?? ".");

    /// <summary>
    /// A one-line summary for the export log — the whole point is that a clean export says so
    /// explicitly rather than leaving you to discover a black layer in the editor.
    /// </summary>
    public static string Summarize(IReadOnlyList<M3TextureReference> refs)
    {
        var missing = refs.Where(r => !r.Resolved).Select(r => r.Path).ToList();
        if (refs.Count == 0) return "no texture references in the .m3 — the model will render untextured";
        return missing.Count == 0
            ? $"all {refs.Count} texture references resolve on disk"
            : $"{missing.Count} of {refs.Count} texture references are missing: {string.Join(", ", missing.Take(6))}"
              + (missing.Count > 6 ? $" (+{missing.Count - 6} more)" : "");
    }
}
