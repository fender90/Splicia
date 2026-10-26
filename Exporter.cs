using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Splicia;

enum ExportFormat { H264, Hevc, Original }
enum ExportQuality { High, Balanced, Small }

/// <param name="Boomerang">Back-and-forth loop: the selection forwards, then backwards, <paramref name="Repeats"/> times.</param>
sealed record ExportSettings(ExportFormat Format, ExportQuality Quality, bool IncludeAudio, string OutputPath,
    bool Boomerang = false, int Repeats = 1);

/// <summary>A trim of one file: the selected range, the extra clockwise rotation and an optional crop.</summary>
sealed record TrimJob(MediaInfo Info, double Start, double End, int Rotation)
{
    public double Length => End - Start;
    /// <summary>Rotation the result is shown with (the file's own plus the user's).</summary>
    public int TotalRotation => (Info.Rotation + Rotation) % 360;

    /// <summary>Part of the picture to keep, as fractions (0..1) of the picture as shown, after rotation. Null = all.</summary>
    public RectangleF? Crop { get; init; }

    /// <summary>Size of the exported picture (display pixels: rotated, cropped, even).</summary>
    public (int W, int H) OutputSize()
    {
        var (w, h) = Info.DisplaySize(Rotation);
        if (Crop is RectangleF c) { w = (int)(w * c.Width); h = (int)(h * c.Height); }
        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
    }
}

