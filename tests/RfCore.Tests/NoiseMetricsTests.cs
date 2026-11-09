// ================================================================
//  NoiseMetricsTests.cs — two-port noise parameters, correlation matrices and circles.
//
//  The oracles are a textbook example (Pozar, Microwave Engineering 4th ed., Ex. 12.5), Bosma's
//  theorem (a passive network at T has Cs = kT(I − S·Sᴴ) and C_Y = 2kT(Y + Yᴴ), so the C_Y transform
//  must take one to the other, complex Z0 included), and an independent formula set: the noise
//  parameters read straight off the noise waves (Wedge & Rutledge) against the C_Y → C_A path. The
//  random networks are built from Y with the Kurokawa power-wave S written out here, not through
//  RFNetwork, so the conversion the production path uses is tested rather than reused.
// ================================================================

using System;
using System.Linq;
using System.Numerics;
using NumFlat;
using RfCore.Data;
using Xunit;

namespace RfCore.Tests;

public sealed class NoiseMetricsTests
{
    private static double Db(double db) => Math.Pow(10, db / 10);
    private static Complex Polar(double mag, double deg) => Complex.FromPolarCoordinates(mag, deg * Math.PI / 180);

    [Fact]
    public void PozarExample12_5_TheTwoDecibelCircle()
    {
        // GaAs FET: Fmin = 1.6 dB, Γopt = 0.62∠100°, Rn = 20 Ω, Z0 = 50 Ω; the F = 2 dB circle.
        var p = new TwoPortNoise(Db(1.6), Polar(0.62, 100), 20, 50);
        var (c, r) = NoiseMetrics.NoiseCircle(p, Db(2.0));

        double n = 0.62 / c.Magnitude - 1;              // centre = Γopt/(1 + N)
        Assert.Equal(0.1019, n, 3);                      // book: N = 0.1019
        Assert.Equal(0.56, c.Magnitude, 2);              // book: 0.56∠100°
        Assert.Equal(100, c.Phase * 180 / Math.PI, 9);
        Assert.InRange(r, 0.235, 0.25);                  // book: 0.24, from its rounded N (0.2454 unrounded)

        // Every point on the circle is at 2 dB.
        foreach (double deg in new[] { 0.0, 77, 190, 300 })
            Assert.Equal(Db(2.0), NoiseMetrics.NoiseFactor(p, c + Polar(r, deg)), 12);
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(25.0)]
    public void ChainCorrelationRoundTripsTheParameters(double r)
    {
        var rng = new Random(7);
        for (int k = 0; k < 20; k++)
        {
            var p  = RandomRealizable(rng, r);
            var ca = NoiseMetrics.ChainCorrelation(p);

            Assert.Equal(ca[0, 1], Complex.Conjugate(ca[1, 0]));
            Assert.True(Det(ca) >= 0 && ca[0, 0].Real > 0 && ca[1, 1].Real > 0);     // PSD

            var q = NoiseMetrics.FromChainCorrelation(ca, r);
            AssertSame(p, q, 1e-12);
            Assert.True(NoiseMetrics.IsRealizable(q));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BosmaPassiveNetworkGivesTwoKTTimesTheHermitianPartOfY(bool complexZ0)
    {
        const double t = 400;
        var rng = new Random(11);
        foreach (int n in new[] { 2, 3 })
        {
            var z0 = (complexZ0
                ? new Complex[] { new(50, -10), new(30, 20), new(75, 5) }
                : new Complex[] { 50, 25, 75 })[..n];

            // A passive Y: a positive semidefinite Hermitian part plus a lossless (anti-Hermitian) part.
            var a = RandomMat(rng, n, 0.02);
            var w = RandomMat(rng, n, 0.02);
            var y = a * a.ConjugateTranspose() + w - w.ConjugateTranspose();
            var s = PowerWaveS(y, z0);

            var cs = Scale(Identity(n) - s * s.ConjugateTranspose(), NoiseMetrics.Boltzmann * t);
            var cy = NoiseMetrics.AdmittanceCorrelation(s, cs, z0);
            var expected = Scale(y + y.ConjugateTranspose(), 2 * NoiseMetrics.Boltzmann * t);

            AssertMatClose(expected, cy, 1e-10);
        }
    }

    [Theory]
    [InlineData(50.0)]
    [InlineData(25.0)]
    public void TheChainPathAndTheDirectWavePathAgree(double z0)
    {
        var rng = new Random(3);
        Complex[] z = [z0, z0];
        for (int k = 0; k < 20; k++)
        {
            var p = RandomRealizable(rng, z0);
            var (s, cs) = NoisyTwoPort(rng, p, z);

            AssertSame(p, NoiseMetrics.FromWaves(s, cs, z, z0), 1e-9);
            AssertSame(p, NoiseMetrics.FromWavesDirect(s, cs, z0), 1e-9);

            // F from a Z0 source: the wave formula against the noise-parameter one at Γs = 0.
            Assert.Equal(NoiseMetrics.NoiseFactor(p, 0), NoiseMetrics.NoiseFactorAtZ0(s, cs), 9);
        }
    }

    [Fact]
    public void ComplexReferenceImpedancesKeepTheNoiseParameters()
    {
        var rng = new Random(5);
        Complex[] z = [new(40, 15), new(60, -5)];
        for (int k = 0; k < 10; k++)
        {
            var p = RandomRealizable(rng, 50);
            var (s, cs) = NoisyTwoPort(rng, p, z);

            AssertSame(p, NoiseMetrics.FromWaves(s, cs, z, 50), 1e-9);

            // Port 1 driven from its own complex Z0: the same F the parameters give at that source.
            Complex gs = (z[0] - 50) / (z[0] + 50);
            Assert.Equal(NoiseMetrics.NoiseFactor(p, gs), NoiseMetrics.NoiseFactorAtZ0(s, cs), 9);
        }
    }

    [Fact]
    public void FAtGammaOptIsFminAndTheRealizabilityBoundaryIsFminMinusOneEqualsFourRnGopt()
    {
        var p = new TwoPortNoise(Db(1.2), Polar(0.4, -60), 12, 50);
        Assert.Equal(p.Fmin, NoiseMetrics.NoiseFactor(p, p.GammaOpt), 14);

        // On the boundary C_A is singular and still positive semidefinite.
        var edge = p with { Rn = (p.Fmin - 1) / (4 * p.Yopt.Real) };
        Assert.True(NoiseMetrics.IsRealizable(edge));
        var ca = NoiseMetrics.ChainCorrelation(edge);
        Assert.True(Math.Abs(Det(ca)) < 1e-12 * ca[0, 0].Real * ca[1, 1].Real);

        // One percent less Rn and it is not: det C_A < 0.
        var beyond = edge with { Rn = edge.Rn * 0.99 };
        Assert.False(NoiseMetrics.IsRealizable(beyond));
        Assert.True(Det(NoiseMetrics.ChainCorrelation(beyond)) < 0);

        Assert.False(NoiseMetrics.IsRealizable(p with { Fmin = 0.99 }));
    }

    [Fact]
    public void ANoiselessTwoPortReadsZeroDecibels()
    {
        var q = NoiseMetrics.FromChainCorrelation(new Mat<Complex>(2, 2), 50);
        Assert.Equal(new TwoPortNoise(1, 0, 0, 50), q);
        Assert.Equal(0, q.NFminDb);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A realizable set: Fmin − 1 strictly below 4·Rn·Gopt.</summary>
    private static TwoPortNoise RandomRealizable(Random rng, double r)
    {
        double  fmin  = 1 + 0.05 + 1.5 * rng.NextDouble();
        Complex gamma = Complex.FromPolarCoordinates(0.8 * rng.NextDouble(), 2 * Math.PI * rng.NextDouble());
        double  gopt  = ((1 - gamma) / (r * (1 + gamma))).Real;
        return new(fmin, gamma, (fmin - 1) / (4 * gopt) * (1.05 + 3 * rng.NextDouble()), r);
    }

    /// <summary>A random active two-port with the given noise parameters, as (S, Cs) at
    /// <paramref name="z0"/>: C_A → C_Y on its Y → Cs by the inverse of the wave transform.</summary>
    private static (Mat<Complex> S, Mat<Complex> Cs) NoisyTwoPort(Random rng, TwoPortNoise p, Complex[] z0)
    {
        var y = RandomMat(rng, 2, 0.01);
        y[1, 0] += 0.1;                                                     // a transconductance
        var cy = NoiseMetrics.AdmittanceFromChain(NoiseMetrics.ChainCorrelation(p), y);

        var mInv = WaveTransform(y, z0).Inverse();
        return (PowerWaveS(y, z0), mInv * cy * mInv.ConjugateTranspose());
    }

    /// <summary>Kurokawa: a = F(V + Z0·I), b = F(V − Z0*·I), F = diag(1/2√Re Z0), I = Y·V ⇒
    /// S = F(I − Z0*·Y)(I + Z0·Y)⁻¹F⁻¹.</summary>
    private static Mat<Complex> PowerWaveS(Mat<Complex> y, Complex[] z0)
    {
        int n = z0.Length;
        Mat<Complex> f = Identity(n), fInv = Identity(n), zc = Identity(n), zd = Identity(n);
        for (int i = 0; i < n; i++)
        {
            f[i, i]    = 1 / (2 * Math.Sqrt(z0[i].Real));
            fInv[i, i] = 2 * Math.Sqrt(z0[i].Real);
            zc[i, i]   = Complex.Conjugate(z0[i]);
            zd[i, i]   = z0[i];
        }
        return f * (Identity(n) - zc * y) * (Identity(n) + zd * y).Inverse() * fInv;
    }

    /// <summary>(I + Y·Z0)·R^−½ — C_Y = M·Cs·Mᴴ.</summary>
    private static Mat<Complex> WaveTransform(Mat<Complex> y, Complex[] z0)
    {
        int n = z0.Length;
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            m[i, j] = ((i == j ? 1 : 0) + y[i, j] * z0[j]) / Math.Sqrt(z0[j].Real);
        return m;
    }

    private static Mat<Complex> RandomMat(Random rng, int n, double scale)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++)
        for (int j = 0; j < n; j++)
            m[i, j] = new Complex(rng.NextDouble() - 0.5, rng.NextDouble() - 0.5) * scale;
        return m;
    }

    private static Mat<Complex> Identity(int n)
    {
        var m = new Mat<Complex>(n, n);
        for (int i = 0; i < n; i++) m[i, i] = 1;
        return m;
    }

    private static Mat<Complex> Scale(Mat<Complex> m, double k)
    {
        var r = new Mat<Complex>(m.RowCount, m.ColCount);
        for (int i = 0; i < m.RowCount; i++)
        for (int j = 0; j < m.ColCount; j++)
            r[i, j] = m[i, j] * k;
        return r;
    }

    private static double Det(Mat<Complex> c) => (c[0, 0] * c[1, 1] - c[0, 1] * c[1, 0]).Real;

    private static void AssertMatClose(Mat<Complex> expected, Mat<Complex> actual, double rel)
    {
        double scale = 0, err = 0;
        for (int i = 0; i < expected.RowCount; i++)
        for (int j = 0; j < expected.ColCount; j++)
        {
            scale = Math.Max(scale, expected[i, j].Magnitude);
            err   = Math.Max(err, (expected[i, j] - actual[i, j]).Magnitude);
        }
        Assert.True(err <= rel * scale, $"max |Δ| {err:G3} against max |entry| {scale:G3}");
    }

    private static void AssertSame(TwoPortNoise expected, TwoPortNoise actual, double rel)
    {
        Assert.Equal(expected.R, actual.R);
        Assert.True(Math.Abs(actual.Fmin - expected.Fmin) <= rel * expected.Fmin, $"Fmin {expected.Fmin} vs {actual.Fmin}");
        Assert.True(Math.Abs(actual.Rn - expected.Rn) <= rel * expected.Rn, $"Rn {expected.Rn} vs {actual.Rn}");
        Assert.True((actual.GammaOpt - expected.GammaOpt).Magnitude <= rel, $"GammaOpt {expected.GammaOpt} vs {actual.GammaOpt}");
    }
}
