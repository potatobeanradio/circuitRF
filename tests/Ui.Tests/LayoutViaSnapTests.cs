using System.Diagnostics;
using Avalonia.Input;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Two owner reports about the via primitive's geometry snap, plus the hang one of them turned out to
/// be sitting next to. Driven through the same OnPointerPressed/Moved/Released dispatch
/// <c>LayoutCanvas</c> uses, mirroring <c>LayoutSnapGestureTests</c>.
///
/// <list type="number">
/// <item><b>"The rendered glyph is a square, which should mean corner."</b> It did: a
/// <see cref="ViaShape"/> registered its CENTRE under <see cref="SnapFeatureKind.CornerEndpoint"/>.
/// A via has no corners — X/Y is the centre — so it is a Centroid and draws the circle glyph.</item>
///
/// <item><b>"The centroid glyph follows the mouse, so with grid snapping on it is not in the
/// centre."</b> During a grab-role drag with nothing else in range, the marker fell back to a
/// synthetic echo drawn at the RAW cursor, while the geometry under it moved by a GRID-SNAPPED delta
/// — so the marker drifted off its own shape by up to half a snap step for the whole drag. It is
/// drawn at the grabbed feature's own snapped position now.</item>
///
/// <item><b>The hang.</b> Placing a via and then moving the pointer could peg a core at 100% with no
/// progress. <c>LayoutSnapFeatureIndex</c> sizes its buckets from the cell's own extent, so a cell
/// whose only feature is a single point gets the 1-DBU floor; the snap tolerance is a few screen
/// pixels converted at the current zoom, so on a zoomed-out board it is hundreds of thousands of DBU.
/// The bucket sweep was therefore asked for ~10^12 iterations over a dictionary holding one entry.
/// Only a point-like shape produces the degenerate span, and only some zooms produce a large enough
/// radius — hence "intermittent, and I can't reproduce it any more".</item>
/// </list>
/// </summary>
public class LayoutViaSnapTests
{
    private static readonly LayerKey Metal = new(1, 0);

    private static LayoutView FreshModel(long snapDbu = 1000) => new()
    {
        DbuPerMicron = 1000,
        DisplayUnit  = LayoutUnit.Um,
        SnapDbu      = snapDbu,
    };

