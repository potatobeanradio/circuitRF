using System;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1d gates 5-9: pure geometry-builder unit tests for LayoutShapeEditing.cs ──
// (VM-level gesture tests exercising these through LayoutEditorViewModel live in
// LayoutHandleGesturesTests.cs; these tests pin the exact array/edge-list contents that
// gesture tests would otherwise have to derive from screen coordinates.)

public class LayoutShapeEditingTests
{
    private static PolygonShape Square() => new()
    {
        Layer = new LayerKey(1, 0),
        Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000],
    };

    // ── SetVertex ────────────────────────────────────────────────────────────

    [Fact]
    public void SetVertex_MovesOnlyTheTargetedVertex_OthersUntouched()
    {
        var original = Square();
        var result = (PolygonShape)LayoutShapeEditing.SetVertex(original, 1, 1500, 500);

        Assert.Equal([0, 0, 1500, 500, 1000, 1000, 0, 1000], result.Xy);
        // original is untouched (immutable-style builder)
        Assert.Equal([0, 0, 1000, 0, 1000, 1000, 0, 1000], original.Xy);
    }

    // ── TranslateEdgeEndpoints ───────────────────────────────────────────────

    [Fact]
    public void TranslateEdgeEndpoints_MovesOnlyThatEdgesTwoEndpoints()
    {
        var poly = Square();
        // Edge 0 runs vertex0(0,0) -> vertex1(1000,0). Translate perpendicular by dy=-200.
        var result = (PolygonShape)LayoutShapeEditing.TranslateEdgeEndpoints(poly, 0, 0, -200);

        Assert.Equal([0, -200, 1000, -200, 1000, 1000, 0, 1000], result.Xy);
    }

    [Fact]
    public void TranslateEdgeEndpoints_ClosedShape_WrapsAroundForLastEdge()
    {
        var poly = Square();
        // Edge 3 runs vertex3(0,1000) -> vertex0(0,0) (wraps).
        var result = (PolygonShape)LayoutShapeEditing.TranslateEdgeEndpoints(poly, 3, -200, 0);

        Assert.Equal([-200, 0, 1000, 0, 1000, 1000, -200, 1000], result.Xy);
    }

    // ── SetBulge ─────────────────────────────────────────────────────────────

    [Fact]
    public void SetBulge_ConvertsTargetEdgeToArc_LeavesOthersLine()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var result = (CurveShape)LayoutShapeEditing.SetBulge(curve, 0, 0.5);

        Assert.NotNull(result.Edges);
        Assert.Equal(EdgeKind.Arc, result.Edges[0].Kind);
        Assert.Equal(0.5, result.Edges[0].Bulge);
        Assert.Equal(EdgeKind.Line, result.Edges[1].Kind);
    }

    [Fact]
    public void SetBulge_Zero_RendersAsStraight_ButStaysArcKind()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var result = (CurveShape)LayoutShapeEditing.SetBulge(curve, 0, 0);

        Assert.NotNull(result.Edges);
        Assert.Equal(EdgeKind.Arc, result.Edges[0].Kind);
        Assert.Equal(0, result.Edges[0].Bulge);
        var arc = LayoutArc.FromBulge(0, 0, 1000, 0, result.Edges[0].Bulge);
        Assert.Equal(0, arc.Sweep, 6); // zero sweep == a straight chord
    }

    [Fact]
    public void SetBulge_NegativeFlipsSide_ButMagnitudeSame()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var pos = (CurveShape)LayoutShapeEditing.SetBulge(curve, 0, 0.6);
        var neg = (CurveShape)LayoutShapeEditing.SetBulge(curve, 0, -0.6);

        Assert.NotNull(pos.Edges);
        Assert.NotNull(neg.Edges);
        var arcPos = LayoutArc.FromBulge(0, 0, 1000, 0, pos.Edges[0].Bulge);
        var arcNeg = LayoutArc.FromBulge(0, 0, 1000, 0, neg.Edges[0].Bulge);
        Assert.Equal(Math.Abs(arcPos.Sweep), Math.Abs(arcNeg.Sweep), 6);
        Assert.NotEqual(0, arcPos.Cy);
        Assert.NotEqual(0, arcNeg.Cy);
        Assert.True(Math.Sign(arcPos.Cy) != Math.Sign(arcNeg.Cy));
    }

    // ── SetCubicControl ──────────────────────────────────────────────────────

    [Fact]
    public void SetCubicControl_SubIndex0And1_SetDistinctControlPoints()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 300, C1Y = 0, C2X = 700, C2Y = 0 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var moved0 = (CurveShape)LayoutShapeEditing.SetCubicControl(curve, 0, 0, 300, 200);
        Assert.NotNull(moved0.Edges);
        Assert.Equal(300, moved0.Edges[0].C1X); Assert.Equal(200, moved0.Edges[0].C1Y);
        Assert.Equal(700, moved0.Edges[0].C2X); Assert.Equal(0, moved0.Edges[0].C2Y); // untouched

        var moved1 = (CurveShape)LayoutShapeEditing.SetCubicControl(curve, 0, 1, 700, 200);
        Assert.NotNull(moved1.Edges);
        Assert.Equal(300, moved1.Edges[0].C1X); Assert.Equal(0, moved1.Edges[0].C1Y); // untouched
        Assert.Equal(700, moved1.Edges[0].C2X); Assert.Equal(200, moved1.Edges[0].C2Y);
    }

    // ── SetRadius / SetCornerRadius ──────────────────────────────────────────

    [Fact]
    public void SetRadius_ClampsAtZero_NeverNegative()
    {
        var circle = new CircleShape { Cx = 0, Cy = 0, R = 500 };
        Assert.Equal(800, LayoutShapeEditing.SetRadius(circle, 800).R);
        Assert.Equal(0, LayoutShapeEditing.SetRadius(circle, -100).R);
    }

    [Fact]
    public void SetCornerRadius_ClampsToHalfOfSmallerDimension()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 2000, CornerRadius = 100 };
        // min(width,height)/2 = min(1000,2000)/2 = 500
        Assert.Equal(500, LayoutShapeEditing.SetCornerRadius(rr, 900).CornerRadius);
        Assert.Equal(0, LayoutShapeEditing.SetCornerRadius(rr, -50).CornerRadius);
        Assert.Equal(300, LayoutShapeEditing.SetCornerRadius(rr, 300).CornerRadius);
    }

    // ── ResizeRectCorner / NormalizeRect ─────────────────────────────────────

    [Fact]
    public void ResizeRectCorner_CanGoInsideOut_DuringDrag()
    {
        var r = new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var resized = LayoutShapeEditing.ResizeRectCorner(r, 2, -500, -500); // drag (X2,Y2) past (X1,Y1)

        Assert.Equal(0, resized.X1); Assert.Equal(0, resized.Y1);
        Assert.Equal(-500, resized.X2); Assert.Equal(-500, resized.Y2);

        var normalized = LayoutShapeEditing.NormalizeRect(resized);
        Assert.Equal(-500, normalized.X1); Assert.Equal(-500, normalized.Y1);
        Assert.Equal(0, normalized.X2); Assert.Equal(0, normalized.Y2);
    }

    // ── TranslateRectEdge / TranslateRoundedRectEdge / FindEdgeLineHit on Rect ────────────────

    [Theory]
    [InlineData(0, "Y1", 300)]  // bottom
    [InlineData(1, "X2", 300)]  // right
    [InlineData(2, "Y2", 300)]  // top
    [InlineData(3, "X1", 300)]  // left
    public void TranslateRectEdge_MovesOnlyTheOneCorrespondingField(int edgeIndex, string field, long delta)
    {
        var r = new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var result = LayoutShapeEditing.TranslateRectEdge(r, edgeIndex, delta);

        long expected = field switch { "Y1" => delta, "X2" => 1000 + delta, "Y2" => 1000 + delta, "X1" => delta, _ => 0 };
        long actual = field switch { "Y1" => result.Y1, "X2" => result.X2, "Y2" => result.Y2, "X1" => result.X1, _ => 0 };
        Assert.Equal(expected, actual);

        // Every OTHER field is untouched.
        if (field != "X1") Assert.Equal(0, result.X1);
        if (field != "Y1") Assert.Equal(0, result.Y1);
        if (field != "X2") Assert.Equal(1000, result.X2);
        if (field != "Y2") Assert.Equal(1000, result.Y2);
    }

    [Fact]
    public void TranslateRoundedRectEdge_LeavesCornerRadiusUntouched()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, CornerRadius = 150 };
        var result = LayoutShapeEditing.TranslateRoundedRectEdge(rr, 1, 400); // right edge

        Assert.Equal(1400, result.X2);
        Assert.Equal(150, result.CornerRadius);
    }

    [Theory]
    [InlineData(500, 10, 0)]    // near the bottom edge's line
    [InlineData(990, 500, 1)]   // near the right edge's line
    [InlineData(500, 990, 2)]   // near the top edge's line
    [InlineData(10, 500, 3)]    // near the left edge's line
    public void FindEdgeLineHit_Rect_FindsTheNearestOfTheFourSides(long px, long py, int expectedEdge)
    {
        var r = new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        Assert.Equal(expectedEdge, LayoutShapeEditing.FindEdgeLineHit(r, px, py, tolDbu: 40));
    }

    [Fact]
    public void FindEdgeLineHit_Rect_OutsideTolerance_ReturnsNull()
    {
        var r = new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        Assert.Null(LayoutShapeEditing.FindEdgeLineHit(r, 500, 500, tolDbu: 40)); // dead center
    }

    [Fact]
    public void FindEdgeLineHit_RoundedRect_AlsoWorks_SameCornerOrderAsRect()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, CornerRadius = 200 };
        Assert.Equal(1, LayoutShapeEditing.FindEdgeLineHit(rr, 990, 500, tolDbu: 40));
    }

    // ── RemoveVertex ─────────────────────────────────────────────────────────

    [Fact]
    public void RemoveVertex_ClosedShape_BlockedAtThreeVertices()
    {
        var tri = new PolygonShape { Xy = [0, 0, 1000, 0, 500, 1000] };
        Assert.Null(LayoutShapeEditing.RemoveVertex(tri, 0));
    }

    [Fact]
    public void RemoveVertex_OpenPath_BlockedAtTwoVertices()
    {
        var path = new PathShape { Xy = [0, 0, 1000, 0], Width = 100 };
        Assert.Null(LayoutShapeEditing.RemoveVertex(path, 0));
    }

    [Fact]
    public void RemoveVertex_ClosedShape_MiddleRemoval_MergesIntoOneLineEdge()
    {
        var poly = Square(); // 4 vertices, closed
        var result = (PolygonShape)LayoutShapeEditing.RemoveVertex(poly, 1)!;

        Assert.Equal([0, 0, 1000, 1000, 0, 1000], result.Xy);
    }

    [Fact]
    public void RemoveVertex_OpenPath_TrueEndpointRemoval_JustShortensNoMergeEdge()
    {
        var path = new PathShape
        {
            Xy = [0, 0, 1000, 0, 2000, 0], Width = 100,
            Edges = [new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var result = (PathShape)LayoutShapeEditing.RemoveVertex(path, 0)!;

        Assert.Equal([1000, 0, 2000, 0], result.Xy);
        Assert.Single(result.Edges!); // one edge remains, no merge-edge inserted for a true endpoint
    }

    [Fact]
    public void RemoveVertex_OpenPath_MiddleRemoval_MergesEdges()
    {
        var path = new PathShape
        {
            Xy = [0, 0, 1000, 0, 2000, 500], Width = 100,
            Edges = [new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var result = (PathShape)LayoutShapeEditing.RemoveVertex(path, 1)!;

        Assert.Equal([0, 0, 2000, 500], result.Xy);
        Assert.Single(result.Edges!);
        Assert.Equal(EdgeKind.Line, result.Edges![0].Kind);
    }

    // ── InsertVertexOnEdge — Line edge: snaps to the click point ────────────

    [Fact]
    public void InsertVertexOnEdge_LineEdge_SnapsClickPointToGrid()
    {
        var poly = Square();
        var result = (PolygonShape)LayoutShapeEditing.InsertVertexOnEdge(poly, 0, 490, 30, snapDbu: 100, suspendSnap: false);

        // Inserted between vertex 0 (0,0) and vertex 1 (1000,0); click (490,30) snaps to (500,0).
        Assert.Equal([0, 0, 500, 0, 1000, 0, 1000, 1000, 0, 1000], result.Xy);
    }

    [Fact]
    public void InsertVertexOnEdge_LineEdge_SuspendSnap_UsesRawClickPoint()
    {
        var poly = Square();
        var result = (PolygonShape)LayoutShapeEditing.InsertVertexOnEdge(poly, 0, 490, 30, snapDbu: 100, suspendSnap: true);

        Assert.Equal([0, 0, 490, 30, 1000, 0, 1000, 1000, 0, 1000], result.Xy);
    }

    // ── InsertVertexOnEdge — Arc edge: exact split, never snapped ───────────

    [Fact]
    public void InsertVertexOnEdge_ArcEdge_SplitsIntoTwoArcsSharingCenterAndRadius()
    {
        // A semicircle from (0,0) to (2000,0): bulge = tan(sweep/4); sweep = pi (180 deg) => bulge = tan(pi/4) = 1.
        var curve = new CurveShape
        {
            Xy = [0, 0, 2000, 0, 2000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 1.0 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var originalArc = LayoutArc.FromBulge(0, 0, 2000, 0, 1.0);

        // Click near the arc's midpoint (roughly (1000, +/-1000) depending on winding).
        double midAngle = originalArc.StartAngle + originalArc.Sweep / 2;
        long clickX = (long)Math.Round(originalArc.Cx + originalArc.R * Math.Cos(midAngle));
        long clickY = (long)Math.Round(originalArc.Cy + originalArc.R * Math.Sin(midAngle));

        var result = (CurveShape)LayoutShapeEditing.InsertVertexOnEdge(curve, 0, clickX, clickY, snapDbu: 100, suspendSnap: false);

        Assert.Equal(4, result.Xy.Length / 2); // one vertex added
        Assert.NotNull(result.Edges);
        Assert.Equal(4, result.Edges.Count);   // one edge split into two
        Assert.Equal(EdgeKind.Arc, result.Edges[0].Kind);
        Assert.Equal(EdgeKind.Arc, result.Edges[1].Kind);

        long newX = result.Xy[2], newY = result.Xy[3];
        var arc1 = LayoutArc.FromBulge(0, 0, newX, newY, result.Edges[0].Bulge);
        var arc2 = LayoutArc.FromBulge(newX, newY, 2000, 0, result.Edges[1].Bulge);

        // Both sub-arcs share the original arc's center and radius (within rounding of the split point).
        Assert.Equal(originalArc.Cx, arc1.Cx, 0);
        Assert.Equal(originalArc.Cy, arc1.Cy, 0);
        Assert.Equal(originalArc.R, arc1.R, 0);
        Assert.Equal(originalArc.Cx, arc2.Cx, 0);
        Assert.Equal(originalArc.R, arc2.R, 0);

        // Sweeps recombine to (approximately) the original sweep.
        Assert.Equal(originalArc.Sweep, arc1.Sweep + arc2.Sweep, 2);

        // The new vertex actually lies on the original circle (visually unchanged, gate 7).
        double distFromCenter = Math.Sqrt(Math.Pow(newX - originalArc.Cx, 2) + Math.Pow(newY - originalArc.Cy, 2));
        Assert.Equal(originalArc.R, distFromCenter, 1);
    }

    // ── InsertVertexOnEdge — Cubic edge: exact de Casteljau split ───────────

    [Fact]
    public void InsertVertexOnEdge_CubicEdge_SplitsIntoTwoCubicsAtNearestParameter()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 3000, 0, 3000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 1000, C1Y = 0, C2X = 2000, C2Y = 0 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        // Click near the midpoint of the (straight, since control points are collinear) cubic.
        var result = (CurveShape)LayoutShapeEditing.InsertVertexOnEdge(curve, 0, 1500, 0, snapDbu: 100, suspendSnap: false);

        Assert.Equal(4, result.Xy.Length / 2);
        Assert.NotNull(result.Edges);
        Assert.Equal(4, result.Edges.Count);
        Assert.Equal(EdgeKind.Cubic, result.Edges[0].Kind);
        Assert.Equal(EdgeKind.Cubic, result.Edges[1].Kind);

        long newX = result.Xy[2], newY = result.Xy[3];
        // Since the source cubic is a degenerate straight line (all points collinear on Y=0),
        // the split point must also land on Y=0, roughly at the midpoint in X.
        Assert.Equal(0, newY);
        Assert.InRange(newX, 1000, 2000);
    }

    // ── ConvertEdge — the promotion rule (R-L1d-3) ──────────────────────────

    [Fact]
    public void ConvertEdge_Polygon_PromotesToCurveShape_SameLayerNetAndVertices()
    {
        var poly = new PolygonShape { Layer = new LayerKey(2, 1), Net = "NET1", Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000] };
        var result = LayoutShapeEditing.ConvertEdge(poly, 0, EdgeKind.Arc);

        var curve = Assert.IsType<CurveShape>(result);
        Assert.Equal(new LayerKey(2, 1), curve.Layer);
        Assert.Equal("NET1", curve.Net);
        Assert.Equal(poly.Xy, curve.Xy);
        Assert.NotNull(curve.Edges);
        Assert.Equal(EdgeKind.Arc, curve.Edges[0].Kind);
        Assert.Equal(0, curve.Edges[0].Bulge); // "a straight arc" per the comment — visually unchanged
        for (int i = 1; i < curve.Edges.Count; i++) Assert.Equal(EdgeKind.Line, curve.Edges[i].Kind);
    }

    [Fact]
    public void ConvertEdge_Path_GainsCurvedEdge_NoTypeChange()
    {
        var path = new PathShape { Xy = [0, 0, 1000, 0, 1000, 1000], Width = 200 };
        var result = LayoutShapeEditing.ConvertEdge(path, 0, EdgeKind.Cubic);

        var samePath = Assert.IsType<PathShape>(result);
        Assert.NotNull(samePath.Edges);
        Assert.Equal(EdgeKind.Cubic, samePath.Edges[0].Kind);
        // 1/3 and 2/3 control points along the original straight edge -- initial shape unchanged.
        Assert.Equal(333, samePath.Edges[0].C1X);
        Assert.Equal(666, samePath.Edges[0].C2X);
    }

    [Fact]
    public void ConvertEdge_ToLine_ResetsToPlainLineEdge()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.7 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var result = (CurveShape)LayoutShapeEditing.ConvertEdge(curve, 0, EdgeKind.Line);

        Assert.NotNull(result.Edges);
        Assert.Equal(EdgeKind.Line, result.Edges[0].Kind);
    }

    // ── FindEdgeLineHit ──────────────────────────────────────────────────────

    [Fact]
    public void FindEdgeLineHit_FindsNearestEdge_WithinTolerance()
    {
        var poly = Square();
        // Point near the bottom edge (vertex0->vertex1, y=0).
        Assert.Equal(0, LayoutShapeEditing.FindEdgeLineHit(poly, 500, 10, tolDbu: 40));
        // Point near the right edge (vertex1->vertex2, x=1000).
        Assert.Equal(1, LayoutShapeEditing.FindEdgeLineHit(poly, 990, 500, tolDbu: 40));
    }

    [Fact]
    public void FindEdgeLineHit_OutsideTolerance_ReturnsNull()
    {
        var poly = Square();
        Assert.Null(LayoutShapeEditing.FindEdgeLineHit(poly, 500, 500, tolDbu: 40)); // dead center, far from any edge
    }

    // ── IsStraightEdge / IsVertexListShape ───────────────────────────────────

    [Fact]
    public void IsStraightEdge_PolygonHasNoEdgeList_EveryEdgeIsImplicitlyLine()
    {
        var poly = Square();
        Assert.True(LayoutShapeEditing.IsStraightEdge(poly, 0));
        Assert.True(LayoutShapeEditing.IsStraightEdge(poly, 3));
    }

    [Fact]
    public void IsVertexListShape_TrueForPolygonCurvePath_FalseForOthers()
    {
        Assert.True(LayoutShapeEditing.IsVertexListShape(Square()));
        Assert.True(LayoutShapeEditing.IsVertexListShape(new CurveShape { Xy = [0, 0, 1, 1, 2, 2] }));
        Assert.True(LayoutShapeEditing.IsVertexListShape(new PathShape { Xy = [0, 0, 1, 1] }));
        Assert.False(LayoutShapeEditing.IsVertexListShape(new RectShape { X1 = 0, Y1 = 0, X2 = 1, Y2 = 1 }));
        Assert.False(LayoutShapeEditing.IsVertexListShape(new CircleShape { Cx = 0, Cy = 0, R = 1 }));
    }
}
