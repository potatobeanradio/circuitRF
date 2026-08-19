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
public sealed partial class WBondLayoutOverlay : ILayoutCanvasOverlay
{
    private readonly WBondViewModel _vm;
    private readonly WBondPointerController _controller;

    private bool _pressed;     // a left press is down on a wire and may yet become a drag
    private bool _dragging;    // ...and has passed the threshold
    private long _lastXNm, _lastYNm;
    private long _pressXNm, _pressYNm;
    private double _dragThresholdNm;
    private bool _gHeld;

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

    /// <summary>
    /// A marquee the LAYOUT editor is running, which this overlay follows without consuming anything —
    /// so one drag selects the shapes it caught AND the wires it caught.
    ///
    /// <para>Used where <see cref="WireMarqueeEnabled"/> is off, which is the wirebond-CELL
    /// configuration (WB40): there the artwork is the subject and the layout's own marquee has to keep
    /// working, but the wires are content of that same cell and the owner expects a box round them to
    /// pick them up (2026-08-17: "I also cannot select wires using marquee select"). It is the same
    /// answer §6.3 already gives for a CLICK — the two selections are independent and can be held at
    /// once — applied to the gesture that resolves several things instead of one.</para>
    ///
    /// <para><b>The BOX is not drawn by this overlay.</b> The layout editor draws its own for the same
    /// gesture, and a second one over it at the same coordinates would be a visible double stroke;
    /// <see cref="_marqueeActive"/> stays false for exactly that reason, and only the wire PREVIEW
    /// highlight is published.</para>
    /// </summary>
    private bool _companionMarquee;

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

    /// <summary>
    /// The wire the swing was STARTED on — the one under the hand, whose far foot is the anchor.
    ///
    /// <para><b>Not the first wire of the selection</b>, which is what the angle used to be measured
    /// about (owner, 2026-08-17: the tool "rotates about the center of the wire when clicking on the
    /// start", and "sometimes doesn't rotate"). Both are the same defect: <c>TouchedWires</c> is a
    /// HASH SET, so "the first one" is an arbitrary member — with several wires selected the angle was
    /// measured about some other wire's foot, and the wire under the cursor turned by an angle that
    /// had nothing to do with the hand. Measured: a quarter turn asked for on the grabbed wire came
    /// out as about a third of one.</para>
    /// </summary>
    private int _rotateWire = -1;

    /// <summary>
    /// The grabbed wire's own bearing when the swing began — pivot foot toward moving foot, in
    /// absolute XY radians.
    ///
    /// <para>Held so Shift can snap the wire to the <b>absolute</b> 45° grid rather than to 45° from
    /// wherever it started (owner, 2026-08-17). The two differ for every wire that is not already on
    /// the grid, and it is the absolute one that gets a wire back onto 0/90/180/270 — the arrangement
    /// almost every bond array wants.</para>
    /// </summary>
    private double _rotateStartBearing;

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
    public WireThicknessMode Thickness { get; set; } = WireThicknessMode.Thin;

    /// <summary>
    /// The MASTER switch: off means a wire point lands exactly where the pointer is — no geometry, no
    /// grid. The standalone editor's own Snap toggle is this one.
    /// </summary>
    public bool SnapEnabled { get; set; } = true;

    /// <summary>
    /// Snap wire points to the layout's own geometry (§6.6) — pads, edges, and the other wires. On by
    /// default, as in the layout editor.
    ///
    /// <para><b>Separate from <see cref="SnapEnabled"/> because the Layout Editor separates them</b>
    /// (owner, 2026-08-17: <i>"geometry snap toggle is not respected in the wBond layout host"</i>).
    /// There, <c>S</c>/<c>F3</c> turns geometry snap off and the GRID goes on working — a rectangle
    /// still lands on the pitch in the Snap box. A wire has to behave the same way or the same key
    /// means two things in one view. Conflating the two under `SnapEnabled` is what made `S` look
    /// ignored: nothing in the layout host was pushing it at all, and there was no property that could
    /// have expressed "geometry off, grid on" if something had.</para>
    /// </summary>
    public bool GeometrySnapEnabled { get; set; } = true;

