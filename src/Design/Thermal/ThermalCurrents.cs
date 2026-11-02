// brief-em3d-77 R-em3d77-1 — a port's DC current in a thermal run: which face of which conductor it enters through, and which
// it leaves through.
//
// THE PORT IS THE EM PORT (overview §1c). Its conductors are the ones the EM lowering resolves — C3dPorts, the same contact
// search, with Flip honoured — and the current enters the POSITIVE one and leaves the NEGATIVE one. Where on each: the face
// the port's edge on that conductor lies on. An edge along a solid's own edge lies on two faces (a line's end face and its
// bottom, say), and the contact must not short the conductor along its length, so the SMALLEST face that holds the edge is
// taken — the line's end, not its whole underside. EnterFace / LeaveFace name a face instead, and the run's notes say which
// face each port used, so an inference that is not what the user meant is visible at once.
//
// Each contact is an equipotential carrying the prescribed total (the solver merges its nodes into one potential), never a
// uniform current density, which would put a spurious hot edge where the contact ends.

using System.Globalization;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Thermal;

/// <summary>One port's contacts: the face groups its current enters and leaves through, and the sentence saying so.</summary>
public sealed record ThermalPortContact(int Port, IReadOnlyList<string> Positive, IReadOnlyList<string> Negative, string Note);

/// <summary>brief-em3d-78 R-em3d78-2 — a port with harmonic currents: its positive conductor, the solid a wire array must land on
/// to carry them.</summary>
public sealed record ThermalRfPort(int Port, string PositiveSolid);

public static class ThermalCurrents
{
    /// <summary>
    /// Each current of <paramref name="t"/>, its contact faces added to <paramref name="faces"/> (as whole faces), or null with
    /// the refusal. <paramref name="solids"/> are the solids the run meshes. brief-em3d-78: a port with harmonics is also in
    /// <paramref name="rf"/>, with its positive conductor; one with harmonics and no Dc has no DC contacts at all (its current is
    /// the wires', not the metal's), and an <c>Array</c> entry is neither's.
    /// </summary>
    public static List<ThermalPortContact>? Contacts(C3dDocument doc, C3dElaboration e, CemThermal t, IReadOnlyList<Em3dSolid> solids,
                                                     List<GmshThermalFace> faces, List<ThermalRfPort> rf, out string? refusal)
    {
        refusal = null;
        var list = new List<ThermalPortContact>();
        var currents = t.Currents ?? [];
        if (currents.Count == 0) return list;
        C3dPortContext? ctx = null;
        foreach (var c in currents)
        {
            if (c.Port is not int number) continue;
            bool harmonics = c.Harmonics is { Count: > 0 };
            bool dc = !harmonics || !string.IsNullOrWhiteSpace(c.Dc);
            var port = doc.Ports.FirstOrDefault(p => p.Number == number);
            if (port is null) { refusal = $"The thermal setup gives port {number} a current, and this 3D view has no port {number}."; return null; }
            string label = C3dPorts.Label(port);
            string? posFace = c.EnterFace, negFace = dc ? c.LeaveFace : "";
            string posWhy = "stated", negWhy = "stated";
            if (posFace is null || negFace is null)
            {
                ctx ??= new C3dPortContext(e, doc.DbuPerMicron, null, e.GroundBandObjects);
                var r = C3dPorts.Resolve(port, doc.DbuPerMicron, ctx);
                if (r.Resolved is not { } em)
                {
                    refusal = $"Port {number} carries a current, and its conductors do not resolve: {r.Refusal} State its EnterFace{(dc ? " and LeaveFace" : "")}.";
                    return null;
                }
                if (em.Kind == Em3dPortKind.Wave)
                {
                    refusal = $"{label} is a wave port; a thermal current enters through named faces: state its EnterFace{(dc ? " and LeaveFace" : "")}.";
                    return null;
                }
                double tol = C3dLowering.Metres(1, doc.DbuPerMicron);
                if (posFace is null)
                {
                    posFace = Infer(em, em.PositiveObject, positive: true, solids, tol, out posWhy);
                    if (posFace is null) { refusal = $"{label}'s current cannot enter '{em.PositiveObject}': {posWhy} State its EnterFace."; return null; }
                }
                if (negFace is null)
                {
                    negFace = Infer(em, em.NegativeObject, positive: false, solids, tol, out negWhy);
                    if (negFace is null) { refusal = $"{label}'s current cannot leave through '{em.NegativeObject}': {negWhy} State its LeaveFace."; return null; }
                }
            }
            string? positiveSolid = null;
            foreach (var (spelled, what) in dc ? new[] { (posFace, "EnterFace"), (negFace, "LeaveFace") } : [(posFace, "EnterFace")])
            {
                if (Add(doc, e, solids, dc ? faces : [], spelled, out string? why, out string? solid) is false)
                {
                    refusal = $"{label}'s {what} '{spelled}' cannot be placed in the mesh: {why}";
                    return null;
                }
                positiveSolid ??= solid;
            }
            if (harmonics) rf.Add(new ThermalRfPort(number, positiveSolid!));
            if (dc)
                list.Add(new ThermalPortContact(number, [posFace], [negFace],
                    $"{label}'s current enters through '{posFace}' ({posWhy}) and leaves through '{negFace}' ({negWhy})."));
        }
        return list;
    }

