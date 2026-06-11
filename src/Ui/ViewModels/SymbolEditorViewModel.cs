using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Symbol;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// ViewModel for the Symbol Editor canvas.
/// All mutations route through the shared UndoRedoStack via Execute(IUiCommand).
/// Mirrors SchematicViewModel at symbol scale.
/// </summary>
public sealed partial class SymbolEditorViewModel : ObservableObject
{
    // ── Dependencies ─────────────────────────────────────────────────────────

    private readonly UndoRedoStack _undoRedo = new();
    public  UndoRedoStack  UndoRedo       => _undoRedo;
    public  EditableSymbol EditableSymbol { get; }

    // ── Tool ─────────────────────────────────────────────────────────────────

    public enum Tool
    {
        Select,
        Line, Polyline, Rect, RoundedRect, Circle, Ellipse, Arc, Triangle, Polygon,
        QuadCurve, CubicCurve, Sine, HalfWave, Text,
        Pin,
    }

    [ObservableProperty] private Tool _activeTool = Tool.Select;

    // Cancel any in-progress drawing/pin operation when the tool changes.
    partial void OnActiveToolChanged(Tool value)
    {
        _isDrawingTwoPoint = false;
        _drawPoints.Clear();
        _isTypingText = false;
        _textBuffer   = "";
        // Cancel pin state
        _selectedPins.Clear();
        _isPinDragging = false;
        _pinLiveDx        = 0;
        _pinLiveDy        = 0;
        OnPropertyChanged(nameof(IsPinToolActive));
        RebuildOverlay();
    }

    // ── Symbol metadata (port count + lock) ──────────────────────────────────

    /// <summary>
    /// Number of ports this symbol can map pins to.
    /// Synced from EditableSymbol.PortCount; changes are propagated back.
    /// </summary>
    [ObservableProperty] private int _portCount;

    partial void OnPortCountChanged(int value)
    {
        if (IsLocked) return;
        value = Math.Max(0, value);
        EditableSymbol.PortCount = value;
        EditableSymbol.NotifyChanged();
    }

    /// <summary>True for built-in / system symbols; the editor opens them read-only.</summary>
    [ObservableProperty] private bool _isLocked;

