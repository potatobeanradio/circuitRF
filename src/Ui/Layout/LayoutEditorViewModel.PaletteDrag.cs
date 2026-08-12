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

    /// <summary>True when <paramref name="generatorId"/> is registered — the drag-over cursor's own
    /// yes/no for a tile that places a parametric cell by id rather than by <see cref="SymbolKind"/>.</summary>
    public bool CanDropPCellGenerator(string? generatorId) =>
        generatorId is { Length: > 0 } id && PCellRegistry.TryGet(id, out _);

    /// <summary>
    /// The live drag ghost for a generator dragged by id. Same caching as the SymbolKind path — the
    /// generator runs at most once per distinct id within one drag; every later tick only moves the
    /// ghost.
    /// </summary>
    public void UpdatePCellDragGhost(string generatorId, long x, long y)
    {
        if (!PCellRegistry.TryGet(generatorId, out var generator)) { CancelPaletteDragGhost(); return; }

        if (_paletteDragGhostView is null || !string.Equals(_paletteDragGeneratorId, generatorId, StringComparison.Ordinal))
        {
            var defaults = PCellRegistry.DeclaredDefaults(generatorId) ?? new Dictionary<string, PCellValue>();
            var result = _paletteDragGeometryCache.GetOrGenerate(
                generatorId, generator, defaults, Technology, PCellLayerSelection.Default);

            var ghostView = new LayoutView();
            ghostView.Shapes.AddRange(result.Shapes);
            _paletteDragGhostView = ghostView;
            _paletteDragGeneratorId = generatorId;
        }

        _paletteDragPoint = (x, y);
        RebuildOverlay();
    }

    /// <summary>Commits a drop of a generator dragged by id, at its own declared defaults.</summary>
    public bool CommitPCellDrop(string generatorId, long x, long y)
    {
        CancelPaletteDragGhost();
        return PlacePCell(generatorId, PCellRegistry.DeclaredDefaults(generatorId) ?? new Dictionary<string, PCellValue>(), x, y);
    }

    /// <summary>
    /// Every generator that can be placed right now — built-in and kit-contributed alike — with the
    /// parameters each would be placed at.
    ///
    /// <para>Built-in defaults come from the component that declares them; a script's come from its
    /// own <c>describe</c> (wire version 4). A generator declaring neither is still offered, with an
    /// empty set: it generates at whatever it falls back to, which is a usable cell — just not an
    /// adjustable one. Withholding it would hide a cell that works.</para>
    ///
    /// <para>Built-ins are gathered FIRST, matching <c>PCellRegistry.TryGet</c>'s own order, so a kit
    /// can never shadow <c>MLIN</c> here either.</para>
    /// </summary>
    public IReadOnlyList<(string Id, IReadOnlyDictionary<string, PCellValue> Parameters)> PlaceablePCells()
    {
        var byId = new SortedDictionary<string, IReadOnlyDictionary<string, PCellValue>>(StringComparer.OrdinalIgnoreCase);

        foreach (var kind in Enum.GetValues<SymbolKind>())
            if (SchematicToLayoutGenerator.HasPCellGenerator(kind, 0, out var builtInId))
                byId[builtInId] = SchematicToLayoutGenerator.ResolveDefaultParameters(kind, 0, Technology);

        foreach (string id in PCellRegistry.AllKnownGeneratorIds())
            if (!byId.ContainsKey(id))
                byId[id] = PCellRegistry.DeclaredDefaults(id) ?? new Dictionary<string, PCellValue>();

        return [.. byId.Select(kv => (kv.Key, kv.Value))];
    }

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
            var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(kind, portCount, Technology);
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

        return SchematicToLayoutGenerator.HasPCellGenerator(kind, portCount, out var generatorId)
            && PlacePCell(generatorId, SchematicToLayoutGenerator.ResolveDefaultParameters(kind, portCount, Technology), x, y);
    }

    /// <summary>
    /// Places a generated cell by GENERATOR ID and an explicit parameter set — the one placement
    /// path, reached both by a palette drop (which resolves a <see cref="SymbolKind"/> to an id
    /// first) and by placing a generator that has no <c>SymbolKind</c> at all, which is every cell a
    /// vendor kit contributes.
    ///
    /// <para><b>Why the id is the primitive and the SymbolKind is the caller.</b> Keying placement on
    /// <c>SymbolKind</c> made a kit's cells structurally unplaceable — they are discovered at run time
    /// and there is no enum member to give them. Inverting it costs nothing for the built-ins (they
    /// resolve their id and defaults exactly as before) and is the whole difference between a kit
    /// whose cells resolve and a kit whose cells can be used.</para>
    /// </summary>
    public bool PlacePCell(string generatorId, IReadOnlyDictionary<string, PCellValue> defaults, long x, long y)
        => ResolvePCellCellRef(generatorId, defaults) is { } cellRef && TryPlaceNewInstance(cellRef, x, y);

    /// <summary>
    /// Arms the ordinary instance-placement gesture for a generated cell — the user gets the same
    /// ghost-follows-cursor, click-to-commit flow every other instance placement uses, and the drop
    /// lands through the same <c>TryPlaceNewInstance</c>.
    ///
    /// <para>Reusing that gesture rather than inventing a PCell-specific one is the point: a placed
    /// generated cell IS an ordinary instance (its own cell folder happens to carry a
    /// <c>PCellOrigin</c>), so anything that behaved differently here would be a second placement
    /// path to keep in step for no gain.</para>
    /// </summary>
    public bool BeginPCellPlacement(string generatorId, IReadOnlyDictionary<string, PCellValue> defaults)
    {
        if (ResolvePCellCellRef(generatorId, defaults) is not { } cellRef) return false;
        BeginInstancePlacement(cellRef);
        return true;
    }

    /// <summary>Creates-or-reuses the generated cell and returns a <c>CellRef</c> relative to this
    /// document, or null when it could not be generated (reported, never thrown — this is reached
    /// from gestures).</summary>
    private string? ResolvePCellCellRef(string generatorId, IReadOnlyDictionary<string, PCellValue> defaults)
    {
        if (WorkspaceRootDir is not { Length: > 0 } workspaceRoot)
        {
            _messageSink?.Error("Can't place this component — no workspace is open, and a generated PCell cell needs a workspace to live in.");
            return null;
        }

        string cellDir;
        IReadOnlyList<string>? diagnostics;
        try
        {
            cellDir = GeneratedCellStore.GetOrCreate(
                workspaceRoot, generatorId, defaults, Technology, ResolvedTechPath, PCellLayerSelection.Default, out diagnostics);
        }
        catch (Exception ex)
        {
            // A generator can now be somebody's own script, so generation can fail — and this is a
            // DROP HANDLER: an exception escaping here takes the gesture (and possibly the app) down
            // rather than reporting anything. The message carries the script's own output, which is
            // usually the only description of what went wrong.
            _messageSink?.Error($"'{generatorId}' could not generate its artwork, so nothing was placed. {ex.Message}");
            return null;
        }

        GeneratedCellStore.RecordSnapshot(Model, cellDir, generatorId, defaults, ResolvedTechPath, PCellLayerSelection.Default);
        if (diagnostics is { Count: > 0 })
            foreach (var d in diagnostics) _messageSink?.Warning(d);

        string cellRef;
        try { cellRef = Path.GetRelativePath(InstanceBaseDir, cellDir); }
        catch { cellRef = cellDir; }

        return cellRef;
    }
}
