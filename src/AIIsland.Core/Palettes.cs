namespace AIIsland.Core;

/// <summary>A preset color scheme for the island indicators: music bars, CPU/memory rings and network rates.</summary>
public sealed record PaletteTheme(
    string Id,
    string Name,
    string Accent,
    string Cpu,
    string Memory,
    string Down,
    string Up,
    string Music);

public static class Palettes
{
    public const string Default = "mist";

    public static IReadOnlyList<PaletteTheme> All { get; } = new PaletteTheme[]
    {
        new("mist", "雾蓝", "#5B8CFF", "#8FA8C8", "#7FC8A9", "#8FA8C8", "#6FC7C2", "#E88AAF"),
        new("sakura", "樱花", "#E78FB3", "#C7A3CC", "#DCA7B4", "#BFA6CE", "#E4A9C6", "#F07EA9"),
        new("mint", "薄荷", "#4FC3A1", "#7FBEB6", "#8FD3A8", "#7FB4C6", "#6FD0B6", "#E79CB8"),
        new("sunset", "落日", "#E0873F", "#D6A063", "#D98A8A", "#C9A27C", "#DD8F66", "#F08A6E"),
        new("grape", "葡萄", "#A78BFA", "#A79BD8", "#BE9BD8", "#9F93D9", "#A88FD9", "#D68AC8"),
        new("graphite", "石墨", "#A8B2C8", "#97A3B8", "#9DB0A6", "#8FA0B4", "#9BB0AE", "#C4A3B4"),
    };

    public static PaletteTheme Find(string? id) =>
        All.FirstOrDefault(palette => string.Equals(palette.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
