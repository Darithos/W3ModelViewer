using System.Globalization;
using System.Numerics;
using System.Text;
using Wc3ModelViewer.Core.Casc;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>One file the glTF export produced (model.gltf, model.bin, *.png).</summary>
public sealed record GltfFile(string FileName, byte[] Data);

public sealed class GltfExportResult
{
    public required List<GltfFile> Files { get; init; }
    public required List<string> Log { get; init; }
}

/// <summary>
/// Writes a glTF 2.0 model (.gltf + .bin + .png) for the Blender pipeline: import into Blender,
/// then export to .m3 with the m3addon/m3studio — the flow that provably loads in StarCraft II,
/// unlike the direct MD34 writer.
/// </summary>
/// <remarks>
/// Everything WC3-shaped survives the trip:
/// <list type="bullet">
/// <item><b>Skeleton</b> — every MDX node becomes a joint; rest translation = pivot − parentPivot
/// (WC3 rest worlds are identity at the pivots, so inverse binds are <c>T(-pivot)</c>).</item>
/// <item><b>Skinning</b> — Reforged 4-weight SKIN passes through; classic matrix groups become
/// equal-weight influences, both as JOINTS_0/WEIGHTS_0.</item>
/// <item><b>Animations</b> — one glTF animation per sequence (Blender imports each as an action),
/// baked at the export fps through the same <see cref="MdxAnimator.LocalTrs"/> math as the m3
/// writer, so hermite/bezier keys, global sequences and inheritance flags all flatten to LINEAR.</item>
/// <item><b>Axes</b> — glTF is Y-up; a scene root rotated −90° about X carries the Z-up data.
/// Skinned vertices ignore node transforms, so the joints live under that root and the inverse
/// binds include it — Blender's importer then puts the model upright again.</item>
/// </list>
/// </remarks>
public sealed class GltfExporter(MdxModel mdx, M3ExportOptions options)
{
    private readonly MdxAnimator _animator = new(mdx);
    private readonly List<string> _log = [];

