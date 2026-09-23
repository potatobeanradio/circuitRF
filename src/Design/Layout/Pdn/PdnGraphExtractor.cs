// The FAST reading: the copper as a GRAPH rather than a mesh, at DC
// (docs/sonnet-briefs/brief-railrf-4-fast-extractor.md R-rail4-1 … R-rail4-6, railrf.md §2.9, §4.6).
//
// ── IT IS NOT A SECOND SIMULATOR ───────────────────────────────────────────────────────────────
//
// §4.6: "It produces the same kind of netlist as §4.1, so the solver, the result model, the tables,
// the plots and the exports are identical AND ONLY THE EXTRACTOR DIFFERS. That is the reason the
// fast path is safe to have at all — it is not a second simulator, it is a second reading of the
// geometry."
//
// So this file returns a PdnNetlist — brief 3's record, with the same Origins, the same NodeCells
// and the same Ports — and everything after the copper is priced happens in PdnAssembly, which the
// mesh reading uses too. If this extractor needed a result type of its own, it would be a second
// simulator and the brief would be wrong.
//
// ── THE ARITHMETIC IS DELIBERATELY NOTHING NEW (R-rail4-1) ─────────────────────────────────────
//
//   Each trace section between junctions    one resistance, R = ρ·L / (W·T)
//   Each via                                its barrel resistance (PdnViaModel, unchanged)
//   Each pad                                a node
//   Each part                               its library model (unchanged)
//   Copper that is NOT trace-shaped         meshed, and coarsely
//
// ρ·L/(W·T) is the same closed form brief 3's gate uses. That is not circular: brief 3's MESH is
// gated against the closed form, and this extractor IS the closed form along a path — so the
// fast-vs-accurate gate is really a gate on THE PATH FINDING AND THE CLASSIFICATION, which is
// exactly where this extractor can be wrong.
//
// ── THE THREE PLACES IT CAN BE WRONG, AND WHAT EACH ONE COSTS (R-rail4-6) ──────────────────────
//
// Brief 3's mesh has no notion of a path: current goes where the copper is. This one has to FIND
// the sections, and that step has no closed form behind it.
//
//   1. A JUNCTION MISSED — two sections merged into one, and the branch current that left in the
//      middle is lost. The drop is then UNDER-estimated: optimistic. Junctions here come from the
//      skeleton's own degree, from every pad and from every via, so a junction is missed only where
//      the copper genuinely has no branch.
//
//   2. A SECTION'S WIDTH TAKEN AT ONE POINT rather than along its length. A taper read at its wide
//      end is optimistic; at its narrow end, pessimistic. THE INTEGRAL IS TAKEN — Σ Δs²/(σ·T·A)
//      over the section, which is the same "number of squares" arithmetic §2.8 describes and the
//      same finite-volume form the mesh uses in two dimensions. NEVER a single sample; see
//      SectionResistance below, and do not "simplify" it to L/W_mean, which is a different number.
//
//   3. A VIA TREATED AS A JUNCTION WHEN IT IS A PARALLEL GROUP. A layer transition with six vias is
//      ONE NODE and SIX PARALLEL BARRELS, not one barrel. That falls out here rather than being
//      special-cased: each via is priced by PdnViaModel and stamped between the two layers' nodes,
//      and the six land on one node because branch points closer together than the copper is wide
//      are one junction (JunctionMergeSquares).
//
// Each of these produces A PLAUSIBLE NUMBER AND NO ERROR, which is the recurring shape of every
// risk in this design.
//
// ── WHY A RASTER AND A SKELETON, AND NOT A GRID ────────────────────────────────────────────────
//
// A grid cannot find a section. A trace two grid cells across is topologically a ladder, not a
// chain, and no amount of contraction turns it into one resistance; a grid coarse enough to make it
// a chain merges the trace beside it. So the copper is rasterised and thinned (Zhang-Suen, which is
// a published algorithm and not ours), which gives a one-pixel centreline of ANY orientation —
// bends, 45s, T-junctions and stubs all fall out of it.
//
// THE RASTER IS NOT THE MODEL. It is how the topology is found and how the local width is measured;
// the netlist that comes out has one element per SECTION, which is what makes the solve a few
// hundred elements rather than thousands. Counting the netlist's elements is the structural
// property that makes this fast, and it is what the tests assert — never a wall-clock time.
//
// ── THE WIDTH IS AREA PER UNIT LENGTH, AND IT IS UNBIASED BY CONSTRUCTION ──────────────────────
//
// Every copper pixel is assigned to its nearest skeleton pixel, so a skeleton pixel carries the
// copper that belongs to it and W = A/Δs falls out with no distance transform and no calibration.
// The one thing a raster does get wrong is the boundary — a 0.3 mm trace is not an exact number of
// pixels wide — so the assigned areas are SCALED ONCE by (the polygon's true area) / (the raster's
// area). That removes the whole of the rasterisation bias in one multiply and is what lets a
// straight trace reproduce ρ·L/(W·T) to well under 1 % at a coarse pitch.

using Clipper2Lib;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>How the fast reading traces, and how coarsely it meshes what it will not trace.</summary>
public sealed class PdnGraphSettings
{
    /// <summary>
    /// How many raster pixels the narrowest copper on a piece gets across it. <b>Six, and it buys
    /// TOPOLOGY rather than accuracy</b> — the width is area per unit length and is unbiased at any
    /// pitch (see this file's header), so this number is what decides whether a thin neck survives
    /// thinning as a chain, not how right the resistance is.
    /// </summary>
    public int RasterCellsAcrossMinimumFeature { get; set; } = 6;

    /// <summary>A stated raster pitch in METRES, which overrides the computed one. Null is the
    /// ordinary case.</summary>
    public double? RasterPitchMetres { get; set; }

    /// <summary>How many coarse cells a spreading region gets across its narrower dimension. <b>Four
    /// is §2.9's "meshed, and coarsely" made a number</b>, and it is why rule 4 exists: the error it
    /// leaves is measured on the user's own board rather than promised here.</summary>
    public int PourCellsAcross { get; set; } = 4;

    /// <summary>The ceiling on coarse cells per spreading region. Above it the region is coarsened
    /// further and the coarsening is REPORTED — a fast model that quietly grew to ten thousand cells
    /// would no longer be the fast model.</summary>
    public int MaxPourCells { get; set; } = 2_000;

    /// <summary>
    /// The ceiling on cells per spreading region ONCE it is refined where current enters and
    /// leaves it (brief-railrf-33). The refinement is not optional — a port's spreading resistance
    /// is set by the cell it lands in — so a region that would need more than this is REFUSED,
    /// naming it, rather than answered at a contact size the mesh does not share.
    /// <para><b>200,000, not the 20,000 it shipped at.</b> The refined grid is a tensor product —
    /// every port adds lines across the whole region — so the count grows with the number of loads,
    /// not the region's size: a 50 mm return plane under 20 loads needs 28,106 cells, and at 20,000
    /// the fast model refused an ordinary many-load rail it answers in 0.6 s (80 loads on the same
    /// plane: 2.3 s). The ceiling is what keeps the fast model fast, and seconds is fast.</para>
    /// </summary>
    public int MaxRefinedPourCells { get; set; } = 200_000;

    /// <summary>
    /// How many coarse cells a spreading region gets between the two port pads on it that lie
    /// farthest apart (brief-railrf-33). Current runs from one port to the other across the whole
    /// of that distance, and four cells across the region left the field board's return plane with
    /// a few cells between its ports: a 30 × 20 mm plane with two ports 20 mm apart read 1.21× the
    /// mesh at four across, 1.07× at eight, and 1.01× once the ports themselves were refined too.
    /// Eight, and it only ever makes a region FINER than <see cref="PourCellsAcross"/> would.
    /// </summary>
    public int PourCellsBetweenPorts { get; set; } = 8;

    /// <summary>See <see cref="PdnCopperClassifier.TraceSquaresThreshold"/>.</summary>
    public double TraceSquaresThreshold { get; set; } = PdnCopperClassifier.TraceSquaresThreshold;

    /// <summary>
    /// Two branch points closer together than <i>this many squares</i> are ONE junction.
    ///
    /// <para>One square, and the reason is §2.9's "each pad a node": six vias in one land pad are one
    /// layer transition, and a model that made them six nodes joined by fractions of a square would
    /// report six barrels in series-parallel with copper that is not there. The term this gives away
    /// is BOUNDED — at most one square per merge, and the merges are counted in the notes — which is
    /// the opposite of the classification's unbounded one.</para>
    /// </summary>
    public double JunctionMergeSquares { get; set; } = 1.0;

    /// <summary>A leaf branch shorter than this many times the piece's own width is a corner artifact
    /// of thinning rather than a stub, and is pruned. A leaf carrying an attachment is NEVER pruned:
    /// a pad is a node whatever the skeleton thinks.</summary>
    public double SpurPruneWidths { get; set; } = 1.5;

    /// <summary>
    /// The frequency the answer is wanted at, in HERTZ, for a caller that has only the graph
    /// settings to hand. Anything above <see cref="PdnGraphExtractor.ShuntBandTopHz"/> is REFUSED
    /// rather than answered (R-rail4-4).
    ///
    /// <para><b><see cref="PdnExtractionRequest.FrequencyHz"/> is the spelling both readings share
    /// and it wins where it is set</b> (R-rail13-7). Brief 13 gave the mesh a frequency and the two
    /// readings have to be asked for the same one: a fast curve and an accurate curve of the same
    /// board taken at two different frequencies would be compared against each other on the same
    /// plot, and §2.9 rule 4 is that comparison.</para>
    /// </summary>
    public double FrequencyHz { get; set; }

    /// <summary>The ceiling on raster pixels per piece. A piece above it is rasterised coarser and
    /// the coarsening is reported.</summary>
    public int MaxRasterCells { get; set; } = 4_000_000;
}

/// <summary>Copper in, <c>ElaboratedNetlist</c> out — the same currency as
/// <see cref="PdnMeshExtractor"/>, read a different way.</summary>
public static class PdnGraphExtractor
{
    /// <summary>Metres per second, in vacuum.</summary>
    private const double SpeedOfLight = 299_792_458.0;

    /// <summary>
    /// How far below the plane pair's first cavity resonance the fast reading is allowed to go —
    /// <b>a tenth, and the number is derived rather than chosen</b>.
    ///
    /// <para>The fast model has no shunt branch: it prices copper and nothing else. That is honest
    /// while the plane pair is electrically short. A rectangular plane pair of span <c>a</c> resonates
    /// first at <c>f₁ = c / (2·√εᵣ·a)</c>, and at a frequency <c>f</c> its electrical length is
    /// <c>βa = π·f/f₁</c>. Treating a distributed shunt as absent costs <c>tan(βa)/βa − 1</c>; at
    /// <c>f = f₁/10</c> that is <b>3.4 %</b>, inside §7's own 5 % gate, and it grows without bound as
    /// <c>f</c> approaches <c>f₁</c>.</para>
    ///
    /// <para>So a tenth is where the omitted term stops being small, and above it Fast does not offer
    /// a degraded number — it offers NO number (§2.9 rule 3).</para>
    /// </summary>
    public const double CavityMarginFactor = 10.0;

    /// <summary>
    /// The frequency above which the fast reading refuses, in hertz, for a plane pair of the given
    /// span and dielectric.
    ///
    /// <para><b>Exposed because the refusal has to state it.</b> "Fast cannot answer above about
    /// 180 MHz on this stackup" is a sentence a user can act on; "Fast cannot answer here" is not.
    /// Brief 7's Run button asks this the same way the extraction does.</para>
    /// </summary>
    /// <param name="largestPlaneSpanMetres">The larger dimension of the plane pair.</param>
    /// <param name="epsilonR">The dielectric between them — the LARGEST in the stackup, which is the
    /// shortest wavelength and so the lowest first mode.</param>
    public static double ShuntBandTopHz(double largestPlaneSpanMetres, double epsilonR)
    {
        if (!(largestPlaneSpanMetres > 0) || !(epsilonR > 0)) return double.PositiveInfinity;
        double firstMode = SpeedOfLight / (2.0 * Math.Sqrt(epsilonR) * largestPlaneSpanMetres);
        return firstMode / CavityMarginFactor;
    }

    // ── the extraction ─────────────────────────────────────────────────────────────────────────

