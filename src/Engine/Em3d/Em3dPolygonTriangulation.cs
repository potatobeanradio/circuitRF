// brief-em3d-28 R-em3d28-2a — the caps of an extruded polygon: a polygon with holes, triangulated.
//
// EAR CLIPPING WITH HOLE BRIDGING, written here because there was no triangulator in the repository
// and the brief rules out a package for it. Each hole is joined to the outline by a zero-width bridge
// (a pair of coincident edges), which turns the polygon-with-holes into ONE weakly simple ring, and
// that ring is clipped ear by ear. The shape of the algorithm is the textbook one (Eberly's bridging,
// Meisters' ears) with the three standard fall-backs for input a layout really produces — duplicate and
// collinear points, a local self-touch, and a ring no ear can be found on — each described where it is.
//
// DETERMINISTIC like the rest of Em3dTessellation: nothing is ordered by a hash, holes are merged in a
// fixed order (leftmost vertex, then y, then input order), every tie is broken by input index, and the
// constants are named. The same polygon gives the same triangles, bit for bit, on every platform.
//
// FAST ENOUGH FOR A BOARD OUTLINE. An ear test must look at every vertex that could lie inside the
// candidate triangle. Scanning the whole ring makes clipping O(n²) per pass, which a 10,000-vertex board
// outline turns into seconds; above ZOrderMinVertices the vertices are also kept on a second list in
// Morton (z-order) sequence, so an ear test visits only the vertices whose Morton code lies within its
// triangle's bounding box.

namespace CircuitRF.Engine.Em3d;

/// <summary>One cap triangle, as indices into the rings handed to
/// <see cref="Em3dPolygonTriangulation.Triangulate"/>: the outline's vertices first, then each hole's in
/// order. Wound counter-clockwise seen from +z.</summary>
public readonly record struct Em3dCapTriangle(int A, int B, int C);

/// <summary>Triangulation of a polygon with holes (R-em3d28-2a).</summary>
public static class Em3dPolygonTriangulation
{
    /// <summary>Below this many vertices an ear test scans the whole ring: building the Morton index
    /// costs more than it saves on a rectangle or a pad.</summary>
    public const int ZOrderMinVertices = 80;

    /// <summary>Bits per axis of the Morton code. 15 bits keeps the interleaved code inside a
    /// non-negative <see cref="int"/>.</summary>
    public const int ZOrderBitsPerAxis = 15;

    /// <summary>
    /// The triangles covering <paramref name="outline"/> minus <paramref name="holes"/>, CCW from +z.
    /// Rings may be wound either way, may repeat their first point at the end, and may carry duplicate
    /// or collinear points; a ring with fewer than three distinct points contributes nothing. A vertex
    /// index refers to the concatenation outline + holes[0] + holes[1] + … of the rings AS GIVEN, so a
    /// caller that built its side walls from the same rings shares their vertices.
    /// </summary>
    public static IReadOnlyList<Em3dCapTriangle> Triangulate(IReadOnlyList<Point2> outline,
                                                           IReadOnlyList<IReadOnlyList<Point2>> holes)
    {
        ArgumentNullException.ThrowIfNull(outline);
        ArgumentNullException.ThrowIfNull(holes);
        var t = new Triangulator();
        return t.Run(outline, holes);
    }

    /// <summary>Twice the signed area of a ring: positive when it runs counter-clockwise.</summary>
    public static double SignedArea2(IReadOnlyList<Point2> ring)
    {
        double s = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            s += (ring[j].X - ring[i].X) * (ring[i].Y + ring[j].Y);
        return s;
    }

    private sealed class Node(int index, double x, double y)
    {
        public readonly int I = index;
        public readonly double X = x, Y = y;
        public Node Prev = null!, Next = null!;
        public int Z = -1;
        public Node? PrevZ, NextZ;
    }

    private sealed class Triangulator
    {
        private readonly List<Em3dCapTriangle> _out = [];
        private double _minX, _minY, _invSize;
        private bool _zIndexed;

