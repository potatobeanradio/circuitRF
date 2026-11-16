using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// MRRC's coupled-line form and MSPS's meandered reference against their closed forms: March's section is the 270° line
/// of √2·Z0 with the asked coupling, each of the three arms is λg/4 at F counting its corner, every node is one place, the
/// tees keep a gap from the grounded strips and G2 has room for its via; the ring of line is unchanged; a section the
/// board cannot build is refused naming the admittance it needs. The meandered reference's legs are the C-section's
/// length and its pins sit on the left edge with the C-section's.
/// </summary>
public class RatRaceCoupledLinePlanTests
{
    private static readonly MicrostripSubstrate GaAs = new(100e-6, 3e-6, 12.9, 4.1e7, 0.0006);
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    private static (double X, double Y) Posed(PlanSection s, double x, double y)
    {
        double th = s.RotationDeg * Math.PI / 180.0;
        return (s.X + x * Math.Cos(th) - y * Math.Sin(th), s.Y + x * Math.Sin(th) + y * Math.Cos(th));
    }

    /// <summary>Where each section's pins sit, posed, in its terminal order.</summary>
    private static (double X, double Y)[] PinsOf(PlanSection section)
    {
        (double X, double Y)[] local = section switch
        {
            PlanTee t => [(0, 0), (t.L1 + t.L2, 0), (t.L1, -t.L3)],
            PlanLine l => [(0, 0), (l.L, 0)],
            PlanCurve c => [(0, 0), (c.Radius * Math.Sin(Math.Abs(c.AngleDeg) * Math.PI / 180.0),
                                     Math.Sign(c.AngleDeg) * c.Radius * (1.0 - Math.Cos(c.AngleDeg * Math.PI / 180.0)))],
            PlanBend b => [(0, 0), (b.Arm * (1.0 + Math.Cos(b.AngleDeg * Math.PI / 180.0)), b.Arm * Math.Sin(b.AngleDeg * Math.PI / 180.0))],
            PlanCoupledLine c => [(0, 0), (c.L, 0), (0, -(c.W + c.S)), (c.L, -(c.W + c.S))],
            _ => throw new InvalidOperationException(section.GetType().Name),
        };
        return [.. local.Select(p => Posed(section, p.X, p.Y))];
    }

    private static Dictionary<int, List<(double X, double Y)>> Nodes(PassivePlan plan)
    {
        var at = new Dictionary<int, List<(double X, double Y)>>();
        foreach (var section in plan.Sections)
        {
            var pins = PinsOf(section);
            for (int j = 0; j < section.Nodes.Length; j++)
                (at.TryGetValue(section.Nodes[j], out var list) ? list : at[section.Nodes[j]] = []).Add(pins[j]);
        }
        return at;
    }

    [Theory]
    [InlineData(10.0, PlanCorner.Curve)]
    [InlineData(12.0, PlanCorner.Miter)]
    [InlineData(15.0, PlanCorner.Square)]
    public void TheSection_IsMarchs270DegreeLine_AndEachArmIsAQuarterWave(double cDb, PlanCorner corner)
    {
        const double f = 10e9, z0 = 50.0;
        var d = RatRacePlan.Build(z0, f, GaAs, corner, form: RatRaceForm.CoupledLine, coupling: cDb);
        var (ze, zo) = d.SectionModes;
        double c = Math.Pow(10, -cDb / 20);
        Assert.Equal(Math.Sqrt(2.0) * z0, 2 * ze * zo / (ze - zo), 9);
        Assert.Equal(c, (ze - zo) / (ze + zo), 12);
        Assert.Equal(RatRacePlan.CoupledLinePins, d.Plan.Pins);

        // Each arm, between its tees' reference planes, is λg/4 at F: its lines' lengths plus its corner's own.
        var lines = d.Plan.Sections.OfType<PlanLine>().ToList();
        double Arm(params int[] from) => lines.Where(l => from.Contains(l.Nodes[0])).Sum(l => l.L) + d.CornerMeters;
        Assert.Equal(d.QuarterWaveMeters, Arm(9, 11), 9);          // 2 → 1: tee 2's run and the rise to tee 1
        Assert.Equal(d.QuarterWaveMeters, Arm(13, 15), 9);         // 1 → 3
        Assert.Equal(d.QuarterWaveMeters, Arm(17, 19), 8);         // 3 → 4, what the square leaves, to the grid
    }

