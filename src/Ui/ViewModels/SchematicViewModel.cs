using System.Diagnostics;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// ViewModel for a single schematic editing session (one Content tab).
/// All mutations route through the UndoRedoStack via Execute(IUiCommand).
/// </summary>
public sealed partial class SchematicViewModel : ObservableObject
{
    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly UndoRedoStack _undoRedo;
    private readonly IMessageSink? _messageSink;

    public SchematicEditModel EditModel  { get; }
    public SchematicSelection Selection  { get; } = new();

    // ── Render snapshot ───────────────────────────────────────────────────────

    [ObservableProperty] private SchematicModel?        _renderModel;
    [ObservableProperty] private SchematicSpatialIndex? _spatialIndex;
    [ObservableProperty] private SchematicOverlay        _overlay = SchematicOverlay.Empty;

    // ── Tool state ────────────────────────────────────────────────────────────

    public enum Tool { Select, Pan, Wire, Place, ZoomBox, MoveLabels }

    [ObservableProperty] private Tool   _activeTool  = Tool.Select;
    [ObservableProperty] private bool   _gridSnap    = true;
    [ObservableProperty] private bool   _keepConnect = true;

    private SymbolKind     _placementSymbol;
    private SymbolRotation _placementRot;
    private bool           _placementMirrorX;

    private readonly List<(double X, double Y)> _wirePoints = [];

    // ── Drag state ────────────────────────────────────────────────────────────

    // Per-drag wire info for SELECTED wires
    private sealed class WireDragInfo
    {
        public required IReadOnlyList<(double X, double Y)> StartPoints { get; init; }
        public required bool StartPinned { get; init; }   // endpoint 0 connected to unselected?
        public required bool EndPinned   { get; init; }   // endpoint N-1 connected to unselected?
    }

    private bool   _isDragging;
    private double _dragStartWorldX, _dragStartWorldY;
    private Dictionary<string, (double X, double Y)>?                _dragStartCompPositions;
    private Dictionary<string, WireDragInfo>?                        _dragWireInfo;
    private Dictionary<string, IReadOnlyList<(double X, double Y)>>? _dragUnselectedWirePoints;
    private Dictionary<string, (double X, double Y)>?                _dragStartObjPositions;

    // Rubber-band state
    private bool   _isRubberBanding;
    private double _rbStartX, _rbStartY;

    // Per-segment wire drag state (B1–B4)
    // Note: segment *selection* lives in SchematicSelection.SelectedSegments, not here.
    private bool   _isSegmentDrag;
    private string? _segmentDragWireId;
    private int     _segmentDragSegmentIndex;
    private IReadOnlyList<(double X, double Y)>? _segmentDragStartPoints;
    private bool    _segmentDragStartPinned;
    private bool    _segmentDragEndPinned;

    // T-junction stems riding the dragged through-segment: other wires whose endpoint lands
    // on the segment's interior. They follow the segment so the T stays intact (§5.1). Snapshot
    // once at drag start; moved live and folded into the same MoveCommand at commit.
    private List<StemFollow>? _segmentDragStems;

    /// <summary>A wire T-ed onto the dragged segment: which end is the junction, plus the
    /// original junction/far endpoints and the original full point list (for snapshot/restore).</summary>
    private readonly record struct StemFollow(
        EditableWire Wire,
        bool JunctionAtStart,
        (double X, double Y) JunctionPt,
        (double X, double Y) FarPt,
        IReadOnlyList<(double X, double Y)> StartPoints);

    // Move-Labels state
    private enum MoveLabelPhase { Picking, WaitFirstClick, Moving }
    private MoveLabelPhase          _moveLabelPhase;
    private List<EditableComponent> _moveLabelComps = [];
    private double _moveLabelRefX, _moveLabelRefY;

    // ── Inline editing ────────────────────────────────────────────────────────

    public enum InlineEditKind { None, ComponentType, ComponentName, ComponentParam, WireNetLabel }

    [ObservableProperty] private bool   _isInlineEditing;
    [ObservableProperty] private double _inlineEditScreenX;
    [ObservableProperty] private double _inlineEditScreenY;
    [ObservableProperty] private string _inlineEditValue = "";

    private InlineEditKind    _inlineEditKind  = InlineEditKind.None;
    private string?           _inlineEditTargetId;
    private EditableParameter? _inlineEditParam;
    private EditableNetLabel?  _inlineEditExistingNetLabel;
    private double             _inlineEditWorldX, _inlineEditWorldY;

    // ── Constructor ───────────────────────────────────────────────────────────

    public SchematicViewModel(SchematicEditModel editModel, UndoRedoStack undoRedo,
                              IMessageSink? messageSink = null)
    {
        EditModel    = editModel;
        _undoRedo    = undoRedo;
        _messageSink = messageSink;

        EditModel.Changed += (_, _) => RebuildRenderModel();
        Selection.Changed += (_, _) => RebuildOverlay();

        RebuildRenderModel();
    }

    // ── Command execution ─────────────────────────────────────────────────────

    public void Execute(IUiCommand cmd) => _undoRedo.Execute(cmd);

    // ── Render model rebuild ──────────────────────────────────────────────────

    private void RebuildRenderModel()
    {
        var (model, index) = EditModel.BuildRenderModel();
        RenderModel  = model;
        SpatialIndex = index;
        RebuildOverlay();
    }

    private void RebuildOverlay()
    {
        var selComps = Selection.GetSelectedComponentIds(EditModel).ToHashSet();
        var selWires = Selection.GetSelectedWireIds(EditModel).ToHashSet();
        var selObjs  = Selection.GetSelectedCanvasObjectIds(EditModel).ToHashSet();
        var selSegs  = Selection.GetSelectedSegments(EditModel).ToHashSet();

        Overlay = new SchematicOverlay
        {
            SelectedComponentIds = selComps,
            SelectedWireIds      = selWires,
            SelectedCanvasObjIds = selObjs,
            SelectedWireSegments = selSegs,
            WirePreview          = _wirePoints.Count > 0 ? _wirePoints.ToList() : null,
            Ghost                = ActiveTool == Tool.Place
                ? new PlacementGhost(0, 0, _placementSymbol, _placementRot, _placementMirrorX)
                : null,
            RubberBand           = _isRubberBanding ? Overlay.RubberBand : null,
            LabelDragOffsets     = ActiveTool == Tool.MoveLabels && _moveLabelPhase == MoveLabelPhase.Moving
                ? Overlay.LabelDragOffsets
                : null,
        };
    }

    // ── Tool selection ────────────────────────────────────────────────────────

    // Callback invoked with world (x0,y0,x1,y1) when a zoom-box is completed; set by SchematicCanvas.
    public Action<double, double, double, double>? ZoomToRectCallback { get; set; }

    [RelayCommand]
    public void SetSelectTool()  { ActiveTool = Tool.Select;  CancelCurrentOp(); }
    [RelayCommand]
    public void SetPanTool()     { ActiveTool = Tool.Pan;     CancelCurrentOp(); }
    [RelayCommand]
    public void SetWireTool()    { ActiveTool = Tool.Wire;    CancelCurrentOp(); }
    [RelayCommand]
    public void SetZoomBoxTool() { ActiveTool = Tool.ZoomBox; CancelCurrentOp(); }

    /// <summary>
    /// Enters Move-Labels mode.
    /// If components are selected, waits for the first click to set the reference point.
    /// Otherwise, waits for the user to click a component first.
    /// </summary>
    public void BeginMoveLabels()
    {
        // Snapshot current selection before CancelCurrentOp() clears it.
        var selected = Selection.GetSelectedComponentIds(EditModel)
            .Select(id => EditModel.FindComponent(id))
            .OfType<EditableComponent>().ToList();

        ActiveTool = Tool.MoveLabels;
        CancelCurrentOp();

        if (selected.Count > 0)
        {
            _moveLabelComps = selected;
            _moveLabelPhase = MoveLabelPhase.WaitFirstClick;
            _messageSink?.Info("Click to start moving labels, Esc to cancel");
        }
        else
        {
            _moveLabelComps = [];
            _moveLabelPhase = MoveLabelPhase.Picking;
            _messageSink?.Info("Select component label to move");
        }
    }

    /// <summary>
    /// Resets all label offsets to (0,0) for every selected component.
    /// Undoable via the MoveLabelsCommand with "Reset Label Position" description.
    /// </summary>
    public void ResetLabelOffsets()
    {
        var targets = Selection.GetSelectedComponentIds(EditModel)
            .Select(id => EditModel.FindComponent(id))
            .OfType<EditableComponent>()
            .Where(c => c.LabelOffsets.Any(o => o.DX != 0 || o.DY != 0))
            .ToList();

        if (targets.Count == 0) return;

        int LabelCount(EditableComponent c) => 2 + c.Parameters.Count(p => p.ShowOnSchematic);

        var snaps = targets.Select(c =>
        {
            int n = LabelCount(c);
            var oldOffsets = new List<(double DX, double DY)>(c.LabelOffsets);
            while (oldOffsets.Count < n) oldOffsets.Add((0, 0));
            var newOffsets = Enumerable.Repeat((0.0, 0.0), n).ToList();
            return new MoveLabelSnapshot(c, oldOffsets, newOffsets);
        });

        Execute(new MoveLabelsCommand(EditModel, snaps, "Reset Label Position"));
    }

