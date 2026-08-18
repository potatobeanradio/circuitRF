using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.WBond;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// wbond.md §9.6/WB42 — <b>Update Schematic from Layout brings the wires back into the component.</b>
///
/// <h3>The reported bug</h3>
/// <para>Owner, 2026-08-17: <i>"if I change loop height in layout editor, the component in the schematic
/// is not updated. Even if I use Update Schematic from Layout command. … Same is true for deleting a
/// whole group of wires in layout — the deletion is not respected in schematic."</i></para>
///
/// <para>Both are one cause: <see cref="LayoutToSchematicGenerator"/> walks a layout's
/// <c>LayoutInstance</c>s and had <b>no knowledge of the wire layer whatsoever</b>. §9.6 specifies this
/// reconcile and two shipped messages already named the command as the remedy — including
/// <see cref="WBondCellSeeding"/>'s own "use Design ▸ Update Schematic from Layout to bring them back
/// into the component". The command existed; the half of it that handles wires did not.</para>
///
/// <h3>Why this matters even for a LINKED instance</h3>
/// <para>It is tempting to think linking makes this unnecessary — WB45 says a linked instance simulates
/// the file, so a geometry edit in the layout is picked up at the next Run with nothing to reconcile.
/// That is true of GEOMETRY and false of the ARRAY LIST, and the difference is not cosmetic: a placed
/// wBond's <b>pins are drawn from its carried payload</b> (<c>WBondSymbolProvider</c>), so deleting an
/// array in the layout leaves a symbol still showing that array's two terminals, still wired to
/// whatever the user connected them to, while the model behind it has one branch fewer. The netlist and
/// the symbol then disagree about how many terminals this component has.</para>
///
/// <para>So: under <c>Carried</c> this command is how a layout edit reaches the simulation at all; under
/// <c>Linked</c> it is how a layout edit reaches the <i>symbol</i>. Neither is optional, and the
/// Source-note text was corrected at the same time as this was built, because it had claimed a wire
/// edited in the layout "no longer needs bringing back into the schematic".</para>
///
/// <h3>What it does NOT touch</h3>
/// <para><c>Source</c>. Reconciling makes the payload agree with the file; it does not decide which of
/// them the next Run reads, and quietly flipping that is precisely what WB45a forbids.</para>
/// </summary>
public static class WBondSchematicReconcile
{
    /// <param name="Command">
    /// The single parameter edit to execute, or null when nothing changed. One command, so the whole
    /// reconcile is one undo entry that puts the previous wires back — the same shape the wirebond
    /// import already uses.
    /// </param>
    /// <param name="Messages">Lines to report, most important first.</param>
    /// <param name="ArraysMoved">
    /// True when the array LIST changed, so the symbol's pins have moved and existing wiring needs
    /// checking. Separated from the ordinary case because it is the one outcome that can silently
    /// re-point a wire the user already drew.
    /// </param>
    public readonly record struct Result(
        IUiCommand? Command, IReadOnlyList<string> Messages, bool ArraysMoved)
    {
        public static Result None => new(null, [], false);
    }

