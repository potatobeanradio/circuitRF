using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.ViewModels;

public sealed class BoolToIndentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new Thickness(20, 0, 0, 0) : new Thickness(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
