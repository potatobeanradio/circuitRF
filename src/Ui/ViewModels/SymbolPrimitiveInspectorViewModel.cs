using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CircuitRF.Ui.Commands.Symbol;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.ViewModels;

/// <summary>
/// Exposes every field of the selected SymbolPrimitive (or Pin) as editable, live, undoable bindings.
/// Each field change fires a SetSymbolPrimitiveFieldCommand through the VM's undo stack.
/// Framework-free (no Avalonia types).
/// </summary>
public sealed partial class SymbolPrimitiveInspectorViewModel : ObservableObject
{
    private SymbolEditorViewModel? _vm;
    private SymbolPrimitive?       _prim;
    private int                    _primIdx = -1;
    private bool                   _isRefreshing;

    // ── Static options (ComboBox ItemsSources) ────────────────────────────────

    public static SymbolStrokeTier[] StrokeTierOptions { get; } = Enum.GetValues<SymbolStrokeTier>();
    public static SineAxis[]         AxisOptions       { get; } = Enum.GetValues<SineAxis>();
    public static SymbolTextAlign[]  AlignOptions      { get; } = Enum.GetValues<SymbolTextAlign>();
    public static SymbolFontStyle[]  FontStyleOptions  { get; } = Enum.GetValues<SymbolFontStyle>();

    // ── Empty / header state ──────────────────────────────────────────────────

    [ObservableProperty] private bool   _isEmptyState = true;
    [ObservableProperty] private string _emptyMessage = "Select a primitive to inspect.";
    public bool IsNotEmptyState => !IsEmptyState;
    partial void OnIsEmptyStateChanged(bool oldValue, bool newValue)
        => OnPropertyChanged(nameof(IsNotEmptyState));

    [ObservableProperty] private string _typeName = "";

    // ── Visibility flags ──────────────────────────────────────────────────────

    [ObservableProperty] private bool _showLineCoords;
    [ObservableProperty] private bool _showCxCy;
    [ObservableProperty] private bool _showWH;
    [ObservableProperty] private bool _showRadius;
    [ObservableProperty] private bool _showRxRy;
    [ObservableProperty] private bool _showCornerRadius;
    [ObservableProperty] private bool _showArcAngles;
    [ObservableProperty] private bool _showSineFields;
    [ObservableProperty] private bool _showExpTaperFields;
    [ObservableProperty] private bool _showQuadCurve;
    [ObservableProperty] private bool _showCubicCurve;
    [ObservableProperty] private bool _showFilled;
    [ObservableProperty] private bool _showStrokeTier;
    [ObservableProperty] private bool _showBitmapFields;

    // ── Line coords ───────────────────────────────────────────────────────────

    [ObservableProperty] private double _fieldX1;
    [ObservableProperty] private double _fieldY1;
    [ObservableProperty] private double _fieldX2;
    [ObservableProperty] private double _fieldY2;

    partial void OnFieldX1Changed(double oldValue, double newValue) => ApplyDouble("X1", oldValue, newValue,
        _prim is LinePrimitive p ? v => p.X1 = v : null);
    partial void OnFieldY1Changed(double oldValue, double newValue) => ApplyDouble("Y1", oldValue, newValue,
        _prim is LinePrimitive p ? v => p.Y1 = v : null);
    partial void OnFieldX2Changed(double oldValue, double newValue) => ApplyDouble("X2", oldValue, newValue,
        _prim is LinePrimitive p ? v => p.X2 = v : null);
    partial void OnFieldY2Changed(double oldValue, double newValue) => ApplyDouble("Y2", oldValue, newValue,
        _prim is LinePrimitive p ? v => p.Y2 = v : null);

    // ── Cx / Cy (shared by Rect, RoundedRect, Circle, Ellipse, Arc, Sine) ──────

    [ObservableProperty] private double _fieldCx;
    [ObservableProperty] private double _fieldCy;

