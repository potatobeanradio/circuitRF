using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Commands;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Moving a wire segment must never detach it from another connection. Instead the wire bows out /
/// adds jogs (rubber-band) so T-junction, corner, and cross connections all survive the drag, and
/// one Undo restores everything.
/// </summary>
public class SegmentDragKeepsConnectionTests
{
    private static EditableWire MakeWire(params (double X, double Y)[] pts)
    {
        var w = new EditableWire();
        w.Points.AddRange(pts);
        return w;
    }

    // Resistor at (cx, cy).  Port 0 → world (cx, cy-200).  Port 1 → world (cx, cy+200).
    private static EditableComponent MakeResistor(double cx, double cy)
        => new() { Symbol = SymbolKind.Resistor, X = cx, Y = cy, InstanceName = "R?" };

    private static (SchematicEditModel Model, SchematicViewModel Vm, UndoRedoStack Undo) MakeVm(
        params EditableWire[] wires)
    {
        var model = new SchematicEditModel { GridSnap = false };
        foreach (var w in wires) model.Wires.Add(w);
        var vm   = new SchematicViewModel(model);
        return (model, vm, vm.UndoRedo);
    }

    private static void DragSegment(SchematicViewModel vm, double pressX, double pressY, double toX, double toY)
    {
        vm.OnPointerPressed(pressX, pressY, default);
        vm.OnPointerMoved(toX, toY, leftDown: true);
        vm.OnPointerReleased(toX, toY);
    }

    private static bool HasDotAt(SchematicViewModel vm, double x, double y)
        => vm.RenderModel!.ConnectionDots.Any(d => System.Math.Abs(d.X - x) < 1e-6 && System.Math.Abs(d.Y - y) < 1e-6);

    // ── T-junction: dragging the stem segment slides the T along the through-wire ──

    [Fact]
    public void DraggingStemSegment_SlidesTAlongThroughWire_StaysConnected()
    {
        // Through-wire extended to 400 so T at x=200 and T at x=300 are both on the interior
        // (not at endpoints) — required for P-multiple quantization to detect them as T-junctions.
        var through = MakeWire((0, 0), (400, 0));
        var stem    = MakeWire((200, 0), (200, 200));   // endpoint (200,0) on through's body → T
        var (model, vm, undo) = MakeVm(through, stem);
        Assert.True(HasDotAt(vm, 200, 0), "precondition: T-junction dot");

        // Grab the stem's (vertical) segment and drag it sideways by +100 (P-multiple delta).
        // The through-wire is parallel to the drag, so the T-point slides along it.
        DragSegment(vm, 200, 100, 300, 100);

        Assert.True(HasDotAt(vm, 300, 0), "the T slides along the through-wire, staying connected");
        Assert.Equal(2, model.FindWire(stem.Id)!.Points.Count);   // no jog — still a 2-point wire
        Assert.Single(vm.RenderModel!.ConnectionDots);

        undo.Undo();
        Assert.True(HasDotAt(vm, 200, 0));
        Assert.Equal(2, model.FindWire(stem.Id)!.Points.Count);
    }

    // ── Corner: dragging a wire joined at another wire's corner keeps it attached ──

    [Fact]
    public void DraggingWireJoinedAtCorner_KeepsConnection()
    {
        var bent = MakeWire((0, 0), (100, 0), (100, 100));   // corner at (100,0)
        var join = MakeWire((100, 0), (100, -80));           // ends at the corner
        var (model, vm, _) = MakeVm(bent, join);
        Assert.True(HasDotAt(vm, 100, 0), "precondition: corner junction dot");

        DragSegment(vm, 100, -40, 150, -40);   // drag join's vertical segment sideways

        Assert.True(HasDotAt(vm, 100, 0), "corner connection must survive the drag");
    }

    // ── Cross: dragging a crossing wire carries the user dot along ─────────────

