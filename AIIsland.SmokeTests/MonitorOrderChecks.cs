using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIIsland.Modules;
using AIIsland.Services;
using AIIsland.UI;

internal static class MonitorOrderChecks
{
    private static readonly string[] DefaultOrder = { "resources", "qqmusic", "neteasemusic", "clash", "balance" };
    private static readonly string[] CustomOrder = { "clash", "neteasemusic", "resources", "qqmusic", "balance" };

    public static async Task Run(string root)
    {
        CheckSettings();
        CheckEditor();
        await CheckSettingsWindow(root);
        await CheckViewModel();
        await CheckIslandWindow();
    }

    private static void CheckSettings()
    {
        var defaults = new Settings();
        Check(defaults.Codex && defaults.Claude && defaults.MonitorOrder.SequenceEqual(DefaultOrder),
            "New settings enable both Agent providers and use the default monitor order");
        var legacy = JsonSerializer.Deserialize<Settings>("{\"QQMusic\":false,\"Claude\":false}")!;
        Check(legacy.Codex && !legacy.Claude && !legacy.QQMusic && legacy.MonitorOrder.SequenceEqual(DefaultOrder),
            "Legacy settings gain monitor ordering while retaining explicitly disabled providers and monitors");
        Check(MonitorOrderCatalog.Normalize(null).SequenceEqual(DefaultOrder) &&
              MonitorOrderCatalog.Normalize(Array.Empty<string>()).SequenceEqual(DefaultOrder),
            "Missing and empty monitor orders restore all known monitors");
        Check(MonitorOrderCatalog.Normalize(new[] { "clash", "unknown", "clash", null!, "qqmusic", "ai", "codex", "claude" })
                .SequenceEqual(new[] { "clash", "qqmusic", "resources", "neteasemusic", "balance" }),
            "Order normalization removes duplicates, unknown/null IDs and Agents while appending missing monitors");
        var malformed = JsonSerializer.Deserialize<Settings>("{\"MonitorOrder\":null}")!;
        using (var model = new IslandViewModel(malformed))
            Check(Ids(model).SequenceEqual(DefaultOrder), "A null order from persisted settings safely restores defaults");
        defaults.MonitorOrder[0] = "unexpected";
        Check(new Settings().MonitorOrder.SequenceEqual(DefaultOrder), "Default monitor arrays are independent between settings objects");
        var saved = new Settings { MonitorOrder = CustomOrder.ToArray(), QQMusic = false, SystemResources = false };
        var restored = Roundtrip(saved);
        Check(restored.MonitorOrder.SequenceEqual(CustomOrder) && !restored.QQMusic && !restored.SystemResources &&
              restored.Codex && restored.Claude,
            "JSON persistence retains custom order independently of monitor selection");
    }

    private static void CheckEditor()
    {
        var settings = new Settings { MonitorOrder = CustomOrder.ToArray(), NetEaseMusic = false };
        var original = JsonSerializer.Serialize(settings);
        var editor = new MonitorOrderEditor(settings);
        Check(!editor.Move("clash", -1) && !editor.Move("balance", 1) && !editor.Move("unknown", 1) &&
              !editor.Move("resources", 0) && editor.Order.SequenceEqual(CustomOrder),
            "Moving beyond either edge or using an invalid move leaves ordering unchanged");
        var exported = editor.Order;
        exported[0] = "unexpected";
        Check(editor.Order.SequenceEqual(CustomOrder), "An exported editor order cannot mutate its internal draft");
        editor.SetMonitorEnabled("qqmusic", false);
        Check(editor.Move("neteasemusic", -1) && editor.Move("resources", 1) &&
              editor.Order.SequenceEqual(new[] { "neteasemusic", "clash", "qqmusic", "resources", "balance" }) &&
              !editor.IsMonitorEnabled("neteasemusic") && !editor.IsMonitorEnabled("qqmusic") &&
              editor.IsMonitorEnabled("resources") && editor.IsMonitorEnabled("clash"),
            "Reordering preserves each monitor's checkbox, including disabled monitors");
        editor.SetMonitorEnabled("qqmusic", true);
        Check(editor.IsMonitorEnabled("qqmusic") && JsonSerializer.Serialize(settings) == original,
            "Editing order and selections does not modify the current settings before save");
    }

