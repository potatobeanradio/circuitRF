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
        var tolCap = new Dictionary<LayerKey, long>();
        foreach (var r in tech.DrcRules)
        {
            if (r.ValueDbu <= 0) continue;
            long cap = Math.Max(1, r.ValueDbu / DrcRegions.ToleranceFractionOfRule);
            tolCap[r.Layer] = tolCap.TryGetValue(r.Layer, out long existing) ? Math.Min(existing, cap) : cap;
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

        // ── Rules, grouped by layer in the technology's own stated order ────────────────────────
        var violations = new List<DrcViolation>();
        var checkedLayers = new HashSet<LayerKey>();
        int rulesEvaluated = 0;

        foreach (var rule in tech.DrcRules)
        {
            if (rule.ValueDbu <= 0) continue;                      // an unset rule checks nothing
            if (!byLayer.TryGetValue(rule.Layer, out var onLayer)) continue;

            rulesEvaluated++;
            checkedLayers.Add(rule.Layer);

            switch (rule.Kind)
            {
                case DrcRuleKind.MinWidth:   CheckMinWidth(rule, onLayer, violations);   break;
                case DrcRuleKind.MinSpacing: CheckMinSpacing(rule, onLayer, violations); break;
            }
        }

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

    // ── Minimum width ───────────────────────────────────────────────────────────────────────────

    private static void CheckMinWidth(
        DrcRule rule, List<(string? Net, Paths64 Paths)> onLayer, List<DrcViolation> into)
    {
        // Width is a property of the METAL, not of a net: two overlapping shapes are one conductor
        // whatever their net attributes say, and a rule that measured them separately would report a
        // violation at every overlap. (Two DIFFERENT nets overlapping is a short — an LVS finding,
        // §9A.3, not a width one.)
        var all = new Paths64();
        foreach (var (_, paths) in onLayer) all.AddRange(paths);

        var union = DrcRegions.Union(all);
        if (union.Count == 0) return;

        double radius = rule.ValueDbu / 2.0 - WidthRadiusBackoffDbu;
        if (radius <= 0) return;    // a rule at or below 2 DBU cannot be checked by opening; nothing to say

        var eroded = Clipper.InflatePaths(union, -radius, JoinType.Miter, EndType.Polygon, MiterLimit);
        var opened = eroded.Count == 0
            ? []
            : Clipper.InflatePaths(eroded, radius + WidthDilateOvershootDbu, JoinType.Miter, EndType.Polygon, MiterLimit);

        var narrow = opened.Count == 0
            ? union
            : Clipper.BooleanOp(ClipType.Difference, union, opened, LayoutClipper.Rule);

        foreach (var region in DrcRegions.Components(narrow))
        {
            if (Math.Abs(Clipper.Area(region)) <= 0) continue;
            into.Add(Make(rule, region, netA: null, netB: null));
        }
    }

    // ── Minimum spacing ─────────────────────────────────────────────────────────────────────────

    private static void CheckMinSpacing(
        DrcRule rule, List<(string? Net, Paths64 Paths)> onLayer, List<DrcViolation> into)
    {
        var conductors = DrcRegions.BuildConductors(onLayer);
        if (conductors.Count < 2) return;

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
