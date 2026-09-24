// Framework-free. No Avalonia, no SkiaSharp.
//
// ── WHY THIS IS NOT IN LayoutPortDirection, WHICH IS WHERE IT USED TO LIVE ────────────────────
//
// It is the same resolver, and splitting one type is not something to do lightly — so the reason is
// stated once, here. `LayoutPortDirection` had to move DOWN to src/Design, because the EM port
// extractor (`EmPortExtraction`, and through it `EmRunService`) must be able to ask the ONE question
// "is this port standing in the interior of its conductor, or at an end of it?" and get the SAME
// answer the drawing gives. src/Design cannot reference src/Render, so a copy of that test in the
// extractor was the alternative, and a second copy of an inference is exactly what every other note
// in that file exists to prevent.
//
// This one overload could not come with it: it resolves through `LayoutHitTest`, which measures a
// label with real Skia glyph metrics off `LayoutRenderer`, and that genuinely is render-layer code.
// Nothing below the wall wants it — a conductor lookup over a plain shape list
// (`LayoutConductorLookup.LookupFor(shapes)`) is all the extractor ever needs, and this form exists for
// the EDITOR, which has a LayoutView, a technology, layer visibility and placed instances to resolve
// through.
//
// The geometry, the PortHint, the direction inference and the interior test all stayed together in
// src/Design. Only the candidate ENUMERATION is here.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircuitRF.Render;

