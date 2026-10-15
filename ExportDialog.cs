using System.Diagnostics;

namespace VideoTrimmer;

/// <summary>
/// Export window in the app's design language: choose the format and quality, pick where to save, then a progress
/// page and a result page (show in folder / play).
/// </summary>
sealed class ExportDialog : PagedWindow
{
    const int WinW = 600, WinH = 668, Gutter = 44, PageTop = 58, PageBottom = 30;

    readonly TrimJob _job;
    readonly MediaInfo _info;
    readonly double? _keyframe;           // where an "Original" export really starts
    readonly string? _originalBlocked;    // why "Original" can't be used
    ExportFormat _format;
    ExportQuality _quality = ExportQuality.High;
    bool _audio;
    Exporter.HwEncoder? _hw;
    OptionCard? _hevcCard;

    CancellationTokenSource? _cts;
    PassLabel? _status, _percent;
    ThinProgress? _bar;

    public ExportDialog(TrimJob job)
        : base("Export video", new Size(WinW, WinH), new Rectangle(Gutter, PageTop, WinW - 2 * Gutter, WinH - PageTop - PageBottom), minimizeBox: true)
    {
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        _job = job;
        _info = job.Info;
        _audio = _info.HasAudio;
        _format = _info.IsHdr ? ExportFormat.Hevc : ExportFormat.H264;
        _originalBlocked = Exporter.OriginalUnavailableReason(job);
        try
        {
            using var probe = new MediaSource(_info.Path, decoders: false);
            _keyframe = probe.KeyframeAtOrBefore(job.Start);
        }
        catch (Exception ex) { CrashLog.Write(ex, "Finding the start keyframe"); }

        // Hardware HEVC encoder (checked once per session, in the background)
        var probeTask = Exporter.HardwareHevc;
        if (probeTask.IsCompletedSuccessfully) _hw = probeTask.Result;
        else probeTask.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && !IsDisposed)
                try { BeginInvoke(() => { _hw = t.Result; UpdateHevcCard(); }); } catch (InvalidOperationException) { }
        });

        ShowOptions(null);
    }

    string Summary()
    {
        var (w, h) = _info.DisplaySize(_job.Rotation);
        string rot = _job.Rotation switch { 90 => "  ·  rotated 90° right", 180 => "  ·  rotated 180°", 270 => "  ·  rotated 90° left", _ => "" };
        return $"{TrimmerForm.Fmt(_job.Start)} → {TrimmerForm.Fmt(_job.End)}  ·  {TrimmerForm.Length(_job.Length)}  ·  {w}×{h}{rot}";
    }

    // ───────────────────────── 1. options ─────────────────────────

    void ShowOptions(string? note)
    {
        NewPage();
        AddText("Export video", H1, Theme.Fg);
        AddText(Summary(), Body, Theme.Dim, 4);

        AddText("FORMAT", Section, Theme.Faint, 20);
        var h264 = AddControl(new OptionCard("MP4  ·  H.264",
            _info.IsHdr ? "Plays on everything. HDR is converted to standard range. Cuts exactly where you set them."
                        : "Plays on everything. Cuts exactly where you set them.") { Badge = "Most compatible" }, P(76), 8);
        _hevcCard = AddControl(new OptionCard("MP4  ·  HEVC (H.265)", HevcDescription()) { Badge = _info.IsHdr ? "Keeps HDR" : "Smaller files" }, P(76), 10);
        var original = AddControl(new OptionCard("Original quality, no re-encoding", OriginalDescription()) { Badge = "Fastest" }, P(76), 10);
        original.Enabled = _originalBlocked == null;
        var cards = new[] { (h264, ExportFormat.H264), (_hevcCard, ExportFormat.Hevc), (original, ExportFormat.Original) };

        AddText("QUALITY", Section, Theme.Faint, 18);
        var quality = AddControl(new Segmented("Best", "Balanced", "Smaller file"), P(36), 8, width: P(330));
        quality.SelectedIndex = (int)_quality;
        quality.SelectedChanged += (_, _) => _quality = (ExportQuality)quality.SelectedIndex;

        var sound = AddControl(new ToggleSwitch(_info.HasAudio ? "Include sound" : "Include sound (this video has none)",
            _audio && _info.HasAudio), P(28), 20);
        sound.Enabled = _info.HasAudio;
        sound.CheckedChanged += (_, _) => _audio = sound.Checked;

        InlineError = AddText(note ?? "", Small, note != null ? Theme.Dim : Theme.Danger, 10, fixedHeight: P(36));
        InlineError.Visible = note != null;

        void Select(ExportFormat f)
        {
            _format = f;
            foreach (var (card, fmt) in cards) card.Selected = fmt == f;
            quality.Enabled = f != ExportFormat.Original;
        }
        foreach (var (card, fmt) in cards) card.SelectedChanged += (_, _) => { if (card.Selected) Select(fmt); };
        Select(_format == ExportFormat.Original && _originalBlocked != null ? ExportFormat.H264 : _format);

        Footer(new[] { Button("Cancel", PillButton.Kind.Ghost, TryClose) }, Button("Export…", PillButton.Kind.Primary, ChooseFileAndStart));
    }

    string HevcDescription()
    {
        string d = "About half the size of H.264. Plays on recent phones, Macs and PCs.";
        if (_hw != null) d += $" Encoded on your {_hw.Vendor} graphics.";
        return d;
    }

    void UpdateHevcCard()
    {
        if (_hevcCard == null || _hevcCard.IsDisposed) return;
        _hevcCard.Description = HevcDescription();
    }

    string OriginalDescription()
    {
        if (_originalBlocked != null) return _originalBlocked;
        string start = _keyframe is double k && k < _job.Start - 0.01
            ? $"Starts at the keyframe at {TrimmerForm.Fmt(k)}, slightly before your start."
            : "Lossless; the start snaps to the nearest keyframe.";
        return _job.Rotation != 0 ? start + " Rotation is saved as a flag." : "Lossless and very fast. " + start;
    }

    void ChooseFileAndStart()
    {
        string ext = _format == ExportFormat.Original ? Exporter.OriginalExtension(_info) : ".mp4";
        string suggested = Exporter.SuggestOutput(_info, ext);
        using var dlg = new SaveFileDialog
        {
            Title = "Export video",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested),
            Filter = $"{ext.TrimStart('.').ToUpperInvariant()} video (*{ext})|*{ext}",
            DefaultExt = ext,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string output = Path.GetFullPath(dlg.FileName);
        if (string.Equals(output, Path.GetFullPath(_info.Path), StringComparison.OrdinalIgnoreCase))
        {
            ShowInlineError("Choose a different file name: the export can't replace the video it's made from.");
            return;
        }
        Start(new ExportSettings(_format, _quality, _audio && _info.HasAudio, output));
    }

    // ───────────────────────── 2. progress ─────────────────────────

    async void Start(ExportSettings settings)
    {
        NewPage();
        AddText("Exporting…", H1, Theme.Fg);
        AddText(Path.GetFileName(settings.OutputPath), Body, Theme.Dim, 6);
        _bar = AddControl(new ThinProgress(), P(16), 30);
        _percent = AddText("Starting…", Small, Theme.Faint, 8);
        _status = AddText(settings.Format == ExportFormat.Original ? "Copying without re-encoding." :
            settings.Format == ExportFormat.Hevc && _hw != null && (!(_info.BitDepth > 8 || _info.IsHdr) || _hw.TenBit)
                ? $"Encoding HEVC on your {_hw.Vendor} graphics." : $"Encoding {(settings.Format == ExportFormat.Hevc ? "HEVC" : "H.264")}.",
            Small, Theme.Faint, 4);
        Footer(null, Button("Cancel", PillButton.Kind.Secondary, () => _cts?.Cancel()));

        _cts = new CancellationTokenSource();
        Working = true;
        var sw = Stopwatch.StartNew();
        double last = 0;
        try
        {
            await Exporter.RunAsync(_job, settings, settings.Format == ExportFormat.Hevc ? _hw : null, f =>
            {
                BeginInvoke(() =>
                {
                    if (_bar == null || _bar.IsDisposed || _percent == null) return;
                    _bar.Value = f;
                    if (Math.Abs(f - last) < 0.002 && f < 1) return;
                    last = f;
                    string left = "";
                    double el = sw.Elapsed.TotalSeconds;
                    if (f > 0.03 && el > 1.5 && f < 1)
                    {
                        double remain = el / f * (1 - f);
                        left = remain < 60 ? $"  ·  about {Math.Max(1, Math.Round(remain))} s left" : $"  ·  about {Math.Round(remain / 60)} min left";
                    }
                    _percent.Text = $"{f * 100:0}%{left}";
                });
            }, _cts.Token);
            Working = false;
            ShowDone(settings);
        }
        catch (OperationCanceledException)
        {
            Working = false;
            ShowOptions("Export cancelled. Nothing was saved.");
        }
        catch (Exception ex)
        {
            Working = false;
            if (ex is not InvalidOperationException) CrashLog.Write(ex, "Export");
            ShowFailed(ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }
    }

    protected override void TryClose()
    {
        if (Working) { _cts?.Cancel(); return; } // closing while exporting = cancel
        base.TryClose();
    }

    // ───────────────────────── 3. result ─────────────────────────

    void ShowDone(ExportSettings s)
    {
        NewPage();
        AddControl(new ResultBadge { Success = true }, P(68), 0, width: P(68));
        AddText("Export finished", H1, Theme.Fg, 18);
        string size = "";
        try { size = FormatSize(new FileInfo(s.OutputPath).Length) + "  ·  "; } catch { }
        AddText(Path.GetFileName(s.OutputPath), Body, Theme.Fg, 8);
        AddText($"{size}{TrimmerForm.Length(_job.Length)}  ·  in {Path.GetDirectoryName(s.OutputPath)}", Small, Theme.Dim, 4);
        Footer(new[]
            {
                Button("Show in folder", PillButton.Kind.Ghost, () => Shell("explorer.exe", $"/select,\"{s.OutputPath}\"")),
                Button("Play", PillButton.Kind.Ghost, () => Shell(s.OutputPath, null)),
            },
            Button("Done", PillButton.Kind.Primary, Close));
    }

    void ShowFailed(string message)
    {
        NewPage();
        AddControl(new ResultBadge { Success = false }, P(68), 0, width: P(68));
        AddText("Export failed", H1, Theme.Fg, 18);
        AddText(message, Body, Theme.Dim, 6);
        AddText($"Details were saved to {CrashLog.FilePath}", Small, Theme.Faint, 12);
        Footer(null, Button("Close", PillButton.Kind.Secondary, TryClose), Button("Try again", PillButton.Kind.Primary, () => ShowOptions(null)));
    }

    static void Shell(string file, string? args)
    {
        try
        {
            var psi = args == null ? new ProcessStartInfo(file) { UseShellExecute = true } : new ProcessStartInfo(file, args) { UseShellExecute = true };
            Process.Start(psi);
        }
        catch (Exception ex) { CrashLog.Write(ex, $"Opening {file}"); }
    }

    static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{bytes / 1024.0:0} KB";
}
