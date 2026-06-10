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
        _selectedPinIndex = null;
        _isPinDragging    = false;
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

    // ── Select-tool rubber-band state ─────────────────────────────────────────

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

    private int?   _selectedPinIndex;
    private bool   _isPinDragging;
    private double _pinOrigX, _pinOrigY;    // pin position at drag start
    private double _pinLiveDx, _pinLiveDy; // live delta (P-snapped)

    // ── Selected-pin port remap ───────────────────────────────────────────────

    /// <summary>Port index of the currently selected pin; changing it fires RemapSymbolPinCommand.</summary>
    [ObservableProperty] private int _selectedPinPortIndex;
    private bool _applyingRemap;

    partial void OnSelectedPinPortIndexChanged(int value)
    {
        if (_applyingRemap || ActiveTool != Tool.Pin || IsLocked) return;
        if (_selectedPinIndex is not int idx) return;
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

    private static double SnapToP(double v)               => Math.Round(v / SmallGrid) * SmallGrid;
    private static double SnapToConnectionGrid(double v)  => Math.Round(v / PinGrid)   * PinGrid;

    // ── Toolbar commands ─────────────────────────────────────────────────────

    public IRelayCommand       UndoCommand               { get; }
    public IRelayCommand       RedoCommand               { get; }
    public IRelayCommand<string> SetActiveToolCommand    { get; }
    public IRelayCommand<string> SetCurrentStrokeTierCommand { get; }

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

        // Validate selected pin index
        if (_selectedPinIndex.HasValue &&
            (_selectedPinIndex.Value < 0 || _selectedPinIndex.Value >= EditableSymbol.Pins.Count))
            _selectedPinIndex = null;

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

        Overlay = new SymbolEditorOverlay
        {
            SelectedIndices     = _selection.ToHashSet(),
            LiveDragOffset      = (_liveDx, _liveDy),
            RubberBand          = _isRubberBanding
                ? (Math.Min(_rbStartX, _rbCurX), Math.Min(_rbStartY, _rbCurY),
                   Math.Max(_rbStartX, _rbCurX), Math.Max(_rbStartY, _rbCurY))
                : null,
            InProgressPrimitive = inProgress,
            SelectedPinIndex    = _selectedPinIndex ?? -1,
            PinLiveDragOffset   = (_pinLiveDx, _pinLiveDy),
            UnmappedPortIndices = ComputeUnmappedPorts(),
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
            if (_isDragging || _isRubberBanding || _isDrawingTwoPoint || _isPinDragging)
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
            if (_isDragging)
            {
                _liveDx = SnapToP(lx - _dragStartLocalX);
                _liveDy = SnapToP(ly - _dragStartLocalY);
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

        if (ActiveTool == Tool.Pin && _isPinDragging)
        {
            double nx = SnapToConnectionGrid(lx);
            double ny = SnapToConnectionGrid(ly);
            _pinLiveDx = nx - _pinOrigX;
            _pinLiveDy = ny - _pinOrigY;
            RebuildOverlay();
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
            if (_isDragging)
            {
                if ((_liveDx != 0 || _liveDy != 0) && !IsLocked)
                {
                    var prims = _selection
                        .Where(i => i >= 0 && i < EditableSymbol.Primitives.Count)
                        .Select(i => EditableSymbol.Primitives[i])
                        .ToList();
                    if (prims.Count > 0)
                        Execute(new MoveSymbolPrimitivesCommand(EditableSymbol, prims, _liveDx, _liveDy));
                }
                _isDragging = false;
                _liveDx = 0; _liveDy = 0;
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

        if (ActiveTool == Tool.Pin && _isPinDragging)
        {
            _isPinDragging = false;
            if ((_pinLiveDx != 0 || _pinLiveDy != 0) && _selectedPinIndex.HasValue && !IsLocked)
            {
                int idx = _selectedPinIndex.Value;
                if (idx >= 0 && idx < EditableSymbol.Pins.Count)
                {
                    var pin = EditableSymbol.Pins[idx];
                    double newX = _pinOrigX + _pinLiveDx;
                    double newY = _pinOrigY + _pinLiveDy;
                    Execute(new MoveSymbolPinCommand(EditableSymbol, pin, newX, newY));
                    _selectedPinIndex = EditableSymbol.Pins.IndexOf(pin);
                }
            }
            _pinLiveDx = 0; _pinLiveDy = 0;
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
            if (key == Key.Escape)                           { CancelOp(); return; }
            if (key == Key.Enter || key == Key.Return)       { CommitText(); return; }
            if (key == Key.Back && _textBuffer.Length > 0)  { _textBuffer = _textBuffer[..^1]; RebuildOverlay(); }
            return;
        }

        // Pin tool key handling.
        if (ActiveTool == Tool.Pin)
        {
            if ((key == Key.Delete || key == Key.Back) && _selectedPinIndex.HasValue && !IsLocked)
            {
                int idx = _selectedPinIndex.Value;
                if (idx >= 0 && idx < EditableSymbol.Pins.Count)
                {
                    Execute(new DeleteSymbolPinCommand(EditableSymbol, EditableSymbol.Pins[idx]));
                    _selectedPinIndex = null;
                    RebuildOverlay();
                }
                return;
            }
            if (key == Key.Escape)
            {
                _selectedPinIndex = null;
                _isPinDragging    = false;
                _pinLiveDx        = 0; _pinLiveDy = 0;
                RebuildOverlay();
            }
            return;
        }

        if (key == Key.Delete || key == Key.Back)
        {
            if (_selection.Count > 0 && !IsLocked)
            {
                Execute(new DeleteSymbolPrimitivesCommand(EditableSymbol, _selection.ToList()));
                _selection.Clear();
                RebuildOverlay();
            }
            return;
        }

        if (key == Key.Escape)
        {
            if (_isDrawingTwoPoint || _drawPoints.Count > 0 || _isDragging || _isRubberBanding)
                CancelOp();
            else
                ClearSelection();
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
        RebuildOverlay();
    }

    // ── Select-tool helpers ───────────────────────────────────────────────────

    private void SelectToolPress(double lx, double ly, KeyModifiers mods)
    {
        bool shift = (mods & KeyModifiers.Shift) != 0;
        int  hit   = HitTestTopmost(lx, ly);

        if (hit >= 0)
        {
            if (shift)
            {
                if (!_selection.Add(hit)) _selection.Remove(hit);
            }
            else if (!_selection.Contains(hit))
            {
                _selection.Clear();
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
            if (!shift) _selection.Clear();
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
        if (hit >= 0)
        {
            // Select existing pin; begin drag.
            _selectedPinIndex = hit;
            _isPinDragging    = true;
            _pinOrigX         = EditableSymbol.Pins[hit].LocalX;
            _pinOrigY         = EditableSymbol.Pins[hit].LocalY;
            _pinLiveDx        = 0;
            _pinLiveDy        = 0;
            SyncSelectedPinPortIndex();
        }
        else
        {
            // Place new pin at P-snapped position.
            double px      = SnapToConnectionGrid(lx);
            double py      = SnapToConnectionGrid(ly);
            int    portIdx = NextUnmappedPortIndex();
            var    pin     = new SymbolPin(px, py, portIdx);
            Execute(new PlaceSymbolPinCommand(EditableSymbol, pin));
            _selectedPinIndex = EditableSymbol.Pins.IndexOf(pin);
            _isPinDragging    = false;
            SyncSelectedPinPortIndex();
        }
        RebuildOverlay();
    }

    private void SyncSelectedPinPortIndex()
    {
        if (_selectedPinIndex is int idx && idx >= 0 && idx < EditableSymbol.Pins.Count)
        {
            _applyingRemap       = true;
            SelectedPinPortIndex = EditableSymbol.Pins[idx].PortIndex;
            _applyingRemap       = false;
        }
    }

    private int HitTestPin(double lx, double ly)
    {
        const double Tol = 15.0; // local units
        var pins = EditableSymbol.Pins;
        for (int i = pins.Count - 1; i >= 0; i--)
        {
            double dx = pins[i].LocalX - lx;
            double dy = pins[i].LocalY - ly;
            if (dx * dx + dy * dy <= Tol * Tol)
                return i;
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
        const double Tol = 8.0;
        var prims = EditableSymbol.Primitives;
        for (int i = prims.Count - 1; i >= 0; i--)
            if (SymbolGeometry.HitTest(prims[i], lx, ly, Tol))
                return i;
        return -1;
    }

    private void UpdateRubberBandSelection()
    {
        double rbX0 = Math.Min(_rbStartX, _rbCurX), rbY0 = Math.Min(_rbStartY, _rbCurY);
        double rbX1 = Math.Max(_rbStartX, _rbCurX), rbY1 = Math.Max(_rbStartY, _rbCurY);
        if (rbX1 - rbX0 < 2 && rbY1 - rbY0 < 2) return;

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
        _pinLiveDx         = 0; _pinLiveDy = 0;
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
        var result = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Symbol",
            DefaultExtension  = "csym",
            SuggestedFileName = Path.GetFileNameWithoutExtension(CurrentSymbolPath ?? "symbol"),
            FileTypeChoices   =
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
