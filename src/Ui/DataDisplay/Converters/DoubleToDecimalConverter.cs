using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.DataDisplay.Converters;

/// <summary>
/// Converts between double (ViewModel) and decimal? (NumericUpDown.Value).
/// </summary>
public sealed class DoubleToDecimalConverter : IValueConverter
{
    public static readonly DoubleToDecimalConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? (decimal?)d : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is decimal dec ? (object?)(double)dec : null;
}
