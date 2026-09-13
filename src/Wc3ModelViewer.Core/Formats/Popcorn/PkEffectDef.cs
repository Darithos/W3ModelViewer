using System.Numerics;

namespace Wc3ModelViewer.Core.Formats.Popcorn;

/// <summary>A curve sampler: knots in 0..1, <see cref="Dimension"/> values per knot, cubic Hermite with in/out tangents.</summary>
public sealed class PkCurve
{
    public required int Dimension { get; init; }
    public required float[] Times { get; init; }
    public required float[] Values { get; init; }
    /// <summary><c>knots x 2 x Dimension</c>: per knot, the in tangent then the out tangent; empty for linear.</summary>
    public required float[] Tangents { get; init; }

    public PkValue Sample(float t)
    {
        var r = new PkValue();
        int n = Times.Length;
        if (n == 0) return r;
        if (!float.IsFinite(t)) t = 0;
        if (n == 1 || t <= Times[0]) { Knot(0, ref r); return r; }
        if (t >= Times[n - 1]) { Knot(n - 1, ref r); return r; }
        int k = 0;
        while (k + 2 < n && Times[k + 1] <= t) k++;
        float span = Times[k + 1] - Times[k];
        float u = span > 1e-9f ? (t - Times[k]) / span : 0;
        float u2 = u * u, u3 = u2 * u;
        float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
        bool hermite = Tangents.Length >= n * 2 * Dimension;
        for (int d = 0; d < Dimension && d < 4; d++)
        {
            float p0 = Values[k * Dimension + d], p1 = Values[(k + 1) * Dimension + d];
            if (!hermite) { r[d] = p0 + (p1 - p0) * u; continue; }
            float m0 = Tangents[k * 2 * Dimension + Dimension + d];      // out tangent of knot k
            float m1 = Tangents[(k + 1) * 2 * Dimension + d];            // in tangent of knot k+1
            r[d] = h00 * p0 + h10 * m0 * span + h01 * p1 + h11 * m1 * span;
        }
        return r;
    }

    private void Knot(int k, ref PkValue r)
    {
        for (int d = 0; d < Dimension && d < 4; d++) r[d] = Values[k * Dimension + d];
    }
}

/// <summary>A shape sampler. Types are PopcornFX 2's: 0 box, 1 sphere, 2 ellipsoid, 3 cylinder, 4 capsule, 5 cone, 6 mesh.</summary>
public sealed class PkShape
{
    public int Type { get; init; }
    public float Radius { get; init; } = 1;
    public float InnerRadius { get; init; }
    public float Height { get; init; } = 1;
    /// <summary>Field 13. Stored in PopcornFX's Y-up shape frame; applied here with Y and Z swapped.</summary>
    public Vector3 Offset { get; init; }
    /// <summary>Field 14: Euler angles in degrees.</summary>
    public Vector3 EulerDegrees { get; init; }

    /// <summary>A point in the shape's volume, in the effect's Z-up frame.</summary>
    public Vector3 SamplePosition(Random rng)
    {
        float U() => (float)rng.NextDouble();
        Vector3 p;   // Y-up shape space
        switch (Type)
        {
            case 1: case 2:
            {
                var dir = RandomUnit(rng);
                float r = Radial(U(), InnerRadius, Radius, 3);
                p = dir * r;
                break;
            }
            case 3:
            {
                float a = U() * MathF.Tau;
                float r = Radial(U(), InnerRadius, Radius, 2);
                p = new Vector3(MathF.Cos(a) * r, (U() - 0.5f) * Height, MathF.Sin(a) * r);
                break;
            }
            case 4:
            {
                float a = U() * MathF.Tau;
                float r = Radial(U(), InnerRadius, Radius, 2);
                p = new Vector3(MathF.Cos(a) * r, (U() - 0.5f) * (Height + 2 * Radius), MathF.Sin(a) * r);
                break;
            }
            case 5:
            {
                float h = U();
                float a = U() * MathF.Tau;
                float r = Radial(U(), 0, Radius * h, 2);
                p = new Vector3(MathF.Cos(a) * r, h * Height, MathF.Sin(a) * r);
                break;
            }
            case 0:
                p = new Vector3((U() - 0.5f) * Radius, (U() - 0.5f) * Height, (U() - 0.5f) * Radius);
                break;
            default:
                p = Vector3.Zero;
                break;
        }
        if (EulerDegrees != Vector3.Zero)
        {
            var q = Quaternion.CreateFromYawPitchRoll(float.DegreesToRadians(EulerDegrees.Y), float.DegreesToRadians(EulerDegrees.X), float.DegreesToRadians(EulerDegrees.Z));
            p = Vector3.Transform(p, q);
        }
        p += Offset;
        return new Vector3(p.X, p.Z, p.Y);
    }

