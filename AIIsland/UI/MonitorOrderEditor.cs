using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIIsland.Services;

namespace AIIsland.UI;

public sealed class MonitorOrderEditor : UserControl
{
    private readonly List<string> _order;
    private readonly Dictionary<string, MonitorRow> _rows = new(StringComparer.Ordinal);
    private readonly StackPanel _panel = new();

    public MonitorOrderEditor(Settings current)
    {
        _order = new List<string>(MonitorOrderCatalog.Normalize(current.MonitorOrder));
        Content = _panel;
        foreach (var id in _order)
        {
            var name = MonitorOrderCatalog.Name(id);
            var enabled = new CheckBox
            {
                Content = name,
                IsChecked = id switch
                {
                    "resources" => current.SystemResources,
                    "balance" => current.BalanceEnabled,
                    "qqmusic" => current.QQMusic,
                    "neteasemusic" => current.NetEaseMusic,
                    "clash" => current.Clash,
                    _ => false
                },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 6)
            };
            var up = CreateMoveButton("↑", "上移 " + name);
            var down = CreateMoveButton("↓", "下移 " + name);
            up.Click += (_, _) => Move(id, -1);
            down.Click += (_, _) => Move(id, 1);

            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(up, 1);
            Grid.SetColumn(down, 2);
            row.Children.Add(enabled);
            row.Children.Add(up);
            row.Children.Add(down);
            _rows.Add(id, new MonitorRow(row, enabled, up, down));
        }
        RefreshRows();
    }

    public string[] Order => _order.ToArray();

    public bool IsMonitorEnabled(string id) => _rows[id].Enabled.IsChecked == true;

    public void SetMonitorEnabled(string id, bool enabled) => _rows[id].Enabled.IsChecked = enabled;

    public bool Move(string id, int offset)
    {
        if (offset != -1 && offset != 1) return false;
        var index = _order.IndexOf(id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _order.Count) return false;
        (_order[index], _order[target]) = (_order[target], _order[index]);
        RefreshRows();
        return true;
    }

    private static Button CreateMoveButton(string text, string description)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = description,
            Width = 28,
            Height = 26,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, description);
        return button;
    }

    private void RefreshRows()
    {
        _panel.Children.Clear();
        for (var index = 0; index < _order.Count; index++)
        {
            var row = _rows[_order[index]];
            row.Up.IsEnabled = index > 0;
            row.Down.IsEnabled = index < _order.Count - 1;
            _panel.Children.Add(row.Container);
        }
    }

    private sealed record MonitorRow(Grid Container, CheckBox Enabled, Button Up, Button Down);
}