    partial void OnFieldCxChanged(double oldValue, double newValue) => ApplyDouble("Cx", oldValue, newValue,
        _prim switch {
            RectPrimitive             p => v => p.Cx = v,
            RoundedRectPrimitive      p => v => p.Cx = v,
            CirclePrimitive           p => v => p.Cx = v,
            EllipsePrimitive          p => v => p.Cx = v,
            ArcPrimitive              p => v => p.Cx = v,
            SinePrimitive             p => v => p.Cx = v,
            ExponentialTaperPrimitive p => v => p.Cx = v,
            _ => null,
        });
    partial void OnFieldCyChanged(double oldValue, double newValue) => ApplyDouble("Cy", oldValue, newValue,
        _prim switch {
            RectPrimitive             p => v => p.Cy = v,
            RoundedRectPrimitive      p => v => p.Cy = v,
            CirclePrimitive           p => v => p.Cy = v,
            EllipsePrimitive          p => v => p.Cy = v,
            ArcPrimitive              p => v => p.Cy = v,
            SinePrimitive             p => v => p.Cy = v,
            ExponentialTaperPrimitive p => v => p.Cy = v,
            _ => null,
        });

    // ── W / H ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private double _fieldW;
    [ObservableProperty] private double _fieldH;

    partial void OnFieldWChanged(double oldValue, double newValue) => ApplyDouble("W", oldValue, newValue,
        _prim switch {
            RectPrimitive        p => v => p.W = v,
            RoundedRectPrimitive p => v => p.W = v,
            _ => null,
        });
    partial void OnFieldHChanged(double oldValue, double newValue) => ApplyDouble("H", oldValue, newValue,
        _prim switch {
            RectPrimitive        p => v => p.H = v,
            RoundedRectPrimitive p => v => p.H = v,
            _ => null,
        });

    // ── R (radius / single for Circle, Arc) ───────────────────────────────────

    [ObservableProperty] private double _fieldRadius;

    partial void OnFieldRadiusChanged(double oldValue, double newValue) => ApplyDouble("R", oldValue, newValue,
        _prim switch {
            CirclePrimitive p => v => p.R = v,
            ArcPrimitive    p => v => p.R = v,
            _ => null,
        });

    // ── Rx / Ry (Ellipse) ─────────────────────────────────────────────────────

    [ObservableProperty] private double _fieldRx;
    [ObservableProperty] private double _fieldRy;

    partial void OnFieldRxChanged(double oldValue, double newValue) => ApplyDouble("Rx", oldValue, newValue,
        _prim is EllipsePrimitive p ? v => p.Rx = v : null);
    partial void OnFieldRyChanged(double oldValue, double newValue) => ApplyDouble("Ry", oldValue, newValue,
        _prim is EllipsePrimitive p ? v => p.Ry = v : null);

    // ── Corner radius (RoundedRect) ───────────────────────────────────────────

    [ObservableProperty] private double _fieldCornerRadius;

    partial void OnFieldCornerRadiusChanged(double oldValue, double newValue) => ApplyDouble("Radius", oldValue, newValue,
        _prim is RoundedRectPrimitive p ? v => p.Radius = v : null);

    // ── Arc angles ────────────────────────────────────────────────────────────

    [ObservableProperty] private double _fieldStartDeg;
    [ObservableProperty] private double _fieldSweepDeg;

    partial void OnFieldStartDegChanged(double oldValue, double newValue) => ApplyDouble("StartDeg", oldValue, newValue,
        _prim is ArcPrimitive p ? v => p.StartDeg = v : null);
    partial void OnFieldSweepDegChanged(double oldValue, double newValue) => ApplyDouble("SweepDeg", oldValue, newValue,
        _prim is ArcPrimitive p ? v => p.SweepDeg = v : null);

    // ── Sine fields ───────────────────────────────────────────────────────────

    [ObservableProperty] private double   _fieldAmp;
    [ObservableProperty] private double   _fieldCycles;
    [ObservableProperty] private double   _fieldLength;
    [ObservableProperty] private SineAxis _fieldAxis;
    [ObservableProperty] private int      _fieldPtsPerCycle;

