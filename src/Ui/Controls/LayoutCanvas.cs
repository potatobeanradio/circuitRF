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
        DoubleTapped        += OnDoubleTapped;
        KeyDown             += OnKeyDown;
        KeyUp               += OnKeyUp;
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
            Cursor = new Cursor(StandardCursorType.Hand);
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
                _viewModel.OnPointerPressed(wx, wy, e.KeyModifiers, e.ClickCount, HitTolDbu(), 0, SnapTolDbu());
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
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            _viewModel.OnPointerPressed(wx, wy, e.KeyModifiers, e.ClickCount, HitTolDbu(), _zoom, SnapTolDbu());
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

        if (!vm.CanDropPaletteComponent(payload.Kind, payload.PortCount)) return;
        vm.CommitPaletteDrop(payload.Kind, payload.PortCount, sx, sy);
        e.Handled = true;
        InvalidateVisual();
    }

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
        var items = new List<object>();
        if (_viewModel is null) return items;

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
        return items;
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
            InvalidateVisual();
            return;
        }

        _viewModel?.OnPointerMoved(wx, wy, leftDown, e.KeyModifiers, HitTolDbu(), OnePixelDbu(), SnapTolDbu());
        InvalidateVisual();
    }

    private void OnPointerExited(object? _, PointerEventArgs e) => CursorWorldChanged?.Invoke(this, null);

    private void OnPointerReleased(object? _, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            UpdateCursor();
            return;
        }

        var pos = e.GetPosition(this);
        var (wx, wy) = ScreenToWorld(pos.X, pos.Y);

        if (_canvasOverlay?.OnPointerReleased((long)Math.Round(wx), (long)Math.Round(wy)) == true)
        {
            InvalidateVisual();
            return;
        }

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
        if (_isPanning || _spaceHeld) { Cursor = new Cursor(StandardCursorType.Hand); return; }
        bool useCross = _viewModel is { ActiveTool: not LayoutEditorViewModel.Tool.Select };
        Cursor = useCross ? new Cursor(StandardCursorType.Cross) : Cursor.Default;
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

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;
            using var lease = leaseFeature.Lease();
            var result = LayoutRenderer.Draw(lease.SkCanvas, _view, _tech, _vp, _opts);

            // After the layout, inside the same lease — the overlay draws ON the layout (WB23), and
            // its own pass never reaches LayoutRenderer's caches.
            _overlay?.Draw(lease.SkCanvas, _vp, _opts.Theme);

            _onResult(result);
        }

        public void Dispose() { }
    }
}
