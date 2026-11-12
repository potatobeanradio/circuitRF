// brief-em3d-67 — the rules a fillet or a chamfer is MADE by, in the editor and anywhere else: what may be rounded and why
// not, the object that is made (it wraps its target and takes its place and name), how it is removed, the chain of features
// on one solid, and the sentences a refusal is said in. Pure functions of the document's objects, as C3dBooleans is for
// brief 66: the editor, its tree, its inspector and its tests all call these, so no rule is written twice.
//
// A FEATURE WRAPS ITS TARGET (overview §1f, R-em3d67-5e). A Fillet or Chamfer holds its target inline and takes its NAME and
// its place in construction order; the target inside has none of its own, so every port, face boundary and wire that
// referred to the target's faces still does — the target's faces keep their names, and the faces the operation makes are
// fillet(<edge>) and chamfer(<edge>). A second Fillet… on the same solid wraps again: it never merges silently into the
// first, whose edge list is edited in Properties.
//
// ONE FEATURE OF A CHAIN IS ENABLED AT A TIME (3D editor round 5). A solid may carry any number of fillets and chamfers as
// alternatives: a new one is made enabled with every other switched off, and enabling one switches the others off, each in
// the same undo entry (SoleEnabled). A file holding a chain with several enabled — written before the rule — still reads
// and builds as it always did, each rounding the one inside it; the rule is the editor's, not the reader's.
//
// AN EDGE IS NAMED, NEVER NUMBERED BY INDEX (em-3d.md §6.4). An edge name resolves through an edit by the fold rule on both
// sides (the worker's EdgeLookup, R-em3d67-2d); one that resolves to nothing is a refusal naming it and the face it lost,
// and is never mapped to a nearby edge by guess.

using System.Globalization;
using CircuitRF.Design.ThreeD.Occ;

namespace CircuitRF.Design.ThreeD;

/// <summary>Which of the two edge operations.</summary>
public enum C3dEdgeOp { Fillet, Chamfer }

public static class C3dFillets
{
    // ── what may be rounded (R-em3d67-5a) ────────────────────────────────────────────────────────

    /// <summary>Edges of more than one object are selected.</summary>
    public static string OneObject(C3dEdgeOp op) => op == C3dEdgeOp.Fillet
        ? "A fillet rounds one solid: select edges of one object."
        : "A chamfer bevels one solid: select edges of one object.";

    /// <summary>No edge is selected.</summary>
    public static string SelectEdges(C3dEdgeOp op)
        => $"Select the edges to {(op == C3dEdgeOp.Fillet ? "round" : "bevel")}: Edge mode (E), then click; Shift-click adds.";

    /// <summary>A sheet's edge.</summary>
    public const string SheetRefused = "A sheet has no volume to round.";

    /// <summary>brief-em3d-102 R-em3d102-3d — a sphere: one closed face, so Edge mode finds nothing to pick on it.</summary>
    public static string SphereRefused(C3dEdgeOp op)
        => $"A sphere has no edges to {(op == C3dEdgeOp.Fillet ? "round" : "bevel")}: its surface is one closed face.";

    /// <summary>Why <paramref name="o"/>'s edges cannot be filleted or chamfered, or null when they can: a solid of brief 66
    /// §1a's kinds — Box, Prism, Cylinder, Polyhedron, Boolean, Step, or a solid already filleted or chamfered.</summary>
    public static string? TargetRefusal(C3dObject o) => o switch
    {
        C3dSheet or C3dCylinderSheet => SheetRefused,
        _ when C3dSheetBooleans.IsSheetBoolean(o) => SheetRefused,
        C3dSphere => $"'{o.Name}' is a sphere: its surface is one closed face, with no edge to round or bevel.",
        C3dEllipsoid => $"'{o.Name}' is an ellipsoid: its surface is one closed face, with no edge to round or bevel.",
        C3dPolyline => $"'{o.Name}' is a polyline — construction geometry, never in the problem: Extrude it first.",
        C3dWire => $"'{o.Name}' is a bond wire: its shape is made again from its points and its pads, so a rounded wire would stop being a wire.",
        _ when C3dOperands.IsSolid(o) => null,
        _ => $"'{o.Name}' is not a solid.",
    };

    // ── making and unmaking (R-em3d67-5e, -6b) ────────────────────────────────────────────────────

