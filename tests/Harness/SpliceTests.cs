using System.Drawing;
using System.Diagnostics;

namespace Splicia;

/// <summary>
/// Spliced exports: which source frame ends up at each output frame (straight cuts, crossfades, fades through black),
/// in one ffmpeg run and split into runs; lengths and sound; rotation and crop; 4K HDR to HEVC and H.264; no sound at
/// 60 fps; many parts (runs, and the graph in a file); memory against a plain export; and the bookkeeping (parts, joins,
/// runs, simplification).
/// </summary>
static class SpliceTests
{
    const int FW = 160, FH = 90, FS = FW * FH;

    /// <summary>All frames of a video, decoded small and grey, for telling frames apart.</summary>
    static byte[][] Frames(string file)
    {
        string raw = Path.Combine(H.Out, Path.GetFileNameWithoutExtension(file) + ".gray");
        H.Ffmpeg($"-v error -y -i \"{file}\" -vf scale={FW}:{FH},format=gray -fps_mode passthrough -f rawvideo \"{raw}\"");
        var all = File.ReadAllBytes(raw);
        File.Delete(raw);
        return Enumerable.Range(0, all.Length / FS).Select(i => all.AsSpan(i * FS, FS).ToArray()).ToArray();
    }

    static double Diff(byte[] a, byte[] b) { long s = 0; for (int i = 0; i < FS; i++) s += Math.Abs(a[i] - b[i]); return s / (double)FS; }
    static double Mean(byte[] a) => a.Average(x => (double)x);

    /// <summary>The source frame (within ±3 of the expected one) that output frame <paramref name="k"/> looks most like.</summary>
    static int Closest(byte[][] outF, byte[][] src, int k, int expected) =>
        Enumerable.Range(Math.Max(0, expected - 3), 7).Where(j => j < src.Length).MinBy(j => Diff(outF[k], src[j]));

    static double Duration(string file, string stream) =>
        double.TryParse(H.Probe(file, $"-select_streams {stream} -show_entries stream=duration -of csv=p=0"), out double d) ? d : -1;

    static string FrameCount(string file, string stream = "v") => H.Probe(file, $"-select_streams {stream} -show_entries stream=nb_frames -of csv=p=0").Trim();

    /// <summary>Exports; returns ffmpeg's peak memory (all ffmpeg processes, sampled).</summary>
    static long Export(TrimJob job, ExportSettings s)
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

    /// <summary>
    /// True when the picture's decode times only go forwards, a frame at a time: what a join of separately encoded files
    /// gets wrong when one of them has B-frames' early decode times and the next doesn't.
    /// </summary>
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

    static Splice Sp(double from, double to, TransitionKind k = TransitionKind.None, double sec = 0.5) => new(from, to, new Transition(k, sec));

    static bool NoLeftovers() => Directory.GetFileSystemEntries(H.Out, ".splicia-*").Length == 0;

    public static void Run()
    {
        Bookkeeping();
        var src = Frames(Path.Combine(H.Vids, "t7_counter.mp4"));
        Console.WriteLine($"  source: {src.Length} frames");
        Counter(src, null);
        Counter(src, 1); // every part a run of its own, the crossfade split between two runs
        OneFrame(src);
        BoomerangShortPiece(src);
        Others();
        Memory();
    }

