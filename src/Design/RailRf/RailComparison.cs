// Two designs, matched — and everything it could not match, named
// (docs/design/railrf.md §2.5, §2.1 Q3; docs/sonnet-briefs/brief-railrf-16-ab-comparison.md
//  R-rail16-2, R-rail16-3, R-rail16-8).
//
// ══ THE MATCH IS THE FEATURE, AND THE PROHIBITION IS THE HALF THAT MATTERS ═════════════════════
//
// §2.5, verbatim: "railRF matches the two by NET NAME where it can and by REFDES for the parts,
// falling back to PART NUMBER for the models. Where it cannot match something, IT SAYS SO AND ASKS
// — it does not pair things by proximity or by guessing."
//
// Three rules and a prohibition:
//
//   1. Nets by net name.
//   2. Parts by refdes.
//   3. Models by part number, where a refdes did not match — and only where exactly one leftover on
//      each side carries that part number, because three unmatched 100 nF 0402s against two is not
//      a pairing, it is a question.
//   4. NOTHING by proximity, by value, by footprint or by any geometric similarity.
//
// ── HOW RULE 4 IS ENFORCED: BY THE SIGNATURE ──────────────────────────────────────────────────
//
// <see cref="Match"/> takes two DOCUMENTS and nothing else. No artwork, no placement file, no
// technology, no shapes — so there is no coordinate in scope to pair by, and a future edit that
// wanted to pair by proximity would have to widen the signature to do it. That is a stronger
// guarantee than a rule written in a comment, and it is why the parameter list is what it is.
//
// The one coordinate this file reads at all is <see cref="RailPortAnchor.Point"/>, and it is
// compared for EXACT equality and never for nearness. That is not a loophole: an exact match is an
// identity, and after the re-layout this whole feature exists to judge, two ports at exactly the
// same DBU are the same port that did not move. Two ports 0.2 mm apart are unmatched, and they stay
// unmatched however close they get.
//
// ── R-rail16-3: WHY THE PORTS PAIR BY REFDES AND PIN ──────────────────────────────────────────
//
// §2.5: "Sources and loads pair by refdes and pin for the same reason §2.2 anchors them there:
// pairing by coordinate is exactly what a re-layout breaks, and a comparison whose ports moved is a
// comparison of nothing."
//
// A coordinate-anchored port is therefore not refused — sometimes it is all there is — but it is
// never silent: every pair where either side fell back to a coordinate is on
// <see cref="RailComparison.CoordinateAnchored"/> and reaches the report as a stated caveat.
//
// ── R-rail16-8: THE ONE MISMATCH THAT IS A REFUSAL ────────────────────────────────────────────
//
// §2.2 gives <see cref="RailReferenceExtent.Infinite"/> partly for this: it is "useful as an upper
// bound and for comparing two different outlines on equal terms". The corollary is that a
// comparison whose two sides used DIFFERENT reference extents is meaningless — one side's return
// path was taken as the copper that is there and the other's as a sheet that is not — and there is
// no honest way to present the difference, because every number on both sides moves. So it is the
// one place in this feature where a mismatch stops the comparison rather than appearing on it.
//
// ── FRAMEWORK-FREE, AND IT SOLVES NOTHING ─────────────────────────────────────────────────────
//
// This file matches documents. It does not sweep, it does not solve and it draws nothing — the
// results are paired by <see cref="RailComparisonReport"/>, which takes this match plus what each
// side's own run produced.

namespace CircuitRF.Design.RailRf;

/// <summary>Which of §2.5's rules paired a row, or failed to.</summary>
public enum RailMatchRule
{
    /// <summary>Rule 1 — the rail's own power net.</summary>
    NetName,

    /// <summary>Rule 2 — the part's reference designator.</summary>
    Refdes,

    /// <summary>Rule 3 — the internal part number, where a refdes did not match.</summary>
    PartNumber,

    /// <summary>R-rail16-3 — a source or load port, by refdes and pin.</summary>
    Anchor,
}

/// <summary>Which design a row belongs to.</summary>
public enum RailSide
{
    /// <summary>The design that is known to work — §2.5's "the reference passes".</summary>
    Reference,

    /// <summary>The design being judged — §2.5's "does yours?".</summary>
    Target,
}

