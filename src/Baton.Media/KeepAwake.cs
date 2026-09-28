using System.Runtime.InteropServices;

namespace Baton.Media;

/// <summary>
/// Keeps the display on and the PC out of sleep while held, so a PC being controlled from the
/// phone does not lock or sleep mid-stream. A power request, not a thread state, so it can be
/// taken and released from any thread.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    private const int PowerRequestDisplayRequired = 0, PowerRequestSystemRequired = 1;
    private readonly IntPtr _request;

    public KeepAwake(string reason)
    {
        var text = Marshal.StringToHGlobalUni(reason);
        var context = new ReasonContext { Version = 0, Flags = 0x1 /* POWER_REQUEST_CONTEXT_SIMPLE_STRING */, SimpleReasonString = text };
        _request = PowerCreateRequest(ref context);
        Marshal.FreeHGlobal(text);
        if (_request != IntPtr.Zero && _request != new IntPtr(-1))
        {
            PowerSetRequest(_request, PowerRequestDisplayRequired);
            PowerSetRequest(_request, PowerRequestSystemRequired);
        }
    }

    public void Dispose()
    {
        if (_request != IntPtr.Zero && _request != new IntPtr(-1))
        {
            PowerClearRequest(_request, PowerRequestDisplayRequired);
            PowerClearRequest(_request, PowerRequestSystemRequired);
            CloseHandle(_request);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerSetRequest(IntPtr request, int type);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool PowerClearRequest(IntPtr request, int type);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
