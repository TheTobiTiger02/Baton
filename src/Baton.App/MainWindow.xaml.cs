using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
        AudioToggle.IsChecked = UserSettings.StreamAudio;
        ShortcutToggle.IsChecked = WelcomeShortcut.IsChecked = DesktopShortcut.Exists;
        SuggestToggle.IsChecked = UserSettings.SuggestOnReturn;
        ShowHotkeyProblems();
        Welcome.Visibility = UserSettings.Welcomed ? Visibility.Collapsed : Visibility.Visible;
        VersionText.Text = $"Baton {services.Updates.CurrentVersion}";
        QualityChoice.SelectedItem = QualityChoice.Items.OfType<ComboBoxItem>()
            .First(item => (string)item.Tag == UserSettings.StreamQuality.ToString());

        IsVisibleChanged += (_, _) => UpdateDiagnosticsSummary();
        services.Runtime.Browser.Changed += () => Dispatcher.BeginInvoke(UpdateExtensionStatus);
        services.Runtime.Browser.ApprovalRequested += (_, _) => Dispatcher.BeginInvoke(UpdateExtensionStatus);
        UpdateExtensionStatus();
        services.Runtime.Apps.PreferencesChanged += () => Dispatcher.BeginInvoke(UpdateAppChoices);
        UpdateAppChoices();
    }

    public void ShowSettings() => NavSettings.IsChecked = true;

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
        AppChoicesHint.Visibility = items.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
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

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_services.Updates.ReadyVersion is not null)
        {
            _services.Updates.RestartToUpdate();
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatus.Text = "Checking…";
        UpdateStatus.Text = await _services.CheckForUpdatesAsync();
        UpdateButton.Content = _services.Updates.ReadyVersion is not null ? "Restart to update" : "Check for updates";
        UpdateButton.IsEnabled = true;
    }

    private void QualityChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityChoice.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<Baton.Host.Streaming.StreamQuality>(tag, out var quality))
        {
            UserSettings.StreamQuality = quality;
            _services.Host.WindowStreams.Quality = quality;
        }
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        DesktopShortcut.Set(((CheckBox)sender).IsChecked == true);
        ShortcutToggle.IsChecked = WelcomeShortcut.IsChecked = DesktopShortcut.Exists;
    }

    private void WelcomeSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void WelcomeDone_Click(object sender, RoutedEventArgs e)
    {
        UserSettings.Welcomed = true;
        Welcome.Visibility = Visibility.Collapsed;
    }

    private void AudioToggle_Click(object sender, RoutedEventArgs e)
    {
        UserSettings.StreamAudio = AudioToggle.IsChecked == true;
        _services.Host.WindowStreams.StreamAudio = UserSettings.StreamAudio;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DeviceViewModel phone
            && TextPrompt.Ask(this, $"Rename {phone.Name}", "Shown on this PC only. Leave empty to use the phone's own name.", phone.Name) is { } name)
        {
            _services.Host.DeviceNames.Set(phone.DeviceId, name);
        }
    }

    private void MakeDefault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DeviceViewModel phone)
        {
            _services.MakeDefault(phone.DeviceId);
        }
    }

    private Button? _capturing;

    /// <summary>A shortcut button waits for the new combination after a click.</summary>
    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        _capturing = (Button)sender;
        _capturing.Content = "Press keys…";
        _capturing.Focus();
    }

    private void Hotkey_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(sender, _capturing))
        {
            return;
        }

        e.Handled = true;
        var action = Enum.Parse<HotkeyAction>((string)_capturing.Tag);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndCapture();
            return;
        }

        Hotkey? hotkey = key == Key.Back ? null : Hotkey.FromKeyEvent(key, Keyboard.Modifiers);
        if (hotkey is null && key != Key.Back)
        {
            return; // Only modifiers so far, or a key without one.
        }

        var bound = _services.Rebind(action, hotkey);
        EndCapture();
        ShowHotkeyProblems(bound ? null : $"Another app already uses {hotkey}. Pick a different shortcut.");
    }

    private void Hotkey_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _capturing))
        {
            EndCapture();
        }
    }

    private void EndCapture()
    {
        var button = _capturing;
        _capturing = null;
        // The binding was replaced by "Press keys…"; restore it.
        button?.SetBinding(ContentControl.ContentProperty, (string)button.Tag switch
        {
            nameof(HotkeyAction.SendToPhone) => nameof(ShellViewModel.SendHotkey),
            nameof(HotkeyAction.ContinueHere) => nameof(ShellViewModel.ContinueHotkey),
            _ => nameof(ShellViewModel.ChooseHotkey)
        });
    }

    private void ShowHotkeyProblems(string? problem = null)
    {
        problem ??= _services.HotkeyProblems.Count > 0
            ? $"Another app already uses {string.Join(", ", _services.HotkeyProblems)}. Close it and restart Baton, or pick a different shortcut."
            : null;
        HotkeyWarning.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        HotkeyWarningText.Text = problem;
    }

    private void SuggestToggle_Click(object sender, RoutedEventArgs e) =>
        UserSettings.SuggestOnReturn = SuggestToggle.IsChecked == true;

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        StartupRegistration.SetEnabled(StartupToggle.IsChecked == true);
        StartupToggle.IsChecked = StartupRegistration.IsEnabled;
        AudioToggle.IsChecked = UserSettings.StreamAudio;
        ShortcutToggle.IsChecked = WelcomeShortcut.IsChecked = DesktopShortcut.Exists;
    }

    private void UpdateExtensionStatus()
    {
        var bridge = _services.Runtime.Browser;
        var browsers = bridge.ConnectedBrowsers;
        ExtensionStatus.Text = browsers.Count > 0
            ? $"Connected: {string.Join(", ", browsers)}"
            : "Not connected. Add it to your browser to hand over tabs exactly.";
        PendingBrowsers.ItemsSource = bridge.PendingApprovals.Select(pending => new { pending.ConnectionId, pending.Browser }).ToArray();
        AllowedBrowsers.ItemsSource = bridge.Approvals?.Approved
            .Select(browser => new { browser.Id, browser.Browser, Since = $"on {browser.ApprovedAt.ToLocalTime():d}" }).ToArray();
    }

    private async void AllowBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string connectionId })
        {
            await _services.Runtime.Browser.ApproveAsync(connectionId);
            UpdateExtensionStatus();
        }
    }

    private void RemoveBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            _services.Runtime.Browser.Approvals?.Revoke(id);
            UpdateExtensionStatus();
        }
    }

    private void Link_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
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

        // How long the last handoffs took to open, so "instant" can be checked.
        RecentHandoffs.Text = string.Join(Environment.NewLine, _services.Host.Timeline.Recent(5)
            .Where(handoff => handoff.Stages.Count > 1)
            .Select(handoff => $"{handoff.StartedAt.ToLocalTime():HH:mm:ss}  {string.Join(" → ", handoff.Stages.Skip(1).Select(stage => $"{stage.Stage} {stage.Ms} ms"))}"));
        RecentHandoffs.Visibility = RecentHandoffs.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var entries = _services.Host.Diagnostics.Snapshot(1);
        DiagnosticsSummary.Text = entries.Count == 0
            ? "Nothing recorded yet."
            : $"Last: {entries[0].Event} at {entries[0].Timestamp.ToLocalTime():t}";
    }
}

/// <summary>A welcome step's icon, replaced by a tick once the step is done.</summary>
public sealed class StepGlyphConverter : System.Windows.Data.IValueConverter
{
    private const string Done = "";

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Done : parameter as string ?? string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
