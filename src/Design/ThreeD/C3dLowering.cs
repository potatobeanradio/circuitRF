// brief-em3d-42 R-em3d42-1d — a .c3d object as the RICHEST neutral primitive that states it exactly
// (overview §1g), and the rigid transforms that carry a lowered primitive into a parent's frame.
//
// THE TABLE (a test pins it, because the FDTD path's cost depends on it):
//
//   Box, placement a pure translation or 90° multiples        → Em3dBox
//   Prism on XY, no Shear, rotation about z only              → Em3dExtrudedPolygon (rotated outline)
//   Cylinder                                                  → Em3dCylinder (any axis: the rotation is applied)
//   Sphere                                                    → Em3dSphere (the placement moves its centre)
//   Ellipsoid (brief-sar-3)                                   → Em3dEllipsoid (the placement turns its axes)
//   Anything else                                             → Em3dPolyhedron
//
// A sheet lies flat (Em3dSheet at Z) under the same condition a prism stays an extrusion, and otherwise
// carries a frame (Em3dPlaneFrame). A polyline is construction geometry and lowers to nothing.
// A Boolean of coplanar sheets (brief-hzn-10) lowers here too, to one sheet geometry per piece of its result.
//
// brief-em3d-47 R-em3d47-1c: a Polyhedron that is exactly an axis-aligned box or a z-prism is recognised and
// lowers by the Box or the XY-prism row (C3dRecognition) — the document keeps the polyhedron.
//
// The table is applied to the WHOLE transform an object ends up under — its own placement, then every
// instance above it — so a box rotated by 30° inside an instance rotated by −30° is an Em3dBox again.
//
// Lengths go from DBU to metres through LayoutUnits' exact decimal path (R-em3d42-3b); placements then
// act in doubles. A 90° multiple is an integer matrix (C3dTransform's note), so an axis-aligned result is
// the exact decimal conversion plus one rounded addition per coordinate.
//
// FACE NAMES travel with every lowered primitive, indexed by the neutral primitive's own face numbering
// (Em3dTriangle.Face's): a box's world xmin…zmax, an extrusion's bottom, top and ring edges, a cylinder's
// bottom, top, side, and a polyhedron's faces by their Em3dFace.Name. The provenance map is built from
// them, so brief 43's picking and brief 49's boundaries find a face by the name the document gave it.

using CircuitRF.Design.Layout;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What an object lowered to: a solid's primitive, or a sheet's geometry — with the face names
/// indexed by the primitive's face numbering, and the table's row.</summary>
public sealed record C3dLowered(Em3dPrimitive? Solid, C3dSheetGeometry? Sheet, IReadOnlyList<string> FaceNames, string Kind)
{
    /// <summary>brief-sar-8b — a cylindrical sheet's patch, in the world; <see cref="Solid"/> and <see cref="Sheet"/> are then null.</summary>
    public Em3dCylinderPatch? CylinderSheet { get; init; }

    /// <summary>brief-sar-8b — why the object could not be lowered (a cylindrical sheet whose axis a placement tipped off x, y or z).</summary>
    public string? Refusal { get; init; }

    /// <summary>brief-hzn-10 R-hzn10-2 — every piece of a sheet boolean's result, <see cref="Sheet"/> the first; empty for anything
    /// else. Each piece is a sheet of its own in the problem, carrying the object's provenance.</summary>
    public IReadOnlyList<C3dSheetGeometry> SheetPieces { get; init; } = [];
}

/// <summary>A sheet's geometry, metres: its outline and holes at <see cref="Z"/>, flat or in <see cref="Frame"/>.</summary>
public sealed record C3dSheetGeometry(IReadOnlyList<Point2> Outline, IReadOnlyList<IReadOnlyList<Point2>> Holes,
                                      double Z, Em3dPlaneFrame? Frame);

