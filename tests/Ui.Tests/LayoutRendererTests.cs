using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── Phase L1a gates: LayoutRenderer pixel/behavioral oracles ─────────────────
// docs/sonnet-briefs/brief-L1a-layout-canvas.md "Gate (acceptance)" items 2,3,4,9.

public class LayoutRendererTests
{
    private static readonly LayerKey LayerA = new(1, 0);
    private static readonly LayerKey LayerB = new(2, 0);

    private static Technology MakeTech(params (LayerKey Key, SKColor Color, double FillOpacity)[] layers)
    {
        var tech = new Technology { Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000 };
        int z = 0;
        foreach (var (key, color, fillOpacity) in layers)
        {
            tech.Layers.Add(new LayerDef
            {
                Key = key,
                Name = $"L{key.Layer}",
                Color = new CircuitRF.Ui.Theming.Rgba(color.Red, color.Green, color.Blue),
                FillOpacity = fillOpacity,
                ZOrder = z++,
                Visible = true,
                Selectable = true,
            });
        }
        return tech;
    }

    private static LayoutView MakeView(int dbuPerMicron = 1000) => new()
    {
        DbuPerMicron = dbuPerMicron,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
        AngleMode    = AngleMode.AnyAngle,
    };

    private static LayoutViewport FitViewport(LayoutView view, int width = 400, int height = 400, double marginFrac = 0.2)
    {
        var bb = Bbox.Empty;
        foreach (var s in view.Shapes) bb = bb.Union(LayoutGeometry.BboxOf(s));
        return LayoutViewport.ZoomToFit(bb, width, height, marginFrac);
    }

