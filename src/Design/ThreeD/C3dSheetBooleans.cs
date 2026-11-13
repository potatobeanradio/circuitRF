// brief-hzn-10 — a Boolean of coplanar SHEETS: a notched patch, a stub filter's united trace, a ground plane with a cut-out,
// every operand still sized by its own expressions.
//
// WHAT IT IS (R-hzn10-1). A Boolean whose Blank and every Tool are sheets — a Sheet, or a sheet Boolean, at any depth — that all
// lie in ONE plane: the same Plane, and the same Offset once resolved. An operand's placement may move it within that plane and
// do nothing else; the boolean's own placement is unrestricted. The result is a sheet: it conducts, it takes the Blank's
// material, role and ThicknessUm (the innermost Blank's, the Sheet that carries them, Carrier), and it names no faces.
//
// HOW IT IS EVALUATED (R-hzn10-2). Here, exactly, with no geometry worker: every operand resolved to its polygon in DBU in the
// plane's (u, v), combined with the layout code's own Clipper2 conversion (LayoutClipper), integer throughout. Nothing is
// computed into the file; the document keeps every operand's Rect or Outline and its expressions, so a variable moves the result.
//
// A sheet Boolean is therefore NOT a kernel object (C3dOperands.IsKernel) and NOT a solid (C3dOperands.IsSolid): the kernel
// is never asked, and a document holding only sheet booleans opens without the worker.

using Clipper2Lib;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.ThreeD;

/// <summary>One piece of a sheet boolean's result: an outline and its holes, DBU, in the plane's (u, v).</summary>
public sealed record C3dSheetPiece(IReadOnlyList<C3dPoint2> Outline, IReadOnlyList<IReadOnlyList<C3dPoint2>> Holes);

/// <summary>A sheet boolean's result before its own placement: the plane, the offset along its normal (DBU), and the pieces.
/// No pieces is an empty result.</summary>
public sealed record C3dSheetResult(C3dPlane Plane, long Offset, IReadOnlyList<C3dSheetPiece> Pieces);

public static class C3dSheetBooleans
{
    // ── what is a sheet ─────────────────────────────────────────────────────────────────────────

    /// <summary>A Boolean whose Blank and every Tool are sheets (a Sheet or a sheet Boolean), enabled or not.</summary>
    public static bool IsSheetBoolean(C3dObject o)
        => o is C3dBoolean { Blank: { } blank } b && IsSheet(blank) && b.Tools.All(IsSheet);

    /// <summary>Whether <paramref name="o"/> is a flat sheet to every reader that asks: a Sheet, or a sheet Boolean. A sheet on a
    /// cylinder is a sheet of another kind and is not one of these.</summary>
    public static bool IsSheet(C3dObject o) => o is C3dSheet || IsSheetBoolean(o);

    /// <summary>The Sheet that carries <paramref name="o"/>'s material, role and thickness: itself, or a sheet Boolean's innermost
    /// Blank. Null for anything else.</summary>
    public static C3dSheet? Carrier(C3dObject o) => o switch
    {
        C3dSheet s => s,
        C3dBoolean { Blank: { } blank } when IsSheetBoolean(o) => Carrier(blank),
        _ => null,
    };

    /// <summary>A Boolean with a sheet among its operands and something that is not a sheet beside it, or null: the sheet
    /// operand's label and the other's, for <see cref="C3dDiagnostics.SheetBooleanMixed"/>.</summary>
    public static (string Sheet, string Other, string OtherKind)? Mixed(C3dBoolean b, string label)
    {
        var operands = C3dOperands.Of(b).ToList();
        var sheet = operands.FirstOrDefault(p => IsSheet(p.Operand));
        var other = operands.FirstOrDefault(p => !IsSheet(p.Operand));
        if (sheet.Operand is null || other.Operand is null) return null;
        return (Label(sheet.Operand, sheet.Prefix, label), Label(other.Operand, other.Prefix, label), C3dObject.KindOf(other.Operand));
    }

    /// <summary>An operand as a sentence names it: its own name, or where it sits (<c>Patch.Blank</c>).</summary>
    public static string Label(C3dObject operand, string prefix, string parent)
        => operand.Name.Length > 0 ? operand.Name : parent + "." + prefix.TrimEnd('.');

    // ── one plane (R-hzn10-1) ───────────────────────────────────────────────────────────────────

