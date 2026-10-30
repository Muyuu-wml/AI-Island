using System.Text.Json;

namespace AIIsland.Core.Sessions;

/// <summary>
/// Island side of the permission channel: the hook polls
/// <c>replies\&lt;requestId&gt;.json</c> and turns it into Claude Code's decision output.
/// </summary>
public static class PermissionReplies
{
    public static void Write(string requestId, bool allow, string? directory = null) =>
        WriteBehavior(requestId, allow ? "allow" : "deny", directory);

    /// <summary>Releases a settled request or disabled monitor without deciding for the terminal.</summary>
    public static void Cancel(string requestId, string? directory = null) =>
        WriteBehavior(requestId, "cancel", directory);

    private static void WriteBehavior(string requestId, string behavior, string? directory)
    {
        if (requestId.Length == 0 || requestId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Invalid request id", nameof(requestId));
        directory ??= AppPaths.Replies;
        Directory.CreateDirectory(directory);
        Prune(directory);
        var file = Path.Combine(directory, requestId);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new { behavior }));
        File.Move(file + ".tmp", file + ".json", overwrite: true);
    }

    /// <summary>Replies the hook never picked up (it timed out or was killed).</summary>
    private static void Prune(string directory)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch { }
        }
    }
}
