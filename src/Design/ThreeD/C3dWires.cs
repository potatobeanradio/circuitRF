// brief-em3d-50 — a .c3d's drawn bond wires: where each end lands, what the wire resolves to, and the arithmetic the
// Wire tool and the editor's edits share with elaboration.
//
// THE PAD LOOKUP (R-em3d50-1b). An end's pad is the elaborated conductor whose TOP SURFACE contains the end's plan
// point AT THE END'S z, within 1 DBU of the document — a conductor inside an instance included, which is the whole
// point: a die pad is in `U1`, a package lead in the parent. Where tops stack there, the highest wins (series 1's
// rule: a bond lands on exposed metal). A top surface is a box's zmax, an extrusion's top, a vertical cylinder's top
// disc, a polyhedron's upward faces and a flat sheet. No pad is a refusal naming the wire and the end — never a foot
// in mid-air — and it is exactly what elaboration says when the die a wire was bonded to has moved: the wire keeps
// its points, because re-routing it silently would change its inductance.
//
// RE-SEATING is the vertical question only: the highest top surface under an end's PLAN point, and the end moved
// onto it in z. A die moved sideways leaves nothing under the end, and re-seating says so.
//
// RESOLUTION is Em3dWires.Resolve, the SAME function a .wBond's wires go through (R-em3d50-1a), so a drawn wire on
// the .wBond's points is the .wBond's wire (gate 5).
//
// THE LOOP HEIGHT A USER TYPES is the ASSEMBLY one (em-3d.md §6.6): the bottom of the lower foot — the lower pad's
// top — to the top of the wire at its apex. The axis is shaped by LoopShape (wBond's own arithmetic) to an AXIS loop
// height, and the assembly height of the resolved solid is then measured; the axis target is corrected by the
// difference until the two agree to within half a DBU. The first guess is the typed height less half the section's
// height, which is the whole difference for a flat apex; the mitre at the apex and a ball's neck are what the
// correction takes up.

using System.Globalization;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using CircuitRF.WBond;
using Point3 = CircuitRF.Engine.Em3d.Point3;
using WPoint3 = CircuitRF.WBond.Point3;

namespace CircuitRF.Design.ThreeD;

/// <summary>One conductor's top surface a wire end can land on, world metres.</summary>
/// <param name="Name">The elaborated conductor's name (<c>U1/pad3</c> inside an instance).</param>
public sealed record C3dWirePad(string Name, PlanarPolygon Poly, double TopM)
{
    public Em3dWirePad ForResolution => new(Name, Poly, TopM);
}

/// <summary>What elaboration made of one drawn wire: the resolution, the process values, and the pads it landed on — or
/// the refusal that stopped it.</summary>
public sealed record C3dWireResult(
    Em3dWireResolution? Resolution, WireBondProcessValues? StartProcess, WireBondProcessValues? EndProcess,
    C3dWirePad? StartPad, C3dWirePad? EndPad, string? Refusal)
{
    public bool Ok => Refusal is null && Resolution is { Ok: true };

    /// <summary>brief-em3d-78 R-em3d78-3 — the wire's axis in world metres, as resolution was handed it (before a foot is added or a
    /// wedge end moved onto its pad): the centreline wBond's inductance reads, which is what an RF current share is taken from.</summary>
    public IReadOnlyList<Point3> Axis { get; init; } = [];
}

public static class C3dWires
{
    /// <summary>wBond's own default diameter, 1 mil — what an omitted <see cref="C3dWire.DiameterUm"/> means.</summary>
    public static readonly double DefaultDiameterUm = new Wire().DiameterNm / 1000.0;

    /// <summary>The points the Wire tool's seed arch has (LoopShape's default).</summary>
    public const int SeedPoints = 7;

    /// <summary>The wire's metal: its own, or wBond's default when it states none.</summary>
    public static string MaterialOf(C3dWire w) => w.Material is { Length: > 0 } m ? m : WireMaterials.Default.Name;

    /// <summary>The diameter, nm.</summary>
    public static long DiameterNm(C3dWire w) => (long)Math.Round((w.DiameterUm ?? DefaultDiameterUm) * 1000, MidpointRounding.AwayFromZero);

    public static WireCrossSection SectionOf(C3dWire w) => w.Section ?? WireCrossSection.Hexagon;

    private static long? FootNm(C3dWireEnd e) => e.FootLengthUm is { } f ? (long)Math.Round(f * 1000, MidpointRounding.AwayFromZero) : null;

