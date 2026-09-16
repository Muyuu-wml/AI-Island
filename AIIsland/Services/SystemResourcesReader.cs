using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace AIIsland.Services;

public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);
public sealed record MemorySnapshot(long TotalBytes, long AvailableBytes);
public sealed record SystemResourceSample(CpuTimes? Cpu, MemorySnapshot? Memory);
public sealed record DiskSnapshot(string Name, long TotalBytes, long FreeBytes);

public interface ISystemResourcesReader
{
    SystemResourceSample ReadUsage();
    IReadOnlyList<DiskSnapshot> ReadDisks();
}

/// <summary>GetSystemTimes includes idle time in kernel time, summed across all processors.</summary>
public sealed class CpuUsageTracker
{
    private CpuTimes? previous;

    public double? Sample(CpuTimes? current)
    {
        if (current is not { } value || value.Idle > value.Kernel)
        {
            Reset();
            return null;
        }

        var baseline = previous;
        previous = value;
        return baseline is { } old ? Calculate(old, value) : null;
    }

    public void Reset() => previous = null;

    public static double? Calculate(CpuTimes previous, CpuTimes current)
    {
        if (previous.Idle > previous.Kernel || current.Idle > current.Kernel ||
            current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User)
            return null;

        var kernelDelta = current.Kernel - previous.Kernel;
        var userDelta = current.User - previous.User;
        var idleDelta = current.Idle - previous.Idle;
        if (idleDelta > kernelDelta) return null;
        // Convert before adding so even very large counters cannot overflow ulong.
        var totalDelta = (double)kernelDelta + userDelta;
        return totalDelta > 0 ? Math.Clamp((totalDelta - idleDelta) / totalDelta * 100, 0, 100) : null;
    }
}

public sealed class SystemResourcesReader : ISystemResourcesReader
{
    public SystemResourceSample ReadUsage()
    {
        CpuTimes? cpu = null;
        MemorySnapshot? memory = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
            cpu = new CpuTimes(idle.Value, kernel.Value, user.Value);

        var state = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref state) && state.TotalPhysical <= long.MaxValue && state.AvailablePhysical <= long.MaxValue)
            memory = new MemorySnapshot((long)state.TotalPhysical, (long)state.AvailablePhysical);

        return new SystemResourceSample(cpu, memory);
    }

    public IReadOnlyList<DiskSnapshot> ReadDisks()
    {
        var disks = new List<DiskSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // Empty optical drives are not storage volumes. A slow mapped/removable drive
                // can delay this scan, but the module never awaits it on the usage/UI path.
                if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown || !drive.IsReady)
                    continue;
                var name = drive.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                disks.Add(new DiskSnapshot(name, drive.TotalSize, drive.TotalFreeSpace));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A drive may disappear or become inaccessible between enumeration and reading.
            }
        }
        return disks;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
