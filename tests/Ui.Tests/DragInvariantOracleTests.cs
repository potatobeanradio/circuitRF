using System.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Layer 0 oracle — documents current drag-invariant behavior for the four cases in
/// docs/design/placement-connectivity-and-drag-follow.md.
///
/// Tests run headless (no Avalonia rendering) via SchematicViewModel.SimulateDragCommit,
/// which invokes the identical commit path a real UI drag uses.
///
/// Expected pass/fail at Layer 0 (before any Layer 1-3 fixes):
///   Case 1a (pin on wire endpoint):      PASS  — endpoint-follow lands the wire at the new pin
///   Case 1b (pin on wire body / T-body): PASS  — RouteBodyFollow re-routes through the new pin
///   Case 2  (pin-on-pin → auto-wire):    FAIL  — no auto-wire; pins separate, unconnected
///   Case 3  (wire drag, pin held):       PASS  — StartPinned=true; endpoint stays on pin
/// </summary>
public class DragInvariantOracleTests
{
    // Resistor at (cx, cy).  Port 1 → world (cx, cy-200).  Port 2 → world (cx, cy+200).
    private static EditableComponent MakeResistor(double cx, double cy)
        => new() { Symbol = SymbolKind.Resistor, X = cx, Y = cy, InstanceName = "R?" };

    private static bool Near(double a, double b) => System.Math.Abs(a - b) < 1.0;

    // ── Case 1a — pin on wire ENDPOINT ─────────────────────────────────────────

