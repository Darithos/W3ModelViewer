# Complete binary specification of the classic Warcraft III MDX format (MDLX / VERS 800)

## [certain] File is `MDLX` magic followed by a flat sequence of 8-byte-headered chunks; chunk size EXCLUDES the 8-byte header; chunk order is not guaranteed and unknown chunks must be skipped by size.

Layout:
```
offset 0: char[4] magic = "MDLX"   (0x4D,0x44,0x4C,0x58 -> as little-endian uint32 = 0x584C444D)
then repeat until EOF:
  char[4] tag        // e.g. "VERS","MODL","SEQS"...
  uint32  size       // bytes of payload, NOT including these 8 header bytes
  byte[size] payload
```
All integers/floats are LITTLE-ENDIAN. float = IEEE754 binary32. There is no alignment/padding anywhere: every chunk starts immediately after the previous chunk's payload.

C# skeleton:
```csharp
var br = new BinaryReader(stream);
if (br.ReadUInt32() != 0x584C444D) throw new InvalidDataException("not MDLX");
while (stream.Position < stream.Length) {
    uint tag  = br.ReadUInt32();        // compare against 'S','R','E','V' packed etc.
    uint size = br.ReadUInt32();
    long end  = stream.Position + size;
    switch (tag) { ... default: break; }
    stream.Position = end;              // always trust the header size
}
```
Chunk tags that exist in v800 (all optional, all top-level):
VERS, MODL, SEQS, GLBS, MTLS, TEXS, TXAN, GEOS, GEOA, BONE, LITE, HELP, ATCH, PIVT, PREM, PRE2, RIBB, EVTS, CAMS, CLID, and the vestigial SNDS (sound tracks, pre-release only, size/272 entries: char[260] fileName, float volume, float pitch, uint32 flags).
Chunks that only exist for version > 800 (Reforged): BPOS, FAFX, CORN. Blizzard's own Reforged-era "SD" models are stored as version 900/1000/1100 but may still lack TANG/SKIN in geosets.
Ghostwolf's writer emits chunks in exactly this order: VERS, MODL, SEQS, GLBS, MTLS, TEXS, TXAN, GEOS, GEOA, BONE, LITE, HELP, ATCH, PIVT, PREM, PRE2, (CORN), RIBB, CAMS, EVTS, CLID, (FAFX), (BPOS), then any preserved unknown chunks. Writing in this order is safe for WC3.
Note WC3 tolerates unknown chunks — third-party tools stash metadata in them.

_evidence: mdx-m3-viewer src/parsers/mdlx/model.ts loadMdx()/saveMdx() (local: C:\Users\Darithos\AppData\Local\Temp\mdxv\mdx-m3-viewer-master\src\parsers\mdlx\model.ts:116-273); hiveworkshop.com/threads/mdx-specifications.240487 "Header { char[4] tag; uint32 size }"_

## [certain] VERS payload is a single uint32 version; 800 = RoC/TFT classic. MODL payload is exactly 372 bytes: char[80] name, char[260] animationFileName, Extent(28), uint32 blendTime.

```
VERS  size=4
  uint32 version    // 800 classic; 900/1000/1100/1200 Reforged

MODL  size=372
  +0    char[80]  name              // NUL-padded, not necessarily NUL-terminated if exactly 80
  +80   char[260] animationFileName // "" in practice for every shipped model
  +340  float     boundsRadius      // <-- Extent starts here
  +344  float[3]  minimumExtent     // x,y,z
  +356  float[3]  maximumExtent
  +368  uint32    blendTime         // only used by the defunct Art Tools previewer; often 150
  = 372
```
Extent is a reusable 28-byte struct used by MODL, SEQS and GEOS:
```csharp
struct Extent { float BoundsRadius; Vector3 Min; Vector3 Max; }  // 4 + 12 + 12 = 28
```
IMPORTANT ORDER GOTCHA: boundsRadius comes FIRST, before min/max. TaylorMouse's MaxScript ReadMODL reads radius,min,max (agrees) but his ReadSEQS mislabels the boundsRadius float as "Priority" — the byte order is nonetheless identical.
Fixed-size strings: read exactly N bytes, then cut at the first 0x00. Encoding is effectively Windows-1252/ASCII (Reforged uses UTF-8); use Encoding.Latin1 or UTF8 with a trim.

_evidence: model.ts:179-188 & saveModelChunk (writes size 372); extent.ts readMdx; Retera craft3data/src/com/hiveworkshop/wc3/mdx/ModelChunk.java (name read as 336+4 = 340 bytes); local MaxScript GriffonStudios_Warcraft_3_Reforged_Read.ms ReadMODL_

## [certain] SEQS is an array of fixed 132-byte Sequence structs (count = size/132); times are in MILLISECONDS.

```
Sequence  (132 bytes)
  +0    char[80] name          // e.g. "Stand", "Walk", "Attack - 1", "Death"
  +80   uint32   intervalStart // ms
  +84   uint32   intervalEnd   // ms
  +88   float    moveSpeed
  +92   uint32   flags         // 0 = looping, 1 = non-looping  (MDL token "NonLooping")
  +96   float    rarity
  +100  uint32   syncPoint     // MDL "Default"/sync; almost always 0
  +104  float    boundsRadius
  +108  float[3] minimumExtent
  +120  float[3] maximumExtent
  = 132
```
count = chunkSize / 132.
Animation names carry semantics in WC3 ("Stand", "Stand Ready", "Walk", "Attack", "Death", "Decay Flesh", "Dissipate", "Morph", "Spell", "Portrait"; a trailing " - 1"/" - 2" or " Alternate" suffix selects variants). All keyframe times everywhere in the file are in the same global millisecond timeline; a sequence is simply the window [intervalStart, intervalEnd]. WC3 plays at 1000 ms/s, so for an m3 export at 30 fps: frame = ms * 30 / 1000. Sequences may overlap or be listed unsorted; some models repeat a start frame (TaylorMouse dedupes on identical startFrame).

_evidence: sequence.ts readMdx; model.ts:132 `size / 132`; Retera SequenceChunk.java Sequence.load; hive MDX spec "Sequence { char[80] name; uint32[2] interval; float moveSpeed; uint32 flags; float rarity; uint32 syncPoint; Extent extent }"_

## [certain] GLBS is a raw uint32[size/4] array of global-sequence durations (ms). A track whose globalSequenceId >= 0 is driven by GLBS[id] instead of by the current sequence.

```
GLBS  payload = uint32[size/4] durations   // milliseconds
```
Semantics: any keyframe track (see the KGxx container below) has an int32 globalSequenceId. If it is -1 (0xFFFFFFFF) the track's frame times are absolute times on the sequence timeline and only keys inside [seq.intervalStart, seq.intervalEnd] are used. If globalSequenceId >= 0, the track ignores the current sequence entirely and loops with period GLBS[globalSequenceId] ms: t = (totalElapsedMs % duration), and the track's frame values are in [0, duration].
For an .m3 export this must be baked: global-sequence tracks have to be resampled into every SC2 animation, because M3 (STC/SD) has no equivalent of a free-running global sequence.

_evidence: model.ts loadGlobalSequenceChunk; animations.ts Animation.globalSequenceId; Retera GlobalSequenceChunk.java_

## [certain] Every animated property uses one identical container: char[4] tag, uint32 numberOfTracks, uint32 interpolationType, int32 globalSequenceId, then numberOfTracks entries of {int32 frame; T value; if(interp>1){T inTan; T outTan;}}.

