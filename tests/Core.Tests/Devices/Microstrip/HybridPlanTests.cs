using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// <see cref="BranchLinePlan"/> and <see cref="RatRacePlan"/> against their closed forms (Pozar; Reed and Wheeler
/// 1956): the arms' impedances, λg/4 in MLIN's own dispersive εeff at F between the junctions' reference planes,
/// and pieces that meet where the plan says they do.
/// </summary>
public class HybridPlanTests
{
    /// <summary>20 mil RO4350B, 1 oz copper — the example workspace's board.</summary>
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private static MicrostripLineParameters Line(double w, double f)
        => new MicrostripLineModel(w, 1.0, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, "test").LineParameters(f);

    private static double Quarter(double eeff, double f) => 2.99792458e8 / (4.0 * f * Math.Sqrt(eeff));

    [Theory]
    [InlineData(1, 50.0, 2.4e9)]
    [InlineData(1, 50.0, 5.8e9)]
    [InlineData(2, 50.0, 2.4e9)]
    [InlineData(2, 35.0, 1.0e9)]
    public void TheBranchLine_IsTheClosedForm(int n, double z0, double f)
    {
        var d = BranchLinePlan.Build(z0, f, n, Ro4350b);

        Assert.Equal(n, d.Series.Count);
        Assert.Equal(n + 1, d.Shunts.Count);
        // Normalised admittances: t the series arms, a the outer shunts, b the middle one.
        double t = z0 / d.Series[0].Z, a = z0 / d.Shunts[0].Z;
        if (n == 1)
        {
            Assert.Equal(Math.Sqrt(2.0), t, 12);                       // even/odd: t² − a² = 1, coupling a/t = 1/√2
            Assert.Equal(1.0, a, 12);
        }
        else
        {
            double b = z0 / d.Shunts[1].Z;
            Assert.Equal(1.0, t, 12);
            Assert.Equal(b, 2.0 * a * t * t / (1.0 + a * a), 12);       // matched and isolated at F
            Assert.Equal(1.0 / Math.Sqrt(2.0), b / (t * t), 12);       // and 3 dB
            Assert.Equal(d.Shunts[0].Z, d.Shunts[2].Z, 12);
        }

        Assert.Equal(z0, Line(d.W0Meters, f).Z0, 2);
        foreach (var arm in d.Series.Concat(d.Shunts))
        {
            Assert.Equal(arm.Z, Line(arm.WMeters, f).Z0, 2);
            Assert.Equal(Line(arm.WMeters, f).Eeff, arm.Eeff, 12);
        }
        foreach (var arm in d.Series)
            Assert.True(Math.Abs(arm.LMeters - Quarter(arm.Eeff, f)) < 2e-9, $"series {arm.LMeters} vs {Quarter(arm.Eeff, f)}");
        // The shunt arms share one span: each within its εeff's spread of λg/4, and on average exact.
        foreach (var arm in d.Shunts)
            Assert.True(Math.Abs(arm.LMeters / Quarter(arm.Eeff, f) - 1.0) < (n == 1 ? 1e-6 : 0.03), $"shunt {arm.Z} ohm");
        Assert.True(Math.Abs(d.Shunts.Average(arm => arm.LMeters - Quarter(arm.Eeff, f))) < 4e-9);
    }

