using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIIsland.Core;

public enum CompactStyle
{
    Notch,
    Capsule,
    Indicator,
}

public enum TopMode
{
    BelowTaskbar,
    ScreenTop,
}

public sealed class IslandSettings
{
    public int Version { get; set; } = 2;
    public string Language { get; set; } = "zh-CN";
    public bool StartWithWindows { get; set; } = true;
    public CompactStyle Style { get; set; } = CompactStyle.Notch;
    public TopMode TopMode { get; set; } = TopMode.BelowTaskbar;
    public bool HideInFullscreen { get; set; } = true;
    public bool HideWhenIdle { get; set; }
    public double Scale { get; set; } = 1;
    public int TopOffset { get; set; } = 0;
    /// <summary>Monitor device name such as \\.\DISPLAY2; null means the primary screen.</summary>
    public string? Display { get; set; }
    public List<string> PageOrder { get; set; } = new() { "ai", "system", "music", "weather", "todo" };
    public AppearanceSettings Appearance { get; set; } = new();
    public List<ColumnSettings> Columns { get; set; } = CreateDefaultColumns();
    public SessionSettings Sessions { get; set; } = new();
    public MonitorSettings Monitor { get; set; } = new();
    public MusicSettings Music { get; set; } = new();
    public WeatherSettings Weather { get; set; } = new();
    public AlertSettings Alerts { get; set; } = new();

    public static List<ColumnSettings> CreateDefaultColumns() => new()
    {
        new ColumnSettings { Name = "Claude", QuotaSource = SourceCatalog.Claude, UsageSource = SourceCatalog.Claude, Accent = "#D97757" },
        new ColumnSettings { Name = "Codex", QuotaSource = SourceCatalog.Codex, UsageSource = SourceCatalog.Codex, Accent = "#10A37F" },
    };
}

public sealed class ColumnSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string QuotaSource { get; set; } = SourceCatalog.Claude;
    public string UsageSource { get; set; } = SourceCatalog.Claude;
    public string Accent { get; set; } = "#5B8CFF";
    public string? Site { get; set; }
    public bool AllowHttp { get; set; }
}

public sealed class AppearanceSettings
{
    public string Panel { get; set; } = "#0B0D12";
    public string Text { get; set; } = "#F2F4F8";
    public string Muted { get; set; } = "#8B93A7";
    public string Accent { get; set; } = "#5B8CFF";
    public string Theme { get; set; } = Palettes.Default;
    public bool ShowClock { get; set; } = true;
    public bool ShowSessionPreview { get; set; } = true;
}

public sealed class SessionSettings
{
    public bool Enabled { get; set; } = true;
    public bool NotifyDone { get; set; } = true;
    public bool NotifyWaiting { get; set; } = true;
    public bool NotifyFailed { get; set; } = true;
    public int KeepMinutes { get; set; } = 30;
}

public sealed class MonitorSettings
{
    public bool Enabled { get; set; } = true;
    public bool ShowInCompact { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowNetwork { get; set; } = true;
    public bool ShowDisks { get; set; } = true;
    public bool ShowBattery { get; set; } = true;
    public int IntervalMs { get; set; } = 1500;
}

public sealed class WeatherSettings
{
    public bool Enabled { get; set; }

    /// <summary>City name ("北京") or an explicit "lat,lon" pair.</summary>
    public string City { get; set; } = "";
    public bool ShowAqi { get; set; } = true;
}

public sealed class MusicSettings
{
    public bool Enabled { get; set; } = true;
    public bool ShowInCompact { get; set; } = true;
    public bool ShowLyrics { get; set; } = true;
}

public sealed class AlertSettings
{
    /// <summary>Alert when any column's peak exceeds this percentage.</summary>
    public double QuotaThreshold { get; set; } = 80.0;

    /// <summary>Alert when a DeepSeek or relay balance drops below this amount, in the account's own currency.</summary>
    public decimal BalanceThreshold { get; set; } = 10.0m;
}

public sealed class SettingsStore
{
    private readonly string path;

    public SettingsStore(string? directory = null) => path = Path.Combine(directory ?? AppPaths.Data, "island.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IslandSettings Load()
    {
        try
        {
            if (File.Exists(path))
            {
                var settings = JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(path), Options);
                if (settings is not null) return Normalize(settings);
            }
        }
        catch { /* fall through to defaults */ }
        return Normalize(new IslandSettings());
    }

    public void Save(IslandSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(settings, Options);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, path, true);
    }

    private static IslandSettings Normalize(IslandSettings settings)
    {
        settings.Columns ??= new List<ColumnSettings>();
        settings.Alerts ??= new AlertSettings();
        settings.Sessions ??= new SessionSettings();
        settings.Weather ??= new WeatherSettings();
        settings.Weather.City ??= "";
        settings.Sessions.KeepMinutes = Math.Clamp(settings.Sessions.KeepMinutes, 1, 1440);
        if (settings.Columns.Count == 0) settings.Columns = IslandSettings.CreateDefaultColumns();
        settings.PageOrder = NormalizePageOrder(settings.PageOrder);
        return settings;
    }

    public static List<string> NormalizePageOrder(IEnumerable<string>? order)
    {
        var known = new[] { "ai", "system", "music", "weather", "todo" };
        var result = new List<string>();
        foreach (var id in order ?? Enumerable.Empty<string>())
            if (known.Contains(id) && !result.Contains(id)) result.Add(id);
        foreach (var id in known)
            if (!result.Contains(id)) result.Add(id);
        return result;
    }
}

/// <summary>API keys are encrypted with DPAPI for the current Windows user.</summary>
public sealed class SecretStore
{
    private readonly Dictionary<string, string> values;
    private readonly string path;

    private SecretStore(Dictionary<string, string> values, string path) => (this.values, this.path) = (values, path);

    public static SecretStore Load(string? directory = null)
    {
        var path = Path.Combine(directory ?? AppPaths.Data, "secrets.dat");
        try
        {
            if (!File.Exists(path)) return new SecretStore(new Dictionary<string, string>(), path);
            var encrypted = File.ReadAllBytes(path);
            var plain = System.Security.Cryptography.ProtectedData.Unprotect(encrypted, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? new Dictionary<string, string>();
            return new SecretStore(values, path);
        }
        catch
        {
            return new SecretStore(new Dictionary<string, string>(), path);
        }
    }

    public string? Get(string key) => values.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    public void Set(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) values.Remove(key);
        else values[key] = value.Trim();
    }

    public void Remove(string key) => values.Remove(key);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(values);
        var encrypted = System.Security.Cryptography.ProtectedData.Protect(plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, encrypted);
        File.Move(temporary, path, true);
    }
}
