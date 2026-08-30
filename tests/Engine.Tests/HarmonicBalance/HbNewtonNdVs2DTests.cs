using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// <b>The equivalence gate for the T-tone engine.</b>
///
/// <para><see cref="HbNewtonNd"/> replaces two things at once: the rectangular multidimensional
/// FFT becomes the <see cref="HbApft"/> sample-set transform, and the Jacobian's
/// difference/sum-frequency convolution becomes the triple product <c>A·diag(dg)·Γ</c>. Neither
/// substitution is verifiable by inspection, and a wrong one does not crash — it converges to a
/// plausible, wrong spectrum. Production only ever runs the new path at T ≥ 3, where there is no
/// second implementation to compare against.</para>
///
/// <para>So this test runs it at <b>T = 2</b>, the one tone count where the frozen
/// <see cref="HbNewton2D"/> also exists, on the identical circuit, lattice, linear interface and
/// drive — and requires the converged interface spectra to agree. Agreement across two
/// independent formulations of the same physics is the strongest evidence available that the
/// T ≥ 3 path is right, and it costs two-tone runtime rather than six-tone runtime.</para>
///
/// <para><b>Since 2026-08-30 the lattice path is what a two-tone analysis runs by default</b>
/// (<c>AnalysisSettings.HbTwoToneOnLattice</c>), so this comparison is no longer new-against-shipped
/// — it is shipped-against-the-implementation-it-replaced, reached through the setting. That makes
/// it more important rather than less: it is now the only place the two formulations meet, and the
/// committed Hero-5 goldens were themselves produced on the FFT path, so <c>Hero5GateTests</c> is a
/// cross-path check too.</para>
/// </summary>
public class HbNewtonNdVs2DTests(ITestOutputHelper output)
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

    /// <summary>One two-tone problem solved by both formulations, plus the shared index map.</summary>
    private sealed record BothWays(
        Complex[,] V2d, Complex[,] Vnd, MixingGrid Grid, int Drain, string[] Names,
        int FftSamples, int ApftSamples);

    /// <summary>
    /// One two-tone solve run BOTH ways at a given diamond order — same circuit, same lattice,
    /// same linear interface, same Norton drive, same seed, same convergence tolerance. The ONLY
    /// difference is the transform and the Jacobian form.
    /// </summary>
    private BothWays SolveBothWays(int order)
    {
        const double f1 = 1.995e9, f2 = 2.005e9;
        const double tol = 1e-8;

        var dirPath   = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dirPath, "hero2_convergence.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var extractor = new HbLinearExtractor(netlist, AnalysisSettings.Default);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;
        var nodeNames = ifNodes.Select(n => netlist.Nodes.NameOf(n)).ToArray();
        int gate  = Array.FindIndex(nodeNames, s => s.Contains("gate",  StringComparison.OrdinalIgnoreCase));
        int drain = Array.FindIndex(nodeNames, s => s.Contains("drain", StringComparison.OrdinalIgnoreCase));
        Assert.True(gate >= 0 && drain >= 0);

        var grid     = new MixingGrid(order);
        var lattice  = new MixingLattice(2, order);
        int M        = grid.MixCount;
        Assert.Equal(M, lattice.MixCount);   // same retained set, same enumeration (MixingLatticeTests)

        var (N1, N2) = HbFft2D.GridSizes(order, order, 2);
        var apft     = new HbApft(lattice, AnalysisSettings.Default.HbApftOversample);

        // ── One linear interface and one drive, shared by both solves ────────
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
        iSrc[grid.IndexOf(1, 0)][gate] = new Complex(0.010, 0);
        iSrc[grid.IndexOf(0, 1)][gate] = new Complex(0.008, 0);

        var dc = NonlinearDcEngine.Run(netlist, AnalysisSettings.Default);

        Complex[,] Seed()
        {
            var V = new Complex[N, M];
            for (int n = 0; n < N; n++)
            {
                int circNode = ifNodes[n];
                double vdc = circNode > 0 && circNode - 1 < dc.NodeVoltages.Length
                    ? dc.NodeVoltages[circNode - 1] : 0.0;
                V[n, 0] = new Complex(vdc, 0);
                for (int m = 1; m < M; m++) V[n, m] = new Complex(1e-3, 1e-3);
            }
            return V;
        }

        // ── Solve the same problem twice, by two different formulations ──────
        var v2d = Seed();
        var r2d = HbNewton2D.Solve(v2d, yNN, iSrc, grid, f1, f2, N, N1, N2,
            netlist, ifNodes, AnalysisSettings.Default, tol);

        var vNd = Seed();
        var rNd = HbNewtonNd.Solve(vNd, yNN, iSrc, apft, [f1, f2], N,
            netlist, ifNodes, AnalysisSettings.Default, tol);

        Assert.True(r2d.Converged, $"the frozen two-tone reference did not converge at order {order}");
        Assert.True(rNd.Converged, $"the T-tone solve did not converge at T = 2, order {order}");

        return new BothWays(v2d, vNd, grid, drain, nodeNames, N1 * N2, apft.SampleCount);
    }

    /// <summary>Relative disagreement between the two formulations at one product, at one node.</summary>
    private static double RelDiff(Complex[,] a, Complex[,] b, int node, int mix)
        => (a[node, mix] - b[node, mix]).Magnitude / Math.Max(a[node, mix].Magnitude, 1e-30);

    [Fact]
    public void NdSolve_AtTwoTones_ReproducesTheFrozen2DSolve()
    {
        // Order 5 is the shipping two-tone default, and the products a user actually reads
        // (DC, carriers, IM3) sit well inside the retained diamond there.
        var r = SolveBothWays(5);
        Complex[,] v2d = r.V2d, vNd = r.Vnd;
        var grid = r.Grid; int drain = r.Drain; var names = r.Names;
        int fftS = r.FftSamples, apftS = r.ApftSamples;

        int c10 = grid.IndexOf(1, 0), c01 = grid.IndexOf(0, 1), im3 = grid.IndexOf(2, -1);

        output.WriteLine($"order 5 — FFT grid {fftS} samples vs APFT {apftS} samples");
        output.WriteLine($"  drain DC      : 2-D {v2d[drain,0].Real:F9} V   N-D {vNd[drain,0].Real:F9} V");
        output.WriteLine($"  carrier (1,0) : rel {RelDiff(v2d, vNd, drain, c10):E3}");
        output.WriteLine($"  carrier (0,1) : rel {RelDiff(v2d, vNd, drain, c01):E3}");
        output.WriteLine($"  IM3    (2,-1) : rel {RelDiff(v2d, vNd, drain, im3):E3}");

        Assert.Equal(v2d[drain, 0].Real, vNd[drain, 0].Real, 9);
        Assert.True(RelDiff(v2d, vNd, drain, c10) < 1e-6, "carrier (1,0) disagrees");
        Assert.True(RelDiff(v2d, vNd, drain, c01) < 1e-6, "carrier (0,1) disagrees");
        Assert.True(RelDiff(v2d, vNd, drain, im3) < 1e-5, "IM3 (2,-1) disagrees");

        // Every retained product at every node, not just the three that get read.
        double worst = 0; int wn = 0, wm = 0;
        for (int n = 0; n < v2d.GetLength(0); n++)
        for (int m = 0; m < v2d.GetLength(1); m++)
        {
            double d = (v2d[n, m] - vNd[n, m]).Magnitude;
            if (d > worst) { worst = d; wn = n; wm = m; }
        }
        var (wk1, wk2) = grid.ToneOf(wm);
        output.WriteLine($"  worst absolute: {worst:E3} at {names[wn]} ({wk1},{wk2})");
        Assert.True(worst < 1e-5, $"max |ΔV| = {worst:E3} at {names[wn]} ({wk1},{wk2})");
    }

    [Fact]
    public void TheTwoFormulationsConverge_ToEachOther_AsTheDiamondGrows()
    {
        // The two paths are NOT expected to agree bit for bit: they truncate the same infinite
        // problem differently. The FFT grid aliases everything above the diamond back onto it by
        // periodic wrap; the APFT least-squares-projects it. So the product sitting on the EDGE
        // of the retained set — the one most exposed to what was discarded — always disagrees
        // most, and that is physics, not a defect.
        //
        // What must be true is that the disagreement is TRUNCATION and therefore vanishes as the
        // diamond grows past the product being read. Asserting that trend is a far stronger claim
        // than any single tolerance, and it is what stops a real formulation error (which would
        // NOT shrink with order) from being absorbed by a loose bound.
        var o3 = SolveBothWays(3);
        var o4 = SolveBothWays(4);
        var o5 = SolveBothWays(5);

        static double Im3(BothWays r) => RelDiff(r.V2d, r.Vnd, r.Drain, r.Grid.IndexOf(2, -1));

        double e3 = Im3(o3), e4 = Im3(o4), e5 = Im3(o5);
        output.WriteLine($"IM3 (2,-1) relative disagreement vs MaxMixOrder:");
        output.WriteLine($"  order 3 : {e3:E3}   (IM3 is ON the diamond edge)");
        output.WriteLine($"  order 4 : {e4:E3}");
        output.WriteLine($"  order 5 : {e5:E3}   (IM3 is two orders inside)");

        Assert.True(e4 < e3 / 10.0, $"order 3→4 did not converge: {e3:E3} → {e4:E3}");
        Assert.True(e5 < e4 / 10.0, $"order 4→5 did not converge: {e4:E3} → {e5:E3}");
        Assert.True(e5 < e3 / 1000.0,
            $"the two formulations are not converging to each other: {e3:E3} → {e5:E3}");
    }

    [Fact]
    public void NdJacobian_MatchesFiniteDifferences_AtThreeTones()
    {
        // The FD oracle on the T-vector index arithmetic and the triple-product Jacobian, the
        // T-tone twin of HbJacobian2DTests. Kept at order 2 so the dense FD comparison
        // (dof² entries, 2·dof nonlinear evaluations) stays sub-second.
        const int order = 2;
        double[] tones  = [1.90e9, 2.00e9, 2.10e9];

        var dirPath   = Hero2Dir();
        var (lib, tb) = CnlReader.ReadFile(Path.Combine(dirPath, "hero2_convergence.cnl"));
        var netlist   = new Elaborator(lib).Elaborate(tb);

        var extractor = new HbLinearExtractor(netlist, AnalysisSettings.Default);
        int N         = extractor.InterfaceCount;
        int[] ifNodes = extractor.InterfaceNodes;

        var lattice = new MixingLattice(3, order);
        var apft    = new HbApft(lattice, AnalysisSettings.Default.HbApftOversample);
        int M       = lattice.MixCount;

        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int m = 1; m < M; m++)
        {
            var y = new Complex[N, N];
            for (int n = 0; n < N; n++) y[n, n] = new Complex(0.02, 0.005);
            yNN[m]  = y;
            iSrc[m] = new Complex[N];
        }

        // A deliberately non-trivial operating point: a linearization about DC alone would leave
        // most of the conversion blocks zero and the comparison would prove nothing.
        var dc = NonlinearDcEngine.Run(netlist, AnalysisSettings.Default);
        var V  = new Complex[N, M];
        for (int n = 0; n < N; n++)
        {
            int circNode = ifNodes[n];
            V[n, 0] = new Complex(circNode > 0 && circNode - 1 < dc.NodeVoltages.Length
                ? dc.NodeVoltages[circNode - 1] : 0.0, 0);
            for (int m = 1; m < M; m++)
                V[n, m] = new Complex(0.05 + 0.01 * m, 0.03 - 0.004 * m);
        }

        var cmp = HbNewtonNd.CompareJacobianNumericalNd(
            V, yNN, iSrc, apft, N, tones, netlist, ifNodes);

        output.WriteLine($"3-tone Jacobian vs FD: N={cmp.N}, M={cmp.M}, dof={cmp.Dof} " +
                         $"(APFT {apft.SampleCount} samples)");
        output.WriteLine($"  max abs err {cmp.MaxAbsError:E3}, max rel err {cmp.MaxRelError:E3} " +
                         $"at ({cmp.MaxRelRow},{cmp.MaxRelCol})");
        output.WriteLine($"  DC Im-dummy DOFs skipped: {cmp.DcDummyCount} " +
                         $"(max abs {cmp.DcDummyMaxAbsError:E3})");
        foreach (var d in cmp.TopDiscrepancies.Take(6))
            output.WriteLine($"    row {MixingLattice.Label(d.RowK)}{(d.RowIsIm ? "im" : "re")} " +
                             $"col {MixingLattice.Label(d.ColK)}{(d.ColIsIm ? "im" : "re")}: " +
                             $"analytic {d.AnalyticVal:E4} fd {d.FdVal:E4} rel {d.RelError:E2}");

        Assert.True(cmp.MaxRelError < 1e-4,
            $"analytic Jacobian disagrees with finite differences: max rel err {cmp.MaxRelError:E3}");
    }
}
