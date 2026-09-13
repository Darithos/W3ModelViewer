# PopcornFX compiled scripts — the VM inside a `.pkb` bake

Established 2026-09-13 from Warcraft III's own bakes (2,165 files, 54,716 compiled scripts). Nothing
here comes from a PopcornFX specification — there is none public for the runtime format — so every
claim below is something the real files were made to agree with. The container itself is described
in `popcornfx-bake.md`; this note covers what the `CCompilerBlobCache` records *contain*, and how
`Wc3ModelViewer.Core.Formats.Popcorn` (`PkBakeFile`, `PkScript`, `PkEffectDef`, `PkRuntime`,
`PkCornPlayer`) runs them. Probe modes: `--pkdis <pkb> [layer]` disassembles, `--pkrun <pkb> [s]`
simulates and prints each layer, `--pksweep <dir>` runs every dumped bake (`--dumpbakes <dir>`).

## Why it matters

The earlier approximation guessed lifetimes and sizes from the constants a layer happened to keep.
The user's first look at Holy Light (a white needle, then a wide ellipse over a rectangle) showed
where that goes wrong: the "beam" is 0.1–0.4 m wide and grows from 5.4 to 13.2 m over its life, the
base "rectangle" is thin sparks rising at 3–7 m/s stretched along their velocity, and the spawners
emit in bursts of 0.3–0.6 s rather than for the whole sequence. All of that is in the scripts.

## Records that belong to a script

| class | fields | meaning |
|---|---|---|
| `CCompilerBlobCache` | 1 kind, 2 words, 3 externals, 4 functions, 5 entry point | kind absent = spawn, 3 = evolve on the spawn frame, 4 = evolve |
| `CCompilerBlobCacheExternal` | 0 name, 1 type name | stream fields (`n20_8__Size`), `scene.dt`, `__sampler_N`, `__e_Child`, `__spatialLayer_N`, `__a_Game.*` attributes |
| `CCompilerBlobCacheFunctionDef` | 0 name, 3 args | `rand`, `sample`, `generate`, `kick`, … |

Field 2 is a word array, but it is really bytes: header `u32[9]` = `{0, 0, constBytes, codeBytes,
0, constCount, ?, ?, ?}`, then `constBytes` of constants (32 bytes each: one value broadcast over 8
SIMD lanes — the first 4 lanes are the value), then `codeBytes` of byte code.

## Operands

Four bytes: `u16 index, u8 space, u8 kind`.

- **space** — `0x00` constant table, `0x01`/`0x02`/`0x03` three register spaces (the compiler splits
  temporaries by how they vary; a runtime can treat them as three arrays), `0x10` a typed zero
  (default rotation, `invLife = 0` meaning immortal), `0xFF` none.
- **kind** — `0x02..0x05` bool×1..4 (all-ones masks), `0x1A..0x1D` int×1..4, `0x20..0x23`
  float×1..4, `0x25` quaternion, `0x00` pointer (contexts). A field record stores kind − 1.

## Instructions (12 opcodes, no jumps — branches compile to select)

| op | length | form |
|---|---|---|
| `43` load | 7 | `dst(4) u16 externalSlot` |
| `44` store | 7 | `src(4) u16 externalSlot` |
| `4A` cast | 9 | `dst src` — float4 ↔ quaternion bit copy |
| `4B` cast | 9 | `dst src` — bool/int/float conversions (true → 1) |
| `4C` vector | 2 + 4(n+2) | `u8 n-1, dst, n components` |
| `4D` swizzle | 12 | `u24 laneCodes, dst, src` |
| `4E` binary | 14 | `u8 sub, dst, a, b` |
| `4F` unary | 10 | `u8 sub, dst, a` |
| `50` binary2 | 14 | `u8 sub, dst, a, b` |
| `51` ternary | 18 | `u8 sub(0 = lerp), dst, a, b, t` |
| `52` select | 17 | `dst, a, b, cond` → `cond ? b : a` |
| `53` call | 11 + 5·argc | `u8 ?, u16 thisExternal (FFFF = free), u16 function, u8 argc, dst, argc × (u8 flags, operand)` |

