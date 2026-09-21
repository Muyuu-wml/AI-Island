using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using AIIsland.Services;

namespace AIIsland.Modules;

public sealed class BalanceModule : ModuleBase
{
    private readonly BalanceService service;
    private readonly DispatcherTimer timer = new();
    private CancellationTokenSource requests = new();
    private string email = "", password = "", balance = "--", status = "请在设置中配置账户";
    private string todayCost = "--", usageStatus = "等待刷新";
    private bool running, started, automatic;
    private int generation;
    public override string Id => "balance";
    public string Balance { get => balance; private set => Set(ref balance, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string TodayCost { get => todayCost; private set => Set(ref todayCost, value); }
    public string UsageStatus { get => usageStatus; private set => Set(ref usageStatus, value); }
    public ICommand Refresh { get; }
    public BalanceModule(BalanceService? service = null)
    {
        this.service = service ?? new BalanceService();
        Refresh = Action(FetchAsync, () => !running);
        timer.Tick += async (_, _) => await FetchAsync();
    }
    public void Configure(Settings settings)
    {
        generation++;
        requests.Cancel(); requests.Dispose(); requests = new();
        var changed = email != settings.BalanceEmail || password != settings.BalancePassword;
        email = settings.BalanceEmail; password = settings.BalancePassword;
        IsVisible = settings.BalanceEnabled;
        if (changed)
        {
            Balance = "--"; Status = "等待刷新";
            TodayCost = "--"; UsageStatus = "等待刷新";
        }
        timer.Stop();
        automatic = settings.BalanceRefreshSeconds > 0;
        if (automatic)
        {
            timer.Interval = TimeSpan.FromSeconds(Math.Clamp(settings.BalanceRefreshSeconds, 10, 86400));
            if (started && IsVisible) timer.Start();
        }
        if (started && IsVisible) _ = FetchAsync();
    }
    public override Task StartAsync(CancellationToken token)
    {
        started = true;
        if (IsVisible && automatic) timer.Start();
        _ = FetchAsync();
        return Task.CompletedTask;
    }
    public override Task RefreshAsync(ProcessSnapshot processes, CancellationToken token) => Task.CompletedTask;
    private async Task FetchAsync()
    {
        if (running || !IsVisible || Disposed) return;
        running = true;
        var version = generation;
        Status = "正在刷新…";
        try
        {
            var value = await service.ReadAsync(email, password, requests.Token);
            if (version != generation || Disposed) return;
            Balance = value.ToString("$0.00");
            Status = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
            UsageStatus = "正在刷新…";
            try
            {
                var usage = await service.ReadUsageAsync(email, password, requests.Token);
                if (version != generation || Disposed) return;
                TodayCost = usage.TodayCost.ToString("$0.00");
                UsageStatus = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception e)
            {
                if (version == generation && !Disposed)
                    UsageStatus = (e is OperationCanceledException ? "消耗查询超时，请重试。" :
                        e is InvalidOperationException ? e.Message : "消耗查询失败，请检查网络或本地存储。") +
                        (TodayCost == "--" ? "" : "（保留上次消耗）");
            }
        }
        catch (OperationCanceledException) { if (version == generation && !Disposed) Status = "请求超时，请重试（保留上次余额）"; }
        catch (Exception e)
        {
            if (version == generation && !Disposed) Status = (e is InvalidOperationException ? e.Message : "刷新失败，请检查网络或本地存储。") + (Balance == "--" ? "" : "（保留上次余额）");
        }
        finally
        {
            running = false; CommandManager.InvalidateRequerySuggested();
            if (version != generation && !Disposed && IsVisible) _ = FetchAsync();
        }
    }
    public override void Dispose() { base.Dispose(); timer.Stop(); requests.Cancel(); service.Dispose(); requests.Dispose(); }
}
