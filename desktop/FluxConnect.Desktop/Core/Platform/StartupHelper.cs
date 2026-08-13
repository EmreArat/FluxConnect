using Microsoft.Win32;

namespace FluxConnect.Desktop.Core.Platform;

public static class StartupHelper
{
    public const string MinimizedArg = "--minimized";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "FluxConnect";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(AppName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static void ConfigureStartup(bool enabled, bool startMinimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return;

                var command = startMinimized
                    ? $"\"{exePath}\" {MinimizedArg}"
                    : $"\"{exePath}\"";
                key.SetValue(AppName, command);
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Registry erişimi reddedildiyse sessizce geç
        }
    }
}
