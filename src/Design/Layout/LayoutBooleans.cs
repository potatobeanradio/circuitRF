// Boolean/offset/repair operations built on LayoutClipper (docs/design/layout-view.md §6.1, L1e brief
// §3/§4). Framework-free — pure geometry + the layer/net attribute rules; no undo, no selection, no
// Messages. LayoutEditorViewModel is the only caller that turns a result into a
// ReplaceShapesCommand.

using Clipper2Lib;

namespace CircuitRF.Design.Layout;

/// <summary>One boolean/offset/repair result. <see cref="Shapes"/> may be empty (a legal outcome — an
/// intersection with no overlap, or an over-shrunk offset). <see cref="AnyCurvedOperand"/> is true
/// when at least one operand needed flattening (§3.2 R9e — "warn once per session", a session-level
/// concern the caller owns). <see cref="NetsDiffered"/> is true when the operands did not all share a
/// net, in which case <see cref="Shapes"/> already carries a cleared (null) <c>Net</c> rather than an
/// arbitrarily picked one (§3.4 R10a).</summary>
public readonly record struct LayoutBooleanResult(
    IReadOnlyList<LayoutShape> Shapes,
    bool AnyCurvedOperand,
    bool NetsDiffered);

public static class LayoutBooleans
{
    // ── Public operations ──────────────────────────────────────────────────────

    /// <summary>All operands merged.</summary>
    public static LayoutBooleanResult Union(IReadOnlyList<LayoutShape> operands, Technology? tech) =>
        Combine(ClipType.Union, operands, tech);

    /// <summary>Common region of all operands.</summary>
    public static LayoutBooleanResult Intersect(IReadOnlyList<LayoutShape> operands, Technology? tech) =>
        Combine(ClipType.Intersection, operands, tech);

    /// <summary>First operand minus every other operand, in selection order.</summary>
    public static LayoutBooleanResult Difference(IReadOnlyList<LayoutShape> operands, Technology? tech) =>
        Combine(ClipType.Difference, operands, tech);

    /// <summary>Symmetric difference of all operands.</summary>
    public static LayoutBooleanResult Xor(IReadOnlyList<LayoutShape> operands, Technology? tech) =>
        Combine(ClipType.Xor, operands, tech);

    /// <summary>Union restricted to shapes sharing a layer, applied per layer — one
    /// <see cref="LayoutBooleanResult"/> per distinct layer among <paramref name="operands"/>, in the
    /// order that layer's first member appears.</summary>
    public static IReadOnlyList<(LayerKey Layer, LayoutBooleanResult Result, IReadOnlyList<LayoutShape> GroupOperands)> Merge(
        IReadOnlyList<LayoutShape> operands, Technology? tech)
    {
        var order = new List<LayerKey>();
        var groups = new Dictionary<LayerKey, List<LayoutShape>>();
        foreach (var shape in operands)
        {
            if (!groups.TryGetValue(shape.Layer, out var list))
            {
                groups[shape.Layer] = list = [];
                order.Add(shape.Layer);
            }
            list.Add(shape);
        }

        var results = new List<(LayerKey, LayoutBooleanResult, IReadOnlyList<LayoutShape>)>(order.Count);
        foreach (var layer in order)
        {
            var group = groups[layer];
            results.Add((layer, Combine(ClipType.Union, group, tech), group));
        }
        return results;
    }

