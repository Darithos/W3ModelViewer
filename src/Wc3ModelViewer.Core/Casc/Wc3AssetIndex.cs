namespace Wc3ModelViewer.Core.Casc;

/// <summary>Which art set a Warcraft III asset belongs to.</summary>
public enum Wc3ArtSet
{
    /// <summary>Classic/SD art: MDX v800 models and BLP textures.</summary>
    Classic,
    /// <summary>Reforged/HD art (the <c>_hd.w3mod</c> tree): MDX v900+ models and DDS textures.</summary>
    Reforged,
}

/// <summary>One model file found in the storage, with its SD/HD counterpart paired up.</summary>
public sealed class Wc3ModelEntry
{
    /// <summary>The name to pass to <see cref="Wc3Storage.ReadFile"/> — CascLib's own spelling.</summary>
    public required string CascName { get; init; }

    /// <summary>Path with the mod-archive prefixes stripped, e.g. <c>units\human\knight\knight.mdx</c>.</summary>
    public required string RelativePath { get; init; }

    public required Wc3ArtSet ArtSet { get; init; }

    /// <summary>
    /// Other CASC names providing this same relative path — add-on archives (<c>hd2.w3addon</c> and
    /// friends) republish base-game assets, so one model can be reachable under several prefixes.
    /// </summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>File name without directory or extension, e.g. <c>knight</c>.</summary>
    public string Name => Path.GetFileNameWithoutExtension(RelativePath);

    /// <summary>Directory part of <see cref="RelativePath"/>, used as the browser's tree grouping.</summary>
    public string Folder => Path.GetDirectoryName(RelativePath)?.Replace('/', '\\') ?? "";

    /// <summary>True for the small <c>*_portrait.mdx</c> companion models that carry the portrait camera.</summary>
    public bool IsPortrait => Name.EndsWith("_portrait", StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{RelativePath} ({ArtSet})";
}

/// <summary>
/// The browsable catalog built from one full CASC enumeration.
/// </summary>
/// <remarks>
/// Warcraft III's storage reports each name with the chain of mod archives that produced it, colon
/// separated, exactly as CascView displays it:
/// <c>war3.w3mod:_hd.w3mod:units\human\knight\knight.mdx</c>. Add-on content nests deeper still —
/// <c>_addons\hd2.w3addon\136env.w3mod:_hd.w3mod:_tilesets\a.w3mod:replaceabletextures\...</c> — so
/// rather than enumerate archive names, the asset path is taken as everything after the <b>last</b>
/// colon, and the art set from whether <c>_hd.w3mod</c> appears in the prefix that was dropped.
/// </remarks>
public sealed class Wc3AssetIndex
{
    public required IReadOnlyList<string> AllNames { get; init; }
    public required IReadOnlyList<Wc3ModelEntry> Models { get; init; }
    public required IReadOnlyList<string> Textures { get; init; }

    /// <summary>Every relative texture path, mapped to the CASC names that provide it (SD and HD).</summary>
    public required IReadOnlyDictionary<string, List<string>> TextureLookup { get; init; }

    private Dictionary<string, List<string>>? _byFileName;

    /// <summary>
    /// Every texture file name, mapped to the CASC names that provide it under any folder — the last
    /// resort for a custom model whose author's tools rewrote the path but kept the name.
    /// </summary>
    /// <remarks>
    /// Built on first use and kept here rather than in <see cref="Wc3TextureCache"/>: it is derived
    /// purely from the catalog, and a per-cache copy would rebuild it for every model opened.
    /// </remarks>
    public IReadOnlyDictionary<string, List<string>> TextureLookupByFileName
    {
        get
        {
            if (_byFileName is not null) return _byFileName;
            var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (relative, providers) in TextureLookup)
            {
                int slash = relative.LastIndexOfAny(['\\', '/']);
                string leaf = slash < 0 ? relative : relative[(slash + 1)..];
                if (leaf.Length == 0) continue;
                if (!byName.TryGetValue(leaf, out var list)) byName[leaf] = list = [];
                list.AddRange(providers);
            }
            return _byFileName = byName;
        }
    }

    public static Wc3AssetIndex FromNames(IReadOnlyList<string> names)
    {
        var modelNames = new List<(string CascName, string Relative, Wc3ArtSet ArtSet)>();
        var textures = new List<string>();
        var textureLookup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
        {
            string ext = Path.GetExtension(name);
            bool isModel = ext.Equals(".mdx", StringComparison.OrdinalIgnoreCase)
                        || ext.Equals(".mdl", StringComparison.OrdinalIgnoreCase);
            bool isTexture = ext.Equals(".blp", StringComparison.OrdinalIgnoreCase)
                          || ext.Equals(".dds", StringComparison.OrdinalIgnoreCase);
            if (!isModel && !isTexture) continue;

            var (relative, artSet) = StripArchivePrefix(name);

            if (isModel)
            {
                if (relative.Length > 0) modelNames.Add((name, relative, artSet));
            }
            else
            {
                textures.Add(relative);
                if (!textureLookup.TryGetValue(relative, out var providers))
                    textureLookup[relative] = providers = [];
                providers.Add(name);
            }
        }

        // Collapse the add-on republishes: one entry per (path, art set), with the plain base-game
        // name as the primary and the rest kept as overrides. The base name is the one with the
        // fewest archive segments, which is also the spelling CascView shows.
        var models = modelNames
            .GroupBy(m => (m.Relative, m.ArtSet))
            .Select(g =>
            {
                var ordered = g.OrderBy(m => m.CascName.Count(c => c == ':'))
                               .ThenBy(m => m.CascName.Length)
                               .ToList();
                return new Wc3ModelEntry
                {
                    CascName = ordered[0].CascName,
                    RelativePath = g.Key.Relative,
                    ArtSet = g.Key.ArtSet,
                    Overrides = ordered.Skip(1).Select(m => m.CascName).ToList(),
                };
            })
            .OrderBy(m => m.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.ArtSet)
            .ToList();

        return new Wc3AssetIndex
        {
            AllNames = names,
            Models = models,
            Textures = textures,
            TextureLookup = textureLookup,
        };
    }

    /// <summary>
    /// Splits a raw CASC name into the asset path and the art set its archive prefix implies.
    /// The asset path is everything after the last colon; a name with no colon is returned as-is.
    /// </summary>
    public static (string Relative, Wc3ArtSet ArtSet) StripArchivePrefix(string cascName)
    {
        int lastColon = cascName.LastIndexOf(':');
        string prefix = lastColon >= 0 ? cascName[..lastColon] : "";
        string relative = (lastColon >= 0 ? cascName[(lastColon + 1)..] : cascName).Replace('/', '\\');

        var artSet = prefix.Contains("_hd.w3mod", StringComparison.OrdinalIgnoreCase)
            ? Wc3ArtSet.Reforged
            : Wc3ArtSet.Classic;

        return (relative, artSet);
    }
}
