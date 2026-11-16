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
/// The coupled-line options through the engine (docs/design/rf-passives.md §3.5-§3.6): MCLC's Sections, Tandem,
/// Compensation and Broadside, MIDC's Fingers, and the Schiffman phase shifter (MSPS). Each is its plan's sections and
/// nothing else, so it matches the same network wired by hand to 1e-9; and each does what it is for — a flat coupling
/// over the band, a broadband 3 dB tandem in quadrature, directivity restored, a 90° difference flat over the band.
/// </summary>
public class CouplerOptionsSParamTests(ITestOutputHelper output)
{
    private const string Sub = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";
    private const string Lossless = "H=0.508e-3 T=35e-6 Er=3.66 Sigma=1e300 TanD=0";
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

    /// <summary>A plan's sections as explicit instances of the parts they are; <paramref name="node"/> names plan nodes.</summary>
    private static string ByHand(PassivePlan plan, Func<int, string> node, string sub)
    {
        var b = new StringBuilder();
        int k = 0;
        foreach (var section in plan.Sections)
        {
            string nodes = string.Join(" ", section.Nodes.Select(node));
            b.AppendLine(section switch
            {
                PlanCoupledLine { Fingers: 2, Wiggly: false } c => $"MCLIN:K{++k} {nodes} W={F(c.W)} S={F(c.S)} L={F(c.L)} {sub}",
                PlanLine l => $"MLIN:L{++k} {nodes} W={F(l.W)} L={F(l.L)} {sub}",
                PlanBend { M: { } m } e => $"MMITER:B{++k} {nodes} W={F(e.W)} Angle={F(e.AngleDeg)} deg M={F(m)} {sub}",
                PlanBend e => $"MBEND:B{++k} {nodes} W={F(e.W)} Angle={F(e.AngleDeg)} deg Miter=0 {sub}",
                PlanCurve c => $"MCURVE:C{++k} {nodes} W={F(c.W)} Angle={F(c.AngleDeg)} deg Radius={F(c.Radius)} {sub}",
                PlanBroadsideLine { Stack: var s } p => $"MBCLIN:K{++k} {nodes} W={F(p.W)} O={F(p.O)} L={F(p.L)} Hb={F(s.Hb)} "
                    + $"Hs={F(s.Hs)} Ta={F(s.Ta)} Tb={F(s.Tb)} Er={F(s.EpsR)} Sigma={F(s.SigmaSPerM)} TanD={F(s.TanD)}",
                _ => throw new InvalidOperationException(section.GetType().Name),
            });
        }
        return b.ToString();
    }

    private static string Pin(int n, int pins) => n <= pins ? $"p{n}" : $"i{n}";

    // ── Sections ────────────────────────────────────────────────────────────────────────────────────────

    private static string Sections(int n, double cDb, double band) => $"""
        {Ports(4)}
        MCLC:C1 p1 p2 p3 p4 Coupling={F(cDb)} dB Z0=50 Ohm F=2.4 GHz Sections={n} Band={F(band)} {Sub}
        """;

    [Theory]
    [InlineData(1.6e9)]
    [InlineData(2.4e9)]
    [InlineData(3.2e9)]
    public void ThreeSections_AreTheirMclinsWiredByHand(double f)
    {
        var plan = CoupledLineCouplerPlan.Build(15.0, 50.0, F0, Ro4350b, sections: 3, band: 2.0).Plan;
        AssertSame(SAt(Sections(3, 15.0, 2.0), f, 4), SAt(Ports(4) + "\n" + ByHand(plan, n => Pin(n, 4), Sub), f, 4), f);
    }

    /// <summary>Over the 2:1 band the three-section coupler holds its coupling far flatter than one section does, whose
    /// coupling falls with sin θ towards the band's edges.</summary>
    [Fact]
    public void ThreeSections_HoldTheCouplingFlatOverTheBand()
    {
        double[] fs = [.. Enumerable.Range(0, 21).Select(i => F0 * (2.0 / 3.0 + (4.0 / 3.0 - 2.0 / 3.0) * i / 20.0))];
        double Spread(string cnl) { var c = fs.Select(f => Db(SAt(cnl, f, 4)[2, 0])).ToArray(); return c.Max() - c.Min(); }
        var d = CoupledLineCouplerPlan.Build(15.0, 50.0, F0, Ro4350b, sections: 3, band: 2.0);
        double three = Spread(Sections(3, 15.0, 2.0)), one = Spread(Sections(1, 15.0, 2.0));
        output.WriteLine($"ripple ±{d.RippleDb:F3} dB (TEM); coupling spread over 2:1 — 3 sections {three:F2} dB, 1 section {one:F2} dB");
        Assert.True(three < one / 2.0, $"three sections {three:F2} dB vs one {one:F2} dB");
        Assert.InRange(Db(SAt(Sections(3, 15.0, 2.0), F0, 4)[2, 0]), -15.0 - 2.0 * d.RippleDb - 0.5, -15.0 + 2.0 * d.RippleDb + 0.5);
    }

