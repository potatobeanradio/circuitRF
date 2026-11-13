// brief-em3d-47 R-em3d47-2 — what each primitive can state, and the face commands built on the kernel.
//
// A PRIMITIVE STAYS A PRIMITIVE FOR AS LONG AS IT CAN (overview §1g). A box, a prism or a polyhedron is edited by
// the kernel on its B-rep, and the result is then READ BACK as the primitive it came from (C3dBrepBuild.ReadBox /
// ReadPrism): a box face pushed along its normal is still a box, so only Min and Size change; a prism's side
// pushed out is still a prism, so only its Outline changes; a prism's top moved sideways is an oblique prism, so
// only Shear (and Height) change. What does not read back becomes a Polyhedron carrying the same face names —
// nothing attached to a face is lost — and the result says so (Converted), so the status bar can.
//
// A CYLINDER is not a B-rep here (its faces are curved): its ends move along its axis (Length, and Base for the
// bottom), its side along its radius, and a free move is refused with what to do instead. A SPHERE (brief-em3d-102) has one
// curved face and no vertex: every face edit is refused with where its Radius is changed (owner decision D3), and its centre
// snaps and measures but does not move. A SHEET is not a
// solid: pushed along its normal it moves as a whole (Offset), moved in its plane its outline translates, and
// anything out of its plane is refused.
//
// EVERYTHING HERE IS IN THE OBJECT'S OWN FRAME. The editor takes a world vector into it by the inverse placement
// (R-em3d47-1a) — the geometry stays integer, and the placement stays what the user wrote.


namespace CircuitRF.Design.ThreeD.Kernel;

/// <summary>A face or vertex edit's outcome: the edited object (a copy — the document is never touched here) or why
/// not; whether it became a polyhedron; each folded face's pieces; and, when refused, the would-be solid to draw red.</summary>
public sealed record C3dFaceEditResult(C3dObject? Object, string? Refusal, bool Converted,
                                       IReadOnlyDictionary<string, IReadOnlyList<string>> Folds, C3dBrep? Attempt, C3dKernelStats? Stats)
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoFolds = new Dictionary<string, IReadOnlyList<string>>();

    public static C3dFaceEditResult Refuse(string why, C3dBrep? attempt = null, C3dKernelStats? stats = null)
        => new(null, why, false, NoFolds, attempt, stats);

    public bool Ok => Object is not null;
}

/// <summary>
/// The face and vertex edits of ONE object, keeping its B-rep (and the kernel's adjacency and face-bounds hierarchy)
/// for as long as the gesture that made it lasts — so each mouse move is the operation and nothing else (R-em3d47-6).
/// </summary>
public sealed class C3dFaceEditor
{
    public C3dFaceEditor(C3dObject source)
    {
        Source = source;
        Brep = C3dBrepBuild.Of(source);
    }

    public C3dObject Source { get; }

    /// <summary>The source's B-rep: a box's, a prism's or a polyhedron's; null for a cylinder or a sheet.</summary>
    public C3dBrep? Brep { get; }

    /// <summary>The source's kind as the status bar says it: <c>box</c>, <c>prism</c> …</summary>
    public string Kind => C3dObject.KindOf(Source).ToLowerInvariant();

    // ── what can be edited ───────────────────────────────────────────────────────────────────

    /// <summary>The source's face names, in its own order.</summary>
    public IReadOnlyList<string> FaceNames => Brep is { } b ? [.. b.Faces.Select(f => f.Name)] : Source.FaceNames();

    /// <summary>The source's vertices a Vertex-mode edit may move (a sheet's corners; none for a cylinder), own frame.</summary>
    /// <para>brief-em3d-50 — a wire's are its axis points: Vertex mode edits its shape as wBond's point edit does.</para>
    public IReadOnlyList<C3dPoint3> Vertices => Brep?.Vertices ?? (Source switch
    {
        C3dSheet s => SheetCorners(s),
        C3dWire w => w.Points,
        _ => [],
    });

