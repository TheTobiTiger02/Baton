using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace Baton.App.Controls;

/// <summary>
/// Caption area for windows that draw their own title bar. With Mica, DWM draws the native buttons
/// in this space and the control only reserves it (see WindowBackdrop). Otherwise it draws
/// minimize, maximize and close itself; the maximize button then answers WM_NCHITTEST with
/// HTMAXBUTTON so Windows 11 still shows the Snap Layouts flyout over it.
/// </summary>
public partial class CaptionButtons : UserControl
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A2;
    private const int HtMaxButton = 9;
    private const int SmCxFrame = 32;
    private const int SmCxPaddedBorder = 92;
    private const uint TmeLeave = 0x2;
    private const uint TmeNonClient = 0x10;
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr Target;
        public uint HoverTime;
    }

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    private Window? _window;
    private HwndSource? _source;

    public CaptionButtons()
    {
        InitializeComponent();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        _window = Window.GetWindow(this);
        if (_window is null)
        {
            return;
        }

        _window.StateChanged += Window_StateChanged;
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        _source = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
        _source?.AddHook(WndProc);
        UpdateState();
    }

    private void Detach()
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
        _source?.RemoveHook(WndProc);
        _source = null;
        if (_window is not null)
        {
            _window.StateChanged -= Window_StateChanged;
            _window = null;
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => UpdateState();

    private void ThemeManager_ThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateState);

    private void UpdateState()
    {
        if (_window is null)
        {
            return;
        }

        // Hidden, not collapsed: the native buttons still need the space.
        Visibility = WindowBackdrop.SupportsMica ? Visibility.Hidden : Visibility.Visible;

        var maximized = _window.WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? RestoreGlyph : MaximizeGlyph;
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, maximized ? "Restore" : "Maximize");

        // A maximized WindowChrome window overhangs the screen by its invisible resize frame.
        if (_window.Content is FrameworkElement root)
        {
            var frame = (GetSystemMetrics(SmCxFrame) + GetSystemMetrics(SmCxPaddedBorder)) / VisualTreeHelper.GetDpi(_window).DpiScaleX;
            root.Margin = maximized ? new Thickness(frame) : new Thickness(0);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WmNcHitTest when MaximizeButton.IsVisible && IsOverMaximizeButton(lParam):
                handled = true;
                return new IntPtr(HtMaxButton);
            case WmNcMouseMove:
                var overMaximize = wParam.ToInt32() == HtMaxButton;
                if (overMaximize)
                {
                    var track = new TrackMouseEventInfo
                    {
                        Size = (uint)Marshal.SizeOf<TrackMouseEventInfo>(),
                        Flags = TmeLeave | TmeNonClient,
                        Target = hwnd
                    };
                    TrackMouseEvent(ref track);
                }
                if (!Equals(MaximizeButton.Tag, "pressed"))
                {
                    MaximizeButton.Tag = overMaximize ? "hover" : null;
                }
                break;
            case WmNcMouseLeave:
                MaximizeButton.Tag = null;
                break;
            case WmNcLButtonDown when wParam.ToInt32() == HtMaxButton:
                MaximizeButton.Tag = "pressed";
                handled = true;
                break;
            case WmNcLButtonUp when wParam.ToInt32() == HtMaxButton:
                MaximizeButton.Tag = "hover";
                ToggleMaximize();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var screenPoint = new Point((short)(value & 0xFFFF), (short)((value >> 16) & 0xFFFF));
        try
        {
            var local = MaximizeButton.PointFromScreen(screenPoint);
            return local.X >= 0 && local.Y >= 0 && local.X < MaximizeButton.ActualWidth && local.Y < MaximizeButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ToggleMaximize()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(_window);
        }
        else
        {
            SystemCommands.MaximizeWindow(_window);
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_window is not null)
        {
            SystemCommands.MinimizeWindow(_window);
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => _window?.Close();
}
