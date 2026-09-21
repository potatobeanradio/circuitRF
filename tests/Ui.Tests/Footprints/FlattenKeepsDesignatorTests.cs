using CircuitRF.Design;
using CircuitRF.Design.Layout.Footprints;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Xunit;

namespace CircuitRF.Ui.Tests.Footprints;

/// <summary>
/// Owner report, 2026-09-20: flattening a footprint made its designator text (Silk Top) disappear.
///
/// <para>A designator is the PLACEMENT's data, not the cell's (R-fp4b-5), so deleting the placement
/// deleted the only thing that knew the part was called C1 — while every other thing the placement
/// drew became geometry. The fix emits it through <c>FootprintLabel.ShapeFor</c>, the same function
/// the renderer and the whole-design flatten already use, so what lands in the document is the label
/// that was on the screen rather than a fourth opinion about where a designator goes.</para>
/// </summary>
public sealed class FlattenKeepsDesignatorTests : IDisposable
{
    private readonly string _root;

    public FlattenKeepsDesignatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-flatten-refdes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".cws"), "{}");
        CellLayoutResolver.InvalidateUnder(_root);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_root);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>The tech entry whose silkscreen role a land pattern actually draws on — the same one
    /// the generated cell is built against, so the flatten's label and the cell's own body outline
    /// land on one layer rather than two.</summary>
    private (LayoutEditorViewModel Vm, Technology Tech) Setup()
    {
        // The first PCB technology, not simply the first shipped one: the MMIC stackup declares no
        // silkscreen role at all, and a technology with no silkscreen draws no designator anywhere —
        // so every assertion below would pass on it while proving nothing.
        var entry = ShippedTechnologies.All.First(e =>
            LandPatternLayers.Resolve(ShippedTechnologies.Load(e), PCellLayerSelection.Default, []).Silkscreen is not null);
        _techId = entry.Id;
        var tech = ShippedTechnologies.Load(entry);
        var vm = new LayoutEditorViewModel(
            new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 },
            Path.Combine(_root, "Board", "layout", "main.clay"))
            { ActiveTool = LayoutEditorViewModel.Tool.Select, Technology = tech };
        return (vm, tech);
    }

    private string _techId = "";

    private string PlaceLandPattern(LayoutEditorViewModel vm, Technology tech, string refDes)
    {
        var reference = FootprintRef.For(SmtCaseTable.All[0]);
        string cellDir = GeneratedCellStore.GetOrCreate(
            _root, reference.ToString(), new Dictionary<string, PCellValue>(),
            tech, _techId, PCellLayerSelection.Default);
        vm.Model.Instances.Add(new LayoutInstance
        {
            CellRef = Path.GetRelativePath(vm.InstanceBaseDir, cellDir),
            X = 4_000_000, Y = 1_000_000, Mag = 1.0,
            RefDes = refDes,
        });
        return cellDir;
    }

    [Fact]
    public void FlatteningAPlacementLeavesItsDesignatorBehindAsSilkscreenArtwork()
    {
        var (vm, tech) = Setup();
        PlaceLandPattern(vm, tech, "C7");
        var inst = vm.Model.Instances[0];

        // Where the renderer draws it now, from the one function everything shares.
        var roles = LandPatternLayers.Resolve(tech, PCellLayerSelection.Default, []);
        var drawn = FootprintLabel.ShapeFor(
            inst, CellLayoutResolver.Resolve(inst.CellRef, vm.InstanceBaseDir).View,
            roles, vm.Model.DbuPerMicron, roles.Silkscreen);
        Assert.NotNull(drawn);

        vm.SelectInstance(0);
        vm.CommitFlattenOneLevel();

        Assert.Empty(vm.Model.Instances);
        var label = Assert.Single(vm.Model.Shapes.OfType<LabelShape>(), s => s.Text == "C7");
        Assert.Equal(roles.Silkscreen, label.Layer);
        Assert.Equal(drawn!.X, label.X);
        Assert.Equal(drawn.Y, label.Y);
        Assert.Equal(drawn.Height, label.Height);

        // ...and it comes back on undo with everything else, as one entry.
        vm.UndoCommand.Execute(null);
        Assert.Single(vm.Model.Instances);
        Assert.DoesNotContain(vm.Model.Shapes.OfType<LabelShape>(), s => s.Text == "C7");
    }

    /// <summary>The preview on the menu item promises a shape count; the emit now includes the
    /// designator, so the preview has to as well. This file's own history is that a preview
    /// disagreeing with the commit is its own bug — in either direction.</summary>
    [Fact]
    public void TheOutcomePreviewCountsTheDesignatorItIsAboutToEmit()
    {
        var (vm, tech) = Setup();
        PlaceLandPattern(vm, tech, "C7");
        vm.SelectInstance(0);

        string promised = vm.FlattenOneLevelOutcomeText!;
        vm.CommitFlattenOneLevel();
        Assert.Equal($"→ {vm.Model.Shapes.Count:N0} shape(s)", promised);
    }

    /// <summary>A placement with its designator switched off draws none, so flatten emits none —
    /// the toggle means the same thing on both sides of the operation.</summary>
    [Fact]
    public void AHiddenDesignatorIsNotEmitted()
    {
        var (vm, tech) = Setup();
        PlaceLandPattern(vm, tech, "C7");
        vm.Model.Instances[0].ShowRefDes = false;

        vm.SelectInstance(0);
        vm.CommitFlattenOneLevel();

        Assert.DoesNotContain(vm.Model.Shapes.OfType<LabelShape>(), s => s.Text == "C7");
    }
}
