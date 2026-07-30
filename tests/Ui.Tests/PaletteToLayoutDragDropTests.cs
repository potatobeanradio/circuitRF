using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-schematic-to-layout.md §3, gates 9-12: dragging a PCell-eligible
/// palette item into a layout shows a ghost of its real artwork at default parameters (generator
/// invoked once, not per pointer move); a non-PCell component (Term/Var) is not droppable; a
/// palette-dropped instance has no SchematicId and survives a schematic→layout re-run; and the drop
/// path produces an identical instance/generated cell to the schematic→layout path for the same
/// component at default parameters (R-L5-6's "one placement path").
/// </summary>
public sealed class PaletteToLayoutDragDropTests : IDisposable
{
    private readonly string _workspaceDir;

    public PaletteToLayoutDragDropTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crf-palette-drop-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        File.WriteAllText(Path.Combine(_workspaceDir, ".cws"), "{}");
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private LayoutEditorViewModel MakeVmAt(string cellName)
    {
        string clayPath = Path.Combine(_workspaceDir, cellName, "layout", "main.clay");
        return new LayoutEditorViewModel(new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 }, clayPath);
    }

    [Fact]
    public void Mlin_IsDroppable_TermAndVar_AreNot_R_L5_8()
    {
        var vm = MakeVmAt("Doc");
        Assert.True(vm.CanDropPaletteComponent(SymbolKind.Mlin, 0));
        Assert.False(vm.CanDropPaletteComponent(SymbolKind.Term, 0));
        Assert.False(vm.CanDropPaletteComponent(SymbolKind.Var, 0));
    }

    [Fact]
    public void DragGhost_GeneratesOnce_ThenOnlyMoves_NotPerPointerMove_R_L5_7()
    {
        var vm = MakeVmAt("Doc");
        var cache = new PCellGeometryCache();
        // Direct assertion against the shared static cache's call count isn't accessible from the test
        // (it's process-lifetime and private); instead verify observable behavior: the ghost view
        // reference is STABLE (same object) across several drag-over ticks for the SAME component —
        // proof the generator did not re-run per tick (a re-run would rebuild a fresh LayoutView).
        vm.UpdatePaletteDragGhost(SymbolKind.Mlin, 0, 0, 0);
        var overlay1 = vm.Overlay.PendingPCellPlacement;
        Assert.NotNull(overlay1);

        vm.UpdatePaletteDragGhost(SymbolKind.Mlin, 0, 1000, 2000);
        var overlay2 = vm.Overlay.PendingPCellPlacement;
        Assert.NotNull(overlay2);

        Assert.Same(overlay1!.Value.GhostView, overlay2!.Value.GhostView);
        Assert.Equal((1000L, 2000L), (overlay2.Value.X, overlay2.Value.Y));
        Assert.NotEmpty(overlay2.Value.GhostView.Shapes);
    }

    [Fact]
    public void NonDroppableComponent_NeverArmsGhost_R_L5_8()
    {
        var vm = MakeVmAt("Doc");
        vm.UpdatePaletteDragGhost(SymbolKind.Term, 0, 0, 0);
        Assert.Null(vm.Overlay.PendingPCellPlacement);
    }

    [Fact]
    public void CommitPaletteDrop_PlacesInstance_NoSchematicId_R_L5_6()
    {
        var vm = MakeVmAt("Doc");
        bool placed = vm.CommitPaletteDrop(SymbolKind.Mlin, 0, 3_000_000, 0);
        Assert.True(placed);
        Assert.Single(vm.Model.Instances);
        Assert.Null(vm.Model.Instances[0].SchematicId);

        var res = CellLayoutResolver.Resolve(vm.Model.Instances[0].CellRef, vm.InstanceBaseDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        Assert.Equal("MLIN", res.View!.PCellOrigin!.GeneratorId);

        // Ghost is cleared after commit.
        Assert.Null(vm.Overlay.PendingPCellPlacement);
    }

    [Fact]
    public void PaletteDroppedInstance_SurvivesSchematicToLayoutRerun_R_L5_6()
    {
        var vm = MakeVmAt("Amp");
        vm.CommitPaletteDrop(SymbolKind.Mlin, 0, 0, 0);
        Assert.Single(vm.Model.Instances);
        string cellRefBefore = vm.Model.Instances[0].CellRef;

        // An empty schematic (no components) re-run must leave the palette-placed instance alone —
        // it carries no SchematicId, so it is never a candidate for "no longer in the schematic."
        var model = new SchematicEditModel { SchematicDirectory = Path.Combine(_workspaceDir, "Amp", "schematic") };
        var result = SchematicToLayoutGenerator.Run(
            model, vm.Model, model.SchematicDirectory!, _workspaceDir, vm.InstanceBaseDir,
            null, null, cellResolver: null);

        Assert.Null(result.Command); // nothing to add/update/remove
        Assert.Single(vm.Model.Instances);
        Assert.Equal(cellRefBefore, vm.Model.Instances[0].CellRef);
    }

    [Fact]
    public void PaletteDrop_And_SchematicToLayout_ProduceIdenticalGeneratedCell_ForSameDefaults_Gate12()
    {
        var layoutVm = MakeVmAt("Amp");
        layoutVm.CommitPaletteDrop(SymbolKind.Mlin, 0, 0, 0);
        var paletteCellRef = layoutVm.Model.Instances[0].CellRef;
        var paletteAbsDir = Path.GetFullPath(Path.Combine(layoutVm.InstanceBaseDir, paletteCellRef));

        var schematicDir = Path.Combine(_workspaceDir, "Amp2", "schematic");
        var layoutDir = Path.Combine(_workspaceDir, "Amp2", "layout");
        var model = new SchematicEditModel { SchematicDirectory = schematicDir };
        var comp = new EditableComponent { InstanceName = "ML1", Symbol = SymbolKind.Mlin, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(SymbolKind.Mlin, 0))
            comp.Parameters.Add(new EditableParameter { Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension });
        model.Components.Add(comp);

        var target = new LayoutView();
        var result = SchematicToLayoutGenerator.Run(model, target, schematicDir, _workspaceDir, layoutDir, null, null, null);
        result.Command!.Execute();
        var schematicCellRef = target.Instances[0].CellRef;
        var schematicAbsDir = Path.GetFullPath(Path.Combine(layoutDir, schematicCellRef));

        Assert.Equal(paletteAbsDir, schematicAbsDir, StringComparer.OrdinalIgnoreCase);
    }
}
