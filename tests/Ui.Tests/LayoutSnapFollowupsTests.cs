using System;
using System.IO;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-geometry-snap-followups.md — three owner reports against
// brief-snap-distance-and-geometry-snap.md, all traced to UpdateSnapMarker (LayoutEditorViewModel.
// Snap.cs): (1) handle drags never snapped (the marker update bailed out for every handle/scale drag,
// R-snpf-1/2/3); (2) the dragged shape attracted itself, and only a single shape could ever be excluded
// (R-snpf-4/5/6); (3) no hover marker at all — the query ran but RebuildOverlay() was never called on
// the plain-hover path, so nothing was ever pushed into Overlay.SnapMarker for the renderer to draw
// (R-snpf-7/8/9). Driven through OnPointerPressed/Moved/Released exactly as LayoutCanvas would, mirroring
// LayoutSnapGestureTests.cs's own methodology.

public sealed class LayoutSnapFollowupsTests : IDisposable
{
    private readonly string _workspaceDir;

    public LayoutSnapFollowupsTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfSnapFollowups_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateUnder(_workspaceDir);
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model, string? currentLayoutPath = null) =>
        new(model, currentLayoutPath) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static void Click(LayoutEditorViewModel vm, double wx, double wy, KeyModifiers mods = default, long tolDbu = 40)
    {
        vm.OnPointerPressed(wx, wy, mods, 1, tolDbu);
        vm.OnPointerReleased(wx, wy, mods);
    }

    // Large enough to keep these tests independent of the exact canvas pixel constant — the point
    // under test is the DISPATCH, not the specific tolerance value (same convention as
    // LayoutSnapGestureTests.cs).
    private const long SnapTol = 3000;

    // ── R-snpf-7/8/9: hover shows a marker — over a primitive, an instance, and nested-cell geometry ──

    [Fact]
    public void HoverMarker_OverPrimitiveCorner_QueryRunsAndMarkerIsDrawn()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 });
        var vm = SelectVm(model);

        int before = vm.SnapQueryRunCount;
        // A plain hover move — no button down, no drag of any kind in progress.
        vm.OnPointerMoved(200, 100, leftDown: false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 0, snapTolDbu: SnapTol);

        Assert.True(vm.SnapQueryRunCount > before, "the query must run on a plain hover move");
        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, vm.Overlay.SnapMarker!.Value.Kind);
    }

    private string CreateCellWithCorner(string name, long localX, long localY)
    {
        var cellDir = CellFolder.CreateCellFolder(_workspaceDir, name);
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = localX, Y1 = localY, X2 = localX + 1000, Y2 = localY + 1000 });
        LayoutPersistence.SaveToFile(Path.Combine(layoutDir, "main.clay"), view);
        return cellDir;
    }

    [Fact]
    public void HoverMarker_OverInstanceCorner_QueryRunsAndMarkerIsDrawn()
    {
        CreateCellWithCorner("Sub", 0, 0);
        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = "Sub", X = 10_000, Y = 20_000, Mag = 1.0 });
        var vm = SelectVm(top, Path.Combine(_workspaceDir, "top.clay"));

        int before = vm.SnapQueryRunCount;
        // Sub-cell's corner (0,0) is placed at world (10000,20000).
        vm.OnPointerMoved(10_050, 20_050, leftDown: false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 0, snapTolDbu: SnapTol);

        Assert.True(vm.SnapQueryRunCount > before);
        Assert.NotNull(vm.Overlay.SnapMarker);
        var marker = vm.Overlay.SnapMarker!.Value;
        Assert.Equal(SnapFeatureKind.CornerEndpoint, marker.Kind);
        Assert.True(marker.OwnerIsInstance);
        Assert.Equal(0, marker.OwnerIndex);
    }

    [Fact]
    public void HoverMarker_OverNestedCellGeometry_TwoLevelsDeep_QueryRunsAndMarkerIsDrawn()
    {
        // Sub2 (the innermost cell) has a corner at local (0,0). Sub1 has ONE instance of Sub2 offset
        // by (5000,5000). Top has ONE instance of Sub1 offset by (10000,20000) — so Sub2's corner
        // reaches world space through TWO nested instance transforms, exercising RecurseInstance's own
        // nested-instance recursion branch specifically (R-snp-13's cell-space transform path), not
        // just a single level of instancing.
        CreateCellWithCorner("Sub2", 0, 0);
        var sub1Dir = CellFolder.CreateCellFolder(_workspaceDir, "Sub1");
        var sub1View = new LayoutView { DbuPerMicron = 1000 };
        // "Sub2" is a SIBLING of "Sub1" directly under _workspaceDir — resolved relative to Sub1's own
        // layout/ subfolder (CellHierarchy.LayoutBaseDirOf), a CellRef needs TWO "../" to reach a true
        // sibling (src/Ui/CLAUDE.md's own standing note on this exact trap).
        sub1View.Instances.Add(new LayoutInstance { CellRef = "../../Sub2", X = 5000, Y = 5000, Mag = 1.0 });
        LayoutPersistence.SaveToFile(Path.Combine(CellFolder.SubFolderPath(sub1Dir, ViewType.Layout), "main.clay"), sub1View);

        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = "Sub1", X = 10_000, Y = 20_000, Mag = 1.0 });
        var vm = SelectVm(top, Path.Combine(_workspaceDir, "top.clay"));

        // Sub2's corner (0,0) -> Sub1-local (5000,5000) -> world (15000,25000).
        int before = vm.SnapQueryRunCount;
        vm.OnPointerMoved(15_050, 25_050, leftDown: false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 0, snapTolDbu: SnapTol);

        Assert.True(vm.SnapQueryRunCount > before);
        Assert.NotNull(vm.Overlay.SnapMarker);
        var marker = vm.Overlay.SnapMarker!.Value;
        Assert.Equal(SnapFeatureKind.CornerEndpoint, marker.Kind);
        Assert.Equal(15_000, marker.X);
        Assert.Equal(25_000, marker.Y);
        Assert.True(marker.OwnerIsInstance);
        Assert.Equal(0, marker.OwnerIndex); // owned by the TOP-level instance (Sub1), per RecurseInstance's contract
    }

    // ── R-snpf-1/2/3: vertex and edge handle drags now snap; bulge/scale stay out of scope ────────

    [Fact]
    public void VertexDrag_OntoAnotherRectsCorner_LandsExactlyOnIt()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        // Off-grid corner, placed BEYOND shape 0's (0,0) corner so dragging (0,0) there never crosses
        // shape 0's OWN opposite corner (10000,10000) — a crossing would flip which field ends up
        // holding X1 vs. X2 once ResizeRectCorner's result is normalized at commit, which is a
        // property of Rect normalization, not of geometry snap, and would confuse the assertion below.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = -20_000, Y1 = -25_000, X2 = -2469, Y2 = -7091 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000); // select shape 0's body so its handles show
        Assert.Equal([0], vm.SelectedIndices);

        // Press exactly on shape 0's own (0,0) corner handle — a Rect corner maps to HandleDragKind.
        // RectCorner, R-snpf-2's "Vertex" row (the same position-shaped drag as a Polygon vertex).
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(-2469, -7091, true, KeyModifiers.None, 40, 0, SnapTol); // exactly on shape 1's off-grid corner
        vm.OnPointerReleased(-2469, -7091, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        // Lands EXACTLY on the off-grid target corner — a value ordinary grid snap (SnapDbu=1000)
        // could never produce, proving geometry snap overrode it (R-snpf-3).
        Assert.Equal(-2469, result.X1);
        Assert.Equal(-7091, result.Y1);
        Assert.Equal(10_000, result.X2); // opposite corner untouched — no inside-out flip occurred
        Assert.Equal(10_000, result.Y2);
    }

    [Fact]
    public void VertexDrag_NoCandidateInRange_FallsBackToGridSnap()
    {
        var model = FreshModel(); // SnapDbu=1000
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 0, SnapTol); // grab corner (0,0)
        vm.OnPointerMoved(3417, 2803, true, KeyModifiers.None, 40, 0, SnapTol); // nothing else nearby
        vm.OnPointerReleased(3417, 2803, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        // R-snpf-3's override order, second half: no candidate in range -> ordinary grid snap.
        Assert.Equal(3000, result.X1);
        Assert.Equal(3000, result.Y1);
    }

    [Fact]
    public void EdgeDrag_TowardAnotherShapesCorner_LandsOnItsProjectionOntoTheDragAxis()
    {
        var model = FreshModel();
        // Shape 0's bottom edge (edge index 0, horizontal -> perpendicular axis is vertical/Y).
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        // Target corner sits exactly where the cursor will move to — an off-grid Y.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 9500, Y1 = -3417, X2 = 20_000, Y2 = -1000 });
        var vm = SelectVm(model);

        Click(vm, 5000, 5000);
        Assert.Equal([0], vm.SelectedIndices);

        // Press the bottom edge's midpoint handle (5000,0); drag the cursor to land EXACTLY on the
        // target's corner (9500,-3417) — trivially within tolerance since the distance is zero.
        vm.OnPointerPressed(5000, 0, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(9500, -3417, true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(9500, -3417, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        // The edge's Y lands EXACTLY on the candidate's Y (projected onto the vertical drag axis) —
        // an off-grid value ordinary grid snap could never produce — while X1/X2 stay untouched: the
        // edge stays straight and the perpendicular constraint holds (gate 4's own wording).
        Assert.Equal(-3417, result.Y1);
        Assert.Equal(0, result.X1);
        Assert.Equal(10_000, result.X2);
    }

    [Fact]
    public void BulgeDrag_NeverConsultsASnapCandidate_EvenWithOneRightWhereTheCursorMoves()
    {
        var model = FreshModel(1); // no grid-snapping noise
        var curve = new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 2000, 0, 2000, 1000],
            Edges = [new LayoutEdge { Kind = EdgeKind.Arc, Bulge = 0.3 }, new LayoutEdge { Kind = EdgeKind.Line }, new LayoutEdge { Kind = EdgeKind.Line }],
        };
        model.Shapes.Add(curve);
        // A candidate corner sitting exactly where the bulge drag below will move the cursor.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 900, Y1 = -600, X2 = 1100, Y2 = -400 });
        var vm = SelectVm(model);

        Click(vm, 1000, -50); // select the curve via a body click near its (roughly) closed outline
        Assert.Equal([0], vm.SelectedIndices);

        var originalArc = LayoutArc.FromBulge(0, 0, 2000, 0, 0.3);
        double midAngle = originalArc.StartAngle + originalArc.Sweep / 2;
        long handleX = (long)Math.Round(originalArc.Cx + originalArc.R * Math.Cos(midAngle));
        long handleY = (long)Math.Round(originalArc.Cy + originalArc.R * Math.Sin(midAngle));

        int before = vm.SnapQueryRunCount;
        vm.OnPointerPressed(handleX, handleY, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(1000, -500, true, KeyModifiers.None, 40, 0, SnapTol); // lands right on the candidate corner

        // R-snpf-2: a Bulge drag is a curvature control, not a position — UpdateSnapMarker never even
        // queries for it, so it stays out of scope no matter how close a candidate sits to the cursor.
        Assert.Equal(before, vm.SnapQueryRunCount);
        Assert.Null(vm.Overlay.SnapMarker);

        vm.OnPointerReleased(1000, -500, KeyModifiers.None);
    }

    [Fact]
    public void ScaleDrag_NeverConsultsASnapCandidate_EvenWithOneRightWhereTheCursorMoves()
    {
        var model = FreshModel();
        var a = new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 };
        var b = new RectShape { Layer = new LayerKey(1, 0), X1 = 2000, Y1 = 0, X2 = 3000, Y2 = 1000 };
        model.Shapes.Add(a); model.Shapes.Add(b);
        // A candidate corner sitting exactly where the scale drag below will move the cursor.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = 6000, Y1 = 2000, X2 = 6200, Y2 = 2200 });
        var vm = SelectVm(model);

        Click(vm, 500, 500);               // select shape a
        Click(vm, 2500, 500, KeyModifiers.Shift); // add shape b -> 2+ selection shows bbox scale handles
        Assert.Equal([0, 1], vm.SelectedIndices);

        int before = vm.SnapQueryRunCount;
        // Combined bbox top-right corner is (3000,1000); drag it out to exactly (6000,2000) — the
        // candidate's own corner.
        vm.OnPointerPressed(3000, 1000, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(6000, 2000, true, KeyModifiers.None, 40, 0, SnapTol);

        // R-snpf-2: a Scale drag moves many points at once with no single grab point to snap — stays
        // out of scope exactly like Bulge.
        Assert.Equal(before, vm.SnapQueryRunCount);
        Assert.Null(vm.Overlay.SnapMarker);

        vm.OnPointerReleased(6000, 2000, KeyModifiers.None);
    }

    // ── R-snpf-4/5/6: self-exclusion applies to every drag, covers every selected shape/instance, ──
    // ── and is never fooled by a dragged shape's now-stale (still-in-Model) pre-drag geometry ──────

    [Fact]
    public void BodyDrag_PlainMoveStartedFromTheShapesBody_NeverAttractsToItsOwnGeometry()
    {
        var model = FreshModel();
        // The ONLY shape in the model — its own corners are the only possible (and wrong) candidates.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 20_000 });
        var vm = SelectVm(model);

        // A PLAIN body click (not a marker click) begins the drag — this is the reported bug's own
        // exact trigger: R-snpf-4 says exclusion previously applied ONLY to a marker-initiated drag.
        // (6000,4000) is deliberately NOT the shape's centroid (10000,10000) or any corner/midpoint —
        // all of which sit within SnapTol of the geometric center, which would otherwise make this
        // click a MARKER click (R-snp-8) and anchor on a feature instead of the raw press point,
        // defeating the "started from the body" premise this test is actually about. Nearest feature
        // to (6000,4000) is the (10000,0) midpoint at distance ~5657, safely outside SnapTol (3000).
        vm.OnPointerPressed(6000, 4000, KeyModifiers.None, 1, 40, 0, SnapTol);
        Assert.Equal([0], vm.SelectedIndices);
        Assert.False(vm.SnapDragActiveForTests, "a body click, not a marker click, must have begun this drag");

        // Mid-drag, the cursor moves within SnapTol of shape 0's OWN corner (20000,20000) — without
        // per-tick, drag-kind-agnostic exclusion this would attract the shape to itself. Since this
        // drag was NOT snap-grabbed, the "always visible during a grab" fallback does not apply either
        // — the marker must be genuinely null, not a persisted echo of anything.
        vm.OnPointerMoved(19_950, 19_950, true, KeyModifiers.None, 40, 0, SnapTol);
        Assert.Null(vm.Overlay.SnapMarker);
        vm.OnPointerReleased(19_950, 19_950, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        // Ordinary grid-snapped move: anchor (6000,4000) -> SnapValue(19950-6000=13950,1000)=14000,
        // SnapValue(19950-4000=15950,1000)=16000.
        Assert.Equal(14_000, result.X1);
        Assert.Equal(16_000, result.Y1);
    }

    [Fact]
    public void BodyDrag_ThreeSelectedShapes_ExcludesAllThreeFromAttraction()
    {
        var model = FreshModel();
        // 20000x20000 (not 1000x1000, and not merely "away from corners/midpoints/centroid") so a body
        // click can clear a snap candidate's true reach: the lowest-priority "Nearest point on EDGE"
        // candidate fires from ANYWHERE within SnapTol of any of the four bounding edge LINES, not just
        // the discrete corner/midpoint/centroid points — a smaller shape (or a press point merely a
        // Euclidean SnapTol away from the discrete features) can still land within the perpendicular
        // reach of an edge itself and trigger a marker click. (6000,4000) is 4000+ from every one of
        // shape 0's own four edges — the same off-feature point already verified safe for the
        // single-shape body-drag tests above.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 20_000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 30_000, Y1 = 0, X2 = 50_000, Y2 = 20_000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 60_000, Y1 = 0, X2 = 80_000, Y2 = 20_000 });
        var vm = SelectVm(model);
        vm.SelectAllCommand.Execute(null); // all three
        Assert.Equal([0, 1, 2], vm.SelectedIndices);

        vm.OnPointerPressed(6000, 4000, KeyModifiers.None, 1, 40, 0, SnapTol);
        Assert.False(vm.SnapDragActiveForTests, "a body click, not a marker click, must have begun this drag");

        // The cursor lands exactly on shape 1's own corner (30000,0) — one of the THREE selected
        // shapes' own corners; a single-int exclusion (R-snpf-5's own diagnosis) could only ever have
        // excluded ONE of them.
        vm.OnPointerMoved(30_000, 0, true, KeyModifiers.None, 40, 0, SnapTol);
        Assert.Null(vm.Overlay.SnapMarker);
        vm.OnPointerReleased(30_000, 0, KeyModifiers.None);
    }

    [Fact]
    public void BodyDrag_SelectedInstance_ExcludesThatInstanceFromAttraction()
    {
        CreateCellWithCorner("Sub", 0, 0);
        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = "Sub", X = 0, Y = 0, Mag = 1.0 });
        var vm = SelectVm(top, Path.Combine(_workspaceDir, "top.clay"));

        // No shape under (200,200) -> falls through to instance click-select (R-L3a-5's own path),
        // which also begins a move-drag. A small press-tolerance (100, well under the ~283 distance to
        // the sub-cell's own nearest corner) keeps this a genuine body click, not a marker click.
        vm.OnPointerPressed(200, 200, KeyModifiers.None, 1, 40, 0, snapTolDbu: 100);
        Assert.Equal([0], vm.SelectedInstanceIndices);
        Assert.False(vm.SnapDragActiveForTests);

        // The cursor lands exactly on the SELECTED instance's own (sub-cell) corner (1000,1000) —
        // it must not attract to itself.
        vm.OnPointerMoved(1000, 1000, true, KeyModifiers.None, 40, 0, SnapTol);
        Assert.Null(vm.Overlay.SnapMarker);
        vm.OnPointerReleased(1000, 1000, KeyModifiers.None);
    }

    [Fact]
    public void BodyDrag_MidDrag_NeverFindsAStaleCandidateAtTheDraggedShapesPreDragLocation()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 20_000, Y2 = 20_000 });
        var vm = SelectVm(model);

        // Same off-feature press point as the test above — a genuine body click.
        vm.OnPointerPressed(6000, 4000, KeyModifiers.None, 1, 40, 0, SnapTol);
        Assert.False(vm.SnapDragActiveForTests);
        vm.OnPointerMoved(30_000, 30_000, true, KeyModifiers.None, 40, 0, SnapTol); // drag far away first
        Assert.Null(vm.Overlay.SnapMarker);

        // Bring the cursor back over the shape's ORIGINAL (pre-drag) corner (0,0) — Model still holds
        // this geometry untouched (the live preview lives only in DragOverrides, R-snpf-6), so a query
        // that read Model without excluding the dragged shape would find it here and snap the drag
        // right back onto its own stale position.
        vm.OnPointerMoved(0, 0, true, KeyModifiers.None, 40, 0, SnapTol);
        Assert.Null(vm.Overlay.SnapMarker);

        vm.OnPointerReleased(0, 0, KeyModifiers.None);
        var result = (RectShape)model.Shapes[0];
        // Ordinary grid-snapped delta from anchor (6000,4000) to (0,0): SnapValue(-6000,1000)=-6000,
        // SnapValue(-4000,1000)=-4000.
        Assert.Equal(-6000, result.X1);
        Assert.Equal(-4000, result.Y1);
    }

    // ── Cost stays bounded once hover runs continuously (gate 10) ──────────────────────────────────

    [Fact]
    public void HoverThenSubPixelMove_QueryRunsAtMostOncePerQualifyingMove()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        int before = vm.SnapQueryRunCount;
        vm.OnPointerMoved(200, 100, false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 50, snapTolDbu: SnapTol);
        Assert.Equal(before + 1, vm.SnapQueryRunCount); // exactly one run for this qualifying move

        int afterFirst = vm.SnapQueryRunCount;
        // Sub-device-pixel move (well under pixelDbu=50) — R-snp-16 must still skip it, now that hover
        // reaches UpdateSnapMarker on every plain pointer move too.
        vm.OnPointerMoved(210, 110, false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 50, snapTolDbu: SnapTol);
        Assert.Equal(afterFirst, vm.SnapQueryRunCount);

        // A genuine move past one device pixel still re-runs it exactly once.
        vm.OnPointerMoved(300, 300, false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 50, snapTolDbu: SnapTol);
        Assert.Equal(afterFirst + 1, vm.SnapQueryRunCount);
    }
}
