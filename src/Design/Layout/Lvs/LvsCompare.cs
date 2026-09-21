// The comparison — brief-lvs-7-comparison.md, docs/design/lvs.md §6.2.
//
//   anchors ──► initial colours ──► refine to fixed point ──► classes ──► correspondence
//                                                                │
//                                                                └──► divergence, where it diverges
//
// ── WHAT THE COLOUR CARRIES, AND THE TWO THINGS THE BRIEF ASKED FOR THAT IT CANNOT ────────────
//
// R-lvs7-3a asks the initial device colour to hash the canonical DeviceType and the parameter
// CLASS alongside the terminal count and the anchor. Neither can be in a colour, and the reason is
// the same for both: **a colour component must be EQUAL on both sides for a correct design**, and
// those two are not.
//
//   * The TYPE is not. A board drawn the ordinary way gives DeviceKind.Cell plus a land-pattern
//     directory on the layout side and DeviceKind.Resistor with no directory on the schematic
//     side — see DeviceType.CouldBe, which documents why that asymmetry is irreducible and why
//     folding the footprint into the identity would cost brief 10 its property finding. Hashing it
//     would put every part on every ordinary board in a class of its own on each side.
//   * The PARAMETER CLASS is not. A MIM capacitor's layout parameters are its generator's (w, l),
//     and its schematic component's are C and Footprint. The NAME SETS have nothing in common, so
//     hashing them would separate every PCell from its own symbol.
//
// So the type is what DeviceType says it is — **a veto, not evidence** — applied where a pairing
// is actually proposed, and a pairing it refuses is reported as lvs.device.type-mismatch rather
// than as two anonymous unmatched devices (R-lvs7-5d). Parameters are brief 10's entirely.
//
// ── WHAT AN ANCHOR IS FOR, WHICH IS NOT WHAT THE REFINEMENT IS FOR ───────────────────────────
//
// An anchored pair is MATCHED. The refinement's job is to match what is left and to say where an
// anchor is refuted — not to unpair one. That distinction is the whole difference between a report
// with six findings and a report with four hundred: colour refinement is an isomorphism test, so
// ANY local difference — one deleted capacitor changing one net's degree — propagates outwards and
// would tear apart every correctly-named part around it. The names are what stop the cascade, and
// they are also the thing a fault cannot forge, because the designer wrote them before the fault
// existed.
//
// An anchor is refuted when EVERY terminal of the pair reaches copper belonging to some other
// schematic net (R-lvs7-2b). One terminal doing that is a mis-wiring and is reported as one;
// all of them is a pairing that was simply wrong, and keeping it would spray a wrong-net line per
// pin. The anchor is dropped and the whole comparison re-run ONCE without it (R-lvs7-2c) — once,
// never to a fixed point, because a loop that keeps dropping anchors ends up comparing two
// anonymous graphs, which is the answer we were trying not to give.
//
// ── DETERMINISM (R-lvs7-6a) ──────────────────────────────────────────────────────────────────
//
// No GetHashCode, no dictionary enumeration reaching the answer, no parallelism. Every list is
// built in an explicit order and every tie is broken by provenance — instance path, then
// designator, then the document's own ordinal. Colours are dense integers assigned by sorting,
// never hashes of strings, so "same colour" is an identity rather than a probability.

using System.Linq;
using CircuitRF.Diagnostics;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Lvs;

/// <summary>Which half of a netlist a pair is about.</summary>
public enum LvsObjectKind
{
    /// <summary>A device.</summary>
    Device,

    /// <summary>A net.</summary>
    Net,
}

/// <summary>Why two objects were paired — <b>and how much the pairing is worth</b>.</summary>
public enum LvsPairedBy
{
    /// <summary>Both documents named it, and the structure did not refute them.</summary>
    Anchor,

    /// <summary>The refinement put it alone in its class on both sides.</summary>
    Structure,

    /// <summary>
    /// One of <i>n</i> interchangeable objects, paired by a tie-break. <b>Arbitrary</b>
    /// (R-lvs7-4c): a finding naming this object may mean any other in its group.
    /// </summary>
    Symmetry,
}

/// <summary>One correspondence.</summary>
/// <param name="Kind">Device or net.</param>
/// <param name="Schematic">Index into the schematic netlist's own list.</param>
/// <param name="Layout">Index into the layout netlist's own list.</param>
/// <param name="SchematicName">What the schematic calls it.</param>
/// <param name="LayoutName">What the layout calls it.</param>
/// <param name="By">How confident the pairing is, and whether it is arbitrary.</param>
public sealed record LvsPair(
    LvsObjectKind Kind, int Schematic, int Layout,
    string SchematicName, string LayoutName, LvsPairedBy By);

/// <summary>
/// Two schematic nets on one piece of copper — <b>one pair, not one group</b> (R-lvs8-4d).
/// </summary>
/// <remarks>
/// <b>Structure as well as a sentence.</b> The comparison knows WHICH nets and WHICH copper by
/// index; the report has to walk the artwork from one to the other to say how they are joined, and
/// it cannot do that from a comma-separated list of names. So the pass hands over the indices and
/// the report renders the line — one producer for the id either way, because both go through
/// <c>LvsDiagnostics.NetShort</c>.
/// </remarks>
/// <param name="LayoutNet">The copper they share, as an index into the layout netlist.</param>
/// <param name="SchematicA">One schematic net, the lower index.</param>
/// <param name="SchematicB">The other.</param>
public readonly record struct LvsShort(int LayoutNet, int SchematicA, int SchematicB);

