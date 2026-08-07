using System.Numerics;

namespace Wc3ModelViewer.Core.Formats;

/// <summary>How a keyframe track interpolates between keys.</summary>
public enum MdxInterpolation
{
    None = 0,
    Linear = 1,
    Hermite = 2,
    Bezier = 3,
}

/// <summary>MDX layer filter mode — how the layer composites against what is behind it.</summary>
public enum MdxFilterMode
{
    None = 0,          // opaque
    Transparent = 1,   // alpha test (cutout)
    Blend = 2,
    Additive = 3,
    AddAlpha = 4,
    Modulate = 5,
    Modulate2x = 6,
}

/// <summary>Shading flag bits on an MDX layer.</summary>
[Flags]
public enum MdxShadingFlags
{
    None = 0,
    Unshaded = 0x1,
    SphereEnvironmentMap = 0x2,
    WrapWidth = 0x4,
    WrapHeight = 0x8,
    TwoSided = 0x10,
    Unfogged = 0x20,
    NoDepthTest = 0x40,
    NoDepthSet = 0x80,
    NoFallback = 0x100,
}

/// <summary>Node flag bits shared by every object in the MDX hierarchy.</summary>
[Flags]
public enum MdxNodeFlags
{
    None = 0,
    DontInheritTranslation = 0x1,
    DontInheritScaling = 0x2,
    DontInheritRotation = 0x4,
    Billboarded = 0x8,
    BillboardLockX = 0x10,
    BillboardLockY = 0x20,
    BillboardLockZ = 0x40,
    CameraAnchored = 0x80,
    Bone = 0x100,
    Light = 0x200,
    EventObject = 0x400,
    Attachment = 0x800,
    ParticleEmitter = 0x1000,
    CollisionShape = 0x2000,
    RibbonEmitter = 0x4000,

    // Flags from 0x8000 up are PRE2-only and change what the emitter does rather than what the
    // node is. The two low ones are overloaded — they mean something else entirely on a PREM.
    ParticleUnshaded = 0x8000,
    ParticleSortFarZ = 0x10000,
    LineEmitter = 0x20000,
    ParticleUnfogged = 0x40000,
    ParticleModelSpace = 0x80000,
    ParticleInheritScale = 0x100000,
}

/// <summary>The Reforged HD texture slots, as bound by an HD layer's texture-slot table.</summary>
public enum MdxTextureSlot
{
    Diffuse = 0,
    Normal = 1,
    Orm = 2,          // R = occlusion, G = roughness, B = metallic
    Emissive = 3,
    TeamColor = 4,
    EnvironmentMap = 5,
}

/// <summary>
/// One animated property. Times are milliseconds on the model's single global timeline; a sequence
/// is just the window <c>[IntervalStart, IntervalEnd]</c> of that timeline.
/// </summary>
/// <typeparam name="T">float, Vector3 or Quaternion.</typeparam>
public sealed class MdxTrack<T>
{
    public required string Tag { get; init; }
    public MdxInterpolation Interpolation { get; init; }

    /// <summary>Index into <see cref="MdxModel.GlobalSequences"/>, or -1 when driven by the sequence timeline.</summary>
    public int GlobalSequenceId { get; init; } = -1;

    public required int[] Times { get; init; }
    public required T[] Values { get; init; }

    /// <summary>Tangents, only present when <see cref="Interpolation"/> is Hermite or Bezier.</summary>
    public T[]? InTangents { get; init; }
    public T[]? OutTangents { get; init; }

    public int Count => Times.Length;
    public bool IsEmpty => Times.Length == 0;
}

/// <summary>A named animation: a window on the model's global millisecond timeline.</summary>
public sealed class MdxSequence
{
    public required string Name { get; init; }
    public int IntervalStart { get; init; }
    public int IntervalEnd { get; init; }
    public float MoveSpeed { get; init; }
    public uint Flags { get; init; }
    public float Rarity { get; init; }
    public uint SyncPoint { get; init; }
    public float BoundsRadius { get; init; }
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }

    /// <summary>Bit 0 of <see cref="Flags"/>: the animation plays once instead of looping.</summary>
    public bool NonLooping => (Flags & 1) != 0;

    public int DurationMs => Math.Max(IntervalEnd - IntervalStart, 0);

    public override string ToString() => $"{Name} [{IntervalStart}-{IntervalEnd}ms]";
}

/// <summary>A texture reference. Either a file path, or a replaceable-texture slot id.</summary>
public sealed class MdxTexture
{
    public uint ReplaceableId { get; init; }
    public required string FileName { get; init; }
    public uint Flags { get; init; }