/// <summary>Runs ffmpeg.exe to write the trimmed / rotated file.</summary>
static class Exporter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string S(double v) => v.ToString("0.######", Inv);

    // ───────────────────────── capabilities ─────────────────────────

    /// <summary>Containers that can carry a rotation flag, so "Original" export can rotate without re-encoding.</summary>
    static readonly string[] RotatableExtensions = { ".mp4", ".m4v", ".mov", ".3gp", ".3g2", ".mkv", ".webm" };

    public static string OriginalExtension(MediaInfo info)
    {
        string ext = Path.GetExtension(info.Path).ToLowerInvariant();
        return ext.Length > 1 ? ext : ".mkv";
    }

    /// <summary>Why "Original" (stream copy) isn't possible for this job, or null.</summary>
    public static string? OriginalUnavailableReason(TrimJob job)
    {
        if (job.Crop != null) return "Cropping needs re-encoding: choose H.264 or HEVC.";
        if (job.Rotation != 0 && !RotatableExtensions.Contains(OriginalExtension(job.Info)))
            return $"{OriginalExtension(job.Info).TrimStart('.').ToUpperInvariant()} files can't store a rotation, so rotating needs re-encoding.";
        return null;
    }

    static readonly Lazy<Task<HwEncoder?>> Hw = new(() => Task.Run(ProbeHardware));

    /// <summary>
    /// Hardware HEVC encoder ffmpeg can use here (null = none; the software encoder is used). Probed once, on first use
    /// (not at startup: waking a laptop's discrete GPU for nothing can stall the system).
    /// </summary>
    public static Task<HwEncoder?> HardwareHevc => Hw.Value;

    public sealed record HwEncoder(string Codec, string Vendor, bool TenBit);

    static HwEncoder? ProbeHardware()
    {
        foreach (var (codec, vendor) in new[] { ("hevc_nvenc", "NVIDIA"), ("hevc_qsv", "Intel"), ("hevc_amf", "AMD") })
        {
            if (!TryEncode(codec, "nv12")) continue;
            return new HwEncoder(codec, vendor, TryEncode(codec, "p010le"));
        }
        return null;
    }

    static bool TryEncode(string codec, string pixFmt)
    {
        try
        {
            var psi = Psi($"-hide_banner -loglevel error -nostdin -f lavfi -i color=c=black:s=640x360:r=30 -frames:v 3 -pix_fmt {pixFmt} -c:v {codec} -f null -");
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    static ProcessStartInfo Psi(string args) => new(FFmpegRuntime.FFmpegExe, args)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        WorkingDirectory = FFmpegRuntime.Directory,
    };

    // ───────────────────────── command line ─────────────────────────

    // Selection edges sit exactly on frame timestamps, and containers store those rounded (Matroska to whole
    // milliseconds), so a cut placed right on a frame can land on either side of it. Re-encodes therefore cut halfway
    // between frames: the seek point moves back half a frame and the length stays, which puts the end cut halfway too.
    // Copies seek half a millisecond late instead, so a start that is exactly a keyframe doesn't pull in the previous one.
    const double Nudge = 0.0005;

    static double CutPoint(MediaInfo info, double t) => Math.Max(0, t - info.FrameDuration / 2);

    // ffmpeg decodes with one thread per CPU core by default, each holding frames: gigabytes for 4K on a big CPU.
    // Eight keep decoding well ahead of the encoder.
    static readonly string[] DecoderThreads = { "-threads", "8" };

    /// <summary>
    /// Input options and trim filters that select exactly [<paramref name="from"/>, <paramref name="to"/>): the input
    /// stops a frame after the end (input-side -t, so a reverse filter sees no more than that), and trim / atrim cut
    /// halfway between frames. Filter timestamps count from the seek point.
    /// </summary>
    static (string[] Input, string VideoTrim, string AudioTrim) Window(MediaInfo info, double from, double to)
    {
        double seek = CutPoint(info, from), start = from - seek, end = to - seek;
        string[] input = { "-ss", S(seek), "-t", S(end + info.FrameDuration) };
        return (input, $"trim=end={S(end)},setpts=PTS-STARTPTS", $"atrim=start={S(start)}:end={S(end)},asetpts=PTS-STARTPTS");
    }

    static List<string> Common() => new() { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };

    /// <summary>How the picture is re-encoded: the filter chain, the encoder arguments, and bytes per pixel of the frames.</summary>
    sealed record VideoEncoding(List<string> Filters, List<string> Codec, double BytesPerPixel, bool Hevc);

    static VideoEncoding EncodingFor(TrimJob job, ExportSettings s, HwEncoder? hw)
    {
        var info = job.Info;
        bool hevc = s.Format == ExportFormat.Hevc;
        bool keepHdr = hevc && info.IsHdr;
        bool tenBit = hevc && (info.BitDepth > 8 || info.IsHdr);
        bool useHw = hevc && hw != null && (!tenBit || hw.TenBit);

        // ffmpeg applies the file's own rotation itself (autorotate); the user's goes in the filter chain.
        var filters = new List<string>();
        if (info.IsHdr && !keepHdr)
            // HDR → SDR (BT.709) so the H.264 file doesn't look washed out.
            filters.Add("zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
        switch (job.Rotation)
        {
            case 90: filters.Add("transpose=clock"); break;
            case 180: filters.Add("hflip,vflip"); break;
            case 270: filters.Add("transpose=cclock"); break;
        }
        // The crop is in fractions of the picture as shown, so it comes after the rotation. Sizes in stored pixels
        // (iw/ih) keep a non-square pixel aspect right.
        if (job.Crop is RectangleF c)
            filters.Add($"crop=w=trunc(iw*{S(c.Width)}/2)*2:h=trunc(ih*{S(c.Height)}/2)*2:x=trunc(iw*{S(c.X)}):y=trunc(ih*{S(c.Y)})");
        // 4:2:0 needs even dimensions.
        filters.Add("crop=trunc(iw/2)*2:trunc(ih/2)*2");
        filters.Add($"format={(tenBit ? (useHw ? "p010le" : "yuv420p10le") : (useHw ? "nv12" : "yuv420p"))}");

        var a = new List<string>();
        int q = s.Quality switch { ExportQuality.High => 0, ExportQuality.Balanced => 1, _ => 2 };
        if (!hevc)
        {
            a.AddRange(new[] { "-c:v", "libx264", "-preset", q == 0 ? "fast" : "faster", "-crf", new[] { "18", "21", "25" }[q] });
            if (info.IsHdr) a.AddRange(new[] { "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709" });
        }
        else if (useHw)
        {
            string cq = new[] { "21", "25", "29" }[q];
            switch (hw!.Codec)
            {
                case "hevc_nvenc":
                    a.AddRange(new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", cq, "-b:v", "0" });
                    break;
                case "hevc_qsv":
                    a.AddRange(new[] { "-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", cq });
                    break;
                default:
                    a.AddRange(new[] { "-c:v", "hevc_amf", "-quality", "quality", "-rc", "cqp", "-qp_i", cq, "-qp_p", cq, "-qp_b", cq });
                    break;
            }
            if (tenBit) a.AddRange(new[] { "-profile:v", "main10" });
        }
        else
        {
            a.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", new[] { "20", "24", "28" }[q], "-x265-params", "log-level=error" });
        }
        if (hevc) a.AddRange(new[] { "-tag:v", "hvc1" });
        if (keepHdr)
            a.AddRange(new[] { "-color_primaries", info.ColorPrimaries, "-color_trc", info.ColorTransfer, "-colorspace", info.ColorSpace });
        return new VideoEncoding(filters, a, tenBit ? 3 : 1.5, hevc);
    }

    static string AudioBitrate(ExportSettings s) => s.Quality == ExportQuality.Small ? "128k" : "192k";

    /// <summary>The ffmpeg command line for a plain (single-pass) export.</summary>
    public static string Arguments(TrimJob job, ExportSettings s, HwEncoder? hw)
    {
        var info = job.Info;
        var a = Common();
        bool audio = s.IncludeAudio && info.HasAudio;

        if (s.Format == ExportFormat.Original)
        {
            a.AddRange(new[] { "-ss", S(job.Start + Nudge) });
            // Rotation as a flag on the copied stream: -display_rotation replaces the file's own, so give the total.
            if (job.Rotation != 0) a.AddRange(new[] { "-display_rotation:v:0", (-job.TotalRotation).ToString(Inv) });
            a.AddRange(new[] { "-i", info.Path, "-t", S(job.Length), "-map", "0:v:0" });
            if (audio) a.AddRange(new[] { "-map", "0:a?" });
            a.AddRange(new[] { "-c", "copy", "-avoid_negative_ts", "make_zero", "-map_metadata", "0" });
            if (IsMp4Family(s.OutputPath)) a.AddRange(new[] { "-movflags", "+faststart" });
            a.Add(s.OutputPath);
            return Join(a);
        }

        // Re-encode: seeking before -i decodes from the previous keyframe and drops frames before the start, so the cut
        // is frame-exact.
        var enc = EncodingFor(job, s, hw);
        var (input, vtrim, atrim) = Window(info, job.Start, job.End);
        a.AddRange(DecoderThreads);
        a.AddRange(input);
        a.AddRange(new[] { "-i", info.Path, "-map", "0:v:0" });
        if (audio) a.AddRange(new[] { "-map", "0:a?" });
        a.AddRange(new[] { "-vf", string.Join(",", enc.Filters.Prepend(vtrim)) });
        a.AddRange(enc.Codec);
        if (audio) a.AddRange(new[] { "-af", atrim, "-c:a", "aac", "-b:a", AudioBitrate(s) });
        a.AddRange(new[] { "-map_metadata", "0", "-movflags", "+faststart", s.OutputPath });
        return Join(a);
    }

    /// <summary>One video-only piece of a back-and-forth export: [<paramref name="from"/>, <paramref name="to"/>), maybe reversed.</summary>
    static string PieceArguments(TrimJob job, VideoEncoding enc, double from, double to, bool reverse, string output)
    {
        var a = Common();
        // The reverse filter waits for the end of its input, so the input must stop at the piece (see Window).
        var (input, vtrim, _) = Window(job.Info, from, to);
        a.AddRange(DecoderThreads);
        a.AddRange(input);
        a.AddRange(new[] { "-i", job.Info.Path, "-map", "0:v:0", "-an" });
        var filters = new List<string> { vtrim };
        filters.AddRange(enc.Filters);
        if (reverse) filters.Add("reverse"); // last, so it holds frames in the (smallest) output format
        a.AddRange(new[] { "-vf", string.Join(",", filters) });
        a.AddRange(enc.Codec);
        a.Add(output);
        return Join(a);
    }

    /// <summary>
    /// The sound of a back-and-forth export in one file: forwards, then the stretch the backwards picture covers played
    /// backwards, all repeated. (Audio is small, so ffmpeg can hold it in memory.)
    /// </summary>
    static string SoundArguments(TrimJob job, ExportSettings s, double backFrom, double backTo, string output)
    {
        var a = Common();
        // Input-side -t: the output is longer than the selection.
        double seek = CutPoint(job.Info, job.Start), off = job.Start - seek; // timestamps start at the seek point
        a.AddRange(new[] { "-ss", S(seek), "-t", S(job.Length + off + job.Info.FrameDuration), "-i", job.Info.Path, "-vn" });
        var g = new StringBuilder();
        string forwards = $"atrim=start={S(off)}:end={S(off + job.Length)},asetpts=PTS-STARTPTS";
        if (backTo - backFrom > 0.001)
            g.Append($"[0:a:0]asplit=2[s][b];[s]{forwards}[f];[b]atrim=start={S(backFrom - seek)}:end={S(backTo - seek)},asetpts=PTS-STARTPTS,areverse[r];" +
                     "[f][r]concat=n=2:v=0:a=1[c]");
        else
            g.Append($"[0:a:0]{forwards}[c]");
        if (s.Repeats > 1)
        {
            g.Append($";[c]asplit={s.Repeats}");
            for (int i = 0; i < s.Repeats; i++) g.Append($"[c{i}]");
            g.Append(';');
            for (int i = 0; i < s.Repeats; i++) g.Append($"[c{i}]");
            g.Append($"concat=n={s.Repeats}:v=0:a=1[a]");
        }
        else g.Append(";[c]anull[a]");
        a.AddRange(new[] { "-filter_complex", g.ToString(), "-map", "[a]", "-c:a", "aac", "-b:a", AudioBitrate(s), output });
        return Join(a);
    }

    static bool IsMp4Family(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov" or ".3gp" or ".3g2";

    static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
    static string Quote(string a)
    {
        if (a.Length > 0 && !a.Any(c => c is ' ' or '\t' or '"')) return a;
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in a)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
            sb.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }

    // ───────────────────────── running ─────────────────────────

    /// <summary>
    /// Runs the export. <paramref name="progress"/> gets 0..1. Throws <see cref="OperationCanceledException"/> when
    /// cancelled and <see cref="InvalidOperationException"/> with ffmpeg's message on failure; either way the partial file
    /// is deleted.
    /// </summary>
    public static async Task RunAsync(TrimJob job, ExportSettings s, HwEncoder? hw, Action<double> progress, CancellationToken ct)
    {
        try
        {
            if (s.Boomerang) await RunBoomerangAsync(job, s with { Format = s.Format == ExportFormat.Original ? ExportFormat.H264 : s.Format }, hw, progress, ct);
            // Copies start at the keyframe before the start point, so their output runs a little longer.
            else await RunFfmpegAsync(Arguments(job, s, hw), Math.Max(0.001, job.Length), progress, ct);
        }
        catch
        {
            TryDelete(s.OutputPath);
            throw;
        }
    }

    /// <summary>Memory the reverse filter may use for one piece; frames beyond it go into further pieces.</summary>
    const double ReverseBudgetBytes = 300e6;

    /// <summary>
    /// Back-and-forth loop. FFmpeg's reverse filter keeps every frame in memory (gigabytes for a few seconds of 4K), so
    /// the backwards half is made in short pieces: the selection forwards, then the pieces of the backwards part from the
    /// last one to the first, each reversed on its own. The pieces share encoder settings, so they're joined without
    /// re-encoding, repeated as asked, with the sound (made separately) laid underneath. Work files go in a hidden
    /// folder next to the output and are removed afterwards.
    /// </summary>
    static async Task RunBoomerangAsync(TrimJob job, ExportSettings s, HwEncoder? hw, Action<double> progress, CancellationToken ct)
    {
        var info = job.Info;
        double fd = info.FrameDuration;
        var enc = EncodingFor(job, s, hw);

        // Backwards without the selection's first and last frames: they'd otherwise show twice at each turn.
        double backFrom = job.Start + fd, backTo = job.End - fd;
        var pieces = new List<(double From, double To, bool Reverse)> { (job.Start, job.End, false) };
        if (backTo - backFrom > fd * 0.5)
        {
            var (w, h) = job.OutputSize();
            int perPiece = (int)Math.Clamp(ReverseBudgetBytes / (w * (double)h * enc.BytesPerPixel), 8, 240);
            var back = new List<(double, double, bool)>();
            for (double t = backFrom; t < backTo - fd * 0.5; t += perPiece * fd) back.Add((t, Math.Min(t + perPiece * fd, backTo), true));
            back.Reverse();
            pieces.AddRange(back);
        }
        double cycle = pieces.Sum(p => p.To - p.From);
        bool sound = s.IncludeAudio && info.HasAudio;

        string dir = Path.GetDirectoryName(Path.GetFullPath(s.OutputPath))!;
        var work = Directory.CreateDirectory(Path.Combine(dir, $".splicia-{Guid.NewGuid():N}"));
        work.Attributes |= FileAttributes.Hidden;
        try
        {
            // 92% of the bar for the pictures (by duration), then the sound, then the join.
            var names = new List<string>();
            double done = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                var (from, to, reverse) = pieces[i];
                string name = $"piece{i:000}.mp4";
                double before = done, len = to - from;
                await RunFfmpegAsync(PieceArguments(job, enc, from, to, reverse, Path.Combine(work.FullName, name)), len,
                    f => progress(0.92 * (before + f * len) / cycle), ct);
                done += len;
                names.Add(name);
            }
            if (sound)
                await RunFfmpegAsync(SoundArguments(job, s, backFrom, backTo, Path.Combine(work.FullName, "sound.m4a")),
                    cycle * s.Repeats, f => progress(0.92 + 0.04 * f), ct);

            string list = Path.Combine(work.FullName, "pieces.txt");
            File.WriteAllLines(list, Enumerable.Range(0, Math.Max(1, s.Repeats)).SelectMany(_ => names).Select(n => $"file '{n}'"));
            var a = Common();
            a.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", list });
            if (sound) a.AddRange(new[] { "-i", Path.Combine(work.FullName, "sound.m4a") });
            a.AddRange(new[] { "-map", "0:v" });
            if (sound) a.AddRange(new[] { "-map", "1:a" });
            a.AddRange(new[] { "-c", "copy" });
            if (enc.Hevc) a.AddRange(new[] { "-tag:v", "hvc1" });
            a.AddRange(new[] { "-movflags", "+faststart", s.OutputPath });
            await RunFfmpegAsync(Join(a), cycle * s.Repeats, f => progress(0.96 + 0.04 * f), ct);
            progress(1);
        }
        finally
        {
            for (int i = 0; i < 20; i++)
            {
                try { work.Delete(recursive: true); break; }
                catch { await Task.Delay(100); }
            }
        }
    }

    /// <summary>Runs one ffmpeg command, reporting its progress against <paramref name="seconds"/> of output.</summary>
    static async Task RunFfmpegAsync(string args, double seconds, Action<double> progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var errors = new StringBuilder();
        using var p = new Process { StartInfo = Psi(args), EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginErrorReadLine();

        double total = Math.Max(0.001, seconds);
        using (ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { } }))
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line.AsSpan(12), out long us) && us >= 0)
                    progress(Math.Clamp(us / 1e6 / total, 0, 1));
                else if (line == "progress=end") progress(1);
            }
            await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string msg;
            lock (errors) msg = errors.ToString().Trim();
            var lines = msg.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            string detail = lines.Length == 0 ? $"ffmpeg exited with code {p.ExitCode}." : string.Join("\n", lines.TakeLast(4));
            CrashLog.Write(new InvalidOperationException($"Export failed (exit {p.ExitCode})\nffmpeg {args}\n{msg}"), "Export");
            throw new InvalidOperationException(detail);
        }
    }

    static void TryDelete(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (File.Exists(path)) File.Delete(path); return; }
            catch { Thread.Sleep(100); }
        }
    }

    /// <summary>A free file name next to the source: "name (trimmed).ext", "name (trimmed 2).ext", ...</summary>
    public static string SuggestOutput(MediaInfo info, string extension, string tag = "trimmed")
    {
        string dir = Path.GetDirectoryName(info.Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string name = Path.GetFileNameWithoutExtension(info.Path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({tag}{(i > 1 ? " " + i : "")}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
