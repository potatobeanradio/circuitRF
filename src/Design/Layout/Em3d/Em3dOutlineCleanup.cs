// brief-dfr28-3 — Robust meshing (EmSetup.RobustMeshing): a layout outline's points that lie within a fraction of a
// micrometre of the line through their neighbours are removed before a 3D run meshes it.
//
// WHY. Imported artwork hands the mesher features far below anything it can resolve: arcs flattened into ~7 µm segments
// that end in a 1.4 µm stub where they meet a straight edge, 0.1 µm and 0.4 µm edges left where draws were merged. On the
// TP149 board three consecutive points of such an arc, all in the board's top plane, became the corners of a FLAT
// tetrahedron (Gmsh's boundary recovery, which its optimiser cannot undo with all four nodes on the boundary); its NaN
// reached every unknown and EMerge wrote S = −I. Removing every point whose removal moves the outline by under 0.5 µm
// took 42 of the board's 3,379 points, its shortest edge went from 0.1 µm to 5 µm, and the flat element was gone.
//
// THE GUARANTEE. A point is removed only when EVERY original point the merged segment now stands for lies within the
// tolerance of it, so no part of an outline moves by more than the tolerance however many neighbours go; Stats.MaxMoveM is
// that distance measured on the result, not an estimate. A point of a corner is never near its neighbours' line, so a
// rectangle, a trace's end (where a port sits) and a cut along the board edge keep every point they have.
//
// ACROSS OBJECTS (brief-dfr31-6). A ring cleaned on its own can be pulled off a line it shares with ANOTHER object. On a
// cropped Gerber board an inner plane's side lay exactly on the board edge for 492 µm, with a 0.109 µm jog at each end
// where the crop met the artwork's own edge; removing the on-edge corner turned that side into one edge from the board
// edge to 0.109 µm inside it, and its face met the board's side face at 0.0127°. Gmsh's boundary recovery stops on such a
// pair ("Invalid boundary mesh (overlapping facets)"). So the dielectrics' outlines are Edges: a point within the
// tolerance of one is moved onto it first (the sub-tolerance jog disappears with it), and a point that lies on one is
// removed only when both its neighbours lie on that same edge, so a face on the board's side stays on it.
//
// A ZERO-WIDTH SPIKE (designer round 35). A merged Gerber pour can run out along a line and straight back along it: a flat
// needle 28 µm long on a pad's arc, with no copper inside it. Its two side faces lie on each other, so Gmsh's boundary
// recovery stops with the same "overlapping facets" as above, and neither of the rules above touches it: its tip is far
// from its neighbours' SEGMENT though on their line. The needle holds no copper, so cutting it off moves no copper; the
// tolerance guarantee is about moved outlines and does not count it, and Stats reports it apart.
//
// A HOLE TOUCHING ITS OWN OUTLINE (designer round 37). A pour's round hole can be tangent to the pour's edge, a corner of the hole
// lying exactly on that edge with nothing beside it. OpenCASCADE splits one object's faces against another's, but never one face's
// outer wire against its own holes, so the edge stays whole and its 1D mesh runs through the hole's corner. Gmsh says "There are 9
// intersections in the 1D mesh", leaves the face with no elements, and stops at the 3D stage with "overlapping facets" or "Could not
// recover boundary mesh". OpenTouches moves that corner by the tolerance, into the hole, which leaves a neck the tolerance wide.

namespace CircuitRF.Design.Layout.Em3d;

public static class Em3dOutlineCleanup
{
    /// <summary>The largest move, metres: well under any process's etch tolerance and any element a mesher makes.</summary>
    public const double MaxToleranceM = 0.5e-6;

    /// <summary>The tolerance as a fraction of the conductor's own thickness, so a thin-film metal (1–3 µm) moves by tens of
    /// nanometres at most and copper (17–70 µm) by the <see cref="MaxToleranceM"/> cap.</summary>
    public const double ThicknessFraction = 1.0 / 50;

