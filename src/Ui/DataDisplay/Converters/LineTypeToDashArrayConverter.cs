using System;
using System.Globalization;
using Avalonia.Collections;
using Avalonia.Data.Converters;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.Converters;

/// <summary>
/// Maps a LineType enum value to an AvaloniaList&lt;double&gt; dash pattern
/// for use as a Shape.StrokeDashArray in the line-style glyph combo.
/// </summary>
public sealed class LineTypeToDashArrayConverter : IValueConverter
{
    public static readonly LineTypeToDashArrayConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LineType lt && lt == LineType.Dashed
            ? new AvaloniaList<double> { 4.0, 2.0 }
            : new AvaloniaList<double>();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => null;
}
