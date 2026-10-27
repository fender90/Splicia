using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Playing timelines with the real player (muted, no window), polling it like the window's 4 ms timer: no stalls or
/// dropped frames at cuts and gaps, the right frames, crossfades blended and fades dark, black in gaps, the sound
/// continuous and the same as the export's sound (the audio track included), seamless looping, 4K HDR, a file without
/// sound, a timeline of sound only, and starting in the middle of a crossfade.
/// </summary>
static class PlaybackTests
{
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

    const int TW = 160, TH = 90;

    sealed record Shown(double Out, double Time, double Wall, byte[] Thumb);

    sealed record Result(List<Shown> Frames, short[] Sound, long MinPending, double Fd);

    static byte[] Thumb(VideoFrame f)
    {
        var t = new byte[TW * TH];
        for (int y = 0; y < TH; y++)
        for (int x = 0; x < TW; x++)
        {
            int x0 = x * f.Width / TW, x1 = Math.Max(x0 + 1, (x + 1) * f.Width / TW), y0 = y * f.Height / TH, y1 = Math.Max(y0 + 1, (y + 1) * f.Height / TH);
            long sum = 0, n = 0;
            for (int yy = y0; yy < y1; yy += 2)
            for (int xx = x0; xx < x1; xx += 2)
            {
                int i = (yy * f.Width + xx) * 4;
                sum += (f.Data[i] * 29 + f.Data[i + 1] * 150 + f.Data[i + 2] * 77) >> 8;
                n++;
            }
            t[y * TW + x] = (byte)(sum / Math.Max(1, n));
        }
        return t;
    }

    /// <summary>Every frame of the file through the player's own decoder and conversion, as thumbnails.</summary>
    static byte[][] SourceThumbs(string file)
    {
        using var src = new MediaSource(file);
        var size = src.ScaledSize(1920);
        var list = new List<byte[]>();
        var f = new VideoFrame();
        src.Seek(0);
        while (true)
        {
            var got = src.Next();
            if (got == MediaSource.Got.End) break;
            if (got != MediaSource.Got.Video) continue;
            src.ConvertVideo(f, size.W, size.H);
            list.Add(Thumb(f));
        }
        return list.ToArray();
    }

