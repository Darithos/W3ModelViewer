using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// Census for the "broken UVs" report: how the TEXS wrap flags (&amp;1 wrap width, &amp;2 wrap
/// height) are set across each art set, and which geosets sample a texture that does NOT wrap on an
/// axis with UVs that leave 0..1 on that axis. Those are the only geosets where clamp-vs-repeat can
/// change the picture, so they are the candidates to look at in the viewer.
/// </summary>
public static class WrapScan
{
    public static int Run(string install, Wc3ArtSet art, int limit, string? filter, int show)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var models = index.Models.Where(m => !m.IsPortrait && m.ArtSet == art);
        if (filter is not null) models = models.Where(m => m.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var texFlags = new SortedDictionary<uint, int>();
        int parsed = 0, layers = 0, layerWrapBits = 0, txan = 0, coordNonZero = 0;
        var suspects = new List<(string Model, int Geoset, string Tex, uint Flags, float OutU, float OutV, Vector2 Lo, Vector2 Hi, int TexAnim, MdxFilterMode Filter)>();
        var layerWrapExamples = new List<string>();

        foreach (var entry in models.Take(limit))
        {
            var bytes = storage.TryReadFile(entry.CascName);
            if (bytes is null) continue;
            MdxModel model;
            try { model = MdxReader.Read(bytes); } catch { continue; }
            parsed++;
            foreach (var t in model.Textures) texFlags[t.Flags] = texFlags.GetValueOrDefault(t.Flags) + 1;

            foreach (var mat in model.Materials)
                foreach (var l in mat.Layers)
                {
                    layers++;
                    if ((l.ShadingFlags & (MdxShadingFlags.WrapWidth | MdxShadingFlags.WrapHeight)) != 0)
                    {
                        layerWrapBits++;
                        if (layerWrapExamples.Count < 12) layerWrapExamples.Add($"{entry.RelativePath}: {l.ShadingFlags}");
                    }
                    if (l.TextureAnimationId >= 0) txan++;
                    if (l.CoordId != 0) coordNonZero++;
                }

            foreach (var g in model.Geosets)
            {
                if (g.LodId != 0 || (uint)g.MaterialId >= (uint)model.Materials.Count || g.UvLayers.Count == 0) continue;
                foreach (var l in model.Materials[g.MaterialId].Layers)
                {
                    // The texture actually drawn: a flipbook's first frame, else the HD diffuse slot, else the header.
                    int texId = l.DiffuseTextureId;
                    if ((uint)texId >= (uint)model.Textures.Count) continue;
                    var tex = model.Textures[texId];
                    bool wrapU = (tex.Flags & 1) != 0, wrapV = (tex.Flags & 2) != 0;
                    if (wrapU && wrapV) continue;

                    var uvs = g.UvLayers[(uint)l.CoordId < (uint)g.UvLayers.Count ? l.CoordId : 0];
                    if (uvs.Length == 0) continue;
                    int outU = 0, outV = 0;
                    var lo = new Vector2(float.MaxValue); var hi = new Vector2(float.MinValue);
                    foreach (var uv in uvs)
                    {
                        lo = Vector2.Min(lo, uv); hi = Vector2.Max(hi, uv);
                        if (uv.X < -0.01f || uv.X > 1.01f) outU++;
                        if (uv.Y < -0.01f || uv.Y > 1.01f) outV++;
                    }
                    float fu = wrapU ? 0 : (float)outU / uvs.Length, fv = wrapV ? 0 : (float)outV / uvs.Length;
                    if (fu == 0 && fv == 0) continue;
                    suspects.Add((entry.RelativePath, g.Index, tex.ToString(), tex.Flags, fu, fv, lo, hi, l.TextureAnimationId, l.FilterMode));
                }
            }
        }

        Console.WriteLine($"{art}: {parsed} models, {layers} layers");
        Console.WriteLine("TEXS flags histogram (&1 wrap width, &2 wrap height):");
        foreach (var (f, n) in texFlags) Console.WriteLine($"  0x{f:X}: {n}");
        Console.WriteLine($"layers with shading WrapWidth/WrapHeight (0x4/0x8): {layerWrapBits}");
        foreach (var e in layerWrapExamples) Console.WriteLine("    " + e);
        Console.WriteLine($"layers with a TXAN id: {txan};  layers with CoordId != 0: {coordNonZero}");

        var byModel = suspects.GroupBy(s => s.Model).ToList();
        Console.WriteLine($"\n{suspects.Count} geoset-layers in {byModel.Count} models sample a non-wrapping axis outside 0..1");
        foreach (var grp in byModel.OrderByDescending(g => g.Max(s => Math.Max(s.OutU, s.OutV))).Take(show))
        {
            Console.WriteLine(grp.Key);
            foreach (var s in grp)
                Console.WriteLine($"    g{s.Geoset} {s.Filter} tex '{s.Tex}' flags={s.Flags} out u {s.OutU:P0} v {s.OutV:P0}  "
                                  + $"u {s.Lo.X:0.##}..{s.Hi.X:0.##} v {s.Lo.Y:0.##}..{s.Hi.Y:0.##}{(s.TexAnim >= 0 ? $" TXAN {s.TexAnim}" : "")}");
        }
        return 0;
    }
}
