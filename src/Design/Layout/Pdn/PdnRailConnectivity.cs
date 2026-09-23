// Whether a rail's source can reach each of its loads AT ALL — asked of the galvanic islands, before
// anything is classified or priced (brief-railrf-29 R-rail29-1, R-rail29-2).
//
// ── WHY THIS IS ITS OWN QUESTION, AND WHY IT COMES FIRST ───────────────────────────────────────
//
// The fast model's pour refusal asks "does the source reach the load through copper the closed form
// can price?". Before this file existed it was also the only place a DISCONNECTED rail was noticed,
// and it gave the wrong answer to it: a rail whose source and load sit on two separate islands
// cannot be joined by any classification of any region, yet the refusal named the largest spreading
// region on the whole rail and told the user to force it to 'trace'. On the reported board that
// region was the source's own connector pad, and the sentence arrived after ten minutes of pricing
// copper that no answer was ever going to come out of.
//
// Both halves are answered here. The islands are Regions.Walk's own — DrcConnectivity, joined
// through via geometry — so this is the region walk's connectivity and not a second one, and it is
// available the moment the walk returns. Classification and pricing are the expensive stages on a
// real board, and neither can change a galvanic fact.
//
// ── AND THE SECOND CONNECTIVITY QUESTION: THE RAIL ROUTED ON ITS OWN RETURN ────────────────────
//
// Both extractors set aside rail copper on the reference layer — a conductor cannot be its own
// return. On a two-sided board with the bottom layer named as the reference, that can be most of
// the rail: on the reported board 58 % of each island was bottom copper, and once the jumper was
// declared as the series part the source still reached the load only through copper that was about
// to be set aside. That too is galvanic and known the moment the walk returns, so it is asked here,
// over the rail's copper with the reference layer taken out — DrcConnectivity again, never a
// second walk.
//
// ── WHAT JOINS TWO ISLANDS ─────────────────────────────────────────────────────────────────────
//
// Only a series part (§2.8: "the copper stops at every pad, so the board is not electrically
// continuous until the user has said what bridges each gap"). Permissive in the way the pour
// refusal's own series loop is: every island either terminal lands on is joined, because being
// generous here can only make this refusal LESS likely, and the assembly refuses a terminal that
// lands on nothing with its own sentence.

// ── AND THE RETURN (brief-railrf-31) ────────────────────────────────────────────────────────────
//
// The rail was asked "is it one piece, or pieces a declared part joins?" and the return never was.
// On the reported board the source's return and the load's landed on two unjoined reference
// pieces, the netlist was two circuits, and a 30 mA load driving a floating network came back from
// the DC solve as −150 MV. The walk already knows which reference piece is which, so the question is
// asked here, at the same cost and at the same moment as the rail's.
//
// Nothing a declared part does can join two RETURN pieces: PdnAssembly stamps a series part between
// rail copper only. So unlike the rail's check, this one takes no part into account — a check more
// generous than the stamping would only pass the split on to PdnAssembly's backstop, which refuses
// it later and less precisely.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// A source or load anchored by a coordinate that stands on more than one galvanic net and does not
/// say which it means (R-rail34-2) — what a refusal names, and what the window offers one click per
/// candidate for.
/// </summary>
/// <param name="RailName">The rail.</param>
/// <param name="IsSource">A source row; false is a load row.</param>
/// <param name="Index">0-based, in that list.</param>
/// <param name="X">The coordinate, DBU.</param>
/// <param name="Y">DBU.</param>
/// <param name="Candidates">What it stands on, topmost first.</param>
public sealed record PdnAnchorAmbiguity(
    string RailName, bool IsSource, int Index, long X, long Y, IReadOnlyList<PdnAnchorCopper> Candidates);

/// <summary>The galvanic reachability of a rail's loads from its sources.</summary>
internal static class PdnRailConnectivity
{
    /// <summary>At most this many bridging parts are named — a list longer than that is no longer
    /// a pointer at the likely bridge.</summary>
    private const int MaxBridgesNamed = 4;