    /// <summary>
    /// Whether geometry snap also offers the INTERSECTIONS of the layout's edges — the layout editor's
    /// own Include Intersections toggle, which this used to hard-code to false.
    ///
    /// <para>Off by default, matching <c>LayoutEditorViewModel.IncludeIntersectionsEnabled</c> and for
    /// its reason: intersections are relational and unindexed, so they cost something to find and are
    /// dense enough to be noisy when nobody asked for them.</para>
    /// </summary>
    public bool IncludeIntersections { get; set; }

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
    /// Whether the LAYOUT editor underneath has a drawing tool armed — Rectangle, Path, Via, and the
    /// rest of the wBond editor's second toolbar row.
    ///
    /// <para><b>Armed means the canvas is theirs.</b> The overlay is offered every press first, so
    /// without this a wire marquee would swallow the click that was meant to start a rectangle, and
    /// the tool would appear to do nothing at all. A press that lands ON a wire is still the
    /// overlay's — a wire stays clickable while a layout tool is armed, exactly as a shape stays
    /// clickable while the wire tools are.</para>
    ///
    /// <para>Null (the default) means "no layout tool can be armed", which is the standalone binary
    /// and every test that constructs an overlay on its own.</para>
    /// </summary>
    public Func<bool>? LayoutToolArmed { get; set; }

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
    /// True between the two clicks of a wire — one foot is down and the ghost is following the cursor.
    ///
    /// <para>A state Escape has to be able to cancel WITHOUT also disarming the tool, so whoever owns
    /// the Escape key needs to be able to ask. The overlay answers it for the Layout Editor, whose
    /// Escape handling lives in its view model rather than here.</para>
    /// </summary>
    public bool IsPlacingWire => _drawStart is not null;

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

    /// <summary>
    /// Asks for a repaint from OUTSIDE the overlay's own gestures.
    ///
    /// <para>The wires can change without this object being touched at all: the docked Array Inductance
    /// panel selects an array by double-clicking its name, and its four settable rows edit a whole
    /// group's geometry. Those write the view-model directly, so the canvas showing the wires had no
    /// reason to redraw and the selection appeared not to take (owner, 2026-08-17). The wBond editor
    /// never saw it because it repaints both canvases off the view-model's own notifications.</para>
    /// </summary>
    public void NotifyChanged() => OverlayChanged?.Invoke();

    /// <summary>
    /// The snap answer the LAST <see cref="SnapPoint"/> produced — what the glyph on screen should be
    /// showing, published for the canvas to hand to the layout editor's own marker slot.
    ///
    /// <para>Guarded on one of the two gestures that actually SNAP being live, so it disappears the
    /// instant the drag or the draw ends rather than lingering as a stale mark on the canvas. A ROTATE
    /// is deliberately excluded: it swings a wire about a pinned end and consults no snap at all, so a
    /// marker during one could only be a leftover from the gesture before it.</para>
    /// </summary>
    /// <summary>
    /// The glyph the canvas should draw for THIS overlay's own gesture, or null.
    ///
    /// <para><b>Never during an alt-drag</b> (owner, 2026-08-17: "the wire vertex snap glyph is still
    /// rendered when the user performs an alt drag to change span"). An alt-drag SCALES: there is no
    /// point being placed, so nothing to mark — and because <c>AltDragFrame</c> returns before
    /// <c>SnapPoint</c> is ever called, the marker left over from the last HOVER would otherwise sit
    /// frozen on screen for the whole gesture, marking a vertex the wire has since moved away from.
    /// That is the same freeze <see cref="ILayoutCanvasOverlay.SnapMarker"/> exists to prevent, one
    /// gesture further along.</para>
    /// </summary>
    public SnapCandidate? SnapMarker =>
        !_altDrag && (_drawStart is not null || _dragging) ? _snapMarker : null;

    private SnapCandidate? _snapMarker;