    static void Bookkeeping()
    {
        using var ms = new MediaSource(Path.Combine(H.Vids, "t7_counter.mp4"), decoders: false);
        var info = ms.Info;
        double fd = info.FrameDuration;

        var job = new TrimJob(info, 1, 9, 0) { Splices = new[] { Sp(3, 4), Sp(6, 7, TransitionKind.Crossfade, 0.5) } };
        var (parts, joins) = job.Pieces();
        H.Check(parts.Count == 3 && parts[0] == (1, 3) && parts[1] == (4, 6) && parts[2] == (7, 9), $"parts {string.Join(" ", parts)}");
        H.Check(joins.Count == 2 && joins[1].Kind == TransitionKind.Crossfade, "one join per meeting point, with its splice's transition");
        H.Check(Math.Abs(job.OutputLength() - 5.5) < 1e-9, $"length with a 0.5 s crossfade: {job.OutputLength()}");

        var fade = new TrimJob(info, 1, 9, 0) { Splices = new[] { Sp(3, 4, TransitionKind.FadeThroughBlack, 1) } };
        var fj = fade.Joins().Single();
        H.Check(fj.FadeOut == 0.5 && fj.FadeIn == 0.5 && fj.Overlap == 0 && Math.Abs(fade.OutputLength() - 7) < 1e-9, "a fade through black splits its time and keeps the length");

        var tight = new TrimJob(info, 1, 9, 0) { Splices = new[] { Sp(1.4, 8.8, TransitionKind.Crossfade, 2) } };
        var tj = tight.Joins().Single();
        H.Check(Math.Abs(tj.Overlap - 0.1) < 1e-9, $"a crossfade is at most half of the shorter part: {tj.Overlap}");
        var odd = new TrimJob(info, 0, 10, 0) { Splices = new[] { Sp(5, 6, TransitionKind.Crossfade, 0.25) } };
        double frames = odd.Joins().Single().Overlap / fd;
        H.Check(Math.Abs(frames - 7) < 1e-6, $"a 0.25 s crossfade at 30 fps is 7 whole frames: {frames:0.###}");

        var edges = new TrimJob(info, 1, 9, 0) { Splices = new[] { Sp(0, 2), Sp(8, 10) } };
        var simple = edges.Simplified();
        H.Check(!edges.IsSpliced && simple.Start == 2 && simple.End == 8 && simple.Splices.Count == 0, "splices at the edges only shorten the selection");
        H.Check(Exporter.OriginalUnavailableReason(simple) == null, "... so Original (copy) stays available");
        H.Check(Exporter.OriginalUnavailableReason(job) != null, "Original is unavailable when parts are joined");

        var all = new TrimJob(info, 1, 9, 0) { Splices = new[] { Sp(0.5, 9.5) } };
        H.Check(all.Pieces().Parts.Count == 0, "nothing is left when a splice covers the selection");

        var outside = new TrimJob(info, 2, 5, 0) { Splices = new[] { Sp(6, 7), Sp(0, 1) } };
        H.Check(!outside.IsSpliced && outside.Simplified().Start == 2 && outside.Simplified().End == 5, "splices outside the selection are ignored");

        var sliver = new TrimJob(info, 1, 9, 0) { Splices = new[] { Sp(3, 4), Sp(4 + fd * 0.3, 5) } };
        H.Check(sliver.Pieces().Parts.Count == 2, "a sliver under half a frame between two splices is dropped");

        // Runs: consecutive parts, at most so many each (a run may start or end inside a crossfade).
        var six = new TrimJob(info, 0, 10, 0)
        {
            Splices = new[] { Sp(1, 1.5, TransitionKind.Crossfade), Sp(2.5, 3), Sp(4, 4.5, TransitionKind.FadeThroughBlack), Sp(5.5, 6), Sp(6.5, 7, TransitionKind.Crossfade) },
        };
        string Runs(int? max) { Exporter.MaxPartsPerRunOverride = max; var r = Exporter.Runs(six); Exporter.MaxPartsPerRunOverride = null; return string.Join(" ", r); }
        H.Check(Runs(5) == "(0, 5) (5, 1)", $"runs of at most 5: {Runs(5)}");
        H.Check(Runs(1) == "(0, 1) (1, 1) (2, 1) (3, 1) (4, 1) (5, 1)", $"runs of 1, even across crossfades: {Runs(1)}");
        H.Check(Runs(24) == "(0, 6)", $"one run when they fit: {Runs(24)}");
    }

