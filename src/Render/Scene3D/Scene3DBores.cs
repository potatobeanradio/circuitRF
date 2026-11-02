// A plated via's bore, DRAWN. The solver gets a plated barrel as a copper cylinder plus a coaxial air cylinder of higher
// precedence (R-em3d3-5d), and the pads the barrel passes through as whole polygons: the precedence rule subtracts the
// air from both. A surface renderer draws every solid's own surface and hides air, so the barrel read as a solid copper
// rod under an unbroken pad — a plated via looked exactly like a filled one.
//
// VIEW ONLY. Nothing here reaches a solver, the document or a picture's geometry beyond the triangles the 3D view draws:
// the problem keeps its cylinder and its air core, bit for bit. What the view asks for instead is the surface the
// precedence rule leaves — the barrel as a tube, and a hole the bore's size in each metal polygon it passes right
// through. Only that case is carved: a vertical air cylinder spanning a conductor's whole height. One lying wholly inside
// the polygon is a hole; one that clips its edge (a via on the board's edge) or overlaps a hole of its own is cut out as a
// notch (2026-10-25). A bore that stops part-way is left to the precedence rule alone, as before.
//
// A DIELECTRIC is carved the same way (2026-09-30): a non-plated hole is an air cylinder with no barrel around it, so an
// uncarved substrate showed no hole at all. A PLATED via's bore carves it too (2026-10-04, owner report): 2026-09-30 left
// those out on the reasoning that the barrel hides the substrate, but it hides only the slab's SIDE — looking down the
// bore, the slab's top face spans the tube and the plating cannot be seen at all. A plated via carves the slab at the
// barrel's OUTSIDE radius, which the tube then fills: at the bore's radius the slab's hole wall and the tube's inner wall
// would be one surface drawn twice, in two colours. Cost: one more ring per via in the slab's caps, which the
// triangulator's z-order index keeps from going quadratic. A box-shaped slab (no board outline) is carved as its
// rectangle extruded, its faces given back the box's own.
//
// The PADS a plated barrel passes through are carved at its outside too (2026-10-25, owner report): at the bore, each pad's
// hole wall lay on the tube's inner wall and its caps on the tube's end rings, nine layers of coincident faces per via kept
// apart only by the via's constant depth bias — which an orthographic view at an angle defeats, so the pads speckled through
// the bore and round its end rings. At the barrel's outside the bore shows the plating alone and the rings meet the pads
// edge to edge. A bare hole through metal (no barrel) is still carved at the bore.
//
// A via the board's edge cuts through (DFR28-1) is no cylinder: its barrel is the drill's disc clipped to the board, an
// extruded polygon whose flat face lies in the board's side. The pads it passes through are drawn whole, so on every
// layer a pad's straight side lay on that flat face, two faces of one colour fighting unseen until the via was hovered
// and tinted (owner report, 2026-10-28: the copper layers broke through the hovered via, differently at each zoom). Its
// barrel outline is a SHAPED bore: it carves the conductors and slabs it passes right through as a round barrel's outside
// does, so the flat face is the only surface on that plane. A slab needs it as well as Scene3DWalls' cut: the wall is
// cut, but a slab's caps ran on under the flat face, and with the slab hovered their edges came through it as a line.

using Clipper2Lib;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Render.Scene3D;

/// <summary>The vertical air cylinders that carve metal in a problem, and the surface each carved conductor is drawn as.</summary>
internal sealed class Scene3DBores
{
    /// <summary>Metres: geometry closer than this is the same place (the generator writes both from one number).</summary>
    private const double Tol = 1e-9;

    /// <param name="Outer">The radius a polygon is carved at: the barrel's outside when the bore is a plated via's, else
    /// <paramref name="R"/> — a bare hole. For a <paramref name="Shape"/>, the radius that encloses it.</param>
    /// <param name="Shape">An edge-cut via's barrel outline, carved exactly instead of a circle.</param>
    /// <param name="Metal">The bore is itself metal (a filled via, a conductor box): it carves a conductor it passes right
    /// through whatever their precedence, since metal over metal draws the same either way.</param>
    private readonly record struct Bore(double X, double Y, double R, double Z0, double Z1, int Priority, double Outer,
                                        Point2[]? Shape = null, bool Metal = false);

    private readonly Bore[] _bores;   // by X
    private readonly Em3dPrecedence _precedence;
    /// <summary>The largest radius a polygon is carved at: how far outside its own x-range a bore's centre can lie and still cut it.</summary>
    private readonly double _reach;

