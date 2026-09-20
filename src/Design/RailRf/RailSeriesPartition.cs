// Which side of the series element everything is on — measured off the artwork, or stated
// (docs/sonnet-briefs/brief-railrf-25-series-element.md R-rail25-2).
//
// ── THE PARTITION IS A MEASUREMENT, NOT A FORM FIELD ───────────────────────────────────────────
//
// R-rail25-2a: "Cut the rail at the series element's two pads and walk from each: the rail falls
// into two galvanically separate regions, and every part, load and observation port lands in one of
// them by where its own pads are."
//
// THERE IS NO CUTTING CODE HERE, AND THERE MUST NOT BE. On imported artwork the copper already
// stops at every pad — §2.8's own sentence, and the whole reason PdnSeriesElement exists — so the
// rail arrives from PdnRailRegions.Walk ALREADY partitioned into galvanically separate islands, and
// the series element is the thing that bridges two of them. The measurement is therefore: which
// island does each terminal land in. Subtracting a synthetic gap at each pad would be a second
// connectivity model beside DrcConnectivity's, and a second one drifts.
//
// That is also why R-rail25-2b falls out rather than being checked for: if both terminals land in
// ONE island there is copper around the element, it is not in series with anything, and the run
// refuses by name. A bridged ferrite is a real and common layout error and finding it is exactly
// what railRF is for.
//
// ── AND IT IS THE SAME WALK, NOT A SIMILAR ONE ─────────────────────────────────────────────────
//
// The islands are PdnRailRegionSet.Power, produced by the extraction the DC answer and the copper
// map are already built from. So the section a part is shaded in on the board IS the section its
// branch is stamped on in the sweep, and the two cannot come to disagree.

