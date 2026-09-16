using AIIsland.Core;

namespace AIIsland.Services;

public enum AiNotificationSeverity { Information, Warning, Error }
public sealed record AiNotification(string Title, string Message, AiNotificationSeverity Severity);

public static class AiNotificationPolicy
{
    public static AiNotification? Create(Settings settings, IslandEvent value, string? fallbackProject = null)
    {
        var project = ProjectNames.Normalize(value.WorkingDirectory) is { } directory
            ? ProjectNames.Name(directory) : fallbackProject ?? "未知项目";
        var title = value.Provider + " · " + project;
        return value.Kind switch
        {
            "Stop" when settings.Notifications => new(title, "本轮响应已结束，请返回终端查看结果。", AiNotificationSeverity.Information),
            "StopFailure" when settings.Notifications => new(title, "本轮响应失败，请返回终端查看原因。", AiNotificationSeverity.Error),
            "PermissionRequest" when settings.ApprovalNotifications ?? settings.Notifications => new(title, "等待确认，请返回终端完成授权。", AiNotificationSeverity.Warning),
            _ => null
        };
    }
}
