// What parts are ON this rail, read off the board — brief-railrf-26-parts-from-the-board.md.
//
// ── THE PARTS TABLE HAD NO PRODUCER ────────────────────────────────────────────────────────────
//
// `RebuildParts` lists `rail.Parts` and enriches them from the BOM, the placement file and the part
// library, and that much works. What was missing was every route by which a row got into
// `rail.Parts` in the first place: `new RailPart` appeared nowhere in `src/` except the reader,
// `RailPartOrigin.Bom` was never assigned by anything, and the window had no add-part gesture at
// all. So the only way a part had ever appeared in railRF was somebody writing the JSON, and a
// board with fifty-five placed footprints opened with column headings over nothing.
//
// ── IT LIVES HERE AND NOT IN THE VIEW MODEL, FOR RailArtwork's REASON ──────────────────────────
//
// `circuitrf rail` and the window must not come to different conclusions about one board. The
// window turns the answer into an OFFER; the verb PRINTS it (R-rail26-7). Framework-free,
// side-effect-free, posting nothing.
//
// IT RETURNS CANDIDATE ROWS AND NEVER WRITES THEM ONTO THE DOCUMENT. A function that edited the
// document would be a second writer beside the view model's undo stack.
//
// ── WHAT "A PART ON THIS RAIL" IS, STATED SO IT CANNOT QUIETLY BECOME "A PART" ─────────────────
//
// A candidate is a TWO-TERMINAL part with one pad on the rail's own copper and one pad on the
// reference. That is decoupling, which is what `RailPart`'s shunt row models, and nothing else may
// become one:
//
//     2 pads, rail + reference    decoupling                offered
//     2 pads, rail + rail         a series element          reported, never offered as shunt
//     2 pads, rail + elsewhere    not on the return path    reported
//     more than 2 pads on it      an IC, a connector, a regulator — a LOAD   reported
//     no pad on the rail          not this rail's business  silent
//
// THE FAILURE THIS EXISTS TO PREVENT is a series resistor, a ferrite or a load turning into a shunt
// capacitor with a library-defaulted ESR. The curve that comes out of that is smooth, plausible and
// wrong, and nothing on the window would say so — which is `RailPart`'s own standing rule: a
// defaulted number inside the sanity band is indistinguishable from a measured one.
//
// ── THE PREDICATE IS REGION MEMBERSHIP, AND THE MOUNTING LOOP IS NOT PART OF IT ────────────────
//
// A pad is ON THE RAIL when it lands in one of `PdnRailRegionSet.Power`'s islands, and ON THE
// REFERENCE when it lands in one of `Reference`'s. THAT IS A GALVANIC TEST AND NOT A SAME-LAYER
// ONE, which is the whole reason it must be the region walk: on a two-layer board both pads of a
// decoupling capacitor sit on the top, and the ground-side one reaches the plane through its own
// stitching via. A test that asked "is this pad on the reference LAYER" would find no decoupling on
// any such board at all.
//
// The regions are READ OFF THE LAST EXTRACTION and never walked again — `RailRfViewModel.
// SeriesRegions` already states that rule and the reason: a second connectivity model beside the
// one the DC answer is built from is two answers to one question.
//
// ── A PAD OVER A PLANE IS NOT A PAD ON IT, AND THE NET IS WHAT SETTLES IT ──────────────────────
//
// `Regions.ReferenceNetOn` records the same fact from the other side and calls the exact
// discriminator GALVANIC AMBIGUITY: a point belongs to the reference only where every piece of
// copper covering it is ONE galvanically-joined net. That test needs the whole piece set, and a
// `PdnRailRegionSet` does not carry one — `Islands(..., onlyLayer: referenceLayer)` keeps only the
// reference LAYER's copper, so a pad's own top-side land is not in it either way. Containment alone
// therefore reads EVERY pad on a board with an inner plane as "on the reference", and a pull-up
// resistor — one pad on the rail, one on a signal net, both over the plane — would be offered as a
// decoupling capacitor. That is precisely the failure the table above exists to prevent.
//
// So where a pad STATES a net, the net is a VETO: a pad naming something other than the reference
// net is not on the reference, and one naming something other than the rail's net is not on the
// rail. That is exact on every board whose pads carry nets — a board netlist, or artwork with a
// schematic behind it — which is every board `PlacedPins` resolves nets for.
//
// WHERE NOTHING NAMES A NET THE VETO CANNOT FIRE AND CONTAINMENT IS ALL THERE IS. On a Gerber set
// with no netlist and no schematic the copper stops at every pad (§2.8), so a ground land with a
// stitching via in it and a signal land without one are the same two polygons over the same plane
// and there is no information anywhere that separates them. Discovery over-offers there rather than
// under-offering, which is why R-rail26-4 makes this an OFFER a reader confirms and not an edit:
// every offered row carries no capacitance, is listed AS unresolved, and is counted on the face.
//
// `PdnMountingLoopExtractor` IS NOT THE PREDICATE, and that is worth being explicit about because
// it is the obvious shortcut. Its header reads "a part with no pad on the rail, no pad on the
// reference, or no via within reach of either" — the first two clauses are this predicate, the
// third is not. A capacitor whose return via is too far to price still IS decoupling on this rail;
// it is a row whose mounting inductance is unresolved, which `RailMountingBasis` and the parts
// table already have a spelling for. Using the loop's verdict as the filter would silently drop
// real parts from the bank, which is the one outcome this feature exists to prevent.
//
// ── NOTHING IS EVER DERIVED FROM THE FOOTPRINT ────────────────────────────────────────────────
//
// An 0402 land pattern is a case size; it is not a capacitance, an ESR or a dielectric class, and a
// row invented from one would be the defaulted number this whole tool marks and counts everywhere
// else. A row with no part number is listed AS unresolved, which is a state the parts table already
// renders.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>Why a part that touches this rail is not decoupling on it.</summary>
public enum RailDiscoverySkip
{
    /// <summary>Two pads, both on the rail's own copper — a series element, whose terminals are a
    /// user's statement about the topology (brief 25) and not something to infer.</summary>
    SpansTheRail,

