using System.Numerics;

namespace Wc3ModelViewer.Core.Convert;

/// <summary>
/// Drops baked animation keys that the game's own interpolation would reproduce anyway.
/// </summary>
/// <remarks>
/// <para>
/// The exporter samples every track on a fixed grid (33 ms at the default rate), so a 3 s walk
/// carries ~90 keys per bone per property whether the artist keyed three poses or thirty. Nine
/// tenths of a typical export was those keys and their timestamps: five murlocs with different
/// meshes all weighed 12.65 MB, against 1.8 MB for a hand-converted one with the same sixteen
/// animations. StarCraft II interpolates between whatever keys a track holds, so a sample lying on
/// the line between its neighbours costs bytes and changes nothing.
/// </para>
/// <para>
/// Ramer–Douglas–Peucker over each track: the first and last samples are kept; a span is accepted
/// once every sample inside it lies within the tolerance of the interpolation between its ends,
/// otherwise the sample furthest off is kept and both halves are tried again. The bound therefore
/// holds by construction — no dropped sample ever sits further than the tolerance from what the
/// kept keys interpolate to at its own time — so the reduction needs no separate verification
/// pass, and the deviation it reports is measured, not estimated.
/// </para>
/// <para>
/// Rotations are tested against both slerp and normalised lerp and charged the worse of the two:
/// nothing in the file says which the engine uses, and the two agree only at the ends and the
/// midpoint of an arc. Neither is taken along the shorter arc, because a naive engine would not:
/// a span whose end keys sit in opposite hemispheres fails the test and gets split until it does
/// not. Flags are held rather than interpolated, so only the keys where the value changes survive.
/// </para>
/// </remarks>
public static class KeyReducer
{
    /// <summary>Running totals across every track one export reduced.</summary>
    public sealed class Stats
    {
        public long KeysIn, KeysOut;
        /// <summary>Largest deviation any dropped rotation key has from the kept curve, degrees.</summary>
        public float MaxRotationDeg;
        /// <summary>Largest deviation any dropped vector key has from the kept curve, in its own unit.</summary>
        public float MaxVector;

        public long Dropped => KeysIn - KeysOut;
    }

    public static (int[] Frames, Vector3[] Vals) ReduceVec3(int[] frames, Vector3[] vals, float tolerance, Stats stats)
    {
        var kept = Kept(frames, vals, Vector3.Lerp, static (a, b) => (a - b).Length(), tolerance, out float worst);
        stats.MaxVector = MathF.Max(stats.MaxVector, worst);
        return Take(frames, vals, kept, stats);
    }

    public static (int[] Frames, Quaternion[] Vals) ReduceQuat(int[] frames, Quaternion[] vals, float toleranceDeg, Stats stats)
    {
        var kept = Kept(frames, vals, static (a, b, t) => (NaiveNlerp(a, b, t), NaiveSlerp(a, b, t)),
                        static (q, pair) => MathF.Max(AngleDeg(q, pair.Item1), AngleDeg(q, pair.Item2)),
                        toleranceDeg, out float worst);
        stats.MaxRotationDeg = MathF.Max(stats.MaxRotationDeg, worst);
        return Take(frames, vals, kept, stats);
    }

    public static (int[] Frames, float[] Vals) ReduceFloat(int[] frames, float[] vals, float tolerance, Stats stats)
    {
        var kept = Kept(frames, vals, static (a, b, t) => a + (b - a) * t, static (a, b) => MathF.Abs(a - b), tolerance, out _);
        return Take(frames, vals, kept, stats);
    }

    /// <summary>BGRA packed colours, each channel interpolated on its own; the tolerance is in 8-bit steps.</summary>
    public static (int[] Frames, uint[] Vals) ReduceColor(int[] frames, uint[] vals, float tolerance, Stats stats)
    {
        var kept = Kept(frames, vals, static (a, b, t) => (
                            Lerp8(a, b, t, 0), Lerp8(a, b, t, 8), Lerp8(a, b, t, 16), Lerp8(a, b, t, 24)),
                        static (c, l) => Math.Max(Math.Max(Math.Abs(((c >> 0) & 255) - l.Item1), Math.Abs(((c >> 8) & 255) - l.Item2)),
                                                  Math.Max(Math.Abs(((c >> 16) & 255) - l.Item3), Math.Abs(((c >> 24) & 255) - l.Item4))),
                        tolerance, out _);
        return Take(frames, vals, kept, stats);
    }

