using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Baton.App;

/// <summary>
/// The optional Baton shortcut on the desktop. Setup adds only the Start menu entry; this is the
/// switch in the welcome card and in Settings.
/// </summary>
internal static class DesktopShortcut
{
    private static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Baton.lnk");

    public static bool Exists => File.Exists(Path);

    /// <summary>
    /// Adds or removes it. An installed copy points at Velopack's launcher next to the versions,
    /// which stays valid across updates; any other copy at its own executable.
    /// </summary>
    public static void Set(bool present)
    {
        try
        {
            if (!present)
            {
                File.Delete(Path);
                return;
            }

            var current = Environment.ProcessPath ?? throw new InvalidOperationException("No executable path.");
            var launcher = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(current)!, "..", "Baton.exe"));
            var target = File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(launcher)!, "Update.exe")) ? launcher : current;

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target)!);
            link.SetDescription("Continue what you're doing on your other device.");
            link.SetIconLocation(current, 0);
            ((IPersistFile)link).Save(Path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            // The switch shows the real state again next time.
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int size, IntPtr data, int flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription(IntPtr name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation(IntPtr path, int size, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
