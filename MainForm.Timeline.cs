using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Splicia;

/// <summary>
/// The timeline, under the video: a ruler, the video track (the clips' pictures, with their sound as a waveform beneath)
/// and the audio track (sound files). The video opens as one clip. S splits the clip under the playhead; a clip can be
/// selected, deleted (leaving a gap, or closing it), dragged somewhere free or onto other clips to rearrange them. Audio
/// clips are dragged along their track and trimmed at either end. Both kinds fade in and out (drag the handle at a top
/// corner, or pick a length from the right-click menu); the dot where two video clips meet picks their transition. Gaps
/// play and export as black and silence. Edits are undoable. The timeline's scale follows its length: it rescales after
/// an edit (never during a drag, so nothing jumps under the pointer).
/// </summary>
sealed partial class MainForm
{
    static readonly Color AudioColor = Color.FromArgb(56, 204, 168);   // the audio track's clips and waveforms
    static readonly Color VideoWave = Color.FromArgb(120, 160, 255);   // the video clips' own sound

    Timeline _tl = new(Array.Empty<Clip>(), Array.Empty<AudioClip>(), 1 / 30.0, 0);
    readonly Stack<Timeline> _undo = new(), _redo = new();
    int _tlVersion;
    double _viewLen = 1;                  // seconds across the timeline's width
    int _selClip = -1, _selMusic = -1;    // the selected clip (one or the other)
    int _hitId = -1, _hoverId = -1;       // the clip under the pointer (by the last hit test / while hovering)
    Peaks? _videoPeaks;
    readonly Dictionary<string, Peaks> _musicPeaks = new(StringComparer.OrdinalIgnoreCase);
    int _peaksVersion;
    Bitmap? _tracks;                      // the tracks' pictures and waveforms (cached; overlays are drawn live)
    (int, int, int, int, int, int, double) _tracksKey;
    readonly ContextMenuStrip _clipMenu = new();
    Transition _nextTransition = new(TransitionKind.Crossfade, 0.5); // the last transition picked

    // Dragging on the timeline
    Timeline? _dragBase;      // the timeline when the drag began (live edits are made from it)
    int _dragId = -1;
    double _grab;             // pointer time − the clip's start, when grabbed
    int _downX, _dragX;
    bool _moved;
    double _ghost;            // where a dragged clip would start

    static readonly string[] SoundExtensions = { ".wav", ".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wma", ".aif", ".aiff" };
    const string SoundFilter = "Sound files|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.opus;*.wma;*.aif;*.aiff|All files (*.*)|*.*";

    static bool IsSoundFile(string path) => SoundExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // ───────────────────────── layout ─────────────────────────

    int TrackLeft => PanelRect.Left + Pad;
    int TrackWidth => PanelRect.Width - 2 * Pad;
    Rectangle RulerRect => new(TrackLeft, PanelRect.Top + P(62), TrackWidth, P(13));
    Rectangle VideoRect => new(TrackLeft, PanelRect.Top + P(77), TrackWidth, P(40));
    Rectangle WaveRect => new(TrackLeft, PanelRect.Top + P(117), TrackWidth, P(16));
    Rectangle MusicRect => new(TrackLeft, PanelRect.Top + P(139), TrackWidth, P(32));
    Rectangle TimelineRect => Rectangle.FromLTRB(TrackLeft, RulerRect.Top - P(2), TrackLeft + TrackWidth, MusicRect.Bottom + P(2));

    float X(double t) => TrackLeft + (float)(t / _viewLen * TrackWidth);
    double TimeAt(float x) => Math.Clamp((x - TrackLeft) / (double)TrackWidth, 0, 1) * _viewLen;
    double PxToTime(float px) => px / (double)TrackWidth * _viewLen;

    /// <summary>
    /// Rescales the timeline to its length, so its right edge is where the video ends (no empty stretch that looks like
    /// part of it), unless dragging. Things dragged past the end make it longer when they're dropped.
    /// </summary>
    void UpdateView()
    {
        if (_drag != Drag.None) return;
        _viewLen = _tl.End > 0 ? _tl.End : 1;
    }

    RectangleF ClipRect(Clip c) => new(X(c.Start), VideoRect.Top, Math.Max(1, X(c.End) - X(c.Start)), VideoRect.Height);
    RectangleF MusicRectOf(AudioClip m) => new(X(m.Start), MusicRect.Top, Math.Max(1, X(m.End) - X(m.Start)), MusicRect.Height);

    /// <summary>Where the fade handles sit: on the top edge, in by the fade (never closer than a few pixels to the corner).</summary>
    (PointF In, PointF Out) FadeHandles(RectangleF r, double fadeIn, double fadeOut)
    {
        float inset = P(7), y = r.Top + P(5);
        float xi = Math.Clamp(r.Left + (float)(fadeIn / _viewLen * TrackWidth), r.Left + inset, r.Right - inset);
        float xo = Math.Clamp(r.Right - (float)(fadeOut / _viewLen * TrackWidth), r.Left + inset, r.Right - inset);
        return (new PointF(xi, y), new PointF(xo, y));
    }

    IEnumerable<(Clip A, Clip B, PointF At)> Junctions()
    {
        for (int i = 0; i + 1 < _tl.Clips.Count; i++)
            if (_tl.Touch(_tl.Clips[i], _tl.Clips[i + 1]))
                yield return (_tl.Clips[i], _tl.Clips[i + 1], new PointF(X(_tl.Clips[i].End), VideoRect.Bottom));
    }

    // ───────────────────────── hit testing ─────────────────────────

    static bool Near(Point p, PointF q, float r) => (p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y) <= r * r;

