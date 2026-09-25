// brief-em3d-3 R-em3d3-5 — Tier A: a layout and its technology, as the solver-neutral 3D problem
// (docs/design/em-3d.md §4.1, §4.1a, §6.3 Tier A, §6.4).
//
// Nothing here solves, writes or starts a process. Brief 5 makes the result visible; briefs 7 and 9
// lower it. Framework-free, like everything in src/Design.
//
// ── REUSE, NOT RESTATEMENT ────────────────────────────────────────────────────────────────────
//
// The 3D model and the planar one are cross-checked against each other (brief 10), so wherever the
// two need the same answer this file asks the planar code for it rather than re-deriving it:
//
//   * the geometry        EmGeometry.ForSetup — flatten, then the .cem's solve region;
//   * connectivity        PlanarExtractor.MergeOverlapping — the planar extractor's own union;
//   * shape -> polygon    PlanarExtractor.ToPolygons — its outline/flatten/degenerate-ring chain;
//   * z                   PlanarExtractor.StackBands — the two-DBU-scales rule;
//   * the return plane    PlanarExtractor.InferredReturnPlane — R-em-4, and RP-3's flip;
//   * port numbering,     EmPortExtraction.Extract — the planar ports, their numbers and their
//     sides, refusals       refusals verbatim, run over this file's own conductor polygons;
//   * via spans           ViaSpanResolver, and the planar via binding (plated entries only);
//   * σ(T)                WireMaterial.SigmaAt.
//
// What is genuinely 3D — dielectric lateral extent, the air box, sheets vs solids, construction
// order, the port sheet's rectangle — is decided here, ONCE, so both backends inherit one answer.

using System.Globalization;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Engine.Em3d;
using CircuitRF.Engine.Mom;
using WireMaterial = CircuitRF.WBond.WireMaterial;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>The generator's answer: a problem, or a refusal naming what is missing, plus every
/// note it made on the way. A problem is returned unvalidated — <see cref="Em3dProblem.Validate"/>
/// is the backend's first call, and <c>check</c>'s.</summary>
public sealed record Em3dGenerationResult(Em3dProblem? Problem, string? Refusal, IReadOnlyList<string> Notes)
{
    public bool Ok => Problem is not null && Refusal is null;

    /// <summary>Findings a user should act on that do not stop the problem being built — a foot that
    /// overhangs its pad, a metal the technology and the <c>.wBond</c> define differently.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>What the model made of each bond wire (brief-em3d-4), in array then member order.</summary>
    public IReadOnlyList<Em3dWireReport> Wires { get; init; } = [];

    /// <summary>
    /// brief-em3d-5 — where each solid and sheet came from, by name: the stackup entry, the drawing
    /// layer whose colour a picture paints it in, and why a conductor became a sheet. Kept BESIDE the
    /// problem rather than in it: the problem is what a backend reads, and none of this is physics.
    /// </summary>
    public IReadOnlyDictionary<string, Em3dObjectOrigin> Origins { get; init; } =
        new Dictionary<string, Em3dObjectOrigin>();

    /// <summary>brief-em3d-5 R-em3d5-3a — where each material's values resolved from, by material
    /// name: the technology's Materials, the <c>.wBond</c>'s own list, a stackup entry's own numbers,
    /// or free space.</summary>
    public IReadOnlyDictionary<string, string> MaterialSources { get; init; } = new Dictionary<string, string>();

    /// <summary>Materials that state σ₂₀ but no α₂₀, so σ₂₀ is used at every temperature.</summary>
    public IReadOnlyList<string> NoAlpha { get; init; } = [];

    /// <summary>Conductor stackup entries that name no material, so their σ is a number of unknown
    /// temperature, used as given.</summary>
    public IReadOnlyList<string> UnknownTemperature { get; init; } = [];
}

/// <summary>What kind of thing in the design a solid or sheet of the 3D problem is.</summary>
public enum Em3dObjectKind { Dielectric, Air, Body, Conductor, Via, Wire }

/// <summary>
/// Where one named object of the 3D problem came from (brief-em3d-5).
/// </summary>
/// <param name="StackupEntry">The stackup entry it was built from; null for a body, a wire, or the
/// air above the stack.</param>
/// <param name="DrawingLayer">The drawing layer whose shapes it was built from — a conductor's or a
/// via's. What a picture paints it with, through the technology's own layer palette.</param>
/// <param name="SheetReason">Why the generator made it a sheet rather than a volume; null for a
/// solid.</param>
public sealed record Em3dObjectOrigin(Em3dObjectKind Kind, string? StackupEntry, LayerKey? DrawingLayer,
                                      string? SheetReason);

public static class Em3dGenerator
{
    /// <summary>
    /// <b>R-em3d3-6a — the default air-box padding, as a fraction of the LONGEST free-space
    /// wavelength in the band</b> (c / the lowest non-zero frequency), on the four lateral faces and
    /// the top. An eighth: far enough that a first-order absorbing face sees a field that has mostly
    /// decayed, near enough that the air is not most of the mesh. A backend may ENLARGE the box
    /// (Palace's absorbing face wants more room than openEMS's PML) and says so when it does; it
    /// never shrinks it. A <c>.cem</c> overrides any face with <c>AirBox</c>.
    /// </summary>
    public const double DefaultPaddingFractionOfLongestWavelength = 1.0 / 8.0;

    /// <summary>
    /// <b>R-em3d3-5f — a conductor is a sheet when its thickness is below this many skin depths at
    /// the top frequency…</b> Three skin depths carries ~95 % of the current, so metal thinner than
    /// that is not "thick" in the sense a volume mesh resolves. <b>Provisional</b>: F0's Q6
    /// (docs/design/em-3d-f0-findings.md) measured that a sheet matches loss but not phase on a
    /// 35 µm line, and said the threshold needs a number well below t/h = 0.17; these two constants
    /// are the first number, not the measured one.
    /// </summary>
    public const double SheetMaxSkinDepths = 3.0;

    /// <summary>…<b>and</b> below this fraction of its own smallest lateral dimension (2·Area /
    /// Perimeter, which is a strip's width). Provisional, as <see cref="SheetMaxSkinDepths"/>.</summary>
    public const double SheetMaxFractionOfWidth = 0.1;

    /// <summary>The material that fills the air box, and a hollow via's barrel.</summary>
    public const string AirMaterial = "Air";

    /// <summary>The name of the air solid above the stack.</summary>
    public const string AirSolidName = "air";

