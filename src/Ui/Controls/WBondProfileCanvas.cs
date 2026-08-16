using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using SkiaSharp;

namespace CircuitRF.Ui.Controls;

/// <summary>
/// The profile view (wbond.md §6.1/§6.2): span across, <b>z always up</b>.
///
/// <h3>Its own viewport, deliberately</h3>
/// <para>It shows a different projection of the same wires, at a different scale — a 100 mil span
/// against a 20 mil loop — so sharing the layout canvas's pan/zoom would make one of the two views
/// useless. WB22a's thickness mode is likewise per-view for the same reason.</para>
///
/// <h3>What a drag means here</h3>
/// <para>A plain drag moves the selection in <b>both</b> axes of this view: vertically in z, and
/// horizontally <b>along each wire's own XY chord</b> — so a wire running north-south moves in y and
/// one running east-west moves in x, which is what the horizontal axis of this view means
/// (<see cref="WireEdits.Translate"/> owns that mapping). An earlier version offered z only, on the
/// grounds that span is a derived coordinate; the owner's answer is that a point you can see move
/// sideways under the pointer has to actually move, and the chord direction is the single answer the
/// argument said did not exist.</para>
/// <para>Alt-drag is unchanged and still means something different: it SCALES the whole bound array
/// by a factor (WB24b) rather than translating the selection.</para>
/// </summary>
public sealed class WBondProfileCanvas : Control
{
    // ── DirectProperty: ViewModel ─────────────────────────────────────────────

    public static readonly DirectProperty<WBondProfileCanvas, WBondViewModel?> ViewModelProperty =
        AvaloniaProperty.RegisterDirect<WBondProfileCanvas, WBondViewModel?>(
            nameof(ViewModel), o => o.ViewModel, (o, v) => o.ViewModel = v);

    private WBondViewModel? _viewModel;
    private WBondPointerController? _controller;

    public WBondViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            if (_viewModel is not null) _viewModel.ReadoutChanged -= OnReadoutChanged;

            SetAndRaise(ViewModelProperty, ref _viewModel, value);
            _controller = value is null ? null : new WBondPointerController(value);

            if (_viewModel is not null) _viewModel.ReadoutChanged += OnReadoutChanged;