    public void BeginPlacement(SymbolKind symbol)
    {
        _placementSymbol  = symbol;
        _placementRot     = SymbolRotation.R0;
        _placementMirrorX = false;
        ActiveTool = Tool.Place;
        CancelCurrentOp();
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    public void OnKeyDown(Key key, KeyModifiers modifiers)
    {
        bool ctrl = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        switch (key)
        {
            case Key.Escape: SetSelectTool(); Selection.Clear(); break;
            // A2: Enter finishes the in-progress wire (KEEP what's drawn) and returns to Select.
            case Key.Return:
                if (ActiveTool == Tool.Wire) { FinishWire(); ActiveTool = Tool.Select; }
                break;
            case Key.Z: SetZoomBoxTool(); break;
            case Key.F5: BeginMoveLabels(); break;
            case Key.Delete: case Key.Back: DeleteSelection(); break;
            case Key.R when !modifiers.HasFlag(KeyModifiers.Shift): RotateSelection(clockwise: false); break;
            case Key.R when  modifiers.HasFlag(KeyModifiers.Shift): RotateSelection(clockwise: true);  break;
            case Key.M when !modifiers.HasFlag(KeyModifiers.Shift): MirrorSelection(horizontal: true);  break;
            case Key.M when  modifiers.HasFlag(KeyModifiers.Shift): MirrorSelection(horizontal: false); break;
            case Key.A when ctrl: SelectAll(); break;
            case Key.Up:    NudgeSelection(0,  -GridStep(modifiers)); break;
            case Key.Down:  NudgeSelection(0,   GridStep(modifiers)); break;
            case Key.Left:  NudgeSelection(-GridStep(modifiers), 0); break;
            case Key.Right: NudgeSelection( GridStep(modifiers), 0); break;
        }
    }

    private double GridStep(KeyModifiers m) =>
        EditModel.GridSize * (m.HasFlag(KeyModifiers.Shift) ? 5 : 1);

    // ── Pointer events ────────────────────────────────────────────────────────

    public void OnPointerPressed(double worldX, double worldY, KeyModifiers modifiers,
                                 double screenX = 0, double screenY = 0)
    {
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);
        switch (ActiveTool)
        {
            case Tool.Select:     HandleSelectPress(worldX, worldY, shift, screenX, screenY); break;
            case Tool.Wire:       HandleWirePress(worldX, worldY);       break;
            case Tool.Place:      HandlePlacePress(worldX, worldY);      break;
            case Tool.ZoomBox:    HandleZoomBoxPress(worldX, worldY);    break;
            case Tool.MoveLabels: HandleMoveLabelPress(worldX, worldY, modifiers); break;
        }
    }

    public void OnPointerMoved(double worldX, double worldY, bool leftDown,
                               double screenX = 0, double screenY = 0,
                               KeyModifiers modifiers = default)
    {
        switch (ActiveTool)
        {
            case Tool.Select:     if (leftDown) HandleSelectDrag(worldX, worldY, modifiers); break;
            case Tool.Wire:       HandleWireMove(worldX, worldY);       break;
            case Tool.Place:      HandlePlaceMove(worldX, worldY);      break;
            case Tool.ZoomBox:    if (leftDown) HandleZoomBoxMove(worldX, worldY); break;
            case Tool.MoveLabels: HandleMoveLabelMove(worldX, worldY, modifiers); break;
        }
    }

    public void OnPointerReleased(double worldX, double worldY, KeyModifiers modifiers = default)
    {
        if      (ActiveTool == Tool.Select)  HandleSelectRelease(worldX, worldY, modifiers);
        else if (ActiveTool == Tool.ZoomBox) HandleZoomBoxRelease(worldX, worldY);
    }

    // ── Select tool ───────────────────────────────────────────────────────────

    private void HandleSelectPress(double wx, double wy, bool shift, double sx, double sy)
    {
        if (RenderModel is null || SpatialIndex is null) return;
        var hit = SchematicHitTest.Test(EditModel, RenderModel, SpatialIndex, wx, wy);

        // B1: Per-segment click — selects just that segment (not the whole wire).
        if (hit.Kind == SchematicHitTest.HitKind.WireSegment)
        {
            if (shift)
                Selection.ToggleSegment(hit.Id, hit.SubIndex);
            else
                Selection.SelectOneSegment(hit.Id, hit.SubIndex);
            // SelectOneSegment/ToggleSegment fires Changed → RebuildOverlay.

            var wire = EditModel.FindWire(hit.Id);
            if (wire is not null && wire.Points.Count >= 2)
            {
                _segmentDragWireId       = hit.Id;
                _segmentDragSegmentIndex = hit.SubIndex;
                _segmentDragStartPoints  = wire.Points.ToList();
                _segmentDragStartPinned  = hit.SubIndex == 0
                    && IsWireEndpointConnectedToUnselected(wire, 0);
                _segmentDragEndPinned    = hit.SubIndex == wire.Points.Count - 2
                    && IsWireEndpointConnectedToUnselected(wire, wire.Points.Count - 1);
                // Stems T-ed onto the dragged segment must follow it (§5.1) — detect now,
                // against the original segment geometry, so they ride along as it moves.
                _segmentDragStems = FindStemsOnSegment(
                    hit.Id, _segmentDragStartPoints[hit.SubIndex], _segmentDragStartPoints[hit.SubIndex + 1]);
                _isDragging      = false;
                _dragStartWorldX = wx;
                _dragStartWorldY = wy;
            }
            return;
        }

        // Any non-segment hit clears the segment selection and drag state.
        Selection.ClearSegmentsSilent();
        ClearSegmentDragState();

        if (hit.Kind == SchematicHitTest.HitKind.None)
        {
            if (!shift) Selection.Clear();
            _isRubberBanding = true;
            _rbStartX = wx; _rbStartY = wy;
        }
        else
        {
            if (shift)
                Selection.Toggle(hit.Id);
            else if (!Selection.IsSelected(hit.Id))
                Selection.SelectOne(hit.Id);

            _isDragging      = false;
            _dragStartWorldX = wx;
            _dragStartWorldY = wy;
            SnapshotDragStartPositions();
        }
    }

    private void HandleSelectDrag(double wx, double wy, KeyModifiers modifiers)
    {
        // B4: Segment drag takes priority — perpendicular-only live preview.
        if (_segmentDragWireId is not null && _segmentDragStartPoints is not null)
        {
            HandleSegmentDragLive(wx, wy);
            return;
        }

        if (_isRubberBanding)
        {
            Overlay = Overlay with
            {
                RubberBand = (Math.Min(_rbStartX, wx), Math.Min(_rbStartY, wy),
                              Math.Abs(wx - _rbStartX), Math.Abs(wy - _rbStartY)),
                RubberBandCrossing = wx < _rbStartX,
            };
            if (RenderModel is not null && SpatialIndex is not null)
            {
                var mode = wx < _rbStartX
                    ? SchematicHitTest.SelectMode.Crossing
                    : SchematicHitTest.SelectMode.Window;
                var hits = SchematicHitTest.TestRect(
                    EditModel, RenderModel, SpatialIndex,
                    _rbStartX, _rbStartY, wx, wy, mode);
                Selection.SetAllSilent(hits.Select(h => h.Id));
                Overlay = Overlay with
                {
                    SelectedComponentIds = Selection.GetSelectedComponentIds(EditModel).ToHashSet(),
                    SelectedWireIds      = Selection.GetSelectedWireIds(EditModel).ToHashSet(),
                    SelectedCanvasObjIds = Selection.GetSelectedCanvasObjectIds(EditModel).ToHashSet(),
                };
            }
            return;
        }

        bool hasDragState = (_dragStartCompPositions is not null && _dragStartCompPositions.Count > 0)
                         || (_dragWireInfo           is not null && _dragWireInfo.Count           > 0)
                         || (_dragStartObjPositions  is not null && _dragStartObjPositions.Count  > 0);
        if (!hasDragState || Selection.IsEmpty) return;

        double rawDx = wx - _dragStartWorldX;
        double rawDy = wy - _dragStartWorldY;
        const double threshold = 5.0;
        if (!_isDragging && Math.Sqrt(rawDx * rawDx + rawDy * rawDy) < threshold) return;
        _isDragging = true;

        // Shift: axis-lock to dominant direction. Components always stay on grid (no Ctrl bypass).
        var (dx, dy) = ApplyDragAxisLock(rawDx, rawDy, modifiers);

        // Move selected components
        if (_dragStartCompPositions is not null)
        {
            foreach (var (id, start) in _dragStartCompPositions)
            {
                var comp = EditModel.FindComponent(id);
                if (comp is not null)
                {
                    comp.X = EditModel.SnapToGrid(start.X + dx);
                    comp.Y = EditModel.SnapToGrid(start.Y + dy);
                }
            }
            // Live-update unselected wires connected to moved components
            UpdateConnectedWireEndpointsLive();
        }

        // Move selected wires (with endpoint pinning + orthogonal re-route)
        if (_dragWireInfo is not null)
        {
            foreach (var (id, info) in _dragWireInfo)
            {
                var wire = EditModel.FindWire(id);
                if (wire is not null) ApplyWireDragLive(wire, info, dx, dy);
            }
        }

        // Move selected canvas objects
        if (_dragStartObjPositions is not null)
        {
            foreach (var (id, start) in _dragStartObjPositions)
            {
                var obj = EditModel.FindCanvasObject(id);
                if (obj is not null)
                {
                    obj.X = EditModel.SnapToGrid(start.X + dx);
                    obj.Y = EditModel.SnapToGrid(start.Y + dy);
                }
            }
        }

        // Fast path: update overlay position overrides only (O(k)).
        // No full BuildRenderModel() per tick — connectivity + index deferred to drag-end.
        UpdateDragOverlay();
    }

