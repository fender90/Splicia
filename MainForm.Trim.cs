namespace Splicia;

/// <summary>
/// Trim mode: a yellow selection over the timeline, across as many clips (and sounds) as it covers. Drag its handles (or
/// press I / [ and O / ] at a frame) to set where it starts and ends, or its grip on the ruler to slide it along; Space
/// plays just the selection, as it will be. Apply (the button, T or Enter) keeps only what's inside it, moved to the
/// start (one undo step), and editing carries on; Cancel (Esc) leaves the timeline as it was. T, the trim button,
/// double-clicking a clip or its menu starts it: on that clip, or on the whole timeline.
/// </summary>
sealed partial class MainForm
{
    static readonly Color TrimColor = Color.FromArgb(255, 210, 60);

    bool _trimming;
    double _trimIn, _trimOut;  // the selection, on the timeline
    double _trimGrab;          // dragging a handle or the grip: pointer time − the edge it moves

    bool Trimming => _trimming;

    /// <summary>The playhead's range.</summary>
    double PosEnd => _tl.End;

    /// <summary>What playing plays: the timeline, or in trim mode just the selection (as it will be), from <see cref="PlayBase"/>.</summary>
    EditList PlayList() => Trimming ? EditList.Of(Kept()) : EditList.Of(_tl);
    double PlayBase => Trimming ? _trimIn : 0;
    double PlayEnd => Trimming ? _trimIn + Kept().End : _tl.End;

    Timeline? _kept;
    (double, double, int) _keptKey;

    /// <summary>What applying the trim would leave: the selection from 0, without a gap at its end (the timeline ends with its last clip or sound).</summary>
    Timeline Kept()
    {
        var key = (_trimIn, _trimOut, _tlVersion);
        if (_kept == null || key != _keptKey) { _kept = _tl.Keep(_trimIn, _trimOut); _keptKey = key; }
        return _kept;
    }

    /// <summary>
    /// Starts trimming, with the selection on clip <paramref name="id"/> (else the selected clip) or, with
    /// <paramref name="whole"/> or no clip, the whole timeline; while trimming, applies it.
    /// </summary>
    void StartTrim(int id = -1, bool whole = false)
    {
        if (_info == null || _pb == null || _drag != Drag.None) return;
        if (Trimming) { FinishTrim(keep: true); return; }
        if (_tl.End <= 0) { ShowToast("The timeline is empty"); return; }
        if (_cropping) FinishCrop(apply: true);
        Pause();
        var c = whole ? null : _tl.Find(id) ?? _tl.Find(_selClip);
        (_trimIn, _trimOut) = c != null ? (c.Start, c.End) : (0, _tl.End);
        _trimming = true;
        _selMusic = -1;
        _hover = Hit.None;
        _hintVisible = false;
        Activity();
        Invalidate();
    }

    /// <summary>Apply: only the selection is kept, moved to the start (one undo step). Cancel: the timeline stays as it was.</summary>
    void FinishTrim(bool keep)
    {
        if (!Trimming) return;
        if (_drag != Drag.None) TimelineUp(_dragX);
        Pause();
        if (keep && Kept() is { Clips.Count: 0, Music.Count: 0 }) { ShowToast("Nothing in the selection to keep"); return; }
        _hover = Hit.None; // the handles, grip and buttons are gone
        _hintVisible = false;
        _trimming = false;
        var next = keep ? Kept() : _tl;
        if (!keep || next.SameAs(_tl)) { Invalidate(); return; }
        // The playhead stays on its frame if that's kept, else goes to the start.
        _pos = _pos >= _trimIn && _pos < _trimOut ? _pos - _trimIn : 0;
        Edit(next, "Trimmed");
    }

    /// <summary>I / [ and O / ]: the selection starts, or ends, at the frame under the playhead (trimming the whole timeline if not yet).</summary>
    void TrimAtPlayhead(bool start)
    {
        if (!Trimming) StartTrim(whole: true);
        if (!Trimming) return;
        Pause();
        double fd = FrameDur, end = _tl.End, at = _tl.Snap(_pos);
        if (start)
        {
            _trimIn = Math.Clamp(at, 0, Math.Max(0, end - fd));
            _trimOut = Math.Min(end, Math.Max(_trimOut, _trimIn + fd));
        }
        else
        {
            _trimOut = Math.Clamp(at + fd, Math.Min(fd, end), end); // this frame is the last one kept
            _trimIn = Math.Max(0, Math.Min(_trimIn, _trimOut - fd));
        }
        Invalidate(PanelRect);
    }

    /// <summary>A handle or the grip dragged to <paramref name="t"/>; the frame at the edge that moved is shown.</summary>
    void DragTrim(double t)
    {
        double fd = FrameDur, end = _tl.End;
        switch (_drag)
        {
            case Drag.TrimStart:
                _trimIn = Math.Clamp(_tl.Snap(t - _trimGrab), 0, Math.Max(0, _trimOut - fd));
                _pos = _trimIn;
                break;
            case Drag.TrimEnd:
                _trimOut = Math.Clamp(_tl.Snap(t - _trimGrab), Math.Min(end, _trimIn + fd), end);
                _pos = Math.Max(_trimIn, _trimOut - fd); // its last frame
                break;
            case Drag.TrimMove:
                double length = _trimOut - _trimIn;
                _trimIn = Math.Clamp(_tl.Snap(t - _trimGrab), 0, Math.Max(0, end - length));
                _trimOut = Math.Min(end, _tl.Snap(_trimIn + length));
                _pos = _trimIn;
                break;
        }
        ShowFrameAt(_pos, exact: false);
    }

