using Microsoft.Win32;

namespace Baton.App;

/// <summary>This PC's own switches, kept in HKCU\Software\Baton next to the autostart choice.</summary>
internal static class UserSettings
{
    private const string KeyPath = @"Software\Baton";

    /// <summary>Offer to continue a phone's activity when the user comes back to the PC.</summary>
    public static bool SuggestOnReturn
    {
        get => Read(nameof(SuggestOnReturn), true);
        set => Write(nameof(SuggestOnReturn), value);
    }

    private static bool Read(string name, bool fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int value ? value != 0 : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void Write(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // The switch keeps its previous value.
        }
    }
}
