using AIIsland.Core;
using AIIsland.Core.Monitoring;
using AIIsland.Core.Sessions;

if (args.Contains("--probe"))
{
    await Probe();
    return 0;
}

var failures = 0;

void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS {name}"); return; }
    failures++;
    Console.WriteLine($"FAIL {name}");
}

Check("usage totals", UsageTotals.From(new[]
{
    new UsageRecord(DateTimeOffset.Now, "m", 100, 20, 30, 5, 0.5m),
    new UsageRecord(DateTimeOffset.Now, "m", 200, 40, 0, 0, null),
}).TotalTokens == 395);

Check("daily usage buckets", DailyBuckets());
Check("quota alert fires", Alerts(new QuotaLine("5 小时", 85)).Single().Key == "c:quota");
Check("quota alert below threshold", !Alerts(new QuotaLine("5 小时", 50)).Any());
Check("balance alert fires", Alerts(new QuotaLine("账户余额", null, "¥3.20")).Single().Key == "c:balance");
Check("balance alert ignores used amounts", !Alerts(new QuotaLine("今日", null, "$1.00", "已用")).Any());
Check("balance alert above threshold", !Alerts(new QuotaLine("余额", null, "$25", "USD")).Any());

Check("pricing matches prefix", Pricing.Load(null).Estimate("claude-sonnet-4-5", 1_000_000, 0, 0, 0) == 3m);
Check("pricing unknown", Pricing.Load(null).Estimate("mystery-model", 1, 1, 1, 1) is null);

Check("claude session start", Apply("SessionStart").State == SessionState.Idle);
Check("claude prompt runs", Apply("UserPromptSubmit").State == SessionState.Running);
Check("claude permission waits", Apply("PermissionRequest").State == SessionState.Waiting);
Check("claude stop done", Apply("Stop").State == SessionState.Done);
Check("claude failure", Apply("StopFailure").State == SessionState.Failed);
Check("permission request keeps tool", PermissionPending());
Check("old inbox event still parses", System.Text.Json.JsonSerializer.Deserialize<ActivityEvent>(
    """{"Provider":"Claude","SessionId":"s","EventName":"Stop","At":"2026-10-29T00:00:00+00:00"}""") is { RequestId: null, EventName: "Stop" });
Check("permission reply file", PermissionReply());

Check("settings roundtrip", Roundtrip());
Check("secrets roundtrip", Secrets());
Check("deepseek balance", DeepSeekBalance());
Check("newapi unlimited token is not an account balance", NewApiQuota(
    """{"unlimited_quota":true,"total_available":-43595837,"total_used":43595837}""", 500000)
    .SequenceEqual(new[] { new QuotaLine("令牌额度", null, "不限额"), new QuotaLine("累计", null, "$87.19", "已用") }));
Check("newapi limited token uses site conversion", NewApiQuota(
    """{"unlimited_quota":false,"total_available":2500,"total_used":1500}""", 1000)
    .SequenceEqual(new[] { new QuotaLine("令牌剩余", null, "$2.5", "USD"), new QuotaLine("累计", null, "$1.5", "已用") }));
Check("newapi unknown units do not invent money", NewApiQuota(
    """{"unlimited_quota":true,"total_used":43595837}""", null)
    .SequenceEqual(new[] { new QuotaLine("令牌额度", null, "不限额") }));
Check("newapi zero conversion is rejected", NewApiQuota(
    """{"unlimited_quota":false,"total_available":2500,"total_used":1500}""", 0).Count == 0);
Check("newapi missing quota flag is not a zero balance", NewApiQuota("{}", 500000).Count == 0);
Check("sub2api wallet beside usage and actual charge", Sub2ApiQuota(
    """{"balance":100.5,"remaining":100.5,"unit":"USD","usage":{"today":{"cost":5,"actual_cost":2}}}""")
    .SequenceEqual(new[] { new QuotaLine("余额", null, "100.5", "USD"), new QuotaLine("今日", null, "2", "USD") }));
Check("sub2api zero balance and free usage", Sub2ApiQuota(
    """{"balance":0,"remaining":10,"usage":{"today":{"cost":4,"actual_cost":0}}}""")
    .SequenceEqual(new[] { new QuotaLine("余额", null, "0", "USD"), new QuotaLine("今日", null, "0", "USD") }));
Check("sub2api legacy wrapped wallet and quota", Sub2ApiQuota(
    """{"data":{"remaining":{"amount":25},"today_cost":1,"daily_quota":{"used":2,"limit":10}}}""")
    .SequenceEqual(new[] { new QuotaLine("余额", null, "25", "USD"), new QuotaLine("今日", null, "1", "USD"), new QuotaLine("每日", 20, "2", "/ 10") }));
Check("sub2api token counts are not money", Sub2ApiQuota(
    """{"usage":{"today":{"requests":2,"total_tokens":500}}}""").Count == 0);
