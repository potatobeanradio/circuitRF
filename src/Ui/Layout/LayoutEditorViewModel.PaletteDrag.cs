using System;
using System.IO;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// L5 §3 — dragging a PCell-eligible component straight from the Library Palette into a layout
/// (mirrors the schematic canvas's own palette drag, and reuses <see cref="PaletteDragPayload"/>
/// verbatim — R-L5-6). A separate state machine from the project-tree cell-drag ghost in
/// <c>LayoutEditorViewModel.Instances.cs</c> (there is no on-disk cell to resolve yet — R-L5-7: the
/// generator runs once, cached, purely in memory, and the generated cell is only ever written to
/// disk on an actual drop, via <see cref="TryPlaceNewInstance"/>, the SAME single placement path
/// schematic→layout uses).
/// </summary>
public sealed partial class LayoutEditorViewModel
{
    // Process-lifetime, purely in-memory — never touches disk (see the type doc comment). Sharing it
    // across drags/documents is harmless: it is keyed on (generator, params, tech, layers) exactly
    // like GeneratedCellStore's own content addressing, so a stale entry is never wrong, only reused.
    private static readonly PCellGeometryCache _paletteDragGeometryCache = new();

    private string? _paletteDragGeneratorId;
    private LayoutView? _paletteDragGhostView;
    private (long X, long Y)? _paletteDragPoint;

    /// <summary>R-L5-8: true only when <paramref name="kind"/> has a registered PCell generator — the
    /// canvas's DragOver handler sets <c>DragEffects = None</c> otherwise, before the drop, so the
    /// cursor itself says no for a <c>Term</c> or a <c>Var</c>.</summary>
    public bool CanDropPaletteComponent(SymbolKind kind, int portCount) =>
        SchematicToLayoutGenerator.HasPCellGenerator(kind, portCount, out _);

    /// <summary>Updates the live drag ghost to the current (already-snapped) point — called on every
    /// DragOver tick. The generator itself runs at most once per distinct (kind, portCount) within a
    /// drag (<see cref="_paletteDragGeometryCache"/>, keyed exactly like <see cref="GeneratedCellStore"/>'s
    /// own content addressing); every subsequent tick during the SAME drag only updates the ghost's
    /// position, never re-invoking it.</summary>
    public void UpdatePaletteDragGhost(SymbolKind kind, int portCount, long x, long y)
    {
        if (!SchematicToLayoutGenerator.HasPCellGenerator(kind, portCount, out var generatorId))
        {
            CancelPaletteDragGhost();
            return;
        }

        if (_paletteDragGhostView is null || !string.Equals(_paletteDragGeneratorId, generatorId, StringComparison.Ordinal))
        {
            if (!PCellRegistry.TryGet(generatorId, out var generator)) { CancelPaletteDragGhost(); return; }
            var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(kind, portCount);
            var result = _paletteDragGeometryCache.GetOrGenerate(generatorId, generator, defaults, Technology, PCellLayerSelection.Default);

            var ghostView = new LayoutView();
            ghostView.Shapes.AddRange(result.Shapes);
            _paletteDragGhostView = ghostView;
            _paletteDragGeneratorId = generatorId;
        }

        _paletteDragPoint = (x, y);
        RebuildOverlay();
    }

    /// <summary>Clears the drag ghost — DragLeave, or any drag ending without a drop.</summary>
    public void CancelPaletteDragGhost()
    {
        if (_paletteDragGhostView is null && _paletteDragPoint is null) return;
        _paletteDragGhostView = null;
        _paletteDragGeneratorId = null;
        _paletteDragPoint = null;
        RebuildOverlay();
    }

    /// <summary>
    /// Commits the drop: creates-or-reuses the generated cell for <paramref name="kind"/> at its
    /// DEFAULT parameters (R-L5-7) and places it via <see cref="TryPlaceNewInstance"/> — R-L5-6's "one
    /// placement path," so this can never diverge from what schematic→layout produces for the same
    /// component and parameters (gate 12). The resulting instance has no <c>SchematicId</c> (default
    /// null) — R-L5-6's own note: it was never in a schematic, so a later schematic→layout re-run must
    /// leave it alone rather than treating it as an orphan.
    /// </summary>
    public bool CommitPaletteDrop(SymbolKind kind, int portCount, long x, long y)
    {
        CancelPaletteDragGhost();

        if (!SchematicToLayoutGenerator.HasPCellGenerator(kind, portCount, out var generatorId))
            return false;

        if (WorkspaceRootDir is not { Length: > 0 } workspaceRoot)
        {
            _messageSink?.Error("Can't place this component — no workspace is open, and a generated PCell cell needs a workspace to live in.");
            return false;
        }

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(kind, portCount);
        string cellDir = GeneratedCellStore.GetOrCreate(
            workspaceRoot, generatorId, defaults, Technology, ResolvedTechPath, PCellLayerSelection.Default, out var diagnostics);
        GeneratedCellStore.RecordSnapshot(Model, cellDir, generatorId, defaults, ResolvedTechPath, PCellLayerSelection.Default);
        if (diagnostics is { Count: > 0 })
            foreach (var d in diagnostics) _messageSink?.Warning(d);

        string cellRef;
        try { cellRef = Path.GetRelativePath(InstanceBaseDir, cellDir); }
        catch { cellRef = cellDir; }

        return TryPlaceNewInstance(cellRef, x, y);
    }
}
