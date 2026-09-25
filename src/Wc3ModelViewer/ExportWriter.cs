using System.IO;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer;

/// <summary>Where an export's files land on disk.</summary>
public enum ExportLayout
{
    /// <summary><c>&lt;Out&gt;\&lt;Name&gt;\Assets\&lt;Name&gt;.m3</c> — one self-contained package per model.</summary>
    FolderPerModel,

    /// <summary>
    /// <c>&lt;Out&gt;\Assets\&lt;Name&gt;.m3</c> and one <c>&lt;Out&gt;\Assets\textures\</c> for every
    /// model — a collection that is one folder to merge. Asked for by a user porting fifty items,
    /// who otherwise dragged fifty texture folders together and answered a replace prompt for
    /// every texture the models share.
    /// </summary>
    SharedAssets,
}

/// <summary>
/// Converts one loaded model and writes it out in the chosen <see cref="ExportLayout"/>. Shared by
/// the single-model export and the batch export, so both write the same package.
/// </summary>
public static class ExportWriter
{
    public sealed record Result(int Files, int TexturesReused, long Bytes, string Location, string Note, bool Warned);

    public static Result Write(MdxModel model, string cascName, Wc3TextureCache textures, M3ExportOptions options,
                               string outDir, bool doGltf, bool doM3, ExportLayout layout)
    {
        // Per model: <Out>\<Name>\ holds the glTF set at the top and the m3 package under an
        // Assets\ folder that mirrors the paths baked into the .m3. Shared: the Assets\ folder is
        // the output root itself, and glTF sets — whose texture names are not hashed — keep a
        // folder per model beside it.
        string modelDir = Path.Combine(outDir, options.ModelName);
        string gltfDir = layout == ExportLayout.SharedAssets ? Path.Combine(outDir, "glTF", options.ModelName) : modelDir;
        string assetsDir = Path.Combine(layout == ExportLayout.SharedAssets ? outDir : modelDir, "Assets");
        int count = 0, reused = 0;
        long bytes = 0;
        string note = "";
        bool warned = false;

        if (doGltf)
        {
            Directory.CreateDirectory(gltfDir);
            var result = new GltfExporter(model, options).Export(textures, cascName);
            foreach (var file in result.Files)
            {
                File.WriteAllBytes(Path.Combine(gltfDir, file.FileName), file.Data);
                bytes += file.Data.Length;
            }
            count += result.Files.Count;
        }
        if (doM3)
        {
            var result = new M3Exporter(model, options).Export(textures, cascName);
            // The .m3 and its textures go inside a real Assets\ folder mirroring the paths
            // baked into the file, so the package is one folder to merge rather than two
            // pieces to place correctly — see M3ExportOptions.TexturePrefix.
            Directory.CreateDirectory(assetsDir);
            string m3Path = Path.Combine(assetsDir, options.ModelName + ".m3");
            File.WriteAllBytes(m3Path, result.M3);
            bytes += result.M3.Length;
            string texDir = Path.Combine(assetsDir, options.TextureFolder);
            Directory.CreateDirectory(texDir);
            foreach (var tex in result.Textures)
            {
                // Names carry a hash of the bytes, so a file already there under the same name is
                // this texture, written by an earlier model in the same shared folder.
                string path = Path.Combine(texDir, tex.FileName);
                if (SameBytes(path, tex.Data)) { reused++; continue; }
                File.WriteAllBytes(path, tex.Data);
                bytes += tex.Data.Length;
            }
            count += 1 + result.Textures.Count - reused;

            // Resolve every path the written .m3 asks for against what is now on disk. SC2
            // draws a layer it cannot find as black rather than reporting anything, so a
            // broken reference would otherwise only show up as a shading bug in the editor.
            var refs = M3TextureAudit.Verify(m3Path);
            var missing = refs.Where(r => !r.Resolved).Select(r => r.Path).ToList();

            // That audit only proves each referenced file exists; a texture the converter
            // could not find was written as a magenta placeholder, which exists too. Both
            // are reported: they are different faults, and reporting only one lets an
            // export that has both look like it only has the second.
            var missingTex = textures.ProvenanceOf(model, cascName, options.TeamColor).Missing;

            var warnings = new List<string>();
            if (missing.Count > 0)
                warnings.Add($"{missing.Count} texture reference(s) do not resolve — "
                             + string.Join(", ", missing.Take(3)));
            if (missingTex.Count > 0)
                warnings.Add($"{missingTex.Count} texture(s) exported as magenta placeholders "
                             + "(not beside the model and not in the game install): "
                             + string.Join(", ", missingTex.Take(3))
                             + (missingTex.Count > 3 ? $" (+{missingTex.Count - 3} more)" : ""));
            warned = warnings.Count > 0;

            // The size figure is the point of the reduction option, so it is reported
            // where the user looks rather than left to a folder listing.
            string keys = result.KeyReduction is { KeysIn: > 0 } k
                ? $"{result.M3.Length / 1048576.0:0.0} MB .m3, {100.0 * k.Dropped / k.KeysIn:0}% of animation keys dropped" +
                  $" (within {k.MaxRotationDeg:0.00}° / {k.MaxVector / options.Scale:0.00} WC3 units of the full bake)"
                : $"{result.M3.Length / 1048576.0:0.0} MB .m3";
            note = keys + "   —   " + (warnings.Count == 0
                ? @"copy the Assets folder into your map/mod root (merge with the existing Assets\)"
                : "WARNING: " + string.Join("; ", warnings));
        }
        string location = doM3 ? assetsDir : gltfDir;
        return new Result(count, reused, bytes, location, note, warned);
    }

    /// <summary>
    /// The model name a batch writes a model under. Names repeat — the SD, HD and DE knight are all
    /// <c>knight</c>, and custom downloads are full of <c>model.mdx</c> — and in a shared folder the
    /// second would silently replace the first, so a name the batch has already used takes
    /// <paramref name="suffix"/> (the art set, or the model's folder), then a number.
    /// </summary>
    public static string UniqueName(string name, string suffix, ISet<string> used)
    {
        if (used.Add(name)) return name;
        string withSuffix = name + string.Concat(suffix.Split(Path.GetInvalidFileNameChars()));
        if (used.Add(withSuffix)) return withSuffix;
        for (int n = 2; ; n++)
            if (used.Add($"{withSuffix}{n}")) return $"{withSuffix}{n}";
    }

    private static bool SameBytes(string path, byte[] data)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != data.Length) return false;
        return File.ReadAllBytes(path).AsSpan().SequenceEqual(data);
    }
}