/// <summary>
/// Something on one design with nothing on the other, or something that could have paired two ways.
///
/// <para><b>This list is an output, not an error list.</b> §2.5's rule is that railRF says so and
/// ASKS; a comparison that quietly dropped an unmatched part would report a delta with no
/// explanation for it anywhere on the page.</para>
/// </summary>
/// <param name="Rule">Which rule was being applied when it failed.</param>
/// <param name="Side">Which design the thing is on. Both appear where a pairing was ambiguous.</param>
/// <param name="Key">What it is called — a refdes, a part number, an anchor.</param>
/// <param name="Why">The sentence the report prints.</param>
public sealed record RailUnmatched(RailMatchRule Rule, RailSide Side, string Key, string Why)
{
    /// <summary>The row as the report's own unmatched block reads it.</summary>
    public string Describe() =>
        $"{Key} ({(Side == RailSide.Reference ? "reference" : "target")} only): {Why}";
}

/// <summary>
/// One part on both designs.
/// </summary>
/// <param name="Key">What pairs them — the shared refdes, or the shared part number under rule 3.</param>
/// <param name="By">Which rule paired them. <b>Shown on the report</b>: a pair made by part number
/// is a part that CHANGED REFDES, which a reader comparing two mounting inductances needs to know
/// before believing the row.</param>
/// <param name="Reference">Its row on the reference design.</param>
/// <param name="Target">Its row on the design being judged.</param>
public sealed record RailPartPair(string Key, RailMatchRule By, RailPart Reference, RailPart Target)
{
    /// <summary>How the row names itself — one refdes, or both where they differ.</summary>
    public string Name =>
        string.Equals(Reference.Refdes, Target.Refdes, StringComparison.OrdinalIgnoreCase)
            ? Reference.Refdes
            : $"{Reference.Refdes} → {Target.Refdes}";
}

/// <summary>
/// One source or load port on both designs.
/// </summary>
/// <param name="Key">The anchor's own spelling — <c>U1.VDD</c>, or the DBU point.</param>
/// <param name="ReferenceIndex">Its index in the reference rail's own list, so a result row matches
/// a document row.</param>
/// <param name="TargetIndex">Its index in the target rail's.</param>
/// <param name="Reference">The reference design's anchor.</param>
/// <param name="Target">The judged design's anchor.</param>
public sealed record RailPortPair(
    string Key,
    int ReferenceIndex,
    int TargetIndex,
    RailPortAnchor Reference,
    RailPortAnchor Target)
{
    /// <summary>R-rail16-3 — true where either side fell back to a coordinate. <b>Never silent</b>:
    /// see this file's header.</summary>
    public bool ByCoordinate => !Reference.IsPad || !Target.IsPad;

    /// <summary>The caveat sentence such a pair puts on the report.</summary>
    public string CoordinateCaveat() =>
        !Reference.IsPad && !Target.IsPad
            ? $"{Key}: both sides are anchored by coordinate, not by a refdes and pin. A pad moves " +
              "when a board is re-laid out and a coordinate does not, so this pair holds only while " +
              "the two designs put that port at the same place."
            : $"{Key}: the {(Reference.IsPad ? "target" : "reference")} side is anchored by " +
              "coordinate rather than by a refdes and pin, so railRF cannot check that these two " +
              "ports are the same port.";
}