    private void HandleSelectRelease(double wx, double wy, KeyModifiers modifiers)
    {
        if (_isRubberBanding)
        {
            FinishRubberBand(wx, wy);
            _isRubberBanding = false;
            Overlay = Overlay with { RubberBand = null };
            return;
        }

        // B4: Commit segment drag on release (B5: connectivity rebuild happens inside Execute).
        if (_segmentDragWireId is not null)
        {
            if (_isSegmentDrag)
                CommitSegmentDragAsCommand();
            // CommitSegmentDragAsCommand → Execute → NotifyChanged → RebuildRenderModel →
            // RebuildOverlay (with segment selection intact for step 2d).
            // For a non-drag release (just a click), we still need RebuildOverlay to show
            // the segment highlight — but if no drag happened, the model didn't change and
            // RebuildRenderModel was not called, so call RebuildOverlay explicitly.
            if (!_isSegmentDrag) RebuildOverlay();
            ClearSegmentDragState();
            return;
        }

        if (_isDragging)
        {
            CommitDragAsCommand(wx, wy, modifiers);
            // CommitDragAsCommand → Execute(MoveCommand) → NotifyChanged() → RebuildRenderModel()
            // which also calls RebuildOverlay() — so drag overrides are cleared by the full rebuild.
        }
        ClearDragState();
    }

    private void FinishRubberBand(double wx, double wy)
    {
        if (RenderModel is null || SpatialIndex is null) return;
        var mode = wx < _rbStartX
            ? SchematicHitTest.SelectMode.Crossing
            : SchematicHitTest.SelectMode.Window;
        var hits = SchematicHitTest.TestRect(
            EditModel, RenderModel, SpatialIndex,
            _rbStartX, _rbStartY, wx, wy, mode);
        Selection.SetAll(hits.Select(h => h.Id));
    }

    private void SnapshotDragStartPositions()
    {
        _dragStartCompPositions   = [];
        _dragWireInfo             = [];
        _dragStartObjPositions    = [];
        _dragUnselectedWirePoints = [];

        foreach (var id in Selection.Ids)
        {
            var comp = EditModel.FindComponent(id);
            if (comp is not null)
            {
                _dragStartCompPositions[id] = (comp.X, comp.Y);
                continue;
            }
            var wire = EditModel.FindWire(id);
            if (wire is not null)
            {
                _dragWireInfo[id] = new WireDragInfo
                {
                    StartPoints  = wire.Points.ToList(),
                    StartPinned  = wire.Points.Count > 0 && IsWireEndpointConnectedToUnselected(wire, 0),
                    EndPinned    = wire.Points.Count > 0 && IsWireEndpointConnectedToUnselected(wire, wire.Points.Count - 1),
                };
                continue;
            }
            var obj = EditModel.FindCanvasObject(id);
            if (obj is not null) _dragStartObjPositions[id] = (obj.X, obj.Y);
        }

        // Snapshot original points of all UNSELECTED wires (needed to compute re-routes on undo)
        foreach (var wire in EditModel.Wires)
        {
            if (Selection.IsSelected(wire.Id)) continue;
            if (wire.Points.Count < 2) continue;
            _dragUnselectedWirePoints[wire.Id] = wire.Points.ToList();
        }
    }

    /// <summary>
    /// Returns true if wire.Points[ptIdx] is connected to something not in the current selection.
    /// </summary>
    private bool IsWireEndpointConnectedToUnselected(EditableWire wire, int ptIdx)
    {
        const double tol = 8.0;
        if ((uint)ptIdx >= (uint)wire.Points.Count) return false;
        var (wx, wy) = wire.Points[ptIdx];

        foreach (var comp in EditModel.Components)
        {
            if (Selection.IsSelected(comp.Id)) continue;
            for (int pi = 0; pi < comp.PortCount; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
                if (SchematicGeometry.CoincidentPoints(wx, wy, px, py, tol)) return true;
            }
        }
        // Wire-to-wire endpoint coincidence intentionally does NOT pin a drag. Wires at
        // junctions can be freely dragged; merge-on-commit handles re-joining after a drag.
        return false;
    }

    // ── Wire endpoint merge helpers ───────────────────────────────────────────

    /// <summary>
    /// Returns the unique other wire whose endpoint is coincident with (x, y) within tol.
    /// Returns null if zero or two-or-more other wires match (no junction or 3+ junction).
    /// Wires in <paramref name="excludeIds"/> are skipped (moved wires, follow-wires).
    /// </summary>
    private EditableWire? FindUniqueEndpointMatch(
        string selfId, double x, double y, double tol,
        HashSet<string>? excludeIds = null)
    {
        EditableWire? found = null;
        int count = 0;
        foreach (var other in EditModel.Wires)
        {
            if (other.Id == selfId || other.Points.Count < 2) continue;
            if (excludeIds is not null && excludeIds.Contains(other.Id)) continue;
            if (SchematicGeometry.CoincidentPoints(x, y, other.Points[0].X, other.Points[0].Y, tol) ||
                SchematicGeometry.CoincidentPoints(x, y, other.Points[^1].X, other.Points[^1].Y, tol))
            {
                found = other;
                count++;
            }
        }
        return count == 1 ? found : null;
    }

    /// <summary>
    /// Checks both endpoints of <paramref name="endPoints"/> for a unique merge target.
    /// If found, builds and returns a WireMergeCommand; otherwise returns null.
    /// </summary>
    private WireMergeCommand? TryBuildMergeCommand(
        EditableWire wire,
        IReadOnlyList<(double X, double Y)> endPoints,
        int wireIndexInModel,
        HashSet<string>? excludeIds = null)
    {
        const double tol = 8.0;
        if (endPoints.Count < 2) return null;

        // Prefer end-endpoint (where the user just dragged / finished drawing).
        var target = FindUniqueEndpointMatch(wire.Id, endPoints[^1].X, endPoints[^1].Y, tol, excludeIds)
                  ?? FindUniqueEndpointMatch(wire.Id, endPoints[0].X,  endPoints[0].Y,  tol, excludeIds);
        if (target is null) return null;

        var mergedPts = WireGeometry.TryBuildMergedPoints(endPoints, target.Points, tol);
        if (mergedPts is null) return null;

        var merged = new EditableWire();
        merged.Points.AddRange(mergedPts);
        return new WireMergeCommand(EditModel, wire, wireIndexInModel, endPoints, target, merged);
    }

    /// <summary>
    /// Live-applies drag to a selected wire, keeping pinned endpoints fixed and re-routing.
    /// </summary>
    private void ApplyWireDragLive(EditableWire wire, WireDragInfo info, double dx, double dy)
    {
        if (info.StartPoints.Count < 2) return;

        if (!info.StartPinned && !info.EndPinned)
        {
            // Free wire: translate all points (stays orthogonal by construction)
            wire.Points.Clear();
            foreach (var (px, py) in info.StartPoints)
                wire.Points.Add((EditModel.SnapToGrid(px + dx), EditModel.SnapToGrid(py + dy)));
        }
        else if (info.StartPinned && !info.EndPinned)
        {
            var (sx, sy) = info.StartPoints[0];   // fixed start
            double ex = EditModel.SnapToGrid(info.StartPoints[^1].X + dx);
            double ey = EditModel.SnapToGrid(info.StartPoints[^1].Y + dy);
            ApplyOrthoRoute(wire, sx, sy, ex, ey);
        }
        else if (!info.StartPinned && info.EndPinned)
        {
            double sx = EditModel.SnapToGrid(info.StartPoints[0].X + dx);
            double sy = EditModel.SnapToGrid(info.StartPoints[0].Y + dy);
            var (ex, ey) = info.StartPoints[^1];  // fixed end
            ApplyOrthoRoute(wire, sx, sy, ex, ey);
        }
        // Both pinned: do not move (fully constrained)
    }

    /// <summary>
    /// Updates unselected wire endpoints live to follow dragged components.
    /// Uses the original endpoint snapshots so moves stay consistent across frames.
    /// </summary>
    private void UpdateConnectedWireEndpointsLive()
    {
        if (_dragStartCompPositions is null || _dragUnselectedWirePoints is null) return;
        const double tol = 8.0;

        // Build map: original port world position → current (new) port world position
        var portMoves = new List<(double Ox, double Oy, double Nx, double Ny)>();
        foreach (var (id, start) in _dragStartCompPositions)
        {
            var comp = EditModel.FindComponent(id);
            if (comp is null) continue;
            var portDefs = SymbolPortDefs.For(comp.Symbol);
            for (int pi = 0; pi < portDefs.Length; pi++)
            {
                var (ox, oy) = SchematicGeometry.LocalToWorld(
                    portDefs[pi].LocalX, portDefs[pi].LocalY,
                    start.X, start.Y, comp.Rotation, comp.MirrorX);
                var (nx, ny) = SchematicGeometry.LocalToWorld(
                    portDefs[pi].LocalX, portDefs[pi].LocalY,
                    comp.X, comp.Y, comp.Rotation, comp.MirrorX);
                portMoves.Add((ox, oy, nx, ny));
            }
        }
        if (portMoves.Count == 0) return;

        foreach (var wire in EditModel.Wires)
        {
            if (Selection.IsSelected(wire.Id)) continue;
            if (!_dragUnselectedWirePoints.TryGetValue(wire.Id, out var orig)) continue;
            if (orig.Count < 2) continue;

            double newSX = orig[0].X,   newSY = orig[0].Y;
            double newEX = orig[^1].X,  newEY = orig[^1].Y;
            bool changed = false;

            foreach (var (ox, oy, nx, ny) in portMoves)
            {
                if (SchematicGeometry.CoincidentPoints(orig[0].X, orig[0].Y, ox, oy, tol))
                { newSX = nx; newSY = ny; changed = true; }
                if (SchematicGeometry.CoincidentPoints(orig[^1].X, orig[^1].Y, ox, oy, tol))
                { newEX = nx; newEY = ny; changed = true; }
            }

            if (changed) ApplyOrthoRoute(wire, newSX, newSY, newEX, newEY);
        }
    }

