// brief-em3d-63 R-em3d63-5 — the tree the geometry worker receives: resolved numbers, and circuitRF's names.
//
// ELABORATE FIRST (R-em3d63-5a). A tree is made from objects C3dResolver has already turned into numbers
// (it writes every bound field in place), and it carries no expression, no unit and no variable: the
// worker never sees a .c3d. Lengths are MICROMETRES (R-em3d63-4c) — OCCT's absolute tolerances sit nine
// orders of magnitude below a 25 µm feature in µm and two in metres, the reason GmshGeoWriter writes µm
// too — converted from DBU once, here, by LayoutUnits' exact decimal path.
//
// EVERY NODE CARRIES ITS FACE NAMES (R-em3d63-5b), in the primitive's own face order — the names
// C3dObject.FaceNames() gives it — and the worker names its OCCT faces from them (README.md "Face names").
//
// CANONICAL, SO IT HASHES (R-em3d63-5c). Fixed key order, invariant culture, shortest round-trip doubles,
// no -0, and one trailing "\n": the same resolved tree is the same bytes and the same SHA-256 on every
// platform — GmshGeoWriter's determinism rules. A rotation matrix's entries are the one input whose last
// bit can come from a platform's cosine, so they are snapped to integers (as C3dLowering does) and
// otherwise quantised to 2⁻⁴⁸ — far below anything geometric, far above a last-bit difference.
//
// THE NODE FORM (the worker's reader is the other half of this; README.md is the reference):
//
//   {"tree":1,"root":{"kind":"box","name":"lid","faces":[…],"transform":[12 numbers, 3 × 4 rows],
//                     "min":[x,y,z],"size":[x,y,z]}}
//   cylinder:   "base":[x,y,z],"axis":[x,y,z],"length":L,"radius":r          (bottom is the cap at base)
//   sphere:     "centre":[x,y,z],"radius":r                                    (one face, surface; brief-em3d-102)
//   prism:      "outline":[[x,y,z]…],"holes":[[[x,y,z]…]…],"extrude":[x,y,z]  (points in the object's frame)
//   polyhedron: "vertices":[[x,y,z]…],"loops":[{"outer":[i…],"holes":[[i…]…]}…]
//
// brief-em3d-64 adds the operation nodes, whose operands are nodes of this form, built in the operation's own frame and
// then carried by its transform. They list no "faces": the worker names the result from its operands' (README.md):
//
//   boolean:  "op":"subtract"|"unite"|"intersect","blank":{node},"tools":[{node}…]   (the blank has "name":"")
//   fillet:   "radius":r,"edges":["xmax|zmax"…],"target":{node}
//   chamfer:  "distance":d,["distance2":d2,]"edges":[…],"target":{node}
//   step:     "file":<absolute path>,"hash":"sha256:…","part":"1/2"[,"solid":k]  (faces are face<n>, the solid's own
//             when "solid" is there; brief-em3d-127 writes it only when set, so a whole part's tree keeps its cache key)

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.ThreeD.Occ;

/// <summary>One object's geometry as the worker receives it: canonical JSON, its bytes and their hash.</summary>
public sealed class GeometryKernelTree
{
    private GeometryKernelTree(string obj, string json, IReadOnlyList<string> faceNames)
    {
        Object = obj;
        Json = json;
        Bytes = Encoding.UTF8.GetBytes(json);
        Hash = Convert.ToHexStringLower(SHA256.HashData(Bytes));
        FaceNames = faceNames;
    }

    /// <summary>The root object's name — what a refusal or a crash sentence names.</summary>
    public string Object { get; }

    /// <summary>The canonical text, ending in one <c>\n</c>.</summary>
    public string Json { get; }

    public byte[] Bytes { get; }

    /// <summary>SHA-256 of <see cref="Bytes"/>, lower-case hex.</summary>
    public string Hash { get; }

    /// <summary>The root's face names, in the order the tree lists them.</summary>
    public IReadOnlyList<string> FaceNames { get; }

    /// <summary>The kinds a tree can hold. A sheet, a polyline and a wire are not solids the kernel builds, and neither is a Boolean of
    /// sheets (brief-hzn-10), which circuitRF combines itself: the worker is never asked for one.</summary>
    public static bool CanHold(C3dObject obj) => (obj is C3dBox or C3dCylinder or C3dSphere or C3dEllipsoid or C3dPrism or C3dPolyhedron
                                                  or C3dBoolean or C3dFillet or C3dChamfer or C3dStep) && !C3dSheetBooleans.IsSheetBoolean(obj);

