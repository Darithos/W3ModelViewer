using System.Numerics;
using Wc3ModelViewer.Core.Formats;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>
/// Writes StarCraft II <c>PAR_</c> particle systems from Warcraft III <c>PRE2</c> emitters.
/// </summary>
/// <remarks>
/// <para>
/// <c>PAR_</c> v24 is a 1496-byte, 141-field struct — four times the size of <c>MAT_</c>, whose one
/// wrong flag bit rejected every model this project produced until it was bisected out. Almost none
/// of those fields have a Warcraft III counterpart, and several carry defaults that are anything but
/// zero: <c>friction</c> and the mass multipliers are 1.0, the flipbook fractions are +infinity, and
/// four sentinel indices are -1. A zero-filled struct is therefore not a neutral starting point, it
/// is a wrong one.
/// </para>
/// <para>
/// So the struct is not hand-assembled. <see cref="Defaults"/> reproduces exactly what m3studio
/// builds for a fresh v24 particle system, generated from its <c>structures.xml</c> — the same
/// machine-readable spec this exporter has been validated against throughout — and only the fields
/// that genuinely map from Warcraft III are then overwritten. Every field left alone holds the value
/// StarCraft II already accepts. Regenerate with <c>spike/m3verify/m3pardefaults.py</c> if the
/// target version ever moves.
/// </para>
/// </remarks>
internal static class M3ParticleWriter
{
    public const int Version = 24;
    public const int Size = 1496;

    // Field offsets, generated from m3studio's structures.xml for PAR_ v24. An animation reference
    // is a 8-byte header (interpolation, flags, id) followed by default, null and unused:
    // float 20 bytes, colour 20, vector3 36, int16 16.
    private const int OffBone = 0;
    private const int OffMaterialIndex = 4;
    private const int OffEmitSpeed = 12;
    private const int OffEmitSpeedRandom = 32;
    private const int OffEmitSpreadX = 92;
    private const int OffEmitSpreadY = 112;
    private const int OffLifespan = 132;
    private const int OffGravity = 184;
    private const int OffSizeAnimMid = 188;
    private const int OffColorAnimMid = 192;
    private const int OffAlphaAnimMid = 196;
    private const int OffSize = 220;
    private const int OffColorInit = 292;
    private const int OffColorMid = 312;
    private const int OffColorEnd = 332;
    private const int OffEmitMax = 400;
    private const int OffEmitRate = 404;
    private const int OffEmitShape = 424;
    private const int OffEmitShapeSize = 428;
    private const int OffFlipbookCols = 728;
    private const int OffFlipbookRows = 730;
    private const int OffParticleType = 772;
    private const int OffFlags = 1232;

    /// <summary>
    /// The non-zero words of a default-initialised <c>PAR_</c> v24, as (byte offset, raw uint32).
    /// Only 17 of 374 words differ from zero, so the table is written out rather than embedded as an
    /// opaque blob — it stays reviewable, and a wrong value is visible in a diff.
    /// </summary>
    private static readonly (int Offset, uint Raw)[] Defaults =
    [
        (188, 0x3F000000),   // size_anim_mid            0.5
        (192, 0x3F000000),   // color_anim_mid           0.5
        (196, 0x3F000000),   // alpha_anim_mid           0.5
        (200, 0x3F000000),   // rotation_anim_mid        0.5
        (356, 0x3A83126F),   // mass                     0.001
        (360, 0x3F800000),   // mass2                    1.0
        (376, 0x3F800000),   // world_forces_mass_mult   1.0
        (732, 0x7F800000),   // uv_flipbook_col_fraction +inf
        (736, 0x7F800000),   // uv_flipbook_row_fraction +inf
        (744, 0x3F800000),   // friction                 1.0
        (748, 0xFFFFFFFF),   // collide_system           -1
        (776, 0x3F800000),   // instance_tail            1.0
        (792, 0x3F800000),   // instance_distance        1.0
        (1380, 0x00000002),  // lod_reduce                2
        (1428, 0xFFFFFFFF),  // trail_system             -1
        (1456, 0xFFFFFFFF),  // collide_splat            -1
        (1492, 0xFFFFFFFF),  // unknown87d57a7a          -1
    ];