    private void CommitDragAsCommand(double wx, double wy, KeyModifiers modifiers)
    {
        var (dx, dy) = ApplyDragAxisLock(wx - _dragStartWorldX, wy - _dragStartWorldY, modifiers);

        var compSnaps      = new List<ComponentMoveSnapshot>();
        var selWireSnaps   = new List<WireMoveSnapshot>();
        var objSnaps       = new List<CanvasObjectMoveSnapshot>();

        // Component moves
        if (_dragStartCompPositions is not null)
        {
            foreach (var (id, start) in _dragStartCompPositions)
            {
                var comp = EditModel.FindComponent(id);
                if (comp is null) continue;
                double ex = EditModel.SnapToGrid(start.X + dx);
                double ey = EditModel.SnapToGrid(start.Y + dy);
                compSnaps.Add(new ComponentMoveSnapshot(comp, start.X, start.Y, ex, ey));
            }
        }

        // Selected wire moves (with pinning + orthogonal re-route)
        if (_dragWireInfo is not null)
        {
            foreach (var (id, info) in _dragWireInfo)
            {
                var wire = EditModel.FindWire(id);
                if (wire is null) continue;
                var endPts = ComputeWireDragEndPoints(info, dx, dy);
                selWireSnaps.Add(new WireMoveSnapshot(wire, info.StartPoints, endPts));
            }
        }

        // Canvas obj moves
        if (_dragStartObjPositions is not null)
        {
            foreach (var (id, start) in _dragStartObjPositions)
            {
                var obj = EditModel.FindCanvasObject(id);
                if (obj is null) continue;
                double ex = EditModel.SnapToGrid(start.X + dx);
                double ey = EditModel.SnapToGrid(start.Y + dy);
                objSnaps.Add(new CanvasObjectMoveSnapshot(obj, start.X, start.Y, ex, ey));
            }
        }

        if (compSnaps.Count == 0 && selWireSnaps.Count == 0 && objSnaps.Count == 0) return;

        // Follow-wire moves: unselected wires re-routed to track dragged component ports
        var followWireSnaps = new List<WireMoveSnapshot>();
        if (compSnaps.Count > 0 && _dragUnselectedWirePoints is not null)
        {
            var selectedWireIds = selWireSnaps.Select(s => s.Wire.Id).ToHashSet();
            var portMoves       = BuildPortMoves(compSnaps);
            const double tol    = 8.0;

            foreach (var wire in EditModel.Wires)
            {
                if (selectedWireIds.Contains(wire.Id)) continue;
                if (!_dragUnselectedWirePoints.TryGetValue(wire.Id, out var orig)) continue;
                if (orig.Count < 2) continue;

                double newSX = orig[0].X,  newSY = orig[0].Y;
                double newEX = orig[^1].X, newEY = orig[^1].Y;
                bool changed = false;

                foreach (var (ox, oy, nx, ny) in portMoves)
                {
                    if (SchematicGeometry.CoincidentPoints(orig[0].X, orig[0].Y, ox, oy, tol))
                    { newSX = nx; newSY = ny; changed = true; }
                    if (SchematicGeometry.CoincidentPoints(orig[^1].X, orig[^1].Y, ox, oy, tol))
                    { newEX = nx; newEY = ny; changed = true; }
                }

                if (!changed) continue;
                var newRoute = WireGeometry.OrthogonalRoute(newSX, newSY, newEX, newEY);
                followWireSnaps.Add(new WireMoveSnapshot(wire, orig, newRoute));
            }
        }

        // Check for endpoint merge on a single-wire-only drag (most common case).
        // Multi-wire or component drags are excluded to avoid complex index bookkeeping.
        WireMergeCommand? mergeCmd = null;
        if (compSnaps.Count == 0 && selWireSnaps.Count == 1 && objSnaps.Count == 0)
        {
            var snap = selWireSnaps[0];
            var excludeIds = followWireSnaps.Select(s => s.Wire.Id).ToHashSet();
            mergeCmd = TryBuildMergeCommand(
                snap.Wire, snap.EndPoints, EditModel.Wires.IndexOf(snap.Wire), excludeIds);
        }

        // Restore everything to start state so MoveCommand.Execute() applies cleanly
        foreach (var s in compSnaps)    { s.Component.X = s.StartX; s.Component.Y = s.StartY; }
        foreach (var s in selWireSnaps) RestoreWirePoints(s.Wire, s.StartPoints);
        foreach (var s in objSnaps)     { s.Object.X = s.StartX; s.Object.Y = s.StartY; }
        foreach (var s in followWireSnaps) RestoreWirePoints(s.Wire, s.StartPoints);

        var allWireSnaps = selWireSnaps.Concat(followWireSnaps).ToList();
        var moveCmd = new MoveCommand(EditModel, compSnaps, allWireSnaps, objSnaps);

        if (mergeCmd is not null)
        {
            Execute(new CompositeCommand(moveCmd, mergeCmd));
            Selection.SelectOne(mergeCmd.MergedWireId);
            Selection.ClearSegmentsSilent();
        }
        else
        {
            Execute(moveCmd);
        }
    }

    private IReadOnlyList<(double, double)> ComputeWireDragEndPoints(WireDragInfo info, double dx, double dy)
    {
        if (!info.StartPinned && !info.EndPinned)
        {
            return info.StartPoints
                .Select(p => (EditModel.SnapToGrid(p.X + dx), EditModel.SnapToGrid(p.Y + dy)))
                .ToList<(double, double)>();
        }
        if (info.StartPinned && !info.EndPinned)
        {
            var (sx, sy) = info.StartPoints[0];
            return WireGeometry.OrthogonalRoute(
                sx, sy,
                EditModel.SnapToGrid(info.StartPoints[^1].X + dx),
                EditModel.SnapToGrid(info.StartPoints[^1].Y + dy));
        }
        if (!info.StartPinned && info.EndPinned)
        {
            var (ex, ey) = info.StartPoints[^1];
            return WireGeometry.OrthogonalRoute(
                EditModel.SnapToGrid(info.StartPoints[0].X + dx),
                EditModel.SnapToGrid(info.StartPoints[0].Y + dy),
                ex, ey);
        }
        return info.StartPoints; // both pinned — no change
    }

    private static List<(double Ox, double Oy, double Nx, double Ny)> BuildPortMoves(
        List<ComponentMoveSnapshot> compSnaps)
    {
        var moves = new List<(double, double, double, double)>();
        foreach (var cs in compSnaps)
        {
            var portDefs = SymbolPortDefs.For(cs.Component.Symbol);
            for (int pi = 0; pi < portDefs.Length; pi++)
            {
                var (ox, oy) = SchematicGeometry.LocalToWorld(
                    portDefs[pi].LocalX, portDefs[pi].LocalY,
                    cs.StartX, cs.StartY, cs.Component.Rotation, cs.Component.MirrorX);
                var (nx, ny) = SchematicGeometry.LocalToWorld(
                    portDefs[pi].LocalX, portDefs[pi].LocalY,
                    cs.EndX, cs.EndY, cs.Component.Rotation, cs.Component.MirrorX);
                moves.Add((ox, oy, nx, ny));
            }
        }
        return moves;
    }

    private static void ApplyOrthoRoute(EditableWire wire, double sx, double sy, double ex, double ey)
    {
        var route = WireGeometry.OrthogonalRoute(sx, sy, ex, ey);
        wire.Points.Clear();
        wire.Points.AddRange(route);
    }

    private static void RestoreWirePoints(EditableWire wire, IReadOnlyList<(double X, double Y)> pts)
    {
        wire.Points.Clear();
        wire.Points.AddRange(pts);
    }

    /// <summary>
    /// Fast drag tick update: writes position overrides into the overlay (O(k) for k moved objects)
    /// so the renderer draws at new positions without rebuilding the 10k-item SchematicModel.
    /// Connectivity and spatial-index rebuild are deferred to drag-end.
    /// </summary>
    private void UpdateDragOverlay()
    {
        // Component position overrides (O(k))
        Dictionary<string, (double X, double Y)>? compOverrides = null;
        if (_dragStartCompPositions is { Count: > 0 })
        {
            compOverrides = new(_dragStartCompPositions.Count);
            foreach (var id in _dragStartCompPositions.Keys)
            {
                var comp = EditModel.FindComponent(id);
                if (comp is not null)
                    compOverrides[id] = (comp.X, comp.Y);
            }
        }

        // Wire point overrides: selected wires + follow-wires (O(k))
        Dictionary<string, IReadOnlyList<(double X, double Y)>>? wireOverrides = null;
        bool hasWireDrag = (_dragWireInfo is { Count: > 0 }) || (_dragUnselectedWirePoints is { Count: > 0 });
        if (hasWireDrag)
        {
            wireOverrides = new();
            if (_dragWireInfo is not null)
            {
                foreach (var id in _dragWireInfo.Keys)
                {
                    var wire = EditModel.FindWire(id);
                    if (wire is not null)
                        wireOverrides[id] = wire.Points.ToList();
                }
            }
            if (_dragUnselectedWirePoints is not null)
            {
                foreach (var wire in EditModel.Wires)
                {
                    if (!_dragUnselectedWirePoints.ContainsKey(wire.Id)) continue;
                    wireOverrides[wire.Id] = wire.Points.ToList();
                }
            }
        }

        // Push overlay update — canvas watches Overlay property change → InvalidateVisual().
        // No BuildRenderModel() call.
        Overlay = new SchematicOverlay
        {
            SelectedComponentIds   = Selection.GetSelectedComponentIds(EditModel).ToHashSet(),
            SelectedWireIds        = Selection.GetSelectedWireIds(EditModel).ToHashSet(),
            SelectedCanvasObjIds   = Selection.GetSelectedCanvasObjectIds(EditModel).ToHashSet(),
            SelectedWireSegments   = Selection.GetSelectedSegments(EditModel).ToHashSet(),
            ComponentDragPositions = compOverrides,
            WireDragPoints         = wireOverrides,
        };
    }

