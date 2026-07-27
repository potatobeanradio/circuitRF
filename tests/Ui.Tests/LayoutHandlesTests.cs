using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// ── Phase L1d: LayoutHandles.Build per shape kind (docs/sonnet-briefs/brief-L1d-shape-editing-handles.md §2) ──

public class LayoutHandlesTests
{
    [Fact]
    public void Rect_FourCornerVertexHandlesPlusFourEdgeMidpoints()
    {
        var r = new RectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 2000 };
        var handles = LayoutHandles.Build(r);

        Assert.Equal(8, handles.Count);
        Assert.Equal(4, handles.Count(h => h.Kind == LayoutHandleKind.Vertex));
        Assert.Contains(handles, h => h.Kind == LayoutHandleKind.Vertex && h.X == 0 && h.Y == 0);
        Assert.Contains(handles, h => h.Kind == LayoutHandleKind.Vertex && h.X == 1000 && h.Y == 0);
        Assert.Contains(handles, h => h.Kind == LayoutHandleKind.Vertex && h.X == 1000 && h.Y == 2000);
        Assert.Contains(handles, h => h.Kind == LayoutHandleKind.Vertex && h.X == 0 && h.Y == 2000);

        // Edge-midpoint handles enable "drag edge midpoint / edge line" on a Rect too (§3).
        var edges = handles.Where(h => h.Kind == LayoutHandleKind.EdgeMidpoint).ToList();
        Assert.Equal(4, edges.Count);
        Assert.Contains(edges, h => h.X == 500 && h.Y == 0);    // bottom
        Assert.Contains(edges, h => h.X == 1000 && h.Y == 1000); // right
        Assert.Contains(edges, h => h.X == 500 && h.Y == 2000); // top
        Assert.Contains(edges, h => h.X == 0 && h.Y == 1000);   // left
    }

    [Fact]
    public void RoundedRect_FourCornersPlusFourEdgeMidpointsPlusCornerRadiusHandle()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, CornerRadius = 200 };
        var handles = LayoutHandles.Build(rr);

        Assert.Equal(9, handles.Count);
        Assert.Equal(4, handles.Count(h => h.Kind == LayoutHandleKind.Vertex));
        Assert.Equal(4, handles.Count(h => h.Kind == LayoutHandleKind.EdgeMidpoint));
        var cr = Assert.Single(handles, h => h.Kind == LayoutHandleKind.CornerRadius);
        Assert.Equal(200, cr.X); Assert.Equal(1000, cr.Y);
    }

    [Fact]
    public void Circle_SingleRadiusHandle_OnPlusXAxis()
    {
        var c = new CircleShape { Cx = 500, Cy = 500, R = 300 };
        var h = Assert.Single(LayoutHandles.Build(c));

        Assert.Equal(LayoutHandleKind.Radius, h.Kind);
        Assert.Equal(800, h.X); Assert.Equal(500, h.Y);
    }

    [Fact]
    public void Polygon_VertexAndEdgeMidpointHandles()
    {
        var p = new PolygonShape { Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000] };
        var handles = LayoutHandles.Build(p);

        Assert.Equal(8, handles.Count); // 4 vertex + 4 edge-midpoint
        Assert.Equal(4, handles.Count(h => h.Kind == LayoutHandleKind.Vertex));
        Assert.Equal(4, handles.Count(h => h.Kind == LayoutHandleKind.EdgeMidpoint));
    }

    [Fact]
    public void Curve_ArcEdge_GetsBulgeHandle_NotEdgeMidpoint()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var handles = LayoutHandles.Build(curve);

        Assert.Equal(3, handles.Count(h => h.Kind == LayoutHandleKind.Vertex));
        Assert.Single(handles, h => h.Kind == LayoutHandleKind.Bulge);
        Assert.Equal(2, handles.Count(h => h.Kind == LayoutHandleKind.EdgeMidpoint));
    }

    [Fact]
    public void Curve_CubicEdge_GetsTwoControlPointHandles()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 300, C1Y = 0, C2X = 700, C2Y = 0 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var handles = LayoutHandles.Build(curve);
        var ctrls = handles.Where(h => h.Kind == LayoutHandleKind.CubicControl).ToList();

        Assert.Equal(2, ctrls.Count);
        Assert.Contains(ctrls, h => h.X == 300 && h.Y == 0 && h.SubIndex == 0);
        Assert.Contains(ctrls, h => h.X == 700 && h.Y == 0 && h.SubIndex == 1);
    }

    [Fact]
    public void Path_OpenEdgeList_HasOneFewerEdgeThanVertices()
    {
        var path = new PathShape { Xy = [0, 0, 1000, 0, 1000, 1000], Width = 100 };
        var handles = LayoutHandles.Build(path);

        Assert.Equal(3, handles.Count(h => h.Kind == LayoutHandleKind.Vertex));
        Assert.Equal(2, handles.Count(h => h.Kind == LayoutHandleKind.EdgeMidpoint));
    }

    [Fact]
    public void ViaAndLabel_HaveNoHandles()
    {
        Assert.Empty(LayoutHandles.Build(new ViaShape { X = 0, Y = 0, PadSize = 100, DrillSize = 50 }));
        Assert.Empty(LayoutHandles.Build(new LabelShape { X = 0, Y = 0, Text = "P1", Height = 500 }));
    }

    [Fact]
    public void ArcHandle_BulgeZero_SitsAtChordMidpoint()
    {
        var curve = new CurveShape
        {
            Xy = [0, 0, 1000, 0, 1000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        var bulgeHandle = Assert.Single(LayoutHandles.Build(curve), h => h.Kind == LayoutHandleKind.Bulge);

        Assert.Equal(500, bulgeHandle.X); Assert.Equal(0, bulgeHandle.Y);
    }
}
