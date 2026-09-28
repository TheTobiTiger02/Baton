using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Baton.App.Mirror;

public enum SurfacePointerAction
{
    Down,
    Move,
    Up
}

[Flags]
public enum SurfaceButtons
{
    None = 0,
    Left = 1,
    Right = 2,
    Middle = 4
}

public readonly record struct SurfacePointerEvent(SurfacePointerAction Action, int X, int Y, SurfaceButtons Buttons);

public readonly record struct SurfaceWheelEvent(int X, int Y, int VerticalNotches, int HorizontalNotches);

public readonly record struct SurfaceKeyEvent(bool IsDown, int VirtualKey, bool Control, bool Shift, bool Alt, int RepeatCount);

/// <summary>
/// A bare child HWND for the mirror's swap chain to present into.
///
/// WPF cannot draw over an <see cref="HwndHost"/> - the "airspace" limitation - which is why the
/// mirror window keeps its chrome around the video rather than on top of it. In exchange the swap
/// chain presents straight to the window with no shared surface and no compositor hop, which is
/// the difference between a frame of latency and none.
/// </summary>
public sealed class MirrorSurfaceHost : HwndHost
{
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmChar = 0x0102;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const int WmGetDlgCode = 0x0087;
    private const int WmDropFiles = 0x0233;
    private const int DlgcWantAllKeys = 0x0004;
    private const int DlgcWantChars = 0x0080;
    private const int WheelDelta = 120;

    private int _wheelRemainder;
    private int _hWheelRemainder;

    /// <summary>
    /// Pointer input over the video, in surface pixels. Raised from the window's message loop;
    /// WPF never sees mouse events over an HwndHost, so this is the only place they exist.
    /// </summary>
    public event Action<SurfacePointerEvent>? PointerInput;

    public event Action<SurfaceWheelEvent>? WheelInput;

    public event Action<SurfaceKeyEvent>? KeyInput;

    /// <summary>A printable character after keyboard layout translation.</summary>
    public event Action<char>? CharacterInput;

    /// <summary>
    /// Files dropped from Explorer onto the video. WPF drag and drop cannot see this area (the
    /// airspace rule again), so the child window takes classic shell drops itself.
    /// </summary>
    public event Action<string[]>? FilesDropped;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsClipSiblings = 0x04000000;

    /// <summary>Valid between <see cref="SurfaceCreated"/> and window teardown.</summary>
    public nint SurfaceHandle { get; private set; }

    public event Action<nint, int, int>? SurfaceCreated;

    public event Action<int, int>? SurfaceResized;

