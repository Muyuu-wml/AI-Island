using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AIIsland.Services;
public sealed record ProxySnapshot(int Flags, string Server, string Bypass, string Pac)
{
    public bool Enabled => (Flags & 2) != 0;
    public bool HasPac => (Flags & 4) != 0;
}
public interface ISystemProxyService
{
    ProxySnapshot Read();
    void SetEnabled(ProxySnapshot before, string server, bool enabled);
}
public static class ProxyPolicy
{
    public static bool Matches(string server, string target)
    {
        if (string.IsNullOrWhiteSpace(server)) return false;
        var entries = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) return false;
        foreach (var part in entries)
        {
            var endpoint = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            if (!endpoint.Equals(target, StringComparison.OrdinalIgnoreCase) && !endpoint.Equals(target.Replace("127.0.0.1", "localhost"), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
    public static string? Conflict(ProxySnapshot state, string? target, bool guard)
    {
        if (target == null) return "无法识别 Clash 本地代理端口";
        if (guard) return "Clash 代理守卫已启用，请在 Clash 中操作";
        if (state.HasPac) return "当前使用 PAC，请在 Clash 中操作";
        if (!string.IsNullOrWhiteSpace(state.Server) && !Matches(state.Server, target)) return "检测到其他代理配置，请在 Clash 中操作";
        return null;
    }
}
public sealed class SystemProxyService : ISystemProxyService
{
    [StructLayout(LayoutKind.Explicit)] private struct Value { [FieldOffset(0)] public int Number; [FieldOffset(0)] public IntPtr Text; [FieldOffset(0)] public long Time; }
    [StructLayout(LayoutKind.Sequential)] private struct Option { public int Kind; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] private struct Options { public int Size; public IntPtr Connection; public int Count, Error; public IntPtr Items; }
    public ProxySnapshot Read()
    {
        var size = Marshal.SizeOf<Option>(); var buffer = Marshal.AllocHGlobal(size * 4);
        try
        {
            for (var i = 0; i < 4; i++) Marshal.StructureToPtr(new Option { Kind = i + 1 }, buffer + i * size, false);
            var list = new Options { Size = Marshal.SizeOf<Options>(), Count = 4, Items = buffer }; var length = list.Size;
            if (!InternetQueryOption(IntPtr.Zero, 75, ref list, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var flags = Marshal.PtrToStructure<Option>(buffer).Value.Number;
            string ReadText(int i) => Marshal.PtrToStringUni(Marshal.PtrToStructure<Option>(buffer + i * size).Value.Text) ?? "";
            return new(flags, ReadText(1), ReadText(2), ReadText(3));
        }
        finally {
            for (var i = 1; i < 4; i++) { var pointer = Marshal.PtrToStructure<Option>(buffer + i * size).Value.Text; if (pointer != IntPtr.Zero) GlobalFree(pointer); }
            Marshal.FreeHGlobal(buffer);
        }
    }
    public void SetEnabled(ProxySnapshot before, string server, bool enabled)
    {
        var size = Marshal.SizeOf<Option>(); var buffer = Marshal.AllocHGlobal(size * 2);
        var text = Marshal.StringToHGlobalUni(enabled ? server : before.Server);
        try
        {
            Marshal.StructureToPtr(new Option { Kind = 1, Value = new Value { Number = enabled ? before.Flags | 3 : (before.Flags & ~2) | 1 } }, buffer, false);
            Marshal.StructureToPtr(new Option { Kind = 2, Value = new Value { Text = text } }, buffer + size, false);
            var list = new Options { Size = Marshal.SizeOf<Options>(), Count = 2, Items = buffer };
            if (!InternetSetOption(IntPtr.Zero, 75, ref list, list.Size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            InternetSetOptionNotify(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOptionNotify(IntPtr.Zero, 37, IntPtr.Zero, 0);
        }
        finally { Marshal.FreeHGlobal(text); Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)] private static extern bool InternetQueryOption(IntPtr h, int option, ref Options value, ref int length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] private static extern bool InternetSetOption(IntPtr h, int option, ref Options value, int length);
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)] private static extern bool InternetSetOptionNotify(IntPtr h, int option, IntPtr value, int length);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
}
