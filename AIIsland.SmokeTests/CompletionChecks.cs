using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIIsland.Core;
using AIIsland.Modules;
using AIIsland.Services;

internal static class CompletionChecks
{
    public static async Task Run(string root)
    {
        var artifacts = Path.GetFullPath(Path.Combine(root, "artifacts"));
        var folder = Path.GetFullPath(Path.Combine(artifacts, "completion-checks-" + Guid.NewGuid().ToString("N")));
        if (!folder.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Completion checks must stay inside the artifacts directory.");
        var inboxDirectory = Path.Combine(folder, "inbox");
        var transcript = Path.Combine(folder, "rollout.jsonl");
        const string sessionId = "completion-integration-session";
        var notifications = new List<IslandEvent>();
        AiModule? module = null;
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(transcript, JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                type = "session_meta",
                payload = new { id = sessionId, source = "cli" }
            }) + "\n");
            var ai = new AiModule(new Settings { Codex = true, Claude = false, QQMusic = false, Clash = false }, new EventInbox(inboxDirectory));
            module = ai;
            ai.Completed += notifications.Add;
            var lastHookAt = DateTimeOffset.UtcNow;

            void Check(bool condition, string message)
            {
                if (!condition) throw new Exception(message);
                Console.WriteLine("PASS " + message);
            }
            void Send(string kind, string turn)
            {
                var at = DateTimeOffset.UtcNow;
                if (at <= lastHookAt) at = lastHookAt.AddTicks(1);
                lastHookAt = at;
                var value = new IslandEvent("Codex", sessionId, kind, at, TurnId: turn,
                    ToolId: kind == "PreToolUse" ? "tool-one" : null, WorkingDirectory: folder, TranscriptPath: transcript);
                File.WriteAllText(Path.Combine(inboxDirectory, Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(value));
            }
            void AppendCompletion(string turn, DateTimeOffset at) => File.AppendAllText(transcript, JsonSerializer.Serialize(new
            {
                timestamp = at,
                type = "event_msg",
                payload = new { type = "task_complete", turn_id = turn }
            }) + "\n");
            Task Refresh() => ai.RefreshAsync(new ProcessSnapshot(Array.Empty<AppProcess>()), CancellationToken.None);
            AiRow Row() => ai.Groups.Single(g => g.Name == "Codex").Rows.Single();

            Send("UserPromptSubmit", "turn-one");
            Send("PreToolUse", "turn-one");
            AppendCompletion("unrelated-turn", DateTimeOffset.UtcNow);
            await Refresh();
            Check(ai.ActiveCount == 1 && Row().Status == "执行中" && notifications.Count == 0,
                "Isolated Hook inbox starts an executing Codex row");
            await Task.Delay(1100);
            await Refresh();
            Check(ai.ActiveCount == 1 && Row().Status == "执行中" && notifications.Count == 0,
                "A transcript without the current turn's completion leaves the task active");

            var completedAt = DateTimeOffset.UtcNow;
            AppendCompletion("turn-one", completedAt);
            await Task.Delay(1100);
            await Refresh();
            Check(ai.ActiveCount == 0 && (Row().Status.Contains("Completed") || Row().Status == "等待输入"),
                "Polling the transcript repairs an omitted Stop Hook and updates the row");
            Check(notifications.Count == 1 && notifications[0].Kind == "Stop" && notifications[0].At == completedAt,
                "Completion reconciliation emits one notification with the actual completion time");

            Send("Stop", "turn-one");
            Send("PreToolUse", "turn-one");
            Send("PermissionRequest", "turn-one");
            await Refresh();
            Check(ai.ActiveCount == 0 && !ai.NeedsAttention && notifications.Count == 1,
                "Late Stop and tool Hooks neither repeat notification nor reopen the reconciled turn");
            await Task.Delay(3100);
            await Refresh();
            Check(Row().Status == "等待输入" && Row().Elapsed == "" && !ai.HasActivity && notifications.Count == 1,
                "The completed row becomes idle after three seconds without retaining its timer");

            Send("UserPromptSubmit", "turn-two");
            await Refresh();
            Check(ai.ActiveCount == 1 && Row().Status == "思考中" && notifications.Count == 1,
                "A new prompt starts a fresh thinking task after reconciliation");
            await Task.Delay(1100);
            await Refresh();
            Check(ai.ActiveCount == 1 && Row().Status == "思考中" && notifications.Count == 1,
                "The previous turn's cached completion cannot finish the new prompt");
        }
        finally
        {
            module?.Dispose();
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