/// <summary>One schematic net on several pieces of copper — R-lvs8-5a's islands.</summary>
/// <param name="SchematicNet">Index into the schematic netlist.</param>
/// <param name="LayoutNets">The islands, ascending — indices into the layout netlist.</param>
public sealed record LvsOpen(int SchematicNet, IReadOnlyList<int> LayoutNets);

/// <summary>
/// What the comparison concluded — <b>the correspondence as well as the failures</b> (R-lvs7-5c),
/// because brief 12's cross-probing needs it and "what DID match" is a reasonable question.
/// </summary>
/// <param name="Devices">Every device pairing, in schematic order.</param>
/// <param name="Nets">Every net pairing, in schematic order.</param>
/// <param name="UnmatchedSchematicDevices">Indices into the schematic netlist.</param>
/// <param name="UnmatchedLayoutDevices">Indices into the layout netlist.</param>
/// <param name="Findings">The typed divergences, ordered. <b>Brief 8 turns these into a
/// report</b> — markers, short paths, island structure and waivers are all its.</param>
/// <param name="Anchors">How many name-based pairings survived. Zero is
/// <c>lvs.match.structural-only</c>.</param>
/// <param name="Iterations">Refinement iterations in the final pass.</param>
/// <param name="RefinementWork">
/// Neighbour visits and sort operations, summed over every pass — the counter the performance gate
/// asserts on (overview §1i: gate on counters, never on wall-clock).
/// </param>
public sealed record LvsComparison(
    IReadOnlyList<LvsPair> Devices,
    IReadOnlyList<LvsPair> Nets,
    IReadOnlyList<int> UnmatchedSchematicDevices,
    IReadOnlyList<int> UnmatchedLayoutDevices,
    IReadOnlyList<Diagnostic> Findings,
    int Anchors,
    int Iterations,
    long RefinementWork)
{
    /// <summary>Nothing above <see cref="DiagnosticSeverity.Info"/> was found.</summary>
    public bool IsClean => !Findings.Any(f => f.Severity > DiagnosticSeverity.Info);

    /// <summary>
    /// Every pair of schematic nets the artwork made one — <b>the structure behind the
    /// <c>lvs.net.short</c> lines</b>, so brief 8 can walk the copper from one to the other.
    /// </summary>
    public IReadOnlyList<LvsShort> Shorts { get; init; } = [];

    /// <summary>Every schematic net the artwork left in pieces, with its islands.</summary>
    public IReadOnlyList<LvsOpen> Opens { get; init; } = [];
}

/// <summary>Two netlists in, a correspondence and the places it fails out.</summary>
public static class LvsCompare
{
    /// <summary>
    /// Compares two <b>already reduced</b> netlists (brief 6 runs first, on both sides, through
    /// one function).
    /// </summary>
    /// <param name="schematic">The drawing's netlist.</param>
    /// <param name="layout">The artwork's netlist.</param>
    /// <param name="control">Progress and cancellation (R-lvs7-6c). A cancelled comparison throws
    /// <see cref="OperationCanceledException"/> and returns nothing.</param>
    /// <remarks>
    /// <b>The two parameters are named so a finding can say which side it means, and for nothing
    /// else.</b> Neither <see cref="LvsNetlist"/> says which document built it (R-lvs3-1a) and
    /// nothing below branches on the side: swap the arguments and the same pairs come back
    /// transposed, which is the property that keeps the two from being treated differently.
    /// </remarks>
    public static LvsComparison Compare(
        LvsNetlist schematic, LvsNetlist layout, RunControl? control = null)
    {
        ArgumentNullException.ThrowIfNull(schematic);
        ArgumentNullException.ThrowIfNull(layout);
        control?.ThrowIfCancellationRequested();

        var findings = new List<Diagnostic>();
        var reportedS = new HashSet<int>();
        var reportedL = new HashSet<int>();

        var anchors   = DeviceAnchors(schematic, layout, findings, reportedS, reportedL);
        var netAnchor = NetAnchors(schematic, layout);

        var pass = Evaluate(schematic, layout, anchors, netAnchor, reportedS, reportedL,
                            detectContradictions: true, control);

        long work = pass.Work;

        // R-lvs7-2c: dropped, and the refinement re-run ONCE without them. The second pass does not
        // look for contradictions of its own — a would-be contradiction there is reported as the
        // ordinary wrong-net findings it is made of, so the recursion has a floor.
        if (pass.Contradicted.Count > 0)
        {
            foreach (var (s, l) in pass.Contradicted)
                findings.Add(LvsDiagnostics.AnchorContradicted(
                    schematic.Devices[s].Path, layout.Devices[l].Path));

            var survivors = anchors.Where(a => !pass.Contradicted.Contains(a)).ToList();
            pass = Evaluate(schematic, layout, survivors, netAnchor, reportedS, reportedL,
                            detectContradictions: false, control);
            work += pass.Work;
            anchors = survivors;
        }

        findings.AddRange(pass.Findings);

        if (anchors.Count == 0 && pass.DevicePairs.Count > 0)
            findings.Add(LvsDiagnostics.MatchStructuralOnly(pass.DevicePairs.Count));

        return new LvsComparison(
            pass.DevicePairs, pass.NetPairs,
            pass.UnmatchedSchematic, pass.UnmatchedLayout,
            Ordered(findings), anchors.Count, pass.Iterations, work)
        {
            Shorts = pass.Shorts,
            Opens = pass.Opens,
        };
    }

    /// <summary>
    /// R-lvs8-1b's ordering, brought forward so the answer is already deterministic: severity,
    /// then id, then the rendered sentence. Two runs over unchanged documents produce identical
    /// lists and a test can assert on order.
    ///
    /// <para><b>Internal because brief 10's property pass re-orders the merged list.</b> Its
    /// findings arrive after this method has already run — they are about values, and values may
    /// not be compared until the correspondence exists — so the one place that knows this order has
    /// to be reachable from there rather than copied.</para>
    /// </summary>
    internal static List<Diagnostic> Ordered(IEnumerable<Diagnostic> findings) =>
        [.. findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ThenBy(f => f.Render(), StringComparer.Ordinal)];

