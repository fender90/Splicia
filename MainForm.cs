using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Borderless, fully custom-drawn main window: the video with a frosted-glass control overlay holding a filmstrip
/// timeline with yellow trim handles, playback, rotate and export controls.
/// </summary>
sealed partial class MainForm : Form
{
    enum Hit { None, Center, Timeline, TrimIn, TrimOut, TrimMove, Play, Prev, Next, SetIn, SetOut, RotL, RotR, Crop, CropBar, CropArea, Vol, VolSlider, Loop, Full, More, Export, Min, Max, Close }
    enum Drag { None, Seek, In, Out, Move, Volume, Crop }

    static readonly Hit[] HitOrder =
    {
        Hit.CropBar, Hit.Min, Hit.Max, Hit.Close, Hit.TrimIn, Hit.TrimOut, Hit.TrimMove, Hit.Timeline, Hit.Play, Hit.Prev, Hit.Next,
        Hit.SetIn, Hit.SetOut, Hit.RotL, Hit.RotR, Hit.Crop, Hit.Vol, Hit.VolSlider, Hit.Loop, Hit.Full, Hit.More, Hit.Export, Hit.Center,
        Hit.CropArea,
    };
    const double HideAfterSeconds = 2.5;
    static readonly Color TrimColor = Color.FromArgb(255, 199, 44);

    public const string VideoFilter =
        "Videos|*.mp4;*.m4v;*.mov;*.qt;*.mkv;*.webm;*.avi;*.wmv;*.asf;*.flv;*.f4v;*.mpg;*.mpeg;*.m2v;*.m2ts;*.mts;*.ts;*.3gp;*.3g2;*.ogv;*.vob;*.mxf;*.dv;*.divx|All files (*.*)|*.*";

    Playback? _pb;
    MediaInfo? _info;
    ThumbnailStrip? _strip;
    int _stripVersion;
    VideoFrame? _shown;          // frame on screen, unrotated (kept to re-render when the rotation changes)
    Bitmap? _bmp, _blur, _view, _film;
    bool _blurDirty;
    int _frameVersion;
    (int, RectangleF, Rectangle, Size) _viewKey;
    (int, int, int, int) _filmKey;

    double _pos, _in, _out;      // seconds: frame on screen, selection start / end
    int _rot;                    // the user's rotation, clockwise
    bool _loop, _muted, _playing, _stopAtOut;
    float _volume = 1f;
    Drag _drag, _snap; // _snap: a handle was just released; line its edge up with the exact frame when it arrives
    double _dragOffset; // pixels from the pointer to the grabbed handle's edge
    int _dragStartX;
    double _moveLength;  // selection length while dragging the selection along
    bool _moved;