    private void ClearDragState()
    {
        _isDragging               = false;
        _dragStartCompPositions   = null;
        _dragWireInfo             = null;
        _dragUnselectedWirePoints = null;
        _dragStartObjPositions    = null;
    }

    // ── Per-segment wire drag (B2–B5) ─────────────────────────────────────────

    private void ClearSegmentDragState()
    {
        _isSegmentDrag          = false;
        _segmentDragWireId      = null;
        _segmentDragStartPoints = null;
        _segmentDragStems       = null;
    }

    /// <summary>Sequence-equal comparison of two wire point lists (exact, within float epsilon).</summary>
    private static bool SamePoints(
        IReadOnlyList<(double X, double Y)> a, IReadOnlyList<(double X, double Y)> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (Math.Abs(a[i].X - b[i].X) > 1e-6 || Math.Abs(a[i].Y - b[i].Y) > 1e-6) return false;
        return true;
    }

    /// <summary>
    /// Live-applies perpendicular drag to the selected wire segment.
    /// Horizontal segments move only vertically; vertical segments only horizontally (B2).
    /// Pinned outer endpoints re-route via OrthogonalRoute (B3).
    /// </summary>
    private void HandleSegmentDragLive(double wx, double wy)
    {
        if (_segmentDragWireId is null || _segmentDragStartPoints is null) return;

        double rawDx = wx - _dragStartWorldX;
        double rawDy = wy - _dragStartWorldY;

        const double threshold = 3.0;
        if (!_isSegmentDrag && Math.Sqrt(rawDx * rawDx + rawDy * rawDy) < threshold) return;
        _isSegmentDrag = true;

        var wire = EditModel.FindWire(_segmentDragWireId);
        if (wire is null) return;

        int i   = _segmentDragSegmentIndex;
        var pts = _segmentDragStartPoints;
        if (i >= pts.Count - 1) return;

        bool isHoriz = IsSegmentHorizontal(pts[i], pts[i + 1]);

        // Constrain to perpendicular axis and snap (B2).
        double dx, dy;
        if (isHoriz)
        {
            double rawY = pts[i].Y + rawDy;
            dy = EditModel.SnapToGrid(rawY) - pts[i].Y;
            dx = 0;
        }
        else
        {
            double rawX = pts[i].X + rawDx;
            dx = EditModel.SnapToGrid(rawX) - pts[i].X;
            dy = 0;
        }

        var newPts = ComputeSegmentDragPoints(pts, i, dx, dy,
            _segmentDragStartPinned, _segmentDragEndPinned);

        wire.Points.Clear();
        wire.Points.AddRange(newPts);

        var wireDragPoints = new Dictionary<string, IReadOnlyList<(double X, double Y)>>
        {
            [_segmentDragWireId] = wire.Points.ToList(),
        };

        // T-junction stem follow (§5.1). When the segment translates rigidly (the base case —
        // both segment endpoints shift by the perpendicular delta), each stem riding it moves by
        // that same delta so its junction endpoint stays on the segment; re-route keeps the stem
        // orthogonal with its far (anchored) end fixed. In the pinned-endpoint re-route case the
        // segment's original line is preserved (an L-leg is added, not a translation), so stems on
        // it stay valid in place — they are intentionally not moved.
        bool rigidTranslate = !(i == 0 && _segmentDragStartPinned)
                           && !(i == pts.Count - 2 && _segmentDragEndPinned);
        if (rigidTranslate && _segmentDragStems is { Count: > 0 })
        {
            foreach (var stem in _segmentDragStems)
            {
                var routed = RouteStem(stem, dx, dy);
                stem.Wire.Points.Clear();
                stem.Wire.Points.AddRange(routed);
                wireDragPoints[stem.Wire.Id] = routed;
            }
        }

        // Fast overlay update — no full BuildRenderModel() per tick (B4 perf).
        Overlay = new SchematicOverlay
        {
            SelectedWireSegments = Selection.GetSelectedSegments(EditModel).ToHashSet(),
            SelectedComponentIds = Selection.GetSelectedComponentIds(EditModel).ToHashSet(),
            SelectedWireIds      = Selection.GetSelectedWireIds(EditModel).ToHashSet(),
            SelectedCanvasObjIds = Selection.GetSelectedCanvasObjectIds(EditModel).ToHashSet(),
            WireDragPoints       = wireDragPoints,
        };
    }

    /// <summary>
    /// Finds wires T-ed onto segment (a)-(b) of the through-wire <paramref name="throughWireId"/>:
    /// other wires with an endpoint on the segment's interior (the same PointOnSegmentInterior test
    /// BuildRenderModel uses for T-detection, at the identical tolerance). Endpoint-on-vertex is a
    /// coincidence/merge case, excluded by the interior test. Picks one junction end per stem
    /// (start preferred); the other end is the anchored far end.
    /// </summary>
    private List<StemFollow> FindStemsOnSegment(
        string throughWireId, (double X, double Y) a, (double X, double Y) b)
    {
        var stems = new List<StemFollow>();
        foreach (var w in EditModel.Wires)
        {
            if (w.Id == throughWireId || w.Points.Count < 2) continue;
            var p0 = w.Points[0];
            var pN = w.Points[^1];
            bool startOn = SchematicGeometry.PointOnSegmentInterior(
                p0.X, p0.Y, a.X, a.Y, b.X, b.Y, SchematicEditModel.ConnectTolerance);
            bool endOn = !startOn && SchematicGeometry.PointOnSegmentInterior(
                pN.X, pN.Y, a.X, a.Y, b.X, b.Y, SchematicEditModel.ConnectTolerance);
            if (!startOn && !endOn) continue;
            var junction = startOn ? p0 : pN;
            var far      = startOn ? pN : p0;
            stems.Add(new StemFollow(w, startOn, junction, far, w.Points.ToList()));
        }
        return stems;
    }

    /// <summary>Re-routes a stem after its junction endpoint moves by the perpendicular delta,
    /// keeping the far end fixed and the wire orthogonal (preserving point order).</summary>
    private static IReadOnlyList<(double X, double Y)> RouteStem(StemFollow stem, double dx, double dy)
    {
        double jx = stem.JunctionPt.X + dx, jy = stem.JunctionPt.Y + dy;
        return stem.JunctionAtStart
            ? WireGeometry.OrthogonalRoute(jx, jy, stem.FarPt.X, stem.FarPt.Y)
            : WireGeometry.OrthogonalRoute(stem.FarPt.X, stem.FarPt.Y, jx, jy);
    }

    /// <summary>
    /// Commits the segment drag as a single undoable MoveCommand (B4).
    /// Restores start state so Execute() applies the end state cleanly (B5: deferred connectivity).
    /// Step 4: simplifies the end points (merges collinear segments, drops zero-length ones).
    /// Step 2d: segment selection is preserved through the rebuild (not cleared here).
    /// </summary>
    private void CommitSegmentDragAsCommand()
    {
        if (_segmentDragWireId is null || _segmentDragStartPoints is null) return;
        var wire = EditModel.FindWire(_segmentDragWireId);
        if (wire is null) return;

        // Step 4: simplify end state before committing (collinear merges, zero-length removal).
        var endPoints = SimplifyWirePoints(wire.Points);

        // Check for endpoint merge BEFORE restoring (merge scans other wires for endpoint coincidence).
        int wireIdx  = EditModel.Wires.IndexOf(wire);
        var mergeCmd = TryBuildMergeCommand(wire, endPoints, wireIdx);

        // Fold T-junction stem follows into the same command — their Points were updated live;
        // restore each to its start so MoveCommand.Execute() re-applies the end state. One Undo
        // then restores the through-segment and every stem together.
        var wireSnaps = new List<WireMoveSnapshot>();
        if (_segmentDragStems is not null)
        {
            foreach (var stem in _segmentDragStems)
            {
                var stemEnd = stem.Wire.Points.ToList();
                if (SamePoints(stemEnd, stem.StartPoints)) continue;   // unmoved (pinned/no-op)
                stem.Wire.Points.Clear();
                stem.Wire.Points.AddRange(stem.StartPoints);
                wireSnaps.Add(new WireMoveSnapshot(stem.Wire, stem.StartPoints, stemEnd));
            }
        }

        // Restore the dragged wire to start state; MoveCommand.Execute() re-applies the end state.
        wire.Points.Clear();
        wire.Points.AddRange(_segmentDragStartPoints);

        wireSnaps.Insert(0, new WireMoveSnapshot(wire, _segmentDragStartPoints, endPoints));
        var moveCmd = new MoveCommand(EditModel, [], wireSnaps, []);

        if (mergeCmd is not null)
        {
            Execute(new CompositeCommand(moveCmd, mergeCmd));
            Selection.SelectOne(mergeCmd.MergedWireId);
            Selection.ClearSegmentsSilent();
        }
        else
        {
            Execute(moveCmd);
        }
    }

