using System.Globalization;
using System.Windows;
using System.Windows.Data;
// This project sets <UseWindowsForms>true</UseWindowsForms> (tray NotifyIcon), so WPF types that
// share a name with a WinForms type (Binding, etc.) are ambiguous without an explicit alias -- the
// same gotcha called out in app/CLAUDE.md for every file that touches these types.
using Binding = System.Windows.Data.Binding;

namespace EveDeck.Converters;

// Visible when the bound int equals the ConverterParameter index, Collapsed otherwise. Used by the
// Options tab's left-menu master-detail so each section shows only for its own menu row.
public sealed class IndexToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var index = value is int i ? i : -1;
        var target = parameter is string s && int.TryParse(s, out var p) ? p : -2;
        return index == target ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