    /// <summary>Team colour and team glow are supplied by the engine, not by a file.</summary>
    public bool IsTeamColor => ReplaceableId == 1;
    public bool IsTeamGlow => ReplaceableId == 2;
    public bool IsReplaceable => ReplaceableId != 0;

    public override string ToString() => IsReplaceable ? $"<replaceable {ReplaceableId}>" : FileName;
}

/// <summary>One layer of a material.</summary>
public sealed class MdxLayer
{
    public MdxFilterMode FilterMode { get; init; }
    public MdxShadingFlags ShadingFlags { get; init; }

    /// <summary>TEXS index of the layer's primary (diffuse) texture.</summary>
    public int TextureId { get; init; }
    public int TextureAnimationId { get; init; } = -1;
    public int CoordId { get; init; }

    public float Alpha { get; init; } = 1f;
    public float EmissiveMultiplier { get; init; }
    public Vector3 FresnelColor { get; init; }
    public float FresnelMultiplier { get; init; }
    public float TeamColorMultiplier { get; init; }

    /// <summary>
    /// Texture bindings, slot → TEXS index. Build 2.0.4 writes this table on <i>every</i> layer:
    /// HD layers bind all six PBR slots, SD layers bind only <see cref="MdxTextureSlot.Diffuse"/>.
    /// See <c>docs/mdx-format-verified.md</c> §4.
    /// </summary>
    public IReadOnlyDictionary<MdxTextureSlot, int> TextureSlots { get; init; }
        = new Dictionary<MdxTextureSlot, int>();

    public MdxTrack<float>? AlphaTrack { get; init; }
    public MdxTrack<float>? EmissiveTrack { get; init; }
    /// <summary>KMTF flipbook selecting the diffuse texture over time, or null when static.</summary>
    public MdxTrack<float>? TextureIdTrack { get; init; }

    /// <summary>The matching flipbook for the normal map, when the layer animates one.</summary>
    public MdxTrack<float>? NormalIdTrack { get; init; }

    /// <summary>True when the layer binds the PBR maps, i.e. it is a Reforged HD layer.</summary>
    public bool IsPbr => TextureSlots.ContainsKey(MdxTextureSlot.Normal)
                      || TextureSlots.ContainsKey(MdxTextureSlot.Orm);

    public int Slot(MdxTextureSlot slot) => TextureSlots.TryGetValue(slot, out int id) ? id : -1;

    /// <summary>
    /// The texture to sample for base colour, preferring the explicit diffuse binding.
    /// </summary>
    /// <remarks>
    /// A <c>KMTF</c> track wins over both. Reforged animates water, waterfalls and similar surfaces
    /// as a flipbook — the fountains carry fifty <c>War3_FountainWater_Diff_00nn</c> frames — and
    /// selects the frame through that track while leaving the layer's static
    /// <see cref="TextureId"/> at 0. Ignoring the track therefore does not just freeze the
    /// animation, it samples whatever happens to be texture 0, which on every fountain is the rock
    /// diffuse. See <see cref="TextureIdAt"/> for picking a specific frame.
    /// </remarks>
    public int DiffuseTextureId => TextureIdTrack is { Count: > 0 } t ? (int)t.Values[0]
                                 : Slot(MdxTextureSlot.Diffuse) is var d && d >= 0 ? d
                                 : TextureId;

    /// <summary>
    /// The flipbook frame this layer shows at <paramref name="timeMs"/>, or
    /// <see cref="DiffuseTextureId"/> when the layer is not animated. <c>KMTF</c> keys are stepped,
    /// never interpolated — a texture index between two frames is meaningless.
    /// </summary>
    public int TextureIdAt(int timeMs)
    {
        if (TextureIdTrack is not { Count: > 0 } t) return DiffuseTextureId;
        int k = 0;
        while (k + 1 < t.Count && t.Times[k + 1] <= timeMs) k++;
        return (int)t.Values[k];
    }

    /// <summary>Distinct texture ids this layer cycles through, in key order. Empty when static.</summary>
    public IEnumerable<int> FlipbookTextureIds =>
        TextureIdTrack is { Count: > 0 } t ? t.Values.Select(v => (int)v).Distinct() : [];
}

public sealed class MdxMaterial
{
    public int PriorityPlane { get; init; }
    public uint Flags { get; init; }
    public required List<MdxLayer> Layers { get; init; }
}

/// <summary>A sub-mesh. Carries either classic matrix-group skinning or Reforged per-vertex skinning.</summary>
public sealed class MdxGeoset
{
    public int Index { get; init; }

    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required int[] Indices { get; init; }

