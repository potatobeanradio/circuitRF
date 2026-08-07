// ================================================================
//  HarmonicaPointer.cs  —  M1 of brief-harmonicarf-h6
//
//  R-h6-1  ONE hit-test, resolved through GammaToCanvas / CanvasToGamma. A hit-test that inverted
//          PlotRenderer's own transform is off by the annulus-headroom factor — visibly, at the rim,
//          which is exactly where markers sit.
//  R-h6-2  the grab radius is in DEVICE PIXELS and is computed PER EVENT. This repo has burned itself
//          on a cached tolerance twice (src/Ui/CLAUDE.md's L1c and L1-fix entries).
//  R-h6-3  the drag never writes to the model mid-gesture beyond the marker itself.
//  R-h6-4  the frame loop is the SCHEDULER's, not the gesture's.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Harmonica;

/// <summary>What a pointer-down landed on.</summary>
public enum HarmonicaGrabKind
{
    /// <summary>Nothing grabbable was under the pointer.</summary>
    None,

    /// <summary>A round termination marker — M1's drag. The extrinsic Γ <i>is</i> the thing being set,
    /// so no inverse solve is involved.</summary>
    ExtrinsicMarker,

    /// <summary>A triangular intrinsic glyph — M2's drag, which runs the inverse solve.</summary>
    IntrinsicGlyph,

    /// <summary>
    /// A Γ grid sample dot (§6.4 / R-h7-12). Beneath the glyphs in z-order, so it is the THIRD pass:
    /// a grid point sitting under a marker must never be grabbed in preference to it.
    /// </summary>
    GridPoint,
}

/// <summary>One resolved grab: what, on which panel.</summary>
/// <param name="GridIndex">
/// Which grid point, for <see cref="HarmonicaGrabKind.GridPoint"/>; −1 otherwise. An index rather
/// than a Γ value, because the drag moves it and the identity has to survive that.
/// </param>
public readonly record struct HarmonicaGrab(
    HarmonicaGrabKind Kind, HarmonicaMarker? Marker, string PanelId, int GridIndex = -1)
{
    public static readonly HarmonicaGrab None = new(HarmonicaGrabKind.None, null, "");
    public bool IsGrab => Kind != HarmonicaGrabKind.None;
}

/// <summary>
/// The hit test, on its own so it can be exercised without a window.
///
/// <para><b>Everything here is computed per call.</b> Nothing about a panel's size, a marker's canvas
/// position or the grab radius is cached — the whole R-h6-2 failure mode is a tolerance that was
/// correct at the size it was computed at and wrong afterwards.</para>
/// </summary>
public static class HarmonicaHitTest
{
    /// <summary>
    /// The grab radius, in DEVICE pixels. Comfortably larger than the marker itself: a hit target
    /// smaller than the glyph is the thing users report as "it does not grab".
    /// </summary>
    public const double GrabRadiusDevicePixels = 14.0;

    /// <summary>The two panels a marker can be grabbed on. §6.5 gives each its own plane and harmonic
    /// selectors; both presently show the same marker list (R-h45-3), and both are hit-testable.</summary>
    public static readonly string[] SmithPanels =
        [HarmonicaPanelId.SmithPower, HarmonicaPanelId.SmithEfficiency];

    /// <summary>Which panel a canvas point is in, or null.</summary>
    public static string? PanelAt(CharmLayout layout, double x, double y, double w, double h)
    {
        foreach (string id in SmithPanels)
        {
            var p = layout.PlacementOf(id);
            double px = p.X * w, py = p.Y * h, pw = p.W * w, ph = p.H * h;
            if (x >= px && x < px + pw && y >= py && y < py + ph) return id;
        }
        return null;
    }

    /// <summary>A canvas point expressed in one panel's own coordinates, and that panel's size.</summary>
    public static (SKPoint Local, (double W, double H) Size) ToPanel(
        CharmLayout layout, string panelId, double x, double y, double w, double h)
    {
        var p = layout.PlacementOf(panelId);
        return (new SKPoint((float)(x - p.X * w), (float)(y - p.Y * h)),
                (p.W * w, p.H * h));
    }

