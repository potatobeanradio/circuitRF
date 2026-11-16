using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// The microstrip ladder's list and plan (<see cref="LadderPlan"/>): the list reads back what it writes, a wrong item is a
/// coded refusal naming it, the pins follow the shorted stubs, open ends are the first internal nodes, and every section
/// meets its neighbours where the list puts them.
/// </summary>
public class LadderPlanTests
{
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    /// <summary>Every kind and option, in the list's own spelling.</summary>
    private static readonly LadderSection[] EveryKind =
    [
        new LadderGap(0.2e-3),
        new LadderStub(0.6e-3, 8e-3),
        new LadderLine(0.3e-3, 4.25e-3),
        new LadderStub(1.2e-3, 5e-3, LadderStubEnd.Short, LadderStubSide.Down),
        new LadderTaper(0.3e-3, 2.5e-3, 3e-3),
        new LadderStub(0.8e-3, 6e-3, LadderStubEnd.Open, LadderStubSide.Both),
        new LadderLine(2.5e-3, 1.5e-3),
        new LadderStub(0.5e-3, 7e-3, LadderStubEnd.Short, LadderStubSide.Both),
    ];

    [Fact]
    public void TheList_ReadsBackWhatItWrites()
    {
        string text = LadderPlan.Format(EveryKind);
        Assert.Equal("gap(S=0.2mm); stub(W=0.6mm, L=8mm, end=open, side=up); line(W=0.3mm, L=4.25mm); "
                   + "stub(W=1.2mm, L=5mm, end=short, side=down); taper(W1=0.3mm, W2=2.5mm, L=3mm); "
                   + "stub(W=0.8mm, L=6mm, end=open, side=both); line(W=2.5mm, L=1.5mm); "
                   + "stub(W=0.5mm, L=7mm, end=short, side=both)", text);
        var back = LadderPlan.Parse(text);
        Assert.Equal(EveryKind.Length, back.Count);
        for (int k = 0; k < back.Count; k++)
            Assert.True(Same(EveryKind[k], back[k]), $"section {k + 1}: {EveryKind[k]} vs {back[k]}");
        Assert.Equal(text, LadderPlan.Format(back));
    }

    /// <summary>Kinds, fields and words in any case, whitespace anywhere (a netlist writer strips it), lengths in metres
    /// or with a length unit, the defaults (an open stub, up), a trailing separator and the list quoted whole.</summary>
    [Fact]
    public void TheList_IsReadInAnyCaseSpacingAndLengthUnit()
    {
        var read = LadderPlan.Parse("\" LINE( w = 1.1 mm , L=0.0181 );Gap(S=100um);taper(W1=1.1e-3,W2=40mil,L=0.5cm);"
                                  + "stub(w=1mm,l=8mm);STUB(W=1mm,L=8mm,End=Short,Side=Both); \"");
        LadderSection[] expected =
        [
            new LadderLine(1.1e-3, 0.0181), new LadderGap(100e-6), new LadderTaper(1.1e-3, 40 * 25.4e-6, 5e-3),
            new LadderStub(1e-3, 8e-3), new LadderStub(1e-3, 8e-3, LadderStubEnd.Short, LadderStubSide.Both),
        ];
        Assert.Equal(expected.Length, read.Count);
        for (int k = 0; k < read.Count; k++) Assert.True(Same(expected[k], read[k]), $"section {k + 1}: {read[k]}");
    }

