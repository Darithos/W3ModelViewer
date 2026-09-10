using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Casc;

/// <summary>Where a resolved texture came from — the distinction a custom model's export turns on.</summary>
public enum TextureSource
{
    /// <summary>A file shipped with the model, found in one of the cache's local roots.</summary>
    BesideModel,
    /// <summary>A file found by name anywhere under a local root, the reference's folder being gone.</summary>
    BesideModelByName,
    /// <summary>A file the user picked by hand for this reference — beats every automatic match.</summary>
    ChosenByUser,
    /// <summary>The installed game, matched on the reference's own path.</summary>
    GameInstall,
    /// <summary>The installed game, matched on file name only because the path did not exist.</summary>
    GameInstallByName,
}

/// <summary>One resolved texture reference and the file that satisfied it.</summary>
/// <param name="Reference">The path as the model spells it.</param>
/// <param name="From">The local path or CASC name the pixels came from.</param>
/// <param name="Alternatives">
/// How many distinct archive assets could have answered this reference. Always 1 for an exact match;
/// above 1 only for a file-name match, where it is the size of the guess.
/// </param>
public readonly record struct TextureOrigin(string Reference, TextureSource Source, string From,
                                            int Alternatives = 1)
{
    /// <summary>True when the match rested on the file name and more than one file carries it.</summary>
    public bool IsAmbiguous => Source == TextureSource.GameInstallByName && Alternatives > 1;

    public override string ToString() => Source == TextureSource.GameInstallByName
        ? $"{Reference} <- {From} (by file name{(Alternatives > 1 ? $", {Alternatives} candidates" : "")})"
        : $"{Reference} <- {From}";
}

/// <summary>Where one model's textures came from, and which of its references found nothing.</summary>
/// <param name="Found">One entry per distinct reference that resolved.</param>
/// <param name="Missing">
/// References that resolved to nothing — neither beside the model nor anywhere in the game install.
/// These export as magenta placeholders; a custom model's author would have to supply them.
/// </param>
public readonly record struct TextureProvenance(IReadOnlyList<TextureOrigin> Found,
                                                IReadOnlyList<string> Missing)
{
    public int BesideModel => Found.Count(o => o.Source is TextureSource.BesideModel
                                                          or TextureSource.BesideModelByName);

    /// <summary>Textures the user pointed at explicitly.</summary>
    public int ChosenByUser => Found.Count(o => o.Source == TextureSource.ChosenByUser);

    /// <summary>Textures pulled out of the installed game — what a custom model borrows.</summary>
    public int FromGameInstall => Found.Count(o => o.Source is TextureSource.GameInstall
                                                            or TextureSource.GameInstallByName);

    /// <summary>Of those, the ones matched on file name because the reference's path did not exist.</summary>
    public IEnumerable<TextureOrigin> MatchedByName =>
        Found.Where(o => o.Source == TextureSource.GameInstallByName);
}

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
/// <para>
/// Stock references from a loose model cannot be resolved by guessing an archive prefix, because a
/// loose model has none to anchor to and custom authors spell paths however their editor did. So
/// when an <see cref="Wc3AssetIndex"/> is supplied, resolution falls back to looking the reference
/// up in the storage's own catalog of texture paths — by full relative path, then by file name
/// alone. That is what lets a downloaded model that references <c>Textures\gutz.blp</c> export with
/// the real Warcraft III texture packaged beside it.
/// </para>
/// </remarks>
public sealed class Wc3TextureCache(Wc3Storage? storage, Wc3AssetIndex? index = null)
{
    private readonly Dictionary<string, RgbaImage?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where each cached reference came from, for the export log — see <see cref="OriginOf"/>.</summary>
    private readonly Dictionary<string, TextureOrigin> _origins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories probed before the CASC — the folder of an opened loose model.</summary>
    public List<string> LocalRoots { get; } = [];

