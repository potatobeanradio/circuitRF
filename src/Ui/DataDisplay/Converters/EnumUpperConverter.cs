using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.DataDisplay.Converters;

/// <summary>Displays an enum as its upper-cased ToString(). View-only; never write-back.
/// Used so TableOptimum {Mxp,Mxe} renders as MXP/MXE without renaming the persisted enum.</summary>
public sealed class EnumUpperConverter : IValueConverter
{
    public static readonly EnumUpperConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToUpperInvariant();
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