    partial void OnFieldAmpChanged(double oldValue, double newValue) => ApplyDouble("Amp", oldValue, newValue,
        _prim is SinePrimitive p ? v => p.Amp = v : null);
    partial void OnFieldCyclesChanged(double oldValue, double newValue) => ApplyDouble("Cycles", oldValue, newValue,
        _prim is SinePrimitive p ? v => p.Cycles = v : null);
    partial void OnFieldLengthChanged(double oldValue, double newValue) => ApplyDouble("Length", oldValue, newValue,
        _prim is SinePrimitive p ? v => p.Length = v : null);
    partial void OnFieldPtsPerCycleChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _prim is not SinePrimitive sp || _vm is null || oldValue == newValue) return;
        int clamped = Math.Max(newValue, 1);
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<int>(_vm.EditableSymbol, "PtsPerCycle", oldValue, clamped,
            v => sp.PtsPerCycle = v));
    }
    partial void OnFieldAxisChanged(SineAxis oldValue, SineAxis newValue)
    {
        if (_isRefreshing || _prim is not SinePrimitive sp || _vm is null || oldValue == newValue) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<SineAxis>(_vm.EditableSymbol, "Axis", oldValue, newValue,
            v => sp.Axis = v));
    }

    // ── ExponentialTaper fields ───────────────────────────────────────────────

    [ObservableProperty] private double _fieldW1;
    [ObservableProperty] private double _fieldW2;
    [ObservableProperty] private double _fieldExpL;
    [ObservableProperty] private int    _fieldNumPts;

    partial void OnFieldW1Changed(double oldValue, double newValue)
    {
        if (_isRefreshing || _prim is not ExponentialTaperPrimitive et || _vm is null) return;
        double clamped = Math.Max(newValue, 1.0);
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<double>(_vm.EditableSymbol, "W1", oldValue, clamped, v => et.W1 = v));
    }
    partial void OnFieldW2Changed(double oldValue, double newValue)
    {
        if (_isRefreshing || _prim is not ExponentialTaperPrimitive et || _vm is null) return;
        double clamped = Math.Max(newValue, 1.0);
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<double>(_vm.EditableSymbol, "W2", oldValue, clamped, v => et.W2 = v));
    }
    partial void OnFieldExpLChanged(double oldValue, double newValue)
    {
        if (_isRefreshing || _prim is not ExponentialTaperPrimitive et || _vm is null) return;
        double clamped = Math.Max(newValue, 1.0);
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<double>(_vm.EditableSymbol, "L", oldValue, clamped, v => et.L = v));
    }
    partial void OnFieldNumPtsChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _prim is not ExponentialTaperPrimitive et || _vm is null || oldValue == newValue) return;
        int clamped = Math.Max(newValue, 2);
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<int>(_vm.EditableSymbol, "NumPts", oldValue, clamped, v => et.NumPts = v));
    }

    // ── Filled checkbox (Rect, RoundedRect, Circle, Ellipse, Polygon) ─────────

    [ObservableProperty] private bool _fieldFilled;

    partial void OnFieldFilledChanged(bool oldValue, bool newValue)
    {
        if (_isRefreshing || _prim is null || _vm is null || oldValue == newValue) return;
        Action<bool>? apply = _prim switch {
            RectPrimitive             p => v => p.Filled = v,
            RoundedRectPrimitive      p => v => p.Filled = v,
            CirclePrimitive           p => v => p.Filled = v,
            EllipsePrimitive          p => v => p.Filled = v,
            PolygonPrimitive          p => v => p.Filled = v,
            ExponentialTaperPrimitive p => v => p.Filled = v,
            _ => null,
        };
        if (apply is not null)
            _vm.Execute(new SetSymbolPrimitiveFieldCommand<bool>(_vm.EditableSymbol, "Filled", oldValue, newValue, apply));
    }

    // ── Bitmap: Opacity ───────────────────────────────────────────────────────

    [ObservableProperty] private double _fieldBitmapOpacity;

    partial void OnFieldBitmapOpacityChanged(double oldValue, double newValue) => ApplyDouble("Opacity", oldValue, newValue,
        _prim is BitmapPrimitive p ? v => p.Opacity = v : null);

    // ── Stroke tier (all stroked primitives) ──────────────────────────────────

    [ObservableProperty] private SymbolStrokeTier _strokeTier;

    partial void OnStrokeTierChanged(SymbolStrokeTier oldValue, SymbolStrokeTier newValue)
    {
        if (_isRefreshing || _prim is null || _vm is null || oldValue == newValue) return;
        Action<SymbolStrokeTier>? apply = _prim switch {
            LinePrimitive             p => v => p.StrokeTier = v,
            PolylinePrimitive         p => v => p.StrokeTier = v,
            RectPrimitive             p => v => p.StrokeTier = v,
            RoundedRectPrimitive      p => v => p.StrokeTier = v,
            CirclePrimitive           p => v => p.StrokeTier = v,
            EllipsePrimitive          p => v => p.StrokeTier = v,
            ArcPrimitive              p => v => p.StrokeTier = v,
            PolygonPrimitive          p => v => p.StrokeTier = v,
            QuadCurvePrimitive        p => v => p.StrokeTier = v,
            CubicCurvePrimitive       p => v => p.StrokeTier = v,
            SinePrimitive             p => v => p.StrokeTier = v,
            ExponentialTaperPrimitive p => v => p.StrokeTier = v,
            _ => null,
        };
        if (apply is not null)
            _vm.Execute(new SetSymbolPrimitiveFieldCommand<SymbolStrokeTier>(_vm.EditableSymbol, "Stroke", oldValue, newValue, apply));
    }

    // ── QuadCurve points ──────────────────────────────────────────────────────

    [ObservableProperty] private double _fieldP0X;
    [ObservableProperty] private double _fieldP0Y;
    [ObservableProperty] private double _fieldCtrlX;
    [ObservableProperty] private double _fieldCtrlY;
    [ObservableProperty] private double _fieldP2X;
    [ObservableProperty] private double _fieldP2Y;

    partial void OnFieldP0XChanged(double oldValue, double newValue) => ApplyDouble("P0X", oldValue, newValue,
        _prim switch {
            QuadCurvePrimitive  p => v => p.P0X = v,
            CubicCurvePrimitive p => v => p.P0X = v,
            _ => null,
        });
    partial void OnFieldP0YChanged(double oldValue, double newValue) => ApplyDouble("P0Y", oldValue, newValue,
        _prim switch {
            QuadCurvePrimitive  p => v => p.P0Y = v,
            CubicCurvePrimitive p => v => p.P0Y = v,
            _ => null,
        });
    partial void OnFieldCtrlXChanged(double oldValue, double newValue) => ApplyDouble("CtrlX", oldValue, newValue,
        _prim is QuadCurvePrimitive p ? v => p.CtrlX = v : null);
    partial void OnFieldCtrlYChanged(double oldValue, double newValue) => ApplyDouble("CtrlY", oldValue, newValue,
        _prim is QuadCurvePrimitive p ? v => p.CtrlY = v : null);
    partial void OnFieldP2XChanged(double oldValue, double newValue) => ApplyDouble("P2X", oldValue, newValue,
        _prim is QuadCurvePrimitive p ? v => p.P2X = v : null);
    partial void OnFieldP2YChanged(double oldValue, double newValue) => ApplyDouble("P2Y", oldValue, newValue,
        _prim is QuadCurvePrimitive p ? v => p.P2Y = v : null);

    // ── CubicCurve extra points ───────────────────────────────────────────────

    [ObservableProperty] private double _fieldC1X;
    [ObservableProperty] private double _fieldC1Y;
    [ObservableProperty] private double _fieldC2X;
    [ObservableProperty] private double _fieldC2Y;
    [ObservableProperty] private double _fieldP3X;
    [ObservableProperty] private double _fieldP3Y;

    partial void OnFieldC1XChanged(double oldValue, double newValue) => ApplyDouble("C1X", oldValue, newValue,
        _prim is CubicCurvePrimitive p ? v => p.C1X = v : null);
    partial void OnFieldC1YChanged(double oldValue, double newValue) => ApplyDouble("C1Y", oldValue, newValue,
        _prim is CubicCurvePrimitive p ? v => p.C1Y = v : null);
    partial void OnFieldC2XChanged(double oldValue, double newValue) => ApplyDouble("C2X", oldValue, newValue,
        _prim is CubicCurvePrimitive p ? v => p.C2X = v : null);
    partial void OnFieldC2YChanged(double oldValue, double newValue) => ApplyDouble("C2Y", oldValue, newValue,
        _prim is CubicCurvePrimitive p ? v => p.C2Y = v : null);
    partial void OnFieldP3XChanged(double oldValue, double newValue) => ApplyDouble("P3X", oldValue, newValue,
        _prim is CubicCurvePrimitive p ? v => p.P3X = v : null);
    partial void OnFieldP3YChanged(double oldValue, double newValue) => ApplyDouble("P3Y", oldValue, newValue,
        _prim is CubicCurvePrimitive p ? v => p.P3Y = v : null);

    // ── Text fields ───────────────────────────────────────────────────────────

    [ObservableProperty] private bool            _isTextPrimitive;
    [ObservableProperty] private string          _textContent  = "";
    [ObservableProperty] private double          _textAnchorX;
    [ObservableProperty] private double          _textAnchorY;
    [ObservableProperty] private double          _textFontSize;
    [ObservableProperty] private SymbolFontStyle _textFontStyle;
    [ObservableProperty] private SymbolTextAlign _textAlign;

    partial void OnTextContentChanged(string? oldValue, string newValue)
    {
        if (!_isRefreshing && _prim is TextPrimitive t && _vm is not null)
            _vm.Execute(new SetTextPrimitiveCommand(_vm.EditableSymbol, t, newValue, t.FontSize, t.FontStyle));
    }
    partial void OnTextFontSizeChanged(double oldValue, double newValue)
    {
        if (!_isRefreshing && _prim is TextPrimitive t && _vm is not null && newValue > 0)
            _vm.Execute(new SetTextPrimitiveCommand(_vm.EditableSymbol, t, t.Content, newValue, t.FontStyle));
    }
    partial void OnTextFontStyleChanged(SymbolFontStyle oldValue, SymbolFontStyle newValue)
    {
        if (!_isRefreshing && _prim is TextPrimitive t && _vm is not null && oldValue != newValue)
            _vm.Execute(new SetTextPrimitiveCommand(_vm.EditableSymbol, t, t.Content, t.FontSize, newValue));
    }
    partial void OnTextAnchorXChanged(double oldValue, double newValue) => ApplyDouble("AnchorX", oldValue, newValue,
        _prim is TextPrimitive p ? v => p.AnchorX = v : null);
    partial void OnTextAnchorYChanged(double oldValue, double newValue) => ApplyDouble("AnchorY", oldValue, newValue,
        _prim is TextPrimitive p ? v => p.AnchorY = v : null);
    partial void OnTextAlignChanged(SymbolTextAlign oldValue, SymbolTextAlign newValue)
    {
        if (_isRefreshing || _prim is not TextPrimitive tp || _vm is null || oldValue == newValue) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<SymbolTextAlign>(_vm.EditableSymbol, "Align", oldValue, newValue, v => tp.Align = v));
    }

    // ── Pin view (Layer 4) ────────────────────────────────────────────────────

    private const double PinGrid = 100.0; // must match SymbolEditorViewModel.PinGrid

    [ObservableProperty] private bool   _isPinSelected;
    [ObservableProperty] private double _pinX;
    [ObservableProperty] private double _pinY;
    [ObservableProperty] private int    _pinPortIndex;

    partial void OnPinXChanged(double oldValue, double newValue)
    {
        if (_isRefreshing || _vm is null) return;
        int pi = _vm.Overlay.SelectedPinIndex;
        if (pi < 0 || pi >= _vm.EditableSymbol.Pins.Count) return;
        var pin     = _vm.EditableSymbol.Pins[pi];
        double snap = Math.Round(newValue / PinGrid) * PinGrid;
        if (Math.Abs(snap - pin.LocalX) < 0.001) return;
        _vm.Execute(new MoveSymbolPinCommand(_vm.EditableSymbol, pin, snap, pin.LocalY));
    }

    partial void OnPinYChanged(double oldValue, double newValue)
    {
        if (_isRefreshing || _vm is null) return;
        int pi = _vm.Overlay.SelectedPinIndex;
        if (pi < 0 || pi >= _vm.EditableSymbol.Pins.Count) return;
        var pin     = _vm.EditableSymbol.Pins[pi];
        double snap = Math.Round(newValue / PinGrid) * PinGrid;
        if (Math.Abs(snap - pin.LocalY) < 0.001) return;
        _vm.Execute(new MoveSymbolPinCommand(_vm.EditableSymbol, pin, pin.LocalX, snap));
    }

    partial void OnPinPortIndexChanged(int oldValue, int newValue)
    {
        if (_isRefreshing || _vm is null) return;
        int pi = _vm.Overlay.SelectedPinIndex;
        if (pi < 0 || pi >= _vm.EditableSymbol.Pins.Count) return;
        var pin = _vm.EditableSymbol.Pins[pi];
        int zeroBasedIndex = newValue - 1;
        if (zeroBasedIndex < 0) return;
        if (zeroBasedIndex == pin.PortIndex) return;
        _vm.Execute(new RemapSymbolPinCommand(_vm.EditableSymbol, pin, zeroBasedIndex));
    }

    // ── Polyline coord list (Layer 3) ─────────────────────────────────────────

    [ObservableProperty] private bool _showPolylineCoords;
    public ObservableCollection<PolylineCoordRowViewModel> PolylineCoords { get; } = [];

    // ── Context binding ───────────────────────────────────────────────────────

    public void SetContext(SymbolEditorViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshFromVm();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SymbolEditorViewModel.Overlay)
                           or nameof(SymbolEditorViewModel.RenderSymbol))
            RefreshFromVm();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private void RefreshFromVm()
    {
        if (_vm is null) { SetEmpty("No active symbol editor."); return; }

        var overlay  = _vm.Overlay;
        var selected = overlay.SelectedIndices;

        if (overlay.SelectedPinIndex >= 0)
        {
            int pi = overlay.SelectedPinIndex;
            if (pi < _vm.EditableSymbol.Pins.Count)
            {
                var (pdx, pdy) = overlay.PinLiveDragOffset;
                SetPinView(_vm.EditableSymbol.Pins[pi], pdx, pdy);
                return;
            }
        }

        if (selected.Count != 1) { SetEmpty("Select a single primitive to inspect."); return; }

        int idx = selected.First();
        if (idx < 0 || idx >= _vm.EditableSymbol.Primitives.Count)
        { SetEmpty("Select a single primitive to inspect."); return; }

        // Always track the original for the switching check (prevents focus loss during live ops).
        var original = _vm.EditableSymbol.Primitives[idx];
        bool switching = original != _prim || _primIdx != idx;
        _prim    = original;
        _primIdx = idx;

        // Determine which primitive to read values from:
        // - resize in progress → use the live-scaled clone from the VM
        // - drag in progress   → translate a clone by the live delta
        // - otherwise          → use the original directly
        SymbolPrimitive readFrom;
        if (_vm.ResizeLivePrimitive is { } resizePrev)
        {
            readFrom = resizePrev;
        }
        else
        {
            var (dx, dy) = overlay.LiveDragOffset;
            if (dx != 0 || dy != 0)
            {
                var translated = SymbolGeometry.Clone(original);
                SymbolGeometry.TranslateBy(translated, dx, dy);
                readFrom = translated;
            }
            else
            {
                readFrom = original;
            }
        }

        SetPrimView(readFrom, switching);
    }

    private void SetEmpty(string msg)
    {
        _prim = null; _primIdx = -1;
        _isRefreshing = true;
        IsEmptyState  = true;
        EmptyMessage  = msg;
        _isRefreshing = false;
    }

    private int _lastPinIndex = -1;

    private void SetPinView(SymbolPin pin, double offsetX = 0, double offsetY = 0)
    {
        int pi = _vm!.Overlay.SelectedPinIndex;
        bool switching = !IsPinSelected || pi != _lastPinIndex;
        _lastPinIndex = pi;

        _isRefreshing = true;
        IsEmptyState  = false;
        TypeName      = "Pin";
        if (switching) HideAllGroups();
        IsPinSelected = true;
        PinX          = pin.LocalX + offsetX;
        PinY          = pin.LocalY + offsetY;
        PinPortIndex  = pin.PortIndex + 1;
        PolylineCoords.Clear();
        _isRefreshing = false;
    }

    // switching=true on first selection of this primitive, false on subsequent refreshes of the
    // same instance (e.g. after a field edit). On same-instance refreshes we skip HideAllGroups
    // so the visibility booleans never toggle — prevents Avalonia from destroying and recreating
    // the focused NumericUpDown, which would drop keyboard focus mid-edit.
    private void SetPrimView(SymbolPrimitive prim, bool switching = true)
    {
        _isRefreshing = true;
        IsEmptyState  = false;
        IsPinSelected = false;

        TypeName = prim switch
        {
            LinePrimitive        => "Line",
            PolylinePrimitive    => "Polyline",
            RectPrimitive        => "Rectangle",
            RoundedRectPrimitive => "Rounded Rect",
            CirclePrimitive      => "Circle",
            EllipsePrimitive     => "Ellipse",
            ArcPrimitive         => "Arc",
            PolygonPrimitive     => "Polygon",
            QuadCurvePrimitive   => "Quad Bézier",
            CubicCurvePrimitive  => "Cubic Bézier",
            SinePrimitive             => "Sine Wave",
            ExponentialTaperPrimitive => "Exp. Taper",
            TextPrimitive             => "Text",
            BitmapPrimitive      => "Bitmap",
            _                    => prim.GetType().Name,
        };

        if (switching) HideAllGroups();

        switch (prim)
        {
            case LinePrimitive l:
                ShowLineCoords = true;
                FieldX1 = l.X1; FieldY1 = l.Y1; FieldX2 = l.X2; FieldY2 = l.Y2;
                ShowStrokeTier = true; StrokeTier = l.StrokeTier;
                break;

            case PolylinePrimitive pl:
                ShowStrokeTier = true; StrokeTier = pl.StrokeTier;
                ShowPolylineCoords = true;
                RefreshPolylineCoords(pl.Points, switching);
                break;

            case RectPrimitive r:
                ShowCxCy = true; FieldCx = r.Cx; FieldCy = r.Cy;
                ShowWH = true; FieldW = r.W; FieldH = r.H;
                ShowFilled = true; FieldFilled = r.Filled;
                ShowStrokeTier = true; StrokeTier = r.StrokeTier;
                break;

            case RoundedRectPrimitive rr:
                ShowCxCy = true; FieldCx = rr.Cx; FieldCy = rr.Cy;
                ShowWH = true; FieldW = rr.W; FieldH = rr.H;
                ShowCornerRadius = true; FieldCornerRadius = rr.Radius;
                ShowFilled = true; FieldFilled = rr.Filled;
                ShowStrokeTier = true; StrokeTier = rr.StrokeTier;
                break;

            case CirclePrimitive c:
                ShowCxCy = true; FieldCx = c.Cx; FieldCy = c.Cy;
                ShowRadius = true; FieldRadius = c.R;
                ShowFilled = true; FieldFilled = c.Filled;
                ShowStrokeTier = true; StrokeTier = c.StrokeTier;
                break;

            case EllipsePrimitive e:
                ShowCxCy = true; FieldCx = e.Cx; FieldCy = e.Cy;
                ShowRxRy = true; FieldRx = e.Rx; FieldRy = e.Ry;
                ShowFilled = true; FieldFilled = e.Filled;
                ShowStrokeTier = true; StrokeTier = e.StrokeTier;
                break;

            case ArcPrimitive a:
                ShowCxCy = true; FieldCx = a.Cx; FieldCy = a.Cy;
                ShowRadius = true; FieldRadius = a.R;
                ShowArcAngles = true; FieldStartDeg = a.StartDeg; FieldSweepDeg = a.SweepDeg;
                ShowStrokeTier = true; StrokeTier = a.StrokeTier;
                break;

            case PolygonPrimitive pg:
                ShowFilled = true; FieldFilled = pg.Filled;
                ShowStrokeTier = true; StrokeTier = pg.StrokeTier;
                ShowPolylineCoords = true;
                RefreshPolylineCoords(pg.Points, switching);
                break;

            case QuadCurvePrimitive qc:
                ShowQuadCurve = true;
                FieldP0X = qc.P0X; FieldP0Y = qc.P0Y;
                FieldCtrlX = qc.CtrlX; FieldCtrlY = qc.CtrlY;
                FieldP2X = qc.P2X; FieldP2Y = qc.P2Y;
                ShowStrokeTier = true; StrokeTier = qc.StrokeTier;
                break;

            case CubicCurvePrimitive cc:
                ShowCubicCurve = true;
                FieldP0X = cc.P0X; FieldP0Y = cc.P0Y;
                FieldC1X = cc.C1X; FieldC1Y = cc.C1Y;
                FieldC2X = cc.C2X; FieldC2Y = cc.C2Y;
                FieldP3X = cc.P3X; FieldP3Y = cc.P3Y;
                ShowStrokeTier = true; StrokeTier = cc.StrokeTier;
                break;

            case SinePrimitive s:
                ShowCxCy = true; FieldCx = s.Cx; FieldCy = s.Cy;
                ShowSineFields = true;
                FieldAmp = s.Amp; FieldCycles = s.Cycles;
                FieldLength = s.Length; FieldPtsPerCycle = s.PtsPerCycle; FieldAxis = s.Axis;
                ShowStrokeTier = true; StrokeTier = s.StrokeTier;
                break;

            case ExponentialTaperPrimitive et:
                ShowCxCy = true; FieldCx = et.Cx; FieldCy = et.Cy;
                ShowExpTaperFields = true;
                FieldW1 = et.W1; FieldW2 = et.W2; FieldExpL = et.L; FieldNumPts = et.NumPts;
                FieldAxis = et.Axis;
                ShowFilled = true; FieldFilled = et.Filled;
                ShowStrokeTier = true; StrokeTier = et.StrokeTier;
                break;

            case TextPrimitive t:
                IsTextPrimitive = true;
                TextAnchorX   = t.AnchorX; TextAnchorY = t.AnchorY;
                TextContent   = t.Content;
                TextFontSize  = t.FontSize;
                TextFontStyle = t.FontStyle;
                TextAlign     = t.Align;
                break;

            case BitmapPrimitive bmp:
                ShowBitmapFields   = true;
                FieldBitmapOpacity = bmp.Opacity;
                break;
        }

        _isRefreshing = false;
    }

    private void HideAllGroups()
    {
        ShowLineCoords     = false;
        ShowCxCy           = false;
        ShowWH             = false;
        ShowRadius         = false;
        ShowRxRy           = false;
        ShowCornerRadius   = false;
        ShowArcAngles      = false;
        ShowSineFields     = false;
        ShowExpTaperFields = false;
        ShowQuadCurve      = false;
        ShowCubicCurve     = false;
        ShowFilled         = false;
        ShowStrokeTier     = false;
        IsTextPrimitive    = false;
        ShowPolylineCoords = false;
        ShowBitmapFields   = false;
        IsPinSelected      = false;
        PolylineCoords.Clear();
    }

    // rebuild=true on new selection (clears + recreates rows); false on same-prim refresh
    // (updates X/Y in-place so the focused row keeps keyboard focus).
    private void RefreshPolylineCoords(List<double[]> pts, bool rebuild)
    {
        if (rebuild || PolylineCoords.Count != pts.Count)
        {
            PolylineCoords.Clear();
            for (int i = 0; i < pts.Count; i++)
                PolylineCoords.Add(new PolylineCoordRowViewModel(i, pts[i][0], pts[i][1], _vm!, _prim!));
        }
        else
        {
            for (int i = 0; i < pts.Count; i++)
                PolylineCoords[i].Refresh(pts[i][0], pts[i][1]);
        }
    }

    // ── Command dispatch helper ────────────────────────────────────────────────

    private void ApplyDouble(string description, double oldVal, double newVal, Action<double>? apply)
    {
        if (_isRefreshing || apply is null || _prim is null || _vm is null) return;
        if (Math.Abs(newVal - oldVal) < 0.001) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<double>(_vm.EditableSymbol, description, oldVal, newVal, apply));
    }
}

