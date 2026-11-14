namespace Splicia;

/// <summary>
/// Hiding faces. The Faces button (in the editor) opens a bar over the picture: Off, Blur or Pixelate, and how strongly
/// (the Amount slider). Blur or Pixelate starts the pass that finds the faces (FaceFinder) in the background; the
/// preview hides them as they're found, and the export waits for the pass to finish. Nothing is remembered: each video
/// starts with faces shown.
/// </summary>
sealed partial class MainForm
{
    const int FaceOffItem = 0, FaceBlurItem = 1, FacePixelItem = 2, FaceSliderItem = 3, FaceDoneItem = 4, FaceStatusItem = -1;

    FaceMode _faceMode;
    int _faceAmount = 60;
    FaceFinder? _finder;
    bool _faceBar;

    /// <summary>How faces are hidden now (null: they aren't, or a finished pass found none: nothing to hide).</summary>
    FaceEffect? FacesNow() => _faceMode == FaceMode.Off || _finder == null || _info == null || _finder is { Done: true, Count: 0 }
        ? null : new FaceEffect(_finder, _faceMode, _faceAmount, _info);

    void ToggleFaceBar()
    {
        if (_info == null) return;
        _faceBar = !_faceBar;
        if (_faceBar) FinishCrop(apply: true);
        _hover = Hit.None;
        _hintVisible = false;
        Activity();
        Invalidate();
    }

    void SetFaceMode(FaceMode mode)
    {
        _faceMode = mode;
        if (mode != FaceMode.Off) StartFinder();
        // Off stops a pass that hasn't finished (it starts again with Blur or Pixelate); a finished one is kept.
        else if (_finder is { Done: false }) StopFinder();
        ApplyFaces();
    }

    /// <summary>
    /// The pass that finds the faces, for the picture as it's shown now. One that failed is tried again; after a turn,
    /// one that has found faces is kept (they're where they were on the frames), one that hasn't starts again the new
    /// way up (faces the right way up are found best).
    /// </summary>
    void StartFinder()
    {
        if (_info == null) return;
        if (_finder is { } old && old.Path == _info.Path && old.Error == null && (old.Rotation == EffectiveRotation || old.Count > 0)) return;
        StopFinder();
        var finder = new FaceFinder(_info.Path, EffectiveRotation);
        finder.Changed += () =>
        {
            try { BeginInvoke(() => FacesFound(finder)); }
            catch (InvalidOperationException) { } // the window is gone
        };
        _finder = finder;
        finder.Start();
    }

    /// <summary>Drops the pass; it stops in the background (within a frame or so) so the window doesn't wait.</summary>
    void StopFinder()
    {
        if (_finder is not { } old) return;
        _finder = null;
        Task.Run(old.Dispose);
    }

    void FacesFound(FaceFinder finder)
    {
        if (finder != _finder) return;
        if (_faceBar) Invalidate(FaceBarRect);
        // A paused frame shows the faces found since
        if (_faceMode != FaceMode.Off && !_playing && _drag == Drag.None) ShowFrameAt(_pos);
    }

    /// <summary>Hands the player how faces are hidden now, and shows the paused frame with it.</summary>
    void ApplyFaces()
    {
        if (_pb != null) _pb.Faces = FacesNow();
        if (!_playing && _info != null) ShowFrameAt(_pos);
        Invalidate();
    }

    void CloseFaces()
    {
        StopFinder();
        _faceMode = FaceMode.Off;
        _faceBar = false;
    }

    // ───────────────────────── the bar ─────────────────────────

    List<(RectangleF R, int Item, string Text)>? _faceItems;
    (Size, int) _faceItemsKey;

    /// <summary>The face bar's items, left to right: the modes, the slider, the status, Done.</summary>
    List<(RectangleF R, int Item, string Text)> FaceBarItems()
    {
        if (_faceItems != null && _faceItemsKey == (ClientSize, DeviceDpi)) return _faceItems;
        var items = new List<(RectangleF, int, string)>();
        float h = P(28), x = 0;
        foreach (var (item, text) in new[] { (FaceOffItem, "Off"), (FaceBlurItem, "Blur"), (FacePixelItem, "Pixelate") })
        {
            float w = TextRenderer.MeasureText(text, _chipFont).Width + P(16);
            items.Add((new RectangleF(x, 0, w, h), item, text));
            x += w + P(2);
        }
        x += P(16) + TextRenderer.MeasureText("Amount", _chipFont).Width + P(8);
        items.Add((new RectangleF(x, 0, P(150), h), FaceSliderItem, "Amount"));
        x += P(150) + P(8) + TextRenderer.MeasureText("100%", _chipFont).Width + P(14);
        items.Add((new RectangleF(x, 0, P(170), h), FaceStatusItem, ""));
        x += P(170) + P(6);
        float dw = TextRenderer.MeasureText("Done", _chipFont).Width + P(30);
        items.Add((new RectangleF(x, 0, dw, h), FaceDoneItem, "Done"));
        x += dw;
        float pad = P(4), left = (ClientSize.Width - (x + 2 * pad)) / 2f + pad, top = P(58) + pad;
        for (int i = 0; i < items.Count; i++)
        {
            var (r, item, text) = items[i];
            items[i] = (new RectangleF(r.X + left, top, r.Width, r.Height), item, text);
        }
        _faceItemsKey = (ClientSize, DeviceDpi);
        return _faceItems = items;
    }

