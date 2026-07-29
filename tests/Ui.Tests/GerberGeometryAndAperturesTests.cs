using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

public class GerberGeometryAndAperturesTests
{
    [Fact]
    public void RoundedRectRing_FourLinesFourArcs_AllPositiveKappaBulge()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 2_000 };
        var ring = GerberGeometry.RoundedRectRing(rr);

        Assert.Equal(8, ring.Count);
        Assert.Equal(4, ring.FindAll(e => e.Kind == EdgeKind.Line).Count);
        var arcs = ring.FindAll(e => e.Kind == EdgeKind.Arc);
        Assert.Equal(4, arcs.Count);
        Assert.All(arcs, a => Assert.Equal(0.41421356237309515, a.Bulge, precision: 12));
    }

    [Fact]
    public void RoundedRectRing_ZeroCornerRadius_FourLinesNoArcs()
    {
        var rr = new RoundedRectShape { X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 0 };
        var ring = GerberGeometry.RoundedRectRing(rr);
        Assert.Equal(4, ring.Count);
        Assert.All(ring, e => Assert.Equal(EdgeKind.Line, e.Kind));
    }

    [Fact]
    public void FlattenCubicsInRing_ReplacesOnlyCubicEdges_ArcsPassThroughUntouched()
    {
        var ring = new List<GerberGeometry.RingEdge>
        {
            new(0, 0, 1000, 0, EdgeKind.Line, 0, 0, 0, 0, 0),
            new(1000, 0, 2000, 1000, EdgeKind.Arc, 0.5, 0, 0, 0, 0),
            new(2000, 1000, 3000, 1000, EdgeKind.Cubic, 0, 2200, 1000, 2800, 1000),
            new(3000, 1000, 0, 0, EdgeKind.Line, 0, 0, 0, 0, 0),
        };

        var flattened = GerberGeometry.FlattenCubicsInRing(ring, 100);

        Assert.DoesNotContain(flattened, e => e.Kind == EdgeKind.Cubic);
        // The original Line and Arc edges pass through byte-identical, in order.
        Assert.Equal(ring[0], flattened[0]);
        Assert.Equal(ring[1], flattened[1]);
        // The Cubic edge is replaced by >=1 Line edges whose chain starts where the cubic started...
        Assert.Equal(2000, flattened[2].X0);
        Assert.Equal(1000, flattened[2].Y0);
        // ...and the trailing original Line edge still appears last, untouched.
        Assert.Equal(ring[3], flattened[^1]);
    }

    [Fact]
    public void CircleAperture_SameDiameter_ReturnsSameCode()
    {
        var table = new GerberApertureTable();
        int a = table.CircleAperture(500_000);
        int b = table.CircleAperture(500_000);
        Assert.Equal(a, b);
        Assert.Single(table.Ordered);
    }

    [Fact]
    public void CircleAperture_DifferentDiameters_DedupedSeparately_SequentialFromD10()
    {
        var table = new GerberApertureTable();
        int a = table.CircleAperture(500_000);
        int b = table.CircleAperture(300_000);
        int c = table.CircleAperture(500_000); // repeat of `a`'s diameter

        Assert.NotEqual(a, b);
        Assert.Equal(a, c);
        Assert.Equal(10, a);
        Assert.Equal(11, b);
        Assert.Equal(2, table.Ordered.Count);
    }

    [Fact]
    public void CircleAperture_NonPositiveDiameter_ClampsToOneDbu()
    {
        var table = new GerberApertureTable();
        table.CircleAperture(0);
        Assert.Equal(1, table.Ordered[0].DiameterDbu);
    }
}