    /// <summary>What on the timeline is under <paramref name="p"/>, and the clip it belongs to.</summary>
    (Hit Hit, int Id) TimelineHit(Point p)
    {
        if (!TimelineRect.Contains(p)) return (Hit.None, -1);
        if (Trimming) return TrimHit(p);
        if (p.Y < RulerRect.Bottom + P(1)) return (Hit.Ruler, -1);
        var video = Rectangle.FromLTRB(VideoRect.Left, VideoRect.Top, VideoRect.Right, WaveRect.Bottom);
        if (p.Y <= video.Bottom + P(3))
        {
            foreach (var (a, _, at) in Junctions()) if (Near(p, at, P(10))) return (Hit.Junction, a.Id);
            foreach (var c in _tl.Clips)
            {
                var r = ClipRect(c);
                if (c.Id == _selClip || c.Id == _hoverId)
                {
                    var (fi, fo) = FadeHandles(r, c.FadeIn, c.FadeOut);
                    if (Near(p, fi, P(7))) return (Hit.ClipFadeIn, c.Id);
                    if (Near(p, fo, P(7))) return (Hit.ClipFadeOut, c.Id);
                }
                if (p.X >= r.Left && p.X < r.Right) return (Hit.Clip, c.Id);
            }
            return (Hit.Track, -1);
        }
        if (p.Y >= MusicRect.Top - P(2))
        {
            foreach (var m in _tl.Music)
            {
                var r = MusicRectOf(m);
                if (m.Id == _selMusic || m.Id == _hoverId)
                {
                    var (fi, fo) = FadeHandles(r, m.FadeIn, m.FadeOut);
                    if (Near(p, fi, P(7))) return (Hit.MusicFadeIn, m.Id);
                    if (Near(p, fo, P(7))) return (Hit.MusicFadeOut, m.Id);
                }
            }
            // Two sounds can touch: the one the pointer is inside comes first, then edges just outside a sound.
            foreach (var m in _tl.Music)
            {
                var r = MusicRectOf(m);
                if (p.X < r.Left || p.X >= r.Right) continue;
                if (p.X - r.Left <= P(5) && r.Width > P(14)) return (Hit.MusicStart, m.Id);
                if (r.Right - p.X <= P(5)) return (Hit.MusicEnd, m.Id);
                return (Hit.Music, m.Id);
            }
            foreach (var m in _tl.Music)
            {
                var r = MusicRectOf(m);
                if (p.X >= r.Left - P(4) && p.X < r.Left && r.Width > P(14)) return (Hit.MusicStart, m.Id);
                if (p.X >= r.Right && p.X <= r.Right + P(4)) return (Hit.MusicEnd, m.Id);
            }
            return (Hit.MusicLane, -1);
        }
        return (Hit.Track, -1);
    }

    static bool IsTimelineHit(Hit h) => h is Hit.Ruler or Hit.Track or Hit.Clip or Hit.ClipFadeIn or Hit.ClipFadeOut or Hit.Junction
        or Hit.Music or Hit.MusicStart or Hit.MusicEnd or Hit.MusicFadeIn or Hit.MusicFadeOut or Hit.MusicLane
        or Hit.TrimStart or Hit.TrimEnd or Hit.TrimGrip;

    // ───────────────────────── drawing ─────────────────────────

    void DrawTimeline(Graphics g)
    {
        if (_info == null) return;
        DrawRuler(g);
        var tr = Rectangle.FromLTRB(TrackLeft, VideoRect.Top, TrackLeft + TrackWidth, MusicRect.Bottom);
        UpdateTracks(tr);
        if (_tracks != null) DrawFaded(g, _tracks, tr);
        if (Trimming) DrawTrim(g); else DrawClipOverlays(g);

        // Playhead
        {
            float x = X(_pos), top = RulerRect.Top, bottom = MusicRect.Bottom + P(3);
            using (var shadow = new Pen(C(90, 0, 0, 0), P(4))) g.DrawLine(shadow, x, top, x, bottom);
            using (var pen = new Pen(W(255), P(2))) g.DrawLine(pen, x, top, x, bottom);
            using var knob = new SolidBrush(W(255));
            g.FillPolygon(knob, new[] { new PointF(x - P(6), top), new PointF(x + P(6), top), new PointF(x, top + P(8)) });
        }

        if (_drag == Drag.Seek)
        {
            string label = Fmt(_pos);
            var size = g.MeasureString(label, _timeFont);
            var r = new RectangleF(X(_pos) - size.Width / 2 - P(9), RulerRect.Top - P(10) - size.Height - P(6), size.Width + P(18), size.Height + P(6));
            r.X = Math.Clamp(r.X, PanelRect.Left + P(6), PanelRect.Right - P(6) - r.Width);
            using (var path = RoundRect(r, r.Height / 2))
            using (var b = new SolidBrush(C(235, 245, 246, 250))) g.FillPath(b, path);
            using var tb = new SolidBrush(C(255, 20, 20, 24));
            g.DrawString(label, _timeFont, tb, r.X + P(9), r.Y + P(3));
        }
    }