    partial void OnIsLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEditable));
    }

    /// <summary>False when the symbol is locked; use to bind IsEnabled on editing controls.</summary>
    public bool IsEditable => !IsLocked;

    /// <summary>True when the Pin tool is active.</summary>
    public bool IsPinToolActive => ActiveTool == Tool.Pin;

    // ── Dirty / path tracking ─────────────────────────────────────────────────

    [ObservableProperty] private bool    _isDirty;
    [ObservableProperty] private string? _currentSymbolPath;

    // ── Current draw style (property controls set these; new primitives read them) ──

    [ObservableProperty] private SymbolColorRole  _currentColorRole  = SymbolColorRole.SymbolLine;
    [ObservableProperty] private SymbolStrokeTier _currentStrokeTier = SymbolStrokeTier.Normal;
    [ObservableProperty] private double           _currentFontSize   = 12.0;
    [ObservableProperty] private SymbolFontStyle  _currentFontStyle  = SymbolFontStyle.Regular;

    // ── Render snapshot (canvas subscribes to PropertyChanged) ────────────────

    [ObservableProperty] private Symbol?              _renderSymbol;
    [ObservableProperty] private SymbolEditorOverlay  _overlay = SymbolEditorOverlay.Empty;

    // ── Primitive selection ───────────────────────────────────────────────────

    private readonly HashSet<int> _selection = [];

    // ── Select-tool drag state ────────────────────────────────────────────────

    private bool   _isDragging;
    private double _dragStartLocalX, _dragStartLocalY;
    private double _liveDx, _liveDy;

    // ── Rubber-band state (Select tool only) ─────────────────────────────────

    private bool   _isRubberBanding;
    private double _rbStartX, _rbStartY;
    private double _rbCurX,   _rbCurY;

    // ── Two-point drag draw state ─────────────────────────────────────────────

    private bool   _isDrawingTwoPoint;
    private double _drawP1X, _drawP1Y;
    private double _drawP2X, _drawP2Y;

    // ── Multi-point click draw state ──────────────────────────────────────────

    private readonly List<(double X, double Y)> _drawPoints = [];
    private double _drawCurX, _drawCurY;

    // ── Text draw state ───────────────────────────────────────────────────────

    private bool   _isTypingText;
    private double _textAnchorX, _textAnchorY;
    private string _textBuffer = "";

    // ── Pin tool state ────────────────────────────────────────────────────────

    private readonly HashSet<int> _selectedPins = [];
    private bool   _isPinDragging;
    private double _pinOrigX, _pinOrigY;    // grabbed pin's position at drag start (delta reference)
    private double _pinGrabX, _pinGrabY;    // raw cursor position at grab
    private double _pinLiveDx, _pinLiveDy; // live delta applied to all selected pins (P-snapped)

    // ── Resize gripper state ─────────────────────────────────────────────────

    private bool   _isResizing;
    private int    _resizePrimIdx = -1;
    private double _resizeBbX0, _resizeBbY0, _resizeBbX1, _resizeBbY1; // original bbox
    private double _resizeLiveX1, _resizeLiveY1;                         // tracked bottom-right
    private bool   _resizeShift;

    // ── Selected-pin port remap ───────────────────────────────────────────────

    /// <summary>Port index of the currently selected pin; changing it fires RemapSymbolPinCommand.</summary>
    [ObservableProperty] private int _selectedPinPortIndex;
    private bool _applyingRemap;

    partial void OnSelectedPinPortIndexChanged(int value)
    {
        if (_applyingRemap || IsLocked || _selectedPins.Count != 1) return;
        int idx = _selectedPins.First();
        if (idx < 0 || idx >= EditableSymbol.Pins.Count) return;
        var pin = EditableSymbol.Pins[idx];
        if (pin.PortIndex == value) return;
        _applyingRemap = true;
        Execute(new RemapSymbolPinCommand(EditableSymbol, pin, value));
        _applyingRemap = false;
    }

    // ── Snap constants ────────────────────────────────────────────────────────

    // Fine art grid: p = P/20 = 100/20 = 5 local units.
    private const double SmallGrid = 5.0;
    // Connection / pin grid: P = 100 local units (1 connection-grid square).
    private const double PinGrid   = 100.0;

    // ── Canvas zoom (set by SymbolEditorCanvas; used to convert screen-px tolerances) ──

    /// <summary>
    /// Current canvas zoom (pixels per world unit).  Set by <see cref="SymbolEditorCanvas"/>
    /// on every zoom change so that hit-test tolerances are expressed in screen-pixel units.
    /// </summary>
    public double CanvasZoom { get; set; } = 1.0;

    /// <summary>
    /// When true (default), art-primitive draw/move operations snap to the fine grid p=5.
    /// Pins ALWAYS snap to the connection grid P=100 regardless of this toggle.
    /// </summary>
    [ObservableProperty] private bool _gridSnap = true;

    private double SnapToP(double v)
        => GridSnap ? Math.Round(v / SmallGrid) * SmallGrid : v;

    private static double SnapToConnectionGrid(double v)
        => Math.Round(v / PinGrid) * PinGrid;

    // ── Toolbar commands ─────────────────────────────────────────────────────

    public IRelayCommand       UndoCommand               { get; }
    public IRelayCommand       RedoCommand               { get; }
    public IRelayCommand<string> SetActiveToolCommand    { get; }
    public IRelayCommand<string> SetCurrentStrokeTierCommand { get; }

    /// <summary>Rotates selected primitives / pins 90° CW about the selection bbox bottom-left.</summary>
    public IRelayCommand       RotateSelectionCommand    { get; }

    public IAsyncRelayCommand<Window?> SaveSymbolCommand   { get; }
    public IAsyncRelayCommand<Window?> SaveSymbolAsCommand { get; }

    /// <summary>All <see cref="SymbolFontStyle"/> values, exposed for XAML <c>ItemsSource</c>.</summary>
    public static SymbolFontStyle[] FontStyleOptions { get; } = Enum.GetValues<SymbolFontStyle>();

    // ── Constructor ───────────────────────────────────────────────────────────

    public SymbolEditorViewModel(EditableSymbol editableSymbol)
    {
        EditableSymbol = editableSymbol;
        _portCount     = editableSymbol.PortCount;
        _isLocked      = !editableSymbol.UserEditable;

        UndoCommand = new RelayCommand(() => _undoRedo.Undo(), () => _undoRedo.CanUndo);
        RedoCommand = new RelayCommand(() => _undoRedo.Redo(), () => _undoRedo.CanRedo);

        SetActiveToolCommand = new RelayCommand<string>(name =>
        {
            if (name is not null && Enum.TryParse<Tool>(name, out var t))
                ActiveTool = t;
        });

        SetCurrentStrokeTierCommand = new RelayCommand<string>(name =>
        {
            if (name is not null && Enum.TryParse<SymbolStrokeTier>(name, out var tier))
                CurrentStrokeTier = tier;
        });

        RotateSelectionCommand = new RelayCommand(() =>
        {
            if (IsLocked) return;
            var prims = _selection
                .Where(i => i >= 0 && i < EditableSymbol.Primitives.Count)
                .Select(i => EditableSymbol.Primitives[i]).ToList();
            var pins = _selectedPins
                .Where(i => i >= 0 && i < EditableSymbol.Pins.Count)
                .Select(i => EditableSymbol.Pins[i]).ToList();
            if (prims.Count > 0 || pins.Count > 0)
                Execute(new Commands.Symbol.RotateSelectionCommand(EditableSymbol, prims, pins));
        }, () => !IsLocked && (_selection.Count > 0 || _selectedPins.Count > 0));

        _undoRedo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UndoRedoStack.CanUndo)) UndoCommand.NotifyCanExecuteChanged();
            if (e.PropertyName is nameof(UndoRedoStack.CanRedo)) RedoCommand.NotifyCanExecuteChanged();
        };

        SaveSymbolCommand   = new AsyncRelayCommand<Window?>(SaveSymbolAsync);
        SaveSymbolAsCommand = new AsyncRelayCommand<Window?>(SaveSymbolAsAsync);

        EditableSymbol.Changed += (_, _) => RebuildRenderSnapshot();
        RebuildRenderSnapshot();
    }

    // ── Command execution ─────────────────────────────────────────────────────

    /// <summary>
    /// Execute a command on the undo stack.
    /// Marks the symbol dirty (if not locked) so the save affordance shows.
    /// </summary>
    public void Execute(IUiCommand cmd)
    {
        _undoRedo.Execute(cmd);
        if (!IsLocked) IsDirty = true;
    }

    // ── Text input (from canvas TextInput event) ──────────────────────────────

    public void OnTextInput(string text)
    {
        if (!_isTypingText || string.IsNullOrEmpty(text)) return;
        _textBuffer += text;
        RebuildOverlay();
    }

    // ── Snapshot ──────────────────────────────────────────────────────────────

    private void RebuildRenderSnapshot()
    {
        RenderSymbol = EditableSymbol.ToSymbol();
        RebuildOverlay();
    }

    private void RebuildOverlay()
    {
        _selection.RemoveWhere(i => i < 0 || i >= EditableSymbol.Primitives.Count);
        _selectedPins.RemoveWhere(i => i < 0 || i >= EditableSymbol.Pins.Count);
        RotateSelectionCommand.NotifyCanExecuteChanged();

        SymbolPrimitive? inProgress = null;
        if (_isDrawingTwoPoint)
            inProgress = BuildTwoPointPrimitive(_drawP1X, _drawP1Y, _drawP2X, _drawP2Y);
        else if (_drawPoints.Count > 0)
            inProgress = BuildMultiPointPreview(_drawCurX, _drawCurY);
        else if (_isTypingText)
            inProgress = new TextPrimitive
            {
                Content   = (_textBuffer.Length > 0 ? _textBuffer : "") + "|",
                AnchorX   = _textAnchorX,
                AnchorY   = _textAnchorY,
                FontSize  = CurrentFontSize,
                FontStyle = CurrentFontStyle,
                Align     = SymbolTextAlign.Left,
            };

        // Resize handle: bottom-right of single selected prim's bbox.
        (double X, double Y)? resizeHandle = null;
        (double X0, double Y0, double X1, double Y1)? resizePreviewBb = null;
        if (!IsLocked && ActiveTool == Tool.Select && _selection.Count == 1 && !_isDragging && !_isRubberBanding)
        {
            int idx = _selection.First();
            if (idx >= 0 && idx < EditableSymbol.Primitives.Count)
            {
                var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(EditableSymbol.Primitives[idx]);
                resizeHandle = (bx1, by1);
                if (_isResizing)
                    resizePreviewBb = (_resizeBbX0, _resizeBbY0, _resizeLiveX1, _resizeLiveY1);
            }
        }

        Overlay = new SymbolEditorOverlay
        {
            SelectedIndices     = _selection.ToHashSet(),
            LiveDragOffset      = (_liveDx, _liveDy),
            RubberBand          = _isRubberBanding
                ? (Math.Min(_rbStartX, _rbCurX), Math.Min(_rbStartY, _rbCurY),
                   Math.Max(_rbStartX, _rbCurX), Math.Max(_rbStartY, _rbCurY))
                : null,
            InProgressPrimitive = inProgress,
            SelectedPinIndices  = _selectedPins.ToHashSet(),
            PinLiveDragOffset   = (_pinLiveDx, _pinLiveDy),
            UnmappedPortIndices = ComputeUnmappedPorts(),
            ResizeHandle        = resizeHandle,
            ResizePreviewBb     = resizePreviewBb,
        };
    }

    // ── Pointer handlers ──────────────────────────────────────────────────────

    public void OnPointerPressed(double lx, double ly, KeyModifiers mods, int clickCount = 1)
    {
        if (ActiveTool == Tool.Select)
        {
            SelectToolPress(lx, ly, mods);
            return;
        }

        if (ActiveTool == Tool.Pin)
        {
            if (!IsLocked) PinToolPress(lx, ly);
            return;
        }

        // Drawing tools — blocked when locked.
        if (IsLocked) return;

        double sx = SnapToP(lx), sy = SnapToP(ly);

        if (IsTwoPointDragTool(ActiveTool))
        {
            _isDrawingTwoPoint = true;
            _drawP1X = sx; _drawP1Y = sy;
            _drawP2X = sx; _drawP2Y = sy;
            RebuildOverlay();
        }
        else if (IsMultiPointTool(ActiveTool))
        {
            if (clickCount >= 2)
            {
                FinishMultiPointDraw();
                return;
            }
            _drawPoints.Add((sx, sy));
            _drawCurX = sx; _drawCurY = sy;
            TryAutoComplete();
        }
        else if (ActiveTool == Tool.Text)
        {
            _isTypingText = true;
            _textAnchorX  = sx;
            _textAnchorY  = sy;
            _textBuffer   = "";
            RebuildOverlay();
        }
    }

    public void OnPointerMoved(double lx, double ly, bool leftDown)
    {
        if (!leftDown)
        {
            if (_isDragging || _isRubberBanding || _isDrawingTwoPoint || _isPinDragging || _isResizing)
                CancelOp();

            if (_drawPoints.Count > 0)
            {
                _drawCurX = SnapToP(lx);
                _drawCurY = SnapToP(ly);
                RebuildOverlay();
            }
            return;
        }

        if (ActiveTool == Tool.Select)
        {
            if (_isResizing)
            {
                double nx = SnapToP(lx);
                double ny = SnapToP(ly);
                if (_resizeShift)
                {
                    // Aspect-ratio lock: scale bottom-right uniformly from top-left anchor.
                    double origW = _resizeBbX1 - _resizeBbX0;
                    double origH = _resizeBbY1 - _resizeBbY0;
                    if (origW > 1e-9 && origH > 1e-9)
                    {
                        double sx = (nx - _resizeBbX0) / origW;
                        double sy = (ny - _resizeBbY0) / origH;
                        double s  = Math.Min(Math.Abs(sx), Math.Abs(sy));
                        nx = _resizeBbX0 + origW * s;
                        ny = _resizeBbY0 + origH * s;
                    }
                }
                _resizeLiveX1 = nx;
                _resizeLiveY1 = ny;
                RebuildOverlay();
                return;
            }
            if (_isPinDragging)
            {
                double destX = SnapToConnectionGrid(_pinOrigX + (lx - _pinGrabX));
                double destY = SnapToConnectionGrid(_pinOrigY + (ly - _pinGrabY));
                _pinLiveDx = destX - _pinOrigX;
                _pinLiveDy = destY - _pinOrigY;
                // If primitives are also selected, carry them along at the same delta.
                if (_selection.Count > 0) { _liveDx = _pinLiveDx; _liveDy = _pinLiveDy; }
                RebuildOverlay();
                return;
            }
            if (_isDragging)
            {
                if (_selectedPins.Count > 0)
                {
                    // Pins constrain snap to P=100 so they stay on the connection grid.
                    _liveDx = SnapToConnectionGrid(lx - _dragStartLocalX);
                    _liveDy = SnapToConnectionGrid(ly - _dragStartLocalY);
                    _pinLiveDx = _liveDx;
                    _pinLiveDy = _liveDy;
                }
                else
                {
                    _liveDx = SnapToP(lx - _dragStartLocalX);
                    _liveDy = SnapToP(ly - _dragStartLocalY);
                }
                RebuildOverlay();
            }
            else if (_isRubberBanding)
            {
                _rbCurX = lx; _rbCurY = ly;
                UpdateRubberBandSelection();
                RebuildOverlay();
            }
            return;
        }

        if (_isDrawingTwoPoint)
        {
            _drawP2X = SnapToP(lx);
            _drawP2Y = SnapToP(ly);
            RebuildOverlay();
            return;
        }

        if (_drawPoints.Count > 0)
        {
            _drawCurX = SnapToP(lx);
            _drawCurY = SnapToP(ly);
            RebuildOverlay();
        }
    }

    public void OnPointerReleased(double lx, double ly)
    {
        if (ActiveTool == Tool.Select)
        {
            if (_isResizing)
            {
                _isResizing = false;
                double origW = _resizeBbX1 - _resizeBbX0;
                double origH = _resizeBbY1 - _resizeBbY0;
                double newW  = _resizeLiveX1 - _resizeBbX0;
                double newH  = _resizeLiveY1 - _resizeBbY0;
                if (!IsLocked && Math.Abs(origW) > 1e-9 && Math.Abs(origH) > 1e-9
                              && (Math.Abs(newW - origW) > 0.1 || Math.Abs(newH - origH) > 0.1))
                {
                    double sx = newW / origW;
                    double sy = newH / origH;
                    if (Math.Abs(sx) > 1e-6 && Math.Abs(sy) > 1e-6
                        && _resizePrimIdx >= 0 && _resizePrimIdx < EditableSymbol.Primitives.Count)
                    {
                        Execute(new Commands.Symbol.ResizeSymbolPrimitiveCommand(
                            EditableSymbol, EditableSymbol.Primitives[_resizePrimIdx],
                            _resizeBbX0, _resizeBbY0, sx, sy));
                    }
                }
                RebuildOverlay();
                return;
            }

            if (_isPinDragging)
            {
                _isPinDragging = false;
                if ((_pinLiveDx != 0 || _pinLiveDy != 0) && !IsLocked)
                {
                    double dx = _pinLiveDx, dy = _pinLiveDy;
                    if (_selectedPins.Count > 0)
                    {
                        var moves = _selectedPins
                            .Where(i => i >= 0 && i < EditableSymbol.Pins.Count)
                            .Select(i => EditableSymbol.Pins[i])
                            .Select(p => (p, p.LocalX + dx, p.LocalY + dy));
                        Execute(new MoveMultipleSymbolPinsCommand(EditableSymbol, moves));
                    }
                    if (_selection.Count > 0)
                    {
                        var prims = _selection
                            .Where(i => i >= 0 && i < EditableSymbol.Primitives.Count)
                            .Select(i => EditableSymbol.Primitives[i]).ToList();
                        if (prims.Count > 0)
                            Execute(new MoveSymbolPrimitivesCommand(EditableSymbol, prims, dx, dy));
                    }
                }
                _pinLiveDx = 0; _pinLiveDy = 0;
                _liveDx    = 0; _liveDy    = 0;
                RebuildOverlay();
                return;
            }
            if (_isDragging)
            {
                if ((_liveDx != 0 || _liveDy != 0) && !IsLocked)
                {
                    if (_selection.Count > 0)
                    {
                        var prims = _selection
                            .Where(i => i >= 0 && i < EditableSymbol.Primitives.Count)
                            .Select(i => EditableSymbol.Primitives[i]).ToList();
                        if (prims.Count > 0)
                            Execute(new MoveSymbolPrimitivesCommand(EditableSymbol, prims, _liveDx, _liveDy));
                    }
                    if (_selectedPins.Count > 0)
                    {
                        double dx = _liveDx, dy = _liveDy;
                        var moves = _selectedPins
                            .Where(i => i >= 0 && i < EditableSymbol.Pins.Count)
                            .Select(i => EditableSymbol.Pins[i])
                            .Select(p => (p, p.LocalX + dx, p.LocalY + dy));
                        Execute(new MoveMultipleSymbolPinsCommand(EditableSymbol, moves));
                    }
                }
                _isDragging = false;
                _liveDx     = 0; _liveDy     = 0;
                _pinLiveDx  = 0; _pinLiveDy  = 0;
            }
            else if (_isRubberBanding)
            {
                _rbCurX = lx; _rbCurY = ly;
                UpdateRubberBandSelection();
                _isRubberBanding = false;
            }
            RebuildOverlay();
            return;
        }

        if (_isDrawingTwoPoint)
        {
            _drawP2X = SnapToP(lx);
            _drawP2Y = SnapToP(ly);
            var prim = BuildTwoPointPrimitive(_drawP1X, _drawP1Y, _drawP2X, _drawP2Y);
            if (prim is not null && !IsLocked)
                Execute(new PlaceSymbolPrimitiveCommand(EditableSymbol, prim));
            _isDrawingTwoPoint = false;
            RebuildOverlay();
        }
    }

    public void OnKeyDown(Key key, KeyModifiers mods)
    {
        // Text typing mode — intercept keys before general handlers.
        if (_isTypingText)
        {
            if (key == Key.Escape)                           { CancelOp(); ActiveTool = Tool.Select; return; }
            if (key == Key.Enter || key == Key.Return)       { CommitText(); return; }
            if (key == Key.Back && _textBuffer.Length > 0)  { _textBuffer = _textBuffer[..^1]; RebuildOverlay(); }
            return;
        }

        // G key — toggle snap-to-grid (art grid only; pins always snap to P).
        if (key == Key.G && (mods & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            GridSnap = !GridSnap; return;
        }

        // R key — rotate selected pins (any tool) and/or selected primitives (Select tool)
        // about the selection's bottom-left (min-X, max-Y) anchor.
        if (key == Key.R && !IsLocked && (mods & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            var pins = _selectedPins
                .Where(i => i >= 0 && i < EditableSymbol.Pins.Count)
                .Select(i => EditableSymbol.Pins[i]).ToList();
            var prims = ActiveTool == Tool.Select
                ? _selection
                    .Where(i => i >= 0 && i < EditableSymbol.Primitives.Count)
                    .Select(i => EditableSymbol.Primitives[i]).ToList()
                : [];
            if (pins.Count > 0 || prims.Count > 0)
            {
                Execute(new Commands.Symbol.RotateSelectionCommand(EditableSymbol, prims, pins));
                RebuildOverlay();
            }
            return;
        }

        // Delete — removes all selected pins and/or primitives.
        if ((key == Key.Delete || key == Key.Back) && !IsLocked)
        {
            bool acted = false;
            if (_selectedPins.Count > 0)
            {
                var pinsToDelete = _selectedPins
                    .Where(i => i >= 0 && i < EditableSymbol.Pins.Count)
                    .Select(i => EditableSymbol.Pins[i]).ToList();
                if (pinsToDelete.Count > 0)
                {
                    Execute(new DeleteMultipleSymbolPinsCommand(EditableSymbol, pinsToDelete));
                    _selectedPins.Clear();
                    acted = true;
                }
            }
            if (_selection.Count > 0)
            {
                Execute(new DeleteSymbolPrimitivesCommand(EditableSymbol, _selection.ToList()));
                _selection.Clear();
                acted = true;
            }
            if (acted) RebuildOverlay();
            return;
        }

        // Pin tool key handling — only Escape remains (select/move/delete now live in Select).
        if (ActiveTool == Tool.Pin)
        {
            if (key == Key.Escape)
                ActiveTool = Tool.Select;
            return;
        }

        if (key == Key.Escape)
        {
            bool hasActiveOp = ActiveTool != Tool.Select
                            || _isDrawingTwoPoint
                            || _drawPoints.Count > 0
                            || _isDragging
                            || _isRubberBanding;
            if (hasActiveOp) { CancelOp(); ActiveTool = Tool.Select; }
            else ClearSelection();
            return;
        }

        if (key == Key.Enter || key == Key.Return)
        {
            if (_drawPoints.Count > 0) FinishMultiPointDraw();
            return;
        }
        // Undo/redo handled at window level — not here.
    }

    // ── Public helpers ────────────────────────────────────────────────────────

    public void ClearSelection()
    {
        _selection.Clear();
        _selectedPins.Clear();
        RebuildOverlay();
    }

    // ── Select-tool helpers ───────────────────────────────────────────────────

    private void SelectToolPress(double lx, double ly, KeyModifiers mods)
    {
        bool shift = (mods & KeyModifiers.Shift) != 0;

        // Check gripper first so a resize drag doesn't accidentally move the prim.
        if (!IsLocked && HitTestGripper(lx, ly) && _selection.Count == 1)
        {
            int idx = _selection.First();
            if (idx >= 0 && idx < EditableSymbol.Primitives.Count)
            {
                var (x0, y0, x1, y1) = SymbolGeometry.BboxOf(EditableSymbol.Primitives[idx]);
                _isResizing    = true;
                _resizePrimIdx = idx;
                _resizeBbX0    = x0; _resizeBbY0 = y0;
                _resizeBbX1    = x1; _resizeBbY1 = y1;
                _resizeLiveX1  = x1; _resizeLiveY1 = y1;
                _resizeShift   = shift;
                RebuildOverlay();
                return;
            }
        }

        // Pin hit-test before primitives.
        if (!IsLocked)
        {
            int pinHit = HitTestPin(lx, ly);
            if (pinHit >= 0)
            {
                if (shift)
                {
                    // Toggle this pin; don't disturb primitive selection or start a drag.
                    if (!_selectedPins.Add(pinHit)) _selectedPins.Remove(pinHit);
                    _isPinDragging = false;
                }
                else if (_selectedPins.Contains(pinHit))
                {
                    // Clicked pin already in selection — drag all selected items together.
                    _isPinDragging = true;
                    _pinOrigX      = EditableSymbol.Pins[pinHit].LocalX;
                    _pinOrigY      = EditableSymbol.Pins[pinHit].LocalY;
                    _pinGrabX      = lx;
                    _pinGrabY      = ly;
                    _pinLiveDx     = 0;
                    _pinLiveDy     = 0;
                }
                else
                {
                    // New pin clicked — replace all selection with just this pin.
                    _selection.Clear();
                    _selectedPins.Clear();
                    _selectedPins.Add(pinHit);
                    _isPinDragging = true;
                    _pinOrigX      = EditableSymbol.Pins[pinHit].LocalX;
                    _pinOrigY      = EditableSymbol.Pins[pinHit].LocalY;
                    _pinGrabX      = lx;
                    _pinGrabY      = ly;
                    _pinLiveDx     = 0;
                    _pinLiveDy     = 0;
                }
                SyncSelectedPinPortIndex();
                RebuildOverlay();
                return;
            }
        }
        int hit = HitTestTopmost(lx, ly);

        if (hit >= 0)
        {
            if (shift)
            {
                if (!_selection.Add(hit)) _selection.Remove(hit);
                // Pins unchanged — shift extends the selection.
            }
            else if (_selection.Contains(hit))
            {
                // Clicked prim is already selected — drag all selected items (pins + prims).
            }
            else
            {
                // Fresh prim click — replace the entire selection.
                _selection.Clear();
                _selectedPins.Clear();
                _selection.Add(hit);
            }
            _isDragging      = true;
            _dragStartLocalX = lx;
            _dragStartLocalY = ly;
            _liveDx          = 0;
            _liveDy          = 0;
        }
        else
        {
            if (!shift) { _selection.Clear(); _selectedPins.Clear(); }
            _isRubberBanding = true;
            _rbStartX = lx; _rbStartY = ly;
            _rbCurX   = lx; _rbCurY   = ly;
        }

        RebuildOverlay();
    }

    // ── Pin tool helpers ──────────────────────────────────────────────────────

    private void PinToolPress(double lx, double ly)
    {
        int hit = HitTestPin(lx, ly);
        _selectedPins.Clear();
        if (hit >= 0)
        {
            // Existing pin: select it so the inspector shows it. No drag under the Pin tool.
            _selectedPins.Add(hit);
            _isPinDragging = false;
            SyncSelectedPinPortIndex();
        }
        else
        {
            double px = SnapToConnectionGrid(lx), py = SnapToConnectionGrid(ly);
            var pin = new SymbolPin(px, py, NextUnmappedPortIndex());
            Execute(new PlaceSymbolPinCommand(EditableSymbol, pin));
            int newIdx = EditableSymbol.Pins.IndexOf(pin);
            if (newIdx >= 0) _selectedPins.Add(newIdx);
            _isPinDragging = false;
            SyncSelectedPinPortIndex();
        }
        RebuildOverlay();
    }

    private void SyncSelectedPinPortIndex()
    {
        if (_selectedPins.Count != 1) return;
        int idx = _selectedPins.First();
        if (idx >= 0 && idx < EditableSymbol.Pins.Count)
        {
            _applyingRemap       = true;
            SelectedPinPortIndex = EditableSymbol.Pins[idx].PortIndex;
            _applyingRemap       = false;
        }
    }

    private int HitTestPin(double lx, double ly)
    {
        // Pick radius = max(12 screen-px, half pin grid). Half-grid floor ensures a
        // click anywhere inside the grid cell that placed the pin selects it, making
        // the pick radius commensurate with the placement snap (P=100 → floor = 50).
        double tol  = Math.Max(12.0 / Math.Max(CanvasZoom, 1e-6), PinGrid * 0.5);
        var    pins = EditableSymbol.Pins;
        double minDist = double.MaxValue;
        int    minIdx  = -1;
        for (int i = pins.Count - 1; i >= 0; i--)
        {
            double dx   = pins[i].LocalX - lx;
            double dy   = pins[i].LocalY - ly;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < minDist) { minDist = dist; minIdx = i; }
            if (dist <= tol) return i;
        }
        return -1;
    }

    private int NextUnmappedPortIndex()
    {
        if (PortCount <= 0) return 0;
        var mapped = EditableSymbol.Pins.Select(p => p.PortIndex).ToHashSet();
        for (int i = 0; i < PortCount; i++)
            if (!mapped.Contains(i))
                return i;
        return 0; // all ports mapped — default to 0 (user can remap)
    }

    private IReadOnlyList<int> ComputeUnmappedPorts()
    {
        if (PortCount <= 0) return [];
        var mapped = EditableSymbol.Pins.Select(p => p.PortIndex).ToHashSet();
        var result = new List<int>();
        for (int i = 0; i < PortCount; i++)
            if (!mapped.Contains(i))
                result.Add(i);
        return result;
    }

    // ── Drawing tool helpers ──────────────────────────────────────────────────

    private static bool IsTwoPointDragTool(Tool t) => t is
        Tool.Line or Tool.Rect or Tool.RoundedRect or
        Tool.Circle or Tool.Ellipse or Tool.Arc or
        Tool.Sine or Tool.HalfWave;

    private static bool IsMultiPointTool(Tool t) => t is
        Tool.Polyline or Tool.Polygon or Tool.Triangle or
        Tool.QuadCurve or Tool.CubicCurve;

    private void TryAutoComplete()
    {
        bool complete = ActiveTool switch
        {
            Tool.Triangle   => _drawPoints.Count == 3,
            Tool.QuadCurve  => _drawPoints.Count == 3,
            Tool.CubicCurve => _drawPoints.Count == 4,
            _               => false,
        };
        if (complete) CommitMultiPointDraw();
        else          RebuildOverlay();
    }

    private void FinishMultiPointDraw()
    {
        int minPts = ActiveTool switch
        {
            Tool.Triangle   => 3,
            Tool.QuadCurve  => 3,
            Tool.CubicCurve => 4,
            Tool.Polygon    => 3,
            _               => 2,
        };
        if (_drawPoints.Count >= minPts) CommitMultiPointDraw();
        else                             { _drawPoints.Clear(); RebuildOverlay(); }
    }

    private void CommitMultiPointDraw()
    {
        var prim = BuildCommittedMultiPointPrimitive();
        if (prim is not null && !IsLocked)
            Execute(new PlaceSymbolPrimitiveCommand(EditableSymbol, prim));
        _drawPoints.Clear();
        RebuildOverlay();
    }

    private void CommitText()
    {
        if (!string.IsNullOrWhiteSpace(_textBuffer) && !IsLocked)
            Execute(new PlaceSymbolPrimitiveCommand(EditableSymbol, new TextPrimitive
            {
                Content   = _textBuffer,
                AnchorX   = _textAnchorX,
                AnchorY   = _textAnchorY,
                FontSize  = CurrentFontSize,
                FontStyle = CurrentFontStyle,
                Align     = SymbolTextAlign.Left,
            }));
        _isTypingText = false;
        _textBuffer   = "";
        RebuildOverlay();
    }

    // ── Primitive builders ────────────────────────────────────────────────────

    private SymbolPrimitive? BuildTwoPointPrimitive(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        if (dx == 0 && dy == 0) return null;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        var role = CurrentColorRole;
        var tier = CurrentStrokeTier;

        switch (ActiveTool)
        {
            case Tool.Line:
                return new LinePrimitive(role, tier, x1, y1, x2, y2);

            case Tool.Rect:
                return new RectPrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = (x1 + x2) / 2, Cy = (y1 + y2) / 2,
                    W  = Math.Max(Math.Abs(dx), SmallGrid), H = Math.Max(Math.Abs(dy), SmallGrid),
                };

            case Tool.RoundedRect:
            {
                double rw = Math.Max(Math.Abs(dx), SmallGrid), rh = Math.Max(Math.Abs(dy), SmallGrid);
                return new RoundedRectPrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = (x1 + x2) / 2, Cy = (y1 + y2) / 2,
                    W  = rw, H = rh,
                    Radius = Math.Min(rw, rh) * 0.2,
                };
            }

            case Tool.Circle:
                return new CirclePrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = x1, Cy = y1, R = Math.Max(dist, SmallGrid),
                };

            case Tool.Ellipse:
                return new EllipsePrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = (x1 + x2) / 2, Cy = (y1 + y2) / 2,
                    Rx = Math.Max(Math.Abs(dx) / 2, SmallGrid), Ry = Math.Max(Math.Abs(dy) / 2, SmallGrid),
                };

            case Tool.Arc:
                return new ArcPrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = x1, Cy = y1, R = Math.Max(dist, SmallGrid),
                    StartDeg = 0, SweepDeg = 270,
                };

            case Tool.Sine:
            {
                double adx = Math.Abs(dx), ady = Math.Abs(dy);
                bool horizontal = adx >= ady;
                double length = Math.Max(horizontal ? adx : ady, SmallGrid * 2);
                double amp    = Math.Max(horizontal ? ady / 2 : adx / 2, SmallGrid);
                return new SinePrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = (x1 + x2) / 2, Cy = (y1 + y2) / 2,
                    Length = length, Amp = amp, Cycles = 1,
                    Axis   = horizontal ? SineAxis.Horizontal : SineAxis.Vertical,
                };
            }

            case Tool.HalfWave:
            {
                double adx = Math.Abs(dx), ady = Math.Abs(dy);
                bool horizontal = adx >= ady;
                double length = Math.Max(horizontal ? adx : ady, SmallGrid * 2);
                double amp    = Math.Max(horizontal ? ady / 2 : adx / 2, SmallGrid);
                return new HalfWavePrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = (x1 + x2) / 2, Cy = (y1 + y2) / 2,
                    Length = length, Amp = amp,
                    Axis   = horizontal ? SineAxis.Horizontal : SineAxis.Vertical,
                };
            }

            default:
                return null;
        }
    }

    private SymbolPrimitive? BuildMultiPointPreview(double curX, double curY)
    {
        if (_drawPoints.Count == 0) return null;

        var role = CurrentColorRole;
        var tier = CurrentStrokeTier;
        var pts  = _drawPoints.Select(p => new double[] { p.X, p.Y }).ToList();
        var cur  = new double[] { curX, curY };

        switch (ActiveTool)
        {
            case Tool.Polyline:
                return new PolylinePrimitive { ColorRole = role, StrokeTier = tier, Points = [..pts, cur] };

            case Tool.Polygon or Tool.Triangle:
                return new PolygonPrimitive  { ColorRole = role, StrokeTier = tier, Filled = false, Points = [..pts, cur] };

            case Tool.QuadCurve:
                return _drawPoints.Count switch
                {
                    1 => new LinePrimitive(role, tier, pts[0][0], pts[0][1], curX, curY),
                    2 => new QuadCurvePrimitive
                    {
                        ColorRole = role, StrokeTier = tier,
                        P0X = pts[0][0], P0Y = pts[0][1], CtrlX = pts[1][0], CtrlY = pts[1][1],
                        P2X = curX,      P2Y = curY,
                    },
                    _ => null,
                };

            case Tool.CubicCurve:
                return _drawPoints.Count switch
                {
                    1 => new LinePrimitive(role, tier, pts[0][0], pts[0][1], curX, curY),
                    2 => new PolylinePrimitive { ColorRole = role, StrokeTier = tier, Points = [pts[0], pts[1], cur] },
                    3 => new CubicCurvePrimitive
                    {
                        ColorRole = role, StrokeTier = tier,
                        P0X = pts[0][0], P0Y = pts[0][1],
                        C1X = pts[1][0], C1Y = pts[1][1],
                        C2X = pts[2][0], C2Y = pts[2][1],
                        P3X = curX,      P3Y = curY,
                    },
                    _ => null,
                };

            default:
                return null;
        }
    }

    private SymbolPrimitive? BuildCommittedMultiPointPrimitive()
    {
        var role = CurrentColorRole;
        var tier = CurrentStrokeTier;
        var pts  = _drawPoints.Select(p => new double[] { p.X, p.Y }).ToList();

        return ActiveTool switch
        {
            Tool.Polyline when pts.Count >= 2 =>
                new PolylinePrimitive { ColorRole = role, StrokeTier = tier, Points = pts },

            Tool.Polygon or Tool.Triangle when pts.Count >= 3 =>
                new PolygonPrimitive { ColorRole = role, StrokeTier = tier, Filled = false, Points = pts },

            Tool.QuadCurve when pts.Count == 3 =>
                new QuadCurvePrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    P0X = pts[0][0], P0Y = pts[0][1], CtrlX = pts[1][0], CtrlY = pts[1][1],
                    P2X = pts[2][0], P2Y = pts[2][1],
                },

            Tool.CubicCurve when pts.Count == 4 =>
                new CubicCurvePrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    P0X = pts[0][0], P0Y = pts[0][1],
                    C1X = pts[1][0], C1Y = pts[1][1],
                    C2X = pts[2][0], C2Y = pts[2][1],
                    P3X = pts[3][0], P3Y = pts[3][1],
                },

            _ => null,
        };
    }

    // ── Shared private helpers ─────────────────────────────────────────────────

    private int HitTestTopmost(double lx, double ly)
    {
        // 6 screen-px base pick radius converted to world units.
        double baseTol = 6.0 / Math.Max(CanvasZoom, 1e-6);
        var prims = EditableSymbol.Primitives;
        for (int i = prims.Count - 1; i >= 0; i--)
        {
            var tier  = SymbolGeometry.StrokeTierOf(prims[i]);
            double extra = tier == SymbolStrokeTier.Normal ? 1.5 / Math.Max(CanvasZoom, 1e-6)
                         : tier == SymbolStrokeTier.Thin   ? 0.75 / Math.Max(CanvasZoom, 1e-6)
                         : 0.0;
            if (SymbolGeometry.HitTest(prims[i], lx, ly, baseTol + extra))
                return i;
        }
        return -1;
    }

    private bool HitTestGripper(double lx, double ly)
    {
        if (Overlay.ResizeHandle is not { } h) return false;
        // 7 screen-px gripper half-size converted to world units.
        double halfSize = 7.0 / Math.Max(CanvasZoom, 1e-6);
        double dx = lx - h.X, dy = ly - h.Y;
        return Math.Abs(dx) <= halfSize && Math.Abs(dy) <= halfSize;
    }

    private void UpdateRubberBandSelection()
    {
        double rbX0 = Math.Min(_rbStartX, _rbCurX), rbY0 = Math.Min(_rbStartY, _rbCurY);
        double rbX1 = Math.Max(_rbStartX, _rbCurX), rbY1 = Math.Max(_rbStartY, _rbCurY);
        if (rbX1 - rbX0 < 2 && rbY1 - rbY0 < 2) return;

        // Pins — collect all pins whose centre falls within the band.
        _selectedPins.Clear();
        var pins = EditableSymbol.Pins;
        for (int i = 0; i < pins.Count; i++)
        {
            double px = pins[i].LocalX, py = pins[i].LocalY;
            if (px >= rbX0 && px <= rbX1 && py >= rbY0 && py <= rbY1)
                _selectedPins.Add(i);
        }
        SyncSelectedPinPortIndex();

        // Primitives — collect all whose bboxes intersect the band.
        _selection.Clear();
        var prims = EditableSymbol.Primitives;
        for (int i = 0; i < prims.Count; i++)
        {
            var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(prims[i]);
            if (bx0 <= rbX1 && bx1 >= rbX0 && by0 <= rbY1 && by1 >= rbY0)
                _selection.Add(i);
        }
    }

    private void CancelOp()
    {
        _isDragging        = false;
        _isRubberBanding   = false;
        _liveDx            = 0; _liveDy = 0;
        _isDrawingTwoPoint = false;
        _drawPoints.Clear();
        _isTypingText      = false;
        _textBuffer        = "";
        _isPinDragging     = false;
        _pinGrabX          = 0; _pinGrabY = 0;
        _pinLiveDx         = 0; _pinLiveDy = 0;
        _isResizing        = false;
        _resizePrimIdx     = -1;
        RebuildOverlay();
    }

    // ── Save / load ────────────────────────────────────────────────────────────

    private async Task SaveSymbolAsync(Window? owner)
    {
        if (IsLocked) return;
        if (CurrentSymbolPath is not null)
            PerformSave(CurrentSymbolPath);
        else
            await SaveSymbolAsAsync(owner);
    }

    private async Task SaveSymbolAsAsync(Window? owner)
    {
        if (IsLocked || owner is null) return;

        IStorageFolder? startFolder = null;
        if (CurrentSymbolPath is { Length: > 0 } p)
        {
            string? dir = Path.GetDirectoryName(p);
            if (dir is not null)
                try { startFolder = await owner.StorageProvider.TryGetFolderFromPathAsync(dir); }
                catch { }
        }

        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title                  = "Save Symbol",
            DefaultExtension       = "csym",
            SuggestedFileName      = Path.GetFileNameWithoutExtension(CurrentSymbolPath ?? "symbol"),
            SuggestedStartLocation = startFolder,
            FileTypeChoices        =
            [
                new FilePickerFileType("circuitRF Symbol") { Patterns = ["*.csym"] },
            ],
        });
        if (result is null) return;
        PerformSave(result.Path.LocalPath);
    }

    /// <summary>
    /// Fired after each successful save with the absolute path of the saved .csym file.
    /// WorkspaceViewModel subscribes to invalidate the CellSymbolResolver cache and
    /// trigger re-renders of any open schematics that reference this cell's symbol.
    /// </summary>
    public event Action<string>? SymbolSaved;

    private void PerformSave(string path)
    {
        SymbolPersistence.SaveToFile(path, EditableSymbol.ToSymbol());
        CurrentSymbolPath = path;
        IsDirty           = false;
        SymbolSaved?.Invoke(path);
    }
}