    /// <summary>
    /// Computes the new point list after dragging segment[i] by (dx,dy).
    /// Parallel component of the delta is zeroed out by the caller before this is called.
    /// Pinned outer endpoints are re-routed via OrthogonalRoute (B3).
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> ComputeSegmentDragPoints(
        IReadOnlyList<(double X, double Y)> startPoints,
        int i, double dx, double dy,
        bool startPinned, bool endPinned)
    {
        int n = startPoints.Count;
        if (n < 2 || i < 0 || i >= n - 1) return startPoints;

        // Both outer endpoints pinned on a single-segment wire → no movement.
        if (startPinned && endPinned && n == 2) return startPoints;

        // Shift pts[i] and pts[i+1] by the perpendicular delta (base case).
        var result = startPoints.ToList();
        result[i]     = (startPoints[i].X     + dx, startPoints[i].Y     + dy);
        result[i + 1] = (startPoints[i + 1].X + dx, startPoints[i + 1].Y + dy);

        // B3: First segment with pinned outer start → re-route from pts[0] to new pts[1].
        if (i == 0 && startPinned)
        {
            var rerouted = WireGeometry.OrthogonalRoute(
                startPoints[0].X, startPoints[0].Y,
                result[1].X, result[1].Y);
            var final = new List<(double X, double Y)>(rerouted);
            for (int k = 2; k < n; k++) final.Add(startPoints[k]);
            return final;
        }

        // B3: Last segment with pinned outer end → re-route from new pts[n-2] to pts[n-1].
        if (i == n - 2 && endPinned)
        {
            var rerouted = WireGeometry.OrthogonalRoute(
                result[n - 2].X, result[n - 2].Y,
                startPoints[n - 1].X, startPoints[n - 1].Y);
            var final = new List<(double X, double Y)>();
            for (int k = 0; k < n - 2; k++) final.Add(startPoints[k]);
            final.AddRange(rerouted);
            return final;
        }

        return result;
    }

    private static bool IsSegmentHorizontal((double X, double Y) a, (double X, double Y) b)
        => Math.Abs(a.X - b.X) >= Math.Abs(a.Y - b.Y);

    // ── Zoom-box tool ─────────────────────────────────────────────────────────

    private void HandleZoomBoxPress(double wx, double wy)
    {
        _isRubberBanding = true;
        _rbStartX = wx; _rbStartY = wy;
    }

    private void HandleZoomBoxMove(double wx, double wy)
    {
        Overlay = Overlay with
        {
            RubberBand = (Math.Min(_rbStartX, wx), Math.Min(_rbStartY, wy),
                          Math.Abs(wx - _rbStartX), Math.Abs(wy - _rbStartY)),
            RubberBandCrossing = false,
        };
    }

    private void HandleZoomBoxRelease(double wx, double wy)
    {
        _isRubberBanding = false;
        double x0 = Math.Min(_rbStartX, wx), x1 = Math.Max(_rbStartX, wx);
        double y0 = Math.Min(_rbStartY, wy), y1 = Math.Max(_rbStartY, wy);
        Overlay = Overlay with { RubberBand = null };
        if (x1 - x0 > 1 && y1 - y0 > 1)
            ZoomToRectCallback?.Invoke(x0, y0, x1, y1);
        SetSelectTool();
    }

    // ── Move-Labels tool ─────────────────────────────────────────────────────

    private void HandleMoveLabelPress(double wx, double wy, KeyModifiers modifiers)
    {
        switch (_moveLabelPhase)
        {
            case MoveLabelPhase.Picking:
            {
                if (RenderModel is null || SpatialIndex is null) return;
                var hit = SchematicHitTest.Test(EditModel, RenderModel, SpatialIndex, wx, wy);
                // Accept clicks on the component glyph body or any of its text labels.
                // Wire, dot, net-label, and empty-canvas clicks are ignored.
                if (hit.Kind is not (SchematicHitTest.HitKind.Component
                    or SchematicHitTest.HitKind.ComponentType
                    or SchematicHitTest.HitKind.ComponentName
                    or SchematicHitTest.HitKind.ComponentParam))
                {
                    _messageSink?.Info("Click on a component or its label to move labels, Esc to cancel");
                    return;
                }
                var comp = EditModel.FindComponent(hit.Id);
                if (comp is null) return;
                _moveLabelComps = [comp];
                _moveLabelRefX  = wx;
                _moveLabelRefY  = wy;
                _moveLabelPhase = MoveLabelPhase.Moving;
                _messageSink?.Info("Click to place labels, Esc to cancel");
                break;
            }
            case MoveLabelPhase.WaitFirstClick:
                _moveLabelRefX  = wx;
                _moveLabelRefY  = wy;
                _moveLabelPhase = MoveLabelPhase.Moving;
                _messageSink?.Info("Click to place labels, Esc to cancel");
                break;
            case MoveLabelPhase.Moving:
                CommitMoveLabels(wx, wy, modifiers);
                break;
        }
    }

    private void HandleMoveLabelMove(double wx, double wy, KeyModifiers modifiers)
    {
        if (_moveLabelPhase != MoveLabelPhase.Moving || _moveLabelComps.Count == 0) return;
        var (dx, dy) = ComputeLabelDelta(wx - _moveLabelRefX, wy - _moveLabelRefY,
                                         modifiers, EditModel.GridSize);
        var dict = _moveLabelComps.ToDictionary(c => c.Id, _ => (DX: dx, DY: dy));
        Overlay = Overlay with { LabelDragOffsets = dict };
    }

    private void CommitMoveLabels(double wx, double wy, KeyModifiers modifiers)
    {
        var (dx, dy) = ComputeLabelDelta(wx - _moveLabelRefX, wy - _moveLabelRefY,
                                         modifiers, EditModel.GridSize);
        var snaps = _moveLabelComps.Select(c =>
        {
            int labelCount = 2 + c.Parameters.Count(p => p.ShowOnSchematic);
            var oldOffsets = new List<(double DX, double DY)>(c.LabelOffsets);
            while (oldOffsets.Count < labelCount) oldOffsets.Add((0, 0));
            var newOffsets = oldOffsets.Select(o => (o.DX + dx, o.DY + dy)).ToList();
            return new MoveLabelSnapshot(c, oldOffsets, newOffsets);
        });
        Execute(new MoveLabelsCommand(EditModel, snaps));
        SetSelectTool();
    }

    /// <summary>
    /// Applies Shift axis-lock to a component drag delta.
    /// Components always land on grid (grid snap is applied per-component via SnapToGrid);
    /// no Ctrl bypass is provided — keeping components on-grid is non-negotiable.
    /// </summary>
    private static (double DX, double DY) ApplyDragAxisLock(double rawDx, double rawDy,
                                                             KeyModifiers modifiers = default)
    {
        if (!modifiers.HasFlag(KeyModifiers.Shift)) return (rawDx, rawDy);
        return Math.Abs(rawDy) >= Math.Abs(rawDx)
            ? (0,     rawDy)   // predominantly vertical   — lock X
            : (rawDx, 0);      // predominantly horizontal — lock Y
    }

    /// <summary>
    /// Applies grid-snap and axis-lock rules to a raw label drag delta.
    /// Default: snaps both axes to the nearest grid multiple.
    /// Ctrl held: free movement, no snap.
    /// Shift held: locks to the dominant axis first, then grid-snaps the free axis (unless Ctrl).
    /// </summary>
    private static (double DX, double DY) ComputeLabelDelta(
        double rawDx, double rawDy, KeyModifiers modifiers, double gridSize)
    {
        bool ctrl  = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool shift = modifiers.HasFlag(KeyModifiers.Shift);

        double dx = rawDx;
        double dy = rawDy;

        // Shift: constrain to the dominant axis; zero out the minor axis.
        if (shift)
        {
            if (Math.Abs(rawDy) >= Math.Abs(rawDx))
                dx = 0;   // predominantly vertical — lock X
            else
                dy = 0;   // predominantly horizontal — lock Y
        }

        // Grid snap (Ctrl overrides).
        if (!ctrl && gridSize > 0)
        {
            dx = Math.Round(dx / gridSize) * gridSize;
            dy = Math.Round(dy / gridSize) * gridSize;
        }

        return (dx, dy);
    }

    // ── Wire tool ─────────────────────────────────────────────────────────────

    private void HandleWirePress(double wx, double wy)
    {
        double sx = EditModel.SnapToGrid(wx);
        double sy = EditModel.SnapToGrid(wy);
        if (SpatialIndex is not null)
        {
            var (pFound, _, _, px, py) = SchematicHitTest.NearestPort(EditModel, wx, wy, 15);
            if (pFound) { sx = px; sy = py; }
            else
            {
                var (eFound, _, _, ex, ey) = SchematicHitTest.NearestWireEndpoint(EditModel, wx, wy, 15);
                if (eFound) { sx = ex; sy = ey; }
                else
                {
                    // Lowest-priority snap: project onto a wire's segment body so the
                    // endpoint lands exactly on another wire, forming a T-junction (§5.1).
                    // Grid-snapped to stay consistent with the rest of wire placement;
                    // orthogonal grid-aligned wires keep the projected point on the segment.
                    var (sFound, _, _, segX, segY) = SchematicHitTest.NearestPointOnWireSegment(EditModel, wx, wy, 15);
                    if (sFound) { sx = EditModel.SnapToGrid(segX); sy = EditModel.SnapToGrid(segY); }
                }
            }
        }
        if (_wirePoints.Count == 0)
        {
            _wirePoints.Add((sx, sy));
        }
        else
        {
            var lastPt = _wirePoints[_wirePoints.Count - 1];
            var route  = WireGeometry.OrthogonalRoute(lastPt.X, lastPt.Y, sx, sy);
            for (int i = 1; i < route.Count; i++) _wirePoints.Add(route[i]);

            // Merge any collinear continuation into the existing segment (Bug-2 fix).
            // A click collinear with the previous segment produces a redundant interior vertex;
            // NormalizePoints removes it so the polyline stays minimal and alternating H/V.
            var normalized = WireGeometry.NormalizePoints(_wirePoints);
            if (normalized.Count >= 2)
            {
                _wirePoints.Clear();
                _wirePoints.AddRange(normalized);
            }

            if (SpatialIndex is not null)
            {
                var (pfound, _, _, _, _) = SchematicHitTest.NearestPort(EditModel, sx, sy, 8);
                if (pfound) FinishWire();
            }
        }
        RebuildOverlay();
    }

