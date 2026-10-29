using System.Drawing.Drawing2D;

namespace Splicia;

/// <summary>
/// Crop mode: the whole frame is shown with a crop rectangle over it (corner and edge handles, drag inside to move,
/// drag outside to draw a new one) and a toolbar of aspect ratios. Outside crop mode the preview shows just the crop.
/// The crop is kept as fractions of the picture as shown (after rotation), which is what the export uses too.
/// </summary>
sealed partial class MainForm
{
    enum CropPart { None, Move, N, S, W, E, NW, NE, SW, SE, New }

    static readonly RectangleF FullFrame = new(0, 0, 1, 1);

    /// <summary>Aspect choices. Ratio 0 = free, -1 = the picture's own.</summary>
    static readonly (string Label, double Ratio)[] Aspects =
    {
        ("Free", 0), ("Original", -1), ("1:1", 1), ("16:9", 16 / 9.0), ("9:16", 9 / 16.0),
        ("4:3", 4 / 3.0), ("3:4", 3 / 4.0), ("4:5", 4 / 5.0), ("21:9", 21 / 9.0),
    };
    const int ResetItem = -2, DoneItem = -3;

    RectangleF _crop = FullFrame, _cropBefore = FullFrame;
    bool _cropping;
    int _aspect;                 // index into Aspects
    CropPart _cropPart;
    RectangleF _cropStart;       // display pixels, at the start of a drag
    PointF _cropAnchor, _cropGrab;
    int _hoverItem = int.MinValue, _pressedItem = int.MinValue;
    readonly Font _chipFont = new("Segoe UI Semibold", 9.5f);

    bool HasCrop => _crop.X > 1e-4f || _crop.Y > 1e-4f || _crop.Right < 1 - 1e-4f || _crop.Bottom < 1 - 1e-4f;

    /// <summary>The picture as shown (rotated, square pixels), in "display pixels".</summary>
    (float W, float H) Disp
    {
        get
        {
            if (_info == null) return (1, 1);
            var (w, h) = _info.DisplaySize(_rot);
            return (Math.Max(1, w), Math.Max(1, h));
        }
    }

    float MinCropPx { get { var (w, h) = Disp; return Math.Max(16, 0.04f * Math.Min(w, h)); } }

    double LockedRatio
    {
        get
        {
            double r = Aspects[_aspect].Ratio;
            var (w, h) = Disp;
            return r < 0 ? w / (double)h : r;
        }
    }

    RectangleF ToDisp(RectangleF n) { var (w, h) = Disp; return new(n.X * w, n.Y * h, n.Width * w, n.Height * h); }

    RectangleF ToNorm(RectangleF d)
    {
        var (w, h) = Disp;
        var n = new RectangleF(d.X / w, d.Y / h, d.Width / w, d.Height / h);
        // Guard against float drift past the edges.
        float x = Math.Clamp(n.X, 0, 1), y = Math.Clamp(n.Y, 0, 1);
        return new RectangleF(x, y, Math.Clamp(n.Width, 0.001f, 1 - x), Math.Clamp(n.Height, 0.001f, 1 - y));
    }

    /// <summary>Exported picture size for the current rotation and crop.</summary>
    (int W, int H) OutputSize() => _info == null ? (0, 0)
        : new TrimJob(_info, 0, 1, _rot) { Crop = HasCrop ? _crop : null }.OutputSize();

    // ───────────────────────── layout ─────────────────────────

    /// <summary>Part of the bitmap the preview shows: the crop, except while editing it.</summary>
    Rectangle SourceRect
    {
        get
        {
            if (_bmp == null) return Rectangle.Empty;
            int bw = _bmp.Width, bh = _bmp.Height;
            if (_cropping || !HasCrop) return new Rectangle(0, 0, bw, bh);
            int x = Math.Clamp((int)Math.Round(_crop.X * bw), 0, bw - 1), y = Math.Clamp((int)Math.Round(_crop.Y * bh), 0, bh - 1);
            int w = Math.Clamp((int)Math.Round(_crop.Width * bw), 1, bw - x), h = Math.Clamp((int)Math.Round(_crop.Height * bh), 1, bh - y);
            return new Rectangle(x, y, w, h);
        }
    }