    /// <summary>One galvanically joined piece of the rail, as the membership test reads it.</summary>
    private sealed record Group(List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)> Copper);

    /// <summary>
    /// R-rail31-1 — the return net, resolved ONCE, and then the walk. Both extractors call this and
    /// nothing else, so the net the window's reference row shows is the net the run uses, and the
    /// board's galvanic partition is made once for both questions.
    /// </summary>
    /// <remarks>
    /// <b>R-rail34-2: an anchor that does not say which copper it means is refused before the
    /// walk</b>, and <paramref name="ambiguous"/> says which and what it could mean. A bare
    /// coordinate standing on two galvanic nets off the reference layer seeds both, which makes one
    /// rail of two supplies — a plausible answer and a wrong one. Null region set then; the walk is
    /// not made, because nothing it would find can be priced.
    /// </remarks>
    public static PdnRailRegionSet? Walk(
        PdnExtractionRequest request, IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        LayerKey referenceLayer, IReadOnlyList<(long X, long Y, LayerKey? Layer)> anchorSeeds,
        IReadOnlyList<(long X, long Y)> bareCoordinateSeeds, out IReadOnlyList<PdnAnchorAmbiguity> ambiguous)
    {
        var tech = request.Technology;
        var pieces = DrcConnectivity.Extract(layerRegions, tech);
        var returnNet = Regions.ResolveReturnNet(request.ReferenceNet, pieces, tech, request.NetPoints, referenceLayer);

        ambiguous = Ambiguous(request, pieces, referenceLayer, returnNet.Net);
        if (ambiguous.Count > 0) return null;

        return Regions.Walk(pieces, tech, request.NetPoints, request.Rail.NetName, referenceLayer,
                            returnNet.Net, anchorSeeds, bareCoordinateSeeds) with { ReturnNet = returnNet };
    }

    /// <summary>
    /// R-rail34-2 — every source and load anchored by a coordinate that states no layer and stands on
    /// more than one galvanic net off the reference layer, after the return is taken out.
    /// </summary>
    private static IReadOnlyList<PdnAnchorAmbiguity> Ambiguous(
        PdnExtractionRequest request, IReadOnlyList<DrcNetPiece> pieces, LayerKey referenceLayer,
        string? returnNet)
    {
        var rail = request.Rail;
        List<PdnAnchorAmbiguity>? found = null;
        (HashSet<int> Nets, bool Resolved)? ret = null;

        void Ask(RailPortAnchor anchor, bool isSource, int index)
        {
            if (anchor.Refdes is { Length: > 0 } || anchor.Layer is not null || anchor.Point is not { } xy) return;

            ret ??= Regions.ReturnNets(pieces, request.NetPoints, rail.NetName, referenceLayer, returnNet);
            var under = Regions.CopperUnder(pieces, request.Technology, request.NetPoints, xy.X, xy.Y,
                                            referenceLayer, ret.Value.Nets, ret.Value.Resolved);
            if (under.Count > 1)
                (found ??= []).Add(new PdnAnchorAmbiguity(rail.Name, isSource, index, xy.X, xy.Y, under));
        }

        for (int i = 0; i < rail.Sources.Count; i++) Ask(rail.Sources[i].Anchor, true, i);
        for (int i = 0; i < rail.Loads.Count; i++) Ask(rail.Loads[i].Anchor, false, i);
        return found ?? [];
    }

    /// <summary>R-rail34-2 — the refusal for <see cref="Ambiguous"/>'s anchors: each one, every
    /// candidate it stands on, and the <c>.crail</c> spelling that answers it.</summary>
    public static string AmbiguityRefusal(PdnExtractionRequest request, IReadOnlyList<PdnAnchorAmbiguity> ambiguous)
    {
        var fmt = request.LengthFormat;
        double dbuPerMm = request.DbuPerMicron * 1000.0;
        var sentences = new List<string>();

        foreach (var a in ambiguous)
        {
            string candidates = string.Join("; ", a.Candidates.Select(c =>
                $"{c.LayerName}, {(c.Net is { Length: > 0 } net ? $"net '{net}'" : "no net named")}, " +
                $"{c.AreaSquareDbu / (dbuPerMm * dbuPerMm):0.###} mm²"));
            var first = a.Candidates[0].Layer;

            sentences.Add(
                $"Rail '{a.RailName}''s {(a.IsSource ? "source" : "load")} {a.Index + 1} at " +
                $"{fmt.Point(a.X, a.Y)} stands on the copper of {a.Candidates.Count} separate nets — " +
                $"{candidates} — and a coordinate alone does not say which it means. Seeding them all " +
                "would price them as one rail, so this rail is not solved. Choose one: in the window, " +
                $"or in the .crail by giving that anchor its layer (\"Layer\": {first.Layer}, " +
                $"\"LayerDatatype\": {first.Datatype} for the first).");
        }

        return string.Join(" ", sentences);
    }

    /// <summary>
    /// R-rail31-3 and R-rail31-4 — the refusal for a return that is not one: a reference that cannot
    /// be told apart from the rail, or a source's return and a load's that land on reference copper
    /// no conductor joins. Null is every ordinary board.
    /// </summary>
    public static string? ReturnRefusal(
        PdnExtractionRequest request, PdnRailRegionSet regions, LayerKey referenceLayer)
    {
        // A FILLED or UNBOUNDED reference is one plate by construction: it cannot be split, and it
        // is not "every piece on the layer", so it cannot take the rail's own lands in either.
        if (request.Rail.ReferenceExtent != RailReferenceExtent.AsImported) return null;

        if (regions.MixedReturnRefusal is { } mixed) return $"Rail '{request.Rail.Name}': {mixed}";
        if (regions.Reference.Count < 2) return null;

        var fmt = request.LengthFormat;
        var islands = regions.Reference
            .Select(i => new Group(i.Copper
                .Where(c => c.Layer == referenceLayer)
                .Select(c => (c.Layer, c.Paths, DrcRegions.BoundsOf(c.Paths)))
                .ToList()))
            .ToList();

        // Every island one anchor's return touches is ONE node in the netlist — PdnAssembly ties an
        // anchor's pin field together — so an anchor straddling two islands joins them.
        var root = new int[islands.Count];
        for (int i = 0; i < root.Length; i++) root[i] = i;
        int Find(int x) { while (root[x] != x) { root[x] = root[root[x]]; x = root[x]; } return x; }

        var returns = new List<(string What, RailPortAnchor Anchor, int Island)>();
        void Add(string what, RailPortAnchor anchor)
        {
            var on = new HashSet<int>();
            var pads = PdnAttachments.Resolve(anchor, request.Pads);
            foreach (var (x, y) in pads)
                foreach (int k in GroupsAt(islands, x, y)) on.Add(k);

            // No reference copper under the pads — an antipad, a keepout. The assembly attaches that
            // return to the NEAREST reference copper and says so, and this asks about the same place.
            if (on.Count == 0 && pads.Count > 0 && Nearest(islands, pads[0].X, pads[0].Y) is { } near)
                on.Add(near);
            if (on.Count == 0) return;   // the assembly's own refusal, later and more specific

            int first = -1;
            foreach (int k in on)
            {
                if (first < 0) { first = k; continue; }
                int ra = Find(first), rb = Find(k);
                if (ra != rb) root[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
            returns.Add((what, anchor, first));
        }

        foreach (var s in request.Rail.Sources) Add("source", s.Anchor);
        foreach (var l in request.Rail.Loads) Add("load", l.Anchor);
        if (returns.Count < 2) return null;

        var (firstWhat, firstAnchor, firstIsland) = returns[0];
        foreach (var (what, anchor, island) in returns.Skip(1))
        {
            if (Find(island) == Find(firstIsland)) continue;

            return
                $"Rail '{request.Rail.Name}' has no single return: its {firstWhat} at " +
                $"{firstAnchor.Describe(fmt)} returns on one piece of the reference on " +
                $"{Regions.LayerLabel(request.Technology, referenceLayer)}, and its {what} at " +
                $"{anchor.Describe(fmt)} returns on another, and no copper joins the two. The " +
                $"reference there is {regions.Reference.Count} galvanically separate pieces. " +
                "Solving it would drive the load's current into a network with no path back to the " +
                "source — a number, and a meaningless one. Join the reference (a stitching via, a " +
                "link), name a reference layer that is one plane under both, or take the reference " +
                "as filled to the board outline, which is optimistic and says so.";
        }

        return null;
    }

    /// <summary>The group whose copper on its own layers comes nearest the point, or null.</summary>
    private static int? Nearest(IReadOnlyList<Group> groups, long x, long y)
    {
        int? best = null;
        double bestD = double.PositiveInfinity;

        for (int k = 0; k < groups.Count; k++)
            foreach (var (_, paths, _) in groups[k].Copper)
                foreach (var path in paths)
                    for (int i = 0; i < path.Count; i++)
                    {
                        var a = path[i];
                        var b = path[(i + 1) % path.Count];
                        double d = SegmentDistanceSquared(x, y, a.X, a.Y, b.X, b.Y);
                        if (d < bestD) { bestD = d; best = k; }
                    }

        return best;
    }

    private static double SegmentDistanceSquared(long px, long py, long ax, long ay, long bx, long by)
    {
        double dx = bx - ax, dy = by - ay;
        double len = dx * dx + dy * dy;
        double t = len > 0 ? Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len, 0, 1) : 0;
        double ex = ax + t * dx - px, ey = ay + t * dy - py;
        return ex * ex + ey * ey;
    }

    /// <summary>
    /// The walk's diagnostics for the run, with its "nothing bridges them" note restated where the
    /// rail declares series parts. The walk cannot see parts, and a run that stamps a declared
    /// jumper across two regions while its notes say nothing bridges them reads as if the
    /// declaration had been ignored. <see cref="Refusal"/> is what holds the claim: a declared part
    /// that bridges nothing the source needs is refused there.
    /// </summary>
    public static IEnumerable<string> DiagnosticsOf(PdnExtractionRequest request, PdnRailRegionSet regions)
    {
        string separate = Regions.SeparateRegionsNote(regions.Power.Count);
        var parts = request.SeriesElements.Select(p => p.Refdes).Distinct(StringComparer.Ordinal).ToList();
        foreach (string d in regions.Diagnostics)
            yield return parts.Count > 0 && d == separate
                ? $"The rail's copper is {regions.Power.Count} galvanically separate regions. On imported " +
                  $"artwork the copper stops at every pad, and the series part{(parts.Count == 1 ? "" : "s")} " +
                  $"this rail declares — {Join(parts)} — {(parts.Count == 1 ? "is" : "are")} what bridge" +
                  $"{(parts.Count == 1 ? "s" : "")} them at DC."
                : d;
    }

    /// <summary>
    /// The refusal for a rail whose source cannot reach some load through copper the extraction will
    /// read — separate islands no series part joins, or islands joined only through copper on the
    /// rail's own reference layer — or null. Null is every connected rail, and every rail whose
    /// anchors did not land on copper at all: those have refusals of their own, later and more
    /// specific.
    /// </summary>
    public static string? Refusal(
        PdnExtractionRequest request, PdnRailRegionSet regions, LayerKey referenceLayer)
    {
        var rail = request.Rail;
        if (regions.Power.Count == 0 || rail.Sources.Count == 0 || rail.Loads.Count == 0) return null;

        var fmt = request.LengthFormat;

        // ── the islands ────────────────────────────────────────────────────────────────────────
        //
        // Membership skips the reference layer: a pad is a coordinate, and the return plane under
        // it is not the rail.
        var islands = regions.Power
            .Select(i => new Group(i.Copper
                .Where(c => c.Layer != referenceLayer)
                .Select(c => (c.Layer, c.Paths, DrcRegions.BoundsOf(c.Paths)))
                .ToList()))
            .ToList();

        if (Unreached(request, islands) is { } apart)
        {
            var bridges = BridgingParts(request, islands, apart.SourceSide, apart.LoadSide);
            string bridge = bridges.Count > 0
                ? $"{Join(bridges)} {(bridges.Count == 1 ? "has" : "have")} pads on both the " +
                  $"source's copper and the load's, so {(bridges.Count == 1 ? "it is" : "one of them is")} " +
                  "the likely bridge. Add it to this rail as its series part — a part row in the " +
                  ".crail with Connection \"Series\" and its two pins as TerminalA and TerminalB, as " +
                  "the Power Rail example's FB1 is — or anchor the load on the source's copper."
                : "No placed part has pads on both the source's copper and the load's, so either the " +
                  "part that joins them is not on the artwork or the load is anchored on the wrong " +
                  "copper.";

            // Regions.Walk's own diagnostic wording, so the two sentences a user reads about the same
            // islands do not describe them twice in two ways (R-rail29-1).
            return
                $"Rail '{rail.Name}' cannot reach {apart.Load.Anchor.Describe(fmt)} from its source: " +
                $"the two are on separate copper. The rail's copper is {regions.Power.Count} " +
                "galvanically separate regions. On imported artwork the copper stops at every pad, so " +
                "this is ordinary and not an error — but nothing bridges them at DC until a series " +
                $"part says what does. {bridge} No override on the class tab changes this: there is no " +
                "copper between them to classify.";
        }

        // ── the same question without the rail's copper on its own reference layer ───────────
        double onReference = regions.Power
            .SelectMany(i => i.Copper)
            .Where(c => c.Layer == referenceLayer)
            .Sum(c => Math.Abs(Clipper.Area(c.Paths)));
        if (!(onReference > 0)) return null;

        var railOnly = new Dictionary<LayerKey, Paths64>();
        foreach (var (layer, paths) in regions.Power.SelectMany(i => i.Copper))
        {
            if (layer == referenceLayer) continue;
            if (!railOnly.TryGetValue(layer, out var acc)) railOnly[layer] = acc = [];
            acc.AddRange(paths);
        }

        var pieces = DrcConnectivity.Extract(
            railOnly.ToDictionary(kv => kv.Key, kv => DrcRegions.Union(kv.Value)), request.Technology);
        var joined = pieces
            .GroupBy(p => p.Net)
            .Select(g => new Group(g.Select(p => (p.Layer, p.Paths, p.Bounds)).ToList()))
            .ToList();

        if (Unreached(request, joined) is not { } throughReturn) return null;

        string layerName = request.Technology.Layers.FirstOrDefault(l => l.Key == referenceLayer)?.Name
            is { Length: > 0 } name
            ? $"'{name}' (layer {referenceLayer.Layer}/{referenceLayer.Datatype})"
            : $"layer {referenceLayer.Layer}/{referenceLayer.Datatype}";
        double dbuPerMm = request.DbuPerMicron * 1e3;
        double railArea = regions.Power.Sum(i => i.AreaSquareDbu);

        return
            $"Rail '{rail.Name}' reaches {throughReturn.Load.Anchor.Describe(fmt)} from its source only " +
            $"through its own copper on {layerName}, which is also the layer this rail names as its " +
            $"reference return. {onReference / (dbuPerMm * dbuPerMm):0.##} mm² of the rail — " +
            $"{(railArea > 0 ? onReference / railArea * 100 : 0):0}% of it — is on that layer, and a " +
            "conductor cannot be its own return, so both models set that copper aside and what is " +
            "left does not join the source to the load. Name as the reference a layer this rail does " +
            "not route on — on a board whose supply is routed on both outer layers, that is the plane " +
            "between them, once the stackup attaches it to its drawing layer.";
    }

    /// <summary>The first load no source reaches across <paramref name="groups"/>, joined by the
    /// request's series parts — and which groups are on each side — or null.</summary>
    private static (RailLoad Load, Func<int, bool> SourceSide, Func<int, bool> LoadSide)? Unreached(
        PdnExtractionRequest request, IReadOnlyList<Group> groups)
    {
        if (groups.Count < 2) return null;

        var root = new int[groups.Count];
        for (int i = 0; i < root.Length; i++) root[i] = i;

        int Find(int x) { while (root[x] != x) { root[x] = root[root[x]]; x = root[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) root[Math.Max(ra, rb)] = Math.Min(ra, rb); }

        HashSet<int> GroupsOf(RailPortAnchor anchor)
        {
            var on = new HashSet<int>();
            foreach (var (x, y) in PdnAttachments.Resolve(anchor, request.Pads))
                foreach (int k in GroupsAt(groups, x, y)) on.Add(k);
            return on;
        }

        foreach (var part in request.SeriesElements)
        {
            var ends = GroupsOf(part.A);
            ends.UnionWith(GroupsOf(part.B));
            int first = -1;
            foreach (int k in ends) { if (first < 0) first = k; else Union(first, k); }
        }

        var sourceRoots = new HashSet<int>();
        foreach (var s in request.Rail.Sources)
            foreach (int k in GroupsOf(s.Anchor)) sourceRoots.Add(Find(k));

        if (sourceRoots.Count == 0) return null;

        foreach (var load in request.Rail.Loads)
        {
            var on = GroupsOf(load.Anchor);
            if (on.Count == 0 || on.Any(k => sourceRoots.Contains(Find(k)))) continue;

            var loadRoots = on.Select(Find).ToHashSet();
            return (load, k => sourceRoots.Contains(Find(k)), k => loadRoots.Contains(Find(k)));
        }

        return null;
    }

    /// <summary>Every group with copper at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static IEnumerable<int> GroupsAt(IReadOnlyList<Group> groups, long x, long y)
    {
        for (int k = 0; k < groups.Count; k++)
            foreach (var (_, paths, bounds) in groups[k].Copper)
                if (bounds.Contains(x, y) && Regions.Contains(paths, x, y)) { yield return k; break; }
    }

    /// <summary>The designators with a pad on the source's side AND a pad on the load's, in
    /// designator order — what the user most likely forgot to declare as the series part.</summary>
    private static List<string> BridgingParts(
        PdnExtractionRequest request, IReadOnlyList<Group> groups,
        Func<int, bool> onSourceSide, Func<int, bool> onLoadSide)
    {
        var found = new List<string>();

        foreach (var part in request.Pads
                     .Where(p => p.Refdes is { Length: > 0 })
                     .GroupBy(p => p.Refdes!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool source = false, load = false;
            foreach (var pad in part)
                foreach (int k in GroupsAt(groups, pad.X, pad.Y))
                {
                    source |= onSourceSide(k);
                    load |= onLoadSide(k);
                }

            if (source && load) found.Add(part.Key);
        }

        return found;
    }

    private static string Join(List<string> names)
    {
        var shown = names.Take(MaxBridgesNamed).ToList();
        string list = shown.Count switch
        {
            1 => shown[0],
            2 => $"{shown[0]} and {shown[1]}",
            _ => string.Join(", ", shown.Take(shown.Count - 1)) + $" and {shown[^1]}",
        };
        return names.Count > shown.Count ? $"{list} (and {names.Count - shown.Count} more)" : list;
    }
}
