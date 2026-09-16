using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIIsland.Core;
using AIIsland.Modules;
using AIIsland.Services;

internal static class StartupChecks
{
    public static async Task Run(string root)
    {
        var folder = Path.Combine(Path.GetFullPath(root), "artifacts", "startup-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var process = Process.GetCurrentProcess();
        var old = new IslandEvent("Codex", "already-running", "PreToolUse", DateTimeOffset.UtcNow.AddMinutes(-5),
            process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()), "old-turn", "old-tool", folder);
        var snapshot = new ProcessSnapshot(new[] { new AppProcess(process.Id, "codex", null) });
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        try
        {
            File.WriteAllText(Path.Combine(folder, "old.json"), JsonSerializer.Serialize(old));
            using (var module = new AiModule(new Settings(), new EventInbox(folder)))
            {
                var notices = 0;
                module.Completed += _ => notices++;
                module.ApprovalRequested += _ => notices++;
                await module.RefreshAsync(snapshot, CancellationToken.None);
                Check(module.MonitoredCount == 1 && module.ActiveCount == 0 && module.Summary.Contains("等待状态同步") &&
                    module.Groups.Single().Rows.Single().Elapsed == "" && notices == 0,
                    "Starting Island after an Agent restores its live session without replaying historical work or notifications");
                File.WriteAllText(Path.Combine(folder, "new.json"), JsonSerializer.Serialize(old with
                { Kind = "UserPromptSubmit", At = DateTimeOffset.UtcNow, TurnId = "new-turn", ToolId = null }));
                await module.RefreshAsync(snapshot, CancellationToken.None);
                Check(module.MonitoredCount == 1 && module.ActiveCount == 1 && module.Groups.Single().Rows.Single().Status == "思考中",
                    "A fresh Hook updates the restored session without duplicating it");
            }
            using (var restarted = new AiModule(new Settings(), new EventInbox(folder)))
            {
                await restarted.RefreshAsync(snapshot, CancellationToken.None);
                Check(restarted.MonitoredCount == 1 && restarted.ActiveCount == 0,
                    "Restarting Island recovers an idle connected Agent even after its inbox was consumed");
            }
            using (var disabled = new AiModule(new Settings { Codex = false }, new EventInbox(folder)))
            {
                await disabled.RefreshAsync(snapshot, CancellationToken.None);
                Check(disabled.MonitoredCount == 0 && !disabled.Summary.Contains("发现"), "Startup recovery respects disabled providers");
            }
            var registry = Path.Combine(folder, "sessions");
            foreach (var file in Directory.GetFiles(registry, "*.json"))
                File.WriteAllText(file, JsonSerializer.Serialize(old with { ProcessStartedAt = old.ProcessStartedAt!.Value.AddSeconds(-1) }));
            using (var stale = new AiModule(new Settings(), new EventInbox(folder)))
            {
                await stale.RefreshAsync(snapshot, CancellationToken.None);
                Check(stale.MonitoredCount == 0 && stale.ActiveCount == 0 && stale.Summary.Contains("发现 1 个 Agent"),
                    "Reused PID metadata is rejected and an unlinked native Agent gets an explicit Hook hint");
            }
            SessionRegistry.Remember(registry, old);
            SessionRegistry.Remember(registry, old with { Kind = "SessionEnd" });
            Check(Directory.GetFiles(registry, "*.json").Length == 0, "SessionEnd removes the persisted connection metadata");
            Check(!SessionRegistry.IsAlive(old with { ProcessId = int.MaxValue }) && !SessionRegistry.IsAlive(old with { ProcessStartedAt = null }),
                "Exited processes and sessions without process identity are not restored");
            using (var fresh = new AiModule(new Settings(), new EventInbox(folder)))
            {
                File.WriteAllText(Path.Combine(folder, "fresh.json"), JsonSerializer.Serialize(old with
                { Kind = "UserPromptSubmit", At = DateTimeOffset.UtcNow, SessionId = "fresh-session" }));
                await fresh.RefreshAsync(snapshot, CancellationToken.None);
                Check(fresh.MonitoredCount == 1 && fresh.ActiveCount == 1,
                    "Connection recovery does not swallow a fresh task in the first inbox batch");
            }
        }
        finally { Directory.Delete(folder, true); }
    }
}
