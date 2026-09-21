using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AIIsland.Services;
public sealed class Settings
{
    public bool BalanceEnabled { get; set; }
    public string BalanceEmail { get; set; } = "";
    public string BalancePassword { get; set; } = "";
    public int BalanceRefreshSeconds { get; set; } = 60;
    public bool Codex { get; set; } = true;
    public bool Claude { get; set; } = true;
    public bool QQMusic { get; set; } = true;
    public bool NetEaseMusic { get; set; } = true;
    public bool Clash { get; set; } = true;
    public bool SystemResources { get; set; } = true;
    public string[] MonitorOrder { get; set; } = MonitorOrderCatalog.DefaultIds;
    public bool Animation { get; set; } = true;
    public bool EnhancedOutline { get; set; } = true;
    public bool RainbowOutline { get; set; } = true;
    public string OutlinePalette { get; set; } = "Rainbow";
    public string OutlineColor { get; set; } = "#72D8EE";
    public string OutlineMode { get; set; } = "Static";
    private double outlineThickness = 1;
    public double OutlineThickness
    {
        get => outlineThickness;
        set => outlineThickness = double.IsFinite(value) ? Math.Clamp(value, 0.5, 6) : 1;
    }
    public bool Notifications { get; set; } = true;
    // Legacy Notifications controlled approval reminders too; inherit it until saved separately.
    public bool? ApprovalNotifications { get; set; }
    public bool StartWithWindows { get; set; }
    public string Position { get; set; } = "Center";
    // Desktop coordinates in the window's DPI-awareness context.
    public int? CustomLeft { get; set; }
    public int? CustomTop { get; set; }
}
public static class SettingsService
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIIsland");
    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(DataDirectory, "settings.json"))) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public static void Save(Settings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (settings.StartWithWindows) key.SetValue("AIIsland", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("AIIsland", false);
        SavePosition(settings);
    }
    public static void SavePosition(Settings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var target = Path.Combine(DataDirectory, "settings.json");
        File.WriteAllText(target + ".tmp", JsonSerializer.Serialize(settings));
        File.Move(target + ".tmp", target, true);
    }
}
