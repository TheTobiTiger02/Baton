using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Baton.App;

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SettingsKeyPath = @"Software\Baton";
    private const string RunValueName = "Baton";
    private const string OptOutValueName = "StartupOptOut";
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder? packageFullName);

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(RunValueName) is string command
                    && string.Equals(command, ExpectedCommand(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Autostart is on but points at another copy: take it over when this is the installed copy
    /// (scripts\Install.ps1) or the other copy is gone. A build run from a dev folder leaves it alone.
    /// </summary>
    public static void Repair()
    {
        if (IsOptedOut || IsEnabled || IsPackaged())
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            if (key?.GetValue(RunValueName) is not string command)
            {
                return;
            }

            var registered = command.StartsWith('"') ? command[1..Math.Max(1, command.IndexOf('"', 1))] : command.Split(' ')[0];
            var installed = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Baton"));
            var isInstalledCopy = Environment.ProcessPath is { } current
                && current.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (isInstalledCopy || !File.Exists(registered))
            {
                SetEnabled(enabled: true, rememberUserChoice: false);
            }
        }
        catch
        {
            // Autostart stays as it was.
        }
    }

    public static bool EnableAfterPairingIfAllowed()
    {
        if (IsOptedOut || IsEnabled)
        {
            return IsEnabled;
        }

        return SetEnabled(enabled: true, rememberUserChoice: false);
    }

    public static bool SetEnabled(bool enabled, bool rememberUserChoice = true)
    {
        try
        {
            using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                var command = ExpectedCommand();
                if (string.IsNullOrWhiteSpace(command))
                {
                    return false;
                }

                runKey.SetValue(RunValueName, command, RegistryValueKind.String);
            }
            else
            {
                runKey.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            if (rememberUserChoice)
            {
                using var settingsKey = Registry.CurrentUser.CreateSubKey(SettingsKeyPath, writable: true);
                settingsKey.SetValue(OptOutValueName, enabled ? 0 : 1, RegistryValueKind.DWord);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOptedOut
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
                return key?.GetValue(OptOutValueName) is int value && value != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string? ExpectedCommand()
    {
        if (IsPackaged())
        {
            // Stable app-execution alias declared by the MSIX package; unlike WindowsApps paths it survives updates.
            return "Baton.exe --background";
        }

        var executable = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(executable) ? null : $"\"{executable}\" --background";
    }

    private static bool IsPackaged()
    {
        var length = 0;
        var result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }
}
