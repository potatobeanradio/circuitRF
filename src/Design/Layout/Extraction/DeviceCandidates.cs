// WHAT A PIECE OF COPPER LOOKS LIKE — brief-lvs-14-recognition.md R-lvs14-3, docs/design/lvs.md
// §4.1 tier 3.
//
//   shapes ──► LayerRegions ──► DrcRegionEval(Body) ──► components ──► one candidate each
//                                     │                                      │
//                                     └─ DrcRegionEval(Terminals) ──► the pieces that TOUCH it
//
// ── IT LIVES HERE AND NOT IN Lvs/ FOR SubCellContact's REASON ─────────────────────────────────
//
// It calls Clipper, and geometry belongs to Extraction and Drc — the rule `LayoutRead`'s own header
// states and `tests/Ui.Tests/Lvs/LayoutNetlistTests.cs` gate 15 holds as a source scan over every
// file under `src/Design/Layout/Lvs/`. What is in Lvs/ is `DeviceRecognition`, which decides what a
// candidate MEANS; nothing in it touches a path.
//
// ── THE REGION EVALUATOR IS THE DRC's, UNCHANGED (R-lvs14-2a) ────────────────────────────────
//
// `Body` and each `Terminals` entry are `DrcLayerExpr`s and are evaluated by `DrcRegionEval` —
// the same object, with the same memoization and the same "this layer is not in the technology"
// accounting a DRC run uses. A second layer grammar would be a second grammar to document, test
// and get wrong, and a second EVALUATOR would be a second answer to "what region is this".
//
// ── AND THE MEASUREMENTS ARE THE ONES THAT ALREADY EXIST, PLUS ONE ───────────────────────────
//
// Area and Perimeter are `DrcRegionEval`'s own component measurements, called rather than
// re-derived. Length and Width have no existing answer, so there is one here: the MINIMUM-AREA
// enclosing rectangle, by rotating calipers over the convex hull. It is exact for a rectangle at
// any rotation — which is what a drawn resistor body is — well defined for any polygon, and it
// yields the principal axis as a direction rather than as a guess about which way is "along".

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>
/// One terminal of a recognised device — <b>where it is, so a net can be looked up under it</b>.
/// </summary>
/// <param name="Slot">Which of the rule's terminal expressions produced it, 0-based. The rule's own
/// layer order, which orders terminals before position does (R-lvs14-3b).</param>
/// <param name="X">DBU, a point inside the terminal's own copper.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layers">The drawing layers its expression reads, in the expression's own order —
/// what the point lookup is tried on. <b>Never "any layer"</b>: a pad takes the identity of the
/// piece it lands on, and an any-layer query would answer with whatever sits underneath it.</param>
/// <param name="WidthDbu">The terminal's smaller extent, for a marker.</param>
/// <param name="Along">Its position along the body's principal axis, DBU — the tie-break within
/// one slot.</param>
public readonly record struct DeviceTerminalCandidate(
    int Slot, long X, long Y, IReadOnlyList<LayerKey> Layers, long WidthDbu, double Along);

/// <summary>
/// One connected component of a rule's <c>Body</c> region, measured — <b>a candidate device</b>
/// (R-lvs14-3a).
/// </summary>
/// <remarks>
/// <b>Everything is a plain number or a ring list.</b> No Clipper type crosses this boundary, which
/// is what lets the file that decides what a candidate MEANS live under <c>Lvs/</c> at all.
/// </remarks>
/// <param name="X">DBU, the body's centre — what its derived path is spelled from (R-lvs14-3e).</param>
/// <param name="Y">DBU.</param>
/// <param name="Bounds">The body's bounding box.</param>
/// <param name="AreaDbu2">Enclosed area, square DBU, holes subtracted.</param>
/// <param name="PerimeterDbu">Total boundary length, DBU, holes included.</param>
/// <param name="LengthDbu">The longer side of the minimum-area enclosing rectangle.</param>
/// <param name="WidthDbu">Its shorter side.</param>
/// <param name="AxisAmbiguous">
/// True where the two sides are within <see cref="SquareFraction"/> of each other, so which way is
/// "along" is not decidable from the shape — R-lvs14-3c, and the caller must not guess.
/// </param>
/// <param name="Terminals">Every terminal piece touching this body, in the rule's layer order and
/// then by position along the principal axis.</param>
/// <param name="Rings">The body's own outline, in the DRC marker convention.</param>
public sealed record DeviceCandidate(
    long X, long Y, Bbox Bounds,
    double AreaDbu2, double PerimeterDbu, double LengthDbu, double WidthDbu, bool AxisAmbiguous,
    IReadOnlyList<DeviceTerminalCandidate> Terminals,
    IReadOnlyList<long[]> Rings);

