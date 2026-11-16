using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// The coupled-line options' numbers against their closed forms (docs/design/rf-passives.md §3.5-§3.6): the
/// three-section equal-ripple coupler against the cubic minimax it reduces to, the tandem's coupling, Schiffman's ρ and
/// reference length, the Lange's finger count and pins, the broadside pair's modes; each option off is the part it was,
/// and a stack or a combination the part cannot build is a coded refusal.
/// </summary>
public class CouplerOptionsPlanTests(ITestOutputHelper output)
{
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const double F0 = 2.4e9;

    private static double Cdb(double q) => 10.0 * Math.Log10(1.0 + 1.0 / (q * q));

    /// <summary>
    /// Three sections: Q(s) = a·s + b·s³ (s = sin θ) is the minimax fit to a constant on [s1, 1] with its extremes at s1,
    /// s* and 1. Equal at the ends, a = −b(1 + s1 + s1²); flat at s*, s*² = (1 + s1 + s1²)/3; and Q(1) + Q(s*) = 2 at unit
    /// level, b = −2/(s1 + s1² + 2·s*³). So the plan's chain must give Q(s1) = Q(1), Q(s*)/Q(1) = (2 − Q̂(1))/Q̂(1) with
    /// Q̂(1) = a + b, and a coupling whose mean in dB is the spec.
    /// </summary>
    [Theory]
    [InlineData(15.0, 2.0)]
    [InlineData(10.0, 3.0)]
    [InlineData(20.0, 1.5)]
    public void ThreeSections_AreTheCubicMinimax(double cDb, double band)
    {
        var (z, ripple) = CoupledLineCouplerPlan.EqualRipple(cDb, 3, band);
        Assert.Equal(z[0], z[2]);
        double s1 = Math.Sin(Math.PI / (1.0 + band)), ss = Math.Sqrt((1 + s1 + s1 * s1) / 3);
        double b = -2.0 / (s1 + s1 * s1 + 2 * ss * ss * ss), a = -b * (1 + s1 + s1 * s1), top = a + b;
        double q1 = CoupledLineCouplerPlan.ChainQ(z, 1.0), qs = CoupledLineCouplerPlan.ChainQ(z, ss);
        Assert.Equal(q1, CoupledLineCouplerPlan.ChainQ(z, s1), 9);
        Assert.Equal((2.0 - top) / top, qs / q1, 9);
        Assert.Equal(cDb, 0.5 * (Cdb(q1) + Cdb(qs)), 9);
        Assert.Equal(Math.Abs(Cdb(qs) - Cdb(q1)) / 2.0, ripple, 9);
        output.WriteLine($"{cDb} dB over {band}:1 — z {string.Join(", ", z.Select(x => x.ToString("F4")))}, ripple ±{ripple:F4} dB");
    }

    /// <summary>Five sections: the chain's Q ripples equally over the band, its m + 2 = 4 extremes within the band equal
    /// in turn, the ends included.</summary>
    [Fact]
    public void FiveSections_RippleEqually()
    {
        var (z, ripple) = CoupledLineCouplerPlan.EqualRipple(10.0, 5, 3.0);
        double s1 = Math.Sin(Math.PI / 4.0);
        var c = Enumerable.Range(0, 4001).Select(i => Cdb(CoupledLineCouplerPlan.ChainQ(z, s1 + (1 - s1) * i / 4000.0))).ToArray();
        Assert.Equal(10.0, 0.5 * (c.Max() + c.Min()), 6);
        Assert.Equal(ripple, 0.5 * (c.Max() - c.Min()), 6);
    }

