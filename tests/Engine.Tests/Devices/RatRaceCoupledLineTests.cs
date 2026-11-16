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
/// MRRC with Form = CoupledLine, the coupled-line ring (S. March, 1968), through the engine, its grounded ends G2 and G4
/// on ideal vias. It is its plan's MCLIN, MTEE, MLIN and corner sections and nothing else, so it matches them wired by hand
/// to 1e-9; and at F it is a rat-race: driven at Σ (1) the outputs 2 and 3 split in phase and 4 is isolated, driven at Δ
/// (4) they split 180° apart and 1 is isolated, every port matched. The section needs 1/Zo − 1/Ze = √2/Z0 whatever its
/// coupling, which 100 µm GaAs gives a 50 Ω ring at 10 GHz and 20 mil RO4350B only from about 65 Ω: these are the cases.
/// </summary>
public class RatRaceCoupledLineTests(ITestOutputHelper output)
{
    /// <summary>The two cases: a 50 Ω ring on 100 µm GaAs at 10 GHz, and a 75 Ω one on 20 mil RO4350B at 2.4 GHz.</summary>
    private sealed record Case(string Sub, MicrostripSubstrate S, double F, double Z0)
    {
        public string Ports => string.Join("\n", Enumerable.Range(1, 4).Select(k =>
            $"Port:P{k} p{k} 0 Num={k} Z={Z0.ToString("R", CultureInfo.InvariantCulture)} Ohm"));
    }

    private static readonly Case GaAs = new("H=100e-6 T=3e-6 Er=12.9 Sigma=4.1e7 TanD=0.0006",
        new MicrostripSubstrate(100e-6, 3e-6, 12.9, 4.1e7, 0.0006), 10e9, 50.0);
    private static readonly Case Board = new("H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037",
        new MicrostripSubstrate(0.508e-3, 35e-6, 3.66, 5.8e7, 0.0037), 2.4e9, 75.0);
    private static Case Of(string name) => name == "GaAs" ? GaAs : Board;
    private const double F0 = 2.4e9;

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static double Db(Complex x) => 20.0 * Math.Log10(x.Magnitude);
    private static double Wrap(double deg) => deg - 360.0 * Math.Round(deg / 360.0);
    private static double Deg(Complex a, Complex b) => Wrap((a.Phase - b.Phase) * 180.0 / Math.PI);

