// One row of §2.4's ranked breakdown, as the results column lists it
// (docs/sonnet-briefs/brief-railrf-19-unreachable-states.md R-rail19-3).
//
// ── WHY THE BREAKDOWN IS A LIST NOW AND NOT A PARAGRAPH ────────────────────────────────────────
//
// It was a SelectableTextBlock over a joined string, which can be read and copied and not
// SELECTED — so "22.7 mV, 45 %, 26.5 mm of 0.209 mm BOT copper" named copper the reader had no way
// to find on the board beside it. The parts table, which is the less valuable of the two, already
// marked its selected row. PdnBreakdownRow.GroupKey was written for exactly this and says so; what
// was missing was a row to select.

using CircuitRF.Engine.Pdn;

namespace CircuitRF.Ui.RailRf;

/// <summary>One ranked breakdown row, and the sentence it reads as.</summary>
/// <param name="Row">The result's own row — <see cref="PdnBreakdownRow.GroupKey"/> is what maps it
/// back to copper.</param>
/// <param name="Text">Label, drop, share, resistance, current and the element count, formatted
/// once so the list and anything that prints it cannot disagree.</param>
public sealed record RailBreakdownRowViewModel(PdnBreakdownRow Row, string Text)
{
    /// <summary>The group key, for the locator.</summary>
    public string GroupKey => Row.GroupKey;

    public override string ToString() => Text;
}
