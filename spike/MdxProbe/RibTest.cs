using System.Numerics;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Convert;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// <c>--ribtest &lt;outDir&gt; [--name N]</c>: a model whose ribbon bones fly in circles under their
/// own animation, so every <c>RIB_</c> draws a full loop while the model stands still.
/// </summary>
/// <remarks>
/// A StarCraft II ribbon only lays geometry where its bone has moved, so a parked model shows none —
/// which makes "the trail is missing" impossible to tell from "the ribbon does not draw" in a static
/// preview, and awkward to test by dragging a doodad. Here the motion is inside the model: four
/// ribbons of different colour, side by side, each on a bone that circles once per sequence, so one
/// screenshot says which configurations draw at all.
///
/// The variants differ only in the two things the exporter chooses per emitter — blend mode and
/// lifespan — because everything else (`additional_flags`, `length`, `speed`, `divisions`) is fixed
/// for every ribbon the writer emits.
/// </remarks>
internal static class RibTest
{
    private readonly record struct Variant(string Name, Vector3 Colour, MdxParticleBlend Blend, float Life, float Speed = 0f, float Gravity = 0f, string Tex = "solid", int Type = 0, float Length = 0f, bool Vertical = false, float BoneRollDeg = 0f, bool RollAboutPath = false);

    // Blend mode is the only variable: two additive and two alpha-blended, everything else equal,
    // each pair a different colour so a single screenshot cannot be misread. RibTestC had blend
    // and lifespan varying together and put one ribbon on a billboarded bone, which left three
    // explanations for the three that were missing.
    // Self-motion is the variable: a ribbon draws only where its points have been, and the editor's
    // terrain view does not play a doodad's animation, so a bone that "circles" never actually
    // moves there. A ribbon whose points carry their own speed or fall under gravity extrudes
    // anyway — so if any of these three draw and the still one does not, RIB_ works and the trail
    // was simply never given anything to trace.
    // The texture is the variable now: blend mode and self-speed are both settled, and the only
    // thing left that differs between a ribbon that draws and the fireball's baked strip is the
    // sheet it samples — 1024x256 instead of a small square, and mostly transparent.
    // RibPlaneA said: ribbon_type does nothing, RIB_.length = 1 does nothing, but a 90-degree roll
    // on the BONE was by far the most consistently visible variant (max/median area 2.9x and only
    // 11% of frames near zero, against the control's 44x and 82%). So the bone's orientation does
    // reach the ribbon's plane — but a roll fixed in world space cannot help all the way round a
    // circle, because the path direction turns underneath it.
    //
    // So roll about the PATH instead, which holds a constant angle to the direction of travel. The
    // question these four answer is which angle stands the band up: 90 degrees should put its width
    // along world Z, which from the game's fixed ~55-degree camera is foreshortened by cos(35) =
    // 0.82 instead of a ground-flat band's cos(55) = 0.57, and never vanishes for a camera above
    // the horizon.
    private static readonly Variant[] Variants =
    [
        new("a_roll0",   new Vector3(1.0f, 0.15f, 0.10f), MdxParticleBlend.Blend, 0.60f, Tex: "bandfade", RollAboutPath: true, BoneRollDeg: 0f),
        new("b_roll45",  new Vector3(0.15f, 1.0f, 0.20f), MdxParticleBlend.Blend, 0.60f, Tex: "bandfade", RollAboutPath: true, BoneRollDeg: 45f),
        new("c_roll90",  new Vector3(0.25f, 0.45f, 1.0f), MdxParticleBlend.Blend, 0.60f, Tex: "bandfade", RollAboutPath: true, BoneRollDeg: 90f),
        new("d_roll135", new Vector3(1.0f, 0.75f, 0.10f), MdxParticleBlend.Blend, 0.60f, Tex: "bandfade", RollAboutPath: true, BoneRollDeg: 135f),
    ];

