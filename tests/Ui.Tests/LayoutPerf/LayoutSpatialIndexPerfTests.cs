// Gates 2, 3, 10 (docs/sonnet-briefs/brief-L2b-spatial-index.md §6) — culling actually reduces work at
// scale (counter assertion, not wall-clock), full-extent stays unchanged, and a clustered-distribution
// query returns O(visible) candidates rather than a large fraction of the tree. Reuses L2a's
// SyntheticLayoutGenerator so these numbers are directly comparable to the L2a baseline table.

using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.LayoutPerf;

[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public class LayoutSpatialIndexPerfTests : System.IDisposable
{
    private readonly ITestOutputHelper _out;
    public LayoutSpatialIndexPerfTests(ITestOutputHelper output)
    {
        _out = output;
        LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    }
    public void Dispose() => LayoutTextOutline.TestOverrideTypeface = null;

    private const long ExtentHalf = 50_000_000; // must match SyntheticLayoutGenerator's own ExtentHalf

    /// <summary>Same 1%-of-extent grid histogram the L2a distribution gate and baseline harness use —
    /// finds a genuinely dense cluster to zoom into, rather than an arbitrary (likely near-empty) point.</summary>
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
            int ix = (int)System.Math.Clamp((cx + ExtentHalf) / cell, 0, grid - 1);
            int iy = (int)System.Math.Clamp((cy + ExtentHalf) / cell, 0, grid - 1);
            counts[ix, iy]++;
        }
        int bestIx = 0, bestIy = 0, best = -1;
        for (int ix = 0; ix < grid; ix++)
        for (int iy = 0; iy < grid; iy++)
            if (counts[ix, iy] > best) { best = counts[ix, iy]; bestIx = ix; bestIy = iy; }
        return (-ExtentHalf + bestIx * cell + cell / 2, -ExtentHalf + bestIy * cell + cell / 2);
    }

    private void AssertZoomedCullingIsEffective(GeneratorProfile profile, int shapeCount)
    {
        var view = SyntheticLayoutGenerator.Generate(shapeCount, 200, seed: 555, profile);
        var tech = SyntheticLayoutGenerator.GenerateTechnology(200);
        var (denseX, denseY) = FindDensePoint(view);

        // ~1% of the extent's width/height, centered on a dense cluster.
        double zoomedSpan = 2 * ExtentHalf * 0.01;
        double zoom = 800.0 / zoomedSpan;
        var vp = new LayoutViewport(denseX - zoomedSpan / 2, denseY - zoomedSpan / 2, zoom, 800, 600);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        _out.WriteLine($"{profile} @ {shapeCount:N0}: zoomed-1% ShapesExamined={result.ShapesExamined:N0} " +
                        $"({100.0 * result.ShapesExamined / shapeCount:F3}% of total), ShapesDrawn={result.ShapesDrawn:N0}");

        // Gate 2: O(visible), not the total — a generous 10% ceiling (the actual clustered fraction is
        // far smaller; see the L2b completion note for the measured number).
        Assert.True(result.ShapesExamined < shapeCount / 10,
            $"zoomed-in ShapesExamined={result.ShapesExamined} should be far below total={shapeCount}");
        // Every layer in this fixture is Visible/Selectable, so every examined candidate is also drawn.
        Assert.Equal(result.ShapesExamined, result.ShapesDrawn);
    }

    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 50_000)]
    [InlineData(GeneratorProfile.Mixed, 50_000)]
    public void ZoomedViewport_50k_ShapesExaminedFarBelowTotal(GeneratorProfile profile, int shapeCount) =>
        AssertZoomedCullingIsEffective(profile, shapeCount);

    [Trait("Category", "Nightly")]
    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 500_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 500_000)]
    [InlineData(GeneratorProfile.Mixed, 500_000)]
    public void ZoomedViewport_500k_ShapesExaminedFarBelowTotal(GeneratorProfile profile, int shapeCount) =>
        AssertZoomedCullingIsEffective(profile, shapeCount);

    // ── Gate 3: full extent is unchanged — ShapesDrawn is still ~total ───────────

    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 50_000)]
    [InlineData(GeneratorProfile.Mixed, 50_000)]
    public void FullExtentViewport_ShapesDrawnEqualsTotal(GeneratorProfile profile, int shapeCount)
    {
        var view = SyntheticLayoutGenerator.Generate(shapeCount, 200, seed: 777, profile);
        var tech = SyntheticLayoutGenerator.GenerateTechnology(200);
        var bbox = view.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s)));
        var vp = LayoutViewport.ZoomToFit(bbox, 800, 600);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.Equal(shapeCount, result.ShapesExamined);
        Assert.Equal(shapeCount, result.ShapesDrawn);
    }

    // ── Gate 10: STR bulk-load build time at 500k, recorded (not asserted tightly — R-L2a-3 stands) ──

    [Trait("Category", "Nightly")]
    [Fact]
    public void BulkLoad_500k_BuildTimeRecorded()
    {
        var view = SyntheticLayoutGenerator.Generate(500_000, 200, seed: 999, GeneratorProfile.Mixed);
        var timing = BenchmarkHarness.Measure(1, 3, () =>
        {
            var idx = new LayoutSpatialIndex();
            idx.Apply(view.Shapes, LayoutChangeInfo.Full);
        });
        _out.WriteLine($"STR bulk-load, 500,000 shapes (Mixed): {timing}");
        // No hard ceiling here beyond a generous catastrophe backstop — this exists to RECORD the
        // number for the completion note (R-L2a-7 convention), same as L2a's own benchmark tests.
        Assert.True(timing.P95Ms < 10_000, $"STR bulk-load for 500k took {timing.P95Ms:F0}ms p95 — investigate before recording as the baseline");
    }
}
