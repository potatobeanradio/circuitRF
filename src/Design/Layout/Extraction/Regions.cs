// Which copper is one galvanically-joined piece, and which pieces are this rail's
// (docs/sonnet-briefs/brief-railrf-3-mesh-extractor.md R-rail3-3 / R-rail3-4, promoted by
// brief-lvs-2-shared-extraction.md R-lvs2-1).
//
// ── THE WALK IS ALREADY WRITTEN, AND THERE IS NO SECOND ONE ────────────────────────────────────
//
// Layout/Drc/DrcConnectivity.cs partitions flat per-layer geometry into electrically-joined pieces,
// bridging layers THROUGH VIA GEOMETRY rather than by assuming the metal above and below overlaps —
// which is what makes an offset staircase of metal connect correctly. It is `internal`, which is not
// an obstacle: this file is the same assembly.
//
// DO NOT COPY IT, DO NOT MAKE IT PUBLIC, AND DO NOT WRITE A SECOND WALK. A board whose island
// structure the DRC, railRF and LVS disagree about is a bug none of them reports.
//
// What it does NOT do is name a net. Net identity comes from the board netlist or the .kicad_pcb
// (brief 2) and this file joins the two: a NET POINT is a (net name, coordinate) pair, and the
// pieces containing those points are the rail.
//
// ── THE COPPER STOPS AT EVERY PAD, AND THAT IS CORRECT (railrf §2.8) ───────────────────────────
//
// On imported artwork the board is not electrically continuous until the user has said what bridges
// each gap. So the island structure is a first-class OUTPUT — "this rail is three regions joined by
// a 20 mil neck" — and §2.3 step 2 says that alone has caught real problems. Two islands joined by
// nothing at DC and by a capacitor at AC is NOT an error and must not be reported as one. It is two
// regions, stated.
//
// ── WHY THE RECORDS BELOW STILL SPELL THEMSELVES `Pdn` ─────────────────────────────────────────
//
// PdnNetPoint, PdnRegion and PdnRailRegionSet came here with the walk. R-lvs2-1's table renames the
// types LVS and railRF SHARE and says nothing about these three, which are railRF's own vocabulary
// for a rail and its return — and R-lvs2-1d forbids an alias, so renaming them would be sixty call
// sites of churn for no reader's benefit. PdnConductor did not come at all: it PRICES copper, and
// R-lvs2-1b keeps every pricing type in Pdn.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;

namespace CircuitRF.Design.Layout.Extraction;

/// <summary>A (net, coordinate) pair — what the board netlist or the <c>.kicad_pcb</c> knows that
/// the geometry does not. <c>BoardNetlistRecord</c> maps onto this directly.</summary>
/// <param name="Net">The net name, as that file spells it.</param>
/// <param name="X">DBU, on the artwork's own coordinate system.</param>
/// <param name="Y">DBU.</param>
/// <param name="Layer">The drawing layer the point's LAND is on, where that is known — a placed
/// footprint's pad is; a netlist record and a via are not. <see cref="Regions.Walk"/> seeds a
/// point that states one ON THAT LAYER ONLY (field report, 2026-09-22): a pad is a coordinate, a
/// ground plane is usually under every one of them, and before a reference layer was named there was
/// nothing to exclude — so picking any net on such a board outlined the whole board.</param>
public readonly record struct PdnNetPoint(string Net, long X, long Y, LayerKey? Layer = null);

/// <summary>
/// One galvanically-joined island of a rail's copper, across every layer it reaches.
/// </summary>
/// <param name="Index">0-based, in descending area order — so island 0 is the main body and the
/// report reads the way a user would say it.</param>
/// <param name="Copper">The island's geometry, per drawing layer.</param>
/// <param name="Bounds">Its bounding box, DBU.</param>
/// <param name="AreaSquareDbu">Total copper area summed over every layer.</param>
public sealed record PdnRegion(
    int Index,
    IReadOnlyList<(LayerKey Layer, Paths64 Paths)> Copper,
    Bbox Bounds,
    double AreaSquareDbu);

