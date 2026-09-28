using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Shell;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace Baton.App;

/// <summary>
/// Gives a window the Windows 11 look: Mica behind the client area and a native frame that follows
/// the app's light/dark theme. Windows without Mica (Windows 10, early Windows 11, high contrast)
/// get the solid theme background instead, so the same XAML works everywhere.
/// </summary>
internal static class WindowBackdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3;
    private const int DwmwcpRound = 2;

    // DWMWA_SYSTEMBACKDROP_TYPE first shipped in Windows 11 22H2.
    private const int MicaMinimumBuild = 22621;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    public static bool SupportsMica =>
        Environment.OSVersion.Version.Build >= MicaMinimumBuild && !ThemeManager.IsHighContrast;

    /// <summary>
    /// Call once the window has a handle; the backdrop then tracks theme changes on its own.
    /// <paramref name="transient"/> is for flyouts and notifications: acrylic instead of Mica, as
    /// Windows' own flyouts have.
    /// </summary>
    public static void Attach(Window window, bool transient = false)
    {
        void Apply(object? sender, EventArgs e) => window.Dispatcher.BeginInvoke(() => ApplyCore(window, transient));

        if (new WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => ApplyCore(window, transient);
        }
        else
        {
            ApplyCore(window, transient);
        }

        ThemeManager.ThemeChanged += Apply;
        window.Closed += (_, _) => ThemeManager.ThemeChanged -= Apply;
    }

    private static void ApplyCore(Window window, bool transient)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || HwndSource.FromHwnd(handle) is not { } source)
        {
            return;
        }

        // With Mica the frame reaches into the client area and DWM draws the real caption buttons
        // there (with Snap Layouts); WindowChrome only has to route hit-testing to them. Without Mica
        // the opaque background would cover them, so CaptionButtons draws its own instead.
        if (WindowChrome.GetWindowChrome(window) is { } chrome && chrome.UseAeroCaptionButtons != SupportsMica)
        {
            var updated = (WindowChrome)chrome.Clone();
            updated.UseAeroCaptionButtons = SupportsMica;
            WindowChrome.SetWindowChrome(window, updated);
        }

        var dark = ThemeManager.IsDark ? 1 : 0;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        var corners = DwmwcpRound;
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));

        if (SupportsMica)
        {
            // Mica is drawn by DWM behind the frame, so WPF has to leave the whole client area clear.
            source.CompositionTarget!.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
            var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
            var backdrop = transient ? DwmsbtTransientWindow : DwmsbtMainWindow;
            DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        }
        else
        {
            if (Environment.OSVersion.Version.Build >= MicaMinimumBuild)
            {
                var backdrop = DwmsbtNone;
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
            }

            window.SetResourceReference(Window.BackgroundProperty, transient ? "FlyoutBrush" : "SolidBackgroundBrush");
        }
    }
}
