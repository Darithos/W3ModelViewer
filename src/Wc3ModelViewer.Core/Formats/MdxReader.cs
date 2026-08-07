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
                case "PRE2": ReadNodes(model, r, MdxNodeKind.ParticleEmitter2,
                                       (p, i) => ReadPre2(model, p, i)); break;
                case "PREM": ReadNodes(model, r, MdxNodeKind.ParticleEmitter,
                                       (p, i) => ReadPrem(model, p, i)); break;
                case "RIBB": ReadNodes(model, r, MdxNodeKind.RibbonEmitter,
                                       (p, i) => ReadRibb(model, p, i)); break;
                case "LITE": ReadNodes(model, r, MdxNodeKind.Light,
                                       (p, i) => ReadLite(model, p, i)); break;
                // Reforged's PopcornFX emitters reference external baked .pkb effects through a
                // third-party runtime. Nothing here converts, so only the count is kept — enough to
                // tell the user what was dropped instead of silently losing a third of the effects.
                case "CORN": model.PopcornEmitterCount += CountCornEmitters(r); break;
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
    /// <summary>
    /// Reads a chunk of hierarchy nodes. <paramref name="payload"/> lets an emitter chunk claim the
    /// bytes between the end of the generic node and the end of its entry: emitters are nodes first
    /// (they carry a transform and a place in the hierarchy) and effect data second.
    /// </summary>
    private static void ReadNodes(MdxModel m, Cursor r, MdxNodeKind kind,
                                  Action<Cursor, int>? payload = null)
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

            int entryEnd = hasOuterSize && inclusive >= 4 ? start + inclusive : after;
            if (payload is not null && entryEnd > after && entryEnd <= r.End)
            {
                // Hand the emitter its own bytes. A malformed payload must not desync the node
                // walk, so the cursor is a sub-view and we seek past it either way.
                try { payload(r.Sub(after, entryEnd - after), m.Nodes.Count - 1); }
                catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException
                                              or IndexOutOfRangeException) { }
            }

            r.Seek(entryEnd);
            if (r.Position <= start) break;                 // never loop on a malformed entry
        }
    }

    // ---------------------------------------------------------------- effects

    /// <summary>
    /// PRE2 particle emitter payload — 171 bytes, then the tracks.
    /// </summary>
    /// <remarks>
    /// The published spec describes two forms of this struct, a "classic" one carrying an emitter
    /// shape, longitude, zsource, two 260-byte model paths and trailing twinkle/tumble/wind/spline
    /// blocks, and a shorter Reforged one. <b>Build 2.0.4 ships only the short form, in both the SD
    /// and HD trees.</b> Verified against real bytes (<c>abilities\weapons\boatmissile</c>, SD): the
    /// fields decode as a fire gradient — blend Add, an 8×8 sprite sheet, alphas 255/255/0 — and the
    /// first track tag <c>KP2V</c> lands exactly 171 bytes in, which is only true with
    /// <c>squirt</c> present and none of the classic block. Reading the spec's long form here would
    /// desync every field. This matches the project's standing finding that Reforged 2.0 does not
    /// match any published spec; trust the bytes.
    /// </remarks>
    private static void ReadPre2(MdxModel m, Cursor p, int nodeIndex)
    {
        float speed = p.F32(), variation = p.F32(), latitude = p.F32(), gravity = p.F32();
        float life = p.F32(), emissionRate = p.F32(), length = p.F32(), width = p.F32();
        var blend = (MdxParticleBlend)p.U32();

        int rows = p.I32(), cols = p.I32();
        var type = (MdxParticleType)p.U32();
        float tailLength = p.F32(), middleTime = p.F32();

        Vector3 c0 = p.Vec3(), c1 = p.Vec3(), c2 = p.Vec3();
        byte a0 = p.U8(), a1 = p.U8(), a2 = p.U8();
        float s0 = p.F32(), s1 = p.F32(), s2 = p.F32();     // unaligned by the three alpha bytes
        p.Skip(48);                                         // 12 uint32 head/tail UV animation ranges

        int textureId = p.I32();
        bool squirt = p.I32() != 0;
        int priorityPlane = p.I32();
        int replaceableId = p.I32();

        var flags = m.Nodes[nodeIndex].Flags;
        var e = new MdxParticleEmitter2
        {
            Name = m.Nodes[nodeIndex].Name, NodeIndex = nodeIndex,
            Speed = speed, Variation = variation, Latitude = latitude,
            Gravity = gravity, Life = life, EmissionRate = emissionRate,
            Length = length, Width = width,
            Blend = blend, Rows = Math.Max(1, rows), Columns = Math.Max(1, cols),
            ParticleType = type, TailLength = tailLength, MiddleTime = middleTime,
            StartColor = c0, MiddleColor = c1, EndColor = c2,
            StartAlpha = a0, MiddleAlpha = a1, EndAlpha = a2,
            StartScale = s0, MiddleScale = s1, EndScale = s2,
            TextureId = textureId, PriorityPlane = priorityPlane, ReplaceableId = replaceableId,
            Squirt = squirt,
            Unshaded = (flags & MdxNodeFlags.ParticleUnshaded) != 0,
            Unfogged = (flags & MdxNodeFlags.ParticleUnfogged) != 0,
            ModelSpace = (flags & MdxNodeFlags.ParticleModelSpace) != 0,
            LineEmitter = (flags & MdxNodeFlags.LineEmitter) != 0,
            SpeedTrack = FindFloatTrack(p, "KP2S"),
            VariationTrack = FindFloatTrack(p, "KP2R"),
            LatitudeTrack = FindFloatTrack(p, "KP2L"),
            GravityTrack = FindFloatTrack(p, "KP2G"),
            LifeTrack = FindFloatTrack(p, "KLIF"),
            EmissionRateTrack = FindFloatTrack(p, "KP2E"),
            WidthTrack = FindFloatTrack(p, "KP2W"),
            LengthTrack = FindFloatTrack(p, "KP2N"),
            VisibilityTrack = FindFloatTrack(p, "KP2V") ?? FindFloatTrack(p, "KVIS"),
        };
        m.ParticleEmitters.Add(e);
    }

    /// <summary>
    /// RIBB ribbon emitter payload — 52 bytes, then the tracks.
    /// </summary>
    /// <remarks>
    /// The published spec puts an <c>emitterSize</c> uint32 in front of this block. Build 2.0.4 does
    /// not: the payload begins directly at <c>heightAbove</c>. Reading the phantom field shifts
    /// every subsequent one by four bytes, which is nearly invisible — the shifted colour channels
    /// still land in 0..1 and the shifted material id still reads 0 whenever gravity is 0. It was
    /// only caught because <c>MdxProbe --fx</c> range-checks every field across the whole game and
    /// <c>tinkerrocketmissile</c> has gravity 50, which surfaced as a material id of 1112014848.
    /// </remarks>
    private static void ReadRibb(MdxModel m, Cursor p, int nodeIndex)
    {
        float above = p.F32(), below = p.F32(), alpha = p.F32();
        var color = p.Vec3();
        float edgeLifetime = p.F32();
        int textureSlot = p.I32(), edgesPerSecond = p.I32();
        int rows = p.I32(), cols = p.I32(), materialId = p.I32();
        float gravity = p.F32();

        m.RibbonEmitters.Add(new MdxRibbonEmitter
        {
            Name = m.Nodes[nodeIndex].Name, NodeIndex = nodeIndex,
            HeightAbove = above, HeightBelow = below, Alpha = alpha, Color = color,
            EdgeLifetime = edgeLifetime, TextureSlot = textureSlot,
            EdgesPerSecond = Math.Max(1, edgesPerSecond),
            Rows = Math.Max(1, rows), Columns = Math.Max(1, cols),
            MaterialId = materialId, Gravity = gravity,
            HeightAboveTrack = FindFloatTrack(p, "KRHA"),
            HeightBelowTrack = FindFloatTrack(p, "KRHB"),
            AlphaTrack = FindFloatTrack(p, "KRAL"),
            ColorTrack = FindVec3Track(p, "KRCO"),
            VisibilityTrack = FindFloatTrack(p, "KRVS") ?? FindFloatTrack(p, "KVIS"),
        });
    }

    private static void ReadLite(MdxModel m, Cursor p, int nodeIndex)
    {
        var type = (MdxLightType)p.U32();
        float attenStart = p.F32(), attenEnd = p.F32();
        var color = p.Vec3();
        float intensity = p.F32();
        var ambColor = p.Vec3();
        float ambIntensity = p.F32();

        m.Lights.Add(new MdxLight
        {
            Name = m.Nodes[nodeIndex].Name, NodeIndex = nodeIndex,
            LightType = type, AttenuationStart = attenStart, AttenuationEnd = attenEnd,
            Color = color, Intensity = intensity,
            AmbientColor = ambColor, AmbientIntensity = ambIntensity,
            AttenuationStartTrack = FindFloatTrack(p, "KLAS"),
            AttenuationEndTrack = FindFloatTrack(p, "KLAE"),
            ColorTrack = FindVec3Track(p, "KLAC"),
            IntensityTrack = FindFloatTrack(p, "KLAI"),
            VisibilityTrack = FindFloatTrack(p, "KVIS"),
        });
    }

    /// <summary>
    /// The legacy PREM emitter. Its payload is read only far enough to place it: PREM emits *models*
    /// rather than sprites, which has no SC2 counterpart, and it appears on 0.4% of models.
    /// </summary>
    private static void ReadPrem(MdxModel m, Cursor p, int nodeIndex)
    {
        float emissionRate = p.F32(), gravity = p.F32();
        float longitude = p.F32(), latitude = p.F32();
        p.Skip(260);                                        // model path — a PREM particle *is* a model
        float life = p.F32(), speed = p.F32();

        m.ParticleEmitters.Add(new MdxParticleEmitter2
        {
            Name = m.Nodes[nodeIndex].Name, NodeIndex = nodeIndex,
            EmissionRate = emissionRate, Gravity = gravity, Longitude = longitude,
            Latitude = latitude, Life = life, Speed = speed,
            TextureId = -1,                                 // no sprite: nothing to draw or export
            VisibilityTrack = FindFloatTrack(p, "KVIS"),
        });
    }

    /// <summary>
    /// Counts CORN entries by walking their inclusive sizes. The emitters themselves are not parsed
    /// — each one points at an external baked PopcornFX <c>.pkb</c> that a third-party runtime owns,
    /// so there is nothing to convert. The count exists purely so the exporter can say how many
    /// effects it dropped.
    /// </summary>
    private static int CountCornEmitters(Cursor r)
    {
        int count = 0;
        while (r.Remaining >= 4)
        {
            int start = r.Position;
            int inclusive = r.I32();
            if (inclusive < 8 || start + inclusive > r.End) break;
            count++;
            r.Seek(start + inclusive);
            if (r.Position <= start) break;
        }
        return count;
    }

    /// <summary>
    /// Scans the remainder of an emitter payload for a named track. Emitter tracks are optional and
    /// appear in a fixed order, but scanning rather than stepping means one unexpected or unknown
    /// track cannot cost us the rest — which matters because Reforged reorders these.
    /// </summary>
    private static MdxTrack<float>? FindFloatTrack(Cursor p, string tag)
    {
        int at = p.FindTag(tag);
        if (at < 0) return null;
        var c = p.At(at);
        return ReadFloatTrack(c);
    }

    private static MdxTrack<Vector3>? FindVec3Track(Cursor p, string tag)
    {
        int at = p.FindTag(tag);
        if (at < 0) return null;
        var c = p.At(at);
        return ReadVec3Track(c);
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

        /// <summary>A cursor over this one's remaining bytes, starting at an absolute position.</summary>
        public Cursor At(int position) => new(_d, position, End - position);

        /// <summary>
        /// Absolute position of a four-character tag at or after the current one, or -1. Emitter
        /// payloads are scanned for their tracks rather than stepped through, so an unrecognised
        /// or reordered track cannot cost the ones after it.
        /// </summary>
        public int FindTag(string tag)
        {
            for (int i = Position; i + 4 <= End; i++)
                if (_d[i] == tag[0] && _d[i + 1] == tag[1] && _d[i + 2] == tag[2] && _d[i + 3] == tag[3])
                    return i;
            return -1;
        }

        public byte U8() => Position < End ? _d[Position++] : (byte)0;
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