/// <summary>
/// The rail's copper and its reference's, each partitioned into islands, plus the sentence a report
/// prints.
/// </summary>
/// <param name="Power">The rail's own islands, largest first. Empty means the net names no copper —
/// which the extractor refuses rather than meshing nothing.</param>
/// <param name="Reference">The reference conductor's islands, largest first, after the reference
/// extent has been applied.</param>
/// <param name="IslandReport">The sentence — "this rail is one region" or "this rail is three
/// regions". Carried into <see cref="PdnProvenance.IslandReport"/> and onto every export.</param>
/// <param name="Diagnostics">Everything else worth saying.</param>
/// <param name="OwnReturnRefusal">
/// R-rail27-2 — <b>the rail is anchored on its own reference return's copper</b>, so there is
/// nothing left for current to return through. Non-null means the extraction must REFUSE; null is
/// every ordinary board.
///
/// <para><b>Why a refusal and not a note.</b> On the reported board a rail made by clicking a pour
/// landed on the ground pour, and what came back was a solved RESULT: three notes saying the rail
/// has copper on its own reference layer, one breakdown row of 526,314,394.9 squares of reference
/// copper, and a drop of 0 mV at 0 %. It looks like an answer, and nothing on it says the rail is
/// not a rail. The three notes it replaces were already printed and were read past — a result that
/// exists is evidence that the tool understood the question.</para>
/// </param>
public sealed record PdnRailRegionSet(
    IReadOnlyList<PdnRegion> Power,
    IReadOnlyList<PdnRegion> Reference,
    string IslandReport,
    IReadOnlyList<string> Diagnostics,
    string? OwnReturnRefusal = null)
{
    /// <summary>
    /// R-rail31-1 — which NET the return was taken to be, and how that was decided. What
    /// <see cref="Regions.ResolveReturnNet(string?, IReadOnlyDictionary{LayerKey, Paths64}, Technology, IReadOnlyList{PdnNetPoint}, LayerKey)"/>
    /// answered before the walk, carried so the result's provenance can say it.
    /// </summary>
    public PdnReturnNet ReturnNet { get; init; }

    /// <summary>
    /// R-rail31-3 — <b>the return could not be resolved to a net, and this rail has copper on the
    /// reference layer</b>, so "every piece on that layer" would mix the rail into its own return.
    /// Non-null means the extraction must REFUSE; a board whose reference layer carries only the
    /// plane never sets it.
    /// </summary>
    public string? MixedReturnRefusal { get; init; }
}

/// <summary>How the reference return's net was decided (R-rail31-1).</summary>
public enum PdnReturnNetBasis
{
    /// <summary>Neither named nor measurable: the reference is every piece on its layer.</summary>
    Unresolved,

    /// <summary>Named — the <c>.crail</c>'s <c>ReferenceNet</c>, or the request's.</summary>
    Named,

    /// <summary>Measured from the copper on the confirmed reference layer
    /// (<see cref="Regions.ReferenceNetOn"/>'s galvanic ambiguity).</summary>
    Measured,
}

/// <summary>
/// The reference return's net and where it came from — the one answer the window's reference row
/// and the run both read (R-rail31-1).
/// </summary>
/// <param name="Net">The net, or null where it could not be resolved.</param>
/// <param name="Basis">How it was decided.</param>
/// <param name="Layer">The reference layer, as a sentence names it.</param>
public readonly record struct PdnReturnNet(string? Net, PdnReturnNetBasis Basis, string Layer)
{
    /// <summary>The sentence a provenance and the reference row print.</summary>
    public string Describe() => Basis switch
    {
        PdnReturnNetBasis.Named    => $"'{Net}', named in the document",
        PdnReturnNetBasis.Measured => $"'{Net}', measured from the copper on {Layer}",
        _ => $"no net — none is named and the copper on {Layer ?? "the reference layer"} does not " +
             "measure to one, so every piece on that layer is the return",
    };
}

/// <summary>The galvanic region walk — <see cref="DrcConnectivity"/> joined to a net name.</summary>
public static class Regions
{
    /// <summary>
    /// Partitions <paramref name="layerRegions"/> into the rail's islands and the reference's.
    /// </summary>
    /// <param name="layerRegions">Per-layer unioned copper, DBU — built exactly as the DRC run
    /// builds it, so the two cannot disagree.</param>
    /// <param name="tech">Supplies the stackup that says which layers a via joins.</param>
    /// <param name="netPoints">What the board netlist knows. Only points naming
    /// <paramref name="railNet"/> or <paramref name="referenceNet"/> are read.</param>
    /// <param name="railNet">The power net's name, or null where the caller seeds by point instead.</param>
    /// <param name="referenceLayer">The reference return's drawing layer (§2.2, Q-8 — asked for,
    /// never inferred).</param>
    /// <param name="referenceNet">The reference net's name, or null to take the whole reference
    /// layer.</param>
    /// <param name="extraRailSeeds">Coordinates that are on the rail whatever the netlist says —
    /// the source and load anchors, which is how a rail with no net name at all is still found
    /// (§2.2's assisted Gerber path).</param>
    /// <param name="bareCoordinateSeeds">
    /// R-rail27-2 — the subset of <paramref name="extraRailSeeds"/> that came from an anchor which is
    /// <b>nothing but a coordinate</b>: no refdes, no pin. Those are the only anchors
    /// <see cref="PdnRailRegionSet.OwnReturnRefusal"/> is asked about, and the distinction is the
    /// whole of its safety. A refdes anchor NAMES A PART and a rail net name names a net; either is
    /// an independent statement about what this copper is, and a rail carrying one that also reaches
    /// the reference layer is an ordinary multilayer board. A pour click has neither — the
    /// coordinate is the only evidence there is, which is why it is the one route on which landing
    /// on the return cannot be detected any other way, and it is the only route available on a
    /// Gerber-only board.
    /// </param>
    public static PdnRailRegionSet Walk(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        IReadOnlyList<PdnNetPoint> netPoints,
        string? railNet,
        LayerKey referenceLayer,
        string? referenceNet,
        IReadOnlyList<(long X, long Y)> extraRailSeeds,
        IReadOnlyList<(long X, long Y)>? bareCoordinateSeeds = null) =>
        Walk(DrcConnectivity.Extract(layerRegions, tech), tech, netPoints, railNet, referenceLayer,
             referenceNet, extraRailSeeds, bareCoordinateSeeds);

