using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Converts between decimal? (NumericUpDown.Value) and any numeric VM property (double, int, …).
/// ConvertBack returns AvaloniaProperty.UnsetValue when the field is empty or invalid so that
/// Avalonia skips the source update entirely — no exception text rendered.
/// </summary>
public sealed class NumericFieldConverter : IValueConverter
{
    public static readonly NumericFieldConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try   { return value is null ? null : (decimal?)System.Convert.ToDecimal(value); }
        catch { return null; }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal dec)
        {
            try   { return System.Convert.ChangeType(dec, targetType); }
            catch { return AvaloniaProperty.UnsetValue; }
        }
        return AvaloniaProperty.UnsetValue;
    }
}
