// brief-em3d-5 R-em3d5-2 — what a picture of a 3D problem SHOWS, before any of it is drawn: the cut
// of every solid by a plane, or every solid's outline projected isometrically.
//
// Kept apart from the drawing (Em3dSectionRenderer) so the geometry can be asked about directly — "at
// mid-substrate there is one substrate region and no copper" is a question about this scene, not about
// SVG text.
//
// ── The cut ───────────────────────────────────────────────────────────────────────────────────
//
// ANALYTIC where the primitive allows it (R-em3d5-2c): an extruded polygon, a box and a vertical
// cylinder are cut exactly. Everything else (a sweep, a sphere, a truncated sphere, a tilted
// cylinder) is cut through Em3dTessellation, the same triangles every other consumer sees.
//
// THE INTERFACE CONVENTION, stated once and applied everywhere: a solid is present at a plane when
// bottom ≤ plane < top along the plane's normal, and a sheet is present when the plane is within
// SnapTolerance of its height. The plane is first SNAPPED to any boundary within that tolerance, so
// "z = the top of the copper" typed as 578um lands exactly on the copper's top whatever the last bit
// of the DBU-to-metre conversion did, and reads as the layer above, every time. A vertex exactly on
// the plane counts as below it — which is what makes the half-open rule hold for the mesh cut too.
//
// ── The frame ─────────────────────────────────────────────────────────────────────────────────
//
// A view is framed on the CONTENT — conductors, sheets, ports, and anything not laterally as wide as
// the air box — padded by ContentPadFraction and clipped to the box. Not on the box: the generator pads
// it by an eighth of the longest wavelength, which for a 100 MHz start is 375 mm around a via a few
// millimetres across, and a picture of that would be a picture of a dot. A box face outside the frame
// is still named, with how far beyond the frame it lies.

using System.Globalization;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render;

/// <summary>Which picture of a 3D problem: a section normal to one axis, or the isometric outline.</summary>
public enum Em3dViewKind { SectionZ, SectionY, SectionX, Iso }

/// <summary>A view: its kind and, for a section, where the plane is along its normal, metres.</summary>
public readonly record struct Em3dView(Em3dViewKind Kind, double At)
{
    public static Em3dView Iso => new(Em3dViewKind.Iso, 0);

    /// <summary>The plane's normal axis, lower-case, or null for the isometric view.</summary>
    public string? Axis => Kind switch
    {
        Em3dViewKind.SectionZ => "z", Em3dViewKind.SectionY => "y", Em3dViewKind.SectionX => "x", _ => null,
    };

    /// <summary>The two axes the picture's horizontal and vertical are, e.g. "xz".</summary>
    public string Plane => Kind switch
    {
        Em3dViewKind.SectionZ => "xy", Em3dViewKind.SectionY => "xz", Em3dViewKind.SectionX => "yz", _ => "iso",
    };
}

/// <summary>A point in the picture's own plane, metres: (x, y), (x, z) or (y, z) for a section, the
/// isometric projection otherwise. V is up.</summary>
public readonly record struct Uv(double U, double V);

/// <summary>A filled piece of a section: one object's cut, as even-odd rings or as one circle.</summary>
public sealed record Em3dSceneRegion(
    string Object, Em3dRole Role, string Material, int Order, bool IsSheet,
    IReadOnlyList<IReadOnlyList<Uv>> Rings, Uv? CircleCentre = null, double CircleRadius = 0);

/// <summary>A stroked line: a sheet crossing a vertical section (<see cref="WidthM"/> is its real
/// thickness), or one edge of an isometric outline.</summary>
public sealed record Em3dSceneLine(string Object, Em3dRole Role, string Material, int Order, bool IsSheet,
                                   Uv A, Uv B, double WidthM);

/// <summary>A port's sheet projected onto the picture's plane.</summary>
public sealed record Em3dScenePort(int Number, IReadOnlyList<Uv> Outline);

