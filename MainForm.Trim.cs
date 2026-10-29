namespace Splicia;

/// <summary>
/// Trim mode: the trim of earlier versions, for a clip of the timeline. The timeline shows the clip's whole video with
/// the part kept framed in yellow. Drag its handles (or press I / [ and O / ] at a frame) to set where it starts and
/// ends, or the grip under it to move the part kept along the video; Space plays just that part. Enter keeps it (the
/// clips after it close up; one undo step), Esc leaves the clip as it was. T, the trim button, double-clicking a clip
/// or its menu starts it.
/// </summary>
sealed partial class MainForm
{
    static readonly Color TrimColor = Color.FromArgb(255, 210, 60);

    int _trimClip = -1;        // the clip being trimmed, or -1
    double _trimIn, _trimOut;  // the part kept, in the file's time
    double _trimGrab;          // dragging a handle or the grip: pointer time − the edge it moves
    Timeline? _trimView;       // the clip's whole video as one clip, to draw

    bool Trimming => _trimClip >= 0;

    /// <summary>The playhead's range: the timeline, or in trim mode the clip's whole video (the file's time).</summary>
    double PosEnd => Trimming ? _tl.SourceEnd : _tl.End;

    /// <summary>What playing plays: the timeline, or in trim mode the part kept, which starts at <see cref="PlayBase"/>.</summary>
    EditList PlayList() => Trimming ? EditList.Single(_trimIn, _trimOut, declick: true) : EditList.Of(_tl);
    double PlayBase => Trimming ? _trimIn : 0;
    double PlayEnd => Trimming ? _trimOut : _tl.End;

    /// <summary>Trims clip <paramref name="id"/> (else the selected one, the one at the playhead, or the only one); again: keeps it.</summary>
    void StartTrim(int id = -1)
    {
        if (_info == null || _pb == null || _drag != Drag.None) return;
        if (Trimming) { FinishTrim(keep: true); return; }
        var c = _tl.Find(id) ?? _tl.Find(_selClip) ?? _tl.At(_pos) ?? (_tl.Clips.Count == 1 ? _tl.Clips[0] : null);
        if (c == null) { ShowToast("Select a clip to trim"); return; }
        if (_cropping) FinishCrop(apply: true);
        Pause();
        // The playhead onto the file's time: the same frame if it's in the clip, else the clip's start.
        double at = _pos >= c.Start && _pos < c.End ? c.In + (_pos - c.Start) : c.In;
        _trimClip = c.Id;
        _trimIn = c.In;
        _trimOut = c.Out;
        _trimView = new Timeline(new[] { new Clip(c.Id, 0, _tl.SourceEnd, 0, Transition.Cut) }, Array.Empty<AudioClip>(), _tl.Frame, _tl.SourceLength);
        _selClip = c.Id;
        _selMusic = -1;
        _hover = Hit.None;
        _hintVisible = false;
        UpdateView();
        _pos = Math.Clamp(_tl.Snap(at), 0, Math.Max(0, PosEnd - FrameDur / 2));
        ShowFrameAt(_pos);
        ShowToast("Trim: Enter keeps, Esc cancels");
        Activity();
        Invalidate();
    }

    /// <summary>Keeps the part chosen (the clips after it close up; one undo step), or leaves the clip as it was.</summary>
    void FinishTrim(bool keep)
    {
        if (!Trimming) return;
        if (_drag != Drag.None) TimelineUp(_dragX);
        Pause();
        _hover = Hit.None; // the handles and grip are gone
        _hintVisible = false;
        var c = _tl.Find(_trimClip);
        _trimClip = -1;
        _trimView = null;
        if (c == null) { UpdateView(); Invalidate(); return; }
        double from = keep ? _trimIn : c.In, to = keep ? _trimOut : c.Out;
        // The playhead back onto the timeline: the same frame if it's kept, else the clip's start.
        _pos = _pos >= from && _pos < to ? c.Start + (_pos - from) : c.Start;
        var next = keep ? _tl.Trim(c.Id, from, to) : _tl;
        if (!ReferenceEquals(next, _tl)) Edit(next, "Trimmed");
        else { UpdateView(); ShowFrameAt(_pos); Invalidate(); }
    }

    /// <summary>I / [ and O / ]: the part kept starts, or ends, at the frame under the playhead (starting trim mode if needed).</summary>
    void TrimAtPlayhead(bool start)
    {
        if (!Trimming)
        {
            if (_tl.At(_pos) is not Clip under) { ShowToast("No clip under the playhead"); return; }
            StartTrim(under.Id);
        }
        if (!Trimming) return;
        Pause();
        double fd = FrameDur, max = _tl.SourceEnd, at = _tl.Snap(_pos);
        if (start)
        {
            _trimIn = Math.Clamp(at, 0, max - fd);
            _trimOut = Math.Max(_trimOut, _trimIn + fd);
        }
        else
        {
            _trimOut = Math.Clamp(at + fd, fd, max); // this frame is the last one kept
            _trimIn = Math.Min(_trimIn, _trimOut - fd);
        }
        Invalidate(PanelRect);
    }

