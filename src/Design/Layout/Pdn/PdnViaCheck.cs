// The worst via of each layer transition, against the limit, with the count that would clear it
// (docs/sonnet-briefs/brief-railrf-6-via-check.md R-rail6-1, R-rail6-4, R-rail6-5; railrf.md §2.4,
//  §4.2).
//
// ── THE WORST VIA, BECAUSE VIAS DO NOT SHARE EQUALLY ───────────────────────────────────────────
//
// §2.4: "railRF knows the current in EACH via, not the average — vias in parallel do not share
// equally, and the one nearest the load routinely carries several times its share."
//
// The mesh already knows this. Brief 3 stamps each barrel as its own element on its own nodes with
// no special case for a group, and brief 5 solved the field; all this file does is read the
// per-element currents back and group them.
//
//     AVERAGING IS THE DEFECT THIS WHOLE DESIGN EXISTS TO AVOID, AND IT IS ONE LINE AWAY AT
//     EVERY STEP. `group.Sum(i) / group.Count` reads entirely naturally and is wrong. The
//     group's TOTAL is a real number and appears below; its MEAN is not, and does not.
//
// A flag saying "this transition is over" is a complaint. A flag saying "six vias, the worst carries
// 0.62 A against a 0.45 A limit, and this many would clear it" is an instruction — which is why the
// count is on the flag and why a re-solve with that count is part of this brief's own gate.
//
// ── IT IS NOT A THERMAL MODEL AND IT IS NOT A CURRENT-DENSITY FIELD ────────────────────────────
//
// §2.7 for the first; Q-7 and §2.4 for the second, both of which put current density as a FIELD and
// the hot-spot map behind it explicitly at step 2. This brief produces flags. It draws nothing — the
// results panel is brief 7's and the board overlay is brief 8's.
//
// ── AND NOTHING HERE SOLVES ANYTHING ───────────────────────────────────────────────────────────
//
// Same rule as the rest of this folder (R-rail3-2): the netlist is already solved, the node voltages
// are already in hand, and a barrel's current is Ohm's law across it. No matrix, no factorisation.

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// One barrel of a transition, with what it actually carried.
/// </summary>
/// <param name="Barrel">The geometry, as the extraction read it.</param>
/// <param name="CurrentA">What flows through it, as a magnitude.</param>
/// <param name="ShareOfTransition">Its fraction of the transition's total current, in 0…1. The
/// number §2.4's sentence is about: an equal split puts every via at <c>1/n</c> and a real one does
/// not.</param>
public sealed record PdnViaCurrent(PdnViaBarrel Barrel, double CurrentA, double ShareOfTransition);

