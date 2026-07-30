using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups.md §4/R-L5f-7, gate 7: no Cell reference field and no
/// Re-target button for a PCell instance in the Properties Inspector; both present for an ordinary
/// instance.
/// </summary>
public sealed class PCellPropertiesInspectorHidingTests : IDisposable
{
    private readonly string _root;

    public PCellPropertiesInspectorHidingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-pcell-props-hide-test-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static void ClickInstance(LayoutEditorViewModel vm, LayoutInstance inst) =>
        vm.OnPointerPressed(inst.X, inst.Y, Avalonia.Input.KeyModifiers.None);

    [Fact]
    public void PCellInstance_HidesCellRefField()
    {
        var (vm, props) = Setup("Doc");
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 3_000_000, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst);

        ClickInstance(vm, inst);

        Assert.True(props.IsSingleInstanceSelected);
        Assert.True(props.IsSelectedInstancePCell);
    }

    [Fact]
    public void OrdinaryInstance_ShowsCellRefField()
    {
        var (vm, props) = Setup("Doc2");
        var leafDir = CellFolder.CreateCellFolder(_root, "Leaf");
        var leafLayoutDir = CellFolder.SubFolderPath(leafDir, ViewType.Layout);
        var leafView = new LayoutView { DbuPerMicron = 1000 };
        leafView.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 100_000 });
        LayoutPersistence.SaveToFile(Path.Combine(leafLayoutDir, "main.clay"), leafView);

        var inst = new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, leafDir), X = 0, Y = 0, Mag = 1.0 };
        vm.Model.Instances.Add(inst);

        vm.OnPointerPressed(50_000, 50_000, Avalonia.Input.KeyModifiers.None);

        Assert.True(props.IsSingleInstanceSelected);
        Assert.False(props.IsSelectedInstancePCell);
    }
}
