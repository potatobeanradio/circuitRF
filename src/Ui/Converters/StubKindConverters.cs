using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CircuitRF.Ui.ViewModels.Dock;

namespace CircuitRF.Ui.Converters;

public class StubKindIsSchematicConverter : IValueConverter
{
    public static readonly StubKindIsSchematicConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StubDocument.StubKind kind && kind == StubDocument.StubKind.Schematic;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StubKindIsDataDisplayConverter : IValueConverter
{
    public static readonly StubKindIsDataDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is StubDocument.StubKind kind && kind == StubDocument.StubKind.DataDisplay;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