    // ── pads ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every conductor top surface of <paramref name="solids"/> and <paramref name="sheets"/> — world metres — whose
    /// name starts with <paramref name="prefix"/> (a wire inside an instance lands on its own document's conductors),
    /// leaving out <paramref name="exclude"/> (the wires themselves: a wire does not bond onto a wire).
    /// </summary>
    public static List<C3dWirePad> Pads(IEnumerable<Em3dSolid> solids, IEnumerable<Em3dSheet> sheets, string prefix = "",
                                        ISet<string>? exclude = null)
    {
        var pads = new List<C3dWirePad>();
        bool Take(string name) => name.StartsWith(prefix, StringComparison.Ordinal) && exclude?.Contains(name) != true;
        foreach (var s in solids)
        {
            if (s.Role != Em3dRole.Conductor || !Take(s.Name)) continue;
            switch (s.Primitive)
            {
                case Em3dBox b:
                    pads.Add(new(s.Name, new PlanarPolygon([new(b.Min.X, b.Min.Y), new(b.Max.X, b.Min.Y), new(b.Max.X, b.Max.Y),
                                                            new(b.Min.X, b.Max.Y)]), b.Max.Z));
                    break;
                case Em3dExtrudedPolygon e:
                    pads.Add(new(s.Name, Poly(e.Outline, e.Holes), Math.Max(e.ZTop, e.ZBottom)));
                    break;
                case Em3dCylinder c when c.AxisStart.X == c.AxisEnd.X && c.AxisStart.Y == c.AxisEnd.Y:
                {
                    const int sides = 72;
                    var ring = new List<EmPoint>(sides);
                    for (int k = 0; k < sides; k++)
                    {
                        double a = 2 * Math.PI * k / sides;
                        ring.Add(new(c.AxisStart.X + c.Radius * Math.Cos(a), c.AxisStart.Y + c.Radius * Math.Sin(a)));
                    }
                    pads.Add(new(s.Name, new PlanarPolygon(ring), Math.Max(c.AxisStart.Z, c.AxisEnd.Z)));
                    break;
                }
                // brief-em3d-66 R-em3d66-5d — a kernel solid (a boolean's result) is a pad wherever it has a flat face looking
                // up: a wire bonded to the lid before a bore was subtracted from it still lands on the lid afterwards.
                case Em3dShapeSolid k:
                    foreach (var (outline, holes, z) in UpwardFaces(k))
                        pads.Add(new(s.Name, new PlanarPolygon(outline, holes), z));
                    break;
                case Em3dPolyhedron p:
                    foreach (var f in p.Faces)
                        if (UpwardZ(p.Vertices, f.Outer) is { } z)
                            pads.Add(new(s.Name, new PlanarPolygon([.. f.Outer.Select(i => new EmPoint(p.Vertices[i].X, p.Vertices[i].Y))],
                                                                   [.. f.Holes.Select(h => (IReadOnlyList<EmPoint>)[.. h.Select(i => new EmPoint(p.Vertices[i].X, p.Vertices[i].Y))])]),
                                         z));
                    break;
            }
        }
        foreach (var sh in sheets)
            if (sh.Frame is null && Take(sh.Name)) pads.Add(new(sh.Name, Poly(sh.Outline, sh.Holes), sh.Z));
        return pads;
    }

    /// <summary>
    /// brief-em3d-66 — every flat face of a kernel solid that looks straight up, as outline and holes (world metres) at its
    /// height: the face's boundary loops, read off its display triangles — an edge used by one triangle of the face is on
    /// its boundary, and the triangles' own winding makes the outer loop counter-clockwise and each hole clockwise.
    /// </summary>
    internal static IEnumerable<(IReadOnlyList<EmPoint> Outline, IReadOnlyList<IReadOnlyList<EmPoint>> Holes, double Z)> UpwardFaces(Em3dShapeSolid k)
    {
        var v = k.Display.Vertices;
        var tris = k.Display.Triangles;
        for (int f = 0; f < k.Faces.Count; f++)
        {
            var box = k.Faces[f].Box;
            double size = Math.Max(box.X1 - box.X0, box.Y1 - box.Y0);
            if (!(size > 0) || box.Z1 - box.Z0 > 1e-9 * Math.Max(size, 1e-9)) continue;
            // Welded by position: a vertex shared by two triangles is one point whatever its index.
            var ids = new Dictionary<(double, double), int>();
            int Id(Point3 p) => ids.TryGetValue((p.X, p.Y), out int i) ? i : ids[(p.X, p.Y)] = ids.Count;
            var at = new List<EmPoint>();
            var edges = new Dictionary<(int, int), int>();
            bool up = true, any = false;
            foreach (var t in tris)
            {
                if (t.Face != f) continue;
                any = true;
                Point3 a = v[t.A], b = v[t.B], c = v[t.C];
                double nz = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (nz < 0) { up = false; break; }
                if (nz == 0) continue;                       // a sliver: no area, no boundary
                int ia = Id(a), ib = Id(b), ic = Id(c);
                while (at.Count < ids.Count) at.Add(default);
                at[ia] = new(a.X, a.Y); at[ib] = new(b.X, b.Y); at[ic] = new(c.X, c.Y);
                foreach (var (p, q) in new[] { (ia, ib), (ib, ic), (ic, ia) })
                {
                    if (edges.Remove((q, p))) continue;      // the other triangle's half: interior
                    edges[(p, q)] = 1;
                }
            }
            if (!any || !up || edges.Count < 3) continue;
            var next = new Dictionary<int, int>();
            foreach (var (p, q) in edges.Keys) next[p] = q;
            var loops = new List<List<EmPoint>>();
            var seen = new HashSet<int>();
            foreach (int start in next.Keys)
            {
                if (!seen.Add(start)) continue;
                var loop = new List<EmPoint> { at[start] };
                int cur = next[start];
                while (cur != start && seen.Add(cur) && next.ContainsKey(cur))
                {
                    loop.Add(at[cur]);
                    cur = next[cur];
                }
                if (loop.Count >= 3) loops.Add(loop);
            }
            static double Area(List<EmPoint> l)
            {
                double s = 0;
                for (int i = 0, j = l.Count - 1; i < l.Count; j = i++) s += l[j].X * l[i].Y - l[i].X * l[j].Y;
                return s / 2;
            }
            var outers = loops.Where(l => Area(l) > 0).ToList();
            var holes = loops.Where(l => Area(l) < 0).ToList();
            foreach (var o in outers)
            {
                var poly = new PlanarPolygon(o);
                var mine = holes.Where(h => poly.Contains(h[0].X, h[0].Y)).Select(h => (IReadOnlyList<EmPoint>)h).ToList();
                yield return (o, mine, box.Z1);
            }
        }
    }

