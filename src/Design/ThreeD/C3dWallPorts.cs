// PWL-3 — a .c3d's WALL ports: a lumped gap from a PEC face of the air box to the end of the line that meets it.
//
// `docs/briefs/brief-pwl-3-wall-port-model-and-palace.md`. A wall port states what a wave port states — its plane, its offset
// and a rectangle round the line's end on that plane — with Kind Wall. It lowers to an ORDINARY lumped port carrying an
// Em3dWallPort record, so no solver lowers a third kind: the gap is a sheet in the plane of the line's face toward its return,
// from the face (−) to the line's end (+), across the line's width at its end.
//
// Three things change the SOLVED problem and never the document:
//   • the face becomes PEC with zero padding (ForceFaces), whatever the setup states — the wall is the port's ground;
//   • the line is cut back by the gap (Trim), because a line touching a PEC face is shorted there;
//   • the gap is g = min(w, h)/5, w the line's width at its end and h its height above its return (PWL-2 Q1: the calibrated
//     answer is flat from w/40 to w/5, so the size is chosen on cost, and is never a user setting).
//
// A wall port on a face normal to z (the floor or the lid) is refused by name in this version: its line would be vertical.

using System.Globalization;
using System.Numerics;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>The resolution, face forcing and line trimming of wall ports (PWL-3). See the file header.</summary>
public static class C3dWallPorts
{
    /// <summary>g as a fraction of min(width, height) — PWL-2 Q1.</summary>
    public const double GapFraction = 0.2;

    private static readonly string[] FaceKeys = ["xmin", "xmax", "ymin", "ymax"];

    /// <summary>The face key a port drawn on <paramref name="port"/>'s plane and offset lies on, given the content's extent; null
    /// when it lies on no x or y face of it.</summary>
    public static string? FaceOf(C3dPort port, int dbuPerMicron,
                                 (double X0, double Y0, double Z0, double X1, double Y1, double Z1) extent)
    {
        int axis = port.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (axis == 2 || port.Corners is not null) return null;
        double at = C3dLowering.Metres(port.Offset, dbuPerMicron), tol = 2 * C3dLowering.Metres(1, dbuPerMicron);
        double lo = axis == 0 ? extent.X0 : extent.Y0, hi = axis == 0 ? extent.X1 : extent.Y1;
        return Math.Abs(at - lo) <= tol ? FaceKeys[2 * axis] : Math.Abs(at - hi) <= tol ? FaceKeys[2 * axis + 1] : null;
    }