    private void HandleWireMove(double wx, double wy)
    {
        if (_wirePoints.Count == 0) return;
        double sx = EditModel.SnapToGrid(wx);
        double sy = EditModel.SnapToGrid(wy);
        var previewPts = new List<(double X, double Y)>(_wirePoints);
        var lastPt = previewPts[previewPts.Count - 1];
        var route  = WireGeometry.OrthogonalRoute(lastPt.X, lastPt.Y, sx, sy);
        for (int i = 1; i < route.Count; i++) previewPts.Add(route[i]);
        Overlay = Overlay with { WirePreview = previewPts };
    }

    private void FinishWire()
    {
        if (_wirePoints.Count < 2) { _wirePoints.Clear(); RebuildOverlay(); return; }
        var normalized = WireGeometry.NormalizePoints(_wirePoints);
        _wirePoints.Clear();
        if (normalized.Count < 2) { RebuildOverlay(); return; }

        var wire = new EditableWire();
        wire.Points.AddRange(normalized);

        // wireA is not yet in the model; after PlaceWireCommand.Execute() it will be appended.
        var mergeCmd = TryBuildMergeCommand(wire, wire.Points, EditModel.Wires.Count);
        if (mergeCmd is not null)
        {
            Execute(new CompositeCommand(new PlaceWireCommand(EditModel, wire), mergeCmd));
            Selection.SelectOne(mergeCmd.MergedWireId);
            Selection.ClearSegmentsSilent();
        }
        else
        {
            Execute(new PlaceWireCommand(EditModel, wire));
        }
        RebuildOverlay();
    }

    // ── Place tool ────────────────────────────────────────────────────────────