    /// <summary>
    /// solid: a small opaque square (the shape every drawing ribbon so far has used).
    /// wide: 1024x256 and fully opaque — tests size and aspect alone.
    /// band: 1024x256, opaque only across the middle third, transparent above and below — the
    ///       baked strip's vertical layout.
    /// bandfade: the same, also fading to nothing along its length, as the real strip does.
    /// </summary>
    private static RgbaImage Texture(string kind)
    {
        if (kind == "solid")
            return new RgbaImage { Width = 32, Height = 32, Pixels = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray() };
        const int w = 1024, h = 256;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                float a = kind switch
                {
                    "wide" => 1f,
                    "band" => y >= h / 3 && y < 2 * h / 3 ? 1f : 0f,
                    _ => (y >= h / 3 && y < 2 * h / 3 ? 1f : 0f) * MathF.Max(0f, 1f - x / (float)w),
                };
                px[i] = px[i + 1] = px[i + 2] = 255;
                px[i + 3] = (byte)Math.Clamp(a * 255f, 0, 255);
            }
        return new RgbaImage { Width = w, Height = h, Pixels = px };
    }

    public static int Run(string install, string outDir, string[] args)
    {
        string name = Array.IndexOf(args, "--name") is int ni && ni >= 0 && ni + 1 < args.Length ? args[ni + 1] : "RibTest";
        float scale = Array.IndexOf(args, "--scale") is int si && si >= 0 && si + 1 < args.Length
            ? float.Parse(args[si + 1], System.Globalization.CultureInfo.InvariantCulture) : 0.025f;

        using var storage = new Wc3Storage(install);
        const string src = "war3.w3mod:abilities\\spells\\human\\holybolt\\holyboltspecialart.mdx";
        var mdl = MdxReader.Read(storage.TryReadFile(src) ?? throw new FileNotFoundException(src));
        mdl.Geosets.Clear(); mdl.GeosetAnims.Clear();
        mdl.ParticleEmitters.Clear(); mdl.RibbonEmitters.Clear(); mdl.PopcornEmitters.Clear();

        // A billboarded bone turns to face the camera, which is no place to hang a ribbon and is a
        // second explanation for one not drawing; pick bones that only do what we tell them.
        var plain = Enumerable.Range(0, mdl.Nodes.Count)
            .Where(i => (mdl.Nodes[i].Flags & (MdxNodeFlags.Billboarded | MdxNodeFlags.BillboardLockX
                                             | MdxNodeFlags.BillboardLockY | MdxNodeFlags.BillboardLockZ)) == 0)
            .Take(Variants.Length).ToList();
        if (plain.Count < Variants.Length)
        {
            Console.WriteLine($"source model has only {plain.Count} non-billboarded nodes; need {Variants.Length}");
            return 1;
        }
        Console.WriteLine($"sequences: {string.Join(", ", mdl.Sequences.Select(s => $"'{s.Name}' {s.IntervalStart}..{s.IntervalEnd} ms"))}");

        // A white, fully opaque strip: the ribbon's own colour ramp tints it, so anything that
        // appears on screen is unambiguously the ribbon and not the sprite's own shading.
        var white = new RgbaImage
        {
            Width = 32, Height = 32,
            Pixels = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray(),
        };

        // Everything has to fit the editor's default view of the doodad, so the four circles sit in
        // a tight 2x2 about the origin rather than in a row: spread over 24 SC2 units they were
        // simply off screen, which reads exactly like "nothing drew".
        const float radius = 60f;         // Warcraft III units; 1.5 SC2 units at 0.025
        const float spacing = 90f;
        float halfWidth = 25f;            // the writer doubles this into full width

        for (int v = 0; v < Variants.Length; v++)
        {
            var variant = Variants[v];
            int node = plain[v];
            var centre = new Vector3(v % 2 == 0 ? -spacing : spacing, v < 2 ? -spacing : spacing, 2f / scale);
            mdl.Nodes[node] = Circle(mdl, mdl.Nodes[node], centre, radius, variant.Vertical, variant.BoneRollDeg, variant.RollAboutPath);
            mdl.ParticleEmitters.Add(new MdxParticleEmitter2
            {
                Name = "rib_" + variant.Name,
                NodeIndex = node,
                TextureId = -1,
                BakedSprite = Texture(variant.Tex),
                Ribbon = true,
                Blend = variant.Blend,
                Life = variant.Life,
                EmissionRate = 1f,
                MiddleTime = 0.5f,
                StartScale = halfWidth, MiddleScale = halfWidth, EndScale = halfWidth,
                StartColor = variant.Colour, MiddleColor = variant.Colour, EndColor = variant.Colour,
                StartAlpha = 255, MiddleAlpha = 255, EndAlpha = 255,
                Rows = 1, Columns = 1,
                Unshaded = true,
                ModelSpace = false,          // world space: the strip stays where it was drawn
                Intensity = 1f,
                RibbonSpeed = variant.Speed,
                RibbonGravity = variant.Gravity,
                RibbonType = variant.Type,
                RibbonLength = variant.Length,
            });
            Console.WriteLine($"  {variant.Name,-18} node {node} '{mdl.Nodes[node].Name}' centre ({centre.X:0},{centre.Y:0},{centre.Z:0}) "
                              + $"roll {variant.BoneRollDeg:0}deg {(variant.RollAboutPath ? "about the path" : "about X")} width {2 * halfWidth:0} u");
        }

        // Two controls, both ordinary camera-facing cards, which we know StarCraft II draws:
        //   "anchor" sits still at the origin — if it is missing, the model never loaded at all;
        //   "tracer" rides the first ribbon's bone in model space — if it does not trace a circle,
        //            the bones are not animating and no ribbon could have had anything to draw.
        // Without them "nothing on screen" says nothing about RIB_.
        mdl.ParticleEmitters.Add(Card("ctl_anchor", -1, new Vector3(0, 0, 2f / scale), new Vector3(1f, 1f, 1f), modelSpace: false, size: 12f));
        mdl.ParticleEmitters.Add(Card("ctl_tracer", plain[0], Vector3.Zero, new Vector3(1f, 0.1f, 1f), modelSpace: true, size: 12f));

        var opts = new M3ExportOptions { Lod = mdl.LodLevels.FirstOrDefault(), ModelName = name, Scale = scale };
        var res = new M3Exporter(mdl, opts).Export(new Wc3TextureCache(storage), src);
        string dir = Deploy.Resolve(outDir, name);
        Directory.CreateDirectory(Path.Combine(dir, opts.TextureFolder));
        File.WriteAllBytes(Path.Combine(dir, name + ".m3"), res.M3);
        foreach (var t in res.Textures) File.WriteAllBytes(Path.Combine(dir, opts.TextureFolder, t.FileName), t.Data);
        foreach (string l in res.Log) Console.WriteLine("  | " + l);
        Console.WriteLine($"-> {Path.Combine(dir, name + ".m3")}");
        return 0;
    }

    /// <summary>A plain camera-facing card, the control that proves what did load and move.</summary>
    private static MdxParticleEmitter2 Card(string name, int nodeIndex, Vector3 offset, Vector3 colour, bool modelSpace, float size)
    {
        var white = new RgbaImage { Width = 16, Height = 16, Pixels = Enumerable.Repeat((byte)255, 16 * 16 * 4).ToArray() };
        return new MdxParticleEmitter2
        {
            Name = name,
            NodeIndex = nodeIndex,
            TextureId = -1,
            BakedSprite = white,
            Blend = MdxParticleBlend.Add,
            Life = 0.5f,
            EmissionRate = 20f,
            MiddleTime = 0.5f,
            StartScale = size, MiddleScale = size, EndScale = size,
            StartColor = colour, MiddleColor = colour, EndColor = colour,
            StartAlpha = 255, MiddleAlpha = 255, EndAlpha = 255,
            Rows = 1, Columns = 1,
            SpawnOffset = offset,
            SpawnImmediately = true,
            Unshaded = true,
            ModelSpace = modelSpace,
            Intensity = 1f,
        };
    }

    /// <summary>
    /// The same node, given a translation track that walks a circle of <paramref name="radius"/>
    /// about <paramref name="centre"/> once over every sequence in the model. Keys are absolute
    /// offsets from the node's pivot, which is what Warcraft III translation keys are.
    /// </summary>
    private static MdxNode Circle(MdxModel mdl, MdxNode node, Vector3 centre, float radius, bool vertical = false, float rollDeg = 0f, bool rollAboutPath = false)
    {
        const int steps = 32;
        var times = new List<int>();
        var values = new List<Vector3>();
        var rots = new List<Quaternion>();
        foreach (var seq in mdl.Sequences.OrderBy(s => s.IntervalStart))
        {
            int span = Math.Max(seq.IntervalEnd - seq.IntervalStart, 1);
            for (int k = 0; k <= steps; k++)
            {
                int t = seq.IntervalStart + span * k / steps;
                if (times.Count > 0 && times[^1] >= t) continue;
                float a = 2 * MathF.PI * k / steps;
                times.Add(t);
                var off = vertical
                    ? new Vector3(MathF.Cos(a) * radius, 0, MathF.Sin(a) * radius)
                    : new Vector3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 0);
                values.Add(centre + off - node.Pivot);
                if (rollAboutPath)
                {
                    // the circle's tangent at this angle, which is the axis to roll about
                    var tan = vertical
                        ? new Vector3(-MathF.Sin(a), 0, MathF.Cos(a))
                        : new Vector3(-MathF.Sin(a), MathF.Cos(a), 0);
                    rots.Add(Quaternion.CreateFromAxisAngle(Vector3.Normalize(tan), float.DegreesToRadians(rollDeg)));
                }
                else if (rollDeg != 0)
                    rots.Add(Quaternion.CreateFromAxisAngle(Vector3.UnitX, float.DegreesToRadians(rollDeg)));
            }
        }
        return new MdxNode
        {
            Name = node.Name,
            ObjectId = node.ObjectId,
            ParentId = -1,                 // stand alone, so no parent animation moves the circle
            Flags = node.Flags,
            Kind = node.Kind,
            GeosetId = node.GeosetId,
            GeosetAnimId = node.GeosetAnimId,
            AttachmentId = node.AttachmentId,
            AttachmentPath = node.AttachmentPath,
            Pivot = node.Pivot,
            Translation = new MdxTrack<Vector3>
            {
                Tag = "KGTR",
                Interpolation = MdxInterpolation.Linear,
                Times = [.. times],
                Values = [.. values],
            },
            // A constant roll about X: if the ribbon's plane comes from the bone at all, this turns
            // the band on its side and the difference is unmissable.
            Rotation = rots.Count == 0 ? node.Rotation : new MdxTrack<Quaternion>
            {
                Tag = "KGRT",
                Interpolation = MdxInterpolation.Linear,
                Times = [.. times],
                Values = [.. rots],
            },
        };
    }
}
