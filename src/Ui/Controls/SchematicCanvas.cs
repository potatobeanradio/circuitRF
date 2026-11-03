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
using CircuitRF.Design.Schematic.Import;
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
            _needsInitialFit = value is not null && !_viewportEstablished;
            if (FitOnResize && _followingFit) ZoomToFitInternal(Bounds.Width, Bounds.Height);
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
            // A different session in a canvas that already has a view of its own: a push into a cell or
            // back up to its parent. The parent's pan/zoom says nothing about where the cell's drawing is,
            // so the session comes back at the view it was left at, or fitted when it has never been shown.
            bool retarget = _viewportEstablished && _editContext is not null && value is not null
                            && !ReferenceEquals(_editContext, value);
            if (_editContext is not null)
            {
                _editContext.PropertyChanged -= OnVmPropertyChanged;
                _editContext.ZoomToRectCallback = null;
                _editContext.ViewportProvider   = null;
                _editContext.PointerWorldProvider = null;
            }

            SetAndRaise(EditContextProperty, ref _editContext, value);

            if (_editContext is not null)
            {
                _editContext.PropertyChanged += OnVmPropertyChanged;
                _editContext.ZoomToRectCallback = ZoomToRect;
                _editContext.ViewportProvider   = () => WorldViewport;
                _editContext.PointerWorldProvider = PointerWorld;

                // A session this canvas has not shown yet but that HAS been on screen before — the
                // view was re-realised — comes back at the pan/zoom it was left at instead of being
                // fitted again. Field report (2026-09-22): the zoom was lost going schematic -> layout
                // -> schematic. Only a canvas that has not established a view of its own restores:
                // a push-in inside a live canvas keeps the behaviour it has always had.
                if ((!_viewportEstablished || retarget) && _editContext.LastViewport is { } last)
                {
                    (_panX, _panY, _zoom) = last;
                    MarkViewportEstablished();
                }
                _editContext.CanvasZoom = _zoom;
                _fitOnRetarget = retarget && _editContext.LastViewport is null;
                SyncFromVm();
                UpdateCursor();
            }
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SchematicViewModel.RenderModel)
                           or nameof(SchematicViewModel.SpatialIndex)
                           or nameof(SchematicViewModel.Overlay)
                           or nameof(SchematicViewModel.TunedLabels))
            SyncFromVm();
        else if (e.PropertyName is nameof(SchematicViewModel.ActiveTool)
                                or nameof(SchematicViewModel.DuplicateDragArmed))
            UpdateCursor();
    }

    /// <summary>R-dup-1: Alt held, for the copy CURSOR. Cleared on key-up AND on LostFocus, because
    /// the key-up after a click on a toolbar button is delivered to whatever took focus — the latch
    /// bug the layout canvas already shipped once with Space-to-pan.</summary>
    private bool _altHeld;

    private void UpdateCursor() => ApplyCursor(DesiredCursor(), reassert: false);

    private StandardCursorType DesiredCursor()
    {
        if (_isLeftPanning || _isPanning) return StandardCursorType.Hand;
        if (_editContext is null && _viewZoomBoxArmed) return StandardCursorType.Cross;

        // R-dup-1: Alt says the drag will leave the original where it is and take a copy. Shown while
        // the duplicate is actually armed, and also on a bare Alt with something selected, so the
        // gesture announces itself BEFORE the press rather than only once the ghost appears.
        if (_editContext is { ActiveTool: SchematicViewModel.Tool.Select } vm
            && (vm.DuplicateDragArmed || (_altHeld && vm.HasDuplicableSelection)))
            return StandardCursorType.DragCopy;

        return _editContext?.ActiveTool switch
        {
            SchematicViewModel.Tool.Pan        => StandardCursorType.Hand,
            SchematicViewModel.Tool.Wire       => StandardCursorType.Cross,
            SchematicViewModel.Tool.Place      => StandardCursorType.Cross,
            SchematicViewModel.Tool.ZoomBox    => StandardCursorType.Cross,
            SchematicViewModel.Tool.MoveLabels => StandardCursorType.SizeAll,
            SchematicViewModel.Tool.Text       => StandardCursorType.Cross,
            _                                  => StandardCursorType.Arrow,
        };
    }

    // Two instances per cursor type, alternated by a re-assert: Avalonia hands a cursor to the
    // platform only when the pointer-over element changes or its Cursor PROPERTY changes, so
    // re-assigning an equal cursor needs a different instance to reach the OS at all.
    private readonly Dictionary<StandardCursorType, (Cursor A, Cursor B)> _cursors = new();
    private StandardCursorType? _appliedCursorType;
    private bool _appliedCursorIsB;

    /// <summary>
    /// Sets the canvas cursor. <paramref name="reassert"/> pushes it to the platform again even when
    /// the type has not changed — the macOS native cursor is process-global, and AppKit resets it to
    /// the arrow on its own (cursor-rect rebuilds after a window or layout change), which Avalonia
    /// does not observe. Field report: the wire tool stayed armed after a wire ended on a pin, but
    /// the cross had become an arrow and stayed one while the mouse moved over the canvas, and
    /// pressing W again changed nothing because the tool was already Wire.
    /// </summary>
    internal void ApplyCursor(StandardCursorType type, bool reassert)
    {
        if (!reassert && _appliedCursorType == type) return;
        if (!_cursors.TryGetValue(type, out var pair))
            _cursors[type] = pair = (new Cursor(type), new Cursor(type));
        _appliedCursorIsB = _appliedCursorType == type && !_appliedCursorIsB;
        _appliedCursorType = type;
        Cursor = _appliedCursorIsB ? pair.B : pair.A;
    }

    /// <summary>Re-sends the current tool's cursor to the platform — what a pointer move over the
    /// canvas and a re-press of the armed tool's key do.</summary>
    internal void ReassertCursor() => ApplyCursor(DesiredCursor(), reassert: true);

    private void SyncFromVm()
    {
        if (_editContext is null) return;
        _model   = _editContext.RenderModel;
        _index   = _editContext.SpatialIndex;
        _overlay = _editContext.TunedLabels is { } tuned
            ? _editContext.Overlay with { TunedLabels = tuned }
            : _editContext.Overlay;
        _needsInitialFit = _model is not null && _needsInitialFit;
        if (_fitOnRetarget && _model is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _fitOnRetarget = false;
            ZoomToFitInternal(Bounds.Width, Bounds.Height);
            RaiseViewportChanged();
            _followingFit = true;
        }
        InvalidateVisual();
    }

    /// <summary>A push into a cell (or up to a parent) never shown in this canvas: fitted as soon as its
    /// drawing is there, rather than left at the view of the schematic it came from.</summary>
    private bool _fitOnRetarget;

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

    // ── Read-only navigation — a canvas with no EditContext (a preview) ──────
    // Z arms a zoom box, as it arms the editor's ZoomBox tool; F fits. The editor's own Z and F are
    // the View's (OnViewKeyDownTunnel), which a preview has none of.
    private bool _viewZoomBoxArmed;
    private (double X, double Y)? _viewZoomBoxStart;

    /// <summary>Draw the editing grid. A preview turns it off, as the static previews do.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>
    /// Keep the drawing fitted as the canvas changes size, or its model changes, until the user pans or zooms — a
    /// docked preview whose panel is resized or re-arranged. Zoom to Fit resumes it. Off for the editor, whose view is
    /// the user's.
    /// </summary>
    public bool FitOnResize { get; set; }

    /// <summary>The view is the fit, and no pan or zoom has moved it since.</summary>
    private bool _followingFit;

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

    /// <summary>The parameter (its index in the component's full list) whose shown value the last
    /// right-click landed on, or null — what the context menu's Tune acts on (brief-tuneopt-4).</summary>
    public int? ContextMenuParamIndex { get; private set; }

    /// <summary>The placed picture a right-click landed on when it hit no component, or null — what the canvas menu's
    /// Create … from Image rows read (brief-img-6-entry-points.md R-im6-3). A LOCKED bitmap counts: locking stops
    /// dragging, not reading, so this is not the selection hit test, which passes over a locked object.</summary>
    public string? ContextMenuBitmapId { get; private set; }

    /// <summary>The topmost bitmap under (<paramref name="wx"/>, <paramref name="wy"/>), locked or not — the hit test's
    /// own canvas-object order.</summary>
    private static EditableBitmap? BitmapUnder(SchematicEditModel model, double wx, double wy)
    {
        for (int i = model.CanvasObjects.Count - 1; i >= 0; i--)
        {
            if (model.CanvasObjects[i] is not EditableBitmap b) continue;
            var bb = b.GetBoundingBox();
            if (wx >= bb.MinX && wx <= bb.MaxX && wy >= bb.MinY && wy <= bb.MaxY) return b;
        }
        return null;
    }

    // ── Internal state ────────────────────────────────────────────────────────

    private SchematicSpatialIndex? _index;
    private SchematicOverlay?      _overlay;
    private bool _needsInitialFit;
    private bool _viewportEstablished;

    // ── Events (raised to code-behind) ────────────────────────────────────────

    public event EventHandler<EditableComponent>?  ComponentDoubleTapped;
    public event EventHandler<TextLabelHitArgs>?   TextLabelDoubleTapped;
    public event EventHandler<WireHitArgs>?        WireDoubleTapped;
    /// <summary>A free text was double-clicked with the Select tool: edit its content in place.</summary>
    public event EventHandler<EditableText>?       CanvasTextDoubleTapped;

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
        KeyUp               += OnKeyUp;
        LostFocus           += OnCanvasLostFocus;
        DoubleTapped        += OnDoubleTapped;
        TextInput           += OnTextInput;

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
        //   4. A parts table (.csv / .tsv / .txt) — places the parts it lists.
        AddHandler(DragDrop.DragOverEvent,  OnPartsTableDragOver);
        AddHandler(DragDrop.DropEvent,      OnPartsTableDrop);
        //   5. A schematic design file (.kicad_sch / EDIF / binary .DSN) — built into this schematic at the drop
        //      point (brief-schimp-5 R-si5-3). A picture is never one: images stay with handler 3.
        AddHandler(DragDrop.DragOverEvent,  OnSchematicFileDragOver);
        AddHandler(DragDrop.DropEvent,      OnSchematicFileDrop);
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _model is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            MarkViewportEstablished();
            ZoomToFitInternal(Bounds.Width, Bounds.Height);
            _followingFit = true;
            RememberViewport();
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!FitOnResize || !_followingFit || _model is null) return;
        ZoomToFitInternal(e.NewSize.Width, e.NewSize.Height);
        RememberViewport();
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>This canvas has a view of its own now — fitted or restored — so the one-shot initial
    /// fit is spent. Unsubscribing is what makes the Model setter's re-arm harmless afterwards.</summary>
    private void MarkViewportEstablished()
    {
        _viewportEstablished = true;
        _needsInitialFit = false;
        LayoutUpdated -= OnLayoutUpdated;
    }

    private void RememberViewport()
    {
        if (_editContext is not null) _editContext.LastViewport = (_panX, _panY, _zoom);
    }

    /// <summary>Every pan/zoom change funnels through here, so the session always holds the view
    /// a re-realised canvas should come back to.</summary>
    private void RaiseViewportChanged()
    {
        _followingFit = false;
        RememberViewport();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
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
            _panX, _panY, _zoom, theme, _overlay, excludeGrid: !ShowGrid));
    }

    // ── Navigation ───────────────────────────────────────────────────────────

    public void ZoomToFit()
    {
        ZoomToFitInternal(Bounds.Width, Bounds.Height);
        InvalidateVisual();
        RaiseViewportChanged();
        _followingFit = true;
    }

    /// <summary>
    /// Brings a world rectangle on screen with as little change to the view as possible: nothing when
    /// it is already fully visible, a pan that centres it when it fits at the current zoom, and a
    /// zoom out to fit it only when it does not. The user's zoom is theirs; a command that put
    /// something off screen owes them a pan, not a re-framing.
    /// </summary>
    public void RevealWorldRect(double minX, double minY, double maxX, double maxY)
    {
        double canvasW = Bounds.Width, canvasH = Bounds.Height;
        if (canvasW < 1 || canvasH < 1 || _zoom <= 0) return;

        double viewW = canvasW / _zoom, viewH = canvasH / _zoom;
        if (minX >= _panX && maxX <= _panX + viewW && minY >= _panY && maxY <= _panY + viewH) return;

        const double pad = 0.05;
        double w = Math.Max(maxX - minX, 1), h = Math.Max(maxY - minY, 1);
        if (w > viewW * (1 - 2 * pad) || h > viewH * (1 - 2 * pad))
        {
            _zoom = Math.Clamp(Math.Min(canvasW / w, canvasH / h) * (1.0 - 2 * pad), MinZoom, MaxZoom);
            if (_editContext is not null) _editContext.CanvasZoom = _zoom;
            viewW = canvasW / _zoom;
            viewH = canvasH / _zoom;
        }
        _panX = (minX + maxX) / 2 - viewW / 2;
        _panY = (minY + maxY) / 2 - viewH / 2;
        InvalidateVisual();
        RaiseViewportChanged();
    }

    /// <summary>
    /// Frames a world rectangle — zooms so it fills the view and centres it, whatever the zoom was.
    /// <see cref="RevealWorldRect"/>'s opposite, for a command whose whole point is to SHOW something
    /// (the Instances panel's double-click, brief-find-instance-panel.md R-fi-10).
    /// </summary>
    public void FrameWorldRect(double minX, double minY, double maxX, double maxY)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        ZoomToRect(minX, minY, maxX, maxY);
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
        foreach (var sh in model.Shapes)
        {
            var (x0, y0, x1, y1) = CircuitRF.Design.Symbol.SymbolGeometry.BboxOf(sh.Primitive);
            Union(x0, y0, x1, y1);
        }

        // Nothing on the sheet: keep the model's own empty-schematic box rather than inventing one.
        if (minX == double.MaxValue)
            return (model.BbMinX, model.BbMinY, model.BbMaxX, model.BbMaxY);

        const double margin = 200;   // the same breathing room the model box adds
        return (minX - margin, minY - margin, maxX + margin, maxY + margin);
    }

    /// <summary>One step closer, anchored on the middle of the canvas — the keyboard's
    /// Ctrl/⌘+'+'. Centre-anchored rather than pointer-anchored because the keyboard gesture
    /// carries no pointer position, which is the same choice <c>LayoutCanvas.ZoomIn</c> makes.</summary>
    public void ZoomIn()  => ZoomAtPoint(new Point(Bounds.Width / 2.0, Bounds.Height / 2.0), +1);

    /// <summary>One step further out, anchored on the middle of the canvas — Ctrl/⌘+'-'.</summary>
    public void ZoomOut() => ZoomAtPoint(new Point(Bounds.Width / 2.0, Bounds.Height / 2.0), -1);

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
        RaiseViewportChanged();
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

    /// <summary>The pointer's world position while it is over this canvas, else null — where a paste's ghost
    /// appears (SchematicViewModel.PointerWorldProvider).</summary>
    private (double X, double Y)? PointerWorld()
        => IsPointerOver && _lastPointerScreen is { } pos ? (ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y)) : null;

    private double ScreenToWorldX(double sx) => sx / _zoom + _panX;
    private double ScreenToWorldY(double sy) => sy / _zoom + _panY;

    /// <summary>Converts a world position to canvas screen coordinates.</summary>
    public (double X, double Y) WorldToScreen(double wx, double wy)
        => ((wx - _panX) * _zoom, (wy - _panY) * _zoom);

    // ── Pointer — press ───────────────────────────────────────────────────────

    /// <summary>
    /// Takes keyboard focus for the canvas.
    ///
    /// <para><b>A drop has to do this, and it is not obvious.</b> Dropping a tile from the Library
    /// palette places the component and SELECTS it, but the drag began in the palette, so that is
    /// where keyboard focus still is — and every editing key (R, M, arrows, Delete) is routed by
    /// this control's own KeyDown. The result is a part that looks ready to work on and ignores the
    /// keyboard until the user clicks it. <c>OnPointerPressed</c> has always taken focus on its
    /// first line for exactly this reason; a drop finishes the same gesture and owes the same.</para>
    /// </summary>
    private void TakeKeyboardFocus() => Focus();

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
            ContextMenuParamIndex = null;
            ContextMenuBitmapId = null;
            if (_editContext is not null && _model is not null && _index is not null)
            {
                var hit = SchematicHitTest.Test(_editContext.EditModel, _model, _index, wx, wy, zoom: _zoom);
                if (hit.Kind is SchematicHitTest.HitKind.Component
                    or SchematicHitTest.HitKind.ComponentType
                    or SchematicHitTest.HitKind.ComponentName
                    or SchematicHitTest.HitKind.ComponentParam
                    or SchematicHitTest.HitKind.ComponentVendorValue
                    or SchematicHitTest.HitKind.ComponentFootprint)
                {
                    ContextMenuTargetId = hit.Id;
                    if (hit.Kind == SchematicHitTest.HitKind.ComponentParam) ContextMenuParamIndex = hit.SubIndex;
                    // Also select the right-clicked component if not already selected
                    _editContext.SelectIfUnselected(hit.Id);
                }
                else if (BitmapUnder(_editContext.EditModel, wx, wy) is { } bitmap)
                    ContextMenuBitmapId = bitmap.Id;
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
        else if (_viewZoomBoxArmed)
        {
            _viewZoomBoxStart = (wx, wy);
            e.Pointer.Capture(this);
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

    // Where the pointer last moved over this canvas, in control coordinates — what a keyboard-armed
    // placement shows its ghost at before the mouse has moved again.
    private Point? _lastPointerScreen;

    /// <summary>
    /// Shows the armed placement's ghost under the pointer straight away, as if the mouse had moved —
    /// for a placement armed from the KEYBOARD (G / T / P), where the pointer is already resting on the
    /// canvas and no move event is coming. Does nothing when the pointer is not over this canvas: then
    /// the ghost waits for the first move, as a toolbar-armed placement's does.
    /// </summary>
    public void ShowPlacementGhostAtPointer()
    {
        if (_editContext is null || !IsPointerOver || _lastPointerScreen is not { } pos) return;
        _editContext.OnPointerMoved(ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y), leftDown: false, pos.X, pos.Y);
        InvalidateVisual();
    }

    private void OnPointerMoved(object? _, PointerEventArgs e)
    {
        var pos   = e.GetPosition(this);
        _lastPointerScreen = pos;
        double wx = ScreenToWorldX(pos.X);
        double wy = ScreenToWorldY(pos.Y);

        if (_isPanning || _isLeftPanning)
        {
            _panX = _panDragStartPanX - (pos.X - _panDragStartScreen.X) / _zoom;
            _panY = _panDragStartPanY - (pos.Y - _panDragStartScreen.Y) / _zoom;
            InvalidateVisual();
            RaiseViewportChanged();
            return;
        }

        // macOS only: AppKit can put the arrow back behind Avalonia's back (see ApplyCursor), and a
        // move is the moment the user is looking at the pointer.
        if (OperatingSystem.IsMacOS()) ReassertCursor();

        if (_editContext is null && _viewZoomBoxStart is { } start)
        {
            _overlay = (_overlay ?? SchematicOverlay.Empty) with
            {
                RubberBand = (Math.Min(start.X, wx), Math.Min(start.Y, wy),
                              Math.Abs(wx - start.X), Math.Abs(wy - start.Y)),
                RubberBandCrossing = false,
            };
            InvalidateVisual();
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

        if (_editContext is null && _viewZoomBoxStart is { } start)
        {
            var pos = e.GetPosition(this);
            double wx = ScreenToWorldX(pos.X), wy = ScreenToWorldY(pos.Y);
            _viewZoomBoxStart = null;
            _overlay = _overlay is null ? null : _overlay with { RubberBand = null };
            e.Pointer.Capture(null);
            // The editor's own rule (HandleZoomBoxRelease): a click without a drag zooms nothing, and
            // the box disarms either way, as the ZoomBox tool returns to Select.
            if (Math.Abs(wx - start.X) > 1 && Math.Abs(wy - start.Y) > 1)
                ZoomToRect(Math.Min(start.X, wx), Math.Min(start.Y, wy), Math.Max(start.X, wx), Math.Max(start.Y, wy));
            SetViewZoomBoxArmed(false);
            InvalidateVisual();
            return;
        }

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

        // The Text tool's double-click is two clicks that each start a text, as in the symbol editor.
        if (_editContext.ActiveTool == SchematicViewModel.Tool.Text) return;

        var hit = SchematicHitTest.Test(_editContext.EditModel, _model, _index, wx, wy, zoom: _zoom);

        switch (hit.Kind)
        {
            case SchematicHitTest.HitKind.CanvasObject
                when _editContext.EditModel.FindCanvasObject(hit.Id) is EditableText txt:
                CanvasTextDoubleTapped?.Invoke(this, txt);
                break;
            case SchematicHitTest.HitKind.ComponentType:
            case SchematicHitTest.HitKind.ComponentName:
            case SchematicHitTest.HitKind.ComponentParam:
            // A vendor part's value row opens the inline editor with the library search under it.
            case SchematicHitTest.HitKind.ComponentVendorValue:
                RaiseLabelDoubleTap(hit);
                break;
            // The footprint label is a READOUT of a choice made from a closed list, so the inline
            // text editor is the wrong gesture for it entirely — a typed `smt:0403` would be a
            // stored reference nothing can resolve. Double-tapping it opens the same dialog
            // double-tapping the body does, which is where the picker is.
            case SchematicHitTest.HitKind.ComponentFootprint:
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
                // Alt (Option on macOS) + double-click selects the wire's run up to the component
                // pins; the plain double-click below still opens the net-label editor.
                if ((e.KeyModifiers & KeyModifiers.Alt) != 0)
                {
                    if (_editContext.SelectWireRun(hit.Id, wx, wy)) InvalidateVisual();
                    break;
                }
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
                          or SchematicHitTest.HitKind.ComponentParam
                          or SchematicHitTest.HitKind.ComponentVendorValue)) return false;
        return RaiseLabelDoubleTap(hit);
    }

    /// <summary>
    /// World anchor (left edge, Skia baseline) of one component label row — the renderer's own
    /// <see cref="SchematicComponent.LabelRowGeometry"/>, given the real glyph extent so a resolved
    /// cell symbol and the annotation symbols land where they are actually drawn.
    /// </summary>
    private (double X, double Y) LabelRowAnchorWorld(
        EditableComponent comp, int row, double oDx, double oDy)
    {
        double? glyphHalfH = _editContext is null
            ? null
            : _editContext.EditModel.EffectiveGlyphBbOf(comp).MaxY - comp.Y;
        var (bx, by, _, _) = SchematicComponent.LabelRowGeometry(
            comp.X, comp.Y, row, oDx, oDy, comp.Symbol, comp.PortCount, glyphHalfH);
        return (bx, by);
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
                else if (hit.Kind == SchematicHitTest.HitKind.ComponentVendorValue) row = hit.SubIndex;
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
                double textSize = Math.Max(_zoom * 70, 4.0);        // matches renderer (no upper cap)
                // The anchor comes from LabelRowGeometry, the same helper the renderer draws with —
                // the arithmetic that used to be repeated here approximated it, and an approximation
                // cannot follow a per-symbol anchor (VAR/MEAS hug their glyph).
                var (lxw, lyw) = LabelRowAnchorWorld(editComp, row, oDx, oDy);
                var (lx, ly)   = WorldToScreen(lxw, lyw);

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
                else if (hit.Kind == SchematicHitTest.HitKind.ComponentVendorValue)
                {
                    using var mf = new SKFont(SkiaFonts.PlexRegular, (float)textSize);
                    lx += mf.MeasureText("C = ");
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

        SchematicHitTest.HitResult? found = null;
        double foundLx = 0, foundLy = 0;
        var tried = new List<string>();

        for (int probeRow = 0; probeRow <= 3 + shownBefore && found is null; probeRow++)
        {
            var (oDx, oDy) = comp.GetLabelOffset(probeRow);
            var (lxw, lyw) = LabelRowAnchorWorld(comp, probeRow, oDx, oDy);
            var (lx, ly)   = WorldToScreen(lxw, lyw);

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

    private void OnKeyUp(object? _, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt) { _altHeld = false; UpdateCursor(); }
    }

    private void OnCanvasLostFocus(object? _, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _altHeld = false;
        UpdateCursor();
    }

    private void OnTextInput(object? _, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 } || _editContext is not { IsTypingText: true } vm) return;
        vm.OnTextInput(e.Text);
        e.Handled = true;
    }

    private void OnKeyDown(object? _, KeyEventArgs e)
    {
        // Deliberately not `return` — Alt is a modifier, and swallowing it here would cost every
        // Alt-combination below it.
        if (e.Key is Key.LeftAlt or Key.RightAlt) { _altHeld = true; UpdateCursor(); }

        if (_editContext is null) { OnViewerKeyDown(e); return; }

        // Typing a text: every key is the text's (the VM consumes them). A character key is left
        // unhandled so the platform still delivers it as TextInput; the editing keys are claimed.
        if (_editContext.IsTypingText)
        {
            _editContext.OnKeyDown(e.Key, e.KeyModifiers);
            if (e.Key is Key.Enter or Key.Return or Key.Back or Key.Escape) e.Handled = true;
            InvalidateVisual();
            return;
        }

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;

        // Clipboard shortcuts — handled async by code-behind.
        if (ctrl && e.Key == Key.C) { ClipboardCopyRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { ClipboardCutRequested?.Invoke(this, EventArgs.Empty);  e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { ClipboardPasteRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }

        // Ctrl/⌘ +/- step the zoom, exactly as the layout editor's canvas does. OemPlus/OemMinus are
        // the main row — and OemPlus is what the key reports whether or not Shift is down, which is
        // what makes ⌘+'+' work without a separate Shift branch; Add/Subtract are the numeric keypad,
        // which reports different keys for the same characters.
        if (ctrl && e.Key is Key.OemPlus  or Key.Add)      { ZoomIn();  e.Handled = true; return; }
        if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { ZoomOut(); e.Handled = true; return; }

        // F5 — Move Labels (invoked synchronously; BeginMoveLabels snapshots selection state).
        if (e.Key == Key.F5)
        {
            _editContext.BeginMoveLabels();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Arrow keys pan the VIEW when there is nothing to nudge — see CanvasArrowPan for why the
        // gesture exists and why the selection gates it. Ahead of the VM delegation below because
        // SchematicViewModel.OnKeyDown consumes all four arrows unconditionally (its NudgeSelection
        // is simply a no-op on an empty selection), so a pan placed after it would never run.
        // Suppressed while an inline text box is open: there an arrow key is caret movement.
        if (TryArrowPan(e)) { e.Handled = true; return; }

        // Delegate to VM for Delete, R, nudge, Enter, and other canvas-specific keys.
        // Esc/S/W/F/Z are owned by the View-level tunnel handler (OnViewKeyDownTunnel) and
        // will already be marked handled before this bubble handler fires, so the VM won't
        // re-process them.
        if (_editContext.OnKeyDown(e.Key, e.KeyModifiers))
            e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>The keys a canvas with no EditContext answers: Z zoom box, F fit, Escape disarms the box,
    /// Ctrl/⌘ +/- step the zoom. Escape is claimed only when it disarmed something, so a dialog's Cancel
    /// still gets it otherwise.</summary>
    private void OnViewerKeyDown(KeyEventArgs e)
    {
        if (_model is null) return;
        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        if (ctrl && e.Key is Key.OemPlus  or Key.Add)      { ZoomIn();  e.Handled = true; return; }
        if (ctrl && e.Key is Key.OemMinus or Key.Subtract) { ZoomOut(); e.Handled = true; return; }
        if (ctrl) return;
        switch (e.Key)
        {
            case Key.Z: SetViewZoomBoxArmed(true); e.Handled = true; break;
            case Key.F: ZoomToFit(); e.Handled = true; break;
            case Key.Escape when _viewZoomBoxArmed:
                _viewZoomBoxStart = null;
                _overlay = _overlay is null ? null : _overlay with { RubberBand = null };
                SetViewZoomBoxArmed(false);
                InvalidateVisual();
                e.Handled = true;
                break;
        }
    }

    private void SetViewZoomBoxArmed(bool armed)
    {
        _viewZoomBoxArmed = armed;
        UpdateCursor();
    }

    /// <summary>Pans on a bare arrow key when the selection is empty. Returns false (leaving the key
    /// to the nudge) whenever something is selected or a text gesture owns the keyboard.</summary>
    private bool TryArrowPan(KeyEventArgs e)
    {
        if (_editContext is null || !_editContext.Selection.IsEmpty || _editContext.IsInlineEditing
            || _editContext.IsTypingText)
            return false;
        if (CanvasArrowPan.ScreenStep(e.Key, e.KeyModifiers) is not { } step) return false;

        // World Y is screen-sense here (down is +), so both components carry straight through.
        _panX += step.Dx / _zoom;
        _panY += step.Dy / _zoom;
        InvalidateVisual();
        RaiseViewportChanged();
        return true;
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
        RaiseViewportChanged();
    }

    private void OnPointerWheel(object? _, PointerWheelEventArgs e)
    {
        // A preview's keys (Z, F) are its own only while it has focus; scrolling it is using it, as a press is.
        if (_editContext is null) Focus();
        ZoomAtPoint(e.GetPosition(this), e.Delta.Y);
        e.Handled = true;
    }

    // ── Palette DnD drop target ───────────────────────────────────────────────

    /// <summary>
    /// The rotation every DRAG-AND-DROP placement lands with, ghost and commit alike.
    ///
    /// <para>Unrotated, always — never the schematic's last-used placement rotation. Arming a
    /// placement from the palette starts at R0 (<c>PlacementService</c> arms every entry that way),
    /// so honouring the sticky rotation here made the same tile place differently depending on
    /// whether it was clicked or dragged, with nothing on screen to explain the difference. A drag
    /// also has no way to rotate mid-gesture — the keyboard does not route during a drag — so the
    /// carried-over angle could only ever be a surprise from an earlier edit.</para>
    /// </summary>
    private const SymbolRotation DropRotation = SymbolRotation.R0;

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
            var (ghostPrims, ghostPins) = p.Vendor is { } vendor
                ? VendorGhostSymbol(vendor, _editContext.KitWorkspaceRoot)
                : ResolveGhostSymbol(p.CellDir);

            _editContext.Overlay = _editContext.Overlay with
            {
                Ghost = new PlacementGhost(sx, sy, p.Kind, DropRotation, false,
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

    /// <summary>A dragged vendor part's ghost: its glyph (the built-in C or L), or the unconfigured SPICE
    /// box for a part with none — the same choice the click's ghost makes.</summary>
    private static (IReadOnlyList<SymbolPrimitive>?, IReadOnlyList<SymbolPin>?) VendorGhostSymbol(VendorPartRef vendor, string? workspaceRoot)
    {
        var glyph = VendorPartPlacement.GhostSymbol(vendor, workspaceRoot);
        return (glyph.Primitives, glyph.Pins);
    }

    private void OnPaletteDragLeave(object? sender, DragEventArgs e)
    {
        if (_editContext is not null)
            _editContext.Overlay = _editContext.Overlay with { Ghost = null };
        InvalidateVisual();
    }

    private void OnPaletteDrop(object? sender, DragEventArgs e)
    {
        TakeKeyboardFocus();
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
        var rotation = DropRotation;

        // A kit part places as the cell its symbol was installed into — the SAME path the
        // click-to-arm gesture takes, so dragging and clicking a tile can never disagree.
        if (payload.CellDir is { Length: > 0 } cellDir)
            _ = _editContext.CommitCellPlacementAsync(cellDir, wx, wy, rotation);
        else if (payload.Vendor is { } vendor)
            _editContext.CommitVendorPlacement(vendor, rotation, wx, wy);
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
            var rotation = DropRotation;

            PlacementGhost ghost;
            var schDir = _editContext.EditModel.SchematicDirectory;
            if (schDir is not null)
            {
                try
                {
                    var cellRef    = ExternalCellRef.MakeCellRef(schDir, payload.CellAbsPath);
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
        TakeKeyboardFocus();
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
        var rotation = DropRotation;

        await _editContext.CommitCellPlacementAsync(payload.CellAbsPath, wx, wy, rotation);
        // Again after the await: placing a cell with no symbol yet asks whether to generate one, and
        // the dialog takes focus on its way in. Without this the prompt hands it back to nobody.
        TakeKeyboardFocus();
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
        TakeKeyboardFocus();
        DropDiagnostics.Dump("SchematicCanvas.Drop", e);
        var path = TryExtractImagePath(e);
        if (path is null || _editContext is null) return;
        var pos = e.GetPosition(this);
        _editContext.DropBitmap(path, ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y));
        e.Handled = true;
        InvalidateVisual();
    }

    // ── Parts-table file DnD ──────────────────────────────────────────────────
    // A .csv (or a tab-separated .tsv/.txt) dropped on the sheet places the resistors, capacitors
    // and inductors it lists, at the drop point — the same reading a pasted table gets. A file that
    // turns out not to be a parts table is reported and nothing is placed.

    private void OnPartsTableDragOver(object? _, DragEventArgs e)
    {
        if (e.Handled) return;
        if (TryExtractPath(e, IsPartsTableExtension) is not null)
        { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
    }

    private void OnPartsTableDrop(object? _, DragEventArgs e)
    {
        // First, before any early return: the drop finished a gesture on this canvas whatever it
        // carried (DropTakesKeyboardFocusTests), as every other drop handler here does.
        TakeKeyboardFocus();
        if (e.Handled) return;
        var path = TryExtractPath(e, IsPartsTableExtension);
        if (path is null || _editContext is null) return;
        e.Handled = true;

        string? text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _editContext.MessageSink?.Warning($"{Path.GetFileName(path)} could not be read: {ex.Message}");
            return;
        }

        var pos = e.GetPosition(this);
        if (!_editContext.PlaceBomTable(text, Path.GetFileName(path),
                                        (ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y))))
            _editContext.MessageSink?.Warning(
                $"{Path.GetFileName(path)} is not a parts table circuitRF could read — it needs a " +
                "reference column and a type, value or case column.", path);
        InvalidateVisual();
    }

    // ── Schematic design file DnD (brief-schimp-5 R-si5-3, overview D10) ─────────────────────
    // The drag-over accepts by extension, which is cheap; the drop classifies by CONTENT, through the workspace, which
    // reads and builds off the UI thread with Esc to cancel and adds the design as one undo step. A file that classifies
    // as nothing is one Messages error and nothing is placed.

    private void OnSchematicFileDragOver(object? _, DragEventArgs e)
    {
        if (e.Handled) return;
        if (TryExtractPath(e, SchematicImport.HasSchematicExtension) is not null)
        { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
    }

    private void OnSchematicFileDrop(object? _, DragEventArgs e)
    {
        TakeKeyboardFocus();
        if (e.Handled) return;
        var path = TryExtractPath(e, SchematicImport.HasSchematicExtension);
        if (path is null || _editContext is null) return;
        e.Handled = true;
        var pos = e.GetPosition(this);
        if (DataContext is SchematicDocument { Hierarchy: ISchematicFileDropHost host } doc)
            _ = host.BuildSchematicFileIntoAsync(doc, path, (ScreenToWorldX(pos.X), ScreenToWorldY(pos.Y)));
        else
            _editContext.MessageSink?.Error(
                $"{Path.GetFileName(path)} was not built: open the schematic in a workspace to build a design into it.", path);
    }

    private static bool IsPartsTableExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".tsv", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryExtractPath(DragEventArgs e, Func<string, bool> accept)
    {
        foreach (var item in e.DataTransfer.Items)
        {
            string? path = item.TryGetRaw(DataFormat.File) switch
            {
                IStorageItem single             => single.Path?.LocalPath,
                IEnumerable<IStorageItem> files => files.FirstOrDefault()?.Path?.LocalPath,
                string s                        => s,
                _                               => null,
            };
            if (path is not null && accept(path)) return path;
        }
        return null;
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
        private readonly bool                    _excludeGrid;

        public SchematicDrawOperation(
            Rect bounds, SchematicModel? model, SchematicSpatialIndex? index,
            double panX, double panY, double zoom,
            SchematicRenderTheme theme, SchematicOverlay? overlay, bool excludeGrid)
        {
            _bounds    = bounds; _model = model; _index = index;
            _panX = panX; _panY = panY; _zoom = zoom;
            _theme = theme; _overlay = overlay; _excludeGrid = excludeGrid;
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
                _overlay, excludeGrid: _excludeGrid);
        }

        public void Dispose() { }
    }
}
