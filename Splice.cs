namespace Splicia;

enum TransitionKind { None, Crossfade, FadeThroughBlack }

/// <summary>
/// How the two ends of a splice are joined. <see cref="Seconds"/> is the whole transition: a crossfade overlaps the ends
/// by that much; a fade goes out to black over the first half and comes back in over the second.
/// </summary>
sealed record Transition(TransitionKind Kind, double Seconds)
{
    public static readonly double[] Lengths = { 0.25, 0.5, 1, 2 };

    public static string Name(TransitionKind k) => k switch
    {
        TransitionKind.Crossfade => "Crossfade",
        TransitionKind.FadeThroughBlack => "Fade out, fade in",
        _ => "None (straight cut)",
    };

    public override string ToString() => Kind == TransitionKind.None ? "No transition" : $"{Name(Kind)}, {Seconds:0.##} s";
}

/// <summary>A spliced-out part of the video: [<paramref name="From"/>, <paramref name="To"/>) is removed and the ends either side are joined.</summary>
sealed record Splice(double From, double To, Transition Transition);

/// <summary>
/// Where two kept parts meet, with the transition times that fit: a crossfade overlaps the parts by
/// <paramref name="Overlap"/>; a fade darkens the end of the first by <paramref name="FadeOut"/> and lightens the start of
/// the second by <paramref name="FadeIn"/>. Each is at most half of the parts it touches, so joins never collide, and a
/// crossfade is a whole number of frames, so the result's length is too.
/// </summary>
readonly record struct Join(TransitionKind Kind, double Overlap, double FadeOut, double FadeIn)
{
    public static Join For(Transition t, double before, double after, double frame) => t.Kind switch
    {
        TransitionKind.Crossfade => new(t.Kind, Math.Floor(Math.Min(t.Seconds, Math.Min(before, after) / 2) / frame + 1e-6) * frame, 0, 0),
        TransitionKind.FadeThroughBlack => new(t.Kind, 0, Math.Min(t.Seconds / 2, before / 2), Math.Min(t.Seconds / 2, after / 2)),
        _ => new(TransitionKind.None, 0, 0, 0),
    };
}
