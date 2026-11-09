using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine.Loadpull;
using RfCore;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Loadpull;

/// <summary>
/// Brief NZ-6: noise figure as a loadpull and sourcepull metric. Each gate against a number from outside the loadpull: the
/// S-parameter noise analysis on the same terminations, the noise circles its four noise parameters draw, and the run with
/// no key.
/// </summary>
public sealed class LoadpullNoiseTests(ITestOutputHelper output)
{
    private static string N(double x) => x.ToString("R", CultureInfo.InvariantCulture);
    private static string Z(Complex z) => $"({N(z.Real)}{(z.Imaginary < 0 ? "-" : "+")}{N(Math.Abs(z.Imaginary))}j)";

    private const double Tone = 1e9;

    /// <summary>
    /// brief NZ-5's one-BJT LNA (base and collector shot noise, flicker, Rb thermal, a lossy collector load and a base bias
    /// resistor), its input and output terminated by <paramref name="terminations"/>: two tuners for the loadpull, two ports
    /// for the S-parameter reference.
    /// </summary>
    private static string Lna(string terminations, string analysis) => $"""
        C:C1  in b  C=20e-12
        R:RB  vcc b  R=150e3
        BJT_NPN:Q1  c b 0  Is=1e-15 Bf=100 Rb=20 Kf=1e-14 Cje=0.5e-12 Cjc=0.2e-12
        L:LC  vcc c  L=100e-9
        R:RC  vcc c  R=400
        C:C2  c out  C=20e-12
        Vdc:V1  vcc 0  Vdc=3
        {terminations}
        {analysis}
        """;

    private const string Tuners = """
        Tuner:TS  in 0   Zdefault=50 Z0=50 BiasTee=on Vbias=0 Z[1]=50
        Tuner:TL  out 0  Zdefault=50 Z0=50 BiasTee=on Vbias=0 Z[1]=50
        """;

    /// <summary>A sourcepull at −130 dBm, two rungs so a constant-drive contour has a drive axis to read along; tickle off.
    /// The rungs 10 dB apart differ by 1e-7 dB, so the drive's own share at −130 dBm is ~1e-8 dB: the small-signal limit.
    /// </summary>
    private static string Sourcepull(string gamPath, string noiseKeys) =>
        $"analysis LP1 type=loadpull Tone={N(Tone)} MaxHarm=3 LoadTuner=TL SourceTuner=TS Grid=\"{gamPath}\" Sweep=Source " +
        $"PinStart=-130 PinStep=10 PinMax=-120 Tickle=off Compression=3 Tol=1e-12 {noiseKeys}";

    /// <summary>
    /// The loadpull forces a series resistance on every inductor branch at DC (<c>InductanceRegularization=Always</c>), 1 µΩ
    /// by default, which floors the HB residual near 1e-10 A: at the 150 kΩ-biased base that is ~2.5e-7 V of Vbe and a few
    /// 1e-6 dB of NF. 1 mΩ conditions the solve a thousand times better and changes nothing here: no DC flows through a
    /// tuner's choke (both are behind DC blocks), and the collector inductor's ~1 mA drops 1 µV across it.
    /// </summary>
    private static readonly AnalysisSettings Settings = new() { InductanceRegR = 1e-3 };

    private static string WriteGam(string dir, IEnumerable<Complex> gammas)
    {
        string path = Path.Combine(dir, "grid.gam");
        var lines = new List<string> { "# impedance Z0=50 re+j*imag" };
        foreach (var g in gammas)
        {
            var z = 50 * (1 + g) / (1 - g);
            lines.Add($"{z.Real.ToString("F12", CultureInfo.InvariantCulture)}{(z.Imaginary >= 0 ? "+" : "")}{z.Imaginary.ToString("F12", CultureInfo.InvariantCulture)}j");
        }
        File.WriteAllLines(path, lines);
        return path;
    }

