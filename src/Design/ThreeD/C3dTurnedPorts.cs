// A lumped port turned in its plane: a rectangle at any angle in the XY plane, for a gap between pads that are not square
// to the axes.
//
// WHY ONLY A RECTANGLE. A lumped port drives one uniform field along one direction across its sheet and turns voltage and
// current into impedance with the sheet's length along that direction and its width across it. Both mean something only
// when the two ends are straight, parallel and the same distance apart everywhere: a rectangle. Palace measures the sheet
// the same way (a box fitted to its corners, the length checked against the sheet's own extent to one part in a million)
// and stops the run otherwise. Make Port fits a drawn polygon to one and refuses anything else, saying what is wrong.
//
// WHY THE COPPER TAKES THE PORT'S CORNERS. A turned rectangle is exact in floating point, and copper read from a layout
// is not: its corners were rounded to the layout's grid, so a turned pad's side is a nanometre off any line the port
// could be drawn on. The mesher joins two surfaces only where they coincide (to 0.1 pm), so a port end a nanometre short
// of its copper leaves a sliver of air, and one a nanometre inside it leaves a piece of sheet that bounds nothing. So each
// end must lie along ONE straight side of its copper to within the contact tolerance, and the solved problem's outline
// of that copper is given the end's two corners in place of the stretch of side between them: the copper moves by at
// most that tolerance, and the port and the copper share the same points exactly. The layout is not changed.

using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>One end of a turned port: the two corners the outline of <paramref name="Conductor"/> takes, world metres in
/// plan.</summary>
public sealed record C3dSeam(string Conductor, Point2 A, Point2 B);

public static class C3dTurnedPorts
{
    /// <summary>
    /// How far a turned port's end may lie from its copper's side and still be on it: two DBU, and Robust meshing's largest move.
    /// One DBU is an upright port's; a turned one is a rectangle rounded to the grid at an angle (up to three quarters of a DBU)
    /// meeting copper whose corners were rounded too (up to seven tenths), so a port drawn exactly on its copper can be more
    /// than one DBU from it.
    /// </summary>
    public static double Tolerance(C3dPortContext ctx) => 2 * ctx.Dbu + ctx.OutlineMoveM;

    /// <summary>Why <paramref name="port"/>, whose Angle is not 0, cannot be turned; null when it can.</summary>
    internal static string? AngleRefusal(C3dPort port, string label)
    {
        if (!double.IsFinite(port.Angle)) return $"{label}'s Angle is {port.Angle}; write a number of degrees.";
        if (port.Kind == Em3dPortKind.Wave)
            return $"{label} is a wave port with an Angle of {Deg(port.Angle)}°. A wave port is a rectangle of an air-box face, " +
                   "square to its edges; set its Angle to 0.";
        if (port.Cylinder is not null)
            return $"{label} is a gap on a cylinder with an Angle of {Deg(port.Angle)}°; a gap on a cylinder is not turned. Set its Angle to 0.";
        if (port.Plane != C3dPlane.XY)
            return $"{label} lies in the {port.Plane} plane with an Angle of {Deg(port.Angle)}°. A port is turned only in the XY " +
                   "plane, where a board's pads lie; set its Angle to 0.";
        return null;
    }

    /// <summary>The port's two in-plane axes, world: its plane's, turned by its Angle when it is a turned port.</summary>
    public static (Point3 U, Point3 V) Axes(C3dPort port)
    {
        if (port.Angle == 0 || port.Plane != C3dPlane.XY) return C3dPorts.PlaneAxes(port.Plane);
        var (_, u, v) = Map(port.Angle, 0, 0, 0, 0, 0);
        return (u, v);
    }

    /// <summary>The map from the port's rectangle before it is turned to the world, turned by <paramref name="degrees"/>
    /// about the rectangle's centre at height <paramref name="h"/>, and the two turned axes.</summary>
    internal static (Func<double, double, Point3> P, Point3 U, Point3 V) Map(double degrees, double u0, double v0, double u1, double v1, double h)
    {
        double t = degrees * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        double uc = (u0 + u1) / 2, vc = (v0 + v1) / 2;
        return ((u, v) => new Point3(uc + (u - uc) * c - (v - vc) * s, vc + (u - uc) * s + (v - vc) * c, h),
                new Point3(c, s, 0), new Point3(-s, c, 0));
    }

