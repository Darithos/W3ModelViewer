# Handoff: the Warcraft III → StarCraft II fireball

Written 2026-09-25 morning, revised the same afternoon. For a session starting with no context.
Read this first, then `handover-popcornfx-sc2-export.md` for the older history.

## The one-line state

The PopcornFX → SC2 effect export bakes each effect into a flipbook sheet plus a trail ribbon. The
morning session ended with "it's still not right" and a theory that our head/trail *structure* was
wrong. **That theory was built on a contaminated measurement and is withdrawn.** Measured honestly,
the parked head card already matches the World Editor closely, and two real defects were found and
fixed: the tone map and a material flag.

Nothing is committed. Everything is uncommitted on `main`.

## Read this before you form a theory

Five things were believed and turned out to be false. Do not re-derive them.

1. **The reference clip was the wrong model** (found 2026-09-25 morning).
   `abilities\weapons\fireballmissile\` holds two different effects. Every export in this work uses
   **`fireballmissile2.mdx`** (FireBallMissile2.pkfx, 13 renderers, 2 ribbons), whose recording is
   `Videos\wc3-sc2-vfxtest\Round 1\VfxFireball2-wc3editor.mp4`. `VfxFireballA-wc3editor.mp4` is
   `fireballmissile.mdx` — 8 renderers, no ribbon.

2. **"In Warcraft III nearly the whole comet is saturated" is false** (found 2026-09-25 afternoon).
   It came from subtracting a background plate while **the editor's white grid lines were still in
   the frame**; the grid raised every outer annulus and every "saturated pixel" count. The morning's
   whole measurement table is void. Measured with the grid masked, the moving comet is **bright
   yellow, not white**: the peak pixel of nearly every slice along it is `B 130-160, G 255, R 255`,
   and the share of fully-clipped-white pixels is **0.0% in almost every slice** (a few percent in
   the leading 10-20%, which is the head ball, not the trail).

3. **The parked fireball is not a comet at all.** The reference clip is the user *dragging* a
   doodad. Its parked instances — the blobs that sit still in every frame — are a **compact ball**
   with no trail, ~52 px across at that zoom. Since the effect's other 10 renderers are all
   "spawned per distance" or ribbons, a parked missile draws **only** the three continuous layers
   (n34 flares, n18 EclipseRing, n30 whisps), which is exactly what the head card bakes. So a
   parked SC2 test *is* a fair test of the head card — it just cannot say anything about the trail.

4. **A trail ribbon cannot appear on a parked model, and the editor cannot animate one.** A `RIB_`
   lays geometry only where its points have moved, and our trail has `speed` 0 by design. The
   editor's terrain viewport also does not play a doodad's animation. Judge a trail on a moving unit.

5. **The ribbon was never broken.** Swapping only the strip's *texture bytes* for an opaque test
   sheet made the same ribbon draw plainly (`VfxFireballP`).

## How to measure against the clip without getting it wrong

This is the part that cost two sessions. The scripts are in the session scratchpad; recreate them
from this recipe — it is short, and the traps are all in it.

- **Never colour-key the grid lines.** They are white *and* yellow, and so is the fire: the
  fireball's core is achromatic (255,255,255) and its body is the same yellow as a yellow grid
  line. Every colour rule tried ate the flame. A morphological "keep long thin bright things"
  opening also fails, because the fireball *sits on* a grid line and the two together form a run
  longer than the kernel.
- **Take the plate from a frame where the effect is absent at that spot.** The parked fireball at
  (885,455) is absent for frames 0-2 of `VfxFireball2-wc3editor.mp4`, so frame 0 is an exact local
  plate: terrain and grid with no fire over them. The grid mask is then just "bright pixels of the
  plate", dilated — nothing bright but a grid line exists there, so it cannot touch the flame.
  **Look at the mask over a frame before trusting any number it produced.** Two wrong masks were
  caught exactly that way, and both had already produced confident, wrong conclusions.
- For a *moving* effect a temporal median is a valid plate (the missile passes any pixel only
  briefly), but the parked instance pollutes the median at its own spot.
- Compare shapes **scale-free**: normalise the radial profile by `r10`, the radius where the added
  light falls to a tenth of its centre value. For an effect that is not a blob (the aura's ring),
  compare the **distribution of added brightness** over the drawn pixels instead — a tone map is a
  per-pixel transfer curve, so that is what it actually changes.

## What was fixed, and on what evidence

**The tone map: ACES → Clip** (`PkImpostorOptions.ToneMap`, `M3ExportOptions.ImpostorToneMap`, and
the probe's default). The World Editor's renderer has no filmic curve — it blends additively and
saturates at 1 — and the recordings say so:

| | profile rms vs the parked reference | near-white radius / r10 (ref 0.43) |
|---|---|---|
| Clip, exposure 1 | **0.010** | 0.34 |
| Reinhard, exposure 1 | 0.015 | 0.33 |
| Clip, exposure 0.5 | 0.018 | 0.33 |
| ACES, exposure 1 (was the default) | 0.045 | 0.44 |

At Clip/1 the ring colours match to a few levels (core ours B180 G160 R116 vs ref B176 G160 R117;
mid B57 G150 R116 vs B65 G141 R115). Cross-checked on a second, very different effect —
`VfxUnholyAuraA`, a ring of orbiting runes, compared by brightness distribution — where **ACES is
the worst operator at every exposure** (0.29-0.41 total variation) and Clip the best (0.20-0.27).
Exposure stays at 1: the fireball prefers it clearly and the aura's preference for 0.5 is weak.

**`MAT_.flags`: stop setting `transparent_depth_effects` (0x10000) on particle materials**
(`M3Exporter.cs`). Across 900 Blizzard models it is set on **0 of 2,283 particle and 0 of 569
ribbon materials** — not rare, never — while those same materials set transparent_shadows 91% of
the time and unshaded 72%, so they are otherwise the same shape as ours. It is a depth-effect bit
for transparent *geometry*; particles inherited it only because they are translucent. Our `MAT_/RIB_`
went from NEVER to an ordinary 1.9% of the corpus. Found by `m3audit.py`.

## What is now known to be fine (stop looking here)

- **The parked head card's shape, brightness and mottling.** Profile rms 0.010, and the
  coefficient of variation per annulus is 0.10/0.14/0.31/0.62 against the reference's
  0.08/0.16/0.38/0.73.
- **Smoke.** The morning's "the reference darkens the ground by 26 levels and we darken by 0" was
  the bad mask. Honestly measured, the parked reference darkens **nothing**: a mean of 0 darkened
  pixels per frame. Our purely-additive card is right.
- **The three baked layers' balance.** `--onlylayer` (new, below) renders each on its own: n30
  whisps carry the flame structure, n34 the core and halo, n18 a faint ring. Under ACES n34's smooth
  halo visibly drowned n30's structure; under Clip it does not.

## The trail has never had anything to extrude along (2026-09-25 pm, measured)

`VfxFireballT` went into the map and was recorded as `Round 3\FireballT-Trail-Sc2Preview.mp4`. The
user read it as "the trail is really faint and doesn't match WC3 in movement". Measured, it is not
faint — it is **absent**: against a head of ~140 px at peak 157, the light at 150/250/350/450 px
behind it peaks at **1-4 of 255 (1-3% of the head)** and has **zero width** at a tenth of the head's
peak, on every clean frame.

The cause is not the ribbon. **`VfxFireballT` contains no location track at all** — dumping its
`VEC3` sections finds none, while `VfxFireballU` (below) has a 47-key track spanning 14.2 units. The
model never moves. The apparent motion in that recording is the Archive Browser previewer's camera
sweeping past a model sitting at the origin, and a world-space ribbon at `speed` 0 lays nothing when
its bone does not move. That is the third recording in a row read as a trail defect when the trail
had nowhere to go — the exact trap [[wc3-editor-clip-is-the-reference]] warns about.

The design itself checks out against the corpus: our trail emitter is world-space with `speed` 0,
which is what Blizzard use on **124 of 235** of their own missile ribbons. Proportions check out too
— our strip is 444 x 56 units against a 72-unit head (6.2 : 1), where the WC3 comet measures about
5.7 : 1.

**So there is now a way to see it: `--fly [units/s]` on `--export1`** (`spike\MdxProbe\FlyTest.cs`).
It gives the root node a circular translation track — 900 units/s, one lap per 2 s, matching the
speed the strip was baked at — so the ribbon extrudes inside the previewer with no map, no missile
actor and no camera trickery. `VfxFireballU` is that build. It is a **diagnostic**: a real missile is
flown by its actor and a shipped export must never carry it.

## The trail, measured for the first time (`FireballU-Trail-Sc2Preview.mp4`)

`--fly` worked: the ribbon extrudes along the flight path, so the trail has now actually been seen.
Walking its centreline and measuring the cross-section against the head:

| distance behind the head | WC3 peak / width | ours peak / width |
|---|---|---|
| 94 units | 69% / **0.57** | 78% / **0.38** |
| 125 units | 54% / **0.53** | 80% / **0.31** |
| 157 units | 55% / **0.40** | 70% / **0.26** |
| 188 units | 53% / **0.30** | 53% / **0.18** |

(width as a fraction of the head card's 72 units; distances beyond the head ball, so the head's own
glow is not being counted.)

**Brightness along the trail already matches.** **Width is about 1.5x too narrow** — and the factor
is near-constant along the whole length, so it is not a taper problem.

Ruled out, each by measurement, not argument:
- **Not the blend mode.** Baking the strip additive (`--alphaadd`) instead of blended gives
  *identical* widths (0.60/0.38/0.31/0.26/0.18 either way).
- **Not SC2 drawing it wrong.** Box-filtering the strip down to the size it actually draws
  (302 x 38 px from 1024 x 256) reproduces the recorded widths closely (0.61/0.39/0.27/0.08 against
  a measured 0.37/0.20/0.20/0.08). SC2 renders our strip faithfully; the strip is what is narrow.
- **Not the bake camera.** `BakeTrail` already uses `Basis(PitchDegrees, YawDegrees - 90)` — the
  56-degree three-quarter view, not a flat side-on one, so there is no foreshortening to recover.
- **Not the ribbon geometry.** Measured at a texture column whose content spans all 256 rows, the
  rendered band is 33-47 units against the strip's nominal 56 — consistent with 56 and faded edges,
  nowhere near half.

So the gap is in the baked strip: the simulated trail sprites cover a narrower band than Warcraft
III's comet does. **Why is not yet explained**, so rather than bury a correction in the bake there is
now an explicit knob — `--trailwidth N` (`M3ExportOptions.TrailWidthScale`), default 1 — which
stretches the strip across a wider ribbon without touching the art.

## The trail does not face the camera (2026-09-25, user, on `VfxFireballU/V/W`)

User: *"It's only leaving what looks like a flat 2D trail that doesn't face the camera"*, and
**`VfxFireballW` (`--trailwidth 2.0`) showed nothing at all**. A pure width change cannot delete a
ribbon, so the likeliest reading is that W was recorded at a moment when the strip was edge-on.

**That probably supersedes the width finding above.** A ribbon stuck in a fixed plane is
foreshortened by cos(angle) — at the editor's 56-degree pitch that is 0.56, i.e. 1.8x, which is
close enough to the "1.5x too narrow" number that the two are most likely the same defect seen
twice. Treat `--trailwidth` as a knob, not as a fix, and do not tune it until the orientation is
settled.

What is known about the orientation:
- We write `RIB_.ribbon_type = 0`, which m3studio's `structures.xml` calls **PlanarBillboarded**
  (1 Planar, 2 Cylinder, 3 Star Shaped), and it reads back as 0. 509 of 610 corpus ribbons use 0.
- `flags` 0xC080, `sides` 5, `cull_method` 0 are all Blizzard's commonest values for missile
  ribbons. The undocumented `0x10000` bit is set on only 24% of theirs, so it is not required.
- So on every field we know how to check, ours already matches the ribbons Blizzard ship.

**`RibTypeA` (`--ribtest`) settles it in one screenshot.** Four identical ribbons on four identical
circling bones, differing only in `ribbon_type` (0/1/2/3, verified written). Whichever keeps a
constant face as its bone rounds the circle is the one that actually billboards. `RibbonType` is now
plumbed through `MdxParticleEmitter2` to the writer, so whatever wins can be adopted directly.

Worth keeping in mind: the strip is a **picture taken from one angle** (the 56-degree editor view).
Even with perfect billboarding it will read as a flat painted band from a very different camera, so
"doesn't face the camera" may partly be the impostor approach showing its limits rather than a bug.

## `RibTypeA`: ribbon_type is not the lever (2026-09-25, five screenshots)

Four ribbons on four identical circling bones, differing only in `ribbon_type` 0/1/2/3, shot from
five camera angles. The result is clean and negative:

- **All four behave the same way with the camera.** At steep angles every one of them draws a solid
  curved band; as the camera drops toward the horizon they *all* collapse to thin slivers together.
- So **none of the four billboards**, including type 0, the one `structures.xml` calls
  PlanarBillboarded and the one we ship. Their width lies in the plane of the motion — a flat
  horizontal band, a decal on the ground.

That is exactly the user's "flat 2D trail that doesn't face the camera", and it explains the width
deficit as the same defect: a horizontal band seen from the game's ~55-degree camera is foreshortened
by about cos(55) = 0.57, i.e. the 1.5-1.8x narrowness measured twice.

It also means **SC2 ribbons may simply not be camera-facing**, and Blizzard's trails read correctly
because the RTS camera angle is fixed and known. Warcraft III's PopcornFX ribbon *is* camera-facing,
which is the mismatch at the heart of this.

**`RibPlaneA` asks what does set the plane.** Same four-ribbon rig, one candidate each:
`a_control` (red, as shipped), `b_vertpath` (green, the bone's circle stood up into XZ — does the
band follow the path's plane?), `c_length1` (blue, `RIB_.length` = 1, which 97% of Blizzard's missile
ribbons write and we never have), `d_boneroll` (amber, the bone rolled 90 degrees about X — does the
bone's orientation reach the ribbon at all?). `RibbonType` and `RibbonLength` are both plumbed
through `MdxParticleEmitter2` now, so whichever wins can be adopted directly.

## `RibPlaneA`: it is the bone, not `ribbon_type` and not `length`

Colour-keyed drawn area per frame over a 16-second orbit, with the control markers' glow halos
masked out:

| variant | median px | max/median | frames near zero |
|---|---|---|---|
| red `a_control` (as shipped) | 1 | 44x | 82% |
| green `b_vertpath` (circle stood up) | 191 | 4.6x | 32% |
| blue `c_length1` (`RIB_.length` = 1) | 101 | 5.9x | 33% |
| amber `d_boneroll` (bone rolled 90 deg) | 222 | **2.9x** | **11%** |

So `RIB_.length` does nothing — it is no steadier than the control's neighbours — and neither does
`ribbon_type`. **A roll on the bone does**: amber is far the steadiest and is almost always visible.
The bone's orientation reaches the ribbon's plane.

*Measurement trap worth keeping:* the magenta tracer card's blue-violet glow first read as a
rock-steady "blue ribbon" of 1,464 px and made `length` = 1 look like the answer outright. Dilate
both control markers out before keying anything by colour, and look at the mask over a frame.

**`RibPlaneB` follows it up.** A roll fixed in world space cannot hold its angle all the way round a
circle, because the path turns underneath it, so this one rolls about the **path tangent** at
0/45/90/135 degrees. 90 should put the band's width along world Z — foreshortened by cos(35) = 0.82
from the game's fixed ~55-degree camera, against a ground-flat band's cos(55) = 0.57, and never
edge-on for a camera above the horizon.

## Exports now go straight into the map

The test map is **unpacked again**, and the user asked for exports to land in it directly. Pass
**`map`** as the output directory (`--export1 <casc> map --name X`, `--ribtest map --name X`);
`spike\MdxProbe\Deploy.cs` resolves it to
`C:\Games\StarCraft II\Maps\Test\modeltest.SC2Map\Assets` and writes the `.m3` flat, the way
the editor wants it. The default texture prefix already resolves to the map's `Assets\Textures\`.
The editor still caches game data per session, so a name it has already loaded needs a restart —
keep using a new name each time.

## `RibPlaneB`: rolling about the path does not work either

Roll about the path tangent, 0/45/90/135 degrees, otherwise identical ribbons:

| variant | median px | max/median | frames near zero |
|---|---|---|---|
| green `b_roll45` | 82 | 9.4x | 15% |
| blue `c_roll90` | 107 | 6.5x | 25% |
| amber `d_roll135` | 56 | 7.6x | 26% |

All still swing 6-9x, and none beats `RibPlaneA`'s world-X 90-degree roll (2.9x / 11%). So a
path-relative roll is not the answer either.

**Correction worth carrying: slot 0 of the test rig is dead.** Red came out at a median of 1 px and
76-82% near-zero in *all three* tests — `RibTypeA`, `RibPlaneA`, `RibPlaneB` — while other variants
with the same settings on other nodes drew fine, and in `RibPlaneB` red carries a rotation track
(identity) and is still dead. It is node 0, `Cylinder07`, which is also the node `ctl_tracer` is
hosted to. **Every "control" row in all three tests was measuring a dead slot, not the control
configuration.** Fix the rig before running another variant sweep, or put the control on a known-good
node.

## The alternative: stop using a ribbon for the trail

Four experiments have now failed to make a `RIB_` hold its face: `ribbon_type` (all four values),
`RIB_.length`, a vertical path, a world-axis bone roll and a path-relative bone roll. The working
hypothesis is that SC2 ribbons simply are not camera-facing, and Blizzard's read correctly only
because the RTS camera is fixed.

A camera-facing **card** has no such problem — that is what `PAR_` does by construction, and the
exporter already has that path. `M3ExportOptions.BakeTrails` (probe: `--notrailbake`) skips the trail
bake so the per-distance layers stay per-layer `PAR_` stand-ins while the head still bakes.
`VfxFireballX` is that build: **7 particle systems + 2 ribbons**, where `VfxFireballU` was 1 + 1. The
per-distance smoke and flame — which is where the trail's width comes from — now always faces the
viewer; only the two genuine PopcornFX ribbons stay ribbons.

## The particle trail wins, and is now the default

`VfxFireballX` (head baked, trail left as per-layer camera-facing `PAR_` stand-ins) measured against
the editor's comet, trail width as a fraction of the head's:

| behind the head, in head-widths | WC3 editor | X, particles | U, baked ribbon |
|---|---|---|---|
| 0.43 | 0.78 | **0.96** | 0.37 |
| 1.30 | 0.57 | **0.80** | 0.20 |
| 1.73 | 0.53 | 0.27 | 0.14 |
| 2.17 | 0.40 | 0.10 | 0.08 |

Near the head it now matches and slightly overshoots, where the ribbon was 2.5x too narrow. It also
brings back **the dark smoke curling off the path**, which the strip never showed at all — so the
comet finally has the editor's structure: bright head, flame, smoke.

`M3ExportOptions.BakeTrails` is therefore **off by default** (probe: `--trailbake` puts the ribbon
strip back for an A/B). `--trailwidth` applies only to the ribbon path and is now moot.

## `VfxFireballY` / `Z`: both moving, both good

`Y` carries a `--fly 900` track. `Z` has **no location track in the file** (confirmed: no `VEC3`
sections) — but the user moved it in the editor with **cutscene animation frames**, so it was moving
too. Both draw the same comet, which is the expected result and says nothing more.

**Retracted:** an earlier version of this document concluded from `Z` that "the particle trail no
longer depends on the model moving" and wrote up a trade-off about parked models trailing and the
head reading oval. That was inferred from `Z` having no location track, without knowing the user had
moved it by hand. There is **no evidence either way** about the parked case — it has not been tested.

What is true from the code rather than the clip: the per-distance layers become `PAR_` stand-ins with
a measured **emission rate**, and a `PAR_` emits at its rate whether or not the emitter moves. So a
genuinely parked model would still emit, and the particles — world-space, drifting on their own
10-12 u/s — would pile up near the head rather than string out behind it. Whether that reads wrong
is untested. Warcraft III, whose layers spawn per distance travelled, would emit nothing at all.
Note Blizzard do not model per-distance spawning either: `use_trailing_particle` is set on 3 of 1,310
particle systems across their missile models (0.2%).

**Workflow note worth keeping:** the user can move a model in the editor with **cutscene animation
frames**, which is a far cheaper way to test a trail than baking a flight into the export. `--fly`
is still useful for a repeatable, known-speed run, but it is no longer the only way to get motion.

## The bake was tuned on the smallest effect, and it does not generalise

User, on the five other effects: *"The previews are regressions on the earlier versions honestly.
Like really low quality."* The Unholy Aura's runes come out soft and smeared where the editor's are
crisp glyphs. The cause is resolution, and it is structural:

| effect | footprint | cell | units/px | peak linear |
|---|---|---|---|---|
| **Fireball** | 72 u | 128 px | **0.6** | 45.6 |
| Unholy Aura | 127 u | 128 px | 1.0 | **3.6** |
| Thunder Clap | 345 u | 256 px | 1.3 | 83.3 |
| Revive | 546 u | 256 px | 2.1 | 27.8 |
| Holy Light | 600 u | 256 px | **2.3** | 8.8 |

The fireball — the one effect the bake was developed and validated against — is the **only** one at
sub-unit resolution. Everything else is 2-4x coarser. `TargetUnitsPerPixel` is 2.5, so a cell only
doubles once the footprint would exceed that, and `MaxCellSize` caps it at 256 regardless. A soft
fire blob survives that; a rune glyph 20 units across, drawn from a 256-pixel sprite into 20 pixels,
does not.

Two fixes, and they are independent:

1. **Spend the atlas budget on resolution, not frames.** 8x8 = 64 cells is the constraint. The same
   2048 atlas at 4x4 gives 512-pixel cells — 4x the linear detail — and at 2x2, 8x. A rune ring that
   turns slowly does not need 64 distinct frames; it needs readable runes. Choose the grid from the
   footprint instead of fixing it at 8x8.
2. **Do not bake an effect that has no HDR to resolve.** The bake exists to stand in for Warcraft
   III's tone mapping over dense overlapping additive sprites. Peak linear says who needs it: the
   fireball 45.6 and Thunder Clap 83.3 genuinely do; the **Unholy Aura at 3.6 has essentially
   nothing to tone map** and pays the resolution cost for no gain. Per-layer stand-ins draw each
   sprite at its own full resolution and are sharp.

Both `Preview<Effect>` (baked) and `Preview<Effect>Raw` (`--nobake`, per-layer stand-ins, what the
earlier good versions were) are in the map for a direct A/B.

## Round 4: unbaked wins, brightness was 2x, and the aura's runes do not descend

User on the five unbaked previews: *"they all look great, but maybe a bit on the brighter side"* —
so per-layer stand-ins beat the bake on every one of them, which settles the resolution finding above.

**Brightness: `ParticleGlow` 2 -> 1.** Measured on the Unholy Aura against
`VfxUnholyAuraA-wc3editor`, as light added over the background: the editor peaks at 137 with the lit
area averaging 44 and a median of 37; ours peaked at a comparable 158 but averaged **97** with a
median of **113**. The peak was near right and the **mid-tones were 2.2-3x too high** — a gain
pushing everything toward the clip, not one merely too strong. The 2x dated from 2026-09-24, before
particle materials were fixed to declare `vertex_color | vertex_alpha`; without those a particle
could not deliver its own ramps at all, and once they worked the 2x became an overshoot.

*Trap:* `Program.cs` repeated `2f` as its own default for `--glow`, so changing
`M3ExportOptions.ParticleGlow` did nothing to probe exports and the first re-export came out
byte-identical. Both `--glow` and `--hdrcap` now fall through to the option's default instead of
repeating a number. Verified in the files: the aura's `hdr_emis` went 2.0/2.5/3.25 -> 1.0/1.25/1.5.

**The aura's runes are meant to descend, and ours do not.** User: *"the runes that surround the aura
are meant to descend at the start ... it only does that animation on the first try."* Confirmed in
the simulation (`--pkrun` on `UnholyAura.pkb`, slot 8 `n46`): the four `DemonRune3` runes start at
**z = 2.69 m (134 units)** and ease down to **z ~0.75 m (38 units)** over their first ~20 steps —
accelerating, then decelerating — after which they bob gently between 0.64 and 0.78 forever.

We lose it because `PkRendererStats` measures speed from the **second half** of each particle's
samples (the same choice that makes orbit measurement work), by which time the descent is over and
the rune is bobbing: it reads speed 0 and the settled height, so the stand-in spawns the runes
already in place. The layer is `births 1, immortal`, so there is no re-emission to hide it.

The fix has a proven precedent: orbiting stand-ins already get a `Spin_` bone with baked rotation
keys. The same shape works here — a bone carrying the measured Z-descent. The wrinkle is that it must
play **once**, not every loop, and the Warcraft III model has only one sequence (`Stand 1`, 6667 ms):
faithful behaviour needs the descent synthesised into a **Birth** sequence with `Stand` holding the
settled height, which the exporter already does for other models (Holy Light exports `Birth, Stand`).

## What is still open

- **The trail tails off sooner than the editor's** (0.27 vs 0.53 at 1.73 head-widths, and gone by
  2.6 where the editor still has 0.30). Note the reference clip is a **hand-drag at unknown speed**,
  so its absolute length is not a fair target from that recording — judge this on a real missile, or
  on a `--fly` build at a known speed against a WC3 clip at a matched speed.
- **Every other effect is untouched by this.** Holy Light, Thunder Clap, Unholy Aura, Revive and
  Resurrect Caster have no trail layers, so they are unchanged — and still unviewed in SC2.
- **The test rig's slot 0 is dead** — fix before any further `--ribtest` sweep.
- **`RibbonColourAlpha` — the reason recorded for it is now disproven, but the answer is not.**
  It is `false` (the strip is baked as colour × texture, alpha left out) because the measured
  alphas "could not produce the editor's saturated comet" — and the comet is now measured not to be
  saturated. Re-measured under Clip, the two settings are closer than that argument implied and
  neither wins outright:

  | | clipped white at the head (ref 0.3-3.4%) | mid-trail colour (ref B130-160 G255 R255) | reach |
  |---|---|---|---|
  | alpha **off** (today) | 11.9% — too hot | B 85-94 | lit to 80% of the strip |
  | alpha **on** | 0.0% | B 95-110 | lit to 60% of the strip |

  Off is too white where the strip meets the head card, which is precisely where they overlap; on
  fades out too early. The reference's trail is brighter and bluer than both. **Left unchanged** —
  this needs the in-game A/B on a moving missile that has never been run (`--ribbonalpha`).
- **`PAR_.flags` two_sided (0x8)** keeps our particle material at "NEVER" in the corpus, though the
  bit itself is set on 2.5% of particle and 7.0% of ribbon materials. Left alone deliberately: a
  ribbon that turns a corner can show its back face, and dropping it risks the strip vanishing on
  one side. The other `PAR_` outliers (`uv_ss_tiling`, `spline_bounds_max`, `rotation_flags`,
  `mass`, `emit_shape_size`) are unchanged and still not known to matter.
- **Only the fireball has been looked at closely.** Holy Light, Thunder Clap, Unholy Aura, Revive
  and Resurrect Caster were exported and never viewed in SC2. Holy Light is a thin vertical beam and
  Unholy Aura a rune ring — neither is a card, so neither is covered by this calibration.

## Build, export, test

```
dotnet build "src\Wc3ModelViewer.slnx" -c Release
dotnet build "spike\MdxProbe" -c Debug

