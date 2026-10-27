namespace Splicia;

enum TransitionKind { None, Crossfade, FadeThroughBlack }

/// <summary>
/// How two touching clips meet. <see cref="Seconds"/> is the whole transition: a crossfade runs that long centred on the
/// cut (using a little of each clip's video beyond the cut, as editors do); a fade goes out to black over the first half
/// and comes back in over the second.
/// </summary>
sealed record Transition(TransitionKind Kind, double Seconds)
{
    public static readonly double[] Lengths = { 0.25, 0.5, 1, 2 };
    public static readonly Transition Cut = new(TransitionKind.None, 0.5);

    public static string Name(TransitionKind k) => k switch
    {
        TransitionKind.Crossfade => "Crossfade",
        TransitionKind.FadeThroughBlack => "Fade out, fade in",
        _ => "None (straight cut)",
    };

    public override string ToString() => Kind == TransitionKind.None ? "No transition" : $"{Name(Kind)}, {Seconds:0.##} s";
}

/// <summary>
/// A stretch of the video, [<paramref name="In"/>, <paramref name="Out"/>) in the file's seconds, placed on the video
/// track at <paramref name="Start"/>, with its own sound. <paramref name="Next"/> is how it meets the clip after it when
/// that one touches it; <paramref name="FadeIn"/> / <paramref name="FadeOut"/> fade it from / to black (and its sound
/// from / to silence) at its own edges: one-sided transitions that move with the clip, as in Premiere and FilmCraft.
/// </summary>
sealed record Clip(int Id, double In, double Out, double Start, Transition Next, double FadeIn = 0, double FadeOut = 0)
{
    public double Length => Out - In;
    public double End => Start + Length;
}

/// <summary>
/// A stretch [<paramref name="In"/>, <paramref name="Out"/>) of a sound file (<paramref name="SourceLength"/> long) on
/// the audio track at <paramref name="Start"/>, faded in / out over <paramref name="FadeIn"/> / <paramref name="FadeOut"/>.
/// </summary>
sealed record AudioClip(int Id, string Path, double In, double Out, double Start, double SourceLength, double FadeIn = 0, double FadeOut = 0)
{
    public double Length => Out - In;
    public double End => Start + Length;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The edit: a video track (clips of the video, each with its own sound) and an audio track (clips of sound files), each
/// in time order and never overlapping itself, maybe with gaps (black and silent, in the app and the export). The
/// timeline runs from 0 to the end of whichever track ends last. Positions stay on the frame grid. Immutable: every edit
/// returns a new timeline, so undo is a list of timelines.
/// </summary>
sealed class Timeline
{
    public readonly IReadOnlyList<Clip> Clips;
    public readonly IReadOnlyList<AudioClip> Music;
    /// <summary>Length of a frame (positions are multiples of it) and of the whole video file.</summary>
    public readonly double Frame, SourceLength;

    /// <summary>Shortest audio clip trimming may leave.</summary>
    public const double MinAudio = 0.1;

    public Timeline(IEnumerable<Clip> clips, IEnumerable<AudioClip> music, double frame, double sourceLength)
    {
        Clips = clips.OrderBy(c => c.Start).ToList();
        Music = music.OrderBy(c => c.Start).ToList();
        Frame = frame;
        SourceLength = sourceLength;
    }

    /// <summary>The whole video as one clip, ending on the frame grid (containers' durations often don't).</summary>
    public static Timeline Whole(MediaInfo info)
    {
        double fd = info.FrameDuration, end = Math.Max(fd, Math.Round(info.Duration / fd) * fd);
        return new(new[] { new Clip(1, 0, end, 0, Transition.Cut) }, Array.Empty<AudioClip>(), fd, info.Duration);
    }

    /// <summary>The same clips and sounds as <paramref name="other"/> (nothing changed).</summary>
    public bool SameAs(Timeline other) => Clips.SequenceEqual(other.Clips) && Music.SequenceEqual(other.Music);

    /// <summary>Where the video track ends.</summary>
    public double VideoEnd => Clips.Count == 0 ? 0 : Clips[^1].End;

