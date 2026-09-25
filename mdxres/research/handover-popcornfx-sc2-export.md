# Handover: PopcornFX effects → StarCraft II export

**Goal.** Definitive Edition spell effects (PopcornFX `.pkb` bakes on CORN emitters) should export to
`.m3` and look like they do in the WC3 editor. The reference case is DE `HolyBoltSpecialArt`.
The viewer already runs the scripts correctly. The export is what is still wrong.

**State.** Uncommitted on `main`, built into `src\Wc3ModelViewer\bin\Release\net10.0-windows`.
The user tests by importing into the SC2 Cutscene editor (`C:\Games\StarCraft II\Maps\Test\modeltest.SC2Map`)
and playing **Stand** (a copy of Birth). Test exports live in `sc2test\`.

## Session 6 (2026-09-25, overnight): why no trail appeared, tested in the SC2 editor

The user authorised driving the editor directly, so this round was verified rather than argued.

**The reference clip was the wrong one.** `abilities\weapons\fireballmissile\` holds two models:
`fireballmissile.mdx` (FireBallMissile.pkfx, 8 renderers, no ribbon) and `fireballmissile2.mdx`
(FireBallMissile2.pkfx, 13 renderers, 2 ribbons). Every export of this work uses **fireballmissile2**,
whose editor recording is `VfxFireball2-wc3editor.mp4` — not `VfxFireballA`, which is the *other*
model and had been the standing reference for two sessions.

**`RIB_` works; a trail ribbon just needs its bone to travel.** `MdxProbe --ribtest` (`RibTest.cs`)
builds a model with four ribbons and two control cards. In the editor: the white anchor card and the
magenta bone-parented card both drew (so the model loads and `PAR_` is fine), ribbons at `speed` 1
and 4 drew plainly, and the `speed` 0 ribbon drew nothing. `MdxParticleEmitter2.RibbonSpeed` /
`RibbonGravity` now reach `RIB_` +12 and +184; both stay 0 for a trail, deliberately, so a hovering
missile leaves nothing as in Warcraft III.

**The editor's terrain viewport cannot test a trail at all.** It does not play a doodad's animation:
a card parented to a bone carrying a full translation track sat at the same pixel across 3.5 s of
capture. So a bone-driven ribbon can never appear there, parked *or* dragged. `VfxFireballM` showing
no trail in that venue says nothing about the export. Judge a trail on a moving unit in game.

**`MAT_.additional_flags` was wrong on every material we have ever written.** Blizzard set
vertex_color|vertex_alpha on all 2,171 particle and ribbon materials across 700 of their missiles;
we wrote 0. Fixed (`0xC` when `IsParticle`); `VfxFireballN` is M plus exactly that change.

**The trail strip was 95% empty, and that is why it never drew.** Swapping the strip's bytes for an
opaque test sheet and changing nothing else made the same ribbon draw plainly (`VfxFireballP`), which
cleared geometry, material, flags, bone and the non-square DXT5. The footprint came from sprite
corners, so the wide faint smoke stretched the sheet to 512 x 128 units at a **mean alpha of 5%**.
`BakeTrail` now crops to the drawn alpha's bounding box — head stays at x=0, only empty tail and
margins go — and resamples back up: **444 x 45 units, mean alpha 22%**. The bake log prints it.
`VfxFireballR` is the real export; `VfxFireballQ` is the same with `--trailribbonspeed 1.5` so the
strip extrudes on a standing model, which is the only way to see it without a moving unit.

**New tooling.** `spike\m3verify\m3audit.py <ours.m3> [corpus-root] [n]` builds the corpus
distribution of every scalar field of `PAR_`, `RIB_` and the `MAT_`/`LAYR` they use, then prints the
fields where our value is rare or unseen, rarest first. It is how the `additional_flags` bug was
found, and it still flags, unfixed and untested: `MAT_.flags` (we set `0x10000`
transparent_depth_effects, which no Blizzard particle/ribbon material sets), `specularity` 80 vs 20,
zeroed `uv_tiling`/`uv_w_scale` on our unused emissive layers, and several `PAR_` defaults
(`uv_ss_tiling`, `spline_bounds_max`, `instance_direction`, `rotation_flags`, `mass`) we leave at 0
where Blizzard always writes a value. Our `PAR_` draws correctly regardless, so none is urgent.

## Session 5 (2026-09-25, early): the trail becomes one baked ribbon

**Why.** `VfxFireballL` (head impostor + per-layer trail stand-ins) still did not match the editor
clip: WC3's fireball is a comet — a saturated white-yellow body about 55 units wide and 250–350
units long — and SC2 drew a soft ball followed by a dotted line of sparks and black smoke puffs.
Per-layer stats (`--bake ... --layerstats`) showed the three layers that travel with the head were
in the sheet, and everything that makes the comet is spawned per distance (n11 flashes, n29 wisps,
n17 smoke, three n33 flares) or is a ribbon (two n12 `FlareShot_Trail2` wedges), left as stand-ins.
Three stand-in defects: the ribbons came out ~4× too dim after the alpha fold (a faint red haze in
boosted frames); the wisps shrank on the *colour* middle time (0.2) instead of their own (0.5), so
they were dots before they were bright; and separate sprites at a per-second rate never fuse the
way WC3's tone-mapped sum does.

**What.** `PkImpostorBaker.BakeTrail` flies the effect at `TrailSpeedMetres` (18 m/s = 900 units/s,
the speed `PkRendererStats` measures trail rates at) until its trail is whole (max trail life +
0.5 s), collects the per-distance sprites (`CollectSprites`) and every ribbon's points
(`CollectStrips`, read straight from the layer slots: position, size, colour and the scripted
`TextureU` input, ordered newest-first by life ratio then serial — two points born in one frame
share a ratio and zig-zagged), renders them in the effect's frame from beside the flight line
(camera yaw −90°, same pitch), smoke first and light on top, into a power-of-two strip with the head
at x=0 (0.5 units/px, ≤2048×512), tone-mapped and matted like the sheet. `SynthesizeTrail` makes one
`Ribbon` emitter: `BakedSprite` = strip, width = the strip's, life = length/speed, colours white,
alpha 255, blend add or blend; `M3Exporter` writes it through the existing `RIB_` path (uv turn
−π/2 puts the strip's x along the ribbon, head at the base). The head sheet is unchanged.
- **Ribbon alpha.** By the numbers the ribbons are faint ((4.7,1.8,0) and (11,6,2.7) × wedge, alpha
  ≤ 0.26); the editor's saturated comet only comes out with the colour alpha left off — additive
  ribbons are baked as colour × texture (`RibbonColourAlpha` false, `--ribbonalpha` to A/B). This is
  inferred from the clip, not from the engine.
- Stand-ins get `SizeMiddleTime` (`MdxParticleEmitter2`), written to `size_anim_mid` separately.
- Probe: `--bake` also writes `<stem>_trail.png` and logs each ribbon's point range; `--speed N`
  (units/s), `--trailframes N` (moments averaged), `--ribbonalpha`; `--export1 --bakepng` dumps
  the strip too. `ImpostorTrailSpeed`/`ImpostorTrailFrames` on `M3ExportOptions`.

**State.** `VfxFireballM` in the test map (not loaded): one impostor `PAR_` + one baked `RIB_`
(512×128 units, 0.57 s, 1024×256 DXT5), 1.7 MB. The strip PNG shows the comet: saturated core
tapering over ~250 units, orange edges, licks, smoke behind. Other test effects have no trail layers
and export unchanged. Risks: the ribbon-alpha inference; the strip is one moment (flicker lost);
RIB_ V-across orientation unverified (only matters for the smoke's slight rise).

## Session 4 (2026-09-24, night): impostor bake replaces per-layer stand-ins

**Why.** Three A/B rounds against the WC3 World Editor recordings (`Videos\wc3-sc2-vfxtest\Round 1\
*-wc3editor.mp4`, the user's standing reference) showed the stand-in approach cannot converge: SC2 has
no tone mapping, so WC3's HDR layers (×5–×22) clip to flat discs and a low-alpha strip cannot be
lifted by `hdr_emis` (Blizzard's own materials stop at ≈10), and one constant ramp per layer loses the
scripts' curls, licks and per-distance spawning.

**What.** `M3Exporter.BuildEffects` → `BakeImpostors` → `PkImpostorBaker.Bake` (Formats/Popcorn):
runs the effect headless (`PkEffectInstance`), collects sprites with the viewer's own
`PkCornPlayer.CollectSprites`, builds their quads with the shared `PkSpriteGeometry`, rasterises each
frame over black and over white at an orthographic camera pitched 56° on the model's +X side (the face
a WC3 unit at its default 270° facing shows the editor, and the face `ToSc2` turns toward SC2's
camera), tone-maps per channel (ACES, exposure 1 — per channel reproduces WC3's clipped hues),
reads coverage in *linear* light (display-space coverage put a 9% haze on every empty texel) and
brightness in display space, and packs an 8×8 sheet with feathered cell borders.
`PopcornApproximation.SynthesizeImpostor` makes the one card: `ModelSpace`, `SpawnOffset` = footprint
centre, scale = half side, `BakedSprite` = atlas, `hdr_emis` 1 (no `ParticleGlow`). Cases:
- one-shot: `emit_count` burst of 1 at gate-on, life = duration (cap 6 s, tail faded; an effect still
  drawing at 18 s is steady instead);
- steady state: one period P (2 s, or an orbit's symmetric period), life P, rate 2/P, alpha 0→255→0
  — two cards always alive P/2 apart, alphas summing to 1, the card at frame 63 always at alpha 0;
- static (frames identical): one cell, one card at rate 1/P, constant alpha;
- purely additive layers → an alpha-add card (`blend_mode` 3; exact cross-fade); anything laid over
  → alpha-blended card (`blend_mode` 1) with the matte;
- excluded, kept as stand-ins: ribbons (`RIB_`), `TrailRate > 0` layers, `TeamColoured` layers.
Cell size doubles from 128 up to 256 while the footprint exceeds 2.5 units/px (Holy Light's 600
units → 2048²); `ImpostorAtlasSize` 0 = auto, dialog "auto/1024/2048". Identical sheets dedupe by
content in `AddTexture`.

**Flipbook bytes, measured over 1,132 HotS flipbook systems.** Phase 1 `start_init→start_stop` over
`start_lifespan_factor` of the life, phase 2 `start_stop→end_init` over the rest, `end_stop` always
0, ranges may run backward; commonest `(0,63,63,0,1.0)`. `M3ParticleWriter` now writes +720..+736
whenever cols×rows > 1 (the +inf fractions showed cell 0 forever) and sets `random_uv_flipbook_start`
for a one-cell range on a multi-cell sheet.

**Probes.** `--fbcalib <outDir>` (numbered 8×8 sheet: dots count up, grey ramp — playback order and
sRGB in one screenshot); `--bake <casc> <outDir> [--frames] [--stats] [--exposure X] [--tonemap
clip|reinhard|aces] [--pitch N] [--loop N] [--atlas N] [--alphaadd]` (PNG sheets and per-frame
luminance, no SC2 needed); `--export1 ... [--nobake] [--bakepng <dir>]`.

**State.** In the test map, none loaded yet: `FlipbookCalibA`, `VfxFireballL` (head impostor +
trail stand-ins), `VfxHolyLightE`, `VfxThunderClapE`, `VfxUnholyAuraE`, `VfxReviveE`
(`resurrect.mdx`), `VfxResurrectCasterE` (`resurrectcaster.mdx`, the angel with 15 glow effects).
Bakes of the fireball head, Holy Light and Unholy Aura looked like the editor reference on the PNGs.
Open: exposure calibration against the clips; `--nobake` regression; the viewer still draws the VM
live (no bake preview).

## Session 2 (2026-09-13, afternoon): the burst problem was the Stand copy

**Root cause of B and E.** The exporter synthesises a `Stand` sequence for models without one by
copying the first sequence's `SeqDef`, and the copy carried only the bone and colour tracks. The
`emit_rate` (SDR3) and geoset-toggle (SDFG) tracks stayed behind in `Birth_full`, so in `Stand` every
burst emitter ran at its rest rate, which for a burst is 0. Confirmed by parsing E:
`Stand_full` had one anim id (the end event) against `Birth_full`'s 14. The rate track was never at
fault. The copy now carries every track kind (`M3Exporter`, the `hasStand` block), and
`PopcornApproximation.EmitBursts` is back to true.

The same bug silenced any KP2V-gated SD effect exported without a Stand of its own, so re-test one
of those too if convenient.

**Blizzard's burst mechanism is `emit_count`** (`PAR_` +704, int16 anim ref, header interp 0,
flags 6, keys in STC slot 7 `SDS6` → `I16_`). Over the 7,196 HotS effect models: 14,464 particle
systems burst through `emit_count` against 4,014 through an `emit_rate` window. It is edge-triggered,
not per frame: the median key holds the count for one 33 ms frame, and systems holding 15 for two
seconds cap `emit_max` at 31 (`emit_max` / peak: median 4.4, never below 1 except 32 cases).
Typical Blizzard track: frames `[0, 33, 166, 2999]`, keys `[0, 25, 0, 0]`, `emit_rate` 0 alongside
in 95% of them.

Now written by the exporter when `PopcornApproximation.CountBursts` is on (probe `--count`;
**default off until the editor confirms it**): bursts whose births span ≤ 0.1 s
(`CountBurstMaxSeconds`) become one `emit_count` key of `Births`, held 33 ms, with rate 0; wider
bursts keep the rate window. `emit_max` is raised to 3 × peak + 1. `MdxParticleEmitter2.EmitCountTrack`
carries it; the viewer ignores it (the scripts run there).

**Scripts** (in `spike/m3verify`): `spike/m3verify/ratescan.py <dir|files> [--verbose]` dumps every
`emit_rate`/`emit_count` anim header, STC binding and key list, with corpus statistics; `countmax.py
<dir>` is the `emit_max`-versus-peak measurement.

## Done in session 1

- **Viewer:**
  - Play replays spell effects after a one-shot sequence (effects reset). `MainWindow.OnPlayToggled`.
  - Turbulence sampler reads field 11 (wavelength) and 12 (strength), both defaulting to 1. This
    fixed Unholy Aura's scattered runes. See `popcornfx-vm.md`.
- **Export:** stand-ins come from a measured headless run. `Popcorn/PkRendererStats.Measure` records,
  per renderer: birth centre and box, velocity cone, life, size and colour ramps, beam axis length and
  coherence. `PopcornApproximation.SynthesizeMeasured` turns that into PRE2-style emitters.
  - An emitter with a spawn offset or a tilted direction gets a static child bone
    (`M3Exporter.EmitterBone`).
  - Parallel beams export as a fixed tail. Axes that disagree export as velocity streaks. An immortal
    particle is placed where it settles.
- **`M3ParticleWriter` corrections, measured rather than guessed:**
  - `size` = 2 × WC3 scale. Blizzard's own conversion `war3_holyboltspecialart.m3` is at 0.01
    world scale, but its sizes are ×0.02.
  - Emission spread is in **radians** (latitude 5° → 0.0873).
  - `instance_tail` counts **particle sizes**, not units. HotS `storm_*` fixed-tail systems:
    median 16; medic stim is 64 on a size of 0.05.
  - `emit_rate` anim header flags = **6**. Blizzard flags 13,671 of 34,827 this way; `LAYR color_value`
    also uses 6.

## User-verified in the SC2 editor

| Export | Result |
|---|---|
| Old stand-ins | Everything at the origin, "flattened" |
| A: placed, constant rate, tail in units | Visible, super tall, no rune |
| B: burst `emit_rate` tracks, flags 0 | **Nothing** |
| C: unplaced, constant rate | Visible, tall, rune at centre |
| D: A + tail in sizes | Column height OK, but the whole effect "huge": a persistent glow cloud |
| E: bursts + flags 6 | **Still nothing** |
| F: D + flags 6 | Same as D: tall soft column, large glow cloud high up, rune not clearly visible |
| G, H | scale 1 by mistake, then overwritten at 0.025 — "massive"; superseded, do not use |
| I: bursts, Stand carries all tracks, scale 0.025 (**current default**) | *untested* — expected: flashes once, no persistent cloud, rune at 166 ms |
| J: I + `emit_count` for the ≤ 0.1 s bursts (`--count`) | *untested* — expected: as I, rune and flares exactly one particle each |
| SizeCalib | Cards ballooned frame by frame — they hung from the SD model's node 0, the beam cylinder bone that scales up through Birth; frame 0 looked right (size 2 card ≈ a unit). Void, see SizeCalib2 |
| SizeCalib2 | *untested* — same three systems on root bones; beam born 4 units up: spans 0..4 if the tail trails behind, 4..8 if it runs forward |
| K: J + beam emitters at the far end | **Wrong**: shafts stood on top of their spawn points, far above the rune — the fixed tail runs *forward*. J's "beam below the glow" was the tail's bright centre near the ground with its faint upper half above |
| L: J's anchoring restored (near end) | **Wrong**: shafts hung entirely *below* the impact glow. With K (entirely above), this proves SC2 centres the fixed tail on the particle |
| M: beam emitters at the beam centre, count bursts, scale 0.025 (**current default**) | **Correct** ("yay, you got it"): rune on top, shafts centred on the glow at the origin, as the viewer draws it. Proves: `emit_count` bursts work, the Stand-copy fix works, no glow cloud, fixed tail centred, rune placement on an offset bone works |

**Every changed export gets a new letter; never overwrite a name** — the editor caches by name and the
user would have to restart it.

Both G and H pass `m3accept.py`, `m3tags.py` and `m3invariant.py`. If H works, make `CountBursts`
the default. If G works and H shows no rune/flares, `emit_count` fires on something other than a
rising step key (try holding the key longer, or a key every 33 ms).

**Size calibration (session 2, after G/H at 0.025 still looked "massive").** The numbers in G are what
the viewer draws: halos 11 SC2 units across (440 WC3 units, 8.8 m), beams 25 units tall (1,000 WC3
units). That is the largest billboard size in all of HotS (median 0.8, p99 7.0), but the metre scale
is anchored: DE Divine Shield's persistent ground ring measures 55 units in radius, a hero footprint,
so 50 units/m stands. What is unproven is SC2's reading of `size` (full width, per Blizzard's ×0.02
conversion of the SD halo) and `instance_tail` (sizes). `sc2test\SizeCalib` (`MdxProbe --calib
sc2test --scale 0.025`) settles both next to a 0.025-scale unit (~2.3 units tall): a card of size
2.0 at the origin, size 1.0 on one side, and a fixed-tail beam of size 0.5 × tail 8 on the other.
Card ≈ unit height → full width; twice that → radius (halve the writer's ×2). Beam 4 units → tail
counts sizes; 8 → units. Also check the import is not stale: G and H were first exported at scale 1
under the same file names. `sc2test\Blizzard\war3_holyboltspecialart.m3` is Blizzard's own SD
conversion at 0.01 (textures in the SC2 CASC) for a side-by-side at 2.5× actor scale.

## Settled by M (user-verified 2026-09-13)

1. Animated emission works; the blank bursts were the Stand copy. `emit_count` bursts are now the
   default (`CountBursts = true`), rate windows remain for births spread over > 0.1 s.
2. No glow cloud once flashes fire once.
3. Offset emitter bones displace the system: the rune floats at 4.8 units on `Emit_..._n12`.
4. **SC2 centres the fixed tail on the particle.** Near-end emitters (J, L) hung the shafts wholly
   below the glow; far-end emitters (K) stood them wholly above the rune. The emitter goes at the
   PopcornFX spawn centre, as the viewer draws it.
5. `PAR_.size` is a full width (SizeCalib frame 0: the size-2 card matched a unit's height), so the
   writer's ×2 on the WC3 half-width stands. `instance_tail` in sizes gave the right shaft length.

## Round 2 (2026-09-13, evening): Thunder Clap perfect; hero glow blank; Unholy Aura runes still

| Export | Result |
|---|---|
| Thunder Clap (app export) | **Perfect** |
| Unholy Aura (app export) | Ring and runes right, runes do not orbit |
| Hero glow (app export) | **Nothing** — every layer's alpha came out 0: the CORN's `ColorMultiplier` is (1,1,1,0), which the script ignores but `SplitColour` applied afterwards. The multiplier now goes into the measurement as `__a_Game.ColorMultiplier` (as the viewer does) and is not applied again |
| HeroGlowA | *untested* — alpha 44/185/172 now; white, not team-coloured (SC2 team colour on particles is a separate job) |
| UnholyAuraB | *untested* — runes orbit: `PkRendererStats.Orbit` measures angular velocity about the effect's Z from the second half of each particle's samples (0.87 rad/s); export gives each orbiting stand-in a `Spin_` bone with baked Z-rotation keys and hosts the particles to it (`ModelSpace`, no world_space flag). The rate is snapped so a looping sequence ends on a symmetric position (4 runes → quarter turns; Stand 01 is 6.67 s → one full turn) |

| UnholyAuraB | **Orbit correct** (user-verified) — so SC2 hosts non-world-space particles to an animated bone. Runes were camera-facing; in WC3 they are upright cards facing outward |
| UnholyAuraC | *untested* — plane-aligned cards that do not lie flat now export as type 7 (emitter-facing) on a bone whose Z is the measured normal; for orbiting cards the normal is measured in a (radial, tangential, up) frame and rebuilt at the spawn point so it turns with the ring. The bone frame keeps world up as the card's Y (`M3Exporter.EmitterBone.Frame`); if the runes come out sideways, SC2 takes the bone's X as up instead |

## Open problems

1. **Confirm HeroGlowA and UnholyAuraC.** If the runes still sit still, check that SC2 hosts
   non-world-space particles to an animated bone (the alternative is baking bone location keys on the
   emitter bones themselves along the circle, which needs no hosting).
2. **Team colour on PopcornFX stand-ins** (hero glow is white). See [[sc2-team-colour-is-a-blend-mode]].
3. **Other effects.** Verified: Holy Light, Thunder Clap. Partly: Unholy Aura, hero glow.
2. **SizeCalib2** is still worth one look for the exact card/beam sizes; M already answers the
   direction question.
3. **Viewer, low priority:** SD PRE2 particles are drawn as `half = scale*0.5`. Blizzard's
   conversion implies scale is already a half-width, so they are probably half size. There is also
   no ground plane, so the underground halves of beams show.

## Tools

- `MdxProbe --export1 <cascName> <outDir> [--name X] [--burst|--noburst] [--count] [--noplace] [--scale 0.025]`:
  single export plus a per-emitter dump and baked rate/count keys. **Test exports must be at scale
  0.025** (the user's map scale; scale 1 is "massive"). G and H in `sc2test\` are. Holy Light's DE name is
  `war3.w3mod:_de.w3mod:abilities\spells\human\holybolt\holyboltspecialart.mdx`.
- `--pkrun`, `--pkdis`, `--pkrec <pkb> <class>`, `--pktex`, `--pkbakes <filter>`, and
  `--pkmeasure <bakeDir>` (all 2,165 bakes; median 3 ms).
- Structural checks: `spike/m3verify` (`m3accept.py` headless m3studio import, `m3invariant.py`,
  `m3par.py`). All test exports pass, but these checks cannot see the problems above.
- Bakes are dumped at `%TEMP%\claude\c--Projects-Wc3-Model-Viewer\a797af7a-…\scratchpad\bakes`
  (regenerate with `--dumpbakes`).
