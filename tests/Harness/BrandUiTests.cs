using System.Reflection;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>Renders the setup wizard pages, the About window and the empty main window offscreen (never activated).</summary>
static class BrandUiTests
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    static void Pump(double s)
    {
        var end = DateTime.UtcNow.AddSeconds(s);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
    }

    static void Snap(Form f, string name)
    {
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-31900, -31900);
        ShowWindow(f.Handle, 4); // SW_SHOWNOACTIVATE
        Pump(0.5);
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(Path.Combine(H.Out, name + ".png"));
        Console.WriteLine($"  saved {name}.png");
    }

    public static void Run()
    {
        var setup = new SetupForm(uninstall: false);
        Snap(setup, "b1_setup_scope");
        setup.GetType().GetMethod("ShowDestination", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(setup, null);
        Pump(0.3);
        Snap(setup, "b2_setup_destination");
        setup.Close();

        var about = new AboutDialog();
        Snap(about, "b3_about");
        about.Close();
        var keys = new ShortcutsDialog();
        Snap(keys, "b6_shortcuts");
        keys.Close();

        using (var ms = new MediaSource(Path.Combine(H.Vids, "t7_counter.mp4"), decoders: false))
        {
            var dlg = new ExportDialog(new TrimJob(ms.Info, 2, 6, 0));
            Snap(dlg, "b5_export");
            dlg.Close();
        }

        var main = new MainForm(null) { ClientSize = new Size(1100, 700) };
        foreach (var (clip, shot) in new[] { ("t7_counter.mp4", "b7_main_dark"), ("t1_h264_1080p30.mp4", "b8_main_bright") })
        {
            var f = new MainForm(null) { ClientSize = new Size(1100, 700), StartPosition = FormStartPosition.Manual, Location = new Point(-31900, -31900) };
            ShowWindow(f.Handle, 4);
            f.GetType().GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(f, new object[] { Path.Combine(H.Vids, clip) });
            Pump(2.5);
            var timer = (System.Windows.Forms.Timer)f.GetType().GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!;
            timer.Stop();
            f.GetType().GetField("_ui", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(f, 1f);
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(Path.Combine(H.Out, shot + ".png"));
            }
            Console.WriteLine($"  saved {shot}.png");
            f.Close();
        }
        Snap(main, "b4_main_empty");
        H.Check(main.Text == "Splicia", $"main window title: {main.Text}");
        main.Close();
    }
}