/// <summary>The geometric half of tier-3 recognition — R-lvs14-3.</summary>
public static class DeviceCandidates
{
    /// <summary>
    /// How close to square a body may be before its principal axis is called undecidable.
    /// </summary>
    /// <remarks>
    /// <b>Two percent, and the tolerance is the point rather than the number.</b> A resistor read
    /// the wrong way round is off by (L/W)², silently — so the honest answer near square is "this
    /// shape does not say", and a threshold small enough to pass every deliberately elongated body
    /// is all that is needed. A drawn 10:1 NiCr is nowhere near it.
    /// </remarks>
    public const double SquareFraction = 0.02;

    /// <summary>
    /// How far apart a terminal and a body may be and still count as touching, DBU.
    /// </summary>
    /// <remarks>
    /// <b><see cref="DrcRegionEval"/>'s own <c>TouchDilationDbu</c>, for its own reason.</b>
    /// Clipper reports the intersection of two regions sharing only an EDGE as empty, and a
    /// terminal that abuts a resistor body without overlapping it is the ordinary drawn case — the
    /// strict test would recognise nothing at all.
    /// </remarks>
    private const double TouchDilationDbu = 1.0;

    private const double MiterLimit = 2.0;

    /// <summary>
    /// Every candidate device <paramref name="body"/> finds in <paramref name="copper"/>, with its
    /// terminals and its measurements.
    /// </summary>
    /// <param name="copper">The shapes recognition may see — R-lvs14-3d's subset, already chosen by
    /// the caller. Nothing here decides what an instance owns.</param>
    /// <param name="tech">Supplies the expansion and the layer table every reading of this board's
    /// copper shares.</param>
    /// <param name="body">The rule's body expression.</param>
    /// <param name="terminals">The rule's terminal expressions, in the rule's own order.</param>
    /// <param name="missingLayers">Layers the expressions name that the TECHNOLOGY does not define
    /// — not merely layers this design has nothing on. <see cref="DrcRegionEval"/>'s own
    /// distinction, carried out rather than re-derived.</param>
    public static IReadOnlyList<DeviceCandidate> Find(
        IReadOnlyList<LayoutShape> copper, Technology? tech,
        DrcLayerExpr body, IReadOnlyList<DrcLayerExpr> terminals,
        out IReadOnlyCollection<LayerKey> missingLayers)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(terminals);

        missingLayers = [];
        if (tech is null || copper is not { Count: > 0 }) return [];

        // One union per layer, through the expansion every other reader uses — so a via decomposes
        // into a barrel and a landing pad here exactly as it does in the DRC and in the partition.
        //
        // EVERY layer, not only the electrical ones: a `Body` may name a dielectric, which is what
        // the deck's own `MIM Metal AND Nitride` does. The electrical drop exists so a soldermask
        // opening does not join the copper under it, and that is a question about CONNECTIVITY —
        // asking it here would make the shipped example recognise nothing, silently. The terminals
        // still reach their nets through the partition, which does apply it.
        var layerRegions = LayerRegions.Build(copper, tech, null, electricalOnly: false);
        if (layerRegions.Count == 0) return [];

        var eval = new DrcRegionEval(layerRegions, tech.Layers.Select(l => l.Key).ToHashSet());

        var bodies = DrcRegions.Components(eval.Evaluate(body));

