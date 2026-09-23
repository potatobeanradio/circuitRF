// Which SECTION of the rail everything is on — measured off the artwork, or stated
// (docs/sonnet-briefs/brief-railrf-25-series-element.md R-rail25-2;
//  docs/sonnet-briefs/brief-railrf-35-series-parts-from-the-window.md R-rail35-3).
//
// ── THE PARTITION IS A MEASUREMENT, NOT A FORM FIELD ───────────────────────────────────────────
//
// R-rail25-2a: "Cut the rail at the series element's two pads and walk from each: the rail falls
// into two galvanically separate regions, and every part, load and observation port lands in one of
// them by where its own pads are."
//
// THERE IS NO CUTTING CODE HERE, AND THERE MUST NOT BE. On imported artwork the copper already
// stops at every pad — §2.8's own sentence, and the whole reason PdnSeriesElement exists — so the
// rail arrives from Regions.Walk ALREADY partitioned into galvanically separate islands, and
// each series element is the thing that bridges two of them. The measurement is therefore: which
// island does each terminal land in. Subtracting a synthetic gap at each pad would be a second
// connectivity model beside DrcConnectivity's, and a second one drifts.
//
// That is also why R-rail25-2b falls out rather than being checked for: if both terminals land in
// ONE island there is copper around the element, it is not in series with anything, and the run
// refuses by name. A bridged ferrite is a real and common layout error and finding it is exactly
// what railRF is for.
//
// ── K ELEMENTS, K+1 SECTIONS, AND THEY MUST FORM A TREE (brief 35) ─────────────────────────────
//
// Brief 25 had one element and therefore two sides. A field report's rail runs through a bead and
// then a switch with the load behind both, so the islands are now NODES and the elements EDGES, and
// R-rail35-3b's three refusals are exactly the three ways that graph fails to be a tree rooted at
// the source: an element joining two islands already joined (a CYCLE — the DC split between two
// series paths is a copper question the lumped model cannot answer), an element with both ends on
// one island (BRIDGED, per element, brief 25's own sentence), and an island no chain of elements
// reaches from the source (fed by NOTHING). A tree is what makes every element's current the sum of
// the loads beyond it, which is what makes every DCR one row of the breakdown.
//
// Sections are numbered by a breadth-first walk from the source's island, visiting elements in ROW
// order — so section 0 is always the source's and a rail with one element numbers its two sections
// exactly as brief 25's Upstream and Downstream, which is what keeps every existing document's
// netlist, and therefore its DataSet, bit for bit what it was.
//
// ── AND IT IS THE SAME WALK, NOT A SIMILAR ONE ─────────────────────────────────────────────────
//
// The islands are PdnRailRegionSet.Power, produced by the extraction the DC answer and the copper
// map are already built from. So the section a part is shaded in on the board IS the section its
// branch is stamped on in the sweep, and the two cannot come to disagree.

using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// One series element as an edge of the section tree: the section it is fed from and the section it
/// feeds (brief 35, R-rail35-3a).
/// </summary>
/// <param name="Element">The document's own row.</param>
/// <param name="Upstream">The section nearer the source — the one its current comes FROM.</param>
/// <param name="Downstream">The section beyond it.</param>
public sealed record RailSeriesEdge(RailPart Element, int Upstream, int Downstream)
{
    /// <summary>The element, as every report names it.</summary>
    public string Refdes => Element.Refdes;
}

