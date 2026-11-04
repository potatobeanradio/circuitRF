// brief-dfr34-3 R-dfr34-3-1 — a vertical section through two points in plan: the picture a pad and a via that share no x or y
// line need, and no axis plane can give. The plane holds the line and z; the picture's horizontal is the distance along the line
// from its first point (FieldSectionLine.Along), its vertical z — the plot's own axes, so a field slice drawn over it
// (Em3dSectionField.Build) lands where the window draws it.
//
// The cut follows Section's rules where they carry over: an extruded polygon, a box and a vertical cylinder are cut exactly (their
// plan rings crossed by the line, swept between their heights), anything else through Em3dTessellation; a solid crossing the
// plane exactly at an edge counts on the half-open rule (a point exactly on the plane counts as behind it). There is no SNAP:
// the plane is stated by two points, not typed as a coordinate, and nothing moves it.

using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D.Fields;

namespace CircuitRF.Render;

public static partial class Em3dSectionScene
{
    private static Em3dScene LineSection(Em3dProblem problem, Em3dView view, Point3 fMin, Point3 fMax, double tol,
                                         List<string> dielectrics)
    {
        var line = view.Line ?? throw new ArgumentException("A SectionLine view states its line.", nameof(view));
        double Off(Point3 q) => line.Across(q.X, q.Y);
        Uv P(Point3 q) => new(line.Along(q.X, q.Y), q.Z);

        // Where the line crosses the rings in plan, as distances along it, sorted: consecutive pairs are the intervals inside.
        List<double> Crossings(IEnumerable<IReadOnlyList<Point2>> rings)
        {
            var hits = new List<double>();
            foreach (var r in rings)
                for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
                {
                    double a1 = line.Across(r[j].X, r[j].Y), a2 = line.Across(r[i].X, r[i].Y);
                    if (a1 > 0 == a2 > 0) continue;
                    double t = a1 / (a1 - a2);
                    hits.Add(line.Along(r[j].X + t * (r[i].X - r[j].X), r[j].Y + t * (r[i].Y - r[j].Y)));
                }
            hits.Sort();
            return hits;
        }

        var regions = new List<Em3dSceneRegion>();
        var lines = new List<Em3dSceneLine>();
        foreach (var s in problem.Solids)
        {
            void Rect(double u0, double v0, double u1, double v1) =>
                regions.Add(new Em3dSceneRegion(s.Name, s.Role, s.Material, s.Order, false,
                                                [[new Uv(u0, v0), new Uv(u1, v0), new Uv(u1, v1), new Uv(u0, v1)]]));
            void Swept(List<double> xs, double z0, double z1)
            {
                for (int i = 0; i + 1 < xs.Count; i += 2)
                    if (xs[i + 1] > xs[i]) Rect(xs[i], z0, xs[i + 1], z1);
            }

            switch (s.Primitive)
            {
                case Em3dExtrudedPolygon e:
                    Swept(Crossings([e.Outline, .. e.Holes]), e.ZBottom, e.ZTop);
                    break;
                case Em3dBox b:
                    Swept(Crossings([[new Point2(b.Min.X, b.Min.Y), new Point2(b.Max.X, b.Min.Y), new Point2(b.Max.X, b.Max.Y), new Point2(b.Min.X, b.Max.Y)]]),
                          b.Min.Z, b.Max.Z);
                    break;
                case Em3dCylinder c when c.AxisStart.X == c.AxisEnd.X && c.AxisStart.Y == c.AxisEnd.Y:
                {
                    double d = line.Across(c.AxisStart.X, c.AxisStart.Y);
                    if (Math.Abs(d) < c.Radius)
                    {
                        double hw = Math.Sqrt(c.Radius * c.Radius - d * d), mid = line.Along(c.AxisStart.X, c.AxisStart.Y);
                        Rect(mid - hw, Math.Min(c.AxisStart.Z, c.AxisEnd.Z), mid + hw, Math.Max(c.AxisStart.Z, c.AxisEnd.Z));
                    }
                    break;
                }
                default:
                    foreach (var ring in MeshCut(Em3dTessellation.Of(s), Off, 0))
                        regions.Add(new Em3dSceneRegion(s.Name, s.Role, s.Material, s.Order, false, [ring.Select(P).ToList()]));
                    break;
            }
        }

        foreach (var sh in problem.Sheets)
        {
            if (sh.Frame is not null)
            {
                // a sheet not lying flat: the segments where the plane crosses its triangles
                var mesh = Em3dTessellation.OfSheet(sh);
                foreach (var t in mesh.Triangles)
                {
                    var hits = new List<Point3>();
                    var tri = new[] { mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C] };
                    for (int k = 0; k < 3; k++)
                    {
                        var a = tri[k]; var b = tri[(k + 1) % 3];
                        double da = Off(a), db = Off(b);
                        if ((da < 0) == (db < 0) || da == db) continue;
                        double f = da / (da - db);
                        hits.Add(new Point3(a.X + f * (b.X - a.X), a.Y + f * (b.Y - a.Y), a.Z + f * (b.Z - a.Z)));
                    }
                    if (hits.Count == 2)
                        lines.Add(new Em3dSceneLine(sh.Name, Em3dRole.Conductor, sh.Material, sh.Order, true, P(hits[0]), P(hits[1]), sh.ThicknessM));
                }
                continue;
            }
            var xs = Crossings([sh.Outline, .. sh.Holes]);
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] > xs[i])
                    lines.Add(new Em3dSceneLine(sh.Name, Em3dRole.Conductor, sh.Material, sh.Order, true,
                                                new Uv(xs[i], sh.Z), new Uv(xs[i + 1], sh.Z), sh.ThicknessM));
        }

        // Paint order is precedence, exactly as an axis section paints it.
        var precedence = Em3dPrecedence.Of(problem);
        regions.Sort((a, b) => precedence.Of(a.Role, a.Order, a.IsSheet).CompareTo(precedence.Of(b.Role, b.Order, b.IsSheet)));

        // A port's box seen along the plane's normal: its extent along the line, and its height.
        var ports = problem.Ports.Select(p =>
        {
            double[] along = [line.Along(p.Min.X, p.Min.Y), line.Along(p.Max.X, p.Min.Y), line.Along(p.Max.X, p.Max.Y), line.Along(p.Min.X, p.Max.Y)];
            double u0 = along.Min(), u1 = along.Max();
            return new Em3dScenePort(p.Number, [new Uv(u0, p.Min.Z), new Uv(u1, p.Min.Z), new Uv(u1, p.Max.Z), new Uv(u0, p.Max.Z)]);
        }).ToList();

        // The frame: the line where it runs over the content's plan rectangle, and the content's height.
        var (s0, s1) = Clip(line, fMin, fMax);
        Uv fa = new(s0, fMin.Z), fc = new(s1, fMax.Z);
        var box = problem.Boundary;
        // Only the floor and the lid face the picture's sides; the four walls meet the plane at an angle (or not at all).
        double lidBeyond = Beyond(box.Max.Z - fMax.Z, tol), floorBeyond = Beyond(fMin.Z - box.Min.Z, tol);
        var faces = new List<Em3dSceneFace>
        {
            new("xmin", box.Faces.XMin, Em3dFaceSide.None, Beyond(fMin.X - box.Min.X, tol)),
            new("xmax", box.Faces.XMax, Em3dFaceSide.None, Beyond(box.Max.X - fMax.X, tol)),
            new("ymin", box.Faces.YMin, Em3dFaceSide.None, Beyond(fMin.Y - box.Min.Y, tol)),
            new("ymax", box.Faces.YMax, Em3dFaceSide.None, Beyond(box.Max.Y - fMax.Y, tol)),
            new("zmin", box.Faces.ZMin, Em3dFaceSide.Bottom, floorBeyond),
            new("zmax", box.Faces.ZMax, Em3dFaceSide.Top, lidBeyond),
        };
        var edges = new List<Em3dBoxEdge>
        {
            new(new Uv(fa.U, fa.V), new Uv(fc.U, fa.V), floorBeyond > 0),
            new(new Uv(fc.U, fa.V), new Uv(fc.U, fc.V), true),
            new(new Uv(fc.U, fc.V), new Uv(fa.U, fc.V), lidBeyond > 0),
            new(new Uv(fa.U, fc.V), new Uv(fa.U, fa.V), true),
        };
        return new Em3dScene(view with { At = 0 }, 0, tol, regions, lines, ports, edges, fa, fc, faces, dielectrics);
    }

    /// <summary>Where <paramref name="line"/> runs over the plan rectangle <paramref name="min"/>..<paramref name="max"/>, as
    /// distances along it — or, when it misses the rectangle, the rectangle's corners projected onto it.</summary>
    private static (double S0, double S1) Clip(FieldSectionLine line, Point3 min, Point3 max)
    {
        var (dx, dy) = line.Direction;
        double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
        bool Slab(double p0, double d, double a, double b)
        {
            if (Math.Abs(d) < 1e-15) return p0 >= a && p0 <= b;
            double t0 = (a - p0) / d, t1 = (b - p0) / d;
            if (t0 > t1) (t0, t1) = (t1, t0);
            lo = Math.Max(lo, t0); hi = Math.Min(hi, t1);
            return true;
        }
        if (Slab(line.X0, dx, min.X, max.X) && Slab(line.Y0, dy, min.Y, max.Y) && hi > lo) return (lo, hi);
        double[] corners = [line.Along(min.X, min.Y), line.Along(max.X, min.Y), line.Along(max.X, max.Y), line.Along(min.X, max.Y)];
        return (corners.Min(), corners.Max());
    }

    /// <summary>The caption's words for a two-point section: its two points, in plan.</summary>
    public static string LineText(FieldSectionLine line)
        => $"({FormatLength(line.X0)}, {FormatLength(line.Y0)}) and ({FormatLength(line.X1)}, {FormatLength(line.Y1)})";
}