public static class C3dLowering
{
    /// <summary>The lowering table's rows, as <see cref="C3dLowered.Kind"/> spells them.</summary>
    public const string KindBox = "box", KindExtrusion = "extruded-polygon", KindCylinder = "cylinder", KindSphere = "sphere", KindEllipsoid = "ellipsoid",
                        KindPolyhedron = "polyhedron", KindSheet = "sheet", KindFramedSheet = "sheet-in-a-plane",
                        KindCylinderSheet = "cylinder-sheet";

    /// <summary>DBU to metres, exactly: the DBU as a decimal number of micrometres (LayoutUnits), then to metres.</summary>
    public static double Metres(long dbu, int dbuPerMicron)
        => (double)(LayoutUnits.FromDbu(dbu, LayoutUnit.Um, dbuPerMicron) / 1_000_000m);

    /// <summary>A placement (translation in DBU) as the same transform with its translation in metres.</summary>
    public static C3dTransform InMetres(C3dTransform t, int dbuPerMicron) => t with
    {
        Tx = Metres(checked((long)t.Tx), dbuPerMicron),
        Ty = Metres(checked((long)t.Ty), dbuPerMicron),
        Tz = Metres(checked((long)t.Tz), dbuPerMicron),
    };

    /// <summary>
    /// <paramref name="obj"/> lowered under <paramref name="world"/> (metres; the instances above it), with its
    /// own placement applied first. Null for a polyline, which is never in the solved problem.
    /// </summary>
    public static C3dLowered? Lower(C3dObject obj, C3dTransform world, int dbuPerMicron)
    {
        double M(long v) => Metres(v, dbuPerMicron);
        var w = Snapped(InMetres(obj.Placement.ToTransform(), dbuPerMicron).Then(world));
        switch (obj)
        {
            case C3dBox b:
            {
                var lo = new Point3(M(b.Min.X), M(b.Min.Y), M(b.Min.Z));
                var hi = new Point3(M(b.Min.X + b.Size.X), M(b.Min.Y + b.Size.Y), M(b.Min.Z + b.Size.Z));
                return Transform(new Em3dBox(lo, hi), C3dBox.FaceNameList, w, KindBox);
            }
            case C3dPrism p:
            {
                var outline = p.Outline.Select(q => new Point2(M(q.U), M(q.V))).ToList();
                var holes = p.Holes.Select(h => (IReadOnlyList<Point2>)[.. h.Select(q => new Point2(M(q.U), M(q.V)))]).ToList();
                double h0 = M(p.Offset), h1 = M(p.Offset + p.Height);
                var sides = p.FaceNames().Skip(2).ToList();
                if (p.Plane == C3dPlane.XY && p.Shear == default)
                {
                    IReadOnlyList<string> names = h1 >= h0 ? ["bottom", "top", .. sides] : ["top", "bottom", .. sides];
                    return Transform(new Em3dExtrudedPolygon(outline, holes, Math.Min(h0, h1), Math.Max(h0, h1)),
                                     names, w, KindExtrusion);
                }
                var shear = new Point2(M(p.Shear.U), M(p.Shear.V));
                return Transform(Prism(p.Plane, outline, holes, h0, h1, shear, sides), [], w, KindPolyhedron);
            }
            case C3dCylinder c:
            {
                var b0 = new Point3(M(c.Base.X), M(c.Base.Y), M(c.Base.Z));
                double len = M(c.Length);
                var e = c.Axis switch
                {
                    C3dAxis.X => b0 with { X = b0.X + len },
                    C3dAxis.Y => b0 with { Y = b0.Y + len },
                    _         => b0 with { Z = b0.Z + len },
                };
                return Transform(new Em3dCylinder(b0, e, M(c.Radius)), C3dCylinder.FaceNameList, w, KindCylinder);
            }
            case C3dSphere { FacetVertices: > 0 } fs:
                // brief-hzn-12 — a faceted sphere is the hull of its lattice: a polyhedron, every face 'surface'
                return Transform(C3dFacetedSphere.Metres(new Point3(M(fs.Centre.X), M(fs.Centre.Y), M(fs.Centre.Z)), M(fs.Radius), fs.FacetVertices),
                                 [], w, KindPolyhedron);
            case C3dSphere sp:
                return Transform(new Em3dSphere(new Point3(M(sp.Centre.X), M(sp.Centre.Y), M(sp.Centre.Z)), M(sp.Radius)),
                                 C3dSphere.FaceNameList, w, KindSphere);
            case C3dEllipsoid el:
                return Transform(Em3dEllipsoid.Aligned(new Point3(M(el.Centre.X), M(el.Centre.Y), M(el.Centre.Z)),
                                                       M(el.SemiAxes.X), M(el.SemiAxes.Y), M(el.SemiAxes.Z)),
                                 C3dEllipsoid.FaceNameList, w, KindEllipsoid);
            case C3dPolyhedron ph:
            {
                // brief-em3d-47 R-em3d47-1c — an edited solid that is exactly a box or a z-prism again lowers as one.
                if (Kernel.C3dRecognition.Box(ph) is var (bMin, bMax, bNames))
                    return Transform(Kernel.C3dRecognition.BoxMetres(bMin, bMax, dbuPerMicron), bNames, w, KindBox);
                if (Kernel.C3dRecognition.ZPrism(ph) is var (rings, z0, z1, pNames))
                {
                    IReadOnlyList<Point2> Ring(List<C3dPoint2> r) => [.. r.Select(q => new Point2(M(q.U), M(q.V)))];
                    return Transform(new Em3dExtrudedPolygon(Ring(rings[0]), [.. rings.Skip(1).Select(Ring)], M(z0), M(z1)), pNames, w, KindExtrusion);
                }
                var faces = Kernel.C3dRecognition.ExactlyPlanarFaces(ph) ?? ph.Faces;
                var poly = new Em3dPolyhedron(
                    [.. ph.Vertices.Select(v => new Point3(M(v.X), M(v.Y), M(v.Z)))],
                    [.. faces.Select(f => new Em3dFace(f.Outer, [.. f.Holes.Select(h => (IReadOnlyList<int>)h)], f.Name))]);
                return Transform(poly, [], w, KindPolyhedron);
            }
            case C3dSheet s:
            {
                List<Point2> outline;
                List<IReadOnlyList<Point2>> holes = [];
                if (s.Rect is { } r)
                {
                    double u0 = M(r.Min.U), v0 = M(r.Min.V), u1 = M(r.Min.U + r.Size.U), v1 = M(r.Min.V + r.Size.V);
                    outline = [new(u0, v0), new(u1, v0), new(u1, v1), new(u0, v1)];
                }
                else
                {
                    outline = [.. s.Outline.Select(q => new Point2(M(q.U), M(q.V)))];
                    holes = [.. s.Holes.Select(h => (IReadOnlyList<Point2>)[.. h.Select(q => new Point2(M(q.U), M(q.V)))])];
                }
                double h = M(s.Offset);
                var sheet = s.Plane == C3dPlane.XY
                    ? new C3dSheetGeometry(outline, holes, h, null)
                    : new C3dSheetGeometry(outline, holes, 0, PlaneFrame(s.Plane, h));
                var placed = TransformSheet(sheet, w);
                return new C3dLowered(null, placed, [], placed.Frame is null ? KindSheet : KindFramedSheet);
            }
            case C3dBoolean sb when C3dSheetBooleans.IsSheetBoolean(sb):
                return SheetBoolean(sb, w, dbuPerMicron);
            case C3dCylinderSheet cs:
            {
                var (patch, why) = CylinderPatch(cs.Rect, w, dbuPerMicron);
                return new C3dLowered(null, null, [], KindCylinderSheet)
                       { CylinderSheet = patch, Refusal = why is null ? null : $"Sheet '{cs.Name}' {why}" };
            }
            default:
                return null;
        }
    }