    /// <summary>
    /// <paramref name="obj"/> — RESOLVED, its placement applied first and then <paramref name="worldUm"/> (the
    /// instances above it, translation in micrometres; identity when omitted).
    /// </summary>
    /// <exception cref="ArgumentException">The object is not a kind a tree can hold (<see cref="CanHold"/>).</exception>
    /// <param name="documentDir">The folder of the <c>.c3d</c> holding it: what a Step object's <c>File</c> is relative to.</param>
    public static GeometryKernelTree From(C3dObject obj, int dbuPerMicron, C3dTransform? worldUm = null, string? documentDir = null)
    {
        if (!CanHold(obj))
            throw new ArgumentException($"'{obj.Name}' is a {C3dObject.KindOf(obj).ToLowerInvariant()}, which is not a solid the geometry kernel builds.", nameof(obj));
        var w = new CanonicalJson();
        w.Begin().Key("tree").Int(1).Key("root");
        var names = WriteNode(w, obj, dbuPerMicron, worldUm ?? C3dTransform.Identity, documentDir);
        w.End();
        return new GeometryKernelTree(obj.Name, w.Text + "\n", names);
    }

    /// <summary>A tree from text the caller vouches is canonical — the tests' route to the worker's test-only nodes.</summary>
    internal static GeometryKernelTree FromJson(string obj, string canonicalJson, IReadOnlyList<string>? faceNames = null) =>
        new(obj, canonicalJson.EndsWith('\n') ? canonicalJson : canonicalJson + "\n", faceNames ?? []);