    /// <summary>
    /// Builds one <c>PAR_</c> from a Warcraft III emitter.
    /// </summary>
    /// <param name="boneIndex">m3 BONE index of the node that positions the emitter.</param>
    /// <param name="materialIndex">Index into the model's material-reference list.</param>
    /// <param name="nextAnimId">Supplies fresh animation ids; every anim ref needs a unique one.</param>
    /// <param name="emitRateAnimId">
    /// The id the exporter has reserved for <c>emit_rate</c>, so a per-sequence SDR3 track can gate
    /// emission on the Warcraft III KP2V visibility track. Warcraft III switches emitters on and
    /// off per animation; StarCraft II has no such switch, so a rate of zero stands in for it.
    /// </param>
    /// <param name="restEmitRate">
    /// Rate to fall back on when no sequence drives the track — the emitter's rate during the
    /// model's primary Stand animation, so an emitter Warcraft III only switches on for a death or
    /// a spell stays silent at rest.
    /// </param>
    public static byte[] Build(MdxParticleEmitter2 e, int boneIndex, int materialIndex,
                               float scale, Func<uint> nextAnimId, uint emitRateAnimId,
                               float restEmitRate)
    {
        var b = new byte[Size];
        foreach (var (offset, raw) in Defaults) WriteU32(b, offset, raw);

        WriteI32(b, OffBone, boneIndex);
        WriteI32(b, OffMaterialIndex, materialIndex);

        // Warcraft III emits into a cone of `latitude` degrees about the node axis; StarCraft II
        // spreads by two independent angles. A cone is the symmetric case of that.
        float spread = Math.Clamp(e.Latitude, 0, 180);
        FloatAnim(b, OffEmitSpeed, e.Speed * scale, nextAnimId());
        FloatAnim(b, OffEmitSpeedRandom, e.Speed * scale * Math.Abs(e.Variation), nextAnimId());
        FloatAnim(b, OffEmitSpreadX, spread, nextAnimId());
        FloatAnim(b, OffEmitSpreadY, spread, nextAnimId());
        FloatAnim(b, OffLifespan, MathF.Max(e.Life, 0.001f), nextAnimId());
        FloatAnim(b, OffEmitRate, MathF.Max(restEmitRate, 0), emitRateAnimId);

        // Warcraft III's gravity is a downward acceleration; StarCraft II's field is signed the
        // other way, which is why Blizzard's own emitters store it negative.
        WriteF32(b, OffGravity, -e.Gravity * scale);

        // A plane emitter's width and length become the emission box. Zero means a point, which is
        // what the great majority of Warcraft III emitters are. Width and length swap because the
        // exporter turns the model -90 degrees about Z on the way out (see M3Exporter.ToSc2) while
        // the emitter's bone frame stays world-aligned, so what lay along the model's X now lies
        // along its -Y.
        WriteI32(b, OffEmitShape, e.Width > 0 || e.Length > 0 ? 1 : 0);
        Vector3Anim(b, OffEmitShapeSize, new Vector3(e.Length * scale, e.Width * scale, 0), nextAnimId());

        // `size` holds the three-stage scale ramp in one vector: start, middle, end.
        Vector3Anim(b, OffSize, new Vector3(e.StartScale * scale, e.MiddleScale * scale, e.EndScale * scale),
                    nextAnimId());
        WriteF32(b, OffSizeAnimMid, Math.Clamp(e.MiddleTime, 0f, 1f));
        WriteF32(b, OffColorAnimMid, Math.Clamp(e.MiddleTime, 0f, 1f));
        WriteF32(b, OffAlphaAnimMid, Math.Clamp(e.MiddleTime, 0f, 1f));

        ColorAnim(b, OffColorInit, e.StartColor, e.StartAlpha, nextAnimId());
        ColorAnim(b, OffColorMid, e.MiddleColor, e.MiddleAlpha, nextAnimId());
        ColorAnim(b, OffColorEnd, e.EndColor, e.EndAlpha, nextAnimId());

        WriteI16(b, OffFlipbookCols, (short)Math.Clamp(e.Columns, 1, short.MaxValue));
        WriteI16(b, OffFlipbookRows, (short)Math.Clamp(e.Rows, 1, short.MaxValue));

        // A live cap so a high emission rate cannot allocate without bound: rate x lifespan is the
        // steady-state population, which is what Blizzard's own emitters store here.
        int emitMax = (int)Math.Clamp(MathF.Ceiling(e.EmissionRate * MathF.Max(e.Life, 0.001f)) + 1, 1, 10000);
        WriteI32(b, OffEmitMax, emitMax);

        // 0 is the plain camera-facing billboard — the only type every Warcraft III head particle
        // maps onto. Tail and Both would need SC2's stretched types, which carry their own geometry
        // rules, so they are deliberately not claimed here.
        WriteI32(b, OffParticleType, 0);

        WriteU32(b, OffFlags, BuildFlags(e));
        return b;
    }

