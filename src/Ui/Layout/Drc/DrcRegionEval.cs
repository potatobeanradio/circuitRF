// Evaluating a DrcLayerExpr to a region (docs/design/layout-view.md §9A.5).
//
// Every operation funnels through Clipper2 over the same integer DBU coordinates the rest of the
// layout uses — §6.1's single conversion point, unchanged. This file adds no second geometry
// notion; it composes the one that already exists.

using Clipper2Lib;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// Evaluates <see cref="DrcLayerExpr"/> against the per-layer regions a run has already built.
///
/// <para>One instance per DRC run. Sub-expression results are memoized for the lifetime of that
/// run: a deck routinely measures several rules against the same derived layer (metal minus a
/// keep-out region can appear in a dozen rules), and re-deriving it per rule is the difference between
/// a check that runs once and one that runs once per rule. The cache is keyed by the expression
/// itself — <see cref="DrcLayerExpr"/> is a record tree, so two structurally identical expressions
/// are equal and share an entry with no hand-written key to get wrong.</para>
/// </summary>
internal sealed class DrcRegionEval(
    IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
    IReadOnlySet<LayerKey>?                definedLayers = null)
{
    /// <summary>
    /// How far apart two regions may be and still count as touching, for the topological
    /// selections.
    ///
    /// <para><b>This is not a tolerance in the usual sense and must not be raised.</b> Clipper2
    /// reports the intersection of two regions that share only an EDGE as empty — zero area is
    /// still zero — so a strict area test would report two abutting polygons as not interacting,
    /// which is the opposite of what every deck means by the word. Testing against a one-DBU
    /// dilation makes edge contact register. At the default resolution one DBU is a nanometre,
    /// far below any drawn feature, so nothing that is genuinely apart is caught by it.</para>
    /// </summary>
    private const double TouchDilationDbu = 1.0;

    /// <summary>Miter limit for <see cref="DrcLayerExpr.Sized"/>. Matches the width check's own
    /// erosion, so a sized derived layer and the measurement of it agree about corners.</summary>
    private const double MiterLimit = 2.0;

    private readonly Dictionary<DrcLayerExpr, Paths64> _cache = new();

    /// <summary>
    /// Layers named by an expression that the TECHNOLOGY does not define — not merely layers this
    /// design has no geometry on. Accumulated across every evaluation and reported once per run.
    /// </summary>
    public HashSet<LayerKey> MissingLayers { get; } = [];

    /// <summary>How many sub-expressions were evaluated rather than served from the cache.
    /// Test-only instrumentation — there is no other way to observe that memoization is working.</summary>
    internal int EvaluatedNodeCount { get; private set; }

    public Paths64 Evaluate(DrcLayerExpr expr)
    {
        if (_cache.TryGetValue(expr, out var hit)) return hit;

        EvaluatedNodeCount++;
        var result = Compute(expr);
        _cache[expr] = result;
        return result;
    }

    private Paths64 Compute(DrcLayerExpr expr)
    {
        switch (expr)
        {
            case DrcLayerExpr.Layer l:
                if (layerRegions.TryGetValue(l.Key, out var region)) return region;

                // Either way the layer contributes NOTHING rather than failing the rule outright: a
                // deck written for a full process names layers a simpler technology omits, and
                // refusing those rules would hide the ones that ARE evaluable.
                //
                // But the two reasons are NOT the same fact and must not be reported as one. A layer
                // the technology defines that this design simply has no geometry on is entirely
                // normal and unremarkable — most layers of a real process are empty in any one cell.
                // Reporting those as "not defined in the technology" told users their technology was
                // broken when it was fine: measured on a real process, 31 layers were named that way
                // and every single one was defined. Only a genuinely UNDEFINED layer is recorded.
                if (definedLayers is null || !definedLayers.Contains(l.Key))
                    MissingLayers.Add(l.Key);

                return [];

            case DrcLayerExpr.And a:
                return Boolean(ClipType.Intersection, Evaluate(a.A), Evaluate(a.B));

            case DrcLayerExpr.Or o:
                return Boolean(ClipType.Union, Evaluate(o.A), Evaluate(o.B));

            case DrcLayerExpr.Not n:
                return Boolean(ClipType.Difference, Evaluate(n.A), Evaluate(n.B));

            case DrcLayerExpr.Xor x:
                return Boolean(ClipType.Xor, Evaluate(x.A), Evaluate(x.B));

            case DrcLayerExpr.Sized s:
            {
                var src = Evaluate(s.A);
                if (src.Count == 0 || s.ByDbu == 0) return src;
                var grown = Clipper.InflatePaths(src, s.ByDbu, JoinType.Miter, EndType.Polygon, MiterLimit);
                // A shrink can dissolve a region entirely; that is a legitimate empty result.
                return grown.Count == 0 ? [] : DrcRegions.Union(grown);
            }

            case DrcLayerExpr.Select sel:
                return Selection(Evaluate(sel.A), Evaluate(sel.B), sel.Op);

            case DrcLayerExpr.Holes h:
                return HolesOf(Evaluate(h.A));

            case DrcLayerExpr.Merged m:
                return DrcRegions.Union(Evaluate(m.A));

            case DrcLayerExpr.WithArea wa:
                return SelectByMeasure(Evaluate(wa.A), wa.MinDbu2, wa.MaxDbu2, AreaOfComponent);

            case DrcLayerExpr.WithPerimeter wp:
                return SelectByMeasure(Evaluate(wp.A), wp.MinDbu, wp.MaxDbu, PerimeterOfComponent);

            default:
                throw new NotSupportedException($"Unhandled expression node {expr.GetType().Name}.");
        }
    }

    private static Paths64 Boolean(ClipType op, Paths64 a, Paths64 b)
    {
        // Short-circuits that are correct rather than merely fast: Clipper2 handles empty operands,
        // but naming the identities here keeps a chain of derived layers over a sparse design from
        // walking the clipper for every step.
        if (a.Count == 0)
            return op switch
            {
                ClipType.Union or ClipType.Xor => DrcRegions.Union(b),
                _ => [],   // Intersection and Difference with an empty A are both empty.
            };

        if (b.Count == 0)
            return op switch
            {
                ClipType.Intersection => [],
                _ => DrcRegions.Union(a),   // Union, Xor and Difference all leave A alone.
            };

        return Clipper.BooleanOp(op, a, b, LayoutClipper.Rule);
    }

    /// <summary>
    /// Whole-polygon selection. Splits A into connected components and keeps or drops each one
    /// INTACT — partial area is never returned, which is the whole difference between
    /// <c>interacting</c> and <c>and</c>.
    /// </summary>
    private static Paths64 Selection(Paths64 a, Paths64 b, DrcSelectOp op)
    {
        if (a.Count == 0) return [];

        // With an empty B every relation collapses to a constant, and saying so explicitly avoids
        // a per-component clipper call that would return the same answer.
        if (b.Count == 0)
            return op switch
            {
                DrcSelectOp.NotInteracting or DrcSelectOp.Outside or DrcSelectOp.NotCovering
                    => DrcRegions.Union(a),
                _ => [],
            };

        var touchB = op is DrcSelectOp.Interacting or DrcSelectOp.NotInteracting or DrcSelectOp.Outside
            ? Clipper.InflatePaths(b, TouchDilationDbu, JoinType.Miter, EndType.Polygon, MiterLimit)
            : b;

        var bComponents = op is DrcSelectOp.Covering or DrcSelectOp.NotCovering
            ? DrcRegions.Components(b)
            : null;

        var kept = new Paths64();

        foreach (var component in DrcRegions.Components(a))
        {
            if (Keeps(component, b, touchB, bComponents, op))
                kept.AddRange(component);
        }

        return kept;
    }

    private static bool Keeps(
        Paths64 component,
        Paths64 b,
        Paths64 touchB,
        List<Paths64>? bComponents,
        DrcSelectOp op)
    {
        switch (op)
        {
            case DrcSelectOp.Interacting:
                return !IsEmpty(Clipper.BooleanOp(ClipType.Intersection, component, touchB, LayoutClipper.Rule));

            case DrcSelectOp.NotInteracting:
                return IsEmpty(Clipper.BooleanOp(ClipType.Intersection, component, touchB, LayoutClipper.Rule));

            case DrcSelectOp.Inside:
                // Entirely within B iff nothing of it survives subtracting B.
                return IsEmpty(Clipper.BooleanOp(ClipType.Difference, component, b, LayoutClipper.Rule));

            case DrcSelectOp.Outside:
                // Stricter than "not inside": no overlap AND no shared edge.
                return IsEmpty(Clipper.BooleanOp(ClipType.Intersection, component, touchB, LayoutClipper.Rule));

            case DrcSelectOp.Covering:
                return bComponents is not null && bComponents.Exists(bc =>
                    IsEmpty(Clipper.BooleanOp(ClipType.Difference, bc, component, LayoutClipper.Rule)));

            case DrcSelectOp.NotCovering:
                return bComponents is null || !bComponents.Exists(bc =>
                    IsEmpty(Clipper.BooleanOp(ClipType.Difference, bc, component, LayoutClipper.Rule)));

            default:
                throw new NotSupportedException($"Unhandled select op {op}.");
        }
    }

    /// <summary>
    /// True when a Clipper2 result encloses no area.
    ///
    /// <para>A path count of zero is the common case, but not the only one: a difference that
    /// cancels exactly can leave degenerate rings with zero area. Testing area rather than count
    /// is what makes <c>inside</c> report correctly for a polygon that exactly coincides with
    /// its container.</para>
    /// </summary>
    private static bool IsEmpty(Paths64 paths)
    {
        if (paths.Count == 0) return true;
        double area = 0;
        foreach (var p in paths) area += Clipper.Area(p);
        return Math.Abs(area) < 1.0;   // Less than one square DBU is not a region.
    }

    /// <summary>
    /// Keeps whole components whose measured value falls in [min, max]. A null bound is open.
    ///
    /// <para>Whole components, like every other selection here — a polygon either survives intact
    /// or is dropped. Bounds are INCLUSIVE at both ends, which matches how a deck states them: a
    /// minimum-area rule of 100 permits a shape of exactly 100.</para>
    /// </summary>
    private static Paths64 SelectByMeasure(
        Paths64 region, long? min, long? max, Func<Paths64, double> measure)
    {
        if (region.Count == 0) return [];

        var kept = new Paths64();
        foreach (var component in DrcRegions.Components(region))
        {
            double v = measure(component);
            if (min.HasValue && v < min.Value) continue;
            if (max.HasValue && v > max.Value) continue;
            kept.AddRange(component);
        }
        return kept;
    }

    /// <summary>Enclosed area of one component, holes subtracted.</summary>
    internal static double AreaOfComponent(Paths64 component)
    {
        double sum = 0;
        foreach (var p in component) sum += Clipper.Area(p);
        return Math.Abs(sum);
    }

    /// <summary>Total boundary length of one component, holes included.</summary>
    internal static double PerimeterOfComponent(Paths64 component)
    {
        double sum = 0;
        foreach (var path in component)
        {
            for (int i = 0; i < path.Count; i++)
            {
                var a = path[i];
                var b = path[(i + 1) % path.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                sum += Math.Sqrt(dx * dx + dy * dy);
            }
        }
        return sum;
    }

    /// <summary>The holes of a region, returned as solids.</summary>
    private static Paths64 HolesOf(Paths64 region)
    {
        if (region.Count == 0) return [];

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, region, [], tree, LayoutClipper.Rule);

        var holes = new Paths64();
        Collect(tree);
        return holes;

        void Collect(PolyPath64 node)
        {
            for (int i = 0; i < node.Count; i++)
            {
                var child = node[i];
                if (child.IsHole && child.Polygon is { } ring) holes.Add(ring);
                Collect(child);
            }
        }
    }
}
