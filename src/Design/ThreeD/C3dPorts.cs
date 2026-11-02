// brief-em3d-49 R-em3d49-2 — a .c3d's ports, turned into the neutral problem's Em3dPort list.
//
// PORTS BELONG TO THE DOCUMENT (R-em3d49-2a). Every setup uses all of them; a placed cell's ports are never used
// (overview §1k) — a port may NAME a conductor inside an instance (U1/pad3), which is the parent choosing where its
// signal enters.
//
// POLARITY BY CONTACT (R-em3d49-2b). A lumped port's rectangle has four edges. Each edge — its two ends pulled in by
// two DBU, so a conductor meeting the edge only at a corner does not count — is tested against every elaborated
// conductor (solids of role Conductor, every sheet, and the setup's PEC air-box faces) for contact to within ONE DBU.
// Exactly one pair of OPPOSITE edges must each touch exactly one conductor, the two different; that pair's axis is the
// port's direction. The NEGATIVE end is the one in the setup's ground set (its Ground3D net, a layout instance's
// ground-reference conductors, a PEC air-box face); failing that, the one with the larger elaborated surface. Flip
// swaps them. Stating Positive AND Negative overrides inference entirely: the contact query over all conductors is not
// run (C3dPortContext.InferenceRuns counts it, which is gate 1's counter), and only the two named objects are measured against the
// edges to find which pair they sit on.
//
// A refusal names what was found — which conductor on which edge — because "the port is invalid" gives nothing to fix.
//
// A WAVE PORT lies on an air-box face of the SETUP being run (the box is per setup), checked here because only here is
// there a box. Its two conductors are the ones whose surface meets the rectangle's region of that plane, and its
// voltage path runs across the gap between them at the positive conductor's centre — brief 23's rule (return face to
// signal face along the line's centre) stated for any geometry. A stated VoltagePath is used as stated.
//
// Geometry is metres, read from the elaboration — the model the editor draws and the solver gets. A cylinder is
// measured analytically: its facets stand up to half a percent of its radius inside the true surface, which is far
// more than a DBU.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>What one port's inference found (R-em3d49-5c: <c>check</c> reports it, <c>explain</c> walks it).</summary>
/// <param name="Port">The document's record.</param>
/// <param name="Resolved">The neutral port, or null when refused.</param>
/// <param name="Refusal">Why the port cannot be built, naming what was found.</param>
/// <param name="Contacts">Each edge and the conductors that touch it (left, right, bottom, top), for a lumped port whose
/// inference ran; a wave port's candidates under "region".</param>
/// <param name="Reason">Why the negative end is the one it is — or that both ends were stated.</param>
public sealed record C3dPortResult(C3dPort Port, Em3dPort? Resolved, string? Refusal,
                                   IReadOnlyList<(string Edge, IReadOnlyList<string> Objects)> Contacts, string Reason)
{
    /// <summary>The port's label in a sentence: <c>P1</c>, or <c>port 1</c> when it has no name.</summary>
    public string Label => C3dPorts.Label(Port);

    /// <summary>brief-em3d-114 R-em3d114-2a — a multi-terminal wave port's terminals, each its own neutral port, in the
    /// document's terminal order; <see cref="Resolved"/> is then the first. Null on every other port, and when refused.</summary>
    public IReadOnlyList<Em3dPort>? Terminals { get; init; }

    /// <summary>Every neutral port this one lowers to: its terminals, or itself, or none when refused.</summary>
    public IReadOnlyList<Em3dPort> All => Terminals ?? (Resolved is { } r ? [r] : []);

    /// <summary>brief-em3d-121 D1 — a reading the port keeps and the gesture would not make, naming the alternative; null on
    /// every other port. A run adds it to its notes, and <c>check</c>/<c>explain</c> print it after the reason.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// The conductors of one elaboration, ready to be measured against port edges: tessellated once, bounded, with their
/// surface areas. Build one per elaboration (and air box) and ask it about every port — the editor's port tool asks on
/// every mouse move.
/// </summary>
public sealed class C3dPortContext
{
    internal sealed record Conductor(string Name, (double X0, double Y0, double Z0, double X1, double Y1, double Z1) Box,
                                     Em3dTriangleMesh? Mesh, Em3dCylinder? Cylinder, double Area, int Axis = -1, double At = 0)
    {
        /// <summary>brief-sar-8b — a sheet on a cylinder: its patch, measured exactly by a gap on the same cylinder (its
        /// <see cref="Mesh"/> is the patch's tessellation, for anything else).</summary>
        public Em3dCylinderPatch? Patch { get; init; }
    }

    internal readonly List<Conductor> Conductors = [];
    internal readonly HashSet<string> Ground;

    /// <summary>The air box the ports are checked against, or null (no setup: wave ports cannot be placed).</summary>
    public Em3dAirBox? Box { get; }

    /// <summary>brief-em3d-93 — the elaboration's not-modelled content: a modelled port on one of these is refused.</summary>
    public IReadOnlySet<string> NotModelled { get; }

    /// <summary>The document's DBU in metres — the contact tolerance.</summary>
    public double Dbu { get; }

    /// <summary>The document's DBU per micrometre.</summary>
    public int DbuPerMicron { get; }

    /// <summary>brief-emb-2 R-emb2-3 — the farthest Robust meshing moved a placed layout's outline in this elaboration, metres:
    /// how far from the moved outline an inferred port's edge may sit and still be taken to touch it (and then follow it).</summary>
    public double OutlineMoveM { get; }

    /// <summary>Contact queries over all conductors made through this context — gate 1 reads that a port with both ends
    /// stated makes none.</summary>
    public int InferenceRuns { get; internal set; }

    /// <summary>The elaborated content's centre, metres — which side of a lumped port is "into the structure".</summary>
    internal Point3 Centre { get; }

    /// <param name="ground">The ground set: conductors on the setup's Ground3D net and a layout instance's ground-reference
    /// conductors. A PEC air-box face is always in it.</param>
    public C3dPortContext(C3dElaboration elaboration, int dbuPerMicron, Em3dAirBox? box, IEnumerable<string> ground)
    {
        Box = box;
        NotModelled = elaboration.NotModelled;
        DbuPerMicron = dbuPerMicron;
        Dbu = C3dLowering.Metres(1, dbuPerMicron);
        OutlineMoveM = elaboration.OutlineMoveM;
        Ground = new HashSet<string>(ground, StringComparer.Ordinal);
        foreach (var s in elaboration.Solids.Where(s => s.Role == Em3dRole.Conductor))
        {
            if (s.Primitive is Em3dCylinder c)
            {
                double len = Math.Sqrt(Sq(c.AxisEnd.X - c.AxisStart.X) + Sq(c.AxisEnd.Y - c.AxisStart.Y) + Sq(c.AxisEnd.Z - c.AxisStart.Z));
                Conductors.Add(new Conductor(s.Name, Em3dProblem.Bounds(c), null, c,
                                             2 * Math.PI * c.Radius * len + 2 * Math.PI * c.Radius * c.Radius));
                continue;
            }
            var mesh = Em3dTessellation.Of(s);
            Conductors.Add(new Conductor(s.Name, Em3dProblem.Bounds(s.Primitive), mesh, null, Area(mesh)));
        }
        foreach (var sh in elaboration.Sheets)
        {
            var mesh = Em3dTessellation.OfSheet(sh);
            // Both of a sheet's faces are surface: a sheet is a conductor thin enough to be one.
            Conductors.Add(new Conductor(sh.Name, sh.WorldBounds(), mesh, null, 2 * Area(mesh)));
        }
        foreach (var cs in elaboration.CylinderSheets)
        {
            var p = cs.Patch;
            Conductors.Add(new Conductor(cs.Name, cs.WorldBounds(), p.Tessellate(cs.Name), null, 2 * p.Radius * (p.Alpha1 - p.Alpha0) * (p.Hi - p.Lo))
                           { Patch = p });
        }
        if (box is not null)
        {
            string[] keys = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
            Em3dBoundaryKind[] kinds = [box.Faces.XMin, box.Faces.XMax, box.Faces.YMin, box.Faces.YMax, box.Faces.ZMin, box.Faces.ZMax];
            for (int k = 0; k < 6; k++)
            {
                if (kinds[k] != Em3dBoundaryKind.Pec) continue;
                int axis = k / 2;
                double at = k % 2 == 0 ? Get(box.Min, axis) : Get(box.Max, axis);
                double[] lo = [box.Min.X, box.Min.Y, box.Min.Z], hi = [box.Max.X, box.Max.Y, box.Max.Z];
                lo[axis] = hi[axis] = at;
                double area = 1;
                for (int a = 0; a < 3; a++) if (a != axis) area *= hi[a] - lo[a];
                string name = Em3dAirBox.FaceName(keys[k]);
                Conductors.Add(new Conductor(name, (lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]), null, null, area, axis, at));
                Ground.Add(name);
            }
        }
        var e = elaboration.Extent() ?? (0, 0, 0, 0, 0, 0);
        Centre = new Point3((e.X0 + e.X1) / 2, (e.Y0 + e.Y1) / 2, (e.Z0 + e.Z1) / 2);
    }

    private C3dPortContext(C3dPortContext from, string omit)
    {
        Box = from.Box;
        NotModelled = from.NotModelled;
        DbuPerMicron = from.DbuPerMicron;
        Dbu = from.Dbu;
        OutlineMoveM = from.OutlineMoveM;
        Ground = new HashSet<string>(from.Ground.Where(g => g != omit), StringComparer.Ordinal);
        Conductors.AddRange(from.Conductors.Where(c => c.Name != omit));
        Centre = from.Centre;
    }

    /// <summary>This context with the conductor named <paramref name="conductor"/> left out — a port made from a sheet's surface
    /// REPLACES that sheet, so the sheet is not one of the conductors the port's edges meet.</summary>
    public C3dPortContext Without(string conductor) => new(this, conductor);

    /// <summary>The distance from segment ab to conductor <paramref name="c"/>, metres.</summary>
    /// <param name="bounded">True (contact queries): anything further than 2 DBU outside the conductor's box is
    /// infinitely far, which keeps inference cheap. False: the true distance however far — what choosing a stated
    /// port's axis needs, since a sheet stopped short of its metal is far from both conductors by the same few µm.</param>
    internal double Distance(Conductor c, Point3 a, Point3 b, bool bounded = true)
    {
        double tol = 2 * Dbu + OutlineMoveM;       // brief-emb-2 R-emb2-3: a moved outline is still in reach
        var (x0, y0, z0, x1, y1, z1) = c.Box;
        if (bounded &&
            (Math.Min(a.X, b.X) > x1 + tol || Math.Max(a.X, b.X) < x0 - tol || Math.Min(a.Y, b.Y) > y1 + tol ||
             Math.Max(a.Y, b.Y) < y0 - tol || Math.Min(a.Z, b.Z) > z1 + tol || Math.Max(a.Z, b.Z) < z0 - tol))
            return double.PositiveInfinity;
        if (c.Axis >= 0)
        {
            // A PEC air-box face: the segment must lie on its plane.
            return Math.Max(Math.Abs(Get(a, c.Axis) - c.At), Math.Abs(Get(b, c.Axis) - c.At));
        }
        if (c.Cylinder is { } cyl) return SegmentToCylinder(a, b, cyl);
        double best = double.PositiveInfinity;
        var m = c.Mesh!;
        // A triangle is measured only when it could matter: the gap between its box and the segment's is a lower bound on its
        // distance, so one no nearer than the best so far cannot lower the minimum, and bounded, one beyond the tolerance is as
        // far as the bounded contract says (infinitely). A board's ground pour is one conductor of thousands of triangles whose
        // box is the whole board, and every port measured every one: ~8 ms a port.
        double sx0 = Math.Min(a.X, b.X), sx1 = Math.Max(a.X, b.X), sy0 = Math.Min(a.Y, b.Y), sy1 = Math.Max(a.Y, b.Y),
               sz0 = Math.Min(a.Z, b.Z), sz1 = Math.Max(a.Z, b.Z);
        foreach (var t in m.Triangles)
        {
            Point3 p = m.Vertices[t.A], q = m.Vertices[t.B], r = m.Vertices[t.C];
            double gx = Math.Max(0, Math.Max(Math.Min(p.X, Math.Min(q.X, r.X)) - sx1, sx0 - Math.Max(p.X, Math.Max(q.X, r.X))));
            double gy = Math.Max(0, Math.Max(Math.Min(p.Y, Math.Min(q.Y, r.Y)) - sy1, sy0 - Math.Max(p.Y, Math.Max(q.Y, r.Y))));
            double gz = Math.Max(0, Math.Max(Math.Min(p.Z, Math.Min(q.Z, r.Z)) - sz1, sz0 - Math.Max(p.Z, Math.Max(q.Z, r.Z))));
            double lowest = Math.Sqrt(gx * gx + gy * gy + gz * gz);
            if (lowest >= best || (bounded && lowest > tol)) continue;
            best = Math.Min(best, Geometry3.SegmentTriangle(a, b, p, q, r));
            if (best == 0) break;
        }
        return best;
    }

    /// <summary>The distance from segment ab to a solid cylinder — convex along the segment, so a ternary search.</summary>
    private static double SegmentToCylinder(Point3 a, Point3 b, Em3dCylinder c)
    {
        double lo = 0, hi = 1;
        double F(double t) => PointToCylinder(new Point3(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t), c);
        for (int i = 0; i < 80; i++)
        {
            double m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
            if (F(m1) <= F(m2)) hi = m2; else lo = m1;
        }
        return Math.Min(F((lo + hi) / 2), Math.Min(F(0), F(1)));
    }

    private static double PointToCylinder(Point3 p, Em3dCylinder c)
    {
        var ax = new Point3(c.AxisEnd.X - c.AxisStart.X, c.AxisEnd.Y - c.AxisStart.Y, c.AxisEnd.Z - c.AxisStart.Z);
        double len = Math.Sqrt(ax.X * ax.X + ax.Y * ax.Y + ax.Z * ax.Z);
        var u = new Point3(ax.X / len, ax.Y / len, ax.Z / len);
        var d = new Point3(p.X - c.AxisStart.X, p.Y - c.AxisStart.Y, p.Z - c.AxisStart.Z);
        double s = d.X * u.X + d.Y * u.Y + d.Z * u.Z;
        double rx = d.X - s * u.X, ry = d.Y - s * u.Y, rz = d.Z - s * u.Z;
        double dr = Math.Max(0, Math.Sqrt(rx * rx + ry * ry + rz * rz) - c.Radius);
        double da = Math.Max(0, Math.Max(-s, s - len));
        return Math.Sqrt(dr * dr + da * da);
    }

    private static double Area(Em3dTriangleMesh m)
    {
        double sum = 0;
        foreach (var t in m.Triangles)
        {
            var (p, q, r) = (m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C]);
            var n = Geometry3.Cross(Geometry3.Sub(q, p), Geometry3.Sub(r, p));
            sum += Math.Sqrt(Geometry3.Dot(n, n)) / 2;
        }
        return sum;
    }

    internal static double Get(Point3 q, int a) => a == 0 ? q.X : a == 1 ? q.Y : q.Z;
    private static double Sq(double v) => v * v;
}