    [Fact]
    public void DraggingCrossingWire_DotFollows_StaysConnected()
    {
        var h = MakeWire((0, 0), (300, 0));
        var v = MakeWire((100, -100), (100, 100));
        var (model, vm, undo) = MakeVm(h, v);
        vm.PlaceDot(100, 0);                       // user dot at the crossing
        Assert.True(HasDotAt(vm, 100, 0));

        // Drag V's segment sideways by +50 → the crossing (and its dot) should slide to x=150.
        DragSegment(vm, 100, 50, 150, 50);

        Assert.True(HasDotAt(vm, 150, 0), "the cross dot must ride the dragged wire");
        Assert.False(HasDotAt(vm, 100, 0));
        Assert.Single(model.Dots);                 // still exactly one user dot (not removed)

        undo.Undo();
        Assert.True(HasDotAt(vm, 100, 0));         // dot and wire restored together
        Assert.Equal(100.0, model.Dots.Single().X, 3);
    }

    // ── Live simplification: dragging a jog back to start collapses it ────────

    [Fact]
    public void DraggingPinnedSegmentBackToStart_SimplifiesLive()
    {
        // A wire joined at a corner (vertex → pinned). Dragging perpendicular jogs it; dragging
        // back collapses the jog live (no segments left stacked over the original line).
        var bent = MakeWire((0, 0), (100, 0), (100, 100));
        var join = MakeWire((100, 0), (100, -80));   // ends at the corner (100,0) → pinned
        var (model, vm, _) = MakeVm(bent, join);

        vm.OnPointerPressed(100, -40, default);        // grab join's vertical segment
        vm.OnPointerMoved(150, -40, leftDown: true);   // drag sideways → a jog forms
        Assert.True(model.FindWire(join.Id)!.Points.Count >= 3, "jogged mid-drag");

        vm.OnPointerMoved(100, -40, leftDown: true);   // back to the original x
        Assert.Equal(2, model.FindWire(join.Id)!.Points.Count);   // collapsed live to 2 points

        vm.OnPointerReleased(100, -40);
    }

    // ── The reported bug: a wire joining two VERTICAL wires slides, not bows ──

    [Fact]
    public void DraggingWireBetweenTwoVerticalWires_Slides_StaysTwoDots()
    {
        // H joins two vertical wires at their bodies → 2 T-dots. Dragging H down must slide the
        // connections along the verticals (H stays one straight segment) — exactly 2 dots, not 4.
        // Verticals extended to ±200 so the dragged T-points (y=100) remain on the interior
        // (not at endpoints) — required for P-multiple quantization to detect them as T-junctions.
        var v1 = MakeWire((0, -200), (0, 200));
        var v2 = MakeWire((200, -200), (200, 200));
        var h  = MakeWire((0, 0), (200, 0));
        var (model, vm, _) = MakeVm(v1, v2, h);
        Assert.Equal(2, vm.RenderModel!.ConnectionDots.Count);

        DragSegment(vm, 100, 0, 100, 100);   // drag H down by 100 (P-multiple delta)

        var rh = model.FindWire(h.Id)!;
        Assert.Equal(2, rh.Points.Count);                           // straight wire, no jogs
        Assert.Equal(2, vm.RenderModel!.ConnectionDots.Count);      // 2 dots, NOT 4
        Assert.True(HasDotAt(vm, 0, 100) && HasDotAt(vm, 200, 100)); // dots slid down with H
    }

    [Fact]
    public void SlidingClampsAtShorterWireEnd_NoConnectionLost()
    {
        // v1 is tall; v2 is short (only reaches y=40). Sliding H down must STOP at y=40 (v2's end)
        // so the connection to v2 is never lost — even though the user dragged far past it.
        var v1 = MakeWire((0, -100), (0, 100));
        var v2 = MakeWire((200, -100), (200, 40));     // shorter: top end at y=40
        var h  = MakeWire((0, 0), (200, 0));
        var (model, vm, _) = MakeVm(v1, v2, h);

        vm.OnPointerPressed(100, 0, default);
        vm.OnPointerMoved(100, 200, leftDown: true);   // try to drag H far past v2's end (no release)

        // Clamped to v2's end (y=40): both endpoints stop exactly there — neither slid off its wire.
        var rh = model.FindWire(h.Id)!;
        Assert.Equal(40.0, rh.Points[0].Y, 3);
        Assert.Equal(40.0, rh.Points[^1].Y, 3);

        vm.OnPointerReleased(100, 200);

        // v1 connection preserved as a T at the clamped y; v2 preserved via its (now shared) endpoint.
        Assert.True(HasDotAt(vm, 0, 40), "the connection to the tall wire is preserved at the clamp");
    }

