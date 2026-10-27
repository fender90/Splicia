using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Plays the timeline. Paused, its worker thread decodes single frames on request (seek, scrub, frame step), or makes a
/// black one for a gap. Playing, it plays an <see cref="EditList"/>, the way editors play a timeline: pictures come from
/// two decoder lanes, A and B (<see cref="Lane"/>), the next clip already decoding while the current one shows, and the
/// worker makes the timeline's frames from them (straight cuts; crossfades blended from both sides; fades; black in gaps)
/// into a short queue. The sound is mixed into one continuous stream by <see cref="EditAudio"/>. The UI asks for the
/// frame due at <see cref="Clock"/>, timeline time, which follows the sound card while sound is playing and a stopwatch
/// otherwise. Nothing stops or seeks at a cut, so there is no gap but the ones on the timeline.
/// </summary>
sealed class Playback : IDisposable
{
    enum Kind { At, After, Keyframe, Black, Play }

    /// <summary>A request: <paramref name="Time"/> in the file (unused for black / play), <paramref name="Out"/> on the timeline.</summary>
    readonly record struct Request(Kind Kind, double Time, double Out);

    const int MaxQueue = 12;

    public readonly MediaInfo Info;
    readonly string _path;
    readonly MediaSource _src;            // still frames, and lane A's decoder
    MediaSource? _src2;                   // lane B's decoder (opened when the timeline needs it)
    readonly (int W, int H) _size;
    readonly AudioOut? _audio;
    EditAudio? _sound;                    // opened with the first play
    readonly Lane?[] _lanes = new Lane?[2];
    readonly Thread _thread;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Stack<VideoFrame> _pool = new();
    readonly Queue<VideoFrame> _queue = new();

    Request? _request;
    bool _quit;
    VideoFrame? _still;
    volatile bool _playing;
    bool _streamEnded, _clockRunning;
    double _endAt;        // timeline time the played edit ends at (the last frame's end)
    int _generation;      // bumps on every play/pause so stale output is discarded

    // Set by Play under _gate; the worker takes its own copy when it starts
    EditList _edit = EditList.Empty;
    bool _loop;

    // Mixing state (worker thread only)
    EditList _ed = EditList.Empty;
    bool _mixing;
    int _mixGen;
    long _g;              // global index of the part being shown: cycle _g / n, part _g % n
    long _armed = -1;     // the part the other lane is decoding
    double _cursor;       // timeline time of the next frame to make (black frames fill gaps on this grid)
    double _roundEnd;     // where this round of the timeline ends (looping: the next round starts there)
    VideoFrame? _heldB;   // during a crossfade, the incoming side's frame for the current moment

    // Clock state (guarded by _gate)
    double _base;                      // timeline time the current run started at
    readonly Stopwatch _sw = new();    // video-only clock, and extrapolation when the sound runs dry
    double _anchor;                    // last clock value read from the sound card
    readonly Stopwatch _anchorAge = new();

    public Playback(string path)
    {
        _path = path;
        _src = new MediaSource(path);
        Info = _src.Info;
        // Decode at most ~1920 on the long side: plenty for the preview, and 4K stays smooth.
        _size = _src.ScaledSize(1920);
        // Always (the audio track can bring sound to a silent video); the clock follows it.
        try { _audio = new AudioOut(); }
        catch (Exception ex) { CrashLog.Write(ex, "Opening audio output (continuing without sound)"); }
        _lanes[0] = new Lane(_src, _size, Rent, Recycle, "Lane A");
        _thread = new Thread(Run) { IsBackground = true, Name = "Playback" };
        _thread.Start();
    }

    public bool HasSound => _audio != null;

    /// <summary>The sound output (tests tap it).</summary>
    internal AudioOut? Output => _audio;

    public float Volume
    {
        get => _audio?.Volume ?? 1f;
        set { if (_audio != null) _audio.Volume = value; }
    }

    public bool IsPlaying => _playing;

