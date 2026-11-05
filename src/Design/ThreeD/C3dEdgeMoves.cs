// Expand Edges and Contract Edges: a rectangle's edge moved along its own plane to the next thing it meets.
//
// A rough sheet or a lumped port that misses its copper by a few micrometres is refused as a port, and typing the right height
// means knowing the stackup to the nanometre. So an edge can be sent to the next boundary instead. Everything the rectangle's plane
// passes through is cut once into 2D, and an edge moving through that picture stops at the first place the part of the plane it
// sweeps meets a boundary. It reads the same elaboration, and the same contact tolerance, as C3dPorts: an edge sent here is
// an edge the port's own contact test accepts.
//
// WHAT THE CUT IS MADE FROM. Triangles: every solid already tessellates, and the tessellation holds exact design coordinates. Two
// things a mesh would get wrong are dealt with here. A wall's two triangles cut into two collinear pieces whose join falls
// at an arbitrary height, so collinear pieces are merged, or an edge would stop half way up a wall at nothing. A cap lying in the plane
// has inner diagonals, so an edge shared by two coplanar triangles is not boundary. A cylinder is cut exactly when its axis lies along
// a world axis, because a facet would stop a round via's tangent short of the via.
//
// WHY THIS IS NOT THE GENERATOR'S ReturnWalk. That walk finds a layout port's return: it runs in z over a layout's stackup bands and
// sees only metal, and its target is chosen by what the technology marks as ground. An edge here moves along any axis of any plane, and
// a drawn solid or sheet stops it as well. One thing is the same on purpose: a boundary between two dielectrics stops
// nothing. A multilayer stackup is dielectric on dielectric, and an edge halted at each one would never reach its copper, so a
// dielectric boundary counts only where the edge goes into or out of the dielectric from open space.

using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What moving an edge does: outward to the next thing, or inward to the next boundary inside the rectangle.</summary>
public enum C3dEdgeMoveKind { Expand, Contract }

/// <summary>One edge of a rectangle on its plane: which of the plane's two axes it moves along (0 is u, 1 is v), and which end.</summary>
public readonly record struct C3dEdge(int Axis, bool High);

/// <summary>A rectangle in world DBU on a plane's u and v axes.</summary>
public readonly record struct C3dEdgeRect(long U0, long V0, long U1, long V1)
{
    public long Low(int axis) => axis == 0 ? U0 : V0;
    public long High(int axis) => axis == 0 ? U1 : V1;
    public C3dEdgeRect With(C3dEdge e, long at) => (e.Axis, e.High) switch
    {
        (0, false) => this with { U0 = at }, (0, true) => this with { U1 = at },
        (1, false) => this with { V0 = at }, _ => this with { V1 = at },
    };
    public long At(C3dEdge e) => e.High ? High(e.Axis) : Low(e.Axis);
}

/// <summary>An edge that moved: from where to where, and what it stopped at.</summary>
public sealed record C3dEdgeMoved(C3dEdge Edge, long From, long To, string Hit);

/// <summary>An edge that stayed, and why.</summary>
public sealed record C3dEdgeStayed(C3dEdge Edge, string Why);

/// <summary>The rectangle after a move, each moved edge, and each edge that stayed.</summary>
public sealed record C3dEdgeMoveResult(C3dEdgeRect Rect, IReadOnlyList<C3dEdgeMoved> Moved, IReadOnlyList<C3dEdgeStayed> Stayed);

/// <summary>Everything one plane passes through, cut once and ready to be asked about any edge moving on it.</summary>
public sealed class C3dEdgeMoves
{
    private readonly record struct Seg(double U0, double V0, double U1, double V1);
    private readonly record struct Circle(double U, double V, double R);

    /// <summary>One object's cut. Closed: it bounds a region (a solid's section, or a sheet lying in the plane). Otherwise it is
    /// only lines, the cut of a sheet crossing the plane or a PEC face of the box, with no inside to be in.</summary>
    private sealed record Section(string Name, bool Closed, IReadOnlyList<Seg> Segs, IReadOnlyList<Circle> Circles)
    {
        /// <summary>A conductor, a sheet or a PEC face, as against a dielectric or an object with no material.</summary>
        public bool Metal { get; init; } = true;
    }

    private readonly List<Section> _sections = [];
    private readonly double _dbu, _tol, _inset;
    private readonly char[] _axisNames;
    private readonly LayoutUnit _unit;

