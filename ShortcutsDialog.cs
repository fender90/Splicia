namespace VideoTrimmer;

/// <summary>Keyboard and mouse shortcuts, grouped, in the same style as the other dialogs.</summary>
sealed class ShortcutsDialog : DarkDialog
{
    public ShortcutsDialog() : base("Video Trimmer — shortcuts", 460)
    {
        AddText("Keyboard & mouse shortcuts", H1);

        Group("Playback",
            ("Space", "Play / pause"),
            ("← / →, mouse wheel", "Previous / next frame"),
            ("Shift + ← / →", "Back / forward 1 s"),
            ("Ctrl + ← / →, PgUp / PgDn", "Back / forward 10 s"),
            ("Home / End", "Start / end of the selection"),
            ("↑ / ↓", "Volume"),
            ("M", "Mute"));

        Group("Trim & rotate",
            ("I  or  [", "Start the selection at this frame"),
            ("O  or  ]", "End the selection at this frame"),
            ("Drag the yellow handles", "Adjust the start and end"),
            ("R / Shift + R", "Rotate right / left"),
            ("Ctrl + E", "Export"));

        Group("View & file",
            ("Ctrl + O", "Open a video"),
            ("Ctrl + wheel, pinch", "Zoom at the cursor"),
            ("Ctrl + 0", "Reset zoom"),
            ("F, F11, double-click", "Fullscreen"),
            ("Esc", "Leave fullscreen"));

        AddButtons(("Close", true, Close));
    }

    void Group(string title, params (string Keys, string Action)[] rows)
    {
        AddHeading(title);
        var table = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, LogicalToDeviceUnits(4), 0, 0), MinimumSize = new Size(ContentWidth, 0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(210)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (keys, action) in rows)
        {
            var pad = new Padding(0, LogicalToDeviceUnits(2), 0, LogicalToDeviceUnits(2));
            table.Controls.Add(new Label { Text = keys, AutoSize = true, UseMnemonic = false, Font = H2, ForeColor = Fg, Margin = pad });
            table.Controls.Add(new Label { Text = action, AutoSize = true, UseMnemonic = false, ForeColor = Dim, Margin = pad });
        }
        Content.Controls.Add(table);
    }
}
