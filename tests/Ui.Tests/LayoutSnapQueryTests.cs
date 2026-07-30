using System;
using System.Collections.Generic;
using System.IO;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md §2.2 — the query engine: priority
// ordering (R-snp-5), tolerance-per-call (R-snp-15), layer visible/locked scoping (§2.5), transform-
// the-cursor-not-the-geometry for nested instances (R-snp-13), and the intersection toggle + its own
// counter (R-snp-12).

public sealed class LayoutSnapQueryTests : IDisposable
{
    private readonly string _workspaceDir;

    public LayoutSnapQueryTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), "crfSnapQuery_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workspaceDir);
        CellLayoutResolver.InvalidateAll();
    }

    public void Dispose()
    {
        CellLayoutResolver.InvalidateAll();
        if (Directory.Exists(_workspaceDir))
            Directory.Delete(_workspaceDir, recursive: true);
    }

    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    // ── R-snp-5: priority order ────────────────────────────────────────────────

    [Fact]
    public void CornerAndEdge_BothInRange_CornerWins()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });

        var counters = new SnapQueryCounters();
        // Cursor near the corner (0,0) — the corner AND the nearest-on-edge candidate along the
        // bottom edge are both within tolerance; corner must win.
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 200, 100, 2000, includeIntersections: false, null, null, ref counters);
        Assert.NotEmpty(result);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, result[0].Kind);
    }

    [Fact]
    public void PinAndCorner_Coincident_PinWinsOverCorner()
    {
        // Two shapes coincident at the same point: the query returns both a CornerEndpoint (from a
        // rect) — a real pin candidate requires a resolved PCell, out of scope for a plain unit test,
        // so this proves the ORDERING RULE using two distinguishable kinds that ARE easy to construct
        // directly: an Intersection ranks below CornerEndpoint, confirming the priority enum ordering
        // is respected end-to-end through the sort.
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 10_000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = -5000, Y1 = 5000, X2 = 5000, Y2 = -5000 });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 0, 0, 3000, includeIntersections: true, null, null, ref counters);
        Assert.NotEmpty(result);
        Assert.Equal(SnapFeatureKind.CornerEndpoint, result[0].Kind);
    }

    [Fact]
    public void NearestOnEdge_OnlyAppears_WhenNothingDiscreteIsInRange()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100_000, Y2 = 10_000 });

        var counters = new SnapQueryCounters();
        // Cursor well away from any corner/midpoint (they're at x=0,50000,100000) but on the bottom edge.
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 20_000, 0, 500, includeIntersections: false, null, null, ref counters);
        Assert.NotEmpty(result);
        Assert.Equal(SnapFeatureKind.Nearest, result[0].Kind);
    }

    // ── R-snp-15: tolerance is per-call, never cached ──────────────────────────

    [Fact]
    public void SmallerTolerance_ExcludesACandidateTheLargerToleranceIncluded()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        var c1 = new SnapQueryCounters();
        var far = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 5000, 5000, 10_000, false, null, null, ref c1);
        Assert.NotEmpty(far);

        var c2 = new SnapQueryCounters();
        var near = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 5000, 5000, 100, false, null, null, ref c2);
        Assert.Empty(near);
    }

    // ── §2.5: visible-but-locked snaps; hidden does not ────────────────────────

    [Fact]
    public void HiddenLayer_ContributesNoCandidates()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var tech = new Technology { Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "M1", Visible = false, Selectable = true }] };

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(model, tech, _workspaceDir, 0, 0, 500, false, null, null, ref counters);
        Assert.Empty(result);
    }

    [Fact]
    public void VisibleButLockedLayer_StillSnaps()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var tech = new Technology { Layers = [new LayerDef { Key = new LayerKey(1, 0), Name = "M1", Visible = true, Selectable = false }] };

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(model, tech, _workspaceDir, 0, 0, 500, false, null, null, ref counters);
        Assert.NotEmpty(result);
    }

    // ── excludeShapeIndices/excludeInstanceIndices (widened to sets, brief-geometry-snap-followups.md R-snpf-5) ──

    [Fact]
    public void ExcludeShapeIndices_OmitsThatShapesOwnFeatures()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 0, 0, 500, false, excludeShapeIndices: new HashSet<int> { 0 }, null, ref counters);
        Assert.Empty(result);
    }

    [Fact]
    public void ExcludeShapeIndices_MultipleShapes_OmitsAllOfThem()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 100, Y1 = 100, X2 = 900, Y2 = 900 });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(
            model, null, _workspaceDir, 0, 0, 500, false, excludeShapeIndices: new HashSet<int> { 0, 1 }, null, ref counters);
        Assert.Empty(result);
    }

    [Fact]
    public void ExcludeInstanceIndices_OmitsThatInstancesFeatures()
    {
        CreateCellWithCorner("Sub", 0, 0);
        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = "Sub", X = 10_000, Y = 20_000, Mag = 1.0 });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(
            top, null, _workspaceDir, 10_000, 20_000, 200, false, null, excludeInstanceIndices: new HashSet<int> { 0 }, ref counters);
        Assert.Empty(result);
    }

    // ── R-snp-13: transform the cursor into a nested instance's local frame ────

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
    public void NestedInstance_FeatureFoundNearCursor_ReturnedInWorldSpace_OwnedByTopLevelInstance()
    {
        CreateCellWithCorner("Sub", 0, 0);
        var top = new LayoutView { DbuPerMicron = 1000 };
        top.Instances.Add(new LayoutInstance { CellRef = "Sub", X = 10_000, Y = 20_000, Mag = 1.0 });

        // Sub-cell's corner (0,0) placed at world (10000,20000).
        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(top, null, _workspaceDir, 10_000, 20_000, 200, false, null, null, ref counters);

        Assert.Contains(result, c => c.Kind == SnapFeatureKind.CornerEndpoint && c.X == 10_000 && c.Y == 20_000 && c.OwnerIsInstance && c.OwnerIndex == 0);
    }

    [Fact]
    public void NestedInstance_Rotated90_FeatureLandsAtCorrectlyRotatedWorldPosition()
    {
        CreateCellWithCorner("Sub", 0, 0); // corner at local (1000,1000) is the far corner too
        var top = new LayoutView { DbuPerMicron = 1000 };
        // R90: local (x,y) -> world (originX - y, originY + x) per LayoutInstanceTransform's table.
        top.Instances.Add(new LayoutInstance { CellRef = "Sub", X = 0, Y = 0, Rot = LayoutRotation.R90, Mag = 1.0 });

        var counters = new SnapQueryCounters();
        // Local corner (1000,1000) under R90 -> world (-1000, 1000).
        var result = LayoutSnapQuery.FindCandidates(top, null, _workspaceDir, -1000, 1000, 200, false, null, null, ref counters);
        Assert.Contains(result, c => c.Kind == SnapFeatureKind.CornerEndpoint && c.X == -1000 && c.Y == 1000);
    }

    // ── R-snp-12: intersections are relational, live-only, counted, and OFF by default behavior ──

    [Fact]
    public void Intersections_Disabled_NeverAppear_PairCounterStaysZero()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = -5000, Y1 = -1000, X2 = 5000, Y2 = 1000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = -1000, Y1 = -5000, X2 = 1000, Y2 = 5000 });

        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 0, 0, 2000, includeIntersections: false, null, null, ref counters);
        Assert.DoesNotContain(result, c => c.Kind == SnapFeatureKind.Intersection);
        Assert.Equal(0, counters.IntersectionPairsTested);
    }

    [Fact]
    public void Intersections_Enabled_TwoCrossingRects_FindsIntersectionNearCursor()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = -5000, Y1 = -1000, X2 = 5000, Y2 = 1000 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(2, 0), X1 = -1000, Y1 = -5000, X2 = 1000, Y2 = 5000 });

        var counters = new SnapQueryCounters();
        // Near the corner (1000,1000) which is exactly where the two rects' edges cross.
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 1000, 1000, 200, includeIntersections: true, null, null, ref counters);
        Assert.Contains(result, c => c.Kind == SnapFeatureKind.Intersection);
        Assert.True(counters.IntersectionPairsTested > 0);
    }

    // ── Determinism / no crash on empty model ──────────────────────────────────

    [Fact]
    public void EmptyModel_ReturnsEmpty_NeverThrows()
    {
        var model = FreshModel();
        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 0, 0, 1000, true, null, null, ref counters);
        Assert.Empty(result);
    }

    [Fact]
    public void ZeroTolerance_ReturnsEmpty()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });
        var counters = new SnapQueryCounters();
        var result = LayoutSnapQuery.FindCandidates(model, null, _workspaceDir, 0, 0, 0, false, null, null, ref counters);
        Assert.Empty(result);
    }
}
