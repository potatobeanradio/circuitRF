using System;
using System.Globalization;
using Avalonia.Media;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The two pure decisions every inline value editor in this application makes: how wide the box has
/// to be for the text it holds, and how much of the seeded text is the VALUE as opposed to its unit.
/// </summary>
/// <remarks>
/// <b>One implementation, three call sites.</b> harmonicaRF's readout strip
/// (<c>ReadoutStripView.BeginInlineEdit</c>) grew these first; the schematic editor has its own
/// VM-side selection rule (<c>SchematicViewModel.InlineEditSelLength</c>); the Match Designer's
/// specification pane is the third (owner, 2026-08-19: "harmonicaRF has already implemented this for
/// a UI panel — please reuse it, it selects units within the text properly"). Both halves are pure
/// functions of strings and a typeface, which is what makes them shareable at all — the hosting
/// machinery (a floating overlay in the strip, an in-place swap in the Designer) is genuinely
/// different in each and is deliberately NOT shared.
///
/// <para><b>The width is a MEASUREMENT, never a character count.</b> An assumed per-character advance
/// is wrong by a different amount for every non-ASCII glyph these fields render — "Ω", "µ", "∞" — and
/// by a different amount again at every non-integer font size.</para>
/// </remarks>
public static class InlineEdit
{
    /// <summary>
    /// How many leading characters of <paramref name="text"/> are the value, so an editor can
    /// pre-select the number and leave the unit alone: type over the digits, keep the "pF".
    /// </summary>
    /// <remarks>
    /// The unit is whatever follows the LAST space, and only when what follows is not itself numeric
    /// — "1.5 nH" selects "1.5", while a bare "1.5" or a unitless "12" selects the whole thing. A
    /// leading sign, digits, a decimal point and an exponent all count as part of the value, so
    /// "-1.5e-9 F" still selects only "-1.5e-9".
    /// </remarks>
    public static int ValueSelectionLength(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int space = text.TrimEnd().LastIndexOf(' ');
        if (space <= 0) return text.Length;

        string tail = text[(space + 1)..].Trim();
        if (tail.Length == 0) return text.Length;
        if (double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return text.Length;   // "1 000" is not a value and a unit
        return space;
    }

    /// <summary>
    /// How wide an inline editor should be for <paramref name="text"/> at <paramref name="fontSize"/>
    /// — an actual measurement against <paramref name="typeface"/> plus a caret's worth of slack,
    /// with a floor of two characters so an empty box stays clickable rather than a sliver.
    /// </summary>
    public static double MeasureWidth(string? text, double fontSize, Typeface typeface)
    {
        string measured = string.IsNullOrEmpty(text) ? "0" : text;
        var formatted = new FormattedText(measured, CultureInfo.InvariantCulture,
                                          FlowDirection.LeftToRight, typeface, fontSize, Brushes.Black);
        double textWidth = string.IsNullOrEmpty(text) ? 0 : formatted.Width;
        return Math.Max(fontSize * 2.0, textWidth + fontSize * 0.8);
    }
}
