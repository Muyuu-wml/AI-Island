using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIIsland.Services;

namespace AIIsland.Modules;

public sealed record DiskUsage
{
    public string Name { get; }
    public long TotalBytes { get; }
    public long FreeBytes { get; }
    public bool HasCapacity => TotalBytes > 0 && FreeBytes >= 0 && FreeBytes <= TotalBytes;
    public double UsedPercent => HasCapacity ? (TotalBytes - FreeBytes) / (double)TotalBytes * 100 : 0;
    public string PercentLabel => HasCapacity ? ResourceDisplay.Percent(UsedPercent) : "--";
    public string CapacityLabel => HasCapacity ? $"可用 {ResourceDisplay.Bytes(FreeBytes)}\n共 {ResourceDisplay.Bytes(TotalBytes)}" : "容量未知";

    public DiskUsage(DiskSnapshot snapshot)
    {
        Name = snapshot.Name;
        TotalBytes = snapshot.TotalBytes;
        FreeBytes = snapshot.FreeBytes;
    }
}

internal static class ResourceDisplay
{
    public static string Percent(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "%";

    public static string Bytes(long value)
    {
        var amount = Math.Max(0, value) / 1024d / 1024 / 1024;
        return amount >= 1024
            ? (amount / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " TB"
            : amount.ToString("0.#", CultureInfo.InvariantCulture) + " GB";
    }
}

public sealed class SystemResourcesModule : ModuleBase
{
    private static readonly SystemResourceSample Unknown = new(null, null);
    private readonly ISystemResourcesReader reader;
    private readonly CpuUsageTracker cpuTracker = new();
    private Task<SystemResourceSample>? usageRead;
    private Task<IReadOnlyList<DiskSnapshot>>? diskRead;
    private DateTime nextDiskRead = DateTime.MinValue;
    private int generation, usageGeneration, diskGeneration, refreshing;
    private bool enabled = true;
    private double? cpu, memory;
    private string memoryUsage = "等待采样";
    private string diskStatus = "正在读取磁盘";

    public override string Id => "resources";
    public bool HasCpu => cpu.HasValue;
    public bool HasMemory => memory.HasValue;
    public double CpuPercent => cpu ?? 0;
    public double MemoryPercent => memory ?? 0;
    public string CpuLabel => cpu is { } value ? ResourceDisplay.Percent(value) : "--";
    public string MemoryLabel => memory is { } value ? ResourceDisplay.Percent(value) : "--";
    public string MemoryUsage { get => memoryUsage; private set => Set(ref memoryUsage, value); }
    public string Compact => $"CPU {CpuLabel} · 内存 {MemoryLabel}";
    public ObservableCollection<DiskUsage> Disks { get; } = new();
    public bool HasDisks => Disks.Count > 0;
    public string DiskStatus { get => diskStatus; private set => Set(ref diskStatus, value); }

    public SystemResourcesModule(ISystemResourcesReader? reader = null)
    {
        this.reader = reader ?? new SystemResourcesReader();
        IsVisible = true;
    }

    public void Configure(bool value)
    {
        if (Disposed) return;
        if (enabled != value)
        {
            enabled = value;
            generation++;
            cpuTracker.Reset();
            ApplyUsage(Unknown);
            Disks.Clear();
            Changed(nameof(Disks));
            Changed(nameof(HasDisks));
            DiskStatus = "正在读取磁盘";
            nextDiskRead = DateTime.MinValue;
        }
        IsVisible = enabled;
    }

    public override async Task RefreshAsync(ProcessSnapshot processes, CancellationToken token)
    {
        if (Disposed || !enabled || token.IsCancellationRequested || Interlocked.Exchange(ref refreshing, 1) != 0) return;
        try
        {
            var currentGeneration = generation;
            PollDisks(currentGeneration);
            if (diskRead == null && DateTime.UtcNow >= nextDiskRead)
            {
                diskGeneration = currentGeneration;
                diskRead = Task.Run(ReadDisksSafely);
                nextDiskRead = DateTime.UtcNow.AddSeconds(10);
            }

            if (usageRead == null)
            {
                usageGeneration = currentGeneration;
                usageRead = Task.Run(ReadUsageSafely);
            }

            try
            {
                var result = await usageRead.WaitAsync(TimeSpan.FromSeconds(1), token);
                usageRead = null;
                if (Disposed || !enabled || token.IsCancellationRequested || generation != currentGeneration || usageGeneration != currentGeneration) return;
                ApplyUsage(result);
                // A quick first disk scan can be displayed immediately; slow scans remain independent.
                PollDisks(currentGeneration);
            }
            catch (TimeoutException)
            {
                if (!Disposed && enabled && generation == currentGeneration && !token.IsCancellationRequested)
                    ApplyUsage(Unknown);
            }
        }
        finally { Volatile.Write(ref refreshing, 0); }
    }

    private SystemResourceSample ReadUsageSafely()
    {
        try { return reader.ReadUsage(); }
        catch (Exception) { return Unknown; }
    }

    private IReadOnlyList<DiskSnapshot> ReadDisksSafely()
    {
        try { return reader.ReadDisks(); }
        catch (Exception) { return Array.Empty<DiskSnapshot>(); }
    }

    private void PollDisks(int currentGeneration)
    {
        if (diskRead is not { IsCompletedSuccessfully: true }) return;
        var result = diskRead.Result;
        diskRead = null;
        if (Disposed || !enabled || generation != currentGeneration || diskGeneration != currentGeneration) return;
        var disks = result.Select(d => new DiskUsage(d)).OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (!Disks.SequenceEqual(disks))
        {
            Disks.Clear();
            foreach (var disk in disks) Disks.Add(disk);
            Changed(nameof(Disks));
            Changed(nameof(HasDisks));
        }
        DiskStatus = Disks.Count > 0 ? "" : "暂无可读取的磁盘";
    }

    private void ApplyUsage(SystemResourceSample sample)
    {
        var nextCpu = cpuTracker.Sample(sample.Cpu);
        double? nextMemory = null;
        var nextMemoryUsage = "内存状态不可用";
        if (sample.Memory is { TotalBytes: > 0, AvailableBytes: >= 0 } state && state.AvailableBytes <= state.TotalBytes)
        {
            var used = state.TotalBytes - state.AvailableBytes;
            nextMemory = used / (double)state.TotalBytes * 100;
            nextMemoryUsage = $"{ResourceDisplay.Bytes(used)} / {ResourceDisplay.Bytes(state.TotalBytes)}";
        }
        if (Set(ref cpu, nextCpu, nameof(CpuPercent)))
        {
            Changed(nameof(CpuLabel));
            Changed(nameof(HasCpu));
            Changed(nameof(Compact));
        }
        if (Set(ref memory, nextMemory, nameof(MemoryPercent)))
        {
            Changed(nameof(MemoryLabel));
            Changed(nameof(HasMemory));
            Changed(nameof(Compact));
        }
        MemoryUsage = nextMemoryUsage;
    }

    public override void Dispose()
    {
        if (Disposed) return;
        generation++;
        enabled = false;
        cpuTracker.Reset();
        base.Dispose();
    }
}
