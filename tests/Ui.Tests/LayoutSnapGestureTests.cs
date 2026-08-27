using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md §2.6/gate 7/9/10 — the click-through-
// marker headline behaviour, the grab/target drag roles, handle precedence, and Alt suppression.
// Driven through OnPointerPressed/Moved/Released exactly as LayoutCanvas would (mirrors
// LayoutHandleGesturesTests.cs's own methodology), so a wiring bug fails here even if the underlying
// query is correct in isolation.

public class LayoutSnapGestureTests
{
    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    // A large snap tolerance keeps these gesture tests independent of the exact SnapCanvas pixel
    // constant — the point under test is the DISPATCH, not the specific tolerance value.
    private const long SnapTol = 3000;

    [Fact]
    public void ClickMissesShapesOwnHitTest_ButLandsNearACorner_SelectsAndBeginsDrag()
    {
        var model = FreshModel();
        // A big rect, but the click below lands well OUTSIDE it (misses ordinary hit-test) while
        // still within SnapTol of its (0,0) corner.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 });
        var vm = SelectVm(model);

        // Press at (-2000,-2000): outside the rect entirely, but within SnapTol of corner (0,0).
        vm.OnPointerPressed(-2000, -2000, KeyModifiers.None, 1, hitTolDbu: 40, zoomPxPerDbu: 0, snapTolDbu: SnapTol);

        Assert.Equal([0], vm.SelectedIndices);
    }

