// ================================================================
//  ContourExtractor.cs  —  Marching-squares iso-line extraction
//
//  Turns a row-major SurfaceGrid (from LoadpullSurface.Resample)
//  into ordered polylines in world coordinates at a set of iso-levels.
//
//  Grid convention: idx = yi*res + xi (row=y/im, col=x/re) — must
//  match Resample exactly; a transpose bug silently rotates contours.
//
//  Saddle disambiguation (cases 5 & 10): centre-average rule —
//  if the cell-centre average >= level, the two "above" corners
//  are connected through the centre and the contour encircles the
//  two "below" corners as separate islands; otherwise vice versa.
//
//  Firewall: pure RfCore — no Skia, no Avalonia, no colour.
// ================================================================

using System;
using System.Collections.Generic;

namespace RfCore.Loadpull
{
    // ----------------------------------------------------------------
    //  Public types
    // ----------------------------------------------------------------

    /// <summary>
    /// One iso-contour polyline at a given level in world coordinates.
    /// <c>Closed=true</c> for interior rings; <c>false</c> for chains
    /// that terminate at the Γ-disk boundary (NaN cells) or grid edge.
    /// </summary>
    public readonly record struct IsoPolyline(
        double Level,
        IReadOnlyList<(double X, double Y)> Points,
        bool Closed);

    /// <summary>Explicit set of iso-levels for contour extraction.</summary>
    public sealed record ContourLevelSet(double[] Levels);

    // ----------------------------------------------------------------
    //  ContourExtractor
    // ----------------------------------------------------------------

