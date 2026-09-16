using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIIsland.Modules;
using AIIsland.Services;
using AIIsland.UI;

internal static class SystemResourcesChecks
{
    private const long GiB = 1024L * 1024 * 1024;
    private static readonly ProcessSnapshot NoProcesses = new(Array.Empty<AppProcess>());
    private static readonly DiskSnapshot[] PreviewDisks = {
        new("C:", 512 * GiB, 320 * GiB),
        new("D:", 1024 * GiB, 1024 * GiB),
        new("E:", 256 * GiB, 0)
    };

    public static async Task Run(string root, bool probe = false)
    {
        CheckCpu();
        await CheckModuleAndRender(root);
        await CheckLateUsage(dispose: false);
        await CheckLateUsage(dispose: true);
        await CheckSlowDisks();
        await CheckLateDisks();
        if (probe) await Probe();
    }

    private static void CheckCpu()
    {
        var tracker = new CpuUsageTracker();
        Check(tracker.Sample(new CpuTimes(1000, 2000, 3000)) == null,
            "First CPU sample is unknown instead of a fabricated zero");
        Check(Near(tracker.Sample(new CpuTimes(1760, 2800, 3200)), 24),
            "CPU subtracts idle time from the kernel plus user time delta");
        Check(Near(CpuUsageTracker.Calculate(new CpuTimes(2000, 4000, 4000), new CpuTimes(4000, 9000, 7000)), 75),
            "CPU uses aggregate processor times without dividing usage by the processor count again");
        Check(tracker.Sample(new CpuTimes(10, 20, 30)) == null &&
              Near(tracker.Sample(new CpuTimes(30, 100, 50)), 80),
            "CPU counter rollback resets the baseline and the next interval recovers");
        tracker.Reset();
        Check(tracker.Sample(new CpuTimes(100, 200, 100)) == null &&
              Near(tracker.Sample(new CpuTimes(200, 300, 100)), 0) &&
              Near(tracker.Sample(new CpuTimes(200, 400, 200)), 100),
            "A reset needs a fresh baseline and genuine idle/busy intervals report zero/100 percent");
        Check(tracker.Sample(null) == null && tracker.Sample(new CpuTimes(200, 400, 200)) == null &&
              tracker.Sample(new CpuTimes(200, 400, 200)) == null &&
              CpuUsageTracker.Calculate(new CpuTimes(0, 0, 0), new CpuTimes(11, 10, 5)) == null,
            "Missing, unchanged, and invalid CPU counters do not invent a percentage");
        Check(!new DiskUsage(new DiskSnapshot("X:", 0, 0)).HasCapacity &&
              !new DiskUsage(new DiskSnapshot("X:", 100, 101)).HasCapacity &&
              !new DiskUsage(new DiskSnapshot("X:", 100, -1)).HasCapacity,
            "Invalid disk capacity remains unknown instead of looking empty or full");
    }

    private static async Task CheckModuleAndRender(string root)
    {
        var reader = new SampleReader();
        using var module = new SystemResourcesModule(reader);
        await Refresh(module);
        Check(module.IsVisible && !module.HasCpu && module.CpuLabel == "--" &&
              module.HasMemory && Near(module.MemoryPercent, 58),
            "The production module exposes memory immediately and waits for a CPU interval");
        await Refresh(module);
        Check(Near(module.CpuPercent, 24) && Near(module.MemoryPercent, 58) &&
              module.Compact == "CPU 24% · 内存 58%",
            "The production module provides the requested compact CPU and memory summary");
        await Until(() => module.Disks.Count == PreviewDisks.Length, () => Refresh(module));
        Check(module.Disks.Select(d => d.Name).SequenceEqual(new[] { "C:", "D:", "E:" }) &&
              Near(module.Disks[0].UsedPercent, 37.5) && module.Disks[1].UsedPercent == 0 &&
              module.Disks[2].UsedPercent == 100 && module.Disks[0].CapacityLabel.Contains("可用 320 GB"),
            "Each disk exposes its used fraction and available/total capacity, including empty and full disks");
        await Render(root, module, reader);
        module.Configure(false);
        var reads = reader.UsageReads;
        await Refresh(module);
        Check(!module.IsVisible && !module.HasCpu && !module.HasMemory && module.Disks.Count == 0 && reader.UsageReads == reads,
            "Disabling resources clears its card and stops collection");
        module.Configure(true);
        await Refresh(module);
        Check(module.IsVisible && !module.HasCpu && module.HasMemory,
            "Re-enabling resources starts from a new CPU baseline");
    }

