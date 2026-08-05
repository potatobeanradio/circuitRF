using System;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups.md §2/R-L5f-5, gate 5: the generated branch arm points the
/// SAME PHYSICAL DIRECTION the symbol's own port 3 does. Asserted against the symbol's port-3
/// position (not a hardcoded layout-space sign), converting through each coordinate system's own
/// sense of "down" (schematic canvas: Y-down; layout: Y-up — <c>EditableSchematic.cs</c>'s own
/// port-table comment and <c>LayoutInstanceTransform</c>'s own doc comment, respectively) so the two
/// can never silently drift apart again.
/// </summary>
public class MTeeOrientationTests
{
    [Fact]
    public void Branch_PhysicalDirection_MatchesSymbolPort3()
    {
        var symbolPorts = SymbolPortDefs.For(SymbolKind.MTee, 0);
        var port3 = Array.Find(symbolPorts, p => p.Name == "3");
        // The symbol canvas is Y-DOWN: a positive LocalY means the port is physically BELOW center.
        bool symbolPort3IsPhysicallyDown = port3.LocalY > 0;
        Assert.True(symbolPort3IsPhysicallyDown, "test assumption: MTee's symbol port 3 is drawn below center");

        var result = MTeePCell.Generate(
            new Dictionary<string, PCellValue> { ["W1"] = 0.0029, ["W2"] = 0.0015, ["W3"] = 0.0029 },
            StarterTechnologies.Pcb2Layer(), PCellLayerSelection.Default);
        var pin3 = result.Pins.First(p => p.Name == "3");

        // Layout is Y-UP: physically "down" is NEGATIVE Y — the opposite sign from the symbol's own
        // Y-down convention, for the identical physical direction.
        bool layoutPin3IsPhysicallyDown = pin3.Y < 0;
        Assert.Equal(symbolPort3IsPhysicallyDown, layoutPin3IsPhysicallyDown);

        // The arm's own emitted geometry (not just the pin marker) extends toward that same -Y side.
        var shape = Assert.IsType<PolygonShape>(Assert.Single(result.Shapes));
        long minY = shape.Xy.Where((_, i) => i % 2 == 1).Min();
        Assert.True(minY < 0, "branch geometry must extend below the through-line axis (Y < 0)");
    }
}
