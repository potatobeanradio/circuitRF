// Gate 7 (docs/sonnet-briefs/brief-L2b-spatial-index.md §6): "after each of add, delete, replace,
// move, scale, paste, undo, redo, flatten, boolean op, technology retarget and resolution change, a
// full linear scan and an index query return the SAME shape set. Run this as a table-driven test over
// every mutation; one missed path is the whole risk of this phase." One test method per mutation kind,
// all funneling through the SAME assertion helper, checked at three checkpoints per mutation
// (immediately after, after Undo, after Redo) so "undo"/"redo" are covered as an intrinsic part of
// every row rather than one contrived case.

using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class LayoutSpatialIndexFreshnessTests
{
    private static LayoutView FreshModel(long snapDbu = 100) => new()
    {
        DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    /// <summary>The gate-7 assertion itself — a query over an extent covering every plausible shape
    /// position must return EXACTLY the same set as a from-scratch linear scan of the current
    /// <c>Shapes</c> list, using the identical conservative-bbox notion the index itself stores.</summary>
    private static void AssertIndexMatchesLinearScan(LayoutView view, string when)
    {
        var everything = new Bbox(long.MinValue / 4, long.MinValue / 4, long.MaxValue / 4, long.MaxValue / 4);
        var expected = new List<int>();
        for (int i = 0; i < view.Shapes.Count; i++)
        {
            var bb = LayoutSpatialIndex.ConservativeBboxOf(view.Shapes[i]);
            if (!bb.IsEmpty) expected.Add(i);
        }
        var actual = view.SpatialIndex.QueryIntersecting(view.Shapes, everything);
        Assert.True(expected.SequenceEqual(actual.OrderBy(x => x)),
            $"[{when}] index candidates {{{string.Join(",", actual)}}} != linear scan {{{string.Join(",", expected)}}}");
    }

    private static void AssertAtAllThreeCheckpoints(LayoutEditorViewModel vm)
    {
        AssertIndexMatchesLinearScan(vm.Model, "after execute");
        vm.UndoRedo.Undo();
        AssertIndexMatchesLinearScan(vm.Model, "after undo");
        vm.UndoRedo.Redo();
        AssertIndexMatchesLinearScan(vm.Model, "after redo");
    }

    // ── add (draw) ────────────────────────────────────────────────────────────

    [Fact]
    public void Add_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var vm = new LayoutEditorViewModel(FreshModel()) { ActiveTool = LayoutEditorViewModel.Tool.Rect };
        vm.OnPointerPressed(0, 0, KeyModifiers.None);
        vm.OnPointerMoved(5000, 5000, true, KeyModifiers.None);
        vm.OnPointerReleased(5000, 5000, KeyModifiers.None);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── delete ────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        for (int i = 0; i < 5; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = i * 10_000, Y1 = 0, X2 = i * 10_000 + 5000, Y2 = 5000 });
        var vm = SelectVm(model);

        Click(vm, 2500, 2500);
        Click(vm, 12_500, 2500, KeyModifiers.Shift);
        vm.OnKeyDown(Key.Delete, KeyModifiers.None);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── replace (single-shape vertex/handle edit) ────────────────────────────

    [Fact]
    public void ReplaceSingleShape_HandleDrag_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000] });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(1000, 2000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(1000, 2000, KeyModifiers.None);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── move / nudge ──────────────────────────────────────────────────────────

    [Fact]
    public void Move_DragWholeShape_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 5000 });
        var vm = SelectVm(model);

        vm.OnPointerPressed(2500, 2500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(2500 + 20_000, 2500 + 20_000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(2500 + 20_000, 2500 + 20_000, KeyModifiers.None);

        AssertAtAllThreeCheckpoints(vm);
    }

    [Fact]
    public void Nudge_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 5000 });
        var vm = SelectVm(model);

        Click(vm, 2500, 2500);
        vm.OnKeyDown(Key.Right, KeyModifiers.None);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── scale (numeric) ───────────────────────────────────────────────────────

    [Fact]
    public void Scale_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 5000 });
        var vm = SelectVm(model);

        Click(vm, 2500, 2500);
        vm.ApplyScale(2.0, 2.0, 0, 0);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── paste ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Paste_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 5000, Y2 = 5000 });
        var vm = SelectVm(model);

        Click(vm, 2500, 2500);
        var payload = vm.BuildCopyPayload();
        Assert.NotNull(payload);
        var rescale = vm.RescaleFragment(payload!);
        var reconciled = vm.ApplyFragmentReconciliation(rescale.Shapes, [], null);
        vm.PasteInPlace(reconciled);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── flatten to polygon ────────────────────────────────────────────────────

    [Fact]
    public void Flatten_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 0, Cy = 0, R = 5000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 0);
        vm.FlattenSelectionToPolygon(1000);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── boolean op (union) ────────────────────────────────────────────────────

    [Fact]
    public void BooleanUnion_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 5000, Y1 = 0, X2 = 15_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        Click(vm, 2500, 2500);
        Click(vm, 12_500, 2500, KeyModifiers.Shift);
        vm.ApplyUnion();

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── technology retarget ───────────────────────────────────────────────────

    [Fact]
    public void TechnologyRetarget_IndexMatchesLinearScan_AtAllCheckpoints()
    {
        var pcb = StarterTechnologies.Pcb2Layer();
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = pcb.Layers[0].Key, X1 = 0, Y1 = 0, X2 = 5000, Y2 = 5000 });
        var vm = SelectVm(model);
        vm.ApplyTechResolution(new TechResolution(pcb, "/fake/pcb.ctech", TechResolutionSource.LayoutRef, []));

        var mapping = LayoutLayerMapping.Propose(model.Shapes, pcb.Layers, pcb); // same tech -> trivial identity mapping
        var target = new TechResolution(pcb, "/fake/pcb-renamed.ctech", TechResolutionSource.LayoutRef, []);
        vm.RetargetTo("/fake/pcb-renamed.ctech", target, adoptUnits: false, mapping);

        AssertAtAllThreeCheckpoints(vm);
    }

    // ── DBU resolution change ─────────────────────────────────────────────────
    // No VM/command seam exists for this yet (confirmed: LayoutScaling.TryChangeResolution has no
    // production call site) — this exercises the safety net directly: an un-classified NotifyChanged()
    // call defaults to Full, which is always correct regardless of what mutated.

    [Fact]
    public void ResolutionChange_IndexMatchesLinearScan_AfterDirectModelCall()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        _ = model.SpatialIndex.QueryIntersecting(model.Shapes, new Bbox(-1, -1, 1, 1)); // seed the index once

        Assert.True(LayoutScaling.TryChangeResolution(model, model.DbuPerMicron * 10, out _));
        model.NotifyChanged(); // no VM seam yet — simulates what a future caller would do; defaults to Full

        AssertIndexMatchesLinearScan(model, "after resolution change");
    }
}