            _needsInitialFit = true;
            InvalidateVisual();
        }
    }

    private void OnReadoutChanged() => InvalidateVisual();

    public WBondRenderTheme WireTheme { get; set; } = WBondRenderTheme.Fallback;

    /// <summary>
    /// The layout theme, for the grid and the marquee — resolved exactly as <c>LayoutCanvas</c>
    /// resolves its own, so the two canvases sitting one above the other cannot disagree about what
    /// the grid or a selection box looks like. Settable, for callers with no visual tree.
    /// </summary>
    public LayoutRenderTheme LayoutTheme { get; set; } = LayoutRenderTheme.Light;

    private void OnThemeChanged(object? sender, EventArgs e) => RefreshTheme();

    private void RefreshTheme()
    {
        var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        LayoutTheme = LayoutRenderTheme.FromTheme(ThemeService.Active, variant);

        // The WIRE colours follow the variant too, and did not before — this canvas drew the same
        // hardcoded dark palette in light mode, which is where the invisible white selection came from.
        WireTheme = WBondRenderTheme.FromTheme(ThemeService.Active, variant);

        ThemeRefreshed?.Invoke();
        InvalidateVisual();
    }

    /// <summary>
    /// Raised after <see cref="WireTheme"/> has been re-resolved, so the hosting view can push the
    /// same colours into the LAYOUT overlay — which draws the same wires and must not disagree about
    /// them. The overlay is not a control and has no theme notifications of its own.
    /// </summary>
    public event Action? ThemeRefreshed;

    /// <summary>Per-view, per WB22a.</summary>
    public WireThicknessMode Thickness { get; set; } = WireThicknessMode.ConstantPixels;

    public ProfileProjection.SpanMode SpanMode { get; set; } = ProfileProjection.SpanMode.Absolute;

    /// <summary>
    /// The plane this view projects onto — null for AUTO (each wire on its own chord), 0 for XZ,
    /// π/2 for YZ, anything for a diagonal. Pushed in from the toolbar's own combo.
    ///
    /// <para>Every gesture in this control reads it: the render, the hit test, the marquee and the
    /// horizontal drag. One value, so a point cannot be drawn in one place and moved in another.</para>
    /// </summary>
    public double? Azimuth { get; set; }

    /// <summary>
    /// The grid pitch in nanometres, or 0 for no grid — the same snap distance the layout view's own
    /// grid is drawn from, pushed in by the view so one Snap box governs both canvases.
    /// </summary>
    public long GridPitchNm { get; set; }

    // ── Viewport (span across, z up) ──────────────────────────────────────────

    private double _panSpan;   // span at the left edge
    private double _panZ;      // z at the bottom edge
    private double _zoom = 1e-3;

    private const double ZoomFactor = 1.15;
    private const double MinZoom = 1e-12;
    private const double MaxZoom = 1e6;
    private const double HitTolerancePixels = 5.0;

    private bool _needsInitialFit = true;
    private bool _isPanning;
    private Point _panStartScreen;
    private double _panStartSpan, _panStartZ;

    public WBondProfileCanvas()
    {
        Focusable = true;
        ClipToBounds = true;

        AttachedToVisualTree += (_, _) => { ThemeService.ThemeChanged += OnThemeChanged; RefreshTheme(); };
        DetachedFromVisualTree += (_, _) => ThemeService.ThemeChanged -= OnThemeChanged;
        ActualThemeVariantChanged += (_, _) => RefreshTheme();

        PointerPressed  += OnPointerPressedInternal;
        PointerMoved    += OnPointerMovedInternal;
        PointerReleased += OnPointerReleasedInternal;
        PointerWheelChanged += OnPointerWheelInternal;
        KeyDown += OnKeyDownInternal;
        LayoutUpdated += OnLayoutUpdated;
        ContextRequested += OnContextRequested;
    }

    /// <summary>
    /// Records WHICH group the pointer is over and lets the framework open the declared menu.
    ///
    /// <para>Deliberately does not mark the event handled: Avalonia's own right-click gesture is what
    /// opens the <c>ContextMenu</c> declared in XAML, and consuming the event here would suppress
    /// it.</para>
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        _contextMenuTargetArray = e.TryGetPosition(this, out var p) ? HitTestArray(p) : -1;
    }

    /// <summary>
    /// The array a right-click landed on, recorded by <see cref="ContextRequested"/> and consumed
    /// once by the view that owns the menu.
    ///
    /// <para>Recorded here and built there, exactly like <c>LayoutCanvas.ContextMenuTarget</c>: this
    /// control knows what is under the pointer, and the view knows what the commands are. A context
    /// menu constructed and opened by hand per right-click is the stacking bug this codebase has
    /// already fixed twice — the menu is declared once in XAML and rebuilt on <c>Opening</c>.</para>
    /// </summary>
    private int _contextMenuTargetArray = -1;

    /// <summary>Reads and CLEARS the recorded right-click target, so a stale one can never leak.</summary>
    public int ConsumeContextMenuTargetArray()
    {
        int t = _contextMenuTargetArray;
        _contextMenuTargetArray = -1;
        return t;
    }

    /// <summary>
    /// Which ARRAY the given screen point lands on, or −1 for empty space.
    ///
    /// <para>The profile view draws one curve per array (§6.4), so the thing under the pointer here
    /// is a group — which is what makes a group-scoped context menu the natural gesture for this view
    /// and a selection-scoped one wrong for it.</para>
    ///
    /// <para>Resolution goes through the SAME <see cref="WireHitTest.HitTestProfile"/> and the same
    /// tolerance a left-click already uses, then maps the hit wire to its owning array — so a
    /// right-click can never disagree with a left-click about what is under the pointer.</para>
    /// </summary>
    public int HitTestArray(Point screenPoint)
    {
        if (_viewModel is null) return -1;

        var mesh = _viewModel.Mesh;
        if (mesh.WireCount == 0) return -1;

        double span = ScreenToSpan(screenPoint.X);
        double z = ScreenToZ(screenPoint.Y);

        // The tolerance is a PIXEL quantity converted at the CURRENT zoom, never cached — the same
        // rule every other hit test in this codebase follows.
        double tolNm = HitTolerancePixels / _zoom;

        var hit = WireHitTest.HitTestProfile(mesh, span, (long)Math.Round(z), tolNm, SpanMode,
                                             azimuthRadians: Azimuth);
        if (!hit.Found) return -1;

        return mesh.ArrayOfWire[hit.Wire];
    }

    private double SpanToScreen(double span) => (span - _panSpan) * _zoom;

    private double ZToScreen(double z) => Bounds.Height - (z - _panZ) * _zoom;

    private double ScreenToSpan(double x) => x / _zoom + _panSpan;

    private double ScreenToZ(double y) => (Bounds.Height - y) / _zoom + _panZ;

    private double HitToleranceNm => _zoom > 0 ? HitTolerancePixels / _zoom : 0;

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_needsInitialFit || _viewModel is null || Bounds.Width <= 1 || Bounds.Height <= 1) return;
        _needsInitialFit = false;
        ZoomToFit();
    }

    /// <summary>Frames every wire's projected extent with a margin.</summary>
    public void ZoomToFit()
    {
        if (_viewModel is null || Bounds.Width <= 1 || Bounds.Height <= 1) return;

        double minSpan = double.MaxValue, maxSpan = double.MinValue;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        bool any = false;

        foreach (var wire in _viewModel.Design.AllWires())
        {
            for (int i = 0; i < wire.Points.Count; i++)
            {
                var p = ProfileProjection.Project(wire, i, SpanMode, Azimuth);
                minSpan = Math.Min(minSpan, p.Span); maxSpan = Math.Max(maxSpan, p.Span);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
                any = true;
            }
        }

        if (!any) return;

        // A degenerate axis (one point, or a perfectly flat set) still has to produce a usable view
        // rather than an infinite zoom.
        double spanExtent = Math.Max(maxSpan - minSpan, 1.0);
        double zExtent = Math.Max(maxZ - minZ, 1.0);

        const double MarginFraction = 0.12;
        double zoom = Math.Min(Bounds.Width / (spanExtent * (1 + 2 * MarginFraction)),
                               Bounds.Height / (zExtent * (1 + 2 * MarginFraction)));

        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        _panSpan = minSpan - (Bounds.Width / _zoom - spanExtent) / 2.0;
        _panZ = minZ - (Bounds.Height / _zoom - zExtent) / 2.0;

        InvalidateVisual();
    }

    // ── Zoom, mirroring LayoutCanvas's own four commands ──────────────────────

    public void ZoomIn() => ZoomAtCenter(_zoom * ZoomFactor);

    public void ZoomOut() => ZoomAtCenter(_zoom / ZoomFactor);

    /// <summary>
    /// One device pixel per one tick of the given display unit — the same "actual size" definition
    /// <c>LayoutCanvas.Zoom1To1</c> uses, expressed in this view's own units (nanometres).
    /// </summary>
    public void Zoom1To1(WBondUnit unit)
    {
        long nmPerUnit = WBondUnits.NmPerUnit(unit);
        if (nmPerUnit > 0) ZoomAtCenter(1.0 / nmPerUnit);
    }

    private void ZoomAtCenter(double newZoom)
    {
        if (Bounds.Width <= 1 || Bounds.Height <= 1) return;

        double cx = Bounds.Width / 2.0, cy = Bounds.Height / 2.0;
        double spanUnder = ScreenToSpan(cx), zUnder = ScreenToZ(cy);

        _zoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        _panSpan = spanUnder - cx / _zoom;
        _panZ = zUnder - (Bounds.Height - cy) / _zoom;

        InvalidateVisual();
    }

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context) =>
        context.Custom(new ProfileDrawOperation(
            new Rect(Bounds.Size), _viewModel?.Design, WireTheme, LayoutTheme, SpanMode, Azimuth,
            // EffectiveSelection, never Selection: the live preview while any marquee is running — in
            // EITHER canvas — and the committed selection the rest of the time.
            _viewModel?.EffectiveSelection,
            SpanToScreen, ZToScreen,
            _marqueeActive ? MarqueeRect() : null,
            GridPitchNm, _zoom, _panSpan, _panZ));

    /// <summary>The live marquee in SCREEN coordinates, plus which way the hand went.</summary>
    private (Rect Box, bool Crossing) MarqueeRect()
    {
        double x0 = SpanToScreen(_marqueeStartSpan), x1 = SpanToScreen(_marqueeSpan);
        double y0 = ZToScreen(_marqueeStartZ), y1 = ZToScreen(_marqueeZ);

        return (new Rect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0)),
                _marqueeSpan < _marqueeStartSpan);
    }

    // ── Pointer ───────────────────────────────────────────────────────────────

    /// <summary>
    /// How far the pointer must travel before a press becomes a DRAG rather than a click.
    ///
    /// <para><b>This is a correctness threshold, not a comfort one.</b> Without it every click moved
    /// the grabbed point by whatever sub-pixel jitter the hand contributed between press and release,
    /// and — because a press opened a gesture — left an undo entry behind as well. Clicking a wire's
    /// input foot to select it therefore changed that wire's span, which is exactly what the owner
    /// reported.</para>
    /// </summary>
    private const double DragThresholdPixels = 3.0;

    private bool _pressed;              // a left press is down and may yet become a drag
    private bool _dragging;             // ...and has passed the threshold
    private double _lastZNm;
    private double _lastSpanNm;
    private double _pressScreenX, _pressScreenY;
    private double _dragStartSpan, _dragStartZ;
    private bool _altDrag;
    private bool _altMoveOutputFoot = true;
    private double _altReferenceSpan, _altReferenceHeight;
    private double _altSpanApplied = 1.0, _altHeightApplied = 1.0;

    /// <summary>A press that left the selection alone, held in case the gesture is a plain click.</summary>
    private (long Span, long Z, WBondModifiers Modifiers, int ClickCount)? _deferredPress;

    private bool _marqueeActive;
    private double _marqueeStartSpan, _marqueeStartZ, _marqueeSpan, _marqueeZ;
    private WireSelection _marqueeBase = new();
    private WBondModifiers _marqueeModifiers = WBondModifiers.None;

    /// <summary>
    /// What the live marquee would select, or null when none is in progress.
    ///
    /// <para>Held on the shared view-model (<c>WBondViewModel.PreviewSelection</c>), so a box dragged
    /// HERE highlights the same wires in the layout view too — a wire is a thing both views draw.</para>
    /// </summary>
    public WireSelection? MarqueePreview => _marqueeActive ? _viewModel?.PreviewSelection : null;

    private void OnPointerPressedInternal(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var props = e.GetCurrentPoint(this).Properties;
        var pos = e.GetPosition(this);

        if (props.IsMiddleButtonPressed)
        {
            _isPanning = true;
            _panStartScreen = pos;
            _panStartSpan = _panSpan;
            _panStartZ = _panZ;
            e.Pointer.Capture(this);
            return;
        }

        if (!props.IsLeftButtonPressed || _viewModel is null || _controller is null) return;

        double span = ScreenToSpan(pos.X);
        double z = ScreenToZ(pos.Y);
        var modifiers = Modifiers(e.KeyModifiers);

        _pressScreenX = pos.X;
        _pressScreenY = pos.Y;
        _dragStartSpan = span;
        _dragStartZ = z;
        _lastZNm = z;
        _lastSpanNm = span;

        // Is the thing under the pointer ALREADY selected? If so the press means "pick this selection
        // up", not "start a new one" — see PressKeepingSelection.
        var hit = WireHitTest.HitTestProfile(_viewModel.Mesh, span, (long)Math.Round(z),
                                             HitToleranceNm, SpanMode, azimuthRadians: Azimuth);

        bool keepSelection = hit.Found && !modifiers.HasFlag(WBondModifiers.Shift) && SelectionCovers(hit);

        // ...and if the press turns out to be a plain CLICK it is re-resolved on release instead —
        // the standard click-through, so an element inside a selected wire is still reachable.
        _deferredPress = keepSelection
            ? ((long)Math.Round(span), (long)Math.Round(z), modifiers, e.ClickCount)
            : null;

        if (!keepSelection)
            _controller.Press((long)Math.Round(span), (long)Math.Round(z), HitToleranceNm,
                              modifiers, e.ClickCount, EditorView.Profile);

        if (!hit.Found)
        {
            // Empty space: a marquee, exactly as the layout view's own overlay does it. The press has
            // already cleared the selection unless Shift was held, so this is the union base.
            _marqueeActive = true;
            _marqueeStartSpan = _marqueeSpan = span;
            _marqueeStartZ = _marqueeZ = z;
            _marqueeModifiers = modifiers;
            _marqueeBase = _viewModel.Selection;
            _viewModel.PreviewSelection = _marqueeBase;
            InvalidateVisual();
            return;
        }

        if (_viewModel.Selection.IsEmpty) { InvalidateVisual(); return; }

        // Armed, not yet dragging. Nothing is committed and no undo entry is pushed until the pointer
        // clears DragThresholdPixels — a click must not move geometry.
        _pressed = true;
        _altDrag = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        _altSpanApplied = _altHeightApplied = 1.0;
        _altReferenceSpan = ReferenceSpanNm();
        _altReferenceHeight = ReferenceHeightNm();

        // The pinned foot is the one FURTHER from the grab — grabbing near an end IS the instruction
        // to move that end (WB26a's rule, reused here so alt-drag needs no mode switch either).
        _altMoveOutputFoot = GrabMovesOutputFoot(hit);

        InvalidateVisual();
    }

    /// <summary>
    /// Whether the current selection already covers what the pointer is over.
    ///
    /// <para><b>This is what makes a selection draggable.</b> The press used to re-resolve the hit
    /// unconditionally, so pressing on three selected segments to move them collapsed the selection to
    /// the one element under the cursor and then dragged only that — "clicking on the selection starts
    /// a new selection", as reported. Shift still re-resolves, because extending a selection is the
    /// one case where a press on something already selected means something else.</para>
    /// </summary>
    private bool SelectionCovers(WireHitTest.Hit hit)
    {
        if (_viewModel is null) return false;

        var selection = _viewModel.Selection;
        if (selection.Wires.Contains(hit.Wire)) return true;
        if (selection.Points.Contains(new PointRef(hit.Wire, hit.Point))) return true;

        return hit.IsSegment && selection.Segments.Contains(new SegmentRef(hit.Wire, hit.Point));
    }

    /// <summary>
    /// Which foot an alt-drag should MOVE — the one the grab landed nearer, with the far one pinned
    /// (WB26a's rule). Stated as "which one moves" because that is what <c>ScaleSpan</c> takes.
    /// </summary>
    private bool GrabMovesOutputFoot(WireHitTest.Hit hit)
    {
        if (_viewModel is null) return true;

        var wires = _viewModel.Design.AllWires().ToList();
        if (hit.Wire < 0 || hit.Wire >= wires.Count) return true;

        int last = wires[hit.Wire].Points.Count - 1;
        return last > 0 && hit.Point > last / 2.0;
    }

    private void OnPointerMovedInternal(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);

        if (_isPanning)
        {
            _panSpan = _panStartSpan - (pos.X - _panStartScreen.X) / _zoom;
            _panZ = _panStartZ + (pos.Y - _panStartScreen.Y) / _zoom;
            InvalidateVisual();
            return;
        }

        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (_marqueeActive)
        {
            if (!leftDown) { EndMarquee(); InvalidateVisual(); return; }

            _marqueeSpan = ScreenToSpan(pos.X);
            _marqueeZ = ScreenToZ(pos.Y);
            _viewModel!.PreviewSelection = ResolveMarqueeNow();
            InvalidateVisual();
            return;
        }

        if (!_pressed || _viewModel is null || _controller is null) return;
        if (!leftDown) { EndDrag(); return; }

        if (!_dragging)
        {
            double moved = Math.Max(Math.Abs(pos.X - _pressScreenX), Math.Abs(pos.Y - _pressScreenY));
            if (moved < DragThresholdPixels) return;

            // The threshold is crossed: NOW open the gesture, so a click leaves no undo entry.
            _dragging = true;
            _viewModel.BeginGesture();
            _controller.BeginDrag();
        }

        double span = ScreenToSpan(pos.X);
        double z = ScreenToZ(pos.Y);

        if (_altDrag) { AltDragFrame(span, z); return; }

        long dz = (long)Math.Round(z - _lastZNm);
        long dSpan = (long)Math.Round(span - _lastSpanNm);
        if (dz == 0 && dSpan == 0) return;

        _lastZNm += dz;
        _lastSpanNm += dSpan;

        // ONE Translate call, so the horizontal and vertical components of a diagonal drag land in the
        // same frame — two calls would make the quality ladder time half a gesture as a whole one.
        _controller.DragFrame(
            _ => WireEdits.Translate(_viewModel.Design, _viewModel.Selection, dSpan, dz,
                                     EditorView.Profile, Azimuth));

        InvalidateVisual();
    }

    /// <summary>
    /// Alt-drag: scale the selection's span AND height, live, as the hand moves (WB24a/b/c).
    ///
    /// <para><b>Both axes, every frame.</b> An earlier version declared one axis on the first few
    /// pixels of travel and ignored the other for the rest of the gesture, so a diagonal alt-drag
    /// silently did half of what it looked like. Each axis is measured independently against its own
    /// reference, so a purely vertical or purely horizontal drag still changes only that quantity.</para>
    ///
    /// <para>The span anchor is the foot FURTHER from the grab, decided once at press.</para>
    /// </summary>
    private void AltDragFrame(double span, double z)
    {
        if (_viewModel is null || _controller is null) return;

        // Grabbing near the INPUT foot moves that foot, so pulling the cursor BACKWARDS along the axis
        // is what lengthens the wire — the sign has to follow the anchor or the wire shrinks when the
        // hand says grow. The layout overlay's own alt-drag has carried this flip since it was
        // written; this one did not, which is half of the "wrong anchor" the owner saw here.
        double dSpan = span - _dragStartSpan;
        if (!_altMoveOutputFoot) dSpan = -dSpan;

        double spanFactor = FrameFactor(_altReferenceSpan, dSpan, ref _altSpanApplied);
        double heightFactor = FrameFactor(_altReferenceHeight, z - _dragStartZ, ref _altHeightApplied);

        if (spanFactor == 1.0 && heightFactor == 1.0) return;

        _controller.DragFrame(_ => _viewModel.ScaleSelection(spanFactor, heightFactor, _altMoveOutputFoot));
        InvalidateVisual();
    }

    /// <summary>
    /// The factor to apply THIS frame for one axis: the target factor since the drag began, divided by
    /// what has already been applied. Returns exactly 1.0 when the axis has no usable reference (a
    /// flat wire has no height to scale) or has not moved, so the caller can skip it.
    /// </summary>
    private static double FrameFactor(double reference, double delta, ref double applied)
    {
        if (reference <= 0) return 1.0;

        // A non-positive factor would fold the array through itself; clamp rather than refuse, so the
        // drag stays live and the user simply cannot push it past flat.
        double target = Math.Max((reference + delta) / reference, 1e-3);

        double frame = target / applied;
        if (Math.Abs(frame - 1.0) < 1e-9) return 1.0;

        applied = target;
        return frame;
    }

    private void OnPointerReleasedInternal(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning) { _isPanning = false; e.Pointer.Capture(null); return; }

        if (_marqueeActive)
        {
            var pos = e.GetPosition(this);
            _marqueeSpan = ScreenToSpan(pos.X);
            _marqueeZ = ScreenToZ(pos.Y);

            var resolved = ResolveMarqueeNow();
            EndMarquee();

            if (_viewModel is not null) _viewModel.Selection = resolved;
            InvalidateVisual();
            return;
        }

        if (_pressed) EndDrag();
    }

    /// <summary>The live preview, resolved by the same rule the release commits.</summary>
    private WireSelection ResolveMarqueeNow()
    {
        if (_viewModel is null) return new WireSelection();

        var direction = _marqueeSpan < _marqueeStartSpan
            ? MarqueeDirection.RightToLeft
            : MarqueeDirection.LeftToRight;

        // The marquee's own axes are THIS view's — span and z — so the resolver is handed the same
        // projection the curves were drawn with rather than left to fall back on world x.
        var resolved = SelectionResolver.ResolveMarquee(
            _viewModel.Mesh,
            (long)Math.Round(_marqueeStartSpan), (long)Math.Round(_marqueeStartZ),
            (long)Math.Round(_marqueeSpan), (long)Math.Round(_marqueeZ),
            direction, EditorView.Profile,
            spanOf: (wire, index) =>
                (long)Math.Round(ProfileProjection.Project(_viewModel.Mesh.Wires[wire], index,
                                                           SpanMode, Azimuth).Span));

        return _marqueeModifiers.HasFlag(WBondModifiers.Shift)
            ? SelectionResolver.Union(_marqueeBase, resolved)
            : resolved;
    }

    private void EndMarquee()
    {
        _marqueeActive = false;
        if (_viewModel is not null) _viewModel.PreviewSelection = null;
    }

    private void EndDrag()
    {
        bool wasDragging = _dragging;

        _pressed = false;
        _dragging = false;

        // A press that never crossed the threshold opened nothing, so there is nothing to close —
        // calling EndDrag on the controller would publish a spurious final answer for a plain click.
        if (wasDragging)
        {
            _controller?.EndDrag();
            _viewModel?.EndGesture();
        }
        else if (_deferredPress is { } press && _controller is not null)
        {
            // It was a click on an already-selected thing after all: resolve it now.
            _controller.Press(press.Span, press.Z, HitToleranceNm, press.Modifiers, press.ClickCount,
                              EditorView.Profile);
        }

        _deferredPress = null;
        InvalidateVisual();
    }

    private void OnPointerWheelInternal(object? sender, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        double spanUnder = ScreenToSpan(pos.X);
        double zUnder = ScreenToZ(pos.Y);

        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? ZoomFactor : 1.0 / ZoomFactor), MinZoom, MaxZoom);

        // Keep whatever was under the cursor under the cursor.
        _panSpan = spanUnder - pos.X / _zoom;
        _panZ = zUnder - (Bounds.Height - pos.Y) / _zoom;

        InvalidateVisual();
        e.Handled = true;
    }

    private void OnKeyDownInternal(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        if (e.Key == Key.Escape)
        {
            _viewModel.Selection = new WireSelection();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F) { ZoomToFit(); e.Handled = true; return; }

        if (_viewModel.Selection.IsEmpty) return;

        bool coarse = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        var (dx, dz) = e.Key switch
        {
            Key.Left  => (-1, 0),
            Key.Right => (1, 0),
            Key.Down  => (0, -1),
            Key.Up    => (0, 1),      // +z in the profile view (§6.3)
            _         => (0, 0),
        };

        if (dx == 0 && dz == 0) return;

        _viewModel.NudgeSelection(dx, dz, coarse, EditorView.Profile);
        InvalidateVisual();
        e.Handled = true;
    }

    private static WBondModifiers Modifiers(KeyModifiers modifiers)
    {
        var result = WBondModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0) result |= WBondModifiers.Shift;
        if ((modifiers & KeyModifiers.Alt) != 0) result |= WBondModifiers.Alt;
        return result;
    }

    private string? SelectedProfileName()
    {
        if (_viewModel is null) return null;
        var wires = _viewModel.Design.AllWires().ToList();

        foreach (int index in _viewModel.Selection.TouchedWires())
            if (index >= 0 && index < wires.Count && wires[index].ProfileBinding is { } binding)
                return binding;

        return null;
    }

    /// <summary>
    /// The quantity a height scale is measured against — the bound profile's loop height, or, for a
    /// wire that follows no profile, the wire's own max-minus-min z.
    ///
    /// <para>The fallback is what makes alt-drag work on a detached wire at all: reading the profile
    /// alone returned zero there, and a zero reference silently disables the whole gesture.</para>
    /// </summary>
    private double ReferenceHeightNm()
    {
        if (_viewModel is null) return 0;

        if (SelectedProfileName() is { } name &&
            _viewModel.Design.ProfileByName(name)?.LoopHeightNm is { } bound && bound > 0)
            return bound;

        var wires = _viewModel.Design.AllWires().ToList();
        foreach (int index in _viewModel.Selection.TouchedWires())
        {
            if (index < 0 || index >= wires.Count) continue;
            if (wires[index].LoopHeightNm is > 0 and var h) return h;
        }

        return 0;
    }

    /// <summary>The quantity a span scale is measured against — the selected wire's own chord.</summary>
    private double ReferenceSpanNm()
    {
        if (_viewModel is null) return 0;
        var wires = _viewModel.Design.AllWires().ToList();

        foreach (int index in _viewModel.Selection.TouchedWires())
        {
            if (index < 0 || index >= wires.Count) continue;
            var wire = wires[index];
            if (wire.Points.Count < 2) continue;

            var last = ProfileProjection.Project(wire, wire.Points.Count - 1, SpanMode, Azimuth);
            var first = ProfileProjection.Project(wire, 0, SpanMode, Azimuth);
            double chord = Math.Abs(last.Span - first.Span);
            if (chord > 0) return chord;
        }

        return 0;
    }

    // ── ICustomDrawOperation ──────────────────────────────────────────────────

    private sealed class ProfileDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly WBondDesign? _design;
        private readonly WBondRenderTheme _theme;
        private readonly LayoutRenderTheme _layoutTheme;
        private readonly ProfileProjection.SpanMode _mode;
        private readonly double? _azimuth;
        private readonly WireSelection? _selection;
        private readonly Func<double, double> _spanToScreen;
        private readonly Func<double, double> _zToScreen;
        private readonly (Rect Box, bool Crossing)? _marquee;
        private readonly long _gridPitchNm;
        private readonly double _zoom, _panSpan, _panZ;

        public ProfileDrawOperation(Rect bounds, WBondDesign? design, WBondRenderTheme theme,
                                    LayoutRenderTheme layoutTheme,
                                    ProfileProjection.SpanMode mode, double? azimuth,
                                    WireSelection? selection,
                                    Func<double, double> spanToScreen, Func<double, double> zToScreen,
                                    (Rect Box, bool Crossing)? marquee,
                                    long gridPitchNm, double zoom, double panSpan, double panZ)
        {
            _bounds = bounds; _design = design; _theme = theme; _layoutTheme = layoutTheme;
            _mode = mode; _azimuth = azimuth; _selection = selection;
            _spanToScreen = spanToScreen; _zToScreen = zToScreen; _marquee = marquee;
            _gridPitchNm = gridPitchNm; _zoom = zoom; _panSpan = panSpan; _panZ = panZ;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            DrawGrid(canvas);

            if (_design is not null)
                WBondRenderer.DrawProfile(
                    canvas, _design, _theme,
                    s => (float)_spanToScreen(s), z => (float)_zToScreen(z),
                    _mode, _selection, azimuthRadians: _azimuth);

            if (_marquee is { } m) DrawMarquee(canvas, m.Box, m.Crossing);
        }

        /// <summary>
        /// The same dot grid the layout view draws — <see cref="LayoutGridMath.ComputeGridPitch"/>'s
        /// decimation, the same major-every-five rule, and the same two theme colours.
        ///
        /// <para>Reusing the math rather than the renderer is deliberate: <c>LayoutRenderer.DrawGrid</c>
        /// is bound to a <c>LayoutView</c> and a <c>LayoutViewport</c>, and this view has neither — it
        /// works in (span, z) nanometres with its own independent pan and zoom (§6.1). The part that
        /// can be WRONG is the decimation, and that is the part that is shared.</para>
        /// </summary>
        private void DrawGrid(SKCanvas canvas)
        {
            if (LayoutGridMath.ComputeGridPitch(_gridPitchNm, _zoom) is not { } minorPitch) return;

            long majorPitch = minorPitch * LayoutGridMath.MajorGridStepCount;

            double maxSpan = _panSpan + _bounds.Width / _zoom;
            double maxZ = _panZ + _bounds.Height / _zoom;

            long iStart = (long)Math.Floor(_panSpan / minorPitch);
            long iEnd = (long)Math.Ceiling(maxSpan / minorPitch);
            long jStart = (long)Math.Floor(_panZ / minorPitch);
            long jEnd = (long)Math.Ceiling(maxZ / minorPitch);

            // The same safety cap the layout renderer uses: a degenerate zoom must cost nothing rather
            // than emit millions of points.
            const long SafetyCap = 4096;
            if (iEnd - iStart > SafetyCap || jEnd - jStart > SafetyCap) return;

            var minor = new List<SKPoint>();
            var major = new List<SKPoint>();

            for (long i = iStart; i <= iEnd; i++)
            {
                long span = i * minorPitch;
                float sx = (float)_spanToScreen(span);
                bool iMajor = span % majorPitch == 0;

                for (long j = jStart; j <= jEnd; j++)
                {
                    long z = j * minorPitch;
                    (iMajor && z % majorPitch == 0 ? major : minor)
                        .Add(new SKPoint(sx, (float)_zToScreen(z)));
                }
            }

            using var minorPaint = new SKPaint
            {
                IsAntialias = true, Color = _layoutTheme.GridMinor,
                StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round,
            };
            using var majorPaint = new SKPaint
            {
                IsAntialias = true, Color = _layoutTheme.GridMajor,
                StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round,
            };

            if (minor.Count > 0) canvas.DrawPoints(SKPointMode.Points, [.. minor], minorPaint);
            if (major.Count > 0) canvas.DrawPoints(SKPointMode.Points, [.. major], majorPaint);
        }

        /// <summary>
        /// The marquee, in the layout's own selection accent with the same alpha, hairline stroke and
        /// dash period — one selection rectangle across the whole application.
        /// </summary>
        private void DrawMarquee(SKCanvas canvas, Rect box, bool crossing)
        {
            var rect = new SKRect((float)box.X, (float)box.Y,
                                  (float)(box.X + box.Width), (float)(box.Y + box.Height));

            using var fill = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Fill,
                Color = _layoutTheme.Selection.WithAlpha(50),
            };
            canvas.DrawRect(rect, fill);

            using var stroke = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0,
                Color = _layoutTheme.Selection.WithAlpha(255),
                PathEffect = crossing ? SKPathEffect.CreateDash([6f, 4f], 0f) : null,
            };
            canvas.DrawRect(rect, stroke);
            stroke.PathEffect?.Dispose();
        }

        public void Dispose() { }
    }
}
