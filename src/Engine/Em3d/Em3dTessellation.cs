// brief-em3d-5 R-em3d5-1 — one tessellation of a 3D problem's solids, below the firewall.
//
// WRITTEN ONCE because three consumers need the same triangles: brief 5's section cuts through the
// primitives that are not cut analytically (sweeps and spheres) and its isometric outline, brief 9's
// polyhedra for hexagonal wires (em-3d.md §6.6: "circuitRF generates the mitred prisms itself"), and
// F2's viewer. Three tessellations would be three answers to where a wire's surface is.
//
// DETERMINISTIC: every segment count is a constant below, every vertex is computed by one expression
// from the primitive's own numbers, and nothing is ordered by a hash. The same solid gives the same
// triangles, bit for bit, on every platform.
//
// A SWEEP IS NOT RE-DERIVED. Brief 4 made Em3dSweep carry its section RINGS — mitred at every interior
// vertex, square at the two ends — so the rings ARE the geometry. The tessellation joins ring k to
// ring k + 1 using the ring's own Point3 values, which is what makes two consecutive prisms share
// their joint face exactly: the same numbers, not two computations of them (R-em3d5-1b).
//
// AN EXTRUDED POLYGON (brief-em3d-28 R-em3d28-2a): its side walls join each ring's bottom vertex to its
// top one, and its two caps come from Em3dPolygonTriangulation over the SAME ring vertices, so walls and
// caps share their corners. A collinear point the triangulator drops stays on the wall, which leaves a
// T-junction on the cap's edge — still a closed surface, and the one the viewer draws. A sheet
// (Em3dSheet) is its cap alone, at its height (OfSheet).

namespace CircuitRF.Engine.Em3d;

/// <summary>One triangle, as three indices into its mesh's vertices, tagged with the solid it
/// belongs to. The tag travels with the triangle so meshes of several solids can be concatenated
/// without losing which is which.</summary>
public readonly record struct Em3dTriangle(int A, int B, int C, string Solid);

/// <summary>A closed triangle mesh. Vertices are shared between the triangles that meet at them —
/// a mesh welded by construction, which is what lets a consumer find an edge's two faces.</summary>
public sealed record Em3dTriangleMesh(IReadOnlyList<Point3> Vertices, IReadOnlyList<Em3dTriangle> Triangles);

public static class Em3dTessellation
{
    /// <summary>Facets around a cylinder's axis. A multiple of four, so a facet vertex sits on each
    /// of the two axes perpendicular to a vertical via and its XZ/YZ silhouettes are exact.</summary>
    public const int CylinderSegments = 32;

    /// <summary>Facets around a sphere's vertical axis (longitude). A multiple of four for the same
    /// reason as <see cref="CylinderSegments"/>.</summary>
    public const int SphereSegments = 32;

    /// <summary>Bands from a sphere's south pole to its north pole (latitude). Even, so the equator is
    /// a ring of vertices. A truncated sphere keeps the same number of bands between its two cuts.</summary>
    public const int SphereBands = 16;

    /// <summary>The triangles of <paramref name="solid"/>, every one tagged with its name.</summary>
    public static Em3dTriangleMesh Of(Em3dSolid solid)
    {
        ArgumentNullException.ThrowIfNull(solid);
        var b = new Builder(solid.Name);
        switch (solid.Primitive)
        {
            case Em3dBox box:              b.Box(box); break;
            case Em3dCylinder cyl:         b.Cylinder(cyl); break;
            case Em3dSweep sweep:          b.Sweep(sweep); break;
            case Em3dSphere s:             b.Sphere(s.Center, s.Radius, -s.Radius, s.Radius); break;
            case Em3dTruncatedSphere t:
                b.Sphere(t.Center, t.Radius,
                         Math.Max(t.ZMin - t.Center.Z, -t.Radius), Math.Min(t.ZMax - t.Center.Z, t.Radius));
                break;
            case Em3dExtrudedPolygon e:    b.Extrusion(e.Outline, e.Holes, e.ZBottom, e.ZTop); break;
            default:
                throw new ArgumentOutOfRangeException(nameof(solid), solid.Primitive.GetType().Name,
                                                      "unknown primitive");
        }
        return new Em3dTriangleMesh(b.Vertices, b.Triangles);
    }

    /// <summary>Whether <see cref="Of"/> accepts this primitive — every primitive, since brief 28 gave
    /// the extruded polygon its caps.</summary>
    public static bool CanTessellate(Em3dPrimitive p) => p is not null;

