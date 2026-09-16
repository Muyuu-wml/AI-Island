using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIIsland.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var result = 0;
        var exitRequested = false;
        var windowClosed = false;
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Startup += async (_, _) => {
            var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
            if (args.Contains("--balance-checks"))
            {
                try { await BalanceChecks.Run(root); }
                catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
                finally { application.Shutdown(); }
                return;
            }
            if (args.Contains("--monitor-order-checks"))
            {
                try { await MonitorOrderChecks.Run(root); }
                catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
                finally { application.Shutdown(); }
                return;
            }
            if (args.Contains("--resources-checks"))
            {
                try { await SystemResourcesChecks.Run(root, args.Contains("--probe")); }
                catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
                finally { application.Shutdown(); }
                return;
            }
            if (args.Contains("--music-checks"))
            {
                try { await MusicChecks.Run(root, args.Contains("--probe")); }
                catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
                finally { application.Shutdown(); }
                return;
            }
            if (args.Contains("--completion-checks"))
            {
                try { await StartupChecks.Run(root); await CompletionChecks.Run(root); }
                catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
                finally { application.Shutdown(); }
                return;
            }
            if (args.Contains("--ui-checks"))
            {
                try { NotificationChecks.Run(); }
                catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
                finally { application.Shutdown(); }
                return;
            }
            if (args.Contains("--probe") || args.Contains("--proxy-roundtrip") || args.Contains("--music-roundtrip"))
            {
                try { if (args.Contains("--proxy-roundtrip")) await LiveChecks.ProxyRoundtrip(); else if (args.Contains("--music-roundtrip")) await LiveChecks.MusicRoundtrip(); else await LiveChecks.Probe(root); }
                catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
                finally { application.Shutdown(); }
                return;
            }
            try { await ModuleChecks.Run(root); }
            catch (Exception e) { Console.Error.WriteLine(e); result = 1; application.Shutdown(); return; }
            var window = new IslandWindow(true);
            window.Closed += (_, _) => windowClosed = true;
            try
            {
                window.Show();
                ((Border)window.FindName("Header")).IsHitTestVisible = false;
                var output = Path.Combine(root, "artifacts", "screenshots"); Directory.CreateDirectory(output);
                string Text() => string.Join(" ", window.ViewModel.Ai.Groups.SelectMany(g => g.Rows.Select(r => g.Name + " " + r.Project + " " + r.Status)));
                void Check(bool condition, string message) { if (!condition) throw new Exception(message + ": " + Text()); Console.WriteLine("PASS " + message); }
                void Capture(string name)
                {
                    var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(window);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bmp));
                    using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
                }
                async Task Send(string provider, string kind, string session)
                {
                    var start = new ProcessStartInfo(Path.Combine(root, "artifacts/hook/AIIsland.Hook.exe"), provider) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true };
                    using var process = Process.Start(start)!;
                    await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { session_id = session, hook_event_name = kind, cwd = provider == "Codex" ? @"D:\go_space\src\AI-Island" : @"D:\go_space\src\gin-frame", prompt = "PRIVATE_SENTINEL_NOT_FOR_DISK" }));
                    process.StandardInput.Close(); await process.WaitForExitAsync();
                    Check(process.ExitCode == 0 && (await process.StandardOutput.ReadToEndAsync()).Length == 0, "Hook is silent and exits 0");
                    await Task.Delay(1400);
                }
                await Task.Delay(1100);
                Check(window.ActualWidth == 420, "Idle capsule stays compact"); Capture("01-idle");
                var aiCard = (Border)window.FindName("AiCard");
                var toolsPanel = (StackPanel)window.FindName("ToolPanel");
                var idleHeight = window.ActualHeight;
                var appearanceProbe = new Border();
                IslandAppearance.Apply(appearanceProbe, true, true, "approval", true);
                IslandAppearance.Apply(appearanceProbe, false, true, "approval", true);
                var originalShadow = (System.Windows.Media.Effects.DropShadowEffect)appearanceProbe.Effect;
                Check(((SolidColorBrush)appearanceProbe.Background).Color.ToString() == "#F018191E"
                    && ((SolidColorBrush)appearanceProbe.BorderBrush).Color.ToString() == "#FF34363F"
                    && originalShadow.Color == Colors.Black && originalShadow.Opacity == .35 && originalShadow.ShadowDepth == 3
                    && !originalShadow.HasAnimatedProperties, "Disabling outline restores original palette and removes glow animations");
                var savedAppearance = JsonSerializer.Deserialize<AIIsland.Services.Settings>(JsonSerializer.Serialize(new AIIsland.Services.Settings { EnhancedOutline = false }))!;
                Check(!savedAppearance.EnhancedOutline && JsonSerializer.Deserialize<AIIsland.Services.Settings>("{}")!.EnhancedOutline, "Appearance rollback persists and legacy settings default to enhanced");
                Check(window.ViewModel.Ai.IntegrationStatus.Contains("尚未收到事件"), "Integration does not claim connection before events");
                Check(AIIsland.Services.TerminalNavigator.Open(null, null).Contains("缺少"), "Missing terminal association has explicit fallback");
                Check(AIIsland.Services.TerminalNavigator.Open(Environment.ProcessId, DateTimeOffset.MinValue).Contains("退出"), "Reused PID identity cannot activate an unrelated window");
                Check(!window.ViewModel.AiExpanded && !window.ViewModel.DrawerOpen && window.ViewModel.Ai.Summary == "等待 AI Agent 接入", "No sessions shows only waiting summary");
                await Send("Codex", "SessionStart", "smoke-codex");
                Check(window.ActualWidth == 420, "Session start does not expand");
                Check(window.ViewModel.Ai.IntegrationStatus.Contains("Codex · 已收到事件") && window.ViewModel.Ai.IntegrationStatus.Contains("Claude · 尚未收到事件"), "Provider integration reports actual independent receipts");
                Check(!aiCard.IsVisible && window.ViewModel.Ai.MonitoredCount == 1 && window.ViewModel.Ai.Groups.First().Rows.First().Elapsed == "", "Idle session counts without showing rows or timer");
                await Send("Codex", "UserPromptSubmit", "smoke-codex");
                Check(Text().Contains("思考中") && window.ActualWidth == 420, "Actual inbox expands thinking capsule"); Capture("02-thinking");
                Check(aiCard.IsVisible && !toolsPanel.IsVisible && window.ActualHeight > idleHeight, "Task automatically opens AI only");
                await Send("Codex", "SessionStart", "smoke-idle");
                Check(window.ViewModel.Ai.MonitoredCount == 2 && window.ViewModel.Ai.Groups.First().Rows.Count == 2, "Active AI display also includes idle same-provider sessions");
                await Send("Claude", "UserPromptSubmit", "smoke-claude");
                window.ToggleDrawer(); await Task.Delay(1000);
                window.ToggleDrawer(); await Task.Delay(1000);
                Check(aiCard.IsVisible && !toolsPanel.IsVisible && window.ViewModel.Ai.ActiveCount == 2, "Closing tools leaves both running agents visible");
                window.ToggleDrawer(); await Task.Delay(1000);
                Check(Text().Contains("Codex AI-Island") && Text().Contains("Claude gin-frame") && window.ActualWidth == 420, "Drawer groups both providers with project names"); Capture("03-multiple");
                var retainedRow = window.ViewModel.Ai.Groups.First().Rows.First(); retainedRow.IsExpanded = true;
                var approvalNotices = 0;
                window.ViewModel.Ai.ApprovalRequested += _ => approvalNotices++;
                await Send("Claude", "PermissionRequest", "smoke-claude");
                await Send("Claude", "PermissionRequest", "smoke-claude");
                Check(window.ViewModel.Ai.NeedsAttention && window.ViewModel.Ai.AccentColor == "#F3BD65" && approvalNotices == 1, "Approval stays amber without duplicate reminders");
                Check(Text().Contains("等待确认"), "Permission state visible"); Capture("04-approval");
                Check(ReferenceEquals(retainedRow, window.ViewModel.Ai.Groups.First().Rows.First()) && retainedRow.IsExpanded, "State updates preserve row instance and expanded details");
                await Send("Codex", "Stop", "smoke-codex");
                Check(Text().Contains("Completed") && Text().Contains("Claude"), "One completion leaves other session visible"); Capture("05-completed");
                await Send("Claude", "StopFailure", "smoke-claude");
                Check(Text().Contains("Failed"), "Failure visible"); Capture("06-failed");
                Check(!window.ViewModel.Ai.NeedsAttention, "Failure clears approval attention");
                await Task.Delay(3500);
                Check(window.ActualWidth == 420 && Text().Contains("等待输入"), "Manual drawer remains open with idle project sessions");
                Check(!aiCard.IsVisible && toolsPanel.IsVisible && window.ViewModel.DrawerOpen && window.ViewModel.Ai.MonitoredCount == 3, "All completions return AI to summary without closing tools");
                Check(window.ViewModel.Ai.Groups.SelectMany(g => g.Rows).All(r => r.Elapsed == ""), "Idle rows do not retain completed task timers");
                window.ToggleDrawer(); await Task.Delay(1000);
                Check(window.ActualWidth == 420, "Closing drawer returns to compact capsule (width=" + window.ActualWidth + ")");
                window.ToggleDrawer(); await Task.Delay(40);
                window.ToggleDrawer(); await Task.Delay(40);
                window.ToggleDrawer(); await Task.Delay(1000);
                var drawer = toolsPanel;
                Check(window.ViewModel.DrawerOpen && window.ActualWidth == 420 && drawer.Opacity >= .99 && drawer.IsHitTestVisible, "Rapid reversal leaves drawer fully visible and interactive (open=" + window.ViewModel.DrawerOpen + ", opacity=" + drawer.Opacity + ", hit=" + drawer.IsHitTestVisible + ")");
                window.ToggleDrawer(); await Task.Delay(1000);
                Check(!window.ViewModel.DrawerOpen && window.ActualWidth == 420, "Drawer closes normally after rapid reversal");
                await Send("Claude", "UserPromptSubmit", "smoke-claude");
                Check(aiCard.IsVisible && !toolsPanel.IsVisible, "Next task automatically reopens only AI");
                await Send("Codex", "SessionEnd", "smoke-idle");
                Check(window.ViewModel.Ai.MonitoredCount == 2 && window.ViewModel.Ai.Groups.First().Rows.Count == 1, "Exited idle session leaves count and rows");
                window.ViewModel.Ai.TogglePause(); await Task.Delay(1000);
                Check(!aiCard.IsVisible && window.ViewModel.Ai.Summary == "AI 监控已暂停", "Pause collapses AI and reports paused summary");
                var capsule = (Border)window.FindName("Capsule");
                exitRequested = true;
                ((MenuItem)capsule.ContextMenu.Items[3]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            catch (Exception e) { Console.Error.WriteLine(e); result = 1; }
            finally { if (!exitRequested || result != 0) { window.Close(); application.Shutdown(); } }
        };
        application.Run();
        if (args.Contains("--balance-checks")) return result;
        if (args.Contains("--monitor-order-checks") || args.Contains("--resources-checks") || args.Contains("--music-checks") || args.Contains("--completion-checks") || args.Contains("--ui-checks") || args.Contains("--probe") || args.Contains("--proxy-roundtrip") || args.Contains("--music-roundtrip")) return result;
        if (result == 0 && exitRequested && windowClosed) Console.WriteLine("PASS Exit menu closes window and shuts down application\nWPF + actual Hook inbox smoke test passed");
        else result = 1;
        return result;
    }
}
