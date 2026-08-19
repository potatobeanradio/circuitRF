// ================================================================
//  Rbf2DTests.cs — correctness gate for Rbf2D
//
//  Tests:
//   1. Epsilon default formula matches scipy's convention
//   2. Self-consistency: smooth=0 reproduces nodal values (tol 1e-6)
//   3. NaN-drop: UsedIndices excludes NaN entries; NodeCount drops
//   4. Constant field: reasonable (not exact for multiquadric)
//   5. Symmetric radial field: sanity check
//   6. 4-node hand-verified example
//   7. Thin-plate and Gaussian kernels (smoke test)
//   8. Complex-overload maps identically to the re/im overload
//
//  // GOLDEN: if the owner supplies a scipy-generated CSV
//  //   testdata/rbf2d_golden.csv with (qRe, qIm, expected) columns,
//  //   drop it here and uncomment the test below.
// ================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class Rbf2DTests
{
    // ---- helpers ---------------------------------------------------
    private static void Near(double expected, double actual, double tol, string label = "")
    {
        Assert.True(Math.Abs(actual - expected) <= tol,
            $"{label}: expected {expected}, got {actual}, diff={Math.Abs(actual - expected):G4}, tol={tol}");
    }

    // ---- Test 1: Epsilon default -----------------------------------
    [Fact]
    public void EpsilonDefault_MatchesScipy2DFormula()
    {
        // 4 nodes: re in [0,1], im in [0,2] → Δre=1, Δim=2, N=4
        // scipy: epsilon = sqrt(1*2/4) = sqrt(0.5) ≈ 0.70711
        var re  = new double[] { 0, 1, 0, 1 };
        var im  = new double[] { 0, 0, 2, 2 };
        double eps = Rbf2D.ComputeEpsilon(re, im, 4);
        Near(Math.Sqrt(0.5), eps, 1e-12, "epsilon 4-node");
    }

    [Fact]
    public void EpsilonDefault_FiltersZeroAxis()
    {
        // All nodes on re=0 → degenerate re axis, only im range counts
        // re=[0,0,0], im=[0,1,2] → Δre=0 (filtered), Δim=2, N=3
        // scipy: edges=[2], epsilon = 2/3
        var re  = new double[] { 0, 0, 0 };
        var im  = new double[] { 0, 1, 2 };
        double eps = Rbf2D.ComputeEpsilon(re, im, 3);
        Near(2.0 / 3.0, eps, 1e-12, "epsilon degenerate axis");
    }

    // ---- Test 2: Self-consistency (smooth=0 reproduces node values) -
    [Fact]
    public void SelfConsistency_MultiquadricSmooth0_ReproducesNodes()
    {
        // 5 scattered nodes, arbitrary values
        double[] re  = {  0.0,  1.0, -0.5,  0.5, 0.0 };
        double[] im  = {  0.0, -0.5,  0.5, -0.5, 0.8 };
        double[] val = { 10.0, 15.0, 12.0, 11.0, 9.0 };

        var rbf = new Rbf2D(re, im, val, RbfKernel.Multiquadric, smooth: 0.0);

        Assert.Equal(5, rbf.NodeCount);
        for (int i = 0; i < val.Length; i++)
            Near(val[i], rbf.Evaluate(re[i], im[i]), 1e-6,
                $"node[{i}] self-consistency");
    }

    [Fact]
    public void SelfConsistency_GaussianSmooth0_ReproducesNodes()
    {
        double[] re  = { 0.0, 1.0, -1.0, 0.5 };
        double[] im  = { 0.0, 1.0,  0.5, -0.5 };
        double[] val = { 3.0, 7.0,  5.0, 4.0 };

        var rbf = new Rbf2D(re, im, val, RbfKernel.Gaussian, smooth: 0.0);
        for (int i = 0; i < val.Length; i++)
            Near(val[i], rbf.Evaluate(re[i], im[i]), 1e-6,
                $"Gaussian node[{i}]");
    }

    // ---- Test 3: NaN-drop ------------------------------------------
    [Fact]
    public void NanDrop_ExcludesNanEntries()
    {
        double[] re  = { 0.0, 1.0, double.NaN, 0.5, double.NaN };
        double[] im  = { 0.0, 0.0, 0.5,        1.0, -0.5       };
        double[] val = { 1.0, 2.0, double.NaN, 3.0, double.NaN };

        var rbf = new Rbf2D(re, im, val);
        Assert.Equal(3, rbf.NodeCount);

        var used = rbf.UsedIndices;
        Assert.Equal(3, used.Count);
        Assert.Contains(0, (IEnumerable<int>)used);
        Assert.Contains(1, (IEnumerable<int>)used);
        Assert.Contains(3, (IEnumerable<int>)used);
        Assert.DoesNotContain(2, (IEnumerable<int>)used);
        Assert.DoesNotContain(4, (IEnumerable<int>)used);
    }

    [Fact]
    public void NanDrop_AllNan_NodeCountZero()
    {
        double[] re  = { 0.0, 1.0 };
        double[] im  = { 0.0, 0.0 };
        double[] val = { double.NaN, double.NaN };
        var rbf = new Rbf2D(re, im, val);
        Assert.Equal(0, rbf.NodeCount);
        Assert.Equal(0.0, rbf.Evaluate(0.5, 0.5)); // zero-weight fit
    }

    // ---- Test 4: Constant field ------------------------------------
    [Fact]
    public void ConstantField_Multiquadric_ReasonableApproximation()
    {
        // Multiquadric does not reproduce constants exactly without
        // augmented polynomial terms, but should be close in-range.
        double[] re  = { 0.0, 1.0, 0.5,  0.0, 1.0 };
        double[] im  = { 0.0, 0.0, 0.5,  1.0, 1.0 };
        double[] val = { 5.0, 5.0, 5.0,  5.0, 5.0 };

        var rbf = new Rbf2D(re, im, val, smooth: 1e-6);
        // Evaluate at centroid — should be within 10% of 5.0
        double result = rbf.Evaluate(0.5, 0.5);
        Assert.InRange(result, 4.0, 6.0);
    }

    // ---- Test 5: Monotone field sanity -----------------------------
    [Fact]
    public void MonotoneField_InterpIsOrdered()
    {
        // Values increase from left to right (re direction).
        // The interpolant at x=0 should be between the values at x=-1 and x=1.
        double[] re  = { -1.0, 0.0, 1.0, -1.0, 0.0, 1.0 };
        double[] im  = {  0.0, 0.0, 0.0,  1.0, 1.0, 1.0 };
        double[] val = {  1.0, 5.0, 9.0,  2.0, 6.0, 10.0 };

        var rbf = new Rbf2D(re, im, val, smooth: 1e-6);

        // At the centroid (0, 0.5), value should be between min(val) and max(val)
        double mid = rbf.Evaluate(0.0, 0.5);
        Assert.InRange(mid, 1.0, 10.0);

        // Interpolant should be larger at re=0.8 than at re=-0.8 (monotone trend)
        double right = rbf.Evaluate(0.8, 0.5);
        double left  = rbf.Evaluate(-0.8, 0.5);
        Assert.True(right > left,
            $"Expected right ({right:F3}) > left ({left:F3}) for monotone field");
    }

    // ---- Test 6: Hand-verified 3-node example ----------------------
    [Fact]
    public void ThreeNodeExample_SmoothZero_SolvesExactly()
    {
        // 3 nodes in a line with known values; smooth=0 means exact interp
        double[] re  = { 0.0, 1.0, 2.0 };
        double[] im  = { 0.0, 0.0, 0.0 };
        double[] val = { 1.0, 4.0, 9.0 };

        var rbf = new Rbf2D(re, im, val, smooth: 0.0);
        Near(1.0, rbf.Evaluate(0.0, 0.0), 1e-5, "node 0");
        Near(4.0, rbf.Evaluate(1.0, 0.0), 1e-5, "node 1");
        Near(9.0, rbf.Evaluate(2.0, 0.0), 1e-5, "node 2");

        // Interpolation is smooth between nodes
        double mid = rbf.Evaluate(0.5, 0.0);
        Assert.InRange(mid, 1.0, 4.0);
    }

    // ---- Test 7: Thin-plate kernel --------------------------------
    [Fact]
    public void ThinPlateKernel_SmokeTest_SelfConsistency()
    {
        double[] re  = { 0.0, 1.0, 0.0, 1.0 };
        double[] im  = { 0.0, 0.0, 1.0, 1.0 };
        double[] val = { 1.0, 2.0, 3.0, 4.0 };

        var rbf = new Rbf2D(re, im, val, RbfKernel.ThinPlate, smooth: 0.0);
        for (int i = 0; i < val.Length; i++)
            Near(val[i], rbf.Evaluate(re[i], im[i]), 1e-4, $"ThinPlate node[{i}]");
    }

    // ---- Test 8: Complex overload ----------------------------------
    [Fact]
    public void ComplexOverload_MatchesRealImagOverload()
    {
        double[] re  = { 0.2, -0.3, 0.5, -0.1 };
        double[] im  = { 0.1,  0.4, -0.2, 0.3 };
        double[] val = { 2.0,  3.0,  1.5, 4.0 };

        var complex = new Complex[4];
        for (int i = 0; i < 4; i++) complex[i] = new Complex(re[i], im[i]);

        var rbf1 = new Rbf2D(re, im, val);
        var rbf2 = new Rbf2D(complex, val);

        double qRe = 0.1, qIm = 0.2;
        Near(rbf1.Evaluate(qRe, qIm), rbf2.Evaluate(qRe, qIm), 1e-12,
            "complex vs re/im overload");
    }

    // ---- Test 9: Batch Evaluate agrees with scalar -----------------
    [Fact]
    public void BatchEvaluate_MatchesScalar()
    {
        double[] re  = { 0.0, 1.0, 0.5, 0.0, 1.0 };
        double[] im  = { 0.0, 0.0, 0.5, 1.0, 1.0 };
        double[] val = { 10.0, 20.0, 15.0, 12.0, 18.0 };

        var rbf = new Rbf2D(re, im, val);

        double[] qRe  = { 0.1, 0.9, 0.5 };
        double[] qIm  = { 0.1, 0.1, 0.8 };
        double[] res  = new double[3];
        rbf.Evaluate(qRe, qIm, res);

        for (int i = 0; i < 3; i++)
            Near(rbf.Evaluate(qRe[i], qIm[i]), res[i], 1e-14,
                $"batch vs scalar q[{i}]");
    }

    // ---- Test 10: NodeValues/NodesRe/NodesIm reflect post-NaN-drop --
    [Fact]
    public void NodesAccessors_ReflectPostDropState()
    {
        double[] re  = { 0.0, 0.5, 1.0 };
        double[] im  = { 0.0, 0.5, 1.0 };
        double[] val = { 1.0, double.NaN, 3.0 };

        var rbf = new Rbf2D(re, im, val);
        Assert.Equal(2, rbf.NodeCount);
        Assert.Equal(2, rbf.NodesRe.Length);
        Assert.Equal(0.0, rbf.NodesRe[0]);
        Assert.Equal(1.0, rbf.NodesRe[1]);
        Assert.Equal(1.0, rbf.NodeValues[0]);
        Assert.Equal(3.0, rbf.NodeValues[1]);
    }

    // ---- Test 11: Phi kernel values (scipy formulas) ---------------
    [Fact]
    public void PhiMultiquadric_MatchesFormula()
    {
        // phi(r) = sqrt((r/eps)^2 + 1); at r=0 → 1; at r=eps → sqrt(2)
        double eps = 2.5;
        Near(1.0,        Rbf2D.Phi(0.0, eps, RbfKernel.Multiquadric), 1e-15, "phi(0)");
        Near(Math.Sqrt(2.0), Rbf2D.Phi(eps, eps, RbfKernel.Multiquadric), 1e-15, "phi(eps)");
        // phi(r) > 1 for all r > 0
        Assert.True(Rbf2D.Phi(0.001, eps, RbfKernel.Multiquadric) > 1.0);
    }

    [Fact]
    public void PhiThinPlate_ZeroAtOrigin()
    {
        Near(0.0, Rbf2D.Phi(0.0, 1.0, RbfKernel.ThinPlate), 1e-15, "ThinPlate phi(0)");
    }

    [Fact]
    public void PhiGaussian_OneAtOrigin_DecaysAway()
    {
        double eps = 1.0;
        Near(1.0, Rbf2D.Phi(0.0, eps, RbfKernel.Gaussian), 1e-15, "Gaussian phi(0)");
        Assert.True(Rbf2D.Phi(2.0, eps, RbfKernel.Gaussian) < 0.1);
    }

    // GOLDEN (owner-supplied scipy reference — uncomment and drop CSV in testdata/):
    //
    // [Fact]
    // public void GoldenCsv_MatchesScipy()
    // {
    //     // testdata/rbf2d_golden.csv: columns qRe, qIm, expected
    //     // Generated with:
    //     //   import numpy as np
    //     //   from scipy.interpolate import Rbf
    //     //   spl = SPLData('Ideal_GaN_FET_1p6_mm_1p8_GHz.spl')
    //     //   xi = spl.DataInterp[...].xi
    //     //   di = spl.DataInterp[...].di
    //     //   rbf = Rbf(xi[0], xi[1], di, function='multiquadric', smooth=1e-3, norm='euclidean')
    //     //   # evaluate on query grid, save as csv
    //     const double Tol = 1e-4;
    //     var csv = System.IO.File.ReadAllLines(
    //         System.IO.Path.Combine(AppContext.BaseDirectory, "testdata", "rbf2d_golden.csv"));
    //     // ... parse and compare
    // }

    // ================================================================
    //  Thin-plate's polynomial tail, and the smoothing sign (2026-08-18)
    //
    //  Owner: thin-plate produced +4 dB contour islands on a real loadpull surface, and no smoothing
    //  value fixed it. It was not user error — see Rbf2D.RequiresPolynomialTail and
    //  Rbf2D.SmoothingSign for the measurements these gates hold.
    // ================================================================

    /// <summary>A polar loadpull grid — rings of constant |Γ|, which is what a tuner sweeps.</summary>
    private static (double[] Re, double[] Im) PolarGrid(int rings = 5, int perRing = 12, double rMax = 0.8)
    {
        var re = new List<double> { 0.0 };
        var im = new List<double> { 0.0 };
        for (int k = 1; k <= rings; k++)
        {
            double r = rMax * k / rings;
            for (int p = 0; p < perRing; p++)
            {
                double a = 2.0 * Math.PI * p / perRing + 0.3 * k;
                re.Add(r * Math.Cos(a));
                im.Add(r * Math.Sin(a));
            }
        }
        return (re.ToArray(), im.ToArray());
    }

    /// <summary>Pout(Γ) in dBm — a smooth bowl peaking off-centre, the shape loadpull actually has.</summary>
    private static double LoadpullBowl(double re, double im)
    {
        double dr = re - 0.35, di = im + 0.20;
        double d2 = dr * dr + di * di;
        return 40.0 - 9.0 * d2 - 3.0 * d2 * d2 + 0.8 * dr * di;
    }

    /// <summary>Worst |fit − truth| and worst excursion outside the data range, over the sampled disc.</summary>
    private static (double WorstError, double Overshoot, double Undershoot) SurveyDisc(
        Rbf2D fit, double dataMin, double dataMax)
    {
        double worst = 0, over = 0, under = 0;
        for (int a = 0; a < 90; a++)
            for (int b = 0; b < 90; b++)
            {
                double x = -0.8 + 1.6 * a / 89.0, y = -0.8 + 1.6 * b / 89.0;
                if (x * x + y * y > 0.64) continue;

                double got = fit.Evaluate(x, y);
                worst = Math.Max(worst, Math.Abs(got - LoadpullBowl(x, y)));
                over = Math.Max(over, got - dataMax);
                under = Math.Min(under, got - dataMin);
            }
        return (worst, over, under);
    }

    /// <summary>
    /// <b>Thin-plate carries a linear polynomial tail, so raising smoothing SMOOTHS.</b>
    ///
    /// <para>The defect this pins: without the tail, overshoot above the data maximum went +0.18 dB at
    /// smooth = 0.01, <b>+7.2 dB at 0.1</b> and +212 dB at 0.5 — the owner's contour islands, and
    /// non-monotonic, so no amount of turning the knob could find a good value.</para>
    /// </summary>
    [Fact]
    public void ThinPlate_SmoothingDoesNotBlowUpTheSurface()
    {
        var (re, im) = PolarGrid();
        var values = new double[re.Length];
        for (int i = 0; i < re.Length; i++) values[i] = LoadpullBowl(re[i], im[i]);

        double dataMin = values.Min(), dataMax = values.Max();

        foreach (double smooth in new[] { 0.0, 1e-3, 1e-2, 0.1, 0.5, 1.0 })
        {
            var fit = new Rbf2D(re, im, values, RbfKernel.ThinPlate, smooth);
            var (worst, over, under) = SurveyDisc(fit, dataMin, dataMax);

            Assert.True(over < 0.5,
                $"smooth={smooth}: thin-plate overshot the data maximum by {over:F3} dB. Without its " +
                "polynomial tail this reached +7.2 dB at smooth=0.1 and +212 dB at 0.5.");
            Assert.True(under > -0.5,
                $"smooth={smooth}: thin-plate undershot the data minimum by {under:F3} dB.");
            Assert.True(worst < 3.0,
                $"smooth={smooth}: worst error {worst:F3} dB against the true surface.");
        }
    }

    /// <summary>
    /// <b>With no smoothing, thin-plate is a true interpolant</b> — the polynomial tail is what makes
    /// its own nodes come back exactly rather than approximately.
    /// </summary>
    [Fact]
    public void ThinPlate_WithNoSmoothing_ReproducesItsNodesExactly()
    {
        var (re, im) = PolarGrid();
        var values = new double[re.Length];
        for (int i = 0; i < re.Length; i++) values[i] = LoadpullBowl(re[i], im[i]);

        var fit = new Rbf2D(re, im, values, RbfKernel.ThinPlate, smooth: 0.0);

        for (int i = 0; i < re.Length; i++)
            Near(values[i], fit.Evaluate(re[i], im[i]), 1e-8, $"ThinPlate node[{i}]");
    }

    /// <summary>
    /// The tail reproduces a PLANE exactly, at any smoothing — that is what the polynomial is for, and
    /// it is the cheapest statement that it is actually being solved for rather than merely present.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(10.0)]
    public void ThinPlate_ReproducesAPlaneAtAnySmoothing(double smooth)
    {
        var (re, im) = PolarGrid();
        var values = new double[re.Length];
        for (int i = 0; i < re.Length; i++) values[i] = 3.0 + 2.0 * re[i] - 1.5 * im[i];

        var fit = new Rbf2D(re, im, values, RbfKernel.ThinPlate, smooth);

        foreach (var (x, y) in new[] { (0.0, 0.0), (0.4, -0.2), (-0.55, 0.3), (0.7, 0.1) })
            Near(3.0 + 2.0 * x - 1.5 * y, fit.Evaluate(x, y), 1e-6, $"plane at ({x},{y}) smooth={smooth}");
    }

    /// <summary>
    /// <b>The Gaussian's default epsilon does not leave it ringing between its own nodes.</b>
    ///
    /// <para>scipy's default works out at roughly the node spacing, and a Gaussian that narrow
    /// interpolates every node perfectly while oscillating ±3 dB in between — a failure a node-based
    /// self-consistency test cannot see, which is why this one surveys the whole disc.</para>
    /// </summary>
    [Fact]
    public void Gaussian_DefaultEpsilon_DoesNotRingBetweenNodes()
    {
        var (re, im) = PolarGrid();
        var values = new double[re.Length];
        for (int i = 0; i < re.Length; i++) values[i] = LoadpullBowl(re[i], im[i]);

        double dataMin = values.Min(), dataMax = values.Max();
        var fit = new Rbf2D(re, im, values, RbfKernel.Gaussian, smooth: 1e-3);
        var (worst, over, under) = SurveyDisc(fit, dataMin, dataMax);

        Assert.True(worst < 1.0,
            $"Gaussian worst error {worst:F3} dB. At scipy's own default epsilon this was 8.5 dB, with " +
            "the node error still reading 0.03 — it interpolated its nodes and lied everywhere else.");
        Assert.True(over < 0.5 && under > -0.5,
            $"Gaussian excursions: +{over:F3} / {under:F3} dB outside the data range.");
    }

    /// <summary>The Gaussian's default epsilon is scipy's, scaled — and a stated epsilon still wins.</summary>
    [Fact]
    public void Gaussian_EpsilonDefaultIsScaled_ButAStatedOneWins()
    {
        var (re, im) = PolarGrid();
        var values = new double[re.Length];
        for (int i = 0; i < re.Length; i++) values[i] = LoadpullBowl(re[i], im[i]);

        double scipy = Rbf2D.ComputeEpsilon(re, im, re.Length);

        Assert.Equal(scipy * Rbf2D.GaussianEpsilonScale,
                     new Rbf2D(re, im, values, RbfKernel.Gaussian).Epsilon, 12);
        Assert.Equal(scipy, new Rbf2D(re, im, values, RbfKernel.Multiquadric).Epsilon, 12);
        Assert.Equal(0.5, new Rbf2D(re, im, values, RbfKernel.Gaussian, 1e-3, 0.5).Epsilon, 12);
    }

    /// <summary>
    /// <b>Multiquadric is untouched by any of it</b> — same epsilon default, same scipy smoothing sign,
    /// no polynomial tail. It is the shipped default and the kernel the loadpull display uses, so the
    /// numbers anyone has already looked at must not move.
    /// </summary>
    [Fact]
    public void Multiquadric_IsUnchangedByTheThinPlateAndGaussianFixes()
    {
        Assert.False(Rbf2D.RequiresPolynomialTail(RbfKernel.Multiquadric));
        Assert.Equal(-1.0, Rbf2D.SmoothingSign(RbfKernel.Multiquadric));
        Assert.True(Rbf2D.RequiresPolynomialTail(RbfKernel.ThinPlate));
        Assert.Equal(+1.0, Rbf2D.SmoothingSign(RbfKernel.Gaussian));

        // The 4-node hand-verified example of test 6 still lands where it always did.
        double[] re = [0, 1, 0, 1], im = [0, 0, 1, 1], val = [1, 2, 3, 4];
        var fit = new Rbf2D(re, im, val, RbfKernel.Multiquadric, smooth: 0.0);
        for (int i = 0; i < 4; i++) Near(val[i], fit.Evaluate(re[i], im[i]), 1e-9, $"MQ node[{i}]");
    }

    /// <summary>
    /// A re-solve through <see cref="Rbf2D.Factorize"/> matches a fresh fit for EVERY kernel — the
    /// thin-plate path stores an augmented system rather than a factorization, so this is where a
    /// divergence between the two entry points would show.
    /// </summary>
    [Theory]
    [InlineData(RbfKernel.Multiquadric)]
    [InlineData(RbfKernel.ThinPlate)]
    [InlineData(RbfKernel.Gaussian)]
    public void Factorize_AgreesWithAFreshFit_ForEveryKernel(RbfKernel kernel)
    {
        var (re, im) = PolarGrid(rings: 3, perRing: 8);
        var a = new double[re.Length];
        var b = new double[re.Length];
        for (int i = 0; i < re.Length; i++)
        {
            a[i] = LoadpullBowl(re[i], im[i]);
            b[i] = 2.0 * LoadpullBowl(re[i], im[i]) - 5.0;
        }

        var factored = Rbf2D.Factorize(re, im, a, kernel, smooth: 1e-2);

        foreach (var values in new[] { a, b })
        {
            var reused = factored.Solve(values);
            var fresh = new Rbf2D(re, im, values, kernel, smooth: 1e-2);

            foreach (var (x, y) in new[] { (0.0, 0.0), (0.3, -0.15), (-0.4, 0.25) })
                Near(fresh.Evaluate(x, y), reused.Evaluate(x, y), 1e-9,
                     $"{kernel} re-solve at ({x},{y})");
        }
    }
}
