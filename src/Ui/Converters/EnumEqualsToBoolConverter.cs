using System.Globalization;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Returns <c>true</c> when the bound enum value's <c>ToString()</c> matches the
/// <c>ConverterParameter</c> string.  Used to drive <c>Classes.ToolActive</c> on
/// toolbar buttons so the active tool is highlighted without code-behind.
/// </summary>
public sealed class EnumEqualsToBoolConverter : IValueConverter
{
    public static readonly EnumEqualsToBoolConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(EnumEqualsToBoolConverter)} does not support ConvertBack.");
}
