// ================================================================
//  NetLabelPinConnectionTests.cs — a labelled pin is a connected pin (2026-09-01)
//
//  Owner: a component pin whose net is labelled is drawn with the unconnected
//  warning; with a label on it the pin should read as connected.
//
//  The netlist already agreed with the owner. NetExtractor seeds every pin's P-cell
//  into the union-find and FindLabelNetKey resolves a label sitting on one straight to
//  that key, so the pin extracts as a named net and joins every same-named label — which
//  is exactly what SubcircuitCellBuilder relies on when its router cannot reach a
//  terminal and connects it by name instead. Only the CONNECTION VISUALS disagreed:
//  EditableSchematic's IsConnected counted wire vertices and other pins and knew nothing
//  about labels, so a pin connected by name was drawn as an error.
// ================================================================

using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class NetLabelPinConnectionTests
{
    /// <summary>A one-port component at the origin; its port world position comes back with it.</summary>
    private static (SchematicEditModel Model, EditableComponent Comp, double Px, double Py) Fixture()
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            X = 0, Y = 0,
        };
        model.Components.Add(comp);
        var (px, py) = comp.GetPortWorldCoord(0);
        return (model, comp, px, py);
    }

    private static PortConnectionState StateOfPort0(SchematicEditModel model)
        => model.BuildRenderModel().Model.Components[0].Ports[0].State;

    /// <summary>The control: with nothing on it the pin still reads unconnected. Without this the
    /// test below could pass on a build that reported every pin connected.</summary>
    [Fact]
    public void ABarePin_IsStillUnconnected()
    {
        var (model, _, _, _) = Fixture();
        Assert.Equal(PortConnectionState.Unconnected, StateOfPort0(model));
    }

    [Fact]
    public void APinWithANetLabelOnIt_IsConnected()
    {
        var (model, _, px, py) = Fixture();
        model.NetLabels.Add(new EditableNetLabel { X = px, Y = py, Name = "VDD" });

        Assert.Equal(PortConnectionState.Connected, StateOfPort0(model));
    }

    /// <summary>Scoped to the pin the label is actually on: the component's OTHER pin is untouched.</summary>
    [Fact]
    public void ALabelOnOnePin_DoesNotConnectTheOther()
    {
        var (model, comp, px, py) = Fixture();
        model.NetLabels.Add(new EditableNetLabel { X = px, Y = py, Name = "VDD" });

        var render = model.BuildRenderModel().Model.Components[0];

        Assert.Equal(PortConnectionState.Connected,   render.Ports[0].State);
        Assert.Equal(PortConnectionState.Unconnected, render.Ports[1].State);
    }

    /// <summary>A label somewhere else on the sheet connects nothing — the rule is coincidence with
    /// the pin, which is the same rule the extractor resolves the label's own net by.</summary>
    [Fact]
    public void ALabelElsewhere_ConnectsNothing()
    {
        var (model, _, px, py) = Fixture();
        model.NetLabels.Add(new EditableNetLabel { X = px + 500, Y = py + 500, Name = "VDD" });

        Assert.Equal(PortConnectionState.Unconnected, StateOfPort0(model));
    }

    /// <summary>The visuals and the netlist have to say the same thing, which is the whole complaint:
    /// the pin the editor now draws as connected is the pin the extractor puts on net VDD.</summary>
    [Fact]
    public void TheLabelledPin_ExtractsOnTheLabelsNet()
    {
        var (model, comp, px, py) = Fixture();
        model.NetLabels.Add(new EditableNetLabel { X = px, Y = py, Name = "VDD" });

        var inst = NetExtractor.Extract(model).TestBench.Instances
            .First(i => i.InstanceName == comp.InstanceName);

        Assert.Equal("VDD", inst.NetBindings[0]);
    }

    /// <summary>A label anchored to a wire is left out of the new rule deliberately — it rides that
    /// wire, so the pin under it is already connected THROUGH the wire, and its stored X/Y is the
    /// previous build's until RecomputePosition runs. Gated so "unanchored only" stays a choice
    /// rather than something a later edit quietly widens.</summary>
    [Fact]
    public void AWiredAndLabelledPin_IsConnected()
    {
        var (model, _, px, py) = Fixture();
        var wire = new EditableWire();
        wire.Points.Add((px, py));
        wire.Points.Add((px, py - model.GridSize * 2));
        model.Wires.Add(wire);

        var lbl = new EditableNetLabel { Name = "VDD" };
        lbl.AnchorToWire(wire, px, py - model.GridSize);
        model.NetLabels.Add(lbl);

        Assert.True(lbl.IsAnchored);
        Assert.Equal(PortConnectionState.Connected, StateOfPort0(model));
    }
}
