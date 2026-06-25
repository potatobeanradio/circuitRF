using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Clipboard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Commands.Symbol;
using CircuitRF.Ui.Renderers;
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
        QuadCurve, CubicCurve, Sine, ExpTaper, Text,
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
    /// When non-null, this symbol is cell-bound and the cell declares this many ports (read-only).
    /// When null, this is an orphan/scratch symbol — effective port count = Pins.Count.
    /// </summary>
    public int? ExternalPortCount => EditableSymbol.ExternalPortCount;

    /// <summary>
    /// Label shown in the metadata bar: "Ports: N".
    /// Cell-bound: N = ExternalPortCount.  Orphan: N = Pins.Count.
    /// </summary>
    public string PortsLabel =>
        EditableSymbol.ExternalPortCount is { } ext
            ? $"Ports: {ext}"
            : $"Ports: {EditableSymbol.Pins.Count}";

    /// <summary>
    /// Updates the cell-declared external port count — e.g. after the owning cell's .ccell NumPorts
    /// changed in the cell editor while this tab was inactive. Refreshes the Ports label and the
    /// unmapped-port overlay. No-op when unchanged. Does NOT dirty the document (ExternalPortCount is
    /// cell authority, not symbol data, and is not serialized).
    /// </summary>
    public void SetExternalPortCount(int? count)
    {
        if (EditableSymbol.ExternalPortCount == count) return;
        EditableSymbol.ExternalPortCount = count;
        OnPropertyChanged(nameof(ExternalPortCount));
        OnPropertyChanged(nameof(PortsLabel));
        RebuildOverlay();   // unmapped-port warnings depend on ExternalPortCount
    }

    /// <summary>
    /// Re-reads NumPorts from the owning cell's .ccell and calls <see cref="SetExternalPortCount"/>.
    /// Called on window/view activation to pick up changes made in the cell editor while this tab
    /// was inactive. No-op for orphan symbols (no .ccell) or when the value is unchanged.
    /// </summary>
    public void RefreshPortCountFromDisk()
    {
        if (CurrentSymbolPath is not { } sp) return;
        var symbolDir = Path.GetDirectoryName(sp);
        if (symbolDir is null) return;
        var cellDir = Path.GetDirectoryName(symbolDir);
        if (cellDir is null) return;
        var ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        if (!File.Exists(ccellPath)) return;
        try { SetExternalPortCount(CellPersistence.LoadFromFile(ccellPath).NumPorts); }
        catch { }
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

    /// <summary>
    /// During a resize, the live-scaled transient clone of the selected primitive; null otherwise.
    /// Set before <see cref="Overlay"/> so the inspector can read it synchronously
    /// when it reacts to the <see cref="Overlay"/> PropertyChanged notification.
    /// </summary>
    public SymbolPrimitive? ResizeLivePrimitive { get; private set; }

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
    /// <summary>True while a text primitive is being typed; key handlers read this to suppress shortcuts.</summary>
    public  bool   IsTypingText => _isTypingText;
    private double _textAnchorX, _textAnchorY;
    private string _textBuffer = "";

    // ── Pin tool state ────────────────────────────────────────────────────────

    private readonly HashSet<int> _selectedPins = [];
    private bool   _isPinDragging;
    private double _pinOrigX, _pinOrigY;    // grabbed pin's position at drag start (delta reference)
    private double _pinGrabX, _pinGrabY;    // raw cursor position at grab
    private double _pinLiveDx, _pinLiveDy; // live delta applied to all selected pins (P-snapped)

    // ── Resize gripper state ─────────────────────────────────────────────────

    private enum ResizeCorner { None, BottomRight, TopLeft }

    private bool   _isResizing;
    private int    _resizePrimIdx = -1;
    private double _resizeBbX0, _resizeBbY0, _resizeBbX1, _resizeBbY1; // original bbox
    private double _resizeLiveX1, _resizeLiveY1;                         // tracked bottom-right (BR handle)
    private double _resizeLiveX0, _resizeLiveY0;                         // tracked top-left (TL handle)
    private bool   _resizeCornerIsTL;                                     // which corner is being dragged
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

    // ── Inline text edit request ──────────────────────────────────────────────

    /// <summary>Payload raised on double-click of a text primitive; the view opens an inline editor.</summary>
    public readonly record struct TextEditRequest(int Index, double WorldX, double WorldY,
                                                  string Content, double FontSize);

    public event Action<TextEditRequest>? TextEditRequested;

    // ── Canvas zoom (set by SymbolEditorCanvas; used to convert screen-px tolerances) ──

    /// <summary>
    /// Current canvas zoom (pixels per world unit).  Set by <see cref="SymbolEditorCanvas"/>
    /// on every zoom change so that hit-test tolerances are expressed in screen-pixel units.
    /// </summary>
    public double CanvasZoom { get; set; } = 1.0;

    /// <summary>
    /// Tri-state snap for art primitives.  Pins ALWAYS snap to P=100 regardless.
    /// ConnectionGrid = snap to P=100, FineGrid = snap to p=5, None = free.
    /// </summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SnapModeTooltip))]
    private SnapMode _snapMode = SnapMode.FineGrid;

    public string SnapModeTooltip => SnapMode switch
    {
        SnapMode.ConnectionGrid => "Snap: Connection Grid  (G)",
        SnapMode.FineGrid       => "Snap: Fine Grid  (G)",
        _                       => "Snap: Off  (G)",
    };

    [RelayCommand]
    private void CycleSnapMode()
    {
        SnapMode = SnapMode switch
        {
            SnapMode.ConnectionGrid => SnapMode.FineGrid,
            SnapMode.FineGrid       => SnapMode.None,
            _                       => SnapMode.ConnectionGrid,
        };
    }

    private double SnapToP(double v) => SnapMode switch
    {
        SnapMode.ConnectionGrid => Math.Round(v / PinGrid)   * PinGrid,
        SnapMode.FineGrid       => Math.Round(v / SmallGrid) * SmallGrid,
        _                       => v,
    };

    private static double SnapToConnectionGrid(double v)
        => Math.Round(v / PinGrid) * PinGrid;

    private (double X, double Y)? SingleSelectedTextAnchor()
    {
        if (_selection.Count != 1) return null;
        int idx = _selection.First();
        if (idx < 0 || idx >= EditableSymbol.Primitives.Count) return null;
        return EditableSymbol.Primitives[idx] is TextPrimitive t ? (t.AnchorX, t.AnchorY) : null;
    }

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
            // Dirty mirrors the stack's saved baseline: it clears on undo back to the last save and
            // re-dirties on the next edit (matches SchematicDocument and the project-tree cell).
            if (e.PropertyName is nameof(UndoRedoStack.IsModified)) IsDirty = !IsLocked && _undoRedo.IsModified;
        };

        SaveSymbolCommand   = new AsyncRelayCommand<Window?>(SaveSymbolAsync);
        SaveSymbolAsCommand = new AsyncRelayCommand<Window?>(SaveSymbolAsAsync);

        EditableSymbol.Changed += (_, _) => { RebuildRenderSnapshot(); OnPropertyChanged(nameof(PortsLabel)); };
        RebuildRenderSnapshot();
    }

    // ── Command execution ─────────────────────────────────────────────────────

    /// <summary>
    /// Execute a command on the undo stack.  Dirty state follows <see cref="UndoRedoStack.IsModified"/>
    /// (wired in the constructor), so it clears on undo back to the saved baseline.
    /// </summary>
    public void Execute(IUiCommand cmd) => _undoRedo.Execute(cmd);

    /// <summary>Commits an inline text edit (undoable). No-op when locked, unchanged, or the index is
    /// stale / not a TextPrimitive.</summary>
    public void CommitTextEdit(int index, string newContent)
    {
        if (IsLocked || index < 0 || index >= EditableSymbol.Primitives.Count) return;
        if (EditableSymbol.Primitives[index] is not TextPrimitive tp) return;
        if (tp.Content == newContent) return;
        Execute(new SetTextPrimitiveCommand(EditableSymbol, tp, newContent, tp.FontSize, tp.FontStyle));
    }

    // ── Text input (from canvas TextInput event) ──────────────────────────────

    public void OnTextInput(string text)
    {
        if (!_isTypingText || string.IsNullOrEmpty(text)) return;
        // Backspace/Delete/Enter/Tab arrive here as control chars on some platforms and would render
        // as tofu (□). Deletion is handled in OnKeyDown (Key.Back); keep only printable text here.
        string printable = new string(text.Where(c => !char.IsControl(c)).ToArray());
        if (printable.Length == 0) return;
        _textBuffer += printable;
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
                VAlign    = SymbolTextVAlign.Top,
            };

        // Resize handles: BR and TL corners of single selected prim's bbox.
        // Layer 3: Text primitives show no grippers.
        // Layer 1 fix: during resize, both handles track the live-scaled clone's bbox.
        (double X, double Y)? resizeHandle   = null;
        (double X, double Y)? resizeHandleTL = null;
        SymbolPrimitive? resizeLivePrim = null;
        if (!IsLocked && ActiveTool == Tool.Select && _selection.Count == 1 && !_isDragging && !_isRubberBanding)
        {
            int idx = _selection.First();
            if (idx >= 0 && idx < EditableSymbol.Primitives.Count)
            {
                var committedPrim = EditableSymbol.Primitives[idx];
                if (committedPrim is not TextPrimitive)  // Layer 3: no grippers for Text
                {
                    if (_isResizing)
                    {
                        double origW = _resizeBbX1 - _resizeBbX0;
                        double origH = _resizeBbY1 - _resizeBbY0;
                        if (Math.Abs(origW) > 1e-9 && Math.Abs(origH) > 1e-9)
                        {
                            double sx, sy, anchorX, anchorY;
                            if (_resizeCornerIsTL)
                            {
                                sx      = (_resizeBbX1 - _resizeLiveX0) / origW;
                                sy      = (_resizeBbY1 - _resizeLiveY0) / origH;
                                anchorX = _resizeBbX1;
                                anchorY = _resizeBbY1;
                            }
                            else
                            {
                                sx      = (_resizeLiveX1 - _resizeBbX0) / origW;
                                sy      = (_resizeLiveY1 - _resizeBbY0) / origH;
                                anchorX = _resizeBbX0;
                                anchorY = _resizeBbY0;
                            }
                            if (Math.Abs(sx) > 1e-6 && Math.Abs(sy) > 1e-6)
                            {
                                var clone = SymbolGeometry.Clone(committedPrim);
                                SymbolGeometry.ScaleBy(clone, anchorX, anchorY, sx, sy);
                                resizeLivePrim = clone;
                                // Both handles from the live bbox (Layer 1 fix)
                                var (lx0, ly0, lx1, ly1) = SymbolGeometry.BboxOf(resizeLivePrim);
                                resizeHandle   = (lx1, ly1);
                                resizeHandleTL = (lx0, ly0);
                            }
                        }
                        // Fallback if scale is degenerate: committed bbox
                        if (resizeLivePrim is null)
                        {
                            var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(committedPrim);
                            resizeHandle   = (bx1, by1);
                            resizeHandleTL = (bx0, by0);
                        }
                    }
                    else
                    {
                        var (bx0, by0, bx1, by1) = SymbolGeometry.BboxOf(committedPrim);
                        resizeHandle   = (bx1, by1);
                        resizeHandleTL = (bx0, by0);
                    }
                }
            }
        }

        // Set before Overlay so the inspector reads the correct value when reacting to Overlay changes.
        ResizeLivePrimitive = resizeLivePrim;

        Overlay = new SymbolEditorOverlay
        {
            SelectedIndices     = _selection.ToHashSet(),
            LiveDragOffset      = (_liveDx, _liveDy),
            RubberBand          = _isRubberBanding
                ? (Math.Min(_rbStartX, _rbCurX), Math.Min(_rbStartY, _rbCurY),
                   Math.Max(_rbStartX, _rbCurX), Math.Max(_rbStartY, _rbCurY))
                : null,
            InProgressPrimitive = inProgress ?? resizeLivePrim,
            SelectedPinIndices  = _selectedPins.ToHashSet(),
            PinLiveDragOffset   = (_pinLiveDx, _pinLiveDy),
            UnmappedPortIndices = ComputeUnmappedPorts(),
            ResizeHandle        = resizeHandle,
            ResizeHandleTopLeft = resizeHandleTL,
        };
    }

    // ── Pointer handlers ──────────────────────────────────────────────────────

    public void OnPointerPressed(double lx, double ly, KeyModifiers mods, int clickCount = 1)
    {
        if (ActiveTool == Tool.Select)
        {
            SelectToolPress(lx, ly, mods, clickCount);
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
                double origW = _resizeBbX1 - _resizeBbX0;
                double origH = _resizeBbY1 - _resizeBbY0;
                if (_resizeCornerIsTL)
                {
                    // TL handle: anchor = BR, move = TL.
                    if (_resizeShift && origW > 1e-9 && origH > 1e-9)
                    {
                        double sx = (_resizeBbX1 - nx) / origW;
                        double sy = (_resizeBbY1 - ny) / origH;
                        double s  = Math.Min(Math.Abs(sx), Math.Abs(sy));
                        nx = _resizeBbX1 - origW * s;
                        ny = _resizeBbY1 - origH * s;
                    }
                    _resizeLiveX0 = nx;
                    _resizeLiveY0 = ny;
                }
                else
                {
                    // BR handle: anchor = TL, move = BR.
                    if (_resizeShift && origW > 1e-9 && origH > 1e-9)
                    {
                        double sx = (nx - _resizeBbX0) / origW;
                        double sy = (ny - _resizeBbY0) / origH;
                        double s  = Math.Min(Math.Abs(sx), Math.Abs(sy));
                        nx = _resizeBbX0 + origW * s;
                        ny = _resizeBbY0 + origH * s;
                    }
                    _resizeLiveX1 = nx;
                    _resizeLiveY1 = ny;
                }
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
                else if (SingleSelectedTextAnchor() is { } ta)
                {
                    // Text: snap the (Align,VAlign) ANCHOR to absolute grid coordinates so the corner lands
                    // on grid intersections — not in grid-sized steps relative to an off-grid start (which
                    // is what a rotate/resize leaves the anchor as).
                    _liveDx = SnapToP(ta.X + (lx - _dragStartLocalX)) - ta.X;
                    _liveDy = SnapToP(ta.Y + (ly - _dragStartLocalY)) - ta.Y;
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
                if (!IsLocked && Math.Abs(origW) > 1e-9 && Math.Abs(origH) > 1e-9)
                {
                    double sx, sy, refX, refY;
                    bool changed;
                    if (_resizeCornerIsTL)
                    {
                        // TL handle: anchor = BR corner, moving = TL corner.
                        sx      = (_resizeBbX1 - _resizeLiveX0) / origW;
                        sy      = (_resizeBbY1 - _resizeLiveY0) / origH;
                        refX    = _resizeBbX1;
                        refY    = _resizeBbY1;
                        changed = Math.Abs(_resizeLiveX0 - _resizeBbX0) > 0.1
                               || Math.Abs(_resizeLiveY0 - _resizeBbY0) > 0.1;
                    }
                    else
                    {
                        // BR handle: anchor = TL corner, moving = BR corner.
                        sx      = (_resizeLiveX1 - _resizeBbX0) / origW;
                        sy      = (_resizeLiveY1 - _resizeBbY0) / origH;
                        refX    = _resizeBbX0;
                        refY    = _resizeBbY0;
                        changed = Math.Abs(_resizeLiveX1 - _resizeBbX1) > 0.1
                               || Math.Abs(_resizeLiveY1 - _resizeBbY1) > 0.1;
                    }
                    if (changed && Math.Abs(sx) > 1e-6 && Math.Abs(sy) > 1e-6
                        && _resizePrimIdx >= 0 && _resizePrimIdx < EditableSymbol.Primitives.Count)
                    {
                        Execute(new Commands.Symbol.ResizeSymbolPrimitiveCommand(
                            EditableSymbol, EditableSymbol.Primitives[_resizePrimIdx],
                            refX, refY, sx, sy));
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

        // G key — cycle snap mode (art only; pins always snap to P).
        if (key == Key.G && (mods & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            CycleSnapMode(); return;
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

        // Arrow keys — nudge selected primitives (p=5) and/or pins (P=100).
        if (!IsLocked && ActiveTool == Tool.Select &&
            (key == Key.Left || key == Key.Right || key == Key.Up || key == Key.Down))
        {
            bool hasPrims = _selection.Any(i => i >= 0 && i < EditableSymbol.Primitives.Count);
            bool hasPins  = _selectedPins.Any(i => i >= 0 && i < EditableSymbol.Pins.Count);
            if (hasPrims || hasPins)
            {
                double adx = key == Key.Left ? -SmallGrid : key == Key.Right ? SmallGrid : 0;
                double ady = key == Key.Up   ? -SmallGrid : key == Key.Down  ? SmallGrid : 0;
                double pdx = key == Key.Left ? -PinGrid   : key == Key.Right ? PinGrid   : 0;
                double pdy = key == Key.Up   ? -PinGrid   : key == Key.Down  ? PinGrid   : 0;

                IUiCommand? cmd = null;
                if (hasPrims)
                {
                    var prims = _selection
                        .Where(i => i >= 0 && i < EditableSymbol.Primitives.Count)
                        .Select(i => EditableSymbol.Primitives[i]).ToList();
                    cmd = new MoveSymbolPrimitivesCommand(EditableSymbol, prims, adx, ady);
                }
                if (hasPins)
                {
                    var moves = _selectedPins
                        .Where(i => i >= 0 && i < EditableSymbol.Pins.Count)
                        .Select(i => EditableSymbol.Pins[i])
                        .Select(p => (p, SnapToConnectionGrid(p.LocalX + pdx), SnapToConnectionGrid(p.LocalY + pdy)));
                    var pinCmd = new MoveMultipleSymbolPinsCommand(EditableSymbol, moves);
                    cmd = cmd is null ? pinCmd : new CompositeCommand(cmd, pinCmd);
                }
                if (cmd is not null) { Execute(cmd); RebuildOverlay(); }
            }
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

    /// <summary>Selects every primitive in the symbol (Ctrl/Cmd+A). Pins are a separate selection set, so
    /// they are left unselected. Safe on an empty symbol.</summary>
    public void SelectAll()
    {
        _selectedPins.Clear();
        _selection.Clear();
        for (int i = 0; i < EditableSymbol.Primitives.Count; i++) _selection.Add(i);
        RebuildOverlay();
    }

    // ── Select-tool helpers ───────────────────────────────────────────────────

    private void SelectToolPress(double lx, double ly, KeyModifiers mods, int clickCount = 1)
    {
        // Double-click a text primitive → request inline content edit (handled by the view).
        if (clickCount >= 2 && !IsLocked)
        {
            int th = HitTestTopmost(lx, ly);
            if (th >= 0 && EditableSymbol.Primitives[th] is TextPrimitive tp)
            {
                _selection.Clear();
                _selectedPins.Clear();
                _selection.Add(th);
                RebuildOverlay();
                var (bx0, by0, _, _) = SymbolGeometry.BboxOf(tp);
                TextEditRequested?.Invoke(new TextEditRequest(th, bx0, by0, tp.Content, tp.FontSize));
                return;
            }
        }

        bool shift = (mods & KeyModifiers.Shift) != 0;

        // Check gripper first so a resize drag doesn't accidentally move the prim.
        var gripCorner = !IsLocked && _selection.Count == 1
            ? HitTestGripper(lx, ly) : ResizeCorner.None;
        if (gripCorner != ResizeCorner.None)
        {
            int idx = _selection.First();
            if (idx >= 0 && idx < EditableSymbol.Primitives.Count)
            {
                var (x0, y0, x1, y1) = SymbolGeometry.BboxOf(EditableSymbol.Primitives[idx]);
                _isResizing       = true;
                _resizePrimIdx    = idx;
                _resizeBbX0       = x0; _resizeBbY0 = y0;
                _resizeBbX1       = x1; _resizeBbY1 = y1;
                _resizeCornerIsTL = gripCorner == ResizeCorner.TopLeft;
                _resizeLiveX1     = x1; _resizeLiveY1 = y1; // BR tracking (used when !_resizeCornerIsTL)
                _resizeLiveX0     = x0; _resizeLiveY0 = y0; // TL tracking (used when  _resizeCornerIsTL)
                _resizeShift      = shift || EditableSymbol.Primitives[idx] is BitmapPrimitive;
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
        // Hit radius = glyph radius + 4 screen-px, converted to local units.
        // Glyph radius mirrors the renderer formula: max(3, zoom*5) px → 5 local units.
        // The 4 px margin is constant in screen space: just beyond the visible dot edge.
        double glyphR = Math.Max(3.0, CanvasZoom * 5.0);
        double tol    = (glyphR + 4.0) / Math.Max(CanvasZoom, 1e-6);
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
        var ext = EditableSymbol.ExternalPortCount;
        if (ext is null or <= 0)
            return EditableSymbol.Pins.Count; // orphan: auto-increment port indices
        var mapped = EditableSymbol.Pins.Select(p => p.PortIndex).ToHashSet();
        for (int i = 0; i < ext; i++)
            if (!mapped.Contains(i))
                return i;
        return EditableSymbol.Pins.Count; // all declared ports mapped
    }

    private IReadOnlyList<int> ComputeUnmappedPorts()
    {
        var ext = EditableSymbol.ExternalPortCount;
        if (ext is null or <= 0) return []; // orphan or 0-port cell → no warning
        var mapped = EditableSymbol.Pins.Select(p => p.PortIndex).ToHashSet();
        var result = new List<int>();
        for (int i = 0; i < ext; i++)
            if (!mapped.Contains(i))
                result.Add(i);
        return result;
    }

    // ── Drawing tool helpers ──────────────────────────────────────────────────

    private static bool IsTwoPointDragTool(Tool t) => t is
        Tool.Line or Tool.Rect or Tool.RoundedRect or
        Tool.Circle or Tool.Ellipse or Tool.Arc or
        Tool.Sine or Tool.ExpTaper;

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
                VAlign    = SymbolTextVAlign.Top,
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

            case Tool.ExpTaper:
            {
                double adx = Math.Abs(dx), ady = Math.Abs(dy);
                bool horizontal = adx >= ady;
                double length = Math.Max(horizontal ? adx : ady, SmallGrid * 2);
                double wide   = Math.Max(horizontal ? ady : adx, SmallGrid * 2);
                return new ExponentialTaperPrimitive
                {
                    ColorRole = role, StrokeTier = tier,
                    Cx = (x1 + x2) / 2, Cy = (y1 + y2) / 2,
                    L  = length, W1 = wide, W2 = Math.Max(wide / 4.0, SmallGrid),
                    Axis = horizontal ? SineAxis.Horizontal : SineAxis.Vertical,
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

    private ResizeCorner HitTestGripper(double lx, double ly)
    {
        // 7 screen-px gripper half-size converted to world units.
        double halfSize = 7.0 / Math.Max(CanvasZoom, 1e-6);
        if (Overlay.ResizeHandle is { } br)
        {
            double dx = lx - br.X, dy = ly - br.Y;
            if (Math.Abs(dx) <= halfSize && Math.Abs(dy) <= halfSize)
                return ResizeCorner.BottomRight;
        }
        if (Overlay.ResizeHandleTopLeft is { } tl)
        {
            double dx = lx - tl.X, dy = ly - tl.Y;
            if (Math.Abs(dx) <= halfSize && Math.Abs(dy) <= halfSize)
                return ResizeCorner.TopLeft;
        }
        return ResizeCorner.None;
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
        _resizeCornerIsTL  = false;
        _resizeLiveX0      = 0; _resizeLiveY0 = 0;
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

    /// <summary>Raised when a save fails (e.g. a read-only / unwritable location). The workspace routes
    /// it to the Messages pane. A failed save must surface an error, never crash the app.</summary>
    public event Action<string>? SaveError;

    internal void PerformSave(string path)   // internal for the save-error regression test
    {
        try
        {
            SymbolPersistence.SaveToFile(path, EditableSymbol.ToSymbol());
        }
        catch (Exception ex)
        {
            // Do NOT mark the document saved or raise SymbolSaved — the file was not written.
            SaveError?.Invoke($"Couldn't save symbol to '{path}': {ex.Message}");
            return;
        }
        CurrentSymbolPath = path;
        _undoRedo.MarkSaved();   // record the clean baseline → IsModified false → IsDirty false
        SymbolSaved?.Invoke(path);
    }

    // ── Clipboard ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Copies (or cuts) the current selection to the system clipboard as symbol JSON.
    /// No-op if nothing is selected or the symbol is locked.
    /// </summary>
    /// <summary>
    /// Creates a <see cref="BitmapPrimitive"/> at the given local coordinates from a file dropped on the canvas.
    /// No-ops if <paramref name="path"/> is null/empty or the symbol is locked.
    /// </summary>
    public void DropBitmap(string path, double worldX, double worldY)
    {
        if (IsLocked || string.IsNullOrEmpty(path)) return;

        // Size to the image's native aspect ratio (fit ~200 world units on the long edge)
        // so non-4:3 images are not skewed. Falls back to 200×150 if the file can't be decoded.
        const double fit = 200.0;
        double w = 200.0, h = 150.0;
        if (SchematicRenderer.TryGetBitmapPixelSize(path) is { } px && px.Width > 0 && px.Height > 0)
        {
            if (px.Width >= px.Height) { w = fit; h = fit * px.Height / px.Width; }
            else                       { h = fit; w = fit * px.Width  / px.Height; }
        }

        var prim = new BitmapPrimitive
        {
            ImagePathRef = path,
            X   = SnapToP(worldX),
            Y   = SnapToP(worldY),
            W   = w,
            H   = h,
            Opacity = 1.0,
        };
        Execute(new PlaceSymbolPrimitiveCommand(EditableSymbol, prim));
        RebuildOverlay();
    }

    // ── Bitmap context-menu actions ───────────────────────────────────────────

    /// <summary>Right-click hit-test: returns the target if a BitmapPrimitive is under (lx,ly).</summary>
    public (int PrimIdx, string Path, bool IsBroken)? OnPointerRightPressed(double lx, double ly)
    {
        int idx = HitTestTopmost(lx, ly);
        if (idx < 0) return null;
        if (EditableSymbol.Primitives[idx] is not BitmapPrimitive bmp) return null;
        bool isBroken = string.IsNullOrEmpty(bmp.ImagePathRef) || !File.Exists(bmp.ImagePathRef);
        return (idx, bmp.ImagePathRef, isBroken);
    }

    public void ResolveBitmapPath(int primIdx, string newPath)
    {
        if (IsLocked || primIdx < 0 || primIdx >= EditableSymbol.Primitives.Count) return;
        if (EditableSymbol.Primitives[primIdx] is not BitmapPrimitive bmp) return;
        SchematicRenderer.InvalidateBitmapCache(bmp.ImagePathRef);
        Execute(new SetSymbolPrimitiveFieldCommand<string>(
            EditableSymbol, "Resolve Bitmap Path", bmp.ImagePathRef, newPath,
            v => bmp.ImagePathRef = v));
    }

    public void RefreshBitmapCache(int primIdx)
    {
        if (primIdx < 0 || primIdx >= EditableSymbol.Primitives.Count) return;
        if (EditableSymbol.Primitives[primIdx] is not BitmapPrimitive bmp) return;
        SchematicRenderer.InvalidateBitmapCache(bmp.ImagePathRef);
        RebuildOverlay();
    }

    public async Task ClipboardCopyAsync(IClipboard clipboard, bool cut = false)
    {
        var prims = _selection
            .Where(i => i >= 0 && i < EditableSymbol.Primitives.Count)
            .Select(i => EditableSymbol.Primitives[i])
            .ToList();
        var pins = _selectedPins
            .Where(i => i >= 0 && i < EditableSymbol.Pins.Count)
            .Select(i => EditableSymbol.Pins[i])
            .ToList();

        if (prims.Count == 0 && pins.Count == 0) return;

        await SymbolClipboard.CopyAsync(clipboard, prims, pins);

        if (cut && !IsLocked)
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
        }
    }

    /// <summary>
    /// Pastes from the system clipboard into the symbol (undoable).
    /// No-op if the clipboard contains no recognized symbol payload or the symbol is locked.
    /// </summary>
    public async Task ClipboardPasteAsync(IClipboard clipboard)
    {
        if (IsLocked) return;

        var result = await SymbolClipboard.PasteAsync(clipboard);
        if (result is null) return;
        var (prims, pins, _) = result.Value;
        if (prims.Count == 0 && pins.Count == 0) return;

        Execute(new PasteSymbolSelectionCommand(
            EditableSymbol, prims, pins,
            (primIndices, pinIndices) =>
            {
                _selection.Clear();
                foreach (var i in primIndices) _selection.Add(i);
                _selectedPins.Clear();
                foreach (var i in pinIndices) _selectedPins.Add(i);
            }));
        RebuildOverlay();
    }
}
