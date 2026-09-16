using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows.Input;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIIsland.Core;
using AIIsland.Services;

namespace AIIsland.Modules;
public sealed class AiRow : Observable
{
    public string SessionId { get; }
    private string project = "", status = "", elapsed = "", details = "", connection = "";
    private bool expanded;
    private AIProcess? process;
    private string navigationNote = "";
    public string NavigationNote { get => navigationNote; private set => Set(ref navigationNote, value); }
    public ICommand OpenTerminal { get; }
    public string StatusColor => Status == "等待确认" ? "#F3BD65" : Status.Contains("Failed") ? "#F18B91" : Status.Contains("Completed") ? "#81E4BA" : "#B6C6DE";
    public string Project { get => project; private set => Set(ref project, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Elapsed { get => elapsed; private set => Set(ref elapsed, value); }
    public string Details { get => details; private set => Set(ref details, value); }
    public string Connection { get => connection; private set => Set(ref connection, value); }
    public bool IsExpanded { get => expanded; set => Set(ref expanded, value); }
    public AiRow(string session)
    {
        SessionId = session;
        OpenTerminal = new RelayCommand(() => { NavigationNote = TerminalNavigator.Open(process?.ProcessId, process?.ProcessStartedAt); if (NavigationNote.Length > 0) IsExpanded = true; });
    }
    public void Update(AIProcess s, string label, DateTimeOffset now)
    {
        process = s;
        Project = label;
        Status = Label(s, now);
        Changed(nameof(StatusColor));
        Connection = s.Connection == ConnectionStatus.Unknown ? "连接状态未知" : "";
        var duration = s.Status == AIStatus.Idle ? TimeSpan.Zero : s.Elapsed(now);
        Elapsed = s.AwaitingState || Status == "等待输入" ? "" : $"{Math.Max(0, (int)duration.TotalMinutes):00}:{Math.Max(0, duration.Seconds):00}";
        Details = $"{s.WorkingDirectory ?? "未知项目目录"}\nPID  {s.ProcessId?.ToString() ?? "未知"}    Started  {s.StartedAt.LocalDateTime:HH:mm:ss}\nSession  {s.SessionId}";
    }
    public static string Label(AIProcess s, DateTimeOffset now)
    {
        if (s.AwaitingState) return "等待状态同步";
        if (!s.Active && s.Connection != ConnectionStatus.Offline && s.EndedAt.HasValue && now - s.EndedAt.Value >= TimeSpan.FromSeconds(3)) return "等待输入";
        return s.Status switch { AIStatus.Thinking => "思考中", AIStatus.Executing => "执行中", AIStatus.WaitingForApproval => "等待确认", AIStatus.Completed => "✓ Completed", AIStatus.Failed => "⚠ Failed", AIStatus.Ended => "已结束", _ => "等待输入" };
    }
}
public sealed class AiGroup
{
    public string Name { get; }
    public ObservableCollection<AiRow> Rows { get; } = new();
    public AiGroup(string name) => Name = name;
}
public sealed class AiModule : ModuleBase
{
    private readonly IslandState state = new();
    private readonly EventInbox inbox;
    private readonly EventBus bus = new();
    private readonly CodexCompletionReader completionReader = new();
    private DateTimeOffset nextCompletionCheck;
    private Settings settings;
    private DateTimeOffset acceptAfter = DateTimeOffset.UtcNow;
    private string summary = "等待 AI 任务", compact = "";
    private bool paused;
    private int unlinkedCount;
    private bool startupRecovery = true;
    private readonly Dictionary<string, DateTimeOffset> lastSignals = new();
    public string IntegrationStatus => string.Join("\n", new[] { Integration("Codex", settings.Codex), Integration("Claude", settings.Claude) });
    private string Integration(string provider, bool enabled) => provider + " · " + (!enabled ? "已禁用" : Paused ? "监控已暂停" : lastSignals.TryGetValue(provider, out var time) ? "已收到事件 · 最近 " + time.LocalDateTime.ToString("MM-dd HH:mm:ss") : state.Sessions.Any(s => s.Provider == provider && s.Connection == ConnectionStatus.Online) ? "已恢复在线会话 · 等待新 Hook 同步任务状态" : "尚未收到事件 · 请安装 Hook，并在已有 CLI 中提交任务；首次安装 Hook 后需重启 CLI");
    public bool NeedsAttention => state.Sessions.Any(s => s.Status == AIStatus.WaitingForApproval);
    public string AccentColor => NeedsAttention ? "#F3BD65" : "#81E4BA";
    public override string Id => "ai";
    public ObservableCollection<AiGroup> Groups { get; } = new();
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public string Compact { get => compact; private set => Set(ref compact, value); }
    public bool Paused { get => paused; private set => Set(ref paused, value); }
    public bool HasRows => Groups.Count > 0;
    public int MonitoredCount => state.Sessions.Count(s => s.Connection != ConnectionStatus.Offline);
    public int ActiveCount => state.Sessions.Count(s => s.Active);
    public bool HasActivity => state.Visible(DateTimeOffset.UtcNow).Length > 0;
    public event Action<IslandEvent>? Completed;
    public event Action<IslandEvent>? ApprovalRequested;
    public AiModule(Settings settings, EventInbox? inbox = null)
    {
        this.inbox = inbox ?? new EventInbox();
        this.settings = settings; IsVisible = true;
        bus.Received += e => {
            if (Paused || e.At < acceptAfter || e.Provider == "Codex" && !this.settings.Codex || e.Provider == "Claude" && !this.settings.Claude) return;
            var wasWaiting = state.Sessions.Any(s => s.Provider == e.Provider && s.SessionId == e.SessionId && s.Status == AIStatus.WaitingForApproval);
            if (state.Apply(e))
            {
                lastSignals[e.Provider] = e.At;
                var session = state.Sessions.First(s => s.Provider == e.Provider && s.SessionId == e.SessionId);
                var notification = e with { WorkingDirectory = session.WorkingDirectory };
                if (e.Kind is "Stop" or "StopFailure") Completed?.Invoke(notification);
                if (e.Kind == "PermissionRequest" && !wasWaiting) ApprovalRequested?.Invoke(notification);
            }
        };
    }
    public void Configure(Settings value) { settings = value; if (!value.Codex) state.RemoveProvider("Codex"); if (!value.Claude) state.RemoveProvider("Claude"); Update(); }
    public void TogglePause() { Paused = !Paused; startupRecovery = false; acceptAfter = DateTimeOffset.UtcNow; state.Clear(); Update(); }
    private void Recover(IslandEvent value)
    {
        if (!startupRecovery || Paused || value.Provider == "Codex" && !settings.Codex || value.Provider == "Claude" && !settings.Claude ||
            state.Sessions.Any(s => s.Provider == value.Provider && s.SessionId == value.SessionId)) return;
        if (state.Apply(value with { Kind = "SessionStart", TurnId = null, ToolId = null, TranscriptPath = null }))
            state.Sessions.First(s => s.Provider == value.Provider && s.SessionId == value.SessionId).AwaitingState = true;
    }
    public override Task RefreshAsync(ProcessSnapshot processes, CancellationToken token)
    {
        if (Disposed) return Task.CompletedTask;
        try { inbox.Drain(bus.Publish, Recover); }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { Note = "无法读取 AI 事件：" + e.Message; }
        foreach (var session in state.Sessions.Where(s => s.Connection != ConnectionStatus.Offline).ToArray())
        {
            if (!session.ProcessId.HasValue || !session.ProcessStartedAt.HasValue) { session.Connection = ConnectionStatus.Unknown; continue; }
            var ended = false;
            try {
                using var process = Process.GetProcessById(session.ProcessId.Value);
                ended = process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()) != session.ProcessStartedAt;
                if (!ended) session.Connection = ConnectionStatus.Online;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { ended = true; }
            catch (System.ComponentModel.Win32Exception) { session.Connection = ConnectionStatus.Unknown; }
            if (ended) state.Apply(new IslandEvent(session.Provider, session.SessionId, "SessionEnd", DateTimeOffset.UtcNow));
        }
        var now = DateTimeOffset.UtcNow;
        unlinkedCount = processes.Apps.Count(p =>
            (settings.Codex && p.Name.Equals("codex", StringComparison.OrdinalIgnoreCase) || settings.Claude && p.Name.Equals("claude", StringComparison.OrdinalIgnoreCase)) &&
            !state.Sessions.Any(s => s.ProcessId == p.Id && s.Connection != ConnectionStatus.Offline));
        if (now >= nextCompletionCheck)
        {
            nextCompletionCheck = now.AddSeconds(1);
            foreach (var session in state.Sessions.Where(s => s.Active && s.Provider == "Codex").ToArray())
            {
                var completion = completionReader.Read(session);
                if (completion != null && state.ReconcileCompletion(completion) && completion.Kind == "Stop")
                    Completed?.Invoke(completion with { WorkingDirectory = session.WorkingDirectory });
            }
        }
        state.Expire(now); Update(); return Task.CompletedTask;
    }
    private void Update()
    {
        var now = DateTimeOffset.UtcNow;
        var visible = state.Visible(now);
        var count = visible.Count(s => s.Active);
        Summary = Paused ? "AI 监控已暂停" : MonitoredCount == 0 && visible.Length == 0
            ? unlinkedCount > 0 ? $"发现 {unlinkedCount} 个 Agent · 等待 Hook 接入" : "等待 AI Agent 接入"
            : $"正在监控 {MonitoredCount} 个 Agent · " + (count > 0 ? $"{count} 个处理中" : state.Sessions.Any(s => s.AwaitingState && s.Connection != ConnectionStatus.Offline) ? "等待状态同步" : visible.Length > 0 ? "任务已结束" : "空闲");
        Compact = Summary;
        if (!Paused && NeedsAttention) Summary = $"{state.Sessions.Count(s => s.Status == AIStatus.WaitingForApproval)} 个 Agent 等待你确认";
        foreach (var provider in new[] { "Codex", "Claude" })
        {
            var sessions = state.Sessions.Where(s => s.Provider == provider && (s.Connection != ConnectionStatus.Offline || visible.Contains(s))).OrderBy(s => s.StartedAt).ToArray();
            var group = Groups.FirstOrDefault(g => g.Name == provider);
            if (sessions.Length == 0) { if (group != null) Groups.Remove(group); continue; }
            if (group == null) { group = new AiGroup(provider); Groups.Add(group); }
            foreach (var old in group.Rows.Where(r => sessions.All(s => s.SessionId != r.SessionId)).ToArray()) group.Rows.Remove(old);
            foreach (var session in sessions)
            {
                var row = group.Rows.FirstOrDefault(r => r.SessionId == session.SessionId);
                if (row == null) { row = new AiRow(session.SessionId); group.Rows.Add(row); }
                row.Update(session, ProjectNames.Display(session, sessions), now);
            }
        }
        Changed(nameof(HasRows)); Changed(nameof(HasActivity)); Changed(nameof(MonitoredCount)); Changed(nameof(ActiveCount));
        Changed(nameof(IntegrationStatus)); Changed(nameof(NeedsAttention)); Changed(nameof(AccentColor));
    }
}