    /// <summary>The face spelled <paramref name="spelled"/> as a whole-face group, unless one is there already.</summary>
    private static bool Add(C3dDocument doc, C3dElaboration e, IReadOnlyList<Em3dSolid> solids, List<GmshThermalFace> faces, string spelled,
                            out string? why, out string? solid)
    {
        why = null;
        solid = null;
        if (faces.FirstOrDefault(f => f.Name == spelled && !f.ExteriorOnly) is { } there) { solid = solids[there.Solid].Name; return true; }
        // an inferred face is spelled with the elaborated solid's name; a stated one as the document spells it
        int slash = spelled.LastIndexOf('/');
        int si = slash > 0 ? IndexOf(solids, spelled[..slash]) : -1;
        if (si >= 0 && Em3dFaceGeometry.Pieces(solids[si].Primitive, spelled[(slash + 1)..], out _) is { } own)
        {
            faces.Add(new GmshThermalFace(spelled, si, [.. own], false));
            solid = solids[si].Name;
            return true;
        }
        if (ThermalLowerings.FacePieces(doc, e, solids, spelled, out int s2, out why) is not { } pieces) return false;
        faces.Add(new GmshThermalFace(spelled, s2, pieces, false));
        solid = solids[s2].Name;
        return true;
    }

    private static int IndexOf(IReadOnlyList<Em3dSolid> solids, string name)
    {
        for (int i = 0; i < solids.Count; i++) if (solids[i].Name == name) return i;
        return -1;
    }

    /// <summary>The smallest face of <paramref name="conductor"/> the port's edge on that side lies on, spelled object/face.</summary>
    private static string? Infer(Em3dPort port, string conductor, bool positive, IReadOnlyList<Em3dSolid> solids, double tol, out string why)
    {
        int si = IndexOf(solids, conductor);
        if (si < 0) { why = "it is not a solid this thermal run meshes."; return null; }
        double[] lo = [port.Min.X, port.Min.Y, port.Min.Z], hi = [port.Max.X, port.Max.Y, port.Max.Z];
        double[] d = [port.Direction.X, port.Direction.Y, port.Direction.Z];
        int along = Enumerable.Range(0, 3).MaxBy(k => Math.Abs(d[k]));
        int normal = Enumerable.Range(0, 3).Where(k => k != along).MinBy(k => hi[k] - lo[k]);
        int across = 3 - along - normal;
        bool high = positive == d[along] > 0;
        double at = high ? hi[along] : lo[along];
        Point3 P(double a) { var v = new double[3]; v[along] = at; v[normal] = lo[normal]; v[across] = a; return new Point3(v[0], v[1], v[2]); }
        var probe = new[] { P(lo[across] + tol), P((lo[across] + hi[across]) / 2), P(hi[across] - tol) };
        if (port.Quad is { } quad)
        {
            // A turned port: its end on this side, from corner to corner.
            var (negEnd, posEnd) = quad.Ends(port.Direction);
            var (e0, e1) = positive ? posEnd : negEnd;
            double len = Math.Sqrt((e1.X - e0.X) * (e1.X - e0.X) + (e1.Y - e0.Y) * (e1.Y - e0.Y) + (e1.Z - e0.Z) * (e1.Z - e0.Z));
            double f = len > 2 * tol ? tol / len : 0.5;
            Point3 Q(double t) => new(e0.X + t * (e1.X - e0.X), e0.Y + t * (e1.Y - e0.Y), e0.Z + t * (e1.Z - e0.Z));
            probe = [Q(f), Q(0.5), Q(1 - f)];
        }
        var prim = solids[si].Primitive;
        string? best = null;
        double bestArea = double.PositiveInfinity;
        foreach (string f in Em3dFaceGeometry.FaceNames(prim))
        {
            if (Em3dFaceGeometry.Pieces(prim, f, out _) is not { } pieces) continue;
            bool holds = pieces.Any(pc =>
            {
                var b = pc.Bounds();
                return probe.All(q => q.X >= b.X0 - tol && q.X <= b.X1 + tol && q.Y >= b.Y0 - tol && q.Y <= b.Y1 + tol && q.Z >= b.Z0 - tol && q.Z <= b.Z1 + tol);
            });
            if (!holds) continue;
            double area = pieces.Sum(Area);
            if (area < bestArea) (best, bestArea) = (f, area);
        }
        if (best is null)
        {
            why = $"the port's {(positive ? "positive" : "negative")} edge lies on none of its faces.";
            return null;
        }
        why = $"the smallest face its edge lies on, {(bestArea * 1e12).ToString("0.##", CultureInfo.InvariantCulture)} µm²";
        return $"{conductor}/{best}";
    }

    /// <summary>A planar piece's area (its outer loop less its holes), m², by Newell's method.</summary>
    private static double Area(Em3dFacePolygon p)
    {
        static double Loop(IReadOnlyList<Point3> v)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < v.Count; i++)
            {
                var (a, b) = (v[i], v[(i + 1) % v.Count]);
                x += (a.Y - b.Y) * (a.Z + b.Z); y += (a.Z - b.Z) * (a.X + b.X); z += (a.X - b.X) * (a.Y + b.Y);
            }
            return Math.Sqrt(x * x + y * y + z * z) / 2;
        }
        return Loop(p.Outer) - p.Holes.Sum(Loop);
    }
}
