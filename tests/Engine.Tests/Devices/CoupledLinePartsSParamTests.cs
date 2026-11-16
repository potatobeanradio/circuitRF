using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// The coupled-line planned passives through the engine: MCLC (coupled-line coupler), MIDC (four-finger interdigitated
/// coupler) and MCBL (coupled-line balun) are their plans' sections and nothing else, so each matches the same network
/// wired by hand, and at F each does what it was asked: the coupling, the quadrature split, the balanced outputs.
/// </summary>
public class CoupledLinePartsSParamTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const double F0 = 2.4e9;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);
    private static double Deg(Complex x) => x.Phase * 180.0 / Math.PI;
    private static double Wrap(double deg) => deg - 360.0 * Math.Round(deg / 360.0);

    private static string Ports(int n) => string.Join("\n",
        Enumerable.Range(1, n).Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm"));

    private static Complex[,] SAt(string cnl, double freqHz, int n)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        var m = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static void AssertSame(Complex[,] a, Complex[,] b, double f)
    {
        for (int i = 0; i < a.GetLength(0); i++)
            for (int j = 0; j < a.GetLength(1); j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    /// <summary>A plan's MCLIN and MLIN sections as explicit instances; <paramref name="node"/> names plan nodes.</summary>
    private static string ByHand(PassivePlan plan, Func<int, string> node, string sub)
    {
        var b = new StringBuilder();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(node));
            b.AppendLine(section switch
            {
                PlanCoupledLine { Fingers: 2 } c => $"MCLIN:K{++k} {nodes} W={F(c.W)} S={F(c.S)} L={F(c.L)} {sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {sub}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return b.ToString();
    }

    // ── MCLC ────────────────────────────────────────────────────────────────────────────────────────────

    private static string Coupler(double cDb) => $"""
        {Ports(4)}
        MCLC:C1 p1 p2 p3 p4 Coupling={F(cDb)} dB Z0=50 Ohm F=2.4 GHz {Sub}
        """;

    [Theory]
    [InlineData(1.2e9)]
    [InlineData(2.4e9)]
    [InlineData(4.8e9)]
    public void TheCoupler_IsItsMclinWiredByHand(double f)
    {
        var plan = CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b).Plan;
        AssertSame(SAt(Coupler(10.0), f, 4), SAt(Ports(4) + "\n" + ByHand(plan, n => $"p{n}", Sub), f, 4), f);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(15.0)]
    [InlineData(20.0)]
    public void AtF_TheCouplerCouplesWhatWasAsked_AndIsMatched(double cDb)
    {
        var s = SAt(Coupler(cDb), F0, 4);
        output.WriteLine($"{cDb} dB: S11 {Db(s[0, 0]):F2}  S21 {Db(s[1, 0]):F2}  S31 {Db(s[2, 0]):F2}  S41 {Db(s[3, 0]):F2} dB, "
                       + $"directivity {Db(s[2, 0]) - Db(s[3, 0]):F1} dB, S31-S21 {Wrap(Deg(s[2, 0]) - Deg(s[1, 0])):F1} deg");
        Assert.InRange(Db(s[2, 0]), -cDb - 0.3, -cDb + 0.3);
        Assert.True(Db(s[0, 0]) < -20.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.True(Db(s[3, 0]) < Db(s[2, 0]), "the isolated port gets less than the coupled one");
        Assert.InRange(Math.Abs(Wrap(Deg(s[2, 0]) - Deg(s[1, 0]))), 85.0, 95.0);
    }

    // ── MIDC ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Jumpers: pin k to Jk, a Short each (an ideal wire).</summary>
    private const string Jumpers = """
        Short:S1 p1 j1
        Short:S2 p2 j2
        Short:S3 p3 j3
        Short:S4 p4 j4
        """;

    private static string Lange(double cDb, string sub = Sub) => $"""
        {Ports(4)}
        MIDC:X1 p1 p2 p3 p4 j1 j2 j3 j4 Coupling={F(cDb)} dB Z0=50 Ohm F=2.4 GHz {sub}
        {Jumpers}
        """;

    /// <summary>
    /// The four fingers by hand, on a lossless board at F: the outer two (fingers 1 and 4) a CLIN of the pair's own modes,
    /// the inner two (3 and 2) a CLIN of an inner finger's share, each mode its pair's electrical length — the split Ou's
    /// equal-potential assumption gives, and that the jumpers then join.
    /// </summary>
    [Fact]
    public void TheLange_IsTwoCoupledPairsOfOusSharesJoinedByItsJumpers()
    {
        const string lossless = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e300 TanD=0";
        var board = Ro4350b with { SigmaSPerM = 1e300, TanD = 0.0 };
        var d = LangePlan.Build(4.0, 50.0, F0, board);
        var m = d.Modes;
        double ye = 1 / m.Ze, yo = 1 / m.Zo, yIn = ye * ye / (ye + (yo - ye) / 2);
        double k0 = 2 * Math.PI * F0 / 2.99792458e8;
        string E = $"E={F(k0 * Math.Sqrt(m.EeffE) * d.LengthMeters)} Eo={F(k0 * Math.Sqrt(m.EeffO) * d.LengthMeters)} F=2.4 GHz";
        string byHand = $"""
            {Ports(4)}
            CLIN:K1 p1 p2 p3 p4 Ze={F(1 / ye)} Zo={F(1 / yo)} {E}
            CLIN:K2 j1 j2 j3 j4 Ze={F(1 / yIn)} Zo={F(1 / (yIn + 2 * (yo - ye)))} {E}
            {Jumpers}
            """;
        AssertSame(SAt(Lange(4.0, lossless), F0, 4), SAt(byHand, F0, 4), F0);
    }

    /// <summary>With ideal jumpers the four fingers are Ou's two-conductor coupler exactly: one CLIN of Ze,N and Zo,N.</summary>
    [Fact]
    public void WithIdealJumpers_TheLangeIsOusEquivalentPair()
    {
        const string lossless = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e300 TanD=0";
        var d = LangePlan.Build(4.0, 50.0, F0, Ro4350b with { SigmaSPerM = 1e300, TanD = 0.0 });
        double k0 = 2 * Math.PI * F0 / 2.99792458e8;
        string pair = $"""
            {Ports(4)}
            CLIN:K1 p1 p2 p3 p4 Ze={F(d.Ze)} Zo={F(d.Zo)} E={F(k0 * Math.Sqrt(d.Modes.EeffE) * d.LengthMeters)} Eo={F(k0 * Math.Sqrt(d.Modes.EeffO) * d.LengthMeters)} F=2.4 GHz
            """;
        AssertSame(SAt(Lange(4.0, lossless), F0, 4), SAt(pair, F0, 4), F0);
    }

    /// <summary>4 dB, the tightest four-finger coupling a 100 µm gap builds on this board: at F it splits into the
    /// through and coupled ports in quadrature, matched, with the isolated port below both.</summary>
    [Fact]
    public void AtF_TheLangeSplitsInQuadrature()
    {
        var s = SAt(Lange(4.0), F0, 4);
        double quad = Wrap(Deg(s[2, 0]) - Deg(s[1, 0]));
        output.WriteLine($"S11 {Db(s[0, 0]):F2}  S21 {Db(s[1, 0]):F2}  S31 {Db(s[2, 0]):F2}  S41 {Db(s[3, 0]):F2} dB, "
                       + $"S31-S21 {quad:F2} deg");
        Assert.InRange(Db(s[2, 0]), -4.3, -3.7);
        double through = 10 * Math.Log10(1 - Math.Pow(10, -0.4));     // the rest of the power, lossless
        Assert.InRange(Db(s[1, 0]), through - 0.3, through + 0.1);
        Assert.True(Db(s[0, 0]) < -20.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.True(Db(s[3, 0]) < -15.0, $"|S41| {Db(s[3, 0]):F2} dB");
        Assert.InRange(Math.Abs(quad), 87.0, 93.0);
    }

    /// <summary>A missing jumper is a different circuit, not a singular one: the inner fingers then float as their own
    /// pair and the coupling falls.</summary>
    [Fact]
    public void WithoutJumpers_TheLangeCouplesLess()
    {
        string open = Lange(4.0).Replace(Jumpers, "");
        var s = SAt(open, F0, 4);
        Assert.True(Db(s[2, 0]) < -6.0, $"|S31| {Db(s[2, 0]):F2} dB");
    }

    // ── MCBL ────────────────────────────────────────────────────────────────────────────────────────────

    private static string Ports3(double zl) => $"""
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z={F(zl)} Ohm
        Port:P3 p3 0 Num=3 Z={F(zl)} Ohm
        """;

    private static string Balun(double zl) => $"""
        {Ports3(zl)}
        MCBL:B1 p1 p2 p3 0 0 Z0=50 Ohm ZL={F(zl)} Ohm F=2.4 GHz Gap=1 mm {Sub}
        """;

    [Theory]
    [InlineData(1.2e9)]
    [InlineData(2.4e9)]
    [InlineData(4.8e9)]
    public void TheBalun_IsItsSectionsWiredByHand(double f)
    {
        var plan = MarchandBalunPlan.Build(50.0, 200.0, F0, 1e-3, Ro4350b).Plan;
        string Node(int n) => n switch { <= 3 => $"p{n}", 4 or 5 => "0", _ => $"i{n}" };
        AssertSame(SAt(Balun(200.0), f, 3), SAt(Ports3(200.0) + "\n" + ByHand(plan, Node, Sub), f, 3), f);
    }

    /// <summary>The design equations, with the medium taken out: the plan's Ze and Zo as TEM pairs a quarter wave long
    /// at F match the input exactly and give equal and opposite outputs.</summary>
    [Fact]
    public void AsTemPairs_ThePlansModesMatchAndBalanceExactly()
    {
        var d = MarchandBalunPlan.Build(50.0, 200.0, F0, 1e-3, Ro4350b);
        string pair = $"Ze={F(d.Ze)} Zo={F(d.Zo)} E=90 deg F=2.4 GHz";
        var s = SAt($"""
            {Ports3(200.0)}
            CLIN:K1 p1 m 0 p2 {pair}
            CLIN:K2 m o p3 0 {pair}
            """, F0, 3);
        Assert.True(Db(s[0, 0]) < -60.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.True((s[1, 0] + s[2, 0]).Magnitude < 1e-6, $"S21 {s[1, 0]} vs S31 {s[2, 0]}");
        Assert.Equal(-10 * Math.Log10(2), Db(s[1, 0]), 6);
    }

    /// <summary>200 Ω on each output, the lightest load whose coupling a 100 µm gap builds on this board. At F the two
    /// outputs are equal in amplitude and the input matched; the phase is 180° less microstrip's limit, about 16° here:
    /// the even mode (εeff 2.97) is slower than the odd (2.30), so neither section is a quarter wave for both.</summary>
    [Fact]
    public void AtF_TheBalunOutputsAreBalanced_AndTheInputMatched()
    {
        var s = SAt(Balun(200.0), F0, 3);
        double amp = Db(s[1, 0]) - Db(s[2, 0]), phase = Wrap(Deg(s[1, 0]) - Deg(s[2, 0]));
        output.WriteLine($"S11 {Db(s[0, 0]):F2}  S21 {Db(s[1, 0]):F2}  S31 {Db(s[2, 0]):F2} dB, "
                       + $"balance {amp:F3} dB, {phase:F2} deg");
        Assert.True(Math.Abs(amp) < 0.3, $"amplitude balance {amp:F3} dB");
        Assert.InRange(Math.Abs(phase), 160.0, 180.0);
        Assert.True(Db(s[0, 0]) < -15.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.InRange(Db(s[1, 0]), -3.6, -3.0);
    }

    // ── DC ──────────────────────────────────────────────────────────────────────────────────────────────

    private static Func<string, double> Dc(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl + "\nanalysis DC1 type=dc\n");
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);
        return n => dc.NodeVoltages[nl.Nodes.GetOrAssign(n) - 1];
    }

    /// <summary>At DC each strip is copper end to end and the strips are apart: 1 V through 50 Ω reaches the through
    /// port's 50 Ω load, and nothing the coupled side.</summary>
    [Fact]
    public void AtDc_TheCouplersStripsAreTwoNodes()
    {
        var v = Dc($"""
            Vdc:V1 a 0 Vdc=1 V
            R:Rs a p1 R=50 Ohm
            MCLC:C1 p1 p2 p3 p4 Coupling=10 dB Z0=50 Ohm F=2.4 GHz {Sub}
            R:L2 p2 0 R=50 Ohm
            R:L3 p3 0 R=50 Ohm
            R:L4 p4 0 R=50 Ohm
            """);
        Assert.Equal(0.5, v("p1"), 9);
        Assert.Equal(0.5, v("p2"), 9);
        Assert.Equal(0.0, v("p3"), 9);
        Assert.Equal(0.0, v("p4"), 9);
    }

    /// <summary>With its jumpers each conductor is one node: the source reaches the through load and the jumper
    /// landings alike, and the other conductor sits at its loads' 0 V. The jumpers are wires of 1 mΩ here: ideal
    /// Shorts would close a loop of DC shorts with the fingers, which the DC solve cannot (a loop of ideal lines is
    /// the same).</summary>
    [Fact]
    public void AtDc_TheLangesJumperedConductorsAreTwoNodes()
    {
        var v = Dc($"""
            Vdc:V1 a 0 Vdc=1 V
            R:Rs a p1 R=50 Ohm
            MIDC:X1 p1 p2 p3 p4 j1 j2 j3 j4 Coupling=4 dB Z0=50 Ohm F=2.4 GHz {Sub}
            R:S1 p1 j1 R=1 mOhm
            R:S2 p2 j2 R=1 mOhm
            R:S3 p3 j3 R=1 mOhm
            R:S4 p4 j4 R=1 mOhm
            R:L2 p2 0 R=50 Ohm
            R:L3 p3 0 R=50 Ohm
            R:L4 p4 0 R=50 Ohm
            """);
        foreach (string n in new[] { "p1", "p2", "j1", "j2" }) Assert.Equal(0.5, v(n), 4);
        foreach (string n in new[] { "p3", "p4", "j3", "j4" }) Assert.Equal(0.0, v(n), 9);
    }

    /// <summary>The balun's input strip ends open, so its input carries the source's full volt, while each output sits
    /// on its strip's ground.</summary>
    [Fact]
    public void AtDc_TheBalunsInputIsOpen_AndItsOutputsGrounded()
    {
        var v = Dc($"""
            Vdc:V1 a 0 Vdc=1 V
            R:Rs a p1 R=50 Ohm
            MCBL:B1 p1 p2 p3 g2 g3 Z0=50 Ohm ZL=200 Ohm F=2.4 GHz Gap=1 mm {Sub}
            R:G2 g2 0 R=1 mOhm
            R:G3 g3 0 R=1 mOhm
            R:L2 p2 a R=200 Ohm
            R:L3 p3 a R=200 Ohm
            """);
        Assert.Equal(1.0, v("p1"), 9);
        Assert.Equal(v("g2"), v("p2"), 12);
        Assert.Equal(v("g3"), v("p3"), 12);
        Assert.InRange(v("p2"), 1e-6, 1e-5);       // 1 V across 200 Ω into 1 mΩ
    }
}
