using System.Diagnostics;
using System.Text;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>
/// Runs the final .m3 serialisation through Blender + the m3studio add-on, headlessly.
/// </summary>
/// <remarks>
/// The viewer's own MD34 writer produces files that m3studio imports losslessly, but the SC2
/// editor has undocumented expectations that only m3studio's writer demonstrably satisfies
/// (every .m3 known to load in SC2 here came from it — the Diablo 3 pipeline included). So the
/// direct writer is the bridge <em>into</em> Blender, and m3studio is the serialiser
/// <em>out</em>: viewer .m3 → m3studio import → m3studio export → final .m3, plus a patch step
/// restoring the per-sequence bounds m3studio does not round-trip.
/// </remarks>
public static class BlenderM3Studio
{
    /// <summary>Full path to blender.exe, or null when not installed.</summary>
    public static string? FindBlender()
    {
        var candidates = new List<(Version Ver, string Path)>();
        foreach (string root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            string foundation = Path.Combine(root, "Blender Foundation");
            if (!Directory.Exists(foundation)) continue;
            foreach (string dir in Directory.EnumerateDirectories(foundation, "Blender*"))
            {
                string exe = Path.Combine(dir, "blender.exe");
                if (!File.Exists(exe)) continue;
                string tail = Path.GetFileName(dir).Replace("Blender", "").Trim();
                Version.TryParse(tail.Length > 0 ? tail : "0.0", out var ver);
                candidates.Add((ver ?? new Version(0, 0), exe));
            }
        }
        return candidates.OrderByDescending(c => c.Ver).Select(c => c.Path).FirstOrDefault();
    }

    /// <summary>The installed m3studio add-on folder (any Blender version), or null.</summary>
    public static string? FindM3Studio()
    {
        string roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Blender Foundation", "Blender");
        if (!Directory.Exists(roaming)) return null;
        return Directory.EnumerateDirectories(roaming)
            .OrderByDescending(Path.GetFileName)
            .Select(v => Path.Combine(v, "scripts", "addons", "m3studio-main"))
            .FirstOrDefault(Directory.Exists);
    }

    public static bool IsAvailable(out string? blender, out string? addon)
    {
        blender = FindBlender();
        addon = FindM3Studio();
        return blender is not null && addon is not null && ScriptPath() is not null;
    }

    private static string? ScriptPath()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Convert", "blender_m3studio_export.py");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Converts <paramref name="srcM3"/> (viewer-written) to <paramref name="dstM3"/>
    /// (m3studio-written). Returns success plus the tail of Blender's output for diagnostics.
    /// </summary>
    public static (bool Ok, string Log) Convert(string srcM3, string dstM3, TimeSpan? timeout = null)
    {
        string? blender = FindBlender();
        string? script = ScriptPath();
        if (blender is null) return (false, "Blender not found under Program Files\\Blender Foundation.");
        if (script is null) return (false, "blender_m3studio_export.py missing next to the application.");
        if (FindM3Studio() is null) return (false, "m3studio add-on (m3studio-main) not installed in Blender.");

        var psi = new ProcessStartInfo
        {
            FileName = blender,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--background");
        psi.ArgumentList.Add("--factory-startup");
        psi.ArgumentList.Add("--python");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(srcM3);
        psi.ArgumentList.Add(dstM3);

        var output = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        if (!proc.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(10)).TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (false, "Blender conversion timed out.\n" + Tail(output));
        }
        proc.WaitForExit();    // flush async readers

        string log = output.ToString();
        bool ok = proc.ExitCode == 0 && log.Contains("M3PIPELINE_OK") && File.Exists(dstM3);
        return (ok, ok ? ExtractMarkers(log) : Tail(output));
    }

    private static string Tail(StringBuilder sb)
    {
        string s = sb.ToString();
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join("\n", lines.TakeLast(12));
    }

    private static string ExtractMarkers(string log)
        => string.Join("  ", log.Split('\n')
            .Where(l => l.StartsWith("M3PIPELINE_", StringComparison.Ordinal))
            .Select(l => l.Trim()));
}