    private void HandlePlacePress(double wx, double wy)
    {
        double sx = EditModel.SnapToGrid(wx);
        double sy = EditModel.SnapToGrid(wy);
        var comp = new EditableComponent
        {
            InstanceName = GenerateInstanceName(_placementSymbol),
            Symbol       = _placementSymbol,
            X = sx, Y = sy,
            Rotation     = _placementRot,
            MirrorX      = _placementMirrorX,
        };
        int portCount = SymbolPortDefs.For(_placementSymbol).Length;
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(_placementSymbol, portCount))
            comp.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic });
        Execute(new PlaceComponentCommand(EditModel, comp));
        Selection.SelectOne(comp.Id);
    }

    private void HandlePlaceMove(double wx, double wy)
    {
        double sx = EditModel.SnapToGrid(wx);
        double sy = EditModel.SnapToGrid(wy);
        Overlay = Overlay with
        {
            Ghost = new PlacementGhost(sx, sy, _placementSymbol, _placementRot, _placementMirrorX),
        };
    }

    private string GenerateInstanceName(SymbolKind symbol)
        => SchematicEditModel.NextAvailableName(EditModel.Components, symbol);

    // ── Edit actions ──────────────────────────────────────────────────────────

    public void DeleteSelection()
    {
        // Step 2b: if segments are specifically selected, delete those segments.
        if (Selection.HasSelectedSegments)
        {
            var segs = Selection.GetSelectedSegments(EditModel);
            if (segs.Count > 0)
            {
                Execute(new DeleteSegmentsCommand(EditModel, segs));
                Selection.Clear();
            }
            return;
        }
        var ids = Selection.Ids.ToList();
        if (ids.Count == 0) return;
        Execute(new DeleteCommand(EditModel, ids, newIds => Selection.SetAll(newIds)));
        Selection.Clear();
    }

    public void RotateSelection(bool clockwise = false)
    {
        var ids = Selection.Ids.ToList();
        if (ids.Count == 0)
        {
            _placementRot = clockwise
                ? _placementRot switch
                {
                    SymbolRotation.R0   => SymbolRotation.R270,
                    SymbolRotation.R270 => SymbolRotation.R180,
                    SymbolRotation.R180 => SymbolRotation.R90,
                    _                   => SymbolRotation.R0,
                }
                : _placementRot switch
                {
                    SymbolRotation.R0   => SymbolRotation.R90,
                    SymbolRotation.R90  => SymbolRotation.R180,
                    SymbolRotation.R180 => SymbolRotation.R270,
                    _                   => SymbolRotation.R0,
                };
            RebuildOverlay();
            return;
        }
        Execute(new RotateCommand(EditModel, ids, clockwise));
    }

    public void MirrorSelection(bool horizontal = true)
    {
        var ids = Selection.Ids.ToList();
        if (ids.Count == 0)
        {
            if (horizontal) _placementMirrorX = !_placementMirrorX;
            RebuildOverlay();
            return;
        }
        Execute(new MirrorCommand(EditModel, ids, horizontal));
    }

    public void NudgeSelection(double dx, double dy)
    {
        // Step 2c: if segments are specifically selected, nudge them perpendicular to their axis.
        if (Selection.HasSelectedSegments)
        {
            NudgeSelectedSegments(dx, dy);
            return;
        }
        var ids = Selection.Ids.ToList();
        if (ids.Count == 0) return;
        var compSnaps = new List<ComponentMoveSnapshot>();
        var objSnaps  = new List<CanvasObjectMoveSnapshot>();
        foreach (var id in ids)
        {
            var comp = EditModel.FindComponent(id);
            if (comp is not null)
            {
                compSnaps.Add(new ComponentMoveSnapshot(comp, comp.X, comp.Y, comp.X + dx, comp.Y + dy));
                continue;
            }
            var obj = EditModel.FindCanvasObject(id);
            if (obj is not null)
                objSnaps.Add(new CanvasObjectMoveSnapshot(obj, obj.X, obj.Y, obj.X + dx, obj.Y + dy));
        }
        if (compSnaps.Count > 0 || objSnaps.Count > 0)
        {
            var sw = Stopwatch.StartNew();
            Execute(new MoveCommand(EditModel, compSnaps, [], objSnaps));
            sw.Stop();
            // RebuildRenderModel() runs inside Execute → NotifyChanged.
            // Report to diagnostics so the measured rebuild time can be observed.
            Debug.WriteLine($"[Perf] NudgeSelection rebuild: {sw.ElapsedMilliseconds} ms " +
                            $"({EditModel.Components.Count} comps, {EditModel.Wires.Count} wires)");
        }
    }

    /// <summary>
    /// Nudges each selected segment perpendicular to its own axis (one grid step).
    /// Horizontal segment → moves vertically (Up/Down arrows).
    /// Vertical segment → moves horizontally (Left/Right arrows).
    /// Preserves pinned-endpoint constraint and simplifies the result.
    /// </summary>
    private void NudgeSelectedSegments(double dx, double dy)
    {
        var segments = Selection.GetSelectedSegments(EditModel);
        if (segments.Count == 0) return;

        var wireSnaps = new List<WireMoveSnapshot>();

        // Group by wire so we apply all selected segments on the same wire together.
        foreach (var group in segments.GroupBy(s => s.WireId))
        {
            var wire = EditModel.FindWire(group.Key);
            if (wire is null || wire.Points.Count < 2) continue;

            var startPoints = wire.Points.ToList();
            var pts         = startPoints.ToList();
            bool changed    = false;

            foreach (var (_, segIdx) in group)
            {
                if (segIdx >= pts.Count - 1) continue;

                bool isHoriz = IsSegmentHorizontal(pts[segIdx], pts[segIdx + 1]);

                // Perpendicular-only: horizontal segment moves vertically; vertical moves horizontally.
                double nudgeDx = isHoriz ? 0 : dx;
                double nudgeDy = isHoriz ? dy : 0;
                if (nudgeDx == 0 && nudgeDy == 0) continue;

                bool startPinned = segIdx == 0
                    && IsWireEndpointConnectedToUnselected(wire, 0);
                bool endPinned = segIdx == startPoints.Count - 2
                    && IsWireEndpointConnectedToUnselected(wire, startPoints.Count - 1);

                pts = ComputeSegmentDragPoints(pts, segIdx, nudgeDx, nudgeDy,
                          startPinned, endPinned).ToList();
                changed = true;
            }

            if (!changed) continue;

            var endPoints = SimplifyWirePoints(pts);
            wireSnaps.Add(new WireMoveSnapshot(wire, startPoints, endPoints));
        }

        if (wireSnaps.Count > 0)
            Execute(new MoveCommand(EditModel, [], wireSnaps, []));
    }

    /// <summary>
    /// Normalizes wire points after a segment move: delegates to WireGeometry.NormalizePoints
    /// and applies the "never shrink below 2 points" safety guard so a valid wire stays valid.
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> SimplifyWirePoints(
        IReadOnlyList<(double X, double Y)> pts)
    {
        var result = WireGeometry.NormalizePoints(pts);
        return result.Count >= 2 ? result : pts;
    }

    public void SelectAll()
    {
        var ids = EditModel.Components.Select(c => c.Id)
            .Concat(EditModel.Wires.Select(w => w.Id))
            .Concat(EditModel.CanvasObjects.Select(o => o.Id));
        Selection.SetAll(ids);
    }

    public void DisableSelection(DisableState state)
    {
        var ids = Selection.Ids.ToList();
        if (ids.Count == 0) return;
        Execute(new SetDisableStateCommand(EditModel, ids, state));
    }

    public void SelectIfUnselected(string id)
    {
        if (!Selection.IsSelected(id)) Selection.SelectOne(id);
    }

    // ── Inline editing ────────────────────────────────────────────────────────

    public void BeginInlineEditForHit(SchematicHitTest.HitResult hit, double screenX, double screenY)
    {
        var comp = EditModel.FindComponent(hit.Id);
        if (comp is null) return;

        switch (hit.Kind)
        {
            case SchematicHitTest.HitKind.ComponentType:
                SetInlineEdit(InlineEditKind.ComponentType, hit.Id,
                    ComponentTypeRegistry.DisplayName(comp.Symbol, comp.PortCount), screenX, screenY);
                break;
            case SchematicHitTest.HitKind.ComponentName:
                SetInlineEdit(InlineEditKind.ComponentName, hit.Id, comp.InstanceName, screenX, screenY);
                break;
            case SchematicHitTest.HitKind.ComponentParam:
                var param = comp.Parameters.ElementAtOrDefault(hit.SubIndex);
                if (param is null) return;
                _inlineEditParam = param;
                SetInlineEdit(InlineEditKind.ComponentParam, hit.Id,
                    ParamInlineInitValue(param), screenX, screenY);
                break;
        }
    }

    public void BeginWireNodeLabelEdit(
        string wireId, double worldX, double worldY, double screenX, double screenY)
    {
        var existing = EditModel.NetLabels.FirstOrDefault(l =>
            Math.Abs(l.X - worldX) < 150 && Math.Abs(l.Y - worldY) < 80);
        _inlineEditExistingNetLabel = existing;
        _inlineEditWorldX = worldX;
        _inlineEditWorldY = worldY;
        SetInlineEdit(InlineEditKind.WireNetLabel, wireId, existing?.Name ?? "", screenX, screenY);
    }

    public void BeginInlineEdit(EditableComponent comp, EditableParameter param,
                                double screenX, double screenY)
    {
        _inlineEditParam = param;
        SetInlineEdit(InlineEditKind.ComponentParam, comp.Id,
            ParamInlineInitValue(param), screenX, screenY);
    }

    /// <summary>Initial text for the inline edit box: "Expression Unit" (unit omitted when empty).</summary>
    private static string ParamInlineInitValue(EditableParameter p)
        => string.IsNullOrEmpty(p.Unit) ? p.Expression : $"{p.Expression} {p.Unit}";

    private void SetInlineEdit(InlineEditKind kind, string targetId, string value,
                               double screenX, double screenY)
    {
        _inlineEditKind     = kind;
        _inlineEditTargetId = targetId;
        InlineEditValue     = value;
        InlineEditScreenX   = screenX;
        InlineEditScreenY   = screenY;
        IsInlineEditing     = true;
    }

    public void CommitInlineEdit()
    {
        string newVal = InlineEditValue.Trim();
        switch (_inlineEditKind)
        {
            case InlineEditKind.ComponentType:
            {
                var comp = EditModel.FindComponent(_inlineEditTargetId ?? "");
                if (comp is null) break;
                if (!ComponentTypeRegistry.TryParseCode(newVal, out var newKind))
                {
                    _messageSink?.Warning($"Unknown component type: '{newVal}' — use R, L, C, V, GND, FET, Z2P, …");
                    break;
                }
                if (newKind != comp.Symbol)
                {
                    // Build the replacement component: same position/rotation/mirror, new Id/name/params.
                    // Exclude the old component from naming so its slot is treated as free.
                    string prefix    = ComponentTypeRegistry.InstancePrefix(newKind);
                    var    remaining = EditModel.Components.Where(c => c.Id != comp.Id);
                    string newName   = SchematicEditModel.NextAvailableName(remaining, prefix);
                    int    portCount = SymbolPortDefs.For(newKind).Length;
                    var    newComp   = new EditableComponent
                    {
                        InstanceName = newName,
                        Symbol       = newKind,
                        X = comp.X, Y = comp.Y,
                        Rotation     = comp.Rotation,
                        MirrorX      = comp.MirrorX,
                    };
                    foreach (var dp in ComponentTypeRegistry.DefaultParameters(newKind, portCount))
                        newComp.Parameters.Add(new EditableParameter
                            { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic });
                    Execute(new ChangeComponentTypeCommand(EditModel, comp, newComp));
                }
                break;
            }
            case InlineEditKind.ComponentName:
            {
                var comp = EditModel.FindComponent(_inlineEditTargetId ?? "");
                if (comp is null || newVal.Length == 0 || newVal == comp.InstanceName) break;
                Execute(new RenameComponentCommand(comp, newVal));
                EditModel.NotifyChanged();
                break;
            }
            case InlineEditKind.ComponentParam:
            {
                if (_inlineEditParam is null) break;
                if (newVal.Length > 0)
                {
                    var (expr, unit) = ParseExpressionUnit(newVal);
                    if (expr != _inlineEditParam.Expression || unit != _inlineEditParam.Unit)
                    {
                        Execute(new EditParameterCommand(_inlineEditParam, expr, unit));
                        EditModel.NotifyChanged();
                    }
                }
                break;
            }
            case InlineEditKind.WireNetLabel:
            {
                if (newVal.Length == 0) break;
                if (_inlineEditExistingNetLabel is not null)
                {
                    if (newVal != _inlineEditExistingNetLabel.Name)
                    {
                        Execute(new RenameNetLabelCommand(_inlineEditExistingNetLabel, newVal));
                        EditModel.NotifyChanged();
                    }
                }
                else
                {
                    double sx = EditModel.SnapToGrid(_inlineEditWorldX);
                    double sy = EditModel.SnapToGrid(_inlineEditWorldY);
                    Execute(new PlaceNetLabelCommand(EditModel,
                        new EditableNetLabel { Name = newVal, X = sx, Y = sy }));
                }
                break;
            }
        }
        CancelInlineEdit();
    }

    public void CancelInlineEdit()
    {
        IsInlineEditing             = false;
        _inlineEditKind             = InlineEditKind.None;
        _inlineEditTargetId         = null;
        _inlineEditParam            = null;
        _inlineEditExistingNetLabel = null;
        InlineEditValue             = "";
    }

    /// <summary>
    /// Splits "2.5 nH" → ("2.5", "nH"). The last whitespace-separated token is treated as
    /// a unit if it starts with a letter (e.g. "nH", "pF", "ohm"); otherwise the whole string
    /// is the expression and the unit is empty.
    /// </summary>
    private static (string Expression, string Unit) ParseExpressionUnit(string raw)
    {
        raw = raw.Trim();
        int lastSpace = raw.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            string tail = raw[(lastSpace + 1)..];
            if (tail.Length > 0 && char.IsLetter(tail[0]))
                return (raw[..lastSpace].Trim(), tail);
        }
        return (raw, "");
    }

    // ── Misc helpers ──────────────────────────────────────────────────────────

    public EditableComponent? GetComponentAtPoint(double wx, double wy)
    {
        if (RenderModel is null || SpatialIndex is null) return null;
        var hit = SchematicHitTest.Test(EditModel, RenderModel, SpatialIndex, wx, wy);
        return hit.Kind == SchematicHitTest.HitKind.Component
            ? EditModel.FindComponent(hit.Id)
            : null;
    }

    public void PlaceDot(double wx, double wy)
    {
        double sx = EditModel.SnapToGrid(wx);
        double sy = EditModel.SnapToGrid(wy);
        Execute(new PlaceDotCommand(EditModel, new EditableDot { X = sx, Y = sy }));
    }

    public void FinishCurrentWire() => FinishWire();
    public bool IsDrawingWire => _wirePoints.Count > 0;

    private void CancelCurrentOp()
    {
        // Restore wire points if a segment drag was in progress (points were mutated live).
        if (_isSegmentDrag && _segmentDragWireId is not null && _segmentDragStartPoints is not null)
        {
            var wire = EditModel.FindWire(_segmentDragWireId);
            if (wire is not null)
            {
                wire.Points.Clear();
                wire.Points.AddRange(_segmentDragStartPoints);
            }
            // Restore any T-junction stems moved live during the drag.
            if (_segmentDragStems is not null)
                foreach (var stem in _segmentDragStems)
                {
                    stem.Wire.Points.Clear();
                    stem.Wire.Points.AddRange(stem.StartPoints);
                }
        }
        ClearSegmentDragState();
        // Clear segment selection without firing Changed; the Overlay update below carries it.
        Selection.ClearSegmentsSilent();

        // Restore edit-model positions if a drag was in progress (positions were mutated live).
        if (_isDragging)
        {
            if (_dragStartCompPositions is not null)
                foreach (var (id, start) in _dragStartCompPositions)
                {
                    var comp = EditModel.FindComponent(id);
                    if (comp is not null) { comp.X = start.X; comp.Y = start.Y; }
                }
            if (_dragWireInfo is not null)
                foreach (var (id, info) in _dragWireInfo)
                {
                    var wire = EditModel.FindWire(id);
                    if (wire is not null) RestoreWirePoints(wire, info.StartPoints);
                }
            if (_dragUnselectedWirePoints is not null)
                foreach (var wire in EditModel.Wires)
                {
                    if (!_dragUnselectedWirePoints.TryGetValue(wire.Id, out var orig)) continue;
                    RestoreWirePoints(wire, orig);
                }
        }

        if (_wirePoints.Count > 0) { _wirePoints.Clear(); RebuildOverlay(); }
        _isRubberBanding = false;
        ClearDragState();
        CancelInlineEdit();
        _moveLabelComps = [];
        _moveLabelPhase = MoveLabelPhase.Picking;
        // Clear drag overrides and segment highlight so the renderer falls back to model positions.
        Overlay = Overlay with { RubberBand = null, WirePreview = null,
                                 ComponentDragPositions = null, WireDragPoints = null,
                                 LabelDragOffsets = null,
                                 SelectedWireSegments = SchematicOverlay.EmptySegments };
    }
}
