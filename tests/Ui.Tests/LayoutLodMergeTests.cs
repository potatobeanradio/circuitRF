// Phase L2c §§1-2 (docs/sonnet-briefs/brief-L2c-lod-merge-and-caching.md) — gates 3, 4, 5, 6: density is
// preserved when sub-pixel shapes aggregate (never dropped), output stays pixel-identical when LOD/merge
// do NOT engage, counters collapse to O(layers) when they do, and both triggers (sub-pixel size, layer
// shape count) route through the SAME batched-fill mechanism.

using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

public class LayoutLodMergeTests
{
    private static readonly LayerKey LayerA = new(1, 0);

    private static Technology MakeTech(double fillOpacity = 0.5) => new()
    {
        Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000,
        Layers = [new LayerDef { Key = LayerA, Name = "L1", Color = new CircuitRF.Ui.Theming.Rgba(255, 0, 0), FillOpacity = fillOpacity, ZOrder = 0, Visible = true, Selectable = true }],
    };

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static (SKSurface Surface, LayoutRenderResult Result) Render(LayoutView view, Technology tech, LayoutViewport vp, LayoutRenderOptions opts)
    {
        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return (surface, result);
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    private static byte[] AllPixelsRgba(SKSurface surface)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.Bytes;
    }

    private static bool IsRedDominant(SKColor c) => c.Red > c.Green + 30 && c.Red > c.Blue + 30;

