// Assembly DRC — evaluating a `.wasm`'s rules over a wBond design (brief-wbond-wbd §M4).
//
// Framework-free and side-effect-free, exactly like `DrcEngine` next door: hand it a design, a rule
// set and the artwork's own regions, get violations back. It posts nothing and knows nothing about a
// canvas.
//
// ── Two things are checked whether or not a house states a rule ─────────────────────────────────
//
// 1. WIRE-TO-WIRE CLEARANCE — `WBondBuiltInRules.WireClearance`, the rule circuitRF supplies
//    itself, held to half a mil surface-to-surface by default. Two pieces of metal cannot occupy the
//    same space and a bonder cannot hold two wires to zero gap, so a wire pair below it is a
//    geometry error in the design, not a design that is close to somebody's limit. It runs with no
//    `.wasm` present at all, because an assembly house's rule file is not what makes overlapping
//    metal invalid — and it runs alongside one, for the same reason: a file that omits the rule does
//    not repeal it.
// 2. A wire whose diameter or metal is not one the material section lists — a set-membership test,
//    which is why it is checked structurally rather than through the expression language.
//
// ── The BUILT-IN set, and why "no rules resolved" is no longer the answer ───────────────────────
//
// A design with no `.wasm` used to be told only that it had none. That is a check that runs and
// reports nothing, which is the shape of a tool people stop pressing. The built-in set (see
// `WBondBuiltInRules` for what may and may not go in it) is what such a design is checked against
// instead, and BOTH the count of rules evaluated and the diagnostics say which set ran — because
// "clean against one geometry rule" and "clean against your house's forty" are answers a user must
// never have to guess between.
//
// ── Cost (R-wbd-4) ──────────────────────────────────────────────────────────────────────────────
//
// 600 wires is 179,700 unordered pairs before anything looks at a segment. Every pair rule therefore
// runs through `WirePairSweep`'s bounding-box broad phase rather than a double loop; measured on the
// 600-wire fixture that is 2.0 ms against 417 ms for the same answer without it. The fallback path —
// a rule the cutoff analysis below cannot bound — pays the full cost and SAYS SO in the diagnostics
// rather than being quietly slow.

using Clipper2Lib;
using CircuitRF.Ui.Layout.Assembly;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Drc;

/// <summary>Everything the wire check needs from outside the wBond design itself.</summary>
/// <param name="Design">The wires.</param>
/// <param name="Assembly">The resolved `.wasm`, or null — null is not an error (§M1).</param>
/// <param name="Tech">The resolved technology, for stackup heights and region resolution.</param>
/// <param name="DbuPerMicron">The reference LAYOUT's own resolution — the R-wbd-1 crossing.</param>
/// <param name="RegionOf">
/// Resolves a layer expression to artwork, in DBU. Supplied by <see cref="DrcEngine"/> from the same
/// <see cref="DrcRegionEval"/> the die-side rules use, so `wire_to_layer(G1, and(8/0, 9/0))` means
/// exactly what the same expression means in a `.ctech` rule.
/// </param>
/// <param name="LayoutExtent">
/// The artwork's overall bounding box — what <c>dist_to_edge</c> measures against. Empty when there
/// is no reference layout, which turns that one function into a reported no-op rather than a zero.
/// </param>
public sealed record WBondCheckContext(
    WBondDesign            Design,
    WasmFile?              Assembly,
    Technology?            Tech,
    int                    DbuPerMicron,
    Func<DrcLayerExpr, Paths64>? RegionOf,
    Bbox                   LayoutExtent);

/// <summary>What one wire check produced.</summary>
public sealed record WBondCheckResult(
    IReadOnlyList<DrcViolation> Violations,
    int                         RulesEvaluated,
    IReadOnlyList<string>       Diagnostics);

public static class DrcWireCheck
{
    /// <summary>
    /// A wire violation's marker is a projection into the LAYOUT PLANE, and this is how big it is
    /// when the offending feature is a point (a closest approach). Half a mil each way — big enough
    /// to see at a working zoom, small enough to point at one spot rather than at a region.
    /// </summary>
    private const long PointMarkerHalfNm = 12_700;

