using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIIsland.Core;
using AIIsland.Core.Sessions;

namespace AIIsland.Services;

/// <summary>
/// Registers the hook bridge with Codex, Claude Code and OpenCode. Every write is
/// merge-based: existing user hooks are preserved, our previous entry is replaced.
/// </summary>
public static class HookInstaller
{
    private static readonly string[] CommonEvents =
    {
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PermissionRequest",
        "Stop", "SessionEnd", "Interrupt", "SubagentStart", "SubagentStop",
    };

    private static string Bridge => Path.Combine(AppContext.BaseDirectory, "AIIsland.Hook.exe");

    public static bool IsBridgePresent => File.Exists(Bridge);

    public static string Install(bool codex, bool claude, bool opencode)
    {
        if (!IsBridgePresent) return "缺少 AIIsland.Hook.exe，请先运行 scripts/build.ps1";
        var results = new List<string>();
        if (codex) results.Add(InstallJson(Path.Combine(AppPaths.CodexDir, "hooks.json"), "Codex"));
        if (claude) results.Add(InstallJson(Path.Combine(AppPaths.ClaudeDir, "settings.json"), "Claude"));
        if (opencode) results.Add(InstallOpenCode());
        return string.Join("；", results);
    }

    private static string InstallJson(string path, string provider)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            JsonObject root;
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (text.Trim().Length > 0) File.Copy(path, path + ".ai-island.bak", true);
                root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            var events = CommonEvents.ToList();
            if (provider == "Codex") events.AddRange(new[] { "StopFailure", "PostToolUseFailure" });
            else events.AddRange(new[] { "PostToolUseFailure" });

            foreach (var name in events)
            {
                var list = hooks[name] as JsonArray;
                if (list is null)
                {
                    list = new JsonArray();
                    hooks[name] = list;
                }

                HookConfiguration.RemoveIslandHooks(list);

                var command = provider == "Codex"
                    ? $"cmd.exe /d /c \"{CodexWrapper()}\""
                    : $"\"{Bridge}\" {provider}";
                // Claude reads a PermissionRequest decision from stdout, so that hook runs
                // synchronously and may wait up to 60s for a click in the island.
                var decides = provider == "Claude" && name == "PermissionRequest";
                var hook = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = decides ? 70 : 3,
                };
                if (provider == "Codex") hook["commandWindows"] = command;
                if (!decides && name is not ("SessionEnd" or "Interrupt")) hook["async"] = true;
                list.Add(new JsonObject { ["hooks"] = new JsonArray(hook) });
            }

            File.WriteAllText(path + ".tmp", root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(path + ".tmp", path, true);
            return provider + " 已安装";
        }
        catch (Exception e)
        {
            AppLog.Write(e);
            return provider + " 安装失败：" + e.Message;
        }
    }

    private static string CodexWrapper()
    {
        var wrapper = Path.Combine(AppContext.BaseDirectory, "AIIsland.Hook-Codex.cmd");
        File.WriteAllText(wrapper, "@echo off\r\n\"" + Bridge + "\" Codex\r\n");
        return wrapper;
    }

    private static string InstallOpenCode()
    {
        try
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(AppPaths.Home, ".config");
            var plugin = Path.Combine(configHome, "opencode", "plugins", "ai-island");
            var source = Path.Combine(AppContext.BaseDirectory, "integrations", "opencode");
            if (!Directory.Exists(source)) return "OpenCode 插件文件缺失";

            Directory.CreateDirectory(plugin);
            foreach (var file in new[] { "package.json", "index.js", "monitor.js" })
                File.Copy(Path.Combine(source, file), Path.Combine(plugin, file), true);
            File.WriteAllText(Path.Combine(plugin, "bridge.json"),
                JsonSerializer.Serialize(new { bridge = Bridge }));
            return "OpenCode 已安装";
        }
        catch (Exception e)
        {
            AppLog.Write(e);
            return "OpenCode 安装失败：" + e.Message;
        }
    }
}