    /// <summary><see cref="Walk(IReadOnlyDictionary{LayerKey, Paths64}, Technology, IReadOnlyList{PdnNetPoint}, string?, LayerKey, string?, IReadOnlyList{ValueTuple{long, long}}, IReadOnlyList{ValueTuple{long, long}}?)"/>
    /// over a partition already made — the extractors make it once for
    /// <see cref="ResolveReturnNet(string?, IReadOnlyList{DrcNetPiece}, Technology, IReadOnlyList{PdnNetPoint}, LayerKey)"/>
    /// and the walk both, and it is the one piece of either that is not free.</summary>
    internal static PdnRailRegionSet Walk(
        IReadOnlyList<DrcNetPiece> pieces,
        Technology tech,
        IReadOnlyList<PdnNetPoint> netPoints,
        string? railNet,
        LayerKey referenceLayer,
        string? referenceNet,
        IReadOnlyList<(long X, long Y)> extraRailSeeds,
        IReadOnlyList<(long X, long Y)>? bareCoordinateSeeds = null)
    {
        var diagnostics = new List<string>();

        if (pieces.Count == 0)
            return new PdnRailRegionSet([], [], "No copper was found on any layer.", diagnostics);

        var railSeeds = new List<(long X, long Y, LayerKey? Layer)>();
        var refSeeds = new List<(long X, long Y, LayerKey? Layer)>();

        foreach (var p in netPoints)
        {
            if (railNet is { Length: > 0 } && string.Equals(p.Net, railNet, StringComparison.OrdinalIgnoreCase))
                railSeeds.Add((p.X, p.Y, p.Layer));
            else if (referenceNet is { Length: > 0 } && string.Equals(p.Net, referenceNet, StringComparison.OrdinalIgnoreCase))
                refSeeds.Add((p.X, p.Y, null));
        }

        foreach (var (x, y) in extraRailSeeds) railSeeds.Add((x, y, null));

        // A seed point sits over the RETURN as well as over the rail — a pad is a coordinate and the
        // reference plane is usually under all of them. Seeding off it would make the reference an
        // island OF THE RAIL, which is a shorted board reported as an ordinary one.
        var railNets = NetsAt(pieces, railSeeds, anyLayer: true, exceptLayer: referenceLayer);

        // The reference is the copper on ITS OWN layer. Seeding it by net where the netlist names
        // one, and by "everything on that layer" where it does not — which is the ordinary case,
        // because a reference plane is usually the only thing on its layer and asking a user to name
        // its net twice buys nothing.
        var refNets = refSeeds.Count > 0
            ? NetsAt(pieces, refSeeds, anyLayer: false, onlyLayer: referenceLayer)
            : pieces.Where(p => p.Layer == referenceLayer).Select(p => p.Net).ToHashSet();

        // ── R-rail31-2: A RAIL SEED NEVER CLAIMS THE RETURN NET ─────────────────────────────────
        //
        // Excluding the reference LAYER from the rail's seeding is not enough, because the return is
        // a NET and reaches other layers. A coordinate on a VDD pad over a bottom-side ground pour
        // stitched to the reference plane seeded the pour, and the pour is galvanically the plane:
        // on the reported board the whole ground net (1,055 of its net points) was priced as supply
        // copper. Once the return's galvanic nets are known they are not rail, whatever lies over
        // them. Only a RESOLVED return may do this — "every piece on the layer" is not a net, and
        // is §3's refusal below where it would matter. And not where the net walked IS the return
        // (the window's preview of the return itself): that walk is asking for exactly this copper.
        bool returnResolved = refSeeds.Count > 0 && refNets.Count > 0;
        if (returnResolved
            && !(railNet is { Length: > 0 } && string.Equals(railNet, referenceNet, StringComparison.OrdinalIgnoreCase)))
            railNets.ExceptWith(refNets);

        var power = Islands(pieces, railNets, onlyLayer: null);
        var reference = Islands(pieces, refNets, onlyLayer: referenceLayer);

        string report = Describe("rail", power) + " " + Describe("reference", reference);

        string? ownReturn = railNet is { Length: > 0 }
            ? null   // see OwnReturnRefusalFor's own note: this is the POUR-PICK route's question
            : OwnReturnRefusalFor(pieces, bareCoordinateSeeds ?? [], refNets, returnResolved, tech,
                                  referenceLayer, referenceNet);

        // ── R-rail31-3: THE RETURN IS NOT A NET, AND THE RAIL IS ON ITS LAYER ──────────────────
        //
        // Exactly the case in which "every piece on the layer" silently mixes the rail into its
        // own return: on the reported board the rail's own 2.5 mm² land in an antipad was taken as
        // reference and node 0 was put on it. A reference layer carrying only the plane is untouched.
        string? mixed = returnResolved
            ? null
            : MixedReturnRefusalFor(pieces, railNets, netPoints, railNet, tech, referenceLayer, referenceNet);

        if (power.Count > 1)
            diagnostics.Add(
                $"The rail's copper is {power.Count} galvanically separate regions. On imported " +
                "artwork the copper stops at every pad, so this is ordinary and not an error — but " +
                "nothing bridges them at DC until a series part says what does.");

        return new PdnRailRegionSet(power, reference, report, diagnostics, ownReturn)
            { MixedReturnRefusal = mixed };
    }

