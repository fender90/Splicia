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

    /// <summary>The timeline to export (clips, gaps, transitions); null = just [Start, End) of the file.</summary>
    public EditList? Edit { get; init; }

    /// <summary>True when exporting a timeline rather than one stretch of the file.</summary>
    public bool IsEdited => Edit != null;

    /// <summary>Length of the exported video.</summary>
    public double OutputLength() => Edit?.Length ?? Length;
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
        if (job.IsEdited) return "Joining clips needs re-encoding: choose H.264 or HEVC.";
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
    /// stops a frame after the end (input-side -t, so a reverse filter sees no more than that), the picture is cut
    /// halfway between frames at both ends and the sound exactly at them. Filter timestamps count from the seek point.
    /// </summary>
    static (string[] Input, string VideoTrim, string AudioTrim) Window(MediaInfo info, double from, double to)
    {
        double seek = CutPoint(info, from), start = from - seek, end = to - seek;
        string[] input = { "-ss", S(seek), "-t", S(end + info.FrameDuration) };
        // Not on the first frame left out: the seek is rounded to the file's time base (1/30000 for 29.97 fps, where
        // half a frame isn't a whole tick), which can put that frame a hair before the end and let it through.
        double videoEnd = Math.Max(start, end - info.FrameDuration / 2);
        return (input, $"trim=end={S(videoEnd)},setpts=PTS-STARTPTS", $"atrim=start={S(start)}:end={S(end)},asetpts=PTS-STARTPTS");
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
    /// One input of a timeline run: [<paramref name="From"/>, <paramref name="To"/>) of the file (or, with
    /// <paramref name="Black"/>, that long of black and silence: a gap), darkened over <paramref name="FadeIn"/> at its
    /// start and <paramref name="FadeOut"/> at its end, crossfading from the one before over <paramref name="Overlap"/>
    /// (0: they're concatenated), with the sound's declick fades where it starts or stops abruptly
    /// (<paramref name="CutIn"/>, <paramref name="CutOut"/>; see <see cref="EditList.Declick"/>).
    /// </summary>
    readonly record struct Segment(double From, double To, double FadeIn, double FadeOut, double Overlap, bool CutIn = false, bool CutOut = false,
        bool Black = false)
    {
        public double Length => To - From;
        public double DeclickLength => Math.Min(EditList.Declick, Length / 2);
    }

    /// <summary>
    /// The inputs that make parts [<paramref name="first"/>, <paramref name="first"/> + <paramref name="count"/>) of the
    /// timeline, with the gap before each. A part plays with its handles (see <see cref="EditList"/>). A run may begin or
    /// end inside a crossfade: when its first part crossfades from the part before, it starts with that part's last
    /// stretch (both handles' worth) to crossfade from; when its last part crossfades into the next run, it stops where
    /// that crossfade begins. Joined end to end, the runs are the whole timeline, frame for frame.
    /// </summary>
    static List<Segment> Plan(EditList edit, int first, int count)
    {
        var parts = edit.Parts;
        var segs = new List<Segment>();
        int last = first + count - 1;
        for (int i = first; i <= last; i++)
        {
            var p = parts[i];
            double prevEnd = i == 0 ? 0 : parts[i - 1].PlayEnd;
            if (p.PlayStart > prevEnd + edit.Frame / 2) segs.Add(new Segment(0, p.PlayStart - prevEnd, 0, 0, 0, Black: true));
            if (i == first && i > 0 && p.HandleIn > 0)
            {
                var q = parts[i - 1];
                segs.Add(new Segment(q.To - q.HandleOut, q.To + q.HandleOut, 0, 0, 0));
            }
            double to = p.To + p.HandleOut;
            if (i == last && i + 1 < parts.Count && p.HandleOut > 0) to -= 2 * p.HandleOut; // the next run starts with this stretch
            double overlap = p.HandleIn > 0 && segs.Count > 0 ? 2 * p.HandleIn : 0;
            segs.Add(new Segment(p.From - p.HandleIn, to, p.FadeIn, p.FadeOut, overlap, p.CutIn, p.CutOut));
        }
        if (last == parts.Count - 1)
        {
            double videoEnd = parts.Count == 0 ? 0 : parts[^1].PlayEnd;
            if (edit.Length > videoEnd + edit.Frame / 2) segs.Add(new Segment(0, edit.Length - videoEnd, 0, 0, 0, Black: true));
        }
        // Nothing at all (no clips, a sound under half a frame): a frame of black to carry it.
        if (segs.Count == 0) segs.Add(new Segment(0, Math.Max(edit.Length, edit.Frame), 0, 0, 0, Black: true));
        return segs;
    }

    /// <summary>Length of a run: its inputs less the crossfades between them.</summary>
    static double RunLength(List<Segment> segs) => segs.Sum(x => x.Length) - segs.Skip(1).Sum(x => x.Overlap);

    /// <summary>
    /// The ffmpeg command line for a timeline run, with the picture (<paramref name="video"/>), the sound
    /// (<paramref name="audio"/>; uncompressed with <paramref name="pcm"/>) or both. Each input seeks straight to its
    /// stretch (so footage left out isn't decoded) and is trimmed like a plain export; gaps are generated black and
    /// silence; then they're joined in order (a crossfade overlaps two; anything else concatenates them) and rotated /
    /// cropped / encoded once. All share a pixel format, picture size, time base and sound format, which joining needs.
    /// A long graph goes in <paramref name="graphFile"/> instead of on the command line, which Windows limits to 32K.
    /// </summary>
    static string SplicedArguments(TrimJob job, ExportSettings s, HwEncoder? hw, List<Segment> segs, bool video, bool audio,
        string output, bool pcm = false, bool forJoining = false, string? graphFile = null, string[]? outputOptions = null,
        EditList? music = null)
    {
        var info = job.Info;
        string pixFmt = info.BitDepth > 8 || info.IsHdr ? "yuv420p10le" : "yuv420p";
        const string Sound = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
        // Black pictures the size ffmpeg decodes the file to: it turns the picture by the file's own rotation first.
        bool turned = info.Rotation % 180 == 90;
        int bw = turned ? info.Height : info.Width, bh = turned ? info.Width : info.Height;
        double sar = turned ? 1 / info.Sar : info.Sar;
        // ... tagged with the file's colours, so filters that convert colour (HDR → SDR) see one kind of picture throughout.
        static bool Known(string v) => v.Length > 0 && v != "unknown" && v != "reserved";
        var tags = new List<string>();
        if (Known(info.ColorPrimaries)) tags.Add($"color_primaries={info.ColorPrimaries}");
        if (Known(info.ColorTransfer)) tags.Add($"color_trc={info.ColorTransfer}");
        if (Known(info.ColorSpace)) tags.Add($"colorspace={info.ColorSpace}");
        string colour = tags.Count > 0 ? ",setparams=range=tv:" + string.Join(":", tags) : "";

        var a = Common();
        var g = new StringBuilder();
        int inputs = segs.Count(x => !x.Black);
        // Every input's decoder is open at once and decodes a little ahead, so they share the usual eight threads' worth
        // (at least two each) rather than taking eight each.
        string threads = Math.Clamp(16 / Math.Max(1, inputs), 2, 8).ToString(Inv);
        int input = 0;
        for (int k = 0; k < segs.Count; k++)
        {
            var seg = segs[k];
            string vtrim, atrim;
            if (seg.Black)
            {
                vtrim = $"color=c=black:s={bw}x{bh}:r={info.Rate}:d={S(seg.Length)},setsar={S(sar)}{colour}";
                atrim = $"anullsrc=r=48000:cl=stereo,atrim=duration={S(seg.Length)}";
            }
            else
            {
                var (inArgs, vt, at) = Window(info, seg.From, seg.To);
                if (video) a.AddRange(new[] { "-threads", threads });
                a.AddRange(inArgs);
                a.AddRange(new[] { "-i", info.Path });
                // On the exact frame grid: containers round timestamps (Matroska to whole milliseconds), and joins need
                // the frames of every input exactly where the plan says (a variable frame rate is evened out).
                vtrim = $"[{input}:v:0]{vt},fps={info.Rate}:round=near";
                // A video without sound still gets a (silent) sound track when the audio track has something.
                atrim = info.HasAudio ? $"[{input}:a:0]{at}" : $"anullsrc=r=48000:cl=stereo,atrim=duration={S(seg.Length)}";
                input++;
            }
            if (video)
            {
                g.Append($"{vtrim},format={pixFmt},settb=AVTB");
                if (seg.FadeIn > 0) g.Append($",fade=t=in:st=0:d={S(seg.FadeIn)}");
                if (seg.FadeOut > 0) g.Append($",fade=t=out:st={S(seg.Length - seg.FadeOut)}:d={S(seg.FadeOut)}");
                g.Append($"[v{k}];");
            }
            if (audio)
            {
                // Padded to the stretch's exact length so the sound stays with the picture at every join.
                g.Append($"{atrim},apad=whole_dur={S(seg.Length)},{Sound}");
                if (seg.FadeIn > 0) g.Append($",afade=t=in:st=0:d={S(seg.FadeIn)}:curve=qsin");
                if (seg.FadeOut > 0) g.Append($",afade=t=out:st={S(seg.Length - seg.FadeOut)}:d={S(seg.FadeOut)}:curve=qsin");
                if (seg.CutIn) g.Append($",afade=t=in:st=0:d={S(seg.DeclickLength)}");
                if (seg.CutOut) g.Append($",afade=t=out:st={S(seg.Length - seg.DeclickLength)}:d={S(seg.DeclickLength)}");
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
                // Constant power (qsin), like the preview (EditAudio.CrossGain).
                if (audio) g.Append($"[{au}][a{k}]acrossfade=d={S(seg.Overlap)}:c1=qsin:c2=qsin[ja{k}];");
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

        if (audio && music is { Music.Count: > 0 })
        {
            g.Append($"[{au}]anull[main];");
            au = MixMusic(a, g, music, input, "main", total);
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
        if (!pcm) a.AddRange(input > 0 ? new[] { "-map_metadata", "0", "-movflags", "+faststart" } : new[] { "-movflags", "+faststart" });
        if (outputOptions != null) a.AddRange(outputOptions);
        a.Add(output);
        return Join(a);
    }

    /// <summary>
    /// Adds the audio track's clips as inputs (from input <paramref name="firstInput"/> on) and mixes them over
    /// <paramref name="main"/>, the timeline's own sound (<paramref name="length"/> long): each trimmed to its stretch, faded
    /// (qsin, like the preview), declicked at both ends and delayed to its place; summed without lowering the levels, as
    /// the preview does. Returns the label of the mix.
    /// </summary>
    static string MixMusic(List<string> a, StringBuilder g, EditList edit, int firstInput, string main, double length)
    {
        const string Sound = "aformat=sample_fmts=fltp:sample_rates=48000:channel_layouts=stereo";
        var labels = new List<string> { main };
        for (int i = 0; i < edit.Music.Count; i++)
        {
            var c = edit.Music[i];
            a.AddRange(new[] { "-ss", S(c.From), "-t", S(c.Length + 0.05), "-i", c.Path });
            double d = c.DeclickLength;
            g.Append($"[{firstInput + i}:a:0]atrim=end={S(c.Length)},asetpts=PTS-STARTPTS,apad=whole_dur={S(c.Length)},{Sound}");
            if (c.FadeIn > 0) g.Append($",afade=t=in:st=0:d={S(c.FadeIn)}:curve=qsin");
            if (c.FadeOut > 0) g.Append($",afade=t=out:st={S(c.Length - c.FadeOut)}:d={S(c.FadeOut)}:curve=qsin");
            g.Append($",afade=t=in:st=0:d={S(d)},afade=t=out:st={S(c.Length - d)}:d={S(d)}");
            g.Append($",adelay=delays={(long)Math.Round(c.Start * MediaSource.AudioRate)}S:all=1[m{i}];");
            labels.Add($"m{i}");
        }
        g.Append(string.Concat(labels.Select(l => $"[{l}]")) + $"amix=inputs={labels.Count}:duration=first:normalize=0:dropout_transition=0,atrim=end={S(length)}[mixed];");
        return "mixed";
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
            if (job.IsEdited)
            {
                if (s.Boomerang || s.Format == ExportFormat.Original)
                    throw new NotSupportedException("A timeline of clips can't be exported as a back-and-forth loop or without re-encoding.");
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

    /// <summary>
    /// The export's sound alone, as 48 kHz stereo 16-bit WAV, made exactly as an export makes it (tests compare it with
    /// what the preview plays).
    /// </summary>
    internal static Task RenderSoundAsync(TrimJob job, string wav, CancellationToken ct = default)
    {
        var edit = job.Edit ?? EditList.Single(job.Start, job.End);
        var segs = Plan(edit, 0, edit.Parts.Count);
        var s = new ExportSettings(ExportFormat.H264, ExportQuality.High, true, wav);
        string args = SplicedArguments(job, s, null, segs, video: false, audio: true, wav, pcm: true,
            outputOptions: new[] { "-ar", MediaSource.AudioRate.ToString(Inv), "-ac", "2" }, music: edit);
        return RunFfmpegAsync(args, RunLength(segs), _ => { }, ct);
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
        int n = job.Edit?.Parts.Count ?? 1, max = MaxPartsPerRun(job.Info);
        var runs = new List<(int, int)>();
        for (int first = 0; first < n; first += max) runs.Add((first, Math.Min(max, n - first)));
        if (runs.Count == 0) runs.Add((0, 0)); // no video clips: black under the audio track
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
        var edit = job.Edit!;
        bool sound = s.IncludeAudio && (info.HasAudio || edit.Music.Count > 0);
        double length = Math.Max(0.001, job.OutputLength());
        var runs = Runs(job);
        string dir = Path.GetDirectoryName(Path.GetFullPath(s.OutputPath))!;

        // Several runs are joined afterwards (see EncodingFor's forJoining); a single run is the export itself.
        bool joining = runs.Count > 1;
        async Task Run(List<Segment> segs, bool video, bool audio, bool pcm, string output, Action<double> report)
        {
            // In one run the audio track is mixed in there; in several, when the runs are joined.
            var music = runs.Count == 1 ? edit : null;
            string args = SplicedArguments(job, s, hw, segs, video, audio, output, pcm, joining, music: music);
            if (args.Length <= MaxInlineArguments)
            {
                await RunFfmpegAsync(args, RunLength(segs), report, ct);
                return;
            }
            string graph = Path.Combine(dir, $".splicia-{Guid.NewGuid():N}.txt");
            try
            {
                args = SplicedArguments(job, s, hw, segs, video, audio, output, pcm, joining, graph, music: music);
                File.SetAttributes(graph, File.GetAttributes(graph) | FileAttributes.Hidden);
                await RunFfmpegAsync(args, RunLength(segs), report, ct);
            }
            finally { TryDelete(graph); }
        }

        if (runs.Count == 1)
        {
            await Run(Plan(edit, 0, runs[0].Count), true, sound, false, s.OutputPath, progress);
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
                var segs = Plan(edit, runs[r].First, runs[r].Count);
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
                    var segs = Plan(edit, runs[r].First, runs[r].Count);
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
            string? mixed = null;
            var g = new StringBuilder("[1:a]anull[main];");
            if (sound && edit.Music.Count > 0) mixed = MixMusic(a, g, edit, 2, "main", length); // adds the sound files as inputs
            if (mixed != null) a.AddRange(new[] { "-filter_complex", g.ToString().TrimEnd(';') });
            a.AddRange(new[] { "-map", "0:v" });
            if (mixed != null) a.AddRange(new[] { "-map", $"[{mixed}]" });
            else if (sound) a.AddRange(new[] { "-map", "1:a" });
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
