using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Baton.Protocol;

namespace Baton.Host;

public static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string PlainPrefix = "plain:";
    private const int CryptProtectUiForbidden = 0x1;

    public static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var clearBytes = Encoding.UTF8.GetBytes(value);
        if (!OperatingSystem.IsWindows())
        {
            return PlainPrefix + Convert.ToBase64String(clearBytes);
        }

        return DpapiPrefix + Convert.ToBase64String(ProtectWindows(clearBytes));
    }

    public static string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            var protectedBytes = Convert.FromBase64String(value[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(UnprotectWindows(protectedBytes));
        }

        if (value.StartsWith(PlainPrefix, StringComparison.Ordinal))
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value[PlainPrefix.Length..]));
        }

        // v1 trust stores contained the clear trust key directly. Reading it here
        // provides an in-place migration; the next save writes the DPAPI form.
        return value;
    }

    private static byte[] ProtectWindows(byte[] bytes)
    {
        using var input = DataBlob.FromBytes(bytes);
        if (!CryptProtectData(
                ref input.Value,
                "Baton trusted device",
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return CopyAndFree(output);
    }

    private static byte[] UnprotectWindows(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows DPAPI data can only be opened by Windows.");
        }

        using var input = DataBlob.FromBytes(bytes);
        if (!CryptUnprotectData(
                ref input.Value,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CryptProtectUiForbidden,
                out var output))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return CopyAndFree(output);
    }

    private static byte[] CopyAndFree(NativeBlob output)
    {
        try
        {
            var bytes = new byte[output.ByteCount];
            if (bytes.Length > 0)
            {
                Marshal.Copy(output.Data, bytes, 0, bytes.Length);
            }

            return bytes;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBlob
    {
        public int ByteCount;
        public IntPtr Data;
    }

    private sealed class DataBlob : IDisposable
    {
        private DataBlob(NativeBlob value) => Value = value;

        public NativeBlob Value;

        public static DataBlob FromBytes(byte[] bytes)
        {
            var pointer = bytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(bytes.Length);
            if (bytes.Length > 0)
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
            }

            return new DataBlob(new NativeBlob { ByteCount = bytes.Length, Data = pointer });
        }

        public void Dispose()
        {
            if (Value.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(Value.Data);
                Value.Data = IntPtr.Zero;
            }
        }
    }

    [DllImport("Crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref NativeBlob dataIn,
        string? dataDescription,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out NativeBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref NativeBlob dataIn,
        IntPtr dataDescription,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out NativeBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
