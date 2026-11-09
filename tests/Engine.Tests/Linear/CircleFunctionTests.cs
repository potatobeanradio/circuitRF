using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Linear;

/// <summary>
/// Brief NZ-2: <c>ga_circle</c>, <c>gp_circle</c>, <c>stab_circle</c> and <c>stab_side</c> on a two-port run. The references
/// are the textbook closed forms written out here, the gains and reflections computed straight from S at the circles' own
/// points, and a point test of stability.
/// </summary>
public sealed class CircleFunctionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("circles-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static string NoiseDir()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "testdata", "noise"))) return Path.Combine(dir, "testdata", "noise");
        throw new DirectoryNotFoundException("testdata/noise not found above " + AppContext.BaseDirectory);
    }

    private sealed record Run(DataSet Ds, ElaboratedNetlist Nl, TestBench Tb);

    private static Run RunCnl(string cnl, string dir)
    {
        var (lib, tb) = new CnlReader().Read(cnl, sourceDirectory: dir);
        var nl  = new Elaborator(lib).Elaborate(tb);
        var spa = tb.Analyses.OfType<SParameterAnalysis>().First();
        return new Run(SParameterEngine.Run(nl, spa.Expand(nl.ResolvedGlobals), AnalysisSettings.Default.WithNoiseOf(spa)), nl, tb);
    }

    /// <summary>The testdata/noise <c>pozar_ex12_5</c> two-port, the one PR #50's noise-circle tests use, at 3.5, 4 and 4.5 GHz.</summary>
    private static Run TextbookPart() => RunCnl("""
        Port:P1  p1 0  Num=1 Z=50.0 Ohm
        Port:P2  p2 0  Num=2 Z=50.0 Ohm
        SnP:X1  p1  p2  NumPorts=2 File="pozar_ex12_5.s2p"
        analysis SP1 type=sparam start=3.5 stop=4.5 npts=3 Unit=GHz Noise=yes
        """, NoiseDir());

    private static Value Eval(DataSet ds, string expr) =>
        new Evaluator(new MeasurementContext(new Dictionary<string, DataSet> { ["SP1"] = ds })).Eval(expr, new Scope("t"));

    private static Mat<Complex> S(DataSet ds, int fi)
    {
        var m = new Mat<Complex>(2, 2);
        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++) m[i, j] = (Complex)ds["S"][fi, i, j];
        return m;
    }

    private static double Sq(Complex z) => z.Magnitude * z.Magnitude;

    /// <summary>A circle's centre and radius from its first and middle points (θ = 0 and π).</summary>
    private static (Complex C, double R) CircleOf(Complex[] pts, int fi, int npts)
        => ((pts[fi * npts] + pts[fi * npts + (npts - 1) / 2]) / 2,
            (pts[fi * npts] - pts[fi * npts + (npts - 1) / 2]).Magnitude / 2);

    // ── gate 1: textbook values ───────────────────────────────────────────────────

    /// <summary>
    /// The available-gain circle at max_gain − 2 dB and both stability circles against the closed forms, written here in
    /// the normalised-gain form: g = G/|S21|², C1 = S11 − Δ·S22*, centre g·C1*/(1 + g(|S11|² − |Δ|²)), radius
    /// √(1 − 2K|S12S21|g + |S12S21|²g²)/|1 + g(|S11|² − |Δ|²)|; the source stability circle (S11 − Δ·S22*)*/(|S11|² − |Δ|²)
    /// and |S12S21|/||S11|² − |Δ|²|, the load one with the ports exchanged. Centres and radii to 1e-9; every point on its
    /// circle to 1e-12.
    /// </summary>
    [Fact]
    public void TextbookPart_CirclesMeetTheClosedForms()
    {
        var r = TextbookPart();
        const int N = 9;
        var maxDb = Eval(r.Ds, "max_gain(SP1.S)").AsCube().RealValues;
        var ga  = Eval(r.Ds, $"ga_circle(SP1.S, max_gain(SP1.S) - 2, {N})").AsCube().ComplexValues;
        var ssc = Eval(r.Ds, $"stab_circle(SP1.S, \"source\", {N})").AsCube().ComplexValues;
        var lsc = Eval(r.Ds, $"stab_circle(SP1.S, \"load\", {N})").AsCube().ComplexValues;

        for (int fi = 0; fi < 3; fi++)
        {
            var s = S(r.Ds, fi);
            Complex s11 = s[0, 0], s12 = s[0, 1], s21 = s[1, 0], s22 = s[1, 1], d = s11 * s22 - s12 * s21;
            double c = (s12 * s21).Magnitude, k = (1 - Sq(s11) - Sq(s22) + Sq(d)) / (2 * c);

            double g = Math.Pow(10, (maxDb[fi] - 2) / 10) / Sq(s21);
            Complex den = 1 + g * (Sq(s11) - Sq(d));
            Complex gaC = g * Complex.Conjugate(s11 - d * Complex.Conjugate(s22)) / den;
            double gaR = Math.Sqrt(1 - 2 * k * c * g + c * c * g * g) / den.Magnitude;
            Complex sC = Complex.Conjugate(s11 - d * Complex.Conjugate(s22)) / (Sq(s11) - Sq(d));
            double sR = c / Math.Abs(Sq(s11) - Sq(d));
            Complex lC = Complex.Conjugate(s22 - d * Complex.Conjugate(s11)) / (Sq(s22) - Sq(d));
            double lR = c / Math.Abs(Sq(s22) - Sq(d));

            foreach (var (pts, centre, radius, what) in new[] { (ga, gaC, gaR, "ga"), (ssc, sC, sR, "source"), (lsc, lC, lR, "load") })
            {
                var (gotC, gotR) = CircleOf(pts, fi, N);
                Assert.True((gotC - centre).Magnitude < 1e-9, $"{what} centre at f{fi}: {gotC} vs {centre}");
                Assert.Equal(radius, gotR, 1e-9);
                for (int p = 0; p < N; p++)
                    Assert.Equal(radius, (pts[fi * N + p] - centre).Magnitude, 1e-12 * Math.Max(1, radius));
            }
        }
    }

    // ── gate 2: consistency ───────────────────────────────────────────────────────

    /// <summary>
    /// Every point of a ga_circle at max_gain − 2 dB gives its GdB back as the available gain computed from S and that Γs, and every point of a
    /// gp_circle its GdB as the operating gain; every point of a stability circle has |Γout| = 1 (source) or |Γin| = 1 (load).
    /// stab_side agrees with a point test at the circle's centre.
    /// </summary>
    [Fact]
    public void TextbookPart_CirclesAreWhatTheyClaim()
    {
        var r = TextbookPart();
        const int N = 13;
        var maxDb = Eval(r.Ds, "max_gain(SP1.S)").AsCube().RealValues;
        var ga  = Eval(r.Ds, $"ga_circle(SP1.S, max_gain(SP1.S) - 2, {N})").AsCube().ComplexValues;
        var gp  = Eval(r.Ds, $"gp_circle(SP1.S, max_gain(SP1.S) - 2, {N})").AsCube().ComplexValues;
        var ssc = Eval(r.Ds, $"stab_circle(SP1.S, \"source\", {N})").AsCube().ComplexValues;
        var lsc = Eval(r.Ds, $"stab_circle(SP1.S, \"load\", {N})").AsCube().ComplexValues;
        var sSide = Eval(r.Ds, "stab_side(SP1.S, \"source\")").AsCube().RealValues;
        var lSide = Eval(r.Ds, "stab_side(SP1.S, \"load\")").AsCube().RealValues;

        for (int fi = 0; fi < 3; fi++)
        {
            var s = S(r.Ds, fi);
            double G = maxDb[fi] - 2;
            for (int p = 0; p < N; p++)
            {
                Assert.Equal(G, 10 * Math.Log10(TwoPortCircles.AvailableGainAt(s, ga[fi * N + p])), 1e-9);
                Assert.Equal(G, 10 * Math.Log10(TwoPortCircles.OperatingGainAt(s, gp[fi * N + p])), 1e-9);
                Assert.Equal(1.0, TwoPortCircles.OutputReflection(s, ssc[fi * N + p]), 1e-9);
                Assert.Equal(1.0, TwoPortCircles.InputReflection(s, lsc[fi * N + p]), 1e-9);
            }
            var (sc, _) = CircleOf(ssc, fi, N);
            var (lc, _) = CircleOf(lsc, fi, N);
            Assert.Equal(TwoPortCircles.OutputReflection(s, sc) < 1 ? 1.0 : -1.0, sSide[fi]);
            Assert.Equal(TwoPortCircles.InputReflection(s, lc) < 1 ? 1.0 : -1.0, lSide[fi]);
        }
    }

    // ── gate 3: unconditionally stable, and no circle ─────────────────────────────

    private Run Stable()
    {
        File.WriteAllText(Path.Combine(_dir, "stable.s2p"), """
            # GHz S MA R 50
            1 0.3 -30 2 90 0.05 30 0.4 -45
            2 0.3 -40 2 80 0.05 35 0.4 -55
            """);
        return RunCnl($"""
            Port:P1  p1 0  Num=1 Z=50 Ohm
            Port:P2  p2 0  Num=2 Z=50 Ohm
            SnP:X1  p1  p2  NumPorts=2 File="{Path.Combine(_dir, "stable.s2p")}"
            analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz
            measure TooMuch = ga_circle(SP1.S, max_gain(SP1.S) + 1)
            measure Fine = ga_circle(SP1.S, max_gain(SP1.S) - 1)
            """, _dir);
    }

    /// <summary>K &gt; 1 and |Δ| &lt; 1: stab_side puts every point of the Smith chart on the stable side, for both planes,
    /// and each of those points is stable by a direct test.</summary>
    [Fact]
    public void UnconditionallyStable_TheWholeChartIsStable()
    {
        var r = Stable();
        foreach (var (plane, reflection) in new (string, Func<Mat<Complex>, Complex, double>)[]
                 { ("source", TwoPortCircles.OutputReflection), ("load", TwoPortCircles.InputReflection) })
        {
            var pts  = Eval(r.Ds, $"stab_circle(SP1.S, \"{plane}\", 9)").AsCube().ComplexValues;
            var side = Eval(r.Ds, $"stab_side(SP1.S, \"{plane}\")").AsCube().RealValues;
            for (int fi = 0; fi < 2; fi++)
            {
                var s = S(r.Ds, fi);
                var (k, _, _, delta, _) = RFNetwork.StabilityK(s);
                Assert.True(k > 1 && delta < 1, $"the device should be unconditionally stable: K = {k}, |Δ| = {delta}");
                var (c, rad) = CircleOf(pts, fi, 9);
                for (double re = -0.99; re <= 0.99; re += 0.11)
                    for (double im = -0.99; im <= 0.99; im += 0.11)
                    {
                        var g = new Complex(re, im);
                        if (g.Magnitude >= 1) continue;
                        bool inside = (g - c).Magnitude < rad;
                        Assert.True(inside == (side[fi] > 0), $"{plane} at {g}: on the unstable side");
                        Assert.True(reflection(s, g) < 1, $"{plane} at {g}: not stable");
                    }
            }
        }
    }

    /// <summary>A gain above max_gain has no circle: NaN points, and one warning for the measurement naming its
    /// frequencies. A gain below it draws, with no warning.</summary>
    [Fact]
    public void AGainAboveTheMaximum_IsNaNAndOneWarning()
    {
        var r = Stable();
        var ds = new DataSet();
        new MeasurementEvaluator(r.Tb, r.Nl, new Dictionary<string, DataSet> { ["SP1"] = r.Ds }).EvaluateInto(ds);
        Assert.All(ds["TooMuch"].ComplexValues, z => Assert.True(double.IsNaN(z.Real)));
        Assert.All(ds["Fine"].ComplexValues, z => Assert.True(double.IsFinite(z.Real)));
        var w = Assert.Single(r.Nl.Warnings, x => x.Contains("above the maximum gain"));
        Assert.Contains("Measurement 'TooMuch'", w);
        Assert.Contains("2 of 2 frequencies (1, 2 GHz)", w);
    }

    /// <summary>A network that is not a two-port is refused naming its shape.</summary>
    [Fact]
    public void NotATwoPort_IsRefusedNamingItsShape()
    {
        var r = RunCnl("""
            Port:P1  a 0  Num=1 Z=50 Ohm
            Port:P2  b 0  Num=2 Z=50 Ohm
            Port:P3  c 0  Num=3 Z=50 Ohm
            R:R1  a b  R=10 Ohm
            R:R2  b c  R=10 Ohm
            analysis SP1 type=sparam start=1 stop=2 npts=2 Unit=GHz
            """, _dir);
        var ex = Assert.Throws<ExpressionException>(() => Eval(r.Ds, "ga_circle(SP1.S, 1)"));
        Assert.Contains("this one is [freq, 3, 3]", ex.Message);
    }
}