    readonly Font _titleFont = new("Segoe UI Semibold", 12.5f);
    readonly Font _timeFont = new("Segoe UI Semibold", 10.5f);
    readonly Font _smallFont = new("Segoe UI", 8.5f);
    readonly Font _hintFont = new("Segoe UI", 11f);
    readonly Font _buttonFont = new("Segoe UI Semibold", 10f);
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 10 };
    readonly System.Diagnostics.Stopwatch _uptime = System.Diagnostics.Stopwatch.StartNew();
    readonly ContextMenuStrip _menu = new();

    Hit _hover, _pressed;
    double _hoverSince;
    bool _hintVisible;
    string? _toast;
    double _toastUntil;
    bool _cursorHidden, _fullscreen, _ignoreUp;
    double _lastActivity = double.NegativeInfinity;
    Point _lastMousePos = new(int.MinValue, int.MinValue);
    float _ui = 1f; // overlay opacity 0..1

    // Zoom / pan: _zoom is relative to fit-to-window, _center is the image point (0..1) at the view centre.
    const float MaxZoom = 32f;
    float _zoom = 1f;
    PointF _center = new(0.5f, 0.5f);
    bool _dragArmed, _panning;
    Point _dragStart;
    PointF _dragCenter;
    ulong _gestureDistance;
    Point _gesturePoint;
    Rectangle _restoreBounds;
    FormWindowState _restoreState;

    public MainForm(string? path)
    {
        Text = "Splicia";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        ClientSize = new Size(P(1100), P(700));
        MinimumSize = new Size(P(840), P(520));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;

        BuildMenu();
        CrashLog.Context = () => _info == null ? "no file open" :
            $"file '{_info.Path}' {_info.Width}x{_info.Height} {_info.VideoCodec} {_info.BitDepth}-bit rot={_info.Rotation} " +
            $"{_info.Fps:0.###} fps dur={_info.Duration:0.###} audio={_info.AudioCodec ?? "none"}; pos={_pos:0.###} in={_in:0.###} " +
            $"out={_out:0.###} userRot={_rot} playing={_playing} zoom={_zoom:0.##} client={ClientSize.Width}x{ClientSize.Height} " +
            $"dpi={DeviceDpi} fullscreen={_fullscreen}";
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } f) BeginInvoke(() => Open(f[0])); };

        if (path != null) Shown += (_, _) => Open(path);
    }

    double Now => _uptime.Elapsed.TotalSeconds;
    double Duration => _info is { Duration: > 0 } i ? i.Duration : 1;
    double FrameDur => _info?.FrameDuration ?? 1 / 30.0;
    int EffectiveRotation => ((_info?.Rotation ?? 0) + _rot) % 360;

    // ───────────────────────── window chrome ─────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000 | 0x00010000; // WS_MINIMIZEBOX | WS_MAXIMIZEBOX: taskbar minimize + snap
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetRoundedCorners(true);
        // Enable all touch gestures (pinch-zoom + pan in every direction) for touchscreens.
        try
        {
            var cfg = new GESTURECONFIG { dwID = 0, dwWant = 1, dwBlock = 0 };
            SetGestureConfig(Handle, 0, 1, new[] { cfg }, Marshal.SizeOf<GESTURECONFIG>());
        }
        catch { }
    }

    void SetRoundedCorners(bool round)
    {
        int pref = round ? 2 : 1; // DWMWCP_ROUND / DWMWCP_DONOTROUND (Windows 11; ignored elsewhere)
        try { DwmSetWindowAttribute(Handle, 33, ref pref, sizeof(int)); } catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_GETMINMAXINFO = 0x24, HTCLIENT = 1;
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if (m.Result == HTCLIENT)
            {
                long lp = m.LParam.ToInt64();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                m.Result = NcHitTest(p);
            }
            return;
        }
        if (m.Msg == WM_GETMINMAXINFO)
        {
            base.WndProc(ref m);
            // Keep maximized borderless window off the taskbar.
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            var mon = MonitorFromWindow(Handle, 2);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(mon, ref mi))
            {
                mmi.ptMaxPosition = new POINT { X = mi.rcWork.Left - mi.rcMonitor.Left, Y = mi.rcWork.Top - mi.rcMonitor.Top };
                mmi.ptMaxSize = new POINT { X = mi.rcWork.Right - mi.rcWork.Left, Y = mi.rcWork.Bottom - mi.rcWork.Top };
                Marshal.StructureToPtr(mmi, m.LParam, false);
            }
            return;
        }
        if (m.Msg == 0x00A0 /* WM_NCMOUSEMOVE: the top drag strip */ && !_dragArmed)
        {
            if (MousePosition != _lastMousePos) { _lastMousePos = MousePosition; Activity(); }
        }
        if (m.Msg == WM_GESTURE && HandleGesture(m.LParam))
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    const int WM_GESTURE = 0x0119;

    /// <summary>Touchscreen pinch-zoom and pan. (Precision-touchpad pinch arrives as Ctrl+wheel instead.)</summary>
    bool HandleGesture(IntPtr hGesture)
    {
        const int GID_ZOOM = 3, GID_PAN = 4, GF_BEGIN = 1;
        var gi = new GESTUREINFO { cbSize = Marshal.SizeOf<GESTUREINFO>() };
        if (!GetGestureInfo(hGesture, ref gi)) return false;
        var pt = PointToClient(new Point(gi.ptsLocation.x, gi.ptsLocation.y));
        bool begin = (gi.dwFlags & GF_BEGIN) != 0;
        bool handled = true;
        switch (gi.dwID)
        {
            case GID_ZOOM:
                if (!begin && _gestureDistance > 0 && gi.ullArguments > 0)
                    ZoomAt((float)gi.ullArguments / _gestureDistance, pt);
                _gestureDistance = gi.ullArguments;
                break;
            case GID_PAN:
                if (!begin && _zoom > 1f) PanBy(pt.X - _gesturePoint.X, pt.Y - _gesturePoint.Y);
                _gesturePoint = pt;
                break;
            default:
                handled = false; // GID_BEGIN / GID_END must go to DefWindowProc
                break;
        }
        if (handled) CloseGestureInfoHandle(hGesture);
        return handled;
    }

    IntPtr NcHitTest(Point p)
    {
        const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
            HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        if (_fullscreen) return HTCLIENT;
        if (WindowState == FormWindowState.Normal)
        {
            int b = P(6);
            bool l = p.X < b, r = p.X >= ClientSize.Width - b, t = p.Y < b, d = p.Y >= ClientSize.Height - b;
            if (t && l) return HTTOPLEFT;
            if (t && r) return HTTOPRIGHT;
            if (d && l) return HTBOTTOMLEFT;
            if (d && r) return HTBOTTOMRIGHT;
            if (l) return HTLEFT;
            if (r) return HTRIGHT;
            if (t) return HTTOP;
            if (d) return HTBOTTOM;
        }
        if (HitTest(p) != Hit.None) return HTCLIENT;
        if (p.Y < P(48)) return HTCAPTION; // drag strip along the top
        return HTCLIENT;
    }

    void ToggleMaximize()
    {
        if (_fullscreen) { ToggleFullscreen(); return; }
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _restoreState = WindowState;
            if (WindowState != FormWindowState.Normal) WindowState = FormWindowState.Normal;
            _restoreBounds = Bounds;
            _fullscreen = true;
            SetRoundedCorners(false);
            Bounds = Screen.FromHandle(Handle).Bounds;
        }
        else
        {
            _fullscreen = false;
            Bounds = _restoreBounds;
            WindowState = _restoreState;
            SetRoundedCorners(true);
        }
        Invalidate();
    }

    // ───────────────────────── layout ─────────────────────────

    int P(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    Rectangle PanelRect
    {
        get
        {
            int m = P(22), h = P(178);
            int w = Math.Min(ClientSize.Width - 2 * m, P(1180));
            return new Rectangle((ClientSize.Width - w) / 2, ClientSize.Height - m - h, w, h);
        }
    }

    int Pad => P(24);
    int HandleW => P(14);

    /// <summary>The filmstrip including the handle margins on both sides.</summary>
    Rectangle StripRect { get { var pr = PanelRect; return new Rectangle(pr.Left + Pad, pr.Top + P(66), pr.Width - 2 * Pad, P(46)); } }

    (float L, float W) Axis { get { var sr = StripRect; return (sr.Left + HandleW, sr.Width - 2 * HandleW); } }

    float X(double t) { var (l, w) = Axis; return l + (float)(Math.Clamp(t / Duration, 0, 1) * w); }

    double TimeAt(float x) { var (l, w) = Axis; return Math.Clamp((x - l) / w, 0, 1) * Duration; }

    Rectangle WindowButtonsRect
    {
        get { int w = P(46) * 3, h = P(34), m = P(12); return new Rectangle(ClientSize.Width - m - w, m, w, h); }
    }

    int ExportW => P(108);

    Rectangle Rect(Hit h)
    {
        var pr = PanelRect;
        int bs = P(36), rowY = pr.Top + P(146) - bs / 2, step = P(42);
        int left = pr.Left + Pad - P(8), right = pr.Right - Pad + P(8);
        var wb = WindowButtonsRect;
        var sr = StripRect;
        int d = P(78);
        int exportX = right - P(8) - ExportW, moreX = exportX - P(12) - bs;
        return h switch
        {
            Hit.Timeline => Rectangle.Inflate(sr, 0, P(6)),
            Hit.Play => new Rectangle(left, rowY, bs, bs),
            Hit.Prev => new Rectangle(left + step, rowY, bs, bs),
            Hit.Next => new Rectangle(left + 2 * step, rowY, bs, bs),
            Hit.SetIn => new Rectangle(left + 3 * step + P(16), rowY, bs, bs),
            Hit.SetOut => new Rectangle(left + 4 * step + P(16), rowY, bs, bs),
            Hit.RotL => new Rectangle(left + 5 * step + P(32), rowY, bs, bs),
            Hit.RotR => new Rectangle(left + 6 * step + P(32), rowY, bs, bs),
            Hit.Crop => new Rectangle(left + 7 * step + P(32), rowY, bs, bs),
            Hit.Export => new Rectangle(exportX, rowY, ExportW, bs),
            Hit.More => new Rectangle(moreX, rowY, bs, bs),
            Hit.Full => new Rectangle(moreX - step, rowY, bs, bs),
            Hit.Loop => new Rectangle(moreX - 2 * step, rowY, bs, bs),
            Hit.VolSlider => new Rectangle(moreX - 2 * step - P(14) - P(96), rowY, P(96), bs),
            Hit.Vol => new Rectangle(moreX - 2 * step - P(14) - P(96) - bs - P(2), rowY, bs, bs),
            Hit.Min => new Rectangle(wb.Left, wb.Top, wb.Width / 3, wb.Height),
            Hit.Max => new Rectangle(wb.Left + wb.Width / 3, wb.Top, wb.Width / 3, wb.Height),
            Hit.Close => new Rectangle(wb.Left + 2 * wb.Width / 3, wb.Top, wb.Width - 2 * wb.Width / 3, wb.Height),
            Hit.Center => new Rectangle((ClientSize.Width - d) / 2, (PanelRect.Top - d) / 2, d, d),
            _ => Rectangle.Empty,
        };
    }

    static bool NeedsFile(Hit h) => h is not (Hit.None or Hit.Center or Hit.More or Hit.Full or Hit.Min or Hit.Max or Hit.Close);

    Hit HitTest(Point p)
    {
        if (_ui < 0.5f) return Hit.None;
        foreach (var h in HitOrder)
        {
            if (h == Hit.CropBar)
            {
                if (_cropping && CropBarRect.Contains(p)) return h;
                continue;
            }
            if (h == Hit.CropArea)
            {
                if (_cropping && CropPartAt(p) != CropPart.None) return h;
                continue;
            }
            if (h == Hit.Center)
            {
                if (_info != null && (_playing || _cropping)) continue; // the big button only shows while paused
                var c = Rect(h);
                float dx = p.X - (c.Left + c.Width / 2f), dy = p.Y - (c.Top + c.Height / 2f);
                if (dx * dx + dy * dy <= c.Width * c.Width / 4f) return h;
            }
            else if (h is Hit.TrimIn or Hit.TrimOut or Hit.TrimMove)
            {
                if (_info == null) continue;
                var t = TrimHit(p);
                if (t == h) return h;
            }
            else if (Rect(h).Contains(p) && (_info != null || !NeedsFile(h))) return h; // inactive without a file
        }
        return Hit.None;
    }

    /// <summary>
    /// Which part of the selection frame is under <paramref name="p"/>: a handle (when they touch, the side of the
    /// pointer decides), or the grip tab / top and bottom bars that move the whole selection.
    /// </summary>
    Hit TrimHit(Point p)
    {
        var sr = StripRect;
        if (p.Y < sr.Top - P(8) || p.Y > sr.Bottom + P(14)) return Hit.None;
        float xi = X(_in), xo = X(_out), hw = HandleW;
        if (Rectangle.Inflate(Rectangle.Round(GripRect), P(5), P(3)).Contains(p)) return Hit.TrimMove;
        if (p.Y > sr.Bottom + P(8)) return Hit.None;
        bool onIn = p.X >= xi - hw - P(4) && p.X <= xi + P(3);
        bool onOut = p.X >= xo - P(3) && p.X <= xo + hw + P(4);
        if (onIn && onOut) return p.X < (xi + xo) / 2 ? Hit.TrimIn : Hit.TrimOut;
        if (onIn) return Hit.TrimIn;
        if (onOut) return Hit.TrimOut;
        bool onBars = p.X > xi && p.X < xo && (p.Y < sr.Top + P(5) || p.Y > sr.Bottom - P(5));
        return onBars ? Hit.TrimMove : Hit.None;
    }

    /// <summary>The tab hanging under the middle of the selection, for dragging the selection along.</summary>
    RectangleF GripRect
    {
        get
        {
            var sr = StripRect;
            float w = P(38), h = P(11), cx = (X(_in) + X(_out)) / 2;
            return new RectangleF(cx - w / 2, sr.Bottom - P(1), w, h);
        }
    }

    RectangleF VideoRectF
    {
        get
        {
            if (_bmp == null) return RectangleF.Empty;
            // The part of the frame shown (the crop, or all of it while cropping), fitted to the window or, in crop mode,
            // to the space between the aspect bar and the control panel.
            var src = SourceRect;
            var area = _cropping ? CropEditArea : new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
            float s = Math.Min(area.Width / src.Width, area.Height / src.Height) * _zoom;
            float w = src.Width * s, h = src.Height * s;
            return new RectangleF(area.X + area.Width / 2f - _center.X * w, area.Y + area.Height / 2f - _center.Y * h, w, h);
        }
    }

    // ───────────────────────── zoom / pan ─────────────────────────

    /// <summary>Keeps the image covering the window: centred on an axis where it fits, edge-clamped where it overflows.</summary>
    void ClampCenter()
    {
        var vr = VideoRectF;
        if (vr.Width <= 0) return;
        float hx = ClientSize.Width / 2f / vr.Width, hy = ClientSize.Height / 2f / vr.Height;
        _center.X = hx >= 0.5f ? 0.5f : Math.Clamp(_center.X, hx, 1 - hx);
        _center.Y = hy >= 0.5f ? 0.5f : Math.Clamp(_center.Y, hy, 1 - hy);
    }

    /// <summary>Zooms by <paramref name="factor"/>, keeping the image point under <paramref name="anchor"/> fixed.</summary>
    void ZoomAt(float factor, Point anchor)
    {
        if (_bmp == null || _cropping) return;
        var vr = VideoRectF;
        float u = (anchor.X - vr.X) / vr.Width, v = (anchor.Y - vr.Y) / vr.Height;
        float z = Math.Clamp(_zoom * factor, 1f, MaxZoom);
        if (Math.Abs(z - 1f) < 0.02f) z = 1f;
        if (z == _zoom) return;
        _zoom = z;
        var nr = VideoRectF;
        _center = new PointF(u - (anchor.X - ClientSize.Width / 2f) / nr.Width, v - (anchor.Y - ClientSize.Height / 2f) / nr.Height);
        ClampCenter();
        Invalidate();
    }

    void ZoomCentered(float factor) => ZoomAt(factor, new Point(ClientSize.Width / 2, ClientSize.Height / 2));

    void ResetZoom()
    {
        _zoom = 1f;
        _center = new PointF(0.5f, 0.5f);
        Invalidate();
    }

    void PanBy(float dx, float dy)
    {
        var vr = VideoRectF;
        if (vr.Width <= 0) return;
        _center = new PointF(_center.X - dx / vr.Width, _center.Y - dy / vr.Height);
        ClampCenter();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampCenter();
    }

    // ───────────────────────── painting ─────────────────────────

    Color C(int a, int r, int g, int b) => Color.FromArgb(Math.Clamp((int)(a * _ui), 0, 255), r, g, b);
    Color C(int a, Color c) => C(a, c.R, c.G, c.B);
    Color W(int a) => C(a, 255, 255, 255);

    // Safety net against re-entrancy while a frame is being rendered (bitmaps are locked then). NoPump.For keeps the UI
    // thread from pumping messages during that work, so this should never trigger.
    int _busy;
    bool _repaintPending, _paintFailed;

    void EndBusy()
    {
        if (--_busy == 0 && _repaintPending)
        {
            _repaintPending = false;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_busy > 0) { _repaintPending = true; return; }
        _busy++;
        try { PaintCore(e); }
        catch (Exception ex)
        {
            // Never let one bad paint leave WinForms' permanent red-X "paint failed" state; rebuild caches next time.
            CrashLog.Write(ex, "Paint error (recovered)");
            _viewKey = default;
            _filmKey = default;
            _blurDirty = true;
            if (!_paintFailed) { _paintFailed = true; BeginInvoke(() => { _paintFailed = false; Invalidate(); }); }
        }
        finally { EndBusy(); }
    }

    void PaintCore(PaintEventArgs e)
    {
        var g = e.Graphics;

        if (_bmp != null && ClientSize.Width > 0 && ClientSize.Height > 0)
        {
            // The scaled frame is cached; hover/fade repaints just copy it 1:1.
            RenderView();
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            var clip = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, _view!.Size));
            g.DrawImage(_view, clip, clip.X, clip.Y, clip.Width, clip.Height, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
        }
        else
        {
            using var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(18, 22, 34), Color.FromArgb(6, 8, 12), 90f);
            g.FillRectangle(bg, ClientRectangle);
            if (_info == null)
                TextRenderer.DrawText(g, "Drop a video here, or click the button to browse", _hintFont,
                    new Rectangle(0, Rect(Hit.Center).Bottom + P(10), ClientSize.Width, P(40)), Color.FromArgb(140, 150, 165),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (_cropping && _bmp != null) DrawCropOverlay(g);
        if (_toast != null && Now < _toastUntil) DrawToast(g);
        if (_ui <= 0.01f) return;
        if (_blurDirty) UpdateBlur();

        // Window buttons
        var wb = WindowButtonsRect;
        using (var path = RoundRect(wb, wb.Height / 2f)) Glass(g, path, wb, darken: 125);
        foreach (var h in new[] { Hit.Min, Hit.Max, Hit.Close })
        {
            var r = Rect(h);
            if (_hover == h)
            {
                var st = g.Save();
                using (var clip = RoundRect(wb, wb.Height / 2f)) g.SetClip(clip);
                using var hb = new SolidBrush(h == Hit.Close ? C(220, 220, 50, 50) : W(40));
                g.FillRectangle(hb, r);
                g.Restore(st);
            }
            DrawWindowGlyph(g, h, r);
        }

        // Centre button: "add video" (browse) until a file is open, then play while paused
        if (_info == null || !_playing && !_cropping)
        {
            var cr = Rect(Hit.Center);
            using (var cp = new GraphicsPath()) { cp.AddEllipse(cr); Glass(g, cp, cr, _hover == Hit.Center ? 40 : 60); }
            var gr = RectangleF.Inflate(cr, -cr.Width * 0.3f, -cr.Height * 0.3f);
            if (_info == null) AddVideoGlyph(g, RectangleF.Inflate(cr, -cr.Width * 0.2f, -cr.Height * 0.2f), W(245));
            else PlayGlyph(g, new RectangleF(gr.X + gr.Width * 0.08f, gr.Y, gr.Width, gr.Height), W(245));
        }

        // Control panel
        var pr = PanelRect;
        using (var pp = RoundRect(pr, P(14))) Glass(g, pp, pr, darken: 135); // darker than the other glass: text and trim frame stay legible over bright video
        DrawPanelContents(g, pr);
        if (_cropping) DrawCropBar(g);
        if (_hintVisible && _hover != Hit.None && _drag == Drag.None) DrawHint(g, _hover);
    }

    void DrawPanelContents(Graphics g, Rectangle pr)
    {
        using var titleBrush = new SolidBrush(W(245));
        using var subBrush = new SolidBrush(W(160));
        using var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        string title = _info != null ? Path.GetFileName(_info.Path) : "Splicia";
        string time = _info != null ? $"{Fmt(_pos)} / {Fmt(Duration)}" : "";
        string sub = "No video loaded";
        string sel = "";
        if (_info != null)
        {
            var (dw, dh) = HasCrop ? OutputSize() : _info.DisplaySize(_rot);
            sub = $"{dw}×{dh}{(HasCrop ? " (cropped)" : "")}  ·  {_info.Fps:0.##} fps  ·  {_info.VideoCodec}";
            if (_info.IsHdr) sub += _info.IsHlg ? " HDR (HLG)" : " HDR10";
            else if (_info.BitDepth > 8) sub += $" {_info.BitDepth}-bit";
            sub += _info.HasAudio ? $"  ·  {_info.AudioCodec}" : "  ·  no sound";
            if (_rot != 0) sub += $"  ·  rotated {(_rot == 270 ? "90° left" : _rot == 90 ? "90° right" : "180°")}";
            if (_zoom > 1f) sub += $"  ·  zoom {_zoom * 100:0}%";
            sel = $"Selection {Fmt(_in)} – {Fmt(_out)}   ({Length(_out - _in)})";
        }

        float timeW = g.MeasureString(time, _timeFont).Width, selW = g.MeasureString(sel, _smallFont).Width;
        g.DrawString(title, _titleFont, titleBrush, new RectangleF(pr.Left + Pad, pr.Top + P(14), pr.Width - 2 * Pad - timeW - P(16), P(26)), sf);
        g.DrawString(time, _timeFont, titleBrush, pr.Right - Pad - timeW, pr.Top + P(16));
        g.DrawString(sub, _smallFont, subBrush, new RectangleF(pr.Left + Pad, pr.Top + P(41), pr.Width - 2 * Pad - selW - P(16), P(18)), sf);
        using (var selBrush = new SolidBrush(C(230, TrimColor))) g.DrawString(sel, _smallFont, selBrush, pr.Right - Pad - selW, pr.Top + P(41));

        DrawTimeline(g);

        bool has = _info != null;
        DrawVolumeSlider(g, has);
        foreach (var h in new[] { Hit.Play, Hit.Prev, Hit.Next, Hit.SetIn, Hit.SetOut, Hit.RotL, Hit.RotR, Hit.Crop, Hit.Vol, Hit.Loop, Hit.Full, Hit.More })
        {
            var r = Rect(h);
            bool enabled = has || h is Hit.More or Hit.Full;
            if (_hover == h && enabled)
                using (var hb = new SolidBrush(W(34))) g.FillEllipse(hb, r);
            var col = !enabled ? W(70) : h == Hit.Loop && !_loop ? W(110) : W(240);
            var gr = RectangleF.Inflate(r, -r.Width * 0.24f, -r.Height * 0.24f);
            switch (h)
            {
                case Hit.Play: if (_playing) PauseGlyph(g, gr, col); else PlayGlyph(g, gr, col); break;
                case Hit.Prev: StepGlyph(g, gr, col, forward: false); break;
                case Hit.Next: StepGlyph(g, gr, col, forward: true); break;
                case Hit.SetIn: BracketGlyph(g, gr, enabled && _hover == h ? C(255, TrimColor) : col, start: true); break;
                case Hit.SetOut: BracketGlyph(g, gr, enabled && _hover == h ? C(255, TrimColor) : col, start: false); break;
                case Hit.RotL: RotateGlyph(g, gr, col, clockwise: false); break;
                case Hit.RotR: RotateGlyph(g, gr, col, clockwise: true); break;
                case Hit.Crop: CropGlyph(g, gr, enabled && (HasCrop || _cropping) ? C(255, Theme.AccentHover) : col); break;
                case Hit.Vol: SpeakerGlyph(g, gr, col, _muted || _volume == 0); break;
                case Hit.Loop: LoopGlyph(g, gr, col); break;
                case Hit.Full: FullGlyph(g, gr, col, _fullscreen); break;
                case Hit.More: MoreGlyph(g, gr, col); break;
            }
        }

        // Export: the one accent-coloured button
        var er = Rect(Hit.Export);
        bool down = _pressed == Hit.Export && _hover == Hit.Export, hot = _hover == Hit.Export;
        using (var ep = RoundRect(er, er.Height / 2f))
        {
            if (!has)
                using (var eb = new SolidBrush(C(90, Theme.Btn))) g.FillPath(eb, ep);
            else
                // The logo's blue → violet, like the setup and export windows' main buttons
                using (var eb = new LinearGradientBrush(RectangleF.Inflate(er, 1, 1),
                           C(255, down ? Theme.AccentDown : hot ? Theme.AccentHover : Theme.Accent),
                           C(255, down ? Theme.VioletDown : hot ? Theme.VioletHover : Theme.Violet), 0f))
                    g.FillPath(eb, ep);
        }
        var textCol = has ? W(255) : W(110);
        var tsize = g.MeasureString("Export", _buttonFont);
        float icon = P(14), gap = P(7), total = icon + gap + tsize.Width, x0 = er.Left + (er.Width - total) / 2;
        ExportGlyph(g, new RectangleF(x0, er.Top + (er.Height - icon) / 2f, icon, icon), textCol);
        using (var tb = new SolidBrush(textCol))
            g.DrawString("Export", _buttonFont, tb, x0 + icon + gap - P(2), er.Top + (er.Height - tsize.Height) / 2f + P(0.5f));
    }

    // ───────────────────────── timeline ─────────────────────────

    void DrawTimeline(Graphics g)
    {
        var sr = StripRect;
        var (axL, axW) = Axis;
        var film = new RectangleF(axL, sr.Top, axW, sr.Height);
        using var filmPath = RoundRect(film, P(5));

        // Filmstrip (cached bitmap, rebuilt when pictures arrive or the rotation / size changes)
        var st = g.Save();
        g.SetClip(filmPath, CombineMode.Intersect);
        using (var bg = new SolidBrush(W(_info == null ? 18 : 28))) g.FillRectangle(bg, film);
        if (_info != null)
        {
            UpdateFilm((int)Math.Round(axW), sr.Height);
            if (_film != null) DrawFaded(g, _film, new RectangleF(axL, sr.Top, _film.Width, _film.Height));
            // Outside the selection: darkened
            float xi = X(_in), xo = X(_out);
            using var dim = new SolidBrush(C(165, 0, 0, 0));
            if (xi > axL) g.FillRectangle(dim, axL, sr.Top, xi - axL, sr.Height);
            if (xo < axL + axW) g.FillRectangle(dim, xo, sr.Top, axL + axW - xo, sr.Height);
        }
        g.Restore(st);
        if (_info == null) return;

        // Selection frame: a yellow ring around the selection, thick at the ends where the grips are
        {
            float xi = X(_in), xo = X(_out), hw = HandleW, t = P(3);
            using var frame = RoundRect(new RectangleF(xi - hw, sr.Top, xo - xi + 2 * hw, sr.Height), P(5));
            frame.AddRectangle(new RectangleF(xi, sr.Top + t, Math.Max(0, xo - xi), sr.Height - 2 * t));
            frame.FillMode = FillMode.Alternate;
            using var yb = new SolidBrush(C(255, TrimColor));
            g.FillPath(yb, frame);
            bool hotIn = _hover == Hit.TrimIn || _drag == Drag.In, hotOut = _hover == Hit.TrimOut || _drag == Drag.Out;
            Chevron(g, xi - hw / 2f, sr.Top + sr.Height / 2f, left: true, hotIn);
            Chevron(g, xo + hw / 2f, sr.Top + sr.Height / 2f, left: false, hotOut);

            // Grip tab for dragging the whole selection
            bool hotMove = _hover == Hit.TrimMove || _drag == Drag.Move;
            var grip = GripRect;
            using (var gp = RoundRect(grip, P(4)))
            using (var gb = new SolidBrush(hotMove ? C(255, 255, 222, 120) : C(255, TrimColor))) g.FillPath(gb, gp);
            using var dot = new SolidBrush(C(255, 60, 44, 0));
            float dr = P(1.6f), dy = grip.Top + grip.Height / 2f + P(0.5f);
            for (int i = -1; i <= 1; i++) g.FillEllipse(dot, grip.Left + grip.Width / 2f + i * P(6) - dr, dy - dr, 2 * dr, 2 * dr);
        }

        // Playhead
        {
            float x = X(_pos), top = sr.Top - P(5), bottom = sr.Bottom + P(5);
            using (var shadow = new Pen(C(90, 0, 0, 0), P(5)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(shadow, x, top, x, bottom);
            using (var pen = new Pen(W(255), P(3)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, x, top, x, bottom);
        }

        // Time label while dragging
        if (_drag is Drag.Seek or Drag.In or Drag.Out || _drag == Drag.Move && _moved)
        {
            double t = _drag == Drag.In ? _in : _drag == Drag.Out ? _out : _pos;
            float x = _drag switch { Drag.In => X(_in), Drag.Out => X(_out), Drag.Move => (X(_in) + X(_out)) / 2, _ => X(_pos) };
            string label = _drag == Drag.Move ? $"{Fmt(_in)} – {Fmt(_out)}" : Fmt(t);
            var size = g.MeasureString(label, _timeFont);
            var r = new RectangleF(x - size.Width / 2 - P(9), sr.Top - P(12) - size.Height - P(6), size.Width + P(18), size.Height + P(6));
            r.X = Math.Clamp(r.X, PanelRect.Left + P(6), PanelRect.Right - P(6) - r.Width);
            using (var path = RoundRect(r, r.Height / 2))
            using (var b = new SolidBrush(_drag == Drag.Seek ? C(235, 245, 246, 250) : C(255, TrimColor))) g.FillPath(b, path);
            using var tb = new SolidBrush(C(255, 20, 20, 24));
            g.DrawString(label, _timeFont, tb, r.X + P(9), r.Y + P(3));
        }
    }

    void Chevron(Graphics g, float cx, float cy, bool left, bool hot)
    {
        float s = P(hot ? 5.5f : 4.5f);
        using var pen = new Pen(C(255, 60, 44, 0), P(2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float d = left ? 1 : -1;
        g.DrawLines(pen, new[] { new PointF(cx + d * s * 0.5f, cy - s), new PointF(cx - d * s * 0.5f, cy), new PointF(cx + d * s * 0.5f, cy + s) });
    }

    void DrawFaded(Graphics g, Image img, RectangleF dest)
    {
        if (_ui >= 0.999f) { g.DrawImage(img, dest); return; }
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix { Matrix33 = _ui });
        g.DrawImage(img, Rectangle.Round(dest), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
    }

    /// <summary>Lays the thumbnails out as tiles across the timeline, at the displayed aspect and rotation.</summary>
    void UpdateFilm(int w, int h)
    {
        if (_strip == null || _info == null || w <= 0 || h <= 0) return;
        var key = (_stripVersion, EffectiveRotation, w, h);
        if (_film != null && key == _filmKey) return;
        _filmKey = key;
        if (_film == null || _film.Width != w || _film.Height != h)
        {
            _film?.Dispose();
            _film = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        }
        using var g = Graphics.FromImage(_film);
        g.Clear(Color.FromArgb(255, 30, 32, 38));
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var (dw, dh) = _info.DisplaySize(_rot);
        float tileW = Math.Max(P(14), h * (float)dw / Math.Max(1, dh));
        int n = (int)Math.Ceiling(w / tileW);
        int rot = EffectiveRotation;
        for (int i = 0; i < n; i++)
        {
            float x = i * tileW;
            double center = Math.Clamp((x + tileW / 2) / w, 0, 1);
            var thumb = _strip.Nearest((int)(center * ThumbnailStrip.Count));
            if (thumb == null) continue;
            DrawRotated(g, thumb, new RectangleF(x, 0, tileW, h), rot);
        }
        // hairline between tiles
        using var sep = new Pen(Color.FromArgb(70, 0, 0, 0), 1f);
        for (int i = 1; i < n; i++) g.DrawLine(sep, i * tileW, 0, i * tileW, h);
    }

    static void DrawRotated(Graphics g, Image img, RectangleF dest, int rot)
    {
        var st = g.Save();
        g.SetClip(dest, CombineMode.Intersect);
        g.TranslateTransform(dest.X + dest.Width / 2, dest.Y + dest.Height / 2);
        g.RotateTransform(rot);
        float w = rot % 180 == 0 ? dest.Width : dest.Height, h = rot % 180 == 0 ? dest.Height : dest.Width;
        // cover the tile (crop rather than letterbox)
        float s = Math.Max(w / img.Width, h / img.Height);
        float iw = img.Width * s, ih = img.Height * s;
        g.DrawImage(img, -iw / 2, -ih / 2, iw, ih);
        g.Restore(st);
    }

    // ───────────────────────── other overlay bits ─────────────────────────

    void DrawVolumeSlider(Graphics g, bool enabled)
    {
        var r = Rect(Hit.VolSlider);
        float cy = r.Top + r.Height / 2f, l = r.Left + P(6), w = r.Width - P(12);
        bool active = enabled && (_hover == Hit.VolSlider || _drag == Drag.Volume);
        float v = _muted ? 0 : _volume;
        float th = active ? P(4) : P(2), x = l + v * w;
        using (var track = RoundRect(new RectangleF(l, cy - th / 2, w, th), th / 2))
        using (var tb = new SolidBrush(W(enabled ? 60 : 30))) g.FillPath(tb, track);
        if (x > l)
            using (var done = RoundRect(new RectangleF(l, cy - th / 2, x - l, th), th / 2))
            using (var db = new SolidBrush(W(enabled ? 225 : 70))) g.FillPath(db, done);
        if (enabled)
        {
            float dr = active ? P(7) : P(5);
            using var kb = new SolidBrush(W(255));
            g.FillEllipse(kb, x - dr, cy - dr, 2 * dr, 2 * dr);
        }
    }

    string? HintText(Hit h) => h switch
    {
        Hit.Play => _playing ? "Pause  (Space)" : "Play  (Space)",
        Hit.Prev => "Previous frame  (←)",
        Hit.Next => "Next frame  (→)",
        Hit.SetIn => "Start selection here  (I)",
        Hit.SetOut => "End selection here  (O)",
        Hit.RotL => "Rotate left  (Shift+R)",
        Hit.RotR => "Rotate right  (R)",
        Hit.Crop => _cropping ? "Finish cropping  (C or Enter)" : HasCrop ? "Change the crop  (C)" : "Crop  (C)",
        Hit.Vol => _muted ? "Unmute  (M)" : "Mute  (M)",
        Hit.VolSlider => "Volume  (↑ / ↓)",
        Hit.Loop => _loop ? "Loop selection: on" : "Loop selection: off",
        Hit.Full => _fullscreen ? "Exit fullscreen  (F)" : "Fullscreen  (F)",
        Hit.More => "More",
        Hit.Export => "Export the selection  (Ctrl+E)",
        Hit.TrimIn => "Drag to set the start",
        Hit.TrimOut => "Drag to set the end",
        Hit.TrimMove => "Drag to move the selection",
        _ => null,
    };

    void DrawHint(Graphics g, Hit h)
    {
        if (HintText(h) is not string text) return;
        var anchor = h is Hit.TrimIn ? new RectangleF(X(_in) - HandleW, StripRect.Top, HandleW, 1)
            : h is Hit.TrimOut ? new RectangleF(X(_out), StripRect.Top, HandleW, 1)
            : h is Hit.TrimMove ? new RectangleF((X(_in) + X(_out)) / 2, StripRect.Top, 1, 1) : Rect(h);
        if (h is Hit.Min or Hit.Max or Hit.Close or Hit.Timeline or Hit.Center) return;
        var size = g.MeasureString(text, _smallFont);
        var r = new RectangleF(anchor.X + anchor.Width / 2 - size.Width / 2 - P(10), anchor.Top - P(10) - size.Height - P(8),
            size.Width + P(20), size.Height + P(8));
        r.X = Math.Clamp(r.X, P(8), ClientSize.Width - P(8) - r.Width);
        using (var path = RoundRect(r, r.Height / 2))
        {
            using var b = new SolidBrush(C(235, 12, 17, 44));
            g.FillPath(b, path);
            using var border = new Pen(W(50), 1f);
            g.DrawPath(border, path);
        }
        using var tb = new SolidBrush(W(235));
        g.DrawString(text, _smallFont, tb, r.X + P(10), r.Y + P(4));
    }

    void ShowToast(string text)
    {
        _toast = text;
        _toastUntil = Now + 1.4;
        Invalidate();
    }

    void DrawToast(Graphics g)
    {
        float a = (float)Math.Clamp((_toastUntil - Now) / 0.3, 0, 1);
        var size = g.MeasureString(_toast, _timeFont);
        var r = new RectangleF((ClientSize.Width - size.Width) / 2 - P(16), P(20), size.Width + P(32), size.Height + P(14));
        using (var path = RoundRect(r, r.Height / 2))
        using (var b = new SolidBrush(Color.FromArgb((int)(210 * a), 12, 17, 44))) g.FillPath(b, path);
        using var tb = new SolidBrush(Color.FromArgb((int)(245 * a), 255, 255, 255));
        g.DrawString(_toast, _timeFont, tb, r.X + P(16), r.Y + P(7));
    }

    internal static string Fmt(double s)
    {
        if (s < 0 || double.IsNaN(s)) s = 0;
        long cs = (long)Math.Floor(s * 100 + 1e-6);
        long h = cs / 360000, m = cs / 6000 % 60, sec = cs / 100 % 60, f = cs % 100;
        return h > 0 ? $"{h}:{m:00}:{sec:00}.{f:00}" : $"{m}:{sec:00}.{f:00}";
    }

    internal static string Length(double s) => s < 60 ? $"{s:0.00} s" : Fmt(s);

    /// <summary>
    /// Frosted glass in the brand colours: blurred video behind the shape, the logo's night-sky navy and an indigo tint,
    /// a soft top highlight and a hairline blue border.
    /// </summary>
    void Glass(Graphics g, GraphicsPath path, Rectangle bounds, int tint = 70, int darken = 0)
    {
        var st = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var black = new SolidBrush(C(200, Theme.Bg))) g.FillRectangle(black, bounds);
        if (_blur != null)
        {
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            if (_ui < 1f) ia.SetColorMatrix(new ColorMatrix { Matrix33 = _ui }); // colour matrix is the slow path; only while fading
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            var vr = VideoRectF;
            var dest = Rectangle.Round(RectangleF.Intersect(vr, bounds));
            if (!dest.IsEmpty)
            {
                float sx = _blur.Width / vr.Width, sy = _blur.Height / vr.Height;
                g.DrawImage(_blur, dest, (dest.X - vr.X) * sx, (dest.Y - vr.Y) * sy, dest.Width * sx, dest.Height * sy, GraphicsUnit.Pixel, ia);
            }
        }
        if (darken > 0) using (var dk = new SolidBrush(C(darken, Theme.Bg))) g.FillRectangle(dk, bounds);
        using (var t = new SolidBrush(C(tint, 46, 58, 140))) g.FillRectangle(t, bounds);
        using (var hl = new LinearGradientBrush(new Rectangle(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height + 2), W(30), W(6), 90f))
            g.FillRectangle(hl, bounds);
        g.Restore(st);
        using var border = new Pen(C(70, 130, 160, 255), 1f);
        g.DrawPath(border, path);
    }

    /// <summary>Builds the tiny image the glass blur is stretched from, by averaging a sparse grid of samples per cell.</summary>
    unsafe void UpdateBlur()
    {
        _blurDirty = false;
        if (_bmp == null) return;
        var src = SourceRect;
        int bw = 40, bh = Math.Max(2, (int)Math.Round(bw * src.Height / (double)src.Width));
        if (_blur == null || _blur.Width != bw || _blur.Height != bh)
        {
            _blur?.Dispose();
            _blur = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
        }
        int sw = _bmp.Width, sh = _bmp.Height;
        const int N = 5; // samples per cell axis
        BitmapData? sd = null, bd = null;
        try
        {
            sd = _bmp.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            bd = _blur.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int by = 0; by < bh; by++)
            {
                uint* outRow = (uint*)((byte*)bd.Scan0 + by * bd.Stride);
                for (int bx = 0; bx < bw; bx++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int j = 0; j < N; j++)
                    {
                        int y = src.Y + Math.Min(src.Height - 1, (int)((by + (j + 0.5) / N) * src.Height / bh));
                        uint* row = (uint*)((byte*)sd.Scan0 + (long)y * sd.Stride);
                        for (int i = 0; i < N; i++)
                        {
                            uint p = row[src.X + Math.Min(src.Width - 1, (int)((bx + (i + 0.5) / N) * src.Width / bw))];
                            r += (int)(p >> 16) & 0xFF; g += (int)(p >> 8) & 0xFF; b += (int)p & 0xFF;
                        }
                    }
                    r /= N * N; g /= N * N; b /= N * N;
                    outRow[bx] = 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
                }
            }
        }
        finally
        {
            if (sd != null) _bmp.UnlockBits(sd);
            if (bd != null) _blur.UnlockBits(bd);
        }
    }

    /// <summary>Copies the frame on screen into <see cref="_bmp"/>, applying the file's rotation plus the user's.</summary>
    unsafe void RenderFrame()
    {
        var f = _shown;
        if (f == null) return;
        int rot = EffectiveRotation, w = f.Width, h = f.Height;
        int bw = rot % 180 == 0 ? w : h, bh = rot % 180 == 0 ? h : w;
        if (_bmp == null || _bmp.Width != bw || _bmp.Height != bh)
        {
            _bmp?.Dispose();
            _bmp = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
            ClampCenter();
        }
        _busy++;
        BitmapData? bd = null;
        try
        {
            bd = _bmp.LockBits(new Rectangle(0, 0, bw, bh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            IntPtr dp = bd.Scan0;
            int ds = bd.Stride;
            fixed (byte* srcBytes = f.Data)
            {
                IntPtr sp = (IntPtr)srcBytes;
                NoPump.For(bh, y =>
                {
                    uint* s = (uint*)sp;
                    uint* d = (uint*)((byte*)dp + (long)y * ds);
                    switch (rot)
                    {
                        case 0:
                            Buffer.MemoryCopy(s + (long)y * w, d, (long)bw * 4, (long)bw * 4);
                            break;
                        case 90: // destination (x, y) ← source (y, h-1-x)
                            for (int x = 0; x < bw; x++) d[x] = s[(long)(h - 1 - x) * w + y];
                            break;
                        case 180:
                        {
                            uint* row = s + (long)(h - 1 - y) * w;
                            for (int x = 0; x < bw; x++) d[x] = row[w - 1 - x];
                            break;
                        }
                        default: // 270: destination (x, y) ← source (w-1-y, x)
                            for (int x = 0; x < bw; x++) d[x] = s[(long)x * w + (w - 1 - y)];
                            break;
                    }
                });
            }
        }
        finally
        {
            if (bd != null) _bmp.UnlockBits(bd);
            EndBusy();
        }
        _frameVersion++;
        _blurDirty = true;
        Invalidate();
    }

    /// <summary>
    /// Renders the visible part of the frame into a window-sized cache (parallel; nearest-neighbour when magnified,
    /// bilinear near 1:1, box-filtered when shrinking). Skipped when frame, zoom, pan and size are unchanged.
    /// </summary>
    unsafe void RenderView()
    {
        if (_bmp == null) return;
        var size = ClientSize;
        if (_view == null || _view.Size != size)
        {
            _view?.Dispose();
            _view = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            _viewKey = default;
        }
        var vr = VideoRectF;
        var src = SourceRect;
        var key = (_frameVersion, vr, src, size);
        if (key == _viewKey) return;
        _viewKey = key;

        int W = size.Width, H = size.Height, sw = _bmp.Width, sh = _bmp.Height;
        float scale = vr.Width / src.Width;
        int mode = scale >= 2f ? 0 : scale >= 0.6f ? 1 : 2; // 0 nearest, 1 bilinear, 2 box
        int x0 = Math.Clamp((int)Math.Floor(vr.X), 0, W), x1 = Math.Clamp((int)Math.Ceiling(vr.Right), 0, W);
        int y0 = Math.Clamp((int)Math.Floor(vr.Y), 0, H), y1 = Math.Clamp((int)Math.Ceiling(vr.Bottom), 0, H);

        // Per-column / per-row source lookup within [start, start + len): index a, index b (next tap or box end),
        // bilinear weight
        static void Axis(float origin, float scale, int start, int len, int mode, int[] a, int[] b, int[] f, int from, int to)
        {
            for (int i = from; i < to; i++)
            {
                if (mode == 0)
                {
                    a[i] = start + Math.Clamp((int)((i + 0.5f - origin) / scale), 0, len - 1);
                }
                else if (mode == 1)
                {
                    float s = (i + 0.5f - origin) / scale - 0.5f;
                    int k = (int)Math.Floor(s);
                    f[i] = (int)((s - k) * 256);
                    a[i] = start + Math.Clamp(k, 0, len - 1);
                    b[i] = start + Math.Clamp(k + 1, 0, len - 1);
                }
                else
                {
                    int s0 = Math.Clamp((int)Math.Floor((i - origin) / scale), 0, len - 1);
                    int s1 = Math.Clamp((int)Math.Ceiling((i + 1 - origin) / scale), s0 + 1, len);
                    a[i] = start + s0; b[i] = start + s1;
                }
            }
        }
        int[] ca = new int[W], cb = new int[W], cf = new int[W], ra = new int[H], rb = new int[H], rf = new int[H];
        Axis(vr.X, scale, src.X, src.Width, mode, ca, cb, cf, x0, x1);
        Axis(vr.Y, scale, src.Y, src.Height, mode, ra, rb, rf, y0, y1);

        BitmapData? vd = null, sd = null;
        _busy++;
        try
        {
            vd = _view.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            sd = _bmp.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            IntPtr dp = vd.Scan0, sp = sd.Scan0;
            int ds = vd.Stride, ss = sd.Stride;
            const uint Black = 0xFF000000u;
            NoPump.For(H, y =>
            {
                uint* d = (uint*)((byte*)dp + (long)y * ds);
                if (y < y0 || y >= y1) { new Span<uint>(d, W).Fill(Black); return; }
                if (x0 > 0) new Span<uint>(d, x0).Fill(Black);
                if (x1 < W) new Span<uint>(d + x1, W - x1).Fill(Black);
                if (mode == 0)
                {
                    uint* s = (uint*)((byte*)sp + (long)ra[y] * ss);
                    for (int x = x0; x < x1; x++) d[x] = s[ca[x]];
                }
                else if (mode == 1)
                {
                    uint* s0 = (uint*)((byte*)sp + (long)ra[y] * ss), s1 = (uint*)((byte*)sp + (long)rb[y] * ss);
                    int fy = rf[y];
                    for (int x = x0; x < x1; x++)
                    {
                        int fx = cf[x];
                        uint top = Lerp(s0[ca[x]], s0[cb[x]], fx), bot = Lerp(s1[ca[x]], s1[cb[x]], fx);
                        d[x] = Black | Lerp(top, bot, fy);
                    }
                }
                else
                {
                    int ya = ra[y], yb = rb[y];
                    for (int x = x0; x < x1; x++)
                    {
                        int xa = ca[x], xb = cb[x], r = 0, g = 0, b = 0;
                        for (int sy = ya; sy < yb; sy++)
                        {
                            uint* s = (uint*)((byte*)sp + (long)sy * ss);
                            for (int sx = xa; sx < xb; sx++)
                            {
                                uint p = s[sx];
                                r += (int)(p >> 16) & 0xFF; g += (int)(p >> 8) & 0xFF; b += (int)p & 0xFF;
                            }
                        }
                        int n = (yb - ya) * (xb - xa);
                        d[x] = Black | (uint)(r / n << 16) | (uint)(g / n << 8) | (uint)(b / n);
                    }
                }
            });
        }
        finally
        {
            if (vd != null) _view.UnlockBits(vd);
            if (sd != null) _bmp.UnlockBits(sd);
            EndBusy();
        }
    }

    /// <summary>Blends two packed RGB pixels, f = 0..256 (weight of b).</summary>
    static uint Lerp(uint a, uint b, int f)
    {
        uint wa = (uint)(256 - f), wb = (uint)f;
        uint rb = (((a & 0xFF00FFu) * wa + (b & 0xFF00FFu) * wb) >> 8) & 0xFF00FFu;
        uint g = (((a & 0x00FF00u) * wa + (b & 0x00FF00u) * wb) >> 8) & 0x00FF00u;
        return rb | g;
    }

    // ───────────────────────── glyphs ─────────────────────────

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    Pen GlyphPen(Color c, float w = 1.8f) => new(c, w * DeviceDpi / 96f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    static void PlayGlyph(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        float h = r.Height * 0.9f, w = h * 0.86f, x = r.X + (r.Width - w) / 2, y = r.Y + (r.Height - h) / 2;
        using var path = new GraphicsPath();
        path.AddPolygon(new[] { new PointF(x, y), new PointF(x + w, y + h / 2), new PointF(x, y + h) });
        using var pen = new Pen(c, h * 0.12f) { LineJoin = LineJoin.Round };
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }

    static readonly GraphicsPath AddVideoShape = AddVideoIcon.Create();

    static void AddVideoGlyph(Graphics g, RectangleF r, Color c)
    {
        using var m = new Matrix(r.Width, 0, 0, r.Height, r.X, r.Y);
        using var path = (GraphicsPath)AddVideoShape.Clone();
        path.Transform(m);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
    }

    static void PauseGlyph(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        float h = r.Height * 0.86f, bw = r.Width * 0.24f, gap = r.Width * 0.18f;
        float x = r.X + (r.Width - 2 * bw - gap) / 2, y = r.Y + (r.Height - h) / 2;
        using var p1 = RoundRect(new RectangleF(x, y, bw, h), bw * 0.3f);
        using var p2 = RoundRect(new RectangleF(x + bw + gap, y, bw, h), bw * 0.3f);
        g.FillPath(b, p1);
        g.FillPath(b, p2);
    }

    /// <summary>Frame step: a small triangle against a bar (|◀ / ▶|).</summary>
    void StepGlyph(Graphics g, RectangleF r, Color c, bool forward)
    {
        float h = r.Height * 0.62f, w = h * 0.8f, bar = Math.Max(2f, r.Width * 0.11f);
        float total = w + bar + r.Width * 0.06f, x = r.X + (r.Width - total) / 2, y = r.Y + (r.Height - h) / 2;
        using var b = new SolidBrush(c);
        using var pen = new Pen(c, h * 0.1f) { LineJoin = LineJoin.Round };
        using var path = new GraphicsPath();
        if (forward)
        {
            path.AddPolygon(new[] { new PointF(x, y), new PointF(x + w, y + h / 2), new PointF(x, y + h) });
            using var bp = RoundRect(new RectangleF(x + total - bar, y, bar, h), bar / 2);
            g.FillPath(b, bp);
        }
        else
        {
            using var bp = RoundRect(new RectangleF(x, y, bar, h), bar / 2);
            g.FillPath(b, bp);
            float tx = x + total - w;
            path.AddPolygon(new[] { new PointF(tx + w, y), new PointF(tx, y + h / 2), new PointF(tx + w, y + h) });
        }
        g.FillPath(b, path);
        g.DrawPath(pen, path);
    }

    /// <summary>Selection start / end: a bracket with a small triangle pointing into the selection.</summary>
    void BracketGlyph(Graphics g, RectangleF r, Color c, bool start)
    {
        using var pen = GlyphPen(c, 2f);
        float h = r.Height * 0.86f, y = r.Y + (r.Height - h) / 2, tick = r.Width * 0.22f;
        float bx = start ? r.X + r.Width * 0.22f : r.Right - r.Width * 0.22f, d = start ? 1 : -1;
        g.DrawLines(pen, new[] { new PointF(bx + d * tick, y), new PointF(bx, y), new PointF(bx, y + h), new PointF(bx + d * tick, y + h) });
        float th = h * 0.42f, tw = th * 0.8f, tx = bx + d * r.Width * 0.24f, ty = y + (h - th) / 2;
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[] { new PointF(tx, ty), new PointF(tx + d * tw, ty + th / 2), new PointF(tx, ty + th) });
    }

    /// <summary>Rotate: a circular arrow (like the app icon), clockwise or mirrored.</summary>
    void RotateGlyph(Graphics g, RectangleF r, Color c, bool clockwise)
    {
        using var pen = GlyphPen(c, 1.8f);
        float rad = r.Width * 0.36f, cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2 + r.Height * 0.04f;
        var box = new RectangleF(cx - rad, cy - rad, 2 * rad, 2 * rad);
        // Gap at the top; the arrowhead sits at the end of the sweep.
        float start = clockwise ? -55 : -125, sweep = clockwise ? 270 : -270;
        g.DrawArc(pen, box, start, sweep);
        double end = (start + sweep) * Math.PI / 180;
        var tip0 = new PointF(cx + rad * (float)Math.Cos(end), cy + rad * (float)Math.Sin(end));
        // tangent in the direction of travel
        float tx = -(float)Math.Sin(end) * (clockwise ? 1 : -1), ty = (float)Math.Cos(end) * (clockwise ? 1 : -1);
        float nx = (float)Math.Cos(end), ny = (float)Math.Sin(end), a = r.Width * 0.2f;
        using var b = new SolidBrush(c);
        g.FillPolygon(b, new[]
        {
            new PointF(tip0.X + tx * a * 1.05f, tip0.Y + ty * a * 1.05f),
            new PointF(tip0.X + nx * a * 0.75f - tx * a * 0.1f, tip0.Y + ny * a * 0.75f - ty * a * 0.1f),
            new PointF(tip0.X - nx * a * 0.75f - tx * a * 0.1f, tip0.Y - ny * a * 0.75f - ty * a * 0.1f),
        });
    }

    void SpeakerGlyph(Graphics g, RectangleF r, Color c, bool muted)
    {
        using var b = new SolidBrush(c);
        float cy = r.Y + r.Height / 2, x = r.X + r.Width * 0.04f;
        g.FillPolygon(b, new[]
        {
            new PointF(x, cy - r.Height * 0.16f), new PointF(x + r.Width * 0.2f, cy - r.Height * 0.16f),
            new PointF(x + r.Width * 0.46f, cy - r.Height * 0.4f), new PointF(x + r.Width * 0.46f, cy + r.Height * 0.4f),
            new PointF(x + r.Width * 0.2f, cy + r.Height * 0.16f), new PointF(x, cy + r.Height * 0.16f),
        });
        using var pen = GlyphPen(c, 1.6f);
        if (muted)
        {
            float mx = r.X + r.Width * 0.78f, s = r.Width * 0.14f;
            g.DrawLine(pen, mx - s, cy - s, mx + s, cy + s);
            g.DrawLine(pen, mx + s, cy - s, mx - s, cy + s);
            return;
        }
        float ax = r.X + r.Width * 0.46f;
        g.DrawArc(pen, ax - r.Width * 0.16f, cy - r.Height * 0.2f, r.Width * 0.32f, r.Height * 0.4f, -50, 100);
        if (_volume > 0.5f) g.DrawArc(pen, ax - r.Width * 0.3f, cy - r.Height * 0.38f, r.Width * 0.6f, r.Height * 0.76f, -50, 100);
    }

    void LoopGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c);
        var box = new RectangleF(r.X + r.Width * 0.05f, r.Y + r.Height * 0.22f, r.Width * 0.9f, r.Height * 0.56f);
        using (var path = RoundRect(box, box.Height * 0.35f)) g.DrawPath(pen, path);
        using var b = new SolidBrush(c);
        float a = r.Width * 0.16f;
        float tx = box.X + box.Width * 0.62f, bx = box.X + box.Width * 0.38f;
        g.FillPolygon(b, new[] { new PointF(tx + a, box.Top), new PointF(tx - a * 0.3f, box.Top - a * 0.85f), new PointF(tx - a * 0.3f, box.Top + a * 0.85f) });
        g.FillPolygon(b, new[] { new PointF(bx - a, box.Bottom), new PointF(bx + a * 0.3f, box.Bottom - a * 0.85f), new PointF(bx + a * 0.3f, box.Bottom + a * 0.85f) });
    }

    void FullGlyph(Graphics g, RectangleF r, Color c, bool exit)
    {
        using var pen = GlyphPen(c);
        float l = r.Width * 0.3f, i = r.Width * 0.1f;
        var q = RectangleF.Inflate(r, -i, -i);
        if (!exit)
        {
            g.DrawLines(pen, new[] { new PointF(q.Left, q.Top + l), new PointF(q.Left, q.Top), new PointF(q.Left + l, q.Top) });
            g.DrawLines(pen, new[] { new PointF(q.Right - l, q.Top), new PointF(q.Right, q.Top), new PointF(q.Right, q.Top + l) });
            g.DrawLines(pen, new[] { new PointF(q.Right, q.Bottom - l), new PointF(q.Right, q.Bottom), new PointF(q.Right - l, q.Bottom) });
            g.DrawLines(pen, new[] { new PointF(q.Left + l, q.Bottom), new PointF(q.Left, q.Bottom), new PointF(q.Left, q.Bottom - l) });
        }
        else
        {
            g.DrawLines(pen, new[] { new PointF(q.Left, q.Top + l), new PointF(q.Left + l, q.Top + l), new PointF(q.Left + l, q.Top) });
            g.DrawLines(pen, new[] { new PointF(q.Right - l, q.Top), new PointF(q.Right - l, q.Top + l), new PointF(q.Right, q.Top + l) });
            g.DrawLines(pen, new[] { new PointF(q.Right, q.Bottom - l), new PointF(q.Right - l, q.Bottom - l), new PointF(q.Right - l, q.Bottom) });
            g.DrawLines(pen, new[] { new PointF(q.Left + l, q.Bottom), new PointF(q.Left + l, q.Bottom - l), new PointF(q.Left, q.Bottom - l) });
        }
    }

    static void MoreGlyph(Graphics g, RectangleF r, Color c)
    {
        using var b = new SolidBrush(c);
        float d = r.Width * 0.16f, cx = r.X + r.Width / 2;
        for (int i = -1; i <= 1; i++)
            g.FillEllipse(b, cx - d / 2, r.Y + r.Height / 2 + i * r.Height * 0.32f - d / 2, d, d);
    }

    /// <summary>Export: an arrow rising out of a tray.</summary>
    void ExportGlyph(Graphics g, RectangleF r, Color c)
    {
        using var pen = GlyphPen(c, 1.8f);
        float cx = r.X + r.Width / 2;
        g.DrawLines(pen, new[] { new PointF(r.X, r.Y + r.Height * 0.55f), new PointF(r.X, r.Bottom), new PointF(r.Right, r.Bottom), new PointF(r.Right, r.Y + r.Height * 0.55f) });
        g.DrawLine(pen, cx, r.Y, cx, r.Y + r.Height * 0.7f);
        g.DrawLines(pen, new[] { new PointF(cx - r.Width * 0.28f, r.Y + r.Height * 0.28f), new PointF(cx, r.Y), new PointF(cx + r.Width * 0.28f, r.Y + r.Height * 0.28f) });
    }

    void DrawWindowGlyph(Graphics g, Hit h, Rectangle r)
    {
        using var pen = GlyphPen(W(235), 1.2f);
        float s = P(10), cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
        switch (h)
        {
            case Hit.Min:
                g.DrawLine(pen, cx - s / 2, cy, cx + s / 2, cy);
                break;
            case Hit.Max:
                if (WindowState == FormWindowState.Maximized || _fullscreen)
                {
                    float o = s * 0.25f;
                    g.DrawRectangle(pen, cx - s / 2, cy - s / 2 + o, s - o, s - o);
                    g.DrawLines(pen, new[] { new PointF(cx - s / 2 + o, cy - s / 2 + o), new PointF(cx - s / 2 + o, cy - s / 2), new PointF(cx + s / 2, cy - s / 2), new PointF(cx + s / 2, cy + s / 2 - o), new PointF(cx + s / 2 - o, cy + s / 2 - o) });
                }
                else
                {
                    using var rp = RoundRect(new RectangleF(cx - s / 2, cy - s / 2, s, s), P(1.5f));
                    g.DrawPath(pen, rp);
                }
                break;
            case Hit.Close:
                g.DrawLine(pen, cx - s / 2, cy - s / 2, cx + s / 2, cy + s / 2);
                g.DrawLine(pen, cx + s / 2, cy - s / 2, cx - s / 2, cy + s / 2);
                break;
        }
    }

    // ───────────────────────── input ─────────────────────────

    /// <summary>Shows the overlay and restarts its hide countdown.</summary>
    void Activity() => _lastActivity = Now;

    void HideOverlayNow() => _lastActivity = double.NegativeInfinity;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        // Windows sends WM_MOUSEMOVE for things that aren't movement (repaints, cursor changes, window moves); ignore those.
        var screen = PointToScreen(e.Location);
        bool moved = screen != _lastMousePos;
        _lastMousePos = screen;

        if (_drag == Drag.Crop) { Activity(); DragCrop(e.Location); return; }
        if (_drag != Drag.None) { Activity(); DragTo(e.X); return; }

        if (_dragArmed)
        {
            if (!_panning && _zoom > 1f &&
                (Math.Abs(e.X - _dragStart.X) > SystemInformation.DragSize.Width ||
                 Math.Abs(e.Y - _dragStart.Y) > SystemInformation.DragSize.Height))
            {
                _panning = true;
                Cursor = Cursors.SizeAll;
            }
            if (_panning)
            {
                var vr = VideoRectF;
                _center = new PointF(_dragCenter.X - (e.X - _dragStart.X) / vr.Width, _dragCenter.Y - (e.Y - _dragStart.Y) / vr.Height);
                ClampCenter();
                Invalidate();
            }
            return; // dragging the video never brings the overlay back
        }

        if (moved) Activity();
        var h = HitTest(e.Location);
        int item = h == Hit.CropBar ? CropItemAt(e.Location) : int.MinValue;
        if (item != _hoverItem) { _hoverItem = item; Invalidate(CropBarRect); }
        if (h != _hover)
        {
            if (_hintVisible) Invalidate(); else { InvalidateHit(_hover); InvalidateHit(h); }
            _hover = h;
            _hoverSince = Now;
            _hintVisible = false;
        }
        Cursor = h is Hit.TrimIn or Hit.TrimOut ? Cursors.SizeWE : h == Hit.TrimMove ? Cursors.SizeAll
            : h == Hit.CropArea ? CropCursor(CropPartAt(e.Location)) : h == Hit.CropBar && item == int.MinValue ? Cursors.Default
            : h != Hit.None ? Cursors.Hand : _zoom > 1f ? Cursors.SizeAll : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != Hit.None) { _hover = Hit.None; _hintVisible = false; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var h = HitTest(e.Location);
        if (h != Hit.None) Activity();
        _hintVisible = false;
        if (h == Hit.CropArea)
        {
            if (e.Clicks == 2 && CropPartAt(e.Location) == CropPart.Move) { FinishCrop(apply: true); return; } // double-click = done
            BeginCropDrag(e.Location);
            return;
        }
        if (h == Hit.CropBar) _pressedItem = CropItemAt(e.Location);
        if (_info != null && h is Hit.Timeline or Hit.TrimIn or Hit.TrimOut or Hit.TrimMove or Hit.VolSlider)
        {
            if (h != Hit.VolSlider) Pause();
            _drag = h switch
            {
                Hit.TrimIn => Drag.In, Hit.TrimOut => Drag.Out, Hit.TrimMove => Drag.Move, Hit.VolSlider => Drag.Volume, _ => Drag.Seek,
            };
            _snap = Drag.None;
            // Grabbing a handle (or the selection) anywhere on it shouldn't make it jump: keep the pointer's offset from
            // the edge, in pixels so it survives the clamping at the ends of the timeline.
            _dragOffset = _drag is Drag.In or Drag.Move ? X(_in) - e.X : _drag == Drag.Out ? X(_out) - e.X : 0;
            _dragStartX = e.X;
            _moveLength = _out - _in;
            _moved = false;
            Capture = true;
            if (_drag != Drag.Move) DragTo(e.X); // a move starts only once the pointer actually moves
            return;
        }
        _pressed = h;
        if (h == Hit.Export) InvalidateHit(h);
        if (h == Hit.None && !_cropping)
        {
            if (e.Clicks == 2)
            {
                TogglePlay(); // undo the first click's play/pause toggle
                ToggleFullscreen();
                _ignoreUp = true;
                return;
            }
            _dragArmed = true;
            _dragStart = e.Location;
            _dragCenter = _center;
            Capture = true;
        }
    }

    void DragTo(int x)
    {
        if (_pb == null || _info == null) return;
        double minLen = Math.Max(FrameDur, 0.04);
        switch (_drag)
        {
            case Drag.Seek:
                _pos = TimeAt(x);
                _pb.ShowNear(_pos);
                break;
            case Drag.In:
                _in = Math.Clamp(TimeAt(x + (float)_dragOffset), 0, Math.Max(0, _out - minLen));
                _pos = _in;
                _pb.ShowNear(_in);
                break;
            case Drag.Out:
                _out = Math.Clamp(TimeAt(x + (float)_dragOffset), Math.Min(Duration, _in + minLen), Duration);
                _pos = Math.Max(_in, _out - 0.001);
                _pb.ShowNear(_pos);
                break;
            case Drag.Move:
                if (!_moved && Math.Abs(x - _dragStartX) < SystemInformation.DragSize.Width) return;
                _moved = true;
                // Same length, new place: the preview follows the start of the selection.
                _in = Math.Clamp(TimeAt(x + (float)_dragOffset), 0, Math.Max(0, Duration - _moveLength));
                _out = Math.Min(Duration, _in + _moveLength);
                _pos = _in;
                _pb.ShowNear(_in);
                break;
            case Drag.Volume:
            {
                var r = Rect(Hit.VolSlider);
                float l = r.Left + P(6), w = r.Width - P(12);
                SetVolume((x - l) / w);
                return;
            }
        }
        Invalidate(PanelRect);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.Crop) { EndCropDrag(); return; }
        if (_drag != Drag.None)
        {
            var d = _drag;
            _drag = Drag.None;
            Capture = false;
            if (d == Drag.Move && !_moved)
            {
                // A click on the selection's grip or bars (no drag): move the playhead there, like the rest of the strip.
                d = Drag.Seek;
                _pos = TimeAt(e.X);
            }
            // The exact frame once the pointer is released (scrubbing shows nearby keyframes).
            if (d is Drag.Seek or Drag.In or Drag.Out or Drag.Move && _pb != null) _pb.ShowAt(_pos);
            _snap = d is Drag.In or Drag.Out or Drag.Move ? d : Drag.None;
            Invalidate();
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (_dragArmed)
        {
            bool panned = _panning;
            _dragArmed = _panning = false;
            Capture = false;
            Cursor = _zoom > 1f ? Cursors.SizeAll : Cursors.Default;
            _lastMousePos = PointToScreen(e.Location); // don't treat the release as movement
            if (panned) { _pressed = Hit.None; return; }
        }
        if (_ignoreUp) { _ignoreUp = false; return; }
        var h = HitTest(e.Location);
        var pressed = _pressed;
        _pressed = Hit.None;
        if (pressed == Hit.Export) InvalidateHit(Hit.Export);
        if (h != pressed) return;
        switch (h)
        {
            case Hit.None: if (_info != null && !_cropping) TogglePlay(); break;
            case Hit.CropBar:
            {
                int item = CropItemAt(e.Location);
                if (item != _pressedItem) break;
                if (item == DoneItem) FinishCrop(apply: true);
                else if (item == ResetItem) ResetCrop();
                else if (item >= 0) ApplyAspect(item);
                break;
            }
            case Hit.Crop: ToggleCrop(); break;
            case Hit.Center: if (_info == null) OpenDialog(); else TogglePlay(); break;
            case Hit.Play: TogglePlay(); break;
            case Hit.Prev: Step(-1); break;
            case Hit.Next: Step(+1); break;
            case Hit.SetIn: SetInHere(); break;
            case Hit.SetOut: SetOutHere(); break;
            case Hit.RotL: Rotate(-90); break;
            case Hit.RotR: Rotate(+90); break;
            case Hit.Vol: ToggleMute(); break;
            case Hit.Loop: _loop = !_loop; ShowToast(_loop ? "Loop selection: on" : "Loop selection: off"); break;
            case Hit.Full: ToggleFullscreen(); break;
            case Hit.More: ShowMenu(); break;
            case Hit.Export: Export(); break;
            case Hit.Min: WindowState = FormWindowState.Minimized; break;
            case Hit.Max: ToggleMaximize(); break;
            case Hit.Close: Close(); break;
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_info == null) return;
        // Ctrl+wheel = zoom at the cursor. Precision-touchpad pinch also arrives this way.
        if ((ModifierKeys & Keys.Control) != 0)
        {
            ZoomAt((float)Math.Pow(1.2, e.Delta / 120.0), e.Location);
            return;
        }
        if (HitTest(e.Location) is Hit.Vol or Hit.VolSlider)
        {
            Activity();
            SetVolume((_muted ? 0 : _volume) + 0.05f * e.Delta / 120f);
            return;
        }
        Step(e.Delta > 0 ? -1 : +1);
    }

    void InvalidateHit(Hit h)
    {
        if (h == Hit.None) return;
        var r = h switch
        {
            Hit.Timeline or Hit.TrimIn or Hit.TrimOut or Hit.TrimMove => PanelRect,
            Hit.Min or Hit.Max or Hit.Close => WindowButtonsRect,
            Hit.CropBar => CropBarRect,
            Hit.CropArea => ClientRectangle,
            _ => Rect(h),
        };
        r.Inflate(P(4), P(4));
        Invalidate(r);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            // Zoom (doesn't bring the overlay back)
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Shift | Keys.Oemplus:
            case Keys.Control | Keys.Add:
                ZoomCentered(1.25f); return true;
            case Keys.Control | Keys.OemMinus:
            case Keys.Control | Keys.Subtract:
                ZoomCentered(1 / 1.25f); return true;
            case Keys.Control | Keys.D0:
            case Keys.Control | Keys.NumPad0:
                ResetZoom(); return true;
        }
        Activity();
        switch (keyData)
        {
            case Keys.C: ToggleCrop(); return true;
            case Keys.Enter when _cropping: FinishCrop(apply: true); return true;
            case Keys.Escape when _cropping: FinishCrop(apply: false); return true;
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: Step(-1); return true;
            case Keys.Right: Step(+1); return true;
            case Keys.Shift | Keys.Left: Jump(-1); return true;
            case Keys.Shift | Keys.Right: Jump(+1); return true;
            case Keys.Control | Keys.Left: case Keys.PageUp: Jump(-10); return true;
            case Keys.Control | Keys.Right: case Keys.PageDown: Jump(+10); return true;
            case Keys.Home: GoTo(_in); return true;
            case Keys.End: GoTo(Math.Max(_in, _out - 0.001)); return true;
            case Keys.Control | Keys.Home: GoTo(0); return true;
            case Keys.Control | Keys.End: GoTo(Duration); return true;
            case Keys.I: case Keys.OemOpenBrackets: SetInHere(); return true;
            case Keys.O: case Keys.OemCloseBrackets: SetOutHere(); return true;
            case Keys.R: Rotate(+90); return true;
            case Keys.Shift | Keys.R: Rotate(-90); return true;
            case Keys.Up: SetVolume((_muted ? 0 : _volume) + 0.05f); return true;
            case Keys.Down: SetVolume((_muted ? 0 : _volume) - 0.05f); return true;
            case Keys.M: ToggleMute(); return true;
            case Keys.Control | Keys.E: Export(); return true;
            case Keys.Control | Keys.O: OpenDialog(); return true;
            case Keys.F11: case Keys.F: ToggleFullscreen(); return true;
            case Keys.Escape when _fullscreen: ToggleFullscreen(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ───────────────────────── menu ─────────────────────────

    void BuildMenu()
    {
        _menu.Renderer = new ToolStripProfessionalRenderer(new DarkColors()) { RoundedEdges = false };
        _menu.ShowImageMargin = false;
        _menu.ForeColor = Color.Gainsboro;
        _menu.Font = new Font("Segoe UI", 9.5f);
        _menu.Opening += (_, _) => PopulateMenu();
        _menu.Closed += (_, _) => Activity();
    }

    void PopulateMenu()
    {
        _menu.Items.Clear();
        bool has = _info != null;
        ToolStripMenuItem Item(string text, Action a, string? keys = null, bool enabled = true) =>
            new(text, null, (_, _) => a()) { ShortcutKeyDisplayString = keys, Enabled = enabled };
        _menu.Items.Add(Item("Open video…", OpenDialog, "Ctrl+O"));
        _menu.Items.Add(Item("Export selection…", Export, "Ctrl+E", has));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Rotate right", () => Rotate(+90), "R", has));
        _menu.Items.Add(Item("Rotate left", () => Rotate(-90), "Shift+R", has));
        _menu.Items.Add(Item("Reset rotation", () => Rotate(-_rot), null, has && _rot != 0));
        _menu.Items.Add(Item(_cropping ? "Finish cropping" : "Crop…", ToggleCrop, "C", has));
        _menu.Items.Add(Item("Remove crop", () => { ResetCrop(); FinishCrop(apply: true); }, null, has && HasCrop));
        _menu.Items.Add(Item("Select the whole video", ResetSelection, null, has && (_in > 0 || _out < Duration)));
        _menu.Items.Add(Item((_loop ? "✓  " : "     ") + "Loop selection", () => _loop = !_loop, null, has));
        _menu.Items.Add(Item("Reset zoom", ResetZoom, "Ctrl+0", _zoom > 1f));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Keyboard shortcuts", () => { RevealCursor(); using var dlg = new ShortcutsDialog(); dlg.ShowDialog(this); }));
        _menu.Items.Add(Item("Open crash log folder", CrashLog.OpenFolder));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item($"About {AppInfo.Name}", () => { RevealCursor(); using var dlg = new AboutDialog(); dlg.ShowDialog(this); }));
    }

    void ShowMenu()
    {
        var r = Rect(Hit.More);
        _menu.Show(this, new Point(r.Right, r.Top - P(4)), ToolStripDropDownDirection.AboveLeft);
    }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color Bg = Color.FromArgb(16, 21, 46), Hi = Color.FromArgb(36, 45, 88), Bd = Color.FromArgb(50, 60, 106);
        public override Color ToolStripDropDownBackground => Bg;
        public override Color MenuBorder => Bd;
        public override Color MenuItemBorder => Hi;
        public override Color MenuItemSelected => Hi;
        public override Color MenuItemSelectedGradientBegin => Hi;
        public override Color MenuItemSelectedGradientEnd => Hi;
        public override Color SeparatorDark => Bd;
        public override Color SeparatorLight => Bg;
        public override Color ImageMarginGradientBegin => Bg;
        public override Color ImageMarginGradientMiddle => Bg;
        public override Color ImageMarginGradientEnd => Bg;
    }

    // ───────────────────────── file / edit actions ─────────────────────────

    void OpenDialog()
    {
        RevealCursor();
        using var dlg = new OpenFileDialog { Filter = VideoFilter, Title = "Open a video" };
        if (_info != null) dlg.InitialDirectory = Path.GetDirectoryName(_info.Path);
        if (dlg.ShowDialog(this) == DialogResult.OK) Open(dlg.FileName);
    }

    void Open(string path)
    {
        if (_busy > 0) { BeginInvoke(() => Open(path)); return; }
        if (FFmpegRuntime.Load() is string loadError)
        {
            MessageBox.Show(this, loadError, "Splicia", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"This file doesn't exist (any more):\n{path}", "Splicia", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Playback pb;
        try { pb = new Playback(path); }
        catch (Exception ex)
        {
            if (ex is not NotSupportedException) CrashLog.Write(ex, $"Opening {path}");
            MessageBox.Show(this, $"Couldn't open this video:\n{path}\n\n{ex.Message}", "Splicia", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        CloseFile();
        _pb = pb;
        _info = pb.Info;
        _pb.Volume = _muted ? 0 : _volume;
        _in = 0;
        _out = Duration;
        _pos = 0;
        _rot = 0;
        _crop = _cropBefore = FullFrame;
        _cropping = false;
        _aspect = 0;
        _strip = new ThumbnailStrip(_info, this, P(46) * 2);
        _strip.Updated += () => { _stripVersion++; if (_ui > 0) Invalidate(PanelRect); };
        Text = $"{Path.GetFileName(path)} — Splicia";
        ResetZoom();
        _pb.ShowAt(0);
        Activity();
        Invalidate();
    }

    void CloseFile()
    {
        if (_playing) Pause();
        _strip?.Dispose();
        _strip = null;
        _pb?.Dispose();
        _pb = null;
        _info = null;
        _shown = null;
        _bmp?.Dispose();
        _bmp = null;
        _blur?.Dispose();
        _blur = null;
        _film?.Dispose();
        _film = null;
        _filmKey = default;
        _viewKey = default;
    }

    void ResetSelection()
    {
        _in = 0;
        _out = Duration;
        Invalidate();
    }

    void SetInHere()
    {
        if (_info == null) return;
        if (_playing) Pause();
        _in = Math.Clamp(_pos, 0, Duration);
        if (_out - _in < FrameDur * 0.5) _out = Duration;
        ShowToast($"Start set to {Fmt(_in)}");
    }

    void SetOutHere()
    {
        if (_info == null) return;
        if (_playing) Pause();
        // The selection ends after the frame on screen, so that frame is included.
        _out = Math.Clamp(_pos + FrameDur, 0, Duration);
        if (_out - _in < FrameDur * 0.5) _in = 0;
        ShowToast($"End set to {Fmt(_out)}");
    }

    void Rotate(int degrees)
    {
        if (_info == null) return;
        _rot = ((_rot + degrees) % 360 + 360) % 360;
        RotateCrop(degrees);
        _zoom = 1f;
        _center = new PointF(0.5f, 0.5f);
        RenderFrame();
        ShowToast(_rot switch { 0 => "Original orientation", 90 => "Rotated 90° right", 180 => "Rotated 180°", _ => "Rotated 90° left" });
    }

    void SetVolume(float v)
    {
        v = Math.Clamp(v, 0f, 1f);
        _volume = v;
        _muted = v == 0;
        if (_pb != null) _pb.Volume = v;
        Invalidate(PanelRect);
    }

    void ToggleMute()
    {
        if (_info == null) return;
        if (_muted || _volume == 0)
        {
            _muted = false;
            if (_volume == 0) _volume = 0.5f;
        }
        else _muted = true;
        if (_pb != null) _pb.Volume = _muted ? 0 : _volume;
        ShowToast(_muted ? "Muted" : $"Volume {_volume * 100:0}%");
        Invalidate(PanelRect);
    }

    void Export()
    {
        if (_info == null || _pb == null) return;
        if (_playing) Pause();
        FinishCrop(apply: true);
        var job = new TrimJob(_info, _in, _out, _rot) { Crop = HasCrop ? _crop : null };
        RevealCursor();
        using var dlg = new ExportDialog(job);
        dlg.ShowDialog(this);
        Activity();
    }

    /// <summary>Undoes the idle cursor hiding right away (before a dialog opens over this window).</summary>
    void RevealCursor()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        Cursor.Show();
    }

    // ───────────────────────── playback ─────────────────────────

    void TogglePlay()
    {
        if (_playing) Pause(); else Play();
        Activity(); // a play/pause action shows the controls briefly, then they fade like any other time
    }

    void Play()
    {
        if (_pb == null || _info == null) return;
        double from = _pos;
        // At (or right before) the end of the selection or the file: start over from the selection start.
        if (from >= _out - FrameDur * 1.5 && from < _out + FrameDur) from = _in;
        if (from >= Duration - FrameDur * 1.5) from = _in;
        _stopAtOut = from < _out;
        _playing = true;
        _pb.Play(from);
        timeBeginPeriod(1);
        _timer.Interval = 4;
        Invalidate();
    }

    void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _pb?.Pause();
        timeEndPeriod(1);
        _timer.Interval = 10;
        Invalidate();
    }

    void Step(int delta)
    {
        if (_pb == null) return;
        Pause();
        if (delta > 0) _pb.ShowNext(_pos);
        else _pb.ShowAt(Math.Max(0, _pos - 0.0005));
    }

    void Jump(double seconds)
    {
        if (_pb == null) return;
        GoTo(Math.Clamp(_pos + seconds, 0, Duration));
    }

    void GoTo(double t)
    {
        if (_pb == null) return;
        t = Math.Clamp(t, 0, Duration);
        _pos = t;
        if (_playing)
        {
            _stopAtOut = t < _out;
            _pb.Play(t);
        }
        else _pb.ShowAt(t);
        Invalidate(PanelRect);
    }

    void Present(VideoFrame f, bool updatePos)
    {
        if (_shown != null && !ReferenceEquals(_shown, f)) _pb?.Recycle(_shown);
        _shown = f;
        if (updatePos) _pos = f.Time;
        RenderFrame();
    }

    void Tick()
    {
        if (_busy > 0) return;
        if (_pb != null)
        {
            if (_playing)
            {
                double clock = _pb.Clock;
                if (_stopAtOut && clock >= _out - 1e-4)
                {
                    if (_loop) { _pb.Play(_in); }
                    else
                    {
                        Pause();
                        _pb.ShowAt(Math.Max(_in, _out - 0.001)); // exactly the last frame of the selection
                        Activity();
                    }
                }
                else
                {
                    // Never show the first frame past the end of the selection, even for a moment.
                    var f = _pb.TakeDue(_stopAtOut ? Math.Min(clock, _out - 0.003) : clock);
                    if (f != null) Present(f, updatePos: true);
                    if (_pb.Ended)
                    {
                        if (_loop) _pb.Play(_in);
                        else { Pause(); Activity(); }
                    }
                }
            }
            else if (_pb.TakeStill() is VideoFrame still)
            {
                bool exact = still.Exact && _drag == Drag.None;
                Present(still, updatePos: exact);
                if (exact && _snap != Drag.None)
                {
                    // Cuts happen on frame boundaries: the selection starts with the frame shown at its start and ends
                    // after the frame shown at its end, so the export matches the preview exactly.
                    if (_snap == Drag.In) _in = Math.Min(still.Time, Math.Max(0, _out - FrameDur * 0.5));
                    else if (_snap == Drag.Out) _out = Math.Clamp(still.Time + FrameDur, _in + FrameDur * 0.5, Duration);
                    else
                    {
                        // Moved: the start lands on the frame shown, the length is kept.
                        double len = _out - _in;
                        _in = still.Time;
                        _out = Math.Min(Duration, _in + len);
                    }
                    _snap = Drag.None;
                    Invalidate(PanelRect);
                }
            }
        }

        // Hover hint after resting on a control
        if (_hover != Hit.None && !_hintVisible && _drag == Drag.None && Now - _hoverSince > 0.6 && _ui >= 1f)
        {
            _hintVisible = true;
            Invalidate();
        }
        if (_toast != null && Now >= _toastUntil) { _toast = null; Invalidate(); }
        else if (_toast != null) Invalidate(new Rectangle(0, 0, ClientSize.Width, P(70)));

        // Overlay visibility:
        //  • shown after real mouse movement (not video dragging) or a play/pause action, hidden again after
        //    HideAfterSeconds idle, or immediately when the mouse leaves the window
        //  • always shown with no file open, while dragging on the timeline, or with the menu open
        //  • stays up while the mouse rests on the (still visible) panel or window buttons
        var mouse = PointToClient(MousePosition);
        // Over this window itself, not over a dialog, menu or other window that happens to sit on top of it.
        bool mouseInside = ClientRectangle.Contains(mouse) && WindowFromPoint(MousePosition) == Handle;
        if (_info != null && !mouseInside && _drag == Drag.None && !_menu.Visible && !_cropping) HideOverlayNow();
        bool restingOnUi = _ui > 0f && !_panning && (PanelRect.Contains(mouse) || WindowButtonsRect.Contains(mouse));
        bool show = _info == null || _cropping || _drag != Drag.None || _menu.Visible || restingOnUi || Now - _lastActivity < HideAfterSeconds;
        float target = show ? 1f : 0f;
        if (_ui != target)
        {
            _ui = show ? Math.Min(1f, _ui + 0.2f) : Math.Max(0f, _ui - 0.08f);
            if (_ui == 0f && _hover != Hit.None) { _hover = Hit.None; _hintVisible = false; }
            Invalidate();
        }
        // Cursor.Hide() hides the pointer for every window of the app (the export window included), so only while
        // this window is the active one and the pointer is over it.
        bool hideCursor = _ui == 0f && mouseInside && !_dragArmed && ActiveForm == this;
        if (hideCursor != _cursorHidden)
        {
            _cursorHidden = hideCursor;
            if (hideCursor) Cursor.Hide(); else Cursor.Show();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Dispose();
        CloseFile();
        _view?.Dispose();
        base.OnFormClosed(e);
    }

    // ───────────────────────── interop ─────────────────────────

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)] struct POINTS { public short x, y; }
    [StructLayout(LayoutKind.Sequential)]
    struct GESTUREINFO
    {
        public int cbSize, dwFlags, dwID;
        public IntPtr hwndTarget;
        public POINTS ptsLocation;
        public int dwInstanceID, dwSequenceID;
        public ulong ullArguments;
        public int cbExtraArgs;
    }
    [StructLayout(LayoutKind.Sequential)] struct GESTURECONFIG { public int dwID, dwWant, dwBlock; }

    [DllImport("user32.dll")] static extern bool GetGestureInfo(IntPtr hGestureInfo, ref GESTUREINFO pGestureInfo);
    [DllImport("user32.dll")] static extern bool CloseGestureInfoHandle(IntPtr hGestureInfo);
    [DllImport("user32.dll")] static extern bool SetGestureConfig(IntPtr hwnd, int dwReserved, int cIDs, GESTURECONFIG[] pGestureConfig, int cbSize);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point pt);
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint uPeriod);
}