    /// <summary>Extracts <paramref name="request"/>'s rail as a graph, or refuses and says why.</summary>
    public static PdnExtraction Extract(PdnExtractionRequest request)
    {
        var rail = request.Rail;
        var tech = request.Technology;
        var diagnostics = new List<string>();
        var notes = new List<string>();
        var settings = request.Graph;

        if (rail.Refusal() is { } railRefusal) return PdnExtraction.Refused(railRefusal);

        if (rail.ReferenceLayer is not { } referenceLayer)
            return PdnExtraction.Refused(
                $"Rail '{rail.Name}' states no reference layer, so there is nothing to return current " +
                "through and no graph to build. Name the reference return's drawing layer — railRF " +
                "never infers one (railrf.md §2.2, Q-8).");

        // ── THE STAGE NAMES, AND WHY THEY EARN THEIR KEEP (field report, 2026-09-22) ──────────
        //
        // This extraction is the whole of what "solving…" meant on the window, and on a production
        // six-layer board it is where the wait is. A designer reported it as "probably run in a dead
        // end": the word was the only sign of life, it named no phase, and there was nothing to
        // press to stop it. Naming the phase costs one assignment and means the NEXT report of a
        // slow board arrives saying which phase was slow.
        //
        // Cancellation is checked at the same points and inside the one per-piece loop below, which
        // is RunControl's own stated granularity — within a unit of work, never inside a
        // factorisation. Null control makes every one of these a no-op, which is the headless case.
        var control = request.Control;
        control?.Token.ThrowIfCancellationRequested();
        if (control is not null) control.Stage = $"Rail '{rail.Name}': reading the board's copper";

        var layerRegions = LayerRegions.Build(request.Shapes, tech, diagnostics);
        if (layerRegions.Count == 0)
            return PdnExtraction.Refused(
                "This artwork flattens to no copper at all. Check that the layout view carries the " +
                "board's shapes and that its technology names the layers they are on.");

        // R-rail34-1/2: each seed carries the layer its copper is on where that is known — a pad's
        // land, or the layer a coordinate anchor states — so it seeds that copper and nothing under it.
        var anchorLands = new List<(long X, long Y, LayerKey? Layer)>();
        foreach (var s in rail.Sources) anchorLands.AddRange(PdnAttachments.ResolveLands(s.Anchor, request.Pads));
        foreach (var l in rail.Loads) anchorLands.AddRange(PdnAttachments.ResolveLands(l.Anchor, request.Pads));
        var anchorSeeds = anchorLands.Select(a => (a.X, a.Y)).ToList();

        // R-rail27-2: the anchors that are NOTHING BUT A COORDINATE — the pour-click route, and the
        // only one on which a rail anchored on its own return cannot be detected any other way. See
        // Regions.Walk's own note on why a refdes anchor is not one of these.
        var bareCoordinateSeeds = new List<(long X, long Y)>();
        foreach (var s in rail.Sources)
            if (s.Anchor.Refdes is not { Length: > 0 })
                bareCoordinateSeeds.AddRange(PdnAttachments.Resolve(s.Anchor, request.Pads));
        foreach (var l in rail.Loads)
            if (l.Anchor.Refdes is not { Length: > 0 })
                bareCoordinateSeeds.AddRange(PdnAttachments.Resolve(l.Anchor, request.Pads));

        control?.Token.ThrowIfCancellationRequested();
        if (control is not null) control.Stage = $"Rail '{rail.Name}': following the connectivity";

        var walked = PdnRailConnectivity.Walk(request, layerRegions, referenceLayer, anchorLands,
                                              bareCoordinateSeeds, out var ambiguous);

        // R-rail34-2, before anything is walked or priced: a coordinate standing on two nets would
        // make one rail of both.
        if (walked is not { } regions)
            return PdnExtraction.Refused(PdnRailConnectivity.AmbiguityRefusal(request, ambiguous))
                with { AnchorAmbiguities = ambiguous };

        diagnostics.AddRange(PdnRailConnectivity.DiagnosticsOf(request, regions));

        // R-rail27-2, BEFORE the no-copper refusal and before anything is meshed or priced: a rail
        // anchored on its own return resolves to plenty of copper, and that is exactly the trouble —
        // it comes back as a solved result nothing on the face contradicts.
        if (regions.OwnReturnRefusal is { } ownReturn)
            return PdnExtraction.Refused(ownReturn, regions);

        if (regions.Power.Count == 0)
            return PdnExtraction.Refused(
                $"Rail '{rail.Name}' resolves to no copper. " +
                (rail.NetName is { Length: > 0 } net
                    ? $"Nothing on this board is on net '{net}', and no source or load anchor landed on " +
                      "metal. Check the net name against the board netlist."
                    : "The rail names no net, and no source or load anchor landed on metal. Give the " +
                      "rail its net name, or anchor a source or a load on the rail's own copper."),
                regions);

        // R-rail29-2: A CONNECTIVITY QUESTION, ANSWERED BEFORE ANY PRICING. Whether the source can
        // reach a load at all is a galvanic fact the walk above has already settled, and nothing
        // below — the reference extent, the classification, the per-piece rasters — can change it.
        // On the reported board those stages were the whole of a five-minute Debug run (148 s
        // sorting, 146 s measuring) that ended in this answer, given wrongly; here it costs nothing.
        if (PdnRailConnectivity.Refusal(request, regions, referenceLayer) is { } apart)
            return PdnExtraction.Refused(apart, regions);

        // R-rail31-3 / R-rail31-4: what the RETURN is, answered as galvanically as the rail was.
        if (PdnRailConnectivity.ReturnRefusal(request, regions, referenceLayer) is { } noReturn)
            return PdnExtraction.Refused(noReturn, regions);

        double celsius = request.Settings.CopperTemperatureCelsius;
        if (PdnMeshExtractor.ResolveConductors(request, regions, referenceLayer,
                                               out _, out var byLayer) is { } conductorRefusal)
            return PdnExtraction.Refused(conductorRefusal, regions);

        double dbuPerMetre = request.DbuPerMicron * 1e6;

        // The narrowest rail copper pads the extent, and the extent is read ONLY by an INFINITE
        // reference, which is realised as a plate over it. As imported or filled to the outline it
        // is never read, and the measurement is the costliest single call on a board with a pour
        // in the rail — 182 s of a field report's 190 s "following the connectivity" stage
        // (brief-railrf-30). The mesh keeps its own: its cell size is set from it.
        long extentPad = rail.ReferenceExtent == RailReferenceExtent.Infinite
            ? Math.Max(1, PdnMeshExtractor.MinimumFeatureWidthDbu(regions.Power))
            : 1;

        var extent = PdnMeshExtractor.ExtentOf(regions, anchorSeeds, extentPad);
        if (PdnMeshExtractor.ResolveReferenceCopper(request, regions, referenceLayer, extent, notes,
                                                    out var referenceCopper) is { } extentRefusal)
            return PdnExtraction.Refused(extentRefusal, regions);

        // ── R-rail4-4: the frequency where the shunt branch stops being negligible ─────────────
        //
        // ONE SPELLING FOR BOTH READINGS (R-rail13-7). PdnExtractionRequest.FrequencyHz is what the
        // mesh takes and it wins here where it is set; PdnGraphSettings' own is what a caller with
        // only the graph settings to hand can still use. §2.9 rule 4 compares the two curves on the
        // user's own board, and two readings answering at two different frequencies would be
        // compared against each other with nothing to say so.
        double frequencyHz = request.FrequencyHz > 0 ? request.FrequencyHz : settings.FrequencyHz;

        var referenceBounds = Bbox.Empty;
        foreach (var (_, paths) in referenceCopper)
            referenceBounds = referenceBounds.Union(DrcRegions.BoundsOf(paths));

        double spanM = referenceBounds.IsEmpty
            ? 0
            : Math.Max(referenceBounds.MaxX - referenceBounds.MinX,
                       referenceBounds.MaxY - referenceBounds.MinY) / dbuPerMetre;
        double epsr = LargestEpsilonR(tech);
        double topHz = ShuntBandTopHz(spanM, epsr);

        if (frequencyHz > topHz)
            return PdnExtraction.Refused(
                $"The fast model cannot answer above {Hz(topHz)} on this stackup, and " +
                $"{Hz(frequencyHz)} was asked for. That ceiling is a tenth of this plane " +
                $"pair's first cavity resonance, {Hz(topHz * CavityMarginFactor)} over its " +
                $"{spanM * 1e3:0.#} mm span in εr {epsr:0.##}: the fast reading prices copper and " +
                "has no shunt branch at all, and leaving a distributed shunt out costs 3.4 % at a " +
                "tenth of the first mode and grows without bound above it. Run Accuracy — it carries " +
                "the cavity model and answers here.",
                regions);

        // ── R-rail4-3: which copper is trace-shaped, and WHY ───────────────────────────────────
        var attachmentPoints = AttachmentPoints(request);

        var classification = new List<PdnClassification>();
        var railCopper = new Dictionary<LayerKey, Paths64>();

        // A via's BARREL disc is on a via drawing layer and reaches the rail through the connectivity
        // walk. It is a BRIDGE between conductors rather than sheet copper of its own — PdnViaModel
        // prices it and PdnAssembly stamps it — so it is not classified and not sectioned. Reading it
        // as copper would put a handful of compact 'spreading' regions in the class tab for every via
        // on the board, and every one of them would be a decision nobody has to make.
        var viaDrawingLayers = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Via)
            .SelectMany(l => l.DrawingLayers)
            .ToHashSet();

        bool onReferenceSaid = false;   // once per run, not once per island
        foreach (var island in regions.Power)
            foreach (var (layer, paths) in island.Copper)
            {
                if (viaDrawingLayers.Contains(layer)) continue;
                if (layer == referenceLayer)
                {
                    if (onReferenceSaid) continue;
                    onReferenceSaid = true;
                    diagnostics.Add(
                        $"The rail has copper on layer {layer.Layer}/{layer.Datatype}, which is also " +
                        "its reference layer. That copper was not read as part of the rail — a " +
                        "conductor cannot be its own return.");
                    continue;
                }
                if (!railCopper.TryGetValue(layer, out var acc)) railCopper[layer] = acc = [];
                acc.AddRange(paths);
            }

        var refCopper = new Dictionary<LayerKey, Paths64>();
        foreach (var (layer, paths) in referenceCopper)
        {
            if (!refCopper.TryGetValue(layer, out var acc)) refCopper[layer] = acc = [];
            acc.AddRange(paths);
        }

        int PortsOn(Paths64 piece)
        {
            int n = 0;
            var b = DrcRegions.BoundsOf(piece);
            foreach (var (x, y) in attachmentPoints)
                if (b.Contains(x, y) && Regions.Contains(piece, x, y)) n++;
            return n;
        }

        control?.Token.ThrowIfCancellationRequested();
        if (control is not null)
            control.Stage = $"Rail '{rail.Name}': sorting trace-shaped copper from spreading copper";

        foreach (var (layer, paths) in Ordered(railCopper))
            classification.AddRange(PdnCopperClassifier.Classify(
                layer, DrcRegions.Union(paths), isReference: false, dbuPerMetre,
                request.ClassOverrides, settings.TraceSquaresThreshold, PortsOn));

        foreach (var (layer, paths) in Ordered(refCopper))
            classification.AddRange(PdnCopperClassifier.Classify(
                layer, DrcRegions.Union(paths), isReference: true, dbuPerMetre,
                request.ClassOverrides, settings.TraceSquaresThreshold, PortsOn));

        // ── the graph ──────────────────────────────────────────────────────────────────────────
        control?.Token.ThrowIfCancellationRequested();

        // THE ONE WITH A DENOMINATOR. Everything above is a single pass over the board; this is one
        // unit of work per classified piece, each of which rasterises and thins, and on a real board
        // it is most of the wall clock. A counter that advances is the difference between a long
        // answer and a hung one — which is exactly the distinction the report could not make.
        if (control is not null)
            control.BeginStage(
                $"Rail '{rail.Name}': measuring the copper", classification.Count, "piece(s)");

        var nodes = new PdnGraphNodes(request.DbuPerMicron);
        var build = new GraphBuild(
            request, frequencyHz, nodes, byLayer, classification, attachmentPoints, notes, diagnostics);

        if (build.Build() is { } buildRefusal)
            return PdnExtraction.Refused(buildRefusal, regions) with { Classification = classification };

        if (nodes.NodeTotal == 0)
            return new PdnExtraction(
                $"Rail '{rail.Name}' read as no nodes at all, which means the copper and the " +
                "attachments do not overlap. This is a coordinate-system disagreement rather than a " +
                "design problem.", null, regions, diagnostics) { Classification = classification };

        // ── R-rail4-5: a pour-dominated path is REFUSED, not answered ──────────────────────────
        if (build.PourDominatedRefusal() is { } pourRefusal)
            return new PdnExtraction(pourRefusal, null, regions, diagnostics)
                { Classification = classification };

        var asm = new PdnAssembly(request, nodes, PdnModelKind.Fast, celsius, notes, diagnostics);
        build.StageCopper(asm);
        if (asm.Build() is { } refusal)
            return PdnExtraction.Refused(refusal, regions) with { Classification = classification };

        int traces = classification.Count(c => c.Class == PdnCopperClass.Trace);
        int pours = classification.Count(c => c.Class == PdnCopperClass.Spreading);

        double planeCapacitance = PlaneCapacitance(
            request, railCopper, refCopper, referenceLayer, notes,
            out double overlapArea, out double h, out var medium, out var capacitanceBasis);

        var provenance = new PdnProvenance
        {
            ModelKind = PdnModelKind.Fast,
            Model = "Fast (graph)",
            RailName = rail.Name,
            ReferenceExtent = rail.ReferenceExtent,
            FrequencyHz = frequencyHz,
            PlaneSeparationMetres = h,
            PlaneCapacitanceFarads = planeCapacitance,
            PlaneCapacitanceBasis = capacitanceBasis,
            PlaneOverlapSquareMetres = overlapArea,
            RelativePermittivity = medium?.EpsilonR ?? 0,
            LossTangent = medium?.TanDelta ?? 0,
            LossTangentIsClassDefault = medium?.TanDeltaIsClassDefault ?? false,
            DielectricBasis = medium?.Basis ?? "",

            // The fast reading prices COPPER and stamps no shunt branch at all — R-rail4-4's own
            // ceiling exists because of that. The capacitance above is the stackup readout and
            // nothing else, which is exactly what this flag is for.
            ShuntBranchPresent = false,
            CellSizeMetres = build.CoarsestPourPitchDbu / dbuPerMetre,
            CellSizeBasis =
                $"the fast reading: {traces} region(s) priced as trace sections at " +
                $"{build.FinestRasterPitchDbu / dbuPerMetre * 1e3:0.####} mm of raster, and " +
                $"{pours} meshed coarsely at up to " +
                $"{build.CoarsestPourPitchDbu / dbuPerMetre * 1e3:0.###} mm",
            PortRefinementRatio = 1,
            CopperTemperatureCelsius = celsius,
            CellCount = nodes.NodeTotal,
            MeshedAreaSquareMetres = build.AccountedAreaSquareDbu / (dbuPerMetre * dbuPerMetre),
            IslandReport = regions.IslandReport,
            ReturnNet = regions.ReturnNet,
            ReferencePoint = asm.ReferencePoint,
            UnresolvedViaSpans = asm.UnresolvedViaSpans,
            Notes = notes,
        };

