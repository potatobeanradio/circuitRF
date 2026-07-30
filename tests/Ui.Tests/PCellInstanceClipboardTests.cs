using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §1/gate 2: "cut/copy/paste of a placed PCell
/// works within a layout and across two layouts; the generated cell resolves in the destination, or
/// is reported broken and rendered as a placeholder. Assert it routes through the INSTANCE clipboard,
/// not a new path." Per §1's own claim — a placed PCell is an ordinary <see cref="LayoutInstance"/>
/// pointing at a cell folder — this exercises the EXISTING <c>LayoutEditorViewModel</c> clipboard API
/// (<see cref="LayoutInstanceClipboardTests"/>'s own methods) against a PCell-backed cell instead of a
/// hand-drawn one, with no PCell-specific clipboard code anywhere: if this passes, R-L5-1's "verify
/// rather than build" call was correct.
/// </summary>
public sealed class PCellInstanceClipboardTests : IDisposable
{
    private readonly string _workspaceDir;

    public PCellInstanceClipboardTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-pcell-clip-test-" + Guid.NewGuid().ToString("N")[..8]);
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

    private string CreatePCellCell()
    {
        var defaults = SchematicToLayoutGenerator.ResolveDefaultParameters(SymbolKind.Mlin, 0);
        return GeneratedCellStore.GetOrCreate(_workspaceDir, "MLIN", defaults, null, null, PCellLayerSelection.Default);
    }

    private LayoutEditorViewModel MakeVmAt(string cellName)
    {
        string clayPath = Path.Combine(_workspaceDir, cellName, "layout", "main.clay");
        return new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }

    [Fact]
    public void CopyPCellInstance_PasteInSameDocument_ResolvesToSamePCellOriginCell()
    {
        var pcellCellDir = CreatePCellCell();
        var vm = MakeVmAt("Doc");
        string cellRef = Path.GetRelativePath(vm.InstanceBaseDir, pcellCellDir);
        vm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 });

        vm.OnPointerPressed(5_000_000, 0, Avalonia.Input.KeyModifiers.None, hitTolDbu: 1000);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        var copied = vm.BuildCopyPayload();
        Assert.NotNull(copied);
        var rebased = vm.RebaseFragmentInstances(copied!);
        vm.PasteInstances(rebased);

        Assert.Equal(2, vm.Model.Instances.Count);
        var res = CellLayoutResolver.Resolve(vm.Model.Instances[1].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.NotNull(res.View!.PCellOrigin);
        Assert.Equal("MLIN", res.View.PCellOrigin!.GeneratorId);
    }

    [Fact]
    public void CopyPCellInstance_PasteIntoDifferentDocument_ResolvesToSamePCellOriginCell()
    {
        var pcellCellDir = CreatePCellCell();
        var sourceVm = MakeVmAt("Source");
        var destVm   = MakeVmAt("Dest");

        string cellRef = Path.GetRelativePath(sourceVm.InstanceBaseDir, pcellCellDir);
        sourceVm.Model.Instances.Add(new LayoutInstance { CellRef = cellRef, X = 0, Y = 0, Mag = 1.0 });

        sourceVm.OnPointerPressed(5_000_000, 0, Avalonia.Input.KeyModifiers.None, hitTolDbu: 1000);
        var payload = sourceVm.BuildCopyPayload()!;
        var rebased = destVm.RebaseFragmentInstances(payload);
        destVm.PasteInstancesInPlace(rebased);

        Assert.Single(destVm.Model.Instances);
        var res = CellLayoutResolver.Resolve(destVm.Model.Instances[0].CellRef, destVm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.NotNull(res.View!.PCellOrigin);
        Assert.Equal("MLIN", res.View.PCellOrigin!.GeneratorId);
    }
}
