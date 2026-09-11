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
  masks flattened per material, filter-mode-aware blending, LOD picker, and a player-colour picker
  that recolours the model the way StarCraft II will.
- **Animation player** — double-click a sequence to play; scrub, pause, speed control. CPU skinning
  of both schemes (classic matrix groups and Reforged 4-weight SKIN), GEOA geoset visibility.
- **Effects** — particle emitters (`PRE2`/`PREM`), ribbons (`RIBB`) and lights (`LITE`) are parsed
  and the particles and ribbons simulated live in the viewer; particle emitters also export as SC2
  `PAR_` systems. Reforged's own PopcornFX effects (`CORN`, a third of effect models) are counted
  and reported rather than converted — their per-particle behaviour is compiled bytecode with no
  equivalent in a fixed-field emitter. The status bar names what a model carries so an empty
  viewport reads as a known limit rather than a bug.
- **Animated texture flipbooks** — HD water, fountains and coral animate their diffuse through a
  `KMTF` texture-id track of up to 50 frames. The viewer plays these on the track's own timeline;
  export resolves the flipbook's first frame instead of falling back to texture 0.
- **.m3 export (StarCraft II)** — mesh, skeleton, baked animations, attachments, cameras, GEOA
  visibility, PBR→specular texture conversion, written directly by the app: **no Blender, no
  add-ons, no external tools**. Verified in the StarCraft II editor: models load, animate and
  render textured.
- **Copy/paste-ready export layout** — the export folder holds an `Assets\` folder containing
  `<Name>.m3` and `textures\<Name>\*.dds`, mirroring the references baked into the file
  (`Assets/textures/<Name>/*.dds`) exactly. Merge that one folder into your map or mod root and
  everything lines up with no renaming. The per-model subfolder keeps several imported units from
  colliding on a texture filename. Every texture path is read back out of the written file and
  resolved against what landed on disk, because SC2 draws a layer it cannot find as black rather
  than reporting anything.
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
- **Team colour and team glow** — exported models take the player's colour **live from StarCraft
  II**, so one file is correct for all eight players and recolours in the editor. Warcraft III hides
  the mask in three different places and all three are read: classic units stack an opaque
  `replaceableId 1` layer under the diffuse (mask = `1 - diffuse.a`), Reforged HD units put it in
  the **alpha channel of the ORM map**, and team glow is the falloff of the game's own
  `TeamGlow<nn>.blp`. It lands in SC2 as `blend_mode_emis* = 4`, "Team Color Emissive Add" — the
  mechanism 1,204 of Blizzard's own Heroes materials use. Measured, not assumed; see
  `docs/mdx-format-verified.md` §5.
- **Billboards** — cards that Warcraft III turns to face the camera (the priest's staff orb, most
  spell glows) face it in the viewer and in StarCraft II. They export as `BBSC` entries: a full
  billboard becomes type 6, and a vertical-axis (Lock Z) billboard becomes type 2. No re-orientation
  is needed, because the quarter turn on export lands Warcraft III's card layout exactly on
  Blizzard's (measured over 2,604 WC3 cards and 346 SC2 ones). X- and Y-axis locks have no SC2
  equivalent and keep their rest pose.
- **glTF export** — skeleton, skinning, PNG textures and every sequence baked as a separate
  animation, for editing in Blender (re-export `.m3` there with m3studio if desired). Verified
  headlessly: Blender imports the armature, skinned mesh and all actions.
- **Custom models** — *Open file…* loads a loose `.mdx` from disk (Hive Workshop downloads etc.);
  textures resolve from the model's folder (`.blp` including JPEG-content, `.dds`), then by file
  name anywhere in the folder tree beside it — a download that references
  `Heroes\Human\Drenden\Drenden.blp` from the author's own disk still finds the `Drenden.blp` it
  shipped, wherever in the package it sits. Stock references come from your game install.
- **Texture panel and hand-mapping** — the *Textures* tab lists every reference the model makes,
  what answered it, and how confident that answer was (beside the model, found by name, game
  install, or a file you chose). Anything unresolved raises a banner rather than quietly drawing
  magenta, and `…` points that reference at a file of your choosing. Mapping happens in the viewer,
  so you see the result in the preview, and the export uses exactly what you previewed.
- **Borrowed game textures are packaged** — most custom models ship only the art their author drew
  and leave every Warcraft III texture they reuse as a bare path. Those are pulled out of your
  installed game, converted like any other, and written into the export folder, so the package is
  complete without hunting textures down by hand. Resolution does not depend on the author's
  spelling: a reference that has been flattened to a file name, re-rooted through
  `war3mapImported\`, left as an absolute path from the author's own disk, or written with the
  source art's extension still finds the right file. Where a name matches several archive files the
  export log says so and names the one it used, and anything genuinely missing is reported rather
  than quietly exported as a magenta placeholder.

## Requirements

- Windows 10/11, 64-bit.
- A **local Warcraft III installation** (Reforged, via Battle.net). The tool reads your own game
  files; it neither downloads nor ships any game content. Its CASC storage contains both the classic
  and Reforged art, so one install covers both.
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source.

## Getting started

1. Download `Wc3ModelViewer.exe` from [Releases](../../releases) and run it. It is a single
   self-contained file — nothing to extract, no installer, no .NET installation required.
2. It opens your Warcraft III install by itself if it can find one, and remembers the folder you
   last opened. Otherwise point it at the folder containing `.build.info` (e.g.
   `C:\games\Warcraft III`) and click **Open**. Everything else waits on this, custom models
   included — they borrow most of their textures from the installed game.
3. Filter the list, click a model to preview it, double-click a sequence to play it. **Open file…**
   loads a custom `.mdx` from disk instead.
4. **Export…** chooses formats, geosets, animations, scale and output folder.

To use an exported model in a map: copy the `Assets` folder from the export into your map's root,
merging it with the map's existing `Assets` folder. Keep the `.m3` and its `textures\` folder
together — the paths baked into the model are resolved from the map root, so a `textures\` folder
that lands anywhere other than inside `Assets\` leaves the model untextured with no error.

Exported models are turned a quarter turn on the way out: Warcraft III builds a model facing +X and
StarCraft II expects one facing -Y, so a model exported unturned walks and attacks square to the way
the actor points it. A `_portrait.mdx` carries its camera through as an m3 camera under its original
Warcraft III name (`Camera01` on most models) — name that camera in the portrait's data or SC2 frames
the shot itself.

> Windows SmartScreen may warn on first run because the executable is not code-signed. Use
> "More info → Run anyway", or build from source yourself (see below).

## Building

```
dotnet build src/Wc3ModelViewer.slnx -c Release
```

x64 only — the bundled native `CascLib.dll` is a 64-bit build. The single-file release build is:

```
dotnet publish src/Wc3ModelViewer/Wc3ModelViewer.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

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
