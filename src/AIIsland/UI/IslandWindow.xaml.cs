using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIIsland.Core;
using AIIsland.Core.Monitoring;
using AIIsland.Core.Sessions;
using AIIsland.Services;
using AIIsland.ViewModels;

namespace AIIsland.UI;

public partial class IslandWindow : Window
{
    private readonly SettingsStore store;
    private readonly SecretStore secrets;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ColumnService columns = new();
    private readonly SessionTracker tracker = new();
    private readonly SystemMonitor monitor = new();
    private readonly ProcessMonitor processes = new();
    private readonly MusicService music = new();
    private readonly IslandViewModel vm = new();
    private readonly DispatcherTimer fast;
    private readonly Dictionary<string, SessionState> lastStates = new();
    private readonly HashSet<string> activeAlerts = new();
    private System.Threading.Mutex? sessionSignal;
    private int settingsVersion;
    private const double ExpandedWidth = 620;
    private const double CompactHeightBase = 44;

    private IslandSettings settings;
    private DateTime lastQuota = DateTime.MinValue;
    private DateTime lastMonitor = DateTime.MinValue;
    private DateTime lastMusic = DateTime.MinValue;
    private DateTime sizeAnimUntil = DateTime.MinValue;
    private int sizeAnimVersion;
    private double compactWidth = 400;
    private double compactHeight = 44;
    private bool refreshing;
    private bool expanded;
    private bool pinned;
    private bool hiddenForFullscreen;
    private int monitorTicks;
    private int page;
    private Point dragStart;
    private RadioButton? dragTab;
    private bool draggingTab;

    public TrayIcon? Tray { get; set; }