    /// <summary>
    /// Files the user picked for a reference, keyed by the reference exactly as the model spells it.
    /// Set through <see cref="SetOverride"/> so the decoded image is dropped with it.
    /// </summary>
    private readonly Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The user's current choices, for saving and for showing in the UI.</summary>
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>
    /// Points a reference at a specific file, or clears the choice when <paramref name="filePath"/>
    /// is null. Every cached decode of that reference is discarded, so the next
    /// <see cref="Load"/> re-resolves and the viewer redraws with the new art.
    /// </summary>
    public void SetOverride(string reference, string? filePath)
    {
        if (filePath is null) _overrides.Remove(reference);
        else _overrides[reference] = filePath;

        // Cache keys carry the model's archive prefix, so one reference can hold several entries.
        foreach (string key in _cache.Keys.Where(k => k.EndsWith("|" + reference, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _cache.Remove(key);
            _origins.Remove(key);
        }
    }

    /// <summary>Forgets every user choice, and the images they produced.</summary>
    public void ClearOverrides()
    {
        foreach (string reference in _overrides.Keys.ToList()) SetOverride(reference, null);
    }

    /// <summary>
    /// Prefer the Reforged tree when the catalog offers a reference under both art sets. Set for an
    /// HD model; SD and HD publish colliding paths (<c>textures\gutz.blp</c>) with different art.
    /// </summary>
    public bool PreferHd { get; set; }

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
            int slot = Math.Clamp(teamColor, 0, TeamColors.Length - 1);
            var (b, g, r) = TeamColors[slot];
            if (!texture.IsTeamGlow) return RgbaImage.Solid(8, 8, b, g, r);

            // The game ships the real glow art and it is nothing like a guess: 32x32, a radial
            // falloff in the player's colour that peaks at 123/255, not 255. Reading it keeps the
            // preview and the export honest about how bright a team glow actually is — and gives
            // the exporter the falloff to hand StarCraft II. The synthetic fallback below still
            // covers a loose model opened without the game install behind it.
            string glowRef = $@"ReplaceableTextures\TeamGlow\TeamGlow{slot:00}.blp";
            string glowKey = CacheKey(modelCascName, new MdxTexture { FileName = glowRef });
            if (!_cache.TryGetValue(glowKey, out var glow))
            {
                glow = LoadFile(modelCascName, glowRef, glowKey);
                _cache[glowKey] = glow;
            }
            return glow ?? TeamGlow(b, g, r);
        }
        if (texture.FileName.Length == 0) return null;

        string key = CacheKey(modelCascName, texture);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var image = LoadFile(modelCascName, texture.FileName, key);
        _cache[key] = image;
        return image;
    }

