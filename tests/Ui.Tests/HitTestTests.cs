using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class HitTestTests
{
    private static (SchematicEditModel Edit, SchematicModel Render, SchematicSpatialIndex Index) BuildSmall()
    {
        var edit = new SchematicEditModel();
        edit.Components.Add(new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            X            = 0,
            Y            = 0,
        });
        edit.Components.Add(new EditableComponent
        {
            InstanceName = "C1",
            Symbol       = SymbolKind.Capacitor,
            X            = 600,
            Y            = 0,
        });
        var (render, index) = edit.BuildRenderModel();
        return (edit, render, index);
    }

    [Fact]
    public void HitComponent_Center_ReturnsComponent()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, 0, 0);
        Assert.Equal(SchematicHitTest.HitKind.Component, hit.Kind);
        Assert.Equal(edit.Components[0].Id, hit.Id);
    }

    [Fact]
    public void HitComponent_FarAway_ReturnsNone()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, 99_999, 99_999);
        Assert.Equal(SchematicHitTest.HitKind.None, hit.Kind);
    }

    [Fact]
    public void HitComponent_SecondComponent_ReturnsCorrectId()
    {
        var (edit, render, index) = BuildSmall();
        var hit = SchematicHitTest.Test(edit, render, index, 600, 0);
        Assert.Equal(SchematicHitTest.HitKind.Component, hit.Kind);
        Assert.Equal(edit.Components[1].Id, hit.Id);
    }

    [Fact]
    public void RectHit_BothComponents_ReturnsBoth()
    {
        var (edit, render, index) = BuildSmall();
        var hits = SchematicHitTest.TestRect(edit, render, index, -500, -500, 1200, 500);
        var ids   = hits.Select(h => h.Id).ToHashSet();
        Assert.Contains(edit.Components[0].Id, ids);
        Assert.Contains(edit.Components[1].Id, ids);
    }

    [Fact]
    public void RectHit_NarrowRect_ReturnsOnlyFirst()
    {
        var (edit, render, index) = BuildSmall();
        var hits = SchematicHitTest.TestRect(edit, render, index, -300, -300, 200, 300);
        Assert.Single(hits);
        Assert.Equal(edit.Components[0].Id, hits[0].Id);
    }

    [Fact]
    public void NearestPort_FindsPort_WithinTolerance()
    {
        var (edit, render, index) = BuildSmall();
        // Resistor is vertical: port 0 is at local (0,-200) → world (0,-200) at R0
        var (found, _, _, px, py) = SchematicHitTest.NearestPort(edit, 0, -200, 20);
        Assert.True(found);
        Assert.InRange(py, -210.0, -190.0);
    }
}
