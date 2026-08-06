# Warcraft III Model Viewer

A Windows desktop viewer and exporter for Warcraft III models — classic (SD) and Reforged (HD).
Point it at your own Warcraft III installation, browse every model in the game, preview it, and
**export to `.m3` for StarCraft II** with its textures converted from Reforged's PBR workflow to
SC2's specular one.

The `.m3` export is the point of the tool: it replaces the manual CascView → Photoshop → 3ds Max
pipeline with a single application.

## Status

Working end to end:

- **CASC storage** — opens a local Reforged install and catalogues **8,408 models** and 38,352
  textures in ~35 ms.
- **MDX parser** — classic and Reforged, validated against **86 real models** across every race,
  buildings and heroes with zero defects.
- **Textured viewer** — DDS decode (BC1/BC3/BC5), multi-layer SD materials and HD team-colour
  masks flattened per material, filter-mode-aware blending, LOD picker.
- **Animation player** — double-click a sequence to play; scrub, pause, speed control. CPU skinning
  of both schemes (classic matrix groups and Reforged 4-weight SKIN), GEOA geoset visibility.
- **.m3 export (StarCraft II)** — mesh, skeleton, baked animations, attachments, cameras, GEOA
  visibility, PBR→specular texture conversion, written directly by the app: **no Blender, no
  add-ons, no external tools**. Verified in the StarCraft II editor: models load, animate and
  render textured.
- **Copy/paste-ready export layout** — the export folder holds `<Name>.m3` next to
  `textures\<Name>\*.dds`, which is exactly what you paste into a map's `Assets\` folder; the
  baked references read `Assets/textures/<Name>/*.dds` and line up with no renaming. The per-model
  subfolder keeps several imported units from colliding on a texture filename. Every texture path
  is read back out of the written file and resolved against what landed on disk, because SC2 draws
  a layer it cannot find as black rather than reporting anything.
- **Bone palette splitting** — geosets are split into regions of at most 45 bones. SC2 skins each
  draw call from a fixed matrix palette, and Reforged geosets routinely reference 80+ bones, which
  renders correctly at rest but explodes into spikes as soon as anything animates.
- **Cut-outs decided per geoset** — Reforged marks every HD layer transparent and packs solid body
  parts and cut-out cards into one atlas, so the material cannot say which is which. Each geoset's
  UV triangles are rasterised into its diffuse and only the texels it actually samples are
  measured; a body that reaches no transparent texel stays opaque. This matters because an
  alpha-blended material leaves SC2's depth-writing pass and then clips through itself. Cut-outs
  test at Blizzard's own threshold (32, as on raynor's hair) — the feathered band Reforged authors
  around every strand carries the object's colour, so discarding it thins hair and fur into holes.
- **Team colour** — verified against the shipping art rather than assumed: Reforged HD units carry
  **no** team-colour data (`teamColorMultiplier` is 0 on every layer of all 102 HD unit models, and
  their diffuse alpha is coverage, not a mask), so their team colour is already baked into the
  diffuse by Blizzard. Classic SD units do carry a real mask — an opaque `replaceableId 1` layer
  under the diffuse — which is composited at export using the chosen player slot. See
  `docs/mdx-format-verified.md` §5.
- **glTF export** — skeleton, skinning, PNG textures and every sequence baked as a separate
  animation, for editing in Blender (re-export `.m3` there with m3studio if desired). Verified
  headlessly: Blender imports the armature, skinned mesh and all actions.
- **Custom models** — *Open file…* loads a loose `.mdx` from disk (Hive Workshop downloads etc.);
  textures resolve from the model's folder (`.blp` including JPEG-content, `.dds`), stock
  references from the CASC.

## Requirements

- Windows 10/11, 64-bit.
- A **local Warcraft III installation** (Reforged, via Battle.net). The tool reads your own game
  files; it neither downloads nor ships any game content. Its CASC storage contains both the classic
  and Reforged art, so one install covers both.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source.

## Getting started

1. Download the latest zip from [Releases](../../releases), extract it anywhere, and run
   `Wc3ModelViewer.exe`. The release build is self-contained — no .NET installation required.
2. Point it at your Warcraft III install folder (the one containing `.build.info`, e.g.
   `C:\games\Warcraft III`) and click **Open**.
3. Filter the list, click a model to preview it, double-click a sequence to play it.
4. **Export…** chooses formats, geosets, animations, scale and output folder.

To use an exported model in a map: copy the contents of the export folder — the `.m3` and the
`textures\` folder beside it — into your map's `Assets\` folder.

> Windows SmartScreen may warn on first run because the executable is not code-signed. Use
> "More info → Run anyway", or build from source yourself (see below).

## Building

```
dotnet build src/Wc3ModelViewer.slnx -c Release
```

x64 only — the bundled native `CascLib.dll` is a 64-bit build.

## Layout

| Path | What it is |
| --- | --- |
| `src/Wc3ModelViewer.Core` | Storage, format parsers, conversion. No UI dependencies. |
| `src/Wc3ModelViewer` | WPF app: browser, Helix viewport, export dialog. |
| `spike/CascProbe` | Proves CASC access and dumps the storage's real path spellings. |
| `spike/MdxProbe` | Dumps raw MDX structure; `--validate N` parses N real models and checks them. |
| `spike/m3verify` | Validates exported .m3 against m3studio's structures.xml (headless Blender import). |
| `docs/mdx-format-verified.md` | **The format reference this code is written against.** |
| `native/CascLib.dll` | Prebuilt 64-bit CascLib. |

## A note on the format documentation

Reforged build 2.0.4 does not match the published MDX specifications — the wowdev wiki and
TaylorMouse's 3ds Max scripts both describe older builds. `docs/mdx-format-verified.md` records what
the shipping files actually contain, derived by parsing them; where it disagrees with an external
source, it was checked against real bytes and it wins. Re-run `spike/MdxProbe` after a game patch
rather than assuming the layout held.

## Third-party components

- [CascLib](https://github.com/ladislav-zezula/CascLib) by Ladislav Zezula (MIT) — reads Blizzard
  CASC storages.
- [Helix Toolkit](https://github.com/helix-toolkit/helix-toolkit) (MIT) — WPF 3D viewport.
- The Reforged material and animation conventions were cross-checked against
  [TaylorMouse's Warcraft III Reforged tools](https://github.com/TaylorMouse/warcraft_III_reforged_tools),
  [ReterasModelStudio](https://github.com/Retera/ReterasModelStudio) and
  [mdx-m3-viewer](https://github.com/flowtsohg/mdx-m3-viewer).

## Legal

A fan-made tool for viewing and converting assets **from your own legally purchased copy of
Warcraft III**. It ships no game assets and cannot download any. Warcraft and StarCraft are
trademarks of Blizzard Entertainment, Inc. Do not redistribute exported game assets; keep them for
personal/modding use as permitted by Blizzard's EULA.

## License

MIT — covers this tool's code only, not the third-party components or any game content.