        return new PdnExtraction(null, asm.Finish(provenance), regions, diagnostics)
            { Classification = classification };
    }

    // ── helpers on the request ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every coordinate that must be a NODE — §2.9's "each pad a node", plus every via centre and
    /// every coordinate anchor.
    ///
    /// <para><b>Collected before the sections are cut, because a node cannot be added to a section
    /// afterwards.</b> A via whose centre fell in the middle of a section would otherwise attach to
    /// whichever end was nearer, and the layer transition would move by up to half a section — a
    /// plausible number and no error, which is failure shape 1 of R-rail4-6.</para>
    /// </summary>
    private static List<(long X, long Y)> AttachmentPoints(PdnExtractionRequest request)
    {
        var seen = new HashSet<(long, long)>();
        var points = new List<(long X, long Y)>();

        void Add(long x, long y) { if (seen.Add((x, y))) points.Add((x, y)); }

        foreach (var pad in request.Pads) Add(pad.X, pad.Y);
        foreach (var via in request.Shapes.OfType<ViaShape>()) Add(via.X, via.Y);

        void AddAnchor(RailPortAnchor a)
        {
            foreach (var (x, y) in PdnAttachments.Resolve(a, request.Pads)) Add(x, y);
        }

        foreach (var s in request.Rail.Sources) AddAnchor(s.Anchor);
        foreach (var l in request.Rail.Loads) AddAnchor(l.Anchor);
        foreach (var p in request.SeriesElements) { AddAnchor(p.A); AddAnchor(p.B); }
        foreach (var p in request.ShuntParts) AddAnchor(p.Anchor);

        // Deterministic: the order pieces claim their attachments in is the order nodes are handed
        // out in, and a node numbering that moved between runs would move the drop map with it.
        points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        return points;
    }

    // ── R-rail18-2a: the stackup check, on the model that is the DEFAULT ───────────────────────

    /// <summary>
    /// §4.1's plane capacitance over the rail's real overlap with its reference — the number §9
    /// calls the cheapest gate in the whole tool, computed by the FAST model as well as the mesh.
    /// </summary>
    /// <remarks>
    /// <b>It was missing here for three briefs, and the silence had a voice.</b>
    /// <c>RailDcResult.PlaneCapacitanceLine</c> reads a zero as <i>"the stackup states no dielectric
    /// between this rail's copper and its reference"</i> — a sentence about the USER'S board. The
    /// fast model is the default, so every board got told its stackup was wrong by the check that
    /// exists to catch a wrong stackup. <see cref="PdnPlaneCapacitanceBasis"/> is the other half of
    /// the repair and this is the half that makes the number exist.
    ///
    /// <para><b>The arithmetic is <see cref="PdnCavity"/>'s and not a second copy of it.</b> The
    /// mesh sums <see cref="PdnCavity.CapacitanceFarads"/> over the per-cell overlap of a discretised
    /// grid; this intersects the polygons exactly and calls the same expression once per layer pair.
    /// <b>The two therefore agree to the GRID rather than to machine precision</b> — the mesh's
    /// overlap is quantised to whole cells, so it reads a little under on any shape whose edges do
    /// not fall on cell boundaries, and 5 % is the tolerance this is gated at rather than
    /// discovered at.</para>
    /// </remarks>
    private static double PlaneCapacitance(
        PdnExtractionRequest request,
        Dictionary<LayerKey, Paths64> railCopper,
        Dictionary<LayerKey, Paths64> refCopper,
        LayerKey referenceLayer,
        List<string> notes,
        out double overlapSquareMetres,
        out double separationMetres,
        out PdnMedium? dominant,
        out PdnPlaneCapacitanceBasis basis)
    {
        double dbuPerMetre = request.DbuPerMicron * 1e6;
        double perSquareDbu = dbuPerMetre * dbuPerMetre;

        var conductors = PdnStackupGeometry.Conductors(request.Technology, request.DbuPerMicron);
        var reference = PdnStackupGeometry.ConductorOf(conductors, referenceLayer);

        double total = 0;
        overlapSquareMetres = 0;
        separationMetres = 0;
        dominant = null;

        var missing = new List<LayerKey>();
        var references = Ordered(refCopper).Select(r => DrcRegions.Union(r.Paths)).ToList();

        foreach (var (layer, paths) in Ordered(railCopper))
        {
            var medium = PdnCavity.MediumBetween(
                request.Technology, PdnStackupGeometry.ConductorOf(conductors, layer), reference);
            if (medium is null) { missing.Add(layer); continue; }

            // The NEAREST pair's separation is the one reported, exactly as the mesh reports it —
            // one number for a rail that may sit at several distances from the same reference.
            if (medium.ThicknessMetres > 0 &&
                (separationMetres == 0 || medium.ThicknessMetres < separationMetres))
            {
                separationMetres = medium.ThicknessMetres;
                dominant = medium;
            }
            dominant ??= medium;

            var rail = DrcRegions.Union(paths);
            foreach (var refPaths in references)
            {
                var overlap = Clipper.Intersect(rail, refPaths, LayoutClipper.Rule);
                double area = Math.Abs(Clipper.Area(overlap)) / perSquareDbu;
                double c = PdnCavity.CapacitanceFarads(medium.EpsilonR, area, medium.ThicknessMetres);
                if (!(c > 0)) continue;

                total += c;
                overlapSquareMetres += area;
            }
        }

        // The same two notes the mesh raises, in the same words, for the reason R-rail13-7 gives:
        // the two models are compared against each other on the user's own board and a warning that
        // appears on one of them reads as a difference between the boards.
        if (missing.Count > 0)
            notes.Add(
                "The stackup states no dielectric between this rail's copper on " +
                string.Join(", ", missing.Select(k => $"{k.Layer}/{k.Datatype}")) +
                " and its reference, so that copper carries no plane capacitance and no dielectric " +
                "loss. State the dielectric entries between them — C = ε₀εᵣA/h and G = ωC·tan δ are " +
                "the whole of §4.1's shunt branch.");

        if (dominant is { TanDeltaIsClassDefault: true } d)
            notes.Add(
                $"The stackup states no tan δ for this rail's dielectric, so railRF used " +
                $"{d.TanDelta:0.####} — {d.Basis}. Every peak height in the cavity band is therefore " +
                $"{RailEsrDefaults.Marking}: tan δ sets how sharp a plane resonance is, which is the " +
                "difference between a 6 dB bump and a 20 dB one.");

        // NOT the same as "the total came out zero". A rail whose copper simply does not overlap its
        // reference has a computed zero and no complaint to make about the stackup.
        basis = dominant is null
            ? PdnPlaneCapacitanceBasis.NoDielectricStated
            : PdnPlaneCapacitanceBasis.Computed;

        return total;
    }

    private static IEnumerable<(LayerKey Layer, Paths64 Paths)> Ordered(
        Dictionary<LayerKey, Paths64> byLayer) =>
        byLayer.OrderBy(kv => kv.Key.Layer).ThenBy(kv => kv.Key.Datatype)
               .Select(kv => (kv.Key, kv.Value));

    /// <summary>The largest ε_r in the stackup — the shortest wavelength, so the LOWEST first cavity
    /// mode, so the earliest refusal. Taking a mean would put the ceiling above where the shunt
    /// branch actually starts to matter on the worst layer pair.
    ///
    /// <para><b><see cref="PdnMeshExtractor"/>'s, and deliberately not a second reading of it</b> —
    /// the cavity-band refusal here and the mesh's own wavelength cell size are the same statement
    /// about the same stackup, and two readings of it could quietly come to disagree about where
    /// Fast stops being honest.</para></summary>
    private static double LargestEpsilonR(Technology tech) => PdnMeshExtractor.LargestEpsilonR(tech);

    private static string Hz(double f) =>
        f >= 1e9 ? $"{f / 1e9:0.###} GHz"
        : f >= 1e6 ? $"{f / 1e6:0.###} MHz"
        : f >= 1e3 ? $"{f / 1e3:0.###} kHz"
        : $"{f:0.###} Hz";
}

// ── the node source ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The graph reading's nodes, as <see cref="PdnAssembly"/> needs them.
///
/// <para><b>A node here is a junction, a pad, a via land or one coarse pour cell</b> — never a mesh
/// cell, because there is no mesh. It still carries a <see cref="PdnCellRef"/>, because that is the
/// currency briefs 8 and 15 colour a board from and "same currency" (§4.6) means exactly that the
/// downstream reads a fast result and an accurate one identically.</para>
/// </summary>
internal sealed class PdnGraphNodes(int dbuPerMicron) : IPdnNodeSource
{
    private readonly List<PdnCellRef> _cells = [];
    private readonly List<string> _names = [];
    private readonly List<bool> _attachable = [];
    private readonly Dictionary<(bool IsRef, long X, long Y), List<int>> _bySide = [];
    private readonly Dictionary<(LayerKey Layer, long X, long Y), int> _byLayer = [];
    private readonly List<(LayerKey Layer, bool IsReference, Bbox Bounds, Func<long, long, int> NodeAt)>
        _lookups = [];

    public int DbuPerMicron { get; } = dbuPerMicron;

    public int NodeTotal => _cells.Count;

    /// <summary>Hands out one node. The order they are handed out in IS
    /// <see cref="NodesInNaturalOrder"/>, and it is the order the pieces are walked in — never a
    /// dictionary's, because a node numbering that moved between runs would move the drop map.</summary>
    /// <param name="attachable">False on a cell holding too little copper to be a place a point
    /// is attached to — see <see cref="NearestReferenceNode"/>.</param>
    public int New(LayerKey layer, bool isReference, int ix, int iy, long cx, long cy, string name,
                   bool attachable = true)
    {
        _cells.Add(new PdnCellRef(layer, ix, iy, cx, cy, isReference));
        _names.Add(name);
        _attachable.Add(attachable);
        return _cells.Count - 1;
    }

    /// <summary>Records that a coordinate IS this node — every pad, every via centre and every
    /// coordinate anchor, so a later lookup lands on the node that was cut for it rather than on
    /// whichever end of a section happened to be nearer.</summary>
    public void RegisterPoint(LayerKey layer, bool isReference, long x, long y, int node)
    {
        var key = (isReference, x, y);
        if (!_bySide.TryGetValue(key, out var list)) _bySide[key] = list = [];
        if (!list.Contains(node)) list.Add(node);
        _byLayer.TryAdd((layer, x, y), node);
    }

    /// <summary>How a point that was never registered is resolved — the piece it lands on answers
    /// for itself.</summary>
    public void AddLookup(LayerKey layer, bool isReference, Bbox bounds, Func<long, long, int> nodeAt)
        => _lookups.Add((layer, isReference, bounds, nodeAt));

    public PdnCellRef? CellOfNode(int node) =>
        node >= 0 && node < _cells.Count ? _cells[node] : null;

    public int NodeOnLayer(LayerKey layer, long x, long y)
    {
        if (_byLayer.TryGetValue((layer, x, y), out int n)) return n;

        foreach (var (l, _, bounds, at) in _lookups)
        {
            if (l != layer || !bounds.Contains(x, y)) continue;
            int hit = at(x, y);
            if (hit >= 0) return hit;
        }
        return -1;
    }

    public List<int> NodesAt(long x, long y, bool isReference)
    {
        var hits = new List<int>();
        if (_bySide.TryGetValue((isReference, x, y), out var registered)) hits.AddRange(registered);

        foreach (var (_, isRef, bounds, at) in _lookups)
        {
            if (isRef != isReference || !bounds.Contains(x, y)) continue;
            int hit = at(x, y);
            if (hit >= 0 && !hits.Contains(hit)) hits.Add(hit);
        }
        return hits;
    }

    /// <summary>
    /// The nearest reference node that is a place to attach a point — never a pour cell under
    /// half full (brief-railrf-33). The mesh learned this in brief 32: a cell clipping a corner of
    /// the plane holds a few square microns, and a return forced into it pays a resistance set by
    /// where the grid lines fell. The fast reading's refined cells under a port are exactly that
    /// small, and the field board's source sits over an antipad: attached to the nearest CENTRE,
    /// its return read 0.78 mV against the mesh's 0.11.
    /// </summary>
    public int NearestReferenceNode(long x, long y, out long distanceDbu)
    {
        int best = -1;
        double bestD = double.MaxValue;
        bool any = Enumerable.Range(0, _cells.Count).Any(n => _cells[n].IsReference && _attachable[n]);

        for (int n = 0; n < _cells.Count; n++)
        {
            if (!_cells[n].IsReference || (any && !_attachable[n])) continue;
            double dx = _cells[n].CentreX - x, dy = _cells[n].CentreY - y;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = n; }
        }

        distanceDbu = best < 0 ? 0 : (long)Math.Round(Math.Sqrt(bestD));
        return best;
    }

    public IEnumerable<int> NodesInNaturalOrder => Enumerable.Range(0, _cells.Count);

    public string? NameOfNode(int node) => node >= 0 && node < _names.Count ? _names[node] : null;
}

// ── the raster, the skeleton, and the sections ─────────────────────────────────────────────────

/// <summary>
/// One trace-shaped piece of copper, read as a chain graph.
///
/// <para><b>Three steps, none of them ours:</b> a scanline rasterisation under the non-zero winding
/// rule, Zhang-Suen thinning (the published 1984 algorithm, unchanged), and a multi-source breadth
/// walk that assigns every copper pixel to its nearest skeleton pixel. What comes out is a
/// centreline of any orientation with a LOCAL WIDTH at every point of it, which is everything
/// <c>R = ∫ ds / (σ·T·W)</c> needs.</para>
///
/// <para><b>The raster is not the model.</b> It finds the topology and measures the width; the
/// netlist that leaves here has one element per SECTION. See this file's header.</para>
/// </summary>
internal sealed class PdnTraceRaster
{
    private readonly long _x0, _y0, _pitch;
    private readonly int _nx, _ny;
    private readonly bool[] _on;
    private readonly bool[] _skel;
    private readonly int[] _skelId;          // pixel → skeleton index, or -1
    private readonly List<int> _skelPixels = [];
    private readonly List<int>[] _adjacency;
    private readonly int[] _owner;           // pixel → skeleton index it belongs to, or -1
    private readonly double[] _assignedDbu;  // skeleton index → copper area, square DBU

    /// <summary>The pixel pitch actually used, DBU — reported, because a coarsened one is a
    /// coarsened answer.</summary>
    public long PitchDbu => _pitch;

    /// <summary>How many pixels of copper the raster found.</summary>
    public int CopperPixels { get; }

    /// <summary>The true polygon area, square DBU.</summary>
    public double TrueAreaSquareDbu { get; }

    public int SkeletonCount => _skelPixels.Count;

