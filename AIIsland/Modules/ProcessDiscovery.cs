using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AIIsland.Modules;
public sealed record AppProcess(int Id, string Name, string? Path, string? Directory = null);
public sealed record ProcessSnapshot(IReadOnlyList<AppProcess> Apps)
{
    public AppProcess? Find(string name) => Apps.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}
public sealed class ProcessDiscovery
{
    public Task<ProcessSnapshot> ReadAsync(CancellationToken token) => Task.Run(() => {
        var result = new List<AppProcess>();
        foreach (var name in new[] { "codex", "claude", "opencode", "QQMusic", "cloudmusic", "clash-verge", "verge-mihomo" })
        {
            token.ThrowIfCancellationRequested();
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                    result.Add(new(process.Id, name, path, name == "opencode" ? AIIsland.Core.ProcessLifetime.Directory(process.Id) : null));
                }
            }
        }
        return new ProcessSnapshot(result);
    }, token);
    public static Task OpenAsync(AppProcess? app)
    {
        if (app?.Path == null) throw new InvalidOperationException("无法定位应用程序。");
        Process.Start(new ProcessStartInfo(app.Path) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}
