// Turning artwork into the regions the two checks actually operate on (docs/design/layout-view.md
// §9A.1, "a geometry engine"). Everything here goes through LayoutClipper/LayoutFlattener — §6.1's
// single conversion point — so DRC checks the same flattened geometry the booleans, the mesher and
// every exporter see, never a second approximation of it (R16c).

using Clipper2Lib;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>
/// A conductor: one electrically-distinct region of metal on one layer, as the SPACING check must
/// see it. §9A.1 is specific about why this type exists — spacing must look "for intersections
/// between shapes on DIFFERENT nets (§3.4 R10a is what makes this correct rather than a flood of
/// false positives on a single copper pour)".
/// </summary>
/// <param name="Net">
/// The net name, or null for geometry that states none. A null net is NOT a wildcard: see
/// <see cref="DrcRegions.BuildConductors"/> for how unnamed geometry is grouped, and why grouping it
/// any other way makes the check useless on the artwork people actually draw.
/// </param>
/// <param name="Paths">The region's outline(s), Clipper2 form, DBU.</param>
/// <param name="Bounds">Bounding box, kept so the pairwise sweep can reject most pairs without work.</param>
internal sealed record DrcConductor(string? Net, Paths64 Paths, Bbox Bounds);

internal static class DrcRegions
{
    /// <summary>
    /// Shapes that carry no manufacturable area and are therefore invisible to DRC. Deliberately a
    /// method rather than a type list at each call site: <c>BitmapShape</c>'s own doc comment already
    /// promises "L5b: skipped by DRC", and a label is drafting text — flattening glyph outlines and
    /// checking their stroke width against a metal rule would report nothing anyone wants.
    /// A <c>ViaShape</c> is NOT excluded; it is decomposed — see <see cref="Expand"/>.
    /// </summary>
    internal static bool IsCheckable(LayoutShape shape) => shape is not (LabelShape or BitmapShape);

    /// <summary>
    /// Expands one shape into the (layer, Paths64) contributions DRC sees.
    ///
    /// <para>Every ordinary primitive contributes once, on its own layer, through
    /// <see cref="LayoutClipper.ToClipperPaths"/>. A <see cref="ViaShape"/> contributes TWICE and this
    /// is not a DRC decision — it reuses the decomposition every exporter already applies (R-via-9):
    /// the barrel is a circle of <see cref="ViaShape.DrillSize"/> on <see cref="LayoutShape.Layer"/>,
    /// the pad a circle of <see cref="ViaShape.PadSize"/> on <see cref="ViaShape.LandingLayer"/> when
    /// one is set. Checking a via against a metal rule on the layer its pad actually lands on is the
    /// only reading that means anything, and inventing a different one here would make DRC and export
    /// disagree about what a via IS.</para>
    /// </summary>
    /// <param name="tolCapFor">
    /// The coarsest flattening a given layer's own rules can tolerate — see
    /// <see cref="ResolveCheckingTol"/>. A layer with no rules never reaches the checks, so any cap
    /// is fine for it.
    /// </param>
    internal static void Expand(
        LayoutShape shape, Technology? tech,
        Func<LayerKey, long> tolCapFor,
        Action<LayerKey, string?, Paths64> emit)
    {
        if (!IsCheckable(shape)) return;

        if (shape is ViaShape via)
        {
            if (via.DrillSize > 0)
                emit(via.Layer, via.Net, Disc(via.X, via.Y, via.DrillSize / 2,
                                              ResolveCheckingTol(null, tech, tolCapFor(via.Layer))));

            if (via is { LandingLayer: { } landing, PadSize: > 0 })
                emit(landing, via.Net, Disc(via.X, via.Y, via.PadSize / 2,
                                            ResolveCheckingTol(null, tech, tolCapFor(landing))));
            return;
        }

        long tol = ResolveCheckingTol(shape, tech, tolCapFor(shape.Layer));
        var paths = LayoutClipper.ToClipperPaths(shape, tol);
        if (paths.Count > 0) emit(shape.Layer, shape.Net, paths);
    }

    private static Paths64 Disc(long cx, long cy, long r, long tol) =>
        LayoutClipper.RingsToClipperPaths(
            [LayoutFlattener.Flatten(new CircleShape { Cx = cx, Cy = cy, R = r }, tol)[0]]);

    /// <summary>
    /// The tolerance a curve is flattened at FOR CHECKING — the shape's own resolved tolerance
    /// (R16c: the same <c>ToClipperPaths</c> helper as everything else, so DRC checks what ships),
    /// but never coarser than the rules being applied to that layer can tolerate.
    ///
    /// <para><b>The cap is not defensive tidying; without it the check is wrong.</b>
    /// <c>LayoutFlattener.ResolveTolDbu</c> falls back to a fixed 1 µm when neither the shape nor the
    /// technology states a tolerance — a sane default for DRAWING, and six times the minimum-width
    /// rule of a 130 nm process. Measured before the cap existed: a via pad on such a technology
    /// flattened to a TRIANGLE, whose corners are genuinely narrower than the rule, and the check
    /// dutifully reported three violations on a perfectly legal pad. The flattening error has to be
    /// small compared to the quantity being measured, or the check is measuring its own
    /// approximation. Same reasoning <c>LayoutHitTest</c> already applies to a click tolerance.</para>
    /// </summary>
    internal static long ResolveCheckingTol(LayoutShape? shape, Technology? tech, long capDbu)
    {
        long tol = shape is null
            ? (tech is { DefaultFlattenTolDbu: > 0 } ? tech.DefaultFlattenTolDbu : LayoutFlattener.DefaultTolDbu)
            : LayoutFlattener.ResolveTolDbu(shape, tech);

        return Math.Max(1, Math.Min(tol, Math.Max(1, capDbu)));
    }