    /// <summary>
    /// The team-glow slot, as the radial falloff the game's own <c>ReplaceableTextures\TeamGlow</c>
    /// art is: full team colour at the centre, black at the rim. It is always drawn additively, so
    /// the black rim adds nothing and the quad reads as a soft round glow.
    /// </summary>
    /// <remarks>
    /// Generating a flat solid here instead turned every glow card into a solid coloured rectangle
    /// the size of its whole quad — the most visible artefact on any custom hero, because authors
    /// use this slot for auras, runes and weapon glows, and a flat fill has no shape to fall off.
    /// </remarks>
    private static RgbaImage TeamGlow(byte b, byte g, byte r, int size = 64)
    {
        var px = new byte[size * size * 4];
        float centre = (size - 1) / 2f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - centre) / centre, dy = (y - centre) / centre;
                float falloff = Math.Clamp(1 - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                falloff *= falloff;                     // squared: a bright core and a soft rim
                int o = (y * size + x) * 4;
                px[o] = (byte)(b * falloff);
                px[o + 1] = (byte)(g * falloff);
                px[o + 2] = (byte)(r * falloff);
                px[o + 3] = 255;                        // additive draw takes brightness as coverage
            }
        return new RgbaImage { Width = size, Height = size, Pixels = px };
    }

    /// <summary>Where a previously <see cref="Load"/>ed reference came from, or null if never resolved.</summary>
    public TextureOrigin? OriginOf(string modelCascName, MdxTexture texture)
        => _origins.TryGetValue(CacheKey(modelCascName, texture), out var o) ? o : null;

    /// <summary>
    /// Where one model's textures came from, and which of its references found nothing.
    /// </summary>
    /// <remarks>
    /// Scoped to the model rather than read off the whole cache: the browser reuses one cache for
    /// every model viewed in a session, so cache-wide totals would credit an export with textures
    /// belonging to models the user merely clicked through earlier.
    /// </remarks>
    /// <param name="teamColor">Must match the value the textures were loaded with.</param>
    public TextureProvenance ProvenanceOf(MdxModel model, string modelCascName, int teamColor = 0)
    {
        var found = new List<TextureOrigin>();
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tex in model.Textures)
        {
            if (tex.IsTeamColor || tex.IsTeamGlow || tex.FileName.Length == 0) continue;
            if (!seen.Add(tex.FileName)) continue;

            // Resolve rather than only look up: a texture the exporter never touched (an unused
            // TEXS entry) still belongs in the report, and a resolved one costs nothing to re-ask.
            if (Load(modelCascName, tex, teamColor) is null) missing.Add(tex.FileName);
            else if (OriginOf(modelCascName, tex) is { } o) found.Add(o);
        }
        return new TextureProvenance(found, missing);
    }

    /// <summary>Every resolution this cache has made — for diagnostics over a single-model cache.</summary>
    public IReadOnlyCollection<TextureOrigin> Origins => _origins.Values;

    /// <summary>
    /// Archive folders this cache has resolved into exactly, used only to break ties between files
    /// sharing a name — see <see cref="Rank"/>. A cache reused across models accumulates every
    /// model's folders, which widens the tie-break rather than corrupting it: an exact match never
    /// consults this, so the worst case is a slightly better-informed guess about a different model.
    /// </summary>
    private readonly HashSet<string> _resolvedFolders = new(StringComparer.OrdinalIgnoreCase);

    private static string CacheKey(string modelCascName, MdxTexture texture)
        => ArchivePrefix(modelCascName) + "|" + texture.FileName;

    private RgbaImage? LoadFile(string modelCascName, string fileName, string key)
    {
        // A file the user picked wins outright: they are correcting something this code got wrong,
        // so no automatic match may override it.
        if (_overrides.TryGetValue(fileName, out string? chosen) && File.Exists(chosen))
        {
            var picked = DecodeBytes(File.ReadAllBytes(chosen));
            if (picked is not null) return Resolved(key, fileName, TextureSource.ChosenByUser, chosen, picked);
        }

        // Local roots next: custom models carry their textures beside the .mdx.
        foreach (string root in LocalRoots)
        {
            foreach (string candidate in LocalCandidates(root, fileName))
            {
                if (!File.Exists(candidate)) continue;
                var img = DecodeBytes(File.ReadAllBytes(candidate));
                if (img is not null) return Resolved(key, fileName, TextureSource.BesideModel, candidate, img);
            }
        }

        // Then by file name anywhere under those roots. Downloads arrive organised however their
        // author felt like — textures\, skins\, a folder per unit — while the reference still names
        // a path from the author's own disk, so the folder in the reference proves nothing and only
        // the file name survives.
        foreach (string match in LocalByName(fileName))
        {
            var img = DecodeBytes(File.ReadAllBytes(match));
            if (img is not null) return Resolved(key, fileName, TextureSource.BesideModelByName, match, img);
        }

        if (storage is not null)
        {
            // The prefix guess handles every model browsed out of the archive, and costs one open.
            foreach (string candidate in Candidates(modelCascName, fileName))
            {
                var img = TryDecodeFromStorage(candidate);
                if (img is not null) return Resolved(key, fileName, TextureSource.GameInstall, candidate, img);
            }

            // Then the catalog, which needs no prefix to be guessed right. This is the path a loose
            // custom model takes: it has no archive prefix of its own, and its author's spelling of
            // a stock path only has to agree with the storage on the file name.
            foreach (var (candidate, exact, alternatives) in IndexedCandidates(fileName))
            {
                var img = TryDecodeFromStorage(candidate);
                if (img is not null)
                    return Resolved(key, fileName,
                                    exact ? TextureSource.GameInstall : TextureSource.GameInstallByName,
                                    candidate, img, exact ? 1 : alternatives);
            }
        }

        return null;
    }

    private RgbaImage Resolved(string key, string reference, TextureSource source, string from,
                               RgbaImage image, int alternatives = 1)
    {
        // An exact hit tells us which archive folders this model actually draws from, which is the
        // best evidence available for the next reference whose own folder was destroyed. Only exact
        // hits count: seeding this from a guess would let one wrong guess justify the next.
        if (source == TextureSource.GameInstall)
        {
            string relative = Wc3AssetIndex.StripArchivePrefix(from).Relative;
            int slash = relative.LastIndexOf('\\');
            if (slash > 0) _resolvedFolders.Add(relative[..slash]);
        }

        _origins[key] = new TextureOrigin(reference, source, from, alternatives);
        return image;
    }

    private RgbaImage? TryDecodeFromStorage(string cascName)
    {
        var data = storage!.TryReadFile(cascName);
        return data is null || data.Length < 128 ? null : DecodeBytes(data);
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

    /// <summary>Every image file under each local root, indexed by bare file name. Built once.</summary>
    private Dictionary<string, List<string>>? _localIndex;

    /// <summary>
    /// A cap on the local sweep. The roots include the opened model's PARENT directory, which for a
    /// model sitting loose in a downloads folder can be enormous; a custom model's own package is
    /// a few dozen files, so a limit this size cannot cost a real one anything.
    /// </summary>
    private const int MaxLocalFiles = 20_000;

    /// <summary>
    /// Files under any local root whose name matches the reference's, ignoring the folders in it and
    /// treating <c>.blp</c>/<c>.dds</c>/<c>.tga</c> as interchangeable — authors re-save between them.
    /// </summary>
    private IEnumerable<string> LocalByName(string fileName)
    {
        _localIndex ??= BuildLocalIndex();
        int slash = fileName.LastIndexOfAny(['/', '\\']);
        string stem = Path.GetFileNameWithoutExtension(slash < 0 ? fileName : fileName[(slash + 1)..]);
        return _localIndex.TryGetValue(stem, out var matches) ? matches : [];
    }

    private Dictionary<string, List<string>> BuildLocalIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int budget = MaxLocalFiles;

        foreach (string root in LocalRoots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 8,
                });
            }
            catch (IOException) { continue; }

            foreach (string file in files)
            {
                if (budget-- <= 0) return index;
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".blp", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".dds", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".tga", StringComparison.OrdinalIgnoreCase)) continue;

                string stem = Path.GetFileNameWithoutExtension(file);
                if (!index.TryGetValue(stem, out var list)) index[stem] = list = [];
                if (!list.Contains(file, StringComparer.OrdinalIgnoreCase)) list.Add(file);
            }
        }
        return index;
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

    /// <summary>
    /// CASC names the catalog offers for a reference, best first, each flagged as an exact path
    /// match or a match on file name alone. Tried in widening order: the exact relative path, the
    /// same path with the extension swapped, then the file name under any folder.
    /// </summary>
    /// <remarks>
    /// The file-name pass is what makes custom models work. Their authors' tools rewrite texture
    /// paths freely — a stock <c>Textures\gutz.blp</c> comes back as <c>war3mapImported\gutz.blp</c>,
    /// as a bare <c>gutz.blp</c>, or as the absolute path it had on the author's desktop — while the
    /// file name itself survives. It is a weaker match, so it is reported as one: names are not
    /// unique across the archive, and the wrong <c>white.blp</c> is a plausible-looking mistake.
    /// </remarks>
    private IEnumerable<(string CascName, bool Exact, int Alternatives)> IndexedCandidates(string fileName)
    {
        if (index is null) yield break;

        // The catalog's keys have no leading separator, so a reference written "\Textures\x.blp"
        // must be trimmed before it can match exactly — otherwise it falls through to the weaker
        // file-name pass for no reason.
        string norm = fileName.Replace('/', '\\').TrimStart('\\');
        string noExt = norm.LastIndexOf('.') is var dot && dot > 0 ? norm[..dot] : norm;
        string leaf = SafeLeaf(norm);
        if (leaf.Length == 0) yield break;
        string leafNoExt = leaf.LastIndexOf('.') is var ldot && ldot > 0 ? leaf[..ldot] : leaf;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in new[] { norm, noExt + ".dds", noExt + ".blp" })
            if (index.TextureLookup.TryGetValue(path, out var providers))
                foreach (string name in Rank(providers, norm))
                    if (seen.Add(name)) yield return (name, true, 1);

        foreach (string name in new[] { leaf, leafNoExt + ".dds", leafNoExt + ".blp" })
            if (index.TextureLookupByFileName.TryGetValue(name, out var providers))
            {
                // How many *different* assets carry this file name — republishes of one path are not
                // a choice, two unrelated paths are. The winner is reported with this count so an
                // ambiguous guess can be seen as one.
                int distinct = providers
                    .Select(n => Wc3AssetIndex.StripArchivePrefix(n).Relative)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                foreach (string p in Rank(providers, norm))
                    if (seen.Add(p)) yield return (p, false, distinct);
            }
    }

    /// <summary>
    /// The file-name part of a reference, taking either separator. <see cref="Path.GetFileName(string)"/>
    /// is not used: a custom model's TEXS may hold characters Windows rejects in a path, and this
    /// must degrade to "no match" rather than throw during an export.
    /// </summary>
    private static string SafeLeaf(string norm)
    {
        int i = norm.LastIndexOfAny(['\\', '/', ':']);
        return i < 0 ? norm : norm[(i + 1)..];
    }

    /// <summary>
    /// Orders the archive files that could satisfy a reference, best first.
    /// </summary>
    /// <remarks>
    /// Art set decides first: SD and HD publish colliding paths with different art, and the wrong
    /// set is a visible mistake. Then agreement with the reference's own folders, so a surviving
    /// directory hint outweighs everything below it. Then a folder this same model already resolved
    /// into exactly — a model that borrows one texture from a folder usually borrows the rest from
    /// there too, and that is the only evidence left once an author's tool has flattened the path.
    /// Then <b>shallower paths win</b>: the textures custom models borrow live in the shared roots
    /// (<c>Textures\</c>, <c>ReplaceableTextures\</c>), while a deep path like
    /// <c>doodads\cinematic\lichking\</c> is one scene's own art that merely happens to share a file
    /// name. Base-game archives come before add-on republishes last, as in <see cref="Wc3AssetIndex"/>.
    /// </remarks>
    private IEnumerable<string> Rank(List<string> providers, string reference) => providers
        // Split each name once: the comparators below are called O(n log n) times, and a leaf like
        // "white.blp" can have hundreds of providers.
        .Select(n => (Name: n, Relative: Wc3AssetIndex.StripArchivePrefix(n).Relative))
        .OrderByDescending(p => IsHd(p.Name) == PreferHd)
        .ThenByDescending(p => SharedTrailingSegments(reference, p.Relative))
        .ThenByDescending(p => InResolvedFolder(p.Relative))
        .ThenBy(p => p.Relative.Count(c => c == '\\'))
        .ThenBy(p => p.Name.Count(c => c == ':'))
        .ThenBy(p => p.Name.Length)
        .Select(p => p.Name);

    private bool InResolvedFolder(string relative)
    {
        if (_resolvedFolders.Count == 0) return false;
        int slash = relative.LastIndexOf('\\');
        return slash > 0 && _resolvedFolders.Contains(relative[..slash]);
    }

    /// <summary>
    /// How many trailing path segments two references agree on, file name excluded. Counting from
    /// the tail is what makes a re-rooted path still match: an author's
    /// <c>C:\wip\Textures\Water\x.blp</c> agrees with the archive's <c>Textures\Water\x.dds</c> on
    /// two segments, while an unrelated file sharing the name agrees on none.
    /// </summary>
    private static int SharedTrailingSegments(string a, string b)
    {
        var da = a.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var db = b.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        // Start one back from the end of each: the file name is why they are being compared at all.
        for (int i = da.Length - 2, j = db.Length - 2; i >= 0 && j >= 0; i--, j--, n++)
            if (!da[i].Equals(db[j], StringComparison.OrdinalIgnoreCase)) break;
        return n;
    }

    private static bool IsHd(string cascName)
    {
        int lastColon = cascName.LastIndexOf(':');
        return lastColon >= 0
            && cascName.AsSpan(0, lastColon).Contains("_hd.w3mod", StringComparison.OrdinalIgnoreCase);
    }

    private static string ArchivePrefix(string cascName)
    {
        int i = cascName.LastIndexOf(':');
        return i >= 0 ? cascName[..(i + 1)] : "";
    }
}