Check("sub2api non-object response", Sub2ApiQuota("[]").Count == 0);
Check("palettes", Palettes.All.Select(palette => palette.Id).Distinct().Count() == Palettes.All.Count
    && Palettes.All.All(palette => new[] { palette.Accent, palette.Cpu, palette.Memory, palette.Down, palette.Up, palette.Music }
        .All(hex => System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$")))
    && Palettes.Find("nope") == Palettes.All[0]);

await RegressionTests.Run(Check);

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

static SessionItem Apply(string name)
{
    var item = new SessionItem { Provider = "claude", SessionId = "s" };
    item.Apply(new ActivityEvent("claude", "s", "SessionStart", DateTimeOffset.Now, null, null, null, null, null, null, null, null, null));
    item.Apply(new ActivityEvent("claude", "s", name, DateTimeOffset.Now, null, null, null, null, null, null, null, null, null));
    return item;
}

static bool PermissionPending()
{
    var item = new SessionItem { Provider = "Claude", SessionId = "s" };
    item.Apply(new ActivityEvent("Claude", "s", "PermissionRequest", DateTimeOffset.Now, null, null, null, null, null, null, null, null, null,
        "Bash", "npm test", "abc123"));
    var pending = item.PendingRequestId == "abc123" && item.PendingToolName == "Bash" && item.PendingToolDetail == "npm test";
    item.Apply(new ActivityEvent("Claude", "s", "PostToolUse", DateTimeOffset.Now, null, null, null, null, null, null, null, null, null));
    return pending && item.PendingRequestId == "abc123" && item.State == SessionState.Waiting;
}

static bool PermissionReply()
{
    var directory = TestDirectory();
    try
    {
        PermissionReplies.Write("abc123", allow: false, directory);
        var json = File.ReadAllText(Path.Combine(directory, "abc123.json"));
        var rejected = false;
        try { PermissionReplies.Write("..\\evil", allow: true, directory); }
        catch (ArgumentException) { rejected = true; }
        PermissionReplies.Cancel("def456", directory);
        var cancel = File.ReadAllText(Path.Combine(directory, "def456.json"));
        return json == """{"behavior":"deny"}""" && cancel == """{"behavior":"cancel"}""" && rejected && Directory.GetFiles(directory).Length == 2;
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static List<UsageAlert> Alerts(params QuotaLine[] lines)
{
    var quota = new QuotaReading { State = SourceState.Ok, Lines = lines };
    var snapshot = new ColumnSnapshot(new ColumnSettings { Id = "c", Name = "测试" }, quota, default, default, Array.Empty<UsageRecord>(), null);
    return ColumnService.CheckAlerts(snapshot, new AlertSettings()).ToList();
}

static bool DailyBuckets()
{
    var today = new DateOnly(2026, 10, 29);
    DateTimeOffset At(int day, int hour) => new(new DateTime(2026, 10, day, hour, 0, 0, DateTimeKind.Local));
    var days = DailyUsage.From(new[]
    {
        new UsageRecord(At(29, 9), "m", 10, 0, 0, 0, 1m),
        new UsageRecord(At(29, 23), "m", 5, 0, 0, 0, null),
        new UsageRecord(At(27, 12), "m", 7, 0, 0, 0, null),
        new UsageRecord(At(20, 12), "m", 99, 0, 0, 0, null),
    }, today);
    return days.Count == 7 && days[0].Date == new DateOnly(2026, 10, 23) && days[^1].Date == today
        && days[^1].Tokens == 15 && days[^1].Cost == 1m && days[^3].Tokens == 7
        && days.Sum(day => day.Tokens) == 22;
}

static bool Roundtrip()
{
    var directory = TestDirectory();
    try
    {
        var store = new SettingsStore(directory);
        var settings = new IslandSettings { Style = CompactStyle.Capsule, Language = "en-US" };
        settings.Columns[0].Site = "https://example.com";
        store.Save(settings);
        var loaded = store.Load();
        return loaded.Style == CompactStyle.Capsule && loaded.Language == "en-US" && loaded.Columns[0].Site == "https://example.com";
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static bool DeepSeekBalance()
{
    using var document = System.Text.Json.JsonDocument.Parse(
        """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}]}""");
    var lines = AIIsland.Core.Providers.DeepSeekSource.Parse(document.RootElement);
    return lines.Count == 3
           && lines[0].Value == "¥110.00"
           && lines[1].Title == "赠送余额"
           && lines[2].Value == "¥100.00";
}

static bool Secrets()
{
    var directory = TestDirectory();
    try
    {
        var store = SecretStore.Load(directory);
        store.Set("t", "secret");
        store.Save();
        return SecretStore.Load(directory).Get("t") == "secret";
    }
    finally
    {
        Directory.Delete(directory, true);
    }
}

static List<QuotaLine> Sub2ApiQuota(string json)
{
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return AIIsland.Core.Providers.Sub2ApiSource.Parse(document.RootElement);
}

static List<QuotaLine> NewApiQuota(string json, decimal? quotaPerUnit)
{
    using var document = System.Text.Json.JsonDocument.Parse(json);
    return AIIsland.Core.Providers.NewApiSource.ParseTokenUsage(document.RootElement, quotaPerUnit);
}

static string TestDirectory() => Directory.CreateDirectory(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp", "opencode", "aiisland-test-" + Guid.NewGuid().ToString("N"))).FullName;

static async Task Probe()
{
    foreach (var source in SourceCatalog.All)
    {
        Console.WriteLine($"== {source.Name} ({source.Id}) ==");
        var quota = SourceCatalog.CreateQuota(source.Id);
        if (quota is not null)
        {
            var reading = await quota.ReadAsync(new SourceContext(), CancellationToken.None);
            Console.WriteLine($"  quota: {reading.State} {reading.Status} {reading.Error}");
            foreach (var line in reading.Lines)
                Console.WriteLine($"    {line.Title}: {line.Value} {line.Suffix} {line.Reset}");
        }
        var usage = SourceCatalog.CreateUsage(source.Id);
        if (usage is not null)
        {
            var records = await usage.ReadAsync(new SourceContext(), DateTimeOffset.Now.AddDays(-7), CancellationToken.None);
            var totals = UsageTotals.From(records);
            Console.WriteLine($"  usage: {totals.Requests} requests, {Human.Tokens(totals.TotalTokens)} tokens, {Human.Money(totals.Cost)}");
        }
    }
}
