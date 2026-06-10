using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 1 gate: union-find over on-P connection points → nets.
/// Verifies wire unions, T-junctions, crossing dot gating, and same-name label union.
/// Geometry: GridSize=100 (default). Resistor port 0 at local (0,−200) → world (X, Y−200).
/// </summary>
public class NetExtractorLayer1Tests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static EditableWire Wire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    private static EditableComponent Resistor(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    // Returns the net name of the k-th terminal of the instance named `name`.
    private static string NetOf(NetExtractor.ExtractionResult result, string name, int terminalIndex)
    {
        var inst = result.TestBench.Instances.First(i => i.InstanceName == name);
        return inst.NetBindings[terminalIndex];
    }

    // ── Test 1: two pins on the same wire share a net ────────────────────────

    [Fact]
    public void TwoPinsOnSameWire_SameNet()
    {
        var model = new SchematicEditModel();
        // R1 at (0,200): port0=(0,0), port1=(0,400).
        // R2 at (400,200): port0=(400,0), port1=(400,400).
        model.Components.Add(Resistor("R1", 0, 200));
        model.Components.Add(Resistor("R2", 400, 200));
        // Wire from R1.port0 (0,0) to R2.port0 (400,0).
        model.Wires.Add(Wire((0, 0), (400, 0)));

        var result = NetExtractor.Extract(model);

        Assert.Equal(NetOf(result, "R1", 0), NetOf(result, "R2", 0));
        // Unconnected far ends are different nets.
        Assert.NotEqual(NetOf(result, "R1", 1), NetOf(result, "R2", 1));
    }

    // ── Test 2: pins on unconnected wires are different nets ─────────────────

    [Fact]
    public void TwoPinsOnDifferentWires_DifferentNets()
    {
        var model = new SchematicEditModel();
        model.Components.Add(Resistor("R1", 0, 200));
        model.Components.Add(Resistor("R2", 400, 200));
        // Short wire at R1.port0, not reaching R2.
        model.Wires.Add(Wire((0, 0), (100, 0)));
        // Short wire at R2.port0, not connected to first wire.
        model.Wires.Add(Wire((300, 0), (400, 0)));

        var result = NetExtractor.Extract(model);

        Assert.NotEqual(NetOf(result, "R1", 0), NetOf(result, "R2", 0));
    }

    // ── Test 3: T-junction unions three incident wire ends ───────────────────

    [Fact]
    public void TJunction_UnionsThreeEnds()
    {
        var model = new SchematicEditModel();
        // Horizontal trunk: (0,0)→(400,0).
        // Vertical branch: (200,400)→(200,0) — endpoint (200,0) lies on trunk interior.
        model.Wires.Add(Wire((0, 0), (400, 0)));        // trunk
        model.Wires.Add(Wire((200, 400), (200, 0)));    // branch, ends at T-point

        // One resistor at each of the three incident wire ends.
        // R1 port0=(0,0), R2 port0=(400,0), R3 port0=(200,400).
        model.Components.Add(Resistor("R1", 0, 200));
        model.Components.Add(Resistor("R2", 400, 200));
        model.Components.Add(Resistor("R3", 200, 600));

        var result = NetExtractor.Extract(model);

        var netLeft   = NetOf(result, "R1", 0);
        var netRight  = NetOf(result, "R2", 0);
        var netBranch = NetOf(result, "R3", 0);

        Assert.Equal(netLeft, netRight);
        Assert.Equal(netLeft, netBranch);
    }

    // ── Test 4: un-dotted crossing stays two nets ────────────────────────────

    [Fact]
    public void UndottedCrossing_TwoNets()
    {
        var model = new SchematicEditModel();
        // Horizontal: (0,200)→(400,200), vertical: (200,0)→(200,400).
        // They cross at (200,200) — no wire has a vertex there.
        model.Wires.Add(Wire((0, 200), (400, 200)));
        model.Wires.Add(Wire((200, 0), (200, 400)));

        // R1.port0=(0,200): left end of horizontal. R2.port0=(200,0): top of vertical.
        model.Components.Add(Resistor("R1", 0, 400));
        model.Components.Add(Resistor("R2", 200, 200));

        var result = NetExtractor.Extract(model);

        Assert.NotEqual(NetOf(result, "R1", 0), NetOf(result, "R2", 0));
    }

    // ── Test 5: dotted crossing unions into one net ──────────────────────────

    [Fact]
    public void DottedCrossing_OneNet()
    {
        var model = new SchematicEditModel();
        model.Wires.Add(Wire((0, 200), (400, 200)));
        model.Wires.Add(Wire((200, 0), (200, 400)));
        // User dot at the crossing point connects the two wires.
        model.Dots.Add(new EditableDot { X = 200, Y = 200 });

        model.Components.Add(Resistor("R1", 0, 400));
        model.Components.Add(Resistor("R2", 200, 200));

        var result = NetExtractor.Extract(model);

        Assert.Equal(NetOf(result, "R1", 0), NetOf(result, "R2", 0));
    }

    // ── Test 6: same-name labels union disjoint wires into one net ───────────

    [Fact]
    public void SameNameLabels_UnionNets()
    {
        var model = new SchematicEditModel();
        // Two separate vertical wires with no physical connection.
        model.Wires.Add(Wire((0, 0), (0, 400)));
        model.Wires.Add(Wire((400, 0), (400, 400)));

        // R1.port0=(0,0) on left wire. R2.port0=(400,0) on right wire.
        model.Components.Add(Resistor("R1", 0, 200));
        model.Components.Add(Resistor("R2", 400, 200));

        // "vdd" label placed mid-segment on each wire (not on a vertex).
        model.NetLabels.Add(new EditableNetLabel { X = 0,   Y = 200, Name = "vdd" });
        model.NetLabels.Add(new EditableNetLabel { X = 400, Y = 200, Name = "vdd" });

        var result = NetExtractor.Extract(model);

        Assert.Equal("vdd", NetOf(result, "R1", 0));
        Assert.Equal("vdd", NetOf(result, "R2", 0));
        Assert.Equal(NetOf(result, "R1", 0), NetOf(result, "R2", 0));
    }
}
