using System.Diagnostics;

namespace Splicia;

/// <summary>
/// Owns a <see cref="MediaSource"/> on a worker thread. Paused, it decodes single frames on request (seek, scrub,
/// frame step). Playing, it streams frames into a short queue and sound into <see cref="AudioOut"/>; the UI asks for the
/// frame due at <see cref="Clock"/>, which follows the sound card while sound is playing and a stopwatch otherwise.
/// </summary>
sealed class Playback : IDisposable
{
    enum Kind { At, After, Keyframe, Play }
    readonly record struct Request(Kind Kind, double Time);

    const int MaxQueue = 12;

    public readonly MediaInfo Info;
    readonly MediaSource _src;
    readonly (int W, int H) _size;
    readonly AudioOut? _audio;
    readonly Thread _thread;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Stack<VideoFrame> _pool = new();
    readonly Queue<VideoFrame> _queue = new();

    Request? _request;
    bool _quit;
    VideoFrame? _still;
    volatile bool _playing;
    bool _streaming, _streamEnded, _clockRunning;
    int _generation; // bumps on every play/pause so stale stream output is discarded

    // Clock state (guarded by _gate)
    double _base;                      // media time the current run started at
    readonly Stopwatch _sw = new();    // video-only clock, and extrapolation when the sound runs dry
    double _anchor;                    // last clock value read from the sound card
    readonly Stopwatch _anchorAge = new();
    double _audioCursor;               // media time the next written sample will play at

    public Playback(string path)
    {
        _src = new MediaSource(path);
        Info = _src.Info;
        // Decode at most ~1920 on the long side: plenty for the preview, and 4K stays smooth.
        _size = _src.ScaledSize(1920);
        if (Info.HasAudio)
        {
            try { _audio = new AudioOut(); }
            catch (Exception ex) { CrashLog.Write(ex, "Opening audio output (continuing without sound)"); }
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "Playback" };
        _thread.Start();
    }

    public bool HasSound => _audio != null;

    public float Volume
    {
        get => _audio?.Volume ?? 1f;
        set { if (_audio != null) _audio.Volume = value; }
    }

    public bool IsPlaying => _playing;

    // ───────────────────────── UI side ─────────────────────────

    /// <summary>Shows the frame on screen at <paramref name="time"/> (paused). Result: <see cref="TakeStill"/>.</summary>
    public void ShowAt(double time) => Post(new Request(Kind.At, time));

    /// <summary>Fast approximate preview while scrubbing: the keyframe before <paramref name="time"/>, unless the exact frame is cheap.</summary>
    public void ShowNear(double time) => Post(new Request(Kind.Keyframe, time));

    /// <summary>Shows the first frame after the one starting at <paramref name="time"/> (paused).</summary>
    public void ShowNext(double time) => Post(new Request(Kind.After, time));

    public void Play(double from)
    {
        lock (_gate)
        {
            _playing = true;
            _clockRunning = false;
            _base = from;
            _generation++;
            _streamEnded = false;
            while (_queue.Count > 0) _pool.Push(_queue.Dequeue());
        }
        Post(new Request(Kind.Play, from));
    }

    /// <summary>Stops at the current clock position, which is returned.</summary>
    public double Pause()
    {
        lock (_gate)
        {
            double at = ClockLocked();
            _playing = false;
            _clockRunning = false;
            _base = at;
            _generation++;
            _sw.Reset();
            _audio?.Pause();
            while (_queue.Count > 0) _pool.Push(_queue.Dequeue());
        }
        _wake.Set();
        return _base;
    }

    void Post(Request r)
    {
        lock (_gate)
        {
            if (r.Kind != Kind.Play && _playing)
            {
                // A seek while playing restarts playback there.
                r = new Request(Kind.Play, r.Time);
                _base = r.Time;
                _clockRunning = false;
                _generation++;
            }
            _request = r;
        }
        _wake.Set();
    }

