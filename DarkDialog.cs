using System.Runtime.InteropServices;

namespace VideoTrimmer;

/// <summary>
/// Base for the app's modal windows: dark background, title bar painted the same colour (Windows 11), content laid
/// out top-to-bottom with helpers for headings, text and a right-aligned button row.
/// </summary>
class DarkDialog : Form
{
    protected static readonly Color Bg = Color.FromArgb(28, 30, 36), Fg = Color.FromArgb(230, 232, 236),
        Dim = Color.FromArgb(150, 156, 168), Accent = Color.FromArgb(70, 140, 255), Btn = Color.FromArgb(52, 56, 66),
        Good = Color.FromArgb(110, 200, 130);

    protected readonly FlowLayoutPanel Content;
    protected readonly int ContentWidth;
    protected readonly Font H1 = new("Segoe UI Semibold", 14f), H2 = new("Segoe UI Semibold", 10.5f);

    protected DarkDialog(string title, int logicalWidth = 540)
    {
        Text = title;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(LogicalToDeviceUnits(24), LogicalToDeviceUnits(18), LogicalToDeviceUnits(24), LogicalToDeviceUnits(18));
        ContentWidth = LogicalToDeviceUnits(logicalWidth);
        Content = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        };
        Controls.Add(Content);
    }

    protected Label AddText(string text, Font? font = null, Color? color = null, int top = 0)
    {
        var l = new Label
        {
            Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(ContentWidth, 0), Font = font ?? Font,
            ForeColor = color ?? Fg, Margin = new Padding(0, LogicalToDeviceUnits(top), 0, 0),
        };
        Content.Controls.Add(l);
        return l;
    }

    protected Label AddHeading(string text, int top = 16) => AddText(text, H2, Accent, top);

    /// <summary>Adds a right-aligned button row. Buttons are given left-to-right; the last one is the Close/Cancel button.</summary>
    protected void AddButtons(params (string Text, bool Primary, Action OnClick)[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(ContentWidth, 0), Margin = new Padding(0, LogicalToDeviceUnits(22), 0, 0),
        };
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            var (text, primary, onClick) = buttons[i];
            var b = new Button
            {
                Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White,
                BackColor = primary ? Accent : Btn,
                Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(3), LogicalToDeviceUnits(10), LogicalToDeviceUnits(3)),
                Margin = new Padding(LogicalToDeviceUnits(8), 0, 0, 0),
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => onClick();
            if (i == buttons.Length - 1) CancelButton = b;
            row.Controls.Add(b);
        }
        Content.Controls.Add(row);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar, then paint caption + border in the exact background colour (Windows 11; ignored on 10).
        int dark = 1, caption = ColorRef(Bg), text = ColorRef(Fg);
        try
        {
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));     // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));  // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(Handle, 34, ref caption, sizeof(int));  // DWMWA_BORDER_COLOR
            DwmSetWindowAttribute(Handle, 36, ref text, sizeof(int));     // DWMWA_TEXT_COLOR
        }
        catch { }
    }

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