    /// <summary>
    /// A turned port's two ends checked against their copper, and the seams that make them meet it exactly: each end must lie
    /// on copper drawn as an outline (<see cref="C3dPortContext.Conductor.Prism"/>), at a height within it, along one straight
    /// side of that outline. Returns the refusal, or null with <paramref name="seams"/>.
    /// </summary>
    internal static string? Ends(string label, Em3dPortQuad quad, Point3 direction, string negative, string positive, C3dPortContext ctx,
                                 out IReadOnlyList<C3dSeam> seams)
    {
        seams = [];
        var (neg, pos) = quad.Ends(direction);
        var found = new List<C3dSeam>(2);
        double tol = Tolerance(ctx);
        foreach (var (name, (p, q), which) in new[] { (negative, neg, "negative"), (positive, pos, "positive") })
        {
            var c = ctx.Conductors.FirstOrDefault(k => k.Name == name);
            if (c?.Prism is not { } prism)
                return $"{label} is turned, and its {which} end is on '{name}', which is {KindOf(c)}. A turned port ends on copper " +
                       "drawn as an outline (a layout's copper, or an extruded polygon), whose side its end lies along.";
            double z = p.Z;
            if (z < prism.ZBottom - tol || z > prism.ZTop + tol)
                return $"{label} is turned, and its {which} end is at z = {Um(z)} µm, outside '{name}' ({Um(prism.ZBottom)} to " +
                       $"{Um(prism.ZTop)} µm). Put the port at a height within the copper it ends on.";
            var a = new Point2(p.X, p.Y);
            var b = new Point2(q.X, q.Y);
            if (Seam(prism, a, b, tol) is null)
                return $"{label} is turned, and its {which} end, from ({Mm(a.X)}, {Mm(a.Y)}) to ({Mm(b.X)}, {Mm(b.Y)}) mm, does not lie " +
                       $"along one straight side of '{name}' to within {Um(tol)} µm: it meets that copper at a corner or a curve, or " +
                       "reaches past the end of its side. Draw the port so each end lies along a straight side of its copper.";
            found.Add(new C3dSeam(name, a, b));
        }
        seams = found;
        return null;
    }

    /// <summary>The solids of a solved problem with every seam of <paramref name="seams"/> applied, or the refusal.</summary>
    public static string? Apply(List<Em3dSolid> solids, IEnumerable<C3dSeam> seams, double tol)
    {
        foreach (var seam in seams)
        {
            int at = solids.FindIndex(s => s.Name == seam.Conductor);
            if (at < 0 || solids[at].Primitive is not Em3dExtrudedPolygon prism)
                return $"'{seam.Conductor}', which a turned port ends on, is not in the solved problem as an outline.";
            if (Seam(prism, seam.A, seam.B, tol) is not { } joined)
                return $"Two turned ports end on the same side of '{seam.Conductor}' and cannot both meet it exactly.";
            solids[at] = solids[at] with { Primitive = joined };
        }
        return null;
    }

    /// <summary>
    /// <paramref name="prism"/> with the stretch of one of its rings that runs from <paramref name="a"/> to <paramref name="b"/>
    /// replaced by those two points; null when no ring has both within <paramref name="tol"/> of it with every vertex between
    /// them within <paramref name="tol"/> of the line a–b.
    /// </summary>
    internal static Em3dExtrudedPolygon? Seam(Em3dExtrudedPolygon prism, Point2 a, Point2 b, double tol)
    {
        if (SeamRing(prism.Outline, a, b, tol) is { } outer) return prism with { Outline = outer };
        for (int k = 0; k < prism.Holes.Count; k++)
            if (SeamRing(prism.Holes[k], a, b, tol) is { } hole)
            {
                var holes = prism.Holes.ToList();
                holes[k] = hole;
                return prism with { Holes = holes };
            }
        return null;
    }

