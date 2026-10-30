using Microsoft.Win32;

namespace ExternalIp.Widget;

internal static class StartupManager
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "ExternalIpWidget";
    private static string Command
    {
        get
        {
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;
            var command = "\"" + executable + "\"";
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                command += " \"" + Path.Combine(AppContext.BaseDirectory, "ExternalIpWidget.dll") + "\"";
            return command;
        }
    }
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return string.Equals(key?.GetValue(Name) as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, true);
        if (enabled) key.SetValue(Name, Command, RegistryValueKind.String);
        else key.DeleteValue(Name, false);
    }
}
