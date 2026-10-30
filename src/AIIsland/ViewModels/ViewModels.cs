using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AIIsland.Core;
using AIIsland.Core.Monitoring;
using AIIsland.Core.Sessions;
using AIIsland.Services;

namespace AIIsland.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class LineVm
{
    public string Title { get; init; } = "";
    public double? Percent { get; init; }
    public string Value { get; init; } = "--";
    public string Suffix { get; init; } = "";
    public string Note { get; init; } = "";
    public string Reset { get; init; } = "";
    public Visibility ResetVisibility => string.IsNullOrEmpty(Reset) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoteVisibility => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class ColumnVm : Observable
{
    private string name = "";
    private Brush accent = Brushes.DodgerBlue;
    private double? peak;
    private string peakText = "--";
    private string status = "";
    private string plan = "";
    private string today = "";
    private string week = "";
    private IReadOnlyList<DailyUsage> usageDays = Array.Empty<DailyUsage>();
    private bool hasError;
    private string error = "";
    private bool ringFull;
    private bool showUsage = true;

    public bool IsRelay { get; init; }
    public ObservableCollection<LineVm> Lines { get; } = new();

    public string Name { get => name; set => Set(ref name, value); }
    public Brush Accent { get => accent; set => Set(ref accent, value); }
    public double? Peak { get => peak; set { if (Set(ref peak, value)) Raise(nameof(PeakVisibility)); } }
    public Visibility PeakVisibility => Peak is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Balance-only sources have no percentage, so the ring is drawn as a solid accent circle.</summary>
    public bool RingFull { get => ringFull; private set => Set(ref ringFull, value); }

    /// <summary>Hidden when the column has no usage source at all (e.g. DeepSeek only exposes a balance).</summary>
    public bool ShowUsage { get => showUsage; private set => Set(ref showUsage, value); }
    public string PeakText { get => peakText; set => Set(ref peakText, value); }
    public string Status { get => status; set => Set(ref status, value); }
    public string Plan { get => plan; set => Set(ref plan, value); }
    public string Today { get => today; set => Set(ref today, value); }
    public string Week { get => week; set => Set(ref week, value); }
    public IReadOnlyList<DailyUsage> UsageDays { get => usageDays; private set => Set(ref usageDays, value); }
    public bool HasError { get => hasError; set => Set(ref hasError, value); }
    public string Error { get => error; set => Set(ref error, value); }

    public void Update(ColumnSnapshot snapshot)
    {
        Name = string.IsNullOrWhiteSpace(snapshot.Settings.Name)
            ? SourceCatalog.Find(snapshot.Settings.QuotaSource)?.Name ?? snapshot.Settings.QuotaSource
            : snapshot.Settings.Name;
        Accent = Theme.Brush(snapshot.Settings.Accent);
        var quota = snapshot.Quota;
        if (quota is not null)
        {
            Peak = quota.Peak;
            PeakText = quota.Peak is { } value ? value.ToString("0") + "%" : "--";
            Plan = quota.Plan ?? "";
            Status = quota.State switch
            {
                SourceState.Ok => quota.Status ?? "",
                SourceState.Stale => "离线 · " + quota.ReadAt.ToString("HH:mm"),
                _ => quota.Error ?? quota.Status ?? "",
            };
            HasError = quota.State == SourceState.Error;
            Error = HasError ? (quota.Error ?? quota.Status ?? "") : "";
        }

        Lines.Clear();
        if (quota is not null)
            foreach (var line in quota.Lines)
                Lines.Add(new LineVm
                {
                    Title = line.Title,
                    Percent = line.Percent,
                    Value = line.Value ?? "--",
                    Suffix = line.Suffix ?? "",
                    Note = line.Note ?? "",
                    Reset = line.Reset ?? "",
                });

        Today = FormatUsage("今日", snapshot.Today);
        Week = FormatUsage("7 日", snapshot.Week);
        UsageDays = DailyUsage.From(snapshot.Records, DateOnly.FromDateTime(DateTime.Now));
        ShowUsage = snapshot.Today.Requests > 0 || snapshot.Week.Requests > 0 ||
                    !string.IsNullOrWhiteSpace(snapshot.Settings.UsageSource);

        RingFull = !HasError && quota is not null && quota.Peak is null &&
                   quota.Lines.Any(line => line.Percent is null && !string.IsNullOrEmpty(line.Value));
    }

    public void MarkError(string message)
    {
        HasError = true;
        Error = message;
        Status = message;
        Peak = null;
        PeakText = "--";
        RingFull = false;
    }

    private static string FormatUsage(string label, UsageTotals totals)
    {
        if (totals.Requests == 0) return label + " --";
        var tokens = Human.Tokens(totals.TotalTokens);
        return totals.Cost > 0 ? $"{label} {tokens} · {Human.Money(totals.Cost)}" : $"{label} {tokens}";
    }
}

public sealed class SessionVm : Observable
{
    private string detail = "";
    private string stateText = "";
    private Brush stateBrush = Brushes.Gray;
    private string time = "";
    private string title = "";
    private bool waiting;
    private string? requestId;
    private string toolText = "";

