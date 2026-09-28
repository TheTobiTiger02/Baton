using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Baton.Host.Browser;

/// <summary>
/// Which program is on the other end of a loopback TCP connection. Firefox-based browsers all call
/// themselves "Firefox" to their extensions; the process (zen.exe, librewolf.exe) tells them apart.
/// </summary>
public static class LoopbackOwner
{
    public static string? ProcessName(IPEndPoint? remote, int localPort)
    {
        if (remote is null)
        {
            return null;
        }

        try
        {
            var size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2 /* AF_INET */, 4 /* TCP_TABLE_OWNER_PID_CONNECTIONS */, 0);
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, 2, 4, 0) != 0)
                {
                    return null;
                }

                var count = Marshal.ReadInt32(buffer);
                var rowSize = Marshal.SizeOf<TcpRow>();
                for (var index = 0; index < count; index++)
                {
                    var row = Marshal.PtrToStructure<TcpRow>(buffer + 4 + index * rowSize);
                    // The browser's side of the connection: its local port is our remote port.
                    if (Port(row.LocalPort) == remote.Port && Port(row.RemotePort) == localPort)
                    {
                        using var process = Process.GetProcessById((int)row.OwningPid);
                        return process.ProcessName;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        return null;
    }

    private static int Port(uint value) => (int)(((value & 0xFF) << 8) | ((value >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct TcpRow
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int addressFamily, int tableClass, uint reserved);
}