    private Scene3DBores(Bore[] bores, Em3dPrecedence precedence)
    {
        _bores = bores;
        _precedence = precedence;
        _reach = bores.Max(b => b.Outer);
    }

    /// <summary>The problem's bores, or null when it has none (the ordinary problem, which then costs nothing).
    /// <paramref name="isVia"/> names the conductors that are via barrels, so an edge-cut one is a shaped bore.</summary>
    public static Scene3DBores? Of(Em3dProblem problem, Func<string, bool>? isVia = null)
    {
        Em3dPrecedence? precedence = null;
        var bores = new List<Bore>();
        foreach (var s in problem.Solids)
        {
            if (s.Role != Em3dRole.Air || s.Primitive is not Em3dCylinder c || !Vertical(c)) continue;
            precedence ??= Em3dPrecedence.Of(problem);
            bores.Add(new Bore(c.AxisStart.X, c.AxisStart.Y, c.Radius,
                               Math.Min(c.AxisStart.Z, c.AxisEnd.Z), Math.Max(c.AxisStart.Z, c.AxisEnd.Z), precedence.Of(s), Outer: c.Radius));
        }
        if (bores.Count > 0)
            foreach (var s in problem.Solids)
                if (s.Role == Em3dRole.Conductor && s.Primitive is Em3dCylinder m && Vertical(m))
                    for (int i = 0; i < bores.Count; i++)
                        if (bores[i].Outer == bores[i].R && Math.Abs(bores[i].X - m.AxisStart.X) <= Tol && Math.Abs(bores[i].Y - m.AxisStart.Y) <= Tol &&
                            bores[i].R < m.Radius - Tol)
                            bores[i] = bores[i] with { Outer = m.Radius };
        // A FILLED via (no air core) and a conductor box (a strap, a block) carve the metal they pass right through, at their own
        // outline (2026-11-01, owner report): the via's cap lay on the copper's top face, a box's top and bottom on the copper's,
        // two triangulations of one plane apart only by a constant depth bias, which speckled at a distance and an angle.
        var airBores = bores.Count;
        foreach (var s in problem.Solids)
        {
            if (s.Role != Em3dRole.Conductor) continue;
            if (s.Primitive is Em3dCylinder f && Vertical(f) && isVia?.Invoke(s.Name) == true)
            {
                bool plated = false;
                for (int i = 0; i < airBores; i++)
                    plated |= Math.Abs(bores[i].X - f.AxisStart.X) <= Tol && Math.Abs(bores[i].Y - f.AxisStart.Y) <= Tol;
                if (plated) continue;
                precedence ??= Em3dPrecedence.Of(problem);
                bores.Add(new Bore(f.AxisStart.X, f.AxisStart.Y, f.Radius, Math.Min(f.AxisStart.Z, f.AxisEnd.Z),
                                   Math.Max(f.AxisStart.Z, f.AxisEnd.Z), precedence.Of(s), f.Radius, Metal: true));
            }
            else if (s.Primitive is Em3dBox b && b.Max.Z - b.Min.Z > Tol)
            {
                precedence ??= Em3dPrecedence.Of(problem);
                double cx = (b.Min.X + b.Max.X) / 2, cy = (b.Min.Y + b.Max.Y) / 2;
                double reach = Math.Sqrt((b.Max.X - cx) * (b.Max.X - cx) + (b.Max.Y - cy) * (b.Max.Y - cy));
                bores.Add(new Bore(cx, cy, reach, b.Min.Z, b.Max.Z, precedence.Of(s), reach,
                                   [new(b.Min.X, b.Min.Y), new(b.Max.X, b.Min.Y), new(b.Max.X, b.Max.Y), new(b.Min.X, b.Max.Y)], Metal: true));
            }
        }
        if (isVia is not null)
            foreach (var s in problem.Solids)
            {
                if (s.Role != Em3dRole.Conductor || s.Primitive is not Em3dExtrudedPolygon e || e.Holes.Count > 0 || !isVia(s.Name)) continue;
                precedence ??= Em3dPrecedence.Of(problem);
                double x0 = e.Outline.Min(q => q.X), x1 = e.Outline.Max(q => q.X), y0 = e.Outline.Min(q => q.Y), y1 = e.Outline.Max(q => q.Y);
                double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
                double reach = e.Outline.Max(q => Math.Sqrt((q.X - cx) * (q.X - cx) + (q.Y - cy) * (q.Y - cy)));
                bores.Add(new Bore(cx, cy, reach, e.ZBottom, e.ZTop, precedence.Of(s), reach, [.. e.Outline]));
            }
        if (bores.Count == 0) return null;
        bores.Sort((a, b) => a.X.CompareTo(b.X));
        return new Scene3DBores([.. bores], precedence!);
    }