    /// <summary>
    /// How much finer than a rule its layer's curves must be flattened. A sixteenth keeps the
    /// chord error well under the one- and two-DBU margins the width check itself works to, while
    /// staying coarse enough that a large curve on a coarse-ruled layer does not explode into
    /// vertices — a board's 6 mil rule still permits a ~9500 DBU sagitta.
    /// </summary>
    internal const long ToleranceFractionOfRule = 16;

    /// <summary>
    /// Groups one layer's geometry into conductors for the spacing check.
    ///
    /// <para><b>Named nets union as one</b>, however many shapes and however far apart — two pads of
    /// the same net 5 µm apart are not a spacing violation, they are one net.</para>
    ///
    /// <para><b>Unnamed geometry is grouped by CONNECTIVITY</b>, one conductor per connected
    /// component. Both simpler readings are wrong in ways that make the check worthless on real
    /// artwork: treating all unnamed shapes as one net silently passes every board drawn before L5
    /// stamped any nets (the ordinary case for hand-drawn artwork), and treating each unnamed shape
    /// as its own net reports a violation for every pair of overlapping rectangles a pour is drawn
    /// from — which is §9A.1's named failure mode, arrived at from the other direction. Connectivity
    /// is what the net attribute would have said if anyone had stamped it, and it is one union away.</para>
    ///
    /// <para>An unnamed shape touching a NAMED one stays in the unnamed pool; it is not adopted into
    /// the named net. Inferring net identity across that boundary is §9A.3's connectivity extraction
    /// — the thing DRC is being built first in order to make possible — and guessing at it here would
    /// put an unverified inference underneath a check whose whole value is that it does not guess.</para>
    /// </summary>
    internal static List<DrcConductor> BuildConductors(IReadOnlyList<(string? Net, Paths64 Paths)> onLayer)
    {
        var conductors = new List<DrcConductor>();

        // Named nets: one conductor each, in first-appearance order (determinism — no dictionary
        // enumeration order anywhere in a result the UI lists and a test asserts on).
        var namedOrder = new List<string>();
        var named      = new Dictionary<string, Paths64>(StringComparer.Ordinal);
        var unnamed    = new Paths64();

        foreach (var (net, paths) in onLayer)
        {
            if (net is { Length: > 0 })
            {
                if (!named.TryGetValue(net, out var acc)) { named[net] = acc = []; namedOrder.Add(net); }
                acc.AddRange(paths);
            }
            else unnamed.AddRange(paths);
        }

        foreach (var net in namedOrder)
        {
            var union = Union(named[net]);
            if (union.Count > 0) conductors.Add(new DrcConductor(net, union, BoundsOf(union)));
        }

        foreach (var component in Components(unnamed))
            conductors.Add(new DrcConductor(null, component, BoundsOf(component)));

        return conductors;
    }

    /// <summary>Union of everything handed in, as a flat path set.</summary>
    internal static Paths64 Union(Paths64 paths) =>
        paths.Count == 0 ? [] : Clipper.BooleanOp(ClipType.Union, paths, [], LayoutClipper.Rule);

    /// <summary>
    /// Splits a path set into its connected components — one entry per top-level solid, carrying its
    /// own holes, with any island nested inside a hole returned as its own component. Same walk (and
    /// the same determinism argument) as <see cref="LayoutClipper.FromClipperTree"/>; it returns
    /// <c>LayoutShape</c>s and this returns Clipper2 paths, which is the whole difference.
    /// </summary>
    internal static List<Paths64> Components(Paths64 paths)
    {
        var result = new List<Paths64>();
        if (paths.Count == 0) return result;

        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Union, paths, [], tree, LayoutClipper.Rule);
        Collect(tree, result);
        return result;
    }

    private static void Collect(PolyPath64 node, List<Paths64> into)
    {
        for (int i = 0; i < node.Count; i++)
        {
            var solid = node[i];                 // IsHole == false at this level

            // Clipper2 declares PolyPath64.Polygon nullable (the tree's own root carries none), so a
            // null is skipped rather than dereferenced — a missing outer ring cannot bound a region.
            var component = new Paths64();
            if (solid.Polygon is { } outer) component.Add(outer);

            for (int j = 0; j < solid.Count; j++)
            {
                var hole = solid[j];
                if (hole.Polygon is { } ring) component.Add(ring);
                Collect(hole, into);             // islands inside this hole are their own conductors
            }

            if (component.Count > 0) into.Add(component);
        }
    }

    internal static Bbox BoundsOf(Paths64 paths)
    {
        var b = Bbox.Empty;
        foreach (var path in paths)
            foreach (var pt in path)
                b = b.Union(new Bbox(pt.X, pt.Y, pt.X, pt.Y));
        return b;
    }

    /// <summary>Bounding box grown by <paramref name="by"/> on every side — the pairwise sweep's
    /// cheap rejection test.</summary>
    internal static Bbox Grow(Bbox b, long by) =>
        b.IsEmpty ? b : new Bbox(b.MinX - by, b.MinY - by, b.MaxX + by, b.MaxY + by);

    /// <summary>Converts a Clipper2 path set back to the flat DBU rings a
    /// <see cref="DrcViolation"/> carries.</summary>
    internal static long[][] ToRings(Paths64 paths)
    {
        var rings = new long[paths.Count][];
        for (int i = 0; i < paths.Count; i++)
        {
            var p = paths[i];
            var xy = new long[p.Count * 2];
            for (int k = 0; k < p.Count; k++) { xy[2 * k] = p[k].X; xy[2 * k + 1] = p[k].Y; }
            rings[i] = xy;
        }
        return rings;
    }
}
