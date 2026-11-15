using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MWPD, the equal-split Wilkinson divider, through the engine. It is its plan's MTEE and MCURVE sections and
/// nothing else, so it matches the same parts wired by hand; with 2·Z0 across R2-R3 it divides at F.
/// </summary>
public class WilkinsonSParamTests
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const string Ports = """
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z=50 Ohm
        Port:P3 p3 0 Num=3 Z=50 Ohm
        """;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static Complex[,] SAt(string cnl, double freqHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        var m = new Complex[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static string Divider(string resistor) => $"""
        {Ports}
        MWPD:W1 p1 p2 p3 r2 r3 Z0=50 Ohm F=2.4 GHz Gap=1.35 mm {Sub}
        {resistor}
        """;

    /// <summary>The plan's own sections as explicit MTEE and MCURVE instances.</summary>
    private static string ByHand(string resistor) => ByHand(WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b).Plan, resistor);

    /// <summary>The net each pin of a plan is wired to: p1-p3 for the ports, then r2, r3, r2_2, r3_2 …</summary>
    private static string Net(PassivePlan plan, int node)
        => node <= 3 ? $"p{node}" : node <= plan.Pins.Count ? plan.Pins[node - 1].ToLowerInvariant() : $"i{node}";

    private static string ByHand(PassivePlan plan, string resistor)
    {
        string Node(int n) => Net(plan, n);
        var b = new StringBuilder(Ports).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanCurve c => $"MCURVE:C{++k} {nodes} W={F(c.W)} Angle={F(c.AngleDeg)} deg Radius={F(c.Radius)} {Sub}",
                _ => throw new InvalidOperationException(),
            });
        }
        return b.AppendLine(resistor).ToString();
    }

    [Theory]
    [InlineData(1.2e9)]
    [InlineData(2.4e9)]
    [InlineData(4.8e9)]
    public void TheDivider_IsItsSectionsWiredByHand(double f)
    {
        const string r = "R:R1 r2 r3 R=100 Ohm";
        var a = SAt(Divider(r), f);
        var b = SAt(ByHand(r), f);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    [Fact]
    public void WithTwiceZ0AcrossTheGap_ItDividesAtF()
    {
        var s = SAt(Divider("R:R1 r2 r3 R=100 Ohm"), 2.4e9);
        static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);

        Assert.True(Db(s[0, 0]) < -20.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.True(Db(s[1, 2]) < -20.0, $"|S23| {Db(s[1, 2]):F2} dB");
        Assert.InRange(Db(s[1, 0]), -3.3, -3.0);
        Assert.InRange(Db(s[2, 0]), -3.3, -3.0);
        Assert.True(Math.Abs(Db(s[1, 0]) - Db(s[2, 0])) < 1e-9, "the split is equal");
    }

    // ══ The variants: an unequal split (K) and the multi-section divider (N) ══════════════════════════

    private static string Variant(double k, int n, double band = 2.0)
    {
        var d = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, k, n, band);
        string pins = string.Join(" ", Enumerable.Range(1, d.Plan.Pins.Count).Select(i => Net(d.Plan, i)));
        return $"""
            {Ports}
            MWPD:W1 {pins} Z0=50 Ohm F=2.4 GHz Gap=1.35 mm K={F(k)} N={n} Band={F(band)} {Sub}
            {Resistors(d)}
            """;
    }

    private static string Resistors(WilkinsonDesign d)
        => string.Join("\n", d.Resistors.Select((r, i) =>
            $"R:R{i + 1} {r.PinA.ToLowerInvariant()} {r.PinB.ToLowerInvariant()} R={F(r.Ohms)} Ohm"));

    [Theory]
    [InlineData(2.0, 1, 1.2e9)]
    [InlineData(2.0, 1, 2.4e9)]
    [InlineData(0.6, 1, 4.8e9)]
    [InlineData(1.0, 2, 1.2e9)]
    [InlineData(1.0, 2, 2.4e9)]
    [InlineData(1.0, 3, 2.4e9)]
    [InlineData(1.0, 3, 4.8e9)]
    public void AVariant_IsItsSectionsWiredByHand(double k, int n, double f)
    {
        var d = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b, k, n, 2.0);
        var a = SAt(Variant(k, n), f);
        var b = SAt(ByHand(d.Plan, Resistors(d)), f);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);

    /// <summary>At F the unequal split puts K² more power into port 3 than port 2, with every port matched and the
    /// outputs isolated.</summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void AnUnequalSplit_DividesAsKSquaredAtF(double k)
    {
        var s = SAt(Variant(k, 1), 2.4e9);
        double ratioDb = Db(s[2, 0]) - Db(s[1, 0]);

        Assert.True(Math.Abs(ratioDb - 20.0 * Math.Log10(k)) < 0.3, $"P3/P2 {ratioDb:F2} dB, K² {20 * Math.Log10(k):F2} dB");
        foreach (var (i, j, name) in new[] { (0, 0, "S11"), (1, 1, "S22"), (2, 2, "S33"), (1, 2, "S23") })
            Assert.True(Db(s[i, j]) < -20.0, $"|{name}| {Db(s[i, j]):F2} dB");
        // Lossy lines lose a little: the two outputs together carry within 0.3 dB of the input's power.
        double total = s[1, 0].Magnitude * s[1, 0].Magnitude + s[2, 0].Magnitude * s[2, 0].Magnitude;
        Assert.InRange(10.0 * Math.Log10(total), -0.3, 0.0);
    }

    /// <summary>The fraction of 0.5 to 5 GHz, in 10 MHz steps, over which |S(i,j)| stays under −20 dB, from F out each
    /// way: the band, as f_high/f_low.</summary>
    private static double BandRatio(string cnl, int i, int j)
    {
        double[] f = [.. Enumerable.Range(0, 451).Select(k => 0.5e9 + 1e7 * k)];
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), f)["S"];
        bool Under(int k) => Db((Complex)s[k, i, j]) < -20.0;
        int centre = Array.FindIndex(f, x => x >= 2.4e9), lo = centre, hi = centre;
        Assert.True(Under(centre), "not under -20 dB at F");
        while (lo > 0 && Under(lo - 1)) lo--;
        while (hi < f.Length - 1 && Under(hi + 1)) hi++;
        return f[hi] / f[lo];
    }

    /// <summary>
    /// The point of the sections: the band over which the input stays matched (|S11| < −20 dB) and the outputs isolated
    /// (|S23| < −20 dB) widens with N, on this board's real lines and junctions. The ideal octave design matches the
    /// input everywhere in its band; here the junctions narrow it, and the test asks only that each section buys more.
    /// </summary>
    [Fact]
    public void MoreSections_WidenTheBand()
    {
        double s11One = BandRatio(Divider("R:R1 r2 r3 R=100 Ohm"), 0, 0), s23One = BandRatio(Divider("R:R1 r2 r3 R=100 Ohm"), 1, 2);
        double s11Two = BandRatio(Variant(1.0, 2), 0, 0), s23Two = BandRatio(Variant(1.0, 2), 1, 2);
        double s11Three = BandRatio(Variant(1.0, 3), 0, 0), s23Three = BandRatio(Variant(1.0, 3), 1, 2);

        Assert.True(s11Two > 1.5 * s11One && s11Three > s11Two, $"|S11| bands {s11One:F2}, {s11Two:F2}, {s11Three:F2}");
        Assert.True(s23Two > 1.5 * s23One && s23Three > s23Two, $"|S23| bands {s23One:F2}, {s23Two:F2}, {s23Three:F2}");
    }

    /// <summary>
    /// The sections' numbers on ideal lines, where there is no junction or dispersion to blur them: at each frequency where the
    /// even mode's transformer is matched, the odd mode is too, so every port is matched and the outputs isolated —
    /// to rounding.
    /// </summary>
    [Theory]
    [InlineData(2, 2.0)]
    [InlineData(2, 3.0)]
    [InlineData(3, 2.0)]
    [InlineData(3, 4.0)]
    public void OnIdealLines_TheSectionsAreMatchedAndIsolatedAtTheTransformersZeros(int n, double band)
    {
        var (z, r) = WilkinsonPlan.BroadbandSections(n, band);
        double theta1 = Math.Acos(Math.Cos(Math.PI / (1 + band)) * (n == 2 ? Math.Sqrt(0.5) : Math.Sqrt(3.0) / 2.0));
        // Junction k (from the outputs) is a/b k; junction 0 is the input.
        var b = new StringBuilder(Ports).AppendLine();
        string A(int k) => k == 1 ? "p2" : k > n ? "p1" : $"a{k}";
        string B(int k) => k == 1 ? "p3" : k > n ? "p1" : $"b{k}";
        for (int k = 1; k <= n; k++)
        {
            b.AppendLine($"TLIN:TA{k} {A(k + 1)} {A(k)} Z={F(50 * z[k - 1])} Ohm E=90 deg F=1 GHz");
            b.AppendLine($"TLIN:TB{k} {B(k + 1)} {B(k)} Z={F(50 * z[k - 1])} Ohm E=90 deg F=1 GHz");
            b.AppendLine($"R:R{k} {A(k)} {B(k)} R={F(50 * r[k - 1])} Ohm");
        }
        foreach (double theta in n == 2 ? new[] { theta1, Math.PI - theta1 } : [theta1, Math.PI / 2, Math.PI - theta1])
        {
            var s = SAt(b.ToString(), 1e9 * theta / (Math.PI / 2));
            foreach (var (i, j) in new[] { (0, 0), (1, 1), (2, 2), (1, 2) })
                Assert.True(s[i, j].Magnitude < 1e-9, $"S{i + 1}{j + 1} at θ = {theta * 180 / Math.PI:F2}°: {s[i, j].Magnitude}");
        }
    }

    /// <summary>At DC the ring is copper: 1 V through 50 Ω reaches every pin alike, and the resistor carries nothing.</summary>
    [Fact]
    public void AtDc_EveryPinIsOneNode()
    {
        var (lib, tb) = new CnlReader().Read($"""
            Vdc:V1 a 0 Vdc=1 V
            R:Rs a p1 R=50 Ohm
            MWPD:W1 p1 p2 p3 r2 r3 Z0=50 Ohm F=2.4 GHz Gap=1.35 mm {Sub}
            R:R1 r2 r3 R=100 Ohm
            R:L2 p2 0 R=50 Ohm
            R:L3 p3 0 R=50 Ohm
            analysis DC1 type=dc
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);
        double V(string n) => dc.NodeVoltages[nl.Nodes.GetOrAssign(n) - 1];
        foreach (string n in new[] { "p2", "p3", "r2", "r3" }) Assert.Equal(V("p1"), V(n), 9);
        Assert.Equal(1.0 / 3.0, V("p1"), 9);     // 50 Ω into the two loads in parallel, 25 Ω
    }
}