    /// <summary>Every arm runs between its junctions' reference planes: through arms at the branch's half width,
    /// the branch at the wider through arm's half width plus the W0 of shunt line it carries.</summary>
    [Fact]
    public void TheBranchLineArms_RunBetweenReferencePlanes()
    {
        var d = BranchLinePlan.Build(50.0, 2.4e9, 2, Ro4350b);
        var tees = d.Plan.Sections.OfType<PlanTee>().ToArray();
        var lines = d.Plan.Sections.OfType<PlanLine>().ToArray();

        Assert.Equal(6, tees.Length);
        foreach (var t in tees)
        {
            Assert.Equal(t.W3 / 2.0, t.L1, 15);
            Assert.Equal(t.W3 / 2.0, t.L2, 15);
            Assert.Equal(Math.Max(t.W1, t.W2) / 2.0 + d.W0Meters, t.L3, 15);
        }
        foreach (var arm in d.Series)                               // the same arm on both through lines
            Assert.Equal(2 * d.Series.Count(a => a == arm), lines.Count(l => l.W == arm.WMeters && l.L == arm.LMeters));
        foreach (var arm in d.Shunts.Distinct())
            Assert.Contains(lines, l => l.W == arm.WMeters && Math.Abs(l.L + 2.0 * d.W0Meters - arm.LMeters) < 1e-15);
        Assert.Equal(4, lines.Count(l => l.Nodes.Any(node => node <= 4) && l.L == d.FeedMeters && l.W == d.W0Meters));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TheBranchLine_Closes(int n) => AssertJoined(BranchLinePlan.Build(50.0, 2.4e9, n, Ro4350b).Plan);

    [Theory]
    [InlineData(50.0, 2.4e9)]
    [InlineData(50.0, 5.8e9)]
    [InlineData(35.0, 1.0e9)]
    public void TheRatRace_IsTheClosedForm(double z0, double f)
    {
        var d = RatRacePlan.Build(z0, f, Ro4350b);

        Assert.Equal(Math.Sqrt(2.0) * z0, d.RingZ0, 12);
        Assert.Equal(z0, Line(d.W0Meters, f).Z0, 2);
        Assert.Equal(d.RingZ0, Line(d.RingWMeters, f).Z0, 2);
        Assert.Equal(Line(d.RingWMeters, f).Eeff, d.RingEeff, 12);
        double q = Quarter(d.RingEeff, f);
        Assert.True(Math.Abs(d.QuarterWaveMeters - q) < 2e-9);

        // Ring order 2, 1, 3, 4: each spacing reference plane to reference plane (every tee's through arms end
        // on them), the half-turns at their effective length.
        var s = d.Plan.Sections;
        double Arc(int i) => MicrostripCurve.ElectricalLength(((PlanCurve)s[i]).Radius, d.RingWMeters, 180.0);
        double[] spacing =
        [
            ((PlanLine)s[1]).L,                                   // 2 → 1
            ((PlanLine)s[3]).L,                                   // 1 → 3
            Arc(5) + ((PlanLine)s[6]).L,                          // 3 → 4
            ((PlanLine)s[8]).L + Arc(9),                          // 4 → 2
        ];
        double[] want = [q, q, q, 3.0 * q];
        for (int k = 0; k < 4; k++)
            Assert.True(Math.Abs(spacing[k] - want[k]) < 10e-9, $"spacing {k}: {spacing[k]} vs {want[k]}");
        Assert.True(Math.Abs(spacing.Sum() - 1.5 * 4.0 * q) < 20e-9, "1.5 guide wavelengths round");
        foreach (var t in s.OfType<PlanTee>())
        {
            Assert.Equal(t.W3 / 2.0, t.L1, 15);
            Assert.Equal(d.RingWMeters / 2.0 + d.FeedMeters, t.L3, 15);
        }
        Assert.Equal(["1", "2", "3", "4"], d.Plan.Pins);
        AssertJoined(d.Plan);
    }

    /// <summary>The rat-race at <c>Corner = Curve</c>, every number of its plan fingerprinted before the corners existed.</summary>
    [Theory]
    [InlineData(50.0, 2.4e9, "625C3B157BBCED9C")]
    [InlineData(50.0, 5.8e9, "332F374E5FC98B1A")]
    [InlineData(35.0, 1.0e9, "91A2C4A18898AC18")]
    public void TheRatRace_AtCurvedCorners_IsUnchangedBitForBit(double z0, double f, string fingerprint)
    {
        var d = RatRacePlan.Build(z0, f, Ro4350b);
        Assert.Equal(fingerprint, WilkinsonPlanTests.Fingerprint(d.Plan,
            [d.W0Meters, d.RingZ0, d.RingWMeters, d.RingEeff, d.QuarterWaveMeters, d.RadiusMeters, d.FeedMeters]));
    }

    /// <summary>With corners the ring is a rectangle and its spacings are unchanged: each end's two corners, at their own
    /// electrical length at F from the corner's model, and the side between them make λg/4 − W0, so 3 → 4 is λg/4 and
    /// 4 → 2 is 3λg/4; every piece meets the next.</summary>
    [Theory]
    [InlineData(PlanCorner.Miter)]
    [InlineData(PlanCorner.Square)]
    public void TheRatRace_WithCorners_KeepsItsSpacings(PlanCorner corner)
    {
        var d = RatRacePlan.Build(50.0, 2.4e9, Ro4350b, corner);
        var s = d.Plan.Sections;
        double q = d.QuarterWaveMeters;
        var bends = s.OfType<PlanBend>().ToList();
        Assert.Equal(4, bends.Count);
        Assert.All(bends, b => Assert.Equal(corner == PlanCorner.Miter, b.M is not null));
        double c = bends[0].LineLength(Ro4350b, 2.4e9, d.RingZ0, q);
        Assert.Equal(c, d.CornerMeters, 15);
        double L(int i) => ((PlanLine)s[i]).L;
        double[] spacing = [L(1), L(3), 2.0 * c + L(6) + L(8), L(10) + 2.0 * c + L(12)];
        double[] want = [q, q, q, 3.0 * q];
        for (int k = 0; k < 4; k++)
            Assert.True(Math.Abs(spacing[k] - want[k]) < 10e-9, $"spacing {k}: {spacing[k]} vs {want[k]}");
        Assert.Equal(d.SideMeters, L(6), 15);
        AssertJoined(d.Plan);
    }

    [Theory]
    [InlineData(0.0, 2.4e9, 1.0, "device.branchline.spec-not-positive")]
    [InlineData(50.0, -1.0, 1.0, "device.branchline.spec-not-positive")]
    [InlineData(50.0, 2.4e9, 5.0, "device.branchline.sections")]
    [InlineData(50.0, 2.4e9, 1.5, "device.branchline.sections")]
    [InlineData(50.0, 30e9, 1.0, "device.branchline.arm-too-short")]
    [InlineData(200.0, 2.4e9, 2.0, "device.branchline.width-unreachable")]
    public void ABranchLineThatCannotBeBuilt_IsACodedRefusal(double z0, double f, double n, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => BranchLinePlan.Build(z0, f, n, Ro4350b));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }

