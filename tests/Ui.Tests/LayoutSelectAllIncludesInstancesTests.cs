using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner-reported bug (post-L5): Select All (Ctrl/Cmd+A) in the Layout editor must select
/// EVERYTHING — shapes (including bitmaps, already a LayoutShape) AND instances (ordinary and
/// PCell-backed). <c>SelectAllCommand</c> previously only ever selected shapes.
/// </summary>
public sealed class LayoutSelectAllIncludesInstancesTests : IDisposable
{
    private readonly string _root;

    public LayoutSelectAllIncludesInstancesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-selectall-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void SelectAll_SelectsShapesAndInstances_IncludingPCells()
    {
        string clayPath = Path.Combine(_root, "Doc", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);

        vm.Model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        vm.Model.Shapes.Add(new BitmapShape { X = 0, Y = 0, W = 1000, H = 1000, ImagePathRef = "x.png" });

        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        string cellDir = GeneratedCellStore.GetOrCreate(_root, "MLIN", defaults, null, null, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance { CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir), X = 0, Y = 0, Mag = 1.0 });

        vm.SelectAllCommand.Execute(null);

        Assert.Equal(2, vm.SelectedIndices.Count);
        Assert.Equal(1, vm.SelectedInstanceIndices.Count);
    }

    [Fact]
    public void DeselectAll_ClearsBothKinds()
    {
        string clayPath = Path.Combine(_root, "Doc2", "layout", "main.clay");
        var vm = new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
        vm.Model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        vm.Model.Instances.Add(new LayoutInstance { CellRef = "../nowhere", X = 0, Y = 0, Mag = 1.0 });

        vm.SelectAllCommand.Execute(null);
        Assert.NotEmpty(vm.SelectedIndices);
        Assert.NotEmpty(vm.SelectedInstanceIndices);

        vm.DeselectAllCommand.Execute(null);
        Assert.Empty(vm.SelectedIndices);
        Assert.Empty(vm.SelectedInstanceIndices);
    }
}
