using System.Drawing;
using System.Windows.Forms;
using AIIsland.UI;

namespace AIIsland.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon icon;

    public TrayIcon(IslandWindow window)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => window.ToggleVisible());
        menu.Items.Add("设置", null, (_, _) => window.OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => window.ExitApp());

        icon = new NotifyIcon
        {
            Text = "AI Island",
            Visible = true,
            ContextMenuStrip = menu,
        };

        try
        {
            var path = Environment.ProcessPath;
            icon.Icon = path is null ? SystemIcons.Application : Icon.ExtractAssociatedIcon(path) ?? SystemIcons.Application;
        }
        catch { icon.Icon = SystemIcons.Application; }

        icon.DoubleClick += (_, _) => window.OpenSettings();
    }

    public void Notify(string title, string text)
    {
        try
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = text;
            icon.ShowBalloonTip(3000);
        }
        catch { }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