    [Theory]
    [InlineData(0.0, 2.4e9, "device.ratrace.spec-not-positive")]
    [InlineData(50.0, 40e9, "device.ratrace.ring-too-small")]
    [InlineData(500.0, 2.4e9, "device.ratrace.width-unreachable")]
    [InlineData(50.0, 40e9, "device.ratrace.ring-too-small", PlanCorner.Miter)]
    [InlineData(50.0, 40e9, "device.ratrace.ring-too-small", PlanCorner.Square)]
    public void ARatRaceThatCannotBeBuilt_IsACodedRefusal(double z0, double f, string code,
        PlanCorner corner = PlanCorner.Curve)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => RatRacePlan.Build(z0, f, Ro4350b, corner));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }

    /// <summary>Every plan node is one point: each section's pins, posed, land where every other section on that
    /// node puts it.</summary>
    internal static void AssertJoined(PassivePlan plan)
    {
        var at = new Dictionary<int, (double X, double Y)>();
        foreach (var section in plan.Sections)
        {
            (double X, double Y)[] local = section switch
            {
                PlanTee t => [(0, 0), (t.L1 + t.L2, 0), (t.L1, -t.L3)],
                PlanLine l => [(0, 0), (l.L, 0)],
                PlanCurve c => [(0, 0), (c.Radius * Math.Sin(Math.Abs(c.AngleDeg) * Math.PI / 180.0),
                                         Math.Sign(c.AngleDeg) * c.Radius * (1.0 - Math.Cos(c.AngleDeg * Math.PI / 180.0)))],
                PlanBend b => [(0, 0), (b.Arm * (1.0 + Math.Cos(b.AngleDeg * Math.PI / 180.0)),
                                        b.Arm * Math.Sin(b.AngleDeg * Math.PI / 180.0))],
                _ => throw new InvalidOperationException(),
            };
            double th = section.RotationDeg * Math.PI / 180.0;
            for (int j = 0; j < local.Length; j++)
            {
                var (x, y) = local[j];
                var p = (section.X + x * Math.Cos(th) - y * Math.Sin(th), section.Y + x * Math.Sin(th) + y * Math.Cos(th));
                if (at.TryGetValue(section.Nodes[j], out var q))
                    Assert.True(Math.Abs(p.Item1 - q.X) < 1e-12 && Math.Abs(p.Item2 - q.Y) < 1e-12,
                        $"node {section.Nodes[j]}: {p} vs {q}");
                else at[section.Nodes[j]] = p;
            }
        }
        Assert.Equal(Enumerable.Range(1, plan.NodeCount), at.Keys.Order());
        // An internal node joins two pieces; a pin ends one.
        foreach (int node in at.Keys)
            Assert.Equal(node <= plan.Pins.Count ? 1 : 2, plan.Sections.Count(s => s.Nodes.Contains(node)));
    }

    // ══ MBLC up to four sections (Levy and Lind 1968) ═══════════════════════════════════════════

    /// <summary>Three sections: series arms Z0 and the closed form y2 = √(1 − 1/√2), y1 = √2·y2·(√(1 + 1/√2) − 1). Four:
    /// the even half matched (ρ = 0) and flat (ρ' = 0) at F; both 3 dB (|B| = 1/√2) — the conditions the plan solves.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void MoreBranchLineSections_AreTheEvenOddConditions(int n)
    {
        var y = BranchLinePlan.ShuntAdmittances(n);
        Assert.Equal(n + 1, y.Length);
        Assert.Equal(y.Reverse(), y);
        if (n == 3)
        {
            Assert.Equal(Math.Sqrt(1 - Math.Sqrt(0.5)), y[1], 14);
            Assert.Equal(Math.Sqrt(2) * y[1] * (Math.Sqrt(1 + Math.Sqrt(0.5)) - 1), y[0], 14);
        }
        else
        {
            Assert.Equal(Math.Sqrt(0.5), y[1] * (2 - y[1] * y[2]), 12);
            double h = 1e-4;
            Assert.True(Math.Abs((BranchLinePlan.Rho(y, Math.PI / 2 + h) - BranchLinePlan.Rho(y, Math.PI / 2 - h)) / (2 * h)) < 1e-7);
        }
        Assert.True(Math.Abs(BranchLinePlan.Rho(y, Math.PI / 2)) < 1e-14);

        // The outer arms are 4.26·Z0 (three) and 7.40·Z0 (four): on 20 mil RO4350B a 50 Ω coupler of three builds and one
        // of four is refused (370 Ω), and on a 60 mil PTFE board a 35 Ω one of four builds (259 Ω). The plan lays them out.
        var ptfe = new MicrostripSubstrate(1.524e-3, 35e-6, 2.2, 5.8e7, 0.0009);
        double z0 = n == 3 ? 50.0 : 35.0;
        var d = BranchLinePlan.Build(z0, 2.4e9, n, n == 3 ? Ro4350b : ptfe);
        Assert.Equal(y.Select(v => z0 / v), d.Shunts.Select(a => a.Z));
        Assert.All(d.Series, a => Assert.Equal(z0, a.Z));
        HybridPlanTests.AssertJoined(d.Plan);
        if (n == 4)
            Assert.Equal("device.branchline.width-unreachable",
                Assert.Throws<ParameterRefusalException>(() => BranchLinePlan.Build(50.0, 2.4e9, 4, Ro4350b)).Diagnostic?.Id);
    }
}