    /// <summary>The vertex nearest <paramref name="local"/> (own frame, DBU) within <paramref name="within"/>, or −1.</summary>
    public int NearestVertex((double X, double Y, double Z) local, double within)
    {
        int best = -1;
        double bestD = within * within;
        var v = Vertices;
        for (int i = 0; i < v.Count; i++)
        {
            double dx = v[i].X - local.X, dy = v[i].Y - local.Y, dz = v[i].Z - local.Z;
            double d = dx * dx + dy * dy + dz * dz;
            if (d <= bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>R-em3d47-5 — a cylinder's cap centres, own frame: what Vertex mode shows on it (they snap and measure;
    /// they do not move).</summary>
    public static IReadOnlyList<C3dPoint3> CapCentres(C3dCylinder c)
        => [c.Base, C3dBrepBuild.With(c.Base, (int)c.Axis, C3dBrepBuild.Get(c.Base, (int)c.Axis) + c.Length)];

    /// <summary>brief-em3d-102 R-em3d102-5d — what Vertex mode shows on a cylinder (its cap centres) or a sphere (its centre), own
    /// frame; null for anything else. They snap and measure; they do not move.</summary>
    public static IReadOnlyList<C3dPoint3>? FixedPoints(C3dObject o) => o switch
    {
        C3dCylinder c => CapCentres(c),
        C3dSphere s => [s.Centre],
        C3dEllipsoid e => [e.Centre],
        _ => null,
    };

    /// <summary>brief-em3d-102 R-em3d102-4 (owner decision D3) — what a sphere says to any face edit of its surface.</summary>
    public const string SphereFaceEdit = "A sphere's surface is curved: change its Radius in the Inspector.";

    /// <summary>brief-em3d-102 R-em3d102-5d — what a sphere says to a vertex move.</summary>
    public const string SphereVertexMove = "A sphere's centre snaps and measures but does not move.";

    /// <summary>brief-em3d-102 R-em3d102-4 (owner decision D4) — Convert to Polyhedron is not offered for a sphere.</summary>
    public const string SphereConvert = "A sphere is not converted to a polyhedron: its facets would be ~500 unnamed faces. Cut it with a Boolean instead.";

    /// <summary>The refusal a fixed point gives a vertex move: the cylinder's or the sphere's sentence.</summary>
    public static string FixedPointMove(C3dObject o) => o switch
    {
        C3dSphere => SphereVertexMove, C3dEllipsoid => EllipsoidVertexMove, _ => CylinderVertexMove,
    };

    /// <summary>brief-sar-3 — what an ellipsoid says to a face edit, a vertex move and Convert to Polyhedron, as a sphere does.</summary>
    public const string EllipsoidFaceEdit = "An ellipsoid's surface is curved: change its SemiAxes in the Inspector.";
    public const string EllipsoidVertexMove = "An ellipsoid's centre snaps and measures but does not move.";
    public const string EllipsoidConvert = "An ellipsoid is not converted to a polyhedron: its facets would be ~500 unnamed faces. Cut it with a Boolean instead.";

    // ── Move Along Normal (push/pull) ────────────────────────────────────────────────────────

    /// <summary>How far face <paramref name="face"/> may go along its normal (<paramref name="sign"/> +1 out, −1 in) before
    /// something vanishes: |d| stays strictly below it. Null when nothing limits it.</summary>
    public C3dNormalLimit? Limit(string face, int sign)
    {
        switch (Source)
        {
            case C3dSphere or C3dEllipsoid:
                return null;
            case C3dCylinder c:
                if (sign > 0) return null;
                return face == "side" ? new C3dNormalLimit(c.Radius, "side") : new C3dNormalLimit(Math.Abs(c.Length), "side");
            case C3dSheet:
                return null;
        }
        if (Brep is not { } b || b.Topology.IndexOf(face) is not (>= 0 and var f)) return null;
        return C3dKernel.NormalLimit(b, f, sign);
    }

    /// <summary>The largest whole-DBU distance strictly inside <paramref name="limit"/>, with <paramref name="d"/>'s sign:
    /// what a drag clamps to.</summary>
    public static long Clamp(long d, C3dNormalLimit? limit)
    {
        if (limit is not { } l || Math.Abs(d) < l.Distance) return d;
        long most = (long)Math.Ceiling(l.Distance) - 1;
        return Math.Sign(d) * Math.Max(0, most);
    }

    /// <summary>R-em3d47-3a / §2 — face <paramref name="face"/> moved <paramref name="d"/> DBU along its outward normal.</summary>
    public C3dFaceEditResult MoveAlongNormal(string face, long d, Func<double, string>? length = null)
    {
        switch (Source)
        {
            case C3dCylinder c: return CylinderAlong(c, face, d, length);
            case C3dSphere: return C3dFaceEditResult.Refuse(SphereFaceEdit);
            case C3dEllipsoid: return C3dFaceEditResult.Refuse(EllipsoidFaceEdit);
            case C3dSheet s:
            {
                var copy = Copy(s);
                copy.Offset += d;
                return Done(copy);
            }
            case C3dPolyline:
                return C3dFaceEditResult.Refuse("A polyline is construction geometry: it has no faces.");
        }
        if (Brep is not { } b) return C3dFaceEditResult.Refuse($"'{Source.Name}' has no face '{face}'.");
        int f = b.Topology.IndexOf(face);
        if (f < 0) return C3dFaceEditResult.Refuse($"'{Source.Name}' has no face '{face}'.");
        return Restate(C3dKernel.PushPull(b, f, d, length));
    }

    private C3dFaceEditResult CylinderAlong(C3dCylinder c, string face, long d, Func<double, string>? length)
    {
        string At(double v) => length?.Invoke(v) ?? $"{v:0.###} DBU";
        var copy = Copy(c);
        int s = Math.Sign(c.Length);
        if (s == 0) return C3dFaceEditResult.Refuse($"'{c.Name}' has no length.");
        switch (face)
        {
            case "top":
                copy.Length = c.Length + s * d;
                break;
            case "bottom":
                copy.Base = C3dBrepBuild.With(c.Base, (int)c.Axis, C3dBrepBuild.Get(c.Base, (int)c.Axis) - s * d);
                copy.Length = c.Length + s * d;
                break;
            case "side":
                copy.Radius = c.Radius + d;
                if (copy.Radius <= 0) return C3dFaceEditResult.Refuse($"'side' would vanish at {At(c.Radius)}.");
                return Done(copy);
            default:
                return C3dFaceEditResult.Refuse($"'{c.Name}' has no face '{face}'.");
        }
        if (copy.Length == 0 || Math.Sign(copy.Length) != s) return C3dFaceEditResult.Refuse($"'side' would vanish at {At(Math.Abs(c.Length))}.");
        return Done(copy);
    }

    // ── Move (free) ──────────────────────────────────────────────────────────────────────────

    /// <summary>brief-hzn-13 R-hzn13-2 — what an ellipse sheet says to a vertex or edge move.</summary>
    public const string EllipseVertexMove = "An ellipse sheet has no corners to move: change its Centre and Radius in Properties, or move the whole sheet.";

    /// <summary>What a cylinder says to a free move of an end or its side (R-em3d47-2).</summary>
    public const string CylinderFreeMove = "A cylinder's end moves only along its axis. Convert to Polyhedron to move it freely.";

    /// <summary>R-em3d47-3b / §2 — every vertex of face <paramref name="face"/> translated by <paramref name="by"/> (own frame).</summary>
    public C3dFaceEditResult MoveFace(string face, C3dPoint3 by)
    {
        switch (Source)
        {
            case C3dCylinder: return C3dFaceEditResult.Refuse(CylinderFreeMove);
            case C3dSphere: return C3dFaceEditResult.Refuse(SphereFaceEdit);
            case C3dEllipsoid: return C3dFaceEditResult.Refuse(EllipsoidFaceEdit);
            case C3dSheet s:
            {
                var (u, v, w) = C3dBrepBuild.ToPlane(s.Plane, by);
                if (w != 0) return C3dFaceEditResult.Refuse("A sheet moves out of its plane only as a whole, along its normal: Move Along Normal (N).");
                var copy = Copy(s);
                if (copy.Rect is { } r) r.Min = new C3dPoint2(r.Min.U + u, r.Min.V + v);
                else if (copy.Ellipse is { } el) el.Centre = new C3dPoint2(el.Centre.U + u, el.Centre.V + v);   // brief-hzn-13
                else
                {
                    copy.Outline = [.. copy.Outline.Select(p => new C3dPoint2(p.U + u, p.V + v))];
                    copy.Holes = [.. copy.Holes.Select(h => h.Select(p => new C3dPoint2(p.U + u, p.V + v)).ToList())];
                }
                return Done(copy);
            }
            case C3dPolyline:
                return C3dFaceEditResult.Refuse("A polyline is construction geometry: it has no faces.");
        }
        if (Brep is not { } b || b.Topology.IndexOf(face) is not (>= 0 and var f)) return C3dFaceEditResult.Refuse($"'{Source.Name}' has no face '{face}'.");
        return Restate(C3dKernel.MoveFace(b, f, by));
    }

    // ── Vertex Move ──────────────────────────────────────────────────────────────────────────

    /// <summary>R-em3d47-5 — what a cylinder says to a vertex move.</summary>
    public const string CylinderVertexMove = "A cylinder's cap centres snap and measure but do not move. Convert to Polyhedron to make real vertices.";

    /// <summary>R-em3d47-3e — vertex <paramref name="vertex"/> (an index into <see cref="Vertices"/>) moved to
    /// <paramref name="to"/> (own frame).</summary>
    /// <para>brief-em3d-101 R-em3d101-3e — on an image sheet's <c>Rect</c>, a corner moved RESIZES the rectangle with the opposite
    /// corner fixed, keeping its aspect unless <paramref name="freeAspect"/> (Shift), and it stays a <c>Rect</c>: an outline would
    /// turn the resize into a clip of the picture. The aspect kept is <paramref name="aspect"/> — the image's pixels, width ÷
    /// height, which the caller reads — or the rectangle's own when that is null (a file that does not read).</para>
    public C3dFaceEditResult MoveVertex(int vertex, C3dPoint3 to, bool freeAspect = false, double? aspect = null)
    {
        switch (Source)
        {
            case C3dCylinder: return C3dFaceEditResult.Refuse(CylinderVertexMove);
            case C3dSphere: return C3dFaceEditResult.Refuse(SphereVertexMove);
            case C3dEllipsoid: return C3dFaceEditResult.Refuse(EllipsoidVertexMove);
            case C3dSheet { Image: not null, Rect: { } rect } img when vertex is >= 0 and < 4: return ImageCorner(img, rect, vertex, to, freeAspect, aspect);
            case C3dSheet { Ellipse: not null }: return C3dFaceEditResult.Refuse(EllipseVertexMove);
            case C3dSheet s: return SheetVertex(s, vertex, to);
            case C3dPolyline: return C3dFaceEditResult.Refuse("A polyline is construction geometry: edit it by drawing it again.");
            case C3dWire w:
            {
                // brief-em3d-50 R-em3d50-3c — one axis point moved; the editor re-seats the feet on release.
                if (vertex < 0 || vertex >= w.Points.Count) return C3dFaceEditResult.Refuse("There is no such point on the wire.");
                var copy = Copy(w);
                copy.Points[vertex] = to;
                return Done(copy);
            }
        }
        if (Brep is not { } b || vertex < 0 || vertex >= b.Vertices.Count) return C3dFaceEditResult.Refuse("There is no such vertex.");
        return Restate(C3dKernel.MoveVertex(b, vertex, to));
    }

    /// <summary>
    /// The points the Inspector lists for an outlined object, own frame, in <see cref="MoveOutlinePoint"/>'s order: a sheet's
    /// outline then each hole (its rectangle's four corners when it has one), a prism's bottom outline then each hole (at its
    /// Offset), a polyline's vertices. <see cref="Rings"/> gives each ring's length, outline first. Empty for anything else.
    /// </summary>
    public static IReadOnlyList<C3dPoint3> OutlinePoints(C3dObject o) => o switch
    {
        C3dSheet { Ellipse: not null } => [],   // brief-hzn-13 — an ellipse is edited by its centre and radii
        C3dSheet s => SheetCorners(s),
        C3dPrism p => [.. p.Outline.Concat(p.Holes.SelectMany(h => h)).Select(q => C3dBrepBuild.OnPlane(p.Plane, q.U, q.V, p.Offset))],
        C3dPolyline l => l.Points3 is { } p3 ? [.. p3] : [.. l.Points.Select(q => C3dBrepBuild.OnPlane(l.Plane, q.U, q.V, l.Offset))],
        _ => [],
    };

    /// <summary>The ring lengths behind <see cref="OutlinePoints"/>: the outline, then each hole.</summary>
    public static IReadOnlyList<int> Rings(C3dObject o) => o switch
    {
        C3dSheet { Ellipse: not null } => [],
        C3dSheet { Rect: not null } => [4],
        C3dSheet s => [s.Outline.Count, .. s.Holes.Select(h => h.Count)],
        C3dPrism p => [p.Outline.Count, .. p.Holes.Select(h => h.Count)],
        C3dPolyline l => [l.VertexCount],
        _ => [],
    };

    /// <summary>The drawing plane an outlined object's points must stay on, or null when they may go anywhere (a polyline).</summary>
    public static C3dPlane? PlaneOf(C3dObject o) => o switch { C3dSheet s => s.Plane, C3dPrism p => p.Plane, _ => null };

    /// <summary>
    /// The Inspector's typed point: point <paramref name="vertex"/> of <see cref="OutlinePoints"/> moved to <paramref name="to"/>
    /// (own frame). A sheet's and a prism's stay on their plane — a prism's bottom moves and its top follows, so it stays a
    /// prism — and their outline must not cross itself. A polyline's may leave its plane: it is then stored in 3D form, which
    /// is refused while its planar points hold expressions (they would have nothing to bind to). A typed number replaces any
    /// expression that point held.
    /// </summary>
    public C3dFaceEditResult MoveOutlinePoint(int vertex, C3dPoint3 to)
    {
        switch (Source)
        {
            case C3dSheet { Ellipse: not null }: return C3dFaceEditResult.Refuse(EllipseVertexMove);
            case C3dSheet s:
            {
                // brief-hzn-13 R-hzn13-1 — a typed point is numbers: it replaces the expressions that point held.
                var r = SheetVertex(s, vertex, to);
                if (r.Object is C3dSheet moved && vertex < moved.Outline.Count) Unbind(moved, nameof(C3dSheet.Outline), vertex, 2);
                return r;
            }
            case C3dPrism p:
            {
                var (u, v, w) = C3dBrepBuild.ToPlane(p.Plane, to);
                if (w != p.Offset)
                    return C3dFaceEditResult.Refuse($"A prism's outline is its bottom, on its {p.Plane} plane: change its Offset or Height to move it along the normal.");
                var copy = Copy(p);
                if (!SetRingPoint(copy.Outline, copy.Holes, vertex, new C3dPoint2(u, v))) return C3dFaceEditResult.Refuse("There is no such vertex.");
                foreach (var ring in copy.Holes.Prepend(copy.Outline))
                    if (SelfCrossing(ring)) return C3dFaceEditResult.Refuse($"'{p.Name}''s outline would cross itself.");
                if (vertex < copy.Outline.Count) Unbind(copy, nameof(C3dPrism.Outline), vertex, 2);   // brief-hzn-13 R-hzn13-1
                return Done(copy);
            }
            case C3dPolyline l:
            {
                if (vertex < 0 || vertex >= l.VertexCount) return C3dFaceEditResult.Refuse("There is no such vertex.");
                var copy = Copy(l);
                if (copy.Points3 is { } p3)
                {
                    p3[vertex] = to;
                    Unbind(copy, nameof(C3dPolyline.Points3), vertex, 3);
                    return Done(copy);
                }
                var (u, v, w) = C3dBrepBuild.ToPlane(copy.Plane, to);
                if (w == copy.Offset)
                {
                    copy.Points[vertex] = new C3dPoint2(u, v);
                    Unbind(copy, nameof(C3dPolyline.Points), vertex, 2);
                    return Done(copy);
                }
                if (copy.Exprs?.Keys.Any(k => k.StartsWith(nameof(C3dPolyline.Points) + "[", StringComparison.Ordinal)) == true)
                    return C3dFaceEditResult.Refuse($"'{l.Name}''s points hold expressions in its {l.Plane} plane: a point off the plane would leave them nothing to bind to. Type a point on the plane, or replace the expressions with numbers first.");
                copy.Points3 = [.. copy.Points.Select(q => C3dBrepBuild.OnPlane(copy.Plane, q.U, q.V, copy.Offset))];
                copy.Points3[vertex] = to;
                copy.Points = [];
                return Done(copy);
            }
        }
        return C3dFaceEditResult.Refuse($"A {Kind}'s points are not edited one by one.");
    }

    /// <summary>Point <paramref name="k"/> counted through the outline, then each hole, set to <paramref name="p"/>.</summary>
    private static bool SetRingPoint(List<C3dPoint2> outline, List<List<C3dPoint2>> holes, int k, C3dPoint2 p)
    {
        if (k < 0) return false;
        if (k < outline.Count) { outline[k] = p; return true; }
        k -= outline.Count;
        int h = 0;
        while (h < holes.Count && k >= holes[h].Count) k -= holes[h++].Count;
        if (h >= holes.Count) return false;
        holes[h][k] = p;
        return true;
    }

    /// <summary>Every component of point <paramref name="k"/> of the list <paramref name="property"/> made a number.</summary>
    private static void Unbind(C3dObject l, string property, int k, int components)
    {
        if (C3dBindings.SpecOf(l.GetType(), property) is not { } spec) return;
        var element = spec.ElementAt(k);
        for (int c = 0; c < components; c++) C3dBindings.SetExpr(l, element, c, null);
    }

    private static List<C3dPoint3> SheetCorners(C3dSheet s)
    {
        IEnumerable<C3dPoint2> uv = s.Rect is { } r
            ? [r.Min, new(r.Min.U + r.Size.U, r.Min.V), new(r.Min.U + r.Size.U, r.Min.V + r.Size.V), new(r.Min.U, r.Min.V + r.Size.V)]
            : s.Outline.Concat(s.Holes.SelectMany(h => h));
        return [.. uv.Select(p => C3dBrepBuild.OnPlane(s.Plane, p.U, p.V, s.Offset))];
    }

    /// <summary>brief-em3d-101 — corner <paramref name="vertex"/> (0 min, 1 +u, 2 max, 3 +v) of an image sheet's rectangle moved to
    /// <paramref name="to"/>: the opposite corner stays, the size follows the corner — at <paramref name="imageAspect"/> (the
    /// rectangle's own when null) unless <paramref name="freeAspect"/> — and the result is still a rectangle.</summary>
    private C3dFaceEditResult ImageCorner(C3dSheet s, C3dRect r, int vertex, C3dPoint3 to, bool freeAspect, double? imageAspect)
    {
        var (u, v, w) = C3dBrepBuild.ToPlane(s.Plane, to);
        if (w != s.Offset) return C3dFaceEditResult.Refuse("A sheet's corner moves in its plane only.");
        long u0 = r.Min.U, v0 = r.Min.V, u1 = r.Min.U + r.Size.U, v1 = r.Min.V + r.Size.V;
        // The opposite corner, fixed.
        long fu = vertex is 1 or 2 ? u0 : u1, fv = vertex is 2 or 3 ? v0 : v1;
        long du = u - fu, dv = v - fv;
        if (!freeAspect && r.Size.U != 0 && r.Size.V != 0)
        {
            double aspect = imageAspect is > 0 and < double.PositiveInfinity ? imageAspect.Value : Math.Abs((double)r.Size.U / r.Size.V);
            // The dimension the drag moved further, relative to the aspect, leads; the other follows.
            if (Math.Abs(du) >= Math.Abs(dv) * aspect) dv = Math.Sign(dv == 0 ? (vertex is 2 or 3 ? 1 : -1) : dv) * (long)Math.Round(Math.Abs(du) / aspect);
            else du = Math.Sign(du == 0 ? (vertex is 1 or 2 ? 1 : -1) : du) * (long)Math.Round(Math.Abs(dv) * aspect);
        }
        if (du == 0 || dv == 0) return C3dFaceEditResult.Refuse($"'{s.Name}' would have no area.");
        var copy = Copy(s);
        copy.Rect = new C3dRect { Min = new C3dPoint2(Math.Min(fu, fu + du), Math.Min(fv, fv + dv)), Size = new C3dPoint2(Math.Abs(du), Math.Abs(dv)) };
        return Done(copy);
    }

    private C3dFaceEditResult SheetVertex(C3dSheet s, int vertex, C3dPoint3 to)
    {
        var (u, v, w) = C3dBrepBuild.ToPlane(s.Plane, to);
        if (w != s.Offset) return C3dFaceEditResult.Refuse("A sheet's corner moves in its plane only.");
        var copy = Copy(s);
        if (copy.Rect is { } r)
        {
            copy.Outline = [r.Min, new(r.Min.U + r.Size.U, r.Min.V), new(r.Min.U + r.Size.U, r.Min.V + r.Size.V), new(r.Min.U, r.Min.V + r.Size.V)];
            copy.Rect = null;
        }
        int k = vertex;
        if (k < 0) return C3dFaceEditResult.Refuse("There is no such vertex.");
        if (k < copy.Outline.Count) copy.Outline[k] = new C3dPoint2(u, v);
        else
        {
            k -= copy.Outline.Count;
            int h = 0;
            while (h < copy.Holes.Count && k >= copy.Holes[h].Count) k -= copy.Holes[h++].Count;
            if (h >= copy.Holes.Count) return C3dFaceEditResult.Refuse("There is no such vertex.");
            copy.Holes[h][k] = new C3dPoint2(u, v);
        }
        foreach (var ring in copy.Holes.Prepend(copy.Outline))
            if (SelfCrossing(ring)) return C3dFaceEditResult.Refuse($"'{s.Name}''s outline would cross itself.");
        return Done(copy);
    }

    /// <summary>Whether a ring has no area, or two of its non-adjacent edges meet.</summary>
    private static bool SelfCrossing(List<C3dPoint2> ring)
    {
        int n = ring.Count;
        Int128 area = 0;
        for (int i = 0; i < n; i++) area += (Int128)ring[i].U * ring[(i + 1) % n].V - (Int128)ring[(i + 1) % n].U * ring[i].V;
        if (area == 0) return true;
        C3dPoint3 P(C3dPoint2 p) => new(p.U, p.V, 0);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                // Two edges in one plane meet exactly when the degenerate "triangles" along them do: test as segments.
                var (a, b, c, d) = (P(ring[i]), P(ring[(i + 1) % n]), P(ring[j]), P(ring[(j + 1) % n]));
                if (SegmentsMeet(a, b, c, d)) return true;
            }
        return false;
    }

    private static bool SegmentsMeet(C3dPoint3 p, C3dPoint3 q, C3dPoint3 r, C3dPoint3 s)
    {
        static int O(C3dPoint3 a, C3dPoint3 b, C3dPoint3 c)
            => ((Int128)(b.X - a.X) * (c.Y - a.Y) - (Int128)(b.Y - a.Y) * (c.X - a.X)).CompareTo(Int128.Zero);
        static bool On(C3dPoint3 a, C3dPoint3 b, C3dPoint3 c)
            => Math.Min(a.X, b.X) <= c.X && c.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= c.Y && c.Y <= Math.Max(a.Y, b.Y);
        int d1 = O(r, s, p), d2 = O(r, s, q), d3 = O(p, q, r), d4 = O(p, q, s);
        if (d1 * d2 < 0 && d3 * d4 < 0) return true;
        return (d1 == 0 && On(r, s, p)) || (d2 == 0 && On(r, s, q)) || (d3 == 0 && On(p, q, r)) || (d4 == 0 && On(p, q, s));
    }

    // ── the kernel's answer, restated ────────────────────────────────────────────────────────

    /// <summary>R-em3d47-1b — the kernel's result as the source's own kind when it reads back as one, else as a polyhedron
    /// with the same face names.</summary>
    private C3dFaceEditResult Restate(C3dKernelResult k)
    {
        if (k.Brep is not { } r) return new C3dFaceEditResult(null, k.Refusal, false, k.Folds, k.Attempt, k.Stats);
        if (ReferenceEquals(r, Brep)) return new C3dFaceEditResult(Copy(Source), null, false, k.Folds, null, k.Stats);
        if (k.Folds.Count == 0)
        {
            if (Source is C3dBox box && C3dBrepBuild.ReadBox(r) is var (min, size))
            {
                var copy = Copy(box);
                copy.Min = min;
                copy.Size = size;
                return new C3dFaceEditResult(copy, null, false, k.Folds, null, k.Stats);
            }
            if (Source is C3dPrism prism && C3dBrepBuild.ReadPrism(r, prism) is { } p)
                return new C3dFaceEditResult(p, null, false, k.Folds, null, k.Stats);
        }
        return new C3dFaceEditResult(C3dBrepBuild.ToPolyhedron(r, Source), null, Source is not C3dPolyhedron, k.Folds, null, k.Stats);
    }

    private static T Copy<T>(T o) where T : C3dObject => (T)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(o));

    private static C3dFaceEditResult Done(C3dObject o) => new(o, null, false, C3dFaceEditResult.NoFolds, null, null);

    // ── Convert to Polyhedron (R-em3d47-2) ───────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="o"/> as a polyhedron with the same face names — a cylinder cut into <paramref name="facets"/> sides
    /// (default: the tessellation's own count), its side's pieces named <c>side.&lt;k&gt;</c> (a fold's naming, so what was
    /// attached to <c>side</c> goes to every piece). A cylinder is never faceted any other way.
    /// </summary>
    public static C3dFaceEditResult ConvertToPolyhedron(C3dObject o, int? facets = null)
    {
        switch (o)
        {
            case C3dPolyhedron: return C3dFaceEditResult.Refuse($"'{o.Name}' is already a polyhedron.");
            case C3dSheet or C3dPolyline: return C3dFaceEditResult.Refuse($"'{o.Name}' is not a solid.");
            case C3dSphere: return C3dFaceEditResult.Refuse(C3dFaceEditor.SphereConvert);
            case C3dEllipsoid: return C3dFaceEditResult.Refuse(C3dFaceEditor.EllipsoidConvert);
            case C3dCylinder c:
            {
                int n = facets ?? Engine.Em3d.Em3dTessellation.CylinderSegments;
                if (n < 3) return C3dFaceEditResult.Refuse("A cylinder needs at least three sides.");
                if (C3dBrepBuild.Cylinder(c, n, out var why) is not { } b) return C3dFaceEditResult.Refuse(why!);
                var folds = new Dictionary<string, IReadOnlyList<string>> { ["side"] = [.. Enumerable.Range(0, n).Select(k => $"side.{k}")] };
                return new C3dFaceEditResult(C3dBrepBuild.ToPolyhedron(b, c), null, true, folds, null, null);
            }
        }
        if (C3dBrepBuild.Of(o) is not { } brep) return C3dFaceEditResult.Refuse($"'{o.Name}' is not a solid.");
        return new C3dFaceEditResult(C3dBrepBuild.ToPolyhedron(brep, o), null, true, C3dFaceEditResult.NoFolds, null, null);
    }
}

/// <summary>R-em3d47-4 — the face commands that make a NEW object, and Align's arithmetic.</summary>
public static class C3dFaceCommands
{
    /// <summary>The rings of face <paramref name="face"/> of <paramref name="o"/>, own frame, and whether it lies across a
    /// world axis of that frame (the axis and its outward sign) — or why it has none to give.</summary>
    public static (List<List<C3dPoint3>> Rings, (int Axis, int Sign)? Aligned)? Polygon(C3dObject o, string face, out string? refusal)
    {
        refusal = null;
        switch (o)
        {
            case C3dCylinder:
                refusal = face is "top" or "bottom"
                    ? "A cylinder's cap is round: Convert to Polyhedron first."
                    : "A cylinder's side is curved: Convert to Polyhedron first.";
                return null;
            case C3dSphere:
                refusal = "A sphere's surface is curved, and this needs a flat face.";
                return null;
            case C3dEllipsoid:
                refusal = "An ellipsoid's surface is curved, and this needs a flat face.";
                return null;
            case C3dSheet s:
            {
                var (_, _, w) = C3dBrepBuild.Axes(s.Plane);
                var (ring0, holes0) = C3dEllipses.Polygon(s);   // brief-hzn-13 — a rectangle, an outline or an ellipse's polygon
                List<List<C3dPoint2>> uv = [ring0, .. holes0];
                return ([.. uv.Select(ring => ring.Select(p => C3dBrepBuild.OnPlane(s.Plane, p.U, p.V, s.Offset)).ToList())], (w, 1));
            }
        }
        if (C3dBrepBuild.Of(o) is not { } b || b.Topology.IndexOf(face) is not (>= 0 and var f))
        {
            refusal = $"'{o.Name}' has no face '{face}'.";
            return null;
        }
        var rings = b.Faces[f].Rings().Select(ring => ring.Select(i => b.Vertices[i]).ToList()).ToList();
        var aligned = b.AxisOf(f);
        // "Along an axis" means every vertex on one plane of that axis: an exactly flat face, not one within a DBU.
        if (aligned is var (axis, _) && rings.SelectMany(r => r).Any(p => C3dBrepBuild.Get(p, axis) != C3dBrepBuild.Get(rings[0][0], axis)))
            aligned = null;
        return (rings, aligned);
    }