    /// <summary>
    /// Computes the parameter edit that makes the schematic's wBond component describe
    /// <paramref name="fromLayout"/>.
    /// </summary>
    /// <param name="fromLayout">
    /// The layout's LIVE wire design (<c>LayoutEditorViewModel.WireDesign</c>), not the file on disk —
    /// so an edit the user has not saved yet still reconciles, exactly as the instance half of this
    /// command reads the live <c>LayoutView</c>. Null when the layout carries no wires, which is the
    /// ordinary case for most cells and says nothing.
    /// </param>
    public static Result Run(SchematicEditModel schematic, WBondDesign? fromLayout)
    {
        ArgumentNullException.ThrowIfNull(schematic);

        if (fromLayout is null) return Result.None;

        var wBonds = schematic.Components.Where(c => c.Symbol == SymbolKind.WBond).ToList();

        // EVERY WIRE DELETED: the component goes with them (owner, 2026-08-17 — "if all wires are
        // deleted from a layout and the user performs an Update Schematic from Layout, a wBond symbol
        // remains in the schematic; there should be no wBond component").
        //
        // This is the same rule as the two branches below, applied to the empty case rather than
        // stopping short of it: this command makes the schematic describe the layout, and a layout
        // with no wires is described by no component. Leaving one behind is worse than untidy — a
        // wBond carrying an empty payload still draws its pins and still declares its terminals, so
        // the netlist would go on modelling a bond group the layout no longer has.
        //
        // A NULL design is different and says nothing: that is a layout with no wire layer at all,
        // which is every ordinary cell this command runs on. Only an ATTACHED, EMPTY design means
        // "the user deleted the wires" — the guard for that is the null check above.
        if (fromLayout.WireCount == 0) return RemoveFrom(schematic, wBonds);

        // Wires in the layout and no component to put them on: CREATE one (owner, 2026-08-17 —
        // "if the user does an Update Schematic from Layout, then the schematic should get a wBond
        // symbol with the appropriate parameters/settings that matches the layout"). This is now
        // reachable by an ordinary route: a wBond dropped straight into a layout out of the palette
        // (WB40b) has no schematic component behind it by construction, and telling that user to go
        // and place one by hand is asking them to do what this command is for.
        if (wBonds.Count == 0) return CreateFrom(schematic, fromLayout);

        // §7/WB28 again: merging one layout's wires into two components would break each one's
        // array-to-pin mapping, and there is no way to tell which wire belongs to which. One is
        // written and the rest are NAMED, matching WBondCellSeeding's own stance in the other
        // direction.
        var messages = new List<string>();
        var comp = wBonds[0];

        if (wBonds.Count > 1)
            messages.Add(
                $"This schematic holds {wBonds.Count} wBond components. Only '{comp.InstanceName}' was " +
                "updated from the layout; " +
                string.Join(", ", wBonds.Skip(1).Select(c => $"'{c.InstanceName}'")) +
                " was left alone. One wBond per cell view is the convention (wbond.md §7).");

        // The array list is compared BEFORE the payload is written, because ApplyDesign updates the
        // record in step and the comparison would then always agree with itself.
        var drift = WBondPlacement.DriftBetween(comp, fromLayout, "the layout");

        var updated = comp.Parameters.Select(p => p.Clone()).ToList();
        var scratch = new EditableComponent { Symbol = SymbolKind.WBond };
        foreach (var p in updated) scratch.Parameters.Add(p);
        WBondPlacement.ApplyDesign(scratch, fromLayout);

        // The controlling parameters come back too, or this command is undone by the next Run.
        //
        // Owner, 2026-08-17: "I changed the loop height in layout using the Array Inductance
        // double-click, then did an Update Schematic from Layout, but the loop height was not updated
        // in the schematic." Bringing back only the GEOMETRY left `LoopHeight_G1` stating the old
        // number — so the dialog went on showing it AND the next Run applied it straight back over the
        // wires that had just been imported. Only parameters already SET are written, only when they
        // are literals, and a wire set that disagrees is reported rather than averaged.
        WBondPlacement.WriteBackControllingParameters(scratch.Parameters, fromLayout, messages);

        // An array deleted in the layout takes its controlling parameters with it, and the survivors
        // are ordered so the symbol's labels come out in array order. Without the first, the symbol
        // draws "LoopHeight_G2 = 30 mil" for a pin pair it no longer has.
        var reconciled = WBondPlacement.InCanonicalOrder(
            WBondPlacement.ReconcilePerArrayParameters(
                scratch.Parameters, [.. fromLayout.Arrays.Select(a => a.Name)]));

        // "Nothing changed" is decided on the FINISHED parameter list, not on the payload alone.
        //
        // Comparing only the `Design` payload used to return early here — and that is exactly the hole
        // the owner fell into: the layout and the payload agreed on geometry while `LoopHeight_G1` still
        // stated the old number, so the command returned "already identical" and left the stale override
        // in place to be applied again at the next Run. Whatever this command would write is what
        // decides whether it has anything to do.
        if (SameParameters(comp.Parameters, reconciled))
        {
            // Say nothing at all when there was also nothing to REPORT. "The wires already agree" is
            // not news to someone who just ran this on a cell they have been editing, and reporting it
            // trains people to skim the pane. A write-back that was deliberately skipped is different —
            // the command did not fully do its job, and that has to be sayable.
            return new Result(null, messages, false);
        }

        messages.Add(
            $"wBond '{comp.InstanceName}' updated from the layout: {fromLayout.Arrays.Count} array(s), " +
            $"{fromLayout.WireCount} wire(s).");

        if (drift is not null)
            messages.Add(
                drift.Message + " The layout's arrays are now what the symbol shows — check the wiring " +
                "on this component before running.");

        return new Result(
            new SetParametersCommand(schematic, comp, reconciled),
            messages,
            drift is not null);
    }

