using System.Globalization;
using System.Windows.Data;

namespace Baton.App.Controls;

/// <summary>
/// Negates a boolean. Popup toggles bind IsHitTestVisible to !Popup.IsOpen so the click that closes
/// a light-dismiss popup does not immediately reopen it.
/// </summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
