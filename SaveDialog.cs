using System.Runtime.InteropServices;

namespace Splicia;

/// <summary>
/// Windows' own Save As dialog (IFileSaveDialog), for what WinForms' can't do: open in the folder last used for this
/// kind of file (Windows keeps it, per <c>client</c> id, across runs: Splicia stores nothing), and only the first time
/// in a default folder. WinForms' InitialDirectory always wins over the folder last used.
/// </summary>
static class SaveDialog
{
    const uint FOS_OVERWRITEPROMPT = 0x2, FOS_STRICTFILETYPES = 0x4, FOS_NOCHANGEDIR = 0x8, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800, FOS_NOREADONLYRETURN = 0x8000;
    const uint SIGDN_FILESYSPATH = 0x80058000;
    const int ERROR_CANCELLED = unchecked((int)0x800704C7);

    /// <summary>
    /// Asks where to save: the dialog over <paramref name="owner"/>, with <paramref name="fileName"/> suggested, in the
    /// folder last chosen in a dialog with this <paramref name="client"/> id (else <paramref name="defaultFolder"/>).
    /// The full path chosen (with <paramref name="extension"/>), or null if cancelled.
    /// </summary>
    public static string? Ask(IntPtr owner, string title, string fileName, Guid client, string defaultFolder, string typeName, string extension)
    {
        var dialog = Create(title, fileName, client, defaultFolder, typeName, extension);
        try
        {
            int hr = dialog.Show(owner);
            if (hr == ERROR_CANCELLED) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out var item);
            item.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            Marshal.ReleaseComObject(item);
            return path;
        }
        finally { Marshal.ReleaseComObject(dialog); }
    }

    /// <summary>The dialog, set up and not shown (a test looks at it).</summary>
    internal static IFileSaveDialog Create(string title, string fileName, Guid client, string defaultFolder, string typeName, string extension)
    {
        var dialog = (IFileSaveDialog)new FileSaveDialogClass();
        try
        {
            dialog.SetClientGuid(ref client);
            dialog.GetOptions(out uint options);
            // (strict file types: a name typed with another extension gets this one, and the overwrite question is about it)
            dialog.SetOptions(options | FOS_OVERWRITEPROMPT | FOS_STRICTFILETYPES | FOS_NOCHANGEDIR | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOREADONLYRETURN);
            dialog.SetTitle(title);
            dialog.SetFileTypes(1, new[] { new FilterSpec { Name = $"{typeName} (*{extension})", Spec = "*" + extension } });
            dialog.SetDefaultExtension(extension.TrimStart('.'));
            dialog.SetFileName(fileName);
            if (Directory.Exists(defaultFolder))
            {
                var iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(defaultFolder, IntPtr.Zero, ref iid, out var folder);
                dialog.SetDefaultFolder(folder); // only when there's no folder last used: SetFolder would always win
                Marshal.ReleaseComObject(folder);
            }
            return dialog;
        }
        catch
        {
            Marshal.ReleaseComObject(dialog);
            throw;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem item);

    [ComImport, Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B"), ClassInterface(ClassInterfaceType.None)]
    class FileSaveDialogClass { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    /// <summary>IFileSaveDialog, with IModalWindow's and IFileDialog's methods first, in their order (shobjidl_core.h).</summary>
    [ComImport, Guid("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileSaveDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int where);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        // IFileSaveDialog's own
        void SetSaveAsItem(IShellItem item);
        void SetProperties(IntPtr store);
        void SetCollectedProperties(IntPtr list, int appendDefault);
        void GetProperties(out IntPtr store);
        void ApplyProperties(IShellItem item, IntPtr store, IntPtr hwnd, IntPtr sink);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
