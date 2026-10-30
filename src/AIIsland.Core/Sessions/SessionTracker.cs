using System.Diagnostics;
using System.Text.Json;

namespace AIIsland.Core.Sessions;

public sealed record ActivityEvent(
    string Provider,
    string SessionId,
    string EventName,
    DateTimeOffset At,
    int? ProcessId,
    DateTimeOffset? ProcessStarted,
    string? TurnId,
    string? ToolUseId,
    string? WorkingDirectory,
    string? TranscriptPath,
    int? TerminalId,
    DateTimeOffset? TerminalStarted,
    string? AgentId,
    string? ToolName = null,
    string? ToolDetail = null,
    string? RequestId = null,
    DateTimeOffset? RequestDeadline = null);

public enum SessionState
{
    Idle,
    Running,
    Waiting,
    Done,
    Failed,
    Ended,
}

public sealed record PendingPermission(string? RequestId, string? ToolUseId, string? ToolName,
    string? ToolDetail, DateTimeOffset At, DateTimeOffset Deadline)
{
    public bool Expired { get; internal set; }
}

public sealed class SessionItem
{
    private readonly List<PendingPermission> pending = new();
    public IReadOnlyList<PendingPermission> PendingRequests => pending;
    private PendingPermission? Current => pending.FirstOrDefault(p => p.RequestId is not null && !p.Expired) ?? pending.FirstOrDefault();
    public required string Provider { get; init; }
    public required string SessionId { get; init; }
    public string? WorkingDirectory { get; private set; }
    public SessionState State { get; private set; } = SessionState.Idle;
    public DateTimeOffset StartedAt { get; internal set; }
    public DateTimeOffset LastEventAt { get; internal set; }
    public int? TerminalId { get; private set; }
    public DateTimeOffset? TerminalStarted { get; private set; }
    public int? ProcessId { get; private set; }
    public DateTimeOffset? ProcessStarted { get; private set; }
    public string? PendingTool => Current?.ToolUseId;
    public string? PendingToolName => Current?.ToolName;
    public string? PendingToolDetail => Current?.ToolDetail;
    public string? PendingRequestId => Current is { Expired: false } request ? request.RequestId : null;
    public bool PermissionExpired => Current?.Expired == true;
    public string? LastTool { get; private set; }
    public string? TranscriptPath { get; private set; }
    public bool IsFinished => State is SessionState.Done or SessionState.Failed or SessionState.Ended;

    public void Apply(ActivityEvent e)
    {
        if (e.At > LastEventAt) LastEventAt = e.At;
        WorkingDirectory = e.WorkingDirectory ?? WorkingDirectory;
        TerminalId = e.TerminalId ?? TerminalId;
        TerminalStarted = e.TerminalStarted ?? TerminalStarted;
        ProcessId = e.ProcessId ?? ProcessId;
        ProcessStarted = e.ProcessStarted ?? ProcessStarted;
        TranscriptPath = e.TranscriptPath ?? TranscriptPath;

        switch (e.EventName)
        {
            case "SessionStart":
                StartedAt = StartedAt == default ? e.At : StartedAt;
                if (State is SessionState.Ended or SessionState.Done) State = SessionState.Idle;
                break;
            case "UserPromptSubmit":
                StartedAt = StartedAt == default ? e.At : StartedAt;
                State = SessionState.Running;
                pending.RemoveAll(p => p.At <= e.At);
                break;
            case "Thinking":
                State = SessionState.Running;
                break;
            case "PreToolUse":
                State = SessionState.Running;
                LastTool = e.ToolUseId;
                break;
            case "PermissionRequest":
                if (e.RequestId is not null && pending.Any(p => p.RequestId == e.RequestId)) break;
                var deadline = e.At.AddSeconds(60);
                if (e.RequestDeadline is { } supplied && supplied < deadline) deadline = supplied;
                pending.Add(new PendingPermission(e.RequestId, e.ToolUseId, e.ToolName, e.ToolDetail, e.At, deadline));
                pending.Sort((a, b) => a.At.CompareTo(b.At));
                break;
            case "PermissionExpired":
                foreach (var request in pending.Where(p => p.RequestId == e.RequestId)) request.Expired = true;
                break;
            case "PostToolUse":
            case "PostToolUseFailure":
                // Claude's PermissionRequest often has no tool id; guessing would cancel another tool.
                if (!string.IsNullOrEmpty(e.ToolUseId))
                    pending.RemoveAll(p => p.ToolUseId == e.ToolUseId && p.At <= e.At);
                State = e.EventName == "PostToolUse" ? SessionState.Running : SessionState.Failed;
                break;
            case "StopFailure":
                State = SessionState.Failed;
                pending.RemoveAll(p => p.At <= e.At);
                break;
            case "Stop":
                State = SessionState.Done;
                pending.RemoveAll(p => p.At <= e.At);
                break;
            case "Interrupt":
            case "SessionEnd":
                State = SessionState.Ended;
                pending.RemoveAll(p => p.At <= e.At);
                break;
            case "SubagentStart":
                if (State is SessionState.Idle or SessionState.Done) State = SessionState.Running;
                break;
        }
        if (pending.Count > 0) State = SessionState.Waiting;
    }

