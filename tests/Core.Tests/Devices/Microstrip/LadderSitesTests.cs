using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// The ladder's radial ends and lumped sites (<see cref="LadderPlan"/>): the list reads back what it writes, a wrong item
/// is a coded refusal naming it, every G pin and site follows the list's order, a radial end is MRADIAL's section turned
/// away from the line, and an LC ladder of sites alone is a row of pads that meet.
/// </summary>
public class LadderSitesTests
{
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private static readonly LadderSection[] Mixed =
    [
        new LadderLine(1.1e-3, 5e-3),
        new LadderStub(1e-3, 6e-3, LadderStubEnd.Short),
        new LadderSite(PlanSiteRole.C, 3.3e-12),
        new LadderSite(PlanSiteRole.L, 12e-9, PlanSiteKind.Shunt, LadderStubSide.Down),
        new LadderStub(0.6e-3, 1e-3, LadderStubEnd.Radial, LadderStubSide.Both, 8e-3, 60.0),
        new LadderLine(0.3e-3, 4e-3),
        new LadderStub(0.6e-3, 1e-3, LadderStubEnd.Butterfly, LadderStubSide.Up, 5e-3, 45.0),
        new LadderSite(PlanSiteRole.R, 4.7e3, PlanSiteKind.Shunt),
    ];

    [Fact]
    public void TheNewKinds_ReadBackWhatTheyWrite()
    {
        string text = LadderPlan.Format(Mixed);
        Assert.Equal("line(W=1.1mm, L=5mm); stub(W=1mm, L=6mm, end=short, side=up); site(role=C, value=3.3pF, type=series); "
                   + "site(role=L, value=12nH, type=shunt, side=down); "
                   + "stub(W=0.6mm, L=1mm, end=radial, side=both, Ro=8mm, angle=60); line(W=0.3mm, L=4mm); "
                   + "stub(W=0.6mm, L=1mm, end=butterfly, side=up, Ro=5mm, angle=45); site(role=R, value=4700Ohm, type=shunt, side=up)",
            text);
        var back = LadderPlan.Parse(text);
        Assert.Equal(text, LadderPlan.Format(back));
        Assert.Equal(Mixed.Length, back.Count);
    }

    /// <summary>Defaults and spellings: a site is series unless stated, a value takes its unit, a bare SI prefix or Ω; a
    /// radial end's angle is 60° unless stated and may carry its unit.</summary>
    [Fact]
    public void TheNewKinds_ReadTheirDefaultsAndSpellings()
    {
        var read = LadderPlan.Parse("site(role=c, value=2.2p); SITE(Role=L, Value=0.012uH, Type=Shunt); site(role=R, value=4.7kΩ); "
                                  + "stub(W=0.6mm, L=1mm, end=radial, Ro=8mm); stub(W=0.6mm, L=1mm, end=butterfly, Ro=8mm, angle=45deg)");
        var c = Assert.IsType<LadderSite>(read[0]);
        Assert.Equal((PlanSiteRole.C, PlanSiteKind.Series), (c.Role, c.Kind));
        Assert.Equal(2.2, c.Value * 1e12, 12);
        var l = Assert.IsType<LadderSite>(read[1]);
        Assert.Equal((PlanSiteRole.L, PlanSiteKind.Shunt, LadderStubSide.Up), (l.Role, l.Kind, l.Side));
        Assert.Equal(12.0, l.Value * 1e9, 12);
        Assert.Equal(4.7e3, Assert.IsType<LadderSite>(read[2]).Value, 9);
        Assert.Equal(60.0, Assert.IsType<LadderStub>(read[3]).AngleDeg);
        Assert.Equal((LadderStubEnd.Butterfly, 45.0), (Assert.IsType<LadderStub>(read[4]).End, ((LadderStub)read[4]).AngleDeg));
    }

