// PWL-1 — the line-standard calibration, independent of the solver.
//
// R-pwl1-2: nothing in src/Engine/Calibration names a planar type. R-pwl1-4: a structure built in closed form from known
// error boxes and an ideal (coupled) line is recovered to round-off through LineCalibrator alone, with no solver at all.
// That second gate is what the 3D wall port builds on: it calls exactly these functions with standards a 3D solver made.

using System.Numerics;
using System.Text.RegularExpressions;
using CircuitRF.Engine.Calibration;
using CircuitRF.Engine.Mom;
using NumFlat;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Calibration;

public sealed class LineCalibrationTests(ITestOutputHelper output)
{
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pwl1-2 — the shared calibration names nothing planar
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheSharedCalibrationNamesNoPlanarType()
    {
        string dir = Path.Combine(RepoRoot(), "src", "Engine", "Calibration");
        var files = Directory.GetFiles(dir, "*.cs");
        Assert.NotEmpty(files);

        var forbidden = new Regex(@"\bCircuitRF\.Engine\.Mom\b|\bPlanar[A-Z]\w*|\bGroundedSlab\b|\bEmConstants\b|\bLayerStack\b");
        foreach (var f in files)
        {
            string code = StripComments(File.ReadAllText(f));
            var hit = forbidden.Match(code);
            Assert.False(hit.Success, $"{Path.GetFileName(f)} names '{hit.Value}', which belongs to the planar solver.");
        }

        // The one value the shared code restates must be the kernels' own, to the bit.
        Assert.Equal(EmConstants.C0, LineCalibration.C0);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pwl1-4 — a synthetic two-port: known box, ideal line, recovered to round-off
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private const double Z0 = 50.0, Zc = 42.0, EpsEff = 2.3, AlphaNpPerM = 0.4;
    private const double LShort = 5e-3, LLong = 15e-3;
    private static readonly double[] Freqs = [2e9, 3e9, 4e9];

    private static Complex GammaAt(double f, double epsEff = EpsEff, double alpha = AlphaNpPerM) =>
        new(alpha, 2 * Math.PI * f * Math.Sqrt(epsEff) / LineCalibration.C0);

    [Fact]
    public void ATwoPortIsRecoveredFromItsStandardsAlone()
    {
        var cal = new LineCalibrator(LShort, [LLong - LShort],
                                     f => 1.1 * GammaAt(f).Imaginary);   // a 10 % wrong first guess, as a real one is
        double worst = 0, worstBox = 0, worstGamma = 0;

        foreach (double f in Freqs)
        {
            Complex w = 2 * Math.PI * f * Complex.ImaginaryOne;
            var box = Series(w * 0.25e-9) * Shunt(w * 0.08e-12);            // 0.25 nH, then 0.08 pF
            var mirror = Shunt(w * 0.08e-12) * Series(w * 0.25e-9);
            Complex g = GammaAt(f);

            Mat<Complex> Measure(Abcd inner) => (box * inner * mirror).ToS(Z0, Z0);
            var dutInner = Line(g, Zc, 7e-3) * Series(12.0) * Line(g, Zc, 4e-3);   // a line, a resistor, a line

            Complex cStar = g / (w * Zc);                                   // γ/(jω Z_c), so Z_c comes back exactly
            int pick = cal.SelectedIndexAt(f);
            Assert.Equal(0, pick);
            var p = cal.At(f, Measure(Line(g, Zc, LShort)), Measure(Line(g, Zc, LLong)), pick,
                           gm => LineDeembed.CharacteristicImpedance(gm, cStar, f));

            Assert.True(p.Gamma.Usable, $"βΔℓ {p.Gamma.ElectricalDegrees:F1}° at {f / 1e9} GHz is outside the usable interval");
            worstGamma = Math.Max(worstGamma, (p.Gamma.Gamma - g).Magnitude / g.Magnitude);

            // The box itself, in the line's own reference on its inner side.
            var b = box.ToS(Z0, Zc);
            worstBox = Math.Max(worstBox, Math.Max((p.Box.A11 - b[0, 0]).Magnitude,
                               Math.Max((p.Box.A22 - b[1, 1]).Magnitude, (p.Box.A21 * p.Box.A21 - b[1, 0] * b[1, 0]).Magnitude)));

            // And the DUT, de-embedded and renormalised to Z0.
            var atZc = LineDeembed.Apply(Measure(dutInner), [p.Box, p.Box]);
            var s = LineDeembed.RenormalisePseudoWave(atZc, [p.Zc, p.Zc], [Z0, Z0]);
            worst = Math.Max(worst, MaxDiff(s, dutInner.ToS(Z0, Z0)));
        }

        output.WriteLine($"worst |Δγ|/|γ| {worstGamma:E2}, worst box entry {worstBox:E2}, worst |ΔS| {worst:E2}");
        Assert.True(worstGamma < 1e-10, $"γ off by {worstGamma:E2}");
        Assert.True(worstBox   < 1e-9,  $"box off by {worstBox:E2}");
        Assert.True(worst      < 1e-9,  $"DUT off by {worst:E2}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pwl1-4 — a synthetic four-port: one calibration group per end, two modes
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACoupledFourPortIsRecoveredFromItsGroupStandardsAlone()
    {
        const int N = 2;
        // Two modes of different speed and impedance, on an ASYMMETRIC pair (so the modal basis is not even/odd).
        var tv = new Mat<double>(N, N);
        tv[0, 0] = 1.0; tv[0, 1] = 0.55;
        tv[1, 0] = 0.8; tv[1, 1] = -1.0;
        double[] eps = [2.0, 2.6], zcm = [38.0, 61.0], alpha = [0.3, 0.5];

        var cal = new LineCalibrator(LShort, [LLong - LShort], f => GammaAt(f).Imaginary);
        double worst = 0, worstGamma = 0;

        foreach (double f in Freqs)
        {
            Complex w = 2 * Math.PI * f * Complex.ImaginaryOne;
            var g = new Complex[N];
            for (int m = 0; m < N; m++) g[m] = GammaAt(f, eps[m], alpha[m]);

            // The box: a series inductance matrix (with mutual) then a shunt Maxwell capacitance matrix, both symmetric.
            var zs = Sym(w * 0.30e-9, w * 0.08e-9, w * 0.22e-9);
            var yp = Sym(w * 0.10e-12, w * -0.03e-12, w * 0.07e-12);
            var box = SeriesN(zs) * ShuntN(yp);
            var mirror = ShuntN(yp) * SeriesN(zs);

            var z0 = Enumerable.Repeat(new Complex(Z0, 0), 2 * N).ToArray();
            Mat<Complex> Measure(Abcd inner) => RFNetwork.ZToS((box * inner * mirror).ToZ(), z0);

            var dutInner = CoupledLine(tv, g, zcm, 9e-3) * ShuntN(Sym(1 / new Complex(80, 0), 0, 1 / new Complex(120, 0)));

            int pick = cal.SelectedIndexAt(f);
            var mb = cal.ModalAt(f, Measure(CoupledLine(tv, g, zcm, LShort)), Measure(CoupledLine(tv, g, zcm, LLong)), pick,
                                 m => g[m].Imaginary, tv);

            Assert.True(LineCalibrator.Usable(mb));
            Assert.True(LineCalibrator.Separable(mb, 0.5), $"modes {mb.ModeSeparationDegrees:F2}° apart");
            for (int m = 0; m < N; m++) worstGamma = Math.Max(worstGamma, (mb.Gamma[m] - g[m]).Magnitude / g[m].Magnitude);

            // One block per end, both the same box (the standards are mirror images), then modal → terminal at Z0.
            var near = new LineBlockBox([0, 1], mb.A11, mb.A12, mb.A21, mb.A22);
            var far  = new LineBlockBox([2, 3], mb.A11, mb.A12, mb.A21, mb.A22);
            var atZc = LineDeembed.ApplyBlocks(Measure(dutInner), [near, far]);

            var transform = new Mat<Complex>(2 * N, 2 * N);
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++) { transform[i, j] = tv[i, j]; transform[N + i, N + j] = tv[i, j]; }
            Complex[] zModal = [zcm[0], zcm[1], zcm[0], zcm[1]];
            var s = LineDeembed.ModalToTerminal(atZc, transform, zModal, z0);

            worst = Math.Max(worst, MaxDiff(s, RFNetwork.ZToS(dutInner.ToZ(), z0)));
        }

        output.WriteLine($"worst |Δγ|/|γ| {worstGamma:E2}, worst |ΔS| {worst:E2}");
        Assert.True(worstGamma < 1e-9, $"γ off by {worstGamma:E2}");
        Assert.True(worst      < 1e-8, $"DUT off by {worst:E2}");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Closed-form networks, as block ABCD matrices: [V1; I1] = [[A, B], [C, D]]·[V2; I2], I2 leaving port 2.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    private sealed record Abcd(Mat<Complex> A, Mat<Complex> B, Mat<Complex> C, Mat<Complex> D)
    {
        public static Abcd operator *(Abcd x, Abcd y) =>
            new(x.A * y.A + x.B * y.C, x.A * y.B + x.B * y.D, x.C * y.A + x.D * y.C, x.C * y.B + x.D * y.D);

        /// <summary>Z with both port currents flowing IN: V2 = C⁻¹I1 + C⁻¹D·I2', V1 = AC⁻¹I1 + (AC⁻¹D − B)·I2'.</summary>
        public Mat<Complex> ToZ()
        {
            int n = A.RowCount;
            var ci = C.Inverse();
            var z11 = A * ci; var z12 = A * ci * D - B; var z22 = ci * D;
            var z = new Mat<Complex>(2 * n, 2 * n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    z[i, j] = z11[i, j]; z[i, n + j] = z12[i, j];
                    z[n + i, j] = ci[i, j]; z[n + i, n + j] = z22[i, j];
                }
            return z;
        }

        /// <summary>A two-port's S with port 1 referenced to <paramref name="z1"/> and port 2 to <paramref name="z2"/>
        /// (the √Z pseudo-wave convention the calibration states its box in).</summary>
        public Mat<Complex> ToS(Complex z1, Complex z2)
        {
            Complex a = A[0, 0], b = B[0, 0], c = C[0, 0], d = D[0, 0];
            Complex den = a * z2 + b + c * z1 * z2 + d * z1;
            var s = new Mat<Complex>(2, 2);
            s[0, 0] = (a * z2 + b - c * z1 * z2 - d * z1) / den;
            s[1, 1] = (-a * z2 + b - c * z1 * z2 + d * z1) / den;
            s[1, 0] = s[0, 1] = 2 * Complex.Sqrt(z1) * Complex.Sqrt(z2) / den;
            return s;
        }
    }

    private static Mat<Complex> Scalar(Complex v) { var m = new Mat<Complex>(1, 1); m[0, 0] = v; return m; }
    private static Mat<Complex> Eye(int n) { var m = new Mat<Complex>(n, n); for (int i = 0; i < n; i++) m[i, i] = 1; return m; }
    private static Mat<Complex> Zero(int n) => new(n, n);

    private static Abcd Series(Complex z) => new(Scalar(1), Scalar(z), Scalar(0), Scalar(1));
    private static Abcd Shunt(Complex y)  => new(Scalar(1), Scalar(0), Scalar(y), Scalar(1));
    private static Abcd SeriesN(Mat<Complex> z) => new(Eye(z.RowCount), z, Zero(z.RowCount), Eye(z.RowCount));
    private static Abcd ShuntN(Mat<Complex> y)  => new(Eye(y.RowCount), Zero(y.RowCount), y, Eye(y.RowCount));

    private static Abcd Line(Complex g, double zc, double len)
    {
        Complex ch = Complex.Cosh(g * len), sh = Complex.Sinh(g * len);
        return new(Scalar(ch), Scalar(zc * sh), Scalar(sh / zc), Scalar(ch));
    }

    /// <summary>An N-conductor line from its modes: v = Tv·ṽ, i = Ti·ĩ with Ti = Tv⁻ᵀ, mode m of γ_m and Z_c,m.</summary>
    private static Abcd CoupledLine(Mat<double> tvReal, Complex[] g, double[] zc, double len)
    {
        int n = tvReal.RowCount;
        var tv = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) tv[i, j] = tvReal[i, j];
        var tvInv = tv.Inverse();
        var ti = tvInv.Transpose();

        Mat<Complex> Diag(Func<int, Complex> v) { var d = new Mat<Complex>(n, n); for (int m = 0; m < n; m++) d[m, m] = v(m); return d; }
        var ch = Diag(m => Complex.Cosh(g[m] * len));
        return new(tv * ch * tvInv,
                   tv * Diag(m => zc[m] * Complex.Sinh(g[m] * len)) * tv.Transpose(),
                   ti * Diag(m => Complex.Sinh(g[m] * len) / zc[m]) * tvInv,
                   ti * ch * tv.Transpose());
    }

    private static Mat<Complex> Sym(Complex a, Complex b, Complex c)
    {
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = a; m[0, 1] = m[1, 0] = b; m[1, 1] = c;
        return m;
    }

    private static double MaxDiff(Mat<Complex> x, Mat<Complex> y)
    {
        double d = 0;
        for (int i = 0; i < x.RowCount; i++)
            for (int j = 0; j < x.ColCount; j++) d = Math.Max(d, (x[i, j] - y[i, j]).Magnitude);
        return d;
    }

    private static string StripComments(string code) =>
        Regex.Replace(code, @"//[^\n]*|/\*.*?\*/", "", RegexOptions.Singleline);

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "circuitRF.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("circuitRF.slnx not found above " + AppContext.BaseDirectory);
    }
}
