using System.Reflection;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// The timeline in the real window, offscreen and never activated (muted): split with S, select by clicking, delete
/// (gap / closed), undo and redo, drag a clip into free space and onto others to rearrange, close gaps, add a sound
/// file and trim and move it, drag a fade handle, transitions, play through a gap (black, no stall), the layout at the
/// smallest window size, the export window and an export of the window's own timeline. Screenshots go to out\.
/// </summary>
static class TimelineUiTests
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    static object? Get(object o, string n) => o.GetType().GetField(n, Any) is FieldInfo fi ? fi.GetValue(o) : o.GetType().GetProperty(n, Any)!.GetValue(o);
    static void Set(object o, string n, object? v) => o.GetType().GetField(n, Any)!.SetValue(o, v);
    static object? Call(object o, string n, params object?[] a) => o.GetType().GetMethods(Any).First(m => m.Name == n && m.GetParameters().Length == a.Length).Invoke(o, a);

    static void Pump(double s)
    {
        var end = DateTime.UtcNow.AddSeconds(s);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    static void Snap(Form f, string name)
    {
        var t = (System.Windows.Forms.Timer)Get(f, "_timer")!;
        t.Stop();
        Set(f, "_ui", 1f);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(Path.Combine(H.Out, name + ".png"));
        t.Start();
        Console.WriteLine($"  saved {name}.png");
    }

    static void Mouse(Form f, string m, PointF p, MouseButtons b = MouseButtons.Left) =>
        f.GetType().GetMethod(m, Any)!.Invoke(f, new object[] { new MouseEventArgs(b, 1, (int)Math.Round(p.X), (int)Math.Round(p.Y), 0) });

    static void Drag(Form f, PointF from, PointF to, bool release = true)
    {
        Set(f, "_ui", 1f);
        Mouse(f, "OnMouseMove", from);
        Mouse(f, "OnMouseDown", from);
        Mouse(f, "OnMouseMove", new PointF((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        Mouse(f, "OnMouseMove", to);
        if (release) Mouse(f, "OnMouseUp", to);
    }

    static void Click(Form f, PointF p) => Drag(f, p, p);

    static bool Key(Form f, Keys k) => (bool)f.GetType().GetMethod("ProcessCmdKey", Any)!.Invoke(f, new object[] { new Message(), k })!;

    static Timeline Tl(Form f) => (Timeline)Get(f, "_tl")!;
    static string Show(Timeline t) => string.Join(" ", t.Clips.Select(c => $"#{c.Id}[{c.In:0.##},{c.Out:0.##})@{c.Start:0.##}")) +
        (t.Music.Count > 0 ? "  ♪ " + string.Join(" ", t.Music.Select(m => $"#{m.Id}[{m.In:0.##},{m.Out:0.##})@{m.Start:0.##}")) : "");
    static float X(Form f, double t) => (float)Call(f, "X", t)!;
    static Rectangle R(Form f, string name) => (Rectangle)Get(f, name)!;
    static PointF ClipCentre(Form f, Clip c) => new(X(f, (c.Start + c.End) / 2), R(f, "VideoRect").Top + R(f, "VideoRect").Height / 2f);
    static double Pos(Form f) => (double)Get(f, "_pos")!;
    static Rectangle Button(Form f, string hit) => (Rectangle)Call(f, "Rect", Enum.Parse(typeof(MainForm).GetNestedType("Hit", Any)!, hit))!;
    static PointF Centre(Rectangle r) => new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

    public static void Run()
    {
        var form = new MainForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900), ClientSize = new Size(1280, 860) };
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        Call(form, "Open", Path.Combine(H.Vids, "t7_counter.mp4"));
        Call(form, "SetVolume", 0f);
        Pump(2.0);
        double fd = 1 / 30.0;

        // Split at 3 s and 6 s.
        Call(form, "GoTo", 3.0); Pump(0.3);
        Key(form, Keys.S);
        Call(form, "GoTo", 6.0); Pump(0.3);
        Key(form, Keys.S);
        var tl = Tl(form);
        H.Check(tl.Clips.Count == 3 && tl.Clips[1].In == 3 && tl.Clips[2].In == 6, $"S splits at the playhead: {Show(tl)}");
        int a = tl.Clips[0].Id, b = tl.Clips[1].Id, c = tl.Clips[2].Id;

        // The undo and redo buttons: back to two clips, then three again (the same timeline).
        Click(form, Centre(Button(form, "Undo")));
        int afterUndo = Tl(form).Clips.Count;
        Click(form, Centre(Button(form, "Redo")));
        H.Check(afterUndo == 2 && ReferenceEquals(Tl(form), tl), $"the undo button takes the second split back ({afterUndo} clips), redo puts it back ({Tl(form).Clips.Count})");
        Click(form, Centre(Button(form, "Redo")));
        H.Check(ReferenceEquals(Tl(form), tl) && (string?)Get(form, "_toast") == "Nothing to redo", $"redo with nothing to redo changes nothing and says so ({Get(form, "_toast")})");

        // Click the middle clip: selected, the playhead goes there.
        Click(form, ClipCentre(form, tl.Clips[1]));
        Pump(0.3);
        H.Check((int)Get(form, "_selClip")! == b && Math.Abs(Pos(form) - 4.5) < 0.1, $"click selects the clip and moves the playhead ({Pos(form):0.##})");
        Snap(form, "u1_three_clips");

        // Delete leaves a gap; undo / redo; Shift+Delete closes it.
        Key(form, Keys.Delete);
        H.Check(Tl(form).Clips.Count == 2 && Tl(form).HasGaps && Tl(form).End == 10, $"Delete leaves a gap: {Show(Tl(form))}");
        Pump(0.4);
        Snap(form, "u2_gap");
        Key(form, Keys.Control | Keys.Z);
        H.Check(Tl(form).Clips.Count == 3, "Ctrl+Z undoes it");
        Key(form, Keys.Control | Keys.Y);
        H.Check(Tl(form).Clips.Count == 2, "Ctrl+Y redoes it");
        Key(form, Keys.Control | Keys.Z);
        Set(form, "_selClip", b);
        Key(form, Keys.Shift | Keys.Delete);
        H.Check(Tl(form).Clips.Count == 2 && !Tl(form).HasGaps && Math.Abs(Tl(form).End - 7) < 1e-9, $"Shift+Delete closes the gap: {Show(Tl(form))}");
        Key(form, Keys.Control | Keys.Z);

        // Drag the last clip into free space past the end (the scale holds during the drag), then onto the first clip.
        tl = Tl(form);
        var vr = R(form, "VideoRect");
        var cc = ClipCentre(form, tl.Find(c)!);
        Drag(form, cc, new PointF(X(form, 10.4), cc.Y), release: false);
        Pump(0.1);
        Snap(form, "u3_dragging");
        Mouse(form, "OnMouseUp", new PointF(X(form, 10.4), cc.Y));
        tl = Tl(form);
        // Grabbed at its middle (2 s in), so it lands 2 s before the pointer: [8.4, 12.4).
        H.Check(Math.Abs(tl.Find(c)!.Start - 8.4) < 0.05 && tl.HasGaps && Math.Abs(tl.End - 12.4) < 0.05, $"a clip dragged into free space moves there: {Show(tl)}");
        Key(form, Keys.Control | Keys.Z);
        tl = Tl(form);
        cc = ClipCentre(form, tl.Find(c)!);
        Drag(form, cc, new PointF(X(form, 0.4), cc.Y));
        tl = Tl(form);
        H.Check(tl.Clips[0].Id == c && tl.Clips[1].Id == a && tl.Clips[2].Id == b && !tl.HasGaps && tl.End == 10, $"dropped onto the first clip: rearranged in front: {Show(tl)}");

        // Close gaps.
        Set(form, "_selClip", a);
        Key(form, Keys.Delete);
        H.Check(Tl(form).HasGaps, "a gap again");
        Call(form, "CloseGaps");
        H.Check(!Tl(form).HasGaps && Math.Abs(Tl(form).End - 7) < 1e-9, $"close gaps: {Show(Tl(form))}");
        Key(form, Keys.Control | Keys.Z);
        Key(form, Keys.Control | Keys.Z);

        // A sound file at 1 s; trim its end by dragging, then move it.
        Call(form, "AddAudio", new[] { Path.Combine(H.Vids, "s1_tone.wav") }, 1.0);
        Pump(0.6);
        tl = Tl(form);
        H.Check(tl.Music.Count == 1 && tl.Music[0].Start == 1 && Math.Abs(tl.Music[0].Length - 3) < 0.01, $"sound added at 1 s: {Show(tl)}");
        var mr = R(form, "MusicRect");
        float my = mr.Top + mr.Height / 2f;
        var m = tl.Music[0];
        Drag(form, new PointF(X(form, m.End), my), new PointF(X(form, m.End - 1), my));
        m = Tl(form).Music[0];
        H.Check(Math.Abs(m.Length - 2) < 0.1 && m.Start == 1 && m.In == 0, $"its end dragged in: {Show(Tl(form))}");
        Drag(form, new PointF(X(form, m.Start + 1), my), new PointF(X(form, m.Start + 3), my));
        m = Tl(form).Music[0];
        H.Check(Math.Abs(m.Start - 3) < 0.1 && Math.Abs(m.Length - 2) < 0.1, $"dragged along: {Show(Tl(form))}");

        // Two sounds that touch: either side of the join grabs the edge of the sound it's in.
        Call(form, "AddAudio", new[] { Path.Combine(H.Vids, "s2_low.mp3") }, m.End);
        Pump(0.4);
        tl = Tl(form);
        string HitAt(float x)
        {
            var h = Call(form, "TimelineHit", new Point((int)Math.Round(x), (int)my))!;
            return $"{h.GetType().GetField("Item1")!.GetValue(h)} {h.GetType().GetField("Item2")!.GetValue(h)}";
        }
        if (tl.Music.Count == 2)
        {
            var (m1, m2) = (tl.Music[0], tl.Music[1]);
            float join = X(form, m1.End);
            H.Check(Math.Abs(m2.Start - m1.End) < 1e-6 && HitAt(join - 3) == $"MusicEnd {m1.Id}" && HitAt(join + 3) == $"MusicStart {m2.Id}",
                $"touching sounds: left of the join {HitAt(join - 3)}, right of it {HitAt(join + 3)} ({Show(tl)})");
        }
        else H.Check(false, $"a second sound added: {Show(tl)}");
        Key(form, Keys.Control | Keys.Z);

        // A drag that ends where it began isn't an undo step.
        var undo = (System.Collections.ICollection)Get(form, "_undo")!;
        int steps = undo.Count;
        tl = Tl(form);
        m = tl.Music[0];
        var mc = new PointF(X(form, m.Start + m.Length / 2), my);
        Set(form, "_ui", 1f);
        Mouse(form, "OnMouseMove", mc);
        Mouse(form, "OnMouseDown", mc);
        Mouse(form, "OnMouseMove", new PointF(mc.X + 40, my));
        Mouse(form, "OnMouseMove", mc);
        Mouse(form, "OnMouseUp", mc);
        H.Check(undo.Count == steps && Tl(form).SameAs(tl), $"a drag back to where it began: {undo.Count - steps} undo steps ({Show(Tl(form))})");

        // Undo, split and delete wait while a drag is under way; the drag is one undo step.
        var endEdge = new PointF(X(form, m.End), my);
        var inward = new PointF(X(form, m.End - 0.5), my);
        Mouse(form, "OnMouseMove", endEdge);
        Mouse(form, "OnMouseDown", endEdge);
        Mouse(form, "OnMouseMove", inward);
        var during = Tl(form);
        Key(form, Keys.Control | Keys.Z);
        Key(form, Keys.S);
        Key(form, Keys.Delete);
        bool untouched = ReferenceEquals(Tl(form), during) && undo.Count == steps;
        Mouse(form, "OnMouseUp", inward);
        H.Check(untouched && undo.Count == steps + 1 && Math.Abs(Tl(form).Music[0].Length - (m.Length - 0.5)) < 0.1,
            $"keys during a drag do nothing ({untouched}); the trim is one undo step: {Show(Tl(form))}");
        Key(form, Keys.Control | Keys.Z);
        H.Check(Tl(form).SameAs(tl), "and undoes in one step");

        // The window loses the mouse mid-drag (a window opened from a key, say): the drag ends there, one undo step.
        Mouse(form, "OnMouseMove", endEdge);
        Mouse(form, "OnMouseDown", endEdge);
        Mouse(form, "OnMouseMove", inward);
        form.Capture = false;
        string dragNow = Get(form, "_drag")!.ToString()!;
        H.Check(dragNow == "None" && undo.Count == steps + 1 && Math.Abs(Tl(form).Music[0].Length - (m.Length - 0.5)) < 0.1,
            $"losing the mouse mid-drag ends the drag ({dragNow}): {Show(Tl(form))}");
        Key(form, Keys.Control | Keys.Z);

        // Fade in on the first clip by dragging its handle.
        tl = Tl(form);
        var first = tl.Clips[0];
        Set(form, "_selClip", first.Id);
        var (fi, _) = ((PointF, PointF))Call(form, "FadeHandles", (RectangleF)Call(form, "ClipRect", first)!, first.FadeIn, first.FadeOut)!;
        Mouse(form, "OnMouseMove", fi);
        string hit = Call(form, "HitTest", Point.Round(fi))!.ToString()!;
        Drag(form, fi, new PointF(X(form, first.Start + 1), fi.Y));
        H.Check(hit == "ClipFadeIn" && Math.Abs(Tl(form).Clips[0].FadeIn - 1) < 0.1, $"fade handle: {hit}, fade in {Tl(form).Clips[0].FadeIn:0.##} s");

        // A transition where two clips meet.
        tl = Tl(form);
        var junction = ((System.Collections.IEnumerable)Call(form, "Junctions")!).Cast<object>().Select(j => (PointF)j.GetType().GetField("Item3")!.GetValue(j)!).First();
        H.Check(Call(form, "HitTest", Point.Round(junction))!.ToString() == "Junction", "the dot where clips meet is clickable");
        Call(form, "SetTransition", tl.Clips[0].Id, new Transition(TransitionKind.Crossfade, 1));
        H.Check(Tl(form).Clips[0].Next.Kind == TransitionKind.Crossfade, "transition set");
        Pump(0.5);
        Snap(form, "u4_sound_fade_transition");

        // Export window and export of the window's own timeline.
        var job = (TrimJob)Call(form, "CurrentJob")!;
        var dlg = new ExportDialog(job) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900) };
        ShowWindow(dlg.Handle, 4);
        Pump(0.6);
        string summary = (string)Call(dlg, "Summary")!;
        H.Check(job.IsEdited && summary.Contains("3 clips") && summary.Contains("1 sound"), $"export summary: {summary}");
        using (var bm = new Bitmap(dlg.Width, dlg.Height))
        {
            dlg.DrawToBitmap(bm, new Rectangle(0, 0, dlg.Width, dlg.Height));
            bm.Save(Path.Combine(H.Out, "u5_export.png"));
        }
        dlg.Close();
        string outFile = Path.Combine(H.Out, "u6_window_timeline.mp4");
        Exporter.RunAsync(job, new ExportSettings(ExportFormat.H264, ExportQuality.Small, true, outFile), null, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        double dur = TimelineTests.Duration(outFile, "v:0");
        H.Check(Math.Abs(dur - Tl(form).End) < 0.05, $"exported {dur} s = the timeline's {Tl(form).End:0.###} s");

        // Play through a gap: black there, no stall, then the next clip.
        Set(form, "_selClip", Tl(form).Clips[1].Id);
        Key(form, Keys.Delete);
        tl = Tl(form);
        var gapFrom = tl.Clips[0].End;
        Call(form, "GoTo", Math.Max(0, gapFrom - 1));
        Pump(0.4);
        Call(form, "Play");
        var seen = new List<(double Pos, bool Black)>();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && (bool)Get(form, "_playing")!)
        {
            Application.DoEvents();
            var shown = (VideoFrame?)Get(form, "_shown");
            double p = Pos(form);
            if (shown != null && (seen.Count == 0 || seen[^1].Pos != p)) seen.Add((p, double.IsNaN(shown.Time)));
            Thread.Sleep(3);
        }
        Call(form, "Pause");
        var gapNext = tl.Clips[1].Start;
        var inGap = seen.Where(s => s.Pos > gapFrom + fd && s.Pos < gapNext - fd).ToList();
        H.Check(seen.Count > 60 && inGap.Count > 20 && inGap.All(s => s.Black) && seen.Any(s => s.Pos > gapNext + 0.2 && !s.Black),
            $"playing through the gap: {seen.Count} frames, {inGap.Count} black in the gap, then the next clip");

        // Looping: the playhead goes round, never past the end of the timeline.
        Set(form, "_loop", true);
        double end = Tl(form).End;
        Call(form, "GoTo", Math.Max(0, end - 0.6));
        Pump(0.3);
        Call(form, "Play");
        var positions = new List<double>();
        until = DateTime.UtcNow.AddSeconds(1.6);
        while (DateTime.UtcNow < until) { Application.DoEvents(); positions.Add(Pos(form)); Thread.Sleep(5); }
        Call(form, "Pause");
        Set(form, "_loop", false);
        H.Check(positions.Max() <= end + 1e-6 && positions.Any(p => p < 0.6), $"looping: the playhead stays on the timeline (at most {positions.Max():0.###} of {end:0.###}) and comes round to the start");

        // Paused at the very end, over black (a sound runs on past the last clip): the playhead stays at the end.
        Call(form, "AddAudio", new[] { Path.Combine(H.Vids, "s2_low.mp3") }, Math.Max(0, Tl(form).End - 0.5));
        Pump(0.4);
        double tail = Tl(form).End;
        Call(form, "GoTo", tail + 5);
        Pump(0.5);
        H.Check(tail > end + 1 && Pos(form) > tail - 0.1, $"at the end of a black tail: the playhead at {Pos(form):0.###} of {tail:0.###}");
        Key(form, Keys.Control | Keys.Z);

        // The export window for a silent video with a sound file on the audio track: there's sound to include.
        using (var ns = new MediaSource(Path.Combine(H.Vids, "t5_noaudio.mkv"), decoders: false))
        {
            var silent = TimelineTests.Placed(ns.Info, new[] { new Clip(1, 0, 3, 0, TimelineTests.Cut) },
                new[] { new AudioClip(10, Path.Combine(H.Vids, "s1_tone.wav"), 0, 3, 0, 3) });
            var sd = new ExportDialog(TimelineTests.Job(ns.Info, silent)) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900) };
            ShowWindow(sd.Handle, 4);
            Pump(0.3);
            H.Check((bool)Get(sd, "_audio")! && (bool)Get(sd, "HasSound")!, "export window, silent video + a sound file: sound is included");
            sd.Close();
        }

        // Smallest window: the buttons don't run into each other.
        form.ClientSize = form.MinimumSize;
        Pump(0.4);
        Rectangle Btn(string h) => (Rectangle)Call(form, "Rect", Enum.Parse(typeof(MainForm).GetNestedType("Hit", Any)!, h))!;
        int Gap(string a, string b) => Btn(b).Left - Btn(a).Right;
        // Groups: playback | undo, redo | the tools (split … crop, evenly spaced)
        H.Check(Gap("Next", "Undo") > Gap("Prev", "Next") && Gap("Redo", "Split") > Gap("Undo", "Redo") && Gap("AddAudio", "RotL") == Gap("Split", "Delete")
            && Gap("Crop", "Vol") >= 4,
            $"smallest window: undo {Btn("Undo")}, add sound {Btn("AddAudio")}, crop {Btn("Crop")}, volume {Btn("Vol")}");
        Snap(form, "u7_smallest_window");
        form.Close();
    }
}
