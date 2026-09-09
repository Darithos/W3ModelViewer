using Wc3ModelViewer.Core.Casc;

namespace MdxProbe;

/// <summary>
/// Finds assets the install ships as BOTH <c>.blp</c> and <c>.dds</c>. A pair like that is ground
/// truth: the DDS is decoded by code with no guesswork in it, so it says exactly what the BLP's
/// JPEG is supposed to look like once decoded.
/// </summary>
public static class BlpPairs
{
    public static int Run(string install)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();

        var blps = index.AllNames.Where(n => n.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)).ToList();
        Console.WriteLine($"{blps.Count:N0} .blp names in the storage");

        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string n in index.AllNames)
            if (n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                byStem[Strip(n)] = n;

        int pairs = 0;
        foreach (string blp in blps)
        {
            if (!byStem.TryGetValue(Strip(blp), out string? dds)) continue;
            if (pairs++ < 25) Console.WriteLine($"  {blp}\n      <-> {dds}");
        }
        Console.WriteLine($"\n==== {pairs} blp/dds pairs ====");
        return 0;
    }

    private static string Strip(string cascName)
    {
        string rel = Wc3AssetIndex.StripArchivePrefix(cascName).Relative;
        int dot = rel.LastIndexOf('.');
        return dot > 0 ? rel[..dot] : rel;
    }
}