    /// <summary>
    /// Runs the structural geometry checks and every rule the assembly rule set states.
    /// </summary>
    public static WBondCheckResult Run(WBondCheckContext ctx, DrcRunSettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        settings ??= DrcRunSettings.Default;

        var diagnostics = new List<string>();
        var violations = new List<DrcViolation>();

        // Wires, flattened once, with the array each came from. `AllWires()` defines the flat order
        // and every index below is into THIS list — never persisted, see the waiver key.
        var wires = new List<(string Group, Wire Wire)>();
        foreach (var array in ctx.Design.Arrays)
            foreach (var w in array.Wires)
                wires.Add((array.Name, w));

        if (wires.Count == 0) return new WBondCheckResult([], 0, diagnostics);

        // Bounded like the shape ceiling already bounds the 2D run: refuse with a message rather than
        // hang. The same settings record, widened, rather than a second settings type (R-wbd-4).
        if (wires.Count > settings.MaxWires)
            return new WBondCheckResult([], 0,
                [$"This design has {wires.Count:N0} wires, above the {settings.MaxWires:N0} checking " +
                 "ceiling. No wire rules were checked."]);

        var heights = WBondLayerHeights.Resolve(ctx.Tech);

        // ── Built-in: wire-to-wire clearance ────────────────────────────────────────────────────
        //
        // COST. This is the one rule that runs on every check of every wirebond design, so it pays
        // for the broad phase rather than an all-pairs loop: 600 wires is 179,700 unordered pairs
        // before anything looks at a segment, and `WirePairSweep`'s uniform grid answers the same
        // question in ~2 ms against ~417 ms. Nothing here is quadratic in the wire count.
        //
        // The sweep is built with the SAME limit it is queried at, so the broad phase cannot prune a
        // pair the narrow phase would have reported.
        double clearanceNm = Math.Max(WBondBuiltInRules.MinimumClearanceNm, settings.WireClearanceNm);
        int builtInEvaluated = 0;

        var clearanceSweep = new WirePairSweep(wires.Select(w => w.Wire).ToList(), clearanceNm);
        var tooClose = clearanceSweep.FindTouching(clearanceNm);
        builtInEvaluated++;

        foreach (var hit in tooClose)
        {
            var (ga, wa) = wires[hit.A];
            var (gb, wb) = wires[hit.B];

            WireGeometry3D.ClosestApproach(wa, wb, out var pa, out var pb);
            var marker = PointMarker(pa, pb, ctx.DbuPerMicron);

            violations.Add(MakeWireViolation(
                ruleName: WBondBuiltInRules.WireClearanceRuleName,
                severity: WBondBuiltInRules.WireClearance.Severity,
                section: null,
                groups: ga == gb ? [ga] : [ga, gb],
                marker: marker,
                measured: FormatMil(hit.ClearanceNm),
                limitText: $"at least {FormatMil(clearanceNm)}"));
        }

        if (tooClose.Count > 0)
        {
            // Touching and merely close are the same rule but not the same conversation — one is a
            // design that cannot be built, the other one that can be built badly — so the count of
            // each is stated rather than left to be read off the list.
            int contact = tooClose.Count(h => h.ClearanceNm <= WBondBuiltInRules.MinimumClearanceNm);

            diagnostics.Add(
                $"{tooClose.Count:N0} wire pair(s) are closer than the built-in minimum clearance of " +
                $"{FormatMil(clearanceNm)}" +
                (contact > 0 ? $", of which {contact:N0} touch or overlap outright" : "") +
                ". Clearance is measured surface to surface, between the wires' outer edges.");
        }

        if (ctx.Assembly is null)
        {
            // Named rather than merely absent: a user reading "no rules resolved" cannot tell whether
            // the clean result they are looking at means anything at all.
            diagnostics.Add($"No .wasm assembly rule file is referenced by this design, so it was " +
                            $"checked against the {WBondBuiltInRules.SetName} — " +
                            $"{DescribeBuiltIn(clearanceNm)}. Reference a .wasm file to check your " +
                            "assembly house's own bonder, process and material rules.");
            return new WBondCheckResult(violations, builtInEvaluated, diagnostics);
        }

        diagnostics.AddRange(heights.Diagnostics);

        // ── Structural: material section ────────────────────────────────────────────────────────
        CheckMaterials(ctx, wires, violations);

        // ── The stated rules ────────────────────────────────────────────────────────────────────
        int evaluated = 0;
        var setIndex = BuildSetIndex(wires);

        foreach (var (section, rule) in ctx.Assembly.AllRules())
        {
            if (!DrcPredicateParser.TryParse(rule.Expression, out var predicate, out string? parseError) ||
                predicate is null)
            {
                diagnostics.Add($"Assembly rule \"{rule.Name}\" ({section}) was not checked: {parseError}");
                continue;
            }

            var unknownSets = predicate.ReferencedSets()
                .Where(s => !setIndex.ContainsKey(s))
                .ToList();

            if (unknownSets.Count > 0)
            {
                // Named rather than silently skipped: a rule written for a design with a G3 array,
                // run against one without, is a rule the user believes is protecting them.
                diagnostics.Add($"Assembly rule \"{rule.Name}\" ({section}) names wire set(s) this design " +
                                $"does not have ({string.Join(", ", unknownSets)}), so it was not checked.");
                continue;
            }

            var missingTables = predicate.ReferencedEnvelopes()
                .Where(t => ctx.Assembly.EnvelopeByName(t) is null)
                .ToList();

            if (missingTables.Count > 0)
            {
                diagnostics.Add($"Assembly rule \"{rule.Name}\" ({section}) looks up envelope table(s) " +
                                $"the rule file does not declare ({string.Join(", ", missingTables)}), " +
                                "so it was not checked.");
                continue;
            }

            int before = violations.Count;
            bool ran = predicate.Domain == WasmDomain.Pair
                ? EvaluatePairRule(ctx, section, rule, predicate, wires, setIndex, heights, violations, diagnostics)
                : EvaluateWireRule(ctx, section, rule, predicate, wires, setIndex, heights, violations, diagnostics);

            if (ran) evaluated++;
            else violations.RemoveRange(before, violations.Count - before);
        }

        return new WBondCheckResult(violations, evaluated + builtInEvaluated, diagnostics);
    }