    /// <summary>Writes one object as a node; returns the face names it listed.</summary>
    internal static IReadOnlyList<string> WriteNode(CanonicalJson w, C3dObject obj, int dbuPerMicron, C3dTransform worldUm,
                                                    string? documentDir = null, bool operand = false)
    {
        double U(long dbu) => Um(dbu, dbuPerMicron);
        // brief-hzn-12 — a faceted sphere is handed to the kernel as the polyhedron it is.
        if (obj is C3dSphere { FacetVertices: > 0 } faceted) obj = C3dFacetedSphere.Polyhedron(faceted);
        var placement = obj.Placement.ToTransform();
        var t = placement with { Tx = U((long)placement.Tx), Ty = U((long)placement.Ty), Tz = U((long)placement.Tz) };
        t = Canonical(t.Then(worldUm));

        // 3D editor round 5 — a DISABLED operation inside another operation is what it wraps, carried by its own placement,
        // exactly as the elaborator draws a disabled one. Written as an operation node it was built as one under an enabled
        // one wrapping it, so a solid's switched-off fillet still rounded the solid and a switched-off boolean still cut.
        // A disabled Boolean's Tools are not in the tree at all: they are the elaborator's standalone solids (NestedTools).
        if (operand && obj is C3dOperation { Enabled: false } && C3dOperands.Inner(obj) is { } inner)
            return WriteNode(w, inner, dbuPerMicron, t, documentDir, operand: true);

        if (obj is C3dOperation or C3dStep)
        {
            WriteOperation(w, obj, t, dbuPerMicron, documentDir);
            return obj.FaceNames();
        }

        IReadOnlyList<string> names = obj is C3dPolyhedron ph0 ? [.. (Kernel.C3dRecognition.ExactlyPlanarFaces(ph0) ?? ph0.Faces).Select(f => f.Name)]
                                                               : obj.FaceNames();
        w.Begin()
         .Key("kind").Str(obj switch { C3dBox => "box", C3dCylinder => "cylinder", C3dSphere => "sphere", C3dEllipsoid => "ellipsoid", C3dPrism => "prism", _ => "polyhedron" })
         .Key("name").Str(obj.Name)
         .Key("faces").BeginArr();
        foreach (var n in names) w.Str(n);
        w.EndArr().Key("transform").BeginArr();
        foreach (double v in (double[])[t.M00, t.M01, t.M02, t.Tx, t.M10, t.M11, t.M12, t.Ty, t.M20, t.M21, t.M22, t.Tz]) w.Num(v);
        w.EndArr();

        switch (obj)
        {
            case C3dBox b:
                w.Key("min").Point(U(b.Min.X), U(b.Min.Y), U(b.Min.Z));
                w.Key("size").Point(U(b.Size.X), U(b.Size.Y), U(b.Size.Z));
                break;
            case C3dCylinder c:
                w.Key("base").Point(U(c.Base.X), U(c.Base.Y), U(c.Base.Z));
                w.Key("axis").Point(c.Axis == C3dAxis.X ? 1 : 0, c.Axis == C3dAxis.Y ? 1 : 0, c.Axis == C3dAxis.Z ? 1 : 0);
                w.Key("length").Num(U(c.Length));
                w.Key("radius").Num(U(c.Radius));
                break;
            case C3dSphere sp:
                // brief-em3d-102 R-em3d102-3a — a worker built before spheres refuses the kind with its own sentence.
                w.Key("centre").Point(U(sp.Centre.X), U(sp.Centre.Y), U(sp.Centre.Z));
                w.Key("radius").Num(U(sp.Radius));
                break;
            case C3dEllipsoid el:
                // brief-sar-3 R-sar3-1 — a unit sphere scaled along its own axes; a worker built before ellipsoids refuses the kind.
                w.Key("centre").Point(U(el.Centre.X), U(el.Centre.Y), U(el.Centre.Z));
                w.Key("semi").Point(U(el.SemiAxes.X), U(el.SemiAxes.Y), U(el.SemiAxes.Z));
                break;
            case C3dPrism p:
            {
                double h = U(p.Offset);
                void Ring(List<C3dPoint2> ring)
                {
                    w.BeginArr();
                    foreach (var q in ring)
                    {
                        var pt = C3dLowering.OnPlane(p.Plane, U(q.U), U(q.V), h);
                        w.Point(pt.X, pt.Y, pt.Z);
                    }
                    w.EndArr();
                }
                w.Key("outline");
                Ring(p.Outline);
                w.Key("holes").BeginArr();
                foreach (var hole in p.Holes) Ring(hole);
                w.EndArr();
                // The top is the outline moved by the height along the plane's normal and by the shear in it.
                var e = C3dLowering.OnPlane(p.Plane, U(p.Shear.U), U(p.Shear.V), U(p.Height));
                w.Key("extrude").Point(e.X, e.Y, e.Z);
                break;
            }
            case C3dPolyhedron ph:
            {
                w.Key("vertices").BeginArr();
                foreach (var v in ph.Vertices) w.Point(U(v.X), U(v.Y), U(v.Z));
                w.EndArr().Key("loops").BeginArr();
                foreach (var f in Kernel.C3dRecognition.ExactlyPlanarFaces(ph) ?? ph.Faces)
                {
                    w.Begin().Key("outer").BeginArr();
                    foreach (int i in f.Outer) w.Int(i);
                    w.EndArr().Key("holes").BeginArr();
                    foreach (var hole in f.Holes)
                    {
                        w.BeginArr();
                        foreach (int i in hole) w.Int(i);
                        w.EndArr();
                    }
                    w.EndArr().End();
                }
                w.EndArr();
                break;
            }
        }
        w.End();
        return names;
    }

    /// <summary>An operation node: its switches, its transform, then its operands — each in the operation's own frame.</summary>
    private static void WriteOperation(CanonicalJson w, C3dObject obj, C3dTransform t, int dbuPerMicron, string? documentDir)
    {
        double U(long dbu) => Um(dbu, dbuPerMicron);
        w.Begin()
         .Key("kind").Str(obj switch { C3dBoolean => "boolean", C3dFillet => "fillet", C3dChamfer => "chamfer", _ => "step" })
         .Key("name").Str(obj.Name)
         .Key("transform").BeginArr();
        foreach (double v in (double[])[t.M00, t.M01, t.M02, t.Tx, t.M10, t.M11, t.M12, t.Ty, t.M20, t.M21, t.M22, t.Tz]) w.Num(v);
        w.EndArr();
        void Operand(C3dObject o) => WriteNode(w, o, dbuPerMicron, C3dTransform.Identity, documentDir, operand: true);
        void Edges(List<string> edges)
        {
            w.Key("edges").BeginArr();
            foreach (string e in edges) w.Str(e);
            w.EndArr();
        }
        switch (obj)
        {
            case C3dBoolean b:
                w.Key("op").Str(b.Op switch { C3dBooleanOp.Subtract => "subtract", C3dBooleanOp.Unite => "unite", _ => "intersect" });
                w.Key("blank");
                Operand(b.Blank ?? throw new ArgumentException($"The boolean '{obj.Name}' has no Blank.", nameof(obj)));
                w.Key("tools").BeginArr();
                foreach (var tool in b.Tools) Operand(tool);
                w.EndArr();
                break;
            case C3dFillet f:
                w.Key("radius").Num(U(f.Radius));
                Edges(f.Edges);
                w.Key("target");
                Operand(f.Target ?? throw new ArgumentException($"The fillet '{obj.Name}' has no Target.", nameof(obj)));
                break;
            case C3dChamfer c:
                w.Key("distance").Num(U(c.Distance));
                if (c.Distance2 != 0) w.Key("distance2").Num(U(c.Distance2));
                Edges(c.Edges);
                w.Key("target");
                Operand(c.Target ?? throw new ArgumentException($"The chamfer '{obj.Name}' has no Target.", nameof(obj)));
                break;
            case C3dStep st:
                string file = documentDir is null ? st.File : Path.GetFullPath(Path.Combine(documentDir, st.File));
                w.Key("file").Str(file).Key("hash").Str(st.Hash).Key("part").Str(st.Part);
                if (st.Solid is { } k) w.Key("solid").Int(k);
                break;
        }
        w.End();
    }