        public IReadOnlyList<Em3dCapTriangle> Run(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
        {
            var outer = Ring(outline, 0, counterClockwise: true);
            if (outer is null || outer.Next == outer.Prev) return _out;

            int start = outline.Count;
            var leftmost = new List<(Node Node, int Order)>();
            for (int h = 0; h < holes.Count; h++)
            {
                var ring = Ring(holes[h], start, counterClockwise: false);
                start += holes[h].Count;
                if (ring is null || ring.Next == ring.Prev) continue;
                leftmost.Add((Leftmost(ring), h));
            }
            leftmost.Sort((a, b) => a.Node.X != b.Node.X ? a.Node.X.CompareTo(b.Node.X)
                                  : a.Node.Y != b.Node.Y ? a.Node.Y.CompareTo(b.Node.Y)
                                  : a.Order.CompareTo(b.Order));
            foreach (var (hole, _) in leftmost)
                outer = EliminateHole(hole, outer);

            outer = FilterPoints(outer, null);
            if (outer is null) return _out;

            int count = 0;
            var p = outer;
            do { count++; p = p.Next; } while (p != outer);
            if (count > ZOrderMinVertices)
            {
                double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                p = outer;
                do
                {
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                    p = p.Next;
                } while (p != outer);
                double size = Math.Max(maxX - minX, maxY - minY);
                if (size > 0)
                {
                    _minX = minX; _minY = minY;
                    _invSize = ((1 << ZOrderBitsPerAxis) - 1) / size;
                    _zIndexed = true;
                }
            }

            Clip(outer, 0);
            return _out;
        }

        // ── rings ────────────────────────────────────────────────────────────────────────────

        /// <summary>A circular list for one ring, wound as asked, with consecutive duplicates (and a
        /// closing repeat of the first point) dropped. Null for a ring of fewer than three points.</summary>
        private static Node? Ring(IReadOnlyList<Point2> ring, int start, bool counterClockwise)
        {
            if (ring.Count < 3) return null;
            bool ccw = SignedArea2(ring) > 0;
            Node? last = null;
            if (ccw == counterClockwise)
                for (int i = 0; i < ring.Count; i++) last = Insert(start + i, ring[i], last);
            else
                for (int i = ring.Count - 1; i >= 0; i--) last = Insert(start + i, ring[i], last);

            if (last is not null && Equal(last, last.Next)) { var n = last.Next; Remove(last); last = n; }
            return last is null ? null : FilterPoints(last, null);
        }

        private static Node Insert(int i, Point2 p, Node? last)
        {
            var n = new Node(i, p.X, p.Y);
            if (last is null) { n.Prev = n; n.Next = n; }
            else
            {
                n.Next = last.Next; n.Prev = last;
                last.Next.Prev = n; last.Next = n;
            }
            return n;
        }

        private static void Remove(Node p)
        {
            p.Next.Prev = p.Prev;
            p.Prev.Next = p.Next;
            if (p.PrevZ is not null) p.PrevZ.NextZ = p.NextZ;
            if (p.NextZ is not null) p.NextZ.PrevZ = p.PrevZ;
        }

        /// <summary>Drops coincident neighbours and exactly collinear points between
        /// <paramref name="start"/> and <paramref name="end"/> (the whole ring when null). Removing a
        /// collinear point loses no area: the point lies on the segment that replaces its two edges.
        /// Returns a node still in the ring, or null when fewer than three are left.</summary>
        private static Node? FilterPoints(Node start, Node? end)
        {
            end ??= start;
            var p = start;
            bool again;
            do
            {
                again = false;
                if (Equal(p, p.Next) || Area2(p.Prev, p, p.Next) == 0)
                {
                    if (p.Next == p.Prev || p.Next == p) return null;
                    var prev = p.Prev;
                    Remove(p);
                    p = end = prev;
                    if (p == p.Next) return null;
                    again = true;
                }
                else p = p.Next;
            } while (again || p != end);
            return end;
        }

        private static Node Leftmost(Node start)
        {
            Node p = start, best = start;
            do
            {
                if (p.X < best.X || (p.X == best.X && (p.Y < best.Y || (p.Y == best.Y && p.I < best.I)))) best = p;
                p = p.Next;
            } while (p != start);
            return best;
        }

        // ── holes ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Joins the hole whose leftmost vertex is <paramref name="hole"/> to the outer ring. A hole that
        /// TOUCHES the ring at a vertex is joined there, with a bridge of zero length — that is the one
        /// place the two may be joined without the bridge crossing either. Otherwise a ray is cast to −x
        /// from the hole's leftmost vertex and the bridge goes to the nearest visible vertex it finds.
        /// Holes are merged left to right, so everything the ray can meet is already on the ring.
        /// </summary>
        private Node EliminateHole(Node hole, Node outer)
        {
            var bridge = TouchingVertex(hole, outer, out var holeAt) ?? FindBridge(hole, outer);
            if (bridge is null) return outer;     // a hole outside the outline: nothing to cut
            var from = holeAt ?? hole;
            var b2 = Split(bridge, from);
            FilterPoints(b2, b2.Next);
            return FilterPoints(bridge, bridge.Next) ?? bridge;
        }

        private static Node? TouchingVertex(Node hole, Node outer, out Node? holeAt)
        {
            holeAt = null;
            var p = outer;
            do
            {
                var h = hole;
                do
                {
                    if (Equal(p, h) && LocallyInside(p, h.Next) && LocallyInside(h, p.Next))
                    {
                        holeAt = h;
                        return p;
                    }
                    h = h.Next;
                } while (h != hole);
                p = p.Next;
            } while (p != outer);
            return null;
        }

        private static Node? FindBridge(Node hole, Node outer)
        {
            double hx = hole.X, hy = hole.Y, qx = double.NegativeInfinity;
            Node? m = null;
            var p = outer;
            do
            {
                if (hy <= p.Y && hy >= p.Next.Y && p.Next.Y != p.Y)
                {
                    double x = p.X + (hy - p.Y) * (p.Next.X - p.X) / (p.Next.Y - p.Y);
                    if (x <= hx && x > qx)
                    {
                        qx = x;
                        m = p.X < p.Next.X ? p : p.Next;
                        if (x == hx) return m;      // the hole touches this edge: its end is visible
                    }
                }
                p = p.Next;
            } while (p != outer);
            if (m is null) return null;

            // A reflex vertex inside the triangle (hole, ray hit, m) would hide m; take the one making
            // the smallest angle with the ray instead, preferring the rightmost on a tie.
            var stop = m;
            double mx = m.X, my = m.Y, tanMin = double.PositiveInfinity;
            p = m;
            do
            {
                if (hx >= p.X && p.X >= mx && hx != p.X &&
                    PointInTriangle(hy < my ? hx : qx, hy, mx, my, hy < my ? qx : hx, hy, p.X, p.Y))
                {
                    double tan = Math.Abs(hy - p.Y) / (hx - p.X);
                    if (LocallyInside(p, hole) &&
                        (tan < tanMin || (tan == tanMin && (p.X > m.X || (p.X == m.X && SectorContainsSector(m, p))))))
                    {
                        m = p;
                        tanMin = tan;
                    }
                }
                p = p.Next;
            } while (p != stop);
            return m;
        }

        /// <summary>Whether the sector at <paramref name="m"/> contains the one at <paramref name="p"/>
        /// (two coincident candidates: bridge to the one whose opening faces the hole).</summary>
        private static bool SectorContainsSector(Node m, Node p)
            => Area2(m.Prev, m, p.Prev) > 0 && Area2(p.Next, m, m.Next) > 0;

        /// <summary>Links <paramref name="a"/> to <paramref name="b"/> with a two-way diagonal, splitting
        /// one ring into two (or joining two into one, for a hole). Returns the copy of <paramref name="b"/>
        /// on the other side.</summary>
        private static Node Split(Node a, Node b)
        {
            var a2 = new Node(a.I, a.X, a.Y);
            var b2 = new Node(b.I, b.X, b.Y);
            var an = a.Next;
            var bp = b.Prev;
            a.Next = b; b.Prev = a;
            a2.Next = an; an.Prev = a2;
            b2.Next = a2; a2.Prev = b2;
            bp.Next = b2; b2.Prev = bp;
            return b2;
        }

        // ── ears ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clips ears until three vertices are left. A full lap with no ear moves to the next fall-back:
        /// pass 0 → drop duplicate and collinear points and retry; pass 1 → clip local self-touches
        /// (a vertex whose neighbours' edges cross); pass 2 → split the ring along any valid diagonal and
        /// clip each half. A ring that none of these can reduce is left, with what could be clipped.
        /// </summary>
        private void Clip(Node? ear, int pass)
        {
            if (ear is null) return;
            if (pass == 0 && _zIndexed) IndexCurve(ear);
            var stop = ear;
            while (ear.Prev != ear.Next)
            {
                var prev = ear.Prev;
                var next = ear.Next;
                if (_zIndexed ? IsEarIndexed(ear) : IsEar(ear))
                {
                    _out.Add(new Em3dCapTriangle(prev.I, ear.I, next.I));
                    Remove(ear);
                    ear = next.Next;
                    stop = next.Next;
                    continue;
                }
                ear = next;
                if (ear == stop)
                {
                    switch (pass)
                    {
                        case 0: Clip(FilterPoints(ear, null), 1); break;
                        case 1: Clip(CureLocalIntersections(FilterPoints(ear, null)), 2); break;
                        case 2: SplitClip(ear); break;
                    }
                    break;
                }
            }
        }

        private static bool IsEar(Node ear)
        {
            Node a = ear.Prev, b = ear, c = ear.Next;
            if (Area2(a, b, c) <= 0) return false;       // reflex, or flat
            var p = c.Next;
            while (p != a)
            {
                if (Blocks(a, b, c, p)) return false;
                p = p.Next;
            }
            return true;
        }

        private bool IsEarIndexed(Node ear)
        {
            Node a = ear.Prev, b = ear, c = ear.Next;
            if (Area2(a, b, c) <= 0) return false;
            double x0 = Math.Min(a.X, Math.Min(b.X, c.X)), y0 = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            double x1 = Math.Max(a.X, Math.Max(b.X, c.X)), y1 = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            int minZ = ZOrder(x0, y0), maxZ = ZOrder(x1, y1);

            Node? p = ear.PrevZ, n = ear.NextZ;
            while (p is not null && p.Z >= minZ && n is not null && n.Z <= maxZ)
            {
                if (p != a && p != c && InBox(p, x0, y0, x1, y1) && Blocks(a, b, c, p)) return false;
                p = p.PrevZ;
                if (n != a && n != c && InBox(n, x0, y0, x1, y1) && Blocks(a, b, c, n)) return false;
                n = n.NextZ;
            }
            while (p is not null && p.Z >= minZ)
            {
                if (p != a && p != c && InBox(p, x0, y0, x1, y1) && Blocks(a, b, c, p)) return false;
                p = p.PrevZ;
            }
            while (n is not null && n.Z <= maxZ)
            {
                if (n != a && n != c && InBox(n, x0, y0, x1, y1) && Blocks(a, b, c, n)) return false;
                n = n.NextZ;
            }
            return true;
        }

        private static bool InBox(Node p, double x0, double y0, double x1, double y1)
            => p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1;

        /// <summary>
        /// Whether <paramref name="p"/> stops a→b→c being an ear: it lies inside the triangle or on its
        /// boundary and is reflex or flat there. A point coincident with a corner does not block — it is
        /// the other end of a bridge, or a hole touching the outline, and both are allowed to share a
        /// corner with the ear. A CONVEX point inside cannot occur without a reflex one inside as well.
        /// </summary>
        private static bool Blocks(Node a, Node b, Node c, Node p)
        {
            if (p == a || p == b || p == c) return false;
            if (Equal(p, a) || Equal(p, b) || Equal(p, c)) return false;
            return PointInTriangle(a.X, a.Y, b.X, b.Y, c.X, c.Y, p.X, p.Y) && Area2(p.Prev, p, p.Next) <= 0;
        }

        private Node? CureLocalIntersections(Node? start)
        {
            if (start is null) return null;
            var p = start;
            do
            {
                Node a = p.Prev, b = p.Next.Next;
                if (!Equal(a, b) && Intersects(a, p, p.Next, b) && LocallyInside(a, b) && LocallyInside(b, a))
                {
                    _out.Add(new Em3dCapTriangle(a.I, p.I, b.I));
                    Remove(p);
                    Remove(p.Next);
                    p = start = b;
                }
                p = p.Next;
            } while (p != start);
            return FilterPoints(p, null);
        }

        private void SplitClip(Node start)
        {
            var a = start;
            do
            {
                var b = a.Next.Next;
                while (b != a.Prev)
                {
                    if (a.I != b.I && IsValidDiagonal(a, b))
                    {
                        var c = Split(a, b);
                        var a1 = FilterPoints(a, a.Next);
                        var c1 = FilterPoints(c, c.Next);
                        Clip(a1, 0);
                        Clip(c1, 0);
                        return;
                    }
                    b = b.Next;
                }
                a = a.Next;
            } while (a != start);
        }

        private static bool IsValidDiagonal(Node a, Node b)
            => a.Next.I != b.I && a.Prev.I != b.I && !IntersectsPolygon(a, b)
               && LocallyInside(a, b) && LocallyInside(b, a) && MiddleInside(a, b)
               && (Area2(a.Prev, a, b.Prev) != 0 || Area2(a, b.Prev, b) != 0);

        // ── the Morton index ─────────────────────────────────────────────────────────────────

        private void IndexCurve(Node start)
        {
            var p = start;
            do
            {
                if (p.Z < 0) p.Z = ZOrder(p.X, p.Y);
                p.PrevZ = p.Prev;
                p.NextZ = p.Next;
                p = p.Next;
            } while (p != start);
            p.PrevZ!.NextZ = null;
            p.PrevZ = null;
            SortLinked(p);
        }

        /// <summary>Merge sort of the z-list by Morton code — stable, so equal codes keep ring order.</summary>
        private static Node? SortLinked(Node? list)
        {
            int inSize = 1, merges;
            do
            {
                var p = list;
                list = null;
                Node? tail = null;
                merges = 0;
                while (p is not null)
                {
                    merges++;
                    var q = p;
                    int pSize = 0;
                    for (int i = 0; i < inSize; i++)
                    {
                        pSize++;
                        q = q.NextZ;
                        if (q is null) break;
                    }
                    int qSize = inSize;
                    while (pSize > 0 || (qSize > 0 && q is not null))
                    {
                        Node e;
                        if (pSize != 0 && (qSize == 0 || q is null || p!.Z <= q.Z)) { e = p!; p = p!.NextZ; pSize--; }
                        else { e = q!; q = q!.NextZ; qSize--; }
                        if (tail is not null) tail.NextZ = e; else list = e;
                        e.PrevZ = tail;
                        tail = e;
                    }
                    p = q;
                }
                tail!.NextZ = null;
                inSize *= 2;
            } while (merges > 1);
            return list;
        }

        private int ZOrder(double x, double y)
        {
            int ix = (int)((x - _minX) * _invSize), iy = (int)((y - _minY) * _invSize);
            return Spread(ix) | (Spread(iy) << 1);
        }

        private static int Spread(int v)
        {
            v = (v | (v << 8)) & 0x00FF00FF;
            v = (v | (v << 4)) & 0x0F0F0F0F;
            v = (v | (v << 2)) & 0x33333333;
            v = (v | (v << 1)) & 0x55555555;
            return v;
        }
    }