    /// <summary>
    /// What <paramref name="s"/> is drawn as when a bore carves it — the cache key and the tessellation — or null to draw
    /// its own primitive. A cylinder with a coaxial bore is a tube; an extruded polygon gains one hole per bore through it.
    /// </summary>
    public (object Key, Func<Em3dTriangleMesh> Make)? Carved(Em3dSolid s)
    {
        if (s.Role is not (Em3dRole.Conductor or Em3dRole.Dielectric)) return null;
        int priority = _precedence.Of(s);
        switch (s.Primitive)
        {
            case Em3dCylinder c when Vertical(c):
            {
                double z0 = Math.Min(c.AxisStart.Z, c.AxisEnd.Z), z1 = Math.Max(c.AxisStart.Z, c.AxisEnd.Z);
                foreach (var b in Near(c.AxisStart.X - Tol, c.AxisStart.X + Tol))
                    if (b.Priority > priority && Math.Abs(b.Y - c.AxisStart.Y) <= Tol && b.R < c.Radius - Tol && Spans(b, z0, z1))
                    {
                        string name = s.Name;
                        return (new TubeKey(c, b.R), () => Em3dTessellation.OfTube(name, c, b.R));
                    }
                return null;
            }
            case Em3dBox box when s.Role == Em3dRole.Dielectric:
            {
                // Counter-clockwise from (min, min): its walls are y min, x max, y max, x min — box faces 2, 1, 3, 0.
                var rect = new Em3dExtrudedPolygon(
                    [new Point2(box.Min.X, box.Min.Y), new Point2(box.Max.X, box.Min.Y),
                     new Point2(box.Max.X, box.Max.Y), new Point2(box.Min.X, box.Max.Y)], [], box.Min.Z, box.Max.Z);
                return Extruded(s, rect, priority, BoxFace);
            }
            case Em3dExtrudedPolygon e:
                return Extruded(s, e, priority, null);
            default:
                return null;
        }
    }

    /// <summary>A box's face for the face of its rectangle extruded: bottom, top, then the walls in ring order.</summary>
    private static int BoxFace(int f) => f switch { 0 => 4, 1 => 5, 2 => 2, 3 => 1, 4 => 3, 5 => 0, _ => f };