    /// <summary>The tolerance for a conductor <paramref name="thicknessM"/> thick.</summary>
    public static double Tolerance(double thicknessM) => Math.Min(MaxToleranceM, Math.Max(0, thicknessM) * ThicknessFraction);

    /// <summary>What a run's cleanup did, over every outline it saw.</summary>
    public sealed class Stats
    {
        public int Points { get; set; }
        public int Removed { get; set; }
        /// <summary>Points moved onto another object's edge (<see cref="Snap{T}"/>).</summary>
        public int Snapped { get; set; }
        public double MaxMoveM { get; set; }
        /// <summary>Zero-width spikes cut off: an outline that ran out along a line and back along it.</summary>
        public int Spikes { get; set; }
        /// <summary>How far the longest of <see cref="Spikes"/> reached, metres.</summary>
        public double LongestSpikeM { get; set; }
        /// <summary>Corners moved off another stretch of their own object's outline (<see cref="OpenTouches{T}"/>).</summary>
        public int Touches { get; set; }
    }

    /// <summary>
    /// brief-dfr31-6 — the segments of other objects' outlines (the dielectrics') that a conductor's outline may lie along,
    /// binned on a grid so a whole board's outline costs a cell lookup per point.
    /// </summary>
    public sealed class Edges
    {
        /// <summary>A point this close to a segment, metres, lies on it: rounding, not geometry.</summary>
        private const double OnM = 1e-12;

        private readonly List<((double X, double Y) A, (double X, double Y) B)> _segments = [];
        private readonly Dictionary<(long, long), List<int>> _cells = [];
        private readonly double _x0, _y0, _cell;

        public Edges(IEnumerable<IReadOnlyList<(double X, double Y)>> rings)
        {
            foreach (var ring in rings)
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    if (ring[j] != ring[i]) _segments.Add((ring[j], ring[i]));
            if (_segments.Count == 0) return;
            _x0 = _segments.Min(s => Math.Min(s.A.X, s.B.X));
            _y0 = _segments.Min(s => Math.Min(s.A.Y, s.B.Y));
            double span = Math.Max(_segments.Max(s => Math.Max(s.A.X, s.B.X)) - _x0, _segments.Max(s => Math.Max(s.A.Y, s.B.Y)) - _y0);
            _cell = Math.Max(span / 256, 1e-6);
            for (int k = 0; k < _segments.Count; k++)
            {
                // In pieces no longer than half a cell, so a long diagonal fills the cells along it, not its whole box.
                var (a, b) = _segments[k];
                int pieces = (int)Math.Ceiling(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / (_cell / 2)) + 1;
                for (int m = 0; m < pieces; m++)
                {
                    double t0 = (double)m / pieces, t1 = (double)(m + 1) / pieces;
                    double xa = a.X + t0 * (b.X - a.X), ya = a.Y + t0 * (b.Y - a.Y), xb = a.X + t1 * (b.X - a.X), yb = a.Y + t1 * (b.Y - a.Y);
                    var (i0, j0) = Cell(Math.Min(xa, xb) - MaxToleranceM, Math.Min(ya, yb) - MaxToleranceM);
                    var (i1, j1) = Cell(Math.Max(xa, xb) + MaxToleranceM, Math.Max(ya, yb) + MaxToleranceM);
                    for (long i = i0; i <= i1; i++)
                        for (long j = j0; j <= j1; j++)
                        {
                            if (!_cells.TryGetValue((i, j), out var list)) _cells[(i, j)] = list = [];
                            if (list.Count == 0 || list[^1] != k) list.Add(k);
                        }
                }
            }
        }

        public bool IsEmpty => _segments.Count == 0;

        /// <summary>The segments that may lie within the tolerance of <paramref name="p"/>.</summary>
        internal IEnumerable<((double X, double Y) A, (double X, double Y) B)> Around((double X, double Y) p)
            => Near(p).Select(k => _segments[k]);