    /// <summary>Held values: a key survives only where the value differs from the previous one.</summary>
    public static (int[] Frames, uint[] Vals) ReduceFlags(int[] frames, uint[] vals, Stats stats)
    {
        var kept = new List<int>(4);
        for (int i = 0; i < vals.Length; i++)
            if (i == 0 || i == vals.Length - 1 || vals[i] != vals[i - 1]) kept.Add(i);
        return Take(frames, vals, kept, stats);
    }

    // ---------------------------------------------------------------- core

    /// <summary>
    /// Indices to keep. <paramref name="interp"/> evaluates the kept curve between two keys at a
    /// 0..1 fraction; <paramref name="error"/> measures how far a sample is from that. The result
    /// always holds the first and last index, in order.
    /// </summary>
    private static List<int> Kept<T, TInterp>(int[] frames, T[] vals, Func<T, T, float, TInterp> interp,
                                              Func<T, TInterp, float> error, float tolerance, out float worstDropped)
    {
        int n = vals.Length;
        worstDropped = 0;
        if (n <= 2) return Enumerable.Range(0, n).ToList();

        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var spans = new Stack<(int Lo, int Hi)>();
        spans.Push((0, n - 1));
        while (spans.Count > 0)
        {
            var (lo, hi) = spans.Pop();
            if (hi - lo < 2) continue;

            int worst = -1;
            float worstErr = -1;
            float span = frames[hi] - frames[lo];
            for (int i = lo + 1; i < hi; i++)
            {
                float f = span > 0 ? (frames[i] - frames[lo]) / span : 0;
                float e = error(vals[i], interp(vals[lo], vals[hi], f));
                if (e > worstErr) { worstErr = e; worst = i; }
            }

            if (worstErr > tolerance)
            {
                keep[worst] = true;
                spans.Push((lo, worst));
                spans.Push((worst, hi));
            }
            else if (worstErr > worstDropped) worstDropped = worstErr;
        }

        var kept = new List<int>();
        for (int i = 0; i < n; i++) if (keep[i]) kept.Add(i);
        return kept;
    }

    private static (int[] Frames, T[] Vals) Take<T>(int[] frames, T[] vals, List<int> kept, Stats stats)
    {
        stats.KeysIn += vals.Length;
        stats.KeysOut += kept.Count;
        if (kept.Count == vals.Length) return (frames, vals);
        var f = new int[kept.Count];
        var v = new T[kept.Count];
        for (int i = 0; i < kept.Count; i++) { f[i] = frames[kept[i]]; v[i] = vals[kept[i]]; }
        return (f, v);
    }

    // ---------------------------------------------------------------- rotations

    /// <summary>Angle between two rotations in degrees; a zero-length quaternion counts as a full miss.</summary>
    private static float AngleDeg(Quaternion a, Quaternion b)
    {
        float lb = b.Length();
        if (lb < 1e-6f) return 180f;
        float dot = MathF.Abs(Quaternion.Dot(a, b)) / (a.Length() * lb);
        return 2f * MathF.Acos(MathF.Min(1f, dot)) * (180f / MathF.PI);
    }

    /// <summary>Component-wise lerp then normalise, taken as stored — no flip onto the shorter arc.</summary>
    private static Quaternion NaiveNlerp(Quaternion a, Quaternion b, float t)
    {
        var q = new Quaternion(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
        float len = q.Length();
        return len < 1e-6f ? default : q * (1f / len);
    }

    /// <summary>Great-arc interpolation, taken as stored — no flip onto the shorter arc.</summary>
    private static Quaternion NaiveSlerp(Quaternion a, Quaternion b, float t)
    {
        float dot = Quaternion.Dot(a, b);
        if (dot > 0.9995f) return NaiveNlerp(a, b, t);
        if (dot < -0.9995f) return default;   // antipodal: undefined, count it as a miss
        float theta = MathF.Acos(Math.Clamp(dot, -1f, 1f));
        float sin = MathF.Sin(theta);
        float wa = MathF.Sin((1 - t) * theta) / sin, wb = MathF.Sin(t * theta) / sin;
        return new Quaternion(a.X * wa + b.X * wb, a.Y * wa + b.Y * wb, a.Z * wa + b.Z * wb, a.W * wa + b.W * wb);
    }

    private static int Lerp8(uint a, uint b, float t, int shift)
    {
        int ca = (int)((a >> shift) & 255), cb = (int)((b >> shift) & 255);
        return (int)MathF.Round(ca + (cb - ca) * t);
    }
}
