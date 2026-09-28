using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Baton.Host.Handoff;
using Baton.Protocol;

namespace Baton.App;

/// <summary>A small, non-activating status card above the notification area.</summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _hide;
    private Action? _action;

    public ToastWindow()
    {
        InitializeComponent();
        WindowBackdrop.Attach(this, transient: true);
        _hide = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal, (_, _) => HideNow(), Dispatcher);
        _hide.Stop();
    }

    /// <summary>A suggestion with one button, e.g. continuing a phone's video on return.</summary>
    public void ShowSuggestion(Activity activity, string heading, string actionLabel, Action onAction) =>
        ShowAction(heading, activity.Subtitle is { } subtitle ? $"{activity.Title} · {subtitle}" : activity.Title, "", actionLabel, onAction);

    /// <summary>A notice with one button, e.g. an update that is ready.</summary>
    public void ShowAction(string heading, string detail, string glyph, string actionLabel, Action onAction)
    {
        Heading.Text = heading;
        Detail.Text = detail;
        Glyph.Text = glyph;
        Badge.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "AccentSubtleBrush");
        Glyph.SetResourceReference(ForegroundProperty, "AccentBrush");
        _action = onAction;
        ActionButton.Content = actionLabel;
        ActionButton.Visibility = Visibility.Visible;
        Present(TimeSpan.FromSeconds(12));
    }

    public void Show(HandoffEvent handoff, string sourceName, string targetName)
    {
        _action = null;
        ActionButton.Visibility = Visibility.Collapsed;
        (Heading.Text, Detail.Text, Glyph.Text) = handoff.Status switch
        {
            null => (handoff.Title, handoff.Detail ?? $"Moving to {targetName}…", ""),
            HandoffStatus.Opened => ($"Continuing on {targetName}", handoff.Title, ""),
            HandoffStatus.Fallback => ($"Continuing on {targetName}", handoff.Detail ?? handoff.Title, ""),
            _ => (handoff.Title == "Nothing to continue" || handoff.Title == "No phone connected"
                    ? handoff.Title
                    : $"Couldn't continue on {targetName}",
                handoff.Detail ?? handoff.Title, "")
        };
        Badge.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, handoff.Status switch
        {
            HandoffStatus.Failed => "DangerFillBrush",
            HandoffStatus.Opened => "SuccessFillBrush",
            _ => "AccentSubtleBrush"
        });
        Glyph.SetResourceReference(ForegroundProperty, handoff.Status switch
        {
            HandoffStatus.Failed => "DangerBrush",
            HandoffStatus.Opened => "SuccessBrush",
            _ => "AccentBrush"
        });

        Present(handoff.Status is null ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(handoff.Status == HandoffStatus.Failed ? 7 : 4));
    }

    private void Present(TimeSpan duration)
    {
        base.Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 12;
        Top = area.Bottom - ActualHeight - 12;
        _hide.Stop();
        _hide.Interval = duration;
        _hide.Start();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        var action = _action;
        HideNow();
        action?.Invoke();
    }

    private void HideNow()
    {
        _hide.Stop();
        Hide();
    }

    private void Card_Click(object sender, MouseButtonEventArgs e) => HideNow();
}
