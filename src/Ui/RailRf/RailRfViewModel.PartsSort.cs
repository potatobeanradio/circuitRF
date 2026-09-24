// The parts table's click-to-sort headers, and double-click-to-locate (owner, 2026-09-23).
//
// ── THREE STATES, AND THE THIRD IS THE DOCUMENT'S OWN ORDER ────────────────────────────────────
//
// A header click cycles ascending → descending → unsorted. Unsorted is not "whatever order the
// rows happen to be in": it is the .crail's own order, which is the order the rows were typed or
// the bill of materials listed them — the order the designer last CHOSE — so the table remembers it
// across every sort rather than having no way back to it.
//
// ── THE SORT IS A VIEW, NOT AN EDIT ────────────────────────────────────────────────────────────
//
// Nothing here touches RailSpec.Parts. The document's order is what gets saved and what `circuitrf
// rail` reports; a sort that rewrote it would make clicking a header an undoable edit and a dirty
// document, which is not what looking at a table in a different order is. And because RebuildParts
// runs on every solve, every part edit and every placement or BOM arriving, the order is applied
// THERE as well — a sort that fell away on the next re-solve would be a sort that does not work.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Schematic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CircuitRF.Ui.RailRf;

/// <summary>Which parts-table column the rows are sorted on.</summary>
public enum RailPartsSortColumn
{
    /// <summary>The document's own order.</summary>
    None,
    Refdes,
    PartNumber,
    Footprint,
    Capacitance,
    Esr,
    SelfResonance,
    Inductance,
    ModelSource,
    Location,
    Side,
}

public sealed partial class RailRfViewModel
{
    /// <summary>The rows as RebuildParts built them — the .crail's own order, kept so a third
    /// header click has something to go back to.</summary>
    private readonly List<RailPartRowViewModel> _partsInDocumentOrder = [];

    /// <summary>The column the table is sorted on; <see cref="RailPartsSortColumn.None"/> is the
    /// document's own order.</summary>
    [ObservableProperty]
    private RailPartsSortColumn _partsSortColumn = RailPartsSortColumn.None;

    /// <summary>Descending, on <see cref="PartsSortColumn"/>. Meaningless while that is None.</summary>
    [ObservableProperty]
    private bool _partsSortDescending;

    /// <summary>
    /// One header click: a new column sorts ascending, the same column again descending, and a third
    /// time back to the document's own order.
    /// </summary>
    public void SortParts(RailPartsSortColumn column)
    {
        if (column == RailPartsSortColumn.None || column != PartsSortColumn)
        {
            PartsSortColumn     = column;
            PartsSortDescending = false;
        }
        else if (!PartsSortDescending)
        {
            PartsSortDescending = true;
        }
        else
        {
            PartsSortColumn     = RailPartsSortColumn.None;
            PartsSortDescending = false;
        }

        // Re-seated by REFERENCE: the rows are the same objects, only their order changes. Clearing
        // the collection takes the ListBox's selection with it, and the two-way binding writes that
        // null back here — so the primary row is put back afterwards, the board mark with it.
        var wasSelected = SelectedPart;
        var ordered = OrderedParts();
        Parts.Clear();
        foreach (var row in ordered) Parts.Add(row);
        if (wasSelected is not null && Parts.Contains(wasSelected)) SelectedPart = wasSelected;
    }

    /// <summary>
    /// The rows in the order the header asks for. <b>A row with nothing in the sorted column goes
    /// LAST in both directions</b> — an unresolved capacitance is not the smallest one, and flipping
    /// the direction must not bring every unresolved row to the top. Ties keep the document's order.
    /// Names sort as a person reads them, C2 before C10.
    /// </summary>
    private List<RailPartRowViewModel> OrderedParts()
    {
        var rows = _partsInDocumentOrder;
        if (PartsSortColumn == RailPartsSortColumn.None) return [.. rows];

        int sign = PartsSortDescending ? -1 : 1;
        var names = SchematicSortPlacement.NaturalNameComparer.Instance;

        int Compare(RailPartRowViewModel a, RailPartRowViewModel b) => PartsSortColumn switch
        {
            RailPartsSortColumn.Refdes        => sign * names.Compare(a.Refdes, b.Refdes),
            RailPartsSortColumn.PartNumber    => Text(a.PartNumberSortKey, b.PartNumberSortKey),
            RailPartsSortColumn.Footprint     => Text(a.FootprintSortKey, b.FootprintSortKey),
            RailPartsSortColumn.Capacitance   => Number(a.CapacitanceSortKey, b.CapacitanceSortKey),
            RailPartsSortColumn.Esr           => Number(a.EsrSortKey, b.EsrSortKey),
            RailPartsSortColumn.SelfResonance => Number(a.SelfResonanceSortKey, b.SelfResonanceSortKey),
            RailPartsSortColumn.Inductance    => Number(a.InductanceSortKey, b.InductanceSortKey),
            RailPartsSortColumn.ModelSource   => Text(a.ModelSourceText, b.ModelSourceText),
            RailPartsSortColumn.Location      => Location(a.PositionDbu, b.PositionDbu),
            RailPartsSortColumn.Side          => a.BoardSideIndex.CompareTo(b.BoardSideIndex),
            _                                 => 0,
        };

        // Nulls last whatever the direction; the direction applies to the values only.
        int Text(string? x, string? y) =>
            x is null ? (y is null ? 0 : 1) : y is null ? -1 : sign * names.Compare(x, y);
        int Number(double? x, double? y) =>
            x is null ? (y is null ? 0 : 1) : y is null ? -1 : sign * x.Value.CompareTo(y.Value);
        // Left to right across the board, then by Y.
        int Location((long X, long Y)? x, (long X, long Y)? y) =>
            x is not { } p ? (y is null ? 0 : 1)
            : y is not { } q ? -1
            : sign * (p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

        // Equal keys fall back to the document's order, so a sort never shuffles ties.
        var index = rows.Select((r, i) => (r, i)).ToDictionary(p => p.r, p => p.i);
        return [.. rows.Order(Comparer<RailPartRowViewModel>.Create((a, b) =>
        {
            int c = Compare(a, b);
            return c != 0 ? c : index[a].CompareTo(index[b]);
        }))];
    }

    /// <summary>
    /// Double-click on a row's location: bring that part on screen on the board — the same camera
    /// move the breakdown's locator makes (<see cref="ShowOnBoardHook"/>).
    /// </summary>
    /// <returns>False where the board does not place the part, so there is nowhere to go — the same
    /// rule <see cref="PartHighlight"/> follows, which draws no mark for it either. The row already
    /// says "not placed" in the column that was clicked.</returns>
    public bool ShowPartOnBoard(RailPartRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!ReferenceEquals(SelectedPart, row)) SelectedPart = row;

        // The same mark the selection draws, so the camera frames exactly what is outlined.
        if (PartHighlight is { } mark && mark.Outline is { IsEmpty: false } outline)
        {
            ShowOnBoardHook?.Invoke(outline);
            return true;
        }

        return false;
    }
}
