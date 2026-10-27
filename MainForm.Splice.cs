using System.Drawing.Drawing2D;

namespace Splicia;

/// <summary>
/// Splicing: cutting a part out of the video and joining the ends. S marks the first frame to cut, S again the last one;
/// the part between is removed. Spliced-out parts show on the timeline as hatched bands with draggable edges and a dot
/// in the middle whose menu picks the transition at the join (none, crossfade, fade out / fade in) or restores the part.
/// Playback skips them and previews fades; the export joins the kept parts with the transitions.
/// </summary>
sealed partial class MainForm
{
    static readonly Color SpliceColor = Theme.VioletHover;

    readonly List<Splice> _splices = new();   // sorted, not overlapping, in seconds of the file
    double? _spliceMark;                      // the first S: where the part to cut out starts
    Transition _nextTransition = new(TransitionKind.None, 0.5); // the last transition picked, for new splices
    int _hitSplice = -1, _hoverSplice = -1, _pressedSplice = -1, _dragSplice = -1, _menuSplice = -1;
    readonly ContextMenuStrip _spliceMenu = new();

    /// <summary>The export as things stand: selection, rotation, crop and splices.</summary>
    TrimJob CurrentJob() => new(_info!, _in, _out, _rot) { Crop = HasCrop ? _crop : null, Splices = _splices.ToArray() };

    /// <summary>Where the kept part of the selection starts and ends (the selection itself without splices at its edges).</summary>
    (double Start, double End) KeptRange()
    {
        if (_info == null || _splices.Count == 0) return (_in, _out);
        var (parts, _) = CurrentJob().Pieces();
        return parts.Count == 0 ? (_in, _out) : (parts[0].From, parts[^1].To);
    }

    /// <summary>Splices that overlap the selection.</summary>
    int SplicesInSelection => _splices.Count(s => s.To > _in && s.From < _out);

    // ───────────────────────── actions ─────────────────────────

    /// <summary>S / the splice button: mark the first frame to cut, then (on the second press) the last one.</summary>
    void SpliceHere()
    {
        if (_info == null || _drag != Drag.None) return; // not while dragging (the list mustn't change under the drag)
        if (_spliceMark is not double mark)
        {
            _spliceMark = _pos;
            ShowToast("Splice starts here: go to its last frame and press S again");
            Invalidate(PanelRect);
            return;
        }
        if (_playing) Pause();
        _spliceMark = null;
        // Both frames on screen are cut, like the selection's start and end.
        double from = Math.Min(mark, _pos), to = Math.Min(Duration, Math.Max(mark, _pos) + FrameDur);
        AddSplice(from, to);
    }

    void AddSplice(double from, double to)
    {
        if (_info == null || to - from < FrameDur * 0.5) return;
        var before = _splices.ToList();
        SplicesChanged();
        _splices.Add(new Splice(from, to, _nextTransition));
        NormalizeSplices();
        if (CurrentJob().Pieces().Parts.Count == 0)
        {
            _splices.Clear();
            _splices.AddRange(before);
            ShowToast("That would cut out the whole selection");
            return;
        }
        ShowToast($"Cut out {Length(to - from)}: the ends are joined");
        GoTo(to < Duration - FrameDur * 0.5 ? to : Math.Max(0, from - FrameDur)); // the first frame after the join
    }

    void CancelSpliceMark()
    {
        _spliceMark = null;
        ShowToast("Splice cancelled");
        Invalidate(PanelRect);
    }

    void RestoreSplice(int i)
    {
        if (i < 0 || i >= _splices.Count) return;
        var sp = _splices[i];
        _splices.RemoveAt(i);
        SplicesChanged();
        ShowToast($"Restored {Length(sp.To - sp.From)}");
        Invalidate();
    }

    void ClearSplices()
    {
        if (_splices.Count == 0) return;
        _splices.Clear();
        SplicesChanged();
        _spliceMark = null;
        ShowToast("All spliced-out parts restored");
        Invalidate();
    }

