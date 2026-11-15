using System.Diagnostics;

namespace Splicia;

/// <summary>
/// Hiding faces, on a video made from public-domain NASA crew photos (assets\): 7 faces upright for 4 s, then 3 tilted
/// 15° for 4 s; and the same stored sideways, as phones store it. The pass finds them all (and where they are, turned
/// back), quickly enough; the preview hides them, and only them; the export does the same, blurred and pixelated.
/// </summary>
static class FaceTests
{
    static readonly double[] Upright = { 0.5, 1.2, 2.0, 2.8, 3.6 }, Tilted = { 4.8, 5.5, 6.2, 7.0, 7.6 };

    public static void Run()
    {
        string file = Path.Combine(H.Vids, "t10_faces.mp4"), sideways = Path.Combine(H.Vids, "t11_faces_sideways.mp4");
        using var ms = new MediaSource(file, decoders: false);
        var info = ms.Info;

        // 1. The pass over the upright video
        var sw = Stopwatch.StartNew();
        using var finder = new FaceFinder(file, info.Rotation).Start();
        bool ended = finder.Completion.Wait(TimeSpan.FromMinutes(3));
        double secs = sw.Elapsed.TotalSeconds;
        H.Check(ended && finder.Done && finder.Error == null, $"the pass ends: {finder.Error ?? "done"}, {secs:0.0} s for {info.Duration:0.#} s of video ({info.Duration / secs:0.0}× real time)");
        var up = Upright.Select(t => finder.At(t).Count).ToArray();
        var tilt = Tilted.Select(t => finder.At(t).Count).ToArray();
        H.Check(up.All(n => n == 7) && tilt.All(n => n == 3), $"faces found: [{string.Join(",", up)}] upright (7 crew), [{string.Join(",", tilt)}] tilted 15° (3 crew)");
        H.Check(finder.Count is >= 10 and <= 14, $"{finder.Count} faces in all (10 people; a few tracks may break)");

        // 1b. Hard cases: in profile, turned away, looking down, partly covered (a gaiter, a mask). Each face (its middle,
        // in the 1280-wide photo) must be under a cover at each moment; the photos pan up 10 pixels a second from 60.
        string hardFile = Path.Combine(H.Vids, "t13_faces_hard.mp4");
        var hardFaces = new (int X, int Y)[][]
        {
            new[] { (710, 370) },             // a gaiter over the nose, turned to the side, looking down
            new[] { (826, 305) },             // seen from behind, face turned up and away, a mask
            new[] { (940, 300) },             // looking down over someone, safety glasses
            new[] { (430, 330), (840, 290) }, // seen from behind in near profile; facing
            new[] { (315, 330), (615, 290) }, // in profile looking down; further back
        };
        sw.Restart();
        using (var hardFinder = new FaceFinder(hardFile, 0).Start())
        {
            hardFinder.Completion.Wait(TimeSpan.FromMinutes(3));
            double hs = sw.Elapsed.TotalSeconds;
            int covered = 0, total = 0, most = 0;
            var missed = new List<string>();
            for (int i = 0; i < hardFaces.Length; i++)
                foreach (double local in new[] { 0.5, 1.0, 1.5 })
                {
                    double t = 2 * i + local;
                    var boxes = hardFinder.At(t);
                    most = Math.Max(most, boxes.Count - hardFaces[i].Length);
                    foreach (var (x, y) in hardFaces[i])
                    {
                        total++;
                        var p = new PointF(x / 1280f, (y - 60 - 10 * (float)local) / 720f);
                        if (boxes.Any(b => b.Contains(p))) covered++;
                        else missed.Add($"photo {i + 1} face ({x},{y}) at {t:0.0} s");
                    }
                }
            H.Check(hardFinder.Done && covered == total && most <= 2,
                $"hard cases: {covered}/{total} faces covered ({hs:0.0} s for 10 s of video), at most {most} other covers" +
                (missed.Count > 0 ? "; missed " + string.Join(", ", missed) : ""));

            // ... exported pixelated: a picture of each, to look at (faces_hard_N.png)
            using var hm = new MediaSource(hardFile, decoders: false);
            string hardOut = Path.Combine(H.Out, "faces_hard.mp4");
            var htl = TimelineTests.Placed(hm.Info, new[] { new Clip(1, 0, hm.Info.Duration, 0, TimelineTests.Cut) });
            try
            {
                TimelineTests.Export(TimelineTests.Job(hm.Info, htl) with { Faces = new FaceEffect(hardFinder, FaceMode.Pixelate, 60, hm.Info) },
                    new ExportSettings(ExportFormat.H264, ExportQuality.High, true, hardOut));
                for (int i = 0; i < hardFaces.Length; i++)
                    H.Ffmpeg($"-v error -y -ss {2 * i + 1} -i \"{hardOut}\" -frames:v 1 \"{Path.Combine(H.Out, $"faces_hard_{i + 1}.png")}\"");
            }
            catch (Exception ex) { H.Check(false, $"export, hard cases: {ex.Message}"); }
        }

        // 2. Stored sideways: the same faces, turned back to the same places
        using (var ss = new MediaSource(sideways, decoders: false))
        using (var turned = new FaceFinder(sideways, ss.Info.Rotation).Start())
        {
            turned.Completion.Wait(TimeSpan.FromMinutes(3));
            var boxes = turned.At(2.0);
            var reference = finder.At(2.0);
            // the sideways file's boxes (on its frames as stored), shown turned: where the upright file has them
            int matched = boxes.Count(b =>
            {
                float cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
                var (dx, dy) = ss.Info.Rotation switch { 90 => (1 - cy, cx), 270 => (cy, 1 - cx), 180 => (1 - cx, 1 - cy), _ => (cx, cy) };
                return reference.Any(r => Math.Abs(r.X + r.Width / 2 - dx) < 0.03 && Math.Abs(r.Y + r.Height / 2 - dy) < 0.03);
            });
            H.Check(ss.Info.Rotation == 90 && boxes.Count == 7 && matched == 7 && turned.At(6.2).Count == 3,
                $"stored sideways (rotation {ss.Info.Rotation}°): {boxes.Count} faces at 2 s, {matched} where the upright video has them; {turned.At(6.2).Count} at 6.2 s");
        }

        // 3. The preview: faces hidden, the rest untouched
        foreach (var mode in new[] { FaceMode.Pixelate, FaceMode.Blur })
        {
            var fx = new FaceEffect(finder, mode, 60, info);
            var frame = new VideoFrame();
            using var dec = new MediaSource(file);
            dec.DecodeVideoAt(2.0, false, frame, info.Width, info.Height);
            var before = (byte[])frame.Data.Clone();
            fx.Apply(frame);
            var faces = finder.At(2.0);
            var (inside, outside) = Compare(before, frame.Data, frame.Width, frame.Height, 4, faces);
            H.Check(inside.Detail < 0.45 && outside.Change < 0.5, $"preview, {mode}: detail in the faces down to {inside.Detail:P0}, the rest changed by {outside.Change:0.00} (of 255)");
        }

        // 4. The export: blurred and pixelated where the preview hides them
        foreach (var mode in new[] { FaceMode.Blur, FaceMode.Pixelate })
        {
            string outFile = Path.Combine(H.Out, $"faces_{mode}.mp4".ToLowerInvariant());
            var tl = TimelineTests.Placed(info, new[] { new Clip(1, 0, info.Duration, 0, TimelineTests.Cut) });
            var job = TimelineTests.Job(info, tl) with { Faces = new FaceEffect(finder, mode, 60, info) };
            try
            {
                TimelineTests.Export(job, new ExportSettings(ExportFormat.H264, ExportQuality.High, true, outFile));
                var src = Grey(file, 2.0, info.Width, info.Height);
                var res = Grey(outFile, 2.0, info.Width, info.Height);
                var (inside, outside) = Compare(src, res, info.Width, info.Height, 1, finder.At(2.0));
                H.Check(src.Length > 0 && res.Length == src.Length && inside.Detail < 0.45 && outside.Change < 3,
                    $"export, {mode}: detail in the faces down to {inside.Detail:P0}, the rest changed by {outside.Change:0.00} (of 255, encoding)");
            }
            catch (Exception ex) { H.Check(false, $"export, {mode}: {ex.Message}"); }
        }

        // 4b. Two clips far apart in the file (the mask is written for each stretch used, not the whole file): the faces
        // hidden in both, on the right frames
        {
            string outFile = Path.Combine(H.Out, "faces_two_stretches.mp4");
            var tl = TimelineTests.Placed(info, new[] { new Clip(1, 0.5, 1.5, 0, TimelineTests.Cut), new Clip(2, 6.0, 7.5, 1.0, TimelineTests.Cut) });
            var job = TimelineTests.Job(info, tl) with { Faces = new FaceEffect(finder, FaceMode.Blur, 60, info) };
            try
            {
                TimelineTests.Export(job, new ExportSettings(ExportFormat.H264, ExportQuality.High, true, outFile));
                double half = 1 / 60.0; // mid-frame, so both files give the same frame
                foreach (var (src, res, n) in new[] { (1.0, 0.5, 7), (6.5, 1.5, 3) })
                {
                    var a = Grey(file, src + half, info.Width, info.Height);
                    var b = Grey(outFile, res + half, info.Width, info.Height);
                    var faces = finder.At(src + half);
                    var (inside, outside) = Compare(a, b, info.Width, info.Height, 1, faces);
                    H.Check(a.Length > 0 && b.Length == a.Length && faces.Count == n && inside.Detail < 0.45 && outside.Change < 3,
                        $"export of two stretches, at {res:0.0} s (the file's {src:0.0} s): {faces.Count} faces, detail {inside.Detail:P0}, the rest changed by {outside.Change:0.00}");
                }
            }
            catch (Exception ex) { H.Check(false, $"export, two stretches: {ex.Message}"); }
        }

        // 5. The sideways file exported: the faces hidden in the picture as shown
        using (var ss = new MediaSource(sideways, decoders: false))
        using (var turned = new FaceFinder(sideways, ss.Info.Rotation).Start())
        {
            turned.Completion.Wait(TimeSpan.FromMinutes(3));
            string outFile = Path.Combine(H.Out, "faces_sideways.mp4");
            var tl = TimelineTests.Placed(ss.Info, new[] { new Clip(1, 0, ss.Info.Duration, 0, TimelineTests.Cut) });
            var job = TimelineTests.Job(ss.Info, tl) with { Faces = new FaceEffect(turned, FaceMode.Pixelate, 60, ss.Info) };
            try
            {
                TimelineTests.Export(job, new ExportSettings(ExportFormat.H264, ExportQuality.High, true, outFile));
                var (w, h) = ss.Info.DisplaySize();
                var src = Grey(file, 2.0, w, h); // the upright original: the same picture as shown
                var res = Grey(outFile, 2.0, w, h);
                var (inside, outside) = Compare(src, res, w, h, 1, finder.At(2.0));
                H.Check(res.Length == src.Length && inside.Detail < 0.45 && outside.Change < 4,
                    $"export, stored sideways: the faces hidden where they're shown (detail {inside.Detail:P0}, the rest changed by {outside.Change:0.00})");
            }
            catch (Exception ex) { H.Check(false, $"export, sideways: {ex.Message}"); }
        }

        // 6. 10-bit HDR (HLG): the faces hidden, the picture still 10-bit HLG (the mask is merged in its own format)
        string hlg = Path.Combine(H.Vids, "t12_faces_hlg.mov");
        using (var hs = new MediaSource(hlg, decoders: false))
        using (var found = new FaceFinder(hlg, hs.Info.Rotation).Start())
        {
            found.Completion.Wait(TimeSpan.FromMinutes(3));
            string outFile = Path.Combine(H.Out, "faces_hlg.mp4");
            var tl = TimelineTests.Placed(hs.Info, new[] { new Clip(1, 0, hs.Info.Duration, 0, TimelineTests.Cut) });
            var job = TimelineTests.Job(hs.Info, tl) with { Faces = new FaceEffect(found, FaceMode.Blur, 60, hs.Info) };
            try
            {
                TimelineTests.Export(job, new ExportSettings(ExportFormat.Hevc, ExportQuality.Balanced, true, outFile));
                string p = H.Probe(outFile, "-select_streams v -show_entries stream=width,height,color_transfer,pix_fmt -of compact=p=0");
                int w = hs.Info.Width, h = hs.Info.Height, n = found.At(2.0).Count;
                var src = Grey(hlg, 2.0, w, h);
                var res = Grey(outFile, 2.0, w, h);
                var (inside, outside) = Compare(src, res, w, h, 1, found.At(2.0));
                H.Check(p.Contains("10le") && p.Contains("arib-std-b67") && n == 7 && res.Length == src.Length && inside.Detail < 0.45 && outside.Change < 3,
                    $"export, 10-bit HLG: {p}; {n} faces, detail in them down to {inside.Detail:P0}, the rest changed by {outside.Change:0.00}");
            }
            catch (Exception ex) { H.Check(false, $"export, HLG: {ex.Message}"); }
        }
    }

