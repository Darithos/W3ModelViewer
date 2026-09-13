# PopcornFX `.pkb` bakes — what Reforged's CORN emitters point at

Established 2026-09-13 from the real files in Warcraft III build 2.0.4 / Definitive Edition 3.0
(`abilities\spells\human\holybolt\holyboltspecialart.pkb` first, then Thunder Clap, the hero glow,
the priest's attack, the paladin's spell; the record walker was then run over all 2,165 bakes in the
archive without a failure). Everything below was read out of bytes, not out of a specification —
PopcornFX has none public for this format.

## Where the data is

- A `CORN` entry in the `.mdx` is an ordinary node (name, object id, parent, flags `0x1000`, TRS
  tracks) followed by two `C4Color`s (colour multiplier, team colour with alpha 0 = off), a 260-byte
  `.pkfx` path, a 260-byte `popcornFlags` string, then optional tracks `KPPA KPPC KPPE KPPL KPPS
  KPPV` (alpha, colour, emission-rate, lifespan and speed multipliers, visibility).
- The `.pkfx` is the source project; the archive ships the **bake** at the same path with the
  extension changed to `.pkb`, under the model's own tree (`war3.w3mod:_hd.w3mod:` or
  `_de.w3mod:`). 2,165 bakes ship; every reference resolves.
- `popcornFlags` gates the effect per sequence: `Always=on, Death=off, Decay=off`,
  `Always=off, Attack Spell=on`, `Always=Off, Dissipate=Off, Portrait=Off`. Blizzard's data has
  typos (`Alwyas`) and stray spaces; match by leading words, most specific wins.

## Container

```
0x00  u32 magic 0xCA000B11      0x04 u32 version (02 05 01 01)
0x08  u32 ?                     0x0C u32 recordCount
0x10  u32 classCount            0x14 u32 stringTableOffset
0x18  u32 0
0x1C  classCount × { u32 classNameStringIndex, u32 instanceCount }   (counts sum to recordCount)
....  recordCount records
strT  u32 stringCount, then stringCount × { u8 length, bytes }        (runs exactly to EOF)
```

A record is `{ u32 size; u8 0x20; u32 classNameStringIndex; u16 fieldCount; (u16 fieldIndex,
value)* }`, `size` counting from the `0x20` byte. Field indices ascend; a field at its default is
simply absent. **Object references are 1-based** (0 = null) — proven by renderer→property and
layer→sampler links landing on the right classes.

Value types are not encoded. The runtime knows them from its class definitions, so a reader has to
infer them: try `{4, 1, 2, 8, 12, 16 bytes; count-prefixed arrays of 1/2/4/8/12/16-byte elements}`
and keep the parse under which every next field index is larger and the record ends on its size.
One parse survives in practice; a small per-class preference table settles the rest.

## Classes that matter (22 in HD bakes, 24 in DE with `CParticleAttributeDeclaration` and `CLayerCompileCacheAttrib`)

| class | fields used | meaning |
|---|---|---|
| `CLayerCompileCache` | 1 arr16, 2 arr4, 7 arr4, 8 arr4, 10 arr4 | one graph layer: constant pool (1), particle fields (2), samplers (7), renderers (8), bytecode blobs (10). A layer with no renderer is a spawner or event layer. |
| `CLayerCompileCacheRenderer` | 1 arr4, 2 arr4, 3 u32 | particle inputs, properties, material path (`Default_Billboard.pkma`) |
| `CLayerCompileCacheRendererProperty` | 0 name, 1 type, 2 float4 value, 3 string | `BillboardingMode` (type 3: 0 ScreenAligned, 1 ViewposAligned, 2 AxisAligned, 3 AxisAlignedSpheroid, 4 AxisAlignedCapsule, 5 PlaneAligned), `Transparent.Type` (0 Additive, 1 AdditiveNoAlpha, 2 AlphaBlend, 3 Premultiplied), `Diffuse.DiffuseMap` (type 12, string in field 3, e.g. `_HD.w3mod/Textures/FX/Flare/Flare_BW.tif`), `SoftParticles.SoftnessDistance` |
| `CLayerCompileCacheSampler` | 0 name, 1 data ref | `__sampler_N` → a Curve or Shape record |
| `CParticleNodeSamplerData_Curve` | 9 dim, 16 knots, 17 values, 18 tangents | colour over life (dim 4) or a scalar over life (dim 1); knots in 0..1 |
| `CParticleNodeSamplerData_Shape` | 13 v3, 15 type, 17 f32, 20 f32 | spawn volume (dimensions, radius) |
| `CLayerCompileCacheField` | 0 name | particle stream fields: `self.lifeRatio`, `n20_8__Size`, `n20_8__Axis`, `n20_8__Color`, `Rotation_…` — the `nNN` prefix is the graph node |
| `CCompilerBlobCache*` | — | compiled per-particle scripts — decoded and executed; see `popcornfx-vm.md` |

