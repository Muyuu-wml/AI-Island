using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIIsland.UI;
using AIIsland.Modules;
using AIIsland.Services;

internal static class MusicChecks
{
    public sealed class PreviewCard
    {
        public string DisplayName { get; init; } = "";
        public bool IsVisible { get; init; }
        public bool IsExpanded { get; set; }
        public bool IsPlaying { get; init; }
        public ImageSource? Cover { get; init; }
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public double DurationSeconds { get; init; }
        public double PositionSeconds { get; init; }
        public bool HasTimeline { get; init; }
        public string PositionLabel { get; init; } = "";
        public string DurationLabel { get; init; } = "";
        public string PlayLabel { get; init; } = "";
        public string Note { get; init; } = "";
        public RelayCommand Previous { get; } = new(() => { });
        public RelayCommand Toggle { get; } = new(() => { });
        public RelayCommand Next { get; } = new(() => { });
        public RelayCommand Open { get; } = new(() => { });
    }
    public static async Task Run(string root, bool probe = false)
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }
        var qqOrder = new MusicPlaybackOrder(); var netEaseOrder = new MusicPlaybackOrder();
        qqOrder.Observe(true); netEaseOrder.Observe(true);
        Check(qqOrder.LastStarted == 0 && netEaseOrder.LastStarted == 0, "Initial discovery does not invent historical playback order");
        qqOrder.Observe(false); qqOrder.Observe(true);
        netEaseOrder.Observe(false); netEaseOrder.Observe(true);
        Check(netEaseOrder.LastStarted > qqOrder.LastStarted, "The player started later wins when both are playing");
        var last = netEaseOrder.LastStarted; netEaseOrder.Observe(true); qqOrder.Observe(true);
        Check(netEaseOrder.LastStarted == last && netEaseOrder.LastStarted > qqOrder.LastStarted, "Repeated playback updates and track refreshes do not change priority");
        qqOrder.Played();
        Check(qqOrder.LastStarted > netEaseOrder.LastStarted, "An accepted play command promotes that player");
        var now = DateTimeOffset.UtcNow;
        var progress = MusicProgress.Calculate(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(210), TimeSpan.FromSeconds(40), now.AddSeconds(-5), now, true, 1);
        Check(progress.Position == 35 && progress.Duration == 200, "Progress interpolates playback relative to the timeline start");
        Check(MusicProgress.Calculate(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(40), now.AddSeconds(-5), now, false, 1).Position == 40, "Paused playback does not advance the progress bar");
        Check(MusicProgress.Calculate(TimeSpan.Zero, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(199), now.AddSeconds(-5), now, true, 1).Position == 200 &&
            MusicProgress.Calculate(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(40), now, now, true, 1).Duration == 0,
            "Progress clamps at song end and handles missing durations");
        using var music = new NetEaseMusicModule();
        var present = new ProcessSnapshot(new[] { new AppProcess(99, "cloudmusic", null) });
        foreach (var name in new[] { "QQMusic", "cloudmusicupdater", "cloudmusichelper" })
        {
            await music.RefreshAsync(new ProcessSnapshot(new[] { new AppProcess(98, name, null) }), CancellationToken.None);
            Check(!music.IsVisible, name + " does not create a NetEase music card");
        }
        await music.RefreshAsync(present, CancellationToken.None);
        Check(music.IsVisible && !music.Previous.CanExecute(null) && !music.Toggle.CanExecute(null) && !music.Next.CanExecute(null) && !music.Open.CanExecute(null),
            "NetEase running without a media session shows its card with unavailable controls disabled");
        music.Configure(false);
        await music.RefreshAsync(present, CancellationToken.None);
        Check(!music.IsVisible, "Disabling NetEase hides its card while its process is running");
        music.Configure(true);
        await music.RefreshAsync(present, CancellationToken.None);
        Check(music.IsVisible, "Re-enabling NetEase restores its running card");
        await music.RefreshAsync(new ProcessSnapshot(Array.Empty<AppProcess>()), CancellationToken.None);
        Check(!music.IsVisible, "NetEase card disappears after process exit");
        var settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { QQMusic = true, NetEaseMusic = false }))!;
        Check(settings.QQMusic && !settings.NetEaseMusic && JsonSerializer.Deserialize<Settings>("{}")!.NetEaseMusic,
            "NetEase setting persists independently of QQ and defaults on for existing configurations");
        await RenderCards(root, Check);
        if (probe)
        {
            foreach (var live in new MusicModule[] { new QQMusicModule(), new NetEaseMusicModule() })
            using (live)
            {
                await live.StartAsync(CancellationToken.None);
                await live.RefreshAsync(await new ProcessDiscovery().ReadAsync(CancellationToken.None), CancellationToken.None);
                Console.WriteLine($"{live.DisplayName} live probe: visible={live.IsVisible}, playing={live.IsPlaying}, cover={live.Cover != null}, progress={live.PositionLabel}/{live.DurationLabel}, toggle={live.Toggle.CanExecute(null)}, title={live.Title}, note={live.Note}");
            }
        }
    }
    private static async Task RenderCards(string root, Action<bool, string> check)
    {
        var owner = new IslandWindow(notificationSink: _ => { });
        try
        {
            await CheckRealMusicCards(owner, check);
            var cover = new DrawingImage(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(59, 85, 159), Color.FromRgb(226, 142, 177), 45), null, new RectangleGeometry(new Rect(0, 0, 100, 100))));
            var cards = new ItemsControl { ItemTemplate = (DataTemplate)owner.FindResource("MusicCardTemplate"), ItemsSource = new[] {
                new PreviewCard { DisplayName = "网易云音乐", IsVisible = true, IsExpanded = true, IsPlaying = true, Cover = cover, Title = "示例歌曲 · 最近开始播放", Artist = "示例歌手", DurationSeconds = 245d, PositionSeconds = 87d, HasTimeline = true, PositionLabel = "01:27", DurationLabel = "04:05", PlayLabel = "暂停", Note = "" },
                new PreviewCard { DisplayName = "QQ 音乐", IsVisible = true, IsExpanded = true, IsPlaying = false, Cover = null, Title = "另一首示例歌曲", Artist = "示例歌手 · 暂停中", DurationSeconds = 210d, PositionSeconds = 42d, HasTimeline = true, PositionLabel = "00:42", DurationLabel = "03:30", PlayLabel = "播放", Note = "" }
            }};
            var surface = new Border { Width = 398, Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(20, 26, 40)), Child = cards, Resources = owner.Resources };
            surface.Measure(new Size(398, double.PositiveInfinity)); surface.Arrange(new Rect(surface.DesiredSize)); surface.UpdateLayout();
            var bitmap = new RenderTargetBitmap(398, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
            var directory = Path.Combine(root, "artifacts", "screenshots"); Directory.CreateDirectory(directory);
            using var stream = File.Create(Path.Combine(directory, "music-cards.png"));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
            Console.WriteLine("PASS Shared music card template renders cover, placeholder, progress and controls");
        }
        finally { owner.Close(); }
    }

    private static async Task CheckRealMusicCards(IslandWindow owner, Action<bool, string> check)
    {
        using var qq = new QQMusicModule();
        using var netEase = new NetEaseMusicModule();
        var modules = new MusicModule[] { qq, netEase };
        var processes = new ProcessSnapshot(new[] { new AppProcess(97, "QQMusic", null), new AppProcess(98, "cloudmusic", null) });
        foreach (var module in modules)
        {
            // No StartAsync: these synthetic processes never connect to a real media session.
            await module.RefreshAsync(processes, CancellationToken.None);
            // Exercise the production properties and notifications without making their setters public.
            SetTimeline(module, 245, 87);
            module.IsExpanded = true;
        }

        var cards = new ItemsControl { ItemTemplate = (DataTemplate)owner.FindResource("MusicCardTemplate"), ItemsSource = modules };
        var surface = new Border { Width = 398, Padding = new Thickness(12), Child = cards, Resources = owner.Resources };
        void Layout()
        {
            surface.Measure(new Size(398, double.PositiveInfinity));
            surface.Arrange(new Rect(surface.DesiredSize));
            surface.UpdateLayout();
        }
        // Keep the surface detached: showing the owner would start its normal Agent/Hook monitoring.
        Layout();
        foreach (var module in modules)
        {
            var container = cards.ItemContainerGenerator.ContainerFromItem(module);
            var progress = FindVisual<ProgressBar>(container);
            var expander = FindVisual<Expander>(container);
            check(progress != null && expander?.IsExpanded == true && progress.Value == 87 && progress.Maximum == 245,
                module.DisplayName + " expanded production card renders its read-only timeline without a binding exception");

            SetTimeline(module, 300, 95);
            Layout();
            check(progress!.Value == 95 && progress.Maximum == 300,
                module.DisplayName + " progress follows production timeline property changes");
        }
    }

    private static void SetTimeline(MusicModule module, double duration, double position)
    {
        typeof(MusicModule).GetProperty(nameof(MusicModule.DurationSeconds))!.SetValue(module, duration);
        typeof(MusicModule).GetProperty(nameof(MusicModule.PositionSeconds))!.SetValue(module, position);
    }

    private static T? FindVisual<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        if (parent == null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}
