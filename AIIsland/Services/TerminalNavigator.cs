using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AIIsland.Services;

public static class TerminalNavigator
{
    public static string Open(int? pid, DateTimeOffset? started)
    {
        if (pid == null || started == null) return "缺少终端关联，请回到启动该会话的终端。";
        try
        {
            using var agent = Process.GetProcessById(pid.Value);
            if (new DateTimeOffset(agent.StartTime.ToUniversalTime()) != started || agent.HasExited)
                return "原会话进程已退出，无法定位终端。";
            // Classic console windows can be identified without guessing window titles.
            if (AttachConsole((uint)pid.Value))
            {
                IntPtr console;
                try { console = GetConsoleWindow(); }
                finally { FreeConsole(); }
                if (console != IntPtr.Zero && IsWindowVisible(console)) return Activate(console, false);
            }
            var id = pid.Value;
            var childStarted = agent.StartTime.ToUniversalTime();
            for (var depth = 0; depth < 12; depth++)
            {
                using var process = Process.GetProcessById(id);
                var parentStarted = process.StartTime.ToUniversalTime();
                if (parentStarted > childStarted) break; // Parent PID was reused.
                if (process.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase)
                    && process.MainWindowHandle != IntPtr.Zero)
                {
                    var windows = 0;
                    EnumWindows((window, _) => { GetWindowThreadProcessId(window, out var owner); if (owner == (uint)id && IsWindowVisible(window)) windows++; return true; }, IntPtr.Zero);
                    if (windows == 1) return Activate(process.MainWindowHandle, true);
                    return "该终端进程有多个窗口，无法确定对应窗口，请手动选择。";
                }
                var info = new BasicInfo();
                if (NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<BasicInfo>(), out _) != 0) break;
                var parent = info.ParentId.ToInt32();
                if (parent <= 0 || parent == id) break;
                id = parent; childStarted = parentStarted;
            }
            return "无法可靠定位该会话窗口，请手动切回终端。";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return "终端已退出或无权访问，请手动切回。"; }
    }
    private static string Activate(IntPtr handle, bool terminal)
    {
        if (IsIconic(handle)) ShowWindowAsync(handle, 9);
        if (!SetForegroundWindow(handle)) return "Windows 未允许切换焦点，请从任务栏打开终端。";
        return terminal ? "已打开关联终端窗口；多标签页请手动选择对应会话。" : "";
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicInfo { public IntPtr Reserved1, Peb, Reserved2, Reserved3, ProcessId, ParentId; }
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int kind, ref BasicInfo info, int size, out int returned);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    private delegate bool EnumWindow(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
}