    /// <summary>UV layers; layer 0 is the one used for rendering. V is stored top-down (D3D convention).</summary>
    public required List<Vector2[]> UvLayers { get; init; }

    // ---- classic skinning: vertex -> group -> a set of equally-weighted bones ----
    public byte[] VertexGroups { get; init; } = [];
    public int[] MatrixGroupSizes { get; init; } = [];
    public int[] MatrixIndices { get; init; } = [];

    // ---- Reforged skinning: 4 weighted bones per vertex ----
    public byte[] SkinBoneIndices { get; init; } = [];   // 4 per vertex, index into the node list
    public byte[] SkinBoneWeights { get; init; } = [];   // 4 per vertex, 0..255
    public Vector4[] Tangents { get; init; } = [];

    public int MaterialId { get; init; }
    public int SectionGroupId { get; init; }
    public int SectionGroupType { get; init; }
    public int LodId { get; init; }
    public string LodName { get; init; } = "";

    public float BoundsRadius { get; init; }
    public Vector3 Min { get; init; }
    public Vector3 Max { get; init; }

    public int VertexCount => Positions.Length;
    public int TriangleCount => Indices.Length / 3;

    /// <summary>True when the geoset carries a SKIN chunk, i.e. Reforged per-vertex weights.</summary>
    public bool HasSkin => SkinBoneIndices.Length >= Positions.Length * 4;

    public Vector2[] Uvs => UvLayers.Count > 0 ? UvLayers[0] : [];

    /// <summary>
    /// LOD 0 is the unoptimised mesh; Reforged units also ship LOD 1, which the guide recommends
    /// for anything appearing in numbers.
    /// </summary>
    public override string ToString() => $"geoset {Index} ({VertexCount} verts, {LodName})";
}

/// <summary>Per-geoset animated alpha and colour — how Warcraft III hides and fades body parts.</summary>
public sealed class MdxGeosetAnim
{
    public float Alpha { get; init; } = 1f;
    public uint Flags { get; init; }
    public Vector3 Color { get; init; } = Vector3.One;
    public int GeosetId { get; init; } = -1;
    public MdxTrack<float>? AlphaTrack { get; init; }
    public MdxTrack<Vector3>? ColorTrack { get; init; }
}

/// <summary>What kind of thing a node is. Determines which extra fields followed it in the file.</summary>
public enum MdxNodeKind { Bone, Helper, Attachment, Light, ParticleEmitter, ParticleEmitter2, RibbonEmitter, Event, CollisionShape, Camera }

/// <summary>
/// A node in the model hierarchy. Bones, helpers, attachments, emitters and events all share this
/// header, and <see cref="ObjectId"/> is the index into <see cref="MdxModel.Pivots"/>.
/// </summary>
public sealed class MdxNode
{
    public required string Name { get; init; }
    public int ObjectId { get; init; } = -1;
    public int ParentId { get; init; } = -1;
    public MdxNodeFlags Flags { get; init; }
    public MdxNodeKind Kind { get; init; }

    public MdxTrack<Vector3>? Translation { get; init; }
    public MdxTrack<Quaternion>? Rotation { get; init; }
    public MdxTrack<Vector3>? Scale { get; init; }

    // Bone-only.
    public int GeosetId { get; init; } = -1;
    public int GeosetAnimId { get; init; } = -1;

    // Attachment-only.
    public int AttachmentId { get; init; } = -1;
    public string AttachmentPath { get; init; } = "";

    /// <summary>
    /// The node's rest position, taken from PIVT by <see cref="ObjectId"/>. Warcraft III bones have
    /// no rest rotation or scale — the pivot is a pure translation and every animation key is an
    /// offset from it. Resolved by the reader.
    /// </summary>
    public Vector3 Pivot { get; set; }

    public bool IsBillboarded => (Flags & MdxNodeFlags.Billboarded) != 0;
    public bool Animated => Translation is { Count: > 0 } || Rotation is { Count: > 0 } || Scale is { Count: > 0 };

    public override string ToString() => $"{Kind} '{Name}' (id {ObjectId}, parent {ParentId})";
}

/// <summary>How a particle's texture is combined with what is behind it.</summary>
public enum MdxParticleBlend { Blend = 0, Add = 1, Modulate = 2, Modulate2X = 3, AlphaKey = 4 }

/// <summary>Head sprites, a stretched tail, or both.</summary>
public enum MdxParticleType { Head = 0, Tail = 1, Both = 2 }

public enum MdxLightType { Omni = 0, Directional = 1, Ambient = 2 }

