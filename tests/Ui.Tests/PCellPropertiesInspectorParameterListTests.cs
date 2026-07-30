using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups.md §5/R-L5f-8/R-L5f-9, gates 8/9: selecting a PCell instance
/// lists its parameters, editable, with lazy row materialization (the L1j assertion); editing one of
/// two instances sharing a generated cell forks a new cell and leaves the other unchanged.
/// </summary>
public sealed class PCellPropertiesInspectorParameterListTests : IDisposable
{
    private readonly string _root;

    public PCellPropertiesInspectorParameterListTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-paramlist-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (LayoutEditorViewModel Vm, LayoutShapePropertiesViewModel Props) Setup(string cellName)
    {
        string clayPath = Path.Combine(_root, cellName, "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath)
            { ActiveTool = LayoutEditorViewModel.Tool.Select };
        var props = new LayoutShapePropertiesViewModel();
        props.SetContext(vm);
        return (vm, props);
    }

    [Fact]
    public void PCellInstance_ListsParameters_Gate8()
    {
        var (vm, props) = Setup("Doc");
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mklopf, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MKLOPF", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 3_000_000, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst);

        vm.OnPointerPressed(3_000_000, 0, Avalonia.Input.KeyModifiers.None);

        Assert.True(props.ShowPCellParameterList);
        Assert.NotNull(props.PCellParamRows);
        Assert.Equal(defaults.Count, props.PCellParamRows!.Count);

        // Realizing exactly one row (index 0) materializes exactly one — the L1j virtualization proof.
        var firstRow = props.PCellParamRows[0];
        Assert.Equal(1, props.PCellParamRows.MaterializedCount);
        Assert.False(string.IsNullOrEmpty(firstRow.ValueText));
    }

    [Fact]
    public void EditingParameter_ForksNewCell_LeavesSiblingUnchanged_Gate9()
    {
        var (vm, props) = Setup("Doc2");
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir);

        var inst1 = new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 };
        var inst2 = new LayoutInstance { CellRef = cellRef, X = 20_000_000, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst1);
        vm.Model.Instances.Add(inst2);

        vm.OnPointerPressed(5_000_000, 0, Avalonia.Input.KeyModifiers.None); // mid-MLIN1, selects inst1
        Assert.True(props.ShowPCellParameterList);

        // Find the "W" row and commit a new value through the same path the view's LostFocus handler uses.
        int wIndex = -1;
        for (int i = 0; i < props.PCellParamRows!.Count; i++)
            if (props.PCellParamRows[i].Name == "W") { wIndex = i; break; }
        Assert.True(wIndex >= 0);
        var wRow = props.PCellParamRows[wIndex];

        wRow.Commit("9.5"); // mm, layout display unit is Um by default... use whatever's currently shown format

        // inst2 (never selected/edited) keeps pointing at the ORIGINAL cell.
        Assert.Equal(cellRef, vm.Model.Instances[1].CellRef);
        // inst1 now points somewhere else.
        Assert.NotEqual(cellRef, vm.Model.Instances[0].CellRef);
    }
}
