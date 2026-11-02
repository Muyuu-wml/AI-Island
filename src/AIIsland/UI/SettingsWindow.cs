using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using AIIsland.Core;
using AIIsland.Services;

namespace AIIsland.UI;

/// <summary>Code-built settings panel so the whole theme stays in one place.</summary>
public sealed class SettingsWindow : Window
{
    private readonly IslandSettings settings;
    private readonly SecretStore secrets;
    private readonly StackPanel columnPanel = new();
    private readonly List<ColumnEditor> editors = new();

    private readonly ComboBox style;
    private readonly ComboBox topMode;
    private readonly ComboBox theme;
    private readonly StackPanel swatches = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox topOffset;
    private readonly CheckBox startWithWindows;
    private readonly CheckBox hideFullscreen;
    private readonly ComboBox display;
    private readonly CheckBox sessionsEnabled;
    private readonly CheckBox notifyDone;
    private readonly CheckBox notifyWaiting;
    private readonly CheckBox monitorEnabled;
    private readonly CheckBox showBattery;
    private readonly CheckBox musicEnabled;
    private readonly CheckBox weatherEnabled;
    private readonly CheckBox weatherAqi;
    private readonly TextBox weatherCity;
    private readonly CheckBox showClock;
    private readonly TextBox quotaThreshold;
    private readonly TextBox balanceThreshold;

    public event Action<IslandSettings>? Saved;