        // Each terminal expression's own components, once — a rule is evaluated against the whole
        // design and then matched against each body, not re-evaluated per body.
        //
        // EVALUATED EVEN WHERE THE BODY FOUND NOTHING, which costs a boolean over an empty region
        // and buys a complete `missingLayers`: a rule naming a layer the technology does not define
        // must come back sayable whichever half of it named the missing layer, or the caller reports
        // "no terminals" about a rule that was never applicable to this process.
        var terminalPieces = new List<(int Slot, Paths64 Component, IReadOnlyList<LayerKey> Layers)>();
        for (int slot = 0; slot < terminals.Count; slot++)
        {
            var layers = terminals[slot].ReferencedLayers().ToArray();
            foreach (var component in DrcRegions.Components(eval.Evaluate(terminals[slot])))
                terminalPieces.Add((slot, component, layers));
        }

        missingLayers = eval.MissingLayers;
        if (bodies.Count == 0) return [];

        var candidates = new List<DeviceCandidate>(bodies.Count);
        foreach (var component in bodies)
        {
            var bounds = BoundsOf(component);
            var (length, width, axisX, axisY) = MinimumAreaRectangle(component);

            var touching = new List<DeviceTerminalCandidate>();
            var grown = Clipper.InflatePaths(
                component, TouchDilationDbu, JoinType.Miter, EndType.Polygon, MiterLimit);

            foreach (var (slot, piece, layers) in terminalPieces)
            {
                var pieceBounds = BoundsOf(piece);
                if (!pieceBounds.Intersects(bounds) && !Grazes(pieceBounds, bounds)) continue;
                if (Clipper.Intersect(grown, piece, LayoutClipper.Rule).Count == 0) continue;

                var (px, py) = InteriorPointOf(piece, pieceBounds);
                touching.Add(new DeviceTerminalCandidate(
                    slot, px, py, layers,
                    Math.Min(pieceBounds.MaxX - pieceBounds.MinX, pieceBounds.MaxY - pieceBounds.MinY),
                    px * axisX + py * axisY));
            }

            // R-lvs14-3b's order: the rule's own layer order first, then position ALONG the
            // principal axis — so a two-terminal device's terminals come back the way the artwork
            // reads, left to right along the body, however the body is rotated.
            touching.Sort((a, b) =>
            {
                int bySlot = a.Slot.CompareTo(b.Slot);
                if (bySlot != 0) return bySlot;
                int byAlong = a.Along.CompareTo(b.Along);
                return byAlong != 0 ? byAlong : (a.X, a.Y).CompareTo((b.X, b.Y));
            });

            candidates.Add(new DeviceCandidate(
                (bounds.MinX + bounds.MaxX) / 2, (bounds.MinY + bounds.MaxY) / 2, bounds,
                DrcRegionEval.AreaOfComponent(component),
                DrcRegionEval.PerimeterOfComponent(component),
                length, width,
                length > 0 && (length - width) <= SquareFraction * length,
                touching,
                DrcRegions.ToRings(component)));
        }

        // Deterministic, and by the artwork's own coordinates: two runs over unchanged geometry
        // produce the same list in the same order, which is what makes a coordinate-derived path
        // stable across runs (R-lvs3-1c).
        candidates.Sort((a, b) =>
        {
            int byX = a.X.CompareTo(b.X);
            return byX != 0 ? byX : a.Y.CompareTo(b.Y);
        });

