using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// 48 kHz stereo 16-bit sound through waveOut. <see cref="Write"/> never blocks: PCM goes into a queue that a feeder
/// thread hands to the device in 20 ms buffers. <see cref="PlayedFrames"/> is the device's own position, i.e. what has
/// actually been heard, which the playback clock follows.
/// </summary>
sealed class AudioOut : IDisposable
{
    const int Rate = MediaSource.AudioRate, BlockAlign = 4, BufCount = 8, BufBytes = Rate / 50 * BlockAlign;

    IntPtr _wave;
    readonly IntPtr[] _hdr = new IntPtr[BufCount], _buf = new IntPtr[BufCount];
    readonly bool[] _queued = new bool[BufCount];
    readonly AutoResetEvent _event = new(false);
    readonly Thread _feeder;
    readonly object _gate = new();
    byte[] _ring = new byte[Rate * BlockAlign];
    int _ringStart, _ringCount;
    long _written; // sample frames handed to the device since the last reset
    volatile bool _quit;
    int _next; // next buffer to fill (buffers are queued round-robin so they play in order)

    /// <summary>0..1, applied to the samples (so it never touches the system mixer).</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>Opens the default output device. Throws if there is none.</summary>
    public AudioOut()
    {
        var fmt = new WAVEFORMATEX
        {
            wFormatTag = 1, nChannels = 2, nSamplesPerSec = Rate, nAvgBytesPerSec = Rate * BlockAlign,
            nBlockAlign = BlockAlign, wBitsPerSample = 16,
        };
        int mm = waveOutOpen(out _wave, WAVE_MAPPER, ref fmt, _event.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CALLBACK_EVENT);
        if (mm != 0) throw new InvalidOperationException($"No audio output (waveOutOpen error {mm}).");
        waveOutPause(_wave); // nothing plays until Play()
        for (int i = 0; i < BufCount; i++)
        {
            _buf[i] = Marshal.AllocHGlobal(BufBytes);
            _hdr[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
            Marshal.StructureToPtr(new WAVEHDR { lpData = _buf[i], dwBufferLength = BufBytes }, _hdr[i], false);
            waveOutPrepareHeader(_wave, _hdr[i], Marshal.SizeOf<WAVEHDR>());
        }
        _feeder = new Thread(Feed) { IsBackground = true, Name = "Audio feeder", Priority = ThreadPriority.AboveNormal };
        _feeder.Start();
    }

    /// <summary>Queues PCM (interleaved stereo s16, 48 kHz).</summary>
    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (pcm.IsEmpty) return;
        lock (_gate)
        {
            if (_ringCount + pcm.Length > _ring.Length)
            {
                var bigger = new byte[Math.Max(_ring.Length * 2, _ringCount + pcm.Length)];
                CopyOut(bigger, _ringCount);
                _ring = bigger;
                _ringStart = 0;
            }
            int end = (_ringStart + _ringCount) % _ring.Length;
            int first = Math.Min(pcm.Length, _ring.Length - end);
            pcm[..first].CopyTo(_ring.AsSpan(end));
            pcm[first..].CopyTo(_ring);
            _ringCount += pcm.Length;
        }
        _event.Set();
    }

    /// <summary>Queues silence.</summary>
    public void WriteSilence(int frames)
    {
        Span<byte> zero = stackalloc byte[4096];
        zero.Clear();
        while (frames > 0)
        {
            int n = Math.Min(frames, zero.Length / BlockAlign);
            Write(zero[..(n * BlockAlign)]);
            frames -= n;
        }
    }

    void CopyOut(Span<byte> dst, int count)
    {
        int first = Math.Min(count, _ring.Length - _ringStart);
        _ring.AsSpan(_ringStart, first).CopyTo(dst);
        _ring.AsSpan(0, count - first).CopyTo(dst[first..]);
    }

