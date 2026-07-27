// L2a acceptance gates 2-6 (docs/sonnet-briefs/brief-L2a-performance-harness.md §7) — fast, part of
// the default `dotnet test` run at every commit (no [Trait("Category","Nightly")] here; those live on
// the 500k-scale cases in LayoutPerformanceBaselineTests.cs per gate 7).

using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.Theming;
using SkiaSharp;

namespace CircuitRF.Ui.Tests.LayoutPerf;

// SkiaFonts.PlexRegular cannot load in this project's headless xunit tests (no live Avalonia app
// host — see src/Ui/CLAUDE.md's "SkiaFonts.PlexRegular cannot load..." note); the Mixed profile draws
// LabelShapes, so every test in this class routes label rendering through SKTypeface.Default via the
// same TestOverrideTypeface seam LayoutLabelFixAndTextFlattenTests.cs already established.
[Collection(CircuitRF.Ui.Tests.LayoutTextOutlineTypefaceCollection.Name)]
public class LayoutPerfHarnessGateTests : System.IDisposable
{
    public LayoutPerfHarnessGateTests() => LayoutTextOutline.TestOverrideTypeface = SKTypeface.Default;
    public void Dispose() => LayoutTextOutline.TestOverrideTypeface = null;

    // ── Gate 2: determinism (R-L2a-1) ────────────────────────────────────────────

    [Theory]
    [InlineData(GeneratorProfile.Manhattan)]
    [InlineData(GeneratorProfile.CurveHeavy)]
    [InlineData(GeneratorProfile.Mixed)]
    public void Determinism_SameSeedSameArgs_ByteIdenticalSerialization(GeneratorProfile profile)
    {
        var a = SyntheticLayoutGenerator.Generate(2000, 30, seed: 42, profile);
        var b = SyntheticLayoutGenerator.Generate(2000, 30, seed: 42, profile);
        Assert.Equal(LayoutPersistence.Serialize(a), LayoutPersistence.Serialize(b));
    }

    [Theory]
    [InlineData(GeneratorProfile.Manhattan)]
    [InlineData(GeneratorProfile.CurveHeavy)]
    [InlineData(GeneratorProfile.Mixed)]
    public void Determinism_DifferentSeed_ProducesDifferentSerialization(GeneratorProfile profile)
    {
        // Guards against a degenerate generator that ignores its seed entirely and would otherwise
        // make the "same seed -> same output" gate above vacuously true.
        var a = SyntheticLayoutGenerator.Generate(2000, 30, seed: 1, profile);
        var b = SyntheticLayoutGenerator.Generate(2000, 30, seed: 2, profile);
        Assert.NotEqual(LayoutPersistence.Serialize(a), LayoutPersistence.Serialize(b));
    }

    [Theory]
    [InlineData(GeneratorProfile.Manhattan)]
    [InlineData(GeneratorProfile.CurveHeavy)]
    [InlineData(GeneratorProfile.Mixed)]
    public void Determinism_SurvivesSerializeReloadRoundTrip(GeneratorProfile profile)
    {
        var view = SyntheticLayoutGenerator.Generate(1500, 25, seed: 7, profile);
        var json1 = LayoutPersistence.Serialize(view);
        var reloaded = LayoutPersistence.Deserialize(json1);
        var json2 = LayoutPersistence.Serialize(reloaded);
        Assert.Equal(json1, json2);
    }

    // ── Gate 3: clustered distribution (R-L2a-2) ─────────────────────────────────

    [Theory]
    [InlineData(GeneratorProfile.Manhattan)]
    [InlineData(GeneratorProfile.CurveHeavy)]
    [InlineData(GeneratorProfile.Mixed)]
    public void Distribution_IsClustered_NotUniform(GeneratorProfile profile)
    {
        const int shapeCount = 6000;
        const long extentHalf = 50_000_000; // must match SyntheticLayoutGenerator's own ExtentHalf
        const int grid = 10; // each cell = exactly 1% of the extent's area

        var view = SyntheticLayoutGenerator.Generate(shapeCount, 20, seed: 123, profile);

        long cell = 2 * extentHalf / grid;
        var counts = new int[grid, grid];
        foreach (var shape in view.Shapes)
        {
            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;
            long cxw = (bb.MinX + bb.MaxX) / 2;
            long cyw = (bb.MinY + bb.MaxY) / 2;
            int ix = (int)System.Math.Clamp((cxw + extentHalf) / cell, 0, grid - 1);
            int iy = (int)System.Math.Clamp((cyw + extentHalf) / cell, 0, grid - 1);
            counts[ix, iy]++;
        }

        int max = 0, min = int.MaxValue;
        foreach (var c in counts) { if (c > max) max = c; if (c < min) min = c; }

        // "A viewport covering 1% of the extent" is exactly one grid cell here; a uniform generator
        // would put ~1% of shapes (shapeCount/100) in every cell. A clustered one puts far more than
        // that in the cell holding a dense cluster, and far fewer (often zero) in an empty stretch.
        double uniformSharePerCell = shapeCount / (double)(grid * grid);
        Assert.True(max > uniformSharePerCell * 5,
            $"densest 1%-of-extent cell has {max} shapes, expected far more than the uniform share of {uniformSharePerCell:F1} — generator reads as uniform, not clustered");
        Assert.True(min < uniformSharePerCell * 0.5,
            $"sparsest 1%-of-extent cell has {min} shapes, expected far fewer than the uniform share of {uniformSharePerCell:F1} — generator has no real empty stretches");
    }

