using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md R-L5-12, gate 14: the whole re-run is one
/// undoable action — a single Undo() on the generator's returned command restores every overwritten
/// value and removes every added instance.
/// </summary>
public sealed class SchematicToLayoutUndoTests : IDisposable
{
    private readonly string _root;

    public SchematicToLayoutUndoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-s2l-undo-test-" + Guid.NewGuid().ToString("N"));
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

    private static EditableComponent MakeMlin(string instanceName, double wMm)
    {
        var comp = new EditableComponent { InstanceName = instanceName, Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Name == "W" ? wMm.ToString() : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }

    [Fact]
    public void OneUndo_RestoresOverwrittenValue_AndRemovesAddedInstance()
    {
        var (schematicDir, layoutDir) = MakeCell("Amp");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        model.Components.Add(MakeMlin("MLIN1", wMm: 10));

        var target = new LayoutView();
        var r1 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        r1.Command!.Execute();
        string originalCellRef = target.Instances[0].CellRef;

        // Layout-side edit that the next run will overwrite.
        var origin = CellLayoutResolver.Resolve(target.Instances[0].CellRef, layoutDir).View!.PCellOrigin!;
        var edited = new Dictionary<string, double>(origin.Parameters) { ["W"] = 20 * 1e-3 };
        string editedCellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", edited, null, null, PCellLayerSelection.Default);
        string editedCellRef = Path.GetRelativePath(layoutDir, editedCellDir);
        target.Instances[0].CellRef = editedCellRef;

        // Also add a second schematic component, so this run both overwrites AND adds.
        model.Components.Add(MakeMlin("MLIN2", wMm: 5));

        var r2 = SchematicToLayoutGenerator.Run(model, target, schematicDir, _root, layoutDir, null, null, null);
        Assert.NotNull(r2.Command);
        r2.Command!.Execute();

        Assert.Equal(2, target.Instances.Count);
        // Overwritten back to the schematic's 10mm value — content-addressed, so it's the SAME cell
        // the very first run pointed at, not merely "some cell with W=10mm."
        Assert.Equal(originalCellRef, target.Instances[0].CellRef);

        // ONE Undo reverts the WHOLE run.
        r2.Command!.Undo();

        Assert.Single(target.Instances);
        Assert.Equal(editedCellRef, target.Instances[0].CellRef); // the layout-side 20mm edit is restored
    }
}
