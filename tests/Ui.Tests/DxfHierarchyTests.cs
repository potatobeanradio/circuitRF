using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Design.Layout.Interchange;

namespace CircuitRF.Ui.Tests;

/// <summary>Gate 7 — nested blocks import as nested cells and instances; a crafted cyclic file is
/// caught by R-L3a-2's load-time check without throwing.</summary>
public class DxfHierarchyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dxf-hierarchy-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void NestedBlocks_ImportAsNestedCellsAndInstances()
    {
        var leaf = new InterchangeStructure("Leaf", [new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 }], []);
        var top = new InterchangeStructure("TOP", [], [new LayoutInstance { CellRef = "Leaf", X = 0, Y = 0, Mag = 1.0 }]);
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [leaf, top], "TOP", null, 1000, new DxfExportOptions());

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString()));
        var result = DxfImport.Import(stream, _dir, null, 1000);
        Assert.False(result.Cancelled);

        // Three cells: Leaf, TOP, and the synthetic model-space cell (§2A's own "one INSERT of the
        // root in ENTITIES" design means re-importing our own export always gets a third, thin cell
        // that simply instances TOP — this is what proves the design really is "on screen" on open,
        // not a hierarchy-collection bug).
        Assert.Equal(3, result.CreatedCellDirs.Count);

        var topDir = Path.Combine(_dir, result.CellNameByBlockName["TOP"]);
        var topLayoutDir = CellFolder.SubFolderPath(topDir, ViewType.Layout);
        var topView = LayoutPersistence.LoadFromFile(Path.Combine(topLayoutDir, $"{result.CellNameByBlockName["TOP"]}.clay"));
        var inst = Assert.Single(topView.Instances);

        var resolution = CellLayoutResolver.Resolve(inst.CellRef, topLayoutDir);
        Assert.Equal(CellLayoutState.Resolved, resolution.State);
        Assert.Single(resolution.View!.Shapes);

        var modelDir = Path.Combine(_dir, result.CellNameByBlockName[DxfReader.ModelSpaceName]);
        var modelLayoutDir = CellFolder.SubFolderPath(modelDir, ViewType.Layout);
        var modelView = LayoutPersistence.LoadFromFile(Path.Combine(modelLayoutDir, $"{result.CellNameByBlockName[DxfReader.ModelSpaceName]}.clay"));
        var modelInst = Assert.Single(modelView.Instances);
        var modelResolution = CellLayoutResolver.Resolve(modelInst.CellRef, modelLayoutDir);
        Assert.Equal(CellLayoutState.Resolved, modelResolution.State);
    }

    [Fact]
    public void MutualCycle_DoesNotThrow_ResolvedCellRefsFormACycle()
    {
        var a = new InterchangeStructure("A", [], [new LayoutInstance { CellRef = "B", X = 0, Y = 0, Mag = 1.0 }]);
        var b = new InterchangeStructure("B", [], [new LayoutInstance { CellRef = "A", X = 0, Y = 0, Mag = 1.0 }]);
        using var sw = new StringWriter();
        DxfWriter.Write(sw, [a, b], "A", null, 1000, new DxfExportOptions());

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sw.ToString()));
        var result = DxfImport.Import(stream, _dir, null, 1000);
        Assert.False(result.Cancelled);

        var aDir = Path.Combine(_dir, result.CellNameByBlockName["A"]);
        var aLayoutDir = CellFolder.SubFolderPath(aDir, ViewType.Layout);
        var aView = LayoutPersistence.LoadFromFile(Path.Combine(aLayoutDir, $"{result.CellNameByBlockName["A"]}.clay"));
        var instA = Assert.Single(aView.Instances);

        var resolution = CellLayoutResolver.Resolve(instA.CellRef, aLayoutDir);
        Assert.Equal(CellLayoutState.Resolved, resolution.State);

        // Exercise the SAME load-time cycle guard every other hierarchy consumer relies on — must not
        // throw or overflow even though A -> B -> A is a genuine cycle. The only thing under test here
        // is that the walk completes at all; the actual bbox value is not load-bearing.
        var ex = Record.Exception(() => CellHierarchy.InstanceBbox(instA, aLayoutDir));
        Assert.Null(ex);
    }
}
