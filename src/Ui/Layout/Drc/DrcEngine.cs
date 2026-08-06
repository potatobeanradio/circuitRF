// The two checks (docs/design/layout-view.md §9A). Framework-free and side-effect-free: hand it flat
// geometry and a technology, get violations back. It posts nothing, opens nothing, and knows nothing
// about a canvas — R16b's "DRC never blocks editing" is trivially true of a function that cannot
// touch the editor.
//
// ── Why minimum width is an OPENING, and the two things that get it wrong ────────────────────────
//
// A region is at least w wide exactly where a disc of diameter w fits inside it, so the parts that
// are too narrow are (region − opening(region, w/2)) — erode by w/2, dilate back by w/2, subtract.
// Two details decide whether that reports the truth:
//
//   1. MITER joins, not round. Opening with ROUND joins rounds every convex corner by w/2 and the
//      subtraction then reports four corner slivers on a plain rectangle that passes the rule — a
//      false positive on literally every shape. With miter joins the erode/dilate pair is an exact
//      identity for anything wide enough, so a clean layout produces an EMPTY difference rather than
//      a small one that has to be thresholded away.
//   2. Erode by w/2 − 1 DBU, not w/2. A region exactly w wide erodes to zero width, which Clipper2
//      drops as degenerate — so the dilate has nothing to restore and the whole region is reported.
//      A trace drawn exactly AT the minimum is the commonest thing on a board, and reporting it is
//      the one failure that would make people stop running the check. Backing the radius off by one
//      DBU leaves a 2-DBU sliver to dilate from, restores the region exactly, and moves the detection
//      threshold to "narrower than w by more than 1 DBU" — 1 nm on a default technology.
//   3. DILATE BY SLIGHTLY MORE THAN THE ERODE. This one is not obvious and it is the difference
//      between a check that is usable on curved artwork and one that is not. Clipper2 offsets onto
//      the integer DBU grid, so erode-then-dilate is NOT an exact identity: every vertex lands back
//      within a DBU or so of where it started, never exactly on it. On an axis-aligned rectangle the
//      rounding is exact and nothing shows. On a FLATTENED CURVE — a circle, a rounded corner, an
//      arc edge, a round-capped trace — every one of its many oblique vertices is off by a fraction
//      of a DBU, and the difference comes back as a rash of sub-DBU slivers around the whole
//      perimeter. Measured before the fix: a plain circle comfortably wider than the rule reported
//      3-4 violations, and the count SCALED WITH VERTEX COUNT (a larger circle reported 20). Dilating
//      back by radius + 2 DBU makes the opening cover the original wherever the erosion left
//      anything, so clean geometry differences to nothing by construction. It cannot hide a
//      violation: the extra dilation only ever REMOVES from `union − opened`, and a genuinely narrow
//      neck is erased by the erosion entirely, so it has no opened region to be covered by.
//
// ── Why spacing inflates BOTH sides by s/2 ───────────────────────────────────────────────────────
//
// Two regions are closer than s exactly where their s/2 inflations overlap, and the overlap IS the
// gap that has to be opened — which is the marker a user can act on, rather than a highlight on
// metal that is not itself wrong. Round joins here, because spacing is a euclidean distance and a
// mitered corner would measure the diagonal.

using Clipper2Lib;

namespace CircuitRF.Ui.Layout.Drc;

public static class DrcEngine
{
    /// <summary>v1 runs flat (§9A.1's hierarchy answer), so the ceiling that guards a flatten guards
    /// this too. Reused, not re-derived — same risk, same number.</summary>
    public const int DefaultMaxShapes = 500_000;

    /// <summary>See this file's header, "erode by w/2 − 1 DBU".</summary>
    private const double WidthRadiusBackoffDbu = 1.0;

    /// <summary>
    /// See this file's header, point 3 — how much further the dilate goes than the erode, to absorb
    /// integer-grid rounding around a flattened curve. Two DBU: the round trip displaces a vertex by
    /// well under one DBU per offset and there are two offsets, so this covers it with margin while
    /// costing only that much off the edge of a reported marker.
    /// </summary>
    private const double WidthDilateOvershootDbu = 2.0;

    /// <summary>
    /// Clipper2's default. Stated explicitly because it is load-bearing rather than incidental: it is
    /// what squares off a spike sharper than ~60°, so a needle-thin tip is REPORTED as too narrow
    /// instead of being restored exactly by the dilate and passing silently.
    /// </summary>
    private const double MiterLimit = 2.0;

