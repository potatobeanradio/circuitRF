using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>Gates for brief-mtaper-mklopf.md §3.2/§3.2a — the Offset quintic centerline's own
/// geometry (a novel, unpublished design per R-klp-11; these formulas ARE the specification, see
/// MicrostripOffsetCenterline's own doc comment).</summary>
public class MicrostripOffsetCenterlineTests
{
    // ── R-klp-7: G2 continuity — zero slope AND zero curvature at both endpoints ───────────────

    [Fact]
    public void Slope_IsZero_AtBothEndpoints()
    {
        double l = 10e-3, offset = 2e-3;
        Assert.Equal(0.0, MicrostripOffsetCenterline.DyDx(0.0, l, offset), 9);
        Assert.Equal(0.0, MicrostripOffsetCenterline.DyDx(l, l, offset), 9);
    }

    [Fact]
    public void Curvature_IsZero_AtBothEndpoints()
    {
        double l = 10e-3, offset = 2e-3;
        Assert.Equal(0.0, MicrostripOffsetCenterline.Curvature(0.0, l, offset), 6);
        Assert.Equal(0.0, MicrostripOffsetCenterline.Curvature(l, l, offset), 6);
    }

    [Fact]
    public void Y_HitsZeroAndOffset_AtTheEndpoints()
    {
        double l = 10e-3, offset = 2e-3;
        Assert.Equal(0.0, MicrostripOffsetCenterline.Y(0.0, l, offset), 12);
        Assert.Equal(offset, MicrostripOffsetCenterline.Y(l, l, offset), 12);
    }

    [Fact]
    public void RaisedCosine_WouldHaveHadMaximumCurvatureAtTheEndpoints_UnlikeTheQuintic()
    {
        // The brief's own rejected alternative, y=(Offset/2)(1-cos(pi*x/L)): y''(0) is its PEAK
        // curvature, not zero. Included as a negative-control contrast to the quintic's own
        // zero-curvature-at-endpoints property just proven above.
        double l = 10e-3, offset = 2e-3;
        double RaisedCosine(double x) => offset / 2.0 * (1.0 - Math.Cos(Math.PI * x / l));
        double h = 1e-7;
        double d2 = (RaisedCosine(h) - 2 * RaisedCosine(0) + RaisedCosine(-h)) / (h * h);
        Assert.True(Math.Abs(d2) > 1.0); // materially nonzero curvature at x=0
    }

    // ── R-klp-6: arc length exceeds axial length once Offset != 0 ───────────────────────────────

    [Fact]
    public void TotalArcLength_OffsetZero_EqualsAxialLength()
    {
        Assert.Equal(10e-3, MicrostripOffsetCenterline.TotalArcLength(10e-3, 0.0), 12);
    }

    [Fact]
    public void TotalArcLength_WorkedExample_ExceedsAxialByRoughlySevenPercent()
    {
        // Brief §3.2a: L=3in=76.2mm, Offset=1in=25.4mm -> arc length ~81.9mm (~7.4% longer).
        double l = 76.2e-3, offset = 25.4e-3;
        double arc = MicrostripOffsetCenterline.TotalArcLength(l, offset);
        double excessFraction = (arc - l) / l;
        Assert.InRange(excessFraction, 0.06, 0.09); // "roughly 7.4%"
    }

    [Fact]
    public void AxialPositionAtArcFraction_RoundTrips_ThroughArcLength()
    {
        double l = 10e-3, offset = 3e-3;
        double total = MicrostripOffsetCenterline.TotalArcLength(l, offset);
        double x = MicrostripOffsetCenterline.AxialPositionAtArcFraction(0.3, l, offset, total);
        double arcAtX = MicrostripOffsetCenterline.ArcLength(0.0, x, l, offset);
        Assert.Equal(0.3 * total, arcAtX, 4);
    }

    [Fact]
    public void AxialPositionAtArcFraction_EndpointsMapToEndpoints()
    {
        double l = 10e-3, offset = 3e-3;
        double total = MicrostripOffsetCenterline.TotalArcLength(l, offset);
        Assert.Equal(0.0, MicrostripOffsetCenterline.AxialPositionAtArcFraction(0.0, l, offset, total), 6);
        Assert.Equal(l, MicrostripOffsetCenterline.AxialPositionAtArcFraction(1.0, l, offset, total), 3);
    }

    // ── R-klp-10: the curvature check, worked example must NOT warn ────────────────────────────

    [Fact]
    public void MinRadiusOfCurvature_OffsetZero_IsInfinite()
    {
        Assert.Equal(double.PositiveInfinity, MicrostripOffsetCenterline.MinRadiusOfCurvature(10e-3, 0.0));
    }

    [Fact]
    public void MinRadiusOfCurvature_WorkedExample_MatchesBriefsApproximation()
    {
        // Brief §3.2a: L=3in=76.2mm, Offset=1in=25.4mm -> R_min ~ 44mm.
        double l = 76.2e-3, offset = 25.4e-3;
        double rMin = MicrostripOffsetCenterline.MinRadiusOfCurvature(l, offset);
        Assert.InRange(rMin, 40e-3, 48e-3); // "R_min ~ 44mm", a stated approximation
    }

    [Fact]
    public void MinRadiusOfCurvature_WorkedExample_PassesTheThreeWTest_AgainstA50OhmFr4Trace()
    {
        // Brief §3.2a: "against a 50 Ohm trace on 1.6mm FR-4 (W~3mm, so 3W=9mm) that is roughly
        // 5x clear" -- the ordinary-geometry case that must NOT trigger R-klp-10's warning.
        double l = 76.2e-3, offset = 25.4e-3;
        double rMin = MicrostripOffsetCenterline.MinRadiusOfCurvature(l, offset);
        double w = 3e-3;
        Assert.True(rMin > 3.0 * w);
    }

    [Fact]
    public void MinRadiusOfCurvature_ShortSharpOffset_FailsTheThreeWTest()
    {
        // A short taper with a large offset -- the case the check SHOULD fire on.
        double l = 3e-3, offset = 2e-3;
        double rMin = MicrostripOffsetCenterline.MinRadiusOfCurvature(l, offset);
        double w = 1e-3;
        Assert.True(rMin < 3.0 * w);
    }
}
