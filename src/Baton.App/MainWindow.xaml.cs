using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Baton.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;

    internal MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        DataContext = services.Shell;
        WindowBackdrop.Attach(this);
        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        SuggestToggle.IsChecked = UserSettings.SuggestOnReturn;
        if (services.HotkeyProblems.Count > 0)
        {
            HotkeyWarning.Visibility = Visibility.Visible;
            HotkeyWarningText.Text = $"Another app already uses {string.Join(", ", services.HotkeyProblems)}. Close it and restart Baton to use that shortcut.";
        }

        IsVisibleChanged += (_, _) => UpdateDiagnosticsSummary();
        services.Runtime.Browser.Changed += () => Dispatcher.BeginInvoke(UpdateExtensionStatus);
        UpdateExtensionStatus();
        services.Runtime.Apps.PreferencesChanged += () => Dispatcher.BeginInvoke(UpdateAppChoices);
        UpdateAppChoices();
    }

    private void UpdateAppChoices()
    {
        var items = _services.Runtime.Apps.Preferences.Select(preference => new
        {
            preference.Key,
            preference.SourceName,
            Description = $"{(preference.Key.StartsWith("android:", StringComparison.Ordinal) ? "From the phone, on this PC" : "From this PC, on the phone")}: {preference.Choice.Label}"
        }).ToArray();
        AppChoicesList.ItemsSource = items;
        AppChoicesEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ForgetChoice_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string key })
        {
            _services.Runtime.Apps.Forget(key);
        }
    }

    /// <summary>Closing only hides the window; Baton keeps running in the notification area.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePage is null)
        {
            return;
        }

        HomePage.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DevicesPage.Visibility = NavDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateDiagnosticsSummary();
    }

    private void Pair_Click(object sender, RoutedEventArgs e) => _services.ShowPairing();

    private async void Forget_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DeviceViewModel phone)
        {
            return;
        }

        var answer = MessageBox.Show(this,
            $"Forget {phone.Name}? It will have to be paired again to use Baton with this PC.",
            "Forget phone", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK)
        {
            await _services.Host.ForgetAsync(phone.DeviceId);
        }
    }

    private void SuggestToggle_Click(object sender, RoutedEventArgs e) =>
        UserSettings.SuggestOnReturn = SuggestToggle.IsChecked == true;

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        StartupRegistration.SetEnabled(StartupToggle.IsChecked == true);
        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
    }

    private void UpdateExtensionStatus()
    {
        var browsers = _services.Runtime.Browser.ConnectedBrowsers;
        ExtensionStatus.Text = browsers.Count > 0
            ? $"Connected: {string.Join(", ", browsers)}"
            : "Not connected. Add it to your browser to hand over tabs exactly.";
    }

    private void ExtensionFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.Combine(AppContext.BaseDirectory, "BrowserExtension");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (var entry in _services.Host.Diagnostics.Snapshot())
        {
            text.AppendLine($"{entry.Timestamp:O} {entry.Severity,-7} {entry.Category,-9} {entry.Event} {entry.DeviceId} {entry.Detail}");
        }

        Clipboard.SetText(text.ToString());
        DiagnosticsSummary.Text = "Copied to the clipboard.";
    }

    private void UpdateDiagnosticsSummary()
    {
        if (DiagnosticsSummary is null)
        {
            return;
        }

        var entries = _services.Host.Diagnostics.Snapshot(1);
        DiagnosticsSummary.Text = entries.Count == 0
            ? "Nothing recorded yet."
            : $"Last: {entries[0].Event} at {entries[0].Timestamp.ToLocalTime():t}";
    }
}

public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