    /// <summary>The grip, on the ruler over the selection: at least wide enough to grab.</summary>
    RectangleF TrimGripRect
    {
        get
        {
            float l = X(_trimIn), r = X(_trimOut), min = P(28);
            if (r - l < min) { float mid = (l + r) / 2; l = mid - min / 2; r = mid + min / 2; }
            return RectangleF.FromLTRB(l, RulerRect.Top - P(1), r, RulerRect.Bottom + P(1));
        }
    }

    /// <summary>The bottom of what the selection covers: the video track and the audio track.</summary>
    Rectangle TrimBand => Rectangle.FromLTRB(TrackLeft, VideoRect.Top, TrackLeft + TrackWidth, MusicRect.Bottom);

    (Hit Hit, int Id) TrimHit(Point p)
    {
        if (p.Y < RulerRect.Bottom + P(1) && Math.Abs(p.X - X(_pos)) <= P(6)) return (Hit.Ruler, -1); // the playhead's knob
        if (TrimGripRect.Contains(p)) return (Hit.TrimGrip, -1);
        if (p.Y < RulerRect.Bottom + P(1)) return (Hit.Ruler, -1);
        float l = X(_trimIn), r = X(_trimOut), dl = Math.Abs(p.X - l), dr = Math.Abs(p.X - r);
        if (Math.Min(dl, dr) <= P(7)) return (dl < dr || dl == dr && p.X < l ? Hit.TrimStart : Hit.TrimEnd, -1);
        return (Hit.Track, -1);
    }

    /// <summary>Outside the selection dimmed; the selection framed in yellow with a handle at each end; its grip on the ruler.</summary>
    void DrawTrim(Graphics g)
    {
        float l = X(_trimIn), r = X(_trimOut);
        var band = TrimBand;
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
            // Inside the frame, so a handle at either end of the timeline stays on the track
            var hr = new RectangleF(h == Hit.TrimStart ? x - P(1) : x - w + P(1), band.Top - P(4), w, band.Height + P(8));
            using (var path = RoundRect(hr, P(2)))
            using (var b = new SolidBrush(C(255, TrimColor))) g.FillPath(b, path);
            using var notch = new Pen(C(170, 70, 50, 0), P(1.2f));
            float nx = hr.X + hr.Width / 2;
            g.DrawLine(notch, nx, hr.Y + hr.Height * 0.42f, nx, hr.Y + hr.Height * 0.58f);
        }

        var gr = TrimGripRect;
        bool gripHot = _hover == Hit.TrimGrip || _drag == Drag.TrimMove;
        using (var path = RoundRect(gr, gr.Height / 2f))
        using (var b = new SolidBrush(C(gripHot ? 255 : 225, TrimColor))) g.FillPath(b, path);
        string label = Length(Kept().End); // what applying keeps
        var size = g.MeasureString(label, _smallFont);
        using var tb = new SolidBrush(C(255, 40, 30, 0));
        if (size.Width + P(12) < gr.Width) g.DrawString(label, _smallFont, tb, gr.X + (gr.Width - size.Width) / 2, gr.Y + (gr.Height - size.Height) / 2);
        else
            for (int k = -1; k <= 1; k++) // grip dots
                g.FillEllipse(tb, gr.X + gr.Width / 2 + k * P(5) - P(1.5f), gr.Y + gr.Height / 2 - P(1.5f), P(3), P(3));
    }

    /// <summary>The Apply and Cancel buttons in the panel's header while trimming (Apply on the right).</summary>
    Rectangle TrimButtonRect(bool apply)
    {
        if (!Trimming) return Rectangle.Empty;
        var pr = PanelRect;
        int h = P(23), top = pr.Top + P(37);
        int aw = TextRenderer.MeasureText("Apply trim", _chipFont).Width + P(28), cw = TextRenderer.MeasureText("Cancel", _chipFont).Width + P(20);
        int right = pr.Right - Pad;
        return apply ? new Rectangle(right - aw, top, aw, h) : new Rectangle(right - aw - P(6) - cw, top, cw, h);
    }

    void DrawTrimButtons(Graphics g)
    {
        foreach (var (h, apply) in new[] { (Hit.TrimCancel, false), (Hit.TrimApply, true) })
        {
            var r = TrimButtonRect(apply);
            bool hot = _hover == h;
            Color fill = apply ? (hot ? Theme.AccentHover : Theme.Accent) : hot ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(28, 255, 255, 255);
            using (var p = RoundRect(r, r.Height / 2f))
            using (var b = new SolidBrush(C(fill.A, fill))) g.FillPath(b, p);
            TextRenderer.DrawText(g, apply ? "Apply trim" : "Cancel", _chipFont, r, C(255, 255, 255, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
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
