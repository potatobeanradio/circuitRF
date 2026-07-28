using CircuitRF.Ui.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

// ──────────────────────────────────────────────────────────────────────────────
//  Phase L3a gate 9 — index freshness for instances: after add, move, delete, array-change,
//  undo/redo, and a SUB-CELL RESOLUTION CHANGE, a linear scan and an index query return the same
//  instance set. Uses a synthetic instanceBboxOf delegate (independent of CellHierarchy/the
//  filesystem) so this is purely a test of LayoutSpatialIndex's OWN freshness bookkeeping — R-L3a-4.
// ──────────────────────────────────────────────────────────────────────────────

public sealed class LayoutSpatialIndexInstanceTests
{
    private static Bbox FixedBboxOf(LayoutInstance inst) => new(inst.X - 100, inst.Y - 100, inst.X + 100, inst.Y + 100);

    private static List<int> LinearScanInstances(IReadOnlyList<LayoutInstance> instances, Func<LayoutInstance, Bbox> bboxOf, Bbox rect)
    {
        var result = new List<int>();
        for (int i = 0; i < instances.Count; i++)
            if (bboxOf(instances[i]).Intersects(rect)) result.Add(i);
        return result;
    }

    private static List<int> QueryInstanceIndices(LayoutView view, Func<LayoutInstance, Bbox> bboxOf, long resolutionVersion, Bbox rect) =>
        view.SpatialIndex.QueryIntersecting(view.Shapes, view.Instances, bboxOf, resolutionVersion, rect)
            .Where(e => e.Kind == SpatialEntryKind.Instance)
            .Select(e => e.Index)
            .OrderBy(i => i)
            .ToList();

    private static LayoutView MakeView(int count)
    {
        var view = new LayoutView { DbuPerMicron = 1000 };
        for (int i = 0; i < count; i++)
            view.Instances.Add(new LayoutInstance { CellRef = $"Cell{i}", X = i * 1000, Y = 0, Mag = 1.0 });
        return view;
    }

    private static readonly Bbox Everything = new(-1_000_000, -1_000_000, 1_000_000, 1_000_000);

    [Fact]
    public void Query_MatchesLinearScan_Initially()
    {
        var view = MakeView(20);
        var expected = LinearScanInstances(view.Instances, FixedBboxOf, Everything);
        var actual = QueryInstanceIndices(view, FixedBboxOf, 0, Everything);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Query_AfterAdd_MatchesLinearScan()
    {
        var view = MakeView(5);
        _ = QueryInstanceIndices(view, FixedBboxOf, 0, Everything); // seed the index once
        view.Instances.Add(new LayoutInstance { CellRef = "New", X = 9000, Y = 0, Mag = 1.0 });
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);

        var expected = LinearScanInstances(view.Instances, FixedBboxOf, Everything);
        var actual = QueryInstanceIndices(view, FixedBboxOf, 0, Everything);
        Assert.Equal(expected, actual);
        Assert.Equal(6, actual.Count);
    }

    [Fact]
    public void Query_AfterMove_ReflectsNewBbox()
    {
        var view = MakeView(3);
        _ = QueryInstanceIndices(view, FixedBboxOf, 0, Everything);

        view.Instances[1].X = 500_000; // move far away
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);

