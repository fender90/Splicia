using System.Globalization;
using static Splicia.TimelineUiTests;

namespace Splicia;

/// <summary>
/// The screenshot button, in the real window (offscreen, never activated, muted), saving into the test's own folder:
/// the exact frame on screen at full size in the player; turned and cropped in the editor; nothing in a gap; a 4K HDR
/// phone video's frame upright, 16 bits a channel and brought to SDR. (Hidden faces: see FaceTests.Window.)
/// </summary>
static class ShotTests
{
    static string S(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>A fresh folder the Save dialog's stand-in answers with (the name suggested, kept); the names asked about in <see cref="Asked"/>.</summary>
    internal static string Folder(MainForm form, string name)
    {
        string dir = Path.Combine(H.Out, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Set(form, "_askShotPath", (Func<string, string?>)(suggested => { Asked.Add(suggested); return Path.Combine(dir, suggested); }));
        return dir;
    }

    internal static readonly List<string> Asked = new();

    /// <summary>Presses the button and waits for the picture: the newest PNG in <paramref name="dir"/>, or null.</summary>
    internal static string? Shoot(MainForm form, string dir)
    {
        Set(form, "_shot", null);
        Click(form, Centre(Button(form, "Shot")));
        ((Task?)Get(form, "_shot"))?.Wait(TimeSpan.FromSeconds(60));
        Pump(0.2);
        return Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName : null;
    }

    /// <summary>A PNG's size, bits a channel and colour type (from its header).</summary>
    internal static (int W, int H, int Bits, int Type) Png(string path)
    {
        var b = new byte[26];
        using (var fs = File.OpenRead(path)) fs.ReadExactly(b);
        int Be(int o) => b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3];
        return (Be(16), Be(20), b[24], b[25]);
    }

    /// <summary>A picture (any file ffmpeg reads; a video's frame starting at <paramref name="t"/>) as 8-bit RGB.</summary>
    internal static byte[] Rgb(string file, double? t = null, double fd = 0)
    {
        string raw = Path.Combine(H.Out, "shot_cmp.rgb");
        string seek = t is double s ? $"-ss {S(Math.Max(0, s - fd / 2))} " : "";
        H.Ffmpeg($"-v error -y {seek}-i \"{file}\" -frames:v 1 -vf scale=in_color_matrix=bt709,format=rgb24 -f rawvideo \"{raw}\"");
        if (!File.Exists(raw)) return Array.Empty<byte>();
        var bytes = File.ReadAllBytes(raw);
        File.Delete(raw);
        return bytes;
    }

    static double Diff(byte[] a, byte[] b) => a.Length != b.Length || a.Length == 0 ? 255 : a.Zip(b, (x, y) => (double)Math.Abs(x - y)).Average();

    public static void Run()
    {
        // The player: the button is there; the picture is the frame on screen, the file's full size
        string counter = Path.Combine(H.Vids, "t7_counter.mp4");
        var form = OpenOffscreen("t7_counter.mp4");
        string dir = Folder(form, "shots");
        H.Check(!Button(form, "Shot").IsEmpty && !(bool)Get(form, "_editing")!, "the screenshot button is in the player");
        Call(form, "GoTo", 2.0);
        Pump(0.4);
        double fd = (double)Get(form, "FrameDur")!, shown = ((VideoFrame)Get(form, "_shown")!).Time;
        string? png = Shoot(form, dir);
        if (png == null) { H.Check(false, "a screenshot is saved"); form.Close(); return; }
        var (w, h, bits, type) = Png(png);
        var pic = Rgb(png);
        double exact = Diff(pic, Rgb(counter, shown, fd)), next = Diff(pic, Rgb(counter, shown + fd, fd)), prev = Diff(pic, Rgb(counter, shown - fd, fd));
        H.Check(w == 1280 && h == 720 && bits == 8 && type == 2 && exact < 1.5 && next > exact + 2 && prev > exact + 2
            && Asked.LastOrDefault() == "t7_counter 00-00-02.000.png" && ((string?)Get(form, "_toast"))?.StartsWith("Saved ") == true,
            $"player: asked where, suggesting \"{Asked.LastOrDefault()}\"; {w}×{h} {bits}-bit RGB, the frame on screen (differs by {exact:0.00} from it, {prev:0.0} and {next:0.0} from its neighbours); \"{Get(form, "_toast")}\"");

        // Cancelling the Save dialog saves nothing (and says nothing)
        var answer = Get(form, "_askShotPath");
        Set(form, "_askShotPath", (Func<string, string?>)(_ => null));
        int had = Directory.GetFiles(dir).Length;
        string toastBefore = (string?)Get(form, "_toast") ?? "";
        Shoot(form, dir);
        H.Check(Directory.GetFiles(dir).Length == had && (string?)Get(form, "_toast") == toastBefore, "cancelling the Save dialog saves nothing");
        Set(form, "_askShotPath", answer);

        // The real dialog, set up (not shown): the name suggested, an overwrite prompt, files only
        var dialog = SaveDialog.Create("Save the picture", "a name.png", Guid.NewGuid(), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PNG picture", ".png");
        dialog.GetFileName(out string suggestedName);
        dialog.GetOptions(out uint options);
        System.Runtime.InteropServices.Marshal.ReleaseComObject(dialog);
        H.Check(suggestedName == "a name.png" && (options & 0x2) != 0 && (options & 0x4) != 0 && (options & 0x40) != 0,
            $"the Save dialog is set up: \"{suggestedName}\", options 0x{options:X} (overwrite prompt, its own extension, files only)");

        // A save that fails (no frame there: past the end) leaves the file it was to replace as it was, and no work file
        string keep = Path.Combine(dir, "keep.png");
        File.WriteAllBytes(keep, new byte[] { 1, 2, 3 });
        string? failed = null;
        try { Exporter.SaveFrameAsync((MediaInfo)Get(form, "_info")!, 1000, 0, null, null, keep).Wait(); }
        catch (AggregateException ex) { failed = ex.InnerException?.Message; }
        H.Check(failed != null && File.ReadAllBytes(keep).SequenceEqual(new byte[] { 1, 2, 3 }) && Directory.GetFiles(dir, ".splicia-*").Length == 0,
            $"a failed save keeps the file it was replacing: \"{failed}\"");

        // The editor: turned right and cropped (half the width, 60% of the height of the picture as shown)
        Call(form, "SetEditing", true);
        H.Check(!Button(form, "Shot").IsEmpty, "the screenshot button is in the editor too");
        Call(form, "Rotate", 90);
        Set(form, "_crop", new RectangleF(0.25f, 0.1f, 0.5f, 0.6f));
        Pump(0.3);
        png = Shoot(form, dir);
        var turned = png != null ? Png(png) : default;
        H.Check(turned.W == 360 && turned.H == 768, $"editor, turned and cropped: {turned.W}×{turned.H} (720×1280 turned, cropped to 360×768)");
        Call(form, "Rotate", -90);
        Set(form, "_crop", new RectangleF(0, 0, 1, 1));

        // In a gap: nothing saved, and it says why
        Call(form, "GoTo", 3.0); Pump(0.2); Key(form, Keys.S);
        Call(form, "GoTo", 6.0); Pump(0.2); Key(form, Keys.S);
        Set(form, "_selClip", Tl(form).Clips[1].Id);
        Key(form, Keys.Delete);
        Call(form, "GoTo", 4.5);
        Pump(0.4);
        int before = Directory.GetFiles(dir, "*.png").Length;
        Shoot(form, dir);
        string toast = (string?)Get(form, "_toast") ?? "";
        H.Check(Directory.GetFiles(dir, "*.png").Length == before && toast.StartsWith("No picture here"), $"in a gap: nothing saved (\"{toast}\")");
        form.Close();

        // A 4K HDR (HLG) phone video stored sideways: upright, 2160×3840, 16 bits a channel, brought to SDR (black is black)
        form = OpenOffscreen("t2_iphone_like.mov");
        dir = Folder(form, "shots_hdr");
        Call(form, "GoTo", 1.0);
        Pump(1.0);
        png = Shoot(form, dir);
        var hdr = png != null ? Png(png) : default;
        var px = png != null ? Rgb(png) : Array.Empty<byte>();
        int darkest = px.Length > 0 ? px.Min() : 255, brightest = px.Length > 0 ? px.Max() : 0;
        H.Check(hdr.W == 2160 && hdr.H == 3840 && hdr.Bits == 16 && hdr.Type == 2 && darkest < 12 && brightest > 240,
            $"HDR phone video: {hdr.W}×{hdr.H} {hdr.Bits}-bit RGB, from {darkest} to {brightest} (of 255: not washed out)");
        form.Close();
    }
}
