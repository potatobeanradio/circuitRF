// Phase L2b — docs/sonnet-briefs/brief-L2b-spatial-index.md. Direct tests of LayoutSpatialIndex
// itself: STR bulk-load / incremental insert-with-split / remove correctness against a reference
// linear scan, the lazy self-healing staleness check (gate for the "test never calls NotifyChanged"
// scenario the whole design leans on), degradation-triggered rebuild, and ConservativeBboxOf's
// safety margin over the narrower per-consumer notions of a shape's extent.

using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

public class LayoutSpatialIndexTests
{
    private static LayoutView FreshView() => new() { DbuPerMicron = 1000, SnapDbu = 1000 };

    /// <summary>The pre-L2b linear-scan behavior, reimplemented here as the reference oracle every
    /// index query is checked against — deliberately independent of <see cref="LayoutSpatialIndex"/>'s
    /// own internals.</summary>
    private static List<int> LinearScanIntersecting(IReadOnlyList<LayoutShape> shapes, Bbox rect)
    {
        var result = new List<int>();
        for (int i = 0; i < shapes.Count; i++)
        {
            var bb = LayoutSpatialIndex.ConservativeBboxOf(shapes[i]);
            if (!bb.IsEmpty && bb.Intersects(rect)) result.Add(i);
        }
        return result;
    }

    private static RectShape Rect(long cx, long cy, long half) =>
        new() { Layer = new LayerKey(1, 0), X1 = cx - half, Y1 = cy - half, X2 = cx + half, Y2 = cy + half };

    // ── STR bulk load correctness ────────────────────────────────────────────────

    [Fact]
    public void QueryIntersecting_ClusteredShapes_MatchesLinearScanForManyQueryRects()
    {
        var view = FreshView();
        var rng = new System.Random(7);
        // Deliberately clustered (not uniform), mirroring L2a's own generator methodology.
        for (int cluster = 0; cluster < 8; cluster++)
        {
            long ccx = rng.Next(-500_000, 500_000), ccy = rng.Next(-500_000, 500_000);
            for (int i = 0; i < 200; i++)
                view.Shapes.Add(Rect(ccx + rng.Next(-5000, 5000), ccy + rng.Next(-5000, 5000), 200));
        }

        var queryRects = new[]
        {
            new Bbox(-1_000_000, -1_000_000, 1_000_000, 1_000_000), // everything
            new Bbox(-100, -100, 100, 100),                          // tiny, likely nothing
            new Bbox(0, 0, 10_000, 10_000),                          // a quadrant
            new Bbox(long.MinValue / 4, long.MinValue / 4, long.MaxValue / 4, long.MaxValue / 4), // huge
        };

        foreach (var rect in queryRects)
        {
            var expected = LinearScanIntersecting(view.Shapes, rect);
            expected.Sort();
            var actual = view.SpatialIndex.QueryIntersecting(view.Shapes, rect);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void QueryIntersecting_EmptyView_ReturnsEmpty()
    {
        var view = FreshView();
        Assert.Empty(view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(-1000, -1000, 1000, 1000)));
    }

    [Fact]
    public void QueryIntersecting_EmptyQueryRect_MatchesNothing()
    {
        var view = FreshView();
        view.Shapes.Add(Rect(0, 0, 1000));
        Assert.Empty(view.SpatialIndex.QueryIntersecting(view.Shapes, Bbox.Empty));
    }

    // ── The lazy self-healing staleness check — the whole reason ~2,300 pre-existing tests need
    // zero changes (see LayoutSpatialIndex's own type doc comment) ────────────────────────────────

    [Fact]
    public void TestConstructedView_NeverCallsNotifyChanged_StillQueriesCorrectly()
    {
        var view = FreshView();
        // Direct Shapes.Add — exactly the pattern the overwhelming majority of this project's Layout
        // tests use. NotifyChanged is never called.
        for (int i = 0; i < 50; i++) view.Shapes.Add(Rect(i * 1000, 0, 400));

        var result = view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(-1000, -1000, 5000, 1000));
        var expected = LinearScanIntersecting(view.Shapes, new Bbox(-1000, -1000, 5000, 1000));
        Assert.Equal(expected.OrderBy(x => x), result.OrderBy(x => x));
    }

    [Fact]
    public void TestConstructedView_MutatedAgainAfterFirstQuery_SelfHealsOnNextQuery()
    {
        // Not a pattern any real test in this repo uses (confirmed by search before this phase), but
        // the count-based staleness check should still self-heal correctly if it ever occurred: a
        // direct Add AFTER the index has already been lazily built once must still be picked up by
        // the next query, since Shapes.Count changed.
        var view = FreshView();
        view.Shapes.Add(Rect(0, 0, 400));
        _ = view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(-100_000, -100_000, 100_000, 100_000)); // builds the index

        view.Shapes.Add(Rect(50_000, 0, 400)); // direct mutation, no NotifyChanged

