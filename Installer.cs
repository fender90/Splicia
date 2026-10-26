using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Splicia;

/// <summary>Who an installation is for: just the current account (HKCU, no admin) or everyone (HKLM, Program Files).</summary>
enum InstallScope { CurrentUser, AllUsers }

/// <summary>What the user chose in the setup wizard.</summary>
sealed record InstallOptions(InstallScope Scope, string Directory, bool StartMenuShortcut, bool DesktopShortcut);

/// <summary>
/// Install engine. The exe is self-contained (no .NET download needed) and copies itself into the chosen folder; the
/// FFmpeg components go into an ffmpeg\ folder beside it, unpacked from the payload build.ps1 appends to the setup exe
/// (or copied from the running copy's own ffmpeg\ folder). Per-user installs register under HKCU and need no admin;
/// all-users installs register under HKLM and must run elevated (the wizard uses an elevated helper, see
/// <see cref="ElevatedHelper"/>). Video files get an "Open with" entry; Windows' default apps are left alone.
/// </summary>
static class Installer
{
    /// <summary>An app's identity on disk and in the registry.</summary>
    sealed record Brand(string AppName, string ExeName, string ProgId, string Key)
    {
        public string UninstallKey => @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Key;
        public string AppKey => @"Software\" + Key;
        public string CapabilitiesKey => AppKey + @"\Capabilities";
        public string IconPattern => Key + "-*.ico";
    }

    static readonly Brand Current = new("Splicia", "Splicia.exe", "Splicia.video", "Splicia");

    /// <summary>Splicia's name before 2.0. Installing Splicia replaces a copy of it installed the same way.</summary>
    static readonly Brand Legacy = new("Video Trimmer", "VideoTrimmer.exe", "VideoTrimmer.video", "VideoTrimmer");

    public const string AppName = AppInfo.Name;
    const string ExeName = "Splicia.exe";
    const string ProgId = "Splicia.video";
    const string Description = AppInfo.Tagline;
    static string UninstallKey => Current.UninstallKey;
    static string AppKey => Current.AppKey;
    static string CapabilitiesKey => Current.CapabilitiesKey;

    public static readonly string[] VideoExtensions =
    {
        ".mp4", ".m4v", ".mov", ".qt", ".mkv", ".webm", ".avi", ".wmv", ".asf", ".flv", ".f4v", ".mpg", ".mpeg", ".m2v",
        ".m2ts", ".mts", ".ts", ".3gp", ".3g2", ".ogv", ".vob", ".mxf", ".dv", ".divx",
    };

    public static string Version => Application.ProductVersion.Split('+')[0];

    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    static RegistryKey Root(InstallScope s) => s == InstallScope.AllUsers ? Registry.LocalMachine : Registry.CurrentUser;

    public static string DefaultDirectory(InstallScope s) => s == InstallScope.AllUsers
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Splicia");

    static string StartMenuShortcut(InstallScope s, Brand? b = null) => Path.Combine(Environment.GetFolderPath(
        s == InstallScope.AllUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), (b ?? Current).AppName + ".lnk");

    static string DesktopShortcut(InstallScope s, Brand? b = null) => Path.Combine(Environment.GetFolderPath(
        s == InstallScope.AllUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory), (b ?? Current).AppName + ".lnk");

    /// <summary>Shared, user-writable crash-log folder for all-users installs (Program Files isn't writable by the app).</summary>
    public static string MachineLogDirectory => MachineLogDirectoryOf(Current);

