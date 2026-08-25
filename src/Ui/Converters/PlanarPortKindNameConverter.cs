using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Displays a <see cref="PlanarPortKind"/> as the name the USER knows it by — "Edge" and
/// "Internal delta gap" rather than the enum's own <c>InternalDeltaGap</c>.
///
/// <para>Same shape and same reason as <see cref="EmAnalysisKindNameConverter"/> beside it: the
/// ComboBox's <c>SelectedItem</c> stays bound to the enum, so nothing here can affect what is
/// persisted in the <c>.cem</c>. View-only.</para>
/// </summary>
public sealed class PlanarPortKindNameConverter : IValueConverter
{
    public static readonly PlanarPortKindNameConverter Instance = new();

    /// <summary>The one place a port kind's user-facing name is written down.</summary>
    public static string Label(PlanarPortKind kind) => kind switch
    {
        PlanarPortKind.InternalDeltaGap => "Internal delta gap",
        _                               => "Edge",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PlanarPortKind k ? Label(k) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(PlanarPortKindNameConverter)} does not support ConvertBack.");
}