    /// <summary>
    /// Runs every rule the technology states, over already-flattened world-space geometry.
    /// </summary>
    /// <param name="shapes">Elaborated, flat artwork — see <c>LayoutDesignFlatten</c>.</param>
    /// <param name="tech">The resolved technology. Its <see cref="Technology.DrcRules"/> ARE the rule
    /// set; there is no second place rules can come from, which is what makes "which rules did this
    /// check?" answerable with one file name.</param>
    /// <param name="waivers">Persisted per-violation exceptions; matched by <see cref="DrcViolation.Key"/>.</param>
    public static DrcRunResult Run(
        IReadOnlyList<LayoutShape> shapes,
        Technology?                tech,
        IEnumerable<DrcWaiver>?    waivers = null,
        DrcRunSettings?            settings = null)
    {
        settings ??= DrcRunSettings.Default;

        if (tech is null)
            return DrcRunResult.Empty(null,
                ["No technology resolved for this layout, so there are no rules to check against."]);

        if (tech.DrcRules.Count == 0)
            return DrcRunResult.Empty(tech.Name,
                [$"\"{tech.Name}\" states no design rules. Add them in the Technology Editor."]);

        var diagnostics = new List<string>();

        var checkable = shapes.Where(DrcRegions.IsCheckable).ToList();
        if (checkable.Count > settings.MaxShapes)
            return DrcRunResult.Empty(tech.Name,
                [$"This design flattens to {checkable.Count:N0} shapes, above the {settings.MaxShapes:N0} " +
                 "checking ceiling. Nothing was checked."]);

        int skipped = shapes.Count - checkable.Count;
        if (skipped > 0)
            diagnostics.Add($"{skipped:N0} label/reference-image shape(s) carry no manufacturable area and were not checked.");

        // ── How finely each layer's curves must be flattened, BEFORE any is flattened ────────────
        // A curve has to be polygonised finely enough that the approximation cannot be mistaken for
        // the thing being measured — see DrcRegions.ResolveCheckingTol for the measurement that made
        // this necessary rather than merely tidy. Keyed on the SMALLEST rule the layer carries, since
        // that is the one with the least room for error.
        //
        // A rule that measures a DERIVED region caps every layer that region READS, not just the one
        // its marker is attributed to: `and(1/0, 2/0)` measured at 100 DBU constrains both operands,
        // and flattening either coarsely would put the error into the derived region before the check
        // ever sees it.
        var tolCap = new Dictionary<LayerKey, long>();
        foreach (var r in tech.DrcRules)
        {
            if (r.ValueDbu <= 0) continue;
            long cap = Math.Max(1, r.ValueDbu / DrcRegions.ToleranceFractionOfRule);
            foreach (var key in LayersTouchedBy(r))
                tolCap[key] = tolCap.TryGetValue(key, out long existing) ? Math.Min(existing, cap) : cap;
        }
        long CapFor(LayerKey k) => tolCap.TryGetValue(k, out long c) ? c : long.MaxValue;

        // ── Geometry, once, grouped by layer ────────────────────────────────────────────────────
        // Flattening is the expensive half and every rule on a layer wants the same result, so it
        // happens once per shape here rather than once per rule.
        var byLayer = new Dictionary<LayerKey, List<(string? Net, Paths64 Paths)>>();
        var layerOrder = new List<LayerKey>();

        foreach (var shape in checkable)
        {
            try
            {
                DrcRegions.Expand(shape, tech, CapFor, (layer, net, paths) =>
                {
                    if (!byLayer.TryGetValue(layer, out var list))
                    {
                        byLayer[layer] = list = [];
                        layerOrder.Add(layer);
                    }
                    list.Add((net, paths));
                });
            }
            catch (Exception ex)
            {
                // One unflattenable shape must not lose the whole run's findings.
                diagnostics.Add($"A {shape.GetType().Name} on layer {shape.Layer.Layer}/{shape.Layer.Datatype} " +
                                $"could not be flattened and was skipped: {ex.Message}");
            }
        }

        // ── One unioned region per layer, for the expression evaluator ──────────────────────────
        // The net-aware `byLayer` structure above stays as it is — minimum SPACING is the one check
        // that needs net attribution, and it reads that structure directly. Everything else measures
        // regions, so it reads these.
        var layerRegions = new Dictionary<LayerKey, Paths64>();
        foreach (var (layer, entries) in byLayer)
        {
            var all = new Paths64();
            foreach (var (_, paths) in entries) all.AddRange(paths);
            layerRegions[layer] = DrcRegions.Union(all);
        }

        // The technology's own layer table, so the evaluator can tell "this layer does not exist"
        // from "this design has nothing on it" — two very different things to tell a user.
        var definedLayers = tech.Layers.Select(l => l.Key).ToHashSet();
        var eval = new DrcRegionEval(layerRegions, definedLayers);

        // ── Net identity, once, and only when a rule actually asks for it ───────────────────────
        // Extraction walks every via in the stackup against every piece of metal, so it is not free.
        // A technology whose rules never mention nets must not pay for it — which is most of them,
        // and all of the starter technologies.
        bool needsNets = tech.DrcRules.Any(r =>
            r.NetScope != DrcNetScope.Any || r.Kind == DrcRuleKind.AntennaRatio);

        IReadOnlyList<DrcNetPiece> netPieces = needsNets
            ? DrcConnectivity.Extract(layerRegions, tech)
            : [];

        if (needsNets)
        {
            int netCount = netPieces.Count == 0 ? 0 : netPieces.Max(p => p.Net) + 1;
            diagnostics.Add($"Net extraction found {netCount:N0} net(s) across {netPieces.Count:N0} " +
                            "connected piece(s) of metal.");
        }

        // ── Rules, in the technology's own stated order ─────────────────────────────────────────
        var violations = new List<DrcViolation>();
        var checkedLayers = new HashSet<LayerKey>();
        int rulesEvaluated = 0;
        int unusable = 0;

        foreach (var rule in tech.DrcRules)
        {
            // An unset rule checks nothing — but Density and AntennaRatio do not use ValueDbu at
            // all (a density is a fraction, an antenna limit is a ratio), so gating them on it
            // would silently skip every one of them.
            bool usesValue = rule.Kind is not (DrcRuleKind.Density or DrcRuleKind.AntennaRatio);
            if (usesValue && rule.ValueDbu <= 0) continue;

            if (!TryResolveRegion(rule, rule.RegionA, isSecond: false, eval, out var regionA, out string? whyA))
            {
                diagnostics.Add($"Rule \"{rule.Name}\" was not checked: {whyA}");
                unusable++;
                continue;
            }

            Paths64 regionB = [];
            if (rule.NeedsSecondRegion &&
                !TryResolveRegion(rule, rule.RegionB, isSecond: true, eval, out regionB, out string? whyB))
            {
                diagnostics.Add($"Rule \"{rule.Name}\" was not checked: {whyB}");
                unusable++;
                continue;
            }

            // Nothing on the layer (or the derived region came out empty) is not a rule failure —
            // there is simply nothing to measure. Not counted as evaluated, so "N rules checked"
            // means N rules that actually looked at geometry.
            if (regionA.Count == 0) continue;

            rulesEvaluated++;
            checkedLayers.Add(rule.Layer);

            switch (rule.Kind)
            {
                case DrcRuleKind.MinWidth:
                    CheckMinWidth(rule, regionA, violations);
                    break;

                case DrcRuleKind.MinSpacing:
                    // Net attribution survives only for a rule measuring a bare layer. A derived
                    // region is built by boolean algebra over several layers and no longer has one
                    // net per polygon to report, so its conductors are the connected components —
                    // §9A.1's own fallback for unnamed geometry, reached here for the same reason.
                    if (rule.NetScope != DrcNetScope.Any)
                        CheckMinSpacing(rule, ConductorsByNet(regionA, netPieces), violations);
                    else if (rule.RegionA is null && byLayer.TryGetValue(rule.Layer, out var onLayer))
                        CheckMinSpacing(rule, DrcRegions.BuildConductors(onLayer), violations);
                    else
                        CheckMinSpacing(rule, ConductorsOf(regionA), violations);
                    break;

                case DrcRuleKind.MinSeparation: CheckMinSeparation(rule, regionA, regionB, violations); break;
                case DrcRuleKind.MinEnclosure:  CheckMinEnclosure(rule, regionA, regionB, violations);  break;
                case DrcRuleKind.MinOverlap:    CheckMinOverlap(rule, regionA, regionB, violations);    break;
                case DrcRuleKind.MinNotch:      CheckMinNotch(rule, regionA, violations);               break;
                case DrcRuleKind.MinArea:       CheckMinArea(rule, regionA, violations);                break;
                case DrcRuleKind.MinPerimeter:  CheckMinPerimeter(rule, regionA, violations);           break;
                case DrcRuleKind.Density:       CheckDensity(rule, regionA, violations);                break;
                case DrcRuleKind.AntennaRatio:  CheckAntennaRatio(rule, regionA, regionB, netPieces, violations); break;
            }
        }

        if (unusable > 0)
            diagnostics.Add($"{unusable:N0} rule(s) could not be checked and are listed above. " +
                            "Every other rule was checked normally.");

        if (eval.MissingLayers.Count > 0)
            diagnostics.Add(
                $"{eval.MissingLayers.Count:N0} layer(s) named by a rule do not exist in " +
                $"\"{tech.Name}\", so those rules measured nothing: " +
                string.Join(", ", eval.MissingLayers
                    .OrderBy(k => k.Layer).ThenBy(k => k.Datatype)
                    .Take(8).Select(k => $"{k.Layer}/{k.Datatype}")) +
                (eval.MissingLayers.Count > 8 ? ", …" : "") + ".");

        // Deterministic order: layer, then rule, then position. Two runs over unchanged geometry must
        // produce identical lists — the panel's selection, the markers and every test depend on it.
        violations.Sort(Compare);

        var byKey = (waivers ?? []).GroupBy(w => w.Key, StringComparer.Ordinal)
                                   .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var final = new List<DrcViolation>(violations.Count);
        foreach (var v in violations)
            final.Add(byKey.TryGetValue(v.Key, out var w)
                ? v with { Waived = true, WaiverReason = w.Reason }
                : v);

        return new DrcRunResult(final, rulesEvaluated, checkedLayers.Count, checkable.Count, tech.Name, diagnostics);
    }

