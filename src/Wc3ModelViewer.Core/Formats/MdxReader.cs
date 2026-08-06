using System.Numerics;
using System.Text;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>
/// Reads a Warcraft III <c>.mdx</c> model, classic (SD) or Reforged (HD).
/// </summary>
/// <remarks>
/// Written against <c>docs/mdx-format-verified.md</c>, which was derived by parsing the real files of
/// Reforged build 2.0.4.23745 rather than from the published specifications — those describe older
/// builds and disagree with what ships today.
/// <para>
/// The two rules that matter: <b>the version field does not determine the layout</b> (SD and HD both
/// report VERS 1200, and are told apart structurally), and <b>optional keyframe tracks are found by
/// consuming tags until the owning object's inclusive size runs out</b>, never by peeking for a known
/// tag and rewinding — the latter breaks on any track the reader does not know.
/// </para>
/// </remarks>
public static class MdxReader
{
    public static MdxModel Read(byte[] data)
    {
        if (data.Length < 8 || Tag(data, 0) != "MDLX")
            throw new InvalidDataException("Not an MDX model (missing the MDLX magic). MDL text models are not supported.");

        var model = new MdxModel();
        int p = 4;
        while (p + 8 <= data.Length)
        {
            string tag = Tag(data, p);
            int size = BitConverter.ToInt32(data, p + 4);
            if (size < 0 || p + 8 + size > data.Length)
                throw new InvalidDataException($"Chunk '{tag}' at {p} declares {size} bytes, which overruns the file.");

            var r = new Cursor(data, p + 8, size);
            switch (tag)
            {
                case "VERS": model.Version = r.U32(); break;
                case "MODL": ReadModl(model, r); break;
                case "SEQS": ReadSeqs(model, r); break;
                case "GLBS": while (r.More) model.GlobalSequences.Add(r.U32()); break;
                case "MTLS": ReadMtls(model, r); break;
                case "TEXS": ReadTexs(model, r); break;
                case "GEOS": ReadGeos(model, r); break;
                case "GEOA": ReadGeoa(model, r); break;
                case "BONE": ReadNodes(model, r, MdxNodeKind.Bone); break;
                case "HELP": ReadNodes(model, r, MdxNodeKind.Helper); break;
                case "ATCH": ReadNodes(model, r, MdxNodeKind.Attachment); break;
                case "EVTS": ReadNodes(model, r, MdxNodeKind.Event); break;
                case "CLID": ReadNodes(model, r, MdxNodeKind.CollisionShape); break;
                case "PIVT": while (r.More) model.Pivots.Add(r.Vec3()); break;
                case "CAMS": ReadCams(model, r); break;
                default: model.SkippedChunks.Add($"{tag}({size})"); break;
            }
            p += 8 + size;
        }

        ResolvePivots(model);
        return model;
    }

    /// <summary>
    /// Copies each node's rest position out of PIVT. Warcraft III bones carry no rest rotation or
    /// scale: the pivot is the whole rest pose, and animation keys are offsets from it.
    /// </summary>
    private static void ResolvePivots(MdxModel model)
    {
        foreach (var node in model.Nodes)
            if ((uint)node.ObjectId < (uint)model.Pivots.Count)
                node.Pivot = model.Pivots[node.ObjectId];
    }

    // ---------------------------------------------------------------- chunks

    private static void ReadModl(MdxModel m, Cursor r)
    {
        m.Name = r.FixedString(80);
        m.AnimationFile = r.FixedString(260);
        (m.BoundsRadius, m.Min, m.Max) = r.Bounds();
        if (r.Remaining >= 4) m.BlendTime = r.U32();
    }

    private static void ReadSeqs(MdxModel m, Cursor r)
    {
        while (r.Remaining >= 132)
        {
            string name = r.FixedString(80);
            int start = r.I32(), end = r.I32();
            float moveSpeed = r.F32();
            uint flags = r.U32();
            float rarity = r.F32();
            uint syncPoint = r.U32();
            var (radius, min, max) = r.Bounds();
            m.Sequences.Add(new MdxSequence
            {
                Name = name, IntervalStart = start, IntervalEnd = end, MoveSpeed = moveSpeed,
                Flags = flags, Rarity = rarity, SyncPoint = syncPoint,
                BoundsRadius = radius, Min = min, Max = max,
            });
        }
    }

