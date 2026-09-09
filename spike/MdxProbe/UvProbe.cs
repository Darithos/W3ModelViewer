using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Draws a geoset's UV triangles over the texture its material composites to, so "is this geoset
/// sampling the part of the sheet it should be" can be answered by looking instead of inferring.
/// </summary>
public static class UvProbe
{
    public static int Run(string install, string modelPath, int geosetIndex, string outDir)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var model = MdxReader.Read(File.ReadAllBytes(modelPath));
        var cache = new Wc3TextureCache(storage, index) { PreferHd = model.IsReforged };
        cache.LocalRoots.Add(Path.GetDirectoryName(Path.GetFullPath(modelPath))!);
        Directory.CreateDirectory(outDir);

        foreach (var g in model.Geosets.Where(g => geosetIndex < 0 || g.Index == geosetIndex))
        {
            if ((uint)g.MaterialId >= (uint)model.Materials.Count) continue;
            var comp = MaterialCompositor.Compose(model, model.Materials[g.MaterialId], cache, "", 0);
            var tex = comp.Texture;

            int w = tex.Width, h = tex.Height;
            var px = (byte[])tex.Pixels.Clone();
            var uvs = g.Uvs;
            var idx = g.Indices;
            int drawn = 0;
            float uMin = float.MaxValue, uMax = float.MinValue, vMin = float.MaxValue, vMax = float.MinValue;

            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                int i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                if ((uint)i0 >= (uint)uvs.Length || (uint)i1 >= (uint)uvs.Length || (uint)i2 >= (uint)uvs.Length) continue;
                foreach (int i in new[] { i0, i1, i2 })
                {
                    uMin = Math.Min(uMin, uvs[i].X); uMax = Math.Max(uMax, uvs[i].X);
                    vMin = Math.Min(vMin, uvs[i].Y); vMax = Math.Max(vMax, uvs[i].Y);
                }
                Line(px, w, h, uvs[i0], uvs[i1]);
                Line(px, w, h, uvs[i1], uvs[i2]);
                Line(px, w, h, uvs[i2], uvs[i0]);
                drawn++;
            }

            // Alpha inside the footprint, sampled at the UV vertices: a face drawn through an
            // alpha channel it was never meant to use shows up here and nowhere else.
            int transparent = 0, sampled = 0;
            foreach (var uv in uvs)
            {
                int x = Math.Clamp((int)(uv.X * w), 0, w - 1);
                int y = Math.Clamp((int)(uv.Y * h), 0, h - 1);
                sampled++;
                if (tex.Pixels[(y * w + x) * 4 + 3] < 250) transparent++;
            }

            string name = $"g{g.Index}_mat{g.MaterialId}_uv";
            // Classic skins are often 128px or smaller; written at 1:1 they are unreadable on a
            // modern screen, and this image exists to be looked at.
            var sheet = Upscale(new RgbaImage { Width = w, Height = h, Pixels = px });
            File.WriteAllBytes(Path.Combine(outDir, name + ".png"), PngWriter.Write(sheet));
            Console.WriteLine($"geoset {g.Index} mat {g.MaterialId}: {drawn} tris on {w}x{h}, "
                              + $"u {uMin:0.###}..{uMax:0.###}  v {vMin:0.###}..{vMax:0.###}, "
                              + $"blend {comp.Blend}, {transparent}/{sampled} verts on non-opaque texels -> {name}.png");
        }
        return 0;
    }

    /// <summary>Nearest-neighbour enlargement to at least 512px, so texels stay square and countable.</summary>
    private static RgbaImage Upscale(RgbaImage src)
    {
        int factor = Math.Max(1, Math.Min(8, 512 / Math.Max(src.Width, src.Height)));
        if (factor <= 1) return src;

        int w = src.Width * factor, h = src.Height * factor;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = ((y / factor) * src.Width + x / factor) * 4;
                int d = (y * w + x) * 4;
                px[d] = src.Pixels[s]; px[d + 1] = src.Pixels[s + 1];
                px[d + 2] = src.Pixels[s + 2]; px[d + 3] = src.Pixels[s + 3];
            }
        return new RgbaImage { Width = w, Height = h, Pixels = px };
    }

    // Bright green wireframe: nothing in Warcraft III art is this colour, so the overlay never
    // reads as part of the texture.
    private static void Line(byte[] px, int w, int h, Vector2 a, Vector2 b)
    {
        float ax = a.X * w, ay = a.Y * h, bx = b.X * w, by = b.Y * h;
        int steps = (int)Math.Max(Math.Abs(bx - ax), Math.Abs(by - ay)) + 1;
        for (int s = 0; s <= steps; s++)
        {
            float f = steps == 0 ? 0 : (float)s / steps;
            int x = (int)MathF.Round(ax + (bx - ax) * f);
            int y = (int)MathF.Round(ay + (by - ay) * f);
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) continue;
            int o = (y * w + x) * 4;
            px[o] = 0; px[o + 1] = 255; px[o + 2] = 0; px[o + 3] = 255;
        }
    }
}
