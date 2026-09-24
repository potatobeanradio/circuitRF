// Mount and unmount, in the two places the designer reaches for
// (docs/sonnet-briefs/brief-railrf-23-mount-and-unmount.md R-rail23-2a … R-rail23-2d).
//
// ── THIS FILE OWNS NO STATE AND NO RULE ────────────────────────────────────────────────────────
//
// Every gesture below ends in ONE call to RailRfViewModel.SetPartsMounted, which rewrites the rows,
// rebuilds the table and re-solves once. What is here is the two gestures: a checkbox in the parts
// table's own row, and a row on the board's context menu — which is the one the designer actually
// asked for, because he was looking at the layout when he asked how to take a part off it.
//
// ── AND BOTH OF THEM ACT ON THE SELECTION ──────────────────────────────────────────────────────
//
// R-rail23-2c: "unmount these four and re-run" is the real gesture. So a click on a row that is
// part of a multi-selection acts on the whole selection, and a click on a row outside it acts on
// that row alone — which is what every list in every application does, and is the behaviour that
// makes the batch discoverable without a second control saying "apply to selection".

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.RailRf;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    /// <summary>R-rail23-2a — the checkbox in the parts table's own row.</summary>
    /// <remarks>
    /// The box is bound ONE WAY, so this handler is what actually moves the state. A two-way bind
    /// would have the row's own property written before the view model saw the gesture, and the row
    /// object is replaced by the rebuild that follows — so the tick would land on an object nobody
    /// holds any more and the table would refresh back to where it started.
    /// </remarks>
    private void OnPartMountToggled(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not CheckBox { DataContext: RailPartRowViewModel row } box) return;

        // The CheckBox has already flipped its own IsChecked by the time Click arrives; the row is
        // the authority on what the state WAS, so the target state is read from the row and the box
        // is put back under the binding by the rebuild.
        bool wanted = !row.IsMounted;
        box.IsChecked = row.IsMounted;

        vm.SetPartsMounted(TargetsFor(row), wanted);
    }

    /// <summary>
    /// The rows a gesture on <paramref name="row"/> acts on: the whole selection where that row is
    /// in it, and that row alone otherwise.
    /// </summary>
    private IReadOnlyList<string> TargetsFor(RailPartRowViewModel row)
    {
        var selected = PartsList.SelectedItems?
                                .OfType<RailPartRowViewModel>()
                                .Select(r => r.Refdes)
                                .Where(r => r.Length > 0)
                                .ToList() ?? [];

        return selected.Contains(row.Refdes, StringComparer.OrdinalIgnoreCase)
            ? selected
            : [row.Refdes];
    }

    /// <summary>
    /// R-rail23-2b — the board's <b>Unmount</b> / <b>Mount</b> rows, for the part under the click.
    /// </summary>
    /// <remarks>
    /// <b>Only where the click lands on a part this rail carries a row for.</b> A pad belonging to
    /// something that is not on this rail's parts list has no mount state to change, and a row
    /// offering to change one would be a row that does nothing.
    ///
    /// <para>It acts on the parts-table selection for the same reason the checkbox does, so
    /// selecting four rows and right-clicking one of them on the board unmounts all four in one
    /// re-solve.</para>
    /// </remarks>
    private IReadOnlyList<object> MountRowsFor(string? refdes)
    {
        if (Vm is not { } vm) return [];
        if (refdes is not { Length: > 0 }) return [];
        if (vm.Parts.FirstOrDefault(
                p => string.Equals(p.Refdes, refdes, StringComparison.OrdinalIgnoreCase))
            is not { } row) return [];

        var targets = TargetsFor(row);
        bool wanted = !row.IsMounted;

        string what = targets.Count == 1
            ? targets[0]
            : $"{targets.Count} parts";

        var item = new MenuItem { Header = (wanted ? "Mount " : "Unmount ") + what };
        ToolTip.SetTip(item, row.MountTooltip);
        item.Click += (_, _) => vm.SetPartsMounted(targets, wanted);

        return [item];
    }

    /// <summary>
    /// A row's top/bottom combo (owner, 2026-09-24) — one call to
    /// <see cref="RailRfViewModel.SetPartsBoardSide"/>, on the whole selection where the row is in
    /// it, the checkbox's own rule.
    /// </summary>
    private void OnPartSideChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is not { } vm || sender is not ComboBox { DataContext: RailPartRowViewModel row } box) return;
        if (box.SelectedIndex < 0 || box.SelectedIndex == row.BoardSideIndex) return;

        vm.SetPartsBoardSide(TargetsFor(row),
            box.SelectedIndex == 1 ? CircuitRF.Design.RailRf.RailBoardSide.Bottom : CircuitRF.Design.RailRf.RailBoardSide.Top);
    }
}
