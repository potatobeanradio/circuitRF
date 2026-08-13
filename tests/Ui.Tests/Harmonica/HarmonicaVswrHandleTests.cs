// ================================================================
//  HarmonicaVswrHandleTests.cs  —  R-h9r2-8's drag handle
//
//  A marker's VSWR circle can be grabbed and dragged: the handle sits at the locus's own θ = 0 sample
//  (HarmonicaVswrHandle), is hit-tested through the SAME raw-Gamma transform the locus is drawn with
//  (GammaToCanvas, never MarkerToCanvas), and a drag sets VswrValue by inverting the REAL Möbius-circle
//  geometry — not an approximation — via HarmonicaVswrHandle.VswrThrough. No solve, no frame request:
//  the overlay is a display annotation over an already-solved termination.
//
//  MARKER INDEX NOTE: HarmonicaViewModel's constructor inserts markers in RANK order (source bands
//  ascending, then load bands ascending), not insertion order — vm.Markers is [S1, S2, L1, L2, L3].
//  S2/L2/L3 default to TerminationSet.UnmarkedBandOhms (a near-short, Γ magnitude ≈ 1 — right at the
//  rim), which is a degenerate fixture for VSWR-handle geometry (the circle has nowhere to expand into
//  before hitting the unit circle, so the handle can land almost on top of the marker). Tests here use
//  S1 (vm.Markers[0], Z=25 Ω) or L1 (vm.Markers[2], Z=80+j10 Ω) — both comfortably inside the disk —
//  and never vm.Markers[1]/[3]/[4] (the unmarked S2/L2/L3) unless the test overrides Gamma itself.
// ================================================================