    private static void ReadTexs(MdxModel m, Cursor r)
    {
        while (r.Remaining >= 268)
            m.Textures.Add(new MdxTexture
            {
                ReplaceableId = r.U32(),
                FileName = r.FixedString(260),
                Flags = r.U32(),
            });
    }

    private static void ReadMtls(MdxModel m, Cursor r)
    {
        while (r.Remaining >= 12)
        {
            int start = r.Position;
            int inclusive = r.I32();
            if (inclusive < 12 || start + inclusive > r.End) break;
            var body = r.Sub(start, inclusive);
            body.Skip(4);                                   // the inclusive size just read

            int priorityPlane = body.I32();
            uint flags = body.U32();

            // Every material in build 2.0.4 puts LAYS here, SD and HD alike. Older Reforged builds
            // inserted a char[80] shader name first, so tolerate that rather than fail.
            if (body.PeekTag() != "LAYS" && body.Remaining >= 84 && body.PeekTag(80) == "LAYS")
                body.Skip(80);

            var layers = new List<MdxLayer>();
            if (body.PeekTag() == "LAYS")
            {
                body.Skip(4);
                int layerCount = body.I32();
                for (int i = 0; i < layerCount && body.Remaining >= 4; i++)
                {
                    int layerStart = body.Position;
                    int layerSize = body.I32();
                    if (layerSize < 8 || layerStart + layerSize > body.End) break;
                    layers.Add(ReadLayer(body.Sub(layerStart, layerSize)));
                    body.Seek(layerStart + layerSize);
                }
            }

            m.Materials.Add(new MdxMaterial { PriorityPlane = priorityPlane, Flags = flags, Layers = layers });
            r.Seek(start + inclusive);
        }
    }

    /// <summary>
    /// Reads one material layer. The Reforged HD form adds seven PBR floats and a texture-slot table
    /// binding diffuse/normal/ORM/emissive/team-colour/environment to TEXS indices — the table that
    /// older tools skip as "56 unknown bytes". See <c>docs/mdx-format-verified.md</c> §4.
    /// </summary>
    private static MdxLayer ReadLayer(Cursor r)
    {
        r.Skip(4);                                          // inclusive size
        var filterMode = (MdxFilterMode)r.I32();
        var shading = (MdxShadingFlags)r.I32();
        int textureId = r.I32();
        int texAnimId = r.I32();
        int coordId = r.I32();
        float alpha = r.F32();

        float emissive = 0, fresnelMul = 0, teamColorMul = 0;
        var fresnel = Vector3.Zero;
        var slots = new Dictionary<MdxTextureSlot, int>();

        // The classic layer stops after alpha; the Reforged one continues with six more floats.
        // A track tag here means this is a classic layer whose optional tracks have begun.
        if (r.Remaining >= 24 && !IsTrackTag(r.PeekTag()))
        {
            emissive = r.F32();
            fresnel = new Vector3(r.F32(), r.F32(), r.F32());
            fresnelMul = r.F32();
            teamColorMul = r.F32();

            // ... and then, on HD layers only, the slot table: unknown, slotCount, (textureId, slot)*.
            if (r.Remaining >= 8 && !IsTrackTag(r.PeekTag()))
            {
                r.Skip(4);                                  // observed 1
                int slotCount = r.I32();
                if ((uint)slotCount <= 16 && r.Remaining >= slotCount * 8)
                    for (int i = 0; i < slotCount; i++)
                    {
                        int texId = r.I32();
                        int slot = r.I32();
                        if ((uint)slot <= (uint)MdxTextureSlot.EnvironmentMap)
                            slots[(MdxTextureSlot)slot] = texId;
                    }
            }
        }

        MdxTrack<float>? alphaTrack = null, emissiveTrack = null, texIdTrack = null;
        while (r.Remaining >= 8)
        {
            string tag = r.PeekTag();
            switch (tag)
            {
                case "KMTA": alphaTrack = ReadFloatTrack(r); continue;
                case "KMTE": emissiveTrack = ReadFloatTrack(r); continue;
                case "KMTF": texIdTrack = ReadFloatTrack(r, asInt: true); continue;
                default: r.SkipToEnd(); break;
            }
            break;
        }

        return new MdxLayer
        {
            FilterMode = filterMode, ShadingFlags = shading, TextureId = textureId,
            TextureAnimationId = texAnimId, CoordId = coordId, Alpha = alpha,
            EmissiveMultiplier = emissive, FresnelColor = fresnel, FresnelMultiplier = fresnelMul,
            TeamColorMultiplier = teamColorMul, TextureSlots = slots,
            AlphaTrack = alphaTrack, EmissiveTrack = emissiveTrack, TextureIdTrack = texIdTrack,
        };
    }

