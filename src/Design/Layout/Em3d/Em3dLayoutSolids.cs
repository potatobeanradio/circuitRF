// brief-em3d-42 R-em3d42-2 — the solids of a layout: its geometry and materials, and nothing else.
//
// SPLIT OUT OF Em3dGenerator, which used to build the whole 3D problem in one pass. Two callers need the
// geometry half now: the generator (a .cem run of a layout), which adds the air box, the ports, the sweep
// and the air above the stack on top of it; and the .c3d elaborator (a layout placed as an INSTANCE in a
// 3D view), which adds none of those because the parent owns them (overview §1i, §1k).
//
// THE SPLIT IS BYTE-IDENTICAL (R-em3d42-2b). Every .cem in the repository and every generator fixture was
// dumped as text BEFORE this file existed (testdata/em3d/generator-dumps/, Em3dGeneratorDumpTests), and the
// generator must still reproduce every dump. That is why the builder below is STAGED rather than one call:
// the generator's notes and materials are ordered lists, and the generator's port and air-box work falls
// BETWEEN the geometry's preparation and its solids — a one-shot "geometry first, then the rest" would
// have reordered both. The stages are:
//
//   Prepare()        everything up to and including the bond wires, and the content's extent;
//   (the generator's ports and air box, which read Bands/Pieces/Ground/FloorZ)
//   BuildSolids()    the solids and sheets in construction order — WITHOUT the air above the stack, whose
//                    slot (order, index, material) is reserved when a caller asks for it, so the generator
//                    adds the air solid itself;
//   FinishNotes()    the temperature notes, which the generator has always written last but one.
//
// `From` is the one-call form an instance uses, with the instance options (§2c): no solve region, no PEC
// floor, no metal the layout does not draw, and every laterally unbounded slab — a dielectric or a body
// without an outline — bounded by the board outline, else by the box round the drawn geometry, one
// rectangle for every slab, with a note saying which. A planar setup previewed in 3D (DisplaySlabs) draws its
// slabs the same way.

using System.Globalization;
using Clipper2Lib;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using WireMaterial = CircuitRF.WBond.WireMaterial;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>What a layout's solids are built for, and at what physics.</summary>
/// <param name="FMaxHz">The top frequency — the sheet rule compares a conductor's thickness with its skin
/// depth there (R-em3d3-5f). Null when there is no sweep (a 3D view elaborated with no setup): a conductor
/// with thickness is then a solid.</param>
/// <param name="TempC">The operating temperature σ(T) is evaluated at.</param>
/// <param name="Instance">True for a layout placed in a 3D view (§2c): no solve region, no floor, bounded slabs, and
/// no metal but what the layout draws.</param>
public sealed record Em3dLayoutSolidsOptions(double? FMaxHz, double TempC, bool Instance)
{
    /// <summary>A <c>.cem</c> run's region: the setup's SolveRegion (the generator's case, and an instance's when the 3D view
    /// is the in-memory one a <c>.cem</c> with component ports is built through). Null takes the whole layout.</summary>
    public EmSetup? RegionSetup { get; init; }

    /// <summary>False when the <c>.cem</c>'s AirBox ZMin states a boundary other than PEC, which steps the
    /// floor rule aside (the generator's case). An instance never has a floor.</summary>
    public bool FloorAllowed { get; init; } = true;

    /// <summary>A planar setup previewed in 3D, for VIEWING only: every laterally unbounded slab is drawn to
    /// <see cref="SlabLateralBound"/>'s shape rather than across the air box, and one with no outline or copper to
    /// take a shape from is not drawn at all. The planar solve still treats
    /// every dielectric as laterally infinite; nothing a solver reads is built this way.</summary>
    public bool DisplaySlabs { get; init; }

    /// <summary>A picture, never a solve: a layout with no conductor artwork is drawn when it has wires, and a wire end
    /// over no conductor lands on an undrawn pad. What a solve would have refused on is kept in
    /// <see cref="Em3dLayoutSolids.Builder.PadRefusal"/>. The bug this answers: a wBond placed in a schematic and pushed
    /// to an empty layout by Update Layout from Schematic has wires and no artwork, so its 3D view showed nothing.</summary>
    public bool PadsOptional { get; init; }

    /// <summary>brief-dfr28-3 — <see cref="EmSetup.RobustMeshing"/>: every artwork conductor's outline loses the points that lie
    /// within <see cref="Em3dOutlineCleanup.Tolerance"/> of their neighbours' line, and one note says how many and how far.
    /// Never a via's barrel, an edge-cut drill's cut, a slab or the board outline.</summary>
    public bool RobustMeshing { get; init; }

    /// <summary>EM-TFR — <see cref="EmSetup.Conductors"/>.</summary>
    public Em3dConductorMode Conductors { get; init; }

    /// <summary>brief-emb-2 R-emb2-2 — every via is a SOLID barrel, whatever its entry's <see cref="ViaFillKind"/>: what an EM
    /// problem (driven, eigenmode, electrostatic, magnetostatic) is built with. A hollow barrel's air core, 25 µm inside the
    /// plating, meshed into inverted elements on an imported board and is electrically the same at RF, so only a thermal
    /// problem, where the wall carries the heat, keeps it. Off (the wall as the technology states it) for a picture with no
    /// setup and for a thermal run.</summary>
    public bool SolidVias { get; init; }
}

/// <summary>A layout's geometry as the 3D problem's vocabulary: solids, sheets and materials, and where
/// each came from. No air box, port, sweep, air solid or solve region (R-em3d42-2a).</summary>
public sealed record Em3dLayoutSolidsResult(
    IReadOnlyList<Em3dSolid>    Solids,
    IReadOnlyList<Em3dSheet>    Sheets,
    IReadOnlyList<Em3dMaterial> Materials,
    string?                     Refusal,
    IReadOnlyList<string>       Notes)
{
    public bool Ok => Refusal is null;
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyDictionary<string, Em3dObjectOrigin> Origins { get; init; } = new Dictionary<string, Em3dObjectOrigin>();
    public IReadOnlyDictionary<string, string> MaterialSources { get; init; } = new Dictionary<string, string>();

    /// <summary>Each conductor's net(s), by object name.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<string>> ObjectNets { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>();

    /// <summary>brief-dfr28-5 — each via on a net of its own that passes copper of another net, by object name.</summary>
    public IReadOnlyDictionary<string, string> ViaNetConflicts { get; init; } = new Dictionary<string, string>();

    /// <summary>brief-dfr34-4 D1 — each via whose stated net is on none of the copper it passes, all of which is one other
    /// net, by object name: the net map gives it that copper's net, which is what is built.</summary>
    public IReadOnlyDictionary<string, Em3dViaNetAdoption> ViaNetAdoptions { get; init; } = new Dictionary<string, Em3dViaNetAdoption>();

    /// <summary>The conductors on ground-reference stackup entries.</summary>
    public IReadOnlyList<string> GroundBandObjects { get; init; } = [];

    /// <summary>brief-emb-2 R-emb2-3 — the farthest Robust meshing moved any outline point, metres; 0 when it is off or moved
    /// nothing. A port edge that touched the artwork as drawn is within this of the outline the run meshes.</summary>
    public double OutlineMoveM { get; init; }

    public IReadOnlyList<Em3dWireReport> Wires { get; init; } = [];
    public IReadOnlyList<string> NoAlpha { get; init; } = [];
    public IReadOnlyList<string> UnknownTemperature { get; init; } = [];

    /// <summary>The bottom of the stackup, metres — what an instance's z places (overview §1i).</summary>
    public double StackBottomM { get; init; }

    /// <summary>With <see cref="Em3dLayoutSolidsOptions.PadsOptional"/>: what a solve would have refused on — no conductor
    /// artwork, a wire end over no pad, or a wire that could not be built. Null otherwise.</summary>
    public string? PadRefusal { get; init; }

    /// <summary>The <see cref="Refusal"/> is the bond wires' (their <c>.wBond</c> could not be built), not the artwork's.</summary>
    public bool RefusedOnWires { get; init; }
}