    /// <param name="elaboration">The objects an edge can meet.</param>
    /// <param name="plane">The plane the rectangle lies on.</param>
    /// <param name="offset">The plane's world position, DBU.</param>
    /// <param name="dbuPerMicron">The document's DBU per micrometre.</param>
    /// <param name="box">The active setup's air box: a PEC face is a conductor a port may end on, so an edge stops at it.</param>
    /// <param name="self">The object the rectangle is, which it must not meet.</param>
    /// <param name="unit">The unit a sentence spells a distance in.</param>
    public C3dEdgeMoves(C3dElaboration elaboration, C3dPlane plane, long offset, int dbuPerMicron, Em3dAirBox? box, string? self, LayoutUnit unit)
    {
        _dbu = C3dLowering.Metres(1, dbuPerMicron);
        // Port's own numbers: an edge this close to a conductor already touches it, and its ends are pulled in as far.
        _tol = _dbu + elaboration.OutlineMoveM;
        _inset = 2 * _dbu + 2 * elaboration.OutlineMoveM;
        _unit = unit;
        (int n, int u, int v) = Axes(plane);
        _axisNames = ["XYZ"[u], "XYZ"[v]];
        double h = C3dLowering.Metres(offset, dbuPerMicron);
        double eps = _dbu / 100;

        bool Skip(string name) => name == self || elaboration.NotModelled.Contains(name);
        bool Crosses((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b)
        {
            double[] lo = [b.X0, b.Y0, b.Z0], hi = [b.X1, b.Y1, b.Z1];
            return lo[n] <= h + eps && hi[n] >= h - eps;
        }
        void AddSolid(Em3dSolid s)
        {
            if (Skip(s.Name) || !Crosses(Em3dProblem.Bounds(s.Primitive))) return;
            bool metal = s.Role == Em3dRole.Conductor;
            if (s.Primitive is Em3dCylinder c && CylinderCut(s.Name, c, n, u, v, h) is { } cut) { if (cut.Segs.Count + cut.Circles.Count > 0) _sections.Add(cut with { Metal = metal }); return; }
            AddMesh(s.Name, Em3dTessellation.Of(s), n, u, v, h, eps, closed: true, metal);
        }
        void AddSheet(Em3dSheet s)
        {
            if (!Skip(s.Name) && Crosses(s.WorldBounds())) AddMesh(s.Name, Em3dTessellation.OfSheet(s), n, u, v, h, eps, closed: false, metal: true);
        }

        // Conductors first: where copper and a dielectric share a boundary, the sentence names the copper.
        foreach (var s in elaboration.Solids.Where(s => s.Role == Em3dRole.Conductor)) AddSolid(s);
        foreach (var s in elaboration.Sheets) AddSheet(s);
        foreach (var s in elaboration.CylinderSheets)
            if (!Skip(s.Name) && Crosses(s.WorldBounds())) AddMesh(s.Name, s.Patch.Tessellate(s.Name), n, u, v, h, eps, closed: false, metal: true);
        if (box is not null) AddPecFaces(box, n, u, v);
        foreach (var s in elaboration.Solids.Where(s => s.Role != Em3dRole.Conductor)) AddSolid(s);
        foreach (var s in elaboration.UnassignedSolids) AddSolid(s);
        foreach (var s in elaboration.UnassignedSheets) AddSheet(s);
    }

    /// <summary>A plane's normal axis and its u and v axes, world indices.</summary>
    public static (int Normal, int U, int V) Axes(C3dPlane plane) => plane switch
    {
        C3dPlane.XY => (2, 0, 1), C3dPlane.XZ => (1, 0, 2), _ => (0, 1, 2),
    };

    /// <summary>
    /// Moves <paramref name="edges"/>. Axis v goes before axis u: on a board's vertical sheet that settles its height against the
    /// stackup first, and its sides then measure over that height rather than over whatever it was drawn across. Each edge sees
    /// the rectangle as the edges before it left it. Contracting a pair, the edge with the shorter way to go goes first, so it is the
    /// other that finds nothing left to stop at, and the rectangle never collapses.
    /// </summary>
    public C3dEdgeMoveResult Move(C3dEdgeMoveKind kind, C3dEdgeRect rect, IReadOnlyList<C3dEdge> edges)
    {
        var moved = new List<C3dEdgeMoved>();
        var stayed = new List<C3dEdgeStayed>();
        foreach (int axis in new[] { 1, 0 })
        {
            var these = edges.Where(e => e.Axis == axis).ToList();
            if (kind == C3dEdgeMoveKind.Contract && these.Count == 2)
            {
                double Way(C3dEdge e) => Step(kind, rect, e, out _, out _) is { } to ? Math.Abs(to - M(rect.At(e))) : double.PositiveInfinity;
                these = [.. these.OrderBy(Way)];
            }
            foreach (var e in these)
            {
                long from = rect.At(e);
                if (Step(kind, rect, e, out string hit, out string why) is { } to && D(to) is var at && at != from && rect.With(e, at) is var next &&
                    next.U1 > next.U0 && next.V1 > next.V0)
                {
                    rect = next;
                    moved.Add(new C3dEdgeMoved(e, from, at, hit));
                }
                else stayed.Add(new C3dEdgeStayed(e, why));
            }
        }
        return new C3dEdgeMoveResult(rect, moved, stayed);
    }

    /// <summary>An edge as a menu names it: <c>-Z</c>.</summary>
    public string EdgeName(C3dEdge e) => (e.High ? "+" : "-") + _axisNames[e.Axis];

    /// <summary>What a move does, in a sentence.</summary>
    public string Describe(C3dEdgeMoveResult r, int dbuPerMicron)
    {
        string Length(long dbu) => C3dUnits.Spell(C3dLowering.Metres(dbu, dbuPerMicron), _unit);
        var parts = r.Moved.Select(m => $"the {EdgeName(m.Edge)} edge moves {Length(Math.Abs(m.To - m.From))} to {m.Hit}")
                     .Concat(r.Stayed.Select(s => $"the {EdgeName(s.Edge)} edge {s.Why}"));
        string text = string.Join("; ", parts);
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    private double M(long dbu) => dbu * _dbu;
    private long D(double m) => (long)Math.Round(m / _dbu, MidpointRounding.AwayFromZero);

    // ── one edge ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Where <paramref name="e"/> stops, metres, or null with why it stays.</summary>
    private double? Step(C3dEdgeMoveKind kind, C3dEdgeRect rect, C3dEdge e, out string hit, out string why)
    {
        hit = why = "";
        // Its ends pulled in, as a port's are: the wall a sheet's side lies flush against is not in its way, and neither is the
        // copper a side edge grazes at its corner.
        double a = M(rect.Low(1 - e.Axis)) + _inset, b = M(rect.High(1 - e.Axis)) - _inset;
        if (a > b) a = b = M(rect.Low(1 - e.Axis) + rect.High(1 - e.Axis)) / 2;
        double at = M(rect.At(e));
        // The way this edge moves: outward from the rectangle to expand, inward to contract.
        double dir = (e.High ? 1 : -1) * (kind == C3dEdgeMoveKind.Expand ? 1 : -1);
        if (kind == C3dEdgeMoveKind.Expand)
            foreach (var s in _sections)
                if (EntersAt(s, e.Axis, a, b, at, dir))
                {
                    why = $"already touches '{s.Name}'";
                    return null;
                }
        double limit = kind == C3dEdgeMoveKind.Expand ? double.PositiveInfinity : Math.Abs(M(rect.High(e.Axis) - rect.Low(e.Axis))) - _tol;
        double best = double.PositiveInfinity;
        string? name = null;
        foreach (var s in _sections)
            foreach (var (w, across) in Stops(s, e.Axis, a, b))
            {
                double way = (w - at) * dir;
                if (way <= _tol || way >= limit || way >= best - _tol) continue;
                if (!s.Metal && WithinDielectric(e.Axis, across, w, dir)) continue;
                (best, name) = (way, s.Name);
            }
        if (name is null)
        {
            why = kind == C3dEdgeMoveKind.Expand ? "has nothing beyond it" : "has nothing to stop at before the opposite edge";
            return null;
        }
        hit = name.StartsWith("airbox/", StringComparison.Ordinal) ? "the air box's PEC face" : $"'{name}'";
        return at + best * dir;
    }

    /// <summary>A segment or circle in the frame of an edge moving along <paramref name="axis"/>: s across the edge, w along its way.</summary>
    private static (double S0, double W0, double S1, double W1) Framed(Seg q, int axis)
    {
        var (s0, w0, s1, w1) = axis == 1 ? (q.U0, q.V0, q.U1, q.V1) : (q.V0, q.U0, q.V1, q.U1);
        return s0 <= s1 ? (s0, w0, s1, w1) : (s1, w1, s0, w0);
    }

    /// <summary>Whether a dielectric lies on both sides of a point on the edge's way: a boundary between two of them, which stops nothing.</summary>
    private bool WithinDielectric(int axis, double s, double w, double dir)
    {
        bool At(double q) => _sections.Any(x => !x.Metal && x.Closed && Inside(x, axis, s, q));
        return At(w + dir * _dbu / 4) && At(w - dir * _dbu / 4);
    }

    /// <summary>Every position along its way at which an edge spanning a..b meets a boundary of <paramref name="sec"/>, each with a place
    /// across the edge where it does.</summary>
    private IEnumerable<(double W, double S)> Stops(Section sec, int axis, double a, double b)
    {
        foreach (var q in sec.Segs)
        {
            var (s0, w0, s1, w1) = Framed(q, axis);
            if (s1 < a || s0 > b) continue;
            if (s1 - s0 < 1e-15) { yield return (w0, s0); yield return (w1, s0); continue; }
            double W(double s) => w0 + (w1 - w0) * (s - s0) / (s1 - s0);
            double from = Math.Max(s0, a), to = Math.Min(s1, b);
            double wf = W(from), wt = W(to);
            // Along the edge, the middle of the stretch is a place on it that a corner cannot confuse.
            bool level = Math.Abs(wf - wt) <= _tol;
            yield return (wf, level ? (from + to) / 2 : from);
            yield return (wt, level ? (from + to) / 2 : to);
        }
        foreach (var c in sec.Circles)
        {
            var (cs, cw) = axis == 1 ? (c.U, c.V) : (c.V, c.U);
            double d = cs - Math.Clamp(cs, a, b);
            if (Math.Abs(d) >= c.R) continue;
            double half = Math.Sqrt(c.R * c.R - d * d);
            yield return (cw - half, Math.Clamp(cs, a, b));
            yield return (cw + half, Math.Clamp(cs, a, b));
        }
    }

    /// <summary>
    /// Whether the edge lies on a boundary of <paramref name="sec"/> with the object ahead of it and not behind: it already touches.
    /// A dielectric the sheet lies inside does not count where the edge reaches the far side of it, since there the object is behind,
    /// and nor does one met from inside another dielectric.
    /// </summary>
    private bool EntersAt(Section sec, int axis, double a, double b, double at, double dir)
    {
        double nudge = _dbu / 4;
        foreach (var q in sec.Segs)
        {
            var (s0, w0, s1, w1) = Framed(q, axis);
            if (s1 < a || s0 > b) continue;
            if (s1 - s0 < 1e-15)
            {
                // Along the edge's way: for a region its neighbouring boundary says so; a line starting here runs on ahead.
                if (sec.Closed) continue;
                if ((Math.Abs(w0 - at) <= _tol && (w1 - at) * dir > _tol) || (Math.Abs(w1 - at) <= _tol && (w0 - at) * dir > _tol)) return true;
                continue;
            }
            double from = Math.Max(s0, a), to = Math.Min(s1, b);
            double W(double s) => w0 + (w1 - w0) * (s - s0) / (s1 - s0);
            double wf = W(from), wt = W(to);
            if (Math.Abs(wf - at) > _tol && Math.Abs(wt - at) > _tol) continue;
            if (!sec.Closed) return true;
            double sample = Math.Abs(wf - at) <= _tol && Math.Abs(wt - at) <= _tol ? (from + to) / 2
                          : Math.Abs(wf - at) <= _tol ? from + Math.Min(nudge, (to - from) / 2) : to - Math.Min(nudge, (to - from) / 2);
            double on = W(sample);
            if (Inside(sec, axis, sample, on + dir * nudge) && !Inside(sec, axis, sample, on - dir * nudge) &&
                (sec.Metal || !WithinDielectric(axis, sample, on, dir))) return true;
        }
        foreach (var c in sec.Circles)
        {
            var (cs, cw) = axis == 1 ? (c.U, c.V) : (c.V, c.U);
            double d = cs - Math.Clamp(cs, a, b);
            if (Math.Abs(d) >= c.R) continue;
            double near = cw - dir * Math.Sqrt(c.R * c.R - d * d);
            if (Math.Abs(near - at) <= _tol) return true;
        }
        return false;
    }

    /// <summary>Whether a point lies inside a closed section, by crossings of a ray along w.</summary>
    private static bool Inside(Section sec, int axis, double s, double w)
    {
        foreach (var c in sec.Circles)
        {
            var (cs, cw) = axis == 1 ? (c.U, c.V) : (c.V, c.U);
            if ((s - cs) * (s - cs) + (w - cw) * (w - cw) < c.R * c.R) return true;
        }
        bool inside = false;
        foreach (var q in sec.Segs)
        {
            var (s0, w0, s1, w1) = Framed(q, axis);
            if (s0 > s || s1 <= s) continue;                         // half-open, so a vertex is counted once
            if (w0 + (w1 - w0) * (s - s0) / (s1 - s0) > w) inside = !inside;
        }
        return inside;
    }

    // ── the cut ──────────────────────────────────────────────────────────────────────────────

    private void AddMesh(string name, Em3dTriangleMesh mesh, int n, int u, int v, double h, double eps, bool closed, bool metal)
    {
        var segs = new List<Seg>();
        var coplanar = new Dictionary<(int, int), (int A, int B, int Count)>();
        double C(Point3 p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
        Seg Line(Point3 p, Point3 q) => new(C(p, u), C(p, v), C(q, u), C(q, v));
        Point3 Cross(Point3 p, Point3 q, double dp, double dq)
        {
            double t = dp / (dp - dq);
            return new Point3(p.X + (q.X - p.X) * t, p.Y + (q.Y - p.Y) * t, p.Z + (q.Z - p.Z) * t);
        }
        foreach (var t in mesh.Triangles)
        {
            int[] ix = [t.A, t.B, t.C];
            Point3[] p = [mesh.Vertices[t.A], mesh.Vertices[t.B], mesh.Vertices[t.C]];
            double[] d = [C(p[0], n) - h, C(p[1], n) - h, C(p[2], n) - h];
            bool[] on = [Math.Abs(d[0]) <= eps, Math.Abs(d[1]) <= eps, Math.Abs(d[2]) <= eps];
            int count = on.Count(x => x);
            if (count == 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int i = ix[k], j = ix[(k + 1) % 3];
                    var key = i < j ? (i, j) : (j, i);
                    coplanar[key] = coplanar.TryGetValue(key, out var was) ? was with { Count = was.Count + 1 } : (i, j, 1);
                }
            }
            else if (count == 2)
            {
                int k = Array.IndexOf(on, false);
                segs.Add(Line(p[(k + 1) % 3], p[(k + 2) % 3]));
            }
            else if (count == 1)
            {
                int k = Array.IndexOf(on, true);
                int i = (k + 1) % 3, j = (k + 2) % 3;
                if (d[i] * d[j] < 0) segs.Add(Line(p[k], Cross(p[i], p[j], d[i], d[j])));
            }
            else
            {
                var crossings = new List<Point3>(2);
                for (int k = 0; k < 3; k++)
                {
                    int j = (k + 1) % 3;
                    if (d[k] * d[j] < 0) crossings.Add(Cross(p[k], p[j], d[k], d[j]));
                }
                if (crossings.Count == 2) segs.Add(Line(crossings[0], crossings[1]));
            }
        }
        // An edge two coplanar triangles share is inside the face; an edge one of them has is its boundary.
        foreach (var e in coplanar.Values.Where(e => e.Count == 1)) segs.Add(Line(mesh.Vertices[e.A], mesh.Vertices[e.B]));
        if (segs.Count == 0) return;
        _sections.Add(new Section(name, closed || coplanar.Count > 0, MergeCollinear(segs, eps), []) { Metal = metal });
    }

    /// <summary>Pieces of one straight line joined into one, so the only ends left are real ones.</summary>
    private static List<Seg> MergeCollinear(List<Seg> segs, double eps)
    {
        var lines = new List<(double Angle, double Off, double T0, double T1)>(segs.Count);
        foreach (var q in segs)
        {
            double du = q.U1 - q.U0, dv = q.V1 - q.V0, len = Math.Sqrt(du * du + dv * dv);
            if (len <= eps) continue;
            (du, dv) = (du / len, dv / len);
            if (du < -1e-12 || (Math.Abs(du) <= 1e-12 && dv < 0)) (du, dv) = (-du, -dv);
            double t0 = q.U0 * du + q.V0 * dv, t1 = q.U1 * du + q.V1 * dv;
            lines.Add((Math.Atan2(dv, du), -q.U0 * dv + q.V0 * du, Math.Min(t0, t1), Math.Max(t0, t1)));
        }
        lines.Sort((x, y) => x.Angle != y.Angle ? x.Angle.CompareTo(y.Angle) : x.Off.CompareTo(y.Off));
        var merged = new List<Seg>();
        for (int i = 0; i < lines.Count;)
        {
            int j = i + 1;
            while (j < lines.Count && Math.Abs(lines[j].Angle - lines[i].Angle) < 1e-7) j++;
            // One direction: sorted again by offset, since a tiny difference in angle may have split the offsets' order.
            var direction = lines.GetRange(i, j - i).OrderBy(l => l.Off).ToList();
            for (int a = 0; a < direction.Count;)
            {
                int b = a + 1;
                while (b < direction.Count && direction[b].Off - direction[b - 1].Off <= eps) b++;
                var (angle, off) = (direction[a].Angle, direction[a].Off);
                double du = Math.Cos(angle), dv = Math.Sin(angle);
                double lo = double.NaN, hi = double.NaN;
                void Flush()
                {
                    if (!double.IsNaN(lo)) merged.Add(new Seg(lo * du - off * dv, lo * dv + off * du, hi * du - off * dv, hi * dv + off * du));
                }
                foreach (var l in direction.GetRange(a, b - a).OrderBy(l => l.T0))
                {
                    if (double.IsNaN(lo) || l.T0 > hi + eps) { Flush(); (lo, hi) = (l.T0, l.T1); }
                    else hi = Math.Max(hi, l.T1);
                }
                Flush();
                a = b;
            }
            i = j;
        }
        return merged;
    }

    /// <summary>An axis-aligned cylinder's exact cut, or null when its axis lies along no world axis and its mesh must do.</summary>
    private static Section? CylinderCut(string name, Em3dCylinder c, int n, int u, int v, double h)
    {
        double[] p = [c.AxisStart.X, c.AxisStart.Y, c.AxisStart.Z], q = [c.AxisEnd.X, c.AxisEnd.Y, c.AxisEnd.Z];
        int[] along = [.. Enumerable.Range(0, 3).Where(k => Math.Abs(q[k] - p[k]) > 1e-15)];
        if (along.Length != 1) return null;
        int axis = along[0];
        if (axis == n) return new Section(name, true, [], [new Circle(p[u], p[v], c.Radius)]);
        // The axis lies in the plane: a rectangle, as wide as the chord at this distance from the axis.
        double off = h - p[n];
        if (Math.Abs(off) >= c.Radius) return new Section(name, true, [], []);
        double half = Math.Sqrt(c.Radius * c.Radius - off * off);
        double a0 = Math.Min(p[axis], q[axis]), a1 = Math.Max(p[axis], q[axis]);
        int across = axis == u ? v : u;
        double b0 = p[across] - half, b1 = p[across] + half;
        Seg S(double aa, double ba, double ab, double bb) => axis == u ? new Seg(aa, ba, ab, bb) : new Seg(ba, aa, bb, ab);
        return new Section(name, true, [S(a0, b0, a1, b0), S(a1, b0, a1, b1), S(a1, b1, a0, b1), S(a0, b1, a0, b0)], []);
    }

    /// <summary>The air box's PEC faces that cross the plane, as lines across it.</summary>
    private void AddPecFaces(Em3dAirBox box, int n, int u, int v)
    {
        string[] keys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
        Em3dBoundaryKind[] kinds = [box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax];
        double[] lo = [box.Min.X, box.Min.Y, box.Min.Z], hi = [box.Max.X, box.Max.Y, box.Max.Z];
        for (int k = 0; k < 6; k++)
        {
            int axis = k / 2;
            if (kinds[k] != Em3dBoundaryKind.Pec || axis == n) continue;
            double at = k % 2 == 0 ? lo[axis] : hi[axis];
            var line = axis == u ? new Seg(at, lo[v], at, hi[v]) : new Seg(lo[u], at, hi[u], at);
            _sections.Add(new Section(Em3dAirBox.FaceName(keys[k]), false, [line], []));
        }
    }
}