    internal static List<Point2>? SeamRing(IReadOnlyList<Point2> ring, Point2 a, Point2 b, double tol)
    {
        int n = ring.Count;
        if (n < 3) return null;
        int sa = Nearest(ring, a, tol), sb = Nearest(ring, b, tol);
        if (sa < 0 || sb < 0) return null;
        if (sa == sb)
        {
            // Both on one side: in the side's own order.
            var (p, q) = (ring[sa], ring[(sa + 1) % n]);
            double ta = (a.X - p.X) * (q.X - p.X) + (a.Y - p.Y) * (q.Y - p.Y);
            double tb = (b.X - p.X) * (q.X - p.X) + (b.Y - p.Y) * (q.Y - p.Y);
            return ta <= tb ? Build(sa, sa, a, b) : Build(sa, sa, b, a);
        }
        return Build(sa, sb, a, b) ?? Build(sb, sa, b, a);

        // The ring walked from side `from` (which holds `first`) to side `to` (which holds `second`): every vertex strictly
        // after `from`'s start up to `to`'s start lies between them and must be on the line; they are replaced by the two
        // points. A vertex left beside either point within the tolerance is that corner itself and goes too.
        List<Point2>? Build(int from, int to, Point2 first, Point2 second)
        {
            int between = (to - from + n) % n;
            if (between > n - 2) return null;
            for (int j = 1; j <= between; j++)
                if (SegmentDistance(ring[(from + j) % n], a, b) > tol) return null;
            var o = new List<Point2>(n + 2);
            for (int j = 0; j < n - between; j++) o.Add(ring[(to + 1 + j) % n]);
            if (o.Count > 2 && Distance(o[^1], first) <= tol) o.RemoveAt(o.Count - 1);
            if (o.Count > 2 && Distance(o[0], second) <= tol) o.RemoveAt(0);
            o.Add(first);
            o.Add(second);
            return o.Count >= 3 ? o : null;
        }
    }

    /// <summary>The side of <paramref name="ring"/> nearest <paramref name="q"/>, by the index of its first vertex, or −1 when
    /// none is within <paramref name="tol"/>.</summary>
    private static int Nearest(IReadOnlyList<Point2> ring, Point2 q, double tol)
    {
        int best = -1;
        double bd = double.PositiveInfinity;
        for (int i = 0; i < ring.Count; i++)
        {
            double d = SegmentDistance(q, ring[i], ring[(i + 1) % ring.Count]);
            if (d < bd) (bd, best) = (d, i);
        }
        return bd <= tol ? best : -1;
    }

    private static double SegmentDistance(Point2 q, Point2 p0, Point2 p1)
    {
        double dx = p1.X - p0.X, dy = p1.Y - p0.Y, l2 = dx * dx + dy * dy;
        double t = l2 > 0 ? Math.Clamp(((q.X - p0.X) * dx + (q.Y - p0.Y) * dy) / l2, 0, 1) : 0;
        double ex = p0.X + t * dx - q.X, ey = p0.Y + t * dy - q.Y;
        return Math.Sqrt(ex * ex + ey * ey);
    }