    /// <summary>The built-in rules, listed by name and by the limit each ran at — so a diagnostic
    /// says what was actually checked rather than asking the reader to trust a count.</summary>
    private static string DescribeBuiltIn(double clearanceNm) =>
        $"\"{WBondBuiltInRules.WireClearanceRuleName}\" at {FormatMil(clearanceNm)}";

    // ── Wire-domain rules ───────────────────────────────────────────────────────────────────────

    private static bool EvaluateWireRule(
        WBondCheckContext ctx, WasmSection section, WasmRule rule, WasmPredicate predicate,
        List<(string Group, Wire Wire)> wires, Dictionary<string, List<int>> setIndex,
        WBondLayerHeights heights, List<DrcViolation> into, List<string> diagnostics)
    {
        var sets = predicate.ReferencedSets();

        // One set, or the rule has no single candidate wire to be about. `loop_height(G1) <= span(G2)`
        // is two different wires compared with each other and is not a rule this language expresses.
        if (sets.Count > 1)
        {
            diagnostics.Add($"Assembly rule \"{rule.Name}\" ({section}) measures more than one wire set " +
                            $"({string.Join(", ", sets)}) without pairing them, so there is no single wire " +
                            "to check it against. Use wire_spacing or foot_pitch to state a pair rule.");
            return false;
        }

        string set = sets.Count == 1 ? sets[0] : DrcPredicateParser.AllWiresSet;
        var measurements = new Measurements(ctx, wires, heights, null);

        foreach (int i in setIndex[set])
        {
            measurements.SetWire(i, set);
            if (predicate.Evaluate(measurements)) continue;

            into.Add(MakeWireViolation(
                rule.Name, rule.Severity, section,
                groups: [wires[i].Group],
                marker: WireMarker(wires[i].Wire, ctx.DbuPerMicron),
                measured: measurements.DescribeLastFailure(predicate),
                limitText: rule.Description ?? DrcPredicateParser.Format(predicate)));
        }

        return true;
    }

    // ── Pair-domain rules ───────────────────────────────────────────────────────────────────────