    /// <summary>
    /// Resolves a pointer-down to a marker or a glyph.
    ///
    /// <para><b>Markers are tested before glyphs, because markers are drawn ON TOP of them</b>
    /// (R-h45-4's z-order). A hit test that disagreed with the z-order would grab the thing the user
    /// cannot see.</para>
    /// </summary>
    /// <param name="renderScaling">
    /// Device pixels per DIP. The radius is a DEVICE-pixel constant, so it is divided by this every
    /// time rather than being stored in whatever unit the last caller happened to use.
    /// </param>
    /// <param name="gridPoints">
    /// R-h7-12's Γ samples, in the order the grid holds them. Tested LAST, because they are drawn
    /// beneath the glyphs and a hit test that disagreed with the z-order would grab the thing the
    /// user cannot see. Their grab radius is deliberately SMALLER than a marker's — a grid can carry
    /// sixty of them and a marker-sized target would make the two indistinguishable.
    /// </param>
    public static HarmonicaGrab Resolve(CharmLayout layout, IReadOnlyList<HarmonicaMarker> markers,
                                        double x, double y, double w, double h,
                                        double renderScaling = 1.0,
                                        double grabRadiusDevicePixels = GrabRadiusDevicePixels,
                                        IReadOnlyList<HarmonicaGridPoint>? gridPoints = null)
    {
        if (w <= 0 || h <= 0) return HarmonicaGrab.None;
        if (markers.Count == 0 && (gridPoints is null || gridPoints.Count == 0))
            return HarmonicaGrab.None;

        string? panelId = PanelAt(layout, x, y, w, h);
        if (panelId is null) return HarmonicaGrab.None;

        var (local, size) = ToPanel(layout, panelId, x, y, w, h);
        double radius = grabRadiusDevicePixels / Math.Max(1e-9, renderScaling);
        double r2 = radius * radius;

        HarmonicaMarker? best = null;
        double bestD2 = double.MaxValue;

        // Pass 1 — the round termination markers, which are on top.
        foreach (var m in markers)
        {
            double d2 = Distance2(HarmonicaPanelRenderer.MarkerToCanvas(m.Gamma, size), local);
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; best = m; }
        }
        if (best is not null) return new HarmonicaGrab(HarmonicaGrabKind.ExtrinsicMarker, best, panelId);

        // Pass 2 — the intrinsic glyphs beneath them.
        foreach (var m in markers)
        {
            double d2 = Distance2(HarmonicaPanelRenderer.MarkerToCanvas(m.GammaIntrinsic, size), local);
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; best = m; }
        }
        if (best is not null) return new HarmonicaGrab(HarmonicaGrabKind.IntrinsicGlyph, best, panelId);

        // Pass 3 — the Γ grid samples, beneath everything.
        if (gridPoints is null) return HarmonicaGrab.None;

        double gridRadius = GridPointGrabRadiusDevicePixels / Math.Max(1e-9, renderScaling);
        double g2 = gridRadius * gridRadius;
        int bestIndex = -1;
        bestD2 = double.MaxValue;

        for (int i = 0; i < gridPoints.Count; i++)
        {
            // Grid points are Γ values on the chart, NOT on the compressed radial scale — they are
            // drawn through GammaToCanvas. Hit-testing them through MarkerToCanvas would be off by
            // the compression wherever |Γ| approaches 1, which is where the outer ring sits.
            double d2 = Distance2(HarmonicaPanelRenderer.GammaToCanvas(gridPoints[i].Gamma, size), local);
            if (d2 <= g2 && d2 < bestD2) { bestD2 = d2; bestIndex = i; }
        }

        return bestIndex < 0
            ? HarmonicaGrab.None
            : new HarmonicaGrab(HarmonicaGrabKind.GridPoint, null, panelId, bestIndex);
    }

    /// <summary>
    /// R-h7-12's grab radius for a Γ grid dot, in DEVICE pixels. Smaller than a marker's on purpose:
    /// a 61-point grid at a marker's 14 px would leave no gaps between targets at all.
    /// </summary>
    public const double GridPointGrabRadiusDevicePixels = 7.0;

    private static double Distance2(SKPoint a, SKPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }
}

