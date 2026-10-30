namespace Baton.Media;

/// <summary>
/// A key combination written as people write it, "Ctrl+Shift+T" or "Win+D": modifiers, then one
/// key (a letter, digit, F1-F24, or a named key), as Windows virtual-key codes.
/// </summary>
public static class KeyCombo
{
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["strg"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12,
        ["win"] = 0x5B, ["windows"] = 0x5B, ["meta"] = 0x5B
    };

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["printscreen"] = 0x2C, ["print"] = 0x2C
    };

    /// <summary>The key and its modifiers, or null when it isn't a combination this knows.</summary>
    public static (ushort Key, IReadOnlyList<ushort> Modifiers)? Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var modifiers = new List<ushort>();
        foreach (var part in parts[..^1])
        {
            if (!Modifiers.TryGetValue(part, out var modifier))
            {
                return null;
            }

            modifiers.Add(modifier);
        }

        return KeyCode(parts[^1]) is { } key ? (key, modifiers) : null;
    }

    private static ushort? KeyCode(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return char.ToUpperInvariant(key[0]);
        }

        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') && int.TryParse(key[1..], out var number) && number is >= 1 and <= 24)
        {
            return (ushort)(0x70 + number - 1);
        }

        return Named.TryGetValue(key, out var named) ? named : null;
    }
}