```
KeyTrack<T> {
  char[4] tag                // "KGTR","KGRT","KGSC","KMTA","KMTF",... (KEVT is the ONE exception, see EVTS)
  uint32  tracksCount
  uint32  interpolationType  // 0 = DontInterp (step), 1 = Linear, 2 = Hermite, 3 = Bezier
  int32   globalSequenceId   // -1 = none, else index into GLBS
  Track[tracksCount] {
    int32 frame              // milliseconds; signed - a few models really do have negatives
    T     value
    if (interpolationType > 1) { T inTan; T outTan; }
  }
}
```
sizeof(container) = 16 + tracksCount * (4 + (interp>1 ? 3 : 1) * sizeof(T)).
T per tag (classic v800 set):
- float[3] (12 B): KGTR translation, KGSC scaling, KGAC geoset color(BGR), KLAC light color(BGR), KLBC light ambient color(BGR), KRCO ribbon color(BGR), KTAT texture-anim translation, KTAS texture-anim scaling, KCTR camera translation, KTTR camera target translation.
- float[4] (16 B): KGRT rotation (quaternion, component order x,y,z,w), KTAR texture-anim rotation (quaternion).
- float (4 B): KMTA layer alpha, KGAO geoset alpha, KATV attachment visibility, KLAS/KLAE light attenuation start/end, KLAI intensity, KLBI ambient intensity, KLAV light visibility, KPEE/KPEG/KPLN/KPLT/KPEL/KPES/KPEV (PREM), KP2S/KP2R/KP2L/KP2G/KP2E/KP2N/KP2W/KP2V (PRE2), KRHA/KRHB/KRAL/KRVS (RIBB), KCRL camera roll (radians, the only non-quaternion rotation).
- uint32 (4 B): KMTF layer textureId, KRTX ribbon texture slot.
- Reforged-only (>800): KMTE emissiveGain(float), KFC3 fresnelColor(float[3]), KFCA fresnelOpacity(float), KFTC fresnelTeamColor(uint32/float), KPPA/KPPE/KPPL/KPPS/KPPV(float) and KPPC(float[3]) for CORN.
Interpolation math (matches the WC3 runtime and mdx-m3-viewer):
- 0 DontInterp: value = value[i] for t in [frame[i], frame[i+1]).
- 1 Linear: lerp(v[i], v[i+1], s) with s=(t-f[i])/(f[i+1]-f[i]); quaternions use slerp.
- 2 Hermite: cubic Hermite h(v[i], outTan[i], inTan[i+1], v[i+1], s). i.e. the OUT tangent of the current key and the IN tangent of the NEXT key are used; each is an absolute value in the same units as `value`, not a delta. Quaternions use squad.
- 3 Bezier: cubic Bezier with control points p0=v[i], p1=outTan[i], p2=inTan[i+1], p3=v[i+1].
Out-of-range clamping: t before first key -> first value, after last -> last value.
C# reader:
```csharp
class KeyTrack { public string Tag; public int Interp; public int GlobalSeqId; public int[] Frames; public float[][] Values, InTans, OutTans; }
static KeyTrack ReadTrack(BinaryReader br, string tag, int dim) {
  int n = br.ReadInt32(), it = br.ReadInt32(), gs = br.ReadInt32();
  var t = new KeyTrack { Tag=tag, Interp=it, GlobalSeqId=gs, Frames=new int[n], Values=new float[n][] };
  if (it > 1) { t.InTans = new float[n][]; t.OutTans = new float[n][]; }
  for (int i=0;i<n;i++){ t.Frames[i]=br.ReadInt32(); t.Values[i]=ReadN(br,dim);
    if (it>1){ t.InTans[i]=ReadN(br,dim); t.OutTans[i]=ReadN(br,dim);} }
  return t; }
```
DETECTION of optional tracks: after reading an object's fixed fields, keep consuming 4-byte tags while (streamPos < objectStart + inclusiveSize). This size-driven approach (used by mdx-m3-viewer) is strictly more robust than TaylorMouse's "peek for a known tag, else seek -4" approach, which breaks on unknown tracks. Use the size method.

_evidence: mdx-m3-viewer src/parsers/mdlx/animations.ts (Animation.readMdx / getByteLength) and animationmap.ts; Retera GeosetTranslation.java + GeosetRotation.java; hive MDX spec "TracksChunk" section; local Read.ms ReadVector3Anim/ReadQuatAnim/ReadFloatAnim/ReadLongAnim_

## [certain] MTLS holds variable-length Material entries, each led by an inclusiveSize; each contains an inline LAYS sub-chunk with a layer count and that many variable-length Layer entries.

```
Material (v800)
  +0  uint32 inclusiveSize   // includes these 4 bytes and all layers
  +4  int32  priorityPlane   // signed; higher = drawn later
  +8  uint32 flags           // 0x1 ConstantColor, 0x2 TwoSided(>800 only in MDL),
                             // 0x8 SortPrimsNearZ, 0x10 SortPrimsFarZ, 0x20 FullResolution
  [ if version > 800: char[80] shader ]   // e.g. "Shader_HD_DefaultUnit" - ABSENT in v800
  +12 char[4] "LAYS"
  +16 uint32 layersCount
  +20 Layer[layersCount]
```
Fixed part = 20 bytes for v800 (28+80 style for >800: +80).
```
Layer (v800)
  +0  uint32 inclusiveSize          // = 28 + sum(track sizes) for v800
  +4  uint32 filterMode            // 0 None(opaque) 1 Transparent(alpha test @0.75)
                                   // 2 Blend 3 Additive 4 AddAlpha 5 Modulate 6 Modulate2x
  +8  uint32 shadingFlags          // 0x1 Unshaded, 0x2 SphereEnvMap, 0x4 ?, 0x8 ?,
                                   // 0x10 TwoSided, 0x20 Unfogged, 0x40 NoDepthTest,
                                   // 0x80 NoDepthSet, (0x100 Unlit, >800 only)
  +12 int32  textureId             // index into TEXS; -1 possible when animated by KMTF
  +16 int32  textureAnimationId    // index into TXAN; -1 = none
  +20 uint32 coordId               // which UVBS set of the geoset to use; ~always 0
  +24 float  alpha                 // 0..1 static alpha
  [ if >800: float emissiveGain; float[3] fresnelColor; float fresnelOpacity; float fresnelTeamColor ]
  +28 optional (KMTF)              // uint32 texture id over time (flipbook / team-color swaps)
      optional (KMTA)              // float alpha over time
      [ >800: (KMTE), >900: (KFC3)(KFCA)(KFTC) ]
```
GEOSET->MATERIAL: geoset.materialId indexes MTLS. Each layer of that material is a separate draw call over the same geometry, blended in order.
TEAM COLOR / ALPHA (the reported Reforged pain point, but also the classic one): a texture entry with replaceableId 1 = TeamColor, 2 = TeamGlow (see TEXS). The classic idiom is layer0 = opaque team-color texture (filterMode 0/1), layer1 = the diffuse texture with filterMode 2 (Blend) so that the diffuse's alpha reveals the team color underneath. When converting to .m3 you must bake this: sample the diffuse RGBA, and where alpha<1 composite the chosen team colour underneath, otherwise SC2 will show a hole. For filterMode 1 (Transparent) the runtime does alpha-test at 0.75, NOT blending.
Colors: any float[3] color in MDX binary is stored B,G,R (see the MDL/BGR finding).

_evidence: material.ts + layer.ts readMdx/getByteLength (mdx-m3-viewer); Retera MaterialChunk.java / LayerChunk.java; hive MDX spec Material/Layer; local Read.ms ReadShader/ReadLayer (confirms v1000 inserts an 80-byte name and v1100+ adds 56 unknown bytes)_

## [certain] TEXS is an array of fixed 268-byte Texture structs: uint32 replaceableId, char[260] fileName, uint32 flags(wrap mode).