    /// <summary>Where the timeline ends: the later of the two tracks.</summary>
    public double End => Math.Max(VideoEnd, Music.Count == 0 ? 0 : Music.Max(m => m.End));

    Timeline With(IEnumerable<Clip> clips) => new(clips, Music, Frame, SourceLength);
    Timeline WithMusic(IEnumerable<AudioClip> music) => new(Clips, music, Frame, SourceLength);

    /// <summary>A time on the frame grid.</summary>
    public double Snap(double t) => Math.Max(0, Math.Round(t / Frame) * Frame);

    public Clip? Find(int id) => Clips.FirstOrDefault(c => c.Id == id);
    public AudioClip? FindMusic(int id) => Music.FirstOrDefault(c => c.Id == id);

    /// <summary>The clip showing at timeline time <paramref name="t"/>, or null in a gap or past the end.</summary>
    public Clip? At(double t) => Clips.FirstOrDefault(c => t >= c.Start - 1e-6 && t < c.End - 1e-6);

    /// <summary>Whether <paramref name="b"/> starts where <paramref name="a"/> ends (within half a frame).</summary>
    public bool Touch(Clip a, Clip b) => Math.Abs(a.End - b.Start) < Frame / 2;

    /// <summary>Touching clips that continue the same footage (split and left alone): nothing to join.</summary>
    public bool Continuous(Clip a, Clip b) => Touch(a, b) && Math.Abs(a.Out - b.In) < Frame / 2;

    /// <summary>The clip after <paramref name="c"/> if it touches it.</summary>
    public Clip? Following(Clip c)
    {
        int i = Clips.ToList().FindIndex(x => x.Id == c.Id);
        return i >= 0 && i + 1 < Clips.Count && Touch(c, Clips[i + 1]) ? Clips[i + 1] : null;
    }

    int NextId => Math.Max(Clips.Count == 0 ? 0 : Clips.Max(c => c.Id), Music.Count == 0 ? 0 : Music.Max(c => c.Id)) + 1;

    public bool HasGaps => Clips.Count > 0 && (Clips[0].Start > Frame / 2 || Clips.Zip(Clips.Skip(1)).Any(p => !Touch(p.First, p.Second)));

    /// <summary>A file time inside clip <paramref name="c"/> → timeline time.</summary>
    public static double ToTimeline(Clip c, double source) => c.Start + (source - c.In);

    // ───────────────────────── video track ─────────────────────────

    /// <summary>
    /// Splits the clip under <paramref name="t"/> there: the frame at <paramref name="t"/> starts the second clip, which
    /// keeps the fade out and the transition after; the first keeps the fade in. Nothing changes at a clip's edge or in
    /// a gap.
    /// </summary>
    public Timeline Split(double t, out int secondId)
    {
        secondId = -1;
        var c = At(t);
        if (c == null) return this;
        double offset = Snap(t - c.Start);
        if (offset < Frame / 2 || offset > c.Length - Frame / 2) return this;
        secondId = NextId;
        var a = c with { Out = c.In + offset, Next = Transition.Cut, FadeOut = 0, FadeIn = Math.Min(c.FadeIn, offset) };
        var b = new Clip(secondId, c.In + offset, c.Out, c.Start + offset, c.Next, 0, Math.Min(c.FadeOut, c.Length - offset));
        return With(Clips.Where(x => x.Id != c.Id).Append(a).Append(b));
    }

    /// <summary>Removes a clip, leaving a gap.</summary>
    public Timeline Delete(int id) => With(Clips.Where(c => c.Id != id));

    /// <summary>Removes a clip and closes the gap: the clips after it move back by its length.</summary>
    public Timeline RippleDelete(int id)
    {
        var c = Find(id);
        if (c == null) return this;
        return With(Clips.Where(x => x.Id != id).Select(x => x.Start >= c.End - Frame / 2 ? x with { Start = Snap(x.Start - c.Length) } : x));
    }

