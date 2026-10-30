using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace AIIsland.Core.Monitoring;

public sealed record DiskInfo(string Name, long Used, long Total);

public sealed record MonitorSample(
    double? CpuPercent,
    long MemoryUsed,
    long MemoryTotal,
    double? DownBytesPerSecond,
    double? UpBytesPerSecond,
    IReadOnlyList<DiskInfo> Disks,
    DateTimeOffset At);

public sealed class SystemMonitor
{
    private ulong previousIdle, previousKernel, previousUser;
    private long previousReceived, previousSent;
    private DateTimeOffset previousAt;
    private bool primed;

    public MonitorSample Sample(bool includeDisks)
    {
        var now = DateTimeOffset.Now;
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (primed)
                cpu = Calculate(idle, kernel, user);
            previousIdle = idle;
            previousKernel = kernel;
            previousUser = user;
        }

        var memory = ReadMemory();
        double? down = null, up = null;
        var (received, sent) = ReadNetwork();
        if (primed && previousAt != default && now > previousAt)
        {
            var seconds = (now - previousAt).TotalSeconds;
            if (seconds > 0.2)
            {
                down = Math.Max(0, (received - previousReceived) / seconds);
                up = Math.Max(0, (sent - previousSent) / seconds);
            }
        }
        previousReceived = received;
        previousSent = sent;
        previousAt = now;
        primed = true;

        return new MonitorSample(cpu, memory.Used, memory.Total, down, up, includeDisks ? ReadDisks() : Array.Empty<DiskInfo>(), now);
    }

    private double? Calculate(ulong idle, ulong kernel, ulong user)
    {
        if (idle > kernel) return null;
        var kernelDelta = kernel - previousKernel;
        var userDelta = user - previousUser;
        var idleDelta = idle - previousIdle;
        if (idleDelta > kernelDelta) return null;
        var total = (double)kernelDelta + userDelta;
        return total > 0 ? Math.Clamp((total - idleDelta) / total * 100, 0, 100) : null;
    }

    private static (long Used, long Total) ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return ((long)(status.TotalPhysical - status.AvailablePhysical), (long)status.TotalPhysical);
    }

    private static (long Received, long Sent) ReadNetwork()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch { }
        return (received, sent);
    }

    private static IReadOnlyList<DiskInfo> ReadDisks()
    {
        var disks = new List<DiskInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory or DriveType.Unknown or DriveType.Network || !drive.IsReady)
                    continue;
                disks.Add(new DiskInfo(drive.Name.TrimEnd('\\'), drive.TotalSize - drive.TotalFreeSpace, drive.TotalSize));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return disks;
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
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

public static class Human
{
    public static string Bytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return size.ToString(unit == 0 ? "0" : "0.#") + " " + units[unit];
    }

    public static string Rate(double bytesPerSecond) => Bytes((long)Math.Max(0, bytesPerSecond)) + "/s";

    /// <summary>Compact rate for the collapsed bar, e.g. "59.7K/s".</summary>
    public static string RateCompact(double bytesPerSecond)
    {
        var bytes = (long)Math.Max(0, bytesPerSecond);
        if (bytes < 1024) return bytes + "B/s";
        string[] units = { "K", "M", "G" };
        double size = bytes;
        var unit = -1;
        do
        {
            size /= 1024;
            unit++;
        } while (size >= 1024 && unit < units.Length - 1);
        return size.ToString(size >= 10 ? "0" : "0.#") + units[unit] + "/s";
    }

    public static string Money(decimal value) => value < 0.01m && value > 0 ? "<$0.01" : "$" + value.ToString("0.##");

    public static string Tokens(long value) => value >= 1_000_000 ? (value / 1_000_000.0).ToString("0.#") + "M"
        : value >= 1_000 ? (value / 1_000.0).ToString("0.#") + "K"
        : value.ToString();
}