```
Texture (268 bytes)
  +0   uint32   replaceableId   // 0 = use fileName
  +4   char[260] fileName       // e.g. "Textures\\Arthas.blp" (backslashes, MPQ/CASC path)
  +264 uint32   flags           // wrap mode bitfield: 0x1 WrapWidth, 0x2 WrapHeight
                                // (0 = repeat both / clamp semantics per Ghostwolf's WrapMode enum:
                                //  0 RepeatBoth, 1 WrapWidth, 2 WrapHeight, 3 WrapBoth)
```
count = chunkSize / 268.
Replaceable IDs (classic, from WC3 art tools / ReplaceableTextures\):
 1 = TeamColor  -> ReplaceableTextures\TeamColor\TeamColorNN.blp
 2 = TeamGlow   -> ReplaceableTextures\TeamGlow\TeamGlowNN.blp
 11..31 = Cliff/Lordaeron tree/etc. terrain-driven textures; 21 = ReplaceableTextures\CliffTextures, 31..34 = LordaeronTree/AshenvaleTree/BarrensTree/NorthrendTree, 35 = MushroomTree, 36 = RuinsTree, 37 = OutlandMushroomTree.
When replaceableId != 0 the fileName is usually empty and the engine substitutes the texture. For .m3 export you must resolve these to a concrete image (bake team colour or emit a team-colour material slot).
Classic textures are .blp (BLP1, JPEG-or-paletted); Reforged uses .dds and the model may reference "...\\Foo.dds" directly.

_evidence: texture.ts readMdx + WrapMode enum; model.ts:138 `size / 268`; Retera TextureChunk.java (256+4+4 split of the same 268); hive MDX spec Texture; local Read.ms ReadTEXS (`numTexs = tag.Size / 268`)_

## [certain] TXAN holds variable-length TextureAnimation entries: uint32 inclusiveSize then optional (KTAT)(KTAR)(KTAS) tracks; referenced by Layer.textureAnimationId.

```
TextureAnimation
  +0 uint32 inclusiveSize      // 4 + sum of present track sizes
  then any of, in any order:
    KTAT  float[3] translation  (u,v,_)
    KTAR  float[4] rotation     (quaternion, rotates UVs about w-axis)
    KTAS  float[3] scaling      (u,v,_)
```
Read loop: `long end = pos + inclusiveSize; while (pos < end) { tag = read4(); track = ReadTrack(...); }`.
The transform is applied to the layer's UVs at draw time: uv' = S*R*(uv) + T (centre 0,0). SC2 .m3 has an equivalent (STC-driven UV transform on the material layer), so these can usually be carried across 1:1.

_evidence: textureanimation.ts readMdx; Retera TextureAnimationChunk.java; local Read.ms ReadTXAN_

## [certain] GEOS holds variable-length Geosets. A v800 Geoset is: inclusiveSize, then the fixed sequence of inline tagged arrays VRTX,NRMS,PTYP,PCNT,PVTX,GNDX,MTGC,MATS, then materialId/selectionGroup/selectionFlags, then Extent + per-sequence Extents, then UVAS/UVBS. Sub-chunk order is FIXED, and each sub-array header is char[4] tag + uint32 COUNT (an element count, not a byte size).

