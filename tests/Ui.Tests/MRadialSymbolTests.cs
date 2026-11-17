// MRADIAL's dynamic symbol: the sector's arc is a true ArcPrimitive at the instance's Angle (a VAR included), Sides = 2 draws the
// butterfly, the pin never moves, and the glyph's box is the drawing's, not the arc's whole circle.

using CircuitRF.Design.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class MRadialSymbolTests
{
    private static EditableComponent Stub(string angle, string sides = "1", string angleUnit = "deg")
    {
        var c = new EditableComponent { Symbol = SymbolKind.MRadial, InstanceName = "MRD1" };
        c.Parameters.Add(new EditableParameter { Name = "Angle", Expression = angle, Unit = angleUnit });
        c.Parameters.Add(new EditableParameter { Name = "Sides", Expression = sides, Unit = "" });
        return c;
    }

    /// <summary>The arcs sweep the instance's Angle in total, a VAR resolving as the netlist's does; a value that does not resolve draws
    /// the default 60°; Sides = 2 draws two sectors; every variant keeps the one pin where it was.</summary>
    [Fact]
    public void TheArcFollowsAngleAndSides_AndThePinNeverMoves()
    {
        static double Sweep(EditableComponent c) => c.ToRenderComponent().InstanceSymbol!.Primitives.OfType<ArcPrimitive>().Sum(a => a.SweepDeg);

        Assert.Equal(60, Sweep(Stub("60")), 9);
        Assert.Equal(120, Sweep(Stub("120")), 9);
        Assert.Equal(180, Sweep(Stub("180")), 9);
        Assert.Equal(60, Sweep(Stub("nope")), 9);
        Assert.Equal(2 * 90, Sweep(Stub("90", sides: "2")), 9);

        var model = new SchematicEditModel();
        var vars = new EditableComponent { Symbol = SymbolKind.Var, InstanceName = "VAR1" };
        vars.Parameters.Add(new EditableParameter { Name = "fan", Expression = "75", Unit = "deg" });
        var byVar = Stub("fan", angleUnit: "");
        model.Components.Add(vars);
        model.Components.Add(byVar);
        var drawn = model.BuildRenderModel().Model.Components.Single(c => c.Symbol == SymbolKind.MRadial);
        Assert.Equal(75, drawn.InstanceSymbol!.Primitives.OfType<ArcPrimitive>().Sum(a => a.SweepDeg), 9);

        foreach (var c in new[] { Stub("20"), Stub("180"), Stub("60", "2"), Stub("180", "2") })
        {
            var pin = Assert.Single(c.ToRenderComponent().InstanceSymbol!.Pins);
            Assert.Equal((-200.0, 0.0), (pin.LocalX, pin.LocalY));
            Assert.Equal((-200.0, 0.0), c.GetPortWorldCoord(0));
        }
    }

    /// <summary>The box is the drawing's: it starts at the pin and reaches the arc, however narrow the sector (whose arc is then centred
    /// far left of the pin), never the arc's whole circle.</summary>
    [Theory]
    [InlineData("10", "1")]
    [InlineData("60", "1")]
    [InlineData("180", "1")]
    [InlineData("60", "2")]
    [InlineData("180", "2")]
    public void TheGlyphBox_IsTheDrawing(string angle, string sides)
    {
        var (minX, minY, maxX, maxY) = Stub(angle, sides).ComputeGlyphBb();
        Assert.InRange(minX, -220, -195);                         // the pin, plus the glyph box's stroke margin
        Assert.InRange(maxX, 100, 220);
        Assert.InRange(maxY - minY, 80, 700);
    }
}