    /// <summary>
    /// Extrude to New Solid (R-em3d47-4): a new object from face <paramref name="face"/>'s polygon, pulled
    /// <paramref name="distance"/> along its outward normal — a Prism when the face lies across an axis of the object's
    /// frame, a Polyhedron when it is tilted, a Cylinder from a cylinder's cap. It keeps the source's placement.
    /// </summary>
    public static C3dObject? Extrude(C3dObject source, string face, long distance, string name, string? material, out string? refusal)
    {
        refusal = null;
        if (distance == 0) { refusal = "Extrude: the distance is zero."; return null; }
        var placement = source.Placement.Clone();
        if (source is C3dCylinder c && face is "top" or "bottom")
        {
            int a = (int)c.Axis, s = Math.Sign(c.Length);
            var top = C3dBrepBuild.With(c.Base, a, C3dBrepBuild.Get(c.Base, a) + c.Length);
            return new C3dCylinder
            {
                Name = name, Material = material, Placement = placement, Axis = c.Axis, Radius = c.Radius,
                Base = face == "top" ? top : c.Base,
                Length = face == "top" ? s * distance : -s * distance,
            };
        }
        if (Polygon(source, face, out refusal) is not var (rings, aligned)) return null;
        if (aligned is var (axis, sign))
        {
            var plane = C3dBrepBuild.PlaneNormalTo(axis);
            List<C3dPoint2> Uv(List<C3dPoint3> ring) => [.. ring.Select(p => { var (u, v, _) = C3dBrepBuild.ToPlane(plane, p); return new C3dPoint2(u, v); })];
            return new C3dPrism
            {
                Name = name, Material = material, Placement = placement, Plane = plane,
                Offset = C3dBrepBuild.Get(rings[0][0], axis),
                Outline = Uv(rings[0]), Holes = [.. rings.Skip(1).Select(Uv)],
                Height = sign * distance,
            };
        }
        // A tilted face: the face and the face moved by the one rounded vector d·n, joined by a side per ring edge.
        var (nx, ny, nz) = Normal(rings);
        var off = new C3dPoint3(R(nx * distance), R(ny * distance), R(nz * distance));
        var vertices = new List<C3dPoint3>();
        var bottom = new List<int[]>();
        var topRings = new List<int[]>();
        foreach (var ring in rings)
        {
            bottom.Add([.. ring.Select(p => { vertices.Add(p); return vertices.Count - 1; })]);
            topRings.Add([.. ring.Select(p => { vertices.Add(p + off); return vertices.Count - 1; })]);
        }
        var faces = new List<C3dBrepFace>
        {
            new("bottom", [.. bottom[0].Reverse()], [.. bottom.Skip(1).Select(h => h.Reverse().ToArray())]),
            new("top", topRings[0], [.. topRings.Skip(1)]),
        };
        for (int r = 0; r < rings.Count; r++)
            for (int k = 0; k < rings[r].Count; k++)
            {
                int j = (k + 1) % rings[r].Count;
                faces.Add(new C3dBrepFace(r == 0 ? $"side{k}" : $"hole{r - 1}.side{k}",
                                          [bottom[r][k], bottom[r][j], topRings[r][j], topRings[r][k]], []));
            }
        var brep = new C3dBrep(vertices, new C3dBrepTopology(faces, vertices.Count));
        if (brep.Volume6 < 0) brep = C3dBrepBuild.Reversed(brep);
        var poly = C3dBrepBuild.ToPolyhedron(brep, new C3dPolyhedron { Name = name, Material = material, Placement = placement });
        return poly;
    }

