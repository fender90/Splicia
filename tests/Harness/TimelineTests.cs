using System.Drawing;
using System.Diagnostics;

namespace Splicia;

/// <summary>
/// The timeline: its edits (split, delete, close gaps, move and rearrange, the audio track, fades, how transitions are
/// held) and its export, checked frame by frame on the numbered clip: straight cuts, crossfades centred on the cut,
/// fades, black gaps (also before the first clip and after the last, under sound), clip fades, the audio track mixed in
/// where it belongs, rotation and crop, 4K HDR to HEVC and H.264, a 60 fps clip without sound, many clips (in runs, and
/// the graph in a file), one-frame clips, the back-and-forth loop's short piece, and memory against a plain export.
/// </summary>
static class TimelineTests
{
    const int FW = 160, FH = 90, FS = FW * FH;

    // ───────────────────────── helpers (shared with the other timeline tests) ─────────────────────────

    /// <summary>All frames of a video, decoded small and grey, every one as stored.</summary>
    internal static byte[][] Frames(string file)
    {
        string raw = Path.Combine(H.Out, Path.GetFileNameWithoutExtension(file) + ".gray");
        H.Ffmpeg($"-v error -y -i \"{file}\" -vf scale={FW}:{FH},format=gray -fps_mode passthrough -f rawvideo \"{raw}\"");
        var all = File.ReadAllBytes(raw);
        File.Delete(raw);
        return Enumerable.Range(0, all.Length / FS).Select(i => all.AsSpan(i * FS, FS).ToArray()).ToArray();
    }

