using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Displays any bound value (typically an enum, e.g. <c>LayoutUnit</c>) as the invariant-culture
/// lower-cased form of its <c>ToString()</c>. View-only — never write-back — so it is safe to use
/// as a ComboBox <c>ItemTemplate</c> converter over a value still bound (via the item itself, not
/// this converter) two-way to the underlying enum property. Never renames the persisted enum.
/// </summary>
public sealed class ToLowerStringConverter : IValueConverter
{
    public static readonly ToLowerStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString()?.ToLowerInvariant();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(ToLowerStringConverter)} does not support ConvertBack.");
}
