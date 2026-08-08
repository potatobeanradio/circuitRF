using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using CircuitRF.Ui.Renderers;
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
/// <h3>What a drag means here, and what it deliberately does not</h3>
/// <para>A plain drag moves the selection in <b>z only</b>. Horizontal free-dragging is not offered,
/// and that is a decision rather than an omission: span is a <i>derived</i> coordinate — cumulative
/// XY arc length — so "move this point 10 mil to the right in the profile" has no single answer in
/// the geometry that is actually stored. Changing span is <b>alt-drag</b>, which scales the whole
/// bound array by a factor (WB24b) and is well defined.</para>
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

    /// <summary>Per-view, per WB22a.</summary>
    public WireThicknessMode Thickness { get; set; } = WireThicknessMode.ConstantPixels;

    public ProfileProjection.SpanMode SpanMode { get; set; } = ProfileProjection.SpanMode.Absolute;

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

        var hit = WireHitTest.HitTestProfile(mesh, span, (long)Math.Round(z), tolNm, SpanMode);
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
                var p = ProfileProjection.Project(wire, i, SpanMode);
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

    // ── Render ────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context) =>
        context.Custom(new ProfileDrawOperation(
            new Rect(Bounds.Size), _viewModel?.Design, WireTheme, SpanMode,
            _viewModel?.Selection, SpanToScreen, ZToScreen));

    // ── Pointer ───────────────────────────────────────────────────────────────

    private bool _dragging;
    private double _lastZNm;
    private double _dragStartSpan, _dragStartZ;
    private bool _altDrag;
    private char _altAxis;              // '\0' until the drag has declared an axis, then 'z' or 's'
    private double _altReference;       // the quantity the factor is measured against
    private double _altApplied = 1.0;   // factor applied so far, so each frame applies only the change
    private string? _altProfile;

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

        _controller.Press((long)Math.Round(span), (long)Math.Round(z), HitToleranceNm,
                          Modifiers(e.KeyModifiers), e.ClickCount, EditorView.Profile);

        if (_viewModel.Selection.IsEmpty) { InvalidateVisual(); return; }

        _dragging = true;
        _dragStartSpan = span;
        _dragStartZ = z;
        _lastZNm = z;
        _altDrag = (e.KeyModifiers & KeyModifiers.Alt) != 0;
        _altAxis = '\0';
        _altApplied = 1.0;
        _altProfile = SelectedProfileName();

        // One undo entry for the whole gesture, not one per frame.
        _viewModel.BeginGesture();
        _controller.BeginDrag();
        InvalidateVisual();
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

        if (!_dragging || _viewModel is null || _controller is null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) { EndDrag(); return; }

        double span = ScreenToSpan(pos.X);
        double z = ScreenToZ(pos.Y);

        if (_altDrag) { AltDragFrame(span, z); return; }

        long dz = (long)Math.Round(z - _lastZNm);
        if (dz == 0) return;
        _lastZNm += dz;

        _controller.DragFrame(
            _ => WireEdits.Translate(_viewModel.Design, _viewModel.Selection, 0, dz, EditorView.Profile));

        InvalidateVisual();
    }

    /// <summary>
    /// Alt-drag: scale the whole bound array (WB24a/b/c). The axis is declared once, by whichever
    /// component first moves past a few pixels — re-deciding it every frame would let a wobbling hand
    /// alternate between scaling height and scaling span within one gesture.
    /// </summary>
    private void AltDragFrame(double span, double z)
    {
        if (_viewModel is null || _controller is null || _altProfile is null) return;

        double dSpan = span - _dragStartSpan;
        double dZ = z - _dragStartZ;
        double threshold = 4.0 / Math.Max(_zoom, MinZoom);

        if (_altAxis == '\0')
        {
            if (Math.Abs(dZ) < threshold && Math.Abs(dSpan) < threshold) return;
            _altAxis = Math.Abs(dZ) >= Math.Abs(dSpan) ? 'z' : 's';
            _altReference = _altAxis == 'z' ? ReferenceHeightNm() : ReferenceSpanNm();
            if (_altReference <= 0) { _altAxis = '\0'; return; }
        }

        double target = _altAxis == 'z'
            ? (_altReference + dZ) / _altReference
            : (_altReference + dSpan) / _altReference;

        // A non-positive factor would fold the array through itself; clamp rather than refuse, so the
        // drag stays live and the user simply cannot push it past flat.
        target = Math.Max(target, 1e-3);

        double frameFactor = target / _altApplied;
        if (Math.Abs(frameFactor - 1.0) < 1e-9) return;
        _altApplied = target;

        _controller.DragFrame(_ =>
        {
            if (_altAxis == 'z') _viewModel.ScaleProfileHeight(_altProfile, frameFactor);
            else _viewModel.ScaleProfileSpan(_altProfile, frameFactor);
        });

        InvalidateVisual();
    }

    private void OnPointerReleasedInternal(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning) { _isPanning = false; e.Pointer.Capture(null); return; }
        if (_dragging) EndDrag();
    }

    private void EndDrag()
    {
        _dragging = false;
        _controller?.EndDrag();
        _viewModel?.EndGesture();
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

    /// <summary>The quantity a height scale is measured against — the bound profile's loop height.</summary>
    private double ReferenceHeightNm()
    {
        if (_viewModel is null || _altProfile is null) return 0;
        return _viewModel.Design.ProfileByName(_altProfile)?.LoopHeightNm ?? 0;
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

            var last = ProfileProjection.Project(wire, wire.Points.Count - 1, SpanMode);
            var first = ProfileProjection.Project(wire, 0, SpanMode);
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
        private readonly ProfileProjection.SpanMode _mode;
        private readonly WireSelection? _selection;
        private readonly Func<double, double> _spanToScreen;
        private readonly Func<double, double> _zToScreen;

        public ProfileDrawOperation(Rect bounds, WBondDesign? design, WBondRenderTheme theme,
                                    ProfileProjection.SpanMode mode, WireSelection? selection,
                                    Func<double, double> spanToScreen, Func<double, double> zToScreen)
        {
            _bounds = bounds; _design = design; _theme = theme; _mode = mode;
            _selection = selection; _spanToScreen = spanToScreen; _zToScreen = zToScreen;
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public Rect Bounds => _bounds;
        public bool HitTest(Point p) => _bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (_design is null) return;
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            WBondRenderer.DrawProfile(
                lease.SkCanvas, _design, _theme,
                s => (float)_spanToScreen(s), z => (float)_zToScreen(z),
                _mode, _selection);
        }

        public void Dispose() { }
    }
}