    /// <summary>Plays <paramref name="edit"/> from timeline time <paramref name="at"/> and records what's shown and heard.</summary>
    static Result Play(string file, EditList edit, double at, bool loop, double seconds)
    {
        using var pb = new Playback(file);
        pb.Volume = 0;
        var sound = new List<short>();
        if (pb.Output is AudioOut output)
            output.Tap = pcm => { var s = MemoryMarshal.Cast<byte, short>(pcm); lock (sound) foreach (var x in s) sound.Add(x); };
        var frames = new List<Shown>();
        long minPending = long.MaxValue;
        timeBeginPeriod(1);
        try
        {
            pb.Play(at, edit, loop);
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                var f = pb.TakeDue(pb.Clock);
                if (f != null)
                {
                    frames.Add(new Shown(f.Out, f.Time, sw.Elapsed.TotalSeconds, Thumb(f)));
                    pb.Recycle(f);
                }
                if (frames.Count > 5 && pb.Output != null && (loop || pb.Clock < edit.Length - 0.4)) minPending = Math.Min(minPending, pb.Output.PendingFrames);
                if (pb.Ended) break;
                Thread.Sleep(4);
            }
            pb.Pause();
        }
        finally { timeEndPeriod(1); }
        short[] snd;
        lock (sound) snd = sound.ToArray();
        return new Result(frames, snd, minPending, pb.Info.FrameDuration);
    }

    /// <summary>Frames of the timeline never shown, and the longest wait between two shown frames (after the start).</summary>
    static (int Missing, double MaxGap) Smoothness(Result r, double length)
    {
        int expected = (int)Math.Round(length / r.Fd);
        var shown = r.Frames.Select(f => (int)Math.Round(f.Out / r.Fd)).ToHashSet();
        int missing = Enumerable.Range(0, expected).Count(k => !shown.Contains(k));
        var gaps = r.Frames.Skip(1).Zip(r.Frames.Skip(2), (a, b) => (Gap: b.Wall - a.Wall, At: b.Out)).ToList();
        var worst = gaps.DefaultIfEmpty((0, 0)).MaxBy(g => g.Gap);
        double startup = r.Frames.Count > 1 ? r.Frames[1].Wall - r.Frames[0].Wall : 0;
        Console.WriteLine($"    longest wait {worst.Gap * 1000:0} ms before {worst.At:0.###} s; first frame after {r.Frames.FirstOrDefault()?.Wall * 1000:0} ms, moving {startup * 1000:0} ms later");
        return (missing, worst.Gap);
    }

    static short[] Left(short[] stereo) => Enumerable.Range(0, stereo.Length / 2).Select(i => stereo[i * 2]).ToArray();

    static short[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int i = 12;
        while (i + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, i, 4);
            int size = BitConverter.ToInt32(bytes, i + 4);
            if (id == "data") return MemoryMarshal.Cast<byte, short>(bytes.AsSpan(i + 8, Math.Min(size, bytes.Length - i - 8))).ToArray();
            i += 8 + size;
        }
        return Array.Empty<short>();
    }

    /// <summary>The preview's sound against the export's: best alignment within ±2 ms, then RMS of the difference over RMS of the export.</summary>
    static (double Rel, int Lag) Compare(short[] preview, short[] export)
    {
        int n = Math.Min(preview.Length, export.Length), skip = 480;
        (double, int) best = (double.MaxValue, 0);
        for (int lag = -96; lag <= 96; lag++)
        {
            double d = 0, e = 0;
            for (int i = skip; i < n - 100; i += 3)
            {
                int j = i + lag;
                if (j < 0 || j >= preview.Length) continue;
                double x = preview[j] - export[i];
                d += x * x;
                e += export[i] * (double)export[i];
            }
            double rel = Math.Sqrt(d / Math.Max(1, e));
            if (rel < best.Item1) best = (rel, lag);
        }
        return best;
    }

    static int MaxStep(short[] mono, double at)
    {
        int c = (int)(at * MediaSource.AudioRate), m = 0;
        for (int i = Math.Max(1, c - 480); i < Math.Min(mono.Length, c + 480); i++) m = Math.Max(m, Math.Abs(mono[i] - mono[i - 1]));
        return m;
    }

    static int MaxStepAll(short[] mono) { int m = 0; for (int i = 1; i < mono.Length; i++) m = Math.Max(m, Math.Abs(mono[i] - mono[i - 1])); return m; }

    /// <summary>Renders the export's sound for the timeline and checks the preview sounded the same and never ran short.</summary>
    static void SoundMatches(string tag, MediaInfo info, Timeline tl, Result r)
    {
        string wav = Path.Combine(H.Out, $"play_{tag.Replace(' ', '_').Replace("+", "and")}_export_sound.wav");
        Exporter.RenderSoundAsync(TimelineTests.Job(info, tl), wav).GetAwaiter().GetResult();
        var exp = Left(ReadWav(wav));
        var pre = Left(r.Sound);
        var (rel, lag) = Compare(pre, exp);
        H.Check(r.MinPending > MediaSource.AudioRate / 20, $"{tag}: sound never ran short (at least {r.MinPending * 1000.0 / MediaSource.AudioRate:0} ms queued)");
        H.Check(rel < 0.12 && Math.Abs(lag) <= 48, $"{tag}: preview sound = export sound: difference {rel * 100:0.#}% (lag {lag} samples)");
    }

    public static void Run()
    {
        string counter = Path.Combine(H.Vids, "t7_counter.mp4");
        var src = SourceThumbs(counter);
        using var ms = new MediaSource(counter, decoders: false);
        var info = ms.Info;
        double fd = info.FrameDuration;
        Shown? At(Result r, int k) => r.Frames.FirstOrDefault(f => Math.Abs(f.Out - k * fd) < fd / 3);

        // 1. [1,3) | [4,6) × 0.5 s crossfade × [7,9): 6 s, the crossfade 3.767–4.233.
        var tl1 = TimelineTests.Packed(info, (1, 3, TimelineTests.Cut), (4, 6, TimelineTests.Cross(0.5)), (7, 9, TimelineTests.Cut));
        var r1 = Play(counter, EditList.Of(tl1), 0, false, 8);
        var (miss1, gap1) = Smoothness(r1, tl1.End);
        H.Check(r1.Frames.Count > 170 && miss1 <= 1 && gap1 < 2.6 * fd, $"cut + crossfade: {r1.Frames.Count} frames, {miss1} missed, longest wait {gap1 * 1000:0} ms");
        foreach (var (k, j) in new[] { (59, 89), (60, 120), (111, 171), (128, 218), (179, 269) })
            if (At(r1, k) is Shown s) H.Check(TimelineTests.Closest(s.Thumb, src, j) == j, $"cut + crossfade: frame {k} shows source {TimelineTests.Closest(s.Thumb, src, j)} (expected {j})");
        if (At(r1, 120) is Shown mid) H.Check(TimelineTests.IsMix(mid.Thumb, src, 180, 210, out string dm), $"crossfade shown blended ({dm})");
        else H.Check(false, "the crossfade's middle frame was shown");
        SoundMatches("cut + crossfade", info, tl1, r1);
        var pre1 = Left(r1.Sound);
        int normal = MaxStepAll(pre1), cut = MaxStep(pre1, 2.0);
        H.Check(cut <= normal * 1.1, $"no click at the straight cut: largest step {cut}, the sound's own largest {normal}");

        // 2. A fade through black, then a gap: [1,3) ∨ 1 s ∨ [4,6) at 2, [7,8) at 5 (gap 4–5).
        var tl2 = TimelineTests.Placed(info, new[]
        {
            new Clip(1, 1, 3, 0, TimelineTests.Dip(1)), new Clip(2, 4, 6, 2, TimelineTests.Cut), new Clip(3, 7, 8, 5, TimelineTests.Cut),
        });
        var r2 = Play(counter, EditList.Of(tl2), 0, false, 8);
        var (miss2, gap2) = Smoothness(r2, tl2.End);
        H.Check(miss2 <= 1 && gap2 < 2.6 * fd, $"fade + gap: {miss2} missed, longest wait {gap2 * 1000:0} ms");
        if (At(r2, 30) is Shown n2 && At(r2, 59) is Shown o2 && At(r2, 60) is Shown i2 && At(r2, 44) is Shown b2)
        {
            double nb = TimelineTests.Mean(n2.Thumb);
            H.Check(TimelineTests.Mean(o2.Thumb) < 0.2 * nb && TimelineTests.Mean(i2.Thumb) < 0.2 * nb && TimelineTests.Mean(b2.Thumb) > 0.85 * nb,
                $"fade shown: {TimelineTests.Mean(o2.Thumb):0} / {TimelineTests.Mean(i2.Thumb):0} at the join, {TimelineTests.Mean(b2.Thumb):0} before, normal {nb:0}");
        }
        else H.Check(false, "fade frames shown");
        var gapFrames = r2.Frames.Where(f => f.Out > 4 + fd / 2 && f.Out < 5 - fd / 2).ToList();
        H.Check(gapFrames.Count > 25 && gapFrames.All(f => double.IsNaN(f.Time) && TimelineTests.Mean(f.Thumb) < 2), $"gap shown black: {gapFrames.Count} frames");
        if (At(r2, 150) is Shown after) H.Check(TimelineTests.Closest(after.Thumb, src, 210) == 210, "after the gap: source 210");
        SoundMatches("fade + gap", info, tl2, r2);

        // 3. The audio track: a WAV over the video and an MP3 past its end (black under it).
        string tone = Path.Combine(H.Vids, "s1_tone.wav"), low = Path.Combine(H.Vids, "s2_low.mp3");
        var tl3 = TimelineTests.Placed(info, new[] { new Clip(1, 0, 4, 0, TimelineTests.Cut) },
            new[] { new AudioClip(10, tone, 0, 3, 1, 3, 0.5, 0.5), new AudioClip(11, low, 1, 3, 4.5, 4) });
        var r3 = Play(counter, EditList.Of(tl3), 0, false, 8);
        var (miss3, gap3) = Smoothness(r3, tl3.End);
        H.Check(miss3 <= 1 && gap3 < 2.6 * fd && r3.Frames.Last().Out > 6.4, $"sound track: {miss3} missed, played to {r3.Frames.Last().Out:0.##} s");
        var pre3 = Left(r3.Sound);
        H.Check(TimelineTests.Tone(pre3, 2, 3, 1000) > 2500 && TimelineTests.Tone(pre3, 5, 6.3, 300) > 2500, "sound track heard: the WAV, then the MP3");
        SoundMatches("sound track", info, tl3, r3);

        // 4. Starting inside the crossfade plays on from there.
        var r4 = Play(counter, EditList.Of(tl1), 4.1, false, 3);
        H.Check(r4.Frames.Count > 30 && Math.Abs(r4.Frames.First().Out - 4.1) < fd, $"start inside a crossfade: first frame at {r4.Frames.FirstOrDefault()?.Out:0.###}");

        // 5. Looping a short timeline with a gap: seamless, round and round.
        var tl5 = TimelineTests.Placed(info, new[] { new Clip(1, 1, 1.8, 0, TimelineTests.Cross(0.25)), new Clip(2, 2.2, 3, 0.8, TimelineTests.Cut), new Clip(3, 5, 5.5, 2, TimelineTests.Cut) });
        var r5 = Play(counter, EditList.Of(tl5), 0, true, tl5.End * 2.6);
        int wraps = r5.Frames.Zip(r5.Frames.Skip(1)).Count(p => Math.Floor(p.Second.Out / tl5.End + 1e-6) > Math.Floor(p.First.Out / tl5.End + 1e-6));
        double gap5 = r5.Frames.Zip(r5.Frames.Skip(1), (a, b) => b.Wall - a.Wall).Skip(3).DefaultIfEmpty(0).Max();
        H.Check(wraps >= 2 && gap5 < 2.6 * fd && r5.MinPending > MediaSource.AudioRate / 20,
            $"loop: {wraps} wraps, longest wait {gap5 * 1000:0} ms, sound queued at least {r5.MinPending * 1000.0 / 48000:0} ms");

        // 6. 4K HEVC HDR: two decoders and the blend keep up.
        string hdr = Path.Combine(H.Vids, "t2_iphone_like.mov");
        using (var hs = new MediaSource(hdr, decoders: false))
        {
            var tl6 = TimelineTests.Packed(hs.Info, (0, 2, TimelineTests.Cut), (3, 5, TimelineTests.Cross(0.5)), (6, 8, TimelineTests.Cut));
            var r6 = Play(hdr, EditList.Of(tl6), 0, false, 8);
            var (miss6, gap6) = Smoothness(r6, tl6.End);
            H.Check(miss6 <= 3 && gap6 < 3 * hs.Info.FrameDuration, $"4K HDR: {r6.Frames.Count} frames, {miss6} missed, longest wait {gap6 * 1000:0} ms");
            using var me = Process.GetCurrentProcess();
            me.Refresh();
            Console.WriteLine($"    test process peak memory after 4K: {me.PeakWorkingSet64 / 1e6:0} MB");
        }

        // 7. No sound, 60 fps: every kind of join; the sound card plays silence and keeps the clock.
        string mkv = Path.Combine(H.Vids, "t5_noaudio.mkv");
        using (var ns = new MediaSource(mkv, decoders: false))
        {
            var tl7 = TimelineTests.Packed(ns.Info, (0, 1, TimelineTests.Cross(0.25)), (1.5, 2.5, TimelineTests.Cut), (3, 4, TimelineTests.Dip(1)), (4.5, 6, TimelineTests.Cut));
            var r7 = Play(mkv, EditList.Of(tl7), 0, false, 7);
            var (miss7, gap7) = Smoothness(r7, tl7.End);
            H.Check(miss7 <= 2 && gap7 < 3 * ns.Info.FrameDuration && r7.Sound.All(x => x == 0),
                $"no sound, 60 fps: {r7.Frames.Count} frames, {miss7} missed, longest wait {gap7 * 1000:0} ms, silent");

            // 8. Only sound: no clips, a sound file. Black throughout, the sound plays.
            var tl8 = TimelineTests.Placed(ns.Info, Array.Empty<Clip>(), new[] { new AudioClip(10, tone, 0, 2, 0.5, 3) });
            var r8 = Play(mkv, EditList.Of(tl8), 0, false, 4);
            H.Check(r8.Frames.Count > 100 && r8.Frames.All(f => double.IsNaN(f.Time)) && TimelineTests.Tone(Left(r8.Sound), 1, 2, 1000) > 2500,
                $"sound only: {r8.Frames.Count} black frames, the sound heard");
        }

        // 9. Not looping, nothing sounds after the end: started past the last video clip (the next round's first clip would
        // come in at the end), and past the last sound (the next round's first sound would).
        var r9 = Play(counter, EditList.Of(tl3), 4.6, false, 3);
        var pre9 = Left(r9.Sound);
        double end9 = tl3.End - 4.6;
        var tl9 = TimelineTests.Placed(info, new[] { new Clip(1, 0, 4, 0, TimelineTests.Cut) }, new[] { new AudioClip(10, tone, 0, 1, 0, 3) });
        var r9b = Play(counter, EditList.Of(tl9), 2, false, 3);
        var pre9b = Left(r9b.Sound);
        double after9 = TimelineTests.Rms(pre9, end9 + 0.05, 99), after9b = TimelineTests.Rms(pre9b, 2.05, 99);
        H.Check(TimelineTests.Tone(pre9, 0.3, end9 - 0.2, 300) > 2500 && after9 < 30 && after9b < 30,
            $"nothing after the end: {after9:0} past the audio track ({pre9.Length / 48000.0 - end9:0.###} s heard after it), {after9b:0} past the video ({pre9b.Length / 48000.0 - 2:0.###} s)");
    }
}
