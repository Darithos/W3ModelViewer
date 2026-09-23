using System.Globalization;
using System.IO;
using System.Text;

namespace Wc3ModelViewer;

/// <summary>
/// The handful of choices the app remembers between runs, as a flat <c>key=value</c> file in the
/// user's roaming profile.
/// </summary>
/// <remarks>
/// This started as a single file holding nothing but the last install path. Porting models in bulk
/// made the export dialog's own settings worth keeping too — retyping the scale for every model was
/// the most-reported annoyance — so the store became general. The old <c>install-path.txt</c> is
/// still read once, so upgrading does not forget the install.
/// <para>
/// Only choices that mean the same thing for the next model are kept. LOD, the sequence list and
/// the visible-geoset selection are properties of the model in front of you, not preferences, and
/// restoring them onto a different model would silently drop geometry.
/// </para>
/// </remarks>
public static class UserSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "W3ModelViewer");

    private static string File_ => Path.Combine(Dir, "settings.txt");
    private static string LegacyInstallFile => Path.Combine(Dir, "install-path.txt");

    private static readonly Dictionary<string, string> Values = Load();
    private static bool _migrated;

    /// <summary>Writes the migrated install path out once, so the old file stops being the only copy.</summary>
    static UserSettings()
    {
        if (_migrated) Save();
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (System.IO.File.Exists(File_))
            {
                foreach (string line in System.IO.File.ReadAllLines(File_))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }
            }
            // One-time migration: the install path used to live in a file of its own.
            if (!map.ContainsKey("install") && System.IO.File.Exists(LegacyInstallFile))
            {
                string old = System.IO.File.ReadAllText(LegacyInstallFile).Trim();
                if (old.Length > 0) { map["install"] = old; _migrated = true; }
            }
        }
        catch (IOException) { /* remembering is a convenience; defaults are always valid */ }
        catch (UnauthorizedAccessException) { }
        return map;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var sb = new StringBuilder();
            foreach (var (k, v) in Values.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(k).Append('=').Append(v).Append('\n');
            System.IO.File.WriteAllText(File_, sb.ToString());
        }
        catch (IOException) { /* not being able to remember is not worth interrupting the user for */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The stored string for <paramref name="key"/>, or <paramref name="fallback"/>.</summary>
    public static string Get(string key, string fallback = "") =>
        Values.TryGetValue(key, out string? v) && v.Length > 0 ? v : fallback;

    public static int GetInt(string key, int fallback) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    public static bool GetBool(string key, bool fallback) =>
        Get(key) is "1" or "true" ? true : Get(key) is "0" or "false" ? false : fallback;

    /// <summary>Invariant culture on purpose: a scale written on a German machine must read back
    /// the same everywhere, and the export dialog parses the box invariantly too.</summary>
    public static float GetFloat(string key, float fallback) =>
        float.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
        && float.IsFinite(v) ? v : fallback;

    public static void Set(string key, string value)
    {
        string clean = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (Values.TryGetValue(key, out string? had) && had == clean) return;
        Values[key] = clean;
        Save();
    }

    public static void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));
    public static void Set(string key, bool value) => Set(key, value ? "1" : "0");
    public static void Set(string key, float value) => Set(key, value.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>Stores several keys and writes the file once.</summary>
    public static void SetMany(params (string Key, string Value)[] pairs)
    {
        bool changed = false;
        foreach (var (k, v) in pairs)
        {
            string clean = (v ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (Values.TryGetValue(k, out string? had) && had == clean) continue;
            Values[k] = clean;
            changed = true;
        }
        if (changed) Save();
    }
}