    // ── Region resolution ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every drawing layer a rule reads — its expressions' operands, or just its own layer.
    /// A malformed expression contributes the rule's own layer rather than nothing, so the
    /// tolerance cap stays conservative even for a rule that will later be reported as unusable.
    /// </summary>
    private static IEnumerable<LayerKey> LayersTouchedBy(DrcRule rule)
    {
        var acc = new HashSet<LayerKey> { rule.Layer };

        foreach (string? text in new[] { rule.RegionA, rule.RegionB })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (DrcLayerExprParser.TryParse(text, out var expr, out _) && expr is not null)
                foreach (var k in expr.ReferencedLayers()) acc.Add(k);
        }

        return acc;
    }

    /// <summary>
    /// Turns a rule's region text into geometry. A null/blank expression means "the rule's own
    /// layer", which is what every pre-v2 rule says.
    ///
    /// <para>Returns false — with a reason naming the rule's problem in the user's own terms —
    /// rather than throwing or silently checking nothing. A rule that cannot be read is a fact the
    /// user needs; a rule that silently passes is the failure this whole feature exists to
    /// avoid.</para>
    /// </summary>
    private static bool TryResolveRegion(
        DrcRule rule, string? text, bool isSecond, DrcRegionEval eval,
        out Paths64 region, out string? error)
    {
        region = [];
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            if (isSecond)
            {
                error = $"a {rule.Kind} rule measures two regions, but no second region is set.";
                return false;
            }

            region = eval.Evaluate(new DrcLayerExpr.Layer(rule.Layer));
            return true;
        }

        if (!DrcLayerExprParser.TryParse(text, out var expr, out string? parseError) || expr is null)
        {
            error = $"the {(isSecond ? "second" : "first")} region \"{text}\" is not a valid " +
                    $"layer expression — {parseError}";
            return false;
        }

        region = eval.Evaluate(expr);
        return true;
    }

    /// <summary>
    /// A region's components, each carrying the EXTRACTED net it belongs to as its conductor name.
    ///
    /// <para>Reusing <see cref="DrcConductor.Net"/> to carry an extracted net index is deliberate:
    /// the spacing check already compares conductor names to decide whether a pair is worth
    /// reporting, so a net-scoped rule needs no second comparison path — only a different source
    /// for the name. A component the extraction does not cover keeps a null name, which reads as
    /// "unknown net" and is never equal to any other.</para>
    /// </summary>
    private static List<DrcConductor> ConductorsByNet(
        Paths64 region, IReadOnlyList<DrcNetPiece> netPieces)
    {
        var result = new List<DrcConductor>();

        foreach (var component in DrcRegions.Components(region))
        {
            var bounds = DrcRegions.BoundsOf(component);
            string? net = null;

            foreach (var piece in netPieces)
            {
                if (!piece.Bounds.Intersects(bounds)) continue;
                var meet = Clipper.BooleanOp(ClipType.Intersection, piece.Paths, component, LayoutClipper.Rule);
                if (meet.Count == 0) continue;
                net = $"net{piece.Net}";
                break;
            }

            result.Add(new DrcConductor(net, component, bounds));
        }

        return result;
    }

    // ── Antenna ratio ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per net: the connected metal area against the gate area attached to it.
    ///
    /// <para>The marker is the METAL of the offending net, not the gate — the metal is what has to
    /// be cut back or jumpered to another layer, and a highlight on the gate would point at the
    /// thing the designer cannot change.</para>
    /// </summary>
    private static void CheckAntennaRatio(
        DrcRule rule, Paths64 metal, Paths64 gate,
        IReadOnlyList<DrcNetPiece> netPieces, List<DrcViolation> into)
    {
        if (rule.MaxRatio is not { } limit || limit <= 0) return;
        if (metal.Count == 0 || gate.Count == 0) return;

        // Group the metal by extracted net, so the ratio is asked of a whole net rather than of one
        // shape — which is the entire point of the rule.
        var metalByNet = new Dictionary<int, Paths64>();

        foreach (var component in DrcRegions.Components(metal))
        {
            var bounds = DrcRegions.BoundsOf(component);

            foreach (var piece in netPieces)
            {
                if (!piece.Bounds.Intersects(bounds)) continue;
                var meet = Clipper.BooleanOp(ClipType.Intersection, piece.Paths, component, LayoutClipper.Rule);
                if (meet.Count == 0) continue;

                if (!metalByNet.TryGetValue(piece.Net, out var acc)) metalByNet[piece.Net] = acc = [];
                acc.AddRange(component);
                break;
            }
        }

        foreach (var (net, netMetal) in metalByNet)
        {
            var attachedGate = Clipper.BooleanOp(ClipType.Intersection, gate, netMetal, LayoutClipper.Rule);
            double gateArea = DrcRegionEval.AreaOfComponent(attachedGate);

            // A net attached to no gate has no antenna to discharge through and no rule to break —
            // reporting it would flag every routing net on the design.
            if (gateArea <= 0) continue;

            double ratio = DrcRegionEval.AreaOfComponent(netMetal) / gateArea;
            if (ratio <= limit) continue;

            into.Add(Make(rule, DrcRegions.Union(netMetal), netA: $"net{net}", netB: null));
        }
    }

    /// <summary>A derived region's connected components, as net-less conductors.</summary>
    private static List<DrcConductor> ConductorsOf(Paths64 region)
    {
        var result = new List<DrcConductor>();
        foreach (var component in DrcRegions.Components(region))
            result.Add(new DrcConductor(null, component, DrcRegions.BoundsOf(component)));
        return result;
    }

    // ── Minimum width ───────────────────────────────────────────────────────────────────────────

    private static void CheckMinWidth(DrcRule rule, Paths64 union, List<DrcViolation> into)
    {
        // Width is a property of the METAL, not of a net: two overlapping shapes are one conductor
        // whatever their net attributes say, and a rule that measured them separately would report a
        // violation at every overlap. (Two DIFFERENT nets overlapping is a short — an LVS finding,
        // §9A.3, not a width one.)
        foreach (var region in NarrowerThan(union, rule.ValueDbu))
            into.Add(Make(rule, region, netA: null, netB: null));
    }

    /// <summary>
    /// The parts of <paramref name="union"/> narrower than <paramref name="widthDbu"/>, as separate
    /// regions. Shared by the width check and the overlap check — an overlap that must be at least
    /// <c>o</c> wide is the width question asked of the intersection, and answering it twice would
    /// be two chances to get the opening wrong.
    /// </summary>
    private static List<Paths64> NarrowerThan(Paths64 union, long widthDbu)
    {
        var found = new List<Paths64>();
        if (union.Count == 0) return found;

        double radius = widthDbu / 2.0 - WidthRadiusBackoffDbu;
        if (radius <= 0) return found;   // a rule at or below 2 DBU cannot be checked by opening

        var eroded = Clipper.InflatePaths(union, -radius, JoinType.Miter, EndType.Polygon, MiterLimit);
        var opened = eroded.Count == 0
            ? []
            : Clipper.InflatePaths(eroded, radius + WidthDilateOvershootDbu, JoinType.Miter, EndType.Polygon, MiterLimit);

        var narrow = opened.Count == 0
            ? union
            : Clipper.BooleanOp(ClipType.Difference, union, opened, LayoutClipper.Rule);

        foreach (var region in DrcRegions.Components(narrow))
            if (Math.Abs(Clipper.Area(region)) > 0)
                found.Add(region);

        return found;
    }

    // ── Minimum spacing ─────────────────────────────────────────────────────────────────────────

    private static void CheckMinSpacing(
        DrcRule rule, List<DrcConductor> conductors, List<DrcViolation> into)
    {
        if (conductors.Count < 2) return;

        // A net-scoped rule reports only the pairs it is about. `Any` (the default, and every
        // pre-v2 rule) keeps the old behaviour exactly: every pair is a candidate.
        bool Wanted(string? netA, string? netB) => rule.NetScope switch
        {
            DrcNetScope.SameNet      => netA is not null && netA == netB,

            // An unknown net is never assumed to be different — that would report a pair the
            // extraction simply could not resolve as a potential short, which is the false positive
            // most likely to make people stop trusting the check.
            DrcNetScope.DifferentNet => netA is not null && netB is not null && netA != netB,

            _                        => true,
        };

        double half = rule.ValueDbu / 2.0;

        // Inflate once per conductor, not once per pair — the pairwise loop is quadratic and the
        // inflate is the expensive part of it.
        var grown = new Paths64[conductors.Count];
        for (int i = 0; i < conductors.Count; i++)
            grown[i] = Clipper.InflatePaths(conductors[i].Paths, half, JoinType.Round, EndType.Polygon);

        for (int i = 0; i < conductors.Count; i++)
        {
            var boundsI = DrcRegions.Grow(conductors[i].Bounds, rule.ValueDbu);
            for (int j = i + 1; j < conductors.Count; j++)
            {
                if (!boundsI.Intersects(conductors[j].Bounds)) continue;   // cheap rejection first
                if (!Wanted(conductors[i].Net, conductors[j].Net)) continue;

                var overlap = Clipper.BooleanOp(ClipType.Intersection, grown[i], grown[j], LayoutClipper.Rule);
                if (overlap.Count == 0) continue;

                foreach (var region in DrcRegions.Components(overlap))
                {
                    // A gap of exactly the rule value inflates to a zero-area contact, which Clipper2
                    // drops; anything that survives with area is genuinely closer than the rule.
                    if (Math.Abs(Clipper.Area(region)) <= 0) continue;
                    into.Add(Make(rule, region, conductors[i].Net, conductors[j].Net));
                }
            }
        }
    }

    // ── Minimum separation (two regions) ────────────────────────────────────────────────────────

    private static void CheckMinSeparation(
        DrcRule rule, Paths64 a, Paths64 b, List<DrcViolation> into)
    {
        if (a.Count == 0 || b.Count == 0) return;

        // Pairwise over components, exactly like the spacing check — not region-against-region.
        //
        // <b>The reason is semantic, not performance.</b> Separation is the gap between two things
        // standing APART. Where a polygon of B sits inside a polygon of A there is no gap: the
        // distance from A's boundary in to B is an ENCLOSURE, which has its own rule kind and its
        // own marker. Measured region-against-region, a contact correctly enclosed by metal reports
        // a separation violation as a ring all the way around it — one false finding per contact on
        // a real design, which is most of them. Skipping overlapping PAIRS is what draws that line;
        // subtracting the overlap afterwards does not, because the ring survives the subtraction.
        double half = rule.ValueDbu / 2.0;

        var aComponents = DrcRegions.Components(a);
        var bComponents = DrcRegions.Components(b);

        var grownA = new Paths64[aComponents.Count];
        for (int i = 0; i < aComponents.Count; i++)
            grownA[i] = Clipper.InflatePaths(aComponents[i], half, JoinType.Round, EndType.Polygon);

        var grownB = new Paths64[bComponents.Count];
        for (int j = 0; j < bComponents.Count; j++)
            grownB[j] = Clipper.InflatePaths(bComponents[j], half, JoinType.Round, EndType.Polygon);

        for (int i = 0; i < aComponents.Count; i++)
        {
            var boundsI = DrcRegions.Grow(DrcRegions.BoundsOf(aComponents[i]), rule.ValueDbu);

            for (int j = 0; j < bComponents.Count; j++)
            {
                if (!boundsI.Intersects(DrcRegions.BoundsOf(bComponents[j]))) continue;   // cheap first

                // Overlapping pair → enclosure or overlap territory, not separation.
                var meet = Clipper.BooleanOp(ClipType.Intersection, aComponents[i], bComponents[j], LayoutClipper.Rule);
                if (meet.Count > 0 && Math.Abs(AreaOf(meet)) > 0) continue;

                var close = Clipper.BooleanOp(ClipType.Intersection, grownA[i], grownB[j], LayoutClipper.Rule);
                if (close.Count == 0) continue;

                foreach (var region in DrcRegions.Components(close))
                    if (Math.Abs(Clipper.Area(region)) > 0)
                        into.Add(Make(rule, region, netA: null, netB: null));
            }
        }
    }

    // ── Minimum enclosure (A must surround B) ───────────────────────────────────────────────────

    private static void CheckMinEnclosure(
        DrcRule rule, Paths64 enclosing, Paths64 enclosed, List<DrcViolation> into)
    {
        if (enclosed.Count == 0) return;

        // Enclosure is only a question where the two regions actually meet. A B-polygon nowhere near
        // A is not "enclosed by zero" — it is a different rule's problem (usually a presence or
        // separation rule), and reporting it here would bury the real findings under one violation
        // per unrelated shape on the layer.
        var relevant = enclosing.Count == 0
            ? []
            : Clipper.BooleanOp(ClipType.Intersection, enclosed, enclosing, LayoutClipper.Rule);
        if (relevant.Count == 0) return;

        // A extends at least `e` beyond B exactly where B fits inside A shrunk by `e`. Whatever of B
        // falls outside that shrunk region is enclosed by less — which is both the test and the
        // marker. The one-DBU backoff matches the width check's, and for the same reason: an
        // enclosure drawn exactly AT the rule must pass.
        double margin = rule.ValueDbu - WidthRadiusBackoffDbu;
        var shrunk = margin <= 0
            ? enclosing
            : Clipper.InflatePaths(enclosing, -margin, JoinType.Miter, EndType.Polygon, MiterLimit);

        var short_ = shrunk.Count == 0
            ? relevant
            : Clipper.BooleanOp(ClipType.Difference, relevant, shrunk, LayoutClipper.Rule);

        foreach (var region in DrcRegions.Components(short_))
            if (Math.Abs(Clipper.Area(region)) > 0)
                into.Add(Make(rule, region, netA: null, netB: null));
    }

    // ── Minimum overlap ─────────────────────────────────────────────────────────────────────────

    private static void CheckMinOverlap(
        DrcRule rule, Paths64 a, Paths64 b, List<DrcViolation> into)
    {
        if (a.Count == 0 || b.Count == 0) return;

        var overlap = Clipper.BooleanOp(ClipType.Intersection, a, b, LayoutClipper.Rule);
        if (overlap.Count == 0) return;   // no overlap at all is a presence question, not a width one

        // "The overlap must be at least o wide" IS the width question asked of the intersection.
        foreach (var region in NarrowerThan(overlap, rule.ValueDbu))
            into.Add(Make(rule, region, netA: null, netB: null));
    }

    // ── Minimum notch ───────────────────────────────────────────────────────────────────────────

    private static void CheckMinNotch(DrcRule rule, Paths64 region, List<DrcViolation> into)
    {
        if (region.Count == 0) return;

        double radius = rule.ValueDbu / 2.0 - WidthRadiusBackoffDbu;
        if (radius <= 0) return;

        // A notch is a gap whose two sides belong to the SAME conductor, so the closing has to be
        // done per connected component. Closing the whole layer at once would also bridge the gaps
        // BETWEEN separate conductors and report every one of them — which is the spacing check's
        // job, answered there with net attribution this check does not have.
        foreach (var component in DrcRegions.Components(region))
        {
            // Morphological closing: dilate then erode. A gap narrower than the rule is bridged by
            // the dilation and does not come back, so it survives the subtraction as a filled notch.
            var dilated = Clipper.InflatePaths(component, radius, JoinType.Miter, EndType.Polygon, MiterLimit);
            if (dilated.Count == 0) continue;

            var closed = Clipper.InflatePaths(dilated, -radius, JoinType.Miter, EndType.Polygon, MiterLimit);
            if (closed.Count == 0) continue;

            // Subtract the component GROWN by the overshoot, not the component itself — the same
            // integer-grid rounding that made the width check shed slivers on curves applies to this
            // dilate/erode pair too, and the overshoot absorbs it identically.
            var grown = Clipper.InflatePaths(component, WidthDilateOvershootDbu, JoinType.Miter, EndType.Polygon, MiterLimit);
            var notches = Clipper.BooleanOp(ClipType.Difference, closed, grown.Count == 0 ? component : grown,
                                            LayoutClipper.Rule);

            foreach (var notch in DrcRegions.Components(notches))
                if (Math.Abs(Clipper.Area(notch)) > 0)
                    into.Add(Make(rule, notch, netA: null, netB: null));
        }
    }

    // ── Minimum area and perimeter ──────────────────────────────────────────────────────────────

    private static void CheckMinArea(DrcRule rule, Paths64 region, List<DrcViolation> into)
    {
        // ValueDbu is SQUARE DBU for this kind — see DrcRuleKind.MinArea. The whole polygon is the
        // marker: unlike a width violation there is no narrow SUB-region to point at, the shape
        // itself is what is too small.
        foreach (var component in DrcRegions.Components(region))
            if (DrcRegionEval.AreaOfComponent(component) < rule.ValueDbu)
                into.Add(Make(rule, component, netA: null, netB: null));
    }

    private static void CheckMinPerimeter(DrcRule rule, Paths64 region, List<DrcViolation> into)
    {
        foreach (var component in DrcRegions.Components(region))
            if (DrcRegionEval.PerimeterOfComponent(component) < rule.ValueDbu)
                into.Add(Make(rule, component, netA: null, netB: null));
    }

    // ── Density ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far the window advances between samples, as a fraction of its own side.
    ///
    /// <para>Half a window. A fab states density over a window at a stated STEP, and stepping by a
    /// full window would miss a violating region straddling two window boundaries — the classic way
    /// a density check passes a design the fab then rejects. Half-window overlap is the usual
    /// convention and costs 4× the samples, which is the trade a correctness-critical check should
    /// make.</para>
    /// </summary>
    private const double DensityStepFraction = 0.5;

    /// <summary>
    /// Guard against a pathological window/design combination producing an unbounded sample grid.
    /// Exceeded, the check refuses and says so rather than hanging — the same "a pathological design
    /// costs a message, not a freeze" rule the shape ceiling already follows.
    /// </summary>
    private const int MaxDensityWindows = 250_000;

    private static void CheckDensity(DrcRule rule, Paths64 region, List<DrcViolation> into)
    {
        if (rule.WindowDbu is not > 0) return;                 // validation reports this
        if (rule.MinRatio is null && rule.MaxRatio is null) return;

        long window = rule.WindowDbu.Value;
        long step = Math.Max(1, (long)(window * DensityStepFraction));

        // The design's own extent, not the window's: density is asked of the area a design occupies.
        var extent = DrcRegions.BoundsOf(region);
        if (extent.IsEmpty) return;

        // A window is never sampled past the edge of the artwork. Letting the grid run off the end
        // measures blank space beyond the design as low density and reports a violation all the way
        // around the boundary — which is not a density problem, it is the absence of a die outline.
        // Where the design is smaller than one window, one window covering it is the honest answer.
        long spanX = extent.MaxX - extent.MinX;
        long spanY = extent.MaxY - extent.MinY;
        long cols = spanX <= window ? 1 : (spanX - window) / step + 1;
        long rows = spanY <= window ? 1 : (spanY - window) / step + 1;
        if (cols * rows > MaxDensityWindows) return;

        double windowArea = (double)window * window;

        for (long iy = 0; iy < rows; iy++)
        {
            long y0 = extent.MinY + iy * step;

            for (long ix = 0; ix < cols; ix++)
            {
                long x0 = extent.MinX + ix * step;

                Paths64 box =
                [[
                    new Point64(x0, y0), new Point64(x0 + window, y0),
                    new Point64(x0 + window, y0 + window), new Point64(x0, y0 + window),
                ]];

                var inside = Clipper.BooleanOp(ClipType.Intersection, region, box, LayoutClipper.Rule);
                double ratio = DrcRegionEval.AreaOfComponent(inside) / windowArea;

                bool low  = rule.MinRatio is { } lo && ratio < lo;
                bool high = rule.MaxRatio is { } hi && ratio > hi;
                if (!low && !high) continue;

                // The marker is the WINDOW, not the metal in it — the user has to add or remove fill
                // across that whole area, and highlighting the existing metal would point at the
                // shapes that are already correct.
                into.Add(Make(rule, box, netA: null, netB: null));
            }
        }
    }

    /// <summary>Total signed area of a path set.</summary>
    private static double AreaOf(Paths64 paths)
    {
        double sum = 0;
        foreach (var p in paths) sum += Clipper.Area(p);
        return sum;
    }

    // ── Violation construction ──────────────────────────────────────────────────────────────────

    private static DrcViolation Make(DrcRule rule, Paths64 region, string? netA, string? netB)
    {
        var rings  = DrcRegions.ToRings(region);
        var bounds = DrcRegions.BoundsOf(region);
        return new DrcViolation(
            rule.Name, rule.Kind, rule.Severity, rule.Layer, rule.ValueDbu,
            rings, bounds, netA, netB, KeyFor(rule, bounds));
    }

    /// <summary>
    /// The identity a waiver names. Rule + layer + the marker's exact bounding box: exact rather than
    /// quantised because DBU are integers and both Clipper2 and the flattener are deterministic, so
    /// re-running on unchanged geometry reproduces the key bit-for-bit. Editing the offending shape
    /// changes it, which un-waives the violation — the correct outcome, since the waiver was granted
    /// for geometry that no longer exists.
    /// </summary>
    public static string KeyFor(DrcRule rule, Bbox marker) =>
        $"{rule.Kind}|{rule.Name}|{rule.Layer.Layer}/{rule.Layer.Datatype}|" +
        $"{marker.MinX},{marker.MinY},{marker.MaxX},{marker.MaxY}";

    private static int Compare(DrcViolation a, DrcViolation b)
    {
        int c = a.Layer.Layer.CompareTo(b.Layer.Layer);          if (c != 0) return c;
        c = a.Layer.Datatype.CompareTo(b.Layer.Datatype);        if (c != 0) return c;
        c = string.CompareOrdinal(a.RuleName, b.RuleName);       if (c != 0) return c;
        c = a.Marker.MinX.CompareTo(b.Marker.MinX);              if (c != 0) return c;
        c = a.Marker.MinY.CompareTo(b.Marker.MinY);              if (c != 0) return c;
        c = a.Marker.MaxX.CompareTo(b.Marker.MaxX);              if (c != 0) return c;
        return a.Marker.MaxY.CompareTo(b.Marker.MaxY);
    }
}
