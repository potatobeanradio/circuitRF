using System;
using System.Collections.Generic;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Tests;

// ── Phase L1d gates 4-12: full-VM-gesture tests over LayoutEditorViewModel's handle drag state
// machine (docs/sonnet-briefs/brief-L1d-shape-editing-handles.md "Gate (acceptance)" list).
// Pure-geometry-builder invariants are pinned separately in LayoutShapeEditingTests.cs; this file
// drives the same edits through OnPointerPressed/Moved/Released — press, move, release — exactly
// as the canvas would, so a wiring bug (wrong handle picked, wrong drag-kind dispatch, a missed
// Execute()) would fail here even if the underlying builder is correct in isolation.

public class LayoutHandleGesturesTests
{
    private sealed class RecordingSink : IMessageSink
    {
        public List<(MessageLevel Level, string Text)> Messages { get; } = [];
        public void Post(MessageLevel level, string text, string? filePath = null) => Messages.Add((level, text));
        public void Clear() => Messages.Clear();
    }

    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model, IMessageSink? sink = null) =>
        new(model, messageSink: sink) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    private static PolygonShape Square(long side = 10_000) => new()
    {
        Layer = new LayerKey(1, 0),
        Xy = [0, 0, side, 0, side, side, 0, side],
    };

    // ── Gate 5: vertex drag snaps the resulting POSITION; move drag still snaps the DELTA ──────

    [Fact]
    public void VertexHandleDrag_SnapsResultingPosition_OtherVerticesUntouched()
    {
        var model = FreshModel(1000);
        var poly = Square();
        model.Shapes.Add(poly);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000); // select via body click
        Assert.Equal([0], vm.SelectedIndices);

        // Press exactly on vertex 0 (0,0), drag to a raw point that snaps to (1000,2000).
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(1230, 1780, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(1230, 1780, KeyModifiers.None);

        var result = (PolygonShape)model.Shapes[0];
        Assert.Equal([1000, 2000, 10_000, 0, 10_000, 10_000, 0, 10_000], result.Xy);
    }

    [Fact]
    public void WholeShapeMoveDrag_StillSnapsTheDelta_RegressionAgainstL1c()
    {
        var model = FreshModel(1000);
        long[] original = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000];
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = (long[])original.Clone() };
        model.Shapes.Add(poly);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000); // select — a body click, not on any handle

        vm.OnPointerPressed(5000, 5000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(5000 + 2345, 5000 + 2987, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(5000 + 2345, 5000 + 2987, KeyModifiers.None);

        // Snapped delta (2000, 3000) applied uniformly to every vertex.
        long[] expected = [2000, 3000, 12_000, 3000, 12_000, 13_000, 2000, 13_000];
        Assert.Equal(expected, poly.Xy);
    }

    // ── Gate 6: edge drag preserves direction (45 degrees stays 45 degrees) ─────────────────────

    [Fact]
    public void EdgeHandleDrag_45DegreeEdge_DirectionPreservedAfterDrag()
    {
        var model = FreshModel(1); // SnapDbu=1: snapping is effectively a no-op at these integer scales
        var tri = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 1000, 2000, 0] };
        model.Shapes.Add(tri);
        var vm = SelectVm(model);

        Click(vm, 1000, 300); // inside the triangle body -> selects it

        // Edge 0 runs (0,0) -> (1000,1000): its midpoint handle sits at (500,500).
        vm.OnPointerPressed(500, 500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(500 - 1000, 500 + 1000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(500 - 1000, 500 + 1000, KeyModifiers.None);

        var result = (PolygonShape)model.Shapes[0];
        long dx = result.Xy[2] - result.Xy[0], dy = result.Xy[3] - result.Xy[1];
        Assert.Equal(1000, dx);
        Assert.Equal(1000, dy); // still exactly 45 degrees
        Assert.Equal(2000, result.Xy[4]); Assert.Equal(0, result.Xy[5]); // uninvolved vertex untouched
        Assert.NotEqual(0L, result.Xy[0]); // the edge actually moved (not a no-op)
    }

    // ── Gate 7: insert/remove vertex ─────────────────────────────────────────────────────────────

    [Fact]
    public void CtrlClickOnEdgeLine_InsertsOneVertex_AsOneUndoEntry()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(Square());
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Assert.False(vm.UndoRedo.CanUndo);

        // Point on edge 0's line (0,0)->(10000,0) but well clear of any handle (vertex or midpoint).
        Click(vm, 2000, 10, KeyModifiers.Control, tolDbu: 40);

        Assert.True(vm.UndoRedo.CanUndo);
        var result = (PolygonShape)model.Shapes[0];
        Assert.Equal(5, result.Xy.Length / 2);

        vm.UndoRedo.Undo();
        Assert.Equal(4, ((PolygonShape)model.Shapes[0]).Xy.Length / 2);
    }

    [Fact]
    public void CtrlClickExactlyOnTheEdgeMidpointHandle_StillInsertsAVertex_NotAnEdgeDrag()
    {
        // Regression: every straight edge already carries an EdgeMidpoint drag-handle sitting exactly
        // at the edge's midpoint -- the most natural spot to "click the edge". Ctrl+click there must
        // still insert a vertex, not silently begin the (handle-priority) edge-perpendicular drag.
        var model = FreshModel(1000);
        model.Shapes.Add(Square(10_000));
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Assert.False(vm.UndoRedo.CanUndo);

        // Edge 0 runs (0,0) -> (10000,0); its EdgeMidpoint handle sits at exactly (5000,0).
        vm.OnPointerPressed(5000, 0, KeyModifiers.Control, 1, 40);
        vm.OnPointerReleased(5000, 0, KeyModifiers.Control);

        Assert.True(vm.UndoRedo.CanUndo);
        var result = (PolygonShape)model.Shapes[0];
        Assert.Equal(5, result.Xy.Length / 2); // one vertex inserted
        Assert.Equal(5000, result.Xy[2]); Assert.Equal(0, result.Xy[3]); // at the clicked (snapped) point
    }

    [Fact]
    public void RemoveVertex_ClosedShape_BlockedAtThreeVertices_NoOp()
    {
        var model = FreshModel(1000);
        var tri = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0, 500, 1000] };
        model.Shapes.Add(tri);
        var vm = SelectVm(model);

        Click(vm, 500, 300); // select via body click

        // Click precisely on vertex 0 (no drag) -> picks the vertex without mutating the model.
        Click(vm, 0, 0, tolDbu: 40);
        Assert.False(vm.UndoRedo.CanUndo);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);

        Assert.False(vm.UndoRedo.CanUndo); // RemoveVertex returned null -> DeleteVertex no-op
        Assert.Equal(3, ((PolygonShape)model.Shapes[0]).Xy.Length / 2);
    }

    [Fact]
    public void ClickVertexThenDelete_RemovesThatVertex_AsOneUndoEntry()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(Square(1000));
        var vm = SelectVm(model);

        Click(vm, 500, 500); // select via body click

        // Click precisely on vertex 1 (1000,0) -- press+release with no movement, just picks it.
        Click(vm, 1000, 0, tolDbu: 40);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);

        var result = (PolygonShape)model.Shapes[0];
        Assert.Equal([0, 0, 1000, 1000, 0, 1000], result.Xy);

        vm.UndoRedo.Undo();
        Assert.Equal([0, 0, 1000, 0, 1000, 1000, 0, 1000], ((PolygonShape)model.Shapes[0]).Xy);
    }

    [Fact]
    public void ClickRectCornerThenDelete_FallsThroughToDeletingTheWholeShape()
    {
        // Regression: a Rect corner IS reported as a Vertex-kind handle (it maps to the RectCorner
        // resize gesture, not a removable vertex). Before the fix, clicking it set _pickedVertexIndex
        // anyway, and the next Delete keypress called DeleteVertex -- which correctly refuses (Rect
        // is not a vertex-list shape) but the caller returned unconditionally, so Delete did NOTHING
        // instead of deleting the shape.
        var model = FreshModel(1000);
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = SelectVm(model);

        Click(vm, 500, 500); // select via body click

        // Click precisely on the (0,0) corner handle -- no drag, just picks/inspects it.
        Click(vm, 0, 0, tolDbu: 40);

        vm.OnKeyDown(Key.Delete, KeyModifiers.None);

        Assert.Empty(model.Shapes); // the whole Rect was deleted, not silently ignored
    }

    [Fact]
    public void PlainClickOnEdgeLine_AwayFromTheMidpointHandle_StillBeginsTheEdgeDrag()
    {
        // The edge-midpoint HANDLE only covers a small tolerance window at the exact midpoint; a plain
        // click anywhere else along a long straight edge must still hit LayoutShapeEditing.FindEdgeLineHit's
        // fallback and begin the same perpendicular edge-drag.
        var model = FreshModel(1000);
        model.Shapes.Add(Square(10_000));
        var vm = SelectVm(model);

        Click(vm, 5000, 5000); // select via body click

        // Edge 0 runs (0,0)->(10000,0); click a quarter of the way along it, well clear of the
        // midpoint handle at (5000,0).
        vm.OnPointerPressed(2500, 10, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(2500, 10 - 1000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(2500, 10 - 1000, KeyModifiers.None);

        var result = (PolygonShape)model.Shapes[0];
        // Both endpoints of edge 0 moved by the same perpendicular (Y) delta; the other two vertices
        // (which belong to different edges) are untouched -- proof this was an edge drag, not a
        // whole-shape move.
        Assert.Equal(0, result.Xy[0]); Assert.Equal(-1000, result.Xy[1]);
        Assert.Equal(10_000, result.Xy[2]); Assert.Equal(-1000, result.Xy[3]);
        Assert.Equal(10_000, result.Xy[4]); Assert.Equal(10_000, result.Xy[5]);
        Assert.Equal(0, result.Xy[6]); Assert.Equal(10_000, result.Xy[7]);
    }

    // ── Gate 8: bulge drag (flip sign past the chord; bulge=0 renders straight) ────────────────

    [Fact]
    public void BulgeHandleDrag_DraggingPastTheChord_FlipsSign()
    {
        var model = FreshModel(1); // no snapping noise
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 2000, 0, 2000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.3 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        model.Shapes.Add(curve);
        var vm = SelectVm(model);

        Click(vm, 1000, -50); // select via a body click near the (roughly) closed outline

        var originalArc = LayoutArc.FromBulge(0, 0, 2000, 0, 0.3);
        double midAngle = originalArc.StartAngle + originalArc.Sweep / 2;
        long handleX = (long)Math.Round(originalArc.Cx + originalArc.R * Math.Cos(midAngle));
        long handleY = (long)Math.Round(originalArc.Cy + originalArc.R * Math.Sin(midAngle));

        vm.OnPointerPressed(handleX, handleY, KeyModifiers.None, 1, 40);
        // Drag to the point mirrored across the chord (the x-axis, since the chord runs y=0).
        vm.OnPointerMoved(handleX, -handleY, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(handleX, -handleY, KeyModifiers.None);

        var result = (CurveShape)model.Shapes[0];
        Assert.NotNull(result.Edges);
        Assert.Equal(EdgeKind.Arc, result.Edges[0].Kind);
        Assert.True(Math.Sign(result.Edges[0].Bulge) != Math.Sign(0.3));
    }

    [Fact]
    public void BulgeHandleDrag_OntoTheChordMidpoint_YieldsZeroBulge_RendersStraight()
    {
        var model = FreshModel(1);
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 2000, 0, 2000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.3 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        model.Shapes.Add(curve);
        var vm = SelectVm(model);

        Click(vm, 1000, -50);

        var originalArc = LayoutArc.FromBulge(0, 0, 2000, 0, 0.3);
        double midAngle = originalArc.StartAngle + originalArc.Sweep / 2;
        long handleX = (long)Math.Round(originalArc.Cx + originalArc.R * Math.Cos(midAngle));
        long handleY = (long)Math.Round(originalArc.Cy + originalArc.R * Math.Sin(midAngle));

        vm.OnPointerPressed(handleX, handleY, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(1000, 0, true, KeyModifiers.None, 40); // the chord midpoint itself
        vm.OnPointerReleased(1000, 0, KeyModifiers.None);

        var result = (CurveShape)model.Shapes[0];
        Assert.NotNull(result.Edges);
        Assert.Equal(0, result.Edges[0].Bulge, 6);
        var straightArc = LayoutArc.FromBulge(0, 0, 2000, 0, result.Edges[0].Bulge);
        Assert.Equal(0, straightArc.Sweep, 6);
    }

    // ── Gate 9: the promotion rule (R-L1d-3) ────────────────────────────────────────────────────

    [Fact]
    public void ConvertEdge_PolygonToArc_PromotesToCurveAtSameIndex_UndoRestoresOriginalPolygonInstance()
    {
        var model = FreshModel(1000);
        var original = Square();
        model.Shapes.Add(original);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        var hit = vm.FindEdgeForContextMenu(5000, 0, tolDbu: 40); // edge 0's midpoint, (5000,0)
        Assert.NotNull(hit);
        Assert.Equal(EdgeKind.Line, hit!.Value.CurrentKind);

        vm.ConvertEdge(hit.Value.ShapeIndex, hit.Value.EdgeIndex, EdgeKind.Arc);

        var promoted = Assert.IsType<CurveShape>(model.Shapes[0]);
        Assert.Equal(EdgeKind.Arc, promoted.Edges![0].Kind);
        Assert.Equal(original.Xy, promoted.Xy);

        vm.UndoRedo.Undo();
        Assert.Same(original, model.Shapes[0]); // exact original instance restored, not an equivalent copy
    }

    [Fact]
    public void ConvertEdge_Path_GainsCurvedEdge_SameInstanceType_OneUndoEntry()
    {
        var model = FreshModel(1000);
        var path = new PathShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0, 1000, 1000], Width = 200 };
        model.Shapes.Add(path);
        var vm = SelectVm(model);

        Click(vm, 500, 0);
        var hit = vm.FindEdgeForContextMenu(500, 0, tolDbu: 40);
        Assert.NotNull(hit);

        vm.ConvertEdge(hit!.Value.ShapeIndex, hit.Value.EdgeIndex, EdgeKind.Cubic);

        Assert.IsType<PathShape>(model.Shapes[0]); // no type change for a shape that already has an edge list
        Assert.True(vm.UndoRedo.CanUndo);
        vm.UndoRedo.Undo();
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── Gate 10: one gesture -> one undo entry; redo reproduces exactly ─────────────────────────

    [Fact]
    public void VertexDrag_ManyIntermediateMoves_CollapsesToOneUndoEntry_RedoByteIdentical()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(Square());
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Assert.False(vm.UndoRedo.CanUndo);
        string beforeJson = LayoutPersistence.Serialize(model);

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40);
        for (int i = 1; i <= 50; i++)
            vm.OnPointerMoved(i * 20, i * 15, true, KeyModifiers.None, 40);
        vm.OnPointerMoved(1230, 1780, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(1230, 1780, KeyModifiers.None);

        Assert.True(vm.UndoRedo.CanUndo);
        string afterDragJson = LayoutPersistence.Serialize(model);
        Assert.NotEqual(beforeJson, afterDragJson);

        vm.UndoRedo.Undo();
        // Exactly ONE entry existed: a single Undo fully restores the pre-drag state.
        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal(beforeJson, LayoutPersistence.Serialize(model));

        Assert.True(vm.UndoRedo.CanRedo);
        vm.UndoRedo.Redo();
        Assert.Equal(afterDragJson, LayoutPersistence.Serialize(model));
    }

    // ── Gate 11: Escape mid-drag restores the original shape, pushes no command ─────────────────

    [Fact]
    public void EscapeMidVertexDrag_RestoresOriginalShape_PushesNoCommand()
    {
        var model = FreshModel(1000);
        var original = Square();
        model.Shapes.Add(original);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(1230, 1780, true, KeyModifiers.None, 40);

        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        Assert.False(vm.UndoRedo.CanUndo); // no command was ever pushed
        Assert.Same(original, model.Shapes[0]); // the model was never mutated mid-drag
        Assert.Equal([0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000], ((PolygonShape)model.Shapes[0]).Xy);

        // The drag is truly over -- a subsequent release does nothing further.
        vm.OnPointerReleased(1230, 1780, KeyModifiers.None);
        Assert.False(vm.UndoRedo.CanUndo);
    }

    // ── Gate 12: self-intersection is flagged, never blocked; the edit is kept ─────────────────

    [Fact]
    public void VertexDragThatCreatesSelfIntersection_PostsWarning_ButKeepsTheEdit()
    {
        var model = FreshModel(1000);
        var poly = Square();
        model.Shapes.Add(poly);
        var sink = new RecordingSink();
        var vm = SelectVm(model, sink);

        Click(vm, 5000, 5000);

        // Drag vertex 0 (0,0) across to (20000,0) -- past vertex 1 (10000,0) -- so the closing edge
        // (vertex3 -> vertex0) crosses the (now-reordered) edge1, a genuine non-adjacent self-cross.
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(20_000, 0, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(20_000, 0, KeyModifiers.None);

        var result = (PolygonShape)model.Shapes[0];
        Assert.Equal(20_000, result.Xy[0]); // the edit was applied and kept, not reverted
        Assert.Equal(0, result.Xy[1]);
        Assert.Contains(sink.Messages, m => m.Level == MessageLevel.Warning && m.Text.Contains("self-intersect"));
    }

    // ── Gate 4: grab-radius handle hit-testing works identically on both starter technologies,
    // via a tolDbu computed per-query from the CURRENT zoom (never cached, never derived from SnapDbu) ──

    [Theory]
    [InlineData(25_400L)]  // PCB starter tech: DefaultSnapDbu = 1 mil
    [InlineData(5L)]       // MMIC starter tech: DefaultSnapDbu = 5 nm
    public void HandleHitTolerance_DerivedFromZoom_WorksAcrossVeryDifferentSnapScales(long snapDbu)
    {
        var vp = LayoutViewport.Default(width: 800, height: 600, snapDbu: snapDbu, dbuPerMicron: 1000);
        const double GrabRadiusPixels = 8.0;
        long tolDbu = (long)Math.Round(GrabRadiusPixels / vp.Zoom);
        Assert.True(tolDbu > 0);

        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = snapDbu };
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, snapDbu * 50, 0, snapDbu * 50, snapDbu * 50, 0, snapDbu * 50] };
        model.Shapes.Add(poly);
        var vm = SelectVm(model);
        Click(vm, snapDbu * 25, snapDbu * 25, tolDbu: tolDbu);

        // A press a couple of DBU off the true vertex still hits the VERTEX HANDLE at this zoom's
        // tolerance -- only vertex 0 moves, the far corner (vertex 2) is untouched.
        vm.OnPointerPressed(2, 2, KeyModifiers.None, 1, tolDbu);
        vm.OnPointerMoved(2 + snapDbu * 3, 2, true, KeyModifiers.None, tolDbu);
        vm.OnPointerReleased(2 + snapDbu * 3, 2, KeyModifiers.None);
        Assert.True(vm.UndoRedo.CanUndo);
        var afterVertexEdit = (PolygonShape)model.Shapes[0];
        Assert.Equal(snapDbu * 50, afterVertexEdit.Xy[4]); Assert.Equal(snapDbu * 50, afterVertexEdit.Xy[5]);

        // ...but with zero tolerance, the same slightly-off press must NOT hit the handle -- it falls
        // through to the shape's ordinary body/move-drag instead, which translates EVERY vertex
        // (including the far corner) by the same delta.
        vm.UndoRedo.Undo();
        var vm2 = SelectVm(model);
        Click(vm2, snapDbu * 25, snapDbu * 25, tolDbu: 0);
        vm2.OnPointerPressed(2, 2, KeyModifiers.None, 1, 0);
        vm2.OnPointerMoved(2 + snapDbu * 3, 2, true, KeyModifiers.None, 0);
        vm2.OnPointerReleased(2 + snapDbu * 3, 2, KeyModifiers.None);
        var afterFallback = (PolygonShape)model.Shapes[0];
        Assert.NotEqual(snapDbu * 50, afterFallback.Xy[4]); // the far corner moved too -> whole-shape move, not a vertex edit
    }

    // ── Rect/RoundedRect edge-drag (owner follow-up: "make Rect/RRect support drag edge/edge line") ──

    [Fact]
    public void Rect_EdgeMidpointHandleDrag_MovesOnlyThatOneSide()
    {
        var model = FreshModel(1000);
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        model.Shapes.Add(rect);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000); // select via body click

        // Bottom edge's midpoint handle sits at (5000, 0). Drag it down by 2000.
        vm.OnPointerPressed(5000, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(5000, -2000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(5000, -2000, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        Assert.Equal(-2000, result.Y1); // only the bottom moved
        Assert.Equal(0, result.X1); Assert.Equal(10_000, result.X2); Assert.Equal(10_000, result.Y2); // untouched
    }

    [Fact]
    public void Rect_PlainClickOnEdgeLine_AwayFromMidpoint_StillBeginsTheEdgeDrag()
    {
        var model = FreshModel(1000);
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        model.Shapes.Add(rect);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);

        // A quarter of the way along the right edge (X2=10000), well clear of its midpoint (10000,5000).
        vm.OnPointerPressed(10_000, 2500, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(11_000, 2500, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(11_000, 2500, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        Assert.Equal(11_000, result.X2); // only the right side moved
        Assert.Equal(0, result.X1); Assert.Equal(0, result.Y1); Assert.Equal(10_000, result.Y2);
    }

    [Fact]
    public void Rect_EdgeDrag_PastTheOppositeEdge_NormalizesAtCommit()
    {
        var model = FreshModel(1000);
        var rect = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 };
        model.Shapes.Add(rect);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);

        // Drag the top edge (Y2=10000) all the way down past the bottom edge (Y1=0).
        vm.OnPointerPressed(5000, 10_000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(5000, -5000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(5000, -5000, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        Assert.True(result.Y1 < result.Y2); // normalized, not left inside-out
    }

    [Fact]
    public void RoundedRect_EdgeMidpointHandleDrag_MovesOnlyThatOneSide()
    {
        var model = FreshModel(1000);
        var rr = new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 };
        model.Shapes.Add(rr);
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);

        // Left edge's midpoint sits at (0, 5000).
        vm.OnPointerPressed(0, 5000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(-2000, 5000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(-2000, 5000, KeyModifiers.None);

        var result = (RoundedRectShape)model.Shapes[0];
        Assert.Equal(-2000, result.X1);
        Assert.Equal(10_000, result.X2); Assert.Equal(0, result.Y1); Assert.Equal(10_000, result.Y2);
        Assert.Equal(1000, result.CornerRadius); // untouched by an edge drag
    }

    // ── Corner radius: a grip per corner, and the right-hand pair drags the other way ──────────
    //
    // Owner request, 2026-09-04. The radius is one shape-wide field, so all four grips write the same
    // value — what differs is which direction GROWS it. A grip is at R from its own corner, so on the
    // left pair R = px - X1 and on the right pair R = X2 - px; getting that wrong makes a rightward
    // drag on the right-hand grip shrink the radius while the grip flees the cursor at 2x.

    [Fact]
    public void CornerRadiusGrip_OnTheLeft_GrowsWithARightwardDrag()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 });
        var vm = SelectVm(model);
        Click(vm, 5000, 5000);

        // Bottom-left grip (corner 0) sits at (X1 + R, Y1) = (1000, 0).
        vm.OnPointerPressed(1000, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(3000, 0, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(3000, 0, KeyModifiers.None);

        Assert.Equal(3000, ((RoundedRectShape)model.Shapes[0]).CornerRadius);
    }

    [Fact]
    public void CornerRadiusGrip_OnTheRight_GrowsWithALeftwardDrag()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 });
        var vm = SelectVm(model);
        Click(vm, 5000, 5000);

        // Bottom-right grip (corner 1) sits at (X2 - R, Y1) = (9000, 0). Dragging it LEFT to 7000
        // means "the rounding now reaches 3000 in from this corner" — the mirror of the case above,
        // and the grip lands exactly under the cursor rather than running from it.
        vm.OnPointerPressed(9000, 0, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(7000, 0, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(7000, 0, KeyModifiers.None);

        Assert.Equal(3000, ((RoundedRectShape)model.Shapes[0]).CornerRadius);
    }

    [Fact]
    public void CornerRadiusGrip_OnTheTopRight_AlsoMirrors()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000, CornerRadius = 1000 });
        var vm = SelectVm(model);
        Click(vm, 5000, 5000);

        // Top-right grip (corner 2) at (X2 - R, Y2) = (9000, 10000).
        vm.OnPointerPressed(9000, 10_000, KeyModifiers.None, 1, 40);
        vm.OnPointerMoved(6000, 10_000, true, KeyModifiers.None, 40);
        vm.OnPointerReleased(6000, 10_000, KeyModifiers.None);

        Assert.Equal(4000, ((RoundedRectShape)model.Shapes[0]).CornerRadius);
    }

    // ── "Delete Vertex" context menu (owner follow-up: an explicit alternative to the invisible
    // click-to-pick-then-press-Delete gesture) ─────────────────────────────────────────────────────

    [Fact]
    public void FindVertexForContextMenu_FindsTheNearestVertexOnTheSingleSelection()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(Square(10_000));
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);

        var hit = vm.FindVertexForContextMenu(9990, 20, tolDbu: 40); // near vertex 1 (10000,0)
        Assert.NotNull(hit);
        Assert.Equal(1, hit!.Value.VertexIndex);
    }

    [Fact]
    public void FindVertexForContextMenu_RectCorner_ReturnsNull_NotARemovableVertex()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var vm = SelectVm(model);

        Click(vm, 500, 500);

        Assert.Null(vm.FindVertexForContextMenu(0, 0, tolDbu: 40));
    }

    [Fact]
    public void DeleteVertex_ViaContextMenuLookup_RemovesTheFoundVertex_AsOneUndoEntry()
    {
        var model = FreshModel(1000);
        model.Shapes.Add(Square(1000));
        var vm = SelectVm(model);

        Click(vm, 500, 500);

        var hit = vm.FindVertexForContextMenu(1000, 0, tolDbu: 40); // vertex 1 (1000,0)
        Assert.NotNull(hit);

        vm.DeleteVertex(hit!.Value.ShapeIndex, hit.Value.VertexIndex);

        var result = (PolygonShape)model.Shapes[0];
        Assert.Equal([0, 0, 1000, 1000, 0, 1000], result.Xy);
        Assert.True(vm.UndoRedo.CanUndo);
    }

    [Fact]
    public void DeleteVertex_ViaContextMenuLookup_BlockedAtThreeVertices_NoOp()
    {
        var model = FreshModel(1000);
        var tri = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0, 500, 1000] };
        model.Shapes.Add(tri);
        var vm = SelectVm(model);

        Click(vm, 500, 300);

        var hit = vm.FindVertexForContextMenu(0, 0, tolDbu: 40);
        Assert.NotNull(hit);

        vm.DeleteVertex(hit!.Value.ShapeIndex, hit.Value.VertexIndex);

        Assert.False(vm.UndoRedo.CanUndo);
        Assert.Equal(3, ((PolygonShape)model.Shapes[0]).Xy.Length / 2);
    }
}