/// <summary>Editable row in the polyline/polygon coordinate list.</summary>
public sealed partial class PolylineCoordRowViewModel : ObservableObject
{
    private readonly SymbolEditorViewModel _vm;
    private readonly SymbolPrimitive       _prim;
    private bool                           _refreshing;

    public int    Index { get; }
    public string Label => $"[{Index}]";

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    public PolylineCoordRowViewModel(int index, double x, double y,
                                     SymbolEditorViewModel vm, SymbolPrimitive prim)
    {
        Index = index; _x = x; _y = y; _vm = vm; _prim = prim;
    }

    partial void OnXChanged(double oldValue, double newValue)
    {
        if (_refreshing || Math.Abs(newValue - oldValue) < 0.001) return;
        var pts = Points();
        if (pts is null || Index >= pts.Count) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<double>(
            _vm.EditableSymbol, "X", oldValue, newValue, v => pts[Index][0] = v));
    }

    partial void OnYChanged(double oldValue, double newValue)
    {
        if (_refreshing || Math.Abs(newValue - oldValue) < 0.001) return;
        var pts = Points();
        if (pts is null || Index >= pts.Count) return;
        _vm.Execute(new SetSymbolPrimitiveFieldCommand<double>(
            _vm.EditableSymbol, "Y", oldValue, newValue, v => pts[Index][1] = v));
    }

    // Called by RefreshPolylineCoords on same-instance refreshes (e.g. after undo).
    public void Refresh(double x, double y)
    {
        _refreshing = true;
        X = x; Y = y;
        _refreshing = false;
    }

    private List<double[]>? Points() => _prim switch {
        PolylinePrimitive pl => pl.Points,
        PolygonPrimitive  pg => pg.Points,
        _ => null,
    };
}