/// <summary>
/// One layer transition: every barrel that joins this pair of conductors in this via field, the
/// current in each, the worst one and the limit.
///
/// <para><b>A transition is a FIELD, not a board-wide layer pair.</b> Every through via on a
/// four-layer board joins L1 to L4, and "add four more vias" is only an instruction about the group
/// under this part — so barrels join one transition when they share a layer pair AND sit within
/// <see cref="PdnViaCheck.FieldLinkageDrillMultiple"/> drill diameters of one another. A via field's
/// pitch is a small multiple of its own drill; two unrelated fields on a board are not.</para>
/// </summary>
/// <param name="FromLayer">The drawing layer at one end of the span.</param>
/// <param name="ToLayer">The drawing layer at the other.</param>
/// <param name="Vias">Every barrel of the field, worst first.</param>
/// <param name="TotalCurrentA">What the transition carries between the two conductors — the SUM of
/// the signed barrel currents, which is the only aggregate of a group that is a real quantity.</param>
/// <param name="Limit">What one barrel of this field may carry, or null where nothing resolvable
/// produced one.</param>
public sealed record PdnViaTransition(
    LayerKey FromLayer,
    LayerKey ToLayer,
    IReadOnlyList<PdnViaCurrent> Vias,
    double TotalCurrentA,
    PdnViaLimit? Limit)
{
    /// <summary>How many barrels. The <i>n</i> of "six vias, ten would clear it".</summary>
    public int Count => Vias.Count;

    /// <summary>The one that carries most — <b>what the flag is on</b>. Never the mean.</summary>
    public PdnViaCurrent? Worst => Vias.Count > 0 ? Vias[0] : null;

    /// <summary>
    /// How far the worst via is above an equal split, as a multiple.
    ///
    /// <para>1 exactly when the field shares evenly, and §2.4's "several times its share" is this
    /// number being 3 or 4. It is also what the count that clears the flag holds constant.</para>
    /// </summary>
    public double PeakingFactor =>
        Worst is { } w && TotalCurrentA > 0 ? w.CurrentA * Count / TotalCurrentA : 1.0;

    /// <summary>True where the worst barrel is over the limit. Null where there is no limit to be
    /// over — which is a note, never a pass and never a flag.</summary>
    public bool? Exceeded =>
        Limit is { } lim && Worst is { } w ? w.CurrentA > lim.AmpsLimit : null;

    /// <summary>
    /// How many barrels this field would need for its worst one to sit at the limit.
    ///
    /// <para><b>The extrapolation is calibrated by the field's own <see cref="PeakingFactor"/>, not by
    /// a constant.</b> A field that shares PERFECTLY improves in proportion to its count: double the
    /// vias and the worst carries half, so <c>m = n·(I_worst/limit)</c> and that is exact. A field
    /// whose worst via carries <i>p</i> times an equal share improves more slowly, because a via
    /// added to it does not take an equal share either — it takes what its own distance from the load
    /// allows, and the ones already nearest keep most of theirs. The same <i>p</i> the mesh measured
    /// is what says by how much, so the count is <c>m = ⌈n·(I_worst/limit)^p⌉</c>, which collapses to
    /// the exact answer at <c>p = 1</c> and is deliberately conservative above it.</para>
    ///
    /// <para><b>Conservative on purpose.</b> A count that leaves the flag standing is worse than one
    /// that adds a via too many: the first is a wrong instruction and the second is a cheap one. The
    /// gate measures it — a six-via field flagged at 1.34× its limit peaks at 1.91, asks for eleven,
    /// and eleven clears it where the nine an equal-share extrapolation would have asked for does
    /// not.</para>
    ///
    /// <para>Null where there is no limit, or where the field is already inside it.</para>
    /// </summary>
    public int? CountThatClears
    {
        get
        {
            if (Limit is not { AmpsLimit: > 0 } lim || Worst is not { } w) return null;
            if (w.CurrentA <= lim.AmpsLimit) return null;

            double ratio = w.CurrentA / lim.AmpsLimit;
            double exponent = Math.Clamp(PeakingFactor, 1.0, 6.0);
            double needed = Count * Math.Pow(ratio, exponent);

            if (double.IsNaN(needed) || needed > int.MaxValue / 2) return null;
            return Math.Max(Count + 1, (int)Math.Ceiling(needed));
        }
    }
}

/// <summary>
/// One flagged transition, with the basis that produced it (R-rail6-4).
/// </summary>
/// <param name="Transition">The field, its barrels and its limit.</param>
public sealed record PdnViaFlag(PdnViaTransition Transition)
{
    /// <summary>Computed from the barrel's geometry, or the drill-size table.</summary>
    public PdnViaLimitBasis Basis => Transition.Limit!.Basis;

    /// <summary>
    /// The sentence, and it is an instruction: the count, the worst via's own current, the limit,
    /// the count that would clear it — and the plating thickness and its provenance, every time
    /// (R-rail6-3).
    /// </summary>
    public string Describe()
    {
        var t = Transition;
        var w = t.Worst!;
        var lim = t.Limit!;

        return
            $"The {t.FromLayer.Layer}/{t.FromLayer.Datatype} → {t.ToLayer.Layer}/{t.ToLayer.Datatype} " +
            $"transition at ({w.Barrel.X}, {w.Barrel.Y}) carries {t.TotalCurrentA:0.###} A through " +
            $"{t.Count} via(s). They do not share it equally: the worst carries {w.CurrentA:0.###} A, " +
            $"{t.PeakingFactor:0.##}× an equal split, against a limit of {lim.Describe()}. " +
            (t.CountThatClears is { } m
                ? $"{m} via(s) of the same kind over the same footprint would clear it."
                : "No realistic count of vias of this kind clears it — this transition needs a " +
                  "larger drill, thicker plating or less current.");
    }
}

/// <summary>
/// What one via check produced: every transition, the ones that are over, and what could not be
/// checked at all.
/// </summary>
/// <param name="Transitions">Every via field of the extraction, worst-first within each.</param>
/// <param name="Flags">The ones whose WORST via exceeds its limit.</param>
/// <param name="Notes">What railRF established rather than found — an unresolved span, a drill the
/// limit could not be computed for. <b>Never mixed with the flags</b>: a note says a check did not
/// happen, a flag says one failed.</param>
public sealed record PdnViaCheckResult(
    IReadOnlyList<PdnViaTransition> Transitions,
    IReadOnlyList<PdnViaFlag> Flags,
    IReadOnlyList<string> Notes)
{
    /// <summary>Nothing to check — a board with no vias on this rail.</summary>
    public static readonly PdnViaCheckResult Empty = new([], [], []);
}