/// <summary>Which side of the frame a face of the air box is written beside, if any.</summary>
public enum Em3dFaceSide { Left, Right, Bottom, Top, None }

/// <summary>One air-box face: what it does, where it is labelled, and how far beyond the frame it
/// lies (zero when the frame's side IS the face).</summary>
public sealed record Em3dSceneFace(string Face, Em3dBoundaryKind Kind, Em3dFaceSide Side, double BeyondM);

/// <summary>An edge of the air box (clipped to the frame) in the picture's plane. Dashed where it is
/// a cut of the frame rather than a face of the box.</summary>
public readonly record struct Em3dBoxEdge(Uv A, Uv B, bool Dashed);

/// <summary>Everything one picture shows, in picture coordinates.</summary>
/// <param name="At">The plane's position AFTER snapping (R-em3d5-2c).</param>
/// <param name="DielectricMaterials">Every non-conductor material in the problem, in problem order —
/// the index a material's fill colour is keyed by, so a material keeps its colour from one view to the
/// next.</param>
public sealed record Em3dScene(
    Em3dView View, double At, double SnapTolerance,
    IReadOnlyList<Em3dSceneRegion> Regions,
    IReadOnlyList<Em3dSceneLine> Lines,
    IReadOnlyList<Em3dScenePort> Ports,
    IReadOnlyList<Em3dBoxEdge> BoxEdges,
    Uv FrameMin, Uv FrameMax,
    IReadOnlyList<Em3dSceneFace> Faces,
    IReadOnlyList<string> DielectricMaterials)
{
    /// <summary>The names of the objects this picture draws, regions then lines, in draw order.</summary>
    public IEnumerable<string> Objects =>
        Regions.Select(r => r.Object).Concat(Lines.Select(l => l.Object)).Distinct(StringComparer.Ordinal);
}

public static class Em3dSectionScene
{
    /// <summary>The content's padding on every side, as a fraction of its largest dimension.</summary>
    public const double ContentPadFraction = 0.15;

    /// <summary>The plane snaps to a boundary within this fraction of the air box's largest side, and a
    /// sheet within it of the plane is drawn. A part in a billion: far below any drawn feature, far above
    /// a DBU-to-metre rounding.</summary>
    public const double SnapFraction = 1e-9;

    /// <summary>An isometric edge between two faces that turn by more than this is a SHARP edge and is
    /// drawn; a smaller turn is a facet of a curved surface and is drawn only on the silhouette.</summary>
    public const double SharpEdgeDegrees = 30.0;

    private static readonly double Cos30 = Math.Cos(Math.PI / 6), Sin30 = 0.5;
    private static readonly double CosSharp = Math.Cos(SharpEdgeDegrees * Math.PI / 180);

    /// <summary>The unit vector toward the isometric viewer: from +x, +y, +z.</summary>
    private static readonly Point3 Viewer = new(1 / Math.Sqrt(3), 1 / Math.Sqrt(3), 1 / Math.Sqrt(3));

    /// <summary>The scene <paramref name="view"/> shows of <paramref name="problem"/>.</summary>
    public static Em3dScene Build(Em3dProblem problem, Em3dView view)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var box = problem.Boundary;
        double largest = Math.Max(box.Max.X - box.Min.X, Math.Max(box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z));
        double tol = Math.Max(1e-15, SnapFraction * largest);
        var (fMin, fMax) = Frame(problem, tol);

        var dielectrics = problem.Solids.Where(s => s.Role == Em3dRole.Dielectric)
                                 .Select(s => s.Material).Distinct(StringComparer.Ordinal).ToList();

