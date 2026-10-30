using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIIsland.Services;

/// <summary>
/// Brings the source of an AI session forward: the owning terminal window when the
/// hook captured one, otherwise a terminal opened in the session directory.
/// </summary>
public static class TerminalNavigator
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);

    public static void Locate(int? terminalId, string? workingDirectory)
    {
        if (terminalId is { } id)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                var handle = process.MainWindowHandle;
                if (handle != IntPtr.Zero)
                {
                    if (IsIconic(handle)) ShowWindow(handle, 9);
                    SetForegroundWindow(handle);
                    return;
                }
            }
            catch { }
        }
        OpenDirectory(workingDirectory);
    }

    private static void OpenDirectory(string? directory)
    {
        var target = directory is { Length: > 0 } && Directory.Exists(directory) ? directory : null;
        try
        {
            if (target is not null)
                Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{target}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true });
        }
        catch
        {
            if (target is not null)
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
    }
}