/// <summary>
/// Fields shared by everything that hangs off a node — the emitters and lights all reference the
/// node that positions them rather than duplicating its transform.
/// </summary>
public abstract class MdxNodeAttachedObject
{
    /// <summary>Index into <see cref="MdxModel.Nodes"/> of the node carrying this object's transform.</summary>
    public int NodeIndex { get; init; } = -1;

    /// <summary>Emitters are switched on and off per sequence through this track.</summary>
    public MdxTrack<float>? VisibilityTrack { get; init; }
}

/// <summary>
/// A PRE2 particle emitter — the one Warcraft III actually uses (PREM is deprecated).
/// </summary>
/// <remarks>
/// Build 2.0.4 ships one 171-byte form of this struct in both the SD and HD trees — not the two
/// variants the published spec describes. <see cref="Longitude"/> is only ever set by the legacy
/// PREM path. See <c>MdxReader.ReadPre2</c> for how that was established from real bytes.
/// </remarks>
public sealed class MdxParticleEmitter2 : MdxNodeAttachedObject
{
    public required string Name { get; init; }

    public float Speed { get; init; }
    public float Variation { get; init; }
    public float Latitude { get; init; }
    public float Longitude { get; init; }
    public float Gravity { get; init; }
    public float Life { get; init; }
    public float EmissionRate { get; init; }
    public float Length { get; init; }
    public float Width { get; init; }

    public MdxParticleBlend Blend { get; init; }
    public int Rows { get; init; } = 1;
    public int Columns { get; init; } = 1;
    public MdxParticleType ParticleType { get; init; }
    public float TailLength { get; init; }
    public float MiddleTime { get; init; }

    public Vector3 StartColor { get; init; } = Vector3.One;
    public Vector3 MiddleColor { get; init; } = Vector3.One;
    public Vector3 EndColor { get; init; } = Vector3.One;
    public byte StartAlpha { get; init; } = 255;
    public byte MiddleAlpha { get; init; } = 255;
    public byte EndAlpha { get; init; } = 255;
    public float StartScale { get; init; } = 1;
    public float MiddleScale { get; init; } = 1;
    public float EndScale { get; init; } = 1;

    /// <summary>TEXS index, or -1. The particle sprite sheet.</summary>
    public int TextureId { get; init; } = -1;

    // Sprite-sheet cells a live particle walks through, indexed row-major across Rows x Columns.
    public int HeadCellStart { get; init; }
    public int HeadCellEnd { get; init; }
    public int HeadCellRepeat { get; init; } = 1;
    public int PriorityPlane { get; init; }
    public int ReplaceableId { get; init; }
    public bool Squirt { get; init; }

    public MdxTrack<float>? SpeedTrack { get; init; }
    public MdxTrack<float>? VariationTrack { get; init; }
    public MdxTrack<float>? LatitudeTrack { get; init; }
    public MdxTrack<float>? GravityTrack { get; init; }
    public MdxTrack<float>? LifeTrack { get; init; }
    public MdxTrack<float>? EmissionRateTrack { get; init; }
    public MdxTrack<float>? WidthTrack { get; init; }
    public MdxTrack<float>? LengthTrack { get; init; }

    /// <summary>Unshaded particles ignore scene lighting — the usual choice for glows and fire.</summary>
    public bool Unshaded { get; init; }
    public bool Unfogged { get; init; }
    public bool ModelSpace { get; init; }
    public bool LineEmitter { get; init; }

    public override string ToString() => $"PRE2 '{Name}' ({Blend}, {EmissionRate:0.#}/s, life {Life:0.##}s)";
}

/// <summary>A RIBB ribbon emitter — a trailing strip of quads, used for weapon trails and banners.</summary>
public sealed class MdxRibbonEmitter : MdxNodeAttachedObject
{
    public required string Name { get; init; }

    public float HeightAbove { get; init; }
    public float HeightBelow { get; init; }
    public float Alpha { get; init; } = 1;
    public Vector3 Color { get; init; } = Vector3.One;

    /// <summary>Seconds an emitted edge survives. The client clamps this to a 0.25 s minimum.</summary>
    public float EdgeLifetime { get; init; }
    public int TextureSlot { get; init; }
    public int EdgesPerSecond { get; init; } = 1;
    public int Rows { get; init; } = 1;
    public int Columns { get; init; } = 1;

    /// <summary>MTLS index — unlike a particle emitter, a ribbon draws through a real material.</summary>
    public int MaterialId { get; init; } = -1;
    public float Gravity { get; init; }

    public MdxTrack<float>? HeightAboveTrack { get; init; }
    public MdxTrack<float>? HeightBelowTrack { get; init; }
    public MdxTrack<float>? AlphaTrack { get; init; }
    public MdxTrack<Vector3>? ColorTrack { get; init; }