    private static async Task CheckSettingsWindow(string root)
    {
        var settings = new Settings();
        var original = JsonSerializer.Serialize(settings);
        var saved = false;
        var closed = false;
        var window = new SettingsWindow(settings, _ => saved = true);
        var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Warning;
        try
        {
            // Render detached content only: no real settings file, registry, Hook or visible GUI is touched.
            var content = (FrameworkElement)window.Content;
            window.Content = null;
            var surface = new Border { Width = 360, Height = 565, Background = Brushes.White, Child = content };
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            surface.Measure(new Size(surface.Width, surface.Height));
            surface.Arrange(new Rect(0, 0, surface.Width, surface.Height));
            surface.UpdateLayout();
            var checks = Visuals<CheckBox>(surface).ToArray();
            Check(checks.Length >= 6 && checks[0].Content as string == "Codex" && checks[1].Content as string == "Claude" &&
                  checks[0].IsChecked == true && checks[1].IsChecked == true,
                "Settings show Codex and Claude selected above all sortable monitors");
            Check(Visuals<TextBlock>(surface).Any(t => t.Text.Contains("AI Agent") && t.Text.Contains("固定置顶")),
                "Settings label both providers as Agent monitoring fixed at the top");
            var editor = Visuals<MonitorOrderEditor>(surface).Single();
            var rows = Visuals<Grid>(editor).Where(g => g.Children.OfType<CheckBox>().Any()).ToArray();
            Check(rows.Select(g => (string)g.Children.OfType<CheckBox>().Single().Content)
                    .SequenceEqual(DefaultOrder.Select(MonitorOrderCatalog.Name)) &&
                  !rows.First().Children.OfType<Button>().First().IsEnabled &&
                  !rows.Last().Children.OfType<Button>().Last().IsEnabled,
                "The real editor renders rows in order with disabled edge arrows");
            var directory = Path.Combine(Path.GetFullPath(root), "artifacts", "screenshots");
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap(360, 565, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            using (var stream = File.Create(Path.Combine(directory, "settings-order.png")))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }
            editor.Move("qqmusic", -1);
            editor.SetMonitorEnabled("clash", false);
            checks[0].IsChecked = false;
            window.Close();
            closed = true;
            Check(!saved && JsonSerializer.Serialize(settings) == original,
                "Closing Settings without saving discards provider, checkbox and order changes");
            listener.Flush();
            Check(string.IsNullOrWhiteSpace(trace.ToString()), "Settings order controls render without WPF binding errors or warnings");
        }
        finally
        {
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
            if (!closed) window.Close();
        }
    }

    private static async Task CheckViewModel()
    {
        var settings = new Settings();
        using var model = new IslandViewModel(settings);
        var originalModules = model.ToolModules.ToDictionary(m => m.Id);
        model.Music.IsExpanded = false;
        settings.MonitorOrder = CustomOrder.ToArray();
        model.Configure(settings);
        Check(Ids(model).SequenceEqual(CustomOrder) && model.ToolModules.All(m => ReferenceEquals(m, originalModules[m.Id])) &&
              !model.Music.IsExpanded && model.ToolModules.All(m => m.Id != "ai"),
            "Applying settings immediately reorders existing module instances without losing expansion or including Agents");
        var processes = new ProcessSnapshot(new[] { new AppProcess(97, "QQMusic", null), new AppProcess(98, "cloudmusic", null) });
        // Deliberately do not call StartAsync: the synthetic processes cannot connect to live music sessions.
        await model.Music.RefreshAsync(processes, CancellationToken.None);
        await model.NetEaseMusic.RefreshAsync(processes, CancellationToken.None);
        Play(model.NetEaseMusic);
        Play(model.Music);
        Check(model.Music.IsPlaying && model.NetEaseMusic.IsPlaying && model.Music.LastStarted > model.NetEaseMusic.LastStarted &&
              Ids(model).SequenceEqual(CustomOrder),
            "A later QQ play event cannot override the user's manual order placing NetEase first");
        Play(model.NetEaseMusic);
        Check(model.NetEaseMusic.LastStarted > model.Music.LastStarted && Ids(model).SequenceEqual(CustomOrder),
            "Further playback priority notifications also retain the saved monitor order");
        settings.NetEaseMusic = false;
        model.Configure(settings);
        Check(!model.NetEaseMusic.IsVisible && Ids(model).SequenceEqual(CustomOrder),
            "Disabling a monitor hides its card without removing its saved position");
        var restored = Roundtrip(settings);
        using var restarted = new IslandViewModel(restored);
        await restarted.NetEaseMusic.RefreshAsync(processes, CancellationToken.None);
        Check(!restarted.NetEaseMusic.IsVisible && Ids(restarted).SequenceEqual(CustomOrder),
            "After a settings roundtrip and new view model, a disabled monitor retains its order");
        restored.NetEaseMusic = true;
        restarted.Configure(restored);
        await restarted.NetEaseMusic.RefreshAsync(processes, CancellationToken.None);
        Check(restarted.NetEaseMusic.IsVisible && Ids(restarted).SequenceEqual(CustomOrder),
            "Re-enabling a monitor restores it at the chosen position");
    }

