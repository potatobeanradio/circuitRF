using System;
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

    // Owner request 2026-09-04: a corner-radius grip in EVERY corner, not only the top-left one.
    // Each sits R along the horizontal edge measured from ITS OWN corner, so the two on the right are
    // at X2-R — which is what makes their drag direction the mirror of the left pair's.
    [Fact]
    public void RoundedRect_FourCornersPlusFourEdgeMidpointsPlusACornerRadiusGripPerCorner()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, CornerRadius = 200 };
        var handles = LayoutHandles.Build(rr);

        Assert.Equal(12, handles.Count);
        Assert.Equal(4, handles.Count(h => h.Kind == LayoutHandleKind.Vertex));
        Assert.Equal(4, handles.Count(h => h.Kind == LayoutHandleKind.EdgeMidpoint));

        var cr = handles.Where(h => h.Kind == LayoutHandleKind.CornerRadius).ToList();
        Assert.Equal(4, cr.Count);
        Assert.Contains(cr, h => h.Index == 0 && h.X == 200  && h.Y == 0);     // (X1,Y1) corner
        Assert.Contains(cr, h => h.Index == 1 && h.X == 800  && h.Y == 0);     // (X2,Y1) corner
        Assert.Contains(cr, h => h.Index == 2 && h.X == 800  && h.Y == 1000);  // (X2,Y2) corner
        Assert.Contains(cr, h => h.Index == 3 && h.X == 200  && h.Y == 1000);  // (X1,Y2) corner
    }

    // The whole point of four grips is that each is AT the corner it controls; a shared "always at
    // X1+R" position would put all four on the left-hand edge and make the Index meaningless.
    [Fact]
    public void RoundedRect_EachCornerRadiusGrip_SitsOnItsOwnCorner()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 4000, Y2 = 2000, CornerRadius = 500 };
        var cr = LayoutHandles.Build(rr).Where(h => h.Kind == LayoutHandleKind.CornerRadius).ToList();

        foreach (var h in cr)
        {
            // The nearest rect corner to each grip is the one whose Index it carries.
            var corner = h.Index switch
            {
                0 => (X: 0L,    Y: 0L),
                1 => (X: 4000L, Y: 0L),
                2 => (X: 4000L, Y: 2000L),
                _ => (X: 0L,    Y: 2000L),
            };
            Assert.Equal(500, Math.Abs(h.X - corner.X));
            Assert.Equal(0,   Math.Abs(h.Y - corner.Y));
        }
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