    /// <summary>
    /// R-rail31-3 — the refusal for a rail with copper on a reference layer whose net is not known,
    /// beside copper that is not the rail's, or null.
    /// </summary>
    private static string? MixedReturnRefusalFor(
        IReadOnlyList<DrcNetPiece> pieces, HashSet<int> railNets, IReadOnlyList<PdnNetPoint> netPoints,
        string? railNet, Technology tech, LayerKey referenceLayer, string? referenceNet)
    {
        var mine = pieces.Where(p => p.Layer == referenceLayer && railNets.Contains(p.Net)).ToList();
        if (mine.Count == 0) return null;

        // MIXED, and only mixed. Where everything on the layer is the rail's — two plates and a via
        // field, the shape several mesh fixtures price on purpose (OwnReturnRefusalFor's note) —
        // "every piece" is not mixing anything in; there is nothing else there to be the return.
        if (!pieces.Any(p => p.Layer == referenceLayer && !railNets.Contains(p.Net))) return null;

        // WHICH NET, as the copper says — a point counts only where nothing else covers it (the
        // galvanic ambiguity ReferenceNetOn uses), because every pad on the board sits over a plane.
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var under = new HashSet<int>();
        foreach (var point in netPoints)
        {
            if (point.Layer is { } land && land != referenceLayer) continue;
            if (!mine.Any(p => p.Bounds.Contains(point.X, point.Y) && Contains(p.Paths, point.X, point.Y))) continue;

            under.Clear();
            foreach (var piece in pieces)
                if (piece.Bounds.Contains(point.X, point.Y) && Contains(piece.Paths, point.X, point.Y))
                    under.Add(piece.Net);
            if (under.Count != 1) continue;

            names[point.Net] = names.GetValueOrDefault(point.Net) + 1;
        }

        var found = names.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                         .Select(kv => $"'{kv.Key}'").ToList();
        if (found.Count == 0 && railNet is { Length: > 0 }) found.Add($"'{railNet}'");

        string what = found.Count switch
        {
            0 => "this rail's own copper",
            1 => $"this rail's copper, on net {found[0]},",
            _ => $"this rail's copper, on nets {string.Join(", ", found.Take(3))}{(found.Count > 3 ? " and more" : "")},",
        };

        string layer = LayerLabel(tech, referenceLayer);
        string why = referenceNet is { Length: > 0 } named
            ? $"The reference net '{named}' stands on no copper on {layer}"
            : $"No reference net is named, and the copper on {layer} does not measure to one net";

        return
            $"{why} — and {what} is on that layer too. Taking every piece on it as the return would " +
            "count the rail as its own return, so this rail is not solved. Name the reference net: " +
            "pick it in the reference row of the rail's card, or set \"ReferenceNet\" in the .crail.";
    }