/// <summary>
/// <b>The full conductor lookup: top-level shapes FIRST (exact hit-testing, so a click on an edge
/// counts), then placed instances.</b> The editor's form of
/// <see cref="LayoutConductorLookup.LookupFor(IReadOnlyList{LayoutShape})"/>.
/// </summary>
public static class LayoutConductorLookup
{
    /// <summary>
    /// The full form: top-level shapes FIRST (exact hit-testing, so a click on an edge counts), then
    /// placed instances.
    ///
    /// <para><b>Two owner requirements pull in opposite directions, and this is where they are
    /// reconciled.</b> (1) a placed port must never move on its own — so its geometry may not depend
    /// on which layers are switched on. (2) a drag must not be attracted to metal on a layer that is
    /// switched off — so it may not see metal that is not on screen. Resolve the
    /// conductor against the VISIBLE artwork every time and (1) breaks; resolve it against ALL
    /// artwork and (2) breaks. Both were shipped, in that order, and each broke the other.</para>
    ///
    /// <para><b>The reconciliation is that the two questions belong to different MOMENTS.</b> A port
    /// COMMITS to a conductor layer at a user gesture — placement, or a move — and
    /// <see cref="LabelShape.PortLayer"/> records which. That gesture asks about VISIBLE metal, so
    /// nothing invisible can ever attract it. At rest the port asks only about the layer it already
    /// committed to, and pays no attention to whether that layer is currently shown, so no visibility
    /// toggle can move it. The two rules never meet, because a port is never resting and being
    /// dragged at the same time.</para>
    ///
    /// <list type="bullet">
    /// <item><paramref name="onLayer"/> given (a committed port): shapes on THAT layer only,
    /// visibility ignored.</item>
    /// <item><paramref name="onLayer"/> null (a gesture, or a port written before
    /// <see cref="LabelShape.PortLayer"/> existed): shapes on VISIBLE layers only.</item>
    /// </list>
    ///
    /// <para><b>Visibility here means <see cref="LayerDef.Visible"/> alone, deliberately, not
    /// <c>Visible &amp;&amp; Selectable</c>.</b> `LayoutHitTest.HitStack` requires both because it
    /// answers "what did the user CLICK", and a locked layer may not be clicked. This asks "what
    /// metal is on screen", and a locked layer's metal is on screen — it is the same gate
    /// `LayoutSnapQuery` applies to every snap feature, so a port's marker and the snap that placed
    /// it can no longer disagree about what is there.</para>
    ///
    /// <para><b>The SMALLEST conductor at the point wins, not the topmost.</b> `HitStack` orders
    /// ZOrder-descending because that is what a click means, and borrowing it put a POUR ahead of the
    /// trace lying on it whenever the pour's layer draws on top — measured on the reporting board, a
    /// trace on ZOrder 0 crossing a pour on ZOrder 10. Smallest area is what a user pointed at, and
    /// it is what <see cref="ConductorUnderShape"/> — the shapes-only form used by the clipboard and
    /// <c>DocumentExtents</c> — has always returned, so the two forms no longer disagree about where
    /// a port is.</para>
    ///
    /// <para>Genuinely ambiguous artwork — a port over metal on more than one conductor level — is
    /// still a REFUSAL, and it is <c>EmPortExtraction</c>'s to make ("a port's LEVEL is part of its
    /// identity"). This picks a stable conductor to draw a marker against; it does not decide what
    /// runs.</para>
    ///
    /// <para><b>Why instances have to be in here (owner report, 2026-08-09: "placing a port does not
    /// set a direction, when I placed it by clicking on the metal").</b> A layout built by "Update
    /// Layout from Schematic" is ALL instances and no top-level shapes, so a shapes-only lookup finds
    /// nothing on artwork the user can plainly see, and the port silently gets no direction at all.
    /// </para>
    ///
    /// <para><b>An instance answers with its PIN when the point names one, and only falls back to
    /// its bbox otherwise</b> (owner report, 2026-08-09). The array-expanded bbox is a fair seed for
    /// a straight run of metal and a badly wrong one for anything else — an MTee's box spans both
    /// arms, and a TAPER's spans a width it has nowhere along its length. Since a cell's pins carry
    /// an exact width and outward direction, preferring them makes the common case (a port placed on
    /// a PCell's own pin, which is what the snap lands on) exact instead of approximate. The bbox
    /// survives for a port placed on the metal but NOT at a pin, where there is genuinely nothing
    /// better to say. <c>EmPortExtraction</c> still re-derives the side from exact flattened geometry
    /// and refuses rather than guessing — it does not read this at all.</para>
    ///
    /// <para><paramref name="tolDbu"/> is how close the point must be to a pin to be naming it. Zero
    /// — every caller's default — means exact coincidence, which is precisely what a port snapped
    /// onto a pin has, and correctly declines to claim a pin the user placed the port merely NEAR.</para>
    /// </summary>
    public static LayoutPortDirection.ConductorLookup LookupFor(LayoutView view, Technology? tech, string baseDir, long tolDbu = 0)
    {
        // Built once per lookup — which is once per frame, not once per port — for the same reason
        // LayoutSnapQuery builds its own: a real process stack is hundreds of layers, and a linear
        // scan per candidate shape is what this replaces.
        var visible = new Dictionary<LayerKey, bool>(tech?.Layers.Count ?? 0);
        if (tech is { } t)
            foreach (var l in t.Layers)
                visible.TryAdd(l.Key, l.Visible);   // FIRST wins, matching every other layer lookup

        bool IsVisible(LayerKey key) =>
            visible.TryGetValue(key, out bool v) ? v : FallbackPalette.For(key).Visible;

        // ── COPPER THAT OVERLAPS IS ONE CONDUCTOR (round-7 field report, 2026-09-24) ─────────────
        //
        // A placed part's footprint pad lying over an imported board's own pad is two shapes and one
        // piece of metal. Answering with the smaller shape alone let a port snap to the board pad's
        // end face, which was 6 µm INSIDE the copper, and be drawn as an edge port there — while the
        // EM run, which meshes the union, used the footprint's face. The shape answered is now the
        // union of every same-layer shape (top-level or inside a placed instance) that overlaps the
        // one under the point, so snap-to-boundary, the edge/interior test and the direction all
        // measure the copper. A shape nothing overlaps is answered AS ITSELF, exactly as before.
        // Cached per lookup, which is per frame.
        var mergedCache = new Dictionary<LayoutShape, LayoutShape>(ReferenceEqualityComparer.Instance);
        List<(Bbox Box, IReadOnlyList<LayoutShape> Shapes)>? instanceCopper = null;

        // ownInstance: the instance `best` comes from, whose OTHER pieces are not merged with it — a
        // cell drawn as overlapping pieces (a taper's sections) is one part and keeps its box answer.
        LayoutShape MergedWithOverlapping(LayoutShape best, Bbox box, long px, long py, int ownInstance = -1)
        {
            if (mergedCache.TryGetValue(best, out var hit)) return hit;

            bool Overlaps(Bbox b) => b.MinX < box.MaxX && box.MinX < b.MaxX && b.MinY < box.MaxY && box.MinY < b.MaxY;

            var operands = new List<LayoutShape> { best };
            foreach (var s in view.Shapes)
                if (!ReferenceEquals(s, best) && s.Layer == best.Layer && s is not (LabelShape or BitmapShape)
                    && LayoutBooleans.IsClipperOperand(s) && Overlaps(LayoutGeometry.BboxOf(s)))
                    operands.Add(s);

            if (view.Instances.Count > 0)
            {
                instanceCopper ??= [.. view.Instances.Select(inst =>
                    (CellHierarchy.InstanceBbox(inst, baseDir), (IReadOnlyList<LayoutShape>)[]))];
                for (int k = 0; k < view.Instances.Count; k++)
                {
                    if (k == ownInstance) continue;
                    if (instanceCopper[k].Box.IsEmpty || !Overlaps(instanceCopper[k].Box)) continue;
                    if (instanceCopper[k].Shapes.Count == 0)
                        instanceCopper[k] = (instanceCopper[k].Box,
                                             LayoutFlatten.FlattenAllLevels(view.Instances[k], baseDir).Shapes);
                    foreach (var s in instanceCopper[k].Shapes)
                        if (s.Layer == best.Layer && LayoutBooleans.IsClipperOperand(s) && Overlaps(LayoutGeometry.BboxOf(s)))
                            operands.Add(s);
                }
            }

            LayoutShape answer = best;
            if (operands.Count > 1 && LayoutBooleans.IsClipperOperand(best))
            {
                try
                {
                    var union = LayoutBooleans.Union(operands, tech).Shapes;
                    // Nothing actually overlapped when the union has as many pieces as it had operands.
                    if (union.Count < operands.Count)
                        answer = union.FirstOrDefault(u => LayoutGeometry.BboxOf(u) is var ub
                                                           && ub.MinX <= px && px <= ub.MaxX && ub.MinY <= py && py <= ub.MaxY
                                                           && ub.MinX <= box.MinX && box.MaxX <= ub.MaxX
                                                           && ub.MinY <= box.MinY && box.MaxY <= ub.MaxY) ?? best;
                }
                catch (Exception) { /* a degenerate operand: answer as drawn */ }
            }

            mergedCache[best] = answer;
            return answer;
        }

        // The smallest copper shape of instance k containing the point, EDGE INCLUSIVE — a port snapped
        // onto a pad's face stands exactly on it.
        LayoutShape? InstanceCopperAt(int k, long px, long py, LayerKey? onLayer)
        {
            instanceCopper ??= [.. view.Instances.Select(inst =>
                (CellHierarchy.InstanceBbox(inst, baseDir), (IReadOnlyList<LayoutShape>)[]))];
            if (instanceCopper[k].Shapes.Count == 0)
                instanceCopper[k] = (instanceCopper[k].Box, LayoutFlatten.FlattenAllLevels(view.Instances[k], baseDir).Shapes);

            LayoutShape? found = null;
            double foundArea = double.MaxValue;
            foreach (var s in instanceCopper[k].Shapes)
            {
                if (!LayoutBooleans.IsClipperOperand(s)) continue;
                if (onLayer is { } want ? s.Layer != want : !IsVisible(s.Layer)) continue;
                var b = LayoutGeometry.BboxOf(s);
                if (px < b.MinX || px > b.MaxX || py < b.MinY || py > b.MaxY) continue;
                var paths = LayoutClipper.ToClipperPaths(s, LayoutFlattener.ResolveTolDbu(s, tech));
                bool inside = paths.Any(path => Clipper2Lib.Clipper.PointInPolygon(new Clipper2Lib.Point64(px, py), path)
                                                != Clipper2Lib.PointInPolygonResult.IsOutside);
                if (!inside) continue;
                double area = (double)(b.MaxX - b.MinX) * (b.MaxY - b.MinY);
                if (area < foundArea) { foundArea = area; found = s; }
            }
            return found;
        }

        return (x, y, onLayer) =>
        {
            LayoutShape? best = null;
            Bbox bestBox = default;
            double bestArea = double.MaxValue;

            // ignoreLayerVisibility: this decides for itself, by the rule in the summary — a
            // committed port narrows by LAYER instead, and must not be filtered by what is shown.
            foreach (int i in LayoutHitTest.HitStack(view, tech, x, y, tolDbu, ignoreLayerVisibility: true))
            {
                var shape = view.Shapes[i];
                if (shape is LabelShape or BitmapShape) continue;
                if (onLayer is { } want ? shape.Layer != want : !IsVisible(shape.Layer)) continue;

                var bb = LayoutGeometry.BboxOf(shape);
                if (bb.IsEmpty) continue;

                double area = (double)(bb.MaxX - bb.MinX) * (bb.MaxY - bb.MinY);
                if (area >= bestArea) continue;   // ties keep the earlier (topmost) candidate
                bestArea = area;
                bestBox = bb;
                best = shape;
            }

            // The SHAPE, not only its box: a top-level conductor can be measured at the end face,
            // and for anything that changes width along its length the box is the wrong number.
            if (best is not null)
            {
                var merged = MergedWithOverlapping(best, bestBox, x, y);
                return new LayoutPortDirection.ConductorInfo(LayoutGeometry.BboxOf(merged), null, merged);
            }

            foreach (int i in LayoutHitTest.HitInstanceStack(view, tech, baseDir, x, y, tolDbu))
            {
                var inst = view.Instances[i];
                var bb = CellHierarchy.InstanceBbox(inst, baseDir);
                if (bb.IsEmpty) continue;
                var pin = PinAt(inst, baseDir, tech, x, y, tolDbu);

                // No pin named, and the instance's own copper here OVERLAPS other copper (a footprint
                // pad over a board's pad): answer with the merged outline, as a top-level shape does,
                // so a port snapped onto that copper's edge measures the edge. Copper that overlaps
                // nothing keeps the box answer it always had.
                if (pin is null && InstanceCopperAt(i, x, y, onLayer) is { } own)
                {
                    var ownBox = LayoutGeometry.BboxOf(own);
                    var merged = MergedWithOverlapping(own, ownBox, x, y, ownInstance: i);
                    if (!ReferenceEquals(merged, own))
                        return new LayoutPortDirection.ConductorInfo(LayoutGeometry.BboxOf(merged), null, merged);
                }
                return new LayoutPortDirection.ConductorInfo(bb, pin);
            }

            return null;
        };
    }

