using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIIsland.Core;
using AIIsland.Modules;
using AIIsland.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var at = DateTimeOffset.UtcNow;
var state = new IslandState();
IslandEvent Event(string kind, string provider = "OpenCode", string session = "ses_test") => new(provider, session, kind, at = at.AddMilliseconds(1));
Check(state.Apply(Event("UserPromptSubmit")), "OpenCode starts a task");
var task = state.Sessions.Single();
var started = task.StartedAt;
state.Apply(Event("PreToolUse"));
state.Apply(Event("PermissionRequest"));
Check(task.Status == AIStatus.WaitingForApproval, "Approval state");
state.Apply(Event("PreToolUse"));
state.Apply(Event("Thinking"));
Check(task.StartedAt == started && task.Status == AIStatus.Thinking, "Phase changes preserve task duration");
state.Apply(Event("StopFailure"));
Check(task.Status == AIStatus.Failed && !task.Active, "Failure ends task");
state.Apply(Event("Thinking"));
state.Apply(Event("Stop"));
Check(task.Status == AIStatus.Completed, "Next task completes");
state.Apply(Event("SessionEnd"));
Check(task.Connection == ConnectionStatus.Offline, "Session closes");
state.Apply(Event("Thinking"));
Check(task.Active && task.Connection == ConnectionStatus.Online, "A reloaded plugin resumes an existing session");
Check(!state.Apply(Event("Thinking", "Claude")), "OpenCode-only event is rejected for other providers");
foreach (var provider in new[] { "Codex", "Claude" })
{
    Check(state.Apply(Event("UserPromptSubmit", provider)), provider + " start regression");
    Check(state.Apply(Event("Stop", provider)), provider + " completion regression");
}
Check(!state.Apply(Event("UserPromptSubmit", "Unknown")), "Unknown provider rejected");
Check(JsonSerializer.Deserialize<Settings>("{\"Codex\":false}")!.OpenCode, "Old settings enable OpenCode by default");
Check(!JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { OpenCode = false }))!.OpenCode, "Disabled setting round-trips");
var ownDirectory = ProcessLifetime.Directory(Environment.ProcessId);
Check(ownDirectory != null && Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownDirectory)) == Path.TrimEndingDirectorySeparator(Environment.CurrentDirectory), "OpenCode client directory is readable");

var directory = Path.Combine(Path.GetTempPath(), "ai-island-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    using var process = Process.GetCurrentProcess();
    var live = Event("SessionStart") with { ProcessId = process.Id, ProcessStartedAt = process.StartTime.ToUniversalTime(), WorkingDirectory = directory };
    var registry = Path.Combine(directory, "registry");
    SessionRegistry.Remember(registry, live);
    var recovered = new List<IslandEvent>();
    SessionRegistry.Read(registry, recovered.Add);
    Check(recovered.Count == 1 && recovered[0].Provider == "OpenCode" && recovered[0].ShellProcessId == null, "Service session recovery");
    SessionRegistry.Remember(registry, live with { Kind = "SessionEnd" });
    Check(!Directory.EnumerateFiles(registry).Any(), "Deleted sessions are not restored");

    var inboxPath = Path.Combine(directory, "inbox");
    using var module = new AiModule(new Settings(), new EventInbox(inboxPath));
    var approvals = 0; var completions = 0;
    module.ApprovalRequested += _ => approvals++;
    module.Completed += _ => completions++;
    async Task Deliver(string kind, params AppProcess[] apps)
    {
        var value = Event(kind) with { ProcessId = live.ProcessId, ProcessStartedAt = live.ProcessStartedAt, WorkingDirectory = directory };
        File.WriteAllText(Path.Combine(inboxPath, Guid.NewGuid() + ".json"), JsonSerializer.Serialize(value));
        await module.RefreshAsync(new ProcessSnapshot(apps), CancellationToken.None);
    }
    at = DateTimeOffset.UtcNow.AddMilliseconds(50);
    await Deliver("UserPromptSubmit");
    await Deliver("PermissionRequest");
    await Deliver("PermissionRequest");
    Check(approvals == 1 && module.NeedsAttention, "Repeated approval snapshots notify only once");
    await Deliver("Thinking");
    await Deliver("Stop");
    Check(completions == 1 && module.Groups.Single().Name == "OpenCode", "Inbox-to-UI completion and grouping");
    module.Configure(new Settings { OpenCode = false });
    await Deliver("UserPromptSubmit");
    Check(module.Groups.Count == 0, "Disabled OpenCode ignores events");
    module.Configure(new Settings());
    await Deliver("Thinking");
    Check(module.ActiveCount == 1, "Re-enabled OpenCode resumes on a phase event");
    // The plugin reports the shared background service PID; only a CLI started in the session's
    // own directory keeps that session monitored.
    var service = new AppProcess(live.ProcessId!.Value, "opencode", null, @"C:\Users\service");
    var client = new AppProcess(live.ProcessId!.Value + 100000, "opencode", null, directory);
    var elsewhere = new AppProcess(live.ProcessId!.Value + 200000, "opencode", null, directory + "-other");
    await Deliver("UserPromptSubmit", service, client);
    await Deliver("Stop", service, client);
    Check(module.ActiveCount == 0 && module.MonitoredCount == 1, "An attached client keeps an idle session");
    await module.RefreshAsync(new ProcessSnapshot(new[] { service, elsewhere }), CancellationToken.None);
    Check(module.MonitoredCount == 0 && module.Groups.SelectMany(g => g.Rows).All(r => r.Status != "等待输入"), "Closing the CLI ends the idle OpenCode session");
    at = DateTimeOffset.UtcNow.AddSeconds(1); // A new prompt arrives after the synthetic session end.
    await Deliver("UserPromptSubmit", service, elsewhere);
    Check(module.ActiveCount == 1, "A running task stays tracked without a client");
    module.TogglePause();
    await Deliver("PermissionRequest");
    Check(module.ActiveCount == 0, "Paused monitoring ignores events");
}
finally { Directory.Delete(directory, true); }
Console.WriteLine("OpenCode state, settings, recovery and inbox integration checks passed.");
