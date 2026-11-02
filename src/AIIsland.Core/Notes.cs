using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIIsland.Core;

public sealed record NoteItem(string Id, string Text, bool Done, DateTimeOffset At);

/// <summary>
/// Local notepad: one JSON file in the data directory, no account and no network.
/// Every change is written immediately so nothing is lost if the island closes.
/// </summary>
public sealed class NoteStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string path;
    private readonly List<NoteItem> items = new();

    public NoteStore(string? directory = null)
    {
        path = Path.Combine(directory ?? AppPaths.Data, "notes.json");
        Load();
    }

    public IReadOnlyList<NoteItem> Items => items;

    public NoteItem Add(string text)
    {
        var item = new NoteItem(Guid.NewGuid().ToString("N"), text.Trim(), false, DateTimeOffset.Now);
        items.Add(item);
        Save();
        return item;
    }

    public void Toggle(string id)
    {
        var index = items.FindIndex(item => item.Id == id);
        if (index < 0) return;
        items[index] = items[index] with { Done = !items[index].Done };
        Save();
    }

    public void Remove(string id)
    {
        if (items.RemoveAll(item => item.Id == id) > 0) Save();
    }

    public void ClearDone()
    {
        if (items.RemoveAll(item => item.Done) > 0) Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var saved = JsonSerializer.Deserialize<List<NoteItem>>(
                File.ReadAllText(path).TrimStart('\uFEFF'), Options);
            if (saved is null) return;
            items.AddRange(saved.Where(item => !string.IsNullOrWhiteSpace(item.Text)));
        }
        catch { /* A broken file must not break the island. */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(items, Options));
            File.Move(temporary, path, true);
        }
        catch { }
    }
}