    internal static double Diff(byte[] a, byte[] b) { long s = 0; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) s += Math.Abs(a[i] - b[i]); return s / (double)a.Length; }
    internal static double Mean(byte[] a) => a.Average(x => (double)x);

    /// <summary>The source frame (within ±3 of the expected one) that frame <paramref name="f"/> looks most like.</summary>
    internal static int Closest(byte[] f, byte[][] src, int expected) =>
        Enumerable.Range(Math.Max(0, expected - 3), 7).Where(j => j < src.Length).MinBy(j => Diff(f, src[j]));

    /// <summary>Whether <paramref name="f"/> is a mix of source frames <paramref name="a"/> and <paramref name="b"/> rather than either.</summary>
    internal static bool IsMix(byte[] f, byte[][] src, int a, int b, out string detail)
    {
        var mix = src[a].Zip(src[b], (x, y) => (byte)((x + y + 1) / 2)).ToArray();
        double dMix = Diff(f, mix), dA = Diff(f, src[a]), dB = Diff(f, src[b]);
        detail = $"to the mix {dMix:0.0}, to either side {dA:0.0} / {dB:0.0}";
        return dMix < 0.6 * Math.Min(dA, dB);
    }

    internal static double Duration(string file, string stream) =>
        double.TryParse(H.Probe(file, $"-select_streams {stream} -show_entries stream=duration -of csv=p=0"), out double d) ? d : -1;

    static string FrameCount(string file) => H.Probe(file, "-select_streams v -show_entries stream=nb_frames -of csv=p=0").Trim();

    /// <summary>The sound of a file, mono 48 kHz.</summary>
    internal static short[] Sound(string file)
    {
        string raw = Path.Combine(H.Out, Path.GetFileNameWithoutExtension(file) + ".pcm");
        H.Ffmpeg($"-v error -y -i \"{file}\" -vn -ac 1 -ar 48000 -f s16le \"{raw}\"");
        if (!File.Exists(raw)) return Array.Empty<short>();
        var bytes = File.ReadAllBytes(raw);
        File.Delete(raw);
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes).ToArray();
    }

    /// <summary>How strong frequency <paramref name="f"/> Hz is between two times (Goertzel; a full-scale sine ≈ 32767).</summary>
    internal static double Tone(short[] mono, double from, double to, double f)
    {
        int a = Math.Max(0, (int)(from * 48000)), b = Math.Min(mono.Length, (int)(to * 48000));
        if (b - a < 100) return 0;
        double w = 2 * Math.PI * f / 48000, c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = a; i < b; i++) { double s0 = mono[i] + c * s1 - s2; s2 = s1; s1 = s0; }
        double power = s1 * s1 + s2 * s2 - c * s1 * s2;
        return 2 * Math.Sqrt(Math.Max(0, power)) / (b - a);
    }

    internal static double Rms(short[] mono, double from, double to)
    {
        int a = Math.Max(0, (int)(from * 48000)), b = Math.Min(mono.Length, (int)(to * 48000));
        if (b <= a) return 0;
        double s = 0;
        for (int i = a; i < b; i++) s += mono[i] * (double)mono[i];
        return Math.Sqrt(s / (b - a));
    }

    /// <summary>Exports; returns ffmpeg's peak memory (all ffmpeg processes, sampled).</summary>
    internal static long Export(TrimJob job, ExportSettings s)
    {
        long peak = 0;
        bool done = false;
        var watch = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                foreach (var pr in Process.GetProcessesByName("ffmpeg"))
                    try { pr.Refresh(); peak = Math.Max(peak, pr.PeakWorkingSet64); } catch { }
                Thread.Sleep(30);
            }
        }) { IsBackground = true };
        watch.Start();
        var sw = Stopwatch.StartNew();
        try
        {
            Exporter.RunAsync(job, s, s.Format == ExportFormat.Hevc ? Exporter.HardwareHevc.Result : null, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            Volatile.Write(ref done, true);
            watch.Join();
        }
        Console.WriteLine($"  exported {Path.GetFileName(s.OutputPath)} in {sw.Elapsed.TotalSeconds:0.0} s, ffmpeg peak {peak / 1e6:0} MB");
        return peak;
    }

    static bool NoLeftovers() => Directory.GetFileSystemEntries(H.Out, ".splicia-*").Length == 0;

    static bool DtsClean(string file, double fd, out string detail)
    {
        var dts = H.Probe(file, "-select_streams v -show_entries packet=dts_time -of csv=p=0")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => double.Parse(x.TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var steps = dts.Zip(dts.Skip(1), (a, b) => b - a).ToList();
        double min = steps.Count == 0 ? fd : steps.Min(), max = steps.Count == 0 ? fd : steps.Max();
        detail = $"{dts.Count} packets, decode steps {min * 1000:0.##}–{max * 1000:0.##} ms";
        return min > fd * 0.5 && max < fd * 1.5;
    }

    internal static Transition Cut => Transition.Cut;
    internal static Transition Cross(double s) => new(TransitionKind.Crossfade, s);
    internal static Transition Dip(double s) => new(TransitionKind.FadeThroughBlack, s);

    /// <summary>A timeline of the given clips (from, to of the file; packed from 0 unless a start is given), the transition after each.</summary>
    internal static Timeline Packed(MediaInfo info, params (double From, double To, Transition Next)[] clips)
    {
        var list = new List<Clip>();
        double t = 0;
        for (int i = 0; i < clips.Length; i++)
        {
            list.Add(new Clip(i + 1, clips[i].From, clips[i].To, t, clips[i].Next));
            t += clips[i].To - clips[i].From;
        }
        return new Timeline(list, Array.Empty<AudioClip>(), info.FrameDuration, info.Duration);
    }

    internal static Timeline Placed(MediaInfo info, IEnumerable<Clip> clips, IEnumerable<AudioClip>? music = null) =>
        new(clips, music ?? Array.Empty<AudioClip>(), info.FrameDuration, info.Duration);

    internal static TrimJob Job(MediaInfo info, Timeline tl, int rotation = 0) => new(info, 0, tl.End, rotation) { Edit = EditList.Of(tl) };

    // ───────────────────────── tests ─────────────────────────

    public static void Run()
    {
        Model();
        var src = Frames(Path.Combine(H.Vids, "t7_counter.mp4"));
        Console.WriteLine($"  source: {src.Length} frames");
        Counter(src, null);
        Counter(src, 1); // each clip a run of its own: the crossfade is split between two runs
        Gaps(src);
        Sounds(src);
        OneFrame(src);
        BoomerangShortPiece(src);
        Others();
        Ntsc();
        Memory();
    }

    static void Model()
    {
        using var ms = new MediaSource(Path.Combine(H.Vids, "t7_counter.mp4"), decoders: false);
        var info = ms.Info;
        double fd = info.FrameDuration;
        static string Show(Timeline t) => string.Join(" ", t.Clips.Select(c => $"#{c.Id}[{c.In:0.###},{c.Out:0.###})@{c.Start:0.###}"));

        var tl = Timeline.Whole(info);
        H.Check(tl.Clips.Count == 1 && Math.Abs(tl.End - info.Duration) < fd / 2 && Math.Abs(tl.End / fd - Math.Round(tl.End / fd)) < 1e-6,
            $"a video opens as one clip, ending on the frame grid ({tl.End})");
        H.Check(tl.SameAs(Timeline.Whole(info)) && !tl.SameAs(tl.Split(3, out _)), "the same clips are the same timeline; a cut isn't");
        var s1 = tl.Split(3, out int b);
        var s2 = s1.Split(6, out int c);
        H.Check(s2.Clips.Count == 3 && s2.Clips[1].In == 3 && s2.Clips[1].Start == 3 && s2.Clips[2].In == 6 && b > 0 && c > 0, $"split twice: {Show(s2)}");
        H.Check(ReferenceEquals(s2.Split(3, out int none), s2) && none < 0, "splitting at a cut changes nothing");
        var del = s2.Delete(b);
        H.Check(del.Clips.Count == 2 && del.Clips[1].Start == 6 && del.End == 10 && del.HasGaps, $"delete leaves a gap: {Show(del)}");
        var ripple = s2.RippleDelete(b);
        H.Check(ripple.Clips[1].Start == 3 && Math.Abs(ripple.End - 7) < 1e-9 && !ripple.HasGaps, $"delete and close the gap: {Show(ripple)}");
        H.Check(Math.Abs(del.CloseGaps().End - 7) < 1e-9 && !del.CloseGaps().HasGaps, "close gaps packs the clips");

        // Moving: into free space (its old place becomes a gap); onto others: rearranged, packed clips stay packed.
        var free = s2.Move(c, 12, 12.5);
        H.Check(free.Find(c)!.Start == 12 && free.End == 16 && free.HasGaps, $"move into free space: {Show(free)}");
        var front = s2.Move(c, 0.5, 0.5); // onto the first clip's left half: goes first
        H.Check(front.Clips[0].Id == c && front.Clips[1].Id == 1 && front.Clips[2].Id == b && !front.HasGaps && front.End == 10, $"rearrange to the front: {Show(front)}");
        var last = s2.Move(1, 8, 9); // the first clip onto the last one's right half: goes last
        H.Check(last.Clips[^1].Id == 1 && last.Clips[0].Id == b && !last.HasGaps && last.End == 10, $"rearrange to the end: {Show(last)}");

        // Fades: held to the clip.
        var faded = s2.SetFades(b, 2, 5);
        H.Check(faded.Find(b)!.FadeIn == 2 && Math.Abs(faded.Find(b)!.FadeOut - 1) < 1e-9, "fades are held to the clip's length");
        var split = faded.Split(4, out int d);
        H.Check(split.Find(b)!.FadeIn == 1 && split.Find(b)!.FadeOut == 0 && split.Find(d)!.FadeOut == 1, "a split keeps the fade in on the first part and the fade out on the second");

        // The audio track: added where it's free, moved without overlapping, trimmed within the file.
        var withMusic = s2.AddMusic("a.wav", 3, 1, out int m1).AddMusic("b.wav", 4, 2, out int m2);
        H.Check(withMusic.FindMusic(m2)!.Start == 4, $"a second sound goes after the first: {withMusic.FindMusic(m2)!.Start}");
        var moved = withMusic.MoveMusic(m2, 2);
        H.Check(moved.FindMusic(m2)!.Start is 4 or 0 && !Overlap(moved), $"sounds never overlap: {moved.FindMusic(m2)!.Start}");
        var trimmed = withMusic.TrimMusicStart(m1, 2).TrimMusicEnd(m1, 3.5);
        var tm = trimmed.FindMusic(m1)!;
        H.Check(tm.Start == 2 && tm.In == 1 && tm.End == 3.5 && tm.Out == 2.5, $"trim a sound: [{tm.In},{tm.Out})@{tm.Start}");
        var over = withMusic.TrimMusicEnd(m1, 9).TrimMusicStart(m1, -5);
        var om = over.FindMusic(m1)!;
        H.Check(om.In == 0 && om.Start == 1 && Math.Abs(om.End - 4) < 1e-9, $"trims stop at the file's ends and the next sound: [{om.In},{om.Out})@{om.Start}");
        H.Check(withMusic.AddMusic("c.wav", 8, 9, out _).End == 17, "the timeline runs to the end of the audio track when that's later");

        // Transitions: a crossfade uses the footage beyond the cut, held to what the file has and to the clips.
        var e1 = EditList.Of(Packed(info, (1, 3, Cross(0.5)), (4, 6, Cut)));
        H.Check(Math.Abs(e1.Parts[0].HandleOut - 7 * fd) < 1e-9 && e1.Parts[1].HandleIn == e1.Parts[0].HandleOut && e1.Length == 4,
            $"crossfade 0.5 s: 7 whole frames each side ({e1.Parts[0].HandleOut / fd:0.##} / {e1.Parts[1].HandleIn / fd:0.##}), length kept");
        var e2 = EditList.Of(Packed(info, (8, 10, Cross(1)), (0.1, 2, Cut)));
        H.Check(e2.Parts[0].HandleOut == 0 && e2.Parts[0].CutOut, "no footage beyond the cut (end and start of the file): a straight cut");
        var e3 = EditList.Of(Packed(info, (1, 1.2, Cross(2)), (4, 6, Cut)));
        H.Check(Math.Abs(e3.Parts[0].HandleOut - 0.1) < 1e-9, $"a crossfade is at most half the shorter clip: {e3.Parts[0].HandleOut}");
        var e4 = EditList.Of(Packed(info, (1, 3, Cut), (3, 5, Cut)));
        H.Check(!e4.Parts[0].CutOut && !e4.Parts[1].CutIn, "continuous footage (split, untouched): no declick at the cut");
        var e5 = EditList.Of(Packed(info, (1, 3, Dip(1)), (4, 6, Cut)));
        H.Check(e5.Parts[0].FadeOut == 0.5 && e5.Parts[1].FadeIn == 0.5 && e5.Length == 4, "fade through black: half each side, length kept");
        var e6 = EditList.Of(Placed(info, new[] { new Clip(1, 1, 3, 0, Cross(0.5), 0.5, 1), new Clip(2, 4, 6, 2, Cut, 1, 0.5) }));
        H.Check(e6.Parts[0].HandleOut > 0 && e6.Parts[0].FadeOut == 0 && e6.Parts[1].FadeIn == 0 && e6.Parts[0].FadeIn == 0.5 && e6.Parts[1].FadeOut == 0.5,
            $"clip fades give way to a crossfade on the same edge (out {e6.Parts[0].FadeOut}, in {e6.Parts[1].FadeIn}); the other ends keep theirs");
        H.Check(EditList.Of(del).Locate(4) == 1 && EditList.Of(del).Locate(11) == 2, "locating a time in a gap finds the next clip, past the clips none");

        static bool Overlap(Timeline t) => t.Music.Zip(t.Music.Skip(1)).Any(p => p.Second.Start < p.First.End - 1e-6);
    }

    /// <summary>Frame-by-frame checks on the numbered clip, in one run (<paramref name="maxPerRun"/> null) or in runs.</summary>
    static void Counter(byte[][] src, int? maxPerRun)
    {
        string file = Path.Combine(H.Vids, "t7_counter.mp4");
        string tag = maxPerRun == null ? "one run" : "in runs";
        using var ms = new MediaSource(file, decoders: false);
        var info = ms.Info;
        Exporter.MaxPartsPerRunOverride = maxPerRun;
        try
        {
            // 1. [1,3) | [4,6) × 0.5 s crossfade × [7,9): 6 s. The crossfade (7 frames each side) runs 3.767–4.233.
            var tl1 = Packed(info, (1, 3, Cut), (4, 6, Cross(0.5)), (7, 9, Cut));
            string out1 = Path.Combine(H.Out, $"tl1_cut_crossfade_{maxPerRun}.mp4");
            Export(Job(info, tl1), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out1));
            var o1 = Frames(out1);
            H.Check(o1.Length == 180, $"{tag}, cut + crossfade: {o1.Length} frames (expected 180, a crossfade doesn't shorten)");
            foreach (var (k, j) in new[] { (0, 30), (59, 89), (60, 120), (111, 171), (128, 218), (179, 269) })
                if (k < o1.Length) H.Check(Closest(o1[k], src, j) == j, $"{tag}: frame {k} is source frame {Closest(o1[k], src, j)} (expected {j})");
            if (o1.Length > 120) H.Check(IsMix(o1[120], src, 180, 210, out string d1), $"{tag}: the cut's frame mixes both sides ({d1})");
            double v1 = Duration(out1, "v:0"), a1 = Duration(out1, "a:0");
            H.Check(Math.Abs(v1 - 6) < 0.04 && Math.Abs(a1 - 6) < 0.06, $"{tag}: video {v1}, sound {a1} (expected 6)");

            // 2. A 1 s fade through black between the first two, a straight cut to the third.
            var tl2 = Packed(info, (1, 3, Dip(1)), (4, 6, Cut), (7, 9, Cut));
            string out2 = Path.Combine(H.Out, $"tl2_fade_cut_{maxPerRun}.mp4");
            Export(Job(info, tl2), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out2));
            var o2 = Frames(out2);
            H.Check(o2.Length == 180, $"{tag}, fade + cut: {o2.Length} frames");
            foreach (var (k, j) in new[] { (0, 30), (90, 150), (119, 179), (120, 210), (179, 269) })
                if (k < o2.Length) H.Check(Closest(o2[k], src, j) == j, $"{tag}, fade + cut: frame {k} is source {Closest(o2[k], src, j)} (expected {j})");
            if (o2.Length == 180)
            {
                double normal = Mean(o2[30]);
                H.Check(Mean(o2[59]) < 0.2 * normal && Mean(o2[60]) < 0.2 * normal && Mean(o2[44]) > 0.85 * normal && Mean(o2[75]) > 0.85 * normal,
                    $"{tag}: dark at the join ({Mean(o2[59]):0} / {Mean(o2[60]):0}), bright outside ({Mean(o2[44]):0} / {Mean(o2[75]):0}) vs {normal:0}");
            }
            H.Check(NoLeftovers(), $"{tag}: no work files left");
            if (maxPerRun != null) H.Check(DtsClean(out1, 1 / 30.0, out string dd), $"{tag}: decode times step a frame at a time ({dd})");

            if (maxPerRun != null) return;
            // 3. Rotated and cropped: [1,3) × 1 s crossfade × [4,9): 7 s.
            var tl3 = Packed(info, (1, 3, Cross(1)), (4, 9, Cut));
            string out3 = Path.Combine(H.Out, "tl3_rotated_cropped.mp4");
            var job3 = Job(info, tl3, 90) with { Crop = new RectangleF(0, 0.25f, 1, 0.5f) };
            Export(job3, new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, out3));
            string dims = H.Probe(out3, "-select_streams v -show_entries stream=width,height,nb_frames -of compact=p=0");
            var (w3, h3) = job3.OutputSize();
            H.Check(dims.Contains($"width={w3}|height={h3}") && dims.Contains("nb_frames=210"), $"rotated + cropped: {dims} (expected {w3}x{h3}, 210 frames)");
        }
        finally { Exporter.MaxPartsPerRunOverride = null; }
    }

    /// <summary>Gaps: black and silent between clips, before the first, and after the last while sound plays on.</summary>
    static void Gaps(byte[][] src)
    {
        string file = Path.Combine(H.Vids, "t7_counter.mp4");
        using var ms = new MediaSource(file, decoders: false);
        var info = ms.Info;

        // [1,2) at 0, [5,6) at 3: 4 s, black and silent 1–3 s.
        var tl = Placed(info, new[] { new Clip(1, 1, 2, 0, Cut), new Clip(2, 5, 6, 3, Cut) });
        string out1 = Path.Combine(H.Out, "tl4_gap.mp4");
        Export(Job(info, tl), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out1));
        var o = Frames(out1);
        H.Check(o.Length == 120, $"gap: {o.Length} frames (expected 120)");
        if (o.Length == 120)
        {
            H.Check(Closest(o[29], src, 59) == 59 && Closest(o[90], src, 150) == 150, "gap: the clips either side are the right frames");
            H.Check(Mean(o[30]) < 3 && Mean(o[60]) < 3 && Mean(o[89]) < 3, $"gap: black ({Mean(o[30]):0.#}, {Mean(o[60]):0.#}, {Mean(o[89]):0.#})");
        }
        var snd = Sound(out1);
        H.Check(Rms(snd, 1.05, 2.95) < 30 && Rms(snd, 0.1, 0.9) > 500 && Rms(snd, 3.1, 3.9) > 500, $"gap: silent ({Rms(snd, 1.05, 2.95):0}), sound either side");

        // A gap under half a frame (clips off the frame grid) is a join: no black frame.
        var tiny = Placed(info, new[] { new Clip(1, 1, 2, 0, Cut), new Clip(2, 5, 6, 1 + 0.4 / 30, Cut) });
        string outTiny = Path.Combine(H.Out, "tl4b_tiny_gap.mp4");
        Export(Job(info, tiny), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, outTiny));
        var ot = Frames(outTiny);
        H.Check(ot.Length == 60 && ot.All(f => Mean(f) > 10) && Closest(ot[30], src, 150) == 150, $"a gap under half a frame: {ot.Length} frames (expected 60), none black");

        // Before the first clip: [2,3) at 1 → 2 s, the first second black.
        var lead = Placed(info, new[] { new Clip(1, 2, 3, 1, Cut) });
        string out2 = Path.Combine(H.Out, "tl5_leading_gap.mp4");
        Export(Job(info, lead), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out2));
        var o2 = Frames(out2);
        H.Check(o2.Length == 60 && Mean(o2[0]) < 3 && Mean(o2[29]) < 3 && Closest(o2[30], src, 60) == 60, $"leading gap: {o2.Length} frames, black then source 60");

        // Clip fades: [0,3) fading in 1 s and out 1 s.
        var fades = Placed(info, new[] { new Clip(1, 0, 3, 0, Cut, 1, 1) });
        string out3 = Path.Combine(H.Out, "tl6_clip_fades.mp4");
        Export(Job(info, fades), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out3));
        var o3 = Frames(out3);
        var s3 = Sound(out3);
        if (o3.Length == 90)
        {
            double normal = Mean(o3[45]);
            H.Check(Mean(o3[0]) < 0.1 * normal && Math.Abs(Mean(o3[15]) / normal - 0.5) < 0.2 && Mean(o3[89]) < 0.1 * normal,
                $"clip fades: {Mean(o3[0]):0} / {Mean(o3[15]):0} / {Mean(o3[89]):0} vs {normal:0}");
        }
        else H.Check(false, $"clip fades: {o3.Length} frames (expected 90)");
        H.Check(Rms(s3, 0, 0.1) < 0.2 * Rms(s3, 1.2, 1.8) && Rms(s3, 2.9, 3) < 0.2 * Rms(s3, 1.2, 1.8), "clip fades: the sound fades too");
    }

    /// <summary>The audio track: mixed in where its clips are, faded, trimmed; sound past the last video clip over black.</summary>
    static void Sounds(byte[][] src)
    {
        string file = Path.Combine(H.Vids, "t7_counter.mp4"), tone = Path.Combine(H.Vids, "s1_tone.wav"), low = Path.Combine(H.Vids, "s2_low.mp3");
        using var ms = new MediaSource(file, decoders: false);
        var info = ms.Info;

        // Video [0,4) at 0; a 1 kHz tone (WAV, 3 s) at 1 s with 0.5 s fades; a 300 Hz tone (MP3, trimmed to [1,3)) at 4.5 s:
        // the timeline runs to 6.5 s, black after 4 s.
        var music = new[]
        {
            new AudioClip(10, tone, 0, 3, 1, 3, 0.5, 0.5),
            new AudioClip(11, low, 1, 3, 4.5, 4),
        };
        // In one run; and as two clips, each a run of its own (the audio track is mixed in when the runs are joined).
        var tl = Placed(info, new[] { new Clip(1, 0, 4, 0, Cut) }, music);
        var tlRuns = Placed(info, new[] { new Clip(1, 0, 2, 0, Cut), new Clip(2, 5, 7, 2, Cut) }, music);
        foreach (var (t, max) in new[] { (tl, (int?)null), (tlRuns, (int?)1) })
        {
            string tag = max == null ? "sound track" : "sound track in runs";
            string out1 = Path.Combine(H.Out, max == null ? "tl7_sound_track.mp4" : "tl7b_sound_track_runs.mp4");
            Exporter.MaxPartsPerRunOverride = max;
            try { Export(Job(info, t), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out1)); }
            finally { Exporter.MaxPartsPerRunOverride = null; }
            double v = Duration(out1, "v:0"), a = Duration(out1, "a:0");
            H.Check(Math.Abs(v - 6.5) < 0.04 && Math.Abs(a - 6.5) < 0.06, $"{tag}: video {v}, sound {a} (expected 6.5: the sound runs on)");
            var o = Frames(out1);
            int at100 = max == null ? 100 : 190;
            H.Check(o.Length > 190 && Closest(o[100], src, at100) == at100 && Mean(o[150]) < 3 && Mean(o[190]) < 3, $"{tag}: the video, then black under the sound");
            var snd = Sound(out1);
            double before = Tone(snd, 0.2, 0.9, 1000), middle = Tone(snd, 2, 3, 1000), after = Tone(snd, 4.2, 4.4, 1000);
            H.Check(before < 300 && middle > 2500 && after < 300, $"{tag}: the WAV plays from 1 to 4 s: 1 kHz {before:0} / {middle:0} / {after:0}");
            double fadeIn = Tone(snd, 1.05, 1.15, 1000);
            H.Check(fadeIn < 0.6 * middle, $"{tag}: the WAV fades in: {fadeIn:0} at the start vs {middle:0}");
            double lowOn = Tone(snd, 5, 6.3, 300), lowOff = Tone(snd, 2, 3, 300);
            H.Check(lowOn > 2500 && lowOff < 300, $"{tag}: the MP3 plays from 4.5 s: 300 Hz {lowOn:0} there, {lowOff:0} before");
            double video440 = Tone(snd, 0.2, 0.9, 440);
            H.Check(video440 > 1000, $"{tag}: the video's own sound is still there: 440 Hz {video440:0}");
        }

        // Only sound on the timeline (every clip deleted): black under it from the start, no file to copy metadata from.
        var only = Placed(info, Array.Empty<Clip>(), new[] { new AudioClip(10, tone, 0, 3, 0.5, 3) });
        string outOnly = Path.Combine(H.Out, "tl8b_sound_only.mp4");
        Export(Job(info, only), new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, outOnly));
        double vo = Duration(outOnly, "v:0"), ao = Duration(outOnly, "a:0");
        var oo = Frames(outOnly);
        var so = Sound(outOnly);
        H.Check(Math.Abs(vo - 3.5) < 0.04 && Math.Abs(ao - 3.5) < 0.06 && oo.Length > 100 && oo.All(f => Mean(f) < 3) && Tone(so, 1, 3, 1000) > 2500 && Tone(so, 0, 0.4, 1000) < 300,
            $"sound only: video {vo}, sound {ao} (expected 3.5), {oo.Length} black frames, the tone from 0.5 s");
        // ... and a sound shorter than half a frame: still a picture to carry it.
        var blip = Placed(info, Array.Empty<Clip>(), new[] { new AudioClip(10, tone, 0, 0.01, 0, 3) });
        string outBlip = Path.Combine(H.Out, "tl8c_blip_only.mp4");
        try
        {
            Export(Job(info, blip), new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, outBlip));
            H.Check(FrameCount(outBlip) == "1", $"a sound under half a frame, nothing else: {FrameCount(outBlip)} frame");
        }
        catch (Exception ex) { H.Check(false, $"a sound under half a frame: {ex.Message}"); }

        // A silent video with a sound file: the export has sound.
        string mkv = Path.Combine(H.Vids, "t5_noaudio.mkv");
        using var ns = new MediaSource(mkv, decoders: false);
        var tl2 = Placed(ns.Info, new[] { new Clip(1, 0, 3, 0, Cut) }, new[] { new AudioClip(10, tone, 0, 3, 0, 3) });
        string out2 = Path.Combine(H.Out, "tl8_silent_video_with_sound.mp4");
        Export(Job(ns.Info, tl2), new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, out2));
        var snd2 = Sound(out2);
        H.Check(Tone(snd2, 0.5, 2.5, 1000) > 2500, $"silent video + sound file: 1 kHz {Tone(snd2, 0.5, 2.5, 1000):0}");
    }

    /// <summary>A clip one frame long, with and without sound, in one run and in runs.</summary>
    static void OneFrame(byte[][] src)
    {
        foreach (var (file, fps, sound) in new[] { ("t7_counter.mp4", 30.0, true), ("t5_noaudio.mkv", 60.0, false) })
        {
            using var ms = new MediaSource(Path.Combine(H.Vids, file), decoders: false);
            var tl = Packed(ms.Info, (0, 10 / fps, Cut), (20 / fps, 21 / fps, Cut), (31 / fps, 2, Cut));
            int expected = (int)Math.Round(2 * fps) - 20;
            foreach (int? max in new int?[] { null, 1 })
            {
                string outFile = Path.Combine(H.Out, $"tl_oneframe_{Path.GetFileNameWithoutExtension(file)}_{max}.mp4");
                Exporter.MaxPartsPerRunOverride = max;
                try
                {
                    Export(Job(ms.Info, tl), new ExportSettings(ExportFormat.H264, ExportQuality.High, true, outFile));
                    string tag = $"one-frame clip, {file}, {(max == null ? "one run" : "in runs")}";
                    H.Check(FrameCount(outFile) == expected.ToString(), $"{tag}: {FrameCount(outFile)} frames (expected {expected})");
                    if (max != null) H.Check(DtsClean(outFile, 1 / fps, out string d), $"{tag}: decode times ({d})");
                    if (sound)
                    {
                        var o = Frames(outFile);
                        H.Check(o.Length > 12 && Closest(o[10], src, 20) == 20 && Closest(o[11], src, 31) == 31, $"{tag}: frames 10, 11 are source 20, 31");
                    }
                }
                catch (Exception ex) { H.Check(false, $"one-frame clip {file}: {ex.Message}"); }
                finally { Exporter.MaxPartsPerRunOverride = null; }
            }
        }
    }

    static void BoomerangShortPiece(byte[][] src)
    {
        using var ms = new MediaSource(Path.Combine(H.Vids, "t7_counter.mp4"), decoders: false);
        string outFile = Path.Combine(H.Out, "boomerang_short_piece.mp4");
        try
        {
            Export(new TrimJob(ms.Info, 0, 220 / 30.0, 0), new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, outFile, Boomerang: true));
            var o = Frames(outFile);
            H.Check(o.Length == 438, $"boomerang: {o.Length} frames (expected 220 + 218)");
            foreach (var (k, j) in new[] { (0, 0), (219, 219), (220, 218), (221, 217), (437, 1) })
                if (k < o.Length) H.Check(Closest(o[k], src, j) == j, $"boomerang: frame {k} is source {Closest(o[k], src, j)} (expected {j})");
            H.Check(DtsClean(outFile, 1 / 30.0, out string d), $"boomerang: decode times ({d})");
        }
        catch (Exception ex) { H.Check(false, $"boomerang: {ex.Message}"); }
    }

    static void Others()
    {
        string hdr = Path.Combine(H.Vids, "t2_iphone_like.mov");
        using (var ms = new MediaSource(hdr, decoders: false))
        {
            var tl = Packed(ms.Info, (0.5, 2, Cross(0.5)), (3, 5, Dip(0.5)), (6, 7.5, Cut));
            string out4 = Path.Combine(H.Out, "tl9_hdr.mp4");
            try
            {
                Export(Job(ms.Info, tl), new ExportSettings(ExportFormat.Hevc, ExportQuality.Balanced, true, out4));
                string p = H.Probe(out4, "-select_streams v -show_entries stream=width,height,color_transfer,pix_fmt -of compact=p=0");
                double v = Duration(out4, "v:0"), a = Duration(out4, "a:0");
                H.Check(p.Contains("width=2160|height=3840") && p.Contains("arib-std-b67") && p.Contains("10le"), $"HDR: {p}");
                H.Check(Math.Abs(v - tl.End) < 0.02 && Math.Abs(a - tl.End) < 0.06, $"HDR lengths: video {v}, sound {a} (expected {tl.End})");
            }
            catch (Exception ex) { H.Check(false, $"HDR export: {ex.Message}"); }

            string out5 = Path.Combine(H.Out, "tl10_hdr_to_h264.mp4");
            try
            {
                var gap = Placed(ms.Info, new[] { new Clip(1, 0.5, 2, 0, Cut), new Clip(2, 3, 4.5, 2, Cut) });
                Export(Job(ms.Info, gap), new ExportSettings(ExportFormat.H264, ExportQuality.Small, true, out5));
                string p = H.Probe(out5, "-select_streams v -show_entries stream=pix_fmt,color_transfer,nb_frames -of compact=p=0");
                H.Check(p.Contains("yuv420p|") && p.Contains("bt709") && p.Contains("nb_frames=105"), $"HDR → H.264 with a gap: {p} (expected 105 frames)");
            }
            catch (Exception ex) { H.Check(false, $"HDR → H.264 export: {ex.Message}"); }
        }

        string mkv = Path.Combine(H.Vids, "t5_noaudio.mkv");
        using (var ms = new MediaSource(mkv, decoders: false))
        {
            var tl = Packed(ms.Info, (0, 1, Cross(0.25)), (1.5, 2.5, Cut), (3, 4, Dip(2)), (4.5, 6, Cut));
            int expected = (int)Math.Round(tl.End * 60);
            foreach (int? max in new int?[] { null, 1 })
            {
                string out6 = Path.Combine(H.Out, $"tl11_noaudio_{max}.mp4");
                Exporter.MaxPartsPerRunOverride = max;
                try
                {
                    Export(Job(ms.Info, tl), new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, out6));
                    string p = H.Probe(out6, "-show_entries stream=codec_type,nb_frames -of compact=p=0");
                    H.Check(!p.Contains("audio") && p.Contains($"nb_frames={expected}"), $"no sound, 60 fps ({(max == null ? "one run" : "in runs")}): {p} (expected {expected})");
                }
                catch (Exception ex) { H.Check(false, $"no-sound export: {ex.Message}"); }
                finally { Exporter.MaxPartsPerRunOverride = null; }
            }
            var two = Packed(ms.Info, (0, 1, Cross(0.5)), (1.5, 2.5, Cross(0.5)), (3, 4, Cut));
            foreach (int? max in new int?[] { null, 1 })
            {
                string outTwo = Path.Combine(H.Out, $"tl11b_two_crossfades_{max}.mp4");
                Exporter.MaxPartsPerRunOverride = max;
                try
                {
                    Export(Job(ms.Info, two), new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, outTwo));
                    H.Check(FrameCount(outTwo) == "180", $"two crossfades in a row, 60 fps MKV ({(max == null ? "one run" : "in runs")}): {FrameCount(outTwo)} frames (expected 180)");
                }
                catch (Exception ex) { H.Check(false, $"two crossfades: {ex.Message}"); }
                finally { Exporter.MaxPartsPerRunOverride = null; }
            }
        }

        // Many clips: 85 four-frame clips and the last five frames, packed. Runs of 24, or one run with the graph in a file.
        string longClip = Path.Combine(H.Vids, "t1_h264_1080p30.mp4");
        using (var ms = new MediaSource(longClip, decoders: false))
        {
            var clips = Enumerable.Range(0, 86).Select(k => (From: 7 * k / 30.0, To: k < 85 ? (7 * k + 4) / 30.0 : 20.0, Next: Cut)).ToArray();
            clips[85] = (595 / 30.0, 20.0, Cut);
            var tl = Packed(ms.Info, clips);
            var job = Job(ms.Info, tl);
            H.Check(Exporter.Runs(job).Count == 4, $"86 clips of 1080p: {Exporter.Runs(job).Count} runs");
            foreach (int? max in new int?[] { null, 100 })
            {
                string out8 = Path.Combine(H.Out, $"tl12_many_{max}.mp4");
                Exporter.MaxPartsPerRunOverride = max;
                try
                {
                    Export(job, new ExportSettings(ExportFormat.H264, ExportQuality.Small, true, out8));
                    double a = Duration(out8, "a:0");
                    H.Check(FrameCount(out8) == "345" && Math.Abs(a - 345 / 30.0) < 0.06 && NoLeftovers(),
                        $"86 clips ({(max == null ? "in runs" : "one run, graph in a file")}): {FrameCount(out8)} frames (expected 345), sound {a:0.###} s");
                }
                catch (Exception ex) { H.Check(false, $"many clips: {ex.Message}"); }
                finally { Exporter.MaxPartsPerRunOverride = null; }
            }
        }

        // One clip from the start, nothing else: a plain trim, so it can be copied.
        using (var ms = new MediaSource(Path.Combine(H.Vids, "t7_counter.mp4"), decoders: false))
        {
            string out7 = Path.Combine(H.Out, "tl13_copy.mp4");
            try
            {
                Export(new TrimJob(ms.Info, 3, 7, 0), new ExportSettings(ExportFormat.Original, ExportQuality.High, true, out7));
                double v = Duration(out7, "v:0");
                H.Check(v > 3.9 && v < 5.6, $"a plain trim copies: {v}");
            }
            catch (Exception ex) { H.Check(false, $"copy: {ex.Message}"); }
        }
    }

    /// <summary>29.97 fps: a gap and then a crossfade, frame-exact and at the file's own rate.</summary>
    static void Ntsc()
    {
        using var ms = new MediaSource(Path.Combine(H.Vids, "t8_ntsc.mp4"), decoders: false);
        var info = ms.Info;
        double fd = info.FrameDuration;
        H.Check(info.Rate == "30000/1001", $"29.97 fps: the rate is {info.Rate}");
        // Frames [0,30) at 0, a 15-frame gap, [60,90) at 45 × 0.5 s crossfade × [120,150) at 75: 105 frames.
        var tl = Placed(info, new[] { new Clip(1, 0, 30 * fd, 0, Cut), new Clip(2, 60 * fd, 90 * fd, 45 * fd, Cross(0.5)), new Clip(3, 120 * fd, 150 * fd, 75 * fd, Cut) });
        foreach (int? max in new int?[] { null, 1 })
        {
            string outFile = Path.Combine(H.Out, $"tl14_ntsc_{max}.mp4");
            Exporter.MaxPartsPerRunOverride = max;
            try
            {
                Export(Job(info, tl), new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, outFile));
                string p = H.Probe(outFile, "-select_streams v -show_entries stream=nb_frames,r_frame_rate,avg_frame_rate -of compact=p=0");
                H.Check(p.Contains("nb_frames=105") && p.Contains("r_frame_rate=30000/1001"), $"29.97 fps, gap + crossfade ({(max == null ? "one run" : "in runs")}): {p} (expected 105 frames at 30000/1001)");
            }
            catch (Exception ex) { H.Check(false, $"29.97 fps export: {ex.Message}"); }
            finally { Exporter.MaxPartsPerRunOverride = null; }
        }
    }

    /// <summary>ffmpeg's peak memory for 4K HDR: six clips (and a chain of crossfades) against a plain export.</summary>
    static void Memory()
    {
        string hdr = Path.Combine(H.Vids, "t2_iphone_like.mov");
        using var ms = new MediaSource(hdr, decoders: false);
        var six = Packed(ms.Info, (0, 1, Cross(0.5)), (1.5, 2.5, Cut), (3, 4, Dip(0.5)), (4.5, 5.5, Cut), (6, 6.5, Cross(0.25)), (7, 8, Cut));
        foreach (var fmt in new[] { ExportFormat.H264, ExportFormat.Hevc })
        {
            string outPlain = Path.Combine(H.Out, $"mem_plain_{fmt}.mp4"), outSix = Path.Combine(H.Out, $"mem_six_{fmt}.mp4");
            long plain = Export(new TrimJob(ms.Info, 0, 8, 0), new ExportSettings(fmt, ExportQuality.Small, true, outPlain));
            long spliced = Export(Job(ms.Info, six), new ExportSettings(fmt, ExportQuality.Small, true, outSix));
            double v = Duration(outSix, "v:0");
            H.Check(Math.Abs(v - six.End) < 0.02, $"4K {fmt}, six clips: {v} s (expected {six.End})");
            H.Check(spliced < plain + 1_800_000_000L, $"4K {fmt}: six clips take {spliced / 1e6:0} MB, a plain export {plain / 1e6:0} MB");
            if (fmt == ExportFormat.H264) H.Check(plain < 3_500_000_000L, $"4K HDR → H.264 plain export under 3.5 GB: {plain / 1e6:0} MB");
            if (fmt != ExportFormat.Hevc) continue;
            var chain = Packed(ms.Info, Enumerable.Range(0, 8).Select(k => (From: k * 1.0, To: k + 0.8, Next: Cross(0.2))).ToArray());
            string outChain = Path.Combine(H.Out, "mem_chain_Hevc.mp4");
            long chained = Export(Job(ms.Info, chain), new ExportSettings(fmt, ExportQuality.Small, true, outChain));
            double cv = Duration(outChain, "v:0");
            H.Check(Exporter.Runs(Job(ms.Info, chain)).Count == 2 && chained < plain + 1_800_000_000L && Math.Abs(cv - chain.End) < 0.02,
                $"4K, eight crossfaded clips: {Exporter.Runs(Job(ms.Info, chain)).Count} runs, {chained / 1e6:0} MB, {cv} s (expected {chain.End:0.###})");
        }
    }
}