    /// <summary>The row a sheet boolean's result takes: a sheet, built in circuitRF from its operands' polygons.</summary>
    public const string KindSheetBoolean = "sheet (boolean of sheets)";

    /// <summary>
    /// brief-hzn-10 R-hzn10-2 — a Boolean of coplanar sheets: its operands' polygons combined exactly in DBU
    /// (<see cref="C3dSheetBooleans.Result"/>), each piece a sheet in its plane carried by <paramref name="w"/> (the boolean's own
    /// placement, then the world). Operands out of one plane, and an empty result, are its refusal.
    /// </summary>
    private static C3dLowered SheetBoolean(C3dBoolean b, C3dTransform w, int dbuPerMicron)
    {
        string name = b.Name;
        if (C3dSheetBooleans.PlaneFaults(b, name) is [var (operand, why), ..])
            return new C3dLowered(null, null, [], KindSheetBoolean) { Refusal = C3dDiagnostics.SheetBooleanPlane(name, operand, why).Render() };
        var result = C3dSheetBooleans.Result(b);
        if (result.Pieces.Count == 0)
            return new C3dLowered(null, null, [], KindSheetBoolean) { Refusal = C3dBooleans.EmptyResult(b, name) };
        double M(long v) => Metres(v, dbuPerMicron);
        double h = M(result.Offset);
        var pieces = new List<C3dSheetGeometry>(result.Pieces.Count);
        foreach (var piece in result.Pieces)
        {
            List<Point2> outline = [.. piece.Outline.Select(q => new Point2(M(q.U), M(q.V)))];
            List<IReadOnlyList<Point2>> holes = [.. piece.Holes.Select(r => (IReadOnlyList<Point2>)[.. r.Select(q => new Point2(M(q.U), M(q.V)))])];
            var sheet = result.Plane == C3dPlane.XY
                ? new C3dSheetGeometry(outline, holes, h, null)
                : new C3dSheetGeometry(outline, holes, 0, PlaneFrame(result.Plane, h));
            pieces.Add(TransformSheet(sheet, w));
        }
        return new C3dLowered(null, pieces[0], [], KindSheetBoolean) { SheetPieces = pieces };
    }

