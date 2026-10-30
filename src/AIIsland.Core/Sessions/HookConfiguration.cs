using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIIsland.Core.Sessions;

public static class HookConfiguration
{
    public static void RemoveIslandHooks(JsonArray groups)
    {
        for (var group = groups.Count - 1; group >= 0; group--)
        {
            if (groups[group]?["hooks"] is not JsonArray hooks) continue;
            var removed = false;
            for (var index = hooks.Count - 1; index >= 0; index--)
            {
                if (hooks[index] is not JsonObject hook || hook["type"]?.GetValue<string>() != "command") continue;
                var command = hook["commandWindows"]?.GetValue<string>() ?? hook["command"]?.GetValue<string>() ?? "";
                if (!Regex.IsMatch(command, """^\s*(?:cmd(?:\.exe)?\s+/d\s+/c\s+)?(?:"(?:[^"\r\n]*[\\/])?AIIsland\.Hook(?:\.exe|-Codex\.cmd)"|(?:[^\s"\r\n]*[\\/])?AIIsland\.Hook(?:\.exe|-Codex\.cmd))(?=$|\s)""", RegexOptions.IgnoreCase)) continue;
                hooks.RemoveAt(index);
                removed = true;
            }
            if (removed && hooks.Count == 0) groups.RemoveAt(group);
        }
    }
}