        private (long, long) Cell(double x, double y) => ((long)Math.Floor((x - _x0) / _cell), (long)Math.Floor((y - _y0) / _cell));

        private IReadOnlyList<int> Near((double X, double Y) p)
            => _segments.Count > 0 && _cells.TryGetValue(Cell(p.X, p.Y), out var list) ? list : [];

        /// <summary>The segment <paramref name="p"/> lies on, or −1.</summary>
        public int On((double X, double Y) p)
        {
            foreach (int k in Near(p))
                if (Distance(p, _segments[k].A, _segments[k].B) <= OnM) return k;
            return -1;
        }

        /// <summary>Whether <paramref name="p"/> lies on segment <paramref name="k"/>.</summary>
        public bool On(int k, (double X, double Y) p) => Distance(p, _segments[k].A, _segments[k].B) <= OnM;

        /// <summary>Where <paramref name="p"/> goes: the nearest segment end within <paramref name="tolerance"/>, else its
        /// foot on the nearest segment within it, else itself.</summary>
        public (double X, double Y) Snap((double X, double Y) p, double tolerance)
        {
            var near = Near(p);
            (double X, double Y) best = p;
            double bestD = double.PositiveInfinity;
            foreach (int k in near)
                foreach (var end in new[] { _segments[k].A, _segments[k].B })
                    if (Math.Sqrt((p.X - end.X) * (p.X - end.X) + (p.Y - end.Y) * (p.Y - end.Y)) is var d && d <= tolerance && d < bestD)
                        (best, bestD) = (end, d);
            if (bestD < double.PositiveInfinity) return best;
            foreach (int k in near)
            {
                var (a, c) = _segments[k];
                double ux = c.X - a.X, uy = c.Y - a.Y, t = ((p.X - a.X) * ux + (p.Y - a.Y) * uy) / (ux * ux + uy * uy);
                if (t is < 0 or > 1) continue;
                (double X, double Y) foot = (a.X + t * ux, a.Y + t * uy);
                double d = Math.Sqrt((p.X - foot.X) * (p.X - foot.X) + (p.Y - foot.Y) * (p.Y - foot.Y));
                if (d <= tolerance && d < bestD) (best, bestD) = (foot, d);
            }
            return best;
        }
    }

    /// <summary>
    /// brief-dfr31-6 — <paramref name="ring"/> with every point within <paramref name="tolerance"/> of one of
    /// <paramref name="edges"/> moved onto it, and the repeats that makes dropped (the same list when nothing moves, or
    /// when what is left is not a polygon), counted into <paramref name="stats"/>.
    /// </summary>
    public static IReadOnlyList<T> Snap<T>(IReadOnlyList<T> ring, Func<T, (double X, double Y)> xy, Func<double, double, T> make,
                                           double tolerance, Edges edges, Stats stats)
    {
        if (edges.IsEmpty || !(tolerance > 0)) return ring;
        var moved = new List<(double X, double Y)>(ring.Count);
        int snapped = 0;
        double worst = 0;
        foreach (var q in ring)
        {
            var p = xy(q);
            var s = edges.On(p) >= 0 ? p : edges.Snap(p, tolerance);
            if (s != p)
            {
                snapped++;
                worst = Math.Max(worst, Math.Sqrt((p.X - s.X) * (p.X - s.X) + (p.Y - s.Y) * (p.Y - s.Y)));
            }
            if (moved.Count == 0 || moved[^1] != s) moved.Add(s);
        }
        while (moved.Count > 1 && moved[^1] == moved[0]) moved.RemoveAt(moved.Count - 1);
        if (snapped == 0 || moved.Count < 3) return ring;
        stats.Snapped += snapped;
        stats.Points += ring.Count - moved.Count;    // Clean counts the rest
        stats.Removed += ring.Count - moved.Count;
        stats.MaxMoveM = Math.Max(stats.MaxMoveM, worst);
        return [.. moved.Select(p => make(p.X, p.Y))];
    }