    /// <summary>
    /// brief-sar-8b R-sar8b-2 — a cylindrical rectangle as its world patch under <paramref name="w"/> (metres), or why it cannot be one:
    /// a placement may move it and turn it, so long as its axis stays along x, y or z. The azimuth range follows the turn (and a
    /// mirror reverses it), so the patch is still openEMS's α range about the axis it now has.
    /// </summary>
    public static (Em3dCylinderPatch? Patch, string? Refusal) CylinderPatch(C3dCylinderRect r, C3dTransform w, int dbuPerMicron)
    {
        double M(long v) => Metres(v, dbuPerMicron);
        int a = (int)r.Axis, u = (a + 1) % 3, v = (a + 2) % 3;
        var b0 = new Point3(M(r.Base.X), M(r.Base.Y), M(r.Base.Z));
        double len = M(r.Length), radius = M(r.Radius);
        double s0 = r.StartDeg * Math.PI / 180, s1 = (r.StartDeg + r.SweepDeg) * Math.PI / 180;
        double a0 = Math.Min(s0, s1), a1 = Math.Max(s0, s1);
        double z0 = Get(b0, a) + Math.Min(0, len), z1 = Get(b0, a) + Math.Max(0, len);
        if (w == C3dTransform.Identity) return (new Em3dCylinderPatch(a, b0, radius, a0, a1, z0, z1), null);

        static Point3 Unit(int axis) => axis switch { 0 => new(1, 0, 0), 1 => new(0, 1, 0), _ => new(0, 0, 1) };
        var axisDir = Rotate(w, Unit(a));
        int k = Enumerable.Range(0, 3).FirstOrDefault(i => Math.Abs(Get(axisDir, i)) > 1 - 1e-12, -1);
        if (k < 0) return (null, "is placed so that its axis lies along none of x, y and z, and a cylindrical sheet is round about one of them.");
        int nu = (k + 1) % 3, nv = (k + 2) % 3;
        var du = Rotate(w, Unit(u));
        var dv = Rotate(w, Unit(v));
        double phi = Math.Atan2(Get(du, nv), Get(du, nu));
        double sense = Get(du, nu) * Get(dv, nv) - Get(du, nv) * Get(dv, nu);
        var (n0, n1) = sense > 0 ? (phi + a0, phi + a1) : (phi - a1, phi - a0);
        var origin = Apply(w, b0);
        double e0 = Get(Apply(w, b0), k), e1 = Get(Apply(w, Add(b0, a, len)), k);
        return (new Em3dCylinderPatch(k, origin, radius, n0, n1, Math.Min(e0, e1), Math.Max(e0, e1)), null);

        static Point3 Add(Point3 q, int axis, double d) => axis switch { 0 => q with { X = q.X + d }, 1 => q with { Y = q.Y + d }, _ => q with { Z = q.Z + d } };
    }