    /// <summary>Signed offset of one shape's own geometry outline — positive grows, negative shrinks.
    /// An over-shrink annihilates the shape (empty <see cref="LayoutBooleanResult.Shapes"/>), which is
    /// legal and must be reported, not thrown.</summary>
    public static LayoutBooleanResult Offset(LayoutShape shape, long deltaDbu, Technology? tech)
    {
        long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
        var paths = LayoutClipper.ToClipperPaths(shape, tol);
        var offset = Clipper.InflatePaths(paths, deltaDbu, JoinType.Miter, EndType.Polygon);

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, offset, new Paths64(), tree, LayoutClipper.Rule);
        var shapes = LayoutClipper.FromClipperTree(tree, shape.Layer, shape.Net);
        return new LayoutBooleanResult(shapes, IsCurved(shape), NetsDiffered: false);
    }

    /// <summary>Self-intersection repair (§4 of the brief): a Clipper2 <c>Union</c> of the single
    /// shape against nothing, <c>NonZero</c>, which resolves crossings into a clean simple result —
    /// possibly several pieces, or one with holes.</summary>
    public static LayoutBooleanResult Repair(LayoutShape shape, Technology? tech)
    {
        long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
        var paths = LayoutClipper.ToClipperPaths(shape, tol);
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, new Paths64(), tree, LayoutClipper.Rule);
        var shapes = LayoutClipper.FromClipperTree(tree, shape.Layer, shape.Net);
        return new LayoutBooleanResult(shapes, IsCurved(shape), NetsDiffered: false);
    }

    /// <summary>True when a shape's Clipper2 conversion needs the flattener — §3.2 R9e's "curved
    /// operands are flattened" warning trigger. A plain <c>Rect</c>/<c>Polygon</c> never does.</summary>
    public static bool IsCurved(LayoutShape shape) =>
        shape is CircleShape or RoundedRectShape or CurveShape or PathShape;

    // ── Shared n-ary fold ──────────────────────────────────────────────────────

    /// <summary>Pairwise-folds <paramref name="clipType"/> across every operand — <c>A op B op C …</c>
    /// in selection order. This generalizes correctly to N operands for every op the brief lists:
    /// Union/Intersection are associative, Xor (symmetric difference) is associative, and Difference
    /// folded left-to-right is exactly "first minus the rest."</summary>
    private static LayoutBooleanResult Combine(ClipType clipType, IReadOnlyList<LayoutShape> operands, Technology? tech)
    {
        if (operands.Count == 0) return new LayoutBooleanResult([], false, false);

        var layer = operands[0].Layer;
        string? net = operands[0].Net;
        bool anyCurved = IsCurved(operands[0]);
        bool netsDiffered = false;
        for (int i = 1; i < operands.Count; i++)
        {
            if (IsCurved(operands[i])) anyCurved = true;
            if (operands[i].Net != net) netsDiffered = true;
        }
        if (netsDiffered) net = null;

        Paths64 acc = LayoutClipper.ToClipperPaths(operands[0], LayoutFlattener.ResolveTolDbu(operands[0], tech));

        var tree = new PolyTree64();
        if (operands.Count == 1)
        {
            Clipper.BooleanOp(clipType, acc, new Paths64(), tree, LayoutClipper.Rule);
        }
        // ── UNION REDUCES AS A BALANCED TREE, NOT A RUNNING ACCUMULATOR ─────────────────────────
        //
        // The linear fold below is REQUIRED for Intersection, Difference and Xor — Difference in
        // particular is not commutative, so those operands must be applied in selection order, one at
        // a time. But the SHAPE of that fold is what makes it quadratic: every step runs a full
        // BooleanOp against an accumulator that has already absorbed everything before it, so operand
        // number N is clipped against a result carrying N-1 operands' worth of contours.
        //
        // That is invisible on the handful of shapes a user selects by hand, and fatal on the case
        // this codebase's OWN advice sends here. The Gerber importer tells the user, by name, that a
        // vector-filled pour "arrived as N separate strokes ... use the editor's Merge action to turn
        // them into one region" — and on the owner's 4-up panel that is 46,721 strokes on one copper
        // layer. The linear fold ran for over forty minutes on it without finishing. Reduced as a
        // balanced tree the same union completes in about forty seconds, which is what makes the
        // import's advice something a user can actually act on.
        //
        // Union is associative, so pairing (A∪B)∪(C∪D) instead of ((A∪B)∪C)∪D is the same region —
        // this is a change of ORDER, not of semantics, and it deliberately keeps every step a real
        // pairwise BooleanOp. Concatenating every operand into one subject set and resolving it in a
        // single call was tried first and is WRONG: under NonZero a hole contour from one operand
        // cancels another operand's fill where they overlap, so a shape union that should have closed
        // a hole punches one instead. PcbImportTests' custom-pad cases caught exactly that ("is one
        // unioned region" came back as two), which is why the pairing below unions two resolved
        // regions at a time and never a raw pile of contours.
        else if (clipType == ClipType.Union)
        {
            var level = new List<Paths64>(operands.Count) { acc };
            for (int i = 1; i < operands.Count; i++)
                level.Add(LayoutClipper.ToClipperPaths(operands[i], LayoutFlattener.ResolveTolDbu(operands[i], tech)));

            while (level.Count > 2)
            {
                var next = new List<Paths64>((level.Count + 1) / 2);
                for (int i = 0; i < level.Count; i += 2)
                {
                    if (i + 1 == level.Count) next.Add(level[i]);   // odd one out rides to the next level
                    else next.Add(Clipper.BooleanOp(ClipType.Union, level[i], level[i + 1], LayoutClipper.Rule));
                }
                level = next;
            }

            // The last pair goes through the PolyTree overload, exactly as the linear fold's final
            // step does — that is where hole nesting is resolved for FromClipperTree.
            Clipper.BooleanOp(ClipType.Union, level[0], level.Count > 1 ? level[1] : new Paths64(), tree, LayoutClipper.Rule);
        }
        else
        {
            for (int i = 1; i < operands.Count - 1; i++)
            {
                var next = LayoutClipper.ToClipperPaths(operands[i], LayoutFlattener.ResolveTolDbu(operands[i], tech));
                acc = Clipper.BooleanOp(clipType, acc, next, LayoutClipper.Rule);
            }
            var last = LayoutClipper.ToClipperPaths(operands[^1], LayoutFlattener.ResolveTolDbu(operands[^1], tech));
            Clipper.BooleanOp(clipType, acc, last, tree, LayoutClipper.Rule);
        }

        var shapes = LayoutClipper.FromClipperTree(tree, layer, net);
        return new LayoutBooleanResult(shapes, anyCurved, netsDiffered);
    }
}
