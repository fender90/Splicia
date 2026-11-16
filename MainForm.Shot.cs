namespace Splicia;

/// <summary>
/// Screenshots: the camera button (in the player and the editor) saves the frame on screen as a PNG at the file's full
/// size and quality, as the export shows it (turned, cropped, faces hidden, HDR brought to SDR). It asks where, in the
/// folder used last time (Windows remembers it for the screenshots' dialog; the first time, Pictures), suggesting the
/// video's name and the playhead's time.
/// </summary>
sealed partial class MainForm
{
    /// <summary>The screenshots' Save dialog: Windows keeps the folder last used under this id.</summary>
    static readonly Guid ShotDialog = new("4f017dc0-2e71-4143-833b-cdf71d91ee9b");

    /// <summary>Answers where to save instead of the dialog (a test): the suggested name → the full path, or null.</summary>
    Func<string, string?>? _askShotPath = null;
    Task? _shot; // the last one, while it's being saved

    void TakeScreenshot()
    {
        if (_info == null) return;
        if (_shot is { IsCompleted: false })
        {
            ShowToast("Still saving the last picture…");
            return;
        }
        if (_shown is not { } f || double.IsNaN(f.Time))
        {
            ShowToast("No picture here: this is a gap in the timeline");
            return;
        }
        FinishCrop(apply: true); // as the export does: a crop being drawn is kept
        // The picture as it is now (pausing to ask shows the paused frame again, the same one)
        var info = _info;
        double time = f.Time;
        int rotation = _rot;
        var crop = HasCrop ? _crop : (RectangleF?)null;
        var faces = FacesNow();
        string video = Path.GetFileNameWithoutExtension(info.Path);
        if (video.Length > 120) video = video[..120];
        string name = $"{video} {Stamp(_pos)}.png";
        if (_playing) Pause();
        string? path = AskShotPath(name);
        if (path == null) return; // cancelled
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) path += ".png";

        // Faces still to be found where the picture is: it waits for them (never saved with a face showing)
        bool waiting = faces != null && !faces.Finder.Done && faces.Finder.Progress * info.Duration < time + FaceLookahead;
        ShowToast(waiting ? "Saving the picture once the faces here are found…" : "Saving the picture…", 600);
        _shot = Task.Run(async () =>
        {
            if (faces != null) await WaitForFaces(faces.Finder, info, time + FaceLookahead).ConfigureAwait(false);
            await Exporter.SaveFrameAsync(info, time, rotation, crop, faces, path).ConfigureAwait(false);
        }).ContinueWith(t =>
        {
            var ex = t.Exception?.InnerException;
            if (ex != null && ex is not InvalidOperationException) CrashLog.Write(ex, "Screenshot");
            string why = ex == null ? "" : ex.Message.StartsWith("Couldn't") ? ex.Message : "Couldn't save the picture: " + ex.Message;
            if (why.Length > 160) why = why[..157] + "…";
            string text = ex == null ? $"Saved {Path.GetFileName(path)} in {Place(Path.GetDirectoryName(path)!)}" : why;
            try { BeginInvoke(() => ShowToast(text, 3.5)); }
            catch (InvalidOperationException) { } // the window is gone
        }, TaskScheduler.Default);
    }

    /// <summary>Where to save <paramref name="name"/>: the full path chosen, or null if cancelled.</summary>
    string? AskShotPath(string name)
    {
        if (_askShotPath != null) return _askShotPath(name);
        RevealCursor();
        try
        {
            return SaveDialog.Ask(Handle, "Save the picture", name, ShotDialog,
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PNG picture", ".png");
        }
        catch (Exception ex) // COM failures come as several exception types
        {
            CrashLog.Write(ex, "Screenshot dialog");
            ShowToast("Couldn't open the Save dialog", 3);
            return null;
        }
    }

    const double FaceLookahead = 1.0; // faces found a second beyond the picture: its tracks are sure and lead into it

    /// <summary>Until the pass looking for faces is past <paramref name="until"/> (the file's time), or has ended.</summary>
    static async Task WaitForFaces(FaceFinder finder, MediaInfo info, double until)
    {
        while (!finder.Completion.IsCompleted && finder.Progress * info.Duration < until)
            await Task.WhenAny(finder.Completion, Task.Delay(100)).ConfigureAwait(false);
        if (finder.Error is string error) throw new InvalidOperationException("couldn't look for the faces (" + error + ")");
        if (!finder.Done && finder.Progress * info.Duration < until) throw new InvalidOperationException("the search for faces stopped");
    }

    /// <summary>A folder as the user knows it: Pictures\… for one in their Pictures, else its path.</summary>
    static string Place(string folder)
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures).TrimEnd('\\');
        if (pictures.Length == 0) return folder;
        if (string.Equals(folder.TrimEnd('\\'), pictures, StringComparison.OrdinalIgnoreCase)) return "Pictures";
        return folder.StartsWith(pictures + "\\", StringComparison.OrdinalIgnoreCase) ? "Pictures" + folder[pictures.Length..] : folder;
    }

    /// <summary>A time for a file name: 00-01-23.456 (no colons in file names).</summary>
    static string Stamp(double t)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, t));
        return $"{(int)ts.TotalHours:00}-{ts.Minutes:00}-{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    /// <summary>Screenshot button: a camera.</summary>
    void ShotGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c);
        var body = new RectangleF(r.X + r.Width * 0.04f, r.Y + r.Height * 0.26f, r.Width * 0.92f, r.Height * 0.62f);
        using (var path = RoundRect(body, r.Width * 0.12f)) g.DrawPath(pen, path);
        // the viewfinder bump on top, and the lens
        g.DrawLines(pen, new[]
        {
            new PointF(body.X + body.Width * 0.3f, body.Top), new PointF(body.X + body.Width * 0.38f, r.Y + r.Height * 0.12f),
            new PointF(body.X + body.Width * 0.62f, r.Y + r.Height * 0.12f), new PointF(body.X + body.Width * 0.7f, body.Top),
        });
        float lr = r.Width * 0.18f, cx = body.X + body.Width / 2, cy = body.Y + body.Height * 0.53f;
        g.DrawEllipse(pen, cx - lr, cy - lr, 2 * lr, 2 * lr);
    }
}
