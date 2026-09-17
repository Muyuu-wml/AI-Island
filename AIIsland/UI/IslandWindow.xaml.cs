using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIIsland.Core;
using AIIsland.Modules;
using AIIsland.Services;
using Forms = System.Windows.Forms;

namespace AIIsland.UI;
public partial class IslandWindow : Window
{
    private Settings settings;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly Action<AiNotification> notificationSink;
    public IslandViewModel ViewModel { get; }
    private SettingsWindow? settingsWindow;
    private bool pointerDown, dragging, layoutQueued, closing;
    private bool drawerRequested, enterPending, aiWasExpanded;
    private int transitionVersion;
    private readonly TranslateTransform drawerOffset = new();
    private MouseHook? mouseHook;
    private IntPtr mouseHookHandle;
    private string appearanceKey = "";
    private string appearanceState = "idle";
    private string outlineKey = "";
    private NativePoint pointerOrigin;
    private NativeRect windowOrigin;
    public IslandWindow()
    {
        settings = SettingsService.Load();
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        InitializeComponent();
        ToolPanel.RenderTransform = drawerOffset;
        var contentClip = new RectangleGeometry { RadiusX = 23, RadiusY = 23 };
        RootPanel.Clip = contentClip;
        Capsule.SizeChanged += (_, e) => contentClip.Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2));
        ViewModel = new(settings); DataContext = ViewModel;
        Capsule.MouseEnter += (_, _) => UpdateAppearance();
        Capsule.MouseLeave += (_, _) => UpdateAppearance();
        UpdateAppearance();
        var iconUri = new Uri("pack://application:,,,/AIIsland;component/Assets/app.ico");
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
        using (var iconStream = Application.GetResourceStream(iconUri).Stream)
        using (var loadedIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize))
            trayIcon = (System.Drawing.Icon)loadedIcon.Clone();
        tray = new Forms.NotifyIcon { Text = "AI Island · Monitoring", Icon = trayIcon, Visible = true };
        notificationSink = notice => tray.ShowBalloonTip(4000, notice.Title, notice.Message, notice.Severity switch
        {
            AiNotificationSeverity.Warning => Forms.ToolTipIcon.Warning,
            AiNotificationSeverity.Error => Forms.ToolTipIcon.Error,
            _ => Forms.ToolTipIcon.Info
        });
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show Island", null, (_, _) => Dispatcher.Invoke(Show));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add("暂停 / 恢复 AI 监控", null, (_, _) => Dispatcher.Invoke(TogglePause));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitFromTray());
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Show);
        var context = new ContextMenu();
        AddMenu(context, "Show Island", Show); AddMenu(context, "Settings", OpenSettings);
        AddMenu(context, "暂停 / 恢复 AI 监控", TogglePause); AddMenu(context, "Exit", () => Application.Current.Shutdown());
        AddMenu(context, "顶部居中", () => { settings.Position = "Center"; SettingsService.SavePosition(settings); Position(); ScheduleLayout(); });
        Capsule.ContextMenu = context;
        AddMenu(context, "监控接入状态", () => MessageBox.Show(this, ViewModel.Ai.IntegrationStatus + "\n\n已收到事件不代表 CLI 当前仍在线；会话详情显示连接状态。", "监控接入状态", MessageBoxButton.OK, MessageBoxImage.Information));
        ViewModel.LayoutChanged += ScheduleLayout;
        ViewModel.Ai.Completed += NotifyAi;
        ViewModel.Ai.ApprovalRequested += NotifyAi;
        SourceInitialized += (_, _) => {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000 | 0x80));
            HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
            mouseHook = ObserveMouse;
            mouseHookHandle = SetWindowsHookEx(14, mouseHook, GetModuleHandle(null), 0);
        };
        Loaded += async (_, _) => { Position(); ScheduleLayout(); await ViewModel.StartAsync(); };
        AddHandler(Expander.ExpandedEvent, new RoutedEventHandler((_, _) => ScheduleLayout()));
        AddHandler(Expander.CollapsedEvent, new RoutedEventHandler((_, _) => ScheduleLayout()));
        SizeChanged += (_, _) => Position();
        Closed += (_, _) => { closing = true; if (mouseHookHandle != IntPtr.Zero) UnhookWindowsHookEx(mouseHookHandle); ViewModel.Dispose(); tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); settingsWindow?.Close(); };
    }
    internal void ExitFromTray() => Dispatcher.Invoke(() => Application.Current.Shutdown());

    private void NotifyAi(IslandEvent value)
    {
        var project = ViewModel.Ai.Groups.FirstOrDefault(group => group.Name == value.Provider)?.Rows.FirstOrDefault(row => row.SessionId == value.SessionId)?.Project;
        if (AiNotificationPolicy.Create(settings, value, project) is { } notification) notificationSink(notification);
    }
    private IntPtr ObserveMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() == 0x201 && drawerRequested && !pointerDown && !closing && Capsule.ContextMenu?.IsOpen != true)
        {
            var point = Marshal.PtrToStructure<NativePoint>(data);
            var hit = WindowFromPoint(point);
            if (GetAncestor(hit, 2) != new WindowInteropHelper(this).Handle)
            {
                var version = transitionVersion;
                Dispatcher.BeginInvoke(new Action(() => { if (!closing && drawerRequested && version == transitionVersion) ToggleDrawer(); }));
            }
        }
        return CallNextHookEx(mouseHookHandle, code, message, data);
    }
    private static void AddMenu(ContextMenu menu, string title, Action action) { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
    private void TogglePause() { ViewModel.Ai.TogglePause(); tray.Text = ViewModel.Ai.Paused ? "AI Island · AI Paused" : "AI Island · Monitoring"; }
    private void OpenSettings()
    {
        if (settingsWindow != null) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(settings, value => {
            settings = value;
            if (!value.Animation) {
                transitionVersion++; enterPending = false; ViewModel.DrawerOpen = drawerRequested;
                ToolPanel.BeginAnimation(OpacityProperty, null); ToolPanel.Opacity = 1;
                drawerOffset.BeginAnimation(TranslateTransform.YProperty, null); drawerOffset.Y = 0;
                BeginAnimation(WidthProperty, null); BeginAnimation(HeightProperty, null);
            }
            ViewModel.Configure(value); UpdateAppearance(); ScheduleLayout();
        });
        settingsWindow.Closed += (_, _) => settingsWindow = null; settingsWindow.Show();
    }
    public async void ToggleDrawer()
    {
        drawerRequested = !drawerRequested;
        var version = ++transitionVersion;
        ToolPanel.IsHitTestVisible = drawerRequested;
        if (drawerRequested)
        {
            var wasClosed = !ViewModel.DrawerOpen;
            if (wasClosed && settings.Animation) { ToolPanel.BeginAnimation(OpacityProperty, null); ToolPanel.Opacity = 0; }
            ViewModel.DrawerOpen = true; enterPending = true; ScheduleLayout();
        }
        else
        {
            enterPending = false;
            if (settings.Animation)
            {
                FadeDrawer(0, -5, 180, 0);
                ScheduleLayout();
                await Task.Delay(360);
                if (closing || version != transitionVersion) return;
            }
            ViewModel.DrawerOpen = false; ScheduleLayout();
        }
    }
    private void FadeDrawer(double opacity, double offset, int milliseconds, int delay)
    {
        var currentOpacity = ToolPanel.Opacity; var currentOffset = drawerOffset.Y;
        ToolPanel.BeginAnimation(OpacityProperty, null); ToolPanel.Opacity = opacity;
        drawerOffset.BeginAnimation(TranslateTransform.YProperty, null); drawerOffset.Y = offset;
        if (!settings.Animation) return;
        ToolPanel.BeginAnimation(OpacityProperty, DrawerTransition(currentOpacity, opacity, milliseconds, delay));
        drawerOffset.BeginAnimation(TranslateTransform.YProperty, DrawerTransition(currentOffset, offset, milliseconds, delay));
    }
    private static DoubleAnimationUsingKeyFrames DrawerTransition(double from, double to, int milliseconds, int delay)
    {
        // Hold the current value during the opening delay so the target never flashes first.
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0) animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + milliseconds)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        return animation;
    }
    private void ScheduleLayout()
    {
        if (layoutQueued || closing || !IsLoaded) return;
        layoutQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            layoutQueued = false; if (closing) return;
            UpdateAppearance();
            if (aiWasExpanded != ViewModel.AiExpanded)
            {
                aiWasExpanded = ViewModel.AiExpanded;
                DrawerScroll.ScrollToTop();
                AiCard.BeginAnimation(OpacityProperty, null);
                if (aiWasExpanded && settings.Animation) AiCard.BeginAnimation(OpacityProperty, DrawerTransition(0, 1, 240, 60));
            }
            const double width = 420d;
            var area = Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle).WorkingArea;
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
            var anchor = Anchor();
            var availableHeight = Math.Max(66, (area.Bottom - anchor.Y) / scale);
            Header.Measure(new Size(width - 22, double.PositiveInfinity));
            DrawerScroll.MaxHeight = Math.Max(0, Math.Min(area.Height / scale * .7, availableHeight) - Header.DesiredSize.Height - 30);
            RootPanel.Measure(new Size(width - 22, double.PositiveInfinity));
            Header.Measure(new Size(width - 22, double.PositiveInfinity));
            AiCard.Measure(new Size(width - 42, double.PositiveInfinity));
            ToolPanel.Measure(new Size(width - 42, double.PositiveInfinity));
            var bodyHeight = (ViewModel.AiExpanded ? AiCard.DesiredSize.Height : 0) + (drawerRequested ? ToolPanel.DesiredSize.Height : 0);
            var height = Math.Max(66, Header.DesiredSize.Height + 22 + (bodyHeight > 0 ? Math.Min(bodyHeight, DrawerScroll.MaxHeight) + 8 : 0));
            Animate(HeightProperty, Math.Min(height, availableHeight)); Position();
            if (enterPending && drawerRequested)
            {
                enterPending = false;
                if (ToolPanel.Opacity < .01) { drawerOffset.BeginAnimation(TranslateTransform.YProperty, null); drawerOffset.Y = -8; }
                FadeDrawer(1, 0, 240, 60);
            }
        }));
    }
    private void UpdateAppearance()
    {
        var state = ViewModel.Ai.NeedsAttention ? "approval"
            : ViewModel.Ai.Groups.SelectMany(g => g.Rows).Any(r => r.Status.Contains("Failed")) ? "failed"
            : ViewModel.Ai.ActiveCount > 0 ? "running" : "idle";
        var lightKey = $"{settings.RainbowOutline}:{settings.OutlinePalette}:{settings.OutlineColor}:{settings.OutlineMode}:{settings.Animation}";
        var key = $"{settings.EnhancedOutline}:{lightKey}:{Capsule.IsMouseOver}:{state}";
        if (key == appearanceKey) return;
        var changed = state != appearanceState;
        appearanceKey = key; appearanceState = state;
        var previousBrush = Capsule.BorderBrush;
        IslandAppearance.Apply(Capsule, settings.EnhancedOutline, Capsule.IsMouseOver, state, settings.Animation && changed, settings.RainbowOutline, settings.OutlinePalette, settings.OutlineColor, settings.OutlineMode, settings.Animation);
        if (settings.RainbowOutline && outlineKey == lightKey) Capsule.BorderBrush = previousBrush;
        outlineKey = lightKey;
    }
    private void BeginPointer(object sender, MouseButtonEventArgs e)
    {
        if (!GetCursorPos(out pointerOrigin) || !GetWindowRect(new WindowInteropHelper(this).Handle, out windowOrigin)) return;
        pointerDown = Header.CaptureMouse(); dragging = false; e.Handled = true;
    }
    private void MovePointer(object sender, MouseEventArgs e)
    {
        if (!pointerDown || !GetCursorPos(out var cursor)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishPointer(false); return; }
        var dx = cursor.X - pointerOrigin.X; var dy = cursor.Y - pointerOrigin.Y; var dpi = VisualTreeHelper.GetDpi(this);
        if (!dragging && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX && Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
        dragging = true;
        MoveWithin(Forms.Screen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y)).WorkingArea, windowOrigin.Left + dx, windowOrigin.Top + dy);
        e.Handled = true;
    }
    private void EndPointer(object sender, MouseButtonEventArgs e) { if (pointerDown) { FinishPointer(true); e.Handled = true; } }
    private void CancelPointer(object sender, MouseEventArgs e) { if (pointerDown) FinishPointer(false); }
    private void FinishPointer(bool allowClick)
    {
        var moved = dragging; pointerDown = dragging = false; Header.ReleaseMouseCapture();
        if (moved && GetWindowRect(new WindowInteropHelper(this).Handle, out var rect))
        {
            settings.Position = "Custom"; settings.CustomLeft = rect.Left;
            settings.CustomTop = rect.Top;
            try { SettingsService.SavePosition(settings); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { tray.ShowBalloonTip(3000, "AI Island", "位置已移动，但无法保存。", Forms.ToolTipIcon.Warning); }
            ScheduleLayout();
        }
        else if (allowClick) ToggleDrawer();
    }
    private NativePoint Anchor()
    {
        if (settings.Position == "Custom" && settings.CustomLeft is int x && settings.CustomTop is int y) return new() { X = x, Y = y };
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return new() { X = settings.Position switch { "Left" => area.Left + 14, "Right" => area.Right - (int)(ActualWidth * scale) - 14, _ => area.Left + (area.Width - (int)(ActualWidth * scale)) / 2 }, Y = area.Top + 8 };
    }
    private void Position()
    {
        if (pointerDown || new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        var anchor = Anchor(); var area = Forms.Screen.FromPoint(new System.Drawing.Point(anchor.X, anchor.Y)).WorkingArea;
        var y = anchor.Y;
        MoveWithin(area, anchor.X, y);
    }
    private void MoveWithin(System.Drawing.Rectangle area, int x, int y)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(handle, out var rect)) return;
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - (rect.Right - rect.Left)));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top)));
        SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, 0x0015);
    }
    private void Animate(DependencyProperty property, double target)
    {
        if (Math.Abs((double)GetAnimationBaseValue(property) - target) < .5) return;
        var from = (double)GetValue(property); BeginAnimation(property, null); SetValue(property, target);
        const int duration = 360;
        IEasingFunction easing = target > from
            ? new CubicEase { EasingMode = EasingMode.EaseOut }
            : new SineEase { EasingMode = EasingMode.EaseInOut };
        if (settings.Animation) BeginAnimation(property, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing, FillBehavior = FillBehavior.Stop });
    }
    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    { if (msg == 0x21 && !ShowInTaskbar) { handled = true; return new IntPtr(3); } if (msg is 0x7E or 0x2E0) ScheduleLayout(); return IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    private delegate IntPtr MouseHook(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, MouseHook callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