    // ── Stem anchored at a component pin must not be dragged off it ───────────

    /// <summary>
    /// Regression oracle for the pin-stem drag bug (fixed).
    ///
    /// Geometry:
    ///   RL via MakeResistor(0,-200)   → port1 (bottom) at PL=(0,0).
    ///   RR via MakeResistor(400,-200) → port1 (bottom) at PR=(400,0).
    ///   Wh = wire [(0,0),(400,0)]              — PL → PR.
    ///   Wv = wire [(0,0),(400,0),(400,400)]    — the L: top at PL, corner at PR, drop down.
    ///
    /// Action: drag Wv's vertical drop (segment index 1) leftward to x=300.
    ///
    /// Invariant: Wh's endpoint at PR is a component pin — it must NOT be treated
    /// as a free stem and dragged to (300,0). RR's port1 stays Connected.
    /// </summary>
    [Fact]
    public void StemAnchoredAtComponentPin_MustNotBeDraggedOff()
    {
        var model = new SchematicEditModel { GridSnap = false };
        var rL = MakeResistor(0,   -200);   // port1 at (0,  0)
        var rR = MakeResistor(400, -200);   // port1 at (400,0)
        model.Components.Add(rL);
        model.Components.Add(rR);

        var wh = new EditableWire();
        wh.Points.AddRange([(0.0, 0.0), (400.0,   0.0)]);
        model.Wires.Add(wh);

        var wv = new EditableWire();
        wv.Points.AddRange([(0.0, 0.0), (400.0, 0.0), (400.0, 400.0)]);
        model.Wires.Add(wv);

        var vm = new SchematicViewModel(model);

        // Drag Wv's vertical drop segment (at x=400, from y=0 to y=400) leftward.
        DragSegment(vm, 400, 200, 300, 200);

        // RR's port1 (pin at PR=(400,0)) must remain Connected — the stem was not dragged off.
        var (render, _) = model.BuildRenderModel();
        var rRR = render.Components.First(c => c.Id == rR.Id);
        Assert.Equal(PortConnectionState.Connected, rRR.Ports[1].State);

        // Wh must not have been dragged: still (0,0) → (400,0).
        var origWh = model.FindWire(wh.Id)!;
        Assert.True(System.Math.Abs(origWh.Points[0].X -   0) < 1.0 &&
                    System.Math.Abs(origWh.Points[0].Y -   0) < 1.0,
            $"Wh start must stay at (0,0), got ({origWh.Points[0].X},{origWh.Points[0].Y})");
        Assert.True(System.Math.Abs(origWh.Points[^1].X - 400) < 1.0 &&
                    System.Math.Abs(origWh.Points[^1].Y -   0) < 1.0,
            $"Wh end must stay at (400,0), got ({origWh.Points[^1].X},{origWh.Points[^1].Y})");
    }

    // ── Option 2 slide-vs-bow: a wire dropping off a port slides into a clean T (no overlap) ──

    /// <summary>
    /// Dragging a wire whose top sits on a component pin TOWARD a collinear wire on that pin slides
    /// the top along the wire into a clean T — a straight 2-point wire, no overlapping L, connection
    /// preserved. (Pre-fix this pinned at the pin and bowed into a 3-point L overlapping Wh.)
    /// </summary>
    [Fact]
    public void DraggingPortWireTowardCollinearWire_SlidesIntoCleanT_NoOverlap()
    {
        var model = new SchematicEditModel { GridSnap = false };
        var rL = MakeResistor(0,   -200);   // port1 at PL=(0,0)
        var rR = MakeResistor(400, -200);   // port1 at PR=(400,0)
        model.Components.Add(rL);
        model.Components.Add(rR);
        var wh = MakeWire((0, 0), (400, 0));      // PL → PR
        var wv = MakeWire((0, 0), (0, 400));      // vertical drop from PL
        model.Wires.Add(wh);
        model.Wires.Add(wv);
        var vm = new SchematicViewModel(model);

        // Drag Wv's vertical segment fully right (toward PR): it slides along Wh, no bow.
        DragSegment(vm, 0, 200, 400, 200);

        var rWv = model.FindWire(wv.Id)!;
        Assert.Equal(2, rWv.Points.Count);   // clean vertical — no overlapping horizontal leg
        Assert.True(System.Math.Abs(rWv.Points[0].X - 400) < 1.0 && System.Math.Abs(rWv.Points[0].Y) < 1.0,
            $"Wv top should have slid onto PR (400,0), got ({rWv.Points[0].X},{rWv.Points[0].Y})");
        Assert.Equal(new System.Collections.Generic.List<(double, double)> { (0, 0), (400, 0) },
            model.FindWire(wh.Id)!.Points);   // Wh unchanged
        Assert.True(HasDotAt(vm, 400, 0), "a clean T forms at PR");
    }