    static string MachineLogDirectoryOf(Brand b) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), b.AppName, "CrashLogs");

    /// <summary>Folder of the installation for <paramref name="s"/>, or null if not installed that way.</summary>
    public static string? InstalledDirectory(InstallScope s) => InstalledDirectory(s, Current);

    static string? InstalledDirectory(InstallScope s, Brand b)
    {
        using var k = Root(s).OpenSubKey(b.UninstallKey);
        return k?.GetValue("InstallLocation") is string dir && File.Exists(Path.Combine(dir, b.ExeName)) ? dir : null;
    }

    /// <summary>Where Video Trimmer (Splicia's former name) is installed, by scope.</summary>
    public static IEnumerable<InstallScope> LegacyScopes() =>
        new[] { InstallScope.CurrentUser, InstallScope.AllUsers }.Where(s => Root(s).OpenSubKey(Legacy.UninstallKey) is { } k && Dispose(k));

    static bool Dispose(IDisposable d) { d.Dispose(); return true; }

    /// <summary>True if the old Video Trimmer is running from any of its installations.</summary>
    public static bool LegacyRunning() =>
        LegacyScopes().Any(s => InstalledDirectory(s, Legacy) is string dir && IsRunningFrom(dir, Legacy));

    public static string? InstalledVersion(InstallScope s)
    {
        if (InstalledDirectory(s) == null) return null;
        using var k = Root(s).OpenSubKey(UninstallKey);
        return k?.GetValue("DisplayVersion") as string;
    }

    public static IEnumerable<InstallScope> InstalledScopes() =>
        new[] { InstallScope.CurrentUser, InstallScope.AllUsers }.Where(s => InstalledDirectory(s) != null);

    /// <summary>The installed exe (per-user copy first, as HKCU wins over HKLM).</summary>
    internal static string InstalledExe => Path.Combine(
        InstalledDirectory(InstallScope.CurrentUser) ?? InstalledDirectory(InstallScope.AllUsers) ?? DefaultDirectory(InstallScope.CurrentUser), ExeName);

    public static bool HasDesktopShortcut(InstallScope s) => File.Exists(DesktopShortcut(s));
    public static bool HasStartMenuShortcut(InstallScope s) => File.Exists(StartMenuShortcut(s));

    /// <summary>True if Splicia is currently running from <paramref name="directory"/> (other than this process).</summary>
    public static bool IsRunningFrom(string directory) => IsRunningFrom(directory, Current);

    static bool IsRunningFrom(string directory, Brand b)
    {
        string target = Path.GetFullPath(Path.Combine(directory, b.ExeName));
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(b.ExeName)))
        {
            try
            {
                if (p.Id != Environment.ProcessId &&
                    string.Equals(p.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { /* access denied etc. */ }
            finally { p.Dispose(); }
        }
        return false;
    }

    /// <summary>Validates the chosen folder. Per-user installs must be writable without admin; all-users ones are written elevated.</summary>
    public static string? CheckDirectory(string dir, InstallScope scope)
    {
        try
        {
            dir = Path.GetFullPath(dir);
            if (Path.GetPathRoot(dir) == dir) return "Choose a folder, not the root of a drive.";
            if (scope == InstallScope.AllUsers && !IsElevated) return null; // the elevated helper creates it
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "You don't have permission to install there. Choose a folder in your user profile, " +
                   "or go back and choose \"Install for all users\".";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ───────────────────────── payload ─────────────────────────

    /// <summary>
    /// The setup exe is the app exe with a zip of the ffmpeg\ folder appended, then the zip's length (8 bytes) and
    /// <see cref="Magic"/> (8 bytes). The .NET bundle inside the exe is located by an offset stored in its header, so
    /// trailing data doesn't disturb it.
    /// </summary>
    const ulong Magic = 0x31444C5941505456; // "VTPAYLD1"

    /// <summary>(length of the exe part, offset and length of the zip), or null if this exe has no payload.</summary>
    static (long ExeLength, long ZipOffset, long ZipLength)? FindPayload(string exe)
    {
        using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 16) return null;
        fs.Seek(-16, SeekOrigin.End);
        var tail = new byte[16];
        fs.ReadExactly(tail);
        if (BitConverter.ToUInt64(tail, 8) != Magic) return null;
        long zipLength = BitConverter.ToInt64(tail, 0);
        long zipOffset = fs.Length - 16 - zipLength;
        return zipOffset > 0 ? (zipOffset, zipOffset, zipLength) : null;
    }

    // ───────────────────────── install ─────────────────────────

    public static void Install(InstallOptions o, Action<string, double>? progress = null)
    {
        void Step(string text, double fraction) => progress?.Invoke(text, fraction);
        var scope = o.Scope;
        if (scope == InstallScope.AllUsers && !IsElevated)
            throw new UnauthorizedAccessException("Installing for all users requires administrator permission.");
        string dir = Path.GetFullPath(o.Directory);
        string exe = Path.Combine(dir, ExeName);
        string? previousDir = InstalledDirectory(scope);
        string src = Environment.ProcessPath!;
        bool sameExe = string.Equals(Path.GetFullPath(src), exe, StringComparison.OrdinalIgnoreCase);

        Step("Copying files…", 0.04);
        Directory.CreateDirectory(dir);
        var payload = FindPayload(src);
        if (!sameExe)
            CopyWithProgress(src, exe, payload?.ExeLength ?? new FileInfo(src).Length, f => Step("Copying files…", 0.04 + 0.3 * f));

        Step("Installing FFmpeg…", 0.35);
        string ffDir = Path.Combine(dir, "ffmpeg");
        if (payload is { } pl)
            ExtractPayload(src, pl.ZipOffset, pl.ZipLength, ffDir, f => Step("Installing FFmpeg…", 0.35 + 0.45 * f));
        else if (!sameExe)
        {
            string srcFf = Path.Combine(Path.GetDirectoryName(src)!, "ffmpeg");
            if (!FFmpegRuntime.IsPresent(srcFf))
                throw new FileNotFoundException("The FFmpeg components weren't found. Run the full Splicia-Setup exe.");
            Directory.CreateDirectory(ffDir);
            var files = Directory.GetFiles(srcFf);
            for (int i = 0; i < files.Length; i++)
            {
                File.Copy(files[i], Path.Combine(ffDir, Path.GetFileName(files[i])), overwrite: true);
                Step("Installing FFmpeg…", 0.35 + 0.45 * (i + 1) / files.Length);
            }
        }
        if (!FFmpegRuntime.IsPresent(ffDir)) throw new FileNotFoundException("The FFmpeg components are incomplete.");

        string fileIcon = $"\"{WriteFileIcon(dir)}\",0";
        WriteResource("Splicia.LICENSE.txt", Path.Combine(dir, AppInfo.LicenseFile));
        WriteResource("Splicia.THIRD-PARTY-NOTICES.txt", Path.Combine(dir, AppInfo.NoticesFile));
        if (scope == InstallScope.AllUsers) PrepareMachineLogDirectory();

        Step("Adding Open with entries for videos…", 0.84);
        RegisterFileTypes(scope, exe, fileIcon);

        Step("Adding to Settings → Apps…", 0.88);
        string scopeArg = scope == InstallScope.AllUsers ? " --allusers" : "";
        long size = new FileInfo(exe).Length + Directory.GetFiles(ffDir).Sum(f => new FileInfo(f).Length);
        using (var k = Root(scope).CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", AppName);
            k.SetValue("DisplayVersion", Version);
            k.SetValue("Publisher", AppInfo.Publisher);
            k.SetValue("URLInfoAbout", AppInfo.Website);
            k.SetValue("HelpLink", AppInfo.Website);
            k.SetValue("Comments", $"{AppInfo.Tagline}. Licensed under {AppInfo.License}.");
            k.SetValue("DisplayIcon", $"\"{exe}\",0");
            k.SetValue("InstallLocation", dir);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall{scopeArg}");
            k.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet{scopeArg}");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
        }

        Step("Creating shortcuts…", 0.92);
        if (o.StartMenuShortcut) CreateShortcut(StartMenuShortcut(scope), exe); else DeleteIfOurs(StartMenuShortcut(scope));
        if (o.DesktopShortcut) CreateShortcut(DesktopShortcut(scope), exe); else DeleteIfOurs(DesktopShortcut(scope));

        // Moved to a new folder: remove the old copy's files.
        if (previousDir != null && !string.Equals(Path.GetFullPath(previousDir), dir, StringComparison.OrdinalIgnoreCase))
            try { RemoveOurFiles(previousDir); } catch { }

        // Splicia used to be Video Trimmer: remove that copy (same scope) so there's one app, not two.
        if (InstalledDirectory(scope, Legacy) != null || LegacyScopes().Contains(scope))
        {
            Step("Removing Video Trimmer (Splicia's old name)…", 0.94);
            try { Remove(Legacy, scope, forgetUser: true); }
            catch (Exception ex) { CrashLog.Write(ex, "Removing Video Trimmer"); }
        }

        Step("Refreshing icons…", 0.97);
        NotifyShell();
        Step("Done", 1.0);
    }

    static void CopyWithProgress(string src, string dst, long length, Action<double> progress)
    {
        string tmp = dst + ".new";
        using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            var buf = new byte[1 << 20];
            long done = 0;
            while (done < length)
            {
                int n = input.Read(buf, 0, (int)Math.Min(buf.Length, length - done));
                if (n <= 0) throw new EndOfStreamException("The setup file is incomplete.");
                output.Write(buf, 0, n);
                done += n;
                progress((double)done / length);
            }
        }
        File.Move(tmp, dst, overwrite: true);
    }

    static void ExtractPayload(string exe, long offset, long length, string dir, Action<double> progress)
    {
        Directory.CreateDirectory(dir);
        using var fs = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var slice = new SubStream(fs, offset, length);
        using var zip = new ZipArchive(slice, ZipArchiveMode.Read);
        long total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            string target = Path.GetFullPath(Path.Combine(dir, entry.FullName));
            if (!target.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string tmp = target + ".new";
            entry.ExtractToFile(tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            done += entry.Length;
            progress((double)done / total);
        }
    }

    /// <summary>ProgramData\Splicia\CrashLogs, writable by every user (the app runs unelevated).</summary>
    static void PrepareMachineLogDirectory()
    {
        var di = Directory.CreateDirectory(MachineLogDirectory);
        var acl = di.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        di.SetAccessControl(acl);
    }

    /// <summary>"Open with" entries for every video type, plus the app's registration for Settings → Default apps.
    /// Existing defaults are never changed.</summary>
    static void RegisterFileTypes(InstallScope scope, string exe, string fileIcon)
    {
        string open = $"\"{exe}\" \"%1\"";
        var root = Root(scope);
        using (var classes = root.CreateSubKey(@"Software\Classes"))
        {
            using (var k = classes.CreateSubKey(ProgId))
            {
                k.SetValue("", "Video");
                k.SetValue("FriendlyTypeName", "Video");
                using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", fileIcon);
                using (var c = k.CreateSubKey(@"shell\open")) c.SetValue("FriendlyAppName", AppName);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
            }
            foreach (var ext in VideoExtensions)
                using (var k = classes.CreateSubKey(ext + @"\OpenWithProgids")) k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);

            // Application registration (Open with → Choose another app).
            using (var k = classes.CreateSubKey(@"Applications\" + ExeName))
            {
                k.SetValue("FriendlyAppName", AppName);
                using (var ic = k.CreateSubKey("DefaultIcon")) ic.SetValue("", fileIcon);
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
                using var st = k.CreateSubKey("SupportedTypes");
                foreach (var ext in VideoExtensions) st.SetValue(ext, "");
            }
        }

        // Register with Settings → Default apps, so Splicia is listed there by name.
        using (var cap = root.CreateSubKey(CapabilitiesKey))
        {
            cap.SetValue("ApplicationName", AppName);
            cap.SetValue("ApplicationDescription", Description);
            cap.SetValue("ApplicationIcon", fileIcon);
            using var fa = cap.CreateSubKey("FileAssociations");
            foreach (var ext in VideoExtensions) fa.SetValue(ext, ProgId);
        }
        using (var ra = root.CreateSubKey(@"Software\RegisteredApplications"))
            ra.SetValue(AppName, CapabilitiesKey);
    }

    /// <summary>
    /// Writes the app icon as Splicia-&lt;hash&gt;.ico in the install folder and removes older ones. Explorer caches
    /// icons by path, so a content-derived name guarantees a changed icon is picked up instead of a stale cached one.
    /// </summary>
    static string WriteFileIcon(string dir)
    {
        using var res = typeof(Installer).Assembly.GetManifestResourceStream("Splicia.app.ico")
                        ?? throw new InvalidOperationException("Embedded icon missing.");
        using var ms = new MemoryStream();
        res.CopyTo(ms);
        byte[] bytes = ms.ToArray();
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..8].ToLowerInvariant();
        string path = Path.Combine(dir, $"Splicia-{hash}.ico");
        if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
        foreach (var old in Directory.GetFiles(dir, Current.IconPattern))
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(old); } catch { }
        return path;
    }

    static void WriteResource(string name, string path)
    {
        using var res = typeof(Installer).Assembly.GetManifestResourceStream(name)
                        ?? throw new InvalidOperationException($"Embedded {name} missing.");
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        res.CopyTo(file);
    }

    // ───────────────────────── uninstall ─────────────────────────

    /// <summary>
    /// Removes the installation for <paramref name="scope"/>. With <paramref name="forgetUser"/> (a real uninstall, not a
    /// move between scopes) the current user's Windows records that point at Splicia are cleared too.
    /// </summary>
    public static void Uninstall(InstallScope scope, bool forgetUser, Action<string, double>? progress = null) =>
        Remove(Current, scope, forgetUser, progress);

    static void Remove(Brand b, InstallScope scope, bool forgetUser, Action<string, double>? progress = null)
    {
        void Step(string text, double fraction) => progress?.Invoke(text, fraction);
        if (scope == InstallScope.AllUsers && !IsElevated)
            throw new UnauthorizedAccessException("Removing an all-users installation requires administrator permission.");
        string? dir = InstalledDirectory(scope, b);
        var root = Root(scope);

        Step("Removing Open with entries…", 0.2);
        using (var classes = root.OpenSubKey(@"Software\Classes", writable: true))
        {
            classes?.DeleteSubKeyTree(b.ProgId, false);
            classes?.DeleteSubKeyTree(@"Applications\" + b.ExeName, false);
        }
        foreach (var ext in VideoExtensions) ForgetClassesEntries(root, ext, b);
        if (forgetUser && scope == InstallScope.CurrentUser) ForgetUserRecords(b);

        Step("Removing from Settings → Apps…", 0.45);
        root.DeleteSubKeyTree(b.UninstallKey, false);
        root.DeleteSubKeyTree(b.AppKey, false);
        using (var ra = root.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            ra?.DeleteValue(b.AppName, false);

        Step("Removing shortcuts…", 0.65);
        DeleteIfOurs(StartMenuShortcut(scope, b), b);
        DeleteIfOurs(DesktopShortcut(scope, b), b);
        NotifyShell();

        Step("Removing files…", 0.85);
        if (dir != null) RemoveOurFiles(dir, b);
        if (scope == InstallScope.AllUsers)
            try { Directory.Delete(Path.GetDirectoryName(MachineLogDirectoryOf(b))!, recursive: true); } catch { }
        Step("Done", 1.0);
    }

    /// <summary>
    /// The current user's Windows records for a removed all-users install (the elevated helper may run as another
    /// account, so the wizard calls this itself afterwards). Skipped if still installed for this user.
    /// </summary>
    public static void ForgetCurrentUserRecords()
    {
        if (InstalledDirectory(InstallScope.CurrentUser) != null) return;
        ForgetUserRecords(Current);
    }

    static bool IsOurProgId(string? progId, Brand b) => progId != null &&
        (progId == b.ProgId || progId.Equals(@"Applications\" + b.ExeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Our Open-with entry under &lt;root&gt;\Software\Classes\&lt;ext&gt;, then the key if it's left empty.</summary>
    static void ForgetClassesEntries(RegistryKey root, string ext, Brand b)
    {
        using var classes = root.OpenSubKey(@"Software\Classes", writable: true);
        if (classes == null) return;
        using (var k = classes.OpenSubKey(ext, writable: true))
        {
            if (k == null) return;
            using (var owp = k.OpenSubKey("OpenWithProgids", writable: true))
                foreach (var name in owp?.GetValueNames() ?? Array.Empty<string>())
                    if (IsOurProgId(name, b)) owp!.DeleteValue(name, false);
            DeleteIfEmpty(k, "OpenWithProgids");
        }
        DeleteIfEmpty(classes, ext);
    }

    /// <summary>
    /// Clears what Windows remembers for this user that points at the app: a default-app choice the user made
    /// (UserChoice / UserChoiceLatest), its place in the "opened with" history and Open-with lists, the "new app can
    /// open this" notification records, and the cached app name. Entries for other apps are left alone.
    /// </summary>
    static void ForgetUserRecords(Brand b)
    {
        using (var fileExts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", writable: true))
        {
            foreach (var ext in VideoExtensions)
            {
                using (var k = fileExts?.OpenSubKey(ext, writable: true))
                {
                    if (k == null) continue;
                    foreach (var choice in new[] { "UserChoice", "UserChoiceLatest" })
                    {
                        bool ours;
                        using (var uc = k.OpenSubKey(choice))
                        using (var sub = uc?.OpenSubKey("ProgId"))
                            ours = IsOurProgId(uc?.GetValue("ProgId") as string, b) || IsOurProgId(sub?.GetValue("ProgId") as string, b);
                        if (ours)
                            try { k.DeleteSubKeyTree(choice, false); }
                            catch (Exception ex) { CrashLog.Write(ex, $"Clearing {ext} {choice}"); }
                    }
                    // "Opened with" history: single-letter values naming exes, ordered by MRUList.
                    using (var owl = k.OpenSubKey("OpenWithList", writable: true))
                    {
                        if (owl != null)
                        {
                            string mru = owl.GetValue("MRUList") as string ?? "";
                            foreach (var name in owl.GetValueNames())
                                if (name.Length == 1 && string.Equals(owl.GetValue(name) as string, b.ExeName, StringComparison.OrdinalIgnoreCase))
                                {
                                    owl.DeleteValue(name, false);
                                    mru = mru.Replace(name, "");
                                }
                            if (mru.Length > 0) owl.SetValue("MRUList", mru); else owl.DeleteValue("MRUList", false);
                        }
                    }
                    DeleteIfEmpty(k, "OpenWithList");
                    using (var owp = k.OpenSubKey("OpenWithProgids", writable: true))
                        foreach (var name in owp?.GetValueNames() ?? Array.Empty<string>())
                            if (IsOurProgId(name, b)) owp!.DeleteValue(name, false);
                    DeleteIfEmpty(k, "OpenWithProgids");
                }
                if (fileExts != null) DeleteIfEmpty(fileExts, ext);
            }
        }

        // "You have a new app that can open this type of file" records: "<progid>_<ext>".
        using (var toasts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ApplicationAssociationToasts", writable: true))
            foreach (var name in toasts?.GetValueNames() ?? Array.Empty<string>())
            {
                int us = name.LastIndexOf('_');
                if (us > 0 && IsOurProgId(name[..us], b)) toasts!.DeleteValue(name, false);
            }

        // Windows' cached display name / company for the exe (MuiCache).
        using (var mui = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", writable: true))
            foreach (var name in mui?.GetValueNames() ?? Array.Empty<string>())
                if (name.Contains(@"\" + b.ExeName + ".", StringComparison.OrdinalIgnoreCase))
                    mui!.DeleteValue(name, false);
        NotifyShell();
    }

    /// <summary>Deletes parent\name if it has no values (other than an empty default) and no subkeys.</summary>
    static void DeleteIfEmpty(RegistryKey parent, string name)
    {
        bool empty;
        using (var k = parent.OpenSubKey(name))
        {
            if (k == null) return;
            empty = k.SubKeyCount == 0 &&
                    k.GetValueNames().All(v => v.Length == 0 && string.IsNullOrEmpty(k.GetValue(v) as string));
        }
        if (empty) try { parent.DeleteSubKeyTree(name, false); } catch { }
    }

    /// <summary>
    /// Deletes only what the app put in the folder (exe, file icon, ffmpeg\, crash logs), then the folder if it's
    /// empty, so a custom location that holds other files is never wiped. If the exe is in use (this process, or the
    /// wizard that launched an elevated helper), a hidden cmd keeps retrying for up to two minutes until it's free.
    /// </summary>
    static void RemoveOurFiles(string dir, Brand? brand = null)
    {
        var b = brand ?? Current;
        string exe = Path.Combine(dir, b.ExeName);
        string ff = Path.Combine(dir, "ffmpeg");
        try
        {
            if (Directory.Exists(ff)) Directory.Delete(ff, recursive: true);
            if (File.Exists(exe)) File.Delete(exe);
            foreach (var ico in Directory.GetFiles(dir, b.IconPattern)) File.Delete(ico);
            if (b == Current)
                foreach (var doc in new[] { AppInfo.LicenseFile, AppInfo.NoticesFile })
                    if (File.Exists(Path.Combine(dir, doc))) File.Delete(Path.Combine(dir, doc));
            string logs = Path.Combine(dir, "CrashLogs");
            if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            return;
        }
        catch (Exception) when (File.Exists(exe) || Directory.Exists(ff)) { /* in use: defer */ }

        string cmd = $"/c for /l %i in (1,1,120) do @(if exist \"{exe}\" (del /f /q \"{exe}\" >nul 2>&1 & if exist \"{exe}\" ping -n 2 127.0.0.1 >nul))" +
                     $" & rmdir /s /q \"{ff}\" >nul 2>&1" +
                     $" & del /f /q \"{Path.Combine(dir, b.IconPattern)}\" >nul 2>&1" +
                     (b == Current ? $" & del /f /q \"{Path.Combine(dir, AppInfo.LicenseFile)}\" \"{Path.Combine(dir, AppInfo.NoticesFile)}\" >nul 2>&1" : "") +
                     $" & rmdir /s /q \"{Path.Combine(dir, "CrashLogs")}\" >nul 2>&1 & rmdir \"{dir}\" >nul 2>&1";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }

    // ───────────────────────── quiet (command-line) mode ─────────────────────────

    public static int InstallQuiet(InstallScope scope)
    {
        try
        {
            if (scope == InstallScope.AllUsers && !IsElevated) return ElevatedHelper.RelaunchElevatedAndWait();
            string dir = InstalledDirectory(scope) ?? DefaultDirectory(scope);
            bool startMenu = InstalledDirectory(scope) == null || HasStartMenuShortcut(scope);
            Install(new InstallOptions(scope, dir, startMenu, HasDesktopShortcut(scope)));
            return 0;
        }
        catch (Exception ex) { CrashLog.Write(ex, "Quiet install"); return 1; }
    }

    public static int UninstallQuiet(InstallScope scope)
    {
        try
        {
            if (scope == InstallScope.AllUsers && !IsElevated)
            {
                int rc = ElevatedHelper.RelaunchElevatedAndWait();
                if (rc == 0) ForgetCurrentUserRecords(); // the elevated run may be another account
                return rc;
            }
            Uninstall(scope, forgetUser: true);
            return 0;
        }
        catch (Exception ex) { CrashLog.Write(ex, "Quiet uninstall"); return 1; }
    }

    // ───────────────────────── helpers ─────────────────────────

    static void CreateShortcut(string lnk, string target)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Description = Description;
            sc.Save();
        }
        catch { /* shortcuts are optional */ }
    }

    /// <summary>Deletes a shortcut only if it points at the app's exe (never a user's unrelated .lnk with the same name).</summary>
    static void DeleteIfOurs(string lnk, Brand? brand = null)
    {
        string exeName = (brand ?? Current).ExeName;
        if (!File.Exists(lnk)) return;
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t != null)
            {
                dynamic shell = Activator.CreateInstance(t)!;
                string target = shell.CreateShortcut(lnk).TargetPath;
                if (!target.EndsWith(exeName, StringComparison.OrdinalIgnoreCase)) return;
            }
            File.Delete(lnk);
        }
        catch { }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    static void NotifyShell() => SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Read-only window onto part of another stream (the zip inside the setup exe).</summary>
    sealed class SubStream : Stream
    {
        readonly Stream _inner;
        readonly long _start, _length;
        long _pos;

        public SubStream(Stream inner, long start, long length) { _inner = inner; _start = start; _length = length; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, _length); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            count = (int)Math.Min(count, _length - _pos);
            if (count <= 0) return 0;
            _inner.Position = _start + _pos;
            int n = _inner.Read(buffer, offset, count);
            _pos += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _pos + offset,
            _ => _length + offset,
        };

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