    /// <summary>Fades, the selection, handles, transition dots, the audio track's hint, and a clip being dragged.</summary>
    void DrawClipOverlays(Graphics g)
    {
        bool movingClip = _drag == Drag.ClipMove && _moved, movingMusic = _drag == Drag.MusicMove && _moved;

        // Fades, selection, handles
        var parts = PlayedEdit().Parts;
        for (int i = 0; i < _tl.Clips.Count; i++)
        {
            var c = _tl.Clips[i];
            var r = ClipRect(c);
            bool xin = i < parts.Count && parts[i].HandleIn > 0, xout = i < parts.Count && parts[i].HandleOut > 0;
            DrawFadeShades(g, r, xin ? 0 : c.FadeIn, xout ? 0 : c.FadeOut, Rectangle.FromLTRB((int)r.Left, VideoRect.Top, (int)r.Right, WaveRect.Bottom));
            if (c.Id == _selClip) Outline(g, RectangleF.FromLTRB(r.Left, VideoRect.Top, r.Right, WaveRect.Bottom), Theme.AccentHover);
            if (c.Id == _selClip || c.Id == _hoverId) DrawFadeHandles(g, r, c.FadeIn, c.FadeOut, c.Id, Hit.ClipFadeIn, Hit.ClipFadeOut);
        }
        foreach (var m in _tl.Music)
        {
            var r = MusicRectOf(m);
            DrawFadeShades(g, r, m.FadeIn, m.FadeOut, Rectangle.Round(r));
            if (m.Id == _selMusic) Outline(g, r, AudioColor);
            if (m.Id == _selMusic || m.Id == _hoverId) DrawFadeHandles(g, r, m.FadeIn, m.FadeOut, m.Id, Hit.MusicFadeIn, Hit.MusicFadeOut);
        }

        // Transition dots where clips meet
        foreach (var (a, _, at) in Junctions())
        {
            bool hot = _hover == Hit.Junction && _hoverId == a.Id;
            float rr = P(8);
            var box = new RectangleF(at.X - rr, at.Y - rr, 2 * rr, 2 * rr);
            using (var fill = new SolidBrush(C(245, hot ? Theme.BtnHover : Theme.Bg))) g.FillEllipse(fill, box);
            using (var ring = new Pen(C(255, Theme.VioletHover), P(hot ? 2.2f : 1.5f))) g.DrawEllipse(ring, box);
            TransitionGlyph(g, at, rr * 0.5f, a.Next.Kind, W(235));
        }

        if (_tl.Music.Count == 0)
            using (var hint = new SolidBrush(W(120)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString("Drop WAV or MP3 files here to add sound", _smallFont, hint, MusicRect, sf);

        // A clip being dragged: its old place dimmed, the clip at the new place, and where it would go in
        var clipState = g.Save();
        g.SetClip(Rectangle.Inflate(TimelineRect, 0, P(4)), CombineMode.Intersect);
        if (movingClip && _tl.Find(_dragId) is Clip dc)
        {
            var from = Rectangle.FromLTRB((int)X(dc.Start), VideoRect.Top, (int)X(dc.End), WaveRect.Bottom);
            using (var dim = new SolidBrush(C(150, 0, 0, 0))) g.FillRectangle(dim, from);
            var to = new RectangleF(X(_ghost), VideoRect.Top - P(3), from.Width, from.Height);
            if (_tracks != null)
            {
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.85f * Alpha });
                g.DrawImage(_tracks, Rectangle.Round(to), from.Left - TrackLeft, from.Top - VideoRect.Top, from.Width, from.Height, GraphicsUnit.Pixel, ia);
            }
            Outline(g, to, Color.White);
            if (InsertPoint(dc) is double ins)
            {
                using var pen = new Pen(C(255, Theme.AccentHover), P(3));
                g.DrawLine(pen, X(ins), VideoRect.Top - P(4), X(ins), WaveRect.Bottom + P(4));
            }
        }
        if (movingMusic && _tl.FindMusic(_dragId) is AudioClip dm)
        {
            var from = Rectangle.Round(MusicRectOf(dm));
            using (var dim = new SolidBrush(C(150, 0, 0, 0))) g.FillRectangle(dim, from);
            var to = new RectangleF(X(_ghost), MusicRect.Top - P(3), from.Width, from.Height);
            if (_tracks != null)
            {
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = 0.85f * Alpha });
                g.DrawImage(_tracks, Rectangle.Round(to), from.Left - TrackLeft, from.Top - VideoRect.Top, from.Width, from.Height, GraphicsUnit.Pixel, ia);
            }
            Outline(g, to, Color.White);
        }
        g.Restore(clipState);
    }

    void Outline(Graphics g, RectangleF r, Color c)
    {
        using var path = RoundRect(r, P(5));
        using var pen = new Pen(C(255, c), P(2));
        g.DrawPath(pen, path);
    }

    /// <summary>A fade's darkening: a wedge from the clip's corner, as editors draw it.</summary>
    EditList? _played;
    int _playedVersion = -1;

    /// <summary>The timeline as it plays (cached until it changes).</summary>
    EditList PlayedEdit()
    {
        if (_played == null || _playedVersion != _tlVersion) { _played = EditList.Of(_tl); _playedVersion = _tlVersion; }
        return _played;
    }

    void DrawFadeShades(Graphics g, RectangleF r, double fadeIn, double fadeOut, Rectangle area)
    {
        using var shade = new SolidBrush(C(150, 0, 0, 0));
        using var line = new Pen(W(200), P(1.2f));
        if (fadeIn > 0)
        {
            float w = Math.Min(r.Width, (float)(fadeIn / _viewLen * TrackWidth));
            g.FillPolygon(shade, new[] { new PointF(r.Left, area.Top), new PointF(r.Left + w, area.Top), new PointF(r.Left, area.Bottom) });
            g.DrawLine(line, r.Left, area.Bottom, r.Left + w, area.Top);
        }
        if (fadeOut > 0)
        {
            float w = Math.Min(r.Width, (float)(fadeOut / _viewLen * TrackWidth));
            g.FillPolygon(shade, new[] { new PointF(r.Right, area.Top), new PointF(r.Right - w, area.Top), new PointF(r.Right, area.Bottom) });
            g.DrawLine(line, r.Right, area.Bottom, r.Right - w, area.Top);
        }
    }

    void DrawFadeHandles(Graphics g, RectangleF r, double fadeIn, double fadeOut, int id, Hit inHit, Hit outHit)
    {
        var (fi, fo) = FadeHandles(r, fadeIn, fadeOut);
        foreach (var (p, h) in new[] { (fi, inHit), (fo, outHit) })
        {
            bool hot = _hover == h && _hoverId == id || _drag == (h is Hit.ClipFadeIn or Hit.MusicFadeIn ? Drag.FadeIn : Drag.FadeOut) && _dragId == id;
            float rr = P(hot ? 5.5f : 4.5f);
            using var b = new SolidBrush(W(255));
            using var pen = new Pen(C(200, 0, 0, 0), P(1.2f));
            g.FillEllipse(b, p.X - rr, p.Y - rr, 2 * rr, 2 * rr);
            g.DrawEllipse(pen, p.X - rr, p.Y - rr, 2 * rr, 2 * rr);
        }
    }

    void DrawRuler(Graphics g)
    {
        var rr = RulerRect;
        double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600 };
        double step = steps.FirstOrDefault(s => _viewLen / s <= 10, 3600);
        using var tick = new Pen(W(90), 1);
        using var minor = new Pen(W(45), 1);
        using var text = new SolidBrush(W(140));
        for (double t = 0; t <= _viewLen; t += step / 2)
        {
            float x = X(t);
            bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            g.DrawLine(major ? tick : minor, x, rr.Bottom - (major ? P(6) : P(3)), x, rr.Bottom);
            if (major && x + P(30) < rr.Right)
            {
                long s = (long)Math.Round(t);
                g.DrawString(s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}", _smallFont, text, x + P(2), rr.Top - P(4));
            }
        }
        // The end of the timeline
        using var end = new Pen(C(140, Theme.AccentHover), 1);
        g.DrawLine(end, X(_tl.End), rr.Top, X(_tl.End), MusicRect.Bottom);
    }

    /// <summary>The tracks' pictures and waveforms, redrawn only when the timeline, its scale or what's decoded changes.</summary>
    void UpdateTracks(Rectangle tr)
    {
        if (_info == null || tr.Width <= 0) return;
        var key = (_tlVersion, _stripVersion, _peaksVersion, EffectiveRotation, tr.Width, tr.Height, _viewLen);
        if (_tracks != null && key == _tracksKey) return;
        _tracksKey = key;
        if (_tracks == null || _tracks.Width != tr.Width || _tracks.Height != tr.Height)
        {
            _tracks?.Dispose();
            _tracks = new Bitmap(tr.Width, tr.Height, PixelFormat.Format32bppPArgb);
        }
        using var g = Graphics.FromImage(_tracks);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(-tr.Left, -tr.Top);

        // Lanes
        using (var lane = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
        {
            using (var p = RoundRect(Rectangle.FromLTRB(TrackLeft, VideoRect.Top, TrackLeft + TrackWidth, WaveRect.Bottom), P(5))) g.FillPath(lane, p);
            using (var p = RoundRect(MusicRect, P(5))) g.FillPath(lane, p);
        }

        var (dw, dh) = _info.DisplaySize(_rot);
        float tileW = Math.Max(P(14), VideoRect.Height * (float)dw / Math.Max(1, dh));
        int rot = EffectiveRotation;
        foreach (var c in _tl.Clips)
        {
            var r = ClipRect(c);
            var whole = RectangleF.FromLTRB(r.Left, VideoRect.Top, r.Right, WaveRect.Bottom);
            using var path = RoundRect(whole, P(5));
            var st = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using (var bg = new SolidBrush(Color.FromArgb(255, 30, 32, 38))) g.FillRectangle(bg, whole);
            // Pictures: tiles from the clip's own stretch of the video
            if (_strip != null)
                for (float x = r.Left; x < r.Right; x += tileW)
                {
                    double t = c.In + PxToTime(x + tileW / 2 - r.Left);
                    var thumb = _strip.Nearest((int)(Math.Clamp(t / Math.Max(Duration, 1e-6), 0, 1) * ThumbnailStrip.Count));
                    if (thumb != null) DrawRotated(g, thumb, new RectangleF(x, VideoRect.Top, tileW, VideoRect.Height), rot);
                }
            // Its sound
            using (var wbg = new SolidBrush(Color.FromArgb(255, 16, 20, 40))) g.FillRectangle(wbg, RectangleF.FromLTRB(r.Left, WaveRect.Top, r.Right, WaveRect.Bottom));
            if (_videoPeaks != null) DrawWave(g, _videoPeaks, c.In, r.Left, r.Right, WaveRect, VideoWave);
            g.Restore(st);
            using var border = new Pen(Color.FromArgb(150, 0, 0, 0), 1);
            g.DrawPath(border, path);
        }

        using var nameFont = new Font("Segoe UI Semibold", 8f);
        foreach (var m in _tl.Music)
        {
            var r = MusicRectOf(m);
            using var path = RoundRect(r, P(5));
            var st = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            using (var bg = new SolidBrush(Color.FromArgb(255, 18, 62, 58))) g.FillRectangle(bg, r);
            if (_musicPeaks.TryGetValue(m.Path, out var pk)) DrawWave(g, pk, m.In, r.Left, r.Right, Rectangle.Round(r), AudioColor);
            using (var tb = new SolidBrush(Color.FromArgb(230, 255, 255, 255))) g.DrawString(m.Name, nameFont, tb, r.Left + P(5), r.Top + P(1));
            g.Restore(st);
            using var border = new Pen(Color.FromArgb(160, AudioColor), 1);
            g.DrawPath(border, path);
        }
    }

    /// <summary>
    /// A waveform: for each pixel column, the loudest sound in its stretch, centred. Drawn on a square-root scale, as
    /// editors do, so quiet sound still shows.
    /// </summary>
    void DrawWave(Graphics g, Peaks peaks, double sourceAt, float left, float right, Rectangle band, Color color)
    {
        double perPx = _viewLen / TrackWidth;
        float mid = band.Top + band.Height / 2f, half = band.Height / 2f - 1;
        using var pen = new Pen(Color.FromArgb(220, color), 1);
        for (float x = left; x < right; x += 1)
        {
            double t = sourceAt + (x - left) * perPx;
            float a = MathF.Sqrt(peaks.Max(t, t + perPx)) * half;
            if (a >= 0.5f) g.DrawLine(pen, x, mid - a, x, mid + a);
        }
    }

    /// <summary>A transition's kind: a straight cut (|), a crossfade (×) or a dip through black (∨).</summary>
    void TransitionGlyph(Graphics g, PointF c, float s, TransitionKind kind, Color col)
    {
        using var pen = GlyphPen(col, 1.7f);
        switch (kind)
        {
            case TransitionKind.Crossfade:
                g.DrawLine(pen, c.X - s, c.Y - s * 0.75f, c.X + s, c.Y + s * 0.75f);
                g.DrawLine(pen, c.X - s, c.Y + s * 0.75f, c.X + s, c.Y - s * 0.75f);
                break;
            case TransitionKind.FadeThroughBlack:
                g.DrawLines(pen, new[] { new PointF(c.X - s, c.Y - s * 0.7f), new PointF(c.X, c.Y + s * 0.7f), new PointF(c.X + s, c.Y - s * 0.7f) });
                break;
            default:
                g.DrawLine(pen, c.X, c.Y - s * 0.85f, c.X, c.Y + s * 0.85f);
                break;
        }
    }

    /// <summary>Undo button: an arrow turning back to the left (redo: the mirror image, to the right).</summary>
    void UndoGlyph(Graphics g, RectangleF r, Color c, bool redo)
    {
        using var pen = GlyphPen(c, 1.7f);
        float w = r.Width, h = r.Height, top = r.Y + h * 0.28f, rad = h * 0.3f;
        float X(float f) => redo ? r.Right - f * w : r.X + f * w;
        using var path = new GraphicsPath();
        path.AddLine(X(0.06f), top, X(0.58f), top);
        path.AddArc(X(0.58f) - rad, top, 2 * rad, 2 * rad, -90, redo ? -180 : 180);
        path.AddLine(X(0.58f), top + 2 * rad, X(0.3f), top + 2 * rad);
        g.DrawPath(pen, path);
        g.DrawLine(pen, X(0.06f), top, X(0.27f), top - h * 0.21f);
        g.DrawLine(pen, X(0.06f), top, X(0.27f), top + h * 0.21f);
    }

    /// <summary>Split button: scissors.</summary>
    void ScissorsGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.7f);
        float rr = r.Width * 0.15f, cy = r.Bottom - rr - r.Height * 0.02f;
        float lx = r.X + r.Width * 0.27f, rx = r.Right - r.Width * 0.27f;
        g.DrawEllipse(pen, lx - rr, cy - rr, 2 * rr, 2 * rr);
        g.DrawEllipse(pen, rx - rr, cy - rr, 2 * rr, 2 * rr);
        g.DrawLine(pen, lx + rr * 0.7f, cy - rr * 0.7f, r.Right - r.Width * 0.14f, r.Y + r.Height * 0.04f);
        g.DrawLine(pen, rx - rr * 0.7f, cy - rr * 0.7f, r.X + r.Width * 0.14f, r.Y + r.Height * 0.04f);
    }

    /// <summary>Delete button: a bin.</summary>
    void TrashGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.7f);
        float l = r.X + r.Width * 0.18f, rt = r.Right - r.Width * 0.18f, top = r.Y + r.Height * 0.2f;
        g.DrawLine(pen, l - r.Width * 0.06f, top, rt + r.Width * 0.06f, top);
        g.DrawLine(pen, r.X + r.Width * 0.4f, top, r.X + r.Width * 0.42f, r.Y + r.Height * 0.06f);
        g.DrawLine(pen, r.X + r.Width * 0.42f, r.Y + r.Height * 0.06f, r.X + r.Width * 0.58f, r.Y + r.Height * 0.06f);
        g.DrawLine(pen, r.X + r.Width * 0.58f, r.Y + r.Height * 0.06f, r.X + r.Width * 0.6f, top);
        g.DrawLines(pen, new[] { new PointF(l + r.Width * 0.04f, top), new PointF(l + r.Width * 0.1f, r.Bottom - r.Height * 0.04f),
            new PointF(rt - r.Width * 0.1f, r.Bottom - r.Height * 0.04f), new PointF(rt - r.Width * 0.04f, top) });
    }

    /// <summary>Add-sound button: a note with a plus.</summary>
    void AddSoundGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.7f);
        using var b = new SolidBrush(c);
        float hx = r.X + r.Width * 0.3f, hy = r.Bottom - r.Height * 0.2f, hr = r.Width * 0.14f;
        g.FillEllipse(b, hx - hr, hy - hr * 0.8f, 2 * hr, 1.6f * hr);
        float sx = hx + hr * 0.9f;
        g.DrawLine(pen, sx, hy, sx, r.Y + r.Height * 0.08f);
        g.DrawLine(pen, sx, r.Y + r.Height * 0.08f, sx + r.Width * 0.18f, r.Y + r.Height * 0.2f);
        float px = r.Right - r.Width * 0.16f, py = r.Y + r.Height * 0.62f, ps = r.Width * 0.15f;
        g.DrawLine(pen, px - ps, py, px + ps, py);
        g.DrawLine(pen, px, py - ps, px, py + ps);
    }

    // ───────────────────────── editing ─────────────────────────

    /// <summary>Makes <paramref name="next"/> the timeline, undoably.</summary>
    void Edit(Timeline next, string? toast = null)
    {
        if (ReferenceEquals(next, _tl)) return;
        if (Trimming) { ShowToast("Apply or cancel the trim first"); return; }
        if (next.SameAs(_tl)) { _tlVersion++; UpdateView(); Invalidate(); return; }
        _undo.Push(_tl);
        _redo.Clear();
        _tl = next;
        TimelineChanged();
        if (toast != null) ShowToast(toast);
    }

    void Undo()
    {
        if (_info == null || _drag != Drag.None) return;
        if (Trimming) { ShowToast("Apply or cancel the trim first"); return; }
        if (_undo.Count == 0) { ShowToast("Nothing to undo"); return; }
        _redo.Push(_tl);
        _tl = _undo.Pop();
        TimelineChanged();
        ShowToast("Undone");
    }

    void Redo()
    {
        if (_info == null || _drag != Drag.None) return;
        if (Trimming) { ShowToast("Apply or cancel the trim first"); return; }
        if (_redo.Count == 0) { ShowToast("Nothing to redo"); return; }
        _undo.Push(_tl);
        _tl = _redo.Pop();
        TimelineChanged();
        ShowToast("Redone");
    }

    /// <summary>After any change to the timeline: rescale it, keep the selection valid, and show / play what's there now.</summary>
    void TimelineChanged()
    {
        _tlVersion++;
        if (_tl.Find(_selClip) == null) _selClip = -1;
        if (_tl.FindMusic(_selMusic) == null) _selMusic = -1;
        UpdateView();
        _pos = Math.Clamp(_pos, 0, Math.Max(0, _tl.End - FrameDur / 2));
        if (_playing && _pb != null) _pb.Play(_pos, EditList.Of(_tl), _loop);
        else ShowFrameAt(_pos);
        Invalidate();
    }

    /// <summary>S / the split button: splits the clip under the playhead there.</summary>
    void SplitHere()
    {
        if (_info == null || _drag != Drag.None) return;
        if (Trimming) { ShowToast("Apply or cancel the trim first"); return; }
        if (_playing) Pause();
        var next = _tl.Split(_pos, out int second);
        if (second < 0) { ShowToast(_tl.At(_pos) == null ? "No clip under the playhead" : "Already a cut here"); return; }
        _selClip = second;
        _selMusic = -1;
        Edit(next, "Split");
    }

    /// <summary>Delete: removes the selected clip, leaving a gap (or, with <paramref name="close"/>, closing it).</summary>
    void DeleteSelected(bool close)
    {
        if (_drag != Drag.None) return;
        if (_selClip >= 0) Edit(close ? _tl.RippleDelete(_selClip) : _tl.Delete(_selClip), close ? "Deleted; gap closed" : "Deleted");
        else if (_selMusic >= 0) Edit(_tl.DeleteMusic(_selMusic), "Sound deleted");
        else ShowToast("Select a clip first");
    }

    void CloseGaps()
    {
        if (_drag == Drag.None) Edit(_tl.CloseGaps(), "Gaps closed");
    }

    void AddAudioDialog()
    {
        if (_info == null || _drag != Drag.None) return;
        if (Trimming) { ShowToast("Apply or cancel the trim first"); return; }
        RevealCursor();
        using var dlg = new OpenFileDialog { Filter = SoundFilter, Title = "Add sound", Multiselect = true };
        if (dlg.ShowDialog(this) == DialogResult.OK) AddAudio(dlg.FileNames, _pos);
    }

    /// <summary>Puts sound files on the audio track from <paramref name="at"/> on, one after another.</summary>
    void AddAudio(IEnumerable<string> paths, double at)
    {
        if (_info == null) { ShowToast("Open a video first"); return; }
        if (_drag != Drag.None) return;
        if (Trimming) { ShowToast("Apply or cancel the trim first"); return; }
        var next = _tl;
        int added = 0, last = -1;
        foreach (var path in paths)
        {
            double length;
            try
            {
                using var probe = new MediaSource(path, decoders: false, video: false);
                if (!probe.Info.HasAudio || probe.Info.Duration <= 0) { ShowToast($"No sound in {Path.GetFileName(path)}"); continue; }
                length = probe.Info.Duration;
            }
            catch (Exception ex)
            {
                CrashLog.Write(ex, $"Adding sound {path}");
                ShowToast($"Couldn't read {Path.GetFileName(path)}");
                continue;
            }
            next = next.AddMusic(path, length, at, out last);
            at = next.FindMusic(last)!.End;
            if (!_musicPeaks.ContainsKey(path))
            {
                var pk = new Peaks(path, length, this);
                pk.Updated += () => { _peaksVersion++; if (PanelAlpha > 0) Invalidate(PanelRect); };
                _musicPeaks[path] = pk;
            }
            added++;
        }
        if (added == 0) return;
        _selMusic = last;
        _selClip = -1;
        Edit(next, added == 1 ? "Sound added" : $"{added} sounds added");
    }

    void SetClipFades(int id, double? fadeIn, double? fadeOut) => Edit(_tl.SetFades(id, fadeIn, fadeOut));
    void SetMusicFades(int id, double? fadeIn, double? fadeOut) => Edit(_tl.SetMusicFades(id, fadeIn, fadeOut));

    void SetTransition(int id, Transition t)
    {
        _nextTransition = t;
        Edit(_tl.SetTransition(id, t));
    }

    /// <summary>Where a clip dragged to <see cref="_ghost"/> would be inserted (null where it simply fits).</summary>
    double? InsertPoint(Clip c)
    {
        var others = _tl.Clips.Where(x => x.Id != c.Id).ToList();
        double end = _ghost + c.Length;
        if (!others.Any(o => o.Start < end - FrameDur / 2 && _ghost < o.End - FrameDur / 2)) return null;
        double pointer = TimeAt(PointToClient(MousePosition).X);
        var target = others.FirstOrDefault(o => pointer >= o.Start && pointer < o.End)
                     ?? others.Where(o => o.Start < end && _ghost < o.End).MinBy(o => Math.Abs((o.Start + o.End) / 2 - pointer));
        if (target == null) return null;
        return pointer < (target.Start + target.End) / 2 ? target.Start : target.End;
    }

    /// <summary>A dragged clip's start, pulled onto nearby edges (other clips, the playhead, 0) within a few pixels.</summary>
    double SnapMove(double start, double length, int self, bool music)
    {
        var edges = new List<double> { 0, _pos };
        if (music) foreach (var m in _tl.Music) { if (m.Id != self) { edges.Add(m.Start); edges.Add(m.End); } }
        else foreach (var c in _tl.Clips) { if (c.Id != self) { edges.Add(c.Start); edges.Add(c.End); } }
        foreach (var c in _tl.Clips) if (music) { edges.Add(c.Start); edges.Add(c.End); }
        double best = start, bestPx = P(8);
        foreach (var e in edges)
        {
            foreach (double s in new[] { e, e - length })
            {
                double px = Math.Abs(X(s) - X(start));
                if (px < bestPx) { bestPx = px; best = s; }
            }
        }
        return Math.Max(0, _tl.Snap(best));
    }

    // ───────────────────────── pointer ─────────────────────────

    /// <summary>A press on the timeline: start scrubbing, a clip drag, a trim or a fade drag.</summary>
    void TimelineDown(Hit h, MouseEventArgs e)
    {
        Pause();
        _downX = _dragX = e.X;
        _moved = false;
        _dragId = _hitId;
        _dragBase = _tl;
        switch (h)
        {
            case Hit.Ruler or Hit.Track or Hit.MusicLane:
                if (h != Hit.Ruler && !Trimming) { _selClip = _selMusic = -1; }
                _drag = Drag.Seek;
                ScrubTo(e.X, exact: false);
                break;
            case Hit.Clip:
                _selClip = _hitId;
                _selMusic = -1;
                _grab = TimeAt(e.X) - _tl.Find(_hitId)!.Start;
                _ghost = _tl.Find(_hitId)!.Start;
                _drag = Drag.ClipMove;
                break;
            case Hit.Music:
                _selMusic = _hitId;
                _selClip = -1;
                _grab = TimeAt(e.X) - _tl.FindMusic(_hitId)!.Start;
                _ghost = _tl.FindMusic(_hitId)!.Start;
                _drag = Drag.MusicMove;
                break;
            case Hit.MusicStart or Hit.MusicEnd:
                _selMusic = _hitId;
                _selClip = -1;
                _drag = h == Hit.MusicStart ? Drag.MusicStart : Drag.MusicEnd;
                break;
            case Hit.ClipFadeIn or Hit.MusicFadeIn:
                _drag = Drag.FadeIn;
                break;
            case Hit.ClipFadeOut or Hit.MusicFadeOut:
                _drag = Drag.FadeOut;
                break;
            case Hit.TrimStart:
                _trimGrab = TimeAt(e.X) - _trimIn;
                _drag = Drag.TrimStart;
                break;
            case Hit.TrimEnd:
                _trimGrab = TimeAt(e.X) - _trimOut;
                _drag = Drag.TrimEnd;
                break;
            case Hit.TrimGrip:
                _trimGrab = TimeAt(e.X) - _trimIn;
                _drag = Drag.TrimMove;
                break;
        }
        _dragMusic = h is Hit.Music or Hit.MusicStart or Hit.MusicEnd or Hit.MusicFadeIn or Hit.MusicFadeOut;
        Capture = true;
        Invalidate(PanelRect);
    }

    bool _dragMusic;

    void ScrubTo(int x, bool exact)
    {
        _pos = Math.Clamp(_tl.Snap(TimeAt(x)), 0, Math.Max(0, PosEnd - FrameDur / 2));
        ShowFrameAt(_pos, exact);
    }

    /// <summary>The pointer moved during a timeline drag.</summary>
    void TimelineDrag(int x)
    {
        _dragX = x;
        if (!_moved && Math.Abs(x - _downX) < SystemInformation.DragSize.Width && _drag is Drag.ClipMove or Drag.MusicMove or Drag.TrimStart or Drag.TrimEnd or Drag.TrimMove) return;
        _moved = true;
        var b = _dragBase ?? _tl;
        // Moves, and a sound's end, may go past the end of the timeline: the pointer isn't held to the track there.
        double t = _drag is Drag.ClipMove or Drag.MusicMove or Drag.MusicEnd ? Math.Max(0, PxToTime(x - TrackLeft)) : TimeAt(x);
        switch (_drag)
        {
            case Drag.Seek:
                ScrubTo(x, exact: false);
                break;
            case Drag.ClipMove when b.Find(_dragId) is Clip c:
                _ghost = SnapMove(Math.Max(0, t - _grab), c.Length, c.Id, music: false);
                break;
            case Drag.MusicMove when b.FindMusic(_dragId) is AudioClip m:
                _ghost = SnapMove(Math.Max(0, t - _grab), m.Length, m.Id, music: true);
                break;
            case Drag.TrimStart or Drag.TrimEnd or Drag.TrimMove:
                DragTrim(t);
                break;
            case Drag.MusicStart:
                _tl = b.TrimMusicStart(_dragId, t);
                _tlVersion++;
                break;
            case Drag.MusicEnd:
                _tl = b.TrimMusicEnd(_dragId, t);
                _tlVersion++;
                break;
            case Drag.FadeIn or Drag.FadeOut:
            {
                bool fadeIn = _drag == Drag.FadeIn;
                if (_dragMusic && b.FindMusic(_dragId) is AudioClip m2)
                    _tl = fadeIn ? b.SetMusicFades(_dragId, Math.Max(0, t - m2.Start), null) : b.SetMusicFades(_dragId, null, Math.Max(0, m2.End - t));
                else if (b.Find(_dragId) is Clip c2)
                    _tl = fadeIn ? b.SetFades(_dragId, Math.Max(0, t - c2.Start), null) : b.SetFades(_dragId, null, Math.Max(0, c2.End - t));
                _tlVersion++;
                break;
            }
        }
        Invalidate(PanelRect);
    }

    /// <summary>The end of a timeline drag: commit the move / trim / fade as one undo step, or treat it as a click.</summary>
    void TimelineUp(int x)
    {
        var d = _drag;
        var b = _dragBase ?? _tl;
        _drag = Drag.None;
        Capture = false;
        _dragBase = null;
        switch (d)
        {
            case Drag.Seek:
                ShowFrameAt(_pos, exact: true);
                break;
            case Drag.TrimMove when !_moved:
                ScrubTo(x, exact: true);
                break;
            case Drag.TrimStart or Drag.TrimEnd or Drag.TrimMove:
                ShowFrameAt(_pos, exact: true);
                break;
            case Drag.ClipMove or Drag.MusicMove when !_moved:
                ScrubTo(x, exact: true); // a click on a clip: select it and move the playhead there
                break;
            case Drag.ClipMove:
                _tl = b;
                Edit(b.Move(_dragId, _ghost, TimeAt(x)));
                break;
            case Drag.MusicMove:
                _tl = b;
                Edit(b.MoveMusic(_dragId, _ghost));
                break;
            default:
                // Trims and fades were made live; one undo step back to where the drag began.
                if (!ReferenceEquals(_tl, b))
                {
                    var after = _tl;
                    _tl = b;
                    Edit(after);
                }
                else UpdateView();
                break;
        }
        _dragId = -1;
        UpdateView();
        Invalidate();
    }

    // ───────────────────────── menus ─────────────────────────

    void BuildClipMenu()
    {
        _clipMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
        _clipMenu.ShowImageMargin = false;
        _clipMenu.ForeColor = Color.Gainsboro;
        _clipMenu.Font = new Font("Segoe UI", 9.5f);
        _clipMenu.Closed += (_, _) => { Activity(); Invalidate(PanelRect); };
    }

    static string Check(bool on, string text) => (on ? "✓  " : "     ") + text;

    ToolStripMenuItem MenuItem(string text, Action a, bool enabled = true, string? keys = null) =>
        new(text, null, (_, _) => a()) { Enabled = enabled, ShortcutKeyDisplayString = keys };

    /// <summary>A submenu of fade lengths ("Off" first), the current one ticked.</summary>
    ToolStripMenuItem FadeMenu(string text, double current, IEnumerable<double> lengths, Action<double> set)
    {
        var item = new ToolStripMenuItem(text);
        item.DropDown.Renderer = _clipMenu.Renderer;
        item.DropDown.BackColor = Color.FromArgb(16, 21, 46);
        item.DropDownItems.Add(MenuItem(Check(current <= 0, "Off"), () => set(0)));
        foreach (double len in lengths)
            item.DropDownItems.Add(MenuItem(Check(Math.Abs(current - len) < 1e-6, $"{len:0.##} s"), () => set(len)));
        foreach (ToolStripItem i in item.DropDownItems) i.ForeColor = Color.Gainsboro;
        return item;
    }

    static readonly double[] VideoFades = { 0.25, 0.5, 1, 2, 3 }, SoundFades = { 0.5, 1, 2, 3, 5 };

    void ShowClipMenu(int id, Point at)
    {
        var c = _tl.Find(id);
        if (c == null) return;
        _selClip = id;
        _selMusic = -1;
        _clipMenu.Items.Clear();
        _clipMenu.Items.Add(new ToolStripMenuItem($"Clip {Fmt(c.In)} – {Fmt(c.Out)}  ({Length(c.Length)})") { Enabled = false });
        _clipMenu.Items.Add(new ToolStripSeparator());
        _clipMenu.Items.Add(MenuItem("Split at the playhead", SplitHere, _tl.At(_pos)?.Id == id, "S"));
        _clipMenu.Items.Add(MenuItem("Trim…", () => StartTrim(id), true, "T"));
        _clipMenu.Items.Add(FadeMenu("Fade in", c.FadeIn, VideoFades, v => SetClipFades(id, v, null)));
        _clipMenu.Items.Add(FadeMenu("Fade out", c.FadeOut, VideoFades, v => SetClipFades(id, null, v)));
        _clipMenu.Items.Add(new ToolStripSeparator());
        _clipMenu.Items.Add(MenuItem("Delete", () => DeleteSelected(close: false), true, "Delete"));
        _clipMenu.Items.Add(MenuItem("Delete and close the gap", () => DeleteSelected(close: true), true, "Shift+Delete"));
        Invalidate(PanelRect);
        _clipMenu.Show(this, at);
    }

    void ShowMusicMenu(int id, Point at)
    {
        var m = _tl.FindMusic(id);
        if (m == null) return;
        _selMusic = id;
        _selClip = -1;
        _clipMenu.Items.Clear();
        _clipMenu.Items.Add(new ToolStripMenuItem($"{m.Name}  ({Length(m.Length)})") { Enabled = false });
        _clipMenu.Items.Add(new ToolStripSeparator());
        _clipMenu.Items.Add(FadeMenu("Fade in", m.FadeIn, SoundFades, v => SetMusicFades(id, v, null)));
        _clipMenu.Items.Add(FadeMenu("Fade out", m.FadeOut, SoundFades, v => SetMusicFades(id, null, v)));
        _clipMenu.Items.Add(new ToolStripSeparator());
        _clipMenu.Items.Add(MenuItem("Delete", () => DeleteSelected(close: false), true, "Delete"));
        Invalidate(PanelRect);
        _clipMenu.Show(this, at);
    }

    void ShowTransitionMenu(int id, Point at)
    {
        var a = _tl.Find(id);
        if (a == null) return;
        var t = a.Next;
        _clipMenu.Items.Clear();
        _clipMenu.Items.Add(new ToolStripMenuItem("TRANSITION") { Enabled = false });
        foreach (var k in new[] { TransitionKind.None, TransitionKind.Crossfade, TransitionKind.FadeThroughBlack })
            _clipMenu.Items.Add(MenuItem(Check(t.Kind == k, Transition.Name(k)), () => SetTransition(id, t with { Kind = k })));
        _clipMenu.Items.Add(new ToolStripSeparator());
        _clipMenu.Items.Add(new ToolStripMenuItem("LENGTH") { Enabled = false });
        foreach (double len in Transition.Lengths)
            _clipMenu.Items.Add(MenuItem(Check(Math.Abs(t.Seconds - len) < 1e-6, $"{len:0.##} s"), () => SetTransition(id, t with { Seconds = len }),
                t.Kind != TransitionKind.None));
        _clipMenu.Items.Add(new ToolStripSeparator());
        _clipMenu.Items.Add(MenuItem("Use this transition for every cut", () => { _nextTransition = t; Edit(_tl.SetAllTransitions(t)); }, _tl.Clips.Count > 2));
        Invalidate(PanelRect);
        _clipMenu.Show(this, at);
    }

    // ───────────────────────── playing and exporting ─────────────────────────

    /// <summary>Shows the frame at timeline time <paramref name="t"/>: the clip's frame there, or black in a gap.</summary>
    void ShowFrameAt(double t, bool exact = true)
    {
        if (_pb == null) return;
        if (_tl.At(t) is Clip c)
        {
            double source = c.In + (t - c.Start);
            if (exact) _pb.ShowAt(source, c.Start - c.In);
            else _pb.ShowNear(source, c.Start - c.In);
        }
        else _pb.ShowBlack(t);
    }

    /// <summary>
    /// The export: one stretch of the file when the timeline is just that (one clip from 0, no fades, no sound added, so
    /// it can be copied without re-encoding or looped back and forth), otherwise the whole timeline.
    /// </summary>
    TrimJob CurrentJob(Timeline? timeline = null)
    {
        var tl = timeline ?? _tl;
        var crop = HasCrop ? _crop : (RectangleF?)null;
        if (tl.Clips.Count == 1 && tl.Music.Count == 0 && tl.Clips[0] is { Start: < 1e-6, FadeIn: <= 0, FadeOut: <= 0 } only)
            return new TrimJob(_info!, only.In, only.Out, _rot) { Crop = crop };
        return new TrimJob(_info!, 0, tl.End, _rot) { Crop = crop, Edit = EditList.Of(tl) };
    }
}