    /// <summary>
    /// Moves a clip so it starts at <paramref name="start"/>. Where it fits, it simply goes there (its old place becomes
    /// a gap). Dropped onto other clips it is rearranged instead: taken out (the clips after it close up), then inserted
    /// before or after the clip under <paramref name="pointer"/> (whichever half the pointer is on), the clips after that
    /// point moving along to make room.
    /// </summary>
    public Timeline Move(int id, double start, double pointer)
    {
        var c = Find(id);
        if (c == null) return this;
        start = Snap(start);
        var others = Clips.Where(x => x.Id != id).ToList();
        var placed = c with { Start = start };
        if (!others.Any(o => o.Start < placed.End - Frame / 2 && placed.Start < o.End - Frame / 2)) return With(others.Append(placed));

        var target = others.FirstOrDefault(o => pointer >= o.Start && pointer < o.End)
                     ?? others.Where(o => o.Start < placed.End && placed.Start < o.End).MinBy(o => Math.Abs((o.Start + o.End) / 2 - pointer))!;
        bool before = pointer < (target.Start + target.End) / 2;
        var taken = With(others.Select(x => x.Start >= c.End - Frame / 2 ? x with { Start = Snap(x.Start - c.Length) } : x));
        var t = taken.Find(target.Id)!;
        double at = before ? t.Start : t.End;
        return With(taken.Clips.Select(x => x.Start >= at - Frame / 2 ? x with { Start = Snap(x.Start + c.Length) } : x)
            .Append(c with { Start = at }));
    }

    /// <summary>Packs the video clips together from the start, in order.</summary>
    public Timeline CloseGaps()
    {
        double t = 0;
        var list = new List<Clip>();
        foreach (var c in Clips) { list.Add(c with { Start = t }); t = Snap(t + c.Length); }
        return With(list);
    }

    public Timeline SetTransition(int id, Transition t) => With(Clips.Select(c => c.Id == id ? c with { Next = t } : c));

    public Timeline SetAllTransitions(Transition t) => With(Clips.Select(c => c with { Next = t }));

    /// <summary>Sets a video clip's fades (null leaves one as it is), each at most the clip's length less the other.</summary>
    public Timeline SetFades(int id, double? fadeIn, double? fadeOut) => With(Clips.Select(c =>
    {
        if (c.Id != id) return c;
        double i = Math.Clamp(fadeIn ?? c.FadeIn, 0, c.Length), o = Math.Clamp(fadeOut ?? c.FadeOut, 0, c.Length - i);
        return c with { FadeIn = i, FadeOut = o };
    }));

    // ───────────────────────── audio track ─────────────────────────

    /// <summary>Adds a whole sound file at <paramref name="at"/>, or the first free place after it on the audio track.</summary>
    public Timeline AddMusic(string path, double length, double at, out int id)
    {
        id = NextId;
        var clip = new AudioClip(id, path, 0, length, Snap(at), length);
        return WithMusic(Music.Append(clip with { Start = FreePlace(clip, clip.Start, -1) }));
    }

    public Timeline DeleteMusic(int id) => WithMusic(Music.Where(m => m.Id != id));

    /// <summary>
    /// Moves an audio clip to start at <paramref name="start"/>; where that would overlap another one, it goes to the
    /// nearest free place either side (clips on the track never overlap).
    /// </summary>
    public Timeline MoveMusic(int id, double start)
    {
        var c = FindMusic(id);
        if (c == null) return this;
        return WithMusic(Music.Select(m => m.Id == id ? m with { Start = FreePlace(m, Snap(start), id) } : m));
    }

    /// <summary>The free start nearest <paramref name="want"/> for <paramref name="c"/> among the other audio clips.</summary>
    double FreePlace(AudioClip c, double want, int self)
    {
        var others = Music.Where(m => m.Id != self && m.Id != c.Id).OrderBy(m => m.Start).ToList();
        bool Fits(double s) => s >= -1e-9 && others.All(o => s + c.Length <= o.Start + 1e-6 || s >= o.End - 1e-6);
        if (Fits(want)) return want;
        var candidates = new List<double> { 0 };
        foreach (var o in others) { candidates.Add(o.End); candidates.Add(Snap(o.Start - c.Length)); }
        return candidates.Where(Fits).OrderBy(s => Math.Abs(s - want)).DefaultIfEmpty(End).First();
    }