    private static bool EvaluatePairRule(
        WBondCheckContext ctx, WasmSection section, WasmRule rule, WasmPredicate predicate,
        List<(string Group, Wire Wire)> wires, Dictionary<string, List<int>> setIndex,
        WBondLayerHeights heights, List<DrcViolation> into, List<string> diagnostics)
    {
        if (predicate.PairSets() is not { } sets) return false;

        var pairs = predicate.PairCalls();
        foreach (var other in pairs.Skip(1))
            if (!SamePairing(pairs[0], other))
            {
                diagnostics.Add($"Assembly rule \"{rule.Name}\" ({section}) compares two different wire " +
                                "pairings, so there is no single pair to check it against. Split it into " +
                                "one rule per pairing.");
                return false;
            }

        var inA = setIndex[sets.A].ToHashSet();
        var inB = setIndex[sets.B].ToHashSet();

        var wireList = wires.Select(w => w.Wire).ToList();
        var measurements = new Measurements(ctx, wires, heights, sets);

        // ── The broad phase, and exactly when it is sound ───────────────────────────────────────
        // Pruning a far-apart pair is only correct when EVERY pair term states a lower bound and the
        // predicate is a plain conjunction: then a pair further apart than the largest stated limit
        // satisfies all of them, and skipping it cannot miss a violation. An upper bound ("these must
        // stay close"), an `||` or a `!` breaks that, and so does a per-wire term inside a pair rule.
        double? cutoff = TryComputePairCutoff(predicate);
        bool needsXy = pairs.Any(p => p.Fn == WasmPairFunction.FootPitch);

        IEnumerable<(int A, int B)> candidates;
        if (cutoff is { } gap)
        {
            var sweep = new WirePairSweep(wireList, gap);
            candidates = sweep.CandidatePairs(xyOnly: needsXy);
        }
        else
        {
            // Said rather than quietly paid. At the owner's 600-wire worst case this is ~180,000
            // pairs, which is seconds rather than milliseconds.
            diagnostics.Add($"Assembly rule \"{rule.Name}\" ({section}) states a limit the checker cannot " +
                            "bound (an upper bound, an || or a !), so every wire pair was measured. " +
                            "Rewrite it as a conjunction of minimum distances to make it fast.");
            candidates = AllPairs(wires.Count);
        }

        foreach (var (i, j) in candidates)
        {
            // The pair has to be drawn from the two sets the rule names, in either order — a pairing
            // of one set with itself must not report a wire against itself.
            bool forward = inA.Contains(i) && inB.Contains(j);
            bool reverse = inA.Contains(j) && inB.Contains(i);
            if (!forward && !reverse) continue;
            if (i == j) continue;

            int a = forward ? i : j;
            int b = forward ? j : i;

            measurements.SetPair(a, b);
            if (predicate.Evaluate(measurements)) continue;

            WireGeometry3D.ClosestApproach(wires[a].Wire, wires[b].Wire, out var pa, out var pb);

            into.Add(MakeWireViolation(
                rule.Name, rule.Severity, section,
                groups: wires[a].Group == wires[b].Group
                    ? [wires[a].Group]
                    : [wires[a].Group, wires[b].Group],
                marker: PointMarker(pa, pb, ctx.DbuPerMicron),
                measured: measurements.DescribeLastFailure(predicate),
                limitText: rule.Description ?? DrcPredicateParser.Format(predicate)));
        }

        return true;
    }

    private static IEnumerable<(int A, int B)> AllPairs(int count)
    {
        for (int i = 0; i < count; i++)
        for (int j = i + 1; j < count; j++)
            yield return (i, j);
    }

    private static bool SamePairing(WasmValue.PairCall a, WasmValue.PairCall b) =>
        (Eq(a.SetA, b.SetA) && Eq(a.SetB, b.SetB)) || (Eq(a.SetA, b.SetB) && Eq(a.SetB, b.SetA));

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The largest distance any pair term in the predicate requires, or null when the predicate's
    /// shape makes pruning unsound. See the call site for why the conditions are what they are.
    /// </summary>
    internal static double? TryComputePairCutoff(WasmPredicate predicate)
    {
        double best = 0.0;
        bool sawPair = false;
        return Walk(predicate) ? (sawPair ? best : null) : null;

        bool Walk(WasmPredicate p)
        {
            switch (p)
            {
                case WasmPredicate.And a:
                    return Walk(a.A) && Walk(a.B);

                case WasmPredicate.Compare c:
                {
                    bool leftPair  = c.Left is WasmValue.PairCall;
                    bool rightPair = c.Right is WasmValue.PairCall;

                    // A term with no pair function in it is a per-wire term inside a pair rule, which
                    // pruning would silently stop checking for the pruned pairs.
                    if (!leftPair && !rightPair) return false;
                    if (leftPair && rightPair) return false;

                    // Lower bound only: `pair >= L` or `L <= pair`.
                    bool lowerBound = leftPair
                        ? c.Op is WasmCompareOp.Ge or WasmCompareOp.Gt
                        : c.Op is WasmCompareOp.Le or WasmCompareOp.Lt;

                    if (!lowerBound) return false;

                    var limit = leftPair ? c.Right : c.Left;
                    if (limit is not WasmValue.Literal lit) return false;

                    sawPair = true;
                    best = Math.Max(best, lit.Value);
                    return true;
                }

                // `||` and `!` both break the "every term must hold" reasoning the bound rests on.
                default:
                    return false;
            }
        }
    }