    Rectangle FaceBarRect
    {
        get
        {
            var items = FaceBarItems();
            float pad = P(4);
            return Rectangle.Round(RectangleF.FromLTRB(items[0].R.Left - pad, items[0].R.Top - pad, items[^1].R.Right + pad, items[0].R.Bottom + pad));
        }
    }

    int FaceItemAt(Point p)
    {
        foreach (var (r, item, _) in FaceBarItems())
            if (item != FaceStatusItem && RectangleF.Inflate(r, 0, P(4)).Contains(p)) return item;
        return int.MinValue;
    }

    RectangleF FaceSliderRect => FaceBarItems().First(i => i.Item == FaceSliderItem).R;

    void SetFaceAmountAt(int x)
    {
        var r = FaceSliderRect;
        int v = Math.Clamp((int)Math.Round((x - r.Left) / r.Width * 100), 1, 100);
        if (v == _faceAmount) return;
        _faceAmount = v;
        ApplyFaces();
    }

    void FaceBarClick(int item)
    {
        switch (item)
        {
            case FaceOffItem: SetFaceMode(FaceMode.Off); break;
            case FaceBlurItem: SetFaceMode(FaceMode.Blur); break;
            case FacePixelItem: SetFaceMode(FaceMode.Pixelate); break;
            case FaceDoneItem: ToggleFaceBar(); break;
        }
    }

    string FaceStatus()
    {
        if (_faceMode == FaceMode.Off || _finder == null) return "Faces are shown";
        if (_finder.Error != null) return "Couldn't look for faces";
        int n = _finder.Count;
        if (!_finder.Done) return $"Finding faces… {_finder.Progress * 100:0}%";
        return n == 0 ? "No faces found" : $"{n} face{(n == 1 ? "" : "s")} found";
    }

    void DrawFaceBar(Graphics g)
    {
        var bar = FaceBarRect;
        using (var path = RoundRect(bar, bar.Height / 2f)) Glass(g, path, bar, darken: 95);
        foreach (var (r, item, text) in FaceBarItems())
        {
            bool hot = _hover == Hit.FaceBar && item == _hoverItem;
            switch (item)
            {
                case FaceOffItem or FaceBlurItem or FacePixelItem or FaceDoneItem:
                {
                    bool done = item == FaceDoneItem;
                    bool selected = !done && (int)_faceMode == item;
                    Color fill = done ? (hot ? Theme.AccentHover : Theme.Accent)
                        : selected ? Theme.BtnHover : hot ? Color.FromArgb(60, 255, 255, 255) : Color.Empty;
                    if (fill != Color.Empty)
                        using (var p = RoundRect(r, r.Height / 2))
                        using (var b = new SolidBrush(C(fill.A, fill))) g.FillPath(b, p);
                    if (selected)
                        using (var p = RoundRect(r, r.Height / 2))
                        using (var pen = new Pen(C(255, Theme.AccentHover), P(1.5f))) g.DrawPath(pen, p);
                    TextRenderer.DrawText(g, text, _chipFont, Rectangle.Round(r), C(done || selected ? 255 : 225, 255, 255, 255),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    break;
                }
                case FaceSliderItem:
                {
                    var lsize = TextRenderer.MeasureText("Amount", _chipFont);
                    TextRenderer.DrawText(g, "Amount", _chipFont, new Point((int)(r.Left - P(8) - lsize.Width), (int)(r.Top + (r.Height - lsize.Height) / 2)), C(200, 255, 255, 255));
                    bool active = _faceMode != FaceMode.Off;
                    float cy = r.Top + r.Height / 2, kx = r.Left + r.Width * (_faceAmount / 100f);
                    using (var track = new Pen(W(60), P(3)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawLine(track, r.Left, cy, r.Right, cy);
                    using (var fill = new Pen(C(255, active ? Theme.AccentHover : Color.FromArgb(150, 150, 160)), P(3)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                        g.DrawLine(fill, r.Left, cy, kx, cy);
                    float kr = P(hot || _drag == Drag.FaceAmount ? 7 : 6);
                    using (var knob = new SolidBrush(W(255))) g.FillEllipse(knob, kx - kr, cy - kr, 2 * kr, 2 * kr);
                    var vsize = TextRenderer.MeasureText($"{_faceAmount}%", _chipFont);
                    TextRenderer.DrawText(g, $"{_faceAmount}%", _chipFont, new Point((int)(r.Right + P(8)), (int)(r.Top + (r.Height - vsize.Height) / 2)), C(230, 255, 255, 255));
                    break;
                }
                case FaceStatusItem:
                    TextRenderer.DrawText(g, FaceStatus(), _chipFont, Rectangle.Round(r), C(_finder?.Error != null ? 255 : 170, 255, _finder?.Error != null ? 140 : 255, _finder?.Error != null ? 140 : 255),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                    break;
            }
        }
    }

    /// <summary>Faces button: a face, lit while faces are hidden.</summary>
    void FaceGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.6f);
        g.DrawEllipse(pen, r.X + r.Width * 0.08f, r.Y + r.Height * 0.04f, r.Width * 0.84f, r.Height * 0.92f);
        using var dot = new SolidBrush(c);
        float er = r.Width * 0.07f;
        g.FillEllipse(dot, r.X + r.Width * 0.33f - er, r.Y + r.Height * 0.4f - er, 2 * er, 2 * er);
        g.FillEllipse(dot, r.X + r.Width * 0.67f - er, r.Y + r.Height * 0.4f - er, 2 * er, 2 * er);
        g.DrawArc(pen, r.X + r.Width * 0.3f, r.Y + r.Height * 0.45f, r.Width * 0.4f, r.Height * 0.3f, 20, 140);
    }
}
