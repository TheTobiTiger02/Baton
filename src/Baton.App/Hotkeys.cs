using System.Windows.Input;

namespace Baton.App;

/// <summary>A key combination as RegisterHotKey takes it: MOD_* flags and a virtual-key code.</summary>
internal readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    /// <summary>Packs into one number for the registry: modifiers in the high word, the key in the low one.</summary>
    public int Packed => (int)((Modifiers << 16) | VirtualKey);

    public static Hotkey FromPacked(int packed) => new((uint)packed >> 16, (uint)packed & 0xFFFF);

    /// <summary>
    /// The combination a key press in a capture box means, or null while only modifiers are down
    /// or without any modifier (a bare key would steal it from every app).
    /// </summary>
    public static Hotkey? FromKeyEvent(Key key, ModifierKeys modifiers)
    {
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.System or Key.None || modifiers == ModifierKeys.None)
        {
            return null;
        }

        uint flags = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) flags |= HotkeyManager.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Control)) flags |= HotkeyManager.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) flags |= HotkeyManager.ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) flags |= HotkeyManager.ModWin;
        return new Hotkey(flags, (uint)KeyInterop.VirtualKeyFromKey(key));
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & HotkeyManager.ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyManager.ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyManager.ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyManager.ModWin) != 0) parts.Add("Win");
        var key = KeyInterop.KeyFromVirtualKey((int)VirtualKey);
        parts.Add(key switch
        {
            Key.Left => "←",
            Key.Right => "→",
            Key.Up => "↑",
            Key.Down => "↓",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            _ => key.ToString()
        });
        return string.Join("+", parts);
    }
}

/// <summary>The three shortcuts Baton offers, each with its default.</summary>
internal enum HotkeyAction { SendToPhone, ContinueHere, Choose }

internal static class HotkeyDefaults
{
    private const uint VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27;

    public static Hotkey For(HotkeyAction action) => action switch
    {
        HotkeyAction.SendToPhone => new(HotkeyManager.ModControl | HotkeyManager.ModAlt, VkRight),
        HotkeyAction.ContinueHere => new(HotkeyManager.ModControl | HotkeyManager.ModAlt, VkLeft),
        _ => new(HotkeyManager.ModControl | HotkeyManager.ModAlt, VkUp)
    };
}
