// brief-cxp-2 R-cxp2-1 — Make Port ▸ Coaxial on a round face: the end face of a connector's dielectric, whose inner edge is the
// pin and whose outer edge is the bore — or the pin's own end face inside its bore. The face gives the ring's plane, centre and
// one radius; the other is the nearest coaxial cylinder inside it (the dielectric's face) or round it (the pin's face) with which
// the ring RESOLVES (C3dPorts — the run's own rule decides, not a guess about materials here). A radius or the centre that a document cylinder states with an expression is written as that expression,
// so editing pin_d or bore_d moves the port with the geometry.

using CircuitRF.Design.Layout;
using CircuitRF.Design.ThreeD;
using CircuitRF.Engine.Em3d;
using CircuitRF.Render.Scene3D;
using CircuitRF.Render.Scene3D.Edit;
using CircuitRF.Ui.Viewer3D;

namespace CircuitRF.Ui.ThreeD;

public sealed partial class C3dEditorViewModel
{
    /// <summary>A face is round when its farthest corner is this close, relative, to half its bounding square's side.</summary>
    private const double RoundTolerance = 0.02;

    /// <summary>A drawn port's resolution as the records view holds it (by number: it resolves copies), with a ring's TEM
    /// impedance; null when the port is not in it.</summary>
    public (Em3dPort? Resolved, double? RingImpedance)? PortReport(C3dPort port)
    {
        if (PortResults.FirstOrDefault(r => r.Port.Number == port.Number && r.Port.Name == port.Name) is not { } r) return null;
        double? z = Elaboration is { } e && C3dPortReports.RingImpedance(r, e) is var (ohms, _, _) ? ohms : null;
        return (r.Resolved, z);
    }

    /// <summary>brief-cxp-2 R-cxp2-3 — one of a ring's dimensions (<c>Annulus.Inner</c> …), a length or an expression, through the
    /// one field write every dimension goes through. One undo entry; null on success.</summary>
    public string? SetPortFieldText(C3dPort port, string path, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "Type a number or an expression.";
        int index = Document.Ports.IndexOf(port);
        if (index < 0) return $"{C3dPorts.Label(port)} is not in this 3D view.";
        return EditNames($"Set {C3dPorts.Label(port)} {path} = {text}", (doc, _) =>
        {
            var copy = doc.Ports[index];
            if (path is "Annulus.Inner" or "Annulus.Outer" && LayoutUnits.TryParse(text, Document.DisplayUnit, Document.DbuPerMicron, out long r) && r <= 0)
                return "A coaxial port's radius is a positive length.";
            return WriteField(doc, copy, C3dPorts.Label(copy), path, text, path);
        });
    }

    /// <summary>Make Port ▸ Coaxial: the ring on a round face, added as one undo entry. Returns the refusal, or null.</summary>
    public string? MakeCoaxialPortFromFace(uint objectId, int face)
    {
        if (CoaxialPortFromFace(objectId, face, out var port, out _) is { } why) return why;
        AddPort(port!);
        return null;
    }

    /// <summary>The item, or null when the face is not round with a coaxial cylinder inside or around it (a rectangle, a pin's
    /// tip out in the air): the menu offers Coaxial only where a coax could end.</summary>
    private Viewer3DMenuItem? MakeCoaxialPortItem(Scene3DItem item)
    {
        string? why = CoaxialPortFromFace(item.Object, item.Face, out _, out bool round);
        if (!round) return null;
        return new Viewer3DMenuItem("Coaxial", () => Report(MakeCoaxialPortFromFace(item.Object, item.Face)), Enabled: why is null,
                                    Tip: why ?? "A coaxial port's ring on this face, between the pin at its centre (+) and the shell round " +
                                         "it (−): the coax's own radial field.");
    }

