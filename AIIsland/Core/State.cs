using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace AIIsland.Core;

public enum AIStatus { Idle, Thinking, Executing, WaitingForApproval, Completed, Failed, Ended }
public enum ConnectionStatus { Online, Offline, Unknown }
public sealed record IslandEvent(string Provider, string SessionId, string Kind, DateTimeOffset At, int? ProcessId = null, DateTimeOffset? ProcessStartedAt = null, string? TurnId = null, string? ToolId = null, string? WorkingDirectory = null, string? TranscriptPath = null, int? ShellProcessId = null, DateTimeOffset? ShellProcessStartedAt = null);
public sealed class AIProcess
{
    public required string Provider { get; init; }
    public required string SessionId { get; init; }
    public AIStatus Status { get; set; }
    public bool AwaitingState { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartedAt { get; set; }
    public int? ShellProcessId { get; set; }
    public DateTimeOffset? ShellProcessStartedAt { get; set; }
    public string? TurnId { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? TranscriptPath { get; set; }
    public string? ReconciledTurnId { get; set; }
    public string ProjectName => ProjectNames.Name(WorkingDirectory);
    public ConnectionStatus Connection { get; set; } = ConnectionStatus.Unknown;
    public HashSet<string> ActiveTools { get; } = new();
    public bool Active => Status is AIStatus.Thinking or AIStatus.Executing or AIStatus.WaitingForApproval;
    public TimeSpan Elapsed(DateTimeOffset now) => (EndedAt ?? now) - StartedAt;
}
public sealed class EventBus
{
    public event Action<IslandEvent>? Received;
    public void Publish(IslandEvent value) => Received?.Invoke(value);
}
public sealed class IslandState
{
    private readonly Dictionary<string, AIProcess> sessions = new();
    public IEnumerable<AIProcess> Sessions => sessions.Values;
    public void Clear() => sessions.Clear();
    public void RemoveProvider(string provider)
    { foreach (var key in sessions.Where(x => x.Value.Provider == provider).Select(x => x.Key).ToArray()) sessions.Remove(key); }
    public bool Apply(IslandEvent e)
    {
        if (e.Provider is not ("Codex" or "Claude") || string.IsNullOrWhiteSpace(e.SessionId) || e.SessionId.Length > 200) return false;
        var next = e.Kind switch {
            "SessionStart" => AIStatus.Idle, "UserPromptSubmit" => AIStatus.Thinking,
            "PreToolUse" => AIStatus.Executing, "PostToolUse" or "PostToolUseFailure" => AIStatus.Thinking,
            "PermissionRequest" => AIStatus.WaitingForApproval, "Stop" => AIStatus.Completed,
            "StopFailure" => AIStatus.Failed, "SessionEnd" or "Interrupt" => AIStatus.Ended,
            _ => (AIStatus?)null
        };
        if (next is null) return false;
        var key = e.Provider + ":" + e.SessionId;
        if (!sessions.TryGetValue(key, out var s))
        {
            if (sessions.Count >= 256) return false;
            sessions[key] = s = new AIProcess { Provider = e.Provider, SessionId = e.SessionId, StartedAt = e.At };
        }
        if (e.At <= s.LastEventAt) return false;
        if (e.Kind is "PreToolUse" or "PermissionRequest" && e.TurnId != null && e.TurnId == s.ReconciledTurnId) return false;
        if (s.Connection == ConnectionStatus.Offline && e.Kind is not ("SessionStart" or "UserPromptSubmit")) return false;
        if (e.Kind != "UserPromptSubmit" && e.TurnId != null && s.TurnId != null && e.TurnId != s.TurnId) return false;
        s.LastEventAt = e.At;
        s.AwaitingState = false;
        if (s.WorkingDirectory == null) s.WorkingDirectory = ProjectNames.Normalize(e.WorkingDirectory);
        if (s.Provider == "Codex" && ProjectNames.Normalize(e.TranscriptPath) is { } transcript) s.TranscriptPath = transcript;
        s.Connection = e.Kind == "SessionEnd" ? ConnectionStatus.Offline : ConnectionStatus.Online;
        if (e.ProcessId.HasValue && e.ProcessStartedAt.HasValue) { s.ProcessId = e.ProcessId; s.ProcessStartedAt = e.ProcessStartedAt; }
        if (e.ShellProcessId.HasValue && e.ShellProcessStartedAt.HasValue) { s.ShellProcessId = e.ShellProcessId; s.ShellProcessStartedAt = e.ShellProcessStartedAt; }
        if (e.Kind == "SessionEnd" && !s.Active) return true;
        if (e.Kind == "SessionStart" && s.Active) return false;
        if (!s.Active && e.Kind is "PostToolUse" or "PostToolUseFailure" or "Stop" or "StopFailure" or "SessionEnd" or "Interrupt") return false;
        if (e.Kind == "UserPromptSubmit" || (!s.Active && next is AIStatus.Executing or AIStatus.WaitingForApproval))
        { s.StartedAt = e.At; s.EndedAt = null; s.ActiveTools.Clear(); s.TurnId = e.TurnId; s.ReconciledTurnId = null; }
        if (e.Kind == "PreToolUse" && e.ToolId != null) s.ActiveTools.Add(e.ToolId);
        if (e.Kind is "PostToolUse" or "PostToolUseFailure")
        { if (e.ToolId != null) s.ActiveTools.Remove(e.ToolId); if (s.ActiveTools.Count > 0) next = AIStatus.Executing; }
        s.Status = next.Value;
        if (!s.Active && s.Status != AIStatus.Idle) { s.EndedAt = e.At; s.ActiveTools.Clear(); }
        return true;
    }
    public bool ReconcileCompletion(IslandEvent e)
    {
        // Only an explicit record for the active turn can repair a missing Hook.
        // Hook receipt time may be later than the CLI's completion timestamp.
        if (e.Provider != "Codex" || e.Kind is not ("Stop" or "Interrupt") || string.IsNullOrEmpty(e.TurnId) ||
            !sessions.TryGetValue(e.Provider + ":" + e.SessionId, out var s) || !s.Active ||
            s.TurnId != e.TurnId || e.At < s.StartedAt || e.At > DateTimeOffset.UtcNow.AddSeconds(5)) return false;
        s.Status = e.Kind == "Stop" ? AIStatus.Completed : AIStatus.Ended;
        s.EndedAt = e.At;
        s.ReconciledTurnId = e.TurnId;
        if (e.At > s.LastEventAt) s.LastEventAt = e.At;
        s.ActiveTools.Clear();
        return true;
    }
    public void Expire(DateTimeOffset now)
    {
        foreach (var key in sessions.Where(x => x.Value.Connection == ConnectionStatus.Offline && !x.Value.Active && now - x.Value.LastEventAt > TimeSpan.FromHours(1)).Select(x => x.Key).ToArray()) sessions.Remove(key);
    }
    public AIProcess[] Visible(DateTimeOffset now) => sessions.Values.Where(s => s.Active || (s.EndedAt.HasValue && now - s.EndedAt.Value < TimeSpan.FromSeconds(3))).OrderBy(s => s.StartedAt).ToArray();
}
public static class ProjectNames
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || value.Any(char.IsControl)) return null;
        try { return Path.IsPathFullyQualified(value) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value)) : null; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
    public static string Name(string? path) => path == null ? "未知项目" : Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } name ? name : path;
    public static string Display(AIProcess session, IEnumerable<AIProcess> peers)
    {
        var sameName = peers.Where(x => x.ProjectName.Equals(session.ProjectName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var label = session.ProjectName;
        if (sameName.Any(x => !string.Equals(x.WorkingDirectory, session.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) && session.WorkingDirectory != null)
            label += " · " + (Path.GetDirectoryName(session.WorkingDirectory) ?? session.WorkingDirectory);
        if (sameName.Count(x => string.Equals(x.WorkingDirectory, session.WorkingDirectory, StringComparison.OrdinalIgnoreCase)) > 1)
        {
            var length = Math.Min(8, session.SessionId.Length);
            while (length < session.SessionId.Length && sameName.Any(x => x != session && x.SessionId.StartsWith(session.SessionId[..length], StringComparison.Ordinal))) length = Math.Min(length + 4, session.SessionId.Length);
            label += " · " + session.SessionId[..length];
        }
        return label;
    }
}
