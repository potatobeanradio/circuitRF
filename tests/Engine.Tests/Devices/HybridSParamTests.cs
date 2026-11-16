using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Engine.Tests.Devices;

/// <summary>
/// MBLC, the 3 dB branch-line hybrid, and MRRC, the rat-race hybrid, through the engine. Each is its plan's MTEE,
/// MLIN and MCURVE sections and nothing else, so it matches the same parts wired by hand, and the one-section
/// branch-line on the C2 fixture's dimensions is the C2 fixture. At F they split 3 dB in quadrature or in and
/// out of phase.
/// </summary>
public class HybridSParamTests
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private static readonly MicrostripSubstrate Ro4350b = new(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037);
    private const string Ports = """
        Port:P1 p1 0 Num=1 Z=50 Ohm
        Port:P2 p2 0 Num=2 Z=50 Ohm
        Port:P3 p3 0 Num=3 Z=50 Ohm
        Port:P4 p4 0 Num=4 Z=50 Ohm
        """;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Part(string kind) => kind switch
    {
        "MBLC1" => $"MBLC:H1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz N=1 {Sub}",
        "MBLC2" => $"MBLC:H1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz N=2 {Sub}",
        "MRRC-Miter" => $"MRRC:H1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz Corner=Miter {Sub}",
        "MRRC-Square" => $"MRRC:H1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz Corner=Square {Sub}",
        _ => $"MRRC:H1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz {Sub}",
    };

    private static PassivePlan Plan(string kind) => kind switch
    {
        "MBLC1" => BranchLinePlan.Build(50.0, 2.4e9, 1, Ro4350b).Plan,
        "MBLC2" => BranchLinePlan.Build(50.0, 2.4e9, 2, Ro4350b).Plan,
        "MRRC-Miter" => RatRacePlan.Build(50.0, 2.4e9, Ro4350b, PlanCorner.Miter).Plan,
        "MRRC-Square" => RatRacePlan.Build(50.0, 2.4e9, Ro4350b, PlanCorner.Square).Plan,
        _ => RatRacePlan.Build(50.0, 2.4e9, Ro4350b).Plan,
    };

    private static ElaboratedNetlist Elaborate(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        return new Elaborator(lib).Elaborate(tb);
    }

    private static Complex[,] SAt(ElaboratedNetlist nl, double freqHz)
    {
        var s = SParameterEngine.Run(nl, [freqHz])["S"];
        var m = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static void AssertSame(Complex[,] a, Complex[,] b, double f)
    {
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
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

    /// <summary>The plan's own sections as explicit MTEE, MLIN and MCURVE instances.</summary>
    private static string ByHand(PassivePlan plan)
    {
        string Node(int n) => n <= 4 ? $"p{n}" : $"i{n}";
        var b = new StringBuilder(Ports).AppendLine();
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
        return b.ToString();
    }

    [Theory]
    [InlineData("MBLC1", 1.2e9)]
    [InlineData("MBLC1", 2.4e9)]
    [InlineData("MBLC1", 4.8e9)]
    [InlineData("MBLC2", 1.2e9)]
    [InlineData("MBLC2", 2.4e9)]
    [InlineData("MBLC2", 4.8e9)]
    [InlineData("MRRC", 1.2e9)]
    [InlineData("MRRC", 2.4e9)]
    [InlineData("MRRC", 4.8e9)]
    [InlineData("MRRC-Miter", 1.2e9)]
    [InlineData("MRRC-Miter", 2.4e9)]
    [InlineData("MRRC-Square", 2.4e9)]
    [InlineData("MRRC-Square", 4.8e9)]
    public void TheHybrid_IsItsSectionsWiredByHand(string kind, double f)
        => AssertSame(SAt(Elaborate($"{Ports}\n{Part(kind)}"), f), SAt(Elaborate(ByHand(Plan(kind))), f), f);

    [Theory]
    [InlineData("MBLC1")]
    [InlineData("MBLC2")]
    public void TheBranchLine_SplitsInQuadratureAtF(string kind)
    {
        var s = SAt(Elaborate($"{Ports}\n{Part(kind)}"), 2.4e9);

        Assert.True(Db(s[0, 0]) < -20.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.True(Db(s[3, 0]) < -20.0, $"|S41| {Db(s[3, 0]):F2} dB");
        Assert.InRange(Db(s[1, 0]), -3.4, -3.0);
        Assert.InRange(Db(s[2, 0]), -3.4, -3.0);
        Assert.InRange(PhaseDeg(s[1, 0], s[2, 0]), 87.0, 93.0);      // 3 lags 2 by a quarter period
    }

    /// <summary>With corners too: the corners' own electrical length comes out of the sides, so every spacing is still
    /// λg/4 or 3λg/4 at F. The cornered limit is set to catch it if it were not: on this board the mitred ring's |S11|,
    /// |S44|, |S41| and |S23| are −49 to −51 dB at F, as the curved ring's are, the square ring's −43 to −59 dB, and with
    /// the mitred corners' 2 × 0.57 mm left in each side (6 % of λg/4) |S11|, |S44| and |S23| rise to about −30 dB.</summary>
    [Theory]
    [InlineData("MRRC", -20.0)]
    [InlineData("MRRC-Miter", -40.0)]
    [InlineData("MRRC-Square", -40.0)]
    public void TheRatRace_SumsAndDifferencesAtF(string kind, double limitDb)
    {
        var s = SAt(Elaborate($"{Ports}\n{Part(kind)}"), 2.4e9);

        foreach (var (i, j) in new[] { (0, 0), (3, 3), (3, 0), (2, 1) })
            Assert.True(Db(s[i, j]) < limitDb, $"|S{i + 1}{j + 1}| {Db(s[i, j]):F2} dB");
        foreach (var (i, j) in new[] { (1, 0), (2, 0), (1, 3), (2, 3) })
            Assert.InRange(Db(s[i, j]), -3.4, -3.0);
        Assert.InRange(PhaseDeg(s[1, 0], s[2, 0]), -3.0, 3.0);       // Σ: in phase
        Assert.InRange(Math.Abs(PhaseDeg(s[1, 3], s[2, 3])), 177.0, 180.0);   // Δ: out of phase
    }

    /// <summary>testdata/schematic-to-layout/C2-branch-line, the hand-drawn one-section coupler: four MTEEs, four
    /// arms and four launches. The plan laid out on its dimensions — no branch stub, which is how C2 draws its
    /// tees — is that network.</summary>
    [Fact]
    public void TheOneSectionPlan_OnTheC2FixturesDimensions_IsTheFixture()
    {
        string csch = Path.Combine(RepoRoot(), "testdata", "schematic-to-layout", "C2-branch-line", "schematic",
            "C2-branch-line.csch");
        var fixture = Elaborate(NetlistOf(csch));

        // The fixture's VAR1, on the factory's fallback substrate it simulates on (it resolves no technology).
        const double w50 = 280.152e-6, l90 = 4.6604e-3, launch = 1e-3;
        var plan = BranchLinePlan.Layout(w50, launch, 0.0, [(w50, l90 - w50 / 2.0)], [(w50, l90), (w50, l90)]);
        var substrate = new MicrostripSubstrate(ComponentModelFactory.DefaultSubstrateHMeters,
            ComponentModelFactory.DefaultSubstrateTMeters, ComponentModelFactory.DefaultSubstrateEpsR,
            ComponentModelFactory.DefaultSubstrateSigmaSPerM, ComponentModelFactory.DefaultSubstrateTanD);
        var part = Elaborate($"{Ports}\nMBLC:H1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz N=1");
        int at = part.Components.FindIndex(c => c.InstancePath == "H1");
        var c = part.Components[at];
        part.Components[at] = new ElaboratedComponent(c.ComponentType, c.InstancePath, c.Nodes, c.Parameters,
            new MicrostripPlanModel(plan, substrate, c.InstancePath));

        Assert.Equal(12, plan.Sections.Count);
        foreach (double f in new[] { 1.2e9, 2.4e9, 4.8e9 })
            AssertSame(SAt(part, f), SAt(fixture, f), f);
    }

    /// <summary>At DC each hybrid is copper: 1 V through 50 Ω reaches every pin alike, into three 50 Ω loads.</summary>
    [Theory]
    [InlineData("MBLC1")]
    [InlineData("MBLC2")]
    [InlineData("MRRC")]
    public void AtDc_EveryPinIsOneNode(string kind)
    {
        var nl = Elaborate($"""
            Vdc:V1 a 0 Vdc=1 V
            R:Rs a p1 R=50 Ohm
            {Part(kind)}
            R:L2 p2 0 R=50 Ohm
            R:L3 p3 0 R=50 Ohm
            R:L4 p4 0 R=50 Ohm
            analysis DC1 type=dc
            """);
        var dc = NonlinearDcEngine.Run(nl);
        Assert.True(dc.Converged);
        double V(string n) => dc.NodeVoltages[nl.Nodes.GetOrAssign(n) - 1];
        foreach (string n in new[] { "p2", "p3", "p4" }) Assert.Equal(V("p1"), V(n), 9);
        Assert.Equal(0.25, V("p1"), 9);
    }

    /// <summary>The schematic's netlist as <c>circuitrf netlist</c> extracts it: the CLI built beside these tests,
    /// run as a process, since this project links nothing above the engine.</summary>
    private static string NetlistOf(string csch)
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(HybridSParamTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (string arg in new[] { Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll")), "netlist", csch })
            psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        string cnl = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"netlist exited {p.ExitCode}: {stderr.Result}");
        return cnl;
    }

    private static string RepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata"))) return dir;
        throw new DirectoryNotFoundException("testdata not found above " + AppContext.BaseDirectory);
    }
}