    /// <summary>A layer as a sentence names it — "'GND' (layer 3/0)".</summary>
    internal static string LayerLabel(Technology tech, LayerKey layer) =>
        tech.Layers.FirstOrDefault(l => l.Key == layer)?.Name is { Length: > 0 } name
            ? $"'{name}' (layer {layer.Layer}/{layer.Datatype})"
            : $"layer {layer.Layer}/{layer.Datatype}";

    /// <summary>
    /// R-rail31-1 — <b>which net the return is, decided ONCE</b>, for the extraction and the window's
    /// reference row alike: the named net where there is one, else the net the copper on the
    /// confirmed reference layer measures to (<see cref="ReferenceNetOn"/>), else unresolved.
    /// </summary>
    /// <remarks>
    /// <b>The preview had this answer and the run never received it.</b> The window measured the
    /// return to mark its pick list, and the run was handed the document's <c>ReferenceNet</c>, which
    /// is empty on every Gerber board — so the run took every piece on the reference layer as the
    /// return, the rail's own lands among them. One function, called by both, is the fix; a second
    /// rule in either is the defect back.
    /// </remarks>
    /// <param name="namedNet">The reference net the request or the document names, or null.</param>
    /// <param name="layerRegions">Per-layer unioned copper, DBU.</param>
    /// <param name="tech">Supplies the stackup that says which layers a via joins.</param>
    /// <param name="netPoints">What the board netlist knows.</param>
    /// <param name="referenceLayer">The CONFIRMED reference layer.</param>
    public static PdnReturnNet ResolveReturnNet(
        string? namedNet,
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        IReadOnlyList<PdnNetPoint> netPoints,
        LayerKey referenceLayer)
    {
        ArgumentNullException.ThrowIfNull(layerRegions);

        // A named net needs no copper read at all — the partition is the expensive half.
        if (namedNet is { Length: > 0 })
            return new PdnReturnNet(namedNet, PdnReturnNetBasis.Named, LayerLabel(tech, referenceLayer));

        return ResolveReturnNet(null, DrcConnectivity.Extract(layerRegions, tech), tech, netPoints, referenceLayer);
    }

    /// <summary><see cref="ResolveReturnNet(string?, IReadOnlyDictionary{LayerKey, Paths64}, Technology, IReadOnlyList{PdnNetPoint}, LayerKey)"/>
    /// over a partition already made.</summary>
    internal static PdnReturnNet ResolveReturnNet(
        string? namedNet,
        IReadOnlyList<DrcNetPiece> pieces,
        Technology tech,
        IReadOnlyList<PdnNetPoint> netPoints,
        LayerKey referenceLayer)
    {
        string layer = LayerLabel(tech, referenceLayer);
        if (namedNet is { Length: > 0 }) return new PdnReturnNet(namedNet, PdnReturnNetBasis.Named, layer);

        return MeasureReturnNet(pieces, netPoints, referenceLayer) is { } measured
            ? new PdnReturnNet(measured, PdnReturnNetBasis.Measured, layer)
            : new PdnReturnNet(null, PdnReturnNetBasis.Unresolved, layer);
    }