    // ── predicates ───────────────────────────────────────────────────────────────────────────

    /// <summary>Twice the signed area of a→b→c: positive when it turns left (counter-clockwise).</summary>
    private static double Area2(Node a, Node b, Node c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool Equal(Node a, Node b) => a.X == b.X && a.Y == b.Y;

    /// <summary>Inside or on the boundary of the triangle, for either winding.</summary>
    private static bool PointInTriangle(double ax, double ay, double bx, double by, double cx, double cy,
                                        double px, double py)
    {
        double d1 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        double d2 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
        double d3 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }

    private static int Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;

    private static bool OnSegment(Node p, Node q, Node r)
        => q.X <= Math.Max(p.X, r.X) && q.X >= Math.Min(p.X, r.X) && q.Y <= Math.Max(p.Y, r.Y) && q.Y >= Math.Min(p.Y, r.Y);

    /// <summary>Whether segments p1q1 and p2q2 meet, touching included.</summary>
    private static bool Intersects(Node p1, Node q1, Node p2, Node q2)
    {
        int o1 = Sign(Area2(p1, q1, p2)), o2 = Sign(Area2(p1, q1, q2));
        int o3 = Sign(Area2(p2, q2, p1)), o4 = Sign(Area2(p2, q2, q1));
        if (o1 != o2 && o3 != o4) return true;
        if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
        if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
        if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
        if (o4 == 0 && OnSegment(p2, q1, q2)) return true;
        return false;
    }

    private static bool IntersectsPolygon(Node a, Node b)
    {
        var p = a;
        do
        {
            if (p.I != a.I && p.Next.I != a.I && p.I != b.I && p.Next.I != b.I && Intersects(p, p.Next, a, b))
                return true;
            p = p.Next;
        } while (p != a);
        return false;
    }

    /// <summary>Whether the diagonal a→b leaves <paramref name="a"/> into the polygon's interior.</summary>
    private static bool LocallyInside(Node a, Node b)
        => Area2(a.Prev, a, a.Next) > 0
            ? Area2(a, b, a.Next) <= 0 && Area2(a, a.Prev, b) <= 0
            : Area2(a, b, a.Prev) > 0 || Area2(a, a.Next, b) > 0;

    /// <summary>Whether the midpoint of a→b is inside the ring (crossing parity).</summary>
    private static bool MiddleInside(Node a, Node b)
    {
        var p = a;
        bool inside = false;
        double px = (a.X + b.X) / 2, py = (a.Y + b.Y) / 2;
        do
        {
            if (((p.Y > py) != (p.Next.Y > py)) && p.Next.Y != p.Y &&
                px < (p.Next.X - p.X) * (py - p.Y) / (p.Next.Y - p.Y) + p.X)
                inside = !inside;
            p = p.Next;
        } while (p != a);
        return inside;
    }
}
