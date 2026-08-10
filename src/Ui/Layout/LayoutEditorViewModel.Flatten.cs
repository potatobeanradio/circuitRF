using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Commands;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L3c — Flatten Hierarchy, one level and all levels (docs/sonnet-briefs/brief-L3c-flatten-and-
/// group.md §2/§3). Mirrors how <c>.Booleans.cs</c>/<c>.Clipboard.cs</c>/<c>.Retarget.cs</c> split
/// concerns out of the main VM file. All the actual geometry/recursion math lives in
/// <see cref="LayoutFlatten"/> (framework-free); this file is selection/availability/undo/Messages
/// plumbing plus the cross-technology reconciliation seam (R-L3c-3), reusing L1g's
/// <see cref="LayoutLayerMapping"/> exactly as paste and retarget already do.
/// <br/><br/>
/// <b>Scoped to a SINGLE selected instance, deliberately</b> — every gate in the brief (§6, gates 2-7)
/// describes one instance's own hierarchy; multi-instance flatten with independent per-instance
/// cross-technology reconciliation is real added scope the brief never asks for, so it is not built
/// here (Group Into Cell, by contrast, explicitly needs a full mixed multi-item selection — that one
/// lives in <c>.Group.cs</c>).
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>R-L3c-1a: "confirm above a modest threshold in either unit — shapes because of what
    /// L2c measured, instances because 2,500 objects is still a large selection to land on someone
    /// unannounced." 500 is comfortably below L2c's own 2,000-shape merge-tier threshold (where
    /// per-shape darkening feedback already becomes visual noise) and comfortably below R-L3a-3's own
    /// 2,500-instance array-explode example — large enough that a small, everyday flatten never nags,
    /// small enough that neither unit's "large selection" case slips through unconfirmed.</summary>
    public const int FlattenConfirmThreshold = 500;

    /// <summary>Instance-only, single-selection gate shared by Flatten Hierarchy / Flatten All Levels /
    /// Explode Array — mirrors <c>ShapeOnlyBlockReason</c>'s "state why, never silently vanish" rule
    /// (R13a) for the inverse case (these ops are instance-only; a shape in the selection blocks them).</summary>
    private LayoutCommandAvailability SingleInstanceOnlyAvailability(string opLabel)
    {
        if (_selectedIndices.Count > 0)
            return LayoutCommandAvailability.Disabled($"{opLabel} apply to a single instance; shape(s) are selected.");
        if (_selectedInstanceIndices.Count == 0)
            return LayoutCommandAvailability.Disabled($"{opLabel}: select one instance.");
        if (_selectedInstanceIndices.Count > 1)
            return LayoutCommandAvailability.Disabled($"{opLabel} applies to exactly one instance at a time; {_selectedInstanceIndices.Count} are selected.");
        return LayoutCommandAvailability.Enabled;
    }

    public LayoutCommandAvailability FlattenHierarchyAvailability => SingleInstanceOnlyAvailability("Flatten Hierarchy");

    public LayoutCommandAvailability FlattenAllLevelsAvailability => SingleInstanceOnlyAvailability("Flatten All Levels");

    public LayoutCommandAvailability ExplodeArrayAvailability
    {
        get
        {
            var basic = SingleInstanceOnlyAvailability("Explode Array");
            if (!basic.CanExecute) return basic;
            var inst = Model.Instances[_selectedInstanceIndices[0]];
            return inst.Rows > 1 || inst.Cols > 1
                ? LayoutCommandAvailability.Enabled
                : LayoutCommandAvailability.Disabled("Explode Array: the selected instance is not an array.");
        }
    }

    /// <summary>R-L3c-1a's outcome preview — "→ 2,500 instances" for an array, "→ 20 shapes" for a
    /// plain instance. The shape count comes from <see cref="LayoutFlatten.CountOneLevelShapes"/>,
    /// which shares its "does this survive a flatten" predicate with the emit loop itself — this
    /// getter previously read <c>res.View.Shapes.Count</c> directly and claimed in this very comment
    /// not to re-derive the count, which is exactly what it was doing; the day the emit learned to
    /// drop a sub-cell's own port labels, the menu went on promising three shapes for a one-shape
    /// result. Null when nothing is selected or the instance does not resolve — the enablement gate
    /// already reports those cases on its own.</summary>
    public string? FlattenOneLevelOutcomeText
    {
        get
        {
            if (_selectedIndices.Count > 0 || _selectedInstanceIndices.Count != 1) return null;
            var inst = Model.Instances[_selectedInstanceIndices[0]];
            if (inst.Rows > 1 || inst.Cols > 1)
                return $"→ {(long)Math.Max(1, inst.Rows) * Math.Max(1, inst.Cols):N0} instance(s)";
            return LayoutFlatten.CountOneLevelShapes(inst, InstanceBaseDir) is { } n
                ? $"→ {n:N0} shape(s)"
                : null;
        }
    }

    /// <summary>The all-levels analogue of <see cref="FlattenOneLevelOutcomeText"/> — R-L3c-4's
    /// pre-computed count, shown before anything is mutated. Reads "→ over N,NNN shapes — refused" when
    /// the count would exceed <see cref="LayoutFlatten.FlattenAllLevelsHardCeiling"/>.</summary>
    public string? FlattenAllLevelsOutcomeText
    {
        get
        {
            if (_selectedIndices.Count > 0 || _selectedInstanceIndices.Count != 1) return null;
            var inst = Model.Instances[_selectedInstanceIndices[0]];
            long count = LayoutFlatten.CountResultingShapes(inst, InstanceBaseDir);
            return count < 0
                ? $"→ over {LayoutFlatten.FlattenAllLevelsHardCeiling:N0} shapes — refused"
                : $"→ {count:N0} shape(s)";
        }
    }

    /// <summary>True once either unit of the outcome preview crosses <see cref="FlattenConfirmThreshold"/>
    /// — the view uses this to decide whether to show a confirm dialog before committing.</summary>
    public bool FlattenOneLevelNeedsConfirmation
    {
        get
        {
            if (_selectedInstanceIndices.Count != 1) return false;
            var inst = Model.Instances[_selectedInstanceIndices[0]];
            if (inst.Rows > 1 || inst.Cols > 1)
                return (long)Math.Max(1, inst.Rows) * Math.Max(1, inst.Cols) > FlattenConfirmThreshold;
            // Same count the menu label shows — a confirm dialog that fires on a threshold the
            // preview beside it never crossed reads as the app disagreeing with itself.
            return LayoutFlatten.CountOneLevelShapes(inst, InstanceBaseDir) > FlattenConfirmThreshold;
        }
    }

    /// <summary>Resolves the technology the currently selected instance's OWN sub-cell uses — the
    /// first place this codebase resolves a sub-cell's technology rather than inheriting the
    /// embedding document's (L3a's stated simplification; R-L3c-3 is the first consumer that needs to
    /// look past it). Null when nothing is resolvable, or when <see cref="ResolveTechAt"/> was never
    /// wired (a scratch document with no workspace, matching every other seam's null-safe fallback).</summary>
    private Technology? ResolveSubCellTechnology(LayoutInstance inst)
    {
        var res = CellLayoutResolver.Resolve(inst.CellRef, InstanceBaseDir);
        if (res.State != CellLayoutState.Resolved) return null;
        var subCellLayoutDir = CellHierarchy.LayoutBaseDirOf(res.ResolvedCellDir!);
        return ResolveTechAt?.Invoke(res.View!.TechRef, subCellLayoutDir).Tech;
    }

    /// <summary>Public accessor for the currently selected instance's sub-cell technology — used only
    /// to label the cross-technology confirmation dialog (the canvas has no other way to reach it;
    /// <see cref="ResolveSubCellTechnology"/> itself stays private since every other caller already
    /// has the instance in hand).</summary>
    public Technology? FlattenSelectedSubCellTechnology() =>
        _selectedIndices.Count > 0 || _selectedInstanceIndices.Count != 1
            ? null
            : ResolveSubCellTechnology(Model.Instances[_selectedInstanceIndices[0]]);

    /// <summary>
    /// R-L3c-3: proposes L1g's cross-technology layer mapping for the currently selected instance's
    /// DIRECT sub-cell, exactly as paste/retarget already do (never a second reconciliation
    /// implementation). Returns <c>null</c> when no confirmation is needed — an array (nothing to
    /// remap; explode never touches shapes), an unresolvable instance (the flatten itself will report
    /// that), a document with no technology on either side, or a mapping where every row is a
    /// confident same-key-same-name/exact-name match. The view calls this BEFORE committing and, when
    /// non-null, shows the SAME <c>LayerMappingDialog</c> the paste/retarget flows use.
    /// </summary>
    public IReadOnlyList<LayerMappingRow>? CheckFlattenCrossTechMapping()
    {
        if (_selectedIndices.Count > 0 || _selectedInstanceIndices.Count != 1) return null;
        var inst = Model.Instances[_selectedInstanceIndices[0]];
        if (inst.Rows > 1 || inst.Cols > 1) return null;

        var res = CellLayoutResolver.Resolve(inst.CellRef, InstanceBaseDir);
        if (res.State != CellLayoutState.Resolved) return null;

        var subTech = ResolveSubCellTechnology(inst);
        if (subTech is null) return null;

        var mapping = LayoutLayerMapping.Propose(res.View!.Shapes, subTech.Layers, Technology);
        return LayoutLayerMapping.RequiresConfirmation(mapping) ? mapping : null;
    }

    /// <summary>Applies a resolved cross-tech mapping to a just-flattened shape list, reusing the SAME
    /// <see cref="ApplyFragmentReconciliation"/> paste already uses (Add-to-technology choices install
    /// through the identical live-tech seam). A no-op (returns <paramref name="shapes"/> verbatim) when
    /// there is nothing to reconcile against.</summary>
    private IReadOnlyList<LayoutShape> ApplyFlattenReconciliation(
        LayoutInstance sourceInst, IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayerMappingRow>? resolvedMapping)
    {
        if (resolvedMapping is null || shapes.Count == 0) return shapes;
        var subTech = ResolveSubCellTechnology(sourceInst);
        if (subTech is null) return shapes;
        var choices = LayoutLayerMapping.BuildChoices(resolvedMapping);
        return ApplyFragmentReconciliation(shapes, subTech.Layers, choices);
    }

    /// <summary>
    /// Commits Flatten Hierarchy — ONE level (R-L3c-1) — for the currently selected instance. On an
    /// array this yields N plain instances (Explode Array is the same command under a second, array-
    /// only-enabled menu entry — §2's explicit "must route through the same command" requirement); on
    /// a plain instance it yields the sub-cell's own shapes plus its own nested instances, unchanged.
    /// One <see cref="CompositeCommand"/> — delete the original instance, add the new shapes, add the
    /// new instances — is a single undo entry regardless of how many of each kind resulted, mirroring
    /// <c>InsertPastedMixed</c>'s exact shape. <paramref name="resolvedMapping"/> is the (possibly null)
    /// result of a prior <see cref="CheckFlattenCrossTechMapping"/> + confirmation dialog round trip.
    /// </summary>
    public void CommitFlattenOneLevel(IReadOnlyList<LayerMappingRow>? resolvedMapping = null)
    {
        if (_selectedIndices.Count > 0 || _selectedInstanceIndices.Count != 1) return;
        int index = _selectedInstanceIndices[0];
        if (index < 0 || index >= Model.Instances.Count) return;
        var inst = Model.Instances[index];

        var result = LayoutFlatten.FlattenOneLevel(inst, InstanceBaseDir);
        if (result is null)
        {
            _messageSink?.Error($"Flatten Hierarchy: '{inst.CellRef}' could not be resolved — nothing was changed.");
            return;
        }

        var shapes = ApplyFlattenReconciliation(inst, result.Shapes, resolvedMapping);

        int shapeInsertAt = Model.Shapes.Count;
        int instanceInsertAt = Model.Instances.Count;

        IUiCommand combined = new Commands.Layout.DeleteInstancesCommand(Model, [index]);
        if (shapes.Count > 0)
            combined = new CompositeCommand(combined, new Commands.Layout.ReplaceShapesCommand(Model, [], shapes, "Flatten Hierarchy"));
        foreach (var subInst in result.Instances)
            combined = new CompositeCommand(combined, new Commands.Layout.AddInstanceCommand(Model, subInst));

        Execute(combined);
        ReplaceMixedSelection(Enumerable.Range(shapeInsertAt, shapes.Count), Enumerable.Range(instanceInsertAt, result.Instances.Count));

        _messageSink?.Success(result.WasArray
            ? $"Flatten Hierarchy: exploded array into {result.Instances.Count} instance(s)."
            : result.Instances.Count > 0
                ? $"Flatten Hierarchy: replaced instance with {shapes.Count} shape(s) and {result.Instances.Count} nested instance(s)."
                : $"Flatten Hierarchy: replaced instance with {shapes.Count} shape(s).");
    }

    /// <summary>
    /// Commits Flatten Hierarchy — ALL levels (§3) — for the currently selected instance. Refuses
    /// outright, unmutated, when <see cref="LayoutFlatten.CountResultingShapes"/> would exceed
    /// <see cref="LayoutFlatten.FlattenAllLevelsHardCeiling"/> (R-L3c-4, the ceiling named in the
    /// message). A broken/unresolvable instance anywhere in the tree survives as one of
    /// <see cref="LayoutFlatten.AllLevelsResult.SurvivingInstances"/> and is reported, never dropped.
    /// Cross-technology reconciliation (R-L3c-3) is applied only against the TOP-LEVEL selected
    /// instance's own direct sub-cell — a deeper hierarchy that ALSO mixes technologies at a nested
    /// level is a stated, narrower simplification (mirrors L3a's own "sub-cell renders using the
    /// parent's technology" scope decision), not silently ignored.
    /// </summary>
    public void CommitFlattenAllLevels(IReadOnlyList<LayerMappingRow>? resolvedMapping = null)
    {
        if (_selectedIndices.Count > 0 || _selectedInstanceIndices.Count != 1) return;
        int index = _selectedInstanceIndices[0];
        if (index < 0 || index >= Model.Instances.Count) return;
        var inst = Model.Instances[index];

        long count = LayoutFlatten.CountResultingShapes(inst, InstanceBaseDir);
        if (count < 0)
        {
            _messageSink?.Error(
                $"Flatten All Levels: the result would exceed {LayoutFlatten.FlattenAllLevelsHardCeiling:N0} shapes — refused. Nothing was changed.");
            return;
        }

        var result = LayoutFlatten.FlattenAllLevels(inst, InstanceBaseDir);
        var shapes = ApplyFlattenReconciliation(inst, result.Shapes, resolvedMapping);

        int shapeInsertAt = Model.Shapes.Count;
        int instanceInsertAt = Model.Instances.Count;

        IUiCommand combined = new Commands.Layout.DeleteInstancesCommand(Model, [index]);
        if (shapes.Count > 0)
            combined = new CompositeCommand(combined, new Commands.Layout.ReplaceShapesCommand(Model, [], shapes, "Flatten All Levels"));
        foreach (var surviving in result.SurvivingInstances)
            combined = new CompositeCommand(combined, new Commands.Layout.AddInstanceCommand(Model, surviving));

        Execute(combined);
        ReplaceMixedSelection(Enumerable.Range(shapeInsertAt, shapes.Count), Enumerable.Range(instanceInsertAt, result.SurvivingInstances.Count));

        if (result.SurvivingInstances.Count > 0)
            _messageSink?.Warning(
                $"Flatten All Levels: {shapes.Count} shape(s) created; {result.SurvivingInstances.Count} unresolvable instance(s) left in place.");
        else
            _messageSink?.Success($"Flatten All Levels: replaced instance with {shapes.Count} shape(s).");
    }
}
