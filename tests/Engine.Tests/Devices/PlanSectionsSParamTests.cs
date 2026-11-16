using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The gap, taper and open-end sections through the engine. Each, inside a small plan, is the same parts wired by hand
/// as ordinary instances — MGAP, MTAPER, and an open end's capacitance as a C — and the open end lowers a quarter-wave
/// stub's resonance by the end extension its closed form predicts.
/// </summary>
public class PlanSectionsSParamTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const string Ports = """
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z=50 Ohm
        """;
    private const double W = 1.1e-3, Arm = 2e-3, Stub = 17e-3;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static PassivePlan Plan(string kind) => kind switch
    {
        "Gap" => new(["1", "2"],
        [
            new PlanLine([1, 3], 0.0, 0.0, 0.0, 1e-3, 5e-3),
            new PlanGap([3, 4], 5e-3, 0.0, 0.0, 1e-3, 0.2e-3),
            new PlanLine([4, 2], 5.2e-3, 0.0, 0.0, 1e-3, 5e-3),
        ], 4),
        "Taper" => new(["1", "2"],
        [
            new PlanLine([1, 3], 0.0, 0.0, 0.0, W, 4e-3),
            new PlanTaper([3, 4], 4e-3, 0.0, 0.0, W, 0.4e-3, 6e-3),
            new PlanLine([4, 2], 10e-3, 0.0, 0.0, 0.4e-3, 4e-3),
        ], 4),
        "Stub" => Stub_(Stub, open: true),
        // A coupled pair, its far ends open: pins 1 and 2 are the two strips' near ends.
        _ => new(["1", "2"],
        [
            new PlanCoupledLine([1, 3, 2, 4], 0.0, 0.0, 0.0, 1e-3, 0.2e-3, 10e-3),
            new PlanOpen([3, 4], 10e-3, 0.0, 0.0, 1e-3, 0.2e-3),
        ], 4),
    };

    /// <summary>A shunt stub of length <paramref name="l"/> past a tee's branch arm: node 3 its far end, numbered first
    /// so it is eliminated last, node 4 the branch's end.</summary>
    private static PassivePlan Stub_(double l, bool open)
    {
        var sections = new List<PlanSection>
        {
            new PlanTee(W, W, W, Arm, Arm, Arm, [1, 2, 4], 0.0, 0.0, 0.0),
            new PlanLine([4, 3], Arm, -Arm, 270.0, W, l),
        };
        if (open) sections.Add(new PlanOpen([3], Arm, -Arm - l, 270.0, W));
        return new PassivePlan(["1", "2"], sections, 4);
    }

    /// <summary>The plan as one instance: a two-pin placeholder whose model is the plan's.</summary>
    private static ElaboratedNetlist AsPart(PassivePlan plan)
    {
        var nl = Elaborate($"{Ports}\nMLIN:X1 p1 p2 W=1e-3 L=1e-3 {Sub}");
        int at = nl.Components.FindIndex(c => c.InstancePath == "X1");
        var c = nl.Components[at];
        nl.Components[at] = new ElaboratedComponent(c.ComponentType, c.InstancePath, c.Nodes, c.Parameters,
            new MicrostripPlanModel(plan, Ro4350b, c.InstancePath));
        return nl;
    }

    /// <summary>The plan's own sections as ordinary instances at <paramref name="f"/>; an open end is its capacitance
    /// there, from the closed form on the line's own static and dispersive values.</summary>
    private static ElaboratedNetlist ByHand(PassivePlan plan, double f)
    {
        string Node(int n) => n <= 2 ? $"p{n}" : $"i{n}";
        var b = new StringBuilder(Ports).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanCoupledLine c => $"MCLIN:M{++k} {nodes} W={F(c.W)} S={F(c.S)} L={F(c.L)} {Sub}",
                PlanGap g => $"MGAP:G{++k} {nodes} W={F(g.W)} S={F(g.S)} {Sub}",
                PlanTaper t => $"MTAPER:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} L={F(t.L)} {Sub}",
                PlanOpen { S: null } o => $"C:C{++k} {nodes} 0 C={F(SingleEnd(o.W, f))}",
                PlanOpen o => PairEnds(o, f, ++k),
                _ => throw new InvalidOperationException(),
            });
        }
        return Elaborate(b.ToString());
    }

    private static MicrostripLineModel Line(double w)
        => new(w, 0.0, Ro4350b.HMeters, Ro4350b.TMeters, Ro4350b.EpsR, Ro4350b.SigmaSPerM, Ro4350b.TanD, "test");

    /// <summary>The closed form's end extension of a line of width <paramref name="w"/>, at its static εeff.</summary>
    private static double Extension(double w)
        => MicrostripOpenEnd.Extension(w, Ro4350b.HMeters, Ro4350b.EpsR, Line(w).LineParameters(0.0).EeffStatic);

    private static double SingleEnd(double w, double f)
    {
        var p = Line(w).LineParameters(f);
        return MicrostripOpenEnd.Capacitance(Extension(w), p.Eeff, p.Z0);
    }

    /// <summary>A pair's two ends: each mode's capacitance, Ce to ground at each end and (Co − Ce)/2 between them.</summary>
    private static string PairEnds(PlanOpen o, double f, int k)
    {
        var pair = new MicrostripCoupledLineModel(o.W, o.S!.Value, 0.0, Ro4350b.HMeters, Ro4350b.TMeters, Ro4350b.EpsR,
            Ro4350b.SigmaSPerM, Ro4350b.TanD, "test");
        var (st, m) = (pair.ModesAt(0.0), pair.ModesAt(f));
        double h = Ro4350b.HMeters, er = Ro4350b.EpsR;
        double ce = MicrostripOpenEnd.Capacitance(MicrostripOpenEnd.Extension(o.W, h, er, st.EeffE), m.EeffE, m.Ze);
        double co = MicrostripOpenEnd.Capacitance(MicrostripOpenEnd.Extension(o.W, h, er, st.EeffO), m.EeffO, m.Zo);
        string a = $"i{o.Nodes[0]}", b = $"i{o.Nodes[1]}";
        return $"C:Ca{k} {a} 0 C={F(ce)}\nC:Cb{k} {b} 0 C={F(ce)}\nC:Cm{k} {a} {b} C={F((co - ce) / 2.0)}";
    }

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static Complex[,] SAt(ElaboratedNetlist nl, double freqHz)
    {
        var s = SParameterEngine.Run(nl, [freqHz])["S"];
        var m = new Complex[2, 2];
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    [Theory]
    [InlineData("Gap", 1.2e9)]
    [InlineData("Gap", 4.8e9)]
    [InlineData("Taper", 1.2e9)]
    [InlineData("Taper", 4.8e9)]
    [InlineData("Stub", 1.2e9)]
    [InlineData("Stub", 4.8e9)]
    [InlineData("CoupledOpen", 1.2e9)]
    [InlineData("CoupledOpen", 4.8e9)]
    public void TheSection_IsItsPartWiredByHand(string kind, double f)
    {
        var plan = Plan(kind);
        var (a, b) = (SAt(AsPart(plan), f), SAt(ByHand(plan, f), f));
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    /// <summary>The open end is the line run on by Δl: the stub with it nulls the through line where the bare stub
    /// Δl longer does, and so lower than the bare stub of the same length.</summary>
    [Fact]
    public void TheOpenEnd_LowersAQuarterWaveStubByItsExtension()
    {
        double bare = Null(Stub_(Stub, open: false)), open = Null(Stub_(Stub, open: true));
        double longer = Null(Stub_(Stub + Extension(W), open: false));
        output.WriteLine($"Δl {Extension(W) * 1e3:F4} mm; nulls: bare {bare / 1e9:F5}, open {open / 1e9:F5}, Δl longer {longer / 1e9:F5} GHz");
        Assert.True(open < bare, $"open {open / 1e9} GHz, bare {bare / 1e9} GHz");
        Assert.InRange((bare - open) / (bare - longer), 0.99, 1.01);
    }

    /// <summary>The frequency of the through line's |S21| null, by golden section over the first quarter-wave band.</summary>
    private static double Null(PassivePlan plan)
    {
        var nl = AsPart(plan);
        double S21(double f) => SAt(nl, f)[1, 0].Magnitude;
        double lo = 1.6e9, hi = 3.4e9, r = (Math.Sqrt(5.0) - 1.0) / 2.0;
        double x1 = hi - r * (hi - lo), x2 = lo + r * (hi - lo), f1 = S21(x1), f2 = S21(x2);
        while (hi - lo > 1.0)
        {
            if (f1 < f2) { hi = x2; x2 = x1; f2 = f1; x1 = hi - r * (hi - lo); f1 = S21(x1); }
            else { lo = x1; x1 = x2; f1 = f2; x2 = lo + r * (hi - lo); f2 = S21(x2); }
        }
        return (lo + hi) / 2.0;
    }
}