    /// <summary>
    /// R-rail27-2 — the refusal for a rail anchored on the copper of its own reference return.
    /// </summary>
    /// <remarks>
    /// <b>The predicate is GALVANIC AMBIGUITY, which is <see cref="ReferenceNetOn"/>'s own
    /// discriminator and is exact.</b> A seed is on the reference only where EVERY piece of copper
    /// covering it is ONE galvanically-joined net, and that net is one the reference resolved to.
    /// That is what a click on a ground pour looks like: the pour, the plane under it and the stitch
    /// between them are one piece, and there is nothing else at that coordinate.
    ///
    /// <para><b>Containment against the plane alone would refuse every real board.</b> A pad is a
    /// coordinate and a reference plane is usually under all of them — <see cref="Walk"/> and
    /// <see cref="ReferenceNetOn"/> both record that trap from their own side. A click on the supply
    /// pour of a board with a ground plane covers two pieces that are NOT joined, so it is ambiguous
    /// and is not this.</para>
    ///
    /// <para><b>And it does not fire for a rail that merely reaches the reference LAYER.</b> A rail
    /// with a via landing there — a mixed plane carrying a power pour, a barrel through an antipad —
    /// resolves its seed to its own piece, not the plane's. That case is already reported by name as
    /// a diagnostic in both extractors, and it must stay a diagnostic: it is the difference between
    /// a refusal and a tool that refuses every real board.</para>
    ///
    /// <para><b>EVERY seed, or none.</b> A rail with one anchor on the return and others on real rail
    /// copper is a document with one bad anchor, which is a different fault and not this one — so
    /// the refusal is raised only where nothing the rail is anchored by landed anywhere else.</para>
    ///
    /// <para><b>AND ONLY WHERE NOTHING ELSE NAMES THE RAIL</b> — no net name, and an anchor that is
    /// a bare coordinate. Both gates are <see cref="Walk"/>'s, and both are the same argument: a net
    /// name or a refdes is an independent statement about what this copper is, and a rail carrying
    /// one whose copper also reaches the reference layer is an ordinary multilayer board. A top
    /// plane stitched to a bottom one is two plates and a via field, which is a legitimate thing to
    /// price and is what several of this repository's own mesh fixtures are. The pour-click route
    /// has no such statement, it is the only route available on a Gerber-only board, and it is how
    /// the reported rail came to be its own return.</para>
    ///
    /// <para><b>Not a name check.</b> A board may legitimately have several returns, and which net
    /// the reference is was MEASURED from the copper on the confirmed layer (R-rail19-1d). This is
    /// region membership, like everything else in this series.</para>
    /// </remarks>
    private static string? OwnReturnRefusalFor(
        IReadOnlyList<DrcNetPiece> pieces,
        IReadOnlyList<(long X, long Y)> seeds,
        HashSet<int> refNets, bool returnResolved,
        Technology tech, LayerKey referenceLayer, string? referenceNet)
    {
        if (seeds.Count == 0 || refNets.Count == 0) return null;

        var anchored = new HashSet<int>();
        var under = new HashSet<int>();

        foreach (var (x, y) in seeds)
        {
            under.Clear();
            foreach (var piece in pieces)
            {
                if (!piece.Bounds.Contains(x, y)) continue;
                if (Contains(piece.Paths, x, y)) under.Add(piece.Net);
            }

            if (under.Count == 0) continue;                              // on no copper at all

            // Ambiguous, or unambiguously on copper the reference did not resolve to: this rail is
            // anchored somewhere real and the question does not arise.
            if (under.Count != 1 || !refNets.Contains(under.First())) return null;

            anchored.Add(under.First());
        }

        if (anchored.Count == 0) return null;

        // ── AND THE REFERENCE MUST HAVE NOTHING LEFT ────────────────────────────────────────────
        //
        // This is the half that keeps a real board out of the refusal. A rail whose own copper sits
        // ON the reference layer — a mixed inner layer carrying a power pour, which is an ordinary
        // board — resolves its seed to a piece that is legitimately in `refNets`, because with no
        // reference NET named the reference is taken to be everything on its layer. What separates
        // that from the reported board is whether the plane's own piece survives: there, it does and
        // a return exists; on the reported board the rail's piece was the only thing on the layer,
        // and the sentence below is then literally true.
        //
        // R-rail31-2: this clause answers the "every piece on the layer" reading only. Where the
        // return is a resolved NET, `refNets` is that net's copper and nothing of the rail's, so a
        // seed that lands unambiguously on it is on the return — and a return split into pieces the
        // anchors did not all reach is still the return.
        if (!returnResolved && !refNets.IsSubsetOf(anchored)) return null;

        string what = referenceNet is { Length: > 0 } net
            ? $"'{net}'"
            : tech.Layers.FirstOrDefault(l => l.Key == referenceLayer)?.Name is { Length: > 0 } name
                ? $"'{name}'"
                : $"layer {referenceLayer.Layer}/{referenceLayer.Datatype}";

        return
            $"This rail is anchored on the copper of its own reference return ({what}), so there is " +
            "nothing for current to return through. Pick the supply pour instead, or name a " +
            "different reference layer.";
    }

