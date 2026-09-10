using System.Runtime.InteropServices;

namespace bulk2rest.Web;

/// Opens the native Windows "open file" dialog on the machine hosting the UI
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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const int OFN_FILEMUSTEXIST = 0x1000;
    private const int OFN_PATHMUSTEXIST = 0x0800;
    private const int OFN_EXPLORER = 0x00080000;
    private const int BufferChars = 4096;

    /// Returns the selected path, or null if the user cancelled or the OS is not Windows.
    public static string? Pick()
    {
        if (!OperatingSystem.IsWindows()) return null;

        // Own the dialog to whatever window is in front (the browser that called us),
        // so it opens on top and focused instead of behind, fighting the foreground lock.
        var owner = GetForegroundWindow();

        string? result = null;
        // The common dialog requires an STA thread.
        var thread = new Thread(() => result = ShowDialog(owner));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static string? ShowDialog(IntPtr owner)
    {
        var buffer = Marshal.AllocHGlobal(BufferChars * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0, 0); // empty initial filename
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = "CSV files\0*.csv\0Text files\0*.csv;*.txt\0All files\0*.*\0\0",
                lpstrFile = buffer,
                nMaxFile = BufferChars,
                lpstrTitle = "Select CSV export",
                Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER,
            };
            return GetOpenFileName(ref ofn) ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
