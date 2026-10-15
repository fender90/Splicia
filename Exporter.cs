using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoTrimmer;

enum ExportFormat { H264, Hevc, Original }
enum ExportQuality { High, Balanced, Small }

sealed record ExportSettings(ExportFormat Format, ExportQuality Quality, bool IncludeAudio, string OutputPath);

/// <summary>A trim of one file: the selected range and the extra clockwise rotation to apply.</summary>
sealed record TrimJob(MediaInfo Info, double Start, double End, int Rotation)
{
    public double Length => End - Start;
    /// <summary>Rotation the result is shown with (the file's own plus the user's).</summary>
    public int TotalRotation => (Info.Rotation + Rotation) % 360;
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

    public static string Arguments(TrimJob job, ExportSettings s, HwEncoder? hw)
    {
        var info = job.Info;
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats" };
        bool audio = s.IncludeAudio && info.HasAudio;

        // Selection edges sit exactly on frame timestamps, which are rarely round decimals. Nudge the start half a
        // millisecond the safe way so rounding can't move it past a frame: later for copies (so a start that is exactly a
        // keyframe doesn't pull in the previous one), earlier for re-encodes (so the first frame isn't dropped).
        const double Nudge = 0.0005;
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
        // is frame-exact. ffmpeg applies the file's own rotation itself (autorotate); the user's goes in the filter.
        a.AddRange(new[] { "-ss", S(Math.Max(0, job.Start - Nudge)), "-i", info.Path, "-t", S(job.Length), "-map", "0:v:0" });
        if (audio) a.AddRange(new[] { "-map", "0:a?" });

        bool hevc = s.Format == ExportFormat.Hevc;
        bool keepHdr = hevc && info.IsHdr;
        bool tenBit = hevc && (info.BitDepth > 8 || info.IsHdr);
        bool useHw = hevc && hw != null && (!tenBit || hw.TenBit);

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
        // 4:2:0 needs even dimensions.
        filters.Add("crop=trunc(iw/2)*2:trunc(ih/2)*2");
        filters.Add($"format={(tenBit ? (useHw ? "p010le" : "yuv420p10le") : (useHw ? "nv12" : "yuv420p"))}");
        a.AddRange(new[] { "-vf", string.Join(",", filters) });

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

        if (audio) a.AddRange(new[] { "-c:a", "aac", "-b:a", q == 2 ? "128k" : "192k" });
        a.AddRange(new[] { "-map_metadata", "0", "-movflags", "+faststart", s.OutputPath });
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
    /// cancelled (the partial file is deleted) and <see cref="InvalidOperationException"/> with ffmpeg's message on failure.
    /// </summary>
    public static async Task RunAsync(TrimJob job, ExportSettings s, HwEncoder? hw, Action<double> progress, CancellationToken ct)
    {
        string args = Arguments(job, s, hw);
        var errors = new StringBuilder();
        using var p = new Process { StartInfo = Psi(args), EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        p.Start();
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        p.BeginErrorReadLine();

        // Copies start at the keyframe before the start point, so their output runs a little longer.
        double total = Math.Max(0.001, job.Length);
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

        if (ct.IsCancellationRequested)
        {
            TryDelete(s.OutputPath);
            throw new OperationCanceledException(ct);
        }
        if (p.ExitCode != 0)
        {
            TryDelete(s.OutputPath);
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
    public static string SuggestOutput(MediaInfo info, string extension)
    {
        string dir = Path.GetDirectoryName(info.Path) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string name = Path.GetFileNameWithoutExtension(info.Path);
        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} (trimmed{(i > 1 ? " " + i : "")}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
