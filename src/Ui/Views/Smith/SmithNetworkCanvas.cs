using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Smith;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Views.Smith;

/// <summary>
/// The network strip's canvas: the cascade drawn by <c>SchematicRenderer</c> itself, with click
/// selection, drag reorder, scroll-wheel zoom and middle-drag pan
/// (<c>brief-smith-6-network-strip.md</c> <c>R-smith6-1</c>, <c>R-smith6-2</c>).
/// </summary>
/// <remarks>
/// <b>The renderer is the schematic editor's and this control does not touch it.</b> Everything visual
/// — the grid, the glyphs, the three label roles and their colours, the connected-pin markers, the
/// selection outline, the disabled overlay and the LOD fade — arrives through
/// <see cref="SmithNetworkModel"/> and <c>SchematicOverlay</c>, so none of it can drift out of step
/// with a schematic page. <c>MatchSchematicCanvas</c> is the worked example and this is built on its
/// gestures, including the three the owner settled there:
///
/// <list type="bullet">
///   <item><b>Pan on the MIDDLE button</b>, exactly as a schematic page does — which is what leaves the
///   left button for what the pointer is aimed AT, and the right button for the context menu.</item>
///   <item><b>The ordinary arrow cursor</b>, because a crosshair advertises a placement gesture this
///   pane does not have.</item>
///   <item><b>The re-fit test is the EXTENT, not the topology.</b> A rebuild that draws into the same
///   rectangle keeps the user's zoom and pan; one that no longer fits it re-frames. Adding, deleting
///   or mirroring changes the extent and re-fits — which is <c>R-smith6-2</c>'s "Zoom to Fit is the
///   default on any change that alters the element count" — while a slider drag, twenty times a
///   second, does not and must not.</item>
/// </list>
///
/// <para><b>There is no editable schematic here.</b> No wire tool, no free placement, no multi-select,
/// no move: the only thing that can be dragged is an element along the strip, and what that does is
/// reorder the LIST.</para>
/// </remarks>
public sealed class SmithNetworkCanvas : Control
{
    /// <summary>The document's view model — the projection, the selection and the operations.</summary>
    public static readonly StyledProperty<SmithChartViewModel?> ViewModelProperty =
        AvaloniaProperty.Register<SmithNetworkCanvas, SmithChartViewModel?>(nameof(ViewModel));

    /// <inheritdoc cref="ViewModelProperty"/>
    public SmithChartViewModel? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    private const double MinZoom  = 0.01;
    private const double MaxZoom  = 4.0;
    private const double ZoomStep = 1.15;

    /// <summary>How far the pointer must travel before a press on an element becomes a reorder drag
    /// rather than a click. Screen pixels.</summary>
    private const double DragThresholdPixels = 6.0;

    private SmithChartViewModel?     _vm;
    private SmithNetworkProjection   _projection = SmithNetworkProjection.Empty;
    private double _panX, _panY, _zoom = 1.0;
    private bool   _fitted;
    private Point? _panFrom;
    private ColorTheme _theme = ColorTheme.BuiltIn;

    // Zoom-box state (owner instruction, 2026-09-19).
    private bool   _zoomBoxArmed;
    private bool   _zoomBoxDragging;
    private Point  _zoomBoxStart;
    private Point  _zoomBoxCurrent;

    /// <summary>How far the pointer must travel before an armed press counts as a box rather than a
    /// click. Screen pixels — a stray click while armed should disarm, not zoom to a dot.</summary>
    private const double ZoomBoxMinDragPixels = 4.0;

    // Reorder drag state.
    private int    _dragFromIndex = -1;
    private int    _dragToIndex   = -1;
    private Point  _dragOrigin;
    private bool   _dragging;

    /// <summary>
    /// The drawing as the drop would leave it, while a reorder drag is in flight — null otherwise.
    /// </summary>
    /// <remarks>
    /// <b>A whole projection, not a set of moved positions</b>; see
    /// <see cref="SmithChartViewModel.BuildReorderPreview"/> for the two reports that is the fix to.
    /// Rebuilt only when the target SLOT changes, not on every pointer move: the picture cannot
    /// differ between two positions inside one slot, and a build per move is a build per frame for
    /// an answer that has not changed.
    /// </remarks>
    private SmithNetworkProjection? _dragPreview;

