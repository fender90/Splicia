namespace Splicia;

/// <summary>
/// Plays the timeline's sound: renders it on its own thread, clip after clip, into the sound card's queue as one
/// continuous stream, the way editors mix a timeline rather than play files: the video clips' sound (both sides of a
/// crossfade mixed, fades, silence in gaps) plus the audio track, and the short declick fades wherever sound starts or
/// stops abruptly (<see cref="EditList.Declick"/>).
/// Nothing stops, seeks or jumps at a cut. Each part's sound comes from one of two sound-only decoders, so the next part
/// is ready while the current one plays out. Gains follow the export's ffmpeg filters (afade / acrossfade), so what is
/// heard is what is exported. At the end it keeps the queue topped up with silence (so the clock runs on until the
/// player stops) and carries on into the next round if looping is switched on.
/// </summary>
sealed class EditAudio : IDisposable
{
    const int Rate = MediaSource.AudioRate;
    const int Chunk = Rate / 100;           // 10 ms
    const int Ahead = Rate * 3 / 4;          // keep this much queued ahead of what is heard

    readonly AudioOut _out;
    readonly Func<bool> _looping;
    readonly SoundReader[] _readers;
    readonly List<SoundReader> _music = new(); // readers of sound files, reused by path (render thread only)
    readonly Thread _thread;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly ManualResetEventSlim _idle = new(true);

    // The job (guarded by _gate)
    int _job;
    bool _active, _quit;
    EditList? _edit;
    int _firstPart;
    double _startAt;

