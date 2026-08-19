using System.Numerics;
using CircuitRF.WBond.Mom;

namespace CircuitRF.WBond.Tests.Mom;

/// <summary>
/// The two factorisation changes WM-3 makes to the kernel's arithmetic: <see cref="CholeskyFactor"/>'s
/// explicit in-place inverse (M1) and <see cref="ComplexLdlt"/>'s complex-symmetric factorisation (M2).
///
/// <para><b>These are correctness gates and they are routine, not Benchmark.</b> Both changes exist for
/// speed, and both produce a finite, plausible, wrong answer when they are wrong — an index swapped in
/// M1's four-term expression, or a silently broken-down pivot in M2 — so hiding them behind an opt-in
/// flag would be hiding the half that matters.</para>
/// </summary>
public sealed class MomFactorisationTests
{
    // ---------------------------------------------------------------- M1: the SPD inverse

    /// <summary>
    /// <c>A·A⁻¹ = I</c>, and the inverse agrees entry for entry with the one the triangular solves
    /// would have produced. <b>The second half is the one that matters</b>: it is the route M1 replaced,
    /// so an inverse that satisfies its own identity but disagrees with the solves would be a new
    /// definition rather than a faster path to the old one.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(300)]
    public void TheInPlaceInverseIsTheInverse(int n)
    {
        var a = Spd(n, seed: 3 + n);

        var bySolves = new double[n * n];
        var factor = CholeskyFactor.Factor(a, n);
        for (int col = 0; col < n; col++)
        {
            var e = new double[n];
            e[col] = 1.0;
            factor.SolveInPlace(e);
            for (int row = 0; row < n; row++) bySolves[row * n + col] = e[row];
        }

        var inverse = CholeskyFactor.Factor(a, n).InvertInPlace();

        double scale = 0.0;
        for (int i = 0; i < bySolves.Length; i++) scale = Math.Max(scale, Math.Abs(bySolves[i]));

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                Assert.True(Math.Abs(inverse[i * n + j] - bySolves[i * n + j]) < 1e-9 * scale,
                    $"A^-1[{i},{j}] = {inverse[i * n + j]:E6} against {bySolves[i * n + j]:E6} from the solves.");

                double identity = 0.0;
                for (int k = 0; k < n; k++) identity += a[i * n + k] * inverse[k * n + j];
                Assert.True(Math.Abs(identity - (i == j ? 1.0 : 0.0)) < 1e-9,
                    $"(A A^-1)[{i},{j}] = {identity:E6}.");
            }
    }

    /// <summary>
    /// Both triangles are populated, and they agree. A caller that had to remember which half was
    /// written is a caller that reads the wrong one — and <see cref="MomAssembly"/>'s four-term
    /// expressions gather from both.
    /// </summary>
    [Fact]
    public void TheInverseIsMirroredIntoBothTriangles()
    {
        const int n = 50;
        var inverse = CholeskyFactor.Factor(Spd(n, seed: 17), n).InvertInPlace();

        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                Assert.Equal(inverse[i * n + j], inverse[j * n + i]);
    }

    /// <summary>
    /// The parallel and serial paths of the inverse are the same arithmetic in a different order, so
    /// they agree to rounding. Taken above <c>ParallelThreshold</c>, where the parallel path is actually
    /// taken — below it the flag does nothing and the test would prove nothing.
    /// </summary>
    [Fact]
    public void TheParallelInverseAgreesWithTheSerialOne()
    {
        const int n = 600;
        var a = Spd(n, seed: 23);

        var serial = CholeskyFactor.Factor(a, n).InvertInPlace(parallel: false);
        var parallel = CholeskyFactor.Factor(a, n).InvertInPlace(parallel: true);

        double scale = 0.0;
        for (int i = 0; i < serial.Length; i++) scale = Math.Max(scale, Math.Abs(serial[i]));

        for (int i = 0; i < serial.Length; i++)
            Assert.True(Math.Abs(serial[i] - parallel[i]) < 1e-12 * scale,
                $"entry {i}: serial {serial[i]:E6}, parallel {parallel[i]:E6}.");
    }

    /// <summary>
    /// The factor is <b>consumed</b>. A second inversion, or a solve after one, is a bug in the caller
    /// and is refused rather than answered from an array that no longer holds L.
    /// </summary>
    [Fact]
    public void TheConsumedFactorRefusesToBeUsedAgain()
    {
        var factor = CholeskyFactor.Factor(Spd(8, seed: 5), 8);
        factor.InvertInPlace();

        Assert.Throws<InvalidOperationException>(() => factor.InvertInPlace());
        Assert.Throws<InvalidOperationException>(() => factor.SolveInPlace(new double[8]));
    }

    // ---------------------------------------------------------------- M2: the complex-symmetric factor

    /// <summary>
    /// <see cref="ComplexLdlt"/> and <see cref="ComplexLu"/> solve the same system to the same answer.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(120)]
    public void TheSymmetricFactorisationSolvesWhatTheLuSolves(int n)
    {
        var a = ComplexSymmetric(n, seed: 31 + n);
        var b = new Complex[n];
        var rng = new Random(97);
        for (int i = 0; i < n; i++) b[i] = new Complex(rng.NextDouble(), rng.NextDouble());

        var byLu = ComplexLu.Factor(a, n).Solve(b);
        var byLdlt = ComplexLdlt.Factor(a, n).Solve(b);

        double scale = 0.0;
        foreach (var v in byLu) scale = Math.Max(scale, v.Magnitude);

        for (int i = 0; i < n; i++)
            Assert.True((byLu[i] - byLdlt[i]).Magnitude < 1e-9 * scale,
                $"x[{i}]: LU {byLu[i]}, LDLt {byLdlt[i]}.");
    }

    /// <summary>
    /// The multi-right-hand-side solve — one triangular sweep for all T ports — is the same as T
    /// separate ones. It is the form the frequency loop actually calls.
    /// </summary>
    [Fact]
    public void TheMultiRhsSolveMatchesTheSingleRhsOne()
    {
        const int n = 40, cols = 5;
        var a = ComplexSymmetric(n, seed: 61);
        var factor = ComplexLdlt.Factor(a, n);

        var block = new Complex[n * cols];
        var rng = new Random(101);
        for (int i = 0; i < block.Length; i++) block[i] = new Complex(rng.NextDouble(), rng.NextDouble());

        var expected = new Complex[n * cols];
        for (int c = 0; c < cols; c++)
        {
            var rhs = new Complex[n];
            for (int i = 0; i < n; i++) rhs[i] = block[i * cols + c];
            var x = factor.Solve(rhs);
            for (int i = 0; i < n; i++) expected[i * cols + c] = x[i];
        }

        factor.SolveInPlace(block, cols);

        for (int i = 0; i < block.Length; i++)
            Assert.True((block[i] - expected[i]).Magnitude < 1e-12 * (1.0 + expected[i].Magnitude),
                $"entry {i}: block {block[i]}, single-rhs {expected[i]}.");
    }

    /// <summary>
    /// <b>The failure mode this factorisation has, on a matrix that is perfectly well conditioned.</b>
    /// <c>[[0,1],[1,0]]</c> is symmetric, has determinant −1 and a condition number of 1 — and has no
    /// unpivoted LDLᵀ at all, because its first pivot is zero. A partial-pivoted LU solves it without
    /// noticing.
    ///
    /// <para>This is the whole argument for <see cref="WireMomSettings.MinimumPivotRatio"/> existing:
    /// the danger is not a singular matrix, it is a fine one that this algorithm cannot factor.</para>
    /// </summary>
    [Fact]
    public void AWellConditionedMatrixCanHaveNoUnpivotedFactorisation()
    {
        Complex[] a = [Complex.Zero, Complex.One, Complex.One, Complex.Zero];

        Assert.Throws<InvalidOperationException>(() => ComplexLdlt.Factor(a, 2));

        var x = ComplexLu.Factor(a, 2).Solve([Complex.One, new Complex(2.0, 0.0)]);
        Assert.True((x[0] - new Complex(2.0, 0.0)).Magnitude < 1e-12);
        Assert.True((x[1] - Complex.One).Magnitude < 1e-12);
    }

    /// <summary>
    /// The near-miss of the same failure: a tiny leading pivot factorises without throwing and produces
    /// a <see cref="ComplexLdlt.PivotRatio"/> far below the shipped floor. <b>That is what the guard
    /// reads</b> — a breakdown that throws needs no detector.
    /// </summary>
    [Fact]
    public void ANearBreakdownIsVisibleInThePivotRatio()
    {
        const double eps = 1e-7;
        Complex[] a = [new Complex(eps, 0.0), Complex.One, Complex.One, Complex.Zero];

        var factor = ComplexLdlt.Factor(a, 2);
        Assert.True(factor.PivotRatio < WireMomSettings.Default.MinimumPivotRatio,
            $"pivot ratio {factor.PivotRatio:E3} is not below the {WireMomSettings.Default.MinimumPivotRatio:E0} floor.");

        var healthy = ComplexLdlt.Factor(ComplexSymmetric(60, seed: 7), 60);
        Assert.True(healthy.PivotRatio > 1e-6,
            $"a healthy diagonally dominant matrix reported a pivot ratio of {healthy.PivotRatio:E3}.");
    }

    // ---------------------------------------------------------------- fixtures

    private static double[] Spd(int n, int seed)
    {
        var rng = new Random(seed);
        var b = new double[n * n];
        for (int i = 0; i < b.Length; i++) b[i] = rng.NextDouble() - 0.5;

        var a = new double[n * n];
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                double s = 0.0;
                for (int k = 0; k < n; k++) s += b[i * n + k] * b[j * n + k];
                if (i == j) s += n;
                a[i * n + j] = s;
                a[j * n + i] = s;
            }
        return a;
    }

    private static Complex[] ComplexSymmetric(int n, int seed)
    {
        var rng = new Random(seed);
        var a = new Complex[n * n];
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                var v = new Complex(rng.NextDouble() - 0.5, 0.2 * (rng.NextDouble() - 0.5));
                if (i == j) v += n;
                a[i * n + j] = v;
                a[j * n + i] = v;
            }
        return a;
    }
}
