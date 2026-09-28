using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;

namespace Baton.App;

/// <summary>The panel above the notification-area icon: what can be continued, one click away.</summary>
public partial class FlyoutWindow : Window
{
    private const int MaxLocalItems = 3;
    private readonly AppServices _services;

    internal FlyoutWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DataContext = services.Shell;
        services.Shell.Local.Activities.CollectionChanged += (_, _) => RefreshLocal();
        RefreshLocal();
    }

    public void ShowNearTray(bool pinned)
    {
        if (IsVisible && !pinned)
        {
            Hide();
            return;
        }

        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
        Activate();
    }

    private void RefreshLocal() =>
        LocalList.ItemsSource = _services.Shell.Local.Activities.Take(MaxLocalItems).ToArray();

    private void Window_Deactivated(object? sender, EventArgs e) => Hide();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _services.ShowMain();
    }

    private void Action_Click(object sender, RoutedEventArgs e) => Hide();
}