    private static float Radial(float u, float inner, float outer, int dims)
    {
        inner = Math.Clamp(inner, 0, MathF.Max(outer, 0));
        if (outer <= 0) return 0;
        float a = MathF.Pow(inner / outer, dims), f = a + (1 - a) * u;
        return outer * (dims == 3 ? MathF.Cbrt(f) : MathF.Sqrt(f));
    }

    public static Vector3 RandomUnit(Random rng)
    {
        float z = (float)rng.NextDouble() * 2 - 1;
        float a = (float)rng.NextDouble() * MathF.Tau;
        float s = MathF.Sqrt(MathF.Max(0, 1 - z * z));
        return new Vector3(s * MathF.Cos(a), s * MathF.Sin(a), z);
    }
}

public enum PkSamplerKind { Unknown, Curve, Shape, EventStream, Turbulence, Spectrum }

public sealed class PkSampler
{
    public required string Name { get; init; }
    public PkSamplerKind Kind { get; init; }
    public PkCurve? Curve { get; init; }
    public PkShape? Shape { get; init; }
    /// <summary>Event times of an EventStream sampler, in seconds of effect age.</summary>
    public float[] EventTimes { get; init; } = [];
    /// <summary>Turbulence: noise wavelength in metres (record field 11; 0.1 or 6 where written, 1 by default).</summary>
    public float Wavelength { get; init; } = 1f;
    /// <summary>Turbulence: displacement amplitude (field 12; 0.01–2 where written, 1 by default).</summary>
    public float Strength { get; init; } = 1f;
}

public enum PkRendererKind { Billboard, Ribbon, Distortion, Light, Other }

public sealed class PkRendererDef
{
    public PkRendererKind Kind { get; init; }
    public string Material { get; init; } = "";
    public string Texture { get; init; } = "";
    public PopcornBillboardMode Billboard { get; init; }
    public PopcornBlend Blend { get; init; }
    public int AtlasColumns { get; init; } = 1;
    public int AtlasRows { get; init; } = 1;
    public bool Size2D { get; init; }
    public bool FlipUVs { get; init; }

    /// <summary>Particle field index bound to each renderer input: Position, Size, Size2, Axis, NormalAxis, Rotation, Color, TextureID, Enabled.</summary>
    public Dictionary<string, int> Inputs { get; } = new(StringComparer.Ordinal);

    public int Input(string name) => Inputs.TryGetValue(name, out int i) ? i : -1;
}

public sealed class PkFieldDef
{
    public required string Name { get; init; }
    /// <summary>The operand kind the field holds (the bake stores kind − 1).</summary>
    public byte Kind { get; init; }
}

public sealed class PkLayerDef
{
    public int Record { get; init; }
    public string Name { get; set; } = "";
    public List<PkFieldDef> Fields { get; } = [];
    public Dictionary<string, int> FieldIndex { get; } = new(StringComparer.Ordinal);
    public PkScript? Spawn { get; set; }
    public PkScript? EvolveOnSpawn { get; set; }
    public PkScript? Evolve { get; set; }
    public List<PkSampler> Samplers { get; } = [];
    public List<PkRendererDef> Renderers { get; } = [];

