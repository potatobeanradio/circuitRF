using System;
using System.Collections.Generic;
using Avalonia.Input;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.WBond;
using SkiaSharp;

namespace CircuitRF.Ui.WBond;

/// <summary>
/// The wire layer over the layout canvas (wbond.md §6.1/§6.6, WB23) — the layout half of the wBond
/// editor's two canvases.
///
/// <h3>It draws and routes; it decides nothing</h3>
/// <para>Which thing a click selects lives in <c>WireHitTest</c>/<c>SelectionResolver</c>, what a drag
/// does to geometry lives in <c>WireEdits</c>, and when a frame degrades lives in <c>QualityLadder</c>
/// — all framework-free and tested against arithmetic rather than through a canvas. This class is the
/// thin layer that turns canvas pixels into calls on those, which is what keeps that split worth
/// having (brief-wbond-wbc §0.2).</para>
///
/// <h3>It never touches the layout</h3>
/// <para>No wire enters <c>.clay</c> and no wire edit invalidates the layout's path cache — the whole
/// justification for an overlay rather than a shape type. It declines every gesture it did not hit,
/// so the layout editor underneath keeps working normally: the designer nudges a bond pad and drags a
/// wire onto it in the same view, with the same mouse.</para>
/// </summary>
public sealed class WBondLayoutOverlay : ILayoutCanvasOverlay
{
    private readonly WBondViewModel _vm;
    private readonly WBondPointerController _controller;

    private bool _pressed;     // a left press is down on a wire and may yet become a drag
    private bool _dragging;    // ...and has passed the threshold
    private long _lastXNm, _lastYNm;
    private long _pressXNm, _pressYNm;
    private double _dragThresholdNm;
    private bool _wHeld, _gHeld;

    // Alt-drag (span scaling) in THIS view — the layout half of WB24b.
    private bool _altDrag;
    private bool _altMoveOutputFoot = true;
    private double _altReferenceSpan;
    private double _altApplied = 1.0;
    private double _altAxisX, _altAxisY;

    /// <summary>
    /// A press that deliberately LEFT the selection alone, held in case the gesture turns out to be a
    /// plain click — see the click-through note at the call site.
    /// </summary>
    private (long X, long Y, double TolNm, WBondModifiers Modifiers, int ClickCount)? _deferredPress;

    private bool _marqueeActive;
    private long _marqueeStartX, _marqueeStartY, _marqueeX, _marqueeY;
    private WBondModifiers _lastModifiers = WBondModifiers.None;

    /// <summary>The selection the marquee is adding to — captured after the press has resolved it.</summary>
    private WireSelection _marqueeBase = new();

    /// <summary>
    /// What the live marquee is currently highlighting, or null when no marquee is in progress.
    ///
    /// <para>Held on the shared view-model (<c>WBondViewModel.PreviewSelection</c>) rather than here,
    /// so the PROFILE view highlights the same wires this box is catching — a wire is a thing both
    /// views draw, and highlighting it in only the canvas the pointer is over is half an answer. The
    /// committed selection is <c>WBondViewModel.Selection</c> and is not touched until release.</para>
    /// </summary>
    public WireSelection? MarqueePreview => _marqueeActive ? _vm.PreviewSelection : null;

    private bool _drawArmed;
    private Point3? _drawStart;
    private Wire? _drawGhost;

    private bool _rotateArmed;
    private bool _rotating;
    private bool _rotatePivotIsInputFoot;
    private double _rotateStartAngle;
    private double _rotateApplied;