    /// <summary>Scene-root rotation taking the Z-up model into glTF's Y-up convention.</summary>
    private static readonly Quaternion RootRotation =
        Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);

    private sealed class Prim
    {
        public required MdxGeoset Geoset;
        public required CompositeMaterial Material;
        public int MaterialIndex;
    }

    public GltfExportResult Export(Wc3TextureCache textures, string modelCascName)
    {
        var bin = new BinWriter();
        var json = new Json();
        float scale = options.Scale;

        // ---- geosets & materials ----
        var prims = new List<Prim>();
        var materials = new List<(string Name, CompositeMaterial Mat, string PngName)>();
        var pngs = new List<GltfFile>();

        foreach (var g in mdx.Geosets)
        {
            if (g.LodId != options.Lod || g.VertexCount == 0 || g.Indices.Length < 3) continue;
            if (options.Geosets is not null && !options.Geosets.Contains(g.Index)) continue;
            if ((uint)g.MaterialId >= (uint)mdx.Materials.Count) continue;

            var composite = MaterialCompositor.Compose(mdx, mdx.Materials[g.MaterialId], textures,
                                                       modelCascName, options.TeamColor);
            int matIndex = materials.FindIndex(m => ReferenceEquals(m.Mat.Texture, composite.Texture)
                                                    && m.Mat.Blend == composite.Blend);
            if (matIndex < 0)
            {
                string stem = composite.PrimaryTexturePath.Length > 0
                    ? San(Path.GetFileNameWithoutExtension(composite.PrimaryTexturePath.Replace('/', '\\').Split('\\')[^1]))
                    : $"material{materials.Count:00}";
                string png = Unique(stem, pngs) + ".png";
                pngs.Add(new GltfFile(png, PngWriter.Write(composite.Texture)));
                materials.Add(($"{stem}", composite, png));
                matIndex = materials.Count - 1;
            }
            prims.Add(new Prim { Geoset = g, Material = composite, MaterialIndex = matIndex });
        }
        if (prims.Count == 0)
            throw new InvalidOperationException($"No geosets at LOD {options.Lod} — nothing to export.");

        // ---- nodes: [0] scene root (Y-up rotation), then one joint per MDX node ----
        var nodes = mdx.Nodes;
        int JointNode(int mdxIndex) => 1 + mdxIndex;

        json.BeginArray("nodes");
        {
            var rootChildren = new List<int> { 1 + nodes.Count };            // the mesh node
            for (int i = 0; i < nodes.Count; i++)
                if (_animator.ParentIndex(i) < 0) rootChildren.Add(JointNode(i));

            json.BeginObject();
            json.Str("name", options.ModelName);
            json.Raw("rotation", $"[{F(RootRotation.X)},{F(RootRotation.Y)},{F(RootRotation.Z)},{F(RootRotation.W)}]");
            json.Raw("children", $"[{string.Join(',', rootChildren)}]");
            json.EndObject();

            for (int i = 0; i < nodes.Count; i++)
            {
                var children = new List<int>();
                for (int c = 0; c < nodes.Count; c++)
                    if (_animator.ParentIndex(c) == i) children.Add(JointNode(c));

                var parentPivot = _animator.ParentIndex(i) >= 0 ? nodes[_animator.ParentIndex(i)].Pivot : Vector3.Zero;
                var rest = (nodes[i].Pivot - parentPivot) * scale;

                json.BeginObject();
                json.Str("name", nodes[i].Name.Length > 0 ? nodes[i].Name : $"node{i:000}");
                json.Raw("translation", $"[{F(rest.X)},{F(rest.Y)},{F(rest.Z)}]");
                if (children.Count > 0) json.Raw("children", $"[{string.Join(',', children)}]");
                json.EndObject();
            }

            json.BeginObject();                                              // the mesh node
            json.Str("name", options.ModelName + "_mesh");
            json.Num("mesh", 0);
            json.Num("skin", 0);
            json.EndObject();
        }
        json.EndArray();

        // ---- inverse bind matrices: inverse(rootRotation · T(pivot·scale)) ----
        var ibms = new Matrix4x4[nodes.Count];
        var rootMat = Matrix4x4.CreateFromQuaternion(RootRotation);
        for (int i = 0; i < nodes.Count; i++)
        {
            var bind = Matrix4x4.CreateTranslation(nodes[i].Pivot * scale) * rootMat;   // row-vector order
            Matrix4x4.Invert(bind, out ibms[i]);
        }
        int ibmAccessor = bin.Mat4Accessor(json, ibms);

        json.BeginArray("skins");
        json.BeginObject();
        json.Num("inverseBindMatrices", ibmAccessor);
        json.Raw("joints", $"[{string.Join(',', Enumerable.Range(0, nodes.Count).Select(JointNode))}]");
        json.EndObject();
        json.EndArray();

        // ---- mesh: one primitive per geoset ----
        json.BeginArray("meshes");
        json.BeginObject();
        json.Str("name", options.ModelName);
        json.BeginArray("primitives");
        foreach (var prim in prims)
        {
            var g = prim.Geoset;
            int n = g.VertexCount;

            var positions = new Vector3[n];
            for (int v = 0; v < n; v++) positions[v] = g.Positions[v] * scale;

            var normals = new Vector3[n];
            for (int v = 0; v < n; v++)
            {
                var nr = g.Normals.Length > v ? g.Normals[v] : Vector3.UnitZ;
                normals[v] = nr.LengthSquared() > 1e-10f ? Vector3.Normalize(nr) : Vector3.UnitZ;
            }

            var (joints, weights) = BuildInfluences(g);

            int posAcc = bin.Vec3Accessor(json, positions, withBounds: true);
            int nrmAcc = bin.Vec3Accessor(json, normals);
            int uvAcc = bin.Vec2Accessor(json, g.Uvs.Length >= n ? g.Uvs : new Vector2[n]);
            int jointAcc = bin.UShort4Accessor(json, joints);
            int weightAcc = bin.Vec4Accessor(json, weights);
            int idxAcc = bin.IndexAccessor(json, g.Indices);

            json.BeginObject();
            json.Raw("attributes", $"{{\"POSITION\":{posAcc},\"NORMAL\":{nrmAcc},\"TEXCOORD_0\":{uvAcc}," +
                                   $"\"JOINTS_0\":{jointAcc},\"WEIGHTS_0\":{weightAcc}}}");
            json.Num("indices", idxAcc);
            json.Num("material", prim.MaterialIndex);
            json.EndObject();
        }
        json.EndArray();
        json.EndObject();
        json.EndArray();

        // ---- materials & textures ----
        json.BeginArray("materials");
        foreach (var (name, mat, _) in materials)
        {
            json.BeginObject();
            json.Str("name", name);
            json.Raw("pbrMetallicRoughness",
                $"{{\"baseColorTexture\":{{\"index\":{materials.FindIndex(m => m.Name == name)}}}," +
                "\"metallicFactor\":0.0,\"roughnessFactor\":0.9}");
            if (mat.TwoSided) json.Bool("doubleSided", true);
            switch (mat.Blend)
            {
                case CompositeBlend.AlphaTest:
                    json.Str("alphaMode", "MASK");
                    json.Num("alphaCutoff", MaterialCompositor.CutoutThreshold);
                    break;
                case CompositeBlend.AlphaBlend or CompositeBlend.Additive:
                    json.Str("alphaMode", "BLEND");
                    break;
            }
            json.EndObject();
        }
        json.EndArray();

        json.BeginArray("textures");
        for (int i = 0; i < materials.Count; i++)
            json.Raw(null, $"{{\"source\":{i}}}");
        json.EndArray();

        json.BeginArray("images");
        foreach (var (_, _, png) in materials)
            json.Raw(null, $"{{\"uri\":\"{png}\"}}");
        json.EndArray();

        // ---- animations: one per sequence, baked LINEAR ----
        var wanted = M3Exporter.PlanSequences(mdx, options.Sequences, options.SequenceNames);
        if (wanted.Count > 0)
        {
            json.BeginArray("animations");
            foreach (var (_, seq, name) in wanted) BakeAnimation(json, bin, seq, name, JointNode);
            json.EndArray();
            _log.Add($"{wanted.Count} sequences baked at {options.Fps} fps");
        }

        // ---- scene / asset / buffer plumbing ----
        string binName = San(options.ModelName) + ".bin";
        json.Raw("scenes", "[{\"nodes\":[0]}]");
        json.Num("scene", 0);
        json.Raw("asset", "{\"version\":\"2.0\",\"generator\":\"Wc3ModelViewer\"}");
        json.Raw("buffers", $"[{{\"uri\":\"{binName}\",\"byteLength\":{bin.Length}}}]");
        bin.WriteViewsAndAccessors(json);

        var files = new List<GltfFile>
        {
            new(San(options.ModelName) + ".gltf", Encoding.UTF8.GetBytes(json.Build())),
            new(binName, bin.ToArray()),
        };
        files.AddRange(pngs);
        _log.Add($"{prims.Count} geosets, {nodes.Count} joints, {materials.Count} materials");

        // A loose custom model usually ships only its own art, so most of what just got packaged as
        // PNG came out of the game install. Say so, and name what could not be found at all — an
        // unresolved reference is written as a magenta placeholder, which looks like a real texture.
        var provenance = textures.ProvenanceOf(mdx, modelCascName, options.TeamColor);
        if (provenance.FromGameInstall > 0)
            _log.Add($"{provenance.FromGameInstall} texture(s) taken from the game install");
        if (provenance.Missing.Count > 0)
            _log.Add($"{provenance.Missing.Count} texture(s) not found anywhere — exported as magenta: "
                     + string.Join(", ", provenance.Missing.Take(5)));

        return new GltfExportResult { Files = files, Log = _log };
    }

    /// <summary>JOINTS_0 / WEIGHTS_0 for either skinning scheme.</summary>
    private (ushort[] Joints, Vector4[] Weights) BuildInfluences(MdxGeoset g)
    {
        int n = g.VertexCount;
        var joints = new ushort[n * 4];
        var weights = new Vector4[n];

        var boneChunk = Enumerable.Range(0, mdx.Nodes.Count)
                                  .Where(i => mdx.Nodes[i].Kind == MdxNodeKind.Bone)
                                  .ToArray();
        var byObjectId = new Dictionary<int, int>();
        for (int i = 0; i < mdx.Nodes.Count; i++) byObjectId.TryAdd(mdx.Nodes[i].ObjectId, i);

        Span<(int Joint, float W)> slots = stackalloc (int, float)[4];
        for (int v = 0; v < n; v++)
        {
            int used = 0;

            if (g.HasSkin)
            {
                for (int k = 0; k < 4; k++)
                {
                    int w8 = g.SkinBoneWeights[v * 4 + k];
                    if (w8 == 0) continue;
                    int chunkIndex = g.SkinBoneIndices[v * 4 + k];
                    if (chunkIndex >= boneChunk.Length) continue;
                    slots[used++] = (boneChunk[chunkIndex], w8 / 255f);
                }
            }
            else if (g.VertexGroups.Length > v && g.MatrixGroupSizes.Length > 0)
            {
                int grp = Math.Min(g.VertexGroups[v], g.MatrixGroupSizes.Length - 1);
                int at = 0;
                for (int gi = 0; gi < grp; gi++) at += g.MatrixGroupSizes[gi];
                int size = g.MatrixGroupSizes[grp];
                int take = Math.Min(size, 4);
                for (int k = 0; k < take && at + k < g.MatrixIndices.Length; k++)
                    if (byObjectId.TryGetValue(g.MatrixIndices[at + k], out int ni))
                        slots[used++] = (ni, 1f / take);
            }
            if (used == 0) slots[used++] = (0, 1f);

            float total = 0;
            for (int k = 0; k < used; k++) total += slots[k].W;
            var w4 = Vector4.Zero;
            for (int k = 0; k < used; k++)
            {
                joints[v * 4 + k] = (ushort)slots[k].Joint;
                float w = total > 0 ? slots[k].W / total : 0;
                w4 = k switch
                {
                    0 => w4 with { X = w }, 1 => w4 with { Y = w },
                    2 => w4 with { Z = w }, _ => w4 with { W = w },
                };
            }
            weights[v] = w4;
        }
        return (joints, weights);
    }

    private void BakeAnimation(Json json, BinWriter bin, MdxSequence seq, string name, Func<int, int> jointNode)
    {
        int step = Math.Max(1000 / Math.Max(options.Fps, 1), 10);
        var timesMs = new List<int>();
        for (int t = seq.IntervalStart; t < seq.IntervalEnd; t += step) timesMs.Add(t);
        timesMs.Add(seq.IntervalEnd);

        int n = mdx.Nodes.Count;
        var locs = new Vector3[n][];
        var rots = new Quaternion[n][];
        var scls = new Vector3[n][];
        for (int b = 0; b < n; b++)
        {
            locs[b] = new Vector3[timesMs.Count];
            rots[b] = new Quaternion[timesMs.Count];
            scls[b] = new Vector3[timesMs.Count];
        }

        for (int ti = 0; ti < timesMs.Count; ti++)
        {
            _animator.Evaluate(seq, timesMs[ti], timesMs[ti]);
            for (int b = 0; b < n; b++)
            {
                var (loc, rot, scale) = _animator.LocalTrs(b);
                locs[b][ti] = loc * options.Scale;
                rots[b][ti] = ti > 0 && Quaternion.Dot(rots[b][ti - 1], rot) < 0 ? -rot : rot;
                scls[b][ti] = scale;
            }
        }

        var seconds = timesMs.Select(t => (t - seq.IntervalStart) / 1000f).ToArray();
        int timeAcc = bin.ScalarAccessor(json, seconds, withBounds: true);

        json.BeginObject();
        json.Str("name", name);
        var samplers = new StringBuilder();
        var channels = new StringBuilder();
        int samplerCount = 0;

        for (int b = 0; b < n; b++)
        {
            var parentPivot = _animator.ParentIndex(b) >= 0 ? mdx.Nodes[_animator.ParentIndex(b)].Pivot : Vector3.Zero;
            var rest = (mdx.Nodes[b].Pivot - parentPivot) * options.Scale;

            // Constant-at-rest channels are culled, same rule as the m3 writer.
            if (locs[b].Any(v => (v - rest).Length() > 0.001f * Math.Max(options.Scale, 1)))
                AddChannel(json, bin, samplers, channels, ref samplerCount, timeAcc, jointNode(b),
                           "translation", bin.Vec3Accessor(json, locs[b]));
            if (rots[b].Any(q => Math.Min((q - Quaternion.Identity).Length(), (q + Quaternion.Identity).Length()) > 1e-3f))
                AddChannel(json, bin, samplers, channels, ref samplerCount, timeAcc, jointNode(b),
                           "rotation", bin.QuatAccessor(json, rots[b]));
            if (scls[b].Any(v => (v - Vector3.One).Length() > 1e-3f))
                AddChannel(json, bin, samplers, channels, ref samplerCount, timeAcc, jointNode(b),
                           "scale", bin.Vec3Accessor(json, scls[b]));
        }

        json.Raw("samplers", $"[{samplers}]");
        json.Raw("channels", $"[{channels}]");
        json.EndObject();
    }

    private static void AddChannel(Json json, BinWriter bin, StringBuilder samplers, StringBuilder channels,
                                   ref int samplerCount, int timeAcc, int node, string path, int outputAcc)
    {
        if (samplers.Length > 0) { samplers.Append(','); channels.Append(','); }
        samplers.Append($"{{\"input\":{timeAcc},\"output\":{outputAcc},\"interpolation\":\"LINEAR\"}}");
        channels.Append($"{{\"sampler\":{samplerCount},\"target\":{{\"node\":{node},\"path\":\"{path}\"}}}}");
        samplerCount++;
    }

    private static string San(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return sb.Length > 0 ? sb.ToString() : "model";
    }

    private static string Unique(string stem, List<GltfFile> existing)
    {
        string name = stem;
        for (int i = 2; existing.Any(f => f.FileName.Equals(name + ".png", StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{stem}_{i}";
        return name;
    }

    private static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- binary buffer

    /// <summary>Accumulates the .bin buffer and the bufferView/accessor JSON that describes it.</summary>
    private sealed class BinWriter
    {
        private readonly MemoryStream _ms = new();
        private readonly BinaryWriter _w;
        private readonly List<string> _views = [];
        private readonly List<string> _accessors = [];

        public BinWriter() => _w = new BinaryWriter(_ms);

        public long Length => _ms.Length;
        public byte[] ToArray() => _ms.ToArray();

        private int View(long start, long length, int? target = null)
        {
            _views.Add($"{{\"buffer\":0,\"byteOffset\":{start},\"byteLength\":{length}" +
                       (target is not null ? $",\"target\":{target}" : "") + "}");
            return _views.Count - 1;
        }

        private void Align(int to)
        {
            while (_ms.Length % to != 0) _w.Write((byte)0);
        }

        public int Vec3Accessor(Json _, Vector3[] data, bool withBounds = false)
        {
            Align(4);
            long start = _ms.Length;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var v in data)
            {
                _w.Write(v.X); _w.Write(v.Y); _w.Write(v.Z);
                min = Vector3.Min(min, v); max = Vector3.Max(max, v);
            }
            int view = View(start, _ms.Length - start, 34962);
            string bounds = withBounds && data.Length > 0
                ? $",\"min\":[{F(min.X)},{F(min.Y)},{F(min.Z)}],\"max\":[{F(max.X)},{F(max.Y)},{F(max.Z)}]"
                : "";
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{data.Length},\"type\":\"VEC3\"{bounds}}}");
            return _accessors.Count - 1;
        }

        public int Vec2Accessor(Json _, Vector2[] data)
        {
            Align(4);
            long start = _ms.Length;
            foreach (var v in data) { _w.Write(v.X); _w.Write(v.Y); }
            int view = View(start, _ms.Length - start, 34962);
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{data.Length},\"type\":\"VEC2\"}}");
            return _accessors.Count - 1;
        }

        public int Vec4Accessor(Json _, Vector4[] data)
        {
            Align(4);
            long start = _ms.Length;
            foreach (var v in data) { _w.Write(v.X); _w.Write(v.Y); _w.Write(v.Z); _w.Write(v.W); }
            int view = View(start, _ms.Length - start, 34962);
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{data.Length},\"type\":\"VEC4\"}}");
            return _accessors.Count - 1;
        }

        public int QuatAccessor(Json _, Quaternion[] data)
        {
            Align(4);
            long start = _ms.Length;
            foreach (var q in data) { _w.Write(q.X); _w.Write(q.Y); _w.Write(q.Z); _w.Write(q.W); }
            int view = View(start, _ms.Length - start);
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{data.Length},\"type\":\"VEC4\"}}");
            return _accessors.Count - 1;
        }

        public int ScalarAccessor(Json _, float[] data, bool withBounds = false)
        {
            Align(4);
            long start = _ms.Length;
            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in data) { _w.Write(v); min = Math.Min(min, v); max = Math.Max(max, v); }
            int view = View(start, _ms.Length - start);
            string bounds = withBounds && data.Length > 0 ? $",\"min\":[{F(min)}],\"max\":[{F(max)}]" : "";
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{data.Length},\"type\":\"SCALAR\"{bounds}}}");
            return _accessors.Count - 1;
        }

        public int UShort4Accessor(Json _, ushort[] data)
        {
            Align(4);
            long start = _ms.Length;
            foreach (ushort v in data) _w.Write(v);
            int view = View(start, _ms.Length - start, 34962);
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5123,\"count\":{data.Length / 4},\"type\":\"VEC4\"}}");
            return _accessors.Count - 1;
        }

        public int IndexAccessor(Json _, int[] indices)
        {
            Align(4);
            long start = _ms.Length;
            foreach (int i in indices) _w.Write((ushort)i);
            int view = View(start, _ms.Length - start, 34963);
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5123,\"count\":{indices.Length},\"type\":\"SCALAR\"}}");
            return _accessors.Count - 1;
        }

        public int Mat4Accessor(Json _, Matrix4x4[] data)
        {
            Align(4);
            long start = _ms.Length;
            foreach (var m in data)
            {
                // glTF matrices are column-major; System.Numerics row-vector matrices lay out in
                // memory exactly as glTF expects (basis rows become columns of the column-major view).
                _w.Write(m.M11); _w.Write(m.M12); _w.Write(m.M13); _w.Write(m.M14);
                _w.Write(m.M21); _w.Write(m.M22); _w.Write(m.M23); _w.Write(m.M24);
                _w.Write(m.M31); _w.Write(m.M32); _w.Write(m.M33); _w.Write(m.M34);
                _w.Write(m.M41); _w.Write(m.M42); _w.Write(m.M43); _w.Write(m.M44);
            }
            int view = View(start, _ms.Length - start);
            _accessors.Add($"{{\"bufferView\":{view},\"componentType\":5126,\"count\":{data.Length},\"type\":\"MAT4\"}}");
            return _accessors.Count - 1;
        }

        public void WriteViewsAndAccessors(Json json)
        {
            json.Raw("bufferViews", $"[{string.Join(',', _views)}]");
            json.Raw("accessors", $"[{string.Join(',', _accessors)}]");
        }
    }

    // ---------------------------------------------------------------- tiny JSON builder

    /// <summary>Order-preserving JSON assembly without a serializer dependency.</summary>
    private sealed class Json
    {
        private readonly StringBuilder _sb = new("{");
        private bool _needComma;

        private void Comma()
        {
            if (_needComma) _sb.Append(',');
            _needComma = true;
        }

        public void BeginArray(string name) { Comma(); _sb.Append($"\"{name}\":["); _needComma = false; }
        public void EndArray() { _sb.Append(']'); _needComma = true; }
        public void BeginObject() { Comma(); _sb.Append('{'); _needComma = false; }
        public void EndObject() { _sb.Append('}'); _needComma = true; }

        public void Str(string name, string value)
        {
            Comma();
            _sb.Append($"\"{name}\":\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        }

        public void Num(string name, double value)
        {
            Comma();
            _sb.Append($"\"{name}\":{value.ToString("R", CultureInfo.InvariantCulture)}");
        }

        public void Bool(string name, bool value)
        {
            Comma();
            _sb.Append($"\"{name}\":{(value ? "true" : "false")}");
        }

        public void Raw(string? name, string rawJson)
        {
            Comma();
            _sb.Append(name is null ? rawJson : $"\"{name}\":{rawJson}");
        }

        public string Build() => _sb.ToString() + "}";
    }
}
