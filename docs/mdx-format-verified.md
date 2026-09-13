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
| Definitive Edition / DE (patch 3.0) | `war3.w3mod:_de.w3mod:<path>` | `war3.w3mod:_de.w3mod:units\human\scarletfootman\scarletfootman.mdx` |

Patch 3.0 ("Definitive Edition", September 2026) added the `_de.w3mod` tree and re-exported every
model in all three trees as **`VERS` 1800** — the classic footman reports 1800 too, so the version
is still no layout discriminator. The DE tree is a third art set, not a republish of HD: of 5,965
DE models, 974 exist in no other tree (the Scarlet footman, a full set of new human spells), 4,973
share a path with HD, and a byte comparison of 120 of those found a third differ. 66 HD models have
no DE counterpart. Storage grew to **175,026** files.

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
[SKIN count, elem[count]]                  // HD only; count = vertexCount * 8:
                                           //   4 bone indices then 4 weights (weight/255)
                                           //   build 2.0.4: elem = uint8  (8 bytes per vertex)
                                           //   patch 3.0:   elem = uint16 (16 bytes per vertex)
UVAS  int32 layerCount
UVBS  count, float2[count]                 // repeated layerCount times (HD uses 2)
```

> **Patch 3.0 widened SKIN.** The header count is unchanged (still `vertexCount * 8`) but every
> element is now a `uint16` — indices because DE rigs pass 255 bones (the HD knight's geosets reach
> bone 164; before 3.0 the same knight was under 255 by construction), weights along with them, still
> 0..255 and summing to ~255. Verified on the DE scarletfootman (365-vertex geoset: `SKIN` at +19,072,
> 5,840 bytes = 365 × 16, then `UVAS`), the 3.0 HD footman and the 3.0 HD knight. Read at the old
> width the record `6e 00 6e 00 6e 00 6e 00 | ff 00 00 00 00 00 00 00` becomes vertex A = bones
> 110,0,110,0 with weights 110,0,110,0 (half the mesh glued to the root) and vertex B = bones 255,0,0,0
> with no weight at all; then the unread second half shifts `UVAS` off its byte. Detect the width the
> way `MdxReader` does: whichever length lands the next sub-chunk tag where a tag belongs. Hive custom
> HD models (VERS 1000/1100) still use the byte form.

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

### Team glow (replaceable 2), measured

The game ships the real art at `ReplaceableTextures\TeamGlow\TeamGlow<nn>.blp`, one per player
slot, and it is worth reading rather than guessing: **32x32, alpha 255 throughout, and a radial
falloff in the player's colour that peaks at 123/255** — under half brightness. `TeamGlow00` is pure
red (player 0's colour is 255,4,2) and `TeamGlow01` reads 0,32,123 at its centre against player 1's
0,66,255, i.e. the same 0.482 falloff. So the brightest channel of `TeamGlow00` **is** the falloff
in bytes, which is how `MaterialCompositor.GlowMask` extracts it.

Usage across 400 unit models per art set: **SD 102 models, 115 geoset layers (Additive x92,
None x23, never inside a multi-layer material) and 4 particle emitters. HD: 1.** Like team colour,
this is classic-art machinery that Reforged abandoned.

### Where team colour actually comes from (measured, not assumed)

Two different mechanisms, and only one of them is live in the shipping HD art:

* **Classic SD** — a material stacks an *opaque* layer whose diffuse **is** the `replaceableId 1`
  texture, then blends the real diffuse over it (cryptfiend's SD material 1 is exactly this:
  `Diffuse=tex1(,REPL1)` filter `None`, then `Diffuse=tex0(CryptFiend.blp)` filter `Blend`). Where
  the upper layer's alpha is low the player colour shows through. 19 of the 102 classic unit models
  under `_addons\hd2.w3addon\…` use it — all Undead (abomination, acolyte, banshee, cryptfiend,
  frostwyrm, gargoyle, ghoul, meatwagon, necromancer, skeleton, …).
* **Reforged HD** — the mask is the **alpha channel of the ORM map** (slot 2). The `teamColorMultiplier`
  scalar is 0 on every layer of every HD unit model and every HD body diffuse is 100% opaque in
  alpha, so neither of *those* is the signal — but the ORM's fourth channel, which the
  occlusion/roughness/metallic packing leaves free, holds the team region exactly. Measured on the
  HD footman: `Human_Footman_Main_ORM` alpha is his tabard panels and shoulder emblems (10.5% of
  the atlas), `Shield_ORM` alpha is the shield crest (7.7%) and nothing else, `Pauldron_ORM` is the
  pauldron trim (5.4%), and `Helmet_ORM` / `Sword_ORM` / `Corpse_ORM` are alpha-empty — which is
  right, none of those is player-coloured. Across 400 HD unit models: **486 ORM textures carry such
  a mask, 330 are alpha-empty, 10 are neither.**

  The remaining **11** have *uniformly opaque* alpha, which must be rejected rather than read as
  "team-colour everything": they are unauthored placeholders — hair, dragon wings, ship sails —
  whose RGB is a degenerate constant too (`Human_Footman_Hair_ORM` is R 0, G 255, B 255 everywhere).
  So the rule is: a mask needs both dark and bright texels to be real.
  `MaterialCompositor.TeamMaskOf` implements exactly this, and its classic counterpart.

The practical consequence for export: **do not** invent an HD mask from the diffuse alpha — on HD
that channel is coverage, and using it paints player colour over cut-out holes. That was tried and
reverted; see `memory/reforged-hd-alpha-is-team-mask.md`. Read the ORM's alpha instead.
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

## 7. CORN — PopcornFX emitters (verified 2026-09-13)

Each entry is a node (inclusive size, then the standard node block) followed by a fixed payload
that matches the published spec exactly, checked on `holyboltspecialart.mdx`:

```
C4Color colorMultiplier      (1,1,1,1) on every stock model looked at
C4Color teamColor            (1,1,1,0): alpha 0 = not team-coloured
char[260] path               "Abilities/Spells/Human/HolyBolt/HolyBoltSpecialArt.pkfx"
char[260] popcornFlags       "Always=on, Death=off, Decay=off"
tracks                       KPPA KPPC KPPE KPPL KPPS KPPV, any subset, found by tag
```

The path names the PopcornFX *source*; the archive ships the *bake* at the same path with the
extension `.pkb`, in the same tree as the model. All 2,165 bakes in the archive resolve and parse.
The node's pivot is where the effect is spawned; the effect's own layout is inside the bake, in
metres (1 m = 50 units). How the bake itself is laid out is in `mdxres/research/popcornfx-bake.md`;
its compiled per-particle scripts — a 12-opcode virtual machine the viewer executes — are in
`mdxres/research/popcornfx-vm.md`.

Unit models put their spell effects here too: the priest's `PriestAttack` (gated `Always=off,
Attack Spell=on`, with a `KPPE` rate-multiplier track) and the paladin's `HeroPaladinSpell` and
`Hero_Glow` (`Always=On, Death=Off, Dissipate=Off, Portrait=Off`, with `KPPA` and `KPPV` tracks).
`Hero_Glow` is how every HD hero gets its glow — there is no team-glow emitter in HD.
