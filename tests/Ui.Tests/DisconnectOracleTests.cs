using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 2 oracle for the Component Disconnect feature.
///
/// Documents the target behavior across the four filter boundaries:
///   1. Render mapping  — detached port always reads Unconnected (ToRenderComponent)
///   2. Connectivity    — detached port excluded from conPointCounts + dot pass (ComputeConnectivityGeometry)
///   3. Extraction      — detached port gets its own unique net; not unioned with overlapping wire/pin (NetExtractor)
///   4. Drag-follow     — detached port drives no wire follow and no auto-wire on separation (SchematicViewModel)
///
/// Expected RED before L2b filters are wired, GREEN after.
/// Existing connectivity / extraction / drag oracles must stay GREEN throughout.
/// </summary>
public class DisconnectOracleTests
{
    // Resistor at (cx, cy).
    //   Port 0 (local "1") → world (cx, cy-200)
    //   Port 1 (local "2") → world (cx, cy+200)
    private static EditableComponent MakeResistor(double cx, double cy, string name)
        => new() { Symbol = SymbolKind.Resistor, X = cx, Y = cy, InstanceName = name };

    private static bool Near(double a, double b) => System.Math.Abs(a - b) < 1.0;

    // ── Pin-on-pin: baseline ──────────────────────────────────────────────────

    /// <summary>Sanity: both coincident ports read Connected before any disconnect.</summary>
    [Fact]
    public void PinOnPin_Baseline_BothPortsConnected()
    {
        // R1 port1 at (0, 200).  R2 port0 at (0, 200).  Coincident, no wire.
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0,   "R1");
        var r2 = MakeResistor(0, 400, "R2");
        model.Components.Add(r1);
        model.Components.Add(r2);

        var (render, _) = model.BuildRenderModel();
        var rr1 = render.Components.First(c => c.Id == r1.Id);
        var rr2 = render.Components.First(c => c.Id == r2.Id);

