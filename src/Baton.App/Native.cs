using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Interop;

namespace Baton.App;

/// <summary>Lets a second launch ask the running copy to show itself.</summary>
internal static class SingleInstance
{
    private static string EventName => $@"Local\Baton.App.Show.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    public static void SignalShow()
    {
        if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }

    public static void Listen(Action onShow)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var thread = new Thread(() =>
        {
            while (handle.WaitOne())
            {
                onShow();
            }
        })
        { IsBackground = true, Name = "Baton single instance" };
        thread.Start();
    }
}

/// <summary>System-wide keyboard shortcuts through RegisterHotKey on a message-only window.</summary>
internal sealed class HotkeyManager : IDisposable
{
    public const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private readonly HwndSource _window;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public HotkeyManager()
    {
        _window = new HwndSource(new HwndSourceParameters("Baton hotkeys") { ParentWindow = new IntPtr(-3) });
        _window.AddHook(WndProc);
    }

    /// <summary>Returns false when another app already owns the combination.</summary>
    public bool Register(uint modifiers, uint virtualKey, Action action)
    {
        var id = _nextId++;
        if (!RegisterHotKey(_window.Handle, id, modifiers | ModNoRepeat, virtualKey))
        {
            return false;
        }

        _actions[id] = action;
        return true;
    }

    /// <summary>Releases every shortcut, before registering a changed set.</summary>
    public void Clear()
    {
        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(_window.Handle, id);
        }

        _actions.Clear();
    }

    public void Dispose()
    {
        Clear();
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
