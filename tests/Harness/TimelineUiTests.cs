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
    [DllImport("user32.dll")] internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    internal static object? Get(object o, string n) => o.GetType().GetField(n, Any) is FieldInfo fi ? fi.GetValue(o) : o.GetType().GetProperty(n, Any)!.GetValue(o);
    internal static void Set(object o, string n, object? v) => o.GetType().GetField(n, Any)!.SetValue(o, v);
    internal static object? Call(object o, string n, params object?[] a) => o.GetType().GetMethods(Any).First(m => m.Name == n && m.GetParameters().Length == a.Length).Invoke(o, a);

    internal static void Pump(double s)
    {
        var end = DateTime.UtcNow.AddSeconds(s);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    internal static void Snap(Form f, string name)
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

    internal static void Mouse(Form f, string m, PointF p, MouseButtons b = MouseButtons.Left) =>
        f.GetType().GetMethod(m, Any)!.Invoke(f, new object[] { new MouseEventArgs(b, 1, (int)Math.Round(p.X), (int)Math.Round(p.Y), 0) });

    internal static void Drag(Form f, PointF from, PointF to, bool release = true)
    {
        Set(f, "_ui", 1f);
        Mouse(f, "OnMouseMove", from);
        Mouse(f, "OnMouseDown", from);
        Mouse(f, "OnMouseMove", new PointF((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        Mouse(f, "OnMouseMove", to);
        if (release) Mouse(f, "OnMouseUp", to);
    }

    internal static void Click(Form f, PointF p) => Drag(f, p, p);

    internal static bool Key(Form f, Keys k) => (bool)f.GetType().GetMethod("ProcessCmdKey", Any)!.Invoke(f, new object[] { new Message(), k })!;

    internal static Timeline Tl(Form f) => (Timeline)Get(f, "_tl")!;
    internal static string Show(Timeline t) => string.Join(" ", t.Clips.Select(c => $"#{c.Id}[{c.In:0.##},{c.Out:0.##})@{c.Start:0.##}")) +
        (t.Music.Count > 0 ? "  ♪ " + string.Join(" ", t.Music.Select(m => $"#{m.Id}[{m.In:0.##},{m.Out:0.##})@{m.Start:0.##}")) : "");
    internal static float X(Form f, double t) => (float)Call(f, "X", t)!;
    internal static Rectangle R(Form f, string name) => (Rectangle)Get(f, name)!;
    internal static PointF ClipCentre(Form f, Clip c) => new(X(f, (c.Start + c.End) / 2), R(f, "VideoRect").Top + R(f, "VideoRect").Height / 2f);
    internal static double Pos(Form f) => (double)Get(f, "_pos")!;
    internal static Rectangle Button(Form f, string hit) => (Rectangle)Call(f, "Rect", Enum.Parse(typeof(MainForm).GetNestedType("Hit", Any)!, hit))!;
    internal static PointF Centre(Rectangle r) => new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

    public static void Run()
    {
        var form = new MainForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900), ClientSize = new Size(1280, 860) };
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        // With no video the panel is just its header and buttons; a video brings the timeline in between.
        Pump(0.3);
        var emptyPanel = R(form, "PanelRect");
        var emptyPlay = Button(form, "Play");
        var add = Button(form, "Center");
        int captionH = TextRenderer.MeasureText("Drop a video here, or click the button to browse", (Font)Get(form, "_hintFont")!).Height;
        float groupMid = (add.Top + add.Bottom + 10 + captionH) / 2f;
        H.Check(Math.Abs(groupMid - emptyPanel.Top / 2f) <= 2, $"no video: the add button and its caption centred above the panel ({groupMid:0.#} vs {emptyPanel.Top / 2f:0.#})");
        Snap(form, "u0_no_video");
        Call(form, "Open", Path.Combine(H.Vids, "t7_counter.mp4"));
        Call(form, "SetVolume", 0f);
        Pump(2.0);
        // A video opens in the player: a compact timeline, no editing tools, Edit where Export is; S does nothing there.
        var playerPanel = R(form, "PanelRect");
        H.Check(!(bool)Get(form, "_editing")! && Button(form, "Split").IsEmpty && Button(form, "Export").IsEmpty && !Button(form, "Edit").IsEmpty
            && playerPanel.Height > emptyPanel.Height && Call(form, "TimelineHit", Point.Round(Centre(R(form, "VideoRect"))))!.ToString()!.Contains("Track"),
            $"a video opens in the player: panel {playerPanel.Height} px, Edit button {Button(form, "Edit")}");
        Key(form, Keys.S);
        H.Check(Tl(form).Clips.Count == 1, "S does nothing in the player");
        Snap(form, "u0b_player");
        Click(form, Centre(Button(form, "Edit")));
        Pump(0.2);
        var fullPanel = R(form, "PanelRect");
        var fullPlay = Button(form, "Play");
        H.Check(emptyPanel.Bottom == fullPanel.Bottom && emptyPanel.Height < fullPanel.Height * 0.6 && emptyPlay.Top > emptyPanel.Top + 60
            && emptyPlay.Bottom < emptyPanel.Bottom && fullPlay.Top > R(form, "MusicRect").Bottom && (bool)Get(form, "_editing")!
            && fullPanel.Height > playerPanel.Height && !Button(form, "Split").IsEmpty && !Button(form, "EditDone").IsEmpty,
            $"no video: panel {emptyPanel.Height} px high, play button at {emptyPlay.Top}; Edit opens the editor: {fullPanel.Height} px, play button below the timeline");
        double fd = 1 / 30.0;

        // The whole video dragged right 3 s (a gap at the start), then back to 0: the timeline is 10 s again.
        {
            var vr0 = R(form, "VideoRect");
            var whole = Tl(form).Clips[0];
            float y = vr0.Top + vr0.Height / 2f;
            Drag(form, new PointF(X(form, 5), y), new PointF(X(form, 8), y));
            var right = Tl(form);
            double viewRight = (double)Get(form, "_viewLen")!;
            float endX = X(form, right.End), trackEnd = R(form, "VideoRect").Right;
            Drag(form, new PointF(X(form, 8), y), new PointF(X(form, 5), y));
            var back = Tl(form);
            double viewBack = (double)Get(form, "_viewLen")!;
            H.Check(Math.Abs(right.Clips[0].Start - 3) < 0.05 && Math.Abs(right.End - 13) < 0.05,
                $"dragged right 3 s: {Show(right)}, {right.End:0.###} s long, view {viewRight:0.##} s");
            H.Check(Math.Abs(viewRight - right.End) < 1e-9 && Math.Abs(endX - trackEnd) < 1.5,
                $"the timeline ends where the video does: view {viewRight:0.###} s, its end at x {endX:0.#} (track ends at {trackEnd})");
            H.Check(back.Clips[0].Start == 0 && Math.Abs(back.End - whole.Length) < 1e-6 && Math.Abs(viewBack - back.End) < 1e-9,
                $"dragged back to 0: {Show(back)}, {back.End:0.###} s long (expected {whole.Length:0.###}), view {viewBack:0.##} s");
            while (((System.Collections.ICollection)Get(form, "_undo")!).Count > 0) Key(form, Keys.Control | Keys.Z);

            // The same with real mouse messages (Windows' own capture handling), many small moves, 3 s right and back.
            void Send(int msg, PointF p, int buttons)
            {
                Set(form, "_ui", 1f);
                SendMessage(form.Handle, msg, (IntPtr)buttons, (IntPtr)(((int)Math.Round(p.Y) << 16) | ((int)Math.Round(p.X) & 0xFFFF)));
                Application.DoEvents();
            }
            void RealDrag(double from, double to)
            {
                Send(0x200, new PointF(X(form, from), y), 0);
                Send(0x201, new PointF(X(form, from), y), 1);
                for (int k = 1; k <= 20; k++) Send(0x200, new PointF(X(form, from + (to - from) * k / 20), y), 1);
                Send(0x202, new PointF(X(form, to), y), 0);
                Pump(0.1);
            }
            RealDrag(5, 8);
            var right2 = Tl(form);
            RealDrag(8, 5);
            var back2 = Tl(form);
            H.Check(Math.Abs(right2.End - 13) < 0.05 && back2.Clips[0].Start == 0 && Math.Abs(back2.End - whole.Length) < 1e-6,
                $"real mouse: right {Show(right2)} ({right2.End:0.###} s), back {Show(back2)} ({back2.End:0.###} s), view {(double)Get(form, "_viewLen")!:0.##} s");
            while (((System.Collections.ICollection)Get(form, "_undo")!).Count > 0) Key(form, Keys.Control | Keys.Z);
        }

        // Trim, as earlier versions did, on the one clip: T; the yellow handles to 3–6 s; the grip (on the ruler) slides the
        // selection to 5–8 s; playing plays just that; Enter applies it (3 s, from 0). Then I and O at the playhead; Esc cancels.
        {
            var vr0 = R(form, "VideoRect");
            float y = vr0.Top + vr0.Height / 2f;
            double In() => (double)Get(form, "_trimIn")!;
            double Out() => (double)Get(form, "_trimOut")!;
            bool Trimming() => (bool)Get(form, "Trimming")!;
            var trimUndo = (System.Collections.ICollection)Get(form, "_undo")!;
            Key(form, Keys.T);
            H.Check(Trimming() && Math.Abs((double)Get(form, "_viewLen")! - 10) < 1e-6, "T starts trimming: a selection over the clip");
            Drag(form, new PointF(X(form, 0) + 2, y), new PointF(X(form, 3) + 2, y));
            Drag(form, new PointF(X(form, 10) - 2, y), new PointF(X(form, 6) - 2, y));
            H.Check(Math.Abs(In() - 3) < 0.05 && Math.Abs(Out() - 6) < 0.05, $"the yellow handles: keeping {In():0.###}–{Out():0.###}");
            Mouse(form, "OnMouseMove", new PointF(X(form, 3) + 5, y));
            Mouse(form, "OnMouseDown", new PointF(X(form, 3) + 5, y));
            Mouse(form, "OnMouseUp", new PointF(X(form, 3) + 5, y));
            H.Check(Math.Abs(In() - 3) < 0.05, $"a handle pressed off its centre stays put: {In():0.###}");
            Pump(0.3);
            Snap(form, "t1_trimming");
            var grip = (RectangleF)Get(form, "TrimGripRect")!;
            float gy = grip.Top + grip.Height / 2;
            double before = Out() - In();
            Drag(form, new PointF(X(form, 4.5), gy), new PointF(X(form, 6.5), gy));
            H.Check(Math.Abs(In() - 5) < 0.05 && Math.Abs(Out() - In() - before) < 1e-6, $"the grip moves the part kept: {In():0.###}–{Out():0.###}");
            Call(form, "Play");
            var heard = new List<double>();
            var stop = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < stop && (bool)Get(form, "_playing")!) { Application.DoEvents(); heard.Add(Pos(form)); Thread.Sleep(5); }
            Call(form, "Pause");
            H.Check(heard.Count > 20 && heard.Min() >= In() - 1e-6 && heard.Max() < Out(), $"playing plays the part kept: {heard.Min():0.##}–{heard.Max():0.##}");
            int trimSteps = trimUndo.Count;
            Key(form, Keys.Enter);
            var kept = Tl(form);
            H.Check(!Trimming() && kept.Clips.Count == 1 && Math.Abs(kept.Clips[0].In - 5) < 0.05 && kept.Clips[0].Start == 0 && Math.Abs(kept.End - before) < 1e-6
                && trimUndo.Count == trimSteps + 1, $"Enter keeps it: {Show(kept)}");
            Key(form, Keys.Control | Keys.Z);
            H.Check(Math.Abs(Tl(form).Clips[0].Length - 10) < 1e-6, "and undoes in one step");
            Call(form, "GoTo", 2.0); Pump(0.2);
            Key(form, Keys.I);
            Call(form, "GoTo", 4.0); Pump(0.2);
            Key(form, Keys.OemCloseBrackets);
            H.Check(Trimming() && Math.Abs(In() - 2) < 1e-6 && Math.Abs(Out() - (4 + fd)) < 1e-6, $"I and ] at the playhead: {In():0.###}–{Out():0.###}");
            trimSteps = trimUndo.Count;
            Key(form, Keys.Escape);
            H.Check(!Trimming() && Math.Abs(Tl(form).Clips[0].Length - 10) < 1e-6 && trimUndo.Count == trimSteps, "Esc leaves the clip as it was");
        }

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

        // Trim across clips: the selection 2–7 s over all three (I and O); Apply trim keeps 2–3, 3–6 and 6–7 from 0, one undo
        // step. Then T and Cancel: nothing changes.
        {
            var trimUndo = (System.Collections.ICollection)Get(form, "_undo")!;
            Call(form, "GoTo", 2.0); Pump(0.2);
            Key(form, Keys.I);
            Call(form, "GoTo", 7.0 - fd); Pump(0.2);
            Key(form, Keys.O);
            Pump(0.2);
            Snap(form, "t2_trim_across");
            int trimSteps = trimUndo.Count;
            // A click on the grip (no drag) moves the playhead there; the selection stays. C waits; the export is the selection.
            var gripAt = new PointF(X(form, 4), R(form, "RulerRect").Top + R(form, "RulerRect").Height / 2f);
            Click(form, gripAt);
            Pump(0.2);
            H.Check(Math.Abs(Pos(form) - 4) < 0.1 && Math.Abs((double)Get(form, "_trimIn")! - 2) < 1e-6, $"a click on the grip moves the playhead: {Pos(form):0.##}");
            Key(form, Keys.C);
            var selJob = (TrimJob)Call(form, "CurrentJob", Call(form, "Kept"))!;
            H.Check((bool)Get(form, "Trimming")! && !(bool)Get(form, "_cropping")! && Math.Abs(selJob.OutputLength() - 5) < 1e-6,
                $"C waits for the trim; exporting while trimming exports the selection ({selJob.OutputLength():0.###} s)");
            Click(form, Centre(Button(form, "TrimApply")));
            var cut = Tl(form);
            H.Check(!(bool)Get(form, "Trimming")! && cut.Clips.Count == 3 && cut.Clips[0].Start == 0 && Math.Abs(cut.Clips[0].In - 2) < 1e-6
                && Math.Abs(cut.Clips[2].Out - 7) < 1e-6 && Math.Abs(cut.End - 5) < 1e-6 && trimUndo.Count == trimSteps + 1, $"Apply trim across clips: {Show(cut)}");
            Key(form, Keys.Control | Keys.Z);
            H.Check(Tl(form).SameAs(tl), $"and undoes in one step: {Show(Tl(form))}");
            Key(form, Keys.T);
            Click(form, Centre(Button(form, "TrimCancel")));
            H.Check(!(bool)Get(form, "Trimming")! && Tl(form).SameAs(tl) && trimUndo.Count == trimSteps, "T then Cancel leaves the timeline as it was");
        }

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
        var job = (TrimJob)Call(form, "CurrentJob", (object?)null)!;
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

        // The control panel: pinned by default (full width, under the picture, there when the overlay hides); P unpins it.
        form.ClientSize = new Size(1280, 860);
        Pump(0.3);
        var pr1 = R(form, "PanelRect");
        var pic1 = (RectangleF)Get(form, "VideoRectF")!;
        Set(form, "_ui", 0f);
        string idle1 = Call(form, "HitTest", Point.Round(Centre(Button(form, "Split"))))!.ToString()!;
        Set(form, "_ui", 1f);
        H.Check((bool)Get(form, "_pinned")! && pr1.Left == 0 && pr1.Width == form.ClientSize.Width && pr1.Bottom == form.ClientSize.Height
            && pic1.Top >= -0.5f && pic1.Bottom <= pr1.Top + 0.5f && idle1 == "Split",
            $"pinned: panel {pr1}, picture {pic1} above it, a button with the overlay hidden: {idle1}");
        Snap(form, "u8_pinned");
        // The panel's background between the button groups: nothing happens there (it isn't the picture).
        var gapPt = new PointF((Button(form, "Crop").Right + Button(form, "Vol").Left) / 2f, Centre(Button(form, "Crop")).Y);
        string gapHit = Call(form, "HitTest", Point.Round(gapPt))!.ToString()!;
        Pump(0.6);
        Click(form, gapPt);
        Pump(0.2);
        H.Check(gapHit == "Panel" && !(bool)Get(form, "_playing")!, $"a click on the panel's background ({gapHit}) doesn't play");
        Key(form, Keys.P);
        Pump(0.2);
        var pr2 = R(form, "PanelRect");
        var pic2 = (RectangleF)Get(form, "VideoRectF")!;
        Set(form, "_ui", 0f);
        string idle2 = Call(form, "HitTest", Point.Round(Centre(Button(form, "Split"))))!.ToString()!;
        Set(form, "_ui", 1f);
        H.Check(!(bool)Get(form, "_pinned")! && pr2.Left > 0 && pr2.Bottom < form.ClientSize.Height && pic2.Height > pic1.Height && idle2 == "None",
            $"unpinned: panel {pr2} floats, picture {pic2} fills the window, hidden with the overlay ({idle2})");
        Snap(form, "u9_unpinned");
        var pinAt = Centre(Button(form, "Pin"));
        Click(form, pinAt);
        Click(form, pinAt); // quickly again where the pin was: it moved, and the click is ignored
        Pump(0.2);
        H.Check((bool)Get(form, "_pinned")! && !(bool)Get(form, "_playing")!, "the pin button pins it again; a quick second click where it was does nothing");

        // Smallest window: the buttons don't run into each other.
        form.ClientSize = form.MinimumSize;
        Pump(0.4);
        Rectangle Btn(string h) => (Rectangle)Call(form, "Rect", Enum.Parse(typeof(MainForm).GetNestedType("Hit", Any)!, h))!;
        int Gap(string a, string b) => Btn(b).Left - Btn(a).Right;
        // Groups: playback | undo, redo | the tools (split … faces, evenly spaced) | the player's (volume, screenshot, loop,
        // fullscreen, more), apart by at least a group's gap, pinned or not
        H.Check(Gap("Next", "Undo") > Gap("Prev", "Next") && Gap("Redo", "Split") > Gap("Undo", "Redo") && Gap("AddAudio", "RotL") == Gap("Split", "Trim") && Gap("Trim", "Delete") == Gap("Split", "Trim")
            && Gap("Faces", "Vol") >= 16 && Gap("VolSlider", "Shot") > Gap("Shot", "Loop") && Gap("Shot", "Loop") == Gap("Loop", "Full"),
            $"smallest window: undo {Btn("Undo")}, add sound {Btn("AddAudio")}, faces {Btn("Faces")}, volume {Btn("Vol")}, screenshot {Btn("Shot")}");
        Snap(form, "u7_smallest_window");
        Key(form, Keys.P);
        Pump(0.2);
        int floating = Gap("Faces", "Vol");
        Key(form, Keys.P);
        Pump(0.2);
        H.Check(floating >= 16 && (bool)Get(form, "_pinned")!, $"smallest window, unpinned: {floating} px between the tools and the volume");

        // Done goes back to the player (the edits stay).
        var edited = Tl(form);
        Click(form, Centre(Button(form, "EditDone")));
        H.Check(!(bool)Get(form, "_editing")! && ReferenceEquals(Tl(form), edited) && Button(form, "Crop").IsEmpty, "Done goes back to the player, the edits kept");

        // A WebM at an odd size opens, shows its thumbnails and plays (its pictures converted without overrunning their buffers).
        Call(form, "Open", Path.Combine(H.Vids, "t9_vp8_odd.webm"));
        Pump(1.5);
        Call(form, "Play");
        Pump(1.0);
        Call(form, "Pause");
        Pump(0.3);
        var webmInfo = (MediaInfo)Get(form, "_info")!;
        H.Check(webmInfo.Width == 811 && webmInfo.VideoCodec.Length > 0 && Pos(form) > 0.5, $"a WebM, VP8 811×1442, opens and plays: at {Pos(form):0.##} s");
        Snap(form, "u10_webm");
        form.Close();
    }

    internal static MainForm OpenOffscreen(string name)
    {
        var form = new MainForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900), ClientSize = new Size(1280, 860) };
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        Call(form, "Open", Path.Combine(H.Vids, name));
        Call(form, "SetVolume", 0f);
        Pump(1.0);
        return form;
    }

    /// <summary>The picture shown after a key, once it has come (or the one before, after half a second).</summary>
    static VideoFrame? After(Form form, Keys key)
    {
        var before = Get(form, "_shown");
        Key(form, key);
        var until = DateTime.UtcNow.AddSeconds(0.5);
        while (DateTime.UtcNow < until && ReferenceEquals(Get(form, "_shown"), before)) Pump(0.01);
        Pump(0.02);
        return (VideoFrame?)Get(form, "_shown");
    }

    /// <summary>
    /// Frame by frame with → and ←, where the frames aren't on the frame grid: a 60 fps Matroska file (timestamps in
    /// whole milliseconds) and one with a variable frame rate (every fifth frame left out). Each press moves the playhead;
    /// the pictures go one way, and none is skipped.
    /// </summary>
    public static void Stepping()
    {
        foreach (var (name, frames) in new[] { ("t5_noaudio.mkv", 40), ("t14_vfr.mkv", 30) })
        {
            var form = OpenOffscreen(name);
            double fd = (double)Get(form, "FrameDur")!;
            var times = new List<double> { ((VideoFrame)Get(form, "_shown")!).Time };
            var pos = new List<double> { Pos(form) };
            for (int i = 0; i < frames; i++)
            {
                times.Add(After(form, Keys.Right)?.Time ?? double.NaN);
                pos.Add(Pos(form));
            }
            var steps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
            int stuck = 0, run = 0;
            foreach (var s in steps) { run = s < 1e-6 ? run + 1 : 0; stuck = Math.Max(stuck, run); }
            bool moved = pos.Zip(pos.Skip(1)).All(p => p.Second > p.First + fd / 2);
            // A frame left out shows its predecessor for two steps (once), then the next frame two frames on: a long step
            // beside a repeat. Never more repeats than that, and no long step without one (a frame skipped).
            bool Fine(List<double> d) => d.Select((s, i) => s >= -1e-6 && (s < 1.5 * fd + 1e-3
                || s < 2.5 * fd && (i > 0 && d[i - 1] < 1e-6 || i + 1 < d.Count && d[i + 1] < 1e-6))).All(ok => ok);
            bool forward = Fine(steps) && stuck <= (name == "t14_vfr.mkv" ? 1 : 0);
            var back = new List<double> { times[^1] };
            for (int i = 0; i < 10; i++) back.Add(After(form, Keys.Left)?.Time ?? double.NaN);
            bool backward = Fine(back.Zip(back.Skip(1), (a, b) => a - b).ToList());
            H.Check(moved && forward && backward,
                $"frame by frame, {name}: {frames} steps forward to {times[^1]:0.###} s (the longest {steps.Max() * 1000:0} ms, {stuck} repeats in a row), the playhead moving each time; 10 back to {back[^1]:0.###} s");

            // At the end: → stays on the last frame (the playhead on its start), ← goes back one
            double end = Tl(form).End;
            Call(form, "GoTo", end);
            Pump(0.3);
            var last = After(form, Keys.Right)?.Time ?? double.NaN;
            double atEnd = Pos(form);
            var again = After(form, Keys.Right)?.Time ?? double.NaN;
            var before = After(form, Keys.Left)?.Time ?? double.NaN;
            H.Check(Math.Abs(atEnd - Math.Floor((end - 1e-6) / fd) * fd) < 1e-6 && Math.Abs(Pos(form) - (atEnd - fd)) < 1e-6 && again == last && before < last,
                $"frame by frame at the end, {name}: the playhead stops on the last frame's start ({atEnd:0.###} of {end:0.###} s), → stays, ← goes back to {before:0.###} s");
            form.Close();
        }
    }

    /// <summary>
    /// A clip a few pixels wide (split near the start of a 20 s video) can be selected by clicking anywhere on it, at the
    /// top where its fade handles are, beside the transition dot, or on its waveform.
    /// </summary>
    public static void NarrowClip()
    {
        var form = OpenOffscreen("t1_h264_1080p30.mp4");
        Call(form, "SetEditing", true);
        Call(form, "GoTo", 0.4);
        Pump(0.2);
        Key(form, Keys.S);
        Pump(0.2);
        var tl = Tl(form);
        int left = tl.Clips[0].Id, right = tl.Clips[1].Id;
        var v = R(form, "VideoRect");
        var w = R(form, "WaveRect");
        float x0 = X(form, 0), x1 = X(form, 0.4);
        var missed = new List<string>();
        int tried = 0;
        foreach (float fx in new[] { 0.1f, 0.3f, 0.5f, 0.7f, 0.85f })
            foreach (var (row, y) in new[] { ("top", v.Top + 3f), ("middle", v.Top + v.Height / 2f), ("bottom", v.Bottom - 4f), ("waveform", w.Top + w.Height / 2f) })
            {
                Set(form, "_selClip", right);
                var p = new PointF(x0 + (x1 - x0) * fx, y);
                if (Math.Sqrt((p.X - x1) * (p.X - x1) + (p.Y - v.Bottom) * (p.Y - v.Bottom)) <= 9) continue; // on the transition dot
                Call(form, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, (int)p.X, (int)p.Y, 0)); // hovering it first
                Click(form, p);
                Pump(0.05);
                tried++;
                if ((int)Get(form, "_selClip")! != left) missed.Add($"{row} at {fx:P0} ({Call(form, "HitTest", Point.Round(p))})");
            }
        H.Check(missed.Count == 0 && Tl(form).SameAs(tl),
            $"a clip {x1 - x0:0} px wide before a cut: {tried - missed.Count}/{tried} clicks select it" + (missed.Count > 0 ? "; missed " + string.Join(", ", missed) : ""));

        // Narrower than its two fade handles (they share its middle): hovering, selecting and drawing it work
        Call(form, "GoTo", 0.13);
        Pump(0.2);
        Key(form, Keys.S);
        Pump(0.2);
        var tiny = Tl(form).Clips[0];
        float tx0 = X(form, tiny.Start), tx1 = X(form, tiny.End);
        string? error = null;
        try
        {
            var mid = new PointF((tx0 + tx1) / 2, v.Top + v.Height / 2f);
            Call(form, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, (int)mid.X, (int)mid.Y, 0));
            Click(form, mid);
            Call(form, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, (int)mid.X, (int)v.Top + 3, 0));
            using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.ClientSize));
        }
        catch (Exception ex) { error = (ex.InnerException ?? ex).Message; }
        H.Check(error == null && (int)Get(form, "_selClip")! == tiny.Id,
            $"a clip {tx1 - tx0:0.#} px wide: hovered, clicked (selected) and drawn{(error != null ? ": " + error : "")}");
        form.Close();
    }
}
