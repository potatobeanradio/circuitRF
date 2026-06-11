using System;
using System.Linq;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Oracle for pin-on-pin connectivity detection.
///
/// B1: PinOnPin_TodayBothPortsUnconnected_NoDot and PinOnWireVertex_PortConnected
///     document current behavior — both pass today.
/// B2: Flips the pin-on-pin test to the correct expected behavior (Connected + one dot)
///     and adds the lone-port anti-over-connect guard.
/// </summary>
public class PinOnPinConnectivityTests
{
    private static EditableComponent MakeResistor(double cx, double cy) =>
        new() { Symbol = SymbolKind.Resistor, X = cx, Y = cy, InstanceName = "R?" };

    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.5;

    // ── B2: pin-on-pin shows Connected + exactly one dot ─────────────────────

    [Fact]
    public void PinOnPin_BothPortsConnected_ExactlyOneDot()
    {
        // Resistor A: port 2 (Ports[1]) at world (0, 200).
        // Resistor B: port 1 (Ports[0]) at world (0, 200).  No wire.
        var model = new SchematicEditModel();
        var compA = MakeResistor(0, 0);
        var compB = MakeResistor(0, 400);
        model.Components.Add(compA);
        model.Components.Add(compB);

        var (render, _) = model.BuildRenderModel();

        var rA = render.Components.First(c => c.Id == compA.Id);
        var rB = render.Components.First(c => c.Id == compB.Id);

        Assert.Equal(PortConnectionState.Connected, rA.Ports[1].State);  // port 2 of A
        Assert.Equal(PortConnectionState.Connected, rB.Ports[0].State);  // port 1 of B

        // Exactly one junction dot at the coincidence point; no second dot elsewhere.
        var touchDots = render.ConnectionDots.Where(d => Near(d.X, 0) && Near(d.Y, 200)).ToList();
        Assert.Single(touchDots);
    }

    // ── B2: lone port stays Unconnected — guard against over-connecting ──────

    [Fact]
    public void LonePort_StaysUnconnected_NoDot()
    {
        var model = new SchematicEditModel();
        var comp = MakeResistor(0, 0);
        model.Components.Add(comp);

        var (render, _) = model.BuildRenderModel();

        var rComp = render.Components.First(c => c.Id == comp.Id);
        foreach (var port in rComp.Ports)
            Assert.Equal(PortConnectionState.Unconnected, port.State);
        Assert.Empty(render.ConnectionDots);
    }

    // ── B1: control — pin-on-wire-vertex already works today ────────────────

    [Fact]
    public void PinOnWireVertex_PortConnected_Control()
    {
        // Resistor: port 2 (Ports[1]) at world (0, 200).  Wire vertex also at (0, 200).
        var model = new SchematicEditModel();
        var comp = MakeResistor(0, 0);
        model.Components.Add(comp);
        var wire = new EditableWire();
        wire.Points.AddRange([(0.0, 200.0), (400.0, 200.0)]);
        model.Wires.Add(wire);

        var (render, _) = model.BuildRenderModel();

        var rComp = render.Components.First(c => c.Id == comp.Id);
        Assert.Equal(PortConnectionState.Connected, rComp.Ports[1].State);
    }
}
