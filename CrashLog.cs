using System.Diagnostics;
using System.Text;

namespace VideoTrimmer;

/// <summary>
/// Appends exception reports to CrashLogs\crash.log in VideoTrimmer.exe's folder (rolled to crash.old.log past 1 MB), so
/// everything the app writes lives in its own folder. For an all-users install (read-only Program Files folder) it uses
/// %ProgramData%\Video Trimmer\CrashLogs instead.
/// </summary>
static class CrashLog
{
    const long MaxBytes = 1 << 20;
    static readonly object Gate = new();

    // Resolved on first use (not at startup), and the CrashLogs folder itself is only created when an entry is written.
    static readonly Lazy<string> Dir = new(PickDirectory);
    public static string Directory => Dir.Value;
    public static string FilePath => Path.Combine(Directory, "crash.log");

    static string PickDirectory()
    {
        string exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        try
        {
            // Probe the exe's folder without creating CrashLogs yet.
            string probe = Path.Combine(exeDir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(exeDir, "CrashLogs");
        }
        catch
        {
            // Read-only install folder (an all-users install in Program Files): the shared, user-writable
            // ProgramData\Video Trimmer\CrashLogs the installer prepared, else %TEMP% as a last resort.
            return System.IO.Directory.Exists(Installer.MachineLogDirectory)
                ? Installer.MachineLogDirectory
                : Path.Combine(Path.GetTempPath(), "VideoTrimmer");
        }
    }

    /// <summary>Supplies a one-line description of the app state (file, position, selection...). Set by the main window.</summary>
    public static Func<string>? Context { get; set; }

    /// <summary>Installs handlers for UI-thread, background-thread and unobserved task exceptions.</summary>
    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportAndAsk(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
                  e.IsTerminating ? "Unhandled exception (fatal)" : "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception, "Unobserved task exception"); e.SetObserved(); };
    }

    /// <summary>Writes an entry; never throws.</summary>
    public static void Write(Exception ex, string kind)
    {
        try
        {
            string ctx;
            try { ctx = Context?.Invoke() ?? "(no context)"; }
            catch (Exception cex) { ctx = "(context unavailable: " + cex.Message + ")"; }

            var sb = new StringBuilder();
            sb.AppendLine(new string('=', 80));
            sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {kind}");
            sb.AppendLine($"Version : {Application.ProductVersion.Split('+')[0]}   PID {Environment.ProcessId}   {Environment.ProcessPath}");
            sb.AppendLine($"System  : {Environment.OSVersion}   .NET {Environment.Version}   {Environment.ProcessorCount} CPUs");
            sb.AppendLine($"State   : {ctx}");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();

            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(Directory);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(FilePath, Path.Combine(Directory, "crash.old.log"), overwrite: true);
                File.AppendAllText(FilePath, sb.ToString());
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    static void ReportAndAsk(Exception ex)
    {
        Write(ex, "UI thread exception");
        var answer = MessageBox.Show(
            $"Video Trimmer hit an unexpected error:\n\n{ex.Message}\n\nDetails were saved to:\n{FilePath}\n\n" +
            "Yes = keep running   No = quit   Cancel = open the log folder",
            "Video Trimmer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Error);
        if (answer == DialogResult.No) Environment.Exit(1);
        if (answer == DialogResult.Cancel) OpenFolder();
    }

    public static void OpenFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (File.Exists(FilePath)) Process.Start("explorer.exe", $"/select,\"{FilePath}\"");
            else Process.Start("explorer.exe", $"\"{Directory}\"");
        }
        catch { }
    }
}