    internal void Expire(DateTimeOffset now)
    {
        foreach (var request in pending)
            if (request.RequestId is not null && now >= request.Deadline) request.Expired = true;
        var expired = pending.LastOrDefault(p => p.Expired);
        pending.RemoveAll(p => p.Expired && p != expired);
    }

    internal void Answered(string requestId)
    {
        pending.RemoveAll(p => p.RequestId == requestId);
        State = pending.Count > 0 ? SessionState.Waiting : SessionState.Running;
    }

    internal void End()
    {
        pending.Clear();
        State = SessionState.Ended;
    }
}

/// <summary>Consumes hook payloads from the shared inbox and folds them into sessions.</summary>
public sealed class SessionTracker
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<(string Provider, string SessionId), SessionItem> sessions = new();
    private readonly object gate = new();
    private readonly Dictionary<string, DateTimeOffset> seen = new(StringComparer.Ordinal);
    private readonly string inbox;
    private readonly string replies;
    private readonly TimeProvider clock;
    private DateTimeOffset lastPrune;
    private bool enabled = true;

    public SessionTracker(string? directory = null, TimeProvider? clock = null)
    {
        inbox = Path.Combine(directory ?? AppPaths.Data, "inbox");
        replies = Path.Combine(directory ?? AppPaths.Data, "replies");
        this.clock = clock ?? TimeProvider.System;
    }

    public event Action<SessionItem, ActivityEvent>? Changed;

    public IReadOnlyList<SessionItem> Snapshot()
    {
        lock (gate)
            return sessions.Values.OrderByDescending(s => s.PendingRequestId is not null).ThenByDescending(s => s.LastEventAt).ToArray();
    }

    public void Poll(bool enabled = true, int keepMinutes = 30)
    {
        var now = clock.GetUtcNow();
        lock (gate)
        {
            this.enabled = enabled;
            if (!enabled)
            {
                foreach (var item in sessions.Values) Cancel(item.PendingRequests, now);
                sessions.Clear();
                seen.Clear();
            }
            foreach (var item in sessions.Values) item.Expire(now);
            if (now - lastPrune >= TimeSpan.FromSeconds(5))
            {
                Prune(keepMinutes);
                lastPrune = now;
            }
        }
        if (!Directory.Exists(inbox)) return;
        foreach (var file in Directory.EnumerateFiles(inbox, "*.json").OrderBy(File.GetLastWriteTimeUtc).Take(256))
        {
            ActivityEvent? activity;
            try
            {
                var json = ReadShared(file);
                activity = JsonSerializer.Deserialize<ActivityEvent>(json, Options);
            }
            catch { activity = null; }

            try { File.Delete(file); } catch { }
            if (activity is null || string.IsNullOrEmpty(activity.Provider) || string.IsNullOrEmpty(activity.SessionId)) continue;

            lock (gate)
            {
                if (!enabled)
                {
                    if (activity.RequestId is not null && activity.EventName == "PermissionRequest" && activity.At.AddSeconds(60) > now)
                        TryCancel(activity.RequestId);
                    continue;
                }
                if (activity.At < now.AddMinutes(-Math.Clamp(keepMinutes, 1, 1440))) continue;
                var key = JsonSerializer.Serialize(new { activity.Provider, activity.SessionId, activity.At, activity.EventName, activity.RequestId, activity.ToolUseId, activity.TurnId, activity.AgentId });
                if (!seen.TryAdd(key, now)) continue;
                if (seen.Count > 4096)
                    foreach (var old in seen.OrderBy(p => p.Value).Take(256).Select(p => p.Key).ToArray()) seen.Remove(old);
                if (!sessions.TryGetValue((activity.Provider, activity.SessionId), out var item))
                {
                    if (activity.EventName == "PermissionExpired") continue;
                    if (sessions.Count >= 256)
                    {
                        var oldest = sessions.OrderBy(p => p.Value.PendingRequestId is not null).ThenBy(p => p.Value.LastEventAt).First();
                        Cancel(oldest.Value.PendingRequests, now);
                        sessions.Remove(oldest.Key);
                    }
                    item = new SessionItem { Provider = activity.Provider, SessionId = activity.SessionId, StartedAt = activity.At, LastEventAt = activity.At };
                    sessions[(activity.Provider, activity.SessionId)] = item;
                }
                var pending = item.PendingRequests.ToArray();
                item.Apply(activity);
                Cancel(pending.Where(p => !item.PendingRequests.Contains(p)), now);
                item.Expire(now);
                Changed?.Invoke(item, activity);
            }
        }
    }

    /// <summary>Answers a pending permission request. Returns false if it is no longer pending.</summary>
    public bool Answer(string provider, string sessionId, string requestId, bool allow, string? repliesDirectory = null)
    {
        lock (gate)
        {
            if (!enabled || !sessions.TryGetValue((provider, sessionId), out var item)) return false;
            item.Expire(clock.GetUtcNow());
            if (!item.PendingRequests.Any(p => p.RequestId == requestId && !p.Expired)) return false;
            PermissionReplies.Write(requestId, allow, repliesDirectory ?? replies);
            item.Answered(requestId);
            return true;
        }
    }

    public void Prune(int keepMinutes = 30)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var cutoff = now.AddMinutes(-Math.Clamp(keepMinutes, 1, 1440));
            foreach (var item in sessions.Values)
            {
                var alive = IsAlive(item.ProcessId ?? item.TerminalId, item.ProcessId is null ? item.TerminalStarted : item.ProcessStarted);
                if (!item.IsFinished && (alive == false || (alive is null && item.LastEventAt < cutoff)))
                {
                    Cancel(item.PendingRequests, now);
                    item.End();
                }
            }
            foreach (var key in sessions.Where(p => p.Value.IsFinished && p.Value.LastEventAt < cutoff).Select(p => p.Key).ToArray()) sessions.Remove(key);
            foreach (var key in seen.Where(p => p.Value < now.AddMinutes(-5)).Select(p => p.Key).ToArray()) seen.Remove(key);
        }
    }

    private void Cancel(IEnumerable<PendingPermission> requests, DateTimeOffset now)
    {
        foreach (var request in requests)
            if (request.RequestId is { } id && !request.Expired && request.Deadline > now) TryCancel(id);
    }

    private void TryCancel(string id)
    {
        try { PermissionReplies.Cancel(id, replies); } catch { }
    }

    private static bool? IsAlive(int? id, DateTimeOffset? started)
    {
        if (id is null) return null;
        try
        {
            using var process = Process.GetProcessById(id.Value);
            return !process.HasExited && (started is null || Math.Abs((process.StartTime.ToUniversalTime() - started.Value.UtcDateTime).TotalSeconds) < 1);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return null; }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
