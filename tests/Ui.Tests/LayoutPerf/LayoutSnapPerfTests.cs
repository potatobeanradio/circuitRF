// docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md gate 12 — the performance discipline the
// brief's own §3 guardrails require: at 500k shapes, snap-query cost stays bounded by what's actually
// near the cursor rather than growing with total design size; a large array shares ONE per-cell feature
// index across every placement rather than rebuilding it once per placement; and a sub-device-pixel
// cursor move skips the query outright (R-snp-16). Mirrors LayoutSpatialIndexPerfTests.cs's own
// established shape for a routine (untagged), counter-only 500k test — no Stopwatch, no timing sweep.

using System;
using System.IO;
using Avalonia.Input;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests.LayoutPerf;

public sealed class LayoutSnapPerfTests : IDisposable
{
    private readonly string _workspaceDir;

    public LayoutSnapPerfTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfSnapPerf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private const long ExtentHalf = 50_000_000; // must match SyntheticLayoutGenerator's own ExtentHalf

    /// <summary>Same 1%-of-extent grid histogram <c>LayoutSpatialIndexPerfTests</c>'s own
    /// <c>FindDensePoint</c> uses — finds a genuinely dense cluster to query near, rather than an
    /// arbitrary (likely near-empty) point in a mostly-empty 100mm×100mm design.</summary>
    private static (long X, long Y) FindDensePoint(LayoutView view)
    {
        const int grid = 20;
        long cell = 2 * ExtentHalf / grid;
        var counts = new int[grid, grid];
        foreach (var shape in view.Shapes)
        {
            var bb = LayoutGeometry.BboxOf(shape);
            if (bb.IsEmpty) continue;
            long cx = (bb.MinX + bb.MaxX) / 2, cy = (bb.MinY + bb.MaxY) / 2;
            int ix = (int)Math.Clamp((cx + ExtentHalf) / cell, 0, grid - 1);
            int iy = (int)Math.Clamp((cy + ExtentHalf) / cell, 0, grid - 1);
            counts[ix, iy]++;
        }
        int bestIx = 0, bestIy = 0, best = -1;
        for (int ix = 0; ix < grid; ix++)
        for (int iy = 0; iy < grid; iy++)
            if (counts[ix, iy] > best) { best = counts[ix, iy]; bestIx = ix; bestIy = iy; }
        return (-ExtentHalf + bestIx * cell + cell / 2, -ExtentHalf + bestIy * cell + cell / 2);
    }

    // ── Gate 12a: 500k shapes — features examined near the cursor stays far below the design total ──

    [Fact]
    public void Query500k_FeaturesExaminedIsBoundedNearCursor_NotProportionalToDesignSize()
    {
        const int shapeCount = 500_000;
        var view = SyntheticLayoutGenerator.Generate(shapeCount, 200, seed: 555, GeneratorProfile.Manhattan);
        var tech = SyntheticLayoutGenerator.GenerateTechnology(200);
        var (denseX, denseY) = FindDensePoint(view);

        var counters = new SnapQueryCounters();
        _ = LayoutSnapQuery.FindCandidates(
            view, tech, _workspaceDir, denseX, denseY, 2000, includeIntersections: false, null, null, ref counters);

        Assert.True(counters.FeaturesExamined < shapeCount / 10,
            $"FeaturesExamined={counters.FeaturesExamined} should be far below the design's total shape count={shapeCount}");
    }

    // ── Gate 12b: a 50×50 array shares ONE per-cell feature index across every placement ─────────

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
    public void Array50x50_EveryPlacementQueriesTheSameSharedSubCellFeatureIndex_NeverRebuiltPerPlacement()
    {
        CreateCellWithCorner("Sub", 0, 0);
        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = "Sub", X = 0, Y = 0, Rows = 50, Cols = 50, PitchX = 5000, PitchY = 5000, Mag = 1.0 });

        var res = CellLayoutResolver.Resolve("Sub", _workspaceDir);
        Assert.Equal(CellLayoutState.Resolved, res.State);
        var before = LayoutSnapFeatureIndex.Get(res.View!, null);

        var counters = new SnapQueryCounters();
        // Query near the first, a middle, and the last of the 2,500 array placements — every one of
        // them must resolve through the SAME per-cell feature index (RecurseInstance resolves the
        // sub-cell once, outside its own r/c loop, and LayoutSnapFeatureIndex.Get is a per-VIEW cache
        // keyed by reference — never once-per-placement).
        var nearFirst = LayoutSnapQuery.FindCandidates(top, null, _workspaceDir, 0, 0, 200, false, null, null, ref counters);
        Assert.Contains(nearFirst, c => c.Kind == SnapFeatureKind.CornerEndpoint);

        long midX = 25 * 5000, midY = 25 * 5000;
        var nearMiddle = LayoutSnapQuery.FindCandidates(top, null, _workspaceDir, midX, midY, 200, false, null, null, ref counters);
        Assert.Contains(nearMiddle, c => c.Kind == SnapFeatureKind.CornerEndpoint);

        long lastX = 49 * 5000, lastY = 49 * 5000;
        var nearLast = LayoutSnapQuery.FindCandidates(top, null, _workspaceDir, lastX, lastY, 200, false, null, null, ref counters);
        Assert.Contains(nearLast, c => c.Kind == SnapFeatureKind.CornerEndpoint);

        // The shared per-cell index was never evicted/rebuilt by any of the 2,500 placements this
        // queried across — the exact same instance still resolves for the sub-cell afterward.
        var after = LayoutSnapFeatureIndex.Get(res.View!, null);
        Assert.Same(before, after);
    }

    // ── Gate 12c: a sub-device-pixel cursor move never re-runs the query (R-snp-16) ───────────────

    [Fact]
    public void SubPixelCursorMove_SkipsTheSnapQuery_GenuineMoveStillRecomputes()
    {
        var model = new LayoutView { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        var vm = new LayoutEditorViewModel(model) { ActiveTool = LayoutEditorViewModel.Tool.Select };

        vm.OnPointerMoved(5000, 5000, false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 50, snapTolDbu: 2000);
        int afterFirst = vm.SnapQueryRunCount;
        Assert.True(afterFirst > 0);

        // Sub-device-pixel move (well under pixelDbu=50) — the query must not run again.
        vm.OnPointerMoved(5010, 5010, false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 50, snapTolDbu: 2000);
        Assert.Equal(afterFirst, vm.SnapQueryRunCount);

        // A genuine move past one device pixel DOES re-run it.
        vm.OnPointerMoved(5100, 5100, false, KeyModifiers.None, hitTolDbu: 40, pixelDbu: 50, snapTolDbu: 2000);
        Assert.True(vm.SnapQueryRunCount > afterFirst);
    }
}
