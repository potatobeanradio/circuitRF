using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.Views.Dialogs;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// Custom Avalonia control that renders a <see cref="LayoutView"/> via <see cref="LayoutRenderer"/>
/// and dispatches pointer/keyboard/text input to <see cref="LayoutEditorViewModel"/>'s drawing-tool
/// state machine (docs/sonnet-briefs/brief-L1b-drawing-tools.md — fills L1a's marked seam). Clones
/// <c>SymbolEditorCanvas</c>'s shape: viewport state (<c>_panX</c>/<c>_panY</c>/<c>_zoom</c>) is
/// owned by the canvas, mirrored out via <see cref="ViewportChanged"/> for readouts (metadata bar,
/// rulers); a single <see cref="ViewModel"/> DirectProperty (not separate Model/Technology
/// properties, as in L1a) so the canvas can dispatch to it directly. Layout is Y-UP (see
/// <see cref="LayoutViewport"/>), unlike the schematic/symbol canvases.
/// </summary>
public sealed class LayoutCanvas : Control
{
    // ── DirectProperty: ViewModel ─────────────────────────────────────────────

    public static readonly DirectProperty<LayoutCanvas, LayoutEditorViewModel?> ViewModelProperty =
        AvaloniaProperty.RegisterDirect<LayoutCanvas, LayoutEditorViewModel?>(
            nameof(ViewModel), o => o.ViewModel, (o, v) => o.ViewModel = v);

    private LayoutEditorViewModel? _viewModel;

    /// <summary>L2c §3 (docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md) — one path cache per
    /// bound document, for exactly as long as this canvas is showing it. A fresh instance every time
    /// <see cref="ViewModel"/> changes: cache entries are keyed by shape INDEX, which is meaningful only
    /// within the ONE <see cref="LayoutView"/> that index came from — reusing a cache across two
    /// different models would silently draw the wrong geometry at a reused index. Invalidated
    /// incrementally by <see cref="OnModelChanged"/> (below), riding the same <see cref="LayoutChangeInfo"/>
    /// notification the L2b spatial index already consumes — no second notification path.</summary>
    private LayoutPathCache? _pathCache;