    private static double Get(Point3 q, int axis) => axis switch { 0 => q.X, 1 => q.Y, _ => q.Z };

    /// <summary>A drawing plane's frame at <paramref name="offset"/> along its normal: u and v are the plane's
    /// two axes in the object's own frame (C3dPlane's note).</summary>
    private static Em3dPlaneFrame PlaneFrame(C3dPlane plane, double offset) => plane switch
    {
        C3dPlane.YZ => new(new Point3(offset, 0, 0), new Point3(0, 1, 0), new Point3(0, 0, 1)),
        C3dPlane.XZ => new(new Point3(0, offset, 0), new Point3(1, 0, 0), new Point3(0, 0, 1)),
        _           => new(new Point3(0, 0, offset), new Point3(1, 0, 0), new Point3(0, 1, 0)),
    };

    /// <summary>A point of a drawing plane, (u, v) at height h along its normal, in the object's frame.</summary>
    public static Point3 OnPlane(C3dPlane plane, double u, double v, double h) => plane switch
    {
        C3dPlane.YZ => new(h, u, v),
        C3dPlane.XZ => new(u, h, v),
        _           => new(u, v, h),
    };

    /// <summary>
    /// A prism as a polyhedron: its polygon at <paramref name="h0"/>, the same polygon moved by
    /// <paramref name="shear"/> at <paramref name="h1"/>, and a side face per ring edge. Built in the
    /// document's own vertex order so side k is still edge k → k+1, each face turned outward by the rule
    /// for its ring's winding, then the whole solid turned inside out if the plane is left-handed or the
    /// height negative (the signed volume says which).
    /// </summary>
    public static Em3dPolyhedron Prism(C3dPlane plane, IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes,
                                       double h0, double h1, Point2 shear, IReadOnlyList<string> sideNames)
    {
        var rings = new List<IReadOnlyList<Point2>> { outline };
        rings.AddRange(holes);
        var vertices = new List<Point3>();
        var bottom = new List<int[]>();
        var top = new List<int[]>();
        foreach (var r in rings)
        {
            var b = new int[r.Count];
            var t = new int[r.Count];
            for (int k = 0; k < r.Count; k++)
            {
                b[k] = vertices.Count; vertices.Add(OnPlane(plane, r[k].X, r[k].Y, h0));
                t[k] = vertices.Count; vertices.Add(OnPlane(plane, r[k].X + shear.X, r[k].Y + shear.Y, h1));
            }
            bottom.Add(b); top.Add(t);
        }
        // Winding in (u, v): counter-clockwise is positive. The outline wants CCW on top, a hole CW.
        bool Ccw(int i) => Em3dPolygonTriangulation.SignedArea2(rings[i]) > 0;
        IReadOnlyList<int> Maybe(IReadOnlyList<int> ring, bool reverse) => reverse ? [.. ring.Reverse()] : ring;

        var faces = new List<Em3dFace>
        {
            new(Maybe(bottom[0], Ccw(0)), [.. Enumerable.Range(1, holes.Count).Select(i => Maybe(bottom[i], !Ccw(i)))], "bottom"),
            new(Maybe(top[0], !Ccw(0)), [.. Enumerable.Range(1, holes.Count).Select(i => Maybe(top[i], Ccw(i)))], "top"),
        };
        int side = 0;
        for (int i = 0; i < rings.Count; i++)
        {
            int n = rings[i].Count;
            bool outward = i == 0 ? Ccw(i) : !Ccw(i);
            for (int k = 0; k < n; k++)
            {
                int j = (k + 1) % n;
                IReadOnlyList<int> quad = [bottom[i][k], bottom[i][j], top[i][j], top[i][k]];
                faces.Add(new Em3dFace(Maybe(quad, !outward), [], side < sideNames.Count ? sideNames[side] : $"side{side}"));
                side++;
            }
        }
        var poly = new Em3dPolyhedron(vertices, faces);
        return poly.SignedVolume() < 0 ? Reversed(poly) : poly;
    }