    private PdnTraceRaster(
        Paths64 piece, long x0, long y0, long pitch, int nx, int ny, bool[] on, int onCount)
    {
        _x0 = x0; _y0 = y0; _pitch = pitch; _nx = nx; _ny = ny; _on = on;
        CopperPixels = onCount;
        TrueAreaSquareDbu = Math.Abs(Clipper.Area(piece));

        _skel = Thin(on, nx, ny);

        _skelId = new int[on.Length];
        Array.Fill(_skelId, -1);
        for (int k = 0; k < _skel.Length; k++)
            if (_skel[k]) { _skelId[k] = _skelPixels.Count; _skelPixels.Add(k); }

        _adjacency = BuildAdjacency();
        _owner = new int[on.Length];
        _assignedDbu = new double[_skelPixels.Count];
        Assign();
    }

    /// <summary>Rasterises one piece at <paramref name="pitchDbu"/>, coarsening rather than growing
    /// past <paramref name="maxCells"/>.</summary>
    public static PdnTraceRaster Build(Paths64 piece, long pitchDbu, int maxCells, out bool coarsened)
    {
        var b = DrcRegions.BoundsOf(piece);
        coarsened = false;

        long pitch = Math.Max(1, pitchDbu);
        long w = Math.Max(pitch, b.MaxX - b.MinX + 2 * pitch);
        long h = Math.Max(pitch, b.MaxY - b.MinY + 2 * pitch);

        while ((double)(w / pitch + 1) * (h / pitch + 1) > maxCells)
        {
            pitch *= 2;
            coarsened = true;
        }

        long x0 = b.MinX - pitch, y0 = b.MinY - pitch;
        int nx = (int)((b.MaxX - x0) / pitch) + 2;
        int ny = (int)((b.MaxY - y0) / pitch) + 2;

        var on = new bool[nx * ny];
        int onCount = Scanline(piece, x0, y0, pitch, nx, ny, on);

        return new PdnTraceRaster(piece, x0, y0, pitch, nx, ny, on, onCount);
    }

    // ── the raster ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A pixel is copper where its CENTRE is inside the polygon, under the non-zero winding rule —
    /// the same rule <see cref="LayoutClipper.Rule"/> states once for the whole of the layout code,
    /// so a hole here is a hole everywhere else too.
    ///
    /// <para>Centres are taken at half-pixel offsets, which is what keeps a vertex lying exactly on a
    /// row from being counted twice.</para>
    /// </summary>
    internal static int Scanline(
        Paths64 piece, long x0, long y0, long pitch, int nx, int ny, bool[] on)
    {
        int count = 0;
        var crossings = new List<(double X, int Dir)>();

        for (int iy = 0; iy < ny; iy++)
        {
            double yc = y0 + (iy + 0.5) * pitch;
            crossings.Clear();

            foreach (var ring in piece)
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i];
                    var b = ring[(i + 1) % ring.Count];
                    if (a.Y == b.Y) continue;

                    double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                    if (yc < lo || yc >= hi) continue;

                    double t = (yc - a.Y) / (double)(b.Y - a.Y);
                    crossings.Add((a.X + t * (b.X - a.X), b.Y > a.Y ? 1 : -1));
                }
            }

            if (crossings.Count == 0) continue;
            crossings.Sort((p, q) => p.X.CompareTo(q.X));

            int winding = 0;
            for (int c = 0; c + 1 <= crossings.Count - 1; c++)
            {
                winding += crossings[c].Dir;
                if (winding == 0) continue;

                double xa = crossings[c].X, xb = crossings[c + 1].X;

                // The pixels whose centre falls in [xa, xb).
                int ia = (int)Math.Ceiling((xa - x0) / (double)pitch - 0.5);
                int ib = (int)Math.Ceiling((xb - x0) / (double)pitch - 0.5) - 1;
                ia = Math.Max(0, ia);
                ib = Math.Min(nx - 1, ib);

                for (int ix = ia; ix <= ib; ix++)
                {
                    int k = iy * nx + ix;
                    if (on[k]) continue;
                    on[k] = true;
                    count++;
                }
            }
        }

        return count;
    }

    // ── the skeleton ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Zhang-Suen thinning — T. Y. Zhang and C. Y. Suen, <i>A fast parallel algorithm for thinning
    /// digital patterns</i>, CACM 27(3), 1984. Unchanged, and deliberately so: it is external
    /// arithmetic in exactly the sense the PRD's validation rule means, and a hand-rolled thinning
    /// would be one more thing to be wrong about.
    /// </summary>
    private static bool[] Thin(bool[] src, int nx, int ny)
    {
        var img = (bool[])src.Clone();
        var doomed = new List<int>();

        for (int guard = 0; guard < 512; guard++)
        {
            bool changed = false;

            for (int step = 0; step < 2; step++)
            {
                doomed.Clear();

                for (int y = 1; y < ny - 1; y++)
                    for (int x = 1; x < nx - 1; x++)
                    {
                        int k = y * nx + x;
                        if (!img[k]) continue;

                        // P2..P9, clockwise from north.
                        bool p2 = img[k + nx], p3 = img[k + nx + 1], p4 = img[k + 1],
                             p5 = img[k - nx + 1], p6 = img[k - nx], p7 = img[k - nx - 1],
                             p8 = img[k - 1], p9 = img[k + nx - 1];

                        int b = Count(p2) + Count(p3) + Count(p4) + Count(p5)
                              + Count(p6) + Count(p7) + Count(p8) + Count(p9);
                        if (b < 2 || b > 6) continue;

                        int a = Trans(p2, p3) + Trans(p3, p4) + Trans(p4, p5) + Trans(p5, p6)
                              + Trans(p6, p7) + Trans(p7, p8) + Trans(p8, p9) + Trans(p9, p2);
                        if (a != 1) continue;

                        bool ok = step == 0
                            ? (!p2 || !p4 || !p6) && (!p4 || !p6 || !p8)
                            : (!p2 || !p4 || !p8) && (!p2 || !p6 || !p8);
                        if (!ok) continue;

                        doomed.Add(k);
                    }

                foreach (int k in doomed) img[k] = false;
                changed |= doomed.Count > 0;
            }

            if (!changed) break;
        }

        return img;

        static int Count(bool v) => v ? 1 : 0;
        static int Trans(bool p, bool q) => !p && q ? 1 : 0;
    }

    /// <summary>
    /// Eight-connected, <b>with the diagonal suppressed where both of its legs are present</b>.
    ///
    /// <para>Without that, a staircase of three pixels is a triangle in which every pixel has degree
    /// two — a cycle rather than a chain — and a section walk around it never terminates. Suppressing
    /// the diagonal makes a staircase an ordinary chain and changes no topology anywhere else.</para>
    /// </summary>
    private List<int>[] BuildAdjacency()
    {
        var adj = new List<int>[_skelPixels.Count];
        for (int i = 0; i < adj.Length; i++) adj[i] = [];

        for (int i = 0; i < _skelPixels.Count; i++)
        {
            int k = _skelPixels[i];
            int x = k % _nx, y = k / _nx;

            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nxp = x + dx, nyp = y + dy;
                    if (nxp < 0 || nyp < 0 || nxp >= _nx || nyp >= _ny) continue;

                    int kk = nyp * _nx + nxp;
                    if (_skelId[kk] < 0) continue;

                    if (dx != 0 && dy != 0)
                    {
                        bool legX = _skelId[y * _nx + nxp] >= 0;
                        bool legY = _skelId[nyp * _nx + x] >= 0;
                        if (legX || legY) continue;
                    }

                    adj[i].Add(_skelId[kk]);
                }
        }

        return adj;
    }

    /// <summary>
    /// Every copper pixel to its nearest skeleton pixel, by a breadth walk from all of them at once.
    /// The count each skeleton pixel collects IS its local width times its own step — which is why
    /// there is no distance transform here and no width sampled at a point.
    /// </summary>
    private void Assign()
    {
        Array.Fill(_owner, -1);
        var queue = new Queue<int>();

        foreach (int i in Enumerable.Range(0, _skelPixels.Count))
        {
            _owner[_skelPixels[i]] = i;
            queue.Enqueue(_skelPixels[i]);
        }

        while (queue.Count > 0)
        {
            int k = queue.Dequeue();
            int owner = _owner[k];
            int x = k % _nx, y = k / _nx;

            void Visit(int xx, int yy)
            {
                if (xx < 0 || yy < 0 || xx >= _nx || yy >= _ny) return;
                int kk = yy * _nx + xx;
                if (!_on[kk] || _owner[kk] >= 0) return;
                _owner[kk] = owner;
                queue.Enqueue(kk);
            }

            Visit(x + 1, y); Visit(x - 1, y); Visit(x, y + 1); Visit(x, y - 1);
        }

        double pixel = (double)_pitch * _pitch;
        for (int k = 0; k < _on.Length; k++)
            if (_on[k] && _owner[k] >= 0) _assignedDbu[_owner[k]] += pixel;

        // The one bias a raster has is its boundary — a 0.3 mm trace is not an exact number of
        // pixels wide. Scaling the assigned areas so they sum to the polygon's TRUE area removes the
        // whole of it in one multiply, and is what lets a coarse raster reproduce ρ·L/(W·T).
        double rasterArea = CopperPixels * pixel;
        if (rasterArea > 0 && TrueAreaSquareDbu > 0)
        {
            double scale = TrueAreaSquareDbu / rasterArea;
            for (int i = 0; i < _assignedDbu.Length; i++) _assignedDbu[i] *= scale;
        }
    }

    // ── reading it ─────────────────────────────────────────────────────────────────────────────

    public IReadOnlyList<int> Neighbours(int skel) => _adjacency[skel];

    public int Degree(int skel) => _adjacency[skel].Count;

    public double AssignedAreaSquareDbu(int skel) => _assignedDbu[skel];

    public (int Ix, int Iy) PixelOf(int skel)
    {
        int k = _skelPixels[skel];
        return (k % _nx, k / _nx);
    }

    public (long X, long Y) CentreOf(int skel)
    {
        var (ix, iy) = PixelOf(skel);
        return (_x0 + ix * _pitch + _pitch / 2, _y0 + iy * _pitch + _pitch / 2);
    }

    /// <summary>The distance between two adjacent skeleton pixels, DBU.</summary>
    public double StepDbu(int a, int b)
    {
        var (ax, ay) = PixelOf(a);
        var (bx, by) = PixelOf(b);
        int dx = Math.Abs(ax - bx), dy = Math.Abs(ay - by);
        return (dx != 0 && dy != 0) ? _pitch * Math.Sqrt(2.0) : _pitch;
    }

    /// <summary>The skeleton pixel a board coordinate belongs to, or -1 where the point is not on
    /// this piece's copper.</summary>
    public int SkeletonAt(long x, long y)
    {
        int ix = (int)Math.Floor((x - _x0) / (double)_pitch);
        int iy = (int)Math.Floor((y - _y0) / (double)_pitch);
        if (ix < 0 || iy < 0 || ix >= _nx || iy >= _ny) return -1;

        int k = iy * _nx + ix;
        if (_on[k]) return _owner[k];

        // A pad's coordinate can land in the hole its own drill made, or one pixel outside a
        // boundary the raster rounded. Look at the eight neighbours before giving up — never
        // further, because a point that is genuinely off this copper must read as off it.
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = ix + dx, yy = iy + dy;
                if (xx < 0 || yy < 0 || xx >= _nx || yy >= _ny) continue;
                int kk = yy * _nx + xx;
                if (_on[kk]) return _owner[kk];
            }

        return -1;
    }

    /// <summary>Deletes skeleton pixels — how a thinning artifact is pruned. The adjacency and the
    /// assignment are rebuilt by the caller through <see cref="Rebuild"/>.</summary>
    public void Delete(IEnumerable<int> skeletons)
    {
        foreach (int s in skeletons) _skel[_skelPixels[s]] = false;
    }

    /// <summary>Re-derives everything downstream of the skeleton after a prune.</summary>
    public PdnTraceRaster Rebuild(Paths64 piece) => new(this, piece);

    private PdnTraceRaster(PdnTraceRaster from, Paths64 piece)
    {
        _x0 = from._x0; _y0 = from._y0; _pitch = from._pitch;
        _nx = from._nx; _ny = from._ny; _on = from._on;
        CopperPixels = from.CopperPixels;
        TrueAreaSquareDbu = from.TrueAreaSquareDbu;

        _skel = (bool[])from._skel.Clone();
        _skelId = new int[_on.Length];
        Array.Fill(_skelId, -1);
        for (int k = 0; k < _skel.Length; k++)
            if (_skel[k]) { _skelId[k] = _skelPixels.Count; _skelPixels.Add(k); }

        _adjacency = BuildAdjacency();
        _owner = new int[_on.Length];
        _assignedDbu = new double[_skelPixels.Count];
        Assign();
        _ = piece;
    }
}

// ── turning the classification into nodes and elements ─────────────────────────────────────────

