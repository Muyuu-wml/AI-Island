using System.Diagnostics;

namespace AIIsland.Hook;

/// <summary>
/// Walks up from an agent process to the terminal window that owns it. Returns the
/// outermost shell (WindowsTerminal, conhost, powershell, cmd, ...) so the island can
/// bring that window forward instead of the agent's hidden child process.
/// </summary>
internal static class ProcessLifetime
{
    private static readonly string[] Shells =
    {
        "WindowsTerminal", "wt", "conhost", "OpenConsole", "pwsh", "powershell",
        "cmd", "bash", "wsl", "mintty", "alacritty", "wezterm-gui",
    };

    public static (int? Id, DateTimeOffset? Started) FindShell(int? startId, DateTimeOffset? startTime)
    {
        if (!OperatingSystem.IsWindows() || startId is not { } id) return (null, null);
        try
        {
            var shellId = id;
            var shellStarted = startTime;
            for (var depth = 0; depth < 6; depth++)
            {
                using var process = Process.GetProcessById(shellId);
                if (process.HasExited) break;
                if (Shells.Any(s => process.ProcessName.Equals(s, StringComparison.OrdinalIgnoreCase)))
                    return (shellId, new DateTimeOffset(process.StartTime.ToUniversalTime()));
                var parent = ParentId(process);
                if (parent <= 0 || parent == shellId) break;
                shellId = parent;
                try { shellStarted = new DateTimeOffset(Process.GetProcessById(parent).StartTime.ToUniversalTime()); }
                catch { shellStarted = null; }
            }
        }
        catch { }
        return (null, null);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BasicInfo { public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentId; }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int kind, ref BasicInfo info, int size, out int returned);

    private static int ParentId(Process process)
    {
        try
        {
            var info = new BasicInfo();
            return NtQueryInformationProcess(process.Handle, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf<BasicInfo>(), out _) == 0
                ? info.ParentId.ToInt32()
                : 0;
        }
        catch { return 0; }
    }
}
