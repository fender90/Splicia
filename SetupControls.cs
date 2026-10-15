using System.Drawing.Drawing2D;

namespace VideoTrimmer;

/// <summary>Colours and helpers shared by the setup wizard (same palette as the player and its dialogs).</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(24, 26, 32), Field = Color.FromArgb(36, 39, 47),
        Fg = Color.FromArgb(232, 234, 238), Dim = Color.FromArgb(150, 156, 168), Faint = Color.FromArgb(105, 110, 122),
        Accent = Color.FromArgb(70, 140, 255), AccentHover = Color.FromArgb(98, 160, 255), AccentDown = Color.FromArgb(55, 118, 225),
        Btn = Color.FromArgb(52, 56, 66), BtnHover = Color.FromArgb(66, 71, 84), BtnDown = Color.FromArgb(44, 47, 56),
        Danger = Color.FromArgb(222, 72, 72), DangerHover = Color.FromArgb(236, 96, 96), Good = Color.FromArgb(96, 196, 128),
        Border = Color.FromArgb(64, 68, 80);

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>Small shield glyph: marks actions that need administrator permission (Windows convention).</summary>
    public static void DrawShield(Graphics g, RectangleF r, Color c)
    {
        float l = r.Left, t = r.Top, w = r.Width, h = r.Height, cx = l + w / 2;
        using var p = new GraphicsPath();
        p.AddLine(cx, t, l + w, t + h * 0.17f);
        p.AddLine(l + w, t + h * 0.17f, l + w, t + h * 0.48f);
        p.AddBezier(l + w, t + h * 0.48f, l + w, t + h * 0.78f, cx + w * 0.2f, t + h * 0.93f, cx, t + h);
        p.AddBezier(cx, t + h, cx - w * 0.2f, t + h * 0.93f, l, t + h * 0.78f, l, t + h * 0.48f);
        p.AddLine(l, t + h * 0.48f, l, t + h * 0.17f);
        p.CloseFigure();
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }
}

/// <summary>Base for owner-drawn controls: double-buffered, paints its parent's background colour first.</summary>
abstract class DrawnControl : Control
{
    protected DrawnControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
    }

    protected bool Hover, Down;
    protected int Px(float logical) => (int)Math.Round(logical * DeviceDpi / 96f);

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = false; Down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { Down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected void PaintBackground(Graphics g)
    {
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }
}

/// <summary>Rounded pill button, like the player's controls.</summary>
sealed class PillButton : DrawnControl, IButtonControl
{
    public enum Kind { Primary, Secondary, Danger, Ghost }
    public Kind Style { get; set; } = Kind.Secondary;
    public DialogResult DialogResult { get; set; }

    /// <summary>Show the administrator shield before the text (this action will trigger a UAC prompt).</summary>
    public bool Shield { get; set; }

    public PillButton(string text, Kind style = Kind.Secondary)
    {
        Text = text;
        Style = style;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 10f);
    }

    public void NotifyDefault(bool value) { }
    public void PerformClick() { if (Enabled && Visible) OnClick(EventArgs.Empty); }

    public override Size GetPreferredSize(Size proposed) =>
        new(Math.Max(Px(96), TextRenderer.MeasureText(Text, Font).Width + Px(40) + (Shield ? Px(22) : 0)), Px(38));

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter) { PerformClick(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        (Color fill, Color text) = Style switch
        {
            Kind.Primary => (Down ? Theme.AccentDown : Hover ? Theme.AccentHover : Theme.Accent, Color.White),
            Kind.Danger => (Down ? Theme.Blend(Theme.Danger, Color.Black, 0.15f) : Hover ? Theme.DangerHover : Theme.Danger, Color.White),
            Kind.Ghost => (Down ? Theme.BtnDown : Hover ? Theme.Btn : Parent?.BackColor ?? Theme.Bg, Hover ? Theme.Fg : Theme.Dim),
            _ => (Down ? Theme.BtnDown : Hover ? Theme.BtnHover : Theme.Btn, Theme.Fg),
        };
        if (!Enabled) { fill = Theme.Blend(fill, Parent?.BackColor ?? Theme.Bg, 0.55f); text = Theme.Faint; }
        using (var path = Theme.Round(r, r.Height / 2))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (Focused && ShowFocusCues)
                using (var p = new Pen(Theme.AccentHover, Px(2))) g.DrawPath(p, path);
        }
        if (!Shield)
        {
            TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), text, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            return;
        }
        int tw = TextRenderer.MeasureText(Text, Font).Width, icon = Px(14), gap = Px(6);
        float x = (Width - (icon + gap + tw)) / 2f;
        Theme.DrawShield(g, new RectangleF(x, (Height - icon) / 2f, icon * 0.85f, icon), text);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(x + icon + gap) - Px(4), 0, tw + Px(8), Height), text, fill,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>Selectable card with a radio dot, title, optional badge / shield and a wrapped description.</summary>