public static class C3dPorts
{
    /// <summary>A port's label in a sentence: its name, or <c>port N</c> (a multi-terminal port's numbers joined).</summary>
    public static string Label(C3dPort p) => p.Name is { Length: > 0 } n ? n : $"port {string.Join("/", Numbers(p))}";

    // ── brief-em3d-114: a terminal is a port (overview rule 1) ──────────────────────────────────

    /// <summary>The port-level keys a multi-terminal port's terminals carry instead (R-em3d114-1a).</summary>
    public static readonly IReadOnlySet<string> PortLevelKeys =
        new HashSet<string>(StringComparer.Ordinal) { "Number", "Z0", "Positive", "Negative", "Flip", "VoltagePath" };

    /// <summary>Whether <paramref name="p"/>'s own value states port-level <paramref name="key"/> — every key but Z0, whose
    /// default is a value (<see cref="C3dPort.Stated"/> says whether a file wrote it).</summary>
    internal static bool StatesPortLevel(C3dPort p, string key) => key switch
    {
        "Number"      => p.Number != 0,
        "Positive"    => p.Positive is not null,
        "Negative"    => p.Negative is not null,
        "Flip"        => p.Flip,
        "VoltagePath" => p.VoltagePath is not null,
        _             => false,
    };

    /// <summary>True when <paramref name="p"/> is a port with terminals (one terminal included: it is refused, not ignored).</summary>
    public static bool HasTerminals(C3dPort p) => p.Terminals is { Count: > 0 };

    /// <summary>The result-port numbers <paramref name="p"/> takes: its terminals', or its own.</summary>
    public static IReadOnlyList<int> Numbers(C3dPort p) => p.Terminals is { Count: > 0 } t ? [.. t.Select(x => x.Number)] : [p.Number];

    /// <summary>Every result-port number the document's ports and terminals take, in document order.</summary>
    public static IEnumerable<int> UsedNumbers(C3dDocument doc) => doc.Ports.SelectMany(Numbers);

    /// <summary>The port that holds result-port <paramref name="number"/> — itself, or as a terminal — or null.</summary>
    public static C3dPort? OwnerOf(C3dDocument doc, int number) => doc.Ports.FirstOrDefault(p => Numbers(p).Contains(number));

    /// <summary>The terminal numbered <paramref name="number"/> of <paramref name="p"/>, or null.</summary>
    public static C3dTerminal? TerminalOf(C3dPort p, int number) => p.Terminals?.FirstOrDefault(t => t.Number == number);

    /// <summary>A terminal in a sentence: <c>terminal 'P2' of Left</c>.</summary>
    public static string TerminalLabel(C3dPort p, C3dTerminal t)
        => $"terminal '{(t.Name is { Length: > 0 } n ? n : t.Number.ToString(CultureInfo.InvariantCulture))}' of {Label(p)}";

    /// <summary>The name the neutral problem gives port <paramref name="number"/> — the generator's, so a magnetostatic
    /// terminal's <c>Source</c> resolves the same way from either container.</summary>
    public static string ProblemName(int number) => $"port/{number}";

    /// <summary>Every port of <paramref name="doc"/>, resolved — or refused, each with what was found.</summary>
    public static IReadOnlyList<C3dPortResult> Resolve(C3dDocument doc, C3dPortContext context)
    {
        var results = new List<C3dPortResult>(doc.Ports.Count);
        // R-em3d114-1b — one namespace: every terminal number is unique across every port and every terminal.
        var numbers = new Dictionary<int, string>();
        foreach (var p in doc.Ports)
        {
            string? clash = null;
            var entries = p.Terminals is { Count: > 0 } ts
                ? ts.Select(t => (t.Number, Who: TerminalLabel(p, t), Upper: "T" + TerminalLabel(p, t)[1..])).ToList()
                : [(p.Number, Label(p), Label(p))];
            foreach (var (n, who, upper) in entries)
            {
                if (n < 1) clash ??= $"{upper} has number {n}; a port is numbered from 1.";
                else if (numbers.TryGetValue(n, out var first))
                    clash ??= $"{upper} and {first} are both port {n}; each port has its own number" +
                              (p.Terminals is { Count: > 0 } ? ", and a terminal is a port." : ".");
                numbers.TryAdd(n, who);
            }
            results.Add(clash is not null ? Refuse(p, clash) : Resolve(p, doc.DbuPerMicron, context));
        }
        return results;
    }

    private static C3dPortResult Refuse(C3dPort p, string why, IReadOnlyList<(string, IReadOnlyList<string>)>? contacts = null)
        => new(p, null, why, contacts ?? [], "");

    /// <summary>One port, resolved against <paramref name="context"/>. brief-em3d-93 — a MODELLED port whose conductor is not
    /// modelled is refused, naming both; a port that is itself off is resolved as drawn (its rows and arrow still show where it
    /// is), and no run takes it.</summary>
    public static C3dPortResult Resolve(C3dPort port, int dbuPerMicron, C3dPortContext context)
    {
        var r = ResolveDrawn(port, dbuPerMicron, context);
        if (!port.Model || r.Resolved is null || context.NotModelled.Count == 0) return r;
        foreach (var p in r.All)
            foreach (var (conductor, positive) in new[] { (p.PositiveObject, true), (p.NegativeObject, false) })
                if (context.NotModelled.Contains(conductor))
                    return r with { Resolved = null, Terminals = null, Refusal = C3dModelled.PortConductorRefusal(port, conductor, positive) };
        return r;
    }

    private static C3dPortResult ResolveDrawn(C3dPort port, int dbuPerMicron, C3dPortContext context)
    {
        string label = Label(port);
        Complex z0 = default;
        if (port.Terminals is { Count: > 0 } terminals)
        {
            if (TerminalShapeRefusal(port, label, terminals) is { } shape) return Refuse(port, shape);
        }
        else
        {
            if (!TryParseZ0(port.Z0, out z0))
                return Refuse(port, $"{label}'s Z0 '{port.Z0}' is not an impedance: write a number of ohms, or a complex one such as 25+j10.");
            if (!(z0.Real > 0))
                return Refuse(port, $"{label}'s Z0 is {port.Z0} Ω; a port's reference impedance has a positive real part.");
        }
        if (port.Cylinder is { } onCylinder)
        {
            if (port.Terminals is null && (port.Positive is null) != (port.Negative is null))
                return Refuse(port, $"{label} states its {(port.Positive is null ? "Negative" : "Positive")} end only. State both " +
                                    "ends, or neither and let what the port touches decide.");
            return port.Kind == Em3dPortKind.Wave
                ? Refuse(port, $"{label} is a wave port on a cylinder. A gap on a cylinder is a lumped port; a wave port lies on a face of the air box.")
                : LumpedOnCylinder(port, label, z0, context, onCylinder, dbuPerMicron);
        }
        if (port.Rect.Size.U <= 0 || port.Rect.Size.V <= 0)
            return Refuse(port, $"{label}'s rectangle has no area ({port.Rect.Size.U} × {port.Rect.Size.V} DBU).");
        if (port.Terminals is null && (port.Positive is null) != (port.Negative is null))
            return Refuse(port, $"{label} states its {(port.Positive is null ? "Negative" : "Positive")} end only. State both " +
                                "ends, or neither and let what the port touches decide.");

        double M(long v) => C3dLowering.Metres(v, dbuPerMicron);
        double u0 = M(port.Rect.Min.U), v0 = M(port.Rect.Min.V);
        double u1 = M(port.Rect.Min.U + port.Rect.Size.U), v1 = M(port.Rect.Min.V + port.Rect.Size.V);
        double h = M(port.Offset);
        Point3 P(double u, double v) => C3dLowering.OnPlane(port.Plane, u, v, h);
        var corners = new[] { P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1) };
        var min = new Point3(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Min(c => c.Z));
        var max = new Point3(corners.Max(c => c.X), corners.Max(c => c.Y), corners.Max(c => c.Z));
        int normalAxis = port.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        var (uDir, vDir) = PlaneAxes(port.Plane);

