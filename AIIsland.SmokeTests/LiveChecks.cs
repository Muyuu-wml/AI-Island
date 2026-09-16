using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIIsland.Modules;
using AIIsland.Services;
using AIIsland.UI;

internal static class LiveChecks
{
    public static async Task MusicRoundtrip()
    {
        using var module = new QQMusicModule();
        await module.StartAsync(CancellationToken.None);
        var processes = await new ProcessDiscovery().ReadAsync(CancellationToken.None);
        await module.RefreshAsync(processes, CancellationToken.None);
        if (!module.Toggle.CanExecute(null)) throw new InvalidOperationException("QQ Music media control not available");
        var before = module.IsPlaying;
        async Task ToggleAndWait(bool target)
        {
            module.Toggle.Execute(null);
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(100);
                await module.RefreshAsync(processes, CancellationToken.None);
                if (!module.IsBusy && module.IsPlaying == target) return;
            }
            throw new InvalidOperationException("QQ Music playback state did not change: " + module.Note);
        }
        try { await ToggleAndWait(!before); Console.WriteLine("PASS Actual QQMusic play/pause request and readback"); }
        finally
        {
            await module.RefreshAsync(processes, CancellationToken.None);
            if (module.IsPlaying != before) await ToggleAndWait(before);
            if (module.IsPlaying != before) throw new InvalidOperationException("QQ Music playback state could not be restored");
            Console.WriteLine("PASS Original QQMusic playing/paused state restored");
        }
    }
    public static async Task Probe(string root)
    {
        var window = new IslandWindow(true);
        try
        {
            window.Show(); await Task.Delay(3500);
            async Task Sample(string phase)
            {
                using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime.TotalSeconds; var clock = Stopwatch.StartNew();
                await Task.Delay(8000); process.Refresh();
                var sample = new { Phase = phase, WorkingSetMB = Math.Round(process.WorkingSet64 / 1048576d, 1), PrivateMB = Math.Round(process.PrivateMemorySize64 / 1048576d, 1), CpuPercent = Math.Round((process.TotalProcessorTime.TotalSeconds - cpu) / clock.Elapsed.TotalSeconds / Environment.ProcessorCount * 100, 3) };
                var line = System.Text.Json.JsonSerializer.Serialize(sample); Console.WriteLine(line);
                Directory.CreateDirectory(Path.Combine(root, "artifacts")); File.AppendAllText(Path.Combine(root, "artifacts", "module-performance.jsonl"), line + Environment.NewLine);
            }
            await Sample("collapsed"); window.ToggleDrawer(); await Task.Delay(700); await Sample("drawer");
            Console.WriteLine($"QQMusic visible={window.ViewModel.Music.IsVisible}; play/pause available={window.ViewModel.Music.Toggle.CanExecute(null)}; note={window.ViewModel.Music.Note}");
            Console.WriteLine($"Clash visible={window.ViewModel.Clash.IsVisible}; system proxy on={window.ViewModel.Clash.IsOn}; toggle available={window.ViewModel.Clash.Toggle.CanExecute(null)}");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var directory = Path.Combine(root, "artifacts", "screenshots"); Directory.CreateDirectory(directory);
            using var file = File.Create(Path.Combine(directory, "07-live-modules.png")); encoder.Save(file);
        }
        finally { window.Close(); }
    }
    public static async Task ProxyRoundtrip()
    {
        var service = new SystemProxyService(); var before = service.Read();
        var processes = await new ProcessDiscovery().ReadAsync(CancellationToken.None);
        using var module = new ClashModule(service);
        await module.RefreshAsync(processes, CancellationToken.None);
        if (!module.Toggle.CanExecute(null) || !before.Enabled) throw new InvalidOperationException("Roundtrip requires an already-enabled owned Clash proxy without conflicts.");
        ProxySnapshot? changed = null;
        try
        {
            module.Toggle.Execute(null);
            for (var i = 0; module.IsBusy && i < 200; i++) await Task.Delay(10);
            if (module.IsBusy) throw new InvalidOperationException("Proxy operation timed out");
            changed = service.Read();
            if (changed.Enabled) throw new InvalidOperationException("Proxy did not turn off: " + module.Note);
            Console.WriteLine("PASS Native WinINet system proxy off and readback");
        }
        finally
        {
            var current = service.Read();
            if (changed != null && current == changed || current == before with { Flags = (before.Flags & ~2) | 1 })
                service.SetEnabled(before, before.Server, before.Enabled);
            if (service.Read() != before) throw new InvalidOperationException("System proxy changed externally or could not be restored; inspect current settings.");
            Console.WriteLine("PASS Original system proxy configuration restored exactly");
        }
    }
}