    // ── Gate 4: every profile generates and renders at every size ───────────────
    // (500k-scale cases live in LayoutPerformanceBaselineTests.cs under the Nightly trait — this file
    // stays fast enough to run on every commit, per gate 7.)

    [Theory]
    [InlineData(GeneratorProfile.Manhattan, 1_000)]
    [InlineData(GeneratorProfile.Manhattan, 50_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 1_000)]
    [InlineData(GeneratorProfile.CurveHeavy, 50_000)]
    [InlineData(GeneratorProfile.Mixed, 1_000)]
    [InlineData(GeneratorProfile.Mixed, 50_000)]
    public void Generation_EveryProfile_GeneratesAndRendersWithoutException(GeneratorProfile profile, int shapeCount)
    {
        var view = SyntheticLayoutGenerator.Generate(shapeCount, 200, seed: 99, profile);
        var tech = SyntheticLayoutGenerator.GenerateTechnology(200);
        Assert.Equal(shapeCount, view.Shapes.Count);

        if (profile == GeneratorProfile.Mixed)
        {
            Assert.Contains(view.Shapes, s => s is PolygonShape { Holes.Count: > 0 });
            Assert.Contains(view.Shapes, s => s is BitmapShape);
            Assert.Contains(view.Shapes, s => s is LabelShape);
        }

        var bbox = view.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s)));
        var vp = LayoutViewport.ZoomToFit(bbox, 800, 600);

        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = true };
        var ex = Record.Exception(() => LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts));
        Assert.Null(ex);
    }

    // ── Gate 5: counters are correct ─────────────────────────────────────────────

    [Fact]
    public void Counters_HandBuiltTenShapeLayout_ExaminedAndDrawnMatchExpected()
    {
        // 10 shapes on one visible/selectable layer; 3 of them (indices 7,8,9) sit far outside the
        // viewport passed to Draw. L2a shipped no viewport culling (that was L2b's R-tree) — this test
        // originally pinned "examined == drawn == 10" as the then-CURRENT no-culling behavior, with a
        // comment saying it should become 7/7 once culling landed. It has: L2b's spatial index now
        // culls the 3 off-screen shapes before they are even examined.
        var layer = new LayerKey(1, 0);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        for (int i = 0; i < 10; i++)
        {
            long x = i < 7 ? i * 1000 : 10_000_000 + i * 1000; // last 3 far off in +X
            view.Shapes.Add(new RectShape { Layer = layer, X1 = x, Y1 = 0, X2 = x + 500, Y2 = 500 });
        }

        var tech = new Technology
        {
            Layers = [new LayerDef { Key = layer, Color = new Rgba(255, 0, 0), Visible = true, Selectable = true, FillOpacity = 0.5 }],
        };

        // Viewport frames only the first 7 shapes' region.
        var vp = new LayoutViewport(-1000, -1000, 0.05, 400, 400);

        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.Equal(7, result.ShapesExamined);     // L2b: the 3 off-screen shapes never reach the candidate set
        Assert.Equal(7, result.ShapesDrawn);
        Assert.Equal(7, result.PathsConstructed);   // one SKPath per visible Rect
        Assert.Equal(8, result.DrawCalls);          // 7 fills + 1 batched layer stroke
        Assert.Equal(1, result.LayersVisited);
    }

    [Fact]
    public void Counters_EmptyLayout_AllZero()
    {
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        var vp = new LayoutViewport(0, 0, 1.0, 400, 400);
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var result = LayoutRenderer.Draw(surface.Canvas, view, null, vp, opts);

        Assert.Equal(0, result.ShapesExamined);
        Assert.Equal(0, result.ShapesDrawn);
        Assert.Equal(0, result.PathsConstructed);
        Assert.Equal(0, result.DrawCalls);
        Assert.Equal(0, result.LayersVisited);
    }

    [Fact]
    public void Counters_HiddenLayer_ExaminedButNotDrawn()
    {
        var layer = new LayerKey(2, 0);
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        view.Shapes.Add(new RectShape { Layer = layer, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        var tech = new Technology
        {
            Layers = [new LayerDef { Key = layer, Color = new Rgba(0, 255, 0), Visible = false, Selectable = true }],
        };
        var vp = new LayoutViewport(-1000, -1000, 0.1, 400, 400);
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        Assert.Equal(1, result.ShapesExamined);
        Assert.Equal(0, result.ShapesDrawn);
        Assert.Equal(0, result.LayersVisited);
        Assert.Equal(0, result.DrawCalls);
    }

    // ── Gate 6: counters cost nothing (R-L2a-3) ─────────────────────────────────
    // No conditional compilation exists to build a "counters stubbed out" variant to diff against
    // (the brief explicitly forbids adding one) — so this is a structural proof instead: every counter
    // is a plain `int` field on a class with no dictionaries, no strings, no logging, incremented with
    // `x++` at the call site (see LayoutRenderer.cs). A reflection check that the type genuinely has
    // only cheap value-type fields is the closest thing to a permanent regression gate for "stays
    // cheap" available without a second build configuration.

    [Fact]
    public void Counters_FrameCountersType_IsOnlyPlainIntFields()
    {
        var fields = typeof(LayoutFrameCounters).GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotEmpty(fields);
        foreach (var f in fields)
            Assert.Equal(typeof(int), f.FieldType);
    }
}