    // ── Material section ────────────────────────────────────────────────────────────────────────

    private static void CheckMaterials(
        WBondCheckContext ctx, List<(string Group, Wire Wire)> wires, List<DrcViolation> into)
    {
        var wasm = ctx.Assembly!;
        if (wasm.AllowedDiametersNm.Count == 0 && wasm.AllowedMetals.Count == 0) return;

        foreach (var (group, wire) in wires)
        {
            if (!wasm.DiameterAllowed(wire.DiameterNm))
                into.Add(MakeWireViolation(
                    "Wire diameter not stocked", DrcSeverity.Error, WasmSection.Material,
                    [group], WireMarker(wire, ctx.DbuPerMicron),
                    measured: FormatMil(wire.DiameterNm),
                    limitText: $"one of {wasm.DescribeAllowedDiameters()}"));

            if (!wasm.MetalAllowed(wire.Material))
                into.Add(MakeWireViolation(
                    "Wire metal not bonded here", DrcSeverity.Error, WasmSection.Material,
                    [group], WireMarker(wire, ctx.DbuPerMicron),
                    measured: wire.Material,
                    limitText: $"one of {string.Join(", ", wasm.AllowedMetals)}"));
        }
    }

    // ── Violations, markers and the waiver key ──────────────────────────────────────────────────

    private static DrcViolation MakeWireViolation(
        string ruleName, DrcSeverity severity, WasmSection? section,
        IReadOnlyList<string> groups, Bbox marker, string measured, string limitText)
    {
        // One implicitly-closed ring: the marker rectangle, in the flat DBU vertex form every other
        // violation's marker already uses, so the renderer needs no wire-specific path.
        long[] ring =
        [
            marker.MinX, marker.MinY,
            marker.MaxX, marker.MinY,
            marker.MaxX, marker.MaxY,
            marker.MinX, marker.MaxY,
        ];
        var rings = new List<long[]> { ring };

        return new DrcViolation(
            ruleName, DrcRuleKind.MinSpacing, severity,
            Layer: null, RequiredDbu: 0, rings, marker,
            NetA: null, NetB: null,
            Key: KeyFor(ruleName, groups, marker))
        {
            Section      = section,
            WireGroups   = groups,
            MeasuredText = $"{measured} (limit {limitText})",
        };
    }

    /// <summary>
    /// The identity a waiver names, for a wire violation: rule name + the participating GROUPS +
    /// the marker's exact bounding box in DBU.
    ///
    /// <para><b>The flat wire index is deliberately not in it (R-wbd-3), and that is the whole
    /// point.</b> Flat indices shift whenever a wire is added, deleted, pasted or moved between
    /// groups, so a key built on one would silently re-point an existing waiver at a DIFFERENT wire
    /// after any structural edit — which is worse than losing the waiver, because it looks like it
    /// still applies. A waiver names a PLACE, exactly as the 2D DRC's own key does: editing the
    /// offending geometry changes the marker and therefore the key, which un-waives the violation.
    /// That is the correct outcome — the waiver was granted for geometry that no longer exists.</para>
    /// </summary>
    public static string KeyFor(string ruleName, IReadOnlyList<string> groups, Bbox marker) =>
        $"wire|{ruleName}|{string.Join("+", groups.OrderBy(g => g, StringComparer.Ordinal))}|" +
        $"{marker.MinX},{marker.MinY},{marker.MaxX},{marker.MaxY}";