    // ── Tandem ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Jumpers: ideal wires on the two crossovers.</summary>
    private const string Crossovers = """
        Short:X1 j1 j2
        Short:X2 j3 j4
        """;

    private static string Tandem(double cDb, string sub = Sub) => $"""
        {Ports(4)}
        MCLC:T1 p1 p2 p3 p4 j1 j2 j3 j4 Coupling={F(cDb)} dB Z0=50 Ohm F=2.4 GHz Tandem=1 {sub}
        {Crossovers}
        """;

    private static string TandemNode(int n) => n switch { <= 4 => $"p{n}", <= 8 => $"j{n - 4}", _ => $"i{n}" };

    [Theory]
    [InlineData(1.2e9)]
    [InlineData(2.4e9)]
    [InlineData(3.6e9)]
    public void TheTandem_IsItsSectionsWiredByHand(double f)
    {
        var plan = CoupledLineCouplerPlan.Build(3.0, 50.0, F0, Ro4350b, tandem: 1).Plan;
        AssertSame(SAt(Tandem(3.0), f, 4), SAt(Ports(4) + "\n" + ByHand(plan, TandemNode, Sub) + Crossovers, f, 4), f);
    }

    /// <summary>Two couplers of 8.34 dB in tandem split 3 dB in quadrature, and hold it over a wide band: at F within
    /// half a dB of an even split, and from 1.7 to 2.8 GHz within a dB and a half of it, 90° to within 2°.</summary>
    [Fact]
    public void TheTandem_SplitsThreeDbInQuadrature_OverABroadBand()
    {
        foreach (double f in new[] { 1.7e9, 2.0e9, 2.4e9, 2.8e9 })
        {
            var s = SAt(Tandem(3.0), f, 4);
            double quad = Wrap(Deg(s[2, 0]) - Deg(s[1, 0]));
            output.WriteLine($"{f / 1e9:F1} GHz: S11 {Db(s[0, 0]):F2}  S21 {Db(s[1, 0]):F2}  S31 {Db(s[2, 0]):F2}  "
                           + $"S41 {Db(s[3, 0]):F2} dB, S31-S21 {quad:F2} deg");
            Assert.InRange(Math.Abs(quad), 88.0, 92.0);
            Assert.InRange(Db(s[2, 0]), -4.5, -2.5);
            Assert.InRange(Db(s[1, 0]), -4.5, -2.5);
        }
        var c = SAt(Tandem(3.0), F0, 4);
        Assert.InRange(Db(c[1, 0]) - Db(c[2, 0]), -0.5, 0.5);
        Assert.True(Db(c[0, 0]) < -15.0, $"|S11| {Db(c[0, 0]):F2} dB");
    }

    // ── Compensation ────────────────────────────────────────────────────────────────────────────────────

    private static string Compensated(string form, string extra = "", string sub = Sub) => $"""
        {Ports(4)}
        MCLC:C1 p1 p2 p3 p4 Coupling=10 dB Z0=50 Ohm F=2.4 GHz Compensation={form} {sub}
        {extra}
        """;

    private static double Directivity(Complex[,] s) => Db(s[2, 0]) - Db(s[3, 0]);