    /// <summary>
    /// Dragging the same port wire AWAY from the collinear wire bows it (stays pinned at the pin):
    /// no lock, connection preserved, no overlap (the bow goes the empty direction). Guards against
    /// a future "always slide" change that would lock the away-direction drag.
    /// </summary>
    [Fact]
    public void DraggingPortWireAwayFromCollinearWire_Bows_StaysConnected()
    {
        var model = new SchematicEditModel { GridSnap = false };
        var rL = MakeResistor(0,   -200);   // port1 at PL=(0,0)
        var rR = MakeResistor(400, -200);   // port1 at (400,0)
        model.Components.Add(rL);
        model.Components.Add(rR);
        var wh = MakeWire((0, 0), (400, 0));      // PL → PR (extends RIGHT from PL)
        var wv = MakeWire((0, 0), (0, 400));      // vertical drop from PL
        model.Wires.Add(wh);
        model.Wires.Add(wv);
        var vm = new SchematicViewModel(model);

        // Drag Wv LEFT — away from Wh (which extends right). It must bow, not lock, not slide.
        DragSegment(vm, 0, 200, -100, 200);

        var rWv = model.FindWire(wv.Id)!;
        Assert.True(rWv.Points.Count >= 3, "dragging away from the collinear wire bows into an L (not locked)");
        Assert.True(rWv.Points.Any(p => System.Math.Abs(p.X) < 1.0 && System.Math.Abs(p.Y) < 1.0),
            "Wv must still touch PL (0,0) — the pin connection is preserved");
        Assert.Equal(new System.Collections.Generic.List<(double, double)> { (0, 0), (400, 0) },
            model.FindWire(wh.Id)!.Points);   // Wh unchanged

        var (render, _) = model.BuildRenderModel();
        var rRL = render.Components.First(c => c.Id == rL.Id);
        Assert.Equal(PortConnectionState.Connected, rRL.Ports[1].State);   // PL stays connected
    }

    // ── A component pin on the dragged wire's BODY: an auto-stub keeps it connected (Option B) ──

