// ================================================================
//  HarmonicaVswrHandleTests.cs  —  brief-harmonicarf-r6b §1's grab-anywhere VSWR circle
//
//  §1.1: there is no single handle point any more — the whole circumference is grabbable, hit-tested
//  by point-to-SEGMENT distance against LoadpullSurface.VswrLocus's own default-resolution polyline
//  (HarmonicaHitTest.Resolve's Pass 2.5), through the SAME raw-Gamma transform the locus is drawn with
//  (GammaToCanvas, never MarkerToCanvas). §1.2: the drag is UNCLAMPED beyond the geometric VSWR ≥ 1
//  floor — HarmonicaVswrHandle.VswrThrough inverts the REAL Möbius-circle geometry via bisection, not
//  an approximation, and nothing downstream re-clamps the result. §1.3: the live "VSWR: <val>" readout
//  is tracked on the gesture itself and shares its formatter with §2.1's menu header.
//
//  MARKER INDEX NOTE: HarmonicaViewModel's constructor inserts markers in RANK order (source bands
//  ascending, then load bands ascending), not insertion order — vm.Markers is [S1, S2, L1, L2, L3].
//  S2/L2/L3 default to TerminationSet.UnmarkedBandOhms (a near-short, Γ magnitude ≈ 1 — right at the
//  rim), which is a degenerate fixture for VSWR-circle geometry (the circle has nowhere to expand into
//  before hitting the unit circle). Tests here use S1 (vm.Markers[0], Z=25 Ω) or L1 (vm.Markers[2],
//  Z=80+j10 Ω) — both comfortably inside the disk — and never vm.Markers[1]/[3]/[4] (the unmarked
//  S2/L2/L3) unless the test overrides Gamma itself.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

public sealed class HarmonicaVswrHandleTests(ITestOutputHelper output)
{
    /// <summary>Canvas coordinates of a Γ on the POWER Smith panel, at this canvas size — mirrors
    /// HarmonicaDragTests' own OnPowerPanel, but through the RAW transform (GammaToCanvas), since the
    /// VSWR locus is never drawn on the compressed intrinsic scale.</summary>
    private static (double X, double Y) RawOnPowerPanel(HarmonicaViewModel vm, Complex gamma,
                                                         double w, double h)
    {
        var p = vm.Layout.PlacementOf(HarmonicaPanelId.SmithPower);
        var local = HarmonicaPanelRenderer.GammaToCanvas(gamma, (p.W * w, p.H * h));
        return (p.X * w + local.X, p.Y * h + local.Y);
    }

    /// <summary>A point ON a marker's own VSWR locus, at angle θ (radians) — the general "any θ, not
    /// just 0" grab point §1.4 asks tests to use.</summary>
    private static (double X, double Y) OnLocus(HarmonicaViewModel vm, HarmonicaMarker marker,
                                                 double z0, double thetaRadians, double w, double h)
    {
        var (ctr, rad) = CircleParamsFor(marker.Gamma, marker.VswrValue, z0);
        var gamma = ctr + rad * new Complex(Math.Cos(thetaRadians), Math.Sin(thetaRadians));
        return RawOnPowerPanel(vm, gamma, w, h);
    }

    /// <summary>The circle's own centre/radius, recovered the same way <c>HarmonicaVswrHandle</c>'s
    /// own (private) <c>CircleParams</c> does — from <c>VswrLocus</c>'s θ = 0 / θ = π samples, which
    /// are diametrically opposite by construction.</summary>
    private static (Complex Ctr, double Rad) CircleParamsFor(Complex center, double vswr, double z0)
    {
        var pts = LoadpullSurface.VswrLocus(center, vswr, SurfacePlane.Gamma, new Complex(z0, 0.0), nPoints: 2);
        return ((pts[0] + pts[1]) / 2.0, (pts[0] - pts[1]).Magnitude / 2.0);
    }

    private static double Z0Of(HarmonicaViewModel vm) => vm.Frame.SmithPower.Z0;

    // ══ the math ══════════════════════════════════════════════════════════

    [Fact]
    public void VswrThrough_InvertsAnArbitraryLocusPoint_ForAnOffCenterMarker()
    {
        // Dragging exactly onto a point on the true locus (θ = 0.7 rad, not just 0) must recover the
        // VSWR that placed it there — the round trip the bisection search exists to guarantee.
        var center = new Complex(0.3, -0.2);
        const double vswr = 7.0;
        var (ctr, rad) = CircleParamsFor(center, vswr, 50.0);
        var onLocus = ctr + rad * new Complex(Math.Cos(0.7), Math.Sin(0.7));

        double recovered = HarmonicaVswrHandle.VswrThrough(center, onLocus, 50.0);
        Assert.Equal(vswr, recovered, precision: 3);
    }