    void SetTransition(int i, Transition t)
    {
        if (i < 0 || i >= _splices.Count) return;
        _splices[i] = _splices[i] with { Transition = t };
        _nextTransition = t;
        Invalidate(PanelRect);
    }

    void SetAllTransitions(Transition t)
    {
        for (int i = 0; i < _splices.Count; i++) _splices[i] = _splices[i] with { Transition = t };
        _nextTransition = t;
        ShowToast($"Every join: {t}");
        Invalidate(PanelRect);
    }

    /// <summary>
    /// Splices were added, removed or merged: indices into the list may now mean another splice, so a splice edge still
    /// waiting to land on its exact frame is dropped.
    /// </summary>
    void SplicesChanged()
    {
        if (_snap is Drag.SpliceFrom or Drag.SpliceTo) _snap = Drag.None;
        _dragSplice = -1;
    }

    /// <summary>True when the splices cut out everything in the selection.</summary>
    bool NothingKept => _info != null && _splices.Count > 0 && CurrentJob().Pieces().Parts.Count == 0;

    /// <summary>Sorted, inside the file, overlapping or touching splices merged (the first one's transition wins), slivers dropped.</summary>
    void NormalizeSplices()
    {
        var list = _splices
            .Select(s => s with { From = Math.Clamp(s.From, 0, Duration), To = Math.Clamp(s.To, 0, Duration) })
            .Where(s => s.To - s.From >= FrameDur * 0.5)
            .OrderBy(s => s.From).ToList();
        _splices.Clear();
        foreach (var s in list)
        {
            if (_splices.Count > 0 && s.From <= _splices[^1].To + FrameDur * 0.5)
                _splices[^1] = _splices[^1] with { To = Math.Max(_splices[^1].To, s.To) };
            else _splices.Add(s);
        }
    }

    // ───────────────────────── playback ─────────────────────────

    /// <summary>The splice playback is inside (it jumps to its end, as the export does), or null.</summary>
    Splice? CutAt(double t)
    {
        foreach (var s in _splices)
            if (t >= s.From - 1e-4 && t < s.To - 1e-4) return s;
        return null;
    }

    /// <summary>Start of the next splice after <paramref name="t"/>, or infinity: frames from there on aren't shown.</summary>
    double NextCut(double t)
    {
        foreach (var s in _splices)
            if (s.From > t) return s.From;
        return double.PositiveInfinity;
    }

    /// <summary>How dark the preview is at <paramref name="t"/> during a fade through black (0..1). Crossfades aren't previewed.</summary>
    float FadeAt(double t)
    {
        if (_info == null || _splices.Count == 0) return 0;
        var job = CurrentJob();
        var (parts, _) = job.Pieces();
        var joins = job.Joins();
        float dark = 0;
        for (int i = 0; i < joins.Count; i++)
        {
            var j = joins[i];
            if (j.Kind != TransitionKind.FadeThroughBlack) continue;
            double end = parts[i].To, start = parts[i + 1].From;
            if (j.FadeOut > 0 && t >= end - j.FadeOut && t < end) dark = Math.Max(dark, (float)((t - (end - j.FadeOut)) / j.FadeOut));
            if (j.FadeIn > 0 && t >= start && t < start + j.FadeIn) dark = Math.Max(dark, (float)(1 - (t - start) / j.FadeIn));
        }
        return Math.Clamp(dark, 0, 1);
    }

    // ───────────────────────── timeline ─────────────────────────

    PointF ChipCenter(Splice s) { var sr = StripRect; return new((X(s.From) + X(s.To)) / 2, sr.Top + sr.Height / 2f); }

    float ChipRadius => P(10);