using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// Which side of a rail's series element each part, load and source sits on.
/// </summary>
/// <param name="Refusal">Why the rail could not be partitioned, or null. Non-null means NOTHING was
/// assigned — the contract <see cref="PdnSweepResult"/> and <see cref="RailDcRunResult"/> already
/// state.</param>
/// <param name="Element">The series element, or null on a rail that has none — in which case every
/// section below is <see cref="RailSection.Downstream"/> and nothing reads them.</param>
/// <param name="FromArtwork">True where the sections were MEASURED off the board (R-rail25-2a),
/// false where they were stated on the rows (R-rail25-2d). <b>Reported</b>, because the two are the
/// same two-state answer arrived at in very different ways.</param>
/// <param name="Parts">Refdes → section, for every part row but the element itself.</param>
/// <param name="Loads">One section per load, in the rail's own load order.</param>
/// <param name="Sources">One section per source, in the rail's own source order.</param>
/// <param name="Notes">What railRF established that the document did not state.</param>
public sealed record RailSeriesPartition(
    string? Refusal,
    RailPart? Element,
    bool FromArtwork,
    IReadOnlyDictionary<string, RailSection> Parts,
    IReadOnlyList<RailSection> Loads,
    IReadOnlyList<RailSection> Sources,
    IReadOnlyList<string> Notes)
{
    /// <summary>True where this rail has a series element and therefore two nodes rather than one.
    /// <b>The condition R-rail25-4b's note is conditional on.</b></summary>
    public bool HasSeriesElement => Element is not null;

    /// <summary>Which section one part row is on. <see cref="RailSection.Downstream"/> for anything
    /// nothing placed, which is where decoupling goes.</summary>
    public RailSection PartSection(string? refdes) =>
        refdes is { Length: > 0 } r && Parts.TryGetValue(r, out var s) ? s : RailSection.Downstream;

    /// <summary>Which section one load is on, by its index in the rail's own list.</summary>
    public RailSection LoadSection(int index) =>
        index >= 0 && index < Loads.Count ? Loads[index] : RailSection.Downstream;

    /// <summary>Which section one source is on, by its index in the rail's own list.</summary>
    public RailSection SourceSection(int index) =>
        index >= 0 && index < Sources.Count ? Sources[index] : RailSection.Upstream;

    /// <summary>Which power island each section is, by island index — <b>what the copper map shades
    /// from</b> (R-rail25-4c). Empty where the partition was typed rather than measured.</summary>
    public IReadOnlyDictionary<int, RailSection> Islands { get; init; } =
        new Dictionary<int, RailSection>();

    /// <summary>The sentence a report prints about the partition, or null where there is no series
    /// element to partition at.</summary>
    public string? Describe() => Element is null
        ? null
        : $"Rail is cut at series element {Element.Refdes}: " +
          $"{Sources.Count(s => s == RailSection.Upstream)} source(s) and " +
          $"{Parts.Count(p => p.Value == RailSection.Upstream)} part(s) upstream of it, " +
          $"{Parts.Count(p => p.Value == RailSection.Downstream)} part(s) and " +
          $"{Loads.Count(l => l == RailSection.Downstream)} port(s) downstream. " +
          (FromArtwork
              ? "Measured off the artwork — cutting at the element's two pads separates the rail's " +
                "copper into two galvanically independent regions, and every row landed in one of " +
                "them by where its own pads are."
              : "Stated on the rows: this rail has no artwork, so nothing could measure it.");

    private static RailSeriesPartition Refused(string why, RailPart? element) =>
        new(why, element, FromArtwork: true,
            new Dictionary<string, RailSection>(), [], [], []);

    /// <summary>
    /// The partition of a rail with no series element: one section, and nothing reads it.
    /// </summary>
    public static RailSeriesPartition None(RailSpec rail)
    {
        ArgumentNullException.ThrowIfNull(rail);
        return new RailSeriesPartition(
            null, null, FromArtwork: false,
            rail.Parts.ToDictionary(p => p.Refdes, _ => RailSection.Downstream,
                                    StringComparer.OrdinalIgnoreCase),
            [.. rail.Loads.Select(_ => RailSection.Downstream)],
            [.. rail.Sources.Select(_ => RailSection.Downstream)],
            []);
    }

    /// <summary>
    /// <b>R-rail25-2d — the typed route.</b> §6 makes P1 artwork-OPTIONAL and a rail may have none;
    /// there the partition is STATED, on the same two-state field, filled in a different way.
    /// </summary>
    public static RailSeriesPartition Typed(RailSpec rail)
    {
        ArgumentNullException.ThrowIfNull(rail);

        if (rail.SeriesElement is not { } element) return None(rail);

        var notes = new List<string>
        {
            $"This rail has no artwork, so the side of {element.Refdes} each row is on is the side " +
            "the row STATES (downstream by default, which is where decoupling goes) rather than a " +
            "measurement off the board. Import the artwork to have railRF cut the rail at the " +
            "element's own pads and work it out.",
        };

        return new RailSeriesPartition(
            null, element, FromArtwork: false,
            rail.Parts.Where(p => !ReferenceEquals(p, element))
                      .ToDictionary(p => p.Refdes, p => p.Side, StringComparer.OrdinalIgnoreCase),
            [.. rail.Loads.Select(l => l.Side)],

            // A SOURCE IS UPSTREAM OF THE ELEMENT BY DEFINITION and there is no field for it: the
            // series element is what the source feeds the rest of the rail THROUGH, so a source on
            // the far side of it would not be a partition, it would be a second source.
            [.. rail.Sources.Select(_ => RailSection.Upstream)],
            notes);
    }

    /// <summary>
    /// <b>R-rail25-2a — the measured route.</b> Each row lands on a side by which of the rail's own
    /// galvanic islands its pads are in.
    /// </summary>
    /// <param name="rail">The rail. Its part, load and source rows are the subjects.</param>
    /// <param name="regions">The islands <see cref="PdnRailRegions.Walk"/> already produced for this
    /// rail — <b>the same walk the DC answer and the copper map are built from</b>, never a second
    /// one.</param>
    /// <param name="pads">The board's pads, so a refdes resolves to coordinates.</param>
    public static RailSeriesPartition FromArtworkRegions(
        RailSpec rail, PdnRailRegionSet regions, IReadOnlyList<PdnPad> pads)
    {
        ArgumentNullException.ThrowIfNull(rail);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(pads);

        if (rail.SeriesElement is not { } element) return None(rail);

        if (element.TerminalA is not { } termA || element.TerminalB is not { } termB)
            return Refused(
                $"Series element {element.Refdes} on rail '{rail.Name}' names no terminals, so " +
                "railRF cannot tell which two pieces of copper it bridges. Give both of its " +
                "rail-side terminals — the refdes and pin of each pad. A refdes on its own is not " +
                "enough: it resolves to EVERY pad of the part, which would tie the element's two " +
                "ends into one node and model a short.", element);

        int? a = IslandOf(termA, regions, pads);
        int? b = IslandOf(termB, regions, pads);

        if (a is null)
            return Refused(
                $"Series element {element.Refdes} on rail '{rail.Name}' has its first terminal at " +
                $"{termA.Describe()}, which is not on this rail's copper. Move it onto the rail, or " +
                "name the refdes and pin the pad is under.", element);

        if (b is null)
            return Refused(
                $"Series element {element.Refdes} on rail '{rail.Name}' has its second terminal at " +
                $"{termB.Describe()}, which is not on this rail's copper.", element);

        // ── R-rail25-2b: a cut that does not separate ─────────────────────────────────────────
        //
        // Both ends in one island means there is COPPER AROUND the element — it is not in series
        // with anything, and the board shorts it out. Modelling it as a series part anyway would
        // produce a plausible curve of a circuit the board is not. This is a real and common layout
        // error and it is exactly the kind of thing railRF exists to find.
        if (a == b)
            return Refused(
                $"Series element {element.Refdes} on rail '{rail.Name}' is BRIDGED: its two " +
                $"terminals, {termA.Describe()} and {termB.Describe()}, are on the same piece of " +
                "copper, so cutting the rail at them does not separate it. There is copper around " +
                "the element and the board shorts it out — it is not in series with anything. " +
                "Either the artwork is wrong, or these are not the element's two rail-side pads.",
                element);

        var notes = new List<string>();

        // WHICH SIDE IS UPSTREAM IS THE SOURCE'S ISLAND, because upstream means "between the
        // source and the element". With no source on either island there is nothing to measure it
        // from, and the element's own first terminal is taken as the upstream one and SAID so — a
        // silently chosen orientation would relabel a report's two halves with no way to tell.
        int upstreamIsland = a.Value;
        bool located = false;

        for (int k = 0; k < rail.Sources.Count; k++)
        {
            int? island = IslandOf(rail.Sources[k].Anchor, regions, pads);
            if (island is null) continue;
            if (island == a || island == b) { upstreamIsland = island.Value; located = true; break; }
        }

        if (!located)
            notes.Add(
                $"No source on rail '{rail.Name}' lands on either side of {element.Refdes}, so " +
                $"railRF took its first terminal ({termA.Describe()}) as the UPSTREAM end. " +
                "Upstream means between the source and the element; with no source to measure " +
                "from, the naming is the document's order rather than the board's.");

        int downstreamIsland = upstreamIsland == a.Value ? b.Value : a.Value;

        RailSection SectionOfIsland(int island) =>
            island == upstreamIsland ? RailSection.Upstream : RailSection.Downstream;

        var elsewhere = new List<string>();

        RailSection SectionOfAnchor(RailPortAnchor anchor, string what, RailSection fallback)
        {
            int? island = IslandOf(anchor, regions, pads);
            if (island == upstreamIsland || island == downstreamIsland)
                return SectionOfIsland(island!.Value);

            elsewhere.Add(what);
            return fallback;
        }

        var parts = new Dictionary<string, RailSection>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in rail.Parts)
        {
            if (ReferenceEquals(part, element)) continue;
            parts[part.Refdes] = SectionOfAnchor(
                new RailPortAnchor { Refdes = part.Refdes }, part.Refdes, part.Side);
        }

        var loads = new List<RailSection>(rail.Loads.Count);
        foreach (var load in rail.Loads)
            loads.Add(SectionOfAnchor(load.Anchor, load.Anchor.Describe(), load.Side));

        var sources = new List<RailSection>(rail.Sources.Count);
        foreach (var source in rail.Sources)
            sources.Add(SectionOfAnchor(source.Anchor, source.Anchor.Describe(), RailSection.Upstream));

        if (elsewhere.Count > 0)
            notes.Add(
                $"{elsewhere.Count} row(s) on rail '{rail.Name}' are on neither side of " +
                $"{element.Refdes}: {string.Join(", ", elsewhere)}. Nothing places them on the " +
                "rail's copper, or they sit on a third island the element does not bridge — so " +
                "each was put on the side its own row states (downstream by default). Place them, " +
                "or state the side.");

        var islands = new Dictionary<int, RailSection>
        {
            [upstreamIsland] = RailSection.Upstream,
            [downstreamIsland] = RailSection.Downstream,
        };

        return new RailSeriesPartition(null, element, FromArtwork: true, parts, loads, sources, notes)
        {
            Islands = islands,
        };
    }

    /// <summary>
    /// Which of the rail's own islands <paramref name="anchor"/> lands in, or null where none of its
    /// pads is on the rail's copper.
    /// </summary>
    /// <remarks>
    /// <b>The same containment test <see cref="PdnRailRegions"/> seeds with</b>, holes honoured and
    /// clipped against a 2 DBU square rather than a winding count — a pad coordinate from a drill
    /// file lands ON a boundary as often as inside one, and a via's own centre is the centre of the
    /// HOLE it drilled.
    /// </remarks>
    private static int? IslandOf(
        RailPortAnchor anchor, PdnRailRegionSet regions, IReadOnlyList<PdnPad> pads)
    {
        var points = PdnAttachments.Resolve(anchor, pads);
        if (points.Count == 0) return null;

        foreach (var region in regions.Power)
            foreach (var (_, paths) in region.Copper)
                foreach (var (x, y) in points)
                    if (PdnRailRegions.Contains(paths, x, y)) return region.Index;

        return null;
    }
}