    /// <summary>Where the picture goes while cropping: below the aspect bar, above the control panel, with room for the handles.</summary>
    RectangleF CropEditArea
    {
        get
        {
            float m = P(40), top = CropBarRect.Bottom + P(22), bottom = PanelRect.Top - P(22);
            if (bottom - top < P(120) || ClientSize.Width - 2 * m < P(120)) return new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
            return new RectangleF(m, top, ClientSize.Width - 2 * m, bottom - top);
        }
    }

    List<(RectangleF R, int Item, string Text)>? _barItems;
    (Size, int) _barKey;

    /// <summary>The aspect bar's items (chips, Reset, Done), left to right, with their rectangles.</summary>
    List<(RectangleF R, int Item, string Text)> CropBarItems()
    {
        if (_barItems != null && _barKey == (ClientSize, DeviceDpi)) return _barItems;
        var items = new List<(RectangleF, int, string)>();
        float h = P(28), pad = P(4), x = 0;
        for (int i = 0; i < Aspects.Length; i++)
        {
            float w = TextRenderer.MeasureText(Aspects[i].Label, _chipFont).Width + P(14);
            items.Add((new RectangleF(x, 0, w, h), i, Aspects[i].Label));
            x += w + P(2);
        }
        x += P(12);
        float rw = TextRenderer.MeasureText("Reset", _chipFont).Width + P(16);
        items.Add((new RectangleF(x, 0, rw, h), ResetItem, "Reset"));
        x += rw + P(6);
        float dw = TextRenderer.MeasureText("Done", _chipFont).Width + P(30);
        items.Add((new RectangleF(x, 0, dw, h), DoneItem, "Done"));
        x += dw;
        // centre the row in the window
        float left = (ClientSize.Width - (x + 2 * pad)) / 2f + pad, top = P(58) + pad;
        for (int i = 0; i < items.Count; i++)
        {
            var (r, item, text) = items[i];
            items[i] = (new RectangleF(r.X + left, top, r.Width, r.Height), item, text);
        }
        _barKey = (ClientSize, DeviceDpi);
        return _barItems = items;
    }

    Rectangle CropBarRect
    {
        get
        {
            var items = CropBarItems();
            float pad = P(4);
            return Rectangle.Round(RectangleF.FromLTRB(items[0].R.Left - pad, items[0].R.Top - pad, items[^1].R.Right + pad, items[0].R.Bottom + pad));
        }
    }

    int CropItemAt(Point p)
    {
        foreach (var (r, item, _) in CropBarItems())
            if (RectangleF.Inflate(r, P(1), P(3)).Contains(p)) return item;
        return int.MinValue;
    }

    /// <summary>The crop rectangle in window coordinates (over the whole picture, as shown in crop mode).</summary>
    RectangleF CropWindowRect
    {
        get
        {
            var vr = VideoRectF;
            return new RectangleF(vr.X + _crop.X * vr.Width, vr.Y + _crop.Y * vr.Height, _crop.Width * vr.Width, _crop.Height * vr.Height);
        }
    }

