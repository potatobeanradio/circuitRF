// Phase L2c (docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md) — gates 2, 9, 10. Measures LOD
// ALONE first (merge/cache disabled), per the brief's explicit ordering ("gate this before building
// anything else... re-run the harness after LOD alone"), then the FINAL numbers with all three items
// engaged, plus the path cache's memory footprint. Reuses L2a's SyntheticLayoutGenerator/BenchmarkHarness
// so every number here is directly comparable to the L2a/L2b baseline tables.
//
// The 500k cases in this file are all TIMED sweeps — [Trait("Category","Benchmark")], opt-in only
// (brief-benchmark-gate-split.md, superseding the old Nightly tag here). Run explicitly:
// dotnet test --filter "Category=Benchmark". The 50k cases (LodOnly_FullExtent_50k/Final_FullExtent_50k)
// stay in the routine gate and still exercise every LOD/merge/cache code path at scale.

using System;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.LayoutPerf;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public class LayoutLodMergeCacheBenchmarkTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    public LayoutLodMergeCacheBenchmarkTests(ITestOutputHelper output)
    {
        _out = output;
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }
    public void Dispose() => LayoutTextOutline.TestOverrideTypeface = null;

    private static (LayoutView View, Technology Tech, LayoutViewport Vp) BuildFullExtent(GeneratorProfile profile, int shapeCount)
    {
        var view = SyntheticLayoutGenerator.Generate(shapeCount, 200, seed: 2027, profile);
        var tech = SyntheticLayoutGenerator.GenerateTechnology(200);
        var bbox = view.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s)));
        var vp = LayoutViewport.ZoomToFit(bbox, 1000, 700);
        return (view, tech, vp);
    }

    private static (int Warmup, int Iterations) ConfigFor(int shapeCount) => shapeCount <= 50_000 ? (2, 4) : (1, 2);

    // ── Gate 2: LOD alone (merge/cache both disabled) ────────────────────────────

    private void RunLodOnly(GeneratorProfile profile, int shapeCount)
    {
        var (view, tech, vp) = BuildFullExtent(profile, shapeCount);
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            LodPixelThreshold = LayoutRenderer.DefaultLodPixelThreshold,
            MergeShapeCountThreshold = int.MaxValue, // disable the count trigger — LOD's own contribution only
            PathCache = null,
        };
        using var surface = SKSurface.Create(new SKImageInfo(1000, 700));
        var counters = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        var (warmup, iterations) = ConfigFor(shapeCount);
        var timing = BenchmarkHarness.Measure(warmup, iterations, () => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
        _out.WriteLine($"[LOD-only]  {profile} @ {shapeCount:N0}: {timing}  " +
                        $"examined={counters.ShapesExamined:N0} drawn={counters.ShapesDrawn:N0} paths={counters.PathsConstructed:N0} drawCalls={counters.DrawCalls:N0}");
    }

    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 50_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 50_000)]
    [InlineData(GeneratorProfile.Mixed, 50_000)]
    public void LodOnly_FullExtent_50k(GeneratorProfile profile, int shapeCount) => RunLodOnly(profile, shapeCount);

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 500_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 500_000)]
    [InlineData(GeneratorProfile.Mixed, 500_000)]
    public void LodOnly_FullExtent_500k(GeneratorProfile profile, int shapeCount) => RunLodOnly(profile, shapeCount);

    // ── Gate 10: final numbers, all three items engaged ──────────────────────────

    private void RunFinal(GeneratorProfile profile, int shapeCount)
    {
        var (view, tech, vp) = BuildFullExtent(profile, shapeCount);
        var cache = new LayoutPathCache(capacity: shapeCount);
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            LodPixelThreshold = LayoutRenderer.DefaultLodPixelThreshold,
            MergeShapeCountThreshold = LayoutRenderer.DefaultMergeShapeCountThreshold,
            PathCache = cache,
        };
        using var surface = SKSurface.Create(new SKImageInfo(1000, 700));
        var counters = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts); // warms the cache
        var (warmup, iterations) = ConfigFor(shapeCount);
        var timing = BenchmarkHarness.Measure(warmup, iterations, () => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
        _out.WriteLine($"[Final]     {profile} @ {shapeCount:N0}: {timing}  " +
                        $"examined={counters.ShapesExamined:N0} drawn={counters.ShapesDrawn:N0} paths={counters.PathsConstructed:N0} drawCalls={counters.DrawCalls:N0}  " +
                        $"cache: entries={cache.Count:N0} hit={cache.HitCount:N0} miss={cache.MissCount:N0}");
    }

    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 50_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 50_000)]
    [InlineData(GeneratorProfile.Mixed, 50_000)]
    public void Final_FullExtent_50k(GeneratorProfile profile, int shapeCount) => RunFinal(profile, shapeCount);

    [Trait("Category", "Benchmark")]
    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 500_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 500_000)]
    [InlineData(GeneratorProfile.Mixed, 500_000)]
    public void Final_FullExtent_500k(GeneratorProfile profile, int shapeCount) => RunFinal(profile, shapeCount);

    // ── Gate 9: path cache memory, at 500k ────────────────────────────────────────

    private const long ExtentHalf = 50_000_000; // must match SyntheticLayoutGenerator's own ExtentHalf

    /// <summary>Same 1%-of-extent grid histogram L2a/L2b's own benchmark helpers use — finds a
    /// genuinely dense cluster so a "zoomed in" viewport actually contains many shapes to cache,
    /// instead of landing in one of the mostly-empty background stretches.</summary>
    private static (long X, long Y) FindDensePoint(LayoutView view)
    {
        const int grid = 20;
        long cell = 2 * ExtentHalf / grid;
        var counts = new int[grid, grid];
        foreach (var shape in view.Shapes)
        {
            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;
            long cx = (bb.MinX + bb.MaxX) / 2, cy = (bb.MinY + bb.MaxY) / 2;
            int ix = (int)Math.Clamp((cx + ExtentHalf) / cell, 0, grid - 1);
            int iy = (int)Math.Clamp((cy + ExtentHalf) / cell, 0, grid - 1);
            counts[ix, iy]++;
        }
        int bestIx = 0, bestIy = 0, best = -1;
        for (int ix = 0; ix < grid; ix++)
        for (int iy = 0; iy < grid; iy++)
            if (counts[ix, iy] > best) { best = counts[ix, iy]; bestIx = ix; bestIy = iy; }
        return (-ExtentHalf + bestIx * cell + cell / 2, -ExtentHalf + bestIy * cell + cell / 2);
    }

    [Trait("Category", "Benchmark")]
    [Fact]
    public void PathCache_500k_MemoryStaysUnderCap_TimeAndMemoryReported()
    {
        var view = SyntheticLayoutGenerator.Generate(500_000, 200, seed: 2028, GeneratorProfile.CurveHeavy); // the most path-heavy profile
        var tech = SyntheticLayoutGenerator.GenerateTechnology(200);
        var (denseX, denseY) = FindDensePoint(view);

        int capacity = 500_000;
        var cache = new LayoutPathCache(capacity);
        var opts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            LodPixelThreshold = LayoutRenderer.DefaultLodPixelThreshold,
            MergeShapeCountThreshold = LayoutRenderer.DefaultMergeShapeCountThreshold,
            PathCache = cache,
        };
        using var surface = SKSurface.Create(new SKImageInfo(1000, 700));

        // Zoomed into a dense cluster at a level where a 20-micron (the generator's largest) shape
        // renders comfortably above the LOD threshold (~4px), so most candidates land in the cached
        // individual tier rather than being LOD-aggregated.
        const double zoom = 0.0002; // device px per DBU
        double spanX = 1000 / zoom, spanY = 700 / zoom;
        var vp = new LayoutViewport(denseX - spanX / 2, denseY - spanY / 2, zoom, 1000, 700);

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        proc.Refresh();
        long memBefore = proc.WorkingSet64;

        var timing = BenchmarkHarness.Measure(1, 3, () => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));

        proc.Refresh();
        long memAfter = proc.WorkingSet64;
        long deltaMb = (memAfter - memBefore) / (1024 * 1024);

        _out.WriteLine($"[Cache memory] CurveHeavy @ 500,000, zoomed into a dense cluster: {timing}  " +
                        $"cache entries={cache.Count:N0}/{capacity:N0} evictions={cache.EvictionCount:N0} hit={cache.HitCount:N0} miss={cache.MissCount:N0} " +
                        $"processWorkingSetDelta≈{deltaMb:N0} MB" +
                        (cache.Count > 0 ? $" (~{(deltaMb * 1024.0 * 1024.0 / cache.Count):F0} bytes/cached-entry, includes native Skia buffers)" : ""));

        Assert.True(cache.Count <= capacity, "cache must never exceed its configured capacity");
    }
}
