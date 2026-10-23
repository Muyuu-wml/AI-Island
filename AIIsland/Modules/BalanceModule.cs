using System;
using System.Collections.Generic;
using System.Linq;
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
    private string email = "", password = "", username = "", pucnPassword = "";
    private string balance = "--", status = "请在设置中配置账户";
    private string todayCost = "--", usageStatus = "等待刷新";
    private string domesticBalance = "--", domesticTodayCost = "--";
    private bool running, started, automatic;
    private int generation;
    public override string Id => "balance";
    public string Balance { get => balance; private set => Set(ref balance, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string TodayCost { get => todayCost; private set => Set(ref todayCost, value); }
    public string UsageStatus { get => usageStatus; private set => Set(ref usageStatus, value); }
    public string DomesticBalance { get => domesticBalance; private set => Set(ref domesticBalance, value); }
    public string DomesticTodayCost { get => domesticTodayCost; private set => Set(ref domesticTodayCost, value); }
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
        var changed = email != settings.BalanceEmail || password != settings.BalancePassword ||
            username != settings.PucnUsername || pucnPassword != settings.PucnPassword;
        email = settings.BalanceEmail; password = settings.BalancePassword;
        username = settings.PucnUsername; pucnPassword = settings.PucnPassword;
        IsVisible = settings.BalanceEnabled;
        if (changed)
        {
            Balance = "--"; DomesticBalance = "--"; Status = "等待刷新";
            TodayCost = "--"; DomesticTodayCost = "--"; UsageStatus = "等待刷新";
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
        var token = requests.Token;
        var jobs = new List<Task<(string? Balance, string? Usage)>>();
        if (email.Length > 0 && password.Length > 0) jobs.Add(RefreshInternationalAsync(version, token));
        else { Balance = "--"; TodayCost = "--"; }
        if (username.Length > 0 && pucnPassword.Length > 0) jobs.Add(RefreshDomesticAsync(version, token));
        else { DomesticBalance = "--"; DomesticTodayCost = "--"; }
        try
        {
            if (jobs.Count == 0) { Status = "请在设置中配置账户"; UsageStatus = "等待刷新"; return; }
            Status = "正在刷新…"; UsageStatus = "正在刷新…";
            var results = await Task.WhenAll(jobs);
            if (version != generation || Disposed) return;
            var stamp = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
            var balanceErrors = results.Select(r => r.Balance).OfType<string>().ToArray();
            var usageErrors = results.Select(r => r.Usage).OfType<string>().ToArray();
            Status = balanceErrors.Length == 0 ? stamp : string.Join("；", balanceErrors);
            UsageStatus = usageErrors.Length == 0 ? stamp : string.Join("；", usageErrors);
        }
        catch (OperationCanceledException) { if (version == generation && !Disposed) Status = "请求超时，请重试（保留上次数据）"; }
        catch (Exception e) { if (version == generation && !Disposed) Status = Reason(e) + "（保留上次数据）"; }
        finally
        {
            running = false; CommandManager.InvalidateRequerySuggested();
            if (version != generation && !Disposed && IsVisible) _ = FetchAsync();
        }
    }
    private async Task<(string? Balance, string? Usage)> RefreshInternationalAsync(int version, CancellationToken token)
    {
        try
        {
            var value = await service.ReadAsync(email, password, token);
            if (version == generation && !Disposed) Balance = value.ToString("$0.00");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return ("国际：" + Reason(e) + (Balance == "--" ? "" : "（保留上次余额）"), null); }
        try
        {
            var usage = await service.ReadUsageAsync(email, password, token);
            if (version == generation && !Disposed) TodayCost = usage.TodayCost.ToString("$0.00");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return (null, "国际：" + Reason(e) + (TodayCost == "--" ? "" : "（保留上次消耗）")); }
        return (null, null);
    }
    private async Task<(string? Balance, string? Usage)> RefreshDomesticAsync(int version, CancellationToken token)
    {
        try
        {
            var usage = await service.ReadDomesticAsync(username, pucnPassword, token);
            if (version == generation && !Disposed)
            {
                DomesticBalance = usage.Balance.ToString("$0.00");
                DomesticTodayCost = usage.TodayCost.ToString("$0.00");
            }
            return (null, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { var message = "国内：" + Reason(e); return (message, message); }
    }
    private static string Reason(Exception e) => e is InvalidOperationException ? e.Message : "刷新失败，请检查网络或本地存储。";
    public override void Dispose() { base.Dispose(); timer.Stop(); requests.Cancel(); service.Dispose(); requests.Dispose(); }
}
