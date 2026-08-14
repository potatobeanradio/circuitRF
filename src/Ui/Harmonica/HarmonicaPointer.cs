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

    /// <summary>
    /// R-h9r2-8's VSWR-circle drag handle. The circle is drawn beneath a marker's own round glyph, and
    /// above the intrinsic glyphs — so it is tested after markers and intrinsic glyphs, ahead of grid
    /// points (a handle sitting under a grid point must not lose to it).
    /// </summary>
    VswrHandle,
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
    /// <param name="gridPointsVisible">
    /// R-h9b-7 — grid points are draggable only while shown. Passed explicitly rather than the caller
    /// substituting <c>null</c> for <paramref name="gridPoints"/> to mean "off", so the reason a point
    /// is untestable is legible at the call site rather than indistinguishable from "no grid solved
    /// yet". Defaults true so a caller with no visibility concept of its own (most direct tests) keeps
    /// today's behaviour.
    /// </param>
    /// <param name="topmost">
    /// R-h9r2-5 — the session's promoted marker (<see cref="HarmonicaViewModel.TopmostMarker"/>), fed
    /// straight to <see cref="HarmonicaMarkerZOrder.RankOf"/> so this hit test agrees with the
    /// renderer about what is visually on top.
    /// </param>
    /// <param name="z0">
    /// R-h9r2-8 — the panel's own reference impedance, needed to place a VSWR handle exactly:
    /// <see cref="HarmonicaVswrHandle.HandleGamma"/> is only correct once it goes through the real
    /// Möbius-circle geometry, which is Z0-dependent off the matched point. Defaults to 50 Ω for
    /// callers with no panel context (most direct tests).
    /// </param>
    public static HarmonicaGrab Resolve(CharmLayout layout, IReadOnlyList<HarmonicaMarker> markers,
                                        double x, double y, double w, double h,
                                        double renderScaling = 1.0,
                                        double grabRadiusDevicePixels = GrabRadiusDevicePixels,
                                        IReadOnlyList<HarmonicaGridPoint>? gridPoints = null,
                                        bool gridPointsVisible = true,
                                        HarmonicaMarker? topmost = null,
                                        double z0 = 50.0)
    {
        if (w <= 0 || h <= 0) return HarmonicaGrab.None;
        if (!gridPointsVisible) gridPoints = null;
        if (markers.Count == 0 && (gridPoints is null || gridPoints.Count == 0))
            return HarmonicaGrab.None;

        string? panelId = PanelAt(layout, x, y, w, h);
        if (panelId is null) return HarmonicaGrab.None;

        var (local, size) = ToPanel(layout, panelId, x, y, w, h);
        double radius = grabRadiusDevicePixels / Math.Max(1e-9, renderScaling);
        double r2 = radius * radius;

        HarmonicaMarker? best = null;
        double bestD2 = double.MaxValue;

        // Pass 1 — the round termination markers, which are on top. R-h9r2-5: prefer the TOPMOST-RANK
        // candidate among everything within the grab radius (matching what the renderer actually
        // painted last), falling back to nearest only to break a tie at equal rank.
        int bestRank = int.MinValue;
        foreach (var m in markers)
        {
            double d2 = Distance2(HarmonicaPanelRenderer.MarkerToCanvas(m.Gamma, size), local);
            if (d2 > r2) continue;

            int rank = HarmonicaMarkerZOrder.RankOf(m, topmost);
            if (rank > bestRank || (rank == bestRank && d2 < bestD2))
            {
                bestRank = rank;
                bestD2   = d2;
                best     = m;
            }
        }
        if (best is not null) return new HarmonicaGrab(HarmonicaGrabKind.ExtrinsicMarker, best, panelId);

        // Pass 2 — the intrinsic glyphs beneath them.
        foreach (var m in markers)
        {
            double d2 = Distance2(HarmonicaPanelRenderer.MarkerToCanvas(m.GammaIntrinsic, size), local);
            if (d2 <= r2 && d2 < bestD2) { bestD2 = d2; best = m; }
        }
        if (best is not null) return new HarmonicaGrab(HarmonicaGrabKind.IntrinsicGlyph, best, panelId);

        // Pass 2.5 — R-h9r2-8's VSWR circle, one per marker with the overlay on. brief-harmonicarf-r6b
        // §1.1: there is no gripper any more — the whole circumference is grabbable, sampled at
        // LoadpullSurface.VswrLocus's own default resolution (the SAME polyline DrawVswrLocus draws)
        // and hit-tested by point-to-SEGMENT distance so a coarse polyline still reads as a smooth
        // circle to the pointer. Tested through the SAME raw-Gamma transform the locus is drawn with
        // (GammaToCanvas, never MarkerToCanvas — the locus is not on the compressed intrinsic scale).
        double vswrRadius = VswrHandleGrabRadiusDevicePixels / Math.Max(1e-9, renderScaling);
        double v2 = vswrRadius * vswrRadius;
        bestD2 = double.MaxValue;
        foreach (var m in markers)
        {
            if (!m.VswrEnabled) continue;
            var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(
                m.Gamma, m.VswrValue, RfCore.Loadpull.SurfacePlane.Gamma, new Complex(z0, 0.0));
            if (pts is null || pts.Length < 2) continue;

            for (int i = 0; i < pts.Length; i++)
            {
                var a = HarmonicaPanelRenderer.GammaToCanvas(pts[i], size);
                var b = HarmonicaPanelRenderer.GammaToCanvas(pts[(i + 1) % pts.Length], size);
                double d2 = Distance2ToSegment(local, a, b);
                if (d2 <= v2 && d2 < bestD2) { bestD2 = d2; best = m; }
            }
        }
        if (best is not null) return new HarmonicaGrab(HarmonicaGrabKind.VswrHandle, best, panelId);

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

    /// <summary>R-h9r2-8's grab radius for the VSWR circle, in DEVICE pixels — measured PERPENDICULAR
    /// to the circumference (via <see cref="Distance2ToSegment"/>), not to any single point on it.
    /// There is at most one circle per marker, so it can be a comfortable size without any risk of
    /// crowding.</summary>
    public const double VswrHandleGrabRadiusDevicePixels = 9.0;

    private static double Distance2(SKPoint a, SKPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>Squared distance from <paramref name="p"/> to the segment <paramref name="a"/>–
    /// <paramref name="b"/> — the same point-to-segment measure the Data Display's own
    /// <c>HitTestVswrLocus</c> uses (there, non-squared; squared here to match every other distance
    /// comparison in this file, which all compare against a squared radius).</summary>
    private static double Distance2ToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-9) return Distance2(p, a);

        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        double ex = p.X - cx, ey = p.Y - cy;
        return ex * ex + ey * ey;
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

    /// <summary>
    /// R-h9b-1 — "this gesture is live", for the canvas's pointer-moved/pointer-released gate. Deliberately
    /// NOT folded into <see cref="IsDragging"/>: <see cref="HarmonicaHitTest"/>'s own callers and
    /// <c>PointerUp</c>'s marker/glyph/grid branches read <c>Grab.Kind</c>, and making an edit grab
    /// look like a marker drag to them would be a second, worse bug in the same place this one lived.
    /// </summary>
    public bool IsLive => IsDragging || EditGrab != HarmonicaEditGrab.None;

    /// <summary>How many pointer-moves this gesture has processed. A drag's own frame count.</summary>
    public int MoveCount { get; private set; }

    /// <summary>
    /// R-h9b-3's own diagnosability ask: what the last <see cref="PointerDown"/> resolved to, kept
    /// after <see cref="PointerUp"/> resets <see cref="Grab"/> back to <c>None</c> — so "did the press
    /// even grab anything" survives the gesture rather than only being observable mid-drag.
    /// </summary>
    public HarmonicaGrabKind LastGrabKind { get; private set; } = HarmonicaGrabKind.None;

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

    // ── brief-harmonicarf-r6b §1.3 — the live VSWR readout, mirroring Data Display's
    // PlotControl._vswrReadoutActive/_vswrReadoutPt (set on press AND move, cleared on release/cancel).
    // Canvas-space (not panel-local), since HarmonicaCanvasRenderer draws it OUTSIDE any panel's own
    // clip so it is never cut off at a panel edge (§1.3's "unclipped, last" rule).

    /// <summary>Whether the live "VSWR: …" readout should be drawn this frame.</summary>
    public bool VswrReadoutActive { get; private set; }

    /// <summary>The pointer's own canvas-space position, for the readout's <c>+10, −10</c> offset.</summary>
    public (double X, double Y) VswrReadoutPointer { get; private set; }

    /// <summary>The readout's own text — <c>HarmonicaReadoutFormatting.FormatVswr</c>, the SAME
    /// formatter §2.1's menu header uses, so the number a drag lands on is the number the menu then
    /// shows.</summary>
    public string VswrReadoutText { get; private set; } = "";

    public bool PointerDown(double x, double y, double w, double h, double renderScaling = 1.0)
    {
        MoveCount = 0;
        VswrReadoutActive = false;

        // §7.7 — while unlocked, the canvas edits the LAYOUT and nothing else. A marker drag and a
        // panel drag on the same pointer would be ambiguous the moment a marker sat under a grip.
        if (_vm.EditDisplay.Unlocked)
        {
            var (kind, panelId) = HarmonicaEditTarget.Resolve(
                _vm.Layout, _vm.PickedTraces, x, y, w, h, renderScaling);

            EditGrab     = kind;
            EditPanelId  = panelId;
            Grab         = HarmonicaGrab.None;
            LastGrabKind = HarmonicaGrabKind.None;
            if (kind == HarmonicaEditGrab.None) return false;

            _editAnchorX = x / w;
            _editAnchorY = y / h;
            _editStart   = _vm.Layout.PlacementOf(panelId);
            _vm.EditDisplay.BeginGesture();
            return true;
        }

        var grab = HarmonicaHitTest.Resolve(_vm.Layout, _vm.Markers, x, y, w, h,
                                            renderScaling, GrabRadiusDevicePixels,
                                            GridPointsOf(_vm), _vm.ShowGridPoints, _vm.TopmostMarker,
                                            _vm.Frame.SmithPower.Z0);
        Grab         = grab;
        LastGrabKind = grab.Kind;
        if (!grab.IsGrab) return false;

        // R-h9r2-5 — a successful grab of a marker's own round glyph, its intrinsic glyph, or (R-h9r2-8)
        // its VSWR-circle handle promotes that marker to the top of the z-order for the rest of the
        // session — grabbing the handle is interacting with the marker, same as grabbing the marker.
        if (grab.Kind is HarmonicaGrabKind.ExtrinsicMarker or HarmonicaGrabKind.IntrinsicGlyph
                       or HarmonicaGrabKind.VswrHandle)
            _vm.PromoteMarker(grab.Marker!);

        if (grab.Kind == HarmonicaGrabKind.IntrinsicGlyph)
            _vm.BeginIntrinsicDrag(grab.Marker!);
        else if (grab.Kind == HarmonicaGrabKind.GridPoint)
            _vm.BeginGridPointDrag(grab.GridIndex);
        else if (grab.Kind == HarmonicaGrabKind.VswrHandle)
        {
            // §1.3 — shown on a plain click too, not only once a move has happened, so the user can
            // click the circle to read its value without moving it (mirrors Data Display's own press
            // behaviour). Shows the CURRENT value; Apply below updates it once the pointer actually moves.
            VswrReadoutActive  = true;
            VswrReadoutPointer = (x, y);
            VswrReadoutText    = HarmonicaReadoutFormatting.FormatVswr(grab.Marker!.VswrValue);
        }

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
        // §1.3 — cleared on release, mirroring PlotControl.cs:1110.
        VswrReadoutActive = false;
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
        VswrReadoutActive = false;
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

        if (Grab.Kind == HarmonicaGrabKind.VswrHandle)
        {
            // R-h9r2-8 — the locus, like a grid point, lives on the raw chart transform, never the
            // compressed intrinsic one (it is not drawn through IntrinsicGlyphScale). The circle this
            // handle rides is a genuinely offset Möbius circle whenever the marker's own Γ is off the
            // matched point (HarmonicaVswrHandle's own header) — there is no shortcut from "distance
            // from the marker" to VSWR in general, so the drag point is inverted through the real
            // geometry via bisection rather than approximated.
            var rawGamma = HarmonicaPanelRenderer.CanvasToGamma(local, size);
            double vswr = HarmonicaVswrHandle.VswrThrough(Grab.Marker!.Gamma, rawGamma,
                                                           _vm.Frame.SmithPower.Z0);
            _vm.SetMarkerVswr(Grab.Marker!, vswr);

            // §1.3 — the readout follows the pointer's CANVAS position (x, y), not the panel-local
            // one: it is drawn unclipped, outside any panel's own clip rect.
            VswrReadoutActive  = true;
            VswrReadoutPointer = (x, y);
            VswrReadoutText    = HarmonicaReadoutFormatting.FormatVswr(Grab.Marker!.VswrValue);
            return;
        }

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
            // R-h9r2-9 — Snap to Grid lands the marker on the nearest already-solved Γ sample instead
            // of the raw cursor position. Applied on EVERY move, not only at release, so the marker
            // never shows an unsnapped position that the eventual commit would then jump away from —
            // R-h6-3's "the marker IS the live preview" rule extended to a snapped preview.
            if (Grab.Marker!.SnapToGridEnabled)
                gamma = SnapToNearestGridPoint(gamma, _vm.Frame.SmithPower.GridPoints);

            // R-h6-3 — the marker IS the live preview. Nothing else in the model is touched, and the
            // TerminationSet is not cloned per move: RequestFrame already snapshots what it needs.
            _vm.SetMarkerGamma(Grab.Marker!, gamma);

            // R-h9r2-2/3 — dragging always skips the grid; on release, additionally skip it when the
            // dragged band is the plane/band currently swept (Finding B).
            _vm.RequestFrameOnMarkerRelease(Grab.Marker!.Side, Grab.Marker.Band, dragging);
        }
        else
        {
            // M2 — the target is the INTRINSIC Γ under the pointer; the extrinsic terminations that
            // put it there are what the solve finds. A failure surfaces in the view model's status
            // message, not here, because it arrives with the frame rather than with the gesture.
            _vm.DragIntrinsicGlyph(Grab.Marker!, gamma, dragging);
        }
    }

    /// <summary>R-h9r2-9 — the nearest Γ sample in the currently-solved grid, or <paramref name="gamma"/>
    /// unchanged when the grid is empty (no grid solved yet, or a <c>SkipContours</c> frame carrying
    /// none forward). A no-op rather than an error: the toggle stays available and simply does nothing
    /// until there is something to snap to.</summary>
    private static Complex SnapToNearestGridPoint(Complex gamma, IReadOnlyList<HarmonicaGridPoint> gridPoints)
    {
        if (gridPoints.Count == 0) return gamma;

        Complex best = gamma;
        double bestD2 = double.MaxValue;
        foreach (var gp in gridPoints)
        {
            var diff = gp.Gamma - gamma;
            double d2 = diff.Real * diff.Real + diff.Imaginary * diff.Imaginary;
            if (d2 < bestD2) { bestD2 = d2; best = gp.Gamma; }
        }
        return best;
    }
}
