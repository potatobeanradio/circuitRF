using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests;

// ── Phase L1h gates 7/10/11/12/13/14: docs/sonnet-briefs/brief-L1h-scale-and-context-menu.md §2
// VM-level: the numeric "Scale…" path (LayoutEditorViewModel.ApplyScale) — factor/anchor math, the
// shared coordinate walk actually being shared across all three callers, non-uniform arc promotion,
// rounding (never snapping), the positive-factor/no-collapse guards, and one undo entry per scale.
// Mouse-driven bbox-handle gates (8/9) are in LayoutScaleGesturesTests.cs.

public class LayoutScaleTests
{
    private sealed class FakeMessageSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Posted { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Posted.Add((level, text));
        public void Clear() => Posted.Clear();
    }

    private static readonly LayerKey Layer1 = new(1, 0);

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // ── Gate 7: numeric factor + the 9 anchors position the result correctly ─────────────────────

    [Fact]
    public void ApplyScale_FactorOfTwo_On1mmSquare_YieldsExactly2mm()
    {
        var model = FreshModel();
        long oneMm = LayoutUnits.ToDbu(1, LayoutUnit.Mm, model.DbuPerMicron);
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = oneMm, Y2 = oneMm };
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, oneMm / 2, oneMm / 2);

        vm.ApplyScale(2.0, 2.0, anchorX: 0, anchorY: 0);

        var scaled = Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.Equal(2 * oneMm, scaled.X2 - scaled.X1);
        Assert.Equal(2 * oneMm, scaled.Y2 - scaled.Y1);
    }

    [Theory]
    [InlineData(0, 0, 2000, 2000, 4000, 4000)]     // anchor = layout origin: bbox min moves too
    [InlineData(1500, 1500, 500, 500, 2500, 2500)] // anchor = selection center
    [InlineData(1000, 1000, 1000, 1000, 3000, 3000)] // anchor = bbox's own bottom-left corner: fixed point
    public void ApplyScale_EachAnchor_PositionsTheResultCorrectly(
        long anchorX, long anchorY, long expX1, long expY1, long expX2, long expY2)
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = Layer1, X1 = 1000, Y1 = 1000, X2 = 2000, Y2 = 2000 };
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 1500, 1500);

        vm.ApplyScale(2.0, 2.0, anchorX, anchorY);

        var scaled = Assert.IsType<RectShape>(model.Shapes[0]);
        Assert.Equal(expX1, scaled.X1);
        Assert.Equal(expY1, scaled.Y1);
        Assert.Equal(expX2, scaled.X2);
        Assert.Equal(expY2, scaled.Y2);
    }

    // ── Gate 10: everything scales — hole, cubic, path width, via, label; bulge unchanged (uniform) ─

    [Fact]
    public void ApplyScale_Uniform_ScalesEveryFieldInTheFullSet_BulgeUnchanged()
    {
        var model = FreshModel();
        var polyWithHole = new PolygonShape
        {
            Layer = Layer1,
            Xy = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[30_000, 30_000, 30_000, 70_000, 70_000, 70_000, 70_000, 30_000]],
        };
        var curveWithCubic = new CurveShape
        {
            Layer = Layer1,
            Xy = [200_000, 0, 240_000, 0, 240_000, 40_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 210_000, C1Y = 5_000, C2X = 230_000, C2Y = 5_000 },
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.5 },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
            FlattenTolDbu = 500,
        };
        var path = new PathShape { Layer = Layer1, Xy = [300_000, 0, 340_000, 0], Width = 2_000 };
        var via = new ViaShape { Layer = Layer1, X = 400_000, Y = 0, PadSize = 3_000, DrillSize = 1_000 };
        var label = new LabelShape { Layer = Layer1, X = 500_000, Y = 0, Text = "M1", Height = 4_000 };

        model.Shapes.Add(polyWithHole);
        model.Shapes.Add(curveWithCubic);
        model.Shapes.Add(path);
        model.Shapes.Add(via);
        model.Shapes.Add(label);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);

        vm.ApplyScale(2.0, 2.0, anchorX: 0, anchorY: 0);

        var poly = Assert.IsType<PolygonShape>(model.Shapes[0]);
        Assert.Equal([0, 0, 200_000, 0, 200_000, 200_000, 0, 200_000], poly.Xy);
        Assert.Equal([60_000, 60_000, 60_000, 140_000, 140_000, 140_000, 140_000, 60_000], poly.Holes![0]);

        var curve = Assert.IsType<CurveShape>(model.Shapes[1]);
        Assert.Equal(400_000, curve.Xy[0]); // vertex 0 X: 200_000 * 2
        Assert.Equal(420_000, curve.Edges![0].C1X); // cubic control point scales too
        Assert.Equal(0.5, curve.Edges[1].Bulge); // bulge NEVER scales, uniform or not
        Assert.Equal(1000, curve.FlattenTolDbu); // a length -> scales

        var scaledPath = Assert.IsType<PathShape>(model.Shapes[2]);
        Assert.Equal(4_000, scaledPath.Width);

        var scaledVia = Assert.IsType<ViaShape>(model.Shapes[3]);
        Assert.Equal(6_000, scaledVia.PadSize);
        Assert.Equal(2_000, scaledVia.DrillSize);

        var scaledLabel = Assert.IsType<LabelShape>(model.Shapes[4]);
        Assert.Equal(8_000, scaledLabel.Height);
    }

    // ── Gate 11: the shared traversal is actually shared across TryChangeResolution / Rescale / Scale ─

    [Fact]
    public void SharedTraversal_ScaleAtOriginByTwo_MatchesTryChangeResolutionRefineByTwo()
    {
        // Both a uniform Scale anchored at the layout origin and a DBU-resolution refinement by the
        // same ratio apply the identical v -> v*2 transform to every coordinate — if LayoutScaling
        // and LayoutEditorViewModel.ApplyScale didn't route through the same LayoutCoordinateWalk,
        // this is exactly the kind of field-list drift that would show up here first.
        var fixture = new PolygonShape
        {
            Layer = Layer1,
            Xy = [10_000, 10_000, 40_000, 10_000, 40_000, 40_000, 10_000, 40_000],
            Holes = [[20_000, 20_000, 20_000, 30_000, 30_000, 30_000, 30_000, 20_000]],
        };

        var viaResolution = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        viaResolution.Shapes.Add(LayoutGeometry.Clone(fixture));
        Assert.True(LayoutScaling.TryChangeResolution(viaResolution, 2000, out _)); // refine by 2x

        var viaScaleModel = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        viaScaleModel.Shapes.Add(LayoutGeometry.Clone(fixture));
        var vm = new LayoutEditorViewModel(viaScaleModel) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);
        vm.ApplyScale(2.0, 2.0, anchorX: 0, anchorY: 0);

        var viaResPoly = (PolygonShape)viaResolution.Shapes[0];
        var viaScalePoly = (PolygonShape)viaScaleModel.Shapes[0];
        Assert.Equal(viaResPoly.Xy, viaScalePoly.Xy);
        Assert.Equal(viaResPoly.Holes![0], viaScalePoly.Holes![0]);
    }

    [Fact]
    public void SharedTraversal_PasteRescaleAtOriginByTwo_MatchesScaleAtOriginByTwo()
    {
        var fixture = new CurveShape
        {
            Layer = Layer1,
            Xy = [10_000, 10_000, 40_000, 10_000, 40_000, 40_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Cubic, C1X = 20_000, C1Y = 5_000, C2X = 30_000, C2Y = 5_000 },
                new LayoutEdge { Kind = EdgeKind.Line },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        };

        var payload = LayoutFragment.Build([fixture], null, dbuPerMicron: 1000);
        var rescale = LayoutFragment.Rescale(payload, destDbuPerMicron: 2000); // same ratio as a uniform 2x scale
        var rescaledCurve = (CurveShape)rescale.Shapes[0];

        var scaleModel = new LayoutView { DbuPerMicron = 1000, SnapDbu = 1000 };
        scaleModel.Shapes.Add(LayoutGeometry.Clone(fixture));
        var vm = new LayoutEditorViewModel(scaleModel) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        vm.SelectAllCommand.Execute(null);
        vm.ApplyScale(2.0, 2.0, anchorX: 0, anchorY: 0);
        var scaledCurve = (CurveShape)scaleModel.Shapes[0];

        Assert.Equal(rescaledCurve.Xy, scaledCurve.Xy);
        Assert.Equal(rescaledCurve.Edges![0].C1X, scaledCurve.Edges![0].C1X);
        Assert.Equal(rescaledCurve.Edges[0].C1Y, scaledCurve.Edges[0].C1Y);
    }

    // ── Gate 12: non-uniform scale converts arcs; uniform does not ────────────────────────────────

    [Fact]
    public void ApplyScale_NonUniform_CircleBecomesCurveWithCubics_MatchesEllipseWithinTolerance_PostsMessage()
    {
        var model = FreshModel();
        long cx = 50_000, cy = 50_000, r = 20_000;
        var circle = new CircleShape { Layer = Layer1, Cx = cx, Cy = cy, R = r };
        model.Shapes.Add(circle);
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, cx, cy);

        vm.ApplyScale(2.0, 1.0, anchorX: cx, anchorY: cy);

        var curve = Assert.IsType<CurveShape>(model.Shapes[0]);
        Assert.Equal(4, curve.Edges!.Count);
        Assert.All(curve.Edges, e => Assert.Equal(EdgeKind.Cubic, e.Kind));

        // The FLATTENED outline (not just the 4 exact quadrant vertices — points ALONG each cubic
        // segment are where the 4-Bézier approximation actually has error) must lie on the analytic
        // ellipse ((x-cx)/2r)^2 + ((y-cy)/r)^2 == 1 (semi-axes 2r and r) within a small tolerance.
        var flattened = LayoutFlattener.Flatten(curve, tolDbu: 100)[0];
        int n = flattened.Length / 2;
        Assert.True(n > 4); // confirms the cubic segments were actually subdivided, not just the 4 vertices
        for (int i = 0; i < n; i++)
        {
            double x = flattened[2 * i], y = flattened[2 * i + 1];
            double u = (x - cx) / (2.0 * r), v = (y - cy) / r;
            double onEllipse = u * u + v * v;
            Assert.InRange(onEllipse, 0.95, 1.05);
        }

        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Warning && m.Text.Contains("cubic"));
    }

    [Fact]
    public void ApplyScale_Uniform_CircleStaysACircle_NoPromotionMessage()
    {
        var model = FreshModel();
        var circle = new CircleShape { Layer = Layer1, Cx = 0, Cy = 0, R = 20_000 };
        model.Shapes.Add(circle);
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 0, 0);

        vm.ApplyScale(2.0, 2.0, anchorX: 0, anchorY: 0);

        var scaled = Assert.IsType<CircleShape>(model.Shapes[0]);
        Assert.Equal(40_000, scaled.R);
        Assert.DoesNotContain(sink.Posted, m => m.Text.Contains("cubic"));
    }

    // ── Gate 13: rounding never snaps to the grid; guards reject bad factors/collapse ────────────

    [Fact]
    public void ApplyScale_ArbitraryFactor_RoundsToNearestDbu_NeverSnapsToTheSnapGrid()
    {
        var model = FreshModel();
        model.SnapDbu = 1000; // a coarse snap grid the scaled result must NOT be forced onto
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 777, Y2 = 777 };
        model.Shapes.Add(rect);
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 400, 400);

        vm.ApplyScale(1.37, 1.37, anchorX: 0, anchorY: 0);

        var scaled = Assert.IsType<RectShape>(model.Shapes[0]);
        long expected = (long)System.Math.Round(777 * 1.37, System.MidpointRounding.AwayFromZero);
        Assert.Equal(expected, scaled.X2); // exact rounded value...
        Assert.NotEqual(0, scaled.X2 % 1000); // ...NOT snapped to the 1000-DBU grid
    }

    [Theory]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(1.0, 0.0)]
    [InlineData(1.0, -1.0)]
    public void ApplyScale_NonPositiveFactor_RejectedWithMessage_ModelUnchanged(double fx, double fy)
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        model.Shapes.Add(rect);
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);

        vm.ApplyScale(fx, fy, anchorX: 0, anchorY: 0);

        Assert.Same(rect, model.Shapes[0]); // untouched
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Error && m.Text.Contains("positive"));
    }

    [Fact]
    public void ApplyScale_WouldCollapseBelowOneDbu_RejectedWithMessage_ModelUnchanged()
    {
        var model = FreshModel();
        var rect = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 10, Y2 = 10 };
        model.Shapes.Add(rect);
        var sink = new FakeMessageSink();
        var vm = new LayoutEditorViewModel(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 5, 5);

        vm.ApplyScale(0.01, 0.01, anchorX: 0, anchorY: 0); // 10 DBU * 0.01 = 0.1 -> collapses below 1

        Assert.Same(rect, model.Shapes[0]);
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Contains(sink.Posted, m => m.Level == MessageLevel.Error && m.Text.Contains("collapse"));
    }

    // ── Gate 14: one undo entry restoring originals at their original indices ────────────────────

    [Fact]
    public void ApplyScale_OneUndoEntry_RestoresOriginalsAtOriginalIndices_ByteIdentical()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = Layer1, X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var untouched = new RectShape { Layer = Layer1, X1 = 100_000, Y1 = 0, X2 = 101_000, Y2 = 1000 };
        var b = new CircleShape { Layer = Layer1, Cx = 5000, Cy = 5000, R = 2000 };
        model.Shapes.Add(a); model.Shapes.Add(untouched); model.Shapes.Add(b);

        var jsonBefore = LayoutPersistence.Serialize(model);

        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };
        Click(vm, 500, 500);
        Click(vm, 5000, 5000, KeyModifiers.Shift);
        Assert.Equal(2, vm.SelectedIndices.Count);

        vm.ApplyScale(2.0, 2.0, anchorX: 0, anchorY: 0);

        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();

        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Same(a, model.Shapes[0]);
        Assert.Same(untouched, model.Shapes[1]);
        Assert.Same(b, model.Shapes[2]);
        Assert.Equal(jsonBefore, LayoutPersistence.Serialize(model));
    }
}
