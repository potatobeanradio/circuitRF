using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// The coupled-line plans' numbers: <see cref="CoupledLineSynthesis"/> inverts MCLIN's own modes, Ou's equations
/// (<see cref="LangePlan"/>) invert and agree with Pozar's four-finger closed form, the balun's coupling is the match
/// condition, and a coupling the board cannot build is a coded refusal.
/// </summary>
public class CoupledLinePlanTests(ITestOutputHelper output)
{
    /// <summary>20 mil RO4350B, 1 oz copper — the example workspace's board.</summary>
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private const double C0 = 2.99792458e8;

    private static KirschningJansenCoupled.Modes ModesOf(double w, double s, double f)
        => new MicrostripCoupledLineModel(w, s, 1.0, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, "test").ModesAt(f);

    [Theory]
    [InlineData(69.37, 36.04, 2.4e9)]      // 10 dB, 50 ohm
    [InlineData(55.28, 45.23, 2.4e9)]      // 20 dB
    [InlineData(80.0, 40.0, 1.0e9)]
    [InlineData(120.0, 60.0, 5.8e9)]
    public void Synthesis_ReturnsThePairWhoseModelModesAreAsked(double ze, double zo, double f)
    {
        var g = CoupledLineSynthesis.Synthesize(ze, zo, f, Ro4350b, "test");
        var m = ModesOf(g.WMeters, g.SMeters, f);
        Assert.Equal(ze, m.Ze, 6);
        Assert.Equal(zo, m.Zo, 6);
        Assert.Equal(m, g.Modes);
        Assert.Equal(C0 / (4.0 * f * Math.Sqrt((m.EeffE + m.EeffO) / 2.0)), g.QuarterWaveMeters, 12);
    }

    [Theory]
    [InlineData(10.0, 50.0)]
    [InlineData(20.0, 75.0)]
    public void ModeImpedances_AreTheMatchedCoupler(double cDb, double z0)
    {
        var (ze, zo) = CoupledLineSynthesis.ModeImpedances(cDb, z0);
        Assert.Equal(z0 * z0, ze * zo, 9);
        Assert.Equal(Math.Pow(10, -cDb / 20), CoupledLineSynthesis.Coupling(ze, zo), 12);
    }

    /// <summary>Ou's inversion is exact, and for four fingers it is Pozar's closed form for the Lange coupler.</summary>
    [Theory]
    [InlineData(3.0, 50.0, 4)]
    [InlineData(6.0, 50.0, 4)]
    [InlineData(3.0, 50.0, 6)]
    public void OuEquations_InvertAndMatchThePozarFourFingerForm(double cDb, double z0, int n)
    {
        double c = Math.Pow(10, -cDb / 20);
        var (ze, zo) = LangePlan.PairFor(cDb, z0, n);
        var (zeN, zoN) = LangePlan.Equivalent(ze, zo, n);
        Assert.Equal(z0 * z0, zeN * zoN, 8);
        Assert.Equal(c, CoupledLineSynthesis.Coupling(zeN, zoN), 12);
        if (n != 4) return;
        double root = Math.Sqrt(9 - 8 * c * c);
        Assert.Equal(z0 * (4 * c - 3 + root) / (2 * c * Math.Sqrt((1 - c) / (1 + c))), ze, 9);
        Assert.Equal(z0 * (4 * c + 3 - root) / (2 * c * Math.Sqrt((1 + c) / (1 - c))), zo, 9);
    }

    [Fact]
    public void TheCoupler_IsOneMclinOfTheSynthesizedPair()
    {
        var d = CoupledLineCouplerPlan.Build(10.0, 50.0, 2.4e9, Ro4350b);
        var p = Assert.IsType<PlanCoupledLine>(Assert.Single(d.Plan.Sections));
        Assert.Equal(["1", "2", "3", "4"], d.Plan.Pins);
        Assert.Equal([1, 2, 3, 4], p.Nodes);
        Assert.Equal((d.WMeters, d.SMeters, d.LengthMeters, 2), (p.W, p.S, p.L, p.Fingers));
        // On the 2 nm grid, the drawn pair is within a few milliohms of the asked modes.
        Assert.Equal(d.Ze, d.Modes.Ze, 2);
        Assert.Equal(d.Zo, d.Modes.Zo, 2);
    }

