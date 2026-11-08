// Gates for brief-de-2: the Deembed block is the network that undoes its file's, it cancels an SnP on the same file in every
// analysis, and it refuses by name what it cannot do.

using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Stability;
using CircuitRF.Engine.HarmonicBalance;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

public sealed class DeembedTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("deembed-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── Test networks, made by circuitRF from lumped parts and written as Touchstone ─────────────────────────────────

    /// <summary>A lossy, mismatched two-port: series L and R, shunt C, series L, shunt C.</summary>
    private const string Net2 = """
        Port:P1 o1 0 Num=1 Z=50 Ohm
        Port:P2 i1 0 Num=2 Z=50 Ohm
        L:L1 o1 a L=0.4 nH
        R:R1 a b R=3 Ohm
        C:C1 b 0 C=0.4 pF
        L:L2 b i1 L=0.3 nH
        C:C2 i1 0 C=0.2 pF
        """;

    /// <summary>An asymmetric coupled four-port: ports 1, 2 one side, 3, 4 the other.</summary>
    private const string Net4 = """
        Port:P1 o1 0 Num=1 Z=50 Ohm
        Port:P2 o2 0 Num=2 Z=50 Ohm
        Port:P3 i1 0 Num=3 Z=50 Ohm
        Port:P4 i2 0 Num=4 Z=50 Ohm
        R:R1 o1 a1 R=2 Ohm
        L:L1 a1 i1 L=0.5 nH
        L:L2 o2 a2 L=0.3 nH
        R:R2 a2 i2 R=4 Ohm
        C:C1 a1 0 C=0.3 pF
        C:C2 i2 0 C=0.1 pF
        C:Cc a1 i2 C=0.05 pF
        Mutual:M1 Inductor1="L1" Inductor2="L2" M=0.1 nH
        """;

    /// <summary>A FET's extrinsic six-port (gate, drain, source outside; then inside), with a DC path through every line.</summary>
    private const string Net6 = """
        Port:P1 P1 0 Num=1 Z=50 Ohm
        Port:P2 P2 0 Num=2 Z=50 Ohm
        Port:P3 P3 0 Num=3 Z=50 Ohm
        Port:P4 P4 0 Num=4 Z=50 Ohm
        Port:P5 P5 0 Num=5 Z=50 Ohm
        Port:P6 P6 0 Num=6 Z=50 Ohm
        C:C1 P1 P3 C=7 fF
        C:C2 n1 n2 C=4.9 fF
        L:L1 P1 n1 L=45 pH
        R:R1 P4 n1 R=5.2 Ohm
        C:C3 n1 n3 C=1 fF
        C:C4 P1 P2 C=0.5 fF
        R:R2 n3 P5 R=9.2 Ohm
        R:R3 n2 P6 R=6.6 Ohm
        L:L2 P2 n3 L=52 pH
        L:L3 P3 n2 L=6.5 pH
        C:C5 n3 n2 C=21 fF
        C:C6 P2 P3 C=7 fF
        Mutual:M1 Inductor1="L1" Inductor2="L2" M=1 pH
        """;

    private static readonly double[] Grid = [.. Enumerable.Range(0, 19).Select(i => 1e9 + i * 0.5e9)];

    /// <summary>Solves a lumped netlist at <paramref name="freqs"/> and writes it as a Touchstone file.</summary>
    private string Write(string name, string netlist, double[] freqs)
    {
        var (lib, tb) = new CnlReader().Read(netlist);
        var nl = new Elaborator(lib).Elaborate(tb);
        var snp = DataSetBuilder.ToSnp(SParameterEngine.Run(nl, freqs));
        snp.Format = MatrixFormat.RI;
        string path = Path.Combine(_dir, name);
        TouchstoneIO.WriteFile(snp, path, precision: "G17");
        return path;
    }

    private static SNP S(string netlist, double[] freqs)
    {
        var (lib, tb) = new CnlReader().Read(netlist);
        return DataSetBuilder.ToSnp(SParameterEngine.Run(new Elaborator(lib).Elaborate(tb), freqs));
    }

    private static string Nets(string prefix, int k, int from = 1) => string.Join(" ", Enumerable.Range(from, k).Select(i => $"{prefix}{i}"));

    /// <summary>Ports 1…K on the outer nodes, K+1…2K on the far side, and the two blocks between them in the stated order.</summary>
    private static string Pair(string file, int ports, bool deembedFirst, string extra = "")
    {
        int k = ports / 2;
        string first  = deembedFirst ? "Deembed" : "SnP";
        string second = deembedFirst ? "SnP" : "Deembed";
        var lines = new List<string>();
        for (int i = 1; i <= k; i++) lines.Add($"Port:P{i} o{i} 0 Num={i} Z=50 Ohm");
        for (int i = 1; i <= k; i++) lines.Add($"Port:P{k + i} q{i} 0 Num={k + i} Z=50 Ohm");
        lines.Add($"{first}:X1 {Nets("o", k)} {Nets("m", k)} File=\"{file}\" NumPorts={ports} {extra}");
        lines.Add($"{second}:X2 {Nets("m", k)} {Nets("q", k)} File=\"{file}\" NumPorts={ports} {extra}");
        return string.Join("\n", lines);
    }

    private static double ThroughError(SNP s)
    {
        int n = s.Ports, k = n / 2;
        double worst = 0;
        foreach (var m in s.Matrices)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    Complex want = (j == (i + k) % n) ? Complex.One : Complex.Zero;
                    worst = Math.Max(worst, (m[i, j] - want).Magnitude);
                }
        return worst;
    }

    // ── 1. Cancellation, S-parameters ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far an SnP on <paramref name="file"/>, alone between ports, is from the file's own interpolated S at
    /// <paramref name="f"/>. Normally rounding (1e-15). Where the file's Z is large but under the SnP's switch to its S-wave
    /// stamp (|Z| up to 1e6·Z0), the SnP's Z stamp is off by up to ~1e-10, and that is the SnP's own error: a Deembed reproduces
    /// the inverse of the interpolated S exactly, so the pair can cancel no better than the SnP stamps.
    /// </summary>
    private static double SnpOwnError(string file, int ports, double f, string extra = "",
                                      InterpolationMethod method = InterpolationMethod.CubicSpline,
                                      InterpolationFormat format = InterpolationFormat.MagPhase)
    {
        var lines = new List<string>();
        for (int i = 1; i <= ports; i++) lines.Add($"Port:P{i} n{i} 0 Num={i} Z=50 Ohm");
        lines.Add($"SnP:X1 {Nets("n", ports)} File=\"{file}\" NumPorts={ports} {extra}");
        var alone = S(string.Join("\n", lines), [f]).Matrices[0];
        var want = TouchstoneCache.GetInterpolator(file, method, format, MatrixType.S, OutOfRangePolicy.WarnClamp).Evaluate(f);
        double e = 0;
        for (int i = 0; i < ports; i++) for (int j = 0; j < ports; j++) e = Math.Max(e, (alone[i, j] - want[i, j]).Magnitude);
        return e;
    }

    /// <summary>On the file's own points, between them, below its band and above it, both orders, at 2, 4 and 6 ports:
    /// the pair is a through connection to rounding, plus at most twice what the SnP alone is off its own file there.</summary>
    [Theory]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    public void AnSnpAndADeembedOnOneFile_AreAThroughConnection_AtEveryFrequency(int ports, bool deembedFirst)
    {
        string file = Write($"f{ports}.s{ports}p", ports switch { 2 => Net2, 4 => Net4, _ => Net6 }, Grid);
        foreach (double f in new[] { 2e9, 2.37e9, 9.91e9, 0.3e9, 13e9 })
        {
            double err = ThroughError(S(Pair(file, ports, deembedFirst), [f]));
            double own = SnpOwnError(file, ports, f);
            output.WriteLine($"{ports}-port, deembed first {deembedFirst}, {f / 1e9} GHz: pair {err:E2}, SnP alone {own:E2}");
            // Twice, because a rounding error of the SnP's Z stamp depends on the matrix around it, so it is not one number:
            // 1.96e-10 alone, 1.39e-10 and 2.17e-10 in the two orders of the 6-port pair at 9.91 GHz.
            Assert.True(err <= 1e-12 + 2 * own, $"{f / 1e9} GHz: max |S − through| = {err:E2}, the SnP alone is off its file by {own:E2}");
        }
    }

    /// <summary>Where the SnP stamps its file to rounding, so does the pair: 2- and 4-ports everywhere, the 6-port on its grid.</summary>
    [Theory]
    [InlineData(2)] [InlineData(4)] [InlineData(6)]
    public void WhereTheSnpIsExact_ThePairCancelsToRounding(int ports)
    {
        string file = Write($"f{ports}.s{ports}p", ports switch { 2 => Net2, 4 => Net4, _ => Net6 }, Grid);
        double[] at = ports == 6 ? [1e9, 2e9, 6.5e9, 10e9] : [2e9, 2.37e9, 9.91e9, 0.3e9, 13e9];
        double err = ThroughError(S(Pair(file, ports, false), at));
        Assert.True(err <= 1e-12, $"max |S − through| = {err:E2}");
    }

    [Fact]
    public void TheCancellationHolds_WithLinearRealImagInterpolationOnBoth()
    {
        string file = Write("f4.s4p", Net4, Grid);
        const string Settings = "InterpMode=\"linear\" InterpDomain=\"RI\"";
        foreach (double f in new[] { 2.37e9, 7.77e9 })
        {
            double err = ThroughError(S(Pair(file, 4, false, Settings), [f]));
            double own = SnpOwnError(file, 4, f, Settings, InterpolationMethod.Linear, InterpolationFormat.RealImag);
            Assert.True(err <= 1e-12 + 2 * own, $"{f / 1e9} GHz: max |S − through| = {err:E2}, SnP alone {own:E2}");
        }
    }

    // ── 2. Cancellation, DC ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AtDC_APartBehindTheCancellingPair_SeesWhatItSeesAlone()
    {
        string file = Write("f6dc.s6p", Net6, [0.0, .. Grid]);
        const string Drive = """
            V:V1 s1 0 V=1
            R:Rs1 s1 o1 R=50 Ohm
            V:V2 s2 0 V=0.4
            R:Rs2 s2 o2 R=20 Ohm
            R:Rs3 o3 0 R=1 Ohm
            """;
        static string Load(string a, string b, string c) => $"""
            R:Ra {a} 0 R=100 Ohm
            R:Rb {b} 0 R=30 Ohm
            R:Rab {a} {b} R=70 Ohm
            R:Rc {c} 0 R=5 Ohm
            R:Rbc {b} {c} R=40 Ohm
            """;
        string paired = $"{Drive}\nSnP:X1 o1 o2 o3 m1 m2 m3 File=\"{file}\" NumPorts=6\nDeembed:X2 m1 m2 m3 q1 q2 q3 File=\"{file}\" NumPorts=6\n{Load("q1", "q2", "q3")}";
        string alone  = $"{Drive}\n{Load("o1", "o2", "o3")}";

        double[] V(string text, params string[] nodes)
        {
            var (lib, tb) = new CnlReader().Read(text);
            var nl = new Elaborator(lib).Elaborate(tb);
            // gmin 0: the paired circuit has three more nodes than the part alone, and gmin's 1e-12 S on each would be the
            // whole difference (2.6e-11 V at node 1). Every node here has a DC path without it.
            var r = LinearDcEngine.Run(nl, gmin: 0);
            Assert.True(r.Solution is not null, r.Refusal);
            return [.. nodes.Select(n => r.Solution!.VoltageAt(nl.Nodes.IndexOf(n)))];
        }
        var withPair = V(paired, "q1", "q2", "q3");
        var without  = V(alone, "o1", "o2", "o3");
        for (int i = 0; i < 3; i++)
        {
            output.WriteLine($"node {i + 1}: {withPair[i]:R} vs {without[i]:R}");
            Assert.True(Math.Abs(withPair[i] - without[i]) <= 1e-12, $"node {i + 1}: {withPair[i]} vs {without[i]}");
        }
    }

    // ── 3. Cancellation, HB ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InHarmonicBalance_ANonlinearPartBehindTheCancellingPair_RunsAsItDoesAlone()
    {
        string file = Write("f2.s2p", Net2, Grid);
        static string Circuit(string drainSide) => $"""
            TV0 = 3.5
            B   = 0.02
            Sc  = 0.3
            SDD:M1  g 0  d 0  Ports=2
              I[1,0]=0
              I[2,0]=if(_v1+TV0>0, B*(_v1+TV0)^2*tanh(Sc*_v2), 0)
            V_1Tone:Vs gb 0 Vdc=-3 Freq=2e9 V=0.4 Phase=0
            L:Lg gb g L=1 R=0
            V:Vdd db 0 V=28
            L:Ld db d L=1 R=0
            {drainSide}
            R:Rload out 0 R=50
            analysis HB1 type=hb Tone=2e9 MaxHarm=3 Tol=1e-9
            """;
        DataCube Run(string text)
        {
            var (lib, tb) = new CnlReader().Read(text);
            var nl = new Elaborator(lib).Elaborate(tb);
            var p = HbEngine.Resolve(tb.Analyses.OfType<HarmonicBalanceAnalysis>().First(), nl.ResolvedGlobals);
            return ((DataSet)new HbEngine(nl, tb).Run(p))["V"];
        }
        var paired = Run(Circuit($"SnP:X1 d m File=\"{file}\" NumPorts=2\nDeembed:X2 m out File=\"{file}\" NumPorts=2"));
        var alone  = Run(Circuit("R:Rshort d out R=1e-9 Ohm"));
        int Row(DataCube c, string n) => Array.IndexOf(c.Axes[0].Labels!, n);
        foreach (int h in new[] { 1, 3 })
        {
            Complex a = (Complex)paired[Row(paired, "out"), h], b = (Complex)alone[Row(alone, "out"), h];
            output.WriteLine($"harmonic {h}: {a} vs {b}");
            Assert.True((a - b).Magnitude <= 1e-6 * Math.Max(b.Magnitude, 1e-3), $"harmonic {h}: {a} vs {b}");
        }
    }

    // ── 4. Recovery ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TwoDeembedsAroundAMeasurement_ReturnThePartBetweenTheFixtures()
    {
        string fa = Write("a.s2p", Net2, Grid);
        var a = S(Net2, Grid);
        var b = S(Net2.Replace("0.4 pF", "0.25 pF").Replace("3 Ohm", "1.5 Ohm"), Grid);   // a different fixture
        string fb = Path.Combine(_dir, "b.s2p");
        b.Format = MatrixFormat.RI;
        TouchstoneIO.WriteFile(b, fb, precision: "G17");
        var dut = S(Net2.Replace("0.4 nH", "1.1 nH").Replace("0.2 pF", "0.6 pF"), Grid);

        var measured = new Mat<Complex>[Grid.Length];
        for (int k = 0; k < Grid.Length; k++)
            measured[k] = NetworkInverse.Cascade(NetworkInverse.Cascade(a.Matrices[k], dut.Matrices[k]), b.Matrices[k]);
        string fm = Path.Combine(_dir, "m.s2p");
        TouchstoneIO.WriteFile(new SNP(Grid, measured, MatrixType.S, MatrixFormat.RI), fm, precision: "G17");

        var got = S($"""
            Port:P1 p1 0 Num=1 Z=50 Ohm
            Port:P2 p2 0 Num=2 Z=50 Ohm
            Deembed:DA p1 x File="{fa}" NumPorts=2
            SnP:M x y File="{fm}" NumPorts=2
            Deembed:DB y p2 File="{fb}" NumPorts=2
            """, Grid);
        double worst = got.Matrices.Zip(dut.Matrices, (g, d) =>
            Enumerable.Range(0, 4).Max(e => (g[e / 2, e % 2] - d[e / 2, e % 2]).Magnitude)).Max();
        output.WriteLine($"recovered DUT, max |ΔS| = {worst:E2}");
        Assert.True(worst <= 1e-12, $"max |ΔS| = {worst:E2}");
    }

    // ── 5. Refusals ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnOddPortCount_IsRefusedWhenRead()
    {
        var ex = Assert.Throws<CnlReadException>(() => new CnlReader().Read("Deembed:D1 a b c File=\"x.s3p\" NumPorts=3"));
        Assert.Contains("even number of ports", ex.Message);
    }

    [Fact]
    public void AFixtureWithNoDcPath_IsRefusedAtZeroHertz_NamingTheInstance()
    {
        string file = Write("block.s2p", """
            Port:P1 a 0 Num=1 Z=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            C:C1 a b C=10 pF
            """, [0.0, 1e9, 2e9]);
        var (lib, tb) = new CnlReader().Read($"""
            V:V1 s 0 V=1
            R:R1 s a R=50 Ohm
            Deembed:DB a b File="{file}" NumPorts=2
            R:R2 b 0 R=50 Ohm
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ex = Assert.Throws<DeviceRefusalException>(() => LinearDcEngine.Run(nl));
        Assert.Equal("deembed.not-invertible", ex.Diagnostic.Id);
        output.WriteLine(ex.Message);
        Assert.Contains("Deembed 'DB'", ex.Message);
        Assert.Contains("0 Hz", ex.Message);
        Assert.Contains("no DC path", ex.Message);
    }

    [Fact]
    public void AnSParameterOffset_IsRefused()
    {
        string file = Write("f2.s2p", Net2, Grid);
        var (lib, tb) = new CnlReader().Read($"Deembed:D1 a b File=\"{file}\" NumPorts=2 SdB_2_1=0.5");
        var ex = Assert.ThrowsAny<Exception>(() => new Elaborator(lib).Elaborate(tb));
        Assert.Contains("SdB_2_1", ex.Message);
        Assert.Contains("does not take", ex.Message);
    }

    [Fact]
    public void Ndf_RefusesADeembed_ByName_AndSaysWhy()
    {
        string file = Write("f2.s2p", Net2, Grid);
        var (lib, tb) = new CnlReader().Read($"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            Deembed:DX a b File="{file}" NumPorts=2
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        var ndf = new NdfRequest { PassiveVars = [], PassiveParams = [], PassiveNetlist = () => NdfPassivation.BuildPassiveNetlist(lib, tb, null, [], []) };
        var ex = Assert.Throws<InvalidOperationException>(() => SParameterEngine.Run(nl, [1e9, 2e9], settings: null, control: null, ndf));
        output.WriteLine(ex.Message);
        Assert.Contains("DX", ex.Message);
        Assert.Contains("inverse of the network", ex.Message);
    }

    // ── A path through a floating network, grounded at both ends (DE-5) ─────────────────────────────────────────────

    /// <summary>
    /// A FET's six-port behind a Deembed of itself, with a linear core inside it whose source is the inner source (pin 6 of
    /// the SnP). <paramref name="source"/> is the net the device's outer source and the Deembed's pin 6 share. The core alone
    /// sits on ports 3 and 4, its source on ground: the answer the pair should give on ports 1 and 2.
    /// </summary>
    private (SNP S, IReadOnlyList<string> Warnings) FetBehindItsNetwork(string source)
    {
        string file = Write("fet6.s6p", Net6, Grid);
        var (lib, tb) = new CnlReader().Read($"""
            Port:P1 g1 0 Num=1 Z=50 Ohm
            Port:P2 d1 0 Num=2 Z=50 Ohm
            Deembed:DE g1 d1 0 G D {source} File="{file}" NumPorts=6
            SnP:F G D {source} GI DI SI File="{file}" NumPorts=6
            C:Cgs1 GI SI C=0.5 pF
            C:Cgd1 GI DI C=0.1 pF
            R:Rds1 DI SI R=300 Ohm
            VCCS:Gm1 DI SI GI SI G=50 mS
            Port:P3 g2 0 Num=3 Z=50 Ohm
            Port:P4 d2 0 Num=4 Z=50 Ohm
            C:Cgs2 g2 0 C=0.5 pF
            C:Cgd2 g2 d2 C=0.1 pF
            R:Rds2 d2 0 R=300 Ohm
            VCCS:Gm2 d2 0 g2 0 G=50 mS
            """);
        var nl = new Elaborator(lib).Elaborate(tb);
        return (DataSetBuilder.ToSnp(SParameterEngine.Run(nl, Grid)), nl.Warnings);
    }

    private static double CoreError(SNP s) => s.Matrices.Max(m =>
        Enumerable.Range(0, 2).SelectMany(i => Enumerable.Range(0, 2).Select(j => (m[i, j] - m[i + 2, j + 2]).Magnitude)).Max());

    [Fact]
    public void ADevicesSourceOnANetOfItsOwn_ToThePartSidePin_ReturnsTheCore()
    {
        var (s, warnings) = FetBehindItsNetwork("S0");
        output.WriteLine($"max |dS| {CoreError(s):E2}");
        Assert.True(CoreError(s) < 1e-12);
        Assert.DoesNotContain(warnings, w => w.Contains("both on ground"));
    }

    [Fact]
    public void BothEndsOfAPath_OnGround_IsNamed_BecauseThePathIsNotRemoved()
    {
        var (s, warnings) = FetBehindItsNetwork("0");
        output.WriteLine($"max |dS| {CoreError(s):E2}");
        Assert.True(CoreError(s) > 1e-2);   // ground takes up the difference between the two currents
        var w = Assert.Single(warnings, w => w.Contains("both on ground"));
        Assert.Contains("Deembed 'DE'", w);
        Assert.Contains("pins 3 and 6", w);
    }
}
