using System.Text.Json;

namespace AIIsland.Core.Providers;

/// <summary>
/// Codex: usage from rollout JSONL under ~/.codex/sessions, quota from the same
/// ChatGPT usage backend the CLI calls. Tokens stay in memory.
/// </summary>
public sealed class CodexSource : IQuotaSource, IUsageSource
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        string? accessToken, accountId;
        try { (accessToken, accountId) = ReadAuth(); }
        catch (Exception e) { return QuotaReading.Unavailable("读取 Codex 登录失败：" + e.Message); }
        if (string.IsNullOrWhiteSpace(accessToken))
            return QuotaReading.Unavailable("未在本机登录 Codex");

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
        if (!string.IsNullOrWhiteSpace(accountId))
            request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
        try
        {
            using var response = await Http.Client.SendAsync(request, token);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return QuotaReading.Failure("Codex 登录已失效，请重新登录");
            if (!response.IsSuccessStatusCode)
                return QuotaReading.Failure($"Codex 额度接口返回 HTTP {(int)response.StatusCode}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var root = document.RootElement;
            var now = DateTimeOffset.Now;
            var lines = new List<QuotaLine>();
            var plan = Numbers.Text(root, "plan_type");

            if (root.TryGetProperty("rate_limit", out var rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
            {
                AddWindow(rateLimit, "primary_window", "5 小时", now, lines);
                AddWindow(rateLimit, "secondary_window", "7 天", now, lines);
            }
            if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            {
                var balance = Numbers.Double(credits, "balance");
                if (balance is { } value)
                    lines.Add(new QuotaLine("点数余额", null, value.ToString("0.##"), "点"));
            }

            if (lines.Count == 0)
                return QuotaReading.Failure("Codex 额度接口未返回可用窗口");
            return new QuotaReading
            {
                State = SourceState.Ok,
                ReadAt = now,
                Lines = lines,
                Plan = plan,
                Status = "Codex 登录",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取 Codex 额度失败：" + e.Message); }
    }

    private static void AddWindow(JsonElement rateLimit, string name, string title, DateTimeOffset now, List<QuotaLine> lines)
    {
        if (!rateLimit.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return;
        var percent = Numbers.Double(window, "used_percent");
        if (percent is null) return;
        DateTimeOffset? resetAt = null;
        if (window.TryGetProperty("reset_at", out var reset) && reset.ValueKind == JsonValueKind.Number)
            resetAt = DateTimeOffset.FromUnixTimeSeconds(reset.GetInt64());
        else if (Numbers.Text(window, "reset_at") is { } text && DateTimeOffset.TryParse(text, out var parsed))
            resetAt = parsed;
        lines.Add(new QuotaLine(
            title,
            Math.Clamp(percent.Value, 0, 100),
            percent.Value.ToString("0.#") + "%",
            "已用",
            Reset: ResetText.From(resetAt, now)));
    }

    private static (string? AccessToken, string? AccountId) ReadAuth()
    {
        if (!File.Exists(AppPaths.CodexAuth)) return (null, null);
        using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.CodexAuth));
        var root = document.RootElement;
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return (null, null);
        return (Numbers.Text(tokens, "access_token"), Numbers.Text(tokens, "account_id"));
    }

    public Task<IReadOnlyList<UsageRecord>> ReadAsync(SourceContext context, DateTimeOffset since, CancellationToken token)
    {
        var pricing = Pricing.Load(AppPaths.PricesFile);
        var records = new List<UsageRecord>();
        if (!Directory.Exists(AppPaths.CodexSessions)) return Task.FromResult<IReadOnlyList<UsageRecord>>(records);

        foreach (var file in Directory.EnumerateFiles(AppPaths.CodexSessions, "*.jsonl", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime.AddDays(-1)) continue;
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                records.AddRange(ParseUsage(Lines(reader), since, pricing, token));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return Task.FromResult<IReadOnlyList<UsageRecord>>(records);
    }

    private static IEnumerable<string> Lines(StreamReader reader)
    {
        while (reader.ReadLine() is { } line) yield return line;
    }

    internal static IReadOnlyList<UsageRecord> ParseUsage(IEnumerable<string> lines, DateTimeOffset since, Pricing pricing, CancellationToken token)
    {
        var records = new List<UsageRecord>();
        var model = "";
        (long Input, long Output, long Cache) previous = default;
        var seenLast = new HashSet<string>();
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            if (!line.Contains("\"token_count\"") && !line.Contains("\"turn_context\"")) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                if (Numbers.Text(root, "type") == "turn_context" || Numbers.Text(payload, "type") == "turn_context")
                {
                    model = Numbers.Text(payload, "model") ?? model;
                    continue;
                }
                if (Numbers.Text(payload, "type") != "token_count" ||
                    !payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) continue;

                (long Input, long Output, long Cache) delta;
                if (info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
                {
                    var current = Tokens(total);
                    var reset = current.Input < previous.Input || current.Output < previous.Output;
                    delta = reset ? current : (current.Input - previous.Input, current.Output - previous.Output, Math.Max(0, current.Cache - previous.Cache));
                    previous = current;
                }
                else if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                {
                    if (!seenLast.Add(Numbers.Text(root, "timestamp") + "|" + last.GetRawText())) continue;
                    delta = Tokens(last);
                    previous = (previous.Input + delta.Input, previous.Output + delta.Output, previous.Cache + delta.Cache);
                }
                else continue;

                // Advance the cumulative baseline even for entries outside the requested window.
                var at = ParseTime(Numbers.Text(root, "timestamp"));
                if (at is null || at < since || (delta.Input == 0 && delta.Output == 0)) continue;
                var cacheRead = Math.Min(delta.Input, delta.Cache);
                var input = delta.Input - cacheRead;
                records.Add(new UsageRecord(at.Value, model, input, delta.Output, cacheRead, 0,
                    pricing.Estimate(model, input, delta.Output, cacheRead, 0)));
            }
            catch (JsonException) { }
        }
        return records;
    }

    private static (long Input, long Output, long Cache) Tokens(JsonElement usage) => (
        Math.Max(0, (long)(Numbers.Double(usage, "input_tokens") ?? 0)),
        Math.Max(0, (long)(Numbers.Double(usage, "output_tokens") ?? 0)),
        Math.Max(0, (long)(Numbers.Double(usage, "cached_input_tokens") ?? 0)));

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, out var value) ? value : null;
}