    /// <summary>An extruded polygon with one hole per bore right through it, or null when none is. <paramref name="face"/>
    /// renumbers the primitive's own faces (a box's are not an extrusion's).</summary>
    private (object Key, Func<Em3dTriangleMesh> Make)? Extruded(Em3dSolid s, Em3dExtrudedPolygon e, int priority, Func<int, int>? face)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        foreach (var p in e.Outline) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); }
        List<Bore>? taken = null, crossing = null;
        foreach (var bore in Near(minX - _reach, maxX + _reach))
        {
            // Carved at the barrel's outside, metal and dielectric alike: at the bore, a pad's hole wall would be the tube's inner
            // wall drawn a second time and its cap would lie on the tube's end ring, two coincident faces apart only by a depth bias.
            // A pad too narrow for the barrel's outside is still carved at the bore, as it was, rather than drawn over it.
            if ((bore.Priority <= priority && !(bore.Metal && s.Role == Em3dRole.Conductor)) || !Spans(bore, e.ZBottom, e.ZTop)) continue;
            var centre = new Point2(bore.X, bore.Y);
            if (bore.Shape is not null)
            {
                if (Touches(e, centre, bore.Outer)) (crossing ??= []).Add(bore);
                continue;
            }
            var b = Fits(e, centre, bore.Outer) ? bore with { R = bore.Outer } : bore;
            if (Fits(e, centre, b.R) && taken?.Any(o => Math.Sqrt((o.X - b.X) * (o.X - b.X) + (o.Y - b.Y) * (o.Y - b.Y)) < o.R + b.R) != true)
                (taken ??= []).Add(b);
            // A bore across the polygon's edge (a via on the board's edge) or into one of its holes is cut out of it as a notch.
            else if (Touches(e, centre, bore.Outer)) (crossing ??= []).Add(bore with { R = bore.Outer });
        }
        if (taken is null && crossing is null) return null;
        if (crossing is not null) return Notched(s, e, [.. taken ?? [], .. crossing], face);

        var holes = new List<IReadOnlyList<Point2>>(e.Holes);
        holes.AddRange(taken!.Select(b => Ring(b)));
        var drawn = e with { Holes = holes };
        int ownEdges = e.Outline.Count + e.Holes.Sum(h => h.Count);
        string solidName = s.Name;
        var key = new BoredKey(e, face is not null,
                               string.Join(";", taken!.Select(b => FormattableString.Invariant($"{b.X:R},{b.Y:R},{b.R:R}"))));
        return (key, () =>
        {
            var mesh = Em3dTessellation.Of(new Em3dSolid(solidName, s.Material, s.Role, drawn, s.Order));
            // The bores' walls are no face of the primitive's: its own faces keep their numbers (bottom 0, top 1, one per
            // edge of its own rings), so a face picked on a pad is named exactly as it was before it was carved.
            return new Em3dTriangleMesh(mesh.Vertices,
                [.. mesh.Triangles.Select(t => t.Face >= 2 + ownEdges ? t with { Face = -1 }
                                             : face is not null && t.Face >= 0 ? t with { Face = face(t.Face) } : t)]);
        });
    }

    /// <summary>
    /// The polygon with every bore's circle subtracted (Clipper2, in picometres), when one crosses its edge or a hole of its own —
    /// what a hole cannot be. The pieces are extruded one by one; a vertex that was the polygon's or a bore ring's comes back
    /// exactly, so a notch still meets the tube's own vertices. Each wall lying on one of the primitive's own edges keeps that
    /// edge's face, as a hole's carving does; a bore's arc is no face.
    /// </summary>
    private static (object Key, Func<Em3dTriangleMesh> Make) Notched(Em3dSolid s, Em3dExtrudedPolygon e, List<Bore> bores, Func<int, int>? face)
    {
        var key = new BoredKey(e, face is not null,
                               "cut;" + string.Join(";", bores.Select(b => FormattableString.Invariant($"{b.X:R},{b.Y:R},{b.R:R}"))));
        return (key, () =>
        {
            const double Scale = 1e12;
            var exact = new Dictionary<Point64, Point2>();
            Path64 Path(IReadOnlyList<Point2> ring, bool positive)
            {
                var path = new Path64(ring.Count);
                foreach (var q in ring)
                {
                    var at = new Point64(Math.Round(q.X * Scale), Math.Round(q.Y * Scale));
                    exact.TryAdd(at, q);
                    path.Add(at);
                }
                if (Clipper.IsPositive(path) != positive) path.Reverse();
                return path;
            }
            var clipper = new Clipper64 { PreserveCollinear = true };
            clipper.AddSubject(Path(e.Outline, positive: true));
            foreach (var h in e.Holes) clipper.AddSubject(Path(h, positive: false));
            foreach (var b in bores) clipper.AddClip(Path(Ring(b), positive: true));
            var tree = new PolyTree64();
            clipper.Execute(ClipType.Difference, FillRule.NonZero, tree);

            List<Point2> Back(Path64 path) => [.. path.Select(q => exact.TryGetValue(q, out var p) ? p : new Point2(q.X / Scale, q.Y / Scale))];
            var pieces = new List<Em3dExtrudedPolygon>();
            void Walk(PolyPath64 node)
            {
                for (int i = 0; i < node.Count; i++)
                {
                    var outer = (PolyPath64)node[i];
                    var holes = new List<IReadOnlyList<Point2>>();
                    for (int j = 0; j < outer.Count; j++)
                    {
                        var hole = (PolyPath64)outer[j];
                        holes.Add(Back(hole.Polygon!));
                        Walk(hole);
                    }
                    pieces.Add(e with { Outline = Back(outer.Polygon!), Holes = holes });
                }
            }
            Walk(tree);

            IReadOnlyList<Point2>[] own = [e.Outline, .. e.Holes];
            int OwnFace(Point2 a, Point2 b)
            {
                int start = 0;
                foreach (var ring in own)
                {
                    for (int i = 0; i < ring.Count; i++)
                        if (OnSegment(a, ring[i], ring[(i + 1) % ring.Count]) && OnSegment(b, ring[i], ring[(i + 1) % ring.Count])) return 2 + start + i;
                    start += ring.Count;
                }
                return -1;
            }
            var vertices = new List<Point3>();
            var triangles = new List<Em3dTriangle>();
            foreach (var piece in pieces)
            {
                var mesh = Em3dTessellation.Of(new Em3dSolid(s.Name, s.Material, s.Role, piece, s.Order));
                var edges = new List<(Point2 A, Point2 B)>();
                foreach (var ring in (IReadOnlyList<Point2>[])[piece.Outline, .. piece.Holes])
                    for (int i = 0; i < ring.Count; i++) edges.Add((ring[i], ring[(i + 1) % ring.Count]));
                var walls = new Dictionary<int, int>();
                int at = vertices.Count;
                vertices.AddRange(mesh.Vertices);
                foreach (var t in mesh.Triangles)
                {
                    int f = t.Face;
                    if (f >= 2 && !walls.TryGetValue(f, out f)) walls[t.Face] = f = OwnFace(edges[t.Face - 2].A, edges[t.Face - 2].B);
                    if (face is not null && f >= 0) f = face(f);
                    triangles.Add(t with { A = t.A + at, B = t.B + at, C = t.C + at, Face = f });
                }
            }
            return new Em3dTriangleMesh(vertices, triangles);
        });
    }

    /// <summary>Whether a disc of radius <paramref name="r"/> at <paramref name="centre"/> overlaps the polygon's area at all.</summary>
    private static bool Touches(Em3dExtrudedPolygon e, Point2 centre, double r)
        => (Inside(e.Outline, centre) || Clearance(e.Outline, centre) < r - Tol)
        && !e.Holes.Any(h => Inside(h, centre) && Clearance(h, centre) >= r - Tol);

    private static bool OnSegment(Point2 p, Point2 a, Point2 b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
        double t = len2 > 0 ? Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1) : 0;
        double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
        return ex * ex + ey * ey <= Tol * Tol;
    }

    /// <summary>Whether a hole of radius <paramref name="r"/> at <paramref name="centre"/> lies wholly inside the polygon's outline
    /// and clear of its own holes.</summary>
    private static bool Fits(Em3dExtrudedPolygon e, Point2 centre, double r)
        => Inside(e.Outline, centre) && Clearance(e.Outline, centre) >= r - Tol
        && !e.Holes.Any(h => Inside(h, centre) || Clearance(h, centre) < r - Tol);

    private sealed record TubeKey(Em3dCylinder Cylinder, double Bore);

    private sealed record BoredKey(Em3dExtrudedPolygon Primitive, bool Box, string Bores);

    private static bool Vertical(Em3dCylinder c)
        => Math.Abs(c.AxisStart.X - c.AxisEnd.X) <= Tol && Math.Abs(c.AxisStart.Y - c.AxisEnd.Y) <= Tol && Math.Abs(c.AxisEnd.Z - c.AxisStart.Z) > Tol;

    private static bool Spans(Bore b, double z0, double z1) => b.Z0 <= z0 + Tol && b.Z1 >= z1 - Tol;

    /// <summary>The bores whose centre's x lies in [lo, hi].</summary>
    private IEnumerable<Bore> Near(double lo, double hi)
    {
        int a = 0, z = _bores.Length;
        while (a < z) { int m = (a + z) / 2; if (_bores[m].X < lo) a = m + 1; else z = m; }
        for (int i = a; i < _bores.Length && _bores[i].X <= hi; i++) yield return _bores[i];
    }

    /// <summary>The bore's circle at the tube's own vertices, so a pad's hole and the barrel's bore meet edge to edge.</summary>
    private static Point2[] Ring(Bore b)
    {
        if (b.Shape is { } shape) return shape;
        int n = Em3dTessellation.CylinderSegments;
        var ring = new Point2[n];
        for (int k = 0; k < n; k++)
        {
            double t = 2 * Math.PI * k / n;
            ring[k] = new Point2(b.X + b.R * Math.Cos(t), b.Y + b.R * Math.Sin(t));
        }
        return ring;
    }

    private static bool Inside(IReadOnlyList<Point2> ring, Point2 p)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) &&
                p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>The distance from <paramref name="p"/> to the nearest edge of <paramref name="ring"/>.</summary>
    private static double Clearance(IReadOnlyList<Point2> ring, Point2 p)
    {
        double best = double.MaxValue;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            double dx = ring[i].X - ring[j].X, dy = ring[i].Y - ring[j].Y, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((p.X - ring[j].X) * dx + (p.Y - ring[j].Y) * dy) / len2, 0, 1) : 0;
            double ex = ring[j].X + t * dx - p.X, ey = ring[j].Y + t * dy - p.Y;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }
}