    /// <summary>Two pads, one on the rail and one on neither the rail nor its reference.</summary>
    NotOnTheReturnPath,

    /// <summary>More than two pads, at least one on the rail — an IC, a connector, a regulator.
    /// <b>A LOAD</b>, and §2.3 step 4 keeps that a user act.</summary>
    MoreThanTwoPads,

    /// <summary>
    /// One pad on the rail and no other pad stated anywhere — a test point, a mounting hole, or a
    /// part whose remaining pads the board's own files do not carry.
    /// </summary>
    /// <remarks>
    /// <b>The count is what the BOARD states, not what the part has.</b> A netlist that lists only
    /// a rail's own pads makes a six-pin regulator read as one pad, which is why the sentence says
    /// "the board states no other pad" rather than calling it a one-terminal part. It is skipped
    /// either way — nothing bridges the rail to the reference through a single pad.
    /// </remarks>
    OneTerminalOnly,
}

/// <summary>One part that touches this rail and is not offered, with the reason on its face.</summary>
/// <param name="Refdes">The instance.</param>
/// <param name="Reason">Which row of the table it fell in.</param>
/// <param name="Sentence">What a reader is told, in full.</param>
public sealed record RailDiscoverySkipped(string Refdes, RailDiscoverySkip Reason, string Sentence);

/// <summary>
/// One candidate row, and the mounting loop the geometry would give it.
/// </summary>
/// <param name="Part">The row itself — <b>an ordinary <see cref="RailPart"/></b>, so once added
/// there is no second kind of part (R-rail26-6).</param>
/// <param name="Mounting">What <c>PdnMountingLoopExtractor</c> makes of it, or null where the
/// caller supplied no geometry. <b>Reported, never written onto <paramref name="Part"/></b>: a
/// computed value is a DEFAULT and a typed one wins, so writing it into the document would freeze
/// today's geometry into a number that a re-layout could no longer move, and
/// <see cref="RailMountingBasis"/> would then call it typed. The row gets its loop the way every
/// other row does — from the solve's own <c>ComputedMounting</c> map, once it is on the rail
/// (R-rail26-3a).</param>
public sealed record RailDiscoveryCandidate(RailPart Part, PdnMountingLoop? Mounting)
{
    /// <summary>The instance this is of.</summary>
    public string Refdes => Part.Refdes;
}

