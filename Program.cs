namespace VideoTrimmer;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        CrashLog.Install(); // must run before any window is created
        ApplicationConfiguration.Initialize();

        bool Has(string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        // Elevated helper started by the setup wizard for all-users installs (no UI).
        if (Has("--elevated-helper")) return ElevatedHelper.HelperMain(args);

        bool quiet = Has("--quiet");
        var scope = Has("--allusers") ? InstallScope.AllUsers : InstallScope.CurrentUser;

        // Quiet switches (scripts / Settings → Apps "quiet uninstall")
        if (Has("--install") && quiet) return Installer.InstallQuiet(scope);
        if (Has("--uninstall") && quiet) return Installer.UninstallQuiet(scope);

        // Uninstall from Settings → Apps, or the setup wizard (the setup exe *is* the app: VideoTrimmer-Setup*.exe)
        if (Has("--uninstall"))
        {
            Application.Run(new SetupForm(uninstall: true, uninstallScope: scope));
            return 0;
        }
        string exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        if (Has("--install") || Has("--setup") || exeName.Contains("setup", StringComparison.OrdinalIgnoreCase))
        {
            Application.Run(new SetupForm(uninstall: false));
            return 0;
        }

        Application.Run(new TrimmerForm(args.FirstOrDefault(a => !a.StartsWith("--"))));
        return 0;
    }
}
