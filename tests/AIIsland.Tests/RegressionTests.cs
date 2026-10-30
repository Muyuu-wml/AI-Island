using Process = System.Diagnostics.Process;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIIsland.Core;
using AIIsland.Core.Providers;
using AIIsland.Core.Sessions;

internal static class RegressionTests
{
    public static async Task Run(Action<string, bool> check)
    {
        using var files = new TestFiles();
        var clock = new TestClock();
        var tracker = new SessionTracker(files.Path, clock);
        ActivityEvent Event(string name, string? request = null, string? tool = null) =>
            new("Claude", "session", name, clock.GetUtcNow(), null, null, null, tool, null, null, null, null, null,
                "Bash", request, request);
        void Send(ActivityEvent activity, bool enabled = true)
        {
            files.Send(activity);
            tracker.Poll(enabled);
        }
        string Reply(string id) => File.ReadAllText(System.IO.Path.Combine(files.Path, "replies", id + ".json"));

        Send(Event("PermissionRequest", "first"));
        Send(Event("PermissionRequest", "second")); // Identical timestamp, different requests.
        Send(Event("PostToolUse", tool: "unrelated"));
        Send(Event("PostToolUseFailure", tool: "unrelated"));
        Send(Event("PreToolUse", tool: "unrelated"));
        check("parallel permissions survive unrelated tools", tracker.Snapshot().Single().PendingRequests.Count == 2
            && tracker.Snapshot().Single().PendingRequestId == "first" && !Directory.Exists(System.IO.Path.Combine(files.Path, "replies")));
        check("approve advances queue", tracker.Answer("Claude", "session", "first", true)
            && Reply("first") == """{"behavior":"allow"}""" && tracker.Snapshot().Single().PendingRequestId == "second");
        check("deny targets one request", tracker.Answer("Claude", "session", "second", false)
            && Reply("second") == """{"behavior":"deny"}""" && !tracker.Answer("Claude", "session", "second", true));

        Send(Event("PermissionRequest", "expire"));
        clock.Advance(TimeSpan.FromSeconds(60));
        check("answer rejects exact deadline before next poll", !tracker.Answer("Claude", "session", "expire", true)
            && tracker.Snapshot().Single().PermissionExpired && tracker.Snapshot().Single().PendingRequestId is null
            && !File.Exists(System.IO.Path.Combine(files.Path, "replies", "expire.json")));
        Send(Event("Stop"));
        Send(Event("PermissionRequest", "short") with { RequestDeadline = clock.GetUtcNow().AddSeconds(3) });
        clock.Advance(TimeSpan.FromSeconds(3));
        tracker.Poll();
        check("poll expires request without new events", tracker.Snapshot().Single().PermissionExpired);

        Send(Event("PermissionRequest", "matched", "toolA"));
        Send(Event("PermissionRequest", "other", "toolB"));
        Send(Event("PostToolUse", tool: "toolA"));
        check("only correlated request is cancelled", Reply("matched") == """{"behavior":"cancel"}"""
            && tracker.Snapshot().Single().PendingRequestId == "other");
        Send(Event("Stop"));
        check("turn end releases remaining hook", Reply("other") == """{"behavior":"cancel"}"""
            && tracker.Snapshot().Single().PendingRequests.Count == 0);

        var notifications = 0;
        tracker.Changed += (_, _) => notifications++;
        Send(Event("PermissionRequest", "disable"));
        var before = notifications;
        tracker.Poll(enabled: false);
        Send(Event("PermissionRequest", "disabledInbox"), enabled: false);
        check("disabled tracking drains silently and releases hooks", tracker.Snapshot().Count == 0
            && notifications == before && Reply("disable") == """{"behavior":"cancel"}"""
            && Reply("disabledInbox") == """{"behavior":"cancel"}"""
            && !tracker.Answer("Claude", "session", "disable", true));

        using (var process = Process.GetCurrentProcess())
        {
            Send(Event("UserPromptSubmit") with { ProcessId = process.Id, ProcessStarted = process.StartTime.ToUniversalTime() });
            clock.Advance(TimeSpan.FromMinutes(2));
            tracker.Poll(keepMinutes: 1);
            check("live process survives retention", tracker.Snapshot().Single().State == SessionState.Running);
            Send(Event("PreToolUse") with { ProcessStarted = process.StartTime.ToUniversalTime().AddMinutes(-1) });
            clock.Advance(TimeSpan.FromSeconds(5));
            tracker.Poll(keepMinutes: 1);
            check("reused pid is ended", tracker.Snapshot().Single().State == SessionState.Ended);
            clock.Advance(TimeSpan.FromMinutes(1));
            tracker.Poll(keepMinutes: 1);
            check("retention prunes without new sessions", tracker.Snapshot().Count == 0);
        }
        Send(Event("UserPromptSubmit") with { ProcessId = int.MaxValue });
        clock.Advance(TimeSpan.FromSeconds(5));
        tracker.Poll();
        check("dead process is ended", tracker.Snapshot().Single().State == SessionState.Ended);

        Send(Event("PermissionRequest", "earlyExit"));
        Send(Event("PermissionExpired", "earlyExit"));
        check("hook exit disables its button", !tracker.Answer("Claude", "session", "earlyExit", true));

        HookMerge(check);
        UsageParsing(check);
        CacheAndAlerts(files.Path, check);
        await UsagePipeline(files.Path, check);
    }

