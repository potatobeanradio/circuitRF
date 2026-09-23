// The `.crail`'s own display unit (2026-09-23).
//
// ── WHY A RAIL DOCUMENT CARRIES A UNIT AT ALL ─────────────────────────────────────────────────
//
// Every length and coordinate railRF prints — the source and load rows, the mesh cell, the port
// lines, the breakdown, every message and refusal — used to read in the `.clay`'s display unit, so
// the only way to read a rail report in mils was to change a LAYOUT document other people draw in.
// The unit is now the `.crail`'s, seeded from the artwork when a board is first read and the user's
// to change from then on. It is how a number is SPELLED and nothing else: storage is DBU either way,
// so a change re-states every string and recomputes nothing.
//
// ── SEEDING IS NOT AN EDIT ─────────────────────────────────────────────────────────────────────
//
// A document written before this existed carries no unit, and the first board read supplies one. A
// window that was clean before that stays clean: the seed is the unit the window was ALREADY
// printing in, so nothing a user can see differs from what is on disk, and a dirty mark on an
// untouched document would be a question with no answer. It is written by the next save.

using System.Collections.Generic;
using CircuitRF.Design.Layout;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>The units the board toolbar's picker offers — the layout editor's own list.</summary>
    public static IReadOnlyList<LayoutUnit> AllDisplayUnits => Layout.LayoutEditorViewModel.AllUnits;

    /// <summary>
    /// The unit every length on this window is printed in — the <c>.crail</c>'s own, bound to the
    /// board toolbar's picker. Null only while there is no board to seed it from.
    /// </summary>
    /// <remarks>
    /// Document state, so it dirties the document; not an undo entry, for the reason the layout
    /// editor's own unit is not one — it is a view preference and moves no number.
    /// </remarks>
    public LayoutUnit? DisplayUnit
    {
        get => _document.DisplayUnit;
        set
        {
            // A ComboBox clears its selection to null while its items are being replaced; that is
            // not a request to forget the unit.
            if (value is not { } unit || _document.DisplayUnit == unit) return;

            _document.DisplayUnit = unit;
            OnPropertyChanged();
            RefreshIfUnitChanged();
            RefreshDirty();
        }
    }

    /// <summary>
    /// Gives an unseeded document the unit the artwork reads in — the layout's own display unit, or
    /// the technology's default where the board came from no layout document.
    /// </summary>
    private void SeedDisplayUnit(RailBoardInputs? board)
    {
        if (board is null || _document.DisplayUnit is not null) return;

        bool wasClean = !IsDirty;
        _document.DisplayUnit = board.View?.DisplayUnit ?? board.Technology.DefaultDisplayUnit;
        if (wasClean) CaptureSnapshot();

        OnPropertyChanged(nameof(DisplayUnit));
    }
}