    /// <summary>
    /// Reported repro: an inductor with horizontal pins (right pin RP), and a vertical wire Wv that
    /// passes THROUGH RP on its body (extends above and below it). Dragging Wv perpendicular (to the
    /// right) moves the wire off RP cleanly, and an auto-wire STUB forms from RP to the moved wire so
    /// RP stays connected (the segment-drag analogue of the pin-on-pin auto-wire). Pre-fix the wire
    /// translated freely and RP disconnected (the segment pinning logic only inspected wire
    /// endpoints, never a pin on the wire's interior).
    /// </summary>
    [Fact]
    public void DraggingWireWithPinOnItsBody_AutoFormsStubToKeepPinConnected()
    {
        var model = new SchematicEditModel { GridSnap = false };
        // Resistor rotated 90° → its port0 (local (0,-200)) lands at world (X+200, Y).
        // Place at (-200,0) so RP = port0 = (0,0) — like a horizontal-pin inductor's right pin.
        var r = new EditableComponent
        {
            Symbol = SymbolKind.Resistor, X = -200, Y = 0,
            Rotation = SymbolRotation.R90, InstanceName = "L1",
        };
        model.Components.Add(r);
        var wv = MakeWire((0, -300), (0, 300));   // vertical wire through RP=(0,0) on its body
        model.Wires.Add(wv);
        var vm = new SchematicViewModel(model);
        Assert.True(HasDotAt(vm, 0, 0), "precondition: RP sits on Wv's body → T-junction dot");

        // Drag Wv's segment to the right; Wv moves cleanly and a stub auto-forms back to RP.
        DragSegment(vm, 0, 100, 100, 100);

        // The dragged wire is a clean 2-point vertical at the new x — it did NOT bow back.
        var rWv = model.FindWire(wv.Id)!;
        Assert.Equal(2, rWv.Points.Count);
        Assert.True(System.Math.Abs(rWv.Points[0].X - 100) < 1.0, "Wv moved cleanly to x=100");

        // A new stub wire connects RP=(0,0) to the moved wire at (100,0).
        Assert.Equal(2, model.Wires.Count);
        var stub = model.Wires.First(w => w.Id != wv.Id);
        Assert.Contains(stub.Points, p => System.Math.Abs(p.X) < 1.0 && System.Math.Abs(p.Y) < 1.0);   // touches RP
        Assert.Contains(stub.Points, p => System.Math.Abs(p.X - 100) < 1.0 && System.Math.Abs(p.Y) < 1.0); // meets Wv

        // RP stays connected via the stub, with a dot at the pin.
        var (render, _) = model.BuildRenderModel();
        var rr = render.Components.First(c => c.Id == r.Id);
        Assert.Equal(PortConnectionState.Connected, rr.Ports[0].State);
        Assert.True(HasDotAt(vm, 0, 0), "the pin connection survives the drag (via the stub)");
    }

    // ── Whole-wire drag (grabbed by an endpoint): the slide clamps so a body-tap can't fall off ──

    /// <summary>
    /// Reported repro: a vertical wire Wv with a stationary connection tapping its midpoint (here a
    /// component pin at (0,0), like the T-junction a horizontal wire would make). Grabbing Wv by an
    /// ENDPOINT (the whole-wire free-translation path, not the segment-drag path) and dragging it
    /// ALONG its axis must stop the slide when an endpoint reaches the tap — it must never slide the
    /// wire off the connection. Pre-fix the free-translation path ignored body taps and Wv slid off.
    /// </summary>
    [Fact]
    public void DraggingWireByEndpointAlongAxis_ClampsSoBodyTapStaysOn()
    {
        var model = new SchematicEditModel { GridSnap = false };
        // Resistor R90 at (-200,0) → port0 at world (0,0): a pin tapping Wv's midpoint.
        var r = new EditableComponent
        {
            Symbol = SymbolKind.Resistor, X = -200, Y = 0,
            Rotation = SymbolRotation.R90, InstanceName = "R1",
        };
        model.Components.Add(r);
        var wv = MakeWire((0, -200), (0, 200));   // vertical; pin sits at (0,0) on its body
        model.Wires.Add(wv);
        var vm = new SchematicViewModel(model);
        Assert.True(HasDotAt(vm, 0, 0), "precondition: pin taps Wv's body → T-junction dot");

        // Grab Wv by its TOP endpoint (0,-200) and drag DOWN by 300, overshooting the tap at (0,0).
        DragSegment(vm, 0, -200, 0, 100);

        // The slide stops when the top endpoint reaches the tap (0,0): Wv = [(0,0),(0,400)] — it did
        // NOT translate the full 300 to (0,100)/(0,500), which would have left the pin behind.
        var rWv = model.FindWire(wv.Id)!;
        Assert.Single(model.Wires);                 // no merge, no extra wire
        Assert.Equal(2, rWv.Points.Count);
        var top = rWv.Points.OrderBy(p => p.Y).First();
        Assert.True(System.Math.Abs(top.X) < 1.0 && System.Math.Abs(top.Y) < 1.0,
            $"top endpoint should clamp to the tap (0,0), got ({top.X},{top.Y})");

        // The pin stays connected (now at Wv's endpoint), with a dot at (0,0).
        var (render, _) = model.BuildRenderModel();
        var rr = render.Components.First(c => c.Id == r.Id);
        Assert.Equal(PortConnectionState.Connected, rr.Ports[0].State);
        Assert.True(HasDotAt(vm, 0, 0), "the tap connection survives the clamped slide");
    }
}