    /// <summary>
    /// <c>PAR_.flags</c>. Only bits with a Warcraft III meaning are set; the rest stay at the
    /// default. <c>0x1</c> keeps the system emitting rather than waiting to be triggered, and
    /// <c>0x8</c> is the "sort by distance" bit additive glows want.
    /// </summary>
    private static uint BuildFlags(MdxParticleEmitter2 e)
    {
        uint flags = 0x1;                                  // enabled
        if (e.ModelSpace) flags |= 0x40;                   // simulate in the emitter's local space
        if (e.Blend is MdxParticleBlend.Add) flags |= 0x8;  // sort far-to-near, as additive wants
        return flags;
    }

    // ---- primitive writers ---------------------------------------------------------------------
    // An animation reference is header{ interpolation(u16), flags(u16), id(u32) } then default,
    // then null, then an int32 that Blizzard leaves at -1 on static values.

    private static void AnimHeader(byte[] b, int at, uint id, ushort interpolation = 1)
    {
        WriteU16(b, at, interpolation);
        WriteU16(b, at + 2, 0);
        WriteU32(b, at + 4, id);
    }

    private static void FloatAnim(byte[] b, int at, float value, uint id)
    {
        AnimHeader(b, at, id);
        WriteF32(b, at + 8, value);
        WriteF32(b, at + 12, 0f);
        WriteI32(b, at + 16, -1);
    }

    private static void Vector3Anim(byte[] b, int at, Vector3 value, uint id)
    {
        AnimHeader(b, at, id);
        WriteF32(b, at + 8, value.X); WriteF32(b, at + 12, value.Y); WriteF32(b, at + 16, value.Z);
        WriteF32(b, at + 20, 0f); WriteF32(b, at + 24, 0f); WriteF32(b, at + 28, 0f);
        WriteI32(b, at + 32, -1);
    }

    /// <summary>A colour anim ref. m3 colours are stored BGRA, not RGBA.</summary>
    private static void ColorAnim(byte[] b, int at, Vector3 rgb, byte alpha, uint id)
    {
        AnimHeader(b, at, id);
        b[at + 8] = Channel(rgb.Z); b[at + 9] = Channel(rgb.Y);
        b[at + 10] = Channel(rgb.X); b[at + 11] = alpha;
        WriteU32(b, at + 12, 0);
        WriteI32(b, at + 16, -1);

        static byte Channel(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);
    }

    private static void WriteU16(byte[] b, int at, ushort v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
    private static void WriteI16(byte[] b, int at, short v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
    private static void WriteU32(byte[] b, int at, uint v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
    private static void WriteI32(byte[] b, int at, int v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
    private static void WriteF32(byte[] b, int at, float v) => BitConverter.TryWriteBytes(b.AsSpan(at), v);
}
