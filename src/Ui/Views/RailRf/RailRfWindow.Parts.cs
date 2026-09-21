// "Which part is this?" — the two gestures brief 27 puts where the work is
// (docs/sonnet-briefs/brief-railrf-27-gerber-set-to-a-curve.md R-rail27-3a, R-rail27-3c).
//
// ── WHY THIS IS NOT "MAKING THE PARTS TABLE EDITABLE" ──────────────────────────────────────────
//
// RailPartRowViewModel is read-only deliberately: "a row that could be edited here would be a second
// place the same number lives". That rule is about the MODEL — capacitance, ESR, f0 — which belongs
// to the part library. A part NUMBER is not a model value. It is the row's own field on the document
// (RailPart.PartNumber), it is what the library is keyed BY, and choosing it is choosing WHICH
// library row applies. Every electrical column stays read-only and stays the library's.
//
// ── AND WHY IT IS PER SELECTION ────────────────────────────────────────────────────────────────
//
// After brief 26 a board with no bill of materials yields rows with a refdes, a position and a
// computed mounting loop, and no part number, because discovery refuses to invent one from a land
// pattern. With no BOM that is EVERY row, all listed as unresolved, and the |Z| curve has no
// decoupling in it. The table is right and the answer is still empty. The missing gesture is the one
// a designer expects: select the rows that are the same part and say which part they are — which is
// why the footprint column now falls back to the board's own land pattern (R-rail27-3b), because
// that is what tells you which rows those ARE.
//
// This file owns no state and no rule: both gestures end in one call the view model or the workspace
// already has, on RailRfWindow.Mount.cs's terms.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CircuitRF.Ui.RailRf;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.RailRf;

public partial class RailRfWindow
{
    /// <summary>R-rail27-3a — the parts table's context menu, filled on Opening.</summary>
    /// <remarks>
    /// ONE <c>ContextMenu</c> instance, filled here — the shape the board canvas and the title bar
    /// already use, because a <c>ContextMenu</c> is a popup with its own lifetime and constructing a
    /// new one per open leaks the old.
    /// </remarks>
    private void OnPartsContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        if (Vm is not { } vm || vm.Parts.Count == 0) { e.Cancel = true; return; }

        var row = PartsList.SelectedItem as RailPartRowViewModel
               ?? PartsList.SelectedItems?.OfType<RailPartRowViewModel>().FirstOrDefault();
        if (row is null) { e.Cancel = true; return; }

        var targets = TargetsFor(row);

        var assign = new MenuItem
        {
            Header = targets.Count == 1
                ? $"Assign part number to {targets[0]}…"
                : $"Assign part number to {targets.Count} parts…",
        };
        ToolTip.SetTip(assign,
            "Write one internal part number onto these rows. It is what the part library is keyed "
            + "by — the capacitance, the ESR and the self-resonance stay the library's.");
        assign.Click += (_, _) => AssignPartNumberTo(targets, row.PartNumber);

        var library = new MenuItem { Header = "Create part library…" };
        ToolTip.SetTip(library,
            "Seed a .crlib from the part numbers this document names, and open it. Until a part "
            + "number resolves to a capacitance and a self-resonance the row contributes nothing to "
            + "the curve.");
        library.Click += (_, _) => CreatePartLibrary();

        menu.ItemsSource = new List<object> { assign, new Separator(), library };
    }

    /// <summary>The pane's own button for R-rail27-3c.</summary>
    private void OnCreatePartLibraryClick(object? sender, RoutedEventArgs e) => CreatePartLibrary();

    /// <summary>The pane's own button — the same gesture for a user who is not right-clicking.</summary>
    private void OnAssignPartNumberClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var row = PartsList.SelectedItem as RailPartRowViewModel
               ?? PartsList.SelectedItems?.OfType<RailPartRowViewModel>().FirstOrDefault();

        // R-rail22-2a: no silent return from a visible control. The button is always there, so a
        // press with nothing selected says what to do rather than doing nothing.
        if (row is null)
        {
            vm.Refusal = new RailRefusal(
                "Select the rows that are the same part first — a part number is written onto the "
                + "selection, and there is nothing selected.", RailRefusalControl.None);
            return;
        }

        AssignPartNumberTo(TargetsFor(row), row.PartNumber);
    }

    private async void AssignPartNumberTo(IReadOnlyList<string> refdeses, string? current)
    {
        if (Vm is not { } vm) return;

        var dialog = new RailAssignPartNumberDialog(
            refdeses, vm.KnownPartNumbers,
            // The row prints "unresolved" where it has none; that word is not a part number and
            // must not be pre-filled into the field as though somebody had typed it.
            current is { Length: > 0 } && current != RailPartRowViewModel.UnresolvedText ? current : null);

        // Null is CANCEL; empty is an answer that clears the number, which is the state a discovered
        // row starts in — so a mistaken assignment is taken back by the same gesture.
        if (await dialog.ShowDialog<string?>(this) is not { } assigned) return;

        vm.AssignPartNumber(refdeses, assigned);
    }

    /// <summary>
    /// R-rail27-3c — the existing <b>Create part library…</b> command, put where the work is.
    /// </summary>
    /// <remarks>
    /// <b>No second route.</b> This is the workspace's own <c>CreatePartLibraryForRailDocument</c>,
    /// which the project tree already calls — it seeds the <c>.crlib</c> from this document's own
    /// part numbers, points the <c>.crail</c> at it and opens the editor. What is new is only that
    /// it is reachable from the pane where the part numbers were just assigned.
    ///
    /// <para>railRF is an unowned window that outlives the workspace behind it, so every refusal is
    /// stated rather than returned silently — <see cref="OnBoardEditTechnology"/>'s rule.</para>
    /// </remarks>
    private async void CreatePartLibrary()
    {
        if (Vm is not { } vm) return;

        if (vm.DocumentPath is not { Length: > 0 } crail)
        {
            vm.Refusal = new RailRefusal(
                "Save this document first — a part library is written beside the .crail and named "
                + "by it, and this one has no path yet.", RailRefusalControl.None);
            return;
        }

        if (WorkspaceLocator.Any() is not { } workspace)
        {
            vm.Refusal = new RailRefusal(
                "A part library is created in a workspace, and this railRF window has none open "
                + "behind it. Open the workspace this design belongs to and try again.",
                RailRefusalControl.None);
            return;
        }

        var name = new InputNameDialog(
            "New Part Library", "Part library name:", Path.GetFileNameWithoutExtension(crail));
        if (await name.ShowDialog<string?>(this) is not { } chosen) return;

        string? crlib = workspace.CreatePartLibraryForRailDocument(crail, chosen, out string? error);

        if (error is { Length: > 0 })
        {
            vm.Refusal = new RailRefusal(error, RailRefusalControl.None);
            if (crlib is null) return;
        }
        if (crlib is null) return;

        workspace.OpenOrActivatePartLibrary(crlib);
        WorkspaceLocator.WindowFor(workspace)?.Activate();
    }
}
