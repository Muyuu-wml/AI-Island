using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIIsland.Core;

// Only connection metadata is retained; historical tasks are never replayed.
public static class SessionRegistry
{
    public static bool IsAlive(IslandEvent value)
    {
        if (value.Provider is not ("Codex" or "Claude") || string.IsNullOrWhiteSpace(value.SessionId) ||
            value.SessionId.Length > 200 || !value.ProcessId.HasValue || !value.ProcessStartedAt.HasValue ||
            value.Kind == "SessionEnd" || value.At > DateTimeOffset.UtcNow.AddSeconds(5)) return false;
        return ProcessLifetime.CheckSession(value.ProcessId, value.ProcessStartedAt,
            value.ShellProcessId, value.ShellProcessStartedAt) == ConnectionStatus.Online;
    }

    public static void Remember(string directory, IslandEvent value)
    {
        if (value.Provider is not ("Codex" or "Claude") || string.IsNullOrWhiteSpace(value.SessionId) || value.SessionId.Length > 200) return;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Provider + ":" + value.SessionId)));
        var path = Path.Combine(directory, key + ".json");
        if (value.Kind == "SessionEnd") { if (File.Exists(path)) File.Delete(path); return; }
        if (!IsAlive(value)) return;
        Directory.CreateDirectory(directory);
        if (!File.Exists(path) && Directory.EnumerateFiles(directory, "*.json").Take(256).Count() >= 256)
        {
            Read(directory, _ => { }); // Prune exited processes before enforcing the bound.
            if (Directory.EnumerateFiles(directory, "*.json").Take(256).Count() >= 256) return;
        }
        var metadata = new IslandEvent(value.Provider, value.SessionId, "SessionStart", value.At,
            value.ProcessId, value.ProcessStartedAt, WorkingDirectory: ProjectNames.Normalize(value.WorkingDirectory),
            ShellProcessId: value.ShellProcessId, ShellProcessStartedAt: value.ShellProcessStartedAt);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(metadata)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Read(string directory, Action<IslandEvent> consume)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Take(256))
        {
            try
            {
                var value = new FileInfo(file).Length <= 32768 ? JsonSerializer.Deserialize<IslandEvent>(File.ReadAllText(file)) : null;
                if (value != null && IsAlive(value)) consume(value);
                else File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
}