    /// <summary>The ring Make Port ▸ Coaxial would add on a face, not added — or why not. <paramref name="round"/> says whether
    /// the face is a disc or a ring with a coaxial cylinder inside it, so the menu knows whether to offer the item at all.</summary>
    private string? CoaxialPortFromFace(uint objectId, int face, out C3dPort? port, out bool round)
    {
        port = null;
        round = false;
        var scene = Viewer.Scene;
        if (scene.Object(objectId) is not { } o || face < 0) return "Select a face first.";
        var (_, normal) = Scene3DFaces.AreaAndNormal(scene, objectId, face);
        if (normal is not { } n) return $"Face {o.FaceName(face)} is curved: a coaxial port's ring is flat.";
        int axis = Math.Abs(n.X) > 0.999f ? 0 : Math.Abs(n.Y) > 0.999f ? 1 : Math.Abs(n.Z) > 0.999f ? 2 : -1;
        if (axis < 0) return $"Face {o.FaceName(face)} is not normal to x, y or z: a port lies on a drawing plane.";
        if (scene.FeaturesOf(objectId) is not { Table: { } t } fr || face >= t.FaceCount) return "That face's corners are not known.";
        var pts = Enumerable.Range(t.FaceVertexStart[face], t.FaceVertexStart[face + 1] - t.FaceVertexStart[face])
                            .Select(k => fr.Vertex(t.FaceVertices[k])).ToList();
        if (pts.Count < 6) return $"Face {o.FaceName(face)} is not round.";
        var (ua, va) = Em3dAnnulus.PlaneAxes(axis);
        double h = Em3dAnnulus.Get(pts[0], axis);
        double u0 = pts.Min(q => Em3dAnnulus.Get(q, ua)), u1 = pts.Max(q => Em3dAnnulus.Get(q, ua));
        double v0 = pts.Min(q => Em3dAnnulus.Get(q, va)), v1 = pts.Max(q => Em3dAnnulus.Get(q, va));
        double cu = (u0 + u1) / 2, cv = (v0 + v1) / 2, half = (u1 - u0) / 2;
        double farthest = pts.Max(q => Math.Sqrt(Sq(Em3dAnnulus.Get(q, ua) - cu) + Sq(Em3dAnnulus.Get(q, va) - cv)));
        if (!(half > 0) || Math.Abs((v1 - v0) / 2 - half) > RoundTolerance * half || Math.Abs(farthest - half) > RoundTolerance * half)
            return $"Face {o.FaceName(face)} is not round.";

        double per = C3dLowering.Metres(1, Document.DbuPerMicron);
        long D(double m) => (long)Math.Round(m / per, MidpointRounding.AwayFromZero);
        var plane = axis switch { 0 => C3dPlane.YZ, 1 => C3dPlane.XZ, _ => C3dPlane.XY };
        // The world axes of the drawing plane's own (u, v), which are not always Em3dAnnulus's (XZ is x then z here, z then x there).
        int pu = plane == C3dPlane.YZ ? 1 : 0, pv = plane == C3dPlane.XY ? 1 : 2;
        var centreWorld = new double[3];
        centreWorld[axis] = h; centreWorld[ua] = cu; centreWorld[va] = cv;
        double tol = Math.Max(RoundTolerance * half, 2 * per);

        // Every document cylinder coaxial with the face and reaching its plane, top-level or an operand, with its numbers
        // taken as world coordinates — which they are unless something placed it, and then it simply does not match.
        var cylinders = Document.Objects.SelectMany(C3dOperands.SelfAndDescendants).OfType<C3dCylinder>()
            .Where(c => c.Axis == (C3dAxis)axis)
            .Select(c => (Cyl: c, Base: new[] { c.Base.X * per, c.Base.Y * per, c.Base.Z * per }, R: c.Radius * per, L: c.Length * per))
            .Where(c => Math.Abs(c.Base[ua] - cu) <= tol && Math.Abs(c.Base[va] - cv) <= tol &&
                        h >= Math.Min(c.Base[axis], c.Base[axis] + c.L) - tol && h <= Math.Max(c.Base[axis], c.Base[axis] + c.L) + tol)
            .ToList();
        // The face's own circle, exact when a document cylinder states it.
        C3dCylinder? own = cylinders.Where(c => Math.Abs(c.R - half) <= tol).OrderBy(c => Math.Abs(c.R - half)).Select(c => c.Cyl).FirstOrDefault();
        long faceRadius = own?.Radius ?? D(half);
        // The face's own object never supplies the ring's other edge: a filleted pin's tip is a disc smaller than the pin, and the
        // pin is not a shell round itself. Its cylinders are the object's named so, or everything inside an operation of that name.
        var mine = Document.Objects.Where(x => x.Name == o.Name).SelectMany(C3dOperands.SelfAndDescendants)
                           .Concat(Document.Objects.SelectMany(C3dOperands.SelfAndDescendants).Where(x => x.Name == o.Name))
                           .OfType<C3dCylinder>().ToHashSet();
        var others = cylinders.Where(c => !mine.Contains(c.Cyl)).ToList();
        // Two ways a round face is one edge of the ring, both tried, each nearest-first:
        //  - its OUTER edge (the dielectric's end, round the pin): the inner circle is a smaller coaxial cylinder, or a hole in it;
        //  - its INNER edge (the pin's own end face, inside its bore): the outer circle is a larger coaxial cylinder.
        double nearest = pts.Min(q => Math.Sqrt(Sq(Em3dAnnulus.Get(q, ua) - cu) + Sq(Em3dAnnulus.Get(q, va) - cv)));
        var pairs = new List<(long Inner, C3dCylinder? InnerCyl, long Outer, C3dCylinder? OuterCyl)>();
        pairs.AddRange(others.Where(c => c.Cyl.Radius < faceRadius - D(tol)).OrderByDescending(c => c.Cyl.Radius)
                                .Select(c => (c.Cyl.Radius, (C3dCylinder?)c.Cyl, faceRadius, own)));
        if (nearest > 0.05 * half && nearest < half - tol) pairs.Add((D(nearest), null, faceRadius, own));
        pairs.AddRange(others.Where(c => c.Cyl.Radius > faceRadius + D(tol)).OrderBy(c => c.Cyl.Radius)
                                .Select(c => (faceRadius, own, c.Cyl.Radius, (C3dCylinder?)c.Cyl)));
        if (pairs.Count == 0)
            return $"Face {o.FaceName(face)} has nothing round and coaxial with it, inside or around: a coaxial port lies between a " +
                   "pin and the shell round it.";
        round = true;

        var context = CurrentPortContext();
        string? refusal = null;
        foreach (var (inner, innerCyl, outer, outerCyl) in pairs.DistinctBy(x => (x.Inner, x.Outer)))
        {
            var draft = NewPortTemplate(Em3dPortKind.Lumped);
            draft.Plane = plane;
            draft.Offset = D(h);
            var ring = new C3dPortAnnulus { Centre = new C3dPoint2(D(centreWorld[pu]), D(centreWorld[pv])), Inner = inner, Outer = outer };
            draft.Annulus = ring;
            var r = C3dPorts.Resolve(draft, Document.DbuPerMicron, context);
            if (r.Refusal is { } no) { refusal ??= no; continue; }
            // The expressions the cylinders were drawn with, carried onto the ring; the centre is the pin's.
            if (innerCyl is not null)
            {
                Bind(ring, nameof(C3dPortAnnulus.Inner), 0, C3dBindings.GetExpr(innerCyl, nameof(C3dCylinder.Radius), 0));
                int[] world = [pu, pv];
                for (int k = 0; k < 2; k++) Bind(ring, nameof(C3dPortAnnulus.Centre), k, C3dBindings.GetExpr(innerCyl, nameof(C3dCylinder.Base), world[k]));
            }
            if (outerCyl is not null) Bind(ring, nameof(C3dPortAnnulus.Outer), 0, C3dBindings.GetExpr(outerCyl, nameof(C3dCylinder.Radius), 0));
            port = draft;
            return null;
        }
        return refusal;

        static double Sq(double v) => v * v;
        static void Bind(C3dPortAnnulus ring, string field, int k, C3dExpr? expr)
        {
            if (expr is null) return;
            var spec = C3dBindings.Fields.Single(f => f.Owner == typeof(C3dPortAnnulus) && f.Property == field);
            C3dBindings.SetExpr(ring, spec, k, expr);
        }
    }
}
