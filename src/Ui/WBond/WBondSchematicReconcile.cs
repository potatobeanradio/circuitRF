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
        if (wBonds.Count == 0)
        {
            // Wires in the layout with no component to put them on. Reported rather than silently
            // dropped: the user drew them, and "nothing happened" is the least useful answer.
            return new Result(null,
                [$"The layout holds {fromLayout.WireCount} bond wire(s) in {fromLayout.Arrays.Count} " +
                 "array(s), but this schematic has no wBond component to bring them into. Place one " +
                 "from the Library palette first."],
                false);
        }

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