    public string Provider { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string? WorkingDirectory { get; init; }
    public int? TerminalId { get; init; }
    public int? ProcessId { get; init; }
    public string? TranscriptPath { get; init; }

    public string Title { get => title; set => Set(ref title, value); }
    public string Detail { get => detail; set => Set(ref detail, value); }
    public string StateText { get => stateText; set => Set(ref stateText, value); }
    public Brush StateBrush { get => stateBrush; set => Set(ref stateBrush, value); }
    public string Time { get => time; set => Set(ref time, value); }
    public bool Waiting { get => waiting; set => Set(ref waiting, value); }

    /// <summary>Tool call awaiting approval, e.g. "Bash · npm test".</summary>
    public string ToolText
    {
        get => toolText;
        set
        {
            if (Set(ref toolText, value)) Raise(nameof(ToolVisibility));
        }
    }

    public Visibility ToolVisibility => toolText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string? RequestId
    {
        get => requestId;
        set
        {
            if (Set(ref requestId, value)) Raise(nameof(DecisionVisibility));
        }
    }

    public Visibility DecisionVisibility => requestId is null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed class MonitorVm : Observable
{
    private string cpu = "--";
    private string memory = "--";
    private string network = "";
    private string disks = "";
    private double cpuPercent;
    private double memoryPercent;
    private string compactDown = "";
    private string compactUp = "";
    private bool hasNetwork;
    private bool showCpuRing;
    private bool showMemRing;

    public string Cpu { get => cpu; set => Set(ref cpu, value); }
    public string Memory { get => memory; set => Set(ref memory, value); }
    public string Network { get => network; set => Set(ref network, value); }
    public string Disks { get => disks; set => Set(ref disks, value); }
    public double CpuPercent { get => cpuPercent; set => Set(ref cpuPercent, value); }
    public double MemoryPercent { get => memoryPercent; set => Set(ref memoryPercent, value); }

    /// <summary>Short "↓ 59.7K/s" strings for the collapsed bar; arrows are colored separately.</summary>
    public string CompactDown { get => compactDown; set => Set(ref compactDown, value); }
    public string CompactUp { get => compactUp; set => Set(ref compactUp, value); }
    public bool HasNetwork { get => hasNetwork; set => Set(ref hasNetwork, value); }

    /// <summary>Compact CPU / memory rings follow the same toggles as the system page.</summary>
    public bool ShowCpuRing { get => showCpuRing; set => Set(ref showCpuRing, value); }
    public bool ShowMemRing { get => showMemRing; set => Set(ref showMemRing, value); }

    public Visibility DisksVisibility => string.IsNullOrEmpty(Disks) ? Visibility.Collapsed : Visibility.Visible;

    public void Update(MonitorSample sample, MonitorSettings settings)
    {
        CpuPercent = sample.CpuPercent ?? 0;
        MemoryPercent = sample.MemoryTotal > 0 ? sample.MemoryUsed * 100.0 / sample.MemoryTotal : 0;

        Cpu = sample.CpuPercent is { } value ? "CPU " + value.ToString("0") + "%" : "CPU --";
        Memory = sample.MemoryTotal > 0
            ? $"内存 {Human.Bytes(sample.MemoryUsed)} / {Human.Bytes(sample.MemoryTotal)}"
            : "内存 --";
        Network = sample.DownBytesPerSecond is { } down
            ? $"↓ {Human.Rate(down)}  ↑ {Human.Rate(sample.UpBytesPerSecond ?? 0)}"
            : "";
        if (settings.ShowNetwork && sample.DownBytesPerSecond is { } compactDown)
        {
            CompactDown = "↓ " + Human.RateCompact(compactDown);
            CompactUp = "↑ " + Human.RateCompact(sample.UpBytesPerSecond ?? 0);
            HasNetwork = true;
        }
        else
        {
            CompactDown = "";
            CompactUp = "";
            HasNetwork = false;
        }
        Disks = settings.ShowDisks
            ? string.Join("   ", sample.Disks.Select(d => $"{d.Name} {Human.Bytes(d.Total - d.Used)} 可用"))
            : "";
        Raise(nameof(DisksVisibility));

        if (!settings.ShowCpu) Cpu = "";
        if (!settings.ShowMemory) Memory = "";
        if (!settings.ShowNetwork) Network = "";
        ShowCpuRing = settings.ShowCpu;
        ShowMemRing = settings.ShowMemory;
    }
}

public sealed class ProcessVm : Observable
{
    private static readonly Brush Low = Theme.Brush("#34D399");
    private static readonly Brush Medium = Theme.Brush("#F5A524");
    private static readonly Brush High = Theme.Brush("#F05252");

    private string name = "";
    private string cpu = "";
    private string memory = "";
    private double cpuValue;
    private ImageSource? icon;

    public string Name
    {
        get => name;
        set
        {
            if (!Set(ref name, value)) return;
            Raise(nameof(Initial));
        }
    }

    public string Cpu { get => cpu; set => Set(ref cpu, value); }
    public string Memory { get => memory; set => Set(ref memory, value); }
    public long MemoryBytes { get; init; }

    public double CpuValue
    {
        get => cpuValue;
        set
        {
            if (!Set(ref cpuValue, value)) return;
            Raise(nameof(CpuBrush));
        }
    }

    public ImageSource? Icon
    {
        get => icon;
        set
        {
            if (!Set(ref icon, value)) return;
            Raise(nameof(IconVisibility));
            Raise(nameof(InitialVisibility));
        }
    }

    public string Initial => name.Length > 0 ? name[..1].ToUpperInvariant() : "?";
    public Brush CpuBrush => CpuValue >= 70 ? High : CpuValue >= 30 ? Medium : Low;
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility InitialVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class MusicVm : Observable
{
    private string title = "未在播放";
    private string artist = "";
    private string position = "";
    private string duration = "";
    private double progress;
    private bool playing;
    private ImageSource? cover;

    public string Title { get => title; set => Set(ref title, value); }
    public string Artist { get => artist; set => Set(ref artist, value); }
    public string Position { get => position; set => Set(ref position, value); }
    public string Duration { get => duration; set => Set(ref duration, value); }
    public double Progress { get => progress; set => Set(ref progress, value); }
    public bool IsPlaying { get => playing; set { if (Set(ref playing, value)) Raise(nameof(PlayGlyph)); } }
    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";
    public ImageSource? Cover
    {
        get => cover;
        set
        {
            if (!Set(ref cover, value)) return;
            Raise(nameof(CoverVisibility));
            Raise(nameof(GlyphVisibility));
        }
    }

    public Visibility CoverVisibility => Cover is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Cover is null ? Visibility.Visible : Visibility.Collapsed;
}

public enum ProcessSort
{
    Cpu,
    Memory,
}

public sealed class IslandViewModel : Observable
{
    private const int ProcessRows = 8;

    private string clock = "";
    private bool hasSessions;
    private string compactSessions = "";
    private ProcessSort processSort = ProcessSort.Memory;
    private bool processDescending = true;
    private readonly List<ProcessSample> processSamples = new();

    public ObservableCollection<ColumnVm> Columns { get; } = new();
    public ObservableCollection<SessionVm> Sessions { get; } = new();
    public ObservableCollection<ProcessVm> Processes { get; } = new();
    public MonitorVm Monitor { get; } = new();
    public MusicVm Music { get; } = new();

    public string Clock { get => clock; set => Set(ref clock, value); }
    public bool HasSessions { get => hasSessions; set => Set(ref hasSessions, value); }
    public string CompactSessions { get => compactSessions; set => Set(ref compactSessions, value); }

    public string CpuHeader => "CPU" + (processSort == ProcessSort.Cpu ? (processDescending ? " ↓" : " ↑") : "");
    public string MemoryHeader => "内存" + (processSort == ProcessSort.Memory ? (processDescending ? " ↓" : " ↑") : "");

    /// <summary>Clicking the CPU or memory header sorts by it; clicking the active one flips direction.</summary>
    public void SortProcesses(string key)
    {
        var next = string.Equals(key, "memory", StringComparison.OrdinalIgnoreCase) ? ProcessSort.Memory : ProcessSort.Cpu;
        if (next == processSort) processDescending = !processDescending;
        else
        {
            processSort = next;
            processDescending = true;
        }
        Raise(nameof(CpuHeader));
        Raise(nameof(MemoryHeader));
        RenderProcesses();
    }

    public void SetSessions(IReadOnlyList<SessionItem> items)
    {
        var live = items.Where(item => item.State != SessionState.Ended).Take(6).ToArray();
        if (live.Length != Sessions.Count || live.Where((item, index) => !Matches(Sessions[index], item)).Any())
        {
            Sessions.Clear();
            foreach (var item in live) Sessions.Add(ToVm(item));
        }
        else
        {
            for (var index = 0; index < live.Length; index++) Refresh(Sessions[index], live[index]);
        }

        HasSessions = live.Length > 0;
        var running = live.Count(item => item.State == SessionState.Running);
        var waiting = live.Count(item => item.State == SessionState.Waiting);
        CompactSessions = waiting > 0 ? $"{waiting} 个等待确认" : running > 0 ? $"{running} 个任务进行中" : "";
    }

    public void SetProcesses(IReadOnlyList<ProcessSample> samples)
    {
        processSamples.Clear();
        processSamples.AddRange(samples);
        RenderProcesses();
    }

    private void RenderProcesses()
    {
        var ordered = processSort switch
        {
            ProcessSort.Memory => processDescending
                ? processSamples.OrderByDescending(sample => sample.WorkingSet)
                : processSamples.OrderBy(sample => sample.WorkingSet),
            _ => processDescending
                ? processSamples.OrderByDescending(sample => sample.CpuPercent)
                : processSamples.OrderBy(sample => sample.CpuPercent),
        };
        var samples = ordered.ThenBy(sample => sample.Name, StringComparer.OrdinalIgnoreCase)
            .Take(ProcessRows)
            .ToArray();

        var same = Processes.Count == samples.Length &&
                   samples.Select((sample, index) => Processes[index].Name.Equals(sample.Name, StringComparison.OrdinalIgnoreCase)).All(x => x);

        if (!same)
        {
            Processes.Clear();
            foreach (var sample in samples)
                Processes.Add(new ProcessVm { Name = sample.Name, MemoryBytes = sample.WorkingSet });
        }

        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var target = Processes[index];
            target.CpuValue = sample.CpuPercent;
            target.Cpu = sample.CpuPercent.ToString("0.#") + "%";
            target.Memory = Human.Bytes(sample.WorkingSet);
            target.Icon = ProcessIcons.Get(sample.Path);
        }
    }

    private static SessionVm ToVm(SessionItem item)
    {
        var vm = new SessionVm
        {
            Provider = item.Provider,
            SessionId = item.SessionId,
            WorkingDirectory = item.WorkingDirectory,
            TerminalId = item.TerminalId,
            ProcessId = item.ProcessId,
            TranscriptPath = item.TranscriptPath,
        };
        Refresh(vm, item);
        return vm;
    }

    private static bool Matches(SessionVm vm, SessionItem item) =>
        vm.Provider == item.Provider && vm.SessionId == item.SessionId;

    private static void Refresh(SessionVm vm, SessionItem item)
    {
        vm.Title = ShortDirectory(item.WorkingDirectory) ?? item.SessionId[..Math.Min(8, item.SessionId.Length)];
        vm.StateText = item.State switch
        {
            SessionState.Idle => "空闲",
            SessionState.Running => "进行中",
            SessionState.Waiting => item.PermissionExpired ? "已超时 · 请在终端确认" : "等待确认",
            SessionState.Done => "已完成",
            SessionState.Failed => "失败",
            _ => "已结束",
        };
        vm.StateBrush = Theme.Brush(item.State switch
        {
            SessionState.Waiting => "#F5A524",
            SessionState.Running => "#5B8CFF",
            SessionState.Done => "#31C48D",
            SessionState.Failed => "#F05252",
            _ => "#8B93A7",
        });
        vm.Waiting = item.State == SessionState.Waiting;
        vm.RequestId = vm.Waiting ? item.PendingRequestId : null;
        vm.ToolText = vm.Waiting && item.PendingToolName is { Length: > 0 } tool
            ? tool + (item.PendingToolDetail is { Length: > 0 } detail ? " · " + detail : "")
            : "";
        if (vm.Waiting && item.PendingRequests.Count > 1) vm.ToolText += $"（共 {item.PendingRequests.Count} 项待确认）";
        vm.Detail = item.Provider + (string.IsNullOrEmpty(item.WorkingDirectory) ? "" : " · " + Path.GetFileName(item.WorkingDirectory.TrimEnd('\\', '/')));
        vm.Time = item.LastEventAt.ToLocalTime().ToString("HH:mm:ss");
    }

    private static string? ShortDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

public static class Theme
{
    public static Brush Brush(string? hex)
    {
        try
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex ?? "#5B8CFF")!;
            brush.Freeze();
            return brush;
        }
        catch
        {
            return Brushes.DodgerBlue;
        }
    }
}