    /// <summary>
    /// Which net the copper on <paramref name="referenceLayer"/> belongs to, MEASURED from the
    /// artwork — or null where the measurement does not resolve to exactly one net.
    /// </summary>
    /// <remarks>
    /// <b>Not a name rule, and that is the whole point</b>
    /// (<c>brief-railrf-19-unreachable-states.md</c> R-rail19-1c). Matching a NET called <c>GND</c>,
    /// <c>VSS</c> or <c>0V</c> is a guess about a string the user owns — a board may have several
    /// returns, or a rail genuinely named <c>GND</c> that is the subject. Matching the LAYER's name
    /// is the same guess in different clothes and is worse: layer names live in the technology and
    /// the pick list holds NETS, the two are unrelated objects, and the four-layer technology this
    /// application ships calls its planes <c>Inner 1</c> and <c>Inner 2</c>, which no ground-name
    /// rule catches. The <c>.ctech</c> layer table declares no role either.
    ///
    /// <para><b>A NET POINT OVER A PLANE IS NOT A NET POINT ON IT.</b> A pad is a coordinate with no
    /// layer on it, and a reference plane is usually under all of them — <see cref="Walk"/>'s own
    /// seeding note records the same fact from the other side. So a naive containment count on the
    /// shipped example reads 13 points for the return and 6 for the rail: the right answer by a
    /// margin that means nothing.</para>
    ///
    /// <para><b>The discriminator is GALVANIC AMBIGUITY, and it is exact.</b> A point is counted only
    /// where every piece of copper covering it belongs to ONE galvanically-joined net — which is what
    /// a pad with a via down to the plane looks like, because the via joins its land and the plane
    /// into a single piece. A rail pad merely sitting over the plane covers two pieces that are not
    /// joined, so it is ambiguous and is skipped rather than counted for either. On the shipped
    /// example that reads 10 unambiguous points on the reference layer for the return and <b>0</b>
    /// for the rail.</para>
    ///
    /// <para>Null on a tie, on no evidence at all, and on artwork whose reference layer carries no
    /// copper. A caller that cannot be told which net the return is must not act as though it
    /// had been.</para>
    /// </remarks>
    /// <param name="layerRegions">Per-layer unioned copper, DBU — <see cref="Walk"/>'s own input.</param>
    /// <param name="tech">Supplies the stackup that says which layers a via joins.</param>
    /// <param name="netPoints">What the board netlist knows.</param>
    /// <param name="referenceLayer">The CONFIRMED reference layer. Before it is confirmed there is
    /// nothing to measure against and this must not be called — railRF does not know yet, and
    /// acting as though it did is the guess this method exists to avoid.</param>
    public static string? ReferenceNetOn(
        IReadOnlyDictionary<LayerKey, Paths64> layerRegions,
        Technology tech,
        IReadOnlyList<PdnNetPoint> netPoints,
        LayerKey referenceLayer)
    {
        ArgumentNullException.ThrowIfNull(layerRegions);
        ArgumentNullException.ThrowIfNull(netPoints);

        return MeasureReturnNet(DrcConnectivity.Extract(layerRegions, tech), netPoints, referenceLayer);
    }

    /// <summary><see cref="ReferenceNetOn"/> over a partition already made.</summary>
    private static string? MeasureReturnNet(
        IReadOnlyList<DrcNetPiece> pieces, IReadOnlyList<PdnNetPoint> netPoints, LayerKey referenceLayer)
    {
        if (pieces.Count == 0) return null;

        var votes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var under = new HashSet<int>();

        foreach (var point in netPoints)
        {
            under.Clear();
            bool onReference = false;

            foreach (var piece in pieces)
            {
                if (!piece.Bounds.Contains(point.X, point.Y)) continue;
                if (!Contains(piece.Paths, point.X, point.Y)) continue;
                under.Add(piece.Net);
                if (piece.Layer == referenceLayer) onReference = true;
            }

            if (under.Count != 1 || !onReference) continue;

            votes.TryGetValue(point.Net, out int n);
            votes[point.Net] = n + 1;
        }

        string? best = null;
        int bestVotes = 0, tied = 0;

        foreach (var (net, n) in votes)
        {
            if (n > bestVotes) { best = net; bestVotes = n; tied = 1; }
            else if (n == bestVotes) tied++;
        }

        return bestVotes > 0 && tied == 1 ? best : null;
    }

    /// <summary>
    /// The OTHER net names standing on <paramref name="islands"/>, with how many pins each — the
    /// short a net pick would otherwise show as a board-sized outline and nothing else (field report,
    /// 2026-09-22).
    /// </summary>
    /// <remarks>
    /// <b>Only points that state their land's layer are asked</b> (<see cref="PdnNetPoint.Layer"/>):
    /// a placed pad is where a schematic pin stands and is evidence of what the copper under it is
    /// called; a netlist record or a via has no layer to be on, and asked of every layer it would
    /// find the plane under every pad and call the whole board shorted.
    /// </remarks>
    /// <param name="sameNet">Names that ARE the net walked — its own, and any alias of it (the
    /// schematic's ground <c>0</c> where the walked net is the measured return). Compared
    /// case-insensitively.</param>
    public static IReadOnlyList<(string Net, int Pins)> OtherNetsOn(
        IReadOnlyList<PdnRegion> islands, IReadOnlyList<PdnNetPoint> netPoints, IReadOnlyCollection<string> sameNet)
    {
        ArgumentNullException.ThrowIfNull(islands);
        ArgumentNullException.ThrowIfNull(netPoints);
        ArgumentNullException.ThrowIfNull(sameNet);

        var same = new HashSet<string>(sameNet, StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var point in netPoints)
        {
            if (point.Layer is not { } land || same.Contains(point.Net)) continue;

            foreach (var island in islands)
            {
                if (!island.Bounds.Contains(point.X, point.Y)) continue;
                if (!island.Copper.Any(c => c.Layer == land && Contains(c.Paths, point.X, point.Y))) continue;
                counts[point.Net] = counts.GetValueOrDefault(point.Net) + 1;
                break;
            }
        }

        return [.. counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                         .Select(kv => (kv.Key, kv.Value))];
    }