using System;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using CircuitRF.Ui.Harmonica.Renderers;
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

    private static double Z0Of(HarmonicaViewModel vm) => vm.Frame.SmithPower.Z0;

    // ══ the math ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    [InlineData(10.0)]
    [InlineData(50.0)]
    public void RhoAndVswr_RoundTrip(double vswr)
    {
        double rho = HarmonicaVswrHandle.RhoOf(vswr);
        double back = HarmonicaVswrHandle.VswrOf(rho);
        Assert.Equal(vswr, back, precision: 6);
    }

    [Fact]
    public void VswrOf_ClampsAtTheDegenerateEnds()
    {
        // rho = 0 (the drag lands exactly on the marker) → the lowest expressible VSWR, never 1.0
        // exactly (a zero-radius circle is nothing to grab).
        Assert.Equal(HarmonicaVswrHandle.MinVswr, HarmonicaVswrHandle.VswrOf(0.0), precision: 6);

        // rho → 1 (the drag lands on or beyond the unit circle) → the capped maximum, never infinity.
        Assert.Equal(HarmonicaVswrHandle.MaxVswr, HarmonicaVswrHandle.VswrOf(0.999), precision: 3);
        Assert.Equal(HarmonicaVswrHandle.MaxVswr, HarmonicaVswrHandle.VswrOf(5.0), precision: 3);
    }

    [Fact]
    public void HandleGamma_IsTheLocussOwnThetaZeroSample()
    {
        // HandleGamma is now IMPLEMENTED by calling VswrLocus at nPoints:1 — this pins that wiring
        // rather than re-deriving the geometry, and is the regression gate for ever "optimizing" it
        // back into a hand-rolled formula (the earlier, disproven "center + rho" shortcut).
        var center = new Complex(0.3, -0.2);
        const double vswr = 3.0;
        var locus = RfCore.Loadpull.LoadpullSurface.VswrLocus(
            center, vswr, RfCore.Loadpull.SurfacePlane.Gamma, new Complex(50.0, 0.0));

        var handle = HarmonicaVswrHandle.HandleGamma(center, vswr, 50.0);
        output.WriteLine($"locus[0] = {locus[0]}, HandleGamma = {handle}");
        Assert.Equal(locus[0].Real,      handle.Real,      precision: 9);
        Assert.Equal(locus[0].Imaginary, handle.Imaginary, precision: 9);
    }

    [Fact]
    public void HandleGamma_OffCenterMarker_DoesNotEqualTheNaiveCenterPlusRhoFormula()
    {
        // The disproven shortcut: center + (rho, 0). Confirms the fix actually changed behavior for an
        // off-matched-point marker, rather than merely reformatting the same wrong answer.
        var center = new Complex(0.3, -0.2);
        const double vswr = 3.0;
        double rho = HarmonicaVswrHandle.RhoOf(vswr);
        var naive = center + new Complex(rho, 0.0);

        var handle = HarmonicaVswrHandle.HandleGamma(center, vswr, 50.0);
        Assert.True((handle - naive).Magnitude > 0.01,
            $"handle {handle} should differ materially from the naive formula {naive}");
    }

    [Fact]
    public void VswrThrough_InvertsHandleGamma_ForAnOffCenterMarker()
    {
        // Dragging exactly onto the handle's own position must recover the VSWR that placed it there —
        // the round trip the bisection search exists to guarantee.
        var center = new Complex(0.3, -0.2);
        const double vswr = 7.0;
        var handle = HarmonicaVswrHandle.HandleGamma(center, vswr, 50.0);

        double recovered = HarmonicaVswrHandle.VswrThrough(center, handle, 50.0);
        Assert.Equal(vswr, recovered, precision: 3);
    }

    // ══ the hit test ══════════════════════════════════════════════════════

    [Fact]
    public void TheHandle_IsGrabbableOnlyWhenVswrIsEnabled()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1, Z=80+j10Ω — comfortably off the rim
        double z0 = Z0Of(vm);

        marker.VswrEnabled = false;
        var (hx0, hy0) = RawOnPowerPanel(
            vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, marker.VswrValue, z0), W, H);
        var missGrab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, hx0, hy0, W, H,
            topmost: vm.TopmostMarker, z0: z0);
        Assert.NotEqual(HarmonicaGrabKind.VswrHandle, missGrab.Kind);

        marker.VswrEnabled = true;
        var (hx, hy) = RawOnPowerPanel(
            vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, marker.VswrValue, z0), W, H);
        var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, hx, hy, W, H,
            topmost: vm.TopmostMarker, z0: z0);
        Assert.Equal(HarmonicaGrabKind.VswrHandle, grab.Kind);
        Assert.Same(marker, grab.Marker);
    }

    [Fact]
    public void TheMarkersOwnRoundGlyph_StillWinsOverItsOwnHandle_WhenTheyOverlap()
    {
        // R-h9r2-5's z-order: the round marker is drawn ON TOP of its own VSWR circle (DrawMarkers
        // draws the locus first, then the dot), so a low-VSWR handle sitting close enough to coincide
        // with the marker's own grab radius must still resolve to the marker, not the handle.
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        marker.VswrValue = 1.001; // rho ~ 0.0005 — the handle sits essentially on the marker itself

        var (mx, my) = RawOnPowerPanel(vm, marker.Gamma, W, H);
        var grab = HarmonicaHitTest.Resolve(vm.Layout, vm.Markers, mx, my, W, H,
            topmost: vm.TopmostMarker, z0: Z0Of(vm));
        Assert.Equal(HarmonicaGrabKind.ExtrinsicMarker, grab.Kind);
    }

    // ══ the gesture ═══════════════════════════════════════════════════════

    [Fact]
    public void DraggingTheHandle_AtTheMatchedMarker_SetsVswrFromDistanceAlone_AngleIsIrrelevant()
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

        var (sx, sy) = RawOnPowerPanel(
            vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, marker.VswrValue, z0), W, H);

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

        output.WriteLine($"east: VSWR = {vswrEast:F4}, north: VSWR = {vswrNorth:F4}, " +
                         $"expected {HarmonicaVswrHandle.VswrOf(rho):F4}");
        Assert.Equal(HarmonicaVswrHandle.VswrOf(rho), vswrEast,  precision: 3);
        Assert.Equal(HarmonicaVswrHandle.VswrOf(rho), vswrNorth, precision: 3);

        g.PointerUp(nx, ny, W, H);
        Assert.False(g.IsDragging);
    }

    [Fact]
    public void DraggingTheHandle_AtAnOffCenterMarker_AngleGenerallyMatters()
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

        var (sx, sy) = RawOnPowerPanel(
            vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, marker.VswrValue, z0), W, H);

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
    public void GrabbingTheHandle_PromotesTheMarker_LikeGrabbingTheMarkerItself()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[0]; // S1 — not the default top of the z-order
        marker.VswrEnabled = true;
        double z0 = Z0Of(vm);

        var (sx, sy) = RawOnPowerPanel(
            vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, marker.VswrValue, z0), W, H);

        var g = new HarmonicaGesture(vm);
        Assert.True(g.PointerDown(sx, sy, W, H));
        Assert.Same(marker, vm.TopmostMarker);
    }

    [Fact]
    public void DraggingTheHandle_RequestsNoFrame_ItIsADisplayAnnotationOnly()
    {
        // R-h9r2-8's own scope: the overlay neither reads nor writes anything the circuit depends on.
        // Every OTHER marker drag drives the solve pool (R-h6-3/R-h6-4); this one must not.
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        double z0 = Z0Of(vm);

        var (sx, sy) = RawOnPowerPanel(
            vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, marker.VswrValue, z0), W, H);

        int startedBefore = vm.Pool.StartedCount;
        var g = new HarmonicaGesture(vm);
        g.PointerDown(sx, sy, W, H);
        for (int i = 1; i <= 5; i++)
        {
            var (mx, my) = RawOnPowerPanel(
                vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, 2.0 + i * 0.3, z0), W, H);
            g.PointerMoved(mx, my, W, H);
        }
        g.PointerUp(sx, sy, W, H);

        Assert.Equal(startedBefore, vm.Pool.StartedCount);
    }

    [Fact]
    public void DraggingBeyondTheUnitCircle_ClampsAtMaxVswr_NeverThrows()
    {
        var vm = new HarmonicaViewModel();
        const double W = 1200, H = 800;
        var marker = vm.Markers[2]; // L1
        marker.VswrEnabled = true;
        double z0 = Z0Of(vm);

        var (sx, sy) = RawOnPowerPanel(
            vm, HarmonicaVswrHandle.HandleGamma(marker.Gamma, marker.VswrValue, z0), W, H);
        var g = new HarmonicaGesture(vm);
        g.PointerDown(sx, sy, W, H);

        var farOutside = marker.Gamma + new Complex(50.0, 0.0);
        var (fx, fy) = RawOnPowerPanel(vm, farOutside, W, H);
        g.PointerMoved(fx, fy, W, H);

        Assert.Equal(HarmonicaVswrHandle.MaxVswr, marker.VswrValue, precision: 3);
    }
}
