using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EveDeck.Converters;

// Bool -> Visible/Collapsed, with ConverterParameter="invert" flipping the sense.
//
// WPF ships BooleanToVisibilityConverter, but it has no inverse, and the common need is to show one
// element when a flag is true and a different one when it is false (e.g. the run-at-login checkbox
// versus the "the Store version manages startup for you" note). Registered in App.Main rather than
// referenced from XAML via a local clr-namespace -- see the comment there for why that matters to
// build times.
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("BoolToVisibilityConverter is one-way.");
}