    [Theory]
    [InlineData(PlanCorner.Curve)]
    [InlineData(PlanCorner.Miter)]
    public void EveryNode_IsOnePlace_AndTheTeesClearTheGroundedStrips(PlanCorner corner)
    {
        var d = RatRacePlan.Build(50.0, 10e9, GaAs, corner, form: RatRaceForm.CoupledLine);
        var at = Nodes(d.Plan);
        Assert.Equal(Enumerable.Range(1, d.Plan.NodeCount), at.Keys.Order());
        foreach (var (node, list) in at)
            foreach (var q in list)
                Assert.True(Math.Abs(q.X - list[0].X) < 1e-12 && Math.Abs(q.Y - list[0].Y) < 1e-12, $"node {node}: {q} vs {list[0]}");
        foreach (int pin in Enumerable.Range(1, 6)) Assert.Single(at[pin]);

        var (wc, s, lc, _) = d.Section;
        double p = wc + s;
        var tee2 = d.Plan.Sections.OfType<PlanTee>().Single(t => t.Nodes[2] == 2);
        var tee4 = d.Plan.Sections.OfType<PlanTee>().Single(t => t.Nodes[1] == 4);
        // Each tee's junction is a gap or more from the grounded strip beside it: tee 2's top under strip A, tee 4's
        // bottom over strip B.
        Assert.True(-wc / 2.0 - (-p + Math.Max(tee2.W1, tee2.W2) / 2.0) >= s - 1e-12);
        Assert.True(-Math.Max(tee4.W1, tee4.W2) / 2.0 - (-p + wc / 2.0) >= s - 1e-12);
        // G2's via has three port widths of room inside the ring before the right side.
        double rightInner = d.Plan.Sections.OfType<PlanLine>().Single(l => l.Nodes[0] == 11).X - d.RingWMeters / 2.0;
        Assert.True(rightInner - lc >= 3.0 * d.W0Meters, $"{rightInner - lc} m");
    }

    [Fact]
    public void TheRingOfLine_IsUnchanged()
    {
        var ring = RatRacePlan.Build(50.0, 2.4e9, Ro4350b);
        var explicitRing = RatRacePlan.Build(50.0, 2.4e9, Ro4350b, PlanCorner.Curve, 0, RatRaceForm.Ring, 3.0);
        Assert.Equal(ring.Plan.Sections.Count, explicitRing.Plan.Sections.Count);
        foreach (var (a, b) in ring.Plan.Sections.Zip(explicitRing.Plan.Sections))
        {
            Assert.Equal(a.Nodes, b.Nodes);
            Assert.Equal(a with { Nodes = b.Nodes }, b);
        }
        Assert.Equal(RatRacePlan.Pins, ring.Plan.Pins);
    }

    /// <summary>On 20 mil RO4350B a 50 Ω ring needs 1/Zo − 1/Ze = √2/50 = 0.028 S, past the 0.022 S edge coupling reaches
    /// there at S/h = 0.1, whatever the coupling; a 75 Ω ring needs 0.019 S and builds.</summary>
    [Fact]
    public void ASectionTheBoardCannotBuild_IsRefusedNamingItsAdmittance()
    {
        foreach (double cDb in new[] { 6.0, 10.0, 12.0 })
        {
            var ex = Assert.Throws<ParameterRefusalException>(() =>
                RatRacePlan.Build(50.0, 2.4e9, Ro4350b, form: RatRaceForm.CoupledLine, coupling: cDb));
            Assert.Equal("device.ratrace.coupled-line-unbuildable", ex.Diagnostic?.Id);
        }
        Assert.Equal(RatRaceForm.CoupledLine, RatRacePlan.Build(75.0, 2.4e9, Ro4350b, form: RatRaceForm.CoupledLine).Form);
        var meander = Assert.Throws<ParameterRefusalException>(() =>
            RatRacePlan.Build(50.0, 10e9, GaAs, meander: 1, form: RatRaceForm.CoupledLine));
        Assert.Equal("device.ratrace.coupled-line-meander", meander.Diagnostic?.Id);
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(45.0)]
    public void TheMeanderedReference_LiesUnderTheCSection_PinsOnItsLeftEdge(double phase)
    {
        var d = SchiffmanPlan.Build(50.0, 2.4e9, phase, 1.4, Ro4350b, SchiffmanReference.Meander);
        var at = Nodes(d.Plan);
        foreach (int pin in new[] { 1, 2, 3, 4 }) Assert.Equal(0.0, Assert.Single(at[pin]).X, 12);
        var legs = d.Plan.Sections.OfType<PlanLine>().Where(l => l.Nodes.Contains(3) || l.Nodes.Contains(4)).ToList();
        Assert.Equal(2, legs.Count);
        Assert.All(legs, l => Assert.InRange(l.L, d.LengthMeters - 1e-5, d.LengthMeters + 1e-9));
        Assert.True(at[4][0].Y < at[3][0].Y, "pin 4 below pin 3");
        Assert.True(d.MeanderPitchMeters >= 6.0 * d.W0Meters - 1e-9);
        foreach (var (node, list) in at)
            foreach (var p in list)
                Assert.True(Math.Abs(p.X - list[0].X) < 1e-12 && Math.Abs(p.Y - list[0].Y) < 1e-12 || node is 5 or 6,
                    $"node {node}: {p} vs {list[0]}");
    }
}
