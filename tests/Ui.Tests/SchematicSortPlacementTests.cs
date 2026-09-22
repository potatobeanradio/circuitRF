using CircuitRF.Ui.Commands.Schematic;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>Sort Placement: unconnected components laid out in instance-name order, in free space,
/// without any pin landing where it would connect to something else.</summary>
public sealed class SchematicSortPlacementTests
{
    private static EditableComponent R(string name, double x, double y)
        => new() { InstanceName = name, Symbol = SymbolKind.Resistor, X = x, Y = y };

    /// <summary>A resistor at the origin with a wire leaving each pin — the obstacle the sorted
    /// parts are dropped on top of.</summary>
    private static (SchematicEditModel Model, EditableComponent Wired) WithWiredPartAtOrigin()
    {
        var model = new SchematicEditModel();
        var wired = R("R99", 0, 0);
        model.Components.Add(wired);
        var render = model.BuildRenderModel().Model.Components.Single();
        foreach (var p in render.Ports)
        {
            var (px, py) = SchematicGeometry.LocalToWorld(p.LocalX, p.LocalY, 0, 0, render.Rotation, render.MirrorX);
            var w = new EditableWire();
            w.Points.Add((px, py));
            w.Points.Add((px + 600, py));
            model.Wires.Add(w);
        }
        return (model, wired);
    }

    [Fact]
    public void ParkedOnAWiredPart_TheyAreSortedByNameIntoFreeSpace_AndNothingConnects()
    {
        var (model, wired) = WithWiredPartAtOrigin();
        // Heaped over one another just above the wired part — overlapping, no two pins touching — out
        // of order, and with a name that sorts wrongly as text.
        string[] names = ["R10", "R2", "C1", "R1"];
        var parts = names.Select((n, i) => R(n, 50 * i, -700 + 50 * i)).ToList();
        parts[2].Symbol = SymbolKind.Capacitor;
        model.Components.AddRange(parts);

        var (render, _) = model.BuildRenderModel();
        var ids = parts.Select(p => p.Id).ToList();
        Assert.Null(SchematicSortPlacement.Refusal(model, render, ids));

        var plan = SchematicSortPlacement.Plan(model, render, ids)!;
        Assert.Equal(["C1", "R1", "R2", "R10"], plan.Moves.Select(m => m.Component.InstanceName));

        var cmd = new SortPlacementCommand(model, plan.Moves);
        cmd.Execute();

        // Reading order: left to right along a row, rows downward.
        var sorted = plan.Moves.Select(m => m.Component).ToList();
        for (int i = 1; i < sorted.Count; i++)
            Assert.True(sorted[i].Y > sorted[i - 1].Y || (sorted[i].Y == sorted[i - 1].Y && sorted[i].X > sorted[i - 1].X));

        // Nothing moved is connected to anything, and the wired part still is.
        var after = model.BuildRenderModel().Model;
        foreach (var c in after.Components.Where(c => ids.Contains(c.Id)))
            Assert.All(c.Ports, p => Assert.Equal(PortConnectionState.Unconnected, p.State));
        Assert.All(after.Components.Single(c => c.Id == wired.Id).Ports,
                   p => Assert.Equal(PortConnectionState.Connected, p.State));

        cmd.Undo();
        Assert.All(parts.Select((p, i) => (p, i)), t => Assert.Equal((50.0 * t.i, -700 + 50.0 * t.i), (t.p.X, t.p.Y)));
    }

    [Fact]
    public void AWiredPartInTheSelection_IsRefusedByName()
    {
        var (model, wired) = WithWiredPartAtOrigin();
        var loose = R("R1", 5000, 5000);
        model.Components.Add(loose);

        var (render, _) = model.BuildRenderModel();
        string? refusal = SchematicSortPlacement.Refusal(model, render, [wired.Id, loose.Id]);
        Assert.NotNull(refusal);
        Assert.Contains("R99", refusal);
        Assert.Null(SchematicSortPlacement.Plan(model, render, [wired.Id, loose.Id]));
    }
}
