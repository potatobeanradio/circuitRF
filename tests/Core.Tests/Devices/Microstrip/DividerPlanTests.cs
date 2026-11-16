using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// <see cref="GyselPlan"/>, <see cref="BagleyPlan"/> and <see cref="TeeDividerPlan"/> against their closed forms:
/// impedances, λg/4 in MLIN's own dispersive εeff at F between the junctions' reference planes, and pieces that meet where
/// the plan says they do.
/// </summary>
public class DividerPlanTests
{
    /// <summary>20 mil RO4350B, 1 oz copper — the example workspace's board.</summary>
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private static MicrostripLineParameters Line(double w, double f)
        => new MicrostripLineModel(w, 1.0, 0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037, "test").LineParameters(f);

    /// <summary>Each line's impedance is MLIN's at its width, its λg/4 by MLIN's phase constant at F, on the 2 nm grid.</summary>
    private static void AssertLines(IEnumerable<WilkinsonLine> lines, double f)
    {
        foreach (var l in lines)
        {
            Assert.Equal(l.Z, Line(l.WMeters, f).Z0, 2);
            Assert.Equal(Line(l.WMeters, f).Eeff, l.Eeff, 12);
            Assert.True(Math.Abs(l.QuarterWaveMeters - PlanarLineAngle.LengthMeters(Line(l.WMeters, f), Math.PI / 2.0)) < 2e-9,
                $"{l.Name}: {l.QuarterWaveMeters}");
        }
    }

    /// <summary>The electrical length round a ring from the tee on <paramref name="from"/> to the tee on
    /// <paramref name="to"/> along the sections between them (tees counted at their reference planes, so only the lines,
    /// curves and corners between them), walking the ring by its nodes.</summary>
    private static double Along(PassivePlan plan, int from, int to, MicrostripSubstrate s, double f, double z, double q)
    {
        var tees = plan.Sections.OfType<PlanTee>().ToList();
        var start = tees.Single(t => t.Nodes[2] == from);
        double total = double.PositiveInfinity;
        foreach (int first in new[] { start.Nodes[0], start.Nodes[1] })
        {
            double sum = 0.0;
            int node = first;
            PlanSection? previous = start;
            while (true)
            {
                var next = plan.Sections.Single(x => !ReferenceEquals(x, previous) && x.Nodes.Contains(node));
                if (next is PlanTee t)
                {
                    if (t.Nodes[2] == to) { total = Math.Min(total, sum); break; }
                    break;                                    // another tee: not this way round
                }
                sum += next switch
                {
                    PlanLine l => l.L,
                    PlanCurve c => MicrostripCurve.ElectricalLength(c.Radius, c.W, c.AngleDeg),
                    PlanBend b => b.LineLength(s, f, z, q),
                    _ => throw new InvalidOperationException(),
                };
                node = next.Nodes[0] == node ? next.Nodes[1] : next.Nodes[0];
                previous = next;
            }
        }
        return total;
    }

    // ══ MGPD, the Gysel divider ═════════════════════════════════════════════════════════════════

