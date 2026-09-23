// The ACCURATE reading: a mesh of unit cells over the real copper, at DC and above it
// (docs/sonnet-briefs/brief-railrf-3-mesh-extractor.md R-rail3-6 … R-rail3-8 and
//  brief-railrf-13-distributed.md R-rail13-1 … R-rail13-4, railrf.md §4.1).
//
// ── DC IS THE FIRST POINT OF THE SWEEP, NOT A MODE ─────────────────────────────────────────────
//
// Set ω = 0 and §4.1's inductance and shunt branch both vanish; what is left is a purely resistive
// mesh — real, symmetric, positive-definite and fast. Above ω = 0 each cell edge carries R + jωL
// with L = µ₀·h per square (R-rail13-1); the shunt branch is still brief 14's and is absent at
// every frequency here. §2.8 is explicit that this is not a mode bolted on: ONE EXTRACTOR, ONE
// MESH, ONE SOLVER is what stops a DC answer and an AC answer drifting apart — which is why
// PdnExtractionRequest.FrequencyHz is a number on the request rather than a second entry point.
//
// ── THE FACTOR OF TWO, WHICH IS THE TERM EVERYONE SIMPLIFIES OUT ───────────────────────────────
//
// §4.1 states the plane pair's series resistance per cell edge as R = 2·Rs, "both planes, in series
// in the loop", and rev 2 of the design note got exactly this wrong — it used ONE plane's sheet
// resistance and every derived crossover frequency came out at HALF its real value.
//
// THE 2 IS THE LOOP'S, NOT ONE EDGE'S, AND THIS EXTRACTOR PAYS IT BY MESHING BOTH CONDUCTORS.
// §4.3 requires separate power and reference NODES — "each capacitor … connecting the power node to
// the reference node", "each load port across the power and reference nodes of its own pin-field
// cells". With both conductors meshed, a loop that leaves the source, crosses the power plane and
// returns through the reference plane traverses Rs on the way out and Rs on the way back: 2·Rs, as
// §4.1 says, arrived at by construction rather than by a constant.
//
// SO EACH EDGE CARRIES ONE SQUARE OF ITS OWN CONDUCTOR, AND DELETING THE REFERENCE MESH IS WHAT
// LOSES THE FACTOR OF TWO. A reader who "optimises" by meshing only the power rail and tying the
// reference to ground halves every loop resistance in the answer and nothing reports it. That is the
// simplification this comment exists against — and PdnMeshExtractorTests' own plane-pair test is
// what holds it shut, by measuring the loop and asserting 2·Rs.
//
// ── THE MESH FOLLOWS THE COPPER (R-rail3-7) ────────────────────────────────────────────────────
//
// A cell is present where the conductor has copper; a cutout, an antipad field, a split or a board
// edge simply removes cells. "The actual shapes used" is not a stretch goal — it falls out of the
// meshing, and ANY CODE THAT SPECIAL-CASES A RECTANGLE HAS MADE IT A STRETCH GOAL AGAIN. There is no
// such case below.
//
// ── WHY THE CONDUCTANCES ARE BUILT FROM THE COPPER AND NOT FROM CELL COUNTS ────────────────────
//
// A binary present/absent cell makes a conductor's width a multiple of Δ, so a 0.3 mm trace meshed
// at 0.1 mm is right and the same trace meshed at 0.13 mm is 33 % wrong — silently, and in whichever
// direction the rounding fell. Instead each edge between two cells conducts through the copper the
// two cells actually SHARE across it (R-rail32-2):
//
//     R(edge)  =  ((dx(i) + dx(i+1)) / 2)  /  (σ · T · shared copper along the edge)
//
// On a straight trace of ANY width, at ANY angle and ANY pitch, a uniform field satisfies these
// equations exactly — the flux through each edge is the true flux through the copper on it, and a
// cut cell's walls carry none — so the sum is ρ·L/(W·T), the closed form §7 gates against, whether
// or not the grid lines fall on the edges. That is what lets the DC mesh be coarse (R-rail3-14)
// without being optimistic.
//
// Each cell still carries the AREA of copper inside it: that is what R-rail3-7's area gate and the
// shunt branch read. The conductances used to be built from it — the half-cell harmonic average
// dx²/(2·σ·T·A) — which is the same number on an axis-aligned trace and was 19 % high on a 45° one
// three cells wide, because every partial cell along both edges became a series bottleneck.
//
// ── A POINT NEVER ATTACHES TO A SLIVER (R-rail32-1) ────────────────────────────────────────────
//
// A cell clipping a plane's corner holds a few square microns and shares a few microns of edge; a
// source, a load or a via attached THERE pays a resistance set by where the grid lines fell. On a
// field board that was the whole of a 3.6× error, all of it in ONE edge beside the reference point.
// MeshBuilder.Settle moves such an attachment to the fuller neighbour it shares the most copper with.

using Clipper2Lib;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.RailRf;
using CircuitRF.Engine.Pdn;

using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// How finely, and where. <b>Two rules live here and only one of them binds at DC</b> — see
/// <see cref="PdnMeshExtractor.WavelengthCellSizeMetres"/> for the other and for why stating both in
/// one place matters.
/// </summary>
public sealed class PdnMeshSettings
{
    /// <summary>A stated cell size, in METRES, which overrides the computed one. Null is the
    /// ordinary case.</summary>
    public double? CellSizeMetres { get; set; }

    /// <summary>
    /// How many cells the narrowest current-carrying conductor on the rail gets across it.
    ///
    /// <para>Three, not thirty: the area-weighted conductances above make a straight trace exact at
    /// any pitch, so this buys resolution of SHAPE — a bend, a neck, an antipad — rather than
    /// resolution of width.</para>
    /// </summary>
    public int CellsAcrossMinimumFeature { get; set; } = 3;

    /// <summary>
    /// The local refinement ratio under a port region (R-rail3-8). <b>A correctness requirement, not
    /// an optimisation</b> — see <see cref="PdnMeshExtractor"/>'s own note at the refinement site.
    /// 1 turns it off, which is what the convergence gate sweeps.
    /// </summary>
    public int PortRefinementRatio { get; set; } = 4;

    /// <summary>How far beyond a port's own pads the refined band reaches, in base cells.</summary>
    public int PortRefinementMarginCells { get; set; } = 2;

    /// <summary>The ceiling on cells, power and reference together — cells that EXIST, which is to
    /// say cells with copper in them, not the bounding grid they are cut from (R-rail32-1). A mesh
    /// above it is COARSENED and the coarsening is reported, on the cell size's own basis as well as
    /// in a note — never silently truncated, and never run to exhaustion.</summary>
    public int MaxCells { get; set; } = 400_000;

    /// <summary>
    /// Whether islands of the rail with no DC path to the reference point are stamped anyway.
    ///
    /// <para><b>False by default, and that is a decision about solvability rather than about
    /// physics.</b> The island structure is always REPORTED (R-rail3-4) — that is what
    /// <see cref="PdnRailRegionSet"/> is for. What this controls is whether a region reachable only
    /// through a capacitor, which is to say through nothing at DC, contributes floating nodes to the
    /// netlist. It is a real region and it is correct that it is separate; it simply has no DC
    /// answer, and a netlist carrying it has no DC answer either.</para>
    /// </summary>
    public bool IncludeIsolatedRegions { get; set; }
}

/// <summary>Everything one extraction reads. <b>One rail</b> — see R-rail3-13.</summary>
public sealed class PdnExtractionRequest
{
    /// <summary>The rail to extract. The extractor has no concept of a second one.</summary>
    public required RailSpec Rail { get; init; }

    /// <summary>The artwork, flattened to shapes in DBU — the cell's own layout view.</summary>
    public required IReadOnlyList<LayoutShape> Shapes { get; init; }

    /// <summary>The stackup: layer order, copper thickness, conductivity, via spans and plating.</summary>
    public required Technology Technology { get; init; }

    /// <summary>The artwork's DBU resolution.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// How a coordinate or a length coming off this artwork is SPELLED on a report row.
    /// </summary>
    /// <remarks>
    /// <b>DBU is what a coordinate IS; this is what it reads as</b> (owner, 2026-09-18). Beside
    /// <c>DbuPerMicron</c> rather than derived from it, because the resolution is only half the
    /// answer — the other half is the layout's own display unit, which nothing here can infer.
    /// Defaulted to <see cref="RailLengthFormat.Dbu"/>, which prints the integer and says "DBU" out
    /// loud rather than picking a unit nobody stated.
    /// </remarks>
    public RailLengthFormat LengthFormat { get; init; } = RailLengthFormat.Dbu;

    /// <summary>The board's pads, as the board netlist or a placement join knows them.</summary>
    public IReadOnlyList<PlacedPin> Pads { get; init; } = [];

    /// <summary>What the board netlist knows about net identity. <c>BoardNetlistRecord</c> maps on
    /// directly.</summary>
    public IReadOnlyList<PdnNetPoint> NetPoints { get; init; } = [];

    /// <summary>The reference return's net, where one is named. Null takes the whole reference
    /// layer, which is the ordinary case.</summary>
    public string? ReferenceNet { get; init; }

    /// <summary>The board outline, DBU. Required by
    /// <see cref="RailReferenceExtent.FilledToOutline"/> and unread otherwise.</summary>
    public Paths64? BoardOutline { get; init; }

    /// <summary>What bridges the gaps the copper leaves at every pad (§2.8) — the protection FET,
    /// the ferrite, a link.</summary>
    public IReadOnlyList<PdnSeriesElement> SeriesElements { get; init; } = [];

    /// <summary>The decoupling. Present in the netlist, contributing no DC path (R-rail3-4).</summary>
    public IReadOnlyList<PdnShuntPart> ShuntParts { get; init; } = [];

    /// <summary>The document's own settings — the copper temperature and the via plating.</summary>
    public RailSettings Settings { get; init; } = new();

    /// <summary>
    /// The frequency this extraction is FOR, in hertz. <b>Zero is DC and is the first point of the
    /// sweep</b> (§2.8), not a mode: at ω = 0 §4.1's inductance vanishes, every copper element is a
    /// plain resistor and the system is real, symmetric and positive-definite.
    ///
    /// <para>Above zero each cell edge carries <c>R + jωL</c> with <c>L = µ₀·h</c> per square
    /// (R-rail13-1) and <c>R</c> taken at this frequency's own sheet resistance — which is the SAME
    /// number as the DC one below <see cref="PdnProvenance.SkinCrossoverHz"/>, and that is the
    /// performance property R-rail13-2 exists to make visible.</para>
    ///
    /// <para><b>There is still no shunt branch at any frequency here.</b> §4.1's <c>C</c> and
    /// <c>G</c> to the reference plane are brief 14; this band is a distributed R-L network with the
    /// lumped parts hung on it, exactly as §2.8 says.</para>
    /// </summary>
    public double FrequencyHz { get; init; }

    /// <summary>How finely, and where. Read by <see cref="PdnMeshExtractor"/>; the fast reading
    /// takes only <see cref="PdnMeshSettings.IncludeIsolatedRegions"/> from it, which is a question
    /// about solvability rather than about meshing.</summary>
    public PdnMeshSettings Mesh { get; init; } = new();

    /// <summary>How the fast reading traces and how coarsely it meshes what it will not trace. Read
    /// by <see cref="PdnGraphExtractor"/> and by nothing else.</summary>
    public PdnGraphSettings Graph { get; init; } = new();