/// <summary>
/// Two designs' rails, matched — §2.5's first half. <see cref="Refusal"/> non-null means NOTHING was
/// matched, which is the contract every other railRF result already states.
/// </summary>
/// <param name="Refusal">Why nothing was matched, or null.</param>
/// <param name="Reference">The reference design's rail.</param>
/// <param name="Target">The judged design's rail.</param>
/// <param name="Parts">The parts that paired, in the reference rail's own order.</param>
/// <param name="Sources">The source ports that paired.</param>
/// <param name="Loads">The load and observation ports that paired.</param>
/// <param name="Unmatched">Everything that did not — <b>an output, not an error list</b>.</param>
/// <param name="Notes">What railRF established that neither document stated.</param>
public sealed record RailComparison(
    string? Refusal,
    RailSpec? Reference,
    RailSpec? Target,
    IReadOnlyList<RailPartPair> Parts,
    IReadOnlyList<RailPortPair> Sources,
    IReadOnlyList<RailPortPair> Loads,
    IReadOnlyList<RailUnmatched> Unmatched,
    IReadOnlyList<string> Notes)
{
    /// <summary>What the two designs are called on the report.</summary>
    public string ReferenceName { get; init; } = "reference";

    /// <summary>The judged design's name.</summary>
    public string TargetName { get; init; } = "target";

    /// <summary>R-rail16-3's list — every pair where either side used the coordinate fallback.</summary>
    public IReadOnlyList<RailPortPair> CoordinateAnchored =>
        [.. Sources.Concat(Loads).Where(p => p.ByCoordinate)];

    /// <summary>True where nothing at all failed to pair.</summary>
    public bool Complete => Refusal is null && Unmatched.Count == 0;

    private static RailComparison Refused(string why) => new(why, null, null, [], [], [], [], []);

    /// <summary>
    /// Match one rail on two designs. <b>Documents only</b> — see this file's header for why the
    /// parameter list is the enforcement of rule 4.
    /// </summary>
    /// <param name="reference">The design that is known to work.</param>
    /// <param name="target">The design being judged.</param>
    /// <param name="railName">Which rail, on both. §6's scope: <c>+1V8</c> against <c>+1V8</c> —
    /// there is no cross-rail comparison, because comparing two different rails is not a question
    /// this answers.</param>
    /// <param name="referenceName">What to call the reference on the report.</param>
    /// <param name="targetName">What to call the judged design.</param>
    public static RailComparison Match(
        RailDocument reference,
        RailDocument target,
        string railName,
        string? referenceName = null,
        string? targetName = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);

        string refName = Pick(referenceName, reference.Name, "reference");
        string tgtName = Pick(targetName, target.Name, "target");

        if (string.IsNullOrWhiteSpace(railName))
            return Refused(
                "No rail was named. A comparison is of one rail against the same rail on the other " +
                "design, so railRF needs to be told which — there is no comparison of a board " +
                $"against a board. {refName} declares {Join(reference)}; {tgtName} declares {Join(target)}.");

        if (reference.Rail(railName) is not { } a)
            return Refused(
                $"{refName} declares no rail called '{railName}'. It declares {Join(reference)}. A " +
                "rail is matched by name, so the two designs have to call it the same thing — " +
                "rename it on one of them, or compare a rail they share.");

        if (target.Rail(railName) is not { } b)
            return Refused(
                $"{tgtName} declares no rail called '{railName}'. It declares {Join(target)}. A " +
                "rail is matched by name, so the two designs have to call it the same thing — " +
                "rename it on one of them, or compare a rail they share.");

        // ── R-rail16-8, and it is checked FIRST because it invalidates every other row ─────────
        if (a.ReferenceExtent != b.ReferenceExtent)
            return Refused(
                $"Rail '{railName}' was computed against a {Extent(a.ReferenceExtent)} reference on " +
                $"{refName} and a {Extent(b.ReferenceExtent)} one on {tgtName}. Those are two " +
                "different return paths, so every number on both sides moves and no delta between " +
                "them means anything. Set both to the same extent and run them again — " +
                $"'{Extent(RailReferenceExtent.Infinite)}' is the one that compares two different " +
                "outlines on equal terms.");

        var unmatched = new List<RailUnmatched>();
        var notes = new List<string>();

        MatchNet(a, b, railName, refName, tgtName, unmatched, notes);

        var parts   = MatchParts(a, b, refName, tgtName, unmatched, notes);
        var sources = MatchPorts(
            [.. a.Sources.Select(s => s.Anchor)], [.. b.Sources.Select(s => s.Anchor)],
            "source", refName, tgtName, unmatched);
        var loads   = MatchPorts(
            [.. a.Loads.Select(l => l.Anchor)], [.. b.Loads.Select(l => l.Anchor)],
            "load", refName, tgtName, unmatched);

        foreach (var pair in sources.Concat(loads).Where(p => p.ByCoordinate))
            notes.Add(pair.CoordinateCaveat());

        return new RailComparison(null, a, b, parts, sources, loads, unmatched, notes)
        {
            ReferenceName = refName,
            TargetName    = tgtName,
        };
    }

    // ── rule 1: nets, by net name ────────────────────────────────────────────

    /// <summary>
    /// The rail's own power net. <b>A mismatch is reported, not refused</b>: two designs can call
    /// one supply <c>+1V8</c> and <c>VDD_1V8</c> and still be the same supply, and the user is the
    /// one who knows. What railRF will not do is decide for them silently.
    /// </summary>
    private static void MatchNet(
        RailSpec a, RailSpec b, string railName, string refName, string tgtName,
        List<RailUnmatched> unmatched, List<string> notes)
    {
        string? na = Blank(a.NetName);
        string? nb = Blank(b.NetName);

        if (na is null && nb is null)
        {
            notes.Add($"Neither design states a net name for rail '{railName}', so rule 1 had " +
                      "nothing to match on and the rail name is all that pairs them.");
            return;
        }

        if (na is null || nb is null)
        {
            var side = na is null ? RailSide.Target : RailSide.Reference;
            unmatched.Add(new RailUnmatched(
                RailMatchRule.NetName, side, na ?? nb!,
                $"only {(side == RailSide.Reference ? refName : tgtName)} names the net rail " +
                $"'{railName}' sits on. The other states none, so railRF matched the rail by its " +
                "name alone."));
            return;
        }

        if (!string.Equals(na, nb, StringComparison.OrdinalIgnoreCase))
            unmatched.Add(new RailUnmatched(
                RailMatchRule.NetName, RailSide.Reference, na,
                $"rail '{railName}' sits on net '{na}' on {refName} and on '{nb}' on {tgtName}. " +
                "railRF matched the rail by its name and did not assume the two nets are the same " +
                "supply — rename one of them if they are."));
    }

    // ── rules 2 and 3: parts by refdes, then models by part number ───────────

    private static IReadOnlyList<RailPartPair> MatchParts(
        RailSpec a, RailSpec b, string refName, string tgtName,
        List<RailUnmatched> unmatched, List<string> notes)
    {
        var pairs = new List<RailPartPair>();

        var leftRemaining  = new List<RailPart>(a.Parts);
        var rightRemaining = new List<RailPart>(b.Parts);

        // Rule 2. RailSpec.Refusal already forbids a repeated refdes on one rail, so the lookup is
        // unambiguous by construction and nothing here has to choose between two candidates.
        var byRefdes = rightRemaining
            .Where(p => p.Refdes.Length > 0)
            .ToDictionary(p => p.Refdes, p => p, StringComparer.OrdinalIgnoreCase);

        foreach (var part in a.Parts)
        {
            if (part.Refdes.Length == 0 || !byRefdes.TryGetValue(part.Refdes, out var mate)) continue;
            pairs.Add(new RailPartPair(part.Refdes, RailMatchRule.Refdes, part, mate));
            leftRemaining.Remove(part);
            rightRemaining.Remove(mate);
        }

        // Rule 3 — and only where it is unambiguous. Three unmatched 100 nF 0402s on one side
        // against two on the other is not a pairing, it is the question §2.5 says railRF asks.
        foreach (var group in leftRemaining
                     .Where(p => p.PartNumber.Length > 0)
                     .GroupBy(p => p.PartNumber, StringComparer.OrdinalIgnoreCase)
                     .ToList())
        {
            var mates = rightRemaining
                .Where(p => string.Equals(p.PartNumber, group.Key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (group.Count() != 1 || mates.Count != 1) continue;

            var left = group.First();
            var right = mates[0];

            pairs.Add(new RailPartPair(group.Key, RailMatchRule.PartNumber, left, right));
            notes.Add($"{left.Refdes} on {refName} and {right.Refdes} on {tgtName} both carry part " +
                      $"number {group.Key} and neither refdes appears on the other design, so they " +
                      "were paired by part number — the part kept its place and changed its " +
                      "designator.");

            leftRemaining.Remove(left);
            rightRemaining.Remove(right);
        }

        foreach (var part in leftRemaining)
            unmatched.Add(new RailUnmatched(
                RailMatchRule.Refdes, RailSide.Reference, Name(part),
                Reason(part, tgtName, rightRemaining)));

        foreach (var part in rightRemaining)
            unmatched.Add(new RailUnmatched(
                RailMatchRule.Refdes, RailSide.Target, Name(part),
                Reason(part, refName, leftRemaining)));

        // The report reads the parts table in the reference's own order, because that is the order a
        // reviewer already has in front of them.
        var order = a.Parts.Select((p, i) => (p.Refdes, i))
                           .ToDictionary(x => x.Refdes, x => x.i, StringComparer.OrdinalIgnoreCase);
        pairs.Sort((p, q) => Index(p).CompareTo(Index(q)));
        return pairs;

        int Index(RailPartPair pair) =>
            order.TryGetValue(pair.Reference.Refdes, out int i) ? i : int.MaxValue;

        static string Name(RailPart part) =>
            part.Refdes.Length > 0 ? part.Refdes : $"(no refdes, {part.PartNumber})";

        static string Reason(RailPart part, string there, List<RailPart> others)
        {
            int sameNumber = part.PartNumber.Length == 0 ? 0 : others.Count(
                o => string.Equals(o.PartNumber, part.PartNumber, StringComparison.OrdinalIgnoreCase));

            return sameNumber switch
            {
                0 => $"nothing on {there} carries that refdes, " +
                     (part.PartNumber.Length == 0
                         ? "and this row names no part number for rule 3 to fall back to."
                         : $"and no unmatched row there carries part number {part.PartNumber}."),
                1 => $"rule 3 found one unmatched {part.PartNumber} on {there} as well, but another " +
                     "row had already claimed it — check the two designs' refdeses.",
                _ => $"{sameNumber} unmatched rows on {there} carry part number {part.PartNumber}, " +
                     "so railRF will not choose one — it does not pair parts by value, footprint or " +
                     "position.",
            };
        }
    }

    // ── R-rail16-3: sources and loads, by refdes and pin ─────────────────────

    /// <summary>
    /// Ports paired by their anchor. A pad anchor's key is its refdes and pin; a coordinate anchor's
    /// key is its exact DBU point, <b>never a nearest one</b> — see this file's header.
    /// </summary>
    private static IReadOnlyList<RailPortPair> MatchPorts(
        IReadOnlyList<RailPortAnchor> left,
        IReadOnlyList<RailPortAnchor> right,
        string what,
        string refName,
        string tgtName,
        List<RailUnmatched> unmatched)
    {
        var pairs = new List<RailPortPair>();
        var taken = new bool[right.Count];

        for (int i = 0; i < left.Count; i++)
        {
            string key = KeyOf(left[i]);
            int mate = -1;

            for (int j = 0; j < right.Count; j++)
            {
                if (taken[j] || !string.Equals(key, KeyOf(right[j]), StringComparison.OrdinalIgnoreCase))
                    continue;
                mate = j;
                break;
            }

            if (mate < 0)
            {
                unmatched.Add(new RailUnmatched(
                    RailMatchRule.Anchor, RailSide.Reference, left[i].Describe(),
                    Why(what, left[i], tgtName)));
                continue;
            }

            taken[mate] = true;
            pairs.Add(new RailPortPair(
                left[i].Describe(), i, mate, left[i], right[mate]));
        }

        for (int j = 0; j < right.Count; j++)
            if (!taken[j])
                unmatched.Add(new RailUnmatched(
                    RailMatchRule.Anchor, RailSide.Target, right[j].Describe(),
                    Why(what, right[j], refName)));

        return pairs;

        static string Why(string what, RailPortAnchor anchor, string there)
        {
            return anchor.IsPad
                ? $"a {what} whose refdes and pin appear on no {what} of {there}. Ports pair by " +
                  "refdes and pin — a re-layout moves the pad and not the name, so railRF does not " +
                  "look for one nearby."
                : $"a {what} anchored by coordinate. No {what} of {there} sits at exactly that " +
                  "point, and railRF will not pair two ports because they are close: that is the " +
                  "one mistake a re-layout is guaranteed to make plausible.";
        }
    }

    /// <summary>
    /// The key two anchors pair on. <b>Case-insensitive on the names and exact on the point.</b>
    /// </summary>
    private static string KeyOf(RailPortAnchor anchor) =>
        anchor.IsPad
            ? (anchor.Pin is { Length: > 0 } pin ? $"{anchor.Refdes}.{pin}" : anchor.Refdes!)
            : anchor.Point is { } xy ? $"@{xy.X},{xy.Y}" : "@";

    // ── wording ──────────────────────────────────────────────────────────────

    private static string Extent(RailReferenceExtent extent) => extent switch
    {
        RailReferenceExtent.AsImported      => "as-imported",
        RailReferenceExtent.FilledToOutline => "filled-to-outline",
        _                                   => "infinite",
    };

    private static string Join(RailDocument doc) =>
        doc.Rails.Count == 0
            ? "no rails"
            : string.Join(", ", doc.Rails.Select(r => $"'{r.Name}'"));

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Pick(string? given, string? documentName, string fallback) =>
        Blank(given) ?? Blank(documentName) ?? fallback;
}
