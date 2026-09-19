// A results card's lines, as ONE selectable block of text (owner, 2026-09-19).
//
// ── WHY A CONVERTER AND NOT SEVEN MORE VIEW-MODEL PROPERTIES ────────────────────────────────────
//
// The ask: every bulk text result railRF renders — the stackup line, the drop, the breakdown, the
// via check, the aggressor coincidences, the mask verdict, the anti-resonances, the removal
// ranking, the plane modes — has to be SELECTABLE, so it can be copied out of the window and
// pasted into an email.
//
// Avalonia's `SelectableTextBlock` is the element that does that, and it selects within ONE
// element: a card built as a list of one text block per row lets a reader copy a line and not a
// card, which is not what "paste it into an email" means. So each card becomes one text block over
// the whole card's text.
//
// Doing that by adding a `…Text` property beside each `…Lines` one would also mean adding each new
// property to the ten places its source is re-stated (`OnPropertyChanged(nameof(PortLines))` and
// its nine siblings) — ten places to forget, and forgetting one produces a card that silently
// stops updating while looking exactly like a card that has nothing new to say. A converter over
// the EXISTING property has none of that: the binding re-evaluates on the notification that is
// already there.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Joins a sequence of lines into one string, one line each — what a
/// <c>SelectableTextBlock</c> shows in place of a list of them.
/// </summary>
public sealed class RailLinesToTextConverter : IValueConverter
{
    /// <summary>The one instance, reached from AXAML as <c>{x:Static …Instance}</c> — the same
    /// spelling <c>RailReferenceExtentNameConverter</c> already uses in this window.</summary>
    public static readonly RailLinesToTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            null                       => "",
            string s                   => s,
            IEnumerable<string> lines  => string.Join(Environment.NewLine, lines),
            IEnumerable other          => string.Join(Environment.NewLine,
                                                      other.Cast<object?>().Select(x => x?.ToString() ?? "")),
            _                          => value.ToString() ?? "",
        };

    /// <summary>One way. A card is a rendering of a result and nothing types into it.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException(
            "RailLinesToTextConverter is one-way: a results card renders a computed list and "
          + "nothing writes back through it.");
}