    /// <summary>A sheet's triangles: its polygon at its height, CCW seen from +z, tagged with its
    /// name. Its thickness is a number the solver reads, not geometry (§6.1), so it is not drawn.</summary>
    public static Em3dTriangleMesh OfSheet(Em3dSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        var b = new Builder(sheet.Name);
        b.Flat(sheet.Outline, sheet.Holes, sheet.Z);
        return new Em3dTriangleMesh(b.Vertices, b.Triangles);
    }

    private sealed class Builder(string name)
    {
        public readonly List<Point3> Vertices = [];
        public readonly List<Em3dTriangle> Triangles = [];

        private int V(Point3 p) { Vertices.Add(p); return Vertices.Count - 1; }
        private void T(int a, int b, int c) => Triangles.Add(new Em3dTriangle(a, b, c, name));
        private void Quad(int a, int b, int c, int d) { T(a, b, c); T(a, c, d); }

        public void Box(Em3dBox box)
        {
            var (lo, hi) = (box.Min, box.Max);
            int[] v = new int[8];
            for (int k = 0; k < 8; k++)
                v[k] = V(new Point3((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z));
            // Outward-facing, counter-clockwise seen from outside.
            Quad(v[0], v[2], v[3], v[1]);   // z min
            Quad(v[4], v[5], v[7], v[6]);   // z max
            Quad(v[0], v[1], v[5], v[4]);   // y min
            Quad(v[2], v[6], v[7], v[3]);   // y max
            Quad(v[0], v[4], v[6], v[2]);   // x min
            Quad(v[1], v[3], v[7], v[5]);   // x max
        }

        public void Cylinder(Em3dCylinder c)
        {
            var axis = Sub(c.AxisEnd, c.AxisStart);
            var (u, w) = Frame(axis);
            int n = CylinderSegments;
            int[] bottom = new int[n], top = new int[n];
            for (int k = 0; k < n; k++)
            {
                double t = 2 * Math.PI * k / n;
                double cu = c.Radius * Math.Cos(t), cw = c.Radius * Math.Sin(t);
                var off = new Point3(u.X * cu + w.X * cw, u.Y * cu + w.Y * cw, u.Z * cu + w.Z * cw);
                bottom[k] = V(Add(c.AxisStart, off));
                top[k]    = V(Add(c.AxisEnd, off));
            }
            for (int k = 0; k < n; k++)
                Quad(bottom[k], bottom[(k + 1) % n], top[(k + 1) % n], top[k]);
            Cap(bottom, c.AxisStart, reverse: true);
            Cap(top, c.AxisEnd, reverse: false);
        }

        public void Sweep(Em3dSweep s)
        {
            // One ring of shared vertices per path vertex — the rings' own values, so a joint face is
            // literally one set of vertices used by both prisms.
            var rings = new int[s.Rings.Count][];
            for (int r = 0; r < s.Rings.Count; r++)
            {
                var ring = s.Rings[r];
                rings[r] = new int[ring.Count];
                for (int k = 0; k < ring.Count; k++) rings[r][k] = V(ring[k]);
            }
            for (int r = 0; r + 1 < rings.Length; r++)
            {
                int n = rings[r].Length;
                for (int k = 0; k < n; k++)
                    Quad(rings[r][k], rings[r][(k + 1) % n], rings[r + 1][(k + 1) % n], rings[r + 1][k]);
            }
            Cap(rings[0], Centroid(s.Rings[0]), reverse: true);
            Cap(rings[^1], Centroid(s.Rings[^1]), reverse: false);
        }

        /// <summary>A sphere between two heights relative to its centre, <paramref name="zLo"/> and
        /// <paramref name="zHi"/> (each within ±r). A height strictly inside the sphere is a flat
        /// cut closed by a disc; a height at ±r is a pole.</summary>
        public void Sphere(Point3 c, double r, double zLo, double zHi)
        {
            double a0 = Math.Asin(Math.Clamp(zLo / r, -1, 1)), a1 = Math.Asin(Math.Clamp(zHi / r, -1, 1));
            int n = SphereSegments, m = SphereBands;
            bool southPole = zLo <= -r, northPole = zHi >= r;

            var rings = new List<int[]>();
            int south = -1, north = -1;
            for (int j = 0; j <= m; j++)
            {
                double lat = a0 + (a1 - a0) * j / m;
                if ((j == 0 && southPole) || (j == m && northPole))
                {
                    int pole = V(new Point3(c.X, c.Y, c.Z + (j == 0 ? -r : r)));
                    if (j == 0) south = pole; else north = pole;
                    rings.Add([]);
                    continue;
                }
                double z = j == 0 ? zLo : j == m ? zHi : r * Math.Sin(lat);
                double rho = r * Math.Cos(lat);
                var ring = new int[n];
                for (int k = 0; k < n; k++)
                {
                    double t = 2 * Math.PI * k / n;
                    ring[k] = V(new Point3(c.X + rho * Math.Cos(t), c.Y + rho * Math.Sin(t), c.Z + z));
                }
                rings.Add(ring);
            }

            for (int j = 0; j < m; j++)
            {
                var lo = rings[j]; var hi = rings[j + 1];
                for (int k = 0; k < n; k++)
                {
                    int k1 = (k + 1) % n;
                    if (lo.Length == 0)      T(south, hi[k1], hi[k]);
                    else if (hi.Length == 0) T(lo[k], lo[k1], north);
                    else                     Quad(lo[k], lo[k1], hi[k1], hi[k]);
                }
            }
            if (!southPole) Cap(rings[0], new Point3(c.X, c.Y, c.Z + zLo), reverse: true);
            if (!northPole) Cap(rings[^1], new Point3(c.X, c.Y, c.Z + zHi), reverse: false);
        }

        public void Extrusion(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes,
                              double zBottom, double zTop)
        {
            var rings = new List<IReadOnlyList<Point2>>(1 + holes.Count) { outline };
            rings.AddRange(holes);
            int total = 0;
            foreach (var r in rings) total += r.Count;
            int[] bottom = new int[total], top = new int[total];
            int k = 0;
            foreach (var r in rings)
                foreach (var p in r)
                {
                    bottom[k] = V(new Point3(p.X, p.Y, zBottom));
                    top[k]    = V(new Point3(p.X, p.Y, zTop));
                    k++;
                }

            foreach (var t in Em3dPolygonTriangulation.Triangulate(outline, holes))
            {
                T(top[t.A], top[t.B], top[t.C]);
                T(bottom[t.A], bottom[t.C], bottom[t.B]);
            }

            // Walls: outward, so the outline is walked counter-clockwise and a hole clockwise.
            int start = 0;
            for (int r = 0; r < rings.Count; r++)
            {
                var ring = rings[r];
                int n = ring.Count;
                bool forward = (Em3dPolygonTriangulation.SignedArea2(ring) > 0) == (r == 0);
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    if (ring[i] == ring[j]) continue;
                    var (a, c) = forward ? (start + i, start + j) : (start + j, start + i);
                    Quad(bottom[a], bottom[c], top[c], top[a]);
                }
                start += n;
            }
        }

