using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.Tests.Mom;

/// <summary>
/// <b>Tier 4 — network level.</b> Properties that must hold for any passive uniform line,
/// independent of how good the numbers are: reciprocity, passivity, losslessness in the lossless
/// limit, cascade consistency, and R-mom-11's fill-count guard.
/// </summary>
public class NetworkPropertyTests
{
    private static readonly double[] Sweep = BuildSweep(1e8, 2e10, 41);

    private static double[] BuildSweep(double f0, double f1, int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = f0 + (f1 - f0) * i / (n - 1.0);
        return f;
    }

    private static EmProblem Lossless(double lengthM = 0.02)
        => EmProblemBuilders.Microstrip(2.9e-3, 1.6e-3, 35e-6, 4.4, tanD: 0,
                                        sigmaSm: double.PositiveInfinity,
                                        lengthMeters: lengthM,
                                        groundSigmaSm: double.PositiveInfinity);

    private static EmProblem Lossy(double lengthM = 0.02)
        => EmProblemBuilders.Microstrip(2.9e-3, 1.6e-3, 35e-6, 4.4, tanD: 0.02,
                                        lengthMeters: lengthM);

    private static DataSet Solve(EmProblem p, double[] freqs)
        => new QuasiStaticKernel().Solve(p, EmMeshSettings.Default, freqs, CancellationToken.None);

    private static Mat<Complex> SAt(DataSet ds, int fi)
    {
        var cube = ds["S"];
        var v = cube.ComplexValues;
        var m = new Mat<Complex>(2, 2);
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            m[i, j] = v[fi * 4 + i * 2 + j];
        return m;
    }

    [Fact]
    public void T4_1_Reciprocity_S12EqualsS21()
    {
        var ds = Solve(Lossy(), Sweep);
        for (int i = 0; i < Sweep.Length; i++)
        {
            var s = SAt(ds, i);
            Assert.Equal(s[0, 1].Real, s[1, 0].Real, 1e-12);
            Assert.Equal(s[0, 1].Imaginary, s[1, 0].Imaginary, 1e-12);
            Assert.Equal(s[0, 0].Real, s[1, 1].Real, 1e-12);   // and symmetric, for a uniform line
        }
    }

