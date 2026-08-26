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
