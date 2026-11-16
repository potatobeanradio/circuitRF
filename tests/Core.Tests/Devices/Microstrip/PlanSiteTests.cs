using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>The sites the existing plans declare (docs/design/rf-passives.md, "Sites"): metadata on the pins they already
/// had, so the plans themselves are unchanged (their bit-for-bit fingerprints are <see cref="WilkinsonPlanTests"/>'s and
/// <see cref="HybridPlanTests"/>'s).</summary>
public class PlanSiteTests
{
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    [Fact]
    public void TheWilkinson_HasAResistorSiteAcrossEachPinPair_GapApart()
    {
        var one = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b);
        var site = Assert.Single(one.Plan.Sites);
        Assert.Equal((PlanSiteRole.R, PlanSiteKind.Series, "R2", (string?)"R3", "R2-R3", (double?)100.0), (site.Role, site.Kind, site.PinA, site.PinB, site.Name, site.Value));
        Assert.Equal(one.GapMeters, site.SpanMeters);

        var three = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, 1.0, 3, 2.0);
        Assert.Equal(["R2-R3", "R2_2-R3_2", "R2_3-R3_3"], three.Plan.Sites.Select(s => s.Name));
        Assert.Equal(three.Resistors.Select(r => (double?)r.Ohms), three.Plan.Sites.Select(s => s.Value));
        Assert.All(three.Plan.Sites, s => Assert.Equal(three.GapMeters, s.SpanMeters));

        var unequal = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, 1.5);
        Assert.Equal(50.0 * (1.5 + 1.0 / 1.5), Assert.Single(unequal.Plan.Sites).Value!.Value, 12);
    }

    [Fact]
    public void TheLange_HasAJumperFromEachPortToItsFinger_TwoPitchesAcross()
    {
        var d = LangePlan.Build(4.0, 50.0, 2.4e9, Ro4350b);
        Assert.Equal(["1-J1", "2-J2", "3-J3", "4-J4"], d.Plan.Sites.Select(s => s.Name));
        Assert.All(d.Plan.Sites, s =>
        {
            Assert.Equal((PlanSiteRole.Jumper, PlanSiteKind.Series), (s.Role, s.Kind));
            Assert.Equal(2.0 * (d.WMeters + d.SMeters), s.SpanMeters!.Value, 15);
        });
    }

    [Fact]
    public void TheBalun_HasAViaOnEachGroundAndTheLoadAcrossItsOutputs()
    {
        var d = MarchandBalunPlan.Build(50.0, 200.0, 2.4e9, 1e-3, Ro4350b);
        Assert.Equal(
        [
            new PlanSite(PlanSiteRole.Via, PlanSiteKind.Shunt, "G2", null, null, null),
            new PlanSite(PlanSiteRole.Via, PlanSiteKind.Shunt, "G3", null, null, null),
            new PlanSite(PlanSiteRole.Load, PlanSiteKind.Series, "2", "3", d.GapMeters, 400.0),
        ], d.Plan.Sites);
    }
}
