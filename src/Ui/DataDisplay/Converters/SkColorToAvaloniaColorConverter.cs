using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SkiaSharp;

namespace CircuitRF.Ui.DataDisplay.Converters;

/// <summary>Converts <see cref="SKColor"/> to <see cref="SolidColorBrush"/> for XAML color-swatch bindings.</summary>
public sealed class SkColorToAvaloniaColorConverter : IValueConverter
{
    public static readonly SkColorToAvaloniaColorConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SKColor c
            ? new SolidColorBrush(new Color(c.Alpha, c.Red, c.Green, c.Blue))
            : (object?)new SolidColorBrush(Colors.Transparent);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SolidColorBrush sb) return new SKColor(sb.Color.R, sb.Color.G, sb.Color.B, sb.Color.A);
        if (value is Color c)            return new SKColor(c.R, c.G, c.B, c.A);
        return (object?)SKColors.Black;
    }
}