    /// <summary>The even/odd closed form (Gysel 1975): input arms √2·Z0, isolation arms Z0 and the resistor Z2²/Z0 = Z0;
    /// the half-wave line Z0/√2; every spacing λg/4 (λg/2 between R2 and R3) between the tees' reference planes, round
    /// the curves or the corners, and every piece meets the next.</summary>
    [Theory]
    [InlineData(50.0, 2.4e9, PlanCorner.Curve)]
    [InlineData(35.0, 1.0e9, PlanCorner.Curve)]
    [InlineData(50.0, 2.4e9, PlanCorner.Miter)]
    [InlineData(50.0, 5.8e9, PlanCorner.Square)]
    public void TheGysel_IsTheClosedForm(double z0, double f, PlanCorner corner)
    {
        var d = GyselPlan.Build(z0, f, Ro4350b, corner);
        var (input, iso, half) = (d.Lines[0], d.Lines[1], d.Lines[2]);

        Assert.Equal(Math.Sqrt(2.0) * z0, input.Z, 12);
        Assert.Equal(z0, iso.Z, 12);
        Assert.Equal(z0 / Math.Sqrt(2.0), half.Z, 12);
        Assert.Equal(iso.Z * iso.Z / z0, d.IsolationOhms, 12);
        Assert.Equal(z0, Line(d.W0Meters, f).Z0, 2);
        AssertLines(d.Lines, f);
        Assert.Equal(["1", "2", "3", "R2", "R3"], d.Plan.Pins);

        double Path(int a, int b, WilkinsonLine l) => Along(d.Plan, a, b, Ro4350b, f, l.Z, l.QuarterWaveMeters);
        Assert.True(Math.Abs(Path(1, 2, input) - input.QuarterWaveMeters) < 1e-12);
        Assert.True(Math.Abs(Path(1, 3, input) - input.QuarterWaveMeters) < 1e-12);
        Assert.True(Math.Abs(Path(4, 5, half) - 2.0 * half.QuarterWaveMeters) < 1e-12);
        Assert.True(Math.Abs(Path(2, 4, iso) - iso.QuarterWaveMeters) < 4e-9, $"2 to R2: {Path(2, 4, iso)}");
        Assert.True(Math.Abs(Path(3, 5, iso) - iso.QuarterWaveMeters) < 4e-9, $"3 to R3: {Path(3, 5, iso)}");
        Assert.Equal(corner == PlanCorner.Curve ? 2 : 0, d.Plan.Sections.OfType<PlanCurve>().Count());
        Assert.Equal(corner == PlanCorner.Curve ? 0 : 4, d.Plan.Sections.OfType<PlanBend>().Count());
        HybridPlanTests.AssertJoined(d.Plan);
    }

