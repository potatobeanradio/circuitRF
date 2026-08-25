using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

// ── Event arg types ────────────────────────────────────────────────────────────

public sealed class TextLabelHitArgs : EventArgs
{
    public SchematicHitTest.HitResult HitResult { get; }
    public double ScreenX { get; }
    public double ScreenY { get; }
    public TextLabelHitArgs(SchematicHitTest.HitResult hit, double sx, double sy)
        => (HitResult, ScreenX, ScreenY) = (hit, sx, sy);
}

public sealed class WireHitArgs : EventArgs
{
    public string WireId  { get; }
    public double WorldX  { get; }
    public double WorldY  { get; }
    public double ScreenX { get; }
    public double ScreenY { get; }
    public WireHitArgs(string id, double wx, double wy, double sx, double sy)
        => (WireId, WorldX, WorldY, ScreenX, ScreenY) = (id, wx, wy, sx, sy);
}

/// <summary>
/// Custom Avalonia control that renders a SchematicModel via SkiaSharp.
/// Middle mouse button always pans.
/// Left button delegates to the active tool in EditContext (select / wire / place).
/// Right click triggers the ContextMenu (hit-test result stored in ContextMenuTargetId).
/// </summary>
public sealed class SchematicCanvas : Control
{
    // ── DirectProperty: Model ────────────────────────────────────────────────

    public static readonly DirectProperty<SchematicCanvas, SchematicModel?> ModelProperty =
        AvaloniaProperty.RegisterDirect<SchematicCanvas, SchematicModel?>(
            nameof(Model), o => o.Model, (o, v) => o.Model = v);

    private SchematicModel? _model;
    public SchematicModel? Model
    {
        get => _model;
        set
        {
            SetAndRaise(ModelProperty, ref _model, value);
            if (_editContext is null)
                _index = value is not null ? new SchematicSpatialIndex(value) : null;
            _needsInitialFit = value is not null;
            InvalidateVisual();
        }
    }

    // ── DirectProperty: EditContext ──────────────────────────────────────────

    public static readonly DirectProperty<SchematicCanvas, SchematicViewModel?> EditContextProperty =
        AvaloniaProperty.RegisterDirect<SchematicCanvas, SchematicViewModel?>(
            nameof(EditContext), o => o.EditContext, (o, v) => o.EditContext = v);

    private SchematicViewModel? _editContext;
    public SchematicViewModel? EditContext
    {
        get => _editContext;
        set
        {
            if (_editContext is not null)
            {
                _editContext.PropertyChanged -= OnVmPropertyChanged;
                _editContext.ZoomToRectCallback = null;
                _editContext.ViewportProvider   = null;
            }

            SetAndRaise(EditContextProperty, ref _editContext, value);

            if (_editContext is not null)
            {
                _editContext.PropertyChanged += OnVmPropertyChanged;
                _editContext.ZoomToRectCallback = ZoomToRect;
                _editContext.ViewportProvider   = () => WorldViewport;
                _editContext.CanvasZoom = _zoom;
                SyncFromVm();
                UpdateCursor();
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SchematicViewModel.RenderModel)
                           or nameof(SchematicViewModel.SpatialIndex)
                           or nameof(SchematicViewModel.Overlay))
            SyncFromVm();
        else if (e.PropertyName == nameof(SchematicViewModel.ActiveTool))
            UpdateCursor();
    }

