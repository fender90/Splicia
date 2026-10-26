using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Filmstrip pictures for the trim timeline: <see cref="Count"/> evenly spaced keyframes, decoded on a background thread
/// with their own decoder, coarse-to-fine so the strip fills in quickly. Pictures are not rotated.
/// </summary>
sealed class ThumbnailStrip : IDisposable
{
    public const int Count = 48;

    readonly Bitmap?[] _thumbs = new Bitmap?[Count];
    readonly Control _owner;
    readonly Thread _thread;
    volatile bool _cancel;

    /// <summary>Raised on the UI thread whenever a picture arrives.</summary>
    public event Action? Updated;

    public ThumbnailStrip(MediaInfo info, Control owner, int height)
    {
        _owner = owner;
        _thread = new Thread(() => Generate(info, height)) { IsBackground = true, Name = "Thumbnails", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The picture nearest to slot <paramref name="index"/> that has arrived so far.</summary>
    public Bitmap? Nearest(int index)
    {
        index = Math.Clamp(index, 0, Count - 1);
        for (int d = 0; d < Count; d++)
        {
            if (index - d >= 0 && _thumbs[index - d] is Bitmap a) return a;
            if (index + d < Count && _thumbs[index + d] is Bitmap b) return b;
        }
        return null;
    }

    /// <summary>Slots in coarse-to-fine order: 0, 24, 12, 36, 6, 18, ...</summary>
    static IEnumerable<int> Order()
    {
        var seen = new bool[Count];
        for (int step = Count; step >= 1; step /= 2)
            for (int i = 0; i < Count; i += step)
                if (!seen[i]) { seen[i] = true; yield return i; }
        for (int i = 0; i < Count; i++) if (!seen[i]) yield return i;
    }

    void Generate(MediaInfo info, int height)
    {
        try
        {
            using var src = new MediaSource(info.Path, keyframesOnly: true, threads: 2);
            double aspect = info.Width * info.Sar / Math.Max(1, info.Height);
            int h = Math.Max(8, height), w = Math.Max(8, (int)Math.Round(h * aspect));
            if (w > 4 * h) { w = 4 * h; } // extreme panoramas: keep tiles reasonable
            var frame = new VideoFrame();
            foreach (int i in Order())
            {
                if (_cancel) return;
                double t = (i + 0.5) * info.Duration / Count;
                if (!src.DecodeKeyframeNear(t, frame, w, h)) continue;
                var bmp = ToBitmap(frame);
                if (_cancel) { bmp.Dispose(); return; }
                try
                {
                    _owner.BeginInvoke(() =>
                    {
                        if (_cancel) { bmp.Dispose(); return; }
                        _thumbs[i]?.Dispose();
                        _thumbs[i] = bmp;
                        Updated?.Invoke();
                    });
                }
                catch (InvalidOperationException) { bmp.Dispose(); return; } // window closed
            }
        }
        catch (Exception ex)
        {
            if (!_cancel) CrashLog.Write(ex, "Generating thumbnails");
        }
    }

    static Bitmap ToBitmap(VideoFrame f)
    {
        var bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, f.Width, f.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (int y = 0; y < f.Height; y++)
                Marshal.Copy(f.Data, y * f.Stride, bd.Scan0 + y * bd.Stride, f.Stride);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    public void Dispose()
    {
        _cancel = true;
        _thread.Join(1000);
        for (int i = 0; i < Count; i++)
        {
            _thumbs[i]?.Dispose();
            _thumbs[i] = null;
        }
    }
}