    /// <summary>
    /// Which regions the user has forced either way, keyed by region identity (R-rail4-3).
    ///
    /// <para><b>They live on the <c>RailDocument</c></b>, so they survive a re-import — which is
    /// exactly when a classification would otherwise silently change. This is how they reach the
    /// extraction; nothing here reads a document.</para>
    /// </summary>
    public IReadOnlyDictionary<PdnRegionRef, PdnCopperClass> ClassOverrides { get; init; } =
        new Dictionary<PdnRegionRef, PdnCopperClass>();

    /// <summary>
    /// Cancellation and progress for this extraction. Null is the ordinary headless case and makes
    /// every checkpoint a no-op.
    /// </summary>
    /// <remarks>
    /// <b>Because an extraction could not be stopped and said nothing while it ran</b> (field
    /// report, 2026-09-22). A designer pressed Run on a production six-layer board, got the word
    /// "solving…" at the end of a status strip, and reported that it was "probably run in a dead
    /// end" — with no way to tell a long answer from a hung one and no way to take it back. The
    /// window had BUILT a <see cref="RunControl"/> since brief 7 and passed it to nothing: cancelling
    /// stopped the window LISTENING and left the work running on a thread-pool thread, so a user
    /// editing a value four times had four whole-board extractions in flight at once.
    ///
    /// <para><b>The checkpoints are at the boundaries between phases, and inside the one loop that
    /// is per-PIECE</b> — the same granularity <see cref="RunControl"/>'s own contract states for
    /// every other engine here: answered within one unit of work, never inside a matrix
    /// factorisation. What that buys is that the stage NAME is always the phase actually running, so
    /// the next report of a slow board says which phase was slow instead of "solving…".</para>
    /// </remarks>
    public RunControl? Control { get; init; }
}

/// <summary>Copper in, <c>ElaboratedNetlist</c> out.</summary>
public static class PdnMeshExtractor
{
    /// <summary>
    /// Copper's temperature coefficient of resistivity, per °C, referred to 20 °C. There is no
    /// thermal model (§2.7) and this is not one — it is how the ONE stated basis in
    /// <c>RailSettings.CopperTemperatureCelsius</c> is applied, so a report can say which.
    /// </summary>
    public const double CopperAlphaPerCelsius = 0.00393;

    /// <summary>The temperature every published sheet resistance is quoted at.</summary>
    public const double ReferenceTemperatureCelsius = 20.0;

    /// <summary>
    /// <b>The OTHER cell-size rule, and from brief 14 BOTH of them bind — the cell is the SMALLER.</b>
    ///
    /// <para>§4.1 sizes cells by the shortest wavelength in the dielectric, Δ ≤ λ_min/20 — 7.2 mm at
    /// 1 GHz on FR-4, 1.4 mm at 5 GHz. That rule exists to resolve the CAVITY: a cell longer than
    /// that cannot carry the phase across itself, so the plane resonance it is meant to produce is
    /// simply not in the model. It binds from brief 14 onward, which is why this method is now
    /// called at every non-zero frequency and not only consulted.</para>
    ///
    /// <para><b>The other constraint is not wavelength at all — it is GEOMETRY, and it binds at every
    /// frequency including DC.</b> A cell must resolve the narrowest conductor that carries current,
    /// or a 0.15 mm trace becomes a cell wide and its resistance is wrong by whatever the cell size
    /// is. That is <see cref="MinimumFeatureWidthDbu"/>, with local refinement under every port
    /// region.</para>
    ///
    /// <para><b>So the cell size is the smaller of the two, and each exists for its own reason</b>
    /// (R-rail3-14, R-rail14-2). They are stated together here because a later reader who sees only
    /// the wavelength rule will "fix" the mesh to it and quietly lose every thin trace — 7.2 mm
    /// cells on 0.3 mm traces — and a reader who sees only the feature rule will run the cavity band
    /// on a mesh that cannot hold a resonance. Neither failure reports anything; the extraction
    /// names which rule bound it in <see cref="PdnProvenance.CellSizeBasis"/> so it does.</para>
    /// </summary>
    /// <param name="frequencyHz">The top of the band being solved. Zero — DC — returns infinity,
    /// which is this rule saying it does not bind rather than a guard.</param>
    /// <param name="epsilonR">The dielectric's relative permittivity. <b>The LARGEST in the
    /// stackup</b>, which is the shortest wavelength and so the smallest cell.</param>
    public static double WavelengthCellSizeMetres(double frequencyHz, double epsilonR)
    {
        if (!(frequencyHz > 0) || !(epsilonR > 0)) return double.PositiveInfinity;
        return PdnCavity.SpeedOfLight / (frequencyHz * Math.Sqrt(epsilonR)) / 20.0;
    }