        return port.Kind == Em3dPortKind.Wave
            ? Wave(port, label, z0, context, u0, v0, u1, v1, h, normalAxis, uDir, vDir, min, max, P)
            : Lumped(port, label, z0, context, u0, v0, u1, v1, normalAxis, uDir, vDir, min, max, P);
    }

    /// <summary>
    /// R-em3d114-1a/-1e — what a port with terminals cannot be, before any geometry: one that also states a port-level key, one
    /// with a single terminal, a lumped one, a terminal with no conductor or no impedance, and two terminals on one conductor.
    /// </summary>
    private static string? TerminalShapeRefusal(C3dPort port, string label, List<C3dTerminal> terminals)
    {
        var stated = PortLevelKeys.Where(k => port.Stated?.Contains(k) == true || StatesPortLevel(port, k)).ToList();
        if (stated.Count > 0)
            return $"{label} states both Terminals and {Join(stated)}: with Terminals, each terminal states its own Number, Z0, " +
                   $"conductor, VoltagePath and Flip, and the port states none of them. Remove {Join(stated)} from the port.";
        if (terminals.Count == 1)
            return $"{label} has one terminal. A wave port with one signal conductor is the ordinary wave port: write its Number " +
                   "and Z0 on the port and remove Terminals.";
        if (port.Kind == Em3dPortKind.Lumped)
            return $"{label} is a lumped port with Terminals. A lumped sheet has one gap, so it is one port; make it a wave port, " +
                   "or remove Terminals.";
        foreach (var t in terminals)
        {
            string who = TerminalLabel(port, t);
            if (t.Conductor is not { Length: > 0 })
                return $"{char.ToUpperInvariant(who[0])}{who[1..]} names no conductor: a terminal is the signal conductor it is the port of.";
            if (!TryParseZ0(t.Z0, out var z) || !(z.Real > 0))
                return $"{char.ToUpperInvariant(who[0])}{who[1..]}'s Z0 '{t.Z0}' is not a reference impedance: a number of ohms with a " +
                       "positive real part, or a complex one such as 25+j10.";
        }
        foreach (var g in terminals.GroupBy(t => t.Conductor, StringComparer.Ordinal).Where(g => g.Count() > 1))
            return $"{label}'s terminals {Join([.. g.Select(t => $"'{t.Name}'")])} all name '{g.Key}'; each terminal is its own conductor.";
        return null;

        static string Join(IReadOnlyList<string> items)
            => items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    }

    // ── lumped ───────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] EdgeNames = ["left", "right", "bottom", "top"];

    private static C3dPortResult Lumped(C3dPort port, string label, Complex z0, C3dPortContext ctx, double u0, double v0,
                                        double u1, double v1, int normalAxis, Point3 uDir, Point3 vDir, Point3 min, Point3 max,
                                        Func<double, double, Point3> P)
    {
        // The four edges, their ends pulled in by two DBU so a corner contact does not count.
        // brief-emb-2 R-emb2-3 — and by twice Robust meshing's largest move, which widens what counts as touching.
        double inset = 2 * ctx.Dbu + 2 * ctx.OutlineMoveM;
        (Point3, Point3) Edge(double ua, double va, double ub, double vb)
        {
            double du = ub - ua, dv = vb - va, len = Math.Sqrt(du * du + dv * dv);
            double f = len > 2 * inset ? inset / len : 0.5;
            return (P(ua + du * f, va + dv * f), P(ub - du * f, vb - dv * f));
        }
        var edges = new[] { Edge(u0, v0, u0, v1), Edge(u1, v0, u1, v1), Edge(u0, v0, u1, v0), Edge(u0, v1, u1, v1) };

        string negative, positive, reason;
        int pair;                                   // 0: left/right (along u), 1: bottom/top (along v)
        bool negativeOnLow;
        IReadOnlyList<(string, IReadOnlyList<string>)> contacts = [];

        if (port.Positive is { } sp && port.Negative is { } sn)
        {
            // Both stated: inference is not consulted. Only the two named objects are measured, to find the pair they sit on.
            var pc = ctx.Conductors.FirstOrDefault(c => c.Name == sp);
            var nc = ctx.Conductors.FirstOrDefault(c => c.Name == sn);
            if (pc is null || nc is null)
                return Refuse(port, $"{label} names '{(pc is null ? sp : sn)}' as its {(pc is null ? "Positive" : "Negative")} end, " +
                                    "which is no conductor of the elaborated model" +
                                    (ctx.Box is null ? "" : " and no PEC face of the setup's air box") + ".");
            // Each candidate edge is measured at its MIDPOINT and without the contact query's 2-DBU box cut-off. With the
            // cut-off, a sheet stopped short of its metal (which is what stating both ends is for) scored every pair
            // infinite and silently kept pair 0 — the u axis — so a gap running along v was driven across its width.
            // At the nearest point instead, a side edge's end is as close to each conductor as the true end edge is.
            static (Point3, Point3) Mid((Point3 A, Point3 B) e)
            {
                var m = new Point3((e.A.X + e.B.X) / 2, (e.A.Y + e.B.Y) / 2, (e.A.Z + e.B.Z) / 2);
                return (m, m);
            }
            var mids = edges.Select(Mid).ToArray();
            double[] dn = [.. mids.Select(m => ctx.Distance(nc, m.Item1, m.Item2, bounded: false))];
            double[] dpos = [.. mids.Select(m => ctx.Distance(pc, m.Item1, m.Item2, bounded: false))];
            if (StatedPair(dn, dpos, ctx.Dbu) is not { } chosen)
                return Refuse(port, StatedUndecided(label, sp, sn, dn, dpos, EdgeNames));
            (pair, negativeOnLow) = chosen;
            (negative, positive) = (sn, sp);
            reason = "both ends are stated";
        }
        else
        {
            ctx.InferenceRuns++;
            // brief-emb-2 R-emb2-3 — measured against the outline the run meshes: an edge that touched the artwork as drawn is
            // within Robust meshing's largest move of it, and follows it (below).
            double tol = ctx.Dbu + ctx.OutlineMoveM;
            var touching = edges.Select(e => ctx.Conductors.Where(c => ctx.Distance(c, e.Item1, e.Item2) <= tol)
                                                           .Select(c => c.Name).ToList()).ToArray();
            contacts = [.. EdgeNames.Select((n, i) => (n, (IReadOnlyList<string>)touching[i]))];
            var valid = new List<int>();
            for (int k = 0; k < 2; k++)
                if (touching[2 * k].Count == 1 && touching[2 * k + 1].Count == 1 && touching[2 * k][0] != touching[2 * k + 1][0])
                    valid.Add(k);
            if (valid.Count != 1)
                return Refuse(port, ContactRefusal(label, touching, valid.Count), contacts);
            pair = valid[0];
            string low = touching[2 * pair][0], high = touching[2 * pair + 1][0];
            var lc = ctx.Conductors.First(c => c.Name == low);
            var hc = ctx.Conductors.First(c => c.Name == high);
            (negative, reason) = Negative(lc, hc, ctx);
            negativeOnLow = negative == low;
            positive = negativeOnLow ? high : low;
            if (ctx.OutlineMoveM > 0 && FollowOutline(ctx, pair, lc, hc, ref u0, ref v0, ref u1, ref v1, P, Edge) is { } moved)
            {
                reason += "; " + moved;
                var cs = new[] { P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1) };
                min = new Point3(cs.Min(c => c.X), cs.Min(c => c.Y), cs.Min(c => c.Z));
                max = new Point3(cs.Max(c => c.X), cs.Max(c => c.Y), cs.Max(c => c.Z));
            }
        }
        if (port.Flip)
        {
            (negative, positive) = (positive, negative);
            negativeOnLow = !negativeOnLow;
            reason += "; Flip swaps them";
        }

        var axis = pair == 0 ? uDir : vDir;
        var direction = negativeOnLow ? axis : new Point3(-axis.X, -axis.Y, -axis.Z);
        var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var normal = Into(normalAxis, centre, ctx);
        var resolved = new Em3dPort(port.Number, ProblemName(port.Number), positive, negative, min, max, direction, z0,
                                    new Em3dReferencePlane(centre, normal, 0));
        return new C3dPortResult(port, resolved, null, contacts, reason);
    }

    /// <summary>
    /// brief-emb-2 R-emb2-3 — the two end edges of an inferred lumped port moved onto the outline Robust meshing left: an edge
    /// short of its conductor moves out to it, one reaching into it moves back to its face. Each move is at most the
    /// elaboration's largest outline move; the sentence says what moved, or null when neither edge had to.
    /// </summary>
    private static string? FollowOutline(C3dPortContext ctx, int pair, C3dPortContext.Conductor low, C3dPortContext.Conductor high,
                                         ref double u0, ref double v0, ref double u1, ref double v1, Func<double, double, Point3> P,
                                         Func<double, double, double, double, (Point3, Point3)> edge)
    {
        double a0 = pair == 0 ? u0 : v0, a1 = pair == 0 ? u1 : v1;
        double b0 = pair == 0 ? v0 : u0, b1 = pair == 0 ? v1 : u1;
        (Point3, Point3) At(double a) => pair == 0 ? edge(a, b0, a, b1) : edge(b0, a, b1, a);
        double D(C3dPortContext.Conductor c, double a) { var (p, q) = At(a); return ctx.Distance(c, p, q, bounded: false); }
        double m = ctx.OutlineMoveM, tol = ctx.Dbu;
        // outward: −1 for the low edge, +1 for the high one.
        double Settle(C3dPortContext.Conductor c, double a, int outward)
        {
            double d = D(c, a);
            if (d > tol) return D(c, a + outward * d) <= tol ? a + outward * d : a;
            double inward = m - D(c, a - outward * m);      // how far the conductor reaches into the sheet
            return inward > tol && D(c, a - outward * inward) <= tol ? a - outward * inward : a;
        }
        double n0 = Settle(low, a0, -1), n1 = Settle(high, a1, +1);
        if (Math.Abs(n0 - a0) <= tol && Math.Abs(n1 - a1) <= tol) return null;
        string Moved(string name, double by) => $"its edge on '{name}' follows Robust meshing's outline by {Fmt(Math.Abs(by))} µm";
        var said = new List<string>();
        if (Math.Abs(n0 - a0) > tol) said.Add(Moved(low.Name, n0 - a0));
        if (Math.Abs(n1 - a1) > tol) said.Add(Moved(high.Name, n1 - a1));
        if (pair == 0) (u0, u1) = (n0, n1); else (v0, v1) = (n0, n1);
        return string.Join("; ", said);
    }

    // ── brief-sar-8b R-sar8b-1 — a lumped gap on a cylinder ────────────────────────────────────

    /// <summary>One edge of a gap on a cylinder: an axial line at azimuth <paramref name="At"/> (an α edge), or an arc at axial
    /// coordinate <paramref name="At"/> (an axial edge), from <paramref name="From"/> to <paramref name="To"/> along it.</summary>
    private readonly record struct CylEdge(bool Arc, double At, double From, double To);

    /// <summary>The cylinder edges' names, in the planar edges' order: the two α edges (the pair across the azimuth), then the two arcs.</summary>
    private static readonly string[] CylEdgeNames = ["start azimuth", "end azimuth", "lower arc", "upper arc"];

    /// <summary>
    /// A lumped port's rule on a patch of a cylinder: the patch's four edges — its two axial lines and its two arcs, their ends
    /// pulled in by two DBU — tested for contact against every conductor; exactly one pair of opposite edges each touching one
    /// conductor, the two different. The arcs' pair runs the current along the axis, the axial lines' pair round it. A sheet on
    /// the same cylinder is measured exactly; anything else against the edge as chords no more than a quarter DBU off it.
    /// </summary>
    private static C3dPortResult LumpedOnCylinder(C3dPort port, string label, Complex z0, C3dPortContext ctx, C3dCylinderRect rect, int dbuPerMicron)
    {
        var (patch, why) = C3dLowering.CylinderPatch(rect, C3dTransform.Identity, dbuPerMicron);
        if (patch is null) return Refuse(port, $"{label} {why}");
        if (patch.Problems(label) is { Count: > 0 } bad) return Refuse(port, string.Join(" ", bad));
        if (!(patch.Hi > patch.Lo)) return Refuse(port, $"{label}'s gap on a cylinder has no length along its axis.");
        double inset = 2 * ctx.Dbu, angInset = Math.Min(inset / patch.Radius, (patch.Alpha1 - patch.Alpha0) / 4);
        double sInset = Math.Min(inset, (patch.Hi - patch.Lo) / 4);
        var edges = new[]
        {
            new CylEdge(false, patch.Alpha0, patch.Lo + sInset, patch.Hi - sInset), new CylEdge(false, patch.Alpha1, patch.Lo + sInset, patch.Hi - sInset),
            new CylEdge(true, patch.Lo, patch.Alpha0 + angInset, patch.Alpha1 - angInset), new CylEdge(true, patch.Hi, patch.Alpha0 + angInset, patch.Alpha1 - angInset),
        };

        string negative, positive, reason;
        int pair;
        bool negativeOnLow;
        IReadOnlyList<(string, IReadOnlyList<string>)> contacts = [];
        if (port.Positive is { } sp && port.Negative is { } sn)
        {
            var pc = ctx.Conductors.FirstOrDefault(c => c.Name == sp);
            var nc = ctx.Conductors.FirstOrDefault(c => c.Name == sn);
            if (pc is null || nc is null)
                return Refuse(port, $"{label} names '{(pc is null ? sp : sn)}' as its {(pc is null ? "Positive" : "Negative")} end, " +
                                    "which is no conductor of the elaborated model" + (ctx.Box is null ? "" : " and no PEC face of the setup's air box") + ".");
            // brief-emb-3 R-emb3-1 — the planar rule (F5): each edge at its midpoint, with no box cut-off, and a refusal in place of
            // a default. Measured with the cut-off, a conductor stopped short of the gap scored every pair infinite and kept pair 0.
            var mids = edges.Select(Mid).ToArray();
            double[] dn = [.. mids.Select(m => CylDistance(ctx, nc, patch, m, bounded: false))];
            double[] dpos = [.. mids.Select(m => CylDistance(ctx, pc, patch, m, bounded: false))];
            if (StatedPair(dn, dpos, ctx.Dbu) is not { } chosen)
                return Refuse(port, StatedUndecided(label, sp, sn, dn, dpos, CylEdgeNames));
            (pair, negativeOnLow) = chosen;
            (negative, positive) = (sn, sp);
            reason = "both ends are stated";
        }
        else
        {
            ctx.InferenceRuns++;
            var touching = edges.Select(e => ctx.Conductors.Where(c => CylDistance(ctx, c, patch, e) <= ctx.Dbu).Select(c => c.Name).ToList()).ToArray();
            contacts = [.. CylEdgeNames.Select((n, i) => (n, (IReadOnlyList<string>)touching[i]))];
            var valid = new List<int>();
            for (int k = 0; k < 2; k++)
                if (touching[2 * k].Count == 1 && touching[2 * k + 1].Count == 1 && touching[2 * k][0] != touching[2 * k + 1][0])
                    valid.Add(k);
            if (valid.Count != 1)
            {
                string Names(List<string> n) => n.Count == 0 ? "nothing" : string.Join(" and ", n.Select(x => $"'{x}'"));
                return Refuse(port, $"{label} is a gap on a cylinder, and lies between two conductors only when one pair of its opposite edges each " +
                                    $"touches one: it touches {Names(touching[0])} at its start azimuth, {Names(touching[1])} at its end azimuth, " +
                                    $"{Names(touching[2])} on its lower arc and {Names(touching[3])} on its upper arc. Draw it so two opposite " +
                                    "edges lie on the metal, or state Positive and Negative.", contacts);
            }
            pair = valid[0];
            string low = touching[2 * pair][0], high = touching[2 * pair + 1][0];
            (negative, reason) = Negative(ctx.Conductors.First(c => c.Name == low), ctx.Conductors.First(c => c.Name == high), ctx);
            negativeOnLow = negative == low;
            positive = negativeOnLow ? high : low;
        }
        if (port.Flip)
        {
            (negative, positive) = (positive, negative);
            negativeOnLow = !negativeOnLow;
            reason += "; Flip swaps them";
        }

        var gap = new Em3dCylinderGap(patch, pair == 0 ? Em3dCylinderGapAxis.Azimuthal : Em3dCylinderGapAxis.Axial, negativeOnLow ? 1 : -1);
        var (x0, y0, z0b, x1, y1, z1) = patch.Bounds();
        var centre = patch.Centre;
        var outward = Geometry3.Sub(centre, patch.World((patch.Alpha0 + patch.Alpha1) / 2, (patch.Lo + patch.Hi) / 2, 0));
        double len = Math.Sqrt(Geometry3.Dot(outward, outward));
        var inward = new Point3(-outward.X / len, -outward.Y / len, -outward.Z / len);
        var resolved = new Em3dPort(port.Number, ProblemName(port.Number), positive, negative, new Point3(x0, y0, z0b), new Point3(x1, y1, z1),
                                    gap.Direction, z0, new Em3dReferencePlane(centre, inward, 0)) { Cylinder = gap };
        return new C3dPortResult(port, resolved, null, contacts, reason);
    }

    /// <summary>A cylinder edge's midpoint, as an edge of no length.</summary>
    private static CylEdge Mid(CylEdge e) => e with { From = (e.From + e.To) / 2, To = (e.From + e.To) / 2 };

    /// <summary>
    /// brief-emb-3 R-emb3-1 — which pair of opposite edges two STATED ends sit on, from each end's distance to each edge's midpoint
    /// (left, right, bottom, top; or a cylinder's start azimuth, end azimuth, lower arc, upper arc). The pair and orientation with
    /// the least summed distance, provided that pair is nearer than the other by more than <paramref name="tol"/> and each end is
    /// nearer its own edge than the opposite one; null otherwise, so the direction is undecided rather than defaulted.
    /// </summary>
    internal static (int Pair, bool NegativeOnLow)? StatedPair(double[] dn, double[] dp, double tol)
    {
        double[] best = [double.PositiveInfinity, double.PositiveInfinity];
        bool[] lowOf = [true, true];
        for (int k = 0; k < 2; k++)
            foreach (bool low in new[] { true, false })
            {
                double d = dn[2 * k + (low ? 0 : 1)] + dp[2 * k + (low ? 1 : 0)];
                if (d < best[k]) (best[k], lowOf[k]) = (d, low);
            }
        int pair = best[0] <= best[1] ? 0 : 1;
        if (double.IsInfinity(best[pair]) || !(best[pair] + tol < best[1 - pair])) return null;
        int ne = 2 * pair + (lowOf[pair] ? 0 : 1), pe = 2 * pair + (lowOf[pair] ? 1 : 0);
        if (!(dn[ne] + tol < dn[pe]) || !(dp[pe] + tol < dp[ne])) return null;
        return (pair, lowOf[pair]);
    }

    /// <summary>R-emb3-1 — the refusal when <see cref="StatedPair"/> cannot decide: the port, both ends, and how far each is from
    /// each edge.</summary>
    private static string StatedUndecided(string label, string sp, string sn, double[] dn, double[] dp, string[] edgeNames)
    {
        if (dn.Concat(dp).Any(double.IsInfinity))
            return $"{label} states '{sp}' and '{sn}' as its ends, and neither pair of its opposite edges can be measured against " +
                   "them, so its direction is undecided.";
        string Row(string who, double[] d)
            => $"'{who}' is " + string.Join(", ", d.Select((x, i) => $"{(x * 1e6).ToString("0.###", CultureInfo.InvariantCulture)} µm from its {edgeNames[i]}"));
        return $"{label} states '{sp}' and '{sn}' as its ends, and they do not face two opposite edges of it ({Row(sp, dp)}; " +
               $"{Row(sn, dn)}), so its direction is undecided. Draw the port between them, one at each end of its gap, or " +
               "correct Positive and Negative.";
    }

    /// <summary>The distance from a gap's edge on <paramref name="patch"/>'s cylinder to conductor <paramref name="c"/>, metres.</summary>
    /// <param name="bounded">As <see cref="C3dPortContext.Distance"/>'s: false measures however far, for a stated end.</param>
    private static double CylDistance(C3dPortContext ctx, C3dPortContext.Conductor c, Em3dCylinderPatch patch, CylEdge e, bool bounded = true)
    {
        if (c.Patch is { } q)
        {
            if (q.Axis != patch.Axis || Math.Abs(C3dPortContext.Get(q.Origin, q.U) - C3dPortContext.Get(patch.Origin, patch.U)) > ctx.Dbu ||
                Math.Abs(C3dPortContext.Get(q.Origin, q.V) - C3dPortContext.Get(patch.Origin, patch.V)) > ctx.Dbu)
                return Chords(ctx, c, patch, e, bounded);
            double radial = Math.Abs(q.Radius - patch.Radius);
            double OutsideAxial(double lo, double hi) => Math.Max(0, Math.Max(q.Lo - lo, hi - q.Hi));
            double OutsideAngle(double lo, double hi)
            {
                if (q.FullCircle) return 0;
                // The edge's angular range moved into the patch's turn, then how far either end falls outside it, as an arc.
                double shift = 2 * Math.PI * Math.Floor((lo - q.Alpha0) / (2 * Math.PI));
                double a0 = lo - shift, a1 = hi - shift;
                return Math.Max(0, Math.Max(q.Alpha0 - a0, a1 - q.Alpha1)) * q.Radius;
            }
            return e.Arc ? radial + OutsideAxial(e.At, e.At) + OutsideAngle(e.From, e.To)
                         : radial + OutsideAxial(e.From, e.To) + OutsideAngle(e.At, e.At);
        }
        return Chords(ctx, c, patch, e, bounded);
    }

    /// <summary>The edge as straight chords (one for an axial line; for an arc, enough that none strays a quarter DBU off it).</summary>
    private static double Chords(C3dPortContext ctx, C3dPortContext.Conductor c, Em3dCylinderPatch patch, CylEdge e, bool bounded = true)
    {
        if (!e.Arc) return ctx.Distance(c, patch.World(e.At, e.From), patch.World(e.At, e.To), bounded);
        double step = Math.Sqrt(8 * 0.25 * ctx.Dbu / patch.Radius);
        int n = Math.Clamp((int)Math.Ceiling((e.To - e.From) / step), 1, 4096);
        double best = double.PositiveInfinity;
        for (int i = 0; i < n && best > 0; i++)
            best = Math.Min(best, ctx.Distance(c, patch.World(e.From + (e.To - e.From) * i / n, e.At), patch.World(e.From + (e.To - e.From) * (i + 1) / n, e.At), bounded));
        return best;
    }

    /// <summary>R-em3d49-2b — the refusal, naming what each edge touches.</summary>
    private static string ContactRefusal(string label, List<string>[] touching, int validPairs)
    {
        string Names(List<string> n) => n.Count == 0 ? "nothing" : string.Join(" and ", n.Select(x => $"'{x}'"));
        const string Rule = "A lumped port lies between two conductors, one on each of two opposite edges.";
        if (validPairs > 1)
            return $"{label} touches conductors on all four edges ({Names(touching[0])} left, {Names(touching[1])} right, " +
                   $"{Names(touching[2])} bottom, {Names(touching[3])} top), so which pair it is between is not decided by " +
                   "what it touches; state Positive and Negative.";
        for (int e = 0; e < 4; e++)
            if (touching[e].Count > 1)
                return $"{label}'s {EdgeNames[e]} edge touches both {Names(touching[e])}; an end of a port is one conductor. " +
                       $"{Rule} Shrink the port, or state Positive and Negative.";
        for (int k = 0; k < 2; k++)
            if (touching[2 * k].Count == 1 && touching[2 * k + 1].Count == 1)
                return $"{label} touches {Names(touching[2 * k])} on both its {EdgeNames[2 * k]} and {EdgeNames[2 * k + 1]} edges, so " +
                       $"both of its ends are one conductor. {Rule}";
        // The pair with the most contact says the most about what is missing.
        int best = touching[0].Count + touching[1].Count >= touching[2].Count + touching[3].Count ? 0 : 1;
        var (a, b) = (touching[2 * best], touching[2 * best + 1]);
        if (a.Count + b.Count == 0)
            return $"{label} touches no conductor on any edge. {Rule} Draw it so two opposite edges lie on the metal.";
        var (lo, hi) = a.Count >= b.Count ? (2 * best, 2 * best + 1) : (2 * best + 1, 2 * best);
        return $"{label} touches {Names(touching[lo])} on its {EdgeNames[lo]} edge and nothing on its {EdgeNames[hi]}. {Rule}";
    }

    /// <summary>R-em3d49-2b — which of two ends is negative, and why.</summary>
    private static (string Negative, string Reason) Negative(C3dPortContext.Conductor a, C3dPortContext.Conductor b, C3dPortContext ctx)
    {
        bool ga = ctx.Ground.Contains(a.Name), gb = ctx.Ground.Contains(b.Name);
        if (ga != gb)
        {
            var g = ga ? a : b;
            return (g.Name, $"'{g.Name}' is in the ground set" +
                            (g.Axis >= 0 ? " (a PEC face of the air box)" : ""));
        }
        var (big, small) = a.Area >= b.Area ? (a, b) : (b, a);
        // Both in the larger one's unit, so the two numbers compare at a glance.
        bool mm = big.Area >= 1e-6;
        string Area(double m2) => (mm ? m2 * 1e6 : m2 * 1e12).ToString("0.####", CultureInfo.InvariantCulture) + (mm ? " mm²" : " µm²");
        string why = ga ? "both are in the ground set, and " : "neither is in the ground set, and ";
        return (big.Name, a.Area == b.Area
            ? why + $"their surfaces are equal ({Area(a.Area)}), so the one on the lower edge is negative"
            : why + $"'{big.Name}' has the larger surface ({Area(big.Area)} against {Area(small.Area)})");
    }

    // ── wave ─────────────────────────────────────────────────────────────────────────────────────

    private static C3dPortResult Wave(C3dPort port, string label, Complex z0, C3dPortContext ctx, double u0, double v0,
                                      double u1, double v1, double h, int normalAxis, Point3 uDir, Point3 vDir, Point3 min,
                                      Point3 max, Func<double, double, Point3> P)
    {
        if (ctx.Box is not { } box)
            return Refuse(port, $"{label} is a wave port, and a wave port lies on a face of the setup's air box; with no setup " +
                                "there is no box.");
        string? face = null;
        foreach (bool high in new[] { false, true })
        {
            double at = C3dPortContext.Get(high ? box.Max : box.Min, normalAxis);
            if (Math.Abs(at - h) <= ctx.Dbu) face = "xyz"[normalAxis] + (high ? "max" : "min");
        }
        if (face is null)
        {
            string lo = Fmt(C3dPortContext.Get(box.Min, normalAxis)), hi = Fmt(C3dPortContext.Get(box.Max, normalAxis));
            return Refuse(port, $"{label} is a wave port at {"xyz"[normalAxis]} = {Fmt(h)} µm, which is on neither of the air box's " +
                                $"{"xyz"[normalAxis]} faces ({lo} and {hi} µm) for this setup. A wave port is a region of the box's " +
                                "face; the box is each setup's own, so draw the port on its face, or make it lumped.");
        }

        // The two conductors: whatever meets the rectangle's region of the face.
        string negative, positive, reason;
        string? note = null;
        IReadOnlyList<(string, IReadOnlyList<string>)> contacts = [];
        var feet = Feet(ctx, normalAxis, h, uDir, vDir, u0, v0, u1, v1);
        if (port.Terminals is { Count: > 0 } terminals)
            return WaveTerminals(port, label, terminals, ctx, feet, face, h, normalAxis, uDir, vDir, u0, v0, u1, v1, min, max, P);

        if (port.Positive is { } sp && port.Negative is { } sn)
        {
            (negative, positive, reason) = (sn, sp, "both ends are stated");
            if (port.VoltagePath is null && (!feet.ContainsKey(sn) || !feet.ContainsKey(sp)))
                return Refuse(port, $"{label} names '{(feet.ContainsKey(sn) ? sp : sn)}', which does not meet its region of the " +
                                    $"{face} face, so no voltage path can be inferred between its ends; state its VoltagePath.");
        }
        else
        {
            ctx.InferenceRuns++;
            var found = feet.Keys.ToList();
            contacts = [("region", (IReadOnlyList<string>)found)];
            if (found.Count > 2 && found.Count(n => !n.StartsWith("airbox/", StringComparison.Ordinal)) == 2)
            {
                // brief-em3d-121 D1 — the stored reading stands (the reader never invents terminals); the gesture's is named
                var drawn = found.Where(n => !IsBoxFace(n)).ToList();
                if (BoxReferenced(drawn, found, feet, ctx, normalAxis, h, uDir, vDir))
                    note = $"{label} meets '{drawn[0]}', '{drawn[1]}' and the air box's PEC faces on the {face} face; it is read as one " +
                           "port between the two strips. Make Port ▸ Wave on that face writes a terminal per strip, referenced to the box.";
                found = drawn;
            }
            if (found.Count(n => !IsBoxFace(n)) > 2) found = [.. found.Where(n => !IsBoxFace(n))];
            if (found.Count > 2)
                return Refuse(port, ManyConductorsRefusal(label, face, found, ctx), contacts);
            if (found.Count != 2)
                return Refuse(port, found.Count == 0
                    ? $"{label} is a wave port on the air box's {face} face, and no conductor meets its region: a line must run to " +
                      "the face for its mode to be excited there."
                    : $"{label} is a wave port on the air box's {face} face, and only " +
                      $"{string.Join(", ", found.Select(n => $"'{n}'"))} meets its region; a wave port's " +
                      "mode runs between two conductors. ", contacts);
            var a = ctx.Conductors.First(c => c.Name == found[0]);
            var b = ctx.Conductors.First(c => c.Name == found[1]);
            (negative, reason) = Negative(a, b, ctx);
            positive = negative == a.Name ? b.Name : a.Name;
        }
        if (port.Flip)
        {
            (negative, positive) = (positive, negative);
            reason += "; Flip swaps them";
        }

        Point3 from, to;
        if (port.VoltagePath is { } vp)
        {
            double M(long v) => C3dLowering.Metres(v, ctx.DbuPerMicron);
            from = P(M(vp.From.U), M(vp.From.V));
            to = P(M(vp.To.U), M(vp.To.V));
        }
        else if (Path(feet[negative], feet[positive]) is var (fu, fv, tu, tv))
        {
            from = P(fu, fv);
            to = P(tu, tv);
        }
        // R-em3d121-1 — a coax: the outer's section encloses the inner's foot with clearance, so the path runs along a ray from
        // the outer to the inner, as a terminal's does (R-em3d114-1e). Flipped, the same ray runs the other way.
        else if (EnclosedRay(negative, positive, ctx, feet, normalAxis, h, uDir, vDir, u0, v0, u1, v1) is var (ou, ov, iu, iv, outer, inner))
        {
            (from, to) = outer == negative ? (P(ou, ov), P(iu, iv)) : (P(iu, iv), P(ou, ov));
            reason += $"; the path runs from '{negative}' to '{positive}' along a ray, because '{outer}' encloses '{inner}' on the face";
        }
        else
            return Refuse(port, $"{label}'s two conductors '{negative}' and '{positive}' overlap across its region of the {face} face, " +
                                "so no straight path runs between them; state its VoltagePath.", contacts);

        var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var inward = new Point3(normalAxis == 0 ? (face.EndsWith("min") ? 1 : -1) : 0, normalAxis == 1 ? (face.EndsWith("min") ? 1 : -1) : 0,
                                normalAxis == 2 ? (face.EndsWith("min") ? 1 : -1) : 0);
        var d = Geometry3.Sub(to, from);
        double len = Math.Sqrt(Geometry3.Dot(d, d));
        var resolved = new Em3dPort(port.Number, ProblemName(port.Number), positive, negative, min, max,
                                    len > 0 ? new Point3(d.X / len, d.Y / len, d.Z / len) : uDir, z0,
                                    new Em3dReferencePlane(centre, inward, 0))
        {
            Kind = Em3dPortKind.Wave,
            VoltagePath = new Em3dSegment(from, to),
        };
        return new C3dPortResult(port, resolved, null, contacts, reason) { Note = note };
    }

    /// <summary>
    /// R-em3d121-1 — the ray path between a single wave port's two ends when one's section encloses the other's foot with
    /// clearance (a coax): from the outer's section to the inner's foot, whichever end is the outer. Null when neither
    /// encloses the other, or when no ray meets the outer inside the rectangle: two conductors that overlap on the face
    /// without one enclosing the other are shorted or side by side, and a ray between them is no voltage path.
    /// </summary>
    private static (double OU, double OV, double IU, double IV, string Outer, string Inner)? EnclosedRay(
        string a, string b, C3dPortContext ctx, Dictionary<string, (double U0, double V0, double U1, double V1)> feet, int axis, double h,
        Point3 uDir, Point3 vDir, double u0, double v0, double u1, double v1)
    {
        foreach (var (outer, inner) in new[] { (a, b), (b, a) })
        {
            var o = ctx.Conductors.First(c => c.Name == outer);
            if (!Encloses(o, feet[inner], ctx, axis, h, uDir, vDir)) continue;
            if (RayPath(o, ctx, axis, h, uDir, vDir, feet[inner], u0, v0, u1, v1, longest: true) is var (fu, fv, tu, tv))
                return (fu, fv, tu, tv, outer, inner);
        }
        return null;
    }

    /// <summary>
    /// R-em3d121-1 — whether conductor <paramref name="outer"/>'s section by the plane encloses the foot <paramref name="p"/>
    /// with clearance: no segment of the section meets the foot's box (1 DBU of slack), and the section lies on all four
    /// sides of it — straight along +u, −u, +v and −v from its centre. A PEC box face (a line) encloses nothing.
    /// </summary>
    internal static bool Encloses(C3dPortContext.Conductor outer, (double U0, double V0, double U1, double V1) p, C3dPortContext ctx,
                                  int axis, double h, Point3 uDir, Point3 vDir)
    {
        if (outer.Axis >= 0) return false;
        double tol = ctx.Dbu;
        var segs = Section(outer, ctx, axis, h, uDir, vDir);
        double cu = (p.U0 + p.U1) / 2, cv = (p.V0 + p.V1) / 2;
        bool below = false, above = false, left = false, right = false;
        foreach (var (s, e) in segs)
        {
            if (SegmentMeetsBox(s, e, (p.U0 - tol, p.V0 - tol, p.U1 + tol, p.V1 + tol))) return false;
            foreach (double v in Crossings(s.U, s.V, e.U, e.V, cu, tol)) { below |= v < p.V0; above |= v > p.V1; }
            foreach (double u in Crossings(s.V, s.U, e.V, e.U, cv, tol)) { left |= u < p.U0; right |= u > p.U1; }
        }
        return below && above && left && right;
    }

    /// <summary>True when segment s–e meets the box (Liang–Barsky).</summary>
    private static bool SegmentMeetsBox((double U, double V) s, (double U, double V) e, (double U0, double V0, double U1, double V1) b)
    {
        double t0 = 0, t1 = 1, du = e.U - s.U, dv = e.V - s.V;
        foreach (var (q, r) in new[] { (-du, s.U - b.U0), (du, b.U1 - s.U), (-dv, s.V - b.V0), (dv, b.V1 - s.V) })
        {
            if (q == 0) { if (r < 0) return false; continue; }
            double t = r / q;
            if (q < 0) { if (t > t1) return false; t0 = Math.Max(t0, t); }
            else { if (t < t0) return false; t1 = Math.Min(t1, t); }
        }
        return true;
    }

    /// <summary>
    /// R-em3d121-2 — whether a wave port's region takes the box-referenced terminal reading: two or more drawn conductors meet
    /// it, none in the ground set, none touching a PEC box face that meets it (a ground drawn on the box's floor is joined to
    /// it, so the floor is not its reference), none's section enclosing another's foot (a coax keeps its two-conductor
    /// reading), and at least one PEC box face meets it.
    /// </summary>
    private static bool BoxReferenced(IReadOnlyList<string> drawn, IReadOnlyList<string> found,
                                      Dictionary<string, (double U0, double V0, double U1, double V1)> feet, C3dPortContext ctx, int axis,
                                      double h, Point3 uDir, Point3 vDir)
    {
        var faces = found.Where(IsBoxFace).ToList();
        if (drawn.Count < 2 || faces.Count == 0 || drawn.Any(ctx.Ground.Contains)) return false;
        double tol = ctx.Dbu;
        bool Touch((double U0, double V0, double U1, double V1) x, (double U0, double V0, double U1, double V1) y)
            => x.U0 <= y.U1 + tol && y.U0 <= x.U1 + tol && x.V0 <= y.V1 + tol && y.V0 <= x.V1 + tol;
        if (drawn.Any(n => faces.Any(f => Touch(feet[n], feet[f])))) return false;
        foreach (string outer in drawn)
        {
            var o = ctx.Conductors.First(c => c.Name == outer);
            if (drawn.Any(inner => inner != outer && Encloses(o, feet[inner], ctx, axis, h, uDir, vDir))) return false;
        }
        return true;
    }

    /// <summary>Every conductor meeting the rectangle's region of the plane at <paramref name="h"/>, with its (u, v) box.</summary>
    private static Dictionary<string, (double U0, double V0, double U1, double V1)> Feet(C3dPortContext ctx, int normalAxis, double h, Point3 uDir,
                                                                                      Point3 vDir, double u0, double v0, double u1, double v1)
    {
        var feet = new Dictionary<string, (double U0, double V0, double U1, double V1)>(StringComparer.Ordinal);
        foreach (var c in ctx.Conductors)
            if (Footprint(c, ctx, normalAxis, h, uDir, vDir, u0, v0, u1, v1) is { } fp) feet[c.Name] = fp;
        return feet;
    }

    private static bool IsBoxFace(string name) => name.StartsWith("airbox/", StringComparison.Ordinal);

    /// <summary>
    /// R-em3d114-1d — a wave port with no Terminals whose region meets three or more conductors: the reader never invents
    /// terminals (overview D4), so the refusal names what was found, the reference it would take, and the two fixes.
    /// </summary>
    private static string ManyConductorsRefusal(string label, string face, IReadOnlyList<string> found, C3dPortContext ctx)
    {
        var (reference, why, _) = InferReference(found, ctx);
        string names = string.Join(", ", found.Take(found.Count - 1).Select(n => $"'{n}'")) + $" and '{found[^1]}'";
        return $"{label} is a wave port on the air box's {face} face, and {found.Count} conductors meet its region: {names}. " +
               (reference is not null ? $"The reference would be '{reference}' ({why}), and each of the others a terminal. "
                                      : $"No reference can be inferred among them ({why}). ") +
               "Add Terminals to the port, one per signal conductor, or use Make Port ▸ Wave on that face, which writes them.";
    }

    /// <summary>
    /// R-em3d114-1c — the reference among <paramref name="candidates"/>: the one in the ground set (an air-box PEC face
    /// counts), else the largest surface — among the ground set's members when several are in it. A tie is no answer —
    /// among drawn conductors: when every ground-set candidate is a PEC face of the air box, they are one ground, named by
    /// the first in face order (xmin … zmax), brief-em3d-121.
    /// </summary>
    internal static (string? Reference, string Reason, string? Refusal) InferReference(IReadOnlyList<string> candidates, C3dPortContext ctx)
    {
        var cs = candidates.Select(n => ctx.Conductors.First(c => c.Name == n)).ToList();
        if (cs.Count == 0) return (null, "no conductor is left to be it", "no conductor besides the terminals meets the region");
        var ground = cs.Where(c => ctx.Ground.Contains(c.Name)).ToList();
        if (ground.Count == 1)
            return (ground[0].Name, $"'{ground[0].Name}' is the only one in the ground set" + (ground[0].Axis >= 0 ? " (a PEC face of the air box)" : ""), null);
        // R-em3d121-2 — the air box's PEC faces are one reference (openEMS grounds a line to every one of them), never a tie
        if (ground.Count > 1 && ground.All(c => c.Axis >= 0))
        {
            var first = ground.OrderBy(c => ctx.Conductors.IndexOf(c)).First();
            return (first.Name, $"the air box's PEC faces are one ground; '{first.Name}' names it", null);
        }
        var pool = (ground.Count > 1 ? ground : cs).OrderByDescending(c => c.Area).ToList();
        bool mm = pool[0].Area >= 1e-6;
        string Area(double m2) => (mm ? m2 * 1e6 : m2 * 1e12).ToString("0.####", CultureInfo.InvariantCulture) + (mm ? " mm²" : " µm²");
        string head = ground.Count > 1 ? "several are in the ground set, and " : "none is in the ground set, and ";
        if (pool.Count > 1 && Math.Abs(pool[0].Area - pool[1].Area) <= 1e-9 * pool[0].Area)
        {
            var tied = pool.Where(c => Math.Abs(c.Area - pool[0].Area) <= 1e-9 * pool[0].Area).Select(c => $"'{c.Name}'").ToList();
            string why = head + $"{string.Join(" and ", tied)} have equal surfaces ({Area(pool[0].Area)})";
            return (null, why, why + ", so which is the reference is not decided by the geometry; state Reference");
        }
        return (pool[0].Name, head + $"'{pool[0].Name}' has the largest surface ({Area(pool[0].Area)}" +
                              (pool.Count > 1 ? $" against {Area(pool[1].Area)}" : "") + ")", null);
    }

    /// <summary>
    /// R-em3d114-1c/-1e/-2a — a wave port with N terminals: each terminal's conductor meets the region and is not the
    /// reference, no other conductor is left on the face, and each terminal lowers to its own wave port sharing the
    /// rectangle, the reference and one <see cref="Em3dPort.FaceGroup"/>.
    /// </summary>
    private static C3dPortResult WaveTerminals(C3dPort port, string label, List<C3dTerminal> terminals, C3dPortContext ctx,
                                               Dictionary<string, (double U0, double V0, double U1, double V1)> feet, string face, double h,
                                               int normalAxis, Point3 uDir, Point3 vDir, double u0, double v0, double u1, double v1,
                                               Point3 min, Point3 max, Func<double, double, Point3> P)
    {
        ctx.InferenceRuns++;
        IReadOnlyList<(string, IReadOnlyList<string>)> contacts = [("region", (IReadOnlyList<string>)feet.Keys.ToList())];
        C3dPortResult No(string why) => Refuse(port, why, contacts);
        string Upper(string s) => char.ToUpperInvariant(s[0]) + s[1..];

        foreach (var t in terminals)
        {
            string who = TerminalLabel(port, t);
            if (!ctx.Conductors.Any(c => c.Name == t.Conductor))
                return No($"{Upper(who)} names '{t.Conductor}', which is no conductor of the elaborated model.");
            if (!feet.ContainsKey(t.Conductor))
                return No($"{Upper(who)} names '{t.Conductor}', which does not meet the port's region of the {face} face: a terminal " +
                          "is a conductor that runs to the face.");
        }
        var signal = terminals.Select(t => t.Conductor).ToHashSet(StringComparer.Ordinal);

        string reference, reason;
        if (port.Reference is { } stated)
        {
            if (!ctx.Conductors.Any(c => c.Name == stated))
                return No($"{label}'s Reference '{stated}' is no conductor of the elaborated model" +
                          (ctx.Box is null ? "." : " and no PEC face of the setup's air box."));
            (reference, reason) = (stated, "the reference is stated");
        }
        else
        {
            var candidates = feet.Keys.Where(n => !signal.Contains(n)).ToList();
            if (candidates.Count > 1 && candidates.Any(n => !IsBoxFace(n))) candidates = [.. candidates.Where(n => !IsBoxFace(n))];
            var (inferred, why, refusal) = InferReference(candidates, ctx);
            if (inferred is null)
                return No($"{label}'s reference cannot be inferred: {refusal}. State its Reference.");
            (reference, reason) = (inferred, why);
        }
        if (terminals.FirstOrDefault(t => t.Conductor == reference) is { } onReference)
            return No($"{Upper(TerminalLabel(port, onReference))} names '{reference}', which is the port's reference: a terminal's voltage " +
                      "is measured from the reference, so it is never one. Name another conductor, or another Reference.");
        if (feet.Keys.FirstOrDefault(n => !signal.Contains(n) && n != reference && !IsBoxFace(n)) is { } loose)
            return No($"'{loose}' also meets {label}'s region of the {face} face and is neither a terminal nor the reference " +
                      $"'{reference}': give it a terminal, or shrink the region.");

        double M(long v) => C3dLowering.Metres(v, ctx.DbuPerMicron);
        var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        double s = face.EndsWith("min", StringComparison.Ordinal) ? 1 : -1;
        var inward = new Point3(normalAxis == 0 ? s : 0, normalAxis == 1 ? s : 0, normalAxis == 2 ? s : 0);
        int group = terminals.Min(t => t.Number);
        var ports = new List<Em3dPort>(terminals.Count);
        foreach (var t in terminals)
        {
            Point3 from, to;
            if (t.VoltagePath is { } vp) (from, to) = (P(M(vp.From.U), M(vp.From.V)), P(M(vp.To.U), M(vp.To.V)));
            else if (!feet.ContainsKey(reference))
                return No($"{label}'s reference '{reference}' does not meet its region of the {face} face, so no voltage path can be " +
                          $"inferred for {TerminalLabel(port, t)}; state its VoltagePath.");
            else if ((Path(feet[reference], feet[t.Conductor]) ?? RayPath(ctx.Conductors.First(c => c.Name == reference), ctx, normalAxis, h,
                                                                           uDir, vDir, feet[t.Conductor], u0, v0, u1, v1)) is var (fu, fv, tu, tv))
                (from, to) = (P(fu, fv), P(tu, tv));
            else
                return No($"No straight path runs from {label}'s reference '{reference}' to {TerminalLabel(port, t)} ('{t.Conductor}') " +
                          $"inside its region of the {face} face; state the terminal's VoltagePath.");
            if (t.Flip) (from, to) = (to, from);
            TryParseZ0(t.Z0, out var z0);
            var d = Geometry3.Sub(to, from);
            double len = Math.Sqrt(Geometry3.Dot(d, d));
            ports.Add(new Em3dPort(t.Number, ProblemName(t.Number), t.Conductor, reference, min, max,
                                   len > 0 ? new Point3(d.X / len, d.Y / len, d.Z / len) : uDir, z0, new Em3dReferencePlane(centre, inward, 0))
            {
                Kind = Em3dPortKind.Wave,
                VoltagePath = new Em3dSegment(from, to),
                FaceGroup = group,
                FaceGroupLabel = label,
                SourceLabel = t.Name is { Length: > 0 } n ? n : null,
            });
        }
        return new C3dPortResult(port, ports[0], null, contacts, reason) { Terminals = ports };
    }

    /// <summary>
    /// R-em3d114-3a — what Make Port ▸ Wave writes on a face met by three or more conductors: the reference by the same rule
    /// a run infers it, and every other conductor in a stable order — its foot's centre along the rectangle's long axis, then
    /// its name. brief-em3d-121 R-em3d121-2: two or more drawn conductors between PEC box faces (none grounded, none enclosing
    /// another) are all terminals, the faces their reference. Null when the draft is not a wave port on a box face met by three
    /// or more (an ordinary port, resolved as one);
    /// <paramref name="refusal"/> says why such a face takes no terminals (a tie for the reference).
    /// </summary>
    public static (string Reference, string Reason, IReadOnlyList<string> Conductors)? TerminalsFor(C3dPort draft, int dbuPerMicron,
                                                                                                   C3dPortContext ctx, out string? refusal)
    {
        refusal = null;
        if (draft.Kind != Em3dPortKind.Wave || ctx.Box is not { } box || draft.Rect.Size.U <= 0 || draft.Rect.Size.V <= 0) return null;
        double M(long v) => C3dLowering.Metres(v, dbuPerMicron);
        double u0 = M(draft.Rect.Min.U), v0 = M(draft.Rect.Min.V);
        double u1 = M(draft.Rect.Min.U + draft.Rect.Size.U), v1 = M(draft.Rect.Min.V + draft.Rect.Size.V), h = M(draft.Offset);
        int normalAxis = draft.Plane switch { C3dPlane.YZ => 0, C3dPlane.XZ => 1, _ => 2 };
        if (Math.Abs(C3dPortContext.Get(box.Min, normalAxis) - h) > ctx.Dbu && Math.Abs(C3dPortContext.Get(box.Max, normalAxis) - h) > ctx.Dbu)
            return null;
        var (uDir, vDir) = PlaneAxes(draft.Plane);
        var feet = Feet(ctx, normalAxis, h, uDir, vDir, u0, v0, u1, v1);
        var found = feet.Keys.ToList();
        var drawn = found.Where(n => !IsBoxFace(n)).ToList();
        bool alongU = u1 - u0 >= v1 - v0;
        List<string> Ordered(IEnumerable<string> names)
            => [.. names.OrderBy(n => alongU ? (feet[n].U0 + feet[n].U1) / 2 : (feet[n].V0 + feet[n].V1) / 2).ThenBy(n => n, StringComparer.Ordinal)];
        // R-em3d121-2 — strips between the box's PEC faces (a stripline whose ground planes are the box): each drawn conductor
        // is a terminal, and the faces are the reference
        if (BoxReferenced(drawn, found, feet, ctx, normalAxis, h, uDir, vDir))
        {
            var (boxFace, boxWhy, _) = InferReference([.. found.Where(IsBoxFace)], ctx);
            return (boxFace!, boxWhy, Ordered(drawn));
        }
        if (found.Count > 2 && drawn.Count == 2) return null;      // the two-conductor rule's own reading
        if (drawn.Count > 2) found = drawn;
        if (found.Count < 3) return null;
        var (reference, why, tie) = InferReference(found, ctx);
        if (reference is null) { refusal = $"The face is met by {found.Count} conductors and {tie}."; return null; }
        return (reference, why, Ordered(found.Where(n => n != reference)));
    }

    /// <summary>
    /// R-em3d114-1e — the voltage path when the reference's foot encloses the terminal's (a stripline's joined grounds, a
    /// shield): from the terminal's foot, straight along u or v from its centre to the nearest point of the reference's own
    /// section, whichever is shortest; null when no such line meets it inside the rectangle. brief-em3d-121 — with
    /// <paramref name="longest"/>, whichever is longest: round an enclosed conductor (a coax) every ray meets the one wall,
    /// and a tessellated wall's corners lie on the drawn surface while its chords fall inside it, so the longest ray is the
    /// one that reaches the wall as drawn. Along each ray it is still the nearest crossing.
    /// </summary>
    private static (double FU, double FV, double TU, double TV)? RayPath(C3dPortContext.Conductor reference, C3dPortContext ctx, int axis, double h,
                                                                       Point3 uDir, Point3 vDir, (double U0, double V0, double U1, double V1) p,
                                                                       double u0, double v0, double u1, double v1, bool longest = false)
    {
        double tol = ctx.Dbu;
        var segs = Section(reference, ctx, axis, h, uDir, vDir);
        double cu = (p.U0 + p.U1) / 2, cv = (p.V0 + p.V1) / 2;
        (double, double, double, double)? best = null;
        double bestLen = double.PositiveInfinity;
        // each direction's nearest crossing (−v, +v, −u, +u), for the longest
        var nearest = new ((double, double, double, double) Path, double Len)?[4];
        void Try(int dir, double fu, double fv, double tu, double tv)
        {
            if (fu < u0 - tol || fu > u1 + tol || fv < v0 - tol || fv > v1 + tol) return;
            double len = Math.Abs(tu - fu) + Math.Abs(tv - fv);
            if (len <= tol) return;
            if (len < bestLen) (best, bestLen) = ((fu, fv, tu, tv), len);
            if (nearest[dir] is not { } n || len < n.Len) nearest[dir] = ((fu, fv, tu, tv), len);
        }
        foreach (var (a, b) in segs)
        {
            // the line u = cu: below the foot (−v) and above it (+v)
            foreach (double v in Crossings(a.U, a.V, b.U, b.V, cu, tol))
            {
                if (v <= p.V0 + tol) Try(0, cu, v, cu, p.V0);
                if (v >= p.V1 - tol) Try(1, cu, v, cu, p.V1);
            }
            // the line v = cv: left (−u) and right (+u)
            foreach (double u in Crossings(a.V, a.U, b.V, b.U, cv, tol))
            {
                if (u <= p.U0 + tol) Try(2, u, cv, p.U0, cv);
                if (u >= p.U1 - tol) Try(3, u, cv, p.U1, cv);
            }
        }
        if (!longest) return best;
        var ways = nearest.OfType<((double, double, double, double) Path, double Len)>().ToList();
        return ways.Count == 0 ? null : ways.MaxBy(w => w.Len).Path;
    }

    /// <summary>Where segment (x, y)a–b crosses the line x = <paramref name="at"/>, as y values (both ends when it lies along
    /// the line).</summary>
    private static IEnumerable<double> Crossings(double ax, double ay, double bx, double by, double at, double tol)
    {
        if (Math.Abs(ax - bx) <= tol)
        {
            if (Math.Abs(ax - at) <= tol) { yield return ay; yield return by; }
            yield break;
        }
        if (at < Math.Min(ax, bx) - tol || at > Math.Max(ax, bx) + tol) yield break;
        yield return ay + (Math.Clamp(at, Math.Min(ax, bx), Math.Max(ax, bx)) - ax) / (bx - ax) * (by - ay);
    }

    /// <summary>Conductor <paramref name="c"/>'s section by the plane at <paramref name="h"/>, as (u, v) segments: each
    /// triangle crossing the plane gives one, a triangle lying in it gives its edges, and a PEC face gives its line.</summary>
    private static List<((double U, double V) A, (double U, double V) B)> Section(C3dPortContext.Conductor c, C3dPortContext ctx, int axis, double h,
                                                                                Point3 uDir, Point3 vDir)
    {
        double tol = ctx.Dbu;
        var segs = new List<((double, double), (double, double))>();
        (double, double) UV(Point3 q) => (Geometry3.Dot(q, uDir), Geometry3.Dot(q, vDir));
        if (c.Axis >= 0)
        {
            if (c.Axis == axis) return [];
            var (x0, y0, z0, x1, y1, z1) = c.Box;
            double[] lo = [x0, y0, z0], hi = [x1, y1, z1];
            lo[axis] = hi[axis] = h;
            segs.Add((UV(new Point3(lo[0], lo[1], lo[2])), UV(new Point3(hi[0], hi[1], hi[2]))));
            return segs;
        }
        var m = c.Cylinder is { } cyl ? Em3dTessellation.Of(new Em3dSolid(c.Name, "", Em3dRole.Conductor, cyl, 0)) : c.Mesh!;
        foreach (var t in m.Triangles)
        {
            var tri = new[] { m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C] };
            var on = new List<Point3>(3);
            for (int i = 0; i < 3; i++)
            {
                var p = tri[i];
                var q = tri[(i + 1) % 3];
                double dp = C3dPortContext.Get(p, axis) - h, dq = C3dPortContext.Get(q, axis) - h;
                if (Math.Abs(dp) <= tol) on.Add(p);
                else if ((dp < -tol && dq > tol) || (dp > tol && dq < -tol))
                {
                    double f = dp / (dp - dq);
                    on.Add(new Point3(p.X + (q.X - p.X) * f, p.Y + (q.Y - p.Y) * f, p.Z + (q.Z - p.Z) * f));
                }
            }
            if (on.Count == 2) segs.Add((UV(on[0]), UV(on[1])));
            else if (on.Count == 3) for (int i = 0; i < 3; i++) segs.Add((UV(on[i]), UV(on[(i + 1) % 3])));
        }
        return segs;
    }

    /// <summary>
    /// Where conductor <paramref name="c"/> meets the plane at <paramref name="h"/> inside the rectangle, as a (u, v) box —
    /// or null. Its vertices on the plane and its edges crossing it, clipped to the rectangle (1 DBU of slack).
    /// </summary>
    private static (double U0, double V0, double U1, double V1)? Footprint(C3dPortContext.Conductor c, C3dPortContext ctx, int axis, double h,
                                                                        Point3 uDir, Point3 vDir, double u0, double v0, double u1, double v1)
    {
        double tol = ctx.Dbu;
        var pts = new List<Point3>();
        if (c.Axis >= 0)
        {
            if (c.Axis == axis) return null;       // the port's own face, or its opposite
            // A PEC face perpendicular to the port's: it meets the port's plane along a line.
            var (x0, y0, z0, x1, y1, z1) = c.Box;
            double[] lo = [x0, y0, z0], hi = [x1, y1, z1];
            lo[axis] = hi[axis] = h;
            pts.Add(new Point3(lo[0], lo[1], lo[2]));
            pts.Add(new Point3(hi[0], hi[1], hi[2]));
        }
        else if (c.Cylinder is { } cyl)
        {
            var mesh = Em3dTessellation.Of(new Em3dSolid(c.Name, "", Em3dRole.Conductor, cyl, 0));
            Collect(mesh);
        }
        else Collect(c.Mesh!);

        void Collect(Em3dTriangleMesh m)
        {
            foreach (var t in m.Triangles)
            {
                var tri = new[] { m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C] };
                for (int i = 0; i < 3; i++)
                {
                    var p = tri[i];
                    var q = tri[(i + 1) % 3];
                    double dp = C3dPortContext.Get(p, axis) - h, dq = C3dPortContext.Get(q, axis) - h;
                    if (Math.Abs(dp) <= tol) pts.Add(p);
                    else if ((dp < -tol && dq > tol) || (dp > tol && dq < -tol))
                    {
                        double s = dp / (dp - dq);
                        pts.Add(new Point3(p.X + (q.X - p.X) * s, p.Y + (q.Y - p.Y) * s, p.Z + (q.Z - p.Z) * s));
                    }
                }
            }
        }

        double fu0 = double.PositiveInfinity, fv0 = fu0, fu1 = double.NegativeInfinity, fv1 = fu1;
        foreach (var p in pts)
        {
            double u = Geometry3.Dot(p, uDir), v = Geometry3.Dot(p, vDir);
            if (u < u0 - tol || u > u1 + tol || v < v0 - tol || v > v1 + tol) continue;
            fu0 = Math.Min(fu0, Math.Clamp(u, u0, u1)); fu1 = Math.Max(fu1, Math.Clamp(u, u0, u1));
            fv0 = Math.Min(fv0, Math.Clamp(v, v0, v1)); fv1 = Math.Max(fv1, Math.Clamp(v, v0, v1));
        }
        return double.IsInfinity(fu0) ? null : (fu0, fv0, fu1, fv1);
    }

    /// <summary>The voltage path from the negative footprint to the positive one across the axis they are apart on, at the
    /// positive's centre on the other — or null when they overlap on both.</summary>
    private static (double FU, double FV, double TU, double TV)? Path((double U0, double V0, double U1, double V1) n,
                                                                      (double U0, double V0, double U1, double V1) p)
    {
        double gapV = Math.Max(p.V0 - n.V1, n.V0 - p.V1), gapU = Math.Max(p.U0 - n.U1, n.U0 - p.U1);
        if (gapV <= 0 && gapU <= 0) return null;
        if (gapV >= gapU)
        {
            double u = (p.U0 + p.U1) / 2;
            return p.V0 >= n.V1 ? (u, n.V1, u, p.V0) : (u, n.V0, u, p.V1);
        }
        double v = (p.V0 + p.V1) / 2;
        return p.U0 >= n.U1 ? (n.U1, v, p.U0, v) : (n.U0, v, p.U1, v);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A drawing plane's u and v directions in the world (C3dPlane's note).</summary>
    public static (Point3 U, Point3 V) PlaneAxes(C3dPlane plane) => plane switch
    {
        C3dPlane.YZ => (new Point3(0, 1, 0), new Point3(0, 0, 1)),
        C3dPlane.XZ => (new Point3(1, 0, 0), new Point3(0, 0, 1)),
        _           => (new Point3(1, 0, 0), new Point3(0, 1, 0)),
    };

    /// <summary>The unit normal of a lumped port's plane pointing toward the structure's centre (into the structure).</summary>
    private static Point3 Into(int axis, Point3 centre, C3dPortContext ctx)
    {
        double sign = C3dPortContext.Get(ctx.Centre, axis) >= C3dPortContext.Get(centre, axis) ? 1 : -1;
        return new Point3(axis == 0 ? sign : 0, axis == 1 ? sign : 0, axis == 2 ? sign : 0);
    }

    private static string Fmt(double m) => (m * 1e6).ToString("G6", CultureInfo.InvariantCulture);

    /// <summary>A port's Z0 as the <c>.cem</c> spells a complex impedance: <c>50</c>, <c>25+j10</c>, <c>25-10j</c>, <c>j5</c>.</summary>
    public static bool TryParseZ0(string? text, out Complex z)
    {
        z = default;
        string s = (text ?? "").Replace(" ", "").Replace("Ω", "").Replace("ohm", "", StringComparison.OrdinalIgnoreCase);
        if (s.Length == 0) return false;
        int j = s.IndexOfAny(['j', 'i']);
        if (j < 0) return Real(s, out double re) && Finish(re, 0, out z);
        int split = -1;
        for (int i = 1; i < s.Length; i++)
            if (s[i] is '+' or '-' && s[i - 1] is not ('e' or 'E')) split = i;
        string rePart = split < 0 ? "0" : s[..split];
        string imPart = (split < 0 ? s : s[split..]).Replace("j", "").Replace("i", "");
        if (imPart is "" or "+") imPart = "1";
        if (imPart is "-") imPart = "-1";
        return Real(rePart, out double r) && Real(imPart, out double im) && Finish(r, im, out z);

        static bool Real(string t, out double v) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && double.IsFinite(v);
        static bool Finish(double re, double im, out Complex c) { c = new Complex(re, im); return true; }
    }

    /// <summary>A Z0 written as a port record states it: <c>50</c>, or <c>25+j10</c>.</summary>
    public static string FormatZ0(Complex z)
    {
        string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        return z.Imaginary == 0 ? R(z.Real) : $"{R(z.Real)}{(z.Imaginary < 0 ? "-" : "+")}j{R(Math.Abs(z.Imaginary))}";
    }
}

