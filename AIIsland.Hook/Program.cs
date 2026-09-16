using System.Text.Json;
using AIIsland.Core;

// Fail open: observational hooks must never change the CLI's outcome.
try
{
    if (args.Length != 1 || args[0] is not ("Codex" or "Claude")) return 0;
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
    var root = document.RootElement;
    if (!root.TryGetProperty("session_id", out var session) || session.ValueKind != JsonValueKind.String ||
        !root.TryGetProperty("hook_event_name", out var kind) || kind.ValueKind != JsonValueKind.String) return 0;
    var id = session.GetString(); var eventName = kind.GetString();
    if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || eventName is not ("SessionStart" or "UserPromptSubmit" or "PreToolUse" or "PostToolUse" or "PostToolUseFailure" or "PermissionRequest" or "Stop" or "StopFailure" or "SessionEnd" or "Interrupt")) return 0;
    // Subagent events can share the parent session ID. Do not finish its task.
    if (root.TryGetProperty("agent_id", out var agent) && agent.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(agent.GetString())) return 0;
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIIsland", "inbox");
    Directory.CreateDirectory(directory);
    // Bound disk growth when Island is not running.
    foreach (var old in Directory.EnumerateFiles(directory, "*.json").Take(32))
        if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1)) File.Delete(old);
    if (Directory.EnumerateFiles(directory, "*.json").Take(2048).Count() >= 2048) return 0;
    var file = Path.Combine(directory, Guid.NewGuid().ToString("N"));
    string? Field(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 200 ? v.GetString() : null;
    var parent = ParentProcess.Find(args[0]);
    var cwd = root.TryGetProperty("cwd", out var directoryValue) && directoryValue.ValueKind == JsonValueKind.String ? ProjectNames.Normalize(directoryValue.GetString()) : null;
    var transcript = args[0] == "Codex" && root.TryGetProperty("transcript_path", out var transcriptValue) && transcriptValue.ValueKind == JsonValueKind.String ? ProjectNames.Normalize(transcriptValue.GetString()) : null;
    var value = new IslandEvent(args[0], id, eventName, at, parent.Id, parent.Started, Field("turn_id"), Field("tool_use_id"), cwd, transcript);
    File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(value));
    File.Move(file + ".tmp", file + ".json");
    SessionRegistry.Remember(Path.Combine(directory, "sessions"), value);
}
catch { /* No payload, prompt, or CLI output is logged. */ }
return 0;
