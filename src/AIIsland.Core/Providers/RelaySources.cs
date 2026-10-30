using System.Globalization;
using System.Text.Json;

namespace AIIsland.Core.Providers;

/// <summary>
/// New API style relay: uses the OpenAI-compatible billing endpoints, which work with
/// a plain site token on most forks.
/// </summary>
public sealed class NewApiSource : IQuotaSource
{
    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.Site)) return QuotaReading.Unavailable("请填写站点地址");
        if (string.IsNullOrWhiteSpace(context.Key)) return QuotaReading.Unavailable("请填写 API 密钥");

        Uri site;
        try { site = Http.RequireHttps(context.Site, context.AllowHttp); }
        catch (Exception e) { return QuotaReading.Failure(e.Message); }

        var baseUrl = Http.TrimSite(site.ToString());
        try
        {
            var subscription = await GetJson(baseUrl + "/v1/dashboard/billing/subscription", context.Key, token);
            if (subscription is null) return QuotaReading.Failure("订阅接口无响应");
            var limit = Numbers.Decimal(subscription.Value, "hard_limit_usd")
                        ?? Numbers.Decimal(subscription.Value, "soft_limit_usd")
                        ?? Numbers.Decimal(subscription.Value, "system_hard_limit_usd");
            if (limit is null) return QuotaReading.Failure("未找到额度字段，请确认站点是 New API");

            var today = DateTimeOffset.Now;
            var todayUsage = await GetUsage(baseUrl, context.Key, today.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"), token);
            var totalUsage = await GetUsage(baseUrl, context.Key, "2023-01-01", today.ToString("yyyy-MM-dd"), token);

            var now = DateTimeOffset.Now;
            var lines = new List<QuotaLine>();
            if (totalUsage is { } used)
                lines.Add(new QuotaLine("余额", null, "$" + Math.Max(0, limit.Value - used).ToString("0.##"), "USD"));
            if (todayUsage is { } day)
                lines.Add(new QuotaLine("今日", null, "$" + day.ToString("0.##"), "已用"));
            if (totalUsage is { } all)
                lines.Add(new QuotaLine("累计", null, "$" + all.ToString("0.##"), "已用"));

            return new QuotaReading
            {
                State = SourceState.Ok,
                ReadAt = now,
                Lines = lines,
                Status = site.Host,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取中转站失败：" + e.Message); }
    }

    private static async Task<JsonElement?> GetJson(string url, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        using var response = await Http.Client.SendAsync(request, token);
        if (!response.IsSuccessStatusCode) return null;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }

    private static async Task<decimal?> GetUsage(string baseUrl, string key, string start, string end, CancellationToken token)
    {
        var url = $"{baseUrl}/v1/dashboard/billing/usage?start_date={start}&end_date={end}";
        var json = await GetJson(url, key, token);
        if (json is null) return null;
        var cents = Numbers.Decimal(json.Value, "total_usage");
        return cents / 100m;
    }
}

/// <summary>
/// Sub2API relay. Wallet and subscription responses differ between forks, so the
/// reader probes common endpoints and only shows fields it actually found.
/// </summary>
public sealed class Sub2ApiSource : IQuotaSource
{
    private static readonly string[] Endpoints =
    {
        "/api/v1/usage",
        "/api/v1/auth/me",
        "/api/v1/user/self",
        "/api/v1/dashboard/stats",
    };

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.Site)) return QuotaReading.Unavailable("请填写站点地址");
        if (string.IsNullOrWhiteSpace(context.Key)) return QuotaReading.Unavailable("请填写 API 密钥");

        Uri site;
        try { site = Http.RequireHttps(context.Site, context.AllowHttp); }
        catch (Exception e) { return QuotaReading.Failure(e.Message); }
        var baseUrl = Http.TrimSite(site.ToString());

        foreach (var endpoint in Endpoints)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + endpoint);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + context.Key);
                using var response = await Http.Client.SendAsync(request, token);
                if (!response.IsSuccessStatusCode) continue;
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var lines = Parse(document.RootElement);
                if (lines.Count == 0) continue;
                return new QuotaReading
                {
                    State = SourceState.Ok,
                    ReadAt = DateTimeOffset.Now,
                    Lines = lines,
                    Status = site.Host + endpoint,
                };
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { }
        }

        return QuotaReading.Failure("未识别到 Sub2API 的额度字段，可改用自定义脚本");
    }

    private static List<QuotaLine> Parse(JsonElement root)
    {
        var lines = new List<QuotaLine>();
        var data = root;
        foreach (var wrapper in new[] { "data", "usage", "quota", "wallet", "subscription" })
            if (data.TryGetProperty(wrapper, out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                data = inner;
                break;
            }

        AddMoney(lines, data, "balance", "余额");
        AddMoney(lines, data, "remaining", "余额");
        AddMoney(lines, data, "total_balance", "余额");
        AddMoney(lines, data, "today_cost", "今日");
        AddMoney(lines, data, "today_usage", "今日");
        AddMoney(lines, data, "used", "已用");

        foreach (var (name, title) in new[] { ("daily_quota", "每日"), ("weekly_quota", "每周"), ("monthly_quota", "每月") })
        {
            if (!data.TryGetProperty(name, out var quota) || quota.ValueKind != JsonValueKind.Object) continue;
            var used = Numbers.Double(quota, "used");
            var limit = Numbers.Double(quota, "limit") ?? Numbers.Double(quota, "total");
            if (used is null || limit is null || limit <= 0) continue;
            lines.Add(new QuotaLine(title, Math.Clamp(used.Value / limit.Value * 100, 0, 100),
                used.Value.ToString("0.##"), "/ " + limit.Value.ToString("0.##")));
        }
        return lines;
    }

    private static void AddMoney(List<QuotaLine> lines, JsonElement data, string name, string title)
    {
        if (!data.TryGetProperty(name, out var value)) return;
        if (value.ValueKind == JsonValueKind.Number)
            lines.Add(new QuotaLine(title, null, value.GetDecimal().ToString("0.##", CultureInfo.InvariantCulture), "USD"));
        else if (value.ValueKind == JsonValueKind.Object && Numbers.Decimal(value, "amount") is { } amount)
            lines.Add(new QuotaLine(title, null, amount.ToString("0.##", CultureInfo.InvariantCulture), "USD"));
    }
}