/// <summary>Distances between 3D primitives, metres — the contact query's arithmetic (Ericson, Real-Time Collision
/// Detection, §5.1.5 and §5.1.9).</summary>
public static class Geometry3
{
    public static Point3 Sub(Point3 a, Point3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static double Dot(Point3 a, Point3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Point3 Cross(Point3 a, Point3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static Point3 Add(Point3 a, Point3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point3 Mul(Point3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    private static double Dist(Point3 a, Point3 b) { var d = Sub(a, b); return Math.Sqrt(Dot(d, d)); }

    /// <summary>The distance from segment pq to triangle abc: zero when they meet.</summary>
    public static double SegmentTriangle(Point3 p, Point3 q, Point3 a, Point3 b, Point3 c)
    {
        if (Crosses(p, q, a, b, c)) return 0;
        double d = Math.Min(Dist(p, ClosestOnTriangle(p, a, b, c)), Dist(q, ClosestOnTriangle(q, a, b, c)));
        d = Math.Min(d, SegmentSegment(p, q, a, b));
        d = Math.Min(d, SegmentSegment(p, q, b, c));
        return Math.Min(d, SegmentSegment(p, q, c, a));
    }

    /// <summary>True when segment pq passes through the triangle's interior.</summary>
    private static bool Crosses(Point3 p, Point3 q, Point3 a, Point3 b, Point3 c)
    {
        var n = Cross(Sub(b, a), Sub(c, a));
        double dp = Dot(Sub(p, a), n), dq = Dot(Sub(q, a), n);
        if (dp * dq > 0 || dp == dq) return false;
        var x = Add(p, Mul(Sub(q, p), dp / (dp - dq)));
        var cp = ClosestOnTriangle(x, a, b, c);
        return Dist(cp, x) <= 1e-15 * Math.Max(1, Math.Sqrt(Dot(n, n)));
    }

    internal static Point3 ClosestOnTriangle(Point3 p, Point3 a, Point3 b, Point3 c)
    {
        var ab = Sub(b, a); var ac = Sub(c, a); var ap = Sub(p, a);
        double d1 = Dot(ab, ap), d2 = Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = Sub(p, b);
        double d3 = Dot(ab, bp), d4 = Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        double vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return Add(a, Mul(ab, d1 / (d1 - d3)));
        var cp = Sub(p, c);
        double d5 = Dot(ab, cp), d6 = Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        double vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return Add(a, Mul(ac, d2 / (d2 - d6)));
        double va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return Add(b, Mul(Sub(c, b), (d4 - d3) / (d4 - d3 + (d5 - d6))));
        double den = 1 / (va + vb + vc);
        return Add(a, Add(Mul(ab, vb * den), Mul(ac, vc * den)));
    }

    /// <summary>The distance between segments p1q1 and p2q2.</summary>
    public static double SegmentSegment(Point3 p1, Point3 q1, Point3 p2, Point3 q2)
    {
        var d1 = Sub(q1, p1); var d2 = Sub(q2, p2); var r = Sub(p1, p2);
        double a = Dot(d1, d1), e = Dot(d2, d2), f = Dot(d2, r);
        double s, t;
        if (a <= 0 && e <= 0) return Dist(p1, p2);
        if (a <= 0) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            double c = Dot(d1, r);
            if (e <= 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                double b = Dot(d1, d2), den = a * e - b * b;
                s = den != 0 ? Math.Clamp((b * f - c * e) / den, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        return Dist(Add(p1, Mul(d1, s)), Add(p2, Mul(d2, t)));
    }
}
