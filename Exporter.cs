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

    /// <summary>Parts cut out of the video (sorted, not overlapping). Only the parts inside the selection matter.</summary>
    public IReadOnlyList<Splice> Splices { get; init; } = Array.Empty<Splice>();

    /// <summary>
    /// The kept parts of the selection, in order, and the transition at each place two of them meet (one fewer). Parts
    /// shorter than half a frame are dropped.
    /// </summary>
    public (List<(double From, double To)> Parts, List<Transition> Joins) Pieces()
    {
        double min = Info.FrameDuration / 2;
        var parts = new List<(double, double)>();
        var after = new List<Transition>(); // the splice that ends each part
        double cur = Start;
        foreach (var sp in Splices.OrderBy(s => s.From))
        {
            if (sp.To <= cur || sp.From >= End) continue;
            if (sp.From > cur + min) { parts.Add((cur, sp.From)); after.Add(sp.Transition); }
            cur = Math.Max(cur, sp.To);
            if (cur >= End) break;
        }
        if (End > cur + min) parts.Add((cur, End));
        return (parts, after.Take(Math.Max(0, parts.Count - 1)).ToList());
    }

    /// <summary>The joins with the transition times that fit the parts either side.</summary>
    public List<Join> Joins()
    {
        var (parts, joins) = Pieces();
        return joins.Select((t, i) => Join.For(t, parts[i].To - parts[i].From, parts[i + 1].To - parts[i + 1].From, Info.FrameDuration)).ToList();
    }

    /// <summary>Length of the exported video: the kept parts, less the crossfades' overlaps.</summary>
    public double OutputLength()
    {
        var (parts, _) = Pieces();
        return parts.Sum(p => p.To - p.From) - Joins().Sum(j => j.Overlap);
    }

    /// <summary>True when the result is made of more than one part.</summary>
    public bool IsSpliced => Pieces().Parts.Count > 1;

    /// <summary>
    /// The same job without splices when they only shorten the selection (one part left), so it exports as a plain trim
    /// (and can be copied without re-encoding). Unchanged when it is spliced or nothing is left.
    /// </summary>
    public TrimJob Simplified()
    {
        var (parts, _) = Pieces();
        return parts.Count == 1 ? this with { Start = parts[0].From, End = parts[0].To, Splices = Array.Empty<Splice>() } : this;
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
        if (job.IsSpliced) return "Joining spliced parts needs re-encoding: choose H.264 or HEVC.";
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

    /// <param name="forJoining">
    /// The file will be joined to others without re-encoding: no B-frames. With them each file's decode times start a
    /// couple of frames early, except in a file too short to have any, and the join then steps back in time.
    /// </param>
    static VideoEncoding EncodingFor(TrimJob job, ExportSettings s, HwEncoder? hw, bool forJoining = false)
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
            // x264 starts 1.5 threads per logical core, each holding frames: 5 GB at 4K on a 32-thread CPU. Sixteen are
            // as fast there and take half the memory.
            a.AddRange(new[] { "-c:v", "libx264", "-preset", q == 0 ? "fast" : "faster", "-crf", new[] { "18", "21", "25" }[q] });
            a.AddRange(new[] { "-threads", Math.Min(16, Environment.ProcessorCount * 3 / 2).ToString(Inv) });
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
            a.AddRange(new[] { "-c:v", "libx265", "-preset", "faster", "-crf", new[] { "20", "24", "28" }[q], "-x265-params", forJoining ? "log-level=error:bframes=0" : "log-level=error" });
        }
        if (forJoining) a.AddRange(new[] { "-bf", "0" });
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

    /// <summary>
    /// One input of a spliced run: [<paramref name="From"/>, <paramref name="To"/>) of the file, darkened over
    /// <paramref name="FadeIn"/> at its start and <paramref name="FadeOut"/> at its end, crossfading from the one before
    /// over <paramref name="Overlap"/> (0: they're concatenated).
    /// </summary>
    readonly record struct Segment(double From, double To, double FadeIn, double FadeOut, double Overlap)
    {
        public double Length => To - From;
    }

    /// <summary>
    /// The inputs that make parts [<paramref name="first"/>, <paramref name="first"/> + <paramref name="count"/>). A run may
    /// begin or end inside a crossfade: when its first part crossfades from the part before, it starts with that part's
    /// last stretch (the overlap) to crossfade from; when its last part crossfades into the next run, it stops where that
    /// overlap begins. Joined end to end, the runs are the whole export, frame for frame.
    /// </summary>
    static List<Segment> Plan(TrimJob job, int first, int count)
    {
        var (parts, _) = job.Pieces();
        var joins = job.Joins();
        var segs = new List<Segment>();
        if (first > 0 && joins[first - 1].Overlap > 0)
        {
            double d = joins[first - 1].Overlap;
            segs.Add(new Segment(parts[first - 1].To - d, parts[first - 1].To, 0, 0, 0));
        }
        int last = first + count - 1;
        for (int i = first; i <= last; i++)
        {
            double fadeIn = i > 0 ? joins[i - 1].FadeIn : 0, fadeOut = i < joins.Count ? joins[i].FadeOut : 0;
            double overlap = i > 0 && segs.Count > 0 ? joins[i - 1].Overlap : 0;
            double to = parts[i].To;
            if (i == last && i < joins.Count) to -= joins[i].Overlap; // the next run starts with this stretch
            segs.Add(new Segment(parts[i].From, to, fadeIn, fadeOut, overlap));
        }
        return segs;
    }

    /// <summary>Length of a run: its inputs less the crossfades between them.</summary>
    static double RunLength(List<Segment> segs) => segs.Sum(x => x.Length) - segs.Skip(1).Sum(x => x.Overlap);

    /// <summary>
    /// The ffmpeg command line for a spliced run, with the picture (<paramref name="video"/>), the sound
    /// (<paramref name="audio"/>; uncompressed with <paramref name="pcm"/>) or both. Each input seeks straight to its
    /// stretch (so long cut-out parts aren't decoded) and is trimmed like a plain export; then they're joined in order (a
    /// crossfade overlaps two; a straight cut or a fade through black concatenates them) and rotated / cropped / encoded
    /// once. All share a pixel format and time base, which crossfades need. A long graph goes in
    /// <paramref name="graphFile"/> instead of on the command line, which Windows limits to 32K characters.
    /// </summary>
    static string SplicedArguments(TrimJob job, ExportSettings s, HwEncoder? hw, List<Segment> segs, bool video, bool audio,
        string output, bool pcm = false, bool forJoining = false, string? graphFile = null)
    {
        var info = job.Info;
        string pixFmt = info.BitDepth > 8 || info.IsHdr ? "yuv420p10le" : "yuv420p";

        var a = Common();
        var g = new StringBuilder();
        // Every input's decoder is open at once and decodes a little ahead, so they share the usual eight threads' worth
        // (at least two each) rather than taking eight each.
        string threads = Math.Clamp(16 / segs.Count, 2, 8).ToString(Inv);
        for (int k = 0; k < segs.Count; k++)
        {
            var seg = segs[k];
            var (input, vtrim, atrim) = Window(info, seg.From, seg.To);
            if (video) a.AddRange(new[] { "-threads", threads });
            a.AddRange(input);
            a.AddRange(new[] { "-i", info.Path });
            if (video)
            {
                g.Append($"[{k}:v:0]{vtrim},format={pixFmt},settb=AVTB");
                if (seg.FadeIn > 0) g.Append($",fade=t=in:st=0:d={S(seg.FadeIn)}");
                if (seg.FadeOut > 0) g.Append($",fade=t=out:st={S(seg.Length - seg.FadeOut)}:d={S(seg.FadeOut)}");
                g.Append($"[v{k}];");
            }
            if (audio)
            {
                // Padded to the stretch's exact length so the sound stays with the picture at every join.
                g.Append($"[{k}:a:0]{atrim},apad=whole_dur={S(seg.Length)}");
                if (seg.FadeIn > 0) g.Append($",afade=t=in:st=0:d={S(seg.FadeIn)}");
                if (seg.FadeOut > 0) g.Append($",afade=t=out:st={S(seg.Length - seg.FadeOut)}:d={S(seg.FadeOut)}");
                g.Append($"[a{k}];");
            }
        }

        string v = "v0", au = "a0";
        double total = segs[0].Length;
        for (int k = 1; k < segs.Count; k++)
        {
            var seg = segs[k];
            if (seg.Overlap > 0)
            {
                if (video) g.Append($"[{v}][v{k}]xfade=transition=fade:duration={S(seg.Overlap)}:offset={S(total - seg.Overlap)}[jv{k}];");
                if (audio) g.Append($"[{au}][a{k}]acrossfade=d={S(seg.Overlap)}[ja{k}];");
                total += seg.Length - seg.Overlap;
            }
            else
            {
                string ins = (video ? $"[{v}]" : "") + (audio ? $"[{au}]" : "") + (video ? $"[v{k}]" : "") + (audio ? $"[a{k}]" : "");
                string outs = (video ? $"[jv{k}]" : "") + (audio ? $"[ja{k}]" : "");
                g.Append($"{ins}concat=n=2:v={(video ? 1 : 0)}:a={(audio ? 1 : 0)}{outs};");
                total += seg.Length;
            }
            v = $"jv{k}";
            au = $"ja{k}";
        }

        var enc = video ? EncodingFor(job, s, hw, forJoining) : null;
        if (enc != null) g.Append($"[{v}]{string.Join(",", enc.Filters)}[vout]");
        else g.Append($"[{au}]anull[aout]");
        string graph = g.ToString();
        if (graphFile != null)
        {
            File.WriteAllText(graphFile, graph);
            a.AddRange(new[] { "-/filter_complex", graphFile });
        }
        else a.AddRange(new[] { "-filter_complex", graph });

        if (enc != null)
        {
            a.AddRange(new[] { "-map", "[vout]" });
            if (audio) a.AddRange(new[] { "-map", $"[{au}]" });
            a.AddRange(enc.Codec);
        }
        else a.AddRange(new[] { "-map", "[aout]" });
        if (audio) a.AddRange(pcm ? new[] { "-c:a", "pcm_s16le" } : new[] { "-c:a", "aac", "-b:a", AudioBitrate(s) });
        if (!pcm) a.AddRange(new[] { "-map_metadata", "0", "-movflags", "+faststart" });
        a.Add(output);
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
        job = job.Simplified();
        try
        {
            if (job.IsSpliced)
            {
                if (s.Boomerang || s.Format == ExportFormat.Original)
                    throw new NotSupportedException("A spliced video can't be exported as a back-and-forth loop or without re-encoding.");
                await RunSplicedAsync(job, s, hw, progress, ct);
            }
            else if (s.Boomerang) await RunBoomerangAsync(job, s with { Format = s.Format == ExportFormat.Original ? ExportFormat.H264 : s.Format }, hw, progress, ct);
            // Copies start at the keyframe before the start point, so their output runs a little longer.
            else await RunFfmpegAsync(Arguments(job, s, hw), Math.Max(0.001, job.Length), progress, ct);
        }
        catch
        {
            TryDelete(s.OutputPath);
            throw;
        }
    }

    /// <summary>Command lines longer than this put the filter graph in a hidden file instead.</summary>
    const int MaxInlineArguments = 24000;

    /// <summary>Memory the parts' decoders may take together; more parts than fit are exported in runs.</summary>
    const double PartsBudgetBytes = 2e9;

    /// <summary>Overrides how many parts one run may take (tests).</summary>
    internal static int? MaxPartsPerRunOverride { get; set; }

    /// <summary>
    /// How many parts one ffmpeg run may decode at once: each open decoder holds around sixteen decoded frames (its
    /// threads, references and the queue to the filters), about 400 MB per part for 4K HDR.
    /// </summary>
    static int MaxPartsPerRun(MediaInfo info)
    {
        if (MaxPartsPerRunOverride is int n) return n;
        double frame = info.Width * (double)info.Height * 1.5 * (info.BitDepth > 8 || info.IsHdr ? 2 : 1);
        return (int)Math.Clamp(PartsBudgetBytes / (16 * frame), 2, 24);
    }

    /// <summary>The parts in groups of consecutive ones, each small enough for one run (see <see cref="Plan"/>).</summary>
    internal static List<(int First, int Count)> Runs(TrimJob job)
    {
        int n = job.Pieces().Parts.Count, max = MaxPartsPerRun(job.Info);
        var runs = new List<(int, int)>();
        for (int first = 0; first < n; first += max) runs.Add((first, Math.Min(max, n - first)));
        return runs;
    }

    /// <summary>
    /// A spliced export. Few parts: one ffmpeg run does everything. Many (more than fit in memory at once): the picture
    /// is made in runs of consecutive parts and so is the sound (uncompressed, so it joins seamlessly); the runs, which
    /// share encoder settings, are joined without re-encoding the picture and the sound is encoded once, like a
    /// back-and-forth loop. Work files go in a hidden folder next to the output and are removed afterwards.
    /// </summary>
    static async Task RunSplicedAsync(TrimJob job, ExportSettings s, HwEncoder? hw, Action<double> progress, CancellationToken ct)
    {
        var info = job.Info;
        bool sound = s.IncludeAudio && info.HasAudio;
        double length = Math.Max(0.001, job.OutputLength());
        var runs = Runs(job);
        string dir = Path.GetDirectoryName(Path.GetFullPath(s.OutputPath))!;

        // Several runs are joined afterwards (see EncodingFor's forJoining); a single run is the export itself.
        bool joining = runs.Count > 1;
        async Task Run(List<Segment> segs, bool video, bool audio, bool pcm, string output, Action<double> report)
        {
            string args = SplicedArguments(job, s, hw, segs, video, audio, output, pcm, joining);
            if (args.Length <= MaxInlineArguments)
            {
                await RunFfmpegAsync(args, RunLength(segs), report, ct);
                return;
            }
            string graph = Path.Combine(dir, $".splicia-{Guid.NewGuid():N}.txt");
            try
            {
                args = SplicedArguments(job, s, hw, segs, video, audio, output, pcm, joining, graph);
                File.SetAttributes(graph, File.GetAttributes(graph) | FileAttributes.Hidden);
                await RunFfmpegAsync(args, RunLength(segs), report, ct);
            }
            finally { TryDelete(graph); }
        }

        if (runs.Count == 1)
        {
            await Run(Plan(job, 0, runs[0].Count), true, sound, false, s.OutputPath, progress);
            return;
        }

        var work = Directory.CreateDirectory(Path.Combine(dir, $".splicia-{Guid.NewGuid():N}"));
        work.Attributes |= FileAttributes.Hidden;
        try
        {
            // 85% of the bar for the pictures and 10% for the sound (by duration), then the join.
            double pictureShare = sound ? 0.85 : 0.95;
            var pictures = new List<string>();
            var sounds = new List<string>();
            var lengths = new List<double>();
            double done = 0;
            for (int r = 0; r < runs.Count; r++)
            {
                var segs = Plan(job, runs[r].First, runs[r].Count);
                string name = $"run{r:000}.mp4";
                double before = done, len = RunLength(segs);
                await Run(segs, true, false, false, Path.Combine(work.FullName, name), f => progress(pictureShare * (before + f * len) / length));
                done += len;
                pictures.Add(name);
                lengths.Add(len);
            }
            if (sound)
            {
                done = 0;
                for (int r = 0; r < runs.Count; r++)
                {
                    var segs = Plan(job, runs[r].First, runs[r].Count);
                    string name = $"sound{r:000}.wav";
                    double before = done, len = RunLength(segs);
                    await Run(segs, false, true, true, Path.Combine(work.FullName, name), f => progress(pictureShare + 0.1 * (before + f * len) / length));
                    done += len;
                    sounds.Add(name);
                }
            }

            string pictureList = Path.Combine(work.FullName, "pictures.txt"), soundList = Path.Combine(work.FullName, "sounds.txt");
            // Each run's exact length: a file can't say how long its last frame lasts (a one-frame run would count as
            // nothing), and the next run starts where this says.
            File.WriteAllLines(pictureList, pictures.SelectMany((n, i) => new[] { $"file '{n}'", $"duration {S(lengths[i])}" }));
            File.WriteAllLines(soundList, sounds.SelectMany((n, i) => new[] { $"file '{n}'", $"duration {S(lengths[i])}" }));
            var a = Common();
            a.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", pictureList });
            if (sound) a.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", soundList });
            a.AddRange(new[] { "-map", "0:v" });
            if (sound) a.AddRange(new[] { "-map", "1:a" });
            a.AddRange(new[] { "-c:v", "copy" });
            if (sound) a.AddRange(new[] { "-c:a", "aac", "-b:a", AudioBitrate(s) });
            if (s.Format == ExportFormat.Hevc) a.AddRange(new[] { "-tag:v", "hvc1" });
            a.AddRange(new[] { "-movflags", "+faststart", s.OutputPath });
            await RunFfmpegAsync(Join(a), length, f => progress(0.95 + 0.05 * f), ct);
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
        var enc = EncodingFor(job, s, hw, forJoining: true);

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
            var lengths = new List<double>();
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
                lengths.Add(len);
            }
            if (sound)
                await RunFfmpegAsync(SoundArguments(job, s, backFrom, backTo, Path.Combine(work.FullName, "sound.m4a")),
                    cycle * s.Repeats, f => progress(0.92 + 0.04 * f), ct);

            string list = Path.Combine(work.FullName, "pieces.txt");
            // With each piece's exact length (see RunSplicedAsync): the last piece backwards can be a single frame.
            File.WriteAllLines(list, Enumerable.Range(0, Math.Max(1, s.Repeats))
                .SelectMany(_ => names.SelectMany((n, i) => new[] { $"file '{n}'", $"duration {S(lengths[i])}" })));
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
