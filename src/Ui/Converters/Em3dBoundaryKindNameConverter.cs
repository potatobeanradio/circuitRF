using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Displays an <see cref="Em3dBoundaryKind"/> as it is written in the field: PEC and PMC (perfect electric / magnetic
/// conductor) in capitals, not the enum's "Pec" and "Pmc". Same shape as <see cref="PlanarPortKindNameConverter"/>: the
/// ComboBox's <c>SelectedItem</c> stays bound to the enum, so nothing persisted changes. View-only.
/// </summary>
public sealed class Em3dBoundaryKindNameConverter : IValueConverter
{
    public static readonly Em3dBoundaryKindNameConverter Instance = new();

    /// <summary>The one place a boundary kind's user-facing name is written down.</summary>
    public static string Label(Em3dBoundaryKind kind) => kind switch
    {
        Em3dBoundaryKind.Pec      => "PEC",
        Em3dBoundaryKind.Pmc      => "PMC",
        Em3dBoundaryKind.Symmetry => "Symmetry",
        _                         => "Absorbing",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Em3dBoundaryKind k ? Label(k) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(Em3dBoundaryKindNameConverter)} does not support ConvertBack.");
}