    private static LayoutEditorViewModel SelectVm(LayoutView model) =>
        new(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

    private static ViaShape Via(long x, long y) =>
        new() { Layer = Metal, X = x, Y = y, PadSize = 500_000, DrillSize = 300_000 };

    // ── 1. The glyph kind ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AViasCentre_IsACentroidFeature_NotACorner()
    {
        var model = FreshModel();
        model.Shapes.Add(Via(10_000, 20_000));

        var counters = new SnapQueryCounters();
        var found = LayoutSnapQuery.FindCandidates(
            model, null, "", 10_000, 20_000, tolDbu: 3_000, includeIntersections: false,
            null, null, ref counters);

        var atCentre = Assert.Single(found, c => c.X == 10_000 && c.Y == 20_000);
        Assert.Equal(SnapFeatureKind.Centroid, atCentre.Kind);
    }

    [Fact]
    public void AViaStillOffersExactlyOneSnapPoint_ItsCentre()
    {
        // The kind changed; the feature set did not. A via is a point, and nothing about its pad or
        // barrel diameter is a snappable feature today.
        var model = FreshModel();
        model.Shapes.Add(Via(0, 0));

        var counters = new SnapQueryCounters();
        var found = LayoutSnapQuery.FindCandidates(
            model, null, "", 500, 500, tolDbu: 5_000, includeIntersections: false, null, null, ref counters);

        Assert.Single(found);
    }

    // ── 2. The marker follows the grabbed feature, not the cursor ───────────────────────────────

    [Fact]
    public void DraggingAVia_TheMarkerStaysOnTheViasSnappedCentre_NotOnTheRawCursor()
    {
        // Snap step 1000; the via sits on the grid at (0,0). A cursor at (7400, 3300) snaps the DRAG
        // DELTA to (7000, 3000), so the via's centre lands at (7000, 3000) — and the marker must be
        // drawn there, not at the raw (7400, 3300).
        var model = FreshModel(snapDbu: 1000);
        model.Shapes.Add(Via(0, 0));
        var vm = SelectVm(model);

        vm.OnPointerPressed(200, 200, KeyModifiers.None, 1, hitTolDbu: 40, zoomPxPerDbu: 0, snapTolDbu: 3_000);
        Assert.True(vm.SnapDragActiveForTests, "the press should have grabbed the via's own snap marker");

        // Far from the via, so nothing real is in range and the synthetic echo is what gets drawn.
        vm.OnPointerMoved(7400, 3300, true, KeyModifiers.None, 40, 0, snapTolDbu: 3_000);

        var marker = vm.Overlay.SnapMarker;
        Assert.NotNull(marker);
        Assert.Equal(7000, marker!.Value.X);
        Assert.Equal(3000, marker.Value.Y);

        // And the geometry agrees: committing the drag puts the via exactly under the marker.
        vm.OnPointerReleased(7400, 3300, KeyModifiers.None);
        var moved = Assert.IsType<ViaShape>(model.Shapes[0]);
        Assert.Equal(marker.Value.X, moved.X);
        Assert.Equal(marker.Value.Y, moved.Y);
    }

    [Fact]
    public void TheEchoKeepsTheGrabbedFeaturesOwnKind()
    {
        var model = FreshModel(snapDbu: 1000);
        model.Shapes.Add(Via(0, 0));
        var vm = SelectVm(model);

        vm.OnPointerPressed(200, 200, KeyModifiers.None, 1, 40, 0, 3_000);
        vm.OnPointerMoved(7400, 3300, true, KeyModifiers.None, 40, 0, 3_000);

        Assert.Equal(SnapFeatureKind.Centroid, vm.Overlay.SnapMarker!.Value.Kind);
    }

    [Fact]
    public void GridSnapStillGovernsTheCommittedDelta_WhileTheEchoIsShowing()
    {
        // The echo is DISPLAY ONLY (R-cmb-4/5). Regression guard: if it were ever treated as a real
        // target, the absolute-position branch would land the via on the raw cursor instead.
        var model = FreshModel(snapDbu: 1000);
        model.Shapes.Add(Via(0, 0));
        var vm = SelectVm(model);

        vm.OnPointerPressed(200, 200, KeyModifiers.None, 1, 40, 0, 3_000);
        vm.OnPointerMoved(7400, 3300, true, KeyModifiers.None, 40, 0, 3_000);
        vm.OnPointerReleased(7400, 3300, KeyModifiers.None);

        var moved = Assert.IsType<ViaShape>(model.Shapes[0]);
        Assert.Equal(7000, moved.X);
        Assert.Equal(3000, moved.Y);
    }

    // ── 3. The hang ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASingleVia_QueriedAtABoardScaleTolerance_ReturnsPromptly()
    {
        // The exact reported situation: one via on an otherwise empty layout, pointer moving while
        // zoomed out far enough that a few screen pixels are hundreds of thousands of DBU.
        //
        // Before the fix this ran a bucket sweep of (2*tol + 1)^2 = 1.44e12 dictionary probes.
        var model = FreshModel();
        model.Shapes.Add(Via(0, 0));

        var counters = new SnapQueryCounters();
        var sw = Stopwatch.StartNew();
        var found = LayoutSnapQuery.FindCandidates(
            model, null, "", 1_000, 1_000, tolDbu: 600_000, includeIntersections: false,
            null, null, ref counters);
        sw.Stop();

        Assert.Single(found);

        // The real gate, and deliberately NOT a wall-clock one: the work is bounded by the feature
        // count, not by the query radius. FeaturesExamined alone could not have caught this — the
        // wasted work was empty-bucket probes, of which there was exactly one feature's worth to find.
        Assert.True(counters.BucketsProbed <= 1,
            $"probed {counters.BucketsProbed:N0} grid buckets for a one-feature cell — the sweep is unbounded again");

        // Belt and braces, generously loose: the failure this guards is a hang, not slowness.
        Assert.True(sw.ElapsedMilliseconds < 2_000,
            $"a one-via layout took {sw.ElapsedMilliseconds} ms to query");
    }

    [Fact]
    public void PlacingAViaThenMovingThePointer_DoesNotHang()
    {
        // The gesture as reported: place a via with the Via tool, deselect, move the pointer. Drives
        // the real dispatch so a future regression in EITHER the query or its caller fails here.
        var model = FreshModel();
        var vm = new LayoutEditorViewModel(model)
        {
            Technology  = StarterTechnologies.Pcb2Layer(),
            ActiveTool  = LayoutEditorViewModel.Tool.Via,
        };

        vm.OnPointerPressed(0, 0, KeyModifiers.None, 1);
        Assert.Single(model.Shapes);

        vm.ActiveTool = LayoutEditorViewModel.Tool.Select;
        vm.DeselectAllCommand.Execute(null);

        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= 20; i++)
            vm.OnPointerMoved(i * 25_000, i * 25_000, false, KeyModifiers.None,
                              hitTolDbu: 200_000, pixelDbu: 100_000, snapTolDbu: 600_000);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2_000,
            $"20 pointer moves over a one-via layout took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TheBucketSweepIsStillUsedWhenItIsCheaper()
    {
        // The fallback must not become the only path — a dense cell queried at a small tolerance
        // should still examine far fewer features than it holds.
        var model = FreshModel();
        for (int i = 0; i < 400; i++)
            model.Shapes.Add(new RectShape
            {
                Layer = Metal,
                X1 = i * 10_000, Y1 = 0, X2 = i * 10_000 + 4_000, Y2 = 4_000,
            });

        var counters = new SnapQueryCounters();
        LayoutSnapQuery.FindCandidates(
            model, null, "", 0, 0, tolDbu: 1_000, includeIntersections: false, null, null, ref counters);

        // 400 rects x 9 intrinsic features each = 3,600; a 1,000-DBU query near the origin must not
        // walk all of them.
        Assert.True(counters.FeaturesExamined < 400,
            $"examined {counters.FeaturesExamined} features — the grid is no longer pruning");
        Assert.True(counters.BucketsProbed > 0, "the bucket sweep should still be the path taken here");
    }
}