    /// <summary>Frame-by-frame checks on the numbered clip, in one run (<paramref name="maxPerRun"/> null) or split into runs.</summary>
    static void Counter(byte[][] src, int? maxPerRun)
    {
        string file = Path.Combine(H.Vids, "t7_counter.mp4");
        string tag = maxPerRun == null ? "one run" : "in runs";
        using var ms = new MediaSource(file, decoders: false);
        Exporter.MaxPartsPerRunOverride = maxPerRun;
        try
        {
            // 1. A straight cut at 3–4 s and a 0.5 s crossfade at 6–7 s, selection 1–9 s: parts [1,3) [4,6) [7,9) → 5.5 s.
            string out1 = Path.Combine(H.Out, $"splice1_cut_crossfade_{maxPerRun}.mp4");
            Export(new TrimJob(ms.Info, 1, 9, 0) { Splices = new[] { Sp(3, 4), Sp(6, 7, TransitionKind.Crossfade, 0.5) } },
                new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out1));
            var o1 = Frames(out1);
            H.Check(o1.Length == 165, $"{tag}, cut + crossfade: {o1.Length} frames (expected 165)");
            foreach (var (k, j) in new[] { (0, 30), (59, 89), (60, 120), (104, 164), (120, 225), (164, 269) })
                if (k < o1.Length) H.Check(Closest(o1, src, k, j) == j, $"{tag}, cut + crossfade: output frame {k} is source frame {Closest(o1, src, k, j)} (expected {j})");
            if (o1.Length > 112)
            {
                // Middle of the crossfade: a mix of the part that ends (frame 172) and the one that starts (frame 217).
                var mix = src[172].Zip(src[217], (x, y) => (byte)((x + y + 1) / 2)).ToArray();
                double dMix = Diff(o1[112], mix), dA = Diff(o1[112], src[172]), dB = Diff(o1[112], src[217]);
                H.Check(dMix < 0.6 * Math.Min(dA, dB), $"{tag}, crossfade middle is a mix: diff to mix {dMix:0.0}, to either side {dA:0.0} / {dB:0.0}");
            }
            double v1 = Duration(out1, "v:0"), a1 = Duration(out1, "a:0");
            H.Check(Math.Abs(v1 - 5.5) < 0.04 && Math.Abs(a1 - 5.5) < 0.06, $"{tag}, cut + crossfade lengths: video {v1}, sound {a1} (expected 5.5)");

            // 2. A 1 s fade through black at 3–4 s and a straight cut at 6–7 s: 6 s, dark around the first join.
            string out2 = Path.Combine(H.Out, $"splice2_fade_cut_{maxPerRun}.mp4");
            Export(new TrimJob(ms.Info, 1, 9, 0) { Splices = new[] { Sp(3, 4, TransitionKind.FadeThroughBlack, 1), Sp(6, 7) } },
                new ExportSettings(ExportFormat.H264, ExportQuality.High, true, out2));
            var o2 = Frames(out2);
            H.Check(o2.Length == 180, $"{tag}, fade + cut: {o2.Length} frames (expected 180)");
            foreach (var (k, j) in new[] { (0, 30), (30, 60), (90, 150), (119, 179), (120, 210), (179, 269) })
                if (k < o2.Length) H.Check(Closest(o2, src, k, j) == j, $"{tag}, fade + cut: output frame {k} is source frame {Closest(o2, src, k, j)} (expected {j})");
            if (o2.Length == 180)
            {
                double normal = Mean(o2[30]), lastOut = Mean(o2[59]), firstIn = Mean(o2[60]), before = Mean(o2[44]), after = Mean(o2[75]);
                H.Check(lastOut < 0.2 * normal && firstIn < 0.2 * normal, $"{tag}, dark at the join: {lastOut:0} / {firstIn:0} vs normal {normal:0}");
                H.Check(before > 0.85 * normal && after > 0.85 * normal, $"{tag}, full brightness outside the fade: {before:0} / {after:0} vs {normal:0}");
            }
            double v2 = Duration(out2, "v:0"), a2 = Duration(out2, "a:0");
            H.Check(Math.Abs(v2 - 6) < 0.04 && Math.Abs(a2 - 6) < 0.06, $"{tag}, fade + cut lengths: video {v2}, sound {a2} (expected 6)");
            H.Check(NoLeftovers(), $"{tag}: no work files left");

            if (maxPerRun != null) return;
            // 3. Rotated and cropped too: rotated output size, 2 s + 5 s − 1 s crossfade.
            string out3 = Path.Combine(H.Out, "splice3_rotated_cropped.mp4");
            var job3 = new TrimJob(ms.Info, 1, 9, 90) { Crop = new RectangleF(0, 0.25f, 1, 0.5f), Splices = new[] { Sp(3, 4, TransitionKind.Crossfade, 1) } };
            Export(job3, new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, out3));
            string dims = H.Probe(out3, "-select_streams v -show_entries stream=width,height,nb_frames -of compact=p=0");
            var (w3, h3) = job3.OutputSize();
            H.Check(dims.Contains($"width={w3}|height={h3}") && dims.Contains("nb_frames=180"), $"rotated + cropped: {dims} (expected {w3}x{h3}, 180 frames)");
        }
        finally { Exporter.MaxPartsPerRunOverride = null; }
    }

    /// <summary>A kept part one frame long (frames 10–19 and 21–30 cut), with and without sound, in one run and in runs.</summary>
    static void OneFrame(byte[][] src)
    {
        foreach (var (file, fps, sound) in new[] { ("t7_counter.mp4", 30.0, true), ("t5_noaudio.mkv", 60.0, false) })
        {
            using var ms = new MediaSource(Path.Combine(H.Vids, file), decoders: false);
            var job = new TrimJob(ms.Info, 0, 2, 0) { Splices = new[] { Sp(10 / fps, 20 / fps), Sp(21 / fps, 31 / fps) } };
            int expected = (int)Math.Round(2 * fps) - 20;
            foreach (int? max in new int?[] { null, 1 })
            {
                string outFile = Path.Combine(H.Out, $"splice_oneframe_{Path.GetFileNameWithoutExtension(file)}_{max}.mp4");
                Exporter.MaxPartsPerRunOverride = max;
                try
                {
                    Export(job, new ExportSettings(ExportFormat.H264, ExportQuality.High, true, outFile));
                    string tag = $"one-frame part, {file}, {(max == null ? "one run" : "in runs")}";
                    string n = FrameCount(outFile);
                    H.Check(n == expected.ToString(), $"{tag}: {n} frames (expected {expected})");
                    if (max != null) H.Check(DtsClean(outFile, 1 / fps, out string d), $"{tag}: decode times step a frame at a time ({d})");
                    if (sound)
                    {
                        var o = Frames(outFile);
                        H.Check(o.Length > 12 && Closest(o, src, 10, 20) == 20 && Closest(o, src, 11, 31) == 31, $"{tag}: frames 10, 11 are source 20, 31");
                        double a = Duration(outFile, "a:0");
                        H.Check(Math.Abs(a - expected / fps) < 0.06, $"{tag}: sound {a:0.###} s (expected {expected / fps:0.###})");
                    }
                }
                catch (Exception ex) { H.Check(false, $"one-frame part {file}: {ex.Message}"); }
                finally { Exporter.MaxPartsPerRunOverride = null; }
            }
        }
    }

    /// <summary>
    /// A back-and-forth loop whose backwards half ends in a one-frame piece (220 frames: 217 per piece at 720p, so 218
    /// backwards frames make 217 + 1): every frame in order, and clean decode times where the pieces join.
    /// </summary>
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
                if (k < o.Length) H.Check(Closest(o, src, k, j) == j, $"boomerang: output frame {k} is source frame {Closest(o, src, k, j)} (expected {j})");
            H.Check(DtsClean(outFile, 1 / 30.0, out string d), $"boomerang: decode times step a frame at a time ({d})");
        }
        catch (Exception ex) { H.Check(false, $"boomerang: {ex.Message}"); }
    }

    static void Others()
    {
        // 4K HEVC HDR (HLG), portrait by rotation tag: crossfade + fade, HEVC keeps HDR.
        string hdr = Path.Combine(H.Vids, "t2_iphone_like.mov");
        using (var ms = new MediaSource(hdr, decoders: false))
        {
            string out4 = Path.Combine(H.Out, "splice4_hdr.mp4");
            var job = new TrimJob(ms.Info, 0.5, 7.5, 0) { Splices = new[] { Sp(2, 3, TransitionKind.Crossfade, 0.5), Sp(5, 6, TransitionKind.FadeThroughBlack, 0.5) } };
            try
            {
                Export(job, new ExportSettings(ExportFormat.Hevc, ExportQuality.Balanced, true, out4));
                string p = H.Probe(out4, "-select_streams v -show_entries stream=width,height,color_transfer,pix_fmt -of compact=p=0");
                double v = Duration(out4, "v:0"), a = Duration(out4, "a:0");
                H.Check(p.Contains("width=2160|height=3840") && p.Contains("arib-std-b67") && p.Contains("10le"), $"HDR: {p}");
                H.Check(Math.Abs(v - job.OutputLength()) < 0.02 && Math.Abs(a - job.OutputLength()) < 0.06, $"HDR lengths: video {v}, sound {a} (expected {job.OutputLength()})");
            }
            catch (Exception ex) { H.Check(false, $"HDR export: {ex.Message}"); }

            // HDR to H.264 (tonemapped) with a straight cut.
            string out5 = Path.Combine(H.Out, "splice5_hdr_to_h264.mp4");
            try
            {
                Export(new TrimJob(ms.Info, 0.5, 4.5, 0) { Splices = new[] { Sp(2, 3) } }, new ExportSettings(ExportFormat.H264, ExportQuality.Small, true, out5));
                string p = H.Probe(out5, "-select_streams v -show_entries stream=pix_fmt,color_transfer,nb_frames -of compact=p=0");
                H.Check(p.Contains("yuv420p|") && p.Contains("bt709") && p.Contains("nb_frames=90"), $"HDR → H.264: {p}");
            }
            catch (Exception ex) { H.Check(false, $"HDR → H.264 export: {ex.Message}"); }
        }

        // 60 fps 4:4:4 without sound: every kind of join, in one run and in runs.
        string mkv = Path.Combine(H.Vids, "t5_noaudio.mkv");
        using (var ms = new MediaSource(mkv, decoders: false))
        {
            var job = new TrimJob(ms.Info, 0, 6, 0) { Splices = new[] { Sp(1, 1.5, TransitionKind.Crossfade, 0.25), Sp(2.5, 3), Sp(4, 4.5, TransitionKind.FadeThroughBlack, 2) } };
            int expected = (int)Math.Round(job.OutputLength() * 60);
            foreach (int? max in new int?[] { null, 1 })
            {
                string out6 = Path.Combine(H.Out, $"splice6_noaudio_{max}.mp4");
                Exporter.MaxPartsPerRunOverride = max;
                try
                {
                    Export(job, new ExportSettings(ExportFormat.H264, ExportQuality.Balanced, true, out6));
                    string p = H.Probe(out6, "-show_entries stream=codec_type,nb_frames -of compact=p=0");
                    H.Check(!p.Contains("audio") && p.Contains($"nb_frames={expected}"), $"no sound, 60 fps ({(max == null ? "one run" : "in runs")}): {p} (expected {expected} frames)");
                }
                catch (Exception ex) { H.Check(false, $"no-sound export: {ex.Message}"); }
                finally { Exporter.MaxPartsPerRunOverride = null; }
            }
        }

        // Many parts: 85 four-frame parts of a 1080p clip, then the last five frames. By default in runs of 24; forced
        // into one run, the command is long enough that the graph goes in a file. Either way no work files are left.
        string longClip = Path.Combine(H.Vids, "t1_h264_1080p30.mp4");
        using (var ms = new MediaSource(longClip, decoders: false))
        {
            var cuts = Enumerable.Range(0, 85).Select(k => Sp((7 * k + 4) / 30.0, (7 * k + 7) / 30.0)).ToArray();
            var job = new TrimJob(ms.Info, 0, 20, 0) { Splices = cuts };
            H.Check(Exporter.Runs(job).Count == 4, $"86 parts of 1080p: {Exporter.Runs(job).Count} runs");
            foreach (int? max in new int?[] { null, 100 })
            {
                string out8 = Path.Combine(H.Out, $"splice8_many_parts_{max}.mp4");
                Exporter.MaxPartsPerRunOverride = max;
                try
                {
                    Export(job, new ExportSettings(ExportFormat.H264, ExportQuality.Small, true, out8));
                    string f = FrameCount(out8);
                    double a = Duration(out8, "a:0");
                    H.Check(f == "345" && Math.Abs(a - 345 / 30.0) < 0.06 && NoLeftovers(),
                        $"86 parts ({(max == null ? "in runs" : "one run, graph in a file")}): {f} frames (expected 345), sound {a:0.###} s, work files removed: {NoLeftovers()}");
                    H.Check(DtsClean(out8, 1 / 30.0, out string d), $"86 parts: decode times step a frame at a time ({d})");
                }
                catch (Exception ex) { H.Check(false, $"many parts: {ex.Message}"); }
                finally { Exporter.MaxPartsPerRunOverride = null; }
            }
        }

        // Splices that only shorten the selection export as a plain copy.
        string counter = Path.Combine(H.Vids, "t7_counter.mp4");
        using (var ms = new MediaSource(counter, decoders: false))
        {
            string out7 = Path.Combine(H.Out, "splice7_edges_copy.mp4");
            try
            {
                Export(new TrimJob(ms.Info, 1, 9, 0) { Splices = new[] { Sp(0, 3), Sp(7, 10) } }, new ExportSettings(ExportFormat.Original, ExportQuality.High, true, out7));
                double v = Duration(out7, "v:0");
                H.Check(v > 3.9 && v < 5.6, $"edges-only splices copy the middle 3–7 s (keyframe aligned): {v}");
            }
            catch (Exception ex) { H.Check(false, $"edges-only copy: {ex.Message}"); }
        }
    }

    /// <summary>ffmpeg's peak memory for 4K HDR: six parts against a plain export of the same video, H.264 and HEVC.</summary>
    static void Memory()
    {
        string hdr = Path.Combine(H.Vids, "t2_iphone_like.mov");
        using var ms = new MediaSource(hdr, decoders: false);
        var six = new[] { Sp(1, 1.5, TransitionKind.Crossfade, 0.5), Sp(2.5, 3), Sp(4, 4.5, TransitionKind.FadeThroughBlack, 0.5), Sp(5.5, 6), Sp(6.5, 7, TransitionKind.Crossfade, 0.25) };
        foreach (var fmt in new[] { ExportFormat.H264, ExportFormat.Hevc })
        {
            string outPlain = Path.Combine(H.Out, $"mem_plain_{fmt}.mp4"), outSix = Path.Combine(H.Out, $"mem_six_{fmt}.mp4");
            var sixJob = new TrimJob(ms.Info, 0, 8, 0) { Splices = six };
            long plain = Export(new TrimJob(ms.Info, 0, 8, 0), new ExportSettings(fmt, ExportQuality.Small, true, outPlain));
            long spliced = Export(sixJob, new ExportSettings(fmt, ExportQuality.Small, true, outSix));
            double v = Duration(outSix, "v:0");
            H.Check(Math.Abs(v - sixJob.OutputLength()) < 0.02, $"4K {fmt}, six parts: {v} s (expected {sixJob.OutputLength():0.###})");
            H.Check(spliced < plain + 1_800_000_000L, $"4K {fmt}: six parts take {spliced / 1e6:0} MB, a plain export {plain / 1e6:0} MB");
            if (fmt == ExportFormat.H264) H.Check(plain < 3_500_000_000L, $"4K HDR → H.264 plain export under 3.5 GB: {plain / 1e6:0} MB (was 5 GB in 1.0.5)");
            if (fmt != ExportFormat.Hevc) continue;
            // Eight parts, every join a 0.2 s crossfade: still split into runs, so memory stays bounded.
            var chain = new TrimJob(ms.Info, 0, 8, 0) { Splices = Enumerable.Range(1, 7).Select(k => Sp(k - 0.2, k, TransitionKind.Crossfade, 0.2)).ToArray() };
            string outChain = Path.Combine(H.Out, "mem_chain_Hevc.mp4");
            long chained = Export(chain, new ExportSettings(fmt, ExportQuality.Small, true, outChain));
            double cv = Duration(outChain, "v:0"), ca = Duration(outChain, "a:0");
            H.Check(Exporter.Runs(chain).Count == 2 && chained < plain + 1_800_000_000L && Math.Abs(cv - chain.OutputLength()) < 0.02 && Math.Abs(ca - chain.OutputLength()) < 0.06,
                $"4K, eight crossfaded parts: {Exporter.Runs(chain).Count} runs, {chained / 1e6:0} MB, video {cv} / sound {ca} s (expected {chain.OutputLength():0.###})");
        }
    }
}
