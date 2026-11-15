// ICF-5 R-icf5-3 — an array of identical via cuts becomes one region.
//
// A kit capacitor's plate connection is drawn as tens of small cuts on one via layer, and each cut keeps its own outline
// on the way to the mesher: every cut edge becomes a gridline on every level, and the local grid cannot coarsen a block
// a cut covers partly. A 20 × 20 µm MIM with a 6 × 6 array meshed at 3,382 unknowns as drawn and 1,819 merged
// (src/Engine/Mom/RESOLVED.md §ICF-5). Merged, the array is its bounding rectangle with the via's conductivity scaled
// by the cut fill factor, so σ·A — and with it the DC resistance through the array — is exactly what the cuts carried.
//
// What counts as an array is narrow on purpose: axis-aligned rectangles of one size, each within twice its own size of
// another, at least four of them, filling at least MinFill of their hull, and no other footprint of the same via inside
// that hull (a second via record over the hull would double the metal there).

using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

public static class PlanarViaArrayMerge
{
    /// <summary>The smallest fraction of its bounding rectangle an array's cuts must fill to be merged.</summary>
    public const double MinFill = 0.2;

    /// <summary>The fewest cuts that make an array.</summary>
    public const int MinCuts = 4;

    /// <summary>One merged array: the hull that replaces it, how many cuts it held and the fill factor σ is scaled by.</summary>
    public sealed record Merged(PlanarPolygon Hull, int Cuts, double Fill);

    /// <summary>
    /// Splits one via record's footprints into merged arrays and the footprints left as drawn (in their original order).
    /// Nothing qualifies ⇒ no arrays and the input list unchanged.
    /// </summary>
    public static (IReadOnlyList<Merged> Arrays, List<PlanarPolygon> Remaining) Split(IReadOnlyList<PlanarPolygon> footprints)
    {
        int n = footprints.Count;
        var box = new (double X0, double Y0, double X1, double Y1)?[n];
        for (int i = 0; i < n; i++) box[i] = AsRect(footprints[i]);

        // Union-find over rectangles of one size that sit within twice their size of each other.
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < n; i++)
        {
            if (box[i] is not { } a) continue;
            double w = a.X1 - a.X0, h = a.Y1 - a.Y0, reach = 2.0 * Math.Max(w, h), tol = 1e-3 * Math.Min(w, h);
            for (int j = i + 1; j < n; j++)
            {
                if (box[j] is not { } b) continue;
                if (Math.Abs((b.X1 - b.X0) - w) > tol || Math.Abs((b.Y1 - b.Y0) - h) > tol) continue;
                double gx = Math.Max(0, Math.Max(b.X0 - a.X1, a.X0 - b.X1));
                double gy = Math.Max(0, Math.Max(b.Y0 - a.Y1, a.Y0 - b.Y1));
                if (gx <= reach && gy <= reach) parent[Find(j)] = Find(i);
            }
        }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            if (box[i] is null) continue;
            int r = Find(i);
            if (!groups.TryGetValue(r, out var g)) groups[r] = g = [];
            g.Add(i);
        }

        var merged = new List<Merged>();
        var taken = new bool[n];
        foreach (var g in groups.Values.OrderBy(g => g[0]))
        {
            if (g.Count < MinCuts) continue;
            double x0 = g.Min(i => box[i]!.Value.X0), y0 = g.Min(i => box[i]!.Value.Y0);
            double x1 = g.Max(i => box[i]!.Value.X1), y1 = g.Max(i => box[i]!.Value.Y1);
            double hullArea = (x1 - x0) * (y1 - y0);
            double cutArea = g.Sum(i => (box[i]!.Value.X1 - box[i]!.Value.X0) * (box[i]!.Value.Y1 - box[i]!.Value.Y0));
            double fill = cutArea / hullArea;
            if (!(fill >= MinFill) || fill > 1.0 + 1e-9) continue;   // > 1: the cuts overlap, so the area sum is not the metal

            // Another footprint of this via reaching into the hull would be covered twice once the hull is a record of
            // its own.
            var inGroup = new HashSet<int>(g);
            bool clash = false;
            for (int k = 0; k < n && !clash; k++)
            {
                if (inGroup.Contains(k) || taken[k]) continue;
                var (bx0, by0, bx1, by1) = box[k] ?? Bounds(footprints[k]);
                clash = bx0 < x1 && bx1 > x0 && by0 < y1 && by1 > y0;
            }
            if (clash) continue;

            foreach (int i in g) taken[i] = true;
            merged.Add(new Merged(
                new PlanarPolygon([new EmPoint(x0, y0), new EmPoint(x1, y0), new EmPoint(x1, y1), new EmPoint(x0, y1)]),
                g.Count, Math.Min(fill, 1.0)));
        }

        if (merged.Count == 0) return ([], [.. footprints]);
        var rest = new List<PlanarPolygon>();
        for (int i = 0; i < n; i++) if (!taken[i]) rest.Add(footprints[i]);
        return (merged, rest);
    }

    /// <summary>The rectangle a footprint is, or null when it is not an axis-aligned rectangle with no holes.</summary>
    private static (double X0, double Y0, double X1, double Y1)? AsRect(PlanarPolygon p)
    {
        if (p.HoleRings.Count > 0) return null;
        var pts = p.Outer;
        int m = pts.Count;
        if (m >= 5 && pts[0].X == pts[m - 1].X && pts[0].Y == pts[m - 1].Y) m--;
        if (m != 4) return null;
        var (x0, y0, x1, y1) = Bounds(p);
        if (!(x1 > x0) || !(y1 > y0)) return null;
        for (int i = 0; i < 4; i++)
        {
            var q = pts[i];
            if ((q.X != x0 && q.X != x1) || (q.Y != y0 && q.Y != y1)) return null;
        }
        return (x0, y0, x1, y1);
    }

    private static (double X0, double Y0, double X1, double Y1) Bounds(PlanarPolygon p)
        => (p.Outer.Min(q => q.X), p.Outer.Min(q => q.Y), p.Outer.Max(q => q.X), p.Outer.Max(q => q.Y));
}