    /// <summary>
    /// A Fillet of <paramref name="edges"/> of <paramref name="target"/> (COPIED) by <paramref name="radius"/> DBU. It takes the
    /// target's name; the target inside keeps its material, role and placement and no name of its own.
    /// </summary>
    public static C3dFillet MakeFillet(C3dObject target, IReadOnlyList<string> edges, long radius)
    {
        var t = C3dBooleans.Copy(target);
        string name = t.Name;
        t.Name = "";
        t.Hidden = false;
        t.Group = null;
        t.Transparency = null;
        t.Appearance = null;
        t.Model = true;
        return new C3dFillet { Name = name, Hidden = target.Hidden, Group = target.Group, Transparency = target.Transparency, Appearance = target.Appearance, Model = target.Model, Radius = radius, Edges = [.. edges], Target = t };
    }

    /// <summary>A Chamfer, as <see cref="MakeFillet"/>: <paramref name="distance2"/> 0 is a symmetric chamfer.</summary>
    public static C3dChamfer MakeChamfer(C3dObject target, IReadOnlyList<string> edges, long distance, long distance2)
    {
        var t = C3dBooleans.Copy(target);
        string name = t.Name;
        t.Name = "";
        t.Hidden = false;
        t.Group = null;
        t.Transparency = null;
        t.Appearance = null;
        t.Model = true;
        return new C3dChamfer { Name = name, Hidden = target.Hidden, Group = target.Group, Transparency = target.Transparency, Appearance = target.Appearance, Model = target.Model, Distance = distance, Distance2 = distance2, Edges = [.. edges], Target = t };
    }

    /// <summary>R-em3d67-6b — Remove: the feature's target back in its place under its name, carried by the feature's
    /// placement (the identity, as a feature is made).</summary>
    public static C3dObject Unwrap(C3dOperation feature)
    {
        var inner = C3dOperands.Inner(feature) ?? throw new ArgumentException(null, nameof(feature));
        var c = C3dBooleans.Carried(inner, feature);
        c.Name = feature.Name;
        c.Hidden = feature.Hidden;
        c.Group = feature.Group;
        c.Transparency = feature.Transparency;
        c.Appearance = feature.Appearance;
        c.Model = feature.Model;
        return c;
    }

    /// <summary>A feature's edge list, and a copy of it with that list replaced.</summary>
    public static IReadOnlyList<string> EdgesOf(C3dObject feature) => feature switch
    {
        C3dFillet f => f.Edges,
        C3dChamfer c => c.Edges,
        _ => [],
    };

    public static void SetEdges(C3dObject feature, IReadOnlyList<string> edges)
    {
        switch (feature)
        {
            case C3dFillet f: f.Edges = [.. edges]; break;
            case C3dChamfer c: c.Edges = [.. edges]; break;
        }
    }

    /// <summary>A Fillet or a Chamfer.</summary>
    public static bool IsFeature(C3dObject o) => o is C3dFillet or C3dChamfer;

    // ── the chain of features on one solid (R-em3d67-6a) ─────────────────────────────────────────

    /// <summary>
    /// The features wrapping <paramref name="o"/>, OUTERMOST first, each with its path under <paramref name="o"/> — "" for
    /// <paramref name="o"/> itself, "Target." for the one inside it, "Target.Target." …
    /// </summary>
    public static IReadOnlyList<(string Path, C3dOperation Feature)> Chain(C3dObject o)
    {
        var list = new List<(string, C3dOperation)>();
        string path = "";
        var at = o;
        while (at is C3dFillet or C3dChamfer)
        {
            var op = (C3dOperation)at;
            list.Add((path, op));
            at = C3dOperands.Inner(op);
            path += "Target.";
            if (at is null) break;
        }
        return list;
    }

    /// <summary>3D editor round 5 — a copy of <paramref name="top"/> with the feature at <paramref name="path"/> the one enabled
    /// and every other feature of its chain disabled; a null path disables them all (what a new feature wraps).</summary>
    public static C3dObject SoleEnabled(C3dObject top, string? path)
    {
        var copy = C3dBooleans.Copy(top);
        foreach (var (p, f) in Chain(copy)) f.Enabled = p == path;
        return copy;
    }

    /// <summary>What <paramref name="o"/>'s features round: the object under every Fillet and Chamfer, and its path.</summary>
    public static (C3dObject? Core, string Path) Core(C3dObject o)
    {
        var chain = Chain(o);
        return chain.Count == 0 ? (o, "") : (C3dOperands.Inner(chain[^1].Feature), chain[^1].Path + "Target.");
    }

