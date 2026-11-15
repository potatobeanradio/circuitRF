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
    private static string ByHand(string resistor)
    {
        var plan = WilkinsonPlan.Build(50.0, 2.4e9, 1.35e-3, Ro4350b).Plan;
        string Node(int n) => n <= 5 ? new[] { "p1", "p2", "p3", "r2", "r3" }[n - 1] : $"i{n}";
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
