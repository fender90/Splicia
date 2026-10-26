using System.Reflection;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Crop mode in the real window, rendered offscreen and never activated (focus is never taken): aspect presets, handle,
/// move and draw drags, rotation, Enter / Esc, and the cropped preview. Screenshots go to out\.
/// </summary>
static class CropUiTests
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
    static object? Get(object o, string n) => o.GetType().GetField(n, Any)?.GetValue(o) ?? o.GetType().GetProperty(n, Any)!.GetValue(o);
    static void Set(object o, string n, object? v) => o.GetType().GetField(n, Any)!.SetValue(o, v);
    static object? Call(object o, string n, params object?[] a) => o.GetType().GetMethod(n, Any)!.Invoke(o, a);

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

    static void Mouse(Form f, string m, PointF p, int clicks = 1) =>
        f.GetType().GetMethod(m, Any)!.Invoke(f, new object[] { new MouseEventArgs(MouseButtons.Left, clicks, (int)Math.Round(p.X), (int)Math.Round(p.Y), 0) });

    static void Drag(Form f, PointF from, PointF to)
    {
        Set(f, "_ui", 1f); // the real pointer is outside this offscreen window
        Mouse(f, "OnMouseDown", from);
        Mouse(f, "OnMouseMove", new PointF((from.X + to.X) / 2, (from.Y + to.Y) / 2));
        Mouse(f, "OnMouseMove", to);
        Mouse(f, "OnMouseUp", to);
    }

    static bool Key(Form f, Keys k) => (bool)f.GetType().GetMethod("ProcessCmdKey", Any)!.Invoke(f, new object[] { new Message(), k })!;

    static RectangleF Crop(Form f) => (RectangleF)Get(f, "_crop")!;
    static RectangleF CropWin(Form f) => (RectangleF)Get(f, "CropWindowRect")!;
    static int AspectIndex(string label) => Array.FindIndex(((string Label, double Ratio)[])typeof(MainForm).GetField("Aspects", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!, a => a.Label == label);

    /// <summary>Crop's width / height in display pixels (picture as shown).</summary>
    static double Ratio(Form f)
    {
        var (w, h) = ((float, float))Get(f, "Disp")!;
        var c = Crop(f);
        return c.Width * w / (c.Height * h);
    }

    public static void Run()
    {
        var form = new MainForm(null) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900), ClientSize = new Size(1280, 800) };
        ShowWindow(form.Handle, 4); // SW_SHOWNOACTIVATE
        Call(form, "Open", Path.Combine(H.Vids, "t7_counter.mp4"));
        Pump(2.0);
        Call(form, "GoTo", 4.0);
        Pump(0.8);

        // Enter crop mode
        Key(form, Keys.C);
        Pump(0.4);
        H.Check((bool)Get(form, "_cropping")!, "C enters crop mode");
        Snap(form, "c1_crop_mode");

        // 1:1 via the aspect bar
        var items = (System.Collections.IList)Call(form, "CropBarItems")!;
        object? chip = null;
        foreach (var it in items) if ((string)it.GetType().GetField("Item3")!.GetValue(it)! == "1:1") chip = it;
        var chipRect = (RectangleF)chip!.GetType().GetField("Item1")!.GetValue(chip)!;
        var chipCenter = new PointF(chipRect.X + chipRect.Width / 2, chipRect.Y + chipRect.Height / 2);
        Set(form, "_ui", 1f);
        Mouse(form, "OnMouseMove", chipCenter);
        Mouse(form, "OnMouseDown", chipCenter);
        Mouse(form, "OnMouseUp", chipCenter);
        var c = Crop(form);
        H.Check((int)Get(form, "_aspect")! == AspectIndex("1:1") && Math.Abs(Ratio(form) - 1) < 0.01 && Math.Abs(c.Height - 1) < 1e-3 && Math.Abs(c.X + c.Width / 2 - 0.5) < 1e-3,
            $"1:1 chip → largest centred square {c}");

        // Drag the bottom-right corner in: stays square, top-left fixed
        var w = CropWin(form);
        Drag(form, new PointF(w.Right, w.Bottom), new PointF(w.Right - 120, w.Bottom - 40));
        var c2 = Crop(form);
        H.Check(Math.Abs(Ratio(form) - 1) < 0.01 && Math.Abs(c2.X - c.X) < 1e-3 && Math.Abs(c2.Y - c.Y) < 1e-3 && c2.Width < c.Width,
            $"corner drag keeps 1:1 and the opposite corner: {c2}");

        // Move it right; it stays inside the picture
        w = CropWin(form);
        Drag(form, new PointF(w.X + w.Width / 2, w.Y + w.Height / 2), new PointF(w.X + w.Width / 2 + 2000, w.Y + w.Height / 2));
        var c3 = Crop(form);
        H.Check(Math.Abs(c3.Right - 1) < 1e-3 && Math.Abs(c3.Width - c2.Width) < 1e-4, $"move clamps at the right edge: {c3}");
        Snap(form, "c2_square_moved");

        // Free: drag the left edge; height unchanged
        Call(form, "ApplyAspect", 0);
        w = CropWin(form);
        Drag(form, new PointF(w.Left, w.Y + w.Height / 2), new PointF(w.Left + 60, w.Y + w.Height / 2));
        var c4 = Crop(form);
        H.Check(Math.Abs(c4.Height - c3.Height) < 1e-4 && c4.Width < c3.Width - 0.01 && Math.Abs(c4.Right - c3.Right) < 1e-4, $"free edge drag: {c4}");

        // Draw a new rectangle from outside the current one
        var vr = (RectangleF)Get(form, "VideoRectF")!;
        Drag(form, new PointF(vr.X + vr.Width * 0.05f, vr.Y + vr.Height * 0.1f), new PointF(vr.X + vr.Width * 0.35f, vr.Y + vr.Height * 0.6f));
        var c5 = Crop(form);
        H.Check(Math.Abs(c5.X - 0.05) < 0.01 && Math.Abs(c5.Y - 0.1) < 0.01 && Math.Abs(c5.Width - 0.3) < 0.01 && Math.Abs(c5.Height - 0.5) < 0.01,
            $"drawing a new rectangle: {c5}");

        // 4:3, then rotate right: the crop turns with the picture and the ratio flips to 3:4
        Call(form, "ApplyAspect", AspectIndex("4:3"));
        var before = Crop(form);
        Key(form, Keys.R);
        Pump(0.4);
        var after = Crop(form);
        H.Check((int)Get(form, "_aspect")! == AspectIndex("3:4") && Math.Abs(Ratio(form) - 3 / 4.0) < 0.01 && before.Width < 0.99 &&
                Math.Abs(after.X - (1 - before.Bottom)) < 1e-4 && Math.Abs(after.Y - before.X) < 1e-4,
            $"rotate right: {before} → {after}, ratio {Ratio(form):0.###}");
        Snap(form, "c3_rotated_34");
        Key(form, Keys.Shift | Keys.R);
        Pump(0.4);
        var back = Crop(form);
        H.Check(Math.Abs(back.X - before.X) < 1e-4 && Math.Abs(back.Width - before.Width) < 1e-4, "rotate left restores the crop");

        // Esc undoes this session's changes; Enter keeps them
        Key(form, Keys.Escape);
        H.Check(!(bool)Get(form, "_cropping")! && !(bool)Get(form, "HasCrop")!, "Esc leaves crop mode without a crop");
        Key(form, Keys.C);
        Call(form, "ApplyAspect", AspectIndex("4:5"));
        Key(form, Keys.Enter);
        Pump(0.6);
        var (ow, oh) = ((int, int))Call(form, "OutputSize")!;
        H.Check(!(bool)Get(form, "_cropping")! && (bool)Get(form, "HasCrop")! && Math.Abs(ow / (double)oh - 0.8) < 0.01,
            $"Enter keeps a 4:5 crop → output {ow}×{oh}");
        var src = (Rectangle)Get(form, "SourceRect")!;
        var bmp = (Bitmap)Get(form, "_bmp")!;
        H.Check(src.Width < bmp.Width && Math.Abs(src.Width / (double)src.Height - 0.8) < 0.02, $"preview shows only the crop: {src} of {bmp.Width}×{bmp.Height}");
        Snap(form, "c4_cropped_preview");

        // The export window gets the crop
        var info = (MediaInfo)Get(form, "_info")!;
        var job = new TrimJob(info, 0, 1, (int)Get(form, "_rot")!) { Crop = Crop(form) };
        var dlg = new ExportDialog(job) { StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900) };
        ShowWindow(dlg.Handle, 4);
        Pump(1.0);
        using (var b = new Bitmap(dlg.Width, dlg.Height))
        {
            dlg.DrawToBitmap(b, new Rectangle(0, 0, dlg.Width, dlg.Height));
            b.Save(Path.Combine(H.Out, "c5_export_cropped.png"));
        }
        H.Check(Exporter.OriginalUnavailableReason(job)!.Contains("Cropping"), "export: Original is unavailable with a crop");
        dlg.Close();
        form.Close();
    }
}