/// <summary>§2.4's via check, over a solved extraction.</summary>
public static class PdnViaCheck
{
    /// <summary>
    /// How close two barrels of the same layer pair must be to be one field, as a multiple of the
    /// larger of their two drill diameters.
    ///
    /// <para>Five, because a via field's pitch is a small multiple of its own drill — a 0.3 mm drill
    /// sits on a 0.6–1.0 mm pitch, which is 2–3× — and because the alternative readings are both
    /// wrong in a way that matters. Keying a transition on the layer pair alone makes every through
    /// via on the board one group, and "ten would clear it" then says nothing about the part that is
    /// over. Keying it on the netlist node pair splits a real field into one group per barrel the
    /// moment the mesh is refined under it, and a group of one cannot share unequally.</para>
    /// </summary>
    public const double FieldLinkageDrillMultiple = 5.0;

    /// <summary>
    /// The check.
    /// </summary>
    /// <param name="pdn">The extraction, whose origins carry each barrel's own geometry.</param>
    /// <param name="nodeVoltages">The solved field — brief 5's <c>RailDcResult.NodeVoltages</c>.</param>
    /// <param name="riseCelsius">The rise budget, <c>RailSettings.ViaTemperatureRiseCelsius</c>. It
    /// is a setting on THIS check and nothing else in railRF reads it.</param>
    /// <param name="dbuPerMicron">The artwork's own resolution. The barrels carry their geometry in
    /// METRES and their positions in DBU, and the field clustering compares one against the other.</param>
    public static PdnViaCheckResult Run(
        PdnNetlist pdn, IReadOnlyDictionary<int, double> nodeVoltages, double riseCelsius,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        var notes = new List<string>();

        // ── R-rail6-5: a span that could not be resolved is REPORTED, and gets no flag ─────────
        //
        // Brief 3 reports rather than assumes when it meets a blind or buried span it cannot
        // resolve, and that report reaches here: those holes carry no barrel, so there is no
        // transition for them and nothing to flag. A missing flag and a note is honest; a flag
        // computed from a guessed 1.6 mm span is a number with no basis at all.
        if (pdn.Provenance.UnresolvedViaSpans > 0)
            notes.Add(
                $"{pdn.Provenance.UnresolvedViaSpans} hole(s) could not be resolved to a layer span, " +
                "so they carry no barrel and the via check makes no finding about them. A limit " +
                "computed from an assumed span would be a number with no basis at all — declare the " +
                "span on the stackup's via entry to have them checked.");

        var barrels = new List<(PdnViaBarrel Barrel, double Signed)>();

        foreach (var o in pdn.Origins)
        {
            if (o.Kind != PdnOriginKind.Via || o.Barrel is not { } barrel) continue;
            if (barrel.ResistanceOhms is not > 0) continue;

            var c = pdn.Netlist.Components[o.ComponentIndex];
            if (!nodeVoltages.TryGetValue(c.Nodes[0], out double va) ||
                !nodeVoltages.TryGetValue(c.Nodes[1], out double vb)) continue;

            // Ohm's law across the barrel, oriented FromLayer → ToLayer, which is the order the
            // extraction stamped its two nodes in. Signed, because a transition's total is a sum of
            // currents in a direction and a sum of magnitudes would over-report a field that has
            // current going both ways through it.
            barrels.Add((barrel, (va - vb) / barrel.ResistanceOhms));
        }

        if (barrels.Count == 0) return new PdnViaCheckResult([], [], notes);

        var transitions = new List<PdnViaTransition>();
        var flags = new List<PdnViaFlag>();
        int noLimit = 0;

        foreach (var field in Fields(barrels, dbuPerMicron))
        {
            double total = 0;
            foreach (var (_, signed) in field) total += signed;
            total = Math.Abs(total);

            var vias = new List<PdnViaCurrent>(field.Count);
            foreach (var (barrel, signed) in field)
            {
                double i = Math.Abs(signed);
                // The SHARE of the transition's own total, which is the number §2.4 is about.
                // Deliberately NOT a mean: the mean of this group is one line away and it is the
                // quantity the whole via check exists to refuse to report.
                vias.Add(new PdnViaCurrent(barrel, i, total > 0 ? i / total : 0.0));
            }

            vias.Sort(static (p, q) => q.CurrentA.CompareTo(p.CurrentA));

            var first = field[0].Barrel;
            var limit = PdnViaCurrentLimit.Compute(
                first.DrillMetres, first.PlatingMetres, first.Basis, first.SpanMetres, riseCelsius);

            var transition = new PdnViaTransition(first.FromLayer, first.ToLayer, vias, total, limit);
            transitions.Add(transition);

            if (limit is null) { noLimit++; continue; }
            if (transition.Exceeded is true) flags.Add(new PdnViaFlag(transition));
        }

        if (noLimit > 0)
            notes.Add(
                $"{noLimit} via field(s) carry no current limit: nothing states a plating thickness " +
                "for them and their drill is outside the range the drill-size guidance covers, so " +
                "there is neither an annulus to compute one from nor a row to fall back to. " +
                "State the plated-wall thickness on the stackup's via entry, or set one on the " +
                "document, to have them checked.");

        // Worst-first across the board: the largest excess over its own limit, so a flag on a
        // 0.2 mm signal via and one on a 1.2 mm power via rank by how far over each is rather than
        // by how many amps each carries.
        flags.Sort(static (p, q) =>
            (q.Transition.Worst!.CurrentA / q.Transition.Limit!.AmpsLimit)
            .CompareTo(p.Transition.Worst!.CurrentA / p.Transition.Limit!.AmpsLimit));

        transitions.Sort(static (p, q) => q.TotalCurrentA.CompareTo(p.TotalCurrentA));

        return new PdnViaCheckResult(transitions, flags, notes);
    }

