// brief-emb-3 R-emb3-2 / R-emb3-3 — a lumped port's direction CHECKED, not only printed.
//
// The F5 defect sent four of a board's fifteen ports to the solver driven across their width, and the only thing that
// showed it was the 3D view's arrow. Headless nothing did: check printed the direction it had chosen, which is the
// direction that was wrong. So each lumped port is measured a second way, independently of how its ends were chosen:
//
//   * its gap: the direction beside the gap's length and width, so a sentence that reads "along +x, 25 µm long, 600 µm
//     wide" for a pad pair that faces along y is visibly wrong;
//   * its conductors: the line joining the nearest points of its two conductors to the port's centre. A direction more
//     than 45° off that line is a warning, naming both axes;
//   * a sheet with both ends stated: which pair of its opposite edges faces conductors at all (each edge's midpoint against
//     every conductor, however far). A chosen pair that is not the facing one is the same warning;
//   * stated ends against inference: when what the port touches would also decide it, the two must choose the same pair of
//     edges — a disagreement is an ERROR (a mistyped conductor on a well-drawn port otherwise silently wins). The same pair
//     with different conductors is a warning.
//
// Nothing here changes what a run builds: the run's resolution is C3dPorts', and this reads it.

using CircuitRF.Diagnostics;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.ThreeD;

/// <summary>brief-emb-3 — what <c>check</c> and <c>explain</c> say of each lumped port's direction, beyond its resolution.</summary>
public static class C3dPortChecks
{
    /// <summary>The finding ids, so a caller can filter on them.</summary>
    public const string GapId = "c3d.port.gap", DirectionId = "c3d.port.direction",
                        DisagreesId = "c3d.port.stated-disagrees", OtherEndsId = "c3d.port.stated-other-ends";