    [Fact]
    public void VswrThrough_HasNoUpperClamp_TheResultingLocusPassesThroughTheDragPoint()
    {
        // §1.2 — the direct test: a drag near the rim produces the VSWR whose locus ACTUALLY PASSES
        // THROUGH the drag point, not a magic saturated number — well past the OLD ceiling (MaxVswr
        // was 199 before this brief). Assert the invariant (closest approach on a fine resample of the
        // resulting locus is tiny), not a specific value.
        //
        // MEASURED, NOT ASSUMED (found while writing this test): for a PASSIVE marker (|Γ| < 1, the
        // ordinary case), the entire VSWR family stays strictly INSIDE |Γ| = 1 for every finite VSWR —
        // it approaches the rim as VSWR → ∞ but never reaches or crosses it (the underlying Möbius map
        // is an automorphism of the passive half-plane, so a passive Zc's power-wave disk can never
        // image outside the passive Γ disk). So "drag past the rim" for a passive marker has no finite
        // answer arbitrarily far out — it has one arbitrarily CLOSE to the rim, which is what this test
        // actually exercises. (The reverse holds for an ACTIVE marker, |Γ| > 1 — R-h6-10's flag — whose
        // ENTIRE family then stays outside |Γ| = 1 instead; not exercised here.)
        var center = new Complex(0.3, -0.2); // this file's own header example
        var dragGamma = 0.9995 * Complex.FromPolarCoordinates(1.0, center.Phase); // close to the rim

        double vswr = HarmonicaVswrHandle.VswrThrough(center, dragGamma, 50.0);
        output.WriteLine($"near-rim drag recovered VSWR = {vswr:F3}");
        Assert.True(vswr > 199.0, $"expected a VSWR well past the old 199 ceiling, got {vswr:F3}");

        var fine = LoadpullSurface.VswrLocus(center, vswr, SurfacePlane.Gamma,
                                             new Complex(50.0, 0.0), nPoints: 720);
        double minDist = fine.Min(p => (p - dragGamma).Magnitude);
        output.WriteLine($"closest approach of the resulting locus to the drag point = {minDist:E3}");
        Assert.True(minDist < 1e-2, $"expected the locus to pass near the drag point, closest was {minDist:E3}");
    }

    [Fact]
    public void VswrThrough_NeverGoesBelowMinVswr()
    {
        // The drag point lands exactly on the marker itself (zero distance) — the tightest possible
        // circle, which is the ONE remaining floor (§1.2's own ruling).
        var center = new Complex(0.2, 0.1);
        double vswr = HarmonicaVswrHandle.VswrThrough(center, center, 50.0);
        Assert.Equal(HarmonicaVswrHandle.MinVswr, vswr, precision: 3);
    }

