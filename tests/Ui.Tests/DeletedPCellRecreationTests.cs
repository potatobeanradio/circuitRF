using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups.md §6, gate 10: place, delete, re-run — the instance is
/// RECREATED and the report says "added". §1's resolution bug (fixed) explains the symptom fully:
/// before the fix, a PCell that failed to resolve was <c>continue</c>d — nothing was EVER placed on
/// any run, which reads identically to "deleting it stops it coming back." §6.2 is the candidate —
/// verified directly below, not assumed. Delete itself (§6.3, DeleteInstancesCommand) was inspected
/// and is a plain <c>List.RemoveAt</c> with no separate bug.
/// </summary>
public sealed class DeletedPCellRecreationTests : IDisposable
{
    private readonly string _root;

    public DeletedPCellRecreationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-delete-recreate-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (string SchematicDir, string LayoutDir) MakeCell(string name)
    {
        string cellDir = CellFolder.CreateCellFolder(_root, name);
        return (CellFolder.SubFolderPath(cellDir, ViewType.Schematic), CellFolder.SubFolderPath(cellDir, ViewType.Layout));
    }

    private static EditableComponent PlaceFreshMklopf(string instanceName)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Mklopf, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mklopf, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Fact]
    public void PlaceDeleteRerun_RecreatesInstance_ReportsAdded_Gate10()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(PlaceFreshMklopf("MKF1"));

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        Assert.Empty(r1.NoLayoutWarnings); // §1's fix: default MKlopf resolves and places
        Assert.NotNull(r1.Command);
        r1.Command!.Execute();
        Assert.Single(target.Instances);

        // Delete — the exact primitive DeleteInstancesCommand uses.
        target.Instances.RemoveAt(0);
        Assert.Empty(target.Instances);

        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        Assert.NotNull(r2.Command);
        r2.Command!.Execute();

        Assert.Single(target.Instances);
        Assert.Equal(1, r2.AddedCount);
        Assert.Equal(0, r2.UpdatedCount);
        Assert.Equal(0, r2.UnchangedCount);
        Assert.Contains(r2.Lines, l => l.Text.Contains("added"));
    }
}
