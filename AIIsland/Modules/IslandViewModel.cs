using System;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AIIsland.Services;

namespace AIIsland.Modules;
public sealed class IslandViewModel : Observable, IDisposable
{
    private readonly ProcessDiscovery discovery = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer aiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly IIslandModule[] modules;
    private bool refreshing, drawerOpen;
    public AiModule Ai { get; }
    public QQMusicModule Music { get; } = new();
    public NetEaseMusicModule NetEaseMusic { get; } = new();
    public ObservableCollection<ModuleBase> ToolModules { get; } = new();
    public ClashModule Clash { get; } = new();
    public SystemResourcesModule Resources { get; } = new();
    public BalanceModule Balance { get; } = new();
    // DrawerOpen controls only manual music/proxy controls.
    public bool DrawerOpen { get => drawerOpen; set { if (Set(ref drawerOpen, value)) { Changed(nameof(BodyVisible)); Changed(nameof(ShowResourceCompact)); } } }
    public bool ShowResourceCompact => Resources.IsVisible && !DrawerOpen;
    public bool AiExpanded => Ai.HasActivity;
    public bool BodyVisible => AiExpanded || DrawerOpen;
    public bool ShowCompact => true;
    public string Heading => Ai.Paused ? "AI 已暂停" : "AI Island";
    public event Action? LayoutChanged;
    public IslandViewModel(Settings settings)
    {
        Ai = new(settings); modules = new IIslandModule[] { Ai, Resources, Music, NetEaseMusic, Clash, Balance }; Configure(settings);
        foreach (var module in modules) module.PropertyChanged += (_, _) => { Changed(nameof(Heading)); Changed(nameof(AiExpanded)); Changed(nameof(BodyVisible)); Changed(nameof(ShowResourceCompact)); LayoutChanged?.Invoke(); };
        timer.Tick += async (_, _) => await RefreshAsync();
        aiTimer.Tick += async (_, _) => { if (!lifetime.IsCancellationRequested) await Ai.RefreshAsync(new ProcessSnapshot(Array.Empty<AppProcess>()), lifetime.Token); };
    }
    public async Task StartAsync()
    {
        aiTimer.Start();
        try { await Task.WhenAll(modules.Select(m => m.StartAsync(lifetime.Token))); if (!lifetime.IsCancellationRequested) { await RefreshAsync(); timer.Start(); } }
        catch (OperationCanceledException) { }
    }
    public void Configure(Settings settings)
    {
        Balance.Configure(settings);
        Ai.Configure(settings); Resources.Configure(settings.SystemResources); Music.Configure(settings.QQMusic); NetEaseMusic.Configure(settings.NetEaseMusic); Clash.Configure(settings.Clash);
        var order = MonitorOrderCatalog.Normalize(settings.MonitorOrder);
        for (var i = 0; i < order.Length; i++)
        {
            var module = modules.OfType<ModuleBase>().Single(m => m.Id == order[i]);
            var index = ToolModules.IndexOf(module);
            if (index < 0) ToolModules.Insert(i, module);
            else if (index != i) ToolModules.Move(index, i);
        }
        LayoutChanged?.Invoke();
    }
    public async Task RefreshAsync()
    {
        if (refreshing || lifetime.IsCancellationRequested) return;
        refreshing = true;
        try
        {
            var snapshot = await discovery.ReadAsync(lifetime.Token);
            await Task.WhenAll(modules.Where(m => m != Ai).Select(m => m.RefreshAsync(snapshot, lifetime.Token)));
            Changed(nameof(ShowCompact)); LayoutChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (System.ComponentModel.Win32Exception) { }
        finally { refreshing = false; }
    }
    public void Dispose() { timer.Stop(); aiTimer.Stop(); lifetime.Cancel(); foreach (var module in modules) module.Dispose(); }
}
