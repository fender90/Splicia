using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Splicia;

/// <summary>How faces are hidden.</summary>
enum FaceMode { Off, Blur, Pixelate }

/// <summary>
/// Where the models and their runtime are: ffmpeg\faces next to the exe (centerface.onnx for faces, rtmo-s.onnx for
/// people's heads, and onnxruntime.dll when installed; a build finds onnxruntime.dll the usual way).
/// </summary>
static class FaceRuntime
{
    public static string Directory => Path.Combine(FFmpegRuntime.Directory, "faces");
    public static string ModelPath => Path.Combine(Directory, "centerface.onnx");
    public static string PoseModelPath => Path.Combine(Directory, "rtmo-s.onnx");
    static int _resolving;

    /// <summary>How the models run: on the CPU, with a few threads (all of a big CPU's cores make them several times slower).</summary>
    public static SessionOptions Options() => new()
    {
        IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 4, 2, 8),
        InterOpNumThreads = 1,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
    };

    /// <summary>Null when faces can be found, else why not.</summary>
    public static string? Check()
    {
        if (!File.Exists(ModelPath)) return "The face model is missing. Reinstall Splicia to restore it.";
        if (Interlocked.Exchange(ref _resolving, 1) == 0)
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
            {
                string dll = Path.Combine(Directory, "onnxruntime.dll");
                return name == "onnxruntime" && File.Exists(dll) ? NativeLibrary.Load(dll) : IntPtr.Zero;
            });
        return null;
    }
}

/// <summary>
/// CenterFace (MIT, github.com/Star-Clouds/CenterFace; its input made any size): the faces in a picture, at any angle
/// it was trained on (WIDER FACE: turned heads, profiles, crowds), as boxes with a score.
/// </summary>
sealed class FaceDetector : IDisposable
{
    // Below: mostly round things on clothes, buttons and the like. Faces turned away, looking down or partly covered
    // score 0.3 to 0.5: they count once they last or a body backs them (see FaceFinder).
    public const float Threshold = 0.3f;
    readonly InferenceSession _session;
    readonly string _input;
    readonly string[] _outputs;
    float[] _tensor = Array.Empty<float>();

    public FaceDetector()
    {
        using var options = FaceRuntime.Options();
        _session = new InferenceSession(FaceRuntime.ModelPath, options);
        _input = _session.InputMetadata.Keys.First();
        _outputs = _session.OutputMetadata.Keys.ToArray(); // heat map, box size, centre offset, landmarks
    }

