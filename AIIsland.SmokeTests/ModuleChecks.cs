using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AIIsland.Modules;
using AIIsland.Services;

internal static class ModuleChecks
{
    private sealed class FakeProxy : ISystemProxyService
    {
        public ProxySnapshot State = new(3, "127.0.0.1:7897", "<local>;example.com", "");
        public int Writes;
        public bool Fail;
        public ProxySnapshot Read() => State;
        public void SetEnabled(ProxySnapshot before, string server, bool enabled)
        {
            if (Fail) throw new IOException("模拟写入失败");
            Writes++; State = State with { Flags = enabled ? 3 : 1, Server = enabled ? server : before.Server };
        }
    }
    public static async Task Run(string root)
    {
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); }
        var directory = Path.Combine(root, "artifacts", "proxy-test"); Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "clash-verge.yaml"), "mixed-port: 7897\r\nport: 7899\r\n");
        File.WriteAllText(Path.Combine(directory, "verge.yaml"), "enable_proxy_guard: false\r\n");
        var fake = new FakeProxy(); using var module = new ClashModule(fake, directory);
        var present = new ProcessSnapshot(new[] { new AppProcess(42, "clash-verge", null) });
        var absent = new ProcessSnapshot(Array.Empty<AppProcess>());
        await module.RefreshAsync(present, CancellationToken.None);
        Check(module.IsVisible && module.IsOn && module.Toggle.CanExecute(null), "Clash running enables its card and owned proxy command");
        async Task Toggle() { module.Toggle.Execute(null); for (var i = 0; module.IsBusy && i < 100; i++) await Task.Delay(10); Check(!module.IsBusy, "Proxy command finishes asynchronously"); }
        await Toggle();
        Check(!module.IsOn && fake.Writes == 1 && fake.State.Bypass == "<local>;example.com", "Turning proxy off preserves endpoint and bypass");
        await Toggle(); Check(module.IsOn && fake.Writes == 2, "Turning proxy on reads back actual state");
        fake.State = fake.State with { Server = "localhost:9999" };
        await Toggle(); Check(fake.Writes == 2 && module.Note.Contains("其他代理"), "Concurrent external proxy change is not overwritten");
        await module.RefreshAsync(present, CancellationToken.None);
        Check(!module.Toggle.CanExecute(null), "Foreign proxy disables toggle");
        fake.State = new(3, "127.0.0.1:7897", "<local>", ""); fake.Fail = true;
        await module.RefreshAsync(present, CancellationToken.None); await Toggle();
        Check(module.IsOn && module.Note.Contains("模拟写入失败"), "Write failure does not show fake success");
        await module.RefreshAsync(absent, CancellationToken.None); Check(!module.IsVisible, "Clash card disappears after process exit");
        using var music = new QQMusicModule();
        await music.RefreshAsync(new ProcessSnapshot(new[] { new AppProcess(99, "QQMusicUp", null) }), CancellationToken.None);
        Check(!music.IsVisible, "QQ updater does not create a music card");
        await music.RefreshAsync(new ProcessSnapshot(new[] { new AppProcess(99, "QQMusic", null) }), CancellationToken.None);
        Check(music.IsVisible && !music.Toggle.CanExecute(null), "Missing media session disables music commands");
    }
}