    /// <summary>
    /// The pin of <paramref name="inst"/>'s sub-cell that <paramref name="x"/>,<paramref name="y"/>
    /// names, in the PARENT's frame — nearest within <paramref name="tolDbu"/>, or null.
    ///
    /// <para>Walks every array placement, exactly as <c>LayoutSnapQuery</c>'s own instance recursion
    /// does and at the same cost: a placement is a handful of integer operations per pin, and the
    /// caller has already narrowed to instances whose bbox contains the point.</para>
    /// </summary>
    private static LayoutPortDirection.PinFacts? PinAt(LayoutInstance inst, string baseDir, Technology? tech,
                                   long x, long y, long tolDbu)
    {
        var res = CellLayoutResolver.Resolve(inst.CellRef, baseDir);
        if (res.State != CellLayoutState.Resolved) return null;

        var pins = CellPins.Resolve(res.View!, tech);
        if (pins.Count == 0) return null;

        int rows = System.Math.Max(1, inst.Rows), cols = System.Math.Max(1, inst.Cols);
        double bestSq = (double)tolDbu * tolDbu;
        LayoutPortDirection.PinFacts? best = null;

        for (int r = 0; r < rows; r++)
        for (int c = 0; c < cols; c++)
        foreach (var pin in pins)
        {
            var (wx, wy) = LayoutInstanceTransform.TransformPoint(pin.X, pin.Y, inst, r, c);
            double dx = wx - x, dy = wy - y;
            double d2 = dx * dx + dy * dy;
            if (d2 > bestSq) continue;

            bestSq = d2;
            best = new LayoutPortDirection.PinFacts(wx, wy,
                                ScaleWidth(pin.WidthDbu, inst.Mag),
                                // The pin's own outward angle, UNSNAPPED — TransformDirection composes
                                // in real angles and rounds once at the end (R-L3d-12). Inward is
                                // outward + 180, the same relation FromPinOutward states.
                                TransformDirection(pin.OutwardDeg + 180.0, inst));
        }

        // A pin that states no width is a connection point with nothing to say about the metal's
        // extent; falling through to the box is more honest than reporting a zero-width port.
        return best is { WidthDbu: > 0 } ? best : null;
    }