        var narrowRect = new Bbox(-500, -500, 500, 500); // only index 0 (X=0) should match now
        var expected = LinearScanInstances(view.Instances, FixedBboxOf, narrowRect);
        var actual = QueryInstanceIndices(view, FixedBboxOf, 0, narrowRect);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain(1, actual);
    }

    [Fact]
    public void Query_AfterDelete_MatchesLinearScan()
    {
        var view = MakeView(5);
        _ = QueryInstanceIndices(view, FixedBboxOf, 0, Everything);

        view.Instances.RemoveAt(2);
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);

        var expected = LinearScanInstances(view.Instances, FixedBboxOf, Everything);
        var actual = QueryInstanceIndices(view, FixedBboxOf, 0, Everything);
        Assert.Equal(expected, actual);
        Assert.Equal(4, actual.Count);
    }

    [Fact]
    public void Query_AfterArrayChange_ReflectsExpandedBbox()
    {
        var view = MakeView(1);
        Bbox ArrayAwareBboxOf(LayoutInstance i)
        {
            long maxX = i.X + Math.Max(0, i.Cols - 1) * i.PitchX;
            return new Bbox(i.X - 100, i.Y - 100, maxX + 100, i.Y + 100);
        }
        _ = QueryInstanceIndices(view, ArrayAwareBboxOf, 0, Everything);

        view.Instances[0].Cols = 10;
        view.Instances[0].PitchX = 100_000;
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);

        var farRect = new Bbox(800_000, -100, 900_000, 100); // only inside the NEW array extent
        var expected = LinearScanInstances(view.Instances, ArrayAwareBboxOf, farRect);
        var actual = QueryInstanceIndices(view, ArrayAwareBboxOf, 0, farRect);
        Assert.Equal(expected, actual);
        Assert.Single(actual);
    }

    [Fact]
    public void Query_AfterUndoRedo_MatchesLinearScan()
    {
        var view = MakeView(3);
        _ = QueryInstanceIndices(view, FixedBboxOf, 0, Everything);

        // Simulate undo (removing the mutation and re-adding — mirrors DeleteInstancesCommand.Undo).
        var removed = view.Instances[1];
        view.Instances.RemoveAt(1);
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);
        Assert.Equal(2, QueryInstanceIndices(view, FixedBboxOf, 0, Everything).Count);

        view.Instances.Insert(1, removed);
        view.NotifyChanged(LayoutChangeInfo.InstancesOnly);

        var expected = LinearScanInstances(view.Instances, FixedBboxOf, Everything);
        var actual = QueryInstanceIndices(view, FixedBboxOf, 0, Everything);
        Assert.Equal(expected, actual);
        Assert.Equal(3, actual.Count);
    }

    [Fact]
    public void Query_ResolutionVersionChange_RefreshesEvenWithNoListMutation()
    {
        // R-L3a-4: "EnsureFresh must account for a resolution change, not just a shape-count change."
        // Same instance LIST (no add/move/delete at all) — only the bboxOf delegate's OWN answer
        // changes between calls (simulating "the same instance now resolves to different geometry"),
        // gated purely by the resolutionVersion token ticking.
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Instances.Add(new LayoutInstance { CellRef = "X", X = 0, Y = 0, Mag = 1.0 });

        bool resolvedYet = false;
        Bbox VersionedBboxOf(LayoutInstance i) => resolvedYet
            ? new Bbox(500_000, 500_000, 500_100, 500_100)
            : new Bbox(-100, -100, 100, 100);

        var nearRect = new Bbox(-1000, -1000, 1000, 1000);
        var farRect = new Bbox(499_000, 499_000, 501_000, 501_000);

        var beforeNear = QueryInstanceIndices(view, VersionedBboxOf, resolutionVersion: 1, nearRect);
        Assert.Single(beforeNear);

        resolvedYet = true; // "resolution changed" — same instance, different bbox
        var afterFarSameVersion = QueryInstanceIndices(view, VersionedBboxOf, resolutionVersion: 1, farRect);
        // Without bumping the version, staleness is not guaranteed to be detected (count unchanged) —
        // this call is just to show the OLD cached bbox may still be in effect; the real assertion is
        // the next one, after the version actually ticks.
        _ = afterFarSameVersion;

        var afterFarNewVersion = QueryInstanceIndices(view, VersionedBboxOf, resolutionVersion: 2, farRect);
        Assert.Single(afterFarNewVersion);
        var afterNearNewVersion = QueryInstanceIndices(view, VersionedBboxOf, resolutionVersion: 2, nearRect);
        Assert.Empty(afterNearNewVersion);
    }

    [Fact]
    public void ShapeQueries_Unaffected_ByInstanceEntries_SameTree()
    {
        // R-L3a-4: one shared tree — confirm the pre-existing shape-only QueryIntersecting overload
        // still returns exactly the shape entries when the SAME tree also holds instance entries.
        var view = new LayoutView { DbuPerMicron = 1000 };
        view.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });
        view.Instances.Add(new LayoutInstance { CellRef = "X", X = 50, Y = 50, Mag = 1.0 });

        _ = QueryInstanceIndices(view, FixedBboxOf, 0, Everything); // populate instance entries in the shared tree

        var shapeResult = view.SpatialIndex.QueryIntersecting(view.Shapes, Everything);
        Assert.Equal([0], shapeResult);
    }
}