    /// <summary>
    /// For each event this layer fires, the payload names in the order its scripts append them
    /// (<c>appendPayload</c> index -> name).
    /// </summary>
    public Dictionary<string, string[]> OutputPayloads { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The payloads this layer's scripts read, in <c>extractPayloadElement</c> index order. A child
    /// reads by name, not by the order its parent appended them: Lightning Shield's orb appends
    /// Color fourth but its children read it at index 4 of their own layout.
    /// </summary>
    public string[] InputPayloads { get; set; } = [];

    /// <summary>Names of the spatial layers <c>__spatialLayer_N</c> refers to, in N order.</summary>
    public string[] SpatialLayers { get; set; } = [];

    public int Field(string name) => FieldIndex.TryGetValue(name, out int i) ? i : -1;
    public PkSampler? Sampler(string name) => Samplers.FirstOrDefault(s => s.Name == name);
    public override string ToString() => $"{Name} ({Fields.Count} fields, {Renderers.Count} renderer(s))";
}

public sealed class PkLayerSlot
{
    public int Layer { get; init; }
    public int[] InputEvents { get; init; } = [];
    public int[] OutputEvents { get; init; } = [];
}

public sealed class PkEventSlot
{
    public string Name { get; init; } = "";
    public int[] TargetSlots { get; init; } = [];
}

/// <summary>
/// The executable content of a PopcornFX bake: its layers with their compiled scripts, samplers and
/// renderers, and the graph that says which layer's events spawn which.
/// </summary>
public sealed class PkEffectDef
{
    public List<PkLayerDef> Layers { get; } = [];
    public List<PkLayerSlot> Slots { get; } = [];
    public List<PkEventSlot> Events { get; } = [];
    public List<string> Warnings { get; } = [];

    public static PkEffectDef Load(byte[] data)
    {
        var b = PkBakeFile.Load(data);
        var def = new PkEffectDef();
        var layerByRecord = new Dictionary<int, int>();

        foreach (int lr in b.OfClass("CLayerCompileCache"))
        {
            var layer = new PkLayerDef { Record = lr };
            try { ReadLayer(b, lr, layer); }
            catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                def.Warnings.Add($"layer #{lr}: {e.Message}");
            }
            layerByRecord[lr] = def.Layers.Count;
            def.Layers.Add(layer);
        }

        int graph = b.OfClass("CLayerGraphCompileCache").DefaultIfEmpty(-1).First();
        if (graph < 0) { def.Warnings.Add("no layer graph"); return def; }

        foreach (int sr in b.Refs(graph, 2))
        {
            if (sr < 0) continue;
            int lr = b.Ref(sr, 0);
            def.Slots.Add(new PkLayerSlot
            {
                Layer = layerByRecord.TryGetValue(lr, out int li) ? li : -1,
                InputEvents = b.U32s(sr, 1).Select(u => (int)u).ToArray(),
                OutputEvents = b.U32s(sr, 2).Select(u => (int)u).ToArray(),
            });
        }
        foreach (int er in b.Refs(graph, 3))
        {
            if (er < 0) { def.Events.Add(new PkEventSlot()); continue; }
            def.Events.Add(new PkEventSlot
            {
                Name = b.Has(er, 0) ? b.String(er, 0) : "",
                TargetSlots = b.U32s(er, 2).Select(u => (int)u).ToArray(),
            });
        }

        // Layer names: the graph node prefix of its renderer fields (n20_8__Size -> n20_8).
        foreach (var layer in def.Layers)
        {
            string? node = layer.Fields.Select(f => f.Name).Where(n => n.Contains("__") && n.StartsWith('n'))
                                .Select(n => n[..n.IndexOf("__", StringComparison.Ordinal)]).FirstOrDefault();
            layer.Name = node ?? (layer.Renderers.Count == 0 ? $"spawner{layer.Record}" : $"layer{layer.Record}");
        }
        return def;
    }