        Assert.Equal(PortConnectionState.Connected, rr1.Ports[1].State);
        Assert.Equal(PortConnectionState.Connected, rr2.Ports[0].State);
    }

    // ── Pin-on-pin: after Disconnect ──────────────────────────────────────────

    /// <summary>
    /// After disconnecting R1, both of R1's ports must render Unconnected.
    /// R2's port0 (whose only neighbor was R1's now-detached port1) also reads Unconnected.
    /// </summary>
    [Fact]
    public void PinOnPin_AfterDisconnect_DetachedPinsUnconnected()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0,   "R1");
        var r2 = MakeResistor(0, 400, "R2");
        model.Components.Add(r1);
        model.Components.Add(r2);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        var (render, _) = model.BuildRenderModel();
        var rr1 = render.Components.First(c => c.Id == r1.Id);
        var rr2 = render.Components.First(c => c.Id == r2.Id);

        // Filter 1: detached ports must render Unconnected regardless of geometry.
        Assert.Equal(PortConnectionState.Unconnected, rr1.Ports[0].State);
        Assert.Equal(PortConnectionState.Unconnected, rr1.Ports[1].State);

        // Filter 2 consequence: R2's port0 is now the only occupant of its P-cell →
        // conPointCounts[(0,2)] == 1 < 2 → Unconnected.
        Assert.Equal(PortConnectionState.Unconnected, rr2.Ports[0].State);
    }

    /// <summary>
    /// After disconnecting R1, the detached port1 must extract to its own net,
    /// not shared with the coincident R2 port0.
    /// </summary>
    [Fact]
    public void PinOnPin_AfterDisconnect_ExtractionOwnsOwnNet()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0,   "R1");
        var r2 = MakeResistor(0, 400, "R2");
        model.Components.Add(r1);
        model.Components.Add(r2);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        var result = NetExtractor.Extract(model);
        var tb     = result.TestBench;
        var inst1  = tb.Instances.First(i => i.InstanceName == "R1");
        var inst2  = tb.Instances.First(i => i.InstanceName == "R2");

        // Filter 3: R1 port1 synthetic key must not union with R2 port0's P-cell.
        Assert.NotEqual(inst1.NetBindings[1], inst2.NetBindings[0]);
    }

    /// <summary>
    /// After disconnecting R1, dragging R1 must NOT create an auto-wire from the old
    /// contact point to R1's new port1 position.
    /// </summary>
    [Fact]
    public void PinOnPin_AfterDisconnect_DragNoAutoWire()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0,   "R1");
        var r2 = MakeResistor(0, 400, "R2");
        model.Components.Add(r1);
        model.Components.Add(r2);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        int wiresBefore = model.Wires.Count;   // 0

        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(dx: 0, dy: 200); // R1 → (0,200); port1 → (0,400)

        // Filter 4: no pin-on-pin contact recorded for detached port → no auto-wire formed.
        Assert.Equal(wiresBefore, model.Wires.Count);
    }

    // ── Pin-on-wire: baseline ────────────────────────────────────────────────

    /// <summary>Sanity: port on wire endpoint reads Connected before any disconnect.</summary>
    [Fact]
    public void PinOnWire_Baseline_PortConnected()
    {
        // R1 port1 at (0, 200).  Wire endpoint also at (0, 200).
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (0.0, 600.0)]);
        model.Wires.Add(wire);

        var (render, _) = model.BuildRenderModel();
        var rr1 = render.Components.First(c => c.Id == r1.Id);

        Assert.Equal(PortConnectionState.Connected, rr1.Ports[1].State);
    }

    // ── Pin-on-wire: after Disconnect ────────────────────────────────────────

    /// <summary>
    /// After disconnecting R1, both of its ports must render Unconnected even though
    /// port1 is geometrically coincident with a wire endpoint.
    /// </summary>
    [Fact]
    public void PinOnWire_AfterDisconnect_PortUnconnected()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (0.0, 600.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        var (render, _) = model.BuildRenderModel();
        var rr1 = render.Components.First(c => c.Id == r1.Id);

        // Filter 1: detached ports render Unconnected regardless of wire geometry.
        Assert.Equal(PortConnectionState.Unconnected, rr1.Ports[0].State);
        Assert.Equal(PortConnectionState.Unconnected, rr1.Ports[1].State);
    }

    /// <summary>
    /// After disconnecting R1, the detached port1 must extract to its own net,
    /// not shared with R2 which is wired to the same segment's far end.
    /// </summary>
    [Fact]
    public void PinOnWire_AfterDisconnect_ExtractionOwnsOwnNet()
    {
        // R1 port1 at (0,200) — on wire start.
        // Wire: (0,200) → (0,600).
        // R2 port0 at (0,600) — on wire end.  R1 and R2 are in the same net pre-disconnect.
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0,   "R1");
        var r2 = MakeResistor(0, 800, "R2");
        model.Components.Add(r1);
        model.Components.Add(r2);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (0.0, 600.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        var result = NetExtractor.Extract(model);
        var tb     = result.TestBench;
        var inst1  = tb.Instances.First(i => i.InstanceName == "R1");
        var inst2  = tb.Instances.First(i => i.InstanceName == "R2");

        // Filter 3: R1 port1 (detached) must not be in R2 port0's net (the wire net).
        Assert.NotEqual(inst1.NetBindings[1], inst2.NetBindings[0]);
    }

    /// <summary>
    /// After disconnecting R1, dragging R1 must NOT cause the wire endpoint to follow.
    /// The wire must remain at its original position.
    /// </summary>
    [Fact]
    public void PinOnWire_AfterDisconnect_DragNoWireFollow()
    {
        // R1 port1 at (0,200) on wire start.  Wire: (0,200) → (0,600).
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (0.0, 600.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(dx: 0, dy: -200); // R1 → (0,-200); port1 → (0,0)

        // Filter 4: BuildPortMoves skips detached port1 → no portMove → wire doesn't follow.
        Assert.True(Near(wire.Points[0].Y, 200.0),
            $"Wire start must stay at y=200 but is y={wire.Points[0].Y}");
    }

    // ── Layer 3: clear-on-move lifecycle ────────────────────────────────────────

    /// <summary>
    /// Detached state persists at rest — no move means no clear.
    /// This PASSES before L3b (it just verifies the existing model behaviour).
    /// </summary>
    [Fact]
    public void Lifecycle_PersistsAtRest()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        Assert.NotEmpty(r1.DetachedPorts);

        // Don't move — state must still be detached.
        Assert.NotEmpty(r1.DetachedPorts);
    }

    /// <summary>
    /// DetachedPorts must be empty after the component's next committed move.
    /// RED before L3b (clearing never happens without the lifecycle hook).
    /// </summary>
    [Fact]
    public void Lifecycle_ClearsOnMove()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        Assert.NotEmpty(r1.DetachedPorts);

        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(dx: 0, dy: 300);

        Assert.Empty(r1.DetachedPorts);
    }

    /// <summary>
    /// Undo of the move must restore DetachedPorts to the pre-move (disconnected) state.
    /// RED before L3b.
    /// </summary>
    [Fact]
    public void Lifecycle_Undo_RestoresDetached()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(dx: 0, dy: 300);
        Assert.Empty(r1.DetachedPorts);   // clears on move (L3b)

        vm.UndoRedo.Undo();
        Assert.NotEmpty(r1.DetachedPorts);  // undo restores detached state
    }

    /// <summary>
    /// Only the component that moved clears — a disconnected-but-stationary component
    /// must retain its DetachedPorts.
    /// RED before L3b.
    /// </summary>
    [Fact]
    public void Lifecycle_OnlyMovedComponentClears()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0,   0, "R1");
        var r2 = MakeResistor(0, 800, "R2");
        model.Components.Add(r1);
        model.Components.Add(r2);

        var vm = new SchematicViewModel(model);

        // Disconnect both components.
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();
        vm.Selection.SelectOne(r2.Id);
        vm.DisconnectSelection();

        // Move only R1.
        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(dx: 0, dy: 300);

        Assert.Empty(r1.DetachedPorts);     // moved → cleared
        Assert.NotEmpty(r2.DetachedPorts);  // not moved → still detached
    }

    /// <summary>
    /// After disconnect + drag onto a wire, the port reads Connected (flags cleared,
    /// geometry rules at the new position).
    /// RED before L3b (flags never clear → render stays Unconnected even on the wire).
    /// </summary>
    [Fact]
    public void Lifecycle_DragOntoWire_ConnectedAfterMove()
    {
        // R1 at (0,0): port1 at (0,200).
        // Wire at (0,500) → (0,900) — R1's port1 not touching it initially.
        // After disconnect + drag (0,300): R1 at (0,300), port1 at (0,500) = wire start.
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 500.0), (0.0, 900.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(dx: 0, dy: 300);

        var (render, _) = model.BuildRenderModel();
        var rr1 = render.Components.First(c => c.Id == r1.Id);

        // Flags cleared → geometry rules → port1 at (0,500) = wire start → Connected.
        Assert.Equal(PortConnectionState.Connected, rr1.Ports[1].State);
    }

    /// <summary>
    /// After disconnect + drag to empty space, the port reads Unconnected (geometry,
    /// no wire at the new position). Passes before AND after L3b (both paths → Unconnected).
    /// Included as documentation of the expected stable state.
    /// </summary>
    [Fact]
    public void Lifecycle_DragToEmpty_UnconnectedAfterMove()
    {
        var model = new SchematicEditModel();
        var r1 = MakeResistor(0, 0, "R1");
        model.Components.Add(r1);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(r1.Id);
        vm.DisconnectSelection();

        vm.Selection.SelectOne(r1.Id);
        vm.SimulateDragCommit(dx: 0, dy: 300);

        var (render, _) = model.BuildRenderModel();
        var rr1 = render.Components.First(c => c.Id == r1.Id);

        // Empty space → Unconnected regardless of detach state.
        Assert.Equal(PortConnectionState.Unconnected, rr1.Ports[0].State);
        Assert.Equal(PortConnectionState.Unconnected, rr1.Ports[1].State);
    }
}