    /// <summary>Every lumped port's gap sentence (info) and each direction finding, under every readable non-thermal embedded
    /// setup as <see cref="C3dPortReports.For"/> resolves them; identical sentences from two setups are said once.</summary>
    public static IReadOnlyList<Diagnostic> For(C3dDocument doc, C3dElaboration e)
    {
        var found = new List<Diagnostic>();
        if (!doc.Ports.Any(p => p.Kind == Em3dPortKind.Lumped && p.Terminals is not { Count: > 0 }) || !e.Ok) return found;
        var setups = C3dSetups.Read(doc).Where(s => s.Setup is { IsThermal: false }).ToList();
        List<(string? Setup, C3dPortContext Ctx)> contexts = setups.Count == 0
            ? [(null, C3dProblemAssembly.PortContext(null, doc, e, null))]
            : [.. setups.Select(s => ((string?)s.Name, C3dProblemAssembly.PortContext(s.Setup, doc, e, C3dProblemAssembly.AirBox(s.Setup!, e, out _))))];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (setup, ctx) in contexts)
            foreach (var r in C3dPorts.Resolve(doc, ctx))
                foreach (var (id, severity, text) in Port(doc, r, ctx))
                    if (seen.Add(id + text))
                        found.Add(Diagnostic.Create(id, severity, "{why}",
                                                    // the gap is the drawing's: said once, under no setup
                                                    ("why", (setup is not null && contexts.Count > 1 && id != GapId ? $"setup '{setup}': " : "") + text),
                                                    ("port", r.Label)));
        return found;
    }

    /// <summary>One resolved port's findings: none for a refused, wave or not-modelled port.</summary>
    private static IEnumerable<(string Id, DiagnosticSeverity Severity, string Text)> Port(C3dDocument doc, C3dPortResult r, C3dPortContext ctx)
    {
        if (r.Resolved is not { Kind: Em3dPortKind.Lumped } p || r.Terminals is not null || !r.Port.Model) yield break;
        var port = r.Port;
        string label = r.Label;
        string L(double m) => C3dUnits.Spell(m, doc.DisplayUnit);
        bool stated = port.Positive is not null && port.Negative is not null;

        if (p.Cylinder is { } gap)
        {
            var patch = gap.Patch;
            double axial = patch.Hi - patch.Lo, round = patch.Radius * (patch.Alpha1 - patch.Alpha0);
            bool alongAxis = gap.Along == Em3dCylinderGapAxis.Axial;
            yield return (GapId, DiagnosticSeverity.Info,
                $"{label} runs {(alongAxis ? "along its cylinder's axis" : "round its cylinder")} across a {L(alongAxis ? axial : round)} gap, " +
                $"{L(alongAxis ? round : axial)} wide.");
            if (stated && Agreement(port, r, ctx, doc.DbuPerMicron, alongAxis ? 1 : 0, p, cylinder: true) is { } a) yield return a;
            yield break;
        }

        var (uDir, vDir) = C3dTurnedPorts.Axes(port);
        int pair = Math.Abs(Geometry3.Dot(p.Direction, uDir)) >= 0.5 ? 0 : 1;
        double M(long v) => C3dLowering.Metres(v, doc.DbuPerMicron);
        double du = M(port.Rect.Size.U), dv = M(port.Rect.Size.V);
        string axis = Along(p.Direction);
        yield return (GapId, DiagnosticSeverity.Info,
            $"{label} runs along {axis} across a {L(pair == 0 ? du : dv)} gap, {L(pair == 0 ? dv : du)} wide.");

        // The conductors' own line, at the port.
        var centre = new Point3((p.Min.X + p.Max.X) / 2, (p.Min.Y + p.Max.Y) / 2, (p.Min.Z + p.Max.Z) / 2);
        var nc = ctx.Conductors.FirstOrDefault(c => c.Name == p.NegativeObject);
        var pc = ctx.Conductors.FirstOrDefault(c => c.Name == p.PositiveObject);
        bool warned = false;
        if (nc is not null && pc is not null)
        {
            var line = Geometry3.Sub(Nearest(pc, centre), Nearest(nc, centre));
            double len = Math.Sqrt(Geometry3.Dot(line, line));
            if (len > 2 * ctx.Dbu && Math.Abs(Geometry3.Dot(line, p.Direction)) / len < Math.Sqrt(0.5))
            {
                warned = true;
                yield return (DirectionId, DiagnosticSeverity.Warning,
                    $"{label} runs along {axis}, but its conductors face each other along {Along(line)[1..]}: the nearest points of " +
                    $"'{p.NegativeObject}' and '{p.PositiveObject}' to it lie {L(len)} apart along {Along(line)[1..]}. " +
                    (stated ? "Check its Positive and Negative, or redraw it across the gap between them."
                            : "Redraw it so its two ends lie across the gap between them."));
            }
        }

        if (!stated) yield break;
        if (!warned && Facing(port, doc.DbuPerMicron, ctx) is { } facing && facing.Pair != pair)
        {
            string[] names = ["left", "right", "bottom", "top"];
            int lo = 2 * facing.Pair, hi = lo + 1;
            yield return (DirectionId, DiagnosticSeverity.Warning,
                $"{label} runs along {axis}, but its conductors face each other along {Along(facing.Pair == 0 ? uDir : vDir)[1..]}: its " +
                $"{names[lo]} and {names[hi]} edges are {L(facing.Low.D)} and {L(facing.High.D)} from '{facing.Low.Name}' and " +
                $"'{facing.High.Name}', nearer than its {names[2 * pair]} and {names[2 * pair + 1]} edges are to its stated ends " +
                $"'{port.Negative}' and '{port.Positive}'. Check its Positive and Negative, or redraw it across the gap between them.");
        }
        if (Agreement(port, r, ctx, doc.DbuPerMicron, pair, p, cylinder: false) is { } agreement) yield return agreement;
    }

    /// <summary>R-emb3-3 — the stated ends against what inference would choose, when inference would choose at all.</summary>
    private static (string, DiagnosticSeverity, string)? Agreement(C3dPort port, C3dPortResult r, C3dPortContext ctx, int dbuPerMicron,
                                                                   int statedPair, Em3dPort stated, bool cylinder)
    {
        var bare = new C3dPort
        {
            Number = port.Number, Name = port.Name, Kind = port.Kind, Plane = port.Plane, Offset = port.Offset, Rect = port.Rect,
            Angle = port.Angle, Cylinder = port.Cylinder, Z0 = port.Z0, Model = port.Model,
        };
        if (C3dPorts.Resolve(bare, dbuPerMicron, ctx).Resolved is not { } inferred) return null;
        int inferredPair = cylinder
            ? inferred.Cylinder!.Along == Em3dCylinderGapAxis.Axial ? 1 : 0
            : Math.Abs(Geometry3.Dot(inferred.Direction, C3dTurnedPorts.Axes(port).U)) >= 0.5 ? 0 : 1;
        string Way(int pair, Em3dPort q) => cylinder ? (pair == 1 ? "along its cylinder's axis" : "round its cylinder") : "along " + Along(q.Direction)[1..];
        string label = r.Label;
        if (inferredPair != statedPair)
            return (DisagreesId, DiagnosticSeverity.Error,
                $"{label} states '{port.Negative}' and '{port.Positive}' as its ends, which puts it {Way(statedPair, stated)}, but its edges " +
                $"touch '{inferred.NegativeObject}' and '{inferred.PositiveObject}', which put it {Way(inferredPair, inferred)}: the stated " +
                "ends and what the port touches disagree. Correct Positive and Negative, or remove them and let what the port touches decide.");
        var statedSet = new HashSet<string>([port.Negative!, port.Positive!], StringComparer.Ordinal);
        if (!statedSet.SetEquals([inferred.NegativeObject, inferred.PositiveObject]))
            return (OtherEndsId, DiagnosticSeverity.Warning,
                $"{label} states '{port.Negative}' and '{port.Positive}' as its ends, but its edges touch '{inferred.NegativeObject}' " +
                $"and '{inferred.PositiveObject}'. Check Positive and Negative, or remove them and let what the port touches decide.");
        return null;
    }

    private readonly record struct Near(string Name, double D);

    /// <summary>
    /// A rectangular sheet's facing pair: per edge midpoint the two nearest conductors (however far), and per pair of opposite
    /// edges the least distance of two DIFFERENT conductors, one per edge. The facing pair is the nearer by more than a DBU; null
    /// when neither is.
    /// </summary>
    private static (int Pair, Near Low, Near High)? Facing(C3dPort port, int dbuPerMicron, C3dPortContext ctx)
    {
        double M(long v) => C3dLowering.Metres(v, dbuPerMicron);
        double u0 = M(port.Rect.Min.U), v0 = M(port.Rect.Min.V), u1 = u0 + M(port.Rect.Size.U), v1 = v0 + M(port.Rect.Size.V);
        double h = M(port.Offset), um = (u0 + u1) / 2, vm = (v0 + v1) / 2;
        Func<double, double, Point3> P = port.Angle != 0 && port.Plane == C3dPlane.XY
            ? C3dTurnedPorts.Map(port.Angle, u0, v0, u1, v1, h).P
            : (a, b) => C3dLowering.OnPlane(port.Plane, a, b, h);
        Point3[] mids = [P(u0, vm), P(u1, vm), P(um, v0), P(um, v1)];
        var nearest = mids.Select(m => TwoNearest(ctx, m)).ToArray();
        var best = new (double Score, Near Low, Near High)[2];
        for (int k = 0; k < 2; k++)
        {
            best[k] = (double.PositiveInfinity, default, default);
            foreach (var a in nearest[2 * k])
                foreach (var b in nearest[2 * k + 1])
                    if (a.Name != b.Name && a.D + b.D < best[k].Score) best[k] = (a.D + b.D, a, b);
        }
        int pair = best[0].Score <= best[1].Score ? 0 : 1;
        if (double.IsInfinity(best[pair].Score) || !(best[pair].Score + ctx.Dbu < best[1 - pair].Score)) return null;
        return (pair, best[pair].Low, best[pair].High);
    }

    /// <summary>The two conductors nearest <paramref name="q"/>, each measured only once its box could beat the second best.</summary>
    private static List<Near> TwoNearest(C3dPortContext ctx, Point3 q)
    {
        var found = new List<Near>(3);
        foreach (var (c, boxD) in ctx.Conductors.Select(c => (c, BoxDistance(c.Box, q))).OrderBy(x => x.Item2))
        {
            if (found.Count == 2 && boxD >= found[1].D) break;
            double d = ctx.Distance(c, q, q, bounded: false);
            found.Add(new Near(c.Name, d));
            found.Sort((x, y) => x.D.CompareTo(y.D));
            if (found.Count > 2) found.RemoveAt(2);
        }
        return found;
    }

    private static double BoxDistance((double X0, double Y0, double Z0, double X1, double Y1, double Z1) b, Point3 q)
    {
        double dx = Math.Max(0, Math.Max(b.X0 - q.X, q.X - b.X1)), dy = Math.Max(0, Math.Max(b.Y0 - q.Y, q.Y - b.Y1));
        double dz = Math.Max(0, Math.Max(b.Z0 - q.Z, q.Z - b.Z1));
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>The point of conductor <paramref name="c"/> nearest <paramref name="q"/>.</summary>
    private static Point3 Nearest(C3dPortContext.Conductor c, Point3 q)
    {
        var (x0, y0, z0, x1, y1, z1) = c.Box;
        if (c.Axis >= 0)
            return new Point3(Math.Clamp(q.X, x0, x1), Math.Clamp(q.Y, y0, y1), Math.Clamp(q.Z, z0, z1));
        if (c.Cylinder is { } cyl)
        {
            var ax = Geometry3.Sub(cyl.AxisEnd, cyl.AxisStart);
            double len = Math.Sqrt(Geometry3.Dot(ax, ax));
            var u = new Point3(ax.X / len, ax.Y / len, ax.Z / len);
            var d = Geometry3.Sub(q, cyl.AxisStart);
            double s = Math.Clamp(Geometry3.Dot(d, u), 0, len);
            var foot = new Point3(cyl.AxisStart.X + u.X * s, cyl.AxisStart.Y + u.Y * s, cyl.AxisStart.Z + u.Z * s);
            var radial = Geometry3.Sub(Geometry3.Sub(q, cyl.AxisStart), new Point3(u.X * Geometry3.Dot(d, u), u.Y * Geometry3.Dot(d, u), u.Z * Geometry3.Dot(d, u)));
            double rl = Math.Sqrt(Geometry3.Dot(radial, radial));
            double k = rl > cyl.Radius ? cyl.Radius / rl : 1;
            return new Point3(foot.X + radial.X * k, foot.Y + radial.Y * k, foot.Z + radial.Z * k);
        }
        var m = c.Mesh!;
        Point3 best = q;
        double bd = double.PositiveInfinity;
        foreach (var t in m.Triangles)
        {
            var p = Geometry3.ClosestOnTriangle(q, m.Vertices[t.A], m.Vertices[t.B], m.Vertices[t.C]);
            var d = Geometry3.Sub(p, q);
            double dd = Geometry3.Dot(d, d);
            if (dd < bd) (bd, best) = (dd, p);
        }
        return best;
    }

    /// <summary>A direction's dominant axis with its sign: <c>+x</c> … <c>-z</c>.</summary>
    private static string Along(Point3 d)
    {
        double ax = Math.Abs(d.X), ay = Math.Abs(d.Y), az = Math.Abs(d.Z);
        string axis = ax >= ay && ax >= az ? (d.X >= 0 ? "+x" : "-x") : ay >= az ? (d.Y >= 0 ? "+y" : "-y") : (d.Z >= 0 ? "+z" : "-z");
        // A turned port's direction is named by its nearest axis and its angle in the XY plane.
        double n = Math.Sqrt(ax * ax + ay * ay + az * az);
        if (n > 0 && az <= 1e-9 * n && Math.Min(ax, ay) > 1e-6 * n)
            axis += $" (at {(Math.Atan2(d.Y, d.X) * 180 / Math.PI).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}° in XY)";
        return axis;
    }

    /// <summary>The findings as sentences, for <c>explain</c>'s walk.</summary>
    public static string Text(Diagnostic d) => d.Arguments.TryGetValue("why", out var why) ? why?.ToString() ?? "" : d.Render();
}