    [Fact]
    public void T4_2_Passivity_EigenvaluesOfIMinusSHermitianSAreNonNegative()
    {
        var ds = Solve(Lossy(), Sweep);
        for (int i = 0; i < Sweep.Length; i++)
        {
            var s = SAt(ds, i);
            // For a symmetric reciprocal 2-port, I − SᴴS is 2×2 Hermitian; its eigenvalues are
            // (tr ± √(tr² − 4·det))/2, both ≥ 0 iff tr ≥ 0 and det ≥ 0.
            var m = HermitianIMinusSHS(s);
            double tr = m[0, 0].Real + m[1, 1].Real;
            double det = (m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0]).Real;
            Assert.True(tr >= -1e-12, $"f={Sweep[i]:G4}: trace {tr:E3} < 0");
            Assert.True(det >= -1e-12, $"f={Sweep[i]:G4}: det {det:E3} < 0");
        }
    }

    [Fact]
    public void T4_3_Losslessness_WithPerfectMetalAndZeroTanD()
    {
        var ds = Solve(Lossless(), Sweep);
        for (int i = 0; i < Sweep.Length; i++)
        {
            var s = SAt(ds, i);
            double sum = s[0, 0].Magnitude * s[0, 0].Magnitude + s[1, 0].Magnitude * s[1, 0].Magnitude;
            Assert.Equal(1.0, sum, 1e-9);
        }

        // …and the attenuation the DataSet reports is exactly zero, not merely small.
        foreach (double a in ds["tline.AttenDbPerM"].RealValues) Assert.Equal(0.0, a, 1e-12);
        foreach (double r in ds["tline.Rpul"].RealValues) Assert.Equal(0.0, r, 1e-15);
        foreach (double g in ds["tline.Gpul"].RealValues) Assert.Equal(0.0, g, 1e-15);
    }

    /// <summary>
    /// A line of length 2ℓ must equal two length-ℓ lines cascaded. This is the property that would
    /// break first if γ or Z_c carried a factor-of-length error, and it is checked through ABCD so
    /// it is independent of the Z→S conversion.
    /// </summary>
    [Fact]
    public void T4_4_CascadeIdentity_TwoHalfLinesEqualOneFullLine()
    {
        var full = Solve(Lossy(0.04), Sweep);
        var half = Solve(Lossy(0.02), Sweep);

        var z0 = new Complex(50, 0);
        for (int i = 0; i < Sweep.Length; i++)
        {
            var a = SToAbcd(SAt(half, i), z0);
            var aa = Multiply(a, a);
            var want = SToAbcd(SAt(full, i), z0);
            for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                Assert.Equal(want[r, c].Real, aa[r, c].Real, Math.Max(Math.Abs(want[r, c].Real), 1.0) * 1e-9);
        }
    }

    /// <summary>
    /// <b>R-mom-11, enforced with a counter rather than a comment.</b> [C], [C₀] and ∂L/∂n are
    /// frequency-independent, so a 1001-point sweep must cost exactly the same matrix fills as a
    /// 3-point one. This is the property that makes v1 "dramatically snappier than the thing that
    /// replaces it", and it is easy to lose in a later refactor.
    /// </summary>
    [Fact]
    public void T4_5_A1001PointSweep_FillsTheMatrixNoMoreOftenThanA3PointOne()
    {
        var kernel = new QuasiStaticKernel();
        var p = Lossy();

        var few  = kernel.SolveDetailed(p, EmMeshSettings.Default, BuildSweep(1e8, 2e10, 3));
        var many = kernel.SolveDetailed(p, EmMeshSettings.Default, BuildSweep(1e8, 2e10, 1001));

        Assert.Equal(few.Rlgc.MatrixFillCount, many.Rlgc.MatrixFillCount);

        // Four fills, and each one is named: [C] with the real stackup, [C₀] in air, and the two
        // Wheeler recessions (the signal conductor, and the ground plane).
        Assert.Equal(4, many.Rlgc.MatrixFillCount);
        Assert.Equal(1001, many.Data["tline.Zc"].ComplexValues.Length);
    }

    [Fact]
    public void T4_6_TheDataSetFollowsTheHouseConvention()
    {
        var ds = Solve(Lossy(), Sweep);

        // S + per-port Z0 in the default group, exactly as SParameterEngine ends.
        Assert.True(ds.Contains("S"));
        Assert.True(ds.Contains("Z0"));
        Assert.Equal(DataKind.Complex, ds["S"].DataKind);
        Assert.Equal([Sweep.Length, 2, 2], ds["S"].Axes.Select(a => a.Length).ToArray());
        Assert.Equal("freq", ds["S"].Axes[0].Name);
        Assert.Equal(2, ds["Z0"].ComplexValues.Length);

        // …and the transmission-line quantities a line solver is uniquely able to report.
        foreach (var name in new[] { "Zc", "Gamma" })
        {
            var c = ds["tline." + name];
            Assert.Equal(DataKind.Complex, c.DataKind);
            Assert.Equal(Sweep.Length, c.ComplexValues.Length);
        }
        foreach (var name in new[] { "Eeff", "AttenDbPerM", "Rpul", "Lpul", "Gpul", "Cpul" })
        {
            var c = ds["tline." + name];
            Assert.Equal(DataKind.Real, c.DataKind);
            Assert.Equal(Sweep.Length, c.RealValues.Length);
        }

        // Round-trips back through the standard SNP path.
        var snp = DataSetBuilder.ToSnp(ds, DataSetBuilder.FindCubeSpec(ds, "S")!);
        Assert.Equal(2, snp.Ports);
        Assert.Equal(Sweep.Length, snp.FrequencyCount);
    }

    /// <summary>
    /// <b>R-mom-15.</b> De-embedding is a no-op for kernel A, and that is a finding, not a shortcut:
    /// γ and Z_c are computed analytically and the Z of a uniform line of length ℓ is formed
    /// directly, so the reference planes are exactly at the line ends by construction. The
    /// observable consequence — and the reason this is worth a test rather than a comment — is that
    /// the phase of S₂₁ is exactly −βℓ with no port-discontinuity offset.
    /// </summary>
    [Fact]
    public void T4_7_ReferencePlanesAreExactlyAtTheLineEnds()
    {
        var p = Lossless();
        var ds = Solve(p, Sweep);
        var gamma = ds["tline.Gamma"].ComplexValues;

        for (int i = 0; i < Sweep.Length; i++)
        {
            var s = SAt(ds, i);
            if (s[0, 0].Magnitude > 0.2) continue;   // skip near-resonance, where phase is ill-conditioned

            double wantPhase = -(gamma[i] * p.LengthMeters).Imaginary;
            double gotPhase = s[1, 0].Phase;
            double diff = Math.IEEERemainder(gotPhase - wantPhase, 2 * Math.PI);
            Assert.True(Math.Abs(diff) < 5e-3,
                $"f={Sweep[i]:G4}: ∠S₂₁ = {gotPhase:F5} rad, −βℓ = {wantPhase:F5} rad");
        }
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static Mat<Complex> HermitianIMinusSHS(Mat<Complex> s)
    {
        var m = new Mat<Complex>(2, 2);
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
        {
            Complex acc = 0;
            for (int k = 0; k < 2; k++) acc += Complex.Conjugate(s[k, i]) * s[k, j];
            m[i, j] = (i == j ? Complex.One : Complex.Zero) - acc;
        }
        return m;
    }

    private static Mat<Complex> SToAbcd(Mat<Complex> s, Complex z0)
    {
        var d = 2.0 * s[1, 0];
        var m = new Mat<Complex>(2, 2);
        m[0, 0] = ((1 + s[0, 0]) * (1 - s[1, 1]) + s[0, 1] * s[1, 0]) / d;
        m[0, 1] = z0 * ((1 + s[0, 0]) * (1 + s[1, 1]) - s[0, 1] * s[1, 0]) / d;
        m[1, 0] = ((1 - s[0, 0]) * (1 - s[1, 1]) - s[0, 1] * s[1, 0]) / (d * z0);
        m[1, 1] = ((1 - s[0, 0]) * (1 + s[1, 1]) + s[0, 1] * s[1, 0]) / d;
        return m;
    }

    private static Mat<Complex> Multiply(Mat<Complex> a, Mat<Complex> b)
    {
        var m = new Mat<Complex>(2, 2);
        for (int i = 0; i < 2; i++)
        for (int j = 0; j < 2; j++)
            m[i, j] = a[i, 0] * b[0, j] + a[i, 1] * b[1, j];
        return m;
    }
}