    /// <summary>
    /// Component pin is at a wire endpoint.  Drag the component → wire endpoint follows the pin.
    /// Invariant: the pin stays Connected after the drag.
    /// </summary>
    [Fact]
    public void Case1a_ComponentDrag_WireEndpointFollowsPin_StaysConnected()
    {
        // Resistor A at (0,0): port2 at world (0, 200).
        // Wire: endpoint[0] = (0, 200) = port2; endpoint[1] = (600, 200).
        var model = new SchematicEditModel();
        var compA = MakeResistor(0, 0);
        model.Components.Add(compA);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (600.0, 200.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(compA.Id);
        vm.SimulateDragCommit(dx: 200, dy: 0);   // A → (200, 0); port2 → (200, 200)

        var (render, _) = model.BuildRenderModel();
        var rA = render.Components.First(c => c.Id == compA.Id);

        // port2 (index 1) must remain Connected — wire endpoint followed the pin.
        Assert.Equal(PortConnectionState.Connected, rA.Ports[1].State);
    }

    // ── Case 1b — pin on wire BODY (T-junction) ────────────────────────────────

    /// <summary>
    /// Component pin sits on a wire's mid-span (T-junction).  Drag the component → the wire
    /// re-routes through the new pin position (RouteBodyFollow).
    /// Invariant: the pin stays Connected after the drag.
    /// </summary>
    [Fact]
    public void Case1b_ComponentDrag_TJunctionBodyFollowsPin_StaysConnected()
    {
        // Resistor A at (0,0): port1 at world (0, -200).
        // Wire: horizontal from (-400, -200) to (400, -200);  (0,-200) is on the interior.
        var model = new SchematicEditModel();
        var compA = MakeResistor(0, 0);
        model.Components.Add(compA);
        var wire = new EditableWire();
        wire.Points.AddRange([(-400.0, -200.0), (400.0, -200.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(compA.Id);
        vm.SimulateDragCommit(dx: 0, dy: 200);   // A → (0, 200); port1 → (0, 0)

        var (render, _) = model.BuildRenderModel();
        var rA = render.Components.First(c => c.Id == compA.Id);

        // port1 (index 0) must remain Connected — wire re-routed through new pin position.
        Assert.Equal(PortConnectionState.Connected, rA.Ports[0].State);
    }

    // ── Case 2 — pin-on-pin → auto-wire on separation ──────────────────────────

    /// <summary>
    /// Two pins are coincident (pin-on-pin, no wire).  Drag one component away → an auto-wire
    /// must be created so both pins stay Connected.
    /// Currently FAILS: no auto-wire is created; both pins become Unconnected after the drag.
    /// </summary>
    [Fact]
    public void Case2_ComponentDrag_PinOnPinSeparates_AutoWireConnectsBothPins()
    {
        // Resistor A at (0,0): port2 at (0, 200).
        // Resistor B at (0,400): port1 at (0, 200).  Pin-on-pin, no wire.
        var model = new SchematicEditModel();
        var compA = MakeResistor(0, 0);
        var compB = MakeResistor(0, 400);
        model.Components.Add(compA);
        model.Components.Add(compB);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(compA.Id);
        vm.SimulateDragCommit(dx: 200, dy: 0);   // A → (200, 0); port2 → (200, 200); B.port1 stays (0, 200)

        var (render, _) = model.BuildRenderModel();
        var rA = render.Components.First(c => c.Id == compA.Id);
        var rB = render.Components.First(c => c.Id == compB.Id);

        // Both pins must be Connected — an auto-wire should connect (0,200)↔(200,200).
        Assert.Equal(PortConnectionState.Connected, rA.Ports[1].State);   // A port2
        Assert.Equal(PortConnectionState.Connected, rB.Ports[0].State);   // B port1
    }

    // ── Case 3 — wire drag; endpoint on a component pin must stay pinned ────────

    /// <summary>
    /// Wire endpoint coincides with a component pin.  Drag the WIRE → the connected endpoint
    /// stays pinned at the pin (new segments form to bridge the rest of the motion).
    /// Invariant: the pin stays Connected after the drag.
    /// </summary>
    [Fact]
    public void Case3_WireDrag_ConnectedEndpointStaysPinnedToComponentPin_StaysConnected()
    {
        // Resistor A at (0,0): port2 at (0, 200).
        // Wire: endpoint[0] = (0, 200) = port2 of A;  endpoint[1] = (0, 600).
        var model = new SchematicEditModel();
        var compA = MakeResistor(0, 0);
        model.Components.Add(compA);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (0.0, 600.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(wire.Id);          // select the WIRE, not the component
        vm.SimulateDragCommit(dx: 200, dy: 0);   // drag right; endpoint[0] must stay pinned at (0,200)

        var (render, _) = model.BuildRenderModel();
        var rA = render.Components.First(c => c.Id == compA.Id);

        // port2 (index 1) must remain Connected — wire endpoint stayed pinned at the pin.
        Assert.Equal(PortConnectionState.Connected, rA.Ports[1].State);

        // Also verify the wire endpoint actually stayed at the component pin.
        var draggedWire = model.Wires.First(w => w.Id == wire.Id);
        Assert.True(Near(draggedWire.Points[0].X, 0) && Near(draggedWire.Points[0].Y, 200),
            $"Wire endpoint[0] should stay at (0,200) but is ({draggedWire.Points[0].X},{draggedWire.Points[0].Y})");
    }

    // ── Case 4 — shared-point: stationary pin wins; auto-wire forms ──────────────

    /// <summary>
    /// Regression oracle for the shared-point bug (fixed).
    ///
    /// Geometry:
    ///   C1 at (0,0)   — port1 (bottom) at (0,200)
    ///   C2 at (400,0) — port1 (bottom) at (400,200)
    ///   Wire: (0,200) to (400,200) — C1-bottom to C2-bottom
    ///   C3 at (0,400) — port0 (top) at (0,200) — pin-on-pin with C1-bottom
    ///
    /// Action: select C3, drag dy=+200.
    ///   C3-top moves from (0,200) to (0,400).
    ///
    /// Invariant: the wire endpoint belongs to the stationary C1 pin — it must NOT
    /// follow C3. A new auto-wire must form to keep C3 connected.
    ///
    /// Asserts correct post-fix behavior (permanent regression oracle).
    /// </summary>
    [Fact]
    public void Case4_SharedPoint_WireStaysOnStationaryPin_AutoWireConnectsMovingComponent()
    {
        var model = new SchematicEditModel();
        var c1 = MakeResistor(0,   0);    // port1 (bottom) at (0,200)
        var c2 = MakeResistor(400, 0);    // port1 (bottom) at (400,200)
        var c3 = MakeResistor(0, 400);    // port0 (top)    at (0,200) — pin-on-pin with C1

        model.Components.Add(c1);
        model.Components.Add(c2);
        model.Components.Add(c3);

        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (400.0, 200.0)]);
        model.Wires.Add(wire);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(c3.Id);
        vm.SimulateDragCommit(dx: 0, dy: 200);   // C3 moves to (0,600); C3-top to (0,400)

        var (render, _) = model.BuildRenderModel();
        var rC1 = render.Components.First(c => c.Id == c1.Id);
        var rC2 = render.Components.First(c => c.Id == c2.Id);
        var rC3 = render.Components.First(c => c.Id == c3.Id);

        // All three pins must stay Connected.
        Assert.Equal(PortConnectionState.Connected, rC1.Ports[1].State);
        Assert.Equal(PortConnectionState.Connected, rC2.Ports[1].State);
        Assert.Equal(PortConnectionState.Connected, rC3.Ports[0].State);

        // Original wire is unchanged: endpoints still at (0,200) and (400,200).
        var origWire = model.Wires.First(w => w.Id == wire.Id);
        Assert.True(Near(origWire.Points[0].X, 0)    && Near(origWire.Points[0].Y, 200),
            $"Wire start must remain at (0,200), got ({origWire.Points[0].X},{origWire.Points[0].Y})");
        Assert.True(Near(origWire.Points[^1].X, 400) && Near(origWire.Points[^1].Y, 200),
            $"Wire end must remain at (400,200), got ({origWire.Points[^1].X},{origWire.Points[^1].Y})");

        // A new auto-wire must connect (0,200) to (0,400).
        Assert.Equal(2, model.Wires.Count);
        var newWire = model.Wires.First(w => w.Id != wire.Id);
        Assert.True(newWire.Points.Any(p => Near(p.X, 0) && Near(p.Y, 200)),
            "Auto-wire must have an endpoint at C1-bottom (0,200)");
        Assert.True(newWire.Points.Any(p => Near(p.X, 0) && Near(p.Y, 400)),
            "Auto-wire must have an endpoint at C3-new-top (0,400)");
    }

    // ── Case 5 — wire drag; merge must not bury a component port ─────────────

    /// <summary>
    /// Regression oracle for the merge-buries-port bug (fixed).
    ///
    /// Geometry:
    ///   R  = MakeResistor(0,-200) → port1 (bottom) at P = (0,0).
    ///   Wh = wire [(0,0),(400,0)] — horizontal from P rightward.
    ///   Wv = wire [(0,0),(0,400)] — vertical from P downward.
    ///
    /// Action: select Wv; drag dx=+200.
    ///
    /// Invariant: Wh is unchanged; R's port1 stays Connected — the merge did not
    /// normalize the shared junction at P out of existence.
    /// </summary>
    [Fact]
    public void Case5_WireDrag_MergeMustNotBuryComponentPort()
    {
        var model = new SchematicEditModel();
        var r  = MakeResistor(0, -200);    // port1 (bottom) at (0,0)
        var wh = new EditableWire();
        var wv = new EditableWire();
        wh.Points.AddRange([(0.0, 0.0), (400.0,   0.0)]);
        wv.Points.AddRange([(0.0, 0.0), (  0.0, 400.0)]);
        model.Components.Add(r);
        model.Wires.Add(wh);
        model.Wires.Add(wv);

        var vm = new SchematicViewModel(model);
        vm.Selection.SelectOne(wv.Id);
        vm.SimulateDragCommit(dx: 200, dy: 0);   // Wv moves right; merge must not drop (0,0) from the net

        var (render, _) = model.BuildRenderModel();
        var rR = render.Components.First(c => c.Id == r.Id);

        // R's port1 must remain Connected — merge did not normalize away the junction.
        Assert.Equal(PortConnectionState.Connected, rR.Ports[1].State);

        // Both wires still exist — the merge was suppressed.
        Assert.Equal(2, model.Wires.Count);

        // Wh is unchanged: still (0,0) → (400,0).
        var origWh = model.Wires.First(w => w.Id == wh.Id);
        Assert.True(Near(origWh.Points[0].X, 0)    && Near(origWh.Points[0].Y, 0),
            $"Wh start must stay at (0,0), got ({origWh.Points[0].X},{origWh.Points[0].Y})");
        Assert.True(Near(origWh.Points[^1].X, 400) && Near(origWh.Points[^1].Y, 0),
            $"Wh end must stay at (400,0), got ({origWh.Points[^1].X},{origWh.Points[^1].Y})");
    }
}