    private static async Task CheckIslandWindow()
    {
        var settings = new Settings { MonitorOrder = CustomOrder.ToArray() };
        var window = new IslandWindow(initialSettings: settings, notificationSink: _ => { });
        try
        {
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            var cards = (ItemsControl)window.FindName("MonitorCards");
            var aiCard = (Border)window.FindName("AiCard");
            var tools = (StackPanel)window.FindName("ToolPanel");
            var parent = LogicalTreeHelper.GetParent(aiCard) as Panel;
            Check(ReferenceEquals(cards.ItemsSource, window.ViewModel.ToolModules) &&
                  cards.Items.Cast<ModuleBase>().Select(m => m.Id).SequenceEqual(CustomOrder),
                "The actual Island monitor card ItemsSource follows the saved order");
            Check(parent != null && ReferenceEquals(parent, LogicalTreeHelper.GetParent(tools)) &&
                  parent.Children.IndexOf(aiCard) < parent.Children.IndexOf(tools),
                "The actual Agent card stays above the sortable monitor panel");
            settings.MonitorOrder = DefaultOrder.ToArray();
            window.ViewModel.Configure(settings);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Check(cards.Items.Cast<ModuleBase>().Select(m => m.Id).SequenceEqual(DefaultOrder),
                "The real Island ItemsControl immediately follows a settings order change");
            await CheckCardTemplates(window);
        }
        finally { window.Close(); }
    }

    private static async Task CheckCardTemplates(IslandWindow window)
    {
        var model = window.ViewModel;
        foreach (var module in model.ToolModules)
        {
            typeof(ModuleBase).GetProperty(nameof(ModuleBase.IsVisible))!.SetValue(module, true);
            module.IsExpanded = true;
        }
        foreach (var music in new MusicModule[] { model.Music, model.NetEaseMusic })
        {
            typeof(MusicModule).GetProperty(nameof(MusicModule.DurationSeconds))!.SetValue(music, 245d);
            typeof(MusicModule).GetProperty(nameof(MusicModule.PositionSeconds))!.SetValue(music, 87d);
        }
        var cards = new ItemsControl {
            ItemTemplate = (DataTemplate)window.FindResource("MonitorCardTemplate"), ItemsSource = model.ToolModules
        };
        var surface = new Border { Width = 398, Padding = new Thickness(12), Child = cards, Resources = window.Resources };
        var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Warning;
        try
        {
            void Layout()
            {
                surface.Measure(new Size(398, double.PositiveInfinity));
                surface.Arrange(new Rect(surface.DesiredSize));
                surface.UpdateLayout();
            }
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Layout();
            Check(Visuals<SystemResourcesView>(surface).Count() == 1 && Visuals<ProgressBar>(surface).Count() == 4,
                "The shared monitor template selects one resource view and both expanded music views");
            foreach (var music in new MusicModule[] { model.Music, model.NetEaseMusic })
            {
                var container = cards.ItemContainerGenerator.ContainerFromItem(music);
                var progress = Visuals<ProgressBar>(container).Single();
                Check(progress.Value == 87 && progress.Maximum == 245 && progress.ActualWidth > 250,
                    music.DisplayName + " retains its read-only timeline and full card width through the new monitor template");
            }
            var proxyButton = Visuals<Button>(surface).Single(b => ReferenceEquals(b.Command, model.Clash.Toggle));
            typeof(ClashModule).GetProperty(nameof(ClashModule.IsOn))!.SetValue(model.Clash, true);
            Layout();
            var track = (Border)proxyButton.Template.FindName("Track", proxyButton);
            var thumb = (System.Windows.Shapes.Ellipse)proxyButton.Template.FindName("Thumb", proxyButton);
            Check(((SolidColorBrush)track.Background).Color == Color.FromRgb(83, 200, 152) && thumb.HorizontalAlignment == HorizontalAlignment.Right,
                "The actual Clash switch binds its on appearance directly to the module in the new template");
            typeof(ClashModule).GetProperty(nameof(ClashModule.IsOn))!.SetValue(model.Clash, false);
            Layout();
            Check(thumb.HorizontalAlignment == HorizontalAlignment.Left && ReferenceEquals(proxyButton.Command, model.Clash.Toggle),
                "Clash switch appearance follows an off update while retaining its command without invoking the real proxy");
            listener.Flush();
            Check(string.IsNullOrWhiteSpace(trace.ToString()),
                "Every production monitor card renders through the shared template without binding errors or warnings");
        }
        finally
        {
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
        }
    }

    private static void Play(MusicModule module)
    {
        var order = (MusicPlaybackOrder)typeof(MusicModule).GetField("playbackOrder", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
        order.Played();
        typeof(MusicModule).GetProperty(nameof(MusicModule.IsPlaying))!.SetValue(module, true);
        typeof(Observable).GetMethod("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(module, new object[] { nameof(MusicModule.LastStarted) });
    }

    private static Settings Roundtrip(Settings settings) => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
    private static string[] Ids(IslandViewModel model) => model.ToolModules.Select(m => m.Id).ToArray();
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
