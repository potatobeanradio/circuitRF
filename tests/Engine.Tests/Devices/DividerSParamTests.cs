using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MGPD, MBPD and MTPD — the Gysel, Bagley polygon and tee dividers — through the engine. Each is its plan's MTEE, MLIN, MCURVE and corner sections and nothing else, so it matches the same parts
/// wired by hand; at F the Gysel divides matched and isolated with Z0 from R2 and R3 to ground, the Bagley divides N
/// ways from a matched input, and the tee splits as K² from a matched input.
/// </summary>
public class DividerSParamTests
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const double F0 = 2.4e9;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Ports(int n) => string.Concat(Enumerable.Range(1, n)
        .Select(k => $"Port:P{k} p{k} 0 Num={k} Z=50 Ohm\n"));

    /// <summary>The part on its pins: p1 … for the ports, then the Gysel's r2, r3, each with Z0 to ground.</summary>
    private static string Part(string kind) => kind switch
    {
        "MGPD" => $"MGPD:D1 p1 p2 p3 r2 r3 Z0=50 Ohm F=2.4 GHz {Sub}\n{Loads}",
        "MGPD-Miter" => $"MGPD:D1 p1 p2 p3 r2 r3 Z0=50 Ohm F=2.4 GHz Corner=Miter {Sub}\n{Loads}",
        "MGPD-Square" => $"MGPD:D1 p1 p2 p3 r2 r3 Z0=50 Ohm F=2.4 GHz Corner=Square {Sub}\n{Loads}",
        "MBPD3" => $"MBPD:D1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz N=3 {Sub}",
        "MBPD5-Square" => $"MBPD:D1 p1 p2 p3 p4 p5 p6 Z0=50 Ohm F=2.4 GHz N=5 Corner=Square {Sub}",
        "MTPD" => $"MTPD:D1 p1 p2 p3 Z0=50 Ohm F=2.4 GHz {Sub}",
        "MTPD-K" => $"MTPD:D1 p1 p2 p3 Z0=50 Ohm F=2.4 GHz K=1.4142 {Sub}",
        _ => throw new ArgumentException(kind),
    };

    private const string Loads = "R:RA r2 0 R=50 Ohm\nR:RB r3 0 R=50 Ohm";

    private static (PassivePlan Plan, int Ports) Plan(string kind) => kind switch
    {
        "MGPD" => (GyselPlan.Build(50.0, F0, Ro4350b).Plan, 3),
        "MGPD-Miter" => (GyselPlan.Build(50.0, F0, Ro4350b, PlanCorner.Miter).Plan, 3),
        "MGPD-Square" => (GyselPlan.Build(50.0, F0, Ro4350b, PlanCorner.Square).Plan, 3),
        "MBPD3" => (BagleyPlan.Build(50.0, F0, 3, Ro4350b).Plan, 4),
        "MBPD5-Square" => (BagleyPlan.Build(50.0, F0, 5, Ro4350b, PlanCorner.Square).Plan, 6),
        "MTPD" => (TeeDividerPlan.Build(50.0, F0, Ro4350b).Plan, 3),
        "MTPD-K" => (TeeDividerPlan.Build(50.0, F0, Ro4350b, 1.4142).Plan, 3),
        _ => throw new ArgumentException(kind),
    };

    private static Complex[,] SAt(string cnl, int ports, double freqHz)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [freqHz])["S"];
        var m = new Complex[ports, ports];
        for (int i = 0; i < ports; i++)
            for (int j = 0; j < ports; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static int PortCount(string kind) => kind switch
    {
        "MBPD3" => 4,
        "MBPD5-Square" => 6,
        _ => 3,
    };

    private static Complex[,] SOf(string kind, double f)
        => SAt(Ports(PortCount(kind)) + Part(kind), PortCount(kind), f);

    /// <summary>The plan's own sections as explicit MTEE, MLIN, MCURVE, MMITER and MBEND instances, with the Gysel's
    /// loads.</summary>
    private static string ByHand(string kind)
    {
        var (plan, ports) = Plan(kind);
        string Node(int n) => n <= ports ? $"p{n}" : n <= plan.Pins.Count ? plan.Pins[n - 1].ToLowerInvariant() : $"i{n}";
        var b = new StringBuilder(Ports(ports));
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanCurve c => $"MCURVE:C{++k} {nodes} W={F(c.W)} Angle={F(c.AngleDeg)} deg Radius={F(c.Radius)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanBend { M: { } m } bend => $"MMITER:B{++k} {nodes} W={F(bend.W)} Angle={F(bend.AngleDeg)} deg M={F(m)} {Sub}",
                PlanBend bend => $"MBEND:B{++k} {nodes} W={F(bend.W)} Angle={F(bend.AngleDeg)} deg Miter=0 {Sub}",
                _ => throw new InvalidOperationException(),
            });
        }
        if (kind.StartsWith("MGPD")) b.AppendLine(Loads);
        return b.ToString();
    }

    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);

    /// <summary>arg a − arg b in degrees, wrapped into (−180, 180].</summary>
    private static double PhaseDeg(Complex a, Complex b)
    {
        double d = (a.Phase - b.Phase) * 180.0 / Math.PI;
        while (d <= -180.0) d += 360.0;
        while (d > 180.0) d -= 360.0;
        return d;
    }

    [Theory]
    [InlineData("MGPD", 1.2e9)]
    [InlineData("MGPD", 2.4e9)]
    [InlineData("MGPD", 4.8e9)]
    [InlineData("MGPD-Miter", 2.4e9)]
    [InlineData("MGPD-Square", 4.8e9)]
    [InlineData("MBPD3", 1.2e9)]
    [InlineData("MBPD3", 2.4e9)]
    [InlineData("MBPD5-Square", 2.4e9)]
    [InlineData("MTPD", 2.4e9)]
    [InlineData("MTPD-K", 4.8e9)]
    public void TheDivider_IsItsSectionsWiredByHand(string kind, double f)
    {
        int n = PortCount(kind);
        var a = SOf(kind, f);
        var b = SAt(ByHand(kind), n, f);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    /// <summary>With Z0 from R2 and from R3 to ground the Gysel is matched at every port, its outputs isolated, and it
    /// divides 3 dB in phase at F — curved, mitred or square.</summary>
    [Theory]
    [InlineData("MGPD", -25.0)]
    [InlineData("MGPD-Miter", -25.0)]
    [InlineData("MGPD-Square", -20.0)]
    public void TheGysel_DividesMatchedAndIsolatedAtF(string kind, double limitDb)
    {
        var s = SOf(kind, F0);
        foreach (var (i, j) in new[] { (0, 0), (1, 1), (2, 2), (2, 1) })
            Assert.True(Db(s[i, j]) < limitDb, $"|S{i + 1}{j + 1}| {Db(s[i, j]):F2} dB");
        Assert.InRange(Db(s[1, 0]), -3.4, -3.0);
        Assert.InRange(Db(s[2, 0]), -3.4, -3.0);
        Assert.InRange(PhaseDeg(s[1, 0], s[2, 0]), -2.0, 2.0);
    }

    /// <summary>The Bagley divides N ways from a matched input, a third each for three; neighbouring outputs leave half a
    /// period apart, and every other one in phase.</summary>
    [Fact]
    public void TheBagley_DividesThreeWaysAtF()
    {
        var s = SOf("MBPD3", F0);
        Assert.True(Db(s[0, 0]) < -20.0, $"|S11| {Db(s[0, 0]):F2} dB");
        for (int k = 1; k <= 3; k++) Assert.InRange(Db(s[k, 0]), -5.2, -4.77);
        Assert.InRange(Math.Abs(PhaseDeg(s[1, 0], s[2, 0])), 175.0, 180.0);
        Assert.InRange(PhaseDeg(s[1, 0], s[3, 0]), -2.0, 2.0);
    }

    [Fact]
    public void TheBagley_DividesFiveWaysAtF()
    {
        var s = SOf("MBPD5-Square", F0);
        Assert.True(Db(s[0, 0]) < -20.0, $"|S11| {Db(s[0, 0]):F2} dB");
        for (int k = 1; k <= 5; k++) Assert.InRange(Db(s[k, 0]), -7.5, -6.99);
    }

    /// <summary>The tee splits 3 dB in phase from a matched input, or as K² = 2 with K = 1.4142.</summary>
    [Theory]
    [InlineData("MTPD", 1.0)]
    [InlineData("MTPD-K", 2.0)]
    public void TheTee_SplitsAsKSquaredAtF(string kind, double ratio)
    {
        var s = SOf(kind, F0);
        Assert.True(Db(s[0, 0]) < -20.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.InRange(s[2, 0].Magnitude * s[2, 0].Magnitude / (s[1, 0].Magnitude * s[1, 0].Magnitude), 0.95 * ratio, 1.05 * ratio);
        Assert.InRange(PhaseDeg(s[1, 0], s[2, 0]), -3.0, 3.0);
        Assert.InRange(s[1, 0].Magnitude * s[1, 0].Magnitude + s[2, 0].Magnitude * s[2, 0].Magnitude, 0.97, 1.0);
    }
}