```
Geoset (v800)
  +0   uint32 inclusiveSize
  +4   char[4]  "VRTX"; uint32 vertexCount;        float[vertexCount*3]  positions   (x,y,z)
       char[4]  "NRMS"; uint32 normalCount;        float[normalCount*3]  normals
       char[4]  "PTYP"; uint32 faceTypeGroupsCount;uint32[..]            faceTypeGroups
                        // 0 points,1 lines,2 line loop,3 line strip,4 TRIANGLES,
                        // 5 tri strip,6 tri fan,7 quads,8 quad strip,9 polygons
                        // in practice always exactly one entry with value 4
       char[4]  "PCNT"; uint32 faceGroupsCount;    uint32[..] faceGroups
                        // number of indices belonging to each face-type group;
                        // sum(faceGroups) == facesCount
       char[4]  "PVTX"; uint32 facesCount;         uint16[facesCount] faces   // 2 BYTES each!
       char[4]  "GNDX"; uint32 vertexGroupsCount;  uint8 [..] vertexGroups   // 1 BYTE each, == vertexCount
       char[4]  "MTGC"; uint32 matrixGroupsCount;  uint32[..] matrixGroups   // group sizes
       char[4]  "MATS"; uint32 matrixIndicesCount; uint32[..] matrixIndices  // node objectIds
       uint32 materialId       // index into MTLS
       uint32 selectionGroup
       uint32 selectionFlags   // 4 = Unselectable (MDL flag "Unselectable"); 0 otherwise
  [ if version > 800: int32 lod; char[80] lodName ]     // ABSENT in v800
       float boundsRadius; float[3] min; float[3] max    // Extent, 28 bytes
       uint32 extentsCount
       Extent[extentsCount] sequenceExtents              // 28 bytes each, ONE PER SEQS ENTRY, same order
  [ if version > 800: optional "TANG" uint32 count float[count*4];
                      optional "SKIN" uint32 count uint8[count] ]
       char[4] "UVAS"; uint32 uvSetCount
       repeat uvSetCount times: char[4] "UVBS"; uint32 uvCount; float[uvCount*2] uvs
```
Fixed overhead of a v800 geoset (everything except array payloads and per-sequence extents) = 120 bytes; each extra UV set costs 8 + 8*vertexCount; each sequence extent costs 28. (Ghostwolf's getByteLength: `120 + arrays + sequenceExtents.length*28`, +84 and +8+payload per TANG/SKIN for >800.)
Details that bite:
- PVTX indices are uint16 and index into this geoset's own vertex array (0-based, geoset-local).
- facesCount is the number of INDICES, not triangles: triangles = facesCount/3.
- normalCount always equals vertexCount in practice; there is exactly one normal per vertex.
- Every UVBS set has uvCount == vertexCount. WC3 UVs are top-left origin: v_gl = 1 - v_file (TaylorMouse flips: `[u, 1.0 - v, 0]`). Decide the flip once and stay consistent; SC2 .m3 uses the same D3D-style top-left convention as the MDX file, so for MDX->M3 you generally do NOT flip.
- Coordinate system: right-handed, Z-up, 1 unit ~ 1 "WC3 unit" (a footman is ~ 60-70 units tall). SC2 .m3 is also right-handed Z-up, so no axis swizzle is needed; only a uniform scale (WC3 models are roughly 1 unit = 1/32 of an SC2 metre-ish; tune empirically, common factor ~0.03-0.05... verify against a reference model rather than trusting a constant).
- extentsCount SHOULD equal SEQS count; some models lie. Guard with min().

_evidence: geoset.ts readMdx + getByteLength (mdx-m3-viewer); Retera GeosetChunk.java Geoset.load/getSize; hive MDX spec Geoset; local Read.ms ReadGEOS_

## [certain] Classic (SKIN-less) bone binding: GNDX gives each vertex a group index; MTGC slices MATS into groups; the group's MATS entries are node objectIds and every bone in the group has EQUAL weight 1/N.

Algorithm, exactly as the WC3 runtime does it (and as mdx-m3-viewer's shader does it):
```csharp
// 1. Slice matrixIndices into groups using matrixGroups (which holds the SIZE of each group).
var groups = new List<uint[]>();
int k = 0;
foreach (uint sz in geoset.MatrixGroups) { groups.Add(geoset.MatrixIndices.Skip(k).Take((int)sz).ToArray()); k += (int)sz; }
// groups.Count == matrixGroups.Length; k should end == matrixIndices.Length

// 2. Per vertex
for (int v = 0; v < vertexCount; v++) {
    byte g = geoset.VertexGroups[v];          // GNDX
    if (g == 255 || g >= groups.Count) { /* unbound vertex: model-space, treat as bound to nothing */ continue; }
    uint[] bones = groups[g];                 // node objectIds (indices into the global node array)
    float w = 1.0f / bones.Length;            // EQUAL WEIGHTS - there is no per-vertex weight in v800
    // vertex world matrix = (sum of bones' world matrices) / bones.Length
}
```
Evidence for equal weighting is the shader itself:
```glsl
mat4 getVertexGroupMatrix() { mat4 bone;
  for (int i=0;i<4;i++) if (a_bones[i]>0.0) bone += fetchMatrix(a_bones[i]-1.0, 0.0);
  return bone / a_boneNumber; }   // sum of matrices divided by count
```
Notes:
- It is the MATRICES that are averaged, not the transformed positions. For an m3 export with 4 weights of 255/N each (linear blend skinning), the result is *almost* the same and is what every converter does; the difference only shows on large rotations.
- Group sizes are usually 1-4, but real models exceed 4 (Ghostwolf special-cases up to 8 for e.g. the Water Elemental). M3 supports 4 influences; when N>4 you must pick the 4 "most important" (there is no importance data — take the first 4, or better, decompose by averaging the extra matrices' contribution; practical approach: keep first 4 and renormalise).
- MATS values are GLOBAL node objectIds, not bone-array indices: the same numbering space as BONE/HELP/ATCH/... objectId and as PIVT. They usually point at BONE nodes but broken models point at helpers; the game still loads them.
- GNDX == 255 or a group index >= matrixGroups.Length means "attached to nothing" (the game still renders it, un-skinned).
- Reforged (>800) HD geosets instead carry SKIN: uint8[vertexCount*8] laid out per vertex as [b0,b1,b2,b3,w0,w1,w2,w3] with bone ids limited to 0..255 and weights summing to 255. If SKIN is present, GNDX/MTGC/MATS are typically empty and must be ignored. Reforged models that are actually SD (converted) still use GNDX/MTGC/MATS even though version > 800 — so test `skin.Length != 0` rather than testing the version.

_evidence: mdx-m3-viewer src/viewer/handlers/mdx/setupgeosets.ts:101-140 and shaders/transforms.glsl.ts getVertexGroupMatrix(); src/utils/mdlx/sanitytest/utils.ts testGeosetSkinning (255 = not attached; skin weights must sum to 255); Retera GeosetChunk.java skin[] comment_

## [certain] GEOA holds variable-length GeosetAnimation entries: inclusiveSize(4), float alpha, uint32 flags, float[3] color, int32 geosetId, then optional (KGAO)(KGAC). Fixed size 28 bytes.

```
GeosetAnimation
  +0  uint32 inclusiveSize
  +4  float  alpha        // static alpha 0..1 (1 = fully visible)
  +8  uint32 flags        // 0x1 DropShadow, 0x2 Color (i.e. "the color field is meaningful")
  +12 float[3] color      // stored B,G,R !
  +24 int32  geosetId     // index into GEOS; -1 = none
  +28 optional (KGAO)  float alpha over time
      optional (KGAC)  float[3] color over time (B,G,R)
```
This is how WC3 hides/shows geosets per animation: a KGAO track with value 0 during a sequence hides the geoset (the runtime treats alpha < 0.1 as invisible for culling). This is the mechanism you must translate to SC2: an .m3 needs the equivalent per-animation visibility (M3 uses a batch/region visibility flag animation or a material alpha animation). Also note bones carry a geosetAnimationId back-reference.

_evidence: geosetanimation.ts readMdx (size-28 for animations) + readMdl/writeMdl (DropShadow=0x1, Color=0x2); Retera GeosetAnimationChunk.java; local Read.ms ReadGEOA (reads size, opacity, type, color, geoId then KGAO/KGAC when size>28)_

## [certain] All node-ish objects share one 'Node' header: uint32 inclusiveSize, char[80] name, int32 objectId, int32 parentId, uint32 flags, then optional (KGTR)(KGRT)(KGSC). Fixed part = 96 bytes.

```
Node (96 bytes + its own tracks)
  +0  uint32 inclusiveSize   // 96 + size of KGTR/KGRT/KGSC that follow (ONLY those three)
  +4  char[80] name
  +84 int32  objectId        // globally unique across ALL node types; also the PIVT index
  +88 int32  parentId        // objectId of parent, or -1 (0xFFFFFFFF) for a root
  +92 uint32 flags
  +96 optional (KGTR) float[3] translation   // relative to the pivot point
      optional (KGRT) float[4] quaternion    // x,y,z,w
      optional (KGSC) float[3] scaling
```
CRITICAL: the Node's inclusiveSize covers ONLY the node header + KGTR/KGRT/KGSC. Any additional per-type tracks (KATV, KLAV, KP2V, ...) live AFTER the type-specific fixed fields and are covered by the OUTER object's inclusiveSize, when that type has one.
flags bit meanings (a mix of a type tag and behaviour bits):
  0x00000  helper (i.e. no type bit set at all -> it's a HELP node)
  0x00001  don't inherit translation
  0x00002  don't inherit rotation   *(see disagreement note)*
  0x00004  don't inherit scaling    *(see disagreement note)*
  0x00008  billboarded
  0x00010  billboarded lock X
  0x00020  billboarded lock Y
  0x00040  billboarded lock Z
  0x00080  camera anchored
  0x00100  bone            (set on every BONE entry)
  0x00200  light           (LITE)
  0x00400  event object    (EVTS)
  0x00800  attachment      (ATCH)
  0x01000  particle emitter(PREM/PRE2)
  0x02000  collision shape (CLID)
  0x04000  ribbon emitter  (RIBB)
  0x08000  PREM: emitter uses MDL   / PRE2: unshaded
  0x10000  PREM: emitter uses TGA   / PRE2: sort primitives far Z
  0x20000  line emitter
  0x40000  unfogged
  0x80000  model space
  0x100000 XY quad
DISAGREEMENT on 0x2/0x4: the hive spec text and TaylorMouse's header comment say 0x2 = don't inherit ROTATION and 0x4 = don't inherit SCALING; Ghostwolf's code (genericobject.ts Flags enum) and Retera's Node.NodeFlag both say 0x2 = DontInheritScaling and 0x4 = DontInheritRotation. I trust the two independent CODE implementations (Ghostwolf + Retera agree) over the two prose comments (which are copies of each other): 0x2 = scaling, 0x4 = rotation. In practice both bits are rare and usually set together.
Which chunk uses a Node and whether there is an OUTER inclusiveSize:
  BONE  : Node then int32 geosetId, int32 geosetAnimationId. NO outer size -> entrySize = node.inclusiveSize + 8.
  HELP  : Node only.                     NO outer size -> entrySize = node.inclusiveSize.
  EVTS  : Node then "KEVT" block.        NO outer size -> entrySize = node.inclusiveSize + (12 + 4*trackCount).
  CLID  : Node then shape data.          NO outer size -> entrySize = node.inclusiveSize + 16/28/32 (see CLID).
  ATCH, LITE, PREM, PRE2, RIBB, CORN: uint32 outerInclusiveSize FIRST, then the Node, then type fields, then type tracks.
  CAMS  : uint32 inclusiveSize but NO Node (name is inline, no objectId/parentId — cameras are not in the node/pivot numbering).
Hierarchy: nodes[objectId] must be resolvable across ALL chunks. Build one array sized (max objectId + 1) and fill it as you parse BONE, LITE, HELP, ATCH, PREM, PRE2, RIBB, EVTS, CLID. Ghostwolf's runtime concatenates them in exactly that order (bones, lights, helpers, attachments, particleEmitters, particleEmitters2, ribbonEmitters, eventObjects, collisionShapes) and relies on objectId matching the resulting index — true for well-formed models, but index by objectId defensively.
Local transform of a node at time t:
  M_local = T(pivot) * T(KGTR(t)) * R(KGRT(t)) * S(KGSC(t)) * T(-pivot)
  M_world = M_parent_world * M_local     (with the inheritance bits masking out parent components)
where pivot = PIVT[objectId]. KGTR is an OFFSET from the pivot, not an absolute position — this is the single most common source of broken WC3 conversions.

_evidence: genericobject.ts readMdx (`readAnimations(stream, size - 96)`) + Flags enum; Retera Node.java + NodeFlag enum; hive MDX spec Node{} flag list; local Read.ms header comment lines 16-40 and ReadBONE (`SkipBytes (size - 80 - 16)` then geosetId/geosetAnimationId)_

## [certain] PIVT is a raw float[size/12][3] array of pivot points indexed by node objectId; there is one entry per node and its index IS the objectId.

```
PIVT payload = float[3] * (size/12)
```
pivotCount should equal the total number of nodes (bones+helpers+attachments+lights+emitters+event objects+collision shapes). PIVT[objectId] is that node's rest-pose position in MODEL space (not parent-relative).
Deriving a bind pose for .m3 export (v800 has no BPOS):
```
restLocal(node)  = Translate(PIVT[node.objectId] - PIVT[node.parentId])   // parent-relative rest translation
restWorld(node)  = Translate(PIVT[node.objectId])                          // rotation-free rest orientation
invBindPose      = inverse(restWorld(node))
```
Because classic MDX rest poses have NO rotation or scale (only a position), the bind pose is simply a translation matrix per bone. That maps cleanly onto the M3 `IREF` (inverse bind) matrices: IREF[b] = Translate(-pivot[b]).
Reforged >800 adds BPOS: `uint32 count; float[count][12]` — 12 floats = a 4x3 matrix stored as 4 rows of 3 (row-major: row0,row1,row2 = 3x3 basis; row3 = translation) per TaylorMouse's reader which assigns them to m.row1..row4.

_evidence: model.ts loadPivotPointChunk (`size / 12`); Retera PivotPointChunk.java; local Read.ms ReadPIVT (`nbr = tag.Size / 12.0`) and ReadBPOS (count then 4 rows of 3 floats)_

## [certain] BONE = Node + int32 geosetId + int32 geosetAnimationId, with NO outer inclusive size; HELP = Node alone.

```
Bone
  Node node            // its inclusiveSize covers node+KGTR/KGRT/KGSC only
  int32 geosetId       // index into GEOS this bone 'belongs' to, or -1 = Multiple
  int32 geosetAnimationId // index into GEOA, or -1 = None
entry size = node.inclusiveSize + 8

Helper
  Node node
entry size = node.inclusiveSize
```
Parse loop for BONE: `long end = chunkStart + chunkSize; while (pos < end) { long s = pos; uint incl = PeekUInt32(); ReadNode(); geosetId=..; geosetAnimId=..; }` — just read sequentially, the sizes take care of themselves as long as you honour the node's inclusiveSize when scanning its tracks.
Watch out: TaylorMouse's ReadBONE reads `size`, skips `size-96`, then reads geosetId/geosetAnimationId, then re-seeks to parse tracks — functionally the same thing.
Helpers are ordinary nodes with flags==0 (no type bit). For a skeleton export, bones AND helpers should both become joints (attachments/emitters can too, if you want their transforms preserved).

_evidence: bone.ts / helper.ts (mdx-m3-viewer); Retera BoneChunk.java / HelperChunk.java; hive spec "Bone { Node node; uint32 geosetId; uint32 geosetAnimationId }"; local Read.ms ReadBONE_

## [certain] ATCH entries are: uint32 inclusiveSize, Node, char[260] path, int32 attachmentId, then optional (KATV). Fixed overhead 268 + node.

```
Attachment
  +0   uint32 inclusiveSize        // = 268 + node.inclusiveSize + size(KATV)
  +4   Node node                   // 96 + node tracks
  ...  char[260] path              // usually empty; an MDL path for attached models
  ...  int32 attachmentId          // ordinal of the attachment point
  ...  optional (KATV) float visibility
```
Attachment names carry the semantics: "Overhead Ref", "Head Ref", "Hand Left Ref", "Hand Right Ref", "Weapon Ref", "Chest Ref", "Origin Ref", "Foot Left Ref", "Sprite First Ref", "Medium"/"Large" suffixes etc. For .m3 export map these onto SC2 attachment points (ATTACH/Ref_Head, Ref_Weapon, Ref_Origin, Ref_Overhead...). The classic naming convention is `<Name> Ref` and the m3 convention is `Ref_<Name>`.
TaylorMouse's ATCH reader hard-codes `SkipBytes (264 - correction)` after the node tracks, which is the 260-byte path + 4-byte id; use the size-driven approach instead.

_evidence: attachment.ts readMdx/getByteLength (268 + super); Retera AttachmentChunk.java (256+4 null+4 id); hive spec Attachment; local Read.ms ReadATCH_

## [certain] LITE entries: uint32 inclusiveSize, Node, uint32 type, float attenStart, float attenEnd, float[3] color, float intensity, float[3] ambientColor, float ambientIntensity, then up to 7 optional tracks. Fixed overhead 48 + node.

```
Light
  +0  uint32 inclusiveSize   // 48 + node.inclusiveSize + light tracks
  +4  Node node
  ..  uint32 type            // 0 Omnidirectional, 1 Directional, 2 Ambient
  ..  float  attenuationStart
  ..  float  attenuationEnd
  ..  float[3] color          // B,G,R
  ..  float  intensity
  ..  float[3] ambientColor   // B,G,R
  ..  float  ambientIntensity
  [ if version >= 1200: float shadowIntensity ]   // Reforged only
  ..  optional (KLAS)(KLAE)(KLAC)(KLAI)(KLBI)(KLBC)(KLAV)  in any order
```
48 = 4 (inclusiveSize) + 44 (type 4 + atten 8 + color 12 + intensity 4 + ambColor 12 + ambIntensity 4).
SC2 .m3 has LITE-equivalent point/spot lights; the mapping is straightforward but WC3 lights are rare outside doodads.

_evidence: light.ts readMdx/getByteLength(48+super) + LightType enum; Retera LightChunk.java (incl. shadowIntensity gate); hive spec Light; local Read.ms ReadLITE (reads size then headerSize then name — i.e. outer size then node size)_

## [certain] PREM (model-emitting particle emitter) entries: uint32 inclusiveSize, Node, 4 floats, char[260] spawnModelFileName, 2 floats, then up to 7 tracks. Fixed overhead 288 + node.

```
ParticleEmitter
  +0  uint32 inclusiveSize       // 288 + node.inclusiveSize + tracks
  +4  Node node
  ..  float emissionRate
  ..  float gravity
  ..  float longitude
  ..  float latitude
  ..  char[260] spawnModelFileName   // path of the .mdl/.mdx or .tga that is emitted
  ..  float lifeSpan
  ..  float initialVelocity (a.k.a. speed)
  ..  optional (KPEE)(KPEG)(KPLN)(KPLT)(KPEL)(KPES)(KPEV)
```
288 = 4 + 16 (4 floats) + 260 + 8 (2 floats).
The node flags 0x8000 (EmitterUsesMDL) / 0x10000 (EmitterUsesTGA) select whether spawnModelFileName is a model or a texture. PREM has no direct .m3 equivalent (SC2 emits particles, not models — use an M3 PAR system with a billboard, or drop it).

_evidence: particleemitter.ts readMdx/getByteLength(288+super); Retera ParticleEmitterChunk.java; hive spec ParticleEmitter_

## [likely] PRE2 (quad particle emitter) entries: uint32 inclusiveSize, Node, then 171 bytes of fixed fields, then up to 8 tracks. Total fixed overhead 175 + node.

```
ParticleEmitter2   (offsets are relative to the start of the fixed block, i.e. after inclusiveSize+node)
  +0   float speed
  +4   float variation
  +8   float latitude            // cone half-angle, radians
  +12  float gravity
  +16  float lifeSpan
  +20  float emissionRate
  +24  float length              // *see disagreement*
  +28  float width               // *see disagreement*
  +32  uint32 filterMode         // 0 Blend, 1 Additive, 2 Modulate, 3 Modulate2x, 4 AlphaKey
  +36  uint32 rows               // flipbook rows
  +40  uint32 columns            // flipbook cols
  +44  uint32 headOrTail         // 0 head, 1 tail, 2 both
  +48  float tailLength
  +52  float time                // "timeMiddle": normalised 0..1 position of the mid keyframe
  +56  float[3][3] segmentColor  // start,mid,end colours, each B,G,R
  +92  uint8[3] segmentAlpha     // start,mid,end alpha 0..255   <-- 3 BYTES, breaks alignment
  +95  float[3] segmentScaling   // start,mid,end particle size
  +107 uint32[3] headInterval        // start,end,repeat  (flipbook cell range)
  +119 uint32[3] headDecayInterval
  +131 uint32[3] tailInterval
  +143 uint32[3] tailDecayInterval
  +155 int32  textureId          // index into TEXS
  +159 uint32 squirt             // boolean
  +163 int32  priorityPlane
  +167 uint32 replaceableId
  = 171 bytes
  then optional (KP2S)(KP2R)(KP2L)(KP2G)(KP2E)(KP2N)(KP2W)(KP2V)
inclusiveSize = 4 + node.inclusiveSize + 171 + tracks   (Ghostwolf's getByteLength returns 175 + super)
```
Note the deliberate 3-byte segmentAlpha field: from +92 onward NOTHING is 4-byte aligned. Read strictly sequentially.
DISAGREEMENT (fields at +24/+28): Retera (ParticleEmitter2Chunk.java), TaylorMouse's Read.ms and the hive spec text all name +24 = length and +28 = width; Ghostwolf's TypeScript names +24 = width and +28 = length. It is purely a naming disagreement — the byte layout is identical — but if you surface these values in a UI, trust the 3-to-1 majority: +24 = length, +28 = width. Correspondingly the tracks KP2N/KP2W are ambiguous in the same way (hive spec: KP2N=length, KP2W=width; Ghostwolf's animationmap: KP2N=Width, KP2W=Length).
AGREEMENT on the tail: all three sources agree the last four uint32s are textureId, squirt, priorityPlane, replaceableId in that order — TaylorMouse's Read.ms alone lists textureId, squirt, replaceableTextureId, priorityPlane, which is WRONG (his ordering conflicts with Retera + Ghostwolf + hive spec). Use textureId, squirt, priorityPlane, replaceableId.

_evidence: particleemitter2.ts readMdx/writeMdx/getByteLength(175+super); Retera ParticleEmitter2Chunk.java load(); hive MDX spec ParticleEmitter2; conflicting local Read.ms ReadPRE2 lines 1284-1287_

## [certain] RIBB entries: uint32 inclusiveSize, Node, 52 bytes of fixed fields, then up to 6 tracks. Fixed overhead 56 + node.

```
RibbonEmitter (after inclusiveSize + node)
  +0  float heightAbove
  +4  float heightBelow
  +8  float alpha
  +12 float[3] color         // B,G,R
  +24 float lifeSpan
  +28 uint32 textureSlot     // flipbook cell
  +32 uint32 emissionRate    // edges per second (INTEGER)
  +36 uint32 rows
  +40 uint32 columns
  +44 int32  materialId      // index into MTLS
  +48 float  gravity
  = 52
  then optional (KRHA)(KRHB)(KRAL)(KRCO)(KRTX)(KRVS)
```
TaylorMouse's ReadRIBB reads these in a DIFFERENT order (above, below, vAlpha, vColor, edgesLife, flipBookSlot, edgesSec, rows, cols, mtlsId, gravity) which is actually the same order with different names — but note he reads alpha BEFORE color and lifeSpan AFTER color, matching Retera/Ghostwolf. Consistent.

_evidence: ribbonemitter.ts readMdx/getByteLength(56+super); Retera RibbonEmitterChunk.java; hive spec RibbonEmitter; local Read.ms ReadRIBB_

## [certain] EVTS entries are Node followed by a KEVT block with a DIFFERENT layout from every other track: char[4] "KEVT", uint32 trackCount, int32 globalSequenceId, uint32[trackCount] times. There is no outer inclusive size and no per-key value.

```
EventObject
  Node node                       // node.inclusiveSize covers node + KGTR/KGRT/KGSC
  char[4] "KEVT"
  uint32  tracksCount
  int32   globalSequenceId        // -1 = none
  uint32[tracksCount] tracks      // millisecond timestamps at which the event fires
entry size = node.inclusiveSize + 12 + 4*tracksCount
```
KEVT has NO interpolationType field and NO per-key value — it is the single exception to the universal track container. Do not route it through your generic track reader.
Semantics is encoded in the node NAME, first 4 chars + an id:
  SPNxxxxx = spawn model  ("Spawn" -> Objects\Spawnmodels\...)
  SPLxxxxx = splat
  UBRxxxxx = ubersplat
  FPTxxxxx = footprint
  SNDxxxxx = sound (looked up in the UnitCombatSounds/AnimLookups SLK tables)
For .m3 export these become SC2 events (M3 EVNT / SEQ event tracks) or can simply be dropped.
TaylorMouse's reader adds +1 to globalSequenceId and treats it as a parentId — that is a bug in his script; the field is a global sequence id.

_evidence: eventobject.ts readMdx/getByteLength(12 + tracks + super); Retera Tracks.java (KEVT: count then globalSequenceId then int[]); hive spec EventObject and "All tracks except for KEVT ... follow the same structure"; local Read.ms ReadEVTS_

## [certain] CAMS entries: uint32 inclusiveSize, char[80] name, float[3] position, float fieldOfView, float farClip, float nearClip, float[3] targetPosition, then optional (KCTR)(KTTR)(KCRL). Fixed 120 bytes. Cameras are NOT nodes (no objectId/parentId/pivot).

```
Camera
  +0   uint32 inclusiveSize   // 120 + tracks
  +4   char[80] name
  +84  float[3] position
  +96  float fieldOfView      // RADIANS (TaylorMouse converts with radToDeg)
  +100 float farClippingPlane
  +104 float nearClippingPlane
  +108 float[3] targetPosition
  = 120
  then optional (KCTR) float[3] camera position over time
                (KTTR) float[3] target position over time
                (KCRL) float    roll angle over time (radians; the only non-quaternion rotation)
```
The conventional camera is named "Portrait" (plus "Portrait Talk", "Portrait Alternate"), used by the WC3 UI portrait renderer.

_evidence: camera.ts readMdx/getByteLength(120+super); Retera CameraChunk.java; hive spec Camera; local Read.ms ReadCAMS_

## [certain] CLID entries: Node, uint32 type, then 1 or 2 float[3] vertices and an optional radius; no outer inclusive size.

```
CollisionShape
  Node node
  uint32 type   // 0 Box(cube) 1 Plane 2 Sphere 3 Cylinder
  float[3] v0
  if (type != 2) float[3] v1        // box/plane/cylinder have two corner points
  if (type == 2 || type == 3) float radius
entry size = node.inclusiveSize + 4 + 12 + (type!=2 ? 12 : 0) + ((type==2||type==3) ? 4 : 0)
           = node + 16 (sphere), node + 28 (box/plane), node + 32 (cylinder)
```
Ghostwolf's getByteLength: `16 + super` then `+12` if not sphere, `+4` if sphere or cylinder — confirming the above. The hive spec prose says "28 bytes for cubes and 16 bytes for spheres" (it omits the cylinder case; the code is authoritative).
Collision shapes are the WC3 hit-test volumes; SC2 has no direct equivalent worth exporting.

_evidence: collisionshape.ts readMdx + getByteLength + Shape enum; Retera CollisionShapeChunk.java (`vertexs = loadFloatArray(in, (type==2?1:2)*3)`); hive spec CollisionShape; local Read.ms ReadCLID_

## [certain] Reforged-only chunks BPOS, FAFX and CORN, plus the version-gated extra fields, are what separates >800 from classic 800.

```
BPOS (>=900)
  uint32 count
  float[count][12] bindPose      // 4 rows of 3 floats = 4x3 matrix per node

FAFX (>=900)
  FaceEffect[size/340] { char[80] target; char[260] path }   // FaceFX
  NOTE: the hive spec text says size/380 - that is WRONG. 80+260 = 340, confirmed by BOTH
  Ghostwolf's code (`size / 340`) and Retera's (`while (chunkSize >= 340)`) and TaylorMouse's
  (`nbr = tag.size / 340`). Trust 340.

CORN (>=900, PopcornFX)
  uint32 inclusiveSize        // 556 + node.inclusiveSize + tracks
  Node node
  float lifeSpan
  float emissionRate
  float speed
  float[3] color   (+ float alpha)   // Ghostwolf reads float[3] color then float alpha;
                                     // the hive spec text says float[4] color - same 16 bytes
  uint32 replaceableId
  char[260] path                     // .pkfx effect
  char[260] animationVisiblityGuide  // comma-separated flag string
  optional (KPPA)(KPPC)(KPPE)(KPPL)(KPPS)(KPPV)
  556 = 4 + 32 + 260 + 260
```
Version-gated field deltas vs classic v800 (all of these are ABSENT in a true 800 file):
- Material: + char[80] shader (>800)
- Layer:    + float emissiveGain (>=900), + float[3] fresnelColor + float fresnelOpacity + float fresnelTeamColor (>=1000), + a combined-HD texture table (shaderTypeId + textureId/typeIndex pairs) at >=1100, + 56 unknown bytes per TaylorMouse at >=1100
- Layer tracks: + KMTE (>=900), + KFC3/KFCA/KFTC (>=1000)
- Geoset:   + int32 lod + char[80] lodName (>800), + optional TANG (float[n*4] tangents) and SKIN (uint8[n*8]) (>=900)
- Light:    + float shadowIntensity (>=1200)
Local MaxScript specifics worth knowing: TaylorMouse's reader hard-refuses version <1000 (`if (version < 1000 or version > 1200) then throw`) — i.e. it CANNOT read classic 800 at all; it is only a cross-check for the shared structures. His v1000 Material also reads an extra char[80] name before "LAYS", which Ghostwolf models as the `shader` field for version > 800. Same bytes, different name.

_evidence: model.ts loadMdx/saveMdx version gates + faceeffect.ts + particleemitterpopcorn.ts(556+super); Retera FaceEffectsChunk.java, BindPoseChunk.java, ModelUtils gates; local Read.ms ReadVERS/ReadShader/ReadLayer/ReadFAFX/ReadCORN/ReadLITE_

## [likely] All float[3] colors in MDX BINARY are stored in B,G,R order; the MDL TEXT format writes them as R,G,B. Getting this wrong tints everything.

Proof from Ghostwolf's MDL token stream:
```ts
readColor(view) { read(); view[2]=readFloat(); view[1]=readFloat(); view[0]=readFloat(); read(); }
writeColor(name, value) { const b=value[0], g=value[1], r=value[2]; writeLine(`${name} { ${r}, ${g}, ${b} },`); }
```
So the in-memory Float32Array (which is a byte-for-byte image of the MDX binary) is [B, G, R], while the MDL text prints { R, G, B }. This affects: GeosetAnimation.color / KGAC, Light.color and .ambientColor / KLAC / KLBC, RibbonEmitter.color / KRCO, ParticleEmitter2.segmentColor, CornEmitter.color / KPPC.
C#: `float b = br.ReadSingle(), g = br.ReadSingle(), r = br.ReadSingle();`
TaylorMouse's ReadColor reads three floats and calls them r,g,b — so his MaxScript is actually loading BGR as RGB (a latent bug in his importer, and further confirmation that the binary is BGR since Ghostwolf and Retera both handle the swap explicitly at the MDL boundary).

_evidence: mdx-m3-viewer src/parsers/mdlx/tokenstream.ts:187-197 and :276-282; geosetanimation.ts writeMdl uses stream.writeColor; local Read.ms ReadColor_

## [certain] The MDL text format is a 1:1 token-based mirror of MDX with named blocks; every MDX field maps to a token, and 'static X' vs 'X { ... }' distinguishes a constant from a keyframe track.

Top-level block order emitted by Ghostwolf's writer (and accepted by WC3's MdlxConv):
```
Version { FormatVersion 800, }
Model "Name" { BlendTime n, MinimumExtent {..}, MaximumExtent {..}, BoundsRadius r, [AnimationFile ".."] }
Sequences N { Anim "Stand" { Interval { 0, 1000 }, [NonLooping,] [MoveSpeed x,] [Rarity r,] MinimumExtent{}, MaximumExtent{}, BoundsRadius r, } ... }
GlobalSequences N { Duration ms, ... }
Textures N { Bitmap { Image "path.blp", [ReplaceableId n,] [WrapWidth,] [WrapHeight,] } ... }
Materials N { Material { [ConstantColor,][SortPrimsFarZ,][FullResolution,][PriorityPlane n,] Layer { FilterMode Blend, [Unshaded,][SphereEnvMap,][TwoSided,][Unfogged,][NoDepthTest,][NoDepthSet,] static TextureID n, | TextureID n {..}, [TVertexAnimId n,][CoordId n,] static Alpha 1, | Alpha n {..}, } } }
TextureAnims N { TVertexAnim { Translation {..} Rotation {..} Scaling {..} } }
Geoset { Vertices n {..} Normals n {..} TVertices n {..} VertexGroup {..} Faces g c { Triangles { {i,i,i,...} } } Groups g m { Matrices { b,b }, ... } MinimumExtent{} MaximumExtent{} BoundsRadius r Anim{MinimumExtent..}(one per sequence) MaterialID n, SelectionGroup n, [Unselectable,] }
GeosetAnim { [DropShadow,] static Alpha a, | Alpha n {..}, [static Color {r,g,b},] GeosetId n, }
Bone "name" { ObjectId n, [Parent n,] [Billboarded,..] GeosetId n|Multiple, GeosetAnimId n|None, [Translation n{..}][Rotation n{..}][Scaling n{..}] }
Light / Helper / Attachment / PivotPoints N { {x,y,z}, ... } / ParticleEmitter / ParticleEmitter2 / RibbonEmitter / Camera / EventObject / CollisionShape
```
Track syntax in MDL:
```
Translation 3 {
	Hermite,
	GlobalSeqId 2,          // omitted entirely when globalSequenceId == -1
	0: { 0, 0, 0 },
		InTan { 0, 0, 0 },
		OutTan { 0, 0, 0 },
	...
}
```
Interpolation tokens: DontInterp | Linear | Hermite | Bezier — matching enum 0/1/2/3. InTan/OutTan lines appear ONLY when interpolation > Linear, exactly mirroring the binary.
Semantic gotchas when round-tripping MDL<->MDX:
- Colors are RGB in text but BGR in binary (see the BGR finding).
- `GeosetId Multiple` == -1, `GeosetAnimId None` == -1.
- `Unselectable` == selectionFlags value 4 (not a bit-or of other values in practice).
- Sequence `NonLooping` == flags value 1.
- MDL omits any field equal to its default, so an MDL->MDX writer must fill defaults (alpha=1, color=1,1,1, textureAnimationId=-1, parentId=-1).

_evidence: mdx-m3-viewer src/parsers/mdlx/model.ts saveMdl/loadMdl, geoset.ts writeMdl, bone.ts writeMdl, animations.ts readMdl/writeMdl, tokenstream.ts_

## [likely] Practical parse-order and validation rules that a C# parser should encode (derived from Ghostwolf's sanity tester and from how the game itself tolerates bad data).

1. Never infer an object count from anything but (a) chunkSize/fixedSize for SEQS(132)/TEXS(268)/GLBS(4)/PIVT(12)/FAFX(340) or (b) the per-object inclusiveSize walk for MTLS/TXAN/GEOS/GEOA/LITE/ATCH/PREM/PRE2/RIBB/CAMS/CORN or (c) node.inclusiveSize + fixed tail for BONE/HELP/EVTS/CLID.
2. Always clamp to the chunk end: `while (pos < chunkEnd)`. Some models have trailing garbage.
3. Optional track detection must be size-driven, not tag-peek-driven (unknown tags exist).
4. Defensive defaults: textureId -1, textureAnimationId -1, geosetId -1, geosetAnimationId -1, parentId -1, globalSequenceId -1, alpha 1, color (1,1,1).
5. Known real-world breakage the game tolerates and you must too: GNDX referencing a nonexistent matrix group; GNDX == 255 ("attached to nothing"); matrixIndices pointing at a non-Bone node; geosets with 0 vertices; extentsCount != sequence count; sequences with intervalStart == intervalEnd; negative keyframe times; duplicate sequence start frames.
6. Bone-count ceiling: the classic runtime handles many bones, but a Reforged HD SKIN can only index 0..255. mdx-m3-viewer switches its bone index buffer to Uint16 above 255 bones.
7. For .m3 export, the three things that actually cause the user's reported animation breakage are: (a) forgetting that KGTR is a pivot-relative OFFSET (must add PIVT[objectId]); (b) treating Hermite/Bezier InTan/OutTan as deltas rather than absolute values, or using inTan[i]/outTan[i] instead of outTan[i]/inTan[i+1]; (c) not baking global-sequence tracks per SC2 animation. The safest export path is to RESAMPLE every node's world transform at a fixed rate (e.g. every 33 ms) over each sequence's interval, then write M3 SD tracks with linear interpolation — this sidesteps interpolation-type mismatches entirely.
8. For .m3 export of the alpha problem: for each geoset, resolve materialId -> layers; layer 0 with filterMode 0/1 and a replaceableId-1 texture is the team-colour underlay; the visible diffuse is the layer with filterMode 2 whose texture has replaceableId 0. Emit ONE m3 material with the diffuse, and composite the team colour into the diffuse's transparent areas (or emit an m3 team-colour layer). filterMode 1 must become an m3 alpha-test material with threshold 0.75, not a blended one.

_evidence: mdx-m3-viewer src/utils/mdlx/sanitytest/utils.ts (testGeosetSkinning, testVertexSkinning, 255-check, weight!=255 check); src/viewer/handlers/mdx/setupgeosets.ts (Uint16 skin above 255 bones); hive MDX spec filter-mode blend table_

## Open questions

- ParticleEmitter2 fields at +24/+28: Retera + TaylorMouse + the hive spec text say (length, width); Ghostwolf's TypeScript says (width, length). Byte layout is identical, only the semantic naming is disputed. Same ambiguity for the KP2N/KP2W tracks. Resolve empirically by loading a known emitter (e.g. a line emitter with a long thin shape) and comparing against the in-game visual, or against an MDL exported by MdlxConv.
- Layer shadingFlags bits 0x4 and 0x8 are undocumented in every source consulted (hive spec literally writes '0x4: ?' and '0x8: ?'). They do occur in shipped models. Unknown effect.
- Material flags 0x1 ConstantColor / 0x8 SortPrimsNearZ / 0x20 FullResolution have no documented runtime effect beyond the names; ConstantColor is believed to disable per-vertex lighting modulation.
- Geoset selectionFlags: only value 4 (Unselectable) is ever written by tooling; whether it is a bitfield with other meaningful bits is unverified.
- The MODL blendTime field's exact runtime effect in retail WC3 (Ghostwolf claims it is only used by the defunct Art Tools previewer; the value 150 is nearly universal in shipped models).
- Exact unit scale factor for WC3 -> SC2 (.m3) conversion. No source documents it; it must be calibrated against a reference model (e.g. compare a Footman's height to a Marine's).
- The Reforged v1100+ Layer's 56 unknown bytes (TaylorMouse skips them; Retera decodes part of it as shaderTypeId + a textureId/typeIndex table) - not needed for classic 800 but relevant if the app also reads Reforged HD models.
- Whether the classic runtime's DontInherit bits are 0x2=scaling/0x4=rotation (Ghostwolf + Retera code) or 0x2=rotation/0x4=scaling (hive spec prose + TaylorMouse comment). Code majority favours scaling=0x2, but this was not verified against the game.

## Sources

- https://github.com/flowtsohg/mdx-m3-viewer - src/parsers/mdlx/{model,extent,sequence,material,layer,texture,textureanimation,animatedobject,genericobject,animations,animationmap,geoset,geosetanimation,bone,helper,attachment,light,camera,eventobject,collisionshape,particleemitter,particleemitter2,ribbonemitter,particleemitterpopcorn,faceeffect,tokenstream}.ts (master branch, downloaded and read in full)
- https://github.com/flowtsohg/mdx-m3-viewer - src/viewer/handlers/mdx/setupgeosets.ts and src/viewer/handlers/mdx/shaders/transforms.glsl.ts (authoritative for classic vertex-group skinning = averaged matrices)
- https://github.com/flowtsohg/mdx-m3-viewer - src/utils/mdlx/sanitytest/utils.ts (validation rules, GNDX 255 semantics, SKIN weight normalisation)
- https://github.com/Retera/ReterasModelStudio - craft3data/src/com/hiveworkshop/wc3/mdx/*.java (Node.java, GeosetChunk.java, MaterialChunk.java, LayerChunk.java, SequenceChunk.java, TextureChunk.java, AttachmentChunk.java, LightChunk.java, CameraChunk.java, EventObjectChunk.java, CollisionShapeChunk.java, BoneChunk.java, HelperChunk.java, PivotPointChunk.java, BindPoseChunk.java, FaceEffectsChunk.java, ModelChunk.java, GlobalSequenceChunk.java, TextureAnimationChunk.java, RibbonEmitterChunk.java, ParticleEmitterChunk.java, ParticleEmitter2Chunk.java, Tracks.java, GeosetTranslation.java, MdxUtils.java)
- https://www.hiveworkshop.com/threads/mdx-specifications.240487/ - the Ghostwolf/BlinkBoy/Magos MDX specification (full text retrieved and read)
- C:\\Program Files\\Autodesk\\3ds Max 2016\\scripts\\Startup\\Warcraft_3_Reforged_Tools\\GriffonStudios_Warcraft_3_Reforged_Read.ms - TaylorMouse's MaxScript MDX reader (Reforged 1000/1100/1200 only; cross-checked for shared structures, several disagreements noted)
- https://wowdev.wiki/MDX - WoW-alpha MDX v1300 (a LATER divergent descendant of the WC3 format; consulted but NOT used as a source for v800 layouts)