    private static void ReadGeos(MdxModel m, Cursor r)
    {
        int index = 0;
        while (r.Remaining >= 8)
        {
            int start = r.Position;
            int inclusive = r.I32();
            if (inclusive < 8 || start + inclusive > r.End) break;
            var g = r.Sub(start, inclusive);
            g.Skip(4);
            m.Geosets.Add(ReadGeoset(g, index++));
            r.Seek(start + inclusive);
        }
    }

    private static MdxGeoset ReadGeoset(Cursor r, int index)
    {
        Vector3[] positions = [], normals = [];
        int[] indices = [], matrixGroups = [], matrixIndices = [];
        byte[] vertexGroups = [], skinIndices = [], skinWeights = [];
        Vector4[] tangents = [];
        var uvLayers = new List<Vector2[]>();
        int materialId = 0, sectionGroupId = 0, sectionGroupType = 0, lodId = 0;
        string lodName = "";
        float radius = 0;
        Vector3 min = default, max = default;

        while (r.Remaining >= 8)
        {
            string tag = r.PeekTag();
            switch (tag)
            {
                case "VRTX": r.Skip(4); positions = r.Vec3Array(r.I32()); break;
                case "NRMS": r.Skip(4); normals = r.Vec3Array(r.I32()); break;
                case "PTYP": r.Skip(4); r.Skip(r.I32() * 4); break;
                case "PCNT": r.Skip(4); r.Skip(r.I32() * 4); break;
                case "PVTX": r.Skip(4); indices = r.U16ArrayAsInt(r.I32()); break;
                case "GNDX": r.Skip(4); vertexGroups = r.Bytes(r.I32()); break;
                case "MTGC": r.Skip(4); matrixGroups = r.I32Array(r.I32()); break;
                case "TANG": r.Skip(4); tangents = r.Vec4Array(r.I32()); break;

                case "SKIN":
                    // SKIN's header value is a BYTE count, not an element count: 8 bytes per vertex,
                    // four bone indices followed by four 0..255 weights.
                    r.Skip(4);
                    int skinBytes = r.I32();
                    var skin = r.Bytes(skinBytes);
                    int verts = skin.Length / 8;
                    skinIndices = new byte[verts * 4];
                    skinWeights = new byte[verts * 4];
                    for (int v = 0; v < verts; v++)
                    {
                        Array.Copy(skin, v * 8, skinIndices, v * 4, 4);
                        Array.Copy(skin, v * 8 + 4, skinWeights, v * 4, 4);
                    }
                    break;

                case "MATS":
                    // MATS is followed by the geoset's trailing fixed fields, which carry no tag.
                    r.Skip(4);
                    matrixIndices = r.I32Array(r.I32());
                    materialId = r.I32();
                    sectionGroupId = r.I32();
                    sectionGroupType = r.I32();
                    lodId = r.I32();
                    lodName = r.FixedString(80);
                    (radius, min, max) = r.Bounds();
                    int extentCount = r.I32();
                    r.Skip(extentCount * 28);
                    break;

                case "UVAS":
                    r.Skip(4);
                    int layerCount = r.I32();
                    for (int i = 0; i < layerCount && r.PeekTag() == "UVBS"; i++)
                    {
                        r.Skip(4);
                        uvLayers.Add(r.Vec2Array(r.I32()));
                    }
                    break;

                default:
                    r.SkipToEnd();
                    break;
            }
        }

        return new MdxGeoset
        {
            Index = index,
            Positions = positions, Normals = normals, Indices = indices, UvLayers = uvLayers,
            VertexGroups = vertexGroups, MatrixGroupSizes = matrixGroups, MatrixIndices = matrixIndices,
            SkinBoneIndices = skinIndices, SkinBoneWeights = skinWeights, Tangents = tangents,
            MaterialId = materialId, SectionGroupId = sectionGroupId, SectionGroupType = sectionGroupType,
            LodId = lodId, LodName = lodName, BoundsRadius = radius, Min = min, Max = max,
        };
    }

