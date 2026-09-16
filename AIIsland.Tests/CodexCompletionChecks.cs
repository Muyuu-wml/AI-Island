using System.Text;
using System.Text.Json;
using AIIsland.Core;
using AIIsland.Services;

static class CodexCompletionChecks
{
    public static void Run(Action<bool, string> check)
    {
        var folder = Path.Combine(Path.GetTempPath(), "AIIsland-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var files = new List<string>();
        var at = DateTimeOffset.Parse("2026-09-15T00:00:00Z");
        var reader = new CodexCompletionReader();
        string Header(string id = "session-a", object? source = null) => JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, source = source ?? "cli" } }) + "\n";
        string Event(string type = "task_complete", string turn = "turn-a", int second = 10, string record = "event_msg") =>
            JsonSerializer.Serialize(new { timestamp = at.AddSeconds(second), type = record, payload = new { type, turn_id = turn } }) + "\n";
        string Fixture(string text)
        {
            var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jsonl");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            files.Add(path);
            return path;
        }
        AIProcess Session(string? path, string turn = "turn-a", string provider = "Codex") => new()
        {
            Provider = provider, SessionId = "session-a", Status = AIStatus.Executing,
            StartedAt = at, TurnId = turn, TranscriptPath = path
        };
        try
        {
            var good = Fixture(Header() + Event());
            var result = reader.Read(Session(good));
            check(result is { Kind: "Stop", TurnId: "turn-a" } && result.At == at.AddSeconds(10), "Rollout task completion preserves the authoritative timestamp");
            check(reader.Read(Session(good, "turn-b")) == null, "Another turn's completion cannot finish the active turn");
            check(reader.Read(Session(Fixture(Header("other-session") + Event()))) == null, "Transcript must belong to the monitored session");
            check(reader.Read(Session(Fixture(Header(source: new { subagent = new { parent_thread_id = "parent" } }) + Event()))) == null,
                "Subagent transcripts cannot finish a parent task");
            check(reader.Read(Session(Fixture(Header() + Event("turn_aborted"))))?.Kind == "Interrupt", "Explicit rollout interruption ends without reporting success");
            var future = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.AddDays(1), type = "event_msg", payload = new { type = "task_complete", turn_id = "turn-a" } }) + "\n";
            check(reader.Read(Session(Fixture(Header() + future))) == null, "A future-dated rollout event cannot complete a current task");
            check(reader.Read(Session(good, provider: "Claude")) == null && reader.Read(new AIProcess
                { Provider = "Codex", SessionId = "session-a", Status = AIStatus.Completed, StartedAt = at, TurnId = "turn-a", TranscriptPath = good }) == null,
                "Fallback only inspects active Codex tasks");
            check(reader.Read(Session(null)) == null && reader.Read(Session(Path.Combine(folder, "missing.jsonl"))) == null
                && reader.Read(Session(good, "")) == null, "Missing transcript or turn identity does not guess completion");
            var unfinished = Fixture(Header() + Event().TrimEnd('\n'));
            check(reader.Read(Session(unfinished)) == null, "An incomplete final rollout line is ignored");
            File.AppendAllText(unfinished, "\n");
            check(reader.Read(Session(unfinished))?.Kind == "Stop", "A newly completed rollout line invalidates the cached snapshot");
            check(reader.Read(Session(Fixture("{invalid}\n" + Event()))) == null
                && reader.Read(Session(Fixture(Header() + "{invalid}\n[]\n" + Event())))?.Kind == "Stop",
                "Malformed metadata is rejected and malformed event lines do not hide later completion");
            check(reader.Read(Session(Fixture(Header() + Event(record: "response_item") +
                JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "agent_message", message = Event() } }) + "\n"))) == null,
                "Response content and nested completion-looking text are ignored");
            var old = Session(Fixture(Header() + Event()));
            old.StartedAt = at.AddSeconds(11);
            check(reader.Read(old) == null, "Completion before the current task start cannot close a reused turn");
            var turns = Fixture(Header() + Event() + Event("task_started", "turn-b", 20));
            check(reader.Read(Session(turns, "turn-b")) == null, "A later active turn remains running after the previous turn completes");
            File.AppendAllText(turns, Event(turn: "turn-b", second: 30));
            check(reader.Read(Session(turns, "turn-b"))?.At == at.AddSeconds(30), "Appending a later turn completion is detected");
            var hugeTool = JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "function_call_output", output = new string('x', 2 * 1024 * 1024) } });
            check(reader.Read(Session(Fixture(Header() + hugeTool + "\n" + Event())))?.Kind == "Stop",
                "Bounded tail reading skips a partial oversized tool line and finds completion");
            var largeHeader = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "session-a", source = "cli", base_instructions = new string('x', 40 * 1024) } }) + "\n";
            check(reader.Read(Session(Fixture(largeHeader + Event())))?.Kind == "Stop", "Session metadata larger than 32 KB remains supported");
            using (var locked = new FileStream(good, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(new CodexCompletionReader().Read(Session(good)) == null, "An unreadable transcript fails open");
            check(reader.Read(Session(folder)) == null && reader.Read(Session("invalid\0path")) == null, "Invalid transcript paths fail open");

            var state = new IslandState();
            state.Apply(new IslandEvent("Codex", "session-a", "UserPromptSubmit", at, TurnId: "turn-a", TranscriptPath: good));
            state.Apply(new IslandEvent("Codex", "session-a", "PreToolUse", at.AddSeconds(12), TurnId: "turn-a", ToolId: "tool-a"));
            state.Apply(new IslandEvent("Codex", "session-a", "PreToolUse", at.AddSeconds(13), TurnId: "turn-a", ToolId: "tool-b"));
            var active = state.Sessions.Single();
            var completion = reader.Read(active);
            check(completion != null && state.ReconcileCompletion(completion) && !active.Active && active.Status == AIStatus.Completed
                && active.ActiveTools.Count == 0 && active.Elapsed(at.AddMinutes(1)) == TimeSpan.FromSeconds(10)
                && active.LastEventAt == at.AddSeconds(13), "Missing Stop is repaired despite delayed Hook receipt and elapsed time freezes");
            check(!state.Apply(new IslandEvent("Codex", "session-a", "PreToolUse", at.AddSeconds(14), TurnId: "turn-a", ToolId: "late-tool"))
                && !state.Apply(new IslandEvent("Codex", "session-a", "PermissionRequest", at.AddSeconds(15), TurnId: "turn-a")) && !active.Active,
                "Delayed tool and approval hooks cannot reopen an authoritative completed turn");
            check(state.Apply(new IslandEvent("Codex", "session-a", "UserPromptSubmit", at.AddSeconds(20), TurnId: "turn-a")) && active.Active,
                "A new prompt restarts monitoring after authoritative completion");
            check(reader.Read(active) == null && completion != null && !state.ReconcileCompletion(completion) && active.Active,
                "Replaying the previous completion cannot close a new prompt with the same turn ID");
            check(state.Apply(new IslandEvent("Codex", "session-a", "PreToolUse", at.AddSeconds(21), TurnId: "turn-a", ToolId: "new-tool"))
                && active.Status == AIStatus.Executing, "New prompt tools run even when the provider reuses a turn ID");
        }
        finally
        {
            foreach (var file in files) File.Delete(file);
            Directory.Delete(folder);
        }
    }
}
