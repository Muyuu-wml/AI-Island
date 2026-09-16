using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using AIIsland.Core;

namespace AIIsland.Services;
public sealed class EventInbox
{
    private readonly string directory;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private bool recovered;
    public EventInbox(string? directory = null)
    {
        this.directory = directory ?? Path.Combine(SettingsService.DataDirectory, "inbox");
        Directory.CreateDirectory(this.directory);
    }
    public void Drain(Action<IslandEvent> consume, Action<IslandEvent>? recover = null)
    {
        var registry = Path.Combine(directory, "sessions");
        var batch = new System.Collections.Generic.List<IslandEvent>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(256))
        {
            try
            {
                if (new FileInfo(file).Length <= 32768)
                {
                    var value = JsonSerializer.Deserialize<IslandEvent>(File.ReadAllText(file));
                    if (value != null && value.At <= DateTimeOffset.UtcNow.AddSeconds(5))
                    {
                        batch.Add(value);
                    }
                }
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            { try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        foreach (var value in batch.OrderBy(e => e.At))
        {
            // A failed connection cache write must not discard a live task event.
            try { SessionRegistry.Remember(registry, value); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (!recovered)
        {
            if (recover != null) SessionRegistry.Read(registry, value => { if (value.At < started) recover(value); });
            recovered = true;
        }
        foreach (var group in batch.Where(e => e.At < started).GroupBy(e => (e.Provider, e.SessionId)))
        {
            var latest = group.OrderBy(e => e.At).Last();
            if (SessionRegistry.IsAlive(latest)) recover?.Invoke(latest);
        }
        foreach (var value in batch.Where(e => e.At >= started).OrderBy(e => e.At)) consume(value);
    }
}