        return candidates;
    }

    /// <summary>Whether two boxes are close enough to be worth an exact test — the dilation's own
    /// slack, so an abutting terminal survives the broad phase that rejects everything else.</summary>
    private static bool Grazes(Bbox a, Bbox b)
        => !a.IsEmpty && !b.IsEmpty
        && a.MinX - 1 <= b.MaxX && b.MinX - 1 <= a.MaxX
        && a.MinY - 1 <= b.MaxY && b.MinY - 1 <= a.MaxY;

    /// <summary>
    /// A point inside <paramref name="component"/> — what the net lookup is performed at.
    /// </summary>
    /// <remarks>
    /// The centre first, because a drawn pad is convex and its centre is inside it. Where it is not
    /// — an L, a ring, a pad with a hole through it — a small grid over the bounding box is
    /// scanned, which is bounded and needs no second geometry notion. <c>Regions.Contains</c>
    /// decides, so the answer agrees with the partition's own containment test rather than with a
    /// second one.
    /// </remarks>
    private static (long X, long Y) InteriorPointOf(Paths64 component, Bbox bounds)
    {
        long cx = (bounds.MinX + bounds.MaxX) / 2, cy = (bounds.MinY + bounds.MaxY) / 2;
        if (Regions.Contains(component, cx, cy)) return (cx, cy);

        const int Steps = 7;
        for (int i = 1; i < Steps; i++)
        for (int j = 1; j < Steps; j++)
        {
            long x = bounds.MinX + (bounds.MaxX - bounds.MinX) * i / Steps;
            long y = bounds.MinY + (bounds.MaxY - bounds.MinY) * j / Steps;
            if (Regions.Contains(component, x, y)) return (x, y);
        }

        return (cx, cy);   // reported as "on no copper" by the caller, which is the honest answer
    }

    /// <summary>
    /// The minimum-area enclosing rectangle: its longer side, its shorter side, and the UNIT
    /// direction of the longer one — the body's principal axis (R-lvs14-3c).
    /// </summary>
    /// <remarks>
    /// <b>Rotating calipers over the convex hull</b>, which is the standard result that the
    /// minimum-area rectangle has a side collinear with a hull edge: so each hull edge is tried as
    /// the frame, and the smallest area wins. It is EXACT for a rectangle at any angle, including
    /// one the flatten turned into a polygon, and it never needs to know which way is up.
    ///
    /// <para>An axis-aligned bounding box would have been one line and would be wrong for exactly
    /// the artwork this is for: a NiCr resistor drawn at 30° measures longer and much wider than it
    /// is, and R = ρ·L/W would come out low by a factor of several with nothing saying so.</para>
    /// </remarks>
    private static (double Length, double Width, double AxisX, double AxisY)
        MinimumAreaRectangle(Paths64 component)
    {
        var hull = ConvexHull(component);
        if (hull.Count < 2)
        {
            var box = BoundsOf(component);
            double w = box.MaxX - box.MinX, h = box.MaxY - box.MinY;
            return w >= h ? (w, h, 1, 0) : (h, w, 0, 1);
        }

        double bestArea = double.MaxValue, bestLength = 0, bestWidth = 0, bestX = 1, bestY = 0;

        for (int i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) continue;

            double ux = dx / len, uy = dy / len;
            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;

            foreach (var p in hull)
            {
                double u = p.X * ux + p.Y * uy;
                double v = -p.X * uy + p.Y * ux;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            double su = maxU - minU, sv = maxV - minV;
            double area = su * sv;
            if (area >= bestArea) continue;

            bestArea = area;
            if (su >= sv) { bestLength = su; bestWidth = sv; bestX = ux;  bestY = uy; }
            else          { bestLength = sv; bestWidth = su; bestX = -uy; bestY = ux; }
        }

        return bestArea == double.MaxValue
            ? (0, 0, 1, 0)
            : (bestLength, bestWidth, bestX, bestY);
    }

    /// <summary>The convex hull of every vertex of <paramref name="component"/>, counter-clockwise
    /// — Andrew's monotone chain, which needs nothing but a sort.</summary>
    private static List<Point64> ConvexHull(Paths64 component)
    {
        var points = new List<Point64>();
        foreach (var path in component) points.AddRange(path);
        if (points.Count < 3) return points;

        points.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

        var hull = new List<Point64>(points.Count + 1);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = hull.Count;
            var order = pass == 0 ? points : Enumerable.Reverse(points);
            foreach (var p in order)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }
            hull.RemoveAt(hull.Count - 1);
        }
        return hull;
    }

    private static double Cross(Point64 o, Point64 a, Point64 b)
        => (double)(a.X - o.X) * (b.Y - o.Y) - (double)(a.Y - o.Y) * (b.X - o.X);

    private static Bbox BoundsOf(Paths64 paths)
    {
        var box = Bbox.Empty;
        foreach (var path in paths)
            foreach (var point in path)
                box = box.Union(new Bbox(point.X, point.Y, point.X, point.Y));
        return box;
    }
}