    /// <summary>The plane a sheet or sheet Boolean lies in: its carrier's.</summary>
    public static C3dPlane PlaneOf(C3dObject o) => Carrier(o)?.Plane ?? C3dPlane.XY;

    /// <summary>The offset along the plane's normal, DBU, a sheet or sheet Boolean lies at: its carrier's. A placement on the way
    /// down adds nothing along the normal, or it is refused (<see cref="PlacementFault"/>).</summary>
    public static long OffsetOf(C3dObject o) => Carrier(o)?.Offset ?? 0;

    /// <summary>A placement's translation in the plane's (u, v, normal), DBU.</summary>
    public static (long U, long V, long N) InPlane(C3dPlane plane, C3dPoint3 t) => plane switch
    {
        C3dPlane.YZ => (t.Y, t.Z, t.X),
        C3dPlane.XZ => (t.X, t.Z, t.Y),
        _ => (t.X, t.Y, t.Z),
    };

    /// <summary>
    /// What stops <paramref name="b"/> — a sheet Boolean — lying in one plane, at this level and every level below: an operand in
    /// another plane or at another offset, or one whose placement would move it out of the plane. Each is (operand, why),
    /// worded to follow the operand's name. <paramref name="deep"/> false answers for this level alone.
    /// </summary>
    public static List<(string Operand, string Why)> PlaneFaults(C3dBoolean b, string label, bool deep = true)
    {
        var found = new List<(string, string)>();
        if (b.Blank is not { } blank) return found;
        var plane = PlaneOf(blank);
        long offset = OffsetOf(blank);
        foreach (var (prefix, child) in C3dOperands.Of(b))
        {
            string where = Label(child, prefix, label);
            if (PlacementFault(child, plane) is { } moved) found.Add((where, moved));
            else if (!ReferenceEquals(child, blank))
            {
                if (PlaneOf(child) != plane)
                    found.Add((where, $"lies in the {PlaneOf(child)} plane and the Blank in {plane}"));
                else if (OffsetOf(child) != offset)
                    found.Add((where, $"lies at another offset along {Normal(plane)} than the Blank"));
            }
            if (deep && child is C3dBoolean inner && IsSheetBoolean(inner)) found.AddRange(PlaneFaults(inner, where));
        }
        return found;
    }

    /// <summary>Why <paramref name="operand"/>'s placement takes it out of <paramref name="plane"/>, or null when it moves it
    /// within the plane (or not at all).</summary>
    public static string? PlacementFault(C3dObject operand, C3dPlane plane)
    {
        var pl = operand.Placement;
        if (pl.MirrorX) return "is mirrored; an operand of a sheet boolean may only be moved within its plane";
        if (pl.Rotate.Count > 0) return "is rotated; an operand of a sheet boolean may only be moved within its plane";
        if (InPlane(plane, pl.Origin).N != 0) return $"is moved along {Normal(plane)}, out of its plane";
        return null;
    }

    /// <summary>The axis a plane's normal runs along.</summary>
    public static string Normal(C3dPlane plane) => plane switch { C3dPlane.YZ => "x", C3dPlane.XZ => "y", _ => "z" };

    /// <summary>
    /// Why <paramref name="selected"/> — two or more objects about to become a boolean's operands — cannot be a sheet boolean,
    /// or null when they can: they must all be sheets (the caller has checked that), in one plane at one offset, each placed
    /// within it.
    /// </summary>
    public static string? SelectionRefusal(IReadOnlyList<C3dObject> selected)
    {
        if (selected.Count == 0) return null;
        var plane = PlaneOf(selected[0]);
        long offset = OffsetOf(selected[0]);
        foreach (var o in selected)
        {
            if (PlacementFault(o, PlaneOf(o)) is { } moved) return $"'{o.Name}' {moved}.";
            if (PlaneOf(o) != plane)
                return $"'{o.Name}' lies in the {PlaneOf(o)} plane and '{selected[0].Name}' in {plane}: sheets are combined only in one plane.";
            if (OffsetOf(o) != offset)
                return $"'{o.Name}' and '{selected[0].Name}' lie at different offsets along {Normal(plane)}: sheets are combined only in one plane.";
        }
        return null;
    }