    private static void ReadGeoa(MdxModel m, Cursor r)
    {
        while (r.Remaining >= 28)
        {
            int start = r.Position;
            int inclusive = r.I32();
            if (inclusive < 28 || start + inclusive > r.End) break;
            var g = r.Sub(start, inclusive);
            g.Skip(4);

            float alpha = g.F32();
            uint flags = g.U32();
            var color = new Vector3(g.F32(), g.F32(), g.F32());   // stored B,G,R in the binary format
            int geosetId = g.I32();

            MdxTrack<float>? alphaTrack = null;
            MdxTrack<Vector3>? colorTrack = null;
            while (g.Remaining >= 8)
            {
                switch (g.PeekTag())
                {
                    case "KGAO": alphaTrack = ReadFloatTrack(g); continue;
                    case "KGAC": colorTrack = ReadVec3Track(g); continue;
                }
                break;
            }

            m.GeosetAnims.Add(new MdxGeosetAnim
            {
                Alpha = alpha, Flags = flags,
                Color = new Vector3(color.Z, color.Y, color.X),   // -> R,G,B
                GeosetId = geosetId, AlphaTrack = alphaTrack, ColorTrack = colorTrack,
            });
            r.Seek(start + inclusive);
        }
    }

    /// <summary>
    /// Reads a chunk of nodes. Every node shares the same header; the extra fields that follow it
    /// depend on the kind, and are skipped by trusting the node's inclusive size.
    /// </summary>
    private static void ReadNodes(MdxModel m, Cursor r, MdxNodeKind kind)
    {
        while (r.Remaining >= 4)
        {
            int start = r.Position;

            // BONE entries are a bare node followed by two ints, with no outer inclusive size; every
            // other node kind is led by one. Detect by looking at where the name would start.
            bool hasOuterSize = kind is not (MdxNodeKind.Bone or MdxNodeKind.Helper);
            int inclusive = hasOuterSize ? r.I32() : 0;
            int nodeStart = hasOuterSize ? r.Position : start;

            int nodeSize = r.PeekI32(nodeStart);
            if (nodeSize < 96 || nodeStart + nodeSize > r.End) break;

            var n = r.Sub(nodeStart, nodeSize);
            n.Skip(4);
            string name = n.FixedString(80);
            int objectId = n.I32();
            int parentId = n.I32();
            var flags = (MdxNodeFlags)n.U32();

            MdxTrack<Vector3>? translation = null, scale = null;
            MdxTrack<Quaternion>? rotation = null;
            while (n.Remaining >= 8)
            {
                switch (n.PeekTag())
                {
                    case "KGTR": translation = ReadVec3Track(n); continue;
                    case "KGRT": rotation = ReadQuatTrack(n); continue;
                    case "KGSC": scale = ReadVec3Track(n); continue;
                }
                break;
            }

            int after = nodeStart + nodeSize;
            int geosetId = -1, geosetAnimId = -1, attachmentId = -1;
            string attachPath = "";

            if (kind == MdxNodeKind.Bone)
            {
                geosetId = r.PeekI32(after);
                geosetAnimId = r.PeekI32(after + 4);
                after += 8;
            }
            else if (kind == MdxNodeKind.Attachment && after + 264 <= r.End)
            {
                var a = r.Sub(after, 264);
                attachPath = a.FixedString(260);
                attachmentId = a.I32();
            }

            m.Nodes.Add(new MdxNode
            {
                Name = name, ObjectId = objectId, ParentId = parentId, Flags = flags, Kind = kind,
                Translation = translation, Rotation = rotation, Scale = scale,
                GeosetId = geosetId, GeosetAnimId = geosetAnimId,
                AttachmentId = attachmentId, AttachmentPath = attachPath,
            });

            r.Seek(hasOuterSize && inclusive >= 4 ? start + inclusive : after);
            if (r.Position <= start) break;                 // never loop on a malformed entry
        }
    }