    private static long ScaleWidth(long widthDbu, double mag)
    {
        double w = widthDbu * System.Math.Abs(mag);
        return w > 0 ? (long)System.Math.Round(w) : 0;
    }

    /// <summary>
    /// Carries a cell-local direction, as a REAL angle, into the parent's frame. Mirror-then-rotate,
    /// the SAME ordering as <see cref="LayoutInstanceTransform.TransformPoint"/> — a direction that
    /// composed differently from the position it belongs to would put the arrow and the plane bar on
    /// different sides of the same pin. Mirroring negates local X, which reflects a direction about
    /// the Y axis: <c>deg -> 180 - deg</c>.
    ///
    /// <para><b>R-L3d-12: this is the boundary that snaps, and it snaps ONCE.</b> A
    /// <see cref="PinFacts.Direction"/> is a <see cref="LayoutRotation"/> because port extraction
    /// downstream is side-based, and L3d deliberately did not widen that — whether an EM port on a
    /// non-Manhattan conductor is meaningful is an L8/L9 question about extraction, not a placement
    /// question. What changed here is that the composition now happens in real angles and rounds at
    /// the end, where before <see cref="FromPinOutward"/> collapsed the pin's own (already
    /// double-valued) <see cref="LayoutPin.OutwardDeg"/> to four-way BEFORE the instance rotation was
    /// applied — so a pin at 10 deg inside an instance at 80 deg used to land on R0 and now lands on
    /// R90, which is the correct answer. The residual is not reported: this is a pure geometric query
    /// with no Messages sink to report into, and inventing one would thread a diagnostics channel
    /// through hit-test. The limitation is stated here and in the L3d completion note.</para>
    /// </summary>
    private static LayoutRotation TransformDirection(double localInwardDeg, LayoutInstance inst)
    {
        double deg = inst.MirrorX ? 180.0 - localInwardDeg : localInwardDeg;
        return LayoutAngle.NearestCardinal(deg + inst.RotationDegrees);
    }}