/// <summary>
/// The gesture itself: pointer-down picks a marker or a glyph, pointer-move drives the frame loop
/// with <c>dragging: true</c>, pointer-up with <c>dragging: false</c>.
///
/// <para><b>This class picks nothing about frame quality</b> (R-h6-4). It calls
/// <see cref="HarmonicaViewModel.RequestScheduledFrame"/> and the scheduler decides rings, spokes,
/// raster and freeze-and-snap. Feeding the loop is the gesture's other job and is not optional: a
/// ladder that is never told what a frame cost can never degrade, and D4's status message can never
/// fire.</para>
///
/// <para><b>Framework-free on purpose.</b> It takes doubles, not
/// <c>PointerEventArgs</c> — so a synthetic pointer sequence in a headless test drives exactly the
/// code the window drives, rather than a parallel path written to be testable.</para>
/// </summary>
public sealed class HarmonicaGesture(HarmonicaViewModel viewModel)
{
    private readonly HarmonicaViewModel _vm =
        viewModel ?? throw new ArgumentNullException(nameof(viewModel));

    /// <summary>R-h6-2 — DEVICE pixels, never Γ, never cached.</summary>
    public double GrabRadiusDevicePixels { get; init; } = HarmonicaHitTest.GrabRadiusDevicePixels;

    /// <summary>What the pointer currently holds, if anything.</summary>
    public HarmonicaGrab Grab { get; private set; } = HarmonicaGrab.None;

    public bool IsDragging => Grab.IsGrab;

    /// <summary>How many pointer-moves this gesture has processed. A drag's own frame count.</summary>
    public int MoveCount { get; private set; }

    /// <summary>
    /// What an Edit Display drag is currently moving, or <see cref="HarmonicaEditGrab.None"/>. Kept
    /// beside <see cref="Grab"/> rather than folded into it: the two hit tests answer questions about
    /// different things (panels vs. markers) over different sets, and edit mode makes one of them
    /// unreachable entirely.
    /// </summary>
    public HarmonicaEditGrab EditGrab { get; private set; }
    public string EditPanelId { get; private set; } = "";

    private double _editAnchorX, _editAnchorY;
    private CharmPanelPlacement _editStart;

    public bool PointerDown(double x, double y, double w, double h, double renderScaling = 1.0)
    {
        MoveCount = 0;

        // §7.7 — while unlocked, the canvas edits the LAYOUT and nothing else. A marker drag and a
        // panel drag on the same pointer would be ambiguous the moment a marker sat under a grip.
        if (_vm.EditDisplay.Unlocked)
        {
            var (kind, panelId) = HarmonicaEditTarget.Resolve(
                _vm.Layout, _vm.PickedTraces, x, y, w, h, renderScaling);

            EditGrab    = kind;
            EditPanelId = panelId;
            Grab        = HarmonicaGrab.None;
            if (kind == HarmonicaEditGrab.None) return false;

            _editAnchorX = x / w;
            _editAnchorY = y / h;
            _editStart   = _vm.Layout.PlacementOf(panelId);
            _vm.EditDisplay.BeginGesture();
            return true;
        }

        var grab = HarmonicaHitTest.Resolve(_vm.Layout, _vm.Markers, x, y, w, h,
                                            renderScaling, GrabRadiusDevicePixels,
                                            GridPointsOf(_vm));
        Grab      = grab;
        if (!grab.IsGrab) return false;

        if (grab.Kind == HarmonicaGrabKind.IntrinsicGlyph)
            _vm.BeginIntrinsicDrag(grab.Marker!);
        else if (grab.Kind == HarmonicaGrabKind.GridPoint)
            _vm.BeginGridPointDrag(grab.GridIndex);

        return true;
    }

    /// <summary>The Γ samples the last frame drew. Both Smith panels show the same grid, so which
    /// panel the pointer is over does not change the answer.</summary>
    private static IReadOnlyList<HarmonicaGridPoint> GridPointsOf(HarmonicaViewModel vm)
        => vm.Frame.SmithPower.GridPoints;

    public void PointerMoved(double x, double y, double w, double h, double renderScaling = 1.0)
    {
        if (EditGrab != HarmonicaEditGrab.None) { MoveCount++; ApplyEdit(x, y, w, h); return; }
        if (!Grab.IsGrab) return;
        MoveCount++;
        Apply(x, y, w, h, dragging: true);
    }

