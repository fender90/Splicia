using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace VideoTrimmer;

/// <summary>
/// All-users installs need administrator rights, but the wizard itself stays unelevated (so it can launch the app as
/// the user). It starts this same exe elevated with
/// --elevated-helper; the helper does the work and streams progress back over a private named pipe.
/// </summary>
static class ElevatedHelper
{
    const int ErrorCancelled = 1223; // the user said No to the UAC prompt

    // ───────────────────────── wizard side ─────────────────────────

    public static void InstallAllUsers(InstallOptions o, Action<string, double> progress) => Run(progress,
        "--do-install", "--dir", o.Directory, "--startmenu", o.StartMenuShortcut ? "1" : "0", "--desktop", o.DesktopShortcut ? "1" : "0");

    public static void UninstallAllUsers(bool forgetUser, Action<string, double> progress) =>
        Run(progress, forgetUser ? new[] { "--do-uninstall" } : new[] { "--do-uninstall", "--keep-user" });

    static void Run(Action<string, double> progress, params string[] args)
    {
        string pipe = "VideoTrimmerSetup-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = Join(new[] { "--elevated-helper" }.Concat(args).Concat(new[] { "--pipe", pipe })),
        };
        progress("Waiting for administrator permission…", 0.02);
        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the setup helper."); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            throw new OperationCanceledException("Administrator permission wasn't given, so nothing was changed.");
        }
        using (process)
        {
            var connect = server.WaitForConnectionAsync();
            while (!connect.Wait(200))
                if (process.HasExited) throw new InvalidOperationException($"The setup helper stopped unexpectedly (code {process.ExitCode}).");

            string? error = null;
            using (var reader = new StreamReader(server))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("P|"))
                    {
                        var parts = line.Split('|', 3);
                        if (parts.Length == 3 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                            progress(parts[2], f);
                    }
                    else if (line.StartsWith("E|")) error = line[2..];
                }
            }
            process.WaitForExit();
            if (error != null) throw new InvalidOperationException(error);
            if (process.ExitCode != 0) throw new InvalidOperationException($"The setup helper failed (code {process.ExitCode}).");
        }
    }

    // ───────────────────────── helper side (elevated) ─────────────────────────

    public static int HelperMain(string[] args)
    {
        string? Arg(string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        bool Has(string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        using var client = Arg("--pipe") is string pipe ? new NamedPipeClientStream(".", pipe, PipeDirection.Out) : null;
        StreamWriter? writer = null;
        try
        {
            if (client != null)
            {
                client.Connect(15000);
                writer = new StreamWriter(client) { AutoFlush = true };
            }
            void Report(string text, double fraction) =>
                writer?.WriteLine($"P|{fraction.ToString(CultureInfo.InvariantCulture)}|{text}");

            if (Has("--do-install"))
                Installer.Install(new InstallOptions(InstallScope.AllUsers, Arg("--dir") ?? Installer.DefaultDirectory(InstallScope.AllUsers),
                    Arg("--startmenu") == "1", Arg("--desktop") == "1"), Report);
            else if (Has("--do-uninstall"))
                Installer.Uninstall(InstallScope.AllUsers, forgetUser: !Has("--keep-user"), Report);
            return 0;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "Elevated setup helper");
            try { writer?.WriteLine("E|" + ex.Message.Replace('\r', ' ').Replace('\n', ' ')); } catch { }
            return 1;
        }
        finally
        {
            writer?.Dispose();
        }
    }

    // ───────────────────────── quiet command line ─────────────────────────

    /// <summary>Re-runs this exe elevated with the same arguments (quiet --allusers modes) and returns its exit code.</summary>
    public static int RelaunchElevatedAndWait()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = Join(Environment.GetCommandLineArgs().Skip(1)),
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return 1;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled) { return ErrorCancelled; }
    }

    static string Join(IEnumerable<string> args) =>
        string.Join(" ", args.Select(a => a.Length > 0 && !a.Any(c => c is ' ' or '"' or '\t') ? a : "\"" + a.Replace("\"", "\\\"") + "\""));
}