    [Theory]
    [InlineData("site(value=1pF)", "device.ladder.missing-field", 1)]
    [InlineData("line(W=1mm, L=1mm); site(role=via, value=1pF)", "device.ladder.bad-value", 2)]
    [InlineData("site(role=C)", "device.ladder.missing-field", 1)]
    [InlineData("site(role=C, value=3.3nH)", "device.ladder.bad-value", 1)]
    [InlineData("site(role=C, value=0)", "device.ladder.not-positive", 1)]
    [InlineData("site(role=C, value=1pF, side=down)", "device.ladder.unknown-field", 1)]
    [InlineData("site(role=C, value=1pF, type=shunt, side=both)", "device.ladder.unknown-field", 1)]
    [InlineData("site(role=C, value=1pF, type=across)", "device.ladder.bad-value", 1)]
    [InlineData("stub(W=1mm, L=8mm, Ro=8mm)", "device.ladder.unknown-field", 1)]
    [InlineData("stub(W=1mm, L=8mm, end=short, angle=60)", "device.ladder.unknown-field", 1)]
    [InlineData("stub(W=1mm, L=1mm, end=radial)", "device.ladder.missing-field", 1)]
    [InlineData("gap(S=1mm); stub(W=1.1mm, L=1mm, end=radial, Ro=1mm)", "device.ladder.bad-value", 2)]
    [InlineData("stub(W=1mm, L=1mm, end=butterfly, Ro=8mm, angle=wide)", "device.ladder.bad-value", 1)]
    public void AWrongNewItem_IsACodedRefusalNamingIt(string text, string code, int item)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => LadderPlan.Parse(text));
        Assert.Equal(code, ex.Diagnostic?.Id);
        Assert.Contains($"section {item} (", ex.Message);
    }

    /// <summary>G pins in list order: the shorted stub's (a via site), the series site's two, the shunt sites' one each;
    /// every site declared with its role, kind, pins, span (the part's Gap) and value. A radial end is MRADIAL's section,
    /// one sector each side of a cross and a butterfly's two on a tee, each fanning away from the line.</summary>
    [Fact]
    public void TheGPins_AndTheSites_FollowTheList()
    {
        var plan = LadderPlan.Build(Mixed, 50.0, 2.4e9, Ro4350b, 1.6e-3).Plan;
        Assert.Equal(["1", "2", "G1", "G2", "G3", "G4", "G5"], plan.Pins);
        Assert.Equal(5, LadderPlan.Grounds(Mixed));
        Assert.Equal(
            [
                (PlanSiteRole.Via, PlanSiteKind.Shunt, "G1", (string?)null, (double?)null),
                (PlanSiteRole.C, PlanSiteKind.Series, "G2", "G3", 3.3e-12),
                (PlanSiteRole.L, PlanSiteKind.Shunt, "G4", null, 12e-9),
                (PlanSiteRole.R, PlanSiteKind.Shunt, "G5", null, 4.7e3),
            ], plan.Sites.Select(x => (x.Role, x.Kind, x.PinA, x.PinB, x.Value)));
        Assert.Equal(1.6e-3, plan.Sites[1].SpanMeters!.Value, 12);
        Assert.All(plan.Sites.Where(x => x.Kind == PlanSiteKind.Shunt), x => Assert.Null(x.SpanMeters));

        var radials = plan.Sections.OfType<PlanRadialStub>().ToArray();
        Assert.Equal([(1, 90.0), (1, 270.0), (2, 0.0)], radials.Select(r => (r.Sides, r.RotationDeg)));
        Assert.Equal([8, 9, 10], radials.Select(r => r.Nodes.Single()));   // the first internal nodes, after G5's 7
        Assert.All(radials, r => Assert.Equal(0.6e-3, r.W, 12));

        var at = Joined(plan);
        Assert.Equal(1.6e-3, at[5].X - at[4].X, 12);
        Assert.True(at[6].Y < 0.0, "G4, the shunt site's pad, is on the side asked");
    }

    /// <summary>An LC ladder of sites alone (series L, shunt C, series L) is a row of pads: one at each end, a tee whose
    /// branch is the shunt site's pad between the two series sites, each a site span wide; every pad a square of the
    /// width there, and every node a pin.</summary>
    [Fact]
    public void AnLcLadderOfSitesAlone_IsARowOfPads()
    {
        var d = LadderPlan.Build("site(role=L, value=8nH); site(role=C, value=6.4pF, type=shunt); site(role=L, value=8nH)",
            50.0, 1e9, Ro4350b);
        double w = d.W0Meters, gap = LadderPlan.DefaultGap;
        Assert.Equal(["1", "2", "G1", "G2", "G3", "G4", "G5"], d.Plan.Pins);
        Assert.Equal(d.Plan.Pins.Count, d.Plan.NodeCount);
        Assert.Equal(2, d.Plan.Sections.OfType<PlanLine>().Count(l => l.W == w && l.L == w));
        var tee = Assert.Single(d.Plan.Sections.OfType<PlanTee>());
        Assert.Equal((w, 1.5 * w), (tee.W3, tee.L3));
        var at = Joined(d.Plan);
        Assert.Equal(w + gap + w + gap + w, at[2].X, 12);
        Assert.Equal(["G1-G2", "G3", "G4-G5"], d.Plan.Sites.Select(s => s.Name));
    }

    /// <summary>Two series sites in a row, or one after a gap, get a pad between: the part on each lands on copper.</summary>
    [Fact]
    public void SeriesSitesInARowOrAfterAGap_GetAPadBetween()
    {
        var plan = LadderPlan.Build("gap(S=0.2mm); site(role=C, value=1pF); site(role=C, value=1pF); gap(S=0.2mm)",
            50.0, 2.4e9, Ro4350b).Plan;
        Assert.Equal(3, plan.Sections.OfType<PlanLine>().Count());
        Joined(plan);
    }

    private static Dictionary<int, (double X, double Y)> Joined(PassivePlan plan)
    {
        var at = new Dictionary<int, (double X, double Y)>();
        foreach (var section in plan.Sections)
        {
            (double X, double Y)[] local = section switch
            {
                PlanTee t => [(0, 0), (t.L1 + t.L2, 0), (t.L1, -t.L3)],
                PlanCross c => [(c.L1, 0), (0, c.L2), (-c.L3, 0), (0, -c.L4)],
                PlanLine l => [(0, 0), (l.L, 0)],
                PlanGap g => [(0, 0), (g.S, 0)],
                PlanTaper t => [(0, 0), (t.L, 0)],
                PlanOpen or PlanRadialStub => [(0, 0)],
                _ => throw new InvalidOperationException(section.GetType().Name),
            };
            double th = section.RotationDeg * Math.PI / 180.0;
            for (int j = 0; j < local.Length; j++)
            {
                var (x, y) = local[j];
                var p = (section.X + x * Math.Cos(th) - y * Math.Sin(th), section.Y + x * Math.Sin(th) + y * Math.Cos(th));
                if (at.TryGetValue(section.Nodes[j], out var q))
                    Assert.True(Math.Abs(p.Item1 - q.X) < 1e-12 && Math.Abs(p.Item2 - q.Y) < 1e-12, $"node {section.Nodes[j]}: {p} vs {q}");
                else at[section.Nodes[j]] = p;
            }
        }
        // A series site's two pins are copper's ends with nothing between them; each pin ends exactly one piece.
        Assert.Equal(Enumerable.Range(1, plan.NodeCount), at.Keys.Order());
        foreach (int node in at.Keys)
            Assert.Equal(node <= plan.Pins.Count ? 1 : 2, plan.Sections.Count(s => s.Nodes.Contains(node)));
        return at;
    }
}
