using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class LayoutGeometryTests
{
    // ── Bbox struct ─────────────────────────────────────────────────────────

    [Fact]
    public void Bbox_Empty_IsEmpty()
        => Assert.True(Bbox.Empty.IsEmpty);

    [Fact]
    public void Bbox_Union_WithEmpty_ReturnsOther()
    {
        var a = new Bbox(0, 0, 10, 10);
        Assert.Equal(a, a.Union(Bbox.Empty));
        Assert.Equal(a, Bbox.Empty.Union(a));
    }

    [Fact]
    public void Bbox_Union_CombinesExtents()
    {
        var a = new Bbox(0, 0, 10, 10);
        var b = new Bbox(-5, 5, 5, 20);
        Assert.Equal(new Bbox(-5, 0, 10, 20), a.Union(b));
    }

    [Fact]
    public void Bbox_Contains_AndIntersects()
    {
        var a = new Bbox(0, 0, 10, 10);
        Assert.True(a.Contains(5, 5));
        Assert.False(a.Contains(11, 5));
        Assert.True(a.Intersects(new Bbox(5, 5, 15, 15)));
        Assert.False(a.Intersects(new Bbox(20, 20, 30, 30)));
    }

    // ── Gate 9: exact bbox values ──────────────────────────────────────────────

    [Fact]
    public void BboxOf_Rect_Exact()
    {
        var r = new RectShape { X1 = 0, Y1 = 0, X2 = 100, Y2 = 50 };
        Assert.Equal(new Bbox(0, 0, 100, 50), LayoutGeometry.BboxOf(r));
    }

    [Fact]
    public void BboxOf_Polygon_Exact()
    {
        var p = new PolygonShape { Xy = [0, 0, 100, 0, 50, 80, -20, 30] };
        Assert.Equal(new Bbox(-20, 0, 100, 80), LayoutGeometry.BboxOf(p));
    }

    [Fact]
    public void BboxOf_Circle_Exact()
    {
        var c = new CircleShape { Cx = 100, Cy = 200, R = 30 };
        Assert.Equal(new Bbox(70, 170, 130, 230), LayoutGeometry.BboxOf(c));
    }

    [Fact]
    public void BboxOf_RoundedRect_Exact_MatchesOuterRect()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 200, Y2 = 100, CornerRadius = 20 };
        Assert.Equal(new Bbox(0, 0, 200, 100), LayoutGeometry.BboxOf(rr));
    }

    [Fact]
    public void BboxOf_Path_FlushEnds_NoExtensionAlongAxis()
    {
        var path = new PathShape { Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Flush };
        Assert.Equal(new Bbox(0, -100, 1000, 100), LayoutGeometry.BboxOf(path));
    }

    [Fact]
    public void BboxOf_Path_RoundEnds_ExtendsByHalfWidthPastEndpoints()
    {
        var path = new PathShape { Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Round };
        Assert.Equal(new Bbox(-100, -100, 1100, 100), LayoutGeometry.BboxOf(path));
    }

    [Fact]
    public void BboxOf_Path_SquareEnds_ExtendsByHalfWidthPastEndpoints()
    {
        var path = new PathShape { Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Square };
        Assert.Equal(new Bbox(-100, -100, 1100, 100), LayoutGeometry.BboxOf(path));
    }

    [Fact]
    public void BboxOf_Path_ExtendedEnds_ExtendsByHalfWidthPastEndpoints()
    {
        var path = new PathShape { Xy = [0, 0, 1000, 0], Width = 200, End = PathEndStyle.Extended };
        Assert.Equal(new Bbox(-100, -100, 1100, 100), LayoutGeometry.BboxOf(path));
    }

    // ── Gate 9: semicircular arc edge whose true bbox exceeds its chord's bbox ────────────────

    [Fact]
    public void ArcExtremes_HorizontalSemicircle_ExceedsChordBbox()
    {
        // Chord (0,0)-(2_000_000,0), bulge=1 => a semicircle. The chord's own bbox is degenerate
        // in Y (minY==maxY==0); the true arc bulges to Y=-1_000_000.
        var chordBbox = new Bbox(0, 0, 2_000_000, 0);
        var trueBbox = LayoutArc.ArcExtremes(0, 0, 2_000_000, 0, 1.0);

        Assert.Equal(new Bbox(0, -1_000_000, 2_000_000, 0), trueBbox);
        Assert.True(trueBbox.MinY < chordBbox.MinY, "arc bbox must exceed the chord's bbox");
    }

    [Fact]
    public void BboxOf_CurveWithSemicircularArcEdge_ExceedsChordBbox()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 2_000_000, 0, 2_000_000, 2_000_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 1.0 }, // (0,0)->(2e6,0), bulges to y=-1e6
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var bb = LayoutGeometry.BboxOf(curve);
        Assert.Equal(-1_000_000, bb.MinY);
    }

    [Fact]
    public void ArcExtremes_ZeroBulge_IsChordBbox()
    {
        Assert.Equal(new Bbox(0, 0, 100, 0), LayoutArc.ArcExtremes(0, 0, 100, 0, 0.0));
    }

    // ── LayoutArc bulge <-> arc round-trip ────────────────────────────────────

    [Fact]
    public void LayoutArc_FromBulge_QuarterCircle_MatchesKnownGeometry()
    {
        // Unit circle centered at origin: P0=(1,0), P1=(0,1), bulge=tan(22.5deg) => 90 deg sweep.
        double bulge = Math.Tan(Math.PI / 8.0);
        var arc = LayoutArc.FromBulge(1, 0, 0, 1, bulge);

        Assert.Equal(0.0, arc.Cx, 6);
        Assert.Equal(0.0, arc.Cy, 6);
        Assert.Equal(1.0, arc.R, 6);
        Assert.Equal(Math.PI / 2.0, arc.Sweep, 6);
    }

    [Fact]
    public void LayoutArc_ToBulge_InvertsSweep()
    {
        double sweep = Math.PI / 2.0;
        double bulge = LayoutArc.ToBulge(sweep);
        Assert.Equal(Math.Tan(Math.PI / 8.0), bulge, 9);
    }
}
