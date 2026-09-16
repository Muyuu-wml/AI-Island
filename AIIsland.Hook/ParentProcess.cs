using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class ParentProcess
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo { public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int kind, ref BasicInfo info, int size, out int returned);
    public static (int? Id, DateTimeOffset? Started) Find(string provider)
    {
        if (!OperatingSystem.IsWindows()) return (null, null);
        try
        {
            var id = Environment.ProcessId;
            for (var depth = 0; depth < 8; depth++)
            {
                using var process = Process.GetProcessById(id);
                if (depth > 0 && (process.ProcessName.Equals(provider, StringComparison.OrdinalIgnoreCase) || process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)))
                    return (id, new DateTimeOffset(process.StartTime.ToUniversalTime()));
                var info = new BasicInfo();
                if (NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<BasicInfo>(), out _) != 0) break;
                var parent = info.ParentId.ToInt32();
                if (parent <= 0 || parent == id) break;
                id = parent;
            }
        }
        catch { }
        return (null, null);
    }
}