    private static void ReadLayer(PkBakeFile b, int lr, PkLayerDef layer)
    {
        foreach (int fr in b.Refs(lr, 2))
        {
            if (fr < 0) continue;
            string name = b.String(fr, 0);
            layer.FieldIndex.TryAdd(name, layer.Fields.Count);
            layer.Fields.Add(new PkFieldDef { Name = name, Kind = (byte)(b.U32(fr, 1) + 1) });
        }

        foreach (int sr in b.Refs(lr, 7))
        {
            if (sr < 0) continue;
            string name = b.String(sr, 0);
            int dr = b.Ref(sr, 1);
            layer.Samplers.Add(ReadSampler(b, name, dr));
        }

        foreach (int br in b.Refs(lr, 10))
        {
            if (br < 0 || b.ClassOf(br) != "CCompilerBlobCache") continue;
            var script = PkScript.Decode(b, br);
            switch (script.Kind)
            {
                case null: layer.Spawn ??= script; break;
                case 3: layer.EvolveOnSpawn ??= script; break;
                case 4: layer.Evolve ??= script; break;
            }
        }

        foreach (int rr in b.Refs(lr, 8))
        {
            if (rr < 0) continue;
            layer.Renderers.Add(ReadRenderer(b, rr, layer));
        }

        // Events this layer fires (field 5) name their payloads in append order; field 12 points at
        // an unnamed event whose payload list is the layout this layer reads its inputs from.
        foreach (int er in b.Refs(lr, 5))
        {
            if (er < 0 || !b.Has(er, 0)) continue;
            layer.OutputPayloads[b.String(er, 0)] = PayloadNames(b, er);
        }
        int input = b.Ref(lr, 12);
        if (input >= 0 && b.ClassOf(input) == "CLayerCompileCacheEvent") layer.InputPayloads = PayloadNames(b, input);
        layer.SpatialLayers = b.Refs(lr, 6).Where(r => r >= 0).Select(r => b.String(r, 0)).ToArray();
    }

    private static string[] PayloadNames(PkBakeFile b, int eventRec)
        => b.Refs(eventRec, 2).Select(p => p < 0 ? "" : b.String(p, 0)).ToArray();

    private static PkSampler ReadSampler(PkBakeFile b, string name, int dr)
    {
        string cls = dr >= 0 ? b.ClassOf(dr) : "";
        switch (cls)
        {
            case "CParticleNodeSamplerData_Curve":
            {
                int dim = b.Has(dr, 9) ? b.I32(dr, 9) : 1;
                var times = b.F32s(dr, 16);
                var values = b.F32s(dr, 17);
                var tangents = b.F32s(dr, 18);
                if (dim is < 1 or > 4 || values.Length < times.Length * dim) { times = []; values = []; }
                return new PkSampler
                {
                    Name = name, Kind = PkSamplerKind.Curve,
                    Curve = new PkCurve { Dimension = Math.Clamp(dim, 1, 4), Times = times, Values = values, Tangents = tangents },
                };
            }
            case "CParticleNodeSamplerData_Shape":
                return new PkSampler
                {
                    Name = name, Kind = PkSamplerKind.Shape,
                    Shape = new PkShape
                    {
                        Type = b.I32(dr, 15, 0),
                        Radius = b.F32(dr, 17, 1f),
                        InnerRadius = b.F32(dr, 18, 0f),
                        Height = b.F32(dr, 20, 1f),
                        Offset = b.V3(dr, 13),
                        EulerDegrees = b.V3(dr, 14),
                    },
                };
            case "CParticleNodeSamplerData_EventStream":
                return new PkSampler { Name = name, Kind = PkSamplerKind.EventStream, EventTimes = b.F32s(dr, 10) };
            case "CParticleNodeSamplerData_Turbulence":
                // Only the fields a bake overrides are written. Over the 2,165 bakes neither field ever
                // holds exactly 1, the mark of a default. Unholy Aura's runes use strength 0.01, a
                // near-rigid orbit; applying full-strength noise scattered them across the scene.
                return new PkSampler
                {
                    Name = name, Kind = PkSamplerKind.Turbulence,
                    Wavelength = b.F32(dr, 11, 1f), Strength = b.F32(dr, 12, 1f),
                };
            default:
                return new PkSampler
                {
                    Name = name,
                    Kind = cls.Contains("Turbulence", StringComparison.Ordinal) ? PkSamplerKind.Turbulence
                         : cls.Contains("Spectrum", StringComparison.Ordinal) ? PkSamplerKind.Spectrum
                         : PkSamplerKind.Unknown,
                };
        }
    }