    /// <summary>Every ring of every face turned the other way — a solid seen inside out, turned back.</summary>
    public static Em3dPolyhedron Reversed(Em3dPolyhedron p)
        => p with { Faces = [.. p.Faces.Select(f => new Em3dFace([.. f.Outer.Reverse()],
                                                                  [.. f.Holes.Select(h => (IReadOnlyList<int>)[.. h.Reverse()])], f.Name))] };

    // ── transforms ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The transform with every matrix entry within 1e-12 of an integer set to it: rotations that compose to a
    /// quarter turn (30° inside −30°) are exact again, so the table sees the box they state. The translation is
    /// untouched; the entries moved are rounding error in a composition of exact rotations.
    /// </summary>
    public static C3dTransform Snapped(C3dTransform t)
    {
        static double S(double v) => Math.Abs(v - Math.Round(v)) <= 1e-12 ? Math.Round(v) + 0.0 : v;
        return t with
        {
            M00 = S(t.M00), M01 = S(t.M01), M02 = S(t.M02),
            M10 = S(t.M10), M11 = S(t.M11), M12 = S(t.M12),
            M20 = S(t.M20), M21 = S(t.M21), M22 = S(t.M22),
        };
    }

    /// <summary>True when the matrix maps each axis onto an axis (0, ±1 entries): a box stays a box.</summary>
    public static bool IsSignedPermutation(C3dTransform t)
        => t.IntegerMatrix() is { } m && m.All(v => v is -1 or 0 or 1) &&
           Enumerable.Range(0, 3).All(r => Math.Abs(m[3 * r]) + Math.Abs(m[3 * r + 1]) + Math.Abs(m[3 * r + 2]) == 1);

    /// <summary>True when the matrix keeps z along z and x, y in their plane: an extrusion stays one.</summary>
    public static bool IsZOnly(C3dTransform t) => t.M02 == 0 && t.M12 == 0 && t.M20 == 0 && t.M21 == 0;

    private static double Det(C3dTransform t)
        => t.M00 * (t.M11 * t.M22 - t.M12 * t.M21) - t.M01 * (t.M10 * t.M22 - t.M12 * t.M20) + t.M02 * (t.M10 * t.M21 - t.M11 * t.M20);

    public static Point3 Apply(C3dTransform t, Point3 p)
        => new(t.M00 * p.X + t.M01 * p.Y + t.M02 * p.Z + t.Tx,
               t.M10 * p.X + t.M11 * p.Y + t.M12 * p.Z + t.Ty,
               t.M20 * p.X + t.M21 * p.Y + t.M22 * p.Z + t.Tz);

    private static Point3 Rotate(C3dTransform t, Point3 p)
        => new(t.M00 * p.X + t.M01 * p.Y + t.M02 * p.Z, t.M10 * p.X + t.M11 * p.Y + t.M12 * p.Z, t.M20 * p.X + t.M21 * p.Y + t.M22 * p.Z);

