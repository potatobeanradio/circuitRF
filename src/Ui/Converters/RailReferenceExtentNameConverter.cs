using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Displays a <see cref="RailReferenceExtent"/> as a name rather than as its identifier.
/// </summary>
/// <remarks>
/// <b>The combo was showing <c>AsImported</c> and <c>FilledToOutline</c></b> — the enum's own member
/// names, which is what Avalonia renders for an enum with no item template (owner, 2026-09-18).
///
/// <para><b>Each label carries its own warning, because two of the three values are OPTIMISTIC</b> and
/// a user reads this list at the moment of choosing. "Filled to outline" alone says what it does and
/// not what it costs; the parenthesis is the half that makes the choice an informed one, and the
/// status strip then restates whichever is in force on every frame.</para>
///
/// <para><b>This is not what a report says.</b> <c>RailRfViewModel</c>'s status strip and
/// <c>circuitrf rail</c>'s provenance both write a lower-case sentence fragment — "reference filled to
/// outline — optimistic" — which is a different job from a control's label and stays where it is. The
/// <see cref="RailReferenceExtent"/> enum is the one thing both agree on and the only thing either
/// persists; nothing here can reach the <c>.crail</c>.</para>
/// </remarks>
public sealed class RailReferenceExtentNameConverter : IValueConverter
{
    public static readonly RailReferenceExtentNameConverter Instance = new();

    /// <summary>The one place a reference extent's control label is written down.</summary>
    public static string Label(RailReferenceExtent extent) => extent switch
    {
        RailReferenceExtent.AsImported      => "As imported",
        RailReferenceExtent.FilledToOutline => "Filled to outline (optimistic)",
        _                                   => "Infinite (an upper bound)",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RailReferenceExtent e ? Label(e) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException(
            $"{nameof(RailReferenceExtentNameConverter)} does not support ConvertBack.");
}