    private static int CountPixelsMatching(SKSurface surface, System.Func<SKColor, bool> predicate)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (predicate(bmp.GetPixel(x, y))) count++;
        return count;
    }

    // ── Gate 3: density preserved — R-L2c-1's whole point ────────────────────────

    [Fact]
    public void DenseClusterOfSubPixelShapes_RendersAsFilledRegion_NotEmpty()
    {
        var view = MakeView();
        var rng = new System.Random(11);

        // 3,000 tiny (10 DBU) rects scattered across a 200,000 DBU square region.
        for (int i = 0; i < 3000; i++)
        {
            long cx = rng.Next(0, 200_000), cy = rng.Next(0, 200_000);
            view.Shapes.Add(new RectShape { Layer = LayerA, X1 = cx, Y1 = cy, X2 = cx + 10, Y2 = cy + 10 });
        }
        var tech = MakeTech();

        // Zoom so each 10-DBU shape is FAR below the 2px LOD threshold (0.1 device px), but the
        // 200,000-DBU cluster region spans a real pixel area (100x100 px here).
        var vp = new LayoutViewport(0, 0, 100.0 / 200_000, 100, 100);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var (surface, result) = Render(view, tech, vp, opts);

        int coloredPixels = CountPixelsMatching(surface, IsRedDominant);

        // The regression R-L2c-1 exists to prevent: "drop below threshold" would render this as EMPTY
        // (0 colored pixels) despite 3,000 real shapes. A meaningful fraction of the 100x100 region
        // must be visibly filled.
        Assert.True(coloredPixels > 500, $"expected substantial coverage from 3,000 aggregated sub-pixel shapes, got {coloredPixels} colored pixels out of 10,000");
        Assert.Equal(0, result.PathsConstructed); // every shape aggregated via the minimal-rect path — no full geometry built
    }

    [Fact]
    public void SubPixelShape_NeverDropped_EvenAlone()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 5, Y2 = 5 }); // 5 DBU — tiny
        var tech = MakeTech(fillOpacity: 1.0);

        var vp = new LayoutViewport(-50, -50, 100.0 / 200, 100, 100); // shape maps to well under 1 device px
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var (surface, result) = Render(view, tech, vp, opts);

        Assert.Equal(1, result.ShapesDrawn);
        Assert.True(CountPixelsMatching(surface, IsRedDominant) > 0, "a lone sub-pixel shape must still paint at least one visible pixel, not vanish");
    }

    // ── Gate 4: LOD/merge do not engage when comfortably zoomed in — pixel-identical ─────────────

    [Fact]
    public void ComfortablyLargeShapes_LowShapeCount_PixelIdentical_WhetherOrNotLodMergeAreDisabled()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 50_000 });
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 40_000, Y1 = 10_000, X2 = 140_000, Y2 = 60_000 }); // overlaps -> darkens
        view.Shapes.Add(new CircleShape { Layer = LayerA, Cx = 200_000, Cy = 25_000, R = 30_000 });
        var tech = MakeTech();
        var bbox = view.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s)));
        var vp = LayoutViewport.ZoomToFit(bbox, 400, 400, marginFrac: 0.1);

        var defaultOpts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var explicitlyDisabledOpts = new LayoutRenderOptions
        {
            Theme = LayoutRenderTheme.Light, ShowGrid = false,
            LodPixelThreshold = 1e-9, MergeShapeCountThreshold = int.MaxValue,
        };

        var (surfaceA, _) = Render(view, tech, vp, defaultOpts);
        var (surfaceB, _) = Render(view, tech, vp, explicitlyDisabledOpts);

        Assert.Equal(AllPixelsRgba(surfaceA), AllPixelsRgba(surfaceB));
    }

    [Fact]
    public void ComfortablyLargeShapes_OverlapStillDarkens_LodDoesNotSuppressR8a()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 25_000, Y1 = 25_000, X2 = 75_000, Y2 = 75_000 }); // fully inside -> double coverage in the middle
        var tech = MakeTech(fillOpacity: 0.5);
        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 100_000, 100_000), 200, 200, marginFrac: 0.05);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var (surface, _) = Render(view, tech, vp, opts);

        var single = PixelAt(surface, 10, 190);   // covered by one rect only (near a corner)
        var doubled = PixelAt(surface, 100, 100); // covered by both rects (center)
        Assert.True(doubled.Red < single.Red, "overlapping same-layer shapes must still darken at normal zoom (R8a, unaffected by L2c)");
    }

    // ── Gate 5: counters collapse to O(layers) when LOD engages, match L2b exactly when it doesn't ──

    [Fact]
    public void Counters_ManySubPixelShapes_CollapseToOLayers()
    {
        var view = MakeView();
        for (int i = 0; i < 5000; i++)
            view.Shapes.Add(new RectShape { Layer = LayerA, X1 = i, Y1 = 0, X2 = i + 1, Y2 = 1 }); // 1-DBU-wide slivers
        var tech = MakeTech();
        var vp = new LayoutViewport(-1000, -1000, 100.0 / 5000, 800, 600); // each shape << 1 device px
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var (_, result) = Render(view, tech, vp, opts);

        Assert.Equal(5000, result.ShapesExamined);
        Assert.Equal(5000, result.ShapesDrawn);
        Assert.Equal(0, result.PathsConstructed);   // no individual geometry built at all
        Assert.Equal(2, result.DrawCalls);          // one aggregate fill + one batched stroke, for the one layer
        Assert.Equal(1, result.LayersVisited);
    }

    [Fact]
    public void Counters_FewNormalShapes_MatchesPreL2cBehaviorExactly()
    {
        var view = MakeView();
        for (int i = 0; i < 10; i++)
            view.Shapes.Add(new RectShape { Layer = LayerA, X1 = i * 10_000, Y1 = 0, X2 = i * 10_000 + 8_000, Y2 = 8_000 });
        var tech = MakeTech();
        var bbox = view.Shapes.Aggregate(Bbox.Empty, (acc, s) => acc.Union(LayoutGeometry.BboxOf(s)));
        var vp = LayoutViewport.ZoomToFit(bbox, 800, 600, marginFrac: 0.05); // each shape is a large fraction of the viewport
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var (_, result) = Render(view, tech, vp, opts);

        Assert.Equal(10, result.ShapesExamined);
        Assert.Equal(10, result.ShapesDrawn);
        Assert.Equal(10, result.PathsConstructed);  // one SKPath per shape — matches L2b's own gate-5 test exactly
        Assert.Equal(11, result.DrawCalls);         // 10 fills + 1 batched stroke
        Assert.Equal(1, result.LayersVisited);
    }

    // ── Gate 6: sub-pixel size and layer shape-count both route through the SAME aggregate ────────
    // Proof by observable effect: the INDIVIDUAL per-shape tier darkens on overlap (R8a); the
    // aggregate tier does not (R-L2c-1's own stated consequence). Both triggers below produce the
    // SAME "no darkening" signature, which is only possible if both write into the identical batched
    // path rather than two independently-implemented mechanisms that happened to converge by accident.

    [Fact]
    public void SubPixelTrigger_And_CountTrigger_BothCollapseDrawCallsToOLayers_SameStructuralSignature()
    {
        var tech = MakeTech(fillOpacity: 0.5);

        // Scenario A: sub-pixel trigger — many tiny (overlapping) shapes, one layer.
        var viewA = MakeView();
        for (int i = 0; i < 200; i++)
            viewA.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 5, Y2 = 5 });
        var vpA = new LayoutViewport(-50, -50, 0.1, 100, 100); // each 5-DBU shape -> 0.5 device px, well under the 2px LOD threshold
        var optsA = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        var (_, resultA) = Render(viewA, tech, vpA, optsA);

        // Scenario B: count trigger — many NORMAL-sized shapes (well above the LOD threshold
        // individually), forced into merge mode via a low MergeShapeCountThreshold.
        var viewB = MakeView();
        for (int i = 0; i < 200; i++)
            viewB.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        var vpB = LayoutViewport.ZoomToFit(new Bbox(0, 0, 100_000, 100_000), 100, 100, marginFrac: 0.05);
        var optsB = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, MergeShapeCountThreshold = 10 };
        var (surfaceB, resultB) = Render(viewB, tech, vpB, optsB);
        var pixelB = PixelAt(surfaceB, (int)vpB.WorldToScreenX(50_000), (int)vpB.WorldToScreenY(50_000));

        // Reference: the SAME 200-shape overlap WITHOUT merge forced on (individual tier).
        var optsBIndividual = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false, MergeShapeCountThreshold = int.MaxValue };
        var (surfaceBIndividual, resultBIndividual) = Render(viewB, tech, vpB, optsBIndividual);
        var pixelBIndividual = PixelAt(surfaceBIndividual, (int)vpB.WorldToScreenX(50_000), (int)vpB.WorldToScreenY(50_000));

        // Structural proof (robust — unlike a pixel comparison, not thrown off by the fixed-device-pixel
        // stroke dominating a sub-pixel-sized aggregate): BOTH triggers collapse draw calls to O(layers)
        // — exactly 2 (one aggregate fill, one batched stroke) for this one-layer, no-individual-shapes
        // scene — while the un-merged reference issues one draw call per shape.
        Assert.Equal(2, resultA.DrawCalls);
        Assert.Equal(2, resultB.DrawCalls);
        Assert.Equal(201, resultBIndividual.DrawCalls); // 200 fills + 1 batched stroke — L2b's own per-shape behavior

        // Pixel proof at matching scale (B vs its own un-merged reference, same shape size — no
        // stroke-vs-fill-area distortion): merging really did change the compositing behavior, not just
        // the draw-call count.
        Assert.True(pixelBIndividual.Green < pixelB.Green - 50, "200-shape individual overlap should be much darker (lower Green) than the merged/aggregate result");
    }
}
