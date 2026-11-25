using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// <c>--camcensus [filter]</c>: walks every archive model's raw CAMS chunk and compares it with what
/// MdxReader returns — how many cameras the bytes hold, what each camera's leading size word reads,
/// and how many cameras the parsed model carries. Written for the portrait that came back black in
/// game: the reader dropped the camera, so the export had none to write.
/// </summary>
public static class CamCensus
{
    /// <summary>
    /// <c>--camexport [filter] [limit]</c>: exports models that carry cameras and checks the bytes —
    /// the structural verify, one CAM_ per WC3 camera, and each camera bone's bind-pose -Z aimed at
    /// the WC3 target (ToSc2-turned, scaled) and its FOV the 4:3 vertical conversion.
    /// </summary>
    public static int Export(string install, string? filter, int limit)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var cache = new Wc3TextureCache(storage);
        int done = 0, bad = 0;
        double worstAngle = 0;
        foreach (var e in index.Models)
        {
            if (done >= limit) break;
            if (filter is not null && !e.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = storage.TryReadFile(e.CascName);
            if (raw is null) continue;
            MdxModel mdl;
            try { mdl = MdxReader.Read(raw); } catch { continue; }
            if (mdl.Cameras.Count == 0) continue;
            done++;
            var problems = new List<string>();
            try
            {
                const float scale = 0.025f;
                var opts = new Wc3ModelViewer.Core.Convert.M3ExportOptions
                {
                    Lod = mdl.LodLevels.FirstOrDefault(), ModelName = "camx", Scale = scale, MaxTextureSize = 64,
                };
                cache.PreferHd = mdl.IsReforged;
                var res = new Wc3ModelViewer.Core.Convert.M3Exporter(mdl, opts).Export(cache, e.CascName);
                var d = res.M3;
                problems.AddRange(ExportProbe.Verify(d));
                var secs = new List<(string Tag, int Off, int Count, int Ver)>();
                int io = BitConverter.ToInt32(d, 4), n = BitConverter.ToInt32(d, 8);
                for (int i = 0; i < n; i++)
                {
                    var t = Encoding.ASCII.GetString(d, io + 16 * i, 4);
                    secs.Add((new string(t.Reverse().ToArray()).TrimEnd('\0'), BitConverter.ToInt32(d, io + 16 * i + 4),
                              BitConverter.ToInt32(d, io + 16 * i + 8), BitConverter.ToInt32(d, io + 16 * i + 12)));
                }
                var cam = secs.FirstOrDefault(s => s.Tag == "CAM_");
                var iref = secs.First(s => s.Tag == "IREF");
                if (cam.Count != mdl.Cameras.Count) problems.Add($"CAM_ x{cam.Count}, WC3 has {mdl.Cameras.Count}");
                for (int k = 0; k < Math.Min(cam.Count, mdl.Cameras.Count); k++)
                {
                    int o = cam.Off + 264 * k;
                    int bone = BitConverter.ToInt32(d, o);
                    float fov = BitConverter.ToSingle(d, o + 24);
                    var m = new System.Numerics.Matrix4x4();
                    var f = new float[16];
                    for (int j = 0; j < 16; j++) f[j] = BitConverter.ToSingle(d, iref.Off + 64 * bone + 4 * j);
                    m = new(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
                    System.Numerics.Matrix4x4.Invert(m, out var w);
                    var pos = new System.Numerics.Vector3(w.M41, w.M42, w.M43);
                    var fwd = -System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(w.M31, w.M32, w.M33));
                    var c = mdl.Cameras[k];
                    var tgt = new System.Numerics.Vector3(c.TargetPosition.Y, -c.TargetPosition.X, c.TargetPosition.Z) * scale;
                    var to = tgt - pos;
                    if (to.Length() < 1e-6f) continue;
                    double ang = Math.Acos(Math.Clamp(System.Numerics.Vector3.Dot(fwd, System.Numerics.Vector3.Normalize(to)), -1, 1)) * 180 / Math.PI;
                    worstAngle = Math.Max(worstAngle, ang);
                    if (ang > 0.5) problems.Add($"camera {k} looks {ang:F2} deg off its target");
                    float want = 2 * MathF.Atan(MathF.Tan(c.FieldOfView / 2) * 0.75f);
                    if (MathF.Abs(fov - want) > 1e-4f) problems.Add($"camera {k} fov {fov} want {want}");
                }
            }
            catch (Exception ex) { problems.Add("THROW " + ex.Message); }
            if (problems.Count > 0)
            {
                bad++;
                Console.WriteLine($"  BAD {e.RelativePath} ({e.ArtSet}): {string.Join("; ", problems.Take(4))}");
            }
        }
        Console.WriteLine($"{done} camera models exported, {bad} with problems, worst look-at error {worstAngle:F3} deg");
        return bad;
    }

