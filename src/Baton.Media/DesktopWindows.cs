using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Baton.Media;

public sealed record WindowInfo(IntPtr Handle, string Title, string ProcessName, int ProcessId);

/// <summary>Finding and describing top-level windows.</summary>
public static class DesktopWindows
{
    private static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "Baton", "Baton.DevHost", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost",
        "TextInputHost", "LockApp", "ApplicationFrameHost", "SystemSettings", "Taskmgr", "WindowsTerminal"
    };

    public static WindowInfo? Foreground() => Describe(GetForegroundWindow());

    /// <summary>A window worth offering: visible, titled, not a shell surface or Baton itself.</summary>
    public static WindowInfo? Describe(IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsWindowVisible(handle) || GetWindow(handle, 4 /* GW_OWNER */) != IntPtr.Zero)
        {
            return null;
        }

        var length = GetWindowTextLength(handle);
        if (length == 0)
        {
            return null;
        }

        var title = new StringBuilder(length + 1);
        GetWindowText(handle, title, title.Capacity);
        GetWindowThreadProcessId(handle, out var processId);
        string processName;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }

        return IgnoredProcesses.Contains(processName) ? null : new WindowInfo(handle, title.ToString(), processName, (int)processId);
    }

    /// <summary>The main window of the first running process with this executable name (with or without .exe).</summary>
    public static WindowInfo? MainWindowOf(string processOrAumid)
    {
        if (WindowWithAppId(processOrAumid) is { } tagged)
        {
            return tagged;
        }

        foreach (var name in CandidateProcessNames(processOrAumid))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.MainWindowHandle != IntPtr.Zero && Describe(process.MainWindowHandle) is { } window)
                    {
                        return window;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The window that carries <paramref name="appId"/> as its taskbar identity. Browsers tag their
    /// windows this way (Opera's id is an opaque hash), so this is how a media session is traced
    /// back to the app showing it.
    /// </summary>
    public static WindowInfo? WindowWithAppId(string appId)
    {
        WindowInfo? found = null;
        EnumWindows((handle, _) =>
        {
            if (AppIdOf(handle) is { } id && string.Equals(id, appId, StringComparison.OrdinalIgnoreCase) && Describe(handle) is { } window)
            {
                found = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static string? AppIdOf(IntPtr window)
    {
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(window, ref iid, out var store) != 0 || store is null)
        {
            return null;
        }

        try
        {
            var key = new PropertyKey { FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), PropertyId = 5 };
            var value = new PropVariantString();
            if (store.GetValue(ref key, ref value) != 0 || value.VarType != 31 /* VT_LPWSTR */ || value.Pointer == IntPtr.Zero)
            {
                return null;
            }

            var id = Marshal.PtrToStringUni(value.Pointer);
            PropVariantClear(ref value);
            return id;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantString
    {
        [FieldOffset(0)] public ushort VarType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, ref PropVariantString value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariantString value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariantString value);

    /// <summary>
    /// Executable names an app id may belong to: <c>mpv.exe</c> is mpv, and an explicit id such as
    /// <c>app.harbor</c> or <c>Company.Product</c> usually ends in the executable's name.
    /// </summary>
    public static IEnumerable<string> CandidateProcessNames(string processOrAumid)
    {
        var id = processOrAumid.Split('!')[0].Split('\\')[^1];
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            yield return id[..^4];
            yield break;
        }

        yield return id;
        foreach (var segment in Enumerable.Reverse(id.Split('.', '_')).Where(segment => segment.Length > 2))
        {
            yield return segment;
        }
    }

    public static bool Exists(IntPtr handle) => IsWindow(handle);

    /// <summary>The first offerable top-level window whose title contains <paramref name="text"/>.</summary>
    public static WindowInfo? FindByTitle(string text)
    {
        WindowInfo? found = null;
        EnumWindows((handle, _) =>
        {
            if (Describe(handle) is { } window && window.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                found = window;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    public static string Token(IntPtr handle) => handle.ToInt64().ToString("x");

    public static IntPtr FromToken(string token) =>
        long.TryParse(token, System.Globalization.NumberStyles.HexNumber, null, out var value) ? new IntPtr(value) : IntPtr.Zero;

    /// <summary>The window's visible bounds on screen, without the invisible resize border and shadow.</summary>
    public static (int Left, int Top, int Width, int Height) Bounds(IntPtr handle)
    {
        if (DwmGetWindowAttribute(handle, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var rect, Marshal.SizeOf<Rect>()) != 0)
        {
            GetWindowRect(handle, out rect);
        }

        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Restores a minimized window: a minimized window has nothing to capture.</summary>
    public static void EnsureRestored(IntPtr handle)
    {
        if (IsIconic(handle))
        {
            ShowWindow(handle, 9 /* SW_RESTORE */);
        }
    }

    /// <summary>The monitor a window is mostly on, and that monitor's bounds in screen pixels.</summary>
    public static (IntPtr Monitor, int Left, int Top, int Width, int Height) MonitorOf(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        return (monitor, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top);
    }

    /// <summary>The usable area (without the taskbar) of the window's monitor.</summary>
    public static (int Left, int Top, int Width, int Height) WorkAreaOf(IntPtr handle)
    {
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        return (info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    /// <summary>True when the window covers its whole monitor: a fullscreen game or player.</summary>
    public static bool IsFullscreen(IntPtr handle)
    {
        var (left, top, width, height) = Bounds(handle);
        var (_, monitorLeft, monitorTop, monitorWidth, monitorHeight) = MonitorOf(handle);
        return left <= monitorLeft && top <= monitorTop && width >= monitorWidth && height >= monitorHeight;
    }

    /// <summary>True when the whole window is on its monitor.</summary>
    public static bool IsOnScreen(IntPtr handle)
    {
        var (left, top, width, height) = Bounds(handle);
        var (_, monitorLeft, monitorTop, monitorWidth, monitorHeight) = MonitorOf(handle);
        return left >= monitorLeft && top >= monitorTop && left + width <= monitorLeft + monitorWidth && top + height <= monitorTop + monitorHeight;
    }

    /// <summary>Saves where a window is, so it can be put back exactly (including maximized state).</summary>
    public static byte[] SavePlacement(IntPtr handle)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        GetWindowPlacement(handle, ref placement);
        var bytes = new byte[placement.Length];
        var pointer = Marshal.AllocHGlobal(placement.Length);
        try
        {
            Marshal.StructureToPtr(placement, pointer, false);
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return bytes;
    }

    public static void RestorePlacement(IntPtr handle, byte[] saved)
    {
        var pointer = Marshal.AllocHGlobal(saved.Length);
        try
        {
            Marshal.Copy(saved, 0, pointer, saved.Length);
            var placement = Marshal.PtrToStructure<WindowPlacement>(pointer);
            SetWindowPlacement(handle, ref placement);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    /// <summary>
    /// The largest rectangle of <paramref name="aspect"/> (width / height) that fits the area,
    /// centred: where a window goes to fill a phone screen exactly.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) FitRect((int Left, int Top, int Width, int Height) area, double aspect)
    {
        var width = area.Width;
        var height = (int)(width / aspect);
        if (height > area.Height)
        {
            height = area.Height;
            width = (int)(height * aspect);
        }

        return (area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    /// <summary>Resizes a (restored) window so its visible frame is <paramref name="target"/>.</summary>
    public static void MoveTo(IntPtr handle, (int Left, int Top, int Width, int Height) target)
    {
        if (IsZoomed(handle) || IsIconic(handle))
        {
            ShowWindow(handle, 9 /* SW_RESTORE */);
        }

        // SetWindowPos works on the outer frame, which includes the invisible resize border;
        // compensate so the visible frame lands where asked.
        GetWindowRect(handle, out var outer);
        var (left, top, width, height) = Bounds(handle);
        var extraLeft = left - outer.Left;
        var extraTop = top - outer.Top;
        var extraWidth = outer.Right - outer.Left - width;
        var extraHeight = outer.Bottom - outer.Top - height;
        SetWindowPos(handle, IntPtr.Zero, target.Left - extraLeft, target.Top - extraTop,
            target.Width + extraWidth, target.Height + extraHeight, 0x0004 | 0x0010 /* NOZORDER | NOACTIVATE */);
    }

    public static void BringToFront(IntPtr handle)
    {
        EnsureRestored(handle);
        SetForegroundWindow(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public int MinX, MinY, MaxX, MaxY;
        public Rect Normal;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out Rect value, int size);
}

/// <summary>
/// Plays the phone's touches and typing into a PC window with SendInput. Positions arrive as
/// fractions of the window, so they survive any difference in resolution.
/// </summary>
public static class InputInjector
{
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint MouseMove = 0x1, LeftDown = 0x2, LeftUp = 0x4, RightDown = 0x8, RightUp = 0x10, Wheel = 0x800, HWheel = 0x1000;
    private const uint Absolute = 0x8000, VirtualDesk = 0x4000;
    private const uint KeyUp = 0x2, Unicode = 0x4;

    public static void Pointer(IntPtr window, double x, double y, string action)
    {
        var (left, top, width, height) = DesktopWindows.Bounds(window);
        MoveTo(left + x * width, top + y * height);
        switch (action)
        {
            case "down":
                DesktopWindows.BringToFront(window);
                Mouse(LeftDown);
                break;
            case "up":
                Mouse(LeftUp);
                break;
            case "right":
                DesktopWindows.BringToFront(window);
                Mouse(RightDown);
                Mouse(RightUp);
                break;
        }
    }

    /// <summary>Scrolls by <paramref name="notchesY"/> wheel notches (positive = up).</summary>
    public static void Scroll(IntPtr window, double x, double y, double notchesX, double notchesY)
    {
        var (left, top, width, height) = DesktopWindows.Bounds(window);
        MoveTo(left + x * width, top + y * height);
        if (notchesY != 0)
        {
            Mouse(Wheel, (int)(notchesY * 120));
        }

        if (notchesX != 0)
        {
            Mouse(HWheel, (int)(notchesX * 120));
        }
    }

    /// <summary>Moves the cursor by a relative amount, like a trackpad.</summary>
    /// <remarks>
    /// Absolute from the current position rather than a relative mouse move, so Windows' pointer
    /// acceleration does not distort the phone's finger movement. Kept inside <paramref name="clip"/>.
    /// During a burst of moves the position is tracked here: <c>GetCursorPos</c> lags behind
    /// <c>SendInput</c>, and reading it back would drop movement.
    /// </remarks>
    public static void MoveBy(int dx, int dy, (int Left, int Top, int Width, int Height) clip)
    {
        lock (TrackedGate)
        {
            if (Environment.TickCount64 - _trackedAt > 400)
            {
                GetCursorPos(out var cursor);
                _tracked = (cursor.X, cursor.Y);
            }

            _tracked = (Math.Clamp(_tracked.X + dx, clip.Left, clip.Left + clip.Width - 1),
                Math.Clamp(_tracked.Y + dy, clip.Top, clip.Top + clip.Height - 1));
            _trackedAt = Environment.TickCount64;
            MoveTo(_tracked.X, _tracked.Y);
        }
    }

    private static readonly object TrackedGate = new();
    private static (int X, int Y) _tracked;
    private static long _trackedAt = long.MinValue / 2;

    /// <summary>Moves the cursor to a point on screen, in pixels.</summary>
    public static void MoveToScreen(int x, int y) => MoveTo(x, y);

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);

    public static void Button(MouseButton button, bool down)
    {
        uint flags = (button, down) switch
        {
            (MouseButton.Right, true) => RightDown,
            (MouseButton.Right, false) => RightUp,
            (MouseButton.Middle, true) => 0x20,
            (MouseButton.Middle, false) => 0x40,
            (_, true) => LeftDown,
            _ => LeftUp
        };
        Mouse(flags);
    }

    /// <summary>Wheel notches (positive = up / right) wherever the cursor is.</summary>
    public static void ScrollAtCursor(int notchesX, int notchesY)
    {
        if (notchesY != 0)
        {
            Mouse(Wheel, notchesY * 120);
        }

        if (notchesX != 0)
        {
            Mouse(HWheel, notchesX * 120);
        }
    }

    /// <summary>Wheel notches at a point on screen, in pixels.</summary>
    public static void ScrollAt(int screenX, int screenY, int notchesX, int notchesY)
    {
        MoveTo(screenX, screenY);
        ScrollAtCursor(notchesX, notchesY);
    }

    /// <summary>Presses and releases a virtual key while holding <paramref name="modifiers"/>.</summary>
    public static void Chord(ushort virtualKey, IReadOnlyList<ushort> modifiers)
    {
        var inputs = new List<Input>();
        inputs.AddRange(modifiers.Select(modifier => Key(modifier, 0, 0)));
        var extended = IsExtended(virtualKey) ? 0x1u : 0u;
        inputs.Add(Key(virtualKey, 0, extended));
        inputs.Add(Key(virtualKey, 0, extended | KeyUp));
        inputs.AddRange(modifiers.Reverse().Select(modifier => Key(modifier, 0, KeyUp)));
        Send(inputs.ToArray());
    }

    private static bool IsExtended(ushort vk) =>
        vk is 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x5B or 0x5C;

    public static void Text(string text)
    {
        var inputs = new List<Input>();
        foreach (var character in text)
        {
            inputs.Add(Key(0, character, Unicode));
            inputs.Add(Key(0, character, Unicode | KeyUp));
        }

        Send(inputs.ToArray());
    }

    /// <summary>Presses a named key: Enter, Backspace, Tab, Escape, Space, Left, Right, Up, Down, Delete.</summary>
    public static void Press(string key)
    {
        ushort vk = key switch
        {
            "Enter" => 0x0D, "Backspace" => 0x08, "Tab" => 0x09, "Escape" => 0x1B, "Space" => 0x20,
            "Left" => 0x25, "Up" => 0x26, "Right" => 0x27, "Down" => 0x28, "Delete" => 0x2E,
            "F" => 0x46, "F11" => 0x7A, _ => 0
        };
        if (vk != 0)
        {
            Send([Key(vk, 0, 0), Key(vk, 0, KeyUp)]);
        }
    }

    private static void MoveTo(double screenX, double screenY)
    {
        var virtualLeft = GetSystemMetrics(76);
        var virtualTop = GetSystemMetrics(77);
        var virtualWidth = Math.Max(1, GetSystemMetrics(78));
        var virtualHeight = Math.Max(1, GetSystemMetrics(79));
        // Aim at the pixel's centre, so rounding never lands on its neighbour.
        var nx = (int)Math.Round((screenX - virtualLeft + 0.5) * 65535.0 / virtualWidth);
        var ny = (int)Math.Round((screenY - virtualTop + 0.5) * 65535.0 / virtualHeight);
        Send([new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { X = nx, Y = ny, Flags = MouseMove | Absolute | VirtualDesk } } }]);
    }

    private static void Mouse(uint flags, int data = 0) =>
        Send([new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } } }]);

    private static Input Key(ushort vk, ushort scan, uint flags) =>
        new() { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, Scan = scan, Flags = flags } } };

    private static void Send(Input[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y, MouseData;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        // ushort, not char: a char makes the union non-blittable, and marshalling it then
        // overwrites the mouse fields that share its memory.
        public ushort Scan;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
