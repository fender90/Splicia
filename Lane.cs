namespace Splicia;

/// <summary>
/// One picture decoder running ahead on its own thread through a stretch [from, to) of the file, converting frames into
/// a short queue. Playback has two (an A/B roll, as in tape editing and editors since): while one part of an edit plays
/// from one lane, the next part is already seeked and decoded on the other, so a cut needs no stop to seek and a
/// crossfade has both sides' pictures at once.
/// </summary>
sealed class Lane : IDisposable
{
    const int MaxFrames = 6; // ahead of the edit's own queue (Playback.MaxQueue), so a little is enough

    public readonly MediaSource Src;
    readonly MediaInfo _info;
    readonly (int W, int H) _size;
    readonly Func<VideoFrame> _rent;
    readonly Action<VideoFrame> _return;
    readonly Thread _thread;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly ManualResetEventSlim _idle = new(true);
    readonly Queue<VideoFrame> _frames = new();

    // The stretch (guarded by _gate)
    int _job;              // bumps on every Start / Stop: work from an older job is thrown away
    bool _active, _seek, _done, _quit;
    double _from, _to;

    // Decoding state (lane thread only)
    double _backoff;
    bool _firstFrame;

    public Lane(MediaSource src, (int W, int H) size, Func<VideoFrame> rent, Action<VideoFrame> recycle, string name)
    {
        Src = src;
        _info = src.Info;
        _size = size;
        _rent = rent;
        _return = recycle;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>The stretch being decoded, in the file's seconds.</summary>
    public double From { get { lock (_gate) return _from; } }

    /// <summary>Starts decoding [<paramref name="from"/>, <paramref name="to"/>), dropping whatever was queued.</summary>
    public void Start(double from, double to)
    {
        lock (_gate)
        {
            _job++;
            Drain();
            _from = from;
            _to = to;
            _active = _seek = true;
            _done = false;
        }
        _wake.Set();
    }

    /// <summary>
    /// Stops and waits until the thread has let go of <see cref="Src"/> (so it can be used for still frames). False if it
    /// didn't within 10 s (a stuck read): then <see cref="Src"/> mustn't be touched.
    /// </summary>
    public bool Stop()
    {
        lock (_gate)
        {
            _job++;
            _active = false;
            Drain();
        }
        _wake.Set();
        return _idle.Wait(10000);
    }

    /// <summary>The next frame of the stretch, or null when none is ready yet (see <see cref="Done"/>).</summary>
    public VideoFrame? Peek() { lock (_gate) return _frames.Count > 0 ? _frames.Peek() : null; }

    public VideoFrame? Take()
    {
        lock (_gate)
        {
            if (_frames.Count == 0) return null;
            var f = _frames.Dequeue();
            _wake.Set(); // room for more
            return f;
        }
    }

    /// <summary>True once every frame of the stretch has been queued and taken.</summary>
    public bool Done { get { lock (_gate) return _done && _frames.Count == 0; } }

    /// <summary>Frames queued so far (the stretch's start is ready once there's one, or it's done).</summary>
    public bool Ready { get { lock (_gate) return _frames.Count > 0 || _done; } }

    void Drain()
    {
        while (_frames.Count > 0) _return(_frames.Dequeue());
    }

    void Run()
    {
        while (true)
        {
            int job;
            double from, to;
            bool seek;
            lock (_gate)
            {
                if (_quit) { _idle.Set(); return; }
                if (!_active || _done || _frames.Count >= MaxFrames)
                {
                    if (!_active) _idle.Set();
                    job = -1; from = to = 0; seek = false;
                }
                else
                {
                    _idle.Reset();
                    job = _job; from = _from; to = _to; seek = _seek;
                    _seek = false;
                }
            }
            if (job < 0) { _wake.WaitOne(50); continue; }
            try { Step(job, from, to, seek); }
            catch (Exception ex)
            {
                CrashLog.Write(ex, $"Decoding {from:0.###}–{to:0.###} s");
                lock (_gate) if (_job == job) _done = true;
            }
        }
    }

    /// <summary>Seeks (on a new stretch) or decodes one frame and queues it if it's inside the stretch.</summary>
    void Step(int job, double from, double to, bool seek)
    {
        if (seek)
        {
            Src.AudioEnabled = false;
            Src.Seek(from);
            _backoff = 0;
            _firstFrame = true;
            return;
        }
        var got = Src.Next();
        if (got == MediaSource.Got.End)
        {
            lock (_gate) if (_job == job) _done = true;
            return;
        }
        if (got != MediaSource.Got.Video) return;
        double t = Src.VideoTime;
        if (_firstFrame)
        {
            _firstFrame = false;
            // The seek landed on the keyframe after the start (B-frames: its decode time is earlier than its display
            // time). Go further back so the stretch starts on the right frame.
            if (t > from + _info.FrameDuration * 0.5 && from - _backoff > 0.001 && _backoff < 30)
            {
                _backoff = _backoff == 0 ? 1 : _backoff * 3;
                Src.Seek(Math.Max(0, from - _backoff));
                _firstFrame = true;
                return;
            }
        }
        if (t < from - 1e-4) return; // decoding up from the keyframe before the start
        if (t >= to - 1e-4)
        {
            lock (_gate) if (_job == job) _done = true;
            return;
        }
        var f = _rent();
        Src.ConvertVideo(f, _size.W, _size.H);
        f.Exact = true;
        lock (_gate)
        {
            if (_job == job) { _frames.Enqueue(f); return; }
        }
        _return(f);
    }

    public void Dispose()
    {
        lock (_gate) { _quit = true; _job++; _active = false; Drain(); }
        _wake.Set();
        _thread.Join(10000);
        _wake.Dispose();
        _idle.Dispose();
    }
}
