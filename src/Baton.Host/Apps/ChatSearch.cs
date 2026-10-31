using Baton.Media;

namespace Baton.Host.Apps;

/// <summary>
/// Finds a chat by name in WhatsApp on this PC. WhatsApp has no link to a group (or to a contact
/// without a number), so once it is open its chat search gets the name and the user picks the
/// chat. It only types, never presses Enter, and only while WhatsApp is the window in front: should
/// the search not have the focus, the name lands unsent in a message box, where it reaches no one.
/// </summary>
internal static class ChatSearch
{
    private const ushort Control = 0x11;
    private const ushort SearchKey = 'F';
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(10);

    public static async Task WhatsAppAsync(string name)
    {
        var deadline = DateTime.UtcNow + WindowWait;
        WindowInfo? window = null;
        while (DateTime.UtcNow < deadline)
        {
            window = DesktopWindows.FindByTitle("WhatsApp", "WhatsApp");
            if (window is not null)
            {
                DesktopWindows.BringToFront(window.Handle);
                if (InFront(window))
                {
                    break;
                }
            }

            await Task.Delay(250);
        }

        // The page inside the window takes keys a moment after the window comes up.
        await Task.Delay(800);
        if (window is null || !InFront(window))
        {
            return;
        }

        InputInjector.Chord(SearchKey, [Control]);
        await Task.Delay(300);
        if (InFront(window))
        {
            InputInjector.Text(name);
        }
    }

    private static bool InFront(WindowInfo window) => DesktopWindows.Foreground()?.Handle == window.Handle;
}
