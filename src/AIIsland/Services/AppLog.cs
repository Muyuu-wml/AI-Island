using System.IO;
using AIIsland.Core;

namespace AIIsland.Services;

public static class AppLog
{
    private static readonly object Gate = new();

    public static void Write(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            var path = Path.Combine(AppPaths.Logs, "app-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
            lock (Gate)
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {exception}{Environment.NewLine}");
        }
        catch { }
    }
}