/// <summary>Whether there was anything to discover FROM.</summary>
public enum RailDiscoveryState
{
    /// <summary>
    /// The rail has no galvanic regions, so neither half of the predicate can be evaluated.
    /// </summary>
    /// <remarks>
    /// <b>Derived from whether the regions are THERE, not from whether the reference was
    /// confirmed</b> (R-rail26-2b). A rail whose run refused has no regions, and an offer computed
    /// from the confirmation alone would come up empty with nothing saying why.
    /// </remarks>
    NotExtracted,

    /// <summary>The walk was read and the board was classified. The lists may still be empty.</summary>
    Discovered,
}

/// <summary>What one board has to say about one rail's parts.</summary>
/// <param name="State">Whether there was anything to read.</param>
/// <param name="Offered">The candidate rows, by refdes, ordinal — <b>the same order the window and
/// the verb both print</b>, so the two cannot be compared and found to differ over nothing.</param>
/// <param name="Skipped">Every part that touches this rail and is not decoupling on it.</param>
/// <param name="Notes">What discovery established that the board did not state.</param>
public sealed record RailDiscoveryResult(
    RailDiscoveryState State,
    IReadOnlyList<RailDiscoveryCandidate> Offered,
    IReadOnlyList<RailDiscoverySkipped> Skipped,
    IReadOnlyList<string> Notes)
{
    /// <summary>Nothing to read, and why.</summary>
    public static RailDiscoveryResult None(RailDiscoveryState state) => new(state, [], [], []);

    /// <summary>
    /// Every part skipped as <see cref="RailDiscoverySkip.SpansTheRail"/>, as the SERIES row it
    /// would be — offered directly (brief 35, R-rail35-1a), with its two pads as its terminals.
    /// </summary>
    /// <remarks>
    /// <b>Offered, never added</b>, on R-rail26-4's terms: both pads on the rail's copper says the
    /// part is IN the rail, and whether the rail runs THROUGH it is still the designer's statement.
    /// What the board does settle is which two pads its ends are, and those are the one thing a
    /// series row cannot do without — a refdes alone resolves to every pad and models a short. The
    /// row carries no DCR and no model; those come from the row or the part library once it is one.
    /// </remarks>
    public IReadOnlyList<RailPart> SeriesOffered { get; init; } = [];

    /// <summary>True while there is a spanning part to offer as a series element.</summary>
    public bool HasSeriesOffer => SeriesOffered.Count > 0;

    /// <summary>The sentence the series offer carries, or empty.</summary>
    public string SeriesOfferSentence =>
        SeriesOffered.Count == 0 ? ""
        : SeriesOffered.Count == 1
            ? $"{SeriesOffered[0].Refdes} has both pads on this rail — the rail may run through it. " +
              "Add it as a series element to cut the rail there."
            : $"{string.Join(", ", SeriesOffered.Select(p => p.Refdes))} each have both pads on this " +
              "rail — the rail may run through them. Add them as series elements to cut the rail " +
              "there.";

    /// <summary>True while there is something to offer — the button appears only when there is.</summary>
    public bool HasOffer => Offered.Count > 0;

    /// <summary>The candidate rows alone — what an accept adds.</summary>
    public IReadOnlyList<RailPart> Parts => [.. Offered.Select(c => c.Part)];

    /// <summary>
    /// R-rail26-4's headline, or empty where nothing is offered.
    /// </summary>
    public string OfferSentence =>
        Offered.Count == 0 ? ""
        : Offered.Count == 1
            ? "1 two-terminal part sits between this rail and its reference and is not in this document."
            : $"{Offered.Count} two-terminal parts sit between this rail and its reference and are " +
              "not in this document.";

    /// <summary>
    /// The other half, or empty where nothing was skipped.
    /// </summary>
    /// <remarks>
    /// <b>It is not decoration</b> (R-rail26-4). A designer who is told 24 were added and not that
    /// 12 were skipped has no way to know whether the bulk capacitor they are looking for is one of
    /// the 12.
    /// </remarks>
    public string SkippedSentence
    {
        get
        {
            if (Skipped.Count == 0) return "";

            var clauses = new List<string>();
            foreach (var reason in (RailDiscoverySkip[])Enum.GetValues(typeof(RailDiscoverySkip)))
            {
                int n = Skipped.Count(s => s.Reason == reason);
                if (n > 0) clauses.Add(Clause(reason, n));
            }

            string head = Skipped.Count == 1
                ? "1 more part touches this rail and is not decoupling"
                : $"{Skipped.Count} more parts touch this rail and are not decoupling";

            return $"{head}: {string.Join(", ", clauses)}.";
        }
    }

    private static string Label(RailDiscoveryCandidate c) =>
        c.Mounting?.Henries is { } h ? $"{c.Refdes} ({h * 1e12:0.#} pH)" : c.Refdes;

    private static string Clause(RailDiscoverySkip reason, int n) => reason switch
    {
        RailDiscoverySkip.SpansTheRail =>
            n == 1 ? "1 spans the rail twice" : $"{n} span the rail twice",
        RailDiscoverySkip.NotOnTheReturnPath =>
            n == 1 ? "1 has its other pad off this rail's return path"
                   : $"{n} have their other pad off this rail's return path",
        RailDiscoverySkip.MoreThanTwoPads =>
            n == 1 ? "1 has more than two pads" : $"{n} have more than two pads",
        _ => n == 1 ? "1 has only one stated pad" : $"{n} have only one stated pad",
    };

    /// <summary>
    /// The whole answer, as <c>circuitrf rail</c> prints it — one line per sentence, the offered
    /// refdeses named, and every skipped part with its own reason.
    /// </summary>
    /// <remarks>
    /// <b>The refdeses are NAMED rather than only counted</b>, because the verb writes no rows
    /// (R-rail26-7) and naming them is what lets an out-of-process agent write the same ones the
    /// window is offering.
    /// </remarks>
    public IReadOnlyList<string> Lines
    {
        get
        {
            if (State == RailDiscoveryState.NotExtracted) return [];

            var lines = new List<string>();

            if (Offered.Count > 0)
            {
                lines.Add(OfferSentence);

                // Each named, WITH the mounting loop the geometry gives it where one resolved
                // (R-rail26-3a) — that loop is a property of where the part was PLACED and it is
                // the one number a caller writing these rows itself cannot work out from the
                // document. A part whose return via is too far to price is still a part, so it is
                // listed with no figure rather than dropped.
                lines.Add("  " + string.Join(", ", Offered.Select(Label)));
            }

            if (Skipped.Count > 0)
            {
                lines.Add(SkippedSentence);
                foreach (var s in Skipped) lines.Add("  " + s.Sentence);
            }

            lines.AddRange(Notes);
            return lines;
        }
    }
}

