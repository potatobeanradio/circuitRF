// `LayoutClipper.HolesAreValid` gained two bounding-box prefilters (2026-09-04) because reading a
// Gerber-imported board back was dominated by it — see src/Design/RESOLVED.md for the measurement.
//
// A prefilter is only ever allowed to skip work that provably cannot change the answer, so the gate
// is DIFFERENTIAL: `BruteForce` below is the pre-change algorithm, verbatim and unfiltered, and every
// case here asserts the two agree. That is the only assertion worth making — a prefilter tested
// against hand-picked expectations passes for exactly the cases someone thought of, which is never
// the case that breaks it.
//
// The corpus is deliberately full of the shapes a box reject has to get right: holes that touch each
// other, holes that touch the outer ring, holes sharing a bounding box but not a point, rings with
// duplicated vertices, and empty rings.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class LayoutClipperHoleValidityTests
{
    // ── The reference: the algorithm as it stood before the prefilters ──────────────────────────

    private static bool BruteForce(IReadOnlyList<long[]> rings)
    {
        var outer = rings[0];
        for (int i = 1; i < rings.Count; i++)
        {
            var hole = rings[i];
            for (int k = 0; k < hole.Length; k += 2)
                if (!PointInOrOnRing(outer, hole[k], hole[k + 1])) return false;
            if (RingsIntersect(hole, outer)) return false;

            for (int j = i + 1; j < rings.Count; j++)
                if (RingsIntersect(hole, rings[j])) return false;
        }
        return true;
    }

    private static bool PointInOrOnRing(long[] ring, long px, long py)
    {
        int n = ring.Length / 2;
        if (n < 3) return false;
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1];
            double xj = ring[2 * j], yj = ring[2 * j + 1];
            if (OnSegment(px, py, xi, yi, xj, yj)) return true;
            bool crosses = (yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi;
            if (crosses) inside = !inside;
        }
        return inside;
    }

    private static bool OnSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        double cross = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        if (Math.Abs(cross) > 1e-6) return false;
        double dot = (px - ax) * (bx - ax) + (py - ay) * (by - ay);
        double lenSq = (bx - ax) * (bx - ax) + (by - ay) * (by - ay);
        return dot >= 0 && dot <= lenSq;
    }

    private static bool RingsIntersect(long[] a, long[] b)
    {
        int na = a.Length / 2, nb = b.Length / 2;
        for (int i = 0; i < na; i++)
        {
            var (ax0, ay0, ax1, ay1) = Segment(a, i, na);
            for (int j = 0; j < nb; j++)
            {
                var (bx0, by0, bx1, by1) = Segment(b, j, nb);
                if (SegmentsIntersect(ax0, ay0, ax1, ay1, bx0, by0, bx1, by1)) return true;
            }
        }
        return false;
    }

    private static (double, double, double, double) Segment(long[] xy, int i, int n)
    {
        int j = (i + 1) % n;
        return (xy[2 * i], xy[2 * i + 1], xy[2 * j], xy[2 * j + 1]);
    }

    private static bool SegmentsIntersect(
        double ax0, double ay0, double ax1, double ay1,
        double bx0, double by0, double bx1, double by1)
    {
        double d1 = Cross(bx1 - bx0, by1 - by0, ax0 - bx0, ay0 - by0);
        double d2 = Cross(bx1 - bx0, by1 - by0, ax1 - bx0, ay1 - by0);
        double d3 = Cross(ax1 - ax0, ay1 - ay0, bx0 - ax0, by0 - ay0);
        double d4 = Cross(ax1 - ax0, ay1 - ay0, bx1 - ax0, by1 - ay0);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            return true;

        if (d1 == 0 && OnSegment(ax0, ay0, bx0, by0, bx1, by1)) return true;
        if (d2 == 0 && OnSegment(ax1, ay1, bx0, by0, bx1, by1)) return true;
        if (d3 == 0 && OnSegment(bx0, by0, ax0, ay0, ax1, ay1)) return true;
        if (d4 == 0 && OnSegment(bx1, by1, ax0, ay0, ax1, ay1)) return true;
        return false;
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    // ── Ring builders ───────────────────────────────────────────────────────────────────────────

    private static long[] Rect(long x, long y, long w, long h)
        => [x, y, x + w, y, x + w, y + h, x, y + h];

    /// <summary>An n-gon, so a ring can carry hundreds of vertices — the box prefilters only start
    /// mattering at that size, and a corpus of 4-vertex rectangles would exercise none of them.</summary>
    private static long[] Ngon(long cx, long cy, long r, int n, double phase = 0)
    {
        var xy = new long[n * 2];
        for (int i = 0; i < n; i++)
        {
            double t = phase + 2 * Math.PI * i / n;
            xy[2 * i]     = cx + (long)Math.Round(r * Math.Cos(t));
            xy[2 * i + 1] = cy + (long)Math.Round(r * Math.Sin(t));
        }
        return xy;
    }

    private static void AssertAgrees(params long[][] rings)
    {
        bool expected = BruteForce(rings);
        bool actual   = LayoutClipper.HolesAreValid(rings);
        Assert.True(expected == actual,
            $"prefiltered={actual}, unfiltered={expected} for {rings.Length} ring(s) of " +
            string.Join("/", rings.Select(r => r.Length / 2)) + " vertices");
    }

    // ── The cases a box reject has to get right ─────────────────────────────────────────────────

    [Fact]
    public void OrdinaryHolesInsideAnOuterRing_AgreeWithTheUnfilteredAlgorithm()
        => AssertAgrees(Rect(0, 0, 1000, 1000), Rect(100, 100, 100, 100), Rect(600, 600, 100, 100));

    [Fact]
    public void HolesWhoseBoxesOverLAPButWhoseEdgesDoNot_AreStillValid()
        // Two L-shaped-by-position holes: their boxes overlap, so the pair survives the reject and
        // goes to the real test — which is the case the reject must NOT be allowed to answer.
        => AssertAgrees(Rect(0, 0, 1000, 1000), Rect(100, 100, 300, 50), Rect(300, 300, 50, 300));

    [Fact]
    public void HolesThatTOUCHAlongAnEdge_AreInvalid_UnderBothAlgorithms()
        // Touching counts as intersecting (R10b is conservative here) and the boxes touch too — so
        // this is the case that would break an exclusive box test.
        => AssertAgrees(Rect(0, 0, 1000, 1000), Rect(100, 100, 100, 100), Rect(200, 100, 100, 100));

    [Fact]
    public void AHoleTouchingTheOuterRing_IsInvalid_UnderBothAlgorithms()
        => AssertAgrees(Rect(0, 0, 1000, 1000), Rect(0, 100, 100, 100));

    [Fact]
    public void AHoleEscapingTheOuterRing_IsInvalid_UnderBothAlgorithms()
        => AssertAgrees(Rect(0, 0, 1000, 1000), Rect(900, 100, 300, 100));

    [Fact]
    public void AHoleWithADuplicatedVertex_AgreesWithTheUnfilteredAlgorithm()
        // A degenerate segment makes OnSegment answer true for every point, which is a quirk of the
        // original and is preserved deliberately — this is where a careless gate would change it.
        => AssertAgrees(Rect(0, 0, 1000, 1000), [100, 100, 200, 100, 200, 100, 200, 200, 100, 200]);

    [Fact]
    public void AnEmptyHoleRing_AgreesWithTheUnfilteredAlgorithm()
        // BoundsOf reports MaxX < MinX for it, which must read as "overlaps nothing" rather than as
        // a box centred on the origin.
        => AssertAgrees(Rect(0, 0, 1000, 1000), [], Rect(100, 100, 100, 100));

    [Fact]
    public void ADegenerateOuterRing_AgreesWithTheUnfilteredAlgorithm()
        => AssertAgrees([0, 0, 1000, 0], Rect(100, 100, 100, 100));

    [Fact]
    public void AManySidedOuterRingWithManyHoles_AgreesWithTheUnfilteredAlgorithm()
    {
        // The shape of the thing that motivated this: one big ring, many small holes, none touching.
        var rings = new List<long[]> { Ngon(0, 0, 100_000, 400) };
        for (int i = 0; i < 60; i++)
            rings.Add(Ngon(-60_000 + (i % 10) * 13_000, -30_000 + (i / 10) * 13_000, 2_000, 24));
        AssertAgrees([.. rings]);
    }

    // ── And the same over a randomized corpus ───────────────────────────────────────────────────

    [Fact]
    public void OverARandomizedCorpus_ThePrefilteredAndUnfilteredAlgorithmsNeverDisagree()
    {
        // Fixed seed: this is a differential check, and a corpus that changes run to run turns a
        // reproducible failure into a rumour.
        var rng = new Random(20260904);
        int valid = 0, invalid = 0;

        for (int trial = 0; trial < 3_000; trial++)
        {
            var rings = new List<long[]>
            {
                rng.Next(2) == 0 ? Rect(0, 0, 10_000, 10_000) : Ngon(5_000, 5_000, 5_000, rng.Next(8, 64)),
            };

            // Half the trials lay their holes out ADVERSARIALLY — a coarse lattice that produces the
            // exactly touching, exactly collinear and escaping-the-outer-ring configurations a box
            // reject has to survive. The other half places them in distinct cells of an interior
            // grid, which is what an ordinary pour looks like and is the only way the ACCEPT path
            // gets exercised: an all-adversarial corpus answers "invalid" to almost everything and
            // would pass this whole file while testing one branch.
            bool adversarial = rng.Next(2) == 0;
            int holes = rng.Next(1, 9);
            var takenCells = new HashSet<int>();

            for (int h = 0; h < holes; h++)
            {
                long x, y, w;
                if (adversarial)
                {
                    x = rng.Next(-2, 12) * 1_000;
                    y = rng.Next(-2, 12) * 1_000;
                    w = rng.Next(1, 4) * 1_000;
                }
                else
                {
                    int cell = rng.Next(16);
                    if (!takenCells.Add(cell)) continue;      // one hole per cell keeps them disjoint
                    x = 1_500 + cell % 4 * 2_000;
                    y = 1_500 + cell / 4 * 2_000;
                    w = 1_000;
                }

                // A duplicated vertex is rare on purpose. It is the case that turned out to matter —
                // a zero-length segment reports as meeting everything — but at one hole in four it
                // made almost every trial invalid and starved the accept path.
                rings.Add(rng.Next(8) switch
                {
                    0 => [x, y, x + w, y, x + w, y, x + w, y + w],      // a duplicated vertex
                    1 or 2 => Ngon(x + w / 2, y + w / 2, w / 2, rng.Next(3, 20)),
                    3 or 4 => Rect(x, y, w, adversarial ? rng.Next(1, 4) * 1_000 : w),
                    _ => Rect(x, y, w, w),
                });
            }

            bool expected = BruteForce(rings);
            bool actual   = LayoutClipper.HolesAreValid(rings);
            Assert.True(expected == actual,
                $"trial {trial}: prefiltered={actual}, unfiltered={expected}, rings=" +
                string.Join(" | ", rings.Select(r => string.Join(",", r))));

            if (expected) valid++; else invalid++;
        }

        // A corpus that is all one answer proves nothing about the other, so the split is asserted
        // rather than assumed — 3,000 trials that happened to all be invalid would pass everything
        // above while exercising none of the accept path.
        Assert.True(valid   > 200, $"only {valid} valid cases — the corpus stopped covering the accept path");
        Assert.True(invalid > 200, $"only {invalid} invalid cases — the corpus stopped covering the reject path");
    }
}