    /// <summary>Sample frames the device has played since the last <see cref="Reset"/>.</summary>
    public long PlayedFrames
    {
        get
        {
            lock (_gate)
            {
                if (_wave == IntPtr.Zero) return 0;
                var t = new MMTIME { wType = TIME_SAMPLES };
                if (waveOutGetPosition(_wave, ref t, Marshal.SizeOf<MMTIME>()) != 0) return 0;
                long played = t.wType == TIME_BYTES ? t.value / BlockAlign : t.value;
                return Math.Min(played, _written);
            }
        }
    }

    /// <summary>Sample frames queued (written but not yet heard), including what the feeder hasn't sent yet.</summary>
    public long PendingFrames
    {
        get
        {
            long played = PlayedFrames;
            lock (_gate) return _written - played + _ringCount / BlockAlign;
        }
    }

    public void Play() { lock (_gate) if (_wave != IntPtr.Zero) waveOutRestart(_wave); }
    public void Pause() { lock (_gate) if (_wave != IntPtr.Zero) waveOutPause(_wave); }

    /// <summary>Drops everything queued, rewinds the position to zero and leaves the device paused.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_wave == IntPtr.Zero) return;
            waveOutReset(_wave);
            waveOutPause(_wave);
            for (int i = 0; i < BufCount; i++) _queued[i] = false;
            _ringStart = _ringCount = 0;
            _written = 0;
            _next = 0;
        }
    }

    unsafe void Feed()
    {
        while (!_quit)
        {
            _event.WaitOne(10);
            lock (_gate)
            {
                if (_wave == IntPtr.Zero) return;
                for (int k = 0; k < BufCount && _ringCount >= BlockAlign; k++)
                {
                    int i = _next;
                    var hdr = (WAVEHDR*)_hdr[i];
                    if (_queued[i] && (hdr->dwFlags & WHDR_DONE) == 0) break; // device still owns it
                    int n = Math.Min(BufBytes, _ringCount) / BlockAlign * BlockAlign;
                    var dst = new Span<byte>((void*)_buf[i], n);
                    CopyOut(dst, n);
                    _ringStart = (_ringStart + n) % _ring.Length;
                    _ringCount -= n;
                    float vol = Volume;
                    if (vol < 0.999f)
                    {
                        var s = MemoryMarshal.Cast<byte, short>(dst);
                        int scale = (int)(Math.Clamp(vol, 0f, 1f) * 65536);
                        for (int j = 0; j < s.Length; j++) s[j] = (short)((s[j] * scale) >> 16);
                    }
                    hdr->dwBufferLength = (uint)n;
                    hdr->dwFlags &= ~WHDR_DONE;
                    waveOutWrite(_wave, _hdr[i], Marshal.SizeOf<WAVEHDR>());
                    _queued[i] = true;
                    _written += n / BlockAlign;
                    _next = (i + 1) % BufCount;
                }
            }
        }
    }

    public void Dispose()
    {
        _quit = true;
        _event.Set();
        _feeder.Join(500);
        lock (_gate)
        {
            if (_wave == IntPtr.Zero) return;
            waveOutReset(_wave);
            for (int i = 0; i < BufCount; i++)
            {
                waveOutUnprepareHeader(_wave, _hdr[i], Marshal.SizeOf<WAVEHDR>());
                Marshal.FreeHGlobal(_hdr[i]);
                Marshal.FreeHGlobal(_buf[i]);
            }
            waveOutClose(_wave);
            _wave = IntPtr.Zero;
        }
        _event.Dispose();
    }

    // ───────────────────────── interop ─────────────────────────

    const uint WAVE_MAPPER = 0xFFFFFFFF, CALLBACK_EVENT = 0x00050000, WHDR_DONE = 1, TIME_BYTES = 4, TIME_SAMPLES = 2;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public int nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext, reserved;
    }

    [StructLayout(LayoutKind.Explicit, Size = 12)]
    struct MMTIME
    {
        [FieldOffset(0)] public uint wType;
        [FieldOffset(4)] public uint value;
    }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr pwh, int cbwh);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr hwo, ref MMTIME pmmt, int cbmmt);
}
