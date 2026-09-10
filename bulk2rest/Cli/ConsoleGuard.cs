using System.Runtime.InteropServices;

namespace bulk2rest.Cli;

/// Keeps the console window open when the exe was launched by double-click
/// (its own window), so output stays visible instead of flashing and closing.
public static class ConsoleGuard
{
    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);

    public static void PauseIfOwnWindow(string message = "Press any key to exit...")
    {
        // Redirected I/O means a script/pipe is driving us — never block there.
        if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
        if (!OwnsWindow()) return;

        Console.WriteLine();
        Console.WriteLine(message);
        try { Console.ReadKey(intercept: true); }
        catch { /* no interactive console */ }
    }

    /// True when this process is the only one attached to the console — i.e. it
    /// spawned its own window (double-click), rather than sharing a terminal.
    private static bool OwnsWindow()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var buffer = new uint[2];
            return GetConsoleProcessList(buffer, (uint)buffer.Length) <= 1;
        }
        catch { return false; }
    }
}
