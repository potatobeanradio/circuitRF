// Gates for brief-de-1 §2: TRL from synthetic standards, made here from fixtures whose values are known.

using System;
using System.Linq;
using System.Numerics;
using NumFlat;
using RfCore;
using Xunit;

namespace RfCore.Tests;

public sealed class TrlCalibrationTests
{
    private const double Z0 = 50, C0 = 299_792_458;

    /// <summary>The S-matrix of an ABCD matrix in a real Z0.</summary>
    private static Mat<Complex> FromAbcd(Complex a, Complex b, Complex c, Complex d)
    {
        Complex den = a + b / Z0 + c * Z0 + d;
        var s = new Mat<Complex>(2, 2);
        s[0, 0] = (a + b / Z0 - c * Z0 - d) / den;
        s[0, 1] = 2 * (a * d - b * c) / den;
        s[1, 0] = 2 / den;
        s[1, 1] = (-a + b / Z0 - c * Z0 + d) / den;
        return s;
    }

    private static (Complex A, Complex B, Complex C, Complex D) Mul((Complex A, Complex B, Complex C, Complex D) x, (Complex A, Complex B, Complex C, Complex D) y)
        => (x.A * y.A + x.B * y.C, x.A * y.B + x.B * y.D, x.C * y.A + x.D * y.C, x.C * y.B + x.D * y.D);

    private static (Complex, Complex, Complex, Complex) Line(double f, double mm, double zc, double nepersPerMAt1Ghz)
    {
        double w = 2 * Math.PI * f;
        Complex g = new(nepersPerMAt1Ghz * Math.Sqrt(f / 1e9), w / C0 * Math.Sqrt(3.0));
        Complex gl = g * mm * 1e-3;
        return (Complex.Cosh(gl), zc * Complex.Sinh(gl), Complex.Sinh(gl) / zc, Complex.Cosh(gl));
    }

    /// <summary>A launch: series L, shunt C, then a mismatched lossy line.</summary>
    private static Mat<Complex> Launch(double f, double mm, double zc, double lossNp, double lH = 0.3e-9, double cF = 0.15e-12)
    {
        double w = 2 * Math.PI * f;
        var m = Mul(Mul((1, new Complex(0, w * lH), 0, 1), (1, 0, new Complex(0, w * cF), 1)), Line(f, mm, zc, lossNp));
        return FromAbcd(m.Item1, m.Item2, m.Item3, m.Item4);
    }

    private static Mat<Complex> Flip(Mat<Complex> s)
    {
        var r = new Mat<Complex>(2, 2);
        r[0, 0] = s[1, 1]; r[0, 1] = s[1, 0]; r[1, 0] = s[0, 1]; r[1, 1] = s[0, 0];
        return r;
    }

    private static Complex Terminated(Mat<Complex> s, Complex load) => s[0, 0] + s[0, 1] * s[1, 0] * load / (1 - s[1, 1] * load);

    private sealed record Bench(double[] F, SNP Thru, SNP Reflect, SNP LineStd, Mat<Complex>[] A, Mat<Complex>[] B, Mat<Complex>[] Dut, Mat<Complex>[] Measured);

    /// <summary>
    /// Two different lossy, mismatched launches, a lossy 8 mm line standard, a short of −0.98 with a 3 ps offset, and a
    /// DUT measured through both fixtures, at 0.5-20 GHz: the line passes 180° near 11 GHz and 360° past 20.
    /// <paramref name="aFactory"/> replaces fixture A when a test needs a particular one.
    /// </summary>
    private static Bench Make(Func<double, Mat<Complex>>? aFactory = null)
    {
        var f = Enumerable.Range(0, 40).Select(i => 0.5e9 + i * 0.5e9).ToArray();
        int n = f.Length;
        var thru = new Mat<Complex>[n]; var refl = new Mat<Complex>[n]; var line = new Mat<Complex>[n];
        var a = new Mat<Complex>[n]; var b = new Mat<Complex>[n]; var dut = new Mat<Complex>[n]; var meas = new Mat<Complex>[n];
        for (int k = 0; k < n; k++)
        {
            a[k] = aFactory?.Invoke(f[k]) ?? Launch(f[k], 12, 57.5, 0.4);
            b[k] = Flip(Launch(f[k], 9, 45, 0.3, 0.2e-9, 0.1e-12));   // port 1 the part's side
            dut[k] = Launch(f[k], 5, 80, 0.2, 0.5e-9, 0.3e-12);
            var ln = Line(f[k], 8, Z0, 0.5);
            thru[k] = NetworkInverse.Cascade(a[k], b[k]);
            line[k] = NetworkInverse.Cascade(NetworkInverse.Cascade(a[k], FromAbcd(ln.Item1, ln.Item2, ln.Item3, ln.Item4)), b[k]);
            Complex shortStd = -0.98 * Complex.Exp(new Complex(0, -2 * Math.PI * f[k] * 3e-12));
            refl[k] = new Mat<Complex>(2, 2);
            refl[k][0, 0] = Terminated(a[k], shortStd);
            refl[k][1, 1] = Terminated(Flip(b[k]), shortStd);
            meas[k] = NetworkInverse.Cascade(NetworkInverse.Cascade(a[k], dut[k]), b[k]);
        }
        return new Bench(f, new SNP(f, thru), new SNP(f, refl), new SNP(f, line), a, b, dut, meas);
    }