    /// <summary>
    /// The faces the document's modelled wall ports stand on, and a refusal when one of them also carries a wave port
    /// (R-pwl3-5: a face is one kind or the other). Empty when the document has no wall port.
    /// </summary>
    public static (IReadOnlySet<string> Faces, string? Refusal) Faces(C3dDocument doc,
        (double X0, double Y0, double Z0, double X1, double Y1, double Z1) extent)
    {
        var faces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.Ports.Where(p => p.Model && p.Kind == Em3dPortKind.Wall))
            if (FaceOf(p, doc.DbuPerMicron, extent) is { } f) faces.Add(f);
        foreach (var w in doc.Ports.Where(p => p.Model && p.Kind == Em3dPortKind.Wave))
            if (FaceOf(w, doc.DbuPerMicron, extent) is { } f && faces.Contains(f))
            {
                var walls = doc.Ports.Where(p => p.Model && p.Kind == Em3dPortKind.Wall && FaceOf(p, doc.DbuPerMicron, extent) == f)
                                     .Select(C3dPorts.Label);
                return (faces, $"The air box's {f} face carries the wave port {C3dPorts.Label(w)} and the wall port(s) " +
                               $"{string.Join(", ", walls)}. A face is fed by wave ports or by wall ports, not both: make them one kind.");
            }
        return (faces, null);
    }

    /// <summary>
    /// R-pwl3-1 — the setup with each of <paramref name="faces"/> a PEC face with no padding, and the note that says so; the
    /// setup is changed in place (the caller holds a clone).
    /// </summary>
    public static string? ForceFaces(EmSetup setup, IReadOnlySet<string> faces)
    {
        if (faces.Count == 0) return null;
        var box = setup.AirBox ?? new EmAirBox();
        var wall = new EmAirBoxFace(0, Em3dBoundaryKind.Pec);
        var changed = new List<string>();
        foreach (var f in faces.OrderBy(f => Array.IndexOf(FaceKeys, f)))
        {
            var was = f switch { "xmin" => box.XMin, "xmax" => box.XMax, "ymin" => box.YMin, _ => box.YMax };
            if (was is not { PaddingUm: 0, Boundary: Em3dBoundaryKind.Pec, PaddingPercent: null }) changed.Add(f);
            box = f switch
            {
                "xmin" => box with { XMin = wall }, "xmax" => box with { XMax = wall },
                "ymin" => box with { YMin = wall }, _      => box with { YMax = wall },
            };
        }
        setup.AirBox = box;
        return changed.Count == 0 ? null
            : $"The air box's {string.Join(" and ", changed)} face{(changed.Count == 1 ? " is" : "s are")} PEC with no padding for this run, " +
              "whatever the setup states: a wall port's face is the port's ground.";
    }

    /// <summary>
    /// One wall port, resolved against <paramref name="ctx"/>: the line that ends on its face inside its rectangle, the gap and
    /// the lumped port that fills it, or a refusal naming what was found.
    /// </summary>
    internal static C3dPortResult Resolve(C3dPort port, string label, Complex z0, C3dPortContext ctx, int dbuPerMicron)
    {
        C3dPortResult No(string why) => new(port, null, why, [], "");
        if (ctx.Box is not { } box)
            return No($"{label} is a wall port, which stands on a face of the air box, and no setup gives this view an air box.");
        if (port.Corners is not null || port.Angle != 0 || port.Cylinder is not null || port.Annulus is not null || port.Terminals is not null)
            return No($"{label} is a wall port, which is a rectangle on a face of the air box: it states no Corners, Angle, Cylinder, Annulus or Terminals.");
        int axis = port.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (axis == 2)
            return No($"{label} is a wall port on the XY plane. In this version a wall port stands on an x or y face of the air box, " +
                      "where a horizontal line ends; the floor and the lid take none.");

        double tol = 2 * ctx.Dbu + ctx.OutlineMoveM;
        double at = C3dLowering.Metres(port.Offset, dbuPerMicron);
        double lo = C3dPortContext.Get(box.Min, axis), hi = C3dPortContext.Get(box.Max, axis);
        int inward;
        string face;
        if (Math.Abs(at - lo) <= tol) { inward = +1; face = FaceKeys[2 * axis]; at = lo; }
        else if (Math.Abs(at - hi) <= tol) { inward = -1; face = FaceKeys[2 * axis + 1]; at = hi; }
        else
            return No($"{label} lies at {(axis == 0 ? "x" : "y")} = {Um(at)} µm, which is not a face of the air box " +
                      $"({(axis == 0 ? "x" : "y")} runs {Um(lo)} to {Um(hi)} µm). A wall port is drawn on the face where its line ends, " +
                      "and the line must run to the edge of the structure on that side.");

        // The rectangle's extent on the face: lateral (the face's horizontal axis) and z.
        var frame = C3dTurnedPorts.Frame(port, dbuPerMicron);
        var corners = new[] { frame.P(frame.U0, frame.V0), frame.P(frame.U1, frame.V1) };
        int lat = 1 - axis;
        double rLo = corners.Min(c => C3dPortContext.Get(c, lat)), rHi = corners.Max(c => C3dPortContext.Get(c, lat));
        double rZ0 = corners.Min(c => c.Z), rZ1 = corners.Max(c => c.Z);

        // The line: one conductor, not in the ground set, reaching the face inside the rectangle.
        bool Reaches(C3dPortContext.Conductor c)
        {
            var (x0, y0, z0b, x1, y1, z1) = c.Box;
            double cLo = axis == 0 ? x0 : y0, cHi = axis == 0 ? x1 : y1;
            double lLo = axis == 0 ? y0 : x0, lHi = axis == 0 ? y1 : x1;
            bool touches = inward > 0 ? Math.Abs(cLo - at) <= tol : Math.Abs(cHi - at) <= tol;
            return touches && lHi > rLo + tol && lLo < rHi - tol && z1 >= rZ0 - tol && z0b <= rZ1 + tol;
        }
        var candidates = ctx.Conductors.Where(c => c.Axis < 0 && !ctx.Ground.Contains(c.Name) && Reaches(c)).ToList();
        C3dPortContext.Conductor line;
        if (port.Positive is { } stated)
        {
            if (ctx.Conductors.FirstOrDefault(c => c.Name == stated && c.Axis < 0) is not { } named)
                return No($"{label}'s Positive '{stated}' is not a conductor of this view.");
            if (!Reaches(named))
                return No($"{label}'s Positive '{stated}' does not reach the {face} face inside {label}'s rectangle.");
            line = named;
        }
        else if (candidates.Count == 0)
            return No($"{label}: no line reaches the {face} face inside its rectangle. A wall port feeds the line that runs to the face; " +
                      "draw the line to the face, or move the rectangle over its end.");
        else if (candidates.Count > 1)
            return No($"{label}: {candidates.Count} lines reach the {face} face inside its rectangle ({string.Join(", ", candidates.Select(c => $"'{c.Name}'"))}). " +
                      "A wall port feeds one line: shrink the rectangle to one line's end, or state its Positive. *Make Port ▸ Wall* makes one per line.");
        else line = candidates[0];

        // The line's end: its width just inside the face, and the plane of its face toward the return.
        double probe = at + inward * Math.Max(4 * tol, 1e-9);
        if (EndSection(line, axis, probe) is not { } end)
            return No($"{label}'s line '{line.Name}' is not a sheet, an extruded outline or a box, and a wall port in this version feeds one of those.");
        var interval = end.Intervals.Where(i => i.Hi > rLo + tol && i.Lo < rHi - tol).ToList();
        if (interval.Count != 1)
            return No($"{label}'s line '{line.Name}' crosses its rectangle in {interval.Count} pieces at the {face} face; a wall port feeds one end.");
        var (wLo, wHi) = interval[0];
        double width = wHi - wLo;

        // Its return: the highest ground conductor below the line's end (a PEC floor included).
        double regionLo = Math.Min(at, at + inward * 10 * width), regionHi = Math.Max(at, at + inward * 10 * width);
        double top = double.NegativeInfinity;
        string? returnName = null;
        foreach (var c in ctx.Conductors.Where(c => ctx.Ground.Contains(c.Name)))
        {
            if (c.Axis >= 0 && c.Axis != 2) continue;               // the walls themselves are not below the line
            var (x0, y0, z0b, x1, y1, z1) = c.Box;
            double cLo = axis == 0 ? x0 : y0, cHi = axis == 0 ? x1 : y1, lLo = axis == 0 ? y0 : x0, lHi = axis == 0 ? y1 : x1;
            if (z1 > end.Z + tol || cHi < regionLo || cLo > regionHi || lHi < wLo || lLo > wHi) continue;
            if (z1 > top) { top = z1; returnName = c.Name; }
        }
        if (returnName is null)
            return No($"{label}'s line '{line.Name}' has no ground beneath its end. A wall port's line returns through a ground plane or a " +
                      "PEC floor below it; draw one, or mark the plane ground.");
        double height = end.Z - top;
        double gap = GapFraction * Math.Min(width, height);
        double length = (axis == 0 ? line.Box.X1 - line.Box.X0 : line.Box.Y1 - line.Box.Y0);
        if (!(height > 0) || !(gap > 0)) return No($"{label}'s line '{line.Name}' lies on its return; a wall port needs height above it.");
        if (length < 4 * gap)
            return No($"{label}'s line '{line.Name}' is {Um(length)} µm long, and its {Um(gap)} µm gap needs it at least four gaps long.");

        // The lumped port in the gap: from the face (−) to the line's new end (+), across the line, in the line's lower plane.
        double gEnd = at + inward * gap;
        Point3 Pt(double along, double across) => axis == 0 ? new Point3(along, across, end.Z) : new Point3(across, along, end.Z);
        var a = Pt(Math.Min(at, gEnd), wLo);
        var b = Pt(Math.Max(at, gEnd), wHi);
        var direction = axis == 0 ? new Point3(inward, 0, 0) : new Point3(0, inward, 0);
        string wallName = Em3dAirBox.FaceName(face);
        var refOrigin = Pt(at, 0.5 * (wLo + wHi));
        var resolved = new Em3dPort(port.Number, C3dPorts.ProblemName(port.Number), line.Name, wallName, a, b, direction, z0,
                                    new Em3dReferencePlane(refOrigin, direction, 0))
        {
            Wall = new Em3dWallPort(face, axis, at, inward, gap, wLo, wHi, end.Z, height, line.Name),
        };
        string reason = $"a wall port on the {face} face: '{line.Name}' ({Um(width)} µm wide, {Um(height)} µm above '{returnName}') is cut " +
                        $"back {Um(gap)} µm from the face, and the gap is the port, from the wall (−) to the line (+)";
        return new C3dPortResult(port, resolved, null, [("face", [wallName]), ("line", [line.Name])], reason);
    }

    /// <summary>A conductor's cross-section just inside a face: the intervals it covers along the face's lateral axis, and the plane
    /// of its face toward the return (a sheet's own plane, a solid's bottom). Null for a kind a wall port does not feed.</summary>
    private static (List<(double Lo, double Hi)> Intervals, double Z)? EndSection(C3dPortContext.Conductor c, int axis, double probe)
    {
        if (c.Sheet is { } sh)
        {
            if (sh.Frame is not null) return null;                    // a vertical sheet
            return (Slice(sh.Outline, sh.Holes, axis, probe), sh.Z);
        }
        if (c.Prism is { } pr) return (Slice(pr.Outline, pr.Holes, axis, probe), pr.ZBottom);
        if (c.Mesh is not null && c.Cylinder is null && c.Patch is null && c.Closed)
        {
            // A box: its own bounds are its section.
            var (x0, y0, z0, x1, y1, z1) = c.Box;
            return ([(axis == 0 ? y0 : x0, axis == 0 ? y1 : x1)], z0);
        }
        return null;
    }

    /// <summary>The intervals a polygon covers along the other horizontal axis at <paramref name="at"/> (see <see cref="Slice"/>), for the
    /// wall calibration's cross-section.</summary>
    public static List<(double Lo, double Hi)> SliceRings(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes, int axis, double at)
        => Slice(outline, holes, axis, at);

    /// <summary>The intervals a polygon (outline minus holes) covers along the other horizontal axis, at coordinate
    /// <paramref name="at"/> along <paramref name="axis"/> — the even-odd crossings of every ring.</summary>
    internal static List<(double Lo, double Hi)> Slice(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes,
                                                       int axis, double at)
    {
        var xs = new List<double>();
        void Ring(IReadOnlyList<Point2> r)
        {
            for (int i = 0; i < r.Count; i++)
            {
                var p = r[i]; var q = r[(i + 1) % r.Count];
                double pa = axis == 0 ? p.X : p.Y, qa = axis == 0 ? q.X : q.Y;
                if ((pa <= at && qa > at) || (qa <= at && pa > at))
                {
                    double t = (at - pa) / (qa - pa);
                    double pl = axis == 0 ? p.Y : p.X, ql = axis == 0 ? q.Y : q.X;
                    xs.Add(pl + t * (ql - pl));
                }
            }
        }
        Ring(outline);
        foreach (var h in holes) Ring(h);
        xs.Sort();
        var r = new List<(double, double)>();
        for (int i = 0; i + 1 < xs.Count; i += 2) r.Add((xs[i], xs[i + 1]));
        return r;
    }

    /// <summary>
    /// R-pwl3-2 — cut each wall port's line back by its gap, in the SOLVED problem only. The cut removes everything of the line on
    /// the face's side of the gap's inner edge, which is its end and nothing else: a line that also runs elsewhere along that face
    /// inside the gap is refused, since cutting it there would remove metal that is not the port's.
    /// </summary>
    public static string? Trim(List<Em3dSolid> solids, List<Em3dSheet> sheets, IEnumerable<Em3dPort> ports, double tolM)
    {
        foreach (var p in ports)
        {
            if (p.Wall is not { } w) continue;
            double cut = w.FaceAtM + w.Inward * w.GapM;
            // keep = the side of `cut` away from the face
            bool Keep(double coord) => w.Inward > 0 ? coord >= cut : coord <= cut;

            int si = sheets.FindIndex(s => s.Name == w.Conductor);
            if (si >= 0)
            {
                var s = sheets[si];
                if (Refuse(s.Outline, s.Holes) is { } why) return why;
                var outline = Clip(s.Outline, w.Axis, cut, Keep);
                var holes = s.Holes.Select(h => (IReadOnlyList<Point2>)Clip(h, w.Axis, cut, Keep)).Where(h => h.Count >= 3).ToList();
                sheets[si] = s with { Outline = outline, Holes = holes };
                continue;
            }
            int pi = solids.FindIndex(s => s.Name == w.Conductor);
            if (pi < 0) return $"The wall port's line '{w.Conductor}' is not in the solved problem.";
            var solid = solids[pi];
            switch (solid.Primitive)
            {
                case Em3dExtrudedPolygon pr:
                    if (Refuse(pr.Outline, pr.Holes) is { } why) return why;
                    solids[pi] = solid with
                    {
                        Primitive = pr with
                        {
                            Outline = Clip(pr.Outline, w.Axis, cut, Keep),
                            Holes = [.. pr.Holes.Select(h => (IReadOnlyList<Point2>)Clip(h, w.Axis, cut, Keep)).Where(h => h.Count >= 3)],
                        },
                    };
                    break;
                case Em3dBox bx:
                    var min = bx.Min; var max = bx.Max;
                    if (w.Axis == 0) { if (w.Inward > 0) min = min with { X = cut }; else max = max with { X = cut }; }
                    else { if (w.Inward > 0) min = min with { Y = cut }; else max = max with { Y = cut }; }
                    solids[pi] = solid with { Primitive = new Em3dBox(min, max) };
                    break;
                default:
                    return $"The wall port's line '{w.Conductor}' is not a sheet, an extruded outline or a box.";
            }

            string? Refuse(IReadOnlyList<Point2> outline, IReadOnlyList<IReadOnlyList<Point2>> holes)
            {
                double mid = w.FaceAtM + w.Inward * 0.5 * w.GapM;
                foreach (var (lo, hi) in Slice(outline, holes, w.Axis, mid))
                    if (lo < w.LineLoM - tolM || hi > w.LineHiM + tolM)
                        return $"The line '{w.Conductor}' also runs along the {w.Face} face beside its wall port's end, inside the port's " +
                               $"{Um(w.GapM)} µm gap, and cutting the gap would remove that metal too. Keep other metal of the line at least " +
                               "one gap away from the face, or make it a separate object.";
                return null;
            }
        }
        return null;
    }

    /// <summary>A ring clipped to the half-plane <paramref name="keep"/> of the line axis = <paramref name="cut"/>
    /// (Sutherland–Hodgman against one edge, which any simple ring may be clipped by).</summary>
    private static List<Point2> Clip(IReadOnlyList<Point2> ring, int axis, double cut, Func<double, bool> keep)
    {
        var r = new List<Point2>(ring.Count + 2);
        for (int i = 0; i < ring.Count; i++)
        {
            var p = ring[i]; var q = ring[(i + 1) % ring.Count];
            double pa = axis == 0 ? p.X : p.Y, qa = axis == 0 ? q.X : q.Y;
            bool pin = keep(pa), qin = keep(qa);
            if (pin) r.Add(p);
            if (pin != qin)
            {
                double t = (cut - pa) / (qa - pa);
                r.Add(axis == 0 ? new Point2(cut, p.Y + t * (q.Y - p.Y)) : new Point2(p.X + t * (q.X - p.X), cut));
            }
        }
        return r;
    }

    /// <summary>What a run with wall ports says of their gaps.</summary>
    public static string GapNote(IEnumerable<Em3dPort> ports)
    {
        var walls = ports.Where(p => p.Wall is not null).ToList();
        return $"{walls.Count} wall port(s) ({string.Join(", ", walls.Select(p => $"{p.Number} on {p.Wall!.Face}"))}): each line is cut back " +
               $"by its gap ({string.Join(", ", walls.Select(p => Um(p.Wall!.GapM) + " µm").Distinct())}) and fed across it, and the gap is " +
               "calibrated out by line standards the run solves before the structure.";
    }

    private static string Um(double m) => (m * 1e6).ToString("0.###", CultureInfo.InvariantCulture);
}