    CropPart CropPartAt(Point p)
    {
        if (!_cropping || _bmp == null || PanelRect.Contains(p) || CropBarRect.Contains(p) || WindowButtonsRect.Contains(p)) return CropPart.None;
        var c = CropWindowRect;
        float corner = P(16), edge = P(10);
        bool Near(float x, float y) => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) <= corner * corner;
        if (Near(c.Left, c.Top)) return CropPart.NW;
        if (Near(c.Right, c.Top)) return CropPart.NE;
        if (Near(c.Left, c.Bottom)) return CropPart.SW;
        if (Near(c.Right, c.Bottom)) return CropPart.SE;
        bool inY = p.Y > c.Top && p.Y < c.Bottom, inX = p.X > c.Left && p.X < c.Right;
        if (inY && Math.Abs(p.X - c.Left) <= edge) return CropPart.W;
        if (inY && Math.Abs(p.X - c.Right) <= edge) return CropPart.E;
        if (inX && Math.Abs(p.Y - c.Top) <= edge) return CropPart.N;
        if (inX && Math.Abs(p.Y - c.Bottom) <= edge) return CropPart.S;
        if (c.Contains(p)) return CropPart.Move;
        return VideoRectF.Contains(p) ? CropPart.New : CropPart.None;
    }

    static Cursor CropCursor(CropPart part) => part switch
    {
        CropPart.NW or CropPart.SE => Cursors.SizeNWSE,
        CropPart.NE or CropPart.SW => Cursors.SizeNESW,
        CropPart.N or CropPart.S => Cursors.SizeNS,
        CropPart.W or CropPart.E => Cursors.SizeWE,
        CropPart.Move => Cursors.SizeAll,
        CropPart.New => Cursors.Cross,
        _ => Cursors.Default,
    };

    /// <summary>A window point as display pixels of the picture, clamped to it.</summary>
    PointF WindowToDisp(Point p)
    {
        var vr = VideoRectF;
        var (w, h) = Disp;
        return new PointF(Math.Clamp((p.X - vr.X) / vr.Width, 0, 1) * w, Math.Clamp((p.Y - vr.Y) / vr.Height, 0, 1) * h);
    }

    // ───────────────────────── editing ─────────────────────────

    void ToggleCrop()
    {
        if (_info == null) return;
        if (_cropping) { FinishCrop(apply: true); return; }
        if (Trimming) { ShowToast("Apply or cancel the trim first"); return; }
        _cropping = true;
        _cropBefore = _crop;
        _zoom = 1f;
        _center = new PointF(0.5f, 0.5f);
        _hover = Hit.None;
        _hintVisible = false;
        Activity();
        _blurDirty = true;
        Invalidate();
    }

    void FinishCrop(bool apply)
    {
        if (!_cropping) return;
        if (!apply) _crop = _cropBefore;
        if (!HasCrop) _crop = FullFrame;
        _cropping = false;
        _drag = _drag == Drag.Crop ? Drag.None : _drag;
        _blurDirty = true;
        if (apply)
        {
            var (w, h) = OutputSize();
            ShowToast(HasCrop ? $"Cropped to {w} × {h}" : "No crop");
        }
        Invalidate();
    }

    void ResetCrop()
    {
        _crop = FullFrame;
        _aspect = 0;
        Invalidate();
    }

    /// <summary>Locks the aspect ratio: the largest rectangle of that shape around the current crop's centre.</summary>
    void ApplyAspect(int index)
    {
        _aspect = index;
        double r = LockedRatio;
        if (r > 0)
        {
            var (dw, dh) = Disp;
            var cur = ToDisp(_crop);
            float w = (float)Math.Min(dw, dh * r), h = (float)(w / r);
            float cx = cur.X + cur.Width / 2, cy = cur.Y + cur.Height / 2;
            _crop = ToNorm(new RectangleF(Math.Clamp(cx - w / 2, 0, dw - w), Math.Clamp(cy - h / 2, 0, dh - h), w, h));
        }
        Invalidate();
    }

    void BeginCropDrag(Point p)
    {
        _cropPart = CropPartAt(p);
        if (_cropPart == CropPart.None) return;
        _drag = Drag.Crop;
        _cropStart = ToDisp(_crop);
        _cropGrab = WindowToDisp(p);
        var s = _cropStart;
        _cropAnchor = _cropPart switch
        {
            CropPart.NW => new PointF(s.Right, s.Bottom),
            CropPart.NE => new PointF(s.Left, s.Bottom),
            CropPart.SW => new PointF(s.Right, s.Top),
            CropPart.SE => new PointF(s.Left, s.Top),
            _ => _cropGrab, // drawing a new rectangle starts where the pointer went down
        };
        Capture = true;
    }

    void DragCrop(Point p)
    {
        var (dw, dh) = Disp;
        var q = WindowToDisp(p);
        double ratio = LockedRatio;
        float min = MinCropPx;
        var s = _cropStart;
        RectangleF r;
        switch (_cropPart)
        {
            case CropPart.Move:
            {
                float x = Math.Clamp(s.X + q.X - _cropGrab.X, 0, dw - s.Width), y = Math.Clamp(s.Y + q.Y - _cropGrab.Y, 0, dh - s.Height);
                r = new RectangleF(x, y, s.Width, s.Height);
                break;
            }
            case CropPart.New:
                if (Math.Abs(q.X - _cropGrab.X) < 3 && Math.Abs(q.Y - _cropGrab.Y) < 3) return; // not a drag yet
                r = FromAnchor(_cropAnchor, q, ratio, min, dw, dh);
                break;
            case CropPart.NW or CropPart.NE or CropPart.SW or CropPart.SE:
                r = FromAnchor(_cropAnchor, q, ratio, min, dw, dh);
                break;
            default:
                r = FromEdge(_cropPart, s, q, ratio, min, dw, dh);
                break;
        }
        _crop = ToNorm(r);
        Invalidate();
    }

    /// <summary>Rectangle from a fixed corner to the pointer (either side of it), keeping the ratio if locked.</summary>
    static RectangleF FromAnchor(PointF a, PointF p, double ratio, float min, float dw, float dh)
    {
        float dx = p.X - a.X, dy = p.Y - a.Y;
        bool right = dx >= 0, down = dy >= 0;
        float availW = right ? dw - a.X : a.X, availH = down ? dh - a.Y : a.Y;
        float w = Math.Abs(dx), h = Math.Abs(dy);
        if (ratio > 0)
        {
            float rr = (float)ratio;
            w = Math.Max(w, h * rr);                                  // big enough to reach the pointer either way
            w = Math.Max(w, Math.Max(min, min * rr));
            w = Math.Min(w, Math.Min(availW, availH * rr));           // but inside the picture
            h = w / rr;
        }
        else
        {
            w = Math.Min(Math.Max(w, min), availW);
            h = Math.Min(Math.Max(h, min), availH);
        }
        return new RectangleF(right ? a.X : a.X - w, down ? a.Y : a.Y - h, w, h);
    }

    /// <summary>Moves one edge; with a locked ratio the other dimension follows, centred on the rectangle.</summary>
    static RectangleF FromEdge(CropPart edge, RectangleF s, PointF p, double ratio, float min, float dw, float dh)
    {
        float rr = (float)ratio;
        float x = s.X, y = s.Y, w = s.Width, h = s.Height;
        switch (edge)
        {
            case CropPart.E: w = Math.Clamp(p.X, s.Left + min, dw) - s.Left; break;
            case CropPart.W: x = Math.Clamp(p.X, 0, s.Right - min); w = s.Right - x; break;
            case CropPart.S: h = Math.Clamp(p.Y, s.Top + min, dh) - s.Top; break;
            case CropPart.N: y = Math.Clamp(p.Y, 0, s.Bottom - min); h = s.Bottom - y; break;
        }
        if (ratio <= 0) return new RectangleF(x, y, w, h);
        if (edge is CropPart.E or CropPart.W)
        {
            h = w / rr;
            if (h > dh) { h = dh; w = h * rr; if (edge == CropPart.W) x = s.Right - w; }
            y = Math.Clamp(s.Y + s.Height / 2 - h / 2, 0, dh - h);
        }
        else
        {
            w = h * rr;
            if (w > dw) { w = dw; h = w / rr; if (edge == CropPart.N) y = s.Bottom - h; }
            x = Math.Clamp(s.X + s.Width / 2 - w / 2, 0, dw - w);
        }
        return new RectangleF(x, y, w, h);
    }

    void EndCropDrag()
    {
        _cropPart = CropPart.None;
        _drag = Drag.None;
        Capture = false;
        Invalidate();
    }

    /// <summary>Turning the picture turns the crop with it; a locked ratio flips (16:9 ↔ 9:16).</summary>
    void RotateCrop(int degrees)
    {
        // A clockwise quarter turn moves (x, y) to (1 − y, x), as in RenderFrame.
        static RectangleF Cw(RectangleF c) => new(1 - c.Bottom, c.X, c.Height, c.Width);
        int turns = ((degrees / 90) % 4 + 4) % 4;
        for (int i = 0; i < turns; i++)
        {
            _crop = Cw(_crop);
            _cropBefore = Cw(_cropBefore);
        }
        double r = Aspects[_aspect].Ratio;
        if (r > 0 && turns % 2 == 1)
        {
            int flipped = Array.FindIndex(Aspects, a => a.Ratio > 0 && Math.Abs(a.Ratio - 1 / r) < 1e-6);
            _aspect = flipped >= 0 ? flipped : 0;
        }
    }

    // ───────────────────────── drawing ─────────────────────────

    void DrawCropOverlay(Graphics g)
    {
        var vr = VideoRectF;
        var c = CropWindowRect;
        using (var region = new Region(vr))
        {
            region.Exclude(c);
            using var dim = new SolidBrush(Color.FromArgb(165, 0, 0, 0));
            g.FillRegion(dim, region);
        }
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.None;
        using (var grid = new Pen(Color.FromArgb(_drag == Drag.Crop ? 120 : 60, 255, 255, 255), 1f))
            for (int i = 1; i <= 2; i++)
            {
                g.DrawLine(grid, c.Left + c.Width * i / 3, c.Top, c.Left + c.Width * i / 3, c.Bottom);
                g.DrawLine(grid, c.Left, c.Top + c.Height * i / 3, c.Right, c.Top + c.Height * i / 3);
            }
        using (var border = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1f, P(1.2f))))
            g.DrawRectangle(border, c.X, c.Y, c.Width, c.Height);
        g.Restore(st);

        // Handles: L brackets at the corners, short bars mid-edge
        float t = P(4), len = Math.Min(P(22), Math.Min(c.Width, c.Height) / 3);
        using var hb = new SolidBrush(Color.White);
        void Bar(float x, float y, float w, float h) => g.FillRectangle(hb, x, y, w, h);
        Bar(c.Left - t, c.Top - t, len + t, t); Bar(c.Left - t, c.Top - t, t, len + t);
        Bar(c.Right - len, c.Top - t, len + t, t); Bar(c.Right, c.Top - t, t, len + t);
        Bar(c.Left - t, c.Bottom, len + t, t); Bar(c.Left - t, c.Bottom - len, t, len + t);
        Bar(c.Right - len, c.Bottom, len + t, t); Bar(c.Right, c.Bottom - len, t, len + t);
        float mid = Math.Min(P(26), Math.Min(c.Width, c.Height) / 4);
        Bar(c.Left + c.Width / 2 - mid / 2, c.Top - t, mid, t);
        Bar(c.Left + c.Width / 2 - mid / 2, c.Bottom, mid, t);
        Bar(c.Left - t, c.Top + c.Height / 2 - mid / 2, t, mid);
        Bar(c.Right, c.Top + c.Height / 2 - mid / 2, t, mid);

        // Output size while dragging
        if (_drag == Drag.Crop)
        {
            var (ow, oh) = OutputSize();
            string label = $"{ow} × {oh}";
            var size = g.MeasureString(label, _timeFont);
            var r = new RectangleF(c.Left + c.Width / 2 - size.Width / 2 - P(10), c.Top + c.Height / 2 - size.Height / 2 - P(4),
                size.Width + P(20), size.Height + P(8));
            using (var path = RoundRect(r, r.Height / 2))
            using (var b = new SolidBrush(Color.FromArgb(210, 12, 17, 44))) g.FillPath(b, path);
            using var tb = new SolidBrush(Color.White);
            g.DrawString(label, _timeFont, tb, r.X + P(10), r.Y + P(4));
        }
    }

    void DrawCropBar(Graphics g)
    {
        var bar = CropBarRect;
        using (var path = RoundRect(bar, bar.Height / 2f)) Glass(g, path, bar, darken: 95);
        foreach (var (r, item, text) in CropBarItems())
        {
            bool selected = item == _aspect, hot = item == _hoverItem, done = item == DoneItem;
            Color fill = done ? (hot ? Theme.AccentHover : Theme.Accent)
                : selected ? Theme.BtnHover : hot ? Color.FromArgb(60, 255, 255, 255) : Color.Empty;
            if (fill != Color.Empty)
                using (var p = RoundRect(r, r.Height / 2))
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (selected && !done)
                using (var p = RoundRect(r, r.Height / 2))
                using (var pen = new Pen(Theme.AccentHover, P(1.5f))) g.DrawPath(pen, p);
            var col = done || selected ? Color.White : item == ResetItem ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(225, 255, 255, 255);
            TextRenderer.DrawText(g, text, _chipFont, Rectangle.Round(r), col,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        // separator before Reset
        var items = CropBarItems();
        var reset = items.First(i => i.Item == ResetItem).R;
        using var sep = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
        g.DrawLine(sep, reset.Left - P(7), reset.Top + P(6), reset.Left - P(7), reset.Bottom - P(6));
    }

    /// <summary>Crop icon: two interlocking right angles.</summary>
    void CropGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.8f);
        float a = r.Width * 0.22f, l = r.X + r.Width * 0.08f, t = r.Y + r.Height * 0.08f, rr = r.Right - r.Width * 0.08f, b = r.Bottom - r.Height * 0.08f;
        g.DrawLines(pen, new[] { new PointF(l + a, t), new PointF(l + a, b - a), new PointF(rr, b - a) });
        g.DrawLines(pen, new[] { new PointF(l, t + a), new PointF(rr - a, t + a), new PointF(rr - a, b) });
    }
}
