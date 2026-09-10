using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Displays a <see cref="PlanarCurrentModel"/> as the name the USER knows it by.
///
/// <para><b>Every label names the STRUCTURE, not the algorithm</b> — ANT-3's own instruction, and
/// the reason it matters here more than elsewhere: the row reads "This metal is …", and the user is
/// being asked what they drew. "Follow the direction of current" — the checkbox this replaced —
/// describes what the mesher will then do about it instead. Someone who knows they have drawn a
/// patch can answer "a radiating sheet"; the same person has no view on a pitch field. That is also
/// why <see cref="PlanarCurrentModel.None"/> is "Unstated" rather than "Off": nothing is switched
/// off, the question simply has not been answered, and the per-axis rule is what happens then.</para>
///
/// <para>Same shape and same reason as <see cref="PlanarPortKindNameConverter"/> beside it: the
/// ComboBox's <c>SelectedItem</c> stays bound to the enum, so nothing here can affect what is
/// persisted in the <c>.cem</c>. View-only.</para>
/// </summary>
public sealed class PlanarCurrentModelNameConverter : IValueConverter
{
    public static readonly PlanarCurrentModelNameConverter Instance = new();

    /// <summary>The one place a current model's user-facing name is written down.</summary>
    public static string Label(PlanarCurrentModel model) => model switch
    {
        PlanarCurrentModel.TransmissionLine => "Transmission line",
        PlanarCurrentModel.Sheet            => "Radiating sheet",
        _                                   => "Unstated",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is PlanarCurrentModel m ? Label(m) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException(
            $"{nameof(PlanarCurrentModelNameConverter)} does not support ConvertBack.");
}