public static class Em3dLayoutSolids
{
    /// <summary>The solids of <paramref name="source"/>'s layout through <paramref name="tech"/>.</summary>
    /// <param name="wires">Null takes the <c>.wBond</c> stem-paired with the layout (WB40), as a <c>.cem</c> run does.</param>
    public static Em3dLayoutSolidsResult From(EmLayoutSource source, Technology tech, Em3dWireSource? wires,
                                              Em3dLayoutSolidsOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tech);
        ArgumentNullException.ThrowIfNull(options);
        var b = new Builder(source, tech, wires, options);
        if (b.Prepare() is { } refusal) return b.Failed(refusal);
        b.BuildSolids(null, reserveAir: false);
        b.FinishNotes();
        return b.Result();
    }

    /// <summary>
    /// The staged builder (see the file's note). <b>Internal</b>: the generator drives its stages; every
    /// other caller takes <see cref="From"/>.
    /// </summary>
    internal sealed class Builder(EmLayoutSource source, Technology tech, Em3dWireSource? wires, Em3dLayoutSolidsOptions options)
    {
        public readonly List<string> Notes = [];
        public readonly List<string> WarningList = [];
        private readonly List<Em3dMaterial> _materials = [];
        private readonly Dictionary<string, Em3dMaterial> _materialByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _materialSource = new(StringComparer.Ordinal);
        public readonly Dictionary<string, Em3dObjectOrigin> OriginMap = new(StringComparer.Ordinal);
        private double _perDbu;
        // brief-em3d-22 — each conductor's net(s), by object name: a piece's drawn net, a via's own or
        // the net it lands on, a wire's array and its pads' nets. And the pieces on ground-reference
        // entries, which are the ground when the setup names no Ground3D.
        public readonly Dictionary<string, HashSet<string>> ObjectNetMap = new(StringComparer.Ordinal);
        public readonly List<string> GroundBandObjectList = [];
        // brief-dfr28-5 — each via whose own net is not the net of copper it passes, by object name: what it is on and what
        // it joins. A static solve refuses these (Em3dGenerator.ViaNetRefusal); its own net still wins in the net map.
        public readonly Dictionary<string, string> ViaNetConflictMap = new(StringComparer.Ordinal);
        // brief-dfr34-4 D1 — each via whose stated net is on none of its copper, and that copper one net: it takes that net.
        public readonly Dictionary<string, Em3dViaNetAdoption> ViaNetAdoptionMap = new(StringComparer.Ordinal);
        private readonly Dictionary<LayerKey, List<LayoutShape>> _byLayer = [];

        /// <summary>One merged conductor piece: its polygon, its name and what it became.</summary>
        /// <param name="IsFilm">EM-TFR — a resistive film: always a sheet, and not metal a port can sit on.</param>
        internal sealed record Piece(PlanarPolygon Poly, string Name, string Material, bool IsSheet,
                                     double ZBottom, double ZTop, double SheetZ, LayerKey Layer,
                                     string? SheetReason, string? Net = null, bool IsFilm = false);

        private const double StackPerDbu = 1.0 / (LayoutUnits.DefaultDbuPerMicron * 1e6);
        private const double Mu0 = 4e-7 * Math.PI;

        // ── what Prepare decides, read by the generator's ports and air box ──────────────────────
        public IReadOnlyList<PlanarExtractor.StackBand> Bands { get; private set; } = [];
        public Dictionary<int, List<Piece>> Pieces { get; } = [];
        public PlanarExtractor.StackBand? Ground { get; private set; }
        public bool PecFloor { get; private set; }
        public double FloorZ { get; private set; } = double.NaN;
        public bool FloorStatedAway { get; private set; }

        /// <summary>Designer round 33 — the underside of a drawn, unbroken lowest ground plane, where the air box's floor goes
        /// (a PEC face) when the setup states nothing for ZMin; null otherwise.</summary>
        public double? PlaneFloorZ { get; private set; }

        /// <summary>brief-dfr34-2 R-dfr34-2-4 — the conductor the floor rule judged: the bottom-most drawn one, whether or not the
        /// technology marks it ground-reference. <see cref="PlaneFloorZ"/> is its underside when it passed.</summary>
        public PlanarExtractor.StackBand? PlaneFloorBand { get; private set; }

        /// <summary>brief-emb-2 R-emb2-5 — how many holes the floor plane has (none with metal over it), when it is the floor.</summary>
        public int PlaneFloorHoles { get; private set; }

        /// <summary>brief-emb-2 R-emb2-5 — where a hole of the lowest ground plane has metal over it (metres), which keeps the
        /// floor off the plane; null otherwise.</summary>
        public (double X, double Y)? PlaneFloorBlockedAt { get; private set; }

        /// <summary>The plane covers <see cref="Em3dGenerator.PlaneFloorCover"/> of the box round all drawn metal, a band
        /// <paramref name="edgeBandM"/> (H) wide round its edge left out (<see cref="Em3dGenerator.PlaneCoversFootprint"/>).</summary>
        private static bool UnbrokenCover(IReadOnlyList<PlanarPolygon> plane, IEnumerable<PlanarPolygon> allMetal, double? edgeBandM)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var p in allMetal)
                foreach (var q in p.Outer) { x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y); }
            return x1 > x0 && y1 > y0 && Em3dGenerator.PlaneCoversFootprint(
                plane.Select(p => (IReadOnlyList<(double X, double Y)>)[.. p.Outer.Select(q => (q.X, q.Y))]), x0, y0, x1, y1, edgeBandM);
        }

        public Em3dWireBuild? WireBuild { get; private set; }

        /// <summary>With <see cref="Em3dLayoutSolidsOptions.PadsOptional"/>: the refusal a solve would have stopped on —
        /// no conductor artwork, a wire end over no pad, or a wire that could not be built. Null otherwise.</summary>
        public string? PadRefusal { get; private set; }

        /// <summary>True when the refusal <see cref="Prepare"/> returned is the bond wires' — what a 3D view labels as
        /// invalid wBond geometry rather than as a cell it cannot find.</summary>
        public bool RefusedOnWires { get; private set; }

        private string? _wireRefusal;

        /// <summary>The content's extent, metres — what an air box pads.</summary>
        public double Cx0 { get; private set; } = double.PositiveInfinity;
        public double Cy0 { get; private set; } = double.PositiveInfinity;
        public double Cx1 { get; private set; } = double.NegativeInfinity;
        public double Cy1 { get; private set; } = double.NegativeInfinity;
        public double ZLow { get; private set; } = double.PositiveInfinity;
        public double ZHigh { get; private set; } = double.NegativeInfinity;

        private Dictionary<int, PlanarExtractor.StackBand> _bandByIndex = [];
        private List<PlanarExtractor.StackBand> _inProblem = [];
        private readonly Dictionary<int, (double Bottom, double Top)> _dielZ = [];
        private readonly Dictionary<int, List<PlanarPolygon>> _filmPolys = [];
        /// <summary>PSV-2 — each opened dielectric band's mask name and its merged openings (StackupLayer.OpenWithLayer).</summary>
        private readonly Dictionary<int, (string Mask, List<PlanarPolygon> Polys)> _openings = [];
        /// <summary>PSV-4 — a conformal cover's band index → the conductor band it follows.</summary>
        private readonly Dictionary<int, int> _conformalOver = [];
        /// <summary>PSV-4 — a dielectric's band index → the conformal cover directly below it, whose topography it fills.</summary>
        private readonly Dictionary<int, int> _fillsConformal = [];
        private readonly List<PlanarPolygon> _outline = [];

        /// <summary>Where <see cref="ClipDisc"/> cut a board edge, for <see cref="ShareCutVertices"/>.</summary>
        private readonly HashSet<EmPoint> _cutVertices = [];
        private readonly List<PlanarPolygon> _closedOutlineStrokes = [];
        /// <summary>Zero-width paths that close on themselves, off the outline layers — what an import leaves when it
        /// draws the board's edge on every copper layer instead of the outline layer.</summary>
        private readonly List<(PathShape Path, LayerKey Layer)> _closedCentrelines = [];
        private readonly List<(TechBody Body, double ZBottom, double ZTop, List<PlanarPolygon> Polys)> _bodies = [];
        private readonly List<(LayoutShape Shape, StackupLayer Entry, PlanarExtractor.StackBand Top,
                               PlanarExtractor.StackBand Bottom)> _viaSpans = [];
        // Non-plated holes: drilled through their span as air, never metal (GI1's Plated = false).
        private readonly List<(LayoutShape Shape, StackupLayer Entry, PlanarExtractor.StackBand Top,
                               PlanarExtractor.StackBand Bottom)> _holeSpans = [];
        private readonly List<string> _unknownTemperature = [];
        private readonly SortedSet<string> _noAlpha = new(StringComparer.Ordinal);

        /// <summary>Nothing is drawn on a conductor layer (a picture of wires alone, <see cref="Em3dLayoutSolidsOptions.PadsOptional"/>).
        /// No slab or body is then bounded by anything the user drew, so none is built: a wBond's wires pushed to
        /// an empty layout came into a 3D view with a substrate and a bottom plane nobody placed.</summary>
        private bool _noArtwork;

        // ── the solids, once built ───────────────────────────────────────────────────────────────
        public readonly List<Em3dSolid> SolidList = [];
        public readonly List<Em3dSheet> SheetList = [];

        /// <summary>brief-dfr26-1 R-dfr26-1-2 — each round plated barrel's axis and radius, metres, and the conductors it
        /// joins: what a pad-to-plane port's sheet stands clear of. A via drawn as a polygon has no axis and is not here.</summary>
        public IEnumerable<(double X, double Y, double R, PlanarExtractor.StackBand Top, PlanarExtractor.StackBand Bottom)> Barrels
            => _viaSpans.Select(v => (Drill: Drill(v.Shape), v.Top, v.Bottom))
                        .Where(v => v.Drill is not null)
                        .Select(v => (v.Drill!.Value.X, v.Drill.Value.Y, v.Drill.Value.R, v.Top, v.Bottom));

        /// <summary>The air solid's reserved slot, when the caller asked for one and the box leaves room.</summary>
        public (int Index, int Order, string Material, double Bottom)? AirSlot { get; private set; }

        public string? Prepare()
        {
            double tempC = options.TempC;
            if (source.DbuPerMicron <= 0)
                return $"The layout's resolution is {source.DbuPerMicron} DBU per micron, which is not a " +
                       "usable scale. Set a positive DbuPerMicron on the layout.";
            _perDbu = 1.0 / (source.DbuPerMicron * 1e6);

            // ── The geometry: flattened, then the solve region (R-em3d3-5h) ─────────────────
            // An instance takes the whole layout: the solve region is a .cem's, and a sub-cell's .cem is not consulted. The one
            // instance that carries a region is the in-memory view a 3D .cem with component ports is built through, whose
            // elaborator hands its own layout the .cem's region (C3dElaborator.LayoutRegions).
            var geometry = options.RegionSetup is { } regionSetup
                ? EmGeometry.ForSetup(regionSetup, source)
                : EmGeometry.Flatten(source.View, source.AbsolutePath, source.Technology);
            Notes.AddRange(geometry.Notes);
            if (geometry.RegionRefusal is { } regionRefusal) return regionRefusal;

            var bands = PlanarExtractor.StackBands(tech.Stackup);
            Bands = bands;
            if (bands.Count == 0)
                return $"Technology '{tech.Name}' has no stackup layers, so nothing says how thick the " +
                       "metal is, what is under it, or where the ground plane sits. Add a stackup in " +
                       "the technology editor.";
            _bandByIndex = bands.ToDictionary(b => b.Index);

            // ── Classify: the planar extractor's bindings, read the planar extractor's way ───
            var conductorBinding = new Dictionary<LayerKey, PlanarExtractor.StackBand>();
            foreach (var b in bands.Where(b => b.Layer.Kind == StackupKind.Conductor))
                foreach (var key in b.Layer.DrawingLayers)
                    if (!conductorBinding.TryGetValue(key, out var have) ||
                        (have.Layer.IsGroundReference && !b.Layer.IsGroundReference))
                        conductorBinding[key] = b;   // a signal binding wins, as PlanarExtractor's does

            var viaBinding    = PlanarExtractor.ViaBinding(tech.Stackup, out int nonPlated);
            // The entries ViaBinding leaves out: a non-plated hole is still a hole, and in 3D a hole is air.
            var holeBinding   = new Dictionary<LayerKey, StackupLayer>();
            foreach (var l in tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Via && l.Plated == false))
                foreach (var key in l.DrawingLayers) holeBinding.TryAdd(key, l);
            var outlineLayers = Em3dGenerator.BoardOutlineLayers(tech);
            var byLayer       = _byLayer;

            var conductorShapes = new List<(LayoutShape Shape, int Level)>();
            var filmShapes      = new List<(LayoutShape Shape, int Level)>();
            var filmBinding     = new Dictionary<LayerKey, int>();
            foreach (var b in bands)
                if (b.Layer is { Kind: StackupKind.Conductor, IsGroundReference: false, Films: { } onLevel })
                    foreach (var f in onLevel)
                        if (f.SheetResistanceOhmPerSq > 0) filmBinding.TryAdd(f.Layer, b.Index);
            var viaShapes       = new List<(LayoutShape Shape, StackupLayer Entry)>();
            var holeShapes      = new List<(LayoutShape Shape, StackupLayer Entry)>();
            var outlineShapes   = new List<LayoutShape>();
            var outlinePaths    = new List<LayoutShape>();
            int outlineStrokes  = 0, zeroWidthPaths = 0;

            // A via's pad is copper on its landing layer (PlanarExtractor.WithViaPads), as in the planar model.
            foreach (var s in PlanarExtractor.WithViaPads(geometry.Shapes, conductorBinding.ContainsKey))
            {
                if (s is LabelShape or BitmapShape) continue;
                (byLayer.TryGetValue(s.Layer, out var list) ? list : byLayer[s.Layer] = []).Add(s);

                if (outlineLayers.Contains(s.Layer))
                {
                    if (s is PathShape) outlinePaths.Add(s);
                    else outlineShapes.Add(s);
                    continue;
                }
                if (s is ViaShape vs)
                {
                    if (viaBinding.TryGetValue(vs.Layer, out var entry)) viaShapes.Add((vs, entry));
                    else if (holeBinding.TryGetValue(vs.Layer, out var hole)) holeShapes.Add((vs, hole));
                    continue;
                }
                if (s is PathShape { Width: <= 0 } zp)
                {
                    zeroWidthPaths++;
                    var zxy = zp.Xy;
                    if (zxy.Length >= 8 && zxy[0] == zxy[^2] && zxy[1] == zxy[^1]) _closedCentrelines.Add((zp, s.Layer));
                    continue;
                }
                if (conductorBinding.TryGetValue(s.Layer, out var band))
                {
                    conductorShapes.Add((s, band.Index));
                    continue;
                }
                if (viaBinding.TryGetValue(s.Layer, out var regionEntry)) viaShapes.Add((s, regionEntry));
                else if (holeBinding.TryGetValue(s.Layer, out var regionHole)) holeShapes.Add((s, regionHole));
                // EM-TFR — a film on a level is a sheet of its own, never that level's metal.
                else if (filmBinding.TryGetValue(s.Layer, out int filmLevel)) filmShapes.Add((s, filmLevel));
            }

            if (nonPlated > 0)
                Notes.Add($"{nonPlated} via stackup entr(y/ies) are marked NON-PLATED: their holes are drilled through " +
                          "their span as air, never metal. Their artwork is unchanged.");
            if (zeroWidthPaths > 0)
                Notes.Add($"{zeroWidthPaths} zero-width path(s) are centrelines, not artwork, and are not " +
                          "in the 3D problem.");
            // A stroke that closes on itself (alone or with the strokes it touches) is a line drawn round the board, and
            // BoardRegions reads its hole as the board — which is how Crop to Rectangle redraws a stroked outline. Strokes
            // that close nothing bound no region and stay out, as before: read as regions they would be slivers of board.
            // A single stroke whose ends meet is read along its CENTRE LINE — the edge a Gerber profile draws on and the
            // line Crop to Rectangle redraws the outline along — not along the inside of its ink, which put copper cut
            // at the crop line half a stroke over air.
            var openPaths = new List<LayoutShape>();
            foreach (var path in outlinePaths.Cast<PathShape>())
            {
                var xy = path.Xy;
                bool closed = path.Edges is null && xy.Length >= 8 && xy[0] == xy[^2] && xy[1] == xy[^1];
                if (!closed) { openPaths.Add(path); continue; }
                var ring = new List<EmPoint>(xy.Length / 2 - 1);
                for (int i = 0; i + 3 < xy.Length; i += 2) ring.Add(new EmPoint(xy[i] * _perDbu, xy[i + 1] * _perDbu));
                double twice = 0;   // counter-clockwise, as every other outer ring here
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++) twice += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
                if (twice < 0) ring.Reverse();
                _closedOutlineStrokes.Add(new PlanarPolygon(ring));
            }
            if (openPaths.Count > 0)
            {
                foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                             [.. openPaths.Select(s => (s, 0))], tech, out _, out _, touching: true))
                {
                    var polys = PlanarExtractor.ToPolygons(shape, tech, _perDbu).ToList();
                    if (polys.Any(q => q.HoleRings.Count > 0)) _closedOutlineStrokes.AddRange(polys.Where(q => q.HoleRings.Count > 0));
                    else outlineStrokes++;
                }
            }
            if (outlineStrokes > 0)
                Notes.Add($"{outlineStrokes} shape(s) on the board-outline layer are strokes, which bound " +
                          "no region; only closed shapes there give the board its outline.");

            // ── Merge (R-em3d3-5b): the planar extractor's union, per stackup entry ───────────
            // EM-TFR — clipped against the metal as drawn, before the metal is merged: the same pieces the planar run has.
            var filmPieces = PlanarExtractor.ClipFilmsToMetal(filmShapes, conductorShapes, tech, out int filmsCovered);
            if (filmsCovered > 0) Notes.Add(PlanarExtractor.FilmsCoveredNote(filmsCovered));
            var merged = PlanarExtractor.MergeOverlapping(conductorShapes, tech, out int mergedShapes, out int mergedInto, touching: true);
            if (mergedShapes > 0)
                Notes.Add($"{mergedShapes} overlapping conductor shape(s) were merged into {mergedInto} — " +
                          "copper that overlaps on one level is one conductor, in 3D as in the planar model.");

            var polysByBand = new Dictionary<int, List<(PlanarPolygon Poly, string? Net, LayerKey Layer)>>();
            foreach (var (shape, level) in merged)
                foreach (var poly in PlanarExtractor.ToPolygons(shape, tech, _perDbu))
                    (polysByBand.TryGetValue(level, out var l) ? l : polysByBand[level] = [])
                        .Add((poly, shape.Net is { Length: > 0 } n ? n : null, shape.Layer));

            string? noMetal = polysByBand.Count > 0 ? null :
                $"This EM setup is pointed at geometry with nothing on a layer bound to a " +
                $"conductor entry in technology '{tech.Name}', so there is no metal to put in a 3D " +
                "problem. Draw the artwork on a conductor layer, or bind the layer it is on to a " +
                "conductor entry in the technology editor's Stackup tab.";
            // A picture of wires alone is still a picture: the refusal waits until the wires are known.
            if (noMetal is not null && !options.PadsOptional) return noMetal;
            _noArtwork = noMetal is not null;

            // ── Vias: spans resolved once, geometry after the floor is known ─────────────────
            int unspanned = 0;
            foreach (var (shape, entry) in viaShapes)
            {
                if (ViaSpanResolver.Resolve(entry, tech) is not { } span ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Top)) is not { } top ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Bottom)) is not { } bottom)
                { unspanned++; continue; }
                _viaSpans.Add((shape, entry, top, bottom));
            }
            foreach (var (shape, entry) in holeShapes)
            {
                if (ViaSpanResolver.Resolve(entry, tech) is not { } span ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Top)) is not { } top ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Bottom)) is not { } bottom)
                { unspanned++; continue; }
                _holeSpans.Add((shape, entry, top, bottom));
            }
            if (unspanned > 0)
                Notes.Add($"{unspanned} via(s) are on an entry whose span does not resolve to two conductors " +
                          "of the stackup, and are not in the 3D problem. " +
                          "Name both ends on the via entry (SpanFromLayer / SpanToLayer).");

            // ── The floor (R-em3d3-6a) ────────────────────────────────────────────────────────
            //
            // PEC on the lowest ground-reference conductor when that conductor is the planar model's
            // plane: UNDRAWN (so it is the laterally infinite return the planar kernel terminates on)
            // and with nothing drawn below it. A DRAWN plane is metal with holes in it — an antipad
            // closed by a PEC floor would short the via it exists to clear — so it becomes solids and
            // the floor goes below everything, absorbing.
            var ground = bands.Where(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference)
                              .OrderBy(b => b.BottomM).FirstOrDefault();
            Ground = ground;
            // A via's own pad on that plane does not DRAW it: a VIA/VIAGND cell puts a pad (and a pin) on
            // every layer it joins, and on a microstrip board one of them is the undrawn plane. Counting
            // those pads as a drawn plane turned the floor off, left every port with nothing to return
            // through, and refused the whole setup. When pads are all there is, they are the plane's.
            bool groundPadsOnly = ground is not null
                && polysByBand.TryGetValue(ground.Index, out var onGround)
                && onGround.All(p => IsViaPadOn(p.Poly, ground));
            bool pecFloor = ground is not null
                && (!polysByBand.ContainsKey(ground.Index) || groundPadsOnly)
                && polysByBand.Keys.Where(i => i != ground.Index).All(i => _bandByIndex[i].BottomM >= ground.TopM - 1e-15)
                && _viaSpans.All(v => v.Bottom.BottomM >= ground.BottomM - 1e-15)
                && tech.Bodies.All(body => bands.FirstOrDefault(b => b.Layer.Name == body.SitsOn) is not { } on
                                           || on.TopM >= ground.TopM - 1e-15);
            // The .cem's floor steps §6's rule aside only when it states a DIFFERENT boundary. A ZMin
            // that says Pec, or states only a padding, is the default floor restated: turning the floor
            // off for it left an undrawn ground with no representation and refused every port on it.
            FloorStatedAway = !options.FloorAllowed;
            if (FloorStatedAway) pecFloor = false;
            // An instance has no air box to be floored, and draws only what its layout draws: the undrawn plane
            // the floor would have been is not metal in a 3D view (it was, from brief-em3d-42 R-em3d42-2c, and
            // put copper under a drawn ground plane that nothing in the view could remove).
            if (options.Instance) pecFloor = false;
            if (groundPadsOnly && pecFloor)
            {
                Notes.Add($"{polysByBand[ground!.Index].Count} via pad(s) on '{ground.Layer.Name}' are part of that " +
                          "plane rather than separate metal on it.");
                polysByBand.Remove(ground.Index);
            }
            PecFloor = pecFloor;
            FloorZ = pecFloor ? ground!.TopM : double.NaN;
            double floorZ = FloorZ;

            // Designer round 33 — a DRAWN lowest ground plane that is unbroken (no hole after the merge, so no antipad for a
            // floor to close) and covers the drawn metal's footprint, with nothing drawn or stacked below it, shields everything
            // under it: the air box's floor goes on its underside, as a PEC face, instead of 10 H of air nobody's field reaches.
            // The plane stays geometry; only where the box ends changes.
            // brief-emb-2 R-emb2-5 — a hole is allowed where no other metal lies over it (other than metal on the plane's own
            // net): closing such a hole with the floor shorts nothing. A hole with a via through it, or signal metal over it,
            // keeps the floor off, and the note says where.
            // brief-dfr34-2 R-dfr34-2-4 — the floor is the bottom-most DRAWN conductor, marked ground-reference or not: the flags on
            // an imported board describe the technology file it was read with, not the board, and a 91 % bottom pour left
            // unmarked kept the floor off a 10-layer board. Coverage is what decides it.
            var floorBand = bands.Where(b => b.Layer.Kind == StackupKind.Conductor && polysByBand.ContainsKey(b.Index))
                                 .OrderBy(b => b.BottomM).FirstOrDefault();
            if (!pecFloor && !FloorStatedAway && !options.Instance && floorBand is not null
                && polysByBand.TryGetValue(floorBand.Index, out var plane) && plane.Count > 0
                && bands.All(b => b.BottomM >= floorBand.BottomM - 1e-15)
                && _viaSpans.All(v => v.Bottom.BottomM >= floorBand.BottomM - 1e-15)
                && UnbrokenCover(plane.Select(p => p.Poly).ToList(), polysByBand.Values.SelectMany(l => l).Select(p => p.Poly),
                                 Em3dGenerator.PaddingHeight(polysByBand.Keys.Select(i => (_bandByIndex[i].BottomM, _bandByIndex[i].TopM)),
                                                             bands.Where(b => b.Layer.Kind == StackupKind.Dielectric).Select(b => (b.BottomM, b.TopM)),
                                                             floorZ: null)))
            {
                bool OnNet(LayoutShape via, PlanarExtractor.StackBand top, string? net)
                {
                    if (net is null) return false;
                    if (via.Net is { Length: > 0 } stated) return stated == net;
                    if (ViaFootprint(via).FirstOrDefault() is not { } fp || !polysByBand.TryGetValue(top.Index, out var onTop)) return false;
                    var (x0, y0, x1, y1) = fp.Bounds();
                    double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
                    var landing = onTop.Where(q => q.Poly.Contains(cx, cy)).Select(q => q.Net).Distinct().ToList();
                    return landing.Count == 1 && landing[0] == net;
                }
                foreach (var p in plane.Where(p => p.Poly.HoleRings.Count > 0))
                {
                    var own = p.Net;
                    // A drill on the plane's own net (a ground via) is joined to the plane already, so a floor closing its hole
                    // shorts nothing; only a via on another net (or on none that can be told) keeps the floor off.
                    var drills = _viaSpans.Where(v => !OnNet(v.Shape, v.Top, own))
                                          .Select(v => Drill(v.Shape)).OfType<(double X, double Y, double R)>().ToList();
                    var metal = polysByBand.Values.SelectMany(l => l)
                                           .Where(q => !ReferenceEquals(q.Poly, p.Poly) && !(own is not null && q.Net == own))
                                           .Select(q => Ring(q.Poly.Outer));
                    if (Em3dGenerator.CoveredHole(p.Poly.HoleRings.Select(Ring), metal, drills) is { } covered)
                    {
                        PlaneFloorBlockedAt = covered;
                        break;
                    }
                    PlaneFloorHoles += p.Poly.HoleRings.Count;
                }
                PlaneFloorBand = floorBand;
                if (PlaneFloorBlockedAt is null) PlaneFloorZ = floorBand.BottomM;
                else PlaneFloorHoles = 0;
            }

            // ── Conductor pieces: named, sheet or solid ──────────────────────────────────────
            foreach (var band in bands.Where(b => polysByBand.ContainsKey(b.Index)))
            {
                var list = polysByBand[band.Index];
                string material = ConductorMaterial(band.Layer);
                var mat = _materialByName[material];
                double t = band.TopM - band.BottomM;
                double delta = mat.SigmaSm > 0 && options.FMaxHz is { } fMax
                    ? 1.0 / Math.Sqrt(Math.PI * fMax * Mu0 * mat.Mur * mat.SigmaSm) : 0;
                double sheetZ = SheetZOf(band);

                var netCounts = list.Where(p => p.Net is not null).GroupBy(p => p.Net!, StringComparer.Ordinal)
                                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var netSeen = new Dictionary<string, int>(StringComparer.Ordinal);
                int unnamed = 0;
                var made = new List<Piece>(list.Count);
                foreach (var (poly, net, layer) in list)
                {
                    // R-em3d3-5g — what the user already calls it: the net, else a piece ordinal in
                    // merge order. Both are deterministic: merge order is document order.
                    string name;
                    if (net is not null)
                    {
                        int k = netSeen[net] = netSeen.GetValueOrDefault(net) + 1;
                        name = netCounts[net] == 1 ? $"{band.Layer.Name}/{net}" : $"{band.Layer.Name}/{net}/{k}";
                    }
                    else name = $"{band.Layer.Name}/{++unnamed}";

                    double width = CharacteristicWidth(poly);
                    var (sheet, reason) = SheetRule(band, t, delta, width);
                    made.Add(new Piece(poly, name, material, sheet, band.BottomM, band.TopM, sheetZ, layer, reason, net));
                }
                Pieces[band.Index] = made;
            }
            AddFilmPieces(bands, filmPieces);

            // ── Board outline (R-em3d3-5c) ────────────────────────────────────────────────────
            if (outlineShapes.Count > 0 || _closedOutlineStrokes.Count > 0)
            {
                var drawnOutline = new List<PlanarPolygon>(_closedOutlineStrokes);
                if (outlineShapes.Count > 0)
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. outlineShapes.Select(s => (s, 0))], tech, out _, out _))
                        drawnOutline.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                var drills = _viaSpans.Select(v => Drill(v.Shape)).Concat(_holeSpans.Select(h => Drill(h.Shape)))
                                      .OfType<(double X, double Y, double R)>().ToList();
                int drilledAway = 0;
                var regions = BoardRegions(drawnOutline, out int lines);
                foreach (var region in regions)
                    if (drills.Any(d => WithinDrill(region, d))) drilledAway++;
                    else _outline.Add(RobustBoard(region));
                if (lines > 0)
                    Notes.Add($"{lines} shape(s) on the board-outline layer are a line drawn around the board (a Gerber " +
                              "outline's strokes, merged): the board is the region the line encloses, not the line.");
                if (drilledAway > 0)
                    Notes.Add($"{drilledAway} shape(s) on the board-outline layer are no larger than the hole drilled through " +
                              "them (a fabricator's target), so there is no board there.");
            }

            // ── Which bands are in the problem, and each dielectric's z after absorption ──────
            _inProblem = bands.Where(b => !pecFloor || b.BottomM >= floorZ - 1e-15).ToList();
            foreach (var b in _inProblem.Where(b => b.Layer.Kind == StackupKind.Dielectric))
                _dielZ[b.Index] = (b.BottomM, b.TopM);
            // An INNER conductor band — dielectric directly below AND above — is filled, where no metal
            // is drawn, by the dielectric its sheet faces away from: the one above for a sheet at the
            // band's bottom (the planar rule that makes a microstrip's height the substrate), the one
            // below for SheetAt = Top. An OUTER band is air: the metal sits on the board.
            for (int i = 1; i + 1 < _inProblem.Count; i++)
            {
                var c = _inProblem[i];
                if (c.Layer.Kind != StackupKind.Conductor) continue;
                var below = _inProblem[i - 1];
                var above = _inProblem[i + 1];
                if (below.Layer.Kind != StackupKind.Dielectric || above.Layer.Kind != StackupKind.Dielectric) continue;
                if (SheetOnTop(c))
                    _dielZ[below.Index] = (_dielZ[below.Index].Bottom, c.TopM);
                // PSV-1b — a CONFORMAL dielectric starts on top of the metal: the band beside it stays
                // air, as an outer band already is.
                else if (!Stackup.LeavesBandAsAir(c.Layer, above.Layer))
                    _dielZ[above.Index] = (c.BottomM, _dielZ[above.Index].Top);
                // PSV-4 — and in 3D it FOLLOWS the metal (BuildSolids, ConformalCoat): its thickness on the metal's top,
                // down its sides and on the floor of the metal's band beside it. A dielectric directly above it fills
                // down into what that leaves, as a spin-on layer does.
                else
                {
                    _conformalOver[above.Index] = c.Index;
                    if (i + 2 < _inProblem.Count && _inProblem[i + 2] is { Layer.Kind: StackupKind.Dielectric } fill
                        && fill.Layer.PresentWithLayer is not { Length: > 0 })
                        _fillsConformal[fill.Index] = above.Index;
                }
            }

            // ── Patterned films (PresentWithLayer is simply true in 3D, §4.1a) ─────────────────
            foreach (var b in _inProblem.Where(b => b.Layer.Kind == StackupKind.Dielectric &&
                                                    b.Layer.PresentWithLayer is { Length: > 0 }))
            {
                string tie = b.Layer.PresentWithLayer!;
                // A name in NEITHER namespace leaves the film active, as the planar extractor leaves
                // it (StackupLayer.PresentWithLayer): dropping it on a typo would thin the medium.
                if (!bands.Any(x => x.Layer.Kind == StackupKind.Conductor && x.Layer.Name == tie)
                    && !tech.Layers.Any(l => l.Name == tie))
                {
                    Notes.Add($"Dielectric '{b.Layer.Name}' is tied to '{tie}', which is neither a conductor " +
                              "entry nor a drawing layer, so it is kept everywhere, as the planar solver keeps it.");
                    continue;
                }
                var polys = new List<PlanarPolygon>();
                if (bands.FirstOrDefault(x => x.Layer.Kind == StackupKind.Conductor && x.Layer.Name == tie) is { } plate)
                    polys.AddRange(Pieces.TryGetValue(plate.Index, out var pl) ? pl.Select(p => p.Poly) : []);
                else if (tech.Layers.FirstOrDefault(l => l.Name == tie) is { } mask &&
                         byLayer.TryGetValue(mask.Key, out var maskShapes))
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. maskShapes.Select(s => (s, 0))], tech, out _, out _))
                        polys.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                _filmPolys[b.Index] = polys;
                if (polys.Count == 0)
                    Notes.Add($"Dielectric '{b.Layer.Name}' exists only where '{tie}' is drawn, and nothing " +
                              "is drawn there, so it is not in the 3D problem.");
            }

            // ── Openings (PSV-2): a dielectric that exists everywhere except where its mask is drawn ──
            // Gathered here, cut in BuildSolids, where the slab's lateral outline is known. A tie that also
            // carries PresentWithLayer is a validation error; the patterned film above wins, and nothing is cut.
            foreach (var b in _inProblem.Where(b => b.Layer.Kind == StackupKind.Dielectric &&
                                                    b.Layer.OpenWithLayer is { Length: > 0 } &&
                                                    b.Layer.PresentWithLayer is not { Length: > 0 }))
            {
                string opening = b.Layer.OpenWithLayer!;
                if (tech.Layers.FirstOrDefault(l => l.Name == opening) is not { } mask)
                {
                    Notes.Add($"Dielectric '{b.Layer.Name}' is opened by '{opening}', which is not a drawing layer, so " +
                              "the slab is whole.");
                    continue;
                }
                var polys = new List<PlanarPolygon>();
                if (byLayer.TryGetValue(mask.Key, out var maskShapes))
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. maskShapes.Select(s => (s, 0))], tech, out _, out _))
                        polys.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                if (polys.Count == 0)
                {
                    Notes.Add($"Dielectric '{b.Layer.Name}' is opened by '{opening}', and nothing is drawn there, so " +
                              "the slab is whole.");
                    continue;
                }
                // An opening over bare substrate is legal and usually a drawing mistake: a note, not a refusal.
                var metalBelow = Pieces.Where(kv => _bandByIndex[kv.Key].TopM <= b.TopM + 1e-15)
                                       .SelectMany(kv => kv.Value).Select(p => p.Poly).ToList();
                int bare = polys.Count(o => SlabLateralBound.IntersectionArea([o], metalBelow) <= 0);
                if (bare > 0)
                    Notes.Add($"{bare} of the {polys.Count} opening(s) on '{opening}' in '{b.Layer.Name}' lie over no " +
                              "conductor, so they expose bare substrate. That is legal, and is usually a drawing mistake.");
                _openings[b.Index] = (opening, polys);
            }

            // ── Bodies (brief 2's, R-em3d3-5e) ────────────────────────────────────────────────
            foreach (var body in tech.Bodies)
            {
                var on = bands.FirstOrDefault(b => b.Layer.Name == body.SitsOn);
                if (on is null)
                    return $"Body '{body.Name}' sits on '{body.SitsOn}', which is not a conductor or " +
                           $"dielectric entry of technology '{tech.Name}'s stackup, so it has no height to " +
                           "start from.";
                if (tech.FindMaterial(body.Material) is null)
                    return $"Body '{body.Name}' is made of '{body.Material}', which technology " +
                           $"'{tech.Name}' does not define in its Materials.";
                var polys = new List<PlanarPolygon>();
                var outlineOperands = body.OutlineLayers.SelectMany(k => byLayer.TryGetValue(k, out var l) ? l : [])
                                                        .Where(s => s is not PathShape { Width: <= 0 }).ToList();
                if (outlineOperands.Count > 0)
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. outlineOperands.Select(s => (s, 0))], tech, out _, out _))
                        polys.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                else if (body.OutlineLayers.Count > 0)
                {
                    Notes.Add($"Body '{body.Name}' takes its outline from layers on which nothing is drawn, " +
                              "so it is not in the 3D problem.");
                    continue;
                }
                _bodies.Add((body, on.TopM, on.TopM + body.ThicknessDbu * StackPerDbu, polys));
            }

            // ── Bond wires (brief-em3d-4): the stem-paired .wBond, landed on this problem's pieces ──
            var wireSource = wires;
            if (wireSource is null)
            {
                wireSource = Em3dWireSource.ForLayout(source.AbsolutePath, out string? wbNote, out string? wbRefusal);
                if (wbRefusal is not null) return wbRefusal;
                if (wbNote is not null) Notes.Add(wbNote);
            }
            if (wireSource is { Design.WireCount: > 0 })
            {
                // The wire model's z = 0 is the top of the lowest ground-reference conductor
                // (WBondLayerHeights' convention, the plane kernel W images in).
                double zOrigin;
                if (ground is not null) zOrigin = ground.TopM;
                else
                {
                    zOrigin = bands.Min(b => b.BottomM);
                    Notes.Add($"Technology '{tech.Name}' designates no ground-reference conductor, so the wires' " +
                              "z = 0 is taken as the bottom of the stack. Mark the ground plane to place them " +
                              "where kernel W does.");
                }
                var pads = Pieces.Values.SelectMany(l => l)
                                 .Select(p => new Em3dWirePad(p.Name, p.Poly, p.IsSheet ? p.SheetZ : p.ZTop)).ToList();
                string wbondSource = wireSource.Path is { } wbPath
                    ? $".wBond '{Path.GetFileName(wbPath)}' Materials" : "the .wBond's Materials";
                WireBuild = Em3dWires.Build(wireSource, pads, zOrigin, tech, tempC,
                                            (m, fromWBond) => Add(m, fromWBond ? wbondSource : TechnologySource(m.Name)),
                                            options.PadsOptional);
                Notes.AddRange(WireBuild.Notes);
                WarningList.AddRange(WireBuild.Warnings);
                if (WireBuild.Refusal is { } wireRefusal)
                {
                    RefusedOnWires = true;
                    if (!options.PadsOptional) return wireRefusal;
                    // A picture still draws the artwork: the .wBond as a whole could not be built.
                    WarningList.Add($"The wires are not drawn, invalid wBond geometry: {wireRefusal}");
                    _wireRefusal = wireRefusal;
                    WireBuild = null;
                }
            }
            if (noMetal is not null && WireBuild is not { Solids.Count: > 0 })
            {
                // Nothing to draw. When there were wires and none could be built, they are the reason.
                if (_wireRefusal is null && WireBuild?.PadRefusal is null) return noMetal;
                RefusedOnWires = true;
                return _wireRefusal ?? WireBuild!.PadRefusal!;
            }
            RefusedOnWires = false;
            PadRefusal = noMetal ?? _wireRefusal ?? WireBuild?.PadRefusal;

            Extent();
            return null;
        }

        /// <summary>PSV-4 — every piece of metal on a conductor band, as drawn.</summary>
        private List<PlanarPolygon> MetalOf(int band) => Pieces.TryGetValue(band, out var ps) ? [.. ps.Select(p => p.Poly)] : [];

        /// <summary>
        /// PSV-4 — a conformal cover of thickness <paramref name="t"/> over the metal of <paramref name="band"/>, inside
        /// <paramref name="region"/> (the cover's outline less its openings): Field, the coat on the band's floor away from
        /// the metal; Walls, the ring of width t round the metal; Top, the coat on the metal. Disjoint by construction.
        /// </summary>
        private (IReadOnlyList<PlanarPolygon> Field, IReadOnlyList<PlanarPolygon> Walls, IReadOnlyList<PlanarPolygon> Top)
            ConformalCoat(int band, double t, IReadOnlyList<PlanarPolygon> region)
        {
            var metal = MetalOf(band);
            if (metal.Count == 0) return (region, [], []);
            var grown = SlabLateralBound.Inflate(metal, t);
            return (SlabLateralBound.Subtract(region, grown),
                    SlabLateralBound.Intersect(SlabLateralBound.Subtract(grown, metal), region),
                    SlabLateralBound.Intersect(metal, region));
        }

        /// <summary>The content bounds (R-em3d3-6), from everything Prepare built.</summary>
        private void Extent()
        {
            double cx0 = double.PositiveInfinity, cy0 = cx0, cx1 = double.NegativeInfinity, cy1 = cx1;
            void Grow(PlanarPolygon p)
            {
                var (a, b, c, d) = p.Bounds();
                cx0 = Math.Min(cx0, a); cy0 = Math.Min(cy0, b); cx1 = Math.Max(cx1, c); cy1 = Math.Max(cy1, d);
            }
            foreach (var list in Pieces.Values) foreach (var p in list) Grow(p.Poly);
            foreach (var p in _outline) Grow(p);
            foreach (var list in _filmPolys.Values) foreach (var p in list) Grow(p);
            foreach (var bd in _bodies) foreach (var p in bd.Polys) Grow(p);
            // R-dfr28-3-0a — an edge-cut drill bounds the content by the part of it that is built (ClipDisc), so a zero-padded
            // face ends at the board edge rather than 150 µm past it, where nothing is
            foreach (var v in _viaSpans)
                foreach (var p in Drill(v.Shape) is var (x, y, r) && !OnBoard(x, y, r) ? ClipDisc(x, y, r) : ViaFootprint(v.Shape)) Grow(p);

            double zLow = double.PositiveInfinity, zHigh = double.NegativeInfinity;
            foreach (var (b, tp) in _dielZ)
                if (!_filmPolys.TryGetValue(b, out var fp) || fp.Count > 0)
                { zLow = Math.Min(zLow, tp.Bottom); zHigh = Math.Max(zHigh, tp.Top); }
            foreach (var list in Pieces.Values)
                foreach (var p in list)
                {
                    zLow  = Math.Min(zLow,  p.IsSheet ? p.SheetZ : p.ZBottom);
                    zHigh = Math.Max(zHigh, p.IsSheet ? p.SheetZ : p.ZTop);
                }
            foreach (var bd in _bodies) { zLow = Math.Min(zLow, bd.ZBottom); zHigh = Math.Max(zHigh, bd.ZTop); }
            foreach (var v in _viaSpans)
            {
                zLow  = Math.Min(zLow, PecFloor && ReferenceEquals(v.Bottom.Layer, Ground!.Layer) ? FloorZ : ViaBottomZ(v.Bottom));
                zHigh = Math.Max(zHigh, ViaTopZ(v.Top));
            }
            foreach (var (_, _, prim) in WireBuild?.Solids ?? [])
            {
                var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(prim);
                cx0 = Math.Min(cx0, x0); cy0 = Math.Min(cy0, y0); cx1 = Math.Max(cx1, x1); cy1 = Math.Max(cy1, y1);
                zLow = Math.Min(zLow, z0); zHigh = Math.Max(zHigh, z1);
            }
            (Cx0, Cy0, Cx1, Cy1, ZLow, ZHigh) = (cx0, cy0, cx1, cy1, zLow, zHigh);
        }

        /// <summary>
        /// The solids and sheets, in construction order (R-em3d3-1d). <paramref name="fullExtent"/> is what a
        /// laterally unbounded slab spans in x and y: the generator's air box, or — null, an instance — the
        /// board outline if there is one (which every slab already takes) else the drawn geometry's bounding
        /// box. <paramref name="reserveAir"/> reserves the air-above-the-stack's slot, for the generator.
        /// </summary>
        public void BuildSolids(Em3dAirBox? fullExtent, bool reserveAir)
        {
            var solids = SolidList;
            var sheets = SheetList;
            int order = 0;
            double x0 = fullExtent?.Min.X ?? Cx0, y0 = fullExtent?.Min.Y ?? Cy0;
            double x1 = fullExtent?.Max.X ?? Cx1, y1 = fullExtent?.Max.Y ?? Cy1;
            int bounded = 0, unboundedDielectrics = 0;
            // An instance (no air box) or a planar preview: a slab with no outline of its own takes SlabLateralBound's.
            bool bound = fullExtent is null || options.DisplaySlabs;
            _displayBox = options.DisplaySlabs ? fullExtent : null;

            Em3dPrimitive FullExtent(double z0, double z1)
            {
                if (fullExtent is null) bounded++;
                return new Em3dBox(new Point3(x0, y0, z0), new Point3(x1, y1, z1));
            }

            // PSV-4 — one piece of a conformal cover, or of the fill above one, as dielectric solids of the entry's own
            // material at the current order: `name`, `name/2`… for the cover itself, `name/wall`, `name/top/2`… for the rest.
            void Emit(string entry, string part, IReadOnlyList<PlanarPolygon> polys, double z0, double z1)
            {
                if (z1 <= z0) return;
                var layer = _inProblem.First(x => x.Layer.Name == entry).Layer;
                string material = DielectricMaterial(layer);
                string stem = part.Length == 0 ? entry : $"{entry}/{part}";
                for (int k = 0; k < polys.Count; k++)
                    solids.Add(Origin(new Em3dSolid(polys.Count == 1 ? stem : $"{stem}/{k + 1}", material, Em3dRole.Dielectric,
                                                    Extrude(polys[k], z0, z1), order),
                                      Em3dObjectKind.Dielectric, entry));
            }

            //
            // Bottom-up: dielectrics, then the air above the stack, then bodies, then conductors (metal
            // wins over what it is embedded in), then vias, then bond wires (brief 4). STATED
            // here so no backend infers it.
            double airBottom = PecFloor ? FloorZ : double.NaN;
            foreach (var b in _inProblem.Where(b => _dielZ.ContainsKey(b.Index)))
            {
                var (z0, z1) = _dielZ[b.Index];
                string material = DielectricMaterial(b.Layer);
                IReadOnlyList<PlanarPolygon>? lateral =
                    _filmPolys.TryGetValue(b.Index, out var film) ? film : bound ? Bounded(b.Layer.Name)
                    : _outline.Count > 0 ? _outline : null;
                if (_openings.TryGetValue(b.Index, out var opened))
                {
                    // PSV-2 — the slab's outline as it would be without the field, less the openings: one solid with
                    // hole rings (a notch where an opening crosses its edge), never an air solid ordered above it.
                    if (lateral is null)
                    {
                        if (fullExtent is not null) unboundedDielectrics++;
                        lateral = [new PlanarPolygon([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)])];
                    }
                    var whole = lateral;
                    lateral = SlabLateralBound.Subtract(whole, opened.Polys);
                    int cut = opened.Polys.Count(o => SlabLateralBound.IntersectionArea(whole, [o]) > 0);
                    double areaM2 = whole.Sum(p => p.Area()) - lateral.Sum(p => p.Area());
                    Notes.Add($"Dielectric '{b.Layer.Name}' is opened by '{opened.Mask}': {cut} opening(s) cut through " +
                              $"it, {Fmt(areaM2 * 1e12)} µm² in all, with straight walls.");
                }
                if (lateral is { Count: 0 }) continue;
                if (_conformalOver.TryGetValue(b.Index, out int follows))
                {
                    // PSV-4 — a conformal cover follows the metal. Three pieces that do not overlap, so no backend's
                    // overlap order decides anything: on the floor of the metal's band away from the metal, a wall round
                    // each piece of metal up to the coat's top, and a coat on the metal's top. Openings are already out
                    // of `lateral`.
                    order++;
                    var region = lateral ?? [new PlanarPolygon([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)])];
                    _dielectricOutlines.AddRange(region);
                    var coat = ConformalCoat(follows, z1 - z0, region);
                    var c = _bandByIndex[follows];
                    Emit(b.Layer.Name, "", coat.Field, c.BottomM, c.BottomM + (z1 - z0));
                    Emit(b.Layer.Name, "wall", coat.Walls, c.BottomM, z1);
                    Emit(b.Layer.Name, "top", coat.Top, z0, z1);
                    airBottom = double.IsNaN(airBottom) ? z1 : Math.Max(airBottom, z1);
                    continue;
                }
                order++;
                if (lateral is not null) _dielectricOutlines.AddRange(lateral);
                if (_fillsConformal.TryGetValue(b.Index, out int coatIndex) && _conformalOver.TryGetValue(coatIndex, out int metalBand))
                {
                    // PSV-4 — the dielectric directly on a conformal cover fills what the cover's topography leaves beside
                    // the metal, down to the coat on the band's floor, as a spin-on layer does; over the metal it starts
                    // where it always did.
                    var region = lateral ?? [new PlanarPolygon([new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)])];
                    var (c0, c1) = _dielZ[coatIndex];
                    double t = c1 - c0;
                    var c = _bandByIndex[metalBand];
                    var coatOpen = _openings.TryGetValue(coatIndex, out var co) ? co.Polys : [];
                    var grown = SlabLateralBound.Inflate(MetalOf(metalBand), t);
                    if (c.TopM > c.BottomM + t)
                        Emit(b.Layer.Name, "fill", SlabLateralBound.Subtract(region, grown), c.BottomM + t, c.TopM);
                    Emit(b.Layer.Name, "fill-top", SlabLateralBound.Subtract(region, SlabLateralBound.Subtract(grown, coatOpen)), c.TopM, c1);
                }
                if (lateral is null)
                {
                    solids.Add(Origin(new Em3dSolid(b.Layer.Name, material, Em3dRole.Dielectric, FullExtent(z0, z1), order),
                                      Em3dObjectKind.Dielectric, b.Layer.Name));
                    if (fullExtent is not null) unboundedDielectrics++;
                }
                else
                    for (int k = 0; k < lateral.Count; k++)
                        solids.Add(Origin(new Em3dSolid(lateral.Count == 1 ? b.Layer.Name : $"{b.Layer.Name}/{k + 1}",
                                                        material, Em3dRole.Dielectric, Extrude(lateral[k], z0, z1), order),
                                          Em3dObjectKind.Dielectric, b.Layer.Name));
                if (film is null) airBottom = double.IsNaN(airBottom) ? z1 : Math.Max(airBottom, z1);
            }

            // Designer round 33: a board whose edge was drawn only as closed zero-width paths on its copper layers had an
            // empty outline layer, so every dielectric reached the air box — a laminate three boards wide in the preview,
            // with nothing saying why. Said only when such a path goes round the drawn geometry: a laminate with no edge
            // is otherwise the ordinary, intended model (every shipped line and coupon is one).
            if (unboundedDielectrics > 0 && BoardEdgeCentreline() is { } edgeLayer)
            {
                var outlineNames = tech.Layers.Where(l => Em3dGenerator.BoardOutlineLayers(tech).Contains(l.Key)).Select(l => $"'{l.Name}'").ToList();
                Notes.Add($"No board outline is drawn: {unboundedDielectrics} dielectric(s) reach the air box's sides, as a laminate " +
                          $"with no edge. A zero-width path on '{tech.Layers.FirstOrDefault(l => l.Key == edgeLayer)?.Name ?? edgeLayer.ToString()}' goes round " +
                          "the whole layout; if it is the board's edge, put it on " + (outlineNames.Count > 0
                              ? $"{string.Join(" or ", outlineNames)}"
                              : "a layer the technology marks as the board outline (Edge.Cuts)") + " and the dielectric ends there.");
            }

            if (reserveAir && fullExtent is { } box)
            {
                if (double.IsNaN(airBottom)) airBottom = box.Min.Z;
                if (box.Max.Z > airBottom)
                    AirSlot = (solids.Count, ++order, AirMaterialName(), airBottom);
            }

            foreach (var (body, z0, z1, drawnPolys) in _bodies)
            {
                string material = BodyMaterial(tech.FindMaterial(body.Material)!, out var role);
                IReadOnlyList<PlanarPolygon> polys = drawnPolys.Count == 0 && bound ? Bounded(body.Name) : drawnPolys;
                if (polys.Count == 0 && (options.DisplaySlabs || (bound && _noArtwork))) continue;   // nothing drawn bounds it: not in the picture
                order++;
                if (polys.Count == 0)
                    solids.Add(Origin(new Em3dSolid(body.Name, material, role, FullExtent(z0, z1), order),
                                      Em3dObjectKind.Body, null));
                else
                    for (int k = 0; k < polys.Count; k++)
                        solids.Add(Origin(new Em3dSolid(polys.Count == 1 ? body.Name : $"{body.Name}/{k + 1}",
                                                        material, role, Extrude(polys[k], z0, z1), order),
                                          Em3dObjectKind.Body, null));
            }

            foreach (var b in Bands)
            {
                if (!Pieces.ContainsKey(b.Index)) continue;
                foreach (var p in Pieces[b.Index])
                {
                    order++;
                    OriginMap[p.Name] = new Em3dObjectOrigin(Em3dObjectKind.Conductor, b.Layer.Name, p.Layer, p.SheetReason);
                    Net(p.Name, p.Net);
                    if (b.Layer.IsGroundReference) GroundBandObjectList.Add(p.Name);
                    var poly = Robust(CloseSlots(p.Poly, p.ZTop - p.ZBottom, Pieces[b.Index]), p.ZTop - p.ZBottom);
                    if (p.IsSheet)
                        sheets.Add(new Em3dSheet(p.Name, p.Material, Ring(poly.Outer),
                                                 [.. poly.HoleRings.Select(Ring)], p.SheetZ, p.ZTop - p.ZBottom, order));
                    else
                        solids.Add(new Em3dSolid(p.Name, p.Material, Em3dRole.Conductor,
                                                 Extrude(poly, p.ZBottom, p.ZTop), order));
                }
            }
            if (_robust.Removed > 0 || _robust.Snapped > 0 || _robust.Touches > 0 || _robust.Slotted > 0)
                Notes.Add(RobustNote(_robust));
            int sheetCount = Bands.Where(b => Pieces.ContainsKey(b.Index)).Sum(b => Pieces[b.Index].Count(p => p.IsSheet && !p.IsFilm));
            if (sheetCount > 0 && options.Conductors == Em3dConductorMode.Sheets)
                Notes.Add($"{sheetCount} conductor piece(s) are sheets because this setup states Conductors: Sheets, the planar " +
                          "model's own idealisation: each carries its conductivity and thickness rather than a meshed volume.");
            else if (sheetCount > 0)
                Notes.Add($"{sheetCount} conductor piece(s) are thinner than {Em3dGenerator.SheetMaxSkinDepths:G} skin depths " +
                          $"at {options.FMaxHz / 1e9:G4} GHz and than {Em3dGenerator.SheetMaxFractionOfWidth:G} of their own width, so " +
                          "they are sheets carrying their conductivity and thickness rather than meshed " +
                          "volumes. Both thresholds are provisional.");

            // Vias: R-em3d3-5d. A plated barrel with a wall is a TUBE — the plating cylinder, and inside
            // it a higher-order cylinder of air, which is §1d's order rule doing the subtraction.
            int viaN = 0;
            var edgeCut = new List<(string Name, double X, double Y)>();
            var offBoard = new List<string>();
            foreach (var (shape, entry, top, bottom) in _viaSpans)
            {
                viaN++;
                string material = ConductorMaterial(entry);
                double z1 = ViaTopZ(top);
                double z0 = PecFloor && ReferenceEquals(bottom.Layer, Ground!.Layer) ? FloorZ : ViaBottomZ(bottom);
                string name = $"via/{viaN}";
                // A via is on its own net when it names one, else on the nets of the metal it lands on.
                var viaNets = shape.Net is { Length: > 0 } vn ? [vn] : ViaLandingNets(shape, top, bottom);
                if (shape.Net is { Length: > 0 } own && ViaPassedNets(shape, top, bottom, own) is { Count: > 0 } other)
                {
                    // brief-dfr34-4 R-dfr34-4-4 — the drill shape is named by its layer and its place on it, because a click in
                    // 2D at the via usually selects the pad over it, whose net is the copper's.
                    var fix = ViaFix(shape, own, other[0].Net);
                    string at = $"the via at ({Fmt(other[0].X * 1e3)}, {Fmt(other[0].Y * 1e3)}) mm ({fix.ShapeText(tech)})";
                    if (other.Count == 1 && !ViaPassesNet(shape, top, bottom, own))
                    {
                        // D1 (owner, 2026-11-04) — none of its copper is on its stated net, and all of it is on one other: the via
                        // is one piece of metal with that copper, so the stated net cannot be what is built. It takes the copper's.
                        viaNets = [other[0].Net];
                        ViaNetAdoptionMap[name] = new Em3dViaNetAdoption(other[0].Net,
                            $"{char.ToUpperInvariant(at[0])}{at[1..]} is on net '{own}', and every piece of copper it passes is on net " +
                            $"'{other[0].Net}' ({other[0].Layers}), so this solve puts it on '{other[0].Net}': it is one piece of metal " +
                            "with that copper.", fix);
                    }
                    else
                        ViaNetConflictMap[name] = $"{at} is on net '{own}' " +
                            $"and passes through copper of {string.Join(" and ", other.Select(o => $"net '{o.Net}' on {o.Layers}"))}";
                }
                if (Drill(shape) is var (x, y, r) && OnBoard(x, y, r))
                {
                    var a = new Point3(x, y, z0);
                    var e = new Point3(x, y, z1);
                    solids.Add(Origin(new Em3dSolid(name, material, Em3dRole.Conductor, new Em3dCylinder(a, e, r), ++order),
                                      Em3dObjectKind.Via, entry.Name, shape.Layer));
                    foreach (string n in viaNets) Net(name, n);
                    double wall = (entry.WallThicknessDbu ?? 0) * StackPerDbu;
                    if (Hollow(entry) && wall < r)
                        solids.Add(Origin(new Em3dSolid(name + "/fill", AirMaterialName(), Em3dRole.Air,
                                                        new Em3dCylinder(a, e, r - wall), ++order),
                                          Em3dObjectKind.Air, entry.Name));
                }
                else if (Drill(shape) is var (cx, cy, cr))
                {
                    // R-dfr28-1-1 — the board edge cuts this barrel: only the part on the board is built, tube and fill
                    // alike, as the disc's intersection with the board extruded. The cut face lies in the board's side.
                    var cut = ClipDisc(cx, cy, cr);
                    if (cut.Count == 0) { offBoard.Add(name); continue; }
                    edgeCut.Add((name, cx, cy));
                    order++;
                    for (int k = 0; k < cut.Count; k++)
                    {
                        string piece = cut.Count == 1 ? name : $"{name}/{k + 1}";
                        solids.Add(Origin(new Em3dSolid(piece, material, Em3dRole.Conductor, Extrude(cut[k], z0, z1), order),
                                          Em3dObjectKind.Via, entry.Name, shape.Layer));
                        foreach (string n in viaNets) Net(piece, n);
                    }
                    double wall = (entry.WallThicknessDbu ?? 0) * StackPerDbu;
                    if (Hollow(entry) && wall < cr && ClipDisc(cx, cy, cr - wall) is { Count: > 0 } fill)
                    {
                        order++;
                        for (int k = 0; k < fill.Count; k++)
                            solids.Add(Origin(new Em3dSolid(fill.Count == 1 ? name + "/fill" : $"{name}/fill/{k + 1}",
                                                            AirMaterialName(), Em3dRole.Air, Extrude(fill[k], z0, z1), order),
                                              Em3dObjectKind.Air, entry.Name));
                    }
                }
                else
                {
                    var fp = PlanarExtractor.ToPolygons(shape, tech, _perDbu);
                    order++;
                    for (int k = 0; k < fp.Count; k++)
                    {
                        string piece = fp.Count == 1 ? name : $"{name}/{k + 1}";
                        solids.Add(Origin(new Em3dSolid(piece, material, Em3dRole.Conductor, Extrude(fp[k], z0, z1), order),
                                          Em3dObjectKind.Via, entry.Name, shape.Layer));
                        foreach (string n in viaNets) Net(piece, n);
                    }
                }
            }

            if (ViaFillNote() is { } fillNote) Notes.Add(fillNote);

            // Non-plated holes: air, after every metal so the order rule drills it (an Air solid drawn after the first
            // metal is in the metal band, Em3dPrecedence). A conductor that is a SHEET is a surface, and is not cut.
            int holeN = 0;
            foreach (var (shape, entry, top, bottom) in _holeSpans)
            {
                string name = $"hole/{++holeN}";
                double z0 = bottom.BottomM, z1 = top.TopM;
                if (Drill(shape) is var (x, y, r) && OnBoard(x, y, r))
                {
                    solids.Add(Origin(new Em3dSolid(name, AirMaterialName(), Em3dRole.Air,
                                                    new Em3dCylinder(new Point3(x, y, z0), new Point3(x, y, z1), r), ++order),
                                      Em3dObjectKind.Air, entry.Name, shape.Layer));
                }
                else if (Drill(shape) is var (cx, cy, cr))
                {
                    var cut = ClipDisc(cx, cy, cr);
                    if (cut.Count == 0) { offBoard.Add(name); continue; }
                    edgeCut.Add((name, cx, cy));
                    order++;
                    for (int k = 0; k < cut.Count; k++)
                        solids.Add(Origin(new Em3dSolid(cut.Count == 1 ? name : $"{name}/{k + 1}", AirMaterialName(), Em3dRole.Air,
                                                        Extrude(cut[k], z0, z1), order),
                                          Em3dObjectKind.Air, entry.Name, shape.Layer));
                }
                else
                {
                    var fp = PlanarExtractor.ToPolygons(shape, tech, _perDbu);
                    order++;
                    for (int k = 0; k < fp.Count; k++)
                        solids.Add(Origin(new Em3dSolid(fp.Count == 1 ? name : $"{name}/{k + 1}", AirMaterialName(), Em3dRole.Air,
                                                        Extrude(fp[k], z0, z1), order),
                                          Em3dObjectKind.Air, entry.Name, shape.Layer));
                }
            }
            if (edgeCut.Count > 0)
                Notes.Add($"{edgeCut.Count} via(s) cross the board edge, the first ('{edgeCut[0].Name}') at " +
                          $"({Fmt(edgeCut[0].X * 1e3)}, {Fmt(edgeCut[0].Y * 1e3)}) mm: each is built as the part of its " +
                          "barrel on the board. The rest would stand in the air beside the board, where the mesher " +
                          "cannot close it against the board's side.");
            if (offBoard.Count > 0)
                Notes.Add($"{string.Join(", ", offBoard.Select(n => $"'{n}'"))} {(offBoard.Count == 1 ? "lies" : "lie")} " +
                          "wholly off the board and " + (offBoard.Count == 1 ? "is" : "are") + " not in the 3D problem.");
            if (holeN > 0 && sheetCount > 0)
                Notes.Add($"{holeN} non-plated hole(s) are drilled as air through meshed conductors only: a conductor " +
                          "piece that is a sheet is a surface and keeps its metal over a hole.");

            // Bond wires after vias (R-em3d3-1d's order, as brief 3 left room for): each swept wire,
            // then its balls, which meet it face to face on the ball's top.
            var pieceNet = Pieces.Values.SelectMany(l => l).ToDictionary(p => p.Name, p => p.Net, StringComparer.Ordinal);
            foreach (var (name, material, prim) in WireBuild?.Solids ?? [])
            {
                solids.Add(Origin(new Em3dSolid(name, material, Em3dRole.Conductor, prim, ++order),
                                  Em3dObjectKind.Wire, null));
                // A wire (and its balls) is on its array's name and on the nets of the pads it joins.
                if (WireBuild!.Reports.FirstOrDefault(r => name == r.Name || name.StartsWith(r.Name + "/", StringComparison.Ordinal))
                    is { } wr)
                {
                    Net(name, wr.Array);
                    Net(name, pieceNet.GetValueOrDefault(wr.Start.Pad));
                    Net(name, pieceNet.GetValueOrDefault(wr.End.Pad));
                }
            }

            if (fullExtent is null && (bounded > 0 || _slabBounds.Count > 0))
                Notes.Add(SlabBoundNote());
            ShareCutVertices(solids, sheets);
        }

        /// <summary>Makes each crossing <see cref="ClipDisc"/> cut on a board edge a corner of everything else lying along that edge
        /// (<see cref="Em3dOutlineCleanup.InsertOnEdges"/>). The drill's clip still reads the board as drawn.</summary>
        private void ShareCutVertices(List<Em3dSolid> solids, List<Em3dSheet> sheets)
        {
            if (_cutVertices.Count == 0) return;
            List<Point2> cuts = [.. _cutVertices.Select(q => new Point2(q.X, q.Y))];
            IReadOnlyList<Point2> On(IReadOnlyList<Point2> ring) =>
                Em3dOutlineCleanup.InsertOnEdges(ring, q => (q.X, q.Y), cuts, Em3dOutlineCleanup.CutVertexToleranceM);
            for (int i = 0; i < solids.Count; i++)
                if (solids[i].Primitive is Em3dExtrudedPolygon e)
                    solids[i] = solids[i] with { Primitive = e with { Outline = On(e.Outline), Holes = [.. e.Holes.Select(On)] } };
            for (int i = 0; i < sheets.Count; i++)
                if (sheets[i].Frame is null)
                    sheets[i] = sheets[i] with { Outline = On(sheets[i].Outline), Holes = [.. sheets[i].Holes.Select(On)] };
        }

        // ── Lateral bounds (SlabLateralBound) ───────────────────────────────────────────────────────
        private readonly List<string> _slabBounds = [];
        private Em3dAirBox? _displayBox;

        /// <summary>
        /// The lateral shape of a slab that has no outline of its own: the board outline, else ONE rectangle for every
        /// slab, the bounding box of what the layout draws. Nothing reaches past the artwork: a margin or a hull made a
        /// substrate nobody drew, a different shape on each slab.
        /// </summary>
        private IReadOnlyList<PlanarPolygon> Bounded(string slab)
        {
            _slabBounds.Add(slab);
            if (_outline.Count > 0) return _outline;
            // Wires alone, on a layout with no artwork, bound no substrate.
            if (_noArtwork || !(Cx1 > Cx0 && Cy1 > Cy0)) return [];
            IReadOnlyList<PlanarPolygon> box = [new PlanarPolygon([new(Cx0, Cy0), new(Cx1, Cy0), new(Cx1, Cy1), new(Cx0, Cy1)])];
            return _displayBox is { } d ? SlabLateralBound.ClipToRect(box, d.Min.X, d.Min.Y, d.Max.X, d.Max.Y) : box;
        }

        /// <summary>What an instance's note says: which bound its slabs took.</summary>
        private string SlabBoundNote() => _outline.Count > 0
            ? "Placed in a 3D view, the layout's slabs are bounded by its board outline."
            : "Placed in a 3D view, the layout draws no board outline, so its slabs are bounded by the box round its " +
              "drawn geometry. Draw a board outline when the board's edge matters.";

        /// <summary>The temperature notes, which the generator writes after its terminals.</summary>
        public void FinishNotes()
        {
            if (_unknownTemperature.Count > 0)
                Notes.Add($"{string.Join(", ", _unknownTemperature.Select(n => $"'{n}'"))} " +
                          $"name{(_unknownTemperature.Count == 1 ? "s" : "")} no material, so " +
                          $"{(_unknownTemperature.Count == 1 ? "its" : "their")} σ is a number of unknown " +
                          $"temperature and is used as given at {Fmt(options.TempC)} °C. Name a material with an " +
                          "α₂₀ to have it evaluated at the operating temperature.");
            if (_noAlpha.Count > 0)
                Notes.Add($"{string.Join(", ", _noAlpha.Select(n => $"'{n}'"))} state" +
                          $"{(_noAlpha.Count == 1 ? "s" : "")} no α₂₀, so σ₂₀ is used at every temperature, " +
                          $"{Fmt(options.TempC)} °C included.");
        }

        public IReadOnlyList<Em3dMaterial> Materials => _materials;
        public IReadOnlyDictionary<string, string> MaterialSources => _materialSource;
        public IReadOnlyList<string> NoAlphaList => [.. _noAlpha.Union(WireBuild?.NoAlpha ?? []).Order(StringComparer.Ordinal)];
        public IReadOnlyList<string> UnknownTemperatureList => _unknownTemperature;

        public Em3dLayoutSolidsResult Failed(string refusal) => new([], [], [], refusal, Notes)
        {
            Warnings = WarningList,
            RefusedOnWires = RefusedOnWires,
        };

        public Em3dLayoutSolidsResult Result() => new(SolidList, SheetList, _materials, null, Notes)
        {
            Warnings = WarningList,
            Origins = OriginMap,
            MaterialSources = _materialSource,
            ObjectNets = ObjectNetMap.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            ViaNetConflicts = ViaNetConflictMap,
            ViaNetAdoptions = ViaNetAdoptionMap,
            GroundBandObjects = GroundBandObjectList,
            OutlineMoveM = _robust.MaxMoveM,
            Wires = WireBuild?.Reports ?? [],
            NoAlpha = NoAlphaList,
            UnknownTemperature = _unknownTemperature,
            StackBottomM = Bands.Count > 0 ? Bands.Min(b => b.BottomM) : 0,
            PadRefusal = PadRefusal,
        };

        // ── Materials resolved at T (R-em3d3-4) ──────────────────────────────────────────────────

        private string ConductorMaterial(StackupLayer entry)
        {
            double tempC = options.TempC;
            if (tech.FindMaterial(entry.Material) is { } m)
            {
                double sigma = m.Sigma20 ?? entry.SigmaSm;
                if (m.Sigma20 is { } s20)
                {
                    if (m.Alpha20 is { } a20) sigma = new WireMaterial(m.Name, s20, a20, 0).SigmaAt(tempC);
                    else _noAlpha.Add(m.Name);
                }
                // A material that states no σ₂₀ leaves the entry's own σ in force — a number of
                // unknown temperature, from the stackup entry, and reported as both.
                else if (!_unknownTemperature.Contains(entry.Name)) _unknownTemperature.Add(entry.Name);
                return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, null, m.TanD ?? 0, m.Mur ?? 1, sigma),
                           m.Sigma20 is null ? EntrySource(entry) : TechnologySource(m.Name));
            }
            if (!_unknownTemperature.Contains(entry.Name)) _unknownTemperature.Add(entry.Name);
            return Add(new Em3dMaterial(entry.Name, 1, null, 0, entry.Mur, entry.SigmaSm), EntrySource(entry));
        }

        private string DielectricMaterial(StackupLayer entry)
        {
            // Resolve-on-read (brief 2) already wrote a named material's εr/tanδ/μr into the entry;
            // what only the material carries is the tensor.
            var m = tech.FindMaterial(entry.Material);
            return Add(new Em3dMaterial(m?.Name ?? entry.Name, entry.Epsr,
                                        m?.EpsrTensor is { Length: 3 } t ? [.. t] : null,
                                        entry.TanD, entry.Mur, entry.SigmaSm),
                       m is null ? EntrySource(entry) : TechnologySource(m.Name));
        }

        private string BodyMaterial(TechMaterial m, out Em3dRole role)
        {
            double sigma = 0;
            if (m.Sigma20 is { } s20)
            {
                if (m.Alpha20 is { } a20) sigma = new WireMaterial(m.Name, s20, a20, 0).SigmaAt(options.TempC);
                else { sigma = s20; _noAlpha.Add(m.Name); }
            }
            role = m.Epsr is null && m.EpsrTensor is null && sigma > 0 ? Em3dRole.Conductor : Em3dRole.Dielectric;
            return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, m.EpsrTensor is { Length: 3 } t ? [.. t] : null,
                                        m.TanD ?? 0, m.Mur ?? 1, sigma), TechnologySource(m.Name));
        }

        /// <summary>
        /// EM-TFR — true when a band's analysis sheet sits on its top face. <c>Conductors: Sheets</c> is the planar
        /// model's idealisation, so it applies the planar rule too: <c>SheetAt: Top</c> is for a capacitor's plate gap and
        /// holds only where the film above the band is drawn (PatternedDielectric). Every other mode keeps the band's own
        /// statement, which is today's generator to the bit.
        /// </summary>
        private bool SheetOnTop(PlanarExtractor.StackBand band)
        {
            if (band.Layer.SheetAt != ConductorSheetSurface.Top) return false;
            if (options.Conductors != Em3dConductorMode.Sheets) return true;
            int at = -1;
            for (int i = 0; i < Bands.Count; i++) if (Bands[i].Index == band.Index) at = i;
            if (at < 0 || at + 1 >= Bands.Count || Bands[at + 1].Layer is not { Kind: StackupKind.Dielectric, PresentWithLayer: { Length: > 0 } tie }) return true;
            if (Bands.FirstOrDefault(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.Name == tie) is { } plate)
                return _byLayer.Keys.Any(plate.Layer.DrawingLayers.Contains);
            return tech.Layers.FirstOrDefault(l => l.Name == tie) is { } mask && _byLayer.ContainsKey(mask.Key);
        }

        private double SheetZOf(PlanarExtractor.StackBand band) => SheetOnTop(band) ? band.TopM : band.BottomM;

        /// <summary>TFR-V — where a via ends on a level whose every piece is a sheet is that sheet. Past it, the part of the
        /// sheet over the via lies inside the barrel, a surface of no element, and the solver cannot read the mesh.</summary>
        private double ViaTopZ(PlanarExtractor.StackBand band) => SheetZIfAllSheets(band) ?? band.TopM;

        /// <inheritdoc cref="ViaTopZ"/>
        private double ViaBottomZ(PlanarExtractor.StackBand band) => SheetZIfAllSheets(band) ?? band.BottomM;

        private double? SheetZIfAllSheets(PlanarExtractor.StackBand band)
            => Pieces.TryGetValue(band.Index, out var list) && list.Count > 0 && list.All(p => p.IsSheet) ? list[0].SheetZ : null;

        /// <summary>
        /// EM-TFR — every film piece as a sheet on its level: σ = 1/(R_s·t), for which only the product is physical. The z
        /// is the metal's own sheet where the level is a sheet, and the face <see cref="LayerDef.FilmAt"/> names where the
        /// metal is a solid, so the film joins the clipped metal along the contact edges.
        /// </summary>
        private void AddFilmPieces(IReadOnlyList<PlanarExtractor.StackBand> bands, List<(LayoutShape Shape, int Level)> films)
        {
            if (films.Count == 0) return;
            bool volume = options.Conductors == Em3dConductorMode.Solids;
            var named = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var group in films.GroupBy(f => (f.Level, f.Shape.Layer)))
            {
                var band = bands.First(b => b.Index == group.Key.Level);
                var def = tech.Layers.First(l => l.Key == group.Key.Layer);
                var entry = ResistiveFilms.Of(tech, group.Key.Layer)!;
                double rs = entry.SheetResistanceOhmPerSq;
                // A film level's thickness is the entry's, and a film on one states none of its own (R-tfrv-1).
                long? statedDbu = ResistiveFilms.IsFilmLevel(band.Layer) ? band.Layer.ThicknessDbu : entry.ThicknessDbu;
                bool stated = statedDbu is > 0;
                // t < δ exactly when t < 2·R_s/(ωμ₀): σ = 1/(R_s·t) puts t on both sides of the skin depth.
                double thinLimit = options.FMaxHz is { } f && f > 0 ? 2.0 * rs / (2.0 * Math.PI * f * Mu0) : double.PositiveInfinity;
                double t = stated ? statedDbu!.Value * StackPerDbu : Math.Min(DefaultFilmThicknessM, 0.5 * thinLimit);
                double sigma = 1.0 / (rs * t);
                string material = Add(new Em3dMaterial($"Film {def.Name}", 1, null, 0, 1, sigma), TechnologySource());

                bool solidMetal = volume || (Pieces.TryGetValue(band.Index, out var metal) && metal.Any(p => !p.IsSheet));
                double z = solidMetal
                    ? ResistiveFilms.FaceOf(entry, band.Layer) == ConductorSheetSurface.Top ? band.TopM : band.BottomM
                    : SheetZOf(band);

                Notes.Add($"Resistive film '{def.Name}' on '{band.Layer.Name}' is a sheet at σ = {sigma:G4} S/m × t = {t * 1e9:G4} nm" +
                          (stated ? "" : " (a thickness well under a skin depth, picked because the layer states none)") +
                          $", {rs:G4} Ω/sq: for a sheet only the product is physical." +
                          (solidMetal ? " It stays a sheet beside solid metal, because a film's own thickness is a mesh nobody should be asked to pay for." : ""));

                var made = Pieces.TryGetValue(band.Index, out var list) ? list : Pieces[band.Index] = [];
                foreach (var (shape, _) in group)
                    foreach (var poly in PlanarExtractor.ToPolygons(shape, tech, _perDbu))
                    {
                        int k = named[def.Name] = named.GetValueOrDefault(def.Name) + 1;
                        // ZBottom and ZTop are what the sheet's thickness is read from; SheetZ is where it is.
                        made.Add(new Piece(poly, $"{band.Layer.Name}/{def.Name}/{k}", material, true, z, z + t, z, def.Key,
                                           $"a resistive film of {rs:G4} Ω/sq", null, IsFilm: true));
                    }
            }
        }

        /// <summary>EM-TFR — the thickness a film states none of gets: thin against any skin depth a resistor film has.</summary>
        private const double DefaultFilmThicknessM = 50e-9;

        /// <summary>R-em3d3-5f — a conductor is a sheet when it has no thickness, or is thinner than both
        /// thresholds. With no sweep there is no skin depth, so only a zero thickness makes a sheet.</summary>
        private (bool Sheet, string? Reason) SheetRule(PlanarExtractor.StackBand band, double t, double delta, double width)
        {
            // EM-TFR — a setup may state the answer: every band a sheet, or every band its real thickness.
            if (options.Conductors == Em3dConductorMode.Sheets) return (true, "this setup states Conductors: Sheets");
            if (options.Conductors == Em3dConductorMode.Solids && t > 0) return (false, null);
            bool sheet = t <= 0 ||
                         (delta > 0 && t < Em3dGenerator.SheetMaxSkinDepths * delta && t < Em3dGenerator.SheetMaxFractionOfWidth * width);
            string? reason = !sheet ? null
                : t <= 0 ? $"'{band.Layer.Name}' has zero thickness in the stackup"
                : $"{Fmt(t * 1e6)} µm is under {Em3dGenerator.SheetMaxSkinDepths:G} skin depths " +
                  $"({Fmt(Em3dGenerator.SheetMaxSkinDepths * delta * 1e6)} µm at {Fmt(options.FMaxHz!.Value / 1e9)} GHz) and under " +
                  $"{Em3dGenerator.SheetMaxFractionOfWidth:G} of its width ({Fmt(width * 1e6)} µm)";
            return (sheet, reason);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────────

        public void Net(string obj, string? net)
        {
            if (net is not { Length: > 0 }) return;
            (ObjectNetMap.TryGetValue(obj, out var set) ? set : ObjectNetMap[obj] = new(StringComparer.Ordinal)).Add(net);
        }

        /// <summary>True when every vertex of <paramref name="poly"/> lies within the pad of a via that ends on
        /// <paramref name="plane"/> — a pad, or pads merged, and nothing else. The 2 % allows for a circle's polygon.</summary>
        private bool IsViaPadOn(PlanarPolygon poly, PlanarExtractor.StackBand plane)
        {
            var pads = _viaSpans
                .Where(v => v.Shape is ViaShape && (ReferenceEquals(v.Top, plane) || ReferenceEquals(v.Bottom, plane)))
                .Select(v => (ViaShape)v.Shape)
                .Select(v => (X: v.X * _perDbu, Y: v.Y * _perDbu, R: v.PadSize * _perDbu / 2 * 1.02))
                .ToList();
            return pads.Count > 0 &&
                   poly.Outer.All(pt => pads.Any(p => (pt.X - p.X) * (pt.X - p.X) + (pt.Y - p.Y) * (pt.Y - p.Y) <= p.R * p.R));
        }

        /// <summary>The nets of the pieces a via's footprint centre lands on, at its top and bottom.</summary>
        private List<string> ViaLandingNets(LayoutShape shape, PlanarExtractor.StackBand top, PlanarExtractor.StackBand bottom)
        {
            var fp = ViaFootprint(shape).ToList();
            if (fp.Count == 0) return [];
            var (x0, y0, x1, y1) = fp[0].Bounds();
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            var nets = new List<string>();
            foreach (var band in new[] { top, bottom })
                if (Pieces.TryGetValue(band.Index, out var list))
                    foreach (var p in list)
                        if (p.Net is { } n && p.Poly.Contains(cx, cy) && !nets.Contains(n)) nets.Add(n);
            return nets;
        }

        /// <summary>brief-dfr34-4 — true when a piece of net <paramref name="net"/> holds the via's footprint centre on a
        /// conductor it spans.</summary>
        private bool ViaPassesNet(LayoutShape shape, PlanarExtractor.StackBand top, PlanarExtractor.StackBand bottom, string net)
        {
            var fp = ViaFootprint(shape).ToList();
            if (fp.Count == 0) return false;
            var (x0, y0, x1, y1) = fp[0].Bounds();
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            return Bands.Where(b => b.BottomM >= bottom.BottomM - 1e-15 && b.TopM <= top.TopM + 1e-15)
                        .Any(b => Pieces.TryGetValue(b.Index, out var list) && list.Any(p => p.Net == net && p.Poly.Contains(cx, cy)));
        }

        /// <summary>brief-dfr34-4 R-dfr34-4-4 — the edit that gives a drill shape <paramref name="to"/>: the shape by its layer,
        /// its place among the layout's shapes on that layer, and its centre.</summary>
        private Em3dViaNetFix ViaFix(LayoutShape shape, string from, string to)
        {
            int index = _byLayer.TryGetValue(shape.Layer, out var onLayer) ? onLayer.IndexOf(shape) + 1 : 0;
            var (x, y) = Em3dViaNetFix.Centre(shape);
            return new Em3dViaNetFix(source.AbsolutePath, shape.Layer, index, x, y, from, to);
        }

        /// <summary>brief-dfr28-5 — the nets other than <paramref name="own"/> of the pieces a via's footprint centre lies
        /// in, on every conductor it spans, each with the layers it is on; the centre, metres, on each row.</summary>
        private List<(string Net, string Layers, double X, double Y)> ViaPassedNets(LayoutShape shape, PlanarExtractor.StackBand top,
                                                                                   PlanarExtractor.StackBand bottom, string own)
        {
            var fp = ViaFootprint(shape).ToList();
            if (fp.Count == 0) return [];
            var (x0, y0, x1, y1) = fp[0].Bounds();
            double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            var layers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var band in Bands.Where(b => b.BottomM >= bottom.BottomM - 1e-15 && b.TopM <= top.TopM + 1e-15).OrderByDescending(b => b.TopM))
                if (Pieces.TryGetValue(band.Index, out var list))
                    foreach (var p in list)
                        if (p.Net is { } n && n != own && p.Poly.Contains(cx, cy))
                            (layers.TryGetValue(n, out var l) ? l : layers[n] = []).Add($"'{band.Layer.Name}'");
            return [.. layers.Select(kv => (kv.Key, string.Join(", ", kv.Value.Distinct()), cx, cy))];
        }

        public string Add(Em3dMaterial m, string source)
        {
            if (_materialByName.TryGetValue(m.Name, out var have))
            {
                if (have == m || SameValues(have, m)) return m.Name;
                // An anonymous entry sharing a name with a named material, with different numbers: kept
                // apart rather than silently merged into whichever came first.
                return Add(m with { Name = "stackup/" + m.Name }, source);
            }
            _materials.Add(m);
            _materialByName[m.Name] = m;
            _materialSource[m.Name] = source;
            return m.Name;
        }

        /// <summary>brief-em3d-53 R-em3d53-6 — names the library a material came from, when it came from one.</summary>
        private string TechnologySource(string? material = null)
            => tech.LibrarySourceOf(material) is { } lib
                ? $"technology '{tech.Name}' via library '{MaterialLibraries.Display(lib)}'"
                : $"technology '{tech.Name}' Materials";

        private static string EntrySource(StackupLayer entry) => $"stackup entry '{entry.Name}' (its own numbers)";

        public Em3dSolid Origin(Em3dSolid s, Em3dObjectKind kind, string? entry, LayerKey? layer = null)
        {
            OriginMap[s.Name] = new Em3dObjectOrigin(kind, entry, layer, null);
            return s;
        }

        private static bool SameValues(Em3dMaterial a, Em3dMaterial b) =>
            a.Epsr == b.Epsr && a.TanD == b.TanD && a.Mur == b.Mur && a.SigmaSm == b.SigmaSm &&
            (a.EpsrTensor ?? []).SequenceEqual(b.EpsrTensor ?? []);

        public string AirMaterialName()
        {
            if (tech.FindMaterial(Em3dGenerator.AirMaterial) is { } m)
                return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, null, m.TanD ?? 0, m.Mur ?? 1, 0), TechnologySource(m.Name));
            return Add(new Em3dMaterial(Em3dGenerator.AirMaterial, 1, null, 0, 1, 0), "built in (free space)");
        }

        /// <summary>
        /// A round drill's centre and radius, metres: a via, or a CIRCLE drawn on a via or hole layer — which is what a
        /// Gerber or DXF drill file imports as. Drawn as an extruded polygon, a plated circle was a solid copper rod: no
        /// air core, so nothing carved the pads or substrate it passes through, and its caps lay in the pads' own faces
        /// and z-fought them. Null for any other shape, which keeps its drawn footprint.
        /// </summary>
        private (double X, double Y, double R)? Drill(LayoutShape shape) => shape switch
        {
            ViaShape v    => (v.X * _perDbu, v.Y * _perDbu, (v.DrillSize > 0 ? v.DrillSize : v.PadSize) * _perDbu / 2),
            CircleShape c => (c.Cx * _perDbu, c.Cy * _perDbu, c.R * _perDbu),
            _             => null,
        };

        private IEnumerable<PlanarPolygon> ViaFootprint(LayoutShape shape)
        {
            if (shape is ViaShape v)
            {
                double r = (v.DrillSize > 0 ? v.DrillSize : v.PadSize) * _perDbu / 2;
                double cx = v.X * _perDbu, cy = v.Y * _perDbu;
                return [new PlanarPolygon([new(cx - r, cy - r), new(cx + r, cy - r), new(cx + r, cy + r), new(cx - r, cy + r)])];
            }
            return PlanarExtractor.ToPolygons(shape, tech, _perDbu);
        }

        /// <summary>
        /// The board an outline layer's merged shapes describe. A closed region is the board, as drawn. A LINE is not: a
        /// Gerber outline is strokes, and merged they are a thin ring whose hole is the board — read as a region, a
        /// 50 mm board was a 0.13 mm sliver of substrate around its own edge. A shape is a line when it has a hole and
        /// its width (2·area / perimeter) is under <see cref="OutlineLineFraction"/> of its smaller extent. A line's holes
        /// are the board, even-odd: a line inside another line's hole (a cutout drawn round) cuts that hole out, and the
        /// holes of a line nested once more are islands again. A hole under <see cref="OutlineGapFraction"/> of its
        /// line's largest is a gap between corner marks, not a board.
        /// </summary>
        internal static List<PlanarPolygon> BoardRegions(IReadOnlyList<PlanarPolygon> drawn, out int lines)
        {
            var regions = new List<PlanarPolygon>();
            var ink = new List<PlanarPolygon>();
            foreach (var p in drawn) (IsLine(p) ? ink : regions).Add(p);
            lines = ink.Count;
            if (ink.Count == 0) return regions;

            static bool Encloses(IReadOnlyList<EmPoint> ring, IReadOnlyList<EmPoint> other)
                => new PlanarPolygon(ring).Contains(other[0].X, other[0].Y);

            var boards = new List<(IReadOnlyList<EmPoint> Outer, List<IReadOnlyList<EmPoint>> Cutouts)>();
            var cutouts = new List<IReadOnlyList<EmPoint>>();
            foreach (var p in ink)
            {
                int depth = ink.Count(q => !ReferenceEquals(q, p) && Encloses(q.Outer, p.Outer));
                if (depth % 2 == 1) { cutouts.Add(p.Outer); continue; }
                double largest = p.HoleRings.Max(h => new PlanarPolygon(h).Area());
                foreach (var h in p.HoleRings)
                    if (new PlanarPolygon(h).Area() >= OutlineGapFraction * largest) boards.Add((h, []));
            }
            foreach (var c in cutouts)
                foreach (var b in boards)
                    if (Encloses(b.Outer, c)) { b.Cutouts.Add(c); break; }
            regions.AddRange(boards.Select(b => new PlanarPolygon(b.Outer, b.Cutouts.Count > 0 ? b.Cutouts : null)));
            return regions;

            static bool IsLine(PlanarPolygon p)
            {
                if (p.HoleRings.Count == 0) return false;
                var (x0, y0, x1, y1) = p.Bounds();
                return CharacteristicWidth(p) < OutlineLineFraction * Math.Min(x1 - x0, y1 - y0);
            }
        }

        /// <summary>
        /// Whether a drill of radius <paramref name="r"/> at (<paramref name="x"/>, <paramref name="y"/>), metres, lies wholly
        /// on the board — always, with no outline. A drill that is not is built by <see cref="ClipDisc"/> (R-dfr28-1-1).
        /// </summary>
        private bool OnBoard(double x, double y, double r)
        {
            if (_outline.Count == 0) return true;
            double reach = r * (1 - 1e-9);
            foreach (var region in _outline)
                if (region.Contains(x, y) &&
                    new[] { region.Outer }.Concat(region.HoleRings).All(ring => RingDistance(ring, x, y) >= reach))
                    return true;
            return false;
        }

        /// <summary>
        /// R-dfr28-1-1 — a drill's disc, as a <see cref="DiscSides"/>-gon, intersected with the board: empty when the drill
        /// is wholly off it. Clipped on a picometre grid, so a cut along a straight board edge lands on that edge's own
        /// coordinate and the cut face is the board's side face, not a sliver beside it.
        /// </summary>
        private List<PlanarPolygon> ClipDisc(double x, double y, double r)
        {
            static Point64 P(double px, double py) => new((long)Math.Round(px * ClipScale), (long)Math.Round(py * ClipScale));
            var disc = new Path64(DiscSides);
            for (int k = 0; k < DiscSides; k++)
            {
                double t = 2 * Math.PI * k / DiscSides;
                disc.Add(P(x + r * Math.Cos(t), y + r * Math.Sin(t)));
            }
            var board = new Paths64();
            foreach (var region in _outline)
                foreach (var ring in new[] { region.Outer }.Concat(region.HoleRings))
                    board.Add(new Path64(ring.Select(q => P(q.X, q.Y))));
            var tree = new PolyTree64();
            Clipper.BooleanOp(ClipType.Intersection, [disc], board, tree, FillRule.EvenOdd);

            var drawn = new HashSet<(long, long)>(disc.Concat(board.SelectMany(path => path)).Select(q => (q.X, q.Y)));
            var result = new List<PlanarPolygon>();
            List<EmPoint> RingOf(Path64 path)
            {
                foreach (var q in path)
                    if (!drawn.Contains((q.X, q.Y))) _cutVertices.Add(new EmPoint(q.X / ClipScale, q.Y / ClipScale));
                return [.. path.Select(q => new EmPoint(q.X / ClipScale, q.Y / ClipScale))];
            }
            void Walk(PolyPath64 node)
            {
                for (int i = 0; i < node.Count; i++)
                {
                    var outer = node[i];
                    var holes = new List<IReadOnlyList<EmPoint>>();
                    for (int j = 0; j < outer.Count; j++)
                    {
                        holes.Add(RingOf(outer[j].Polygon!));
                        Walk(outer[j]);
                    }
                    if (outer.Polygon is { Count: >= 3 } ring)
                        result.Add(new PlanarPolygon(RingOf(ring), holes.Count > 0 ? holes : null));
                }
            }
            Walk(tree);
            return result;
        }

        private static double RingDistance(IReadOnlyList<EmPoint> ring, double x, double y)
        {
            double best = double.PositiveInfinity;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                double ax = ring[j].X, ay = ring[j].Y, dx = ring[i].X - ax, dy = ring[i].Y - ay;
                double len2 = dx * dx + dy * dy;
                double t = len2 > 0 ? Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1) : 0;
                double ex = ax + t * dx - x, ey = ay + t * dy - y;
                best = Math.Min(best, ex * ex + ey * ey);
            }
            return Math.Sqrt(best);
        }

        /// <summary>The sides of an edge-cut drill's disc (<see cref="ClipDisc"/>).</summary>
        internal const int DiscSides = 64;

        /// <summary>Clipper grid steps per metre for <see cref="ClipDisc"/>: a picometre.</summary>
        private const double ClipScale = 1e12;

        /// <summary>
        /// Whether <paramref name="region"/> lies within drill <paramref name="d"/> (metres), to a thousandth of its radius: an outline
        /// disc the size of its own hole is drilled away. Drawn, it was a full disc of substrate over the barrel, and its faceted wall
        /// and the barrel's interleaved, so the substrate showed through the copper in stripes at some zooms.
        /// </summary>
        internal static bool WithinDrill(PlanarPolygon region, (double X, double Y, double R) d)
        {
            double reach = d.R * 1.001;
            return region.Outer.All(p => (p.X - d.X) * (p.X - d.X) + (p.Y - d.Y) * (p.Y - d.Y) <= reach * reach);
        }

        /// <summary>A holed outline shape narrower than this fraction of its smaller extent is a line (<see cref="BoardRegions"/>).</summary>
        internal const double OutlineLineFraction = 0.05;

        /// <summary>A line's hole smaller than this fraction of its largest hole is not a board (<see cref="BoardRegions"/>).</summary>
        internal const double OutlineGapFraction = 0.01;

        private readonly Em3dOutlineCleanup.Stats _robust = new();

        /// <summary>brief-dfr31-6 — the dielectric solids' lateral outlines (the board outline, a film's), which Robust meshing
        /// keeps a conductor's outline on.</summary>
        private readonly List<PlanarPolygon> _dielectricOutlines = [];
        private Em3dOutlineCleanup.Edges? _dielectricEdges;

        /// <summary>brief-dfr28-3 — an artwork conductor's polygon as Robust meshing leaves it (itself when it is off).</summary>
        private PlanarPolygon Robust(PlanarPolygon poly, double thicknessM)
        {
            if (!options.RobustMeshing) return poly;
            double tol = Em3dOutlineCleanup.Tolerance(thicknessM);
            var edges = _dielectricEdges ??= new Em3dOutlineCleanup.Edges(
                _dielectricOutlines.SelectMany(d => new[] { d.Outer }.Concat(d.HoleRings))
                                   .Select(r => (IReadOnlyList<(double X, double Y)>)[.. r.Select(q => (q.X, q.Y))]));
            IReadOnlyList<EmPoint> Clean(IReadOnlyList<EmPoint> ring) => Em3dOutlineCleanup.Clean(
                Em3dOutlineCleanup.Snap(ring, q => (q.X, q.Y), (x, y) => new EmPoint(x, y), tol, edges, _robust),
                q => (q.X, q.Y), tol, _robust, edges);
            IReadOnlyList<IReadOnlyList<EmPoint>> rings = [Clean(poly.Outer), .. poly.HoleRings.Select(Clean)];
            rings = Em3dOutlineCleanup.OpenTouches(rings, q => (q.X, q.Y), (x, y) => new EmPoint(x, y), tol, _robust);
            var outer = rings[0];
            var holes = rings.Skip(1).ToList();
            return ReferenceEquals(outer, poly.Outer) && holes.Zip(poly.HoleRings).All(h => ReferenceEquals(h.First, h.Second))
                ? poly : new PlanarPolygon(outer, poly.Holes is null ? null : holes);
        }

        /// <summary>Designer round 39 — <paramref name="poly"/> with its slots and holes narrower than twice
        /// <see cref="Em3dOutlineCleanup.SlotHalfWidth"/> filled, when Robust meshing is on (itself otherwise, and when the filled
        /// conductor would reach another piece of its band: then the slot is a gap between two conductors, not one's own).</summary>
        private PlanarPolygon CloseSlots(PlanarPolygon poly, double thicknessM, List<Piece> band)
        {
            if (!options.RobustMeshing) return poly;
            var probe = new Em3dOutlineCleanup.Stats();
            IReadOnlyList<IReadOnlyList<(double X, double Y)>> rings =
                [[.. poly.Outer.Select(q => (q.X, q.Y))], .. poly.HoleRings.Select(h => (IReadOnlyList<(double X, double Y)>)[.. h.Select(q => (q.X, q.Y))])];
            if (Em3dOutlineCleanup.CloseSlots(rings, Em3dOutlineCleanup.SlotHalfWidth(thicknessM), probe) is not { } closed) return poly;
            var filled = new PlanarPolygon([.. closed[0].Select(q => new EmPoint(q.X, q.Y))],
                                           closed.Count > 1 ? [.. closed.Skip(1).Select(h => (IReadOnlyList<EmPoint>)[.. h.Select(q => new EmPoint(q.X, q.Y))])] : null);
            var (fx0, fy0, fx1, fy1) = filled.Bounds();
            foreach (var other in band)
            {
                if (other.IsFilm || ReferenceEquals(other.Poly, poly)) continue;
                var o = other.Poly;
                var (ox0, oy0, ox1, oy1) = o.Bounds();
                if (ox1 < fx0 || ox0 > fx1 || oy1 < fy0 || oy0 > fy1) continue;
                if (o.Outer.Any(q => filled.Contains(q.X, q.Y)) || filled.Outer.Any(q => o.Contains(q.X, q.Y))) return poly;
            }
            _robust.Slotted += probe.Slotted;
            _robust.SlotAreaM2 += probe.SlotAreaM2;
            _robust.SlotWidthM = Math.Max(_robust.SlotWidthM, probe.SlotWidthM);
            return filled;
        }

        /// <summary>Designer feedback round 38 — the board's own outline as Robust meshing leaves it: a rounded corner read from a
        /// Gerber outline holds points under a micrometre apart, and four of them on the board's side make a flat element. Done
        /// here, before anything reads the outline, so a conductor, a via's clip and the laminate all meet the same edge.</summary>
        private PlanarPolygon RobustBoard(PlanarPolygon region)
        {
            if (!options.RobustMeshing) return region;
            IReadOnlyList<EmPoint> Clean(IReadOnlyList<EmPoint> ring)
                => Em3dOutlineCleanup.Clean(ring, q => (q.X, q.Y), Em3dOutlineCleanup.MaxToleranceM, _robust);
            var outer = Clean(region.Outer);
            var holes = region.HoleRings.Select(Clean).ToList();
            return ReferenceEquals(outer, region.Outer) && holes.Zip(region.HoleRings).All(h => ReferenceEquals(h.First, h.Second))
                ? region : new PlanarPolygon(outer, region.Holes is null ? null : holes);
        }

        /// <summary>brief-emb-2 R-emb2-2 — whether a via of <paramref name="entry"/> is built as a tube with an air core: a plated
        /// entry with a wall, unless the problem is an EM one (<see cref="Em3dLayoutSolidsOptions.SolidVias"/>).</summary>
        private bool Hollow(StackupLayer entry)
            => entry.Fill == ViaFillKind.Plated && (entry.WallThicknessDbu ?? 0) > 0 && !options.SolidVias;

        /// <summary>brief-emb-2 R-emb2-2 — for an EM problem, what each via entry's barrels were built as, so <c>explain</c> says
        /// it per entry; null when there is no via or the problem keeps the walls.</summary>
        private string? ViaFillNote()
        {
            if (!options.SolidVias || _viaSpans.Count == 0) return null;
            var entries = _viaSpans.Select(v => v.Entry).Distinct().ToList();
            // Said only where it changes something: an entry whose Fill is Solid is built as it is drawn anyway.
            if (!entries.Any(e => e.Fill == ViaFillKind.Plated && (e.WallThicknessDbu ?? 0) > 0)) return null;
            string Says(StackupLayer e) => e.Fill == ViaFillKind.Plated && (e.WallThicknessDbu ?? 0) > 0
                ? $"'{e.Name}' (Fill Plated, {Fmt((e.WallThicknessDbu ?? 0) * StackPerDbu * 1e6)} µm wall) is built solid"
                : $"'{e.Name}' (Fill {e.Fill}) is solid";
            return $"Via barrels in this EM problem are solid: {string.Join("; ", entries.Select(Says))}. A hollow barrel's air " +
                   "core changes nothing an RF answer can see and meshes into inverted elements; a thermal problem keeps the wall.";
        }

        /// <summary>The note a run gets when Robust meshing removed anything.</summary>
        /// <summary>The layer of a closed zero-width path whose box holds the drawn geometry (within 1 %), or null.</summary>
        private LayerKey? BoardEdgeCentreline()
        {
            double tolX = 0.01 * (Cx1 - Cx0), tolY = 0.01 * (Cy1 - Cy0);
            foreach (var (path, layer) in _closedCentrelines)
            {
                var xy = path.Xy;
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                for (int i = 0; i + 1 < xy.Length; i += 2)
                {
                    double x = xy[i] * _perDbu, y = xy[i + 1] * _perDbu;
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }
                if (x0 <= Cx0 + tolX && y0 <= Cy0 + tolY && x1 >= Cx1 - tolX && y1 >= Cy1 - tolY) return layer;
            }
            return null;
        }

        internal static string RobustNote(Em3dOutlineCleanup.Stats s) =>
            $"Robust meshing removed {s.Removed:N0} of {s.Points:N0} outline points that lay within a fraction of a micrometre " +
            "of the line through their neighbours" +
            (s.Snapped > 0 ? $", and moved {s.Snapped:N0} that lay as close to the board's edge (or a dielectric film's) onto it" : "") +
            $"; no outline moved by more than {Fmt(s.MaxMoveM * 1e6)} µm." +
            (s.Spikes > 0 ? $" It also cut off {s.Spikes:N0} zero-width spike{(s.Spikes == 1 ? "" : "s")} where an outline ran out and straight back, " +
                            $"holding no copper; the longest reached {Fmt(s.LongestSpikeM * 1e6)} µm." : "") +
            (s.Touches > 0 ? $" It also moved {s.Touches:N0} corner{(s.Touches == 1 ? "" : "s")} off {(s.Touches == 1 ? "a stretch" : "stretches")} of the same object's " +
                             "outline, such as a hole touching its pour's edge at one point, which the mesher cannot cross." : "") +
            (s.Slotted > 0 ? $" It filled the slots and holes narrower than {Fmt(s.SlotWidthM * 1e6)} µm inside {s.Slotted:N0} " +
                             $"conductor{(s.Slotted == 1 ? "" : "s")}, {Fmt(s.SlotAreaM2 * 1e12)} µm² of copper in all: the mesher " +
                             "cannot fit elements into them, and each joins copper of one conductor." : "");

        private static Em3dExtrudedPolygon Extrude(PlanarPolygon p, double z0, double z1)
            => new(Ring(p.Outer), [.. p.HoleRings.Select(Ring)], z0, z1);

        private static IReadOnlyList<Point2> Ring(IReadOnlyList<EmPoint> ring)
            => [.. ring.Select(q => new Point2(q.X, q.Y))];

        /// <summary>2·Area / Perimeter: a strip's width, however long it is drawn.</summary>
        internal static double CharacteristicWidth(PlanarPolygon poly)
        {
            double perimeter = RingLength(poly.Outer);
            foreach (var h in poly.HoleRings) perimeter += RingLength(h);
            return perimeter > 0 ? 2 * poly.Area() / perimeter : 0;
        }

        private static double RingLength(IReadOnlyList<EmPoint> ring)
        {
            double s = 0;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                s += Math.Sqrt((ring[i].X - ring[j].X) * (ring[i].X - ring[j].X) +
                               (ring[i].Y - ring[j].Y) * (ring[i].Y - ring[j].Y));
            return s;
        }

        private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// brief-dfr34-4 R-dfr34-4-4 — giving one drill shape the net of its copper: the layout it is in, the shape by its layer, its