    public static class ContourExtractor
    {
        // 16-case marching-squares lookup.
        // Corners per cell:  c0=lower-left (xi,yi),   c1=lower-right (xi+1,yi),
        //                    c2=upper-right (xi+1,yi+1), c3=upper-left (xi,yi+1).
        // Edges per cell:    0=bottom (c0→c1), 1=right (c1→c2),
        //                    2=top    (c2→c3), 3=left  (c3→c0).
        // Bit encoding for case index: bit0=c0, bit1=c1, bit2=c2, bit3=c3
        //   (1 = value >= level).
        // null → no segment (cases 0 and 15) OR saddle (cases 5 and 10).
        private static readonly (int A, int B)?[] s_cases = new (int A, int B)?[16]
        {
            /* 0  0000 */ null,
            /* 1  0001 */ (0, 3),  // c0 above: bottom + left
            /* 2  0010 */ (0, 1),  // c1 above: bottom + right
            /* 3  0011 */ (1, 3),  // c0+c1 above: right + left
            /* 4  0100 */ (1, 2),  // c2 above: right + top
            /* 5  0101 */ null,    // saddle — c0 & c2 above, c1 & c3 below
            /* 6  0110 */ (0, 2),  // c1+c2 above: bottom + top
            /* 7  0111 */ (2, 3),  // c0+c1+c2 above: top + left (c3 isolated below)
            /* 8  1000 */ (2, 3),  // c3 above: top + left
            /* 9  1001 */ (0, 2),  // c0+c3 above: bottom + top
            /* 10 1010 */ null,    // saddle — c1 & c3 above, c0 & c2 below
            /* 11 1011 */ (1, 2),  // c0+c1+c3 above: right + top (c2 isolated below)
            /* 12 1100 */ (1, 3),  // c2+c3 above: right + left
            /* 13 1101 */ (0, 1),  // c0+c2+c3 above: bottom + right (c1 isolated below)
            /* 14 1110 */ (0, 3),  // c1+c2+c3 above: bottom + left (c0 isolated below)
            /* 15 1111 */ null,
        };

        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Extract iso-polylines for every level in <paramref name="levels"/>.
        /// NaN cells (outside the Γ-disk) are skipped, producing open chains
        /// at the disk boundary — correct for Smith-substrate contours.
        /// Output is in world coordinates (XSpace/YSpace values).
        /// </summary>
        public static IReadOnlyList<IsoPolyline> Extract(SurfaceGrid grid, ContourLevelSet levels)
        {
            int res    = grid.XSpace.Length;
            // Vertical-edge key offset: ensures h(xi,yi) keys never collide with v(xi,yi) keys.
            int vOff   = res * res;
            var result = new List<IsoPolyline>();

            foreach (double level in levels.Levels)
            {
                // ── Phase 1: canonical per-edge crossing points ──────────────────
                //
                // Every edge is identified by a canonical integer key:
                //   h(xi, yi)  = yi*res + xi               (horizontal, left→right)
                //   v(xi, yi)  = vOff + xi*(res-1) + yi    (vertical, bottom→top)
                //
                // Two adjacent cells share an edge; using the canonical key and a
                // fixed interpolation direction guarantees bit-identical crossing
                // points from both cells, so the stitcher can use exact int keys.

                var crossings = new Dictionary<int, (double X, double Y)>();

                // Horizontal edges h(xi, yi) for xi in [0, res-2], yi in [0, res-1]
                for (int yi = 0; yi < res; yi++)
                {
                    for (int xi = 0; xi < res - 1; xi++)
                    {
                        double vL = grid.Values[yi * res + xi];
                        double vR = grid.Values[yi * res + xi + 1];
                        if (!double.IsNaN(vL) && !double.IsNaN(vR) && Crosses(vL, vR, level))
                        {
                            double t = (level - vL) / (vR - vL);
                            double x = grid.XSpace[xi] + t * (grid.XSpace[xi + 1] - grid.XSpace[xi]);
                            crossings[yi * res + xi] = (x, grid.YSpace[yi]);
                        }
                    }
                }

                // Vertical edges v(xi, yi) for xi in [0, res-1], yi in [0, res-2]
                for (int xi = 0; xi < res; xi++)
                {
                    for (int yi = 0; yi < res - 1; yi++)
                    {
                        double vB = grid.Values[yi * res + xi];
                        double vT = grid.Values[(yi + 1) * res + xi];
                        if (!double.IsNaN(vB) && !double.IsNaN(vT) && Crosses(vB, vT, level))
                        {
                            double t = (level - vB) / (vT - vB);
                            double y = grid.YSpace[yi] + t * (grid.YSpace[yi + 1] - grid.YSpace[yi]);
                            crossings[vOff + xi * (res - 1) + yi] = (grid.XSpace[xi], y);
                        }
                    }
                }

                // ── Phase 2: per-cell marching squares → segment pairs ───────────
                var segments = new List<(int EA, int EB)>();

                for (int yi = 0; yi < res - 1; yi++)
                {
                    for (int xi = 0; xi < res - 1; xi++)
                    {
                        double c0 = grid.Values[yi * res + xi];
                        double c1 = grid.Values[yi * res + xi + 1];
                        double c2 = grid.Values[(yi + 1) * res + xi + 1];
                        double c3 = grid.Values[(yi + 1) * res + xi];

                        // Skip cells with any NaN corner (Γ-disk boundary)
                        if (double.IsNaN(c0) || double.IsNaN(c1) ||
                            double.IsNaN(c2) || double.IsNaN(c3))
                            continue;

                        // Canonical edge keys for this cell's four edges
                        int eBot   = yi * res + xi;                      // h(xi,  yi)
                        int eRight = vOff + (xi + 1) * (res - 1) + yi;  // v(xi+1, yi)
                        int eTop   = (yi + 1) * res + xi;                // h(xi,  yi+1)
                        int eLeft  = vOff + xi * (res - 1) + yi;        // v(xi,  yi)

                        int idx = (c0 >= level ? 1 : 0)
                                | (c1 >= level ? 2 : 0)
                                | (c2 >= level ? 4 : 0)
                                | (c3 >= level ? 8 : 0);

                        if (idx == 5)
                        {
                            // Saddle: c0 & c2 above, c1 & c3 below.
                            // Centre ≥ level → c0/c2 connected; contour isolates c1 and c3.
                            // Centre <  level → c0 and c2 are separate islands.
                            double ctr = (c0 + c1 + c2 + c3) * 0.25;
                            if (ctr >= level)
                            {
                                TryEmit(segments, crossings, eBot,   eRight);
                                TryEmit(segments, crossings, eLeft,  eTop);
                            }
                            else
                            {
                                TryEmit(segments, crossings, eBot,   eLeft);
                                TryEmit(segments, crossings, eRight, eTop);
                            }
                        }
                        else if (idx == 10)
                        {
                            // Saddle: c1 & c3 above, c0 & c2 below.
                            // Centre ≥ level → c1/c3 connected; contour isolates c0 and c2.
                            // Centre <  level → c1 and c3 are separate islands.
                            double ctr = (c0 + c1 + c2 + c3) * 0.25;
                            if (ctr >= level)
                            {
                                TryEmit(segments, crossings, eBot,   eLeft);
                                TryEmit(segments, crossings, eRight, eTop);
                            }
                            else
                            {
                                TryEmit(segments, crossings, eBot,   eRight);
                                TryEmit(segments, crossings, eLeft,  eTop);
                            }
                        }
                        else if (s_cases[idx] is (int a, int b))
                        {
                            int[] ek = { eBot, eRight, eTop, eLeft };
                            TryEmit(segments, crossings, ek[a], ek[b]);
                        }
                    }
                }

                // ── Phase 3: stitch segments into ordered polylines ──────────────
                result.AddRange(Stitch(level, segments, crossings));
            }

            return result;
        }

