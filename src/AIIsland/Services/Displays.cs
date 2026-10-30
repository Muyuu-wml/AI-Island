using Forms = System.Windows.Forms;

namespace AIIsland.Services;

public sealed record DisplayInfo(string? Device, string Label);

/// <summary>Monitor lookup by device name (e.g. \\.\DISPLAY2); an empty or unplugged name means the primary screen.</summary>
public static class Displays
{
    public static Forms.Screen Find(string? device) =>
        Forms.Screen.AllScreens.FirstOrDefault(screen => string.Equals(screen.DeviceName, device, StringComparison.OrdinalIgnoreCase))
        ?? Forms.Screen.PrimaryScreen
        ?? Forms.Screen.AllScreens[0];

    public static bool IsConnected(string? device) =>
        Forms.Screen.AllScreens.Any(screen => string.Equals(screen.DeviceName, device, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<DisplayInfo> List()
    {
        var result = new List<DisplayInfo> { new(null, "主显示器") };
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var label = $"{Name(screen.DeviceName)}  {screen.Bounds.Width}×{screen.Bounds.Height}";
            if (screen.Primary) label += "（主屏）";
            result.Add(new DisplayInfo(screen.DeviceName, label));
        }
        return result;
    }

    public static string Name(string device)
    {
        var digits = new string(device.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return digits.Length > 0 ? "显示器 " + digits : device;
    }
}