    // ══ Anchors (R-lvs7-2) ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The name-based pairings both documents agree on, <b>every one of which is 1:1 or is not an
    /// anchor at all</b>.
    /// </summary>
    /// <remarks>
    /// Tier 0 is <c>AnchorId</c> against <see cref="LvsDevice.Path"/> — the layout's
    /// <c>SchematicId</c> — and tier 1 is a designator present on both sides. An id or a designator
    /// claimed by two devices is ambiguous and is silently no anchor: brief 3 already reports the
    /// duplicate (<c>lvs.device.duplicate-designator</c>) before anything is matched, and saying it
    /// again here would be the same fault twice.
    ///
    /// <para>R-lvs7-2d's dangling <c>SchematicId</c> needs nothing of its own: an id naming no
    /// device on the other side finds no candidate and is not an anchor, and brief 3 has already
    /// reported it.</para>
    /// </remarks>
    private static List<(int S, int L)> DeviceAnchors(
        LvsNetlist schematic, LvsNetlist layout,
        List<Diagnostic> findings, HashSet<int> reportedS, HashSet<int> reportedL)
    {
        var byPathS = Unique(schematic.Devices.Select((d, i) => (d.Path, i)));
        var byPathL = Unique(layout.Devices.Select((d, i) => (d.Path, i)));

        // schematic index -> the layout indices claiming it, and the reverse. A set on both sides,
        // because ambiguity is what disqualifies a claim and a first-wins map would hide it.
        var claims = new Dictionary<int, HashSet<int>>();
        var claimed = new Dictionary<int, HashSet<int>>();

        void Claim(int s, int l)
        {
            (claims.TryGetValue(s, out var a) ? a : claims[s] = []).Add(l);
            (claimed.TryGetValue(l, out var b) ? b : claimed[l] = []).Add(s);
        }

        // Tier 0, read in BOTH directions so nothing here depends on which side stated it.
        for (int l = 0; l < layout.Devices.Count; l++)
            if (layout.Devices[l].AnchorId is { Length: > 0 } id && byPathS.TryGetValue(id, out int s))
                Claim(s, l);
        for (int s = 0; s < schematic.Devices.Count; s++)
            if (schematic.Devices[s].AnchorId is { Length: > 0 } id && byPathL.TryGetValue(id, out int l))
                Claim(s, l);

        // Tier 1, for what tier 0 left unspoken for.
        var byDesS = Unique(schematic.Devices
            .Select((d, i) => (d.Designator, i))
            .Where(e => e.Designator.Length > 0 && !claims.ContainsKey(e.i)));
        var byDesL = Unique(layout.Devices
            .Select((d, i) => (d.Designator, i))
            .Where(e => e.Designator.Length > 0 && !claimed.ContainsKey(e.i)));

        foreach (var (designator, s) in byDesS.OrderBy(e => e.Key, StringComparer.Ordinal)
                                              .Select(e => (e.Key, e.Value)))
            if (byDesL.TryGetValue(designator, out int l)) Claim(s, l);

        var anchors = new List<(int S, int L)>();
        foreach (int s in claims.Keys.Order())
        {
            if (claims[s].Count != 1) continue;
            int l = claims[s].Single();
            if (claimed[l].Count != 1) continue;

            // R-lvs4-5e's veto, at the one point a pairing is actually proposed. Reported as ONE
            // line naming both, and BOTH devices are then excluded from the structural matching so
            // the same fault does not also arrive as two unmatched lines (R-lvs7-5d).
            var ds = schematic.Devices[s];
            var dl = layout.Devices[l];
            if (!ds.Type.CouldBe(dl.Type))
            {
                findings.Add(LvsDiagnostics.TypeMismatch(
                    ds.Path, dl.Path, Describe(ds.Type), Describe(dl.Type)));
                reportedS.Add(s);
                reportedL.Add(l);
                continue;
            }

            anchors.Add((s, l));
        }

        anchors.Sort((a, b) => a.S != b.S ? a.S.CompareTo(b.S) : a.L.CompareTo(b.L));
        return anchors;
    }

    /// <summary>
    /// The net pairings both documents agree on: a cell BOUNDARY port by its position, and a label
    /// present and unambiguous on both sides (R-lvs7-2a).
    /// </summary>
    /// <remarks>
    /// <b>Position before label, and a conflict between them is no anchor.</b> Port order is stated
    /// explicitly by both documents — a <c>Pin Num=</c> on one side and the cell's own pin order on
    /// the other — where a label is a name someone typed. Where the two disagree, one of them is
    /// describing the fault, and an anchor whose truth is the question cannot also be the evidence.
    /// </remarks>
    private static List<(int S, int L)> NetAnchors(LvsNetlist schematic, LvsNetlist layout)
    {
        var takenS = new HashSet<int>();
        var takenL = new HashSet<int>();
        var pairs = new List<(int S, int L)>();

        int ports = Math.Min(schematic.BoundaryNets.Count, layout.BoundaryNets.Count);
        for (int k = 0; k < ports; k++)
        {
            int s = schematic.BoundaryNets[k], l = layout.BoundaryNets[k];
            if (takenS.Add(s) && takenL.Add(l)) pairs.Add((s, l));
        }

        var byLabelS = Unique(schematic.Nets
            .Where(n => n.Label is { Length: > 0 }).Select(n => (n.Label!, n.Index)));
        var byLabelL = Unique(layout.Nets
            .Where(n => n.Label is { Length: > 0 }).Select(n => (n.Label!, n.Index)));

        foreach (var (label, s) in byLabelS.OrderBy(e => e.Key, StringComparer.Ordinal)
                                           .Select(e => (e.Key, e.Value)))
        {
            if (!byLabelL.TryGetValue(label, out int l)) continue;
            if (takenS.Contains(s) || takenL.Contains(l)) continue;
            takenS.Add(s);
            takenL.Add(l);
            pairs.Add((s, l));
        }

        pairs.Sort((a, b) => a.S != b.S ? a.S.CompareTo(b.S) : a.L.CompareTo(b.L));
        return pairs;
    }