/// <summary>Everything discovery reads. <b>Nothing new is computed about the board.</b></summary>
/// <remarks>
/// Every field is something the window and the verb both already hold (R-rail26-1a) — the pads off
/// <c>RailArtwork.PadsFor</c>, the rail, the region set from the last extraction, the technology,
/// the flattened shapes, and the bill of materials where one was read.
/// </remarks>
public sealed class RailDiscoveryRequest
{
    /// <summary>The rail. <b>Its own part rows are what makes the offer idempotent</b>
    /// (R-rail26-4a): a refdes it already carries is never offered again.</summary>
    public required RailSpec Rail { get; init; }

    /// <summary>The board's pads — the netlist's where it speaks, the artwork's everywhere else.
    /// <c>RailArtwork.PadsFor</c>'s own answer, never a second reading of the board.</summary>
    public required IReadOnlyList<PlacedPin> Pads { get; init; }

    /// <summary>
    /// The rail's galvanic islands and its reference's, off the LAST EXTRACTION. Null before the
    /// first solve, which is <see cref="RailDiscoveryState.NotExtracted"/> and not an empty board.
    /// </summary>
    public PdnRailRegionSet? Regions { get; init; }

    /// <summary>The bill of materials, where one was read — the ONLY source of a part number
    /// (R-rail26-3). Null, refused, or silent about a refdes all mean the same thing: no part
    /// number, and the row is listed as unresolved.</summary>
    public BomTable? Bom { get; init; }

