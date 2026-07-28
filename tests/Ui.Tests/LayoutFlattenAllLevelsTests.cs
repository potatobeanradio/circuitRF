using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3c (brief-L3c-flatten-and-group.md §3/§2.2) — gate 7 (Flatten Hierarchy — all levels: a
//  three-deep hierarchy fully flattens, the pre-computed count matches the actual result, an
//  unresolvable instance survives as an instance and is reported, the hard ceiling refuses with its
//  number named) and gate 6 (cross-technology flatten requires confirmation; same-technology flatten
//  raises none).
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutFlattenAllLevelsTests : IDisposable
{
    private readonly string _workspaceDir;
    private static readonly LayerKey LayerA = new(1, 0);

    public LayoutFlattenAllLevelsTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfFlattenAllTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static LayoutView MakeView() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private string CreateCell(string name, Action<LayoutView> populate)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = MakeView();
        populate(view);
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    private static RectShape Rect(long x1, long y1, long x2, long y2) =>
        new() { Layer = LayerA, X1 = x1, Y1 = y1, X2 = x2, Y2 = y2 };

    // ── Gate 7: a three-deep hierarchy fully flattens ───────────────────────────────────────────────

    [Fact]
    public void FlattenAllLevels_ThreeDeepHierarchy_FullyFlattens_CountMatchesActualResult()
    {
        CreateCell("Leaf", v => v.Shapes.Add(Rect(0, 0, 100, 100)));
        CreateCell("Middle", v => v.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 10, Y = 10, Mag = 1.0 }));
        CreateCell("Outer", v => v.Instances.Add(new LayoutInstance { CellRef = "../../Middle", X = 20, Y = 20, Mag = 1.0 }));

        var top = new LayoutInstance { CellRef = "Outer", X = 1000, Y = 2000, Mag = 1.0 };

        long precomputed = LayoutFlatten.CountResultingShapes(top, _workspaceDir);
        Assert.Equal(1, precomputed);   // exactly one Rect at the bottom of the three-deep chain

        var result = LayoutFlatten.FlattenAllLevels(top, _workspaceDir);
        Assert.Equal(precomputed, result.Shapes.Count);
        Assert.Empty(result.SurvivingInstances);
        Assert.Single(result.Shapes);
        var rect = (RectShape)result.Shapes[0];
        // 1000 + 20 + 10 = 1030, etc — the three translations compose exactly (Mag 1, R0, no mirror).
        Assert.Equal(1030, rect.X1);
        Assert.Equal(2030, rect.Y1);
    }

    [Fact]
    public void FlattenAllLevels_ArraysAtTwoLevels_Multiplies_CountMatchesActualResult()
    {
        CreateCell("Leaf", v => v.Shapes.Add(Rect(0, 0, 10, 10)));
        CreateCell("Middle", v => v.Instances.Add(new LayoutInstance
        {
            CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0, Rows = 3, Cols = 3, PitchX = 100, PitchY = 100,
        }));

        var top = new LayoutInstance { CellRef = "Middle", X = 0, Y = 0, Mag = 1.0, Rows = 2, Cols = 2, PitchX = 1000, PitchY = 1000 };

        long precomputed = LayoutFlatten.CountResultingShapes(top, _workspaceDir);
        Assert.Equal(2 * 2 * 3 * 3, precomputed);   // 36 — arrays at two levels multiply

        var result = LayoutFlatten.FlattenAllLevels(top, _workspaceDir);
        Assert.Equal(precomputed, result.Shapes.Count);
        Assert.Empty(result.SurvivingInstances);
    }

    [Fact]
    public void FlattenAllLevels_UnresolvableNestedInstance_SurvivesAsInstance_IsReported_NotDropped()
    {
        CreateCell("Leaf", v => v.Shapes.Add(Rect(0, 0, 10, 10)));
        CreateCell("Middle", v =>
        {
            v.Instances.Add(new LayoutInstance { CellRef = "../../Leaf", X = 0, Y = 0, Mag = 1.0 });
            v.Instances.Add(new LayoutInstance { CellRef = "../../DoesNotExist", X = 500, Y = 500, Mag = 1.0 });
        });

        var top = new LayoutInstance { CellRef = "Middle", X = 0, Y = 0, Mag = 1.0 };

        var result = LayoutFlatten.FlattenAllLevels(top, _workspaceDir);

        Assert.Single(result.Shapes);                     // the resolvable Leaf instance became geometry
        Assert.Single(result.SurvivingInstances);          // the broken one survives, not dropped
        Assert.Equal("DoesNotExist", result.SurvivingInstances[0].CellRef);   // rebased to _workspaceDir
        Assert.Equal(500, result.SurvivingInstances[0].X);
        Assert.Equal(500, result.SurvivingInstances[0].Y);
    }

    [Fact]
    public void FlattenAllLevels_UnresolvableTopLevelInstance_SurvivesAsItself_NoShapes()
    {
        var top = new LayoutInstance { CellRef = "NeverExisted", X = 42, Y = 99, Mag = 1.0 };
        var result = LayoutFlatten.FlattenAllLevels(top, _workspaceDir);

        Assert.Empty(result.Shapes);
        Assert.Single(result.SurvivingInstances);
        Assert.Equal("NeverExisted", result.SurvivingInstances[0].CellRef);
    }

    [Fact]
    public void FlattenAllLevels_MutualCycle_DoesNotHang_LeavesTheCyclicEdgeAsASurvivingInstance()
    {
        // A hand-authored mutual-cycle .clay pair (edit-time cycle rejection cannot have prevented
        // this — it can only arrive from outside the editor). The cycle-guard visiting-set must stop
        // the recursion rather than hang or stack-overflow.
        CreateCell("A", v => { });
        CreateCell("B", v => v.Instances.Add(new LayoutInstance { CellRef = "../../A", X = 0, Y = 0, Mag = 1.0 }));
        // Overwrite A to reference B, closing the cycle A -> B -> A.
        var aDir = Path.Combine(_workspaceDir, "A");
        var aView = MakeView();
        aView.Instances.Add(new LayoutInstance { CellRef = "../../B", X = 0, Y = 0, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(aDir, ViewType.Layout), "main.clay"), aView);
        CellLayoutResolver.InvalidateAll();

        var top = new LayoutInstance { CellRef = "A", X = 0, Y = 0, Mag = 1.0 };

        var result = LayoutFlatten.FlattenAllLevels(top, _workspaceDir);   // must return, not hang

        Assert.Empty(result.Shapes);
        Assert.Single(result.SurvivingInstances);   // the cyclic re-entry is left in place as an instance
    }

    // ── R-L3c-4: the hard ceiling refuses outright, before mutating anything ───────────────────────

    [Fact]
    public void CountResultingShapes_ExceedsCeiling_ReturnsSentinel_WithoutMaterializingMillions()
    {
        CreateCell("Cell", v => v.Shapes.Add(Rect(0, 0, 10, 10)));
        // 1,000 x 1,000 = 1,000,000 array cells of a 1-shape cell — comfortably over a modest test
        // ceiling, and this call must return promptly (no O(N) materialization).
        var arrayInst = new LayoutInstance
        {
            CellRef = "Cell", X = 0, Y = 0, Mag = 1.0, Rows = 1000, Cols = 1000, PitchX = 100, PitchY = 100,
        };

        long count = LayoutFlatten.CountResultingShapes(arrayInst, _workspaceDir, ceiling: 500_000);
        Assert.Equal(-1, count);
    }

    [Fact]
    public void CountResultingShapes_UnderCeiling_ReturnsExactCount()
    {
        CreateCell("Cell", v => v.Shapes.Add(Rect(0, 0, 10, 10)));
        var arrayInst = new LayoutInstance { CellRef = "Cell", X = 0, Y = 0, Mag = 1.0, Rows = 5, Cols = 5, PitchX = 100, PitchY = 100 };

        long count = LayoutFlatten.CountResultingShapes(arrayInst, _workspaceDir, ceiling: 500_000);
        Assert.Equal(25, count);
    }

    [Fact]
    public void CommitFlattenAllLevels_OverHardCeiling_RefusesOutright_NamesTheCeiling_ModelUnchanged()
    {
        CreateCell("Cell", v => v.Shapes.Add(Rect(0, 0, 10, 10)));
        var model = MakeView();
        var arrayInst = new LayoutInstance
        {
            CellRef = "Cell", X = 0, Y = 0, Mag = 1.0,
            Rows = 1000, Cols = 1000, PitchX = 100, PitchY = 100,
        };
        model.Instances.Add(arrayInst);

        var (vm, sink) = TopLevelVm(model);
        ClickSelectInstance(vm, 5, 5);   // inside the array's first cell (Cell's own Rect 0,0-10,10)
        Assert.Equal([0], vm.SelectedInstanceIndices);

        vm.CommitFlattenAllLevels();

        Assert.Single(model.Instances);      // nothing mutated
        Assert.Empty(model.Shapes);
        Assert.Contains(sink.Posted, p => p.Level == MessageLevel.Error && p.Text.Contains(LayoutFlatten.FlattenAllLevelsHardCeiling.ToString("N0")));
    }

    // ── Gate 6: cross-technology flatten requires confirmation; same-technology raises none ─────────

    private (LayoutEditorViewModel Vm, FakeMessageSink Sink) TopLevelVm(LayoutView model)
    {
        var sink = new FakeMessageSink();
        var clayPath = Path.Combine(_workspaceDir, "top.clay");
        var vm = new LayoutEditorViewModel(model, clayPath, sink);
        return (vm, sink);
    }

    /// <summary>Selects an instance the same way a user would — a plain Select-tool click at a world
    /// point that falls inside its geometry — since no test-only selection setter exists (matching
    /// this codebase's established "drive the real gesture entry points" convention).</summary>
    private static void ClickSelectInstance(LayoutEditorViewModel vm, double wx, double wy) =>
        vm.OnPointerPressed(wx, wy, KeyModifiers.None);

    [Fact]
    public void CheckFlattenCrossTechMapping_SubCellUsesOtherStarterTechnology_RequiresConfirmation()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var mmic = StarterTechnologies.MmicGaAs();

        // Sub-cell "Drilled" uses PCB (whose (7,0) is Drill); the parent uses MMIC GaAs (whose (7,0) is
        // Substrate) — the exact Drill-onto-Substrate trap L1g's own history names.
        var subDir = CellFolder.CreateCellFolder(_workspaceDir, "Drilled");
        var subLayoutDir = CellFolder.SubFolderPath(subDir, ViewType.Layout);
        var subView = MakeView();
        subView.Shapes.Add(new RectShape { Layer = new LayerKey(7, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }); // Drill on PCB
        LayoutPersistence.SaveToFile(Path.Combine(subLayoutDir, "main.clay"), subView);

        var model = MakeView();
        model.Instances.Add(new LayoutInstance { CellRef = "Drilled", X = 0, Y = 0, Mag = 1.0 });
        var (vm, _) = TopLevelVm(model);
        vm.ApplyTechResolution(new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []));
        vm.ResolveTechAt = (techRef, clayDir) => new TechResolution(pcb, "/fake/pcb.ctech", TechResolutionSource.LayoutRef, []);
        ClickSelectInstance(vm, 50, 50);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        var mapping = vm.CheckFlattenCrossTechMapping();

        Assert.NotNull(mapping);
        Assert.True(LayoutLayerMapping.RequiresConfirmation(mapping!));
        // The trap itself: same numeric key (7,0), different name — never silently remapped.
        var row = mapping!.Single(r => r.Source == new LayerKey(7, 0));
        Assert.Equal(LayerMatchKind.SameKeyDifferentName, row.Match);
    }

    [Fact]
    public void CheckFlattenCrossTechMapping_SameTechnologyBothSides_RaisesNoConfirmation()
    {
        var pcb = StarterTechnologies.Pcb2Layer();

        var subDir = CellFolder.CreateCellFolder(_workspaceDir, "Sub");
        var subLayoutDir = CellFolder.SubFolderPath(subDir, ViewType.Layout);
        var subView = MakeView();
        subView.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        LayoutPersistence.SaveToFile(Path.Combine(subLayoutDir, "main.clay"), subView);

        var model = MakeView();
        model.Instances.Add(new LayoutInstance { CellRef = "Sub", X = 0, Y = 0, Mag = 1.0 });
        var (vm, _) = TopLevelVm(model);
        vm.ApplyTechResolution(new TechResolution(pcb, "/fake/pcb.ctech", TechResolutionSource.LayoutRef, []));
        vm.ResolveTechAt = (techRef, clayDir) => new TechResolution(pcb, "/fake/pcb.ctech", TechResolutionSource.LayoutRef, []);
        ClickSelectInstance(vm, 50, 50);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        var mapping = vm.CheckFlattenCrossTechMapping();
        Assert.Null(mapping);
    }

    [Fact]
    public void CommitFlattenOneLevel_WithResolvedCrossTechMapping_RemapsLayers()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var mmic = StarterTechnologies.MmicGaAs();

        var subDir = CellFolder.CreateCellFolder(_workspaceDir, "Drilled");
        var subLayoutDir = CellFolder.SubFolderPath(subDir, ViewType.Layout);
        var subView = MakeView();
        var drillKey = new LayerKey(7, 0);
        subView.Shapes.Add(new RectShape { Layer = drillKey, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        LayoutPersistence.SaveToFile(Path.Combine(subLayoutDir, "main.clay"), subView);

        var model = MakeView();
        model.Instances.Add(new LayoutInstance { CellRef = "Drilled", X = 0, Y = 0, Mag = 1.0 });
        var (vm, _) = TopLevelVm(model);
        vm.ApplyTechResolution(new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []));
        vm.ResolveTechAt = (techRef, clayDir) => new TechResolution(pcb, "/fake/pcb.ctech", TechResolutionSource.LayoutRef, []);
        ClickSelectInstance(vm, 50, 50);
        Assert.Equal([0], vm.SelectedInstanceIndices);

        var mapping = vm.CheckFlattenCrossTechMapping();
        Assert.NotNull(mapping);

        // Simulate the user confirming an explicit remap onto MMIC's own Metal1 (1,0) rather than
        // letting Drill (7,0) silently collide with MMIC's own (7,0) = Substrate.
        var metal1 = new LayerKey(1, 0);
        var resolved = mapping!.Select(r => r.Source == drillKey
            ? r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.MapToExisting, metal1) }
            : r).ToList();

        vm.CommitFlattenOneLevel(resolved);

        Assert.Empty(model.Instances);
        Assert.Single(model.Shapes);
        Assert.Equal(metal1, ((RectShape)model.Shapes[0]).Layer);
    }
}
