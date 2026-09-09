using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Texture-pipeline diagnostics: what a model references, what each reference resolved to, which
/// geosets are real cutouts, and whether an exported package satisfies its own baked paths.
/// </summary>
public static class TextureDiag
{
    public static int Run(string install, string filter, string outDir)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        Directory.CreateDirectory(outDir);

        var matches = index.Models
            .Where(m => m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) && !m.IsPortrait)
            .OrderBy(m => m.CascName)
            .ToList();

        Console.WriteLine($"{matches.Count} models matching '{filter}'\n");

        foreach (var entry in matches)
        {
            Console.WriteLine($"=== {entry.CascName}  [{entry.ArtSet}] ===");
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) { Console.WriteLine("  unreadable\n"); continue; }
            var model = MdxReader.Read(bytes);

            var textures = new Wc3TextureCache(storage, index) { PreferHd = entry.ArtSet == Wc3ArtSet.Reforged };

            Console.WriteLine($"  TEXS ({model.Textures.Count}):");
            for (int i = 0; i < model.Textures.Count; i++)
            {
                var t = model.Textures[i];
                if (t.IsReplaceable && t.FileName.Length == 0)
                {
                    Console.WriteLine($"    [{i,2}] <replaceable {t.ReplaceableId}>");
                    continue;
                }
                var img = textures.Load(entry.CascName, t);
                var origin = textures.OriginOf(entry.CascName, t);
                string state = img is null ? "UNRESOLVED"
                    : $"{img.Width}x{img.Height} {origin?.Source}"
                      + (origin is { Alternatives: > 1 } ? $" ({origin.Value.Alternatives} candidates)" : "");
                Console.WriteLine($"    [{i,2}] '{t.FileName}'");
                Console.WriteLine($"         -> {state}");
                if (origin is { } o && o.From != t.FileName) Console.WriteLine($"         from {o.From}");
            }

            Console.WriteLine($"  MTLS ({model.Materials.Count}):");
            for (int m = 0; m < model.Materials.Count; m++)
            {
                var mat = model.Materials[m];
                Console.WriteLine($"    mat[{m}] {mat.Layers.Count} layer(s)");
                foreach (var (layer, li) in mat.Layers.Select((l, i) => (l, i)))
                {
                    string slots = string.Join(", ", Enum.GetValues<MdxTextureSlot>()
                        .Select(s => (s, id: layer.Slot(s)))
                        .Where(x => x.id >= 0)
                        .Select(x => $"{x.s}={x.id}"));
                    Console.WriteLine($"      layer[{li}] filter={layer.FilterMode} pbr={layer.IsPbr} "
                                      + $"diffuseTexId={layer.DiffuseTextureId} shading={layer.ShadingFlags}");
                    Console.WriteLine($"        slots: {(slots.Length > 0 ? slots : "(none)")}");
                }

                var composite = MaterialCompositor.Compose(model, mat, textures, entry.CascName);
                bool magenta = composite.Texture.Width == 4 && composite.Texture.Height == 4;
                Console.WriteLine($"      => composite {composite.Texture.Width}x{composite.Texture.Height} "
                                  + $"blend={composite.Blend} primary='{composite.PrimaryTexturePath}'"
                                  + (magenta ? "  *** MAGENTA PLACEHOLDER ***" : ""));

                if (!magenta)
                {
                    string set = entry.ArtSet == Wc3ArtSet.Reforged ? "hd" : "sd";
                    string png = Path.Combine(outDir, $"{entry.Name}_{set}_mat{m}.png");
                    File.WriteAllBytes(png, PngWriter.Write(Downscale(composite.Texture, 256)));
                    File.WriteAllBytes(Path.Combine(outDir, $"{entry.Name}_{set}_mat{m}_ALPHA.png"),
                                       PngWriter.Write(AlphaAsGrey(Downscale(composite.Texture, 256))));
                }
            }

            Console.WriteLine($"  GEOS ({model.Geosets.Count}):");
            foreach (var g in model.Geosets)
            {
                Console.WriteLine($"    geoset[{g.Index}] lod={g.LodId} '{g.LodName}' mat={g.MaterialId} "
                                  + $"verts={g.Positions.Length} uvLayers={g.UvLayers.Count}");
                if (g.LodId != 0 || (uint)g.MaterialId >= (uint)model.Materials.Count) continue;

                var comp = MaterialCompositor.Compose(model, model.Materials[g.MaterialId], textures, entry.CascName);
                if (comp.Texture.Width <= 4) continue;
                float cut = MaterialCompositor.CutoutCoverage(comp.Texture, g);
                var (whiteFrac, lowAlpha) = FootprintStats(comp.Texture, g);
                Console.WriteLine($"      cutoutCoverage(a<128)={cut * 100:0.00}%  "
                                  + $"footprint a<32={lowAlpha * 100:0.00}%  near-white RGB={whiteFrac * 100:0.00}%");

                string set2 = entry.ArtSet == Wc3ArtSet.Reforged ? "hd" : "sd";
                File.WriteAllBytes(Path.Combine(outDir, $"{entry.Name}_{set2}_geo{g.Index}_FOOTPRINT.png"),
                                   PngWriter.Write(Downscale(FootprintOverlay(comp.Texture, g), 256)));
            }

            var prov = textures.ProvenanceOf(model, entry.CascName);
            Console.WriteLine($"  provenance: {prov.Found.Count} found, {prov.Missing.Count} missing");
            foreach (string miss in prov.Missing) Console.WriteLine($"    MISSING: {miss}");
            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>
    /// Exports matching models the way the app does, then audits the written package. Verifies the
    /// on-disk layout actually satisfies the paths baked into the .m3.
    /// </summary>
    public static int Pack(string install, string filter, string outDir)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();

        foreach (var entry in index.Models.Where(m =>
                     m.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     && !m.IsPortrait && m.ArtSet == Wc3ArtSet.Reforged))
        {
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            var model = MdxReader.Read(bytes);
            var textures = new Wc3TextureCache(storage, index) { PreferHd = true };
            var options = new Wc3ModelViewer.Core.Convert.M3ExportOptions { ModelName = entry.Name };
            var result = new Wc3ModelViewer.Core.Convert.M3Exporter(model, options).Export(textures, entry.CascName);

            string assetsDir = Path.Combine(outDir, entry.Name, "Assets");
            string texDir = Path.Combine(assetsDir, options.TextureFolder);
            Directory.CreateDirectory(texDir);
            string m3Path = Path.Combine(assetsDir, entry.Name + ".m3");
            File.WriteAllBytes(m3Path, result.M3);
            foreach (var tex in result.Textures)
                File.WriteAllBytes(Path.Combine(texDir, tex.FileName), tex.Data);

            var refs = Wc3ModelViewer.Core.Convert.M3TextureAudit.Verify(m3Path);
            Console.WriteLine($"{entry.Name}: {Wc3ModelViewer.Core.Convert.M3TextureAudit.Summarize(refs)}");

            // The real test of the layout: resolve each baked path against the package root the
            // way SC2 resolves it against a map root, with no Assets-stripping fallback.
            string root = Path.Combine(outDir, entry.Name);
            var unresolved = refs.Select(r => r.Path)
                .Where(p => !File.Exists(Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar))))
                .ToList();
            Console.WriteLine(unresolved.Count == 0
                ? "  as a map root: all references resolve"
                : $"  as a map root: {unresolved.Count} UNRESOLVED -> {string.Join(", ", unresolved)}");
        }
        return 0;
    }

    /// <summary>Marks every texel this geoset's UVs cover, by rasterising its triangles into the texture.</summary>
    private static bool[] Footprint(RgbaImage image, MdxGeoset geoset)
    {
        int w = image.Width, h = image.Height;
        var covered = new bool[w * h];
        var uvs = geoset.Uvs;
        var idx = geoset.Indices;
        if (uvs.Length < geoset.VertexCount) return covered;

        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
            if ((uint)i0 >= (uint)uvs.Length || (uint)i1 >= (uint)uvs.Length || (uint)i2 >= (uint)uvs.Length) continue;
            float ax = uvs[i0].X * w, ay = uvs[i0].Y * h;
            float bx = uvs[i1].X * w, by = uvs[i1].Y * h;
            float cx = uvs[i2].X * w, cy = uvs[i2].Y * h;
            float den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (MathF.Abs(den) < 1e-9f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))));
            int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))));
            int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            for (int y = y0; y <= y1; y++)
            {
                float py = y + 0.5f;
                for (int x = x0; x <= x1; x++)
                {
                    float px2 = x + 0.5f;
                    float l1 = ((by - cy) * (px2 - cx) + (cx - bx) * (py - cy)) / den;
                    float l2 = ((cy - ay) * (px2 - cx) + (ax - cx) * (py - cy)) / den;
                    if (l1 >= -0.001f && l2 >= -0.001f && l1 + l2 <= 1.001f) covered[y * w + x] = true;
                }
            }
        }
        return covered;
    }

    /// <summary>Fraction of the covered footprint that is near-white RGB, and that has alpha under 32.</summary>
    private static (float White, float LowAlpha) FootprintStats(RgbaImage image, MdxGeoset geoset)
    {
        var covered = Footprint(image, geoset);
        long total = 0, white = 0, low = 0;
        for (int i = 0; i < covered.Length; i++)
        {
            if (!covered[i]) continue;
            total++;
            int p = i * 4;
            if (image.Pixels[p] > 235 && image.Pixels[p + 1] > 235 && image.Pixels[p + 2] > 235) white++;
            if (image.Pixels[p + 3] < 32) low++;
        }
        return total == 0 ? (0f, 0f) : ((float)white / total, (float)low / total);
    }

    /// <summary>The texture with this geoset's UV footprint tinted, so misplaced UVs are visible.</summary>
    private static RgbaImage FootprintOverlay(RgbaImage image, MdxGeoset geoset)
    {
        var covered = Footprint(image, geoset);
        var px = (byte[])image.Pixels.Clone();
        for (int i = 0; i < covered.Length; i++)
        {
            int p = i * 4;
            px[p + 3] = 255;
            if (covered[i]) continue;
            // Uncovered texels are dimmed and pushed blue, so the footprint reads as the bright part.
            px[p] = (byte)Math.Min(255, px[p] / 3 + 90);
            px[p + 1] /= 3;
            px[p + 2] /= 3;
        }
        return new RgbaImage { Width = image.Width, Height = image.Height, Pixels = px };
    }

    /// <summary>Alpha channel rendered as opaque greyscale, so a coverage mask can be eyeballed.</summary>
    private static RgbaImage AlphaAsGrey(RgbaImage src)
    {
        var px = new byte[src.Pixels.Length];
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = src.Pixels[i + 3];
            px[i] = px[i + 1] = px[i + 2] = a;
            px[i + 3] = 255;
        }
        return new RgbaImage { Width = src.Width, Height = src.Height, Pixels = px };
    }

    /// <summary>Decodes exported .dds files back to .png so the shipped artifact can be inspected.</summary>
    public static int Dump(string path, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.dds", SearchOption.AllDirectories)
            : [path];
        foreach (string f in files)
        {
            var data = File.ReadAllBytes(f);
            if (!DdsReader.LooksLikeDds(data)) { Console.WriteLine($"not dds: {f}"); continue; }
            var img = DdsReader.Decode(data);
            string label = Path.GetFileNameWithoutExtension(f);
            string parent = Path.GetFileName(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(Path.Combine(outDir, $"{parent}__{label}.png"),
                               PngWriter.Write(Downscale(img, 256)));
            File.WriteAllBytes(Path.Combine(outDir, $"{parent}__{label}_ALPHA.png"),
                               PngWriter.Write(AlphaAsGrey(Downscale(img, 256))));

            long opaque = 0, total = (long)img.Width * img.Height;
            for (int i = 3; i < img.Pixels.Length; i += 4) if (img.Pixels[i] == 255) opaque++;
            Console.WriteLine($"{parent}/{label}: {img.Width}x{img.Height} "
                              + $"alpha==255 on {opaque * 100.0 / total:0.0}% of texels");
        }
        return 0;
    }

    private static RgbaImage Downscale(RgbaImage src, int max)
    {
        if (src.Width <= max && src.Height <= max) return src;
        int w = src.Width >= src.Height ? max : Math.Max(1, src.Width * max / src.Height);
        int h = src.Height >= src.Width ? max : Math.Max(1, src.Height * max / src.Width);
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int sy = y * src.Height / h;
            for (int x = 0; x < w; x++)
            {
                int sx = x * src.Width / w;
                int s = (sy * src.Width + sx) * 4, d = (y * w + x) * 4;
                px[d] = src.Pixels[s]; px[d + 1] = src.Pixels[s + 1];
                px[d + 2] = src.Pixels[s + 2]; px[d + 3] = src.Pixels[s + 3];
            }
        }
        return new RgbaImage { Width = w, Height = h, Pixels = px };
    }
}
