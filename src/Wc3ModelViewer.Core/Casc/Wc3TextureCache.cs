using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Casc;

/// <summary>
/// Resolves model texture references to decoded images, hiding the storage's naming quirks:
/// TEXS paths use <c>.blp</c>/<c>.tif</c> extensions and mixed separators while the storage holds
/// only <c>.dds</c>, and HD assets live under a different archive prefix than the model implies.
/// Decoded images are cached — HD texture sets run to several MB each.
/// </summary>
/// <remarks>
/// Loose custom models resolve through <see cref="LocalRoots"/> first: their textures sit next to
/// the .mdx (as .blp or .dds), and only stock references (e.g. <c>Textures\gutz.blp</c>) fall
/// through to the CASC. The storage may be null when only local files are in play.
/// </remarks>
public sealed class Wc3TextureCache(Wc3Storage? storage)
{
    private readonly Dictionary<string, RgbaImage?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories probed before the CASC — the folder of an opened loose model.</summary>
    public List<string> LocalRoots { get; } = [];

    /// <summary>The classic in-game player colours, BGR — player 0 red first.</summary>
    private static readonly (byte B, byte G, byte R)[] TeamColors =
    [
        (18, 3, 255), (255, 66, 0), (183, 231, 27), (128, 0, 84),
        (0, 252, 255), (30, 46, 254), (0, 139, 32), (176, 92, 228),
    ];

    /// <summary>
    /// Resolves one texture reference from a model. Replaceable slots (team colour/glow) come back
    /// as generated solids; unresolvable paths come back null.
    /// </summary>
    /// <param name="modelCascName">The model's own CASC name — its archive prefix anchors relative lookups.</param>
    /// <param name="teamColor">Player slot for replaceable textures, 0-based.</param>
    public RgbaImage? Load(string modelCascName, MdxTexture texture, int teamColor = 0)
    {
        if (texture.IsTeamColor || texture.IsTeamGlow)
        {
            var (b, g, r) = TeamColors[Math.Clamp(teamColor, 0, TeamColors.Length - 1)];
            // Glow is additive in-game; a dimmed solid reads better than full brightness under WPF.
            return texture.IsTeamGlow
                ? RgbaImage.Solid(8, 8, (byte)(b / 3), (byte)(g / 3), (byte)(r / 3))
                : RgbaImage.Solid(8, 8, b, g, r);
        }
        if (texture.FileName.Length == 0) return null;

        string key = ArchivePrefix(modelCascName) + "|" + texture.FileName;
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var image = LoadFile(modelCascName, texture.FileName);
        _cache[key] = image;
        return image;
    }

    private RgbaImage? LoadFile(string modelCascName, string fileName)
    {
        // Local roots first: custom models carry their textures beside the .mdx.
        foreach (string root in LocalRoots)
        {
            foreach (string candidate in LocalCandidates(root, fileName))
            {
                if (!File.Exists(candidate)) continue;
                var img = DecodeBytes(File.ReadAllBytes(candidate));
                if (img is not null) return img;
            }
        }

        if (storage is null) return null;
        foreach (string candidate in Candidates(modelCascName, fileName))
        {
            var data = storage.TryReadFile(candidate);
            if (data is null || data.Length < 128) continue;
            var img = DecodeBytes(data);
            if (img is not null) return img;
        }
        return null;
    }

    private static RgbaImage? DecodeBytes(byte[] data)
    {
        try
        {
            if (DdsReader.LooksLikeDds(data)) return DdsReader.Decode(data);
            if (BlpReader.LooksLikeBlp(data)) return BlpReader.Decode(data);
        }
        catch (NotSupportedException)
        {
            // An exotic pixel format is a per-file condition; the caller keeps trying spellings.
        }
        return null;
    }

    /// <summary>
    /// Spellings a TEXS path may have on disk next to a custom model: the exact relative path, the
    /// bare file name, and both again with the extension swapped between .blp and .dds.
    /// </summary>
    private static IEnumerable<string> LocalCandidates(string root, string fileName)
    {
        string norm = fileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string bare = Path.GetFileName(norm);
        string[] relatives = norm == bare ? [bare] : [norm, bare];
        foreach (string rel in relatives)
        {
            string full = Path.Combine(root, rel);
            yield return full;
            yield return Path.ChangeExtension(full, ".blp");
            yield return Path.ChangeExtension(full, ".dds");
        }
    }

    /// <summary>
    /// The storage spellings a TEXS path might resolve to, most specific first: the model's own
    /// archive, then the HD tree, then the classic tree — always trying <c>.dds</c> beside the
    /// declared extension, because that is what the storage actually holds.
    /// </summary>
    private static IEnumerable<string> Candidates(string modelCascName, string fileName)
    {
        string norm = fileName.Replace('/', '\\');
        string noExt = norm.LastIndexOf('.') is var dot && dot > 0 ? norm[..dot] : norm;
        string prefix = ArchivePrefix(modelCascName);

        // A loose model has no archive prefix; stock references try the classic tree first.
        string[] prefixes = prefix.Length == 0
            ? ["war3.w3mod:", "war3.w3mod:_hd.w3mod:"]
            : prefix != "war3.w3mod:" && prefix != "war3.w3mod:_hd.w3mod:"
                ? [prefix, "war3.w3mod:_hd.w3mod:", "war3.w3mod:"]
                : [prefix, prefix == "war3.w3mod:_hd.w3mod:" ? "war3.w3mod:" : "war3.w3mod:_hd.w3mod:"];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string p in prefixes)
        {
            if (p.Length == 0) continue;
            foreach (string c in new[] { p + noExt + ".dds", p + norm })
                if (seen.Add(c)) yield return c;
        }
    }

    private static string ArchivePrefix(string cascName)
    {
        int i = cascName.LastIndexOf(':');
        return i >= 0 ? cascName[..(i + 1)] : "";
    }
}