    /// <summary>The object at <paramref name="path"/> ("", "Target.", "Target.Target." …) under <paramref name="top"/>, or null.</summary>
    public static C3dObject? At(C3dObject top, string path)
    {
        C3dObject? o = top;
        for (int at = 0; at < path.Length; at += "Target.".Length)
        {
            if (string.CompareOrdinal(path, at, "Target.", 0, "Target.".Length) != 0) return null;
            o = o is C3dFillet or C3dChamfer ? C3dOperands.Inner(o) : null;
            if (o is null) return null;
        }
        return o;
    }

    /// <summary>A copy of <paramref name="top"/> with the object at <paramref name="path"/> (a Target path) replaced.</summary>
    public static C3dObject With(C3dObject top, string path, C3dObject replacement)
    {
        if (path.Length == 0) return replacement;
        var copy = C3dBooleans.Copy(top);
        var parent = At(copy, path[..^"Target.".Length]) ?? throw new ArgumentException(null, nameof(path));
        switch (parent)
        {
            case C3dFillet f: f.Target = replacement; break;
            case C3dChamfer c: c.Target = replacement; break;
            default: throw new ArgumentException(null, nameof(path));
        }
        return copy;
    }

    /// <summary>
    /// R-em3d67-6e — the world form of what a chain of DISABLED features rounds, under the chain's name: what a face or
    /// vertex edit acts on while the features are off. Null when <paramref name="top"/> is no chain, a feature on it is
    /// enabled, or what it rounds is itself a kernel object (that one is edited through its operands).
    /// </summary>
    public static C3dObject? DisabledCore(C3dObject top)
    {
        var chain = Chain(top);
        if (chain.Count == 0 || chain.Any(f => f.Feature.Enabled)) return null;
        var (core, _) = Core(top);
        if (core is null || C3dOperands.IsKernel(core)) return null;
        var w = C3dBooleans.Copy(core);
        for (int k = chain.Count - 1; k >= 0; k--)
            if (!chain[k].Feature.Placement.IsDefault) w.Placement = w.Placement.Then(chain[k].Feature.Placement.ToTransform(), out _);
        w.Name = top.Name;
        w.Hidden = top.Hidden;
        w.Group = top.Group;
        w.Transparency = top.Transparency;
        w.Appearance = top.Appearance?.Clone();
        w.Model = top.Model;
        return w;
    }

    /// <summary>The chain <paramref name="top"/> with <paramref name="world"/> — an edited <see cref="DisabledCore"/> — back
    /// inside it: its placement relative to the features again, and no name of its own.</summary>
    public static C3dObject WithCore(C3dObject top, C3dObject world)
    {
        var chain = Chain(top);
        var local = C3dBooleans.Copy(world);
        var t = C3dTransform.Identity;
        foreach (var (_, f) in chain)
            if (!f.Placement.IsDefault) t = t.Then(f.Placement.ToTransform());
        if (t != C3dTransform.Identity) local.Placement = local.Placement.Then(t.Inverse(), out _);
        local.Name = "";
        local.Hidden = false;
        local.Group = null;
        local.Transparency = null;
        local.Appearance = null;
        local.Model = true;
        return With(top, Core(top).Path, local);
    }

    // ── the words ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The undo entry's words: <c>Fillet 4 edges of lid</c>, <c>Chamfer 1 edge of lid</c>.</summary>
    public static string Description(C3dEdgeOp op, int edges, string target)
        => $"{op} {edges} edge{(edges == 1 ? "" : "s")} of {target}";

    /// <summary>A feature row's label: <c>Fillet 50 µm — 4 edges</c>; a disabled one says so.</summary>
    public static string RowLabel(C3dOperation feature, Func<long, string> length)
    {
        int n = EdgesOf(feature).Count;
        string size = feature switch
        {
            C3dFillet f => $"Fillet {length(f.Radius)}",
            C3dChamfer { Distance2: > 0 } c => $"Chamfer {length(c.Distance)} × {length(c.Distance2)}",
            C3dChamfer c => $"Chamfer {length(c.Distance)}",
            _ => "",
        };
        return $"{size} — {n} edge{(n == 1 ? "" : "s")}";
    }

    /// <summary>R-em3d67-6d — a face reference to a feature's own face while that feature is disabled.</summary>
    public static string OnlyWhileEnabled(string face, string obj, C3dObject feature)
        => $"'{face}' of '{obj}' exists only while its {(feature is C3dChamfer ? "chamfer" : "fillet")} is enabled.";

