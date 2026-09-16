using System.Threading;
using System.Linq;
using System.Windows;
using AIIsland.UI;
namespace AIIsland;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Exercise real WPF shutdown in the published bundle without touching live monitoring or settings.
        if (e.Args.Contains("--shutdown-check"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new IslandWindow(true, new Services.Settings {
                Codex = false, Claude = false, QQMusic = false, NetEaseMusic = false,
                Clash = false, SystemResources = false, BalanceEnabled = false
            }, _ => { });
            MainWindow = window;
            // No Show(): no Loaded handler, Hook reads, network requests or visible test window.
            Dispatcher.BeginInvoke(new System.Action(window.ExitFromTray));
            return;
        }
        instance = new Mutex(true, @"Local\AIIsland-" + System.Environment.UserName, out var first);
        if (!first) { Shutdown(); return; }
        MainWindow = new IslandWindow(e.Args.Contains("--inspect")); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