/// <summary>
/// Which section of a rail each part, load and source sits on, and which series element joins which
/// two sections.
/// </summary>
/// <param name="Refusal">Why the rail could not be partitioned, or null. Non-null means NOTHING was
/// assigned — the contract <see cref="PdnSweepResult"/> and <see cref="RailDcRunResult"/> already
/// state.</param>
/// <param name="Elements">One edge per series element, in the rail's own row order. Empty on a rail
/// that has none — in which case there is one section and nothing reads it.</param>
/// <param name="FromArtwork">True where the sections were MEASURED off the board (R-rail25-2a),
/// false where they were stated on the rows (R-rail25-2d, R-rail35-3c). <b>Reported</b>, because the
/// two are the same answer arrived at in very different ways.</param>
/// <param name="Parts">Refdes → section, for every part row but the series elements.</param>
/// <param name="Loads">One section per load, in the rail's own load order.</param>
/// <param name="Sources">One section per source, in the rail's own source order.</param>
/// <param name="Notes">What railRF established that the document did not state.</param>
public sealed record RailSeriesPartition(
    string? Refusal,
    IReadOnlyList<RailSeriesEdge> Elements,
    bool FromArtwork,
    IReadOnlyDictionary<string, int> Parts,
    IReadOnlyList<int> Loads,
    IReadOnlyList<int> Sources,
    IReadOnlyList<string> Notes)
{
    /// <summary>The source's own section. Every tree is rooted here.</summary>
    public const int Root = 0;

    /// <summary>How many sections the rail falls into — one more than its series elements, since a
    /// tree of K edges has K+1 nodes.</summary>
    public int SectionCount => Elements.Count + 1;

    /// <summary>True where this rail has a series element and therefore more than one node.
    /// <b>The condition R-rail25-4b's note is conditional on.</b></summary>
    public bool HasSeriesElement => Elements.Count > 0;

    /// <summary>Which section one part row is on. The LAST section for anything nothing placed —
    /// the far end of the chain, which is where decoupling goes.</summary>
    public int PartSection(string? refdes) =>
        refdes is { Length: > 0 } r && Parts.TryGetValue(r, out int s) ? s : Last;

    /// <summary>Which section one load is on, by its index in the rail's own list.</summary>
    public int LoadSection(int index) =>
        index >= 0 && index < Loads.Count ? Loads[index] : Last;

    /// <summary>Which section one source is on, by its index in the rail's own list.</summary>
    public int SourceSection(int index) =>
        index >= 0 && index < Sources.Count ? Sources[index] : Root;

    /// <summary>The section beyond the last element in row order — brief 25's "downstream".</summary>
    private int Last => Elements.Count == 0 ? Root : Elements[^1].Downstream;

    /// <summary>
    /// What a report calls one section: the source's, or the one beyond a named element.
    /// </summary>
    /// <remarks>
    /// <b>Named by its ELEMENT, never by its index</b>, for <see cref="RailPart.Behind"/>'s reason: an
    /// index is a number that silently moves the day somebody inserts a third element, and "beyond
    /// FB1" is what the designer already calls it. A one-element rail keeps brief 25's two words.
    /// </remarks>
    public string SectionName(int section)
    {
        if (Elements.Count == 1)
            return section == Root ? "upstream" : "downstream";

        if (section == Root) return "the source's section";
        var feeding = Elements.FirstOrDefault(e => e.Downstream == section);
        return feeding is null ? $"section {section}" : $"beyond {feeding.Refdes}";
    }

    /// <summary>Which power island each section is, by island index — <b>what the copper map shades
    /// from</b> (R-rail25-4c, R-rail35-3d). Empty where the partition was typed rather than
    /// measured.</summary>
    public IReadOnlyDictionary<int, int> Islands { get; init; } = new Dictionary<int, int>();

    /// <summary>The sentence a report prints about the partition, or null where there is no series
    /// element to partition at.</summary>
    public string? Describe()
    {
        if (Elements.Count == 0) return null;

        string how = FromArtwork
            ? "Measured off the artwork — cutting at each element's pads separates the rail's " +
              "copper into galvanically independent regions, and every row landed in one of them by " +
              "where its own pads are."
            : "Stated on the rows: this rail has no artwork, so nothing could measure it.";

        if (Elements.Count == 1)
            return $"Rail is cut at series element {Elements[0].Refdes}: " +
                   $"{Sources.Count(s => s == Root)} source(s) and " +
                   $"{Parts.Count(p => p.Value == Root)} part(s) upstream of it, " +
                   $"{Parts.Count(p => p.Value != Root)} part(s) and " +
                   $"{Loads.Count(l => l != Root)} port(s) downstream. " + how;

        var sections = Enumerable.Range(0, SectionCount).Select(k =>
            $"{SectionName(k)}: {Parts.Count(p => p.Value == k)} part(s), " +
            $"{Loads.Count(l => l == k)} port(s)" +
            (Sources.Count(s => s == k) is > 0 and var n ? $", {n} source(s)" : ""));

        return $"Rail is cut at {Elements.Count} series elements " +
               $"({string.Join(", ", Elements.Select(e => $"{e.Refdes}: {SectionName(e.Upstream)} → {SectionName(e.Downstream)}"))}) " +
               $"into {SectionCount} sections — {string.Join("; ", sections)}. " + how;
    }

    private static RailSeriesPartition Refused(string why) =>
        new(why, [], FromArtwork: true, new Dictionary<string, int>(), [], [], []);

    /// <summary>
    /// The partition of a rail with no series element: one section, and nothing reads it.
    /// </summary>
    public static RailSeriesPartition None(RailSpec rail)
    {
        ArgumentNullException.ThrowIfNull(rail);
        return new RailSeriesPartition(
            null, [], FromArtwork: false,
            rail.Parts.ToDictionary(p => p.Refdes, _ => Root, StringComparer.OrdinalIgnoreCase),
            [.. rail.Loads.Select(_ => Root)],
            [.. rail.Sources.Select(_ => Root)],
            []);
    }

    /// <summary>
    /// <b>R-rail25-2d and R-rail35-3c — the typed route.</b> §6 makes P1 artwork-OPTIONAL and a rail
    /// may have none; there the partition is STATED. The series rows form a CHAIN in row order, and
    /// every other row names the element it sits behind or states which end it is at.
    /// </summary>
    /// <remarks>
    /// <b>A chain only</b>, which is the brief's own scope: with no artwork there is nothing to say
    /// that FB2 hangs off FB1's section rather than off the source's, and a typed branch would need
    /// a field whose only job is to encode a graph a user cannot see. A branch is what the artwork
    /// route measures.
    /// </remarks>
    public static RailSeriesPartition Typed(RailSpec rail)
    {
        ArgumentNullException.ThrowIfNull(rail);

        var elements = rail.SeriesElements;
        if (elements.Count == 0) return None(rail);

        var edges = new List<RailSeriesEdge>(elements.Count);
        for (int k = 0; k < elements.Count; k++) edges.Add(new RailSeriesEdge(elements[k], k, k + 1));

        var beyond = edges.ToDictionary(e => e.Refdes, e => e.Downstream, StringComparer.OrdinalIgnoreCase);
        int last = edges[^1].Downstream;

        int Stated(RailSection side, string? behind) =>
            behind is { Length: > 0 } b && beyond.TryGetValue(b, out int s) ? s
            : side == RailSection.Upstream ? Root
            : last;

        var notes = new List<string>
        {
            elements.Count == 1
                ? $"This rail has no artwork, so the side of {elements[0].Refdes} each row is on is " +
                  "the side the row STATES (downstream by default, which is where decoupling goes) " +
                  "rather than a measurement off the board. Import the artwork to have railRF cut " +
                  "the rail at the element's own pads and work it out."
                : $"This rail has no artwork, so its {elements.Count} series elements are a CHAIN in " +
                  $"row order ({string.Join(" → ", elements.Select(e => e.Refdes))}), and each row is " +
                  "in the section it STATES — behind the element it names, or at the far end by " +
                  "default, which is where decoupling goes. Import the artwork to have railRF cut " +
                  "the rail at every element's pads and measure it, branches included.",
        };

        return new RailSeriesPartition(
            null, edges, FromArtwork: false,
            rail.Parts.Where(p => !p.IsSeries)
                      .ToDictionary(p => p.Refdes, p => Stated(p.Side, p.Behind), StringComparer.OrdinalIgnoreCase),
            [.. rail.Loads.Select(l => Stated(l.Side, l.Behind))],

            // A SOURCE IS IN THE ROOT SECTION BY DEFINITION and there is no field for it: the
            // series elements are what the source feeds the rest of the rail THROUGH, so a source
            // beyond one of them would not be a partition, it would be a second source.
            [.. rail.Sources.Select(_ => Root)],
            notes);
    }

    /// <summary>
    /// <b>R-rail25-2a and R-rail35-3a — the measured route.</b> Each row lands in a section by which
    /// of the rail's own galvanic islands its pads are in, and each element joins the two islands its
    /// terminals are in.
    /// </summary>
    /// <param name="rail">The rail. Its part, load and source rows are the subjects.</param>
    /// <param name="regions">The islands <see cref="Regions.Walk"/> already produced for this
    /// rail — <b>the same walk the DC answer and the copper map are built from</b>, never a second
    /// one.</param>
    /// <param name="pads">The board's pads, so a refdes resolves to coordinates.</param>
    public static RailSeriesPartition FromArtworkRegions(
        RailSpec rail, PdnRailRegionSet regions, IReadOnlyList<PlacedPin> pads)
    {
        ArgumentNullException.ThrowIfNull(rail);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(pads);

        var elements = rail.SeriesElements;
        if (elements.Count == 0) return None(rail);

        // ── every element's two islands ───────────────────────────────────────────────────────
        var ends = new List<(RailPart Element, int A, int B)>(elements.Count);
        foreach (var element in elements)
        {
            if (element.TerminalA is not { } termA || element.TerminalB is not { } termB)
                return Refused(
                    $"Series element {element.Refdes} on rail '{rail.Name}' names no terminals, so " +
                    "railRF cannot tell which two pieces of copper it bridges. Give both of its " +
                    "rail-side terminals — the refdes and pin of each pad. A refdes on its own is not " +
                    "enough: it resolves to EVERY pad of the part, which would tie the element's two " +
                    "ends into one node and model a short.");

            int? a = IslandOf(termA, regions, pads);
            int? b = IslandOf(termB, regions, pads);

            if (a is null)
                return Refused(
                    $"Series element {element.Refdes} on rail '{rail.Name}' has its first terminal at " +
                    $"{termA.Describe()}, which is not on this rail's copper. Move it onto the rail, or " +
                    "name the refdes and pin the pad is under.");

            if (b is null)
                return Refused(
                    $"Series element {element.Refdes} on rail '{rail.Name}' has its second terminal at " +
                    $"{termB.Describe()}, which is not on this rail's copper.");

            // ── R-rail25-2b, per element: a cut that does not separate ───────────────────────
            //
            // Both ends in one island means there is COPPER AROUND the element — it is not in
            // series with anything, and the board shorts it out. Modelling it as a series part
            // anyway would produce a plausible curve of a circuit the board is not.
            if (a == b)
                return Refused(
                    $"Series element {element.Refdes} on rail '{rail.Name}' is BRIDGED: its two " +
                    $"terminals, {termA.Describe()} and {termB.Describe()}, are on the same piece of " +
                    "copper, so cutting the rail at them does not separate it. There is copper around " +
                    "the element and the board shorts it out — it is not in series with anything. " +
                    "Either the artwork is wrong, or these are not the element's two rail-side pads.");

            ends.Add((element, a.Value, b.Value));
        }

        // ── R-rail35-3b: a CYCLE is refused by name ───────────────────────────────────────────
        //
        // Adding the elements one at a time, an element whose two islands are ALREADY joined by the
        // ones before it closes a loop. The path that already joins them is named with it, because
        // "FB2 forms a cycle" alone does not say with what.
        var adjacency = new Dictionary<int, List<(int To, RailPart Via)>>();
        List<(int To, RailPart Via)> Adj(int island) =>
            adjacency.TryGetValue(island, out var list) ? list : adjacency[island] = [];

        foreach (var (element, a, b) in ends)
        {
            if (PathBetween(adjacency, a, b) is { } path)
                return Refused(
                    $"Series elements on rail '{rail.Name}' form a LOOP: {element.Refdes} joins two " +
                    "pieces of copper that " +
                    string.Join(" and ", path.Select(p => p.Refdes)) +
                    " already join. Two series paths between the same sections make the DC current " +
                    "split between them a question about the copper, which a lumped model cannot " +
                    "answer — so railRF refuses rather than choosing a split. If one of them is not " +
                    "really in the rail's path, mark it a decoupling part or remove it from the rail.");

            Adj(a).Add((b, element));
            Adj(b).Add((a, element));
        }

        var notes = new List<string>();

        // ── the root: the source's island ─────────────────────────────────────────────────────
        //
        // WHICH SIDE IS UPSTREAM IS THE SOURCE'S ISLAND, because upstream means "between the source
        // and the element". With no source on any section there is nothing to measure it from, and
        // the first element's own first terminal is taken as the root and SAID so — a silently
        // chosen orientation would relabel a report's sections with no way to tell.
        int root = ends[0].A;
        bool located = false;

        for (int k = 0; k < rail.Sources.Count && !located; k++)
        {
            int? island = IslandOf(rail.Sources[k].Anchor, regions, pads);
            if (island is { } i && adjacency.ContainsKey(i)) { root = i; located = true; }
        }

        if (!located)
            notes.Add(
                elements.Count == 1
                    ? $"No source on rail '{rail.Name}' lands on either side of {elements[0].Refdes}, " +
                      $"so railRF took its first terminal ({elements[0].TerminalA!.Describe()}) as " +
                      "the UPSTREAM end. Upstream means between the source and the element; with no " +
                      "source to measure from, the naming is the document's order rather than the " +
                      "board's."
                    : $"No source on rail '{rail.Name}' lands on any section its series elements cut " +
                      $"it into, so railRF took {elements[0].Refdes}'s first terminal " +
                      $"({elements[0].TerminalA!.Describe()}) as the SOURCE'S end. With no source to " +
                      "measure from, which way each element faces is the document's order rather " +
                      "than the board's.");

        // ── breadth-first from the root: section numbers, and each element's orientation ─────
        //
        // Neighbours are visited in ROW order (the adjacency lists were filled in row order), which
        // is what makes the numbering deterministic and a one-element rail's two sections 0 and 1.
        var sectionOfIsland = new Dictionary<int, int> { [root] = Root };
        var oriented = new Dictionary<RailPart, (int Up, int Down)>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<int>([root]);

        while (queue.Count > 0)
        {
            int island = queue.Dequeue();
            foreach (var (to, via) in Adj(island))
            {
                if (sectionOfIsland.ContainsKey(to)) continue;
                sectionOfIsland[to] = sectionOfIsland.Count;
                oriented[via] = (sectionOfIsland[island], sectionOfIsland[to]);
                queue.Enqueue(to);
            }
        }

        // ── R-rail35-3b: a section with no path to a source is refused ────────────────────────
        var unreached = ends.Where(e => !oriented.ContainsKey(e.Element)).Select(e => e.Element.Refdes).ToList();
        if (unreached.Count > 0)
            return Refused(
                $"On rail '{rail.Name}', {string.Join(", ", unreached)} " +
                (unreached.Count == 1 ? "joins" : "join") + " copper that no chain of series elements " +
                "connects to the source's, so the section beyond " +
                (unreached.Count == 1 ? "it" : "them") + " is fed by NOTHING and has no voltage and " +
                "no impedance to report. Either the artwork leaves a gap nothing bridges, or a part in " +
                "the path is not marked series.");

        var edges = ends.Select(e => new RailSeriesEdge(e.Element, oriented[e.Element].Up, oriented[e.Element].Down))
                        .ToList();

        // ── every row, by its own pads ────────────────────────────────────────────────────────
        var beyond = edges.ToDictionary(e => e.Refdes, e => e.Downstream, StringComparer.OrdinalIgnoreCase);
        int last = edges[^1].Downstream;
        var elsewhere = new List<string>();

        int SectionOf(RailPortAnchor anchor, string what, RailSection side, string? behind)
        {
            if (IslandOf(anchor, regions, pads) is { } island && sectionOfIsland.TryGetValue(island, out int s))
                return s;

            elsewhere.Add(what);
            return behind is { Length: > 0 } b && beyond.TryGetValue(b, out int named) ? named
                 : side == RailSection.Upstream ? Root
                 : last;
        }

        var parts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rail.Parts)
        {
            if (part.IsSeries) continue;
            parts[part.Refdes] = SectionOf(
                new RailPortAnchor { Refdes = part.Refdes }, part.Refdes, part.Side, part.Behind);
        }

        var loads = new List<int>(rail.Loads.Count);
        foreach (var load in rail.Loads)
            loads.Add(SectionOf(load.Anchor, load.Anchor.Describe(), load.Side, load.Behind));

        var sources = new List<int>(rail.Sources.Count);
        foreach (var source in rail.Sources)
            sources.Add(SectionOf(source.Anchor, source.Anchor.Describe(), RailSection.Upstream, null));

        if (elsewhere.Count > 0)
            notes.Add(
                elements.Count == 1
                    ? $"{elsewhere.Count} row(s) on rail '{rail.Name}' are on neither side of " +
                      $"{elements[0].Refdes}: {string.Join(", ", elsewhere)}. Nothing places them on " +
                      "the rail's copper, or they sit on a third island the element does not bridge " +
                      "— so each was put on the side its own row states (downstream by default). " +
                      "Place them, or state the side."
                    : $"{elsewhere.Count} row(s) on rail '{rail.Name}' are in none of the sections its " +
                      $"series elements cut it into: {string.Join(", ", elsewhere)}. Nothing places " +
                      "them on the rail's copper, or they sit on an island no element bridges — so " +
                      "each was put where its own row states (behind the element it names, or at the " +
                      "far end by default). Place them, or state it.");

        return new RailSeriesPartition(null, edges, FromArtwork: true, parts, loads, sources, notes)
        {
            Islands = sectionOfIsland,
        };
    }

    /// <summary>
    /// The elements already joining <paramref name="from"/> to <paramref name="to"/>, or null where
    /// nothing does — a breadth-first search over the edges added so far.
    /// </summary>
    private static List<RailPart>? PathBetween(
        Dictionary<int, List<(int To, RailPart Via)>> adjacency, int from, int to)
    {
        if (!adjacency.ContainsKey(from) || !adjacency.ContainsKey(to)) return null;

        var cameFrom = new Dictionary<int, (int Island, RailPart Via)?> { [from] = null };
        var queue = new Queue<int>([from]);

        while (queue.Count > 0)
        {
            int island = queue.Dequeue();
            if (island == to)
            {
                var path = new List<RailPart>();
                for (var step = cameFrom[to]; step is { } s; step = cameFrom[s.Island]) path.Add(s.Via);
                path.Reverse();
                return path;
            }

            foreach (var (next, via) in adjacency[island])
                if (!cameFrom.ContainsKey(next))
                {
                    cameFrom[next] = (island, via);
                    queue.Enqueue(next);
                }
        }

        return null;
    }

    /// <summary>
    /// Which of the rail's own islands <paramref name="anchor"/> lands in, or null where none of its
    /// pads is on the rail's copper.
    /// </summary>
    /// <remarks>
    /// <b>The same containment test <see cref="Regions"/> seeds with</b>, holes honoured and
    /// clipped against a 2 DBU square rather than a winding count — a pad coordinate from a drill
    /// file lands ON a boundary as often as inside one, and a via's own centre is the centre of the
    /// HOLE it drilled.
    /// </remarks>
    private static int? IslandOf(
        RailPortAnchor anchor, PdnRailRegionSet regions, IReadOnlyList<PlacedPin> pads)
    {
        var points = PdnAttachments.Resolve(anchor, pads);
        if (points.Count == 0) return null;

        foreach (var region in regions.Power)
            foreach (var (_, paths) in region.Copper)
                foreach (var (x, y) in points)
                    if (Regions.Contains(paths, x, y)) return region.Index;

        return null;
    }
}
