using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// <see cref="PlanRoute"/>: a run θ long at F, laid straight, round one corner or as a meander, holds θ through its
/// corners in the plan model, its pieces meet, and a straight run is one MLIN.
/// </summary>
public class PlanRouteTests
{
    /// <summary>20 mil RO4350B, 1 oz copper — the example workspace's board.</summary>
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    /// <summary>A 100 µm GaAs die with 3 µm gold, for the same rule on an MMIC stack.</summary>
    private static readonly MicrostripSubstrate GaAs = new(100e-6, 3e-6, 12.9, 4.1e7, 0.0006);

    private static MicrostripLineParameters Line(double w, double f, MicrostripSubstrate s)
        => new MicrostripLineModel(w, 1.0, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD, "test", s.RoughnessMeters)
            .LineParameters(f);

    [Fact]
    public void AStraightRun_IsOnePlanLine_BitForBit()
    {
        const double w = 1.1e-3, f = 2.4e9;
        var r = PlanRoute.Build(1e-3, 2e-3, 90.0, w, Math.PI / 2.0, f, Ro4350b);

        var line = Assert.IsType<PlanLine>(Assert.Single(r.Sections));
        double l = PassivePlan.OnGrid(PlanarLineAngle.LengthMeters(Line(w, f, Ro4350b).Eeff, Math.PI / 2.0, f));
        Assert.Equal(new PlanLine(line.Nodes, 1e-3, 2e-3, 90.0, w, l), line);
        Assert.Equal([1, 2], line.Nodes);
        Assert.Equal(3, r.NextNode);
        Assert.Equal((1e-3, 2e-3 + l, 90.0), (r.EndX, r.EndY, r.EndRotationDeg));
        Assert.Empty(r.Warnings);
    }

    /// <summary>θ = 90° at F, round corners and through meanders of every corner kind: S21 between lines of the run's own
    /// impedance is −90° at F to half a degree, in the plan model, on a board and on a die.</summary>
    [Theory]
    [InlineData(PlanRouteStyle.Meander, PlanCorner.Curve, 2, false)]
    [InlineData(PlanRouteStyle.Meander, PlanCorner.Miter, 3, false)]
    [InlineData(PlanRouteStyle.Meander, PlanCorner.Square, 1, false)]
    [InlineData(PlanRouteStyle.Corner, PlanCorner.Curve, 0, false)]
    [InlineData(PlanRouteStyle.Corner, PlanCorner.Miter, 0, false)]
    [InlineData(PlanRouteStyle.Meander, PlanCorner.Miter, 2, true)]
    public void AQuarterWaveRun_HoldsItsPhaseThroughItsCorners(PlanRouteStyle style, PlanCorner corner, int turns, bool die)
    {
        var (s, w, f) = die ? (GaAs, 70e-6, 10e9) : (Ro4350b, 1.1e-3, 2.4e9);
        var r = PlanRoute.Build(0.0, 0.0, 0.0, w, Math.PI / 2.0, f, s, style, corner, turns, pitch: 4.0 * w);
        var plan = new PassivePlan(["1", "2"], r.Sections, r.NextNode - 1);

        HybridPlanTests.AssertJoined(plan);
        double phase = plan.S21(s, f, Line(w, f, s).Z0).Phase * 180.0 / Math.PI;
        Assert.True(Math.Abs(phase + 90.0) < 0.5, $"S21 phase {phase}° at F");
        Assert.Empty(r.Warnings);
        if (style == PlanRouteStyle.Meander)
            Assert.Equal(turns % 2 == 0 ? 0.0 : 180.0, r.EndRotationDeg);
    }

    /// <summary>Legs closer than 3W edge to edge couple, which the model leaves out: the route says so.</summary>
    [Fact]
    public void LegsUnder3WApart_AreAWarning()
    {
        var r = PlanRoute.Build(0.0, 0.0, 0.0, 1.1e-3, Math.PI / 2.0, 2.4e9, Ro4350b, PlanRouteStyle.Meander,
            PlanCorner.Miter, turns: 2, pitch: 2.0 * 1.1e-3);
        Assert.Equal("device.route.runs-coupled", Assert.Single(r.Warnings).Id);
    }

    /// <summary>Built coupled, a meander's legs are one N-line section over the same copper: every leg a strip, the turns
    /// unchanged, the pieces joined, the run ending and spanning where the uncoupled one does, and no warning, since the
    /// model now has the coupling.</summary>
    [Theory]
    [InlineData(PlanCorner.Curve, 3, 1, 0.0)]
    [InlineData(PlanCorner.Miter, 2, -1, 90.0)]
    public void ACoupledMeander_IsItsLegsAsOneNLineSection(PlanCorner corner, int turns, int side, double heading)
    {
        const double w = 1.1e-3, f = 2.4e9;
        PlanRouteResult Route(bool coupled) => PlanRoute.Build(1e-3, -2e-3, heading, w, 2.0 * Math.PI, f, Ro4350b,
            PlanRouteStyle.Meander, corner, turns, pitch: 2.0 * w, side: side, coupled: coupled);
        var (apart, coupled) = (Route(false), Route(true));

        var strips = Assert.Single(coupled.Sections.OfType<PlanCoupledStrips>());
        var legs = apart.Sections.OfType<PlanLine>().Where(l => l.L == strips.L).ToList();
        Assert.Equal(turns + 1, legs.Count);
        Assert.Equal(turns + 1, strips.W.Count);
        Assert.All(strips.S, s => Assert.Equal(w, s, 15));
        Assert.Equal(apart.Sections.Count - legs.Count + 1, coupled.Sections.Count);
        Assert.Equal(legs.SelectMany(l => l.Nodes).Order(), strips.Nodes.Order());
        Assert.Equal((apart.EndX, apart.EndY, apart.EndRotationDeg, apart.NextNode), (coupled.EndX, coupled.EndY,
            coupled.EndRotationDeg, coupled.NextNode));
        Assert.Equal((apart.MinX, apart.MinY, apart.MaxX, apart.MaxY), (coupled.MinX, coupled.MinY, coupled.MaxX, coupled.MaxY));
        HybridPlanTests.AssertJoined(new PassivePlan(["1", "2"], coupled.Sections, coupled.NextNode - 1));
        Assert.Single(apart.Warnings);
        Assert.Empty(coupled.Warnings);
    }

    [Fact]
    public void TooManyTurnsForTheLength_IsACodedRefusal()
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => PlanRoute.Build(0.0, 0.0, 0.0, 1.1e-3, Math.PI / 2.0, 2.4e9,
            Ro4350b, PlanRouteStyle.Meander, PlanCorner.Curve, turns: 6, pitch: 4.4e-3));
        Assert.Equal("device.route.too-short", ex.Diagnostic?.Id);
    }
}