    /// <summary>
    /// A lowered primitive carried by <paramref name="t"/>, still the richest primitive that states it
    /// exactly, with <paramref name="faceNames"/> (indexed by its face numbering) re-indexed for the result.
    /// </summary>
    public static C3dLowered Transform(Em3dPrimitive prim, IReadOnlyList<string> faceNames, C3dTransform t, string kind)
    {
        switch (prim)
        {
            case Em3dBox b when IsSignedPermutation(t):
            {
                var a = Apply(t, b.Min); var c = Apply(t, b.Max);
                var box = new Em3dBox(new Point3(Math.Min(a.X, c.X), Math.Min(a.Y, c.Y), Math.Min(a.Z, c.Z)),
                                      new Point3(Math.Max(a.X, c.X), Math.Max(a.Y, c.Y), Math.Max(a.Z, c.Z)));
                // World face f's normal, carried back into the box's own frame (Mᵀ), names the face.
                var names = new string[6];
                for (int f = 0; f < 6; f++)
                {
                    var n = new Point3(f / 2 == 0 ? (f % 2 == 0 ? -1 : 1) : 0, f / 2 == 1 ? (f % 2 == 0 ? -1 : 1) : 0,
                                       f / 2 == 2 ? (f % 2 == 0 ? -1 : 1) : 0);
                    var o = new Point3(t.M00 * n.X + t.M10 * n.Y + t.M20 * n.Z, t.M01 * n.X + t.M11 * n.Y + t.M21 * n.Z,
                                       t.M02 * n.X + t.M12 * n.Y + t.M22 * n.Z);
                    int axis = o.X != 0 ? 0 : o.Y != 0 ? 1 : 2;
                    double sign = axis == 0 ? o.X : axis == 1 ? o.Y : o.Z;
                    int of = 2 * axis + (sign > 0 ? 1 : 0);
                    names[f] = of < faceNames.Count ? faceNames[of] : Em3dTessellation.BoxFaces[of];
                }
                return new C3dLowered(box, null, names, kind == KindBox ? KindBox : kind);
            }
            case Em3dBox b:
                return Transform(BoxPolyhedron(b, faceNames), [], t, KindPolyhedron);
            case Em3dExtrudedPolygon e when IsZOnly(t):
            {
                IReadOnlyList<Point2> Ring(IReadOnlyList<Point2> r)
                    => [.. r.Select(q => new Point2(t.M00 * q.X + t.M01 * q.Y + t.Tx, t.M10 * q.X + t.M11 * q.Y + t.Ty))];
                double z0 = t.M22 * e.ZBottom + t.Tz, z1 = t.M22 * e.ZTop + t.Tz;
                var names = faceNames.ToList();
                if (t.M22 < 0 && names.Count >= 2) (names[0], names[1]) = (names[1], names[0]);
                return new C3dLowered(new Em3dExtrudedPolygon(Ring(e.Outline), [.. e.Holes.Select(Ring)], Math.Min(z0, z1), Math.Max(z0, z1)),
                                      null, names, KindExtrusion);
            }
            case Em3dExtrudedPolygon e:
                return Transform(Prism(C3dPlane.XY, e.Outline, e.Holes, e.ZBottom, e.ZTop, default,
                                       [.. faceNames.Skip(2)]) is var p && faceNames.Count >= 2
                                     ? Renamed(p, faceNames[0], faceNames[1]) : p,
                                 [], t, KindPolyhedron);
            case Em3dCylinder c:
                return new C3dLowered(new Em3dCylinder(Apply(t, c.AxisStart), Apply(t, c.AxisEnd), c.Radius), null, faceNames, KindCylinder);
            case Em3dPolyhedron ph:
            {
                var moved = new Em3dPolyhedron([.. ph.Vertices.Select(v => Apply(t, v))], ph.Faces);
                if (Det(t) < 0) moved = Reversed(moved);
                return new C3dLowered(moved, null, [.. moved.Faces.Select(f => f.Name)], KindPolyhedron);
            }
            case Em3dSweep s:
                return new C3dLowered(new Em3dSweep([.. s.Path.Select(q => Apply(t, q))], s.Section, s.Diameter,
                                                    [.. s.Rings.Select(r => (IReadOnlyList<Point3>)[.. r.Select(q => Apply(t, q))])]),
                                      null, faceNames, "sweep");
            case Em3dSphere sp:
                return new C3dLowered(new Em3dSphere(Apply(t, sp.Center), sp.Radius), null, faceNames, KindSphere);
            case Em3dEllipsoid el:
            {
                // brief-sar-3 R-sar3-1 — a rigid placement turns the axes and moves the centre; a mirror leaves the shape as it was.
                var o = Apply(t, new Point3(0, 0, 0));
                Point3 Turn(Point3 v) => Apply(t, v) is var q ? new Point3(q.X - o.X, q.Y - o.Y, q.Z - o.Z) : v;
                return new C3dLowered(el with { Center = Apply(t, el.Center), U = Turn(el.U), V = Turn(el.V) }, null, faceNames, KindEllipsoid);
            }
            case Em3dTruncatedSphere ts when IsZOnly(t):
            {
                double z0 = t.M22 * ts.ZMin + t.Tz, z1 = t.M22 * ts.ZMax + t.Tz;
                return new C3dLowered(new Em3dTruncatedSphere(Apply(t, ts.Center), ts.Radius, Math.Min(z0, z1), Math.Max(z0, z1)),
                                      null, faceNames, "truncated-sphere");
            }
            default:
            {
                // A flattened ball tipped over has no primitive of its own: its tessellation is its geometry.
                var mesh = Em3dTessellation.Of(new Em3dSolid("", "", Em3dRole.Conductor, prim, 0));
                var poly = new Em3dPolyhedron(mesh.Vertices,
                    [.. mesh.Triangles.Select(tr => new Em3dFace([tr.A, tr.B, tr.C], [], "surface"))]);
                return Transform(poly, [], t, KindPolyhedron);
            }
        }
    }