    private static async Task Render(string root, SystemResourcesModule module, SampleReader reader)
    {
        var trace = new StringWriter();
        using var listener = new TextWriterTraceListener(trace);
        var source = PresentationTraceSources.DataBindingSource;
        var previousLevel = source.Switch.Level;
        source.Listeners.Add(listener);
        source.Switch.Level = SourceLevels.Warning;
        try
        {
            module.IsExpanded = true;
            var view = new SystemResourcesView { DataContext = module };
            var compact = new TextBlock { FontSize = 12, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 16) };
            compact.SetBinding(TextBlock.TextProperty, new Binding(nameof(module.Compact)) { Source = module });
            var stack = new StackPanel();
            stack.Children.Add(compact);
            stack.Children.Add(view);
            var surface = new Border {
                Width = 374, Padding = new Thickness(18), CornerRadius = new CornerRadius(18),
                Background = new SolidColorBrush(Color.FromRgb(27, 31, 40)), Child = stack
            };
            void Layout()
            {
                surface.Measure(new Size(surface.Width, double.PositiveInfinity));
                surface.Arrange(new Rect(surface.DesiredSize));
                surface.UpdateLayout();
            }
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Layout();
            var bars = Visuals<ProgressBar>(view).ToArray();
            var rings = Visuals<UsageRing>(view).ToArray();
            Check(bars.Length == 2 && Near(bars[0].Value, 24) && Near(bars[1].Value, 58) &&
                  bars.All(b => b.Minimum == 0 && b.Maximum == 100 && b.ActualWidth > 0),
                "The real resource view binds CPU and memory to percentage bars");
            Check(rings.Length == 3 && Near(rings[0].Value, 37.5) && rings[1].Value == 0 && rings[2].Value == 100 &&
                  rings.All(r => r.ActualWidth > 0 && r.ActualHeight > 0),
                "The real resource view renders a ring for normal, empty, and full disks");
            var content = string.Join(" ", Visuals<TextBlock>(surface).Select(t => t.Text));
            Check(compact.Text == "CPU 24% · 内存 58%" &&
                  !content.Contains("网络") && !content.Contains("上传") && !content.Contains("下载"),
                "The rendered summary contains only CPU and memory and the resource card has no network section");

            var directory = Path.Combine(Path.GetFullPath(root), "artifacts", "screenshots");
            Directory.CreateDirectory(directory);
            var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(surface);
            using (var stream = File.Create(Path.Combine(directory, "system-resources.png")))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                encoder.Save(stream);
            }