    public (int Width, int Height) PixelSize
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            return (
                Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY)));
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var handle = CreateWindowExW(
            0,
            "static",
            null,
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            nint.Zero,
            nint.Zero,
            nint.Zero);

        SurfaceHandle = handle;
        DragAcceptFiles(handle, true);

        var (width, height) = PixelSize;
        SurfaceCreated?.Invoke(handle, width, height);
        return new HandleRef(this, handle);
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                // A static control is transparent to the mouse (HTTRANSPARENT), so clicks would go
                // to the window behind it and never reach the phone.
                handled = true;
                return HtClient;

            case WmGetDlgCode:
                // Without this, WPF keyboard navigation eats Tab, arrows and Enter before the
                // mirror ever sees them.
                handled = true;
                return DlgcWantAllKeys | DlgcWantChars;

            case WmLButtonDown:
            case WmRButtonDown:
            case WmMButtonDown:
                SetFocus(hwnd);
                SetCapture(hwnd);
                RaisePointer(SurfacePointerAction.Down, lParam, ButtonFor(msg));
                handled = true;
                return 0;

            case WmMouseMove:
                var held = ButtonsFromWParam(wParam);
                if (held != SurfaceButtons.None)
                {
                    RaisePointer(SurfacePointerAction.Move, lParam, held);
                }

                handled = true;
                return 0;

            case WmLButtonUp:
            case WmRButtonUp:
            case WmMButtonUp:
                ReleaseCapture();
                RaisePointer(SurfacePointerAction.Up, lParam, ButtonFor(msg));
                handled = true;
                return 0;

            case WmMouseWheel:
            case WmMouseHWheel:
                RaiseWheel(hwnd, msg == WmMouseHWheel, wParam, lParam);
                handled = true;
                return 0;

            case WmKeyDown:
            case WmSysKeyDown:
            case WmKeyUp:
            case WmSysKeyUp:
                var isDown = msg is WmKeyDown or WmSysKeyDown;
                KeyInput?.Invoke(new SurfaceKeyEvent(
                    isDown,
                    (int)wParam,
                    (GetKeyState(0x11) & 0x8000) != 0,
                    (GetKeyState(0x10) & 0x8000) != 0,
                    (GetKeyState(0x12) & 0x8000) != 0,
                    (int)((long)lParam & 0xFFFF)));

                // Alt combinations stay unhandled so the window's own shortcuts still work.
                handled = msg is WmKeyDown or WmKeyUp;
                return 0;

            case WmDropFiles:
                var files = ReadDroppedFiles(wParam);
                if (files.Length > 0)
                {
                    FilesDropped?.Invoke(files);
                }

                handled = true;
                return 0;

            case WmChar:
                var character = (char)(long)wParam;
                if (!char.IsControl(character))
                {
                    CharacterInput?.Invoke(character);
                }

                handled = true;
                return 0;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static string[] ReadDroppedFiles(nint drop)
    {
        try
        {
            var count = DragQueryFileW(drop, uint.MaxValue, null, 0);
            var files = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFileW(drop, index, null, 0);
                var buffer = new char[length + 1];
                if (DragQueryFileW(drop, index, buffer, (uint)buffer.Length) > 0)
                {
                    files.Add(new string(buffer, 0, (int)length));
                }
            }

            return files.ToArray();
        }
        finally
        {
            DragFinish(drop);
        }
    }

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(nint drop, uint index, char[]? file, uint length);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(nint drop);

    private void RaisePointer(SurfacePointerAction action, nint lParam, SurfaceButtons buttons)
    {
        var x = unchecked((short)((long)lParam & 0xFFFF));
        var y = unchecked((short)(((long)lParam >> 16) & 0xFFFF));
        PointerInput?.Invoke(new SurfacePointerEvent(action, x, y, buttons));
    }

    private void RaiseWheel(nint hwnd, bool horizontal, nint wParam, nint lParam)
    {
        // Wheel coordinates are screen-relative, unlike every other mouse message.
        var point = new Point32
        {
            X = unchecked((short)((long)lParam & 0xFFFF)),
            Y = unchecked((short)(((long)lParam >> 16) & 0xFFFF))
        };
        ScreenToClient(hwnd, ref point);

        // Precision touchpads send fractions of a notch; accumulate rather than drop them.
        var delta = unchecked((short)(((long)wParam >> 16) & 0xFFFF));
        if (horizontal)
        {
            _hWheelRemainder += delta;
            var notches = _hWheelRemainder / WheelDelta;
            _hWheelRemainder -= notches * WheelDelta;
            if (notches != 0)
            {
                WheelInput?.Invoke(new SurfaceWheelEvent(point.X, point.Y, 0, notches));
            }
        }
        else
        {
            _wheelRemainder += delta;
            var notches = _wheelRemainder / WheelDelta;
            _wheelRemainder -= notches * WheelDelta;
            if (notches != 0)
            {
                WheelInput?.Invoke(new SurfaceWheelEvent(point.X, point.Y, notches, 0));
            }
        }
    }

    private static SurfaceButtons ButtonFor(int msg) => msg switch
    {
        WmLButtonDown or WmLButtonUp => SurfaceButtons.Left,
        WmRButtonDown or WmRButtonUp => SurfaceButtons.Right,
        _ => SurfaceButtons.Middle
    };

    private static SurfaceButtons ButtonsFromWParam(nint wParam)
    {
        var flags = (long)wParam;
        var buttons = SurfaceButtons.None;
        if ((flags & 0x0001) != 0) buttons |= SurfaceButtons.Left;
        if ((flags & 0x0002) != 0) buttons |= SurfaceButtons.Right;
        if ((flags & 0x0010) != 0) buttons |= SurfaceButtons.Middle;
        return buttons;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint hwnd, ref Point32 point);

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        SurfaceHandle = nint.Zero;
        DestroyWindow(hwnd.Handle);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        if (SurfaceHandle == nint.Zero)
        {
            return;
        }

        var (width, height) = PixelSize;
        SurfaceResized?.Invoke(width, height);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);
}
