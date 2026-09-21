// brief-footprint-3-update-layout.md §6 — the layout editor's own two footprint gestures.
//
// They are DIFFERENT gestures and this file keeps them apart deliberately:
//
//   R-fp3-6a  re-point the SELECTED instance at a different land pattern, through the existing
//             ReplaceSelectedInstance path, so it is one undoable command like every other
//             instance edit.
//   R-fp3-6b  place one BY HAND, with nothing selected and no schematic anywhere — the designer's
//             own words: "if you don't have a netlist, you can always place the parts by hand that
//             way, on top of the same pads at that layer".
//
// R-fp3-6d, stated because it is an absence and absences are invisible: re-pointing an instance
// that carries a SchematicId does NOT write back to the schematic. The layout and the schematic
// then disagree, which is what Update Layout exists to reconcile and what its change report exists
// to say; a silent write-back would make an artwork edit change a simulation. Nothing here can do
// it by accident either — this view model holds no reference to a schematic at all.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Ui.Layout;

public sealed partial class LayoutEditorViewModel
{
    /// <summary>
    /// Arms the ordinary instance-placement ghost for a built-in land pattern — R-fp3-6b.
    ///
    /// <para>It is <see cref="BeginPCellPlacement"/> with the reference as the generator id and no
    /// parameters, because that is genuinely all a land pattern is: its case and its density ARE its
    /// identity (<c>FootprintGeneratorResolver.DeclaredDefaults</c> answers with an empty set for
    /// exactly this reason). So this places through the same <c>TryPlaceNewInstance</c> every other
    /// placement uses, which is also what makes R-fp3-6c true without anything asserting it here:
    /// that path writes no <c>SchematicId</c>, because an instance placed by hand corresponds to no
    /// schematic component and v2's LVS should report it as unmatched.</para>
    /// </summary>
    public bool BeginFootprintPlacement(FootprintRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return BeginPCellPlacement(reference.ToString(), new Dictionary<string, PCellValue>());
    }

    /// <summary>
    /// Re-points the single selected instance at <paramref name="reference"/>'s land pattern —
    /// R-fp3-6a. One <c>ReplaceInstanceCommand</c>, therefore one undo entry, therefore
    /// indistinguishable from changing the instance's rotation or its magnification.
    ///
    /// <para>The generated cell is created-or-reused first, through the same content-addressed store
    /// Update Layout writes into, so re-pointing by hand and re-running Update Layout land on the
    /// SAME cell rather than on two identical ones under different names.</para>
    /// </summary>
    public bool RetargetSelectedInstanceToFootprint(FootprintRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (SingleSelectedInstance is null) return false;
        if (ResolveFootprintCellRef(reference) is not { } cellRef) return false;
        RetargetSelectedInstance(cellRef);
        return true;
    }

    /// <summary>The built-in land pattern the single selected instance is currently drawing, or null
    /// when it is anything else — a hand-drawn cell, an imported one, a microstrip PCell. What the
    /// picker opens on, so it never claims a case size for artwork that is not one.</summary>
    public FootprintRef? SelectedInstanceFootprint
    {
        get
        {
            if (SingleSelectedInstance is not { } inst) return null;
            if (CellLayoutResolver.Resolve(inst.CellRef, InstanceBaseDir) is not
                { State: CellLayoutState.Resolved, View.PCellOrigin: { } origin }) return null;
            return FootprintRef.TryParse(origin.GeneratorId, out var reference, out _) ? reference : null;
        }
    }

    /// <summary>Every built-in land pattern, in table order, each reading its metric twin and its
    /// millimetres — the overview's §1e spelling, which is not decoration: <c>0201</c> imperial and
    /// <c>0201</c> metric are two real case sizes differing by 2.4x.</summary>
    public static IReadOnlyList<SmtCase> FootprintCases => SmtCaseTable.All;

    private string? ResolveFootprintCellRef(FootprintRef reference)
        => ResolvePCellCellRef(reference.ToString(), new Dictionary<string, PCellValue>());
}
