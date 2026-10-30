using Microsoft.Win32;

namespace AIIsland.Services;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "AIIsland";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key is null) return;
            if (enabled)
                key.SetValue(Name, "\"" + Environment.ProcessPath + "\"");
            else
                key.DeleteValue(Name, false);
        }
        catch { }
    }
}
