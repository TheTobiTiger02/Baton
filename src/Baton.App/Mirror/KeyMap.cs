namespace Baton.App.Mirror;

/// <summary>
/// Windows virtual-key codes to Android <c>KeyEvent.KEYCODE_*</c>, for keys that are not text.
///
/// Printable characters deliberately do not go through here: they arrive as WM_CHAR after the PC's
/// keyboard layout has been applied and are sent as text, so a German, French or Czech layout types
/// what its keycaps say. Only editing, navigation and shortcut keys are sent as key events.
/// </summary>
public static class KeyMap
{
    // Android meta state flags.
    public const int MetaShiftOn = 0x1;
    public const int MetaAltOn = 0x2;
    public const int MetaCtrlOn = 0x1000;

    private static readonly Dictionary<int, int> Keys = new()
    {
        [0x08] = 67,  // Backspace -> KEYCODE_DEL
        [0x09] = 61,  // Tab
        [0x0D] = 66,  // Enter
        [0x2E] = 112, // Delete -> KEYCODE_FORWARD_DEL
        [0x24] = 122, // Home -> KEYCODE_MOVE_HOME
        [0x23] = 123, // End -> KEYCODE_MOVE_END
        [0x21] = 92,  // Page Up
        [0x22] = 93,  // Page Down
        [0x25] = 21,  // Left
        [0x26] = 19,  // Up
        [0x27] = 22,  // Right
        [0x28] = 20,  // Down
        [0x2D] = 124, // Insert
        [0xAD] = 164, // Volume mute
        [0xAE] = 25,  // Volume down
        [0xAF] = 24,  // Volume up
        [0xB0] = 87,  // Media next
        [0xB1] = 88,  // Media previous
        [0xB3] = 85,  // Media play/pause
    };

    /// <summary>Letters used with Ctrl for the editing shortcuts every Android text field knows.</summary>
    private static readonly Dictionary<int, int> ShortcutLetters = new()
    {
        [0x41] = 29, // A
        [0x43] = 31, // C
        [0x58] = 52, // X
        [0x5A] = 54, // Z
        [0x59] = 53, // Y
    };

    /// <summary>
    /// Returns the Android keycode for a key that must travel as a key event, or null when the key
    /// is text (handled via WM_CHAR) or has no Android equivalent.
    /// </summary>
    public static int? ToAndroid(int virtualKey, bool control)
    {
        if (Keys.TryGetValue(virtualKey, out var code))
        {
            return code;
        }

        return control && ShortcutLetters.TryGetValue(virtualKey, out var letter) ? letter : null;
    }

    public static int MetaState(bool control, bool shift, bool alt) =>
        (control ? MetaCtrlOn : 0) | (shift ? MetaShiftOn : 0) | (alt ? MetaAltOn : 0);

    public const int VirtualKeyEscape = 0x1B;
    public const int VirtualKeyV = 0x56;
}
