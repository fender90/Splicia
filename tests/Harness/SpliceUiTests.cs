using System.Reflection;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Splicing in the real window, offscreen and never activated (muted, so nothing is heard): S / S again / Esc, merging,
/// the whole-selection guard, the transition menu, dragging a cut's edge, hit testing, playback skipping the cut-out
/// parts, the fade preview, the layout at the smallest window size, the export window, and an export of the window's
/// own job. Screenshots go to out\.
/// </summary>
static class SpliceUiTests
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

    static bool Key(Form f, Keys k) => (bool)f.GetType().GetMethod("ProcessCmdKey", Any)!.Invoke(f, new object[] { new Message(), k })!;

    static List<Splice> Splices(Form f) => (List<Splice>)Get(f, "_splices")!;
    static string Show(List<Splice> l) => string.Join(" ", l.Select(s => $"[{s.From:0.###},{s.To:0.###}) {s.Transition}"));
    static double Pos(Form f) => (double)Get(f, "_pos")!;
    static float X(Form f, double t) => (float)Call(f, "X", t)!;
    static float StripMid(Form f) { var r = (Rectangle)Get(f, "StripRect")!; return r.Top + r.Height / 2f; }

    static void GoTo(Form f, double t) { Call(f, "GoTo", t); Pump(0.5); }

    static void ClickMenu(Form f, int splice, string text)
    {
        Call(f, "PopulateSpliceMenu", splice);
        var menu = (ContextMenuStrip)Get(f, "_spliceMenu")!;
        var item = menu.Items.OfType<ToolStripMenuItem>().First(i => i.Text!.Trim().TrimStart('✓').Trim() == text);
        H.Check(item.Enabled, $"menu item '{text}' is enabled");
        item.PerformClick();
    }

    public static void Run()
    {
        var form = new MainForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900), ClientSize = new Size(1280, 800) };
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        Call(form, "Open", Path.Combine(H.Vids, "t7_counter.mp4"));
        Call(form, "SetVolume", 0f);
        Pump(2.0);
        double fd = 1 / 30.0;

        // S at frame 90, S again at frame 119: frames 90–119 are cut, i.e. [3, 4).
        GoTo(form, 3.0);
        Key(form, Keys.S);
        H.Check(Get(form, "_spliceMark") is double m && Math.Abs(m - 3) < 1e-3, "S marks where the cut starts");
        Snap(form, "s1_mark_pending");
        GoTo(form, 119 * fd);
        Snap(form, "s2_mark_extended");
        Key(form, Keys.S);
        Pump(0.5);
        var l = Splices(form);
        H.Check(l.Count == 1 && Math.Abs(l[0].From - 3) < 1e-3 && Math.Abs(l[0].To - 4) < 1e-3 && Get(form, "_spliceMark") == null,
            $"S again cuts frames 90–119: {Show(l)}");
        H.Check(Math.Abs(Pos(form) - 4) < 0.02, $"the playhead goes to the first frame after the join: {Pos(form):0.###}");

        // Esc cancels a started cut.
        GoTo(form, 5.0);
        Key(form, Keys.S);
        Key(form, Keys.Escape);
        H.Check(Get(form, "_spliceMark") == null && Splices(form).Count == 1, "Esc cancels a started cut");

        // A second cut (backwards: end first), then one overlapping it, which merges.
        GoTo(form, 209 * fd);
        Key(form, Keys.S);
        GoTo(form, 6.0);
        Key(form, Keys.S);
        Pump(0.3);
        l = Splices(form);
        H.Check(l.Count == 2 && Math.Abs(l[1].From - 6) < 1e-3 && Math.Abs(l[1].To - 7) < 1e-3, $"marking the end first works too: {Show(l)}");
        GoTo(form, 6.5);
        Key(form, Keys.S);
        GoTo(form, 224 * fd);
        Key(form, Keys.S);
        Pump(0.3);
        l = Splices(form);
        H.Check(l.Count == 2 && Math.Abs(l[1].From - 6) < 1e-3 && Math.Abs(l[1].To - 7.5) < 1e-3, $"an overlapping cut merges: {Show(l)}");

        // Never the whole selection.
        Call(form, "AddSplice", 0.0, 10.0);
        H.Check(Splices(form).Count == 2, "a cut over the whole selection is refused");

        // Transitions from the dot's menu; new cuts get the last one picked.
        ClickMenu(form, 1, "Crossfade");
        ClickMenu(form, 1, "1 s");
        l = Splices(form);
        H.Check(l[1].Transition == new Transition(TransitionKind.Crossfade, 1) && l[0].Transition.Kind == TransitionKind.None, $"crossfade 1 s on the second cut: {Show(l)}");
        ClickMenu(form, 1, "Use this transition for every splice");
        H.Check(Splices(form).All(s => s.Transition == new Transition(TransitionKind.Crossfade, 1)), "use for every splice");
        ClickMenu(form, 0, "Fade out, fade in");
        H.Check(Splices(form)[0].Transition == new Transition(TransitionKind.FadeThroughBlack, 1), $"fade on the first cut: {Show(Splices(form))}");
        H.Check((Transition)Get(form, "_nextTransition")! == new Transition(TransitionKind.FadeThroughBlack, 1), "the next cut gets the last transition picked");
        Call(form, "PopulateSpliceMenu", 0);
        var lengths = ((ContextMenuStrip)Get(form, "_spliceMenu")!).Items.OfType<ToolStripMenuItem>().Where(i => i.Text!.EndsWith(" s") && !i.Text.Contains('(')).ToList();
        H.Check(lengths.Count == 4 && lengths.All(i => i.Enabled), "four lengths, enabled for a transition");
        ClickMenu(form, 0, "None (straight cut)");
        Call(form, "PopulateSpliceMenu", 0);
        lengths = ((ContextMenuStrip)Get(form, "_spliceMenu")!).Items.OfType<ToolStripMenuItem>().Where(i => i.Text!.EndsWith(" s") && !i.Text.Contains('(')).ToList();
        H.Check(lengths.All(i => !i.Enabled), "lengths are disabled for a straight cut");
        ClickMenu(form, 0, "Fade out, fade in");

        // The menu as drawn (rendered without opening it on screen).
        {
            Call(form, "PopulateSpliceMenu", 1);
            var menu = (ContextMenuStrip)Get(form, "_spliceMenu")!;
            menu.CreateControl();
            menu.Size = menu.GetPreferredSize(Size.Empty);
            menu.PerformLayout();
            using var mb = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(mb, new Rectangle(Point.Empty, menu.Size));
            mb.Save(Path.Combine(H.Out, "s8_splice_menu.png"));
            Console.WriteLine("  saved s8_splice_menu.png");
        }

        // Hit testing: the dot, both edges, and the band for the right-click menu.
        Set(form, "_ui", 1f);
        var c0 = (PointF)Call(form, "ChipCenter", Splices(form)[0])!;
        var hit = Call(form, "HitTest", Point.Round(c0))!.ToString();
        H.Check(hit == "SpliceChip" && (int)Get(form, "_hitSplice")! == 0, $"the dot is clickable: {hit}");
        float y = StripMid(form) - 16;
        hit = Call(form, "HitTest", Point.Round(new PointF(X(form, 3), y)))!.ToString();
        string hitTo = Call(form, "HitTest", Point.Round(new PointF(X(form, 4), y)))!.ToString()!;
        H.Check(hit == "SpliceFrom" && hitTo == "SpliceTo", $"edges: {hit} / {hitTo}");
        H.Check((int)Call(form, "SpliceBandAt", Point.Round(new PointF(X(form, 7.2), StripMid(form))))! == 1, "right-click finds the band under the pointer");

        // Drag the first cut's end from 4 s to about 4.5 s; it lands on a frame boundary.
        Mouse(form, "OnMouseMove", new PointF(X(form, 4), y));
        Mouse(form, "OnMouseDown", new PointF(X(form, 4), y));
        Mouse(form, "OnMouseMove", new PointF(X(form, 4.25), y));
        Mouse(form, "OnMouseMove", new PointF(X(form, 4.5), y));
        Mouse(form, "OnMouseUp", new PointF(X(form, 4.5), y));
        Pump(0.8);
        l = Splices(form);
        double frames = l[0].To * 30;
        H.Check(Math.Abs(l[0].To - 4.5) < 0.05 && Math.Abs(frames - Math.Round(frames)) < 1e-3 && Math.Abs(l[0].From - 3) < 1e-3, $"edge drag snaps to a frame: {Show(l)}");
        Snap(form, "s3_two_cuts");

        // While an edge is being dragged, S and the right-click menu do nothing (the list can't change under the drag).
        {
            var ex = Splices(form)[0];
            Set(form, "_menuSplice", -1); // left over from the menu render above (it was never opened)
            Mouse(form, "OnMouseMove", new PointF(X(form, ex.From), y));
            Mouse(form, "OnMouseDown", new PointF(X(form, ex.From), y));
            Mouse(form, "OnMouseMove", new PointF(X(form, ex.From) - 10, y));
            int count = Splices(form).Count;
            Key(form, Keys.S);
            Key(form, Keys.S);
            Mouse(form, "OnMouseDown", new PointF(X(form, 7.2), StripMid(form)), MouseButtons.Right);
            H.Check(Splices(form).Count == count && Get(form, "_spliceMark") == null && (int)Get(form, "_menuSplice")! == -1,
                $"S and the menu are ignored mid-drag: {Splices(form).Count}/{count} splices, mark {Get(form, "_spliceMark") ?? "none"}, menu {Get(form, "_menuSplice")}");
            Mouse(form, "OnMouseMove", new PointF(X(form, ex.From), y));
            Mouse(form, "OnMouseUp", new PointF(X(form, ex.From), y));
            Pump(0.8);
            H.Check(Math.Abs(Splices(form)[0].From - ex.From) < 0.02, $"edge back where it was: {Show(Splices(form))}");
        }

        // Hover hint on the dot.
        var c1 = (PointF)Call(form, "ChipCenter", Splices(form)[1])!;
        Mouse(form, "OnMouseMove", c1);
        Set(form, "_hoverSince", -10.0);
        Pump(0.2);
        Snap(form, "s4_dot_hint");

        // Length shown and exported: [0,3) + [4.5,6) + [7.5,10) with a 1 s fade (no overlap) and a 1 s crossfade, which is
        // held to half the 1.5 s part before it and to whole frames: 22 frames.
        var job = (TrimJob)Call(form, "CurrentJob")!;
        H.Check(Math.Abs(job.OutputLength() - (3 + 1.5 + 2.5 - 22 / 30.0)) < 1e-3, $"output length {job.OutputLength():0.###}");

        // The fade previews while playing: half dark a quarter second before the first cut.
        float half = (float)Call(form, "FadeAt", 2.75)!, none = (float)Call(form, "FadeAt", 2.0)!, after = (float)Call(form, "FadeAt", l[0].To + 0.25)!;
        H.Check(Math.Abs(half - 0.5) < 0.02 && none == 0 && Math.Abs(after - 0.5) < 0.02, $"fade preview: {half:0.##} / {none} / {after:0.##}");

        // Playback from 2 s skips both cut-out parts: no frame from inside them is shown.
        GoTo(form, 2.0);
        Call(form, "Play");
        var seen = new List<double>();
        var end = DateTime.UtcNow.AddSeconds(6.5);
        while (DateTime.UtcNow < end && (bool)Get(form, "_playing")!)
        {
            Application.DoEvents();
            double p = Pos(form);
            if (seen.Count == 0 || seen[^1] != p) seen.Add(p);
            Thread.Sleep(3);
        }
        Call(form, "Pause");
        l = Splices(form);
        var inside = seen.Where(t => l.Any(s => t >= s.From + 1e-3 && t < s.To - 1e-3)).ToList();
        H.Check(seen.Count > 60 && inside.Count == 0 && seen.Any(t => t > 8), $"playback skips the cuts: {seen.Count} frames, {inside.Count} inside ({string.Join(", ", inside.Take(5).Select(t => t.ToString("0.###")))})");

        // Nothing left to play: the selection inside a cut.
        {
            var (i0, o0) = ((double)Get(form, "_in")!, (double)Get(form, "_out")!);
            Set(form, "_in", 6.2);
            Set(form, "_out", 7.2);
            Call(form, "Play");
            H.Check(!(bool)Get(form, "_playing")!, "won't play when everything in the selection is cut out");
            Set(form, "_in", i0);
            Set(form, "_out", o0);
        }

        // Restore one; restore all.
        Call(form, "RestoreSplice", 1);
        H.Check(Splices(form).Count == 1, "restore one part");

        // The export window: parts joined, no back-and-forth loop, no copy.
        Call(form, "AddSplice", 6.0, 7.0);
        job = ((TrimJob)Call(form, "CurrentJob")!).Simplified();
        var dlg = new ExportDialog(job) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900) };
        ShowWindow(dlg.Handle, 4);
        Pump(0.8);
        using (var b = new Bitmap(dlg.Width, dlg.Height))
        {
            dlg.DrawToBitmap(b, new Rectangle(0, 0, dlg.Width, dlg.Height));
            b.Save(Path.Combine(H.Out, "s5_export_spliced.png"));
        }
        var summary = (string)Call(dlg, "Summary")!;
        H.Check(summary.Contains("3 parts joined"), $"export summary: {summary}");
        dlg.Close();

        // Export exactly what the window would.
        string outFile = Path.Combine(H.Out, "s6_window_job.mp4");
        Exporter.RunAsync(job, new ExportSettings(ExportFormat.H264, ExportQuality.Small, true, outFile), null, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
        double dur = double.Parse(H.Probe(outFile, "-select_streams v -show_entries stream=duration -of csv=p=0"));
        H.Check(Math.Abs(dur - job.OutputLength()) < 0.04, $"exported length {dur} = shown {job.OutputLength():0.###}");

        Call(form, "ClearSplices");
        H.Check(Splices(form).Count == 0, "restore all");

        // Smallest window: the splice button doesn't run into the volume control.
        form.ClientSize = form.MinimumSize;
        Pump(0.3);
        var rs = (Rectangle)Call(form, "Rect", Enum.Parse(typeof(MainForm).GetNestedType("Hit", Any)!, "Splice"))!;
        var rv = (Rectangle)Call(form, "Rect", Enum.Parse(typeof(MainForm).GetNestedType("Hit", Any)!, "Vol"))!;
        var rc = (Rectangle)Call(form, "Rect", Enum.Parse(typeof(MainForm).GetNestedType("Hit", Any)!, "Crop"))!;
        H.Check(rc.Right + 4 <= rv.Left && rs.Right < rc.Left, $"smallest window: splice {rs}, crop {rc}, volume {rv}");
        Call(form, "AddSplice", 2.0, 2.5);
        Snap(form, "s7_smallest_window");
        form.Close();
    }
}