        return view.Kind == Em3dViewKind.Iso
            ? Iso(problem, fMin, fMax, tol, dielectrics)
            : Section(problem, view, fMin, fMax, tol, dielectrics);
    }

    // ── the frame ────────────────────────────────────────────────────────────────────────────

    /// <summary>The 3D box a view is framed on (see the file's header).</summary>
    public static (Point3 Min, Point3 Max) Frame(Em3dProblem problem, double tol)
    {
        var box = problem.Boundary;
        double x0 = double.PositiveInfinity, y0 = x0, z0 = x0, x1 = double.NegativeInfinity, y1 = x1, z1 = x1;
        void Grow(double a0, double b0, double c0, double a1, double b1, double c1)
        {
            x0 = Math.Min(x0, a0); y0 = Math.Min(y0, b0); z0 = Math.Min(z0, c0);
            x1 = Math.Max(x1, a1); y1 = Math.Max(y1, b1); z1 = Math.Max(z1, c1);
        }

        foreach (var s in problem.Solids)
        {
            // A slab is as wide as the box by construction, so it says nothing about where the content
            // is laterally; its height is taken below.
            if (IsSlab(s.Primitive, box, tol)) continue;
            var (a0, b0, c0, a1, b1, c1) = Em3dProblem.Bounds(s.Primitive);
            Grow(a0, b0, c0, a1, b1, c1);
        }
        foreach (var sh in problem.Sheets)
            foreach (var q in sh.Outline) Grow(q.X, q.Y, sh.Z, q.X, q.Y, sh.Z);
        foreach (var p in problem.Ports) Grow(p.Min.X, p.Min.Y, p.Min.Z, p.Max.X, p.Max.Y, p.Max.Z);

        // The slabs' heights — a substrate's, not the air's above the stack, which reaches the box's lid.
        foreach (var s in problem.Solids.Where(s => s.Role != Em3dRole.Air && IsSlab(s.Primitive, box, tol)))
        {
            var (_, _, c0, _, _, c1) = Em3dProblem.Bounds(s.Primitive);
            z0 = Math.Min(z0, c0); z1 = Math.Max(z1, c1);
        }

        if (double.IsInfinity(x0)) return (box.Min, box.Max);

        double pad = ContentPadFraction * Math.Max(x1 - x0, Math.Max(y1 - y0, z1 - z0));
        return (new Point3(Math.Max(box.Min.X, x0 - pad), Math.Max(box.Min.Y, y0 - pad), Math.Max(box.Min.Z, z0 - pad)),
                new Point3(Math.Min(box.Max.X, x1 + pad), Math.Min(box.Max.Y, y1 + pad), Math.Min(box.Max.Z, z1 + pad)));
    }

    /// <summary>A box as wide as the air box on both lateral axes — a stack slab or the air above it.</summary>
    private static bool IsSlab(Em3dPrimitive p, Em3dAirBox box, double tol)
        => p is Em3dBox b
           && Math.Abs(b.Min.X - box.Min.X) <= tol && Math.Abs(b.Max.X - box.Max.X) <= tol
           && Math.Abs(b.Min.Y - box.Min.Y) <= tol && Math.Abs(b.Max.Y - box.Max.Y) <= tol;

    // ── sections ─────────────────────────────────────────────────────────────────────────────

    private static Em3dScene Section(Em3dProblem problem, Em3dView view, Point3 fMin, Point3 fMax, double tol,
                                     List<string> dielectrics)
    {
        int axis = view.Kind switch { Em3dViewKind.SectionX => 0, Em3dViewKind.SectionY => 1, _ => 2 };
        double at = Snap(problem, axis, view.At, tol);

        // The picture's (u, v) for a 3D point: the two axes that are not the normal.
        Uv P(Point3 q) => axis switch { 2 => new Uv(q.X, q.Y), 1 => new Uv(q.X, q.Z), _ => new Uv(q.Y, q.Z) };

        var regions = new List<Em3dSceneRegion>();
        var lines   = new List<Em3dSceneLine>();

        foreach (var s in problem.Solids)
        {
            void Rings(IReadOnlyList<IReadOnlyList<Uv>> rings) =>
                regions.Add(new Em3dSceneRegion(s.Name, s.Role, s.Material, s.Order, false, rings));
            void Rect(double u0, double v0, double u1, double v1) =>
                Rings([[new Uv(u0, v0), new Uv(u1, v0), new Uv(u1, v1), new Uv(u0, v1)]]);

            switch (s.Primitive)
            {
                case Em3dExtrudedPolygon e when axis == 2:
                    if (e.ZBottom <= at && at < e.ZTop)
                        Rings([Ring2(e.Outline), .. e.Holes.Select(Ring2)]);
                    break;
                case Em3dExtrudedPolygon e:
                {
                    var xs = Crossings([e.Outline, .. e.Holes], at, axis);
                    for (int i = 0; i + 1 < xs.Count; i += 2)
                        if (xs[i + 1] > xs[i]) Rect(xs[i], e.ZBottom, xs[i + 1], e.ZTop);
                    break;
                }
                case Em3dBox b:
                {
                    double lo = axis == 0 ? b.Min.X : axis == 1 ? b.Min.Y : b.Min.Z;
                    double hi = axis == 0 ? b.Max.X : axis == 1 ? b.Max.Y : b.Max.Z;
                    if (lo <= at && at < hi)
                    {
                        var (a, c) = (P(b.Min), P(b.Max));
                        Rect(a.U, a.V, c.U, c.V);
                    }
                    break;
                }
                case Em3dCylinder c when c.AxisStart.X == c.AxisEnd.X && c.AxisStart.Y == c.AxisEnd.Y:
                {
                    double zLo = Math.Min(c.AxisStart.Z, c.AxisEnd.Z), zHi = Math.Max(c.AxisStart.Z, c.AxisEnd.Z);
                    if (axis == 2)
                    {
                        if (zLo <= at && at < zHi)
                            regions.Add(new Em3dSceneRegion(s.Name, s.Role, s.Material, s.Order, false, [],
                                                            new Uv(c.AxisStart.X, c.AxisStart.Y), c.Radius));
                    }
                    else
                    {
                        double centre = axis == 1 ? c.AxisStart.Y : c.AxisStart.X;
                        double across = axis == 1 ? c.AxisStart.X : c.AxisStart.Y;
                        double d = at - centre;
                        if (Math.Abs(d) < c.Radius)
                        {
                            double hw = Math.Sqrt(c.Radius * c.Radius - d * d);
                            Rect(across - hw, zLo, across + hw, zHi);
                        }
                    }
                    break;
                }
                default:
                    foreach (var ring in MeshCut(Em3dTessellation.Of(s), axis, at))
                        Rings([ring.Select(P).ToList()]);
                    break;
            }
        }

        foreach (var sh in problem.Sheets)
        {
            if (axis == 2)
            {
                if (Math.Abs(sh.Z - at) <= tol)
                    regions.Add(new Em3dSceneRegion(sh.Name, Em3dRole.Conductor, sh.Material, sh.Order, true,
                                                    [Ring2(sh.Outline), .. sh.Holes.Select(Ring2)]));
                continue;
            }
            var xs = Crossings([sh.Outline, .. sh.Holes], at, axis);
            for (int i = 0; i + 1 < xs.Count; i += 2)
                if (xs[i + 1] > xs[i])
                    lines.Add(new Em3dSceneLine(sh.Name, Em3dRole.Conductor, sh.Material, sh.Order, true,
                                                new Uv(xs[i], sh.Z), new Uv(xs[i + 1], sh.Z), sh.ThicknessM));
        }

        // Construction order is paint order: where two solids overlap the later one wins the volume
        // (R-em3d3-1d), so painting it later shows exactly the solid that owns each point.
        regions.Sort((a, b) => a.Order.CompareTo(b.Order));

        var ports = problem.Ports.Select(p =>
        {
            var (a, c) = (P(p.Min), P(p.Max));
            return new Em3dScenePort(p.Number, [a, new Uv(c.U, a.V), c, new Uv(a.U, c.V)]);
        }).ToList();

        var (fa, fc) = (P(fMin), P(fMax));
        var box = problem.Boundary;
        string[] names = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
        Em3dBoundaryKind[] kinds = [box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax];
        double[] beyond =
        [
            fMin.X - box.Min.X, box.Max.X - fMax.X, fMin.Y - box.Min.Y,
            box.Max.Y - fMax.Y, fMin.Z - box.Min.Z, box.Max.Z - fMax.Z,
        ];
        // The two in-plane axes: which face is on which side of the picture.
        int uAxis = axis == 0 ? 1 : 0, vAxis = axis == 2 ? 1 : 2;
        var faces = new List<Em3dSceneFace>();
        for (int f = 0; f < 6; f++)
        {
            int faceAxis = f / 2;
            bool isMax = f % 2 == 1;
            var side = faceAxis == uAxis ? (isMax ? Em3dFaceSide.Right : Em3dFaceSide.Left)
                     : faceAxis == vAxis ? (isMax ? Em3dFaceSide.Top : Em3dFaceSide.Bottom)
                     : Em3dFaceSide.None;
            faces.Add(new Em3dSceneFace(names[f], kinds[f], side, Beyond(beyond[f], tol)));
        }

        bool Cut(Em3dFaceSide side) => faces.First(f => f.Side == side).BeyondM > 0;
        var edges = new List<Em3dBoxEdge>
        {
            new(new Uv(fa.U, fa.V), new Uv(fc.U, fa.V), Cut(Em3dFaceSide.Bottom)),
            new(new Uv(fc.U, fa.V), new Uv(fc.U, fc.V), Cut(Em3dFaceSide.Right)),
            new(new Uv(fc.U, fc.V), new Uv(fa.U, fc.V), Cut(Em3dFaceSide.Top)),
            new(new Uv(fa.U, fc.V), new Uv(fa.U, fa.V), Cut(Em3dFaceSide.Left)),
        };

        return new Em3dScene(view with { At = at }, at, tol, regions, lines, ports, edges, fa, fc, faces, dielectrics);
    }

    /// <summary>The plane, moved onto any boundary along its normal within the tolerance.</summary>
    private static double Snap(Em3dProblem problem, int axis, double at, double tol)
    {
        double best = at, bestD = tol;
        void Try(double c)
        {
            double d = Math.Abs(c - at);
            if (d <= bestD) { best = c; bestD = d; }
        }
        foreach (var s in problem.Solids)
        {
            var b = Em3dProblem.Bounds(s.Primitive);
            Try(axis == 0 ? b.X0 : axis == 1 ? b.Y0 : b.Z0);
            Try(axis == 0 ? b.X1 : axis == 1 ? b.Y1 : b.Z1);
            if (s.Primitive is Em3dExtrudedPolygon e && axis != 2)
                foreach (var q in e.Outline) Try(axis == 0 ? q.X : q.Y);
        }
        foreach (var sh in problem.Sheets)
        {
            if (axis == 2) Try(sh.Z);
            else foreach (var q in sh.Outline) Try(axis == 0 ? q.X : q.Y);
        }
        return best;
    }

    private static double Beyond(double d, double tol) => d > tol ? d : 0;

    private static IReadOnlyList<Uv> Ring2(IReadOnlyList<Point2> ring) => [.. ring.Select(q => new Uv(q.X, q.Y))];

    /// <summary>
    /// Where the line (coordinate <paramref name="axis"/> = <paramref name="at"/>, axis 0 = x, 1 = y)
    /// crosses the rings, as values of the OTHER lateral coordinate, sorted — so consecutive pairs are
    /// the intervals inside. An edge crosses when exactly one end is ABOVE the line, which is the
    /// half-open rule: a rectangle from y0 to y1 is cut at y0 and not at y1.
    /// </summary>
    private static List<double> Crossings(IReadOnlyList<IReadOnlyList<Point2>> rings, double at, int axis)
    {
        var hits = new List<double>();
        foreach (var r in rings)
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
            {
                double a1 = axis == 0 ? r[j].X : r[j].Y, b1 = axis == 0 ? r[j].Y : r[j].X;
                double a2 = axis == 0 ? r[i].X : r[i].Y, b2 = axis == 0 ? r[i].Y : r[i].X;
                if (a1 > at == a2 > at) continue;
                hits.Add(b1 + (at - a1) * (b2 - b1) / (a2 - a1));
            }
        hits.Sort();
        return hits;
    }

    /// <summary>
    /// The closed loops a plane cuts from a welded triangle mesh, in 3D. Each crossing point is keyed by
    /// the EDGE it lies on and computed from that edge's lower-indexed vertex, so the two triangles
    /// sharing an edge produce the same point bit for bit and the loops close exactly.
    /// </summary>
    public static List<List<Point3>> MeshCut(Em3dTriangleMesh mesh, int axis, double at)
    {
        double C(Point3 q) => axis == 0 ? q.X : axis == 1 ? q.Y : q.Z;
        var point = new Dictionary<(int, int), Point3>();
        var links = new Dictionary<(int, int), List<(int, int)>>();

        (int, int)? Hit(int a, int b)
        {
            var va = mesh.Vertices[a]; var vb = mesh.Vertices[b];
            if (C(va) > at == C(vb) > at) return null;
            var key = a < b ? (a, b) : (b, a);
            if (!point.ContainsKey(key))
            {
                var p = mesh.Vertices[key.Item1]; var q = mesh.Vertices[key.Item2];
                double t = (at - C(p)) / (C(q) - C(p));
                point[key] = new Point3(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, p.Z + (q.Z - p.Z) * t);
            }
            return key;
        }

        foreach (var t in mesh.Triangles)
        {
            var found = new List<(int, int)>(2);
            foreach (var e in new[] { Hit(t.A, t.B), Hit(t.B, t.C), Hit(t.C, t.A) })
                if (e is { } k) found.Add(k);
            if (found.Count != 2) continue;
            (links.TryGetValue(found[0], out var l0) ? l0 : links[found[0]] = []).Add(found[1]);
            (links.TryGetValue(found[1], out var l1) ? l1 : links[found[1]] = []).Add(found[0]);
        }

        var loops = new List<List<Point3>>();
        var used = new HashSet<(int, int)>();
        // Deterministic starting order: the edge keys, sorted.
        foreach (var start in links.Keys.OrderBy(k => k.Item1).ThenBy(k => k.Item2))
        {
            if (used.Contains(start)) continue;
            var loop = new List<Point3>();
            var prev = start;
            var cur = start;
            bool closed = false;
            while (true)
            {
                used.Add(cur);
                loop.Add(point[cur]);
                var next = links[cur].FirstOrDefault(n => n != prev && !used.Contains(n));
                if (next == default)
                {
                    closed = links[cur].Contains(start) && loop.Count > 2;
                    break;
                }
                prev = cur;
                cur = next;
            }
            if (closed) loops.Add(loop);
        }
        return loops;
    }

    // ── the isometric outline ─────────────────────────────────────────────────────────────────

    /// <summary>The isometric projection: the viewer is at +x, +y, +z, looking at the origin.</summary>
    public static Uv Project(Point3 q) => new((q.X - q.Y) * Cos30, q.Z - (q.X + q.Y) * Sin30);

    private static Em3dScene Iso(Em3dProblem problem, Point3 fMin, Point3 fMax, double tol, List<string> dielectrics)
    {
        var lines = new List<Em3dSceneLine>();
        var box = problem.Boundary;

        foreach (var s in problem.Solids)
        {
            void Edge(Point3 a, Point3 b) =>
                lines.Add(new Em3dSceneLine(s.Name, s.Role, s.Material, s.Order, false, Project(a), Project(b), 0));

            switch (s.Primitive)
            {
                case Em3dExtrudedPolygon e:
                    foreach (var ring in new[] { e.Outline }.Concat(e.Holes))
                        PrismEdges(ring, e.ZBottom, e.ZTop, Edge);
                    break;
                case Em3dBox b:
                {
                    // Clipped to the frame: a slab is as wide as the air box, which is the one thing a
                    // frame exists to leave out.
                    var lo = new Point3(Math.Max(b.Min.X, fMin.X), Math.Max(b.Min.Y, fMin.Y), Math.Max(b.Min.Z, fMin.Z));
                    var hi = new Point3(Math.Min(b.Max.X, fMax.X), Math.Min(b.Max.Y, fMax.Y), Math.Min(b.Max.Z, fMax.Z));
                    if (hi.X > lo.X && hi.Y > lo.Y && hi.Z > lo.Z)
                        foreach (var (a, c) in BoxEdges(lo, hi)) Edge(a, c);
                    break;
                }
                default:
                    MeshEdges(Em3dTessellation.Of(s), Edge);
                    break;
            }
        }

        foreach (var sh in problem.Sheets)
            foreach (var ring in new[] { sh.Outline }.Concat(sh.Holes))
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    lines.Add(new Em3dSceneLine(sh.Name, Em3dRole.Conductor, sh.Material, sh.Order, true,
                                                Project(new Point3(ring[j].X, ring[j].Y, sh.Z)),
                                                Project(new Point3(ring[i].X, ring[i].Y, sh.Z)), sh.ThicknessM));

        lines.Sort((a, b) => a.Order.CompareTo(b.Order));

        var ports = problem.Ports.Select(p =>
        {
            // The sheet has exactly one zero-extent axis; its corners walk the other two.
            Point3[] c = p.Min.X == p.Max.X
                ? [p.Min, new(p.Min.X, p.Max.Y, p.Min.Z), p.Max, new(p.Min.X, p.Min.Y, p.Max.Z)]
                : p.Min.Y == p.Max.Y
                    ? [p.Min, new(p.Max.X, p.Min.Y, p.Min.Z), p.Max, new(p.Min.X, p.Min.Y, p.Max.Z)]
                    : [p.Min, new(p.Max.X, p.Min.Y, p.Min.Z), p.Max, new(p.Min.X, p.Max.Y, p.Min.Z)];
            return new Em3dScenePort(p.Number, [.. c.Select(Project)]);
        }).ToList();

        var edges = BoxEdges(fMin, fMax).Select(e => new Em3dBoxEdge(Project(e.A), Project(e.B), true)).ToList();

        double u0 = double.PositiveInfinity, v0 = u0, u1 = double.NegativeInfinity, v1 = u1;
        foreach (var e in edges)
            foreach (var q in new[] { e.A, e.B })
            { u0 = Math.Min(u0, q.U); v0 = Math.Min(v0, q.V); u1 = Math.Max(u1, q.U); v1 = Math.Max(v1, q.V); }

        string[] names = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
        Em3dBoundaryKind[] kinds = [box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax];
        double[] beyond =
        [
            fMin.X - box.Min.X, box.Max.X - fMax.X, fMin.Y - box.Min.Y,
            box.Max.Y - fMax.Y, fMin.Z - box.Min.Z, box.Max.Z - fMax.Z,
        ];
        var faces = names.Select((n, f) => new Em3dSceneFace(n, kinds[f], Em3dFaceSide.None, Beyond(beyond[f], tol))).ToList();

        return new Em3dScene(Em3dView.Iso, 0, tol, [], lines, ports, edges, new Uv(u0, v0), new Uv(u1, v1),
                             faces, dielectrics);
    }

    /// <summary>A prism's outline: both rings, and the vertical edge at every vertex where the side
    /// turns sharply or where it is on the silhouette.</summary>
    private static void PrismEdges(IReadOnlyList<Point2> ring, double z0, double z1, Action<Point3, Point3> edge)
    {
        int n = ring.Count;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            edge(new Point3(ring[j].X, ring[j].Y, z0), new Point3(ring[i].X, ring[i].Y, z0));
            edge(new Point3(ring[j].X, ring[j].Y, z1), new Point3(ring[i].X, ring[i].Y, z1));
        }
        for (int i = 0; i < n; i++)
        {
            var before = SideNormal(ring[(i - 1 + n) % n], ring[i]);
            var after  = SideNormal(ring[i], ring[(i + 1) % n]);
            bool sharp = before.X * after.X + before.Y * after.Y < CosSharp;
            bool silhouette = Facing(before.X, before.Y) != Facing(after.X, after.Y);
            if (sharp || silhouette)
                edge(new Point3(ring[i].X, ring[i].Y, z0), new Point3(ring[i].X, ring[i].Y, z1));
        }

        static Point2 SideNormal(Point2 a, Point2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l = Math.Sqrt(dx * dx + dy * dy);
            return l > 0 ? new Point2(dy / l, -dx / l) : new Point2(0, 0);
        }
        static bool Facing(double nx, double ny) => nx * Viewer.X + ny * Viewer.Y > 0;
    }

    /// <summary>A welded mesh's outline: every sharp edge, every silhouette edge, and any edge with
    /// fewer or more than two faces (which a closed mesh does not have, and which is drawn rather
    /// than hidden if one ever does).</summary>
    private static void MeshEdges(Em3dTriangleMesh mesh, Action<Point3, Point3> edge)
    {
        var normals = mesh.Triangles.Select(t => Normal(mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C])).ToList();
        var faces = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < mesh.Triangles.Count; k++)
        {
            var t = mesh.Triangles[k];
            foreach (var (a, b) in new[] { (t.A, t.B), (t.B, t.C), (t.C, t.A) })
            {
                var key = a < b ? (a, b) : (b, a);
                (faces.TryGetValue(key, out var l) ? l : faces[key] = []).Add(k);
            }
        }
        foreach (var (key, list) in faces.OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2))
        {
            bool draw;
            if (list.Count != 2) draw = true;
            else
            {
                var n0 = normals[list[0]]; var n1 = normals[list[1]];
                double cos = n0.X * n1.X + n0.Y * n1.Y + n0.Z * n1.Z;
                draw = cos < CosSharp || Dot(n0, Viewer) > 0 != Dot(n1, Viewer) > 0;
            }
            if (draw) edge(mesh.Vertices[key.Item1], mesh.Vertices[key.Item2]);
        }
    }

    private static IEnumerable<(Point3 A, Point3 B)> BoxEdges(Point3 lo, Point3 hi)
    {
        Point3 V(int k) => new((k & 1) == 0 ? lo.X : hi.X, (k & 2) == 0 ? lo.Y : hi.Y, (k & 4) == 0 ? lo.Z : hi.Z);
        for (int k = 0; k < 8; k++)
            foreach (int bit in new[] { 1, 2, 4 })
                if ((k & bit) == 0) yield return (V(k), V(k | bit));
    }

    private static Point3 Normal(Point3 a, Point3 b, Point3 c)
    {
        double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
        double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        return l > 0 ? new Point3(nx / l, ny / l, nz / l) : new Point3(0, 0, 0);
    }

    private static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>A length for a label: the unit its size reads in, four significant figures.</summary>
    public static string FormatLength(double m)
    {
        double a = Math.Abs(m);
        var (scale, unit) = a == 0 ? (1e6, "µm") : a >= 1 ? (1.0, "m") : a >= 1e-3 ? (1e3, "mm")
                          : a >= 1e-6 ? (1e6, "µm") : (1e9, "nm");
        return (m * scale).ToString("G4", CultureInfo.InvariantCulture) + " " + unit;
    }
}