    private const double C0  = 299_792_458.0;
    private const double Mu0 = 4e-7 * Math.PI;

    /// <summary>
    /// The 3D problem <paramref name="setup"/> describes. <paramref name="source"/> is what
    /// <see cref="EmSetupResolver"/> resolved — the SAME walk-ups the planar run takes (the layout
    /// relative to the <c>.cem</c>'s workspace, the technology relative to the layout's), which this
    /// method does not repeat.
    /// </summary>
    /// <param name="wires">The bond wires to include. Null — the ordinary case — takes the
    /// <c>.wBond</c> stem-paired with the layout (WB40), if it has one.</param>
    public static Em3dGenerationResult Generate(EmSetup setup, EmLayoutSource source, Technology tech,
                                                Em3dWireSource? wires = null)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tech);
        return new Run(setup, source, tech, wires).Go();
    }

    // ── One generation ─────────────────────────────────────────────────────────────────────────

    private sealed class Run(EmSetup setup, EmLayoutSource source, Technology tech, Em3dWireSource? wires)
    {
        private readonly List<string> _notes = [];
        private readonly List<string> _warnings = [];
        private readonly List<Em3dMaterial> _materials = [];
        private readonly Dictionary<string, Em3dMaterial> _materialByName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _materialSource = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Em3dObjectOrigin> _origins = new(StringComparer.Ordinal);
        private double _tempC;
        private double _perDbu;

        /// <summary>One merged conductor piece: its polygon, its name and what it became.</summary>
        private sealed record Piece(PlanarPolygon Poly, string Name, string Material, bool IsSheet,
                                    double ZBottom, double ZTop, double SheetZ, LayerKey Layer,
                                    string? SheetReason);

        public Em3dGenerationResult Go()
        {
            // ── The sweep ─────────────────────────────────────────────────────────────────────
            double[] freqs;
            try { freqs = setup.Frequency.Expand(); }
            catch (Exception ex) { return No(EmDiagnostics.FrequencySweepUnresolvable(ex.Message).Render()); }
            var positive = freqs.Where(f => f > 0).ToArray();
            if (positive.Length == 0) return No(EmDiagnostics.FrequencySweepEmpty().Render());
            double fMin = positive.Min(), fMax = positive.Max();
            if (positive.Length < freqs.Length)
                _notes.Add("The sweep's 0 Hz point is not in the 3D problem: a full-wave solver has no DC " +
                           "solution to give it.");
            var frequency = new Em3dFrequency(fMin, fMax, positive.Length,
                setup.Frequency.Kind == CircuitRF.Core.Design.SweepKind.Log ? Em3dSweepKind.Log : Em3dSweepKind.Linear);

            // ── Temperature (R-em3d3-4) ───────────────────────────────────────────────────────
            _tempC = setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC;

            if (source.DbuPerMicron <= 0)
                return No($"The layout's resolution is {source.DbuPerMicron} DBU per micron, which is not a " +
                          "usable scale. Set a positive DbuPerMicron on the layout.");
            _perDbu = 1.0 / (source.DbuPerMicron * 1e6);
            const double stackPerDbu = 1.0 / (LayoutUnits.DefaultDbuPerMicron * 1e6);

            // ── The geometry: flattened, then the solve region (R-em3d3-5h) ─────────────────
            var geometry = EmGeometry.ForSetup(setup, source);
            _notes.AddRange(geometry.Notes);
            if (geometry.RegionRefusal is { } regionRefusal) return No(regionRefusal);

            var bands = PlanarExtractor.StackBands(tech.Stackup);
            if (bands.Count == 0)
                return No($"Technology '{tech.Name}' has no stackup layers, so nothing says how thick the " +
                          "metal is, what is under it, or where the ground plane sits. Add a stackup in " +
                          "the technology editor.");
            var bandByIndex = bands.ToDictionary(b => b.Index);

            // ── Classify: the planar extractor's bindings, read the planar extractor's way ───
            var conductorBinding = new Dictionary<LayerKey, PlanarExtractor.StackBand>();
            foreach (var b in bands.Where(b => b.Layer.Kind == StackupKind.Conductor))
                foreach (var key in b.Layer.DrawingLayers)
                    if (!conductorBinding.TryGetValue(key, out var have) ||
                        (have.Layer.IsGroundReference && !b.Layer.IsGroundReference))
                        conductorBinding[key] = b;   // a signal binding wins, as PlanarExtractor's does

            var viaBinding    = PlanarExtractor.ViaBinding(tech.Stackup, out int nonPlated);
            var outlineLayers = BoardOutlineLayers(tech);
            var byLayer       = new Dictionary<LayerKey, List<LayoutShape>>();

            var conductorShapes = new List<(LayoutShape Shape, int Level)>();
            var viaShapes       = new List<(LayoutShape Shape, StackupLayer Entry)>();
            var outlineShapes   = new List<LayoutShape>();
            int outlineStrokes  = 0, zeroWidthPaths = 0;

            foreach (var s in geometry.Shapes)
            {
                if (s is LabelShape or BitmapShape) continue;
                (byLayer.TryGetValue(s.Layer, out var list) ? list : byLayer[s.Layer] = []).Add(s);

                if (outlineLayers.Contains(s.Layer))
                {
                    if (s is PathShape) outlineStrokes++;
                    else outlineShapes.Add(s);
                    continue;
                }
                if (s is ViaShape vs)
                {
                    if (viaBinding.TryGetValue(vs.Layer, out var entry)) viaShapes.Add((vs, entry));
                    continue;
                }
                if (s is PathShape { Width: <= 0 }) { zeroWidthPaths++; continue; }
                if (conductorBinding.TryGetValue(s.Layer, out var band)) { conductorShapes.Add((s, band.Index)); continue; }
                if (viaBinding.TryGetValue(s.Layer, out var regionEntry)) viaShapes.Add((s, regionEntry));
            }

            if (nonPlated > 0)
                _notes.Add($"{nonPlated} via stackup entr(y/ies) are marked NON-PLATED and are not in the 3D " +
                           "problem as metal — a non-plated hole is a hole. Their artwork is unchanged.");
            if (zeroWidthPaths > 0)
                _notes.Add($"{zeroWidthPaths} zero-width path(s) are centrelines, not artwork, and are not " +
                           "in the 3D problem.");
            if (outlineStrokes > 0)
                _notes.Add($"{outlineStrokes} shape(s) on the board-outline layer are strokes, which bound " +
                           "no region; only closed shapes there give the board its outline.");

            // ── Merge (R-em3d3-5b): the planar extractor's union, per stackup entry ───────────
            var merged = PlanarExtractor.MergeOverlapping(conductorShapes, tech, out int mergedShapes, out int mergedInto);
            if (mergedShapes > 0)
                _notes.Add($"{mergedShapes} overlapping conductor shape(s) were merged into {mergedInto} — " +
                           "copper that overlaps on one level is one conductor, in 3D as in the planar model.");

            var polysByBand = new Dictionary<int, List<(PlanarPolygon Poly, string? Net, LayerKey Layer)>>();
            foreach (var (shape, level) in merged)
                foreach (var poly in PlanarExtractor.ToPolygons(shape, tech, _perDbu))
                    (polysByBand.TryGetValue(level, out var l) ? l : polysByBand[level] = [])
                        .Add((poly, shape.Net is { Length: > 0 } n ? n : null, shape.Layer));

            if (polysByBand.Count == 0)
                return No($"This EM setup is pointed at geometry with nothing on a layer bound to a " +
                          $"conductor entry in technology '{tech.Name}', so there is no metal to put in a 3D " +
                          "problem. Draw the artwork on a conductor layer, or bind the layer it is on to a " +
                          "conductor entry in the technology editor's Stackup tab.");

            // ── Vias: spans resolved once, geometry after the floor is known ─────────────────
            var viaSpans = new List<(LayoutShape Shape, StackupLayer Entry, PlanarExtractor.StackBand Top,
                                     PlanarExtractor.StackBand Bottom)>();
            int unspanned = 0;
            foreach (var (shape, entry) in viaShapes)
            {
                if (ViaSpanResolver.Resolve(entry, tech) is not { } span ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Top)) is not { } top ||
                    bands.FirstOrDefault(b => ReferenceEquals(b.Layer, span.Bottom)) is not { } bottom)
                { unspanned++; continue; }
                viaSpans.Add((shape, entry, top, bottom));
            }
            if (unspanned > 0)
                _notes.Add($"{unspanned} via(s) are on an entry whose span does not resolve to two conductors " +
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
            bool pecFloor = ground is not null
                && !polysByBand.ContainsKey(ground.Index)
                && polysByBand.Keys.All(i => bandByIndex[i].BottomM >= ground.TopM - 1e-15)
                && viaSpans.All(v => v.Bottom.BottomM >= ground.BottomM - 1e-15)
                && tech.Bodies.All(body => bands.FirstOrDefault(b => b.Layer.Name == body.SitsOn) is not { } on
                                           || on.TopM >= ground.TopM - 1e-15);
            // The .cem's floor steps §6's rule aside only when it states a DIFFERENT boundary. A ZMin
            // that says Pec, or states only a padding, is the default floor restated: turning the floor
            // off for it left an undrawn ground with no representation and refused every port on it.
            var zMinFace = setup.AirBox?.ZMin;
            bool floorStatedAway = zMinFace?.Boundary is { } zMinKind && zMinKind != Em3dBoundaryKind.Pec;
            if (floorStatedAway) pecFloor = false;
            double floorZ = pecFloor ? ground!.TopM : double.NaN;

            // ── Materials resolved at T (R-em3d3-4) ──────────────────────────────────────────
            var unknownTemperature = new List<string>();
            var noAlpha            = new SortedSet<string>(StringComparer.Ordinal);

            string ConductorMaterial(StackupLayer entry)
            {
                if (tech.FindMaterial(entry.Material) is { } m)
                {
                    double sigma = m.Sigma20 ?? entry.SigmaSm;
                    if (m.Sigma20 is { } s20)
                    {
                        if (m.Alpha20 is { } a20) sigma = new WireMaterial(m.Name, s20, a20, 0).SigmaAt(_tempC);
                        else noAlpha.Add(m.Name);
                    }
                    // A material that states no σ₂₀ leaves the entry's own σ in force — a number of
                    // unknown temperature, from the stackup entry, and reported as both.
                    else if (!unknownTemperature.Contains(entry.Name)) unknownTemperature.Add(entry.Name);
                    return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, null, m.TanD ?? 0, m.Mur ?? 1, sigma),
                               m.Sigma20 is null ? EntrySource(entry) : TechnologySource());
                }
                if (!unknownTemperature.Contains(entry.Name)) unknownTemperature.Add(entry.Name);
                return Add(new Em3dMaterial(entry.Name, 1, null, 0, entry.Mur, entry.SigmaSm), EntrySource(entry));
            }

            string DielectricMaterial(StackupLayer entry)
            {
                // Resolve-on-read (brief 2) already wrote a named material's εr/tanδ/μr into the entry;
                // what only the material carries is the tensor.
                var m = tech.FindMaterial(entry.Material);
                return Add(new Em3dMaterial(m?.Name ?? entry.Name, entry.Epsr,
                                            m?.EpsrTensor is { Length: 3 } t ? [.. t] : null,
                                            entry.TanD, entry.Mur, 0),
                           m is null ? EntrySource(entry) : TechnologySource());
            }

            string BodyMaterial(TechMaterial m, out Em3dRole role)
            {
                double sigma = 0;
                if (m.Sigma20 is { } s20)
                {
                    if (m.Alpha20 is { } a20) sigma = new WireMaterial(m.Name, s20, a20, 0).SigmaAt(_tempC);
                    else { sigma = s20; noAlpha.Add(m.Name); }
                }
                role = m.Epsr is null && m.EpsrTensor is null && sigma > 0 ? Em3dRole.Conductor : Em3dRole.Dielectric;
                return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, m.EpsrTensor is { Length: 3 } t ? [.. t] : null,
                                            m.TanD ?? 0, m.Mur ?? 1, sigma), TechnologySource());
            }

            // ── Conductor pieces: named, sheet or solid ──────────────────────────────────────
            var pieces = new Dictionary<int, List<Piece>>();
            foreach (var band in bands.Where(b => polysByBand.ContainsKey(b.Index)))
            {
                var list = polysByBand[band.Index];
                string material = ConductorMaterial(band.Layer);
                var mat = _materialByName[material];
                double t = band.TopM - band.BottomM;
                double delta = mat.SigmaSm > 0 ? 1.0 / Math.Sqrt(Math.PI * fMax * Mu0 * mat.Mur * mat.SigmaSm) : 0;
                double sheetZ = band.Layer.SheetAt == ConductorSheetSurface.Top ? band.TopM : band.BottomM;

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
                    bool sheet = t <= 0 ||
                                 (delta > 0 && t < SheetMaxSkinDepths * delta && t < SheetMaxFractionOfWidth * width);
                    string? reason = !sheet ? null
                        : t <= 0 ? $"'{band.Layer.Name}' has zero thickness in the stackup"
                        : $"{Fmt(t * 1e6)} µm is under {SheetMaxSkinDepths:G} skin depths " +
                          $"({Fmt(SheetMaxSkinDepths * delta * 1e6)} µm at {Fmt(fMax / 1e9)} GHz) and under " +
                          $"{SheetMaxFractionOfWidth:G} of its width ({Fmt(width * 1e6)} µm)";
                    made.Add(new Piece(poly, name, material, sheet, band.BottomM, band.TopM, sheetZ, layer, reason));
                }
                pieces[band.Index] = made;
            }

            // ── Board outline (R-em3d3-5c) ────────────────────────────────────────────────────
            var outline = new List<PlanarPolygon>();
            if (outlineShapes.Count > 0)
                foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                             [.. outlineShapes.Select(s => (s, 0))], tech, out _, out _))
                    outline.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));

            // ── Which bands are in the problem, and each dielectric's z after absorption ──────
            var inProblem = bands.Where(b => !pecFloor || b.BottomM >= floorZ - 1e-15).ToList();
            var dielZ = new Dictionary<int, (double Bottom, double Top)>();
            foreach (var b in inProblem.Where(b => b.Layer.Kind == StackupKind.Dielectric))
                dielZ[b.Index] = (b.BottomM, b.TopM);
            // An INNER conductor band — dielectric directly below AND above — is filled, where no metal
            // is drawn, by the dielectric its sheet faces away from: the one above for a sheet at the
            // band's bottom (the planar rule that makes a microstrip's height the substrate), the one
            // below for SheetAt = Top. An OUTER band is air: the metal sits on the board.
            for (int i = 1; i + 1 < inProblem.Count; i++)
            {
                var c = inProblem[i];
                if (c.Layer.Kind != StackupKind.Conductor) continue;
                var below = inProblem[i - 1];
                var above = inProblem[i + 1];
                if (below.Layer.Kind != StackupKind.Dielectric || above.Layer.Kind != StackupKind.Dielectric) continue;
                if (c.Layer.SheetAt == ConductorSheetSurface.Top)
                    dielZ[below.Index] = (dielZ[below.Index].Bottom, c.TopM);
                else
                    dielZ[above.Index] = (c.BottomM, dielZ[above.Index].Top);
            }

            // ── Patterned films (PresentWithLayer is simply true in 3D, §4.1a) ─────────────────
            var filmPolys = new Dictionary<int, List<PlanarPolygon>>();
            foreach (var b in inProblem.Where(b => b.Layer.Kind == StackupKind.Dielectric &&
                                                   b.Layer.PresentWithLayer is { Length: > 0 }))
            {
                string tie = b.Layer.PresentWithLayer!;
                // A name in NEITHER namespace leaves the film active, as the planar extractor leaves
                // it (StackupLayer.PresentWithLayer): dropping it on a typo would thin the medium.
                if (!bands.Any(x => x.Layer.Kind == StackupKind.Conductor && x.Layer.Name == tie)
                    && !tech.Layers.Any(l => l.Name == tie))
                {
                    _notes.Add($"Dielectric '{b.Layer.Name}' is tied to '{tie}', which is neither a conductor " +
                               "entry nor a drawing layer, so it is kept everywhere, as the planar solver keeps it.");
                    continue;
                }
                var polys = new List<PlanarPolygon>();
                if (bands.FirstOrDefault(x => x.Layer.Kind == StackupKind.Conductor && x.Layer.Name == tie) is { } plate)
                    polys.AddRange(pieces.TryGetValue(plate.Index, out var pl) ? pl.Select(p => p.Poly) : []);
                else if (tech.Layers.FirstOrDefault(l => l.Name == tie) is { } mask &&
                         byLayer.TryGetValue(mask.Key, out var maskShapes))
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. maskShapes.Select(s => (s, 0))], tech, out _, out _))
                        polys.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                filmPolys[b.Index] = polys;
                if (polys.Count == 0)
                    _notes.Add($"Dielectric '{b.Layer.Name}' exists only where '{tie}' is drawn, and nothing " +
                               "is drawn there, so it is not in the 3D problem.");
            }

            // ── Bodies (brief 2's, R-em3d3-5e) ────────────────────────────────────────────────
            var bodies = new List<(TechBody Body, double ZBottom, double ZTop, List<PlanarPolygon> Polys)>();
            foreach (var body in tech.Bodies)
            {
                var on = bands.FirstOrDefault(b => b.Layer.Name == body.SitsOn);
                if (on is null)
                    return No($"Body '{body.Name}' sits on '{body.SitsOn}', which is not a conductor or " +
                              $"dielectric entry of technology '{tech.Name}'s stackup, so it has no height to " +
                              "start from.");
                if (tech.FindMaterial(body.Material) is null)
                    return No($"Body '{body.Name}' is made of '{body.Material}', which technology " +
                              $"'{tech.Name}' does not define in its Materials.");
                var polys = new List<PlanarPolygon>();
                var outlineOperands = body.OutlineLayers.SelectMany(k => byLayer.TryGetValue(k, out var l) ? l : [])
                                                        .Where(s => s is not PathShape { Width: <= 0 }).ToList();
                if (outlineOperands.Count > 0)
                    foreach (var (shape, _) in PlanarExtractor.MergeOverlapping(
                                 [.. outlineOperands.Select(s => (s, 0))], tech, out _, out _))
                        polys.AddRange(PlanarExtractor.ToPolygons(shape, tech, _perDbu));
                else if (body.OutlineLayers.Count > 0)
                {
                    _notes.Add($"Body '{body.Name}' takes its outline from layers on which nothing is drawn, " +
                               "so it is not in the 3D problem.");
                    continue;
                }
                bodies.Add((body, on.TopM, on.TopM + body.ThicknessDbu * stackPerDbu, polys));
            }

            // ── Bond wires (brief-em3d-4): the stem-paired .wBond, landed on this problem's pieces ──
            Em3dWireBuild? wireBuild = null;
            var wireSource = wires;
            if (wireSource is null)
            {
                wireSource = Em3dWireSource.ForLayout(source.AbsolutePath, out string? wbNote, out string? wbRefusal);
                if (wbRefusal is not null) return No(wbRefusal);
                if (wbNote is not null) _notes.Add(wbNote);
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
                    _notes.Add($"Technology '{tech.Name}' designates no ground-reference conductor, so the wires' " +
                               "z = 0 is taken as the bottom of the stack. Mark the ground plane to place them " +
                               "where kernel W does.");
                }
                var pads = pieces.Values.SelectMany(l => l)
                                 .Select(p => new Em3dWirePad(p.Name, p.Poly, p.IsSheet ? p.SheetZ : p.ZTop)).ToList();
                string wbondSource = wireSource.Path is { } wbPath
                    ? $".wBond '{Path.GetFileName(wbPath)}' Materials" : "the .wBond's Materials";
                wireBuild = Em3dWires.Build(wireSource, pads, zOrigin, tech, _tempC,
                                            (m, fromWBond) => Add(m, fromWBond ? wbondSource : TechnologySource()));
                _notes.AddRange(wireBuild.Notes);
                _warnings.AddRange(wireBuild.Warnings);
                if (wireBuild.Refusal is { } wireRefusal) return No(wireRefusal);
            }

            // ── Content bounds, then the air box (R-em3d3-6) ─────────────────────────────────
            double cx0 = double.PositiveInfinity, cy0 = cx0, cx1 = double.NegativeInfinity, cy1 = cx1;
            void Grow(PlanarPolygon p)
            {
                var (a, b, c, d) = p.Bounds();
                cx0 = Math.Min(cx0, a); cy0 = Math.Min(cy0, b); cx1 = Math.Max(cx1, c); cy1 = Math.Max(cy1, d);
            }
            foreach (var list in pieces.Values) foreach (var p in list) Grow(p.Poly);
            foreach (var p in outline) Grow(p);
            foreach (var list in filmPolys.Values) foreach (var p in list) Grow(p);
            foreach (var bd in bodies) foreach (var p in bd.Polys) Grow(p);
            foreach (var v in viaSpans)
                foreach (var p in ViaFootprint(v.Shape)) Grow(p);

            double zLow = double.PositiveInfinity, zHigh = double.NegativeInfinity;
            foreach (var (b, tp) in dielZ)
                if (!filmPolys.TryGetValue(b, out var fp) || fp.Count > 0)
                { zLow = Math.Min(zLow, tp.Bottom); zHigh = Math.Max(zHigh, tp.Top); }
            foreach (var list in pieces.Values)
                foreach (var p in list)
                {
                    zLow  = Math.Min(zLow,  p.IsSheet ? p.SheetZ : p.ZBottom);
                    zHigh = Math.Max(zHigh, p.IsSheet ? p.SheetZ : p.ZTop);
                }
            foreach (var bd in bodies) { zLow = Math.Min(zLow, bd.ZBottom); zHigh = Math.Max(zHigh, bd.ZTop); }
            foreach (var v in viaSpans)
            {
                zLow  = Math.Min(zLow, pecFloor && ReferenceEquals(v.Bottom.Layer, ground!.Layer) ? floorZ : v.Bottom.BottomM);
                zHigh = Math.Max(zHigh, v.Top.TopM);
            }
            foreach (var (_, _, prim) in wireBuild?.Solids ?? [])
            {
                var (x0, y0, z0, x1, y1, z1) = Em3dProblem.Bounds(prim);
                cx0 = Math.Min(cx0, x0); cy0 = Math.Min(cy0, y0); cx1 = Math.Max(cx1, x1); cy1 = Math.Max(cy1, y1);
                zLow = Math.Min(zLow, z0); zHigh = Math.Max(zHigh, z1);
            }

            double pad = DefaultPaddingFractionOfLongestWavelength * C0 / fMin;
            var box = setup.AirBox ?? new EmAirBox();
            double Pad(EmAirBoxFace? f) => f?.PaddingUm is { } um ? um * 1e-6 : pad;
            Em3dBoundaryKind Kind(EmAirBoxFace? f) => f?.Boundary ?? Em3dBoundaryKind.Absorbing;

            var boxMin = new Point3(cx0 - Pad(box.XMin), cy0 - Pad(box.YMin), pecFloor ? floorZ : zLow - Pad(box.ZMin));
            var boxMax = new Point3(cx1 + Pad(box.XMax), cy1 + Pad(box.YMax), zHigh + Pad(box.ZMax));
            var faces = new Em3dFaces(Kind(box.XMin), Kind(box.XMax), Kind(box.YMin), Kind(box.YMax),
                                      pecFloor ? Em3dBoundaryKind.Pec : Kind(box.ZMin), Kind(box.ZMax));
            var airBox = new Em3dAirBox(boxMin, boxMax, faces);

            // ── Solids, in construction order (R-em3d3-1d) ───────────────────────────────────
            //
            // Bottom-up: dielectrics, then the air above the stack, then bodies, then conductors (metal
            // wins over what it is embedded in), then vias, then bond wires (brief 4). STATED
            // here so no backend infers it.
            var solids = new List<Em3dSolid>();
            var sheets = new List<Em3dSheet>();
            int order = 0;

            Em3dPrimitive FullExtent(double z0, double z1)
                => new Em3dBox(new Point3(boxMin.X, boxMin.Y, z0), new Point3(boxMax.X, boxMax.Y, z1));

            double airBottom = pecFloor ? floorZ : double.NaN;
            foreach (var b in inProblem.Where(b => dielZ.ContainsKey(b.Index)))
            {
                var (z0, z1) = dielZ[b.Index];
                string material = DielectricMaterial(b.Layer);
                IReadOnlyList<PlanarPolygon>? lateral =
                    filmPolys.TryGetValue(b.Index, out var film) ? film : outline.Count > 0 ? outline : null;
                if (lateral is { Count: 0 }) continue;
                order++;
                if (lateral is null)
                    solids.Add(Origin(new Em3dSolid(b.Layer.Name, material, Em3dRole.Dielectric, FullExtent(z0, z1), order),
                                      Em3dObjectKind.Dielectric, b.Layer.Name));
                else
                    for (int k = 0; k < lateral.Count; k++)
                        solids.Add(Origin(new Em3dSolid(lateral.Count == 1 ? b.Layer.Name : $"{b.Layer.Name}/{k + 1}",
                                                        material, Em3dRole.Dielectric, Extrude(lateral[k], z0, z1), order),
                                          Em3dObjectKind.Dielectric, b.Layer.Name));
                if (film is null) airBottom = double.IsNaN(airBottom) ? z1 : Math.Max(airBottom, z1);
            }

            if (double.IsNaN(airBottom)) airBottom = boxMin.Z;
            if (boxMax.Z > airBottom)
                solids.Add(Origin(new Em3dSolid(AirSolidName, AirMaterialName(), Em3dRole.Air,
                                                FullExtent(airBottom, boxMax.Z), ++order), Em3dObjectKind.Air, null));

            foreach (var (body, z0, z1, polys) in bodies)
            {
                string material = BodyMaterial(tech.FindMaterial(body.Material)!, out var role);
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

            foreach (var b in bands.Where(b => pieces.ContainsKey(b.Index)))
                foreach (var p in pieces[b.Index])
                {
                    order++;
                    _origins[p.Name] = new Em3dObjectOrigin(Em3dObjectKind.Conductor, b.Layer.Name, p.Layer, p.SheetReason);
                    if (p.IsSheet)
                        sheets.Add(new Em3dSheet(p.Name, p.Material, Ring(p.Poly.Outer),
                                                 [.. p.Poly.HoleRings.Select(Ring)], p.SheetZ, p.ZTop - p.ZBottom, order));
                    else
                        solids.Add(new Em3dSolid(p.Name, p.Material, Em3dRole.Conductor,
                                                 Extrude(p.Poly, p.ZBottom, p.ZTop), order));
                }
            int sheetCount = sheets.Count;
            if (sheetCount > 0)
                _notes.Add($"{sheetCount} conductor piece(s) are thinner than {SheetMaxSkinDepths:G} skin depths " +
                           $"at {fMax / 1e9:G4} GHz and than {SheetMaxFractionOfWidth:G} of their own width, so " +
                           "they are sheets carrying their conductivity and thickness rather than meshed " +
                           "volumes. Both thresholds are provisional.");

            // Vias: R-em3d3-5d. A plated barrel with a wall is a TUBE — the plating cylinder, and inside
            // it a higher-order cylinder of air, which is §1d's order rule doing the subtraction.
            int viaN = 0;
            foreach (var (shape, entry, top, bottom) in viaSpans)
            {
                viaN++;
                string material = ConductorMaterial(entry);
                double z1 = top.TopM;
                double z0 = pecFloor && ReferenceEquals(bottom.Layer, ground!.Layer) ? floorZ : bottom.BottomM;
                string name = $"via/{viaN}";
                if (shape is ViaShape v)
                {
                    double r = (v.DrillSize > 0 ? v.DrillSize : v.PadSize) * _perDbu / 2;
                    var a = new Point3(v.X * _perDbu, v.Y * _perDbu, z0);
                    var e = new Point3(v.X * _perDbu, v.Y * _perDbu, z1);
                    solids.Add(Origin(new Em3dSolid(name, material, Em3dRole.Conductor, new Em3dCylinder(a, e, r), ++order),
                                      Em3dObjectKind.Via, entry.Name, shape.Layer));
                    double wall = (entry.WallThicknessDbu ?? 0) * stackPerDbu;
                    if (entry.Fill == ViaFillKind.Plated && wall > 0 && wall < r)
                        solids.Add(Origin(new Em3dSolid(name + "/fill", AirMaterialName(), Em3dRole.Air,
                                                        new Em3dCylinder(a, e, r - wall), ++order),
                                          Em3dObjectKind.Air, entry.Name));
                }
                else
                {
                    var fp = PlanarExtractor.ToPolygons(shape, tech, _perDbu);
                    order++;
                    for (int k = 0; k < fp.Count; k++)
                        solids.Add(Origin(new Em3dSolid(fp.Count == 1 ? name : $"{name}/{k + 1}", material,
                                                        Em3dRole.Conductor, Extrude(fp[k], z0, z1), order),
                                          Em3dObjectKind.Via, entry.Name, shape.Layer));
                }
            }

            // Bond wires after vias (R-em3d3-1d's order, as brief 3 left room for): each swept wire,
            // then its balls, which meet it face to face on the ball's top.
            foreach (var (name, material, prim) in wireBuild?.Solids ?? [])
                solids.Add(Origin(new Em3dSolid(name, material, Em3dRole.Conductor, prim, ++order),
                                  Em3dObjectKind.Wire, null));

            // ── Ports (R-em3d3-2) ─────────────────────────────────────────────────────────────
            var ports = new List<Em3dPort>();
            if (BuildPorts(bands, pieces, pecFloor ? ground : null, floorZ, ports) is { } portRefusal)
                return No(portRefusal);

            // ── Notes on what was resolved and how ──────────────────────────────────────────
            if (unknownTemperature.Count > 0)
                _notes.Add($"{string.Join(", ", unknownTemperature.Select(n => $"'{n}'"))} " +
                           $"name{(unknownTemperature.Count == 1 ? "s" : "")} no material, so " +
                           $"{(unknownTemperature.Count == 1 ? "its" : "their")} σ is a number of unknown " +
                           $"temperature and is used as given at {Fmt(_tempC)} °C. Name a material with an " +
                           "α₂₀ to have it evaluated at the operating temperature.");
            if (noAlpha.Count > 0)
                _notes.Add($"{string.Join(", ", noAlpha.Select(n => $"'{n}'"))} state" +
                           $"{(noAlpha.Count == 1 ? "s" : "")} no α₂₀, so σ₂₀ is used at every temperature, " +
                           $"{Fmt(_tempC)} °C included.");
            _notes.Add(pecFloor
                ? $"The air box's floor is '{ground!.Layer.Name}' as a PEC plane at {Fmt(floorZ * 1e6)} µm: it is " +
                  "the lowest ground-reference conductor, nothing is drawn on it and nothing is drawn below it." +
                  (zMinFace?.PaddingUm is not null ? " The AirBox ZMin padding does not apply to a floor on the plane." : "")
                : "The air box's floor is below the geometry" +
                  (floorStatedAway ? ", as this setup's AirBox states." :
                   ground is null ? ": the technology designates no ground-reference conductor."
                                  : $": '{ground.Layer.Name}' is drawn or has metal below it, so it is " +
                                    "geometry rather than a floor."));

            var problem = new Em3dProblem(solids, sheets, _materials, ports, airBox, frequency, _tempC);
            return new Em3dGenerationResult(problem, null, _notes)
            {
                Warnings = _warnings,
                Wires = wireBuild?.Reports ?? [],
                Origins = _origins,
                MaterialSources = _materialSource,
                NoAlpha = [.. noAlpha.Union(wireBuild?.NoAlpha ?? []).Order(StringComparer.Ordinal)],
                UnknownTemperature = unknownTemperature,
            };
        }

        // ── Ports ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The planar port extractor, run over this problem's own signal-conductor polygons — so the
        /// numbering (R-em3d3-2d), the side inference and every refusal (R-em3d3-2b) are its own —
        /// then each port turned into a vertical sheet from its conductor to its return.
        /// </summary>
        private string? BuildPorts(IReadOnlyList<PlanarExtractor.StackBand> bands,
                                   Dictionary<int, List<Piece>> pieces,
                                   PlanarExtractor.StackBand? floorPlane, double floorZ, List<Em3dPort> ports)
        {
            var signal = bands.Where(b => pieces.ContainsKey(b.Index) && !b.Layer.IsGroundReference).ToList();
            if (signal.Count == 0)
                return "This layout draws metal only on ground-reference conductors, so there is no signal " +
                       "conductor for a port to drive.";

            var layers = signal.Select(b => new PlanarConductorLayer(
                b.Layer.Name, [.. pieces[b.Index].Select(p => p.Poly)], b.Layer.SigmaSm, b.TopM - b.BottomM)).ToList();
            var portProblem = new PlanarProblem(layers, new GroundedSlab(1, new EmMaterial(1, 0)), 0);

            var extracted = EmPortExtraction.Extract(source.View.Shapes, portProblem, source.DbuPerMicron,
                                                     setup.ResolvePortZ0, source.View.DisplayUnit);
            _notes.AddRange(extracted.Notes);
            if (!extracted.Ok) return extracted.Refusal;

            foreach (var p in extracted.Ports)
            {
                if (p.Kind != PlanarPortKind.Edge || p.IsConductorReferenced)
                    return $"Port {p.Number} is {(p.IsConductorReferenced ? "referenced to a drawn conductor" : "an internal port")}. " +
                           "A 3D setup builds lumped EDGE ports in this version — a vertical sheet from the " +
                           "conductor's end down (or up) to its return plane. Make it an edge port at a " +
                           "conductor end, or solve this layout with a planar setup.";

                var band  = signal[p.LayerIndex ?? 0];
                var piece = PieceAt(pieces[band.Index], p.Location.X, p.Location.Y, p.Side, out var edge);
                if (piece is null || edge is null)
                    return $"Port {p.Number} at ({Fmt(p.Location.X * 1e6)}, {Fmt(p.Location.Y * 1e6)}) µm is on " +
                           $"'{band.Layer.Name}' but no conductor end could be found under it.";
                var (edgeAt, lo, hi) = edge.Value;

                // The return: the .cem's named plane, else R-em-4 (RP-3's flip when the plane is above).
                StackupLayer? ret;
                bool above;
                if (setup.GroundStackupLayerName is { Length: > 0 } named)
                {
                    ret = bands.FirstOrDefault(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.Name == named)?.Layer;
                    if (ret is null)
                        return $"This EM setup names '{named}' as its return plane, but technology '{tech.Name}' " +
                               "has no conductor stackup layer with that name.";
                    above = bands.First(b => ReferenceEquals(b.Layer, ret)).BottomM >= band.TopM;
                }
                else
                {
                    ret = PlanarExtractor.InferredReturnPlane(tech.Stackup, band.Index, out above);
                    if (ret is null)
                        return $"Port {p.Number} is on '{band.Layer.Name}', and technology '{tech.Name}' " +
                               "designates no ground-reference conductor above or below it for the port to " +
                               "return through. Mark the plane as a ground reference, or name it as this " +
                               "setup's return plane.";
                }
                var retBand = bands.First(b => ReferenceEquals(b.Layer, ret));

                // What the port's sheet ends on at the return side, and at which height.
                string negative;
                double retFace;
                if (floorPlane is not null && ReferenceEquals(retBand.Layer, floorPlane.Layer))
                {
                    negative = Em3dAirBox.FaceName("zmin");
                    retFace  = floorZ;
                }
                else if (pieces.TryGetValue(retBand.Index, out var retPieces) &&
                         PieceContaining(retPieces, p.Side, edgeAt, (lo + hi) / 2) is { } rp)
                {
                    negative = rp.Name;
                    retFace  = rp.IsSheet ? rp.SheetZ : above ? rp.ZBottom : rp.ZTop;
                }
                else
                    return $"Port {p.Number} returns through '{retBand.Layer.Name}', but there is no metal on " +
                           "it under the port, and it is not the air box's floor — so the port's sheet has " +
                           "nothing to end on. Draw the plane under the port, or name a different return plane.";

                double sigFace = piece.IsSheet ? piece.SheetZ : above ? piece.ZTop : piece.ZBottom;
                double zLo = above ? sigFace : retFace, zHi = above ? retFace : sigFace;
                if (!(zHi > zLo))
                    return $"Port {p.Number}'s return plane '{retBand.Layer.Name}' is not separated from " +
                           $"'{band.Layer.Name}' by any height, so the port's sheet would be empty.";

                bool alongX = p.Side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
                var min = alongX ? new Point3(edgeAt, lo, zLo) : new Point3(lo, edgeAt, zLo);
                var max = alongX ? new Point3(edgeAt, hi, zHi) : new Point3(hi, edgeAt, zHi);
                var normal = p.Side switch
                {
                    PlanarPortSide.MinX => new Point3(1, 0, 0),
                    PlanarPortSide.MaxX => new Point3(-1, 0, 0),
                    PlanarPortSide.MinY => new Point3(0, 1, 0),
                    _                   => new Point3(0, -1, 0),
                };
                var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
                ports.Add(new Em3dPort(p.Number, $"port/{p.Number}", piece.Name, negative, min, max,
                                       new Point3(0, 0, above ? -1 : 1), p.Z0,
                                       // R-em3d3-2c — a Tier A port's reference plane IS its sheet.
                                       new Em3dReferencePlane(centre, normal, 0)));
            }
            return null;
        }

        /// <summary>The piece whose metal the port sits on, and the end it names: the edge's
        /// coordinate along the port's normal axis, and the metal's extent across it there.</summary>
        private static Piece? PieceAt(List<Piece> pieces, double x, double y, PlanarPortSide side,
                                      out (double At, double Lo, double Hi)? edge)
        {
            edge = null;
            Piece? best = null;
            double bestD = double.PositiveInfinity;
            foreach (var p in pieces)
            {
                var e = EdgeOf(p.Poly, x, y, side);
                if (e is null) continue;
                double d = Math.Abs(e.Value.At - (side is PlanarPortSide.MinX or PlanarPortSide.MaxX ? x : y));
                if (d < bestD) { bestD = d; best = p; edge = e; }
            }
            return best;
        }

        /// <summary>The return piece under a port's sheet, tested just inside the conductor end.</summary>
        private static Piece? PieceContaining(List<Piece> pieces, PlanarPortSide side, double at, double across)
        {
            foreach (var p in pieces)
            {
                var (x0, y0, x1, y1) = p.Poly.Bounds();
                double eps = 1e-6 * Math.Max(x1 - x0, y1 - y0);
                double inward = side is PlanarPortSide.MinX or PlanarPortSide.MinY ? eps : -eps;
                bool alongX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
                double qx = alongX ? at + inward : across, qy = alongX ? across : at + inward;
                if (p.Poly.Contains(qx, qy) || p.Poly.Contains(alongX ? at - inward : qx, alongX ? qy : at - inward))
                    return p;
            }
            return null;
        }

        /// <summary>
        /// The conductor end a port at (x, y) names on <paramref name="poly"/>: the boundary crossing
        /// nearest the label along the port's normal axis, and the interval of metal across that axis
        /// just inside it. Null when the label's line meets the polygon nowhere.
        /// </summary>
        private static (double At, double Lo, double Hi)? EdgeOf(PlanarPolygon poly, double x, double y, PlanarPortSide side)
        {
            bool alongX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            double u = alongX ? x : y, v = alongX ? y : x;

            // Crossings of the line v = const with every ring, in the u coordinate.
            var along = Crossings(poly, v, alongX);
            if (along.Count == 0) return null;
            double at = along.OrderBy(c => Math.Abs(c - u)).First();

            var (x0, y0, x1, y1) = poly.Bounds();
            double eps = 1e-6 * Math.Max(x1 - x0, y1 - y0);
            double inside = at + (side is PlanarPortSide.MinX or PlanarPortSide.MinY ? eps : -eps);

            var across = Crossings(poly, inside, !alongX);
            across.Sort();
            for (int i = 0; i + 1 < across.Count; i += 2)
                if (v >= across[i] - eps && v <= across[i + 1] + eps)
                    return (at, across[i], across[i + 1]);
            return null;
        }

        /// <summary>Where the line (second coordinate = <paramref name="c"/>) crosses the polygon's
        /// rings, as values of the first coordinate. <paramref name="xFirst"/> picks x as the first.</summary>
        private static List<double> Crossings(PlanarPolygon poly, double c, bool xFirst)
        {
            var hits = new List<double>();
            void Ring(IReadOnlyList<EmPoint> ring)
            {
                int n = ring.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double a1 = xFirst ? ring[j].X : ring[j].Y, b1 = xFirst ? ring[j].Y : ring[j].X;
                    double a2 = xFirst ? ring[i].X : ring[i].Y, b2 = xFirst ? ring[i].Y : ring[i].X;
                    if (b1 > c == b2 > c) continue;
                    hits.Add(a1 + (c - b1) * (a2 - a1) / (b2 - b1));
                }
            }
            Ring(poly.Outer);
            foreach (var h in poly.HoleRings) Ring(h);
            return hits;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────────────────

        private string Add(Em3dMaterial m, string source)
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

        private string TechnologySource() => $"technology '{tech.Name}' Materials";

        private static string EntrySource(StackupLayer entry) => $"stackup entry '{entry.Name}' (its own numbers)";

        private Em3dSolid Origin(Em3dSolid s, Em3dObjectKind kind, string? entry, LayerKey? layer = null)
        {
            _origins[s.Name] = new Em3dObjectOrigin(kind, entry, layer, null);
            return s;
        }

        private static bool SameValues(Em3dMaterial a, Em3dMaterial b) =>
            a.Epsr == b.Epsr && a.TanD == b.TanD && a.Mur == b.Mur && a.SigmaSm == b.SigmaSm &&
            (a.EpsrTensor ?? []).SequenceEqual(b.EpsrTensor ?? []);

        private string AirMaterialName()
        {
            if (tech.FindMaterial(AirMaterial) is { } m)
                return Add(new Em3dMaterial(m.Name, m.Epsr ?? 1, null, m.TanD ?? 0, m.Mur ?? 1, 0), TechnologySource());
            return Add(new Em3dMaterial(AirMaterial, 1, null, 0, 1, 0), "built in (free space)");
        }

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

        private static Em3dExtrudedPolygon Extrude(PlanarPolygon p, double z0, double z1)
            => new(Ring(p.Outer), [.. p.HoleRings.Select(Ring)], z0, z1);

        private static IReadOnlyList<Point2> Ring(IReadOnlyList<EmPoint> ring)
            => [.. ring.Select(q => new Point2(q.X, q.Y))];

        /// <summary>2·Area / Perimeter: a strip's width, however long it is drawn.</summary>
        private static double CharacteristicWidth(PlanarPolygon poly)
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

        private Em3dGenerationResult No(string refusal) => new(null, refusal, _notes) { Warnings = _warnings };

        private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// <b>R-em3d3-5c — the drawing layers that are the board outline, found the way the interchange
    /// code already names them</b>, not by a new convention: a layer whose board-format name is
    /// <c>Edge.Cuts</c> (what the board reader and writer map the outline to, and what the shipped
    /// PCB technologies declare), or whose Gerber file function is <c>Profile</c> (X2's own word for
    /// the outline, which a Gerber import records on the layer it creates). A layer merely NAMED
    /// "Outline" is not enough: a name decides a sentence, never geometry (GerberLayerCascade's rule).
    /// </summary>
    public static HashSet<LayerKey> BoardOutlineLayers(Technology tech)
    {
        var keys = new HashSet<LayerKey>();
        foreach (var l in tech.Layers)
        {
            if (string.Equals(l.Interchange?.PcbLayerName, "Edge.Cuts", StringComparison.Ordinal) ||
                GerberLayerCascade.ParseFileFunction(l.Interchange?.GerberFileFunction) is { } fn &&
                string.Equals(fn.Kind, "Profile", StringComparison.OrdinalIgnoreCase))
                keys.Add(l.Key);
        }
        return keys;
    }
}
