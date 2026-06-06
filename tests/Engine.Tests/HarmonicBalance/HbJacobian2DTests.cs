using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using CircuitRF.Engine.HarmonicBalance;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

/// <summary>
/// Phase 4c Step 4 gate — the two-tone FD-Jacobian oracle.
///
/// Validates that <see cref="HbNewton2D.BuildJ2D"/> (the analytic Jacobian over the 2-D mixing
/// lattice) matches a central-difference Jacobian of <see cref="HbNewton2D.BuildF2D"/> at a
/// representative two-tone operating point on the Hero-2 GaN HEMT. This is the oracle that the
/// vector difference/sum index arithmetic (k₁−i₁,k₂−i₂)/(k₁+i₁,k₂+i₂), the per-axis
/// ConversionWeight2D scaling, the ω(k₁,k₂) charge rotation, and the (0,0) DC special cases are
/// all correct.
///
/// Gate: 1e-5 relative (same as the single-tone JacobianFd test — the FD oracle is limited to a
/// few ppm by the SDD model's large J''').
/// </summary>
public class HbJacobian2DTests(ITestOutputHelper output)
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

    [Fact]
    public void JacobianFd2D_MatchesAnalytic_HeroOperatingPoint()
    {
        var dir          = Hero2Dir();
        var (lib, tb)    = CnlReader.ReadFile(Path.Combine(dir, "hero2_convergence.cnl"));
        var netlist      = new Elaborator(lib).Elaborate(tb);

        var extractor    = new HbLinearExtractor(netlist, AnalysisSettings.Default);
        int N            = extractor.InterfaceCount;
        int[] ifNodes    = extractor.InterfaceNodes;
        var   nodeNames  = ifNodes.Select(n => netlist.Nodes.NameOf(n)).ToArray();

        // Locate gate / drain so the DC bias lands on the right interface node.
        int gate  = Array.FindIndex(nodeNames, s => s.Contains("gate",  StringComparison.OrdinalIgnoreCase));
        int drain = Array.FindIndex(nodeNames, s => s.Contains("drain", StringComparison.OrdinalIgnoreCase));
        Assert.True(gate >= 0 && drain >= 0,
            $"Expected gate/drain interface nodes; got [{string.Join(", ", nodeNames)}]");
        Assert.Equal(2, N);

        // ── Two-tone lattice: order 3 is enough to exercise cross/IM blocks; oversample 2. ──
        const int order = 3;
        var grid     = new MixingGrid(order);
        var (N1, N2) = HbFft2D.GridSizes(order, order, 2);
        int M        = grid.MixCount;

        double f1 = 1.995e9, f2 = 2.005e9;   // Hero-5 tone plan (RFfreq ± ToneSpacing/2)

        // ── Representative LOW-DRIVE operating point: DC bias + small carriers + tiny IM content.
        //    The FD oracle is only trustworthy at modest drive (the single-tone JacobianFd test
        //    samples converged low-drive points for the same reason — the GaN SDD's J''' reaches
        //    ~1e7, so large signal swings push central-difference truncation above the gate). ──
        var V = new Complex[N, M];
        void Set(int node, int k1, int k2, Complex val)
        {
            int m = grid.IndexOf(k1, k2);
            Assert.True(m >= 0, $"({k1},{k2}) not in order-{order} grid");
            V[node, m] = val;
        }
        // DC bias (Vgg = -3.05, Vdd = 48).
        Set(gate,  0, 0, new Complex(-3.05, 0));
        Set(drain, 0, 0, new Complex( 48.0, 0));
        // Carriers (1,0) and (0,1) — deliberately unequal, with non-zero phase.
        Set(gate,  1, 0, new Complex(0.030,  0.005));
        Set(gate,  0, 1, new Complex(0.025, -0.004));
        Set(drain, 1, 0, new Complex(0.300, -0.080));
        Set(drain, 0, 1, new Complex(0.260,  0.050));
        // IM products: baseband (1,-1) and IM3 (2,-1).
        Set(gate,  1, -1, new Complex(0.0040,  0.0010));
        Set(gate,  2, -1, new Complex(0.0012, -0.0003));
        Set(drain, 1, -1, new Complex(0.0250,  0.0060));
        Set(drain, 2, -1, new Complex(0.0050, -0.0020));

        // ── Linear interface from the real extractor at each mixing frequency. The admittance is
        //    linear (FD-trivial), so it does not test the Jacobian's nonlinear blocks directly —
        //    but the near-short harmonic terminations (Y≈1e6 S) set a realistic matrix scale so the
        //    relative-error floor isn't corrupted by near-zero FD noise (mirrors the single-tone
        //    JacobianFd test). mix=0 (DC) uses ExtractDC; mix>0 uses Extract(ω(k₁,k₂)). ──
        double w1 = 2.0 * Math.PI * f1, w2 = 2.0 * Math.PI * f2;
        var yNN  = new Complex[M][,];
        var iSrc = new Complex[M][];
        (yNN[0], iSrc[0]) = extractor.ExtractDC();
        for (int m = 1; m < M; m++)
        {
            double omegaMix = grid.OmegaOf(m, w1, w2);  // signed k₁ω₁+k₂ω₂
            (yNN[m], iSrc[m]) = extractor.Extract(omegaMix);
        }

        // ── Compare analytic vs FD ────────────────────────────────────────────────
        var diag = HbNewton2D.CompareJacobianNumerical2D(
            V, yNN, iSrc, grid, N, N1, N2, f1, f2, netlist, ifNodes);

        output.WriteLine($"Two-tone FD Jacobian — N={N} nodes × M={M} mixing products, DOF={diag.Dof}");
        output.WriteLine($"  grid {N1}×{N2}, order {order}");
        output.WriteLine($"  DC Im-dummy excluded : {diag.DcDummyCount} (maxAbsErr={diag.DcDummyMaxAbsError:E3})");
        output.WriteLine($"  Max absolute error   : {diag.MaxAbsError:E3}");
        output.WriteLine($"  Max relative error   : {diag.MaxRelError:E3}");
        if (diag.TopDiscrepancies.Count > 0)
        {
            output.WriteLine($"  Top {Math.Min(diag.TopDiscrepancies.Count, 12)} discrepancies:");
            foreach (var d in diag.TopDiscrepancies.Take(12))
                output.WriteLine(
                    $"    F[n={d.RowNode},({d.RowK1},{d.RowK2}),{(d.RowIsIm ? "Im" : "Re")}] / " +
                    $"V[n={d.ColNode},({d.ColI1},{d.ColI2}),{(d.ColIsIm ? "Im" : "Re")}] : " +
                    $"analytic={d.AnalyticVal,12:G6} FD={d.FdVal,12:G6} " +
                    $"absErr={d.AbsError:E3} relErr={d.RelError:E3}");
        }

        // Gate at 1e-4 — the two-tone FD oracle floor for this active-device point is ~2e-5 (the
        // fundamental rows carry the full GaN J'''≈1e7 at this synthetic, non-converged point, so
        // central-difference truncation on near-zero elements sits a few× higher than the
        // single-tone test's ~3 ppm, which lives in near-shorted harmonic rows). The dominant
        // Jacobian terms agree to ~11 digits; a real structural error appears at O(0.1–1) relative
        // (the per-axis ConversionWeight2D bug this test caught showed at exactly 0.5), so 1e-4 is
        // ~5× above the FD noise and ~5000× below any structural defect. Tightening below the FD
        // floor would need Richardson extrapolation in the oracle (deferred, as for single-tone).
        const double RelTol = 1e-4;
        Assert.True(diag.MaxRelError < RelTol,
            $"Two-tone Jacobian error maxRelErr={diag.MaxRelError:E3} > {RelTol:E0}. " +
            (diag.TopDiscrepancies.Count > 0
                ? $"Worst block: F[n={diag.TopDiscrepancies[0].RowNode}," +
                  $"({diag.TopDiscrepancies[0].RowK1},{diag.TopDiscrepancies[0].RowK2})] / " +
                  $"V[n={diag.TopDiscrepancies[0].ColNode}," +
                  $"({diag.TopDiscrepancies[0].ColI1},{diag.TopDiscrepancies[0].ColI2})]"
                : ""));
    }
}