    private static void ReadCams(MdxModel m, Cursor r)
    {
        while (r.Remaining >= 4)
        {
            int start = r.Position;
            int inclusive = r.I32();
            if (inclusive < 120 || start + inclusive > r.End) break;
            var c = r.Sub(start, inclusive);
            c.Skip(4);

            string name = c.FixedString(80);
            var pos = c.Vec3();
            float fov = c.F32(), far = c.F32(), near = c.F32();
            var target = c.Vec3();

            MdxTrack<Vector3>? tr = null, tt = null;
            MdxTrack<float>? roll = null;
            while (c.Remaining >= 8)
            {
                switch (c.PeekTag())
                {
                    case "KCTR": tr = ReadVec3Track(c); continue;
                    case "KTTR": tt = ReadVec3Track(c); continue;
                    case "KCRL": roll = ReadFloatTrack(c); continue;
                }
                break;
            }

            m.Cameras.Add(new MdxCamera
            {
                Name = name, Position = pos, FieldOfView = fov, FarClip = far, NearClip = near,
                TargetPosition = target, TranslationTrack = tr, TargetTranslationTrack = tt, RollTrack = roll,
            });
            r.Seek(start + inclusive);
        }
    }

    // ---------------------------------------------------------------- tracks

    /// <summary>Track tags all start with 'K' and are four uppercase letters.</summary>
    private static bool IsTrackTag(string tag) =>
        tag.Length == 4 && tag[0] == 'K' && tag.All(c => c is >= 'A' and <= 'Z');

    private static (MdxInterpolation Interp, int GlobalSeq, int Count, string Tag) TrackHeader(Cursor r)
    {
        string tag = r.Tag4();
        int count = r.I32();
        var interp = (MdxInterpolation)r.I32();
        int globalSeq = r.I32();
        return (interp, globalSeq, count, tag);
    }

    private static MdxTrack<float> ReadFloatTrack(Cursor r, bool asInt = false)
    {
        var (interp, gs, count, tag) = TrackHeader(r);
        bool tangents = interp > MdxInterpolation.Linear;
        var times = new int[count];
        var vals = new float[count];
        var inT = tangents ? new float[count] : null;
        var outT = tangents ? new float[count] : null;
        for (int i = 0; i < count; i++)
        {
            times[i] = r.I32();
            vals[i] = asInt ? r.I32() : r.F32();
            if (!tangents) continue;
            inT![i] = asInt ? r.I32() : r.F32();
            outT![i] = asInt ? r.I32() : r.F32();
        }
        return new MdxTrack<float> { Tag = tag, Interpolation = interp, GlobalSequenceId = gs, Times = times, Values = vals, InTangents = inT, OutTangents = outT };
    }

    private static MdxTrack<Vector3> ReadVec3Track(Cursor r)
    {
        var (interp, gs, count, tag) = TrackHeader(r);
        bool tangents = interp > MdxInterpolation.Linear;
        var times = new int[count];
        var vals = new Vector3[count];
        var inT = tangents ? new Vector3[count] : null;
        var outT = tangents ? new Vector3[count] : null;
        for (int i = 0; i < count; i++)
        {
            times[i] = r.I32();
            vals[i] = r.Vec3();
            if (!tangents) continue;
            inT![i] = r.Vec3();
            outT![i] = r.Vec3();
        }
        return new MdxTrack<Vector3> { Tag = tag, Interpolation = interp, GlobalSequenceId = gs, Times = times, Values = vals, InTangents = inT, OutTangents = outT };
    }

    private static MdxTrack<Quaternion> ReadQuatTrack(Cursor r)
    {
        var (interp, gs, count, tag) = TrackHeader(r);
        bool tangents = interp > MdxInterpolation.Linear;
        var times = new int[count];
        var vals = new Quaternion[count];
        var inT = tangents ? new Quaternion[count] : null;
        var outT = tangents ? new Quaternion[count] : null;
        for (int i = 0; i < count; i++)
        {
            times[i] = r.I32();
            vals[i] = r.Quat();
            if (!tangents) continue;
            inT![i] = r.Quat();
            outT![i] = r.Quat();
        }
        return new MdxTrack<Quaternion> { Tag = tag, Interpolation = interp, GlobalSequenceId = gs, Times = times, Values = vals, InTangents = inT, OutTangents = outT };
    }

    private static string Tag(byte[] b, int at) => Encoding.ASCII.GetString(b, at, 4);

