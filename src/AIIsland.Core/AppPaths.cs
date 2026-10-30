namespace AIIsland.Core;

public static class AppPaths
{
    public static string Home { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string Data { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIIsland");
    public static string SettingsFile => Path.Combine(Data, "island.json");
    public static string SecretsFile => Path.Combine(Data, "secrets.dat");
    public static string PricesFile => Path.Combine(Data, "prices.json");
    public static string QuotaCacheFile => Path.Combine(Data, "quota-cache.json");
    public static string Inbox => Path.Combine(Data, "inbox");
    public static string Replies => Path.Combine(Data, "replies");
    public static string Logs => Path.Combine(Data, "logs");

    public static string ClaudeDir => Path.Combine(Home, ".claude");
    public static string ClaudeProjects => Path.Combine(ClaudeDir, "projects");
    public static string ClaudeCredentials => Path.Combine(ClaudeDir, ".credentials.json");
    public static string CodexDir => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Home, ".codex");
    public static string CodexSessions => Path.Combine(CodexDir, "sessions");
    public static string CodexAuth => Path.Combine(CodexDir, "auth.json");

    public static string OpenCodeData
    {
        get
        {
            if (Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg))
                return Path.Combine(xdg, "opencode");
            return Path.Combine(Home, ".local", "share", "opencode");
        }
    }

    public static string OpenCodeDb =>
        Environment.GetEnvironmentVariable("OPENCODE_DB") is { Length: > 0 } custom
            ? custom
            : Path.Combine(OpenCodeData, "opencode.db");

    public static string OpenCodeAuth => Path.Combine(OpenCodeData, "auth.json");

    public static void EnsureData()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(Replies);
        Directory.CreateDirectory(Logs);
    }
}
