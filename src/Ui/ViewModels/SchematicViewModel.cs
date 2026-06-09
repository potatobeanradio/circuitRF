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
    public IMessageSink?      MessageSink => _messageSink;

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

    // User crossing-dots on the dragged segment's interior — they ride the segment (sliding along
    // the stationary crossed wire) so a cross connection is not broken by the move.
    private List<(EditableDot Dot, double StartX, double StartY)>? _segmentDragCrossDots;

    // Allowed range of the perpendicular drag delta when an endpoint slides along a parallel wire:
    // the drag is clamped so the endpoint never slides off the end of a connected wire (which would
    // break the connection). Intersection across sliding endpoints ⇒ stops at the shorter wire's end.
    private double _segSlideMin = double.NegativeInfinity;
    private double _segSlideMax = double.PositiveInfinity;

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

    // Every edit is wrapped so the junction-dot invariant (§5.1) is re-enforced as part of the
    // same undoable command: any user dot whose 4-way crossing dissolved is removed, and one Undo
    // restores both the geometry and the dot. No-op when there are no dots / no crossing changed.
    public void Execute(IUiCommand cmd) => _undoRedo.Execute(new DotRevalidationCommand(EditModel, cmd));

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

    /// <summary>The symbol currently being placed (valid only when ActiveTool == Tool.Place).</summary>
    public SymbolKind PlacementSymbol => _placementSymbol;

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
            case Key.S when !ctrl: SetSelectTool(); break;
            case Key.W when !ctrl: SetWireTool(); break;
            case Key.Z when !ctrl: SetZoomBoxTool(); break;
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
                bool dragIsVertical = IsSegmentHorizontal(
                    wire.Points[hit.SubIndex], wire.Points[hit.SubIndex + 1]);   // horizontal seg → vertical drag
                _segmentDragStartPinned  = hit.SubIndex == 0
                    && ShouldPinDraggedEndpoint(wire, 0, dragIsVertical);
                _segmentDragEndPinned    = hit.SubIndex == wire.Points.Count - 2
                    && ShouldPinDraggedEndpoint(wire, wire.Points.Count - 1, dragIsVertical);
                // Clamp the slide so a connected endpoint can't run off the end of its wire.
                (_segSlideMin, _segSlideMax) = ComputeSlideClamp(
                    wire, hit.SubIndex, dragIsVertical, _segmentDragStartPinned, _segmentDragEndPinned);
                // Wires connected ON the dragged segment must follow it (§5.1) so their connection
                // never breaks — detect now, against the original geometry. A segment vertex that is
                // a pinned outer endpoint is held fixed (does not move), so followers there are
                // excluded; every other vertex (and the interior) moves with the segment.
                bool aMoves = !(hit.SubIndex == 0 && _segmentDragStartPinned);
                bool bMoves = !(hit.SubIndex == wire.Points.Count - 2 && _segmentDragEndPinned);
                var segA = _segmentDragStartPoints[hit.SubIndex];
                var segB = _segmentDragStartPoints[hit.SubIndex + 1];
                _segmentDragStems = FindStemsOnSegment(hit.Id, segA, segB, aMoves, bMoves);
                // User crossing-dots on this segment ride along so the cross stays connected.
                _segmentDragCrossDots = FindDotsOnSegment(segA, segB);
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
                    obj.X = EditModel.SnapToAuthorGrid(start.X + dx);
                    obj.Y = EditModel.SnapToAuthorGrid(start.Y + dy);
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
    /// <summary>
    /// Whether a dragged segment's endpoint must be PINNED (held fixed + jogged) to preserve its
    /// connection, given the perpendicular drag axis (<paramref name="dragIsVertical"/>). A port or
    /// a coincident wire vertex is a fixed point → always pins. A connection to a wire BODY pins
    /// only when that body is perpendicular to the drag (the endpoint would move OFF it); when the
    /// body is PARALLEL to the drag the endpoint simply slides along it and stays connected, so it
    /// is NOT pinned — this prevents bogus jog segments running along (and re-junctioning) that
    /// wire. Example: a horizontal wire joining two vertical wires slides down them as one piece.
    /// </summary>
    private bool ShouldPinDraggedEndpoint(EditableWire wire, int ptIdx, bool dragIsVertical)
    {
        const double tol = 8.0;
        if ((uint)ptIdx >= (uint)wire.Points.Count) return false;
        var (wx, wy) = wire.Points[ptIdx];

        foreach (var comp in EditModel.Components)
        {
            if (Selection.IsSelected(comp.Id)) continue;
            int nPins = SymbolPortDefs.For(comp.Symbol, comp.PortCount).Length;
            for (int pi = 0; pi < nPins; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
                if (SchematicGeometry.CoincidentPoints(wx, wy, px, py, tol)) return true;   // port → pin
            }
        }
        foreach (var other in EditModel.Wires)
        {
            if (other.Id == wire.Id || Selection.IsSelected(other.Id)) continue;
            var pts = other.Points;
            for (int k = 0; k < pts.Count; k++)
                if (SchematicGeometry.CoincidentPoints(wx, wy, pts[k].X, pts[k].Y, tol)) return true;   // vertex → pin
            for (int k = 0; k < pts.Count - 1; k++)
                if (SchematicGeometry.PointOnSegmentInterior(
                        wx, wy, pts[k].X, pts[k].Y, pts[k + 1].X, pts[k + 1].Y, tol))
                {
                    bool bodyVertical = Math.Abs(pts[k + 1].X - pts[k].X) < Math.Abs(pts[k + 1].Y - pts[k].Y);
                    if (bodyVertical != dragIsVertical) return true;   // body ⊥ drag → pin (would move off)
                    // body ∥ drag → endpoint slides along it; keep checking other connections
                }
        }
        return false;
    }

    /// <summary>
    /// Allowed range for the perpendicular drag delta so that any endpoint sliding along a parallel
    /// wire stays within that wire's extent — i.e. it stops at the wire's end instead of sliding off
    /// and disconnecting. Only non-pinned outer endpoints slide; ranges from all of them (and all
    /// the wires each touches) are intersected, so the drag stops at the SHORTER wire's end. Returns
    /// (-∞,+∞) when nothing slides.
    /// </summary>
    private (double Min, double Max) ComputeSlideClamp(
        EditableWire wire, int segIdx, bool dragIsVertical, bool startPinned, bool endPinned)
    {
        const double tol = 8.0;
        int n = wire.Points.Count;
        double min = double.NegativeInfinity, max = double.PositiveInfinity;

        void ConstrainEndpoint(int ptIdx)
        {
            var (ex, ey) = wire.Points[ptIdx];
            foreach (var other in EditModel.Wires)
            {
                if (other.Id == wire.Id || Selection.IsSelected(other.Id)) continue;
                var pts = other.Points;
                for (int k = 0; k < pts.Count - 1; k++)
                {
                    if (!SchematicGeometry.PointOnSegmentInterior(
                            ex, ey, pts[k].X, pts[k].Y, pts[k + 1].X, pts[k + 1].Y, tol)) continue;
                    bool bodyVertical = Math.Abs(pts[k + 1].X - pts[k].X) < Math.Abs(pts[k + 1].Y - pts[k].Y);
                    if (bodyVertical != dragIsVertical) continue;   // perpendicular body pins, doesn't slide
                    // Parallel body: keep the endpoint between the body's ends along the drag axis.
                    double lo = dragIsVertical ? Math.Min(pts[k].Y, pts[k + 1].Y) : Math.Min(pts[k].X, pts[k + 1].X);
                    double hi = dragIsVertical ? Math.Max(pts[k].Y, pts[k + 1].Y) : Math.Max(pts[k].X, pts[k + 1].X);
                    double e  = dragIsVertical ? ey : ex;
                    min = Math.Max(min, lo - e);
                    max = Math.Min(max, hi - e);
                }
            }
        }

        if (segIdx == 0 && !startPinned)         ConstrainEndpoint(0);
        if (segIdx == n - 2 && !endPinned)       ConstrainEndpoint(n - 1);
        return (min, max);
    }

    private bool IsWireEndpointConnectedToUnselected(EditableWire wire, int ptIdx)
    {
        const double tol = 8.0;
        if ((uint)ptIdx >= (uint)wire.Points.Count) return false;
        var (wx, wy) = wire.Points[ptIdx];

        foreach (var comp in EditModel.Components)
        {
            if (Selection.IsSelected(comp.Id)) continue;
            int nPins = SymbolPortDefs.For(comp.Symbol, comp.PortCount).Length;
            for (int pi = 0; pi < nPins; pi++)
            {
                var (px, py) = comp.GetPortWorldCoord(pi);
                if (SchematicGeometry.CoincidentPoints(wx, wy, px, py, tol)) return true;
            }
        }
        // Wire-to-wire connections also pin the drag so the connection is NEVER broken by moving a
        // segment — the re-route adds jogs to keep this endpoint exactly in place (a connection must
        // survive any segment move). Connected if this endpoint coincides with another (unselected)
        // wire's vertex (endpoint/corner) OR lies on another wire's segment body (a T-junction).
        foreach (var other in EditModel.Wires)
        {
            if (other.Id == wire.Id || Selection.IsSelected(other.Id)) continue;
            var pts = other.Points;
            for (int k = 0; k < pts.Count; k++)
                if (SchematicGeometry.CoincidentPoints(wx, wy, pts[k].X, pts[k].Y, tol)) return true;
            for (int k = 0; k < pts.Count - 1; k++)
                if (SchematicGeometry.PointOnSegmentInterior(
                        wx, wy, pts[k].X, pts[k].Y, pts[k + 1].X, pts[k + 1].Y, tol)) return true;
        }
        return false;
    }

    // ── Wire endpoint merge helpers ───────────────────────────────────────────

    /// <summary>
    /// True if (x, y) lies on the segment interior of some wire OTHER than the two being merged —
    /// i.e. the merge point is a T-junction with a third wire. Used to suppress a merge that would
    /// bury that junction (§5.1). Uses the connectivity pass's tolerance so it triggers exactly
    /// when a real T exists there.
    /// </summary>
    /// <summary>
    /// True if a collinear-overlap merge of the two wires would bury a T-junction: an endpoint of
    /// either wire that falls strictly INSIDE the merged span (so it disappears into the merged
    /// wire's interior) and lies on a third wire's body. Merging there would turn that T into an
    /// unconnected crossing, so the caller skips the merge.
    /// </summary>
    private bool OverlapMergeBuriesT(
        IReadOnlyList<(double X, double Y)> a, IReadOnlyList<(double X, double Y)> b,
        IReadOnlyList<(double X, double Y)> union, string idA, string idB)
    {
        const double tol = 8.0;
        bool horiz = Math.Abs(union[0].Y - union[^1].Y) < tol;
        double lo = horiz ? Math.Min(union[0].X, union[^1].X) : Math.Min(union[0].Y, union[^1].Y);
        double hi = horiz ? Math.Max(union[0].X, union[^1].X) : Math.Max(union[0].Y, union[^1].Y);
        foreach (var ep in new[] { a[0], a[^1], b[0], b[^1] })
        {
            double c = horiz ? ep.X : ep.Y;
            bool buried = c > lo + tol && c < hi - tol;   // strictly inside the union span
            if (buried && JointLiesOnThirdWireBody(ep.X, ep.Y, idA, idB)) return true;
        }
        return false;
    }

    private bool JointLiesOnThirdWireBody(double x, double y, string idA, string idB)
    {
        foreach (var w in EditModel.Wires)
        {
            if (w.Id == idA || w.Id == idB) continue;
            var pts = w.Points;
            for (int i = 0; i < pts.Count - 1; i++)
                if (SchematicGeometry.PointOnSegmentInterior(
                        x, y, pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y,
                        SchematicEditModel.ConnectTolerance))
                    return true;
        }
        return false;
    }

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

        // 1. Collinear-overlap merge FIRST: two collinear wires that overlap/abut combine into their
        // UNION span (no junction where collinear wires overlap). This must precede the endpoint
        // merge — for collinear wires that merely share an endpoint, the endpoint merge would build
        // a back-tracking path that NormalizePoints collapses, dropping part of the span.
        foreach (var other in EditModel.Wires)
        {
            if (other.Id == wire.Id || other.Points.Count < 2) continue;
            if (excludeIds is not null && excludeIds.Contains(other.Id)) continue;
            var overlapPts = WireGeometry.TryMergeCollinearOverlap(endPoints, other.Points, tol);
            if (overlapPts is null) continue;
            // Don't bury a T: if either wire has an endpoint that falls INSIDE the merged span and
            // lies on a third wire's body, merging would collapse that T-junction into an
            // unconnected crossing — skip the merge so the connection survives.
            if (OverlapMergeBuriesT(endPoints, other.Points, overlapPts, wire.Id, other.Id)) continue;
            var m = new EditableWire();
            m.Points.AddRange(overlapPts);
            return new WireMergeCommand(EditModel, wire, wireIndexInModel, endPoints, other, m);
        }

        // 2. Endpoint-coincidence merge (non-collinear: L-corners / continuations meeting at a point).
        // Prefer end-endpoint (where the user just dragged / finished drawing).
        var target = FindUniqueEndpointMatch(wire.Id, endPoints[^1].X, endPoints[^1].Y, tol, excludeIds);
        var joint  = endPoints[^1];
        if (target is null)
        {
            target = FindUniqueEndpointMatch(wire.Id, endPoints[0].X, endPoints[0].Y, tol, excludeIds);
            joint  = endPoints[0];
        }
        if (target is null) return null;

        // Connecting a wire must never UNCONNECT another (§5.1). If the shared endpoint sits on a
        // THIRD wire's segment interior, that point is a T-junction. Merging would bury that endpoint
        // and silently break the connection — so suppress the merge and keep them separate.
        if (JointLiesOnThirdWireBody(joint.X, joint.Y, wire.Id, target.Id))
            return null;

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
            var portDefs = SymbolPortDefs.For(comp.Symbol, comp.PortCount);
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
                double ex = EditModel.SnapToAuthorGrid(start.X + dx);
                double ey = EditModel.SnapToAuthorGrid(start.Y + dy);
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
            ConnectionDotsOverride = LiveConnectionDots(),
        };
    }

    // Live connection dots for the drag preview. The drag mutates EditModel geometry live, so a
    // fresh O(N) connectivity pass yields dots at the moved positions. Gated by schematic size:
    // above the cap, the per-tick O(N) pass would risk the 10k frame budget (the locked perf
    // rule), so dots simply snap into place at drag-end (BuildRenderModel) as before.
    private const int LiveDotMaxObjects = 1500;
    private IReadOnlyList<SchematicDot>? LiveConnectionDots()
        => (EditModel.Wires.Count + EditModel.Components.Count) <= LiveDotMaxObjects
            ? EditModel.ComputeConnectionDots()
            : null;

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
        _segmentDragCrossDots   = null;
        _segSlideMin            = double.NegativeInfinity;
        _segSlideMax            = double.PositiveInfinity;
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

        // Constrain to perpendicular axis, snap (B2), then clamp so a sliding endpoint can't run
        // off the end of its connected wire (never break a connection).
        double dx, dy;
        if (isHoriz)
        {
            double rawY = pts[i].Y + rawDy;
            dy = EditModel.SnapToGrid(rawY) - pts[i].Y;
            dy = Math.Clamp(dy, _segSlideMin, _segSlideMax);
            dx = 0;
        }
        else
        {
            double rawX = pts[i].X + rawDx;
            dx = EditModel.SnapToGrid(rawX) - pts[i].X;
            dx = Math.Clamp(dx, _segSlideMin, _segSlideMax);
            dy = 0;
        }

        // Simplify LIVE (same cleanup as commit) so redundant collinear runs / zero-length jogs are
        // collapsed as the drag brings geometry back into line — otherwise segments (and their
        // junction dots) appear stacked exactly over existing ones, which reads as a bug.
        var newPts = SimplifyWirePoints(ComputeSegmentDragPoints(pts, i, dx, dy,
            _segmentDragStartPinned, _segmentDragEndPinned));

        wire.Points.Clear();
        wire.Points.AddRange(newPts);

        var wireDragPoints = new Dictionary<string, IReadOnlyList<(double X, double Y)>>
        {
            [_segmentDragWireId] = wire.Points.ToList(),
        };

        // Stem / vertex follow. The dragged segment's body always translates by the perpendicular
        // delta (even when an outer end is pinned — jogs absorb that at the ends), so every wire
        // connected ON the segment moves by that same delta to stay attached: a stem T-ed onto its
        // interior, and a wire joined at one of its moving vertices. Re-route keeps each follower
        // orthogonal with its far (anchored) end fixed. This is what keeps T/corner connections
        // from breaking as the segment moves.
        if (_segmentDragStems is { Count: > 0 })
        {
            foreach (var stem in _segmentDragStems)
            {
                var routed = SimplifyWirePoints(RouteStem(stem, dx, dy));
                stem.Wire.Points.Clear();
                stem.Wire.Points.AddRange(routed);
                wireDragPoints[stem.Wire.Id] = routed;
            }
        }

        // Crossing dots ride the segment by the same delta (live LiveConnectionDots() re-renders
        // them at the moved crossing). If the drag carries the wire off the crossed wire entirely,
        // the dot stops being a crossing and is removed by re-validation at commit.
        if (_segmentDragCrossDots is { Count: > 0 })
            foreach (var (dot, sx, sy) in _segmentDragCrossDots)
            {
                dot.X = sx + dx;
                dot.Y = sy + dy;
            }

        // Fast overlay update — no full BuildRenderModel() per tick (B4 perf).
        Overlay = new SchematicOverlay
        {
            SelectedWireSegments = Selection.GetSelectedSegments(EditModel).ToHashSet(),
            SelectedComponentIds = Selection.GetSelectedComponentIds(EditModel).ToHashSet(),
            SelectedWireIds      = Selection.GetSelectedWireIds(EditModel).ToHashSet(),
            SelectedCanvasObjIds = Selection.GetSelectedCanvasObjectIds(EditModel).ToHashSet(),
            WireDragPoints       = wireDragPoints,
            ConnectionDotsOverride = LiveConnectionDots(),
        };
    }

    /// <summary>
    /// Finds wires whose endpoint sits on the dragged segment (a)-(b) and so must follow it to keep
    /// their connection. This is any other wire ending on the segment's INTERIOR (a T-junction) or
    /// coincident with one of the segment's MOVING vertices (a corner/endpoint junction —
    /// <paramref name="aMoves"/>/<paramref name="bMoves"/> say which vertices actually move; a
    /// pinned outer vertex is held fixed, so wires there stay put and are excluded). Picks one
    /// junction end per follower (start preferred); the other end is the anchored far end.
    /// </summary>
    private List<StemFollow> FindStemsOnSegment(
        string throughWireId, (double X, double Y) a, (double X, double Y) b,
        bool aMoves, bool bMoves)
    {
        const double tol = SchematicEditModel.ConnectTolerance;
        bool OnMovingPartOfSegment((double X, double Y) p)
            => SchematicGeometry.PointOnSegmentInterior(p.X, p.Y, a.X, a.Y, b.X, b.Y, tol)
            || (aMoves && SchematicGeometry.CoincidentPoints(p.X, p.Y, a.X, a.Y, tol))
            || (bMoves && SchematicGeometry.CoincidentPoints(p.X, p.Y, b.X, b.Y, tol));

        var stems = new List<StemFollow>();
        foreach (var w in EditModel.Wires)
        {
            if (w.Id == throughWireId || w.Points.Count < 2) continue;
            var p0 = w.Points[0];
            var pN = w.Points[^1];
            bool startOn = OnMovingPartOfSegment(p0);
            bool endOn   = !startOn && OnMovingPartOfSegment(pN);
            if (!startOn && !endOn) continue;
            var junction = startOn ? p0 : pN;
            var far      = startOn ? pN : p0;
            stems.Add(new StemFollow(w, startOn, junction, far, w.Points.ToList()));
        }
        return stems;
    }

    /// <summary>User junction dots on the dragged segment's interior (crossing dots), with their
    /// original positions — they ride the segment so the cross connection survives the move.</summary>
    private List<(EditableDot Dot, double StartX, double StartY)> FindDotsOnSegment(
        (double X, double Y) a, (double X, double Y) b)
    {
        var list = new List<(EditableDot, double, double)>();
        foreach (var d in EditModel.Dots)
            if (SchematicGeometry.PointOnSegmentInterior(
                    d.X, d.Y, a.X, a.Y, b.X, b.Y, SchematicEditModel.ConnectTolerance))
                list.Add((d, d.X, d.Y));
        return list;
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

        // Fold crossing-dot follows into the same command — their positions were updated live;
        // restore each so MoveCommand.Execute() re-applies the move. One Undo restores the wire,
        // the stems, and every ridden dot together.
        var dotSnaps = new List<DotMoveSnapshot>();
        if (_segmentDragCrossDots is not null)
            foreach (var (dot, sx, sy) in _segmentDragCrossDots)
            {
                if (Math.Abs(dot.X - sx) < 1e-6 && Math.Abs(dot.Y - sy) < 1e-6) continue;  // unmoved
                double ex = dot.X, ey = dot.Y;
                dot.X = sx; dot.Y = sy;   // restore
                dotSnaps.Add(new DotMoveSnapshot(dot, sx, sy, ex, ey));
            }

        // Restore the dragged wire to start state; MoveCommand.Execute() re-applies the end state.
        wire.Points.Clear();
        wire.Points.AddRange(_segmentDragStartPoints);

        wireSnaps.Insert(0, new WireMoveSnapshot(wire, _segmentDragStartPoints, endPoints));
        var moveCmd = new MoveCommand(EditModel, [], wireSnaps, [], dots: dotSnaps);

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
    /// Computes the new point list after dragging segment[i] by perpendicular (dx,dy).
    /// The dragged segment always translates by the delta; whatever is connected stays put:
    ///  • an interior neighbour stretches to the moved vertex (stays orthogonal);
    ///  • a PINNED outer endpoint is held fixed and a jog (OrthogonalRoute) bridges it to the
    ///    moved segment — so a connected wire bows out instead of detaching or freezing. When BOTH
    ///    outer ends are pinned (a single connected segment), jogs are added at both ends.
    /// Connections are therefore never broken by a segment move (§ rubber-band).
    /// </summary>
    private static IReadOnlyList<(double X, double Y)> ComputeSegmentDragPoints(
        IReadOnlyList<(double X, double Y)> startPoints,
        int i, double dx, double dy,
        bool startPinned, bool endPinned)
    {
        int n = startPoints.Count;
        if (n < 2 || i < 0 || i >= n - 1) return startPoints;

        var movedA = (X: startPoints[i].X     + dx, Y: startPoints[i].Y     + dy);
        var movedB = (X: startPoints[i + 1].X + dx, Y: startPoints[i + 1].Y + dy);

        var result = new List<(double X, double Y)>();

        // Left of the dragged segment (toward pts[0]); ends at the moved segment start.
        if (i == 0)
        {
            if (startPinned)
                result.AddRange(WireGeometry.OrthogonalRoute(
                    startPoints[0].X, startPoints[0].Y, movedA.X, movedA.Y));   // fixed end → jog → movedA
            else
                result.Add(movedA);                                            // free end moves
        }
        else
        {
            for (int k = 0; k < i; k++) result.Add(startPoints[k]);            // unchanged prefix
            result.Add(movedA);                                               // neighbour i-1 stretches to here
        }

        // Right of the dragged segment (toward pts[n-1]); starts at the moved segment end.
        if (i + 1 == n - 1)
        {
            if (endPinned)
                result.AddRange(WireGeometry.OrthogonalRoute(
                    movedB.X, movedB.Y, startPoints[n - 1].X, startPoints[n - 1].Y));   // movedB → jog → fixed end
            else
                result.Add(movedB);
        }
        else
        {
            result.Add(movedB);
            for (int k = i + 2; k < n; k++) result.Add(startPoints[k]);        // unchanged suffix
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
                                         modifiers, EditModel.AuthorGridSize);
        var dict = _moveLabelComps.ToDictionary(c => c.Id, _ => (DX: dx, DY: dy));
        Overlay = Overlay with { LabelDragOffsets = dict };
    }

    private void CommitMoveLabels(double wx, double wy, KeyModifiers modifiers)
    {
        var (dx, dy) = ComputeLabelDelta(wx - _moveLabelRefX, wy - _moveLabelRefY,
                                         modifiers, EditModel.AuthorGridSize);
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
        // Snap the click to a connection target (priority: port → wire endpoint → wire body) and
        // remember whether we hit one. Landing on any existing wire/port both makes the connection
        // and ends the draw (see below) — the standard "click on a wire to terminate" gesture.
        bool onConnectionTarget = false;
        if (SpatialIndex is not null)
        {
            var (pFound, _, _, px, py) = SchematicHitTest.NearestPort(EditModel, wx, wy, 15);
            if (pFound) { sx = px; sy = py; onConnectionTarget = true; }
            else
            {
                var (eFound, _, _, ex, ey) = SchematicHitTest.NearestWireEndpoint(EditModel, wx, wy, 15);
                if (eFound) { sx = ex; sy = ey; onConnectionTarget = true; }
                else
                {
                    // Lowest-priority snap: project onto a wire's segment body so the endpoint
                    // lands exactly on another wire, forming a T-junction (§5.1). Grid-snapped to
                    // stay consistent; orthogonal grid-aligned wires keep the point on the segment.
                    var (sFound, _, _, segX, segY) = SchematicHitTest.NearestPointOnWireSegment(EditModel, wx, wy, 15);
                    if (sFound) { sx = EditModel.SnapToGrid(segX); sy = EditModel.SnapToGrid(segY); onConnectionTarget = true; }
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

            // Clicking onto an existing wire (endpoint or body) or a port terminates the draw and
            // makes the connection there: endpoint→merge, body→T-junction, all via FinishWire().
            if (onConnectionTarget)
            {
                FinishWire();
                return;
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
        // Seed label visibility from registry defaults (Ground → both false, all others → true).
        var placeInfo = ComponentTypeRegistry.Get(_placementSymbol);
        comp.ShowTypeLabel    = placeInfo.DefaultShowTypeLabel;
        comp.ShowInstanceName = placeInfo.DefaultShowInstanceName;
        // DefaultParameters expects N (the Z-matrix port count), not the schematic pin count (N+1).
        // For variadic types default to N=2; for fixed types pin count == port count.
        int portCount = (_placementSymbol is SymbolKind.ZPort or SymbolKind.Sdd)
            ? 2
            : SymbolPortDefs.For(_placementSymbol).Length;
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(_placementSymbol, portCount))
            comp.Parameters.Add(new EditableParameter
                { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
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
                    && ShouldPinDraggedEndpoint(wire, 0, isHoriz);
                bool endPinned = segIdx == startPoints.Count - 2
                    && ShouldPinDraggedEndpoint(wire, startPoints.Count - 1, isHoriz);

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

        // Toggle: if all selected components are already in this state, re-enable them.
        var comps = ids.Select(id => EditModel.FindComponent(id))
                       .OfType<EditableComponent>()
                       .ToList();
        var targetState = (comps.Count > 0 && comps.All(c => c.Disable == state))
            ? DisableState.None
            : state;

        Execute(new SetDisableStateCommand(EditModel, ids, targetState));
    }

    public void SelectIfUnselected(string id)
    {
        if (!Selection.IsSelected(id)) Selection.SelectOne(id);
    }

    // ── Label visibility ──────────────────────────────────────────────────────

    /// <summary>Toggles ShowTypeLabel or ShowInstanceName on a single component (undoable).</summary>
    public void ToggleLabelVisibility(string compId, bool isTypeLabel)
    {
        var comp = EditModel.FindComponent(compId);
        if (comp is null) return;
        bool current = isTypeLabel ? comp.ShowTypeLabel : comp.ShowInstanceName;
        Execute(new SetLabelVisibilityCommand(EditModel, comp, isTypeLabel, !current));
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
        // Idempotency guard: a second call (Enter+LostFocus race or stale deferred post) is a no-op.
        if (_inlineEditKind == InlineEditKind.None) return;

        // Capture locals and clear VM state immediately so any deferred re-entry sees None and exits.
        // Reading captured locals (not fields) also prevents cross-contamination when the user has
        // already started editing a different component before the deferred call fires.
        var kind       = _inlineEditKind;
        var targetId   = _inlineEditTargetId;
        var param      = _inlineEditParam;
        var label      = _inlineEditExistingNetLabel;
        var worldX     = _inlineEditWorldX;
        var worldY     = _inlineEditWorldY;
        string newVal  = InlineEditValue.Trim();

        CancelInlineEdit();  // zero out all fields now — deferred call hits None guard above

        switch (kind)
        {
            case InlineEditKind.ComponentType:
            {
                var comp = EditModel.FindComponent(targetId ?? "");
                if (comp is null) break;
                if (!ComponentTypeRegistry.TryParseCode(newVal, out var newKind, out int parsedPortCount))
                {
                    _messageSink?.Warning($"Unknown component type: '{newVal}' — use R, L, C, V, GND, FET, Z2P, SDD3, …");
                    break;
                }
                if (newKind != comp.Symbol || (parsedPortCount > 0 && parsedPortCount != comp.PortCount))
                {
                    // Build the replacement component: same position/rotation/mirror, new Id/name/params.
                    // Exclude the old component from naming so its slot is treated as free.
                    string prefix    = ComponentTypeRegistry.InstancePrefix(newKind);
                    var    remaining = EditModel.Components.Where(c => c.Id != comp.Id);
                    string newName   = SchematicEditModel.NextAvailableName(remaining, prefix);
                    // Use parsed port count N for variadic types; fall back to SymbolPortDefs for fixed-pin types.
                    int    portCount = parsedPortCount > 0 ? parsedPortCount : SymbolPortDefs.For(newKind).Length;
                    var    typeInfo  = ComponentTypeRegistry.Get(newKind);
                    var    newComp   = new EditableComponent
                    {
                        InstanceName     = newName,
                        Symbol           = newKind,
                        X = comp.X, Y = comp.Y,
                        Rotation         = comp.Rotation,
                        MirrorX          = comp.MirrorX,
                        ShowTypeLabel    = typeInfo.DefaultShowTypeLabel,
                        ShowInstanceName = typeInfo.DefaultShowInstanceName,
                    };
                    foreach (var dp in ComponentTypeRegistry.DefaultParameters(newKind, portCount))
                        newComp.Parameters.Add(new EditableParameter
                            { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
                    Execute(new ChangeComponentTypeCommand(EditModel, comp, newComp));
                }
                break;
            }
            case InlineEditKind.ComponentName:
            {
                var comp = EditModel.FindComponent(targetId ?? "");
                if (comp is null || newVal.Length == 0 || newVal == comp.InstanceName) break;
                Execute(new RenameComponentCommand(EditModel, comp, newVal));
                break;
            }
            case InlineEditKind.ComponentParam:
            {
                if (param is null) break;
                if (newVal.Length > 0)
                {
                    var (expr, unit) = ParseExpressionUnit(newVal);
                    if (expr != param.Expression || unit != param.Unit)
                        Execute(new EditParameterCommand(EditModel, param, expr, unit));
                }
                break;
            }
            case InlineEditKind.WireNetLabel:
            {
                if (newVal.Length == 0) break;
                if (label is not null)
                {
                    if (newVal != label.Name)
                        Execute(new RenameNetLabelCommand(EditModel, label, newVal));
                }
                else
                {
                    // Use the placement coordinates as-is: the perpendicular gap was computed
                    // from the wire's exact position in ClassifySegmentAt, so grid-snapping
                    // the perpendicular axis would round it back onto the wire.
                    Execute(new PlaceNetLabelCommand(EditModel,
                        new EditableNetLabel { Name = newVal, X = worldX, Y = worldY }));
                }
                break;
            }
        }
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
        // INVARIANT (§5.1): a junction dot is valid ONLY on a genuine 4-way wire crossing. Snap to
        // the nearest crossing within tolerance; if there is none, create nothing — the user cannot
        // place an inert dot in empty space, on a lone wire, or at a T/merge (those connect without
        // a dot). This keeps every dot an unambiguous crossing-union for net extraction (6e).
        if (SpatialIndex is null) return;
        var (found, cx, cy) = SchematicHitTest.NearestWireCrossing(EditModel, SpatialIndex, wx, wy, 15);
        if (!found)
        {
            _messageSink?.Warning("A junction dot must be placed on a wire crossing.");
            return;
        }
        Execute(new PlaceDotCommand(EditModel, new EditableDot { X = cx, Y = cy }));
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
            // Restore any crossing-dots moved live during the drag.
            if (_segmentDragCrossDots is not null)
                foreach (var (dot, sx, sy) in _segmentDragCrossDots)
                {
                    dot.X = sx; dot.Y = sy;
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
        Overlay = Overlay with { RubberBand = null, WirePreview = null, Ghost = null,
                                 ComponentDragPositions = null, WireDragPoints = null,
                                 LabelDragOffsets = null,
                                 SelectedWireSegments = SchematicOverlay.EmptySegments };
    }
}
