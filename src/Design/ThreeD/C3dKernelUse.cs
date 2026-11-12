// brief-em3d-64 R-em3d64-5a / -2f — which objects of a .c3d need the geometry kernel, and how a reference into an
// operation's result is read while the operation is disabled.
//
// ONE RULE, BY KIND, AND DELIBERATELY SO (overview §1d). A document "uses the kernel" when it holds a Boolean, Fillet,
// Chamfer or Step object ANYWHERE — at any depth, enabled or not. A disabled boolean of managed operands could elaborate
// without the kernel (R-em3d64-4a) and is refused anyway: a document that opened or refused depending on a checkbox
// would be harder to explain than one rule by kind, and one opened because its only boolean happened to be disabled
// would fail the moment the user re-enabled it, mid-session, with work in the editor. A test pins the disabled case
// as a refusal so nobody "optimises" it later.
//
// REFERENCES SURVIVE THE ENABLED TOGGLE WITH NOTHING REWRITTEN (R-em3d64-2f). A boundary on (`lid`, `bore:side`) is the
// bore's wall in the result while the boolean is enabled; disabled, that face is not on `lid` — the bore elaborates as
// itself — so the reference is read as (`bore`, `side`) at resolution, never in the file.

using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.ThreeD;

/// <summary>The operands an object owns inline — none for anything but an operation.</summary>
public static class C3dOperands
{
    /// <summary>Each operand with the path prefix its fields are named under (<c>Blank.</c>, <c>Tools[0].</c>, <c>Target.</c>).</summary>
    public static IEnumerable<(string Prefix, C3dObject Operand)> Of(C3dObject o)
    {
        switch (o)
        {
            case C3dBoolean b:
                if (b.Blank is { } blank) yield return ("Blank.", blank);
                for (int k = 0; k < b.Tools.Count; k++) yield return ($"Tools[{k}].", b.Tools[k]);
                break;
            case C3dFillet { Target: { } t }: yield return ("Target.", t); break;
            case C3dChamfer { Target: { } t }: yield return ("Target.", t); break;
        }
    }

    /// <summary><paramref name="o"/> and everything inside it, depth first.</summary>
    public static IEnumerable<C3dObject> SelfAndDescendants(C3dObject o)
    {
        yield return o;
        foreach (var (_, child) in Of(o))
            foreach (var d in SelfAndDescendants(child)) yield return d;
    }

    /// <summary>The object an operation wraps and takes its name from: a Boolean's Blank, a Fillet's or Chamfer's Target.</summary>
    public static C3dObject? Inner(C3dObject o) => o switch
    {
        C3dBoolean b => b.Blank,
        C3dFillet f => f.Target,
        C3dChamfer c => c.Target,
        _ => null,
    };

    /// <summary>A kind the geometry kernel builds and nothing else can. brief-hzn-10 — a Boolean of sheets is not one: circuitRF
    /// combines its polygons itself (<see cref="C3dSheetBooleans"/>).</summary>
    public static bool IsKernel(C3dObject o) => (o is C3dOperation && !C3dSheetBooleans.IsSheetBoolean(o)) || o is C3dStep;

    /// <summary>A solid: what may be an operand (R-em3d64-1e). A sheet, a Boolean of sheets, a polyline or a wire is not.</summary>
    public static bool IsSolid(C3dObject o) => o is C3dBox or C3dPrism or C3dCylinder or C3dSphere or C3dEllipsoid or C3dPolyhedron or C3dStep
                                               || (o is C3dOperation && !C3dSheetBooleans.IsSheetBoolean(o));

    /// <summary>How an object is named in a sentence: <c>a Boolean</c>, <c>a Step part</c>.</summary>
    public static string Article(C3dObject o) => o is C3dStep ? "a Step part" : "a " + C3dObject.KindOf(o);
}

/// <summary>One object that needs the geometry kernel.</summary>
/// <param name="Name">Its name — the wrapper's, or a Tool's own; the path to it when it has none.</param>
/// <param name="Kind">Its <c>$type</c>.</param>
public sealed record C3dKernelObject(string Name, string Kind, C3dObject Object)
{
    /// <summary><c>'lid' (a Boolean)</c>.</summary>
    public string Label => $"'{Name}' ({C3dOperands.Article(Object)})";
}

/// <summary>R-em3d64-5 — what in a document needs the geometry kernel, and the sentence that refuses it without one.</summary>
public static class C3dKernelUse
{
    /// <summary>Most objects a refusal lists by name before it says "and N more".</summary>
    public const int ListedAtMost = 5;

