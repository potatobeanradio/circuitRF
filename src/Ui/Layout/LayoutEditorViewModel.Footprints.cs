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
using CircuitRF.Design.Layout.Footprints;

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
        ReportMissingLandPatternRoles();
        return BeginPCellPlacement(reference.ToString(), new Dictionary<string, PCellValue>());
    }

    /// <summary>
    /// Says which of a land pattern's four roles this technology has no layer for, <b>every time a
    /// footprint placement is armed</b>.
    /// </summary>
    /// <remarks>
    /// <b>Because the sentence existed and almost nobody could ever see it</b> (reported from the
    /// field, 2026-09-21: add a silkscreen layer before placing an extra SMD part on a Gerber set
    /// that ships none, or expect trouble).
    /// <c>LandPatternLayers.Resolve</c> has reported a missing silkscreen, mask or courtyard since it
    /// was written, but the only route to that report was <c>GeneratedCellStore.GetOrCreate</c>'s
    /// diagnostics — which are returned on an ACTUAL generation and deliberately not on a reuse, so
    /// the FIRST 0603 said it once and every one after it said nothing. On a board imported from a
    /// Gerber set with no silkscreen in it, what you then get is two bare copper lands on
    /// copper-coloured artwork with no body outline around them, and nothing anywhere connecting that
    /// to the technology.
    ///
    /// <para>It asks the same function the generator asks, so there is no second rule about which
    /// layer a role resolves to — this is a QUERY of it, run for the report, and a pure one.</para>
    ///
    /// <para>Missing COPPER is not reported here: that is a refusal rather than a warning, the
    /// generation itself raises it, and repeating it as a warning first would put the weaker sentence
    /// in front of the stronger one.</para>
    /// </remarks>
    private void ReportMissingLandPatternRoles()
    {
        if (MessageSink is not { } sink) return;

        var diagnostics = new List<string>();
        var roles = LandPatternLayers.Resolve(Technology, PCellLayerSelection.Default, diagnostics);
        if (roles.Copper is null) return;

        foreach (string d in diagnostics) sink.Warning(d);

        // The CONSEQUENCE, once, in the terms a user placing a part on a board actually experiences
        // it — the role sentences above each name a layer and an omission, which is precise and does
        // not add up to "you will not be able to see the part you just placed".
        if (roles.Silkscreen is null)
            sink.Warning(
                "This board's technology has no silkscreen layer, so parts placed on it are drawn as "
              + "their bare copper lands with no body outline around them. If the Gerber set you "
              + "imported shipped a silkscreen or an assembly drawing, import it and this fills in; "
              + "otherwise add a silkscreen layer to the technology before placing parts.");
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
