using System.Text.Json;
using AIIsland.Hook;

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
        var id = session.GetString();
        var eventName = kind.GetString();
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 ||
            (eventName is not ("SessionStart" or "UserPromptSubmit" or "PreToolUse" or "PostToolUse" or "PostToolUseFailure" or "PermissionRequest" or "Stop" or "StopFailure" or "SessionEnd" or "Interrupt" or "SubagentStart" or "SubagentStop") &&
             !(args[0] == "OpenCode" && eventName == "Thinking"))) return 0;

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIIsland", "inbox");
        Directory.CreateDirectory(directory);
        // Bound disk growth when the island is not running.
        foreach (var old in Directory.EnumerateFiles(directory, "*.json").Take(32))
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1)) File.Delete(old);
        if (Directory.EnumerateFiles(directory, "*.json").Take(2048).Count() >= 2048) return 0;

        var parent = args[0] == "OpenCode" && root.TryGetProperty("process_id", out var processValue) && processValue.TryGetInt32(out var processId)
            ? ParentProcess.Read(processId)
            : ParentProcess.Find(args[0]);
        // OpenCode sessions belong to a shared service, not its launching terminal.
        var shell = args[0] == "OpenCode" ? ((int? Id, DateTimeOffset? Started))(null, null) : ProcessLifetime.FindShell(parent.Id, parent.Started);
        var cwd = root.TryGetProperty("cwd", out var directoryValue) && directoryValue.ValueKind == JsonValueKind.String ? directoryValue.GetString() : null;
        var transcript = args[0] == "Codex" && root.TryGetProperty("transcript_path", out var transcriptValue) && transcriptValue.ValueKind == JsonValueKind.String ? transcriptValue.GetString() : null;

        // Claude Code can act on a PermissionRequest decision; ask the island and wait for a click.
        string? toolName = null, toolDetail = null, requestId = null;
        if (eventName == "PermissionRequest")
        {
            toolName = Field(root, "tool_name");
            toolDetail = ToolSummary(root);
            if (args[0] == "Claude" && IslandRunning()) requestId = Guid.NewGuid().ToString("N");
        }

        var value = new HookEvent(
            args[0], id, eventName, at,
            parent.Id, parent.Started,
            Field(root, "turn_id"), Field(root, "tool_use_id"),
            cwd, transcript, shell.Id, shell.Started,
            Field(root, "agent_id"),
            toolName, toolDetail, requestId, requestId is null ? null : at.AddSeconds(60));
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        var json = JsonSerializer.Serialize(value);
        File.WriteAllText(file + ".tmp", json);
        File.Move(file + ".tmp", file + ".json");

        if (requestId is not null)
        {
            var decision = await WaitForDecision(requestId, value.RequestDeadline!.Value);
            if (decision is not null)
            {
                Console.WriteLine(decision);
                return 0;
            }
            var expired = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            File.WriteAllText(expired + ".tmp", JsonSerializer.Serialize(value with { EventName = "PermissionExpired", At = DateTimeOffset.UtcNow }));
            File.Move(expired + ".tmp", expired + ".json");
        }
    }
}
catch { /* No payload, prompt, or CLI output is logged. */ }
return 0;

static string? Field(JsonElement root, string name) =>
    root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 200 } text ? text : null;

// One human-readable line describing the tool call, e.g. the Bash command or the edited file.
static string? ToolSummary(JsonElement root)
{
    if (!root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object) return null;
    foreach (var name in new[] { "command", "file_path", "path", "url", "pattern", "description" })
        if (input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            return text.Length > 160 ? text[..160] + "…" : text;
    return null;
}

static bool IslandRunning()
{
    try
    {
        if (!System.Threading.Mutex.TryOpenExisting(@"Local\AIIsland-Sessions-" + Environment.UserName, out var mutex)) return false;
        mutex.Dispose();
        return true;
    }
    catch { return false; }
}

// Polls for the island's reply. Returns the decision JSON for Claude Code's stdout,
// or null when the user never clicked (the terminal prompt then proceeds as usual).
static async Task<string?> WaitForDecision(string requestId, DateTimeOffset deadline)
{
    var file = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIIsland", "replies", requestId + ".json");
    while (DateTimeOffset.UtcNow < deadline)
    {
        if (!IslandRunning()) return null;
        try
        {
            if (File.Exists(file))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var behavior = document.RootElement.TryGetProperty("behavior", out var value) ? value.GetString() : null;
                try { File.Delete(file); } catch { }
                if (behavior is not ("allow" or "deny")) return null;
                var decision = behavior == "allow"
                    ? (object)new { behavior = "allow" }
                    : new { behavior = "deny", message = "用户在 AI Island 中拒绝了该操作" };
                return JsonSerializer.Serialize(new
                {
                    hookSpecificOutput = new { hookEventName = "PermissionRequest", decision },
                });
            }
        }
        catch { /* Transient read races; retry. */ }
        if (!IslandRunning()) return null;
        await Task.Delay(200);
    }
    return null;
}
