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

    /// <summary>The phone the send shortcut went to last; it goes there again.</summary>
    public static string? LastTargetDeviceId
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(nameof(LastTargetDeviceId)) as string;
            }
            catch
            {
                return null;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
                if (value is null)
                {
                    key.DeleteValue(nameof(LastTargetDeviceId), throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(nameof(LastTargetDeviceId), value, RegistryValueKind.String);
                }
            }
            catch
            {
                // The hotkey falls back to the first phone online.
            }
        }
    }

    /// <summary>The first-run welcome was dismissed.</summary>
    public static bool Welcomed
    {
        get => Read(nameof(Welcomed), false);
        set => Write(nameof(Welcomed), value);
    }

    /// <summary>Windows streamed to a phone bring their sound along.</summary>
    public static bool StreamAudio
    {
        get => Read(nameof(StreamAudio), true);
        set => Write(nameof(StreamAudio), value);
    }

    /// <summary>How much bandwidth streams to a phone may use.</summary>
    public static Baton.Host.Streaming.StreamQuality StreamQuality
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(nameof(StreamQuality)) is int value && Enum.IsDefined(typeof(Baton.Host.Streaming.StreamQuality), value)
                    ? (Baton.Host.Streaming.StreamQuality)value
                    : Baton.Host.Streaming.StreamQuality.Auto;
            }
            catch
            {
                return Baton.Host.Streaming.StreamQuality.Auto;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
                key.SetValue(nameof(StreamQuality), (int)value, RegistryValueKind.DWord);
            }
            catch
            {
                // Streams keep the previous quality.
            }
        }
    }

    /// <summary>The shortcut for an action: the user's own, else the default.</summary>
    public static Hotkey GetHotkey(HotkeyAction action)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue($"Hotkey.{action}") is int packed ? Hotkey.FromPacked(packed) : HotkeyDefaults.For(action);
        }
        catch
        {
            return HotkeyDefaults.For(action);
        }
    }

    /// <summary>Sets an action's shortcut; null goes back to the default.</summary>
    public static void SetHotkey(HotkeyAction action, Hotkey? hotkey)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (hotkey is { } value && value != HotkeyDefaults.For(action))
            {
                key.SetValue($"Hotkey.{action}", value.Packed, RegistryValueKind.DWord);
            }
            else
            {
                key.DeleteValue($"Hotkey.{action}", throwOnMissingValue: false);
            }
        }
        catch
        {
            // The shortcut keeps its previous value.
        }
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