    /// <summary>
    /// The via fields: single-linkage clustering within each layer pair, through a bucket grid so a
    /// board with thousands of holes does not cost a pairwise sweep.
    /// </summary>
    private static List<List<(PdnViaBarrel Barrel, double Signed)>> Fields(
        List<(PdnViaBarrel Barrel, double Signed)> barrels, int dbuPerMicron)
    {
        double dbuPerMetre = Math.Max(1, dbuPerMicron) * 1e6;

        var byPair = new Dictionary<(LayerKey, LayerKey), List<int>>();

        for (int i = 0; i < barrels.Count; i++)
        {
            var b = barrels[i].Barrel;
            var key = (b.FromLayer, b.ToLayer);
            if (!byPair.TryGetValue(key, out var list)) byPair[key] = list = [];
            list.Add(i);
        }

        var fields = new List<List<(PdnViaBarrel, double)>>();

        foreach (var (_, members) in byPair)
        {
            double largestDrill = 0;
            foreach (int i in members) largestDrill = Math.Max(largestDrill, barrels[i].Barrel.DrillMetres);

            // The linkage distance, in the artwork's own coordinates: the barrels carry their
            // positions in DBU and their drills in metres, so this is the one place the two meet.
            double linkDbu = largestDrill * FieldLinkageDrillMultiple * dbuPerMetre;
            if (!(linkDbu > 0)) linkDbu = 1;

            var parent = new Dictionary<int, int>();
            foreach (int i in members) parent[i] = i;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) parent[ra] = rb; }

            long bucket = (long)Math.Max(1, Math.Ceiling(linkDbu));
            var grid = new Dictionary<(long, long), List<int>>();

            foreach (int i in members)
            {
                var b = barrels[i].Barrel;
                var cell = (Div(b.X, bucket), Div(b.Y, bucket));
                if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = [];
                list.Add(i);
            }

            double link2 = linkDbu * linkDbu;

            foreach (var ((cx, cy), list) in grid)
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                    {
                        if (!grid.TryGetValue((cx + dx, cy + dy), out var other)) continue;
                        foreach (int i in list)
                            foreach (int j in other)
                            {
                                if (i >= j) continue;
                                double ddx = barrels[i].Barrel.X - (double)barrels[j].Barrel.X;
                                double ddy = barrels[i].Barrel.Y - (double)barrels[j].Barrel.Y;
                                if (ddx * ddx + ddy * ddy <= link2) Union(i, j);
                            }
                    }

            var grouped = new Dictionary<int, List<(PdnViaBarrel, double)>>();
            foreach (int i in members)
            {
                int root = Find(i);
                if (!grouped.TryGetValue(root, out var g)) grouped[root] = g = [];
                g.Add(barrels[i]);
            }

            fields.AddRange(grouped.Values);
        }

        return fields;
    }

    private static long Div(long v, long bucket) =>
        v >= 0 ? v / bucket : (v - bucket + 1) / bucket;
}