    private static double Distance(Point2 p, Point2 q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));

    // ── Make Port on a drawn polygon ─────────────────────────────────────────────────────────

    /// <summary>
    /// The port rectangle a flat polygon in the XY plane is, before it is turned, and its angle — or why it is not one. The
    /// polygon's corners (points on a straight run between two others are not corners) must be four, with opposite sides
    /// parallel and equal and every corner square, each to within <paramref name="tolM"/>: then its two ends are parallel and
    /// every path between them is the same length, which is what a lumped port needs. The angle is the one of the four that
    /// lies within 45° of 0; a rectangle that is upright to within a part in a million is not turned at all.
    /// </summary>
    /// <param name="corners">The polygon's points in order round it, world metres in plan.</param>
    /// <param name="dbuPerMicron">The document's DBU, which the rectangle is written in.</param>
    public static (C3dRect Rect, double Angle)? Fit(IReadOnlyList<Point2> corners, int dbuPerMicron, double tolM, out string? why)
    {
        why = null;
        var c = Corners(corners, tolM);
        if (c.Count != 4)
        {
            why = $"has {c.Count} corners, and a port is a rectangle: four corners, its two ends parallel and every path between " +
                  "them the same length.";
            return null;
        }
        Point2 S(int i) => new(c[(i + 1) % 4].X - c[i].X, c[(i + 1) % 4].Y - c[i].Y);
        double L(Point2 v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);
        // Opposite sides parallel: else its width changes along it.
        for (int i = 0; i < 2; i++)
        {
            var (p, q) = (S(i), S(i + 2));
            double cross = Math.Abs(p.X * q.Y - p.Y * q.X) / Math.Max(L(p), L(q));
            if (cross > tolM)
            {
                // Across the other pair of sides, the distance between these two at each end.
                double w0 = LineDistance(c[(i + 3) % 4], c[i], c[(i + 1) % 4]), w1 = LineDistance(c[(i + 2) % 4], c[i], c[(i + 1) % 4]);
                why = $"is not a rectangle: its width runs from {Um(Math.Min(w0, w1))} to {Um(Math.Max(w0, w1))} µm along it, and a " +
                      "port's width is the same everywhere.";
                return null;
            }
        }
        // Square corners: else its two ends are offset, and the paths between them are not all the same length.
        var (u, v) = (S(0), S(1));
        double skew = Math.Abs(u.X * v.X + u.Y * v.Y) / L(u);
        if (skew > tolM)
        {
            double deg = Math.Acos(Math.Clamp((u.X * v.X + u.Y * v.Y) / (L(u) * L(v)), -1, 1)) * 180 / Math.PI;
            why = $"is not a rectangle: its corners are {Deg(deg)}° and {Deg(180 - deg)}°, so its two ends are offset along each other " +
                  "and the paths between them are not all the same length.";
            return null;
        }
        // The side whose direction lies within 45° of +x is the rectangle's U side.
        double angle = Math.Atan2(u.Y, u.X) * 180 / Math.PI;
        double width = L(u), height = L(v);
        while (angle > 45) { angle -= 90; (width, height) = (height, width); }
        while (angle <= -45) { angle += 90; (width, height) = (height, width); }
        if (Math.Abs(angle) < 1e-6 * 180 / Math.PI) angle = 0;
        double cx = (c[0].X + c[1].X + c[2].X + c[3].X) / 4, cy = (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4;
        double dbu = C3dLowering.Metres(1, dbuPerMicron);
        long sw = (long)Math.Round(width / dbu, MidpointRounding.AwayFromZero), sh = (long)Math.Round(height / dbu, MidpointRounding.AwayFromZero);
        long mx = (long)Math.Round(cx / dbu - sw / 2.0, MidpointRounding.AwayFromZero), my = (long)Math.Round(cy / dbu - sh / 2.0, MidpointRounding.AwayFromZero);
        if (sw <= 0 || sh <= 0)
        {
            why = "has no area at the document's grid.";
            return null;
        }
        return (new C3dRect { Min = new C3dPoint2(mx, my), Size = new C3dPoint2(sw, sh) }, angle);
    }

    /// <summary>The corners of a polygon: its points less repeats and less any point within <paramref name="tolM"/> of the line
    /// through its two neighbours.</summary>
    private static List<Point2> Corners(IReadOnlyList<Point2> points, double tolM)
    {
        var o = new List<Point2>();
        foreach (var p in points)
            if (o.Count == 0 || Distance(o[^1], p) > tolM) o.Add(p);
        while (o.Count > 1 && Distance(o[0], o[^1]) <= tolM) o.RemoveAt(o.Count - 1);
        bool removed = true;
        while (removed && o.Count > 3)
        {
            removed = false;
            for (int i = 0; i < o.Count; i++)
            {
                var (prev, next) = (o[(i + o.Count - 1) % o.Count], o[(i + 1) % o.Count]);
                if (LineDistance(o[i], prev, next) <= tolM && Between(o[i], prev, next))
                {
                    o.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }
        return o;
    }

    private static bool Between(Point2 q, Point2 p0, Point2 p1)
        => (q.X - p0.X) * (p1.X - p0.X) + (q.Y - p0.Y) * (p1.Y - p0.Y) >= 0 && (q.X - p1.X) * (p0.X - p1.X) + (q.Y - p1.Y) * (p0.Y - p1.Y) >= 0;

    private static double LineDistance(Point2 q, Point2 p0, Point2 p1)
    {
        double dx = p1.X - p0.X, dy = p1.Y - p0.Y, l = Math.Sqrt(dx * dx + dy * dy);
        return l > 0 ? Math.Abs((q.X - p0.X) * dy - (q.Y - p0.Y) * dx) / l : Distance(q, p0);
    }

    private static string KindOf(C3dPortContext.Conductor? c)
        => c is null ? "nothing the problem has" : c.Axis >= 0 ? "a face of the air box" : c.Cylinder is not null ? "a cylinder"
         : c.Patch is not null ? "a sheet on a cylinder" : "a sheet or a solid that is not an outline";

    /// <summary>An angle as a sentence or a field shows it: degrees to a thousandth.</summary>
    public static string Degrees(double d) => Math.Round(d, 3).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Deg(double d) => Degrees(d);
    private static string Um(double m) => (m * 1e6).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    private static string Mm(double m) => (m * 1e3).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