    private static PkRendererDef ReadRenderer(PkBakeFile b, int rr, PkLayerDef layer)
    {
        string material = b.String(rr, 3);
        string texture = "";
        var mode = PopcornBillboardMode.ScreenAligned;
        var blend = PopcornBlend.Additive;
        int ax = 1, ay = 1;
        bool size2d = false, flip = false;
        foreach (int pr in b.Refs(rr, 2))
        {
            if (pr < 0) continue;
            string name = b.String(pr, 0);
            var raw = b.Raw(pr, 2);
            int v0 = raw.Length >= 4 ? BitConverter.ToInt32(raw, 0) : 0;
            int v1 = raw.Length >= 8 ? BitConverter.ToInt32(raw, 4) : 0;
            switch (name)
            {
                case "Diffuse.DiffuseMap": case "Distortion.DistortionMap":
                    if (texture.Length == 0 && b.Has(pr, 3)) texture = b.String(pr, 3);
                    break;
                case "BillboardingMode": mode = (PopcornBillboardMode)Math.Clamp(v0, 0, 5); break;
                case "Transparent.Type": blend = (PopcornBlend)Math.Clamp(v0, 0, 3); break;
                case "Atlas.SubDiv": ax = Math.Max(1, v0); ay = Math.Max(1, v1); break;
                case "EnableSize2D": size2d = raw.Length > 0; break;
                case "FlipUVs": flip = raw.Length > 0; break;
            }
        }
        var def = new PkRendererDef
        {
            Kind = material.Contains("Ribbon", StringComparison.OrdinalIgnoreCase) ? PkRendererKind.Ribbon
                 : material.Contains("Distortion", StringComparison.OrdinalIgnoreCase) ? PkRendererKind.Distortion
                 : material.Contains("Light", StringComparison.OrdinalIgnoreCase) ? PkRendererKind.Light
                 : material.Contains("Billboard", StringComparison.OrdinalIgnoreCase) ? PkRendererKind.Billboard
                 : PkRendererKind.Other,
            Material = material, Texture = texture, Billboard = mode, Blend = blend,
            AtlasColumns = ax, AtlasRows = ay, Size2D = size2d, FlipUVs = flip,
        };

        // Inputs: field 0 is a built-in input id (0 Position, 1 Size, 2 Enabled, 3/6 Rotation,
        // 4 Axis, 5 NormalAxis, 7 Size2) or, from 100 up, a named input whose name is field 2.
        foreach (int ir in b.Refs(rr, 1))
        {
            if (ir < 0) continue;
            int id = b.I32(ir, 0, 0);
            int field = b.I32(ir, 1, 0);
            string key = id switch
            {
                0 => "Position", 1 => "Size", 2 => "Enabled", 3 or 6 => "Rotation", 4 => "Axis", 5 => "NormalAxis", 7 => "Size2",
                9 => "SelfID", 10 => "ParentID",
                _ => b.Has(ir, 2) ? b.String(ir, 2) : $"input{id}",
            };
            if ((uint)field < (uint)layer.Fields.Count) def.Inputs[key] = field;
        }
        return def;
    }
}