    /// <summary>
    /// Trims an audio clip by moving its start edge to timeline time <paramref name="edge"/> (the sound under the rest
    /// stays where it is), within the file and the clip before it, keeping at least <see cref="MinAudio"/>.
    /// </summary>
    public Timeline TrimMusicStart(int id, double edge)
    {
        var c = FindMusic(id);
        if (c == null) return this;
        double prevEnd = Music.Where(m => m.Id != id && m.End <= c.Start + 1e-6).Select(m => m.End).DefaultIfEmpty(0).Max();
        double lo = Math.Max(prevEnd, c.Start - c.In), hi = c.End - MinAudio;
        double start = Math.Clamp(Snap(edge), lo, Math.Max(lo, hi));
        var t = c with { In = c.In + (start - c.Start), Start = start };
        return WithMusic(Music.Select(m => m.Id == id ? Fit(t) : m));
    }

    /// <summary>Trims an audio clip by moving its end edge to <paramref name="edge"/>, within the file and the clip after it.</summary>
    public Timeline TrimMusicEnd(int id, double edge)
    {
        var c = FindMusic(id);
        if (c == null) return this;
        double nextStart = Music.Where(m => m.Id != id && m.Start >= c.End - 1e-6).Select(m => m.Start).DefaultIfEmpty(double.MaxValue).Min();
        double lo = c.Start + MinAudio, hi = Math.Min(nextStart, c.Start + (c.SourceLength - c.In));
        double end = Math.Clamp(Snap(edge), lo, Math.Max(lo, hi));
        var t = c with { Out = c.In + (end - c.Start) };
        return WithMusic(Music.Select(m => m.Id == id ? Fit(t) : m));
    }

    /// <summary>Sets an audio clip's fades (null leaves one as it is), each at most the clip's length less the other.</summary>
    public Timeline SetMusicFades(int id, double? fadeIn, double? fadeOut) =>
        WithMusic(Music.Select(m => m.Id == id ? Fit(m with { FadeIn = fadeIn ?? m.FadeIn, FadeOut = fadeOut ?? m.FadeOut }) : m));

    /// <summary>Fades held to the clip's length.</summary>
    static AudioClip Fit(AudioClip m)
    {
        double i = Math.Clamp(m.FadeIn, 0, m.Length), o = Math.Clamp(m.FadeOut, 0, m.Length - i);
        return m with { FadeIn = i, FadeOut = o };
    }
}

/// <summary>
/// The timeline as the player and the export play it: the video clips in order with their transitions and fades worked
/// out, and the audio clips. A part plays the video file from <see cref="Part.From"/> − <see cref="Part.HandleIn"/> to
/// <see cref="Part.To"/> + <see cref="Part.HandleOut"/> at timeline time <see cref="Part.Start"/> − HandleIn on: a
/// crossfade of length 2h runs from h before the cut to h after it, each side showing h of its video beyond the cut (its
/// "handle"). Fades darken a part's ends (and fade its sound). Wherever sound starts or stops abruptly (a straight cut, a
/// gap, an audio clip's ends) it fades over a few milliseconds (<see cref="Declick"/>), as audio editors do, so the
/// waveform doesn't jump and click. Where no part plays: black (and the audio track, or silence).
/// </summary>
sealed class EditList
{
    /// <summary>Length of the sound fades where it starts or stops abruptly.</summary>
    public const double Declick = 0.005;

    public sealed record Part(double From, double To, double Start, double HandleIn, double HandleOut, double FadeIn, double FadeOut,
        bool CutIn, bool CutOut)
    {
        public double Length => To - From;
        public double End => Start + Length;
        /// <summary>When the part is playing, including its handles.</summary>
        public double PlayStart => Start - HandleIn;
        public double PlayEnd => End + HandleOut;
        /// <summary>The declick fade at either end: never more than half the part.</summary>
        public double DeclickLength => Math.Min(Declick, Length / 2);
    }

    /// <summary>A sound file's [From, To) at timeline time Start, with its fades (and declicks at both ends).</summary>
    public sealed record Sound(string Path, double From, double To, double Start, double FadeIn, double FadeOut)
    {
        public double Length => To - From;
        public double End => Start + Length;
        public double DeclickLength => Math.Min(Declick, Length / 2);
    }