            reader.CpuLoad = 80;
            reader.MemoryUsed = 25;
            await Refresh(module);
            await Dispatcher.Yield(DispatcherPriority.DataBind);
            Layout();
            Check(Near(bars[0].Value, 80) && Near(bars[1].Value, 25) && compact.Text == "CPU 80% · 内存 25%",
                "Production percentage bars and compact text follow live property changes");
            listener.Flush();
            Check(string.IsNullOrWhiteSpace(trace.ToString()),
                "The actual resource controls render without WPF binding errors or warnings");
        }
        finally
        {
            source.Switch.Level = previousLevel;
            source.Listeners.Remove(listener);
        }
    }

    private static async Task CheckLateUsage(bool dispose)
    {
        using var gate = new ManualResetEventSlim(false);
        var started = NewSignal();
        var reader = new DelegateReader {
            Usage = () => { started.TrySetResult(); AwaitGate(gate); return new SystemResourceSample(new CpuTimes(1, 2, 3), new MemorySnapshot(100, 5)); }
        };
        using var module = new SystemResourcesModule(reader);
        var refresh = Refresh(module);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (dispose) module.Dispose(); else module.Configure(false);
            var before = module.Compact;
            var changes = 0;
            module.PropertyChanged += (_, _) => changes++;
            gate.Set();
            await refresh;
            await Refresh(module);
            Check(changes == 0 && module.Compact == before && !module.HasMemory,
                (dispose ? "Disposal" : "Disabling resources") + " ignores an in-flight usage result");
        }
        finally { gate.Set(); await refresh; }
    }

    private static async Task CheckSlowDisks()
    {
        using var gate = new ManualResetEventSlim(false);
        var started = NewSignal();
        var samples = new SampleReader();
        var diskReads = 0;
        var reader = new DelegateReader {
            Usage = samples.ReadUsage,
            Disks = () => { Interlocked.Increment(ref diskReads); started.TrySetResult(); AwaitGate(gate); return PreviewDisks; }
        };
        using var module = new SystemResourcesModule(reader);
        try
        {
            await Refresh(module).WaitAsync(TimeSpan.FromSeconds(2));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Refresh(module).WaitAsync(TimeSpan.FromSeconds(2));
            Check(Near(module.CpuPercent, 24) && Near(module.MemoryPercent, 58) && module.Disks.Count == 0 && diskReads == 1,
                "A blocked disk scan does not block CPU/memory updates or spawn duplicate scans");
            gate.Set();
            await Until(() => module.Disks.Count == PreviewDisks.Length, () => Refresh(module));
            Check(diskReads == 1 && module.Disks.Count == PreviewDisks.Length,
                "A completed background disk scan is displayed on the next refresh");
        }
        finally { gate.Set(); }
    }

    private static async Task CheckLateDisks()
    {
        using var gate = new ManualResetEventSlim(false);
        var started = NewSignal();
        var finished = NewSignal();
        var samples = new SampleReader();
        var scans = 0;
        var reader = new DelegateReader {
            Usage = samples.ReadUsage,
            Disks = () => {
                if (Interlocked.Increment(ref scans) == 1)
                {
                    started.TrySetResult();
                    AwaitGate(gate);
                    finished.TrySetResult();
                    return new[] { new DiskSnapshot("STALE:", 100, 1) };
                }
                return PreviewDisks;
            }
        };
        using var module = new SystemResourcesModule(reader);
        try
        {
            await Refresh(module);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            module.Configure(false);
            module.Configure(true);
            var sawStale = false;
            module.Disks.CollectionChanged += (_, _) => sawStale |= module.Disks.Any(d => d.Name == "STALE:");
            gate.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => module.Disks.Count == PreviewDisks.Length, () => Refresh(module));
            Check(!sawStale && scans == 2 && module.Disks.All(d => d.Name != "STALE:"),
                "Re-enabling resources discards a late disk scan from the previous enabled session");
        }
        finally { gate.Set(); }
    }

    private static async Task Probe()
    {
        var reader = new SystemResourcesReader();
        var tracker = new CpuUsageTracker();
        tracker.Sample((await Task.Run(reader.ReadUsage)).Cpu);
        await Task.Delay(1000);
        var sample = await Task.Run(reader.ReadUsage);
        var cpu = tracker.Sample(sample.Cpu);
        Check(cpu is >= 0 and <= 100 && sample.Memory is { TotalBytes: > 0, AvailableBytes: >= 0 } &&
              sample.Memory.AvailableBytes <= sample.Memory.TotalBytes,
            "Windows APIs return a valid live CPU interval and physical memory snapshot");
        var disks = await Task.Run(reader.ReadDisks).WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine($"Resource live probe: CPU={cpu:F1}%, memory={(sample.Memory!.TotalBytes - sample.Memory.AvailableBytes) / (double)sample.Memory.TotalBytes * 100:F1}%, disks={disks.Count}");
        foreach (var disk in disks)
            Console.WriteLine($"  {disk.Name} {new DiskUsage(disk).CapacityLabel.Replace('\n', ' ')}");
    }

    private static Task Refresh(SystemResourcesModule module) => module.RefreshAsync(NoProcesses, CancellationToken.None);
    private static bool Near(double? actual, double expected) => actual.HasValue && Math.Abs(actual.Value - expected) < .000001;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void AwaitGate(ManualResetEventSlim gate)
    {
        if (!gate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The test reader was not released.");
    }
    private static async Task Until(Func<bool> condition, Func<Task> advance)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
            await advance();
        }
        if (!condition()) throw new Exception("A completed resource scan was not applied within the bounded refresh loop.");
    }
    private static IEnumerable<T> Visuals<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Visuals<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private sealed class DelegateReader : ISystemResourcesReader
    {
        public Func<SystemResourceSample> Usage { get; init; } = () => new SystemResourceSample(null, null);
        public Func<IReadOnlyList<DiskSnapshot>> Disks { get; init; } = () => Array.Empty<DiskSnapshot>();
        public SystemResourceSample ReadUsage() => Usage();
        public IReadOnlyList<DiskSnapshot> ReadDisks() => Disks();
    }
    private sealed class SampleReader : ISystemResourcesReader
    {
        private ulong idle = 1000, kernel = 2000;
        public int CpuLoad = 24, MemoryUsed = 58, UsageReads;
        public SystemResourceSample ReadUsage()
        {
            if (Interlocked.Increment(ref UsageReads) > 1)
            {
                idle += (ulong)(100 - Volatile.Read(ref CpuLoad)) * 10;
                kernel += 1000;
            }
            var total = 32 * GiB;
            return new SystemResourceSample(new CpuTimes(idle, kernel, 3000), new MemorySnapshot(total, total * (100 - Volatile.Read(ref MemoryUsed)) / 100));
        }
        public IReadOnlyList<DiskSnapshot> ReadDisks() => PreviewDisks;
    }
}