    public override string ToString() => $"RIBB '{Name}' (mat {MaterialId}, {EdgesPerSecond}/s, {EdgeLifetime:0.##}s)";
}

/// <summary>A LITE light source.</summary>
public sealed class MdxLight : MdxNodeAttachedObject
{
    public required string Name { get; init; }

    public MdxLightType LightType { get; init; }
    public float AttenuationStart { get; init; }
    public float AttenuationEnd { get; init; }
    public Vector3 Color { get; init; } = Vector3.One;
    public float Intensity { get; init; }
    public Vector3 AmbientColor { get; init; } = Vector3.One;
    public float AmbientIntensity { get; init; }

    public MdxTrack<float>? AttenuationStartTrack { get; init; }
    public MdxTrack<float>? AttenuationEndTrack { get; init; }
    public MdxTrack<Vector3>? ColorTrack { get; init; }
    public MdxTrack<float>? IntensityTrack { get; init; }

    public override string ToString() => $"LITE '{Name}' ({LightType}, range {AttenuationEnd:0.#})";
}

/// <summary>A camera. Cameras are not nodes — they carry no objectId, parentId or pivot.</summary>
public sealed class MdxCamera
{
    public required string Name { get; init; }
    public Vector3 Position { get; init; }
    public float FieldOfView { get; init; }
    public float FarClip { get; init; }
    public float NearClip { get; init; }
    public Vector3 TargetPosition { get; init; }
    public MdxTrack<Vector3>? TranslationTrack { get; init; }
    public MdxTrack<Vector3>? TargetTranslationTrack { get; init; }
    public MdxTrack<float>? RollTrack { get; init; }
}

/// <summary>A parsed Warcraft III model.</summary>
public sealed class MdxModel
{
    public uint Version { get; set; }
    public string Name { get; set; } = "";
    public string AnimationFile { get; set; } = "";
    public float BoundsRadius { get; set; }
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }
    public uint BlendTime { get; set; }

    public List<MdxSequence> Sequences { get; } = [];
    public List<uint> GlobalSequences { get; } = [];
    public List<MdxMaterial> Materials { get; } = [];
    public List<MdxTexture> Textures { get; } = [];
    public List<MdxGeoset> Geosets { get; } = [];
    public List<MdxGeosetAnim> GeosetAnims { get; } = [];
    public List<MdxCamera> Cameras { get; } = [];
    public List<Vector3> Pivots { get; } = [];

    /// <summary>Every node, in file order. <see cref="MdxNode.ObjectId"/> indexes <see cref="Pivots"/>.</summary>
    public List<MdxNode> Nodes { get; } = [];

    // ---- effects. Each entry points back at the node in Nodes that positions it. ----

    public List<MdxParticleEmitter2> ParticleEmitters { get; } = [];
    public List<MdxRibbonEmitter> RibbonEmitters { get; } = [];
    public List<MdxLight> Lights { get; } = [];

    /// <summary>
    /// Reforged PopcornFX emitters (CORN). Only the count is tracked: the chunk references external
    /// baked <c>.pkb</c> effect files through a third-party runtime, so there is nothing to convert
    /// and nothing an exporter can honestly emit. Recorded so a dropped effect can be reported
    /// rather than silently vanishing — a third of Warcraft III's effect models use these.
    /// </summary>
    public int PopcornEmitterCount { get; set; }

    /// <summary>True when the model carries anything the effect pipeline cares about.</summary>
    public bool HasEffects => ParticleEmitters.Count > 0 || RibbonEmitters.Count > 0
                              || Lights.Count > 0 || PopcornEmitterCount > 0;

    /// <summary>Tags of chunks the reader skipped, for diagnostics.</summary>
    public List<string> SkippedChunks { get; } = [];

    /// <summary>
    /// True when any geoset carries Reforged per-vertex skinning — the reliable way to tell HD from
    /// SD, since both report VERS 1200. See <c>docs/mdx-format-verified.md</c> §2.
    /// </summary>
    public bool IsReforged => Geosets.Exists(g => g.HasSkin);

    public IEnumerable<MdxNode> Bones => Nodes.Where(n => n.Kind == MdxNodeKind.Bone);
    public IEnumerable<MdxNode> Attachments => Nodes.Where(n => n.Kind == MdxNodeKind.Attachment);

    /// <summary>Distinct LOD levels present, ascending. LOD 0 is the highest detail.</summary>
    public List<int> LodLevels => Geosets.Select(g => g.LodId).Distinct().Order().ToList();

    public int TotalVertices => Geosets.Sum(g => g.VertexCount);
    public int TotalTriangles => Geosets.Sum(g => g.TriangleCount);
}
