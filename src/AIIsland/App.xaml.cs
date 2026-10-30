using System.Threading;
using System.Windows;
using AIIsland.Core;
using AIIsland.Services;
using AIIsland.UI;

namespace AIIsland;

public partial class App : Application
{
    private Mutex? instance;
    private TrayIcon? tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, @"Local\AIIsland-" + Environment.UserName, out var first);
        if (!first)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Write(args.Exception);
            args.Handled = true;
        };

        var store = new SettingsStore();
        var settings = store.Load();
        AppPaths.EnsureData();

        var window = new IslandWindow(store, settings);
        MainWindow = window;
        if (e.Args.Contains("--settings"))
            window.Loaded += (_, _) => window.OpenSettings();
        window.Show();

        tray = new TrayIcon(window);
        window.Tray = tray;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        tray?.Dispose();
        instance?.Dispose();
        base.OnExit(e);
    }
}
