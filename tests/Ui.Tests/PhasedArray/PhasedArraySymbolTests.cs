// brief-paa-5 — the Phased Antenna Array's dynamic symbol: gates 3 and 4, the variant's resolution (VARs, steer, fallbacks) and the pin
// hover. Gate 1 is the pictures in the brief's completion note; gate 2 is RenderCliVerbTests.RenderingPhasedArraysAsAProcess_….

using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests.PhasedArray;

public sealed class PhasedArraySymbolTests
{
    private static EditableComponent Array(params (string Name, string Expression)[] parameters)
    {
        var c = new EditableComponent { Symbol = SymbolKind.PhasedArray, X = 0, Y = 0, InstanceName = "PA1" };
        foreach (var (n, e) in parameters) c.Parameters.Add(new EditableParameter { Name = n, Expression = e, Unit = n is "dx" or "dy" ? "mm" : "" });
        return c;
    }

    /// <summary>Gate 3 — a placed Elements-mode 4 × 4 changed to 6 × 4 (rows only, so D6 keeps every element's number) gains pins 17…24,
    /// and the wires on pins 1…16 stay on the same pins: none of them moved.</summary>
    [Fact]
    public void AddingRows_AddsPinsBelow_AndKeepsEveryWireOnItsPin()
    {
        var model = new SchematicEditModel { GridSize = 100 };
        var pa = Array(("Mode", "elements"), ("Rows", "4"), ("Cols", "4"));
        model.Components.Add(pa);
        var before = Enumerable.Range(0, 16).Select(pa.GetPortWorldCoord).ToArray();
        foreach (var (x, y) in before)
        {
            var w = new EditableWire();
            w.Points.Add((x - 300, y));
            w.Points.Add((x, y));
            model.Wires.Add(w);
        }
        Assert.All(model.BuildRenderModel().Model.Components.Single().Ports, p => Assert.Equal(PortConnectionState.Connected, p.State));

        pa.Parameters.Single(p => p.Name == "Rows").Expression = "6";
        Assert.Equal(24, pa.PortCount);
        var after = Enumerable.Range(0, 24).Select(pa.GetPortWorldCoord).ToArray();
        Assert.Equal(before, after.Take(16));
        var ports = model.BuildRenderModel().Model.Components.Single().Ports;
        Assert.Equal(24, ports.Count);
        Assert.All(ports.Take(16), p => Assert.Equal(PortConnectionState.Connected, p.State));
        Assert.All(ports.Skip(16), p => Assert.Equal(PortConnectionState.Unconnected, p.State));
    }

    /// <summary>Gate 4 — ten identical instances build one symbol, and share it.</summary>
    [Fact]
    public void TenIdenticalInstances_BuildOneSymbol()
    {
        // A variant no other test draws, so the count is this test's own.
        var parameters = new[] { ("Mode", "feed"), ("Rows", "7"), ("Cols", "13"), ("Lattice", "tri"), ("Off", "2,9,40") };
        int builds = BuiltInSymbols.PhasedArrayBuilds;
        var model = new SchematicEditModel { GridSize = 100 };
        for (int i = 0; i < 10; i++)
        {
            var c = Array(parameters);
            c.X = 1000 * i;
            model.Components.Add(c);
        }
        model.BuildRenderModel();
        Assert.Equal(builds + 1, BuiltInSymbols.PhasedArrayBuilds);
        var symbols = model.Components.Select(c => BuiltInSymbols.PrimitivesForPhasedArray(c.ArrayGlyph())).Distinct().ToList();
        Assert.Single(symbols);
        Assert.Equal(builds + 1, BuiltInSymbols.PhasedArrayBuilds);
    }

    /// <summary>R-paa5-4 — the variant resolves through the schematic's VARs (the lattice and the steer's angles), rounds the angles to
    /// 1°, and falls back to 4 × 4 at boresight with no text when a value does not resolve; with no instance it is Feed mode, 4 × 4.</summary>
    [Fact]
    public void TheVariant_ResolvesThroughTheVars_AndFallsBackWhenItCannot()
    {
        var model = new SchematicEditModel { GridSize = 100 };
        model.Components.Add(new EditableComponent
        {
            Symbol = SymbolKind.Var, InstanceName = "VAR1",
            Parameters = { new EditableParameter { Name = "theta0", Expression = "29.6 deg" }, new EditableParameter { Name = "nr", Expression = "24" } },
        });
        var pa = Array(("Mode", "feed"), ("Rows", "nr"), ("Cols", "32"), ("Phase", "steer(theta0, 0.2deg, 5.9GHz)"));
        model.Components.Add(pa);
        model.BuildRenderModel();
        Assert.Equal(new PhasedArrayGlyph(true, 24, 32, false, "", 30, 0), pa.ArrayGlyph());

        var delay = Array(("Mode", "feed"), ("Rows", "4"), ("Cols", "4"), ("Delay", "2*steerDelay(-20deg, 0)"));
        Assert.Equal(-20, delay.ArrayGlyph().ThetaDeg);

        var lost = Array(("Mode", "feed"), ("Rows", "nr"), ("Cols", "32"), ("Phase", "steer(theta0, 0, f0)"));
        Assert.Equal(PhasedArrayGlyph.Default, lost.ArrayGlyph());          // no VARs: 4 × 4, boresight, no text
        Assert.Equal(1, BuiltInSymbols.Primitives(SymbolKind.PhasedArray, 1).Pins.Count);
        // What the palette places is the variant its tile draws: Feed mode, one pin.
        var placed = ComponentTypeRegistry.DefaultParameters(SymbolKind.PhasedArray, 1)
                                          .Select(d => new EditableParameter { Name = d.Name, Expression = d.Expression });
        Assert.Equal(1, EditableComponent.PhasedArrayPins(placed));
    }

    /// <summary>R-paa5-5 — a hover on an Elements-mode pin names the element, its row and column, and its position in dx's unit.</summary>
    [Fact]
    public void APinHover_NamesTheElement_ItsRowColumnAndPosition()
    {
        var pa = Array(("Mode", "elements"), ("Rows", "2"), ("Cols", "3"), ("dx", "20"), ("dy", "30"));
        Assert.Equal("element 5 — row 2, col 2 — 0 mm, -15 mm", pa.PhasedArrayPinHover(4));
        Assert.Equal("element 1 — row 1, col 1 — -20 mm, 15 mm", pa.PhasedArrayPinHover(0));
        Assert.Null(Array(("Mode", "feed"), ("Rows", "2"), ("Cols", "3")).PhasedArrayPinHover(0));
    }
}
