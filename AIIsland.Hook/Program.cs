using System.Text.Json;
using AIIsland.Core;

// Fail open: observational hooks must never change the CLI's outcome.
try
{
    if (args.Length != 1 || args[0] is not ("Codex" or "Claude" or "OpenCode")) return 0;
    var at = DateTimeOffset.UtcNow;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    var buffer = new char[1024 * 1024];
    var length = 0;
    while (length < buffer.Length)
    {
        var n = await Console.In.ReadAsync(buffer.AsMemory(length), timeout.Token);
        if (n == 0) break;
        length += n;
    }
    if (length == buffer.Length) return 0;
    using var document = JsonDocument.Parse(new string(buffer, 0, length));
    var payload = document.RootElement;
    if (payload.ValueKind == JsonValueKind.Array && (args[0] != "OpenCode" || payload.GetArrayLength() > 256)) return 0;
    var events = payload.ValueKind == JsonValueKind.Array ? payload.EnumerateArray().ToArray() : new[] { payload };
    foreach (var root in events)
    {
        // A state snapshot and its completion may arrive in one batch; preserve order.
        at = at.AddTicks(1);
        if (!root.TryGetProperty("session_id", out var session) || session.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("hook_event_name", out var kind) || kind.ValueKind != JsonValueKind.String) return 0;
        var id = session.GetString(); var eventName = kind.GetString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || (eventName is not ("SessionStart" or "UserPromptSubmit" or "PreToolUse" or "PostToolUse" or "PostToolUseFailure" or "PermissionRequest" or "Stop" or "StopFailure" or "SessionEnd" or "Interrupt" or "SubagentStart" or "SubagentStop") && !(args[0] == "OpenCode" && eventName == "Thinking"))) return 0;
        var agentId = Field(root, "agent_id");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIIsland", "inbox");
        Directory.CreateDirectory(directory);
        // Bound disk growth when Island is not running.
        foreach (var old in Directory.EnumerateFiles(directory, "*.json").Take(32))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1)) File.Delete(old);
        if (Directory.EnumerateFiles(directory, "*.json").Take(2048).Count() >= 2048) return 0;
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var parent = args[0] == "OpenCode" && root.TryGetProperty("process_id", out var processValue) && processValue.TryGetInt32(out var processId)
            ? ParentProcess.Read(processId) : ParentProcess.Find(args[0]);
        // OpenCode V2 sessions belong to a shared service, not its launching terminal.
        var shell = args[0] == "OpenCode" ? ((int? Id, DateTimeOffset? Started))(null, null) : ProcessLifetime.FindShell(parent.Id, parent.Started);
        var cwd = root.TryGetProperty("cwd", out var directoryValue) && directoryValue.ValueKind == JsonValueKind.String ? ProjectNames.Normalize(directoryValue.GetString()) : null;
        var transcript = args[0] == "Codex" && root.TryGetProperty("transcript_path", out var transcriptValue) && transcriptValue.ValueKind == JsonValueKind.String ? ProjectNames.Normalize(transcriptValue.GetString()) : null;
        var value = new IslandEvent(args[0], id, eventName, at, parent.Id, parent.Started, Field(root, "turn_id"), Field(root, "tool_use_id"), cwd, transcript, shell.Id, shell.Started, agentId);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(value));
        File.Move(file + ".tmp", file + ".json");
        SessionRegistry.Remember(Path.Combine(directory, "sessions"), value);
    }
}
catch { /* No payload, prompt, or CLI output is logged. */ }
return 0;

static string? Field(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 200 } text ? text : null;