    /// <summary>Two points this close, metres, are one point: half the layout's smallest step. A gap of 1 nm is two faces, and meshes.</summary>
    public const double SameM = 0.5e-9;

    /// <summary>
    /// Whether corner <paramref name="b"/>, between <paramref name="a"/> and <paramref name="c"/>, touches the segment
    /// <paramref name="s"/>–<paramref name="t"/> at that one point: on it, with both neighbours off its line on one side. So an edge of
    /// the corner's own is not touched (a neighbour lies on its line), nor is a stretch the outline runs along, which is a cut of no width.
    /// </summary>
    public static bool TouchesAtPoint((double X, double Y) a, (double X, double Y) b, (double X, double Y) c,
                                      (double X, double Y) s, (double X, double Y) t)
    {
        double ux = t.X - s.X, uy = t.Y - s.Y, l = Math.Sqrt(ux * ux + uy * uy);
        if (l <= SameM || Distance(b, s, t) > SameM) return false;
        double sa = (ux * (a.Y - s.Y) - uy * (a.X - s.X)) / l, sc = (ux * (c.Y - s.Y) - uy * (c.X - s.X)) / l;
        return (sa > SameM && sc > SameM) || (sa < -SameM && sc < -SameM);
    }

    /// <summary>
    /// <paramref name="rings"/>, one object's outer ring and holes, with every corner that touches another stretch of them at one
    /// point moved by <paramref name="tolerance"/> into its own wedge, counted into <paramref name="stats"/> (the same list when there
    /// is none). A corner too shallow to take the move stays, for <see cref="Em3dGeometryCheck"/> to report.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<T>> OpenTouches<T>(IReadOnlyList<IReadOnlyList<T>> rings, Func<T, (double X, double Y)> xy,
                                                                 Func<double, double, T> make, double tolerance, Stats stats)
    {
        if (!(tolerance > 0) || rings.Sum(r => r.Count) <= 4) return rings;
        var points = rings.Select(r => r.Select(xy).ToList()).ToList();
        var edges = new Edges(points.Select(r => (IReadOnlyList<(double X, double Y)>)r));
        List<IReadOnlyList<T>>? result = null;
        for (int r = 0; r < points.Count; r++)
        {
            var p = points[r];
            int n = p.Count;
            List<T>? moved = null;
            for (int i = 0; i < n && n > 3; i++)
            {
                var (a, b, c) = (p[(i + n - 1) % n], p[i], p[(i + 1) % n]);
                if (!edges.Around(b).Any(e => TouchesAtPoint(a, b, c, e.A, e.B))) continue;
                double la = Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
                double lc = Math.Sqrt((c.X - b.X) * (c.X - b.X) + (c.Y - b.Y) * (c.Y - b.Y));
                double wx = (a.X - b.X) / la + (c.X - b.X) / lc, wy = (a.Y - b.Y) / la + (c.Y - b.Y) / lc;
                double w = Math.Sqrt(wx * wx + wy * wy);
                if (w < 1e-9 || Distance(b, a, c) <= tolerance) continue;
                moved ??= [.. rings[r]];
                moved[i] = make(b.X + tolerance * wx / w, b.Y + tolerance * wy / w);
                stats.Touches++;
                stats.MaxMoveM = Math.Max(stats.MaxMoveM, tolerance);
            }
            if (moved is null) continue;
            result ??= [.. rings];
            result[r] = moved;
        }
        return result ?? rings;
    }

    /// <summary><paramref name="ring"/> with its removable points removed (the same list when none is), counted into
    /// <paramref name="stats"/>. A ring keeps at least three points. With <paramref name="edges"/>, a point on one of them
    /// stays unless both its neighbours lie on that same one (brief-dfr31-6).</summary>
    public static IReadOnlyList<T> Clean<T>(IReadOnlyList<T> ring, Func<T, (double X, double Y)> xy, double tolerance, Stats stats,
                                            Edges? edges = null)
    {
        int n = ring.Count;
        stats.Points += n;
        if (n <= 3 || !(tolerance > 0)) return ring;
        var p = new (double X, double Y)[n];
        for (int i = 0; i < n; i++) p[i] = xy(ring[i]);
        int[] next = new int[n], prev = new int[n];
        for (int i = 0; i < n; i++) { next[i] = (i + 1) % n; prev[i] = (i + n - 1) % n; }
        var alive = new bool[n];
        Array.Fill(alive, true);
        int left = n;
        int[] edgeOf = new int[n];
        for (int i = 0; i < n; i++) edgeOf[i] = edges?.On(p[i]) ?? -1;

        // The points of a spike already cut off: no copper was there, so they are no measure of how far an outline moved.
        var inSpike = new bool[n];

        // The farthest of the original points strictly between a and c (ring order) from the segment a–c.
        double Covered(int a, int c)
        {
            double worst = 0;
            for (int k = (a + 1) % n; k != c; k = (k + 1) % n)
                if (!inSpike[k]) worst = Math.Max(worst, Distance(p[k], p[a], p[c]));
            return worst;
        }

        // Whether the outline turns straight back at b: a reversal whose shorter leg ends within the tolerance of the longer
        // leg's line. Covered, not this, takes a tiny edge at an ordinary corner, whose point is near the segment a–c too.
        bool TurnsBack(int a, int b, int c)
        {
            double ux = p[b].X - p[a].X, uy = p[b].Y - p[a].Y, vx = p[c].X - p[b].X, vy = p[c].Y - p[b].Y;
            double lu = Math.Sqrt(ux * ux + uy * uy), lv = Math.Sqrt(vx * vx + vy * vy);
            return lu > 0 && lv > 0 && ux * vx + uy * vy < 0 && Math.Abs(ux * vy - uy * vx) / Math.Max(lu, lv) < tolerance;
        }

        var touched = new bool[n];
        bool changed = true;
        while (changed && left > 3)
        {
            changed = false;
            Array.Clear(touched);
            for (int b = 0; b < n && left > 3; b++)
            {
                if (!alive[b] || touched[b]) continue;
                int a = prev[b], c = next[b];
                double reach = Covered(a, c);
                if (reach >= tolerance && TurnsBack(a, b, c))
                {
                    stats.Spikes++;
                    stats.LongestSpikeM = Math.Max(stats.LongestSpikeM, reach);
                    for (int k = (a + 1) % n; k != c; k = (k + 1) % n)
                        if (Distance(p[k], p[a], p[c]) >= tolerance) inSpike[k] = true;
                }
                else
                {
                    if (edgeOf[b] is int e and >= 0 && !(edges!.On(e, p[a]) && edges.On(e, p[c]))) continue;
                    if (reach >= tolerance) continue;
                }
                alive[b] = false;
                next[a] = c; prev[c] = a;
                touched[a] = touched[c] = true;
                left--;
                changed = true;
            }
        }
        if (left == n) return ring;

        var kept = new List<T>(left);
        for (int i = 0; i < n; i++)
            if (alive[i])
            {
                kept.Add(ring[i]);
                stats.MaxMoveM = Math.Max(stats.MaxMoveM, Covered(i, next[i]));
            }
        stats.Removed += n - left;
        return kept;
    }

    private static double Distance((double X, double Y) q, (double X, double Y) a, (double X, double Y) c)
    {
        double ux = c.X - a.X, uy = c.Y - a.Y, l2 = ux * ux + uy * uy;
        double t = l2 > 0 ? Math.Clamp(((q.X - a.X) * ux + (q.Y - a.Y) * uy) / l2, 0, 1) : 0;
        double dx = q.X - (a.X + t * ux), dy = q.Y - (a.Y + t * uy);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
