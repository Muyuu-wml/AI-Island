using System;
using System.Windows;
using System.Windows.Controls;
using AIIsland.Services;
namespace AIIsland.UI;
public sealed class SettingsWindow : Window
{
    public SettingsWindow(Settings current, Action<Settings> saved)
    {
        Title = "AI Island · Settings"; Width = 360; Height = 565; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(26) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "AI Island", FontSize = 24, Margin = new Thickness(0, 0, 0, 18) });
        CheckBox Check(string text, bool value) { var c = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 6, 0, 6) }; panel.Children.Add(c); return c; }
        panel.Children.Add(new TextBlock { Text = "AI Agent · 固定置顶", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var codex = Check("Codex", current.Codex); var claude = Check("Claude", current.Claude);
        panel.Children.Add(new TextBlock { Text = "其他监控 · 显示顺序", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "勾选显示，用右侧箭头调整页面顺序。", FontSize = 11, Margin = new Thickness(0, 0, 0, 6) });
        var monitors = new MonitorOrderEditor(current);
        panel.Children.Add(monitors);
        panel.Children.Add(new TextBlock { Text = "账户余额 · xc.lifesecretary.com", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "登录邮箱" });
        var balanceEmail = new TextBox { Text = current.BalanceEmail, Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(balanceEmail);
        panel.Children.Add(new TextBlock { Text = "登录密码" });
        var hasSavedPassword = !string.IsNullOrEmpty(current.BalancePassword);
        var passwordField = new Grid { Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(passwordField);
        var balancePassword = new PasswordBox { PasswordChar = '●' }; passwordField.Children.Add(balancePassword);
        var savedPasswordHint = new TextBlock { Text = "●●●●", Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        passwordField.Children.Add(savedPasswordHint);
        void UpdatePasswordHint() => savedPasswordHint.Visibility = hasSavedPassword && balancePassword.Password.Length == 0 && !balancePassword.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Collapsed;
        UpdatePasswordHint();
        balancePassword.PasswordChanged += (_, _) => UpdatePasswordHint();
        balancePassword.GotKeyboardFocus += (_, _) => UpdatePasswordHint();
        balancePassword.LostKeyboardFocus += (_, _) => UpdatePasswordHint();
        panel.Children.Add(new TextBlock { Text = "刷新间隔（秒）：10–86400，0 为仅手动" });
        var balanceInterval = new TextBox { Text = current.BalanceRefreshSeconds.ToString(), Margin = new Thickness(0, 4, 0, 8) }; panel.Children.Add(balanceInterval);
        panel.Children.Add(new TextBlock { Text = "启用上方“账户余额”后生效。密码和 token 在本机加密保存；token 过期自动重新登录。", TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        panel.Children.Add(new TextBlock { Text = "通用设置", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 4) });
        var animation = Check("启用动画", current.Animation);
        var notification = Check("本轮结束或失败时显示右下角通知", current.Notifications);
        var approvalNotification = Check("等待确认时显示右下角通知", current.ApprovalNotifications ?? current.Notifications);
        var outline = Check("增强轮廓（蓝灰底色与光晕）", current.EnhancedOutline);
        var rainbow = Check("启用 RGB 灯条", current.RainbowOutline);
        var thicknessLabel = new TextBlock { Margin = new Thickness(0, 8, 0, 4) };
        panel.Children.Add(thicknessLabel);
        var thickness = new Slider { Minimum = 0.5, Maximum = 6, TickFrequency = 0.5, IsSnapToTickEnabled = true,
            SmallChange = 0.5, LargeChange = 1, Value = current.OutlineThickness, IsEnabled = current.RainbowOutline,
            ToolTip = "默认 1，范围 0.5–6；随系统显示缩放。" };
        System.Windows.Automation.AutomationProperties.SetName(thickness, "灯条粗细");
        void UpdateThicknessLabel() => thicknessLabel.Text = $"灯条粗细：{thickness.Value:0.#}（默认 1）";
        thickness.ValueChanged += (_, _) => UpdateThicknessLabel();
        rainbow.Checked += (_, _) => thickness.IsEnabled = true;
        rainbow.Unchecked += (_, _) => thickness.IsEnabled = false;
        UpdateThicknessLabel();
        panel.Children.Add(thickness);
        panel.Children.Add(new TextBlock { Text = "灯条颜色", Margin = new Thickness(0, 8, 0, 4) });
        var palette = new ComboBox { ItemsSource = new[] { "彩虹色（默认）", "自定义单色" }, SelectedIndex = current.OutlinePalette == "Custom" ? 1 : 0 }; panel.Children.Add(palette);
        var color = new TextBox { Text = current.OutlineColor, ToolTip = "十六进制颜色，例如 #72D8EE", Margin = new Thickness(0, 6, 0, 6), IsEnabled = palette.SelectedIndex == 1 }; panel.Children.Add(color);
        palette.SelectionChanged += (_, _) => color.IsEnabled = palette.SelectedIndex == 1;
        panel.Children.Add(new TextBlock { Text = "灯条模式", Margin = new Thickness(0, 4, 0, 4) });
        var mode = new ComboBox { ItemsSource = new[] { "静态", "跑马灯", "呼吸" }, SelectedIndex = current.OutlineMode == "Marquee" ? 1 : current.OutlineMode == "Breathing" ? 2 : 0 }; panel.Children.Add(mode);
        panel.Children.Add(new TextBlock { Text = "关闭启用动画时，灯条保持静态。", FontSize = 11, Margin = new Thickness(0, 5, 0, 5) });
        var startup = Check("随 Windows 启动", current.StartWithWindows);
        panel.Children.Add(new TextBlock { Text = "顶部位置", Margin = new Thickness(0, 12, 0, 5) });
        var position = new ComboBox { ItemsSource = new[] { "Left", "Center", "Right", "Custom" }, SelectedItem = current.Position }; panel.Children.Add(position);
        var button = new Button { Content = "保存", Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(10) }; panel.Children.Add(button);
        button.Click += (_, _) => {
            if (!int.TryParse(balanceInterval.Text.Trim(), out var refreshSeconds) || (refreshSeconds != 0 && (refreshSeconds < 10 || refreshSeconds > 86400)))
            { MessageBox.Show(this, "刷新间隔请输入 0，或 10–86400 秒。", "账户余额"); return; }
            if (palette.SelectedIndex == 1 && !IslandAppearance.IsHexColor(color.Text.Trim())) { MessageBox.Show(this, "请输入 # 加 6 位十六进制颜色，例如 #72D8EE。", "灯条颜色"); return; }
            var value = new Settings { Codex = codex.IsChecked == true, Claude = claude.IsChecked == true, Animation = animation.IsChecked == true, Notifications = notification.IsChecked == true, StartWithWindows = startup.IsChecked == true, Position = position.SelectedItem as string ?? "Center" };
            value.ApprovalNotifications = approvalNotification.IsChecked == true;
            value.BalanceEnabled = monitors.IsMonitorEnabled("balance");
            value.BalanceEmail = balanceEmail.Text.Trim();
            value.BalanceRefreshSeconds = refreshSeconds;
            value.CustomLeft = current.CustomLeft; value.CustomTop = current.CustomTop;
            value.QQMusic = monitors.IsMonitorEnabled("qqmusic"); value.Clash = monitors.IsMonitorEnabled("clash");
            value.NetEaseMusic = monitors.IsMonitorEnabled("neteasemusic");
            value.SystemResources = monitors.IsMonitorEnabled("resources");
            value.MonitorOrder = monitors.Order;
            value.EnhancedOutline = outline.IsChecked == true;
            value.RainbowOutline = rainbow.IsChecked == true;
            value.OutlineThickness = thickness.Value;
            value.OutlinePalette = palette.SelectedIndex == 1 ? "Custom" : "Rainbow";
            value.OutlineColor = IslandAppearance.IsHexColor(color.Text.Trim()) ? color.Text.Trim().ToUpperInvariant() : "#72D8EE";
            value.OutlineMode = mode.SelectedIndex == 1 ? "Marquee" : mode.SelectedIndex == 2 ? "Breathing" : "Static";
            try {
                value.BalancePassword = balancePassword.Password.Length > 0 ? LocalSecret.Protect(balancePassword.Password) : current.BalancePassword;
                SettingsService.Save(value); saved(value); Close();
            }
            catch (Exception e) { MessageBox.Show(this, "设置保存失败：" + e.Message, "AI Island"); }
        };
    }
}