    private static Em3dPolyhedron Renamed(Em3dPolyhedron p, string bottom, string top)
        => p with { Faces = [.. p.Faces.Select(f => f.Name == "bottom" ? f with { Name = bottom } : f.Name == "top" ? f with { Name = top } : f)] };

    /// <summary>A box as a polyhedron: eight corners, six outward faces named in <see cref="Em3dTessellation.BoxFaces"/> order.</summary>
    public static Em3dPolyhedron BoxPolyhedron(Em3dBox b, IReadOnlyList<string>? names = null)
    {
        var v = new Point3[8];
        for (int k = 0; k < 8; k++)
            v[k] = new Point3((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z);
        string N(int i) => names is { } n && i < n.Count ? n[i] : Em3dTessellation.BoxFaces[i];
        return new Em3dPolyhedron(v,
        [
            new([0, 4, 6, 2], [], N(0)), new([1, 3, 7, 5], [], N(1)),
            new([0, 1, 5, 4], [], N(2)), new([2, 6, 7, 3], [], N(3)),
            new([0, 2, 3, 1], [], N(4)), new([4, 5, 7, 6], [], N(5)),
        ]);
    }

    /// <summary>A sheet carried by <paramref name="t"/>: still flat when <paramref name="t"/> keeps z along z,
    /// otherwise in a frame.</summary>
    public static C3dSheetGeometry TransformSheet(C3dSheetGeometry s, C3dTransform t)
    {
        if (s.Frame is null && IsZOnly(t))
        {
            IReadOnlyList<Point2> Ring(IReadOnlyList<Point2> r)
                => [.. r.Select(q => new Point2(t.M00 * q.X + t.M01 * q.Y + t.Tx, t.M10 * q.X + t.M11 * q.Y + t.Ty))];
            return new C3dSheetGeometry(Ring(s.Outline), [.. s.Holes.Select(Ring)], t.M22 * s.Z + t.Tz, null);
        }
        var f = s.Frame ?? new Em3dPlaneFrame(new Point3(0, 0, 0), new Point3(1, 0, 0), new Point3(0, 1, 0));
        var frame = new Em3dPlaneFrame(Apply(t, f.World(0, 0, s.Z)), Rotate(t, f.U), Rotate(t, f.V));
        return new C3dSheetGeometry(s.Outline, s.Holes, 0, frame);
    }

    /// <summary>An <see cref="Em3dSheet"/>'s geometry, to carry it with <see cref="TransformSheet"/>.</summary>
    public static C3dSheetGeometry Geometry(Em3dSheet s) => new(s.Outline, s.Holes, s.Z, s.Frame);
}