cd "spike\MdxProbe"
bin\Debug\net10.0-windows\MdxProbe.exe --export1 ^
  "war3.w3mod:_de.w3mod:abilities\weapons\fireballmissile\fireballmissile2.mdx" ^
  sc2test --scale 0.025 --name VfxFireballU
```

Output lands in `sc2test\<name>\<name>.m3` plus `textures\*.dds`. **Use a new letter every time** —
the editor caches by name, and overwriting a loaded model needs a restart. `VfxFireballY` (flying) and `VfxFireballZ` (static) are the
current exports, on the new particle-trail default. `VfxFireballT` was the tone-map + MAT_ fix build. `VfxFireballS` has the tone map fix only. **`VfxFireballU` is
`T` plus `--fly 900`** — the first build whose trail can be seen at all. `VfxFireballV` and
`VfxFireballW` are `U` at `--trailwidth 1.5` and `2.0` (RIB_ scale 1.4 -> 2.1 -> 2.8).

Probe flags: `--nobake` (old per-layer stand-ins), `--bake <casc> <dir> [--frames] [--stats]
[--layerstats]` (PNG sheets, per-frame luminance, measured per-layer stats, no SC2 needed),
**`--onlylayer n30[,n18]`** and **`--footprint cx cy side`** (new: bake one layer on its own while
holding the frame fixed, so a layer's contribution can be looked at beside the whole — how the
n34-drowns-n30 question was settled), `--bakepng <dir>`, `--trailribbonspeed N`, `--ribbonalpha`,
`--exposure/--tonemap/--pitch/--loop`, `--ribtest <dir>`, `--fbcalib <dir>`.

Verification in `spike\m3verify\` (they use m3studio's `structures.xml` as the format truth):
`m3par.py`, `hdrrib.py --file`, `m3dump.py`, and **`m3audit.py <ours.m3> [corpus-root] [n]`**, which
ranks every field where our output is an outlier against Blizzard's corpus. It found both the
`additional_flags` bug and this session's `transparent_depth_effects` one. Note it harvests **only**
particle and ribbon materials, not geoset ones.

Acceptance check (Blender 3.3 is installed):
```
"C:\Program Files\Blender Foundation\Blender 3.3\blender.exe" --background --factory-startup ^
  --python spike\m3verify\m3accept.py -- path\to\model.m3
