using System.Threading;
using System.Windows;
using AIIsland.UI;
namespace AIIsland;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\AIIsland-" + System.Environment.UserName, out var first);
        if (!first) { Shutdown(); return; }
        MainWindow = new IslandWindow(); MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
