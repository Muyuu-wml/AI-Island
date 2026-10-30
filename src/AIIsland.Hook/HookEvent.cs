namespace AIIsland.Hook;

/// <summary>Wire format shared with <c>AIIsland.Core.Sessions.ActivityEvent</c>.</summary>
internal sealed record HookEvent(
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
