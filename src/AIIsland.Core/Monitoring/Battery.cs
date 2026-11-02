using System.Runtime.InteropServices;

namespace AIIsland.Core.Monitoring;

/// <summary>Battery percentage is -1 when the machine reports no battery or no estimate.</summary>
public sealed record BatteryInfo(int Percent, bool Charging, bool OnAc, int? MinutesLeft);

public static class Battery
{
    private const byte Unknown = 255;
    private const byte Charging = 8;
    private const byte NoBattery = 128;

    public static BatteryInfo? Read()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return null;
            if (status.BatteryFlag == Unknown || status.BatteryFlag == NoBattery) return null;
            if (status.BatteryLifePercent == Unknown) return null;

            var minutes = status.BatteryLifeTime == uint.MaxValue ? (int?)null : (int)(status.BatteryLifeTime / 60);
            return new BatteryInfo(
                status.BatteryLifePercent,
                (status.BatteryFlag & Charging) != 0,
                status.ACLineStatus == 1,
                minutes);
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
