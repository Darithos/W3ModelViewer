# Handover: PopcornFX effects → StarCraft II export

**Goal.** Definitive Edition spell effects (PopcornFX `.pkb` bakes on CORN emitters) should export to
`.m3` and look like they do in the WC3 editor. The reference case is DE `HolyBoltSpecialArt`.
The viewer already runs the scripts correctly. The export is what is still wrong.

**State.** Uncommitted on `main`, built into `src\Wc3ModelViewer\bin\Release\net10.0-windows`.
The user tests by importing into the SC2 Cutscene editor (`C:\Games\StarCraft II\Maps\Test\modeltest.SC2Map`)
and playing **Stand** (a copy of Birth). Test exports live in `sc2test\`.

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