    private static Complex[,] SAt(string cnl, double f)
    {
        var (lib, tb) = new CnlReader().Read(cnl);
        var s = SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), [f])["S"];
        var m = new Complex[4, 4];
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                m[i, j] = (Complex)s[0, i, j];
        return m;
    }

    private static string Ring(Case c, string corner = "Curve") => $"""
        {c.Ports}
        MRRC:H1 p1 p2 p3 p4 0 0 Z0={F(c.Z0)} Ohm F={F(c.F)} Hz Form=CoupledLine Coupling=10 dB Corner={corner} {c.Sub}
        """;

    private static string ByHand(PassivePlan plan, Case board)
    {
        string Node(int n) => n switch { <= 4 => $"p{n}", 5 or 6 => "0", _ => $"i{n}" };
        string Sub = board.Sub;
        var b = new StringBuilder(board.Ports).AppendLine();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(Node));
            b.AppendLine(section switch
            {
                PlanTee t => $"MTEE:T{++k} {nodes} W1={F(t.W1)} W2={F(t.W2)} W3={F(t.W3)} L1={F(t.L1)} L2={F(t.L2)} L3={F(t.L3)} {Sub}",
                PlanCurve c => $"MCURVE:C{++k} {nodes} W={F(c.W)} Angle={F(c.AngleDeg)} deg Radius={F(c.Radius)} {Sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {Sub}",
                PlanBend { M: { } m } e => $"MMITER:B{++k} {nodes} W={F(e.W)} Angle={F(e.AngleDeg)} deg M={F(m)} {Sub}",
                PlanBend e => $"MBEND:B{++k} {nodes} W={F(e.W)} Angle={F(e.AngleDeg)} deg Miter=0 {Sub}",
                PlanCoupledLine c => $"MCLIN:K{++k} {nodes} W={F(c.W)} S={F(c.S)} L={F(c.L)} {Sub}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return b.ToString();
    }

    [Theory]
    [InlineData("GaAs", "Curve", 0.5)]
    [InlineData("GaAs", "Curve", 1.0)]
    [InlineData("GaAs", "Curve", 2.0)]
    [InlineData("GaAs", "Miter", 1.0)]
    [InlineData("Board", "Curve", 1.0)]
    [InlineData("Board", "Square", 1.25)]
    public void TheRing_IsItsSectionsWiredByHand(string board, string corner, double atF)
    {
        var c = Of(board);
        double f = atF * c.F;
        var plan = RatRacePlan.Build(c.Z0, c.F, c.S, Enum.Parse<PlanCorner>(corner), form: RatRaceForm.CoupledLine).Plan;
        var a = SAt(Ring(c, corner), f);
        var b = SAt(ByHand(plan, c), f);
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                Assert.True((a[i, j] - b[i, j]).Magnitude < 1e-9, $"S{i + 1}{j + 1} at {f / 1e9} GHz: {a[i, j]} vs {b[i, j]}");
    }

    /// <summary>At F: Σ in phase and Δ 180° out to 2 and 3, each about 3 dB down, the isolated port and every input
    /// matched well below −20 dB: on GaAs −52 dB at Σ, −40 dB at Δ, the outputs within 1.5° of 0° and 180°.</summary>
    [Theory]
    [InlineData("GaAs", "Curve")]
    [InlineData("GaAs", "Miter")]
    [InlineData("Board", "Curve")]
    public void AtF_ItIsARatRace(string board, string corner)
    {
        var c = Of(board);
        var s = SAt(Ring(c, corner), c.F);
        output.WriteLine($"{board} {corner}: S11 {Db(s[0, 0]):F2} S44 {Db(s[3, 3]):F2} S41 {Db(s[3, 0]):F2} S23 {Db(s[1, 2]):F2} dB; "
                       + $"S21 {Db(s[1, 0]):F2} S31 {Db(s[2, 0]):F2} ({Deg(s[1, 0], s[2, 0]):F2} deg); "
                       + $"S24 {Db(s[1, 3]):F2} S34 {Db(s[2, 3]):F2} ({Deg(s[1, 3], s[2, 3]):F2} deg)");
        foreach (var (x, name) in new[] { (s[0, 0], "S11"), (s[3, 3], "S44"), (s[3, 0], "S41"), (s[1, 2], "S23") })
            Assert.True(Db(x) < -20.0, $"|{name}| {Db(x):F2} dB");
        foreach (var x in new[] { s[1, 0], s[2, 0], s[1, 3], s[2, 3] })
            Assert.InRange(Db(x), -3.9, -2.9);          // GaAs's 3 µm gold costs 0.6 dB at 10 GHz
        Assert.InRange(Deg(s[1, 0], s[2, 0]), -5.0, 5.0);
        Assert.InRange(Math.Abs(Deg(s[1, 3], s[2, 3])), 175.0, 180.0);
    }

    /// <summary>March's section as TEM lines: a CLIN of 2·Ze·Zo/(Ze − Zo) = √2·Z0, a quarter wave long with two diagonal far
    /// ends grounded, is a 270° line of √2·Z0 at F — transfer and match as the 3λ/4 arm's.</summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(3.0)]
    public void TheSection_AsTemLines_IsA270DegreeLine(double cDb)
    {
        double zc = 50.0 * Math.Sqrt(2.0), c = Math.Pow(10, -cDb / 20), zm = zc * c / Math.Sqrt(1 - c * c), r = Math.Sqrt((1 + c) / (1 - c));
        Assert.Equal(zc, 2 * zm * r * (zm / r) / (zm * r - zm / r), 9);
        string two = $"""
            Port:P1 a 0 Num=1 Z={F(zc)} Ohm
            Port:P2 b 0 Num=2 Z={F(zc)} Ohm
            """;
        var s = SAt(two + $"\nCLIN:K1 a 0 0 b Ze={F(zm * r)} Zo={F(zm / r)} E=90 deg F=2.4 GHz\nPort:P3 x 0 Num=3 Z=50 Ohm\nPort:P4 y 0 Num=4 Z=50 Ohm", F0);
        var line = SAt(two + "\nTLIN:T1 a b Z=" + F(zc) + " Ohm E=270 deg F=2.4 GHz\nPort:P3 x 0 Num=3 Z=50 Ohm\nPort:P4 y 0 Num=4 Z=50 Ohm", F0);
        Assert.True(s[0, 0].Magnitude < 1e-9, $"|S11| {s[0, 0].Magnitude}");
        Assert.True((s[1, 0] - line[1, 0]).Magnitude < 1e-9, $"S21 {s[1, 0]} vs the 270° line's {line[1, 0]}");
    }
}