    /// <summary>
    /// The faces in <paramref name="f"/> (BGRA, as decoded) looked for the way it's shown, turned
    /// <paramref name="rotation"/>° clockwise; boxes on the frame as decoded, in fractions of its size.
    /// </summary>
    public unsafe List<(RectangleF Box, float Score)> Detect(VideoFrame f, int rotation)
    {
        bool turned = rotation % 180 != 0;
        int w = turned ? f.Height : f.Width, h = turned ? f.Width : f.Height; // upright
        int W = (w + 31) / 32 * 32, H = (h + 31) / 32 * 32, plane = W * H;
        if (_tensor.Length != 3 * plane) _tensor = new float[3 * plane];
        else Array.Clear(_tensor);
        int fw = f.Width, fh = f.Height, stride = f.Stride;
        fixed (byte* src = f.Data)
        fixed (float* dst = _tensor)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var (sx, sy) = Source(x, y, rotation, fw, fh);
                    byte* p = src + sy * stride + sx * 4;
                    int i = y * W + x;
                    dst[i] = p[2];             // R, G, B, 0–255: surer of real faces than B, G, R, and fewer false ones
                    dst[plane + i] = p[1];
                    dst[2 * plane + i] = p[0];
                }
        }
        var input = new DenseTensor<float>(_tensor, new[] { 1, 3, H, W });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, input) }, _outputs);
        var heat = results[0].AsTensor<float>();
        var size = results[1].AsTensor<float>();
        var off = results[2].AsTensor<float>();
        int oh = heat.Dimensions[2], ow = heat.Dimensions[3];

        var found = new List<(float X1, float Y1, float X2, float Y2, float S)>();
        for (int i = 0; i < oh; i++)
            for (int j = 0; j < ow; j++)
            {
                float s = heat[0, 0, i, j];
                if (s < Threshold) continue;
                float bh = MathF.Exp(size[0, 0, i, j]) * 4, bw = MathF.Exp(size[0, 1, i, j]) * 4;
                float cx = (j + off[0, 1, i, j] + 0.5f) * 4, cy = (i + off[0, 0, i, j] + 0.5f) * 4;
                found.Add((Math.Max(0, cx - bw / 2), Math.Max(0, cy - bh / 2), Math.Min(w, cx + bw / 2), Math.Min(h, cy + bh / 2), s));
            }
        // One box per face: the best, without the ones overlapping it
        var faces = new List<(RectangleF, float)>();
        var kept = new List<(float X1, float Y1, float X2, float Y2, float S)>();
        foreach (var b in found.OrderByDescending(b => b.S))
        {
            if (kept.Any(k => Iou(b.X1, b.Y1, b.X2, b.Y2, k.X1, k.Y1, k.X2, k.Y2) > 0.3f)) continue;
            kept.Add(b);
            // Back onto the frame as decoded, in fractions of its size
            faces.Add((Map(RectangleF.FromLTRB(b.X1 / w, b.Y1 / h, b.X2 / w, b.Y2 / h), rotation, Unturn), b.S));
        }
        return faces;
    }

    /// <summary>A point of the picture as shown (fractions) → the same point on the frame as decoded.</summary>
    public static (float X, float Y) Unturn(float ux, float uy, int rotation) => rotation switch
    {
        90 => (uy, 1 - ux),
        180 => (1 - ux, 1 - uy),
        270 => (1 - uy, ux),
        _ => (ux, uy),
    };

    /// <summary>A point on the frame as decoded (fractions) → the same point turned <paramref name="rotation"/>° clockwise.</summary>
    public static (float X, float Y) Turn(float sx, float sy, int rotation) => rotation switch
    {
        90 => (1 - sy, sx),
        180 => (1 - sx, 1 - sy),
        270 => (sy, 1 - sx),
        _ => (sx, sy),
    };

    /// <summary><paramref name="r"/> (fractions of a frame) through <paramref name="map"/>, as a box again.</summary>
    public static RectangleF Map(RectangleF r, int rotation, Func<float, float, int, (float X, float Y)> map)
    {
        var (ax, ay) = map(r.Left, r.Top, rotation);
        var (bx, by) = map(r.Right, r.Bottom, rotation);
        return RectangleF.FromLTRB(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
    }

    /// <summary>The decoded pixel shown at (<paramref name="x"/>, <paramref name="y"/>) of the picture turned <paramref name="rotation"/>°.</summary>
    public static (int X, int Y) Source(int x, int y, int rotation, int fw, int fh) => rotation switch
    {
        90 => (y, fh - 1 - x),
        180 => (fw - 1 - x, fh - 1 - y),
        270 => (fw - 1 - y, x),
        _ => (x, y),
    };

    static float Iou(float ax1, float ay1, float ax2, float ay2, float bx1, float by1, float bx2, float by2)
    {
        float ix = Math.Max(0, Math.Min(ax2, bx2) - Math.Max(ax1, bx1)), iy = Math.Max(0, Math.Min(ay2, by2) - Math.Max(ay1, by1));
        float inter = ix * iy, union = (ax2 - ax1) * (ay2 - ay1) + (bx2 - bx1) * (by2 - by1) - inter;
        return union > 0 ? inter / union : 0;
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// RTMO-s (Apache-2.0, github.com/open-mmlab/mmpose): the people in a picture and their body keypoints, all at once.
/// Their heads are found from the whole body, so the ones the face model misses (in profile, turned away, looking down,
/// partly covered) are found too; each becomes a face box from its nose, eyes and ears, sized as the face model's boxes
/// are (measured on faces both find).
/// </summary>
sealed class PoseDetector : IDisposable
{
    const int Size = 640;                // the model's input: the picture fitted in, the rest grey (114)
    const float PersonThreshold = 0.25f, Sure = 0.3f;
    readonly InferenceSession _session;
    readonly string _input;
    readonly float[] _tensor = new float[3 * Size * Size];

    // Distances between face keypoints (0 nose, 1-2 eyes, 3-4 ears), in face-box widths: the farthest-apart pair
    // seen gives the width (turning shortens them, never lengthens).
    static readonly (int A, int B, float Width)[] Pairs =
    {
        (0, 1, 0.31f), (0, 2, 0.31f), (0, 3, 0.79f), (0, 4, 0.79f), (1, 2, 0.41f),
        (1, 3, 0.56f), (2, 4, 0.56f), (1, 4, 0.93f), (2, 3, 0.93f), (3, 4, 1.13f),
    };

    PoseDetector(string path)
    {
        using var options = FaceRuntime.Options();
        _session = new InferenceSession(path, options);
        _input = _session.InputMetadata.Keys.First();
    }

    /// <summary>The detector, or null when its model isn't installed or won't load (faces are then found by the face model alone).</summary>
    public static PoseDetector? TryCreate()
    {
        if (!File.Exists(FaceRuntime.PoseModelPath)) return null;
        try { return new PoseDetector(FaceRuntime.PoseModelPath); }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Loading the pose model");
            return null;
        }
    }

    /// <summary>
    /// The heads in <paramref name="f"/> (BGRA, as decoded) looked for the way it's shown, turned
    /// <paramref name="rotation"/>° clockwise: face boxes on the frame as decoded, in fractions of its size, with how
    /// sure the model is of the person.
    /// </summary>
    public unsafe List<(RectangleF Box, float Score)> Detect(VideoFrame f, int rotation)
    {
        bool turned = rotation % 180 != 0;
        int w = turned ? f.Height : f.Width, h = turned ? f.Width : f.Height; // upright
        float r = Math.Min(Size / (float)w, Size / (float)h);
        int tw = Math.Min(Size, (int)Math.Round(w * r)), th = Math.Min(Size, (int)Math.Round(h * r));
        const int plane = Size * Size;
        Array.Fill(_tensor, 114f);
        int fw = f.Width, fh = f.Height, stride = f.Stride;
        fixed (byte* src = f.Data)
        fixed (float* dst = _tensor)
        {
            for (int y = 0; y < th; y++)
            {
                int uy = Math.Min(h - 1, (int)(y / r));
                for (int x = 0; x < tw; x++)
                {
                    var (sx, sy) = FaceDetector.Source(Math.Min(w - 1, (int)(x / r)), uy, rotation, fw, fh);
                    byte* p = src + sy * stride + sx * 4;
                    int i = y * Size + x;
                    dst[i] = p[0];             // B, G, R, 0–255: as the model was trained
                    dst[plane + i] = p[1];
                    dst[2 * plane + i] = p[2];
                }
            }
        }
        var input = new DenseTensor<float>(_tensor, new[] { 1, 3, Size, Size });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, input) });
        var dets = results.First(x => x.Name == "dets").AsTensor<float>();     // [1, people, x1 y1 x2 y2 score]
        var kps = results.First(x => x.Name == "keypoints").AsTensor<float>(); // [1, people, 17, x y score]
        var heads = new List<(RectangleF, float)>();
        for (int n = 0; n < dets.Dimensions[1]; n++)
        {
            float score = dets[0, n, 4];
            if (score < PersonThreshold || Head(kps, n, dets[0, n, 2] - dets[0, n, 0]) is not { } b) continue;
            // the picture as shown (fractions), then the frame as decoded
            var up = RectangleF.Intersect(RectangleF.FromLTRB(b.Left / r / w, b.Top / r / h, b.Right / r / w, b.Bottom / r / h), new RectangleF(0, 0, 1, 1));
            if (up.Width <= 0 || up.Height <= 0) continue;
            heads.Add((FaceDetector.Map(up, rotation, FaceDetector.Unturn), score));
        }
        return heads;
    }

    /// <summary>
    /// The face box of person <paramref name="n"/> (in the model's pixels) from their face keypoints: around the sure ones,
    /// as wide as their farthest-apart pair says. With just one sure (an ear, say: the face turned away), around all of
    /// them weighed by how sure (the faint ones say which way the face is), sized from the shoulders or the body.
    /// </summary>
    static RectangleF? Head(Tensor<float> k, int n, float bodyWidth)
    {
        float X(int i) => k[0, n, i, 0];
        float Y(int i) => k[0, n, i, 1];
        bool Seen(int i) => k[0, n, i, 2] >= Sure;
        float Dist(int a, int b) => MathF.Sqrt((X(a) - X(b)) * (X(a) - X(b)) + (Y(a) - Y(b)) * (Y(a) - Y(b)));

        int seen = 0;
        float cx = 0, cy = 0;
        for (int i = 0; i < 5; i++)
            if (Seen(i)) { seen++; cx += X(i); cy += Y(i); }
        if (seen == 0) return null;
        float width = 0;
        if (seen >= 2)
        {
            cx /= seen;
            cy /= seen;
            foreach (var (a, b, ratio) in Pairs)
                if (Seen(a) && Seen(b)) width = Math.Max(width, Dist(a, b) / ratio);
            width /= 1.24f; // the farthest pair overshoots a little on average
            // ... but a pair seen end-on (a near ear and the nose, from behind) undershoots: not much less than the body says
            width = Math.Max(width, Math.Max(Seen(5) && Seen(6) ? Dist(5, 6) / 2.5f : 0, bodyWidth / 8));
        }
        else
        {
            float sum = 0;
            cx = cy = 0;
            for (int i = 0; i < 5; i++)
            {
                float c = k[0, n, i, 2];
                if (c < 0.1f) continue;
                cx += X(i) * c; cy += Y(i) * c; sum += c;
            }
            cx /= sum;
            cy /= sum;
            width = Seen(5) && Seen(6) ? Math.Clamp(Dist(5, 6) / 1.7f, bodyWidth / 6, bodyWidth / 2.5f) : bodyWidth / 3.9f;
        }
        if (!(width >= 2)) return null;
        float height = 1.34f * width;
        cy += 0.084f * width; // the box's middle is a little below the eyes and ears
        return new RectangleF(cx - width / 2, cy - height / 2, width, height);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// The faces in a video: one pass over it in the background (every frame decoded; faces looked for about 15 times a
/// second, at 640 pixels, and people's heads 3 times, on a thread of their own), the picture turned the way it's shown.
/// Detections become tracks, so a face the detectors miss for a moment stays covered, and is covered a moment before
/// it's first found.
/// </summary>
sealed class FaceFinder : IDisposable
{
    const float Grow = 1.35f;    // the cover is this much bigger than the face's box (hair, ears, chin)
    const double PosePerSecond = 3; // heads are looked for about this often (of the video), faces 15 times (heads take five times as long)
    double _hold = 0.4;          // a face stays covered this long after it was last found
    double _maxGap = 0.6;        // a face found again within this long carries on its track

    /// <summary>A face found: its box, the face model's score (0: found from the body alone), and whether a body backs it.</summary>
    public readonly record struct Found(RectangleF Box, float Score, bool Body);

    sealed class Track
    {
        public readonly List<(double T, RectangleF Box)> Points = new();
        public float Best;   // the face model's best score
        public bool Body;    // found from a body (at least once)
        public double Lead;  // covered this long before it was first found
        public double First => Points[0].T;
        public double Last => Points[^1].T;

        /// <summary>
        /// A face, not something else: surely one once, or found again and fairly surely or backed by a body. Faint ones
        /// (0.3 to 0.4) with no body are mostly buttons and round things on clothes, which stay put and are found again.
        /// </summary>
        public bool Sure => Best >= 0.6f || Points.Count >= 2 && (Best >= 0.4f || Body);
    }

    public readonly string Path;
    public readonly int Rotation;
    readonly object _gate = new();
    readonly List<Track> _tracks = new(), _open = new();
    readonly Thread _thread;
    readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool _quit;
    double _faceLead = 0.1, _bodyLead = 0.2;

    /// <summary>How far the pass has got (0..1); the pass ends Done, or with an Error.</summary>
    public double Progress { get; private set; }
    public bool Done { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Progress, new faces or the end of the pass (from the finder's thread, a few times a second).</summary>
    public event Action? Changed;
    public Task Completion => _done.Task;

    /// <summary>A pass over <paramref name="path"/>, shown turned <paramref name="rotation"/>°; it begins with <see cref="Start"/>.</summary>
    public FaceFinder(string path, int rotation)
    {
        Path = path;
        Rotation = rotation;
        _thread = new Thread(Run) { IsBackground = true, Name = "Faces", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Begins the pass (after subscribing to <see cref="Changed"/>, so not even an early end is missed).</summary>
    public FaceFinder Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) == 0) _thread.Start();
        return this;
    }

    int _started; // 0 not yet, 1 started, 2 disposed

    void Run()
    {
        try
        {
            if (FaceRuntime.Check() is string why) throw new FileNotFoundException(why);
            NoEcoQos();
            using var detector = new FaceDetector();
            using var heads = PoseDetector.TryCreate() is { } pose ? new Heads(pose, Rotation) : null;
            using var src = new MediaSource(Path);
            src.AudioEnabled = false;
            var (aw, ah) = src.ScaledSize(640);
            var frame = new VideoFrame();
            int step = Math.Max(1, (int)Math.Round(src.Info.Fps / 15));
            int poseEvery = Math.Max(1, (int)Math.Round(src.Info.Fps / step / PosePerSecond));
            double poseGap = step * poseEvery * src.Info.FrameDuration; // between looks for heads (or faces, slower)
            _faceLead = step * src.Info.FrameDuration * 1.5;
            _bodyLead = poseGap;
            _maxGap = Math.Max(0.6, 1.5 * poseGap);
            _hold = Math.Max(0.4, 1.2 * poseGap);
            double length = Math.Max(src.Info.Duration, 1e-3);
            long n = 0, looked = 0;
            var told = Stopwatch.StartNew();
            // Faces found, in order, waiting for the heads in their picture (if they're looked for in it)
            var waiting = new Queue<(double T, List<Found> Faces, Task<List<(RectangleF, float)>>? Heads)>();
            void Flush(bool all)
            {
                while (waiting.Count > 0)
                {
                    var (t, faces, found) = waiting.Peek();
                    if (found is { IsCompleted: false } && !all && waiting.Count < 64) break;
                    waiting.Dequeue();
                    if (found != null) Merge(faces, Heads.Result(found));
                    Add(t, faces);
                }
            }
            while (!_quit)
            {
                var got = src.Next();
                if (got == MediaSource.Got.End) break;
                if (got != MediaSource.Got.Video || n++ % step != 0) continue;
                src.ConvertVideo(frame, aw, ah);
                var faces = detector.Detect(frame, Rotation).Select(x => new Found(x.Box, x.Score, false)).ToList();
                waiting.Enqueue((frame.Time, faces, heads != null && looked++ % poseEvery == 0 ? heads.Find(frame) : null));
                Flush(all: false);
                Progress = Math.Clamp(frame.Time / length, 0, 1);
                if (told.ElapsedMilliseconds > 250) { told.Restart(); Changed?.Invoke(); }
            }
            if (!_quit)
            {
                Flush(all: true);
                Progress = 1;
                Done = true;
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            CrashLog.Write(ex, $"Finding faces in {Path}");
        }
        finally
        {
            _done.TrySetResult();
            Changed?.Invoke();
        }
    }

    /// <summary>People's heads, found on a thread of their own while faces are looked for in the next pictures.</summary>
    sealed class Heads : IDisposable
    {
        readonly PoseDetector _pose;
        readonly int _rotation;
        readonly BlockingCollection<(VideoFrame Frame, TaskCompletionSource<List<(RectangleF, float)>> Done)> _queue = new(boundedCapacity: 4);
        readonly Thread _thread;
        volatile bool _stop;
        static int _failed;

        public Heads(PoseDetector pose, int rotation)
        {
            _pose = pose;
            _rotation = rotation;
            _thread = new Thread(Run) { IsBackground = true, Name = "Heads", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        /// <summary>The heads in (a copy of) <paramref name="f"/>, when found; waits while four pictures are queued.</summary>
        public Task<List<(RectangleF, float)>> Find(VideoFrame f)
        {
            var copy = new VideoFrame { Data = f.Data.AsSpan(0, f.Width * f.Height * 4).ToArray(), Width = f.Width, Height = f.Height, Time = f.Time };
            var done = new TaskCompletionSource<List<(RectangleF, float)>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((copy, done));
            return done.Task;
        }

        /// <summary>The heads found (none if looking failed: the faces found are still used; logged once).</summary>
        public static List<(RectangleF, float)> Result(Task<List<(RectangleF, float)>> found)
        {
            try { return found.GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (Interlocked.Exchange(ref _failed, 1) == 0) CrashLog.Write(ex, "Finding heads");
                return new();
            }
            catch (OperationCanceledException) { return new(); }
        }

        void Run()
        {
            NoEcoQos();
            foreach (var (frame, done) in _queue.GetConsumingEnumerable())
            {
                if (_stop) { done.TrySetCanceled(); continue; }
                try { done.TrySetResult(_pose.Detect(frame, _rotation)); }
                catch (Exception ex) { done.TrySetException(ex); }
            }
        }

        public void Dispose()
        {
            _stop = true;
            _queue.CompleteAdding();
            _thread.Join();
            _pose.Dispose();
            _queue.Dispose();
        }
    }

    /// <summary>
    /// The heads found from bodies, into the faces found: a head where a face is backs it (the face's box is kept: it's
    /// the closer fit); one where none is (in profile, turned away, covered) is a face found from its body alone.
    /// </summary>
    static void Merge(List<Found> faces, List<(RectangleF Box, float Score)> heads)
    {
        int n = faces.Count;
        var backed = new bool[n];
        var kept = new List<RectangleF>();
        foreach (var (head, _) in heads.OrderByDescending(h => h.Score))
        {
            if (kept.Any(k => Overlap(k, head) > 0.5f)) continue; // the same person found twice (less surely)
            kept.Add(head);
            int best = -1;
            float bestFit = 0;
            for (int i = 0; i < n; i++)
            {
                var f = faces[i].Box;
                // Not one already backed, nor one much smaller or bigger: a faint find inside a head (a button, a hand
                // under the chin) isn't its face, and the head is kept.
                float size = f.Width / Math.Max(1e-6f, head.Width);
                if (backed[i] || size < 0.5f || size > 2f) continue;
                float fit = Math.Max(Overlap(f, head), f.Contains(Centre(head)) || head.Contains(Centre(f)) ? 0.2f : 0);
                if (fit > bestFit) { bestFit = fit; best = i; }
            }
            if (best >= 0 && bestFit >= 0.2f)
            {
                faces[best] = faces[best] with { Body = true };
                backed[best] = true;
            }
            else faces.Add(new Found(head, 0, true));
        }
    }

    static PointF Centre(RectangleF r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    /// <summary>Joins the faces found at <paramref name="t"/> onto the tracks they carry on (or starts new ones).</summary>
    void Add(double t, List<Found> faces)
    {
        lock (_gate)
        {
            _open.RemoveAll(k => t - k.Last > _maxGap);
            var free = new List<Track>(_open);
            foreach (var (box, score, body) in faces.OrderByDescending(f => f.Score).ThenByDescending(f => f.Body))
            {
                Track? best = null;
                float bestFit = 0;
                foreach (var k in free)
                {
                    var last = k.Points[^1].Box;
                    float fit = Overlap(box, last);
                    // a face moving faster than it overlaps itself: close enough, for its size
                    float dx = (box.X + box.Width / 2) - (last.X + last.Width / 2), dy = (box.Y + box.Height / 2) - (last.Y + last.Height / 2);
                    float reach = Math.Max(last.Width, last.Height) * 0.75f;
                    if (fit < 0.15f && dx * dx + dy * dy < reach * reach) fit = 0.15f;
                    if (fit > bestFit) { bestFit = fit; best = k; }
                }
                if (best == null || bestFit < 0.15f)
                {
                    // found from its body alone: it may have been there since the last look for heads
                    best = new Track { Lead = score > 0 ? _faceLead : _bodyLead };
                    _tracks.Add(best);
                    _open.Add(best);
                }
                else free.Remove(best);
                best.Points.Add((t, box));
                best.Best = Math.Max(best.Best, score);
                best.Body |= body;
            }
        }
    }

    static float Overlap(RectangleF a, RectangleF b)
    {
        var i = RectangleF.Intersect(a, b);
        if (i.IsEmpty) return 0;
        float inter = i.Width * i.Height, union = a.Width * a.Height + b.Width * b.Height - inter;
        return union > 0 ? inter / union : 0;
    }

    /// <summary>
    /// The faces to cover at <paramref name="t"/> (the file's time), on the frame as decoded, in fractions of its size:
    /// each track's box there (in between its detections, where they lead), grown to cover the whole head. A face found
    /// only once, and not surely, is taken for something else.
    /// </summary>
    public List<RectangleF> At(double t)
    {
        var boxes = new List<RectangleF>();
        lock (_gate)
        {
            double lead = Math.Max(_faceLead, _bodyLead);
            foreach (var k in _tracks)
            {
                if (k.First - lead > t) break; // the tracks start in order
                if (k.First - k.Lead > t || t > k.Last + _hold || !k.Sure) continue;
                var p = k.Points;
                RectangleF box;
                int i = p.FindLastIndex(q => q.T <= t);
                if (i < 0) box = p[0].Box;
                else if (i == p.Count - 1) box = p[i].Box;
                else
                {
                    float u = (float)((t - p[i].T) / Math.Max(1e-6, p[i + 1].T - p[i].T));
                    RectangleF a = p[i].Box, b = p[i + 1].Box;
                    box = new RectangleF(a.X + (b.X - a.X) * u, a.Y + (b.Y - a.Y) * u, a.Width + (b.Width - a.Width) * u, a.Height + (b.Height - a.Height) * u);
                }
                // Grown upright (as the face was found), with more forehead and hair than chin
                var up = FaceDetector.Map(box, Rotation, FaceDetector.Turn);
                float gw = up.Width * (Grow - 1) / 2, gh = up.Height * (Grow - 1) / 2;
                up = RectangleF.FromLTRB(up.Left - gw, up.Top - gh * 1.3f, up.Right + gw, up.Bottom + gh * 0.7f);
                boxes.Add(FaceDetector.Map(up, Rotation, FaceDetector.Unturn));
            }
        }
        return boxes;
    }

    /// <summary>How many faces have been found so far (tracks that count).</summary>
    public int Count
    {
        get { lock (_gate) return _tracks.Count(k => k.Sure); }
    }

    // Windows throttles a window's background threads (EcoQoS) to a fraction of their speed when it isn't in front.
    [StructLayout(LayoutKind.Sequential)]
    struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] static extern bool SetThreadInformation(IntPtr thread, int infoClass, ref PowerThrottling info, uint size);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, uint size);

    /// <summary>
    /// Full speed for this thread and the process (ONNX Runtime's own threads too): looking for faces is work the user
    /// asked for, and goes on while they switch to another window.
    /// </summary>
    static void NoEcoQos()
    {
        var s = new PowerThrottling { Version = 1, ControlMask = 1 /* execution speed */, StateMask = 0 };
        try
        {
            SetThreadInformation(GetCurrentThread(), 3 /* ThreadPowerThrottling */, ref s, (uint)Marshal.SizeOf<PowerThrottling>());
            SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref s, (uint)Marshal.SizeOf<PowerThrottling>());
        }
        catch (EntryPointNotFoundException) { } // before Windows 10 1709 / Windows 8
    }

    /// <summary>Stops the pass (within a frame or so) and waits for it.</summary>
    public void Dispose()
    {
        _quit = true;
        if (Interlocked.Exchange(ref _started, 2) == 0) _done.TrySetResult(); // never started (and now never will)
        else if (_thread.IsAlive) _thread.Join(10000);
    }
}

/// <summary>
/// How faces are hidden (blurred or pixelated, and how strongly) and where they are: drawn on the preview's frames,
/// and in the export as ffmpeg's own blur or pixelation through a mask of the faces. Strengths are in the file's
/// pixels, from its shorter side, so the preview (smaller) and the export look the same.
/// </summary>
sealed class FaceEffect
{
    public readonly FaceFinder Finder;
    public readonly FaceMode Mode;
    public readonly int Amount; // 1–100
    readonly int _w, _h;        // the file's picture as decoded

    public FaceEffect(FaceFinder finder, FaceMode mode, int amount, MediaInfo info)
    {
        Finder = finder;
        Mode = mode;
        Amount = Math.Clamp(amount, 1, 100);
        _w = Math.Max(1, info.Width);
        _h = Math.Max(1, info.Height);
    }

    double Short => Math.Min(_w, _h);
    /// <summary>The blur's sigma, in the file's pixels (all the way: a 25th of the shorter side).</summary>
    public double Sigma => Math.Max(1, Amount / 100.0 * Short / 25);
    /// <summary>The pixelation's block size, in the file's pixels (all the way: a tenth of the shorter side).</summary>
    public int Block => Math.Max(2, (int)Math.Round(Amount / 100.0 * Short / 10));

    const float Feather = 0.12f; // the soft edge, as a fraction of the cover's radius

    static float Cover(float dx, float dy)
    {
        float d = MathF.Sqrt(dx * dx + dy * dy);
        return Math.Clamp((1 - d) / Feather, 0, 1);
    }

    /// <summary>Hides the faces in a preview frame (BGRA, as decoded, scaled), where they are at its time.</summary>
    public void Apply(VideoFrame f)
    {
        if (Mode == FaceMode.Off || double.IsNaN(f.Time)) return;
        var boxes = Finder.At(f.Time);
        if (boxes.Count == 0) return;
        double scale = f.Width / (double)_w;
        foreach (var b in boxes)
        {
            float cx = (b.X + b.Width / 2) * f.Width, cy = (b.Y + b.Height / 2) * f.Height;
            float rx = Math.Max(1, b.Width / 2 * f.Width), ry = Math.Max(1, b.Height / 2 * f.Height);
            if (Mode == FaceMode.Pixelate) Pixelate(f, cx, cy, rx, ry, Math.Max(1, Block * scale));
            else Blur(f, cx, cy, rx, ry, Math.Max(0.5, Sigma * scale));
        }
    }

    static unsafe void Pixelate(VideoFrame f, float cx, float cy, float rx, float ry, double block)
    {
        int W = f.Width, H = f.Height, stride = f.Stride;
        int x0 = Math.Clamp((int)Math.Floor(cx - rx), 0, W), x1 = Math.Clamp((int)Math.Ceiling(cx + rx), 0, W);
        int y0 = Math.Clamp((int)Math.Floor(cy - ry), 0, H), y1 = Math.Clamp((int)Math.Ceiling(cy + ry), 0, H);
        if (x0 >= x1 || y0 >= y1) return;
        fixed (byte* data = f.Data)
        {
            // Blocks on the frame's grid, as ffmpeg's scale down and up again makes them
            for (int ky = (int)(y0 / block); ky * block < y1; ky++)
                for (int kx = (int)(x0 / block); kx * block < x1; kx++)
                {
                    int bx0 = (int)Math.Round(kx * block), bx1 = Math.Min(W, (int)Math.Round((kx + 1) * block));
                    int by0 = (int)Math.Round(ky * block), by1 = Math.Min(H, (int)Math.Round((ky + 1) * block));
                    if (bx0 >= bx1 || by0 >= by1) continue;
                    long sb = 0, sg = 0, sr = 0;
                    for (int y = by0; y < by1; y++)
                    {
                        byte* p = data + y * stride + bx0 * 4;
                        for (int x = bx0; x < bx1; x++, p += 4) { sb += p[0]; sg += p[1]; sr += p[2]; }
                    }
                    int n = (bx1 - bx0) * (by1 - by0);
                    byte b = (byte)(sb / n), g = (byte)(sg / n), r = (byte)(sr / n);
                    for (int y = Math.Max(by0, y0); y < Math.Min(by1, y1); y++)
                    {
                        byte* p = data + y * stride + Math.Max(bx0, x0) * 4;
                        for (int x = Math.Max(bx0, x0); x < Math.Min(bx1, x1); x++, p += 4)
                        {
                            float a = Cover((x + 0.5f - cx) / rx, (y + 0.5f - cy) / ry);
                            if (a <= 0) continue;
                            p[0] = (byte)(p[0] + (b - p[0]) * a);
                            p[1] = (byte)(p[1] + (g - p[1]) * a);
                            p[2] = (byte)(p[2] + (r - p[2]) * a);
                        }
                    }
                }
        }
    }

    static unsafe void Blur(VideoFrame f, float cx, float cy, float rx, float ry, double sigma)
    {
        int W = f.Width, H = f.Height, stride = f.Stride;
        int x0 = Math.Clamp((int)Math.Floor(cx - rx), 0, W), x1 = Math.Clamp((int)Math.Ceiling(cx + rx), 0, W);
        int y0 = Math.Clamp((int)Math.Floor(cy - ry), 0, H), y1 = Math.Clamp((int)Math.Ceiling(cy + ry), 0, H);
        if (x0 >= x1 || y0 >= y1) return;
        // The blur needs the picture around the cover too
        int pad = (int)Math.Ceiling(3 * sigma);
        int rx0 = Math.Max(0, x0 - pad), rx1 = Math.Min(W, x1 + pad), ry0 = Math.Max(0, y0 - pad), ry1 = Math.Min(H, y1 + pad);
        int w = rx1 - rx0, h = ry1 - ry0;
        // Work space, kept for the next face (each decoding thread has its own)
        var buf = t_buf ??= new float[3][];
        for (int c = 0; c < 3; c++) buf[c] = Room(buf[c], w * h);
        var tmp = t_tmp = Room(t_tmp, w * h);
        var sums = t_sums = Room(t_sums, w);
        fixed (byte* data = f.Data)
        {
            for (int y = 0; y < h; y++)
            {
                byte* p = data + (ry0 + y) * stride + rx0 * 4;
                for (int x = 0; x < w; x++, p += 4) { buf[0][y * w + x] = p[0]; buf[1][y * w + x] = p[1]; buf[2][y * w + x] = p[2]; }
            }
            // A Gaussian, as three box blurs (as ffmpeg's gblur approximates it too)
            foreach (int size in GaussBoxes(sigma, 3))
                for (int c = 0; c < 3; c++)
                {
                    BoxH(buf[c], tmp, w, h, size / 2);
                    BoxV(tmp, buf[c], w, h, size / 2, sums);
                }
            for (int y = y0; y < y1; y++)
            {
                byte* p = data + y * stride + x0 * 4;
                for (int x = x0; x < x1; x++, p += 4)
                {
                    float a = Cover((x + 0.5f - cx) / rx, (y + 0.5f - cy) / ry);
                    if (a <= 0) continue;
                    int i = (y - ry0) * w + (x - rx0);
                    p[0] = (byte)Math.Clamp(p[0] + (buf[0][i] - p[0]) * a, 0, 255);
                    p[1] = (byte)Math.Clamp(p[1] + (buf[1][i] - p[1]) * a, 0, 255);
                    p[2] = (byte)Math.Clamp(p[2] + (buf[2][i] - p[2]) * a, 0, 255);
                }
            }
        }
    }

    /// <summary>Box widths whose successive blurs approximate a Gaussian of <paramref name="sigma"/>.</summary>
    static int[] GaussBoxes(double sigma, int n)
    {
        double ideal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(ideal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        int m = (int)Math.Round((12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4.0 * wl - 4));
        return Enumerable.Range(0, n).Select(i => Math.Max(1, i < m ? wl : wu)).ToArray();
    }

    [ThreadStatic] static float[][]? t_buf;
    [ThreadStatic] static float[]? t_tmp, t_sums;

    static float[] Room(float[]? a, int n) => a != null && a.Length >= n ? a : new float[Math.Max(n, (a?.Length ?? 0) * 3 / 2)];

    static void BoxH(float[] src, float[] dst, int w, int h, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, w * h); return; }
        float inv = 1f / (2 * r + 1);
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0;
            for (int k = -r; k <= r; k++) sum += src[row + Math.Clamp(k, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                dst[row + x] = sum * inv;
                sum += src[row + Math.Min(x + r + 1, w - 1)] - src[row + Math.Max(x - r, 0)];
            }
        }
    }

    /// <summary>The vertical box blur, a row at a time (every column's running sum in <paramref name="sums"/>).</summary>
    static void BoxV(float[] src, float[] dst, int w, int h, int r, float[] sums)
    {
        if (r <= 0) { Array.Copy(src, dst, w * h); return; }
        float inv = 1f / (2 * r + 1);
        Array.Clear(sums, 0, w);
        for (int k = -r; k <= r; k++)
        {
            int row = Math.Clamp(k, 0, h - 1) * w;
            for (int x = 0; x < w; x++) sums[x] += src[row + x];
        }
        for (int y = 0; y < h; y++)
        {
            int o = y * w, add = Math.Min(y + r + 1, h - 1) * w, sub = Math.Max(y - r, 0) * w;
            for (int x = 0; x < w; x++)
            {
                dst[o + x] = sums[x] * inv;
                sums[x] += src[add + x] - src[sub + x];
            }
        }
    }

    /// <summary>
    /// The export's mask of the faces at <paramref name="t"/>, white where they're hidden (soft-edged as in the preview),
    /// on a <paramref name="mw"/>×<paramref name="mh"/> picture turned <paramref name="rotation"/>° clockwise, as ffmpeg
    /// decodes the file (it applies the file's own rotation).
    /// </summary>
    public void DrawMask(byte[] mask, int mw, int mh, double t, int rotation)
    {
        Array.Clear(mask, 0, mw * mh);
        foreach (var s in Finder.At(t))
        {
            // the cover's box, from the frame as decoded to the picture as ffmpeg gives it
            var c = FaceDetector.Map(s, rotation, FaceDetector.Turn);
            float l = c.Left * mw, r = c.Right * mw, tp = c.Top * mh, bt = c.Bottom * mh;
            float cx = (l + r) / 2, cy = (tp + bt) / 2, rx = Math.Max(0.5f, (r - l) / 2), ry = Math.Max(0.5f, (bt - tp) / 2);
            for (int y = Math.Max(0, (int)tp); y < Math.Min(mh, (int)Math.Ceiling(bt)); y++)
                for (int x = Math.Max(0, (int)l); x < Math.Min(mw, (int)Math.Ceiling(r)); x++)
                {
                    float a = Cover((x + 0.5f - cx) / rx, (y + 0.5f - cy) / ry);
                    int i = y * mw + x;
                    mask[i] = (byte)Math.Max(mask[i], (int)(a * 255 + 0.5f));
                }
        }
    }

    /// <summary>The ffmpeg filter that hides a whole <paramref name="w"/>×<paramref name="h"/> picture (the mask picks the faces).</summary>
    public string Filter(int w, int h) => Mode == FaceMode.Pixelate
        ? $"scale=trunc(iw/{Block}):trunc(ih/{Block}):flags=area,scale={w}:{h}:flags=neighbor"
        : $"gblur=sigma={Sigma.ToString("0.###", CultureInfo.InvariantCulture)}";
}
