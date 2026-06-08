using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class WireGeometryTests
{
    // ── NormalizePoints ───────────────────────────────────────────────────────

    [Fact]
    public void NormalizePoints_LShape_Unchanged()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (100, 0), (100, 100) };
        var result = WireGeometry.NormalizePoints(pts);
        Assert.Equal(3, result.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((100.0, 0.0), result[1]);
        Assert.Equal((100.0, 100.0), result[2]);
    }

    [Fact]
    public void NormalizePoints_ThreeCollinearHorizontal_DropsMidpoint()
    {
        // A-B-C all on Y=0: B is redundant
        var pts = new List<(double X, double Y)> { (0, 0), (100, 0), (200, 0) };
        var result = WireGeometry.NormalizePoints(pts);
        Assert.Equal(2, result.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((200.0, 0.0), result[1]);
    }

    [Fact]
    public void NormalizePoints_ThreeCollinearVertical_DropsMidpoint()
    {
        var pts = new List<(double X, double Y)> { (0, 0), (0, 100), (0, 200) };
        var result = WireGeometry.NormalizePoints(pts);
        Assert.Equal(2, result.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((0.0, 200.0), result[1]);
    }

    [Fact]
    public void NormalizePoints_ZeroLengthDuplicate_DropsDuplicate()
    {
        // A, A, B — the repeated A is zero-length
        var pts = new List<(double X, double Y)> { (0, 0), (0, 0), (100, 0) };
        var result = WireGeometry.NormalizePoints(pts);
        Assert.Equal(2, result.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((100.0, 0.0), result[1]);
    }

    [Fact]
    public void NormalizePoints_ZeroLengthBothPoints_CollapsesToOne()
    {
        // [A, A] — zero-length wire: both points are the same
        var pts = new List<(double X, double Y)> { (50, 50), (50, 50) };
        var result = WireGeometry.NormalizePoints(pts);
        Assert.Single(result);
    }

    [Fact]
    public void NormalizePoints_FiveCollinearPoints_CollapsesToTwo()
    {
        var pts = new List<(double X, double Y)>
            { (0, 0), (50, 0), (100, 0), (150, 0), (200, 0) };
        var result = WireGeometry.NormalizePoints(pts);
        Assert.Equal(2, result.Count);
        Assert.Equal((0.0, 0.0),   result[0]);
        Assert.Equal((200.0, 0.0), result[1]);
    }

    // ── OrthogonalRoute ───────────────────────────────────────────────────────

    [Fact]
    public void OrthogonalRoute_StraightHorizontal_TwoPoints()
    {
        var pts = WireGeometry.OrthogonalRoute(0, 0, 200, 0);
        Assert.Equal(2, pts.Count);
        Assert.Equal((0.0, 0.0), pts[0]);
        Assert.Equal((200.0, 0.0), pts[1]);
    }

    [Fact]
    public void OrthogonalRoute_StraightVertical_TwoPoints()
    {
        var pts = WireGeometry.OrthogonalRoute(0, 0, 0, 300);
        Assert.Equal(2, pts.Count);
        Assert.Equal((0.0, 0.0), pts[0]);
        Assert.Equal((0.0, 300.0), pts[1]);
    }

    [Fact]
    public void OrthogonalRoute_DiagonalInput_ThreePoints()
    {
        var pts = WireGeometry.OrthogonalRoute(0, 0, 200, 100);
        Assert.Equal(3, pts.Count);
        // H-first: go horizontally to (200,0) then vertically to (200,100)
        Assert.Equal((0.0, 0.0), pts[0]);
        Assert.Equal((200.0, 0.0), pts[1]);
        Assert.Equal((200.0, 100.0), pts[2]);
    }

    [Fact]
    public void PointOnWire_OnSegment_ReturnsTrue()
    {
        var wire = new EditableWire();
        wire.Points.Add((0, 0));
        wire.Points.Add((100, 0));
        Assert.True(WireGeometry.PointOnWire(wire, 50, 0));
    }

    [Fact]
    public void PointOnWire_OffSegment_ReturnsFalse()
    {
        var wire = new EditableWire();
        wire.Points.Add((0, 0));
        wire.Points.Add((100, 0));
        Assert.False(WireGeometry.PointOnWire(wire, 50, 50));
    }

    [Fact]
    public void PointOnWire_NearEndpoint_ReturnsTrue()
    {
        var wire = new EditableWire();
        wire.Points.Add((0, 0));
        wire.Points.Add((100, 0));
        // Within tolerance of 6 world units
        Assert.True(WireGeometry.PointOnWire(wire, 3, 1));
    }

    [Fact]
    public void FindConnectedPorts_WireAtResistorPort_FindsIt()
    {
        var model = new SchematicEditModel();
        var r1    = new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            X            = 0, Y = 0,
            Rotation     = SymbolRotation.R0,
        };
        model.Components.Add(r1);

        // Resistor port 0 is at local (-150, 0) → world (-150, 0) at rotation R0
        var wire = new EditableWire();
        wire.Points.Add((-150, 0));
        wire.Points.Add((-250, 0));
        model.Wires.Add(wire);

        var connected = WireGeometry.FindConnectedPorts(model);
        Assert.Contains(connected, p => p.CompId == r1.Id && p.PortIdx == 0);
    }
}