    /// <summary>The capacitive form: the readout's capacitor on 1-3 and on 2-4 restores the directivity at F that
    /// microstrip's mode velocities take away (11 dB at 10 dB coupling uncompensated), with the coupling kept.</summary>
    [Fact]
    public void Capacitors_RestoreTheDirectivity()
    {
        var d = CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b, compensation: CouplerCompensation.Capacitive);
        string caps = $"C:C13 p1 p3 C={F(d.CompensationFarads)} F\nC:C24 p2 p4 C={F(d.CompensationFarads)} F";
        var s = SAt(Compensated("Capacitive", caps), F0, 4);
        var bare = SAt(Compensated("None"), F0, 4);
        output.WriteLine($"C {d.CompensationFarads * 1e12:F4} pF: directivity {Directivity(s):F1} dB (bare {Directivity(bare):F1}), "
                       + $"S31 {Db(s[2, 0]):F2}, S11 {Db(s[0, 0]):F2} dB");
        Assert.True(Directivity(s) > Directivity(bare) + 15.0, $"directivity {Directivity(s):F1} dB");
        Assert.InRange(Db(s[2, 0]), -10.5, -9.5);
        Assert.True(Db(s[0, 0]) < -25.0, $"|S11| {Db(s[0, 0]):F2} dB");
    }

    [Theory]
    [InlineData(1.2e9)]
    [InlineData(2.4e9)]
    [InlineData(4.8e9)]
    public void TheInductiveForm_IsItsSectionsWiredByHand(double f)
    {
        var plan = CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b, compensation: CouplerCompensation.Inductive).Plan;
        AssertSame(SAt(Compensated("Inductive"), f, 4), SAt(Ports(4) + "\n" + ByHand(plan, n => Pin(n, 4), Sub), f, 4), f);
    }

    /// <summary>The series sections at the four ends: the directivity at F well above the uncompensated pair's.</summary>
    [Fact]
    public void SeriesSections_ImproveTheDirectivity()
    {
        var s = SAt(Compensated("Inductive"), F0, 4);
        var bare = SAt(Compensated("None"), F0, 4);
        output.WriteLine($"directivity {Directivity(s):F1} dB (bare {Directivity(bare):F1}), S31 {Db(s[2, 0]):F2}, "
                       + $"S11 {Db(s[0, 0]):F2} dB");
        Assert.True(Directivity(s) > Directivity(bare) + 6.0, $"directivity {Directivity(s):F1} dB");
        Assert.InRange(Db(s[2, 0]), -11.0, -9.0);
    }

    /// <summary>Wiggly edges in the circuit are MCLIN's own modes with the odd mode run k times as far: on a lossless board
    /// that is one CLIN of the pair's modes, the odd mode's electrical length k times its own, to 1e-9 at F.</summary>
    [Fact]
    public void TheWigglyPair_IsItsModesWithALongerOddPath()
    {
        var d = CoupledLineCouplerPlan.Build(10.0, 50.0, F0, Ro4350b with { SigmaSPerM = 1e300, TanD = 0.0 },
            compensation: CouplerCompensation.Wiggly);
        double k0 = 2 * Math.PI * F0 / 2.99792458e8;
        string clin = $"""
            {Ports(4)}
            CLIN:K1 p1 p2 p3 p4 Ze={F(d.Modes.Ze)} Zo={F(d.Modes.Zo)} E={F(k0 * Math.Sqrt(d.Modes.EeffE) * d.LengthMeters)} Eo={F(k0 * Math.Sqrt(d.Modes.EeffO) * d.LengthMeters * d.OddLengthFactor)} F=2.4 GHz
            """;
        AssertSame(SAt(Compensated("Wiggly", sub: Lossless), F0, 4), SAt(clin, F0, 4), F0);
        Assert.Equal(Math.Sqrt(d.Modes.EeffE / d.Modes.EeffO), d.OddLengthFactor, 12);
        var s = SAt(Compensated("Wiggly"), F0, 4);
        output.WriteLine($"k {d.OddLengthFactor:F4}: directivity {Directivity(s):F1} dB, S31 {Db(s[2, 0]):F2} dB");
        Assert.True(Directivity(s) > 30.0, $"directivity {Directivity(s):F1} dB");
    }

    // ── Broadside ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A four-layer board's top two copper layers, 2 mil apart over a 10 mil core: what the extraction injects for
    /// an MBCLIN.</summary>
    private static readonly BroadsideStack Stack = new(Hs: 0.05e-3, Hb: 0.25e-3, Ha: double.NaN, Hd: double.NaN,
        Ta: 35e-6, Tb: 35e-6, EpsR: 3.66, SigmaSPerM: 5.8e7, TanD: 0.0037, Stripline: false);

    private const string StackParams = "Hs=0.05e-3 Hb=0.25e-3 Ta=35e-6 Tb=35e-6 Er=3.66 Sigma=5.8e7 TanD=0.0037";

    private static string Broadside(double cDb) => $"""
        {Ports(4)}
        MCLC:C1 p1 p2 p3 p4 Coupling={F(cDb)} dB Z0=50 Ohm F=2.4 GHz Broadside=1 {StackParams}
        """;

    [Theory]
    [InlineData(1.2e9)]
    [InlineData(2.4e9)]
    public void TheBroadsideCoupler_IsItsMbclinWiredByHand(double f)
    {
        var plan = CoupledLineCouplerPlan.Build(3.0, 50.0, F0, Ro4350b, broadside: 1, stack: Stack).Plan;
        AssertSame(SAt(Broadside(3.0), f, 4), SAt(Ports(4) + "\n" + ByHand(plan, n => Pin(n, 4), ""), f, 4), f);
    }

    /// <summary>3 dB, which no edge-coupled gap on a board reaches, from strips one over the other: at F it splits in
    /// quadrature.</summary>
    [Fact]
    public void TheBroadsideCoupler_SplitsThreeDb()
    {
        var s = SAt(Broadside(3.0), F0, 4);
        output.WriteLine($"S11 {Db(s[0, 0]):F2}  S21 {Db(s[1, 0]):F2}  S31 {Db(s[2, 0]):F2}  S41 {Db(s[3, 0]):F2} dB, "
                       + $"S31-S21 {Wrap(Deg(s[2, 0]) - Deg(s[1, 0])):F1} deg");
        Assert.InRange(Db(s[2, 0]), -3.6, -2.6);
        Assert.True(Db(s[0, 0]) < -15.0, $"|S11| {Db(s[0, 0]):F2} dB");
        Assert.InRange(Math.Abs(Wrap(Deg(s[2, 0]) - Deg(s[1, 0]))), 80.0, 100.0);
    }

    // ── MIDC Fingers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Six fingers with ideal jumpers, each landing J(4m+k) to pin k, are Ou's two-conductor coupler of six
    /// fingers exactly: one CLIN of Ze,6 and Zo,6. A six-finger pair sits near 3·Z0 (N/2 of it at weak coupling), past
    /// the coupled-line model's narrowest strip at 50 Ω, so this one is a 25 Ω coupler.</summary>
    [Fact]
    public void SixFingers_WithIdealJumpers_AreOusEquivalentPair()
    {
        const string ports = """
            Port:P1 p1 0 Num=1 Z=25 Ohm
            Port:P2 p2 0 Num=2 Z=25 Ohm
            Port:P3 p3 0 Num=3 Z=25 Ohm
            Port:P4 p4 0 Num=4 Z=25 Ohm
            """;
        var d = LangePlan.Build(3.0, 25.0, F0, Ro4350b with { SigmaSPerM = 1e300, TanD = 0.0 }, fingers: 6);
        string jumpers = string.Join("\n", Enumerable.Range(1, 8).Select(k => $"Short:S{k} p{(k - 1) % 4 + 1} j{k}"));
        string lange = $"""
            {ports}
            MIDC:X1 p1 p2 p3 p4 {string.Join(" ", Enumerable.Range(1, 8).Select(k => $"j{k}"))} Coupling=3 dB Z0=25 Ohm F=2.4 GHz Fingers=6 {Lossless}
            {jumpers}
            """;
        double k0 = 2 * Math.PI * F0 / 2.99792458e8;
        string pair = $"""
            {ports}
            CLIN:K1 p1 p2 p3 p4 Ze={F(d.Ze)} Zo={F(d.Zo)} E={F(k0 * Math.Sqrt(d.Modes.EeffE) * d.LengthMeters)} Eo={F(k0 * Math.Sqrt(d.Modes.EeffO) * d.LengthMeters)} F=2.4 GHz
            """;
        AssertSame(SAt(lange, F0, 4), SAt(pair, F0, 4), F0);
        var s = SAt(lange.Replace(Lossless, Sub), F0, 4);
        output.WriteLine($"6 fingers, W {d.WMeters * 1e6:F0} um S {d.SMeters * 1e6:F0} um: S21 {Db(s[1, 0]):F2}  S31 {Db(s[2, 0]):F2} dB");
        Assert.InRange(Db(s[2, 0]), -3.5, -2.5);
    }

    // ── MSPS ────────────────────────────────────────────────────────────────────────────────────────────

    private static string Schiffman(string reference = "Straight", string sub = Sub) => $"""
        {Ports(4)}
        MSPS:S1 p1 p2 p3 p4 Z0=50 Ohm F=2.4 GHz Phase=90 deg Band=1.5 Reference={reference} {sub}
        """;

    [Theory]
    [InlineData("Straight", 1.8e9)]
    [InlineData("Straight", 2.4e9)]
    [InlineData("Miter", 2.4e9)]
    [InlineData("Curve", 3.0e9)]
    public void TheSchiffman_IsItsSectionsWiredByHand(string reference, double f)
    {
        var plan = SchiffmanPlan.Build(50.0, F0, 90.0, 1.5, Ro4350b, Enum.Parse<SchiffmanReference>(reference)).Plan;
        AssertSame(SAt(Schiffman(reference), f, 4), SAt(Ports(4) + "\n" + ByHand(plan, n => Pin(n, 4), Sub), f, 4), f);
    }

    /// <summary>Schiffman's C-section as TEM lines: a CLIN of ρ = Ze/Zo with its far ends joined is matched at every
    /// frequency and its phase is cos φ = (ρ − tan²θ)/(ρ + tan²θ).</summary>
    [Theory]
    [InlineData(30.0)]
    [InlineData(70.0)]
    [InlineData(90.0)]
    [InlineData(125.0)]
    public void TheCSection_AsTemLines_IsSchiffmansAllPass(double thetaDeg)
    {
        var (rho, _, _) = SchiffmanPlan.EqualRipple(Math.PI / 2.0, 1.5);
        var s = SAt($"""
            Port:P1 p1 0 Num=1 Z=50 Ohm
            Port:P2 p2 0 Num=2 Z=50 Ohm
            CLIN:K1 p1 m p2 m Ze={F(50 * Math.Sqrt(rho))} Zo={F(50 / Math.Sqrt(rho))} E={F(thetaDeg)} deg F=2.4 GHz
            """, F0, 2);
        double t = thetaDeg * Math.PI / 180.0, tan2 = Math.Tan(t) * Math.Tan(t);
        Assert.True(s[0, 0].Magnitude < 1e-9, $"|S11| {s[0, 0].Magnitude}");
        double phi = SchiffmanPlan.CSectionPhase(rho, t);
        Assert.Equal((rho - tan2) / (rho + tan2), Math.Cos(phi), 9);
        Assert.Equal(Math.Cos(-phi), Math.Cos(s[1, 0].Phase), 9);
        Assert.Equal(Math.Sin(-phi), Math.Sin(s[1, 0].Phase), 9);
    }

    /// <summary>On the board the two paths differ by 90° at F, where the plan sets the C-section's length by its own model,
    /// and the difference stays within a few degrees of it over the band. Microstrip's mode velocities cost the C-section's
    /// match, about −12 dB at F on this board where TEM lines would be perfect: the limit the part states.</summary>
    [Fact]
    public void TheSchiffman_HoldsNinetyDegreesOverTheBand()
    {
        var d = SchiffmanPlan.Build(50.0, F0, 90.0, 1.5, Ro4350b);
        double worst = 0.0;
        foreach (double f in new[] { 1.92e9, 2.16e9, 2.4e9, 2.64e9, 2.88e9 })
        {
            var s = SAt(Schiffman(), f, 4);
            double diff = Wrap(Deg(s[1, 0]) - Deg(s[3, 2]));
            output.WriteLine($"{f / 1e9:F2} GHz: S11 {Db(s[0, 0]):F2} S33 {Db(s[2, 2]):F2} dB, S21 {Db(s[1, 0]):F2} S43 "
                           + $"{Db(s[3, 2]):F2} dB, phase difference {diff:F2} deg");
            worst = Math.Max(worst, Math.Abs(diff - 90.0));
            Assert.True(Db(s[0, 0]) < -10.0, $"|S11| {Db(s[0, 0]):F2} dB at {f / 1e9} GHz");
        }
        output.WriteLine($"rho {d.Rho:F3}, TEM ripple ±{d.RippleDeg:F2} deg; worst on the board {worst:F2} deg");
        Assert.True(worst < 3.0 * d.RippleDeg + 1.0, $"worst {worst:F2} deg");
        Assert.Equal(90.0, Wrap(Deg(SAt(Schiffman(), F0, 4)[1, 0]) - Deg(SAt(Schiffman(), F0, 4)[3, 2])), 1);
    }
}
