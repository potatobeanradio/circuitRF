// brief-hzn-12 Part 2 — a faceted sphere: the convex hull of a golden-spiral lattice of points on the sphere, which is what Hertzon
// builds for a sphere with NumberOfSegments above 0 (established by black-box runs of Hertzon's own geometry listing, never its
// source; src/Design/RESOLVED.md §HZN-12). Point j of N, counted from the top: z = 1 − (2j + 1)/N and azimuth −(j + ½)·g, with g
// the golden angle π(3 − √5). Every point lies on the sphere, and the solid is the hull of them all: 2N − 4 triangles.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

public static class C3dFacetedSphere
{
    /// <summary>The fewest points a faceted sphere has: four make a tetrahedron.</summary>
    public const int MinVertices = 4;

    /// <summary>The most: the hull is built in O(N²), and past this a true sphere is the better model anyway.</summary>
    public const int MaxVertices = 20000;

    /// <summary>The lattice on the unit sphere, point j of <paramref name="n"/> from the top.</summary>
    public static Point3[] Lattice(int n)
    {
        double g = Math.PI * (3 - Math.Sqrt(5));
        var p = new Point3[n];
        for (int j = 0; j < n; j++)
        {
            double z = 1 - (2.0 * j + 1) / n, r = Math.Sqrt(Math.Max(0, 1 - z * z)), phi = -(j + 0.5) * g;
            p[j] = new Point3(r * Math.Cos(phi), r * Math.Sin(phi), z);
        }
        return p;
    }

    /// <summary>The convex hull of <paramref name="points"/> (in general position on a sphere), as triangles wound outward.</summary>
    public static List<(int A, int B, int C)> Hull(IReadOnlyList<Point3> points)
    {
        int n = points.Count;
        if (n < 4) throw new ArgumentException("A hull needs at least four points.", nameof(points));
        double scale = 0;
        foreach (var q in points) scale = Math.Max(scale, Math.Max(Math.Abs(q.X), Math.Max(Math.Abs(q.Y), Math.Abs(q.Z))));
        double eps = 1e-12 * scale * scale * scale;

        // The first tetrahedron: point 0, the farthest from it, the farthest from that line, the farthest from that plane.
        int i0 = 0, i1 = Farthest(points, k => Len2(Sub(points[k], points[i0])));
        int i2 = Farthest(points, k => Len2(Cross(Sub(points[i1], points[i0]), Sub(points[k], points[i0]))));
        var n012 = Cross(Sub(points[i1], points[i0]), Sub(points[i2], points[i0]));
        int i3 = Farthest(points, k => Math.Abs(Dot(n012, Sub(points[k], points[i0]))));
        if (Math.Abs(Dot(n012, Sub(points[i3], points[i0]))) <= eps) throw new ArgumentException("The points are coplanar.", nameof(points));

        var faces = new List<(int A, int B, int C)>();
        void Add(int a, int b, int c, Point3 inside)
        {
            var nrm = Cross(Sub(points[b], points[a]), Sub(points[c], points[a]));
            faces.Add(Dot(nrm, Sub(inside, points[a])) > 0 ? (a, c, b) : (a, b, c));
        }
        var centre = Scale(Add3(Add3(points[i0], points[i1]), Add3(points[i2], points[i3])), 0.25);
        Add(i0, i1, i2, centre); Add(i0, i1, i3, centre); Add(i0, i2, i3, centre); Add(i1, i2, i3, centre);

        var used = new HashSet<int> { i0, i1, i2, i3 };
        for (int p = 0; p < n; p++)
        {
            if (used.Contains(p)) continue;
            var visible = new List<int>();
            for (int f = 0; f < faces.Count; f++)
            {
                var (a, b, c) = faces[f];
                var nrm = Cross(Sub(points[b], points[a]), Sub(points[c], points[a]));
                if (Dot(nrm, Sub(points[p], points[a])) > eps) visible.Add(f);
            }
            if (visible.Count == 0) continue;   // inside the hull so far
            var edges = new HashSet<(int, int)>();
            foreach (int f in visible)
            {
                var (a, b, c) = faces[f];
                edges.Add((a, b)); edges.Add((b, c)); edges.Add((c, a));
            }
            var horizon = edges.Where(e => !edges.Contains((e.Item2, e.Item1))).ToList();
            var gone = visible.ToHashSet();
            faces = [.. faces.Where((_, f) => !gone.Contains(f))];
            foreach (var (u, v) in horizon) faces.Add((u, v, p));
            used.Add(p);
        }
        return faces;
    }

    /// <summary>A faceted sphere as a polyhedron in its own frame, in DBU: the lattice scaled to the radius about the centre, every
    /// triangle named <c>surface</c> (the sphere's one face). Its placement and material are the sphere's.</summary>
    public static C3dPolyhedron Polyhedron(C3dSphere sphere)
    {
        var unit = Lattice(sphere.FacetVertices);
        var tris = Hull(unit);
        double r = sphere.Radius;
        var poly = new C3dPolyhedron
        {
            Name = sphere.Name, Material = sphere.Material, Placement = sphere.Placement,
            Vertices = [.. unit.Select(u => new C3dPoint3(sphere.Centre.X + (long)Math.Round(u.X * r),
                                                          sphere.Centre.Y + (long)Math.Round(u.Y * r),
                                                          sphere.Centre.Z + (long)Math.Round(u.Z * r)))],
            Faces = [.. tris.Select(t => new C3dFace { Name = C3dSphere.FaceNameList[0], Outer = [t.A, t.B, t.C] })],
        };
        return poly;
    }

    /// <summary>The faceted sphere in metres, centred at <paramref name="centre"/>, as the problem's polyhedron.</summary>
    public static Em3dPolyhedron Metres(Point3 centre, double radius, int vertices)
    {
        var unit = Lattice(vertices);
        return new Em3dPolyhedron(
            [.. unit.Select(u => new Point3(centre.X + u.X * radius, centre.Y + u.Y * radius, centre.Z + u.Z * radius))],
            [.. Hull(unit).Select(t => new Em3dFace([t.A, t.B, t.C], [], C3dSphere.FaceNameList[0]))]);
    }

    private static int Farthest(IReadOnlyList<Point3> points, Func<int, double> measure)
    {
        int best = 0;
        double most = double.NegativeInfinity;
        for (int k = 0; k < points.Count; k++)
            if (measure(k) is var m && m > most) (best, most) = (k, m);
        return best;
    }

    private static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Point3 Add3(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point3 Scale(Point3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static double Len2(Point3 a) => Dot(a, a);
    private static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}