    private static void HookMerge(Action<string, bool> check)
    {
        var groups = JsonNode.Parse("""
            [{"matcher":"Bash","timeout":20,"hooks":[
                {"type":"command","command":"\"C:/old/AIIsland.Hook.exe\" Claude"},
                {"type":"command","command":"echo keep"},
                {"type":"prompt","prompt":"AIIsland.Hook must not delete this"}]},
             {"hooks":[{"type":"command","command":"cmd.exe /d /c \"C:/old/AIIsland.Hook-Codex.cmd\""}]},
             {"hooks":[{"type":"command","command":"echo AIIsland.Hook.exe"}]}]
            """)!.AsArray();
        HookConfiguration.RemoveIslandHooks(groups);
        check("hook merge preserves mixed groups and metadata", groups.Count == 2 && groups[0]!["matcher"]!.GetValue<string>() == "Bash"
            && groups[0]!["timeout"]!.GetValue<int>() == 20 && groups[0]!["hooks"]!.AsArray().Count == 2
            && groups[1]!["hooks"]![0]!["command"]!.GetValue<string>() == "echo AIIsland.Hook.exe");
        var once = groups.ToJsonString();
        HookConfiguration.RemoveIslandHooks(groups);
        check("hook cleanup is idempotent", once == groups.ToJsonString());
    }

    private static void UsageParsing(Action<string, bool> check)
    {
        var lines = new[]
        {
            """{"type":"turn_context","payload":{"model":"gpt-5"}}""",
            Count("2026-10-28T10:00:00Z", 100, 20, 10),
            Count("2026-10-29T10:00:00Z", 150, 30, 20),
            Count("2026-10-29T10:01:00Z", 150, 30, 20),
            """{"payload":{"type":"token_count",broken""",
            Count("2026-10-29T10:02:00Z", 200, 40, 25),
        };
        IReadOnlyList<UsageRecord> Parse(IEnumerable<string> input, DateTimeOffset since) =>
            CodexSource.ParseUsage(input, since, Pricing.Load(null), CancellationToken.None);
        var all = Parse(lines, DateTimeOffset.MinValue);
        check("codex model and cumulative dedup", all.Count == 3 && all.All(r => r.Model == "gpt-5")
            && UsageTotals.From(all).TotalTokens == 240);
        var filtered = Parse(lines, DateTimeOffset.Parse("2026-10-29T00:00:00Z"));
        check("codex baseline includes earlier days", filtered.Count == 2 && UsageTotals.From(filtered).TotalTokens == 120);
        var reset = Parse(lines.Append(Count("2026-10-29T10:03:00Z", 10, 2, 0)), DateTimeOffset.MinValue);
        check("codex cumulative reset", UsageTotals.From(reset).TotalTokens == 252);
        var last = Count("2026-10-29T10:04:00Z", 30, 4, 5).Replace("total_token_usage", "last_token_usage");
        var mixed = Parse(new[] { last, last, Count("2026-10-29T10:05:00Z", 50, 10, 10) }, DateTimeOffset.MinValue);
        check("codex last-only and cumulative transitions", mixed.Count == 2 && UsageTotals.From(mixed).TotalTokens == 60);
    }

