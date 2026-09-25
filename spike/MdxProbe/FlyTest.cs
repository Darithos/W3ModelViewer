using System.Numerics;
using Wc3ModelViewer.Core.Formats;

namespace MdxProbe;

/// <summary>
/// <c>--fly [units/s]</c> for <c>--export1</c>: gives the model's root node a translation track that
/// flies it in a wide circle over each sequence, so a world-space trail ribbon has something to
/// extrude along.
/// </summary>
/// <remarks>
/// A StarCraft II ribbon lays geometry only where its points have moved, and our trail emitter is
/// world-space with <c>speed</c> 0 by design — the same choice Blizzard make on 124 of 235 of their
/// own missile ribbons — so the motion has to come from the model. Nothing the user can open shows
/// that: the editor's terrain viewport does not play a doodad's animation, and the Archive Browser
/// previewer orbits the camera around a model that never leaves the origin. Three recordings in a
/// row were read as "the trail is faint or missing" when the trail simply had nowhere to go.
///
/// This puts the motion inside the model, so the previewer alone answers the question. It is a
/// diagnostic: a real missile is flown by its actor, and a real export must not carry this.
/// </remarks>
internal static class FlyTest
{
    /// <summary>Warcraft III units per second the diagnostic flies at; the missile speed the trail is measured at.</summary>
    public const float DefaultSpeed = 900f;

    /// <summary>
    /// Returns a copy of <paramref name="model"/> whose root node carries a circular translation
    /// track, or the model unchanged when it has no node to fly. The circle's radius is chosen so
    /// one lap takes a couple of seconds at <paramref name="unitsPerSecond"/> — long enough for the
    /// ribbon to reach full length, tight enough to stay in the previewer's frame.
    /// </summary>
    public static MdxModel Fly(MdxModel model, float unitsPerSecond, out string log)
    {
        if (model.Nodes.Count == 0)
        {
            log = "no nodes to fly";
            return model;
        }
        // The root is the node every emitter ultimately hangs from: the first without a parent.
        int root = model.Nodes.FindIndex(n => n.ParentId < 0);
        if (root < 0) root = 0;

        int end = model.Sequences.Count > 0 ? model.Sequences.Max(s => s.IntervalEnd) : 3000;
        int start = model.Sequences.Count > 0 ? model.Sequences.Min(s => s.IntervalStart) : 0;
        float seconds = MathF.Max(0.5f, (end - start) / 1000f);
        // One full lap per second of flight keeps the head inside the previewer's frame; the radius
        // follows from the speed so the trail is laid at the rate its strip was baked for.
        float lapSeconds = MathF.Min(2f, seconds);
        float radius = unitsPerSecond * lapSeconds / (2 * MathF.PI);

        const int steps = 64;
        var times = new int[steps + 1];
        var values = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = start + (end - start) * (i / (float)steps);
            times[i] = (int)MathF.Round(t);
            float ang = 2 * MathF.PI * ((t - start) / 1000f) / lapSeconds;
            values[i] = new Vector3(radius * MathF.Cos(ang) - radius, radius * MathF.Sin(ang), 0);
        }

        var old = model.Nodes[root];
        model.Nodes[root] = new MdxNode
        {
            Name = old.Name,
            ObjectId = old.ObjectId,
            ParentId = old.ParentId,
            Flags = old.Flags,
            Kind = old.Kind,
            Translation = new MdxTrack<Vector3>
            {
                Tag = "KGTR",
                Interpolation = MdxInterpolation.Linear,
                GlobalSequenceId = -1,
                Times = times,
                Values = values,
            },
            Rotation = old.Rotation,
            Scale = old.Scale,
            GeosetId = old.GeosetId,
            GeosetAnimId = old.GeosetAnimId,
            AttachmentId = old.AttachmentId,
            AttachmentPath = old.AttachmentPath,
            Pivot = old.Pivot,
        };
        log = $"DIAGNOSTIC --fly: '{old.Name}' flies a {radius:0} unit circle, one lap per {lapSeconds:0.0} s "
            + $"({unitsPerSecond:0} units/s), {steps + 1} keys over {start}..{end} ms. Do not ship this model.";
        return model;
    }
}
