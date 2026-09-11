# m3verify — external validation of exported .m3 files

> **The app writes .m3 directly — no Blender, no add-ons.** A detour through m3studio was tried
> and reverted: it changed nothing, because the writer was never at fault. The real defect was
> **too many bones in one region** (see `m3regions.py`), which no well-formedness check can see.

Validates the viewer's `.m3` output against **m3studio** (Solstice245's Blender add-on), whose
`structures.xml` is the most complete machine-readable description of the format SC2 accepts, and
whose exports are proven to load in the SC2 editor. This is the harness that caught the original
"checkered sphere" rejection: the writer emitted a section tagged `COL_` where the real format
uses the 3-letter tag `COL` (on disk: `4C 4F 43 00`).

Requirements: Blender 3.x with the m3studio add-on installed as `addons\m3studio-main`
(any of the user script paths). The scripts run on Blender's bundled Python — no separate
Python install needed.

| Script | Run with | What it does |
| --- | --- | --- |
| `m3accept.py` | `blender --background --factory-startup --python m3accept.py -- model.m3` | **The acceptance test.** Stages a headless-patched copy of m3studio, registers it, imports the .m3 and prints a JSON report (bones, meshes, materials, animation groups). m3studio's loader raises on any `expected_value` violation in any section, so a clean import is a field-level conformance pass. Exit 0 = pass. |
| `m3tags.py` | `<blender>\3.3\python\bin\python.exe m3tags.py a.m3 b.m3 …` | Lists every (tag, version) pair in the section index and checks each against structures.xml. Catches unknown tags/versions — the class of bug SC2 rejects files for. |
| `m3compare.py` | `…python.exe m3compare.py good.m3 mine.m3` | Field-by-field MODL diff of two files through m3studio's parser, plus drilldowns (DIV_/REGN/BAT_/BONE/MAT_/SEQS/STC_/STS_). Use a known-good m3studio export as `good`. |
| `m3dump.py` | `…python.exe m3dump.py model.m3` | Raw MD34 section table (tag/ver/reps/offset/size) + MODL hexdump. No add-on needed. |
| `m3invariant.py` | `…python.exe m3invariant.py a.m3 …` | Checks **parent-first bone order** (SC2 composes worlds in one forward pass — MDX node order violates this and renders as exploded spikes) and the bind invariant `IREF · restWorld = origin` per bone. |
| `m3regions.py` | `…python.exe m3regions.py *.m3` | Per-region **bone palette** sizes. SC2 skins each draw call from a fixed-size matrix palette; overflow is invisible at rest (all bones resolve to identity there) and explodes the moment anything animates. Across 50 sampled Blizzard units the max is **45** — `sm_raynormarine` has 118 bones split into 10 regions of ≤36. Reforged geosets routinely need 80+, so `M3Exporter.MaxRegionBones` splits them. |
| `m3sim.py` | `…python.exe m3sim.py model.ref.json` | **Semantic** check, not structural: replays the .m3's own bone keys, composes worlds, skins the vertices, and compares against what the viewer renders (reference emitted by `MdxProbe --simref`). Proves the file *means* what the viewer shows — this is what ruled the animation math innocent and pointed at the palette. |
| `m3mat.py` | `…python.exe m3mat.py model.m3 [--nodisk]` | Every MAT_'s blend mode, alpha test, priority, decoded flag names and layer bitmap paths, each marked OK or MISSING against the files next to the .m3. Point it at a Blizzard model to read their recipe; `--nodisk` skips resolution when the textures are not extracted. |
| `m3uvalpha.py` | `…python.exe m3uvalpha.py model.m3` | Per-region **UV coverage vs. diffuse alpha** — rasterises each region's UV triangles and reports the share of texels it samples that are transparent. This is what proves a material is or is not a cutout; a body region reads 0.00% on an atlas that is 5% transparent overall. |
| `ddsalpha.py` | `…python.exe ddsalpha.py tex.dds` | Alpha histogram plus an ASCII map of *where* a texture's transparency lives. Distinguishes authored cut-out silhouettes from unused atlas padding. |
| `m3billboard.py` | `…python.exe m3billboard.py <model.m3 or folder> [--flat 0.02]` | Every BBSC entry (type, camera_look_at, up/forward) plus the **bone-local axis its card lies flat on and the side its texture is drawn on** (from UV correlation). Across Blizzard's corpus, type 6 with identity quaternions reads `right=+x up=+z front=-y` in 331 of 346 cards — SC2 turns local -Y to the camera. Model-side counterpart: `MdxProbe --billboards [n] [sd\|hd]`. |
| `m3bones.py` | `…python.exe m3bones.py model.m3 [--all]` | Bone names, decoded flags and parents, plus raw BBSC entries. No add-on needed. |
| `m3vis.py` | `…python.exe m3vis.py model.m3` | Raw-parses every LAYR for its colour_value default alpha (**rest visibility**) and `color_channels`. A diffuse layer defaulting to alpha 0 is invisible in any sequence carrying no visibility key — that renders as a *missing geoset*, and this is the check that rules it in or out. |

Model-side counterpart: `MdxProbe --geosets <cascPath>` lists every geoset with its LOD, vertex and
triangle counts, material and GEOA visibility, and totals LOD0 — compare its triangle total against
`m3uvalpha.py`'s per-region counts to prove no geometry was dropped in export.

The compare/tags scripts import m3studio's `io_m3.py` directly (it is bpy-free); they look for the
add-on at the path hardcoded near the top — adjust if the Blender version changes.

Known-good references for `m3compare.py`:

- `C:\Users\Darithos\Documents\D3Exports\OmniNPC_Male_Skeleton_A\OmniNPC_Male_Skeleton_A.m3`
  (m3studio export that loads in SC2).
- A real Blizzard unit, extracted from the SC2 install with
  `CascProbe "C:/Games/StarCraft II" --extract "mods\liberty.sc2mod\base.sc2assets\assets\units\terran\marine\marine.m3" marine.m3`
  (`--find <substring>` lists paths). The marine confirms: MODL flags 0x180D53 as we write,
  vertex flags 0x182007D is the valid single-UV subset of its 0x186007D, root bones 0x2200,
  SC2 unit scale ≈ 1.5 units tall (Blizzard sizes via root-bone scale, e.g. 1.18 on the marine).

Verified conventions the exporter must keep (each independently confirmed against the marine,
the m3studio round-trip, and the D3Exports files): parent-first BONE order; region-local face
indices and vertex lookup bytes; `(sd_type << 16) | index` anim_refs packing; `IREF` as
column-layout inverse rest; weights renormalised to a 255 sum.

Blizzard's material recipe, read off six extracted units (`m3mat.py`) — 31 of 31 materials set
`unfogged`, and cutouts come in two forms:

| Kind | blend_mode | alpha_test | flags | Example |
| --- | --- | --- | --- | --- |
| Solid | 0 | 0 | `unfogged` | every marine/zealot body material |
| Hard cutout | **0** | 20 | `unfogged two_sided transparent_shadows` | `hightemplar` cloth |
| Soft hair | 1 | 32 | `+ transparent_depth_effects hair_layer_sorting`, `priority` 100 | `RAY_Hair01`, `beard` |

The hard cutout is the one to copy for Warcraft geometry: `blend_mode = 0` does **not** ignore the
alpha channel, it only declines to blend it — `alpha_test_threshold` still discards — so the
geometry stays in the depth-writing opaque pass. Making cutouts alpha-blended instead drops them
out of that pass, and any body sharing the material then clips through itself.
