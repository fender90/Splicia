using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace Splicia;

/// <summary>
/// Install / update / uninstall wizard in the app's design language: borderless dark window with rounded corners,
/// the app artwork as a side panel, option cards, pill buttons, toggle switches and a thin progress bar.
/// Flow: installation type (me only / all users) → destination & shortcuts → progress → finished.
/// </summary>
sealed class SetupForm : PagedWindow
{
    const int WinW = 820, WinH = 520, HeroW = 300, Gutter = 44, PageTop = 64, PageBottom = 32;

    readonly Image? _hero;
    readonly Font _heroTitle = new("Segoe UI Semibold", 22f), _heroSub = new("Segoe UI", 10f), _heroBy = new("Segoe UI Semibold", 10.5f);
    readonly InstallScope? _uninstallScope;

    InstallScope _scope;
    PathBox? _path;
    ToggleSwitch? _startMenu, _desktop, _launch;
    PassLabel? _step, _percent;
    ThinProgress? _bar;

    /// <param name="uninstall">Start on the uninstall flow (Settings → Apps).</param>
    /// <param name="uninstallScope">Which installation Settings asked to remove; null = whatever is installed.</param>
    public SetupForm(bool uninstall, InstallScope? uninstallScope = null)
        : base(uninstall ? "Uninstall Splicia" : "Splicia Setup", new Size(WinW, WinH),
            new Rectangle(HeroW + Gutter, PageTop, WinW - HeroW - 2 * Gutter, WinH - PageTop - PageBottom), minimizeBox: true)
    {
        StartPosition = FormStartPosition.CenterScreen;
        _uninstallScope = uninstallScope;

        using (var s = typeof(SetupForm).Assembly.GetManifestResourceStream("Splicia.hero.jpg"))
            if (s != null) _hero = Image.FromStream(s);

        // Default to the scope that's already installed (so an update stays where it is), else "me only".
        _scope = Installer.InstalledDirectory(InstallScope.CurrentUser) == null && Installer.InstalledDirectory(InstallScope.AllUsers) != null
            ? InstallScope.AllUsers : InstallScope.CurrentUser;

        // Publisher's website under "by Vesperis Labs" in the art panel (a real link: the rest of the window drags it)
        var site = new LinkLabel
        {
            Text = AppInfo.WebsiteLabel, Font = _heroSub, AutoSize = true, BackColor = Theme.Bg,
            LinkColor = Theme.AccentHover, ActiveLinkColor = Color.White, VisitedLinkColor = Theme.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline, Location = new Point(P(31), ClientSize.Height - P(72)),
        };
        site.LinkClicked += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(AppInfo.Website) { UseShellExecute = true }); }
            catch (Exception ex) { CrashLog.Write(ex, "Opening the website"); }
        };
        Controls.Add(site);

        if (uninstall) ShowUninstallConfirm(fromSetup: false); else ShowScope();
    }

    static string ScopeName(InstallScope s) => s == InstallScope.AllUsers ? "all users" : "you only";

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        var hero = new Rectangle(0, 0, P(HeroW), ClientSize.Height);
        // Portrait artwork at the panel's width, top-aligned; it fades out into the background at its foot and along the
        // edge facing the page, leaving the bottom for the title.
        int artH = _hero == null ? 0 : (int)Math.Round(hero.Width * _hero.Height / (double)_hero.Width);
        var artRect = new Rectangle(0, 0, hero.Width, artH);
        if (_hero != null)
        {
            var st = g.Save();
            g.SetClip(hero);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_hero, artRect);
            g.Restore(st);
        }
        // Brush rects are 1 px larger than the fill so GDI+ never wraps the gradient (which leaves a seam line).
        var fade = new Rectangle(0, artRect.Bottom - P(56), hero.Width, P(56) + 1);
        using (var lg = new LinearGradientBrush(Rectangle.Inflate(fade, 0, 1), Color.FromArgb(0, Theme.Bg), Theme.Bg, 90f))
            g.FillRectangle(lg, fade);
        using (var below = new SolidBrush(Theme.Bg)) g.FillRectangle(below, 0, fade.Bottom, hero.Width, hero.Height - fade.Bottom);
        var edge = new Rectangle(hero.Right - P(28), 0, P(28) + 1, hero.Height);
        using (var lg = new LinearGradientBrush(Rectangle.Inflate(edge, 1, 0), Color.FromArgb(0, Theme.Bg), Theme.Bg, 0f))
            g.FillRectangle(lg, edge);

        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var white = new SolidBrush(Color.White);
        using var dim = new SolidBrush(Theme.Dim);
        using var fg = new SolidBrush(Theme.Fg);
        g.DrawString(AppInfo.Name, _heroTitle, white, P(30), hero.Height - P(134));
        g.DrawString($"by {AppInfo.Publisher}", _heroBy, fg, P(32), hero.Height - P(92));
        // the website link (a LinkLabel) sits at hero.Height - 72
        g.DrawString($"Version {Installer.Version}", _heroSub, dim, P(32), hero.Height - P(42));
    }

    // ───────────────────────── 1. installation type ─────────────────────────

    void ShowScope()
    {
        NewPage();
        var installed = Installer.InstalledScopes().ToList();
        AddText("Choose installation type", H1, Theme.Fg);
        AddText(installed.Count == 0
                ? Installer.LegacyScopes().Any()
                    ? "Video Trimmer is now Splicia. Install it just for your account, or for everyone who uses this PC."
                    : "Install Splicia just for your account, or for everyone who uses this PC."
                : $"Splicia {Installer.InstalledVersion(installed[0])} is installed for {ScopeName(installed[0])}." +
                  (installed.Count > 1 ? " It's also installed for you only." : ""),
            Body, Theme.Dim, 6);

        var me = AddControl(new OptionCard("Install for me only",
            "Installs in your user profile. No administrator permission needed."), P(84), 22);
        var all = AddControl(new OptionCard("Install for all users",
            "Installs in Program Files for everyone on this PC. Requires administrator permission."), P(84), 12);
        me.Selected = _scope == InstallScope.CurrentUser;
        all.Selected = _scope == InstallScope.AllUsers;
        me.SelectedChanged += (_, _) => { if (me.Selected) { all.Selected = false; _scope = InstallScope.CurrentUser; } };
        all.SelectedChanged += (_, _) => { if (all.Selected) { me.Selected = false; _scope = InstallScope.AllUsers; } };

        AddFootnote($"{AppInfo.Name} is free under the {AppInfo.License} license.\n{AppInfo.Copyright}  ·  {AppInfo.WebsiteLabel}");

        var left = new List<PillButton> { Button("Cancel", PillButton.Kind.Ghost, TryClose) };
        if (installed.Count > 0) left.Add(Button("Uninstall", PillButton.Kind.Ghost, () => ShowUninstallConfirm(fromSetup: true)));
        Footer(left.ToArray(), Button("Next", PillButton.Kind.Primary, ShowDestination));
    }

    // ───────────────────────── 2. destination & shortcuts ─────────────────────────

    static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb) ? va.CompareTo(vb) : string.CompareOrdinal(a, b);

    InstallScope OtherScope => _scope == InstallScope.AllUsers ? InstallScope.CurrentUser : InstallScope.AllUsers;

    void ShowDestination()
    {
        NewPage();
        var scope = _scope;
        string? installed = Installer.InstalledVersion(scope);
        string? installedDir = Installer.InstalledDirectory(scope);
        int cmp = installed == null ? 1 : CompareVersions(Installer.Version, installed);
        string verb = installed == null ? "Install" : cmp > 0 ? "Update" : cmp == 0 ? "Reinstall" : "Install";
        bool needsAdmin = scope == InstallScope.AllUsers && !Installer.IsElevated;

        AddText($"{verb} Splicia", H1, Theme.Fg);
        string who = scope == InstallScope.AllUsers ? "For everyone on this PC." : "For your account only.";
        AddText(installed == null
                ? $"{who} Choose where to put it and which shortcuts you'd like."
                : cmp > 0 ? $"{who} Version {installed} is installed; this updates it to {Installer.Version}."
                : cmp == 0 ? $"{who} Version {installed} is already installed here; you can reinstall it."
                : $"{who} A newer version ({installed}) is installed; continuing replaces it with {Installer.Version}.",
            Body, Theme.Dim, 6);

        AddText("INSTALL LOCATION", Section, Theme.Faint, 22);
        Y += P(8);
        var browse = new PillButton("Browse…");
        int bw = browse.GetPreferredSize(Size.Empty).Width, rowH = P(40);
        _path = new PathBox { Bounds = new Rectangle(0, Y, Page.Width - bw - P(10), rowH) };
        _path.Text = installedDir ?? Installer.DefaultDirectory(scope);
        _path.TextChanged += (_, _) => { if (InlineError != null) InlineError.Visible = false; };
        browse.Bounds = new Rectangle(Page.Width - bw, Y + (rowH - P(38)) / 2, bw, P(38));
        browse.Click += (_, _) => BrowseForFolder();
        Page.Controls.Add(_path);
        Page.Controls.Add(browse);
        Y += rowH;
        InlineError = AddText("", Small, Theme.Danger, 6, fixedHeight: P(34));
        InlineError.Visible = false;

        bool fresh = installed == null;
        _startMenu = AddControl(new ToggleSwitch("Start menu shortcut", fresh || Installer.HasStartMenuShortcut(scope),
            scope == InstallScope.AllUsers ? "In everyone's Start menu" : "Find Splicia from the Start menu"), P(46), 0);
        _desktop = AddControl(new ToggleSwitch("Desktop shortcut", Installer.HasDesktopShortcut(scope),
            scope == InstallScope.AllUsers ? "On everyone's desktop" : "Put an icon on your desktop"), P(46), 12);

        if (Installer.InstalledDirectory(OtherScope) != null)
            AddFootnote($"The copy installed for {ScopeName(OtherScope)} will be removed.");
        else if (Installer.LegacyScopes().Contains(scope))
            AddFootnote("Video Trimmer, Splicia's previous name, is installed: Splicia replaces it.");

        Footer(new[] { Button("Cancel", PillButton.Kind.Ghost, TryClose) },
            Button("Back", PillButton.Kind.Secondary, ShowScope),
            Button(verb, PillButton.Kind.Primary, () => StartInstall(verb), shield: needsAdmin));
    }

    void BrowseForFolder()
    {
        if (_path == null) return;
        string current = _path.Text.Trim();
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where to install Splicia",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(current) ? current : Path.GetDirectoryName(current) ?? "",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        string dir = dlg.SelectedPath.TrimEnd('\\');
        // Keep the app in its own folder unless the user already picked one named for it.
        string name = Path.GetFileName(dir);
        if (!name.Equals("Splicia", StringComparison.OrdinalIgnoreCase)) dir = Path.Combine(dir, "Splicia");
        _path.Text = dir;
    }

    void StartInstall(string verb)
    {
        if (_path == null || _startMenu == null || _desktop == null) return;
        var scope = _scope;
        string dir = _path.Text.Trim().Trim('"').TrimEnd('\\');
        if (dir.Length == 0) { ShowInlineError("Choose a folder to install into."); return; }
        if (Installer.CheckDirectory(dir, scope) is string problem) { ShowInlineError(problem); return; }
        if (Installer.IsRunningFrom(dir) || Installer.InstalledScopes().Any(s => Installer.IsRunningFrom(Installer.InstalledDirectory(s)!)))
        {
            ShowInlineError("Splicia is open. Close it, then try again.");
            return;
        }
        if (Installer.LegacyRunning())
        {
            ShowInlineError("Video Trimmer (Splicia's old name) is open. Close it, then try again; this replaces it.");
            return;
        }
        var options = new InstallOptions(scope, Path.GetFullPath(dir), _startMenu.Checked, _desktop.Checked);
        bool updating = verb == "Update";
        bool moveFromOther = Installer.InstalledDirectory(OtherScope) != null;
        var other = OtherScope;
        ShowProgress(updating ? "Updating Splicia" : "Installing Splicia");
        Run(report =>
        {
            // Scale the steps so moving away from the other scope gets the last part of the bar.
            Action<string, double> main = moveFromOther ? (s, f) => report(s, f * 0.85) : report;
            if (scope == InstallScope.AllUsers && !Installer.IsElevated) ElevatedHelper.InstallAllUsers(options, main);
            else Installer.Install(options, main);

            if (moveFromOther)
            {
                report($"Removing the copy installed for {ScopeName(other)}…", 0.88);
                if (other == InstallScope.CurrentUser) Installer.Uninstall(InstallScope.CurrentUser, forgetUser: false);
                else if (Installer.IsElevated) Installer.Uninstall(InstallScope.AllUsers, forgetUser: false);
                else ElevatedHelper.UninstallAllUsers(forgetUser: false, (s, f) => report(s, 0.88 + f * 0.1));
                report("Done", 1.0);
            }
        }, () => ShowFinished(updating), ShowDestination);
    }

    // ───────────────────────── 3. progress / result ─────────────────────────

    void ShowProgress(string title)
    {
        NewPage();
        AddText(title, H1, Theme.Fg);
        _step = AddText("Preparing…", Body, Theme.Dim, 10);
        _bar = AddControl(new ThinProgress(), P(16), 30);
        _percent = AddText("0%", Small, Theme.Faint, 8);
    }

    void Report(string step, double fraction)
    {
        if (_step == null || _bar == null || _percent == null || _step.IsDisposed) return;
        _step.Text = step;
        _bar.Value = fraction;
        _percent.Text = $"{fraction * 100:0}%";
    }

    /// <summary>Runs install/uninstall work on a background STA thread (shortcut creation uses COM), reporting progress.</summary>
    void Run(Action<Action<string, double>> work, Action onSuccess, Action onRetry)
    {
        Working = true;
        CloseButton.Enabled = false;
        var thread = new Thread(() =>
        {
            Exception? error = null;
            var sw = Stopwatch.StartNew();
            try { work((s, f) => BeginInvoke(() => Report(s, f))); }
            catch (Exception ex) { error = ex; }
            // Keep the progress page up long enough to read, and let the bar finish easing to 100%.
            if (error == null) Thread.Sleep((int)Math.Max(450, 1100 - sw.ElapsedMilliseconds));
            BeginInvoke(() =>
            {
                Working = false;
                CloseButton.Enabled = true;
                if (error == null) onSuccess();
                else if (error is OperationCanceledException) ShowFailed("Administrator permission needed", error.Message, onRetry, logged: false);
                else
                {
                    CrashLog.Write(error, "Setup");
                    ShowFailed("Something went wrong", error.Message, onRetry, logged: true);
                }
            });
        }) { IsBackground = true, Name = "Setup worker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    void ShowFinished(bool updated)
    {
        NewPage();
        AddControl(new ResultBadge { Success = true }, P(68), 0, width: P(68));
        AddText(updated ? "Splicia is up to date" : "Splicia is ready", H1, Theme.Fg, 18);
        AddText(_scope == InstallScope.AllUsers
                ? "Everyone on this PC can open it from the Start menu, or right-click a video and choose Open with → Splicia."
                : "Open it from the Start menu, or right-click any video and choose Open with → Splicia.",
            Body, Theme.Dim, 6);

        _launch = AddControl(new ToggleSwitch("Launch Splicia", true), P(28), 26);
        Footer(null, Button("Finish", PillButton.Kind.Primary, () =>
        {
            if (_launch.Checked) LaunchApp();
            Close();
        }));
    }

    /// <summary>Starts the installed app as the signed-in user (never elevated, even if setup itself was run as admin).</summary>
    static void LaunchApp()
    {
        string exe = Installer.InstalledExe;
        try
        {
            if (Installer.IsElevated) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch (Exception ex) { CrashLog.Write(ex, "Launching after setup"); }
    }

    void ShowFailed(string title, string message, Action retry, bool logged)
    {
        NewPage();
        AddControl(new ResultBadge { Success = false }, P(68), 0, width: P(68));
        AddText(title, H1, Theme.Fg, 18);
        AddText(message, Body, Theme.Dim, 6);
        if (logged) AddText($"Details were saved to {CrashLog.FilePath}", Small, Theme.Faint, 12);
        Footer(null, Button("Close", PillButton.Kind.Secondary, TryClose), Button("Try again", PillButton.Kind.Primary, retry));
    }

    // ───────────────────────── uninstall ─────────────────────────

    /// <summary>Installations to remove: the one Settings asked for, else every installed one.</summary>
    List<InstallScope> UninstallTargets() =>
        _uninstallScope is InstallScope s ? Installer.InstalledScopes().Where(x => x == s).ToList() : Installer.InstalledScopes().ToList();

    void ShowUninstallConfirm(bool fromSetup)
    {
        NewPage();
        var targets = UninstallTargets();
        if (targets.Count == 0)
        {
            AddText("Splicia isn't installed", H1, Theme.Fg);
            AddText("There's nothing to remove.", Body, Theme.Dim, 6);
            Footer(null, Button("Close", PillButton.Kind.Primary, TryClose));
            return;
        }
        AddText("Uninstall Splicia", H1, Theme.Fg);
        AddText("This removes Splicia, its shortcuts and its Open with entries for video files. " +
                "Your videos, including ones you exported, are not touched.", Body, Theme.Dim, 6);
        foreach (var s in targets)
        {
            AddText(s == InstallScope.AllUsers ? "INSTALLED FOR ALL USERS" : "INSTALLED FOR YOU ONLY", Section, Theme.Faint, 20);
            AddText(Installer.InstalledDirectory(s)!, Body, Theme.Fg, 6);
        }
        InlineError = AddText("", Small, Theme.Danger, 10, fixedHeight: P(34));
        InlineError.Visible = false;
        bool needsAdmin = targets.Contains(InstallScope.AllUsers) && !Installer.IsElevated;
        if (needsAdmin) AddFootnote("Removing the all-users copy needs administrator permission.");

        var uninstall = Button("Uninstall", PillButton.Kind.Danger, () => StartUninstall(fromSetup), shield: needsAdmin);
        var cancel = new[] { Button("Cancel", PillButton.Kind.Ghost, TryClose) };
        if (fromSetup) Footer(cancel, Button("Back", PillButton.Kind.Secondary, ShowScope), uninstall);
        else Footer(cancel, uninstall);
    }

    void StartUninstall(bool fromSetup)
    {
        var targets = UninstallTargets();
        if (targets.Any(s => Installer.IsRunningFrom(Installer.InstalledDirectory(s)!)))
        {
            ShowInlineError("Splicia is open. Close it, then click Uninstall again.");
            return;
        }
        ShowProgress("Removing Splicia");
        Run(report =>
        {
            for (int i = 0; i < targets.Count; i++)
            {
                double from = (double)i / targets.Count, span = 1.0 / targets.Count;
                Action<string, double> part = (s, f) => report(s, from + f * span);
                if (targets[i] == InstallScope.CurrentUser) Installer.Uninstall(InstallScope.CurrentUser, forgetUser: true, part);
                else
                {
                    if (Installer.IsElevated) Installer.Uninstall(InstallScope.AllUsers, forgetUser: true, part);
                    else ElevatedHelper.UninstallAllUsers(forgetUser: true, part);
                    Installer.ForgetCurrentUserRecords(); // the elevated helper may have run as another account
                }
            }
        }, ShowUninstalled, () => ShowUninstallConfirm(fromSetup));
    }

    void ShowUninstalled()
    {
        NewPage();
        AddControl(new ResultBadge { Success = true }, P(68), 0, width: P(68));
        AddText("Splicia has been removed", H1, Theme.Fg, 18);
        AddText("Its shortcuts and Open with entries are gone. Your videos were left untouched.", Body, Theme.Dim, 6);
        Footer(null, Button("Close", PillButton.Kind.Primary, TryClose));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hero?.Dispose();
        base.Dispose(disposing);
    }
}
