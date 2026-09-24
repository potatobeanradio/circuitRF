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
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        if (Vm is not { } vm) { e.Cancel = true; return; }

        // ── AN EMPTY TABLE STILL HAS A MENU, AND IT HAS THE ONE GESTURE THAT MATTERS THERE ─────
        //
        // It used to cancel on zero rows, which meant the pane a designer was staring at — empty,
        // after placing two footprints — answered a right-click with nothing at all (field report,
        // 2026-09-22). Add is exactly the gesture for an empty table, so the menu opens carrying it
        // and nothing else; the two that act on a SELECTION still need one and still cancel without.
        var add = new MenuItem { Header = "Add parts…" };
        ToolTip.SetTip(add,
            "Put rows on this rail, picked from the designators the board places or typed. A row "
            + "arrives carrying its designator and nothing else.");
        add.Click += (_, _) => OnAddPartClick(null, new RoutedEventArgs());

        // Brief 35 (R-rail35-1a): a part the board shows spanning the rail is offered AS a series
        // element, on the menu discovery's own sentence names — with or without a selection.
        var seriesOffers = vm.PartOffer.SeriesOffered.Select(part =>
        {
            var item = new MenuItem { Header = $"Add {part.Refdes} as series element" };
            ToolTip.SetTip(item,
                $"{part.Refdes} has both pads on this rail's copper. Add it as the element the rail "
                + "runs THROUGH — its two pads become its terminals, and the rail is cut there.");
            item.Click += (_, _) => vm.AddSeriesParts([part.Refdes]);
            return (object)item;
        }).ToList();

        var row = PartsList.SelectedItem as RailPartRowViewModel
               ?? PartsList.SelectedItems?.OfType<RailPartRowViewModel>().FirstOrDefault();
        if (row is null)
        {
            if (vm.SelectedRail is null) { e.Cancel = true; return; }
            List<object> empty = seriesOffers.Count == 0 ? [add] : [add, new Separator(), .. seriesOffers];
            // The library's gestures need no selection — they act on the whole document — so they are
            // here too; on an empty table this menu is the only way to them, since the pane's book
            // button is hidden until the table has rows.
            empty.Add(new Separator());
            empty.AddRange(LibraryItems(vm));
            menu.ItemsSource = empty;
            return;
        }

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


        var remove = new MenuItem
        {
            Header = targets.Count == 1
                ? $"Remove {targets[0]} from this rail"
                : $"Remove {targets.Count} parts from this rail",
        };
        ToolTip.SetTip(remove,
            "Take these rows off the rail entirely. To keep a part in the table but off the board, "
            + "clear its mount checkbox instead — that keeps the mounting loop the artwork gave it "
            + "and is reversible.");
        remove.Click += (_, _) => Vm?.RemoveParts(targets);

        // ── R-rail35-1a: SERIES OR SHUNT, on the selection, one undo step each ──────────────
        var selected = PartsList.SelectedItems?.OfType<RailPartRowViewModel>().ToList() ?? [row];
        var connection = new List<object>();

        if (selected.Any(r => !r.IsSeries))
        {
            var series = new MenuItem
            {
                Header = targets.Count == 1 ? $"Make {targets[0]} a series element" : "Make series element",
            };
            ToolTip.SetTip(series,
                "The rail runs THROUGH this part — a ferrite bead, a sense resistor, a switch. It "
                + "cuts the rail into sections, and its DC resistance carries the current of every "
                + "load beyond it. Its two pads on the board become its terminals.");
            series.Click += (_, _) => vm.SetPartsConnection(targets, CircuitRF.Design.RailRf.RailPartConnection.Series);
            connection.Add(series);
        }

        if (selected.Any(r => r.IsSeries))
        {
            var shunt = new MenuItem
            {
                Header = targets.Count == 1 ? $"Make {targets[0]} decoupling (shunt)" : "Make decoupling (shunt)",
            };
            ToolTip.SetTip(shunt,
                "This part sits between the rail and its reference — a decoupling capacitor. Its "
                + "series DCR and model are kept, so making it series again gives the answer it gave "
                + "before.");
            shunt.Click += (_, _) => vm.SetPartsConnection(targets, CircuitRF.Design.RailRf.RailPartConnection.Shunt);
            connection.Add(shunt);
        }

        // EDIT MODEL SOURCE… on a series row (owner, 2026-09-24) — the same dialog a double-click on
        // its model-source cell opens.
        if (row.IsSeries)
        {
            var edit = new MenuItem { Header = $"Edit Model Source… ({row.Refdes})" };
            ToolTip.SetTip(edit, "Edit this series part's model — its DC resistance, and an R-L or a Touchstone file.");
            edit.Click += (_, _) => OpenSeriesModelDialog(row.Refdes);
            connection.Insert(0, edit);
        }

        List<object> items = [add, remove, new Separator(), .. connection, new Separator(), assign];
        if (seriesOffers.Count > 0) { items.Add(new Separator()); items.AddRange(seriesOffers); }
        items.Add(new Separator());
        items.AddRange(LibraryItems(vm));
        menu.ItemsSource = items;
    }

    // ══ EDIT MODEL SOURCE… (owner, 2026-09-24) ════════════════════════════════════════════════
    //
    // A series row's model is edited in a dialog, opened from its context menu or by double-clicking
    // its model-source cell. It used to open under the table whenever the row was SELECTED, which
    // pushed the table up and down as a user clicked through it.

    /// <summary>Opens the dialog on one series row. Modal: the row it edits cannot change under it.</summary>
    private async void OpenSeriesModelDialog(string refdes)
    {
        if (Vm is not { } vm || vm.BeginSeriesEdit(refdes) is not { } editor) return;
        try
        {
            var dialog = new RailSeriesModelDialog(
                editor, host => BrowseSeriesFile(editor, host), () => SaveSeriesFileToLibrary(editor));
            await dialog.ShowDialog(this);
        }
        finally { vm.EndSeriesEdit(); }
    }

    /// <summary>A series row's model-source cell, double-clicked. Other rows' models are the part
    /// library's, and are not edited here.</summary>
    private void OnPartModelSourceDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: RailPartRowViewModel { IsSeries: true } row }) return;
        e.Handled = true;
        OpenSeriesModelDialog(row.Refdes);
    }

    /// <summary>
    /// The dialog's Browse — a Touchstone file for the series row, written relative to the
    /// <c>.crail</c> like every other reference it carries (R-rail35-1b).
    /// </summary>
    private async void BrowseSeriesFile(RailSeriesEditorViewModel editor, Window host)
    {
        if (Vm is not { } vm) return;

        var files = await host.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"railRF — {editor.Refdes}'s measured impedance",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Touchstone") { Patterns = ["*.s2p", "*.s1p", "*.S2P", "*.S1P"] },
                new FilePickerFileType("All Files") { Patterns = ["*.*"] },
            ],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } picked) return;

        editor.TouchstoneEntry = vm.DocumentPath is { Length: > 0 } crail
            ? CircuitRF.Core.RefPath.ToStored(
                  Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(crail))!, picked))
            : picked;
    }

    /// <summary>
    /// <b>Save to library</b> (owner, 2026-09-24) — the series row's own Touchstone file becomes its
    /// part number's model file in the part library.
    /// </summary>
    /// <remarks>
    /// Where the library row is classed Other and was saved, the row's own reference is then cleared:
    /// it inherits the same file from the library, so the library is the one place it lives and a
    /// later correction there reaches this row too. Otherwise the row keeps its own, and the Messages
    /// line says why.
    /// </remarks>
    private void SaveSeriesFileToLibrary(RailSeriesEditorViewModel editor)
    {
        if (Vm is not { } vm) return;

        if (editor.PartNumber is not { } partNumber)
        {
            vm.Refusal = new RailRefusal(
                $"{editor.Refdes} has no part number, and the part library is keyed by one. Assign it "
                + "one first (right-click its row ▸ Assign part number…).", RailRefusalControl.None);
            return;
        }
        if (!NamesLiveLibrary(vm))
        {
            vm.Refusal = new RailRefusal(
                "This design has no part library to save the file to. Create one, or use an existing "
                + "one, from the book button under the parts table.", RailRefusalControl.None);
            return;
        }
        if (vm.DocumentPath is not { Length: > 0 } crail)
        {
            vm.Refusal = new RailRefusal(
                "Save this document first — the row's file is stored relative to the .crail.",
                RailRefusalControl.None);
            return;
        }
        if (WorkspaceLocator.Any() is not { } workspace)
        {
            vm.Refusal = new RailRefusal(
                "The part library is edited in the workspace, and this railRF window has none open "
                + "behind it. Open the workspace this design belongs to and try again.",
                RailRefusalControl.None);
            return;
        }

        string model = CircuitRF.Core.RefPath.Resolve(
            Path.GetDirectoryName(Path.GetFullPath(crail))!, editor.TouchstoneEntry);
        string library = Path.GetFileName(vm.PartLibraryPath)!;

        if (!workspace.SaveModelToPartLibrary(vm.PartLibraryPath!, partNumber, model,
                                              out bool saved, out bool isOther, out string? error))
        {
            vm.Refusal = new RailRefusal(error!, RailRefusalControl.None);
            return;
        }

        string file = Path.GetFileName(model);
        if (saved && isOther)
        {
            editor.TouchstoneEntry = "";
            workspace.Messages.Success(
                $"{file} is now {partNumber}'s model file in {library}; {editor.Refdes} takes it from the library.");
        }
        else if (!saved)
            workspace.Messages.Warning(
                $"{file} was set as {partNumber}'s model file in {library}, which had other unsaved edits "
                + $"and was not saved. Save the library to keep it; {editor.Refdes} keeps its own reference "
                + "until then.");
        else
            workspace.Messages.Warning(
                $"{file} is now {partNumber}'s model file in {library}, but that row is not classed Other, "
                + $"so a series row does not take its file. {editor.Refdes} keeps its own reference; class the "
                + "library row Other for series rows to share it.");
    }

    // ══ ADDING AND REMOVING A ROW (field report, 2026-09-22) ═══════════════════════════════════
    //
    // The pane could gain a row only through brief 26's discovery, and PartsEmptyText promised a
    // gesture — "rows can still be typed" — that did not exist anywhere. See RailAddPartDialog's own
    // header for what was reported and why the row it adds is Typed rather than Artwork.
    //
    // NEITHER OF THESE OWNS A RULE. Both end in one view-model call, on this file's own terms.

    /// <summary>The pane's + button. Offers the board's placed designators, and takes typed ones.</summary>
    private async void OnAddPartClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // R-rail22-2a's backstop: the button is hidden without a rail, and a press that arrived
        // anyway says what to do rather than doing nothing.
        if (vm.SelectedRail is not { } rail)
        {
            vm.Refusal = new RailRefusal(
                "Pick a rail first — a part row belongs to a rail, so there is nothing for it to "
                + "sit on until there is one.", RailRefusalControl.RailSelector);
            return;
        }

        var dialog = new RailAddPartDialog(rail.Name, vm.AddablePlacedParts, vm.Board is not null);
        if (await dialog.ShowDialog<IReadOnlyList<string>?>(this) is not { Count: > 0 } chosen) return;

        int added = vm.AddParts(chosen);
        if (added > 0)
            vm.Refusal = new RailRefusal(
                added == 1
                    ? $"Added {chosen[0]} to '{rail.Name}'. It carries no part number yet, so it "
                    + "contributes nothing to the curve — assign one, and the part library is what "
                    + "gives it a capacitance and a self-resonance."
                    : $"Added {added} parts to '{rail.Name}'. None carries a part number yet, so "
                    + "none contributes to the curve — assign one to the rows that are the same "
                    + "part, and the part library is what gives it a capacitance and a "
                    + "self-resonance.",
                RailRefusalControl.None);
    }

    /// <summary>The pane's remove button — the rows off the rail entirely, which is not unmounting.</summary>
    private void OnRemovePartClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var row = PartsList.SelectedItem as RailPartRowViewModel
               ?? PartsList.SelectedItems?.OfType<RailPartRowViewModel>().FirstOrDefault();

        if (row is null)
        {
            vm.Refusal = new RailRefusal(
                "Select the rows to remove first — there is nothing selected.",
                RailRefusalControl.None);
            return;
        }

        vm.RemoveParts(TargetsFor(row));
    }

    /// <summary>
    /// The pane's book button (R-rail27-3c): a menu of the library's two gestures — open or create
    /// this design's own, and <b>Use existing library…</b> (field report, 2026-09-23).
    /// </summary>
    /// <remarks>
    /// <b>ALWAYS the menu, including when the document already names a library.</b> It opened that
    /// library directly at first and offered the choice only where there was none, so on the ordinary
    /// document — one that already HAS a library — Use existing was nowhere to be seen (owner,
    /// 2026-09-23). Where one exists, Use existing merges the picked library's rows into it.
    /// </remarks>
    private void OnCreatePartLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Control button) { CreatePartLibrary(); return; }

        var flyout = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (var item in LibraryItems(vm)) flyout.Items.Add(item);
        flyout.ShowAt(button);
    }

    /// <summary>True where the document names a part library that is still on disk.</summary>
    private static bool NamesLiveLibrary(RailRfViewModel vm) =>
        vm.PartLibraryPath is { Length: > 0 } p && File.Exists(p);

    /// <summary>The library's two gestures, for the book button and both context menus alike.</summary>
    private MenuItem[] LibraryItems(RailRfViewModel vm)
    {
        bool has = NamesLiveLibrary(vm);

        var first = new MenuItem { Header = has ? "Open part library" : "Create part library…" };
        ToolTip.SetTip(first, has
            ? $"Open {Path.GetFileName(vm.PartLibraryPath)} in the part library editor."
            : "Seed a new .crlib in this workspace from the part numbers this document names, and open it.");
        first.Click += (_, _) => CreatePartLibrary();

        var use = new MenuItem { Header = "Use existing library…" };
        ToolTip.SetTip(use, has
            ? "Reuse a .crlib another design already built: merge its rows into this design's library "
            + "(new part numbers added, blank fields filled, this library's value kept where the two "
            + "disagree), or use it instead of this one."
            : "Use a .crlib another design already built — where it is, shared with every design that "
            + "names it, or copied into a new library in this workspace. A library already inside this "
            + "workspace is used as it is.");
        use.Click += (_, _) => UseExistingPartLibrary();

        return [first, use];
    }

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
        if (LibraryPreconditions() is not var (vm, crail, workspace)) return;

        // A document that already HAS a library opens it: the button is where the parts are, and
        // asking for a name only to refuse it afterwards left a designer asking where the part
        // library that holds the self-resonance could be made at all (field report, 2026-09-23).
        if (vm.PartLibraryPath is { Length: > 0 } existing && File.Exists(existing))
        {
            workspace.OpenOrActivatePartLibrary(existing);
            WorkspaceLocator.WindowFor(workspace)?.Activate();
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
    /// <summary>
    /// What Create and Use existing both need before they can act — a saved document and a workspace
    /// behind the window — with each refusal STATED, because railRF is an unowned window that
    /// outlives the workspace behind it.
    /// </summary>
    private (RailRfViewModel Vm, string Crail, CircuitRF.Ui.ViewModels.WorkspaceViewModel Workspace)?
        LibraryPreconditions()
    {
        if (Vm is not { } vm) return null;

        if (vm.DocumentPath is not { Length: > 0 } crail)
        {
            vm.Refusal = new RailRefusal(
                "Save this document first — a part library is written beside the .crail and named "
                + "by it, and this one has no path yet.", RailRefusalControl.None);
            return null;
        }

        if (WorkspaceLocator.Any() is not { } workspace)
        {
            vm.Refusal = new RailRefusal(
                "A part library is created in a workspace, and this railRF window has none open "
                + "behind it. Open the workspace this design belongs to and try again.",
                RailRefusalControl.None);
            return null;
        }

        return (vm, crail, workspace);
    }

    /// <summary>
    /// <b>Use existing library…</b> — start this design's library from one another design already
    /// built, rather than from nothing (field report, 2026-09-23).
    /// </summary>
    /// <remarks>
    /// <b>A library outside this workspace is REFERENCED or COPIED, as the user chooses</b> (owner,
    /// 2026-09-24; <see cref="RailUseLibraryDialog"/>). Referenced, it is a team's shared library and
    /// Archive Workspace offers it with its model files; copied, it becomes a new library here, seeded
    /// with this document's part numbers and merged on <c>PartLibraryMerge</c>'s rules. <b>One already
    /// inside this workspace is USED as it is.</b> A design that already has a library merges the
    /// picked one's rows into it or names the picked one instead.
    /// </remarks>
    private async void UseExistingPartLibrary()
    {
        if (LibraryPreconditions() is not var (vm, crail, workspace)) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title          = "Use Existing Part Library",
            AllowMultiple  = false,
            FileTypeFilter = [new FilePickerFileType("circuitRF part library") { Patterns = ["*.crlib"] }],
        });
        if (files is not [var file] || file.TryGetLocalPath() is not { Length: > 0 } source) return;

        // What to DO with it is asked where there is a choice (owner, 2026-09-24): a library outside
        // this workspace may be named where it is — a team's shared library — or copied in, and a
        // design that already has one may merge the rows or name the picked library instead. A
        // library already inside the workspace, for a design with none, is simply named.
        bool has     = NamesLiveLibrary(vm);
        bool outside = !workspace.IsInCurrentWorkspace(source);

        var use = RailLibraryUse.Reference;
        if (has || outside)
        {
            var ask = new RailUseLibraryDialog(
                source, has ? Path.GetFileName(vm.PartLibraryPath) : null, outside);
            use = await ask.ShowDialog<RailLibraryUse>(this);
        }
        if (use == RailLibraryUse.Cancel) return;

        // The rows INTO the design's own library — the editor's own Import, as one undoable edit the
        // user then saves, rather than a second library beside the first.
        if (use == RailLibraryUse.Merge)
        {
            if (!workspace.MergeIntoPartLibrary(vm.PartLibraryPath!, source, out string? refusal))
            {
                vm.Refusal = new RailRefusal(refusal!, RailRefusalControl.None);
                return;
            }
            WorkspaceLocator.WindowFor(workspace)?.Activate();
            return;
        }

        string? crlib;
        string? error;
        CircuitRF.Design.RailRf.PartLibraryImportReport? copied = null;

        if (use == RailLibraryUse.Reference)
            crlib = workspace.UsePartLibraryForRailDocument(crail, source, out error, replace: has);
        else
        {
            var name = new InputNameDialog(
                "New Part Library", "Part library name:", Path.GetFileNameWithoutExtension(source));
            if (await name.ShowDialog<string?>(this) is not { } chosen) return;
            crlib = workspace.CreatePartLibraryForRailDocument(crail, chosen, source, out error, out copied);
        }

        if (error is { Length: > 0 })
        {
            vm.Refusal = new RailRefusal(error, RailRefusalControl.None);
            if (crlib is null) return;
        }
        if (crlib is null) return;

        workspace.OpenPartLibraryWithReport(crlib, copied, Path.GetFileName(source));
        WorkspaceLocator.WindowFor(workspace)?.Activate();
    }
}
