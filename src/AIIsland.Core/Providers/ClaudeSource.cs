using System.Text.Json;

namespace AIIsland.Core.Providers;

/// <summary>
/// Claude Code: usage comes from ~/.claude/projects/**/*.jsonl, quota from the same
/// OAuth endpoint `/usage` uses. The token is only held in memory per request.
/// </summary>
public sealed class ClaudeSource : IQuotaSource, IUsageSource
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        string? accessToken;
        try { accessToken = ReadAccessToken(); }
        catch (Exception e) { return QuotaReading.Unavailable("读取 Claude 登录失败：" + e.Message); }
        if (string.IsNullOrWhiteSpace(accessToken))
            return QuotaReading.Unavailable("未在本机登录 Claude Code");

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.TryAddWithoutValidation("User-Agent", "claude-code/2.1.32");
        try
        {
            using var response = await Http.Client.SendAsync(request, token);
            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return QuotaReading.Failure("请求过于频繁，稍后自动重试");
            if (!response.IsSuccessStatusCode)
                return QuotaReading.Failure($"Claude 额度接口返回 HTTP {(int)response.StatusCode}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            var now = DateTimeOffset.Now;
            var lines = new List<QuotaLine>();
            AddWindow(root, "five_hour", "5 小时", now, lines);
            AddWindow(root, "seven_day", "7 天", now, lines);
            AddWindow(root, "seven_day_sonnet", "7 天 Sonnet", now, lines);
            AddWindow(root, "seven_day_opus", "7 天 Opus", now, lines);

            if (lines.Count == 0)
                return QuotaReading.Failure("Claude 额度接口未返回可用窗口");

            return new QuotaReading
            {
                State = SourceState.Ok,
                ReadAt = now,
                Lines = lines,
                Status = "Claude Code 登录",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取 Claude 额度失败：" + e.Message); }
    }

    private static void AddWindow(JsonElement root, string name, string title, DateTimeOffset now, List<QuotaLine> lines)
    {
        if (!root.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return;
        var percent = Numbers.Double(window, "utilization");
        if (percent is null) return;
        var reset = Numbers.Text(window, "resets_at");
        DateTimeOffset? resetAt = DateTimeOffset.TryParse(reset, out var parsed) ? parsed : null;
        lines.Add(new QuotaLine(
            title,
            Math.Clamp(percent.Value, 0, 100),
            percent.Value.ToString("0.#") + "%",
            "已用",
            Reset: ResetText.From(resetAt, now)));
    }

    private static string? ReadAccessToken()
    {
        if (!File.Exists(AppPaths.ClaudeCredentials)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.ClaudeCredentials));
        if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
        return Numbers.Text(oauth, "accessToken");
    }

    public Task<IReadOnlyList<UsageRecord>> ReadAsync(SourceContext context, DateTimeOffset since, CancellationToken token)
    {
        var pricing = Pricing.Load(AppPaths.PricesFile);
        var records = new List<UsageRecord>();
        if (!Directory.Exists(AppPaths.ClaudeProjects)) return Task.FromResult<IReadOnlyList<UsageRecord>>(records);

        foreach (var file in Directory.EnumerateFiles(AppPaths.ClaudeProjects, "*.jsonl", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime.AddDays(-1)) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (line.Length < 40 || !line.Contains("\"usage\"", StringComparison.Ordinal)) continue;
                    if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (Numbers.Text(root, "type") != "assistant") continue;
                    if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                    var at = ParseTime(Numbers.Text(root, "timestamp"));
                    if (at is null || at < since) continue;

                    var input = (long)(Numbers.Double(usage, "input_tokens") ?? 0);
                    var output = (long)(Numbers.Double(usage, "output_tokens") ?? 0);
                    var cacheWrite = (long)(Numbers.Double(usage, "cache_creation_input_tokens") ?? 0);
                    var cacheRead = (long)(Numbers.Double(usage, "cache_read_input_tokens") ?? 0);
                    var model = Numbers.Text(message, "model") ?? "";
                    records.Add(new UsageRecord(at.Value, model, input, output, cacheRead, cacheWrite,
                        pricing.Estimate(model, input, output, cacheRead, cacheWrite)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return Task.FromResult<IReadOnlyList<UsageRecord>>(records);
    }

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, out var value) ? value : null;
}