    /// <summary>The playback position in seconds.</summary>
    public double Clock { get { lock (_gate) return ClockLocked(); } }

    double ClockLocked()
    {
        if (!_playing || !_clockRunning) return _base;
        if (_audio == null) return _base + _sw.Elapsed.TotalSeconds;
        // Follow the sound card while it has sound queued; when it runs dry (sound track ended, or a gap), carry on
        // from its last position with the stopwatch.
        if (_audio.PendingFrames > 0)
        {
            _anchor = _base + _audio.PlayedFrames / (double)MediaSource.AudioRate;
            _anchorAge.Restart();
            return _anchor;
        }
        return _anchor + _anchorAge.Elapsed.TotalSeconds;
    }

    /// <summary>The newest frame due at <see cref="Clock"/> while playing (older due frames are dropped), or null.</summary>
    public VideoFrame? TakeDue(double clock)
    {
        lock (_gate)
        {
            VideoFrame? due = null;
            while (_queue.Count > 0 && _queue.Peek().Time <= clock + 0.002)
            {
                if (due != null) _pool.Push(due);
                due = _queue.Dequeue();
            }
            if (due != null) _wake.Set(); // room for more
            return due;
        }
    }

    /// <summary>True once playback has shown everything to the end of the file.</summary>
    public bool Ended { get { lock (_gate) return _playing && _streamEnded && _queue.Count == 0; } }

    /// <summary>The frame decoded for the last paused request, if a new one is ready.</summary>
    public VideoFrame? TakeStill()
    {
        lock (_gate)
        {
            var s = _still;
            _still = null;
            return s;
        }
    }

    /// <summary>Returns a frame the UI has finished with.</summary>
    public void Recycle(VideoFrame? f)
    {
        if (f == null) return;
        lock (_gate) _pool.Push(f);
    }

    VideoFrame Rent()
    {
        lock (_gate) return _pool.Count > 0 ? _pool.Pop() : new VideoFrame();
    }

    // ───────────────────────── worker ─────────────────────────

    void Run()
    {
        try
        {
            while (true)
            {
                Request? req;
                lock (_gate)
                {
                    if (_quit) return;
                    req = _request;
                    _request = null;
                }
                if (req is Request r)
                {
                    if (r.Kind == Kind.Play) StartStream(r.Time);
                    else DecodeStill(r);
                    continue;
                }
                if (_streaming && _playing && !_streamEnded && NeedMore()) StreamOne();
                else _wake.WaitOne(_streaming && _playing ? 5 : 200);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Playback worker");
            lock (_gate) { _streamEnded = true; }
        }
    }

    void DecodeStill(Request r)
    {
        _streaming = false;
        _audio?.Reset();
        _src.AudioEnabled = false;
        var f = Rent();
        bool ok;
        try
        {
            ok = r.Kind == Kind.Keyframe
                ? _src.DecodeKeyframeNear(r.Time, f, _size.W, _size.H)
                : _src.DecodeVideoAt(r.Time, r.Kind == Kind.After, f, _size.W, _size.H);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, $"Decoding frame at {r.Time:0.###} s");
            ok = false;
        }
        f.Exact = r.Kind != Kind.Keyframe || Math.Abs(f.Time - r.Time) < Info.FrameDuration;
        lock (_gate)
        {
            if (!ok) { _pool.Push(f); return; }
            if (_still != null) _pool.Push(_still);
            _still = f;
        }
    }

    int _streamGen;
    double _streamFrom, _backoff;
    bool _firstVideo;

    void StartStream(double from)
    {
        lock (_gate)
        {
            _streamGen = _generation;
            while (_queue.Count > 0) _pool.Push(_queue.Dequeue());
            _streamEnded = false;
        }
        _audio?.Reset();
        _src.AudioEnabled = _audio != null;
        _src.Seek(from);
        _streamFrom = from;
        _audioCursor = from;
        _firstVideo = true;
        _backoff = 0;
        _streaming = true;
    }

