using System.Numerics;
using System.Text.Json;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Envelope;
using NumFlat;
using RfCore;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// The S-parameter noise analysis (<c>Noise=yes</c>, <see cref="SParameterNoise"/>): Bosma/Twiss thermal noise of passive
/// parts, the noise waves from transposed solves on the wave path's factorisation, and the cubes. The references are
/// closed forms (Bosma's <c>Cs = kT(I − S·Sᴴ)</c> for a passive network at one temperature, a matched pad's noise
/// figure) and testdata/noise/refs.json, written outside circuitRF by gen_noise_refs.py (numpy nodal and ngspice 47
/// <c>.sp … donoise</c>).
/// </summary>
public sealed class SParamNoiseTests(ITestOutputHelper output) : IDisposable
{
    private const double K = NoiseMetrics.Boltzmann;

    private readonly string _dir = Directory.CreateTempSubdirectory("sparam-noise-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string NoiseDir()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata", "noise"))) return Path.Combine(dir, "testdata", "noise");
        throw new DirectoryNotFoundException("testdata/noise not found above " + AppContext.BaseDirectory);
    }

    /// <summary>A bench's entry in refs.json.</summary>
    private static JsonElement Ref(string bench)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(NoiseDir(), "refs.json")));
        return doc.RootElement.GetProperty("benches").GetProperty(bench).Clone();
    }

    /// <summary>A bench's text with <c>Noise=yes</c> on its analysis line (the files leave it off).</summary>
    private static string Bench(string name) => WithNoise(File.ReadAllText(Path.Combine(NoiseDir(), name + ".cnl")));

    private static string WithNoise(string cnl, string value = "yes") => string.Join('\n',
        cnl.Split('\n').Select(l => l.TrimStart().StartsWith("analysis SP1", StringComparison.Ordinal)
            ? l.TrimEnd() + " Noise=" + value : l));

    private sealed record Run(DataSet Ds, double[] Freqs, ElaboratedNetlist Nl, SParameterEngine.RunStats Stats);

    /// <summary>Reads, elaborates and runs the netlist's S-parameter analysis with the settings its directive asks for.</summary>
    private static Run RunCnl(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, sourceDirectory: NoiseDir());
        var nl    = new Elaborator(lib).Elaborate(tb);
        var spa   = tb.Analyses.OfType<SParameterAnalysis>().First();
        var freqs = spa.Expand(nl.ResolvedGlobals);
        var (ds, stats) = SParameterEngine.RunWithStats(nl, freqs, AnalysisSettings.Default.WithNoise(spa.Noise));
        return new Run(ds, freqs, nl, stats);
    }

    private static Mat<Complex> At(DataCube cube, int fi, int n)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                m[i, j] = (Complex)cube[fi, i, j];
        return m;
    }

    /// <summary>max |a − b| / max |b| over the entries.</summary>
    private static double RelDiff(Mat<Complex> a, Mat<Complex> b)
    {
        double num = 0, den = 0;
        for (int i = 0; i < b.RowCount; i++)
            for (int j = 0; j < b.ColCount; j++)
            {
                num = Math.Max(num, (a[i, j] - b[i, j]).Magnitude);
                den = Math.Max(den, b[i, j].Magnitude);
            }
        return num / den;
    }

    /// <summary>Bosma's passive-network noise waves, <c>kT(I − S·Sᴴ)</c>.</summary>
    private static Mat<Complex> Bosma(Mat<Complex> s, double kelvin)
    {
        var c = s * s.ConjugateTranspose();
        var r = new Mat<Complex>(s.RowCount, s.RowCount);
        for (int i = 0; i < s.RowCount; i++)
            for (int j = 0; j < s.RowCount; j++)
                r[i, j] = K * kelvin * ((i == j ? 1 : 0) - c[i, j]);
        return r;
    }

    /// <summary>Every frequency's Cs against Bosma's at <paramref name="kelvin"/>.</summary>
    private static double WorstAgainstBosma(Run r, double kelvin)
    {
        int n = r.Ds["S"].Axes[1].Length;
        double worst = 0;
        for (int fi = 0; fi < r.Freqs.Length; fi++)
            worst = Math.Max(worst, RelDiff(At(r.Ds["Cs"], fi, n), Bosma(At(r.Ds["S"], fi, n), kelvin)));
        return worst;
    }

    // ── closed forms ──────────────────────────────────────────────────────────

    /// <summary>A matched π pad of loss L at T: <c>F = 1 + (T/T0)(L − 1)</c>, and every Cs entry Bosma's.</summary>
    [Theory]
    [InlineData("pad6_290K", 290.0)]
    [InlineData("pad6_400K", 400.0)]
    public void MatchedPad_MeetsTheClosedForm(string bench, double kelvin)
    {
        var r = RunCnl(Bench(bench));
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            var s = At(r.Ds["S"], fi, 2);
            double loss = 1 / (s[1, 0].Magnitude * s[1, 0].Magnitude);
            double f = 1 + kelvin / NoiseMetrics.T0 * (loss - 1);
            Assert.Equal(10 * Math.Log10(f), r.Ds["NF"].RealValues[fi], 1e-9);
            Assert.Equal(10 * Math.Log10(f), r.Ds["NFmin"].RealValues[fi], 1e-9);   // matched: Γopt = 0
        }
        Assert.True(WorstAgainstBosma(r, kelvin) < 1e-9, $"Cs vs kT(I − SSᴴ): {WorstAgainstBosma(r, kelvin):E2}");
    }

    /// <summary>
    /// An unmatched lossy R/L/C two-port at the default 300 K: every Cs entry is Bosma's — the off-diagonals are complex,
    /// so a missing conjugate in the transposed solve shows here — and <c>F = 1 + (T/T0)(1/G_A − 1)</c>.
    /// </summary>
    [Fact]
    public void LossyReactiveNetwork_IsBosmaEntryByEntry()
    {
        var r = RunCnl(Bench("lossy_lc"));
        double worst = WorstAgainstBosma(r, 300.0);
        output.WriteLine($"lossy_lc: worst |Cs − kT(I − SSᴴ)| / max = {worst:E2}");
        Assert.True(worst < 1e-12, $"{worst:E2}");
        Assert.True(Math.Abs(((Complex)r.Ds["Cs"][0, 0, 1]).Imaginary) > 1e-3 * ((Complex)r.Ds["Cs"][0, 0, 0]).Magnitude,
                    "the bench should have complex off-diagonal noise");

        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            var s = At(r.Ds["S"], fi, 2);
            double ga = Sq(s[1, 0]) / (1 - Sq(s[1, 1]));
            double f = 1 + 300.0 / NoiseMetrics.T0 * (1 / ga - 1);
            Assert.Equal(10 * Math.Log10(f), r.Ds["NF"].RealValues[fi], 1e-9);
        }
    }

    /// <summary>Lossy inductors coupled by a Mutual are one network: recorded apart, the coupling would be lost.</summary>
    [Fact]
    public void CoupledLossyInductors_AreOneBosmaNetwork()
    {
        var r = RunCnl("""
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            L:L1  p1  0  L=5 nH R=3 Ohm
            L:L2  p2  0  L=8 nH R=4 Ohm
            Mutual:K1  Inductor1="L1" Inductor2="L2" M=4.4 nH
            C:C1  p1  p2  C=0.25 pF
            analysis SP1 type=sparam start=0.5 stop=5 npts=10 Unit=GHz Noise=yes
            """);
        double worst = WorstAgainstBosma(r, 300.0);
        Assert.True(worst < 1e-12, $"{worst:E2}");
    }

    /// <summary>An ideal transformer leaves its terminated impedance singular; the pseudo-inverse must lose nothing.</summary>
    [Fact]
    public void IdealTransformerBetweenResistors_IsBosma()
    {
        var r = RunCnl("""
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            R:R1  p1  a  R=30 Ohm
            Transformer:T1  a 0 b 0  N=2
            R:R2  b  p2  R=20 Ohm
            R:R3  a  0  R=300 Ohm
            Short:S1  b  c
            R:R4  c  0  R=90 Ohm
            analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz Noise=yes
            """);
        double worst = WorstAgainstBosma(r, 300.0);
        Assert.True(worst < 1e-12, $"{worst:E2}");
    }

    /// <summary>
    /// Bosma/Twiss on one recorded stamp equals <c>2kT(Y + Yᴴ)</c> wherever the part has an admittance — Y here is the
    /// Schur complement of the recorded stamp onto its nodes, independent of the termination the code uses.
    /// </summary>
    [Theory]
    [InlineData("R:X a b R=75 Ohm")]
    [InlineData("R:X a 0 R=40 Ohm")]
    [InlineData("PRC:X a b R=120 Ohm C=1 pF")]
    [InlineData("PRLC:X a b R=120 Ohm L=4 nH C=1 pF")]
    [InlineData("L:X a b L=5 nH R=3 Ohm")]
    [InlineData("TLIN:X a b Z=60 Ohm E=70 deg F=1 GHz A=2")]
    public void OnePart_IsTwoKTTimesTheHermitianPartOfY(string line)
    {
        var ec = Part(line);
        double w = 2 * Math.PI * 1.7e9, kelvin = 300;
        var stamp = EnvelopeTangentCheck.Stamped(ec, w);
        var nc = SParameterNoise.Bosma(stamp, kelvin);
        var y = Schur(stamp, nc.Nodes);
        var expect = new Mat<Complex>(y.RowCount, y.RowCount);
        for (int i = 0; i < y.RowCount; i++)
            for (int j = 0; j < y.RowCount; j++)
                expect[i, j] = 2 * K * kelvin * (y[i, j] + Complex.Conjugate(y[j, i]));
        double d = RelDiff(nc.C, expect);
        output.WriteLine($"{line}: {d:E2}");
        Assert.True(d < 1e-10, $"{line}: {d:E2}");
    }

    /// <summary>A lossless part comes out at round-off, with no special case.</summary>
    [Fact]
    public void LosslessPart_IsNoiseless()
    {
        var nc = SParameterNoise.Bosma(EnvelopeTangentCheck.Stamped(Part("C:X a b C=1 pF"), 2 * Math.PI * 1e9), 300);
        double scale = 4 * K * 300 / EnvelopeTangentCheck.NodeSReference;
        for (int i = 0; i < nc.C.RowCount; i++)
            for (int j = 0; j < nc.C.ColCount; j++)
                Assert.True(nc.C[i, j].Magnitude < 1e-12 * scale, $"C[{i},{j}] = {nc.C[i, j]}");
    }

    /// <summary>
    /// A resistor with an ideal tie in the same recording (a short from one end to a third node) has no admittance. What
    /// the pseudo-inverse gives must drive every node of a network around it exactly as the resistor's own 4kT/R does.
    /// </summary>
    [Fact]
    public void IdealTie_PseudoInverse_IsExactWhereTheNetworkLooks()
    {
        var nl = Netlist("R:R1 a c R=80 Ohm\nShort:S1 a b");
        double w = 2 * Math.PI * 1e9, kelvin = 300;
        var stamp = EnvelopeTangentCheck.Recorded(m => { foreach (var ec in nl.Components) ec.Stamp(m, w); });
        var nc = SParameterNoise.Bosma(stamp, kelvin);
        int a = nl.Nodes.IndexOf("a"), b = nl.Nodes.IndexOf("b"), c = nl.Nodes.IndexOf("c");

        // The network: the recording plus a termination at each node, all noiseless.
        var ids = EnvelopeTangentCheck.Ids(stamp.Keys);
        int size = ids.Count;
        var m = new Complex[size, size];
        foreach (var (key, v) in stamp) m[ids.IndexOf(key >> 32), ids.IndexOf(key & 0xffffffffL)] += v;
        double[] g = [1 / 30.0, 1 / 70.0, 1 / 45.0];
        int[] nodes = [a, b, c];
        for (int k = 0; k < 3; k++) m[ids.IndexOf(nodes[k]), ids.IndexOf(nodes[k])] += g[k];
        var inv = EnvelopeEngine.Invert(m, size);

        Mat<Complex> Seen(int[] at, Mat<Complex> cj)
        {
            var h = new Mat<Complex>(3, at.Length);
            for (int r = 0; r < 3; r++)
                for (int q = 0; q < at.Length; q++)
                    h[r, q] = inv[ids.IndexOf(nodes[r]), ids.IndexOf(at[q])];
            return h * cj * h.ConjugateTranspose();
        }
        double gR = 4 * K * kelvin / 80;
        var own = new Mat<Complex>(2, 2);
        own[0, 0] = gR; own[1, 1] = gR; own[0, 1] = -gR; own[1, 0] = -gR;
        double d = RelDiff(Seen(nc.Nodes, nc.C), Seen([a, c], own));
        Assert.True(d < 1e-10, $"{d:E2}");
    }

    // ── references (testdata/noise/refs.json) ─────────────────────────────────

    /// <summary>NF, NFmin, Rn, Γopt and Cs against the generator's references, at its tolerances.</summary>
    [Theory]
    [InlineData("pad6_290K")]
    [InlineData("pad6_400K")]
    [InlineData("lossy_lc")]
    [InlineData("ngspice_mixed_temp")]
    [InlineData("ngspice_coupled_l")]
    [InlineData("ngspice_vccs_amp")]
    [InlineData("friis_matched")]
    [InlineData("friis_mismatched")]
    [InlineData("touchstone_r50")]
    [InlineData("touchstone_r25")]
    [InlineData("touchstone_r25_in_50")]
    [InlineData("pozar_ex12_5")]
    public void Bench_MatchesItsReference(string bench)
    {
        var b = Ref(bench);
        var e = b.GetProperty("expected");
        var tol = b.GetProperty("tolerance");
        double[] Arr(JsonElement j) => [.. j.EnumerateArray().Select(x => x.GetDouble())];

        var r = RunCnl(Bench(bench));
        Assert.Equal(Arr(b.GetProperty("freq_hz")), r.Freqs);

        double[] nf = Arr(e.GetProperty("nf_db")), nfMin = Arr(e.GetProperty("nfmin_db")), rn = Arr(e.GetProperty("rn_ohm"));
        double[] gRe = Arr(e.GetProperty("gamma_opt_re")), gIm = Arr(e.GetProperty("gamma_opt_im"));
        var csRe = e.GetProperty("cs_re"); var csIm = e.GetProperty("cs_im");
        double worst = 0, dNf = 0, dRn = 0, dGamma = 0;
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            dNf    = Math.Max(dNf, Math.Max(Math.Abs(nf[fi] - r.Ds["NF"].RealValues[fi]), Math.Abs(nfMin[fi] - r.Ds["NFmin"].RealValues[fi])));
            dRn    = Math.Max(dRn, Math.Abs(r.Ds["Rn"].RealValues[fi] - rn[fi]) / Math.Abs(rn[fi]));
            dGamma = Math.Max(dGamma, (r.Ds["GammaOpt"].ComplexValues[fi] - new Complex(gRe[fi], gIm[fi])).Magnitude);
            Assert.Equal(nf[fi],    r.Ds["NF"].RealValues[fi],    tol.GetProperty("db_abs").GetDouble());
            Assert.Equal(nfMin[fi], r.Ds["NFmin"].RealValues[fi], tol.GetProperty("db_abs").GetDouble());
            Assert.True(Math.Abs(r.Ds["Rn"].RealValues[fi] - rn[fi]) <= tol.GetProperty("rn_rel").GetDouble() * Math.Abs(rn[fi]),
                        $"Rn {r.Ds["Rn"].RealValues[fi]} vs {rn[fi]} at {r.Freqs[fi]:G4} Hz");
            Assert.True((r.Ds["GammaOpt"].ComplexValues[fi] - new Complex(gRe[fi], gIm[fi])).Magnitude
                        <= tol.GetProperty("gamma_abs").GetDouble(), $"GammaOpt at {r.Freqs[fi]:G4} Hz");

            var expect = new Mat<Complex>(2, 2);
            for (int i = 0; i < 2; i++)
                for (int j = 0; j < 2; j++)
                    expect[i, j] = new Complex(csRe[fi][i][j].GetDouble(), csIm[fi][i][j].GetDouble());
            worst = Math.Max(worst, RelDiff(At(r.Ds["Cs"], fi, 2), expect));
        }
        output.WriteLine($"{bench}: worst NF/NFmin {dNf:E2} dB, Rn rel {dRn:E2}, GammaOpt {dGamma:E2}, Cs rel {worst:E2}");
        Assert.True(worst <= tol.GetProperty("cs_rel").GetDouble(), $"Cs rel {worst:E2}");
    }

    /// <summary>The controlled source has no noise model: the run says so, by name, once.</summary>
    [Fact]
    public void ActivePart_IsNamedAsNoiseless()
    {
        var r = RunCnl(Bench("ngspice_vccs_amp"));
        var w = Assert.Single(r.Nl.Warnings, x => x.StartsWith("Noise:", StringComparison.Ordinal));
        Assert.Contains("VCCS:G1", w);
        Assert.DoesNotContain("R:Rf", w);
    }

    /// <summary>NF (port 1 driven from its Z0) is F(Γs = 0) of the reported parameters, unequal port Z0 included.</summary>
    [Fact]
    public void NF_IsTheNoiseFactorOfTheReportedParametersAtGammaZero()
    {
        var r = RunCnl(Bench("ngspice_vccs_amp"));
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            var p = new TwoPortNoise(Math.Pow(10, r.Ds["NFmin"].RealValues[fi] / 10), r.Ds["GammaOpt"].ComplexValues[fi],
                                     r.Ds["Rn"].RealValues[fi], 75.0);
            Assert.Equal(r.Ds["NF"].RealValues[fi], 10 * Math.Log10(NoiseMetrics.NoiseFactor(p, Complex.Zero)), 1e-9);
        }
    }

    // ── the knob, the run's shape ─────────────────────────────────────────────

    /// <summary>Off (absent or no) leaves the run as it was; on adds cubes and N solves per frequency and changes no S.</summary>
    [Fact]
    public void Off_ChangesNothing_On_AddsCubesAndSolvesOnly()
    {
        string text = File.ReadAllText(Path.Combine(NoiseDir(), "lossy_lc.cnl"));
        var absent = RunCnl(text);
        var no     = RunCnl(WithNoise(text, "no"));
        var on     = RunCnl(WithNoise(text));

        Assert.Equal(new[] { "S", "Z0" }, absent.Ds.Cubes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(absent.Ds.Cubes.Keys, no.Ds.Cubes.Keys);
        Assert.Equal(absent.Stats, no.Stats);
        Assert.Equal(absent.Ds["S"].ComplexValues, no.Ds["S"].ComplexValues);
        Assert.Empty(absent.Nl.Warnings);

        Assert.Equal(absent.Ds["S"].ComplexValues, on.Ds["S"].ComplexValues);    // bit for bit
        Assert.Equal(absent.Ds["Z0"].ComplexValues, on.Ds["Z0"].ComplexValues);
        Assert.Equal(absent.Stats.Factorizations, on.Stats.Factorizations);
        Assert.Equal(absent.Stats.PatternBuilds, on.Stats.PatternBuilds);
        Assert.Equal(absent.Stats.BackSubstitutions + 2 * absent.Freqs.Length, on.Stats.BackSubstitutions);
        foreach (var name in new[] { "Cs", "NF", "NFmin", "Rn", "GammaOpt" }) Assert.True(on.Ds.Contains(name), name);
        Assert.Equal(new[] { "freq", "i", "j" }, on.Ds["Cs"].Axes.Select(a => a.Name));
        Assert.Empty(on.Nl.Warnings);
    }

    /// <summary>The frequency-parallel path gives the serial path's noise doubles.</summary>
    [Fact]
    public void ParallelRun_IsBitIdenticalToSerial()
    {
        string cnl = Bench("ngspice_coupled_l").Replace("npts=10", "npts=200");
        var serial = RunCnl(cnl);

        var (lib, tb) = new CnlReader().Read(cnl);
        using var nl = new Elaborator(lib).Elaborate(tb);
        var parallel = SParameterEngine.Run(nl, lib, tb, null, serial.Freqs, AnalysisSettings.Default.WithNoise(true),
                                            control: null, maxDegreeOfParallelism: 3);
        Assert.Equal(3, SParameterEngine.PlanDegree(nl, serial.Freqs.Length, 3));
        foreach (var name in new[] { "S", "Cs", "GammaOpt" })
            Assert.Equal(serial.Ds[name].ComplexValues, parallel[name].ComplexValues);
        foreach (var name in new[] { "NF", "NFmin", "Rn" })
            Assert.Equal(serial.Ds[name].RealValues, parallel[name].RealValues);
        Assert.Equal(serial.Nl.Warnings, nl.Warnings);
    }

    /// <summary>A port with no resistive reference terminates nothing: the run refuses before it starts.</summary>
    [Fact]
    public void ReactiveReference_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RunCnl("""
            Port:P1  n1 0  Num=1  Z=(0+50j) Ohm
            Port:P2  n2 0  Num=2  Z=50 Ohm
            R:R1     n1 n2 R=75 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz Noise=yes
            """));
        Assert.Contains("Noise=yes", ex.Message);
        Assert.Contains("port 1", ex.Message);
    }

    /// <summary>A parametric sweep stacks the noise cubes like any other.</summary>
    [Fact]
    public void ParametricSweep_StacksTheNoiseCubes()
    {
        string cnl = Bench("ngspice_mixed_temp").Replace("R:R1  p1  m  R=30.0 Ohm", "R:R1  p1  m  R=Rv Ohm");
        var single = RunCnl("Rv = 40\n" + cnl);
        var (lib, tb) = new CnlReader().Read("Rv = 30\n" + cnl + "\nanalysis SW1 type=parametric_sweep Var=Rv Values=20,40 Inner=SP1");
        var ds = ParametricSweepEngine.Run(tb.Analyses.OfType<ParametricSweepAnalysis>().First(), lib, tb);

        Assert.Equal(new[] { "Rv", "freq" }, ds["NF"].Axes.Select(a => a.Name));
        Assert.Equal(new[] { "Rv", "freq", "i", "j" }, ds["Cs"].Axes.Select(a => a.Name));
        int nf = single.Freqs.Length;
        for (int fi = 0; fi < nf; fi++)
            Assert.Equal(single.Ds["NF"].RealValues[fi], ds["NF"].RealValues[nf + fi], 1e-12);
    }

    /// <summary><c>circuitrf sparam bench.cnl -o x.s2p</c> with Noise=yes writes the two-port noise block.</summary>
    [Fact]
    public void Cli_WritesTheTouchstoneNoiseBlock()
    {
        var snp = CliSparam(Bench("lossy_lc"));
        var r = RunCnl(Bench("lossy_lc"));
        Assert.Equal(r.Freqs.Length, snp.NoiseParameters.Count);
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            var row = snp.NoiseParameters[fi];
            Assert.Equal(r.Freqs[fi], row.FrequencyHz, 1e-3);
            Assert.Equal(r.Ds["NFmin"].RealValues[fi], row.NFminDb, 1e-9);
            Assert.True((r.Ds["GammaOpt"].ComplexValues[fi] - row.GammaOpt).Magnitude < 1e-9);
            Assert.Equal(r.Ds["Rn"].RealValues[fi], row.RnNormalized * snp.Z0.Real, 1e-9 * r.Ds["Rn"].RealValues[fi]);
        }
    }

    /// <summary>An S-parameter block's noise data goes through the run and out of the CLI as it came in.</summary>
    [Fact]
    public void Cli_WritesBackTheNoiseDataItRead()
    {
        var written = CliSparam(Bench("touchstone_r50"), "ampA.s2p");
        var read = TouchstoneIO.ReadFile(Path.Combine(NoiseDir(), "ampA.s2p"));
        Assert.Equal(read.NoiseParameters.Count, written.NoiseParameters.Count);
        Assert.Equal(read.Z0, written.Z0);
        for (int k = 0; k < read.NoiseParameters.Count; k++)
        {
            NoiseParameterPoint a = read.NoiseParameters[k], b = written.NoiseParameters[k];
            Assert.Equal(a.FrequencyHz, b.FrequencyHz, 1e-3);
            Assert.Equal(a.NFminDb, b.NFminDb, 1e-9);
            Assert.True((a.GammaOpt - b.GammaOpt).Magnitude < 1e-9, $"GammaOpt row {k}: {a.GammaOpt} vs {b.GammaOpt}");
            Assert.Equal(a.RnNormalized, b.RnNormalized, 1e-9);
        }
    }

    /// <summary>Runs <c>circuitrf sparam bench.cnl -o x.s2p</c> in a scratch folder holding <paramref name="files"/> from
    /// testdata/noise, and reads x.s2p back.</summary>
    private SNP CliSparam(string cnlText, params string[] files)
    {
        string cliDll = Path.GetFullPath(Path.Combine(
            System.Reflection.CustomAttributeExtensions
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(SParamNoiseTests).Assembly)
                .First(a => a.Key == "CliDir").Value!, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(cliDll), $"the CLI was not built beside these tests: {cliDll}");

        string dir = Path.Combine(Path.GetTempPath(), $"noise-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string f in files) File.Copy(Path.Combine(NoiseDir(), f), Path.Combine(dir, f));
            string cnl = Path.Combine(dir, "bench.cnl"), s2p = Path.Combine(dir, "x.s2p");
            File.WriteAllText(cnl, cnlText);
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            foreach (string arg in new[] { cliDll, "sparam", cnl, "-o", s2p }) psi.ArgumentList.Add(arg);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd(), stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            output.WriteLine(stdout + stderr);
            Assert.Equal(0, proc.ExitCode);
            return TouchstoneIO.ReadFile(s2p);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── S-parameter blocks ────────────────────────────────────────────────────

    /// <summary>Friis: two matched amplifiers from their files with a matched 3 dB pad at T0 between them.</summary>
    [Fact]
    public void FriisCascade_MeetsTheClosedForm()
    {
        var inputs = Ref("friis_matched").GetProperty("inputs");
        double Lin(string key) => Math.Pow(10, inputs.GetProperty(key).GetDouble() / 10);
        double g1 = Lin("G1_db"), f1 = Lin("NF1_db"), f2 = Lin("NF2_db"), loss = Lin("pad_loss_db");
        double f = f1 + (loss - 1) / g1 + (f2 - 1) * loss / g1;

        var r = RunCnl(Bench("friis_matched"));
        for (int fi = 0; fi < r.Freqs.Length; fi++)
            Assert.Equal(10 * Math.Log10(f), r.Ds["NF"].RealValues[fi], 1e-9);
        Assert.DoesNotContain(r.Nl.Warnings, x => x.StartsWith("Noise:", StringComparison.Ordinal));
    }

    /// <summary>
    /// Pozar Ex. 12.5: the 2 dB circle drawn from the parameters the run reports, against the closed form and scikit-rf's
    /// <c>nf_circle</c> points (1e-9) and the textbook's rounded answer (1e-2).
    /// </summary>
    [Fact]
    public void PozarEx12_5_NoiseCircle()
    {
        var circle = Ref("pozar_ex12_5").GetProperty("circle");
        var book = circle.GetProperty("textbook");
        double[] Arr(string key) => [.. circle.GetProperty(key).EnumerateArray().Select(x => x.GetDouble())];
        double f = Math.Pow(10, circle.GetProperty("nf_db").GetDouble() / 10);
        double[] nRef = Arr("N"), cRe = Arr("center_re"), cIm = Arr("center_im"), radius = Arr("radius");
        var ptsRe = circle.GetProperty("scikit_rf_points_npoints9_re");
        var ptsIm = circle.GetProperty("scikit_rf_points_npoints9_im");

        var r = RunCnl(Bench("pozar_ex12_5"));
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            var p = new TwoPortNoise(Math.Pow(10, r.Ds["NFmin"].RealValues[fi] / 10), r.Ds["GammaOpt"].ComplexValues[fi],
                                     r.Ds["Rn"].RealValues[fi], 50.0);
            var (centre, rad) = NoiseMetrics.NoiseCircle(p, f);
            double n = p.R / (4 * p.Rn) * (f - p.Fmin) * Sq(1 + p.GammaOpt);
            output.WriteLine($"{r.Freqs[fi]:G4} Hz: N {n:G6}, centre {centre.Magnitude:G6} at {centre.Phase * 180 / Math.PI:G6} deg, radius {rad:G6}");

            Assert.Equal(nRef[fi], n, 1e-9);
            Assert.True((centre - new Complex(cRe[fi], cIm[fi])).Magnitude < 1e-9, $"centre {centre}");
            Assert.Equal(radius[fi], rad, 1e-9);
            for (int k = 0; k < 9; k++)
            {
                var pt = centre + rad * Complex.Exp(new Complex(0, 2 * Math.PI * k / 8));
                Assert.True((pt - new Complex(ptsRe[fi][k].GetDouble(), ptsIm[fi][k].GetDouble())).Magnitude < 1e-9, $"point {k}");
            }

            double tol = book.GetProperty("tol").GetDouble();
            Assert.Equal(book.GetProperty("N").GetDouble(), n, tol);
            Assert.Equal(book.GetProperty("center_mag").GetDouble(), centre.Magnitude, tol);
            Assert.Equal(book.GetProperty("center_deg").GetDouble(), centre.Phase * 180 / Math.PI, tol);
            Assert.Equal(book.GetProperty("radius").GetDouble(), rad, tol);
        }
    }

    /// <summary>
    /// Between noise rows, Fmin (as a factor), Γopt and Rn are linear; outside them the end row holds, and the run says so
    /// once. ampA.s2p's rows are at 2, 4, 6 and 8 GHz, so 3, 5 and 7 GHz are midpoints.
    /// </summary>
    [Fact]
    public void NoiseData_IsLinearBetweenRows_AndHeldOutsideThem()
    {
        var r = RunCnl(Bench("touchstone_r50").Replace("start=2.0 stop=8.0 npts=4", "start=1.0 stop=9.0 npts=9"));
        var w = Assert.Single(r.Nl.Warnings, x => x.StartsWith("Noise:", StringComparison.Ordinal));
        Assert.Contains("SnP:X1", w);
        Assert.Contains("2 to 8 GHz", w);

        var rows = TouchstoneIO.ReadFile(Path.Combine(NoiseDir(), "ampA.s2p")).NoiseParameters;
        (int, int)[] between = [(0, 0), (0, 0), (0, 1), (1, 1), (1, 2), (2, 2), (2, 3), (3, 3), (3, 3)];
        for (int fi = 0; fi < r.Freqs.Length; fi++)
        {
            var (a, b) = between[fi];
            double fmin = (Math.Pow(10, rows[a].NFminDb / 10) + Math.Pow(10, rows[b].NFminDb / 10)) / 2;
            Assert.Equal(10 * Math.Log10(fmin), r.Ds["NFmin"].RealValues[fi], 1e-9);
            Assert.True((r.Ds["GammaOpt"].ComplexValues[fi] - (rows[a].GammaOpt + rows[b].GammaOpt) / 2).Magnitude < 1e-9);
            Assert.Equal(50 * (rows[a].RnNormalized + rows[b].RnNormalized) / 2, r.Ds["Rn"].RealValues[fi], 1e-9);
        }
    }

    /// <summary>Noise rows no physical two-port has (Fmin − 1 above 4·Rn·Gopt) are reported once and used as given.</summary>
    [Fact]
    public void UnphysicalNoiseData_IsSaidOnce_AndUsedAsGiven()
    {
        // ampA.s2p's network with its 2 and 6 GHz rows replaced: 3 dB at Γopt = 0.5 needs Rn far above 1 Ω.
        string[] head = [.. File.ReadAllLines(Path.Combine(NoiseDir(), "ampA.s2p")).TakeWhile(l => !l.StartsWith("! noise"))];
        string file = Scratch("unphysical.s2p",
            [.. head, "2 3 0.5 0 0.02", "4 0.92 0.42 76 0.24", "6 3 0.5 0 0.02", "8 1.24 0.34 112 0.28"]);
        var r = RunCnl(Bench("touchstone_r50").Replace("\"ampA.s2p\"", $"\"{file}\""));

        var w = Assert.Single(r.Nl.Warnings, x => x.StartsWith("Noise:", StringComparison.Ordinal));
        Assert.Contains("SnP:X1", w);
        Assert.Contains("2 of its 4 rows, first at 2 GHz", w);
        Assert.Equal(3.0, r.Ds["NFmin"].RealValues[0], 1e-9);
        Assert.Equal(1.0, r.Ds["Rn"].RealValues[0], 1e-9);
        Assert.True((r.Ds["GammaOpt"].ComplexValues[0] - 0.5).Magnitude < 1e-9);
    }

    /// <summary>
    /// A passive block with no noise data is thermal (Bosma) like any passive part — here with its ports on a floating
    /// reference that reaches ground through a resistor, so the whole network is passive at one temperature and every Cs
    /// entry must be <c>kT(I − S·Sᴴ)</c>.
    /// </summary>
    [Fact]
    public void PassiveBlock_WithoutNoiseData_IsBosma()
    {
        string file = PassiveFile("passive.s2p", "", noise: false);
        var r = RunCnl(FloatingBlock(file, ""));
        Assert.Empty(r.Nl.Warnings);
        double worst = WorstAgainstBosma(r, 300.0);
        output.WriteLine($"passive SnP, no noise data: {worst:E2}");
        Assert.True(worst < 1e-9, $"{worst:E2}");
    }

    /// <summary>
    /// A passive block whose noise data is its own thermal noise at T0, on a floating reference through a resistor, the
    /// whole bench at T0: the data path (C_A to C_Y, and the reference node carrying minus the port currents) must give
    /// Bosma's <c>kT0(I − S·Sᴴ)</c> for the whole network.
    /// </summary>
    [Fact]
    public void BlockNoiseData_OnAFloatingReference_IsBosmaWhenItIsThermal()
    {
        const string AtT0 = "temp = 16.85\n";
        string file = PassiveFile("thermal.s2p", AtT0, noise: true);
        Assert.NotEmpty(TouchstoneIO.ReadFile(file).NoiseParameters);
        var r = RunCnl(FloatingBlock(file, AtT0));
        Assert.DoesNotContain(r.Nl.Warnings, x => x.StartsWith("Noise:", StringComparison.Ordinal));
        double worst = WorstAgainstBosma(r, NoiseMetrics.T0);
        output.WriteLine($"thermal noise data, floating reference: {worst:E2}");
        Assert.True(worst < 1e-9, $"{worst:E2}");
    }

    /// <summary>
    /// What has no noise model is named: a block with gain and no noise data, and a Deembed, which states none even on a
    /// file that has some. A block with noise data is not named.
    /// </summary>
    [Fact]
    public void BlocksWithoutANoiseModel_AreNamed()
    {
        string[] head = [.. File.ReadAllLines(Path.Combine(NoiseDir(), "ampA.s2p")).TakeWhile(l => !l.StartsWith("! noise"))];
        string gain = Scratch("gain.s2p", head);
        var r = RunCnl($"""
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            SnP:X1  p1  a  NumPorts=2 File="{gain}"
            SnP:X3  a  m  NumPorts=2 File="ampA.s2p"
            Deembed:X2  m  p2  NumPorts=2 File="ampA.s2p"
            analysis SP1 type=sparam start=2 stop=8 npts=4 Unit=GHz Noise=yes
            """);
        var w = Assert.Single(r.Nl.Warnings, x => x.StartsWith("Noise:", StringComparison.Ordinal));
        Assert.Contains("SnP:X1", w);
        Assert.Contains("Deembed:X2", w);
        Assert.DoesNotContain("SnP:X3", w);
    }

    /// <summary>lossy_lc's S (and, with <paramref name="noise"/>, its noise rows) at <paramref name="prefix"/>'s ambient,
    /// written to a scratch Touchstone file.</summary>
    private string PassiveFile(string name, string prefix, bool noise)
    {
        var r = RunCnl(WithNoise(prefix + File.ReadAllText(Path.Combine(NoiseDir(), "lossy_lc.cnl")), noise ? "yes" : "no"));
        string path = Path.Combine(_dir, name);
        TouchstoneIO.WriteFile(DataSetBuilder.ToSnp(r.Ds), path, precision: "G17");
        return path;
    }

    /// <summary>The block between two 50 Ω ports, its reference node reaching ground through 20 Ω, on lossy_lc's grid.</summary>
    private static string FloatingBlock(string file, string prefix) => prefix + $"""
        Port:P1  p1 0  Num=1 Z=50 Ohm
        Port:P2  p2 0  Num=2 Z=50 Ohm
        SnP:X1  p1  p2  r  NumPorts=2 File="{file}"
        R:Rg  r  0  R=20 Ohm
        analysis SP1 type=sparam start=0.5 stop=5.0 npts=10 Unit=GHz Noise=yes
        """;

    /// <summary>A file in this test's scratch folder, holding <paramref name="lines"/>.</summary>
    private string Scratch(string name, string[] lines)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    // ── small helpers ─────────────────────────────────────────────────────────

    private static double Sq(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

    private static ElaboratedNetlist Netlist(string body)
    {
        var (lib, tb) = new CnlReader().Read(body + "\n");
        return new Elaborator(lib).Elaborate(tb);
    }

    private static ElaboratedComponent Part(string line) => Netlist(line).Components.Single();

    /// <summary>The recorded stamp reduced onto <paramref name="nodes"/>: <c>Y = A_nn − A_nb·A_bb⁻¹·A_bn</c>.</summary>
    private static Mat<Complex> Schur(Dictionary<long, Complex> stamp, int[] nodes)
    {
        var ids = EnvelopeTangentCheck.Ids(stamp.Keys);
        var br = ids.Where(i => i >= EnvelopeTangentCheck.Recorder.B).ToList();
        Complex A(long r, long c) => stamp.TryGetValue((r << 32) | c, out var v) ? v : Complex.Zero;
        int n = nodes.Length, m = br.Count;
        var y = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                y[i, j] = A(nodes[i], nodes[j]);
        if (m == 0) return y;
        var bb = new Complex[m, m];
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                bb[i, j] = A(br[i], br[j]);
        var inv = EnvelopeEngine.Invert(bb, m);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                for (int p = 0; p < m; p++)
                    for (int q = 0; q < m; q++)
                        y[i, j] -= A(nodes[i], br[p]) * inv[p, q] * A(br[q], nodes[j]);
        return y;
    }
}