    private DataSet RunLoadpull(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, "tb");
        var nl = new Elaborator(lib).Elaborate(tb);
        var lpa = tb.Analyses.OfType<LoadpullAnalysis>().Single();
        var p = LoadpullEngine.Resolve(lpa, nl.ResolvedGlobals, nl.GlobalsWithExplicitUnit);
        var ds = new LoadpullEngine(nl, tb, Settings).Run(p);
        foreach (var w in nl.Warnings.Concat(nl.Notes)) output.WriteLine($"run says: {w}");
        return ds;
    }

    /// <summary>The S-parameter noise run at one frequency, its DC pre-pass converged to 1e-14 A (brief NZ-5's reason).</summary>
    private static DataSet Sp(string cnl)
    {
        var (lib, tb) = new CnlReader().Read(cnl, "tb");
        var nl = new Elaborator(lib).Elaborate(tb);
        var spa = tb.Analyses.OfType<SParameterAnalysis>().Single();
        return SParameterEngine.Run(nl, spa.Expand(nl.ResolvedGlobals),
            new AnalysisSettings { NonlinearAbsTol = 1e-14 }.WithNoise(true));
    }

    /// <summary>With NoiseFreq absent, NF is read 1 % above the tone.</summary>
    private static double ReadHz => LoadpullEngine.NoiseReadHz(0, Tone, out _);

    /// <summary>
    /// The S-parameter reference on the terminations as a tuner stamps them: a port of the grid point's impedance behind the
    /// tuner's ideal bias tee, a 1 F DC block in series and a 1 H choke to the 0 V supply. The tee is physically nothing at
    /// 1 GHz (1e-10 Ω and 6e9 Ω), but a j6e9 S admittance beside 0.02 S costs the solve ~1e-6 of relative precision: without
    /// it the S-parameter NF is 3.8e-6 dB higher, in both analyses alike (a 1 mF block moves it back to 1e-9 dB).
    /// </summary>
    private static string SpAt(Complex zs) => Lna(
        $"""
        C:CB1  in ib  C=1
        L:LB1  in 0   L=1
        Port:P1  ib 0  Num=1 Z={Z(zs)} Ohm
        C:CB2  out ob  C=1
        L:LB2  out 0   L=1
        Port:P2  ob 0  Num=2 Z=50 Ohm
        """,
        $"analysis SP1 type=sparam start={N(ReadHz)} stop={N(ReadHz)} npts=1 Noise=yes");

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "nz6_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ── gate 1: the small-signal limit ───────────────────────────────────────

    /// <summary>
    /// A sourcepull grid of lossless tuner terminations around the LNA: at every grid point, NF at the small-signal rung is
    /// the S-parameter noise analysis's with port 1 at that grid point's impedance, to 1e-6 dB. The tuners add nothing: a
    /// tuner is noiseless by definition, as a port is, and its termination is the input's at T0.
    /// </summary>
    [Fact]
    public void Gate1_SmallSignalRung_EqualsSParameterNoise_OnTheSameTerminations()
    {
        string dir = TempDir();
        try
        {
            Complex[] gammas = [0, new(0.3, 0.2), new(-0.4, 0.1), new(0.1, -0.5), new(0.6, 0)];
            var ds = RunLoadpull(Lna(Tuners, Sourcepull(WriteGam(dir, gammas), "Noise=yes")));
            var nf = ds["NF"];
            output.WriteLine($"Converged {string.Join(",", ds["Converged"].RealValues)}; NF {string.Join(",", nf.RealValues)}");
            Assert.Equal("dB", nf.Unit);
            int nP = nf.Axes[1].Length;
            for (int gi = 0; gi < gammas.Length; gi++)
            {
                var zs = 50 * (1 + gammas[gi]) / (1 - gammas[gi]);
                double sp = Sp(SpAt(zs))["NF"].RealValues[0];
                double lp = nf.RealValues[gi * nP];
                output.WriteLine($"Γs = {gammas[gi]}: loadpull NF {lp:R} dB, S-parameter NF {sp:R} dB, Δ {lp - sp:E2}");
                Assert.Equal(sp, lp, 1e-6);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── gate 2: sourcepull NF contours are the noise circles ─────────────────

    /// <summary>
    /// The small-signal sourcepull's NF contours, drawn by the Data Display's own machinery (<see cref="LoadpullSurface"/>'s
    /// RBF fit, resampled, then <see cref="ContourExtractor"/>), are the circles <c>ns_circle</c> draws from the S-parameter
    /// analysis's Fmin, Sopt and Rn: centre and radius agree to the grid's interpolation error, which is measured and printed.
    /// </summary>
    [Fact]
    public void Gate2_SourcepullNfContours_AreTheNoiseCircles()
    {
        string dir = TempDir();
        try
        {
            // 7 rings × 12 angles to |Γ| = 0.7, plus the centre.
            var gammas = new List<Complex> { 0 };
            for (int r = 1; r <= 7; r++)
                for (int a = 0; a < 12; a++)
                    gammas.Add(Complex.FromPolarCoordinates(0.1 * r, 2 * Math.PI * a / 12));
            var ds = LoadpullPostProcessor.Enrich(RunLoadpull(Lna(Tuners, Sourcepull(WriteGam(dir, gammas), "Noise=yes"))));

            var sp = Sp(SpAt(50));
            var np = new TwoPortNoise(sp["Fmin"].RealValues[0], sp["Sopt"].ComplexValues[0], sp["Rn"].RealValues[0], 50);
            output.WriteLine($"NFmin {np.NFminDb:F4} dB, Sopt {np.GammaOpt}, Rn {np.Rn:F3} Ω");

            var surface = new LoadpullSurface(ds);
            var fit = surface.Fit(0, "NF", new ConstraintSpec(ConstraintKind.ConstantMetric, "PavlDbm", -130), SurfacePlane.Gamma);
            Assert.NotNull(fit);
            var grid = surface.Resample(fit!, new ViewBox(-0.75, 0.75, -0.75, 0.75), resolution: 151);

            double worstCentre = 0, worstRadius = 0;
            foreach (double above in new[] { 0.25, 0.5, 1.0 })
            {
                double nfDb = np.NFminDb + above;
                var (centre, radius) = NoiseMetrics.NoiseCircle(np, Math.Pow(10, nfDb / 10));
                var pts = ContourExtractor.Extract(grid, new ContourLevelSet([nfDb]))
                    .SelectMany(c => c.Points).ToList();
                Assert.True(pts.Count > 20, $"no NF = {nfDb:F3} dB contour");
                // Best-fit circle (algebraic): x² + y² + D·x + E·y + F = 0, then the fit's centre and radius against
                // ns_circle's, and the worst point's distance from ns_circle's own circle.
                var (fc, fr) = FitCircle(pts.Select(p => new Complex(p.X, p.Y)).ToList());
                double off = pts.Max(p => Math.Abs((new Complex(p.X, p.Y) - centre).Magnitude - radius));
                output.WriteLine($"NF {nfDb:F4} dB: ns_circle centre {centre:F4} r {radius:F4}; contour fit centre {fc:F4} " +
                                 $"r {fr:F4}; |Δcentre| {(fc - centre).Magnitude:E2}, Δr {fr - radius:E2}, worst point off the circle {off:E2}");
                worstCentre = Math.Max(worstCentre, (fc - centre).Magnitude);
                worstRadius = Math.Max(worstRadius, Math.Abs(fr - radius));
            }
            // The grid's interpolation error: an RBF through 85 points 0.1 apart in Γ, resampled at 0.01.
            Assert.True(worstCentre < 5e-3, $"contour centre off ns_circle's by {worstCentre:E2}");
            Assert.True(worstRadius < 5e-3, $"contour radius off ns_circle's by {worstRadius:E2}");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static (Complex Centre, double Radius) FitCircle(List<Complex> pts)
    {
        // Least squares on [x y 1]·[D E F]ᵀ = −(x² + y²).
        var a = new double[3, 3];
        var b = new double[3];
        foreach (var p in pts)
        {
            double[] row = [p.Real, p.Imaginary, 1];
            double rhs = -(p.Real * p.Real + p.Imaginary * p.Imaginary);
            for (int i = 0; i < 3; i++)
            {
                b[i] += row[i] * rhs;
                for (int j = 0; j < 3; j++) a[i, j] += row[i] * row[j];
            }
        }
        var x = Solve3(a, b);
        var c = new Complex(-x[0] / 2, -x[1] / 2);
        return (c, Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary - x[2]));
    }

    private static double[] Solve3(double[,] a, double[] b)
    {
        double Det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
            + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double d = Det(a);
        var x = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var m = (double[,])a.Clone();
            for (int i = 0; i < 3; i++) m[i, k] = b[i];
            x[k] = Det(m) / d;
        }
        return x;
    }

    // ── gate 3: no key, no change ────────────────────────────────────────────

    /// <summary>
    /// A loadpull with <c>Noise</c> absent and with <c>Noise=no</c> carries the same cubes, value for value, and the same
    /// <c>.spl</c> bytes; <c>Noise=yes</c> adds NF and changes nothing else.
    /// </summary>
    [Fact]
    public void Gate3_NoKey_NoChange_SplIncluded()
    {
        string dir = TempDir();
        try
        {
            string gam = WriteGam(dir, [0, new(0.3, 0.2), new(-0.2, -0.3)]);
            string Lp(string keys) =>
                $"analysis LP1 type=loadpull Tone={N(Tone)} MaxHarm=3 LoadTuner=TL SourceTuner=TS Grid=\"{gam}\" " +
                $"PinStart=-30 PinStep=5 PinMax=-15 Tol=1e-12 {keys}";
            var absent = RunLoadpull(Lna(Tuners, Lp("")));
            var off    = RunLoadpull(Lna(Tuners, Lp("Noise=no")));
            var on     = RunLoadpull(Lna(Tuners, Lp("Noise=yes")));

            Assert.False(absent.Contains("NF"));
            Assert.Equal(Keys(absent), Keys(off));
            Assert.Equal(Keys(absent).Append("NF").OrderBy(k => k, StringComparer.Ordinal), Keys(on));
            foreach (var k in Keys(absent))
            {
                Assert.Equal(Values(absent[k]), Values(off[k]));
                Assert.Equal(Values(absent[k]), Values(on[k]));
            }
            Assert.Equal(Spl(absent), Spl(off));
            Assert.Contains(" NF", Spl(on).Split('\n').First(l => l.StartsWith("valid", StringComparison.Ordinal)));
        }
        finally { Directory.Delete(dir, recursive: true); }

        static List<string> Keys(DataSet ds) => [.. ds.Cubes.Keys.OrderBy(k => k, StringComparer.Ordinal)];
        static string[] Values(DataCube c) => c.DataKind == DataKind.Complex
            ? [.. c.ComplexValues.Select(v => $"{v.Real:R},{v.Imaginary:R}")]
            : [.. c.RealValues.Select(v => v.ToString("R", CultureInfo.InvariantCulture))];
        static string Spl(DataSet ds)
        {
            var w = new StringWriter();
            SplWriter.WriteSpl(LoadpullPostProcessor.Enrich(ds), w);
            return w.ToString();
        }
    }

    // ── item 4: a reader that does not know NF ignores the column ────────────

    /// <summary>
    /// <see cref="SplReader"/> maps a column only through <see cref="LoadpullFomDialect.Map"/> and skips a header the map does
    /// not name, with every other column read at its own position — which is what lets a reader that predates NF read a file
    /// with an NF column. Shown with a column no reader knows, inserted before the known ones.
    /// </summary>
    [Fact]
    public void Spl_UnknownColumn_IsSkipped_AndTheRestReadInPlace()
    {
        const string spl = """
            Number of Frequencies = 1
            Number of Variables = 1
            VAR=<F0 Load Gamma>, Units=<>
            VAR=<Pin_avail>, Units=<dBm>
            ! Freq Points per VAR
            1 2 1
            Freq = 1 GHz
            num_src_harmonics = 1, num_ld_harmonics = 1
            valid gamma_ld1 Psource[dBm] Zz_unknown Pout_dBm Gt_dB
            1 0 0 -20 99 -5 15
            1 0.5 0 -20 77 -4 16
            """;
        string path = Path.Combine(Path.GetTempPath(), $"nz6_{Guid.NewGuid():N}.spl");
        File.WriteAllText(path, spl);
        try
        {
            var ds = SplReader.ReadSpl(path);
            Assert.DoesNotContain(ds.Cubes.Keys, k => k.Contains("Zz", StringComparison.Ordinal));
            Assert.Equal([-5.0, -4.0], ds["Pout_dBm"].RealValues);
            Assert.Equal([15.0, 16.0], ds["Gt_dB"].RealValues);
        }
        finally { File.Delete(path); }
    }

    // ── the default frequency ────────────────────────────────────────────────

    /// <summary>Absent, the noise is read 1 % above the tone. A stated tone or half-multiple of it is degenerate for the
    /// conversion matrix and is moved 1e-6 of the tone above; any other stated frequency is read where it was asked for.</summary>
    [Fact]
    public void NoiseReadHz_DefaultsOnePercentAbove_AndMovesOffADegeneratePointOnly()
    {
        Assert.Equal(1.01 * Tone, LoadpullEngine.NoiseReadHz(0, Tone, out bool m0), 1e-3);
        Assert.False(m0);
        Assert.Equal(Tone * (1 + 1e-6), LoadpullEngine.NoiseReadHz(Tone, Tone, out bool m1), 1e-3);
        Assert.True(m1);
        Assert.Equal(1.5 * Tone + 1e-6 * Tone, LoadpullEngine.NoiseReadHz(1.5 * Tone, Tone, out bool m2), 1e-3);
        Assert.True(m2);
        Assert.Equal(1.2 * Tone, LoadpullEngine.NoiseReadHz(1.2 * Tone, Tone, out bool m3));
        Assert.False(m3);
    }
}