    [Fact]
    public void TheBalun_CouplingMatchesZlToZ0_AndItsSectionsMeetAcrossGap()
    {
        var d = MarchandBalunPlan.Build(50.0, 150.0, 2.4e9, 1e-3, Ro4350b);
        Assert.Equal(1.0 / Math.Sqrt(7.0), d.Coupling, 12);
        // The open-circuit input impedance at the quarter wave, 2(Ze Zo)^2 / (ZL (Ze - Zo)^2), is Z0.
        Assert.Equal(50.0, 2 * Math.Pow(d.Ze * d.Zo, 2) / (150.0 * Math.Pow(d.Ze - d.Zo, 2)), 9);
        var s = d.Plan.Sections;
        var first = (PlanCoupledLine)s[0];
        var join = (PlanLine)s[1];
        var second = (PlanCoupledLine)s[2];
        Assert.Equal(first.X + first.L, join.X, 15);
        Assert.Equal(join.X + join.L, second.X, 15);
        Assert.Equal(first.Nodes[1], join.Nodes[0]);
        Assert.Equal(join.Nodes[1], second.Nodes[0]);
    }

    [Theory]
    [InlineData("coupler", 3.0, "device.coupled-line.gap-unreachable")]
    [InlineData("coupler", 0.0, "device.coupled-line-coupler.spec-not-positive")]
    [InlineData("coupler", 45.0, "device.coupled-line.gap-too-wide")]
    [InlineData("lange", -1.0, "device.lange.spec-not-positive")]
    [InlineData("lange", 2.0, "device.coupled-line.gap-unreachable")]
    [InlineData("balun", 50.0, "device.coupled-line.gap-unreachable")]
    [InlineData("balun", 0.0, "device.marchand.spec-not-positive")]
    public void ASpecTheBoardCannotBuild_IsACodedRefusal(string part, double spec, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => _ = part switch
        {
            "coupler" => (object)CoupledLineCouplerPlan.Build(spec, 50.0, 2.4e9, Ro4350b),
            "lange" => LangePlan.Build(spec, 50.0, 2.4e9, Ro4350b),
            _ => MarchandBalunPlan.Build(50.0, spec, 2.4e9, 1e-3, Ro4350b),
        });
        Assert.Equal(code, ex.Diagnostic?.Id);
    }

    /// <summary>What the board builds: the gap each coupling needs on 20 mil RO4350B at 2.4 GHz (the realisability
    /// finding recorded in docs/design/rf-passives.md). Prints; asserts only that the gap shrinks as coupling tightens.</summary>
    [Fact]
    public void Realisability_OnTwentyMilRo4350b()
    {
        double last = 0.0;
        foreach (double cDb in new[] { 2.0, 3.0, 3.5, 4.0, 4.5, 5.0, 6.0, 8.0 })
        {
            var (ze, zo) = LangePlan.PairFor(cDb, 50.0, 4);
            string row;
            try
            {
                var g = CoupledLineSynthesis.Synthesize(ze, zo, 2.4e9, Ro4350b, "Lange");
                row = $"W {g.WMeters * 1e6:F0} um  S {g.SMeters * 1e6:F1} um  L {g.QuarterWaveMeters * 1e3:F2} mm";
                Assert.True(g.SMeters > last);
                last = g.SMeters;
            }
            catch (ParameterRefusalException ex) { row = ex.Diagnostic!.Id; }
            output.WriteLine($"Lange 4-finger {cDb} dB: pair Ze {ze:F1} Zo {zo:F1}  {row}");
        }
        foreach (double cDb in new[] { 6.0, 7.0, 8.0, 10.0, 15.0, 20.0 })
        {
            var (ze, zo) = CoupledLineSynthesis.ModeImpedances(cDb, 50.0);
            string row;
            try
            {
                var g = CoupledLineSynthesis.Synthesize(ze, zo, 2.4e9, Ro4350b, "coupler");
                row = $"W {g.WMeters * 1e6:F0} um  S {g.SMeters * 1e6:F1} um  L {g.QuarterWaveMeters * 1e3:F2} mm";
            }
            catch (ParameterRefusalException ex) { row = ex.Diagnostic!.Id; }
            output.WriteLine($"Coupler {cDb} dB: {row}");
        }
        foreach (double zl in new[] { 50.0, 75.0, 100.0, 125.0, 150.0, 200.0 })
        {
            double c = MarchandBalunPlan.CouplingFor(50.0, zl);
            var (ze, zo) = (50.0 * Math.Sqrt((1 + c) / (1 - c)), 50.0 * Math.Sqrt((1 - c) / (1 + c)));
            string row;
            try
            {
                var g = CoupledLineSynthesis.Synthesize(ze, zo, 2.4e9, Ro4350b, "balun");
                row = $"W {g.WMeters * 1e6:F0} um  S {g.SMeters * 1e6:F1} um";
            }
            catch (ParameterRefusalException ex) { row = ex.Diagnostic!.Id; }
            output.WriteLine($"Balun ZL {zl}: c {-20 * Math.Log10(c):F2} dB  {row}");
        }
    }
}
