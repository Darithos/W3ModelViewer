# MDX format — verified against the local Reforged install

Everything here was confirmed by parsing real files from `C:\games\Warcraft III`
(Warcraft III Reforged, build **2.0.4.23745**, CASC product `w3`) with `spike/MdxProbe`.
Where this contradicts the wowdev wiki or TaylorMouse's 3ds Max scripts, **this document wins** —
those describe older Reforged builds.

## 1. Storage

CascLib opens the install directly and reports product `w3`, **135,543** files.

Asset names are colon-separated virtual paths, exactly as CascView displays them:

| Art set | Name form | Example |
| --- | --- | --- |
| Classic / SD | `war3.w3mod:<path>` | `war3.w3mod:units\human\knight\knight.mdx` |
| Reforged / HD | `war3.w3mod:_hd.w3mod:<path>` | `war3.w3mod:_hd.w3mod:units\human\knight\knight.mdx` |

Backslash-separated variants (`war3.w3mod\units\...`) do **not** work. Lookup is case-insensitive.

> **Trap:** `CascOpenFile` *succeeds* for names the storage does not have, handing back a
> **zero-length** file rather than failing. A bare open is therefore not a membership test —
> `Wc3Storage.Exists` checks the size, and `TryReadFile` treats size 0 as absent.

> **Trap:** full enumeration (`CascFindFirstFile`/`CascFindNextFile`) reaches ~125,000 names in
> **30 ms** and then stalls indefinitely before the reported 135,543. It must be run time-capped, or
> avoided in favour of direct name lookup.

## 2. Version does not tell you the layout

Both the SD and the HD knight report **`VERS` = 1200**. The version field says nothing about which
material or skinning scheme the file uses — **detect structurally**:

* **Material scheme** — read the material's `size, priorityPlane, flags`, then look at the next 4
  bytes. `"LAYS"` at **+12** is the layout used by *every* model in this build, SD and HD alike.
  (The `char[80] shaderName` at +12 that TaylorMouse reads for v1000, putting `LAYS` at +92, does
  **not** occur in build 2.0.4.)
* **Skinning scheme** — a geoset containing a `SKIN` sub-chunk is HD (4 bone indices + 4 weights per
  vertex); one without it is SD (classic `GNDX`/`MTGC`/`MATS` matrix groups). In HD geosets `GNDX`
  is present but has **count 0**.

## 3. Geoset layout (identical for SD and HD except the optional chunks)

Verified offset-by-offset against HD knight geoset[0] (inclusive size 558,048) and SD knight
geoset[0] (22,453) — every field chains exactly to the stated total.

```
int32 inclusiveSize          // includes these 4 bytes
VRTX  count, float3[count]                 // positions
NRMS  count, float3[count]                 // normals
PTYP  count, int32[count]                  // primitive types (4 = triangle list)
PCNT  count, int32[count]                  // primitive counts
PVTX  count, uint16[count]                 // count is the INDEX count, not triangles
GNDX  count, uint8[count]                  // vertex group indices; count 0 on HD
MTGC  count, int32[count]                  // matrix group sizes
MATS  count, int32[count]                  // flat list of node objectIds
      int32   materialId                   // MTLS index
      int32   sectionGroupId
      int32   sectionGroupType
      int32   lodId                        // LOD level; 0 = LOD_0
      char[80] lodName                     // e.g. "LOD_0"
      float[7] bounds                      // radius, then min[3], then max[3]
      int32   numExtents                   // == sequence count, or 0
      float[numExtents * 7] extents        // per-sequence bounds, same radius-first order
[TANG count, float4[count]]                // HD only
[SKIN byteCount, uint8[byteCount]]         // HD only; byteCount = vertexCount * 8
                                           //   4 bone indices then 4 weights (weight/255)
UVAS  int32 layerCount
UVBS  count, float2[count]                 // repeated layerCount times (HD uses 2)
```

Worked example — SD knight geoset[0]: `MATS` ends at +17,645 and `UVAS` begins at +18,109; the
464-byte gap is `128 + 12*28`, and the model has exactly **12** sequences. HD knight geoset[0]: the
gap is exactly **128** bytes (`numExtents = 0`).

> **Bounds order is radius-first** — `float radius; float3 min; float3 max` (28 bytes) — in `MODL`,
> `SEQS` and geoset bounds alike. The wowdev wiki's `CMdlBounds` (box then radius) is wrong here.
> Confirmed against TaylorMouse's `ReadMODL`/`ReadSEQS`.

## 4. Material and layer layout

```
MTLS chunk = repeated, read until the chunk ends:
  int32 inclusiveSize
  int32 priorityPlane
  int32 flags
  "LAYS" int32 layerCount
  layer[layerCount]:
     int32 inclusiveSize        // 596 for HD knight, 68/136 for SD
     int32 filterMode           // 0 none, 1 transparent(alpha test), 2 blend,
                                // 3 additive, 4 addAlpha, 5 modulate, 6 modulate2x
     int32 shadingFlags         // 1 unshaded, 2 sphereEnvMap, 4 wrapW, 8 wrapH,
                                // 16 twoSided, 32 unfogged, 64 noDepthTest, 128 noDepthSet
     int32 textureId            // TEXS index
     int32 textureAnimationId   // TXAN index, -1 = none
     int32 coordId              // UV layer
     float alpha
     float emissiveMultiplier
     float fresnelR, fresnelG, fresnelB
     float fresnelMultiplier
     float teamColorMultiplier
     -- the texture-slot table (the 56 bytes TaylorMouse skips as "a set of 14 integers") --
     int32 unknown              // 1 on HD layers, 0 on SD layers
     int32 slotCount            // 6 on HD, 1 on SD
     (int32 textureId, int32 slot)[slotCount]
     -- then optional KMTA / KMTE / KMTF tracks fill the rest of inclusiveSize --
```

