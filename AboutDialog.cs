using System.Diagnostics;

namespace Splicia;

/// <summary>About window in the app's borderless style: icon, version, publisher with website, and the license.</summary>
sealed class AboutDialog : PagedWindow
{
    const int WinW = 500, WinH = 512, Gutter = 40, PageTop = 44, PageBottom = 28;

    readonly Font _by = new("Segoe UI Semibold", 10.5f);

    public AboutDialog()
        : base($"About {AppInfo.Name}", new Size(WinW, WinH), new Rectangle(Gutter, PageTop, WinW - 2 * Gutter, WinH - PageTop - PageBottom), minimizeBox: false)
    {
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        // Icon beside the name and version
        int icon = P(64);
        using (var s = typeof(AboutDialog).Assembly.GetManifestResourceStream("Splicia.app.ico"))
            if (s != null)
            {
                using var ico = new Icon(s, icon, icon);
                Page.Controls.Add(new PictureBox { Image = ico.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(0, 0, icon, icon), BackColor = Theme.Bg });
            }
        int tx = icon + P(16);
        Page.Controls.Add(new PassLabel { Text = AppInfo.Name, Font = H1, ForeColor = Theme.Fg, BackColor = Theme.Bg, AutoSize = true, Location = new Point(tx - P(3), P(2)) });
        Page.Controls.Add(new PassLabel { Text = $"Version {AppInfo.Version}", Font = Body, ForeColor = Theme.Dim, BackColor = Theme.Bg, AutoSize = true, Location = new Point(tx, P(40)) });
        Y = icon;

        AddText(AppInfo.Tagline + ", with frame-exact cuts.", Body, Theme.Dim, 18);

        AddText($"Published by {AppInfo.Publisher}", _by, Theme.Fg, 22);
        AddLink(AppInfo.WebsiteLabel, AppInfo.Website, 2);

        AddText("LICENSE", Section, Theme.Faint, 24);
        AddText($"{AppInfo.Name} {AppInfo.Copyright} is licensed under {AppInfo.License} ({AppInfo.LicenseName}): " +
                $"free to use, share and adapt, with credit to {AppInfo.Publisher}. The {AppInfo.Name} name and logo are not covered.", Body, Theme.Fg, 6);
        AddLink("creativecommons.org/licenses/by/4.0", AppInfo.LicenseUrl, 2);
        AddText("It uses FFmpeg, which is licensed under the GNU GPL version 3; see the third-party notices.", Small, Theme.Faint, 12);

        Footer(new[]
            {
                Button("License", PillButton.Kind.Ghost, () => OpenFile(AppInfo.LicenseFile, AppInfo.LicenseUrl)),
                Button("Third-party notices", PillButton.Kind.Ghost, () => OpenFile(AppInfo.NoticesFile, AppInfo.Website)),
            },
            Button("Close", PillButton.Kind.Primary, Close));
    }

    void AddLink(string text, string url, int gapBefore)
    {
        Y += P(gapBefore);
        var link = new LinkLabel
        {
            Text = text, Font = Body, AutoSize = true, BackColor = Theme.Bg, Location = new Point(-P(3), Y),
            LinkColor = Theme.AccentHover, ActiveLinkColor = Color.White, VisitedLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        link.LinkClicked += (_, _) => Open(url);
        Page.Controls.Add(link);
        Y += link.PreferredHeight;
    }

    /// <summary>Opens a text file shipped next to the exe, or the web page when it's missing (e.g. a copied exe).</summary>
    static void OpenFile(string name, string fallbackUrl)
    {
        string path = Path.Combine(FFmpegRuntime.AppDirectory, name);
        Open(File.Exists(path) ? path : fallbackUrl);
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { CrashLog.Write(ex, $"Opening {target}"); }
    }
}