    private static string Count(string at, int input, int output, int cache) => JsonSerializer.Serialize(new
    {
        type = "event_msg", timestamp = at,
        payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = input, output_tokens = output, cached_input_tokens = cache } } },
    });

    private static void CacheAndAlerts(string directory, Action<string, bool> check)
    {
        var settings = new ColumnSettings { Id = "cache", QuotaSource = SourceCatalog.DeepSeek };
        var context = new SourceContext("https://example.com", "key-one");
        var identity = ColumnService.QuotaIdentity(settings, context);
        var reading = new QuotaReading { State = SourceState.Ok, ReadAt = DateTimeOffset.UtcNow,
            Lines = new[] { new QuotaLine("账户余额", null, "¥105.00"), new QuotaLine("赠送余额", null, "¥5.00") } };
        var snapshot = new ColumnSnapshot(settings, reading, default, default, Array.Empty<UsageRecord>(), null);
        check("deepseek total excludes balance components", !ColumnService.CheckAlerts(snapshot, new AlertSettings()).Any());
        check("stale quota cannot alert", !ColumnService.CheckAlerts(snapshot with
        {
            Quota = reading with { State = SourceState.Stale, Lines = new[] { new QuotaLine("账户余额", null, "¥1.00"), new QuotaLine("5 小时", 99) } },
        }, new AlertSettings()).Any());
        var service = new ColumnService(directory);
        service.UseQuota(settings.Id, identity, reading);
        var restored = new ColumnService(directory);
        check("same account keeps offline cache", restored.UseQuota(settings.Id, identity, QuotaReading.Failure("offline")).State == SourceState.Stale);
        var changedKey = ColumnService.QuotaIdentity(settings, context with { Key = "key-two" });
        check("different credentials cannot reuse cache", restored.UseQuota(settings.Id, changedKey, QuotaReading.Failure("offline")).State == SourceState.Error
            && new ColumnService(directory).UseQuota(settings.Id, identity, QuotaReading.Failure("offline")).State == SourceState.Error);
        var changedSite = ColumnService.QuotaIdentity(settings, context with { Site = "https://other.example.com" });
        settings.QuotaSource = SourceCatalog.NewApi;
        var changedSource = ColumnService.QuotaIdentity(settings, context);
        check("cache identity covers source site and key", new[] { identity, changedKey, changedSite, changedSource }.Distinct().Count() == 4);
        check("cache never stores plaintext key", !File.ReadAllText(System.IO.Path.Combine(directory, "quota-cache.json")).Contains("key-one"));
    }

    private static async Task UsagePipeline(string directory, Action<string, bool> check)
    {
        var oldHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var codex = System.IO.Path.Combine(directory, "codex");
            var sessions = Directory.CreateDirectory(System.IO.Path.Combine(codex, "sessions"));
            File.WriteAllLines(System.IO.Path.Combine(sessions.FullName, "usage.jsonl"), new[]
            {
                """{"type":"turn_context","payload":{"model":"gpt-5"}}""",
                Count(DateTimeOffset.UtcNow.ToString("O"), 100, 20, 10),
                Count(DateTimeOffset.UtcNow.ToString("O"), 150, 30, 20),
            });
            Environment.SetEnvironmentVariable("CODEX_HOME", codex);
            var settings = new ColumnSettings { QuotaSource = "", UsageSource = SourceCatalog.Codex };
            var service = new ColumnService(directory);
            var batch = await service.ReadColumnsAsync(new[] { settings, settings }, SecretStore.Load(directory), new AlertSettings(), CancellationToken.None);
            check("background usage pipeline shares local results", batch.Snapshots.All(s => s.Week.TotalTokens == 180)
                && ReferenceEquals(batch.Snapshots[0].Records, batch.Snapshots[1].Records));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            try
            {
                await service.ReadAsync(settings, SecretStore.Load(directory), cancellation.Token);
                check("usage read honors cancellation", false);
            }
            catch (OperationCanceledException) { check("usage read honors cancellation", true); }
        }
        finally { Environment.SetEnvironmentVariable("CODEX_HOME", oldHome); }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class TestFiles : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", "opencode", "aiisland-test-" + Guid.NewGuid().ToString("N"));
        public void Send(ActivityEvent activity)
        {
            var inbox = Directory.CreateDirectory(System.IO.Path.Combine(Path, "inbox"));
            File.WriteAllText(System.IO.Path.Combine(inbox.FullName, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(activity));
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
