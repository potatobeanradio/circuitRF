// The ACCURATE reading: a mesh of unit cells over the real copper, at DC
// (docs/sonnet-briefs/brief-railrf-3-mesh-extractor.md R-rail3-6 … R-rail3-8, railrf.md §4.1).
//
// ── AT DC ONLY, AND DC IS THE FIRST POINT OF THE SWEEP ─────────────────────────────────────────
//
// Set ω = 0 and §4.1's inductance and shunt branch both vanish; what is left is a purely resistive
// mesh — real, symmetric, positive-definite and fast. Brief 13 adds L, brief 14 adds the shunt.
// §2.8 is explicit that this is not a mode bolted on: ONE EXTRACTOR, ONE MESH, ONE SOLVER is what
// stops a DC answer and an AC answer drifting apart.
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
// ── WHY THE CONDUCTANCES ARE BUILT FROM AREAS AND NOT FROM CELL COUNTS ─────────────────────────
//
// A binary present/absent cell makes a conductor's width a multiple of Δ, so a 0.3 mm trace meshed
// at 0.1 mm is right and the same trace meshed at 0.13 mm is 33 % wrong — silently, and in whichever
// direction the rounding fell. Instead each cell carries the AREA of copper actually inside it, and
// an edge is the series pair of the two half-cells it joins:
//
//     half-cell resistance along x   =  dx² / (2 · σ · T · A)        A = copper area in the cell
//     R(edge)                        =  half(i) + half(i+1)
//
// which is the ordinary finite-volume harmonic average. On a straight trace of ANY width and ANY
// alignment this sums, exactly, to ρ·L/(W·T) — the closed form §7 gates against — because the areas
// in a column always add up to the real width whether or not the grid lines fall on the edges.
// That is what lets the DC mesh be coarse (R-rail3-14) without being optimistic.