/// 1-based place among that layer's shapes (the flattened layout's, which is the document's own for artwork drawn in it) and
/// its centre in DBU, the net it states and the net it is given. <see cref="Find"/> finds it again in the document.
/// </summary>
public sealed record Em3dViaNetFix(string LayoutPath, LayerKey Layer, int Index, long X, long Y, string FromNet, string ToNet)
{
    /// <summary>"drill shape 12 on layer 'Drill' (20/0)".</summary>
    public string ShapeText(Technology? tech)
        => $"drill shape {Index} on layer " +
           (tech?.Layers.FirstOrDefault(l => l.Key == Layer)?.Name is { } n ? $"'{n}' ({Layer.Layer}/{Layer.Datatype})" : $"{Layer.Layer}/{Layer.Datatype}");

    /// <summary>A drill shape's centre, DBU: a via's point, or its outline's bounds' centre.</summary>
    public static (long X, long Y) Centre(LayoutShape shape)
    {
        if (shape is ViaShape v) return (v.X, v.Y);
        var b = LayoutGeometry.BboxOf(shape);
        return ((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);
    }

    /// <summary>The shape this edit is for among <paramref name="shapes"/> (a layout document's own): on the layer, on the
    /// stated net, at the centre — or null when the layout no longer holds it.</summary>
    public LayoutShape? Find(IEnumerable<LayoutShape> shapes)
    {
        var onLayer = shapes.Where(s => s.Layer == Layer && s is not LabelShape and not BitmapShape).ToList();
        bool Is(LayoutShape s) => s.Net == FromNet && Centre(s) == (X, Y);
        if (Index >= 1 && Index <= onLayer.Count && Is(onLayer[Index - 1])) return onLayer[Index - 1];
        return onLayer.FirstOrDefault(Is);
    }
}

/// <summary>brief-dfr34-4 D1 — a via given its copper's net: the net, the sentence a static run says, and the edit that makes
/// the layout say so too.</summary>
public sealed record Em3dViaNetAdoption(string Net, string Sentence, Em3dViaNetFix Fix);
