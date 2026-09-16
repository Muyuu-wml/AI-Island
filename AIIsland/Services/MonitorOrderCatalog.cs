using System.Collections.Generic;
using System.Linq;

namespace AIIsland.Services;

public static class MonitorOrderCatalog
{
    private static readonly string[] defaults = { "resources", "qqmusic", "neteasemusic", "clash", "balance" };
    public static string[] DefaultIds => (string[])defaults.Clone();
    public static string[] Normalize(IEnumerable<string>? order) => (order ?? System.Array.Empty<string>())
        .Where(id => defaults.Contains(id)).Distinct().Concat(defaults).Distinct().ToArray();

    public static string Name(string id) => id switch
    {
        "resources" => "系统资源",
        "balance" => "账户余额",
        "qqmusic" => "QQ 音乐",
        "neteasemusic" => "网易云音乐",
        "clash" => "Clash 系统代理",
        _ => id
    };
}