    private static string Describe(string what, IReadOnlyList<PdnRegion> islands) =>
        islands.Count switch
        {
            0 => $"The {what} has no copper.",
            1 => $"The {what} is one region.",
            _ => $"The {what} is {islands.Count} regions.",
        };

    /// <summary>The net indices of every piece containing one of <paramref name="seeds"/>.</summary>
    /// <remarks>A seed that states its land's layer meets only the piece on that layer — see
    /// <see cref="PdnNetPoint.Layer"/>. One that does not (a netlist record, a via, a clicked anchor)
    /// meets every layer, which is what those have always done.</remarks>
    private static HashSet<int> NetsAt(
        IReadOnlyList<DrcNetPiece> pieces,
        IReadOnlyList<(long X, long Y, LayerKey? Layer)> seeds,
        bool anyLayer,
        LayerKey onlyLayer = default,
        LayerKey? exceptLayer = null)
    {
        var found = new HashSet<int>();
        foreach (var (x, y, land) in seeds)
            foreach (var piece in pieces)
            {
                if (exceptLayer is { } skip && piece.Layer == skip) continue;
                if (!anyLayer && piece.Layer != onlyLayer) continue;
                if (land is { } own && piece.Layer != own) continue;
                if (!piece.Bounds.Contains(x, y)) continue;
                if (Contains(piece.Paths, x, y)) found.Add(piece.Net);
            }
        return found;
    }

    /// <summary>
    /// Whether <paramref name="paths"/> covers (<paramref name="x"/>, <paramref name="y"/>), holes
    /// honoured.
    ///
    /// <para>Clipped against a 2 DBU square rather than a winding count, because a pad coordinate
    /// from a drill file lands ON a boundary as often as inside one — a via's own centre is the
    /// centre of the hole it drilled, which is a HOLE in the copper. A square straddling the
    /// boundary still meets the annulus; a winding test at the exact centre does not.</para>
    /// </summary>
    internal static bool Contains(Paths64 paths, long x, long y)
    {
        Paths64 probe = [[new Point64(x - 1, y - 1), new Point64(x + 1, y - 1),
                          new Point64(x + 1, y + 1), new Point64(x - 1, y + 1)]];
        return Clipper.BooleanOp(ClipType.Intersection, paths, probe, LayoutClipper.Rule).Count > 0;
    }

    /// <summary>Groups the pieces of the given nets into islands, largest first.</summary>
    private static List<PdnRegion> Islands(
        IReadOnlyList<DrcNetPiece> pieces, HashSet<int> nets, LayerKey? onlyLayer)
    {
        var byNet = new Dictionary<int, List<DrcNetPiece>>();
        var order = new List<int>();

        foreach (var piece in pieces)
        {
            if (!nets.Contains(piece.Net)) continue;
            if (onlyLayer is { } only && piece.Layer != only) continue;
            if (!byNet.TryGetValue(piece.Net, out var list))
            {
                byNet[piece.Net] = list = [];
                order.Add(piece.Net);
            }
            list.Add(piece);
        }

        var islands = new List<(double Area, Bbox Bounds, List<(LayerKey, Paths64)> Copper, int Net)>();

        foreach (int net in order)
        {
            var list = byNet[net];
            double area = 0;
            var bounds = Bbox.Empty;
            var copper = new List<(LayerKey, Paths64)>();

            foreach (var piece in list)
            {
                area += Math.Abs(Clipper.Area(piece.Paths));
                bounds = bounds.Union(piece.Bounds);
                copper.Add((piece.Layer, piece.Paths));
            }

            islands.Add((area, bounds, copper, net));
        }

        // Descending area, then by net index so two islands of identical area never swap between
        // runs — the determinism gate of §7 depends on it, and so do briefs 9 and 17.
        islands.Sort((a, b) =>
        {
            int byArea = b.Area.CompareTo(a.Area);
            return byArea != 0 ? byArea : a.Net.CompareTo(b.Net);
        });

        var result = new List<PdnRegion>(islands.Count);
        for (int i = 0; i < islands.Count; i++)
            result.Add(new PdnRegion(i, islands[i].Copper, islands[i].Bounds, islands[i].Area));
        return result;
    }
}