    /// <summary>The splice part under <paramref name="p"/>: its dot first (on top), then its edges.</summary>
    (Hit Hit, int Index) SpliceHitAt(Point p)
    {
        var sr = StripRect;
        if (_info == null || p.Y < sr.Top - P(2) || p.Y > sr.Bottom + P(2)) return (Hit.None, -1);
        for (int i = _splices.Count - 1; i >= 0; i--)
        {
            var c = ChipCenter(_splices[i]);
            float dx = p.X - c.X, dy = p.Y - c.Y, r = ChipRadius + P(2);
            if (dx * dx + dy * dy <= r * r) return (Hit.SpliceChip, i);
        }
        int best = -1;
        Hit bestHit = Hit.None;
        float bestD = P(5);
        for (int i = 0; i < _splices.Count; i++)
        {
            float df = Math.Abs(p.X - X(_splices[i].From)), dt = Math.Abs(p.X - X(_splices[i].To));
            if (df <= bestD) { bestD = df; best = i; bestHit = Hit.SpliceFrom; }
            if (dt < bestD || dt <= bestD && p.X > X(_splices[i].To)) { bestD = dt; best = i; bestHit = Hit.SpliceTo; }
        }
        return (bestHit, best);
    }

    /// <summary>The spliced-out band under <paramref name="p"/> (for the right-click menu), or -1.</summary>
    int SpliceBandAt(Point p)
    {
        var (h, i) = SpliceHitAt(p);
        if (h != Hit.None) return i;
        var sr = StripRect;
        if (p.Y < sr.Top || p.Y > sr.Bottom) return -1;
        return _splices.FindIndex(s => p.X >= X(s.From) - P(2) && p.X <= X(s.To) + P(2));
    }