    /// <summary>The largest relative permittivity any dielectric of the stackup states — the
    /// shortest wavelength on the board, which is what <see cref="WavelengthCellSizeMetres"/> has to
    /// be sized for. <b>Shared with <see cref="PdnGraphExtractor"/></b>, whose own cavity-band
    /// refusal is derived from the same number and must not be derived from a second reading.</summary>
    internal static double LargestEpsilonR(Technology tech)
    {
        double best = 1.0;
        if (tech is null) return best;
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Dielectric && l.Epsr > best) best = l.Epsr;
        return best;
    }

    /// <summary>Copper resistivity at <paramref name="celsius"/>, from a conductivity quoted at
    /// 20 °C.</summary>
    public static double ResistivityAt(double conductivitySm, double celsius)
    {
        if (!(conductivitySm > 0)) return double.PositiveInfinity;
        return (1.0 / conductivitySm) * (1.0 + CopperAlphaPerCelsius * (celsius - ReferenceTemperatureCelsius));
    }

    // ── the extraction ─────────────────────────────────────────────────────────────────────────

    /// <summary>Extracts <paramref name="request"/>'s rail into a netlist, or refuses and says why.</summary>
    public static PdnExtraction Extract(PdnExtractionRequest request)
    {
        var plan = Plan(request);
        return plan.Refusal ?? plan.At(request.Mesh.CellsAcrossMinimumFeature);
    }

    /// <summary>
    /// Everything an extraction decides BEFORE it picks a pitch — the rail's copper, its return,
    /// the conductors, the narrowest copper — so the same rail can be meshed at a second pitch for
    /// the price of the mesh alone (R-rail32-2). On the field board the part before the pitch is
    /// most of the extraction's time.
    /// </summary>
    internal static PdnMeshPlan Plan(PdnExtractionRequest request)
    {
        var rail = request.Rail;
        var tech = request.Technology;
        var diagnostics = new List<string>();
        var notes = new List<string>();

        if (rail.Refusal() is { } railRefusal) return PdnExtraction.Refused(railRefusal);

        if (rail.ReferenceLayer is not { } referenceLayer)
            return PdnExtraction.Refused(
                $"Rail '{rail.Name}' states no reference layer, so there is nothing to return current " +
                "through and no mesh to build. Name the reference return's drawing layer — railRF " +
                "never infers one (railrf.md §2.2, Q-8).");

        // ── the copper, flattened exactly as the DRC run flattens it ───────────────────────────
        var layerRegions = LayerRegions.Build(request.Shapes, tech, diagnostics);
        if (layerRegions.Count == 0)
            return PdnExtraction.Refused(
                "This artwork flattens to no copper at all. Check that the layout view carries the " +
                "board's shapes and that its technology names the layers they are on.");

        // ── which copper is this rail (R-rail3-3) ──────────────────────────────────────────────
        var anchorSeeds = new List<(long X, long Y)>();
        foreach (var s in rail.Sources) anchorSeeds.AddRange(PdnAttachments.Resolve(s.Anchor, request.Pads));
        foreach (var l in rail.Loads) anchorSeeds.AddRange(PdnAttachments.Resolve(l.Anchor, request.Pads));

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

        var regions = PdnRailConnectivity.Walk(request, layerRegions, referenceLayer, anchorSeeds, bareCoordinateSeeds);

        diagnostics.AddRange(regions.Diagnostics);

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

        // R-rail31-3 / R-rail31-4: what the RETURN is, answered as galvanically as the rail was.
        if (PdnRailConnectivity.ReturnRefusal(request, regions, referenceLayer) is { } noReturn)
            return PdnExtraction.Refused(noReturn, regions);

        // ── the conductors, and what each square of them costs ─────────────────────────────────
        if (ResolveConductors(request, regions, referenceLayer, out _, out var byLayer)
            is { } conductorRefusal)
            return PdnExtraction.Refused(conductorRefusal, regions);

        return new PdnMeshPlan(request, referenceLayer, regions, byLayer, anchorSeeds,
                               MinimumFeatureWidthDbu(regions.Power), diagnostics, notes);
    }

    // ── geometry helpers ───────────────────────────────────────────────────────────────────────



    /// <summary>
    /// The stackup's conductors, and the four refusals a mesh built on a conductor with no sheet
    /// resistance would otherwise hide.
    ///
    /// <para><b>Shared with <see cref="PdnGraphExtractor"/> on purpose.</b> "A perfect plane" is the
    /// optimistic answer §9 warns about and exactly the one nothing reports, so the two readings
    /// refuse on the same terms and in the same words; two copies of this would drift and one of the
    /// readings would quietly start answering where the other refuses.</para>
    /// </summary>
    internal static string? ResolveConductors(
        PdnExtractionRequest request, PdnRailRegionSet regions, LayerKey referenceLayer,
        out List<PdnConductor> conductors, out Dictionary<LayerKey, PdnConductor> byLayer)
    {
        var tech = request.Technology;
        double celsius = request.Settings.CopperTemperatureCelsius;
        conductors = [];
        byLayer = [];

        foreach (var sl in tech.Stackup.Layers)
        {
            if (sl.Kind != StackupKind.Conductor) continue;
            foreach (var key in sl.DrawingLayers)
            {
                if (byLayer.ContainsKey(key)) continue;
                double t = sl.ThicknessDbu / (double)request.DbuPerMicron * 1e-6;
                double sigma = sl.SigmaSm / (1.0 + CopperAlphaPerCelsius * (celsius - ReferenceTemperatureCelsius));
                var c = new PdnConductor(sl.Name, key, t, sigma);
                conductors.Add(c);
                byLayer[key] = c;
            }
        }

        // A conductor with no thickness or no conductivity has no sheet resistance, and a mesh built
        // on one is a mesh of zero-ohm links — a perfect plane, which is exactly the optimistic
        // answer §9 warns about and exactly the one nothing reports. Refuse, and name the field.
        var viaDrawingLayers = tech.Stackup.Layers
            .Where(l => l.Kind == StackupKind.Via)
            .SelectMany(l => l.DrawingLayers)
            .ToHashSet();

        foreach (var layer in RailLayers(regions, referenceLayer))
        {
            // A via's BARREL disc is on a via drawing layer and reaches the rail through the
            // connectivity walk. It is a bridge between conductors rather than sheet copper of its
            // own, it carries no thickness, and PdnViaModel is what prices it.
            if (viaDrawingLayers.Contains(layer)) continue;

            if (!byLayer.TryGetValue(layer, out var c))
                return (
                    $"The rail reaches layer {layer.Layer}/{layer.Datatype}, which no Conductor entry " +
                    "of the stackup claims. Map that drawing layer onto a conductor in the technology's " +
                    "stackup, or the copper on it has no thickness and no resistance.");

            if (!(c.ThicknessMetres > 0))
                return (
                    $"Stackup conductor '{c.StackupName}' states no thickness, so its copper has no " +
                    "sheet resistance and the mesh on it would be a perfect plane. State its finished " +
                    "copper thickness.");

            if (!(c.ConductivitySm > 0))
                return (
                    $"Stackup conductor '{c.StackupName}' states no conductivity, so its copper has no " +
                    "sheet resistance and the mesh on it would be a perfect plane. State its " +
                    "conductivity in S/m — copper is 5.8e7 at 20 °C.");
        }


        return null;
    }


    /// <summary>
    /// What the reference conductor is taken to BE, and the note that says so where two of the three
    /// answers are optimistic (R-rail3-5).
    ///
    /// <para><b>Shared with <see cref="PdnGraphExtractor"/></b>: the reference extent is carried on
    /// every result and stamped on every plot and export, and a reader who does not know which was
    /// used cannot tell. Two readings of the board that applied it differently would make that
    /// stamp mean two things.</para>
    /// </summary>
    internal static string? ResolveReferenceCopper(
        PdnExtractionRequest request, PdnRailRegionSet regions, LayerKey referenceLayer,
        Bbox extent, List<string> notes, out List<(LayerKey Layer, Paths64 Paths)> referenceCopper)
    {
        var rail = request.Rail;
        referenceCopper = [];
        switch (rail.ReferenceExtent)
        {
            case RailReferenceExtent.AsImported:
                foreach (var island in regions.Reference) referenceCopper.AddRange(island.Copper);
                break;

            case RailReferenceExtent.FilledToOutline:
                if (request.BoardOutline is not { Count: > 0 } outline)
                    return (
                        $"Rail '{rail.Name}' asks for its reference to be filled to the board outline, " +
                        "and this extraction was given no outline. Supply the board outline, or set the " +
                        "reference extent to 'as imported' and accept the copper that is actually there.");
                referenceCopper.Add((referenceLayer, outline));
                notes.Add("The reference was taken as SOLID within the board outline. That removes " +
                          "every return constriction the real copper may have, so this answer is " +
                          "optimistic.");
                break;

            default:
                referenceCopper.Add((referenceLayer, RectPaths(extent)));
                notes.Add("The reference was taken as UNBOUNDED at its own z, realised as a solid " +
                          "plane over the whole mesh extent. That is an upper bound and the only way " +
                          "to compare two outlines on equal terms; it is optimistic.");
                break;
        }

        if (referenceCopper.Count == 0)
            return
                $"Rail '{rail.Name}' names layer {referenceLayer.Layer}/{referenceLayer.Datatype} as " +
                "its reference and there is no copper on it. Name the layer the return actually runs " +
                "on, or fill the reference to the board outline.";



        return null;
    }

    internal static IEnumerable<LayerKey> RailLayers(PdnRailRegionSet regions, LayerKey referenceLayer)
    {
        var seen = new HashSet<LayerKey> { referenceLayer };
        yield return referenceLayer;
        foreach (var island in regions.Power)
            foreach (var (layer, _) in island.Copper)
                if (seen.Add(layer)) yield return layer;
    }

    internal static Bbox ExtentOf(
        PdnRailRegionSet regions, IReadOnlyList<(long X, long Y)> anchors, long pad)
    {
        var b = Bbox.Empty;
        foreach (var island in regions.Power) b = b.Union(island.Bounds);
        foreach (var island in regions.Reference) b = b.Union(island.Bounds);
        foreach (var (x, y) in anchors) b = b.Union(new Bbox(x, y, x, y));
        if (b.IsEmpty) return b;
        return new Bbox(b.MinX - pad, b.MinY - pad, b.MaxX + pad, b.MaxY + pad);
    }

    /// <summary>
    /// How many cells of the BOUNDING grid a mesh may be cut from — a guard on memory, not on the
    /// solve. Every conductor holds a few numbers per cell of the bounding grid whether or not it
    /// has copper there, so a sparse rail on a large board could otherwise ask for gigabytes before
    /// the copper count could say no. Four times the copper ceiling: a board whose reference fills
    /// its extent reaches the copper ceiling first.
    /// </summary>
    internal static double BoundingGridCeiling(int maxCells) => 4.0 * maxCells;

    internal static Paths64 RectPaths(Bbox b) =>
        [[new Point64(b.MinX, b.MinY), new Point64(b.MaxX, b.MinY),
          new Point64(b.MaxX, b.MaxY), new Point64(b.MinX, b.MaxY)]];

    /// <summary>
    /// The bands the mesh is refined over — <b>R-rail3-8, and it is a CORRECTNESS requirement rather
    /// than an optimisation.</b>
    ///
    /// <para>§9: "A BGA's antipad array removes a large fraction of the copper in a small region and
    /// it is precisely under the load port. Too coarse a mesh there and the spreading inductance is
    /// underestimated — again OPTIMISTICALLY." At DC the same geometry under-estimates the spreading
    /// RESISTANCE, by the same mechanism and in the same direction. A mesh that is not converged
    /// there is optimistic and looks entirely ordinary.</para>
    ///
    /// <para>The bands are the port regions — every source and every load — which is where §9 places
    /// the concern. They are reported in the provenance, so the refinement is a stated choice rather
    /// than an invisible internal one (brief 8 draws it).</para>
    /// </summary>
    private static List<Bbox> RefinementBands(
        RailSpec rail, PdnExtractionRequest request, long deltaDbu)
    {
        if (request.Mesh.PortRefinementRatio <= 1) return [];

        long margin = Math.Max(1, request.Mesh.PortRefinementMarginCells) * deltaDbu;
        var bands = new List<Bbox>();

        void Add(RailPortAnchor anchor)
        {
            var pads = PdnAttachments.Resolve(anchor, request.Pads);
            if (pads.Count == 0) return;
            var b = Bbox.Empty;
            foreach (var (x, y) in pads) b = b.Union(new Bbox(x, y, x, y));
            bands.Add(new Bbox(b.MinX - margin, b.MinY - margin, b.MaxX + margin, b.MaxY + margin));
        }

        foreach (var s in rail.Sources) Add(s.Anchor);
        foreach (var l in rail.Loads) Add(l.Anchor);
        return bands;
    }

    /// <summary>
    /// The narrowest copper on the rail, in DBU — what the DC cell size is set from (R-rail3-14).
    ///
    /// <para>Measured by morphological OPENING rather than guessed: a shape survives an erosion by
    /// <c>w/2</c> followed by a dilation by <c>w/2</c> only where it was at least <c>w</c> wide, so
    /// the largest <c>w</c> that loses no area is the minimum feature width. Bisected on a geometric
    /// ladder, which is a handful of offsets rather than a scan.</para>
    ///
    /// <para><b>PER DRAWING LAYER, and the minimum across them</b> (R-rail18-1a). The quantity wanted
    /// is "the narrowest copper this rail has anywhere", and copper on two drawing layers is not one
    /// 2-D shape. Pooling every layer into one call is how this read ZERO on every multilayer board
    /// for three briefs: a rail crosses itself at every via, the overload below compares a UNION's
    /// area against a SUM of per-path areas, and overlap makes that comparison unsatisfiable at every
    /// width so the bisection bottoms out on its floor of 1 DBU. Unioning the pooled set instead
    /// would be the other wrong answer — a trace crossing a plane would then measure plane-wide.</para>
    /// </summary>
    internal static long MinimumFeatureWidthDbu(IReadOnlyList<PdnRegion> islands)
    {
        // Islands on ONE layer are galvanically separate pieces of the same sheet, so they are
        // measured together: the narrowest of three collinear trace segments is the narrowest copper
        // on that layer, and measuring each island alone would give the same answer more slowly.
        var byLayer = new Dictionary<LayerKey, Paths64>();
        foreach (var island in islands)
            foreach (var (layer, paths) in island.Copper)
            {
                if (paths.Count == 0) continue;
                if (!byLayer.TryGetValue(layer, out var acc)) byLayer[layer] = acc = [];
                acc.AddRange(paths);
            }

        long min = long.MaxValue;
        foreach (var paths in byLayer.Values) min = Math.Min(min, MinimumFeatureWidthDbu(paths));
        return min == long.MaxValue ? 1 : min;
    }

    /// <summary>
    /// The same measurement over ONE 2-D SHAPE — what <see cref="PdnCopperClassifier"/> reports as a
    /// region's width variation, and what <see cref="PdnGraphExtractor"/> sizes its raster from.
    /// </summary>
    /// <remarks>
    /// <b>The input is UNIONED here rather than assumed disjoint</b> (R-rail18-1b). <c>total</c>
    /// below is the sum of the per-path areas while <c>opened</c> is the area of their union, so any
    /// overlap at all makes <see cref="LosesArea"/> true at every width and the bisection returns its
    /// floor — a plausible 1 DBU, reported in words nobody reads as a fault. Unioning is one Clipper
    /// call on a caller's already-unioned copper and it removes the precondition entirely.
    ///
    /// <para>What unioning does NOT make safe is pooling copper from different DRAWING LAYERS into
    /// one call: that is a semantic error rather than an arithmetic one, and the answer it gives — a
    /// trace crossing a plane measuring plane-wide — is just as plausible. The
    /// <see cref="PdnRegion"/>-list overload above is the per-layer route and the only one a rail's
    /// whole copper should take.</para>
    /// </remarks>
    internal static long MinimumFeatureWidthDbu(Paths64 all) =>
        MinimumFeatureWidthDbu(all, skipProvablyEmpty: true, out _);

    /// <summary>
    /// The measurement itself, with the one short cut it takes made switchable and COUNTED — the
    /// switch exists so a test can hold the short cut to the answer the plain bisection gives, and
    /// the count so it can hold the saving (brief-railrf-30).
    /// </summary>
    /// <param name="all">The copper, DBU.</param>
    /// <param name="skipProvablyEmpty">Skip every step <see cref="InscribedRadiusBoundDbu"/> proves
    /// erodes to nothing. False is the plain bisection.</param>
    /// <param name="offsetPairs">How many erode-then-dilate steps were actually computed.</param>
    /// <remarks>
    /// <b>WHY THE SHORT CUT, and why it cannot change the answer.</b> An erosion's cost grows with
    /// its DISTANCE, not only with the vertex count: on a 60,000-vertex pour every offset edge
    /// crosses every other within the distance, and the bisection starts at half the piece's
    /// smaller span. Measured on a field-report board (Release): the three opening steps, at 49, 25
    /// and 12 mm on a pour whose narrowest copper is 0.25 mm, took 79, 40 and 19 s and each eroded
    /// to NOTHING — 137 of the 167 s the measurement cost, to learn what the pour's own geometry
    /// already says.
    ///
    /// <para>A step whose half-width exceeds the largest disc that fits in the copper erodes to the
    /// empty set, and <see cref="LosesArea"/> answers exactly that case with "loses" before it
    /// dilates anything. Clipper's mitred inward offset removes AT LEAST the true erosion (a mitre
    /// at a reflex corner reaches past the arc a round join would draw), so where no disc fits
    /// Clipper finds nothing either. The steps skipped are therefore only ones whose answer is
    /// already known, in the order the bisection would have asked them, and the path it walks is
    /// unchanged.</para>
    /// </remarks>
    internal static long MinimumFeatureWidthDbu(Paths64 all, bool skipProvablyEmpty, out int offsetPairs)
    {
        Measurements++;
        offsetPairs = 0;
        all = DrcRegions.Union(all);
        var bounds = DrcRegions.BoundsOf(all);

        if (all.Count == 0 || bounds.IsEmpty) return 1;

        double total = Math.Abs(Clipper.Area(all));
        if (!(total > 0)) return 1;

        long hi = Math.Max(2, Math.Min(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY));
        long lo = 1;

        // Only where offsets are expensive: on a piece of a few hundred vertices the whole bisection
        // costs less than the raster the bound is read from.
        int vertices = 0;
        foreach (var ring in all) vertices += ring.Count;
        double fits = skipProvablyEmpty && vertices >= InscribedBoundMinimumVertices
            ? InscribedRadiusBoundDbu(all, bounds)
            : double.PositiveInfinity;

        int computed = 0;
        bool Loses(long width)
        {
            if (width / 2.0 > fits) return true;
            computed++;
            return LosesArea(all, width, total);
        }

        // Everything is at least `hi` wide — a solid plane. Nothing narrower exists to resolve.
        if (!Loses(hi)) { offsetPairs = computed; return hi; }

        while (hi - lo > 1 && hi - lo > lo / 32)
        {
            long mid = lo + (hi - lo) / 2;
            if (Loses(mid)) hi = mid; else lo = mid;
        }

        offsetPairs = computed;
        return Math.Max(1, lo);
    }

    /// <summary>How many single-shape measurements THIS THREAD has taken — the counter that holds
    /// "each piece is measured once per extraction" (brief-railrf-30). Thread-static because the
    /// test runner runs extractions side by side.</summary>
    [ThreadStatic] internal static int Measurements;

    /// <summary>Below this many vertices the bisection is cheaper than the bound that would shorten
    /// it.</summary>
    internal const int InscribedBoundMinimumVertices = 2_000;

    /// <summary>How many raster cells the bound is read from, at most. Its precision is two cells,
    /// and it only has to separate the steps that cost minutes — tens of millimetres — from a
    /// narrowest feature of a fraction of one.</summary>
    private const int InscribedBoundMaxCells = 1 << 18;

    /// <summary>
    /// An UPPER bound on the radius of the largest disc inside <paramref name="all"/>, DBU.
    /// </summary>
    /// <remarks>
    /// <b>Why it is a bound, and on which side.</b> The copper is rasterised at pixel centres by
    /// <see cref="PdnTraceRaster.Scanline"/>, and every OFF centre lies outside the copper's
    /// interior. If a disc of radius <c>r</c> about <c>c</c> fits, the pixel centre <c>q</c> nearest
    /// <c>c</c> is within <c>pitch/√2</c> of it and every off centre is at least <c>r</c> from
    /// <c>c</c> — so the exact Euclidean distance from <c>q</c> to the nearest off centre is at least
    /// <c>r − pitch/√2</c>. The raster's border is off by construction, and leaving the off centres
    /// beyond it out only lengthens distances, which keeps the inequality on the safe side. A second
    /// pitch on top absorbs the scanline's floating-point crossing positions and Clipper's rounding
    /// to whole DBU.
    /// </remarks>
    internal static double InscribedRadiusBoundDbu(Paths64 all, Bbox bounds)
    {
        long w = Math.Max(1, bounds.MaxX - bounds.MinX), h = Math.Max(1, bounds.MaxY - bounds.MinY);
        long pitch = Math.Max(1, (long)Math.Ceiling(Math.Sqrt((double)w * h / InscribedBoundMaxCells)));

        long x0 = bounds.MinX - pitch, y0 = bounds.MinY - pitch;
        int nx = (int)(w / pitch) + 3, ny = (int)(h / pitch) + 3;
        var on = new bool[nx * ny];
        PdnTraceRaster.Scanline(all, x0, y0, pitch, nx, ny, on);

        // Exact squared Euclidean distance to the nearest OFF pixel centre, in pixel units — P. F.
        // Felzenszwalb and D. P. Huttenlocher, "Distance Transforms of Sampled Functions", Theory of
        // Computing 8 (2012): one lower-envelope pass per column, then per row.
        // The column pass is the 1-D distance itself, swept down and up. Every column is finite
        // because the raster's first and last rows are off, so no infinity ever reaches the
        // envelope's arithmetic.
        var d2 = new double[nx * ny];
        for (int i = 0; i < nx; i++)
        {
            int run = 0;
            for (int j = 0; j < ny; j++)
            {
                run = on[j * nx + i] ? run + 1 : 0;
                d2[j * nx + i] = run;
            }
            run = 0;
            for (int j = ny - 1; j >= 0; j--)
            {
                run = on[j * nx + i] ? run + 1 : 0;
                double r = Math.Min(run, d2[j * nx + i]);
                d2[j * nx + i] = r * r;
            }
        }

        var f = new double[nx];
        var outBuf = new double[nx];
        var v = new int[nx];
        var z = new double[nx + 1];

        double max = 0;
        for (int j = 0; j < ny; j++)
        {
            for (int i = 0; i < nx; i++) f[i] = d2[j * nx + i];
            LowerEnvelope(f, nx, outBuf, v, z);
            for (int i = 0; i < nx; i++) max = Math.Max(max, outBuf[i]);
        }

        return (Math.Sqrt(max) + Math.Sqrt(0.5) + 1.0) * pitch;
    }

    private static void LowerEnvelope(double[] f, int n, double[] d, int[] v, double[] z)
    {
        int k = 0;
        v[0] = 0;
        z[0] = double.NegativeInfinity;
        z[1] = double.PositiveInfinity;

        for (int q = 1; q < n; q++)
        {
            double s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            while (s <= z[k])
            {
                k--;
                s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
            }
            k++;
            v[k] = q;
            z[k] = s;
            z[k + 1] = double.PositiveInfinity;
        }

        k = 0;
        for (int q = 0; q < n; q++)
        {
            while (z[k + 1] < q) k++;
            double dq = q - v[k];
            d[q] = dq * dq + f[v[k]];
        }
    }

    private static bool LosesArea(Paths64 paths, long width, double total)
    {
        double half = width / 2.0;
        var eroded = Clipper.InflatePaths(paths, -half, JoinType.Miter, EndType.Polygon, 2.0);
        if (eroded.Count == 0) return true;
        var opened = Clipper.InflatePaths(eroded, half, JoinType.Miter, EndType.Polygon, 2.0);
        return Math.Abs(Clipper.Area(opened)) < total * 0.999;
    }

    // ── the grid ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A tensor-product grid of possibly UNEQUAL spacings. Non-uniform because R-rail3-8's local
    /// refinement has to be local: a uniform grid fine enough for a BGA's antipad field is fine
    /// everywhere, and that is three orders of magnitude of cells nobody needs.
    /// </summary>
    private sealed class PdnGrid
    {
        public required long[] Xs { get; init; }
        public required long[] Ys { get; init; }

        public int Nx => Xs.Length - 1;
        public int Ny => Ys.Length - 1;

        public long Dx(int i) => Xs[i + 1] - Xs[i];
        public long Dy(int j) => Ys[j + 1] - Ys[j];

        /// <summary>The grid at <paramref name="delta"/>, refined <paramref name="ratio"/>× across
        /// every band. Builds exactly what it is asked for — the ceiling is
        /// <see cref="Extract"/>'s, and is applied to the cells this grid turns out to hold.</summary>
        public static PdnGrid Build(Bbox extent, long delta, List<Bbox> bands, int ratio)
        {
            long d = Math.Max(1, delta);
            var xs = Lines(extent.MinX, extent.MaxX, d);
            var ys = Lines(extent.MinY, extent.MaxY, d);

            if (ratio > 1 && bands.Count > 0)
            {
                xs = Refine(xs, bands.Select(b => (b.MinX, b.MaxX)), ratio, d);
                ys = Refine(ys, bands.Select(b => (b.MinY, b.MaxY)), ratio, d);
            }

            return new PdnGrid { Xs = xs, Ys = ys };
        }

        private static long[] Lines(long lo, long hi, long delta)
        {
            var lines = new List<long>();
            for (long v = lo; v < hi; v += delta) lines.Add(v);
            lines.Add(hi);
            if (lines.Count < 2) lines.Insert(0, lo - delta);
            return [.. lines];
        }

        private static long[] Refine(
            long[] lines, IEnumerable<(long Lo, long Hi)> bands, int ratio, long minDelta)
        {
            var banded = bands.ToList();
            var set = new SortedSet<long>(lines);

            for (int i = 0; i + 1 < lines.Length; i++)
            {
                long a = lines[i], b = lines[i + 1];
                if (!banded.Any(x => x.Lo < b && x.Hi > a)) continue;

                long step = (b - a) / ratio;
                if (step < 1) continue;
                for (int k = 1; k < ratio; k++) set.Add(a + step * k);
            }

            _ = minDelta;
            return [.. set];
        }
    }

    // ── the cells ──────────────────────────────────────────────────────────────────────────────

    /// <summary>One conductor's own cells on the shared grid.</summary>
    private sealed class MeshLayer
    {
        public required LayerKey Layer { get; init; }
        public required bool IsReference { get; init; }
        public required PdnConductor Conductor { get; init; }
        public Paths64 Copper { get; } = [];

        /// <summary>The same copper, unioned — kept because R-rail14-3's shunt branch needs the
        /// INTERSECTION of two conductors' copper and unioning twice is both a cost and a chance for
        /// the two readings to differ.</summary>
        public Paths64 Union { get; set; } = [];
        public double[] Area { get; set; } = [];

        /// <summary>The copper cell <c>k</c> shares with cell <c>k + 1</c> across the edge between
        /// them, in DBU — the width that edge conducts through (R-rail32-2).</summary>
        public double[] ChordX { get; set; } = [];

        /// <summary>The same across the edge to the cell ABOVE, <c>k + Nx</c>.</summary>
        public double[] ChordY { get; set; } = [];

        public int[] Node { get; set; } = [];
    }

    /// <summary>
    /// The copper fraction below which a cell is not a place to attach anything — the mesh's
    /// <c>MeshBuilder.Settle</c>, and the fast reading's refined pour cells, which are the mesh's size
    /// under a port (brief-railrf-33).
    /// </summary>
    internal const double AttachFillFraction = 0.5;

    /// <summary>
    /// Rasterises copper onto the grid as AREAS, and nothing else. It builds no matrix and owns no
    /// result — see this file's header and R-rail3-2.
    /// </summary>
    private sealed class MeshBuilder : IPdnNodeSource
    {
        private readonly PdnGrid _grid;
        private readonly IReadOnlyDictionary<LayerKey, PdnConductor> _conductors;
        private readonly LayerKey _referenceLayer;
        private readonly List<MeshLayer> _layers = [];
        private bool _prepared;

        public MeshBuilder(
            PdnGrid grid, IReadOnlyDictionary<LayerKey, PdnConductor> conductors, LayerKey referenceLayer)
        {
            _grid = grid;
            _conductors = conductors;
            _referenceLayer = referenceLayer;
        }

        public int CellCount { get { Prepare(); return _cellCount; } }
        private int _cellCount;

        /// <summary>The cells on the rail's own copper — <see cref="CellCount"/> less the
        /// reference's.</summary>
        public int RailCellCount { get { Prepare(); return _railCellCount; } }
        private int _railCellCount;

        /// <summary>The copper the cells account for, in square DBU. A mesh that filled a cutout in
        /// would exceed the real copper and a mesh that dropped one would fall short of it, so this
        /// one number is the whole of R-rail3-7's gate.</summary>
        public double MeshedAreaSquareDbu { get { Prepare(); return _meshedArea; } }
        private double _meshedArea;

        public IReadOnlyList<MeshLayer> Layers { get { Prepare(); return _layers; } }
        public PdnGrid Grid => _grid;

        public void AddCopper(LayerKey layer, Paths64 paths, bool isReference)
        {
            if (paths.Count == 0) return;
            var ml = _layers.FirstOrDefault(l => l.Layer == layer && l.IsReference == isReference);
            if (ml is null)
            {
                if (!_conductors.TryGetValue(layer, out var c)) return;
                ml = new MeshLayer { Layer = layer, IsReference = isReference, Conductor = c };
                _layers.Add(ml);
            }
            ml.Copper.AddRange(paths);
            _prepared = false;
        }

        /// <summary>
        /// Computes each cell's copper area and hands out node indices.
        ///
        /// <para><b>Deterministic by construction</b> — the layers are sorted, the cells are walked
        /// in row-major order, and nothing iterates a dictionary to decide an index. An extraction
        /// that depended on a hash order would produce a drop map that moved between runs, and
        /// briefs 9 and 17 both depend on it not doing that.</para>
        /// </summary>
        private void Prepare()
        {
            if (_prepared) return;
            _prepared = true;

            _layers.Sort((a, b) =>
            {
                int byKind = a.IsReference.CompareTo(b.IsReference);
                if (byKind != 0) return byKind;
                int byLayer = a.Layer.Layer.CompareTo(b.Layer.Layer);
                return byLayer != 0 ? byLayer : a.Layer.Datatype.CompareTo(b.Layer.Datatype);
            });

            int next = 0;
            _cellCount = 0;
            _railCellCount = 0;
            _meshedArea = 0;

            foreach (var ml in _layers)
            {
                var copper = DrcRegions.Union(ml.Copper);
                ml.Union = copper;
                ml.ChordX = new double[_grid.Nx * _grid.Ny];
                ml.ChordY = new double[_grid.Nx * _grid.Ny];
                ml.Area = Rasterise(copper, ml.ChordX, ml.ChordY);
                ml.Node = new int[ml.Area.Length];

                for (int j = 0; j < _grid.Ny; j++)
                    for (int i = 0; i < _grid.Nx; i++)
                    {
                        int k = j * _grid.Nx + i;
                        ml.Node[k] = ml.Area[k] > 0 ? next++ : -1;
                        if (ml.Area[k] > 0)
                        {
                            _cellCount++;
                            if (!ml.IsReference) _railCellCount++;
                            _meshedArea += ml.Area[k];
                        }
                    }
            }

            _nodeTotal = next;

            // The reverse map, built once. A node that cannot name its cell cannot be coloured on a
            // board, and briefs 8 and 15 both draw from it.
            _cellOf = new PdnCellRef[next];
            foreach (var ml in _layers)
                for (int j = 0; j < _grid.Ny; j++)
                    for (int i = 0; i < _grid.Nx; i++)
                    {
                        int n = ml.Node[j * _grid.Nx + i];
                        if (n >= 0) _cellOf[n] = CellRef(ml, i, j);
                    }
        }

        private PdnCellRef[] _cellOf = [];

        /// <summary>The cell a mesh node sits at, or null for a node this mesh did not hand out.</summary>
        public PdnCellRef? CellOfNode(int node)
        {
            Prepare();
            return node >= 0 && node < _cellOf.Length ? _cellOf[node] : null;
        }

        /// <summary>The node of <paramref name="layer"/>'s cell covering the point, or -1.</summary>
        public int NodeOnLayer(LayerKey layer, long x, long y)
        {
            Prepare();
            int i = IndexOf(_grid.Xs, x), j = IndexOf(_grid.Ys, y);
            if (i < 0 || j < 0) return -1;

            foreach (var ml in _layers)
            {
                if (ml.Layer != layer) continue;
                int n = Settle(ml, i, j);
                if (n >= 0) return n;
            }
            return -1;
        }

        /// <summary>
        /// The node a POINT on cell (i, j) attaches to: the cell's own, unless the cell holds less
        /// than <see cref="AttachFillFraction"/> of copper — then the node of the fuller neighbour
        /// it shares the most copper edge with, repeated while that keeps getting fuller.
        /// </summary>
        /// <remarks>
        /// <b>R-rail32-1's whole 3.6×, on the field board.</b> A cell's node stands for the copper
        /// in it; current forced INTO a node has to leave through that copper's edges. A cell
        /// clipping a plane's corner holds a few square microns and shares a few microns of edge, so
        /// a source, a load or a via attached there pays a resistance set by where the grid lines
        /// happened to fall — 0.35 Ω on the field board's return, ten times the rest of the rail
        /// together, gone at the next pitch. Copper flowing THROUGH such a cell is priced correctly
        /// by its shared edges (<see cref="AddEdge"/>); it is only a point attachment that has to
        /// move. It moves one cell at most in practice, onto copper the point's own copper touches,
        /// so what it attaches to is still galvanically the same place.
        /// </remarks>
        private int Settle(MeshLayer ml, int i, int j)
        {
            int k = j * _grid.Nx + i;
            if (ml.Node[k] < 0) return -1;

            for (int hop = 0; hop < 4; hop++)
            {
                double fill = ml.Area[k] / ((double)_grid.Dx(k % _grid.Nx) * _grid.Dy(k / _grid.Nx));
                if (fill >= AttachFillFraction) break;

                int ci = k % _grid.Nx, cj = k / _grid.Nx;
                int best = -1;
                double bestShared = 0;
                void Try(int nk, double shared)
                {
                    if (ml.Node[nk] >= 0 && shared > bestShared && ml.Area[nk] > ml.Area[k])
                    { best = nk; bestShared = shared; }
                }
                if (ci + 1 < _grid.Nx) Try(k + 1, ml.ChordX[k]);
                if (ci > 0) Try(k - 1, ml.ChordX[k - 1]);
                if (cj + 1 < _grid.Ny) Try(k + _grid.Nx, ml.ChordY[k]);
                if (cj > 0) Try(k - _grid.Nx, ml.ChordY[k - _grid.Nx]);
                if (best < 0) break;
                k = best;
            }
            return ml.Node[k];
        }

        public int NodeTotal { get { Prepare(); return _nodeTotal; } }
        private int _nodeTotal;

        /// <summary>Layer, then row, then column — the order <see cref="Prepare"/> handed the nodes
        /// out in, so a node's name is the cell a reader would point at. Nothing here iterates a
        /// dictionary: an extraction that depended on a hash order would produce a drop map that
        /// moved between runs.</summary>
        public IEnumerable<int> NodesInNaturalOrder
        {
            get
            {
                Prepare();
                foreach (var ml in _layers)
                    for (int j = 0; j < _grid.Ny; j++)
                        for (int i = 0; i < _grid.Nx; i++)
                        {
                            int n = ml.Node[j * _grid.Nx + i];
                            if (n >= 0) yield return n;
                        }
            }
        }

        public string? NameOfNode(int node) =>
            CellOfNode(node) is { } cell
                ? $"{(cell.IsReference ? "ref" : "rail")}.{cell.Layer.Layer}_{cell.Layer.Datatype}." +
                  $"{cell.Ix}.{cell.Iy}"
                : null;

        /// <summary>
        /// Copper area per cell, by clipping a row band once and then each cell of it — and, where
        /// asked, how much copper each cell SHARES with its neighbour across the edge between them.
        /// </summary>
        /// <remarks>
        /// <b>The shared copper is the edge's conductance, not the two cells' areas</b> (R-rail32-2).
        /// <see cref="AddEdge"/> reads it. Both sides' own copper on the edge line is collected and
        /// only what both hold is counted, so copper that merely ENDS on a grid line, and two pieces
        /// that sit in adjacent cells without touching, share nothing and are not joined.
        /// </remarks>
        private double[] Rasterise(Paths64 copper, double[]? chordX = null, double[]? chordY = null)
        {
            var area = new double[_grid.Nx * _grid.Ny];
            if (copper.Count == 0) return area;

            var bounds = DrcRegions.BoundsOf(copper);
            bool chords = chordX is not null && chordY is not null;

            // The copper each cell of the PREVIOUS row holds on its top edge, and the copper the
            // previous cell of THIS row holds on its right edge — the other halves of the two edges
            // a cell shares with neighbours already visited.
            var belowTop = chords ? new List<(long, long)>?[_grid.Nx] : [];
            var rowTop = chords ? new List<(long, long)>?[_grid.Nx] : [];

            for (int j = 0; j < _grid.Ny; j++)
            {
                long y0 = _grid.Ys[j], y1 = _grid.Ys[j + 1];
                if (chords) { (belowTop, rowTop) = (rowTop, belowTop); Array.Clear(rowTop); }
                if (y1 <= bounds.MinY || y0 >= bounds.MaxY) continue;

                Paths64 band = [[new Point64(bounds.MinX, y0), new Point64(bounds.MaxX, y0),
                                 new Point64(bounds.MaxX, y1), new Point64(bounds.MinX, y1)]];
                var strip = Clipper.BooleanOp(ClipType.Intersection, copper, band, LayoutClipper.Rule);
                if (strip.Count == 0) continue;

                var sb = DrcRegions.BoundsOf(strip);
                List<(long, long)>? leftRight = null;
                int leftI = -2;

                for (int i = 0; i < _grid.Nx; i++)
                {
                    long x0 = _grid.Xs[i], x1 = _grid.Xs[i + 1];
                    if (x1 <= sb.MinX || x0 >= sb.MaxX) continue;

                    Paths64 cell = [[new Point64(x0, y0), new Point64(x1, y0),
                                     new Point64(x1, y1), new Point64(x0, y1)]];
                    var meet = Clipper.BooleanOp(ClipType.Intersection, strip, cell, LayoutClipper.Rule);
                    if (meet.Count == 0) continue;

                    double a = Math.Abs(Clipper.Area(meet));
                    if (!(a > 0)) continue;
                    int k = j * _grid.Nx + i;
                    area[k] = a;
                    if (!chords) continue;

                    if (leftI == i - 1 && leftRight is not null)
                        chordX![k - 1] = SharedLength(leftRight, OnEdge(meet, x0, vertical: true));
                    if (belowTop[i] is { } below)
                        chordY![k - _grid.Nx] = SharedLength(below, OnEdge(meet, y0, vertical: false));

                    leftRight = OnEdge(meet, x1, vertical: true);
                    leftI = i;
                    rowTop[i] = OnEdge(meet, y1, vertical: false);
                }
            }

            return area;
        }

        /// <summary>
        /// The stretches of a cell's clipped copper boundary that lie ON one of its edge lines — the
        /// copper that edge carries, seen from this side. One DBU of slack, because a clip's cut
        /// point is rounded to the integer grid.
        /// </summary>
        private static List<(long, long)> OnEdge(Paths64 meet, long at, bool vertical)
        {
            var spans = new List<(long, long)>();
            foreach (var path in meet)
                for (int n = 0; n < path.Count; n++)
                {
                    var p = path[n];
                    var q = path[(n + 1) % path.Count];
                    long pa = vertical ? p.X : p.Y, qa = vertical ? q.X : q.Y;
                    if (Math.Abs(pa - at) > 1 || Math.Abs(qa - at) > 1) continue;
                    long pb = vertical ? p.Y : p.X, qb = vertical ? q.Y : q.X;
                    if (pb != qb) spans.Add((Math.Min(pb, qb), Math.Max(pb, qb)));
                }
            return spans;
        }

        /// <summary>The length both sides of an edge hold copper along — the measure of the
        /// intersection of two sets of spans.</summary>
        private static double SharedLength(List<(long, long)> a, List<(long, long)> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            var ua = Merged(a);
            var ub = Merged(b);
            double shared = 0;
            int ia = 0, ib = 0;
            while (ia < ua.Count && ib < ub.Count)
            {
                long lo = Math.Max(ua[ia].Item1, ub[ib].Item1);
                long hi = Math.Min(ua[ia].Item2, ub[ib].Item2);
                if (hi > lo) shared += hi - lo;
                if (ua[ia].Item2 < ub[ib].Item2) ia++; else ib++;
            }
            return shared;
        }

        private static List<(long, long)> Merged(List<(long, long)> spans)
        {
            spans.Sort();
            var merged = new List<(long, long)>();
            foreach (var (lo, hi) in spans)
            {
                if (merged.Count > 0 && lo <= merged[^1].Item2)
                    merged[^1] = (merged[^1].Item1, Math.Max(merged[^1].Item2, hi));
                else
                    merged.Add((lo, hi));
            }
            return merged;
        }

        /// <summary>
        /// The copper area per cell where BOTH conductors have copper — R-rail14-3's overlap, which
        /// is what §4.1's shunt branch is of.
        ///
        /// <para><b>The real intersection, never the smaller of the two areas.</b> A cell holding a
        /// trace on one conductor and a plane on the other has two areas that both exceed their
        /// overlap, and on a board with a cutout, an antipad field or a split the two coppers are
        /// routinely in DIFFERENT parts of the same cell. min(A₁, A₂) would price a capacitance
        /// between two pieces of metal that do not face each other — optimistically, and by an amount
        /// that depends only on how the grid lines happened to fall.</para>
        ///
        /// <para>Empty where the two never meet, which is a real board: a rail pour that nowhere
        /// overlaps its reference has no plane capacitance and is not an error.</para>
        /// </summary>
        public double[] OverlapAreas(MeshLayer rail, MeshLayer reference)
        {
            Prepare();
            if (rail.Union.Count == 0 || reference.Union.Count == 0) return [];
            var meet = Clipper.BooleanOp(
                ClipType.Intersection, rail.Union, reference.Union, LayoutClipper.Rule);
            return meet.Count == 0 ? [] : Rasterise(meet);
        }

        public PdnCellRef CellRef(MeshLayer ml, int i, int j) =>
            new(ml.Layer, i, j,
                (_grid.Xs[i] + _grid.Xs[i + 1]) / 2,
                (_grid.Ys[j] + _grid.Ys[j + 1]) / 2,
                ml.IsReference);

        /// <summary>Every cell node covering (<paramref name="x"/>, <paramref name="y"/>) on the
        /// conductors of the requested side.</summary>
        public List<int> NodesAt(long x, long y, bool isReference)
        {
            Prepare();
            var hits = new List<int>();
            int i = IndexOf(_grid.Xs, x), j = IndexOf(_grid.Ys, y);
            if (i < 0 || j < 0) return hits;

            foreach (var ml in _layers)
            {
                if (ml.IsReference != isReference) continue;
                int n = Settle(ml, i, j);
                if (n >= 0 && !hits.Contains(n)) hits.Add(n);
            }
            return hits;
        }

        /// <summary>The nearest reference cell to a point, for a return that has no copper directly
        /// under the pad. Reported by the caller, never silent.</summary>
        public int NearestReferenceNode(long x, long y, out long distanceDbu)
        {
            Prepare();
            int best = -1;
            double bestD = double.MaxValue;

            foreach (var ml in _layers)
            {
                if (!ml.IsReference) continue;
                for (int j = 0; j < _grid.Ny; j++)
                    for (int i = 0; i < _grid.Nx; i++)
                    {
                        if (ml.Node[j * _grid.Nx + i] < 0) continue;
                        double cx = (_grid.Xs[i] + _grid.Xs[i + 1]) / 2.0;
                        double cy = (_grid.Ys[j] + _grid.Ys[j + 1]) / 2.0;
                        double d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                        if (d < bestD) { bestD = d; best = Settle(ml, i, j); }
                    }
            }

            distanceDbu = best < 0 ? 0 : (long)Math.Round(Math.Sqrt(bestD));
            return best;
        }

        private static int IndexOf(long[] lines, long v)
        {
            if (v < lines[0] || v > lines[^1]) return -1;
            int lo = 0, hi = lines.Length - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) / 2;
                if (lines[mid] <= v) lo = mid; else hi = mid;
            }
            return lo;
        }
    }

    // ── §4.1 at ω = 0 ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One resistor per cell edge, on EACH conductor.
    ///
    /// <para>The half-cell harmonic form is this file's header; the factor of two §4.1 states is the
    /// LOOP's and is paid by meshing both conductors, which is also this file's header. Read both
    /// before touching the expression below.</para>
    ///
    /// <para><b>This is the ONLY thing this extractor does that <see cref="PdnGraphExtractor"/> does
    /// differently.</b> Everything after it — vias, ground, series parts, shunts, sources, ports,
    /// ties and node numbering — is <see cref="PdnAssembly"/>'s, shared, and that sharing is what
    /// makes §4.6's "not a second simulator, a second reading of the geometry" true rather than
    /// asserted.</para>
    /// </summary>
    /// <summary>
    /// §4.1's <c>h</c> for every meshed conductor, and the one the provenance reports.
    ///
    /// <para><b>Per conductor, because a rail on the outer layer and a rail on an inner one are
    /// different distances from the same reference.</b> The REFERENCE's own edges take the nearest
    /// rail conductor's <c>h</c>: it is the pair the return current actually crosses, and a
    /// reference that took a far layer's separation would carry an inductance no field pays for.</para>
    ///
    /// <para>Zero where the stackup cannot give one — a rail whose reference is not a conductor of
    /// the stackup, or a pair with no dielectric between them. <b>Stated as a note rather than
    /// defaulted</b>: an assumed <c>h</c> would put a plausible inductance on every edge of the
    /// board and nothing would say where it came from.</para>
    /// </summary>
    private static Func<MeshLayer, double> PlaneSeparation(
        PdnExtractionRequest request, MeshBuilder mesh, LayerKey referenceLayer,
        List<string> notes, out double dominantMetres)
    {
        var conductors = PdnStackupGeometry.Conductors(request.Technology, request.DbuPerMicron);
        var reference = PdnStackupGeometry.ConductorOf(conductors, referenceLayer);

        var byLayer = new Dictionary<LayerKey, double>();
        double nearest = 0;

        foreach (var ml in mesh.Layers)
        {
            if (ml.IsReference) continue;
            double h = PdnStackupGeometry.SeparationMetres(
                PdnStackupGeometry.ConductorOf(conductors, ml.Layer), reference);
            byLayer[ml.Layer] = h;
            if (h > 0 && (nearest == 0 || h < nearest)) nearest = h;
        }

        dominantMetres = nearest;
        double dominant = nearest;

        if (request.FrequencyHz > 0 && !(nearest > 0))
            notes.Add(
                "The stackup gives no dielectric separation between this rail's copper and its " +
                "reference, so no cell edge carries an inductance and this extraction is the " +
                "resistive mesh at a non-zero frequency. State the dielectric thicknesses in the " +
                "stackup — L = µ₀·h is the whole of §4.1's low band.");
        else if (request.FrequencyHz > 0 &&
                 byLayer.Values.Any(v => v > 0 && Math.Abs(v - nearest) > 1e-12))
            notes.Add(
                $"This rail's copper sits on conductors at different distances from its reference, " +
                $"from {byLayer.Values.Where(v => v > 0).Min() * 1e6:0.#} µm to " +
                $"{byLayer.Values.Max() * 1e6:0.#} µm. Each carries its own L = µ₀·h and the " +
                $"reference carries the nearest pair's, which is {nearest * 1e6:0.#} µm.");

        return ml => ml.IsReference
            ? dominant
            : byLayer.TryGetValue(ml.Layer, out double v) ? v : dominant;
    }

    /// <summary>
    /// R-rail13-2's stated condition: the lowest frequency at which any meshed conductor reaches
    /// two skin depths.
    ///
    /// <para>Below it the R matrix does not depend on frequency at all, which is a genuine
    /// performance property and the reason it is REPORTED rather than merely obeyed — a caller
    /// reusing one factorisation across a sweep has to know where the reuse stops being valid, and
    /// on the thin inner copper these boards use that is a long way up.</para>
    /// </summary>
    private static double SkinCrossover(MeshBuilder mesh, double frequencyHz, List<string> notes)
    {
        double lowest = double.PositiveInfinity;
        string which = "";

        foreach (var ml in mesh.Layers)
        {
            double f = PdnInductance.SkinCrossoverHz(
                ml.Conductor.ThicknessMetres, ml.Conductor.ConductivitySm);
            if (f < lowest) { lowest = f; which = ml.Conductor.StackupName; }
        }

        if (double.IsFinite(lowest) && frequencyHz > lowest)
            notes.Add(
                $"At {frequencyHz / 1e6:0.###} MHz the copper on '{which}' is thicker than two skin " +
                $"depths ({lowest / 1e6:0.###} MHz), so its sheet resistance is ρ/(2δ) rather than " +
                "ρ/T and this extraction's R matrix is NOT the DC one. Below that crossover it is, " +
                "and only L and the solve change per point.");

        return lowest;
    }

    /// <summary>
    /// §4.1's medium for every meshed rail conductor — the dielectric the shunt branch is of,
    /// combined in series by <see cref="PdnCavity.MediumBetween"/>.
    ///
    /// <para><b>Per conductor for the reason <see cref="PlaneSeparation"/> is per conductor</b>: a
    /// rail on the outer layer and a rail on an inner one are different distances from the same
    /// reference AND cross different dielectrics to reach it. The reference's own cells take
    /// nothing from this — a cell's capacitance is stamped once, between the two conductors it is
    /// between, and the reference is the far plate of that one element rather than a second one.</para>
    ///
    /// <para>Absent where the stackup puts no dielectric between the pair, which is reported once
    /// and never defaulted: an assumed FR-4 would put a plausible capacitance on every cell of the
    /// board and nothing would say where it came from.</para>
    /// </summary>
    private static Dictionary<LayerKey, PdnMedium> PlaneMedia(
        PdnExtractionRequest request, MeshBuilder mesh, LayerKey referenceLayer, List<string> notes)
    {
        var conductors = PdnStackupGeometry.Conductors(request.Technology, request.DbuPerMicron);
        var reference = PdnStackupGeometry.ConductorOf(conductors, referenceLayer);
        var media = new Dictionary<LayerKey, PdnMedium>();
        var missing = new List<LayerKey>();

        foreach (var ml in mesh.Layers)
        {
            if (ml.IsReference || media.ContainsKey(ml.Layer)) continue;
            var medium = PdnCavity.MediumBetween(
                request.Technology, PdnStackupGeometry.ConductorOf(conductors, ml.Layer), reference);
            if (medium is null) missing.Add(ml.Layer);
            else media[ml.Layer] = medium;
        }

        if (missing.Count > 0)
            notes.Add(
                "The stackup states no dielectric between this rail's copper on " +
                string.Join(", ", missing.Select(k => $"{k.Layer}/{k.Datatype}")) +
                " and its reference, so that copper carries no plane capacitance and no dielectric " +
                "loss. State the dielectric entries between them — C = ε₀εᵣA/h and G = ωC·tan δ are " +
                "the whole of §4.1's shunt branch.");

        // R-rail14-3. tan δ is one of the two numbers §2.2 names as most often wrong, and a class
        // default is not a measurement: every peak height in the cavity band computed from one is
        // INDICATIVE, which is the same word and the same treatment brief 11 gives an ESR.
        foreach (var m in media.Values.Where(m => m.TanDeltaIsClassDefault).Take(1))
            notes.Add(
                $"The stackup states no tan δ for this rail's dielectric, so railRF used " +
                $"{m.TanDelta:0.####} — {m.Basis}. Every peak height in the cavity band is therefore " +
                $"{RailEsrDefaults.Marking}: tan δ sets how sharp a plane resonance is, which is the " +
                "difference between a 6 dB bump and a 20 dB one.");

        return media;
    }

    /// <summary>
    /// §4.1's last two terms, one pair per cell where BOTH conductors have copper:
    /// <c>C = ε₀εᵣ·A/h</c> to the reference plane, and <c>G = ω·C·tan δ</c> across it.
    ///
    /// <para><b>The total it returns is the readout §9 asks for</b> — "railRF shows the extracted
    /// plane capacitance as a single number early and prominently, because a designer recognises a
    /// wrong one instantly and would never notice it buried in a curve." It is summed HERE, over the
    /// same cells that were stamped and from the same expression, rather than recomputed from an
    /// outline afterwards: a readout that could differ from the model it describes is worse than no
    /// readout, because it would be believed.</para>
    ///
    /// <para><b>Nothing is stamped at ω = 0 and the total is still computed.</b> §2.8: at DC the
    /// shunt branch vanishes and the system stays real, symmetric and positive-definite — but the
    /// stackup sanity check is exactly as valuable on a DC run, and it costs one multiplication per
    /// cell.</para>
    /// </summary>
    private static double StampCavity(
        MeshBuilder mesh, PdnAssembly asm, int dbuPerMicron, double frequencyHz,
        IReadOnlyDictionary<LayerKey, PdnMedium> media, out double overlapSquareMetres)
    {
        double dbuPerMetre = dbuPerMicron * 1e6;
        double perSquareDbu = dbuPerMetre * dbuPerMetre;
        var grid = mesh.Grid;

        double total = 0;
        overlapSquareMetres = 0;

        foreach (var ml in mesh.Layers)
        {
            if (ml.IsReference || !media.TryGetValue(ml.Layer, out var medium)) continue;

            foreach (var rl in mesh.Layers)
            {
                if (!rl.IsReference) continue;

                var overlap = mesh.OverlapAreas(ml, rl);
                if (overlap.Length == 0) continue;

                for (int j = 0; j < grid.Ny; j++)
                    for (int i = 0; i < grid.Nx; i++)
                    {
                        int k = j * grid.Nx + i;
                        int a = ml.Node[k], b = rl.Node[k];
                        if (a < 0 || b < 0 || !(overlap[k] > 0)) continue;

                        double area = overlap[k] / perSquareDbu;
                        double c = PdnCavity.CapacitanceFarads(
                            medium.EpsilonR, area, medium.ThicknessMetres);
                        if (!(c > 0)) continue;

                        total += c;
                        overlapSquareMetres += area;

                        if (!(frequencyHz > 0)) continue;

                        asm.StageCavityShunt(
                            $"cavity.{ml.Layer.Layer}_{ml.Layer.Datatype}.{i}.{j}", a, b,
                            c, PdnCavity.ConductanceSiemens(frequencyHz, c, medium.TanDelta),
                            $"{area * 1e6:0.###} mm² of plane pair over " +
                            $"{medium.ThicknessMetres * 1e6:0.#} µm of εr {medium.EpsilonR:0.###}",
                            mesh.CellRef(ml, i, j), mesh.CellRef(rl, i, j));
                    }
            }
        }

        return total;
    }

    private static void StampMesh(
        MeshBuilder mesh, PdnAssembly asm, int dbuPerMicron,
        double frequencyHz, Func<MeshLayer, double> separationOf)
    {
        var grid = mesh.Grid;

        foreach (var ml in mesh.Layers)
        {
            // R-rail13-2. Below two skin depths this IS σ·T, so a 1 oz board's whole R matrix is
            // the DC one anywhere under 14 MHz and only L and the solve change per point.
            double sigmaT = PdnInductance.SheetSiemensPerSquare(
                frequencyHz, ml.Conductor.ThicknessMetres, ml.Conductor.ConductivitySm);
            if (!(sigmaT > 0)) continue;

            // ── R-rail13-1, AND THE SAME FACTOR OF TWO THIS FILE'S HEADER IS ABOUT ─────────────
            //
            // §4.1 states the plane pair's per-square loop inductance as L = µ₀·h, exactly as it
            // states the loop's resistance as R = 2·Rs. This extractor pays that 2 BY MESHING BOTH
            // CONDUCTORS — each edge carries one plane's Rs and the loop traverses two of them.
            //
            // THE INDUCTANCE HAS TO BE SPLIT THE SAME WAY, AND IT IS THE ASYMMETRY THAT MAKES THIS
            // EASY TO GET WRONG: §4.1 writes the resistance with its 2 visible and the inductance
            // without one, so µ₀·h copied onto each edge puts 2·µ₀·h round the loop and doubles
            // every plane-pair inductance in the tool. Half on each conductor is what makes the
            // loop µ₀·h — measured round the loop, which is how PdnDistributedTests gates it and
            // how PdnMeshExtractorTests already gates the resistance.
            double half = frequencyHz > 0
                ? PdnInductance.SquareInductanceHenries(separationOf(ml)) / 2.0
                : 0.0;

            double dbuPerMetre = dbuPerMicron * 1e6;
            string side = ml.IsReference ? "reference" : "rail";

            for (int j = 0; j < grid.Ny; j++)
                for (int i = 0; i < grid.Nx; i++)
                {
                    int k = j * grid.Nx + i;
                    int a = ml.Node[k];
                    if (a < 0) continue;

                    if (i + 1 < grid.Nx && ml.Node[k + 1] >= 0)
                        AddEdge(mesh, asm, ml, i, j, i + 1, j, a, ml.Node[k + 1],
                                grid.Dx(i), grid.Dx(i + 1), ml.ChordX[k],
                                sigmaT, half, dbuPerMetre, side, "x");

                    if (j + 1 < grid.Ny && ml.Node[k + grid.Nx] >= 0)
                        AddEdge(mesh, asm, ml, i, j, i, j + 1, a, ml.Node[k + grid.Nx],
                                grid.Dy(j), grid.Dy(j + 1), ml.ChordY[k],
                                sigmaT, half, dbuPerMetre, side, "y");
                }
        }
    }

    private static void AddEdge(
        MeshBuilder mesh, PdnAssembly asm,
        MeshLayer ml, int i0, int j0, int i1, int j1, int a, int b,
        long d0Dbu, long d1Dbu, double sharedDbu,
        double sigmaT, double halfLoopPerSquareH, double dbuPerMetre, string side, string axis)
    {
        // ── R-rail32-2: THE EDGE CONDUCTS THROUGH THE COPPER IT CARRIES ────────────────────────
        //
        // A centre-to-centre run of (d0 + d1)/2 through the copper the two cells actually share
        // across the edge between them: R = run / (σ·T·shared). On an axis-aligned trace the shared
        // copper IS the trace's width in that row, so wherever the copper is uniform along the cell
        // this is exactly the number the half-cell form dx²/(2·σ·T·A) gave. It is the DIAGONAL
        // that differs, and there the half-cell form was wrong: it read each cell as copper spread
        // evenly across the whole cell, so every partial cell along both edges of a 45° trace
        // became a series bottleneck and a trace three cells wide read 19 % high. With the edge
        // priced by its own copper a uniform field across ANY straight trace, at any angle and any
        // pitch, satisfies the discrete equations exactly — the flux through each edge is the true
        // flux through the copper on it, and a cut cell's walls carry none.
        //
        // And two cells that both hold copper but share none across their edge — copper ending on
        // the grid line, two pieces side by side in adjacent cells — are not joined at all. The
        // area form joined them.
        if (!(sharedDbu > 0)) return;

        double d0 = d0Dbu / dbuPerMetre, d1 = d1Dbu / dbuPerMetre;
        double length = (d0 + d1) / 2.0;
        double width = sharedDbu / dbuPerMetre;
        double r = length / (sigmaT * width);

        // ── §4.1's aspect rule, arrived at rather than applied ────────────────────────────────
        //
        // "For non-square cells L and R scale by the aspect ratio (along/across)." The ratio below
        // IS that — the edge's SQUARE COUNT, the same number the resistance is Rs times, so L and R
        // scale together by construction and cannot come to disagree about what a non-square cell
        // is. A uniform grid of side Δ fully in copper gives exactly 1 and a 2:1 cell exactly 2.
        double squares = length / width;
        double l = halfLoopPerSquareH > 0 ? halfLoopPerSquareH * squares : 0.0;

        asm.StageCopper(
            $"mesh.{side}.{ml.Layer.Layer}_{ml.Layer.Datatype}.{i0}.{j0}.{axis}", a, b, r,
            $"{length * 1e3:0.###} mm of {width * 1e3:0.###} mm {ml.Conductor.StackupName} copper",
            mesh.CellRef(ml, i0, j0), mesh.CellRef(ml, i1, j1),
            PdnOriginKind.MeshEdge, length, width, l > 0 ? l : null);
    }

    /// <summary>
    /// One rail, resolved up to the choice of pitch — <see cref="PdnMeshExtractor.Plan"/>. Meshing it
    /// is <see cref="At"/>, which can be asked for more than one pitch; nothing it holds is changed by
    /// meshing, so two pitches are two independent readings of the same copper.
    /// </summary>
    internal sealed class PdnMeshPlan
    {
        private readonly PdnExtractionRequest request;
        private readonly LayerKey referenceLayer;
        private readonly PdnRailRegionSet regions;
        private readonly Dictionary<LayerKey, PdnConductor> byLayer;
        private readonly List<(long X, long Y)> anchorSeeds;
        private readonly List<string> planDiagnostics;
        private readonly List<string> planNotes;

        /// <summary>The narrowest copper on the rail, in DBU — what every pitch is a fraction of.</summary>
        public long MinFeatureDbu { get; }

        /// <summary>Non-null where the rail cannot be meshed at any pitch.</summary>
        public PdnExtraction? Refusal { get; }

        /// <summary>A refusal is a plan that meshes nothing — so the plan's own early returns read
        /// exactly as the extraction's always did.</summary>
        public static implicit operator PdnMeshPlan(PdnExtraction refusal) => new(refusal);

        internal PdnMeshPlan(PdnExtraction refusal)
        {
            Refusal = refusal;
            request = null!; regions = null!; byLayer = null!; anchorSeeds = null!;
            planDiagnostics = null!; planNotes = null!;
        }

        internal PdnMeshPlan(
            PdnExtractionRequest request, LayerKey referenceLayer, PdnRailRegionSet regions,
            Dictionary<LayerKey, PdnConductor> byLayer, List<(long X, long Y)> anchorSeeds,
            long minFeatureDbu, List<string> diagnostics, List<string> notes)
        {
            this.request = request;
            this.referenceLayer = referenceLayer;
            this.regions = regions;
            this.byLayer = byLayer;
            this.anchorSeeds = anchorSeeds;
            MinFeatureDbu = minFeatureDbu;
            planDiagnostics = diagnostics;
            planNotes = notes;
        }

        /// <summary>The rail meshed with <paramref name="cellsAcross"/> cells across its narrowest
        /// copper — or at the stated cell size, which <paramref name="cellsAcross"/> does not
        /// override.</summary>
        public PdnExtraction At(int cellsAcross)
        {
            if (Refusal is not null) return Refusal;

            var rail = request.Rail;
            var tech = request.Technology;
            var diagnostics = new List<string>(planDiagnostics);
            var notes = new List<string>(planNotes);
            double celsius = request.Settings.CopperTemperatureCelsius;
            long minFeature = MinFeatureDbu;

                // ── the cell size (R-rail3-14) ─────────────────────────────────────────────────────────
                double dbuPerMetre = request.DbuPerMicron * 1e6;
                long baseDeltaDbu;
                string cellBasis;

                // ── R-rail14-2: BOTH rules bind now, and the cell is the smaller of the two ────────────
                //
                // WavelengthCellSizeMetres carries the whole argument; what is here is only the choice. The
                // basis names which one bound, because the two fail in opposite directions and neither
                // failure reports anything: too coarse for the feature loses a thin trace's resistance, too
                // coarse for the wavelength loses the resonance the cavity band exists to find.
                double epsilonR = LargestEpsilonR(tech);
                double lambdaMetres = WavelengthCellSizeMetres(request.FrequencyHz, epsilonR);
                long lambdaDbu = double.IsFinite(lambdaMetres) && lambdaMetres > 0
                    ? Math.Max(1, (long)Math.Floor(lambdaMetres * dbuPerMetre))
                    : long.MaxValue;

                if (request.Mesh.CellSizeMetres is { } stated && stated > 0)
                {
                    baseDeltaDbu = Math.Max(1, (long)Math.Round(stated * dbuPerMetre));
                    cellBasis = $"stated: {stated * 1e3:0.###} mm";

                    // A STATED size is honoured and never silently narrowed — it is the knob the convergence
                    // sweeps turn — but a stated size coarser than the wavelength rule produces a curve with
                    // no resonance in it and looks entirely normal, so it is said.
                    if (baseDeltaDbu > lambdaDbu)
                        notes.Add(
                            $"The stated cell size, {stated * 1e3:0.###} mm, is coarser than the " +
                            $"{lambdaMetres * 1e3:0.###} mm the shortest wavelength asks for at " +
                            $"{PdnMask.Hertz(request.FrequencyHz)} in εr {epsilonR:0.###} (λ/20). A cell longer " +
                            "than that cannot carry the phase across itself, so a plane resonance in this " +
                            "band is not in the model at all — the curve will look smooth and be missing it.");
                }
                else
                {
                    int across = Math.Max(1, cellsAcross);
                    long featureDbu = Math.Max(1, minFeature / across);
                    string featureBasis =
                        $"the rail's narrowest copper, {minFeature / dbuPerMetre * 1e3:0.###} mm, " +
                        $"at {across} cells across it";

                    if (lambdaDbu < featureDbu)
                    {
                        baseDeltaDbu = Math.Max(1, lambdaDbu);
                        cellBasis =
                            $"the shortest wavelength at {PdnMask.Hertz(request.FrequencyHz)} in εr " +
                            $"{epsilonR:0.###}, λ/20 = {lambdaMetres * 1e3:0.###} mm — smaller here than " +
                            featureBasis;
                    }
                    else
                    {
                        baseDeltaDbu = featureDbu;
                        cellBasis = featureBasis +
                            (lambdaDbu == long.MaxValue
                                ? ""
                                : $" — smaller here than λ/20, {lambdaMetres * 1e3:0.###} mm");
                    }
                }

                // ── the reference extent, applied HERE and stamped (R-rail3-5) ─────────────────────────
                var extent = ExtentOf(regions, anchorSeeds, baseDeltaDbu);

                if (ResolveReferenceCopper(request, regions, referenceLayer, extent, notes,
                                           out var referenceCopper) is { } extentRefusal)
                    return PdnExtraction.Refused(extentRefusal, regions);

                // ── the grid and the cells, under the ceiling (R-rail32-1) ─────────────────────────────
                //
                // THE CEILING COUNTS CELLS THAT EXIST. It used to count the bounding grid — every cell of
                // the extent, times two conductors — and a cell exists only where there is copper: on the
                // field board that read 975,000 for a mesh of 340,576, so the ceiling bound when it had no
                // business to, coarsened 84 µm to 131 µm, and the provenance went on saying 84. So the mesh
                // is BUILT and then counted; a build over the ceiling is thrown away and the pitch widened
                // by what the count says it needs. The ordinary case builds once.
                //
                // Refinement goes before the base pitch, exactly as it did: at each pitch the refined grid
                // is tried first and the plain one second, and only when neither fits does the pitch widen.
                var refineBands = RefinementBands(rail, request, baseDeltaDbu);
                int ratio = Math.Max(1, request.Mesh.PortRefinementRatio);
                int maxCells = Math.Max(1, request.Mesh.MaxCells);
                long spanX = Math.Max(1, extent.MaxX - extent.MinX), spanY = Math.Max(1, extent.MaxY - extent.MinY);

                // Only a guard on MEMORY: every conductor holds a few numbers per cell of the bounding grid,
                // copper or not. Far above anything the copper count lets through on a real board.
                long delta = baseDeltaDbu;
                while ((double)(spanX / delta + 1) * (spanY / delta + 1) > BoundingGridCeiling(maxCells)
                       && delta < Math.Max(spanX, spanY))
                    delta = (long)Math.Ceiling(delta * 1.25);

                PdnGrid grid;
                MeshBuilder mesh;
                bool refined = ratio > 1 && refineBands.Count > 0;
                bool refinementDropped = false;
                while (true)
                {
                    grid = PdnGrid.Build(extent, delta, refined ? refineBands : [], ratio);
                    mesh = new MeshBuilder(grid, byLayer, referenceLayer);

                    foreach (var island in regions.Power)
                        foreach (var (layer, paths) in island.Copper)
                            if (layer != referenceLayer) mesh.AddCopper(layer, paths, isReference: false);
                    foreach (var (layer, paths) in referenceCopper)
                        mesh.AddCopper(layer, paths, isReference: true);

                    int cells = mesh.CellCount;
                    if (cells <= maxCells || delta >= Math.Max(spanX, spanY)) break;
                    if (refined) { refined = false; refinementDropped = true; continue; }

                    delta = Math.Max(delta + 1, (long)Math.Ceiling(delta * Math.Max(1.1, Math.Sqrt((double)cells / maxCells) * 1.05)));
                    refined = ratio > 1 && refineBands.Count > 0;
                    refinementDropped = false;
                }

                foreach (var island in regions.Power)
                    foreach (var (layer, _) in island.Copper)
                        if (layer == referenceLayer)
                        {
                            diagnostics.Add(
                                $"The rail has copper on layer {layer.Layer}/{layer.Datatype}, which is also " +
                                "its reference layer. That copper was not meshed as part of the rail — a " +
                                "conductor cannot be its own return.");
                            break;
                        }

                var format = request.LengthFormat;
                if (delta != baseDeltaDbu)
                {
                    double acrossNow = (double)minFeature / delta;
                    cellBasis += $"; coarsened to {format.Length(delta)} by the {maxCells:N0}-cell ceiling, " +
                                 $"which leaves the narrowest copper {acrossNow:0.#} cells across";
                    notes.Add(
                        $"The mesh was coarsened from {format.Length(baseDeltaDbu)} to {format.Length(delta)} to " +
                        $"stay under the {maxCells:N0}-cell ceiling, so the rail's narrowest copper is " +
                        $"{acrossNow:0.#} cells across rather than {Math.Max(1, cellsAcross)}. " +
                        "A straight run is still priced exactly at any pitch; what a coarser cell loses is the " +
                        "shape — a neck, a bend, the spreading under a pin. Raise the ceiling to get it back.");
                }

                if (refined)
                    notes.Add(
                        $"The mesh is refined {ratio}× under {refineBands.Count} port region(s). That is a " +
                        "correctness requirement, not a tidiness one: a coarse mesh under a pin field " +
                        "under-estimates the spreading resistance, and it does so optimistically.");
                else if (refinementDropped)
                    notes.Add(
                        $"Local refinement under {refineBands.Count} port region(s) was NOT applied — it " +
                        $"would have taken the mesh past the {maxCells:N0}-cell ceiling. The port " +
                        "resistances here are therefore optimistic; raise the ceiling or state a " +
                        "coarser base cell size to get it back.");

                // What the provenance's cell count IS — the one number a reader cannot otherwise check.
                notes.Add(
                    $"The mesh is {mesh.CellCount:N0} cells — {mesh.RailCellCount:N0} on the rail's copper and " +
                    $"{mesh.CellCount - mesh.RailCellCount:N0} on its reference — cut from a {grid.Nx:N0} × " +
                    $"{grid.Ny:N0} grid at {format.Length(delta)}" +
                    (refined ? $", refined {ratio}× under the ports" : "") +
                    ". A cell exists only where there is copper.");

                if (mesh.CellCount == 0)
                    return PdnExtraction.Refused(
                        $"Rail '{rail.Name}' meshed to no cells at all, which means the grid and the copper do " +
                        "not overlap. This is a coordinate-system disagreement rather than a design problem.",
                        regions);

                // ── §4.1's h, and R-rail13-2's stated condition ────────────────────────────────────────
                var separation = PlaneSeparation(request, mesh, referenceLayer, notes, out double h);
                double crossover = SkinCrossover(mesh, request.FrequencyHz, notes);

                // ── the netlist ────────────────────────────────────────────────────────────────────────
                var media = PlaneMedia(request, mesh, referenceLayer, notes);

                var asm = new PdnAssembly(request, mesh, PdnModelKind.Accurate, celsius, notes, diagnostics);
                StampMesh(mesh, asm, request.DbuPerMicron, request.FrequencyHz, separation);

                // ── R-rail14-3: the readout §9 says must not be buried ─────────────────────────────────
                double planeCapacitance = StampCavity(
                    mesh, asm, request.DbuPerMicron, request.FrequencyHz, media, out double overlapArea);

                if (asm.Build() is { } refusal) return PdnExtraction.Refused(refusal, regions);

                var dominantMedium = media.Values
                    .OrderByDescending(m => m.ThicknessMetres > 0 ? 1 : 0)
                    .FirstOrDefault();

                var provenance = new PdnProvenance
                {
                    ModelKind = PdnModelKind.Accurate,
                    Model = "Accurate (mesh)",
                    RailName = rail.Name,
                    ReferenceExtent = rail.ReferenceExtent,
                    FrequencyHz = request.FrequencyHz,
                    PlaneSeparationMetres = h,
                    SkinCrossoverHz = crossover,
                    CellSizeMetres = delta / dbuPerMetre,
                    CellSizeBasis = cellBasis,
                    PlaneCapacitanceFarads = planeCapacitance,

                    // R-rail18-2b. The mesh always computes it; the only zero it can produce that is ABOUT
                    // the stackup is the one where no rail conductor had a medium to be of.
                    PlaneCapacitanceBasis = media.Count > 0
                        ? PdnPlaneCapacitanceBasis.Computed
                        : PdnPlaneCapacitanceBasis.NoDielectricStated,
                    PlaneOverlapSquareMetres = overlapArea,
                    RelativePermittivity = dominantMedium?.EpsilonR ?? 0,
                    LossTangent = dominantMedium?.TanDelta ?? 0,
                    LossTangentIsClassDefault = dominantMedium?.TanDeltaIsClassDefault ?? false,
                    DielectricBasis = dominantMedium?.Basis ?? "",
                    ShuntBranchPresent = request.FrequencyHz > 0 && planeCapacitance > 0,
                    PortRefinementRatio = refined ? ratio : 1,
                    CopperTemperatureCelsius = celsius,
                    CellCount = mesh.CellCount,
                    MeshedAreaSquareMetres = mesh.MeshedAreaSquareDbu / (dbuPerMetre * dbuPerMetre),
                    IslandReport = regions.IslandReport,
                    ReturnNet = regions.ReturnNet,
                    ReferencePoint = asm.ReferencePoint,
                    UnresolvedViaSpans = asm.UnresolvedViaSpans,
                    Notes = notes,
                };

                return new PdnExtraction(null, asm.Finish(provenance), regions, diagnostics);
            }

    }
}