sealed class OptionCard : DrawnControl
{
    bool _selected;
    string _description;
    public string Title { get; }
    public string Description { get => _description; set { _description = value; Invalidate(); } }
    public string Badge { get; init; } = "";
    public bool ShowShield { get; init; }
    public event EventHandler? SelectedChanged;

    readonly Font _title = new("Segoe UI Semibold", 11f), _desc = new("Segoe UI", 9.5f), _badge = new("Segoe UI Semibold", 8f);

    public OptionCard(string title, string description)
    {
        Title = title;
        _description = description;
        Text = title;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
    }

    public bool Selected
    {
        get => _selected;
        set { if (_selected == value) return; _selected = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); }
    }

    protected override void OnClick(EventArgs e) { Selected = true; Focus(); base.OnClick(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Selected = true; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color fill = Selected ? Theme.Blend(Theme.Field, Theme.Accent, 0.13f) : Hover ? Theme.Blend(Theme.Field, Color.White, 0.04f) : Theme.Field;
        using (var path = Theme.Round(r, Px(12)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using var border = new Pen(Selected ? Theme.Accent : Focused && ShowFocusCues ? Theme.AccentHover : Theme.Border, Selected ? Px(1.6f) : 1f);
            g.DrawPath(border, path);
        }

        // radio dot
        float d = Px(18), rx = Px(18), ry = Px(17);
        using (var ring = new Pen(Selected ? Theme.Accent : Theme.Dim, Px(1.6f))) g.DrawEllipse(ring, rx, ry, d, d);
        if (Selected) using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, rx + d * 0.25f, ry + d * 0.25f, d * 0.5f, d * 0.5f);

        int tx = Px(50), ty = Px(13);
        var titleSize = TextRenderer.MeasureText(Title, _title);
        TextRenderer.DrawText(g, Title, _title, new Point(tx, ty), Theme.Fg, fill);
        float after = tx + titleSize.Width + Px(2);
        if (ShowShield)
        {
            float s = Px(15);
            Theme.DrawShield(g, new RectangleF(after, ty + (titleSize.Height - s) / 2f, s * 0.85f, s), Theme.Dim);
            after += s + Px(6);
        }
        if (Badge.Length > 0)
        {
            var bs = TextRenderer.MeasureText(Badge, _badge);
            var br = new RectangleF(after + Px(4), ty + (titleSize.Height - (bs.Height + Px(4))) / 2f, bs.Width + Px(12), bs.Height + Px(4));
            var bfill = Theme.Blend(fill, Theme.Accent, 0.25f);
            using (var bp = Theme.Round(br, br.Height / 2))
            using (var bb = new SolidBrush(bfill)) g.FillPath(bb, bp);
            TextRenderer.DrawText(g, Badge, _badge, Rectangle.Round(br), Theme.AccentHover, bfill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        TextRenderer.DrawText(g, Description, _desc, new Rectangle(tx, ty + titleSize.Height + Px(4), Width - tx - Px(18), Height),
            Theme.Dim, fill, TextFormatFlags.WordBreak);
    }
}

/// <summary>Pill toggle switch with a label and optional description underneath.</summary>
sealed class ToggleSwitch : DrawnControl
{
    bool _checked;
    public string Description { get; set; } = "";
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); CheckedChanged?.Invoke(this, EventArgs.Empty); }
    }

    readonly Font _desc = new("Segoe UI", 9f);

    public ToggleSwitch(string text, bool on, string description = "")
    {
        Text = text;
        _checked = on;
        Description = description;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 10.5f);
    }

    public override Size GetPreferredSize(Size proposed) => new(proposed.Width, Px(Description.Length > 0 ? 46 : 28));

    protected override void OnClick(EventArgs e) { Checked = !Checked; Focus(); base.OnClick(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        float tw = Px(42), th = Px(24), ty = Px(2);
        var track = new RectangleF(0.5f, ty, tw, th);
        Color fill = Checked ? (Hover ? Theme.AccentHover : Theme.Accent) : (Hover ? Theme.BtnHover : Theme.Btn);
        if (!Enabled) fill = Theme.Blend(fill, Theme.Bg, 0.55f);
        using (var path = Theme.Round(track, th / 2))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (Focused && ShowFocusCues) using (var p = new Pen(Theme.AccentHover, Px(2))) g.DrawPath(p, path);
        }
        float d = th - Px(6), kx = Checked ? track.Right - d - Px(3) : track.Left + Px(3);
        using (var kb = new SolidBrush(Enabled ? Color.White : Theme.Dim)) g.FillEllipse(kb, kx, ty + Px(3), d, d);

        int tx = Px(56);
        TextRenderer.DrawText(g, Text, Font, new Point(tx, (int)ty + Px(1)), Enabled ? Theme.Fg : Theme.Faint, BackColor);
        if (Description.Length > 0)
            TextRenderer.DrawText(g, Description, _desc, new Point(tx, (int)ty + Px(23)), Theme.Dim, BackColor);
    }
}