    /// <summary>
    /// Copy as Sheet (R-em3d47-4): a new sheet exactly on face <paramref name="face"/>, with its outline and holes — a
    /// <c>Rect</c> when the face is a rectangle along its plane's axes. Refused for a tilted face: sheets lie on XY, YZ or
    /// XZ in this version (brief 41).
    /// </summary>
    public static C3dSheet? CopyAsSheet(C3dObject source, string face, string name, string? material, double? thicknessUm, out string? refusal)
    {
        if (Polygon(source, face, out refusal) is not var (rings, aligned)) return null;
        if (aligned is not var (axis, _))
        {
            refusal = $"Face '{face}' is tilted in its object's frame, and a sheet lies on XY, YZ or XZ in this version.";
            return null;
        }
        var plane = C3dBrepBuild.PlaneNormalTo(axis);
        List<C3dPoint2> Uv(List<C3dPoint3> ring) => [.. ring.Select(p => { var (u, v, _) = C3dBrepBuild.ToPlane(plane, p); return new C3dPoint2(u, v); })];
        var outline = Uv(rings[0]);
        var sheet = new C3dSheet
        {
            Name = name, Material = material, ThicknessUm = thicknessUm, Placement = source.Placement.Clone(), Plane = plane,
            Offset = C3dBrepBuild.Get(rings[0][0], axis),
        };
        if (rings.Count == 1 && outline.Count == 4 && IsRect(outline) is { } rect) sheet.Rect = rect;
        else
        {
            sheet.Outline = outline;
            sheet.Holes = [.. rings.Skip(1).Select(Uv)];
        }
        return sheet;
    }

