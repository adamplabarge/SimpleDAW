using System.Globalization;
using System.Windows.Data;

namespace SimpleDAW;

/// <summary>Displays a zero-based channel index as a one-based number for the UI.</summary>
public sealed class PlusOneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i)
        {
            return (i + 1).ToString(culture);
        }

        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (int.TryParse(value?.ToString(), NumberStyles.Integer, culture, out int result))
        {
            return result - 1;
        }

        return Binding.DoNothing;
    }
}