    /// <summary>Whether the timeline starts over when it ends (seamlessly, like any other join).</summary>
    public bool Loop
    {
        set
        {
            lock (_gate) _loop = value;
            _wake.Set();
        }
    }

    bool Looping { get { lock (_gate) return _loop; } }

    // ───────────────────────── UI side ─────────────────────────

    /// <summary>
    /// Shows the file's frame on screen at <paramref name="time"/> (paused); the frame's <see cref="VideoFrame.Out"/> is
    /// its time plus <paramref name="toTimeline"/>. Result: <see cref="TakeStill"/>.
    /// </summary>
    public void ShowAt(double time, double toTimeline) => Post(new Request(Kind.At, time, time + toTimeline));

    /// <summary>Fast approximate preview while scrubbing: the keyframe before <paramref name="time"/>, unless the exact frame is cheap.</summary>
    public void ShowNear(double time, double toTimeline) => Post(new Request(Kind.Keyframe, time, time + toTimeline));

    /// <summary>A black frame for timeline time <paramref name="at"/> (a gap, paused).</summary>
    public void ShowBlack(double at) => Post(new Request(Kind.Black, double.NaN, at));

    /// <summary>Plays <paramref name="edit"/> from timeline time <paramref name="at"/>.</summary>
    public void Play(double at, EditList edit, bool loop)
    {
        lock (_gate)
        {
            _edit = edit;
            _loop = loop;
            _playing = true;
            _clockRunning = false;
            _base = at;
            _generation++;
            _streamEnded = false;
            while (_queue.Count > 0) _pool.Push(_queue.Dequeue());
        }
        Post(new Request(Kind.Play, double.NaN, at));
    }

