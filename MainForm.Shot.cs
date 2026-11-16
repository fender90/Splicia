using System.Diagnostics;

namespace Splicia;

/// <summary>
/// Screenshots: the camera button (in the player and the editor) saves the frame on screen as a PNG at the file's full
/// size and quality, as the export shows it (turned, cropped, faces hidden, HDR brought to SDR), in Pictures\Splicia,
/// named after the video and the playhead's time. Nothing is asked: the toast says where it went, and the ⋯ menu opens
/// the folder.
/// </summary>
sealed partial class MainForm
{
    /// <summary>Where screenshots go (a test points it elsewhere).</summary>
    string _shotDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppInfo.Name);
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
        string path;
        try
        {
            Directory.CreateDirectory(_shotDir);
            // The name taken at once (an empty file, replaced): a second shot meanwhile gets the next one
            string video = Path.GetFileNameWithoutExtension(_info.Path);
            if (video.Length > 120) video = video[..120];
            path = Unique(Path.Combine(_shotDir, $"{video} {Stamp(_pos)}"), ".png");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast($"Couldn't make the folder {_shotDir}", 3);
            return;
        }
        var info = _info;
        double time = f.Time;
        int rotation = _rot;
        var crop = HasCrop ? _crop : (RectangleF?)null;
        var faces = FacesNow();
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
            if (ex != null)
            {
                if (ex is not InvalidOperationException) CrashLog.Write(ex, "Screenshot");
                try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the name it took
            }
            string why = ex == null ? "" : ex.Message.StartsWith("Couldn't") ? ex.Message : "Couldn't save the picture: " + ex.Message;
            if (why.Length > 160) why = why[..157] + "…";
            string text = ex == null ? $"Saved {Path.GetFileName(path)} in {ShotPlace}" : why;
            try { BeginInvoke(() => ShowToast(text, 3.5)); }
            catch (InvalidOperationException) { } // the window is gone
        }, TaskScheduler.Default);
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

    /// <summary>The folder as the user knows it: Pictures\Splicia.</summary>
    string ShotPlace
    {
        get
        {
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return string.Equals(Path.GetDirectoryName(_shotDir), pictures, StringComparison.OrdinalIgnoreCase)
                ? $"Pictures\\{Path.GetFileName(_shotDir)}" : _shotDir;
        }
    }

    void OpenShotFolder()
    {
        try
        {
            Directory.CreateDirectory(_shotDir);
            Process.Start("explorer.exe", $"\"{_shotDir}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>A time for a file name: 00-01-23.456 (no colons in file names).</summary>
    static string Stamp(double t)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, t));
        return $"{(int)ts.TotalHours:00}-{ts.Minutes:00}-{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    /// <summary>
    /// <paramref name="stem"/> + <paramref name="ext"/>, or with " (2)", " (3)"… if that's taken; taken at once (created
    /// empty) so nothing else gets the same name before it's written.
    /// </summary>
    static string Unique(string stem, string ext)
    {
        for (int n = 1; ; n++)
        {
            string path = n == 1 ? stem + ext : $"{stem} ({n}){ext}";
            try
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
                return path;
            }
            catch (IOException) when (File.Exists(path) && n < 10000) { }
        }
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