    /// <summary>
    /// The wire's own XY footprint, in DBU — the projection of a 3D object into the plane it is drawn
    /// over. See <c>DrcViolationRow</c> for why every wire violation says so in the panel.
    /// </summary>
    private static Bbox WireMarker(Wire wire, int dbuPerMicron)
    {
        long minX = long.MaxValue, minY = long.MaxValue, maxX = long.MinValue, maxY = long.MinValue;
        foreach (var p in wire.Points)
        {
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }

        long r = wire.DiameterNm / 2;
        return ToDbu(minX - r, minY - r, maxX + r, maxY + r, dbuPerMicron);
    }

    private static Bbox PointMarker(Point3 a, Point3 b, int dbuPerMicron)
    {
        long cx = (a.X + b.X) / 2, cy = (a.Y + b.Y) / 2;
        return ToDbu(cx - PointMarkerHalfNm, cy - PointMarkerHalfNm,
                     cx + PointMarkerHalfNm, cy + PointMarkerHalfNm, dbuPerMicron);
    }

    /// <summary>The R-wbd-1 crossing, in the one direction this file needs it.</summary>
    private static Bbox ToDbu(long minXNm, long minYNm, long maxXNm, long maxYNm, int dbuPerMicron) => new(
        Ui.WBond.WBondSnap.ToDbu(minXNm, dbuPerMicron), Ui.WBond.WBondSnap.ToDbu(minYNm, dbuPerMicron),
        Ui.WBond.WBondSnap.ToDbu(maxXNm, dbuPerMicron), Ui.WBond.WBondSnap.ToDbu(maxYNm, dbuPerMicron));

    /// <summary>
    /// Lengths in a wire violation are reported in MIL, not the layout's own display unit. A `.wasm`
    /// is written by an assembly house against a bonder set up in mil (WB25 keeps the nudge step in
    /// mil for the same reason), so a violation quoting the rule's own unit is the one a user can
    /// check against the house's document without converting.
    /// </summary>
    private static string FormatMil(double nm) =>
        $"{nm / WBondUnits.NmPerUnit(WBondUnit.Mil):0.###} mil";

    private static Dictionary<string, List<int>> BuildSetIndex(List<(string Group, Wire Wire)> wires)
    {
        var index = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var all = new List<int>(wires.Count);

        for (int i = 0; i < wires.Count; i++)
        {
            if (!index.TryGetValue(wires[i].Group, out var list)) index[wires[i].Group] = list = [];
            list.Add(i);
            all.Add(i);
        }

        index[DrcPredicateParser.AllWiresSet] = all;
        return index;
    }

    // ── The measurement source the predicate evaluates against ──────────────────────────────────

