using System.Globalization;
using System.Net;
using System.Text.Json;

namespace AIIsland.Core.Providers;

/// <summary>
/// DeepSeek official account balance. Only an API key is needed; the endpoint returns
/// the wallet in the account currency plus granted / topped-up breakdowns.
/// </summary>
public sealed class DeepSeekSource : IQuotaSource
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(context.Key)) return QuotaReading.Unavailable("请填写 DeepSeek API 密钥");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + context.Key.Trim());
            using var response = await Http.Client.SendAsync(request, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return QuotaReading.Failure("密钥无效或已过期");
            if (!response.IsSuccessStatusCode)
                return QuotaReading.Failure("DeepSeek 返回 HTTP " + (int)response.StatusCode);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var lines = Parse(document.RootElement);
            if (lines.Count == 0) return QuotaReading.Failure("未解析到账户余额");

            var available = document.RootElement.TryGetProperty("is_available", out var flag)
                            && flag.ValueKind == JsonValueKind.True;
            return new QuotaReading
            {
                State = SourceState.Ok,
                ReadAt = DateTimeOffset.Now,
                Lines = lines,
                Status = available ? "api.deepseek.com" : "账户当前不可用",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取 DeepSeek 失败：" + e.Message); }
    }

    public static List<QuotaLine> Parse(JsonElement root)
    {
        var lines = new List<QuotaLine>();
        if (!root.TryGetProperty("balance_infos", out var infos) || infos.ValueKind != JsonValueKind.Array)
            return lines;

        foreach (var info in infos.EnumerateArray())
        {
            var currency = Numbers.Text(info, "currency") ?? "CNY";
            var symbol = currency switch
            {
                "CNY" => "¥",
                "USD" => "$",
                _ => currency + " ",
            };

            var total = Money(info, "total_balance");
            if (total is null) continue;

            lines.Add(new QuotaLine(currency == "CNY" ? "账户余额" : "账户余额 " + currency, null, symbol + Format(total.Value)));
            if (Money(info, "granted_balance") is { } granted && granted > 0)
                lines.Add(new QuotaLine("赠送余额", null, symbol + Format(granted)));
            if (Money(info, "topped_up_balance") is { } topped && topped > 0)
                lines.Add(new QuotaLine("充值余额", null, symbol + Format(topped)));
        }

        return lines;
    }

    private static decimal? Money(JsonElement info, string name)
    {
        var text = Numbers.Text(info, name);
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return value;
        return Numbers.Decimal(info, name);
    }

    private static string Format(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
