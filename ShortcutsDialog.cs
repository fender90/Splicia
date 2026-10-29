namespace Splicia;

/// <summary>Keyboard and mouse shortcuts in two columns, in the app's borderless window style.</summary>
sealed class ShortcutsDialog : PagedWindow
{
    const int WinW = 860, WinH = 640, Gutter = 40, PageTop = 44, PageBottom = 28, Column = 380, KeyW = 200;

    static readonly (string Title, (string Keys, string Action)[] Rows)[] LeftColumn =
    {
        ("PLAYBACK", new[]
        {
            ("Space", "Play / pause"),
            ("← / →, mouse wheel", "Previous / next frame"),
            ("Shift + ← / →", "Back / forward 1 s"),
            ("Ctrl + ← / →, PgUp / PgDn", "Back / forward 10 s"),
            ("Home / End", "Start / end of the timeline"),
            ("↑ / ↓", "Volume"),
            ("M", "Mute"),
        }),
        ("VIEW & FILE", new[]
        {
            ("Ctrl + O", "Open a video"),
            ("Ctrl + E", "Export"),
            ("Ctrl + wheel, pinch", "Zoom at the cursor"),
            ("Ctrl + 0", "Reset zoom"),
            ("F, F11, double-click", "Fullscreen"),
            ("P", "Pin / unpin the control panel"),
            ("Esc", "Leave fullscreen"),
        }),
        ("ROTATE & CROP", new[]
        {
            ("R / Shift + R", "Rotate right / left"),
            ("C", "Crop (again to finish)"),
            ("Enter / Esc", "Keep / undo crop changes"),
        }),
    };

    static readonly (string Title, (string Keys, string Action)[] Rows)[] RightColumn =
    {
        ("EDIT", new[]
        {
            ("S", "Split the clip at the playhead"),
            ("Click a clip", "Select it"),
            ("Delete", "Delete it, leaving a gap"),
            ("Shift + Delete", "Delete it and close the gap"),
            ("Drag a clip", "Move it, or rearrange"),
            ("Drag a clip's top corner", "Fade in / out"),
            ("Click the dot between clips", "Transition"),
            ("Right-click a clip", "Fades, trim, delete"),
            ("Ctrl + Z / Ctrl + Y", "Undo / redo"),
        }),
        ("TRIM", new[]
        {
            ("T, double-click a clip", "Trim it"),
            ("Drag the yellow handles", "Set its start / end"),
            ("Drag the grip under them", "Slide the part kept"),
            ("I or [  ·  O or ]", "Start / end here"),
            ("Enter / Esc", "Keep / cancel"),
        }),
        ("SOUND", new[]
        {
            ("Drop WAV / MP3 files", "Add them to the audio track"),
            ("Drag a sound", "Move it along its track"),
            ("Drag a sound's ends", "Trim it"),
        }),
    };

    readonly Font _key = new("Segoe UI Semibold", 9.5f), _action = new("Segoe UI", 9.5f);

    public ShortcutsDialog()
        : base($"{AppInfo.Name} — shortcuts", new Size(WinW, WinH), new Rectangle(Gutter, PageTop, WinW - 2 * Gutter, WinH - PageTop - PageBottom), minimizeBox: false)
    {
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        AddText("Keyboard & mouse shortcuts", H1, Theme.Fg);
        int top = Y + P(14);
        PlaceGroups(LeftColumn, 0, top);
        PlaceGroups(RightColumn, P(Column + 20), top);
        Footer(null, Button("Close", PillButton.Kind.Primary, Close));
    }

    void PlaceGroups((string Title, (string Keys, string Action)[] Rows)[] groups, int x, int y)
    {
        foreach (var (title, rows) in groups)
        {
            Page.Controls.Add(new PassLabel { Text = title, Font = Section, ForeColor = Theme.Faint, BackColor = Theme.Bg, AutoSize = true, Location = new Point(x, y) });
            y += P(24);
            foreach (var (keys, action) in rows)
            {
                Page.Controls.Add(new PassLabel { Text = keys, Font = _key, ForeColor = Theme.Fg, BackColor = Theme.Bg, AutoSize = true, Location = new Point(x, y) });
                Page.Controls.Add(new PassLabel { Text = action, Font = _action, ForeColor = Theme.Dim, BackColor = Theme.Bg, AutoSize = true, Location = new Point(x + P(KeyW), y) });
                y += P(23);
            }
            y += P(16);
        }
    }
}