    [Theory]
    [InlineData(0.0, 2.4e9, "device.gysel.spec-not-positive")]
    [InlineData(500.0, 2.4e9, "device.gysel.width-unreachable")]
    [InlineData(50.0, 40e9, "device.gysel.ring-too-small")]
    [InlineData(50.0, 40e9, "device.gysel.ring-too-small", PlanCorner.Miter)]
    public void AGyselThatCannotBeBuilt_IsACodedRefusal(double z0, double f, string code, PlanCorner corner = PlanCorner.Curve)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => GyselPlan.Build(z0, f, Ro4350b, corner));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }

    // ══ MBPD, the Bagley polygon ════════════════════════════════════════════════════════════════

    /// <summary>The closed form (Sakagami et al. 2007): input arms 2·Z0/√N a quarter wave from the input to v1 and vN, and
    /// sides of Z0 a half wave between neighbouring corners, every spacing between the tees' reference planes; N + 1 pins
    /// in turn round the polygon.</summary>
    [Theory]
    [InlineData(3, PlanCorner.Curve)]
    [InlineData(5, PlanCorner.Curve)]
    [InlineData(7, PlanCorner.Curve)]
    [InlineData(9, PlanCorner.Curve)]
    [InlineData(3, PlanCorner.Miter)]
    [InlineData(7, PlanCorner.Square)]
    public void TheBagley_IsTheClosedForm(int n, PlanCorner corner)
    {
        const double z0 = 50.0, f = 2.4e9;
        var d = BagleyPlan.Build(z0, f, n, Ro4350b, corner);
        var (arm, side) = (d.Lines[0], d.Lines[1]);

        Assert.Equal(2.0 * z0 / Math.Sqrt(n), arm.Z, 12);
        Assert.Equal(z0, side.Z, 12);
        AssertLines(d.Lines, f);
        Assert.Equal(BagleyPlan.PinsFor(n), d.Plan.Pins);
        Assert.Equal(n + 1, d.Plan.Pins.Count);
        Assert.Equal(n, BagleyPlan.WaysForPinCount(n + 1));

        // Pin k + 1 is the corner vk: the input to v1 and vN a quarter wave, each corner to the next a half wave.
        double Path(int a, int b, WilkinsonLine l) => Along(d.Plan, a, b, Ro4350b, f, l.Z, l.QuarterWaveMeters);
        Assert.True(Math.Abs(Path(1, 2, arm) - arm.QuarterWaveMeters) < 1e-12);
        Assert.True(Math.Abs(Path(1, n + 1, arm) - arm.QuarterWaveMeters) < 1e-12);
        for (int k = 1; k < n; k++)
            Assert.True(Math.Abs(Path(k + 1, k + 2, side) - 2.0 * side.QuarterWaveMeters) < 4e-9,
                $"v{k} to v{k + 1}: {Path(k + 1, k + 2, side)}");
        HybridPlanTests.AssertJoined(d.Plan);
    }

    [Theory]
    [InlineData(0.0, 2.4e9, 3.0, "device.bagley.spec-not-positive")]
    [InlineData(50.0, 2.4e9, 4.0, "device.bagley.ways-unsupported")]
    [InlineData(50.0, 2.4e9, 1.0, "device.bagley.ways-unsupported")]
    [InlineData(50.0, 2.4e9, 11.0, "device.bagley.ways-unsupported")]
    [InlineData(500.0, 2.4e9, 3.0, "device.bagley.width-unreachable")]
    [InlineData(50.0, 40e9, 3.0, "device.bagley.ring-too-small")]
    public void ABagleyThatCannotBeBuilt_IsACodedRefusal(double z0, double f, double n, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => BagleyPlan.Build(z0, f, n, Ro4350b));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }

    // ══ MTPD, the tee divider ═══════════════════════════════════════════════════════════════════

    /// <summary>The closed form (Pozar §7.2): transformers Z0·√(1 + K²) to 2 and Z0·√(1 + K²)/K to 3, so the junction
    /// sees Z0·(1 + K²) and Z0·(1 + K²)/K², in parallel Z0; each λg/4 from the junction's reference plane to its pin.</summary>
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.4142)]
    [InlineData(0.6)]
    public void TheTee_IsTheClosedForm(double k)
    {
        const double z0 = 50.0, f = 2.4e9;
        var d = TeeDividerPlan.Build(z0, f, Ro4350b, k);
        var t2 = d.Lines[0];
        var t3 = d.Lines[^1];

        Assert.Equal(z0 * Math.Sqrt(1 + k * k), t2.Z, 12);
        Assert.Equal(z0 * Math.Sqrt(1 + k * k) / k, t3.Z, 12);
        Assert.Equal(1.0 / z0, z0 / (t2.Z * t2.Z) + z0 / (t3.Z * t3.Z), 12);
        Assert.Equal(k == 1.0 ? 1 : 2, d.Lines.Count);
        AssertLines(d.Lines, f);
        var tee = Assert.IsType<PlanTee>(Assert.Single(d.Plan.Sections));
        Assert.Equal((t2.WMeters, t3.WMeters, d.W0Meters), (tee.W1, tee.W2, tee.W3));
        Assert.Equal(t2.QuarterWaveMeters, tee.L1 - tee.W3 / 2.0, 15);
        Assert.Equal(t3.QuarterWaveMeters, tee.L2 - tee.W3 / 2.0, 15);
        Assert.Equal([2, 3, 1], tee.Nodes);
        HybridPlanTests.AssertJoined(d.Plan);
    }

    [Theory]
    [InlineData(0.0, 1.0, "device.teedivider.spec-not-positive")]
    [InlineData(50.0, 0.0, "device.teedivider.spec-not-positive")]
    [InlineData(50.0, 0.05, "device.teedivider.width-unreachable")]       // the arm to 3 would be 1 kΩ
    public void ATeeThatCannotBeBuilt_IsACodedRefusal(double z0, double k, string code)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => TeeDividerPlan.Build(z0, 2.4e9, Ro4350b, k));
        Assert.Equal(code, ex.Diagnostic?.Id);
    }
}