    private static double Worst(Mat<Complex>[] x, Mat<Complex>[] y)
        => x.Zip(y, NetworkInverseTests.MaxDiff).Max();

    private static Mat<Complex>[] Deembedded(TrlResult r, Mat<Complex>[] measured)
        => measured.Select((m, k) =>
        {
            Assert.True(NetworkInverse.TryInvert(r.FixtureA.Matrices[k], out var ia, out _));
            Assert.True(NetworkInverse.TryInvert(r.FixtureB.Matrices[k], out var ib, out _));
            return NetworkInverse.Cascade(NetworkInverse.Cascade(ia, m), ib);
        }).ToArray();

    [Fact]
    public void TwoDifferentLossyFixtures_AndTheDut_AreRecovered_AcrossThe180DegreeCrossing()
    {
        var bench = Make();
        var (r, refusal) = TrlCalibration.Solve(bench.Thru, bench.Reflect, bench.LineStd, TrlReflect.Short);
        Assert.True(r is not null, refusal);
        Assert.True(Worst(r!.FixtureA.Matrices, bench.A) <= 1e-10);
        Assert.True(Worst(r.FixtureB.Matrices, bench.B) <= 1e-10);
        Assert.True(Worst(Deembedded(r, bench.Measured), bench.Dut) <= 1e-10);
        Assert.True(r.LineDegrees[^1] > 300, $"the line reaches {r.LineDegrees[^1]:F0}°, so this crosses 180° and the second usable band");
        Assert.All(r.Gamma, g => Assert.True(Math.Abs(g.Magnitude - 0.98) < 1e-9));
        Assert.True(r.UnusableCount > 0 && r.UnusableCount < r.Usable.Length);
    }

    // Without the continuity rules the part still comes out right, because it depends only on products of the two free signs,
    // but the fixtures do not. This is the reason the rules are requirements.
    [Fact]
    public void WithoutContinuityTracking_TheDutIsStillRight_ButTheFixturesAreNot()
    {
        var bench = Make();
        var (r, _) = TrlCalibration.Solve(bench.Thru, bench.Reflect, bench.LineStd, TrlReflect.Short, track: false);
        Assert.NotNull(r);
        Assert.True(Worst(r!.FixtureA.Matrices, bench.A) > 0.5);
        // Up to 10.5 GHz the line is under 175°, where choosing the eigenvalue by its sign is still right; above it the
        // untracked choice is wrong and nothing comes out right.
        var below180 = Enumerable.Range(0, bench.F.Length).Where(k => bench.F[k] <= 10.5e9).ToArray();
        Assert.True(below180.Max(k => NetworkInverseTests.MaxDiff(Deembedded(r, bench.Measured)[k], bench.Dut[k])) <= 1e-10);
    }

    // The solve is written in the eigenvectors, not in ratios that divide by the fixture's S22; a perfectly matched fixture
    // has none.
    [Fact]
    public void APerfectlyMatchedFixture_IsSolved()
    {
        var bench = Make(f => { var l = Line(f, 6, Z0, 0.3); return FromAbcd(l.Item1, l.Item2, l.Item3, l.Item4); });
        Assert.Equal(0.0, bench.A[3][1, 1].Magnitude, 12);
        var (r, refusal) = TrlCalibration.Solve(bench.Thru, bench.Reflect, bench.LineStd, TrlReflect.Short);
        Assert.True(r is not null, refusal);
        Assert.True(Worst(r!.FixtureA.Matrices, bench.A) <= 1e-10);
    }

    [Fact]
    public void StandardsOnDifferentGrids_AreRefused_NamingTheFile()
    {
        var bench = Make();
        var shifted = bench.F.Select(f => f + 1e6).ToArray();
        var line = new SNP(shifted, bench.LineStd.Matrices);
        var (r, refusal) = TrlCalibration.Solve(bench.Thru, bench.Reflect, line, TrlReflect.Short);
        Assert.Null(r);
        Assert.Contains("the Line file's point 1", refusal);
    }

    [Fact]
    public void OpenStatedForAShort_SolvesTheOtherRoot_WithoutFailing()
    {
        var bench = Make();
        var (r, _) = TrlCalibration.Solve(bench.Thru, bench.Reflect, bench.LineStd, TrlReflect.Open);
        Assert.NotNull(r);
        Assert.All(r!.Gamma, g => Assert.True(g.Real > 0));
        Assert.True(Worst(r.FixtureA.Matrices, bench.A) > 0.1);
    }
}
