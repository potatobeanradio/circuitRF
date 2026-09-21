using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Views.Palette;

/// <summary>
/// The arithmetic behind "the Library palette stays a whole number of component glyphs wide" — kept
/// apart from the control that applies it (<see cref="PaletteColumnPin"/>) so it can be tested, since
/// <c>tests/Ui.Tests</c> has no Avalonia platform to lay a real dock out on.
///
/// <para><b>A glyph count is a WIDTH, and a docked panel is a PROPORTION.</b> Dock arranges the
/// workspace's columns as fractions of the window, so the palette's pixel width is whatever its
/// fraction happens to come to — which is why the shipped default was close to two glyphs at the
/// opening window size and drifted off it the moment anyone resized the window. Everything here
/// converts between the two: how many whole glyph columns a width is showing, what width shows that
/// many, what fraction arranges it, and how the column beside it gives that width back.</para>
///
/// <para><b>The chrome is measured, never assumed.</b> The gap between the column's outer edge and
/// the tile area is the dock theme's borders — 2 px as the Fluent theme stands, but a theme change
/// or a padding tweak would move it, and a number baked in here would be wrong silently (the panel
/// would show one column of glyphs with a column's worth of empty space beside it). The caller reads
/// it off the live panel and hands it in.</para>
/// </summary>
public static class PaletteColumnWidth
{
    /// <summary>
    /// One tile's slot in the palette's <c>WrapPanel</c>: <c>PaletteTile.axaml</c>'s 60 px tile plus
    /// its 1 px margin either side. Measured 62 x 72 in the running panel;
    /// <c>PaletteTileMetricsTests</c> holds the XAML to it.
    /// </summary>
    public const double GlyphSlotWidth = 62.0;

    /// <summary>The number of glyph columns the Library OPENS at. Only the opening width uses it —
    /// after that the count is whatever the user has dragged the palette to.</summary>
    public const int DefaultGlyphColumns = 2;

    /// <summary>
    /// Dock arranges a child at <c>floor(available x proportion)</c> (plus a shared fractional-pixel
    /// carry that can only ever add one), so a proportion that works out to exactly the target can
    /// land a pixel SHORT of it through nothing worse than double-rounding — and one pixel short of
    /// N glyph slots is N-1 glyph columns, not N. Aiming half a pixel high costs nothing visible and
    /// cannot round down.
    /// </summary>
    private const double RoundingGuard = 0.5;

    /// <summary>The column width that gives <paramref name="glyphColumns"/> whole glyph columns.</summary>
    /// <param name="chrome">Column width minus tile-area width, read off the live panel.</param>
    public static double TargetWidth(double chrome, int glyphColumns = DefaultGlyphColumns)
        => Math.Ceiling(Math.Max(0.0, chrome)) + glyphColumns * GlyphSlotWidth;

    /// <summary>
    /// How many whole glyph columns a tile area of this width is SHOWING — which is the count the
    /// palette has to go on showing when the window is resized.
    ///
    /// <para><b>Floor, deliberately, not nearest.</b> The count has to be the one on screen: a tile
    /// area four-fifths of the way to a fifth column is showing four, and rounding it up would add a
    /// column the user never asked for at the moment the pin first took hold. Flooring can only ever
    /// take back the leftover strip, which is space no glyph was in.</para>
    ///
    /// <para>Zero means "not showing a whole column at all" — a palette dragged narrower than one
    /// glyph. There is no count to preserve there, so the caller leaves it to scale as it did.</para>
    /// </summary>
    public static int GlyphColumnsIn(double tileAreaWidth)
        => double.IsFinite(tileAreaWidth) && tileAreaWidth > 0.0
            ? (int)Math.Floor(tileAreaWidth / GlyphSlotWidth)
            : 0;

    /// <summary>The proportion that arranges <paramref name="targetWidth"/> out of the pool.</summary>
    /// <param name="available">The panel's width less its splitters — what proportions divide up.</param>
    public static double ProportionFor(double targetWidth, double available)
        => (targetWidth + RoundingGuard) / available;

    /// <summary>
    /// Re-proportions one row of docked columns so the one at <paramref name="index"/> arranges
    /// <paramref name="targetWidth"/> pixels wide, taking the difference out of the column beside it.
    ///
    /// <para><b>The neighbour has to absorb it, and only the neighbour.</b> Dock normalizes a row's
    /// proportions to sum to one before it arranges them, so raising the palette's share on its own
    /// is scaled straight back down and nothing happens. Spreading the difference over every other
    /// column instead would work, but it would twitch the Project Tree's width every time the window
    /// moved; one neighbour absorbing it is what a splitter drag does, and it leaves every other
    /// column exactly where the user put it.</para>
    /// </summary>
    /// <param name="proportions">The row's NON-SPLITTER children, in order.</param>
    /// <param name="index">Which of them is the palette's column.</param>
    /// <param name="available">The panel's width less its splitters.</param>
    /// <param name="targetWidth">What that column should arrange to.</param>
    /// <param name="pinned">The replacement proportions, in the same order.</param>
    /// <returns>False when there is nothing to do, or no room to do it — leave the layout alone.</returns>
    public static bool TryPin(
        IReadOnlyList<double> proportions, int index, double available, double targetWidth,
        out double[] pinned)
    {
        pinned = [];

        if (!HasRoomFor(proportions, index, available, targetWidth)) return false;

        double want = ProportionFor(targetWidth, available);
        double have = proportions[index];

        // Already there. Measured in PIXELS rather than in proportion, because the same proportional
        // difference is nothing at one window size and a visible step at another.
        if (Math.Abs(have - want) * available < 0.5) return false;

        int neighbour = NeighbourOf(proportions, index);

        var result = proportions.ToArray();
        result[index]     = want;
        result[neighbour] = proportions[neighbour] - (want - have);
        pinned = result;
        return true;
    }

    /// <summary>
    /// Whether this row CAN give the palette <paramref name="targetWidth"/> — the whole of
    /// <see cref="TryPin"/>'s refusal test, minus the one refusal that is not a refusal (a column
    /// already at its target has room by definition, and returns true here).
    ///
    /// <para><b>The difference matters, which is why the test is separable.</b> A pin REFUSED for
    /// want of room leaves the palette scaled to whatever share of a too-narrow window it had — a
    /// width that is not the count the user set. <c>PaletteColumnPin</c> reads no count outside a
    /// splitter drag, so that width is never mistaken for one; when the window is widened again the
    /// count it still holds is applied and the columns come back.</para>
    /// </summary>
    public static bool HasRoomFor(
        IReadOnlyList<double> proportions, int index, double available, double targetWidth)
    {
        if (proportions is null || proportions.Count < 2) return false;
        if (index < 0 || index >= proportions.Count) return false;
        if (!(available > 0.0) || !(targetWidth > 0.0) || targetWidth >= available) return false;
        if (proportions.Any(p => !double.IsFinite(p))) return false;

        double want = ProportionFor(targetWidth, available);
        double absorbed = proportions[NeighbourOf(proportions, index)] - (want - proportions[index]);
        return absorbed > 0.0;
    }

    /// <summary>
    /// The column that absorbs the difference: the next one along, or the previous one when the
    /// palette is last — which it is in the shipped right-hand arrangement. Either way this is the
    /// document column.
    /// </summary>
    private static int NeighbourOf(IReadOnlyList<double> proportions, int index)
        => index + 1 < proportions.Count ? index + 1 : index - 1;
}