    public LayoutEditorViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged -= OnVmPropertyChanged;
                _viewModel.Model.Changed   -= OnModelChanged;
            }

            SetAndRaise(ViewModelProperty, ref _viewModel, value);
            _pathCache = _viewModel is not null ? new LayoutPathCache() : null;

            if (_viewModel is not null)
            {
                _viewModel.PropertyChanged += OnVmPropertyChanged;
                _viewModel.Model.Changed   += OnModelChanged;   // Model itself never changes post-construction
                _needsInitialFit = true;
            }
            UpdateCursor();
            InvalidateVisual();
        }
    }

    private void OnModelChanged(object? sender, LayoutChangeInfo e)
    {
        _pathCache?.Apply(e);
        InvalidateVisual();
    }

    // ── Canvas overlay (wbond.md WB23) ────────────────────────────────────────

    public static readonly DirectProperty<LayoutCanvas, ILayoutCanvasOverlay?> CanvasOverlayProperty =
        AvaloniaProperty.RegisterDirect<LayoutCanvas, ILayoutCanvasOverlay?>(
            nameof(CanvasOverlay), o => o.CanvasOverlay, (o, v) => o.CanvasOverlay = v);

    private ILayoutCanvasOverlay? _canvasOverlay;

    /// <summary>
    /// Something drawn over this canvas that is not part of the layout — the wBond wire overlay is the
    /// first. Given first refusal on pointer and key input; see <see cref="ILayoutCanvasOverlay"/>.
    /// Null (the default) leaves every existing behaviour of this control bit-for-bit unchanged.
    /// </summary>
    public ILayoutCanvasOverlay? CanvasOverlay
    {
        get => _canvasOverlay;
        set { SetAndRaise(CanvasOverlayProperty, ref _canvasOverlay, value); InvalidateVisual(); }
    }

    /// <summary>
    /// Repaints because the OVERLAY changed. Deliberately does not touch <see cref="_pathCache"/>:
    /// the layout's geometry has not moved, and invalidating its cached paths is precisely the
    /// "cheap overlay becomes a 500k-shape redraw" failure WB17 exists to prevent.
    /// </summary>
    public void InvalidateOverlay() => InvalidateVisual();

    /// <summary>
    /// Hands the overlay's own snap answer to the layout editor's marker slot, so ONE glyph mechanism
    /// serves both gestures.
    ///
    /// <para>Called after every press, move and release the overlay CONSUMED — those never reach
    /// <c>LayoutEditorViewModel.OnPointerMoved</c>, which is the only other thing that ever refreshes
    /// or clears that marker. Without it the last hover's glyph is simply left on screen for the
    /// duration of the gesture, sitting on a vertex the wire has since been dragged away from, and a
    /// wire being DRAWN shows no glyph at all despite its feet being snapped every frame.</para>
    /// </summary>
    private void PushOverlaySnapMarker() =>
        _viewModel?.SetOverlaySnapMarker(_canvasOverlay?.SnapMarker);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LayoutEditorViewModel.ActiveTool))
            UpdateCursor();
        InvalidateVisual();
    }

    // ── Viewport state — owned by the canvas, mirrored out for readouts ────────

    private double _panX;
    private double _panY;
    private double _zoom = 1.0;

    private const double ZoomFactor = 1.15;
    private const double MinZoom    = 1e-9;
    private const double MaxZoom    = 1e5;

    /// <summary>Screen-pixel hit/cycling tolerance for the Select tool (§6.2/§6.3) — converted to
    /// DBU HERE, from the CURRENT zoom, on every call. Never cached and never derived from
    /// <c>SnapDbu</c>: across a 1000x zoom range the same 4 px spans three orders of magnitude of
    /// DBU, and a tolerance that is right in DBU and wrong in pixels is exactly the class of bug
    /// the L1b/L1-fix round already made once (see the brief's "Read first" section).</summary>
    private const double SelectHitTolerancePixels = 4.0;

    private long HitTolDbu() => _zoom > 0 ? (long)Math.Round(SelectHitTolerancePixels / _zoom) : 0;

    /// <summary>L1i: one device pixel's world-space size in DBU at the CURRENT zoom — computed fresh
    /// per call, same discipline as <see cref="HitTolDbu"/> above, and NOT derived from it (that
    /// constant is a several-pixel hit-test tolerance, a different concern from "did the marquee
    /// rectangle move at all"). Used only to gate the marquee-preview recompute.</summary>
    private long OnePixelDbu() => _zoom > 0 ? Math.Max(1, (long)Math.Round(1.0 / _zoom)) : 1;

    /// <summary>Geometry snap's own screen-pixel tolerance (docs/sonnet-briefs/
    /// brief-snap-distance-and-geometry-snap.md R-snp-15) — a DELIBERATELY separate constant from
    /// <see cref="SelectHitTolerancePixels"/>, converted to DBU HERE from the CURRENT zoom on every
    /// call, same discipline as <see cref="HitTolDbu"/>. Slightly larger than the plain hit-test
    /// tolerance so a marker is discoverable a little before the cursor is exactly on the feature.</summary>
    private const double SnapHitTolerancePixels = 8.0;

    private long SnapTolDbu() => _zoom > 0 ? (long)Math.Round(SnapHitTolerancePixels / _zoom) : 0;

    /// <summary>GRIP-LOCK's own radius (docs/design/pcell-parameter-handles.md R-pch-12) — the
    /// distance within which Alt+press claims the nearest PCell parameter grip. Deliberately much
    /// larger than <see cref="SelectHitTolerancePixels"/>: the whole point of holding Alt is that the
    /// grip stops being a four-pixel target you have to hit exactly, and at a zoom where the grip and
    /// the instance corner are the same pixel there is no aim precise enough to separate them.
    /// <para/>
    /// BOUNDED rather than "nearest grip anywhere" (owner's call): an unbounded radius means an
    /// Alt+press well away from the cell yanks a grip the user was not looking at, and a wide cell is
    /// exactly where that is easiest to do. Outside this radius the press does nothing at all — which
    /// is the promise grip-lock makes, not a failure of it.
    /// <para/>
    /// Converted to DBU HERE, from the CURRENT zoom, on every call — same discipline as the two
    /// tolerances above, and never derived from either of them.</summary>
    private const double GripLockHitTolerancePixels = 24.0;

    private long GripLockTolDbu() => _zoom > 0 ? (long)Math.Round(GripLockHitTolerancePixels / _zoom) : 0;

    /// <summary>R-bmp-4: the current viewport's world-space width in DBU — a newly-placed bitmap's
    /// long edge is sized as ~25% of this, computed fresh per placement (never cached, DBU are
    /// nanometres so a stale width would be meaningless after any zoom/pan).</summary>
    private double ViewportWidthDbu() => CurrentViewport.VisibleMaxX - CurrentViewport.VisibleMinX;

    public double CurrentZoom => _zoom;
    public double CurrentPanX => _panX;
    public double CurrentPanY => _panY;

    public (double X, double Y) WorldToScreen(double wx, double wy) => (CurrentViewport.WorldToScreenX(wx), CurrentViewport.WorldToScreenY(wy));
    public (double X, double Y) ScreenToWorld(double sx, double sy) => (CurrentViewport.ScreenToWorldX(sx), CurrentViewport.ScreenToWorldY(sy));

    /// <summary>The canvas's current pan/zoom, snapshotted as a value — L3b's per-nav-frame viewport
    /// capture reads this (docs/sonnet-briefs/brief-L3b-hierarchy-navigation.md §1) since pan/zoom is
    /// canvas-owned, not VM-owned (see <see cref="LayoutDocument"/>'s <c>NavFrame</c> doc comment).</summary>
    public LayoutViewport CurrentViewport => new(_panX, _panY, _zoom, Bounds.Width, Bounds.Height);

    /// <summary>Directly applies a previously-captured viewport (L3b pop-out/push-in restore) —
    /// suppresses the initial-fit-on-bind that would otherwise immediately override it on the next
    /// layout pass (<see cref="OnLayoutUpdated"/>'s <c>_needsInitialFit</c> check).</summary>
    public void SetViewport(LayoutViewport vp)
    {
        _panX = vp.PanX; _panY = vp.PanY; _zoom = vp.Zoom;
        _needsInitialFit = false;
        RaiseViewportChanged();
        InvalidateVisual();
    }

    /// <summary>Fired whenever pan/zoom changes — the view uses this to refresh rulers and the metadata bar.</summary>
    public event EventHandler? ViewportChanged;

    /// <summary>Fired on pointer move (world coordinate) and on pointer exit (null) — drives the
    /// ruler cursor indicator and the metadata-bar X/Y readout (§1 R6).</summary>
    public event EventHandler<(double X, double Y)?>? CursorWorldChanged;

    /// <summary>
    /// Fired after each frame with any <see cref="LayerKey"/>s a resolved <see cref="Technology"/>
    /// did not define. May be empty. The view/view-model dedupes against what has already been
    /// warned about for this document and posts to Messages "once per layer per load" — the canvas
    /// itself never posts.
    /// </summary>
    public event Action<IReadOnlyList<LayerKey>>? FrameUnknownLayers;

    /// <summary>L3a — mirrors <see cref="FrameUnknownLayers"/> for missing/broken instance cell
    /// references (R-L3a-1: "report once per distinct CellRef per load — not once per placement").</summary>
    public event Action<IReadOnlyList<string>>? FrameMissingInstanceCellRefs;

    /// <summary>L3b — a resolved instance was double-clicked with the Select tool active
    /// (docs/sonnet-briefs/brief-L3b-hierarchy-navigation.md §1: "Push in: double-click a selected
    /// instance"). Mirrors <c>SchematicCanvas.ComponentDoubleTapped</c>'s shape: the canvas only
    /// hit-tests and reports WHICH instance; the view decides whether it's push-in-able and performs
    /// the navigation.</summary>
    public event EventHandler<LayoutInstance>? InstanceDoubleTapped;

    // ── Clipboard (L1f) — handled async by code-behind, mirroring SchematicCanvas ───────────────

    public event EventHandler? ClipboardCopyRequested;
    public event EventHandler? ClipboardCutRequested;
    public event EventHandler? ClipboardPasteRequested;
    public event EventHandler? ClipboardPasteInPlaceRequested;
    public event EventHandler? DuplicateRequested;

    // ── Pan (middle-mouse always; Space-drag as an alternative) ─────────────────

    private bool   _isPanning;
    private Point  _panDragStartScreen;
    private double _panDragStartPanX;
    private double _panDragStartPanY;
    private bool   _spaceHeld;

    // ── Render state ─────────────────────────────────────────────────────────

    private ColorTheme _activeTheme     = ColorTheme.BuiltIn;
    private bool        _needsInitialFit = true;

    public LayoutCanvas()
    {
        Focusable = true;
        PointerPressed      += OnPointerPressed;
        PointerMoved        += OnPointerMoved;
        PointerReleased     += OnPointerReleased;
        PointerExited       += OnPointerExited;
        PointerWheelChanged += OnPointerWheel;
        PointerCaptureLost  += OnPointerCaptureLost;
        DoubleTapped        += OnDoubleTapped;
        KeyDown             += OnKeyDown;
        KeyUp               += OnKeyUp;
        LostFocus           += OnCanvasLostFocus;
        TextInput           += OnTextInput;
        ((IResourceHost)this).ResourcesChanged += (_, _) => InvalidateVisual();
        LayoutUpdated += OnLayoutUpdated;

        // Image file drop target (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md) —
        // mirrors SymbolEditorCanvas's image-file DnD exactly; only the placement SIZE rule differs
        // (R-bmp-4: viewport-relative, not a fixed local-unit constant).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnImageFileDragOver);
        AddHandler(DragDrop.DropEvent,     OnImageFileDrop);

        // Cell drop target — drag a cell from the project tree onto the layout to place an instance
        // (brief-L3a-followups.md §4/R-fix-5/R-fix-6). A SEPARATE handler pair, coexisting with the
        // image-file one above — mirrors SchematicCanvas's own "separate handler pairs per payload
        // kind, not one handler that branches" convention, so a cell drag and an image-file drag can
        // never be confused with each other.
        AddHandler(DragDrop.DragOverEvent,  OnCellDragOver);
        AddHandler(DragDrop.DropEvent,      OnCellDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnCellDragLeave);

        // Palette drop target — drag a PCell-eligible component straight from the Library Palette
        // onto a layout (docs/sonnet-briefs/brief-L5-schematic-to-layout.md §3). A THIRD handler pair,
        // reusing SchematicCanvas's own PaletteDragPayload verbatim (R-L5-6) rather than inventing a
        // new payload kind.
        AddHandler(DragDrop.DragOverEvent,  OnPaletteDragOver);
        AddHandler(DragDrop.DropEvent,      OnPaletteDrop);
        AddHandler(DragDrop.DragLeaveEvent, OnPaletteDragLeave);
    }

    // Fits exactly once per bound ViewModel, as soon as Bounds becomes valid — for a layout that
    // loads with content this frames it (LayoutViewport.ZoomToFit); for a brand-new EMPTY layout it
    // still must run so the canvas starts at a physically sane, immediately-drawable default
    // (LayoutViewport.Default) instead of the raw zoom=1.0 field default (docs/sonnet-briefs/
    // brief-L1-fix-clear-and-default-zoom.md, Bug 2) — an empty layout is not exempt from needing a
    // sensible viewport, it is the case that most needed one.
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_needsInitialFit && _viewModel is not null && Bounds.Width > 1 && Bounds.Height > 1)
        {
            _needsInitialFit = false;
            ZoomToFitInternal();
            RaiseViewportChanged();
        }
    }

    // ── Double-tap → push into cell (L3b) ───────────────────────────────────────

    private void OnDoubleTapped(object? _, TappedEventArgs e)
    {
        if (_viewModel is null || _viewModel.ActiveTool != LayoutEditorViewModel.Tool.Select) return;

        var pos   = e.GetPosition(this);
        double wx = CurrentViewport.ScreenToWorldX(pos.X);
        double wy = CurrentViewport.ScreenToWorldY(pos.Y);
        long tolDbu = HitTolDbu();

        // A RULER first, and above all geometry — the same precedence the single-click path uses,
        // since rulers paint above everything. Double-clicking one opens its properties, which is the
        // gesture the rest of this editor already spends on "show me what this is": a PCell instance's
        // parameter popup is the same shape, and it routes through the SAME dialog the right-click
        // Edit Ruler… item opens rather than a second one.
        if (_viewModel.FindRulerForContextMenu(wx, wy, tolDbu) is { } rulerIndex)
        {
            _ = ShowRulerEditDialogAsync(rulerIndex);
            e.Handled = true;
            return;
        }

        var hits = LayoutHitTest.HitInstanceStack(
            _viewModel.Model, _viewModel.Technology, _viewModel.InstanceBaseDir,
            (long)Math.Round(wx), (long)Math.Round(wy), tolDbu);
        if (hits.Count == 0) return;

        InstanceDoubleTapped?.Invoke(this, _viewModel.Model.Instances[hits[0]]);
    }

    // ── Visual tree ───────────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeService.ThemeChanged += OnThemeChanged;
        _activeTheme = ThemeService.Active;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _activeTheme = ThemeService.Active;
        InvalidateVisual();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        var theme = LayoutRenderTheme.FromTheme(_activeTheme, variant);
        var vp = CurrentViewport;
        var opts = new LayoutRenderOptions
        {
            Theme = theme, ShowGrid = true, Overlay = _viewModel?.Overlay, PathCache = _pathCache,
            BaseDir = _viewModel?.InstanceBaseDir, ShowPCellPins = _viewModel?.ShowPCellPins ?? true,
            ShowEmMesh = _viewModel?.ShowEmMesh ?? false, EmMesh = _viewModel?.EmMeshReport,
            ShowPlanarMesh = _viewModel?.ShowPlanarMesh ?? false, PlanarMesh = _viewModel?.PlanarMeshReport,
            PlanarCurrentDensity = _viewModel?.PlanarCurrentDensity,
            PlanarPorts = _viewModel?.PlanarReferencePlanes ?? [],
            InternalPortMarks = _viewModel?.InternalPortMarks ?? [],
        };

        context.Custom(new LayoutDrawOperation(
            new Rect(Bounds.Size), _viewModel?.Model, _viewModel?.Technology, vp, opts, _canvasOverlay,
            r => Dispatcher.UIThread.Post(() =>
            {
                FrameUnknownLayers?.Invoke(r.UnknownLayers);
                if (r.MissingInstanceCellRefs is { Count: > 0 } missing)
                    FrameMissingInstanceCellRefs?.Invoke(missing);
            })));
    }

    // ── Zoom commands ─────────────────────────────────────────────────────────

    public void ZoomToFit()
    {
        ZoomToFitInternal();
        RaiseViewportChanged();
    }

    private void ZoomToFitInternal()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;

        var bb = Bbox.Empty;
        if (_viewModel?.Model is { } model)
        {
            foreach (var shape in model.Shapes)
                bb = bb.Union(LayoutGeometry.BboxOf(shape));
            // Owner report (2026-07-29): Zoom to Fit ignored every placed instance — including a
            // PCell instance, which is an ordinary LayoutInstance pointing at a generated cell folder
            // (see this file's own architecture note on PCells) — so a layout consisting solely of
            // PCells (or one whose PCells extend past its raw shapes) zoomed to an empty/undersized
            // extent. CellHierarchy.InstanceBbox is the SAME resolved, array-expanded, recursive bbox
            // the marquee/spatial-index and Select-All paths already use for an instance — reused here
            // rather than a second bbox notion.
            foreach (var inst in model.Instances)
                bb = bb.Union(CellHierarchy.InstanceBbox(inst, _viewModel.InstanceBaseDir));

            // docs/design/layout-view.md §9B: a ruler is document content and must be framed like any
            // other. Measured through the RENDERER (LayoutRenderer.MeasureRulerWorldBbox), never by a
            // second footprint derived here — the readout's extent is real font metrics, and a Fixed
            // ruler's is a function of the zoom. The CURRENT zoom is the right one to ask with: this
            // frames what is on screen now, and a fit that changed the zoom to change the answer to
            // change the zoom would not converge.
            foreach (var ruler in model.Rulers)
                bb = bb.Union(LayoutRenderer.MeasureRulerWorldBbox(
                    ruler, model.DisplayUnit, model.DbuPerMicron, _zoom));
        }

        // Whatever rides ON the canvas counts too. A wBond document's wires are an overlay by design
        // (WB23) and are in neither Shapes nor Instances, so without this a wBond on an empty scratch
        // layout fitted to an EMPTY extent and framed nothing the user could see.
        if (_canvasOverlay is { } overlay) bb = bb.Union(overlay.ContentBounds());

        var vp = bb.IsEmpty
            ? LayoutViewport.Default(Bounds.Width, Bounds.Height, _viewModel?.Model.SnapDbu ?? 0, _viewModel?.Model.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron)
            : LayoutViewport.ZoomToFit(bb, Bounds.Width, Bounds.Height);
        _panX = vp.PanX; _panY = vp.PanY; _zoom = vp.Zoom;
    }

    /// <summary>
    /// L5b (§9A.1's click-to-zoom): brings one region on screen, centred, with room around it so the
    /// user sees what the violation is NEXT to — a marker filling the whole viewport shows the defect
    /// and hides its context, which is the thing you actually need in order to fix it.
    ///
    /// <para>A degenerate region (a violation whose marker is a hairline, which is the common case
    /// for spacing) is grown to a minimum span first, so zooming to it lands at a usable
    /// magnification instead of clamping against <c>MaxZoom</c> at some arbitrary depth.</para>
    /// </summary>
    public void ZoomToRegion(Bbox region)
    {
        if (region.IsEmpty || Bounds.Width < 1 || Bounds.Height < 1) return;

        long minSpan = Math.Max(1, _viewModel?.Model.SnapDbu * ZoomToRegionMinSnapSteps ?? 1);
        long w = region.MaxX - region.MinX;
        long h = region.MaxY - region.MinY;

        long padX = Math.Max(0, (minSpan - w) / 2);
        long padY = Math.Max(0, (minSpan - h) / 2);

        long marginX = (long)Math.Round((w + 2 * padX) * ZoomToRegionMargin);
        long marginY = (long)Math.Round((h + 2 * padY) * ZoomToRegionMargin);

        var padded = new Bbox(
            region.MinX - padX - marginX, region.MinY - padY - marginY,
            region.MaxX + padX + marginX, region.MaxY + padY + marginY);

        var vp = LayoutViewport.ZoomToFit(padded, Bounds.Width, Bounds.Height);
        _panX = vp.PanX; _panY = vp.PanY; _zoom = Math.Clamp(vp.Zoom, MinZoom, MaxZoom);
        RaiseViewportChanged();
        InvalidateVisual();
    }

    /// <summary>A hairline violation is grown to at least this many snap steps before zooming.</summary>
    private const int ZoomToRegionMinSnapSteps = 20;

    /// <summary>Context around the violation, as a fraction of its own (padded) span per side.</summary>
    private const double ZoomToRegionMargin = 1.5;

    public void ZoomIn()  => ZoomAtCenter(_zoom * ZoomFactor);
    public void ZoomOut() => ZoomAtCenter(_zoom / ZoomFactor);

    /// <summary>1 device pixel per one tick of the document's display unit (e.g. 1 px = 1 mil on a
    /// PCB layout, 1 px = 1 µm on an MMIC layout) — a stable, physically-meaningful "actual size".</summary>
    public void Zoom1To1()
    {
        if (_viewModel?.Model is not { } model) return;
        long dbuPerUnit = LayoutUnits.ToDbu(1m, model.DisplayUnit, model.DbuPerMicron);
        if (dbuPerUnit <= 0) dbuPerUnit = 1;
        ZoomAtCenter(1.0 / dbuPerUnit);
    }

    private void ZoomAtCenter(double newZoom)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var vp = CurrentViewport.WithZoomAnchoredAt(newZoom, Bounds.Width / 2.0, Bounds.Height / 2.0);
        _panX = vp.PanX; _panY = vp.PanY; _zoom = vp.Zoom;
        RaiseViewportChanged();
    }

    private void RaiseViewportChanged()
    {
        ViewportChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

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
        var pos = e.GetPosition(this);

        if (props.IsMiddleButtonPressed || (props.IsLeftButtonPressed && _spaceHeld))
        {
            _isPanning = true;
            _panDragStartScreen = pos;
            _panDragStartPanX = _panX;
            _panDragStartPanY = _panY;
            e.Pointer.Capture(this);
            // Through SetCursor, NOT a bare `Cursor = new Cursor(...)`. Owner report, 2026-08-27: the
            // pointer sometimes became a hand and was hard to get back to an arrow.
            // Assigning Cursor directly left _currentCursorType saying "default" while the real cursor
            // was a hand — so the release ran UpdateCursor, reached its own "already default, nothing
            // to do" early-out, and returned WITHOUT clearing it. On the Select tool with nothing
            // hovered that is every middle-drag pan, not an occasional one; it only looked intermittent
            // because any state that sets a real cursor (hovering a PCell grip, holding Alt, arming a
            // drawing tool) makes the memo non-null again and the next release then does clear it,
            // which is also exactly why it was "hard" rather than impossible to get back.
            SetCursor(StandardCursorType.Hand);
            return;
        }

        if (props.IsRightButtonPressed && _viewModel is not null)
        {
            var (wx, wy) = ScreenToWorld(pos.X, pos.Y);

            // macOS reports "Control + left-click" as a secondary (right) button press at the OS
            // level -- the classic one-button-mouse "Control-click = right-click" convention. Without
            // this check, holding Control to insert a vertex (L1d gesture) on macOS would silently pop
            // the edge-conversion context menu instead. Route that case through the ordinary press
            // path (which already handles Ctrl/Cmd+click-insert) instead; a genuine right-click with
            // no Control held still opens the context menu.
            if ((e.KeyModifiers & KeyModifiers.Control) != 0)
            {
                ContextMenuTarget = null; // L1-fix: no pending target -> the Opening handler cancels
                _viewModel.OnPointerPressed(wx, wy, e.KeyModifiers, e.ClickCount, HitTolDbu(), 0, SnapTolDbu(),
                                        GripLockTolDbu());
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            // L1-fix (brief-L1-fix-context-menu-stacking.md): only RECORD the click here — mirrors
            // SymbolEditorCanvas's right-click branch (see BitmapContextPrimIdx there) exactly. The
            // single ContextMenu instance is declared once in LayoutEditorView.axaml on this control;
            // Avalonia opens it itself and raises Opening, which calls ConsumeContextMenuTarget and
            // rebuilds ItemsSource fresh. Deliberately NOT e.Handled = true here — that would risk
            // suppressing Avalonia's own right-click-opens-ContextMenu gesture recognition, the same
            // reason the reference implementation leaves this branch unhandled.
            ContextMenuTarget = (wx, wy);
            return;
        }

        if (props.IsLeftButtonPressed && _viewModel is not null)
        {
            var (wx, wy) = ScreenToWorld(pos.X, pos.Y);

            // The overlay is asked first and only consumes what it actually hit — pan and zoom are
            // already handled above, and anything it declines reaches the layout tools untouched.
            if (_canvasOverlay?.OnPointerPressed(
                    (long)Math.Round(wx), (long)Math.Round(wy), HitTolDbu(), e.KeyModifiers, e.ClickCount) == true)
            {
                // CAPTURE for the whole gesture (owner, 2026-08-16: "when I drag many wires around in
                // the layout view, the dragging appears to glitch").
                //
                // Without it, a drag that leaves this control — over a ruler strip, the inductance
                // panel, the splitter, another window — simply stops receiving moves: the wires
                // freeze under a still-moving cursor, and the release is delivered somewhere else so
                // the gesture is only unwound on the next move that happens to come back. The
                // slower the drag, the easier it is to outrun the canvas, which is why it shows up
                // on MANY wires and not on one.
                e.Pointer.Capture(this);

                // The overlay owns the gesture from here, so the layout editor will not run its own
                // snap recompute again until the gesture ends — its marker has to come from the
                // overlay or it freezes where the last hover left it. See ILayoutCanvasOverlay.SnapMarker.
                PushOverlaySnapMarker();

                // A press that landed on nothing means "deselect" whichever selection the user was
                // holding — including the layout's, which the overlay cannot reach.
                if (_canvasOverlay.ConsumedPressWasEmptySpace)
                    _viewModel.DeselectAllCommand.Execute(null);
                else
                {
                    // …and a press it DID hit arms the other half of a mixed selection, so a pad
                    // selected beside a bond wire comes along with it. See
                    // ILayoutCanvasOverlay.BeginCompanionMove — and note the gate: a press that
                    // resolved a NEW wire selection means "just this wire", so the layout's own
                    // selection stays put rather than being dragged by a click that never touched it.
                    _viewModel.CompanionPressResolvedNewSelection = _canvasOverlay.LastPressResolvedNewSelection;
                    _viewModel.BeginCompanionMove();
                }

                InvalidateVisual();
                e.Handled = true;
                return;
            }

            _viewModel.OnPointerPressed(wx, wy, e.KeyModifiers, e.ClickCount, HitTolDbu(), _zoom, SnapTolDbu(),
                                        GripLockTolDbu());

            // The mirror of the branch above: the LAYOUT owns this press, so the overlay's own
            // selection follows it — unless that press resolved a new one, in which case it means
            // "just this" and a wire selected earlier must not come along.
            if (_canvasOverlay is { } companionOverlay)
            {
                companionOverlay.CompanionPressResolvedNewSelection = _viewModel.LastPressResolvedNewSelection;
                companionOverlay.BeginCompanionMove();
            }
            InvalidateVisual();
        }
    }

    // ── Bitmap image-file DnD + Insert Bitmap (docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md) ──

    private void OnImageFileDragOver(object? _, DragEventArgs e)
    {
        if (TryExtractImagePath(e) is not null) { e.DragEffects = DragDropEffects.Copy; e.Handled = true; }
        else e.DragEffects = DragDropEffects.None;
    }

    private void OnImageFileDrop(object? _, DragEventArgs e)
    {
        TakeKeyboardFocus();
        var path = TryExtractImagePath(e);
        if (path is null || _viewModel is null) return;
        var pos = e.GetPosition(this);
        var (wx, wy) = ScreenToWorld(pos.X, pos.Y);
        _viewModel.DropBitmap(path, wx, wy, ViewportWidthDbu());
        e.Handled = true;
        InvalidateVisual();
    }

    // Mirrors SymbolEditorCanvas.TryExtractImagePath — the OS surfaces a dropped file under
    // DataFormat.File; the payload TYPE varies by platform (a single IStorageItem on macOS,
    // IEnumerable<IStorageItem> elsewhere), handled defensively alongside a bare path string.
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
                _                                => null,
            };
            if (path is not null) return path;
        }
        return null;
    }

    // ── Cell DnD drop target (brief-L3a-followups.md §4) — mirrors SchematicCanvas.OnCellDragOver/
    // OnCellDrop, with two substitutions per R-fix-5: resolves through CellLayoutResolver (via the VM's
    // own instance-ghost methods) instead of CellSymbolResolver, and snaps through the layout's own
    // SnapDbu instead of the schematic grid. ──────────────────────────────────────────────────────

    private static CellDragPayload? TryParseCellDragPayload(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.Text) is string text && CellDragPayload.TryParse(text, out var payload))
                return payload;
        return null;
    }

    private (long X, long Y) SnappedDropPoint(DragEventArgs e, LayoutEditorViewModel vm)
    {
        var pos = e.GetPosition(this);
        var (wx, wy) = ScreenToWorld(pos.X, pos.Y);
        return LayoutSnapping.SnapPoint(wx, wy, vm.Model.SnapDbu, suspend: false);
    }

    private void OnCellDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel is not { } vm) { e.DragEffects = DragDropEffects.None; return; }

        var payload = TryParseCellDragPayload(e);
        if (payload is null) { e.DragEffects = DragDropEffects.None; return; }

        // R-fix-1/R-fix-6: "exclude/refuse the parent cell only" — the ONE case obvious enough that a
        // "no" cursor here needs no further explanation. Every other cycle (a deeper A->B->A) is
        // deliberately accepted here and refused (with the path named) on drop instead.
        if (vm.WouldDragCellBeSelfReference(payload.CellAbsPath))
        {
            e.DragEffects = DragDropEffects.None;
            vm.CancelDragInstancePlacement();
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled     = true;

        string cellRef = RelativeCellRefForDrag(payload.CellAbsPath, vm);
        var (sx, sy) = SnappedDropPoint(e, vm);
        vm.UpdateDragInstanceGhost(cellRef, sx, sy);
        InvalidateVisual();
    }

    private void OnCellDrop(object? sender, DragEventArgs e)
    {
        TakeKeyboardFocus();
        // Clear the ghost before processing so it disappears regardless of outcome (mirrors
        // SchematicCanvas.OnCellDrop).
        _viewModel?.CancelDragInstancePlacement();

        if (_viewModel is not { } vm) return;
        var payload = TryParseCellDragPayload(e);
        if (payload is null) return;
        if (vm.WouldDragCellBeSelfReference(payload.CellAbsPath)) return; // DragOver already refused the cursor for this case

        string cellRef = RelativeCellRefForDrag(payload.CellAbsPath, vm);
        var (sx, sy) = SnappedDropPoint(e, vm);
        vm.CommitDragInstancePlacement(cellRef, sx, sy); // R-fix-6: refuses+reports a deeper cycle internally
        e.Handled = true;
        InvalidateVisual();
    }

    private void OnCellDragLeave(object? sender, DragEventArgs e)
    {
        _viewModel?.CancelDragInstancePlacement();
        InvalidateVisual();
    }

    // ── Palette DnD drop target (§3) — mirrors OnCellDragOver/OnCellDrop/OnCellDragLeave exactly,
    // substituting PaletteDragPayload + the PCell ghost VM methods for the cell-resolution ones. ────

    private static PaletteDragPayload? TryParsePaletteDragPayload(DragEventArgs e)
    {
        foreach (var item in e.DataTransfer.Items)
            if (item.TryGetRaw(DataFormat.Text) is string text && PaletteDragPayload.TryParse(text, out var payload))
                return payload;
        return null;
    }

    private void OnPaletteDragOver(object? sender, DragEventArgs e)
    {
        if (_viewModel is not { } vm) { e.DragEffects = DragDropEffects.None; return; }

        var payload = TryParsePaletteDragPayload(e);
        if (payload is null) { e.DragEffects = DragDropEffects.None; return; }

        // A tile that places a parametric cell by id — every cell a kit contributes. Checked first
        // because such a tile carries the placeholder SymbolKind every kit tile shares, which the
        // SymbolKind path below would (correctly) refuse.
        if (payload.PCellGeneratorId is { Length: > 0 } dragGen)
        {
            if (!vm.CanDropPCellGenerator(dragGen))
            {
                e.DragEffects = DragDropEffects.None;
                vm.CancelPaletteDragGhost();
                return;
            }

            e.DragEffects = DragDropEffects.Copy;
            e.Handled     = true;
            var (gx, gy) = SnappedDropPoint(e, vm);
            vm.UpdatePCellDragGhost(dragGen, gx, gy);
            InvalidateVisual();
            return;
        }

        // A wBond produces WIRES, not artwork (WB40b) — there is no generator to resolve and no ghost
        // to show, so it is answered before the generator test below, which would (correctly) refuse
        // it. See LayoutEditorViewModel.CommitWBondDrop.
        if (payload.Kind == SymbolKind.WBond)
        {
            e.DragEffects = CanDropWBondHere(vm) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            vm.CancelPaletteDragGhost();
            return;
        }

        // R-L5-8: only a component with a registered PCell generator is droppable — the cursor says
        // no before release for anything else (a Term, a Var, ...).
        if (!vm.CanDropPaletteComponent(payload.Kind, payload.PortCount))
        {
            e.DragEffects = DragDropEffects.None;
            vm.CancelPaletteDragGhost();
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled     = true;

        var (sx, sy) = SnappedDropPoint(e, vm);
        vm.UpdatePaletteDragGhost(payload.Kind, payload.PortCount, sx, sy);
        InvalidateVisual();
    }

    private void OnPaletteDrop(object? sender, DragEventArgs e)
    {
        TakeKeyboardFocus();
        _viewModel?.CancelPaletteDragGhost();

        if (_viewModel is not { } vm) return;
        var payload = TryParsePaletteDragPayload(e);
        if (payload is null) return;

        var (sx, sy) = SnappedDropPoint(e, vm);

        if (payload.PCellGeneratorId is { Length: > 0 } dropGen)
        {
            if (!vm.CanDropPCellGenerator(dropGen)) return;
            vm.CommitPCellDrop(dropGen, sx, sy);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (payload.Kind == SymbolKind.WBond)
        {
            if (!CanDropWBondHere(vm)) return;
            vm.CommitWBondDrop(sx, sy);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (!vm.CanDropPaletteComponent(payload.Kind, payload.PortCount)) return;
        vm.CommitPaletteDrop(payload.Kind, payload.PortCount, sx, sy);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Whether a wBond tile may be dropped on THIS canvas.
    ///
    /// <para>The view model's own answer, plus one question only the canvas can ask: <b>are the wires
    /// on screen this session's, or a HOST's?</b> The wBond editor hosts this canvas and puts its own
    /// document's wires over it (<c>LayoutEditorView.CanvasOverlay</c> outranks the frame's), so a
    /// drop there would attach a SECOND, invisible wire design to the reference layout — saved to disk
    /// and never drawn, because the host's overlay is the one that renders.</para>
    /// </summary>
    private bool CanDropWBondHere(LayoutEditorViewModel vm) =>
        vm.CanDropWBond()
        && (_canvasOverlay is null || ReferenceEquals(_canvasOverlay, vm.WireOverlay));

    private void OnPaletteDragLeave(object? sender, DragEventArgs e)
    {
        _viewModel?.CancelPaletteDragGhost();
        InvalidateVisual();
    }

    private static string RelativeCellRefForDrag(string cellAbsDir, LayoutEditorViewModel vm)
    {
        try { return Path.GetRelativePath(vm.InstanceBaseDir, cellAbsDir); }
        catch { return cellAbsDir; }
    }

    /// <summary>R-bmp-5 — the Insert Bitmap toolbar button's entry point; centres the placed rect on
    /// the CURRENT viewport centre. Called from the hosting view's code-behind after the file picker
    /// returns a path (UI firewall — the picker itself never lives here).</summary>
    public void InsertBitmapAtViewportCenter(string path)
    {
        if (_viewModel is null) return;
        var vp = CurrentViewport;
        double centerX = (vp.VisibleMinX + vp.VisibleMaxX) / 2.0;
        double centerY = (vp.VisibleMinY + vp.VisibleMaxY) / 2.0;
        _viewModel.InsertBitmapAtViewportCenter(path, centerX, centerY, ViewportWidthDbu());
        InvalidateVisual();
    }

    // ── Shape context menu: edge conversion + delete-vertex (L1d §4/§3) + L1e booleans/offset/
    // repair/flatten (docs/sonnet-briefs/brief-L1e-clipper-operations.md §3/§4/§5) ─────────────────
    //
    // L1-fix (brief-L1-fix-context-menu-stacking.md): the single ContextMenu instance lives in
    // LayoutEditorView.axaml, declared once on this control — never `new`-ed per click (that was the
    // bug: every right-click built and manually opened its OWN ContextMenu, which stacked). This
    // control only RECORDS the click (see OnPointerPressed's ContextMenuTarget) and, on request,
    // BUILDS a fresh item list — it never constructs or opens a ContextMenu itself.

    /// <summary>World-space point of the pending right-click, or null when no menu should open (Ctrl
    /// was held, routing to ordinary press handling instead — see <c>OnPointerPressed</c>). The
    /// hosting view's <c>ContextMenu.Opening</c> handler calls <see cref="ConsumeContextMenuTarget"/>
    /// once per opening and cancels the menu when it returns null.</summary>
    public (double Wx, double Wy)? ContextMenuTarget { get; private set; }

    /// <summary>
    /// The hit tolerance a context menu should test with, in DBU at the CURRENT zoom — the same
    /// several-pixel figure <see cref="BuildContextMenuItems"/> uses. Exposed because the wBond editor
    /// builds its OWN item list on this control (its right-click has to find a WIRE, which this
    /// control knows nothing about) and a second tolerance would let the menu offer to delete a vertex
    /// the click did not actually land on.
    /// </summary>
    internal long ContextMenuHitTolDbu => HitTolDbu();

    /// <summary>Returns and clears the pending target — atomic so a stale target can never be reused
    /// for a later, unrelated opening.</summary>
    internal (double Wx, double Wy)? ConsumeContextMenuTarget()
    {
        var t = ContextMenuTarget;
        ContextMenuTarget = null;
        return t;
    }

    /// <summary>Builds a FRESH list of menu items for a right-click at <paramref name="wx"/>/<paramref
    /// name="wy"/> — called anew on every <c>ContextMenu.Opening</c>, never reused across openings
    /// (reusing item instances and re-subscribing <c>Click</c> would fire an action N times on the
    /// Nth opening — the exact mistake this fix must not reintroduce).</summary>
    internal List<object> BuildContextMenuItems(double wx, double wy)
    {
        // WB39a: the overlay contributes FIRST, and it may be the only contributor — a wBond editor
        // (or a Layout Editor pushed into a wirebond cell) has wire commands to offer whether or not
        // this canvas has a view model bound yet. The canvas's own items follow after a separator.
        var overlayItems = _canvasOverlay?.BuildContextMenuItems(wx, wy, HitTolDbu(), _viewModel, this) ?? [];

        var items = new List<object>();
        if (_viewModel is null) return [.. overlayItems];

        var foundEdge = _viewModel.FindEdgeForContextMenu(wx, wy, HitTolDbu());
        if (foundEdge is { } f)
        {
            void AddItem(string header, EdgeKind kind)
            {
                if (f.CurrentKind == kind) return; // no point offering to convert to what it already is
                var mi = new MenuItem { Header = header };
                mi.Click += (_, _) => { _viewModel.ConvertEdge(f.ShapeIndex, f.EdgeIndex, kind); InvalidateVisual(); };
                items.Add(mi);
            }
            AddItem("Convert to Line", EdgeKind.Line);
            AddItem("Convert to Arc", EdgeKind.Arc);
            AddItem("Convert to Cubic", EdgeKind.Cubic);
        }

        var foundVertex = _viewModel.FindVertexForContextMenu(wx, wy, HitTolDbu());
        if (foundVertex is { } v)
        {
            if (items.Count > 0) items.Add(new Separator());
            // R-L1h-3: shown whenever a vertex is under the click (matches the table's "opened...on a
            // vertex" precondition), but disabled with its reason when removal is blocked by the
            // minimum-vertex-count rule — never a silent no-op click.
            var avail = _viewModel.DeleteVertexAvailability(v.ShapeIndex, v.VertexIndex);
            var deleteVertex = new MenuItem { Header = "Delete Vertex", IsEnabled = avail.CanExecute };
            if (!avail.CanExecute && avail.DisabledReason is { } reason) ToolTip.SetTip(deleteVertex, reason);
            deleteVertex.Click += (_, _) => { _viewModel.DeleteVertex(v.ShapeIndex, v.VertexIndex); InvalidateVisual(); };
            items.Add(deleteVertex);
        }

        // docs/sonnet-briefs/brief-layout-bitmaps-and-insert-button.md — click-target-scoped, same
        // shape as the edge/vertex items above: only present when the right-click actually landed on
        // a bitmap.
        // §9B.6 R-rul-12: click-target-scoped, the same shape as the bitmap items below — only
        // present when the right-click actually landed on a ruler.
        if (_viewModel.FindRulerForContextMenu(wx, wy, HitTolDbu()) is { } rulerIndex)
        {
            if (items.Count > 0) items.Add(new Separator());

            var editRuler = new MenuItem { Header = "Edit Ruler…" };
            editRuler.Click += async (_, _) => await ShowRulerEditDialogAsync(rulerIndex);
            items.Add(editRuler);

            // §9B.12, directly under Edit Ruler… — the undo of an F5 label move, reachable without
            // opening the dialog. Scoped to the ruler under the click, not to the selection, exactly
            // like the two items around it; disabled with its reason when that ruler's label is
            // already at its default position (R13a — never a silent no-op).
            var resetAvail = _viewModel.ResetRulerLabelPositionAvailabilityFor(rulerIndex);
            var resetLabel = new MenuItem
            {
                Header = "Reset Ruler Label Position",
                IsEnabled = resetAvail.CanExecute,
            };
            if (!resetAvail.CanExecute && resetAvail.DisabledReason is { } resetReason)
                ToolTip.SetTip(resetLabel, resetReason);
            resetLabel.Click += (_, _) => { _viewModel.ResetRulerLabelPosition(rulerIndex); InvalidateVisual(); };
            items.Add(resetLabel);

            var deleteRuler = new MenuItem { Header = "Delete Ruler" };
            deleteRuler.Click += (_, _) => { _viewModel.DeleteRuler(rulerIndex); InvalidateVisual(); };
            items.Add(deleteRuler);
        }

        // R13a: always present, disabled with its stated reason rather than hidden, so "Clear All
        // Rulers" is discoverable on a document that has none.
        {
            if (items.Count > 0) items.Add(new Separator());
            var avail = _viewModel.ClearAllRulersAvailability;
            var clearAll = new MenuItem { Header = "Clear All Rulers", IsEnabled = avail.CanExecute };
            if (!avail.CanExecute && avail.DisabledReason is { } reason) ToolTip.SetTip(clearAll, reason);
            clearAll.Click += (_, _) => { _viewModel.ClearAllRulers(); InvalidateVisual(); };
            items.Add(clearAll);
        }

        var foundBitmap = _viewModel.FindBitmapForContextMenu(wx, wy, HitTolDbu());
        if (foundBitmap is { } bmp)
        {
            if (items.Count > 0) items.Add(new Separator());
            var resolvePath = new MenuItem { Header = "Resolve Path…" };
            resolvePath.Click += async (_, _) => await ShowResolveBitmapPathDialogAsync(bmp.ShapeIndex);
            items.Add(resolvePath);

            var refreshCache = new MenuItem { Header = "Refresh Cache" };
            refreshCache.Click += (_, _) => { _viewModel.RefreshBitmapCache(bmp.ShapeIndex); InvalidateVisual(); };
            items.Add(refreshCache);
        }

        AddBooleanAndFlattenMenuItems(items);
        AddInstanceHierarchyMenuItems(items);

        if (overlayItems.Count == 0) return items;

        var combined = new List<object>(overlayItems.Count + items.Count + 1);
        combined.AddRange(overlayItems);
        if (items.Count > 0) combined.Add(new Separator());
        combined.AddRange(items);
        return combined;
    }

    /// <summary>
    /// Phase L3c — Flatten Hierarchy / Flatten All Levels / Explode Array / Group into Cell
    /// (docs/sonnet-briefs/brief-L3c-flatten-and-group.md). Selection-scoped, same "always present,
    /// disabled-with-a-reason" rule as <see cref="AddBooleanAndFlattenMenuItems"/> above — and
    /// deliberately its OWN group, separated by a <c>Separator</c>, never adjacent to "Flatten to
    /// Polygon…" in the same group (§1's own naming warning: the two "Flatten" operations must never
    /// read as the same command). R-L3c-1a's outcome preview ("→ 20 shape(s)", "→ 2,500 instance(s)")
    /// is baked directly into each item's header — more visible than a hover-only tooltip, and the
    /// brief only asks that the outcome be "reported," not where.
    /// </summary>
    private void AddInstanceHierarchyMenuItems(List<object> items)
    {
        if (_viewModel is null) return;

        MenuItem AddAvailItem(string header, LayoutCommandAvailability avail)
        {
            var mi = new MenuItem { Header = header, IsEnabled = avail.CanExecute };
            if (!avail.CanExecute && avail.DisabledReason is { } reason)
                ToolTip.SetTip(mi, reason);
            items.Add(mi);
            return mi;
        }

        void Sep() { if (items.Count > 0) items.Add(new Separator()); }

        Sep();
        string flattenHeader = "Flatten Hierarchy" + (_viewModel.FlattenOneLevelOutcomeText is { } t1 ? $" ({t1})" : "");
        AddAvailItem(flattenHeader, _viewModel.FlattenHierarchyAvailability).Click += async (_, _) => await ShowFlattenHierarchyAsync();

        string allLevelsHeader = "Flatten All Levels" + (_viewModel.FlattenAllLevelsOutcomeText is { } t2 ? $" ({t2})" : "");
        AddAvailItem(allLevelsHeader, _viewModel.FlattenAllLevelsAvailability).Click += async (_, _) => await ShowFlattenAllLevelsAsync();

        // R-L3c-1's own instruction: a separate "Explode Array" entry, enabled only for arrays, routes
        // through the SAME command as Flatten Hierarchy (LayoutFlatten.FlattenOneLevel already detects
        // the array case on its own) so the two can never diverge.
        AddAvailItem("Explode Array", _viewModel.ExplodeArrayAvailability).Click += async (_, _) => await ShowFlattenHierarchyAsync();

        Sep();
        string groupHeader = "Group into Cell…" + (_viewModel.GroupIntoCellOutcomeText is { } t3 ? $" ({t3})" : "");
        AddAvailItem(groupHeader, _viewModel.GroupIntoCellAvailability).Click += async (_, _) => await ShowGroupIntoCellAsync();
    }

    /// <summary>Shared cross-technology confirmation step (R-L3c-3) for both Flatten Hierarchy and
    /// Flatten All Levels — mirrors <c>LayoutEditorView.axaml.cs</c>'s own <c>ResolveLayerMappingAsync</c>
    /// (the technology-retarget flow), duplicated in this small a shape rather than reached across
    /// files, since this dialog is triggered from a context-menu item the canvas itself owns. The
    /// ACTUAL reconciliation logic is never duplicated — only this ~10-line "show the shared dialog"
    /// wrapper is. Returns <c>null</c> both when nothing needs confirming (caller proceeds with no
    /// remap) and when the user cancels (caller must then abandon the whole operation) — the two are
    /// disambiguated by the caller re-checking <c>CheckFlattenCrossTechMapping()</c> was non-null.</summary>
    private async Task<IReadOnlyList<LayerMappingRow>?> ResolveFlattenLayerMappingAsync(IReadOnlyList<LayerMappingRow> mapping)
    {
        if (_viewModel is null) return null;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return null;

        var destTech = _viewModel.Technology;
        if (destTech is null) return null;
        var sourceTechName = _viewModel.FlattenSelectedSubCellTechnology()?.Name;

        var dialog = new LayerMappingDialog("Flatten Hierarchy", sourceTechName, destTech, mapping);
        var result = await dialog.ShowDialog<LayerMappingDialogResult?>(owner);
        return result?.Rows;
    }

    private async Task ShowFlattenHierarchyAsync()
    {
        if (_viewModel is null) return;

        var mapping = _viewModel.CheckFlattenCrossTechMapping();
        if (mapping is not null)
        {
            var resolved = await ResolveFlattenLayerMappingAsync(mapping);
            if (resolved is null) return;   // cancel abandons the whole operation, matches retarget's own rule
            mapping = resolved;
        }

        if (_viewModel.FlattenOneLevelNeedsConfirmation && !await ConfirmFlattenAsync(_viewModel.FlattenOneLevelOutcomeText))
            return;

        _viewModel.CommitFlattenOneLevel(mapping);
        InvalidateVisual();
    }

    private async Task ShowFlattenAllLevelsAsync()
    {
        if (_viewModel is null) return;

        var mapping = _viewModel.CheckFlattenCrossTechMapping();
        if (mapping is not null)
        {
            var resolved = await ResolveFlattenLayerMappingAsync(mapping);
            if (resolved is null) return;
            mapping = resolved;
        }

        // Flatten All Levels always confirms (R-L3c-4 — the pre-computed count, or the hard-ceiling
        // refusal, must be seen before a whole hierarchy collapses into geometry).
        if (!await ConfirmFlattenAsync(_viewModel.FlattenAllLevelsOutcomeText, alwaysConfirm: true))
            return;

        _viewModel.CommitFlattenAllLevels(mapping);
        InvalidateVisual();
    }

    private async Task<bool> ConfirmFlattenAsync(string? outcomeText, bool alwaysConfirm = false)
    {
        if (_viewModel is null) return false;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return !alwaysConfirm && !_viewModel.FlattenOneLevelNeedsConfirmation;

        var dialog = new SaveChangesDialog(
            $"This will replace the selected instance {outcomeText ?? ""}. This is not perfectly reversible except by Undo.",
            saveLabel: "Flatten", dontSaveLabel: null, cancelLabel: "Cancel", title: "Flatten Hierarchy");
        var result = await dialog.ShowDialog<SaveChangesResult>(owner);
        return result == SaveChangesResult.Save;
    }

    private async Task ShowGroupIntoCellAsync()
    {
        if (_viewModel is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner is null) return;

        string? parentDir = _viewModel.WorkspaceRootDir;
        if (parentDir is null)
        {
            _viewModel.ReportError("Group into Cell needs an open workspace to create the new cell in.");
            return;
        }

        var nameDialog = new InputNameDialog("Group into Cell", "New cell name:");
        string? name = await nameDialog.ShowDialog<string?>(owner);
        if (name is null) return;

        // R-L3c-6: undo removes the instance and restores the shapes/instances, but does NOT delete
        // the cell folder just created — say so up front, not only after the fact.
        var confirm = new SaveChangesDialog(
            $"Create cell '{name}' from the current selection {_viewModel.GroupIntoCellOutcomeText ?? ""}. " +
            "Undoing this afterward will restore the selection but will not delete the cell folder.",
            saveLabel: "Create", dontSaveLabel: null, cancelLabel: "Cancel", title: "Group into Cell");
        if (await confirm.ShowDialog<SaveChangesResult>(owner) != SaveChangesResult.Save) return;

        _viewModel.CommitGroupIntoCell(parentDir, name);
        InvalidateVisual();
    }

    /// <summary>
    /// L1e/L1h — items that act on the current SELECTION, independent of what (if anything) is
    /// directly under the right-click point. R-L1h-3: every one of these is ALWAYS present (never
    /// conditionally omitted) — disabled with a stated reason (its tooltip) when it cannot run, so
    /// muscle memory and menu position both stay stable and a missing item never reads as a bug.
    /// "Merge" is gone (R-L1h-1 — Union now groups by layer, which is what Merge always did) and
    /// "Flatten to Polygon" (the no-dialog variant) is gone (R-L1h-2 — the "…" prompt is the one
    /// surviving entry, since flattening is irreversible-except-by-undo and its resolution is the
    /// whole point of the operation).
    /// </summary>
    private void AddBooleanAndFlattenMenuItems(List<object> items)
    {
        if (_viewModel is null) return;

        MenuItem AddAvailItem(string header, LayoutCommandAvailability avail)
        {
            var mi = new MenuItem { Header = header, IsEnabled = avail.CanExecute };
            if (!avail.CanExecute && avail.DisabledReason is { } reason)
                ToolTip.SetTip(mi, reason);
            items.Add(mi);
            return mi;
        }

        void Sep() { if (items.Count > 0) items.Add(new Separator()); }

        // Rotate is reachable from the keyboard already, and was reachable from nowhere else — which
        // is how an EM port, whose whole direction IS its rotation, could look like it had none
        // (owner report, 2026-08-09). Same disabled-with-a-reason rule as everything below it.
        Sep();
        var rotAvail = _viewModel.RotateAvailability;
        AddAvailItem("Rotate 90° CCW", rotAvail).Click += (_, _) => { _viewModel.RotateSelection(clockwise: false); InvalidateVisual(); };
        AddAvailItem("Rotate 90° CW", rotAvail).Click  += (_, _) => { _viewModel.RotateSelection(clockwise: true);  InvalidateVisual(); };

        // Duplicate sits directly below Rotate (owner, 2026-08-27) — until now it was reachable only
        // from Ctrl+D, i.e. from nowhere a user could SEE, which is the same complaint Rotate itself
        // answered above. Both surfaces run the same ShowDuplicateDialogAsync, so the offset prompt
        // cannot appear on one and not the other.
        AddAvailItem("Duplicate…", _viewModel.DuplicateAvailability).Click += async (_, _) => await ShowDuplicateDialogAsync();

        Sep();
        var boolAvail = _viewModel.BooleanOpAvailability;
        AddAvailItem("Union", boolAvail).Click      += (_, _) => { _viewModel.ApplyUnion(); InvalidateVisual(); };
        AddAvailItem("Intersect", boolAvail).Click  += (_, _) => { _viewModel.ApplyIntersect(); InvalidateVisual(); };
        AddAvailItem("Difference", boolAvail).Click += (_, _) => { _viewModel.ApplyDifference(); InvalidateVisual(); };
        AddAvailItem("XOR", boolAvail).Click        += (_, _) => { _viewModel.ApplyXor(); InvalidateVisual(); };

        Sep();
        AddAvailItem("Offset…", _viewModel.OffsetAvailability).Click += async (_, _) => await ShowOffsetDialogAsync();
        AddAvailItem("Scale…", _viewModel.ScaleAvailability).Click   += async (_, _) => await ShowScaleDialogAsync();

        // R-L1h-5 row 3: the only way to reach bbox scale handles on a SINGLE shape (a 2+ selection
        // always shows them — see LayoutEditorViewModel.ShowScaleHandles). "Enabled" here always,
        // matching Offset/Scale… — toggling it on an empty selection is harmless (no handles to show).
        var scaleModeItem = AddAvailItem(
            _viewModel.ScaleModeActive ? "Exit Scale Mode" : "Scale Mode",
            LayoutCommandAvailability.Enabled);
        scaleModeItem.Click += (_, _) => { _viewModel.ToggleScaleModeCommand.Execute(null); InvalidateVisual(); };

        Sep();
        AddAvailItem("Repair Self-Intersection", _viewModel.RepairAvailability).Click +=
            (_, _) => { _viewModel.RepairSelfIntersection(_viewModel.SelectedIndices[0]); InvalidateVisual(); };

        Sep();
        AddAvailItem("Flatten to Polygon…", _viewModel.FlattenAvailability).Click += async (_, _) => await ShowFlattenToPolygonDialogAsync();

        // R-via-6 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.2): recovers a bare Circle
        // drawn on a drill layer (the intuitive, MMIC-genuine gesture §1 describes) back into a real
        // paired ViaShape.
        AddAvailItem("Convert to Via", _viewModel.ConvertToViaAvailability).Click +=
            (_, _) => { _viewModel.CommitConvertToVia(); InvalidateVisual(); };
    }

    private async Task ShowFlattenToPolygonDialogAsync()
    {
        if (_viewModel is null) return;
        var indices = _viewModel.SelectedIndices.ToList();
        if (indices.Count == 0) return;

        var dialog = new FlattenToPolygonDialog(_viewModel, indices);

        var owner = TopLevel.GetTopLevel(this) as Window;
        long? chosen = owner is not null
            ? await dialog.ShowDialog<long?>(owner)
            : null;

        if (chosen is { } tolDbu)
        {
            _viewModel.FlattenSelectionToPolygon(tolDbu);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// R-rul-12's <c>Edit…</c>: selects the right-clicked ruler (so the docked inspector agrees with
    /// the popup) and opens the SAME property surface as a non-modal window, mirroring
    /// <c>LayoutEditorView</c>'s own PCell-parameter popup exactly.
    /// </summary>
    private Task ShowRulerEditDialogAsync(int rulerIndex)
    {
        if (_viewModel is null) return Task.CompletedTask;
        _viewModel.SelectRuler(rulerIndex);
        InvalidateVisual();

        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialogVm = new ViewModels.LayoutShapePropertiesViewModel();
        dialogVm.SetContext(_viewModel);
        var dialog = new Views.Dialogs.LayoutRulerEditDialog { DataContext = dialogVm };
        dialog.Closed += (_, _) => dialogVm.SetContext(null);   // unsubscribe from the layout VM
        if (owner is not null) dialog.Show(owner); else dialog.Show();
        return Task.CompletedTask;
    }

    private async Task ShowResolveBitmapPathDialogAsync(int shapeIndex)
    {
        if (_viewModel is null) return;
        var picker = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (picker is null) return;

        var files = await picker.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Resolve Bitmap Path",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("Image Files") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.tiff", "*.tif", "*.webp" } }
            }
        });
        if (files.Count > 0)
        {
            _viewModel.ResolveBitmapPath(shapeIndex, files[0].Path.LocalPath);
            InvalidateVisual();
        }
    }

    private async Task ShowOffsetDialogAsync()
    {
        if (_viewModel is null) return;
        var dialog = new OffsetDialog(_viewModel);

        var owner = TopLevel.GetTopLevel(this) as Window;
        string? text = owner is not null ? await dialog.ShowDialog<string?>(owner) : null;
        if (text is null) return;

        _viewModel.CommitOffsetText(text);
        _viewModel.ApplyOffsetToSelection();
        InvalidateVisual();
    }

    /// <summary>Prompts for the copy's X/Y offset (default 0,0, in the layout's display unit) and
    /// duplicates. Shared by the context-menu item and the view's Ctrl+D handler — the ONE place the
    /// Duplicate gesture is spelled out, so the keyboard and the menu can never drift apart.</summary>
    internal async Task ShowDuplicateDialogAsync()
    {
        if (_viewModel is null || !_viewModel.CanDuplicateSelection) return;
        var dialog = new DuplicateOffsetDialog(_viewModel);

        var owner = TopLevel.GetTopLevel(this) as Window;
        var result = owner is not null ? await dialog.ShowDialog<DuplicateOffsetResult?>(owner) : null;
        if (result is not { } r) return;

        _viewModel.Duplicate(r.DxDbu, r.DyDbu);
        InvalidateVisual();
    }

    private async Task ShowScaleDialogAsync()
    {
        if (_viewModel is null) return;
        var dialog = new ScaleDialog(_viewModel);

        var owner = TopLevel.GetTopLevel(this) as Window;
        var result = owner is not null ? await dialog.ShowDialog<ScaleDialogResult?>(owner) : null;
        if (result is not { } r) return;

        _viewModel.ApplyScale(r.FactorX, r.FactorY, r.AnchorX, r.AnchorY);
        InvalidateVisual();
    }

    private void OnPointerMoved(object? _, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_isPanning)
        {
            // A pan cannot be in progress with nothing held. If the release went somewhere else, this
            // is the first move that can notice — cheaper and more reliable than hoping for the event.
            var held = e.GetCurrentPoint(this).Properties;
            if (!held.IsLeftButtonPressed && !held.IsMiddleButtonPressed)
            {
                EndPanIfActive();
                return;
            }

            var vp = CurrentViewport;
            // Screen Y maps to world via a Y-up flip, so a downward drag (increasing screen Y) must
            // move PanY the SAME direction as the drag, unlike the schematic's Y-down canvases.
            _panX = _panDragStartPanX - (pos.X - _panDragStartScreen.X) / vp.Zoom;
            _panY = _panDragStartPanY + (pos.Y - _panDragStartScreen.Y) / vp.Zoom;
            RaiseViewportChanged();
            return;   // middle-drag pan keeps working during any left-button drawing gesture
        }

        var (wx, wy) = ScreenToWorld(pos.X, pos.Y);
        CursorWorldChanged?.Invoke(this, (wx, wy));

        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (_canvasOverlay?.OnPointerMoved(
                (long)Math.Round(wx), (long)Math.Round(wy), HitTolDbu(), leftDown, e.KeyModifiers) == true)
        {
            // The overlay is driving: push ITS delta into the layout editor so the layout's half of a
            // mixed selection follows. One delta, from one snap decision — re-deriving it here is how
            // the two halves end up a step apart.
            if (_canvasOverlay.CompanionDragDelta is { } overlayDelta && _viewModel is { } companionVm)
            {
                companionVm.SetCompanionMoveDuplicate(_canvasOverlay.DuplicateDragArmed);
                companionVm.CompanionMoveTo(overlayDelta.Dx, overlayDelta.Dy);
            }

            PushOverlaySnapMarker();

            // …and the pointer, or an Alt taken MID-drag on a wire arms the copy with nothing on
            // screen saying so (owner, 2026-08-27). This branch returns before the UpdateCursor at
            // the end of the method, so it has to ask for itself.
            UpdateCursor();
            InvalidateVisual();
            return;
        }

        _viewModel?.OnPointerMoved(wx, wy, leftDown, e.KeyModifiers, HitTolDbu(), OnePixelDbu(), SnapTolDbu(),
                                   GripLockTolDbu());

        // …and the mirror: the LAYOUT is driving, so the overlay's half follows its delta.
        if (_viewModel?.MoveDragDelta is { } layoutDelta)
            _canvasOverlay?.CompanionMoveTo(layoutDelta.Dx, layoutDelta.Dy);

        // R-pch-12: the pointer shape is half of "you can see which gesture you are about to get", and
        // the view model has just recomputed which grip (if any) is under the cursor. Cheap — it only
        // constructs a Cursor when the answer actually changed.
        UpdateCursor();
        InvalidateVisual();
    }

    private void OnPointerExited(object? _, PointerEventArgs e) => CursorWorldChanged?.Invoke(this, null);

    /// <summary>
    /// Ends a pan wherever it ended — the ONE place <see cref="_isPanning"/> is cleared, so every exit
    /// from the gesture restores the cursor the same way.
    ///
    /// <para><b>A pan that never sees its own release is the latch this exists for.</b> Losing the
    /// capture to another window, the app deactivating mid-drag, or a middle-button release the OS
    /// swallows all leave <c>_isPanning</c> set — and from then on <see cref="OnPointerMoved"/>'s first
    /// branch pans on every move and <see cref="UpdateCursor"/>'s first branch pins the hand, with
    /// nothing on screen explaining either. Same family as the <see cref="_spaceHeld"/> latch
    /// <see cref="OnCanvasLostFocus"/> already documents; this is the one it missed.</para>
    /// </summary>
    private void EndPanIfActive()
    {
        if (!_isPanning) return;
        _isPanning = false;
        UpdateCursor();
    }

    private void OnPointerCaptureLost(object? _, PointerCaptureLostEventArgs e) => EndPanIfActive();

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            e.Pointer.Capture(null);
            EndPanIfActive();
            return;
        }

        var pos = e.GetPosition(this);
        var (wx, wy) = ScreenToWorld(pos.X, pos.Y);

        // Released UNCONDITIONALLY, whatever the overlay says about this particular release: a press
        // it consumed as a plain click (placing a wire's first foot) captures just the same, and a
        // capture that outlives its gesture would swallow every later click on the panel beside it.
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);

        // ONE EDIT STAMP for the whole release, which is what makes a MIXED drag one Ctrl+Z (owner,
        // 2026-08-27). The two halves of such a gesture land on two different undo histories — the
        // wires' snapshots and the layout's command stack — and those cannot be merged; a shared
        // EditSequence stamp is how LayoutEditorViewModel.UndoLast knows they are one edit.
        //
        // Opened unconditionally rather than only for a mixed drag: a release commits at most one
        // gesture, so on every ordinary drag the group holds exactly one entry and costs nothing.
        using var editGroup = CircuitRF.Ui.Commands.EditSequence.Group();

        if (_canvasOverlay?.OnPointerReleased((long)Math.Round(wx), (long)Math.Round(wy)) == true)
        {
            _viewModel?.CommitCompanionMove();
            PushOverlaySnapMarker();   // the gesture is over, so this clears the glyph
            InvalidateVisual();
            return;
        }

        // Both halves close, whichever one drove: the overlay's companion move is armed on every
        // press it declined, and one left open would go on translating wires under the next gesture.
        _canvasOverlay?.CommitCompanionMove();
        _viewModel?.CommitCompanionMove();
        _viewModel?.OnPointerReleased(wx, wy, e.KeyModifiers);
        InvalidateVisual();
    }

    private void OnPointerWheel(object? _, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? ZoomFactor : 1.0 / ZoomFactor;
        double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);

        var vp = CurrentViewport.WithZoomAnchoredAt(newZoom, pos.X, pos.Y);
        _panX = vp.PanX; _panY = vp.PanY; _zoom = vp.Zoom;
        RaiseViewportChanged();
        e.Handled = true;   // wheel zoom keeps working during any drawing gesture (never routed to the VM)
    }

    private void OnKeyDown(object? _, KeyEventArgs e)
    {
        // R-lbl-3 (docs/sonnet-briefs/brief-layout-label-fix-and-text-flatten.md): Space is an
        // ordinary character while typing a label — arming the pan modifier here would leave a
        // subsequent left-drag panning instead of doing nothing (labels have no drag gesture), even
        // though Space itself still reaches the label buffer via TextInput regardless (a separate,
        // unhandled routed event) — this guard only stops the SIDE EFFECT, not the character.
        if (e.Key == Key.Space && _viewModel?.IsTypingLabel != true) { _spaceHeld = true; UpdateCursor(); return; }

        // R-pch-12: Alt with the pointer STATIONARY still has to light the grips up — the armed state
        // is what tells the user the next press cannot move the cell, and waiting for a pointer move to
        // say so would make the mode announce itself only after they had already committed to aiming.
        // Deliberately not `return`: Alt is a modifier, and swallowing it here would cost every
        // Alt-combination below. The view model refuses to arm when the selection has no grips.
        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            _altHeld = true;
            _viewModel?.SetGripLockArmed(true);
            UpdateCursor();
            InvalidateVisual();
        }

        // A paste ghost in progress owns every key itself (Escape cancels it) — never let a
        // clipboard shortcut race with an already-armed placement.
        if (_viewModel?.IsPastePlacementActive == true)
        {
            _viewModel.OnKeyDown(e.Key, e.KeyModifiers);
            InvalidateVisual();
            return;
        }

        // The overlay owns its own selection, so its nudge/delete keys have to reach it before the
        // layout editor's — but only when it actually has something selected, which is what its own
        // return value states.
        if (_canvasOverlay?.OnKeyDown(e.Key, e.KeyModifiers) == true)
        {
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        // F — Zoom to Fit, the same key the Schematic and Symbol editors have always used (owner,
        // 2026-08-26; it was simply never wired up here). Handled on the CANVAS rather than in the
        // view's tunnel because ZoomToFit is the canvas's own, which is where SymbolEditorCanvas and
        // WBondProfileCanvas both put it.
        //
        // Suppressed while a label is being typed, and that guard is load-bearing: 'f' is an ordinary
        // character in label text, and without this the editor would jump to fit the moment the user
        // typed one. SymbolEditorCanvas gates its own F on the same condition for the same reason.
        if (!ctrl && e.Key == Key.F && _viewModel?.IsTypingLabel != true)
        {
            ZoomToFit();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.C) { ClipboardCopyRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && e.Key == Key.X) { ClipboardCutRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V) { ClipboardPasteInPlaceRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { ClipboardPasteRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }
        if (ctrl && e.Key == Key.D) { DuplicateRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; return; }

        _viewModel?.OnKeyDown(e.Key, e.KeyModifiers);
        InvalidateVisual();
    }

    private void OnKeyUp(object? _, KeyEventArgs e)
    {
        _canvasOverlay?.OnKeyUp(e.Key, e.KeyModifiers);
        if (e.Key == Key.Space) { _spaceHeld = false; UpdateCursor(); }
        if (e.Key is Key.LeftAlt or Key.RightAlt)
        {
            _altHeld = false;
            _viewModel?.SetGripLockArmed(false);
            UpdateCursor();
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Drops every held-key latch when focus leaves, because <b>the matching key-up is not
    /// guaranteed to arrive here</b>.
    ///
    /// <para>Hold Space to pan, then — still holding it — click a toolbar button, a combo, or another
    /// tab. The release is delivered to whatever took focus and this control never sees it, so
    /// <see cref="_spaceHeld"/> stays set: from that moment every left-drag is a PAN, the marquee
    /// never starts, and nothing on screen explains it. (The cursor does change to a hand, which is
    /// the only clue there ever was.) An overlay's own promotion latches have the same shape and are
    /// cleared through <see cref="ILayoutCanvasOverlay.OnFocusLost"/>.</para>
    /// </summary>
    private void OnCanvasLostFocus(object? _, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _spaceHeld = false;
        _canvasOverlay?.OnFocusLost();

        // R-pch-12's latch has exactly the shape this method exists for: hold Alt, click a toolbar
        // button, and the key-up is delivered to whatever took focus. Without this the grips stay lit
        // and every subsequent press claims a grip instead of moving the instance — the same failure
        // as the Space-to-pan latch above, with a different key.
        _viewModel?.ClearGripLockArmed();
        _altHeld = false;   // same latch, same reason — see above
        _isPanning = false; // and the pan latch — see EndPanIfActive for the report this closes

        UpdateCursor();
    }

    private void OnTextInput(object? _, TextInputEventArgs e)
    {
        if (e.Text is not { Length: > 0 }) return;
        _viewModel?.OnTextInput(e.Text);
        InvalidateVisual();
    }

    // Crosshair for every drawing tool, arrow for Select — mirrors SymbolEditorCanvas.UpdateCursor.
    private void UpdateCursor()
    {
        if (_isPanning || _spaceHeld) { SetCursor(StandardCursorType.Hand); return; }

        // R-pch-12: over a PCell parameter grip the pointer says which way that grip travels, which is
        // the only pre-press signal separating "edit this parameter" from "move the whole instance".
        // Below panning (a held Space outranks everything) and above the tool cursor, because a grip
        // is a Select-tool affordance and no drawing tool shows one.
        if (_viewModel is { ActiveTool: LayoutEditorViewModel.Tool.Select } vm
            && vm.HoveredPCellHandleCursor != PCellGripCursor.None)
        {
            SetCursor(vm.HoveredPCellHandleCursor switch
            {
                PCellGripCursor.EastWest           => StandardCursorType.SizeWestEast,
                PCellGripCursor.NorthSouth         => StandardCursorType.SizeNorthSouth,
                PCellGripCursor.NorthEastSouthWest => StandardCursorType.TopRightCorner,
                PCellGripCursor.NorthWestSouthEast => StandardCursorType.TopLeftCorner,
                _                                  => StandardCursorType.SizeAll,
            });
            return;
        }

        // R-dup-1: Alt says "the thing you are dragging will be a COPY", and it has to say so with the
        // pointer — the ghost only appears once the drag has moved, and the decision is made before
        // that. Shown whenever Alt is held with something to copy, so it also announces the gesture
        // BEFORE the press, not only during the drag it arms.
        // …and an OVERLAY gets the same say, so an Alt-drag that will copy a bond wire looks like one
        // (owner, 2026-08-27). Asked first: when the overlay owns the gesture, the layout editor's own
        // answer is about a selection this drag is not touching.
        if (_canvasOverlay?.DuplicateDragArmed == true)
        {
            SetCursor(StandardCursorType.DragCopy);
            return;
        }

        if (_viewModel is { ActiveTool: LayoutEditorViewModel.Tool.Select } dupVm
            && (dupVm.DuplicateDragArmed || (_altHeld && dupVm.HasDuplicableSelection)))
        {
            SetCursor(StandardCursorType.DragCopy);
            return;
        }

        bool useCross = _viewModel is { ActiveTool: not LayoutEditorViewModel.Tool.Select };
        if (useCross) { SetCursor(StandardCursorType.Cross); return; }
        if (_currentCursorType is null) return;
        _currentCursorType = null;
        Cursor = Cursor.Default;
    }

    /// <summary>The cursor is now refreshed on EVERY pointer move (R-pch-12 needs it to be), so it has
    /// to be free when nothing changed: a <c>new Cursor(...)</c> per move allocates a platform handle
    /// per frame of a drag.</summary>
    private StandardCursorType? _currentCursorType;

    /// <summary>R-dup-1: Alt held, tracked for the duplicate CURSOR only. A pointer move carries its
    /// own modifiers and does not need this; a bare key-down with the pointer stationary does. Cleared
    /// on LostFocus with every other held-key latch here.</summary>
    private bool _altHeld;

    private void SetCursor(StandardCursorType type)
    {
        if (_currentCursorType == type) return;
        _currentCursorType = type;
        Cursor = new Cursor(type);
    }

    // ── ICustomDrawOperation ──────────────────────────────────────────────────

    private sealed class LayoutDrawOperation : ICustomDrawOperation
    {
        private readonly Rect                _bounds;
        private readonly LayoutView?         _view;
        private readonly Technology?         _tech;
        private readonly LayoutViewport      _vp;
        private readonly LayoutRenderOptions _opts;
        private readonly ILayoutCanvasOverlay? _overlay;
        private readonly Action<LayoutRenderResult> _onResult;

        public LayoutDrawOperation(Rect bounds, LayoutView? view, Technology? tech, LayoutViewport vp, LayoutRenderOptions opts, ILayoutCanvasOverlay? overlay, Action<LayoutRenderResult> onResult)
        {
            _bounds = bounds; _view = view; _tech = tech; _vp = vp; _opts = opts; _overlay = overlay; _onResult = onResult;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);

        // RUNS ON THE RENDER THREAD, not the UI thread — the reason for the lock below, and the
        // reason this method must never call back into the UI thread synchronously (see _onResult).
        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();

            // The WHOLE frame under the model's RenderLock: an edit's NotifyChanged takes the same
            // lock, so a repaint can never overlap a spatial-index rebuild or a LayoutPathCache
            // eviction — the latter DISPOSES the very SKPath objects the draw calls below hand to
            // Skia. Owner-reported crash, 2026-08-22: see LayoutView.RenderLock and
            // LayoutSpatialIndex's own _gate for the two halves of that story.
            //
            // _onResult stays OUTSIDE the lock and posts rather than invokes, so the render thread
            // never waits on the UI thread while holding it.
            LayoutRenderResult result;
            if (_view is null) result = DrawFrame(lease.SkCanvas);
            else lock (_view.RenderLock) result = DrawFrame(lease.SkCanvas);

            _onResult(result);
        }

        private LayoutRenderResult DrawFrame(SKCanvas canvas)
        {
            var opts = _overlay is null
                ? _opts
                : _opts with { DeferSnapMarker = true, DeferRulers = true };
            var result = LayoutRenderer.Draw(canvas, _view, _tech, _vp, opts);

            // After the layout, inside the same lease — the overlay draws ON the layout (WB23), and
            // its own pass never reaches LayoutRenderer's caches.
            _overlay?.Draw(canvas, _vp, _opts.Theme);

            // Then the rulers, above that overlay. §9B.1: a ruler "always paints above every layer",
            // and on a wirebond layout the overlay's wires are painted after every one of them — so
            // drawn in their ordinary place the annotation ends up UNDER the wire it is measuring
            // (owner, 2026-08-27). Order is the property that has to hold. See
            // LayoutRenderOptions.DeferRulers.
            if (_overlay is not null)
                LayoutRenderer.DrawRulersOnTop(canvas, _view, _vp, _opts);

            // …and the geometry-snap glyph goes on last of all, above that overlay, which is the only
            // place it can be seen at high zoom: an overlay's wires and vertex dots scale with zoom
            // without limit while the glyph is a fixed ~8 device pixels, so drawn in its old place it
            // ends up UNDER metal wide enough to hide it entirely (owner, 2026-08-19). Only when there
            // IS an overlay — otherwise LayoutRenderer has already drawn it in the ordinary place and
            // nothing is painted over it. See LayoutRenderOptions.DeferSnapMarker.
            if (_overlay is not null)
                LayoutRenderer.DrawSnapMarkerOnTop(canvas, _view, _tech, _vp, _opts);

            return result;
        }

        public void Dispose() { }
    }
}