    // ---------------------------------------------------------------- cursor

    /// <summary>A bounds-checked read head over a slice of the file.</summary>
    private sealed class Cursor(byte[] data, int start, int length)
    {
        private readonly byte[] _d = data;

        public int Position { get; private set; } = start;
        public int End { get; } = start + length;

        public int Remaining => End - Position;
        public bool More => Remaining > 0;

        public void Seek(int position) => Position = Math.Clamp(position, 0, End);
        public void Skip(int count) => Position = Math.Min(Position + Math.Max(count, 0), End);
        public void SkipToEnd() => Position = End;
        public Cursor Sub(int at, int len) => new(_d, at, Math.Min(len, _d.Length - at));

        public uint U32() { uint v = BitConverter.ToUInt32(_d, Position); Position += 4; return v; }
        public int I32() { int v = BitConverter.ToInt32(_d, Position); Position += 4; return v; }
        public float F32() { float v = BitConverter.ToSingle(_d, Position); Position += 4; return v; }
        public int PeekI32(int at) => at + 4 <= _d.Length ? BitConverter.ToInt32(_d, at) : 0;

        public string Tag4() { string s = Encoding.ASCII.GetString(_d, Position, 4); Position += 4; return s; }
        public string PeekTag(int ahead = 0) =>
            Position + ahead + 4 <= End ? Encoding.ASCII.GetString(_d, Position + ahead, 4) : "";

        public Vector3 Vec3() { var v = new Vector3(BitConverter.ToSingle(_d, Position), BitConverter.ToSingle(_d, Position + 4), BitConverter.ToSingle(_d, Position + 8)); Position += 12; return v; }
        public Quaternion Quat() { var q = new Quaternion(BitConverter.ToSingle(_d, Position), BitConverter.ToSingle(_d, Position + 4), BitConverter.ToSingle(_d, Position + 8), BitConverter.ToSingle(_d, Position + 12)); Position += 16; return q; }

        /// <summary>Reads the 28-byte bounds block: radius first, then min, then max.</summary>
        public (float Radius, Vector3 Min, Vector3 Max) Bounds()
        {
            float radius = F32();
            return (radius, Vec3(), Vec3());
        }

        public string FixedString(int len)
        {
            int n = 0;
            while (n < len && Position + n < End && _d[Position + n] != 0) n++;
            string s = Encoding.UTF8.GetString(_d, Position, n);
            Position = Math.Min(Position + len, End);
            return s;
        }

        public byte[] Bytes(int count)
        {
            count = Math.Clamp(count, 0, Remaining);
            var a = new byte[count];
            Array.Copy(_d, Position, a, 0, count);
            Position += count;
            return a;
        }

        public int[] I32Array(int count)
        {
            count = Math.Clamp(count, 0, Remaining / 4);
            var a = new int[count];
            for (int i = 0; i < count; i++) a[i] = I32();
            return a;
        }

        public int[] U16ArrayAsInt(int count)
        {
            count = Math.Clamp(count, 0, Remaining / 2);
            var a = new int[count];
            for (int i = 0; i < count; i++) { a[i] = BitConverter.ToUInt16(_d, Position); Position += 2; }
            return a;
        }

        public Vector2[] Vec2Array(int count)
        {
            count = Math.Clamp(count, 0, Remaining / 8);
            var a = new Vector2[count];
            for (int i = 0; i < count; i++) { a[i] = new Vector2(BitConverter.ToSingle(_d, Position), BitConverter.ToSingle(_d, Position + 4)); Position += 8; }
            return a;
        }

        public Vector3[] Vec3Array(int count)
        {
            count = Math.Clamp(count, 0, Remaining / 12);
            var a = new Vector3[count];
            for (int i = 0; i < count; i++) a[i] = Vec3();
            return a;
        }

        public Vector4[] Vec4Array(int count)
        {
            count = Math.Clamp(count, 0, Remaining / 16);
            var a = new Vector4[count];
            for (int i = 0; i < count; i++) { a[i] = new Vector4(BitConverter.ToSingle(_d, Position), BitConverter.ToSingle(_d, Position + 4), BitConverter.ToSingle(_d, Position + 8), BitConverter.ToSingle(_d, Position + 12)); Position += 16; }
            return a;
        }
    }
}
