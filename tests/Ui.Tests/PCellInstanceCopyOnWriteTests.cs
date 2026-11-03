using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md R-L5-1/R-L5-2, gates 3/4: placing two MLINs
/// with identical parameters yields ONE generated cell and two instances; editing one instance's
/// parameters forks (repoints to) a new/different generated cell and leaves any sibling instance
/// referencing the original cell completely unchanged.
/// </summary>
public sealed class PCellInstanceCopyOnWriteTests : IDisposable
{
    private readonly string _workspaceDir;

    public PCellInstanceCopyOnWriteTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-pcell-cow-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private LayoutEditorViewModel MakeVmAt(string cellName)
    {
        string clayPath = Path.Combine(_workspaceDir, cellName, "layout", "main.clay");
        return new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }

    [Fact]
    public void TwoInstances_SameParameters_ShareOneGeneratedCell_R_L5_1()
    {
        var vm = MakeVmAt("Doc");
        var defaults = SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir1 = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        string cellDir2 = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN", defaults, null, null, PCellLayerSelection.Default);

        Assert.Equal(cellDir1, cellDir2, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void DifferentParameters_TwoInstances_GetDifferentGeneratedCells_R_L5_1()
    {
        var defaultsA = SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        var defaultsB = new Dictionary<string, PCellValue>(defaultsA) { ["W"] = defaultsA.Real("W") * 2 };

        string cellDirA = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN", defaultsA, null, null, PCellLayerSelection.Default);
        string cellDirB = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN", defaultsB, null, null, PCellLayerSelection.Default);

        Assert.NotEqual(cellDirA, cellDirB, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditInstancePCellParameters_ForksToNewCell_LeavesSiblingInstanceUnchanged_R_L5_2()
    {
        var vm = MakeVmAt("Doc");
        var defaults = SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);

        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 });
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 20_000_000, Y = 0, Mag = 1.0 });

        string sibling1CellRefBefore = vm.Model.Instances[1].CellRef;

        var newW = defaults.Real("W") * 3;
        bool ok = vm.EditInstancePCellParameters(0, new Dictionary<string, PCellValue> { ["W"] = newW });
        Assert.True(ok);

        // Instance 0 now points somewhere else...
        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
        // ...and instance 1 (the sibling) is COMPLETELY untouched.
        Assert.Equal(sibling1CellRefBefore, vm.Model.Instances[1].CellRef);

        var res0 = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res0.State);
        Assert.Equal(newW, res0.View!.PCellOrigin!.Parameters.Real("W"), 9);

        // Undo restores the original CellRef.
        vm.UndoCommand.Execute(null);
        Assert.Equal(cellRef, vm.Model.Instances[0].CellRef);
    }

    [Fact]
    public void EditInstancePCellParameters_KeepsTheInstancesLayerChoice()
    {
        // Field report, 2026-09-29: widening a microstrip placed on an inner layer brought it back on the
        // top copper — the parameters rode through the edit, the layer selection did not.
        var vm = MakeVmAt("Doc");
        var tech = ShippedTechnologies.Load("pcb-4layer_FR-4_62mil_1oz");
        vm.Technology = tech;
        var inner = new PCellLayerSelection("Inner 2", "Inner 1 (Ground Plane)");
        var innerKey = tech.Layers.First(l => l.Name == "Inner 2").Key;

        var defaults = SchematicLayoutPlanner.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN", defaults, tech, null, inner);
        GeneratedCellStore.RecordSnapshot(vm.Model, cellDir, "MLIN", defaults, null, inner, _workspaceDir);
        vm.Model.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), Mag = 1.0 });

        Assert.True(vm.EditInstancePCellParameters(0, new Dictionary<string, PCellValue> { ["W"] = defaults.Real("W") * 2 }));

        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.NotEmpty(res.View!.Shapes);
        Assert.All(res.View.Shapes, s => Assert.Equal(innerKey, s.Layer));
        var snap = vm.Model.PCellSnapshots[Path.GetFileName(vm.Model.Instances[0].CellRef)];
        Assert.Equal("Inner 2", snap.SignalLayerNameOverride);
    }
}