With those lengths all 54,716 scripts parse to exactly their byte count.

- **binary** — 00 add, 01 sub, 02 mul, 03 div, 04 int mod, 05 negate, 08 and, 09 or, 0B not,
  0C `<`, 0D `<=`, 0E `>`, 0F `>=`, 10 `==`, 11 `!=`.
- **unary** — 00 sqrt, 01 rsqrt (both after `dot(v,v)`), 07 angle → (sin, cos), 0D and 2C exp
  (drag integrals `0.5 − 0.5·exp(−2dt)`), 11 reciprocal (`invLife = 1/rand(...)`), 12 abs,
  13 sign, 16 and 17 frac (effect age straight into a curve sample; the hero glow's pulse),
  18 saturate, 24 normalize, 31 any, 33 non-zero.
- **binary2** — 19 pow, 1B min, 1C max, 1D dot, 1E cross.

## Swizzle lane codes

`v = b1 | b2<<8 | b3<<16`. Lane 0 = `(v>>8)&7`, lane 1 = `(v>>11)&7`, lane 2 = `(v>>14)&3` with a
constant flag at bit 20, lane 3 = `(v>>21)&7`. Codes 0–3 pick a source lane, 4 is 0.0, 5 is 1.0
(lane 2 with its flag set is 0.0/1.0 by its 2-bit code). `b1` and the low nibble of `b3` are
redundant for decoding. Only 27 distinct patterns occur; the layout was found by a search
constrained by idioms whose meaning is certain — the scene-intersect result split into `www`, `xyz`
and `(0,0,w)`; `(sin, cos)` into the quaternion `(0,0,sin,cos)`; `float4(x,x,x,1)` before dividing a
colour; `(1,1,1,x)` alpha fades; and axis masks `(0,0,z)`, `(x,0,z)`, `(0,y,z)`.

## Layer graph and events

- `CLayerGraphCompileCache`: 2 → layer slots, 3 → event slots.
- `…_LayerSlot`: 0 → the `CLayerCompileCache`, 1 input event slot indices, 2 output event slot indices.
- `…_EventSlot`: 0 name, 2 target layer slot indices (plain indices, not references).
- Slot 0 is the effect root: its spawn runs once; its evolve walks an `EventStream` sampler
  (field 10: event times) and fires `Signal`, which targets the spawner layers.
- A spawner's evolve calls `generate(state, amount, step, …)` → `(fraction, emitted, count)`:
  one particle per `step` of accumulated amount. Rate spawners pass `dt·rate` with step 1, bursts pass
  a constant with `invLife = inf` (one frame), Lightning Shield's orbs pass `dt` with step 0.025. The
  state is seeded at 0.99999 so the first particle comes on the first frame.
- `trigger(cond, count)` fires on a condition (`lifeRatio >= 1` for `__e_OnDeath`, `index % 7 == 0`).
- Payloads: `initPayload(0, gen)` → handle; `buildPayloadElement(gen, from, to, flags)` (flags 768
  spawn index, 258 position, 516 orientation, 1 lerp, 0 constant); `appendPayload(h, i, element)`;
  `kick(h)`. **A child reads by name**: the firing layer's event record (layer field 5, named) lists
  payload names in append order; the child layer's field 12 points at an unnamed event whose payload
  list is its `extractPayloadElement` index order. Lightning Shield's orb appends
  `EmittedCount, Position, Orientation, Color, SpatialKey1` and its arcs read Color at 4 and the key at
  7 of `EmittedCount, Position, Orientation, Velocity, Color, Size, LifeRatio, SpatialKey1`.
- A `Position` (and `Orientation`) payload makes the parent particle the local frame of the child's
  spawn script: `xform_l2w` samples a shape around the orb that fired it.
- Spatial layers (layer field 6 → `CLayerCompileCacheSpatialLayer`, field 0 a name shared between
  layers): `allocatePayload`, `appendPayload(h, value, i)`, `insert(h, key)`, and
  `closestF3(key, radius, i)` returns the value published under the nearest key. Lightning Shield's
  arcs find their orb this way every frame.
- A layer with no evolve script and no renderer only relays events from its spawn script.

## Lifetime

`self.lifeRatio += dt · self.invLife` before evolve; a particle dies once `lifeRatio >= 1` after its
evolve has run (so `trigger(lifeRatio >= 1)` still fires) or when `self.kill(true)` is called.
`invLife = 0` is immortal (continuous spawners, the hero glow).

## Renderers

`CLayerCompileCacheRenderer`: 1 inputs, 2 properties, 3 material (`Default_Billboard` 13,908,
`Default_Ribbon` 236, `Distortion_Billboard` 83, `Default_Light` 1). An input is `{0 id, 1 field index,
2 name}`: id 0 Position, 1 Size, 2 Enabled, 3/6 Rotation, 4 Axis, 5 NormalAxis, 7 Size2, 9 SelfID,
10 ParentID, ≥100 named (`Color`, `TextureID`, `UVOffset`, …). Properties carry `BillboardingMode`,
`Transparent.Type`, `Diffuse.DiffuseMap`, `Atlas.SubDiv` (int2).

- Size is a **radius**: the hero glow's ground disc is 1.03 m, i.e. 103 units across a hero.
- AxisAligned cards run `Position ± Axis/2` (Holy Light lifts its beam by half the axis so it starts
  at the ground); the spheroid and capsule modes extend both ends by the size.
- Rotation is in degrees (`rand(-180, 180)`).
- Units: 1 m = 50 Warcraft III units (see `popcornfx-bake.md`).

## Built-in functions

Implemented from their names and uses: `rand` (per lane), `vrand`, shape `samplePosition` (types 1
sphere, 2 ellipsoid, 3 cylinder, 4 capsule, 5 cone, 6 mesh; field 17 radius, 18 inner radius, 20
height, 13 offset in PopcornFX's Y-up shape frame, 14 Euler degrees), curve `sample` (cubic Hermite,
tangents as in/out per knot), `xform_l2w/w2l_f/d_masked`, `rgb2hsv`/`hsv2rgb` (hue 0..1),
`radians.rotate`, `rotate`, `orientation_axisSide/Up/Forward`, `effect.age/isRunning/position/axis*`,
`view.position/distance`. Engine attributes: `__a_Game.TeamColor` (player colour),
`__a_Game.ColorMultiplier`, `__a_Game.EmissionRateMultiplier`, `__a_Game.Scale` (1). Scene queries
(`scene.intersect`, `closestF3` on an empty layer) answer "nothing there". Turbulence is a cheap
smooth noise stand-in, but scaled by the sampler's own parameters. `CParticleNodeSamplerData_Turbulence`
field 11 is the wavelength (0.1 or 6 where written) and field 12 the strength (0.01–2). Both default
to 1, and across every bake neither field holds exactly 1, which is how defaults show. Strength
matters: Unholy Aura's four runes are `axisSide(rotZ(0.436·age + k·π/4))·0.9 + turbulence(…)`, a
0.9 m orbit with 0.01 of wobble. Full-strength noise scattered them metres off the ring.

## Coverage (2026-09-13)

All 2,165 bakes load and run for 3 s without error. 37 draw nothing in a static preview — almost all
missiles (their trails emit per distance travelled) and a few ground-targeted spells that read a
target position. Functions still answered with zeros: `samplerShape.projectPCoords/samplePCoords/
intersect/project` (19 bakes), `view.resolution` (2), `samplerSpectrum.sample` (1).
