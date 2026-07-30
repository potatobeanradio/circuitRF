// ================================================================
//  ContourExtractorTests.cs — gate tests for ContourExtractor (brief 7.4d-1)
//
//  Slices covered:
//    7.4d-1  Marching-squares extraction: analytic circle, saddle, real grid
//            Level-set builder helpers (LevelsBetween, LevelsByStep)
// ================================================================

using System;
using System.IO;
using System.Linq;
using RfCore.Loadpull;
using Xunit;

namespace RfCore.Tests;

public class ContourExtractorTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

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

    private static string SplFile(string name) => Path.Combine(SplDir(), name);

    // Build a synthetic SurfaceGrid over a uniform square domain.
    // f is evaluated at each (x, y) pair; NaN from f → NaN in grid.
    private static SurfaceGrid MakeGrid(int res, double lo, double hi, Func<double, double, double> f)
    {
        double[] xs = new double[res];
        double[] ys = new double[res];
        for (int i = 0; i < res; i++)
            xs[i] = ys[i] = lo + (hi - lo) * i / (res - 1);

        double[] vals = new double[res * res];
        for (int yi = 0; yi < res; yi++)
            for (int xi = 0; xi < res; xi++)
                vals[yi * res + xi] = f(xs[xi], ys[yi]);

        return new SurfaceGrid(xs, ys, vals);
    }

    // ── 7.4d-1a: analytic circle ───────────────────────────────────────────────

    [Fact]
    public void Circle_ExtractsOneClosedPolyline()
    {
        // f(x,y) = x²+y²; level = r² gives a circle of radius r
        const double r = 0.8;
        var grid = MakeGrid(res: 41, lo: -1.2, hi: 1.2, f: (x, y) => x * x + y * y);
        var levels = new ContourLevelSet(new[] { r * r });

        var polylines = ContourExtractor.Extract(grid, levels);

        // Exactly one closed polyline for an interior circle
        Assert.Single(polylines);
        Assert.True(polylines[0].Closed, "Circle contour must be Closed=true");
    }

    [Fact]
    public void Circle_PointsCloseToTrueRadius()
    {
        const double r = 0.8;
        const int res = 41;
        // Grid spacing ≈ 2.4/40 = 0.06; max error < half-spacing = 0.03
        var grid = MakeGrid(res, lo: -1.2, hi: 1.2, f: (x, y) => x * x + y * y);
        var levels = new ContourLevelSet(new[] { r * r });

        var polylines = ContourExtractor.Extract(grid, levels);
        Assert.Single(polylines);

        double maxErr = polylines[0].Points.Max(p => Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - r));
        Assert.True(maxErr < 0.08,
            $"Max radius deviation {maxErr:F4} exceeds grid tolerance (expected < 0.08)");
    }

    [Fact]
    public void Circle_HasAtLeast8Points()
    {
        const double r = 0.6;
        var grid = MakeGrid(res: 31, lo: -1.0, hi: 1.0, f: (x, y) => x * x + y * y);
        var levels = new ContourLevelSet(new[] { r * r });

        var polylines = ContourExtractor.Extract(grid, levels);
        Assert.Single(polylines);
        Assert.True(polylines[0].Points.Count >= 8,
            $"Expected ≥8 points on the circle polyline, got {polylines[0].Points.Count}");
    }

    [Fact]
    public void Circle_SmallRadiusInsideGrid_ClosedLoop()
    {
        // Even a very small circle (r=0.2) entirely inside the grid should close
        const double r = 0.2;
        var grid = MakeGrid(res: 61, lo: -1.0, hi: 1.0, f: (x, y) => x * x + y * y);
        var levels = new ContourLevelSet(new[] { r * r });

        var polylines = ContourExtractor.Extract(grid, levels);
        Assert.Single(polylines);
        Assert.True(polylines[0].Closed);
    }

    // ── 7.4d-1b: saddle field ──────────────────────────────────────────────────

    [Fact]
    public void Saddle_NoClosedPolylines()
    {
        // f(x,y) = x²-y²; iso-line at 0.1 forms two open hyperbola branches
        var grid = MakeGrid(res: 21, lo: -1.0, hi: 1.0, f: (x, y) => x * x - y * y);
        var levels = new ContourLevelSet(new[] { 0.1 });

        var polylines = ContourExtractor.Extract(grid, levels);

        // Saddle iso-lines exit the grid domain — no closed loops
        Assert.All(polylines, pl => Assert.False(pl.Closed,
            $"Saddle level should produce open chains, got Closed=true for level {pl.Level}"));
    }

    [Fact]
    public void Saddle_ProducesPolylines()
    {
        var grid = MakeGrid(res: 21, lo: -1.0, hi: 1.0, f: (x, y) => x * x - y * y);
        var levels = new ContourLevelSet(new[] { 0.1, -0.1 });

        var polylines = ContourExtractor.Extract(grid, levels);
        Assert.True(polylines.Count >= 2,
            $"Saddle should produce ≥2 polylines (one per level), got {polylines.Count}");
    }

    // ── 7.4d-1c: real loadpull grid ───────────────────────────────────────────

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public void RealGrid_SaneLevelsAndCounts()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);
        var fit = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.NotNull(fit);

        var grid = sfc.Resample(fit!, resolution: 30);
        var levels = ContourExtractor.LevelsBetween(grid, 5);
        var polylines = ContourExtractor.Extract(grid, levels);

        Assert.True(polylines.Count >= 1,
            $"Expected ≥1 polyline from real loadpull grid, got {polylines.Count}");
        Assert.True(levels.Levels.Length == 5,
            $"LevelsBetween(5) should return 5 levels, got {levels.Levels.Length}");
    }

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public void RealGrid_OpenContoursAtDiskBoundary()
    {
        // Resample over full ±1 box so contours hit the NaN-disk boundary
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);
        var fit = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.NotNull(fit);

        var bigBox = new ViewBox(-1.0, 1.0, -1.0, 1.0);
        var grid   = sfc.Resample(fit!, box: bigBox, resolution: 30);
        // Use 10 levels so that fine outer contours (near min Pout) reach the NaN boundary
        var levels = ContourExtractor.LevelsBetween(grid, 10);
        var polylines = ContourExtractor.Extract(grid, levels);

        bool anyOpen = polylines.Any(p => !p.Closed);
        Assert.True(anyOpen,
            $"Expected ≥1 open polyline from levels-between-10 where contours hit the Γ-disk NaN boundary (got {polylines.Count} total, all closed)");
    }

    [FixtureFact("testdata/spl_test_data", "ask the repo owner for these lab-measured .spl files — not committed to the repository")]
    public void RealGrid_AllPointsWithinBounds()
    {
        var ds  = SplReader.ReadSpl(SplFile("Ideal_GaN_FET_1p6_mm_1p8_GHz.spl"));
        var sfc = new LoadpullSurface(ds);
        var fit = sfc.Fit(0, "Pout_dBm", ConstraintSpec.AtCompression(3.0), SurfacePlane.Gamma);
        Assert.NotNull(fit);

        var grid    = sfc.Resample(fit!, resolution: 30);
        var box     = sfc.RecommendedBox(fit!);
        var levels  = ContourExtractor.LevelsBetween(grid, 5);
        var polys   = ContourExtractor.Extract(grid, levels);

        // All contour points must lie within the grid's XSpace/YSpace bounds (+ small tolerance)
        double xMin = grid.XSpace[0]                    - 1e-6;
        double xMax = grid.XSpace[grid.XSpace.Length-1] + 1e-6;
        double yMin = grid.YSpace[0]                    - 1e-6;
        double yMax = grid.YSpace[grid.YSpace.Length-1] + 1e-6;

        foreach (var pl in polys)
            foreach (var (px, py) in pl.Points)
            {
                Assert.True(px >= xMin && px <= xMax, $"Contour X={px:F4} out of grid bounds [{xMin:F4},{xMax:F4}]");
                Assert.True(py >= yMin && py <= yMax, $"Contour Y={py:F4} out of grid bounds [{yMin:F4},{yMax:F4}]");
            }

        _ = box; // silence unused-variable warning (box is a value type)
    }

    // ── 7.4d-1d: level-set builders ───────────────────────────────────────────

    [Fact]
    public void LevelsBetween_ReturnCorrectCount()
    {
        var grid = MakeGrid(res: 11, lo: -1.0, hi: 1.0, f: (x, y) => x * x + y * y);

        Assert.Equal(5, ContourExtractor.LevelsBetween(grid, 5).Levels.Length);
        Assert.Single(ContourExtractor.LevelsBetween(grid, 1).Levels);
        Assert.Equal(3, ContourExtractor.LevelsBetween(grid, 3).Levels.Length);
    }

    [Fact]
    public void LevelsBetween_SpanGridMinMax()
    {
        var grid = MakeGrid(res: 11, lo: -1.0, hi: 1.0, f: (x, y) => x * x + y * y);
        // Grid values range from 0 (at origin) to ~2 (at corners ±1,±1)
        var levelSet = ContourExtractor.LevelsBetween(grid, 5);

        Assert.True(levelSet.Levels[0] >= 0, "First level should be at grid min (≥0)");
        Assert.True(levelSet.Levels[4] <= 2.0 + 1e-6, "Last level should be at grid max (≤2)");

        // Levels should be monotonically increasing
        for (int i = 1; i < levelSet.Levels.Length; i++)
            Assert.True(levelSet.Levels[i] > levelSet.Levels[i - 1],
                $"Levels not monotone at index {i}");
    }

    [Fact]
    public void LevelsByStep_ProducesCorrectValues()
    {
        // Grid values 0..10
        double[] vals = new double[121];
        double[] xs   = new double[11];
        for (int i = 0; i < 11; i++) xs[i] = i;
        for (int yi = 0; yi < 11; yi++)
            for (int xi = 0; xi < 11; xi++)
                vals[yi * 11 + xi] = xi + yi; // 0..20 total
        var grid = new SurfaceGrid(xs, xs, vals);

        var levelSet = ContourExtractor.LevelsByStep(grid, step: 2.0, anchor: 0.0);

        // Should give 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20
        Assert.True(levelSet.Levels.Length >= 5,
            $"Expected ≥5 levels at step=2, got {levelSet.Levels.Length}");
        for (int i = 0; i < levelSet.Levels.Length; i++)
        {
            double expected = i * 2.0;
            Assert.Equal(expected, levelSet.Levels[i], precision: 6);
        }
    }

    [Fact]
    public void LevelsByStep_Anchor_OffsetApplied()
    {
        // Grid 0..1; step=0.25; anchor=0.1 → first level at 0.1 (since 0.1 >= 0)
        var grid = MakeGrid(res: 11, lo: 0.0, hi: 1.0, f: (x, y) => x);
        var levelSet = ContourExtractor.LevelsByStep(grid, step: 0.25, anchor: 0.1);

        // First level should be 0.1 (closest to 0 from anchor=0.1 at step=0.25)
        Assert.True(Math.Abs(levelSet.Levels[0] - 0.1) < 1e-9,
            $"Expected first level 0.1, got {levelSet.Levels[0]:F6}");
    }

    [Fact]
    public void Extract_EmptyLevelSet_ReturnsEmpty()
    {
        var grid = MakeGrid(res: 11, lo: -1.0, hi: 1.0, f: (x, y) => x + y);
        var polylines = ContourExtractor.Extract(grid, new ContourLevelSet(Array.Empty<double>()));
        Assert.Empty(polylines);
    }

    [Fact]
    public void Extract_AllNanGrid_ReturnsEmpty()
    {
        double[] nan = new double[100];
        for (int i = 0; i < nan.Length; i++) nan[i] = double.NaN;
        double[] xs = new double[10];
        for (int i = 0; i < 10; i++) xs[i] = i * 0.1;
        var grid = new SurfaceGrid(xs, xs, nan);
        var levels = new ContourLevelSet(new[] { 0.5 });

        var polylines = ContourExtractor.Extract(grid, levels);
        Assert.Empty(polylines);
    }
}