        /// <summary>
        /// Build N evenly-spaced levels spanning the grid's finite min and max.
        /// </summary>
        public static ContourLevelSet LevelsBetween(SurfaceGrid grid, int n)
        {
            FiniteRange(grid.Values, out double min, out double max);
            if (min > max) return new ContourLevelSet(Array.Empty<double>());
            if (n <= 1)    return new ContourLevelSet(new[] { (min + max) * 0.5 });
            var levels = new double[n];
            for (int i = 0; i < n; i++)
                levels[i] = min + (max - min) * i / (n - 1);
            return new ContourLevelSet(levels);
        }

        /// <summary>
        /// Build levels at every multiple of <paramref name="step"/> from
        /// <paramref name="anchor"/> that falls within the grid's finite range.
        /// </summary>
        public static ContourLevelSet LevelsByStep(SurfaceGrid grid, double step, double anchor = 0.0)
        {
            FiniteRange(grid.Values, out double min, out double max);
            if (min > max) return new ContourLevelSet(Array.Empty<double>());
            double first = anchor + Math.Ceiling((min - anchor) / step) * step;
            var levels = new List<double>();
            for (double lv = first; lv <= max + step * 1e-9; lv += step)
                levels.Add(lv);
            return new ContourLevelSet(levels.ToArray());
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        private static bool Crosses(double v0, double v1, double level)
            => (v0 < level) != (v1 < level);

        private static void TryEmit(
            List<(int EA, int EB)>                segs,
            Dictionary<int, (double X, double Y)> crossings,
            int eA, int eB)
        {
            if (crossings.ContainsKey(eA) && crossings.ContainsKey(eB))
                segs.Add((eA, eB));
        }

        private static void FiniteRange(double[] values, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            foreach (double v in values)
            {
                if (double.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        // Stitch a flat list of (edgeKeyA, edgeKeyB) segments into polylines.
        private static IEnumerable<IsoPolyline> Stitch(
            double level,
            List<(int EA, int EB)>                segs,
            Dictionary<int, (double X, double Y)> crossings)
        {
            if (segs.Count == 0) yield break;

            // Build edge-key → segment-index adjacency list
            var adj = new Dictionary<int, List<int>>(segs.Count * 2);
            for (int i = 0; i < segs.Count; i++)
            {
                var (eA, eB) = segs[i];
                if (!adj.TryGetValue(eA, out var la)) adj[eA] = la = new List<int>(2);
                if (!adj.TryGetValue(eB, out var lb)) adj[eB] = lb = new List<int>(2);
                la.Add(i);
                lb.Add(i);
            }

            var visited = new bool[segs.Count];

            for (int startIdx = 0; startIdx < segs.Count; startIdx++)
            {
                if (visited[startIdx]) continue;
                visited[startIdx] = true;

                // Build ordered edge-key chain
                var chain = new LinkedList<int>();
                chain.AddFirst(segs[startIdx].EA);
                chain.AddLast(segs[startIdx].EB);

                bool closed = false;

                // Extend forward from chain tail
                while (true)
                {
                    int tail = chain.Last!.Value;
                    int? nxt = NextUnvisited(adj, visited, tail);
                    if (nxt == null) break;
                    visited[nxt.Value] = true;
                    int other = OtherEnd(segs[nxt.Value], tail);
                    if (other == chain.First!.Value) { closed = true; break; }
                    chain.AddLast(other);
                }

                // Extend backward from chain head (only when not already closed)
                if (!closed)
                {
                    while (true)
                    {
                        int head = chain.First!.Value;
                        int? nxt = NextUnvisited(adj, visited, head);
                        if (nxt == null) break;
                        visited[nxt.Value] = true;
                        chain.AddFirst(OtherEnd(segs[nxt.Value], head));
                    }
                }

                // Materialise points from edge keys
                var pts = new List<(double X, double Y)>(chain.Count);
                foreach (int key in chain)
                    if (crossings.TryGetValue(key, out var pt))
                        pts.Add(pt);

                if (pts.Count >= 2)
                    yield return new IsoPolyline(level, pts, closed);
            }
        }

        private static int? NextUnvisited(Dictionary<int, List<int>> adj, bool[] visited, int edgeKey)
        {
            if (!adj.TryGetValue(edgeKey, out var list)) return null;
            foreach (int i in list)
                if (!visited[i]) return i;
            return null;
        }

        private static int OtherEnd((int EA, int EB) seg, int known)
            => seg.EA == known ? seg.EB : seg.EA;
    }
}