using Clipper2Lib;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout.Drc;
using CircuitRF.Design.RailRf;

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

    /// <summary>The ceiling on cells, power and reference together. A mesh above it is COARSENED and
    /// the coarsening is reported — never silently truncated, and never run to exhaustion.</summary>
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

    /// <summary>The board's pads, as the board netlist or a placement join knows them.</summary>
    public IReadOnlyList<PdnPad> Pads { get; init; } = [];

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

    /// <summary>How finely, and where.</summary>
    public PdnMeshSettings Mesh { get; init; } = new();
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
    /// <b>The OTHER cell-size rule, stated here so a later reader cannot find only one of them.</b>
    ///
    /// <para>§4.1 sizes cells by the shortest wavelength in the dielectric, Δ ≤ λ_min/20 — 7.2 mm at
    /// 1 GHz on FR-4, 1.4 mm at 5 GHz. That rule exists to resolve the CAVITY, and it binds from
    /// brief 14 onward.</para>
    ///
    /// <para><b>At DC the binding constraint is not wavelength at all — it is GEOMETRY.</b> A cell
    /// must resolve the narrowest conductor that carries current, or a 0.15 mm trace becomes a cell
    /// wide and its resistance is wrong by whatever the cell size is. So the DC cell size comes from
    /// <see cref="MinimumFeatureWidthDbu"/>, with local refinement under every port region, and this
    /// method is not called at ω = 0.</para>
    ///
    /// <para>A later reader who sees only the wavelength rule will "fix" the DC mesh to it and
    /// quietly lose every thin trace: 7.2 mm cells on 0.3 mm traces. That is why both rules are in
    /// one file, each with the reason it exists.</para>
    /// </summary>
    /// <param name="frequencyHz">The top of the band being solved.</param>
    /// <param name="epsilonR">The dielectric's relative permittivity.</param>
    public static double WavelengthCellSizeMetres(double frequencyHz, double epsilonR)
    {
        const double C0 = 299_792_458.0;
        if (!(frequencyHz > 0) || !(epsilonR > 0)) return double.PositiveInfinity;
        return C0 / (frequencyHz * Math.Sqrt(epsilonR)) / 20.0;
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
        var layerRegions = BuildLayerRegions(request.Shapes, tech);
        if (layerRegions.Count == 0)
            return PdnExtraction.Refused(
                "This artwork flattens to no copper at all. Check that the layout view carries the " +
                "board's shapes and that its technology names the layers they are on.");

        // ── which copper is this rail (R-rail3-3) ──────────────────────────────────────────────
        var anchorSeeds = new List<(long X, long Y)>();
        foreach (var s in rail.Sources) anchorSeeds.AddRange(PdnAttachments.Resolve(s.Anchor, request.Pads));
        foreach (var l in rail.Loads) anchorSeeds.AddRange(PdnAttachments.Resolve(l.Anchor, request.Pads));

        var regions = PdnRailRegions.Walk(
            layerRegions, tech, request.NetPoints, rail.NetName,
            referenceLayer, request.ReferenceNet, anchorSeeds);

        diagnostics.AddRange(regions.Diagnostics);

        if (regions.Power.Count == 0)
            return PdnExtraction.Refused(
                $"Rail '{rail.Name}' resolves to no copper. " +
                (rail.NetName is { Length: > 0 } net
                    ? $"Nothing on this board is on net '{net}', and no source or load anchor landed on " +
                      "metal. Check the net name against the board netlist."
                    : "The rail names no net, and no source or load anchor landed on metal. Give the " +
                      "rail its net name, or anchor a source or a load on the rail's own copper."),
                regions);

        // ── the conductors, and what each square of them costs ─────────────────────────────────
        double celsius = request.Settings.CopperTemperatureCelsius;
        var conductors = new List<PdnConductor>();
        var byLayer = new Dictionary<LayerKey, PdnConductor>();

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
                return PdnExtraction.Refused(
                    $"The rail reaches layer {layer.Layer}/{layer.Datatype}, which no Conductor entry " +
                    "of the stackup claims. Map that drawing layer onto a conductor in the technology's " +
                    "stackup, or the copper on it has no thickness and no resistance.", regions);

            if (!(c.ThicknessMetres > 0))
                return PdnExtraction.Refused(
                    $"Stackup conductor '{c.StackupName}' states no thickness, so its copper has no " +
                    "sheet resistance and the mesh on it would be a perfect plane. State its finished " +
                    "copper thickness.", regions);

            if (!(c.ConductivitySm > 0))
                return PdnExtraction.Refused(
                    $"Stackup conductor '{c.StackupName}' states no conductivity, so its copper has no " +
                    "sheet resistance and the mesh on it would be a perfect plane. State its " +
                    "conductivity in S/m — copper is 5.8e7 at 20 °C.", regions);
        }

        // ── the cell size (R-rail3-14) ─────────────────────────────────────────────────────────
        long minFeature = MinimumFeatureWidthDbu(regions.Power);
        double dbuPerMetre = request.DbuPerMicron * 1e6;
        long baseDeltaDbu;
        string cellBasis;

        if (request.Mesh.CellSizeMetres is { } stated && stated > 0)
        {
            baseDeltaDbu = Math.Max(1, (long)Math.Round(stated * dbuPerMetre));
            cellBasis = $"stated: {stated * 1e3:0.###} mm";
        }
        else
        {
            int across = Math.Max(1, request.Mesh.CellsAcrossMinimumFeature);
            baseDeltaDbu = Math.Max(1, minFeature / across);
            cellBasis = $"the rail's narrowest copper, {minFeature / dbuPerMetre * 1e3:0.###} mm, " +
                        $"at {across} cells across it";
        }

        // ── the reference extent, applied HERE and stamped (R-rail3-5) ─────────────────────────
        var extent = ExtentOf(regions, anchorSeeds, baseDeltaDbu);

        var referenceCopper = new List<(LayerKey Layer, Paths64 Paths)>();
        switch (rail.ReferenceExtent)
        {
            case RailReferenceExtent.AsImported:
                foreach (var island in regions.Reference) referenceCopper.AddRange(island.Copper);
                break;

            case RailReferenceExtent.FilledToOutline:
                if (request.BoardOutline is not { Count: > 0 } outline)
                    return PdnExtraction.Refused(
                        $"Rail '{rail.Name}' asks for its reference to be filled to the board outline, " +
                        "and this extraction was given no outline. Supply the board outline, or set the " +
                        "reference extent to 'as imported' and accept the copper that is actually there.",
                        regions);
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
            return PdnExtraction.Refused(
                $"Rail '{rail.Name}' names layer {referenceLayer.Layer}/{referenceLayer.Datatype} as " +
                "its reference and there is no copper on it. Name the layer the return actually runs " +
                "on, or fill the reference to the board outline.", regions);

        // ── the grid ───────────────────────────────────────────────────────────────────────────
        var refineBands = RefinementBands(rail, request, baseDeltaDbu);
        var grid = PdnGrid.Build(extent, baseDeltaDbu, refineBands,
                                 Math.Max(1, request.Mesh.PortRefinementRatio),
                                 request.Mesh.MaxCells, conductorCount: 2, notes);

        // ── the cells ──────────────────────────────────────────────────────────────────────────
        var mesh = new MeshBuilder(grid, byLayer, referenceLayer);

        foreach (var island in regions.Power)
            foreach (var (layer, paths) in island.Copper)
            {
                if (layer == referenceLayer)
                {
                    diagnostics.Add(
                        $"The rail has copper on layer {layer.Layer}/{layer.Datatype}, which is also " +
                        "its reference layer. That copper was not meshed as part of the rail — a " +
                        "conductor cannot be its own return.");
                    continue;
                }
                mesh.AddCopper(layer, paths, isReference: false);
            }

        foreach (var (layer, paths) in referenceCopper)
            mesh.AddCopper(layer, paths, isReference: true);

        if (mesh.CellCount == 0)
            return PdnExtraction.Refused(
                $"Rail '{rail.Name}' meshed to no cells at all, which means the grid and the copper do " +
                "not overlap. This is a coordinate-system disagreement rather than a design problem.",
                regions);

        // ── the netlist ────────────────────────────────────────────────────────────────────────
        var asm = new Assembly(request, regions, mesh, celsius, notes, diagnostics);
        if (asm.Build() is { } refusal) return PdnExtraction.Refused(refusal, regions);

        var provenance = new PdnProvenance
        {
            Model = "Accurate (mesh)",
            RailName = rail.Name,
            ReferenceExtent = rail.ReferenceExtent,
            FrequencyHz = 0.0,
            CellSizeMetres = baseDeltaDbu / dbuPerMetre,
            CellSizeBasis = cellBasis,
            PortRefinementRatio = refineBands.Count > 0 ? Math.Max(1, request.Mesh.PortRefinementRatio) : 1,
            CopperTemperatureCelsius = celsius,
            CellCount = mesh.CellCount,
            MeshedAreaSquareMetres = mesh.MeshedAreaSquareDbu / (dbuPerMetre * dbuPerMetre),
            IslandReport = regions.IslandReport,
            ReferencePoint = asm.ReferencePoint,
            Notes = notes,
        };

        return new PdnExtraction(null, asm.Finish(provenance), regions, diagnostics);
    }

    // ── geometry helpers ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Per-layer unioned copper, built through <see cref="DrcRegions.Expand"/> — the same expansion
    /// the DRC run performs, including its decomposition of a <see cref="ViaShape"/> into a barrel on
    /// its own layer and a pad on its landing layer. Sharing it is what keeps the two from disagreeing
    /// about what a via IS.
    /// </summary>
    internal static Dictionary<LayerKey, Paths64> BuildLayerRegions(
        IReadOnlyList<LayoutShape> shapes, Technology tech)
    {
        var byLayer = new Dictionary<LayerKey, Paths64>();
        var order = new List<LayerKey>();

        foreach (var shape in shapes)
        {
            if (!DrcRegions.IsCheckable(shape)) continue;
            DrcRegions.Expand(shape, tech, _ => long.MaxValue, (layer, _, paths) =>
            {
                if (!byLayer.TryGetValue(layer, out var acc))
                {
                    byLayer[layer] = acc = [];
                    order.Add(layer);
                }
                acc.AddRange(paths);
            });
        }

        var unioned = new Dictionary<LayerKey, Paths64>();
        foreach (var layer in order) unioned[layer] = DrcRegions.Union(byLayer[layer]);
        return unioned;
    }

    private static IEnumerable<LayerKey> RailLayers(PdnRailRegionSet regions, LayerKey referenceLayer)
    {
        var seen = new HashSet<LayerKey> { referenceLayer };
        yield return referenceLayer;
        foreach (var island in regions.Power)
            foreach (var (layer, _) in island.Copper)
                if (seen.Add(layer)) yield return layer;
    }

    private static Bbox ExtentOf(
        PdnRailRegionSet regions, IReadOnlyList<(long X, long Y)> anchors, long pad)
    {
        var b = Bbox.Empty;
        foreach (var island in regions.Power) b = b.Union(island.Bounds);
        foreach (var island in regions.Reference) b = b.Union(island.Bounds);
        foreach (var (x, y) in anchors) b = b.Union(new Bbox(x, y, x, y));
        if (b.IsEmpty) return b;
        return new Bbox(b.MinX - pad, b.MinY - pad, b.MaxX + pad, b.MaxY + pad);
    }

    private static Paths64 RectPaths(Bbox b) =>
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
    /// </summary>
    internal static long MinimumFeatureWidthDbu(IReadOnlyList<PdnRegion> islands)
    {
        var all = new Paths64();
        var bounds = Bbox.Empty;
        foreach (var island in islands)
        {
            foreach (var (_, paths) in island.Copper) all.AddRange(paths);
            bounds = bounds.Union(island.Bounds);
        }

        if (all.Count == 0 || bounds.IsEmpty) return 1;

        double total = Math.Abs(Clipper.Area(all));
        if (!(total > 0)) return 1;

        long hi = Math.Max(2, Math.Min(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY));
        long lo = 1;

        // Everything is at least `hi` wide — a solid plane. Nothing narrower exists to resolve.
        if (!LosesArea(all, hi, total)) return hi;

        while (hi - lo > 1 && hi - lo > lo / 32)
        {
            long mid = lo + (hi - lo) / 2;
            if (LosesArea(all, mid, total)) hi = mid; else lo = mid;
        }

        return Math.Max(1, lo);
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

        public static PdnGrid Build(
            Bbox extent, long delta, List<Bbox> bands, int ratio, int maxCells,
            int conductorCount, List<string> notes)
        {
            long spanX = Math.Max(1, extent.MaxX - extent.MinX);
            long spanY = Math.Max(1, extent.MaxY - extent.MinY);

            // Coarsen BEFORE building rather than truncating after: a mesh cut off part way is a
            // board with a hole in it, and it would look like a design problem.
            long d = Math.Max(1, delta);
            while (true)
            {
                long nx = Math.Max(1, spanX / d), ny = Math.Max(1, spanY / d);
                if (nx * ny * conductorCount <= maxCells || d >= Math.Max(spanX, spanY)) break;
                d = (long)Math.Ceiling(d * 1.25);
            }

            if (d != Math.Max(1, delta))
                notes.Add(
                    $"The mesh was coarsened from {delta} to {d} DBU to stay under the " +
                    $"{maxCells:N0}-cell ceiling. Each cell still carries the copper AREA actually " +
                    "inside it, so a run's resistance is still right; what a coarse cell loses is the " +
                    "separation between two conductors that share it.");

            var xs = Lines(extent.MinX, extent.MaxX, d);
            var ys = Lines(extent.MinY, extent.MaxY, d);

            if (ratio > 1 && bands.Count > 0)
            {
                var rx = Refine(xs, bands.Select(b => (b.MinX, b.MaxX)), ratio, d);
                var ry = Refine(ys, bands.Select(b => (b.MinY, b.MaxY)), ratio, d);

                if ((long)(rx.Length - 1) * (ry.Length - 1) * conductorCount <= maxCells)
                {
                    xs = rx;
                    ys = ry;
                    notes.Add(
                        $"The mesh is refined {ratio}× under {bands.Count} port region(s). That is a " +
                        "correctness requirement, not a tidiness one: a coarse mesh under a pin field " +
                        "under-estimates the spreading resistance, and it does so optimistically.");
                }
                else
                {
                    notes.Add(
                        $"Local refinement under {bands.Count} port region(s) was NOT applied — it " +
                        $"would have taken the mesh past the {maxCells:N0}-cell ceiling. The port " +
                        "resistances here are therefore optimistic; raise the ceiling or state a " +
                        "coarser base cell size to get it back.");
                }
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
        public double[] Area { get; set; } = [];
        public int[] Node { get; set; } = [];
    }

    /// <summary>
    /// Rasterises copper onto the grid as AREAS, and nothing else. It builds no matrix and owns no
    /// result — see this file's header and R-rail3-2.
    /// </summary>
    private sealed class MeshBuilder
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
            _meshedArea = 0;

            foreach (var ml in _layers)
            {
                var copper = DrcRegions.Union(ml.Copper);
                ml.Area = Rasterise(copper);
                ml.Node = new int[ml.Area.Length];

                for (int j = 0; j < _grid.Ny; j++)
                    for (int i = 0; i < _grid.Nx; i++)
                    {
                        int k = j * _grid.Nx + i;
                        ml.Node[k] = ml.Area[k] > 0 ? next++ : -1;
                        if (ml.Area[k] > 0) { _cellCount++; _meshedArea += ml.Area[k]; }
                    }
            }

            NodeTotal = next;

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
                int n = ml.Node[j * _grid.Nx + i];
                if (n >= 0) return n;
            }
            return -1;
        }

        public int NodeTotal { get; private set; }

        /// <summary>Copper area per cell, by clipping a row band once and then each cell of it.</summary>
        private double[] Rasterise(Paths64 copper)
        {
            var area = new double[_grid.Nx * _grid.Ny];
            if (copper.Count == 0) return area;

            var bounds = DrcRegions.BoundsOf(copper);

            for (int j = 0; j < _grid.Ny; j++)
            {
                long y0 = _grid.Ys[j], y1 = _grid.Ys[j + 1];
                if (y1 <= bounds.MinY || y0 >= bounds.MaxY) continue;

                Paths64 band = [[new Point64(bounds.MinX, y0), new Point64(bounds.MaxX, y0),
                                 new Point64(bounds.MaxX, y1), new Point64(bounds.MinX, y1)]];
                var strip = Clipper.BooleanOp(ClipType.Intersection, copper, band, LayoutClipper.Rule);
                if (strip.Count == 0) continue;

                var sb = DrcRegions.BoundsOf(strip);

                for (int i = 0; i < _grid.Nx; i++)
                {
                    long x0 = _grid.Xs[i], x1 = _grid.Xs[i + 1];
                    if (x1 <= sb.MinX || x0 >= sb.MaxX) continue;

                    Paths64 cell = [[new Point64(x0, y0), new Point64(x1, y0),
                                     new Point64(x1, y1), new Point64(x0, y1)]];
                    var meet = Clipper.BooleanOp(ClipType.Intersection, strip, cell, LayoutClipper.Rule);
                    if (meet.Count == 0) continue;

                    double a = Math.Abs(Clipper.Area(meet));
                    if (a > 0) area[j * _grid.Nx + i] = a;
                }
            }

            return area;
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
                int n = ml.Node[j * _grid.Nx + i];
                if (n >= 0) hits.Add(n);
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
                        int n = ml.Node[j * _grid.Nx + i];
                        if (n < 0) continue;
                        double cx = (_grid.Xs[i] + _grid.Xs[i + 1]) / 2.0;
                        double cy = (_grid.Ys[j] + _grid.Ys[j + 1]) / 2.0;
                        double d = (cx - x) * (cx - x) + (cy - y) * (cy - y);
                        if (d < bestD) { bestD = d; best = n; }
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

    // ── the netlist ────────────────────────────────────────────────────────────────────────────

    /// <summary>One element, before node numbering.</summary>
    private sealed record Staged(
        string Type, string Path, int[] Raw,
        Dictionary<string, Value> Params, ComponentModel Model,
        PdnOriginKind Kind, string Description,
        PdnCellRef? From, PdnCellRef? To, string? Refdes,
        double? ResistanceOhms, double? LengthMetres = null, double? WidthMetres = null);

    /// <summary>
    /// Stages every element of §4.3, ties what §4.3 says is tied, and hands out node indices.
    ///
    /// <para><b>It never solves anything.</b> Brief 5 owns the solve; this class produces the netlist
    /// and calls nothing that factorises it (R-rail3-2).</para>
    /// </summary>
    private sealed class Assembly
    {
        private readonly PdnExtractionRequest _req;
        private readonly MeshBuilder _mesh;
        private readonly double _celsius;
        private readonly List<string> _notes;
        private readonly List<string> _diagnostics;

        private readonly List<Staged> _staged = [];
        private readonly List<PdnPortBinding> _ports = [];
        private readonly UnionFind _tie;
        private int _synthetic;
        private int _ground = -1;

        private readonly List<PdnElementOrigin> _origins = [];
        private readonly Dictionary<int, PdnCellRef> _nodeCells = [];
        private ElaboratedNetlist? _netlist;

        public string ReferencePoint { get; private set; } = "";

        public Assembly(
            PdnExtractionRequest req, PdnRailRegionSet regions, MeshBuilder mesh,
            double celsius, List<string> notes, List<string> diagnostics)
        {
            _req = req;
            _mesh = mesh;
            _celsius = celsius;
            _notes = notes;
            _diagnostics = diagnostics;
            _ = regions;

            _synthetic = mesh.NodeTotal;
            _tie = new UnionFind(mesh.NodeTotal + 4 * (req.Rail.Sources.Count + req.Rail.Loads.Count) + 16);
        }

        /// <summary>Null when the netlist was built, or the refusal sentence.</summary>
        public string? Build()
        {
            StampMesh();
            StampVias();

            if (ChooseGround() is { } groundRefusal) return groundRefusal;
            if (StampSeriesElements() is { } seriesRefusal) return seriesRefusal;
            if (StampShunts() is { } shuntRefusal) return shuntRefusal;
            if (StampSources() is { } sourceRefusal) return sourceRefusal;
            if (StampLoads() is { } loadRefusal) return loadRefusal;

            Emit();
            return null;
        }

        public PdnNetlist Finish(PdnProvenance provenance) => new()
        {
            Netlist = _netlist!,
            Origins = _origins,
            NodeCells = _nodeCells,
            Ports = _ports,
            Provenance = provenance,
        };

        // ── §4.1 at ω = 0 ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One resistor per cell edge, on EACH conductor.
        ///
        /// <para>The half-cell harmonic form is this file's header; the factor of two §4.1 states is
        /// the LOOP's and is paid by meshing both conductors, which is also this file's header. Read
        /// both before touching the expression below.</para>
        /// </summary>
        private void StampMesh()
        {
            var grid = _mesh.Grid;

            foreach (var ml in _mesh.Layers)
            {
                double sigmaT = ml.Conductor.ConductivitySm * ml.Conductor.ThicknessMetres;
                if (!(sigmaT > 0)) continue;

                double dbuPerMetre = _req.DbuPerMicron * 1e6;
                string side = ml.IsReference ? "reference" : "rail";

                for (int j = 0; j < grid.Ny; j++)
                    for (int i = 0; i < grid.Nx; i++)
                    {
                        int k = j * grid.Nx + i;
                        int a = ml.Node[k];
                        if (a < 0) continue;

                        if (i + 1 < grid.Nx && ml.Node[k + 1] >= 0)
                            AddEdge(ml, i, j, i + 1, j, a, ml.Node[k + 1],
                                    grid.Dx(i), grid.Dx(i + 1), ml.Area[k], ml.Area[k + 1],
                                    sigmaT, dbuPerMetre, side, "x");

                        if (j + 1 < grid.Ny && ml.Node[k + grid.Nx] >= 0)
                            AddEdge(ml, i, j, i, j + 1, a, ml.Node[k + grid.Nx],
                                    grid.Dy(j), grid.Dy(j + 1), ml.Area[k], ml.Area[k + grid.Nx],
                                    sigmaT, dbuPerMetre, side, "y");
                    }
            }
        }

        private void AddEdge(
            MeshLayer ml, int i0, int j0, int i1, int j1, int a, int b,
            long d0Dbu, long d1Dbu, double area0Dbu, double area1Dbu,
            double sigmaT, double dbuPerMetre, string side, string axis)
        {
            // Half a cell of copper each, in series. dx²/(2·σ·T·A) is the half-cell resistance for a
            // cell of length dx carrying an average width A/dx — the ordinary finite-volume form,
            // and the reason a trace's resistance is right whether or not the grid lines fall on its
            // edges.
            double d0 = d0Dbu / dbuPerMetre, d1 = d1Dbu / dbuPerMetre;
            double a0 = area0Dbu / (dbuPerMetre * dbuPerMetre), a1 = area1Dbu / (dbuPerMetre * dbuPerMetre);
            if (!(a0 > 0) || !(a1 > 0)) return;

            double r = d0 * d0 / (2 * sigmaT * a0) + d1 * d1 / (2 * sigmaT * a1);
            if (!(r > 0) || double.IsInfinity(r)) return;

            var from = _mesh.CellRef(ml, i0, j0);
            var to = _mesh.CellRef(ml, i1, j1);
            double length = (d0 + d1) / 2.0;
            double width = (a0 / d0 + a1 / d1) / 2.0;

            _staged.Add(new Staged(
                "R", $"mesh.{side}.{ml.Layer.Layer}_{ml.Layer.Datatype}.{i0}.{j0}.{axis}",
                [a, b], new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(r) },
                new ResistorModel(), PdnOriginKind.MeshEdge,
                $"{length * 1e3:0.###} mm of {width * 1e3:0.###} mm {ml.Conductor.StackupName} copper",
                from, to, null, r, length, width));
        }

        // ── §4.2 ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One resistor per plated barrel, with NO SPECIAL CASE for a group: twenty vias side by side
        /// are twenty resistors between the same two cells, which is twenty in parallel because that
        /// is what the netlist says. And two parts sharing one return via are coupled through it
        /// because the mesh has a SINGLE NODE there.
        ///
        /// <para><b>A via and a plated component hole are distinguished by the NETLIST, never by
        /// geometry</b> (R-rail3-10). Nothing here reads a diameter to decide what a hole IS —
        /// <c>DrillViaPairing</c> already applied <c>BoardNetlistFile</c>'s rule when it produced
        /// these <c>ViaShape</c>s, and re-deriving it from hole size would disagree with the import
        /// that made them.</para>
        /// </summary>
        private void StampVias()
        {
            var tech = _req.Technology;
            var z = ZOf(tech, _req.DbuPerMicron);

            var vias = _req.Shapes.OfType<ViaShape>().ToList();
            vias.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));

            double rho = ResistivityAt(CopperSigma(tech), _celsius);
            int unresolved = 0, nonPlated = 0, stamped = 0;
            var bases = new HashSet<PdnPlatingBasis>();

            foreach (var via in vias)
            {
                var entry = tech.Stackup.Layers.FirstOrDefault(
                    l => l.Kind == StackupKind.Via && l.DrawingLayers.Contains(via.Layer));

                if (entry is null) { unresolved++; continue; }
                if (entry.Plated == false) { nonPlated++; continue; }

                if (entry.SpanFromLayer is not { Length: > 0 } fromName ||
                    entry.SpanToLayer is not { Length: > 0 } toName)
                {
                    unresolved++;
                    continue;
                }

                var fromKeys = ConductorKeys(tech, fromName);
                var toKeys = ConductorKeys(tech, toName);
                if (fromKeys.Count == 0 || toKeys.Count == 0) { unresolved++; continue; }

                int na = NodeOn(fromKeys, via.X, via.Y);
                int nb = NodeOn(toKeys, via.X, via.Y);
                if (na < 0 || nb < 0 || na == nb) continue;

                var (plating, basis) = PdnViaModel.ResolvePlating(
                    entry, _req.DbuPerMicron, _req.Settings.ViaPlatingThicknessMicrometres);
                bases.Add(basis);

                double drill = via.DrillSize / (_req.DbuPerMicron * 1e6);
                double span = Math.Abs(z(toName).Far - z(fromName).Near);
                if (!(span > 0)) span = Math.Abs(z(fromName).Far - z(toName).Near);

                double r = PdnViaModel.BarrelResistanceOhms(drill, plating, span, rho);
                if (!(r > 0)) continue;

                _staged.Add(new Staged(
                    "R", $"via.{via.X}.{via.Y}",
                    [na, nb], new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(r) },
                    new ResistorModel(), PdnOriginKind.Via,
                    $"a {drill * 1e3:0.###} mm plated via over {span * 1e3:0.###} mm, " +
                    PdnViaModel.DescribePlating(plating, basis),
                    CellOf(na), CellOf(nb), null, r));
                stamped++;
            }

            if (stamped > 0 && bases.Contains(PdnPlatingBasis.Defaulted))
                _notes.Add(
                    $"{stamped} via barrel(s) were computed at the default " +
                    $"{PdnViaModel.DefaultPlatingMicrometres:0.#} µm of plating, because neither the " +
                    "stackup's via entry nor this document states one. Plating sets the barrel's whole " +
                    "conducting cross-section, so state it where you know it.");

            if (unresolved > 0)
                _diagnostics.Add(
                    $"{unresolved} hole(s) could not be resolved to a layer span and carry no barrel " +
                    "resistance. v1 assumes THROUGH vias and reads a span declaration where one exists; " +
                    "a blind or buried span it cannot resolve is reported rather than assumed.");

            if (nonPlated > 0)
                _diagnostics.Add($"{nonPlated} hole(s) are declared non-plated and are not conductors.");
        }

        private static double CopperSigma(Technology tech)
        {
            foreach (var l in tech.Stackup.Layers)
                if (l.Kind == StackupKind.Conductor && l.SigmaSm > 0) return l.SigmaSm;
            return 5.8e7;
        }

        private static List<LayerKey> ConductorKeys(Technology tech, string name)
        {
            foreach (var l in tech.Stackup.Layers)
                if (l.Kind == StackupKind.Conductor && string.Equals(l.Name, name, StringComparison.Ordinal))
                    return l.DrawingLayers;
            return [];
        }

        /// <summary>The z band each named conductor occupies, top-down, in metres.</summary>
        private static Func<string, (double Near, double Far)> ZOf(Technology tech, int dbuPerMicron)
        {
            var bands = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
            double zz = 0;
            foreach (var l in tech.Stackup.Layers)
            {
                if (l.Kind == StackupKind.Via) continue;
                double t = l.ThicknessDbu / (dbuPerMicron * 1e6);
                if (l.Kind == StackupKind.Conductor && l.Name.Length > 0 && !bands.ContainsKey(l.Name))
                    bands[l.Name] = (zz, zz + t);
                zz += t;
            }
            return name => bands.TryGetValue(name, out var b) ? b : (0, 0);
        }

        /// <summary>The first cell of any of <paramref name="keys"/> covering the point, or -1.</summary>
        private int NodeOn(IReadOnlyList<LayerKey> keys, long x, long y)
        {
            foreach (var key in keys)
            {
                int n = _mesh.NodeOnLayer(key, x, y);
                if (n >= 0) return n;
            }
            return -1;
        }

        // ── §4.3 ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Where the whole answer is measured from: the reference cells under the FIRST source's own
        /// pads, tied together exactly as §4.3 ties a load's pin field.
        ///
        /// <para>That is the physical reference point — a drop is a drop relative to where the supply
        /// returns — and it is stated in the provenance rather than being an unwritten convention, so
        /// a reader can tell which node the numbers are against.</para>
        /// </summary>
        private string? ChooseGround()
        {
            var anchors = new List<(string What, RailPortAnchor Anchor)>();
            foreach (var s in _req.Rail.Sources) anchors.Add(("source", s.Anchor));
            foreach (var l in _req.Rail.Loads) anchors.Add(("load", l.Anchor));

            foreach (var (what, anchor) in anchors)
            {
                var nodes = ReferenceNodesFor(anchor, out long away);
                if (nodes.Count == 0) continue;

                _ground = Merge(nodes);
                ReferencePoint =
                    $"the reference conductor under {anchor.Describe()}, this rail's first {what}" +
                    (away > 0
                        ? $" — the nearest reference copper is {away} DBU away, because there is none " +
                          "directly under that pad"
                        : "");
                return null;
            }

            return
                $"Rail '{_req.Rail.Name}' has no source or load whose pads sit over its reference " +
                "conductor, so there is no point to measure a drop from. Anchor a source on the rail, " +
                "or name a reference layer the rail's pads actually run over.";
        }

        /// <summary>
        /// A series part on the path: a resistance between the two pieces of copper it bridges.
        /// <b>Elements, never annotations</b> — at DC these are the largest terms after the source
        /// (§4.3), and an annotation does not appear in a ranked breakdown.
        /// </summary>
        private string? StampSeriesElements()
        {
            for (int k = 0; k < _req.SeriesElements.Count; k++)
            {
                var part = _req.SeriesElements[k];
                string where = $"Series part {part.Refdes} on rail '{_req.Rail.Name}'";

                var a = PowerNodesFor(part.A);
                var b = PowerNodesFor(part.B);

                if (a.Count == 0)
                    return PdnAttachments.RefusalForUnresolved($"{where}'s first end", part.A, 0);
                if (b.Count == 0)
                    return PdnAttachments.RefusalForUnresolved($"{where}'s second end", part.B, 0);

                int na = Merge(a), nb = Merge(b);
                if (na == nb)
                {
                    _diagnostics.Add(
                        $"{where} bridges {part.A.Describe()} to {part.B.Describe()}, which are already " +
                        "one piece of copper. Its resistance is in the netlist and carries no current.");
                }

                _staged.Add(new Staged(
                    "R", $"series.{part.Refdes}",
                    [na, nb],
                    new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(part.ResistanceOhms) },
                    new ResistorModel(), PdnOriginKind.SeriesElement,
                    $"{part.Refdes} at {part.ResistanceOhms * 1e3:0.###} mΩ ({part.Basis})",
                    CellOf(na), CellOf(nb), part.Refdes, part.ResistanceOhms));
            }
            return null;
        }

        /// <summary>
        /// The decoupling. <b>In the netlist, and carrying no DC path</b> — a capacitor bridges
        /// nothing at DC, which is correct and occasionally surprising (R-rail3-4). Leaving it out
        /// would make brief 14's netlist a different netlist from this one.
        /// </summary>
        private string? StampShunts()
        {
            int unmodelled = 0;

            foreach (var part in _req.ShuntParts)
            {
                var power = PowerNodesFor(part.Anchor);
                if (power.Count == 0)
                {
                    _diagnostics.Add(
                        $"{part.Refdes} is anchored at {part.Anchor.Describe()}, which is not on this " +
                        "rail's copper. It was not added.");
                    continue;
                }

                if (part.CapacitanceFarads is not { } c || !(c > 0)) { unmodelled++; continue; }

                var reference = ReferenceNodesFor(part.Anchor, out _);
                if (reference.Count == 0)
                {
                    _diagnostics.Add(
                        $"{part.Refdes} has no reference copper under it, so it bridges to nothing. It " +
                        "was not added.");
                    continue;
                }

                int np = Merge(power), nr = Merge(reference);

                _staged.Add(new Staged(
                    "C", $"shunt.{part.Refdes}",
                    [np, nr],
                    new Dictionary<string, Value>(StringComparer.Ordinal) { ["C"] = new Value(c) },
                    new CapacitorModel(), PdnOriginKind.Shunt,
                    $"{part.Refdes}, {c * 1e9:0.###} nF — no DC path, by construction",
                    CellOf(np), CellOf(nr), part.Refdes, null));
            }

            if (unmodelled > 0)
                _diagnostics.Add(
                    $"{unmodelled} part(s) on this rail have no capacitance in the library and were " +
                    "counted rather than given one. An unstated value is never a defaulted one.");

            return null;
        }

        /// <summary>
        /// One branch per source, and NOTHING ELSE (R-rail3-12). Two supplies feeding one net do not
        /// share in proportion to anything a designer can see; the copper decides, and a second source
        /// stamped at the wrong node produces a plausible number rather than an error. So there is no
        /// special case below for the second one.
        /// </summary>
        private string? StampSources()
        {
            for (int k = 0; k < _req.Rail.Sources.Count; k++)
            {
                var src = _req.Rail.Sources[k];
                string name = src.Anchor.Describe();
                string where = $"Rail '{_req.Rail.Name}'s source {k + 1} ({name})";

                var power = PowerNodesFor(src.Anchor);
                if (power.Count == 0)
                    return PdnAttachments.RefusalForUnresolved(where, src.Anchor, 0);

                var reference = ReferenceNodesFor(src.Anchor, out _);
                if (reference.Count == 0)
                    return $"{where} has no reference copper anywhere under it, so its return has " +
                           "nowhere to go. Name the layer the return actually runs on.";

                int np = Merge(power), nr = Merge(reference);
                double? r = src.SeriesResistanceOhms;

                if (src.OpenCircuitVoltageV is not { } volts)
                {
                    // Null is not zero: a source with no stated voltage is a branch whose IMPEDANCE
                    // is known and whose LEVEL is not. Its resistance goes in — the frequency answer
                    // uses it — and no DC level is invented (railrf.md §2.2).
                    if (r is { } ohms)
                        _staged.Add(new Staged(
                            "R", $"source.{k + 1}.r", [np, nr],
                            new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(ohms) },
                            new ResistorModel(), PdnOriginKind.SourceResistance,
                            $"{name}'s series resistance, {ohms * 1e3:0.###} mΩ",
                            CellOf(np), CellOf(nr), src.Anchor.Refdes, ohms));

                    _notes.Add(
                        $"{name} states no open-circuit voltage, so it contributes its impedance and no " +
                        "DC level. The DC answer is a drop across this rail, not a voltage at its loads.");
                    continue;
                }

                int internalNode = r is { } series && series > 0 ? _synthetic++ : np;

                _staged.Add(new Staged(
                    "Vdc", $"source.{k + 1}.v", [internalNode, nr],
                    new Dictionary<string, Value>(StringComparer.Ordinal) { ["Vdc"] = new Value(volts) },
                    new VdcModel(), PdnOriginKind.SourceBranch,
                    $"{name} at {volts:0.###} V open circuit",
                    null, CellOf(nr), src.Anchor.Refdes, null));

                if (internalNode != np)
                    _staged.Add(new Staged(
                        "R", $"source.{k + 1}.r", [internalNode, np],
                        new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(r!.Value) },
                        new ResistorModel(), PdnOriginKind.SourceResistance,
                        $"{name}'s series resistance, {r.Value * 1e3:0.###} mΩ",
                        null, CellOf(np), src.Anchor.Refdes, r.Value));
                else
                    _notes.Add(
                        $"{name} states no series resistance, so it is an ideal source at DC. On an " +
                        "aged cell that is the largest term on the path and its absence flatters the " +
                        "answer.");
            }

            return null;
        }

        /// <summary>
        /// Each load port across the power and reference nodes of its own pin-field cells, TIED
        /// TOGETHER — because that is what the die sees (§2.2) — with its DC current as an injection.
        ///
        /// <para><b>A port with no stated current contributes an observation port and no current</b>
        /// (§2.2, Q-16). A defaulted zero and a stated zero are the same number and mean different
        /// things, and the DC report has to list an observed port AS observed rather than omitting
        /// it.</para>
        /// </summary>
        private string? StampLoads()
        {
            for (int k = 0; k < _req.Rail.Loads.Count; k++)
            {
                var load = _req.Rail.Loads[k];
                string name = load.Anchor.Describe();
                string where = $"Rail '{_req.Rail.Name}'s load {k + 1} ({name})";

                var power = PowerNodesFor(load.Anchor);
                if (power.Count == 0)
                    return PdnAttachments.RefusalForUnresolved(where, load.Anchor, 0);

                var reference = ReferenceNodesFor(load.Anchor, out _);
                if (reference.Count == 0)
                    return $"{where} has no reference copper anywhere under it, so there is nothing to " +
                           "measure its impedance against. Name the layer the return actually runs on.";

                int np = Merge(power), nr = Merge(reference);

                var cells = new List<PdnCellRef>();
                foreach (int n in power) if (CellOf(n) is { } c) cells.Add(c);
                foreach (int n in reference) if (CellOf(n) is { } c) cells.Add(c);

                if (load.DcCurrentA is { } amps)
                    _staged.Add(new Staged(
                        // Injected INTO the reference and drawn OUT of the rail, which is what a load
                        // does. The engine's current-source convention delivers J to Nodes[0].
                        "I_1Tone", $"load.{k + 1}.i", [nr, np],
                        new Dictionary<string, Value>(StringComparer.Ordinal) { ["Idc"] = new Value(amps) },
                        new CurrentToneSourceModel([], amps), PdnOriginKind.LoadCurrent,
                        $"{name} drawing {amps * 1e3:0.###} mA",
                        CellOf(np), CellOf(nr), load.Anchor.Refdes, null));

                _staged.Add(new Staged(
                    "Port", $"port.{k + 1}", [np, nr],
                    new Dictionary<string, Value>(StringComparer.Ordinal) { ["Num"] = new Value(k + 1) },
                    new PortModel(), PdnOriginKind.Port,
                    load.DcCurrentA is null
                        ? $"{name}, an observation port — it states no current and draws none"
                        : $"{name}, observed",
                    CellOf(np), CellOf(nr), load.Anchor.Refdes, null));

                _ports.Add(new PdnPortBinding(k, name, load.Anchor, np, nr, cells, load.DcCurrentA));
            }

            return null;
        }

        // ── node bookkeeping ───────────────────────────────────────────────────────────────────

        private List<int> PowerNodesFor(RailPortAnchor anchor)
        {
            var nodes = new List<int>();
            foreach (var (x, y) in PdnAttachments.Resolve(anchor, _req.Pads))
                foreach (int n in _mesh.NodesAt(x, y, isReference: false))
                    if (!nodes.Contains(n)) nodes.Add(n);
            return nodes;
        }

        private List<int> ReferenceNodesFor(RailPortAnchor anchor, out long distanceDbu)
        {
            distanceDbu = 0;
            var pads = PdnAttachments.Resolve(anchor, _req.Pads);
            var nodes = new List<int>();

            foreach (var (x, y) in pads)
                foreach (int n in _mesh.NodesAt(x, y, isReference: true))
                    if (!nodes.Contains(n)) nodes.Add(n);

            if (nodes.Count > 0 || pads.Count == 0) return nodes;

            // No reference copper directly under the pad — an antipad, a split, a keepout. The
            // NEAREST reference cell is the honest stand-in and the distance is reported, because a
            // return that starts 3 mm away is a finding rather than a detail.
            var (x0, y0) = pads[0];
            int nearest = _mesh.NearestReferenceNode(x0, y0, out distanceDbu);
            if (nearest >= 0)
            {
                nodes.Add(nearest);
                _diagnostics.Add(
                    $"There is no reference copper under {anchor.Describe()}; the return was attached " +
                    $"to the nearest reference cell, {distanceDbu} DBU away. The spreading between the " +
                    "two is NOT in this answer.");
            }
            return nodes;
        }

        /// <summary>Ties a set of cells into one node — §4.3's "tied together", and what makes a pin
        /// field one port rather than six.</summary>
        private int Merge(List<int> nodes)
        {
            int rep = nodes[0];
            for (int i = 1; i < nodes.Count; i++) _tie.Union(rep, nodes[i]);
            return _tie.Find(rep);
        }

        private PdnCellRef? CellOf(int raw) => _mesh.CellOfNode(raw);

        /// <summary>
        /// Resolves ties, drops what has no DC path to the reference point, numbers what is left, and
        /// builds the netlist. Nothing here decides anything a solver would — it is bookkeeping.
        /// </summary>
        private void Emit()
        {
            int groundRep = _tie.Find(_ground);

            // Which nodes are joined by an ELEMENT, which is a different question from which are tied.
            var reach = new UnionFind(_tie.Capacity);
            foreach (var s in _staged) reach.Union(_tie.Find(s.Raw[0]), _tie.Find(s.Raw[1]));
            int groundComponent = reach.Find(groundRep);

            bool Keep(int raw) =>
                _req.Mesh.IncludeIsolatedRegions || reach.Find(_tie.Find(raw)) == groundComponent;

            var netlist = new ElaboratedNetlist();
            var final = new Dictionary<int, int> { [groundRep] = 0 };
            _nodeCells[0] = _mesh.CellOfNode(_ground) ?? default;

            int Index(int raw)
            {
                int rep = _tie.Find(raw);
                if (final.TryGetValue(rep, out int idx)) return idx;
                idx = netlist.Nodes.GetOrAssign(NameOf(rep, raw));
                final[rep] = idx;
                if (_mesh.CellOfNode(raw) is { } cell) _nodeCells[idx] = cell;
                return idx;
            }

            // Names in mesh order first, so a node's name is the cell a reader would point at rather
            // than whichever element happened to mention it first.
            var grid = _mesh.Grid;
            foreach (var ml in _mesh.Layers)
                for (int j = 0; j < grid.Ny; j++)
                    for (int i = 0; i < grid.Nx; i++)
                    {
                        int n = ml.Node[j * grid.Nx + i];
                        if (n >= 0 && Keep(n)) Index(n);
                    }

            int dropped = 0;

            foreach (var s in _staged)
            {
                if (!Keep(s.Raw[0]) || !Keep(s.Raw[1])) { dropped++; continue; }

                int componentIndex = netlist.Components.Count;
                netlist.AddComponent(new ElaboratedComponent(
                    s.Type, s.Path, [Index(s.Raw[0]), Index(s.Raw[1])], s.Params, s.Model));

                _origins.Add(new PdnElementOrigin(
                    componentIndex, s.Kind, s.Description, s.From, s.To, s.Refdes,
                    s.ResistanceOhms, s.LengthMetres, s.WidthMetres));
            }

            if (dropped > 0)
                _diagnostics.Add(
                    $"{dropped} element(s) sit on copper with no DC path to the reference point and " +
                    "were not stamped. That is the island structure, not an error — a capacitor " +
                    "bridges nothing at DC. Set IncludeIsolatedRegions to carry them anyway.");

            _portsFinal.Clear();
            foreach (var p in _ports)
                _portsFinal.Add(p with
                {
                    PowerNode = Keep(p.PowerNode) ? Index(p.PowerNode) : -1,
                    ReferenceNode = Keep(p.ReferenceNode) ? Index(p.ReferenceNode) : -1,
                });

            _ports.Clear();
            _ports.AddRange(_portsFinal);
            _netlist = netlist;
        }

        private readonly List<PdnPortBinding> _portsFinal = [];

        private string NameOf(int rep, int raw)
        {
            if (_mesh.CellOfNode(raw) is { } cell)
                return $"{(cell.IsReference ? "ref" : "rail")}.{cell.Layer.Layer}_{cell.Layer.Datatype}." +
                       $"{cell.Ix}.{cell.Iy}";
            return $"internal.{rep}";
        }

        /// <summary>Union-find with path halving — local, for the same reason
        /// <see cref="DrcConnectivity"/>'s is local.</summary>
        private sealed class UnionFind
        {
            private readonly int[] _parent;

            public UnionFind(int n)
            {
                _parent = new int[n];
                for (int i = 0; i < n; i++) _parent[i] = i;
            }

            public int Capacity => _parent.Length;

            public int Find(int x)
            {
                while (_parent[x] != x) { _parent[x] = _parent[_parent[x]]; x = _parent[x]; }
                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra != rb) _parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
            }
        }
    }
}
