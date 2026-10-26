using System.Drawing;

namespace VideoTrimmer;

/// <summary>Cropped exports: output size and picture, plain / rotated / back-and-forth / 4K HDR.</summary>
static class CropTests
{
    public static void Run()
    {
        static void Check(bool ok, string what) => H.Check(ok, what);
        var hw = Exporter.HardwareHevc.Result;
        var cases = new (string File, int Rot, RectangleF Crop, ExportFormat Fmt, bool Boom, int W, int H)[]
        {
            ("t7_counter.mp4", 0, new RectangleF(0.21875f, 0, 0.5625f, 1), ExportFormat.H264, false, 720, 720),
            ("t7_counter.mp4", 90, new RectangleF(0.1f, 0.25f, 0.8f, 0.5f), ExportFormat.H264, false, 576, 640),
            ("t7_counter.mp4", 0, new RectangleF(0.25f, 0.25f, 0.5f, 0.5f), ExportFormat.H264, true, 640, 360),
            ("t2_iphone_like.mov", 0, new RectangleF(0f, 0.2f, 1f, 0.3164f), ExportFormat.Hevc, false, 2160, 1214),
        };
        int n = 0;
        foreach (var c in cases)
        {
            n++;
            using var ms = new MediaSource(Path.Combine(H.Vids, c.File), decoders: false);
            var job = new TrimJob(ms.Info, 2.0, 3.0, c.Rot) { Crop = c.Crop };
            string outFile = Path.Combine(H.Out, $"crop{n}_{Path.GetFileNameWithoutExtension(c.File)}.mp4");
            var s = new ExportSettings(c.Fmt, ExportQuality.Balanced, true, outFile, Boomerang: c.Boom);
            Check(Exporter.OriginalUnavailableReason(job) != null, "Original (copy) is unavailable with a crop");
            try { Exporter.RunAsync(job, s, c.Fmt == ExportFormat.Hevc ? hw : null, _ => { }, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { Check(false, $"{Path.GetFileName(outFile)}: {ex.Message}"); continue; }
            string info = H.Probe(outFile, "-select_streams v -show_entries stream=width,height,color_transfer -of compact=p=0");
            var (ow, oh) = job.OutputSize();
            Check(info.Contains($"width={c.W}|height={c.H}") && ow == c.W && oh == c.H, $"{Path.GetFileName(outFile)}: {info} (expected {c.W}x{c.H}, OutputSize {ow}x{oh})");
            H.Ffmpeg($"-v error -y -i \"{outFile}\" -frames:v 1 -vf scale=-2:180 \"{Path.Combine(H.Out, $"crop{n}.png")}\"");
        }
    }
}
