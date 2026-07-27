using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests;

// ── Phase L1f gates 3/6/8/9/10/11: docs/sonnet-briefs/brief-L1f-clipboard.md
// VM-level: Cut/Duplicate/Paste-placement/Paste-in-Place, one-undo-entry, selection-after-paste,
// screen-coordinate-driven ghost placement, cross-cell/cross-technology paste.

public class LayoutClipboardViewModelTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel(int dbuPerMicron = 1000) => new()
    {
        DbuPerMicron = dbuPerMicron,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
    };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Copy / Cut ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildCopyPayload_NoSelection_ReturnsNull()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        Assert.Null(vm.BuildCopyPayload());
    }

    [Fact]
    public void BuildCopyPayload_NeverMutatesModel_NoUndoEntry()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        var payload = vm.BuildCopyPayload();

        Assert.NotNull(payload);
        Assert.Single(payload!.Shapes);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Single(model.Shapes);
    }

    [Fact]
    public void Cut_CopyPayloadThenDeleteSelection_IsOneUndoEntry()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 };
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        var payload = vm.BuildCopyPayload();
        Assert.NotNull(payload);
        vm.CutSelectionAfterCopy();

        Assert.Empty(model.Shapes);
        vm.UndoRedo.Undo();
        Assert.Single(model.Shapes);
        Assert.Same(rect, model.Shapes[0]);
        vm.UndoRedo.Redo();
        Assert.Empty(model.Shapes);
    }

    // ── Gate 9 (Duplicate half) / §4: Duplicate offsets by one snap step, one undo entry ─────────

    [Fact]
    public void Duplicate_OffsetsByOneSnapStep_OneUndoEntry_SelectsTheCopy()
    {
        var model = FreshModel();
        model.SnapDbu = 500;
        var original = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 };
        model.Shapes.Add(original);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        vm.Duplicate();

        Assert.Equal(2, model.Shapes.Count);
        var copy = Assert.IsType<RectShape>(model.Shapes[1]);
        Assert.Equal(500, copy.X1);
        Assert.Equal(500, copy.Y1);
        Assert.Equal(1_500, copy.X2);
        Assert.Single(vm.SelectedIndices);
        Assert.Equal(1, vm.SelectedIndices[0]);

        vm.UndoRedo.Undo();
        Assert.Single(model.Shapes);
        Assert.Same(original, model.Shapes[0]);
    }

    [Fact]
    public void Duplicate_NoSelection_NoOp()
    {
        var vm = new LayoutEditorViewModel(FreshModel());
        vm.Duplicate();
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── Paste in Place: original coordinates, immediate, one undo entry ──────────────────────────

    [Fact]
    public void PasteInPlace_LandsAtOriginalCoordinates_OneUndoEntry_SelectsThePasted()
    {
        var srcModel = FreshModel();
        srcModel.Shapes.Add(new RectShape { Layer = Layer1, X1 = 10_000, Y1 = 20_000, X2 = 30_000, Y2 = 40_000 });
        var srcVm = new LayoutEditorViewModel(srcModel) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(srcVm, 20_000, 30_000);
        var payload = srcVm.BuildCopyPayload();
        Assert.NotNull(payload);

        var destModel = FreshModel();
        var destVm = new LayoutEditorViewModel(destModel);
        var rescale = destVm.RescaleFragment(payload!);
        var reconciled = destVm.ApplyFragmentReconciliation(rescale.Shapes, payload.Layers, null);

        destVm.PasteInPlace(reconciled);

        var pasted = Assert.IsType<RectShape>(Assert.Single(destModel.Shapes));
        Assert.Equal(10_000, pasted.X1);
        Assert.Equal(20_000, pasted.Y1);
        Assert.Equal(30_000, pasted.X2);
        Assert.Single(destVm.SelectedIndices);

        destVm.UndoRedo.Undo();
        Assert.Empty(destModel.Shapes);
    }

    [Fact]
    public void Paste_AppendsTopmost_NotAtLowestIndex()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 200, Y1 = 0, X2 = 300, Y2 = 100 });
        var vm = new LayoutEditorViewModel(model);

        vm.PasteInPlace([new RectShape { Layer = Layer1, X1 = 500, Y1 = 500, X2 = 600, Y2 = 600 }]);

        Assert.Equal(3, model.Shapes.Count);
        Assert.Equal(500, ((RectShape)model.Shapes[2]).X1);
        Assert.Equal([2], vm.SelectedIndices);
    }

    // ── Gate 8: paste ghost placement driven through screen coordinates ──────────────────────────

    public static IEnumerable<object[]> StarterTechs()
    {
        yield return new object[] { "Pcb2Layer", StarterTechnologies.Pcb2Layer() };
        yield return new object[] { "MmicGaAs", StarterTechnologies.MmicGaAs() };
    }

    [Theory]
    [MemberData(nameof(StarterTechs))]
    public void PastePlacement_ScreenCoordinates_AnchorLandsOnSnappedCursor(string name, Technology tech)
    {
        const double width = 1200, height = 800;
        var model = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = tech.DefaultDisplayUnit,
            SnapDbu      = tech.DefaultSnapDbu,
        };
        var vp = LayoutViewport.Default(width, height, model.SnapDbu, model.DbuPerMicron);
        var vm = new LayoutEditorViewModel(model);

        var shapes = new List<LayoutShape> { new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 } };
        vm.BeginPastePlacement(shapes, anchorX: 0, anchorY: 0);
        Assert.True(vm.IsPastePlacementActive);

        // Drive the ghost through the exact screen->world conversion LayoutCanvas would use.
        double screenX = width / 2 + 137, screenY = height / 2 - 61;
        double wx = vp.ScreenToWorldX(screenX), wy = vp.ScreenToWorldY(screenY);
        vm.OnPointerMoved(wx, wy, leftDown: false, KeyModifiers.None);

        var (sx, sy) = LayoutSnapping.SnapPoint(wx, wy, model.SnapDbu, suspend: false);

        vm.OnPointerPressed(wx, wy, KeyModifiers.None);

        Assert.False(vm.IsPastePlacementActive);
        var placed = Assert.IsType<RectShape>(Assert.Single(model.Shapes));
        Assert.True(sx == placed.X1, $"{name}: anchor X should land on the snapped cursor");
        Assert.True(sy == placed.Y1, $"{name}: anchor Y should land on the snapped cursor");
    }

    [Fact]
    public void PastePlacement_Escape_CancelsWithNoCommandPushed()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model);
        vm.BeginPastePlacement([new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 }], 0, 0);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.IsPastePlacementActive);
        Assert.Empty(model.Shapes);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void PastePlacement_GhostRendersAtAnchor_BeforeAnyPointerMove()
    {
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model);
        vm.BeginPastePlacement([new RectShape { Layer = Layer1, X1 = 5_000, Y1 = 5_000, X2 = 6_000, Y2 = 6_000 }], anchorX: 5_000, anchorY: 5_000);

        var preview = Assert.Single(vm.Overlay.PastePreview!);
        var r = Assert.IsType<RectShape>(preview);
        Assert.Equal(5_000, r.X1); // zero delta before any move
    }

    // ── Gate 3: cross-cell / cross-technology paste ───────────────────────────────────────────────

    [Fact]
    public void Paste_AcrossDifferentTechnologies_NoMatchRequiresConfirmation_DefaultsToKeepUnknown_NothingDropped()
    {
        var srcModel = FreshModel();
        var srcTech = new Technology { Layers = [new LayerDef { Key = Layer1, Name = "SourceCopper" }] };
        srcModel.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var srcVm = new LayoutEditorViewModel(srcModel) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        srcVm.Technology = srcTech;
        Click(srcVm, 500, 500);
        var payload = srcVm.BuildCopyPayload();
        Assert.NotNull(payload);

        var destModel = FreshModel();
        var destTech = new Technology { Layers = [new LayerDef { Key = new LayerKey(9, 0), Name = "OtherLayer" }] };
        var destVm = new LayoutEditorViewModel(destModel);
        destVm.Technology = destTech;

        var rescale = destVm.RescaleFragment(payload!);
        var mapping = destVm.ProposeFragmentLayerMapping(rescale.Shapes, payload!.Layers);
        var row = Assert.Single(mapping);
        Assert.Equal(Layer1, row.Source);
        Assert.Equal(LayerMatchKind.NoMatch, row.Match);
        Assert.True(LayoutLayerMapping.RequiresConfirmation(mapping)); // R-L1g-2: this MUST be confirmed

        var choices = LayoutLayerMapping.BuildChoices(mapping); // rows default to Keep-as-unknown
        var reconciled = destVm.ApplyFragmentReconciliation(rescale.Shapes, payload.Layers, choices);
        destVm.PasteInPlace(reconciled);

        var pasted = Assert.Single(destModel.Shapes);
        Assert.Equal(Layer1, pasted.Layer); // unresolved key preserved, renders via FallbackPalette
    }

    // ── L1g gate 2: the Gap 2 regression test (docs/sonnet-briefs/brief-L1g-technology-retarget.md §0)
    // — Drill (7,0) and Soldermask Top (3,0) both exist at those SAME keys in MMIC GaAs, meaning WRONG
    // MEANING (Via, Cap Dielectric). This is the exact scenario that used to sail through silently
    // under L1f's GetMissingLayers ("which keys are absent?" — none, since both starter techs share a
    // key range). The right question (LayoutLayerMapping.Propose, "which layers need confirmation?")
    // must flag both as requiring it, and without any user choice, neither is silently rewritten onto
    // the numerically-coincident MMIC layer.
    [Fact]
    public void Paste_PcbDrillAndSoldermask_IntoMmic_RequiresConfirmation_NeverSilentlyRewritesOntoUnrelatedLayer()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var mmic = StarterTechnologies.MmicGaAs();
        var drill = new LayerKey(7, 0);
        var soldermaskTop = new LayerKey(3, 0);

        var srcModel = FreshModel();
        srcModel.Shapes.Add(new RectShape { Layer = drill, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        srcModel.Shapes.Add(new RectShape { Layer = soldermaskTop, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var srcVm = new LayoutEditorViewModel(srcModel) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        srcVm.Technology = pcb;
        srcVm.SelectAllCommand.Execute(null);
        var payload = srcVm.BuildCopyPayload();
        Assert.NotNull(payload);

        var destModel = FreshModel();
        var destVm = new LayoutEditorViewModel(destModel);
        destVm.Technology = mmic;

        var rescale = destVm.RescaleFragment(payload!);
        var mapping = destVm.ProposeFragmentLayerMapping(rescale.Shapes, payload!.Layers);

        // The confirmation dialog is REQUIRED — this is what fails against the old GetMissingLayers-
        // driven trigger, which found nothing missing (both keys exist in MMIC GaAs already, just
        // under a completely different meaning — Via/Substrate, not Soldermask/Drill).
        Assert.True(LayoutLayerMapping.RequiresConfirmation(mapping));
        Assert.All(mapping, r => Assert.Equal(LayerMatchKind.SameKeyDifferentName, r.Match));

        // Without any user choice, nothing is silently rewritten onto Via/Cap Dielectric.
        var choices = LayoutLayerMapping.BuildChoices(mapping);
        var reconciled = destVm.ApplyFragmentReconciliation(rescale.Shapes, payload.Layers, choices);

        Assert.Equal(2, reconciled.Count); // gate 12 — nothing dropped
        Assert.Contains(reconciled, s => s.Layer == drill);          // NOT rewritten onto Via (3,0)
        Assert.Contains(reconciled, s => s.Layer == soldermaskTop);  // NOT rewritten onto anything else
    }

    // ── L1g gate 3: same-technology paste stays silent — R-L1g-2's confirmation rule must not make
    // the ordinary same-tech case noisy ───────────────────────────────────────────────────────────
    [Fact]
    public void Paste_SameTechnologyBothSides_RaisesNoConfirmation_AndRewritesNothing()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var drill = new LayerKey(7, 0);

        var srcModel = FreshModel();
        srcModel.Shapes.Add(new RectShape { Layer = drill, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var srcVm = new LayoutEditorViewModel(srcModel) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        srcVm.Technology = pcb;
        Click(srcVm, 500, 500);
        var payload = srcVm.BuildCopyPayload();
        Assert.NotNull(payload);

        var destModel = FreshModel();
        var destVm = new LayoutEditorViewModel(destModel);
        destVm.Technology = pcb; // same technology instance/content as the source

        var rescale = destVm.RescaleFragment(payload!);
        var mapping = destVm.ProposeFragmentLayerMapping(rescale.Shapes, payload!.Layers);

        Assert.False(LayoutLayerMapping.RequiresConfirmation(mapping));
        Assert.All(mapping, r => Assert.Equal(LayerMatchKind.SameKeySameName, r.Match));

        var choices = LayoutLayerMapping.BuildChoices(mapping);
        var reconciled = destVm.ApplyFragmentReconciliation(rescale.Shapes, payload.Layers, choices);

        Assert.Equal(drill, Assert.Single(reconciled).Layer); // key unchanged
    }

    [Fact]
    public void Paste_DifferentDbuPerMicron_RescalesBeforeReconciliation()
    {
        var srcModel = FreshModel(dbuPerMicron: 1000);
        srcModel.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 });
        var srcVm = new LayoutEditorViewModel(srcModel) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(srcVm, 500, 500);
        var payload = srcVm.BuildCopyPayload();

        var destModel = FreshModel(dbuPerMicron: 10_000); // 10x finer
        var destVm = new LayoutEditorViewModel(destModel);

        var rescale = destVm.RescaleFragment(payload!);
        Assert.Empty(rescale.Warnings);
        var r = Assert.IsType<RectShape>(rescale.Shapes[0]);
        Assert.Equal(10_000, r.X2);
    }

    // ── Gate 5 (VM level): "Add to the technology" installs a live override, never writes to disk ──

    [Fact]
    public void ApplyFragmentReconciliation_AddToTechnology_FiresLiveOverrideRequest_NeverTouchesDisk()
    {
        var model = FreshModel();
        var tech = new Technology { Name = "Dest", Layers = [] };
        var vm = new LayoutEditorViewModel(model);
        vm.ApplyTechResolution(new TechResolution(tech, "/fake/path.ctech", TechResolutionSource.LayoutRef, []));

        var unknownLayer = new LayerKey(7, 0);
        var fragmentDef = new LayerDef { Key = unknownLayer, Name = "FromSource" };
        var shapes = new List<LayoutShape> { new RectShape { Layer = unknownLayer, X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 } };
        var choices = new Dictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>
        {
            [unknownLayer] = new(LayoutFragment.LayerReconciliationAction.AddToTechnology),
        };

        (string Path, Technology Tech)? captured = null;
        vm.RequestAddLayerToTechnology += (path, t) => captured = (path, t);

        var result = vm.ApplyFragmentReconciliation(shapes, [fragmentDef], choices);

        Assert.NotNull(captured);
        Assert.Equal("/fake/path.ctech", captured!.Value.Path);
        Assert.Contains(captured.Value.Tech.Layers, l => l.Key == unknownLayer && l.Name == "FromSource");
        // The event's Technology is an independent clone, not the live vm.Technology reference.
        Assert.NotSame(tech, captured.Value.Tech);
        Assert.DoesNotContain(tech.Layers, l => l.Key == unknownLayer);
        Assert.Equal(unknownLayer, Assert.Single(result).Layer);
    }
}