/// <summary>Thin rounded progress bar (styled like the player's seek bar) that eases towards its target value.</summary>
sealed class ThinProgress : DrawnControl
{
    double _shown, _target;
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };

    public ThinProgress()
    {
        _anim.Tick += (_, _) =>
        {
            _shown += (_target - _shown) * 0.18;
            if (Math.Abs(_target - _shown) < 0.002) { _shown = _target; _anim.Stop(); }
            Invalidate();
        };
    }

    public double Value { get => _target; set { _target = Math.Clamp(value, 0, 1); _anim.Start(); } }
    public bool Settled => _shown == _target;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        float h = Px(6), y = (Height - h) / 2f;
        using (var track = Theme.Round(new RectangleF(0, y, Width - 1, h), h / 2))
        using (var tb = new SolidBrush(Theme.Btn)) g.FillPath(tb, track);
        float w = (float)((Width - 1) * _shown);
        if (w > h)
            using (var fill = Theme.Round(new RectangleF(0, y, w, h), h / 2))
            using (var fb = new LinearGradientBrush(new RectangleF(0, y, Math.Max(w, 1), h), Theme.Accent, Theme.AccentHover, 0f))
                g.FillPath(fb, fill);
    }

    protected override void Dispose(bool disposing) { if (disposing) _anim.Dispose(); base.Dispose(disposing); }
}

/// <summary>Minimise / close buttons for the borderless window, in a frosted pill like the player's.</summary>
sealed class ChromeButton : DrawnControl
{
    public enum Kind { Minimize, Close }
    readonly Kind _kind;

    public ChromeButton(Kind kind)
    {
        _kind = kind;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        if (Hover)
            using (var path = Theme.Round(r, r.Height / 2))
            using (var b = new SolidBrush(_kind == Kind.Close ? Theme.Danger : Theme.BtnHover)) g.FillPath(b, path);
        using var pen = new Pen(Enabled ? (Hover ? Color.White : Theme.Fg) : Theme.Faint, Px(1.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float s = Px(9), cx = Width / 2f, cy = Height / 2f;
        if (_kind == Kind.Minimize) g.DrawLine(pen, cx - s / 2, cy, cx + s / 2, cy);
        else { g.DrawLine(pen, cx - s / 2, cy - s / 2, cx + s / 2, cy + s / 2); g.DrawLine(pen, cx + s / 2, cy - s / 2, cx - s / 2, cy + s / 2); }
    }
}

/// <summary>Rounded text field for the install folder.</summary>
sealed class PathBox : DrawnControl
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Fg };

    public PathBox()
    {
        Font = new Font("Segoe UI", 10f);
        Box.Font = Font;
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = Px(14);
        Box.SetBounds(pad, (Height - Box.PreferredHeight) / 2, Width - 2 * pad, Box.PreferredHeight);
    }

    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Theme.Round(r, Px(10));
        using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
        using var p = new Pen(Box.Focused ? Theme.Accent : Theme.Border, Box.Focused ? Px(1.5f) : 1f);
        g.DrawPath(p, path);
    }
}