    /// <summary>R-em3d67-6e — a face or vertex edit on a rounded solid, with what to edit instead.</summary>
    public static string RoundedNotEditable(string name, C3dObject obj)
    {
        var (core, _) = Core(obj);
        string word = obj is C3dChamfer ? "chamfer" : "fillet";
        string kind = core is null ? "solid" : C3dObject.KindOf(core).ToLowerInvariant();
        return $"'{name}' is {(obj is C3dChamfer ? "bevelled by a chamfer" : "rounded by a fillet")}: edit the {kind} " +
               $"(disable the {word}, or change it in Properties).";
    }

    /// <summary>
    /// R-em3d67-5d / -6c — a kernel refusal of a fillet or chamfer on <paramref name="obj"/>, in the user's terms: the edge that
    /// does not fit and the width beside it, the corner that cannot be blended, or the edge that no longer exists and the face
    /// it lost. <paramref name="size"/> is the radius (or first distance) as the user reads it; <paramref name="microns"/>
    /// spells a width in the display unit. Null when the refusal names no edge (the caller shows the kernel's own sentence).
    /// </summary>
    public static string? Refusal(GeometryKernelException e, C3dEdgeOp op, string obj, string size, Func<double, string> microns)
    {
        string word = op == C3dEdgeOp.Fillet ? "fillet" : "chamfer";
        if (e.Code == "edge.missing" && e.Edges is [var gone, ..])
        {
            string why = e.Missing.Count switch
            {
                0 => "",
                1 => $": its face '{e.Missing[0]}' was removed",
                _ => $": its faces {string.Join(" and ", e.Missing.Select(m => $"'{m}'"))} were removed",
            };
            return $"Edge '{gone}' of '{obj}' no longer exists{why}. Edit the {word}'s edges.";
        }
        if (e.Edges.Count == 0) return null;
        if (e.Corner)
            return $"The kernel cannot blend the corner where {Quoted(e.Edges)} meet. " +
                   $"{(op == C3dEdgeOp.Fillet ? "Fillet" : "Chamfer")} them one at a time, or at a smaller size.";
        string noun = op == C3dEdgeOp.Fillet ? "radius" : "distance";
        if (e.WidthUm is { } w and > 0)
            return $"A {size} {noun} does not fit edge '{e.Edges[0]}': the faces beside it are {microns(w)} wide. Try less than {microns(w)}.";
        return $"A {size} {noun} does not fit edge '{e.Edges[0]}'.";
    }

    /// <summary>
    /// <see cref="Refusal"/> for a kernel refusal of <paramref name="top"/> (a chain of features, named <paramref name="name"/>):
    /// the feature it concerns is the one listing the edge refused (else the outermost), its size spelled in
    /// <paramref name="unit"/>. Null when the refusal names no edge or <paramref name="top"/> is no chain.
    /// </summary>
    public static string? Worded(GeometryKernelException e, C3dObject top, string name, Layout.LayoutUnit unit, int dbuPerMicron)
    {
        var chain = Chain(top);
        if (chain.Count == 0) return null;
        var feature = chain.Select(c => c.Feature).FirstOrDefault(f => e.Edges.Count > 0 && EdgesOf(f).Contains(e.Edges[0])) ?? chain[0].Feature;
        long size = feature switch { C3dFillet f => f.Radius, C3dChamfer c => c.Distance, _ => 0 };
        string spelled = Layout.LayoutUnits.Format(size, unit, dbuPerMicron) + " " + Layout.LayoutUnits.Suffix(unit);
        return Refusal(e, feature is C3dChamfer ? C3dEdgeOp.Chamfer : C3dEdgeOp.Fillet, name, spelled, um => Microns(um, unit, dbuPerMicron));
    }

    private static string Quoted(IReadOnlyList<string> names)
    {
        var q = names.Select(n => $"'{n}'").ToList();
        return q.Count <= 1 ? string.Concat(q) : string.Join(", ", q.Take(q.Count - 1)) + " and " + q[^1];
    }

    /// <summary>A width in micrometres, in <paramref name="unit"/>, for <see cref="Refusal"/>: rounded to what the display
    /// unit shows, since the kernel's number is a measurement, not a document value.</summary>
    public static string Microns(double um, Layout.LayoutUnit unit, int dbuPerMicron)
        => Layout.LayoutUnits.Format((long)Math.Round(um * dbuPerMicron), unit, dbuPerMicron, 2) + " " + Layout.LayoutUnits.Suffix(unit);
}