/// <summary>
/// Walks the classified pieces and produces the graph: sections for the trace-shaped ones, a coarse
/// mesh for the rest, a node for every pad and every via land.
///
/// <para><b>It stages copper and nothing else.</b> The vias, the ground choice, the series parts,
/// the shunts, the sources and the load ports are <see cref="PdnAssembly"/>'s, shared with the mesh
/// reading — see that file's header for why a second copy of them would be the defect.</para>
/// </summary>
internal sealed class GraphBuild(
    PdnExtractionRequest request,
    double frequencyHz,
    PdnGraphNodes nodes,
    IReadOnlyDictionary<LayerKey, PdnConductor> conductors,
    List<PdnClassification> classification,
    IReadOnlyList<(long X, long Y)> attachments,
    List<string> notes,
    List<string> diagnostics)
{
    private readonly List<Element> _copper = [];
    private readonly List<int> _traceEdgeA = [];
    private readonly List<int> _traceEdgeB = [];
    // Every rail node inside a SPREADING piece, and which piece it is in (its classification
    // index) — R-rail29-1: the refusal has to know which region a node belongs to in order to say
    // which region is on the path, rather than naming the largest one on the board.
    private readonly Dictionary<int, int> _spreadingPieceOf = [];
    private readonly Dictionary<int, List<int>> _spreadingPieceNodes = [];
    private readonly Dictionary<(long X, long Y), List<int>> _railNodesAtPoint = [];
    private int _merged;
    private int _coarsened;

    // ── brief-railrf-33: where current enters and leaves the copper, and at what size ──────────
    //
    // The points current is injected at — every pad of every source and load; of every series
    // part, on the rail's own copper only (it bridges the rail to itself); and of every shunt part
    // only above DC, where a capacitor carries current at all. A spreading piece is refined around
    // each one that lands on it, and around nothing else: at DC a decoupling capacitor's pads on
    // the example board's return plane were half of 19,489 cells refining where no current flows.
    private readonly List<(long X, long Y, bool RailOnly)> _entries = [];

    // The size of the cell a port lands in on the MESH (its base pitch over its port refinement
    // ratio), and the mesh's base pitch. A point contact's spreading resistance grows as the log of
    // the cell it is smeared over, so the two readings describe the same contact only when they
    // land it in the same size of cell; and a point is on a piece of copper only when copper lies
    // within one mesh cell of it, which is where the mesh would attach it too. Zero where nothing
    // measured the rail's narrowest copper, and then the coarse mesh is left as it was.
    private long _contactDbu;
    private long _attachToleranceDbu;
    private string? _refusal;

    private readonly record struct Element(
        string Path, int A, int B, double Ohms, string Description,
        PdnCellRef From, PdnCellRef To, PdnOriginKind Kind, double LengthM, double WidthM,
        double InductanceH);

    /// <summary>How many raster steps a section's own length is resolved into, at worst. Thirty-two,
    /// which is where the half-step the two ends of a chain give away stops mattering against the
    /// 1 % the closed-form gate asks for.</summary>
    private const int MinimumStepsAlongASection = 32;

    // ── R-rail13-7: §2.9's "and, above DC, one loop inductance" ───────────────────────────────
    //
    // §4.6: "Nothing new, and deliberately so … It produces the same kind of netlist as §4.1, so
    // the solver, the result model, the tables, the plots and the exports are identical and only
    // the extractor differs. THAT IS THE REASON THE FAST PATH IS SAFE TO HAVE AT ALL."
    //
    // A section inductance is a much cruder approximation than a section resistance — that is the
    // point at which the fast model could quietly become dishonest — so it is priced by the SAME
    // law the mesh uses, µ₀·h per square, halved because the reference conductor carries the other
    // half (PdnMeshExtractor.StampMesh says why). A section of ℓ/W squares over a plane at h is
    // µ₀·h·ℓ/W of loop, which is the parallel-plate reading of a trace and is exactly what the mesh
    // converges to on the same copper. The agreement gate is that, rather than a coincidence.

    private readonly Dictionary<LayerKey, double> _separation = [];
    private double _dominantSeparation;
    private double _halfLoop;

    /// <summary>§4.1's <c>h</c> per conductor, read once — the same walk
    /// <see cref="PdnMeshExtractor"/> makes, through the same file, so the two readings cannot come
    /// to disagree about how far apart this board's planes are.</summary>
    private void ResolveSeparations()
    {
        var z = PdnStackupGeometry.Conductors(request.Technology, request.DbuPerMicron);
        var reference = PdnStackupGeometry.ConductorOf(z, request.Rail.ReferenceLayer ?? default);

        foreach (var c in classification)
        {
            if (c.IsReference || _separation.ContainsKey(c.Region.Layer)) continue;
            double h = PdnStackupGeometry.SeparationMetres(
                PdnStackupGeometry.ConductorOf(z, c.Region.Layer), reference);
            _separation[c.Region.Layer] = h;
            if (h > 0 && (_dominantSeparation == 0 || h < _dominantSeparation))
                _dominantSeparation = h;
        }
    }

    /// <summary>Half of §4.1's per-square loop inductance for the conductor this region is on, or
    /// zero at DC — where §4.1's inductance vanishes and every element is a plain resistance.</summary>
    private double HalfLoopPerSquareOf(PdnClassification c)
    {
        if (!(frequencyHz > 0)) return 0.0;

        double h = c.IsReference
            ? _dominantSeparation
            : _separation.TryGetValue(c.Region.Layer, out double v) ? v : _dominantSeparation;

        return PdnInductance.SquareInductanceHenries(h) / 2.0;
    }

    /// <summary>The ports' pads, and the sizes <see cref="Pour"/> refines to — see the fields.</summary>
    private void ResolveEntries()
    {
        var rail = request.Rail;
        var seen = new HashSet<(long, long, bool)>();
        void Add(RailPortAnchor anchor, bool railOnly = false)
        {
            foreach (var (x, y) in PdnAttachments.Resolve(anchor, request.Pads))
                if (seen.Add((x, y, railOnly))) _entries.Add((x, y, railOnly));
        }

        foreach (var s in rail.Sources) Add(s.Anchor);
        foreach (var l in rail.Loads) Add(l.Anchor);
        foreach (var p in request.SeriesElements) { Add(p.A, railOnly: true); Add(p.B, railOnly: true); }
        if (frequencyHz > 0)
            foreach (var p in request.ShuntParts) Add(p.Anchor);

        long narrowest = classification
            .Where(c => !c.IsReference && c.MinimumFeatureDbu > 0)
            .Select(c => c.MinimumFeatureDbu)
            .DefaultIfEmpty(0)
            .Min();
        if (narrowest <= 0) return;

        _attachToleranceDbu = Math.Max(1, narrowest / Math.Max(1, request.Mesh.CellsAcrossMinimumFeature));
        _contactDbu = Math.Max(1, _attachToleranceDbu / Math.Max(1, request.Mesh.PortRefinementRatio));
    }

    public long FinestRasterPitchDbu { get; private set; } = long.MaxValue;
    public long CoarsestPourPitchDbu { get; private set; }
    public double AccountedAreaSquareDbu { get; private set; }

    public string? Build()
    {
        double dbuPerMetre = request.DbuPerMicron * 1e6;
        int piece = 0;

        ResolveSeparations();
        ResolveEntries();

        foreach (var c in classification.ToList())
        {
            // ONE PIECE IS ONE UNIT OF WORK, and this is the only loop in the extraction where that
            // is true — each iteration below rasterises a piece and thins it, and on a real board
            // that is most of the wall clock (field report, 2026-09-22). TickStage checks the token
            // as well as counting, which is RunControl's own contract, so a cancel is answered
            // within one piece rather than at the end of the board.
            request.Control?.TickStage();

            int index = classification.IndexOf(c);
            if (!conductors.TryGetValue(c.Region.Layer, out var conductor)) { piece++; continue; }

            // R-rail13-2: below two skin depths this IS σ·T, so the fast model's whole R matrix is
            // the DC one over most of this band — the same property, from the same one expression,
            // as the mesh's.
            double sigmaT = PdnInductance.SheetSiemensPerSquare(
                frequencyHz, conductor.ThicknessMetres, conductor.ConductivitySm);
            if (!(sigmaT > 0)) { piece++; continue; }

            _halfLoop = HalfLoopPerSquareOf(c);

            if (c.Class == PdnCopperClass.Trace)
                classification[index] = Trace(c, piece, conductor, sigmaT, dbuPerMetre);
            else
                classification[index] = Pour(c, piece, index, conductor, sigmaT, dbuPerMetre);

            piece++;
            if (_refusal is not null) break;
        }

        if (_refusal is not null) return _refusal;
        if (FinestRasterPitchDbu == long.MaxValue) FinestRasterPitchDbu = 0;

        if (_merged > 0)
            notes.Add(
                $"{_merged} pair(s) of branch points sat closer together than the copper is wide and " +
                "were read as ONE junction — six vias in one land pad are one layer transition, not " +
                "six. Each merge gives away under a square of copper, which is a bounded term and is " +
                "counted here.");

        if (_coarsened > 0)
            diagnostics.Add(
                $"{_coarsened} region(s) were rasterised or meshed coarser than asked for, because " +
                "the cell ceiling would otherwise have been exceeded. A fast model that quietly grew " +
                "to ten thousand cells would no longer be the fast model.");

        int pours = classification.Count(x => x.Class == PdnCopperClass.Spreading);
        if (pours > 0)
            notes.Add(
                $"{pours} region(s) were read as SPREADING copper and meshed coarsely at up to " +
                $"{CoarsestPourPitchDbu / dbuPerMetre * 1e3:0.###} mm, which is what §2.9 prescribes " +
                "for copper the closed form cannot price. The spreading inside them is resolved only " +
                "to that pitch; Accuracy is what says how much that is worth on this board.");

        return null;
    }

    /// <summary>Hands every staged element to the shared assembly, in the order they were found.</summary>
    public void StageCopper(PdnAssembly asm)
    {
        foreach (var e in _copper)
            asm.StageCopper(e.Path, e.A, e.B, e.Ohms, e.Description, e.From, e.To,
                            e.Kind, e.LengthM, e.WidthM,
                            e.InductanceH > 0 ? e.InductanceH : null);
    }

    // ── R-rail4-5: Fast REFUSES a pour-dominated path rather than answering it ──────────────────

    /// <summary>
    /// The refusal for a rail whose source cannot reach a load without crossing copper the fast
    /// model classified as spreading — or null.
    ///
    /// <para><b>"Rather than differ" is the requirement.</b> §7: "on a trace-dominated DC path the two
    /// must agree to 5 %; on a POUR-DOMINATED one the fast model must REFUSE rather than differ." A
    /// fast model that produced a slightly different number on a pour has failed that gate just as
    /// surely as one that produced a wildly different one, because the error there is unbounded and
    /// it is optimistic.</para>
    ///
    /// <para><b>The test is on the RAIL's own copper only.</b> The reference return is a plane on
    /// every real board and is always spreading, so a rule that included it would refuse every board
    /// and §2.9's "meshed, and coarsely" would have no meaning. What the coarse reference costs is
    /// stated as a note and measured by rule 4, on the user's own design.</para>
    /// </summary>
    public string? PourDominatedRefusal()
    {
        var rail = request.Rail;
        if (rail.Sources.Count == 0 || rail.Loads.Count == 0) return null;

        List<int> AnchorNodes(RailPortAnchor anchor) => PdnAttachments.RailNodes(nodes, anchor, request.Pads, request.Rail.ReferenceLayer);

        var sourceNodes = rail.Sources.SelectMany(s => AnchorNodes(s.Anchor)).ToList();
        var loadNodes = rail.Loads.Select(l => AnchorNodes(l.Anchor)).ToList();
        if (sourceNodes.Count == 0) return null;

        // ── R-rail29-1: A TERMINAL'S OWN LANDING PIECE IS NOT ON THE PATH, IT IS AN END OF IT ──
        //
        // A spreading piece that lands exactly ONE terminal — one source row or one load row — is
        // where current enters or leaves the rail, by definition, and it joins the union below. It
        // is still priced: Pour() has already meshed it, coarsely, like every spreading piece. The
        // term the closed form omits there is the constriction at a port, and that is the term
        // PdnCopperClassifier.TraceSquaresThreshold already budgets about one square for at each
        // end of every section. What the refusal exists for is spreading copper current has to
        // CROSS between two terminals, and a piece holding two of them is exactly that — the §7
        // pour gate's own fixture is one pour with the source and the load both on it, and it is
        // still refused. Before this, a source on a compact connector land was refused on every
        // board, whatever the rest of the rail was, and the land was the region named.
        //
        // A SERIES ELEMENT'S two ends are terminals in exactly this sense — current leaves the rail's
        // copper into the part at one and comes back at the other — so each lands its piece the way
        // a source does. This was masked while a pad attached to every layer at its XY: a series
        // pad over the rail's own trace on the far layer was joined straight to it, and the pad's
        // land was never on the path. Attached to its own land, a 1206 jumper's pad was named as
        // the spreading copper the rail "only" reaches its load through.
        var terminalNodes = rail.Sources.Select(s => AnchorNodes(s.Anchor).ToHashSet())
            .Concat(loadNodes.Select(n => n.ToHashSet()))
            .Concat(request.SeriesElements
                .SelectMany(p => new[] { p.A, p.B })
                .Select(a => AnchorNodes(a).ToHashSet()))
            .ToList();

        var landing = new HashSet<int>();
        foreach (var (index, members) in _spreadingPieceNodes)
            if (terminalNodes.Count(t => members.Any(t.Contains)) == 1) landing.Add(index);

        var joined = Connect(landing.Contains);

        for (int k = 0; k < rail.Loads.Count; k++)
        {
            var load = rail.Loads[k];
            if (loadNodes[k].Count == 0) continue;
            if (Reaches(joined, sourceNodes, loadNodes[k])) continue;

            var fmt = request.LengthFormat;

            // ── ASK WHICH OF THE TWO CASES THIS IS BEFORE BLAMING A CLASS (R-rail29-1) ────────
            //
            // With every spreading piece joined as well, does the source reach the load at all?
            // PdnRailConnectivity has already refused separate islands, and islands joined only
            // through the rail's copper on its own reference layer, before anything was priced — so
            // a "no" here is a disagreement between this graph and the region walk, not a fact
            // about the board. No class override can answer it, and naming a region would send the
            // user to one.
            var all = Connect(_ => true);
            if (!Reaches(all, sourceNodes, loadNodes[k]))
                return
                    $"Rail '{rail.Name}' does not reach {load.Anchor.Describe(fmt)} from its source " +
                    "through any copper the fast model read, even counting every spreading region as " +
                    "joined — so no class override can answer it. The region walk does join them, so " +
                    "what joins them is copper this reading does not carry as the rail's own. That is " +
                    "a fault in the fast reading rather than in the board; Accuracy reads the copper " +
                    "differently and may answer.";

            // ── only now is spreading the cause, and the region named must be ON the path ──────
            //
            // A CUT: a spreading piece without which the source no longer reaches the load. The
            // largest cut is named — not the largest spreading region on the whole rail, which on
            // the reported board was the source's own connector land and lay on no path at all.
            // A path that needs two spreading pieces in parallel has no single cut; then the
            // largest non-landing piece on the source's side of the copper is the honest pointer.
            var onPath = BestSpreading(i =>
                !landing.Contains(i) &&
                !Reaches(Connect(p => p != i), sourceNodes, loadNodes[k]));
            bool isCut = onPath is not null;
            onPath ??= BestSpreading(i =>
                !landing.Contains(i) &&
                _spreadingPieceNodes[i].Any(n => Reaches(all, sourceNodes, [n])));

            // IN THE BOARD'S OWN UNIT, both the size and the vertex — this row used to read a
            // millimetre size against a DBU vertex, two units in one clause, on a board that reads
            // in neither (owner, 2026-09-20).
            string where = onPath is null
                ? "copper the fast model classified as spreading"
                : $"a {fmt.Length(onPath.Bounds.MaxX - onPath.Bounds.MinX)} × " +
                  $"{fmt.Length(onPath.Bounds.MaxY - onPath.Bounds.MinY)} region on " +
                  $"{onPath.Region.Describe(fmt)}";

            return
                $"Rail '{request.Rail.Name}' reaches {load.Anchor.Describe(fmt)} " +
                (isCut ? "only through " : "only through spreading copper, including ") +
                $"{where}, which the fast model read as spreading rather than as a trace. The " +
                "closed form has no bounded error across copper the current fans out in, and the " +
                "error it would make is OPTIMISTIC — so the fast model produces no number here " +
                "rather than a smaller one. Run Accuracy, which meshes it; or, if you know the " +
                "current on this board follows a path across that region, force it to 'trace' on " +
                "the class tab and the fast reading will price it as one." +
                (onPath is null ? "" : $" What was measured: {onPath.Reason}");
        }

        return null;
    }

    /// <summary>The rail's connectivity at DC with the spreading pieces <paramref name="joins"/>
    /// accepts counted as copper, and every other spreading piece left out — the union-find root of
    /// each node.</summary>
    private int[] Connect(Func<int, bool> joins)
    {
        var root = new int[nodes.NodeTotal];
        for (int i = 0; i < root.Length; i++) root[i] = i;

        int Find(int x) { while (root[x] != x) { root[x] = root[root[x]]; x = root[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) root[Math.Max(ra, rb)] = Math.Min(ra, rb); }
        bool Counted(int n) => !_spreadingPieceOf.TryGetValue(n, out int piece) || joins(piece);

        for (int i = 0; i < _traceEdgeA.Count; i++)
            if (Counted(_traceEdgeA[i]) && Counted(_traceEdgeB[i]))
                Union(_traceEdgeA[i], _traceEdgeB[i]);

        // A spreading piece is ONE connected piece of copper by construction (the classifier cuts
        // on DrcRegions.Components), so a counted one joins all of its own nodes.
        foreach (var (piece, members) in _spreadingPieceNodes)
            if (joins(piece))
                for (int i = 1; i < members.Count; i++) Union(members[0], members[i]);

        // A layer transition joins the rail's copper above and below whatever the class of either
        // piece is. Read from the via COORDINATES rather than re-deriving the stackup's spans, which
        // PdnAssembly already owns; being permissive here can only ever make a refusal LESS likely,
        // and the connectivity it stands in for is the region walk's own.
        foreach (var via in request.Shapes.OfType<ViaShape>())
        {
            if (!_railNodesAtPoint.TryGetValue((via.X, via.Y), out var at) || at.Count < 2) continue;
            for (int i = 1; i < at.Count; i++)
                if (Counted(at[0]) && Counted(at[i])) Union(at[0], at[i]);
        }

        // A SERIES PART bridges two pieces of the rail's copper exactly as a via bridges two
        // conductors, and on imported artwork it is the ORDINARY case rather than an unusual one —
        // §2.8: "the copper stops at every pad, so the board is not electrically continuous until
        // the user has said what bridges each gap". Without this, the design note's own §2.8 board
        // — a battery, a protection FET, and 50 mm of inner copper — is refused as pour-dominated
        // when no pour is involved at all, and so is every real board with a ferrite on the rail.
        // Permissive for the reason the via loop above states.
        foreach (var part in request.SeriesElements)
        {
            var ends = new List<int>();
            foreach (var anchor in new[] { part.A, part.B })
                ends.AddRange(PdnAttachments.RailNodes(nodes, anchor, request.Pads, request.Rail.ReferenceLayer));

            for (int i = 1; i < ends.Count; i++)
                if (Counted(ends[0]) && Counted(ends[i])) Union(ends[0], ends[i]);
        }

        for (int i = 0; i < root.Length; i++) root[i] = Find(i);
        return root;
    }

    private static bool Reaches(int[] root, IEnumerable<int> from, IReadOnlyCollection<int> to)
    {
        var roots = from.Select(n => root[n]).ToHashSet();
        return to.Any(n => roots.Contains(root[n]));
    }

    /// <summary>The largest rail-side spreading piece <paramref name="accept"/> takes, or null.</summary>
    private PdnClassification? BestSpreading(Func<int, bool> accept) =>
        _spreadingPieceNodes.Keys
            .Where(accept)
            .Select(i => classification[i])
            .OrderByDescending(c => (double)(c.Bounds.MaxX - c.Bounds.MinX) *
                                    (c.Bounds.MaxY - c.Bounds.MinY))
            .FirstOrDefault();

    // ── the trace-shaped pieces (R-rail4-1, R-rail4-6) ─────────────────────────────────────────

    private PdnClassification Trace(
        PdnClassification c, int piece, PdnConductor conductor, double sigmaT, double dbuPerMetre)
    {
        // TWO rules, and the finer of them wins. The width rule is what a real trace needs — a neck
        // that thins to one pixel is a neck that thins to nothing. The LENGTH rule is what a piece
        // FORCED to trace needs, and it is not hypothetical: a 20 × 15 mm pour's narrowest copper is
        // 15 mm, so the width rule alone rasters it eight pixels across and the whole region thins to
        // a single junction worth zero ohms. A forced region must be priced as the closed form along
        // its own length — optimistically, which is the user's stated choice — and not as a short.
        // Measured once, by the classifier, on this same copper (brief-railrf-30).
        long minFeature = c.MinimumFeatureDbu > 0
            ? c.MinimumFeatureDbu
            : PdnMeshExtractor.MinimumFeatureWidthDbu(c.Copper);
        long byWidth = Math.Max(1, minFeature / Math.Max(1, request.Graph.RasterCellsAcrossMinimumFeature));
        long byLength = Math.Max(1, (long)(c.EquivalentLengthMetres * dbuPerMetre / MinimumStepsAlongASection));

        long pitch = request.Graph.RasterPitchMetres is { } stated && stated > 0
            ? Math.Max(1, (long)Math.Round(stated * dbuPerMetre))
            : Math.Max(1, Math.Min(byWidth, byLength));

        var raster = PdnTraceRaster.Build(c.Copper, pitch, request.Graph.MaxRasterCells, out bool coarse);
        if (coarse) _coarsened++;
        FinestRasterPitchDbu = Math.Min(FinestRasterPitchDbu, raster.PitchDbu);
        AccountedAreaSquareDbu += raster.TrueAreaSquareDbu;

        var mine = attachments.Where(p => c.Bounds.Contains(p.X, p.Y)).ToList();
        raster = PruneSpurs(raster, c, mine, dbuPerMetre);

        // Where each attachment sits, and WHICH skeleton pixel it hangs off.
        //
        // The coordinate matters, not just the pixel: a thinned skeleton stops about half a width
        // short of the copper's end, and on a ribbon that is a fraction of a percent — but on a
        // region FORCED to trace it is most of the answer. The medial axis of a 20 × 15 mm rectangle
        // is a 4 mm spine; reading a section between its two ends alone prices 300 mm² of copper as a
        // dead short, which is the one number this model must never report. So the section's arc runs
        // from the PAD to the pixel, and the copper that pixel owns is the copper it runs through.
        var attachAt = new Dictionary<int, (long X, long Y)>();
        foreach (var (x, y) in mine)
        {
            int s = raster.SkeletonAt(x, y);
            if (s >= 0) attachAt.TryAdd(s, (x, y));
        }

        var protectedPixels = new HashSet<int>(attachAt.Keys);

        int count = raster.SkeletonCount;
        if (count == 0)
            return c with { Reason = c.Reason + " It thinned to nothing and carries no sections." };

        // ── nodes: a branch point, a leaf, or an attachment ────────────────────────────────────
        var isNodePixel = new bool[count];
        for (int s = 0; s < count; s++)
            isNodePixel[s] = raster.Degree(s) != 2 || protectedPixels.Contains(s);

        var blob = new int[count];
        for (int s = 0; s < count; s++) blob[s] = s;
        int FindBlob(int x) { while (blob[x] != x) { blob[x] = blob[blob[x]]; x = blob[x]; } return x; }
        void UnionBlob(int a, int b) { int ra = FindBlob(a), rb = FindBlob(b); if (ra != rb) blob[Math.Max(ra, rb)] = Math.Min(ra, rb); }

        for (int s = 0; s < count; s++)
        {
            if (!isNodePixel[s]) continue;
            foreach (int t in raster.Neighbours(s))
                if (isNodePixel[t]) UnionBlob(s, t);
        }

        // ── sections: the degree-two chains between them ───────────────────────────────────────
        var chains = new List<List<int>>();
        var used = new bool[count];

        for (int s = 0; s < count; s++)
        {
            if (!isNodePixel[s]) continue;

            foreach (int t in raster.Neighbours(s))
            {
                if (isNodePixel[t] || used[t]) continue;

                var chain = new List<int> { s };
                int prev = s, cur = t, guard = 0;

                while (!isNodePixel[cur] && guard++ <= count)
                {
                    used[cur] = true;
                    chain.Add(cur);

                    int next = -1;
                    foreach (int n in raster.Neighbours(cur)) if (n != prev) { next = n; break; }
                    if (next < 0) break;
                    prev = cur;
                    cur = next;
                }

                if (isNodePixel[cur]) chain.Add(cur);
                chains.Add(chain);
            }
        }

        // ── §2.9's "each pad a node": branch points closer than the copper is wide are ONE ─────
        //
        // Measured on the two ends' own POSITIONS and not on the chain between them, which is the
        // rule stated literally. Six vias in one land pad sit under a millimetre apart on copper two
        // millimetres wide — one layer transition, six parallel barrels — and the centreline that
        // joins them wanders the length of the pad, so a chain-length test would keep them apart. The
        // same test keeps a 20 × 15 mm region FORCED to trace as one section rather than collapsing
        // it to a short: its two ports are 23 mm apart on copper 14 mm wide, which is not one
        // junction by any reading.
        (long X, long Y) Position(int skel) =>
            attachAt.TryGetValue(skel, out var at) ? at : raster.CentreOf(skel);

        var measured = new List<(List<int> Chain, double Ohms, double LengthM, double WidthM, double Squares)>();

        foreach (var chain in chains)
        {
            var m = SectionResistance(raster, chain, attachAt, sigmaT, dbuPerMetre);
            measured.Add((chain, m.Ohms, m.LengthM, m.WidthM, m.Squares));

            var pa = Position(chain[0]);
            var pb = Position(chain[^1]);
            double apart = Math.Sqrt((double)(pa.X - pb.X) * (pa.X - pb.X)
                                   + (double)(pa.Y - pb.Y) * (pa.Y - pb.Y)) / dbuPerMetre;

            if (m.WidthM > 0 && apart / m.WidthM < request.Graph.JunctionMergeSquares &&
                isNodePixel[chain[0]] && isNodePixel[chain[^1]] &&
                FindBlob(chain[0]) != FindBlob(chain[^1]))
            {
                UnionBlob(chain[0], chain[^1]);
                _merged++;
            }
        }

        // ── hand out one node per blob, and map every pixel onto the nearest of them ───────────
        var nodeOfBlob = new Dictionary<int, int>();
        string side = c.IsReference ? "ref" : "rail";

        for (int s = 0; s < count; s++)
        {
            if (!isNodePixel[s]) continue;
            int root = FindBlob(s);
            if (nodeOfBlob.ContainsKey(root)) continue;

            var (ix, iy) = raster.PixelOf(root);
            var (cx, cy) = raster.CentreOf(root);

            // A node that carries a pad is drawn AT the pad — briefs 8 and 15 colour the thing a
            // reader points at, and a pixel of a thinned centreline is not it.
            foreach (int member in Enumerable.Range(0, count))
                if (FindBlob(member) == root && attachAt.TryGetValue(member, out var at))
                { (cx, cy) = at; break; }

            nodeOfBlob[root] = nodes.New(
                c.Region.Layer, c.IsReference, ix, iy, cx, cy,
                $"{side}.{c.Region.Layer.Layer}_{c.Region.Layer.Datatype}.p{piece}.{ix}.{iy}");
        }

        var nodeOfPixel = new int[count];
        Array.Fill(nodeOfPixel, -1);
        for (int s = 0; s < count; s++)
            if (isNodePixel[s]) nodeOfPixel[s] = nodeOfBlob[FindBlob(s)];

        foreach (var (chain, _, _, _, _) in measured)
        {
            int a = nodeOfPixel[chain[0]], b = nodeOfPixel[chain[^1]];
            if (a < 0 || b < 0) continue;
            for (int i = 1; i < chain.Count - 1; i++)
                nodeOfPixel[chain[i]] = i * 2 <= chain.Count ? a : b;
        }

        // ── the elements ───────────────────────────────────────────────────────────────────────
        int emitted = 0;
        for (int k = 0; k < measured.Count; k++)
        {
            var (chain, ohms, lengthM, widthM, _) = measured[k];
            int a = nodeOfPixel[chain[0]], b = nodeOfPixel[chain[^1]];
            if (a < 0 || b < 0 || a == b) continue;

            _copper.Add(new Element(
                $"trace.{side}.{c.Region.Layer.Layer}_{c.Region.Layer.Datatype}.p{piece}.{k}",
                a, b, ohms,
                $"{lengthM * 1e3:0.###} mm of {widthM * 1e3:0.###} mm {conductor.StackupName} copper, " +
                $"priced as one section ({lengthM / Math.Max(widthM, 1e-12):0.#} squares)",
                nodes.CellOfNode(a)!.Value, nodes.CellOfNode(b)!.Value,
                PdnOriginKind.TraceSection, lengthM, widthM,
                // The section's OWN square count — SectionResistance integrates W along the run
                // rather than sampling it, and using its number here is what keeps a tapered
                // section's L and its R the same reading of the same copper.
                _halfLoop * measured[k].Squares));

            _traceEdgeA.Add(a);
            _traceEdgeB.Add(b);
            emitted++;
        }

        Register(raster, nodeOfPixel, c, mine);
        nodes.AddLookup(c.Region.Layer, c.IsReference, c.Bounds, (x, y) =>
        {
            int s = raster.SkeletonAt(x, y);
            return s >= 0 && s < nodeOfPixel.Length ? nodeOfPixel[s] : -1;
        });

        return c with
        {
            Reason = c.Reason +
                $" Read as {emitted} section(s) between {nodeOfBlob.Count} junction(s) at " +
                $"{raster.PitchDbu / dbuPerMetre * 1e3:0.####} mm of raster.",
        };
    }

    /// <summary>
    /// <b>R-rail4-6 failure shape 2, and the reason this is not <c>L / W_mean</c>.</b>
    ///
    /// <para>A taper read at its wide end is optimistic; at its narrow end, pessimistic; and at its
    /// mean width it is neither of those and still wrong, because resistance integrates
    /// <c>1/W</c> and not <c>W</c>. So the sum below is over the section:
    /// <c>R = Σ ℓ² / (σ·T·A)</c> over consecutive pieces of it, where <c>ℓ</c> is a piece's length
    /// and <c>A</c> the copper that belongs to it — which is <c>Σ ℓ / (σ·T·W)</c> written in the
    /// quantities the raster actually measures, and is the same finite-volume form the mesh uses in
    /// two dimensions.</para>
    ///
    /// <para><b>A piece is a CHORD about two widths long, never one pixel step</b> (brief-railrf-33).
    /// Thinning leaves an off-axis centreline as a four-connected STAIRCASE, so its pixel steps
    /// over-state the length (up to ~8 % at 22.5°), and the copper each pixel owns jitters from step
    /// to step, which a per-step <c>Σ 1/A</c> turns into a further bias — a straight 0.25 mm trace
    /// at 22.5° read 1.50× its closed form and at 30° 1.52×, while 0° and 45° were exact. The field
    /// board's rail was 21 % high for that alone. The chord between pixels two widths apart is
    /// within half a pixel of the true centreline at each end, and two widths of copper average the
    /// jitter away; a taper's width moves by a small fraction over two widths, so 1/W is still
    /// integrated along the run rather than sampled.</para>
    ///
    /// <para>An end that carries a pad runs to the PAD, not to the pixel the skeleton stops at — see
    /// the comment at <c>attachAt</c> in <see cref="Trace"/> for why that is not a detail.</para>
    /// </summary>
    private static (double Ohms, double LengthM, double WidthM, double Squares) SectionResistance(
        PdnTraceRaster raster, List<int> chain,
        IReadOnlyDictionary<int, (long X, long Y)> attachAt, double sigmaT, double dbuPerMetre)
    {
        int n = chain.Count;
        if (n < 2) return (0, 0, 0, 0);

        (double X, double Y) Centre(int i)
        {
            var (cx, cy) = raster.CentreOf(chain[i]);
            return (cx, cy);
        }

        (double X, double Y) Position(int i) =>
            (i == 0 || i == n - 1) && attachAt.TryGetValue(chain[i], out var at) ? (at.X, at.Y) : Centre(i);

        static double Apart((double X, double Y) a, (double X, double Y) b) =>
            Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        // The section's width, only to size the chords: its copper over its pixel-step length. The
        // staircase over-states that length by a few percent, which moves a chord by a pixel or two.
        double areaDbu = 0, stepsDbu = 0;
        for (int i = 0; i < n; i++) areaDbu += raster.AssignedAreaSquareDbu(chain[i]);
        for (int i = 0; i + 1 < n; i++) stepsDbu += raster.StepDbu(chain[i], chain[i + 1]);
        stepsDbu += Apart(Position(0), Centre(0)) + Apart(Position(n - 1), Centre(n - 1));
        double widthDbu = stepsDbu > 0 ? areaDbu / stepsDbu : 0;

        int pixelsPerChord = Math.Max(1, (int)Math.Round(ChordWidths * widthDbu / raster.PitchDbu));
        int chords = Math.Max(1, (int)Math.Round((n - 1) / (double)pixelsPerChord));

        double ohms = 0, length = 0, area = 0;
        int from = 0;
        for (int s = 1; s <= chords; s++)
        {
            int to = s == chords ? n - 1 : (int)Math.Round(s * (n - 1) / (double)chords);
            if (to <= from) continue;

            // The pixels strictly between the two ends are this chord's; a pixel on a boundary
            // between two chords is shared half and half, and the section's own two ends are whole
            // — they own the copper beyond the skeleton's end, which is copper this section runs to.
            double a = 0;
            for (int i = from; i <= to; i++)
            {
                double own = raster.AssignedAreaSquareDbu(chain[i]);
                bool shared = (i == from && i != 0) || (i == to && i != n - 1);
                a += shared ? own / 2 : own;
            }

            double l = Apart(Position(from), Position(to)) / dbuPerMetre;
            double am = a / (dbuPerMetre * dbuPerMetre);
            if (am > 0 && l > 0)
            {
                ohms += l * l / (sigmaT * am);
                length += l;
                area += am;
            }
            from = to;
        }

        double width = length > 0 ? area / length : 0;
        return (ohms, length, width, width > 0 ? length / width : 0);
    }

    /// <summary>How long one chord of <see cref="SectionResistance"/> is, in the section's own
    /// widths — two, which is where the staircase's half-pixel wander at each end stops mattering
    /// against the 1 % the closed-form gate asks for at six pixels across.</summary>
    private const double ChordWidths = 2.0;

    /// <summary>
    /// Removes the short leaves thinning leaves behind at a rectangle's corners — and <b>never one
    /// that carries an attachment</b>, because a pad is a node whatever the skeleton thinks of it.
    /// </summary>
    private PdnTraceRaster PruneSpurs(
        PdnTraceRaster raster, PdnClassification c,
        IReadOnlyList<(long X, long Y)> mine, double dbuPerMetre)
    {
        // TWO bounds, and the smaller wins. A thinning artifact at a rectangle's corner is about half
        // the copper's width long, so the width bound is what identifies it. The LENGTH bound is what
        // stops that rule eating a whole region: on a piece FORCED to trace, a width bound of
        // 1.5 × 15 mm is longer than the region itself and three rounds of pruning leave a single
        // junction worth zero ohms — a short reported as an answer, which is the one failure this
        // model must never make silently.
        double limitDbu = Math.Min(
            request.Graph.SpurPruneWidths * c.EquivalentWidthMetres,
            c.EquivalentLengthMetres / 8.0) * dbuPerMetre;
        if (!(limitDbu > 0)) return raster;

        for (int round = 0; round < 3; round++)
        {
            var keep = new HashSet<int>();
            foreach (var (x, y) in mine)
            {
                int s = raster.SkeletonAt(x, y);
                if (s >= 0) keep.Add(s);
            }

            var doomed = new List<int>();

            for (int s = 0; s < raster.SkeletonCount; s++)
            {
                if (raster.Degree(s) != 1 || keep.Contains(s)) continue;

                var walk = new List<int> { s };
                double arc = 0;
                int prev = s, cur = raster.Neighbours(s)[0];

                while (raster.Degree(cur) == 2 && !keep.Contains(cur) && arc <= limitDbu)
                {
                    arc += raster.StepDbu(prev, cur);
                    walk.Add(cur);
                    int next = -1;
                    foreach (int t in raster.Neighbours(cur)) if (t != prev) { next = t; break; }
                    if (next < 0) break;
                    prev = cur;
                    cur = next;
                }

                if (arc <= limitDbu && !keep.Contains(cur) && raster.Degree(cur) >= 3)
                    doomed.AddRange(walk);
            }

            if (doomed.Count == 0) return raster;

            raster.Delete(doomed);
            raster = raster.Rebuild(c.Copper);
        }

        return raster;
    }

    // ── the spreading pieces: meshed, and coarsely (§2.9) ──────────────────────────────────────

    private PdnClassification Pour(
        PdnClassification c, int piece, int index, PdnConductor conductor, double sigmaT,
        double dbuPerMetre)
    {
        var b = c.Bounds;
        long w = Math.Max(1, b.MaxX - b.MinX), h = Math.Max(1, b.MaxY - b.MinY);
        int across = Math.Max(1, request.Graph.PourCellsAcross);
        long pitch = Math.Max(1, Math.Min(w, h) / across);

        while ((double)(w / pitch + 1) * (h / pitch + 1) > request.Graph.MaxPourCells)
        {
            pitch *= 2;
            _coarsened++;
        }

        // ── brief-railrf-33: refined where current enters and leaves ───────────────────────────
        //
        // A port is a POINT, and the resistance of current spreading out of a point grows as the
        // log of the cell it lands in. At four cells across, the field board's 49 mm return plane
        // put its ports in 12 mm cells and read 0.034 mV against the mesh's 0.114; refining the
        // whole plane uniformly converges only logarithmically (0.072 mV at 32 across). So, around
        // every port pad on the piece, cells start at the size the MESH lands a port in, stay that
        // size over the band the mesh refines, and double outward until they reach the coarse pitch
        // — and the coarse pitch itself is no more than an eighth of the distance between the ports,
        // which is the distance the current crosses. A few dozen lines, not a finer grid.
        //
        // A port over a HOLE in this piece — the field board's source sits over an antipad in its
        // return plane — attaches to the nearest copper instead (PdnAssembly.ReferenceNodesFor),
        // so that is where it is refined; refined at the pad, its fine cells were all empty and
        // the return attached a millimetre away into a cell sixty times the mesh's. And it is
        // refined to the size the MESH has there: the mesh refines a band around the pad, and a
        // point attached outside that band lands in one of its base cells, not a port cell.
        var entries = new List<(long X, long Y, long Contact)>();
        if (_contactDbu > 0 && _contactDbu < pitch)
        {
            long band = Math.Max(1, request.Mesh.PortRefinementMarginCells) * _attachToleranceDbu;
            foreach (var p in _entries)
            {
                if ((p.RailOnly && c.IsReference) || !b.Contains(p.X, p.Y) || !Near(c.Copper, p.X, p.Y, pitch))
                    continue;
                if (Near(c.Copper, p.X, p.Y, _attachToleranceDbu)) { entries.Add((p.X, p.Y, _contactDbu)); continue; }

                var (x, y) = Closest(c.Copper, p.X, p.Y);
                bool inBand = Math.Abs(x - p.X) <= band && Math.Abs(y - p.Y) <= band;
                entries.Add((x, y, inBand ? _contactDbu : _attachToleranceDbu));
            }
        }

        // Around the ports — within half their span of the box they sit in, and only there — no
        // cell is wider than an eighth of the distance the current crosses. Outside it the cells go
        // on doubling to the coarse pitch, so two pads a millimetre apart on a 50 mm plane refine a
        // few millimetres and not the plane. (Capping only the gaps BETWEEN the pads was not enough:
        // two ports on one row have no gap between them across the row, and the fixture read 1.14.)
        long between = long.MaxValue, span = 0;
        if (entries.Count > 1)
        {
            span = Math.Max(entries.Max(p => p.X) - entries.Min(p => p.X),
                            entries.Max(p => p.Y) - entries.Min(p => p.Y));
            between = Math.Max(span / Math.Max(1, request.Graph.PourCellsBetweenPorts), 2 * _contactDbu);
        }
        (long Lo, long Hi) Region(IEnumerable<long> at) => (at.Min() - span / 2, at.Max() + span / 2);

        // With ports on it, the coarse pitch is taken per axis — a quarter of the piece's extent
        // along that axis — because the cells that matter are the ones around the ports, which the
        // grading and the cap above size. Square cells a quarter of a 1 mm return strip's WIDTH
        // along its 40 mm length were 160 columns of copper carrying a uniform current.
        long px = entries.Count > 0 ? Math.Max(pitch, w / across) : pitch;
        long py = entries.Count > 0 ? Math.Max(pitch, h / across) : pitch;
        CoarsestPourPitchDbu = Math.Max(CoarsestPourPitchDbu, Math.Max(px, py));

        long xEnd = b.MinX + ((w / px) + 1) * px, yEnd = b.MinY + ((h / py) + 1) * py;
        int core = Math.Max(1, request.Mesh.PortRefinementRatio) *
                   Math.Max(1, request.Mesh.PortRefinementMarginCells);

        var xs = entries.Count > 0
            ? GradedLines(b.MinX, xEnd, entries.Select(p => (p.X, p.Contact)), core, px,
                          Math.Min(between, px), Region(entries.Select(p => p.X)))
            : UniformLines(b.MinX, xEnd, pitch);
        var ys = entries.Count > 0
            ? GradedLines(b.MinY, yEnd, entries.Select(p => (p.Y, p.Contact)), core, py,
                          Math.Min(between, py), Region(entries.Select(p => p.Y)))
            : UniformLines(b.MinY, yEnd, pitch);

        int nx = xs.Length - 1, ny = ys.Length - 1;
        if (entries.Count > 0 && (double)nx * ny > request.Graph.MaxRefinedPourCells)
        {
            var fmt = request.LengthFormat;
            _refusal =
                $"The {(c.IsReference ? "return" : "rail's")} copper on {c.Region.Describe(fmt)}, " +
                $"{fmt.Length(w)} × {fmt.Length(h)}, has {entries.Count} port pad(s) on it, and " +
                $"meshing each one finely enough to price the current spreading out of it would take " +
                $"{(double)nx * ny:N0} cells, over the fast model's ceiling of " +
                $"{request.Graph.MaxRefinedPourCells:N0}. A coarser cell under a port would read its " +
                "spreading resistance low, so the fast model produces no number here rather than a " +
                "smaller one. Run Accuracy, which meshes it.";
            return c;
        }

        var area = CellAreas(c.Copper, xs, ys, out var meet);

        var node = new int[nx * ny];
        Array.Fill(node, -1);
        string side = c.IsReference ? "ref" : "rail";

        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                if (!(area[k] > 0)) continue;

                long cx = (xs[i] + xs[i + 1]) / 2, cy = (ys[j] + ys[j + 1]) / 2;
                node[k] = nodes.New(
                    c.Region.Layer, c.IsReference, i, j, cx, cy,
                    $"{side}.{c.Region.Layer.Layer}_{c.Region.Layer.Datatype}.p{piece}.{i}.{j}",
                    attachable: Fill(k) >= PdnMeshExtractor.AttachFillFraction);
                AccountedAreaSquareDbu += area[k];

                if (!c.IsReference)
                {
                    _spreadingPieceOf[node[k]] = index;
                    if (!_spreadingPieceNodes.TryGetValue(index, out var mine))
                        _spreadingPieceNodes[index] = mine = [];
                    mine.Add(node[k]);
                }
            }

        double m2 = dbuPerMetre * dbuPerMetre;

        // The same half-cell harmonic form PdnMeshExtractor stamps — a cell's copper over its own
        // length along the edge's axis is its width, and each half of the edge runs half a cell.
        void Edge(int ka, int kb, double da, double db, int i, int j, string axis)
        {
            int a = node[ka], bb = node[kb];
            if (a < 0 || bb < 0) return;

            double a0 = area[ka] / m2, a1 = area[kb] / m2;
            if (!(a0 > 0) || !(a1 > 0)) return;

            double r = da * da / (2 * sigmaT * a0) + db * db / (2 * sigmaT * a1);
            double width = (a0 / da + a1 / db) / 2.0;
            double squares = da * da / (2 * a0) + db * db / (2 * a1);

            _copper.Add(new Element(
                $"pour.{side}.{c.Region.Layer.Layer}_{c.Region.Layer.Datatype}.p{piece}.{i}.{j}.{axis}",
                a, bb, r,
                $"{(da + db) / 2 * 1e3:0.###} mm of {width * 1e3:0.###} mm {conductor.StackupName} copper, " +
                "one cell of a coarse mesh over spreading copper",
                nodes.CellOfNode(a)!.Value, nodes.CellOfNode(bb)!.Value,
                PdnOriginKind.MeshEdge, (da + db) / 2, width, _halfLoop * squares));
        }

        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                double dx = (xs[i + 1] - xs[i]) / dbuPerMetre, dy = (ys[j + 1] - ys[j]) / dbuPerMetre;
                if (i + 1 < nx) Edge(k, k + 1, dx, (xs[i + 2] - xs[i + 1]) / dbuPerMetre, i, j, "x");
                if (j + 1 < ny) Edge(k, k + nx, dy, (ys[j + 2] - ys[j + 1]) / dbuPerMetre, i, j, "y");
            }

        // ── §1 of brief-railrf-33: a point is on THIS piece only where its copper is ───────────
        //
        // The lookup used to answer by cell index alone, so a point anywhere in a cell holding some
        // of this piece's copper was this piece — including a point on an island inside one of its
        // holes, which is a different piece. A pad on the island was tied to the pour around it and
        // the copper between them priced out of the answer. The copper is now asked, to within one
        // cell of the mesh, which is exactly as close as the mesh itself would attach it.
        int At(long x, long y)
        {
            int i = Index(xs, x), j = Index(ys, y);
            if (i < 0 || j < 0) return -1;

            int k = j * nx + i;
            if (_attachToleranceDbu <= 0) return Settle(k);
            if (node[k] >= 0 && Near(meet[k]!, x, y, _attachToleranceDbu)) return Settle(k);

            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int ii = i + di, jj = j + dj;
                    if ((di == 0 && dj == 0) || ii < 0 || jj < 0 || ii >= nx || jj >= ny) continue;
                    int kk = jj * nx + ii;
                    if (node[kk] >= 0 && Near(meet[kk]!, x, y, _attachToleranceDbu)) return Settle(kk);
                }
            return -1;
        }

        // The copper fraction of a cell, and the mesh's own rule for a point landing on one under
        // half full (PdnMeshExtractor.Settle, brief 32): it moves to the fuller neighbour, repeated
        // while that keeps getting fuller. The refined cells under a port are the mesh's size, so
        // they can clip an antipad's edge exactly as the mesh's can.
        double Fill(int k) =>
            area[k] / ((double)(xs[k % nx + 1] - xs[k % nx]) * (ys[k / nx + 1] - ys[k / nx]));

        int Settle(int k)
        {
            if (node[k] < 0) return -1;
            for (int hop = 0; hop < 4 && Fill(k) < PdnMeshExtractor.AttachFillFraction; hop++)
            {
                int ci = k % nx, cj = k / nx, best = -1;
                foreach (int nk in new[] { ci + 1 < nx ? k + 1 : -1, ci > 0 ? k - 1 : -1,
                                           cj + 1 < ny ? k + nx : -1, cj > 0 ? k - nx : -1 })
                    if (nk >= 0 && node[nk] >= 0 && area[nk] > area[k] &&
                        (best < 0 || area[nk] > area[best]))
                        best = nk;
                if (best < 0) break;
                k = best;
            }
            return node[k];
        }

        foreach (var (x, y) in attachments)
        {
            if (!b.Contains(x, y)) continue;
            int n = At(x, y);
            if (n < 0) continue;
            nodes.RegisterPoint(c.Region.Layer, c.IsReference, x, y, n);
            if (!c.IsReference) RailNodeAt(x, y, n);
        }

        nodes.AddLookup(c.Region.Layer, c.IsReference, b, At);

        int cells = node.Count(n => n >= 0);
        string refined = entries.Count > 0
            ? $", refined to {_contactDbu / dbuPerMetre * 1e3:0.####} mm under its {entries.Count} " +
              "port pad(s) — the cell the mesh lands a port in"
            : "";
        return c with
        {
            Reason = c.Reason +
                $" Meshed coarsely at {Math.Max(px, py) / dbuPerMetre * 1e3:0.###} mm{refined} — {cells} cell(s).",
        };
    }

    /// <summary>Lines at <paramref name="pitch"/> from <paramref name="lo"/> to <paramref name="hi"/>,
    /// both included.</summary>
    private static long[] UniformLines(long lo, long hi, long pitch)
    {
        var lines = new List<long>();
        for (long v = lo; v < hi; v += pitch) lines.Add(v);
        lines.Add(hi);
        return [.. lines];
    }

    /// <summary>
    /// Lines over [<paramref name="lo"/>, <paramref name="hi"/>] with a cell of each entry's own
    /// contact size centred on it, <paramref name="core"/> more of that size each side of it — the
    /// band the mesh refines under a port — then cells doubling away from them, none wider than
    /// <paramref name="pitch"/>, and none wider than <paramref name="between"/> inside
    /// <paramref name="region"/>.
    ///
    /// <para><b>Per axis, from the entries' coordinates, not per entry.</b> A connector's twenty
    /// pads share four columns and six rows; a window per pad would lay twenty sets of lines over
    /// one another, and the grid would grow as the square of that. Entries closer than one contact
    /// cell share it.</para>
    /// </summary>
    internal static long[] GradedLines(
        long lo, long hi, IEnumerable<(long At, long Contact)> entries, int core, long pitch,
        long between, (long Lo, long Hi) region)
    {
        var cells = new List<(long A, long B, long Size)>();
        foreach (var (e, size) in entries.Distinct().OrderBy(p => p.At))
        {
            long a = e - size / 2, z = a + size;
            if (cells.Count > 0 && a <= cells[^1].B)
                cells[^1] = (cells[^1].A, Math.Max(cells[^1].B, z), Math.Min(cells[^1].Size, size));
            else cells.Add((a, z, size));
        }

        var lines = new List<long> { lo, hi };
        long prev = lo, prevSize = 0;
        foreach (var (a, z, size) in cells)
        {
            Fill(prev, a, prevSize, size);
            lines.Add(a);
            lines.Add(z);
            prev = z;
            prevSize = size;
        }
        Fill(prev, hi, prevSize, 0);

        return [.. lines.Where(v => v >= lo && v <= hi).Distinct().Order()];

        // Lines strictly between a and z: `core` contact-sized cells beside whichever side is a
        // contact, then cells growing ×2, capped at the pitch; the finer side steps first, and a
        // last cell under half the one beside it is folded into it rather than left as a sliver.
        // (A ×2 step straight off the contact cell read 1.21× on the return fixture where the
        // mesh's own band of fine cells reads 1.01×: a node whose neighbours are twice its size is
        // a larger contact than one among its equals.)
        // A size of zero is the grid's own boundary rather than a contact.
        void Fill(long a, long z, long sizeA, long sizeB)
        {
            long left = a, right = z, sa = sizeA, sb = sizeB;
            long lastStep = 0;
            int added = 0, na = 0, nb = 0;
            while (true)
            {
                long nextA = sizeA > 0 ? Math.Min(na < core ? sa : sa * 2, pitch) : pitch;
                long nextB = sizeB > 0 ? Math.Min(nb < core ? sb : sb * 2, pitch) : pitch;
                if (left < region.Hi && left + nextA > region.Lo) nextA = Math.Min(nextA, between);
                if (right > region.Lo && right - nextB < region.Hi) nextB = Math.Min(nextB, between);
                if (right - left <= Math.Max(nextA, nextB)) break;

                if (nextA <= nextB) { left += nextA; lines.Add(left); sa = nextA; lastStep = nextA; na++; }
                else { right -= nextB; lines.Add(right); sb = nextB; lastStep = nextB; nb++; }
                added++;
            }

            if (added > 0 && right - left < lastStep / 2)
                lines.RemoveAt(lines.Count - 1);
        }
    }

    /// <summary>The cell of <paramref name="lines"/> holding <paramref name="v"/>, or -1.</summary>
    private static int Index(long[] lines, long v)
    {
        if (v < lines[0] || v > lines[^1]) return -1;
        int i = Array.BinarySearch(lines, v);
        if (i < 0) i = ~i - 1;
        return Math.Min(i, lines.Length - 2);
    }

    /// <summary>True where <paramref name="x"/>, <paramref name="y"/> is on <paramref name="copper"/>
    /// or within <paramref name="tolerance"/> of its edge.</summary>
    private static bool Near(Paths64 copper, long x, long y, long tolerance)
    {
        if (Regions.Contains(copper, x, y)) return true;

        double limit = (double)tolerance * tolerance;
        foreach (var ring in copper)
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Count];
                double ex = q.X - p.X, ey = q.Y - p.Y;
                double len = ex * ex + ey * ey;
                double t = len > 0 ? Math.Clamp(((x - p.X) * ex + (y - p.Y) * ey) / len, 0, 1) : 0;
                double dx = p.X + t * ex - x, dy = p.Y + t * ey - y;
                if (dx * dx + dy * dy <= limit) return true;
            }
        return false;
    }

    /// <summary>The point of <paramref name="copper"/>'s boundary nearest to <paramref name="x"/>,
    /// <paramref name="y"/>.</summary>
    private static (long X, long Y) Closest(Paths64 copper, long x, long y)
    {
        double best = double.MaxValue;
        (long X, long Y) at = (x, y);
        foreach (var ring in copper)
            for (int i = 0; i < ring.Count; i++)
            {
                var p = ring[i];
                var q = ring[(i + 1) % ring.Count];
                double ex = q.X - p.X, ey = q.Y - p.Y;
                double len = ex * ex + ey * ey;
                double t = len > 0 ? Math.Clamp(((x - p.X) * ex + (y - p.Y) * ey) / len, 0, 1) : 0;
                double px = p.X + t * ex, py = p.Y + t * ey;
                double d = (px - x) * (px - x) + (py - y) * (py - y);
                if (d < best) { best = d; at = ((long)Math.Round(px), (long)Math.Round(py)); }
            }
        return at;
    }

    /// <summary>Exact copper area per cell, by clipping a row band once and then each cell of it
    /// — the same construction <c>PdnMeshExtractor</c>'s rasteriser uses, and exact rather than
    /// sampled, because a cell's area IS its conductance. The clipped copper comes back too, so a
    /// point can be asked whether it is ON a cell's copper rather than merely in the cell.</summary>
    private static double[] CellAreas(Paths64 copper, long[] xs, long[] ys, out Paths64?[] meets)
    {
        int nx = xs.Length - 1, ny = ys.Length - 1;
        var area = new double[nx * ny];
        meets = new Paths64?[nx * ny];

        for (int j = 0; j < ny; j++)
        {
            long ya = ys[j], yb = ys[j + 1];
            Paths64 band = [[new Point64(xs[0], ya), new Point64(xs[^1], ya),
                             new Point64(xs[^1], yb), new Point64(xs[0], yb)]];

            var strip = Clipper.BooleanOp(ClipType.Intersection, copper, band, LayoutClipper.Rule);
            if (strip.Count == 0) continue;

            var sb = DrcRegions.BoundsOf(strip);

            for (int i = 0; i < nx; i++)
            {
                long xa = xs[i], xb = xs[i + 1];
                if (xb <= sb.MinX || xa >= sb.MaxX) continue;

                Paths64 cell = [[new Point64(xa, ya), new Point64(xb, ya),
                                 new Point64(xb, yb), new Point64(xa, yb)]];
                var meet = Clipper.BooleanOp(ClipType.Intersection, strip, cell, LayoutClipper.Rule);
                if (meet.Count == 0) continue;

                double a = Math.Abs(Clipper.Area(meet));
                if (a > 0) { area[j * nx + i] = a; meets[j * nx + i] = meet; }
            }
        }

        return area;
    }

    // ── bookkeeping ────────────────────────────────────────────────────────────────────────────

    private void Register(
        PdnTraceRaster raster, int[] nodeOfPixel, PdnClassification c,
        IReadOnlyList<(long X, long Y)> mine)
    {
        foreach (var (x, y) in mine)
        {
            int s = raster.SkeletonAt(x, y);
            if (s < 0 || s >= nodeOfPixel.Length) continue;
            int n = nodeOfPixel[s];
            if (n < 0) continue;

            nodes.RegisterPoint(c.Region.Layer, c.IsReference, x, y, n);
            if (!c.IsReference) RailNodeAt(x, y, n);
        }
    }

    private void RailNodeAt(long x, long y, int node)
    {
        if (!_railNodesAtPoint.TryGetValue((x, y), out var list)) _railNodesAtPoint[(x, y)] = list = [];
        if (!list.Contains(node)) list.Add(node);
    }
}