    // ══ the hit test — grab anywhere on the circumference (§1.1) ═══════════

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.7)]
    [InlineData(Math.PI)]
    [InlineData(4.2)]
    public void APointOnTheLocus_AtAnyAngle_IsGrabbableWhenVswrIsEnabled(double theta)
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1, Z=80+j10Ω — comfortably off the rim
        double z0 = Z0Of(vm);
        marker.VswrEnabled = true;

        var (hx, hy) = OnLocus(vm, marker, z0, theta, W, H);
        var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, hx, hy, W, H,
            topmost: vm.TopmostMarker, z0: z0);
        Assert.Equal(HarmonicaGrabKind.VswrHandle, grab.Kind);
        Assert.Same(marker, grab.Marker);
    }

    [Fact]
    public void APointOnTheLocus_IsNotGrabbable_WhenVswrIsDisabled()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2];
        double z0 = Z0Of(vm);
        marker.VswrEnabled = false;

        var (hx, hy) = OnLocus(vm, marker, z0, 0.0, W, H);
        var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, hx, hy, W, H,
            topmost: vm.TopmostMarker, z0: z0);
        Assert.NotEqual(HarmonicaGrabKind.VswrHandle, grab.Kind);
    }

    [Fact]
    public void APointWellOffTheLocus_DoesNotGrab()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2];
        double z0 = Z0Of(vm);
        marker.VswrEnabled = true;

        // Nowhere near the locus at all — deep inside the disk, away from the circle and the marker.
        var (fx, fy) = RawOnPowerPanel(vm, Complex.Zero, W, H);
        var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, fx, fy, W, H,
            topmost: vm.TopmostMarker, z0: z0);
        Assert.NotEqual(HarmonicaGrabKind.VswrHandle, grab.Kind);
    }

    [Fact]
    public void TheMarkersOwnRoundGlyph_StillWinsOverItsOwnCircle_WhenTheyOverlap()
    {
        // R-h9r2-5's z-order: the round marker is drawn ON TOP of its own VSWR circle (DrawMarkers
        // draws the locus first, then the dot), so a low-VSWR circle sitting close enough to coincide
        // with the marker's own grab radius must still resolve to the marker, not the circle.
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        marker.VswrValue = 1.001; // the locus sits essentially on the marker itself

        var (mx, my) = RawOnPowerPanel(vm, marker.Gamma, W, H);
        var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, mx, my, W, H,
            topmost: vm.TopmostMarker, z0: Z0Of(vm));
        Assert.Equal(HarmonicaGrabKind.ExtrinsicMarker, grab.Kind);
    }

    // ══ the gesture ═══════════════════════════════════════════════════════

    [Fact]
    public void DraggingTheCircle_AtTheMatchedMarker_SetsVswrFromDistanceAlone_AngleIsIrrelevant()
    {
        // Every default marker (S1/S2/L1/L2/L3) is seeded away from Γ = 0 (SetMarkerImpedance runs on
        // all of them in the constructor), so the matched-point case has to be set up explicitly rather
        // than found in a fixture. At Γ = 0 the locus genuinely IS a plain circle centred on the marker
        // (HarmonicaVswrHandle's own header names this the one degenerate case), so angle really is
        // irrelevant here — this test pins that special case, not the general rule.
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.Gamma = Complex.Zero;
        marker.VswrEnabled = true;
        marker.VswrValue = 2.0;
        double z0 = Z0Of(vm);

        var (sx, sy) = OnLocus(vm, marker, z0, 0.0, W, H);

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(sx, sy, W, H));
        Assert.Equal(HarmonicaGrabKind.VswrHandle, g.Grab.Kind);
        Assert.Same(marker, g.Grab.Marker);

        // Drag to a point 0.15 away from the marker's own Γ, due EAST (θ = 0).
        const double rho = 0.15;
        var east = marker.Gamma + new Complex(rho, 0.0);
        var (ex, ey) = RawOnPowerPanel(vm, east, W, H);
        g.PointerMoved(ex, ey, W, H);
        double vswrEast = marker.VswrValue;

        // Drag to the SAME distance, due NORTH instead — at Γ = 0 the circle is symmetric, so this
        // must land on the identical VSWR.
        var north = marker.Gamma + new Complex(0.0, rho);
        var (nx, ny) = RawOnPowerPanel(vm, north, W, H);
        g.PointerMoved(nx, ny, W, H);
        double vswrNorth = marker.VswrValue;

        double expected = (1.0 + rho) / (1.0 - rho); // plain ρ↔VSWR relation, at the matched point
        output.WriteLine($"east: VSWR = {vswrEast:F4}, north: VSWR = {vswrNorth:F4}, expected {expected:F4}");
        Assert.Equal(expected, vswrEast,  precision: 3);
        Assert.Equal(expected, vswrNorth, precision: 3);

        g.PointerUp(nx, ny, W, H);
        Assert.False(g.IsDragging);
    }

    [Fact]
    public void DraggingTheCircle_AtAnOffCenterMarker_AngleGenerallyMatters()
    {
        // The general case (Γ ≠ 0): the true locus is an offset Möbius circle, so equal-distance drags
        // at different angles are NOT expected to land on the same VSWR. This is the direct regression
        // guard for the bug this file's own header describes — a naive "distance from the marker"
        // formula would (wrongly) make this test's two readings agree.
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[0]; // S1 — given an explicit, clearly off-matched-point value
        marker.Gamma = new Complex(0.3, -0.2);
        marker.VswrEnabled = true;
        marker.VswrValue = 2.0;
        double z0 = Z0Of(vm);

        var (sx, sy) = OnLocus(vm, marker, z0, 0.0, W, H);

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(sx, sy, W, H));
        Assert.Equal(HarmonicaGrabKind.VswrHandle, g.Grab.Kind);

        const double rho = 0.15;
        var east = marker.Gamma + new Complex(rho, 0.0);
        var (ex, ey) = RawOnPowerPanel(vm, east, W, H);
        g.PointerMoved(ex, ey, W, H);
        double vswrEast = marker.VswrValue;

        var north = marker.Gamma + new Complex(0.0, rho);
        var (nx, ny) = RawOnPowerPanel(vm, north, W, H);
        g.PointerMoved(nx, ny, W, H);
        double vswrNorth = marker.VswrValue;

        output.WriteLine($"off-center east: VSWR = {vswrEast:F4}, north: VSWR = {vswrNorth:F4}");
        Assert.True(Math.Abs(vswrEast - vswrNorth) > 0.01,
            $"east ({vswrEast:F4}) and north ({vswrNorth:F4}) should generally differ off-center");

        g.PointerUp(nx, ny, W, H);
    }

    [Fact]
    public void GrabbingTheCircle_PromotesTheMarker_LikeGrabbingTheMarkerItself()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[0]; // S1 — not the default top of the z-order
        marker.VswrEnabled = true;
        double z0 = Z0Of(vm);

        var (sx, sy) = OnLocus(vm, marker, z0, 0.0, W, H);

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(sx, sy, W, H));
        Assert.Same(marker, vm.TopmostMarker);
    }

    [Fact]
    public void DraggingTheCircle_RequestsNoFrame_ItIsADisplayAnnotationOnly()
    {
        // brief-harmonicarf-r6b's own scope: the overlay neither reads nor writes anything the circuit
        // depends on. Every OTHER marker drag drives the solve pool (R-h6-3/R-h6-4); this one must not.
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        double z0 = Z0Of(vm);

        var (sx, sy) = OnLocus(vm, marker, z0, 0.0, W, H);

        int startedBefore = vm.Pool.StartedCount;
        var g = new HarmonicaGesture(vm);
        g.PointerDown(sx, sy, W, H);
        for (int i = 1; i <= 5; i++)
        {
            var (ctr, rad) = CircleParamsFor(marker.Gamma, 2.0 + i * 0.3, z0);
            var (mx, my) = RawOnPowerPanel(vm, ctr + rad, W, H);
            g.PointerMoved(mx, my, W, H);
        }
        g.PointerUp(sx, sy, W, H);

        Assert.Equal(startedBefore, vm.Pool.StartedCount);
    }

    // ══ the live readout (§1.3) ══════════════════════════════════════════

    [Fact]
    public void PressingTheCircle_ShowsTheReadout_WithTheCurrentValue_BeforeAnyMove()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        marker.VswrValue = 3.25;
        double z0 = Z0Of(vm);

        var (sx, sy) = OnLocus(vm, marker, z0, 0.0, W, H);
        var g = new HarmonicaGesture(vm);
        g.PointerDown(sx, sy, W, H);

        Assert.True(g.VswrReadoutActive);
        Assert.Equal(HarmonicaReadoutFormatting.FormatVswr(marker.VswrValue), g.VswrReadoutText);
    }

    [Fact]
    public void DraggingTheCircle_KeepsTheReadoutInStepWithTheLiveValue_AndClearsOnRelease()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        double z0 = Z0Of(vm);

        var (sx, sy) = OnLocus(vm, marker, z0, 0.0, W, H);
        var g = new HarmonicaGesture(vm);
        g.PointerDown(sx, sy, W, H);

        var (ctr, rad) = CircleParamsFor(marker.Gamma, 4.0, z0);
        var (mx, my) = RawOnPowerPanel(vm, ctr + rad, W, H);
        g.PointerMoved(mx, my, W, H);

        Assert.True(g.VswrReadoutActive);
        // §1.3 — the SAME formatter §2.1's menu header uses, so the number a drag lands on is the
        // number the menu then shows.
        Assert.Equal(HarmonicaReadoutFormatting.FormatVswr(marker.VswrValue), g.VswrReadoutText);

        g.PointerUp(mx, my, W, H);
        Assert.False(g.VswrReadoutActive);
    }

    [Fact]
    public void CancellingTheDrag_ClearsTheReadout()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2];
        marker.VswrEnabled = true;
        double z0 = Z0Of(vm);

        var (sx, sy) = OnLocus(vm, marker, z0, 0.0, W, H);
        var g = new HarmonicaGesture(vm);
        g.PointerDown(sx, sy, W, H);
        Assert.True(g.VswrReadoutActive);

        g.Cancel();
        Assert.False(g.VswrReadoutActive);
    }
}
