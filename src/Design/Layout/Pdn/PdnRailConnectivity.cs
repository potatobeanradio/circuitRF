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

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.Layout.Extraction;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>The galvanic reachability of a rail's loads from its sources.</summary>
internal static class PdnRailConnectivity
{
    /// <summary>At most this many bridging parts are named — a list longer than that is no longer
    /// a pointer at the likely bridge.</summary>
    private const int MaxBridgesNamed = 4;

    /// <summary>One galvanically joined piece of the rail, as the membership test reads it.</summary>
    private sealed record Group(List<(LayerKey Layer, Paths64 Paths, Bbox Bounds)> Copper);

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