    /// <summary>
    /// Whether the press just consumed landed on nothing — every wire missed and the layout had nothing
    /// there either, so a wire marquee was started on genuinely empty space.
    ///
    /// <para>The canvas reads it and clears the LAYOUT's selection, which this overlay cannot reach.
    /// A click on empty space means "deselect" whichever selection the user was holding, and before
    /// this the layout half of the wBond editor kept its selection through every such click (owner,
    /// 2026-08-17).</para>
    /// </summary>
    public bool ConsumedPressWasEmptySpace => _pressWasEmptySpace;

    private bool _pressWasEmptySpace;

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
                WBondRenderer.DrawGhostWire(canvas, ghost, viewport, Theme, DbuPerMicron, Thickness);
        }
        finally { canvas.Restore(); }
    }

    // ---------------------------------------------------------------- pointer

    public bool OnPointerPressed(long worldX, long worldY, long tolDbu, KeyModifiers modifiers, int clickCount)
    {
        _pressWasEmptySpace = false;

        // Whatever the last gesture snapped to is not this one's answer. Cleared here rather than at
        // the end of a gesture because a press is the one event that always precedes a new one.
        _snapMarker = null;

        // A companion marquee whose release was delivered elsewhere (the layout editor captures the
        // pointer for its own drag) must not survive into this press — the same "a release is not
        // guaranteed to arrive" rule OnFocusLost exists for.
        _companionMarquee = false;

        if (IsAtDepth) return false;   // locked reference — every gesture belongs to the layout editor

        long xNm = WBondSnap.ToNm(worldX, DbuPerMicron);
        long yNm = WBondSnap.ToNm(worldY, DbuPerMicron);
        double tolNm = WBondSnap.ToNm(tolDbu, DbuPerMicron);

        // A layout drawing tool is armed: every press belongs to it, including one on a wire — a
        // rectangle started on top of a bond wire is an ordinary thing to draw. This is checked
        // before the wire tools rather than beside them because the two are mutually exclusive
        // upstream (WBondEditorView.LayoutTools.cs), so at most one of them can be true.
        if (LayoutToolArmed?.Invoke() == true) return false;

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
                        points: WBondDefaults.Points);

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

        // ── ROUTE FIRST, then resolve the selection ───────────────────────────────────────────────
        //
        // The order matters and it used to be the other way round. A press that misses every wire and
        // lands on a bond pad belongs to the layout editor — and resolving the WIRE selection before
        // discovering that CLEARED it, so nudging a pad silently threw away the wires the user had
        // selected. That contradicts §6.3's own contract ("neither selection clears the other, so
        // 'select the pads and the wires landing on them' is one gesture"), which is the whole basis for
        // holding both at once.
        //
        // A press on genuinely EMPTY space is different and still clears: nothing was clicked, so
        // nothing is selected — see the empty-space branch below, which resolves before it routes.
        if (!hit.Found && LayoutHasSomethingAt(worldX, worldY, tolDbu))
        {
            OverlayChanged?.Invoke();
            return false;
        }

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
            // ── The press missed every wire, and the layout has nothing there either ──────────────
            //
            // A press on a THING belongs to whatever owns that thing — that is how a bond pad gets
            // nudged and how a placed cell instance gets moved, in this same view, and it is handled
            // above. This is the genuinely ambiguous case the marquee toggle was added for: who gets
            // EMPTY space.
            //
            // Without that routing the wire marquee took EVERY press on non-wire space, including one
            // squarely on an instance, so a cell dragged in from the project tree could never be picked
            // up again (owner, 2026-08-16: "wBond editor has no way to move cell instances in the
            // layout view once they are placed"). Turning the marquee toggle off was the only way
            // through, which is a mode switch for something that is not a mode.

            // AFTER Press, which has already cleared the selection unless Shift was held — so this is
            // exactly the base the release-time union will be taken against, on either path below.
            _marqueeStartX = xNm;
            _marqueeStartY = yNm;
            _marqueeX = xNm;
            _marqueeY = yNm;
            _marqueeBase = _vm.Selection;
            _vm.PreviewSelection = _marqueeBase;

            if (!WireMarqueeEnabled)
            {
                // The gesture is the LAYOUT's — its own marquee runs — and this overlay only follows
                // the box so the wires inside it come along. Declined, so nothing is consumed.
                _companionMarquee = true;
                return false;
            }

            // The press hit nothing at all — the canvas clears the LAYOUT's selection for it.
            _pressWasEmptySpace = true;
            _marqueeActive = true;
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

        // Dropped rather than left standing: an alt-drag never recomputes it, so a marker from the
        // last hover would survive the whole gesture. SnapMarker also refuses to publish one while
        // _altDrag is set — belt and braces, because the two answer different questions ("is there
        // one" and "should it be shown").
        if (_altDrag) _snapMarker = null;

        _altApplied = 1.0;
        _altReferenceSpan = SpanNm(hit.Wire);
        _altMoveOutputFoot = GrabMovesOutputFoot(hit);
        (_altAxisX, _altAxisY) = ChordDirection(hit.Wire);

        return true;
    }

    /// <summary>
    /// Whether the reference layout has a shape or an instance under the given point, in the layout's
    /// OWN database units — the question "is this press the layout editor's rather than mine".
    ///
    /// <para>Both stacks are asked because both are draggable: a shape and a placed cell instance are
    /// selected and moved by the same press, through two different hit tests
    /// (<see cref="LayoutHitTest.HitStack"/> and <see cref="LayoutHitTest.HitInstanceStack"/>). Asking
    /// only about shapes would fix pads and leave instances exactly as stuck as they were.</para>
    /// </summary>
    private bool LayoutHasSomethingAt(long worldX, long worldY, long tolDbu)
    {
        if (ReferenceLayout is not { } view) return false;

        if (LayoutHitTest.HitStack(view, ReferenceTechnology, worldX, worldY, tolDbu).Count > 0)
            return true;

        return LayoutHitTest.HitInstanceStack(
            view, ReferenceTechnology, ReferenceBaseDir ?? string.Empty,
            worldX, worldY, tolDbu).Count > 0;
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

    /// <summary>One wire's span in nanometres — an XY distance, like every other span in this
    /// application (<see cref="Wire.SpanMetres"/>).</summary>
    private double SpanNm(int wireIndex)
    {
        var wires = _vm.Design.AllWires().ToList();
        if (wireIndex < 0 || wireIndex >= wires.Count) return 0;

        return wires[wireIndex].SpanMetres() * WBondUnits.NmPerMetre;
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
            _drawGhost = LoopShape.CreateSeedWire(start, new Point3(ex, ey, FootZNm),
                                                  WBondDefaults.DiameterNm, WBondDefaults.Material,
                                                  WBondViewModel.DefaultNewWireLoopHeightNm,
                                                  WBondDefaults.Points);
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

        // A marquee the layout editor owns: follow the box, publish the wire preview, consume NOTHING.
        if (_companionMarquee)
        {
            if (!leftButtonDown) { CommitCompanionMarquee(worldX, worldY); return false; }

            long cx = WBondSnap.ToNm(worldX, DbuPerMicron);
            long cy = WBondSnap.ToNm(worldY, DbuPerMicron);
            if (cx == _marqueeX && cy == _marqueeY) return false;

            _marqueeX = cx;
            _marqueeY = cy;
            _vm.PreviewSelection = ResolveMarqueeNow(cx, cy);
            OverlayChanged?.Invoke();
            return false;
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

        // The resulting SPAN lands on the snap pitch (owner, 2026-08-17: "alt drag for span and loop
        // height does not respect the snap distance setting"). The quantity that has to come out round
        // is the span itself, not the cursor position or the scale factor — a factor snapped to
        // anything is meaningless, and a snapped cursor still leaves an arbitrary span whenever the
        // wire started off-grid.
        //
        // Alt does NOT suppress the snap here, which is the app-wide rule everywhere else (R-snp-11):
        // Alt is what selects this gesture, so it cannot also mean "and ignore the grid" — there would
        // be no way left to ask for a snapped scale at all.
        double target = Math.Max(SnapSpan(_altReferenceSpan + along) / _altReferenceSpan, 1e-3);
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

        // Declined, so the layout editor still gets its own release and commits its own marquee.
        if (_companionMarquee) { CommitCompanionMarquee(worldX, worldY); return false; }

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

    /// <summary>
    /// Commits the wires the LAYOUT's marquee caught, through the same controller call the overlay's own
    /// marquee release uses — so the enclose-versus-crossing rule and the crossing promotion to whole
    /// wires are one implementation, not two.
    /// </summary>
    private void CommitCompanionMarquee(long worldX, long worldY)
    {
        _companionMarquee = false;
        _vm.PreviewSelection = null;

        _controller.Marquee(WBondSnap.ToNm(worldX, DbuPerMicron), WBondSnap.ToNm(worldY, DbuPerMicron),
                            _lastModifiers, EditorView.Layout);
        OverlayChanged?.Invoke();
    }

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
        var moving = _rotatePivotIsInputFoot ? wire.Points[^1] : wire.Points[0];

        _rotateStartAngle = Math.Atan2(yNm - pivot.Y, xNm - pivot.X);
        _rotateStartBearing = Math.Atan2(moving.Y - pivot.Y, moving.X - pivot.X);
        _rotateApplied = 0.0;
        _rotateWire = wireIndex;
        _rotating = true;

        _vm.BeginGesture();     // one undo entry for the whole swing
        _controller.BeginDrag();
        return true;
    }

    private void RotateFrame(long xNm, long yNm, KeyModifiers modifiers)
    {
        var wires = _vm.Design.AllWires().ToList();

        // The GRABBED wire — see _rotateWire. The angle has to be measured about the anchor the user
        // is actually swinging around, or the wire under the hand does not follow it.
        if (_rotateWire < 0 || _rotateWire >= wires.Count) return;

        var wire = wires[_rotateWire];
        var pivot = _rotatePivotIsInputFoot ? wire.Points[0] : wire.Points[^1];

        double angle = Math.Atan2(yNm - pivot.Y, xNm - pivot.X) - _rotateStartAngle;

        // Shift snaps the wire onto the ABSOLUTE 15° grid — 0, 15, 30, 45… measured in XY, not 15°
        // from wherever this wire happened to start (owner, 2026-08-17).
        //
        // ABSOLUTE is the whole value of the modifier. A wire sitting at 7° snapped to RELATIVE
        // increments lands on 22°, 37°, 52° — every one as crooked as it started, and the arrangement
        // almost every bond array wants (0/90/180/270) is unreachable by the gesture that exists to
        // reach it. Snapping the wire's own BEARING is what makes Shift mean "put this straight".
        //
        // 15° rather than 45° (owner): 15 divides 45, 90 and 180, so every angle the coarser step
        // could reach is still reachable, and the ones between them — a 30° fan-out leg, a 60°
        // corner — stop needing a free-hand drag to hit.
        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            const double Step = Math.PI / 12.0;
            double bearing = _rotateStartBearing + angle;
            angle = Math.Round(bearing / Step) * Step - _rotateStartBearing;
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
        _rotateWire = -1;
        _controller.EndDrag();
        _vm.EndGesture();
        OverlayChanged?.Invoke();
    }

    private static double Distance(Point3 p, long xNm, long yNm)
    {
        double dx = p.X - xNm, dy = p.Y - yNm;
        return Math.Sqrt(dx * dx + dy * dy);
    }

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

    /// <summary>
    /// Geometry first, then the grid — and "geometry" now includes the OTHER WIRES (owner,
    /// 2026-08-16), their vertices and their segments alike.
    ///
    /// <para>Everything the current gesture is moving is excluded, or the drag would snap to itself and
    /// the wires could not be moved at all. That is the selection while a drag or a marquee is live,
    /// and nothing at all while a wire is being DRAWN — a wire that does not exist yet cannot be its
    /// own snap target, and the two feet being placed must be free to land on a wire already there.</para>
    /// </summary>
    private (long X, long Y) SnapPoint(long xNm, long yNm)
    {
        if (!SnapEnabled) { _snapMarker = null; return (xNm, yNm); }

        // Geometry snap off leaves the GRID running, exactly as it does for a rectangle in the Layout
        // Editor — see GeometrySnapEnabled. No marker either: there is no feature to mark.
        if (!GeometrySnapEnabled)
        {
            _snapMarker = null;
            return GridPitchNm > 0 ? (Round(xNm, GridPitchNm), Round(yNm, GridPitchNm)) : (xNm, yNm);
        }

        HashSet<int>? moving = _dragging || _rotating
            ? [.. _vm.Selection.TouchedWires()]
            : null;

        var snap = WBondSnap.Snap(ReferenceLayout, ReferenceTechnology, ReferenceBaseDir,
                                  xNm, yNm, SnapToleranceNm,
                                  includeIntersections: IncludeIntersections,
                                  wires: _vm.Design,
                                  excludeWire: moving is null ? null : moving.Contains);

        // The GLYPH is published from here rather than computed a second time by whoever draws it, so
        // what the user sees is by construction the feature the point actually landed on. A GRID snap
        // deliberately publishes nothing: the layout editor's own marker has never marked the grid
        // either, and a mark on every pointer position would say nothing.
        _snapMarker = snap.Snapped
            ? new SnapCandidate(snap.Kind,
                                WBondSnap.ToDbu(snap.XNm, DbuPerMicron),
                                WBondSnap.ToDbu(snap.YNm, DbuPerMicron),
                                default, false, -1)
            : null;

        if (snap.Snapped) return (snap.XNm, snap.YNm);

        return GridPitchNm > 0 ? (Round(xNm, GridPitchNm), Round(yNm, GridPitchNm)) : (xNm, yNm);
    }

    /// <summary>
    /// A span quantised to the snap pitch, never below one pitch.
    ///
    /// <para>The floor is not padding: rounding a big shrinking drag can reach zero, and a wire whose
    /// feet coincide has no chord — the scale would fold it through itself and the fill would have a
    /// zero-length filament to integrate along. One pitch is the shortest span the grid can express.</para>
    /// </summary>
    private double SnapSpan(double spanNm)
    {
        if (GridPitchNm <= 0) return spanNm;
        return Math.Max(Math.Round(spanNm / GridPitchNm) * GridPitchNm, GridPitchNm);
    }

    /// <summary>Nearest multiple of <paramref name="pitch"/>, correct for negatives.</summary>
    private static long Round(long value, long pitch) =>
        (long)Math.Round((double)value / pitch) * pitch;

    // ---------------------------------------------------------------- keyboard

    public bool OnKeyDown(Key key, KeyModifiers modifiers)
    {
        // W is the DRAW WIRE tool now (owner, 2026-08-16) and is claimed by the editor view, so the
        // old w+click "promote to the whole wire" gesture is gone rather than fighting it for the
        // key. Double-clicking a point or a segment already selects the whole wire, which is the
        // gesture every other editor in this application uses for the same promotion.
        if (key == Key.G) { _gHeld = true; return false; }   // a held promotion key, not a command

        if (IsAtDepth) return false;

        // ── Ctrl/Cmd+A selects the WIRES as well as the geometry ─────────────────────────────────
        //
        // Owner, 2026-08-19: "Control/CMD+A does not select all. (Everything should be selected —
        // wires, primitives, cells, etc.)" It selected every shape and every instance and no wire at
        // all, which on a wirebond cell — where the wires ARE most of what is on screen — reads as
        // the key doing nothing.
        //
        // Returning FALSE on purpose, which is the whole trick: false means "not consumed", so the
        // key goes on to reach LayoutEditorViewModel.OnKeyDown and its own SelectAllCommand runs
        // immediately after this. The two selections are separate by design (§6.3 — neither clears
        // the other) and so are the two commands; this is the same pairing the context menu's own
        // "Select All" already makes, and for the same reason it goes through the layout editor's
        // command rather than reimplementing what belongs in a select-all.
        if ((modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && key == Key.A)
        {
            _vm.SelectAllWires();
            OverlayChanged?.Invoke();
            return false;
        }

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

        // Arrow keys, and Delete, are claimed ONLY when the overlay has a selection — otherwise they
        // are the layout editor's own nudge and its own delete, in the same view.
        if (_vm.Selection.IsEmpty) return false;

        // Delete removes the selected WIRES (owner, 2026-08-17: the key did nothing in the Layout
        // Editor over a wirebond cell). The standalone wBond editor has always had this, on its own
        // tunnel handler — but that handler is on an ancestor of THIS view and does not exist in the
        // ordinary Layout Editor, where the wires reach the canvas through the overlay seam instead.
        // Gated on a wire selection above, so a Delete meant for a selected SHAPE still falls through
        // to the layout editor untouched.
        if (key is Key.Delete or Key.Back)
        {
            // DeleteSelection, not DeleteSelectedWires: a selection of SEGMENTS removes those
            // segments, not the wires they belong to (owner, 2026-08-17). The dispatch is the view
            // model's — see WBondViewModel.DeleteSelection.
            if (_vm.DeleteSelection() > 0) NotifyChanged();
            return true;
        }

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
        if (key == Key.G) _gHeld = false;
    }

    /// <summary>
    /// Focus has left the canvas — drop the held-<c>g</c> latch whether or not its release arrived.
    /// See <see cref="ILayoutCanvasOverlay.OnFocusLost"/> for why it may not have.
    /// </summary>
    public void OnFocusLost()
    {
        _gHeld = false;

        // …and a gesture that was still running when focus left is UNWOUND, not left half-open. See
        // AbandonGesture: a stranded drag leaves the wire COLLAPSED TO ITS CHORD, which is a loop the
        // user can watch disappear.
        AbandonGesture();

        // A held-key latch is not the only thing a lost release can strand: a companion marquee left
        // set would go on re-resolving the wire selection on every later hover. Its preview is dropped
        // with it — and ONLY with it, because the profile canvas publishes into the same slot and its
        // own live marquee must survive this canvas losing focus.
        if (_companionMarquee)
        {
            _companionMarquee = false;
            _vm.PreviewSelection = null;
        }
    }

    /// <summary>
    /// Ends a drag or a swing that is still open although the gesture is plainly over — focus has left
    /// the canvas, or a right-click has arrived.
    ///
    /// <para><b>A stranded gesture is not merely untidy: it can lose the wire's shape.</b> While a
    /// drag runs, <c>WBondPointerController</c> may COLLAPSE a wire to its two feet (the quality
    /// ladder's cheapest rung) and restore the interior points at <c>EndDrag</c>. If the release never
    /// arrives — the pointer left the window, a toolbar button took focus — the wire stays collapsed:
    /// it draws as a straight line, "Straighten Wire" reports that it has only its two feet, and the
    /// NEXT <c>BeginDrag</c> clears the record of what was collapsed, at which point the loop is gone
    /// for good. That is the owner's 2026-08-17 report about the menu item being disabled until the
    /// menu is closed and reopened: moving the mouse to reopen it delivered the move that unwound the
    /// gesture.</para>
    ///
    /// <para>Safe to call when nothing is running — every branch is guarded, and this must be, since
    /// both callers fire on ordinary events.</para>
    /// </summary>
    private void AbandonGesture()
    {
        if (_rotating) { EndRotate(); return; }

        if (!_pressed && !_dragging) return;

        bool wasDragging = _dragging;

        _pressed = false;
        _dragging = false;
        _altDrag = false;
        _deferredPress = null;

        if (!wasDragging) return;

        _controller.EndDrag();   // restores whatever was collapsed, and publishes the final answer
        _vm.EndGesture();
        OverlayChanged?.Invoke();
    }

    private WBondModifiers Modifiers(KeyModifiers modifiers)
    {
        var result = WBondModifiers.None;
        if ((modifiers & KeyModifiers.Shift) != 0) result |= WBondModifiers.Shift;
        if ((modifiers & KeyModifiers.Alt) != 0) result |= WBondModifiers.Alt;
        if (_gHeld) result |= WBondModifiers.WholeGroup;
        return result;
    }
}