    /// <summary>A trim handle or the grip dragged to <paramref name="t"/> (the file's time); the frame there is shown.</summary>
    void DragTrim(double t)
    {
        double fd = FrameDur, max = _tl.SourceEnd;
        switch (_drag)
        {
            case Drag.TrimStart:
                _trimIn = Math.Clamp(_tl.Snap(t - _trimGrab), 0, _trimOut - fd);
                _pos = _trimIn;
                break;
            case Drag.TrimEnd:
                _trimOut = Math.Clamp(_tl.Snap(t - _trimGrab), _trimIn + fd, max);
                _pos = _trimOut - fd; // its last frame
                break;
            case Drag.TrimMove:
                double length = _trimOut - _trimIn;
                _trimIn = Math.Clamp(_tl.Snap(t - _trimGrab), 0, Math.Max(0, max - length));
                _trimOut = Math.Min(max, _tl.Snap(_trimIn + length));
                _pos = _trimIn;
                break;
        }
        ShowFrameAt(_pos, exact: false);
    }

    /// <summary>The grip under the part kept (in the audio track's lane): at least wide enough to grab.</summary>
    RectangleF TrimGripRect
    {
        get
        {
            float l = X(_trimIn), r = X(_trimOut), h = P(18), top = MusicRect.Top + (MusicRect.Height - h) / 2f, min = P(28);
            if (r - l < min) { float mid = (l + r) / 2; l = mid - min / 2; r = mid + min / 2; }
            return RectangleF.FromLTRB(l, top, r, top + h);
        }
    }

    (Hit Hit, int Id) TrimHit(Point p)
    {
        if (p.Y >= VideoRect.Top - P(4) && p.Y <= WaveRect.Bottom + P(4))
        {
            float l = X(_trimIn), r = X(_trimOut), dl = Math.Abs(p.X - l), dr = Math.Abs(p.X - r);
            if (Math.Min(dl, dr) <= P(7)) return (dl < dr || dl == dr && p.X < l ? Hit.TrimStart : Hit.TrimEnd, -1);
            return (Hit.Track, -1);
        }
        return TrimGripRect.Contains(p) ? (Hit.TrimGrip, -1) : (Hit.Track, -1);
    }

    /// <summary>Outside the part kept dimmed; the part framed in yellow with a handle at each end; the grip under it.</summary>
    void DrawTrim(Graphics g)
    {
        float l = X(_trimIn), r = X(_trimOut);
        var band = Rectangle.FromLTRB(TrackLeft, VideoRect.Top, TrackLeft + TrackWidth, WaveRect.Bottom);
        using (var dim = new SolidBrush(C(175, 0, 0, 0)))
        {
            if (l > band.Left) g.FillRectangle(dim, RectangleF.FromLTRB(band.Left, band.Top, l, band.Bottom));
            if (r < band.Right) g.FillRectangle(dim, RectangleF.FromLTRB(r, band.Top, band.Right, band.Bottom));
        }
        using (var pen = new Pen(C(255, TrimColor), P(2))) g.DrawRectangle(pen, l, band.Top, Math.Max(1, r - l), band.Height);
        foreach (var (x, h, d) in new[] { (l, Hit.TrimStart, Drag.TrimStart), (r, Hit.TrimEnd, Drag.TrimEnd) })
        {
            bool hot = _hover == h || _drag == d;
            float w = P(hot ? 9 : 7);
            // Inside the frame, so a handle at either end of the video stays on the track
            var hr = new RectangleF(h == Hit.TrimStart ? x - P(1) : x - w + P(1), band.Top - P(4), w, band.Height + P(8));
            using (var path = RoundRect(hr, P(2)))
            using (var b = new SolidBrush(C(255, TrimColor))) g.FillPath(b, path);
            using var notch = new Pen(C(170, 70, 50, 0), P(1.2f));
            float nx = hr.X + hr.Width / 2;
            g.DrawLine(notch, nx, hr.Y + hr.Height * 0.36f, nx, hr.Y + hr.Height * 0.64f);
        }

        var gr = TrimGripRect;
        bool gripHot = _hover == Hit.TrimGrip || _drag == Drag.TrimMove;
        using (var path = RoundRect(gr, gr.Height / 2f))
        using (var b = new SolidBrush(C(gripHot ? 255 : 215, TrimColor))) g.FillPath(b, path);
        string label = Length(_trimOut - _trimIn);
        var size = g.MeasureString(label, _smallFont);
        using var tb = new SolidBrush(C(255, 40, 30, 0));
        if (size.Width + P(12) < gr.Width) g.DrawString(label, _smallFont, tb, gr.X + (gr.Width - size.Width) / 2, gr.Y + (gr.Height - size.Height) / 2);
        else
            for (int k = -1; k <= 1; k++) // grip dots
                g.FillEllipse(tb, gr.X + gr.Width / 2 + k * P(5) - P(1.5f), gr.Y + gr.Height / 2 - P(1.5f), P(3), P(3));
    }

    /// <summary>Trim button: brackets around the part kept.</summary>
    void TrimGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.7f);
        float w = r.Width, h = r.Height, t = r.Y + h * 0.12f, b = r.Bottom - h * 0.12f;
        g.DrawLines(pen, new[] { new PointF(r.X + w * 0.3f, t), new PointF(r.X + w * 0.12f, t), new PointF(r.X + w * 0.12f, b), new PointF(r.X + w * 0.3f, b) });
        g.DrawLines(pen, new[] { new PointF(r.Right - w * 0.3f, t), new PointF(r.Right - w * 0.12f, t), new PointF(r.Right - w * 0.12f, b), new PointF(r.Right - w * 0.3f, b) });
        using var fill = new SolidBrush(c);
        g.FillRectangle(fill, RectangleF.FromLTRB(r.X + w * 0.36f, r.Y + h * 0.36f, r.Right - w * 0.36f, r.Bottom - h * 0.36f));
    }
}