    /// <summary>Spliced-out bands (darkened, hatched) and the pending splice; drawn inside the filmstrip's clip.</summary>
    void DrawSpliceBands(Graphics g, RectangleF film)
    {
        using var dark = new SolidBrush(C(170, 6, 8, 20));
        using var hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, C(90, SpliceColor), Color.Transparent);
        foreach (var s in _splices)
        {
            float x0 = X(s.From), x1 = Math.Max(X(s.To), x0 + 1);
            var band = new RectangleF(x0, film.Top, x1 - x0, film.Height);
            g.FillRectangle(dark, band);
            g.FillRectangle(hatch, band);
        }
        if (_spliceMark is double m)
        {
            float a = X(Math.Min(m, _pos)), b = X(Math.Max(m, _pos) + FrameDur);
            using var pending = new SolidBrush(C(80, SpliceColor));
            g.FillRectangle(pending, a, film.Top, Math.Max(1, b - a), film.Height);
        }
    }

    /// <summary>Edges and transition dots of the splices, and the pending splice's start, over the selection frame.</summary>
    void DrawSpliceMarks(Graphics g)
    {
        var sr = StripRect;
        for (int i = 0; i < _splices.Count; i++)
        {
            var s = _splices[i];
            foreach (var (t, h, d) in new[] { (s.From, Hit.SpliceFrom, Drag.SpliceFrom), (s.To, Hit.SpliceTo, Drag.SpliceTo) })
            {
                bool hot = _hoverSplice == i && _hover == h || _dragSplice == i && _drag == d;
                using var pen = new Pen(C(255, hot ? Color.White : SpliceColor), P(hot ? 3 : 2));
                float x = X(t);
                g.DrawLine(pen, x, sr.Top, x, sr.Bottom);
            }
        }
        // The dots go on top of every band, so a short splice's dot still covers its neighbours' edges.
        for (int i = 0; i < _splices.Count; i++)
        {
            var c = ChipCenter(_splices[i]);
            bool hot = _hoverSplice == i && _hover == Hit.SpliceChip || _menuSplice == i && _spliceMenu.Visible;
            float r = ChipRadius;
            var box = new RectangleF(c.X - r, c.Y - r, 2 * r, 2 * r);
            using (var fill = new SolidBrush(C(245, hot ? Theme.BtnHover : Theme.Bg))) g.FillEllipse(fill, box);
            using (var ring = new Pen(C(255, SpliceColor), P(hot ? 2.2f : 1.6f))) g.DrawEllipse(ring, box);
            TransitionGlyph(g, c, r * 0.5f, _splices[i].Transition.Kind, W(240));
        }
        if (_spliceMark is double m)
        {
            float x = X(m);
            using var pen = new Pen(C(255, SpliceColor), P(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, x, sr.Top - P(5), x, sr.Bottom + P(5));
        }
    }

    /// <summary>The kind of join: a straight cut (|), a crossfade (×) or a dip through black (∨).</summary>
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

    /// <summary>Splice button: scissors.</summary>
    void ScissorsGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.7f);
        float rr = r.Width * 0.15f, cy = r.Bottom - rr - r.Height * 0.02f;
        float lx = r.X + r.Width * 0.27f, rx = r.Right - r.Width * 0.27f;
        g.DrawEllipse(pen, lx - rr, cy - rr, 2 * rr, 2 * rr);
        g.DrawEllipse(pen, rx - rr, cy - rr, 2 * rr, 2 * rr);
        // Blades: from each ring up and across to the opposite top corner.
        g.DrawLine(pen, lx + rr * 0.7f, cy - rr * 0.7f, r.Right - r.Width * 0.14f, r.Y + r.Height * 0.04f);
        g.DrawLine(pen, rx - rr * 0.7f, cy - rr * 0.7f, r.X + r.Width * 0.14f, r.Y + r.Height * 0.04f);
    }

    // ───────────────────────── transition menu ─────────────────────────

    void BuildSpliceMenu()
    {
        _spliceMenu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
        _spliceMenu.ShowImageMargin = false;
        _spliceMenu.ForeColor = Color.Gainsboro;
        _spliceMenu.Font = new Font("Segoe UI", 9.5f);
        _spliceMenu.Closed += (_, _) => { _menuSplice = -1; Activity(); Invalidate(PanelRect); };
    }

    void ShowSpliceMenu(int i, Point at)
    {
        if (i < 0 || i >= _splices.Count || _drag != Drag.None) return;
        if (_playing) Pause();
        PopulateSpliceMenu(i);
        Invalidate(PanelRect);
        _spliceMenu.Show(this, at);
    }

    /// <summary>The splice menu: what's cut out, the transition and its length, apply to all, restore.</summary>
    void PopulateSpliceMenu(int i)
    {
        _menuSplice = i;
        var sp = _splices[i];
        var t = sp.Transition;
        _spliceMenu.Items.Clear();
        ToolStripMenuItem Item(string text, Action a, bool enabled = true) => new(text, null, (_, _) => a()) { Enabled = enabled };
        static string Check(bool on, string text) => (on ? "✓  " : "     ") + text;

        bool joined = sp.From > _in + FrameDur * 0.5 && sp.To < _out - FrameDur * 0.5;
        _spliceMenu.Items.Add(new ToolStripMenuItem(
            $"Cut out {Fmt(sp.From)} – {Fmt(sp.To)}  ({Length(sp.To - sp.From)})") { Enabled = false });
        if (!joined)
            _spliceMenu.Items.Add(new ToolStripMenuItem("At the edge of the selection: nothing to join") { Enabled = false });
        _spliceMenu.Items.Add(new ToolStripSeparator());
        _spliceMenu.Items.Add(new ToolStripMenuItem("TRANSITION") { Enabled = false });
        foreach (var k in new[] { TransitionKind.None, TransitionKind.Crossfade, TransitionKind.FadeThroughBlack })
            _spliceMenu.Items.Add(Item(Check(t.Kind == k, Transition.Name(k)), () => SetTransition(i, t with { Kind = k })));
        _spliceMenu.Items.Add(new ToolStripSeparator());
        _spliceMenu.Items.Add(new ToolStripMenuItem("LENGTH") { Enabled = false });
        foreach (double len in Transition.Lengths)
            _spliceMenu.Items.Add(Item(Check(Math.Abs(t.Seconds - len) < 1e-6, $"{len:0.##} s"), () => SetTransition(i, t with { Seconds = len }),
                t.Kind != TransitionKind.None));
        _spliceMenu.Items.Add(new ToolStripSeparator());
        _spliceMenu.Items.Add(Item("Use this transition for every splice", () => SetAllTransitions(t), _splices.Count > 1));
        _spliceMenu.Items.Add(Item("Restore this part", () => RestoreSplice(i)));
    }
}