    /// <summary>The stackup, for the mounting loop. Null reports no loop and offers the same
    /// rows.</summary>
    public Technology? Technology { get; init; }

    /// <summary>The flattened artwork, for the same. Only <c>ViaShape</c>s are read.</summary>
    public IReadOnlyList<LayoutShape> Shapes { get; init; } = [];

    /// <summary>The artwork's DBU resolution.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The reference return's net, where one is named — the mounting loop's own input.
    /// <b>Not the predicate</b>: which copper is the reference is the region walk's answer and this
    /// is only how a loop tells a part's return pad from its power pad.</summary>
    public string? ReferenceNet { get; init; }
}

/// <summary>The rail's parts, read off the board. <b>Offered, never written.</b></summary>
public static class RailPartDiscovery
{
    /// <summary>
    /// Classifies every part the board places against one rail's copper and its reference's.
    /// </summary>
    public static RailDiscoveryResult Discover(RailDiscoveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // R-rail26-2b. BOTH halves of the predicate come from the extraction, so there is nothing to
        // discover until the rail has been solved once — and the state is read off whether the
        // REGIONS are there rather than off whether the reference was confirmed.
        if (request.Regions is not { } regions || regions.Power.Count == 0)
            return RailDiscoveryResult.None(RailDiscoveryState.NotExtracted);

        var rail = request.Rail;
        string? referenceNet = request.ReferenceNet;

        var already = new HashSet<string>(
            rail.Parts.Select(p => p.Refdes).Where(r => r.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var offeredRows = new List<RailPart>();
        var seriesRows = new List<RailPart>();
        var skipped = new List<RailDiscoverySkipped>();
        var notes = new List<string>();
        var ambiguous = new List<string>();

        foreach (var (refdes, pads) in PadsByRefdes(request.Pads))
        {
            int onRail = 0, onReference = 0;
            foreach (var pad in pads)
            {
                if (In(regions.Power, pad) && NetAllows(pad, rail.NetName)) onRail++;
                else if (In(regions.Reference, pad) && NetAllows(pad, referenceNet)) onReference++;
            }

            // Not this rail's business. SILENT — a board's other rails and its unrelated parts are
            // not something this rail has anything to say about, and listing them would bury the
            // twelve that DO touch it.
            if (onRail == 0) continue;

            // R-rail26-4a. Already a row, so not an offer and not a skip — adding twice adds
            // nothing, and a part the user has is not a part they are being told about.
            if (already.Contains(refdes)) continue;

            if (pads.Count == 1)
            {
                skipped.Add(new RailDiscoverySkipped(
                    refdes, RailDiscoverySkip.OneTerminalOnly,
                    $"{refdes} has one pad on this rail and the board states no other pad for it, " +
                    "so nothing bridges from this rail to the reference through it."));
                continue;
            }

            if (pads.Count > 2)
            {
                skipped.Add(new RailDiscoverySkipped(
                    refdes, RailDiscoverySkip.MoreThanTwoPads,
                    $"{refdes} has {pads.Count} pads, {onRail} of them on this rail. A part with " +
                    "more than two terminals is a load, a connector or a regulator, never a " +
                    "decoupling capacitor — add it as a load if it draws current."));
                continue;
            }

            if (onRail == 2)
            {
                // Brief 35 (R-rail35-1a): the sentence used to say "add it as a series element" —
                // a gesture that did not exist, so a designer added the part with + and got a shunt
                // row. It names the gesture now, and the row is offered with its terminals.
                skipped.Add(new RailDiscoverySkipped(
                    refdes, RailDiscoverySkip.SpansTheRail,
                    $"{refdes} has both pads on this rail's own copper, so it is IN the rail rather " +
                    "than across it — a ferrite, a sense resistor, a link. If the rail runs through " +
                    $"it, choose Add {refdes} as series element on the parts table's right-click " +
                    "menu: its two pads become the element's terminals."));

                var bomRows = request.Bom is { Refusal: null } b ? b.RowsFor(refdes) : [];
                seriesRows.Add(new RailPart
                {
                    Refdes = refdes,
                    PartNumber = bomRows.Count == 1 ? bomRows[0].PartNumber ?? "" : "",
                    Origin = bomRows.Count == 1 && bomRows[0].PartNumber is { Length: > 0 }
                        ? RailPartOrigin.Bom : RailPartOrigin.Artwork,
                    Connection = RailPartConnection.Series,
                    TerminalA = TerminalOf(pads[0], pads[1]),
                    TerminalB = TerminalOf(pads[1], pads[0]),
                });
                continue;
            }

            if (onReference != 1)
            {
                skipped.Add(new RailDiscoverySkipped(
                    refdes, RailDiscoverySkip.NotOnTheReturnPath,
                    $"{refdes} has one pad on this rail and the other on neither this rail nor its " +
                    "reference, so it is not on this rail's return path."));
                continue;
            }

            // ── R-rail26-3: the part NUMBER comes from the BOM or from nowhere ────────────────
            var rows = request.Bom is { Refusal: null } bom ? bom.RowsFor(refdes) : [];

            string partNumber = "";
            var origin = RailPartOrigin.Artwork;

            if (rows.Count == 1 && rows[0].PartNumber is { Length: > 0 } pn)
            {
                partNumber = pn;
                origin = RailPartOrigin.Bom;
            }
            else if (rows.Count > 1)
            {
                // RebuildParts already refuses to pick between approved manufacturers; discovery
                // must not undo that by choosing here.
                ambiguous.Add(refdes);
            }

            offeredRows.Add(new RailPart
            {
                Refdes = refdes,
                PartNumber = partNumber,
                Origin = origin,

                // NULL, deliberately — see RailDiscoveryCandidate.Mounting.
                MountingInductanceHenries = null,
            });
        }

        offeredRows.Sort((a, b) => string.Compare(a.Refdes, b.Refdes, StringComparison.OrdinalIgnoreCase));
        seriesRows.Sort((a, b) => string.Compare(a.Refdes, b.Refdes, StringComparison.OrdinalIgnoreCase));
        skipped.Sort((a, b) => string.Compare(a.Refdes, b.Refdes, StringComparison.OrdinalIgnoreCase));

        if (ambiguous.Count > 0)
            notes.Add(
                $"{ambiguous.Count} of these have more than one bill-of-materials row and were " +
                $"offered with NO part number: {string.Join(", ", ambiguous)}. One internal part " +
                "number sits in front of a list of approved manufacturers, and taking the first is " +
                "what produces a plausible model from the wrong manufacturer's part — so each is " +
                "listed as unresolved until somebody says which.");

        return new RailDiscoveryResult(
            RailDiscoveryState.Discovered,
            [.. offeredRows.Select(p => new RailDiscoveryCandidate(p, null))
                           .Zip(Mounting(request, offeredRows), (c, m) => c with { Mounting = m })],
            skipped,
            notes)
        {
            SeriesOffered = seriesRows,
        };
    }

    /// <summary>
    /// A part's two ends as series terminals, where the board places EXACTLY two pads for it — or
    /// null where it places fewer or more (brief 35, R-rail35-1a).
    /// </summary>
    /// <remarks>
    /// <b>What Make series element writes, and the same reading the offer above makes</b>, so a row
    /// marked series from the window and one accepted from the offer name their ends identically. A
    /// part with three pads is not guessed at: its row is left with no terminals, and the partition
    /// refuses that by name with what to give it.
    /// </remarks>
    public static (RailPortAnchor A, RailPortAnchor B)? SeriesTerminals(
        string refdes, IReadOnlyList<PlacedPin> pads)
    {
        ArgumentNullException.ThrowIfNull(pads);
        foreach (var (r, own) in PadsByRefdes(pads))
            if (string.Equals(r, refdes, StringComparison.OrdinalIgnoreCase))
                return own.Count == 2 ? (TerminalOf(own[0], own[1]), TerminalOf(own[1], own[0])) : null;
        return null;
    }

    /// <summary>
    /// One end of a spanning part, as a series terminal: its refdes and PIN where the board names
    /// distinct pins, and its pad's own coordinate where it does not — a Gerber set with no netlist
    /// names no pins, and a refdes alone would resolve to BOTH pads and model a short.
    /// </summary>
    private static RailPortAnchor TerminalOf(PlacedPin pad, PlacedPin other) =>
        pad.Pin is { Length: > 0 } pin && !string.Equals(pin, other.Pin, StringComparison.OrdinalIgnoreCase)
            ? new RailPortAnchor { Refdes = pad.Refdes, Pin = pin }
            : new RailPortAnchor { Point = (pad.X, pad.Y) };

    /// <summary>
    /// The mounting loops, in the same order as <paramref name="rows"/> — or a null for each where
    /// the caller supplied no geometry to read one from.
    /// </summary>
    private static IReadOnlyList<PdnMountingLoop?> Mounting(
        RailDiscoveryRequest request, IReadOnlyList<RailPart> rows)
    {
        if (rows.Count == 0) return [];
        if (request.Technology is not { } tech || request.Shapes.Count == 0)
            return [.. rows.Select(_ => (PdnMountingLoop?)null)];

        var loops = PdnMountingLoopExtractor.ComputeAll(
            new PdnMountingLoopRequest
            {
                Rail         = request.Rail,
                Shapes       = request.Shapes,
                Technology   = tech,
                DbuPerMicron = request.DbuPerMicron,
                Pads         = request.Pads,
                ReferenceNet = request.ReferenceNet,
            },
            rows.Select(r => r.Refdes));

        return [.. loops.Select(l => (PdnMountingLoop?)l)];
    }

    /// <summary>
    /// The board's pads grouped by the part that carries them, DE-DUPLICATED.
    /// </summary>
    /// <remarks>
    /// <b>The count is the discriminator</b>, so a pad stated twice would turn a two-terminal
    /// capacitor into a three-terminal load — silently, and in the safe-looking direction of
    /// offering fewer parts. <c>RailArtwork.PadsFor</c> already drops the artwork's reading of a
    /// part the netlist names, so this is the belt to that braces; the key is the pin and the
    /// coordinate together, because a part whose two pads share a pin name is still two pads.
    /// </remarks>
    private static IEnumerable<(string Refdes, IReadOnlyList<PlacedPin> Pads)> PadsByRefdes(
        IReadOnlyList<PlacedPin> pads)
    {
        var byRefdes = new Dictionary<string, List<PlacedPin>>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<(string, string, long, long)>();

        foreach (var pad in pads)
        {
            if (pad.Refdes is not { Length: > 0 } refdes) continue;
            if (!seen.Add((refdes, pad.Pin ?? "", pad.X, pad.Y))) continue;

            if (!byRefdes.TryGetValue(refdes, out var list))
                byRefdes[refdes] = list = [];
            list.Add(pad);
        }

        foreach (var (refdes, list) in byRefdes) yield return (refdes, list);
    }

    /// <summary>
    /// Whether a pad's own stated net permits it to be on the copper named by
    /// <paramref name="net"/>.
    /// </summary>
    /// <remarks>
    /// <b>A VETO, never a claim.</b> It can only take a pad OFF a piece of copper containment put
    /// it on; it never puts one on. Unstated on either side is not evidence, so it permits — which
    /// is what leaves the Gerber case on containment alone, as this file's header explains.
    /// </remarks>
    private static bool NetAllows(PlacedPin pad, string? net) =>
        pad.Net is not { Length: > 0 } stated || net is not { Length: > 0 } wanted ||
        string.Equals(stated, wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a pad lands in any of <paramref name="islands"/>.
    /// </summary>
    /// <remarks>
    /// <b>The same containment test <c>RailSeriesPartition.IslandOf</c> uses</b> — holes honoured,
    /// and clipped against a 2 DBU square rather than a winding count, because a pad coordinate
    /// lands ON a boundary as often as inside one.
    /// </remarks>
    private static bool In(IReadOnlyList<PdnRegion> islands, PlacedPin pad)
    {
        foreach (var region in islands)
        {
            if (!region.Bounds.Contains(pad.X, pad.Y)) continue;
            foreach (var (_, paths) in region.Copper)
                if (Regions.Contains(paths, pad.X, pad.Y)) return true;
        }
        return false;
    }
}