Across the archive: 14,228 renderer layers; 13,446 carry a colour curve, 9,107 a scalar curve;
9,313 screen-aligned, 1,992 plane-aligned, 2,872 axis-aligned; 11,696 additive, 2,515 alpha-blend.

## The constant pool and what can be recovered from it

Field 1 of a layer is an array of float4 "registers". Scalars are broadcast to all four lanes;
between them sit int4 masks of ones. What the surveyed layers showed:

- **Lifetime.** The spawn script sets life first, so the first float the bytecode broadcasts is the
  life (or its lower bound, the upper next). The compiler derives `1/life` for `lifeRatio` and keeps
  it in the pool, so a blob float whose reciprocal is in the pool is confirmed. Held on every layer
  checked (0.4–0.6 s beam, 0.3–0.5 s flare, 0.3 s Thunder Clap ring, …).
- **Spawn count and spawner duration** are copied into the layer as equal adjacent pairs
  (`8 8`, `18 18`, `6.4 6.4`, `0.6 0.6`). The pair under three seconds is the spawner's duration,
  the largest remaining pair the count.
- **Single-lane entries** are positions, axes and curve bounds: `(0,0,5.43,0)`/`(0,0,13.2,0)` is the
  Holy Light beam's axis range; `(0,0,-40,0)`/`(0,0,-30,0)` is Thunder Clap's spark gravity; an
  X-only entry is the scalar curve's min/max and says nothing about direction.
- **Bytecode blobs** are compiled for 8-wide SIMD: a script constant sits in the blob as the same
  32-bit word eight times. Those words (`0.6 0.4 22 1 0.1 0.5 30 360 …`) are the only readable
  part of a script; the mapping of constant to meaning is otherwise lost.

## Units

PopcornFX works in metres and Reforged's HD art is at 1 unit = 2 cm. Three independent checks agree:
the Holy Light beam axis of 5.4–13.2 lands on the SD model's 270–650-unit tall beam geometry;
Thunder Clap's ring grows to 6.8 (= 340 units) against a 250-unit gameplay radius; Blizzard's own
War3 mod for StarCraft II converts at 0.02, i.e. one metre per SC2 unit. So bake lengths × 50 are
Warcraft III units.

## What this buys

**Superseded for the viewer (2026-09-13):** the compiled scripts turned out to be a small, regular
virtual machine, and the viewer now runs them — see `popcornfx-vm.md`. The approximation below is
kept for the StarCraft II exporter, which still writes stand-in `PAR_` systems, and its field
layouts above predate the exact schema in `PkBakeFile` (the generic walker described here can accept
a wrong-but-complete parse of Definitive Edition layer records).

`PopcornBake.Parse` reads the above; `PopcornApproximation.Attach` turns each renderer layer into a
stand-in `MdxParticleEmitter2` (texture, blend, orientation, colour curve exactly; life, count, size
and beam length by the rules above) that the viewer draws and `M3ParticleWriter` exports as a
`PAR_`: billboard (type 0) for screen-aligned layers, emitter-facing (type 7) for ground discs, and
for axis-aligned beams a **tail (type 1) with the `tail_fix` flag and `instance_tail` = the beam
length in metres, at zero speed** — a camera-facing card of fixed length along the emission
direction from the moment it is born, which is what a PopcornFX axis-aligned billboard is. That
mapping is Blizzard's own: over the ~18,300 HotS models, 1,905 fixed-tail emitters draw their light
streaks exactly so (the moonwells' rising sparks: `instance_tail` 2–3 m, speed 0–0.15, 215 of them
at speed 0). The first attempt, a ray (type 9) given a speed so it reached the bake's length at the
colour peak, kept growing after the peak and drew Holy Light three times too tall. Two other
conventions came out of the same census and now apply to every exported emitter, PRE2 included:
`flags.vertex_alpha` (0x200000, set on 96% of Blizzard's billboard emitters) is what lets the
colour ramp's alpha channel act, and `additional_flags.world_space` (0x8, 68%) is what keeps a
Warcraft III world-space particle from being hosted to its emitter; the speed randomise bit
(`additional_flags` 0x1) gates whether `emit_speed_random` applies at all. The greyscale `_BW`
sprites also exposed a viewer gap: the emitter's tint was never applied, so every PopcornFX layer
drew white; the material colour now follows the mean colour of the live particles, as its opacity
already followed their mean alpha. It is an approximation of the effect's look, never a
reproduction of its behaviour.

Blizzard's own StarCraft II Holy Light (`mods\war3.sc2mod\...\war3_holyboltspecialart.m3` in the
SC2 CASC) is a conversion of the **SD** model — same six textures, same three PRE2 emitters, beam as
cylinder geometry — so it is a reference for what SC2 accepts, not for the HD bake's structure.
