using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md §2.1/R-snp-12: the per-cell intrinsic
// feature index — corner/endpoint, midpoint, centroid, and (once, per resolved PCell) pins, all in
// cell-local coordinates, cached by LayoutView reference.

public class LayoutSnapFeaturesTests
{
    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static bool Has(IReadOnlyList<IntrinsicSnapFeature> features, SnapFeatureKind kind, long x, long y) =>
        features.Any(f => f.Kind == kind && f.X == x && f.Y == y);

    [Fact]
    public void Rect_ProducesFourCorners_FourMidpoints_OneCentroid()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 20_000 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(5000, 10_000, 20_000, ref counters);

        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 0, 0));
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 10_000, 0));
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 10_000, 20_000));
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 0, 20_000));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 5000, 0));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 10_000, 10_000));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 5000, 20_000));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 0, 10_000));
        Assert.True(Has(near, SnapFeatureKind.Centroid, 5000, 10_000));
    }

    [Fact]
    public void Circle_ProducesOnlyCentroid_NoCornersOrMidpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 1000, Cy = 2000, R = 5000 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(1000, 2000, 100, ref counters);

        var only = Assert.Single(near);
        Assert.Equal(SnapFeatureKind.Centroid, only.Kind);
        Assert.Equal(1000, only.X);
        Assert.Equal(2000, only.Y);
    }

    [Fact]
    public void Polygon_WithHole_IncludesHoleVertices()
    {
        var model = FreshModel();
        model.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 20_000, 0, 20_000, 20_000, 0, 20_000],
            Holes = [[5000, 5000, 15_000, 5000, 15_000, 15_000, 5000, 15_000]],
        });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(5000, 5000, 100, ref counters);
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 5000, 5000));
    }

    [Fact]
    public void ArcEdge_MidpointIsTrueArcMidpoint_NotChordMidpoint()
    {
        var model = FreshModel();
        // A 90-degree bulge (tan(90/4)) from (10000,0) to (0,10000) — the true arc midpoint is NOT
        // the chord midpoint (5000,5000); it bows out from the origin.
        double bulge = System.Math.Tan(System.Math.PI / 8.0);
        model.Shapes.Add(new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [10_000, 0, 0, 10_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(5000, 5000, 20_000, ref counters);
        var midpoints = near.Where(f => f.Kind == SnapFeatureKind.Midpoint).ToList();
        Assert.Contains(midpoints, m => m.X != 5000 || m.Y != 5000);
    }

    [Fact]
    public void Invalidate_ForcesRebuild_NewFeatureAppears()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        var idx1 = LayoutSnapFeatureIndex.Get(model, null);
        Assert.Same(idx1, LayoutSnapFeatureIndex.Get(model, null)); // cached, same reference

        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 50_000, Y1 = 50_000, X2 = 51_000, Y2 = 51_000 });
        LayoutSnapFeatureIndex.Invalidate(model);

        var idx2 = LayoutSnapFeatureIndex.Get(model, null);
        Assert.NotSame(idx1, idx2);
        var counters = new SnapQueryCounters();
        var near = idx2.QueryNear(50_000, 50_000, 100, ref counters);
        Assert.NotEmpty(near);
    }

    [Fact]
    public void Label_And_Bitmap_ContributeNoFeatures()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = new LayerKey(1, 0), X = 1000, Y = 1000, Text = "hi", Height = 500 });
        model.Shapes.Add(new BitmapShape { Layer = new LayerKey(1, 0), X = 0, Y = 0, W = 1000, H = 1000, ImagePathRef = "x.png" });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(500, 500, 5000, ref counters);
        Assert.Empty(near);
    }

    [Fact]
    public void Via_ContributesOneCornerEndpointFeature_AtItsCenter()
    {
        var model = FreshModel();
        model.Shapes.Add(new ViaShape { Layer = new LayerKey(1, 0), X = 2000, Y = 3000, PadSize = 1000, DrillSize = 500 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(2000, 3000, 100, ref counters);
        var only = Assert.Single(near);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, only.Kind);
    }

    [Fact]
    public void QueryNear_OnlyExaminesNearbyFeatures_NotEveryShapeInTheView()
    {
        var model = FreshModel();
        // A cluster of shapes far from the query point, plus one very close.
        for (int i = 0; i < 500; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 1_000_000 + i * 2000, Y1 = 1_000_000, X2 = 1_000_000 + i * 2000 + 500, Y2 = 1_000_500 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(0, 0, 200, ref counters);

        Assert.Contains(near, f => f.Kind == SnapFeatureKind.CornerEndpoint && f.X == 0 && f.Y == 0);
        // Far cluster (500 shapes x 9 features each = 4500) must not all be examined for a query
        // nowhere near them — bounded well under the total feature count.
        Assert.True(counters.FeaturesExamined < 200);
    }
}
