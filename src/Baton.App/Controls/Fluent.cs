using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Baton.App.Controls;

/// <summary>Attached values the Fluent control templates read, such as a text box placeholder.</summary>
public static class Fluent
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Fluent), new FrameworkPropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Fluent), new FrameworkPropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element) => (string)element.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject element, string value) => element.SetValue(PlaceholderProperty, value);

    /// <summary>A Segoe Fluent Icons glyph shown inside the control, for example the search glass.</summary>
    public static string GetIcon(DependencyObject element) => (string)element.GetValue(IconProperty);

    public static void SetIcon(DependencyObject element, string value) => element.SetValue(IconProperty, value);

    /// <summary>
    /// Clips an element to a rounded rectangle that follows its size. Border.CornerRadius does not
    /// clip content, so images inside rounded cards use this instead.
    /// </summary>
    public static readonly DependencyProperty ClipRadiusProperty = DependencyProperty.RegisterAttached(
        "ClipRadius", typeof(double), typeof(Fluent), new PropertyMetadata(0.0, OnClipRadiusChanged));

    public static double GetClipRadius(DependencyObject element) => (double)element.GetValue(ClipRadiusProperty);

    public static void SetClipRadius(DependencyObject element, double value) => element.SetValue(ClipRadiusProperty, value);

    private static void OnClipRadiusChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement frameworkElement)
        {
            return;
        }

        frameworkElement.SizeChanged -= UpdateClip;
        frameworkElement.SizeChanged += UpdateClip;
        ApplyClip(frameworkElement);
    }

    private static void UpdateClip(object sender, SizeChangedEventArgs e) => ApplyClip((FrameworkElement)sender);

    private static void ApplyClip(FrameworkElement element)
    {
        var radius = GetClipRadius(element);
        element.Clip = new RectangleGeometry(new Rect(element.RenderSize), radius, radius);
    }

    /// <summary>
    /// Actions a button opens as a menu below itself when clicked, like a card's "⋯" button.
    /// Items are <see cref="ActivityAction"/>s.
    /// </summary>
    public static readonly DependencyProperty MenuActionsProperty = DependencyProperty.RegisterAttached(
        "MenuActions", typeof(IEnumerable), typeof(Fluent), new PropertyMetadata(null, OnMenuActionsChanged));

    public static IEnumerable? GetMenuActions(DependencyObject element) => (IEnumerable?)element.GetValue(MenuActionsProperty);

    public static void SetMenuActions(DependencyObject element, IEnumerable? value) => element.SetValue(MenuActionsProperty, value);

    private static void OnMenuActionsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is ButtonBase button)
        {
            button.Click -= OpenMenu;
            button.Click += OpenMenu;
        }
    }

    private static void OpenMenu(object sender, RoutedEventArgs e)
    {
        var button = (ButtonBase)sender;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var action in GetMenuActions(button)?.OfType<ActivityAction>() ?? [])
        {
            menu.Items.Add(new MenuItem { Header = action.Label, Icon = action.Glyph, Command = action.Command });
        }

        menu.IsOpen = menu.Items.Count > 0;
        e.Handled = true;
    }
}
