using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AIIsland.Core.Providers;

/// <summary>
/// Cash-only balance sources. Each one needs nothing but an API key, so a column can show
/// the wallet without a site address. Parsing is deliberately tolerant of number/string
/// encodings because these endpoints disagree on the shape.
/// </summary>
internal static class Cash
{
    public static decimal? Value(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number) return value.GetDecimal();
        if (value.ValueKind != JsonValueKind.String) return null;
        return decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    public static string Symbol(string currency) => currency switch
    {
        "CNY" => "¥",
        "USD" => "$",
        _ => currency + " ",
    };

    public static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>GET a JSON document with a bearer key. Throws with a readable message on failure.</summary>
    public static async Task<JsonElement> GetAsync(string url, string key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await Http.Client.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("密钥无效或已过期");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"返回 HTTP {(int)response.StatusCode}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }

    public static JsonElement Unwrap(JsonElement root) =>
        root.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
}

/// <summary>OpenRouter credits: purchased credits minus usage, plus the key's own limit.</summary>
public sealed class OpenRouterSource : IQuotaSource
{
    private const string CreditsUrl = "https://openrouter.ai/api/v1/credits";
    private const string KeyUrl = "https://openrouter.ai/api/v1/key";

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.Key)) return QuotaReading.Unavailable("请填写 OpenRouter API 密钥");

        try
        {
            var root = await Cash.GetAsync(CreditsUrl, context.Key, token);
            var lines = Parse(root);
            if (lines.Count == 0) return QuotaReading.Failure("未解析到账户余额");

            if (await TryKeyAsync(context.Key, token) is { } info && Numbers.Decimal(info, "limit") is { } limit)
            {
                var remaining = Numbers.Decimal(info, "limit_remaining")
                                ?? Math.Max(0, limit - (Numbers.Decimal(info, "usage") ?? 0));
                lines.Insert(1, new QuotaLine("密钥限额", null, Cash.Symbol("USD") + Cash.Format(remaining), "剩余"));
                if (Numbers.Text(info, "label") is { Length: > 0 } label) lines.Add(new QuotaLine("密钥", null, label));
            }

            return new QuotaReading { State = SourceState.Ok, Lines = lines, Status = "openrouter.ai" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取 OpenRouter 失败：" + e.Message); }
    }

    public static List<QuotaLine> Parse(JsonElement root)
    {
        var lines = new List<QuotaLine>();
        var data = Cash.Unwrap(root);
        var total = Cash.Value(data, "total_credits");
        var used = Cash.Value(data, "total_usage");
        if (total is not null)
            lines.Add(new QuotaLine("账户余额", null, Cash.Symbol("USD") + Cash.Format(Math.Max(0, total.Value - (used ?? 0)))));
        if (used is not null)
            lines.Add(new QuotaLine("累计", null, Cash.Symbol("USD") + Cash.Format(used.Value), "已用"));
        return lines;
    }

    private static async Task<JsonElement?> TryKeyAsync(string key, CancellationToken token)
    {
        try { return Cash.Unwrap(await Cash.GetAsync(KeyUrl, key, token)); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}

/// <summary>Moonshot (Kimi) open platform wallet in CNY.</summary>
public sealed class KimiSource : IQuotaSource
{
    private const string BalanceUrl = "https://api.moonshot.cn/v1/users/me/balance";

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.Key)) return QuotaReading.Unavailable("请填写 Kimi API 密钥");

        try
        {
            var root = await Cash.GetAsync(BalanceUrl, context.Key, token);
            var lines = Parse(root);
            if (lines.Count == 0) return QuotaReading.Failure("未解析到账户余额");

            var available = root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.True;
            return new QuotaReading
            {
                State = SourceState.Ok,
                Lines = lines,
                Status = available ? "platform.moonshot.cn" : "账户当前不可用",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取 Kimi 失败：" + e.Message); }
    }

    public static List<QuotaLine> Parse(JsonElement root)
    {
        var lines = new List<QuotaLine>();
        var data = Cash.Unwrap(root);
        if (Cash.Value(data, "available_balance") is { } available)
            lines.Add(new QuotaLine("账户余额", null, Cash.Symbol("CNY") + Cash.Format(available)));
        if (Cash.Value(data, "voucher_balance") is { } voucher && voucher > 0)
            lines.Add(new QuotaLine("赠送余额", null, Cash.Symbol("CNY") + Cash.Format(voucher)));
        if (Cash.Value(data, "cash_balance") is { } cash && cash > 0)
            lines.Add(new QuotaLine("现金余额", null, Cash.Symbol("CNY") + Cash.Format(cash)));
        return lines;
    }
}

/// <summary>SiliconFlow user wallet in CNY.</summary>
public sealed class SiliconFlowSource : IQuotaSource
{
    private const string InfoUrl = "https://api.siliconflow.cn/v1/user/info";

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.Key)) return QuotaReading.Unavailable("请填写硅基流动 API 密钥");

        try
        {
            var root = await Cash.GetAsync(InfoUrl, context.Key, token);
            var lines = Parse(root);
            if (lines.Count == 0) return QuotaReading.Failure("未解析到账户余额");

            return new QuotaReading { State = SourceState.Ok, Lines = lines, Status = "siliconflow.cn" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取硅基流动失败：" + e.Message); }
    }

    public static List<QuotaLine> Parse(JsonElement root)
    {
        var lines = new List<QuotaLine>();
        var data = Cash.Unwrap(root);
        var total = Cash.Value(data, "totalBalance") ?? Cash.Value(data, "total_balance") ?? Cash.Value(data, "balance");
        if (total is not null)
            lines.Add(new QuotaLine("账户余额", null, Cash.Symbol("CNY") + Cash.Format(total.Value)));
        if ((Cash.Value(data, "chargeBalance") ?? Cash.Value(data, "charge_balance")) is { } charge && charge > 0)
            lines.Add(new QuotaLine("充值余额", null, Cash.Symbol("CNY") + Cash.Format(charge)));
        return lines;
    }
}