    public SettingsWindow(IslandSettings source, SecretStore secrets)
    {
        this.secrets = secrets;
        settings = Clone(source);

        Title = "AI Island 设置";
        Width = 560;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = new SolidColorBrush(Color.FromRgb(11, 13, 18));
        Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248));
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");

        ApplyTheme();

        var root = new DockPanel { Margin = new Thickness(18) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var cancel = Button("取消");
        cancel.Click += (_, _) => Close();
        var install = Button("安装 / 修复 Hook");
        install.Margin = new Thickness(0, 0, 10, 0);
        install.Click += (_, _) =>
        {
            var result = HookInstaller.Install(true, true, true);
            MessageBox.Show(this, result, "Hook", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        var save = Button("保存");
        save.Background = new SolidColorBrush(Color.FromRgb(36, 99, 235));
        save.Click += (_, _) => Save();
        buttons.Children.Add(install);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel();
        scroll.Content = stack;
        root.Children.Add(scroll);

        stack.Children.Add(Header("外观"));
        style = Combo(new[] { "Notch（贴顶）", "Capsule（胶囊）", "Indicator（指示条）" }, (int)settings.Style);
        stack.Children.Add(Row("收起样式", style));
        topMode = Combo(new[] { "任务栏下方", "贴屏幕顶（会盖住顶部任务栏）" }, (int)settings.TopMode);
        stack.Children.Add(Row("贴顶方式", topMode));
        topOffset = Input(settings.TopOffset.ToString());
        stack.Children.Add(Row("距顶部像素", topOffset));
        display = Combo(Displays.List().Select(d => d.Label), DisplayIndex(settings.Display));
        stack.Children.Add(Row("位于屏幕", display));
        startWithWindows = Check("开机启动", settings.StartWithWindows);
        stack.Children.Add(startWithWindows);
        hideFullscreen = Check("全屏时自动隐藏", settings.HideInFullscreen);
        stack.Children.Add(hideFullscreen);
        showClock = Check("显示时钟", settings.Appearance.ShowClock);
        stack.Children.Add(showClock);

        var themeBox = new StackPanel();
        theme = Combo(Palettes.All.Select(palette => palette.Name), ThemeIndex(settings.Appearance.Theme));
        theme.SelectionChanged += (_, _) => ShowSwatches();
        themeBox.Children.Add(theme);
        swatches.Margin = new Thickness(0, 7, 0, 2);
        themeBox.Children.Add(swatches);
        themeBox.Children.Add(new TextBlock
        {
            Text = "配色用于音乐竖线、CPU / 内存圆环、网速与强调色。",
            Foreground = new SolidColorBrush(Color.FromRgb(139, 147, 167)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });
        stack.Children.Add(Row("配色模板", themeBox));
        ShowSwatches();

        stack.Children.Add(Header("配额与统计列"));
        stack.Children.Add(new TextBlock
        {
            Text = "每一列可以分别选择额度来源和用量统计来源。留空名称时使用来源名称。",
            Foreground = new SolidColorBrush(Color.FromRgb(139, 147, 167)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        stack.Children.Add(columnPanel);
        RebuildColumns();

        var add = Button("+ 添加一列");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 8, 0, 0);
        add.Click += (_, _) =>
        {
            Collect();
            settings.Columns.Add(new ColumnSettings
            {
                Name = "新列",
                QuotaSource = SourceCatalog.Claude,
                UsageSource = SourceCatalog.Claude,
                Accent = "#5B8CFF",
            });
            RebuildColumns();
        };
        stack.Children.Add(add);

        stack.Children.Add(Header("额度预警"));
        quotaThreshold = Input(settings.Alerts.QuotaThreshold.ToString("0.##", CultureInfo.InvariantCulture));
        stack.Children.Add(Row("额度用量 ≥ (%)", quotaThreshold));
        balanceThreshold = Input(settings.Alerts.BalanceThreshold.ToString("0.##", CultureInfo.InvariantCulture));
        stack.Children.Add(Row("余额低于", balanceThreshold));
        stack.Children.Add(new TextBlock
        {
            Text = "达到条件时弹一次托盘通知，恢复正常后再次触发才会重新通知。余额按各账户自身币种比较（DeepSeek 为人民币，中转站多为美元）。填 0 关闭对应预警。",
            Foreground = new SolidColorBrush(Color.FromRgb(139, 147, 167)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        stack.Children.Add(Header("AI 会话"));
        sessionsEnabled = Check("接收 Hook 事件（Codex / Claude / OpenCode）", settings.Sessions.Enabled);
        stack.Children.Add(sessionsEnabled);
        notifyWaiting = Check("等待确认时通知", settings.Sessions.NotifyWaiting);
        stack.Children.Add(notifyWaiting);
        notifyDone = Check("任务完成时通知", settings.Sessions.NotifyDone);
        stack.Children.Add(notifyDone);

        stack.Children.Add(Header("系统监控"));
        monitorEnabled = Check("显示 CPU、内存、网络与磁盘", settings.Monitor.Enabled);
        stack.Children.Add(monitorEnabled);
        showBattery = Check("显示电池电量", settings.Monitor.ShowBattery);
        stack.Children.Add(showBattery);

        stack.Children.Add(Header("音乐"));
        musicEnabled = Check("显示系统媒体会话", settings.Music.Enabled);
        stack.Children.Add(musicEnabled);

        stack.Children.Add(Header("天气"));
        stack.Children.Add(new TextBlock
        {
            Text = "使用 Open-Meteo，无需 API 密钥。城市名例如「北京」「Shanghai」，也可以直接填坐标「39.90,116.40」。",
            Foreground = new SolidColorBrush(Color.FromRgb(139, 147, 167)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        weatherEnabled = Check("显示天气页", settings.Weather.Enabled);
        stack.Children.Add(weatherEnabled);
        weatherCity = Input(settings.Weather.City);
        stack.Children.Add(Row("城市", weatherCity));
        weatherAqi = Check("同时显示空气质量（AQI）", settings.Weather.ShowAqi);
        stack.Children.Add(weatherAqi);

        stack.Children.Add(Header("记事本"));
        stack.Children.Add(new TextBlock
        {
            Text = "纯本地待办：不联网、不需要账号，内容保存在数据目录的 notes.json，随改随存。",
            Foreground = new SolidColorBrush(Color.FromRgb(139, 147, 167)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        stack.Children.Add(Header("关于"));
        stack.Children.Add(new TextBlock
        {
            Text = "数据只在本机读取；API 密钥使用 Windows DPAPI 加密。\n数据目录：" + AppPaths.Data,
            Foreground = new SolidColorBrush(Color.FromRgb(139, 147, 167)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
        });

        Content = root;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var dark = 1;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyTheme()
    {
        var text = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF4));
        var muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB8));
        var field = new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x30));
        var border = new SolidColorBrush(Color.FromRgb(0x3A, 0x43, 0x56));

        // Default control templates read these system keys; override them for a dark popup.
        Resources[SystemColors.WindowBrushKey] = field;
        Resources[SystemColors.ControlBrushKey] = field;
        Resources[SystemColors.ControlTextBrushKey] = text;
        Resources[SystemColors.MenuBrushKey] = field;
        Resources[SystemColors.MenuTextBrushKey] = text;
        Resources[SystemColors.HighlightBrushKey] = new SolidColorBrush(Color.FromRgb(0x2C, 0x35, 0x46));
        Resources[SystemColors.HighlightTextBrushKey] = text;
        Resources[SystemColors.GrayTextBrushKey] = muted;

        var label = new Style(typeof(TextBlock));
        label.Setters.Add(new Setter(TextBlock.ForegroundProperty, text));
        label.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12d));
        Resources[typeof(TextBlock)] = label;

        var textBox = new Style(typeof(TextBox));
        textBox.Setters.Add(new Setter(Control.BackgroundProperty, field));
        textBox.Setters.Add(new Setter(Control.ForegroundProperty, text));
        textBox.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        textBox.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        textBox.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
        textBox.Setters.Add(new Setter(TextBox.CaretBrushProperty, text));
        Resources[typeof(TextBox)] = textBox;

        var password = new Style(typeof(PasswordBox));
        password.Setters.Add(new Setter(Control.BackgroundProperty, field));
        password.Setters.Add(new Setter(Control.ForegroundProperty, text));
        password.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        password.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        password.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(7, 5, 7, 5)));
        password.Setters.Add(new Setter(PasswordBox.CaretBrushProperty, text));
        Resources[typeof(PasswordBox)] = password;

        var button = new Style(typeof(Button));
        button.Setters.Add(new Setter(Control.BackgroundProperty, field));
        button.Setters.Add(new Setter(Control.ForegroundProperty, text));
        button.Setters.Add(new Setter(Control.BorderBrushProperty, border));
        button.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        button.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(14, 7, 14, 7)));
        button.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        Resources[typeof(Button)] = button;
    }

    private void RebuildColumns()
    {
        columnPanel.Children.Clear();
        editors.Clear();
        foreach (var column in settings.Columns)
        {
            var editor = new ColumnEditor(column, secrets);
            editors.Add(editor);
            columnPanel.Children.Add(editor.Build(() =>
            {
                Collect();
                var target = editor.Settings;
                settings.Columns.RemoveAll(c => c.Id == target.Id);
                secrets.Remove("column:" + target.Id);
                RebuildColumns();
            }));
        }
    }

    private static int ThemeIndex(string? id)
    {
        for (var index = 0; index < Palettes.All.Count; index++)
            if (string.Equals(Palettes.All[index].Id, id, StringComparison.OrdinalIgnoreCase)) return index;
        return 0;
    }

    private static int DisplayIndex(string? device)
    {
        var all = Displays.List();
        for (var index = 0; index < all.Count; index++)
            if (string.Equals(all[index].Device, device, StringComparison.OrdinalIgnoreCase)) return index;
        return 0;
    }

    private static PaletteTheme PaletteAt(int index) =>
        index >= 0 && index < Palettes.All.Count ? Palettes.All[index] : Palettes.All[0];

    /// <summary>Six dots in the order the colors are used: music, CPU, memory, download, upload, accent.</summary>
    private void ShowSwatches()
    {
        var palette = PaletteAt(theme.SelectedIndex);
        swatches.Children.Clear();
        foreach (var hex in new[] { palette.Music, palette.Cpu, palette.Memory, palette.Down, palette.Up, palette.Accent })
        {
            var swatch = new System.Windows.Shapes.Ellipse
            {
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0),
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            };
            swatches.Children.Add(swatch);
        }
    }

    private void Collect()
    {
        foreach (var editor in editors) editor.Collect();
    }

    private void Save()
    {
        Collect();
        settings.Style = (CompactStyle)Math.Max(0, style.SelectedIndex);
        settings.TopMode = (TopMode)Math.Max(0, topMode.SelectedIndex);
        settings.TopOffset = int.TryParse(topOffset.Text, out var offset) ? Math.Clamp(offset, 0, 200) : 6;
        var displays = Displays.List();
        settings.Display = display.SelectedIndex >= 0 && display.SelectedIndex < displays.Count
            ? displays[display.SelectedIndex].Device
            : null;
        settings.StartWithWindows = startWithWindows.IsChecked == true;
        settings.HideInFullscreen = hideFullscreen.IsChecked == true;
        settings.Appearance.ShowClock = showClock.IsChecked == true;
        settings.Appearance.Theme = PaletteAt(theme.SelectedIndex).Id;
        settings.Appearance.Accent = PaletteAt(theme.SelectedIndex).Accent;
        settings.Sessions.Enabled = sessionsEnabled.IsChecked == true;
        settings.Sessions.NotifyWaiting = notifyWaiting.IsChecked == true;
        settings.Sessions.NotifyDone = notifyDone.IsChecked == true;
        settings.Monitor.Enabled = monitorEnabled.IsChecked == true;
        settings.Monitor.ShowBattery = showBattery.IsChecked == true;
        settings.Music.Enabled = musicEnabled.IsChecked == true;
        settings.Weather.Enabled = weatherEnabled.IsChecked == true;
        settings.Weather.City = weatherCity.Text.Trim();
        settings.Weather.ShowAqi = weatherAqi.IsChecked == true;
        if (double.TryParse(quotaThreshold.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
            settings.Alerts.QuotaThreshold = Math.Clamp(percent, 0, 100);
        if (decimal.TryParse(balanceThreshold.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var balance))
            settings.Alerts.BalanceThreshold = Math.Max(0, balance);

        foreach (var editor in editors) editor.SaveSecret(secrets);
        Saved?.Invoke(settings);
        Close();
    }

    private static IslandSettings Clone(IslandSettings source)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(source);
        return System.Text.Json.JsonSerializer.Deserialize<IslandSettings>(json) ?? new IslandSettings();
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 8),
    };

    private static Grid Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    private static TextBox Input(string text) => new()
    {
        Text = text,
        Padding = new Thickness(6, 4, 6, 4),
        Background = new SolidColorBrush(Color.FromRgb(30, 36, 48)),
        Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(48, 56, 72)),
        BorderThickness = new Thickness(1),
    };

    private static ComboBox Combo(IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox
        {
            Background = new SolidColorBrush(Color.FromRgb(30, 36, 48)),
            Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248)),
        };
        foreach (var item in items) combo.Items.Add(item);
        combo.SelectedIndex = selected;
        return combo;
    }

    private static CheckBox Check(string text, bool value) => new()
    {
        Content = text,
        IsChecked = value,
        Margin = new Thickness(0, 4, 0, 4),
    };

    private static Button Button(string text) => new()
    {
        Content = text,
        Padding = new Thickness(14, 7, 14, 7),
        Background = new SolidColorBrush(Color.FromRgb(30, 36, 48)),
        Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248)),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand,
    };

    private sealed class ColumnEditor
    {
        public ColumnSettings Settings { get; }
        private readonly SecretStore secrets;
        private readonly TextBox name;
        private readonly ComboBox quota;
        private readonly ComboBox usage;
        private readonly TextBox site;
        private readonly PasswordBox key;
        private readonly CheckBox allowHttp;
        private readonly TextBox accent;

        public ColumnEditor(ColumnSettings settings, SecretStore secrets)
        {
            Settings = settings;
            this.secrets = secrets;

            name = Input(settings.Name);
            quota = SourceCombo(SourceCatalog.All.Where(s => s.Quota), settings.QuotaSource);
            usage = SourceCombo(SourceCatalog.All.Where(s => s.Usage), settings.UsageSource, allowEmpty: true);
            site = Input(settings.Site ?? "");
            key = new PasswordBox
            {
                Password = secrets.Get("column:" + settings.Id) ?? "",
                Padding = new Thickness(6, 4, 6, 4),
                Background = new SolidColorBrush(Color.FromRgb(30, 36, 48)),
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248)),
            };
            allowHttp = Check("允许 HTTP（仅限可信内网）", settings.AllowHttp);
            accent = Input(settings.Accent);
        }

        public Border Build(Action remove)
        {
            var stack = new StackPanel();
            stack.Children.Add(Row("名称", name));
            stack.Children.Add(Row("额度来源", quota));

            var hint = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 178)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };
            stack.Children.Add(hint);

            stack.Children.Add(Row("统计来源", usage));
            var usageHint = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(150, 160, 178)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, -2, 0, 8),
            };
            usageHint.Visibility = Visibility.Collapsed;
            stack.Children.Add(usageHint);
            var siteRow = Row("站点地址", site);
            var keyRow = Row("API 密钥", key);
            stack.Children.Add(siteRow);
            stack.Children.Add(keyRow);
            stack.Children.Add(allowHttp);
            stack.Children.Add(Row("主题色", accent));

            void Sync()
            {
                var info = quota.SelectedItem as SourceInfo;
                var text = info?.Hint ?? "";
                hint.Text = text;
                hint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
                siteRow.Visibility = info?.NeedsSite == true ? Visibility.Visible : Visibility.Collapsed;
                allowHttp.Visibility = siteRow.Visibility;
                keyRow.Visibility = info is { NeedsKey: true } || info?.Id == SourceCatalog.OpenCode
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                if (info is { Usage: false })
                {
                    usageHint.Text = info.Id == SourceCatalog.DeepSeek
                        ? "DeepSeek 官方 API 只提供账户余额，没有用量接口，因此这一列只显示余额，不显示今日 / 7 日用量。"
                        : "该来源没有用量接口，这一列不显示今日 / 7 日用量。";
                    usageHint.Visibility = Visibility.Visible;
                }
                else
                {
                    usageHint.Visibility = Visibility.Collapsed;
                }
            }

            var lastQuota = (quota.SelectedItem as SourceInfo)?.Id;
            quota.SelectionChanged += (_, _) =>
            {
                var info = quota.SelectedItem as SourceInfo;
                var usageInfo = usage.SelectedItem as SourceInfo;
                // Balance-only sources cannot drive usage; drop a mirrored usage source.
                if (info is not null && !info.Usage && usageInfo is not null && usageInfo.Id == lastQuota)
                    usage.SelectedIndex = 0;
                lastQuota = info?.Id;
                Sync();
            };
            Sync();

            var delete = Button("删除这一列");
            delete.HorizontalAlignment = HorizontalAlignment.Left;
            delete.Margin = new Thickness(0, 6, 0, 0);
            delete.Click += (_, _) => remove();
            stack.Children.Add(delete);

            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(21, 25, 34)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Child = stack,
            };
        }

        public void Collect()
        {
            Settings.Name = name.Text.Trim();
            var quotaInfo = quota.SelectedItem as SourceInfo;
            var usageInfo = usage.SelectedItem as SourceInfo;
            Settings.QuotaSource = quotaInfo?.Id ?? SourceCatalog.Claude;
            Settings.UsageSource = usageInfo?.Id ?? "";
            Settings.Site = string.IsNullOrWhiteSpace(site.Text) ? null : site.Text.Trim();
            Settings.AllowHttp = allowHttp.IsChecked == true;
            Settings.Accent = string.IsNullOrWhiteSpace(accent.Text) ? "#5B8CFF" : accent.Text.Trim();
        }

        public void SaveSecret(SecretStore store)
        {
            var name = "column:" + Settings.Id;
            if (string.IsNullOrWhiteSpace(key.Password)) store.Remove(name);
            else store.Set(name, key.Password);
        }

        private static ComboBox SourceCombo(IEnumerable<SourceInfo> sources, string selected, bool allowEmpty = false)
        {
            var combo = new ComboBox
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 36, 48)),
                Foreground = new SolidColorBrush(Color.FromRgb(242, 244, 248)),
            };
            var template = new DataTemplate(typeof(SourceInfo));
            var factory = new FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new Binding("Name"));
            template.VisualTree = factory;
            combo.ItemTemplate = template;
            if (allowEmpty) combo.Items.Add(new SourceInfo("", "（不统计）", false, false));
            foreach (var source in sources) combo.Items.Add(source);
            combo.SelectedItem = combo.Items.OfType<SourceInfo>().FirstOrDefault(s => s.Id == selected) ?? combo.Items[0];
            return combo;
        }
    }
}
