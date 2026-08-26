using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using SkiaSharp;

namespace CircuitRF.Ui.Tests;

// ── Phase L1e gates 2/3: docs/sonnet-briefs/brief-L1e-clipper-operations.md §0 (holes)

public class LayoutHolesTests
{
    private static readonly LayerKey Layer1 = new(1, 0);

    /// <summary>An outer 0..100000 square with a concentric 30000..70000 hole — CCW outer, CW hole
    /// (opposite windings), exactly what Clipper2 output looks like.</summary>
    private static PolygonShape MakeDonut() => new()
    {
        Layer = Layer1,
        Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
        Holes = [[30_000, 30_000, 30_000, 70_000, 70_000, 70_000, 70_000, 30_000]],
    };

    // ── Gate 2: round-trip ──────────────────────────────────────────────────────

    [Fact]
    public void HoleShape_RoundTrips_ByteIdenticalAfterSerializeReload()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 };
        var rect = new PolygonShape { Layer = Layer1, Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000] };
        var circle = new CircleShape { Layer = Layer1, Cx = 50_000, Cy = 50_000, R = 20_000 };
        var result = LayoutBooleans.Difference([rect, circle], null);
        var withHole = Assert.Single(result.Shapes);
        var poly = Assert.IsType<PolygonShape>(withHole);
        Assert.NotNull(poly.Holes);
        Assert.Single(poly.Holes!);
        view.Shapes.Add(withHole);

        var json1 = LayoutPersistence.Serialize(view);
        var reloaded = LayoutPersistence.Deserialize(json1);
        var json2 = LayoutPersistence.Serialize(reloaded);

        Assert.Equal(json1, json2);
    }

    [Fact]
    public void HoleFreeClay_StillLoads_NoFormatVersionBump()
    {
        // §0's additive/backward-compatible promise: adding Holes must not require a format bump.
        Assert.Equal(1, LayoutPersistence.CurrentFormatVersion);

        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 100 };
        view.Shapes.Add(new PolygonShape { Layer = Layer1, Xy = [0, 0, 1000, 0, 1000, 1000] });
        var reloaded = LayoutPersistence.Deserialize(LayoutPersistence.Serialize(view));
        var p = Assert.IsType<PolygonShape>(Assert.Single(reloaded.Shapes));
        Assert.Null(p.Holes);
    }

    // ── Gate 3: holes behave everywhere ──────────────────────────────────────────

    [Fact]
    public void BboxOf_IgnoresHoles_EqualsOuterRingBbox()
    {
        var donut = MakeDonut();
        var bb = LayoutGeometry.BboxOf(donut);
        Assert.Equal(new Bbox(0, 0, 100_000, 100_000), bb);
    }

    [Fact]
    public void HitTest_PointInsideHole_IsNotAHit()
    {
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 100 };
        view.Shapes.Add(MakeDonut());

        // Dead center of the hole.
        var atHoleCenter = LayoutHitTest.HitStack(view, null, 50_000, 50_000, 0);
        Assert.Empty(atHoleCenter);

        // In the ring (between hole edge and outer edge).
        var inRing = LayoutHitTest.HitStack(view, null, 10_000, 10_000, 0);
        Assert.Single(inRing);
    }

    [Fact]
    public void HitTest_NearHoleBoundary_IsStillAHit()
    {
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 100 };
        view.Shapes.Add(MakeDonut());

        // Exactly on the hole's edge — the boundary is still part of the shape.
        var onHoleEdge = LayoutHitTest.HitStack(view, null, 30_000, 50_000, 50);
        Assert.Single(onHoleEdge);
    }

    [Fact]
    public void Scaling_Refine_ScalesHoleCoordinates()
    {
        var view = new LayoutView { DbuPerMicron = 1000, SnapDbu = 100 };
        view.Shapes.Add(MakeDonut());

        Assert.True(LayoutScaling.TryChangeResolution(view, 2000, out _));

        var poly = Assert.IsType<PolygonShape>(view.Shapes[0]);
        Assert.Equal([60_000, 60_000, 60_000, 140_000, 140_000, 140_000, 140_000, 60_000], poly.Holes![0]);
    }

    [Fact]
    public void TranslateBy_TranslatesHoleCoordinatesToo()
    {
        var donut = MakeDonut();
        LayoutGeometry.TranslateBy(donut, 1000, 2000);
        Assert.Equal([31_000, 32_000, 31_000, 72_000, 71_000, 72_000, 71_000, 32_000], donut.Holes![0]);
    }

    [Fact]
    public void Clone_DeepClonesHoles_MutatingCloneDoesNotAffectOriginal()
    {
        var donut = MakeDonut();
        var clone = (PolygonShape)LayoutGeometry.Clone(donut);
        clone.Holes![0][0] = 999;
        Assert.NotEqual(999, donut.Holes![0][0]);
    }

    // ── Gate 3: rendered donut has an actual visible hole ────────────────────────

    [Fact]
    public void RenderedDonut_CenterPixelIsBackground_RingPixelIsLayerColored()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(MakeDonut());

        var tech = new Technology { Name = "Test", DefaultDisplayUnit = LayoutUnit.Um, DefaultSnapDbu = 1000 };
        tech.Layers.Add(new LayerDef
        {
            Key = Layer1, Name = "L1",
            Color = new CircuitRF.Design.Theming.Rgba(255, 0, 0),
            FillOpacity = 1.0, ZOrder = 0, Visible = true, Selectable = true,
        });

        var vp = LayoutViewport.ZoomToFit(new Bbox(0, 0, 100_000, 100_000), 400, 400, 0.1);
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        var opts = new LayoutRenderOptions { Theme = LayoutRenderTheme.Light, ShowGrid = false };
        LayoutRenderer.Draw(surface.Canvas, view, tech, vp, opts);

        using var img = surface.Snapshot();
        using var bmp = SKBitmap.FromImage(img);

        int cx = (int)vp.WorldToScreenX(50_000);
        int cy = (int)vp.WorldToScreenY(50_000);
        int rx = (int)vp.WorldToScreenX(10_000);
        int ry = (int)vp.WorldToScreenY(50_000);

        var centerPixel = bmp.GetPixel(cx, cy);
        var ringPixel = bmp.GetPixel(rx, ry);
        var bg = LayoutRenderTheme.Light.Background;

        Assert.Equal(bg, centerPixel);
        Assert.True(ringPixel.Red > ringPixel.Green + 30 && ringPixel.Red > ringPixel.Blue + 30,
            $"expected a red-dominant ring pixel, got {ringPixel}");
    }
}
