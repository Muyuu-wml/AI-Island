using System.Globalization;
using System.Text.Json;
using System.Security.Cryptography;

namespace AIIsland.Core;

public sealed record ColumnSnapshot(
    ColumnSettings Settings,
    QuotaReading? Quota,
    UsageTotals Today,
    UsageTotals Week,
    IReadOnlyList<UsageRecord> Records,
    string? Error);

/// <summary>Key identifies the column and alert kind, so a changing percentage doesn't re-notify.</summary>
public sealed record UsageAlert(string Key, string Message);

public sealed record ColumnBatch(IReadOnlyList<ColumnSnapshot> Snapshots, IReadOnlyList<UsageAlert> Alerts);

/// <summary>
/// Reads each column's quota and usage independently. A failed read keeps the last
/// successful quota value and marks it stale instead of showing made-up numbers.
/// </summary>
public sealed class ColumnService
{
    private static readonly System.Text.Json.JsonSerializerOptions CacheOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly Dictionary<string, CachedQuota> quotaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedUsage> usageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private readonly SemaphoreSlim usageGate = new(1, 1);
    private readonly string cacheFile;

    private sealed record CachedQuota(QuotaReading Reading, DateTimeOffset At, string Identity);
    private sealed record CachedUsage(IReadOnlyList<UsageRecord> Records, DateTimeOffset At);