    // ── the result (R-hzn10-2) ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="b"/>'s result in its own frame (before its own placement): every operand's polygon combined in DBU. A
    /// DISABLED Boolean below the top is its Blank in its place (its Tools elaborate as objects of their own, as a disabled
    /// solid Boolean's do). The caller has checked <see cref="PlaneFaults"/>.
    /// </summary>
    public static C3dSheetResult Result(C3dBoolean b)
    {
        var region = Region(b);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, region, new Paths64(), tree, LayoutClipper.Rule);
        var pieces = LayoutClipper.FromClipperTree(tree, default, null)
            .OfType<PolygonShape>()
            .Select(p => new C3dSheetPiece(Ring(p.Xy), [.. (p.Holes ?? []).Select(h => (IReadOnlyList<C3dPoint2>)Ring(h))]))
            .ToList();
        return new C3dSheetResult(PlaneOf(b), OffsetOf(b), pieces);
    }

    /// <summary>The region an operand covers in its parent's frame, as positive outlines and negative holes.</summary>
    private static Paths64 Region(C3dObject o)
    {
        var plane = PlaneOf(o);
        var (du, dv, _) = InPlane(plane, o.Placement.Origin);
        Paths64 own;
        if (o is C3dBoolean b)
        {
            own = Region(b.Blank!);
            if (b.Enabled)
            {
                var op = b.Op switch { C3dBooleanOp.Unite => ClipType.Union, C3dBooleanOp.Intersect => ClipType.Intersection, _ => ClipType.Difference };
                foreach (var t in b.Tools) own = Clipper.BooleanOp(op, own, Region(t), LayoutClipper.Rule);
            }
        }
        else own = SheetPaths((C3dSheet)o);
        if (du == 0 && dv == 0) return own;
        var moved = new Paths64(own.Count);
        foreach (var path in own)
        {
            var p = new Path64(path.Count);
            foreach (var q in path) p.Add(new Point64(q.X + du, q.Y + dv));
            moved.Add(p);
        }
        return moved;
    }

    /// <summary>A sheet's own polygon: its rectangle, its ellipse's ring, or its outline and holes, through the layout code's conversion (which
    /// orients the outline positive and every hole negative).</summary>
    private static Paths64 SheetPaths(C3dSheet s)
    {
        PolygonShape shape;
        if (s.Ellipse is { } e)
        {
            // brief-hzn-13 — an ellipse takes part as the polygon it is built as, everywhere else too.
            var ring = C3dEllipses.Ring(e);
            if (ring.Count < 3) return [];
            shape = new PolygonShape { Xy = Flat(ring) };
        }
        else if (s.Rect is { } r)
        {
            long u0 = Math.Min(r.Min.U, r.Min.U + r.Size.U), u1 = Math.Max(r.Min.U, r.Min.U + r.Size.U);
            long v0 = Math.Min(r.Min.V, r.Min.V + r.Size.V), v1 = Math.Max(r.Min.V, r.Min.V + r.Size.V);
            shape = new PolygonShape { Xy = [u0, v0, u1, v0, u1, v1, u0, v1] };
        }
        else
        {
            if (s.Outline.Count < 3) return [];
            shape = new PolygonShape
            {
                Xy = Flat(s.Outline),
                Holes = s.Holes.Count == 0 ? null : [.. s.Holes.Where(h => h.Count >= 3).Select(Flat)],
            };
        }
        // An area of zero (a rectangle with no width) covers nothing: Clipper returns nothing for it either.
        return LayoutClipper.ToClipperPaths(shape, 1);
    }

    private static long[] Flat(List<C3dPoint2> ring)
    {
        var xy = new long[ring.Count * 2];
        for (int i = 0; i < ring.Count; i++) (xy[2 * i], xy[2 * i + 1]) = (ring[i].U, ring[i].V);
        return xy;
    }

    private static List<C3dPoint2> Ring(long[] xy)
    {
        var ring = new List<C3dPoint2>(xy.Length / 2);
        for (int i = 0; i + 1 < xy.Length; i += 2) ring.Add(new C3dPoint2(xy[i], xy[i + 1]));
        return ring;
    }

    /// <summary>The name of piece <paramref name="k"/> (from 0) of an object named <paramref name="name"/>: the first is the
    /// object's own name, every later one <c>name#k+1</c>. Each carries the object's provenance, so whatever names a piece
    /// names the object.</summary>
    public static string PieceName(string name, int k) => k == 0 ? name : $"{name}#{k + 1}";
}