    bool NeedMore()
    {
        lock (_gate)
        {
            if (_queue.Count >= MaxQueue) return false;
            if (_queue.Count < 6) return true;
        }
        return _audio != null && _audio.PendingFrames < MediaSource.AudioRate / 2;
    }

    void StreamOne()
    {
        var got = _src.Next();
        int gen;
        lock (_gate) gen = _generation;
        if (gen != _streamGen) return; // paused or restarted meanwhile; the next request resets things

        switch (got)
        {
            case MediaSource.Got.Video:
            {
                double t = _src.VideoTime;
                if (_firstVideo)
                {
                    _firstVideo = false;
                    // The seek landed on the keyframe after the start (B-frames: its decode time is earlier than its
                    // display time). Go further back so playback starts on the right frame.
                    if (t > _streamFrom + Info.FrameDuration * 0.5 && _streamFrom - _backoff > 0.001 && _backoff < 30)
                    {
                        _backoff = _backoff == 0 ? 1 : _backoff * 3;
                        _src.Seek(Math.Max(0, _streamFrom - _backoff));
                        _audio?.Reset();
                        _audioCursor = _streamFrom;
                        _firstVideo = true;
                        return;
                    }
                }
                if (t < _streamFrom - 1e-4) return; // before the start point (decoding up from the keyframe)
                var f = Rent();
                _src.ConvertVideo(f, _size.W, _size.H);
                f.Exact = true;
                lock (_gate)
                {
                    if (_generation != _streamGen) { _pool.Push(f); return; }
                    _queue.Enqueue(f);
                }
                break;
            }
            case MediaSource.Got.Audio:
                WriteAudio();
                break;
            case MediaSource.Got.End:
                lock (_gate) _streamEnded = true;
                break;
        }
        MaybeStartClock();
    }

    void WriteAudio()
    {
        if (_audio == null) return;
        double at = _src.AudioTime;
        var pcm = _src.ConvertAudio();
        if (pcm.IsEmpty) return;
        const int Rate = MediaSource.AudioRate;
        if (!double.IsNaN(at))
        {
            double end = at + pcm.Length / 4 / (double)Rate;
            if (end <= _audioCursor + 0.0005) return; // entirely before the start point / already covered
            if (at < _audioCursor - 0.0005)
            {
                int skip = (int)Math.Round((_audioCursor - at) * Rate) * 4;
                pcm = skip < pcm.Length ? pcm[skip..] : ReadOnlySpan<byte>.Empty;
            }
            else if (at > _audioCursor + 0.02 && at - _audioCursor < 10)
            {
                // Gap (the sound starts later than the picture, or a hole in the track): fill with silence so the
                // sound stays lined up with its timestamps.
                int frames = (int)Math.Round((at - _audioCursor) * Rate);
                _audio.WriteSilence(frames);
                _audioCursor += frames / (double)Rate;
            }
        }
        _audio.Write(pcm);
        _audioCursor += pcm.Length / 4 / (double)Rate;
    }

    void MaybeStartClock()
    {
        lock (_gate)
        {
            if (_clockRunning || !_playing || _generation != _streamGen) return;
            // Start once a little is buffered (or everything there is has been decoded).
            bool videoReady = _queue.Count >= 2 || _streamEnded;
            bool audioReady = _audio == null || _streamEnded || _audio.PendingFrames >= MediaSource.AudioRate / 10 ||
                              (_queue.Count >= MaxQueue - 1);
            if (!videoReady || !audioReady) return;
            _clockRunning = true;
            _base = _streamFrom;
            _anchor = _base;
            _anchorAge.Restart();
            _sw.Restart();
            _audio?.Play();
        }
    }

    public void Dispose()
    {
        lock (_gate) _quit = true;
        _wake.Set();
        _thread.Join(2000);
        _audio?.Dispose();
        _src.Dispose();
        _wake.Dispose();
    }
}