    private static (SKSurface Surface, LayoutRenderResult Result) Render(
        LayoutView view, Technology? tech, LayoutViewport vp, bool showGrid = false)
    {
        var surface = SKSurface.Create(new SKImageInfo((int)vp.Width, (int)vp.Height));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = showGrid };
        var result = LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);
        return (surface, result);
    }

    private static SKColor PixelAt(SKSurface surface, int x, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        return bmp.GetPixel(x, y);
    }

    /// <summary>Relative-dominance red test — robust against a near-white background (whose R/G/B
    /// are all close together) and against partial edge-antialiasing blending, unlike an absolute
    /// "Red &gt; N" threshold which the light-theme background (R≈246) would also satisfy.</summary>
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

    // ── Gate 2: everything draws ──────────────────────────────────────────────

    [Fact]
    public void EveryShapeType_RendersInItsLayerColor()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 50_000 });
        view.Shapes.Add(new PolygonShape { Layer = LayerA, Xy = [0, 60_000, 50_000, 60_000, 25_000, 100_000] });
        view.Shapes.Add(new CircleShape { Layer = LayerB, Cx = 200_000, Cy = 25_000, R = 20_000 });
        view.Shapes.Add(new RoundedRectShape { Layer = LayerB, X1 = 250_000, Y1 = 0, X2 = 320_000, Y2 = 50_000, CornerRadius = 8_000 });
        view.Shapes.Add(new CurveShape
        {
            Layer = LayerA,
            Xy = [0, -100_000, 60_000, -100_000, 60_000, -40_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        });
        view.Shapes.Add(new PathShape
        {
            Layer = LayerB,
            Xy = [100_000, -150_000, 200_000, -150_000, 200_000, -90_000],
            Width = 10_000,
            End = PathEndStyle.Round,
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.3 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        });

        var tech = MakeTech((LayerA, SKColors.Red, 0.5), (LayerB, SKColors.Blue, 0.5));
        var vp = FitViewport(view, 600, 600);
        var (surface, result) = Render(view, tech, vp);

        Assert.Empty(result.UnknownLayers);

        int redPixels = CountPixelsMatching(surface, c => c.Red > 100 && c.Green < 100 && c.Blue < 100);
        int bluePixels = CountPixelsMatching(surface, c => c.Blue > 100 && c.Red < 100 && c.Green < 100);
        Assert.True(redPixels > 50, $"expected red (LayerA) pixels, got {redPixels}");
        Assert.True(bluePixels > 50, $"expected blue (LayerB) pixels, got {bluePixels}");

        surface.Dispose();
    }

    // ── Gate 3a: overlap darkens (R8a) ────────────────────────────────────────

    [Fact]
    public void OverlappingSameLayerShapes_CompositeDarkerThanSingleCoverage()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 50_000, Y1 = 0, X2 = 150_000, Y2 = 100_000 });

        var tech = MakeTech((LayerA, SKColors.Red, 0.5));
        var bb = new Bbox(0, 0, 150_000, 100_000);
        var vp = LayoutViewport.ZoomToFit(bb, 300, 200, marginFrac: 0.1);
        var (surface, _) = Render(view, tech, vp);

        // Single-coverage sample (left rect only) vs overlap sample (both rects).
        var single = PixelAt(surface, (int)vp.WorldToScreenX(25_000), (int)vp.WorldToScreenY(50_000));
        var overlap = PixelAt(surface, (int)vp.WorldToScreenX(75_000), (int)vp.WorldToScreenY(50_000));

        double singleLum = 0.299 * single.Red + 0.587 * single.Green + 0.114 * single.Blue;
        double overlapLum = 0.299 * overlap.Red + 0.587 * overlap.Green + 0.114 * overlap.Blue;
        Assert.True(overlapLum < singleLum, $"overlap luminance {overlapLum} should be < single-coverage luminance {singleLum}");

        surface.Dispose();
    }

    // ── Gate 3b: curves are curves ────────────────────────────────────────────

    [Fact]
    public void Circle_FilledPixelCount_MatchesAreaWithin2Percent()
    {
        var view = MakeView();
        const long radiusDbu = 100_000;
        view.Shapes.Add(new CircleShape { Layer = LayerA, Cx = 0, Cy = 0, R = radiusDbu });

        var tech = MakeTech((LayerA, SKColors.Red, 1.0));
        var bb = new Bbox(-radiusDbu, -radiusDbu, radiusDbu, radiusDbu);
        int size = 400;
        var vp = LayoutViewport.ZoomToFit(bb, size, size, marginFrac: 0.05);
        var (surface, _) = Render(view, tech, vp);

        int filled = CountPixelsMatching(surface, IsRedDominant);
        double radiusPx = radiusDbu * vp.Zoom;
        double expected = System.Math.PI * radiusPx * radiusPx;

        double pct = System.Math.Abs(filled - expected) / expected;
        Assert.True(pct < 0.02, $"filled={filled} expected~{expected:F0} pct={pct:P2}");

        surface.Dispose();
    }

    // ── Gate 3c: hairlines stay hairline ──────────────────────────────────────

    [Fact]
    public void OutlineStroke_SamePixelThickness_At1xAnd100xZoom()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 1_000_000, Y2 = 1_000_000 });
        var tech = MakeTech((LayerA, SKColors.Red, 0.0)); // zero fill opacity isolates the stroke

        // Low zoom: whole rect visible.
        var vpLow = new LayoutViewport(-200_000, -200_000, 0.0003, 600, 600);
        var (surfLow, _) = Render(view, tech, vpLow);
        int thicknessLow = MeasureVerticalStrokeThicknessPx(surfLow, (int)vpLow.WorldToScreenX(0), 300);

        // High zoom: zoomed in on the left edge (x=0) so it's still on-screen.
        var vpHigh = new LayoutViewport(-1_000, 400_000, 0.03, 600, 600);
        var (surfHigh, _) = Render(view, tech, vpHigh);
        int thicknessHigh = MeasureVerticalStrokeThicknessPx(surfHigh, (int)vpHigh.WorldToScreenX(0), 300);

        Assert.True(thicknessLow > 0, "expected a visible stroke at low zoom");
        Assert.True(thicknessHigh > 0, "expected a visible stroke at high zoom");
        Assert.Equal(thicknessLow, thicknessHigh);

        surfLow.Dispose();
        surfHigh.Dispose();
    }

    private static int MeasureVerticalStrokeThicknessPx(SKSurface surface, int aroundX, int y)
    {
        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);
        int count = 0;
        for (int x = System.Math.Max(0, aroundX - 10); x < System.Math.Min(bmp.Width, aroundX + 10); x++)
        {
            var c = bmp.GetPixel(x, y);
            if (IsRedDominant(c)) count++;
        }
        return count;
    }

    // ── Gate 3d: fill opacity ─────────────────────────────────────────────────

    [Fact]
    public void FillOpacity_MatchesLayerDef_AgainstKnownBackground()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 200_000, Y2 = 200_000 });
        var tech = MakeTech((LayerA, new SKColor(255, 0, 0), 0.4));

        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 200_000, 200_000), 200, 200, marginFrac: 0.0);
        var (surface, _) = Render(view, tech, vp);

        var bg = LayoutRenderTheme.Light.Background;
        var px = PixelAt(surface, 100, 100);

        byte expectedR = (byte)System.Math.Round(255 * 0.4 + bg.Red * 0.6);
        Assert.True(System.Math.Abs(px.Red - expectedR) <= 2, $"got R={px.Red}, expected~{expectedR}");

        surface.Dispose();
    }

    // ── Gate 4: fallback palette + gap-fill + once-per-layer warning ─────────

    [Fact]
    public void NoTechnology_RendersEveryLayerFromFallbackPalette_NoWarnings()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });

        var vp = FitViewport(view, 200, 200);
        var (surface, result) = Render(view, tech: null, vp);

        Assert.Empty(result.UnknownLayers);
        var expected = FallbackPalette.For(LayerA);
        var px = PixelAt(surface, 100, 100);
        Assert.True(System.Math.Abs(px.Red - expected.Color.R) < 40, "fallback color should dominate the pixel");

        surface.Dispose();
    }

    [Fact]
    public void TechMissingOneLayer_GapFillsOnlyThatLayer_WarnsOncePerLayer()
    {
        var view = MakeView();
        for (int i = 0; i < 10; i++)
            view.Shapes.Add(new RectShape { Layer = LayerB, X1 = i * 5_000, Y1 = 0, X2 = i * 5_000 + 4_000, Y2 = 5_000 });
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 10_000, X2 = 20_000, Y2 = 20_000 });

        var tech = MakeTech((LayerA, SKColors.Red, 0.5)); // LayerB is NOT defined -> gap
        var vp = FitViewport(view, 300, 300);
        var (surface, result) = Render(view, tech, vp);

        Assert.Single(result.UnknownLayers);
        Assert.Equal(LayerB, result.UnknownLayers[0]);

        surface.Dispose();
    }

    // ── Arc handedness — a closed Curve built from 4 quarter-arcs must fill like a circle ─────
    // (regression guard for the path-space Y-flip / Skia-degrees conversion in AppendEdge; a sign
    // error here would still "draw something" but the wrong shape, escaping the softer gate-2 check).

    [Fact]
    public void ClosedCurve_OfFourQuarterArcs_FillsLikeACircle()
    {
        var view = MakeView();
        const long r = 100_000;
        const double quarterBulge = 0.4142135623730951; // tan(pi/8) — standard 90 degree arc bulge

        // CCW ordering around the origin: (r,0) -> (0,r) -> (-r,0) -> (0,-r) -> close.
        view.Shapes.Add(new CurveShape
        {
            Layer = LayerA,
            Xy = [r, 0, 0, r, -r, 0, 0, -r],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = quarterBulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = quarterBulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = quarterBulge },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = quarterBulge },
            ],
        });

        var tech = MakeTech((LayerA, SKColors.Red, 1.0));
        var bb = new Bbox(-r, -r, r, r);
        int size = 400;
        var vp = LayoutViewport.ZoomToFit(bb, size, size, marginFrac: 0.05);
        var (surface, _) = Render(view, tech, vp);

        int filled = CountPixelsMatching(surface, IsRedDominant);
        double radiusPx = r * vp.Zoom;
        double expectedCircleArea = System.Math.PI * radiusPx * radiusPx;
        double expectedSquareArea = (2 * radiusPx) * (2 * radiusPx); // what a handedness bug would produce (arcs bow outward into a near-square)

        double pctVsCircle = System.Math.Abs(filled - expectedCircleArea) / expectedCircleArea;
        Assert.True(pctVsCircle < 0.03,
            $"filled={filled} expected-circle~{expectedCircleArea:F0} expected-square~{expectedSquareArea:F0} pct={pctVsCircle:P2}");

        surface.Dispose();
    }

    // ── Gate 9: large-coordinate fidelity (R-L1a-1) ──────────────────────────

    [Fact]
    public void SmallFeature_AtLargeCoordinate_RendersCorrectSize_NoQuantization()
    {
        var view = MakeView();
        const long farOut = 300_000_000; // ~3e8 DBU, per the brief's example
        const long featureSize = 2_000;  // 2 um square

        view.Shapes.Add(new RectShape
        {
            Layer = LayerA, X1 = farOut, Y1 = farOut, X2 = farOut + featureSize, Y2 = farOut + featureSize,
        });

        var tech = MakeTech((LayerA, SKColors.Red, 1.0));
        var bb = new Bbox(farOut, farOut, farOut + featureSize, farOut + featureSize);
        int size = 400;
        var vp = LayoutViewport.ZoomToFit(bb, size, size, marginFrac: 0.1);
        var (surface, _) = Render(view, tech, vp);

        int filled = CountPixelsMatching(surface, IsRedDominant);
        double sidePx = featureSize * vp.Zoom;
        double expected = sidePx * sidePx;

        double pct = System.Math.Abs(filled - expected) / expected;
        Assert.True(pct < 0.05, $"filled={filled} expected~{expected:F0} pct={pct:P2} (quantization would badly miss this)");

        surface.Dispose();
    }

    // ── L1 fix gate: a layout renderer never writes outside its own viewport rect ────────────
    // docs/sonnet-briefs/brief-L1-fix-clear-and-default-zoom.md Bug 1. Avalonia hands an
    // ICustomDrawOperation the WHOLE render-surface canvas — Bounds is for invalidation/hit-testing
    // only, it does not clip Skia — so an unclipped canvas.Clear(...) wipes every sibling control
    // already painted that frame (this was the "toolbar invisible until hover" bug). Simulated here
    // with an SKSurface strictly larger than the viewport passed to Draw.

    [Fact]
    public void Draw_NeverPaintsOutsideTheViewportRect()
    {
        var view = MakeView();
        view.Shapes.Add(new RectShape { Layer = LayerA, X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 });
        var tech = MakeTech((LayerA, SKColors.Red, 1.0));

        const int surfaceW = 400, surfaceH = 300;
        const int vpW = 200, vpH = 150;
        var sentinel = new SKColor(10, 20, 30, 255);

        using var surface = SKSurface.Create(new SKImageInfo(surfaceW, surfaceH));
        surface.Canvas.Clear(sentinel);

        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 50_000, 50_000), vpW, vpH, marginFrac: 0.1);
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = true };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        for (int y = 0; y < surfaceH; y++)
        for (int x = 0; x < surfaceW; x++)
        {
            if (x < vpW && y < vpH) continue; // inside the viewport rect — the renderer owns this region
            Assert.True(bmp.GetPixel(x, y) == sentinel,
                $"pixel ({x},{y}) outside the {vpW}x{vpH} viewport was overwritten — " +
                "canvas.Clear/DrawRect leaked past the control's own bounds");
        }
    }
}
