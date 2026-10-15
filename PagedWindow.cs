using System.Runtime.InteropServices;

namespace VideoTrimmer;

/// <summary>
/// Borderless dark window with rounded corners and a minimise/close pill, whose content is a page of stacked text,
/// controls and a bottom button row (the setup wizard and the export window). Drag it from anywhere that isn't a control.
/// </summary>
abstract class PagedWindow : Form
{
    protected readonly PassThroughPanel Page;
    protected readonly ChromeButton CloseButton;
    protected readonly ChromeButton? MinimizeButton;
    protected readonly Font H1 = new("Segoe UI Semibold", 20f), Body = new("Segoe UI", 10.5f), Small = new("Segoe UI", 9f),
        Section = new("Segoe UI Semibold", 8.5f);
    protected int Y;
    protected PassLabel? InlineError;

    /// <summary>While true the window can't be closed (work in progress).</summary>
    protected bool Working { get; set; }

    /// <param name="pageBounds">The page area, in logical (96 dpi) pixels.</param>
    protected PagedWindow(string title, Size clientSize, Rectangle pageBounds, bool minimizeBox)
    {
        Text = title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        DoubleBuffered = true;
        KeyPreview = true;
        ClientSize = new Size(P(clientSize.Width), P(clientSize.Height));

        Page = new PassThroughPanel
        {
            BackColor = Theme.Bg,
            Bounds = new Rectangle(P(pageBounds.X), P(pageBounds.Y), P(pageBounds.Width), P(pageBounds.Height)),
        };
        Controls.Add(Page);

        int bw = P(44), bh = P(32), top = P(12);
        CloseButton = new ChromeButton(ChromeButton.Kind.Close) { Bounds = new Rectangle(ClientSize.Width - P(12) - bw, top, bw, bh) };
        CloseButton.Click += (_, _) => TryClose();
        Controls.Add(CloseButton);
        if (minimizeBox)
        {
            MinimizeButton = new ChromeButton(ChromeButton.Kind.Minimize) { Bounds = new Rectangle(CloseButton.Left - bw - P(2), top, bw, bh) };
            MinimizeButton.Click += (_, _) => WindowState = FormWindowState.Minimized;
            Controls.Add(MinimizeButton);
        }
    }

    protected int P(float v) => (int)Math.Round(v * DeviceDpi / 96f);

    // ───────────────────────── window chrome ─────────────────────────

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000;        // WS_MINIMIZEBOX: minimise from the taskbar
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, dark = 1;
        try
        {
            DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // DWMWCP_ROUND (Windows 11)
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));  // dark mode
        }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, WM_NCLBUTTONDBLCLK = 0xA3, HTCLIENT = 1, HTCAPTION = 2;
        if (m.Msg == WM_NCLBUTTONDBLCLK) return; // no maximise on double-click
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == HTCLIENT) m.Result = HTCAPTION; // drag from anywhere that isn't a control
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { TryClose(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected virtual void TryClose()
    {
        if (!Working) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (Working) e.Cancel = true; // never interrupt half-done work
        base.OnFormClosing(e);
    }

    // ───────────────────────── page building ─────────────────────────

    protected void NewPage()
    {
        foreach (var c in Page.Controls.Cast<Control>().ToArray()) c.Dispose();
        Page.Controls.Clear();
        Y = 0;
        AcceptButton = null;
        InlineError = null;
    }

    protected PassLabel AddText(string text, Font font, Color color, int gapBefore = 0, int? fixedHeight = null)
    {
        Y += P(gapBefore);
        var size = TextRenderer.MeasureText(text.Length == 0 ? " " : text, font, new Size(Page.Width, int.MaxValue), TextFormatFlags.WordBreak);
        var l = new PassLabel
        {
            Text = text, Font = font, ForeColor = color, BackColor = Theme.Bg, AutoSize = false,
            Bounds = new Rectangle(0, Y, Page.Width, fixedHeight ?? size.Height),
        };
        Page.Controls.Add(l);
        Y += l.Height;
        return l;
    }

    protected T AddControl<T>(T c, int height, int gapBefore = 0, int? width = null) where T : Control
    {
        Y += P(gapBefore);
        c.Bounds = new Rectangle(0, Y, width ?? Page.Width, height);
        Page.Controls.Add(c);
        Y += height;
        return c;
    }

    protected void AddFootnote(string text)
    {
        Page.Controls.Add(new PassLabel
        {
            Text = text, Font = Small, ForeColor = Theme.Faint, BackColor = Theme.Bg, AutoSize = false,
            // room for two lines, bottom-aligned just above the buttons
            Bounds = new Rectangle(0, Page.Height - P(38) - P(54), Page.Width, P(40)),
            TextAlign = ContentAlignment.BottomLeft,
        });
    }

    /// <summary>
    /// Bottom button row. <paramref name="left"/> buttons sit at the left edge (Cancel, Uninstall); <paramref name="right"/>
    /// ones are right-aligned together (Back next to Next/Install). Both are given left to right; the last right one is default.
    /// </summary>
    protected void Footer(PillButton[]? left, params PillButton[] right)
    {
        int y = Page.Height - P(38), x = Page.Width;
        foreach (var b in right.Reverse())
        {
            var s = b.GetPreferredSize(Size.Empty);
            x -= s.Width;
            b.Bounds = new Rectangle(x, y, s.Width, s.Height);
            Page.Controls.Add(b);
            x -= P(10);
        }
        int lx = 0;
        foreach (var b in left ?? Array.Empty<PillButton>())
        {
            var s = b.GetPreferredSize(Size.Empty);
            b.Bounds = new Rectangle(lx, y, s.Width, s.Height);
            Page.Controls.Add(b);
            lx += s.Width + P(4);
        }
        if (right.Length > 0) { AcceptButton = right[^1]; right[^1].Focus(); }
    }

    protected static PillButton Button(string text, PillButton.Kind kind, Action onClick, bool shield = false)
    {
        var b = new PillButton(text, kind) { Shield = shield };
        b.Click += (_, _) => onClick();
        return b;
    }

    protected void ShowInlineError(string message)
    {
        if (InlineError == null) return;
        InlineError.Text = message;
        InlineError.Visible = true;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