    /// <summary>Stops at the current clock position (timeline time), which is returned.</summary>
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
                // A seek while playing restarts playing there.
                r = new Request(Kind.Play, double.NaN, r.Out);
                _base = r.Out;
                _clockRunning = false;
                _generation++;
                _streamEnded = false;
                while (_queue.Count > 0) _pool.Push(_queue.Dequeue());
            }
            _request = r;
        }
        _wake.Set();
    }

    /// <summary>The playback position, timeline time.</summary>
    public double Clock { get { lock (_gate) return ClockLocked(); } }

    double ClockLocked()
    {
        if (!_playing || !_clockRunning) return _base;
        if (_audio == null) return _base + _sw.Elapsed.TotalSeconds;
        // Follow the sound card while it has sound queued; when it runs dry (an underrun), carry on from its last
        // position with the stopwatch.
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
            while (_queue.Count > 0 && _queue.Peek().Out <= clock + 0.002)
            {
                if (due != null) _pool.Push(due);
                due = _queue.Dequeue();
            }
            if (due != null) _wake.Set(); // room for more
            return due;
        }
    }

    /// <summary>True once the timeline has played to its end, last frame included (never while looping).</summary>
    public bool Ended
    {
        get { lock (_gate) return _playing && _streamEnded && _queue.Count == 0 && ClockLocked() >= _endAt - 0.002; }
    }

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
        while (true)
        {
            Request? req;
            int gen;
            bool playing;
            lock (_gate)
            {
                if (_quit) return;
                req = _request;
                _request = null;
                gen = _generation;
                playing = _playing;
            }
            try
            {
                if (req is Request r)
                {
                    if (r.Kind == Kind.Play) StartEdit(r.Out, gen);
                    else if (StopMixing()) Still(r);
                    continue;
                }
                if (!playing || gen != _mixGen)
                {
                    if (_mixing) StopMixing();
                    _wake.WaitOne(playing ? 5 : 200);
                    continue;
                }
                bool progressed = _mixing && MixOne();
                MaybeStartClock();
                if (!progressed) _wake.WaitOne(3);
            }
            catch (Exception ex)
            {
                // One bad step mustn't end the worker: stop this run (the UI sees it end) and carry on.
                CrashLog.Write(ex, "Playback worker");
                try { StopMixing(); } catch { }
                lock (_gate) { if (_generation == gen) { _streamEnded = true; _endAt = _base; } }
            }
        }
    }

    void Still(Request r)
    {
        var f = Rent();
        bool ok;
        if (r.Kind == Kind.Black)
        {
            Fill(f, 0xFF000000);
            f.Time = double.NaN;
            ok = true;
        }
        else
        {
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
        }
        f.Exact = r.Kind is Kind.Black or Kind.At or Kind.After || Math.Abs(f.Time - r.Time) < Info.FrameDuration;
        f.Out = r.Kind == Kind.Black ? r.Out : f.Time + (r.Out - r.Time);
        lock (_gate)
        {
            if (!ok) { _pool.Push(f); return; }
            if (_still != null) _pool.Push(_still);
            _still = f;
        }
    }

    void Fill(VideoFrame f, uint bgra)
    {
        f.EnsureSize(_size.W, _size.H);
        MemoryMarshal.Cast<byte, uint>(f.Data.AsSpan(0, _size.W * _size.H * 4)).Fill(bgra);
    }

    Lane LaneFor(long g) => _lanes[g % 2]!;

    EditList.Part PartOf(long g) => _ed.Parts[(int)(g % _ed.Parts.Count)];

    /// <summary>Timeline time of part <paramref name="g"/>'s start (its cut, before any handle), in its round of a loop.</summary>
    double BaseOf(long g) => g / _ed.Parts.Count * _ed.Length + PartOf(g).Start;



    /// <summary>Starts playing at timeline time <paramref name="at"/>: the part there on one lane, the next on the other.</summary>
    void StartEdit(double at, int gen)
    {
        StopMixing();
        lock (_gate)
        {
            if (gen != _generation) return;
            _ed = _edit;
        }
        _mixGen = gen;
        if (at >= _ed.Length - Info.FrameDuration / 2 && Looping) at = 0;
        bool over = at >= _ed.Length - Info.FrameDuration / 2;
        lock (_gate)
        {
            if (gen != _generation) return;
            _streamEnded = over;
            _endAt = at;
            _base = at;
        }
        if (over) return;
        int first = _ed.Locate(at); // the number of parts when only black (and the audio track) is left

        _g = first;
        _armed = -1;
        _cursor = at;
        _roundEnd = _ed.Length;
        if (first < _ed.Parts.Count)
        {
            StartPart(_g, at);
            ArmNext(at);
        }
        else if (_ed.Parts.Count > 0 && Looping) { StartPart(_g, at); _armed = _g; }
        if (_audio != null)
        {
            _audio.Reset();
            Sound().Start(_ed, first, at);
        }
        _mixing = true;
    }

    /// <summary>Starts part <paramref name="g"/> on its lane, from timeline time <paramref name="at"/> if that's inside it.</summary>
    void StartPart(long g, double at)
    {
        if (g % 2 == 1) EnsureLaneB();
        var p = PartOf(g);
        double playStart = BaseOf(g) - p.HandleIn;
        LaneFor(g).Start(p.From - p.HandleIn + Math.Max(0, at - playStart), p.To + p.HandleOut);
    }

    /// <summary>Has the other lane start decoding the part after the current one, so it's ready when it's due.</summary>
    void ArmNext(double at)
    {
        long next = _g + 1;
        if (next % _ed.Parts.Count == 0 && !Looping) return;
        StartPart(next, at);
        _armed = next;
    }

    /// <summary>A black frame at the cursor (a gap, or past the last video clip), moving the cursor on a frame.</summary>
    bool Black()
    {
        var black = Rent();
        Fill(black, 0xFF000000);
        black.Time = double.NaN;
        black.Out = _cursor;
        black.Exact = true;
        _cursor += Info.FrameDuration;
        return Queue(black);
    }

    /// <summary>The timeline (this round of it) is over: stop making frames, or carry on into the next round.</summary>
    bool End()
    {
        lock (_gate)
        {
            if (_generation == _mixGen) { _streamEnded = true; _endAt = _cursor; }
        }
        _mixing = false;
        return false;
    }

    void EnsureLaneB()
    {
        if (_lanes[1] != null) return;
        _src2 = new MediaSource(_path);
        _lanes[1] = new Lane(_src2, _size, Rent, Recycle, "Lane B");
    }

    EditAudio Sound()
    {
        if (_sound == null)
        {
            var sound = new EditAudio(_path, _audio!, () => Looping); // opens decoders: not under the lock
            lock (_gate) _sound = sound;
        }
        return _sound;
    }

    /// <summary>Stops the lanes and the sound; false if a decoder didn't let go (then it mustn't be used for a still).</summary>
    bool StopMixing()
    {
        bool idle = true;
        foreach (var lane in _lanes) if (lane != null && !lane.Stop()) idle = false;
        _sound?.Stop();
        _audio?.Reset();
        if (_heldB != null) { Recycle(_heldB); _heldB = null; }
        _mixing = false;
        return idle;
    }

    /// <summary>
    /// Makes the timeline's next frame, if its picture is ready: black in a gap; else the current part's frame, blended
    /// with the incoming part's frame for the same moment during a crossfade (b × x + a × (1 − x), x running 0 → 1 over
    /// the crossfade, as ffmpeg's xfade), darkened during a fade (linear, as ffmpeg's fade). When a part is used up, the
    /// next one (already decoding on the other lane) takes over and the freed lane starts on the one after. Returns false
    /// when nothing could be done yet.
    /// </summary>
    bool MixOne()
    {
        lock (_gate) if (_queue.Count >= MaxQueue) return false;
        int n = _ed.Parts.Count;
        double fd = Info.FrameDuration;
        // The end of this round of the timeline: stop there, or carry on into the next round when looping.
        if (_cursor >= _roundEnd - fd * 0.5)
        {
            if (!Looping) return End();
            _roundEnd += _ed.Length;
        }
        if (n == 0) return Black(); // only the audio track: black throughout

        var p = PartOf(_g);
        double pb = BaseOf(_g);
        if (_cursor < pb - p.HandleIn - fd * 0.5) return Black(); // a gap, the start, or the end after the last clip

        var lane = LaneFor(_g);
        if (_armed != _g && _g % n == 0 && _g > 0 && lane.Peek() == null && !lane.Ready)
        {
            StartPart(_g, _cursor); // looping was switched on late
            _armed = _g;
        }
        var a = lane.Peek();
        if (a == null)
        {
            if (!lane.Done) return false;
            // This part is over: the next one (already decoding) takes over; this lane moves on to the one after.
            if (_heldB != null) { Recycle(_heldB); _heldB = null; }
            _cursor = Math.Max(_cursor, pb + p.Length + p.HandleOut);
            _g++;
            if (_g % n != 0 || Looping)
            {
                if (_armed != _g) { StartPart(_g, _cursor); _armed = _g; }
                ArmNext(_cursor);
            }
            return true;
        }

        double t = pb + (a.Time - p.From);
        if (p.HandleOut > 0 && t >= pb + p.Length - p.HandleOut - fd * 0.25)
        {
            var laneB = LaneFor(_g + 1);
            var q = PartOf(_g + 1);
            double qb = BaseOf(_g + 1), cross = pb + p.Length - p.HandleOut;
            while (laneB.Peek() is VideoFrame b && qb + (b.Time - q.From) <= t + fd * 0.5)
            {
                laneB.Take();
                if (_heldB != null) Recycle(_heldB);
                _heldB = b;
            }
            // Wait for the incoming side if its frame for this moment isn't decoded yet.
            bool stale = _heldB == null || qb + (_heldB.Time - q.From) < t - fd * 0.5;
            if (stale && laneB.Peek() == null && !laneB.Done) return false;
            if (_heldB != null) Blend(a, _heldB, (t - cross) / (2 * p.HandleOut));
        }
        double u = t - pb, left = p.Length - u, bright = 1;
        if (p.FadeIn > 0 && u < p.FadeIn) bright *= Math.Max(0, u) / p.FadeIn;
        if (p.FadeOut > 0 && left < p.FadeOut) bright *= Math.Max(0, left) / p.FadeOut;
        if (bright < 0.999) Darken(a, bright);

        lane.Take();
        a.Out = t;
        _cursor = t + fd;
        return Queue(a);
    }

    bool Queue(VideoFrame f)
    {
        lock (_gate)
        {
            if (_generation == _mixGen) { _queue.Enqueue(f); return true; }
        }
        Recycle(f);
        return false;
    }

    void MaybeStartClock()
    {
        lock (_gate)
        {
            if (_clockRunning || !_playing || _generation != _mixGen) return;
            // Start once a few frames and a little sound are ready (or there's no more to come).
            bool videoReady = _queue.Count >= 3 || _streamEnded;
            bool audioReady = _audio == null || _audio.PendingFrames >= MediaSource.AudioRate / 5 || _streamEnded ||
                              _queue.Count >= MaxQueue - 1;
            if (!videoReady || !audioReady) return;
            _clockRunning = true;
            _anchor = _base;
            _anchorAge.Restart();
            _sw.Restart();
            _audio?.Play();
        }
    }

    /// <summary><paramref name="a"/> = a × (1 − x) + b × x, per channel.</summary>
    static void Blend(VideoFrame a, VideoFrame b, double x)
    {
        if (a.Width != b.Width || a.Height != b.Height) return;
        uint wb = (uint)Math.Clamp((int)Math.Round(x * 256), 0, 256), wa = 256 - wb;
        int pixels = a.Width * a.Height;
        byte[] da = a.Data, db = b.Data;
        Parallel.For(0, 8, k =>
        {
            var pa = MemoryMarshal.Cast<byte, uint>(da.AsSpan(0, pixels * 4));
            var pb = MemoryMarshal.Cast<byte, uint>(db.AsSpan(0, pixels * 4));
            int from = pixels * k / 8, to = pixels * (k + 1) / 8;
            for (int i = from; i < to; i++)
            {
                uint c = pa[i], d = pb[i];
                uint rb = (((c & 0x00FF00FF) * wa + (d & 0x00FF00FF) * wb) >> 8) & 0x00FF00FF;
                uint ag = ((c >> 8) & 0x00FF00FF) * wa + ((d >> 8) & 0x00FF00FF) * wb & 0xFF00FF00;
                pa[i] = rb | ag;
            }
        });
    }

    /// <summary>Scales the colour of <paramref name="a"/> by <paramref name="k"/> (0 = black), keeping it opaque.</summary>
    static void Darken(VideoFrame a, double k)
    {
        uint w = (uint)Math.Clamp((int)Math.Round(k * 256), 0, 256);
        int pixels = a.Width * a.Height;
        byte[] da = a.Data;
        Parallel.For(0, 8, part =>
        {
            var pa = MemoryMarshal.Cast<byte, uint>(da.AsSpan(0, pixels * 4));
            int from = pixels * part / 8, to = pixels * (part + 1) / 8;
            for (int i = from; i < to; i++)
            {
                uint c = pa[i];
                pa[i] = ((c & 0x00FF00FF) * w >> 8 & 0x00FF00FF) | ((c & 0x0000FF00) * w >> 8 & 0x0000FF00) | 0xFF000000;
            }
        });
    }

    public void Dispose()
    {
        lock (_gate) _quit = true;
        _wake.Set();
        _thread.Join(10000);
        foreach (var lane in _lanes) lane?.Dispose();
        _sound?.Dispose();
        _audio?.Dispose();
        _src.Dispose();
        _src2?.Dispose();
        _wake.Dispose();
    }
}
