using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests;

// ── Phase L1e gates 6, 7, 11, 12, 13: docs/sonnet-briefs/brief-L1e-clipper-operations.md §3/§4/§5
// VM-level: selection wiring, one-undo-entry, restore-at-original-index, Messages reporting.

public class LayoutBooleanOperationsViewModelTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel() => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = 1000,
    };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Gate 13: one undo entry, restoring all operands at their original indices ───────────────

    [Fact]
    public void Union_OneUndoEntry_RestoresBothOperandsAtOriginalIndices_ByteIdentical()
    {
        var model = FreshModel();
        var before = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        var overlap = new RectShape { Layer = Layer1, X1 = 5000, Y1 = 5000, X2 = 15_000, Y2 = 15_000 };
        var untouched = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 };
        model.Shapes.Add(before);      // index 0
        model.Shapes.Add(overlap);     // index 1
        model.Shapes.Add(untouched);   // index 2

        var jsonBefore = LayoutPersistence.Serialize(model);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 2500, 2500);
        Click(vm, 12_000, 12_000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        vm.ApplyUnion();

        Assert.Equal(2, model.Shapes.Count); // merged shape + the untouched third rect
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();

        Assert.Equal(3, model.Shapes.Count);
        Assert.Same(before, model.Shapes[0]);
        Assert.Same(overlap, model.Shapes[1]);
        Assert.Same(untouched, model.Shapes[2]);
        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
    }

    // ── L1h gate 2 (brief-L1h-scale-and-context-menu.md R-L1h-1): Union groups by layer — this is
    // what "Merge" used to do; Merge is deleted, and this is now what Union itself does. ───────────

    [Fact]
    public void ApplyUnion_CrossLayerSelection_UnionsWithinEachLayer_OneShapePerLayer_OneUndoEntry()
    {
        var model = FreshModel();
        var layer2 = new LayerKey(2, 0);
        var a1 = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        var a2 = new RectShape { Layer = Layer1, X1 = 5_000, Y1 = 5_000, X2 = 15_000, Y2 = 15_000 };
        var b1 = new RectShape { Layer = layer2, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 };
        var b2 = new RectShape { Layer = layer2, X1 = 105_000, Y1 = 5_000, X2 = 115_000, Y2 = 15_000 };
        model.Shapes.Add(a1); model.Shapes.Add(a2); model.Shapes.Add(b1); model.Shapes.Add(b2);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.ApplyUnion();

        Assert.Equal(2, model.Shapes.Count); // one union'd shape per layer, both still on their own layer
        Assert.Contains(model.Shapes, s => s.Layer == Layer1);
        Assert.Contains(model.Shapes, s => s.Layer == layer2);
        Assert.True(vm.UndoRedo.CanUndo);

        vm.UndoRedo.Undo();
        Assert.Equal(4, model.Shapes.Count);
        Assert.Same(a1, model.Shapes[0]);
        Assert.Same(a2, model.Shapes[1]);
        Assert.Same(b1, model.Shapes[2]);
        Assert.Same(b2, model.Shapes[3]);
    }

    // ── L1h gate 5: an enabled Union that combines nothing (disjoint same-layer shapes) reports
    // through Messages rather than silently no-op'ing (R-L1h-3). ─────────────────────────────────

    [Fact]
    public void ApplyUnion_DisjointSameLayerShapes_PostsMessagesNote_NothingCombined()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1_000, Y2 = 1_000 };
        var b = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 101_000, Y2 = 1_000 }; // far away, no overlap
        model.Shapes.Add(a); model.Shapes.Add(b);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Click(vm, 100_500, 500, KeyModifiers.Shift);

        Assert.True(vm.BooleanOpAvailability.CanExecute); // legitimately enabled — 2 shapes share a layer

        vm.ApplyUnion();

        Assert.Equal(2, model.Shapes.Count); // both shapes survive, untouched, just not combined
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Info && m.Text.Contains("did not overlap"));
    }

    // ── Gate 6: multiple disjoint results, undo restores the single original at its original index ──

    [Fact]
    public void Difference_SplitsIntoTwo_BothAtSensibleIndices_UndoRestoresOriginalAtOriginalIndex()
    {
        var model = FreshModel();
        var before = new RectShape { Layer = Layer1, X1 = -100_000, Y1 = -100_000, X2 = -90_000, Y2 = -90_000 };
        var bar = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 10_000 };
        var strip = new RectShape { Layer = Layer1, X1 = 45_000, Y1 = -10_000, X2 = 55_000, Y2 = 20_000 };
        model.Shapes.Add(before);  // index 0 — never touched by this op
        model.Shapes.Add(bar);     // index 1
        model.Shapes.Add(strip);   // index 2

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 25_000, 5000);              // selects bar (first)
        Click(vm, 50_000, 15_000, KeyModifiers.Shift); // adds strip

        vm.ApplyDifference();

        // before + two split pieces = 3 shapes; both pieces land at index 1/2 (the lowest removed
        // index), before stays at index 0.
        Assert.Equal(3, model.Shapes.Count);
        Assert.Same(before, model.Shapes[0]);
        Assert.IsType<PolygonShape>(model.Shapes[1]);
        Assert.IsType<PolygonShape>(model.Shapes[2]);

        vm.UndoRedo.Undo();

        Assert.Equal(3, model.Shapes.Count);
        Assert.Same(before, model.Shapes[0]);
        Assert.Same(bar, model.Shapes[1]);
        Assert.Same(strip, model.Shapes[2]);
    }

    // ── Gate 7: net propagation reporting ─────────────────────────────────────────

    [Fact]
    public void Union_DifferingNets_ReportsWarning()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, Net = "VCC", X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        var b = new RectShape { Layer = Layer1, Net = "GND", X1 = 5000, Y1 = 5000, X2 = 15_000, Y2 = 15_000 };
        model.Shapes.Add(a);
        model.Shapes.Add(b);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 2500, 2500);
        Click(vm, 12_000, 12_000, KeyModifiers.Shift);

        vm.ApplyUnion();

        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning && m.Text.Contains("net"));
        Assert.Null(model.Shapes[0].Net);
    }

    [Fact]
    public void CurvedOperandWarning_FiresOnlyOncePerSession()
    {
        var model = FreshModel();
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        void UnionCircleWithRect()
        {
            model.Shapes.Clear();
            model.Shapes.Add(new CircleShape { Layer = Layer1, Cx = 5000, Cy = 5000, R = 5000 });
            model.Shapes.Add(new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
            Click(vm, 5000, 5000);
            Click(vm, 1000, 1000, KeyModifiers.Shift);
            vm.ApplyUnion();
        }

        UnionCircleWithRect();
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning && m.Text.Contains("flattened"));
        int countAfterFirst = sink.Posted.Count(m => m.Text.Contains("flattened"));

        UnionCircleWithRect();
        int countAfterSecond = sink.Posted.Count(m => m.Text.Contains("flattened"));

        Assert.Equal(countAfterFirst, countAfterSecond); // warned once, not twice
    }

    // ── Offset via the VM's staged dimension field ────────────────────────────────

    [Fact]
    public void Offset_ViaStagedField_GrowsSelection_OneUndoEntry()
    {
        var model = FreshModel();
        var r = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        model.Shapes.Add(r);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 5000, 5000);
        Assert.True(vm.CanOffsetSelection);

        vm.CommitOffsetText("1um"); // 1 um = 1000 dbu at 1000 dbu/um
        vm.ApplyOffsetToSelection();

        var grown = Assert.IsType<PolygonShape>(Assert.Single(model.Shapes));
        Assert.Equal(new Bbox(-1000, -1000, 11_000, 11_000), LayoutGeometry.BboxOf(grown));

        vm.UndoRedo.Undo();
        Assert.Same(r, Assert.Single(model.Shapes));
    }

    [Fact]
    public void Offset_OverShrink_AnnihilatesAndReports()
    {
        var model = FreshModel();
        var r = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(r);

        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        vm.CommitOffsetText("-10um");
        vm.ApplyOffsetToSelection();

        Assert.Empty(model.Shapes);
        Assert.Contains(sink.Posted, m => m.Text.Contains("removed"));

        vm.UndoRedo.Undo();
        Assert.Same(r, Assert.Single(model.Shapes));
    }

    // ── Gate 12: repair via the VM, L1d self-intersection flag clears ─────────────

    [Fact]
    public void RepairSelfIntersection_ViaVm_ClearsTheFlag()
    {
        var model = FreshModel();
        var bowtie = new PolygonShape { Layer = Layer1, Xy = [0, 0, 100, 100, 100, 0, 0, 100] };
        model.Shapes.Add(bowtie);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 50, 50);
        Assert.True(vm.CanRepairSelected);

        vm.RepairSelfIntersection(vm.SelectedIndices[0]);

        Assert.All(model.Shapes, s => Assert.False(LayoutSelfIntersection.Test(s, null)));

        vm.UndoRedo.Undo();
        Assert.Same(bowtie, Assert.Single(model.Shapes));
    }

    // ── Gate 11: Flatten to Polygon ────────────────────────────────────────────────

    [Fact]
    public void FlattenSelection_Circle_BecomesPolygonWithinTolerance()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 100_000 };
        model.Shapes.Add(circle);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0, tolDbu: 200_000);

        vm.FlattenSelectionToPolygon(1000);

        var poly = Assert.IsType<PolygonShape>(Assert.Single(model.Shapes));
        int n = poly.Xy.Length / 2;
        for (int i = 0; i < n; i++)
        {
            double dx = poly.Xy[2 * i], dy = poly.Xy[2 * i + 1];
            double dist = System.Math.Sqrt(dx * dx + dy * dy);
            Assert.True(System.Math.Abs(dist - 100_000) <= 1001);
        }
    }

    [Fact]
    public void PreviewFlattenVertexCount_MatchesWhatTheCommandProduces()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 100_000 };
        model.Shapes.Add(circle);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0, tolDbu: 200_000);

        int preview = vm.PreviewFlattenVertexCount(vm.SelectedIndices[0], 1000);
        vm.FlattenSelectionToPolygon(1000);
        var poly = Assert.IsType<PolygonShape>(Assert.Single(model.Shapes));

        Assert.Equal(preview, poly.Xy.Length / 2);
    }

    [Fact]
    public void FlattenSelection_MultiSelection_SilentlySkipsNonCurvedShapes()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        var rect = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 };
        model.Shapes.Add(circle);
        model.Shapes.Add(rect);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0, tolDbu: 20_000);
        Click(vm, 105_000, 5000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        vm.FlattenSelectionToPolygon(1000);

        Assert.Equal(2, model.Shapes.Count);
        Assert.IsType<PolygonShape>(model.Shapes[0]); // the circle flattened
        Assert.Same(rect, model.Shapes[1]);            // the rect untouched, same instance
    }

    [Fact]
    public void FlattenSelection_CurvedPath_StaysAPathShape_WidthAndEndStyleIntact()
    {
        var model = FreshModel();
        var path = new PathShape
        {
            Layer = Layer1, Xy = [0, 0, 10_000, 0, 20_000, 10_000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 }, new LayoutEdge { Kind = EdgeKind.Line }],
            Width = 2000, End = PathEndStyle.Round,
        };
        model.Shapes.Add(path);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 5000, 2000, tolDbu: 4000);

        vm.FlattenSelectionToPolygon(1000);

        var flattened = Assert.IsType<PathShape>(Assert.Single(model.Shapes));
        Assert.Equal(2000, flattened.Width);
        Assert.Equal(PathEndStyle.Round, flattened.End);
        Assert.True(flattened.Edges is null || flattened.Edges.All(e => e.Kind == EdgeKind.Line));
    }

    [Fact]
    public void CanFlattenSelection_FalseWhenNothingCurvedSelected()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Assert.False(vm.CanFlattenSelection);
    }

    [Fact]
    public void FlattenAllCurves_OnLayer_OnlyTouchesThatLayer()
    {
        var model = FreshModel();
        var layer2 = new LayerKey(2, 0);
        var circleOnLayer1 = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        var circleOnLayer2 = new CircleShape { Layer = layer2, Cx = 100_000, Cy = 0, R = 10_000 };
        model.Shapes.Add(circleOnLayer1);
        model.Shapes.Add(circleOnLayer2);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.FlattenAllCurves(Layer1, null);

        Assert.IsType<PolygonShape>(model.Shapes[0]);
        Assert.Same(circleOnLayer2, model.Shapes[1]);
    }

    // ── L1h gate 6: "Flatten to Polygon" collapsed to ONE always-prompting entry (R-L1h-2) ────────

    [Fact]
    public void FlattenSelectionToPolygon_Null_IsANoOp_TheNoDialogVariantIsGone()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        model.Shapes.Add(circle);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0);

        vm.FlattenSelectionToPolygon(null); // the removed no-dialog call shape — must do nothing

        Assert.Same(circle, model.Shapes[0]); // still a Circle, untouched
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── L1h gate 6b: pre-fill chain (own value vs. technology default) ────────────────────────────

    [Fact]
    public void OwnTolDbu_ReturnsShapesOwnValue_WhenSet_NullWhenInherited()
    {
        var withOwn = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 5000, FlattenTolDbu = 250 };
        var inherited = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 5000 };

        Assert.Equal(250, LayoutFlattener.OwnTolDbu(withOwn));
        Assert.Null(LayoutFlattener.OwnTolDbu(inherited));
    }

    [Fact]
    public void PreviewFlattenVertexCounts_SkipsNonCurvedShapes_TotalsMatchIndividualPreviews()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        var rect = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 110_000, Y2 = 10_000 }; // no curvature
        model.Shapes.Add(circle);
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        var counts = vm.PreviewFlattenVertexCounts(vm.SelectedIndices, 1000);

        var only = Assert.Single(counts); // the Rect is skipped — nothing to flatten
        Assert.Equal(0, only.Index);
        Assert.Equal(vm.PreviewFlattenVertexCount(0, 1000), only.VertexCount);
    }

    [Fact]
    public void FlattenSelectionToPolygon_WithExplicitTolerance_NeverWritesTheToleranceBackOntoAnyShape()
    {
        // R-L1h-2a: the dialog's chosen value is applied via FlattenSelectionToPolygon directly — it
        // must never appear written onto any surviving/replaced shape's own FlattenTolDbu field,
        // because every shape it touches stops being curved (there's nothing left for it to govern).
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 10_000 };
        model.Shapes.Add(circle);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0);

        vm.FlattenSelectionToPolygon(1234);

        var flattened = Assert.IsType<PolygonShape>(model.Shapes[0]);
        Assert.NotSame(circle, flattened); // the Circle is gone
        // PolygonShape has no FlattenTolDbu field at all — there is structurally nowhere for the
        // dialog's value to have been "written back" to.
    }
}
