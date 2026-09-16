using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AIIsland.Core;

namespace AIIsland.Services;

public sealed class CodexCompletionReader
{
    private const int HeaderLimit = 1024 * 1024;
    private const int TailLimit = 1024 * 1024;
    private const int CacheLimit = 256;
    private sealed record Completion(string TurnId, string Kind, DateTimeOffset At);
    private sealed record Snapshot(long Length, long LastWriteTicks, string? SessionId, Completion[] Completions);
    private readonly Dictionary<string, Snapshot> cache = new(StringComparer.OrdinalIgnoreCase);

    public IslandEvent? Read(AIProcess session)
    {
        if (session.Provider != "Codex" || !session.Active || string.IsNullOrWhiteSpace(session.TurnId)) return null;
        var path = ProjectNames.Normalize(session.TranscriptPath);
        if (path == null) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { cache.Remove(path); return null; }
            var length = info.Length;
            var writeTicks = info.LastWriteTimeUtc.Ticks;
            if (!cache.TryGetValue(path, out var snapshot) || snapshot.Length != length || snapshot.LastWriteTicks != writeTicks)
            {
                snapshot = ReadSnapshot(path, length, writeTicks);
                if (cache.Count >= CacheLimit && !cache.ContainsKey(path)) cache.Clear();
                cache[path] = snapshot;
            }
            if (snapshot.SessionId != session.SessionId) return null;
            for (var i = snapshot.Completions.Length - 1; i >= 0; i--)
            {
                var completion = snapshot.Completions[i];
                if (completion.TurnId == session.TurnId && completion.At >= session.StartedAt && completion.At <= DateTimeOffset.UtcNow.AddSeconds(5))
                    return new IslandEvent("Codex", session.SessionId, completion.Kind, completion.At,
                        TurnId: completion.TurnId, WorkingDirectory: session.WorkingDirectory, TranscriptPath: path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        { cache.Remove(path); }
        return null;
    }

    private static Snapshot ReadSnapshot(string path, long length, long writeTicks)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var fileLength = stream.Length;
        var sessionId = ReadSessionId(stream, fileLength);
        if (sessionId == null) return new Snapshot(length, writeTicks, null, Array.Empty<Completion>());

        var offset = Math.Max(0, fileLength - TailLimit);
        var aligned = offset == 0;
        if (offset > 0)
        {
            stream.Position = offset - 1;
            aligned = stream.ReadByte() == '\n';
        }
        stream.Position = offset;
        var tail = new byte[(int)(fileLength - offset)];
        var tailLength = ReadBytes(stream, tail);
        var completions = new List<Completion>();
        var start = aligned ? 0 : Array.IndexOf(tail, (byte)'\n', 0, tailLength) + 1;
        if (!aligned && start == 0) return new Snapshot(length, writeTicks, sessionId, Array.Empty<Completion>());
        while (start < tailLength)
        {
            var end = Array.IndexOf(tail, (byte)'\n', start, tailLength - start);
            if (end < 0) break; // The writer may still be appending this record.
            var completion = ReadCompletion(tail.AsMemory(start, end - start));
            if (completion != null)
            {
                if (completions.Count == CacheLimit) completions.RemoveAt(0);
                completions.Add(completion);
            }
            start = end + 1;
        }
        return new Snapshot(length, writeTicks, sessionId, completions.ToArray());
    }

    private static int ReadBytes(Stream stream, byte[] buffer)
    {
        var length = 0;
        while (length < buffer.Length)
        {
            var count = stream.Read(buffer, length, buffer.Length - length);
            if (count == 0) break;
            length += count;
        }
        return length;
    }

    private static string? ReadSessionId(Stream stream, long fileLength)
    {
        var header = new byte[(int)Math.Min(fileLength, HeaderLimit)];
        var length = 0;
        while (length < header.Length)
        {
            var count = stream.Read(header, length, Math.Min(4096, header.Length - length));
            if (count == 0) break;
            var end = Array.IndexOf(header, (byte)'\n', length, count);
            if (end >= 0) return SessionId(header.AsMemory(0, end));
            length += count;
        }
        return null;
    }

    private static string? SessionId(ReadOnlyMemory<byte> line)
    {
        try
        {
            if (line.Length >= 3 && line.Span[0] == 0xef && line.Span[1] == 0xbb && line.Span[2] == 0xbf) line = line[3..];
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (Field(root, "type") != "session_meta" || !Object(root, "payload", out var payload)) return null;
            if (payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object && source.TryGetProperty("subagent", out _)) return null;
            return Field(payload, "id");
        }
        catch (JsonException) { return null; }
    }

    private static Completion? ReadCompletion(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (Field(root, "type") != "event_msg" || !Object(root, "payload", out var payload)) return null;
            var kind = Field(payload, "type") switch { "task_complete" => "Stop", "turn_aborted" => "Interrupt", _ => null };
            var turn = Field(payload, "turn_id");
            if (kind == null || turn == null || !DateTimeOffset.TryParse(Field(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) return null;
            return new Completion(turn, kind, at);
        }
        catch (JsonException) { return null; }
    }

    private static bool Object(JsonElement value, string name, out JsonElement child)
    {
        child = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out child) && child.ValueKind == JsonValueKind.Object;
    }

    private static string? Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: > 0 and <= 200 } text ? text : null;
}