        public void Flat(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes, double z)
        {
            int first = Vertices.Count;
            foreach (var p in outline) V(new Point3(p.X, p.Y, z));
            foreach (var h in holes) foreach (var p in h) V(new Point3(p.X, p.Y, z));
            foreach (var t in Em3dPolygonTriangulation.Triangulate(outline, holes))
                T(first + t.A, first + t.B, first + t.C);
        }

        /// <summary>A fan from <paramref name="centre"/> over a convex ring.</summary>
        private void Cap(int[] ring, Point3 centre, bool reverse)
        {
            int c = V(centre), n = ring.Length;
            for (int k = 0; k < n; k++)
                if (reverse) T(c, ring[(k + 1) % n], ring[k]);
                else         T(c, ring[k], ring[(k + 1) % n]);
        }
    }

    // ── small vector arithmetic ──────────────────────────────────────────────────────────────

    private static Point3 Add(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    private static Point3 Centroid(IReadOnlyList<Point3> ring)
    {
        double x = 0, y = 0, z = 0;
        foreach (var q in ring) { x += q.X; y += q.Y; z += q.Z; }
        return new Point3(x / ring.Count, y / ring.Count, z / ring.Count);
    }

    /// <summary>Two unit vectors perpendicular to <paramref name="axis"/> and to each other. For a
    /// vertical axis they are exactly x̂ and ŷ, so a via's facets sit on the coordinate axes.</summary>
    private static (Point3 U, Point3 W) Frame(Point3 axis)
    {
        double len = Math.Sqrt(axis.X * axis.X + axis.Y * axis.Y + axis.Z * axis.Z);
        var a = new Point3(axis.X / len, axis.Y / len, axis.Z / len);
        if (a.X == 0 && a.Y == 0) return (new Point3(1, 0, 0), new Point3(0, a.Z > 0 ? 1 : -1, 0));
        // The world axis least aligned with the cylinder's, crossed twice.
        var seed = Math.Abs(a.Z) < 0.9 ? new Point3(0, 0, 1) : new Point3(1, 0, 0);
        var u = Normalize(Cross(seed, a));
        return (u, Cross(a, u));
    }

    private static Point3 Cross(Point3 a, Point3 b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Point3 Normalize(Point3 a)
    {
        double l = Math.Sqrt(a.X * a.X + a.Y * a.Y + a.Z * a.Z);
        return new Point3(a.X / l, a.Y / l, a.Z / l);
    }
}