    /// <param name="frameBudgetMs">
    /// Passed straight to the drag's <see cref="QualityLadder"/> — 60 fps for every real caller. See
    /// <see cref="WBondPointerController"/>'s own constructor for why a test needs to be able to put
    /// it out of reach: the ladder is fed measured wall clock, so a test that only ever asserts
    /// COUNTERS still fails under a loaded machine once the ladder degrades and the drag stops
    /// committing at all.
    /// </param>
    public WBondLayoutOverlay(WBondViewModel viewModel,
                              double frameBudgetMs = QualityLadder.FrameBudgetMs)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _controller = new WBondPointerController(_vm, frameBudgetMs);
    }

    /// <summary>The layout being used as reference geometry, or null (§10's third entry point).</summary>
    public LayoutView? ReferenceLayout { get; set; }

    public Technology? ReferenceTechnology { get; set; }

    /// <summary>Base directory for resolving the reference layout's instances.</summary>
    public string? ReferenceBaseDir { get; set; }

    public WBondRenderTheme Theme { get; set; } = WBondRenderTheme.Fallback;

    /// <summary>Per-view (WB22a) — the profile view is usually at a different zoom.</summary>
    public WireThicknessMode Thickness { get; set; } = WireThicknessMode.ConstantPixels;

    /// <summary>Snap wire points to the layout's own geometry (§6.6). On by default, as in the layout editor.</summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>Snap tolerance in nanometres; the host refreshes it from the current zoom per event.</summary>
    public long SnapToleranceNm { get; set; } = WBondUnits.ToNm(1.0, WBondUnit.Mil);

    /// <summary>
    /// The grid pitch a wire point falls back to when no layout GEOMETRY is within reach — the
    /// reference layout's own <c>SnapDbu</c>, in nanometres, or 0 for none.
    ///
    /// <para>Geometry first, grid second, and that order matters: landing exactly on a pad corner is
    /// the thing snapping exists for (§6.6), and a grid that overrode it would pull the foot back off
    /// the pad. The grid is what catches everything else — and without it the metadata bar would show
    /// a Snap distance, both canvases would draw a grid at that pitch, and the wires would ignore
    /// both.</para>
    /// </summary>
    public long GridPitchNm { get; set; }

    /// <summary>
    /// Whether a drag on empty space marquee-selects WIRES rather than falling through to the layout
    /// editor's own shape marquee.
    ///
    /// <para><b>Two marquees want the same gesture, so one of them has to be chosen.</b> It is a
    /// toggle rather than a modifier because the choice is a mode the user stays in for a while — in
    /// the wBond editor the wires are the subject and the layout is reference, which is why this
    /// defaults to on; someone rearranging bond pads turns it off and gets the layout editor's
    /// marquee back, unchanged.</para>
    /// </summary>
    public bool WireMarqueeEnabled { get; set; } = true;

    /// <summary>
    /// The draw-a-wire tool (§6.4): click the start point, click the end point, with a live ghost of
    /// the full generated loop in between. Arming it takes every click until it is disarmed or a wire
    /// is placed, so it is a mode the toolbar shows rather than a hidden modifier.
    /// </summary>
    public bool WireDrawArmed
    {
        get => _drawArmed;
        set
        {
            if (_drawArmed == value) return;
            _drawArmed = value;
            CancelWireDraw();          // an armed-state change abandons any half-placed wire
            OverlayChanged?.Invoke();
        }
    }

    /// <summary>
    /// The rotate-about-end-point tool (WB26a): grab a wire near the end you want to move and swing
    /// it with the opposite end pinned.
    ///
    /// <para><b>The pivot is the end FURTHER from the grab</b>, which is why the gesture needs no mode
    /// switch — grabbing near an end IS the instruction to move that end.</para>
    /// </summary>
    public bool WireRotateArmed
    {
        get => _rotateArmed;
        set
        {
            if (_rotateArmed == value) return;
            _rotateArmed = value;
            _rotating = false;
            OverlayChanged?.Invoke();
        }
    }

    /// <summary>
    /// The angle the live rotate has turned through, in degrees — the readout WB26a asks for.
    /// Zero when no rotate is in progress.
    /// </summary>
    public double RotateDegrees => _rotating ? _rotateApplied * 180.0 / Math.PI : 0.0;

    /// <summary>
    /// The z the two feet are placed at. Bond pads are on the die/substrate surface, and the LOOP
    /// (which carries all the height) is generated by the profile — so a foot z of zero is the
    /// ordinary case, and this exists for the one that is not: a wire landing on a raised pedestal.
    /// </summary>
    public long FootZNm { get; set; }

    /// <summary>
    /// The instances descended through to reach the frame on screen (WB27) — empty at the base cell.
    /// While non-empty the wires are a dimmed, locked reference: drawn in the sub-cell's frame, and
    /// not selectable, because editing a wire from inside a cell it does not belong to is ambiguous
    /// about which instance of that cell is being edited.
    /// </summary>
    public IReadOnlyList<(LayoutInstance Instance, int Row, int Col)> DescentChain { get; set; } = [];

    /// <summary>
    /// False when the descent chain cannot be composed exactly (an incomplete chain, or a resolution
    /// change part-way down). The wires are then not drawn at all — showing them at a silently wrong
    /// offset would be worse than showing nothing, because the whole point at depth is judging where a
    /// wire foot sits relative to the pad under it.
    /// </summary>
    public bool CanPlaceAtDepth { get; set; } = true;

    /// <summary>True while the overlay is a locked reference rather than an editing surface.</summary>
    public bool IsAtDepth => DescentChain.Count > 0;

    /// <summary>Raised when the overlay's own state changed and the canvas should repaint.</summary>
    public event Action? OverlayChanged;

    /// <summary>Which rung the last drag frame ran at, for the panel's provisional marker (WB15).</summary>
    public DragQuality Quality => _controller.Quality;

    public bool ReadoutIsProvisional => _controller.ReadoutIsProvisional;

    private int DbuPerMicron => ReferenceLayout?.DbuPerMicron ?? LayoutUnits.DefaultDbuPerMicron;

    // ---------------------------------------------------------------- draw

    public void Draw(SKCanvas canvas, LayoutViewport viewport, LayoutRenderTheme layoutTheme)
    {
        if (IsAtDepth && !CanPlaceAtDepth) return;

        // CLIPPED to the canvas, because nothing else clips this pass. The layout underneath is culled
        // against the viewport before it is drawn; wires are not — every wire in the design is drawn
        // whether or not it is on screen. Unclipped, a wire that is off to the left paints straight
        // across the inductance panel docked beside the canvas (the owner's "a wire from the layout
        // view is partially rendering in the Array Inductance view").
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, (float)viewport.Width, (float)viewport.Height));

        try
        {
            var transform = WBondDescent.FrameTransform(DescentChain, DbuPerMicron);

            WBondRenderer.Draw(
                canvas, _vm.Design, viewport, Theme,
                // Selection is deliberately not drawn at depth: nothing there is selectable, and an accent
                // on an unselectable wire reads as an editing affordance that does not exist.
                // While a marquee is live the PREVIEW is what renders, so the highlight tracks the box.
                // EffectiveSelection, never Selection: it is the live preview while any marquee is
                // running — in EITHER canvas — and the committed selection the rest of the time.
                selection: IsAtDepth ? null : _vm.EffectiveSelection,
                thickness: Thickness,
                frameTransform: transform,
                opacity: IsAtDepth ? WBondDescent.DimmedAlpha : (byte)255,
                dbuPerMicron: DbuPerMicron);

            if (_marqueeActive)
                WBondRenderer.DrawMarquee(canvas, viewport, Theme,
                                          _marqueeStartX, _marqueeStartY, _marqueeX, _marqueeY, DbuPerMicron,
                                          accent: layoutTheme.Selection);

            if (_drawGhost is { } ghost)
                WBondRenderer.DrawGhostWire(canvas, ghost, viewport, Theme, DbuPerMicron);
        }
        finally { canvas.Restore(); }
    }

    // ---------------------------------------------------------------- pointer

    public bool OnPointerPressed(long worldX, long worldY, long tolDbu, KeyModifiers modifiers, int clickCount)
    {
        if (IsAtDepth) return false;   // locked reference — every gesture belongs to the layout editor

        long xNm = WBondSnap.ToNm(worldX, DbuPerMicron);
        long yNm = WBondSnap.ToNm(worldY, DbuPerMicron);
        double tolNm = WBondSnap.ToNm(tolDbu, DbuPerMicron);

        if (_drawArmed)
        {
            if (_drawStart is null)
            {
                var (fx, fy) = SnapPoint(xNm, yNm);
                _drawStart = new Point3(fx, fy, FootZNm);
                OverlayChanged?.Invoke();
                return true;
            }

            // The SAME constraint the ghost was drawn with. Applying it only on the move would place
            // a wire that does not match the ghost the user was looking at when they clicked.
            var (cx, cy) = Constrain(_drawStart.Value, xNm, yNm, modifiers);
            var (sx, sy) = SnapPoint(cx, cy);

            _vm.AddWire(_drawStart.Value, new Point3(sx, sy, FootZNm),
                        WBondDefaults.DiameterNm, WBondDefaults.Material,
                        pointsIfProfileCreated: WBondDefaults.Points);

            CancelWireDraw();
            OverlayChanged?.Invoke();
            return true;
        }

        // Ask what is under the cursor BEFORE routing, because that answer is also the answer to
        // "does this gesture belong to the overlay at all" — a miss must reach the layout editor so
        // its own marquee and tools keep working in this same view.
        var hit = WireHitTest.HitTestLayout(_vm.Mesh, xNm, yNm, tolNm);

        // Captured at PRESS, because that is when the user's hand committed to add-or-replace; a
        // marquee that read the modifiers again at release would change meaning if Shift was let go
        // mid-drag.
        _lastModifiers = Modifiers(modifiers);

        // A press on something ALREADY SELECTED picks the whole selection up rather than replacing it
        // — see SelectionCovers. Shift still re-resolves, because extending is the one case where a
        // press on a selected thing means something else.
        bool keepSelection = hit.Found
                          && !_lastModifiers.HasFlag(WBondModifiers.Shift)
                          && SelectionCovers(hit);

        // ...and if the press turns out to be a plain CLICK, the re-resolve happens on release
        // instead. That is the standard click-through: a drag moves what was selected, a click still
        // narrows to what is under the cursor. Doing only the first would make an element inside a
        // selected wire unreachable by clicking it.
        _deferredPress = keepSelection ? (xNm, yNm, tolNm, _lastModifiers, clickCount) : null;

        if (!keepSelection)
            _controller.Press(xNm, yNm, tolNm, _lastModifiers, clickCount, EditorView.Layout);

        OverlayChanged?.Invoke();

        if (!hit.Found)
        {
            if (!WireMarqueeEnabled) return false;

            _marqueeActive = true;
            _marqueeStartX = xNm;
            _marqueeStartY = yNm;
            _marqueeX = xNm;
            _marqueeY = yNm;

            // AFTER Press, which has already cleared the selection unless Shift was held — so this is
            // exactly the base the release-time union will be taken against.
            _marqueeBase = _vm.Selection;
            _vm.PreviewSelection = _marqueeBase;
            return true;
        }

        if (_rotateArmed && BeginRotate(hit.Wire, xNm, yNm)) return true;

        // The baseline is the SNAPPED press point, not the raw one. Measuring the first frame's delta
        // from an unsnapped press made a click with a pixel of hand-shake jump the grabbed point onto
        // the nearest pad corner — and a jumped FOOT is a changed span, which is exactly what the
        // owner saw when clicking a wire's start point.
        (_lastXNm, _lastYNm) = SnapPoint(xNm, yNm);

        _pressXNm = xNm;
        _pressYNm = yNm;

        // The pointer must leave the distance that counts as "on" the thing it grabbed before this is
        // a drag at all — so a click cannot move geometry, and cannot leave an undo entry behind
        // either (the gesture is opened when the threshold is crossed, not here).
        _dragThresholdNm = Math.Max(tolNm, 1.0);
        _pressed = true;

        _altDrag = (modifiers & KeyModifiers.Alt) != 0;
        _altApplied = 1.0;
        _altReferenceSpan = ChordLengthNm(hit.Wire);
        _altMoveOutputFoot = GrabMovesOutputFoot(hit);
        (_altAxisX, _altAxisY) = ChordDirection(hit.Wire);

        return true;
    }

    /// <summary>
    /// Whether the current selection already covers what the pointer is over — the test that makes a
    /// multi-element selection draggable instead of collapsing to whatever is under the cursor.
    /// </summary>
    private bool SelectionCovers(WireHitTest.Hit hit)
    {
        var selection = _vm.Selection;
        if (selection.Wires.Contains(hit.Wire)) return true;
        if (selection.Points.Contains(new PointRef(hit.Wire, hit.Point))) return true;

        return hit.IsSegment && selection.Segments.Contains(new SegmentRef(hit.Wire, hit.Point));
    }

    /// <summary>
    /// Which foot an alt-drag should MOVE — the one the grab landed nearer.
    ///
    /// <para>WB26a's rule, reused: grabbing near an end IS the instruction to move that end, so the
    /// far foot is pinned and the gesture needs no mode switch. Stated as "which one moves" rather
    /// than "which one was grabbed" because that is what <c>ScaleSpan</c> takes, and the double
    /// negative in between is exactly where this was wrong first time — an alt-drag on the output end
    /// pulled the INPUT end and the wire shrank when the hand said grow.</para>
    /// </summary>
    private bool GrabMovesOutputFoot(WireHitTest.Hit hit)
    {
        var wires = _vm.Design.AllWires().ToList();
        if (hit.Wire < 0 || hit.Wire >= wires.Count) return true;

        int last = wires[hit.Wire].Points.Count - 1;
        return last > 0 && hit.Point > last / 2.0;
    }

    private double ChordLengthNm(int wireIndex)
    {
        var wires = _vm.Design.AllWires().ToList();
        if (wireIndex < 0 || wireIndex >= wires.Count) return 0;

        return wires[wireIndex].ChordLengthMetres() * WBondUnits.NmPerMetre;
    }

    private (double X, double Y) ChordDirection(int wireIndex)
    {
        var wires = _vm.Design.AllWires().ToList();
        return wireIndex < 0 || wireIndex >= wires.Count
            ? (0.0, 0.0)
            : WireEdits.ChordDirectionXY(wires[wireIndex]);
    }

    public bool OnPointerMoved(long worldX, long worldY, long tolDbu, bool leftButtonDown,
                               KeyModifiers modifiers)
    {
        if (_drawStart is { } start)
        {
            var (cx, cy) = Constrain(start,
                                     WBondSnap.ToNm(worldX, DbuPerMicron),
                                     WBondSnap.ToNm(worldY, DbuPerMicron),
                                     modifiers);
            var (ex, ey) = SnapPoint(cx, cy);
            _drawGhost = GhostProfile()?.CreateWire(start, new Point3(ex, ey, FootZNm),
                                                    WBondDefaults.DiameterNm, WBondDefaults.Material);
            OverlayChanged?.Invoke();
            return true;
        }

        if (_rotating)
        {
            if (!leftButtonDown) { EndRotate(); return false; }

            RotateFrame(WBondSnap.ToNm(worldX, DbuPerMicron), WBondSnap.ToNm(worldY, DbuPerMicron),
                        modifiers);
            return true;
        }

        if (_marqueeActive)
        {
            if (!leftButtonDown) { EndMarquee(); OverlayChanged?.Invoke(); return false; }

            long mx = WBondSnap.ToNm(worldX, DbuPerMicron);
            long my = WBondSnap.ToNm(worldY, DbuPerMicron);
            if (mx == _marqueeX && my == _marqueeY) return true;   // consumed, nothing moved

            _marqueeX = mx;
            _marqueeY = my;
            _vm.PreviewSelection = ResolveMarqueeNow(mx, my);
            OverlayChanged?.Invoke();
            return true;
        }

        if (!_pressed) return false;
        if (!leftButtonDown) { EndDrag(); return false; }

        long rawX = WBondSnap.ToNm(worldX, DbuPerMicron);
        long rawY = WBondSnap.ToNm(worldY, DbuPerMicron);

        if (!_dragging)
        {
            double moved = Math.Max(Math.Abs(rawX - _pressXNm), Math.Abs(rawY - _pressYNm));
            if (moved < _dragThresholdNm) return true;   // consumed, but still a click so far

            // The threshold is crossed: NOW open the gesture, so a click leaves nothing behind.
            _dragging = true;
            _vm.BeginGesture();
            _controller.BeginDrag();
        }

        if (_altDrag) return AltDragFrame(rawX, rawY);

        // ONE snap rule for the whole editor — geometry first, then the grid (see SnapPoint).
        var (xNm, yNm) = SnapPoint(rawX, rawY);

        long dx = xNm - _lastXNm;
        long dy = yNm - _lastYNm;
        if (dx == 0 && dy == 0) return true;   // consumed, but nothing to recompute

        _lastXNm = xNm;
        _lastYNm = yNm;

        // The delta is applied by the shared edit primitive; the controller times the frame and feeds
        // the quality ladder around it.
        _controller.DragFrame(
            _ => WireEdits.Translate(_vm.Design, _vm.Selection, dx, dy, EditorView.Layout));

        OverlayChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// <b>Alt-drag in the LAYOUT view scales span</b> (WB24b), which it previously did not do at all —
    /// the gesture existed only in the profile view, so holding Alt here just moved the wire.
    ///
    /// <para>The displacement is PROJECTED onto the wire's own chord, because that is the only
    /// component of a layout-view drag that means "longer" or "shorter"; a drag perpendicular to the
    /// wire changes no span and correctly does nothing. Height is untouched here: this view has no z
    /// axis to drag along, so there is nothing for the user to have meant by it.</para>
    ///
    /// <para>The pinned foot is the one further from the grab — the same anchor rule the profile
    /// view's alt-drag and the rotate tool both use.</para>
    /// </summary>
    private bool AltDragFrame(long xNm, long yNm)
    {
        if (_altReferenceSpan <= 0) return true;

        double along = (xNm - _pressXNm) * _altAxisX + (yNm - _pressYNm) * _altAxisY;

        // Grabbing near the INPUT foot moves that foot, so pulling the cursor "backwards" along the
        // chord is what lengthens the wire — the sign has to follow the anchor or the wire shrinks
        // when the hand says grow.
        if (!_altMoveOutputFoot) along = -along;

        double target = Math.Max((_altReferenceSpan + along) / _altReferenceSpan, 1e-3);
        double frame = target / _altApplied;
        if (Math.Abs(frame - 1.0) < 1e-9) return true;
        _altApplied = target;

        _controller.DragFrame(_ => _vm.ScaleSelection(frame, 1.0, _altMoveOutputFoot));

        OverlayChanged?.Invoke();
        return true;
    }

    public bool OnPointerReleased(long worldX, long worldY)
    {
        if (_rotating) { EndRotate(); return true; }

        if (_marqueeActive)
        {
            EndMarquee();

            // The controller owns the enclose-versus-crossing rule (and the crossing promotion to
            // whole wires) — this only supplies where the hand started and finished.
            _controller.Marquee(WBondSnap.ToNm(worldX, DbuPerMicron), WBondSnap.ToNm(worldY, DbuPerMicron),
                                _lastModifiers, EditorView.Layout);
            OverlayChanged?.Invoke();
            return true;
        }

        if (!_pressed) return false;
        EndDrag();
        return true;
    }

    private void EndDrag()
    {
        bool wasDragging = _dragging;

        _pressed = false;
        _dragging = false;
        _altDrag = false;

        // A press that never crossed the threshold opened nothing, so there is nothing to close —
        // calling EndDrag on the controller would publish a spurious final answer for a plain click.
        if (wasDragging)
        {
            _controller.EndDrag();  // restores exact geometry and publishes the final, non-provisional answer
            _vm.EndGesture();
        }
        else if (_deferredPress is { } press)
        {
            // It was a click on an already-selected thing after all: resolve it now.
            _controller.Press(press.X, press.Y, press.TolNm, press.Modifiers, press.ClickCount,
                              EditorView.Layout);
        }

        _deferredPress = null;
        OverlayChanged?.Invoke();
    }

    /// <summary>
    /// Every wire's XY extent, in the HOST LAYOUT's database units — what Zoom to Fit must include.
    ///
    /// <para>Points are stored in nanometres and the canvas works in the layout's own DBU, so the
    /// bridge is crossed here, through the same <see cref="WBondSnap"/> conversion the draw and the
    /// hit test use. At depth the wires are drawn through the descent transform, so the extent is
    /// taken after it — a fit that framed their untransformed coordinates would be framing a place
    /// they are not.</para>
    /// </summary>
    public Bbox ContentBounds()
    {
        if (IsAtDepth && !CanPlaceAtDepth) return Bbox.Empty;

        var transform = WBondDescent.FrameTransform(DescentChain, DbuPerMicron);
        var bb = Bbox.Empty;

        foreach (var wire in _vm.Design.AllWires())
        {
            foreach (var p in wire.Points)
            {
                var (nx, ny) = transform is null ? (p.X, (double)p.Y) : transform(p.X, p.Y);

                long x = WBondSnap.ToDbu((long)Math.Round(nx), DbuPerMicron);
                long y = WBondSnap.ToDbu((long)Math.Round(ny), DbuPerMicron);

                bb = bb.Union(new Bbox(x, y, x, y));
            }
        }

        return bb;
    }

    /// <summary>The live preview, resolved by the SAME rule the release will commit.</summary>
    private WireSelection ResolveMarqueeNow(long xNm, long yNm) =>
        _controller.ResolveMarquee(xNm, yNm, _lastModifiers, _marqueeBase, EditorView.Layout);

    /// <summary>Closes a marquee gesture and drops its preview, so a stale highlight cannot outlive it.</summary>
    private void EndMarquee()
    {
        _marqueeActive = false;
        _vm.PreviewSelection = null;
    }

    /// <summary>Abandons a half-placed wire. Nothing was added, so there is nothing to undo.</summary>
    private void CancelWireDraw()
    {
        _drawStart = null;
        _drawGhost = null;
    }

    // ---------------------------------------------------------------- rotate about end point (WB26a)

    /// <summary>
    /// Starts a swing. The pivot is the end FURTHER from the grab, which is what removes the need for
    /// a mode switch: grabbing near an end IS the instruction to move that end.
    /// </summary>
    private bool BeginRotate(int wireIndex, long xNm, long yNm)
    {
        var wires = _vm.Design.AllWires().ToList();
        if (wireIndex < 0 || wireIndex >= wires.Count) return false;

        var wire = wires[wireIndex];
        if (wire.Points.Count < 2) return false;

        double toInput = Distance(wire.Points[0], xNm, yNm);
        double toOutput = Distance(wire.Points[^1], xNm, yNm);

        // Grabbed nearer the input foot => the OUTPUT foot is the pivot, and vice versa.
        _rotatePivotIsInputFoot = toInput > toOutput;

        var pivot = _rotatePivotIsInputFoot ? wire.Points[0] : wire.Points[^1];
        _rotateStartAngle = Math.Atan2(yNm - pivot.Y, xNm - pivot.X);
        _rotateApplied = 0.0;
        _rotating = true;

        _vm.BeginGesture();     // one undo entry for the whole swing
        _controller.BeginDrag();
        return true;
    }

    private void RotateFrame(long xNm, long yNm, KeyModifiers modifiers)
    {
        var wires = _vm.Design.AllWires().ToList();
        int first = _vm.Selection.TouchedWires().FirstOrDefault(-1);
        if (first < 0 || first >= wires.Count) return;

        var wire = wires[first];
        var pivot = _rotatePivotIsInputFoot ? wire.Points[0] : wire.Points[^1];

        double angle = Math.Atan2(yNm - pivot.Y, xNm - pivot.X) - _rotateStartAngle;

        // Shift constrains to the 45-degree increments used everywhere else in this editor.
        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            const double Step = Math.PI / 4.0;
            angle = Math.Round(angle / Step) * Step;
        }

        double delta = angle - _rotateApplied;
        if (Math.Abs(delta) < 1e-9) return;
        _rotateApplied = angle;

        _controller.DragFrame(
            _ => _vm.RotateSelectionAboutOwnEnd(delta, _rotatePivotIsInputFoot, EditorView.Layout));

        OverlayChanged?.Invoke();
    }

    private void EndRotate()
    {
        _rotating = false;
        _controller.EndDrag();
        _vm.EndGesture();
        OverlayChanged?.Invoke();
    }

    private static double Distance(Point3 p, long xNm, long yNm)
    {
        double dx = p.X - xNm, dy = p.Y - yNm;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private LoopProfile? GhostProfile() => _vm.Design.Profiles.FirstOrDefault();

    /// <summary>
    /// Shift constrains the placement to ortho (§6.4).
    ///
    /// <para>Applied BEFORE the snap, so a constrained wire can still land exactly on a pad corner
    /// that lies on the constrained axis — constraining afterwards would pull the point back off
    /// whatever it had just snapped to.</para>
    /// </summary>
    private static (long X, long Y) Constrain(Point3 start, long xNm, long yNm, KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Shift) == 0) return (xNm, yNm);

        return Math.Abs(xNm - start.X) >= Math.Abs(yNm - start.Y)
            ? (xNm, start.Y)
            : (start.X, yNm);
    }

    private (long X, long Y) SnapPoint(long xNm, long yNm)
    {
        if (!SnapEnabled) return (xNm, yNm);

        var snap = WBondSnap.Snap(ReferenceLayout, ReferenceTechnology, ReferenceBaseDir,
                                  xNm, yNm, SnapToleranceNm);
        if (snap.Snapped) return (snap.XNm, snap.YNm);

        return GridPitchNm > 0 ? (Round(xNm, GridPitchNm), Round(yNm, GridPitchNm)) : (xNm, yNm);
    }

    /// <summary>Nearest multiple of <paramref name="pitch"/>, correct for negatives.</summary>
    private static long Round(long value, long pitch) =>
        (long)Math.Round((double)value / pitch) * pitch;

    // ---------------------------------------------------------------- keyboard

    public bool OnKeyDown(Key key, KeyModifiers modifiers)
    {
        if (key == Key.W) { _wHeld = true; return false; }   // a held promotion key, not a command
        if (key == Key.G) { _gHeld = true; return false; }

        if (IsAtDepth) return false;

        // Escape abandons a half-placed wire before it clears a selection — cancelling the gesture
        // you are visibly in the middle of is what Escape means everywhere else in this application.
        if (key == Key.Escape && _drawStart is not null)
        {
            CancelWireDraw();
            OverlayChanged?.Invoke();
            return true;
        }

        if (key == Key.Escape && !_vm.Selection.IsEmpty)
        {
            _vm.Selection = new WireSelection();
            OverlayChanged?.Invoke();
            return true;
        }

        // Arrow keys are claimed ONLY when the overlay has a selection — otherwise they are the
        // layout editor's own nudge, in the same view.
        if (_vm.Selection.IsEmpty) return false;

        bool coarse = (modifiers & KeyModifiers.Shift) != 0;
        var (dx, dy) = key switch
        {
            Key.Left  => (-1, 0),
            Key.Right => (1, 0),
            Key.Down  => (0, -1),
            Key.Up    => (0, 1),      // +y in the layout view (§6.3)
            _         => (0, 0),
        };

        if (dx == 0 && dy == 0) return false;

        _vm.NudgeSelection(dx, dy, coarse, EditorView.Layout);
        OverlayChanged?.Invoke();
        return true;
    }

    public void OnKeyUp(Key key, KeyModifiers modifiers)
    {
        if (key == Key.W) _wHeld = false;
        if (key == Key.G) _gHeld = false;
    }

    private WBondModifiers Modifiers(KeyModifiers modifiers)
    {
        var result = WBondModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0) result |= WBondModifiers.Shift;
        if ((modifiers & KeyModifiers.Alt) != 0) result |= WBondModifiers.Alt;
        if (_wHeld) result |= WBondModifiers.WholeWire;
        if (_gHeld) result |= WBondModifiers.WholeGroup;
        return result;
    }
}
