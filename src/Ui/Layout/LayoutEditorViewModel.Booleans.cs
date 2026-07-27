using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Phase L1e — Clipper2 booleans/offsets, self-intersection repair, and Flatten to Polygon
/// (docs/sonnet-briefs/brief-L1e-clipper-operations.md). All geometry lives in
/// <see cref="LayoutBooleans"/>/<see cref="LayoutClipper"/>/<see cref="LayoutFlattenToPolygon"/> (pure,
/// framework-free); this file is only selection plumbing + <c>Commands.Layout.ReplaceShapesCommand</c>
/// wiring + Messages reporting, mirroring how the rest of <c>LayoutEditorViewModel</c> is organized.
/// One undo entry per operation, always (§3/§4/§5 of the brief) — every method below builds exactly
/// one <c>ReplaceShapesCommand</c> and calls <see cref="Execute"/> exactly once.
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    /// <summary>§3.2 R9e: "warn once per session, not once per operation." A session here is this open
    /// document's lifetime — mirrors <c>_warnedUnknownLayers</c>'s per-document scope above.</summary>
    private bool _warnedCurvedOperandThisSession;

    // ── Enablement ─────────────────────────────────────────────────────────────

    private IReadOnlyList<int> ValidSelectedIndices =>
        _selectedIndices.Where(i => i >= 0 && i < Model.Shapes.Count).ToList();

    public bool CanBooleanOp => ValidSelectedIndices.Count >= 2;
    public bool CanOffsetSelection => ValidSelectedIndices.Count >= 1;
    public bool CanMergeSelection => ValidSelectedIndices.Count >= 1;
    public bool CanFlattenSelection => ValidSelectedIndices.Any(HasCurvedGeometryAt);
    public bool CanRepairSelected => _selectedIndices.Count == 1 && IsSelfIntersecting(_selectedIndices[0]);

    public bool HasCurvedGeometryAt(int index) =>
        index >= 0 && index < Model.Shapes.Count && LayoutFlattenToPolygon.HasCurvedGeometry(Model.Shapes[index]);

    public bool IsSelfIntersecting(int shapeIndex) =>
        shapeIndex >= 0 && shapeIndex < Model.Shapes.Count &&
        LayoutSelfIntersection.Test(Model.Shapes[shapeIndex], Technology);

    // ── Commands (menu/toolbar entry points; the public methods below are the tested surface) ─────

    public IRelayCommand UnionCommand { get; private set; } = null!;
    public IRelayCommand IntersectCommand { get; private set; } = null!;
    public IRelayCommand DifferenceCommand { get; private set; } = null!;
    public IRelayCommand XorCommand { get; private set; } = null!;
    public IRelayCommand MergeSelectionCommand { get; private set; } = null!;
    public IRelayCommand ApplyOffsetCommand { get; private set; } = null!;
    public IRelayCommand RepairSelectedSelfIntersectionCommand { get; private set; } = null!;
    public IRelayCommand FlattenSelectionToPolygonCommand { get; private set; } = null!;
    public IRelayCommand FlattenAllCurvesOnCurrentLayerCommand { get; private set; } = null!;
    public IRelayCommand FlattenAllCurvesCommand { get; private set; } = null!;

    private void InitBooleanCommands()
    {
        UnionCommand      = new RelayCommand(ApplyUnion,      () => CanBooleanOp);
        IntersectCommand  = new RelayCommand(ApplyIntersect,  () => CanBooleanOp);
        DifferenceCommand = new RelayCommand(ApplyDifference, () => CanBooleanOp);
        XorCommand        = new RelayCommand(ApplyXor,        () => CanBooleanOp);
        MergeSelectionCommand = new RelayCommand(ApplyMerge, () => CanMergeSelection);
        ApplyOffsetCommand = new RelayCommand(ApplyOffsetToSelection, () => CanOffsetSelection);
        RepairSelectedSelfIntersectionCommand = new RelayCommand(
            () => { if (_selectedIndices.Count == 1) RepairSelfIntersection(_selectedIndices[0]); },
            () => CanRepairSelected);
        FlattenSelectionToPolygonCommand = new RelayCommand(() => FlattenSelectionToPolygon(null), () => CanFlattenSelection);
        FlattenAllCurvesOnCurrentLayerCommand = new RelayCommand(() => FlattenAllCurves(CurrentLayerKey));
        FlattenAllCurvesCommand = new RelayCommand(() => FlattenAllCurves(null));
    }

    // ── Booleans (§3) ──────────────────────────────────────────────────────────

    public void ApplyUnion()      => ApplyBoolean(LayoutBooleans.Union,      "Union");
    public void ApplyIntersect()  => ApplyBoolean(LayoutBooleans.Intersect,  "Intersect");
    public void ApplyDifference() => ApplyBoolean(LayoutBooleans.Difference, "Difference");
    public void ApplyXor()        => ApplyBoolean(LayoutBooleans.Xor,        "XOR");

    /// <summary>Shared fold for Union/Intersect/Difference/XOR — operands are passed to
    /// <paramref name="op"/> in <see cref="_selectedIndices"/>'s own (click) order, NOT sorted by
    /// index: Difference's "first-selected minus the rest" depends on that order being preserved.</summary>
    private void ApplyBoolean(Func<IReadOnlyList<LayoutShape>, Technology?, LayoutBooleanResult> op, string opName)
    {
        var indices = ValidSelectedIndices;
        if (indices.Count < 2) return;

        var operands = indices.Select(i => Model.Shapes[i]).ToList();
        var result = op(operands, Technology);
        CommitReplace(indices, result.Shapes, opName);
        ReportOperandOutcome(result, opName, operands);
    }

    /// <summary>Union restricted to shapes sharing a layer (§3), applied per layer — every selected
    /// shape across every layer group folds into ONE undo entry.</summary>
    public void ApplyMerge()
    {
        var indices = ValidSelectedIndices;
        if (indices.Count == 0) return;

        var operands = indices.Select(i => Model.Shapes[i]).ToList();
        var groups = LayoutBooleans.Merge(operands, Technology);

        var added = new List<LayoutShape>();
        bool anyCurved = false, netsDiffered = false;
        foreach (var (_, result, _) in groups)
        {
            added.AddRange(result.Shapes);
            anyCurved |= result.AnyCurvedOperand;
            netsDiffered |= result.NetsDiffered;
        }

        CommitReplace(indices, added, "Merge");
        ReportOperandOutcome(new LayoutBooleanResult(added, anyCurved, netsDiffered), "Merge", operands);
    }

    // ── Offset (§3) ────────────────────────────────────────────────────────────

    private long _offsetDbu;
    [ObservableProperty] private string _offsetText = "";

    /// <summary>Staged dimension field (§1 R6) — unlike the other typed fields on this VM, a negative
    /// value is valid and meaningful here (shrink), so this does not require <c>dbu &gt;= 0</c>.</summary>
    public void CommitOffsetText(string text)
    {
        if (LayoutUnits.TryParse(text, DisplayUnit, Model.DbuPerMicron, out var dbu))
            _offsetDbu = dbu;
        OffsetText = LayoutUnits.Format(_offsetDbu, DisplayUnit, Model.DbuPerMicron);
    }

    /// <summary>Signed offset applied to each selected shape INDEPENDENTLY (§3) — one shape's result
    /// never depends on another's — folded into a single undo entry across the whole selection.</summary>
    public void ApplyOffsetToSelection()
    {
        var indices = ValidSelectedIndices;
        if (indices.Count == 0) return;

        var added = new List<LayoutShape>();
        bool anyCurved = false, anyAnnihilated = false;
        foreach (var i in indices)
        {
            var result = LayoutBooleans.Offset(Model.Shapes[i], _offsetDbu, Technology);
            added.AddRange(result.Shapes);
            anyCurved |= result.AnyCurvedOperand;
            if (result.Shapes.Count == 0) anyAnnihilated = true;
        }

        CommitReplace(indices, added, "Offset");

        if (anyCurved) WarnCurvedOperandOnce("Offset");
        if (anyAnnihilated)
            _messageSink?.Info("Offset removed one or more shapes entirely — the negative offset exceeded their extent.");
    }

    // ── Self-intersection repair (§4) ─────────────────────────────────────────

    /// <summary>A Clipper2 <c>Union</c> of the single shape against nothing — resolves crossings into
    /// a clean simple result, possibly several pieces, or one with holes (§0).</summary>
    public void RepairSelfIntersection(int shapeIndex)
    {
        if (shapeIndex < 0 || shapeIndex >= Model.Shapes.Count) return;
        var result = LayoutBooleans.Repair(Model.Shapes[shapeIndex], Technology);
        CommitReplace([shapeIndex], result.Shapes, "Repair Self-Intersection");
    }

    // ── Flatten to Polygon (§5) ────────────────────────────────────────────────

    /// <summary>Live vertex-count preview for the "Flatten to Polygon…" tolerance prompt — single
    /// selection only, mirroring <see cref="FindEdgeForContextMenu"/>'s single-selection convention.</summary>
    public int PreviewFlattenVertexCount(int shapeIndex, long tolDbu) =>
        shapeIndex >= 0 && shapeIndex < Model.Shapes.Count
            ? LayoutFlattenToPolygon.PreviewVertexCount(Model.Shapes[shapeIndex], tolDbu)
            : 0;

    /// <summary>Flattens every selected shape that has curved geometry, at <paramref name="tolDbuOverride"/>
    /// or (when null) each shape's own resolved tolerance — silently SKIPS shapes with nothing to
    /// flatten (§3.2 R9d), never an error. One undo entry for the whole selection.</summary>
    public void FlattenSelectionToPolygon(long? tolDbuOverride)
    {
        var indices = _selectedIndices.Where(HasCurvedGeometryAt).OrderBy(i => i).ToList();
        if (indices.Count == 0) return;

        var removed = new List<(int Index, LayoutShape Before)>();
        var added = new List<LayoutShape>();
        foreach (var i in indices)
        {
            var shape = Model.Shapes[i];
            long tol = tolDbuOverride ?? LayoutFlattener.ResolveTolDbu(shape, Technology);
            var flattened = LayoutFlattenToPolygon.FlattenToPolygon(shape, tol);
            if (flattened is null) continue;
            removed.Add((i, shape));
            added.Add(flattened);
        }
        if (removed.Count == 0) return;

        int insertAt = removed.Min(r => r.Index);
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, added, "Flatten to Polygon"));
        SetSelection(Enumerable.Range(insertAt, added.Count));
    }

    /// <summary>"Flatten All Curves" (§5) — on one layer (<paramref name="layerFilter"/> non-null) or
    /// the whole layout (null), for pre-export cleanup. One undo entry.</summary>
    public void FlattenAllCurves(LayerKey? layerFilter)
    {
        var indices = new List<int>();
        for (int i = 0; i < Model.Shapes.Count; i++)
        {
            if (layerFilter is { } lf && Model.Shapes[i].Layer != lf) continue;
            if (HasCurvedGeometryAt(i)) indices.Add(i);
        }
        if (indices.Count == 0) return;

        var removed = new List<(int Index, LayoutShape Before)>();
        var added = new List<LayoutShape>();
        foreach (var i in indices)
        {
            var shape = Model.Shapes[i];
            long tol = LayoutFlattener.ResolveTolDbu(shape, Technology);
            var flattened = LayoutFlattenToPolygon.FlattenToPolygon(shape, tol);
            if (flattened is null) continue;
            removed.Add((i, shape));
            added.Add(flattened);
        }
        if (removed.Count == 0) return;

        string desc = layerFilter is { } l ? $"Flatten All Curves ({LayerDisplayName(l)})" : "Flatten All Curves";
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, added, desc));
        _messageSink?.Success($"{desc}: {added.Count} shape(s) flattened.");
    }

    // ── Shared commit + reporting ─────────────────────────────────────────────

    private void CommitReplace(IReadOnlyList<int> removedIndices, IReadOnlyList<LayoutShape> added, string description)
    {
        var removed = removedIndices.Select(i => (i, Model.Shapes[i])).ToList();
        int insertAt = removed.Count > 0 ? removed.Min(r => r.i) : Model.Shapes.Count;
        Execute(new Commands.Layout.ReplaceShapesCommand(Model, removed, added, description));
        SetSelection(Enumerable.Range(insertAt, added.Count));
    }

    private void WarnCurvedOperandOnce(string opName)
    {
        if (_warnedCurvedOperandThisSession) return;
        _warnedCurvedOperandThisSession = true;
        _messageSink?.Warning($"{opName}: curved operand(s) were flattened to build this result.");
    }

    private void ReportOperandOutcome(LayoutBooleanResult result, string opName, IReadOnlyList<LayoutShape> operands)
    {
        if (opName == "Difference" && operands.Count > 0)
            _messageSink?.Info(
                $"Difference: {ShapeTypeName(operands[0])} · {LayerDisplayName(operands[0].Layer)} " +
                $"(selected first) minus {operands.Count - 1} other shape(s).");

        if (result.AnyCurvedOperand) WarnCurvedOperandOnce(opName);

        if (result.NetsDiffered)
            _messageSink?.Warning($"{opName}: operands were on different nets — net cleared.");

        if (result.Shapes.Count == 0)
            _messageSink?.Info($"{opName} produced no geometry.");
    }
}