    /// <summary>
    /// Removes the wBond component(s) from a schematic whose layout has no wires left.
    ///
    /// <para><b>One command for all of them</b>, so the whole removal is one undo entry — and it is
    /// <c>DeleteCommand</c>, the schematic's own, which restores each component at its original list
    /// index. Undo therefore puts the symbol back exactly where it was, wired to whatever it was wired
    /// to; nothing here needs its own inverse.</para>
    ///
    /// <para><b>The wires that CONNECTED to it are left alone</b>, exactly as deleting a component by
    /// hand leaves them. They are the user's drawing, and guessing which of them existed only to reach
    /// this component is not something this command can know.</para>
    /// </summary>
    private static Result RemoveFrom(SchematicEditModel schematic, IReadOnlyList<EditableComponent> wBonds)
    {
        // Nothing there and nothing to remove: the ordinary state of a cell whose wires were deleted
        // and whose schematic was already updated once. Silent, like every other no-op here.
        if (wBonds.Count == 0) return Result.None;

        string names = string.Join(", ", wBonds.Select(c => $"'{c.InstanceName}'"));

        return new Result(
            new DeleteCommand(schematic, [.. wBonds.Select(c => c.Id)]),
            [$"The layout has no bond wires left, so {names} " +
             $"{(wBonds.Count == 1 ? "was" : "were")} removed from this schematic. Any nets that were " +
             "wired to it are still drawn — check them before running."],
            false);
    }

    /// <summary>
    /// Places a new wBond component carrying <paramref name="fromLayout"/>.
    ///
    /// <para><b>Built through <see cref="WBondPlacement.BuildCarrying"/>, the same call the palette
    /// drop and the wirebond IMPORT both go through</b> — so a component that arrived this way and one
    /// dropped by hand are the same component, with the same defaults, and "wBond" cannot come to mean
    /// two slightly different things depending on which end of the flow it entered from. The
    /// parameters are put in canonical order for the same reason the update path does it: order is
    /// what the symbol renders its labels in.</para>
    ///
    /// <para>It is placed clear of everything already on the sheet and <b>not wired to anything</b>.
    /// It cannot be: an array's two terminals are a pin PAIR whose nets only the user knows. The
    /// message says so, because a component that appears unconnected with no explanation reads as a
    /// half-finished command.</para>
    /// </summary>
    private static Result CreateFrom(SchematicEditModel schematic, WBondDesign fromLayout)
    {
        var comp = WBondPlacement.BuildCarrying(
            fromLayout, SchematicEditModel.NextAvailableName(schematic.Components, SymbolKind.WBond));

        var (x, y) = WBondPlacement.SuggestPlacementPoint(schematic);
        comp.X = x;
        comp.Y = y;

        var ordered = WBondPlacement.InCanonicalOrder(comp.Parameters);
        comp.Parameters.Clear();
        foreach (var p in ordered) comp.Parameters.Add(p);

        return new Result(
            new PlaceComponentCommand(schematic, comp),
            [$"wBond '{comp.InstanceName}' was created from the layout: {fromLayout.Arrays.Count} " +
             $"array(s), {fromLayout.WireCount} wire(s). Its pins are not connected to anything yet — " +
             "each array is one pin pair, and only you know which nets they belong to."],
            false);
    }

    /// <summary>
    /// Whether two parameter lists state the same thing — name, expression, unit and order.
    ///
    /// <para>ORDER is part of it, because order is what the symbol renders its labels in
    /// (<c>WBondPlacement.InCanonicalOrder</c>): a list that differs only in order still needs writing,
    /// or the labels stay in whatever sequence they were typed.</para>
    /// </summary>
    private static bool SameParameters(
        IReadOnlyList<EditableParameter> a, IReadOnlyList<EditableParameter> b)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].Expression, b[i].Expression, StringComparison.Ordinal)) return false;
            if (!string.Equals(a[i].Unit ?? "", b[i].Unit ?? "", StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
