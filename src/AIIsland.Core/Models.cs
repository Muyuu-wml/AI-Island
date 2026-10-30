namespace AIIsland.Core;

public enum SourceState
{
    Ok,
    Stale,
    Unavailable,
    Error,
}

/// <summary>One meter inside a quota column, e.g. "5 小时" or "本周".</summary>
public sealed record QuotaLine(
    string Title,
    double? Percent = null,
    string? Value = null,
    string? Suffix = null,
    string? Note = null,
    string? Reset = null);

/// <summary>Result of reading one quota source. Never invents numbers when data is missing.</summary>
public sealed record QuotaReading
{
    public SourceState State { get; init; } = SourceState.Unavailable;
    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<QuotaLine> Lines { get; init; } = Array.Empty<QuotaLine>();
    public string? Plan { get; init; }
    public string? Status { get; init; }
    public string? Error { get; init; }

    public double? Peak
    {
        get
        {
            double? peak = null;
            foreach (var line in Lines)
                if (line.Percent is { } value && (peak is null || value > peak))
                    peak = value;
            return peak;
        }
    }

    public static QuotaReading Unavailable(string status) => new()
    {
        State = SourceState.Unavailable,
        ReadAt = DateTimeOffset.Now,
        Status = status,
    };

    public static QuotaReading Failure(string error) => new()
    {
        State = SourceState.Error,
        ReadAt = DateTimeOffset.Now,
        Error = error,
    };
}

public sealed record UsageRecord(
    DateTimeOffset At,
    string Model,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    decimal? Cost);

public readonly record struct UsageTotals(
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    decimal Cost,
    int Requests,
    bool CostEstimated)
{
    public long TotalTokens => Input + Output + CacheRead + CacheWrite;

    public static UsageTotals From(IEnumerable<UsageRecord> records)
    {
        long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
        decimal cost = 0;
        var requests = 0;
        var estimated = false;
        foreach (var record in records)
        {
            input += record.Input;
            output += record.Output;
            cacheRead += record.CacheRead;
            cacheWrite += record.CacheWrite;
            if (record.Cost is { } value)
            {
                cost += value;
                estimated = true;
            }
            requests++;
        }
        return new UsageTotals(input, output, cacheRead, cacheWrite, cost, requests, estimated);
    }
}

public sealed record DailyUsage(DateOnly Date, long Tokens, decimal Cost)
{
    /// <summary>One entry per day for the last <paramref name="days"/> days ending today, oldest first; empty days are zero.</summary>
    public static IReadOnlyList<DailyUsage> From(IEnumerable<UsageRecord> records, DateOnly today, int days = 7)
    {
        var first = today.AddDays(1 - days);
        var totals = new Dictionary<DateOnly, (long Tokens, decimal Cost)>();
        foreach (var record in records)
        {
            var date = DateOnly.FromDateTime(record.At.LocalDateTime);
            if (date < first || date > today) continue;
            totals.TryGetValue(date, out var sum);
            totals[date] = (sum.Tokens + record.Input + record.Output + record.CacheRead + record.CacheWrite, sum.Cost + (record.Cost ?? 0));
        }
        return Enumerable.Range(0, days)
            .Select(offset => first.AddDays(offset))
            .Select(date => totals.TryGetValue(date, out var sum) ? new DailyUsage(date, sum.Tokens, sum.Cost) : new DailyUsage(date, 0, 0))
            .ToArray();
    }
}

public static class ResetText
{
    public static string? From(DateTimeOffset? resetAt, DateTimeOffset now)
    {
        if (resetAt is not { } at) return null;
        var span = at - now;
        if (span <= TimeSpan.Zero) return "即将重置";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} 天 {span.Hours} 时后";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} 时 {span.Minutes} 分后";
        return $"{Math.Max(1, span.Minutes)} 分后";
    }
}
