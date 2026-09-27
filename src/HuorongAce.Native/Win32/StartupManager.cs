using Microsoft.Win32;

namespace HuorongAce.Native.Win32;

/// <summary>
/// Manages the current user's Windows startup entry for the unpackaged app.
/// </summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "HuorongAce";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static bool TrySetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executable))
                {
                    return false;
                }

                key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