    private void UpdateCursor()
    {
        if (_isLeftPanning || _isPanning)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }
        Cursor = _editContext?.ActiveTool switch
        {
            SchematicViewModel.Tool.Pan        => new Cursor(StandardCursorType.Hand),
            SchematicViewModel.Tool.Wire       => new Cursor(StandardCursorType.Cross),
            SchematicViewModel.Tool.Place      => new Cursor(StandardCursorType.Cross),
            SchematicViewModel.Tool.ZoomBox    => new Cursor(StandardCursorType.Cross),
            SchematicViewModel.Tool.MoveLabels => new Cursor(StandardCursorType.SizeAll),
            _                                  => Cursor.Default,
        };
    }

    private void SyncFromVm()
    {
        if (_editContext is null) return;
        _model   = _editContext.RenderModel;
        _index   = _editContext.SpatialIndex;
        _overlay = _editContext.Overlay;
        _needsInitialFit = _model is not null && _needsInitialFit;
        InvalidateVisual();
    }

    // ── Viewport state ───────────────────────────────────────────────────────

    private double _panX;
    private double _panY;
    private double _zoom = 1.0;

    /// <summary>Current zoom level (world units per logical pixel). Read by code-behind to size the inline edit box.</summary>
    public double CurrentZoom => _zoom;

    private const double ZoomFactor = 1.15;
    private const double MinZoom    = 0.0005;
    private const double MaxZoom    = 50.0;

    // ── Pan state — middle mouse only ─────────────────────────────────────────

    private bool   _isPanning;
    private Point  _panDragStartScreen;
    private double _panDragStartPanX;
    private double _panDragStartPanY;

    // Left-button Pan tool
    private bool   _isLeftPanning;

    // ── Net-label placement gaps ──────────────────────────────────────────────
    // Offset from the wire's exact coordinate to the stored net-label anchor.
    // Tune these two constants to adjust the visual gap between wire and label.
    // Horizontal wire → label baseline is this many world units ABOVE the wire.
    // Vertical wire   → label left edge is this many world units to the RIGHT of the wire.
    private const float NetLabelGapAboveHorizontal = 20f;
    private const float NetLabelGapBesideVertical  = 20f;

    // ── Context menu tracking ─────────────────────────────────────────────────

    /// <summary>ID of the component (or wire) that was right-clicked. Null if background.</summary>
    public string? ContextMenuTargetId { get; private set; }

    // ── Internal state ────────────────────────────────────────────────────────

    private SchematicSpatialIndex? _index;
    private SchematicOverlay?      _overlay;
    private bool _needsInitialFit;

    // ── Events (raised to code-behind) ────────────────────────────────────────

    public event EventHandler<EditableComponent>?  ComponentDoubleTapped;
    public event EventHandler<TextLabelHitArgs>?   TextLabelDoubleTapped;
    public event EventHandler<WireHitArgs>?        WireDoubleTapped;

    // Clipboard shortcuts — raised so code-behind can do the async clipboard work.
    public event EventHandler? ClipboardCopyRequested;
    public event EventHandler? ClipboardCutRequested;
    public event EventHandler? ClipboardPasteRequested;

    // Fired after any pan or zoom change so overlays (e.g. inline edit box) can reposition.
    public event EventHandler? ViewportChanged;

    // ── Constructor ──────────────────────────────────────────────────────────

    public SchematicCanvas()
    {
        Focusable = true;

        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerWheelChanged += OnPointerWheel;
        KeyDown             += OnKeyDown;
        DoubleTapped        += OnDoubleTapped;

        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
        LayoutUpdated += OnLayoutUpdated;

        // DnD drop targets registered in priority order:
        //   1. Palette (circuitrf-palette: prefix)
        //   2. Cell (circuitrf-cell: prefix)  ← new
        //   3. Image files (foreign file drops)
        // Each handler marks e.Handled=true on accept, stopping lower-priority handlers.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent,  OnPaletteDragOver);
        AddHandler(DragDrop.DropEvent,      OnPaletteDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnPaletteDragLeave);
        AddHandler(DragDrop.DragOverEvent,  OnCellDragOver);
        AddHandler(DragDrop.DropEvent,      OnCellDrop);
        AddHandler(DragDrop.DragOverEvent,  OnImageFileDragOver);
        AddHandler(DragDrop.DropEvent,      OnImageFileDrop);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _model is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _needsInitialFit = false;
            LayoutUpdated -= OnLayoutUpdated;
            ZoomToFitInternal(Bounds.Width, Bounds.Height);
            InvalidateVisual();
        }
    }

    // ── Visual tree ──────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeServiceChanged;
        _activeTheme = ThemeService.Active;
        if (TopLevel.GetTopLevel(this) is TopLevel tl)
            tl.SizeChanged += OnTopLevelSizeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeServiceChanged;
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is TopLevel tl)
            tl.SizeChanged -= OnTopLevelSizeChanged;
    }

    private void OnTopLevelSizeChanged(object? s, SizeChangedEventArgs e) => InvalidateVisual();

    private void OnThemeServiceChanged(object? sender, EventArgs e)
    {
        _activeTheme = ThemeService.Active;
        InvalidateVisual();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    private ColorTheme _activeTheme = ColorTheme.BuiltIn;

    /// <summary>
    /// Sets the active theme globally (via ThemeService) and redraws this canvas.
    /// Prefer setting ThemeService.Active directly; this method is a convenience shim.
    /// </summary>
    public void ApplyTheme(ColorTheme theme) => ThemeService.Active = theme;

    public override void Render(DrawingContext context)
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme   = SchematicRenderTheme.FromTheme(_activeTheme, variant);

        // Apply system accent color to rubber-band
        if (Application.Current?.TryGetResource("SystemAccentColor", ActualThemeVariant, out var res) == true
            && res is Avalonia.Media.Color avColor)
        {
            var accent = new SKColor(avColor.R, avColor.G, avColor.B);
            theme = theme.WithAccent(accent);
        }

        context.Custom(new SchematicDrawOperation(
            new Rect(Bounds.Size), _model, _index,
            _panX, _panY, _zoom, theme, _overlay));
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void ZoomToFit()
    {
        ZoomToFitInternal(Bounds.Width, Bounds.Height);
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ZoomToFitInternal(double canvasW, double canvasH)
    {
        if (_model is null || canvasW < 1 || canvasH < 1) return;
        var (minX, minY, maxX, maxY) = DrawnExtent(_model);
        double worldW = Math.Max(maxX - minX, 1);
        double worldH = Math.Max(maxY - minY, 1);
        const double pad = 0.05;
        _zoom = Math.Clamp(Math.Min(canvasW / worldW, canvasH / worldH) * (1.0 - 2 * pad), MinZoom, MaxZoom);
        double scaledW = worldW * _zoom;
        double scaledH = worldH * _zoom;
        _panX = minX - (canvasW  - scaledW) / (2 * _zoom);
        _panY = minY - (canvasH - scaledH) / (2 * _zoom);
        if (_editContext is not null) _editContext.CanvasZoom = _zoom;
    }

    /// <summary>
    /// What the schematic actually DRAWS — the union of every component's full bounding box (glyph
    /// plus labels), every wire and every bitmap.
    ///
    /// <para><b>Not <see cref="SchematicModel.BbMinX"/> and friends.</b> A component's <c>Bb</c> is a
    /// FIXED square around its origin (<c>EditableComponent.GetBoundingBox</c> — <c>X ± HalfBound</c>,
    /// the same size for a resistor and for a twelve-port SnP), so the model box it aggregates is a
    /// hit-test envelope, not an extent. Fitting to it over-zooms any symbol bigger than that square:
    /// measured on a four-array wBond, whose body ran a fifth of its own height off the top and
    /// bottom of the view no matter how the window was sized (2026-08-20, from the user-doc figure —
    /// but View ▸ Zoom to Fit did the same thing in the application). <c>FullBb</c> is the value the
    /// renderer and the spatial index already cull against, so this is the extent that is on screen.</para>
    /// </summary>
    private static (double MinX, double MinY, double MaxX, double MaxY) DrawnExtent(SchematicModel model)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        void Union(double x0, double y0, double x1, double y1)
        {
            if (x0 < minX) minX = x0; if (y0 < minY) minY = y0;
            if (x1 > maxX) maxX = x1; if (y1 > maxY) maxY = y1;
        }

        foreach (var c in model.Components) Union(c.FullBbMinX, c.FullBbMinY, c.FullBbMaxX, c.FullBbMaxY);
        foreach (var w in model.Wires)      Union(w.BbMinX, w.BbMinY, w.BbMaxX, w.BbMaxY);
        foreach (var b in model.Bitmaps)    Union(b.X, b.Y, b.X + b.Width, b.Y + b.Height);

        // Nothing on the sheet: keep the model's own empty-schematic box rather than inventing one.
        if (minX == double.MaxValue)
            return (model.BbMinX, model.BbMinY, model.BbMaxX, model.BbMaxY);

        const double margin = 200;   // the same breathing room the model box adds
        return (minX - margin, minY - margin, maxX + margin, maxY + margin);
    }

    public void ZoomToPage()
    {
        _panX = 0; _panY = 0; _zoom = 1.0;
        if (_editContext is not null) _editContext.CanvasZoom = _zoom;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ZoomToRect(double x0, double y0, double x1, double y1)
    {
        double worldW = Math.Abs(x1 - x0);
        double worldH = Math.Abs(y1 - y0);
        if (worldW < 1 || worldH < 1) return;
        const double pad = 0.05;
        _zoom = Math.Clamp(
            Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2.0 * pad),
            MinZoom, MaxZoom);
        double cx = (x0 + x1) / 2.0;
        double cy = (y0 + y1) / 2.0;
        _panX = cx - Bounds.Width  / (2.0 * _zoom);
        _panY = cy - Bounds.Height / (2.0 * _zoom);
        if (_editContext is not null) _editContext.CanvasZoom = _zoom;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    // ── World ↔ screen ────────────────────────────────────────────────────────

    /// <summary>
    /// The world rectangle currently on screen, or null before the control has been laid out.
    /// Read by Paste (via SchematicViewModel.ViewportProvider) to land a pasted fragment in view.
    /// </summary>
    public SchematicPasteGeometry.ViewRect? WorldViewport =>
        Bounds.Width > 1 && Bounds.Height > 1 && _zoom > 0
            ? new SchematicPasteGeometry.ViewRect(
                _panX, _panY, _panX + Bounds.Width / _zoom, _panY + Bounds.Height / _zoom)
            : null;

    private double ScreenToWorldX(double sx) => sx / _zoom + _panX;
    private double ScreenToWorldY(double sy) => sy / _zoom + _panY;

    /// <summary>Converts a world position to canvas screen coordinates.</summary>
    public (double X, double Y) WorldToScreen(double wx, double wy)
        => ((wx - _panX) * _zoom, (wy - _panY) * _zoom);

    // ── Pointer — press ───────────────────────────────────────────────────────

    private void OnPointerPressed(object? _, PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var pos   = e.GetPosition(this);
        double wx = ScreenToWorldX(pos.X);
        double wy = ScreenToWorldY(pos.Y);

        // Middle mouse → pan (always, regardless of tool)
        if (props.IsMiddleButtonPressed)
        {
            _isPanning          = true;
            _panDragStartScreen = pos;
            _panDragStartPanX   = _panX;
            _panDragStartPanY   = _panY;
            e.Pointer.Capture(this);
            UpdateCursor();   // show hand cursor immediately (macOS needs explicit update after capture)
            return;
        }

        // Right mouse → hit test for context menu; Avalonia shows ContextMenu on release
        if (props.IsRightButtonPressed)
        {
            ContextMenuTargetId = null;
            if (_editContext is not null && _model is not null && _index is not null)
            {
                var hit = SchematicHitTest.Test(_editContext.EditModel, _model, _index, wx, wy, zoom: _zoom);
                if (hit.Kind is SchematicHitTest.HitKind.Component
                    or SchematicHitTest.HitKind.ComponentType
                    or SchematicHitTest.HitKind.ComponentName
                    or SchematicHitTest.HitKind.ComponentParam)
                {
                    ContextMenuTargetId = hit.Id;
                    // Also select the right-clicked component if not already selected
                    _editContext.SelectIfUnselected(hit.Id);
                }
            }
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        if (_editContext is not null)
        {
            if (_editContext.ActiveTool == SchematicViewModel.Tool.Pan)
            {
                _isLeftPanning      = true;
                _panDragStartScreen = pos;
                _panDragStartPanX   = _panX;
                _panDragStartPanY   = _panY;
                UpdateCursor();
                e.Pointer.Capture(this);
                return;
            }

            _editContext.OnPointerPressed(wx, wy, e.KeyModifiers, pos.X, pos.Y);
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else
        {
            _isLeftPanning      = true;
            _panDragStartScreen = pos;
            _panDragStartPanX   = _panX;
            _panDragStartPanY   = _panY;
            e.Pointer.Capture(this);
        }
    }

    // ── Pointer — move ────────────────────────────────────────────────────────

    private void OnPointerMoved(object? _, PointerEventArgs e)
    {
        var pos   = e.GetPosition(this);
        double wx = ScreenToWorldX(pos.X);
        double wy = ScreenToWorldY(pos.Y);

        if (_isPanning || _isLeftPanning)
        {
            _panX = _panDragStartPanX - (pos.X - _panDragStartScreen.X) / _zoom;
            _panY = _panDragStartPanY - (pos.Y - _panDragStartScreen.Y) / _zoom;
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (_editContext is not null)
        {
            _editContext.OnPointerMoved(wx, wy, leftDown, pos.X, pos.Y, e.KeyModifiers);
            InvalidateVisual();
        }
    }

    // ── Pointer — release ─────────────────────────────────────────────────────

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isPanning)     { _isPanning = false;     UpdateCursor(); e.Pointer.Capture(null); return; }
        if (_isLeftPanning) { _isLeftPanning = false; UpdateCursor(); e.Pointer.Capture(null); return; }

        if (_editContext is not null)
        {
            var pos = e.GetPosition(this);
            _editContext.OnPointerReleased(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y),
                                           e.KeyModifiers);
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
        else
        {
            e.Pointer.Capture(null);
        }
    }

    // ── Double-tap ────────────────────────────────────────────────────────────

    private void OnDoubleTapped(object? _, TappedEventArgs e)
    {
        if (_editContext is null || _model is null || _index is null) return;
        var pos   = e.GetPosition(this);
        double wx = ScreenToWorldX(pos.X);
        double wy = ScreenToWorldY(pos.Y);

        // A2: Double-click while drawing a wire finishes it and keeps the drawn segments.
        // The second PointerPressed has already added the endpoint via HandleWirePress.
        if (_editContext.ActiveTool == SchematicViewModel.Tool.Wire && _editContext.IsDrawingWire)
        {
            _editContext.FinishCurrentWire();
            _editContext.SetSelectTool();
            InvalidateVisual();
            return;
        }

        var hit = SchematicHitTest.Test(_editContext.EditModel, _model, _index, wx, wy, zoom: _zoom);

        switch (hit.Kind)
        {
            case SchematicHitTest.HitKind.ComponentType:
            case SchematicHitTest.HitKind.ComponentName:
            case SchematicHitTest.HitKind.ComponentParam:
                RaiseLabelDoubleTap(hit);
                break;
            case SchematicHitTest.HitKind.Component:
            {
                var comp = _editContext.EditModel.FindComponent(hit.Id);
                if (comp is not null)
                    ComponentDoubleTapped?.Invoke(this, comp);
                break;
            }
            case SchematicHitTest.HitKind.NetLabel:
            {
                var lbl = _editContext.EditModel.FindNetLabel(hit.Id);
                if (lbl is null) break;
                var (sx, sy) = WorldToScreen(lbl.X, lbl.Y);
                WireDoubleTapped?.Invoke(this, new WireHitArgs("", lbl.X, lbl.Y, sx, sy));
                break;
            }
            case SchematicHitTest.HitKind.Wire:
            case SchematicHitTest.HitKind.WireSegment:
            case SchematicHitTest.HitKind.WireEndpoint:
            {
                var wire = _editContext.EditModel.FindWire(hit.Id);
                double labelWx = wx, labelWy = wy;
                if (wire is { Points.Count: >= 2 })
                {
                    // Use the segment's exact coordinate (not click) as the base so the gap is
                    // always exactly NetLabelGap* world units from the wire regardless of click precision.
                    var (horizontal, baseCoord) = ClassifySegmentAt(wire, wx, wy);
                    if (horizontal)
                        labelWy = baseCoord - NetLabelGapAboveHorizontal;
                    else
                        labelWx = baseCoord + NetLabelGapBesideVertical;
                }
                // WorldX/Y = net-label world placement; ScreenX/Y = actual click position for TextBox centering
                WireDoubleTapped?.Invoke(this, new WireHitArgs(hit.Id, labelWx, labelWy, pos.X, pos.Y));
                break;
            }
        }
    }


    /// <summary>
    /// Begin an inline label edit at a WORLD point, exactly as a double-tap on that point would.
    ///
    /// <para>Extracted from <see cref="OnDoubleTapped"/> so the documentation factory can photograph
    /// the inline editor without synthesising a pointer gesture headlessly. It is the same code
    /// raising the same event to the same handler on <c>SchematicView</c>, which is the point: a
    /// figure of a re-creation of the inline editor would be a picture of something the application
    /// does not do. Returns false when nothing editable is under the point.</para>
    /// </summary>
    internal bool BeginInlineLabelEditAtWorld(double wx, double wy)
    {
        if (_editContext is null || _model is null || _index is null) return false;
        var hit = SchematicHitTest.Test(_editContext.EditModel, _model, _index, wx, wy, zoom: _zoom);
        if (hit.Kind is not (SchematicHitTest.HitKind.ComponentType
                          or SchematicHitTest.HitKind.ComponentName
                          or SchematicHitTest.HitKind.ComponentParam)) return false;
        return RaiseLabelDoubleTap(hit);
    }

    /// <summary>Position the edit box over the rendered label and raise the event the view listens to.</summary>
    private bool RaiseLabelDoubleTap(SchematicHitTest.HitResult hit)
    {
        if (_editContext is null) return false;

                // Mirror DrawLabels positioning exactly so the edit box overlays the rendered text.
                // The hit result carries the geometric hitbox centre (for click detection), which
                // differs from the actual text render position — do not use WorldToScreen(hit.LabelWorldX/Y).
                var editComp = _editContext.EditModel.FindComponent(hit.Id);
                if (editComp is null) return false;

                // SubIndex for ComponentParam is the full-list parameter index.
                // The visual row is 2 + count of shown params that precede it.
                int row;
                if (hit.Kind == SchematicHitTest.HitKind.ComponentType) row = 0;
                else if (hit.Kind == SchematicHitTest.HitKind.ComponentName) row = 1;
                else
                {
                    int dispIdx = 0;
                    for (int pi = 0; pi < editComp.Parameters.Count && pi < hit.SubIndex; pi++)
                    {
                        var pp = editComp.Parameters[pi];
                        if (pp.ShowOnSchematic && !string.IsNullOrEmpty(pp.Expression)) dispIdx++;
                    }
                    row = 2 + dispIdx;
                }

                var (oDx, oDy)  = editComp.GetLabelOffset(row);
                var (cpx, cpy)  = WorldToScreen(editComp.X, editComp.Y);
                double textSize = Math.Max(_zoom * 70, 4.0);        // matches renderer (no upper cap)
                double lx = cpx - _zoom * 155 + oDx * _zoom;  // text left edge
                double ly = cpy + _zoom * 120 + textSize
                            + row * (textSize + 2) + oDy * _zoom;  // Skia baseline

                // For parameter rows the rendered label is "<Name> = <Expression> <Unit>".
                // Offset lx past the "<Name> = " prefix so the edit box overlays only the
                // expression+unit value. Use Skia MeasureText for the exact pixel width —
                // the fixed-ratio estimate (chars × 0.55) is inaccurate for IBM Plex Sans and
                // causes a zoom-proportional error.
                if (hit.Kind == SchematicHitTest.HitKind.ComponentParam
                    && hit.SubIndex < editComp.Parameters.Count)
                {
                    var p = editComp.Parameters[hit.SubIndex];
                    if (!string.IsNullOrEmpty(p.Name))
                    {
                        string prefix = $"{p.Name} = ";
                        using var mf = new SKFont(SkiaFonts.PlexRegular, (float)textSize);
                        lx += mf.MeasureText(prefix);
                    }
                }
        TextLabelDoubleTapped?.Invoke(this, new TextLabelHitArgs(hit, lx, ly));
        return true;
    }


    /// <summary>
    /// Open the inline editor on one component parameter, found the way a double-tap finds it — the
    /// documentation factory's entry point for photographing the inline value editor.
    ///
    /// <para>The label's screen position is computed by the SAME arithmetic
    /// <see cref="RaiseLabelDoubleTap"/> uses, then converted back to a world point and put through
    /// the real <see cref="SchematicHitTest"/>. The result is VERIFIED to be the parameter that was
    /// asked for before anything is opened: a figure captioned "editing R" that had quietly landed on
    /// the instance name would be exactly the silently-wrong picture this pipeline exists to refuse.</para>
    /// </summary>
    internal void BeginInlineParamEdit(string instanceName, string paramName)
    {
        if (_editContext is null || _model is null || _index is null)
            throw new InvalidOperationException("The schematic canvas has no model to edit a label on.");

        var comp = _editContext.EditModel.Components
                               .FirstOrDefault(c => c.InstanceName == instanceName)
            ?? throw new InvalidOperationException(
                $"No component named '{instanceName}' in this schematic. It holds: "
              + string.Join(", ", _editContext.EditModel.Components.Select(c => c.InstanceName)) + ".");

        int paramIndex = -1, shownBefore = 0;
        for (int i = 0; i < comp.Parameters.Count; i++)
        {
            var pp = comp.Parameters[i];
            if (pp.Name == paramName) { paramIndex = i; break; }
            if (pp.ShowOnSchematic && !string.IsNullOrEmpty(pp.Expression)) shownBefore++;
        }
        if (paramIndex < 0)
            throw new InvalidOperationException(
                $"'{instanceName}' has no parameter '{paramName}'. It has: "
              + string.Join(", ", comp.Parameters.Select(x => x.Name)) + ".");

        // Where the row lands on screen is the renderer's business, and a hidden type label or a
        // dragged label offset moves it. Rather than assume, sweep the rows the label could be on and
        // accept only a probe the REAL hit test agrees is this parameter. A search that must end in an
        // exact match is not a guess; one unverified point was, and it silently opened the editor on
        // the instance name.
        double textSize = Math.Max(_zoom * 70, 4.0);
        var (cpx, cpy) = WorldToScreen(comp.X, comp.Y);

        SchematicHitTest.HitResult? found = null;
        double foundLx = 0, foundLy = 0;
        var tried = new List<string>();

        for (int probeRow = 0; probeRow <= 3 + shownBefore && found is null; probeRow++)
        {
            var (oDx, oDy) = comp.GetLabelOffset(probeRow);
            double lx = cpx - _zoom * 155 + oDx * _zoom;
            double ly = cpy + _zoom * 120 + textSize + probeRow * (textSize + 2) + oDy * _zoom;

            foreach (double dy in (double[])[0.35, 0.15, 0.55])
            {
                foreach (double dx in (double[])[0.5, 2.0, 4.0, 8.0])
                {
                    double px = ScreenToWorldX(lx + textSize * dx);
                    double py = ScreenToWorldY(ly - textSize * dy);
                    var probe = SchematicHitTest.Test(_editContext.EditModel, _model, _index,
                                                      px, py, zoom: _zoom);
                    if (probe.Kind == SchematicHitTest.HitKind.ComponentParam
                        && probe.Id == comp.Id && probe.SubIndex == paramIndex)
                    {
                        found = probe; foundLx = lx; foundLy = ly;
                        break;
                    }
                    tried.Add(probe.Kind.ToString());
                }
                if (found is not null) break;
            }
        }

        if (found is null)
            throw new InvalidOperationException(
                $"No probe over {instanceName}.{paramName}'s label rows came back as that parameter — "
              + $"the hit test returned [{string.Join(", ", tried.Distinct())}]. The label arithmetic "
              + "and the hit test have drifted apart, and the figure would have opened the editor on "
              + "the wrong text.");

        _ = (foundLx, foundLy);
        RaiseLabelDoubleTap(found.Value);
    }

    // Returns (isHorizontal, baseCoord) where baseCoord is the segment's Y for horizontal
    // or X for vertical — the exact wire coordinate used as the net-label placement base.
    private static (bool IsHorizontal, double BaseCoord) ClassifySegmentAt(EditableWire wire, double wx, double wy)
    {
        const double tol = 8.0;
        var pts = wire.Points;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (!SchematicGeometry.PointOnSegment(wx, wy, pts[i].X, pts[i].Y, pts[i+1].X, pts[i+1].Y, tol))
                continue;
            bool isH = Math.Abs(pts[i+1].Y - pts[i].Y) < tol;
            return (isH, isH ? pts[i].Y : pts[i].X);
        }
        return (true, wy);  // default: treat as horizontal, use click Y
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnKeyDown(object? _, KeyEventArgs e)
    {
        if (_editContext is null) return;

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        // Clipboard shortcuts — handled async by code-behind.
        if (ctrl && e.Key == Key.C) { ClipboardCopyRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { ClipboardCutRequested?.Invoke(this, EventArgs.Empty);  e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { ClipboardPasteRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }

        // F5 — Move Labels (invoked synchronously; BeginMoveLabels snapshots selection state).
        if (e.Key == Key.F5)
        {
            _editContext.BeginMoveLabels();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Delegate to VM for Delete, R, nudge, Enter, and other canvas-specific keys.
        // Esc/S/W/F/Z are owned by the View-level tunnel handler (OnViewKeyDownTunnel) and
        // will already be marked handled before this bubble handler fires, so the VM won't
        // re-process them.
        if (_editContext.OnKeyDown(e.Key, e.KeyModifiers))
            e.Handled = true;
        InvalidateVisual();
    }

    // ── Scroll-wheel zoom ─────────────────────────────────────────────────────

    /// <summary>
    /// Applies a scroll-wheel zoom step at a given canvas-local position.
    /// Called by the canvas's own wheel handler and by overlays (e.g. inline edit box)
    /// that need to forward wheel events so zoom works even when the mouse is over them.
    /// </summary>
    public void ZoomAtPoint(Point canvasPos, double deltaY)
    {
        double wx = canvasPos.X / _zoom + _panX;
        double wy = canvasPos.Y / _zoom + _panY;
        double factor = deltaY > 0 ? 1.0 / ZoomFactor : ZoomFactor;
        _zoom = Math.Clamp(_zoom / factor, MinZoom, MaxZoom);
        _panX = wx - canvasPos.X / _zoom;
        _panY = wy - canvasPos.Y / _zoom;
        if (_editContext is not null) _editContext.CanvasZoom = _zoom;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPointerWheel(object? _, PointerWheelEventArgs e)
    {
        ZoomAtPoint(e.GetPosition(this), e.Delta.Y);
        e.Handled = true;
    }

    // ── Palette DnD drop target ───────────────────────────────────────────────

    private void OnPaletteDragOver(object? sender, DragEventArgs e)
    {
        // Accept only text drops whose content parses as our palette payload (prefix-guarded).
        // Foreign text drops (random files, browser drags, etc.) are silently rejected.
        PaletteDragPayload? p = null;
        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.Text) is string text
                && PaletteDragPayload.TryParse(text, out var found))
            { p = found; break; }
        }

        if (p is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled     = true;

        // Show the placement ghost following the drag cursor.
        if (_editContext is not null)
        {
            var pos  = e.GetPosition(this);
            double sx = _editContext.EditModel.SnapToGrid(ScreenToWorldX(pos.X));
            double sy = _editContext.EditModel.SnapToGrid(ScreenToWorldY(pos.Y));
            // A kit part's SymbolKind is only a placeholder, so a ghost built from it alone draws a
            // generic box while the DROP places the kit's real symbol — the drag showing one thing
            // and the result another. Resolve the cell's own symbol so both agree.
            var (ghostPrims, ghostPins) = ResolveGhostSymbol(p.CellDir);

            _editContext.Overlay = _editContext.Overlay with
            {
                Ghost = new PlacementGhost(sx, sy, p.Kind, _editContext.CurrentPlacementRotation, false,
                                           p.PortCount, ghostPrims, ghostPins),
            };
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Primitives and pins of the cell's primary symbol, or (null, null) for a built-in entry or an
    /// unresolvable cell — in which case the caller falls back to the SymbolKind glyph, exactly as
    /// before. Resolution is cached by <see cref="CellSymbolResolver"/>, so calling this per
    /// drag-over tick is cheap.
    /// </summary>
    private static (IReadOnlyList<SymbolPrimitive>?, IReadOnlyList<SymbolPin>?) ResolveGhostSymbol(string? cellDir)
    {
        var res = CellSymbolResolver.ResolveCellDirOrRef(cellDir);
        return res.State == CellSymbolState.Resolved
            ? (res.Symbol!.Primitives, res.Symbol.Pins)
            : (null, null);
    }

    private void OnPaletteDragLeave(object? sender, DragEventArgs e)
    {
        if (_editContext is not null)
            _editContext.Overlay = _editContext.Overlay with { Ghost = null };
        InvalidateVisual();
    }

    private void OnPaletteDrop(object? sender, DragEventArgs e)
    {
        // Clear drag ghost regardless of outcome.
        if (_editContext is not null)
            _editContext.Overlay = _editContext.Overlay with { Ghost = null };

        if (_editContext is null) return;
        PaletteDragPayload? payload = null;
        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.Text) is string text
                && PaletteDragPayload.TryParse(text, out var p))
            { payload = p; break; }
        }
        if (payload is null) return;

        var pos      = e.GetPosition(this);
        double wx    = ScreenToWorldX(pos.X);
        double wy    = ScreenToWorldY(pos.Y);
        var rotation = _editContext.CurrentPlacementRotation;

        // A kit part places as the cell its symbol was installed into — the SAME path the
        // click-to-arm gesture takes, so dragging and clicking a tile can never disagree.
        if (payload.CellDir is { Length: > 0 } cellDir)
            _ = _editContext.CommitCellPlacementAsync(cellDir, wx, wy, rotation);
        else
            _editContext.CommitPlacement(payload.Kind, payload.PortCount, rotation, wx, wy);

        e.Handled = true;
        InvalidateVisual();
    }

    // ── Cell DnD drop target ──────────────────────────────────────────────────
    // Fires after the palette handlers (palette marks e.Handled on accept).
    // Cell drags carry the circuitrf-cell: prefix; palette and image drags do not parse.
    // DragLeave is handled by the existing OnPaletteDragLeave (clears ghost for all drags).

    private void OnCellDragOver(object? sender, DragEventArgs e)
    {
        CellDragPayload? payload = null;
        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.Text) is string text
                && CellDragPayload.TryParse(text, out var found))
            { payload = found; break; }
        }

        if (payload is null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled     = true;

        // Show a ghost following the drag cursor — resolved symbol when available, neutral box on fallback.
        if (_editContext is not null)
        {
            var pos      = e.GetPosition(this);
            double sx    = _editContext.EditModel.SnapToGrid(ScreenToWorldX(pos.X));
            double sy    = _editContext.EditModel.SnapToGrid(ScreenToWorldY(pos.Y));
            var rotation = _editContext.CurrentPlacementRotation;

            PlacementGhost ghost;
            var schDir = _editContext.EditModel.SchematicDirectory;
            if (schDir is not null)
            {
                try
                {
                    var cellRef    = Path.GetRelativePath(schDir, payload.CellAbsPath);
                    var resolution = CellSymbolResolver.Resolve(cellRef, schDir);
                    if (resolution.State == CellSymbolState.Resolved && resolution.Symbol is { } sym)
                        ghost = new PlacementGhost(sx, sy, SymbolKind.Generic, rotation, false,
                            sym.PortCount, sym.Primitives, sym.Pins);
                    else
                        ghost = new PlacementGhost(sx, sy, SymbolKind.Generic, rotation, false, 2);
                }
                catch
                {
                    ghost = new PlacementGhost(sx, sy, SymbolKind.Generic, rotation, false, 2);
                }
            }
            else
            {
                ghost = new PlacementGhost(sx, sy, SymbolKind.Generic, rotation, false, 2);
            }

            _editContext.Overlay = _editContext.Overlay with { Ghost = ghost };
            InvalidateVisual();
        }
    }

    private async void OnCellDrop(object? sender, DragEventArgs e)
    {
        // Clear ghost before processing so it disappears on any outcome.
        if (_editContext is not null)
            _editContext.Overlay = _editContext.Overlay with { Ghost = null };

        if (_editContext is null) return;
        CellDragPayload? payload = null;
        foreach (var item in e.DataTransfer.Items)
        {
            if (item.TryGetRaw(DataFormat.Text) is string text
                && CellDragPayload.TryParse(text, out var p))
            { payload = p; break; }
        }
        if (payload is null) return;

        var pos      = e.GetPosition(this);
        double wx    = ScreenToWorldX(pos.X);
        double wy    = ScreenToWorldY(pos.Y);
        var rotation = _editContext.CurrentPlacementRotation;

        await _editContext.CommitCellPlacementAsync(payload.CellAbsPath, wx, wy, rotation);
        e.Handled = true;
        InvalidateVisual();
    }

    // ── Image file DnD ────────────────────────────────────────────────────────
    // Fires AFTER the palette and cell handlers; cell handler marks e.Handled=true on accept,
    // so the file handlers only run when both palette and cell rejected the drag.

    private void OnImageFileDragOver(object? _, DragEventArgs e)
    {
        DropDiagnostics.Dump("SchematicCanvas.DragOver", e);
        if (TryExtractImagePath(e) is not null)
        { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
        else
            e.DragEffects = DragDropEffects.None;
    }

    private void OnImageFileDrop(object? _, DragEventArgs e)
    {
        DropDiagnostics.Dump("SchematicCanvas.Drop", e);
        var path = TryExtractImagePath(e);
        if (path is null || _editContext is null) return;
        var pos = e.GetPosition(this);
        _editContext.DropBitmap(path, ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y));
        e.Handled = true;
        InvalidateVisual();
    }

    // The OS surfaces a dropped file under DataFormat.File. The payload TYPE varies by platform:
    // macOS (Avalonia.Native) returns a SINGLE IStorageItem; other backends may return an
    // IEnumerable<IStorageItem>. Handle both, plus a bare path string, defensively.
    private static string? TryExtractImagePath(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            var raw = item.TryGetRaw(DataFormat.File);

            string? path = raw switch
            {
                IStorageItem single             => single.Path?.LocalPath,
                IEnumerable<IStorageItem> files => files.FirstOrDefault()?.Path?.LocalPath,
                string s                        => s,
                _                               => null,
            };

            if (path is not null && IsImageExtension(path)) return path;
        }
        return null;
    }

    private static bool IsImageExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".png",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tif",  StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }

    // ── ICustomDrawOperation ──────────────────────────────────────────────────

    private sealed class SchematicDrawOperation : ICustomDrawOperation
    {
        private readonly Rect                    _bounds;
        private readonly SchematicModel?         _model;
        private readonly SchematicSpatialIndex?  _index;
        private readonly double                  _panX, _panY, _zoom;
        private readonly SchematicRenderTheme    _theme;
        private readonly SchematicOverlay?       _overlay;

        public SchematicDrawOperation(
            Rect bounds, SchematicModel? model, SchematicSpatialIndex? index,
            double panX, double panY, double zoom,
            SchematicRenderTheme theme, SchematicOverlay? overlay)
        {
            _bounds    = bounds; _model = model; _index = index;
            _panX = panX; _panY = panY; _zoom = zoom;
            _theme = theme; _overlay = overlay;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            SchematicRenderer.Draw(
                lease.SkCanvas, (_bounds.Width, _bounds.Height),
                _model, _index, _panX, _panY, _zoom, _theme,
                _overlay);
        }

        public void Dispose() { }
    }
}
