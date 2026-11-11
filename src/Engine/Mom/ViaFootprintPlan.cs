// PMS-1 R-pms1-5 — which grid cells each via footprint polygon can possibly cover, as index ranges.
//
// The vertical-basis scan used to test every cell of the shared grid against every polygon of every via group:
// O(groups × nx·ny × polygons). On a medium MMIC (a 1.4 mm chip, ~800 lines per axis, a few hundred via polygons)
// that is seconds to minutes of a mesh whose every other stage takes a second. A polygon contains a cell centre only
// inside its own bounding box — the even-odd test counts no crossing for a point outside it — so restricting each
// polygon to its box's index range finds exactly the same cells, and the scan becomes the footprints' own area.
//
// The ranges are over cell CENTRES, computed with the scan's own expression (0.5·(g[i] + g[i+1])) and compared
// inclusively, so a centre exactly on a box edge is still tested — and the test, not the plan, decides it.

namespace CircuitRF.Engine.Mom;

/// <summary>One polygon's candidate cells: inclusive index ranges on the grid (empty when <c>X0 &gt; X1</c> or
/// <c>Y0 &gt; Y1</c>).</summary>
internal readonly record struct ViaFootprintBox(int Polygon, int X0, int X1, int Y0, int Y1);

/// <summary>One via group's polygons and the union of their row ranges (<c>Y0 &gt; Y1</c> when nothing is on the grid).</summary>
internal sealed record ViaFootprintRows(IReadOnlyList<ViaFootprintBox> Boxes, int Y0, int Y1)
{
    public int RowCount => Y1 >= Y0 ? Y1 - Y0 + 1 : 0;
}

/// <summary>
/// <b>PMS-1 — the via scan's plan</b>: per via group, each polygon's bounding box as grid index ranges, and the rows
/// the group spans. <see cref="Rows"/> is what the mesher's scan stage declares for the via half.
/// </summary>
internal sealed record ViaFootprintPlan(IReadOnlyList<ViaFootprintRows> Vias, long Rows)
{
    public static ViaFootprintPlan For(IReadOnlyList<PlanarVia> vias, double[] gx, double[] gy)
    {
        int nx = gx.Length - 1, ny = gy.Length - 1;
        var list = new List<ViaFootprintRows>(vias.Count);
        long rows = 0;
        foreach (var via in vias)
        {
            var boxes = new List<ViaFootprintBox>(via.Polygons.Count);
            int y0 = int.MaxValue, y1 = -1;
            for (int p = 0; p < via.Polygons.Count; p++)
            {
                var (minX, minY, maxX, maxY) = via.Polygons[p].Bounds();
                int bx0 = FirstCentreAtOrAbove(gx, nx, minX), bx1 = LastCentreAtOrBelow(gx, nx, maxX);
                int by0 = FirstCentreAtOrAbove(gy, ny, minY), by1 = LastCentreAtOrBelow(gy, ny, maxY);
                if (bx0 > bx1 || by0 > by1) continue;
                boxes.Add(new ViaFootprintBox(p, bx0, bx1, by0, by1));
                if (by0 < y0) y0 = by0;
                if (by1 > y1) y1 = by1;
            }
            var group = new ViaFootprintRows(boxes, boxes.Count > 0 ? y0 : 0, boxes.Count > 0 ? y1 : -1);
            rows += group.RowCount;
            list.Add(group);
        }
        return new ViaFootprintPlan(list, rows);
    }

    private static double Centre(double[] g, int i) => 0.5 * (g[i] + g[i + 1]);

    /// <summary>The first cell whose centre is ≥ <paramref name="v"/>, or <paramref name="n"/> when none is.</summary>
    private static int FirstCentreAtOrAbove(double[] g, int n, double v)
    {
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Centre(g, mid) >= v) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>The last cell whose centre is ≤ <paramref name="v"/>, or −1 when none is.</summary>
    private static int LastCentreAtOrBelow(double[] g, int n, double v)
    {
        int lo = 0, hi = n;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Centre(g, mid) > v) hi = mid; else lo = mid + 1;
        }
        return lo - 1;
    }
}