    [Fact]
    public void GrabRole_ShapeTracksCursor_FromTheExactFeaturePoint_NotTheRawClick()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 });
        var vm = SelectVm(model);

        vm.OnPointerPressed(-2000, -2000, KeyModifiers.None, 1, 40, 0, SnapTol); // grabs corner (0,0)
        // Move the cursor to (3000,3000) — an offset of (+5000,+5000) from the RAW click point, but the
        // grab anchor is the FEATURE point (0,0), not the raw click (-2000,-2000).
        vm.OnPointerMoved(3000, 3000, true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(3000, 3000, KeyModifiers.None);

        // The grabbed corner lands EXACTLY on the cursor's current absolute position (3000,3000) — it
        // tracks the cursor directly from the moment of the grab, ignoring the (-2000,-2000) offset
        // between the raw click and the true feature point. Had the anchor instead been the raw click
        // point, the corner would have landed at (0,0) + (cursor - rawClick) = (5000,5000) instead —
        // this is the exact distinguishing case between the two possible (wrong vs. right) anchors.
        var result = (RectShape)model.Shapes[0];
        Assert.Equal(3000, result.X1);
        Assert.Equal(3000, result.Y1);
    }

    [Fact]
    public void TargetRole_ReleaseNearAnotherFeature_LandsExactlyOnIt()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }); // the one being dragged
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 47_531, Y1 = 52_909, X2 = 60_000, Y2 = 70_000 }); // target's corner is off-grid
        var vm = SelectVm(model);

        // Grab shape 0's corner (0,0) — click just outside it.
        vm.OnPointerPressed(-500, -500, KeyModifiers.None, 1, 40, 0, SnapTol);
        // Move near shape 1's corner (47531,52909) — well within SnapTol.
        vm.OnPointerMoved(47_500, 52_900, true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(47_500, 52_900, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        // The grabbed corner lands EXACTLY on the target's off-grid corner — proves target-attraction
        // overrode ordinary grid snapping (which could never produce these exact off-grid coordinates).
        Assert.Equal(47_531, result.X1);
        Assert.Equal(52_909, result.Y1);
    }

    // ── brief-snap-combobox-and-consistency.md R-cmb-4/5: geometry snap wins ONLY when it has a real
    // candidate to offer; otherwise grid snap must still apply to a move-drag — the bug this brief
    // reports is a grab-role drag free-floating on the raw cursor whenever nothing happens to be
    // nearby, which the "always visible marker" fix from the prior brief silently introduced by
    // reusing the same _currentSnapCandidate field for both rendering and position computation. ──────

    [Fact]
    public void GrabRole_GridSnapsWhenNoCandidateInRange_ThenLandsOnFeatureWhenOneAppears()
    {
        var model = FreshModel(); // SnapDbu = 1000
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 }); // dragged
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 47_531, Y1 = 52_909, X2 = 60_000, Y2 = 70_000 }); // off-grid target
        var vm = SelectVm(model);

        vm.OnPointerPressed(-500, -500, KeyModifiers.None, 1, 40, 0, SnapTol); // grabs corner (0,0)

        // Move somewhere with NOTHING nearby — R-cmb-4's own bug: geometry snap must NOT override
        // grid snap just because the MODE is enabled; grid snap must genuinely apply here.
        vm.OnPointerMoved(23_417, 18_803, true, KeyModifiers.None, 40, 0, SnapTol);
        var previewAway = (RectShape)vm.Overlay.DragOverrides[0];
        // Anchor is the grabbed corner (0,0); SnapValue(23417,1000)=23000, SnapValue(18803,1000)=19000
        // — an exact grid multiple, never the raw (23417,18803) cursor position.
        Assert.Equal(23_000, previewAway.X1);
        Assert.Equal(19_000, previewAway.Y1);

        // Now move near shape 1's off-grid corner — the candidate wins outright, exactly as before.
        vm.OnPointerMoved(47_500, 52_900, true, KeyModifiers.None, 40, 0, SnapTol);
        var previewOnTarget = (RectShape)vm.Overlay.DragOverrides[0];
        Assert.Equal(47_531, previewOnTarget.X1);
        Assert.Equal(52_909, previewOnTarget.Y1);

        vm.OnPointerReleased(47_500, 52_900, KeyModifiers.None);
        var result = (RectShape)model.Shapes[0];
        Assert.Equal(47_531, result.X1);
        Assert.Equal(52_909, result.Y1);
    }

    [Fact]
    public void PlainMoveDrag_GeometrySnapOnButNoCandidateAnywhere_PreservesOffGridVertexSpacing_OnlyOffsetChanges()
    {
        var model = FreshModel(); // SnapDbu = 1000, GeometrySnapEnabled defaults true
        // A deliberately off-grid, large polygon — large enough that a body click well inside it
        // clears every one of its OWN corner/midpoint/centroid/edge snap features by more than
        // SnapTol (a smaller shape's own features are all trivially within a few-thousand-DBU
        // tolerance of any interior point — see brief-geometry-snap-followups.md's own note on this),
        // so the press below is a genuine, non-marker-initiated body drag. No other shape exists in
        // the model, so no geometry candidate is EVER in range for this whole gesture — R-cmb-5's own
        // "no candidate -> grid-snap the delta" path, with geometry snap left ON throughout.
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [137, 291, 20_137, 291, 20_137, 18_291, 137, 18_291] };
        model.Shapes.Add(poly);
        var vm = SelectVm(model);
        Assert.True(vm.GeometrySnapEnabled);

        vm.OnPointerPressed(5137, 4291, KeyModifiers.None, 1, 40, 0, SnapTol);
        Assert.False(vm.SnapDragActiveForTests, "a body click, not a marker click, must have begun this drag");

        vm.OnPointerMoved(9137, 8291, true, KeyModifiers.None, 40, 0, SnapTol); // raw delta (4000,4000)
        vm.OnPointerReleased(9137, 8291, KeyModifiers.None);

        var result = (PolygonShape)model.Shapes[0];
        // SnapValue(4000,1000)=4000 exactly (already grid-aligned, chosen so the resulting numbers
        // are easy to verify directly) — every vertex shifts by the SAME delta.
        Assert.Equal(137 + 4000, result.Xy[0]); Assert.Equal(291 + 4000, result.Xy[1]);
        Assert.Equal(20_137 + 4000, result.Xy[2]); Assert.Equal(291 + 4000, result.Xy[3]);
        Assert.Equal(20_137 + 4000, result.Xy[4]); Assert.Equal(18_291 + 4000, result.Xy[5]);
        Assert.Equal(137 + 4000, result.Xy[6]); Assert.Equal(18_291 + 4000, result.Xy[7]);

        // R-cmb-5: the DELTA was snapped, never the position — the shape's own internal (off-grid)
        // vertex spacing survives exactly, only its offset moved.
        Assert.Equal(20_000, result.Xy[2] - result.Xy[0]); // width unchanged
        Assert.Equal(18_000, result.Xy[5] - result.Xy[1]); // height unchanged
    }

    // ── Owner follow-up (brief-geometry-snap-followups.md addendum): the marker glyph stays visible
    // for the WHOLE grab-role drag, even far from any real feature, and switches kind only when the
    // drag genuinely nears a DIFFERENT feature — never disappearing in between. ─────────────────────

    [Fact]
    public void GrabRole_MarkerRemainsVisibleThroughoutTheDrag_EvenFarFromAnyFeature()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = SelectVm(model);

        // Grab the shape's own CENTROID (5000,5000) via a click right on top of it.
        vm.OnPointerPressed(5000, 5000, KeyModifiers.None, 1, 40, 0, SnapTol);

        // Drag far away — nothing else exists in the model to attract to.
        vm.OnPointerMoved(80_000, 80_000, true, KeyModifiers.None, 40, 0, SnapTol);

        // The marker must stay visible throughout the drag, showing the ORIGINALLY-grabbed feature's
        // own kind (Centroid) even with nothing nearby to attract to — never null mid-drag.
        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.Equal(SnapFeatureKind.Centroid, vm.Overlay.SnapMarker!.Value.Kind);
        Assert.Equal(80_000, vm.Overlay.SnapMarker!.Value.X);
        Assert.Equal(80_000, vm.Overlay.SnapMarker!.Value.Y);

        vm.OnPointerReleased(80_000, 80_000, KeyModifiers.None);
    }

    [Fact]
    public void GrabRole_MarkerChangesKind_NearADifferentFeature_AndRevertsWhenItMovesAway()
    {
        var model = FreshModel();
        // Dragged shape, grabbed by its own centroid.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        // A second, far-away shape whose corner is the "different feature."
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 80_000, Y1 = 80_000, X2 = 90_000, Y2 = 90_000 });
        var vm = SelectVm(model);

        vm.OnPointerPressed(5000, 5000, KeyModifiers.None, 1, 40, 0, SnapTol); // grabs the CENTROID

        // Nowhere near shape 1 yet — the glyph shows the grabbed Centroid kind (the owner's own worked
        // example: "the circle glyph remains visible... until the rect... moves over a different feature").
        vm.OnPointerMoved(40_000, 40_000, true, KeyModifiers.None, 40, 0, SnapTol);
        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.Equal(SnapFeatureKind.Centroid, vm.Overlay.SnapMarker!.Value.Kind);

        // Now the cursor reaches shape 1's own corner — the glyph switches to CornerEndpoint.
        vm.OnPointerMoved(80_000, 80_000, true, KeyModifiers.None, 40, 0, SnapTol);
        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, vm.Overlay.SnapMarker!.Value.Kind);
        Assert.Equal(80_000, vm.Overlay.SnapMarker!.Value.X);
        Assert.Equal(80_000, vm.Overlay.SnapMarker!.Value.Y);

        // Moving away again reverts to the ORIGINALLY-grabbed kind (Centroid), never to null.
        vm.OnPointerMoved(40_000, 40_000, true, KeyModifiers.None, 40, 0, SnapTol);
        Assert.NotNull(vm.Overlay.SnapMarker);
        Assert.Equal(SnapFeatureKind.Centroid, vm.Overlay.SnapMarker!.Value.Kind);

        vm.OnPointerReleased(40_000, 40_000, KeyModifiers.None);
    }

    /// <summary>
    /// R-dup-2: Alt no longer suppresses the click-through. It used to, which made the one press most
    /// likely to be aimed at a feature the one press that could not be duplicated once Alt started
    /// arming a copy. The suppression case it used to cover is the test directly below, through the
    /// toggle that still does it.
    /// </summary>
    [Fact]
    public void AltModifier_NoLongerSuppressesSnap_TheClickStillSelectsThroughTheMarker()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 });
        var vm = SelectVm(model);

        vm.OnPointerPressed(-2000, -2000, KeyModifiers.Alt, 1, 40, 0, SnapTol);

        Assert.Equal([0], vm.SelectedIndices);
    }

    [Fact]
    public void GeometrySnapDisabled_ClickThroughDoesNotFire()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 50_000, Y2 = 50_000 });
        var vm = SelectVm(model);
        vm.GeometrySnapEnabled = false;

        vm.OnPointerPressed(-2000, -2000, KeyModifiers.None, 1, 40, 0, SnapTol);

        Assert.Empty(vm.SelectedIndices);
    }

    [Fact]
    public void HandlePrecedence_ClickWithinHandleRadius_HandleWins_NotSnap()
    {
        var model = FreshModel();
        var poly = new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 10_000, 0, 10_000, 10_000, 0, 10_000] };
        model.Shapes.Add(poly);
        // A second shape whose corner sits right where clicking would otherwise engage geometry snap.
        model.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = -200, Y1 = -200, X2 = -100, Y2 = -100 });
        var vm = SelectVm(model);

        // Select the polygon first (plain body click) so its own vertex handle at (0,0) is showing.
        vm.OnPointerPressed(5000, 5000, KeyModifiers.None, 1, 40, 0, 0);
        vm.OnPointerReleased(5000, 5000, KeyModifiers.None);
        Assert.Equal([0], vm.SelectedIndices);

        // Press exactly on the polygon's OWN vertex handle at (0,0) — within handle tolerance, and
        // also within snap tolerance of the second rect's corner. The handle must win (R-snp-10):
        // dragging must reshape the POLYGON's own vertex, not grab-drag the second shape.
        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(2000, 3000, true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnPointerReleased(2000, 3000, KeyModifiers.None);

        // Polygon vertex 0 moved (handle drag) — the second rect (index 1) is untouched and NOT selected.
        var resultPoly = (PolygonShape)model.Shapes[0];
        Assert.Equal(2000, resultPoly.Xy[0]);
        Assert.Equal(3000, resultPoly.Xy[1]);
        Assert.Equal([0], vm.SelectedIndices);
    }

    [Fact]
    public void ToggleMidDrag_RecomputesImmediately_WithoutWaitingForPointerMove()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 47_000, Y1 = 47_000, X2 = 48_000, Y2 = 48_000 });
        var vm = SelectVm(model);

        vm.OnPointerPressed(-500, -500, KeyModifiers.None, 1, 40, 0, SnapTol);
        // 46200 is deliberately NOT within Model.SnapDbu's (1000) half-step of 47000 — a cursor within
        // ±500 of 47000 would round to 47000 under ordinary grid snap too, making the toggle's effect
        // unobservable (this is exactly the trap the original version of this test fell into: 46990
        // rounds to 47000 regardless of whether target-attraction is engaged, so it could never have
        // distinguished the two cases). 46200 still sits well within SnapTol (3000) of the target
        // corner, so target-attraction is genuinely engaged before the toggle.
        vm.OnPointerMoved(46_200, 46_200, true, KeyModifiers.None, 40, 0, SnapTol);

        // Toggling geometry snap OFF mid-drag must immediately drop the live target attraction —
        // verified indirectly: after the toggle, a release should NOT land exactly on the target
        // corner (47000,47000) the way it would have with snap still engaged.
        vm.GeometrySnapEnabled = false;
        vm.OnPointerReleased(46_200, 46_200, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        Assert.NotEqual(47_000, result.X1);
    }

    [Fact]
    public void EscapeMidSnapDrag_LeavesModelUntouched()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var original = model.Shapes[0] is RectShape r ? (r.X1, r.Y1) : (0L, 0L);
        var vm = SelectVm(model);

        vm.OnPointerPressed(-500, -500, KeyModifiers.None, 1, 40, 0, SnapTol);
        vm.OnPointerMoved(4000, 4000, true, KeyModifiers.None, 40, 0, SnapTol);
        vm.OnKeyDown(Key.Escape, KeyModifiers.None);

        var result = (RectShape)model.Shapes[0];
        Assert.Equal(original, (result.X1, result.Y1));
    }
}
