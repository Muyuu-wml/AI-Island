using System.Globalization;

namespace AIIsland.Core;

public sealed record SourceContext(
    string? Site = null,
    string? Key = null,
    bool AllowHttp = false,
    string? Language = "zh-CN");

public sealed record SourceInfo(
    string Id,
    string Name,
    bool Quota,
    bool Usage,
    bool NeedsSite = false,
    bool NeedsKey = false,
    string? Hint = null);

public interface IQuotaSource
{
    Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token);
}

public interface IUsageSource
{
    Task<IReadOnlyList<UsageRecord>> ReadAsync(SourceContext context, DateTimeOffset since, CancellationToken token);
}

public static class SourceCatalog
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string OpenCode = "opencode";
    public const string NewApi = "newapi";
    public const string Sub2Api = "sub2api";
    public const string DeepSeek = "deepseek";

    public static IReadOnlyList<SourceInfo> All { get; } = new SourceInfo[]
    {
        new(Claude, "Claude", true, true, Hint: "读取本机 Claude Code 登录与 ~/.claude 会话记录"),
        new(Codex, "Codex", true, true, Hint: "读取本机 Codex 登录与 ~/.codex/sessions"),
        new(OpenCode, "OpenCode", true, true, Hint: "读取本机 OpenCode 登录与 opencode.db，可另填 OpenCode Go 密钥"),
        new(DeepSeek, "DeepSeek", true, false, NeedsKey: true, Hint: "DeepSeek 官方 API 密钥，读取账户余额（无需站点地址）"),
        new(NewApi, "New API 中转站", true, false, NeedsSite: true, NeedsKey: true, Hint: "站点地址 + 令牌，令牌额度与累计扣费"),
        new(Sub2Api, "Sub2API 中转站", true, false, NeedsSite: true, NeedsKey: true, Hint: "站点地址 + 令牌，钱包或订阅额度"),
    };

    public static SourceInfo? Find(string? id) =>
        All.FirstOrDefault(source => string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IQuotaSource? CreateQuota(string? id) => id switch
    {
        Claude => new Providers.ClaudeSource(),
        Codex => new Providers.CodexSource(),
        OpenCode => new Providers.OpenCodeSource(),
        DeepSeek => new Providers.DeepSeekSource(),
        NewApi => new Providers.NewApiSource(),
        Sub2Api => new Providers.Sub2ApiSource(),
        _ => null,
    };

    public static IUsageSource? CreateUsage(string? id) => id switch
    {
        Claude => new Providers.ClaudeSource(),
        Codex => new Providers.CodexSource(),
        OpenCode => new Providers.OpenCodeSource(),
        _ => null,
    };
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AIIsland/2.0");
        return client;
    }

    public static Uri RequireHttps(string site, bool allowHttp)
    {
        var text = site.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        var uri = new Uri(text);
        if (uri.Scheme == Uri.UriSchemeHttps) return uri;
        if (uri.Scheme == Uri.UriSchemeHttp && (allowHttp || uri.IsLoopback)) return uri;
        throw new InvalidOperationException("站点地址必须使用 HTTPS（本机地址可开启 HTTP）");
    }

    public static string TrimSite(string site)
    {
        var text = site.Trim().TrimEnd('/');
        if (text.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) text = text[..^3];
        return text.TrimEnd('/');
    }
}

public static class Numbers
{
    public static decimal? Decimal(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    public static double? Double(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetDouble()
            : null;

    public static string? Text(System.Text.Json.JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;
}
