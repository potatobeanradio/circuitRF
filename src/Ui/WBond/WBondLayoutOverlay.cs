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

    private bool _dragging;
    private long _lastXNm, _lastYNm;
    private bool _wHeld, _gHeld;

    private bool _marqueeActive;
    private long _marqueeStartX, _marqueeStartY, _marqueeX, _marqueeY;
    private WBondModifiers _lastModifiers = WBondModifiers.None;

    private bool _drawArmed;
    private Point3? _drawStart;
    private Wire? _drawGhost;

    private bool _rotateArmed;
    private bool _rotating;
    private bool _rotatePivotIsInputFoot;
    private double _rotateStartAngle;
    private double _rotateApplied;

    public WBondLayoutOverlay(WBondViewModel viewModel)
    {
        _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _controller = new WBondPointerController(_vm);
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

    public void Draw(SKCanvas canvas, LayoutViewport viewport)
    {
        if (IsAtDepth && !CanPlaceAtDepth) return;

        var transform = WBondDescent.FrameTransform(DescentChain, DbuPerMicron);

        WBondRenderer.Draw(
            canvas, _vm.Design, viewport, Theme,
            // Selection is deliberately not drawn at depth: nothing there is selectable, and an accent
            // on an unselectable wire reads as an editing affordance that does not exist.
            selection: IsAtDepth ? null : _vm.Selection,
            thickness: Thickness,
            frameTransform: transform,
            opacity: IsAtDepth ? WBondDescent.DimmedAlpha : (byte)255,
            dbuPerMicron: DbuPerMicron);

        if (_marqueeActive)
            WBondRenderer.DrawMarquee(canvas, viewport, Theme,
                                      _marqueeStartX, _marqueeStartY, _marqueeX, _marqueeY, DbuPerMicron);

        if (_drawGhost is { } ghost)
            WBondRenderer.DrawGhostWire(canvas, ghost, viewport, Theme, DbuPerMicron);
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
            return true;
        }

        if (_rotateArmed && BeginRotate(hit.Wire, xNm, yNm)) return true;

        _lastXNm = xNm;
        _lastYNm = yNm;
        _dragging = true;

        // One undo entry for the whole drag — and without it the drag is not undoable AT ALL, because
        // the per-frame commit deliberately pushes nothing.
        _vm.BeginGesture();
        _controller.BeginDrag();
        return true;
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
            if (!leftButtonDown) { _marqueeActive = false; OverlayChanged?.Invoke(); return false; }

            _marqueeX = WBondSnap.ToNm(worldX, DbuPerMicron);
            _marqueeY = WBondSnap.ToNm(worldY, DbuPerMicron);
            OverlayChanged?.Invoke();
            return true;
        }

        if (!_dragging) return false;
        if (!leftButtonDown) { EndDrag(); return false; }

        long xNm = WBondSnap.ToNm(worldX, DbuPerMicron);
        long yNm = WBondSnap.ToNm(worldY, DbuPerMicron);

        if (SnapEnabled)
        {
            var snap = WBondSnap.Snap(ReferenceLayout, ReferenceTechnology, ReferenceBaseDir,
                                      xNm, yNm, SnapToleranceNm);
            if (snap.Snapped) { xNm = snap.XNm; yNm = snap.YNm; }
        }

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

    public bool OnPointerReleased(long worldX, long worldY)
    {
        if (_rotating) { EndRotate(); return true; }

        if (_marqueeActive)
        {
            _marqueeActive = false;

            // The controller owns the enclose-versus-crossing rule (and the crossing promotion to
            // whole wires) — this only supplies where the hand started and finished.
            _controller.Marquee(WBondSnap.ToNm(worldX, DbuPerMicron), WBondSnap.ToNm(worldY, DbuPerMicron),
                                _lastModifiers, EditorView.Layout);
            OverlayChanged?.Invoke();
            return true;
        }

        if (!_dragging) return false;
        EndDrag();
        return true;
    }

    private void EndDrag()
    {
        _dragging = false;
        _controller.EndDrag();      // restores exact geometry and publishes the final, non-provisional answer
        _vm.EndGesture();
        OverlayChanged?.Invoke();
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
        return snap.Snapped ? (snap.XNm, snap.YNm) : (xNm, yNm);
    }

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
