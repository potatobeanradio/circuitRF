using System.Collections.Generic;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1c gate 2 + R-L1c-1 (determinism): docs/sonnet-briefs/brief-L1c-selection-and-properties.md

public class LayoutFlattenerTests
{
    [Fact]
    public void Rect_ReturnsFourCornersUnchanged()
    {
        var r = new RectShape { X1 = 0, Y1 = 0, X2 = 5000, Y2 = 3000 };
        var ring = Assert.Single(LayoutFlattener.Flatten(r, 1000));
        Assert.Equal(new long[] { 0, 0, 5000, 0, 5000, 3000, 0, 3000 }, ring);
    }

    [Fact]
    public void Polygon_StraightEdges_PassesThroughUnchanged_SameArrayReference()
    {
        var xy = new long[] { 0, 0, 1000, 0, 1000, 1000, 0, 1000 };
        var p = new PolygonShape { Xy = xy };
        var ring = Assert.Single(LayoutFlattener.Flatten(p, 1000));
        Assert.Same(xy, ring); // "returned as-is; no work, no allocation churn"
    }

    [Theory]
    [InlineData(100_000, 1000)]
    [InlineData(50_000, 500)]
    [InlineData(1_000_000, 2000)]
    public void Circle_AllVerticesWithinTolerance_OfTrueCircle(long r, long tol)
    {
        var c = new CircleShape { Cx = 12345, Cy = -6789, R = r };
        var ring = Assert.Single(LayoutFlattener.Flatten(c, tol));
        int n = ring.Length / 2;
        Assert.True(n >= 3);
        for (int i = 0; i < n; i++)
        {
            double dx = ring[2 * i] - c.Cx, dy = ring[2 * i + 1] - c.Cy;
            double dist = System.Math.Sqrt(dx * dx + dy * dy);
            Assert.True(System.Math.Abs(dist - r) <= tol + 1,
                $"vertex {i} at distance {dist} from center, expected within {tol} of r={r}");
        }
    }

    [Fact]
    public void Circle_HalvingTolerance_StrictlyIncreasesVertexCount()
    {
        var c = new CircleShape { Cx = 0, Cy = 0, R = 1_000_000 };
        int nCoarse = LayoutFlattener.Flatten(c, 10_000)[0].Length;
        int nFine = LayoutFlattener.Flatten(c, 5_000)[0].Length;
        Assert.True(nFine > nCoarse, $"fine={nFine} coarse={nCoarse}");
    }

    [Fact]
    public void RoundedRect_ZeroRadius_ReturnsFourCorners()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, CornerRadius = 0 };
        var ring = Assert.Single(LayoutFlattener.Flatten(rr, 100));
        Assert.Equal(new long[] { 0, 0, 1000, 0, 1000, 1000, 0, 1000 }, ring);
    }

    [Fact]
    public void RoundedRect_PositiveRadius_MoreThanFourVertices_CornersWithinRadiusOfExpectedCenter()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 60_000, CornerRadius = 20_000 };
        var ring = Assert.Single(LayoutFlattener.Flatten(rr, 1000));
        Assert.True(ring.Length / 2 > 4);

        // Every vertex must lie on the rounded-rect boundary: either on a straight edge, or within
        // tolerance of one of the four corner arc centers at radius CornerRadius.
        (long cx, long cy)[] centers =
        [
            (rr.X2 - rr.CornerRadius, rr.Y1 + rr.CornerRadius),
            (rr.X2 - rr.CornerRadius, rr.Y2 - rr.CornerRadius),
            (rr.X1 + rr.CornerRadius, rr.Y2 - rr.CornerRadius),
            (rr.X1 + rr.CornerRadius, rr.Y1 + rr.CornerRadius),
        ];
        int n = ring.Length / 2;
        for (int i = 0; i < n; i++)
        {
            long x = ring[2 * i], y = ring[2 * i + 1];
            bool onStraightEdge = x == rr.X1 || x == rr.X2 || y == rr.Y1 || y == rr.Y2;
            bool onArc = false;
            foreach (var (cx, cy) in centers)
            {
                double dx = x - cx, dy = y - cy;
                double dist = System.Math.Sqrt(dx * dx + dy * dy);
                if (System.Math.Abs(dist - rr.CornerRadius) <= 1001) { onArc = true; break; }
            }
            Assert.True(onStraightEdge || onArc, $"vertex {i} ({x},{y}) not on any edge or corner arc");
        }
    }

    // ── R-L1c-1: determinism ─────────────────────────────────────────────────

    private static CurveShape SampleCurveWithArcAndCubic() => new()
    {
        Layer = new LayerKey(1, 0),
        Xy = [0, 0, 2_000_000, 0, 2_000_000, 2_000_000, 0, 2_000_000],
        Edges =
        [
            new LayoutEdge { Kind = EdgeKind.Line },
            new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.4142 },
            new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 2_100_000, C1Y = 2_100_000, C2X = 100_000, C2Y = 2_100_000 },
            new LayoutEdge { Kind = EdgeKind.Line },
        ],
    };

    [Fact]
    public void Flatten_SameShapeAndTolerance_100Times_ByteIdentical()
    {
        var curve = SampleCurveWithArcAndCubic();
        var first = LayoutFlattener.Flatten(curve, 500)[0];
        for (int i = 0; i < 100; i++)
        {
            var again = LayoutFlattener.Flatten(curve, 500)[0];
            Assert.Equal(first, again);
        }
    }

    [Fact]
    public void Flatten_AfterSerializeDeserializeRoundTrip_ByteIdentical()
    {
        var view = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        view.Shapes.Add(SampleCurveWithArcAndCubic());

        var before = LayoutFlattener.Flatten(view.Shapes[0], 500)[0];

        var json = LayoutPersistence.Serialize(view);
        var reloaded = LayoutPersistence.Deserialize(json);

        var after = LayoutFlattener.Flatten(reloaded.Shapes[0], 500)[0];
        Assert.Equal(before, after);
    }

    [Fact]
    public void Cubic_SubdividesUntilFlat_RecursionTerminates()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 100_000, 0],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 0, C1Y = 50_000, C2X = 100_000, C2Y = -50_000 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };
        var ring = LayoutFlattener.Flatten(curve, 100)[0];
        Assert.True(ring.Length / 2 > 2);
    }

    [Fact]
    public void ResolveTolDbu_ShapeOverride_WinsOverTechnology()
    {
        var curve = new CurveShape { Xy = [0, 0, 1, 1], FlattenTolDbu = 42 };
        var tech = StarterTechnologies.Pcb2Layer();
        Assert.Equal(42, LayoutFlattener.ResolveTolDbu(curve, tech));
    }

    [Fact]
    public void ResolveTolDbu_NoOverride_FallsBackToTechnologyThenDefault()
    {
        var curve = new CurveShape { Xy = [0, 0, 1, 1] };
        var tech = StarterTechnologies.Pcb2Layer();
        Assert.Equal(tech.DefaultFlattenTolDbu, LayoutFlattener.ResolveTolDbu(curve, tech));
        Assert.Equal(LayoutFlattener.DefaultTolDbu, LayoutFlattener.ResolveTolDbu(curve, null));
    }
}
