using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using NumFlat;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// <b>The gate on the dense Newton solve</b> (brief-hb-p1-dense-solve-and-apft-cost, M1).
///
/// <para><see cref="HbNewton.SolveGaussian"/> is called once per Newton iteration on EVERY HB path
/// — single-tone, two-tone and T-tone — so replacing its augmented Gauss-Jordan sweep with an LU
/// factorisation touches every converged answer this engine has. The replacement is a different
/// sequence of floating-point operations, not a rearrangement of the same one, so it cannot be
/// bit-identical and must instead be pinned against the implementation it replaced.</para>
///
/// <para>The matrices it is pinned on are the ones the engine ACTUALLY produces, not random
/// well-conditioned ones: an HB Jacobian carries the interface admittance on its mix diagonal,
/// which on a Hero-5-shaped circuit spans about six decades (1 µΩ near-shorts give Y ≈ 1e6), and
/// that dynamic range is exactly what separates a pivoting strategy that works from one that looks
/// like it does. <see cref="HbNewtonNd.BuildJNd"/> is the cheapest way to reach every size that
/// matters — 2·N·M is 124 at the two-tone order-5 shape, 172 at 6 tones / order 2 and 756 at
/// 6 tones / order 3 — on one real netlist.</para>
/// </summary>
public class HbDenseSolveTests(ITestOutputHelper output)
{
    private static string Hero2Dir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, "testdata", "Hero2");
            if (Directory.Exists(cand)) return cand;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/Hero2 not found");
    }

    /// <summary>
    /// A real HB Jacobian and a real residual at the given lattice shape, taken from the Hero-2 GaN
    /// FET at its DC operating point — the same construction <see cref="HbNewtonNdVs2DTests"/> uses,
    /// stopped one step before the solve.
    /// </summary>
    internal static (double[] J, double[] negF, int Dof, HbApft Apft, int N)
        RealJacobian(int tones, int order)
    {
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(Hero2Dir(), "hero2_convergence.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var extractor = new HbLinearExtractor(netlist, AnalysisSettings.Default);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        var names     = ifNodes.Select(n => netlist.Nodes.NameOf(n)).ToArray();
        int gate  = Array.FindIndex(names, s => s.Contains("gate",  StringComparison.OrdinalIgnoreCase));
        int drain = Array.FindIndex(names, s => s.Contains("drain", StringComparison.OrdinalIgnoreCase));
        Assert.True(gate >= 0 && drain >= 0);

        // A private transform, never the shared cache: this fixture reads the instance's own
        // product-call counter, which must not be perturbed by whatever else is running.
        var lattice = new MixingLattice(tones, order);
        var apft    = new HbApft(lattice, AnalysisSettings.Default.HbApftOversample);
        int M       = lattice.MixCount;

        var toneFreqs = new double[tones];
        for (int t = 0; t < tones; t++) toneFreqs[t] = 2.0e9 + t * 10e6;

        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int m = 1; m < M; m++)
        {
            var y = new Complex[N, N];
            y[gate,  gate]  = new Complex(0.04, 0);   // 25 Ω source
            y[drain, drain] = new Complex(0.02, 0);   // 50 Ω load
            yNN[m]  = y;
            iSrc[m] = new Complex[N];
        }
        iSrc[1][gate] = new Complex(0.010, 0);

        var dc = NonlinearDcEngine.Run(netlist, AnalysisSettings.Default);
        var V  = new Complex[N, M];
        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            double vdc = circNode > 0 && circNode - 1 < dc.NodeVoltages.Length
                ? dc.NodeVoltages[circNode - 1] : 0.0;
            V[n, 0] = new Complex(vdc, 0);
            for (int m = 1; m < M; m++) V[n, m] = new Complex(1e-3, 1e-3);
        }

        var (iNl, qNl, dg, dcw) = HbNewtonNd.EvaluateNonlinearNd(V, apft, N, netlist, ifNodes);
        var J = HbNewtonNd.BuildJNd(yNN, dg, dcw, apft, N, toneFreqs);
        var F = HbNewtonNd.BuildFNd(V, yNN, iSrc, iNl, qNl, lattice, N, toneFreqs);

        int dof = 2 * N * M;
        var negF = new double[dof];
        for (int r = 0; r < dof; r++) negF[r] = -F[r];
        return (J, negF, dof, apft, N);
    }

    private static double RelDiff(double[] a, double[] b)
    {
        double dn = 0, bn = 0;
        for (int i = 0; i < a.Length; i++) { double d = a[i] - b[i]; dn += d * d; bn += b[i] * b[i]; }
        return Math.Sqrt(dn) / Math.Max(Math.Sqrt(bn), 1e-300);
    }

    // ── (1) the LU and the Gauss-Jordan sweep agree on the real matrices ─────────────────────

    [Theory]
    [InlineData(2, 5)]    // dof 124 — the shipping two-tone shape (hero5.cnl)
    [InlineData(3, 3)]    // dof 128 — hero5_3tone.cnl
    [InlineData(6, 2)]    // dof 172 — hero5_6tone.cnl as shipped
    public void Lu_AgreesWithGaussJordan_OnTheJacobiansTheEngineProduces(int tones, int order)
    {
        var (J, negF, dof, _, _) = RealJacobian(tones, order);
        Assert.True(dof >= HbNewton.SolveCrossover, "this size must take the LU branch");

        double[] gj = HbNewton.SolveGaussJordan(J, negF, dof)!;
        double[] lu = HbNewton.SolveLu(J, negF, dof)!;
        Assert.NotNull(gj);
        Assert.NotNull(lu);

        double rel = RelDiff(lu, gj);
        output.WriteLine($"T={tones} order={order}  dof={dof}  ‖ΔV_lu − ΔV_gj‖ / ‖ΔV_gj‖ = {rel:E3}");
        Assert.True(rel <= 1e-10, $"LU and Gauss-Jordan disagree by {rel:E3} at dof {dof}");
    }

    /// <summary>
    /// The 6-tone order-3 shape (dof 756) is the size the whole brief was written for and the one
    /// where the two implementations have the most round-off to accumulate differently, so it is
    /// gated too — separately, because building a 756×756 Jacobian and solving it twice is the
    /// expensive case.
    /// </summary>
    [Fact]
    public void Lu_AgreesWithGaussJordan_AtTheSixToneOrderThreeSize()
    {
        var (J, negF, dof, _, _) = RealJacobian(6, 3);
        Assert.Equal(756, dof);

        double[] gj = HbNewton.SolveGaussJordan(J, negF, dof)!;
        double[] lu = HbNewton.SolveLu(J, negF, dof)!;

        double rel = RelDiff(lu, gj);
        output.WriteLine($"dof={dof}  ‖ΔV_lu − ΔV_gj‖ / ‖ΔV_gj‖ = {rel:E3}");
        Assert.True(rel <= 1e-10, $"LU and Gauss-Jordan disagree by {rel:E3} at dof {dof}");
    }

    // ── (2) the LU agrees with an independent implementation, not only with the one it replaced ─

    /// <summary>
    /// NumFlat's own LU on the same matrix. This is the check that would catch the two in-house
    /// implementations agreeing on a shared mistake — a pivoting rule that happens to be wrong the
    /// same way in both, for instance. NumFlat's factorisation is not used in production (it is
    /// slower here, and erratic at power-of-two sizes; see the measurement table in
    /// <c>HbNewton</c>), but it is a perfectly good oracle.
    /// </summary>
    [Theory]
    [InlineData(2, 5)]
    [InlineData(6, 2)]
    public void Lu_AgreesWithNumFlat(int tones, int order)
    {
        var (J, negF, dof, _, _) = RealJacobian(tones, order);

        var m = new Mat<double>(dof, dof);
        for (int r = 0; r < dof; r++)
            for (int c = 0; c < dof; c++) m[r, c] = J[r * dof + c];
        var rhs = new Vec<double>(dof);
        for (int r = 0; r < dof; r++) rhs[r] = negF[r];

        var nf = m.Lu().Solve(rhs);
        var reference = new double[dof];
        for (int r = 0; r < dof; r++) reference[r] = nf[r];

        double[] lu = HbNewton.SolveLu(J, negF, dof)!;
        double rel = RelDiff(lu, reference);
        output.WriteLine($"T={tones} order={order}  dof={dof}  vs NumFlat = {rel:E3}");
        Assert.True(rel <= 1e-10, $"the LU disagrees with NumFlat by {rel:E3} at dof {dof}");
    }

    // ── (3) singular still returns null, on both sides of the crossover ──────────────────────

    [Theory]
    [InlineData(4)]     // below the crossover — the Gauss-Jordan branch
    [InlineData(6)]     // below the crossover, non-trivially sized
    [InlineData(8)]     // exactly at the crossover — the first LU size
    [InlineData(40)]    // comfortably inside the LU branch
    public void SingularMatrix_ReturnsNull_WithNoExceptionEscaping(int n)
    {
        // A zeroed ROW: the elimination reaches a column with no usable pivot below it.
        var a = Diagonal(n);
        for (int c = 0; c < n; c++) a[2 * n + c] = 0.0;
        var b = new double[n];
        for (int i = 0; i < n; i++) b[i] = 1.0 + i;

        Assert.Null(HbNewton.SolveGaussian(a, b, n));

        // A zeroed COLUMN: the same, reached from the other side.
        var a2 = Diagonal(n);
        for (int r = 0; r < n; r++) a2[r * n + 2] = 0.0;
        Assert.Null(HbNewton.SolveGaussian(a2, b, n));

        // Both branches, explicitly, so neither is exempt whatever the crossover is set to.
        Assert.Null(HbNewton.SolveLu(a, b, n));
        Assert.Null(HbNewton.SolveGaussJordan(a, b, n));
    }

    private static double[] Diagonal(int n)
    {
        var a = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            a[i * n + i] = 2.0 + i;
            if (i + 1 < n) a[i * n + i + 1] = 0.5;
        }
        return a;
    }

    // ── (4) the crossover is a named constant and both of its branches are live ──────────────

    /// <summary>
    /// <see cref="HbNewton.SolveGaussian"/> dispatches on <see cref="HbNewton.SolveCrossover"/>,
    /// so both branches must actually be reachable and must agree. The sizes above the crossover
    /// come from the real Jacobians in the tests above; the size below it is synthetic, because
    /// the engine has no analysis that small (the smallest HB system is 2·N·M with N ≥ 1 and
    /// M ≥ 1, and Hero-sized circuits start at dof 24).
    /// </summary>
    [Fact]
    public void SolveGaussian_TakesBothBranchesOfTheCrossover_AndTheyAgree()
    {
        // Must sit below the smallest dof the engine produces, or the LU branch is never taken
        // in production; and above 2, or the Gauss-Jordan branch is dead code.
        Assert.InRange(HbNewton.SolveCrossover, 3, 23);

        // Below: dispatches to Gauss-Jordan, and the LU must still give the same answer.
        int small = HbNewton.SolveCrossover - 1;
        var (aS, bS) = WellConditioned(small, seed: 11);
        double[] dispatchedS = HbNewton.SolveGaussian(aS, bS, small)!;
        Assert.Equal(HbNewton.SolveGaussJordan(aS, bS, small)!, dispatchedS);
        Assert.True(RelDiff(HbNewton.SolveLu(aS, bS, small)!, dispatchedS) <= 1e-10);

        // At and above: dispatches to the LU, and Gauss-Jordan must still give the same answer.
        int big = HbNewton.SolveCrossover;
        var (aB, bB) = WellConditioned(big, seed: 12);
        double[] dispatchedB = HbNewton.SolveGaussian(aB, bB, big)!;
        Assert.Equal(HbNewton.SolveLu(aB, bB, big)!, dispatchedB);
        Assert.True(RelDiff(HbNewton.SolveGaussJordan(aB, bB, big)!, dispatchedB) <= 1e-10);

        output.WriteLine($"crossover = {HbNewton.SolveCrossover}; both branches exercised at " +
                         $"n = {small} and n = {big}");
    }

    private static (double[] A, double[] b) WellConditioned(int n, int seed)
    {
        var r = new Random(seed);
        var a = new double[n * n];
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < n; j++) { a[i * n + j] = r.NextDouble() * 2 - 1; s += Math.Abs(a[i * n + j]); }
            a[i * n + i] += s;                       // diagonally dominant, like a damped Newton J
            b[i] = r.NextDouble() * 2 - 1;
        }
        return (a, b);
    }
}