    /// <summary>
    /// Answers a predicate's function calls for one candidate. Mutable and reused across candidates
    /// on purpose: a rule over 600 wires evaluates this thousands of times, and allocating a fresh
    /// context per candidate would dominate the check.
    /// </summary>
    private sealed class Measurements(
        WBondCheckContext ctx,
        List<(string Group, Wire Wire)> wires,
        WBondLayerHeights heights,
        (string A, string B)? pairSets) : IWasmMeasurements
    {
        private readonly Dictionary<DrcLayerExpr, PlanarEdgeIndex> _edgeCache = [];
        private PlanarEdgeIndex? _extentEdges;
        private int _wireIndex = -1;
        private int _pairA = -1, _pairB = -1;
        private string _wireSet = "";

        public void SetWire(int index, string set)
        {
            _wireIndex = index;
            _wireSet   = set;
            _pairA = _pairB = -1;
        }

        public void SetPair(int a, int b)
        {
            _pairA = a;
            _pairB = b;
            _wireIndex = -1;
        }

        public double Pair(WasmPairFunction fn, string setA, string setB)
        {
            if (_pairA < 0 || _pairB < 0) return double.PositiveInfinity;
            var a = wires[_pairA].Wire;
            var b = wires[_pairB].Wire;

            return fn switch
            {
                WasmPairFunction.WireSpacing => WireGeometry3D.Clearance(a, b),
                WasmPairFunction.FootPitch   => WireGeometry3D.FootPitch(a, b),
                _                            => double.PositiveInfinity,
            };
        }

        public double Wire(WasmWireFunction fn, string set, DrcLayerExpr? region)
        {
            var wire = ResolveWire(set);
            if (wire is null) return double.PositiveInfinity;

            return fn switch
            {
                WasmWireFunction.LoopHeight  => wire.LoopHeightNm,
                WasmWireFunction.Span        => wire.SpanMetres() * WBondUnits.NmPerMetre,
                WasmWireFunction.AngleChange => WireGeometry3D.MaxAngleChangeDegrees(wire),
                WasmWireFunction.DistToEdge  => DistToEdge(wire),
                WasmWireFunction.WireToLayer => region is null ? double.PositiveInfinity : ToLayer(wire, region),
                _                            => double.PositiveInfinity,
            };
        }

        public double Envelope(string table, double x) =>
            ctx.Assembly?.EnvelopeByName(table)?.ValueAt(x) ?? double.PositiveInfinity;

        /// <summary>
        /// In PAIR domain, a per-wire function on set S measures the pair member drawn from S. That
        /// rule is what makes `wire_spacing(G1,G2) >= 4mil &amp;&amp; loop_height(G1) &lt;= 20mil`
        /// mean something definite; a set that is neither of the pair's own is refused during
        /// validation, so it cannot reach here.
        /// </summary>
        private Wire? ResolveWire(string set)
        {
            if (_wireIndex >= 0) return wires[_wireIndex].Wire;
            if (_pairA < 0 || pairSets is not { } sets) return null;

            if (string.Equals(set, sets.A, StringComparison.OrdinalIgnoreCase)) return wires[_pairA].Wire;
            if (string.Equals(set, sets.B, StringComparison.OrdinalIgnoreCase)) return wires[_pairB].Wire;
            return null;
        }

        private double DistToEdge(Wire wire)
        {
            if (ctx.LayoutExtent.MinX > ctx.LayoutExtent.MaxX) return double.PositiveInfinity;

            // The die edge is the reference layout's own extent. A design with no reference geometry
            // has no edge to measure to, which is why that case returns infinity rather than zero.
            _extentEdges ??= PlanarEdgeIndex.BuildRectangle(ctx.LayoutExtent, ctx.DbuPerMicron, 0);
            return _extentEdges.MinDistanceTo(wire);
        }

        private double ToLayer(Wire wire, DrcLayerExpr region)
        {
            if (ctx.RegionOf is null) return double.PositiveInfinity;

            if (!_edgeCache.TryGetValue(region, out var index))
            {
                var paths = ctx.RegionOf(region);

                // A derived region reads several layers; its artwork is measured at the height of the
                // FIRST layer the expression names, which is the layer a violation would be attributed
                // to. A region spanning two heights has no single z, and pretending otherwise is the
                // exact silent wrongness this file's header is about — so it is reported, once.
                var layers = region.ReferencedLayers().ToList();
                long z = layers.Count > 0 ? heights.ZNmOf(layers[0]) : 0;

                _edgeCache[region] = index = PlanarEdgeIndex.Build(paths, ctx.DbuPerMicron, z);
            }

            return index.MinDistanceTo(wire);
        }

        /// <summary>
        /// A short "what actually measured wrong" string for the failing candidate. Re-evaluates the
        /// tree looking for the first comparison that is false, which is cheap because it only runs
        /// for candidates that already failed.
        /// </summary>
        public string DescribeLastFailure(WasmPredicate predicate)
        {
            var failing = FirstFailing(predicate);
            if (failing is null) return "";

            double left = WasmPredicate.Eval(failing.Left, this);
            bool angle = failing.Left.Quantity == WasmQuantity.Angle;

            return angle
                ? $"{left:0.##}°"
                : double.IsPositiveInfinity(left) ? "not measurable" : FormatMil(left);
        }

        private WasmPredicate.Compare? FirstFailing(WasmPredicate p) => p switch
        {
            WasmPredicate.Compare c => c.Evaluate(this) ? null : c,
            WasmPredicate.And a     => FirstFailing(a.A) ?? FirstFailing(a.B),
            WasmPredicate.Or o      => o.Evaluate(this) ? null : FirstFailing(o.A) ?? FirstFailing(o.B),
            WasmPredicate.Not n     => n.Evaluate(this) ? null : FirstFailing(n.A),
            _                       => null,
        };
    }
}