    public void PointerUp(double x, double y, double w, double h, double renderScaling = 1.0)
    {
        if (EditGrab != HarmonicaEditGrab.None)
        {
            ApplyEdit(x, y, w, h);
            // R-h7-9's counter shape: ONE undo entry for the whole gesture, pushed here, not one per
            // pointer move.
            _vm.EditDisplay.EndGesture();
            EditGrab = HarmonicaEditGrab.None;
            EditPanelId = "";
            return;
        }

        if (!Grab.IsGrab) return;
        Apply(x, y, w, h, dragging: false);

        if (Grab.Kind == HarmonicaGrabKind.IntrinsicGlyph) _vm.EndIntrinsicDrag();
        else if (Grab.Kind == HarmonicaGrabKind.GridPoint) _vm.EndGridPointDrag();
        Grab = HarmonicaGrab.None;
    }

    /// <summary>Abandons the gesture without applying anything further — a lost pointer capture, or
    /// Escape. The marker keeps whatever the last applied move gave it; an EDIT gesture is rolled
    /// back, because a half-finished panel drag is not a placement anybody chose.</summary>
    public void Cancel()
    {
        if (EditGrab != HarmonicaEditGrab.None)
        {
            _vm.EditDisplay.CancelGesture();
            EditGrab = HarmonicaEditGrab.None;
            EditPanelId = "";
            return;
        }

        if (Grab.Kind == HarmonicaGrabKind.IntrinsicGlyph) _vm.EndIntrinsicDrag();
        else if (Grab.Kind == HarmonicaGrabKind.GridPoint) _vm.EndGridPointDrag();
        Grab = HarmonicaGrab.None;
    }

    /// <summary>One frame of an Edit Display drag, in layout FRACTIONS — the units
    /// <see cref="CharmLayout"/> is in, so nothing converts twice.</summary>
    private void ApplyEdit(double x, double y, double w, double h)
    {
        if (w <= 0 || h <= 0) return;
        double fx = x / w, fy = y / h;

        if (EditGrab == HarmonicaEditGrab.Move)
        {
            // Placed absolutely from the gesture's own anchor rather than accumulated per move: an
            // accumulated delta drifts by every clamp the edge applies, so a panel dragged into a
            // corner and back would not return to where it started.
            _vm.EditDisplay.PlacePanel(EditPanelId,
                _editStart.X + (fx - _editAnchorX), _editStart.Y + (fy - _editAnchorY),
                _editStart.W, _editStart.H);
        }
        else
        {
            _vm.EditDisplay.ResizePanel(EditPanelId, fx - _editStart.X, fy - _editStart.Y);
        }
    }

    private void Apply(double x, double y, double w, double h, bool dragging)
    {
        // The Γ under the pointer, through the SAME transform pair the renderer drew the marker with
        // (R-h6-1). Resolved against the panel the gesture STARTED on, not the one the pointer is
        // over now: dragging off the edge of a panel must not teleport the marker into the other
        // chart's coordinate frame.
        var (local, size) = HarmonicaHitTest.ToPanel(_vm.Layout, Grab.PanelId, x, y, w, h);
        var gamma = HarmonicaPanelRenderer.CanvasToMarker(local, size);

        if (Grab.Kind == HarmonicaGrabKind.GridPoint)
        {
            // R-h7-12 — a grid point is a Γ SAMPLE, not a marker, so it lives on the raw chart
            // transform rather than the compressed radial one.
            _vm.DragGridPoint(Grab.GridIndex,
                              HarmonicaPanelRenderer.CanvasToGamma(local, size), dragging);
            return;
        }

        if (Grab.Kind == HarmonicaGrabKind.ExtrinsicMarker)
        {
            // R-h6-3 — the marker IS the live preview. Nothing else in the model is touched, and the
            // TerminationSet is not cloned per move: RequestFrame already snapshots what it needs.
            _vm.SetMarkerGamma(Grab.Marker!, gamma);
            _vm.RequestScheduledFrame(dragging);
        }
        else
        {
            // M2 — the target is the INTRINSIC Γ under the pointer; the extrinsic terminations that
            // put it there are what the solve finds. A failure surfaces in the view model's status
            // message, not here, because it arrives with the frame rather than with the gesture.
            _vm.DragIntrinsicGlyph(Grab.Marker!, gamma, dragging);
        }
    }
}
