using System.Globalization;
using System.Windows.Data;

namespace Deviant.Desktop;

public sealed class AudioMetadataTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        null => "—",
        int channels when channels <= 0 => "—",
        _ when string.Equals(value.ToString(), "Unknown", StringComparison.OrdinalIgnoreCase) => "—",
        _ => value.ToString() ?? "—"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