/// <summary>Big round success / error badge.</summary>
sealed class ResultBadge : DrawnControl
{
    public bool Success { get; set; } = true;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        float d = Math.Min(Width, Height) - 1;
        var c = Success ? Theme.Good : Theme.Danger;
        using (var halo = new SolidBrush(Color.FromArgb(40, c))) g.FillEllipse(halo, 0, 0, d, d);
        float inset = d * 0.14f;
        using (var b = new SolidBrush(c)) g.FillEllipse(b, inset, inset, d - 2 * inset, d - 2 * inset);
        using var pen = new Pen(Color.White, d * 0.07f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float cx = d / 2, cy = d / 2, s = d * 0.18f;
        if (Success) g.DrawLines(pen, new[] { new PointF(cx - s, cy), new PointF(cx - s * 0.3f, cy + s * 0.7f), new PointF(cx + s * 1.05f, cy - s * 0.65f) });
        else { g.DrawLine(pen, cx - s * 0.8f, cy - s * 0.8f, cx + s * 0.8f, cy + s * 0.8f); g.DrawLine(pen, cx + s * 0.8f, cy - s * 0.8f, cx - s * 0.8f, cy + s * 0.8f); }
    }
}

/// <summary>Panel / label that let clicks fall through to the window, so it can be dragged from anywhere.</summary>
sealed class PassThroughPanel : Panel
{
    public PassThroughPanel() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = -1; return; } // WM_NCHITTEST → HTTRANSPARENT
        base.WndProc(ref m);
    }
}

sealed class PassLabel : Label
{
    public PassLabel() { UseMnemonic = false; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x84) { m.Result = -1; return; }
        base.WndProc(ref m);
    }
}

/// <summary>Row of mutually exclusive choices in one rounded track (quality: Best / Balanced / Small).</summary>
sealed class Segmented : DrawnControl
{
    readonly string[] _items;
    int _selected, _hot = -1;
    public event EventHandler? SelectedChanged;

    public Segmented(params string[] items)
    {
        _items = items;
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 9.5f);
    }

    public int SelectedIndex
    {
        get => _selected;
        set { value = Math.Clamp(value, 0, _items.Length - 1); if (value == _selected) return; _selected = value; Invalidate(); SelectedChanged?.Invoke(this, EventArgs.Empty); }
    }

    int IndexAt(int x) => Math.Clamp(x * _items.Length / Math.Max(1, Width), 0, _items.Length - 1);

    protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.X); if (i != _hot) { _hot = i; Invalidate(); } base.OnMouseMove(e); }
    protected override void OnMouseLeave(EventArgs e) { _hot = -1; base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (Enabled && e.Button == MouseButtons.Left) { SelectedIndex = IndexAt(e.X); Focus(); } base.OnMouseDown(e); }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Left) { SelectedIndex--; e.Handled = true; }
        if (e.KeyCode == Keys.Right) { SelectedIndex++; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        PaintBackground(g);
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.Round(r, r.Height / 2))
        {
            using (var b = new SolidBrush(Theme.Field)) g.FillPath(b, path);
            using var p = new Pen(Focused && ShowFocusCues ? Theme.AccentHover : Theme.Border, 1f);
            g.DrawPath(p, path);
        }
        float w = r.Width / _items.Length;
        for (int i = 0; i < _items.Length; i++)
        {
            var seg = new RectangleF(r.X + i * w + Px(3), r.Y + Px(3), w - Px(6), r.Height - Px(6));
            bool sel = i == _selected;
            Color fill = sel ? (Enabled ? Theme.Accent : Theme.Blend(Theme.Accent, Theme.Field, 0.6f)) : i == _hot && Enabled ? Theme.BtnHover : Theme.Field;
            if (sel || (i == _hot && Enabled))
                using (var sp = Theme.Round(seg, seg.Height / 2))
                using (var sb = new SolidBrush(fill)) g.FillPath(sb, sp);
            var text = !Enabled ? Theme.Faint : sel ? Color.White : Theme.Dim;
            TextRenderer.DrawText(g, _items[i], Font, Rectangle.Round(seg), text, fill,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