    /// <summary>Name to index, dropping every name more than one object claims.</summary>
    private static Dictionary<string, int> Unique(IEnumerable<(string Name, int Index)> entries)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, index) in entries)
            if (!seen.TryAdd(name, index)) duplicate.Add(name);
        foreach (string d in duplicate) seen.Remove(d);
        return seen;
    }

    /// <summary>What to CALL a type in a type-mismatch line: the cell folders where both sides
    /// resolved one, the coarse kind where the kinds are what was compared.</summary>
    private static string Describe(DeviceType type)
        => type.Kind is DeviceKind.Cell or DeviceKind.Unknown && type.Name is { Length: > 0 } name
            ? name
            : type.Kind.ToString();

    // ══ One pass (R-lvs7-3 … R-lvs7-5) ═══════════════════════════════════════════════════════

    private sealed record Pass(
        List<LvsPair> DevicePairs,
        List<LvsPair> NetPairs,
        List<int> UnmatchedSchematic,
        List<int> UnmatchedLayout,
        List<Diagnostic> Findings,
        List<(int S, int L)> Contradicted,
        int Iterations,
        long Work)
    {
        /// <summary>The shorts, by index — brief 8 renders them with the route.</summary>
        public List<LvsShort> Shorts { get; init; } = [];

        /// <summary>The opens, by index.</summary>
        public List<LvsOpen> Opens { get; init; } = [];
    }

    private static Pass Evaluate(
        LvsNetlist schematic, LvsNetlist layout,
        List<(int S, int L)> anchors, List<(int S, int L)> netAnchors,
        HashSet<int> reportedS, HashSet<int> reportedL,
        bool detectContradictions, RunControl? control)
    {
        var findings = new List<Diagnostic>();
        control?.ThrowIfCancellationRequested();

        var refined = Refine(schematic, layout, anchors, netAnchors, control);
        if (refined.Capped) findings.Add(LvsDiagnostics.RefinementCapped(refined.Iterations));

        // ── Match (R-lvs7-4) ──────────────────────────────────────────────────────────────────
        var pairs = new List<LvsPair>();
        var pairedS = new int[schematic.Devices.Count];
        var pairedL = new int[layout.Devices.Count];
        Array.Fill(pairedS, -1);
        Array.Fill(pairedL, -1);

        foreach (var (s, l) in anchors)
        {
            pairedS[s] = l;
            pairedL[l] = s;
            pairs.Add(Pair(schematic, layout, s, l, LvsPairedBy.Anchor));
        }

        MatchClasses(schematic, layout, refined, anchors, reportedS, reportedL,
                     pairs, pairedS, pairedL, findings);

        // ── Which net is which (R-lvs7-5a's "the path from the nearest anchor") ───────────────
        var (votes, principal, owner, netPairs) =
            NetCorrespondence(schematic, layout, netAnchors, pairs);

        // ── Terminals, shorts, opens ──────────────────────────────────────────────────────────
        var contradicted = new List<(int S, int L)>();
        var anchorSet = anchors.ToHashSet();

        foreach (var pair in pairs.Where(p => p.Kind == LvsObjectKind.Device))
        {
            control?.ThrowIfCancellationRequested();
            var ds = schematic.Devices[pair.Schematic];
            var dl = layout.Devices[pair.Layout];
            var byPort = dl.Terminals.ToDictionary(t => t.Port);

            var wrong = new List<Diagnostic>();
            int common = 0;
            foreach (var ts in ds.Terminals.OrderBy(t => t.Port))
            {
                if (!byPort.TryGetValue(ts.Port, out var tl)) continue;
                common++;

                int expected = principal.GetValueOrDefault(ts.NetIndex, -1);
                if (tl.NetIndex == expected) continue;

                // An island of the terminal's OWN net is an open, not a mis-wiring — reporting it
                // here too would turn one missing via into a finding per pin.
                if (owner.GetValueOrDefault(tl.NetIndex, -1) == ts.NetIndex) continue;

                wrong.Add(LvsDiagnostics.TerminalWrongNet(
                    ds.Path, ts.Port, ts.Name,
                    NetName(schematic, ts.NetIndex), NetName(layout, tl.NetIndex)));
            }

            // R-lvs7-2b. Every terminal refuted is a pairing that was simply wrong; some of them is
            // a mis-wiring. Two terminals at least, because a one-terminal device has no majority.
            bool refuted = detectContradictions && common >= 2 && wrong.Count == common
                           && anchorSet.Contains((pair.Schematic, pair.Layout));
            if (refuted) contradicted.Add((pair.Schematic, pair.Layout));
            else findings.AddRange(wrong);
        }

        var shorts = new List<LvsShort>();
        var opens = new List<LvsOpen>();
        if (contradicted.Count == 0)
        {
            shorts.AddRange(Shorts(principal));
            opens.AddRange(Opens(votes, principal, owner));

            foreach (var pair in shorts)
                findings.Add(LvsDiagnostics.NetShort(
                    $"{NetName(schematic, pair.SchematicA)}, {NetName(schematic, pair.SchematicB)}",
                    2, NetName(layout, pair.LayoutNet)));
            foreach (var open in opens)
                findings.Add(LvsDiagnostics.NetOpen(
                    NetName(schematic, open.SchematicNet), open.LayoutNets.Count,
                    string.Join("; ", open.LayoutNets.Select(l =>
                        $"{NetName(layout, l)} ({layout.Nets[l].Pins.Count} pin(s))"))));

            findings.AddRange(Unmatched(schematic, layout, refined, reportedS, reportedL,
                                        pairedS, pairedL));
        }

        return new Pass(
            pairs.Where(p => p.Kind == LvsObjectKind.Device).OrderBy(p => p.Schematic).ToList(),
            netPairs,
            [.. Enumerable.Range(0, schematic.Devices.Count)
                 .Where(i => pairedS[i] < 0 && !reportedS.Contains(i))],
            [.. Enumerable.Range(0, layout.Devices.Count)
                 .Where(i => pairedL[i] < 0 && !reportedL.Contains(i))],
            findings, contradicted, refined.Iterations, refined.Work)
        {
            Shorts = shorts,
            Opens = opens,
        };
    }

    private static LvsPair Pair(LvsNetlist s, LvsNetlist l, int si, int li, LvsPairedBy by)
        => new(LvsObjectKind.Device, si, li, s.Devices[si].Path, l.Devices[li].Path, by);

    private static string NetName(LvsNetlist netlist, int index)
        => index >= 0 && index < netlist.Nets.Count && netlist.Nets[index].Label is { Length: > 0 } l
            ? l
            : $"net {index}";

    // ── Classes to pairs (R-lvs7-4) ──────────────────────────────────────────────────────────

    /// <summary>
    /// Pairs the devices no name spoke for, class by class, with the type veto applied inside each
    /// class and an arbitrary pairing said out loud.
    /// </summary>
    private static void MatchClasses(
        LvsNetlist schematic, LvsNetlist layout, Refinement refined,
        List<(int S, int L)> anchors, HashSet<int> reportedS, HashSet<int> reportedL,
        List<LvsPair> pairs, int[] pairedS, int[] pairedL, List<Diagnostic> findings)
    {
        var anchoredS = anchors.Select(a => a.S).ToHashSet();
        var anchoredL = anchors.Select(a => a.L).ToHashSet();

        var classes = new SortedDictionary<int, (List<int> S, List<int> L)>();
        for (int i = 0; i < schematic.Devices.Count; i++)
        {
            if (anchoredS.Contains(i) || reportedS.Contains(i)) continue;
            Bucket(classes, refined.DeviceColour[i]).S.Add(i);
        }
        for (int i = 0; i < layout.Devices.Count; i++)
        {
            if (anchoredL.Contains(i) || reportedL.Contains(i)) continue;
            Bucket(classes, refined.DeviceColour[schematic.Devices.Count + i]).L.Add(i);
        }

        foreach (var (_, group) in classes)
        {
            group.S.Sort((a, b) => Provenance(schematic, a, b));
            group.L.Sort((a, b) => Provenance(layout, a, b));

            var assignment = TypeCompatibleMatching(schematic, layout, group.S, group.L);

            // Arbitrary whenever BOTH sides have more than one member — including when the counts
            // differ, where it is arbitrary in the strongest sense (three of the four, and nothing
            // says which three). R-lvs7-4d's own finding, the unmatched line below, carries both
            // counts and is what makes the unequal case a divergence rather than an automorphism;
            // this line is the separate fact that the three that DID pair could have been any
            // three, which a reader chasing "C7" needs just as much.
            bool arbitrary = group.S.Count > 1 && group.L.Count > 1;

            var names = new List<string>();
            foreach (var (s, l) in assignment)
            {
                pairedS[s] = l;
                pairedL[l] = s;
                pairs.Add(Pair(schematic, layout, s, l,
                               arbitrary ? LvsPairedBy.Symmetry : LvsPairedBy.Structure));
                names.Add($"{schematic.Devices[s].Path} = {layout.Devices[l].Path}");
            }

            // R-lvs7-4c. Said out loud: the pairing inside the group is arbitrary and a later
            // finding naming one member may mean any other.
            if (arbitrary && assignment.Count > 0)
                findings.Add(LvsDiagnostics.MatchBySymmetry(assignment.Count, string.Join(", ", names)));
        }
    }

    private static (List<int> S, List<int> L) Bucket(
        SortedDictionary<int, (List<int> S, List<int> L)> classes, int colour)
    {
        if (!classes.TryGetValue(colour, out var group)) classes[colour] = group = ([], []);
        return group;
    }

    /// <summary>
    /// R-lvs7-4b's deterministic tie-break: instance path, then designator, then the document's
    /// own ordinal. Same input, same pairing, every run and every platform.
    /// </summary>
    private static int Provenance(LvsNetlist netlist, int a, int b)
    {
        var da = netlist.Devices[a];
        var db = netlist.Devices[b];
        int c = string.CompareOrdinal(da.Provenance.InstancePath, db.Provenance.InstancePath);
        if (c != 0) return c;
        c = string.CompareOrdinal(da.Designator, db.Designator);
        return c != 0 ? c : a.CompareTo(b);
    }

    /// <summary>
    /// The largest type-compatible pairing within one colour class, by augmenting paths over a
    /// deterministic candidate order.
    /// </summary>
    /// <remarks>
    /// <b>Greedy is not enough and the class is small enough that it does not have to be.</b>
    /// Members of one class are interchangeable by construction, so a greedy pass that consumed the
    /// only capacitor on a resistor would leave a real pairing unfound and report two spurious
    /// unmatched devices. The common case — every member of the class the same type — short-circuits
    /// to a zip, so the augmenting search only runs on a class that is genuinely mixed.
    /// </remarks>
    private static List<(int S, int L)> TypeCompatibleMatching(
        LvsNetlist schematic, LvsNetlist layout, List<int> sides, List<int> others)
    {
        int n = Math.Min(sides.Count, others.Count);
        if (n == 0) return [];

        bool uniform = sides.All(s => others.All(l => schematic.Devices[s].Type.CouldBe(layout.Devices[l].Type)));
        if (uniform)
            return [.. Enumerable.Range(0, n).Select(k => (sides[k], others[k]))];

        int[] taken = new int[others.Count];
        Array.Fill(taken, -1);

        bool Augment(int si, bool[] visited)
        {
            for (int j = 0; j < others.Count; j++)
            {
                if (visited[j]) continue;
                if (!schematic.Devices[sides[si]].Type.CouldBe(layout.Devices[others[j]].Type)) continue;
                visited[j] = true;
                if (taken[j] < 0 || Augment(taken[j], visited)) { taken[j] = si; return true; }
            }
            return false;
        }

        for (int i = 0; i < sides.Count; i++) Augment(i, new bool[others.Count]);

        var result = new List<(int S, int L)>();
        for (int j = 0; j < others.Count; j++)
            if (taken[j] >= 0) result.Add((sides[taken[j]], others[j]));
        result.Sort((a, b) => a.S.CompareTo(b.S));
        return result;
    }

    // ── Nets (R-lvs7-5a) ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Which layout net each schematic net IS, derived from the devices that matched.
    /// </summary>
    /// <remarks>
    /// <b>Derived rather than refined, and that is what stops a short becoming forty findings.</b>
    /// A matched device's terminal is a vote that its two nets correspond; the net with the most
    /// votes is the correspondence and every other one it touches is a divergence the shorts and
    /// opens below explain. A net anchor outweighs every terminal, because port order and a shared
    /// name are statements and a terminal is only evidence.
    /// </remarks>
    private static (Dictionary<(int S, int L), int> Votes, Dictionary<int, int> Principal,
                    Dictionary<int, int> Owner, List<LvsPair> Pairs)
        NetCorrespondence(
            LvsNetlist schematic, LvsNetlist layout,
            List<(int S, int L)> netAnchors, List<LvsPair> pairs)
    {
        var votes = new Dictionary<(int S, int L), int>();
        int anchorWeight = 1;

        foreach (var pair in pairs.Where(p => p.Kind == LvsObjectKind.Device))
        {
            var byPort = layout.Devices[pair.Layout].Terminals.ToDictionary(t => t.Port);
            foreach (var ts in schematic.Devices[pair.Schematic].Terminals)
            {
                if (!byPort.TryGetValue(ts.Port, out var tl)) continue;
                var key = (ts.NetIndex, tl.NetIndex);
                votes[key] = votes.GetValueOrDefault(key) + 1;
                anchorWeight++;
            }
        }

        var anchored = new HashSet<(int, int)>();
        foreach (var (s, l) in netAnchors)
        {
            votes[(s, l)] = votes.GetValueOrDefault((s, l)) + anchorWeight;
            anchored.Add((s, l));
        }

        var principal = Best(votes, key => key.S, key => key.L);
        var owner     = Best(votes, key => key.L, key => key.S);

        var netPairs = new List<LvsPair>();
        foreach (int s in principal.Keys.Order())
        {
            int l = principal[s];
            if (owner.GetValueOrDefault(l, -1) != s) continue;
            netPairs.Add(new LvsPair(
                LvsObjectKind.Net, s, l, NetName(schematic, s), NetName(layout, l),
                anchored.Contains((s, l)) ? LvsPairedBy.Anchor : LvsPairedBy.Structure));
        }

        return (votes, principal, owner, netPairs);
    }

    /// <summary>The highest-voted counterpart of each key, ties broken by the lowest index.</summary>
    private static Dictionary<int, int> Best(
        Dictionary<(int S, int L), int> votes,
        Func<(int S, int L), int> from, Func<(int S, int L), int> to)
    {
        var best = new Dictionary<int, (int To, int Votes)>();
        foreach (var key in votes.Keys.OrderBy(from).ThenBy(to))
        {
            int f = from(key), t = to(key), v = votes[key];
            if (!best.TryGetValue(f, out var prior) || v > prior.Votes) best[f] = (t, v);
        }
        return best.ToDictionary(e => e.Key, e => e.Value.To);
    }

    /// <summary>
    /// Several schematic nets on one piece of copper, <b>as PAIRS</b> (R-lvs8-4d).
    /// </summary>
    /// <remarks>
    /// One finding per pair, because "IN and GND are joined" is a sentence with a route and "IN,
    /// GND and VCC are joined" is three of them — and the report has to name the metal that does
    /// the joining, which differs pair by pair. The cap and the summary that keep a forty-net pour
    /// from emitting 780 lines are the report's, not the pass's: what is produced here is the
    /// complete truth and what is SHOWN is bounded.
    /// </remarks>
    private static IEnumerable<LvsShort> Shorts(Dictionary<int, int> principal)
    {
        foreach (var group in principal.GroupBy(e => e.Value)
                                       .Where(g => g.Count() > 1)
                                       .OrderBy(g => g.Key))
        {
            var nets = group.Select(e => e.Key).Order().ToList();
            for (int i = 0; i < nets.Count; i++)
                for (int j = i + 1; j < nets.Count; j++)
                    yield return new LvsShort(group.Key, nets[i], nets[j]);
        }
    }

    /// <summary>
    /// One schematic net on several pieces of copper.
    /// </summary>
    /// <remarks>
    /// <b>A piece counts as an island of <c>s</c> only where nothing else has a better claim on
    /// it</b> — it is either the piece <c>s</c> corresponds to, or a piece whose strongest claim
    /// IS <c>s</c>. Counting every piece <c>s</c> touches would report an open beside every
    /// mis-wired terminal, since a terminal on the wrong trace also puts its net on two pieces,
    /// and one fault would arrive as two findings.
    ///
    /// <para>The two clauses are both needed and the combined board is what shows it: a missing
    /// via AND a short together leave the main pour claimed by the net it was shorted to, so the
    /// owner clause alone sees one island and says nothing.</para>
    /// </remarks>
    private static IEnumerable<LvsOpen> Opens(
        Dictionary<(int S, int L), int> votes,
        Dictionary<int, int> principal, Dictionary<int, int> owner)
    {
        var islandsOf = new SortedDictionary<int, List<int>>();
        foreach (var ((s, l), count) in votes.OrderBy(v => v.Key.S).ThenBy(v => v.Key.L))
        {
            if (count <= 0) continue;
            if (owner.GetValueOrDefault(l, -1) != s && principal.GetValueOrDefault(s, -1) != l) continue;
            (islandsOf.TryGetValue(s, out var list) ? list : islandsOf[s] = []).Add(l);
        }

        foreach (var (s, islands) in islandsOf.Where(e => e.Value.Count > 1))
            yield return new LvsOpen(s, islands);
    }

    /// <summary>
    /// The devices no pairing reached, each reporting <b>both class sizes</b> (R-lvs7-4d): three
    /// parallel caps against four is a different fault from one missing part, and a finding that
    /// gave only the missing part could not tell them apart.
    /// </summary>
    private static IEnumerable<Diagnostic> Unmatched(
        LvsNetlist schematic, LvsNetlist layout, Refinement refined,
        HashSet<int> reportedS, HashSet<int> reportedL, int[] pairedS, int[] pairedL)
    {
        int offset = schematic.Devices.Count;
        var sizes = new Dictionary<int, (int S, int L)>();
        for (int i = 0; i < schematic.Devices.Count; i++)
        {
            var prior = sizes.GetValueOrDefault(refined.DeviceColour[i]);
            sizes[refined.DeviceColour[i]] = (prior.S + 1, prior.L);
        }
        for (int i = 0; i < layout.Devices.Count; i++)
        {
            var prior = sizes.GetValueOrDefault(refined.DeviceColour[offset + i]);
            sizes[refined.DeviceColour[offset + i]] = (prior.S, prior.L + 1);
        }

        for (int i = 0; i < schematic.Devices.Count; i++)
        {
            if (pairedS[i] >= 0 || reportedS.Contains(i)) continue;
            var (s, l) = sizes[refined.DeviceColour[i]];
            yield return LvsDiagnostics.UnmatchedSchematic(schematic.Devices[i].Path, s, l);
        }
        for (int i = 0; i < layout.Devices.Count; i++)
        {
            if (pairedL[i] >= 0 || reportedL.Contains(i)) continue;
            var (s, l) = sizes[refined.DeviceColour[offset + i]];
            yield return LvsDiagnostics.UnmatchedLayout(layout.Devices[i].Path, s, l);
        }
    }

    // ══ Colour refinement (R-lvs7-1, R-lvs7-3) ═══════════════════════════════════════════════

    private sealed record Refinement(
        int[] DeviceColour, int[] NetColour, int Iterations, bool Capped, long Work);

    /// <summary>
    /// The Weisfeiler–Leman fixed point over the bipartite device/net graph of BOTH netlists at
    /// once — one colour space, so "same colour" is comparable across the two documents.
    /// </summary>
    /// <remarks>
    /// <b>It is not a subgraph-isomorphism solver and must never grow into one</b> (R-lvs7-1b).
    /// Where refinement cannot separate two classes they are genuinely symmetric, which §4 handles;
    /// adding search would turn a bounded cost into an unbounded one on exactly the designs that
    /// are largest.
    /// </remarks>
    private static Refinement Refine(
        LvsNetlist schematic, LvsNetlist layout,
        List<(int S, int L)> anchors, List<(int S, int L)> netAnchors, RunControl? control)
    {
        int nS = schematic.Devices.Count, nL = layout.Devices.Count;
        int mS = schematic.Nets.Count,    mL = layout.Nets.Count;
        int devices = nS + nL, nets = mS + mL;
        long work = 0;

        // ── Edges, once: (device, port, net) in the combined index space ──────────────────────
        var owners = new List<int>();
        var ports  = new List<int>();
        var ends   = new List<int>();

        void AddSide(IReadOnlyList<LvsDevice> list, int deviceOffset, int netOffset)
        {
            for (int d = 0; d < list.Count; d++)
                foreach (var t in list[d].Terminals)
                {
                    owners.Add(deviceOffset + d);
                    ports.Add(t.Port);
                    ends.Add(netOffset + t.NetIndex);
                }
        }

        AddSide(schematic.Devices, 0, 0);
        AddSide(layout.Devices, nS, mS);

        int[] owner = [.. owners], port = [.. ports], end = [.. ends];
        int maxPort = port.Length == 0 ? 0 : port.Max();

        // ── Initial colours (R-lvs7-3a/b, minus the two components that cannot be in one) ─────
        var anchorOfDevice = new int[devices];
        Array.Fill(anchorOfDevice, -1);
        for (int a = 0; a < anchors.Count; a++)
        {
            anchorOfDevice[anchors[a].S] = a;
            anchorOfDevice[nS + anchors[a].L] = a;
        }

        var anchorOfNet = new int[nets];
        Array.Fill(anchorOfNet, -1);
        for (int a = 0; a < netAnchors.Count; a++)
        {
            anchorOfNet[netAnchors[a].S] = a;
            anchorOfNet[mS + netAnchors[a].L] = a;
        }

        int[] deviceDegree = new int[devices];
        foreach (int o in owner) deviceDegree[o]++;
        int[] netDegree = new int[nets];
        foreach (int e in end) netDegree[e]++;

        int[] deviceColour = Dense([.. Enumerable.Range(0, devices)
            .Select(i => (long)deviceDegree[i] * 1_000_003 + anchorOfDevice[i] + 1)], ref work);
        int[] netColour = Dense([.. Enumerable.Range(0, nets)
            .Select(i => (long)netDegree[i] * 1_000_003 + anchorOfNet[i] + 1)], ref work);

        // ── Refine (R-lvs7-3c/d/e) ────────────────────────────────────────────────────────────
        int cap = devices + nets + 2;
        int iterations = 0;
        int classes = Distinct(deviceColour) + Distinct(netColour);
        bool capped = false;

        while (true)
        {
            control?.ThrowIfCancellationRequested();

            // The terminal POSITION is not optional (R-lvs7-3c): a resistor is symmetric but a FET
            // is not, and dropping the index matches a drain to a source.
            int[] deviceOrder = GroupSort(owner, port, end, netColour, devices, maxPort, ref work);
            int[] netOrder    = GroupSort(end, port, owner, deviceColour, nets, maxPort, ref work);

            int[] nextDevice = Recolour(deviceColour, deviceOrder, owner, port, end, netColour, ref work);
            int[] nextNet    = Recolour(netColour, netOrder, end, port, owner, deviceColour, ref work);

            iterations++;
            int now = Distinct(nextDevice) + Distinct(nextNet);
            deviceColour = nextDevice;
            netColour = nextNet;

            if (now == classes) break;
            classes = now;

            if (iterations >= cap) { capped = true; break; }
        }

        return new Refinement(deviceColour, netColour, iterations, capped, work);
    }

    /// <summary>
    /// Edge indices grouped by <paramref name="owner"/> and, within a group, sorted by
    /// (<paramref name="port"/>, the far end's colour) — <b>three stable counting sorts over dense
    /// ids</b> (R-lvs7-3e), never a comparison sort over strings.
    /// </summary>
    private static int[] GroupSort(
        int[] owner, int[] port, int[] end, int[] endColour, int owners, int maxPort, ref long work)
    {
        int[] index = [.. Enumerable.Range(0, owner.Length)];
        index = CountingSort(index, i => endColour[end[i]], endColour.Length == 0 ? 1 : endColour.Max() + 1, ref work);
        index = CountingSort(index, i => port[i], maxPort + 1, ref work);
        return CountingSort(index, i => owner[i], owners, ref work);
    }

    /// <summary>A stable counting sort over a bounded key.</summary>
    private static int[] CountingSort(int[] index, Func<int, int> key, int buckets, ref long work)
    {
        if (buckets <= 0) buckets = 1;
        int[] count = new int[buckets + 1];
        foreach (int i in index) count[key(i) + 1]++;
        for (int b = 0; b < buckets; b++) count[b + 1] += count[b];

        int[] sorted = new int[index.Length];
        foreach (int i in index) sorted[count[key(i)]++] = i;

        work += index.Length + buckets;
        return sorted;
    }

    /// <summary>
    /// One recolouring: each object's new colour is its old one plus the sorted sequence of
    /// (terminal position, neighbour colour) it attaches through.
    /// </summary>
    private static int[] Recolour(
        int[] colour, int[] order, int[] owner, int[] port, int[] end, int[] endColour, ref long work)
    {
        var signature = new List<long>[colour.Length];
        for (int i = 0; i < colour.Length; i++) signature[i] = [colour[i]];

        foreach (int e in order)
        {
            signature[owner[e]].Add(port[e]);
            signature[owner[e]].Add(endColour[end[e]]);
            work += 2;
        }

        // The grouping is a sort over INT SEQUENCES — no string is built and nothing is hashed, so
        // two objects share a colour because their signatures are equal rather than because a hash
        // agreed. It is the one n log n step in the pass and the reason RefinementWork counts it.
        int[] ids = [.. Enumerable.Range(0, colour.Length)];
        Array.Sort(ids, (a, b) => CompareSignature(signature[a], signature[b]));

        int[] next = new int[colour.Length];
        int dense = 0;
        for (int k = 0; k < ids.Length; k++)
        {
            if (k > 0 && CompareSignature(signature[ids[k - 1]], signature[ids[k]]) != 0) dense++;
            next[ids[k]] = dense;
            work += signature[ids[k]].Count;
        }
        return next;
    }

    private static int CompareSignature(List<long> a, List<long> b)
    {
        int n = Math.Min(a.Count, b.Count);
        for (int i = 0; i < n; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Count.CompareTo(b.Count);
    }

    /// <summary>Arbitrary keys to dense ids, assigned in sorted order so they are reproducible.</summary>
    private static int[] Dense(long[] keys, ref long work)
    {
        int[] ids = [.. Enumerable.Range(0, keys.Length)];
        Array.Sort(ids, (a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : a.CompareTo(b));

        int[] dense = new int[keys.Length];
        int next = 0;
        for (int k = 0; k < ids.Length; k++)
        {
            if (k > 0 && keys[ids[k - 1]] != keys[ids[k]]) next++;
            dense[ids[k]] = next;
        }
        work += keys.Length;
        return dense;
    }

    private static int Distinct(int[] colours) => colours.Length == 0 ? 0 : colours.Distinct().Count();
}