        var result = view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(49_000, -1000, 51_000, 1000));
        Assert.Equal([1], result);
    }

    // ── Incremental insert with split, and remove ────────────────────────────────

    [Fact]
    public void IncrementalAppend_PastNodeCapacity_TriggersSplit_QueryStaysCorrect()
    {
        var view = FreshView();
        // First shape via Apply so the index is genuinely "built" (not falling back to a lazy full
        // rebuild for every subsequent Appended call).
        view.Shapes.Add(Rect(0, 0, 400));
        view.NotifyChanged(LayoutChangeInfo.Appended(0, 1));
        int rebuildsAfterFirst = view.SpatialIndex.FullRebuildCount;

        // 40 more incremental appends — comfortably past MaxEntries (16), forcing at least one split.
        for (int i = 1; i <= 40; i++)
        {
            view.Shapes.Add(Rect(i * 1000, 0, 400));
            view.NotifyChanged(LayoutChangeInfo.Appended(i, 1));
        }

        Assert.Equal(rebuildsAfterFirst, view.SpatialIndex.FullRebuildCount); // still no rebuild — pure incremental
        Assert.True(view.SpatialIndex.IncrementalApplyCount >= 40);

        var expected = LinearScanIntersecting(view.Shapes, new Bbox(-1_000_000, -1_000_000, 1_000_000, 1_000_000));
        var actual = view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(-1_000_000, -1_000_000, 1_000_000, 1_000_000));
        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));
    }

    [Fact]
    public void RemovedTrailing_ExcludesRemovedIndices_QueryStaysCorrect()
    {
        var view = FreshView();
        for (int i = 0; i < 30; i++) view.Shapes.Add(Rect(i * 1000, 0, 400));
        view.NotifyChanged(LayoutChangeInfo.Appended(0, 30));

        view.Shapes.RemoveRange(25, 5); // trailing 5
        view.NotifyChanged(LayoutChangeInfo.RemovedTrailing(25, 5));

        var result = view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(-1_000_000, -1_000_000, 1_000_000, 1_000_000));
        Assert.Equal(Enumerable.Range(0, 25), result.OrderBy(x => x));
    }

    [Fact]
    public void Updated_MovesShapeOutOfOldRegion_QueryReflectsNewPosition()
    {
        var view = FreshView();
        view.Shapes.Add(Rect(0, 0, 400));
        view.NotifyChanged(LayoutChangeInfo.Appended(0, 1));

        LayoutGeometry.TranslateBy(view.Shapes[0], 1_000_000, 0);
        view.NotifyChanged(LayoutChangeInfo.Updated([0]));

        Assert.Empty(view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(-1000, -1000, 1000, 1000)));
        Assert.Equal([0], view.SpatialIndex.QueryIntersecting(view.Shapes, new Bbox(999_000, -1000, 1_001_000, 1000)));
    }

    // ── R-L2b-2: degradation-triggered rebuild ───────────────────────────────────

    [Fact]
    public void HeavyChurn_EventuallyTriggersAFullRebuild()
    {
        var view = FreshView();
        for (int i = 0; i < 100; i++) view.Shapes.Add(Rect(i * 1000, 0, 400));
        view.NotifyChanged(LayoutChangeInfo.Appended(0, 100));
        int rebuildsAfterInitial = view.SpatialIndex.FullRebuildCount;

        // Move shape 0 back and forth many times — pure Updated churn, well past the
        // max(2000, count/4) threshold for a 100-shape view (threshold = 2000).
        for (int i = 0; i < 2100; i++)
        {
            LayoutGeometry.TranslateBy(view.Shapes[0], 1, 0);
            view.NotifyChanged(LayoutChangeInfo.Updated([0]));
        }

        Assert.True(view.SpatialIndex.FullRebuildCount > rebuildsAfterInitial,
            "expected churn to eventually trigger a quality-restoring full rebuild");
    }

    // ── ConservativeBboxOf — the safety margin ───────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(12)]
    public void ConservativeBboxOf_Label_ContainsTheApproximateHitTestFootprint(int rotationOrdinal)
    {
        var rotation = (LayoutRotation)(rotationOrdinal % 4);
        var label = new LabelShape { X = 1000, Y = 2000, Text = "Hello World", Height = 3000, Rotation = rotation };

        var conservative = LayoutSpatialIndex.ConservativeBboxOf(label);
        var exact = LayoutGeometry.BboxOf(label); // the zero-size point marquee's own predicate uses

        Assert.False(conservative.IsEmpty);
        // The exact (point) bbox must always be inside the conservative one.
        Assert.True(conservative.Contains(exact.MinX, exact.MinY));

        // A generous character-count*height pad in every direction — comfortably covers any
        // reasonable font's real glyph extent at any of the four rotations without needing Skia.
        long pad = (label.Text.Length + 1) * label.Height;
        Assert.Equal(new Bbox(label.X - pad, label.Y - pad, label.X + pad, label.Y + pad), conservative);
    }

    [Fact]
    public void ConservativeBboxOf_EmptyLabel_IsAPoint()
    {
        var label = new LabelShape { X = 5, Y = 9, Text = "", Height = 1000 };
        Assert.Equal(new Bbox(5, 9, 5, 9), LayoutSpatialIndex.ConservativeBboxOf(label));
    }

    [Theory]
    [MemberData(nameof(NonLabelShapes))]
    public void ConservativeBboxOf_NonLabelShapes_EqualsLayoutGeometryBboxOf(LayoutShape shape)
    {
        Assert.Equal(LayoutGeometry.BboxOf(shape), LayoutSpatialIndex.ConservativeBboxOf(shape));
    }

    public static IEnumerable<object[]> NonLabelShapes()
    {
        yield return [Rect(0, 0, 500)];
        yield return [new CircleShape { Layer = new LayerKey(1, 0), Cx = 10, Cy = 20, R = 500 }];
        yield return [new RoundedRectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000, CornerRadius = 100 }];
        yield return [new ViaShape { Layer = new LayerKey(1, 0), X = 0, Y = 0, PadSize = 800, DrillSize = 300 }];
        yield return [new BitmapShape { Layer = new LayerKey(1, 0), X = 0, Y = 0, W = 2000, H = 1000 }];
        yield return [new PolygonShape { Layer = new LayerKey(1, 0), Xy = [0, 0, 1000, 0, 1000, 1000, 0, 1000] }];
    }
}
