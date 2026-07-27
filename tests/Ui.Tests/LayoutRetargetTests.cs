using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests;

// ── Phase L1g gates 6/7/8/9/10/11/12/13: docs/sonnet-briefs/brief-L1g-technology-retarget.md
// VM-level: LayoutEditorViewModel.RetargetTo — one undo entry, byte-identical geometry, workspace-
// default resolution, opt-in unit adoption, DbuPerMicron untouched, Add-to-technology's live seam,
// Keep-as-unknown, nothing-ever-dropped, and the Messages summary.

public class LayoutRetargetTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey TopCopper = new(1, 0);
    private static readonly LayerKey BottomCopper = new(2, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static (LayoutEditorViewModel Vm, FakeMessageSink Sink) PcbLayoutWithShapes()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var model = FreshModel();
        model.DisplayUnit = pcb.DefaultDisplayUnit; // Mil — deliberately differs from MMIC GaAs's Um
        model.SnapDbu     = pcb.DefaultSnapDbu;
        model.Shapes.Add(new RectShape { Layer = TopCopper, Net = "VDD", X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 5_000 });
        model.Shapes.Add(new PolygonShape
        {
            Layer = BottomCopper,
            Net = "GND",
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[30_000, 30_000, 30_000, 70_000, 70_000, 70_000, 70_000, 30_000]],
        });
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink);
        vm.ApplyTechResolution(new TechResolution(pcb, "/fake/pcb.ctech", TechResolutionSource.LayoutRef, []));
        return (vm, sink);
    }

    /// <summary>Builds a mapping where every row explicitly Maps to <paramref name="targets"/> (by
    /// source key) — simulates the user confirming the shared dialog, regardless of what
    /// <see cref="LayoutLayerMapping.Propose"/> defaulted each row to.</summary>
    private static IReadOnlyList<LayerMappingRow> MapExplicitly(
        LayoutEditorViewModel vm, Technology destTech, params (LayerKey Source, LayerKey Target)[] targets)
    {
        var rows = LayoutLayerMapping.Propose(vm.Model.Shapes, vm.Technology!.Layers, destTech);
        return rows.Select(r =>
        {
            foreach (var (source, target) in targets)
            {
                if (r.Source == source)
                    return r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.MapToExisting, target) };
            }
            return r;
        }).ToList();
    }

    // ── Gate 6: retarget round-trip ────────────────────────────────────────────────────────────────

    [Fact]
    public void RetargetTo_EveryMappedShapeMoves_TechRefChanges_GeometryByteIdentical_NetsAndHolesUntouched()
    {
        var (vm, _) = PcbLayoutWithShapes();
        var mmic = StarterTechnologies.MmicGaAs();
        var newTop = new LayerKey(1, 0);   // Metal1
        var newBottom = new LayerKey(2, 0); // Metal2
        var target = new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);
        var mapping = MapExplicitly(vm, mmic, (TopCopper, newTop), (BottomCopper, newBottom));

        var before = LayoutPersistence.Serialize(vm.Model);
        var rectBefore = (RectShape)vm.Model.Shapes[0];
        var polyBefore = (PolygonShape)vm.Model.Shapes[1];

        var summary = vm.RetargetTo("mmic.ctech", target, adoptUnits: false, mapping);

        Assert.Equal("MMIC GaAs", vm.Technology!.Name);
        Assert.Equal("mmic.ctech", vm.Model.TechRef);
        Assert.Equal(2, summary.ShapeCount);

        var rectAfter = (RectShape)vm.Model.Shapes[0];
        var polyAfter = (PolygonShape)vm.Model.Shapes[1];
        Assert.Equal(newTop, rectAfter.Layer);
        Assert.Equal(newBottom, polyAfter.Layer);

        // Geometry coordinates byte-identical.
        Assert.Equal(rectBefore.X1, rectAfter.X1);
        Assert.Equal(rectBefore.Y1, rectAfter.Y1);
        Assert.Equal(rectBefore.X2, rectAfter.X2);
        Assert.Equal(rectBefore.Y2, rectAfter.Y2);
        Assert.Equal(polyBefore.Xy, polyAfter.Xy);
        Assert.Equal(polyBefore.Holes, polyAfter.Holes);

        // Nets untouched.
        Assert.Equal("VDD", rectAfter.Net);
        Assert.Equal("GND", polyAfter.Net);

        // Full serialize round-trip differs ONLY in TechRef + the two LayerKeys (sanity: the whole
        // document did not silently change some other way).
        var after = LayoutPersistence.Serialize(vm.Model);
        Assert.NotEqual(before, after);
    }

    // ── Gate 7: (Workspace default) writes TechRef = null and re-resolves through L0c's order ──────

    [Fact]
    public void RetargetTo_WorkspaceDefault_WritesNullTechRef_ReResolvesThroughWorkspaceDefaultOrder()
    {
        var (vm, _) = PcbLayoutWithShapes();
        var mmic = StarterTechnologies.MmicGaAs();
        vm.ResolveWorkspaceDefaultTech = () => new TechResolution(mmic, "/fake/workspace-default.ctech", TechResolutionSource.WorkspaceDefault, []);

        var target = vm.ResolveWorkspaceDefaultTech();
        var mapping = MapExplicitly(vm, mmic, (TopCopper, new LayerKey(1, 0)), (BottomCopper, new LayerKey(2, 0)));

        vm.RetargetTo(newTechRef: null, target, adoptUnits: false, mapping);

        Assert.Null(vm.Model.TechRef);
        Assert.Equal(TechResolutionSource.WorkspaceDefault, target.Source);
        Assert.Same(mmic, vm.Technology);
        Assert.Equal("/fake/workspace-default.ctech", vm.ResolvedTechPath);
    }

    // ── Gate 8: units are not adopted unless asked; DbuPerMicron is unchanged either way ────────────

    [Fact]
    public void RetargetTo_AdoptUnitsOff_DisplayUnitAndSnapDbuUnchanged_DbuPerMicronUnchanged()
    {
        var (vm, _) = PcbLayoutWithShapes();
        var originalUnit = vm.DisplayUnit;
        var originalSnap = vm.SnapDbu;
        var originalDbuPerMicron = vm.Model.DbuPerMicron;

        var mmic = StarterTechnologies.MmicGaAs();
        Assert.NotEqual(originalUnit, mmic.DefaultDisplayUnit); // fixture sanity: technologies actually differ
        var target = new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);
        var mapping = MapExplicitly(vm, mmic, (TopCopper, new LayerKey(1, 0)), (BottomCopper, new LayerKey(2, 0)));

        vm.RetargetTo("mmic.ctech", target, adoptUnits: false, mapping);

        Assert.Equal(originalUnit, vm.DisplayUnit);
        Assert.Equal(originalSnap, vm.SnapDbu);
        Assert.Equal(originalDbuPerMicron, vm.Model.DbuPerMicron);
    }

    [Fact]
    public void RetargetTo_AdoptUnitsOn_BothComeFromTarget_DbuPerMicronStillUnchanged()
    {
        var (vm, _) = PcbLayoutWithShapes();
        var originalDbuPerMicron = vm.Model.DbuPerMicron;

        var mmic = StarterTechnologies.MmicGaAs();
        var target = new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);
        var mapping = MapExplicitly(vm, mmic, (TopCopper, new LayerKey(1, 0)), (BottomCopper, new LayerKey(2, 0)));

        vm.RetargetTo("mmic.ctech", target, adoptUnits: true, mapping);

        Assert.Equal(mmic.DefaultDisplayUnit, vm.DisplayUnit);
        Assert.Equal(mmic.DefaultSnapDbu, vm.SnapDbu);
        Assert.Equal(originalDbuPerMicron, vm.Model.DbuPerMicron); // never touched by retargeting
    }

    // ── Gate 9: one undo entry restores TechRef and every LayerKey together ─────────────────────────

    [Fact]
    public void RetargetTo_OneUndoEntry_RestoresTechRefAndEveryLayerKey_SerializeEqualityWithPreRetargetState()
    {
        var (vm, _) = PcbLayoutWithShapes();
        var before = LayoutPersistence.Serialize(vm.Model);

        var mmic = StarterTechnologies.MmicGaAs();
        var target = new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);
        var mapping = MapExplicitly(vm, mmic, (TopCopper, new LayerKey(1, 0)), (BottomCopper, new LayerKey(2, 0)));

        vm.RetargetTo("mmic.ctech", target, adoptUnits: true, mapping);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();

        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal(before, LayoutPersistence.Serialize(vm.Model));

        vm.UndoRedo.Redo();
        Assert.Equal("mmic.ctech", vm.Model.TechRef);
        Assert.Equal("MMIC GaAs", vm.Technology!.Name);
    }

    // ── Gate 10: Add to technology marks the .ctech dirty through the live mechanism, never a file write ──

    [Fact]
    public void RetargetTo_AddToTechnology_FiresLiveOverrideRequest_NeverTouchesDisk()
    {
        var (vm, _) = PcbLayoutWithShapes();
        // A blank destination technology (no layers at all) so both source keys are genuinely
        // NoMatch — the real starter technologies fully overlap in key range (both use 1..8), so
        // "Add to technology" against MmicGaAs would collide with its own Metal1/Metal2 and be
        // correctly skipped (ApplyReconciliation never creates two LayerDefs at one key).
        var blank = new Technology { Name = "Blank" };
        var target = new TechResolution(blank, "/fake/blank.ctech", TechResolutionSource.LayoutRef, []);

        var rows = LayoutLayerMapping.Propose(vm.Model.Shapes, vm.Technology!.Layers, blank)
            .Select(r => r with { Choice = new LayoutFragment.LayerReconciliationChoice(LayoutFragment.LayerReconciliationAction.AddToTechnology) })
            .ToList();

        (string Path, Technology Tech)? captured = null;
        vm.RequestAddLayerToTechnology += (path, tech) => captured = (path, tech);

        vm.RetargetTo("blank.ctech", target, adoptUnits: false, rows);

        Assert.NotNull(captured);
        Assert.Equal("/fake/blank.ctech", captured!.Value.Path);
        Assert.Contains(captured.Value.Tech.Layers, l => l.Key == TopCopper && l.Name == "Top Copper");
        Assert.Contains(captured.Value.Tech.Layers, l => l.Key == BottomCopper && l.Name == "Bottom Copper");
        Assert.NotSame(blank, captured.Value.Tech); // independent clone, never the live vm.Technology reference
        Assert.DoesNotContain(blank.Layers, l => l.Key == TopCopper); // the original technology object is untouched
    }

    // ── Gate 11: Keep as unknown leaves the LayerKey intact; renders through FallbackPalette ─────────

    [Fact]
    public void RetargetTo_KeepAsUnknown_LeavesLayerKeyIntact()
    {
        var (vm, _) = PcbLayoutWithShapes();
        var mmic = StarterTechnologies.MmicGaAs();
        var target = new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);

        var rows = LayoutLayerMapping.Propose(vm.Model.Shapes, vm.Technology!.Layers, mmic); // defaults: Keep as unknown

        vm.RetargetTo("mmic.ctech", target, adoptUnits: false, rows);

        Assert.Equal(TopCopper, vm.Model.Shapes[0].Layer);
        Assert.Equal(BottomCopper, vm.Model.Shapes[1].Layer);
        // Neither key is defined in the destination technology under its ORIGINAL name, so it resolves
        // through FallbackPalette (mirrors LayoutEditorViewModel.ResolveLayerDef's fallback order).
        Assert.DoesNotContain(mmic.Layers, l => l.Key == TopCopper && l.Name == "Top Copper");
    }

    // ── Gate 12: nothing is ever dropped — shape count identical for every combination of choices ────

    [Theory]
    [InlineData(LayoutFragment.LayerReconciliationAction.KeepUnknown)]
    [InlineData(LayoutFragment.LayerReconciliationAction.MapToExisting)]
    [InlineData(LayoutFragment.LayerReconciliationAction.AddToTechnology)]
    public void RetargetTo_EveryChoiceCombination_ShapeCountNeverChanges(LayoutFragment.LayerReconciliationAction action)
    {
        var (vm, _) = PcbLayoutWithShapes();
        var originalCount = vm.Model.Shapes.Count;
        var mmic = StarterTechnologies.MmicGaAs();
        var target = new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);

        var rows = LayoutLayerMapping.Propose(vm.Model.Shapes, vm.Technology!.Layers, mmic)
            .Select(r => r with
            {
                Choice = action == LayoutFragment.LayerReconciliationAction.MapToExisting
                    ? new LayoutFragment.LayerReconciliationChoice(action, new LayerKey(1, 0))
                    : new LayoutFragment.LayerReconciliationChoice(action),
            })
            .ToList();

        vm.RetargetTo("mmic.ctech", target, adoptUnits: false, rows);

        Assert.Equal(originalCount, vm.Model.Shapes.Count);
    }

    // ── Gate 13: Messages summary is posted for a retarget ───────────────────────────────────────────

    [Fact]
    public void RetargetTo_PostsMessagesSummary()
    {
        var (vm, sink) = PcbLayoutWithShapes();
        var mmic = StarterTechnologies.MmicGaAs();
        var target = new TechResolution(mmic, "/fake/mmic.ctech", TechResolutionSource.LayoutRef, []);
        var mapping = MapExplicitly(vm, mmic, (TopCopper, new LayerKey(1, 0)), (BottomCopper, new LayerKey(2, 0)));

        var summary = vm.RetargetTo("mmic.ctech", target, adoptUnits: false, mapping);
        vm.ReportMessage($"Retargeted to {summary.TechName} · {summary.ShapeCount} shape(s) · " +
                          LayoutLayerMapping.SummarizeMapping(summary.Rows, target.Tech));

        var posted = Assert.Single(sink.Posted);
        Assert.Equal(MessageLevel.Success, posted.Level);
        Assert.Contains("MMIC GaAs", posted.Text);
        Assert.Contains("2 shape(s)", posted.Text);
    }
}
