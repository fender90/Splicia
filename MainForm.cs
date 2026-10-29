using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Borderless, fully custom-drawn main window: the video with a frosted-glass control overlay holding the timeline (a
/// video track and an audio track, see MainForm.Timeline.cs), playback, editing, rotate, crop and export controls.
/// </summary>
sealed partial class MainForm : Form
{
    enum Hit
    {
        None, Center, Ruler, Track, Clip, ClipFadeIn, ClipFadeOut, Junction, Music, MusicStart, MusicEnd, MusicFadeIn, MusicFadeOut,
        MusicLane, TrimStart, TrimEnd, TrimGrip, Play, Prev, Next, Undo, Redo, Split, Trim, Delete, AddAudio, RotL, RotR, Crop, CropBar, CropArea, Vol, VolSlider, Loop, Full, More,
        Export, Pin, Min, Max, Close, TrimApply, TrimCancel, Panel /* the panel's background, between its controls */,
    }
    enum Drag { None, Seek, Volume, Crop, ClipMove, MusicMove, MusicStart, MusicEnd, FadeIn, FadeOut, TrimStart, TrimEnd, TrimMove }

    static readonly Hit[] HitOrder =
    {
        Hit.CropBar, Hit.Min, Hit.Max, Hit.Close, Hit.TrimApply, Hit.TrimCancel, Hit.Pin, Hit.Ruler /* the whole timeline */, Hit.Play, Hit.Prev, Hit.Next, Hit.Undo, Hit.Redo, Hit.Split, Hit.Trim, Hit.Delete,
        Hit.AddAudio, Hit.RotL, Hit.RotR, Hit.Crop, Hit.Vol, Hit.VolSlider, Hit.Loop, Hit.Full, Hit.More, Hit.Export, Hit.Center,
        Hit.CropArea,
    };
    const double HideAfterSeconds = 2.5;

    public const string VideoFilter =
        "Videos|*.mp4;*.m4v;*.mov;*.qt;*.mkv;*.webm;*.avi;*.wmv;*.asf;*.flv;*.f4v;*.mpg;*.mpeg;*.m2v;*.m2ts;*.mts;*.ts;*.3gp;*.3g2;*.ogv;*.vob;*.mxf;*.dv;*.divx|All files (*.*)|*.*";

    Playback? _pb;
    MediaInfo? _info;
    ThumbnailStrip? _strip;
    int _stripVersion;
    VideoFrame? _shown;          // frame on screen, unrotated (kept to re-render when the rotation changes)
    Bitmap? _bmp, _blur, _view;
    bool _blurDirty;
    int _frameVersion;
    (int, RectangleF, Rectangle, Size) _viewKey;

    double _pos;                 // timeline time of the frame on screen
    int _rot;                    // the user's rotation, clockwise
    bool _loop, _muted, _playing;
    float _volume = 1f;
    Drag _drag;

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
    bool _pinned = true; // the control panel docked under the picture and always shown (unpinned: floats over it, hides when idle)
    double _quietUntil; // clicks on the picture or the panel's background are ignored until then (right after the pin moved)
    float _paintAlpha = -1f; // while painting a pinned panel: full strength, whatever the overlay's opacity

    /// <summary>The opacity colours are drawn with: the overlay's, or the panel's while a pinned panel is painted.</summary>
    float Alpha => _paintAlpha >= 0 ? _paintAlpha : _ui;
    float PanelAlpha => _pinned ? 1f : _ui;

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
        MinimumSize = new Size(P(1010), P(600));
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;

        BuildMenu();
        BuildClipMenu();
        CrashLog.Context = () => _info == null ? "no file open" :
            $"file '{_info.Path}' {_info.Width}x{_info.Height} {_info.VideoCodec} {_info.BitDepth}-bit rot={_info.Rotation} " +
            $"{_info.Fps:0.###} fps dur={_info.Duration:0.###} audio={_info.AudioCodec ?? "none"}; pos={_pos:0.###} clips={_tl.Clips.Count} " +
            $"sounds={_tl.Music.Count} end={_tl.End:0.###} userRot={_rot} playing={_playing} zoom={_zoom:0.##} client={ClientSize.Width}x{ClientSize.Height} " +
            $"dpi={DeviceDpi} fullscreen={_fullscreen}";
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
            var at = PointToClient(new Point(e.X, e.Y));
            // Sound files go on the audio track (where dropped on the timeline, else at the playhead); a video opens.
            if (files.All(IsSoundFile)) BeginInvoke(() => AddAudio(files, TimelineRect.Contains(at) ? _tl.Snap(TimeAt(at.X)) : _pos));
            else BeginInvoke(() => Open(files.First(f => !IsSoundFile(f))));
        };

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
            int m = P(22), h = P(136) + TimelineSpace;
            if (_pinned) return new Rectangle(0, ClientSize.Height - h, ClientSize.Width, h);
            int w = Math.Min(ClientSize.Width - 2 * m, P(1180));
            return new Rectangle((ClientSize.Width - w) / 2, ClientSize.Height - m - h, w, h);
        }
    }

    int Pad => P(24);

    /// <summary>The timeline's share of the panel's height: none until a video is open (then just the header and buttons).</summary>
    int TimelineSpace => _info != null ? P(116) : 0;

    Rectangle WindowButtonsRect
    {
        get { int w = P(46) * 3, h = P(34), m = P(12); return new Rectangle(ClientSize.Width - m - w, m, w, h); }
    }

    int ExportW => P(108);

    Rectangle Rect(Hit h)
    {
        var pr = PanelRect;
        int bs = P(36), rowY = pr.Top + P(94) + TimelineSpace - bs / 2, step = P(42);
        int left = pr.Left + Pad - P(8), right = pr.Right - Pad + P(8);
        var wb = WindowButtonsRect;
        int d = P(78);
        int exportX = right - P(8) - ExportW, moreX = exportX - P(12) - bs;
        return h switch
        {
            Hit.Play => new Rectangle(left, rowY, bs, bs),
            Hit.Prev => new Rectangle(left + step, rowY, bs, bs),
            Hit.Next => new Rectangle(left + 2 * step, rowY, bs, bs),
            // Three groups: playback; undo and redo; the tools (edit the timeline, turn and crop the picture).
            Hit.Undo => new Rectangle(left + 3 * step + P(16), rowY, bs, bs),
            Hit.Redo => new Rectangle(left + 4 * step + P(16), rowY, bs, bs),
            Hit.Split => new Rectangle(left + 5 * step + P(32), rowY, bs, bs),
            Hit.Trim => new Rectangle(left + 6 * step + P(32), rowY, bs, bs),
            Hit.Delete => new Rectangle(left + 7 * step + P(32), rowY, bs, bs),
            Hit.AddAudio => new Rectangle(left + 8 * step + P(32), rowY, bs, bs),
            Hit.RotL => new Rectangle(left + 9 * step + P(32), rowY, bs, bs),
            Hit.RotR => new Rectangle(left + 10 * step + P(32), rowY, bs, bs),
            Hit.Crop => new Rectangle(left + 11 * step + P(32), rowY, bs, bs),
            Hit.Export => new Rectangle(exportX, rowY, ExportW, bs),
            Hit.Pin => new Rectangle(pr.Right - Pad - P(30), pr.Top + P(8), P(30), P(28)),
            Hit.TrimApply => TrimButtonRect(apply: true),
            Hit.TrimCancel => TrimButtonRect(apply: false),
            Hit.More => new Rectangle(moreX, rowY, bs, bs),
            Hit.Full => new Rectangle(moreX - step, rowY, bs, bs),
            Hit.Loop => new Rectangle(moreX - 2 * step, rowY, bs, bs),
            Hit.VolSlider => new Rectangle(moreX - 2 * step - P(14) - P(96), rowY, P(96), bs),
            Hit.Vol => new Rectangle(moreX - 2 * step - P(14) - P(96) - bs - P(2), rowY, bs, bs),
            Hit.Min => new Rectangle(wb.Left, wb.Top, wb.Width / 3, wb.Height),
            Hit.Max => new Rectangle(wb.Left + wb.Width / 3, wb.Top, wb.Width / 3, wb.Height),
            Hit.Close => new Rectangle(wb.Left + 2 * wb.Width / 3, wb.Top, wb.Width - 2 * wb.Width / 3, wb.Height),
            Hit.Center => CenterRect(d),
            _ => Rectangle.Empty,
        };
    }

    const string DropCaption = "Drop a video here, or click the button to browse";

    /// <summary>
    /// The big play button: in the middle of the picture as far as it can be seen above the control panel (the space
    /// above the panel when there's no picture). With no video it's the add-video button, centred with its caption.
    /// </summary>
    Rectangle CenterRect(int d)
    {
        float top = 0, bottom = PanelRect.Top;
        var vr = VideoRectF;
        if (vr.Height > 0)
        {
            top = Math.Max(0, vr.Top);
            bottom = Math.Min(bottom, vr.Bottom);
            if (bottom - top < d) { top = 0; bottom = PanelRect.Top; }
        }
        float cx = vr.Width > 0 ? Math.Clamp(vr.Left + vr.Width / 2, d / 2f, ClientSize.Width - d / 2f) : ClientSize.Width / 2f;
        float lift = _info == null ? (P(10) + TextRenderer.MeasureText(DropCaption, _hintFont).Height) / 2f : 0;
        return new Rectangle((int)Math.Round(cx - d / 2f), (int)Math.Round((top + bottom - d) / 2 - lift), d, d);
    }

    static bool NeedsFile(Hit h) => h is not (Hit.None or Hit.Center or Hit.More or Hit.Full or Hit.Pin or Hit.Min or Hit.Max or Hit.Close);

    Hit HitTest(Point p)
    {
        bool overlay = _ui >= 0.5f;
        if (!overlay && !_pinned) return Hit.None;
        foreach (var h in HitOrder)
        {
            if (!overlay && h is Hit.Min or Hit.Max or Hit.Close or Hit.Center or Hit.CropBar or Hit.CropArea) continue;
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
            if (h == Hit.Ruler)
            {
                if (_info == null) continue;
                var (th, id) = TimelineHit(p);
                if (th == Hit.None) continue;
                _hitId = id;
                return th;
            }
            if (h == Hit.Center)
            {
                if (_info != null && (_playing || _cropping)) continue; // the big button only shows while paused
                var c = Rect(h);
                float dx = p.X - (c.Left + c.Width / 2f), dy = p.Y - (c.Top + c.Height / 2f);
                if (dx * dx + dy * dy <= c.Width * c.Width / 4f) return h;
            }
            else if (Rect(h).Contains(p) && (_info != null || !NeedsFile(h))) return h; // inactive without a file
        }
        // The panel's own background is the panel, not the picture underneath: no play / pause, fullscreen or panning.
        return (_pinned || overlay) && PanelRect.Contains(p) ? Hit.Panel : Hit.None;
    }

    RectangleF VideoRectF
    {
        get
        {
            if (_bmp == null) return RectangleF.Empty;
            // The part of the frame shown (the crop, or all of it while cropping), fitted to the window or, in crop mode,
            // to the space between the aspect bar and the control panel.
            var src = SourceRect;
            var area = ViewArea;
            float s = Math.Min(area.Width / src.Width, area.Height / src.Height) * _zoom;
            float w = src.Width * s, h = src.Height * s;
            return new RectangleF(area.X + area.Width / 2f - _center.X * w, area.Y + area.Height / 2f - _center.Y * h, w, h);
        }
    }

    /// <summary>Where the picture is fitted: above a pinned panel, else the whole window (crop mode has its own area).</summary>
    RectangleF ViewArea => _cropping ? CropEditArea
        : _pinned ? new RectangleF(0, 0, ClientSize.Width, PanelRect.Top) : new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);

    // ───────────────────────── zoom / pan ─────────────────────────

    /// <summary>Keeps the image covering the window: centred on an axis where it fits, edge-clamped where it overflows.</summary>
    void ClampCenter()
    {
        var vr = VideoRectF;
        if (vr.Width <= 0) return;
        var area = ViewArea;
        float hx = area.Width / 2f / vr.Width, hy = area.Height / 2f / vr.Height;
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
        var area = ViewArea;
        _center = new PointF(u - (anchor.X - (area.X + area.Width / 2f)) / nr.Width, v - (anchor.Y - (area.Y + area.Height / 2f)) / nr.Height);
        ClampCenter();
        Invalidate();
    }

    void ZoomCentered(float factor)
    {
        var a = ViewArea;
        ZoomAt(factor, Point.Round(new PointF(a.X + a.Width / 2, a.Y + a.Height / 2)));
    }

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

    Color C(int a, int r, int g, int b) => Color.FromArgb(Math.Clamp((int)(a * Alpha), 0, 255), r, g, b);
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
            _tracksKey = default;
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
                TextRenderer.DrawText(g, DropCaption, _hintFont,
                    new Rectangle(0, Rect(Hit.Center).Bottom + P(10), ClientSize.Width, P(40)), Color.FromArgb(140, 150, 165),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        if (_cropping && _bmp != null) DrawCropOverlay(g);
        if (_toast != null && Now < _toastUntil) DrawToast(g);
        if (_ui <= 0.01f && PanelAlpha <= 0.01f) return;
        if (_blurDirty) UpdateBlur();
        if (_ui > 0.01f) PaintOverlay(g);

        // Control panel: docked along the bottom when pinned, at full strength whatever the overlay's opacity
        _paintAlpha = PanelAlpha;
        try
        {
            var pr = PanelRect;
            if (_pinned)
            {
                using (var pp = new GraphicsPath()) { pp.AddRectangle(pr); Glass(g, pp, pr, darken: 135, outline: false); }
                using var edge = new Pen(C(70, 130, 160, 255), 1f);
                g.DrawLine(edge, pr.Left, pr.Top, pr.Right, pr.Top);
            }
            else using (var pp = RoundRect(pr, P(14))) Glass(g, pp, pr, darken: 135); // darker than the other glass: text and trim frame stay legible over bright video
            DrawPanelContents(g, pr);
            if (_cropping) DrawCropBar(g);
            if (_hintVisible && _hover != Hit.None && _drag == Drag.None) DrawHint(g, _hover);
        }
        finally { _paintAlpha = -1f; }
    }

    /// <summary>The window buttons and the big play button, which fade with the overlay.</summary>
    void PaintOverlay(Graphics g)
    {
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
            // With no video, the add-video icon on its own (lit up on hover); the play button sits in a glass circle.
            if (_info == null)
                AddVideoGlyph(g, RectangleF.Inflate(cr, -cr.Width * 0.15f, -cr.Height * 0.15f), _hover == Hit.Center ? C(255, Theme.AccentHover) : W(235));
            else
            {
                using (var cp = new GraphicsPath()) { cp.AddEllipse(cr); Glass(g, cp, cr, _hover == Hit.Center ? 40 : 60); }
                var gr = RectangleF.Inflate(cr, -cr.Width * 0.3f, -cr.Height * 0.3f);
                PlayGlyph(g, new RectangleF(gr.X + gr.Width * 0.08f, gr.Y, gr.Width, gr.Height), W(245));
            }
        }
    }

    void DrawPanelContents(Graphics g, Rectangle pr)
    {
        using var titleBrush = new SolidBrush(W(245));
        using var subBrush = new SolidBrush(W(160));
        using var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };

        string title = _info != null ? Path.GetFileName(_info.Path) : "Splicia";
        string time = _info != null ? $"{Fmt(_pos)} / {Fmt(PosEnd)}" : "";
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
            int n = _tl.Clips.Count, m = _tl.Music.Count;
            sel = $"{n} clip{(n == 1 ? "" : "s")}{(m > 0 ? $"  ·  {m} sound{(m == 1 ? "" : "s")}" : "")}  ·  {Length(_tl.End)}";
            if (Trimming)
            {
                sub = "Trim: drag the yellow handles, or the grip on the ruler to slide the selection";
                sel = "";
            }
        }

        float timeW = g.MeasureString(time, _timeFont).Width, selW = g.MeasureString(sel, _smallFont).Width;
        if (Trimming) selW = pr.Right - Pad - TrimButtonRect(apply: false).Left; // the Apply and Cancel buttons are there
        float pinW = P(40); // the pin button at the top right
        g.DrawString(title, _titleFont, titleBrush, new RectangleF(pr.Left + Pad, pr.Top + P(14), pr.Width - 2 * Pad - timeW - pinW - P(16), P(26)), sf);
        g.DrawString(time, _timeFont, titleBrush, pr.Right - Pad - pinW - timeW, pr.Top + P(16));
        g.DrawString(sub, _smallFont, subBrush, new RectangleF(pr.Left + Pad, pr.Top + P(41), pr.Width - 2 * Pad - selW - P(16), P(18)), sf);
        using (var selBrush = new SolidBrush(C(230, Theme.AccentHover))) g.DrawString(sel, _smallFont, selBrush, pr.Right - Pad - selW, pr.Top + P(41));

        DrawTimeline(g);
        if (Trimming) DrawTrimButtons(g);

        bool has = _info != null;
        DrawVolumeSlider(g, has);
        foreach (var h in new[] { Hit.Play, Hit.Prev, Hit.Next, Hit.Undo, Hit.Redo, Hit.Split, Hit.Trim, Hit.Delete, Hit.AddAudio, Hit.RotL, Hit.RotR, Hit.Crop, Hit.Vol, Hit.Loop, Hit.Full, Hit.More, Hit.Pin })
        {
            var r = Rect(h);
            bool enabled = (has || h is Hit.More or Hit.Full or Hit.Pin) && (h != Hit.Delete || _selClip >= 0 || _selMusic >= 0)
                && (h != Hit.Undo || _undo.Count > 0) && (h != Hit.Redo || _redo.Count > 0)
                && !(Trimming && h is Hit.Undo or Hit.Redo or Hit.Split or Hit.Delete or Hit.AddAudio);
            if (_hover == h && enabled)
                using (var hb = new SolidBrush(W(34))) g.FillEllipse(hb, r);
            var col = !enabled ? W(70) : h == Hit.Loop && !_loop ? W(110) : W(240);
            var gr = RectangleF.Inflate(r, -r.Width * 0.24f, -r.Height * 0.24f);
            switch (h)
            {
                case Hit.Play: if (_playing) PauseGlyph(g, gr, col); else PlayGlyph(g, gr, col); break;
                case Hit.Prev: StepGlyph(g, gr, col, forward: false); break;
                case Hit.Next: StepGlyph(g, gr, col, forward: true); break;
                case Hit.Undo: UndoGlyph(g, gr, col, redo: false); break;
                case Hit.Redo: UndoGlyph(g, gr, col, redo: true); break;
                case Hit.Split: ScissorsGlyph(g, gr, enabled && _hover == h ? C(255, Theme.VioletHover) : col); break;
                case Hit.Trim: TrimGlyph(g, gr, Trimming || enabled && _hover == h ? C(255, TrimColor) : col); break;
                case Hit.Delete: TrashGlyph(g, gr, col); break;
                case Hit.AddAudio: AddSoundGlyph(g, gr, enabled && _hover == h ? C(255, AudioColor) : col); break;
                case Hit.RotL: RotateGlyph(g, gr, col, clockwise: false); break;
                case Hit.RotR: RotateGlyph(g, gr, col, clockwise: true); break;
                case Hit.Crop: CropGlyph(g, gr, enabled && (HasCrop || _cropping) ? C(255, Theme.AccentHover) : col); break;
                case Hit.Vol: SpeakerGlyph(g, gr, col, _muted || _volume == 0); break;
                case Hit.Loop: LoopGlyph(g, gr, col); break;
                case Hit.Full: FullGlyph(g, gr, col, _fullscreen); break;
                case Hit.More: MoreGlyph(g, gr, col); break;
                case Hit.Pin: PinGlyph(g, gr, _pinned ? C(255, Theme.AccentHover) : col, _pinned); break;
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

    void DrawFaded(Graphics g, Image img, RectangleF dest)
    {
        if (Alpha >= 0.999f) { g.DrawImage(img, dest); return; }
        using var ia = new ImageAttributes();
        ia.SetColorMatrix(new ColorMatrix { Matrix33 = Alpha });
        g.DrawImage(img, Rectangle.Round(dest), 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
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
        Hit.Undo => _undo.Count > 0 ? "Undo  (Ctrl+Z)" : "Nothing to undo",
        Hit.Redo => _redo.Count > 0 ? "Redo  (Ctrl+Y)" : "Nothing to redo",
        Hit.Split => "Split the clip at the playhead  (S)",
        Hit.Trim => Trimming ? "Apply the trim  (T or Enter; Esc cancels)" : "Trim: keep only a part of the timeline  (T)",
        Hit.TrimStart => "Drag where the selection starts  (I or [ at the playhead)",
        Hit.TrimEnd => "Drag where the selection ends  (O or ] at the playhead)",
        Hit.TrimGrip => "Drag to slide the selection along the timeline",
        Hit.TrimApply => "Keep only the selection and carry on editing  (Enter)",
        Hit.TrimCancel => "Leave the timeline as it was  (Esc)",
        Hit.Delete => "Delete the selected clip  (Delete; Shift+Delete closes the gap)",
        Hit.AddAudio => "Add sound: WAV, MP3…  (or drop files on the audio track)",
        Hit.RotL => "Rotate left  (Shift+R)",
        Hit.RotR => "Rotate right  (R)",
        Hit.Crop => _cropping ? "Finish cropping  (C or Enter)" : HasCrop ? "Change the crop  (C)" : "Crop  (C)",
        Hit.Vol => _muted ? "Unmute  (M)" : "Mute  (M)",
        Hit.VolSlider => "Volume  (↑ / ↓)",
        Hit.Loop => _loop ? "Loop: on" : "Loop: off",
        Hit.Full => _fullscreen ? "Exit fullscreen  (F)" : "Fullscreen  (F)",
        Hit.More => "More",
        Hit.Pin => _pinned ? "Unpin the panel: it floats over the video and hides when idle  (P)" : "Pin the panel under the video  (P)",
        Hit.Export => "Export the timeline  (Ctrl+E)",
        Hit.Clip => "Drag to move or rearrange; right-click for fades and more",
        Hit.ClipFadeIn or Hit.MusicFadeIn => "Drag to fade in",
        Hit.ClipFadeOut or Hit.MusicFadeOut => "Drag to fade out",
        Hit.Junction => _tl.Find(_hoverId) is Clip j ? $"{j.Next}: click to change" : null,
        Hit.Music => "Drag to move; drag its ends to trim; right-click for fades",
        Hit.MusicStart or Hit.MusicEnd => "Drag to trim",
        _ => null,
    };

    void DrawHint(Graphics g, Hit h)
    {
        if (HintText(h) is not string text) return;
        var mouse = PointToClient(MousePosition);
        var anchor = IsTimelineHit(h) ? new RectangleF(mouse.X, RulerRect.Top, 1, 1) : Rect(h);
        if (h is Hit.Min or Hit.Max or Hit.Close or Hit.Center or Hit.Ruler or Hit.Track or Hit.MusicLane) return;
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
    void Glass(Graphics g, GraphicsPath path, Rectangle bounds, int tint = 70, int darken = 0, bool outline = true)
    {
        var st = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var black = new SolidBrush(C(200, Theme.Bg))) g.FillRectangle(black, bounds);
        if (_blur != null)
        {
            using var ia = new ImageAttributes();
            ia.SetWrapMode(WrapMode.TileFlipXY);
            if (Alpha < 1f) ia.SetColorMatrix(new ColorMatrix { Matrix33 = Alpha }); // colour matrix is the slow path; only while fading
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
        if (!outline) return;
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

    /// <summary>Pin button: a push pin, upright when the panel is pinned, tipped over when it isn't.</summary>
    void PinGlyph(Graphics g, RectangleF r, Color c, bool pinned)
    {
        var st = g.Save();
        float w = r.Width, h = r.Height, cx = r.X + w / 2, cy = r.Y + h / 2;
        if (!pinned) { g.TranslateTransform(cx, cy); g.RotateTransform(45); g.TranslateTransform(-cx, -cy); }
        using var pen = GlyphPen(c, 1.7f);
        g.DrawLine(pen, r.X + w * 0.28f, r.Y + h * 0.04f, r.X + w * 0.72f, r.Y + h * 0.04f);
        g.DrawLines(pen, new[]
        {
            new PointF(r.X + w * 0.38f, r.Y + h * 0.04f), new PointF(r.X + w * 0.38f, r.Y + h * 0.4f), new PointF(r.X + w * 0.16f, r.Y + h * 0.6f),
            new PointF(r.X + w * 0.84f, r.Y + h * 0.6f), new PointF(r.X + w * 0.62f, r.Y + h * 0.4f), new PointF(r.X + w * 0.62f, r.Y + h * 0.04f),
        });
        g.DrawLine(pen, cx, r.Y + h * 0.6f, cx, r.Bottom);
        g.Restore(st);
    }

    /// <summary>P / the pin button: the panel docked under the picture (always shown), or floating over it (hiding when idle).</summary>
    void TogglePin()
    {
        _pinned = !_pinned;
        _quietUntil = Now + SystemInformation.DoubleClickTime / 1000.0;
        _hover = Hit.None; // the buttons moved
        _hintVisible = false;
        ClampCenter();
        _blurDirty = true;
        Activity();
        Invalidate();
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
        if (_drag == Drag.Volume) { Activity(); DragTo(e.X); return; }
        if (_drag != Drag.None) { Activity(); TimelineDrag(e.X); return; }

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
        bool onPanel = h == Hit.Panel;
        if (onPanel) h = Hit.None; // nothing to hover there
        int item = h == Hit.CropBar ? CropItemAt(e.Location) : int.MinValue;
        if (item != _hoverItem) { _hoverItem = item; Invalidate(CropBarRect); }
        int id = IsTimelineHit(h) ? _hitId : -1;
        if (h != _hover || id != _hoverId)
        {
            if (_hintVisible) Invalidate(); else { InvalidateHit(_hover); InvalidateHit(h); }
            _hover = h;
            _hoverId = id;
            _hoverSince = Now;
            _hintVisible = false;
        }
        Cursor = h is Hit.MusicStart or Hit.MusicEnd or Hit.ClipFadeIn or Hit.ClipFadeOut or Hit.MusicFadeIn or Hit.MusicFadeOut
                or Hit.TrimStart or Hit.TrimEnd ? Cursors.SizeWE
            : h is Hit.Clip or Hit.Music or Hit.TrimGrip ? Cursors.SizeAll
            : h == Hit.CropArea ? CropCursor(CropPartAt(e.Location)) : h == Hit.CropBar && item == int.MinValue ? Cursors.Default
            : h != Hit.None ? Cursors.Hand : _zoom > 1f && !onPanel ? Cursors.SizeAll : Cursors.Default;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != Hit.None) { _hover = Hit.None; _hintVisible = false; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right && _info != null && (_ui >= 0.5f || _pinned) && _drag == Drag.None)
        {
            var hr = HitTest(e.Location);
            Activity();
            if (hr is Hit.Clip or Hit.ClipFadeIn or Hit.ClipFadeOut) { if (_playing) Pause(); ShowClipMenu(_hitId, e.Location); }
            else if (hr is Hit.Music or Hit.MusicStart or Hit.MusicEnd or Hit.MusicFadeIn or Hit.MusicFadeOut) { if (_playing) Pause(); ShowMusicMenu(_hitId, e.Location); }
            else if (hr == Hit.Junction) { if (_playing) Pause(); ShowTransitionMenu(_hitId, e.Location); }
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        var h = HitTest(e.Location);
        if (h != Hit.None) Activity();
        _hintVisible = false;
        // Clicking the pin moves it with the panel: a quick click again where it was isn't meant for what's there now.
        if (h is Hit.None or Hit.Panel && Now < _quietUntil) { _ignoreUp = true; return; }
        if (h == Hit.CropArea)
        {
            if (e.Clicks == 2 && CropPartAt(e.Location) == CropPart.Move) { FinishCrop(apply: true); return; } // double-click = done
            BeginCropDrag(e.Location);
            return;
        }
        if (h == Hit.CropBar) _pressedItem = CropItemAt(e.Location);
        if (h == Hit.Clip && e.Clicks == 2) { StartTrim(_hitId); return; }
        if (_info != null && IsTimelineHit(h) && h != Hit.Junction)
        {
            TimelineDown(h, e);
            return;
        }
        if (_info != null && h == Hit.VolSlider)
        {
            _drag = Drag.Volume;
            Capture = true;
            DragTo(e.X);
            return;
        }
        _pressed = h;
        _pressedId = h == Hit.Junction ? _hitId : -1;
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
        if (_drag != Drag.Volume) return;
        var r = Rect(Hit.VolSlider);
        float l = r.Left + P(6), w = r.Width - P(12);
        SetVolume((x - l) / w);
    }

    int _pressedId = -1;

    /// <summary>The mouse went elsewhere mid-drag (a window opened from a key, another app): the drag ends where it got to.</summary>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || _drag == Drag.None) return;
        if (_drag == Drag.Crop) EndCropDrag();
        else if (_drag == Drag.Volume) _drag = Drag.None;
        else TimelineUp(_dragX);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag == Drag.Crop) { EndCropDrag(); return; }
        if (_drag == Drag.Volume) { _drag = Drag.None; Capture = false; return; }
        if (_drag != Drag.None) { TimelineUp(e.X); return; }
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
            case Hit.Junction:
                if (_hitId == _pressedId)
                {
                    if (_playing) Pause();
                    ShowTransitionMenu(_hitId, new Point(e.X, WaveRect.Bottom + P(8)));
                }
                break;
            case Hit.Undo: Undo(); break;
            case Hit.Redo: Redo(); break;
            case Hit.Split: SplitHere(); break;
            case Hit.Trim: StartTrim(); break;
            case Hit.TrimApply: FinishTrim(keep: true); break;
            case Hit.TrimCancel: FinishTrim(keep: false); break;
            case Hit.Delete: DeleteSelected(close: (ModifierKeys & Keys.Shift) != 0); break;
            case Hit.AddAudio: AddAudioDialog(); break;
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
            case Hit.RotL: Rotate(-90); break;
            case Hit.RotR: Rotate(+90); break;
            case Hit.Vol: ToggleMute(); break;
            case Hit.Loop: ToggleLoop(); ShowToast(_loop ? "Loop: on" : "Loop: off"); break;
            case Hit.Full: ToggleFullscreen(); break;
            case Hit.More: ShowMenu(); break;
            case Hit.Pin: TogglePin(); break;
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
            _ when IsTimelineHit(h) => PanelRect,
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
            case Keys.T: StartTrim(); return true;
            case Keys.Enter when Trimming: FinishTrim(keep: true); return true;
            case Keys.Escape when Trimming: FinishTrim(keep: false); return true;
            case Keys.I: case Keys.OemOpenBrackets: TrimAtPlayhead(start: true); return true;
            case Keys.O: case Keys.OemCloseBrackets: TrimAtPlayhead(start: false); return true;
            case Keys.Enter when _cropping: FinishCrop(apply: true); return true;
            case Keys.Escape when _cropping: FinishCrop(apply: false); return true;
            case Keys.S: SplitHere(); return true;
            case Keys.Delete: case Keys.Back: DeleteSelected(close: false); return true;
            case Keys.Shift | Keys.Delete: case Keys.Shift | Keys.Back: DeleteSelected(close: true); return true;
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.Y: case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
            case Keys.Space: TogglePlay(); return true;
            case Keys.Left: Step(-1); return true;
            case Keys.Right: Step(+1); return true;
            case Keys.Shift | Keys.Left: Jump(-1); return true;
            case Keys.Shift | Keys.Right: Jump(+1); return true;
            case Keys.Control | Keys.Left: case Keys.PageUp: Jump(-10); return true;
            case Keys.Control | Keys.Right: case Keys.PageDown: Jump(+10); return true;
            case Keys.Home: GoTo(Trimming ? _trimIn : 0); return true;
            case Keys.End: GoTo((Trimming ? _trimOut : _tl.End) - FrameDur); return true;
            case Keys.R: Rotate(+90); return true;
            case Keys.Shift | Keys.R: Rotate(-90); return true;
            case Keys.Up: SetVolume((_muted ? 0 : _volume) + 0.05f); return true;
            case Keys.Down: SetVolume((_muted ? 0 : _volume) - 0.05f); return true;
            case Keys.M: ToggleMute(); return true;
            case Keys.P when _drag == Drag.None: TogglePin(); return true;
            case Keys.Control | Keys.E: Export(); return true;
            case Keys.Control | Keys.O: OpenDialog(); return true;
            case Keys.F11 when _drag == Drag.None: case Keys.F when _drag == Drag.None: ToggleFullscreen(); return true;
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
        bool edit = has && !Trimming; // timeline edits wait for the trim to be kept or cancelled
        _menu.Items.Add(Item("Add sound…", AddAudioDialog, null, edit));
        _menu.Items.Add(Item("Export…", Export, "Ctrl+E", has));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Undo", Undo, "Ctrl+Z", !Trimming && _undo.Count > 0));
        _menu.Items.Add(Item("Redo", Redo, "Ctrl+Y", !Trimming && _redo.Count > 0));
        _menu.Items.Add(Item("Split at the playhead", SplitHere, "S", edit && _tl.At(_pos) != null));
        _menu.Items.Add(Item(Trimming ? "Apply the trim" : "Trim…", () => StartTrim(), "T", has));
        if (Trimming) _menu.Items.Add(Item("Cancel the trim", () => FinishTrim(keep: false), "Esc"));
        _menu.Items.Add(Item("Close gaps", CloseGaps, null, edit && _tl.HasGaps));
        if (edit && _tl.Clips.Count > 0)
        {
            var first = _tl.Clips[0];
            var last = _tl.Clips[^1];
            _menu.Items.Add(FadeMenu("Fade in at the start", first.FadeIn, VideoFades, v => SetClipFades(first.Id, v, null)));
            _menu.Items.Add(FadeMenu("Fade out at the end", last.FadeOut, VideoFades, v => SetClipFades(last.Id, null, v)));
        }
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("Rotate right", () => Rotate(+90), "R", has));
        _menu.Items.Add(Item("Rotate left", () => Rotate(-90), "Shift+R", has));
        _menu.Items.Add(Item("Reset rotation", () => Rotate(-_rot), null, has && _rot != 0));
        _menu.Items.Add(Item(_cropping ? "Finish cropping" : "Crop…", ToggleCrop, "C", has));
        _menu.Items.Add(Item("Remove crop", () => { ResetCrop(); FinishCrop(apply: true); }, null, has && HasCrop));
        _menu.Items.Add(Item((_loop ? "✓  " : "     ") + "Loop", ToggleLoop, null, has));
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
        _pos = 0;
        _rot = 0;
        _crop = _cropBefore = FullFrame;
        _cropping = false;
        _aspect = 0;
        _tl = Timeline.Whole(_info);
        _undo.Clear();
        _redo.Clear();
        _selClip = _selMusic = -1;
        if (_info.HasAudio)
        {
            _videoPeaks = new Peaks(path, _info.Duration, this);
            _videoPeaks.Updated += () => { _peaksVersion++; if (PanelAlpha > 0) Invalidate(PanelRect); };
        }
        _strip = new ThumbnailStrip(_info, this, P(46) * 2);
        _strip.Updated += () => { _stripVersion++; if (PanelAlpha > 0) Invalidate(PanelRect); };
        Text = $"{Path.GetFileName(path)} — Splicia";
        ResetZoom();
        _tlVersion++;
        UpdateView();
        ShowFrameAt(0);
        Activity();
        Invalidate();
    }

    void CloseFile()
    {
        if (_playing) Pause();
        _drag = Drag.None;
        _dragBase = null;
        _dragId = -1;
        _trimming = false;
        _strip?.Dispose();
        _strip = null;
        _videoPeaks?.Dispose();
        _videoPeaks = null;
        foreach (var pk in _musicPeaks.Values) pk.Dispose();
        _musicPeaks.Clear();
        _pb?.Dispose();
        _pb = null;
        _info = null;
        _shown = null;
        _bmp?.Dispose();
        _bmp = null;
        _blur?.Dispose();
        _blur = null;
        _tracks?.Dispose();
        _tracks = null;
        _viewKey = default;
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
        // While trimming, just the selection, as applying it would leave the timeline; the trim stays to apply or cancel.
        var tl = Trimming ? Kept() : _tl;
        if (tl.End <= 0) { ShowToast(Trimming ? "Nothing in the selection" : "The timeline is empty"); return; }
        var job = CurrentJob(tl);
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
        if (_tl.End <= 0) { ShowToast("The timeline is empty"); return; }
        if (PlayEnd - PlayBase < FrameDur / 2) { ShowToast("Nothing in the selection"); return; }
        double from = _pos;
        // Outside what's played (trim mode), or at (or right before) its end: from its start.
        if (from < PlayBase || from >= PlayEnd - FrameDur * 1.5) from = PlayBase;
        _playing = true;
        _pb.Play(from - PlayBase, PlayList(), _loop);
        timeBeginPeriod(1);
        _timer.Interval = 4;
        Invalidate();
    }

    void ToggleLoop()
    {
        _loop = !_loop;
        if (_pb != null) _pb.Loop = _loop;
    }

    void Pause()
    {
        if (!_playing) return;
        _playing = false;
        _pb?.Pause();
        timeEndPeriod(1);
        _timer.Interval = 10;
        ShowFrameAt(_pos); // the real frame (a crossfade or fade only blends or darkens it while playing)
        Invalidate();
    }

    void Step(int delta)
    {
        if (_pb == null) return;
        Pause();
        _pos = Math.Clamp(_tl.Snap(_pos + delta * FrameDur), 0, Math.Max(0, PosEnd - FrameDur / 2));
        ShowFrameAt(_pos);
        Invalidate(PanelRect);
    }

    void Jump(double seconds)
    {
        if (_pb == null) return;
        GoTo(_pos + seconds);
    }

    void GoTo(double t)
    {
        if (_pb == null) return;
        t = Math.Clamp(_tl.Snap(t), 0, Math.Max(0, PosEnd - FrameDur / 2));
        _pos = t;
        if (_playing && (t < PlayBase || t >= PlayEnd)) Pause(); // trim mode: outside the part played
        if (_playing) _pb.Play(t - PlayBase, PlayList(), _loop);
        else ShowFrameAt(t);
        Invalidate(PanelRect);
    }

    void Present(VideoFrame f, bool updatePos)
    {
        if (_shown != null && !ReferenceEquals(_shown, f)) _pb?.Recycle(_shown);
        _shown = f;
        if (updatePos)
        {
            // Playing frames of later rounds (looping) carry their round's time: back onto the timeline.
            double len = PlayEnd - PlayBase;
            _pos = _playing && len > 0 ? PlayBase + Math.Max(0, f.Out - Math.Floor((f.Out + 1e-6) / len) * len) : f.Out;
        }
        RenderFrame();
    }

    void Tick()
    {
        if (_busy > 0) return;
        if (_pb != null)
        {
            if (_playing)
            {
                // The player plays the edit itself (cuts, transitions, looping); the frames carry their file time.
                var f = _pb.TakeDue(_pb.Clock);
                if (f != null) Present(f, updatePos: true);
                if (_pb.Ended) { Pause(); Activity(); }
            }
            else if (_pb.TakeStill() is VideoFrame still)
            {
                bool exact = still.Exact && _drag == Drag.None;
                Present(still, updatePos: exact);
            }
        }

        // Hover hint after resting on a control
        if (_hover != Hit.None && !_hintVisible && _drag == Drag.None && Now - _hoverSince > 0.6 && (_ui >= 1f || _pinned))
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
        bool menuOpen = _menu.Visible || _clipMenu.Visible;
        if (_info != null && !mouseInside && _drag == Drag.None && !menuOpen && !_cropping) HideOverlayNow();
        bool restingOnUi = _ui > 0f && !_panning && (PanelRect.Contains(mouse) || WindowButtonsRect.Contains(mouse));
        bool show = _info == null || _cropping || _drag != Drag.None || menuOpen || restingOnUi || Now - _lastActivity < HideAfterSeconds;
        float target = show ? 1f : 0f;
        if (_ui != target)
        {
            _ui = show ? Math.Min(1f, _ui + 0.2f) : Math.Max(0f, _ui - 0.08f);
            if (_ui == 0f && _hover != Hit.None && !(_pinned && PanelRect.Contains(mouse))) { _hover = Hit.None; _hintVisible = false; }
            Invalidate();
        }
        // Cursor.Hide() hides the pointer for every window of the app (the export window included), so only while
        // this window is the active one and the pointer is over it.
        bool hideCursor = _ui == 0f && mouseInside && !(_pinned && PanelRect.Contains(mouse)) && !_dragArmed && ActiveForm == this;
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