    public IslandWindow(SettingsStore store, IslandSettings settings)
    {
        this.store = store;
        this.settings = settings;
        secrets = SecretStore.Load();

        InitializeComponent();
        DataContext = vm;

        ExpandedPanel.RenderTransform = new TranslateTransform(0, 10);
        ExpandedPanel.Opacity = 0;
        foreach (var panel in new FrameworkElement[] { PageAi, PageSystem, PageMusic })
            panel.RenderTransform = new TranslateTransform();
        ApplyTabOrder();

        ApplyAppearance();
        tracker.Changed += OnSessionChanged;
        ApplySessionSettings();
        _ = music.StartAsync();

        fast = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        fast.Tick += FastTick;
        fast.Start();

        _ = Task.Run(SampleProcessesAsync);

        Loaded += (_, _) =>
        {
            Place();
            _ = RefreshColumnsAsync();
        };

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Collapse(); };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closing += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            lifetime.Cancel();
            fast.Stop();
            sessionSignal?.Dispose();
            sessionSignal = null;
            tracker.Poll(enabled: false);
        };
    }

    private void ApplyAppearance()
    {
        var look = settings.Appearance;
        var palette = Palettes.Find(look.Theme);
        Resources["PanelBrush"] = Theme.Brush(look.Panel);
        Resources["TextBrush"] = Theme.Brush(look.Text);
        Resources["MutedBrush"] = Theme.Brush(look.Muted);
        Resources["AccentBrush"] = Theme.Brush(palette.Accent);
        Resources["CpuBrush"] = Theme.Brush(palette.Cpu);
        Resources["MemoryBrush"] = Theme.Brush(palette.Memory);
        Resources["NetDownBrush"] = Theme.Brush(palette.Down);
        Resources["NetUpBrush"] = Theme.Brush(palette.Up);
        Resources["MusicBrush"] = Theme.Brush(palette.Music);
        Resources["CardBrush"] = Theme.Brush(Mix(look.Panel, "#FFFFFF", 0.06));
        Resources["LineBrush"] = Theme.Brush(Mix(look.Panel, "#FFFFFF", 0.12));
        Resources["WarnBrush"] = Theme.Brush("#F5A524");
        Resources["DangerBrush"] = Theme.Brush("#F05252");

        Shell.CornerRadius = settings.Style switch
        {
            CompactStyle.Capsule => new CornerRadius(18),
            CompactStyle.Indicator => new CornerRadius(0, 0, 14, 14),
            _ => new CornerRadius(0, 0, 20, 20),
        };
        CompactPanel.Margin = settings.Style == CompactStyle.Capsule ? new Thickness(16, 7, 16, 7) : new Thickness(18, 8, 18, 8);
        CompactDivider.Visibility = settings.Style == CompactStyle.Indicator ? Visibility.Collapsed : Visibility.Visible;
        CompactSessionsText.Visibility = settings.Style == CompactStyle.Indicator ? Visibility.Collapsed : Visibility.Visible;

        compactWidth = settings.Style switch
        {
            CompactStyle.Capsule => 450,
            CompactStyle.Indicator => 320,
            _ => 460,
        };
        compactHeight = CompactHeightBase;
        if (!expanded) SetSize(compactWidth, compactHeight);
    }

    private void SetSize(double width, double height)
    {
        sizeAnimVersion++;
        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        Shell.Width = width;
        Shell.Height = height;
        Width = ExpandedWidth;
        Height = height;
    }

    // The window stays at its widest and only the Shell inside it animates: resizing a
    // layered window every frame reallocates its surface and moves it, which stutters.
    // The window grows before a taller animation and shrinks after a shorter one, and
    // its transparent margins let clicks through to whatever is underneath.
    private void AnimateSize(double width, double height, int milliseconds = 240)
    {
        var version = ++sizeAnimVersion;
        sizeAnimUntil = DateTime.Now.AddMilliseconds(milliseconds + 40);
        if (height > Height) Height = height;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var heightAnimation = new DoubleAnimation(height, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease };
        heightAnimation.Completed += (_, _) =>
        {
            if (version == sizeAnimVersion) Height = height;
        };
        Shell.BeginAnimation(WidthProperty, new DoubleAnimation(width, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease });
        Shell.BeginAnimation(HeightProperty, heightAnimation);
    }

    private double MeasureExpandedHeight()
    {
        ExpandedPanel.Measure(new Size(ExpandedWidth, double.PositiveInfinity));
        return Math.Ceiling(ExpandedPanel.DesiredSize.Height) + 2;
    }

    private static void Fade(UIElement element, double from, double to, int milliseconds, int delay = 0, Action? done = null)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (done is not null) animation.Completed += (_, _) => done();
        element.BeginAnimation(OpacityProperty, animation);
    }

    private static void Slide(TranslateTransform transform, double from, double to, int milliseconds)
    {
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static void SlideX(TranslateTransform transform, double from, double to, int milliseconds)
    {
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private FrameworkElement PageAt(int index) => index switch
    {
        1 => PageSystem,
        2 => PageMusic,
        _ => PageAi,
    };

    private RadioButton TabAt(int index) => index switch
    {
        1 => TabSystem,
        2 => TabMusic,
        _ => TabAi,
    };

    private RadioButton TabById(string id) => id switch
    {
        "system" => TabSystem,
        "music" => TabMusic,
        _ => TabAi,
    };

    private static string IdOf(FrameworkElement tab) => tab.Tag as string ?? "ai";

    private void ApplyTabOrder()
    {
        var order = SettingsStore.NormalizePageOrder(settings.PageOrder);
        settings.PageOrder = order;

        // Re-adding radios to the panel can make WPF re-evaluate the group, so keep
        // the checked handler detached until the final selection is in place.
        TabAi.Checked -= PageTabChecked;
        TabSystem.Checked -= PageTabChecked;
        TabMusic.Checked -= PageTabChecked;

        TabsPanel.Children.Clear();
        foreach (var id in order) TabsPanel.Children.Add(TabById(id));
        TabAt(page).IsChecked = true;

        TabAi.Checked += PageTabChecked;
        TabSystem.Checked += PageTabChecked;
        TabMusic.Checked += PageTabChecked;
    }

    private static RadioButton? FindTab(DependencyObject? element)
    {
        while (element is not null and not RadioButton)
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        return element as RadioButton;
    }

    private void TabsMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(TabsPanel);
        dragTab = FindTab(e.OriginalSource as DependencyObject);
        draggingTab = false;
    }

    private void TabsMouseMove(object sender, MouseEventArgs e)
    {
        if (dragTab is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (draggingTab) ResetTabDrag();
            else dragTab = null;
            return;
        }

        var position = e.GetPosition(TabsPanel);
        if (!draggingTab)
        {
            if (Math.Abs(position.X - dragStart.X) <= 5 && Math.Abs(position.Y - dragStart.Y) <= 5) return;
            draggingTab = true;
            dragTab.Opacity = 0.5;
            TabsPanel.Cursor = Cursors.SizeWE;
        }

        ShowMarker(Insertion(position.X, dragTab));
    }

    private void TabsLostCapture(object sender, MouseEventArgs e)
    {
        // The tab's own RadioButton captures the mouse on press; losing that capture
        // must not abort a drag that is still running on the panel.
        if (e.OriginalSource is not null && !ReferenceEquals(e.OriginalSource, TabsPanel)) return;
        ResetTabDrag();
    }

    private void ResetTabDrag()
    {
        if (dragTab is not null) dragTab.Opacity = 1;
        dragTab = null;
        draggingTab = false;
        TabDropMarker.Visibility = Visibility.Collapsed;
        TabsPanel.ClearValue(CursorProperty);
    }

    private void TabsMouseUp(object sender, MouseButtonEventArgs e)
    {
        var moved = dragTab;
        var wasDragging = draggingTab;
        var index = moved is null ? 0 : Insertion(e.GetPosition(TabsPanel).X, moved);

        TabDropMarker.Visibility = Visibility.Collapsed;
        TabsPanel.ClearValue(CursorProperty);
        dragTab = null;
        draggingTab = false;
        if (TabsPanel.IsMouseCaptured) TabsPanel.ReleaseMouseCapture();
        if (moved is null) return;
        moved.Opacity = 1;
        if (!wasDragging) return;

        e.Handled = true;
        MoveTab(moved, index);
    }

    /// <summary>Index the dragged tab would take among the other tabs.</summary>
    private int Insertion(double x, RadioButton moved)
    {
        var others = TabsPanel.Children.OfType<RadioButton>().Where(tab => tab != moved).ToList();
        for (var i = 0; i < others.Count; i++)
        {
            var bounds = BoundsOf(others[i]);
            if (x < bounds.Left + bounds.Width / 2) return i;
        }
        return others.Count;
    }

    private void ShowMarker(int index)
    {
        var others = TabsPanel.Children.OfType<RadioButton>().Where(tab => tab != dragTab).ToList();
        double left;
        if (others.Count == 0) left = 0;
        else if (index <= 0) left = BoundsOf(others[0]).Left - 5;
        else if (index >= others.Count) left = BoundsOf(others[^1]).Right + 3;
        else left = (BoundsOf(others[index - 1]).Right + BoundsOf(others[index]).Left) / 2;

        TabDropMarker.Margin = new Thickness(left, 0, 0, 0);
        TabDropMarker.Visibility = Visibility.Visible;
    }

    private void MoveTab(RadioButton moved, int index)
    {
        // FLIP: capture the current offsets, reorder the children in place (no rebuild,
        // so the checked state never flickers), then animate each tab to its new slot.
        var before = new Dictionary<RadioButton, double>();
        foreach (var tab in TabsPanel.Children.OfType<RadioButton>())
        {
            var shift = EnsureShift(tab);
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
            before[tab] = BoundsOf(tab).Left;
        }
        TabsPanel.UpdateLayout();

        var oldOrder = TabsPanel.Children.OfType<RadioButton>().Select(IdOf).ToList();
        var from = TabsPanel.Children.IndexOf(moved);
        if (from < 0) return;
        TabsPanel.Children.RemoveAt(from);
        TabsPanel.Children.Insert(Math.Clamp(index, 0, TabsPanel.Children.Count), moved);
        TabsPanel.UpdateLayout();

        var order = TabsPanel.Children.OfType<RadioButton>().Select(IdOf).ToList();
        if (!order.SequenceEqual(oldOrder))
        {
            settings.PageOrder = order;
            store.Save(settings);
        }

        foreach (var tab in TabsPanel.Children.OfType<RadioButton>())
        {
            if (!before.TryGetValue(tab, out var oldLeft)) continue;
            var delta = oldLeft - BoundsOf(tab).Left;
            if (Math.Abs(delta) < 0.5) continue;

            var shift = EnsureShift(tab);
            shift.X = delta;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(delta, 0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    private System.Windows.Rect BoundsOf(FrameworkElement element) =>
        new(element.TransformToAncestor(TabsPanel).Transform(new Point(0, 0)), element.RenderSize);

    private static TranslateTransform EnsureShift(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform shift) return shift;
        shift = new TranslateTransform();
        element.RenderTransform = shift;
        return shift;
    }

    private void PageTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) ShowPage(PageIndexOf(id));
    }

    private static int PageIndexOf(string id) => id switch
    {
        "system" => 1,
        "music" => 2,
        _ => 0,
    };

    private void ShowPage(int index)
    {
        if (index == page) return;
        var old = PageAt(page);
        var next = PageAt(index);
        page = index;
        TabAt(index).IsChecked = true;
        sizeAnimUntil = DateTime.Now.AddMilliseconds(340);

        next.Visibility = Visibility.Visible;
        next.BeginAnimation(OpacityProperty, null);
        next.Opacity = 0;
        var nextSlide = (TranslateTransform)next.RenderTransform;
        nextSlide.BeginAnimation(TranslateTransform.XProperty, null);
        nextSlide.X = 14;
        Fade(next, 0, 1, 180, 40);
        SlideX(nextSlide, 14, 0, 230);

        if (!ReferenceEquals(old, next) && old.Visibility == Visibility.Visible)
        {
            Fade(old, 1, 0, 130);
            var oldSlide = (TranslateTransform)old.RenderTransform;
            oldSlide.BeginAnimation(TranslateTransform.XProperty, null);
            SlideX(oldSlide, 0, -12, 160);
            var collapse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
            collapse.Tick += (_, _) =>
            {
                collapse.Stop();
                if (!ReferenceEquals(PageAt(page), old)) old.Visibility = Visibility.Collapsed;
                if (expanded) AnimateSize(ExpandedWidth, MeasureExpandedHeight(), 220);
            };
            collapse.Start();
        }
        else if (expanded)
        {
            AnimateSize(ExpandedWidth, MeasureExpandedHeight(), 220);
        }
    }

    private static string Mix(string a, string b, double amount)
    {
        try
        {
            var ca = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(a);
            var cb = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(b);
            byte L(byte x, byte y) => (byte)Math.Clamp(x + (y - x) * amount, 0, 255);
            return $"#{L(ca.R, cb.R):X2}{L(ca.G, cb.G):X2}{L(ca.B, cb.B):X2}";
        }
        catch { return a; }
    }

    private async void FastTick(object? sender, EventArgs e)
    {
        vm.Clock = DateTime.Now.ToString("HH:mm");

        try { tracker.Poll(settings.Sessions.Enabled, settings.Sessions.KeepMinutes); } catch (Exception ex) { AppLog.Write(ex); }
        var sessions = tracker.Snapshot();
        vm.SetSessions(sessions);
        var liveKeys = sessions.Select(s => s.Provider + "|" + s.SessionId).ToHashSet();
        foreach (var key in lastStates.Keys.Where(key => !liveKeys.Contains(key)).ToArray()) lastStates.Remove(key);

        if (settings.Monitor.Enabled && DateTime.Now - lastMonitor >= TimeSpan.FromMilliseconds(settings.Monitor.IntervalMs))
        {
            lastMonitor = DateTime.Now;
            monitorTicks++;
            try
            {
                var sample = monitor.Sample(monitorTicks % 5 == 0);
                vm.Monitor.Update(sample, settings.Monitor);
                CpuSpark.Push(vm.Monitor.CpuPercent);
                MemSpark.Push(vm.Monitor.MemoryPercent);
            }
            catch (Exception ex) { AppLog.Write(ex); }
        }

        if (settings.Music.Enabled && DateTime.Now - lastMusic > TimeSpan.FromMilliseconds(1000))
        {
            lastMusic = DateTime.Now;
            try { await UpdateMusicAsync(); } catch (Exception ex) { AppLog.Write(ex); }
        }

        if (DateTime.Now - lastQuota > TimeSpan.FromSeconds(60)) _ = RefreshColumnsAsync();
        if (settings.HideInFullscreen) CheckFullscreen();

        if (expanded && DateTime.Now > sizeAnimUntil)
        {
            var desired = MeasureExpandedHeight();
            if (Math.Abs(Shell.Height - desired) > 12) AnimateSize(ExpandedWidth, desired, 200);
        }
    }

    // Enumerating every process takes long enough to drop animation frames, so it stays off the UI thread.
    private async Task SampleProcessesAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                if (!settings.Monitor.Enabled) continue;
                try
                {
                    var samples = processes.Sample();
                    await Dispatcher.InvokeAsync(() => vm.SetProcesses(samples));
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { AppLog.Write(ex); }
            }
            while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (OperationCanceledException) { }
    }

    private async Task UpdateMusicAsync()
    {
        if (!music.Available)
        {
            vm.Music.Title = "媒体控制不可用";
            return;
        }
        var state = await music.SnapshotAsync();
        if (state is null)
        {
            vm.Music.Title = "未在播放";
            vm.Music.Artist = "";
            vm.Music.IsPlaying = false;
            vm.Music.Progress = 0;
            return;
        }
        vm.Music.Title = state.Title;
        vm.Music.Artist = state.Artist;
        vm.Music.IsPlaying = state.IsPlaying;
        vm.Music.Cover = state.Cover;
        vm.Music.Position = Format(state.Position);
        vm.Music.Duration = Format(state.Duration);
        vm.Music.Progress = state.Duration.TotalSeconds > 0 ? state.Position.TotalSeconds / state.Duration.TotalSeconds : 0;
    }

    private static string Format(TimeSpan value) =>
        value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");

    private async Task RefreshColumnsAsync()
    {
        if (refreshing) return;
        refreshing = true;
        var version = settingsVersion;
        lastQuota = DateTime.Now;
        try
        {
            EnsureColumns();
            var batch = await columns.ReadColumnsAsync(settings.Columns, secrets, settings.Alerts, lifetime.Token);
            if (version != settingsVersion) return;
            for (var index = 0; index < batch.Snapshots.Count && index < vm.Columns.Count; index++)
                vm.Columns[index].Update(batch.Snapshots[index]);
            NotifyAlerts(batch);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AppLog.Write(e);
            foreach (var column in vm.Columns) column.MarkError(e.Message);
        }
        finally
        {
            refreshing = false;
            if (version != settingsVersion && !lifetime.IsCancellationRequested) _ = RefreshColumnsAsync();
        }
    }

    /// <summary>Notifies once per alert; an alert that clears can fire again later.</summary>
    private void NotifyAlerts(ColumnBatch batch)
    {
        var alerts = batch.Alerts;
        var unknown = batch.Snapshots.Where(s => s.Quota?.State != SourceState.Ok)
            .SelectMany(s => new[] { s.Settings.Id + ":quota", s.Settings.Id + ":balance" });
        activeAlerts.IntersectWith(alerts.Select(alert => alert.Key).Concat(unknown));
        foreach (var alert in alerts)
            if (activeAlerts.Add(alert.Key))
                Tray?.Notify("AI Island · 额度预警", alert.Message);
    }

    private void EnsureColumns()
    {
        if (vm.Columns.Count == settings.Columns.Count &&
            settings.Columns.Select((column, index) => vm.Columns[index].IsRelay == (SourceCatalog.Find(column.QuotaSource)?.NeedsSite ?? false)).All(x => x))
            return;

        vm.Columns.Clear();
        foreach (var column in settings.Columns)
        {
            vm.Columns.Add(new ColumnVm
            {
                Name = string.IsNullOrWhiteSpace(column.Name)
                    ? SourceCatalog.Find(column.QuotaSource)?.Name ?? column.QuotaSource
                    : column.Name,
                Accent = Theme.Brush(column.Accent),
                IsRelay = SourceCatalog.Find(column.QuotaSource)?.NeedsSite ?? false,
            });
        }
    }

    private void OnSessionChanged(SessionItem item, ActivityEvent e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!settings.Sessions.Enabled) return;
            var key = item.Provider + "|" + item.SessionId;
            lastStates.TryGetValue(key, out var previous);
            if (previous == item.State) return;
            lastStates[key] = item.State;

            var title = item.Provider + " · " + (item.WorkingDirectory is { Length: > 0 } dir ? Path.GetFileName(dir.TrimEnd('\\', '/')) : item.SessionId[..Math.Min(8, item.SessionId.Length)]);
            if (item.State == SessionState.Waiting && settings.Sessions.NotifyWaiting)
                Tray?.Notify("等待确认", title);
            else if (item.State == SessionState.Done && settings.Sessions.NotifyDone)
                Tray?.Notify("任务完成", title);
            else if (item.State == SessionState.Failed && settings.Sessions.NotifyFailed)
                Tray?.Notify("任务失败", title);
        });
    }

    private void ShellMouseEnter(object sender, MouseEventArgs e)
    {
        if (expanded) return;
        ShowPageAt(e.GetPosition(this));
        Expand();
    }

    /// <summary>
    /// Which page to open depends on where the pointer entered the collapsed bar:
    /// music bars → music, right stats (CPU/memory/network/clock) → system, anything else → sessions.
    /// Only the horizontal position is used: when the pointer comes down from above, the first
    /// point inside the window is the very top edge, above the bar's own padding.
    /// </summary>
    private void ShowPageAt(Point point)
    {
        var index = 0;
        if (CompactMusicBars.IsVisible && InBand(CompactMusicBars, point.X, 8)) index = 2;
        else if (point.X >= LeftOf(CompactStats)) index = 1;
        if (index == page) return;

        // The panel is still collapsed, so switch instantly instead of running the tab animation.
        var old = PageAt(page);
        var next = PageAt(index);
        page = index;
        TabAt(index).IsChecked = true;
        old.Visibility = Visibility.Collapsed;
        next.BeginAnimation(OpacityProperty, null);
        next.Opacity = 1;
        var slide = (TranslateTransform)next.RenderTransform;
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        slide.X = 0;
        next.Visibility = Visibility.Visible;
    }

    private double LeftOf(FrameworkElement element) =>
        element.TransformToVisual(this).Transform(new Point(0, 0)).X;

    private bool InBand(FrameworkElement element, double x, double padding)
    {
        if (element.ActualWidth <= 0) return false;
        var left = LeftOf(element);
        return x >= left - padding && x <= left + element.ActualWidth + padding;
    }

    private void ShellMouseLeave(object sender, MouseEventArgs e)
    {
        if (pinned) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsMouseOver) Collapse();
        }));
    }

    private void CompactClick(object sender, MouseButtonEventArgs e)
    {
        ShowPageAt(e.GetPosition(this));
        Expand();
    }

    private void CollapseClick(object sender, RoutedEventArgs e) => Collapse();

    private void ShellDoubleClick(object sender, MouseButtonEventArgs e)
    {
        pinned = !pinned;
        if (pinned) Expand();
        else if (!IsMouseOver) Collapse();
        e.Handled = true;
    }

    private void ShellRightClick(object sender, MouseButtonEventArgs e)
    {
        OpenSettings();
        e.Handled = true;
    }

    private void SessionClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SessionVm session }) return;
        TerminalNavigator.Locate(session.TerminalId, session.WorkingDirectory);
    }

    private void AllowClick(object sender, RoutedEventArgs e) => Decide(sender, e, allow: true);

    private void DenyClick(object sender, RoutedEventArgs e) => Decide(sender, e, allow: false);

    private void Decide(object sender, RoutedEventArgs e, bool allow)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: SessionVm { RequestId: { } requestId } session }) return;
        try
        {
            tracker.Answer(session.Provider, session.SessionId, requestId, allow);
        }
        catch (Exception ex) { AppLog.Write(ex); }
        vm.SetSessions(tracker.Snapshot());
    }

    private void ProcessSortClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        vm.SortProcesses(key);
        e.Handled = true;
    }

    private void Expand()
    {
        if (expanded) return;
        expanded = true;

        CompactPanel.Visibility = Visibility.Visible;
        ExpandedPanel.Visibility = Visibility.Visible;
        ExpandedPanel.BeginAnimation(OpacityProperty, null);
        ExpandedPanel.Opacity = 0;
        var slide = (TranslateTransform)ExpandedPanel.RenderTransform;
        slide.BeginAnimation(TranslateTransform.YProperty, null);
        slide.Y = 12;

        AnimateSize(ExpandedWidth, MeasureExpandedHeight());
        Fade(ExpandedPanel, 0, 1, 180, 50);
        Slide(slide, 12, 0, 240);
        Fade(CompactPanel, 1, 0, 90, 0, () =>
        {
            if (expanded) CompactPanel.Visibility = Visibility.Collapsed;
        });

        _ = RefreshColumnsAsync();
    }

    private void Collapse()
    {
        if (!expanded) return;
        expanded = false;

        CompactPanel.Visibility = Visibility.Visible;
        CompactPanel.BeginAnimation(OpacityProperty, null);
        CompactPanel.Opacity = 0;
        Fade(CompactPanel, 0, 1, 160, 90);

        var slide = (TranslateTransform)ExpandedPanel.RenderTransform;
        Fade(ExpandedPanel, 1, 0, 130, 0, () =>
        {
            if (!expanded) ExpandedPanel.Visibility = Visibility.Collapsed;
        });
        Slide(slide, 0, 8, 150);

        AnimateSize(compactWidth, compactHeight);
    }

    private void Place()
    {
        var screen = Displays.Find(settings.Display);
        var work = ToDip(screen.WorkingArea);
        var bounds = ToDip(screen.Bounds);
        Left = work.Left + Math.Max(0, (work.Width - ExpandedWidth) / 2);
        // An auto-hidden taskbar still reports its band in the work area while hidden,
        // which would leave a gap at the top.
        var anchor = settings.TopMode == TopMode.ScreenTop || IsTaskbarAutoHide() ? bounds.Top : work.Top;
        Top = anchor + settings.TopOffset;
    }

    /// <summary>Screen rectangles are in device pixels; window position is in WPF units.</summary>
    private System.Windows.Rect ToDip(System.Drawing.Rectangle pixels)
    {
        var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
                     ?? Matrix.Identity;
        var topLeft = matrix.Transform(new Point(pixels.Left, pixels.Top));
        var bottomRight = matrix.Transform(new Point(pixels.Right, pixels.Bottom));
        return new System.Windows.Rect(topLeft, bottomRight);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(Place);

    private void CheckFullscreen()
    {
        if (expanded) return;
        var handle = new WindowInteropHelper(this).Handle;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == handle) return;
        if (!GetWindowRect(foreground, out var rect)) return;
        // Only a full-screen window on the island's own monitor hides it. The desktop
        // window also spans the screen, so clicking the wallpaper must not count.
        var screen = Displays.Find(settings.Display);
        var bounds = screen.Bounds;
        var covers = !IsDesktop(foreground)
                     && System.Windows.Forms.Screen.FromHandle(foreground).DeviceName == screen.DeviceName
                     && rect.Left <= bounds.Left + 1 && rect.Top <= bounds.Top + 1
                     && rect.Right >= bounds.Right - 1 && rect.Bottom >= bounds.Bottom - 1;
        if (covers && !hiddenForFullscreen)
        {
            hiddenForFullscreen = true;
            Hide();
        }
        else if (!covers && hiddenForFullscreen)
        {
            hiddenForFullscreen = false;
            Show();
        }
    }

    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else { Show(); Place(); }
    }

    public void OpenSettings()
    {
        var window = new SettingsWindow(settings, secrets);
        window.Saved += updated =>
        {
            settings = updated;
            store.Save(settings);
            secrets.Save();
            settingsVersion++;
            activeAlerts.Clear();
            ApplySessionSettings();
            StartupRegistration.Apply(settings.StartWithWindows);
            ApplyAppearance();
            Place();
            vm.Columns.Clear();
            EnsureColumns();
            _ = RefreshColumnsAsync();
        };
        window.ShowDialog();
    }

    private void ApplySessionSettings()
    {
        if (settings.Sessions.Enabled)
            sessionSignal ??= new System.Threading.Mutex(false, @"Local\AIIsland-Sessions-" + Environment.UserName);
        else
        {
            sessionSignal?.Dispose();
            sessionSignal = null;
            lastStates.Clear();
        }
        tracker.Poll(settings.Sessions.Enabled, settings.Sessions.KeepMinutes);
        vm.SetSessions(tracker.Snapshot());
    }

    public void ExitApp()
    {
        lifetime.Cancel();
        Application.Current.Shutdown();
    }

    private async void PreviousClick(object sender, RoutedEventArgs e) { try { await music.PreviousAsync(); } catch { } }
    private async void NextClick(object sender, RoutedEventArgs e) { try { await music.NextAsync(); } catch { } }
    private async void PlayClick(object sender, RoutedEventArgs e) { try { await music.ToggleAsync(); } catch { } }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public int lParam;
    }

    private const uint AbmGetState = 0x00000004;
    private const uint AbsAutoHide = 0x00000001;

    private static bool IsTaskbarAutoHide()
    {
        try
        {
            var data = new AppBarData { cbSize = Marshal.SizeOf<AppBarData>() };
            return (SHAppBarMessage(AbmGetState, ref data).ToUInt64() & AbsAutoHide) != 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    private static bool IsDesktop(IntPtr window)
    {
        if (window == GetShellWindow()) return true;
        var name = new System.Text.StringBuilder(32);
        GetClassName(window, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
}