```

## The SC2 test map

`C:\Games\StarCraft II\Maps\Test\modeltest.SC2Map` **is a single packed 99 MB file** (the user saved
it 2026-09-25 08:46) and the unpacked `.old` copy beside it is gone. So the copy-files-in workflow
no longer applies: use the editor's Import module, or ask the user to save it back out as a folder.

While it was a folder: copy the `.m3` into `Assets\` and the textures into `Assets\Textures\`, then
point `Base.SC2Data\GameData\ModelData.xml`'s `<CModel id="modeltest">` at the new file. The placed
object lives in `Objects` and names its world position, which is where it must be clicked to select
it — the rendered head floats well above that point. **The editor caches game data for the session:
it must be restarted, not just reopened, before a catalog change takes effect.**

## Driving the SC2 editor

The user authorised this: "You can control the sc2 editor by opening their import panel and looking
at the fireballs. If you needed to you can navigate the data editor and change the model test model."

**Check which map is open first.** On 2026-09-25 the editor was running with the user's *other*
project (`AegiosDarkwoodPTR.SC2Map`) loaded and live; switching maps would have risked their unsaved
work. That is a "hand the build over and say what to look at" situation, not an automation one.

Traps that each cost a wrong answer:
- **Call `SetProcessDPIAware()` first.** The display is at 125%. Without it, captures are in
  physical pixels but `SetCursorPos` is scaled, so every click lands 25% low — one opened the user's
  other project by mistake.
- **Do not `ShowWindow(SW_RESTORE)`** to focus; it un-maximises the editor and invalidates every
  coordinate. `SetForegroundWindow` alone.
- Drive the File menu by number (Alt+F then a digit), after reading the list — it is not ordered by
  recency. `Ctrl+F9` Test Document did not launch the game. There is no Previewer module.

## Where the code is

| File | What |
|---|---|
| `src\...\Formats\Popcorn\PkImpostorBaker.cs` | the whole bake: `Bake` (flipbook sheet), `BakeTrail` (ribbon strip), camera, tone map, matte, crop; `OnlyLayers`/`ForceFootprint` are the new diagnostics |
| `src\...\Formats\Popcorn\PkSpriteGeometry.cs` | sprite quads, shared with the viewer |
| `src\...\Formats\PopcornApproximation.cs` | `SynthesizeImpostor`, `SynthesizeTrail`, per-layer stand-ins |
| `src\...\Convert\M3Exporter.cs` | `BakeImpostors`, `AddParticleMaterial`, material flags |
| `src\...\Convert\M3ParticleWriter.cs` | `PAR_` and `BuildRibbon` byte layout |
| `spike\MdxProbe\BakeProbe.cs`, `RibTest.cs`, `Program.cs` | probes |
| `spike\m3verify\m3audit.py` | corpus outlier auditor |

## Standing rules from the user

- Judge effect exports **only** against the Warcraft III editor recording of the **same model**.
  Not the viewer, not the SC2 Archive Browser preview.
- A new name for every test export.
- **Do not commit or push unless asked.**
- A fix is not done until the exe is in `bin\Release`.