    public EditAudio(string path, AudioOut output, Func<bool> looping)
    {
        _out = output;
        _looping = looping;
        _readers = new[] { new SoundReader(path), new SoundReader(path) };
        _thread = new Thread(Run) { IsBackground = true, Name = "Timeline sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Starts rendering <paramref name="edit"/> from timeline time <paramref name="at"/>, from part
    /// <paramref name="part"/> (the first one still playing then). The sound card's queue must be empty.
    /// </summary>
    public void Start(EditList edit, int part, double at)
    {
        lock (_gate)
        {
            _job++;
            _edit = edit;
            _firstPart = part;
            _startAt = at;
            _active = true;
        }
        _wake.Set();
    }

    /// <summary>Stops rendering and waits until the thread is idle.</summary>
    public void Stop()
    {
        lock (_gate) { _job++; _active = false; }
        _wake.Set();
        _idle.Wait(10000);
    }

    void Run()
    {
        while (true)
        {
            int job;
            EditList edit;
            int part;
            double start;
            lock (_gate)
            {
                if (_quit) { _idle.Set(); return; }
                if (!_active || _edit == null) { _idle.Set(); job = -1; edit = null!; part = 0; start = 0; }
                else { _idle.Reset(); job = _job; edit = _edit; part = _firstPart; start = _startAt; }
            }
            if (job < 0) { _wake.WaitOne(100); continue; }
            try { Render(job, edit, part, start); }
            catch (Exception ex) { CrashLog.Write(ex, "Rendering the sound"); }
            lock (_gate)
            {
                if (_job == job) _active = false;
            }
        }
    }

    bool Current(int job) { lock (_gate) return _job == job && !_quit; }

    /// <summary>
    /// Renders from <paramref name="at"/> in 10 ms chunks until stopped. Sample n is timeline time n / Rate; round c of a
    /// loop is shifted by c × the timeline's length (positions are rounded from the exact time, so they don't drift).
    /// </summary>
    void Render(int job, EditList edit, int firstPart, double at)
    {
        var parts = edit.Parts;
        int n = parts.Count;
        long cursor = (long)Math.Round(at * Rate), startSample = cursor;

        // Parts playing, by global index g (round g / n, part g % n), each with a reader positioned at its next sample;
        // the same for the audio track's clips (index k: round k / m, clip k % m).
        var playing = new List<(long G, SoundReader Reader)>();
        long nextG = firstPart;
        var music = edit.Music;
        int m = music.Count;
        var sounding = new List<(long K, SoundReader Reader)>();
        long nextK = 0, roundStart = 0;
        if (m > 0)
        {
            roundStart = (long)Math.Floor(at / Math.Max(edit.Length, 1e-9)) * m;
            nextK = roundStart;
            while (nextK < roundStart + m && SoundEnd(nextK) <= cursor) nextK++;
        }
        long firstK = nextK;
        // The first sound to bring in belongs to this round (not the next one, which only plays when looping).
        bool firstInRound = m > 0 && nextK < roundStart + m;
        var mix = new float[Chunk * 2];
        var tmp = new short[Chunk * 2];
        var pcm = new byte[Chunk * 4];

        long Sample(long g, double t) => (long)Math.Round(((double)(g / Math.Max(n, 1)) * edit.Length + t) * Rate);
        long SoundSample(long k, double t) => (long)Math.Round(((double)(k / m) * edit.Length + t) * Rate);
        long SoundStart(long k) => SoundSample(k, music[(int)(k % m)].Start);
        long SoundEnd(long k) => SoundSample(k, music[(int)(k % m)].End);
        bool MoreSound() => nextK == firstK && firstInRound || nextK % m != 0 || _looping();
        EditList.Part P(long g) => parts[(int)(g % n)];
        long PlayStart(long g) => Sample(g, P(g).PlayStart);
        long PlayEnd(long g) => Sample(g, P(g).PlayEnd);
        bool More() => n > 0 && (nextG == firstPart && firstPart < n || nextG % n != 0 || _looping());
        SoundReader FreeReader() => _readers.First(r => playing.All(p => p.Reader != r));

        void Open(long g, long from)
        {
            var p = P(g);
            var reader = FreeReader();
            reader.Start(p.From - p.HandleIn + Math.Max(0, from - PlayStart(g)) / (double)Rate);
            playing.Add((g, reader));
        }

        while (Current(job))
        {
            // Bring in the next part (and audio clip) once its first sample is due in this chunk.
            while (playing.Count < 2 && More() && PlayStart(nextG) < cursor + Chunk) Open(nextG++, cursor);
            while (m > 0 && sounding.Count < 2 && MoreSound() && SoundStart(nextK) < cursor + Chunk)
            {
                var c = music[(int)(nextK % m)];
                var reader = MusicReader(c.Path, sounding.Select(x => x.Reader));
                reader.Start(c.From + Math.Max(0, cursor - SoundStart(nextK)) / (double)Rate);
                sounding.Add((nextK++, reader));
            }

            if (_out.PendingFrames > Ahead) { _wake.WaitOne(10); continue; }

            Array.Clear(mix);
            foreach (var (g, reader) in playing)
            {
                long ps = PlayStart(g), pe = PlayEnd(g), zero = Sample(g, P(g).Start);
                int a = (int)Math.Clamp(ps - cursor, 0, Chunk), b = (int)Math.Clamp(pe - cursor, 0, Chunk);
                if (b <= a) continue;
                reader.Read(tmp.AsSpan(a * 2, (b - a) * 2));
                var p = P(g);
                for (int i = a; i < b; i++)
                {
                    double u = (cursor + i - zero) / (double)Rate; // seconds from the part's cut (negative in its handle)
                    float gain = (float)Gain(p, u);
                    long sinceStart = cursor + i - startSample;
                    if (sinceStart < EditList.Declick * Rate) gain *= (float)(sinceStart / (EditList.Declick * Rate)); // from where playing began
                    mix[i * 2] += tmp[i * 2] * gain;
                    mix[i * 2 + 1] += tmp[i * 2 + 1] * gain;
                }
            }
            foreach (var (k, reader) in sounding)
            {
                long ss = SoundStart(k), se = SoundEnd(k);
                int a = (int)Math.Clamp(ss - cursor, 0, Chunk), b = (int)Math.Clamp(se - cursor, 0, Chunk);
                if (b <= a) continue;
                reader.Read(tmp.AsSpan(a * 2, (b - a) * 2));
                var c = music[(int)(k % m)];
                for (int i = a; i < b; i++)
                {
                    float gain = (float)SoundGain(c, (cursor + i - ss) / (double)Rate);
                    long sinceStart = cursor + i - startSample;
                    if (sinceStart < EditList.Declick * Rate) gain *= (float)(sinceStart / (EditList.Declick * Rate));
                    mix[i * 2] += tmp[i * 2] * gain;
                    mix[i * 2 + 1] += tmp[i * 2 + 1] * gain;
                }
            }
            var s = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
            for (int i = 0; i < mix.Length; i++) s[i] = (short)Math.Clamp(MathF.Round(mix[i]), short.MinValue, short.MaxValue);
            _out.Write(pcm); // gaps and the end: silence
            cursor += Chunk;
            playing.RemoveAll(p => PlayEnd(p.G) <= cursor);
            sounding.RemoveAll(p => SoundEnd(p.K) <= cursor);
        }
    }

    /// <summary>A reader for the sound file at <paramref name="path"/> that none of <paramref name="busy"/> is.</summary>
    SoundReader MusicReader(string path, IEnumerable<SoundReader> busy)
    {
        var free = _music.FirstOrDefault(r => r.Path == path && !busy.Contains(r));
        if (free != null) return free;
        var reader = new SoundReader(path);
        _music.Add(reader);
        return reader;
    }

    /// <summary>The gain of an audio clip <paramref name="u"/> seconds in: its fades, and declicks at both ends.</summary>
    internal static double SoundGain(EditList.Sound c, double u)
    {
        double left = c.Length - u, g = 1, d = c.DeclickLength;
        if (c.FadeIn > 0 && u < c.FadeIn) g *= CrossGain(Math.Max(0, u) / c.FadeIn);
        if (c.FadeOut > 0 && left < c.FadeOut) g *= CrossGain(Math.Max(0, left) / c.FadeOut);
        if (u < d) g *= Math.Max(0, u) / d;
        if (left < d) g *= Math.Max(0, left) / d;
        return Math.Clamp(g, 0, 1);
    }

    /// <summary>
    /// The gain of part <paramref name="p"/> at <paramref name="u"/> seconds from its cut (negative inside its handle):
    /// crossfades (over 2h centred on each cut with a handle h), fades through black and the declick fades.
    /// </summary>
    internal static double Gain(EditList.Part p, double u)
    {
        double left = p.Length - u, g = 1, d = p.DeclickLength;
        if (p.HandleIn > 0 && u < p.HandleIn) g *= CrossGain((u + p.HandleIn) / (2 * p.HandleIn));
        if (p.HandleOut > 0 && left < p.HandleOut) g *= CrossGain((left + p.HandleOut) / (2 * p.HandleOut));
        if (p.FadeIn > 0 && u < p.FadeIn) g *= CrossGain(Math.Max(0, u) / p.FadeIn);
        if (p.FadeOut > 0 && left < p.FadeOut) g *= CrossGain(Math.Max(0, left) / p.FadeOut);
        if (p.CutIn && u < d) g *= Math.Max(0, u) / d;
        if (p.CutOut && left < d) g *= Math.Max(0, left) / d;
        return Math.Clamp(g, 0, 1);
    }

    /// <summary>
    /// A crossfading side's gain, <paramref name="x"/> = 0 (silent) .. 1 (full): constant power (sin / cos), the usual
    /// default in editors, so the middle of a crossfade doesn't dip in loudness. The export uses ffmpeg's acrossfade with
    /// c1 = c2 = qsin, the same curve.
    /// </summary>
    internal static double CrossGain(double x) => Math.Sin(Math.Clamp(x, 0, 1) * Math.PI / 2);

    public void Dispose()
    {
        lock (_gate) { _quit = true; _job++; _active = false; }
        _wake.Set();
        _thread.Join(10000);
        foreach (var r in _readers) r.Dispose();
        foreach (var r in _music) r.Dispose();
        _wake.Dispose();
        _idle.Dispose();
    }
}

/// <summary>Sound of the file from a given time on, as 48 kHz stereo 16-bit, in order; silence past its end.</summary>
sealed class SoundReader : IDisposable
{
    const int Rate = MediaSource.AudioRate;
    readonly MediaSource _src;
    short[] _buf = new short[Rate];
    int _pos, _len;
    double _cursor; // file time of the next sample
    bool _ended;

    public readonly string Path;

    public SoundReader(string path)
    {
        Path = path;
        _src = new MediaSource(path, video: false);
        _src.AudioEnabled = true;
    }

    public void Start(double from)
    {
        _src.AudioEnabled = true;
        _pos = _len = 0;
        _cursor = from;
        // No sound in the file (a silent video): nothing to read; never read the whole file looking for some.
        _ended = !_src.AudioEnabled;
        if (!_ended) _src.Seek(from);
    }

    /// <summary>Fills <paramref name="dst"/> (interleaved stereo) with the next samples.</summary>
    public void Read(Span<short> dst)
    {
        while (!dst.IsEmpty)
        {
            if (_pos < _len)
            {
                int n = Math.Min(dst.Length, _len - _pos);
                _buf.AsSpan(_pos, n).CopyTo(dst);
                _pos += n;
                dst = dst[n..];
                continue;
            }
            if (_ended) { dst.Clear(); return; }
            Fill();
        }
    }

    /// <summary>Decodes the next sound frame into the buffer, from the cursor on (like the old player's writer).</summary>
    void Fill()
    {
        _pos = _len = 0;
        var got = _src.Next();
        if (got == MediaSource.Got.End) { _ended = true; return; }
        if (got != MediaSource.Got.Audio) return;
        double at = _src.AudioTime;
        var pcm = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(_src.ConvertAudio());
        if (pcm.IsEmpty) return;
        int gap = 0;
        if (!double.IsNaN(at))
        {
            double end = at + pcm.Length / 2 / (double)Rate;
            if (end <= _cursor + 0.0005) return; // entirely before the start
            if (at < _cursor - 0.0005)
            {
                int skip = (int)Math.Round((_cursor - at) * Rate) * 2;
                pcm = skip < pcm.Length ? pcm[skip..] : ReadOnlySpan<short>.Empty;
            }
            else if (at > _cursor + 0.02 && at - _cursor < 10)
                gap = (int)Math.Round((at - _cursor) * Rate) * 2; // a hole in the track: silence keeps it lined up
        }
        if (_buf.Length < gap + pcm.Length) _buf = new short[gap + pcm.Length];
        _buf.AsSpan(0, gap).Clear();
        pcm.CopyTo(_buf.AsSpan(gap));
        _len = gap + pcm.Length;
        _cursor += _len / 2 / (double)Rate;
    }

    public void Dispose() => _src.Dispose();
}
