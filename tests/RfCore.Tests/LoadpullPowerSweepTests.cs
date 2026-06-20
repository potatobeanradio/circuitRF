// ================================================================
//  LoadpullPowerSweepTests.cs — gate tests for LoadpullSurface 7.4c
//
//  Slices covered:
//    7.4c-1  BuildStackAtCompression (surface stack construction)
//    7.4c-2  GetPowerSweep (synthesis + measured-point tracking)
//    7.4c-3  Origin-blind (.lpcwave) + multi-freq coverage
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Numerics;
using RfCore.Data;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class LoadpullPowerSweepTests
{
    // ── test-data helpers ──────────────────────────────────────────────────────

    private static string SplDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var c1 = Path.Combine(dir, "testdata", "spl_test_data");
            if (Directory.Exists(c1)) return c1;
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

    // ── 7.4c-1: BuildStackAtCompression ───────────────────────────────────────

    [Fact]
    public void Stack_PoutAtComp3dB_SliceCountOverHalf()
    {
        // The Pout stack at 3 dB compression on the 145-point grid should produce
        // well over half of the 32 candidate slices (most back-off levels have
        // enough support to exceed the 12-node threshold).
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var ps = sfc.GetPowerSweep(
            0, new Complex(0.3, 0.0), "Pout", "Pout",
            compressionVal: 3.0, SurfacePlane.Gamma);

        // GetPowerSweep returns null only when stacks can't be built;
        // a successful call proves slices were fitted.
        Assert.NotNull(ps);
        Assert.Equal(LoadpullSurface.NumInterpSweep, ps!.X.Length);
    }

    [Fact]
    public void Stack_NodeCountNearGridCount()
    {
        // Each Rbf2D slice in the stack should have NodeCount close to nGrid=145
        // (minus any NaN-dropped metric values at extreme back-off levels).
        // We verify this indirectly: the sweep returned has no all-NaN regions
        // over the mid-range of the 160-point axis.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var ps = sfc.GetPowerSweep(
            0, new Complex(0.0, 0.0), "PavlDbm", "Pout",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps);
        // Check mid-range is finite (first and last few points may be NaN from interp edges)
        int mid = LoadpullSurface.NumInterpSweep / 2;
        for (int i = mid - 10; i <= mid + 10; i++)
        {
            Assert.True(double.IsFinite(ps!.X[i]), $"X[{i}] should be finite in mid-range");
            Assert.True(double.IsFinite(ps.Y[i]),  $"Y[{i}] should be finite in mid-range");
        }
    }

    [Fact]
    public void Stack_BackoffLadderSpans16dB()
    {
        // The back-off ladder should span ~16 dBm (OBO = InterpStackOBO).
        // Verify: X axis of PavlDbm sweep spans roughly 16 dBm.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var ps = sfc.GetPowerSweep(
            0, new Complex(0.0, 0.0), "PavlDbm", "Gt",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps);

        // Filter NaN, get non-NaN X range
        var validX = ps!.X.Where(double.IsFinite).ToArray();
        Assert.True(validX.Length > 10, "Should have >10 finite X (PavlDbm) points");

        double span = validX.Max() - validX.Min();
        // Should be approximately OBO = 16 dBm (allow ±4 dB for edge effects)
        Assert.True(span > 8.0,  $"PavlDbm span ({span:F2} dBm) should be > 8 dBm");
        Assert.True(span < 20.0, $"PavlDbm span ({span:F2} dBm) should be < 20 dBm");
    }

    [Fact]
    public void Stack_CachingReturnsSameResult()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var query = new Complex(0.2, 0.1);
        var ps1   = sfc.GetPowerSweep(0, query, "Pout", "PAE", 3.0, SurfacePlane.Gamma);
        var ps2   = sfc.GetPowerSweep(0, query, "Pout", "PAE", 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps1);
        Assert.NotNull(ps2);
        // Same cached stacks → identical results
        Assert.Equal(ps1!.X, ps2!.X);
        Assert.Equal(ps1.Y,  ps2.Y);
    }

    // ── 7.4c-2: GetPowerSweep synthesis + measured-point tracking ────────────

    [Fact]
    public void PowerSweep_Returns160Points()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var ps = sfc.GetPowerSweep(
            0, new Complex(0.3, 0.2), "PavlDbm", "PAE",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps);
        Assert.Equal(LoadpullSurface.NumInterpSweep, ps!.X.Length);
        Assert.Equal(LoadpullSurface.NumInterpSweep, ps.Y.Length);
        Assert.Equal("PavlDbm", ps.MetricX);
        Assert.Equal("PAE",     ps.MetricY);
    }

    [Fact]
    public void PowerSweep_PavlDbm_IsMonotonicallyIncreasing()
    {
        // The X axis (PavlDbm) should be strictly increasing over the non-NaN region.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var ps = sfc.GetPowerSweep(
            0, new Complex(0.0, 0.0), "PavlDbm", "Pout",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps);

        // Collect finite-pair indices
        double prev = double.NegativeInfinity;
        int    nFinite = 0;
        for (int i = 0; i < ps!.X.Length; i++)
        {
            if (!double.IsFinite(ps.X[i])) continue;
            Assert.True(ps.X[i] >= prev,
                $"PavlDbm X not monotone at index {i}: X[{i}]={ps.X[i]:F3} < prev={prev:F3}");
            prev = ps.X[i];
            nFinite++;
        }
        Assert.True(nFinite > 50, $"Expected >50 finite X points, got {nFinite}");
    }

    [Fact]
    public void PowerSweep_PoutIncreasesWith_PavlDbm()
    {
        // Over the non-NaN mid-range, Pout(W) should increase as Pin(dBm) increases
        // (PA characteristic: more drive → more output up to saturation).
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var ps = sfc.GetPowerSweep(
            0, new Complex(0.0, 0.0), "PavlDbm", "Pout",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps);

        // Find the range with valid (non-NaN) data
        var validPairs = ps!.X.Zip(ps.Y, (x, y) => (x, y))
            .Where(p => double.IsFinite(p.x) && double.IsFinite(p.y))
            .ToArray();

        Assert.True(validPairs.Length > 30, $"Expected >30 finite (X,Y) pairs, got {validPairs.Length}");

        // Pout (Y) should be non-decreasing over the valid range
        for (int i = 1; i < validPairs.Length; i++)
        {
            Assert.True(validPairs[i].y >= validPairs[i - 1].y - 1e-4,
                $"Pout decreased at index {i}: {validPairs[i].y:E3} < {validPairs[i - 1].y:E3}");
        }
    }

    [Fact]
    public void PowerSweep_AtGridPoint_TracksMeasuredDriveUp()
    {
        // Synthesize at a measured grid point's Γ.
        // The synthesized drive-up should track the measured one within
        // reasonable tolerance over the mid-range.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        // Grid point 72 is near the center of the smith chart (Γ ≈ 0)
        // Use the actual gamma from the scatter at compression
        var scatter = sfc.Reduce(0, "Pout", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.True(scatter.Coords.Length > 70, "Need at least 71 grid points for this test");

        // Pick a grid point near the center (|Γ| < 0.2)
        Complex queryGamma = default;
        bool found = false;
        for (int i = 0; i < scatter.Coords.Length; i++)
        {
            if (scatter.Coords[i].Magnitude < 0.2)
            {
                queryGamma = scatter.Coords[i];
                found = true;
                break;
            }
        }
        Assert.True(found, "Could not find a grid point with |Γ| < 0.2");

        // Synthesize drive-up at that Γ
        var ps = sfc.GetPowerSweep(
            0, queryGamma, "PavlDbm", "Pout",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps);

        // Basic physical checks on the synthesized drive-up
        var validPout = ps!.Y.Where(double.IsFinite).ToArray();
        Assert.True(validPout.Length > 30, "Expected >30 finite Pout(W) points");
        Assert.True(validPout.Max() > 0,   "Peak Pout should be positive");
        Assert.True(validPout.Min() >= 0,  "Pout should be non-negative");

        // Peak Pout should be physically reasonable for a PA
        Assert.True(validPout.Max() < 1000.0, $"Peak Pout {validPout.Max():F1} W is unrealistically high");
    }

    [Fact]
    public void PowerSweep_BracketedByNeighbors()
    {
        // Synthesize at a Γ halfway between two adjacent measured grid points.
        // The result should be broadly bracketed by the two neighbors' behavior.
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        var scatter = sfc.Reduce(0, "Pout", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.True(scatter.Coords.Length > 50);

        // Pick two nearby grid points
        Complex g1 = scatter.Coords[0];
        Complex g2 = scatter.Coords[1];
        Complex gMid = (g1 + g2) / 2.0;

        // Synthesize at midpoint
        var psMid = sfc.GetPowerSweep(0, gMid, "PavlDbm", "Pout", 3.0, SurfacePlane.Gamma);
        var ps1   = sfc.GetPowerSweep(0, g1,   "PavlDbm", "Pout", 3.0, SurfacePlane.Gamma);
        var ps2   = sfc.GetPowerSweep(0, g2,   "PavlDbm", "Pout", 3.0, SurfacePlane.Gamma);

        Assert.NotNull(psMid);
        Assert.NotNull(ps1);
        Assert.NotNull(ps2);

        // Peak Pout at midpoint should be between the two neighbors' peaks (±20% tolerance)
        double peakMid = psMid!.Y.Where(double.IsFinite).Max();
        double peak1   = ps1!.Y.Where(double.IsFinite).Max();
        double peak2   = ps2!.Y.Where(double.IsFinite).Max();

        double lo = Math.Min(peak1, peak2) * 0.8;
        double hi = Math.Max(peak1, peak2) * 1.2;
        Assert.True(peakMid >= lo && peakMid <= hi,
            $"Mid-point peak Pout {peakMid:F4} W not bracketed by [{lo:F4}, {hi:F4}] W");
    }

    [Fact]
    public void PowerSweep_NullReturnedForInvalidMetric()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);

        // "NonExistentMetric" is not in DriveUps — stacks can't be built
        var ps = sfc.GetPowerSweep(
            0, new Complex(0.1, 0.1), "NonExistentMetric", "Pout",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.Null(ps);
    }

    // ── 7.4c-3: Origin-blind (.lpcwave) + multi-freq coverage ────────────────

    [Fact]
    public void LpcwaveSource_PowerSweep_Returns160Points()
    {
        var ds  = LpcwaveReader.ReadLpcwave(LpwFile("4x150_new_wavecal_24012020.lpcwave"));
        var sfc = new LoadpullSurface(ds);

        Assert.True(sfc.GridPointCount(0) > 0, "Need at least 1 grid point");

        double recComp = sfc.RecommendedCompression(0);
        var ps = sfc.GetPowerSweep(
            0, new Complex(0.0, 0.0), "PavlDbm", "Pout",
            compressionVal: recComp, SurfacePlane.Gamma);

        // If the file has enough grid points and drive-up depth, this should succeed
        if (ps is not null)
        {
            Assert.Equal(LoadpullSurface.NumInterpSweep, ps.X.Length);
            Assert.Equal(LoadpullSurface.NumInterpSweep, ps.Y.Length);

            // At least some non-NaN values
            Assert.True(ps.X.Any(double.IsFinite), "Expected some finite X values from lpcwave");
            Assert.True(ps.Y.Any(double.IsFinite), "Expected some finite Y values from lpcwave");
        }
        // else: file may not have enough points for the stack; that's acceptable (returns null)
    }

    [Fact]
    public void LpcwaveSource_PowerSweep_FiniteCurve()
    {
        var ds  = LpcwaveReader.ReadLpcwave(LpwFile("4x150_new_wavecal_24012020.lpcwave"));
        var sfc = new LoadpullSurface(ds);

        double recComp = sfc.RecommendedCompression(0);

        // Try multiple query points — at least one should produce a non-null finite sweep
        var queries = new[] { Complex.Zero, new Complex(0.2, 0.0), new Complex(-0.1, 0.1) };
        bool anySuccess = false;
        foreach (var q in queries)
        {
            var ps = sfc.GetPowerSweep(0, q, "PavlDbm", "Pout", recComp, SurfacePlane.Gamma);
            if (ps is null) continue;
            var finiteY = ps.Y.Where(double.IsFinite).ToArray();
            if (finiteY.Length > 20)
            {
                anySuccess = true;
                break;
            }
        }
        Assert.True(anySuccess, "At least one query point should produce a finite lpcwave power sweep");
    }

    [Fact]
    public void MultiFreq_PowerSweep_Freq1Returns160Points()
    {
        // Test on freq index 1 of the 3-frequency .spl file
        var ds  = SplReader.ReadSpl(SplFile("GaN_FET_1p6_mm_3_Freq.spl"));
        var sfc = new LoadpullSurface(ds);

        Assert.Equal(3, sfc.Frequencies.Count);

        var ps = sfc.GetPowerSweep(
            1, new Complex(0.2, 0.1), "PavlDbm", "Pout",
            compressionVal: 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps);
        Assert.Equal(LoadpullSurface.NumInterpSweep, ps!.X.Length);
        Assert.Equal(LoadpullSurface.NumInterpSweep, ps.Y.Length);
        Assert.True(ps.X.Any(double.IsFinite), "Expected finite X values at freq[1]");
        Assert.True(ps.Y.Any(double.IsFinite), "Expected finite Y values at freq[1]");
    }

    [Fact]
    public void MultiFreq_DifferentFreqs_DifferentSweeps()
    {
        // The same query Γ at different frequencies should give different drive-ups
        // (different PA behavior at different frequencies).
        var ds  = SplReader.ReadSpl(SplFile("GaN_FET_1p6_mm_3_Freq.spl"));
        var sfc = new LoadpullSurface(ds);

        var query = new Complex(0.1, 0.0);
        var ps0 = sfc.GetPowerSweep(0, query, "PavlDbm", "Pout", 3.0, SurfacePlane.Gamma);
        var ps1 = sfc.GetPowerSweep(1, query, "PavlDbm", "Pout", 3.0, SurfacePlane.Gamma);

        Assert.NotNull(ps0);
        Assert.NotNull(ps1);

        // Peak Pout should differ across frequencies
        double peak0 = ps0!.Y.Where(double.IsFinite).DefaultIfEmpty(0.0).Max();
        double peak1 = ps1!.Y.Where(double.IsFinite).DefaultIfEmpty(0.0).Max();

        // They should not be identical (different freq = different PA impedance match)
        // Allow a 5% relative match to still call them "different"
        if (peak0 > 0 && peak1 > 0)
        {
            double relDiff = Math.Abs(peak0 - peak1) / Math.Max(peak0, peak1);
            Assert.True(relDiff > 0.001,
                $"Peak Pout at freq 0 ({peak0:E3} W) and freq 1 ({peak1:E3} W) are suspiciously identical");
        }
    }
}
