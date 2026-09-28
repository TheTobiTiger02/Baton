using System.Runtime.InteropServices;
using Baton.Protocol;

namespace Baton.Host;

/// <summary>
/// Whether someone is at this PC: active, idle (no input for a while) or locked. Phones use it to
/// suggest continuing there when the PC is left, and the PC to suggest continuing here on return.
/// </summary>
public sealed class PresenceMonitor : IDisposable
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(90);

    private readonly Timer _timer;
    private PresenceState _state = PresenceState.Active;
    private DateTimeOffset _awaySince = DateTimeOffset.MinValue;

    public PresenceMonitor() => _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

    public PresenceState State => _state;

    /// <summary>The presence changed. Any thread.</summary>
    public event Action<PresenceState>? Changed;

    /// <summary>Someone came back after being away at least <see cref="IdleAfter"/>. Any thread.</summary>
    public event Action<TimeSpan>? Returned;

    private void Poll()
    {
        var next = IsLocked() ? PresenceState.Locked : IdleTime() >= IdleAfter ? PresenceState.Idle : PresenceState.Active;
        if (next == _state)
        {
            return;
        }

        var previous = _state;
        _state = next;
        if (previous == PresenceState.Active)
        {
            _awaySince = DateTimeOffset.UtcNow - (next == PresenceState.Idle ? IdleAfter : TimeSpan.Zero);
        }

        Changed?.Invoke(next);
        if (next == PresenceState.Active && _awaySince != DateTimeOffset.MinValue)
        {
            Returned?.Invoke(DateTimeOffset.UtcNow - _awaySince);
        }
    }

    private static TimeSpan IdleTime()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time))
            : TimeSpan.Zero;
    }

    /// <summary>The input desktop cannot be switched to while the lock screen owns it.</summary>
    private static bool IsLocked()
    {
        var desktop = OpenInputDesktop(0, false, 0x0100 /* DESKTOP_SWITCHDESKTOP */);
        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            return !SwitchDesktop(desktop);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    public void Dispose() => _timer.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("user32.dll")] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool SwitchDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
}
