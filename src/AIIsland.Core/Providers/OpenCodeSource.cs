using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AIIsland.Core.Providers;

/// <summary>
/// OpenCode CLI and desktop share %USERPROFILE%\.local\share\opencode. Usage is read
/// from opencode.db; quota comes from the OpenCode Go usage endpoint.
/// </summary>
public sealed class OpenCodeSource : IQuotaSource, IUsageSource
{
    private const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";

    public async Task<QuotaReading> ReadAsync(SourceContext context, CancellationToken token)
    {
        var key = context.Key;
        if (string.IsNullOrWhiteSpace(key)) key = ReadLocalKey();
        if (string.IsNullOrWhiteSpace(key))
            return QuotaReading.Unavailable("未在本机登录 OpenCode Go，可在设置里填入 API 密钥");

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        try
        {
            using var response = await Http.Client.SendAsync(request, token);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return QuotaReading.Failure("OpenCode 密钥无效或已过期");
            if (!response.IsSuccessStatusCode)
                return QuotaReading.Failure($"OpenCode 额度接口返回 HTTP {(int)response.StatusCode}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!document.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return QuotaReading.Failure("OpenCode 额度接口未返回用量");

            var now = DateTimeOffset.Now;
            var lines = new List<QuotaLine>();
            AddWindow(usage, "rolling", "5 小时", now, lines);
            AddWindow(usage, "weekly", "本周", now, lines);
            AddWindow(usage, "monthly", "本月", now, lines);
            if (lines.Count == 0) return QuotaReading.Failure("OpenCode 额度接口未返回可用窗口");

            return new QuotaReading
            {
                State = SourceState.Ok,
                ReadAt = now,
                Lines = lines,
                Status = "OpenCode Go",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return QuotaReading.Failure("读取 OpenCode 额度失败：" + e.Message); }
    }

    private static void AddWindow(JsonElement usage, string name, string title, DateTimeOffset now, List<QuotaLine> lines)
    {
        if (!usage.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object) return;
        var percent = Numbers.Double(window, "percent");
        if (percent is null) return;
        var status = Numbers.Text(window, "status");
        if (status is "rate-limited")
            percent = 100;
        var resetText = Numbers.Text(window, "resetsAt");
        DateTimeOffset? resetAt = DateTimeOffset.TryParse(resetText, out var parsed) ? parsed : null;
        lines.Add(new QuotaLine(
            title,
            Math.Clamp(percent.Value, 0, 100),
            percent.Value.ToString("0.#") + "%",
            "已用",
            Note: status is "ok" or null ? null : status,
            Reset: ResetText.From(resetAt, now)));
    }

    private static string? ReadLocalKey()
    {
        foreach (var name in new[] { "OPENCODE_GO_API_KEY", "OPENCODE_API_KEY", "OPENCODE_ZEN_API_KEY" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value;

        try
        {
            if (!File.Exists(AppPaths.OpenCodeAuth)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.OpenCodeAuth));
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!entry.Name.Contains("opencode", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (Numbers.Text(entry.Value, "key") is { Length: > 0 } key) return key;
            }
        }
        catch { }
        return null;
    }

    public Task<IReadOnlyList<UsageRecord>> ReadAsync(SourceContext context, DateTimeOffset since, CancellationToken token)
    {
        var records = new List<UsageRecord>();
        var path = AppPaths.OpenCodeDb;
        if (!File.Exists(path)) return Task.FromResult<IReadOnlyList<UsageRecord>>(records);
        var pricing = Pricing.Load(AppPaths.PricesFile);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Shared,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var table = FindMessageTable(connection);
            if (table is null) return Task.FromResult<IReadOnlyList<UsageRecord>>(records);
            var columns = Columns(connection, table);
            var dataColumn = columns.FirstOrDefault(c => c.Name is "data" or "message" or "json");
            if (dataColumn.Name is null) return Task.FromResult<IReadOnlyList<UsageRecord>>(records);

            var select = $"SELECT \"{dataColumn.Name}\" FROM \"{table}\"";
            if (columns.Any(c => c.Name == "role"))
                select += " WHERE \"role\" = 'assistant'";
            select += " ORDER BY rowid DESC LIMIT 50000";

            using var command = connection.CreateCommand();
            command.CommandText = select;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (reader.IsDBNull(0)) continue;
                var json = reader.GetString(0);
                if (json.Length < 20) continue;
                try
                {
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    if (Numbers.Text(root, "role") is { } role && role != "assistant") continue;
                    if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) continue;
                    var at = ReadTime(root);
                    if (at is null || at < since) continue;

                    var input = (long)(Numbers.Double(tokens, "input") ?? 0);
                    var output = (long)(Numbers.Double(tokens, "output") ?? 0);
                    var reasoning = (long)(Numbers.Double(tokens, "reasoning") ?? 0);
                    long cacheRead = 0, cacheWrite = 0;
                    if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                    {
                        cacheRead = (long)(Numbers.Double(cache, "read") ?? 0);
                        cacheWrite = (long)(Numbers.Double(cache, "write") ?? 0);
                    }
                    if (input == 0 && output == 0 && reasoning == 0) continue;
                    var model = Numbers.Text(root, "modelID") ?? Numbers.Text(root, "model") ?? "";
                    var cost = Numbers.Decimal(root, "cost");
                    records.Add(new UsageRecord(at.Value, model, input, output + reasoning, cacheRead, cacheWrite,
                        cost ?? pricing.Estimate(model, input, output + reasoning, cacheRead, cacheWrite)));
                }
                catch (JsonException) { }
            }
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException) { }
        return Task.FromResult<IReadOnlyList<UsageRecord>>(records);
    }

    private static DateTimeOffset? ReadTime(JsonElement root)
    {
        if (root.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.Object)
        {
            var created = Numbers.Double(time, "created");
            if (created is { } value)
                return value > 10_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : DateTimeOffset.FromUnixTimeSeconds((long)value);
        }
        return null;
    }

    private static string? FindMessageTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.FirstOrDefault(n => n.Equals("message", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => n.Equals("messages", StringComparison.OrdinalIgnoreCase))
            ?? names.FirstOrDefault(n => n.Contains("message", StringComparison.OrdinalIgnoreCase));
    }

    private static List<(string Name, string Type)> Columns(SqliteConnection connection, string table)
    {
        var columns = new List<(string, string)>();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read()) columns.Add((reader.GetString(1), reader.GetString(2)));
        return columns;
    }
}
