using System.Runtime.InteropServices;

namespace bulk2rest.Web;

internal enum PickKind { Csv, OpenConfig, SaveConfig }

/// Opens the native Windows open/save dialog on the machine hosting the UI
/// (same machine as the browser, since the server is localhost-only) and returns
/// the chosen absolute path. Browsers cannot expose a real disk path themselves.
internal static class NativeFilePicker
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFileTitle;
        public int nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileName(ref OpenFileName ofn);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const int OFN_OVERWRITEPROMPT = 0x0002;
    private const int OFN_NOCHANGEDIR = 0x0008;
    private const int OFN_FILEMUSTEXIST = 0x1000;
    private const int OFN_PATHMUSTEXIST = 0x0800;
    private const int OFN_EXPLORER = 0x00080000;
    private const int BufferChars = 4096;
    private const string JsonFilter = "JSON files\0*.json\0All files\0*.*\0\0";

    private static (string Title, string Filter, bool Save) SpecFor(PickKind kind) => kind switch
    {
        PickKind.OpenConfig => ("Open config", JsonFilter, false),
        PickKind.SaveConfig => ("Save config as", JsonFilter, true),
        _ => ("Select CSV export", "CSV files\0*.csv\0Text files\0*.csv;*.txt\0All files\0*.*\0\0", false),
    };

    /// Returns the selected path, or null if the user cancelled.
    /// initialPath pre-selects the folder and file name in the dialog.
    public static string? Pick(PickKind kind, string? initialPath = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "File dialogs are Windows-only: type the CSV path, pass --config for the config.");

        // Own the dialog to whatever window is in front (the browser that called us),
        // so it opens on top and focused instead of behind, fighting the foreground lock.
        var owner = GetForegroundWindow();

        string? result = null;
        // The common dialog requires an STA thread.
        var thread = new Thread(() => result = ShowDialog(owner, kind, initialPath));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static string? ShowDialog(IntPtr owner, PickKind kind, string? initialPath)
    {
        var (title, filter, save) = SpecFor(kind);
        var buffer = Marshal.AllocHGlobal(BufferChars * sizeof(char));
        try
        {
            var name = (Path.GetFileName(initialPath) ?? "") + "\0";
            Marshal.Copy(name.ToCharArray(), 0, buffer, name.Length);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filter,
                lpstrFile = buffer,
                nMaxFile = BufferChars,
                lpstrInitialDir = Path.GetDirectoryName(initialPath),
                lpstrTitle = title,
                lpstrDefExt = save ? "json" : null,
                // NOCHANGEDIR: the dialog would otherwise move the process cwd and break the relative out dir.
                Flags = (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST) | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_NOCHANGEDIR,
            };
            var ok = save ? GetSaveFileName(ref ofn) : GetOpenFileName(ref ofn);
            return ok ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
