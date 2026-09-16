using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using AIIsland.Core;
using AIIsland.Modules;
using AIIsland.Services;
using AIIsland.UI;

public static class NotificationChecks
{
    public static void Run()
    {
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS " + message);
        }

        var settings = JsonSerializer.Deserialize<Settings>("{\"Notifications\":false}")!;
        settings.QQMusic = settings.Clash = false;
        var notices = new List<AiNotification>();
        var window = new IslandWindow(inspection: true, initialSettings: settings, notificationSink: notices.Add);
        try
        {
            var bus = (EventBus)typeof(AiModule).GetField("bus", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window.ViewModel.Ai)!;
            var time = DateTimeOffset.UtcNow.AddSeconds(1);
            void Send(string kind)
            {
                bus.Publish(new IslandEvent("Codex", "notification-test", kind, time, WorkingDirectory: kind == "UserPromptSubmit" ? @"D:\projects\中文项目" : null));
                time = time.AddMilliseconds(1);
            }

            Send("UserPromptSubmit"); Send("PermissionRequest"); Send("Stop");
            Check(notices.Count == 0 && settings.ApprovalNotifications == null, "Legacy disabled notifications keep both completion and approval reminders disabled");

            settings.Notifications = true; settings.ApprovalNotifications = false;
            Send("UserPromptSubmit"); Send("PermissionRequest"); Send("Stop"); Send("Stop");
            Check(notices.Count == 1 && notices[0].Title == "Codex · 中文项目"
                && notices[0].Message.Contains("本轮响应已结束") && notices[0].Severity == AiNotificationSeverity.Information,
                "Window sends one completion notice with provider and project while approval reminders are disabled");
            Send("UserPromptSubmit"); Send("StopFailure");
            Check(notices.Count == 2 && notices[1].Message.Contains("本轮响应失败") && notices[1].Severity == AiNotificationSeverity.Error,
                "Window sends a localized failure notice through the completion setting");

            settings.Notifications = false; settings.ApprovalNotifications = true;
            Send("UserPromptSubmit"); Send("PermissionRequest"); Send("PermissionRequest"); Send("Stop");
            Check(notices.Count == 3 && notices[2].Message.Contains("等待确认") && notices[2].Severity == AiNotificationSeverity.Warning,
                "Approval reminders work independently without duplicate or disabled completion notices");

            var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!;
            Check(!restored.Notifications && restored.ApprovalNotifications == true,
                "Independent notification settings survive a JSON round trip");
            var legacyEnabled = JsonSerializer.Deserialize<Settings>("{\"Notifications\":true}")!;
            Check(AiNotificationPolicy.Create(legacyEnabled, new IslandEvent("Claude", "legacy", "PermissionRequest", time)) != null
                && AiNotificationPolicy.Create(legacyEnabled, new IslandEvent("Claude", "legacy", "Interrupt", time)) == null,
                "Legacy enabled approval reminders remain enabled and interruptions do not claim completion");
        }
        finally { window.Close(); }
    }
}