    /// <summary>
    /// Every Boolean, Fillet, Chamfer and Step in <paramref name="doc"/>, at any depth and <b>whether or not it is
    /// enabled</b> (R-em3d64-5a), top-level objects first in construction order and each one's operands after it.
    /// </summary>
    public static IReadOnlyList<C3dKernelObject> Of(C3dDocument doc)
    {
        var found = new List<C3dKernelObject>();
        foreach (var top in doc.Objects)
            Walk(top, top.Name, found);
        return found;

        static void Walk(C3dObject o, string name, List<C3dKernelObject> found)
        {
            if (C3dOperands.IsKernel(o)) found.Add(new C3dKernelObject(name, C3dObject.KindOf(o), o));
            foreach (var (prefix, child) in C3dOperands.Of(o))
                Walk(child, child.Name.Length > 0 ? child.Name : $"{name}.{prefix.TrimEnd('.')}", found);
        }
    }

    /// <summary>The objects as a sentence's subject: <c>'lid' (a Boolean) and 'shell' (a Step part)</c> — more than
    /// <see cref="ListedAtMost"/> are listed as five and "and N more".</summary>
    public static string List(IReadOnlyList<C3dKernelObject> objects)
    {
        var labels = objects.Take(ListedAtMost).Select(o => o.Label).ToList();
        if (objects.Count > ListedAtMost) return string.Join(", ", labels) + $" and {objects.Count - ListedAtMost} more";
        return labels.Count <= 1 ? string.Join("", labels) : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1];
    }

    /// <summary>
    /// R-em3d64-5b — why a document cannot be opened without the kernel: the object list, then
    /// <see cref="GeometryKernel.NeedsKernel(string, GeometryKernelCapability, bool)"/>'s sentence. The only words of this
    /// brief's own are the list.
    /// </summary>
    public static string Refusal(IReadOnlyList<C3dKernelObject> objects, GeometryKernelCapability capability)
        => GeometryKernel.NeedsKernel(List(objects), capability, plural: objects.Count > 1);

    /// <summary>
    /// R-em3d64-5b — why <paramref name="doc"/> cannot be opened, or null when it can: it holds no kernel object (and then
    /// <paramref name="capability"/> is never asked, so the kernel is not even started), or the kernel is here.
    /// </summary>
    public static string? RefusalOnOpen(C3dDocument doc, Func<GeometryKernelCapability> capability)
    {
        var use = Of(doc);
        if (use.Count == 0) return null;
        var cap = capability();
        return cap.Available ? null : Refusal(use, cap);
    }

    /// <summary>The heading of the refusal on open.</summary>
    public const string CannotOpen = "This 3D view cannot be opened.";

    // ── references through an operation (R-em3d64-2f) ──────────────────────────────────────────

    /// <summary>
    /// A reference to face <paramref name="face"/> of top-level object <paramref name="obj"/>, as it resolves in the
    /// elaborated problem: unchanged while every operation on the way is enabled; with a disabled Boolean,
    /// <c>(lid, bore:side)</c> becomes <c>(bore, side)</c> — the standalone Tool — and <c>(lid, zmax)</c> stays on
    /// <c>lid</c>, which is then the Blank under the boolean's name. Nothing in the file changes.
    /// </summary>
    public static (string Object, string Face) Resolve(C3dDocument doc, string obj, string face)
    {
        var top = doc.Objects.FirstOrDefault(o => o.Name == obj);
        return top is null ? (obj, face) : Resolve(top, obj, face);
    }

    private static (string Object, string Face) Resolve(C3dObject o, string name, string face)
    {
        // A disabled wrapper elaborates what it wraps under its own name, so a bare face is the inner object's.
        if (o is C3dOperation { Enabled: false })
        {
            if (o is C3dBoolean b && face.IndexOf(':') is int colon and > 0)
            {
                string tool = face[..colon];
                if (b.Tools.FirstOrDefault(t => t.Name == tool) is { } t) return Resolve(t, t.Name, face[(colon + 1)..]);
            }
            if (C3dOperands.Inner(o) is { } inner) return Resolve(inner, name, face);
        }
        return (name, face);
    }

    /// <summary>
    /// The fold rule (brief 40 §1e, R-em3d64-2a): a reference to <paramref name="face"/> covers the face of that name and
    /// every piece an operation split it into — <c>zmax</c> matches <c>zmax</c>, <c>zmax#1</c> and <c>zmax#2</c>.
    /// </summary>
    public static bool Covers(string face, string candidate)
        => candidate == face || CircuitRF.Engine.Em3d.Em3dFaceGeometry.IsPiece(face, candidate);
}
