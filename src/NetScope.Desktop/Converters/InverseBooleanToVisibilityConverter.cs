using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NetScope.Desktop.Converters;

/// <summary>Collapsed when true, Visible when false — for empty-state hints.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