    /// <summary>DBU to micrometres, exactly: the decimal value, then the nearest double.</summary>
    public static double Um(long dbu, int dbuPerMicron) => (double)LayoutUnits.FromDbu(dbu, LayoutUnit.Um, dbuPerMicron);

    /// <summary>The matrix snapped to integers where it is one within 1e-12, and otherwise quantised to 2⁻⁴⁸.</summary>
    private static C3dTransform Canonical(C3dTransform t)
    {
        static double Q(double v)
        {
            double r = Math.Round(v);
            if (Math.Abs(v - r) <= 1e-12) return r + 0.0;
            const double scale = 281474976710656.0;  // 2^48
            return Math.Round(v * scale) / scale + 0.0;
        }
        return t with
        {
            M00 = Q(t.M00), M01 = Q(t.M01), M02 = Q(t.M02),
            M10 = Q(t.M10), M11 = Q(t.M11), M12 = Q(t.M12),
            M20 = Q(t.M20), M21 = Q(t.M21), M22 = Q(t.M22),
        };
    }
}

/// <summary>
/// A JSON writer whose output depends on nothing but the values written: keys in the order the caller
/// writes them, invariant culture, shortest round-trip doubles, -0 written as 0, no whitespace.
/// </summary>
internal sealed class CanonicalJson
{
    private readonly StringBuilder _s = new();
    private readonly Stack<bool> _first = new();
    private bool _afterKey;

    public string Text => _s.ToString();

    public CanonicalJson Begin() { Value(); _s.Append('{'); _first.Push(true); return this; }
    public CanonicalJson End() { _s.Append('}'); _first.Pop(); return this; }
    public CanonicalJson BeginArr() { Value(); _s.Append('['); _first.Push(true); return this; }
    public CanonicalJson EndArr() { _s.Append(']'); _first.Pop(); return this; }

    public CanonicalJson Key(string k)
    {
        Comma();
        Quote(k);
        _s.Append(':');
        _afterKey = true;
        return this;
    }

    public CanonicalJson Str(string v) { Value(); Quote(v); return this; }
    public CanonicalJson Int(long v) { Value(); _s.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }

    public CanonicalJson Num(double v)
    {
        if (!double.IsFinite(v)) throw new ArgumentException("A geometry tree holds finite numbers only.");
        Value();
        _s.Append((v + 0.0).ToString("R", CultureInfo.InvariantCulture));
        return this;
    }

    public CanonicalJson Point(double x, double y, double z) => BeginArr().Num(x).Num(y).Num(z).EndArr();

    private void Comma()
    {
        if (_first.Count == 0) return;
        if (!_first.Peek()) _s.Append(',');
        _first.Pop();
        _first.Push(false);
    }

    private void Value()
    {
        if (_afterKey) { _afterKey = false; return; }
        Comma();
    }

    private void Quote(string v)
    {
        _s.Append('"');
        foreach (char c in v)
        {
            switch (c)
            {
                case '"': _s.Append("\\\""); break;
                case '\\': _s.Append("\\\\"); break;
                default:
                    if (c < 0x20) _s.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else _s.Append(c);
                    break;
            }
        }
        _s.Append('"');
    }
}
