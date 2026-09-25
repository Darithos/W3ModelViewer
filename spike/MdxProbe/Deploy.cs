namespace MdxProbe;

/// <summary>
/// Where a test export goes so the StarCraft II editor can see it without any copying.
/// </summary>
/// <remarks>
/// The user asked (2026-09-25) for exports to land directly in the unpacked test map. That map's
/// layout is not the probe's: the editor wants every <c>.m3</c> flat in <c>Assets\</c> with its
/// textures in <c>Assets\Textures\</c>, where the probe otherwise writes
/// <c>&lt;outDir&gt;\&lt;name&gt;\&lt;name&gt;.m3</c> with its own <c>textures\</c> beside it.
/// <para>
/// The editor caches game data for the session, so a model it has already loaded needs the editor
/// restarted, not just reopened — which is why every test export still takes a new name.
/// </para>
/// </remarks>
internal static class Deploy
{
    /// <summary>The unpacked test map's asset folder.</summary>
    public const string MapAssets = @"C:\Games\StarCraft II\Maps\Test\modeltest.SC2Map\Assets";

    /// <summary>The texture subfolder the map uses, beside the models rather than under each one.</summary>
    public const string MapTextures = "Textures";

    /// <summary>
    /// True when <paramref name="outDir"/> is the map's asset folder, so the writer should lay the
    /// files out flat the way the editor expects.
    /// </summary>
    public static bool IsMapAssets(string outDir)
    {
        try
        {
            return string.Equals(Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar),
                                 Path.GetFullPath(MapAssets).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Resolves the directory an export's <c>.m3</c> goes in. <c>map</c>, or the Assets path
    /// itself, means the test map and the file goes flat beside the others; anything else keeps the
    /// probe's own <c>&lt;outDir&gt;\&lt;name&gt;\</c> layout. The default texture prefix
    /// (<c>Assets/textures/</c>) already resolves to the map's <c>Assets\Textures\</c> from the
    /// map root, so it needs no special case.
    /// </summary>
    public static string Resolve(string outDir, string modelName)
        => string.Equals(outDir, "map", StringComparison.OrdinalIgnoreCase) || IsMapAssets(outDir)
            ? MapAssets
            : Path.Combine(outDir, modelName);
}