    public SmithNetworkCanvas()
    {
        ClipToBounds = true;
        Focusable    = true;
        Cursor       = new Cursor(StandardCursorType.Arrow);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _theme = ThemeService.Active;
        ThemeService.ThemeChanged += OnThemeChanged;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ThemeService.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _theme = ThemeService.Active;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ViewModelProperty) return;

        if (_vm is not null) _vm.NetworkChanged -= OnNetworkChanged;
        _vm = ViewModel;
        if (_vm is not null) _vm.NetworkChanged += OnNetworkChanged;

        _fitted = false;
        OnNetworkChanged();
    }

    private void OnNetworkChanged()
    {
        var next = _vm?.Network ?? SmithNetworkProjection.Empty;
        if (!SameExtent(_projection.Model, next.Model)) _fitted = false;
        _projection = next;
        InvalidateVisual();
    }

    /// <summary>Whether two builds occupy the same rectangle, to within half a world unit — the
    /// Designer's own tolerance, on a drawing whose grid is in the hundreds.</summary>
    private static bool SameExtent(SchematicModel a, SchematicModel b) =>
        Math.Abs(a.BbMinX - b.BbMinX) <= 0.5 && Math.Abs(a.BbMaxX - b.BbMaxX) <= 0.5 &&
        Math.Abs(a.BbMinY - b.BbMinY) <= 0.5 && Math.Abs(a.BbMaxY - b.BbMaxY) <= 0.5;

    // ── Framing ──────────────────────────────────────────────────────────────

    /// <summary>Frames the whole network, the way the editor's own Zoom to Fit does.</summary>
    public void ZoomToFit()
    {
        if (!Fit()) return;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The framing arithmetic on its own, <b>with no <c>InvalidateVisual</c> in it</b>.
    /// </summary>
    /// <remarks>
    /// The Designer paid for this one: a structural change cleared its fitted flag, the next
    /// <c>Render</c> re-fitted, and the fit invalidated the visual it was in the middle of drawing —
    /// which Avalonia refuses outright (<c>InvalidOperationException: Visual was invalidated during the
    /// render pass</c>). A fit performed FROM the render pass has nothing to request, since the frame
    /// it would ask for is the frame being drawn.
    /// </remarks>
    private bool Fit()
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return false;

        var m = _projection.Model;
        double worldW = Math.Max(m.BbMaxX - m.BbMinX, 1);
        double worldH = Math.Max(m.BbMaxY - m.BbMinY, 1);
        const double pad = 0.04;

        _zoom = Math.Clamp(Math.Min(Bounds.Width / worldW, Bounds.Height / worldH) * (1.0 - 2 * pad),
                           MinZoom, MaxZoom);
        _panX = m.BbMinX - (Bounds.Width  - worldW * _zoom) / (2 * _zoom);
        _panY = m.BbMinY - (Bounds.Height - worldH * _zoom) / (2 * _zoom);
        _fitted = true;
        return true;
    }

    private (double X, double Y) ToWorld(Point p) => (p.X / _zoom + _panX, p.Y / _zoom + _panY);

    // ── The zoom box (owner instruction, 2026-09-19) ─────────────────────────

    /// <summary>
    /// Raised whenever <see cref="ZoomBoxArmed"/> changes, so the toolbar button that armed it can
    /// follow.
    /// </summary>
    /// <remarks>
    /// <b>The button follows the canvas and not the other way round.</b> <c>Z</c> arms it and
    /// Escape, a completed box and a lost capture all disarm it, none of which goes through the
    /// button — a toolbar that only lit up when it was clicked would be wrong exactly when the
    /// keyboard was used. The layout editor's own seam, name for name.
    /// </remarks>
    public event EventHandler? ZoomBoxArmedChanged;

    /// <summary>True while the next left-drag will draw a zoom box rather than select an element.</summary>
    public bool ZoomBoxArmed => _zoomBoxArmed;

    /// <summary>Takes the left button for one box.</summary>
    public void ArmZoomBox()
    {
        if (_zoomBoxArmed) return;
        _zoomBoxArmed    = true;
        _zoomBoxDragging = false;
        Cursor           = new Cursor(StandardCursorType.Cross);
        ZoomBoxArmedChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Gives it back. Idempotent, which is what lets every exit call it.</summary>
    public void DisarmZoomBox()
    {
        if (!_zoomBoxArmed && !_zoomBoxDragging) return;
        _zoomBoxArmed    = false;
        _zoomBoxDragging = false;

        // THE ORDINARY ARROW, which is this pane's own cursor (R-smith6-1): a crosshair advertises a
        // placement gesture the strip does not have, so it belongs to the armed state only.
        Cursor = new Cursor(StandardCursorType.Arrow);
        ZoomBoxArmedChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>
    /// Frames the world rectangle the box just drew.
    /// </summary>
    /// <remarks>
    /// <b>It is <see cref="Fit"/>'s arithmetic with a rectangle handed in</b> rather than the
    /// drawing's own extent — one zoom rule, so a box and a fit cannot disagree about padding or
    /// about the zoom clamp. A box smaller than a few pixels is a click and frames nothing.
    /// </remarks>
    private void ZoomToBox()
    {
        var box = new Rect(_zoomBoxStart, _zoomBoxCurrent);
        if (box.Width < ZoomBoxMinDragPixels || box.Height < ZoomBoxMinDragPixels) return;
        if (Bounds.Width < 1 || Bounds.Height < 1) return;

        var (x0, y0) = ToWorld(box.TopLeft);
        var (x1, y1) = ToWorld(box.BottomRight);

        double worldW = Math.Max(x1 - x0, 1e-9);
        double worldH = Math.Max(y1 - y0, 1e-9);

        _zoom = Math.Clamp(Math.Min(Bounds.Width / worldW, Bounds.Height / worldH), MinZoom, MaxZoom);
        _panX = x0 - (Bounds.Width  - worldW * _zoom) / (2 * _zoom);
        _panY = y0 - (Bounds.Height - worldH * _zoom) / (2 * _zoom);
        _fitted = true;
    }

    // ── Gestures ─────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;

        // Zoom about the pointer, not the pane's centre: the point under the cursor is the one the
        // user is looking at and it must not move.
        var p = e.GetPosition(this);
        var (wx, wy) = ToWorld(p);
        double next = Math.Clamp(_zoom * (e.Delta.Y > 0 ? ZoomStep : 1.0 / ZoomStep), MinZoom, MaxZoom);
        if (Math.Abs(next - _zoom) < double.Epsilon) return;

        _zoom  = next;
        _panX  = wx - p.X / _zoom;
        _panY  = wy - p.Y / _zoom;
        _fitted = true;
        e.Handled = true;
        InvalidateVisual();
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var props = e.GetCurrentPoint(this).Properties;

        if (props.IsMiddleButtonPressed)
        {
            _panFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        Focus();

        // THE ARMED BOX TAKES THE LEFT BUTTON, before the hit test — otherwise a press that landed
        // on a symbol would select it and the box would never start, which is the one place the two
        // gestures could collide.
        if (_zoomBoxArmed)
        {
            _zoomBoxDragging = true;
            _zoomBoxStart    = e.GetPosition(this);
            _zoomBoxCurrent  = _zoomBoxStart;
            e.Pointer.Capture(this);
            e.Handled = true;
            InvalidateVisual();
            return;
        }

        if (_vm is null) return;

        var point = e.GetPosition(this);
        var (wx, wy) = ToWorld(point);

        var hit = SchematicHitTest.Test(_projection.Edit, _projection.Model, _projection.Index,
                                       wx, wy, zoom: _zoom);

        // Selection by click, ON THE SYMBOL OR ITS LABEL (R-smith6-2) — which is what the editor's own
        // hit-test answers, in its own Z order, rather than a box test written here.
        string? id = hit.Kind is SchematicHitTest.HitKind.Component
                              or SchematicHitTest.HitKind.ComponentType
                              or SchematicHitTest.HitKind.ComponentName
                              or SchematicHitTest.HitKind.ComponentParam
                     ? hit.Id : null;

        if (_vm.SelectByComponentId(id))
        {
            _dragFromIndex = _vm.SelectedElementIndex;
            _dragToIndex   = _dragFromIndex;
            _dragOrigin    = point;
            _dragging      = false;
            e.Pointer.Capture(this);
        }
        else
        {
            // A PRESS ON THE BACKGROUND DROPS BOTH SELECTIONS (owner instruction, 2026-09-19) — the
            // strip's element AND the chart's markers, which is the same pair Escape drops and the
            // same command, so the two gestures cannot come to mean different things.
            //
            // It used to drop neither: SelectByComponentId answers false for a press that hit
            // nothing and simply returned, so a part stayed outlined and a marker stayed selected
            // while the user clicked elsewhere to say they were done with it. Deselecting by
            // clicking away is the gesture every canvas in this application already has.
            //
            // "Nothing" here means nothing SELECTABLE — the empty canvas, and equally the generator,
            // a ground glyph or the load pin, none of which is an element and none of which this
            // strip can select.
            _vm.ClearSelection();
        }

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_zoomBoxDragging)
        {
            _zoomBoxCurrent = e.GetPosition(this);
            InvalidateVisual();
            return;
        }

        if (_panFrom is { } from)
        {
            var to = e.GetPosition(this);
            _panX -= (to.X - from.X) / _zoom;
            _panY -= (to.Y - from.Y) / _zoom;
            _panFrom = to;
            _fitted  = true;
            InvalidateVisual();
            ViewportChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_dragFromIndex < 0 || _vm is null) return;

        var p = e.GetPosition(this);
        if (!_dragging
         && Math.Abs(p.X - _dragOrigin.X) < DragThresholdPixels
         && Math.Abs(p.Y - _dragOrigin.Y) < DragThresholdPixels)
            return;

        int slot = TargetIndexAt(p);
        if (!_dragging || slot != _dragToIndex)
        {
            _dragToIndex = slot;
            _dragPreview = _vm.BuildReorderPreview(_dragFromIndex, _dragToIndex);
        }

        _dragging = true;
        InvalidateVisual();
    }

    /// <summary>
    /// Which slot the pointer is over.
    /// </summary>
    /// <remarks>
    /// Columns sit at <c>±(k+1)·Pitch</c> from the generator at the origin, so the slot is one
    /// rounding — and taking the pointer's x through the mirror first is what makes the same arithmetic
    /// serve both directions (<c>R-smith6-6</c>: the flag reaches only the projection).
    /// </remarks>
    private int TargetIndexAt(Point p)
    {
        if (_vm is null) return _dragFromIndex;

        var (wx, _) = ToWorld(p);
        double u = _vm.MirrorNetwork ? -wx : wx;
        int slot = (int)Math.Round(u / SmithNetworkModel.Pitch) - 1;
        return Math.Clamp(slot, 0, Math.Max(0, _vm.Network.ColumnX.Count - 1));
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _panFrom = null;

        if (_zoomBoxDragging)
        {
            _zoomBoxCurrent = e.GetPosition(this);
            ZoomToBox();
            ViewportChanged?.Invoke(this, EventArgs.Empty);

            // ONE BOX PER ARM, exactly as the schematic and layout editors do it: the tool goes back
            // to Select afterwards rather than staying latched, because the common case is one
            // zoom and a latched magnifier then swallows the next selection click.
            DisarmZoomBox();
            e.Pointer.Capture(null);
            return;
        }

        if (_dragging && _vm is not null && _dragToIndex != _dragFromIndex)
            _vm.MoveElement(_dragFromIndex, _dragToIndex);

        EndDrag();
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panFrom = null;

        // A box whose capture was taken away never gets its release, so the latch would stay set and
        // the next ordinary click would draw one. Same reason the pan latch is cleared above.
        DisarmZoomBox();

        // A capture lost without a release — the window losing focus mid-drag — ABANDONS the reorder
        // rather than committing it. A move nobody finished asking for is not a move.
        EndDrag();
    }

    private void EndDrag()
    {
        _dragFromIndex = -1;
        _dragToIndex   = -1;
        _dragging      = false;
        _dragPreview   = null;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F)                            { ZoomToFit();                   e.Handled = true; }
        else if (e.Key == Key.Z)                       { if (_zoomBoxArmed) DisarmZoomBox(); else ArmZoomBox(); e.Handled = true; }
        else if (e.Key == Key.M)                       { _vm?.ToggleMirrorCommand.Execute(null); e.Handled = true; }
        else if (e.Key is Key.Delete or Key.Back)      { _vm?.DeleteElementCommand.Execute(null); e.Handled = true; }

        // ESCAPE DROPS BOTH SELECTIONS (owner instruction, 2026-09-19) — this strip's element and
        // the chart's markers — because the window has two selections and one key. Left UNHANDLED
        // so the document's own Escape binding still runs when the focus is anywhere else; the
        // command is the same one either way, and it is idempotent.
        else if (e.Key == Key.Escape)
        {
            // ESCAPE CANCELS THE BOX FIRST, and only drops the selections when there is no box to
            // cancel — a half-drawn marquee is the more recent gesture, and Escape means "undo what
            // I am in the middle of" before it means anything else.
            if (_zoomBoxArmed || _zoomBoxDragging) DisarmZoomBox();
            else _vm?.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ── The inline value editor (double-click a label) ───────────────────────

    /// <summary>
    /// Raised when a component, its name or one of its parameter labels is double-clicked. The HOST
    /// decides what, if anything, that edits — <see cref="SmithChartViewModel.ResolveInlineEdit"/>.
    /// </summary>
    public event EventHandler<SchematicHitTest.HitResult>? LabelDoubleTapped;

    /// <summary>
    /// Raised after any zoom, pan or re-fit, so an open editor can follow the label it sits on.
    /// </summary>
    public event EventHandler? ViewportChanged;

    /// <summary>
    /// <b>Double-click edits what was clicked</b>, the schematic page's own gesture on its own
    /// hit-test.
    /// </summary>
    /// <remarks>
    /// Zoomed far enough out, the renderer draws no labels, and an editor opened then would sit over
    /// text that is not on screen — so nothing opens. An armed zoom box owns the left button, double
    /// or not. The event is only marked handled when something took it.
    /// </remarks>
    protected override void OnDoubleTapped(TappedEventArgs e)
    {
        base.OnDoubleTapped(e);
        if (_vm is null || _zoomBoxArmed || _zoomBoxDragging) return;
        if (!SchematicRenderer.LabelsVisibleAt(_zoom)) return;

        var (wx, wy) = ToWorld(e.GetPosition(this));
        var hit = SchematicHitTest.Test(_projection.Edit, _projection.Model, _projection.Index,
                                       wx, wy, zoom: _zoom);

        if (hit.Kind is not (SchematicHitTest.HitKind.Component
                          or SchematicHitTest.HitKind.ComponentName
                          or SchematicHitTest.HitKind.ComponentParam))
            return;

        LabelDoubleTapped?.Invoke(this, hit);
        e.Handled = true;
    }

    /// <summary>
    /// Where the editable text of one label row starts, in control-local pixels, at the CURRENT zoom
    /// and pan — past the row's "<c>Z = </c>" prefix — or null when the row is no longer drawn.
    /// </summary>
    /// <remarks>
    /// The row geometry is <see cref="MatchSchematicLabels.Locate"/>'s, which reads the renderer's own
    /// <c>LabelRowGeometry</c> with the renderer's own arguments. This pane draws the same
    /// <c>SchematicModel</c> the Designer's does, so there is no second answer to write.
    /// </remarks>
    public (double X, double Y, double FontSize)? AnchorFor(string componentId, int row)
    {
        if (MatchSchematicLabels.Locate(_projection.Model, componentId, row) is not { } at) return null;
        return ((at.BaseX + at.PrefixWidth - _panX) * _zoom,
                (at.BaselineY - _panY) * _zoom,
                Controls.SchematicInlineEditBox.FontSizeAt(_zoom));
    }

    // ── Render ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 4 || Bounds.Height < 4) return;
        if (!_fitted) Fit();   // never ZoomToFit() — see Fit()'s own note on the render-pass crash

        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;

        // THE PREVIEW IS THE MODEL WHILE A DRAG IS IN FLIGHT, not an overlay on top of the committed
        // one: every element sits where the drop would put it, the spine is drawn in the gaps the
        // new order leaves, and each shunt tap's dot is on its own column. See
        // SmithChartViewModel.BuildReorderPreview.
        var drawn = (_dragging ? _dragPreview : null) ?? _projection;

        context.Custom(new Op(
            new Rect(Bounds.Size), drawn.Model, BuildOverlay(drawn),
            _panX, _panY, _zoom, SchematicRenderTheme.FromTheme(_theme, variant)));
    }

    /// <summary>
    /// The frame's transient chrome: the selection outline, and the zoom box while one is being
    /// dragged.
    /// </summary>
    /// <remarks>
    /// <b>Both are the renderer's own overlay features</b>, which is why this control draws nothing
    /// itself.
    ///
    /// <para><b>A reorder drag is NOT here any more.</b> It used to fill
    /// <c>ComponentDragPositions</c> with the dragged column's target x — the dragged part moved and
    /// nothing else did, so it slid on top of whatever already occupied that slot, and the spine
    /// wires and junction dots (which are not components and have no entry in that map) stayed on
    /// the pre-drag drawing. Both were owner-reported on 2026-09-19. The drag now draws a real
    /// projection of the reordered list instead; see <c>Render</c> and
    /// <see cref="SmithChartViewModel.BuildReorderPreview"/>.</para>
    /// </remarks>
    private SchematicOverlay BuildOverlay(SmithNetworkProjection drawn)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (SelectedIdIn(drawn) is { Length: > 0 } id) selected.Add(id);

        // THE ZOOM BOX IS THE RENDERER'S OWN RUBBER BAND, not a rectangle drawn here — the same
        // overlay field the schematic editor's zoom box fills, so the two look identical and this
        // control still draws nothing itself. It is in WORLD coordinates, which is what makes it
        // stay put under a wheel zoom mid-drag.
        if (_zoomBoxDragging)
        {
            var box = new Rect(_zoomBoxStart, _zoomBoxCurrent);
            var (bx0, by0) = ToWorld(box.TopLeft);
            var (bx1, by1) = ToWorld(box.BottomRight);
            return new SchematicOverlay
            {
                SelectedComponentIds = selected,
                RubberBand           = (bx0, by0, bx1 - bx0, by1 - by0),
                RubberBandCrossing   = false,
            };
        }

        return new SchematicOverlay { SelectedComponentIds = selected };
    }

    /// <summary>
    /// The id of the selected element's component <b>in the projection being drawn</b>.
    /// </summary>
    /// <remarks>
    /// <b>Component ids are fresh on every build</b> (<c>EditableComponent.Id</c> is a new GUID), so
    /// the view model's <c>SelectedComponentId</c> — which is resolved against the COMMITTED
    /// projection — matches nothing in a drag preview. Left unmapped, the selection outline simply
    /// vanished for the duration of every drag: the one moment the user most needs to see which part
    /// they are holding.
    ///
    /// <para>The preview's element order is the committed one with <c>from</c> lifted out and
    /// re-inserted at <c>to</c>, so the index mapping is that move and nothing more.</para>
    /// </remarks>
    private string? SelectedIdIn(SmithNetworkProjection drawn)
    {
        if (_vm is null) return null;
        if (ReferenceEquals(drawn, _projection)) return _vm.SelectedComponentId;

        int index = _vm.SelectedElementIndex;
        if (index < 0) return null;

        int from = _dragFromIndex, to = _dragToIndex;
        int moved = index == from                   ? to
                  : from < index && index <= to     ? index - 1
                  : to   <= index && index < from   ? index + 1
                  : index;

        foreach (var (id, k) in drawn.ElementIndexByComponentId)
            if (k == moved) return id;
        return null;
    }

    private sealed class Op(
        Rect bounds, SchematicModel model, SchematicOverlay overlay,
        double panX, double panY, double zoom, SchematicRenderTheme theme) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point p) => bounds.Contains(p);
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;
            using var l = lease.Lease();

            SchematicRenderer.Draw(l.SkCanvas, (bounds.Width, bounds.Height), model, null,
                                   panX, panY, zoom, theme, overlay);
        }
    }
}
