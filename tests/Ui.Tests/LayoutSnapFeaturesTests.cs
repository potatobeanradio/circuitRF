using System.Collections.Generic;
using System.Linq;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Tests;

// docs/sonnet-briefs/brief-snap-distance-and-geometry-snap.md §2.1/R-snp-12: the per-cell intrinsic
// feature index — corner/endpoint, midpoint, centroid, and (once, per resolved PCell) pins, all in
// cell-local coordinates, cached by LayoutView reference.

public class LayoutSnapFeaturesTests
{
    private static LayoutView FreshModel() => new() { DbuPerMicron = 1000, DisplayUnit = LayoutUnit.Um, SnapDbu = 1000 };

    private static bool Has(IReadOnlyList<IntrinsicSnapFeature> features, SnapFeatureKind kind, long x, long y) =>
        features.Any(f => f.Kind == kind && f.X == x && f.Y == y);

    [Fact]
    public void Rect_ProducesFourCorners_FourMidpoints_OneCentroid()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 10_000, Y2 = 20_000 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(5000, 10_000, 20_000, ref counters);

        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 0, 0));
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 10_000, 0));
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 10_000, 20_000));
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 0, 20_000));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 5000, 0));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 10_000, 10_000));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 5000, 20_000));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 0, 10_000));
        Assert.True(Has(near, SnapFeatureKind.Centroid, 5000, 10_000));
    }

    // Owner question, 2026-09-04: can a HOLE's centre be snapped to? It can — and it costs one extra
    // feature per RING, not per vertex, so a pour with 228 holes gains 228 entries against the ~21,772
    // hole vertices its rings already contribute. The centre is where a drilled hole's axis is; the
    // flattened arc's vertices, which were all that was offered before, are the one part of a round
    // hole nobody aims at.
    [Fact]
    public void PolygonHole_ContributesItsOwnCentre_NotOnlyItsRingVertices()
    {
        var model = FreshModel();
        model.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[40_000, 40_000, 60_000, 40_000, 60_000, 60_000, 40_000, 60_000]],
        });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(50_000, 50_000, 5_000, ref counters);

        Assert.True(Has(near, SnapFeatureKind.Centroid, 50_000, 50_000),
                    "the hole's own centre must be snappable");
    }

    [Fact]
    public void CurveHole_ContributesItsOwnCentre_TheSameWay()
    {
        var model = FreshModel();
        model.Shapes.Add(new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[10_000, 10_000, 30_000, 10_000, 30_000, 30_000, 10_000, 30_000]],
        });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(20_000, 20_000, 5_000, ref counters);

        Assert.True(Has(near, SnapFeatureKind.Centroid, 20_000, 20_000));
    }

    // The hole's ring features are unchanged — the centre is an ADDITION, not a replacement.
    [Fact]
    public void PolygonHole_StillContributesItsRingCornersAndMidpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy    = [0, 0, 100_000, 0, 100_000, 100_000, 0, 100_000],
            Holes = [[40_000, 40_000, 60_000, 40_000, 60_000, 60_000, 40_000, 60_000]],
        });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(45_000, 40_000, 15_000, ref counters);

        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 40_000, 40_000));
        Assert.True(Has(near, SnapFeatureKind.Midpoint, 50_000, 40_000));
    }

    [Fact]
    public void Circle_ProducesOnlyCentroid_NoCornersOrMidpoints()
    {
        var model = FreshModel();
        model.Shapes.Add(new CircleShape { Layer = new LayerKey(1, 0), Cx = 1000, Cy = 2000, R = 5000 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(1000, 2000, 100, ref counters);

        var only = Assert.Single(near);
        Assert.Equal(SnapFeatureKind.Centroid, only.Kind);
        Assert.Equal(1000, only.X);
        Assert.Equal(2000, only.Y);
    }

    [Fact]
    public void Polygon_WithHole_IncludesHoleVertices()
    {
        var model = FreshModel();
        model.Shapes.Add(new PolygonShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [0, 0, 20_000, 0, 20_000, 20_000, 0, 20_000],
            Holes = [[5000, 5000, 15_000, 5000, 15_000, 15_000, 5000, 15_000]],
        });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(5000, 5000, 100, ref counters);
        Assert.True(Has(near, SnapFeatureKind.CornerEndpoint, 5000, 5000));
    }

    [Fact]
    public void ArcEdge_MidpointIsTrueArcMidpoint_NotChordMidpoint()
    {
        var model = FreshModel();
        // A 90-degree bulge (tan(90/4)) from (10000,0) to (0,10000) — the true arc midpoint is NOT
        // the chord midpoint (5000,5000); it bows out from the origin.
        double bulge = System.Math.Tan(System.Math.PI / 8.0);
        model.Shapes.Add(new CurveShape
        {
            Layer = new LayerKey(1, 0),
            Xy = [10_000, 0, 0, 10_000],
            Edges =
            [
                new LayoutEdge { Kind = EdgeKind.Arc, Bulge = bulge },
                new LayoutEdge { Kind = EdgeKind.Line },
            ],
        });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(5000, 5000, 20_000, ref counters);
        var midpoints = near.Where(f => f.Kind == SnapFeatureKind.Midpoint).ToList();
        Assert.Contains(midpoints, m => m.X != 5000 || m.Y != 5000);
    }

    [Fact]
    public void Invalidate_ForcesRebuild_NewFeatureAppears()
    {
        var model = FreshModel();
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 1000, Y2 = 1000 });

        var idx1 = LayoutSnapFeatureIndex.Get(model, null);
        Assert.Same(idx1, LayoutSnapFeatureIndex.Get(model, null)); // cached, same reference

        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 50_000, Y1 = 50_000, X2 = 51_000, Y2 = 51_000 });
        LayoutSnapFeatureIndex.Invalidate(model);

        var idx2 = LayoutSnapFeatureIndex.Get(model, null);
        Assert.NotSame(idx1, idx2);
        var counters = new SnapQueryCounters();
        var near = idx2.QueryNear(50_000, 50_000, 100, ref counters);
        Assert.NotEmpty(near);
    }

    [Fact]
    public void Label_And_Bitmap_ContributeNoFeatures()
    {
        var model = FreshModel();
        model.Shapes.Add(new LabelShape { Layer = new LayerKey(1, 0), X = 1000, Y = 1000, Text = "hi", Height = 500 });
        model.Shapes.Add(new BitmapShape { Layer = new LayerKey(1, 0), X = 0, Y = 0, W = 1000, H = 1000, ImagePathRef = "x.png" });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(500, 500, 5000, ref counters);
        Assert.Empty(near);
    }

    [Fact]
    public void Via_ContributesOneCentroidFeature_AtItsCenter()
    {
        var model = FreshModel();
        model.Shapes.Add(new ViaShape { Layer = new LayerKey(1, 0), X = 2000, Y = 3000, PadSize = 1000, DrillSize = 500 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(2000, 3000, 100, ref counters);
        var only = Assert.Single(near);
        // Centroid, not CornerEndpoint: a via has no corners — X/Y IS its centre. Updated (not
        // loosened) with the owner-reported glyph fix; see LayoutViaSnapTests for the report.
        Assert.Equal(SnapFeatureKind.Centroid, only.Kind);
    }

    // ── The bounded-sweep contract (see LayoutSnapFeatureIndex.QueryNear) ──────────────────────
    //
    // Snap tolerance is a fixed SCREEN distance converted to world units, so it grows without bound
    // as the user zooms out: far enough out on a generated cell carrying a six-figure via field, the
    // tolerance covers the whole cell and EVERY feature in it qualifies. The index answers that from
    // what is near the cursor instead of from what the cell holds — the two tests below pin the two
    // halves of that claim, which have to hold together or neither is worth anything: the first that
    // the bounded answer is the SAME answer, the second that it is not paid for at full price.

    /// <summary>A deliberately independent re-implementation: every feature, filtered by tolerance,
    /// ordered by priority then distance, truncated. Nothing here consults the grid, the rings, or
    /// any bound — that is the point.</summary>
    private static (SnapFeatureKind Kind, double DistSq)[] BruteForce(
        IEnumerable<IntrinsicSnapFeature> all, long x, long y, long tol, int cap)
    {
        double DistSq(IntrinsicSnapFeature f)
        {
            double dx = f.X - x, dy = f.Y - y;
            return dx * dx + dy * dy;
        }

        var inRange = all.Where(f => DistSq(f) <= (double)tol * tol)
                         .OrderBy(f => f.Kind).ThenBy(DistSq)
                         .Select(f => (f.Kind, DistSq(f)));
        return (cap > 0 ? inRange.Take(cap) : inRange).ToArray();
    }

    private static LayoutView RandomLayout(int seed, int shapes)
    {
        var rng = new System.Random(seed);
        var model = FreshModel();
        for (int i = 0; i < shapes; i++)
        {
            var layer = new LayerKey(1 + rng.Next(3), 0);
            long x = rng.Next(-500_000, 500_000), y = rng.Next(-500_000, 500_000);
            switch (rng.Next(5))
            {
                case 0:
                    model.Shapes.Add(new RectShape { Layer = layer, X1 = x, Y1 = y, X2 = x + rng.Next(1, 40_000), Y2 = y + rng.Next(1, 40_000) });
                    break;
                case 1:
                    model.Shapes.Add(new CircleShape { Layer = layer, Cx = x, Cy = y, R = rng.Next(1, 20_000) });
                    break;
                case 2:
                    model.Shapes.Add(new ViaShape { Layer = layer, X = x, Y = y });
                    break;
                case 3:
                    model.Shapes.Add(new PolygonShape
                    {
                        Layer = layer,
                        Xy = [x, y, x + rng.Next(1, 30_000), y, x + rng.Next(1, 30_000), y + rng.Next(1, 30_000), x, y + rng.Next(1, 30_000)],
                    });
                    break;
                default:
                    model.Shapes.Add(new PathShape
                    {
                        Layer = layer, Width = 2000,
                        Xy = [x, y, x + rng.Next(-40_000, 40_000), y + rng.Next(-40_000, 40_000)],
                    });
                    break;
            }
        }
        return model;
    }

    /// <summary>The bounded sweep returns exactly what an exhaustive scan would — at every tolerance,
    /// including ones many times the cell's own extent, and from cursors inside the geometry, on its
    /// edge, and far outside it (which is where the ring bound is hardest and where a too-eager
    /// termination would silently drop the right answer).</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void QueryNear_MatchesAnExhaustiveScan_AtEveryToleranceAndCursor(int seed)
    {
        var model = RandomLayout(seed, 300);
        var idx = LayoutSnapFeatureIndex.Get(model, null);

        // The same feature list the index holds, gathered independently of it.
        var all = new List<IntrinsicSnapFeature>();
        var counters = new SnapQueryCounters();
        all.AddRange(idx.QueryNear(0, 0, long.MaxValue / 8, ref counters));
        Assert.Equal(idx.FeatureCount, all.Count);

        var rng = new System.Random(seed * 7919);
        long[] tolerances = [1, 1000, 25_000, 400_000, 5_000_000, 900_000_000];
        foreach (long tol in tolerances)
        for (int t = 0; t < 12; t++)
        {
            // A spread that reaches well past the geometry, so the cursor is sometimes nowhere near it.
            long qx = rng.Next(-2_000_000, 2_000_000), qy = rng.Next(-2_000_000, 2_000_000);

            foreach (int cap in new[] { 0, 1, 5, 64 })
            {
                var c = new SnapQueryCounters();
                var got = idx.QueryNear(qx, qy, tol, ref c, cap);
                var want = BruteForce(all, qx, qy, tol, cap);

                if (cap > 0)
                {
                    // Compared as (kind, distance) rather than identity: where several features tie on
                    // both, which one lands on the cap boundary is arbitrary in either implementation
                    // and never observable — the marker is drawn at a position, not at an identity.
                    Assert.Equal(
                        want,
                        got.Select(f => (f.Kind, (double)(f.X - qx) * (f.X - qx) + (double)(f.Y - qy) * (f.Y - qy))).ToArray());
                }
                else
                {
                    // Unbounded: order is unspecified, membership is not.
                    Assert.Equal(want.Length, got.Count);
                    Assert.Equal(
                        want.OrderBy(e => e.Kind).ThenBy(e => e.DistSq).ToArray(),
                        got.Select(f => (f.Kind, (double)(f.X - qx) * (f.X - qx) + (double)(f.Y - qy) * (f.Y - qy)))
                           .OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToArray());
                }
            }
        }
    }

    /// <summary>A tolerance that swallows the whole cell still costs a cursor-sized query. This is the
    /// regression gate for the zoomed-far-out pointer move, and it is a COUNTER rather than a clock:
    /// the defect it catches is that the scan is proportional to the cell, which shows up as an
    /// examined count in the hundreds of thousands whatever machine it runs on.</summary>
    [Fact]
    public void QueryNear_OverAWholeCellTolerance_ExaminesOnlyWhatIsNearTheCursor()
    {
        var model = FreshModel();
        // A dense uniform field, the shape a generated capacitor's via array actually takes.
        for (int i = 0; i < 100; i++)
        for (int j = 0; j < 100; j++)
            model.Shapes.Add(new RectShape
            {
                Layer = new LayerKey(1, 0),
                X1 = i * 4000, Y1 = j * 4000, X2 = i * 4000 + 2000, Y2 = j * 4000 + 2000,
            });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        Assert.Equal(90_000, idx.FeatureCount);   // 10,000 rects x (4 corners + 4 midpoints + centroid)

        // Ten times the field's own extent — the cursor is inside it, and every one of the 90,000
        // features is within tolerance.
        long tol = 4_000_000;

        foreach (var (qx, qy) in new[] { (200_000L, 200_000L), (0L, 0L), (399_000L, 1000L), (-3_000_000L, 200_000L) })
        {
            var counters = new SnapQueryCounters();
            var got = idx.QueryNear(qx, qy, tol, ref counters, cap: 64);

            Assert.Equal(64, got.Count);
            // Two orders below the feature count. The unbounded scan this replaced examined all 90,000
            // every time, and did so once per placement of the cell per pointer move.
            Assert.True(counters.FeaturesExamined < 9_000,
                $"examined {counters.FeaturesExamined} of {idx.FeatureCount} features at ({qx},{qy})");
        }
    }

    [Fact]
    public void QueryNear_OnlyExaminesNearbyFeatures_NotEveryShapeInTheView()
    {
        var model = FreshModel();
        // A cluster of shapes far from the query point, plus one very close.
        for (int i = 0; i < 500; i++)
            model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 1_000_000 + i * 2000, Y1 = 1_000_000, X2 = 1_000_000 + i * 2000 + 500, Y2 = 1_000_500 });
        model.Shapes.Add(new RectShape { Layer = new LayerKey(1, 0), X1 = 0, Y1 = 0, X2 = 100, Y2 = 100 });

        var idx = LayoutSnapFeatureIndex.Get(model, null);
        var counters = new SnapQueryCounters();
        var near = idx.QueryNear(0, 0, 200, ref counters);

        Assert.Contains(near, f => f.Kind == SnapFeatureKind.CornerEndpoint && f.X == 0 && f.Y == 0);
        // Far cluster (500 shapes x 9 features each = 4500) must not all be examined for a query
        // nowhere near them — bounded well under the total feature count.
        Assert.True(counters.FeaturesExamined < 200);
    }
}
