using System.Text.Json;

namespace AIIsland.Core;

/// <summary>
/// Approximate prices in USD per million tokens. Used only for display; records that
/// already carry an amount (OpenCode, relays) keep their own value.
/// </summary>
public sealed class Pricing
{
    private static readonly Dictionary<string, (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)> Defaults =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus"] = (15m, 75m, 1.5m, 18.75m),
            ["claude-sonnet"] = (3m, 15m, 0.3m, 3.75m),
            ["claude-haiku"] = (0.8m, 4m, 0.08m, 1m),
            ["gpt-5"] = (1.25m, 10m, 0.125m, 0m),
            ["gpt-5-codex"] = (1.25m, 10m, 0.125m, 0m),
            ["gpt-5.1"] = (1.25m, 10m, 0.125m, 0m),
            ["o3"] = (2m, 8m, 0.5m, 0m),
            ["o4-mini"] = (1.1m, 4.4m, 0.275m, 0m),
            ["gemini-2.5-pro"] = (1.25m, 10m, 0.31m, 0m),
            ["gemini-2.5-flash"] = (0.3m, 2.5m, 0.075m, 0m),
            ["glm-5"] = (0.8m, 2.56m, 0.16m, 0m),
            ["kimi-k2"] = (0.6m, 2.5m, 0.15m, 0m),
            ["deepseek-chat"] = (0.27m, 1.1m, 0.07m, 0m),
            ["deepseek-reasoner"] = (0.55m, 2.19m, 0.14m, 0m),
        };

    private readonly Dictionary<string, (decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite)> table;

    private Pricing(Dictionary<string, (decimal, decimal, decimal, decimal)> table) => this.table = table;

    public static Pricing Load(string? overridePath)
    {
        var table = new Dictionary<string, (decimal, decimal, decimal, decimal)>(Defaults, StringComparer.OrdinalIgnoreCase);
        try
        {
            if (overridePath is { Length: > 0 } && File.Exists(overridePath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(overridePath));
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    var input = Number(entry.Value, "input");
                    var output = Number(entry.Value, "output");
                    if (input is null || output is null) continue;
                    table[entry.Name] = (input.Value, output.Value, Number(entry.Value, "cacheRead") ?? 0, Number(entry.Value, "cacheWrite") ?? 0);
                }
            }
        }
        catch { /* A broken price file must not break usage tracking. */ }
        return new Pricing(table);
    }

    private static decimal? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : null;

    public decimal? Estimate(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        foreach (var (prefix, price) in table)
        {
            if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            return (input * price.Input + output * price.Output + cacheRead * price.CacheRead + cacheWrite * price.CacheWrite) / 1_000_000m;
        }
        return null;
    }
}