    public static int Run(string install, string? filter)
    {
        using var storage = new Wc3Storage(install);
        var index = storage.BuildIndex();
        var highBytes = new Dictionary<(Wc3ArtSet, uint), int>();
        var versions = new Dictionary<(Wc3ArtSet, uint), int>();
        int files = 0, withCams = 0, rawCams = 0, parsedCams = 0, lost = 0, exactEnd = 0, trackMismatch = 0;
        var trackShapes = new Dictionary<string, int>();
        var oddExamples = new List<string>();
        int multi = 0;
        var multiExamples = new List<string>();
        var lostExamples = new List<string>();
        foreach (var e in index.Models)
        {
            if (filter is not null && !e.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var raw = storage.TryReadFile(e.CascName);
            if (raw is null || raw.Length < 8 || Encoding.ASCII.GetString(raw, 0, 4) != "MDLX") continue;
            files++;
            uint vers = 0;
            int camsAt = -1, camsSize = 0;
            for (int o = 4; o + 8 <= raw.Length;)
            {
                string tag = Encoding.ASCII.GetString(raw, o, 4);
                int size = BitConverter.ToInt32(raw, o + 4);
                if (size < 0 || o + 8 + size > raw.Length) break;
                if (tag == "VERS") vers = BitConverter.ToUInt32(raw, o + 8);
                if (tag == "CAMS") { camsAt = o + 8; camsSize = size; }
                o += 8 + size;
            }
            if (camsAt < 0) continue;
            withCams++;

            // Count cameras by the low 24 bits of each size word, which is what the chunk's own
            // length agrees with; the top byte is reported separately.
            int n = 0, end = camsAt;
            var rawTags = new List<string>();
            for (int q = camsAt; q + 4 <= camsAt + camsSize;)
            {
                uint word = BitConverter.ToUInt32(raw, q);
                uint low = word & 0x00FFFFFF, high = word >> 24;
                highBytes[(e.ArtSet, high)] = highBytes.GetValueOrDefault((e.ArtSet, high)) + 1;
                n++;
                if (low < 120) break;
                var tags = new List<string>();
                for (int t = q + 120; t + 16 <= q + (int)low;)
                {
                    string tg = Encoding.ASCII.GetString(raw, t, 4);
                    int keys = BitConverter.ToInt32(raw, t + 4), interp = BitConverter.ToInt32(raw, t + 8);
                    int comp = tg is "KCTR" or "KTTR" ? 3 : tg is "KCRL" or "IDUF" or "ELAF" ? 1 : 0;
                    if (comp == 0)
                    {
                        tags.Add("?" + tg);
                        if (oddExamples.Count < 3)
                            oddExamples.Add($"{e.RelativePath} ({e.ArtSet}) cam {n - 1} len {low} at +{t - q}: {Convert.ToHexString(raw, t, Math.Min(64, q + (int)low - t))}");
                        break;
                    }
                    tags.Add(tg);
                    t += 16 + keys * (4 + 4 * comp * (interp > 1 ? 3 : 1));
                    if (t == q + (int)low) tags.Add("=end");
                }
                rawTags.Add(string.Join("+", tags));
                q += (int)low;
                end = q;
            }
            if (end == camsAt + camsSize) exactEnd++;
            foreach (var t in rawTags) trackShapes[t] = trackShapes.GetValueOrDefault(t) + 1;
            rawCams += n;
            if (n > 1)
            {
                multi++;
                if (multiExamples.Count < 15)
                    multiExamples.Add($"{e.RelativePath} ({e.ArtSet}): " + string.Join(", ", MdxReader.Read(raw).Cameras.Select(c => $"'{c.Name}'")));
            }
            versions[(e.ArtSet, vers)] = versions.GetValueOrDefault((e.ArtSet, vers)) + 1;

            int parsed;
            try
            {
                var cams = MdxReader.Read(raw).Cameras;
                parsed = cams.Count;
                for (int k = 0; k < Math.Min(cams.Count, rawTags.Count); k++)
                {
                    var c = cams[k];
                    bool want = rawTags[k].Contains("KCTR") == (c.TranslationTrack is not null)
                             && rawTags[k].Contains("KTTR") == (c.TargetTranslationTrack is not null)
                             && rawTags[k].Contains("KCRL") == (c.RollTrack is not null);
                    if (!want) trackMismatch++;
                }
            }
            catch { parsed = -1; }
            parsedCams += Math.Max(parsed, 0);
            if (parsed != n)
            {
                lost++;
                if (lostExamples.Count < 12) lostExamples.Add($"{e.RelativePath} ({e.ArtSet}) raw={n} parsed={parsed} word=0x{BitConverter.ToUInt32(raw, camsAt):X8}");
            }
        }
        Console.WriteLine($"{files} models, {withCams} with CAMS, {rawCams} cameras in the bytes, {parsedCams} parsed, {lost} models lose cameras");
        Console.WriteLine($"  {multi} model(s) carry more than one camera");
        Console.WriteLine($"  CAMS chunks walked exactly to their end by the low 24 bits: {exactEnd} of {withCams}; parsed track set differs from the bytes on {trackMismatch} camera(s)");
        foreach (var kv in trackShapes.OrderByDescending(k => k.Value))
            Console.WriteLine($"  tracks [{(kv.Key.Length == 0 ? "none" : kv.Key)}]: {kv.Value}");
        foreach (var kv in versions.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
            Console.WriteLine($"  {kv.Key.Item1,-10} VERS {kv.Key.Item2,5}: {kv.Value} models with CAMS");
        foreach (var kv in highBytes.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
            Console.WriteLine($"  {kv.Key.Item1,-10} size word top byte 0x{kv.Key.Item2:X2}: {kv.Value} cameras");
        foreach (var s in lostExamples) Console.WriteLine("  lost: " + s);
        foreach (var s in oddExamples) Console.WriteLine("  odd: " + s);
        foreach (var s in multiExamples) Console.WriteLine("  multi: " + s);
        return 0;
    }
}