    private static C3dRect? IsRect(List<C3dPoint2> q)
    {
        long u0 = q.Min(p => p.U), u1 = q.Max(p => p.U), v0 = q.Min(p => p.V), v1 = q.Max(p => p.V);
        if (u1 == u0 || v1 == v0) return null;
        foreach (var p in q) if ((p.U != u0 && p.U != u1) || (p.V != v0 && p.V != v1)) return null;
        if (q.Distinct().Count() != 4) return null;
        return new C3dRect { Min = new C3dPoint2(u0, v0), Size = new C3dPoint2(u1 - u0, v1 - v0) };
    }

    private static (double X, double Y, double Z) Normal(List<List<C3dPoint3>> rings)
    {
        double x = 0, y = 0, z = 0;
        foreach (var ring in rings)
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i];
                var b = ring[(i + 1) % ring.Count];
                x += (double)(a.Y - b.Y) * (a.Z + b.Z);
                y += (double)(a.Z - b.Z) * (a.X + b.X);
                z += (double)(a.X - b.X) * (a.Y + b.Y);
            }
        double l = Math.Sqrt(x * x + y * y + z * z);
        return l > 0 ? (x / l, y / l, z / l) : (0, 0, 0);
    }

    private static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    // ── Align to Face (R-em3d47-4) ───────────────────────────────────────────────────────────

    /// <summary>
    /// A face's plane in the WORLD (DBU): a point on it and its outward unit normal, and — when the normal lies along a
    /// world axis and the point's coordinate on it is a whole DBU — that axis and coordinate, which is what makes Align
    /// exact.
    /// </summary>
    public readonly record struct WorldPlane(double Px, double Py, double Pz, double Nx, double Ny, double Nz, (int Axis, long At)? Exact);

    /// <summary>Face <paramref name="face"/> of <paramref name="o"/> in the world, through its placement; null (with the
    /// reason) for a curved face.</summary>
    public static WorldPlane? PlaneOf(C3dObject o, string face, out string? refusal)
    {
        if (o is C3dCylinder c && face is "top" or "bottom")
        {
            refusal = null;
            int a = (int)c.Axis;
            var centre = C3dFaceEditor.CapCentres(c)[face == "top" ? 1 : 0];
            int s = Math.Sign(c.Length) * (face == "top" ? 1 : -1);
            var local = C3dBrepBuild.With(default, a, s);
            return World(o, centre, (local.X, local.Y, local.Z));
        }
        if (Polygon(o, face, out refusal) is not var (rings, _)) return null;
        return World(o, rings[0][0], Normal(rings));
    }

    private static WorldPlane World(C3dObject o, C3dPoint3 p, (double X, double Y, double Z) n)
    {
        var t = o.Placement.ToTransform();
        var (px, py, pz) = t.Apply(p);
        double nx = t.M00 * n.X + t.M01 * n.Y + t.M02 * n.Z, ny = t.M10 * n.X + t.M11 * n.Y + t.M12 * n.Z, nz = t.M20 * n.X + t.M21 * n.Y + t.M22 * n.Z;
        return Plane(px, py, pz, nx, ny, nz, t.IsIntegral);
    }

    /// <summary>A plane from a point and a normal (world DBU); exact along an axis when <paramref name="whole"/> and the
    /// normal is one within 1e-12.</summary>
    public static WorldPlane Plane(double px, double py, double pz, double nx, double ny, double nz, bool whole)
    {
        (int Axis, long At)? exact = null;
        double[] n = [nx, ny, nz], p = [px, py, pz];
        for (int k = 0; k < 3; k++)
            if (Math.Abs(Math.Abs(n[k]) - 1) < 1e-12 && Math.Abs(n[(k + 1) % 3]) < 1e-12 && Math.Abs(n[(k + 2) % 3]) < 1e-12)
            {
                double r = Math.Round(p[k]);
                if (whole && Math.Abs(p[k] - r) <= 1e-6) exact = (k, (long)r);
            }
        return new WorldPlane(px, py, pz, nx, ny, nz, exact);
    }

    /// <summary>
    /// Align to Face: the translation, along the TARGET's normal, that makes the source face coplanar with the target —
    /// facing it (<paramref name="touching"/>) or pointing the same way (flush). Exact in DBU when both planes are.
    /// Refused when the faces are not parallel (Rotate first), or when they face the other way from what was asked.
    /// </summary>
    public static (C3dPoint3 By, bool Exact)? Align(WorldPlane source, WorldPlane target, bool touching, out string? refusal)
    {
        refusal = null;
        double cx = source.Ny * target.Nz - source.Nz * target.Ny, cy = source.Nz * target.Nx - source.Nx * target.Nz,
               cz = source.Nx * target.Ny - source.Ny * target.Nx;
        if (Math.Sqrt(cx * cx + cy * cy + cz * cz) > 1e-9)
        {
            refusal = "The faces are not parallel: Rotate the object first (R), then align.";
            return null;
        }
        double dot = source.Nx * target.Nx + source.Ny * target.Ny + source.Nz * target.Nz;
        if (touching && dot > 0)
        {
            refusal = "These faces point the same way, so they cannot touch: T switches to Flush, or pick the object's opposite face.";
            return null;
        }
        if (!touching && dot < 0)
        {
            refusal = "These faces face each other, so they cannot be flush: T switches to Touching, or pick the object's opposite face.";
            return null;
        }
        if (source.Exact is var (sa, sAt) && target.Exact is var (ta, tAt) && sa == ta)
            return (C3dBrepBuild.With(default, ta, tAt - sAt), true);
        double d = target.Nx * (target.Px - source.Px) + target.Ny * (target.Py - source.Py) + target.Nz * (target.Pz - source.Pz);
        return (new C3dPoint3(R(target.Nx * d), R(target.Ny * d), R(target.Nz * d)), false);
    }

    // ── what follows a fold (overview §1e, brief 49 §4a) ─────────────────────────────────────

    /// <summary>
    /// R-em3d47-3c — every <c>FaceBoundaries</c> entry on object <paramref name="objectName"/> naming a folded face, replaced
    /// by one entry per piece, in order; everything else untouched. Null when nothing named a folded face.
    /// </summary>
    public static List<C3dFaceBoundary>? FollowFolds(IReadOnlyList<C3dFaceBoundary> entries, string objectName, IReadOnlyDictionary<string, IReadOnlyList<string>> folds)
    {
        if (folds.Count == 0) return null;
        List<C3dFaceBoundary>? result = null;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Object == objectName && folds.TryGetValue(e.Face, out var pieces))
            {
                result ??= [.. entries.Take(i)];
                foreach (string piece in pieces)
                    result.Add(new C3dFaceBoundary { Object = e.Object, Face = piece, Kind = e.Kind, Material = e.Material, Unread = e.Unread });
                continue;
            }
            result?.Add(e);
        }
        return result;
    }
}
