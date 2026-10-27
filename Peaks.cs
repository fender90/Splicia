namespace Splicia;

/// <summary>
/// The outline of a file's sound for drawing waveforms: the loudest sample in every 10 ms, decoded on a background
/// thread with its own sound-only decoder, filling in from the start.
/// </summary>
sealed class Peaks : IDisposable
{
    public const int PerSecond = 100;
    const int Rate = MediaSource.AudioRate, Bucket = Rate / PerSecond;

    readonly byte[] _peaks;   // 0..255, loudest |sample| per 10 ms
    readonly Control _owner;
    readonly Thread _thread;
    volatile bool _cancel;
    volatile int _filled;     // buckets decoded so far

    /// <summary>Raised on the UI thread as the outline fills in (a few times a second).</summary>
    public event Action? Updated;

    public Peaks(string path, double duration, Control owner)
    {
        _owner = owner;
        _peaks = new byte[Math.Max(1, (int)Math.Ceiling(duration * PerSecond)) + 1];
        _thread = new Thread(() => Generate(path)) { IsBackground = true, Name = "Waveform", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>The loudest point (0..1) between <paramref name="from"/> and <paramref name="to"/> seconds, of what has been read.</summary>
    public float Max(double from, double to)
    {
        int a = Math.Max(0, (int)(from * PerSecond)), b = Math.Min(_filled, Math.Max(a + 1, (int)Math.Ceiling(to * PerSecond)));
        int m = 0;
        for (int i = a; i < b; i++) if (_peaks[i] > m) m = _peaks[i];
        return m / 255f;
    }

    void Generate(string path)
    {
        try
        {
            using var src = new MediaSource(path, video: false);
            src.AudioEnabled = true;
            src.Seek(0);
            long sample = 0;
            int peak = 0, last = Environment.TickCount;
            while (!_cancel)
            {
                var got = src.Next();
                if (got == MediaSource.Got.End) break;
                if (got != MediaSource.Got.Audio) continue;
                double at = src.AudioTime;
                if (!double.IsNaN(at) && at * Rate > sample + Bucket) sample = (long)(at * Rate); // a hole: skip ahead
                var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(src.ConvertAudio());
                for (int i = 0; i + 1 < pcm.Length; i += 2)
                {
                    int v = Math.Max(Math.Abs((int)pcm[i]), Math.Abs((int)pcm[i + 1]));
                    if (v > peak) peak = v;
                    if (++sample % Bucket == 0)
                    {
                        int k = (int)(sample / Bucket) - 1;
                        if (k >= 0 && k < _peaks.Length) _peaks[k] = (byte)Math.Min(255, peak * 255 / 32767);
                        _filled = Math.Clamp(k + 1, _filled, _peaks.Length);
                        peak = 0;
                    }
                }
                if (Environment.TickCount - last > 250) { last = Environment.TickCount; Notify(); }
            }
            _filled = _peaks.Length;
            Notify();
        }
        catch (Exception ex) { CrashLog.Write(ex, $"Reading the sound of {path} for its waveform"); }
    }

    void Notify()
    {
        if (_cancel) return;
        try { _owner.BeginInvoke(() => { if (!_cancel) Updated?.Invoke(); }); } catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        _cancel = true;
        _thread.Join(1000);
    }
}
