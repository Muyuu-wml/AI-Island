using System.Diagnostics;

namespace AIIsland.Core.Monitoring;

public sealed record ProcessSample(int Pid, string Name, double CpuPercent, long WorkingSet, bool CpuKnown, string? Path);

/// <summary>
/// Samples per-process CPU from TotalProcessorTime deltas, which needs no extra
/// Windows performance counters or services.
/// </summary>
public sealed class ProcessMonitor
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> previous = new();
    private readonly Dictionary<int, string?> paths = new();

    public IReadOnlyList<ProcessSample> Sample(int count = 0)
    {
        var now = DateTimeOffset.Now;
        var results = new List<ProcessSample>();
        var current = new Dictionary<int, (TimeSpan, DateTimeOffset)>();
        var livePaths = new Dictionary<int, string?>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var name = process.ProcessName;
                if (name is "Idle" or "System" or "Registry" or "Memory Compression") continue;
                if (process.HasExited) continue;

                var cpuTime = process.TotalProcessorTime;
                double percent = 0;
                var known = false;
                if (previous.TryGetValue(process.Id, out var old))
                {
                    var seconds = (now - old.At).TotalSeconds;
                    if (seconds > 0.2)
                    {
                        percent = Math.Clamp((cpuTime - old.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
                        known = true;
                    }
                }

                var memory = process.WorkingSet64;
                if (memory <= 0) continue;
                current[process.Id] = (cpuTime, now);
                livePaths[process.Id] = LookupPath(process);
                results.Add(new ProcessSample(process.Id, name, percent, memory, known, livePaths[process.Id]));
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            finally { process.Dispose(); }
        }

        previous.Clear();
        foreach (var pair in current) previous[pair.Key] = pair.Value;
        paths.Clear();
        foreach (var pair in livePaths) paths[pair.Key] = pair.Value;

        var ranked = results
            .Where(sample => sample.CpuKnown)
            .GroupBy(sample => sample.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcessSample(
                0,
                group.Key,
                Math.Clamp(group.Sum(sample => sample.CpuPercent), 0, 100),
                group.Sum(sample => sample.WorkingSet),
                true,
                group.Select(sample => sample.Path).FirstOrDefault(path => !string.IsNullOrEmpty(path))))
            .OrderByDescending(sample => sample.CpuPercent)
            .ThenByDescending(sample => sample.WorkingSet);

        // count <= 0 returns every process so the UI can sort by CPU or memory before trimming.
        return (count > 0 ? ranked.Take(count) : ranked).ToArray();
    }

    private string? LookupPath(Process process)
    {
        if (paths.TryGetValue(process.Id, out var cached)) return cached;
        try { return process.MainModule?.FileName; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { return null; }
    }
}