    public ColumnService(string? directory = null)
    {
        cacheFile = Path.Combine(directory ?? AppPaths.Data, "quota-cache.json");
        try
        {
            if (!File.Exists(cacheFile)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, CachedQuota>>(
                File.ReadAllText(cacheFile), CacheOptions);
            if (saved is null) return;
            foreach (var (id, cached) in saved)
                if (cached?.Reading is not null && !string.IsNullOrEmpty(cached.Identity)) quotaCache[id] = cached;
        }
        catch { }
    }

    public Task<ColumnSnapshot> ReadAsync(ColumnSettings settings, SecretStore secrets, CancellationToken token)
    {
        settings = new ColumnSettings
        {
            Id = settings.Id, Name = settings.Name, QuotaSource = settings.QuotaSource, UsageSource = settings.UsageSource,
            Site = settings.Site, AllowHttp = settings.AllowHttp, Accent = settings.Accent,
        };
        var context = new SourceContext(settings.Site, secrets.Get("column:" + settings.Id), settings.AllowHttp);
        // Local JSONL/SQLite readers and totals are synchronous; keep the whole read off the dispatcher.
        return Task.Run(async () =>
        {
            var quotaTask = ReadQuotaAsync(settings, context, token);
            var usageTask = ReadUsageAsync(settings, context, token);
            await Task.WhenAll(quotaTask, usageTask);

            var quota = quotaTask.Result;
            var records = usageTask.Result;
            var week = DateTimeOffset.Now.AddDays(-7);
            return new ColumnSnapshot(
                settings,
                quota,
                UsageTotals.From(records.Where(r => r.At >= DateTimeOffset.Now.Date)),
                UsageTotals.From(records.Where(r => r.At >= week)),
                records,
                quota?.Error);
        }, token);
    }

    public async Task<ColumnBatch> ReadColumnsAsync(IEnumerable<ColumnSettings> columns, SecretStore secrets, AlertSettings alerts, CancellationToken token)
    {
        var tasks = columns.Select(col => ReadAsync(col, secrets, token)).ToArray();
        await Task.WhenAll(tasks);
        var snapshots = tasks.Select(t => t.Result).ToArray();
        var warnings = new List<UsageAlert>();

        foreach (var snapshot in snapshots)
            warnings.AddRange(CheckAlerts(snapshot, alerts));

        return new ColumnBatch(snapshots, warnings);
    }

    public static IEnumerable<UsageAlert> CheckAlerts(ColumnSnapshot snapshot, AlertSettings alerts)
    {
        var quota = snapshot.Quota;
        if (quota?.State != SourceState.Ok) yield break;

        var settings = snapshot.Settings;
        var name = string.IsNullOrWhiteSpace(settings.Name)
            ? SourceCatalog.Find(settings.QuotaSource)?.Name ?? settings.QuotaSource
            : settings.Name;

        if (alerts.QuotaThreshold > 0 && quota.Peak is { } peak && peak >= alerts.QuotaThreshold)
            yield return new UsageAlert(settings.Id + ":quota", $"{name} 额度已用 {peak:0}%");

        if (alerts.BalanceThreshold <= 0) yield break;
        // Balance components (granted/topped-up) are not the available account total.
        foreach (var line in quota.Lines)
        {
            if (line.Percent is not null || line.Value is null || line.Title is not ("余额" or "账户余额")) continue;
            var text = line.Value.TrimStart('¥', '$', ' ');
            if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance)
                && balance < alerts.BalanceThreshold)
            {
                yield return new UsageAlert(settings.Id + ":balance", $"{name} {line.Title}仅剩 {line.Value}");
                yield break;
            }
        }
    }

    private async Task<QuotaReading?> ReadQuotaAsync(ColumnSettings settings, SourceContext context, CancellationToken token)
    {
        var source = SourceCatalog.CreateQuota(settings.QuotaSource);
        if (source is null) return QuotaReading.Unavailable("未选择额度来源");
        var identity = QuotaIdentity(settings, context);

        QuotaReading reading;
        try
        {
            reading = await source.ReadAsync(context, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            reading = QuotaReading.Failure(e.Message);
        }

        return UseQuota(settings.Id, identity, reading);
    }

    internal QuotaReading UseQuota(string id, string identity, QuotaReading reading)
    {
        lock (gate)
        {
            if (quotaCache.TryGetValue(id, out var old) && old.Identity != identity)
            {
                quotaCache.Remove(id);
                SaveCache();
            }
            if (reading.State == SourceState.Ok)
            {
                quotaCache[id] = new CachedQuota(reading, reading.ReadAt, identity);
                SaveCache();
            }
            else if (quotaCache.TryGetValue(id, out var cached))
            {
                return cached.Reading with
                {
                    State = SourceState.Stale,
                    Error = reading.Error ?? reading.Status,
                    ReadAt = cached.At,
                };
            }
        }
        return reading;
    }

    internal static string QuotaIdentity(ColumnSettings settings, SourceContext context)
    {
        var authPath = settings.QuotaSource switch
        {
            SourceCatalog.Claude => AppPaths.ClaudeCredentials,
            SourceCatalog.Codex => AppPaths.CodexAuth,
            SourceCatalog.OpenCode => AppPaths.OpenCodeAuth,
            _ => null,
        };
        string? auth = null;
        if (authPath is not null && string.IsNullOrEmpty(context.Key))
        {
            try { auth = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(authPath))); }
            catch { auth = "unavailable"; }
        }
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            settings.QuotaSource, context.Site, context.Key, context.AllowHttp, auth,
        })));
    }

    private async Task<IReadOnlyList<UsageRecord>> ReadUsageAsync(ColumnSettings settings, SourceContext context, CancellationToken token)
    {
        var source = SourceCatalog.CreateUsage(settings.UsageSource);
        if (source is null) return Array.Empty<UsageRecord>();

        await usageGate.WaitAsync(token);
        try
        {
            lock (gate)
            {
                if (usageCache.TryGetValue(settings.UsageSource, out var cached) && DateTimeOffset.Now - cached.At < TimeSpan.FromSeconds(20))
                    return cached.Records;
            }
            var records = await source.ReadAsync(context, DateTimeOffset.Now.AddDays(-8), token);
            lock (gate) usageCache[settings.UsageSource] = new CachedUsage(records, DateTimeOffset.Now);
            return records;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            lock (gate)
                return usageCache.TryGetValue(settings.UsageSource, out var cached) ? cached.Records : Array.Empty<UsageRecord>();
        }
        finally { usageGate.Release(); }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            Dictionary<string, CachedQuota> snapshot;
            lock (gate)
                snapshot = new Dictionary<string, CachedQuota>(quotaCache);
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot, CacheOptions);
            var temporary = cacheFile + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, cacheFile, true);
        }
        catch { }
    }
}
