using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Baton.App;

/// <summary>A small dialog asking for one line of text, such as a new name for a phone.</summary>
internal sealed class TextPrompt : Window
{
    private readonly TextBox _input;
    private bool _accepted;

    private TextPrompt(string title, string hint, string initial)
    {
        Title = title;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        WindowBackdrop.Attach(this);

        _input = new TextBox { Text = initial, Margin = new Thickness(0, 12, 0, 6) };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Accept();
            }
        };

        var save = new Button { Content = "Save", MinWidth = 90, IsDefault = true };
        save.SetResourceReference(StyleProperty, "AccentButton");
        save.Click += (_, _) => Accept();
        var cancel = new Button { Content = "Cancel", MinWidth = 90, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_input);
        panel.Children.Add(new TextBlock { Text = hint, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    /// <summary>The text entered (possibly empty), or null when cancelled.</summary>
    public static string? Ask(Window owner, string title, string hint, string initial)
    {
        var prompt = new TextPrompt(title, hint, initial) { Owner = owner };
        prompt.ShowDialog();
        return prompt._accepted ? prompt._input.Text.Trim() : null;
    }

    private void Accept()
    {
        _accepted = true;
        Close();
    }
}