    public readonly IReadOnlyList<Part> Parts;
    public readonly IReadOnlyList<Sound> Music;
    /// <summary>The timeline's length: the end of whichever track ends last.</summary>
    public readonly double Length;
    /// <summary>A frame's length: gaps shorter than half of one are joins.</summary>
    public readonly double Frame;

    public static readonly EditList Empty = new(new List<Part>(), new List<Sound>(), 0, 1 / 30.0);

    EditList(List<Part> parts, List<Sound> music, double length, double frame)
    {
        Parts = parts;
        Music = music;
        Length = length;
        Frame = frame;
    }

    /// <summary>The timeline's clips with their transitions and fades, each held to what the clips and the file allow.</summary>
    public static EditList Of(Timeline tl)
    {
        var clips = tl.Clips;
        double fd = tl.Frame;
        var halves = new double[clips.Count]; // crossfade half-length after clip i
        var dips = new double[clips.Count];   // fade-through-black half-length after clip i
        var cut = new bool[clips.Count];      // straight cut (or nothing joined) after clip i
        for (int i = 0; i < clips.Count; i++)
        {
            var a = clips[i];
            cut[i] = true;
            if (i + 1 >= clips.Count || !tl.Touch(a, clips[i + 1])) continue;
            var b = clips[i + 1];
            var t = a.Next;
            if (t.Kind == TransitionKind.Crossfade)
            {
                // Each side shows h beyond the cut: what the file has there, and at most half of either clip.
                double h = Math.Min(Math.Min(t.Seconds / 2, Math.Min(tl.SourceLength - a.Out, b.In)), Math.Min(a.Length, b.Length) / 2);
                h = Math.Floor(h / fd + 1e-6) * fd;
                if (h > 0) { halves[i] = h; cut[i] = false; continue; }
            }
            else if (t.Kind == TransitionKind.FadeThroughBlack)
            {
                dips[i] = Math.Min(t.Seconds / 2, Math.Min(a.Length, b.Length) / 2);
                cut[i] = false;
                continue;
            }
            if (tl.Continuous(a, b)) cut[i] = false; // the same footage carries on: nothing to declick
        }
        var parts = new List<Part>();
        for (int i = 0; i < clips.Count; i++)
        {
            var c = clips[i];
            // A clip's own fade where it crossfades into its neighbour gives way to the crossfade (one transition per edge).
            double fadeIn = Math.Max(i > 0 ? dips[i - 1] : 0, i > 0 && halves[i - 1] > 0 ? 0 : c.FadeIn);
            double fadeOut = Math.Max(dips[i], halves[i] > 0 ? 0 : c.FadeOut);
            parts.Add(new Part(c.In, c.Out, c.Start,
                HandleIn: i > 0 ? halves[i - 1] : 0, HandleOut: halves[i],
                FadeIn: Math.Min(fadeIn, c.Length), FadeOut: Math.Min(fadeOut, c.Length),
                CutIn: (i == 0 || cut[i - 1]) && fadeIn <= 0, CutOut: cut[i] && fadeOut <= 0));
        }
        var music = tl.Music.Select(m => new Sound(m.Path, m.In, m.Out, m.Start, m.FadeIn, m.FadeOut)).ToList();
        return new EditList(parts, music, tl.End, fd);
    }

    /// <summary>A plain stretch of the file as one part from timeline time 0.</summary>
    public static EditList Single(double from, double to) =>
        new(new List<Part> { new(from, to, 0, 0, 0, 0, 0, false, false) }, new List<Sound>(), to - from, 1 / 30.0);

    /// <summary>
    /// The first part still playing at timeline time <paramref name="t"/> (the outgoing one inside a crossfade; in a gap,
    /// the next one), or the number of parts when none is left (only black, and the audio track, to the end).
    /// </summary>
    public int Locate(double t)
    {
        for (int i = 0; i < Parts.Count; i++)
            if (Parts[i].PlayEnd > t + 1e-6) return i;
        return Parts.Count;
    }
}
