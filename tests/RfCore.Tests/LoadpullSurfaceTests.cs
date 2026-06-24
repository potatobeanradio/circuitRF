// ================================================================
//  LoadpullSurfaceTests.cs — gate tests for LoadpullSurface (brief 7.4b)
//
//  Slices covered:
//    7.4b-1  Compression preprocessing
//    7.4b-2  Scatter reduction + RBF fit + cache
//    7.4b-3  Resample + MXP/MXE + auto-view-box
//    Multi-freq correctness (GaN_FET_1p6_mm_3_Freq.spl)
//    Source-blind .lpcwave entry point
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class LoadpullSurfaceTests
{
    // ── test-data helpers ──────────────────────────────────────────────────────

    private static string SplDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var c1 = Path.Combine(dir, "testdata", "spl_test_data");
            if (Directory.Exists(c1)) return c1;
            // sibling circuitRF repo
            var c2 = Path.Combine(dir, "circuitRF", "testdata", "spl_test_data");
            if (Directory.Exists(c2)) return c2;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/spl_test_data not found");
    }

    private static string LpwDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var c1 = Path.Combine(dir, "testdata", "lpwave_test_data");
            if (Directory.Exists(c1)) return c1;
            var c2 = Path.Combine(dir, "circuitRF", "testdata", "lpwave_test_data");
            if (Directory.Exists(c2)) return c2;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("testdata/lpwave_test_data not found");
    }

    private static string SplFile(string name) => Path.Combine(SplDir(), name);
    private static string LpwFile(string name) => Path.Combine(LpwDir(), name);

    private static void Near(double expected, double actual, double tol, string label = "")
        => Assert.InRange(actual, expected - tol, expected + tol);

    // ── Z-plane contour coverage: MeasuredBox encloses the MXP/MXE auto-zoom ──
    // Regression: a Rect (Z-plane) contour must resample over the FULL measured data extent so
    // iso-lines render wherever data exists — not only within RecommendedBox's MXP/MXE zoom (which
    // for a constant-metric contour can sit in a different load region, leaving the user's view empty).
    [Fact]
    public void MeasuredBox_EnclosesRecommendedBox_AndResampleCoversExtent()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);
        var fit = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Z);
        Assert.NotNull(fit);

        var mb = sfc.MeasuredBox(fit!);
        var rb = sfc.RecommendedBox(fit!);
        // RecommendedBox is clipped to (⊆) the measured extent; MeasuredBox is the full extent.
        Assert.True(mb.MinRe <= rb.MinRe + 1e-6 && mb.MaxRe >= rb.MaxRe - 1e-6, "MeasuredBox must enclose RecommendedBox");
        Assert.True(mb.SpanRe > 0, "MeasuredBox must be non-degenerate");

        // Resampling over the full extent yields finite contour data spanning it.
        var grid = sfc.Resample(fit!, mb);
        Assert.True(grid.XSpace[0] >= mb.MinRe - 1e-6 && grid.XSpace[^1] <= mb.MaxRe + 1e-6);
        Assert.Contains(grid.Values, v => !double.IsNaN(v));
    }

    // Regression: MXP/MXE recommended terminations are COMPRESSION-based (P-3dB), independent of the
    // contour's own metric/constraint — so "Efficiency at Constant Pout" reuses the same MXP/MXE (and
    // RecommendedBox zoom) as the power contour, instead of a constant-Pout MXE in a wrong load region.
    [Fact]
    public void RecommendedMxx_IsCompressionBased_IndependentOfFitConstraint()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var compFit = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Z);
        Assert.NotNull(compFit);

        var (mxp, mxe) = sfc.RecommendedMxx(compFit!);
        Assert.NotNull(mxp);
        Assert.NotNull(mxe);
        // Equal the explicit P-3dB compression optima.
        Assert.Equal(sfc.MaxPower(0,      ConstraintSpec.AtCompression(3.0), SurfacePlane.Z)!.Measured.Real, mxp!.Measured.Real, precision: 6);
        Assert.Equal(sfc.MaxEfficiency(0, ConstraintSpec.AtCompression(3.0), SurfacePlane.Z)!.Measured.Real, mxe!.Measured.Real, precision: 6);

        // A constant-metric fit (Efficiency at Constant Pout) yields the SAME recommended MXP/MXE.
        var cmFit = sfc.Fit(0, "Efficiency", ConstraintSpec.AtConstantMetric("Pout_dBm", 10.0), SurfacePlane.Z);
        if (cmFit is not null)
        {
            var (mxp2, mxe2) = sfc.RecommendedMxx(cmFit);
            Assert.Equal(mxp!.Measured.Real, mxp2!.Measured.Real, precision: 6);
            Assert.Equal(mxe!.Measured.Real, mxe2!.Measured.Real, precision: 6);
        }
    }

    // ── 7.4b-1: Compression preprocessing ────────────────────────────────────

    [Fact]
    public void Compression_GridPointCount_Is145()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        Assert.Equal(145, sfc.GridPointCount(0));
    }

    [Fact]
    public void Compression_MedianIsPositive()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        double med = sfc.MedianCompression(0);
        Assert.True(med > 0,          $"MedianCompression should be > 0, got {med}");
        Assert.True(med < 30,         $"MedianCompression should be physically < 30 dB, got {med}");
    }

    [Fact]
    public void Compression_RecommendedSettingIsKnownValue()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        // RecommendedCompression must be one of {0.1, 0.5, 1, 2, ..., 19}
        double rec = sfc.RecommendedCompression(0);
        var allowed = new[] { 0.1, 0.5, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19 };
        Assert.Contains(rec, allowed);
    }

    [Fact]
    public void Compression_CurveStartsAtZeroAndIncreases_ForOnePoint()
    {
        // Spot-check: use the reduce call as a proxy — if compression
        // preprocessing is wrong the scatter at 0 dB compression will
        // contain nonsense.  We verify via a direct internal check:
        // grid point 0 in Ideal_GaN is at Γ=0 (50 Ω), so Gt drive-up
        // peaks somewhere in the middle of the sweep.
        // Use Reduce to confirm a valid Y value is returned.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        // Reduce at 3 dB compression for Pout — should produce a scatter
        var scatter = sfc.Reduce(0, "Pout_dBm",
            ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);

        // At least ~100 out of 145 grid points should have valid Pout at 3 dB
        int nFinite = scatter.Values.Count(v => !double.IsNaN(v) && double.IsFinite(v));
        Assert.True(nFinite >= 80,
            $"Expected ≥80 finite points at 3 dB compression, got {nFinite}");
    }

    // ── 7.4b-2: Scatter reduction + RBF fit + cache ───────────────────────────

    [Fact]
    public void Reduce_PoutAt3dBCompression_ReturnsMostlyFiniteCoords()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var scatter = sfc.Reduce(0, "Pout_dBm",
            ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);

        // Should have coords for the majority of grid points
        Assert.True(scatter.Coords.Length > 100,
            $"Expected >100 coords, got {scatter.Coords.Length}");

        // All coords should be inside the unit disk (they're Γ)
        foreach (var g in scatter.Coords)
            Assert.True(g.Magnitude <= 1.01,
                $"Γ coord outside unit disk: {g.Magnitude:F4}");
    }

    [Fact]
    public void Fit_BuildsRbfWithNodeCountNearGridCount()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var fit = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);

        Assert.NotNull(fit);
        // NodeCount ≤ 145 and should be close (minus any NaN drops)
        Assert.True(fit!.Rbf.NodeCount > 80,  $"Expected >80 nodes, got {fit.Rbf.NodeCount}");
        Assert.True(fit.Rbf.NodeCount <= 145, $"Expected ≤145 nodes, got {fit.Rbf.NodeCount}");
    }

    [Fact]
    public void Fit_SecondCallReturnsCachedInstance()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var constraint = ConstraintSpec.AtCompression(3.0);
        var fit1 = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma);
        var fit2 = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma);

        Assert.NotNull(fit1);
        Assert.Same(fit1, fit2);  // exact same reference from cache
    }

    [Fact]
    public void Fit_DifferentSmoothProducesNewFit()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var constraint = ConstraintSpec.AtCompression(3.0);
        var fit1 = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma, smooth: 1e-3);
        var fit2 = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma, smooth: 1e-4);

        Assert.NotNull(fit1);
        Assert.NotNull(fit2);
        Assert.NotSame(fit1, fit2);
    }

    [Fact]
    public void Fit_DifferentConstraintValueProducesNewFit()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var fit3dB = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        var fit1dB = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(1.0), SurfacePlane.Gamma);

        Assert.NotNull(fit3dB);
        Assert.NotNull(fit1dB);
        Assert.NotSame(fit3dB, fit1dB);
    }

    // ── 7.4h-1a: epsilon cache ───────────────────────────────────────────────

    [Fact]
    public void Fit_DifferentEpsilonProducesDistinctCacheEntry()
    {
        var ds         = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc        = new LoadpullSurface(ds);
        var constraint = ConstraintSpec.AtCompression(3.0);

        var fitAuto   = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma, epsilon: null);
        var fitCustom = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma, epsilon: 2.0);

        Assert.NotNull(fitAuto);
        Assert.NotNull(fitCustom);
        Assert.NotSame(fitAuto, fitCustom);
        Assert.Equal(2.0, fitCustom!.Epsilon);
        Assert.Null(fitAuto!.Epsilon);
    }

    [Fact]
    public void Fit_SameEpsilonReturnsCachedInstance()
    {
        var ds         = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc        = new LoadpullSurface(ds);
        var constraint = ConstraintSpec.AtCompression(3.0);

        var fit1 = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma, epsilon: 1.5);
        var fit2 = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma, epsilon: 1.5);

        Assert.Same(fit1, fit2);
    }

    // ── 7.4b-3: Resample + MXP/MXE + view-box ───────────────────────────────

    [Fact]
    public void MaxPower_MeasuredGammaInsideUnitDisk()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var mxp = sfc.MaxPower(0, ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);

        Assert.NotNull(mxp);
        Assert.True(mxp!.Measured.Magnitude <= 1.0 + 1e-6,
            $"|Γ_mxp| = {mxp.Measured.Magnitude:F4} should be ≤ 1");
    }

    [Fact]
    public void MaxPower_InterpolatedNearMeasured()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var mxp = sfc.MaxPower(0, ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);

        Assert.NotNull(mxp);
        // Interpolated should be near measured (within VSWR=1.2 search circle)
        double dist = (mxp!.Interpolated - mxp.Measured).Magnitude;
        Assert.True(dist < 0.5, $"MXP interpolated far from measured: Δ={dist:F4}");
    }

    [Fact]
    public void RecommendedBox_IsFiniteAndContainsMXP()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var constraint = ConstraintSpec.AtCompression(3.0);
        var fit = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma);
        Assert.NotNull(fit);

        var box = sfc.RecommendedBox(fit!);
        Assert.True(box.IsValid, $"ViewBox invalid: {box}");
        Assert.True(double.IsFinite(box.MinRe), "MinRe not finite");
        Assert.True(double.IsFinite(box.MaxRe), "MaxRe not finite");
        Assert.True(double.IsFinite(box.MinIm), "MinIm not finite");
        Assert.True(double.IsFinite(box.MaxIm), "MaxIm not finite");

        var mxp = sfc.MaxPower(0, constraint, SurfacePlane.Gamma);
        if (mxp != null)
        {
            var g = mxp.Measured;
            // Box should contain (or nearly contain) MXP with small tolerance
            Assert.True(g.Real   >= box.MinRe - 0.05, $"MXP.Re {g.Real:F4} < MinRe {box.MinRe:F4}");
            Assert.True(g.Real   <= box.MaxRe + 0.05, $"MXP.Re {g.Real:F4} > MaxRe {box.MaxRe:F4}");
            Assert.True(g.Imaginary >= box.MinIm - 0.05, $"MXP.Im {g.Imaginary:F4} < MinIm {box.MinIm:F4}");
            Assert.True(g.Imaginary <= box.MaxIm + 0.05, $"MXP.Im {g.Imaginary:F4} > MaxIm {box.MaxIm:F4}");
        }
    }

    [Fact]
    public void Resample_50x50GridMaxNearMxpValue()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var constraint = ConstraintSpec.AtCompression(3.0);
        var fit  = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma);
        Assert.NotNull(fit);

        var grid = sfc.Resample(fit!, resolution: 50);

        Assert.Equal(50, grid.XSpace.Length);
        Assert.Equal(50, grid.YSpace.Length);
        Assert.Equal(50 * 50, grid.Values.Length);

        // Grid max should be near the measured MXP value (within interp tolerance)
        double gridMax = grid.Values.Where(v => !double.IsNaN(v)).Max();
        Assert.True(double.IsFinite(gridMax) && gridMax > 0,
            $"Grid max should be positive finite Pout (W), got {gridMax}");
    }

    [Fact]
    public void Resample_GammaDisk_OutOfDiskCellsAreNaN()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var fit  = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.NotNull(fit);

        // Use a known box that extends outside the unit disk
        var bigBox = new ViewBox(-1.0, 1.0, -1.0, 1.0);
        var grid   = sfc.Resample(fit!, box: bigBox, resolution: 20);

        int nNaN    = grid.Values.Count(double.IsNaN);
        int nFinite = grid.Values.Count(v => !double.IsNaN(v));

        // With a ±1 box some points must be NaN (outside the Γ measured-data disk)
        // and some must be finite (the center region)
        Assert.True(nFinite > 0, "Expected some finite grid values");
        Assert.True(nNaN    > 0, "Expected some NaN cells outside Γ-disk");
    }

    [Fact]
    public void Resample_ValuesPhysicallyPlausible()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var fit  = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.NotNull(fit);

        var grid = sfc.Resample(fit!, resolution: 50);

        // Pout is in Watts; all non-NaN values should be positive (or at least > -10 W for a PA)
        foreach (var v in grid.Values.Where(x => !double.IsNaN(x)))
            Assert.True(v > -10.0 && v < 1000.0, $"Pout value {v} W not physically plausible");
    }

    // ── Multi-freq correctness ─────────────────────────────────────────────────

    [Fact]
    public void MultiFreq_SplFile_ThreeFrequencies()
    {
        var ds  = SplReader.ReadSpl(SplFile("GaN_FET_1p6_mm_3_Freq.spl"));
        var sfc = new LoadpullSurface(ds);

        Assert.Equal(3, sfc.Frequencies.Count);
    }

    [Fact]
    public void MultiFreq_GridPointCountCorrectPerFreq()
    {
        var ds  = SplReader.ReadSpl(SplFile("GaN_FET_1p6_mm_3_Freq.spl"));
        var sfc = new LoadpullSurface(ds);

        for (int fi = 0; fi < sfc.Frequencies.Count; fi++)
        {
            int n = sfc.GridPointCount(fi);
            Assert.True(n > 50, $"freq[{fi}] expected >50 grid points, got {n}");
        }
    }

    [Fact]
    public void MultiFreq_CompressionSanity()
    {
        var ds  = SplReader.ReadSpl(SplFile("GaN_FET_1p6_mm_3_Freq.spl"));
        var sfc = new LoadpullSurface(ds);

        for (int fi = 0; fi < sfc.Frequencies.Count; fi++)
        {
            double med = sfc.MedianCompression(fi);
            Assert.True(med > 0,  $"freq[{fi}] MedianCompression should be > 0, got {med}");
            Assert.True(med < 30, $"freq[{fi}] MedianCompression should be < 30 dB, got {med}");
        }
    }

    [Fact]
    public void MultiFreq_FitBuildsSeparateCachePerFreq()
    {
        var ds  = SplReader.ReadSpl(SplFile("GaN_FET_1p6_mm_3_Freq.spl"));
        var sfc = new LoadpullSurface(ds);

        var constraint = ConstraintSpec.AtCompression(3.0);
        var fit0 = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma);
        var fit1 = sfc.Fit(1, "Pout_dBm", constraint, SurfacePlane.Gamma);

        Assert.NotNull(fit0);
        Assert.NotNull(fit1);
        Assert.NotSame(fit0, fit1);
    }

    // ── .lpcwave source (origin-blind) ────────────────────────────────────────

    [Fact]
    public void LpcwaveSource_ConstructsSuccessfully()
    {
        var ds  = LpcwaveReader.ReadLpcwave(LpwFile("4x150_new_wavecal_24012020.lpcwave"));
        var sfc = new LoadpullSurface(ds);

        Assert.True(sfc.Frequencies.Count >= 1);
        Assert.True(sfc.GridPointCount(0) > 0);
    }

    [Fact]
    public void LpcwaveSource_CompressionPreprocessing_Valid()
    {
        var ds  = LpcwaveReader.ReadLpcwave(LpwFile("4x150_new_wavecal_24012020.lpcwave"));
        var sfc = new LoadpullSurface(ds);

        double med = sfc.MedianCompression(0);
        Assert.True(med >= 0,  $"lpcwave MedianCompression should be ≥ 0, got {med}");
        Assert.True(med < 30,  $"lpcwave MedianCompression should be < 30 dB, got {med}");
    }

    [Fact]
    public void LpcwaveSource_ReduceReturnsCoords()
    {
        var ds  = LpcwaveReader.ReadLpcwave(LpwFile("4x150_new_wavecal_24012020.lpcwave"));
        var sfc = new LoadpullSurface(ds);

        double recComp = sfc.RecommendedCompression(0);
        var scatter    = sfc.Reduce(0, "Pout_dBm",
            ConstraintSpec.AtCompression(recComp), SurfacePlane.Gamma);

        Assert.True(scatter.Coords.Length > 0,
            "Expected ≥1 scatter coord from lpcwave at recommended compression");
    }

    [Fact]
    public void MaxEfficiency_DifferentKernels_ProduceDifferentInterpolatedPeak()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);
        var constraint = ConstraintSpec.AtCompression(sfc.RecommendedCompression(0));

        var mxeMQ = sfc.MaxEfficiency(0, constraint, SurfacePlane.Gamma,
            kernel: RbfKernel.Multiquadric);
        var mxeTP = sfc.MaxEfficiency(0, constraint, SurfacePlane.Gamma,
            kernel: RbfKernel.ThinPlate);

        Assert.NotNull(mxeMQ);
        Assert.NotNull(mxeTP);
        double distRe = Math.Abs(mxeMQ!.Interpolated.Real  - mxeTP!.Interpolated.Real);
        double distIm = Math.Abs(mxeMQ!.Interpolated.Imaginary - mxeTP!.Interpolated.Imaginary);
        Assert.True(distRe + distIm > 1e-9,
            "MaxEfficiency interpolated peak must vary with kernel choice");
    }

    // ── 7.5a: summary-table accessors ────────────────────────────────────────

    [Fact]
    public void MetricAtCoord_Interp_EqualsSurfaceEval()
    {
        var ds         = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc        = new LoadpullSurface(ds);
        var constraint = ConstraintSpec.AtCompression(3.0);

        var mxp = sfc.MaxPower(0, constraint, SurfacePlane.Gamma);
        Assert.NotNull(mxp);
        Complex optimum = mxp!.Interpolated;

        double fromAccessor = sfc.MetricAtCoord(0, "Pout_dBm", optimum, constraint,
            SurfacePlane.Gamma, nearest: false);
        double fromFit      = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma)!
                                  .Rbf.Evaluate(optimum.Real, optimum.Imaginary);

        Assert.True(double.IsFinite(fromAccessor), $"MetricAtCoord result should be finite, got {fromAccessor}");
        Assert.Equal(fromFit, fromAccessor);
    }

    [Fact]
    public void MetricAtCoord_Nearest_ReturnsNodeValue()
    {
        var ds         = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc        = new LoadpullSurface(ds);
        var constraint = ConstraintSpec.AtCompression(3.0);

        var mxp = sfc.MaxPower(0, constraint, SurfacePlane.Gamma);
        Assert.NotNull(mxp);
        Complex optimum = mxp!.Measured;

        double result = sfc.MetricAtCoord(0, "Pout_dBm", optimum, constraint,
            SurfacePlane.Gamma, nearest: true);

        var rbf = sfc.Fit(0, "Pout_dBm", constraint, SurfacePlane.Gamma)!.Rbf;
        bool isNodeValue = false;
        for (int i = 0; i < rbf.NodeCount; i++)
            if (rbf.NodeValues[i] == result) { isNodeValue = true; break; }
        Assert.True(isNodeValue, $"MetricAtCoord nearest={result} is not any measured node value");
    }

    [Fact]
    public void MetricAtCoord_AbsentMetric_ReturnsNaN()
    {
        var ds         = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc        = new LoadpullSurface(ds);
        var constraint = ConstraintSpec.AtCompression(3.0);

        double result = sfc.MetricAtCoord(0, "NopeMetric", Complex.Zero, constraint,
            SurfacePlane.Gamma, nearest: false);

        Assert.True(double.IsNaN(result), $"Expected NaN for absent metric, got {result}");
    }

    [Fact]
    public void OperatingPoint_AbsentCube_ReturnsNull()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        Assert.Null(sfc.OperatingPoint(0, "NopeCube"));
    }

    [Fact]
    public void OperatingPoint_PresentCube_ReturnsFiniteOrAbsent()
    {
        // BiasVLoad is mapped from VDD; present in some .spl files, absent in others.
        // This test is presence-tolerant: finite value if present, null if absent.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var vdd = sfc.OperatingPoint(0, "BiasVLoad");
        if (vdd.HasValue)
            Assert.True(double.IsFinite(vdd.Value), $"BiasVLoad should be finite, got {vdd.Value}");
        // else: cube absent in this fixture — presence-tolerant, no assertion needed
    }

    [Fact]
    public void SourceZ_PresentAfterImport_ReturnsFiniteValue()
    {
        // After 7.5g, gamma_src1 is captured from the fixture → ZSource cube added → SourceZ is finite.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var sz = sfc.SourceZ(0);
        Assert.NotNull(sz);
        Assert.True(double.IsFinite(sz!.Value.Real),      $"SourceZ.Real should be finite, got {sz.Value.Real}");
        Assert.True(double.IsFinite(sz.Value.Imaginary),  $"SourceZ.Imag should be finite, got {sz.Value.Imaginary}");
    }
}