    /// <summary>
    /// The window: Faces is an editing tool (not in the player); its bar's Pixelate starts the pass, the paused frame shows
    /// the faces hidden once found, the slider sets how strongly, the export takes it (and can't copy the original), Done
    /// closes the bar and the faces stay hidden.
    /// </summary>
    public static void Window()
    {
        string file = Path.Combine(H.Vids, "t10_faces.mp4");
        var form = new MainForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900), ClientSize = new Size(1280, 860) };
        TimelineUiTests.ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        TimelineUiTests.Call(form, "Open", file);
        TimelineUiTests.Call(form, "SetVolume", 0f);
        TimelineUiTests.Pump(1.5);
        H.Check(TimelineUiTests.Button(form, "Faces").IsEmpty, "Faces isn't in the player");
        TimelineUiTests.Call(form, "SetEditing", true);
        TimelineUiTests.Call(form, "GoTo", 2.0);
        TimelineUiTests.Pump(0.5);
        TimelineUiTests.Click(form, TimelineUiTests.Centre(TimelineUiTests.Button(form, "Faces")));
        H.Check((bool)TimelineUiTests.Get(form, "_faceBar")!, "the Faces button opens the face bar");

        RectangleF Item(int item) => ((List<(RectangleF R, int Item, string Text)>)TimelineUiTests.Call(form, "FaceBarItems")!).First(i => i.Item == item).R;
        TimelineUiTests.Click(form, TimelineUiTests.Centre(Rectangle.Round(Item(2)))); // Pixelate
        var finder = (FaceFinder?)TimelineUiTests.Get(form, "_finder");
        H.Check(TimelineUiTests.Get(form, "_faceMode")!.ToString() == "Pixelate" && finder != null, "Pixelate starts finding faces");
        var until = DateTime.UtcNow.AddMinutes(2);
        while (finder != null && !finder.Done && finder.Error == null && DateTime.UtcNow < until) TimelineUiTests.Pump(0.2);
        TimelineUiTests.Call(form, "GoTo", 2.0);
        TimelineUiTests.Pump(1.0);
        string status = (string)TimelineUiTests.Call(form, "FaceStatus")!;
        var shown = (VideoFrame?)TimelineUiTests.Get(form, "_shown");
        var raw = new VideoFrame();
        using (var dec = new MediaSource(file)) dec.DecodeVideoAt(2.0, false, raw, shown?.Width ?? 1, shown?.Height ?? 1);
        if (shown == null) { H.Check(false, "a frame is shown"); form.Close(); return; }
        var (inside, outside) = Compare(raw.Data, shown.Data, shown.Width, shown.Height, 4, finder!.At(2.0));
        H.Check(finder!.Done && status.Contains("faces found") && inside.Detail < 0.45 && outside.Change < 1,
            $"the paused frame shows the faces hidden: {status}, detail {inside.Detail:P0}, the rest changed by {outside.Change:0.00}");
        TimelineUiTests.Snap(form, "f1_faces_bar");

        // The slider: 30%
        var slider = Item(3);
        TimelineUiTests.Drag(form, TimelineUiTests.Centre(Rectangle.Round(slider)), new PointF(slider.Left + slider.Width * 0.3f, slider.Top + slider.Height / 2));
        int amount = (int)TimelineUiTests.Get(form, "_faceAmount")!;
        var job = (TrimJob)TimelineUiTests.Call(form, "CurrentJob", (object?)null)!;
        H.Check(Math.Abs(amount - 30) <= 2 && job.Faces is { Mode: FaceMode.Pixelate } && job.Faces.Amount == amount && job.IsEdited
            && Exporter.OriginalUnavailableReason(job) != null, $"the slider sets the amount ({amount}%); the export hides the faces and can't copy the original");

        // A turn keeps the faces found (they're where they were on the frames); Off keeps a finished pass too
        TimelineUiTests.Call(form, "Rotate", 90);
        bool kept = ReferenceEquals(TimelineUiTests.Get(form, "_finder"), finder);
        TimelineUiTests.Call(form, "Rotate", -90);
        TimelineUiTests.Click(form, TimelineUiTests.Centre(Rectangle.Round(Item(0)))); // Off
        bool keptOff = ReferenceEquals(TimelineUiTests.Get(form, "_finder"), finder);
        TimelineUiTests.Click(form, TimelineUiTests.Centre(Rectangle.Round(Item(2)))); // Pixelate again
        H.Check(kept && keptOff && ReferenceEquals(TimelineUiTests.Get(form, "_finder"), finder),
            "a turn, and Off then Pixelate, keep the faces found (no new pass)");

        // Done closes the bar; the faces stay hidden. Back in the player too.
        TimelineUiTests.Click(form, TimelineUiTests.Centre(Rectangle.Round(Item(4))));
        TimelineUiTests.Call(form, "SetEditing", false);
        H.Check(!(bool)TimelineUiTests.Get(form, "_faceBar")! && ((TrimJob)TimelineUiTests.Call(form, "CurrentJob", (object?)null)!).Faces != null,
            "Done closes the bar; the faces stay hidden, in the player too");
        form.Close();
    }

    /// <summary>One frame, grey, at <paramref name="t"/>, full size.</summary>
    static byte[] Grey(string file, double t, int w, int h)
    {
        string raw = Path.Combine(H.Out, Path.GetFileNameWithoutExtension(file) + $"_{t:0.0}.gray");
        H.Ffmpeg($"-v error -y -ss {t.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{file}\" -frames:v 1 -vf scale={w}:{h},format=gray -f rawvideo \"{raw}\"");
        if (!File.Exists(raw)) return Array.Empty<byte>();
        var b = File.ReadAllBytes(raw);
        File.Delete(raw);
        return b;
    }

    /// <summary>
    /// Inside the faces (their middle half): the detail left (sum of differences between neighbours, after / before);
    /// outside them (well clear): the mean change. <paramref name="bpp"/>: 4 for BGRA (green taken), 1 for grey.
    /// </summary>
    static ((double Detail, int N) In, (double Change, int N) Out) Compare(byte[] a, byte[] b, int w, int h, int bpp, List<RectangleF> faces)
    {
        int At(byte[] p, int x, int y) => p[(y * w + x) * bpp + (bpp == 4 ? 1 : 0)];
        double da = 0, db = 0, change = 0;
        int ni = 0, no = 0;
        for (int y = 1; y < h; y += 2)
            for (int x = 1; x < w; x += 2)
            {
                float fx = x / (float)w, fy = y / (float)h;
                bool core = faces.Any(f => Math.Abs(fx - (f.X + f.Width / 2)) < f.Width / 4 && Math.Abs(fy - (f.Y + f.Height / 2)) < f.Height / 4);
                bool near = faces.Any(f => RectangleF.Inflate(f, f.Width * 0.3f, f.Height * 0.3f).Contains(fx, fy));
                if (core)
                {
                    da += Math.Abs(At(a, x, y) - At(a, x - 1, y)) + Math.Abs(At(a, x, y) - At(a, x, y - 1));
                    db += Math.Abs(At(b, x, y) - At(b, x - 1, y)) + Math.Abs(At(b, x, y) - At(b, x, y - 1));
                    ni++;
                }
                else if (!near)
                {
                    change += Math.Abs(At(a, x, y) - At(b, x, y));
                    no++;
                }
            }
        return ((da > 0 ? db / da : 1, ni), (no > 0 ? change / no : 0, no));
    }
}