    private static PlanarPolygon Poly(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
        => new([.. outline.Select(q => new EmPoint(q.X, q.Y))], [.. holes.Select(h => (IReadOnlyList<EmPoint>)[.. h.Select(q => new EmPoint(q.X, q.Y))])]);

    /// <summary>The face's z when it is flat and faces +z; null otherwise.</summary>
    private static double? UpwardZ(IReadOnlyList<Point3> v, IReadOnlyList<int> loop)
    {
        if (loop.Count < 3) return null;
        double nx = 0, ny = 0, nz = 0;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            var a = v[loop[j]]; var b = v[loop[i]];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }
        double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(len > 0) || nz / len < 1 - 1e-9) return null;
        double z0 = v[loop[0]].Z;
        foreach (int i in loop) if (Math.Abs(v[i].Z - z0) > 1e-12 * Math.Max(1, Math.Abs(z0)) + 1e-15) return null;
        return z0;
    }

    /// <summary>Whether (x, y) is inside <paramref name="poly"/>, or within <paramref name="tol"/> of its boundary.</summary>
    public static bool Covers(PlanarPolygon poly, double x, double y, double tol)
        => poly.Contains(x, y) || NearOutline(poly, x, y, tol);

    /// <summary>Whether (x, y) is within <paramref name="tol"/> of any ring of <paramref name="poly"/>.</summary>
    private static bool NearOutline(PlanarPolygon poly, double x, double y, double tol)
    {
        bool Near(IReadOnlyList<EmPoint> ring)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double ax = ring[j].X, ay = ring[j].Y, dx = ring[i].X - ax, dy = ring[i].Y - ay;
                double l2 = dx * dx + dy * dy;
                double t = l2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1) : 0;
                double ex = ax + t * dx - x, ey = ay + t * dy - y;
                if (ex * ex + ey * ey <= tol * tol) return true;
            }
            return false;
        }
        return Near(poly.Outer) || poly.HoleRings.Any(Near);
    }

    /// <summary>R-em3d50-1b — the pad whose top surface holds <paramref name="p"/>: containing its plan point and at its
    /// z, each within <paramref name="tolM"/>; the highest where several do. Null when none.</summary>
    public static C3dWirePad? PadAt(IEnumerable<C3dWirePad> pads, Point3 p, double tolM)
        => pads.Where(q => Math.Abs(q.TopM - p.Z) <= tolM && Covers(q.Poly, p.X, p.Y, tolM))
               .OrderByDescending(q => q.TopM).FirstOrDefault();

    /// <summary>The first pad top a ray meets (a point on an upward-facing conductor face under the cursor), with where it
    /// meets it; null when it meets none.</summary>
    public static (C3dWirePad Pad, Point3 At)? PadHit(IEnumerable<C3dWirePad> pads, Point3 origin, Point3 direction)
    {
        if (!(direction.Z < 0)) return null;                    // a top is only seen from above
        (C3dWirePad, Point3)? best = null;
        double bestT = double.PositiveInfinity;
        foreach (var pad in pads)
        {
            double t = (pad.TopM - origin.Z) / direction.Z;
            if (!(t > 0) || t >= bestT) continue;
            double x = origin.X + t * direction.X, y = origin.Y + t * direction.Y;
            if (!pad.Poly.Contains(x, y)) continue;
            bestT = t;
            best = (pad, new Point3(x, y, pad.TopM));
        }
        return best;
    }

    /// <summary>
    /// 3D editor bugs round 2 — where a wire end lands when ANY face of an object was clicked: <paramref name="pads"/> are
    /// that one object's tops. A bond sits on a top, so a side or a bottom face lands the end on the top above the point
    /// clicked: the highest top containing (<paramref name="x"/>, <paramref name="y"/>) further than <paramref name="tolM"/>
    /// from its outline, or else the nearest point of the
    /// nearest top's outline — a side face's point lies ON the outline — moved <paramref name="insetM"/> inside it where
    /// the top is wide enough, so the foot is not left hanging over the edge. Null when there are no tops.
    /// </summary>
    public static (C3dWirePad Pad, double X, double Y)? LandOn(IReadOnlyList<C3dWirePad> pads, double x, double y, double insetM, double tolM)
    {
        if (pads.Count == 0) return null;
        // Inside, and clear of the outline: a point ON it (a side face's) is inside by the polygon's own test.
        if (pads.Where(q => q.Poly.Contains(x, y) && !NearOutline(q.Poly, x, y, tolM))
                .OrderByDescending(q => q.TopM).FirstOrDefault() is { } inside)
            return (inside, x, y);
        C3dWirePad? best = null;
        double bx = x, by = y, bd = double.PositiveInfinity, nx = 0, ny = 0;
        foreach (var pad in pads)
            foreach (var ring in pad.Poly.HoleRings.Prepend(pad.Poly.Outer))
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                {
                    double ax = ring[j].X, ay = ring[j].Y, dx = ring[i].X - ax, dy = ring[i].Y - ay;
                    double l2 = dx * dx + dy * dy;
                    if (!(l2 > 0)) continue;
                    double t = Math.Clamp(((x - ax) * dx + (y - ay) * dy) / l2, 0, 1);
                    double px = ax + t * dx, py = ay + t * dy, d = (px - x) * (px - x) + (py - y) * (py - y);
                    if (d >= bd) continue;
                    double l = Math.Sqrt(l2);
                    (best, bx, by, bd, nx, ny) = (pad, px, py, d, -dy / l, dx / l);
                }
        if (best is null) return null;
        for (double inset = insetM; inset > 0 && inset >= insetM / 8; inset /= 2)
            foreach (int side in (ReadOnlySpan<int>)[1, -1])
            {
                double qx = bx + side * nx * inset, qy = by + side * ny * inset;
                if (best.Poly.Contains(qx, qy)) return (best, qx, qy);
            }
        return (best, bx, by);
    }

    /// <summary>Re-seating's question: the highest top surface under the plan point, at any height.</summary>
    public static C3dWirePad? PadUnder(IEnumerable<C3dWirePad> pads, double x, double y, double tolM)
        => pads.Where(q => Covers(q.Poly, x, y, tolM)).OrderByDescending(q => q.TopM).FirstOrDefault();

    // ── one wire ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="w"/> resolved under <paramref name="world"/> (its document's metres to the world) against
    /// <paramref name="pads"/>. <paramref name="name"/> is its elaborated name; <paramref name="dbuPerMicron"/> is its
    /// document's and <paramref name="tolM"/> one DBU of the TOP document, in metres.
    /// </summary>
    public static C3dWireResult Resolve(C3dWire w, string name, C3dTransform world, int dbuPerMicron,
                                        IReadOnlyList<C3dWirePad> pads, double tolM, WireBondWorkspace workspace, string unit)
    {
        if (!w.Placement.IsDefault)
            return new(null, null, null, null, null, C3dDiagnostics.WirePlacement(name).Render());
        if (w.Points.Count < 2 || w.Points.Distinct().Count() < 2)
            return new(null, null, null, null, null, $"The wire '{name}' needs at least two distinct points.");
        long dNm = DiameterNm(w);
        if (dNm <= 0) return new(null, null, null, null, null, $"The wire '{name}' has a diameter that is not positive.");

        var axis = w.Points.Select(q => C3dLowering.Apply(world, new Point3(C3dLowering.Metres(q.X, dbuPerMicron),
                                                                            C3dLowering.Metres(q.Y, dbuPerMicron),
                                                                            C3dLowering.Metres(q.Z, dbuPerMicron)))).ToList();
        var startPad = PadAt(pads, axis[0], tolM);
        var endPad   = PadAt(pads, axis[^1], tolM);
        foreach (var (pad, which, q) in new[] { (startPad, "start", axis[0]), (endPad, "end", axis[^1]) })
            if (pad is null)
                return new(null, null, null, startPad, endPad,
                           NoPad(name, which, q, pads, tolM, unit) + BoundEnd(w, which == "start" ? 0 : w.Points.Count - 1));

        var startProcess = WireBondProcess.Resolve(dNm, FootNm(w.Start), workspace);
        var endProcess   = WireBondProcess.Resolve(dNm, FootNm(w.End), workspace);
        var section = SectionOf(w) == WireCrossSection.Round ? Em3dSection.Circle : Em3dSection.Hexagon;
        long zMin = w.Points.Min(q => q.Z), zMax = w.Points.Max(q => q.Z);
        // The report carries one set of process values: the wedge end's, where there is one (the start's when both).
        var reported = w.Start.Style == BondStyle.Wedge || w.End.Style != BondStyle.Wedge ? startProcess : endProcess;
        var resolution = Em3dWires.Resolve(
            new Em3dWireInput(name, axis, section, dNm * 1e-9, w.Start.Style, w.End.Style, startProcess.FootLength.Nm,
                              endProcess.FootLength.Nm, reported,
                              C3dLowering.Metres(zMax, dbuPerMicron) - C3dLowering.Metres(zMin, dbuPerMicron)),
            startPad!.ForResolution, endPad!.ForResolution);
        return new(resolution, startProcess, endProcess, startPad, endPad, resolution.Refusal) { Axis = axis };
    }

    /// <summary>
    /// The warning a wire end gets when it lands on metal that a placed layout's cover still hides: which wire, which end,
    /// which pad, which covers, and the two ways out. A warning, not a refusal: the geometry is buildable and the run says
    /// what it is solving.
    /// </summary>
    public static string CoveredEnd(string name, string which, string pad, IReadOnlyList<string> covers)
    {
        string list = string.Join(", ", covers.Select(c => $"'{c}'"));
        return $"Wire '{name}': its {which} lands on '{pad}' under {list}, which {(covers.Count == 1 ? "covers" : "cover")} the pad " +
               "there. A bond reaches bare metal only, and this run connects the wire through the dielectric. Draw an opening on " +
               "the cover's Opened by layer over the pad, or move the end into the opening.";
    }

    /// <summary>R-em3d50-3c — the sentence an end that is on no pad gets: which wire, which end, and what is under it.</summary>
    public static string NoPad(string name, string which, Point3 q, IReadOnlyList<C3dWirePad> pads, double tolM, string unit)
    {
        string where = $"({Length(q.X, unit)}, {Length(q.Y, unit)}, {Length(q.Z, unit)}) {unit}";
        string under = PadUnder(pads, q.X, q.Y, tolM) is { } p
            ? $" '{p.Name}' is under it with its top at {Length(p.TopM, unit)} {unit}: Re-seat Wire Ends moves the end onto it."
            : " Nothing conductive is under it: move the end onto a pad.";
        return $"{name}'s {which} is no longer on a pad: no conductor's top surface is at {where}.{under} A wire is not " +
               "re-routed when what it was bonded to moves, because that would change its inductance.";
    }

    /// <summary>brief-em3d-133 R-em3d133-2 — an end that misses its pad, when it is placed by expressions: the text of each
    /// bound component, so the refusal says what put it there (<c> Its z is t_sub + t_die.</c>); empty when none is bound.</summary>
    public static string BoundEnd(C3dWire w, int k)
    {
        if (k < 0 || C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points)) is not { } spec) return "";
        var parts = new List<string>();
        for (int c = 0; c < 3; c++)
            if (C3dBindings.GetExpr(w, spec.ElementAt(k), c) is { } e) parts.Add($"{(c switch { 0 => "x", 1 => "y", _ => "z" })} is {e.Expr}");
        return parts.Count == 0 ? "" : $" Its {string.Join(", its ", parts)}.";
    }

    private static string Length(double m, string unit)
    {
        double per = unit switch { "mil" => 25.4e-6, "mm" => 1e-3, "nm" => 1e-9, "in" => 25.4e-3, _ => 1e-6 };
        return (m / per).ToString("0.####", CultureInfo.InvariantCulture);
    }

    // ── arrays (3D editor round 4) ─────────────────────────────────────────────────────────────

    /// <summary>The name of element <paramref name="k"/> of <paramref name="w"/>: its own name when it is one wire,
    /// <c>w1[k]</c> in an array of more than one.</summary>
    public static string ElementName(C3dWire w, long k) => w.Array is { Count: > 1 } ? $"{w.Name}[{k}]" : w.Name;

    /// <summary>
    /// Every wire <paramref name="w"/> stands for: itself when it is no array, else each element — a copy with its points
    /// moved by k × pitch, named <see cref="ElementName"/>, and no array of its own. The drawn wire is element 0. A count
    /// below 1 is one wire (validation refuses it).
    /// </summary>
    public static IEnumerable<(string Name, C3dWire Wire)> Elements(C3dWire w)
    {
        if (w.Array is not { Count: > 1 } a) { yield return (w.Name, w); yield break; }
        string text = C3dPersistence.SerializeObject(w);
        for (long k = 0; k < a.Count; k++)
        {
            var copy = (C3dWire)C3dPersistence.DeserializeObject(text);
            copy.Array = null;
            copy.Name = ElementName(w, k);
            var d = a.Offset(k);
            copy.Points = [.. w.Points.Select(q => new C3dPoint3(q.X + d.X, q.Y + d.Y, q.Z + d.Z))];
            yield return (copy.Name, copy);
        }
    }

    /// <summary>Each element's name and axis, without copying the wire — what an overlay drawn every frame reads.</summary>
    public static IEnumerable<(string Name, IReadOnlyList<C3dPoint3> Points)> ElementAxes(C3dWire w)
    {
        if (w.Array is not { Count: > 1 } a) { yield return (w.Name, w.Points); yield break; }
        for (long k = 0; k < a.Count; k++)
        {
            var d = a.Offset(k);
            yield return (ElementName(w, k), [.. w.Points.Select(q => new C3dPoint3(q.X + d.X, q.Y + d.Y, q.Z + d.Z))]);
        }
    }

    /// <summary>The elaborated names <paramref name="w"/> stands for (see <see cref="ElementName"/>).</summary>
    public static IEnumerable<string> ElementNames(C3dWire w)
    {
        long n = w.Array is { Count: > 1 } a ? a.Count : 1;
        for (long k = 0; k < n; k++) yield return ElementName(w, k);
    }

    // ── re-seating and editing ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// R-em3d50-3c — <paramref name="w"/> with each end moved in z onto the highest top surface under its plan point,
    /// where there is one. <paramref name="unseated"/> names the ends with nothing under them (left as they were).
    /// The pads are the document's own frame (a wire's points are its document's world points).
    /// </summary>
    public static C3dWire Reseat(C3dWire w, IReadOnlyList<C3dWirePad> pads, int dbuPerMicron, out List<string> unseated)
    {
        var copy = (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(w));
        unseated = [];
        double per = C3dLowering.Metres(1, dbuPerMicron);
        var spec = C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points))!;
        for (int e = 0; e < 2 && copy.Points.Count > 0; e++)
        {
            int i = e == 0 ? 0 : copy.Points.Count - 1;
            var q = copy.Points[i];
            // brief-em3d-133 R-em3d133-2 — a bound z that already lands on a top is never re-seated: not even onto a higher
            // top under it, which would rewrite the name it holds.
            if (C3dBindings.GetExpr(copy, spec.ElementAt(i), 2) is not null &&
                PadAt(pads, new Point3(C3dLowering.Metres(q.X, dbuPerMicron), C3dLowering.Metres(q.Y, dbuPerMicron), C3dLowering.Metres(q.Z, dbuPerMicron)), per) is not null)
                continue;
            if (PadUnder(pads, C3dLowering.Metres(q.X, dbuPerMicron), C3dLowering.Metres(q.Y, dbuPerMicron), per) is { } pad)
                copy.Points[i] = q with { Z = (long)Math.Round(pad.TopM / per, MidpointRounding.AwayFromZero) };
            else unseated.Add(e == 0 ? "start" : "end");
        }
        return copy;
    }

    /// <summary>
    /// brief-em3d-133 R-em3d133-3 — an edit that moved <paramref name="was"/>'s points to <paramref name="moved"/>'s (a
    /// vertex Move, a span) keeps every bound component's expression and writes the step into it (overview D2): each bound
    /// component whose number changed gets <see cref="C3dPointExpressions.OffsetExpression"/> of the change, in
    /// <paramref name="unit"/>, so the variable it names is never rewritten. An END's z is not offset: the seat decides it
    /// (R-em3d133-2), so a bound one keeps its number and its text here and the seat that follows writes nothing when the
    /// expression still lands. Nothing is done when the edit changed the number of points.
    /// </summary>
    public static void OffsetBoundPoints(C3dWire was, C3dWire moved, LayoutUnit unit, int dbuPerMicron)
    {
        if (was.Points.Count != moved.Points.Count || moved.Exprs is null) return;
        var spec = C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points))!;
        int last = moved.Points.Count - 1;
        for (int k = 0; k <= last; k++)
            for (int c = 0; c < 3; c++)
            {
                var element = spec.ElementAt(k);
                if (C3dBindings.GetExpr(moved, element, c) is not { } e) continue;
                long from = (long)C3dBindings.GetNumber(was, element, c)!.Value, to = (long)C3dBindings.GetNumber(moved, element, c)!.Value;
                if (c == 2 && (k == 0 || k == last)) { C3dBindings.SetNumber(moved, element, c, from); continue; }
                if (to != from) C3dBindings.SetExpr(moved, element, c, C3dPointExpressions.OffsetExpression(e, to - from, unit, dbuPerMicron));
            }
    }

    /// <summary>
    /// brief-em3d-50 — a wire's placement applied to its points, and the placement cleared: what every operation that
    /// composes into a placement (move, rotate, mirror, duplicate, array, flatten) ends with for a wire, whose points are
    /// where it is. <paramref name="exact"/> is false when a point was not a whole DBU and was rounded. Anything that is not
    /// a wire is left alone.
    /// <para>brief-em3d-132 R-em3d132-5 — a bound component keeps its expression: under a quarter-turn or a mirror the
    /// component landing on each axis is a signed copy of another plus the translation, written exactly through
    /// <see cref="C3dPointExpressions.SwapExpression"/> (D4), the offset term in <paramref name="unit"/>. Any other turn of a
    /// wire with a bound point (or a bound array pitch) is refused, naming the first such field, and nothing is changed —
    /// the refusal is returned; null on success.</para>
    /// </summary>
    public static string? BakePlacement(C3dObject o, LayoutUnit unit, int dbuPerMicron, out bool exact)
    {
        exact = true;
        if (o is not C3dWire w || w.Placement.IsDefault) return null;
        var t = w.Placement.ToTransform();
        int[]? m = t.IntegerMatrix();
        foreach (var bound in C3dBindings.BoundOf(w.Name, w).Where(f => f.Spec.Element is not null || f.Path.StartsWith("Array.Pitch", StringComparison.Ordinal)))
        {
            string field = $"'{w.Name}' {C3dBindings.Label(bound.Spec, bound.Component, bound.Path)} holds {bound.Expr.Expr}";
            if (m is null)
                return $"{field}: the turn is not a quarter turn, so no expression can follow it exactly. Replace it with a number to turn the wire.";
            try { Core.Expressions.Parser.Parse(bound.Expr.Expr); }
            catch (Core.Expressions.ExpressionException x) { return $"{field}, which does not parse ({x.Message}): it cannot be moved. Correct it first."; }
        }
        bool whole = true;
        long[] offset = [R(t.Tx), R(t.Ty), R(t.Tz)];
        if (Math.Abs(t.Tx - offset[0]) > 1e-6 || Math.Abs(t.Ty - offset[1]) > 1e-6 || Math.Abs(t.Tz - offset[2]) > 1e-6) whole = false;
        var spec = C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points))!;
        for (int i = 0; i < w.Points.Count; i++)
        {
            var (x, y, z) = t.Apply(w.Points[i]);
            long rx = R(x), ry = R(y), rz = R(z);
            if (Math.Abs(x - rx) > 1e-6 || Math.Abs(y - ry) > 1e-6 || Math.Abs(z - rz) > 1e-6) whole = false;
            w.Points[i] = new C3dPoint3(rx, ry, rz);
            if (m is not null) Swap(w, spec.ElementAt(i), m, offset, unit, dbuPerMicron);
        }
        // 3D editor round 4 — a wire array's pitch turns with the wire (rotation and mirror, never the translation), so a
        // rotated row stays a row of the rotated wire.
        if (w.Array is { } wa)
        {
            var (px, py, pz) = (t with { Tx = 0, Ty = 0, Tz = 0 }).Apply(wa.Pitch);
            long rx = R(px), ry = R(py), rz = R(pz);
            if (Math.Abs(px - rx) > 1e-6 || Math.Abs(py - ry) > 1e-6 || Math.Abs(pz - rz) > 1e-6) whole = false;
            wa.Pitch = new C3dPoint3(rx, ry, rz);
            if (m is not null) Swap(wa, C3dBindings.SpecOf(typeof(C3dWireArray), nameof(C3dWireArray.Pitch))!, m, [0, 0, 0], unit, dbuPerMicron);
        }
        // A mirror reverses the handedness of nothing a wire has: the axis is the shape, so the points are all of it.
        w.Placement = new C3dPlacement();
        exact = whole;
        return null;
    }

    /// <summary>One three-component field's expressions under the signed permutation <paramref name="m"/> (row-major): the
    /// component landing on axis a is s·(the expression on axis b) + offset[a].</summary>
    private static void Swap(IC3dBindable owner, C3dFieldSpec spec, int[] m, long[] offset, LayoutUnit unit, int dbuPerMicron)
    {
        var was = new C3dExpr?[3];
        for (int b = 0; b < 3; b++) was[b] = C3dBindings.GetExpr(owner, spec, b);
        if (was.All(e => e is null)) return;
        for (int a = 0; a < 3; a++)
        {
            int b = Array.FindIndex(m, 3 * a, 3, v => v != 0) - 3 * a;
            C3dBindings.SetExpr(owner, spec, a, was[b] is { } e ? C3dPointExpressions.SwapExpression(e, m[3 * a + b], offset[a], unit, dbuPerMicron) : null);
        }
    }

    private static long R(double v) => (long)Math.Round(v, MidpointRounding.AwayFromZero);

    // ── loop height and span (3D editor round 4; held: brief-em3d-135) ──────────────────────────

    /// <summary>A wire's foot-to-foot distance in plan, DBU.</summary>
    public static long SpanDbu(C3dWire w)
    {
        if (w.Points.Count < 2) return 0;
        double dx = w.Points[^1].X - w.Points[0].X, dy = w.Points[^1].Y - w.Points[0].Y;
        return (long)Math.Round(Math.Sqrt(dx * dx + dy * dy), MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// <paramref name="w"/>'s points with its span made <paramref name="targetDbu"/>: the END foot moved along the run in
    /// plan, the start pinned, each interior point keeping its place along the run and its height above the chord — wBond's
    /// own <see cref="WireEdits.ScaleSpan"/>. Null when the feet are one above the other: there is no run to lengthen along.
    /// </summary>
    public static List<C3dPoint3>? WithSpan(C3dWire w, long targetDbu)
    {
        long now = SpanDbu(w);
        if (now <= 0) return null;
        var wire = ToWBond(w);
        if (now != targetDbu) WireEdits.ScaleSpan(wire, (double)targetDbu / now, moveOutputFoot: true);
        return FromWBond(wire);
    }

    /// <summary>What <see cref="FitLoopHeight"/> made of a loop height.</summary>
    public enum LoopFit
    {
        /// <summary>The points are the arch at that height (or already were).</summary>
        Done,
        /// <summary>The wire does not resolve, so there is nothing to measure.</summary>
        NoMeasure,
        /// <summary>Below what a straight wire between those feet already measures.</summary>
        BelowFeet,
        /// <summary>A straight wire: no rise above its feet to scale.</summary>
        Straight,
    }

    /// <summary>
    /// <paramref name="was"/>'s points with an ASSEMBLY loop height of <paramref name="targetDbu"/> (em-3d.md §6.6): every
    /// point's rise above the foot-to-foot chord scaled, every x and y kept (<see cref="WireEdits.SetLoopHeightPreservingPath"/>
    /// on the axis), the axis target corrected by what <paramref name="measure"/> (the resolved solid's assembly height, DBU)
    /// says until the two agree to half a DBU. <c>FootDrop</c> is the feet's difference in height, for a refusal.
    /// </summary>
    public static (LoopFit Outcome, List<C3dPoint3>? Points, long FootDrop) FitLoopHeight(C3dWire was, long targetDbu, Func<C3dWire, double?> measure)
    {
        long footDrop = was.Points.Count < 2 ? 0 : Math.Abs(was.Points[^1].Z - was.Points[0].Z);
        if (measure(was) is not { } now) return (LoopFit.NoMeasure, null, footDrop);
        if (Math.Abs(now - targetDbu) <= 0.5) return (LoopFit.Done, was.Points, footDrop);
        double axis = (was.Points.Max(p => p.Z) - was.Points.Min(p => p.Z)) + (targetDbu - now);
        List<C3dPoint3>? best = null;
        double bestMiss = double.PositiveInfinity;
        var candidate = (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(was));
        for (int iteration = 0; iteration < 12; iteration++)
        {
            long axisTarget = (long)Math.Round(axis, MidpointRounding.AwayFromZero);
            if (axisTarget <= footDrop) return (LoopFit.BelowFeet, null, footDrop);
            var wire = ToWBond(was);
            if (!WireEdits.SetLoopHeightPreservingPath(wire, axisTarget)) return (LoopFit.Straight, null, footDrop);
            candidate.Points = FromWBond(wire);
            if (measure(candidate) is not { } got) break;
            if (Math.Abs(got - targetDbu) < bestMiss) (best, bestMiss) = (candidate.Points, Math.Abs(got - targetDbu));
            if (bestMiss <= 0.5) break;
            double next = axis + (targetDbu - got);
            axis = Math.Abs(next - axis) < 0.5 ? axis + Math.Sign(targetDbu - got) : next;
        }
        return best is null ? (LoopFit.NoMeasure, null, footDrop) : (LoopFit.Done, best, footDrop);
    }

    /// <summary>
    /// brief-em3d-135 — <paramref name="w"/>'s assembly loop height, DBU, measured on its own feet: each end stands on a
    /// pad whose top is the end's z, which is what a seated end stands on, so no elaboration is needed to ask. Null when it
    /// does not resolve.
    /// </summary>
    public static double? AssemblyAtFeet(C3dWire w, int dbuPerMicron, WireBondWorkspace workspace)
    {
        if (w.Points.Count < 2) return null;
        double per = C3dLowering.Metres(1, dbuPerMicron);
        C3dWirePad At(string name, C3dPoint3 q)
        {
            double x = q.X * per, y = q.Y * per;
            const double half = 1.0;   // wide enough that no foot hangs over its edge
            return new(name, new PlanarPolygon([new(x - half, y - half), new(x + half, y - half), new(x + half, y + half), new(x - half, y + half)]), q.Z * per);
        }
        var copy = w.Placement.IsDefault ? w : (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(w));
        copy.Placement = new C3dPlacement();
        var r = Resolve(copy, w.Name, C3dTransform.Identity, dbuPerMicron, [At("start", w.Points[0]), At("end", w.Points[^1])], per, workspace, "um");
        return r.Resolution?.Report is { } report ? report.AssemblyLoopHeightM / per : null;
    }

    /// <summary>
    /// brief-em3d-135 — the first bound point component a held field of <paramref name="w"/> would overwrite, as a refusal;
    /// null when there is none. A held span moves every point but the start in plan (x and y); a held loop height rewrites
    /// every interior point's z. An end's z stays the pad's, so it may be bound under either.
    /// </summary>
    public static string? HeldConflict(C3dWire w, string name)
    {
        if (C3dBindings.SpecOf(typeof(C3dWire), nameof(C3dWire.Points)) is not { } spec) return null;
        int last = w.Points.Count - 1;
        foreach (var (held, rewrites, what) in new[]
                 {
                     (nameof(C3dWire.Span), (Func<int, int, bool>)((k, c) => k > 0 && c < 2), "moves every point but the start in plan"),
                     (nameof(C3dWire.LoopHeight), (Func<int, int, bool>)((k, c) => k > 0 && k < last && c == 2), "rewrites the height of every point between the ends"),
                 })
        {
            if (HeldText(w, held) is not { } text) continue;
            for (int k = 0; k <= last; k++)
                for (int c = 0; c < 3; c++)
                    if (rewrites(k, c) && C3dBindings.GetExpr(w, spec.ElementAt(k), c) is { } e)
                        return $"'{name}' holds its {(held == nameof(C3dWire.Span) ? "span" : "loop height")} at {text}, and its " +
                               $"{C3dBindings.Label(spec.ElementAt(k), c, "")} holds {e.Expr}: a held {(held == nameof(C3dWire.Span) ? "span" : "loop height")} " +
                               $"{what}, so that point cannot keep an expression. Replace one of them with a number.";
        }
        return null;
    }

    /// <summary>A held field as a sentence quotes it: its expression, or its number in DBU; null when it is not held.</summary>
    private static string? HeldText(C3dWire w, string property)
    {
        var spec = C3dBindings.SpecOf(typeof(C3dWire), property)!;
        if (C3dBindings.GetExpr(w, spec, 0) is { } e) return e.Expr;
        return C3dBindings.GetNumber(w, spec, 0) is { } n ? n.ToString("0", CultureInfo.InvariantCulture) + " DBU" : null;
    }

    /// <summary>
    /// brief-em3d-135 — <paramref name="w"/>'s points shaped, in place, to its held <see cref="C3dWire.Span"/> and then its held
    /// <see cref="C3dWire.LoopHeight"/>, from the numbers the resolver last wrote. Already there, nothing changes — so a held
    /// wire re-saves byte for byte. The ends keep their z: a span moving the end off its pad is elaboration's ordinary "no
    /// longer on a pad" refusal, never a silent re-route. Null on success, else the refusal, and the points are untouched.
    /// </summary>
    public static string? Hold(C3dWire w, string name, int dbuPerMicron, WireBondWorkspace workspace)
    {
        if (w.LoopHeight is null && w.Span is null) return null;
        if (HeldConflict(w, name) is { } conflict) return conflict;
        var points = w.Points;
        if (w.Span is { } span)
        {
            if (span <= 0) return $"'{name}' holds its span at {HeldText(w, nameof(C3dWire.Span))}, which is {span} DBU: a span is positive.";
            if (WithSpan(w, span) is not { } spanned)
                return $"'{name}' holds its span at {HeldText(w, nameof(C3dWire.Span))}, but its feet are one above the other: there is no run to lengthen it along.";
            points = spanned;
        }
        if (w.LoopHeight is { } loop)
        {
            string text = HeldText(w, nameof(C3dWire.LoopHeight))!;
            if (loop <= 0) return $"'{name}' holds its loop height at {text}, which is {loop} DBU: a loop height is positive.";
            var shaped = (C3dWire)C3dPersistence.DeserializeObject(C3dPersistence.SerializeObject(w));
            shaped.Points = points;
            var (outcome, fitted, footDrop) = FitLoopHeight(shaped, loop, c => AssemblyAtFeet(c, dbuPerMicron, workspace));
            switch (outcome)
            {
                case LoopFit.NoMeasure: return $"'{name}' holds its loop height at {text}, but the wire does not resolve, so it has no height to measure.";
                case LoopFit.BelowFeet:
                    return $"'{name}' holds its loop height at {text} ({loop} DBU), below what it can stand at: its feet are {footDrop} DBU " +
                           "apart in height, and a straight wire already measures that much above the lower pad.";
                case LoopFit.Straight: return $"'{name}' holds its loop height at {text}, but it is straight: it has no rise above its feet to scale.";
            }
            points = fitted!;
        }
        if (!ReferenceEquals(points, w.Points)) w.Points = points;
        return null;
    }

    private static Wire ToWBond(C3dWire w)
    {
        var wire = new Wire();
        foreach (var p in w.Points) wire.Points.Add(new WPoint3(p.X, p.Y, p.Z));
        return wire;
    }

    private static List<C3dPoint3> FromWBond(Wire wire) => [.. wire.Points.Select(p => new C3dPoint3(p.X, p.Y, p.Z))];

    // ── the Wire tool's shape ────────────────────────────────────────────────────────────────────

    /// <summary>LoopShape's seed arch between two feet (DBU) with an AXIS loop height of <paramref name="axisLoopDbu"/>
    /// (the axis's highest point above its lower foot) — wBond's own arithmetic, feet written exactly.</summary>
    public static List<C3dPoint3> Arch(C3dPoint3 start, C3dPoint3 end, long axisLoopDbu, int points = SeedPoints)
    {
        var wire = new Wire();
        LoopShape.Write(wire, new WPoint3(start.X, start.Y, start.Z), new WPoint3(end.X, end.Y, end.Z),
                        LoopShape.Seed(points), Math.Max(0, axisLoopDbu));
        return [.. wire.Points.Select(p => new C3dPoint3(p.X, p.Y, p.Z))];
    }

    /// <summary>The half-height of a section of diameter <paramref name="dNm"/>, DBU.</summary>
    public static double HalfHeightDbu(WireCrossSection section, long dNm, int dbuPerMicron)
        => Em3dWireSection.Of(section == WireCrossSection.Round ? Em3dSection.Circle : Em3dSection.Hexagon, dNm * 1e-9).Height / 2
           / C3dLowering.Metres(1, dbuPerMicron);

    /// <summary>
    /// R-em3d50-3a — the arch whose RESOLVED assembly loop height is <paramref name="assemblyDbu"/>: shaped by
    /// <see cref="Arch"/> to an axis target, measured by <paramref name="measure"/> (the resolved solid's assembly height,
    /// DBU, or null when it does not resolve), and corrected until the two agree to half a DBU. The best arch found is
    /// returned either way, with its measured height.
    /// </summary>
    public static (List<C3dPoint3> Points, double? MeasuredDbu) ForAssemblyHeight(
        C3dPoint3 start, C3dPoint3 end, long assemblyDbu, double halfHeightDbu, Func<List<C3dPoint3>, double?> measure,
        int points = SeedPoints)
    {
        double axis = assemblyDbu - halfHeightDbu;
        List<C3dPoint3>? best = null;
        double? bestMeasured = null;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            var arch = Arch(start, end, (long)Math.Round(axis, MidpointRounding.AwayFromZero), points);
            var m = measure(arch);
            if (m is not { } got) return (best ?? arch, bestMeasured);
            if (bestMeasured is null || Math.Abs(got - assemblyDbu) < Math.Abs(bestMeasured.Value - assemblyDbu))
                (best, bestMeasured) = (arch, got);
            if (Math.Abs(got - assemblyDbu) <= 0.5) break;
            double next = axis + (assemblyDbu - got);
            if (Math.Abs(next - axis) < 0.5) next = axis + Math.Sign(assemblyDbu - got);
            axis = next;
        }
        return (best!, bestMeasured);
    }
}