    [Theory]
    [InlineData("", "device.ladder.empty", null)]
    [InlineData(" ; ", "device.ladder.empty", null)]
    [InlineData("line(W=1mm, L=2mm); lin(W=1mm, L=2mm)", "device.ladder.unknown-kind", 2)]
    [InlineData("line W=1mm L=2mm", "device.ladder.syntax", 1)]
    [InlineData("gap(S=1mm); line(W=1mm, W=2mm, L=1mm)", "device.ladder.syntax", 2)]
    [InlineData("line(W=1mm, X=2mm)", "device.ladder.unknown-field", 1)]
    [InlineData("gap(S=1mm); gap(0.1mm)", "device.ladder.unknown-field", 2)]
    [InlineData("line(W=1mm)", "device.ladder.missing-field", 1)]
    [InlineData("gap(S=abc)", "device.ladder.bad-value", 1)]
    [InlineData("gap(S=1m)", "device.ladder.bad-value", 1)]
    [InlineData("gap(S=2GHz)", "device.ladder.bad-value", 1)]
    [InlineData("gap(S=1mm); gap(S=1mm); stub(W=1mm, L=2mm, end=closed)", "device.ladder.bad-value", 3)]
    [InlineData("stub(W=1mm, L=2mm, side=1)", "device.ladder.bad-value", 1)]
    [InlineData("line(W=-1mm, L=1mm)", "device.ladder.not-positive", 1)]
    [InlineData("taper(W1=1mm, W2=0, L=1mm)", "device.ladder.not-positive", 1)]
    public void AWrongItem_IsACodedRefusalNamingIt(string text, string code, int? item)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => LadderPlan.Parse(text));
        Assert.Equal(code, ex.Diagnostic?.Id);
        if (item is { } n) Assert.Contains($"section {n} (", ex.Message);
    }

    [Theory]
    [InlineData(0.0, 2.4e9)]
    [InlineData(50.0, -1.0)]
    public void ANonPositiveSpec_IsRefused(double z0, double f)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => LadderPlan.Build(LadderPlan.DefaultSections, z0, f, Ro4350b));
        Assert.Equal("device.ladder.spec-not-positive", ex.Diagnostic?.Id);
    }

    /// <summary>Pins 1 and 2, then a G pin per shorted arm in list order, up before down; the open ends are the first
    /// internal nodes, and the shorted ends are those pins.</summary>
    [Fact]
    public void ThePins_FollowTheShortedStubs_AndTheOpenEndsComeFirst()
    {
        var plan = LadderPlan.Build(EveryKind, 50.0, 2.4e9, Ro4350b).Plan;
        Assert.Equal(["1", "2", "G1", "G2", "G3"], plan.Pins);
        Assert.Equal(3, LadderPlan.Grounds(EveryKind));
        Assert.Equal(3, LadderPlan.Grounds(LadderPlan.Format(EveryKind)));
        Assert.Equal(0, LadderPlan.Grounds("not a list"));

        var opens = plan.Sections.OfType<PlanOpen>().Select(o => o.Nodes.Single()).ToArray();
        Assert.Equal([6, 7, 8], opens.Order());
        // The down stub's tee ends on G1; the shorted cross's up and down arms on G2 and G3.
        var down = plan.Sections.OfType<PlanTee>().Single(t => t.RotationDeg == 0.0);
        Assert.Equal(3, down.Nodes[2]);
        var shorted = plan.Sections.OfType<PlanCross>().Last();
        Assert.Equal((4, 5), (shorted.Nodes[1], shorted.Nodes[3]));
    }

    /// <summary>Every plan node is one point, and the ladder runs from pin 1 at the origin to pin 2 at the sum of its
    /// sections' lengths along the through line (a stub its width, a cross its width and twice the reach past it).</summary>
    [Fact]
    public void EverySection_MeetsItsNeighbours_AndPin2IsAtTheLaddersLength()
    {
        var plan = LadderPlan.Build(EveryKind, 50.0, 2.4e9, Ro4350b).Plan;
        var at = Joined(plan);
        double length = EveryKind.Sum(s => s switch
        {
            LadderLine l => l.L,
            LadderGap g => g.S,
            LadderTaper t => t.L,
            LadderStub { Side: LadderStubSide.Both } b => b.W + 2.0 * LadderPlan.CrossThroughPast,
            LadderStub b => b.W,
            _ => 0.0,
        });
        Assert.Equal((0.0, 0.0), at[1]);
        Assert.Equal(length, at[2].X, 12);
        Assert.Equal(0.0, at[2].Y, 12);
    }

    /// <summary>A stub's arm runs L past the wider through arm's edge, on the side asked; a cross's through arms reach
    /// past its stubs, so MCROSS keeps its junction.</summary>
    [Fact]
    public void AStub_RunsLPastTheThroughLinesEdge_OnTheSideAsked()
    {
        var d = LadderPlan.Build("line(W=0.2mm, L=4mm); stub(W=1mm, L=8mm, side=down); line(W=2mm, L=4mm); "
                               + "stub(W=1mm, L=8mm, side=both); line(W=0.5mm, L=1mm)", 50.0, 2.4e9, Ro4350b);
        var at = Joined(d.Plan);
        var tee = d.Plan.Sections.OfType<PlanTee>().Single();
        Assert.Equal(-(1e-3 + 8e-3), at[tee.Nodes[2]].Y, 12);   // the wider through arm, 2 mm, sets the plane
        var cross = d.Plan.Sections.OfType<PlanCross>().Single();
        Assert.Equal(1e-3 + 8e-3, at[cross.Nodes[1]].Y, 12);
        Assert.Equal(-(1e-3 + 8e-3), at[cross.Nodes[3]].Y, 12);
        Assert.Equal((0.5e-3, 1e-3, 2e-3, 1e-3), (cross.W1, cross.W2, cross.W3, cross.W4));
        Assert.True(cross.L1 - cross.W2 / 2.0 > JunctionArmsZeroLength);

        // The readout: each section's own impedance and length at F, a stub's at the junction it stands on.
        Assert.Equal(5, d.AtF.Count);
        Assert.Equal(2e-3, d.AtF[3].WAt, 12);
        Assert.True(d.AtF[0].Z > 100.0 && d.AtF[2].Z < 40.0, $"{d.AtF[0].Z} Ω, {d.AtF[2].Z} Ω");
        Assert.True(d.AtF[1].OpenEndDeg > 0.0);
    }

    private const double JunctionArmsZeroLength = 1e-9;

    private static bool Same(LadderSection a, LadderSection b)
    {
        static bool Eq(double x, double y) => Math.Abs(x - y) < 0.5e-9;
        return (a, b) switch
        {
            (LadderLine p, LadderLine q) => Eq(p.W, q.W) && Eq(p.L, q.L),
            (LadderGap p, LadderGap q) => Eq(p.S, q.S),
            (LadderTaper p, LadderTaper q) => Eq(p.W1, q.W1) && Eq(p.W2, q.W2) && Eq(p.L, q.L),
            (LadderStub p, LadderStub q) => Eq(p.W, q.W) && Eq(p.L, q.L) && p.End == q.End && p.Side == q.Side,
            _ => false,
        };
    }

    /// <summary>Each node's one point: every section's pins, posed, land where every other section on that node puts it;
    /// an internal node joins two pieces and a pin ends one.</summary>
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
                PlanOpen => [(0, 0)],
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
        Assert.Equal(Enumerable.Range(1, plan.NodeCount), at.Keys.Order());
        foreach (int node in at.Keys)
            Assert.Equal(node <= plan.Pins.Count ? 1 : 2, plan.Sections.Count(s => s.Nodes.Contains(node)));
        return at;
    }
}
