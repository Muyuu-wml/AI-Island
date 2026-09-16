using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using AIIsland.Services;

namespace AIIsland.Modules;
public sealed class ClashModule : ModuleBase
{
    private readonly ISystemProxyService proxy;
    private readonly string configDirectory;
    private AppProcess? app;
    private string? endpoint;
    private bool guard, enabled = true, isOn, canToggle;
    private string status = "正在读取系统代理";
    private ProxySnapshot? last;
    public override string Id => "clash";
    public string Status { get => status; private set => Set(ref status, value); }
    public bool IsOn { get => isOn; private set { if (Set(ref isOn, value)) Changed(nameof(ToggleLabel)); } }
    public string ToggleLabel => IsOn ? "关闭系统代理" : "开启系统代理";
    public ICommand Toggle { get; }
    public ICommand Open { get; }
    public ClashModule(ISystemProxyService? service = null, string? directory = null)
    {
        proxy = service ?? new SystemProxyService();
        configDirectory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "io.github.clash-verge-rev.clash-verge-rev");
        Toggle = Action(ToggleAsync, () => canToggle);
        Open = Action(() => ProcessDiscovery.OpenAsync(app), () => app?.Path != null);
    }
    public void Configure(bool value) { enabled = value; if (!value) IsVisible = false; }
    private (string? Endpoint, bool Guard) ReadConfig()
    {
        var config = File.ReadAllText(Path.Combine(configDirectory, "clash-verge.yaml"));
        var mixed = Regex.Match(config, @"(?m)^mixed-port:\s*(\d+)\s*(?:#.*)?$");
        var http = Regex.Match(config, @"(?m)^port:\s*(\d+)\s*(?:#.*)?$");
        var port = mixed.Success && mixed.Groups[1].Value != "0" ? mixed.Groups[1].Value : http.Groups[1].Value;
        var verge = File.ReadAllText(Path.Combine(configDirectory, "verge.yaml"));
        var guarded = Regex.IsMatch(verge, @"(?m)^enable_proxy_guard:\s*true\s*(?:#.*)?$");
        return (int.TryParse(port, out var p) && p is > 0 and <= 65535 ? $"127.0.0.1:{p}" : null, guarded);
    }
    public override async Task RefreshAsync(ProcessSnapshot processes, CancellationToken token)
    {
        app = processes.Find("clash-verge"); IsVisible = enabled && app != null;
        if (!IsVisible || IsBusy || Disposed) return;
        try
        {
            var result = await Task.Run(() => (Config: ReadConfig(), State: proxy.Read()), token);
            if (Disposed) return;
            endpoint = result.Config.Endpoint; guard = result.Config.Guard; last = result.State;
            IsOn = last.Enabled && endpoint != null && ProxyPolicy.Matches(last.Server, endpoint);
            var conflict = ProxyPolicy.Conflict(last, endpoint, guard);
            canToggle = conflict == null;
            Status = conflict ?? (IsOn ? "Windows 系统代理已开启" : "Windows 系统代理已关闭");
        }
        catch (Exception e) when (e is not OperationCanceledException) { canToggle = false; Status = "状态不可用：" + e.Message; }
        CommandManager.InvalidateRequerySuggested();
    }
    private async Task ToggleAsync()
    {
        var desired = !IsOn;
        var result = await Task.Run(() => {
            var config = ReadConfig(); var before = proxy.Read();
            var conflict = ProxyPolicy.Conflict(before, config.Endpoint, config.Guard);
            if (conflict != null) throw new InvalidOperationException(conflict);
            // Do not invert a state changed by another application since the last refresh.
            if (last == null || before != last) throw new InvalidOperationException("代理状态已被其他应用修改，请刷新后重试。");
            proxy.SetEnabled(before, config.Endpoint!, desired);
            var after = proxy.Read();
            if (after.Enabled != desired || desired && !ProxyPolicy.Matches(after.Server, config.Endpoint!)) throw new InvalidOperationException("系统未保留此设置，可能被其他应用覆盖。");
            return after;
        });
        if (Disposed) return;
        last = result; IsOn = desired; Status = desired ? "Windows 系统代理已开启" : "Windows 系统代理已关闭";
    }
}
