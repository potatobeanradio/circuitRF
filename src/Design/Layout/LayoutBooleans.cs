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

/// <summary>One <see cref="LayoutBooleans.Clip"/>/<see cref="LayoutBooleans.CutOut"/> result
/// (docs/sonnet-briefs/brief-layout-clip-and-cut-out.md §6). Unlike <see cref="LayoutBooleanResult"/>
/// this is NOT one combined region: N operands go in and each contributes 0..N shapes of its own, in
/// operand order, keeping its own <c>Layer</c> and <c>Net</c> (R-clip-5). The three counts partition
/// the operand set exactly — <c>OperandsRemoved + OperandsChanged + OperandsUntouched</c> is the
/// operand count — and are what Messages reports (R-clip-3).</summary>
/// <param name="Shapes">Results in operand order; an operand may contribute 0..N.</param>
/// <param name="OperandsRemoved">Operands that went empty.</param>
/// <param name="OperandsChanged">Operands whose geometry was rebuilt by the clipper.</param>
/// <param name="OperandsUntouched">Operands passed through as the SAME object (R-clip-6).</param>
/// <param name="AnyCurvedOperand">True when an operand that was actually clipped needed flattening —
/// an operand skipped by the bbox test (R-clip-6) never does, whatever its kind.</param>
public readonly record struct LayoutClipResult(
    IReadOnlyList<LayoutShape> Shapes,
    int OperandsRemoved,
    int OperandsChanged,
    int OperandsUntouched,
    bool AnyCurvedOperand);

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

    // ── Clip / Cut Out (brief-layout-clip-and-cut-out.md) ─────────────────────

    /// <summary>Keeps the part of each operand that lies INSIDE <paramref name="stencil"/>, each
    /// operand clipped independently (§2). This is not <see cref="Intersect"/>: that is the one region
    /// shared by ALL operands and goes empty at the first disjoint pair, which is the correct answer to
    /// a different question (§1).</summary>
    public static LayoutClipResult Clip(IReadOnlyList<LayoutShape> operands, LayoutShape stencil, Technology? tech) =>
        ClipCore(ClipType.Intersection, operands, stencil, tech);

    /// <summary>Keeps the part of each operand that lies OUTSIDE <paramref name="stencil"/> — the
    /// complement of <see cref="Clip"/> over the same operand and stencil.</summary>
    public static LayoutClipResult CutOut(IReadOnlyList<LayoutShape> operands, LayoutShape stencil, Technology? tech) =>
        ClipCore(ClipType.Difference, operands, stencil, tech);

    /// <summary>
    /// The shared per-operand clip. <paramref name="clipType"/> is <c>Intersection</c> for Clip and
    /// <c>Difference</c> for Cut Out; everything else about the two is identical, which is why they
    /// are complements by construction rather than by two pieces of arithmetic that have to agree.
    ///
    /// <para><b>R-clip-5 — each result carries its OWN operand's layer and net.</b> The stencil
    /// contributes neither. This is a deliberate departure from <see cref="Combine"/>'s
    /// <c>NetsDiffered</c> rule, which clears the net when operands disagree: that is right for a
    /// union, whose single output region genuinely has no single net, and wrong here, where clipping a
    /// 40-net copper layer would silently strip 40 nets.</para>
    ///
    /// <para><b>R-clip-6 — an operand the stencil cannot touch is passed through as the SAME
    /// OBJECT.</b> Without the bbox reject below, Clip would quietly convert every Circle,
    /// RoundedRect and Curve on the layer into a <c>PolygonShape</c> — a destructive, invisible
    /// flatten of artwork the user never asked to touch. It is also what makes the operation fast: on
    /// the board that motivated this brief it skips 65 of 67 operands with no Clipper2 call at all.
    /// The disjoint test is exact for both operations and every stencil kind. The containment test is
    /// NOT: an operand's bbox lying inside the stencil's bbox implies the operand lies inside the
    /// stencil only when the stencil is convex and hole-free, which a <c>RectShape</c> is and an
    /// arbitrary <c>PolygonShape</c> is not — so it is restricted to a Rect stencil.</para>
    /// </summary>
    /// <summary>
    /// The anchor of a POINT-ANCHORED shape — a <see cref="ViaShape"/> or a <see cref="LabelShape"/> —
    /// or null for anything else.
    ///
    /// <para><b>R-clip-10 — these are clip operands even though they are not clipper operands.</b>
    /// <see cref="IsClipperOperand"/> asks whether a shape can be FLATTENED into a region, which a via
    /// and a label cannot, and that is the right question for Union/Intersect/Difference/Xor/Offset:
    /// combining a drill hole with a polygon means nothing. Clip and Cut Out ask a different question —
    /// "is this shape in the region I am keeping?" — and a shape that sits at a point has an exact
    /// answer to it. Excluding them from the operand set answered it as "always yes, keep it", so a
    /// clip of a whole imported board left every via on it standing, scattered far outside the
    /// stencil, with nothing in Messages to say 189 shapes had been skipped.</para>
    ///
    /// <para>All-or-nothing by construction: a via is never split, never rebuilt, and never becomes a
    /// polygon. It is kept as the SAME OBJECT or removed, so <c>OperandsChanged</c> can never count
    /// one. A <see cref="BitmapShape"/> is deliberately NOT here — it has real extent and no anchor,
    /// and clipping one would mean cropping the image (R-bmp-3 keeps it out of every boolean).</para>
    /// </summary>
    private static Point64? AnchorOf(LayoutShape shape) => shape switch
    {
        ViaShape v   => new Point64(v.X, v.Y),
        LabelShape l => new Point64(l.X, l.Y),
        _            => null,
    };

    /// <summary>Whether a shape may enter a <see cref="Clip"/>/<see cref="CutOut"/> operand set — the
    /// clipper operands plus the point-anchored kinds <see cref="AnchorOf"/> names. Wider than
    /// <see cref="IsClipperOperand"/> on purpose; see that method's note for why the two differ.</summary>
    public static bool IsClipOperand(LayoutShape shape) =>
        IsClipperOperand(shape) || AnchorOf(shape) is not null;

    /// <summary>Point-in-stencil under the SAME fill rule the clip itself uses — a 2-DBU probe square
    /// intersected with the stencil, rather than a second containment predicate that could disagree
    /// with <see cref="LayoutClipper.Rule"/> about a stencil with holes. 2 DBU is 2 nm at a typical
    /// board database unit of 1,000 DBU/µm; a via exactly on the stencil's edge reads as inside.</summary>
    private static bool PointInStencil(Point64 pt, Paths64 stencilPaths)
    {
        var probe = new Paths64(1)
        {
            new Path64(4)
            {
                new Point64(pt.X - 1, pt.Y - 1), new Point64(pt.X + 1, pt.Y - 1),
                new Point64(pt.X + 1, pt.Y + 1), new Point64(pt.X - 1, pt.Y + 1),
            },
        };
        var tree = new PolyTree64();
        Clipper.BooleanOp(ClipType.Intersection, probe, stencilPaths, tree, LayoutClipper.Rule);
        return tree.Count > 0;
    }

    private static LayoutClipResult ClipCore(
        ClipType clipType, IReadOnlyList<LayoutShape> operands, LayoutShape stencil, Technology? tech)
    {
        bool keepInside = clipType == ClipType.Intersection;
        var stencilBox = LayoutGeometry.BboxOf(stencil);
        // A Rect stencil IS its own bounding box, so "inside the box" and "inside the stencil" are the
        // same statement — the only stencil kind for which the containment shortcut is exact.
        bool stencilIsItsBox = stencil is RectShape;

        Paths64? stencilPaths = null;   // built lazily: a selection entirely rejected by bbox never needs it

        var shapes = new List<LayoutShape>(operands.Count);
        int removed = 0, changed = 0, untouched = 0;
        bool anyCurved = false;

        foreach (var operand in operands)
        {
            if (AnchorOf(operand) is { } anchor)
            {
                // R-clip-10: all-or-nothing on the anchor. The bbox test short-circuits the common
                // case (a via nowhere near the stencil) without building the stencil's paths at all.
                bool inside = stencilBox.Contains(anchor.X, anchor.Y);
                if (inside)
                {
                    stencilPaths ??= LayoutClipper.ToClipperPaths(stencil, LayoutFlattener.ResolveTolDbu(stencil, tech));
                    inside = stencilIsItsBox || PointInStencil(anchor, stencilPaths);
                }

                if (inside == keepInside) { shapes.Add(operand); untouched++; }
                else removed++;
                continue;
            }

            var box = LayoutGeometry.BboxOf(operand);

            if (!box.Intersects(stencilBox))
            {
                // Nothing of this operand is inside the stencil.
                if (keepInside) removed++;
                else { shapes.Add(operand); untouched++; }
                continue;
            }

            if (stencilIsItsBox && Inside(box, stencilBox))
            {
                // All of this operand is inside the stencil.
                if (keepInside) { shapes.Add(operand); untouched++; }
                else removed++;
                continue;
            }

            stencilPaths ??= LayoutClipper.ToClipperPaths(stencil, LayoutFlattener.ResolveTolDbu(stencil, tech));

            if (IsCurved(operand)) anyCurved = true;
            var subject = LayoutClipper.ToClipperPaths(operand, LayoutFlattener.ResolveTolDbu(operand, tech));

            var tree = new PolyTree64();
            Clipper.BooleanOp(clipType, subject, stencilPaths, tree, LayoutClipper.Rule);
            var pieces = LayoutClipper.FromClipperTree(tree, operand.Layer, operand.Net);

            if (pieces.Count == 0) { removed++; continue; }
            shapes.AddRange(pieces);
            changed++;
        }

        return new LayoutClipResult(shapes, removed, changed, untouched, anyCurved);
    }

    private static bool Inside(in Bbox inner, in Bbox outer) =>
        !inner.IsEmpty && !outer.IsEmpty &&
        inner.MinX >= outer.MinX && inner.MaxX <= outer.MaxX &&
        inner.MinY >= outer.MinY && inner.MaxY <= outer.MaxY;

    /// <summary>
    /// Whether <see cref="LayoutClipper.ToClipperPaths"/> accepts this shape — i.e. whether it may
    /// enter a boolean/offset/clip operand set at all, or be a clip stencil.
    ///
    /// <para><b>R-clip-8 — stated once, as a POSITIVE test for what the flattener accepts, never as a
    /// deny-list.</b> A deny-list is what let a <c>LabelShape</c> or a <c>ViaShape</c> reach
    /// <see cref="LayoutFlattener.Flatten"/> and throw <c>ArgumentOutOfRangeException</c> out of a
    /// context-menu click: the filter listed <c>BitmapShape</c> and simply never learned about the
    /// other two. Listed positively, a new non-region shape kind is excluded by default rather than
    /// crashing. A via wanted as artwork has <c>Convert to Via</c>'s inverse; a label has
    /// <c>Flatten to Polygon</c>, the supported route from text to a region.</para>
    ///
    /// <para><b>This is NOT the Clip/Cut Out operand test — that one is
    /// <see cref="IsClipOperand"/>.</b> "Can be flattened into a region" and "can be decided in or out
    /// of a region" are different questions, and a via answers no to the first and yes to the second;
    /// see <see cref="AnchorOf"/> for what sharing one test cost.</para>
    /// </summary>
    public static bool IsClipperOperand(LayoutShape shape) =>
        shape is RectShape or PolygonShape or RoundedRectShape or CircleShape or CurveShape or PathShape;

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
