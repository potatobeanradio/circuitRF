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

    // Reorder drag state.
    private int    _dragFromIndex = -1;
    private int    _dragToIndex   = -1;
    private Point  _dragOrigin;
    private bool   _dragging;

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

        if (!props.IsLeftButtonPressed || _vm is null) return;

        Focus();

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

        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_panFrom is { } from)
        {
            var to = e.GetPosition(this);
            _panX -= (to.X - from.X) / _zoom;
            _panY -= (to.Y - from.Y) / _zoom;
            _panFrom = to;
            _fitted  = true;
            InvalidateVisual();
            return;
        }

        if (_dragFromIndex < 0 || _vm is null) return;

        var p = e.GetPosition(this);
        if (!_dragging
         && Math.Abs(p.X - _dragOrigin.X) < DragThresholdPixels
         && Math.Abs(p.Y - _dragOrigin.Y) < DragThresholdPixels)
            return;

        _dragging = true;
        _dragToIndex = TargetIndexAt(p);
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

        // A capture lost without a release — the window losing focus mid-drag — ABANDONS the reorder
        // rather than committing it. A move nobody finished asking for is not a move.
        EndDrag();
    }

    private void EndDrag()
    {
        _dragFromIndex = -1;
        _dragToIndex   = -1;
        _dragging      = false;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F)                            { ZoomToFit();                   e.Handled = true; }
        else if (e.Key == Key.M)                       { _vm?.ToggleMirrorCommand.Execute(null); e.Handled = true; }
        else if (e.Key is Key.Delete or Key.Back)      { _vm?.DeleteElementCommand.Execute(null); e.Handled = true; }
    }

    // ── Render ───────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width < 4 || Bounds.Height < 4) return;
        if (!_fitted) Fit();   // never ZoomToFit() — see Fit()'s own note on the render-pass crash

        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;

        context.Custom(new Op(
            new Rect(Bounds.Size), _projection.Model, BuildOverlay(),
            _panX, _panY, _zoom, SchematicRenderTheme.FromTheme(_theme, variant)));
    }

    /// <summary>
    /// The frame's transient chrome: the selection outline, and — while a reorder drag is in flight —
    /// the dragged column's live position.
    /// </summary>
    /// <remarks>
    /// <b>Both are the renderer's own overlay features</b>, which is why this control draws nothing
    /// itself. <c>ComponentDragPositions</c> exists so a live drag bypasses a full model rebuild, and it
    /// is exactly what a reorder preview wants: the element follows the slot it would land in, and the
    /// list is not touched until the pointer is released.
    /// </remarks>
    private SchematicOverlay BuildOverlay()
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        if (_vm?.SelectedComponentId is { Length: > 0 } id) selected.Add(id);

        if (!_dragging || _vm is null || _dragFromIndex < 0)
            return new SchematicOverlay { SelectedComponentIds = selected };

        // The dragged COLUMN moves to the slot it would take; everything else stays where it is, so
        // what the user sees is the gap the drop would leave.
        //
        // The column, not the component: a shunt arm carries its own Ground glyph, which is a separate
        // component and would otherwise stay behind on the old x while the part it grounds slid away.
        // It is found by NAME rather than by the element map, which deliberately holds only the parts
        // that ARE elements.
        double dir     = _vm.MirrorNetwork ? -1.0 : +1.0;
        double targetX = dir * (_dragToIndex + 1) * SmithNetworkModel.Pitch;

        string? dragged = _projection.Model.Components
            .FirstOrDefault(c => _projection.ElementIndexByComponentId.TryGetValue(c.Id, out int k)
                              && k == _dragFromIndex)?.InstanceName;
        if (dragged is null) return new SchematicOverlay { SelectedComponentIds = selected };

        string ground = dragged + SmithNetworkModel.GroundNameSuffix;

        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        foreach (var c in _projection.Model.Components)
            if (c.InstanceName == dragged || c.InstanceName == ground)
                positions[c.Id] = (targetX, c.Y);

        return new SchematicOverlay
        {
            SelectedComponentIds   = selected,
            ComponentDragPositions = positions,
        };
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
