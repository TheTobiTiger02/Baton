using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Baton.Protocol;

namespace Baton.App;

/// <summary>
/// "Continue with…": which app an activity should continue in on the other device. Asked once per
/// app; the answer is remembered (unless unticked) and can be changed from the card later.
/// </summary>
internal sealed class ContinueWithWindow : Window
{
    private readonly List<(RadioButton Button, HandoffChoice Choice)> _buttons = [];
    private readonly CheckBox _remember;

    private ContinueWithWindow(string title, string appName, string targetName, IReadOnlyList<HandoffChoice> options, HandoffChoice preselected)
    {
        Title = "Continue with";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        MinWidth = 380;
        SetResourceReference(BackgroundProperty, "SolidBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = $"Continue {title}", FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 });
        panel.Children.Add(new TextBlock { Text = $"on {targetName} with…", Opacity = 0.7, Margin = new Thickness(0, 2, 0, 12) });
        foreach (var option in options)
        {
            var content = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
            content.Children.Add(new TextBlock { Text = option.Label, FontSize = 14 });
            content.Children.Add(new TextBlock { Text = Describe(option), FontSize = 12, Opacity = 0.65 });
            var button = new RadioButton { Content = content, GroupName = "choice", Margin = new Thickness(0, 4, 0, 4), IsChecked = option == preselected };
            button.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
            _buttons.Add((button, option));
            panel.Children.Add(button);
        }

        _remember = new CheckBox { Content = $"Always use this for {appName}", IsChecked = true, Margin = new Thickness(0, 14, 0, 16) };
        _remember.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        panel.Children.Add(_remember);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        var ok = new Button { Content = "Continue", IsDefault = true, MinWidth = 110 };
        if (TryFindResource("AccentButton") is Style accent)
        {
            ok.Style = accent;
        }

        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }

    private HandoffChoice? Selected =>
        _buttons.FirstOrDefault(item => item.Button.IsChecked == true).Choice is { } choice ? choice with { Remember = _remember.IsChecked == true } : null;

    private static string Describe(HandoffChoice option) => option.Kind switch
    {
        ChoiceKinds.Default => "Same content, same second",
        ChoiceKinds.Web => option.Url?.Replace("https://", string.Empty).TrimEnd('/') ?? "Website",
        ChoiceKinds.Stream => "Live, with full control",
        _ => "App"
    };

    /// <summary>Asks and returns the pick (with <c>Remember</c> set as ticked); null when cancelled.</summary>
    public static HandoffChoice? Ask(string title, string appName, string targetName, IReadOnlyList<HandoffChoice> options, HandoffChoice? remembered)
    {
        var preselected = options.FirstOrDefault(option => remembered is not null
            && option.Kind == remembered.Kind && option.AppId == remembered.AppId && option.Url == remembered.Url) ?? options[0];
        var window = new ContinueWithWindow(title, appName, targetName, options, preselected);
        return window.ShowDialog() == true ? window.Selected : null;
    }
}