    [Fact]
    public void OneSection_IsThePlainCoupler_ToTheBit()
    {
        var plain = CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b);
        var options = CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b, 1, 3.0, 0, CouplerCompensation.None, 0, null);
        var p = Assert.IsType<PlanCoupledLine>(Assert.Single(plain.Plan.Sections));
        var q = Assert.IsType<PlanCoupledLine>(Assert.Single(options.Plan.Sections));
        Assert.Equal((p.W, p.S, p.L, p.Fingers, p.OddLengthFactor), (q.W, q.S, q.L, q.Fingers, q.OddLengthFactor));
        Assert.Equal([1, 2, 3, 4], q.Nodes);
        var lange = (PlanCoupledLine)Assert.Single(LangePlan.Build(4.0, 50.0, F0, Ro4350b).Plan.Sections);
        Assert.Equal([1, 2, 7, 8, 5, 6, 3, 4], lange.Nodes);
        Assert.Equal(LangePlan.Pins, LangePlan.Build(4.0, 50.0, F0, Ro4350b).Plan.Pins);
    }

    /// <summary>Couplings c1 = sin θ add in tandem to sin 2θ: 3 dB from two of 8.34 dB.</summary>
    [Theory]
    [InlineData(3.0)]
    [InlineData(6.0)]
    public void TheTandem_IsTwoWeakerCouplers(double cDb)
    {
        double c1 = Math.Pow(10, -CoupledLineCouplerPlan.TandemSubCoupling(cDb) / 20);
        Assert.Equal(Math.Pow(10, -cDb / 20), 2 * c1 * Math.Sqrt(1 - c1 * c1), 12);
        if (cDb == 3.0) Assert.Equal(8.34, CoupledLineCouplerPlan.TandemSubCoupling(cDb), 1);
        var d = CoupledLineCouplerPlan.Build(cDb, 50.0, F0, Ro4350b, tandem: 1);
        Assert.Equal(CoupledLineCouplerPlan.TandemPins, d.Plan.Pins);
        Assert.Equal(2, d.Plan.Sections.OfType<PlanCoupledLine>().Count());
    }

    /// <summary>Schiffman's design: the reference is (ΔΦ + π)/(π/2) quarter waves, and over the band the difference
    /// Δφ = kθ − φ(θ) is −ripple at the band's edge and +ripple at its peak inside.</summary>
    [Theory]
    [InlineData(90.0, 1.5)]
    [InlineData(90.0, 2.0)]
    [InlineData(45.0, 1.8)]
    public void Schiffman_RipplesEquallyAboutThePhase(double phaseDeg, double band)
    {
        double dphi = phaseDeg * Math.PI / 180.0;
        var (rho, k, ripple) = SchiffmanPlan.EqualRipple(dphi, band);
        Assert.Equal(2.0 * (dphi + Math.PI) / Math.PI, k, 12);
        double t1 = Math.PI / (1 + band);
        Assert.Equal(-ripple, k * t1 - SchiffmanPlan.CSectionPhase(rho, t1) - dphi, 9);
        Assert.Equal(dphi, k * Math.PI / 2 - SchiffmanPlan.CSectionPhase(rho, Math.PI / 2), 12);
        double peak = Enumerable.Range(0, 2001).Max(i =>
        {
            double t = t1 + (Math.PI / 2 - t1) * i / 2000.0;
            return k * t - SchiffmanPlan.CSectionPhase(rho, t) - dphi;
        });
        Assert.Equal(ripple, peak, 9);
        output.WriteLine($"{phaseDeg} deg over {band}:1 — rho {rho:F4} ({-20 * Math.Log10((rho - 1) / (rho + 1)):F2} dB pair), "
                       + $"ripple ±{ripple * 180 / Math.PI:F3} deg");
    }

    [Fact]
    public void Schiffman_ReferenceIsThreeQuarterWaves_AndThePinsSplitTwoAndTwo()
    {
        var d = SchiffmanPlan.Build(50.0, F0, 90.0, 1.5, Ro4350b);
        var line = new MicrostripLineModel(d.W0Meters, 1.0, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, "t").LineParameters(F0);
        Assert.Equal(3.0 * 2.99792458e8 / (4.0 * F0 * Math.Sqrt(line.Eeff)), d.ReferenceMeters, 12);
        var reference = Assert.Single(d.Plan.Sections.OfType<PlanLine>(), l => l.Nodes.SequenceEqual([3, 4]));
        Assert.Equal(PassivePlan.OnGrid(d.ReferenceMeters), reference.L);
        Assert.Equal([1, 5, 2, 6], Assert.Single(d.Plan.Sections.OfType<PlanCoupledLine>()).Nodes);
        Assert.Equal(2500.0, d.Ze * d.Zo, 9);
        Assert.Equal(d.Rho, d.Ze / d.Zo, 12);
    }

    [Theory]
    [InlineData(6, 12)]
    [InlineData(8, 16)]
    public void MoreFingers_AddFourLandingsAPair(int fingers, int pins)
    {
        var d = LangePlan.Build(3.0, 25.0, F0, Ro4350b, fingers);
        Assert.Equal(pins, d.Plan.Pins.Count);
        Assert.Equal(fingers, LangePlan.FingersForPinCount(pins));
        var p = (PlanCoupledLine)Assert.Single(d.Plan.Sections);
        Assert.Equal(fingers, p.Fingers);
        Assert.Equal(Enumerable.Range(1, pins).Order(), p.Nodes.Order());
        var (ze, zo) = LangePlan.Equivalent(d.Modes.Ze, d.Modes.Zo, fingers);
        Assert.Equal((ze, zo), (d.Ze, d.Zo));
    }

    /// <summary>The broadside pair the plan draws has, as BCLIN or MBCLIN solve it, the asked modes.</summary>
    [Fact]
    public void TheBroadsidePair_HasTheAskedModes()
    {
        var stack = new BroadsideStack(0.05e-3, 0.25e-3, double.NaN, double.NaN, 35e-6, 35e-6, 3.66, 5.8e7, 0.0037, false);
        var d = CoupledLineCouplerPlan.Build(3.0, 50.0, F0, Ro4350b, broadside: 1, stack: stack);
        var p = Assert.IsType<PlanBroadsideLine>(Assert.Single(d.Plan.Sections));
        var m = BroadsideSynthesis.ModesOf(p.W, p.O, stack);
        Assert.Equal(d.Ze, m.Ze, 1);
        Assert.Equal(d.Zo, m.Zo, 1);
        output.WriteLine($"W {p.W * 1e6:F1} um, O {p.O * 1e6:F1} um, L {p.L * 1e3:F3} mm; Ze {m.Ze:F2} Zo {m.Zo:F2}");
    }

    [Theory]
    [InlineData("broadside-no-stack", "device.coupled-line-coupler.no-second-layer")]
    [InlineData("combined", "device.coupled-line-coupler.options-combined")]
    [InlineData("even-sections", "device.coupled-line-coupler.sections-invalid")]
    [InlineData("five-fingers", "device.lange.fingers-invalid")]
    [InlineData("phase", "device.schiffman.phase-invalid")]
    public void AnOptionThePartCannotBuild_IsACodedRefusal(string what, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => _ = what switch
        {
            "broadside-no-stack" => (object)CoupledLineCouplerPlan.Build(3.0, 50.0, F0, Ro4350b, broadside: 1),
            "combined" => CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b, sections: 3, tandem: 1),
            "even-sections" => CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b, sections: 2),
            "five-fingers" => LangePlan.Build(4.0, 50.0, F0, Ro4350b, 5),
            _ => SchiffmanPlan.Build(50.0, F0, 200.0, 1.5, Ro4350b),
        });
        Assert.Equal(code, ex.Diagnostic?.Id);
    }
}