**Build 2.0.4 uses this one layer layout for SD and HD alike** — including the six PBR floats and the
slot table. An SD layer simply binds a single slot (diffuse). Verified by byte accounting on SD
knight material[0]: layer size 140, the six floats run +28..+52, the table is `{0, 1, (0,0)}` at
+52..+68, and `KMTA` with 7 keys occupies +68..+140 exactly.

So "does it have a slot table" does **not** distinguish SD from HD — "does it bind the normal or ORM
slot" does (`MdxLayer.IsPbr`), and for the model as a whole the reliable test remains the presence of
a geoset `SKIN` chunk.

### The texture-slot table (decoded here for the first time)

The 6 `(textureId, slot)` pairs are what bind an HD material to its PBR texture set. Slots follow
the Reforged convention:

| Slot | Meaning |
| --- | --- |
| 0 | Diffuse / base colour |
| 1 | Normal map |
| 2 | ORM — R = occlusion, G = roughness, B = metallic |
| 3 | Emissive |
| 4 | Team colour |
| 5 | Environment map |

Verified on HD knight material[2], whose pairs are `(6,0) (7,1) (8,2) (3,3) (4,4) (5,5)` against
`TEXS[6..8] = Human_Knight_Hair_Diffuse / _Normal / _ORM`, `TEXS[3] = Textures/Black32.blp`,
`TEXS[4] = replaceableId 1 (team colour)`, `TEXS[5] = ReplaceableTextures/EnvironmentMap.blp`.
Material[0] maps `(0,0) (1,1) (2,2) (3,3) (4,4) (5,5)` → the `Main_` set.

**This is the key to HD texturing.** Reading only `layer.textureId` (the pre-Reforged field) yields
just the diffuse and silently loses the normal/ORM/emissive/team-colour bindings.

## 5. TEXS

Fixed 268-byte entries: `uint32 replaceableId, char[260] fileName, uint32 flags`.

* `replaceableId = 1` with an **empty** filename means **team colour**; `2` means team glow.

### Where team colour actually comes from (measured, not assumed)

Two different mechanisms, and only one of them is live in the shipping HD art:

* **Classic SD** — a material stacks an *opaque* layer whose diffuse **is** the `replaceableId 1`
  texture, then blends the real diffuse over it (cryptfiend's SD material 1 is exactly this:
  `Diffuse=tex1(,REPL1)` filter `None`, then `Diffuse=tex0(CryptFiend.blp)` filter `Blend`). Where
  the upper layer's alpha is low the player colour shows through. 19 of the 102 classic unit models
  under `_addons\hd2.w3addon\…` use it — all Undead (abomination, acolyte, banshee, cryptfiend,
  frostwyrm, gargoyle, ghoul, meatwagon, necromancer, skeleton, …).
* **Reforged HD** — the layer carries a `teamColorMultiplier` scalar and binds slot 4 to
  `replaceableId 1`. **Measured across all 102 HD unit models: `teamColorMultiplier` is 0 on every
  layer of every model**, and every HD body diffuse is 100% opaque in alpha (footman, grunt,
  cryptfiend all read `alpha opaque=100%` outside genuine cutouts). So the HD art carries **no
  per-texel team mask and no active team multiplier** — Blizzard bakes the team colour into the
  diffuse RGB. The slot-4 binding is present on every material but unused.

The practical consequence for export: there is nothing to extract for an HD unit, and an exporter
that invents a mask from the diffuse alpha will paint player colour over **cut-out holes**, because
on HD that channel is coverage. This was tried and reverted — see
`memory/reforged-hd-alpha-is-team-mask.md`.
* HD entries name **`.tif`** files with **forward slashes**
  (`Units/Human/Knight/Human_Knight_Main_Diffuse.tif`) while the CASC actually stores **`.dds`**.
  Texture resolution must swap the extension and normalise separators.
* SD entries name `.blp` files with backslashes (`Textures\Knight.blp`).

## 6. Chunk inventory observed

| Model | Chunks |
| --- | --- |
| SD knight | VERS MODL SEQS GLBS MTLS TEXS GEOS GEOA BONE HELP ATCH PIVT PRE2 EVTS CLID |
| HD knight | VERS MODL SEQS MTLS TEXS GEOS GEOA BONE ATCH PIVT CORN CAMS EVTS CLID FAFX BPOS |

HD models carry no `HELP` or `GLBS`; they add `CORN` (popcorn FX), `CAMS`, `FAFX` (FaceFX) and
`BPOS` (bind pose). Chunk order is not guaranteed — always dispatch on the tag and skip by size.
