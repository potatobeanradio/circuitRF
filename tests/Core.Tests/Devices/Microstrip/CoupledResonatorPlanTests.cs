using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using Xunit;

namespace CircuitRF.Core.Tests.Devices.Microstrip;

/// <summary>
/// MCRC's plan: every shape and form is joined where its pieces meet, keeps each copper island clear of the others,
/// numbers its open ends first of the internal nodes, puts pin 1 at the origin, and refuses what it cannot build with a
/// coded message. The responses are the engine tests' (CoupledResonatorSParamTests).
/// </summary>
public class CoupledResonatorPlanTests
{
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);

    /// <summary>A spec from "Key=value;..." as the factory reads one: a number in SI, anything else a name.</summary>
    internal static CoupledResonatorSpec Spec(string values)
    {
        var d = new Dictionary<string, string>();
        foreach (var kv in values.Split(';', StringSplitOptions.RemoveEmptyEntries))
            d[kv[..kv.IndexOf('=')]] = kv[(kv.IndexOf('=') + 1)..];
        return CoupledResonatorSpec.Read(
            k => d.TryGetValue(k, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null,
            k => d.TryGetValue(k, out var v) && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? v : null);
    }

    // The registry's default design: three resonators at 2.4 GHz on 20 mil RO4350B.
    private const string Gaps = "S1=0.126e-3;S2=0.574e-3;S3=0.574e-3;S4=0.126e-3";

    public static TheoryData<string> Samples => new()
    {
        Gaps,
        $"{Gaps};Shape=ZigZag",
        $"{Gaps};Shape=ZigZag;Feed=Tapped;Tap=8e-3;L2=37.5e-3",
        $"{Gaps};Shape=Hairpin;Sa=2.2e-3",
        $"{Gaps};Shape=Hairpin;Profile=SIR;Wc=0.5e-3;Uc=0.4;Feed=Tapped;Tap=9e-3",
        $"{Gaps};Profile=SIR;Wc=0.5e-3;Uc=0.3;L1=36e-3",
        $"{Gaps};Shape=OpenLoop;Gap=1e-3",
        $"{Gaps};Shape=OpenLoop;Gap=1e-3;Feed=Tapped;Tap=3e-3;L2=36e-3",
        $"{Gaps};S5=0.126e-3;N=4;Shape=OpenLoop;Arrangement=Cross;Gap=1e-3;Sx=0.9e-3;Feed=Tapped;Tap=3e-3",
        $"{Gaps};S5=0.126e-3;N=4;Shape=OpenLoop;Arrangement=Cross;Gap=1e-3;Sx=0.9e-3",
        "N=1;Shape=Ring;L=74e-3;S1=0.2e-3;S2=0.2e-3",
        $"{Gaps};Shape=ZigZag;Form=Bandstop",
        $"{Gaps};Shape=Hairpin;Form=Bandstop;Sa=2.2e-3",
        "N=2;W=0.6e-3;L=18e-3;Form=Spurline;S1=0.2e-3;S2=0.3e-3",
        // Coupling = Full: N-line sections.
        $"{Gaps};Shape=Hairpin;Sa=2.2e-3;Coupling=Full",
        $"{Gaps};Shape=Hairpin;Profile=SIR;Wc=0.5e-3;Uc=0.4;Feed=Tapped;Tap=9e-3;Coupling=Full",
        $"{Gaps};Profile=SIR;Wc=0.5e-3;Uc=0.3;W2=0.8e-3;Coupling=Full",
        $"{Gaps};Shape=Hairpin;Form=Bandstop;Sa=2.2e-3;Coupling=Full",
        $"{Gaps};Shape=OpenLoop;Gap=1e-3;Coupling=Full",
    };

    /// <summary>Each strip's centre-line offset in an N-line section, downward from the first.</summary>
    private static double[] Offsets(PlanCoupledStrips m)
    {
        var y = new double[m.W.Count];
        for (int k = 1; k < y.Length; k++) y[k] = y[k - 1] - (m.W[k - 1] + m.W[k]) / 2.0 - m.S[k - 1];
        return y;
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
            PlanCoupledStrips m => [.. Offsets(m).SelectMany(y => new[] { (0.0, y), (m.L, y) })],
            PlanGap g => [(0, 0), (g.S, 0)],
            PlanOpen => [(0, 0)],
            _ => throw new InvalidOperationException(section.GetType().Name),
        };
        return [.. local.Select(p => Posed(section, p.X, p.Y))];
    }

    private static (double X, double Y) Posed(PlanSection s, double x, double y)
    {
        double th = s.RotationDeg * Math.PI / 180.0;
        return (s.X + x * Math.Cos(th) - y * Math.Sin(th), s.Y + x * Math.Sin(th) + y * Math.Cos(th));
    }

    private static double WidthOf(PlanSection s) => s switch
    {
        PlanTee t => t.W1, PlanLine l => l.W, PlanCurve c => c.W, PlanBend b => b.W, PlanCoupledLine c => c.W,
        PlanCoupledStrips m => m.W.Max(),
        PlanGap g => g.W, PlanOpen o => o.W, _ => 0.0,
    };

    /// <summary>
    /// Every node is one place: the pins on it coincide, except where the copper meets across a width (a ring's feed gap
    /// lands on the ring's edge, a spurline's full-width line splits into two strips), and then within the widest piece
    /// there. Each pin is on one section, and every node is used.
    /// </summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void EveryNode_IsOnePlace(string values)
    {
        var plan = CoupledResonatorPlan.Build(Spec(values), Ro4350b);
        var at = new Dictionary<int, List<((double X, double Y) P, PlanSection S)>>();
        foreach (var section in plan.Sections)
        {
            var pins = PinsOf(section);
            for (int j = 0; j < section.Nodes.Length; j++)
                (at.TryGetValue(section.Nodes[j], out var list) ? list : at[section.Nodes[j]] = []).Add((pins[j], section));
        }
        Assert.Equal(Enumerable.Range(1, plan.NodeCount), at.Keys.Order());
        foreach (var (node, list) in at)
        {
            double reach = list.Max(x => WidthOf(x.S)) + list.Select(x => x.S).OfType<PlanCoupledLine>().Select(c => c.S).DefaultIfEmpty().Max();
            bool exact = !list.Any(x => x.S is PlanGap or PlanCurve) && list.Select(x => WidthOf(x.S)).Distinct().Count() == 1;
            foreach (var (p, _) in list)
                foreach (var (q, _) in list)
                {
                    double d = Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
                    Assert.True(exact ? d < 1e-12 : d <= reach, $"node {node}: {p} and {q} are {d} m apart");
                }
        }
        foreach (int pin in new[] { 1, 2 }) Assert.Single(at[pin]);
        var one = Assert.Single(at[1]);
        Assert.True(Math.Abs(one.P.X) < 1e-12 && Math.Abs(one.P.Y) < 1e-12, $"pin 1 at {one.P}");
    }

    /// <summary>The copper rectangles a section draws (an MMITER's as the square corner it is cut from), each with the
    /// node it is joined to.</summary>
    private static IEnumerable<(double X0, double Y0, double X1, double Y1, int Node)> Rectangles(PlanSection s)
    {
        IEnumerable<(double, double, double, double, int)> Local() => s switch
        {
            PlanLine l => [(0, -l.W / 2, l.L, l.W / 2, l.Nodes[0])],
            PlanCoupledLine c => [(0, -c.W / 2, c.L, c.W / 2, c.Nodes[0]), (0, -c.W * 1.5 - c.S, c.L, -c.W / 2 - c.S, c.Nodes[2])],
            PlanCoupledStrips m => [.. Offsets(m).Select((y, k) => (0.0, y - m.W[k] / 2, m.L, y + m.W[k] / 2, m.Nodes[2 * k]))],
            PlanTee t => [(0, -t.W1 / 2, t.L1 + t.L2, t.W1 / 2, t.Nodes[0]), (t.L1 - t.W3 / 2, -t.L3, t.L1 + t.W3 / 2, 0, t.Nodes[0])],
            PlanBend b => [(0, -b.W / 2, b.Arm + b.W / 2, b.W / 2, b.Nodes[0]),
                           (b.Arm - b.W / 2, 0, b.Arm + b.W / 2, Math.Sign(b.AngleDeg) * (b.Arm + 0.0), b.Nodes[0])],
            _ => [],
        };
        foreach (var (x0, y0, x1, y1, n) in Local())
        {
            var (ax, ay) = Posed(s, x0, y0);
            var (bx, by) = Posed(s, x1, y1);
            yield return (Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by), n);
        }
    }

    /// <summary>No piece of one copper island touches a piece of another: no fold runs into a neighbour, no resonator
    /// into the next. (Islands as the model joins them: a strip's ends, every pin of a line, a corner or a tee.)</summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void EachIsland_KeepsClearOfTheOthers(string values)
    {
        var plan = CoupledResonatorPlan.Build(Spec(values), Ro4350b);
        var root = Enumerable.Range(0, plan.NodeCount + 1).ToArray();
        int Find(int n) => root[n] == n ? n : root[n] = Find(root[n]);
        foreach (var s in plan.Sections)
        {
            var n = s.Nodes;
            IEnumerable<(int, int)> joins = s switch
            {
                PlanCoupledLine => [(n[0], n[1]), (n[2], n[3])],
                PlanCoupledStrips m => Enumerable.Range(0, m.W.Count).Select(k => (n[2 * k], n[2 * k + 1])),
                PlanGap or PlanOpen => [],
                _ => n.Skip(1).Select(x => (n[0], x)),
            };
            foreach (var (a, b) in joins) root[Find(a)] = Find(b);
        }
        var rects = plan.Sections.SelectMany(Rectangles).ToList();
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
            {
                var (a, b) = (rects[i], rects[j]);
                if (Find(a.Node) == Find(b.Node)) continue;
                bool touch = a.X0 < b.X1 + 1e-10 && b.X0 < a.X1 + 1e-10 && a.Y0 < b.Y1 + 1e-10 && b.Y0 < a.Y1 + 1e-10;
                Assert.False(touch, $"{a} and {b} touch");
            }
    }

    /// <summary>Every open end, and a loop's two ends at its opening, comes first of the internal nodes, so the local block
    /// eliminates it last.</summary>
    [Theory]
    [MemberData(nameof(Samples))]
    public void TheOpenEnds_AreNumberedFirst(string values)
    {
        var plan = CoupledResonatorPlan.Build(Spec(values), Ro4350b);
        var ends = plan.Sections.OfType<PlanOpen>().SelectMany(o => o.Nodes)
            .Concat(plan.Sections.OfType<PlanGap>().Where(_ => plan.Sections.OfType<PlanCurve>().Any() is false)
                .SelectMany(g => g.Nodes).Where(n => n > 2))
            .Distinct().Order().ToList();
        Assert.Equal(Enumerable.Range(3, ends.Count), ends);
    }

    [Theory]
    [InlineData("n-invalid", "N=2.5;S1=1e-4;S2=1e-4;S3=1e-4")]
    [InlineData("n-invalid", "N=0")]
    [InlineData("spec-not-positive", $"{Gaps};W2=-1e-3")]
    [InlineData("gap-missing", "S1=1e-4;S2=1e-4;S3=1e-4")]
    [InlineData("name-unknown", $"{Gaps};Shape=Spiral")]
    [InlineData("form-shape", $"{Gaps};Form=Bandstop")]
    [InlineData("form-shape", "N=3;Form=Spurline;Shape=Hairpin;S1=1e-4;S2=1e-4;S3=1e-4")]
    [InlineData("form-feed", "N=3;Form=Bandstop;Shape=ZigZag;Feed=Tapped;S1=1e-4;S2=1e-4;S3=1e-4")]
    [InlineData("profile-shape", $"{Gaps};Shape=OpenLoop;Profile=SIR")]
    [InlineData("ring", $"{Gaps};Shape=Ring")]
    [InlineData("cross-n", $"{Gaps};Shape=OpenLoop;Arrangement=Cross")]
    [InlineData("sir-ratio", $"{Gaps};Profile=SIR;Uc=1.2")]
    [InlineData("sir-centre", $"{Gaps};Shape=Hairpin;Profile=SIR;Uc=0.05")]
    [InlineData("unequal-pair", $"{Gaps};W2=0.8e-3")]
    [InlineData("tap-off-line", $"{Gaps};Feed=Tapped;Tap=0.1e-3")]
    [InlineData("tap-off-line", $"{Gaps};Shape=OpenLoop;Feed=Tapped;Tap=4.8e-3")]   // on a corner
    [InlineData("too-short", $"{Gaps};Shape=Hairpin;Sa=0.1e-3")]
    [InlineData("too-short", $"{Gaps};Shape=OpenLoop;Gap=12e-3")]
    public void WhatCannotBeBuilt_IsRefused(string code, string values)
    {
        var ex = Assert.Throws<ParameterRefusalException>(() => CoupledResonatorPlan.Build(Spec(values), Ro4350b));
        Assert.Equal("device.mcrc." + code, ex.Diagnostic!.Id);
    }

    /// <summary>A longer neighbour couples over the shorter one's end section; the rest of its end runs on as a plain line.</summary>
    [Fact]
    public void ALongerNeighbour_CouplesOverTheShorter()
    {
        var plan = CoupledResonatorPlan.Build(Spec($"{Gaps};L2=38e-3"), Ro4350b);
        var pairs = plan.Sections.OfType<PlanCoupledLine>().Select(c => c.L).ToList();
        Assert.Equal(PassivePlan.OnGrid(37.17e-3 / 2.0), pairs[0], 12);
        Assert.Equal(PassivePlan.OnGrid(37.17e-3 / 2.0), pairs[1], 12);
        Assert.Equal(2, plan.Sections.OfType<PlanLine>().Count(l => Math.Abs(l.L - PassivePlan.OnGrid((38e-3 - 37.17e-3) / 2.0)) < 2e-9));
    }

    /// <summary>Coupling = Full: an aligned hairpin filter's arms, the feeds' strips included, are one N-line section
    /// over the arms' length, every arm beside the next; a staggered stepped-impedance one is one section of every wide
    /// end and one of each row's narrow arms. Pairs places only MCLIN pairs.</summary>
    [Fact]
    public void FullCoupling_PutsAHairpinsArmsInOneSection()
    {
        var pairs = CoupledResonatorPlan.Build(Spec($"{Gaps};Shape=Hairpin;Sa=2.2e-3"), Ro4350b);
        Assert.Empty(pairs.Sections.OfType<PlanCoupledStrips>());
        Assert.Equal(4, pairs.Sections.OfType<PlanCoupledLine>().Count());

        var full = CoupledResonatorPlan.Build(Spec($"{Gaps};Shape=Hairpin;Sa=2.2e-3;Coupling=Full"), Ro4350b);
        Assert.Empty(full.Sections.OfType<PlanCoupledLine>());
        var arms = Assert.Single(full.Sections.OfType<PlanCoupledStrips>());
        Assert.Equal(8, arms.W.Count);
        // Feed, then each hairpin's two arms Sa apart, the next hairpin S on.
        Assert.Equal([0.126e-3, 2.2e-3, 0.574e-3, 2.2e-3, 0.574e-3, 2.2e-3, 0.126e-3], arms.S.Select(x => Math.Round(x, 9)));

        var sir = CoupledResonatorPlan.Build(Spec($"{Gaps};Shape=Hairpin;Sa=2.2e-3;Profile=SIR;Wc=0.5e-3;Uc=0.4;Coupling=Full"), Ro4350b);
        var sections = sir.Sections.OfType<PlanCoupledStrips>().ToList();
        Assert.Contains(sections, m => m.W.Count == 8 && m.W.All(w => Math.Abs(w - 1.058e-3) < 1e-12));
        Assert.Contains(sections, m => m.W.Count == 4 && m.W.All(w => Math.Abs(w - 0.5e-3) < 1e-12));   // ∪ hairpins 1 and 3
        Assert.Contains(sections, m => m.W.Count == 2 && m.W.All(w => Math.Abs(w - 0.5e-3) < 1e-12));   // ∩ hairpin 2
    }

    /// <summary>Neighbours of unequal width: refused as an MCLIN pair, built under Full as an N-line pair of two widths.</summary>
    [Fact]
    public void AnUnequalSirPair_BuildsUnderFullCoupling()
    {
        const string values = $"{Gaps};Profile=SIR;Wc=0.5e-3;Uc=0.3;W2=0.8e-3";
        var ex = Assert.Throws<ParameterRefusalException>(() => CoupledResonatorPlan.Build(Spec(values), Ro4350b));
        Assert.Equal("device.mcrc.unequal-pair", ex.Diagnostic!.Id);
        var plan = CoupledResonatorPlan.Build(Spec(values + ";Coupling=Full"), Ro4350b);
        var widths = plan.Sections.OfType<PlanCoupledStrips>().Select(m => (m.W[0], m.W[1])).ToList();
        Assert.Equal(4, widths.Count);
        double w = PassivePlan.OnGrid(1.058e-3), w2 = PassivePlan.OnGrid(0.8e-3);
        Assert.Contains((w, w2), widths);
        Assert.Contains((w2, w), widths);
    }
}
