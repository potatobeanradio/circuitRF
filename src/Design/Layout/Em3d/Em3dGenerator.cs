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
//   * the return plane    NOT the planar rule since brief-dfr28-4: a port's return is walked from its
//                         own artwork (ReturnWalk), which the planar kernel has no way to do;
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
using Piece = CircuitRF.Design.Layout.Em3d.Em3dLayoutSolids.Builder.Piece;

namespace CircuitRF.Design.Layout.Em3d;

/// <summary>brief-dfr28-4 — where one port returns: the conductor layer (or the air box's floor's plane), whether it
/// is above the port's own layer, and how it was decided — <c>walked</c> from the artwork, stated by the port's own
/// <c>Return</c>, or named by the setup's legacy return plane.</summary>
/// <param name="Coplanar">brief-dfr34-1 R-dfr34-1-6 — the return is ground beside the line on its own layer (Return = Same
/// layer); <paramref name="Above"/> is then not read.</param>
public sealed record Em3dPortReturn(string Layer, bool Above, bool Floor, string DecidedBy, bool Coplanar = false);

/// <summary>A port left out of a problem generated with <c>portsEach</c>, and why.</summary>
public sealed record Em3dPortLeftOut(int Port, string Why);

/// <summary>The generator's answer: a problem, or a refusal naming what is missing, plus every
/// note it made on the way. A problem is returned unvalidated — <see cref="Em3dProblem.Validate"/>
/// is the backend's first call, and <c>check</c>'s.</summary>
public sealed record Em3dGenerationResult(Em3dProblem? Problem, string? Refusal, IReadOnlyList<string> Notes)
{
    public bool Ok => Problem is not null && Refusal is null;

    /// <summary>brief-em3d-87 — for a <c>.c3d</c>'s problem, every file its elaboration read (C3dElaboration.FilesRead): what
    /// the run's input manifest hashes. Empty for a layout's.</summary>
    public IReadOnlyList<string> FilesRead { get; init; } = [];

    /// <summary>brief-emdc-2 R-emdc2-1 — the setup's driven sweep held 0 Hz, which the problem does not: the run answers it
    /// with circuitRF's conduction solve (Em3dDcSolve). Kept BESIDE the problem, so no lowering and no provenance hash sees it.</summary>
    public bool WantsDc { get; init; }

    /// <summary>brief-emb-1 — the component ports a <c>.c3d</c> setup built (R-emb1-3), as the run solved them: what the
    /// co-simulation netlist (R-emb1-7) is written from. Null when the setup names none.</summary>
    public CircuitRF.Design.ThreeD.C3dComponentPortBuild? ComponentPorts { get; init; }

    /// <summary>brief-dfr28-4 R-dfr28-4-5 — each layout port's return, by port number: the layer, the side, and who
    /// decided it. Kept BESIDE the problem, as <see cref="Origins"/> is: <c>explain</c> reads it.</summary>
    public IReadOnlyDictionary<int, Em3dPortReturn> PortReturns { get; init; } = new Dictionary<int, Em3dPortReturn>();

    /// <summary>brief-dfr34-4 R-dfr34-4-4 — the layout edit a note offers, keyed by that note: a via a static solve gave its
    /// copper's net, and <i>Give the via its copper's net</i> writes it into the layout.</summary>
    public IReadOnlyDictionary<string, Em3dViaNetFix> ViaNetFixes { get; init; } = new Dictionary<string, Em3dViaNetFix>();

    /// <summary>brief-dfr35-1 R-dfr35-1-7 — what <see cref="Em3dGeometryCheck"/> found in the outlines this run will mesh; each is
    /// among <see cref="Warnings"/> too, as a sentence.</summary>
    public IReadOnlyList<Em3dGeometryFinding> GeometryFindings { get; init; } = [];

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

    /// <summary>Why the problem has no ports, when it was generated with <c>portsOptional</c> and the ports refused:
    /// the refusal a solve would have stopped on. Null otherwise.</summary>
    public string? PortRefusal { get; init; }

    /// <summary>The ports left out, each with the refusal a solve would have stopped on, when the problem was generated
    /// with <c>portsEach</c> (a layout's ports brought into a <c>.c3d</c>). Empty otherwise.</summary>
    public IReadOnlyList<Em3dPortLeftOut> PortsLeftOut { get; init; } = [];

    /// <summary>The ports <c>portsEach</c> moved in from their line's end to where a pulled-back return plane begins,
    /// each said in a sentence (also among <see cref="Notes"/>) — and brief-dfr34-1's, a return through an opening or
    /// through a level the technology does not mark: where a port went that its user would not assume.</summary>
    public IReadOnlyList<string> PortsMoved { get; init; } = [];

    /// <summary>Why a solve would have refused the geometry, when it was generated with <c>padsOptional</c> and drawn
    /// anyway: no conductor artwork, or a wire end over no pad. Null otherwise.</summary>
    public string? PadRefusal { get; init; }

    /// <summary>brief-sar-6 — each phantom of a <c>.c3d</c>'s problem and its layers' solid names, outermost first: what a
    /// SAR region may name a phantom by, and the innermost layer B1's mean is taken over by default. Empty for a layout's.</summary>
    public IReadOnlyList<(string Phantom, IReadOnlyList<string> Layers)> Phantoms { get; init; } = [];
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

/// <summary>
/// Designer round 33 — the padding a face takes when the setup states none (<see cref="Em3dGenerator.DefaultFacePadding"/>):
/// <see cref="Em3dGenerator.DefaultPaddingSidesAndTopH"/> × H on the top and sides and
/// <see cref="Em3dGenerator.DefaultPaddingBelowH"/> × H below when <see cref="HeightM"/> is set, never more than
/// <see cref="CapM"/>, the wavelength rule; <see cref="CapM"/> on every face otherwise. With <see cref="TopByExtent"/> the
/// top instead takes <see cref="Em3dGenerator.DefaultPaddingTopPercent"/> of the content's height, never more than <see cref="CapM"/>.
/// brief-dfr34-2 R-dfr34-2-3 — the floor's H is <see cref="HeightBelowM"/>, measured from the bottom metal upward, when there
/// is one: H under the top metal described the floor only when the stack happened to be symmetric.
/// </summary>
public sealed record Em3dDefaultPadding(double CapM, string CapBasis, double? HeightM, bool TopByExtent = false,
                                        double? HeightBelowM = null, string? CapShort = null)
{
    private double Multiple(string face) => face == "zmin" ? Em3dGenerator.DefaultPaddingBelowH : Em3dGenerator.DefaultPaddingSidesAndTopH;

    /// <summary>The H a face is padded by: the floor's own when there is one, the top metal's otherwise.</summary>
    private double? H(string face) => HeightM is null ? null : face == "zmin" ? HeightBelowM ?? HeightM : HeightM;

    /// <summary>The H rule's share of the face's axis is rounded to this many percent, so the panel reads round numbers.</summary>
    public const double RoundToPercent = 5;

    /// <summary>The padding of <paramref name="face"/> (xmin … zmax), metres, on content <paramref name="spanM"/> long along
    /// that face's axis. The H rule is rounded to the nearest <see cref="RoundToPercent"/> % of the span, never below it; an
    /// axis with no extent keeps the length.</summary>
    public double For(string face, double spanM)
    {
        if (TopByExtent && face == "zmax" && spanM > 0) return Math.Min(Em3dGenerator.DefaultPaddingTopPercent / 100 * spanM, CapM);
        if (H(face) is not { } h) return CapM;
        double pad = Multiple(face) * h;
        if (spanM > 0)
        {
            double pct = Math.Max(RoundToPercent, Math.Round(pad / spanM * 100 / RoundToPercent, MidpointRounding.AwayFromZero) * RoundToPercent);
            pad = pct / 100 * spanM;
        }
        return Math.Min(pad, CapM);
    }

    /// <summary>Why <paramref name="face"/> stands where it does, as the panel's tooltip and <c>explain</c> print it.</summary>
    public string Basis(string face, double spanM) => TopByExtent && face == "zmax" && spanM > 0 && For(face, spanM) < CapM
        ? $"{Em3dGenerator.DefaultPaddingTopPercent.ToString(CultureInfo.InvariantCulture)} % of the content's height"
        : H(face) is { } h && For(face, spanM) < CapM
        ? $"{Multiple(face).ToString(CultureInfo.InvariantCulture)} × H, H = {(h * 1e6).ToString("G4", CultureInfo.InvariantCulture)} µm of " +
          (face == "zmin" && HeightBelowM is not null ? "dielectric over the bottom metal" : "dielectric under the top metal") +
          (spanM > 0 ? $", to the nearest {RoundToPercent.ToString(CultureInfo.InvariantCulture)} %" : "")
        : CapBasis;

    /// <summary>brief-dfr34-2 R-dfr34-2-5 — the rule in a few words, as the panel prints it after the length ("10 × H",
    /// "λ/4 at 2.45 GHz").</summary>
    public string ShortBasis(string face, double spanM) => TopByExtent && face == "zmax" && spanM > 0 && For(face, spanM) < CapM
        ? "the content's height"
        : H(face) is not null && For(face, spanM) < CapM
        ? $"{Multiple(face).ToString(CultureInfo.InvariantCulture)} × H"
        : CapShort ?? CapBasis;
}

public static class Em3dGenerator
{
    /// <summary>
    /// <b>R-em3d3-6a — the default air-box padding, as a fraction of the LONGEST free-space
    /// wavelength in the band</b> (c / the lowest non-zero frequency), on the four lateral faces and
    /// the top, and never more than the structure's largest extent (<see cref="DefaultPadding"/>). An
    /// eighth: far enough that a first-order absorbing face sees a field that has mostly decayed, near
    /// enough that the air is not most of the mesh. A backend may ENLARGE the box
    /// (Palace's absorbing face wants more room than openEMS's PML) and says so when it does; it
    /// never shrinks it. A <c>.cem</c> overrides any face with <c>AirBox</c>.
    /// </summary>
    public const double DefaultPaddingFractionOfLongestWavelength = 1.0 / 8.0;

    /// <summary>
    /// <b>brief-em3d-31 — the default padding when a driven setup asks for a radiation pattern: a QUARTER.</b>
    /// The pattern is transformed from a closed surface that must sit a few cells inside the absorber and a cell
    /// clear of every conductor (OpenEmsFarField), and Palace's far-field integral runs over first-order
    /// absorbing faces that want distance from the radiator. At an eighth the shipped 5.8 GHz patch leaves
    /// ~7 mm where openEMS's surface needs ~10, and every such setup would be refused out of the box. A padding
    /// the <c>.cem</c> states still wins, and is still refused, naming the face, if it is too tight.
    /// </summary>
    public const double PatternPaddingFractionOfLongestWavelength = 1.0 / 4.0;

    /// <summary>
    /// brief-em3d-23 R-em3d23-2b — the default wave-port region, in multiples of the line's width w and its
    /// height h above its return. <b>The rule is the microstrip one repeated across full-wave tools'
    /// documentation and application notes</b> (their names are not written in this repository): a port
    /// about ten line widths wide — ten substrate heights when the line is narrower than its substrate,
    /// where the field spreads by h rather than by w — and six to ten substrate heights tall. The middle
    /// of the height range is taken: at 6, a region 20 % smaller (4.8 h) moved a 50 Ω microstrip's |S21| by
    /// 0.023 dB, outside gate 5's 0.02 dB; the top of the range is the first to support a second
    /// propagating mode (R-em3d23-3 warns when it does). Gate 5 checks the answer does not depend on it.
    /// Stripline and coax cannot be built from a layout's edge port in this version, so only this rule
    /// is needed. A <c>.cem</c> Ports3D entry's Width and Height override it per port.
    /// </summary>
    public const double DefaultWaveWidthFactor = 10, DefaultWaveHeightFactor = 8;

    /// <summary>
    /// How far short of a lumped port's line end its return plane may stop, as a fraction of the line's
    /// width, before the port is refused: the sheet moves in to where the plane begins instead. A tenth of
    /// the width moves the reference plane by less than the line's own end effect, which is of the order of
    /// the substrate height; the gap it forgives is the kind a hand-placed vertex leaves — a designer's
    /// 4-layer board put the plane's edge 6.2 µm short of a 200 µm line, invisible at any working zoom.
    /// </summary>
    public const double ReturnGapToleranceOfWidth = 0.1;

    /// <summary>
    /// How far short of the line's end, as a multiple of the line's width, a return plane may stop when a layout's ports
    /// are brought into a <c>.c3d</c> (<c>portsEach</c>): the sheet moves in to where the plane begins and a note says
    /// how far. A board-edge launch's inner ground is routinely pulled back from the outline by a fraction of a
    /// millimetre, which is where the port goes in practice; three widths keeps a sheet from sliding a long way up a
    /// line that runs over no plane at all.
    /// </summary>
    public const double TranslatedReturnGapOfWidth = 3;

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

    /// <summary>
    /// The 3D problem <paramref name="setup"/> describes. <paramref name="source"/> is what
    /// <see cref="EmSetupResolver"/> resolved — the SAME walk-ups the planar run takes (the layout
    /// relative to the <c>.cem</c>'s workspace, the technology relative to the layout's), which this
    /// method does not repeat.
    /// </summary>
    /// <param name="displaySlabs">True for a PLANAR setup previewed in 3D: every laterally unbounded slab is drawn to
    /// <see cref="SlabLateralBound"/>'s shape instead of across the air box — for viewing only, never for a solve.</param>
    /// <param name="wires">The bond wires to include. Null — the ordinary case — takes the
    /// <c>.wBond</c> stem-paired with the layout (WB40), if it has one.</param>
    /// <param name="portsOptional">True for a picture, never for a solve: ports that refuse leave the problem with no
    /// ports and the refusal in <see cref="Em3dGenerationResult.PortRefusal"/>, so the geometry is still drawn.</param>
    /// <param name="portsEach">True when a layout's ports are being brought into a <c>.c3d</c>, never for a solve: a port
    /// that refuses is left out alone (<see cref="Em3dGenerationResult.PortsLeftOut"/>) and the others are still built, and
    /// a return plane pulled back from the line's end by more than <see cref="ReturnGapToleranceOfWidth"/> — a board-edge
    /// launch's inner ground, typically — moves the sheet in to where the plane begins, up to
    /// <see cref="TranslatedReturnGapOfWidth"/>, with a note. The .c3d's port is an editable rectangle, so where it was
    /// put is shown rather than refused.</param>
    /// <param name="padsOptional">True for a picture, never for a solve: wires are drawn on a layout with no conductor
    /// artwork, and a wire end over no conductor lands on an undrawn pad (<see cref="Em3dLayoutSolidsOptions.PadsOptional"/>);
    /// the refusal is in <see cref="Em3dGenerationResult.PadRefusal"/>.</param>
    /// <summary>brief-emb-1 — a <c>.cem</c> on a layout asking for component ports.</summary>
    public const string ComponentPortsOnLayoutRefusal =
        "This setup names ComponentPorts, which are built in a 3D view of the layout: make one with " +
        "'new view3d <cell> --from-layout' (Design ▸ New 3D View from Layout) and run this setup there, or point the .cem's LayoutRef at that .c3d.";

    public static Em3dGenerationResult Generate(EmSetup setup, EmLayoutSource source, Technology tech,
                                                Em3dWireSource? wires = null, bool displaySlabs = false,
                                                bool portsOptional = false, bool padsOptional = false,
                                                bool portsEach = false)
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tech);
        // brief-emb-1 R-emb1-2 — component ports are built in a 3D view of the layout, where the lowered conductors they land on
        // have names; a .cem on the layout itself places its ports from its port labels.
        if (setup.ComponentPorts.Count > 0)
            return new Em3dGenerationResult(null, ComponentPortsOnLayoutRefusal, []);
        return new Run(setup, source, tech, wires, displaySlabs, portsOptional, padsOptional, portsEach).Go();
    }

    // ── One generation ─────────────────────────────────────────────────────────────────────────

    private sealed class Run(EmSetup setup, EmLayoutSource source, Technology tech, Em3dWireSource? wires, bool displaySlabs,
                             bool portsOptional, bool padsOptional, bool portsEach)
    {
        // brief-em3d-42 R-em3d42-2 — the geometry lives in Em3dLayoutSolids now; this class adds the sweep,
        // the ports, the air box, the air above the stack and the terminals around its stages. The notes
        // and materials lists are the builder's, appended to between its stages, so their ORDER is the one
        // the generator always wrote (the pre-split dumps hold it).
        private Em3dLayoutSolids.Builder _g = null!;
        private List<string> _notes = [];
        private double _tempC;
        private string? _portRefusal;
        private bool _wantsDc;
        private readonly Dictionary<int, Em3dPortReturn> _portReturns = [];
        private readonly List<Em3dPortLeftOut> _portsLeftOut = [];
        private readonly List<string> _portsMoved = [];
        private readonly Dictionary<string, Em3dViaNetFix> _viaNetFixes = new(StringComparer.Ordinal);

        public Em3dGenerationResult Go()
        {
            // ── The sweep ─────────────────────────────────────────────────────────────────────
            double[] freqs;
            try { freqs = setup.ExpandFrequencies(); }
            catch (Exception ex) { return No(EmDiagnostics.FrequencySweepUnresolvable(ex.Message).Render()); }
            var positive = freqs.Where(f => f > 0).ToArray();
            if (positive.Length == 0) return No(EmDiagnostics.FrequencySweepEmpty().Render());
            double fMin = positive.Min(), fMax = positive.Max();
            // brief-emdc-2 R-emdc2-1 — a driven sweep's 0 Hz point is circuitRF's conduction solve (Em3dDcRow), not the solver's.
            _wantsDc = positive.Length < freqs.Length && setup.Problem3D == Em3dProblemType.Driven;
            if (_wantsDc) _notes.Add(CircuitRF.Design.Em3d.Em3dDcRow.ZeroHzNote);
            var frequency = setup.ToEm3dFrequency(positive);

            // ── Temperature (R-em3d3-4) ───────────────────────────────────────────────────────
            _tempC = setup.OperatingTempC ?? EmSetup.DefaultOperatingTempC;

            // The .cem's floor steps §6's rule aside only when it states a DIFFERENT boundary (Em3dLayoutSolids).
            var zMinFace = setup.AirBox?.ZMin;
            bool floorStatedAway = zMinFace?.Boundary is { } zMinKind && zMinKind != Em3dBoundaryKind.Pec;

            // ── The geometry, up to the bond wires (Em3dLayoutSolids' first stage) ────────────
            var earlier = _notes;
            _g = new Em3dLayoutSolids.Builder(source, tech, wires,
                new Em3dLayoutSolidsOptions(fMax, _tempC, Instance: false) { RegionSetup = setup, Conductors = setup.Conductors, FloorAllowed = !floorStatedAway, DisplaySlabs = displaySlabs, PadsOptional = padsOptional, RobustMeshing = setup.RobustMeshing,
                  SolidVias = setup.Problem3D != Em3dProblemType.Thermal });
            _g.Notes.AddRange(earlier);
            _notes = _g.Notes;
            if (_g.Prepare() is { } geometryRefusal) return No(geometryRefusal);
            var bands = _g.Bands;
            var pieces = _g.Pieces;
            var ground = _g.Ground;
            bool pecFloor = _g.PecFloor;
            double floorZ = _g.FloorZ;

            // ── Ports (R-em3d3-2) ─────────────────────────────────────────────────────────────
            // Built BEFORE the air box (brief-em3d-23): a wave port's side of the box has no padding.
            var ports = new List<Em3dPort>();
            var waves = new List<WaveSpec>();
            // brief-em3d-22 R-em3d22-1c — an electrostatic solve has no ports; a magnetostatic one keeps
            // only the ports its terminals are driven through, each a source sheet. brief-em3d-23 — an
            // eigenmode solve needs none: a closed cavity has no port, and a port it has is a load.
            bool anyPortLabel = PortLabels.Shapes.Any(sh => sh is LabelShape { IsPort: true });
            if (setup.Problem3D != Em3dProblemType.Electrostatic &&
                (setup.Problem3D != Em3dProblemType.Eigenmode || anyPortLabel) &&
                BuildPorts(bands, pieces, pecFloor ? ground : null, floorZ, ports, waves) is { } portRefusal)
            {
                if (!portsOptional) return No(portRefusal);
                _portRefusal = portRefusal;
                ports.Clear();
                waves.Clear();
            }
            foreach (int n in setup.Ports3D.Where(q => q.Kind == Em3dPortKind.Wave).Select(q => q.Port).Distinct())
                if (_portRefusal is null && ports.All(q => q.Number != n) && _portsLeftOut.All(q => q.Port != n))
                    return No($"The setup makes port {n} a wave port, and this layout has no port {n}.");

            // ── Content bounds (the builder's), then the air box (R-em3d3-6) ─────────────────
            var (cx0, cy0, cx1, cy1, zLow, zHigh) = (_g.Cx0, _g.Cy0, _g.Cx1, _g.Cy1, _g.ZLow, _g.ZHigh);
            // Designer round 33 — the default padding is a multiple of the dielectric height under the top metal.
            double? paddingH = PaddingHeight(
                _g.Pieces.Values.SelectMany(l => l).Select(q => q.IsSheet ? (q.SheetZ, q.SheetZ) : (q.ZBottom, q.ZTop)),
                _g.Bands.Where(b => b.Layer.Kind == StackupKind.Dielectric && (!pecFloor || b.BottomM >= floorZ - 1e-15))
                        .Select(b => (b.BottomM, b.TopM)),
                pecFloor ? floorZ : null);
            // brief-dfr34-2 R-dfr34-2-3 — the floor's H, from the bottom metal upward.
            double? paddingHBelow = PaddingHeightBelow(
                _g.Pieces.Values.SelectMany(l => l).Select(q => q.IsSheet ? (q.SheetZ, q.SheetZ) : (q.ZBottom, q.ZTop)),
                _g.Bands.Where(b => b.Layer.Kind == StackupKind.Dielectric).Select(b => (b.BottomM, b.TopM)));
            // Designer round 33 — an unbroken drawn bottom ground plane is the floor when the setup states nothing for ZMin.
            double? planeFloor = !pecFloor && setup.AirBox?.ZMin is null && ByHeightApplies(setup) ? _g.PlaneFloorZ : null;
            var airBox = PaddedAirBox(setup, (cx0, cy0, cx1, cy1, zLow, zHigh), fMin, pecFloor ? floorZ : planeFloor,
                                waves.Select(w => (w.Face, w.EdgeAt, w.Port.Number)).ToList(), _notes, out string? boxRefusal, paddingH,
                                paddingHBelow);
            if (boxRefusal is not null) return No(boxRefusal);
            // A wave port is still its lumped sheet here, and it belongs ON its face: only a lumped port is moved clear.
            airBox = ClearOfPortSheets(airBox!, [.. ports.Where(q => waves.All(w => w.Port.Number != q.Number))], _notes,
                                       out string? sheetRefusal);
            if (sheetRefusal is not null) return No(sheetRefusal);
            foreach (var w in waves)
            {
                var (wave, why) = WavePort(w, airBox!);
                if (wave is null) return No(why!);
                ports[ports.FindIndex(q => q.Number == w.Port.Number)] = wave;
            }

            // ── Solids, in construction order (R-em3d3-1d): the builder's, and the air above the stack ──
            _g.BuildSolids(airBox, reserveAir: true);
            var solids = _g.SolidList;
            var sheets = _g.SheetList;
            if (_g.AirSlot is { } air)
            {
                // The builder numbered everything after the dielectrics as if this slot were taken, so the
                // air solid only has to be put into it.
                var box = airBox!;
                solids.Insert(air.Index, _g.Origin(new Em3dSolid(AirSolidName, air.Material, Em3dRole.Air,
                    new Em3dBox(new Point3(box.Min.X, box.Min.Y, air.Bottom), new Point3(box.Max.X, box.Max.Y, box.Max.Z)), air.Order),
                    Em3dObjectKind.Air, null));
            }

            // ── Terminals and ground (R-em3d22-2), by net ─────────────────────────────────────
            List<Em3dTerminal> terminals = [];
            List<string> groundObjects = [];
            if (setup.IsStatic3D)
            {
                if (ViaNetRefusal(setup, _g.ViaNetConflictMap) is { } viaNetRefusal) return No(viaNetRefusal);
                foreach (var a in _g.ViaNetAdoptionMap.Values)
                {
                    _notes.Add(a.Sentence);
                    _viaNetFixes[a.Sentence] = a.Fix;
                }
                if (Terminals(setup, solids, sheets, ports, pecFloor, _g.ObjectNetMap, _g.GroundBandObjectList,
                              out terminals, out groundObjects, _notes) is { } terminalRefusal)
                    return No(terminalRefusal);
                if (setup.Problem3D == Em3dProblemType.Magnetostatic)
                {
                    var driven = terminals.Select(t => t.SourcePort).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    int dropped = ports.RemoveAll(p => !driven.Contains(p.Name));
                    if (dropped > 0)
                        _notes.Add($"{dropped} port(s) drive no terminal and are not in the magnetostatic problem.");
                }
            }

            // ── Notes on what was resolved and how ──────────────────────────────────────────
            _g.FinishNotes();
            _notes.Add(pecFloor
                ? $"The air box's floor is '{ground!.Layer.Name}' as a PEC plane at {Fmt(floorZ * 1e6)} µm: it is " +
                  "the lowest ground-reference conductor, nothing is drawn on it and nothing is drawn below it." +
                  (zMinFace?.PaddingUm is not null ? " The AirBox ZMin padding does not apply to a floor on the plane." : "")
                : planeFloor is { } pf && _g.PlaneFloorBand is { } fb
                ? $"The air box's floor is the underside of '{fb.Layer.Name}' at {Fmt(pf * 1e6)} µm, as a PEC face: it is " +
                  (fb.Layer.IsGroundReference ? "a drawn ground plane "
                      : "the bottom-most conductor, treated as a floor because its metal covers at least " +
                        $"{Fmt(PlaneFloorCover * 100)} % of the footprint (the technology does not mark it ground-reference), ") +
                  (_g.PlaneFloorHoles == 0 ? "with no hole in it"
                      : $"whose {_g.PlaneFloorHoles} hole(s) have no other metal over them") +
                  ", under all the drawn metal, so nothing below it carries field. State the AirBox's ZMin to pad below it instead."
                : "The air box's floor is below the geometry" +
                  (floorStatedAway ? ", as this setup's AirBox states." :
                   _g.PlaneFloorBlockedAt is var (bx, by) && _g.PlaneFloorBand is { } blocked && ByHeightApplies(setup) && setup.AirBox?.ZMin is null
                   ? $": '{blocked.Layer.Name}' has a hole at ({Fmt(bx * 1e3)}, {Fmt(by * 1e3)}) mm with a via or other metal of another net " +
                     "in or over it, which a floor on the plane would short to ground, so the plane is geometry rather than a floor."
                   : ground is null ? ": the technology designates no ground-reference conductor, and no bottom conductor covers " +
                     $"{Fmt(PlaneFloorCover * 100)} % of the footprint."
                                  : $": '{ground.Layer.Name}' is drawn or has metal below it, so it is " +
                                    "geometry rather than a floor."));

            var problem = new Em3dProblem(solids, sheets, _g.Materials, ports, airBox!, frequency, _tempC)
            {
                Type = setup.Problem3D,
                Terminals = terminals,
                GroundObjects = groundObjects,
                EigenmodeCount = setup.Eigenmode?.Count ?? EmEigenmode3D.DefaultCount,
                EigenmodeTargetHz = setup.Eigenmode?.TargetGHz is { } target ? target * 1e9 : fMin,
                MeshFrequencyHz = setup.Problem3D == Em3dProblemType.Driven ? setup.MeshFrequencyGHz * 1e9 : null,
                PaddedContent = (new Point3(cx0, cy0, zLow), new Point3(cx1, cy1, zHigh)),
                PaddingHeightM = paddingH,
                PaddingHeightBelowM = paddingHBelow,
                ArtworkBarrels = ArtworkBarrels(solids, _g.OriginMap),
            };
            if (MeshFrequencyNote(problem, setup) is { } meshedAt) _notes.Add(meshedAt);
            // brief-dfr35-1 R-dfr35-1-7 — what is known to stop Gmsh, said before it runs; a picture of the board needs none of it.
            var geometry = displaySlabs ? [] : Em3dGeometryCheck.ForRun(problem, setup);
            _g.WarningList.AddRange(Em3dGeometryCheck.Sentences(geometry));
            return new Em3dGenerationResult(problem, null, _notes)
            {
                ViaNetFixes = _viaNetFixes,
                GeometryFindings = geometry,
                Warnings = _g.WarningList,
                Wires = _g.WireBuild?.Reports ?? [],
                Origins = _g.OriginMap,
                MaterialSources = _g.MaterialSources,
                NoAlpha = _g.NoAlphaList,
                UnknownTemperature = _g.UnknownTemperatureList,
                PortRefusal = _portRefusal,
                PortReturns = _portReturns,
                PortsLeftOut = _portsLeftOut,
                PortsMoved = _portsMoved,
                PadRefusal = _g.PadRefusal,
                WantsDc = _wantsDc,
            };
        }

        // ── Terminals (brief-em3d-22 R-em3d22-2) ─────────────────────────────────────────────

        // ── Ports ────────────────────────────────────────────────────────────────────────────

        /// <summary>The port labels this problem's ports come from — <see cref="EmComponentPorts.Collect"/>, once.</summary>
        private EmComponentPorts.Result PortLabels => _portLabels ??= EmComponentPorts.Collect(source);
        private EmComponentPorts.Result? _portLabels;

        /// <summary>
        /// The planar port extractor, run over this problem's own signal-conductor polygons — so the
        /// numbering (R-em3d3-2d), the side inference and every refusal (R-em3d3-2b) are its own —
        /// then each port turned into a vertical sheet from its conductor to its return.
        /// </summary>
        private string? BuildPorts(IReadOnlyList<PlanarExtractor.StackBand> bands,
                                   Dictionary<int, List<Piece>> pieces,
                                   PlanarExtractor.StackBand? floorPlane, double floorZ, List<Em3dPort> ports,
                                   List<WaveSpec> waves)
        {
            var signal = bands.Where(b => pieces.ContainsKey(b.Index) && !b.Layer.IsGroundReference).ToList();
            if (signal.Count == 0)
                return "This layout draws metal only on ground-reference conductors, so there is no signal " +
                       "conductor for a port to drive.";

            // EM-TFR — a port sits on metal: a film's piece is not a conductor a port can be resolved onto.
            var layers = signal.Select(b => new PlanarConductorLayer(
                b.Layer.Name, [.. pieces[b.Index].Where(p => !p.IsFilm).Select(p => p.Poly)], b.Layer.SigmaSm, b.TopM - b.BottomM)).ToList();
            var portProblem = new PlanarProblem(layers, new GroundedSlab(1, new EmMaterial(1, 0)), 0);

            // Every signal conductor is a level here, so a port over copper on another layer is resolved by
            // its own layer (the technology says which level that is), and no refusal offers analysis levels.
            // brief-dfr26-7 R-dfr26-7-3: the top level's port labels and every port a placed component declares.
            if (PortLabels.Refusal is { } dup) return dup;
            _notes.AddRange(PortLabels.Notes);
            var extracted = EmPortExtraction.Extract(PortLabels.Shapes, portProblem, source.DbuPerMicron,
                                                     setup.ResolvePortZ0, source.View.DisplayUnit,
                                                     technology: source.Technology, analysisLevelsApply: false,
                                                     padToPlane: true);
            _notes.AddRange(extracted.Notes);
            if (!extracted.Ok) return extracted.Refusal;

            for (int portIndex = 0; portIndex < extracted.Ports.Count; portIndex++)
            {
                // A translated port (portsEach) that refuses is left out ALONE — its sheet, return and notes rolled back —
                // and every other port is still built; a solve refuses on the first, as it always has.
                int portsBefore = ports.Count, wavesBefore = waves.Count, notesBefore = _notes.Count, movedBefore = _portsMoved.Count;
                if (OnePort(portIndex) is not { } refusal) continue;
                if (!portsEach) return refusal;
                ports.RemoveRange(portsBefore, ports.Count - portsBefore);
                waves.RemoveRange(wavesBefore, waves.Count - wavesBefore);
                _notes.RemoveRange(notesBefore, _notes.Count - notesBefore);
                _portsMoved.RemoveRange(movedBefore, _portsMoved.Count - movedBefore);
                _portReturns.Remove(extracted.Ports[portIndex].Number);
                _portsLeftOut.Add(new(extracted.Ports[portIndex].Number, refusal));
            }
            return null;

            string? OnePort(int portIndex)
            {
                var p = extracted.Ports[portIndex];
                var portLabel = portIndex < extracted.SourceLabels.Count ? extracted.SourceLabels[portIndex] : null;
                if (p.Kind == PlanarPortKind.Internal && !p.IsConductorReferenced)
                {
                    if (setup.PortKind3D(p.Number) == Em3dPortKind.Wave)
                        return $"The setup makes port {p.Number} a wave port, but it is a pad-to-plane port. A wave port " +
                               "lies on the air box's face at a line's end, so only an edge port can be one.";
                    if (PadToPlanePort(p, bands, signal, pieces, floorPlane, floorZ, out var padPort) is { } padRefusal)
                        return padRefusal;
                    ports.Add(padPort!);
                    return null;
                }
                if (p.Kind != PlanarPortKind.Edge || p.IsConductorReferenced)
                    return $"Port {p.Number} is {(p.IsConductorReferenced ? "referenced to a drawn conductor" : "an internal delta-gap port")}. " +
                           "A 3D setup builds two kinds of lumped port: an edge port, a vertical sheet from a conductor's " +
                           "end down (or up) to its return plane, and an internal port, a vertical sheet from a pad to the " +
                           "nearest plane with metal over or under it. A gap cut across a conductor is not built in this " +
                           "version. Make it an edge port at a conductor end or an internal port on a pad, or solve this " +
                           "layout with a planar setup.";

                var band  = signal[p.LayerIndex ?? 0];
                var piece = PieceAt(pieces[band.Index], p.Location.X, p.Location.Y, p.Side, out var edge);
                if (piece is null || edge is null)
                    return $"Port {p.Number} at ({Fmt(p.Location.X * 1e6)}, {Fmt(p.Location.Y * 1e6)}) µm is on " +
                           $"'{band.Layer.Name}' but no conductor end could be found under it.";
                var (edgeAt, lo, hi) = edge.Value;
                double faceAt = edgeAt, edgeLo = lo, edgeHi = hi;

                // brief-dfr28-4 R-dfr28-4-1 — the return is found by walking the artwork from the line's end, down
                // and up, with the pad-to-plane port's walk (ReturnWalk): the nearer side's first plane with metal
                // under the end, a ground-reference one preferred. A plane that stops within a tenth of the line's
                // width short of the end still counts, and the sheet moves in to where it begins.
                if (ReturnStated(p.Number, out var stated) is { } unknown) return unknown;
                bool wave = setup.PortKind3D(p.Number) == Em3dPortKind.Wave;
                // brief-dfr34-1 R-dfr34-1-6 — Return = Same layer: across the gap beside the line's end, not down or up.
                // A label placed for the planar kernel as coplanar means the same thing here when the setup says nothing, and
                // the walk below answers when no straight gap is there: the setup's own table never asked for this one.
                if (stated is null && !wave && portLabel?.PortReference == LayoutPortReference.CoplanarGround &&
                    CoplanarPort(p, bands, band, piece, edgeAt, lo, hi, pieces, ports) is null)
                    return null;
                if (stated is { PerPort: true, Layer: EmPort3D.SameLayerReturn })
                    return wave
                        ? $"Port {p.Number} is a wave port, and its Return states the same layer. A wave port's return is the " +
                          "air box's face; a coplanar return is a lumped port's. Make the port lumped, or choose Automatic."
                        : CoplanarPort(p, bands, band, piece, edgeAt, lo, hi, pieces, ports);
                bool sheetAlongX = p.Side is PlanarPortSide.MinY or PlanarPortSide.MaxY;
                double inward = p.Side is PlanarPortSide.MinX or PlanarPortSide.MinY ? 1 : -1;
                // brief-dfr32-2 R-dfr32-2-2 — the sheet's width along the edge (EdgePortSheet, the rule the layout editor
                // draws the port's bar from). The first pass walks the return over the whole edge to learn H; a bounded or
                // stated width walks it again over the sheet actually built, so the return is judged where the sheet is.
                double portAcross = sheetAlongX ? p.Location.X : p.Location.Y;
                double? statedWidth = !wave && portLabel?.PortWidth is > 0 and var statedDbu
                    ? statedDbu / (source.DbuPerMicron * 1e6) : null;
                // brief-port-width-planar-and-c3d R-pw1-0b — the port's "Use edge width" box means the same here as on the
                // planar run: checked (the default for a port stating no width), the sheet is the whole edge.
                bool useEdge = wave || portLabel is null || EdgePortSheet.UsesEdgeWidth(portLabel);
                var sheetSpan = new EdgePortSheetSpan(lo, hi, EdgePortSheetRule.Edge);
                // R-pw1-0b — a port narrower than its edge is walked over its own width from the first pass, H taken from
                // the stack until the walk measures it; walking the whole edge first would refuse it for metal beside it,
                // which is exactly what unticking Use edge width is for.
                if (!useEdge && portLabel is not null)
                {
                    double hStack = EdgePortSheet.HeightToReturnDbu(source.Technology, portLabel.PortLayer ?? portLabel.Layer,
                                                                    source.DbuPerMicron) is { } hd
                        ? hd / (source.DbuPerMicron * 1e6) : 0;
                    var pre = EdgePortSheet.Span(lo, hi, portAcross, hStack, statedWidth, useEdge: false);
                    if (pre.Rule != EdgePortSheetRule.Edge) (lo, hi) = (pre.Lo, pre.Hi);
                }
                ReturnSide? landed;
                Dictionary<int, (double At, double Gap)> moves;
                var openings = new Dictionary<int, (double Along, double Across, double Inward)>();
                double tolerance;
                for (int pass = 0; ; pass++)
                {
                    double mid = (lo + hi) / 2;
                    double probe = edgeAt + inward * 1e-6 * (hi - lo);
                    tolerance = ReturnGapToleranceOfWidth * (hi - lo);
                    moves = new Dictionary<int, (double At, double Gap)>();
                    openings.Clear();
                    ReturnCover EndCover(PlanarExtractor.StackBand b)
                    {
                        var on = pieces.TryGetValue(b.Index, out var ps) ? ps : [];
                        var (whole, touches) = Across(on, probe, sheetAlongX, lo, hi);
                        // Tested just inside the end and just past it, as before: a plane cut on a cropped board's
                        // edge ends exactly where the line does, and is found on the inside.
                        if (PieceContaining(on, p.Side, edgeAt, mid) is { } rp) return new(rp, touches, "metal", Whole: whole is not null);
                        // A plane drawn by hand to the line's end often stops a hair short of it — a vertex a few µm
                        // off makes its edge lean — and nothing on screen shows the difference. Within a tenth of the
                        // line's width the sheet moves in to where the plane begins; beyond it the refusal says how
                        // far short the plane is, which is what the designer has to fix.
                        double? gap = on.Count == 0 ? null : ReturnGap(on, p.Side, edgeAt, lo, hi);
                        // brief-dfr34-1 R-dfr34-1-4 — the plane's metal surrounds the line's end: before it along the line,
                        // past it, and on both sides across it, with none under the end itself. That is an opening — an
                        // anti-pad, a clearance under a pad — not a plane pulled back, so the walk passes through it to the
                        // next plane. A plane that stops within a tenth of the width is still the hand-drawn near miss below.
                        if (!touches && gap is { } og && og > tolerance && Opening(on, p.Side, edgeAt, lo, hi) is { } hole)
                        {
                            openings[b.Index] = hole;
                            // brief-dfr35-1 R-dfr35-1-4 — and how far along the line the metal starts: the edit that fixes it.
                            return new(null, false, $"an opening {Length(hole.Along)} × {Length(hole.Across)} round the line's end" +
                                                    MetalStartsAlongLine(piece, p.Side, edgeAt, inward, mid, hole.Inward));
                        }
                        double moved = edgeAt + inward * (gap ?? 0);
                        // Designer feedback round 34 — beyond the tenth, the sheet moves only along a line as wide as the
                        // sheet: a pad's flat edge over an opening in the plane slid 1.36 mm down onto a feed line an
                        // eighth of the pad's width, an 847 µm sheet across a 109 µm trace. The opening is not a
                        // pulled-back plane, so the port is refused there, as the solve refuses it.
                        double inside = moved + inward * 1e-3 * (hi - lo);
                        bool lineSpansSheet = gap is not { } g0 || g0 <= tolerance
                            || (MetalAt(piece.Poly, p.Side, inside, lo + 0.01 * (hi - lo))
                                && MetalAt(piece.Poly, p.Side, inside, hi - 0.01 * (hi - lo)));
                        if (gap is { } g && g <= (portsEach ? Math.Max(tolerance, TranslatedReturnGapOfWidth * (hi - lo)) : tolerance) && !wave &&
                            lineSpansSheet &&
                            PieceContaining(on, p.Side, moved, mid) is { } near &&
                            MetalAt(piece.Poly, p.Side, inside, mid))
                        {
                            moves[b.Index] = (moved, g);
                            return new(near, touches, "metal");
                        }
                        return new(null, touches,
                            gap is { } wide
                                ? $"its metal stops {Length(wide)} short of the port's end of the line" +
                                  (wave ? ", and a wave port's return has to reach the line's end on the air box's face"
                                        : $", beyond a tenth of the line's width, {Length(tolerance)}")
                                : touches ? "metal across part of the line's end"
                                : "no metal under the line's end" +
                                  (StartsInward(on, p.Side, edgeAt, inward, mid) is { } starts
                                      ? MetalStartsAlongLine(piece, p.Side, edgeAt, inward, mid, starts) : ""),
                            Short: gap is not null);
                    }
                    var walks = new[] { false, true }
                        .Select(up => ReturnWalk(bands, band, up, stated?.Layer, floorPlane, EndCover)).ToList();
                    landed = walks.Where(w => w.Band is not null)
                                  .MinBy(w => w.Above ? w.Band!.BottomM - band.TopM : band.BottomM - w.Band!.TopM);
                    if (landed is null)
                        return EndReturnRefusal(p.Number, band, stated, walks, useEdge && pass == 0);
                    if (pass > 0 || wave || useEdge) break;
                    double sigZ = piece.IsSheet ? piece.SheetZ : landed.Above ? piece.ZTop : piece.ZBottom;
                    double retZ = landed.Piece is not { } rp0 ? floorZ
                                : rp0.IsSheet ? rp0.SheetZ : landed.Above ? rp0.ZBottom : rp0.ZTop;
                    var span = EdgePortSheet.Span(edgeLo, edgeHi, portAcross, Math.Abs(retZ - sigZ), statedWidth, useEdge);
                    if (span.Rule == EdgePortSheetRule.Edge) break;
                    sheetSpan = span;
                    (lo, hi) = (span.Lo, span.Hi);
                }

                var retBand = landed.Band!;
                bool above = landed.Above;
                string negative;
                double retFace;
                if (landed.Piece is not { } rpc)
                {
                    negative = Em3dAirBox.FaceName("zmin");
                    retFace  = floorZ;
                }
                else
                {
                    negative = rpc.Name;
                    retFace  = rpc.IsSheet ? rpc.SheetZ : above ? rpc.ZBottom : rpc.ZTop;
                    if (moves.TryGetValue(retBand.Index, out var mv))
                    {
                        edgeAt = mv.At;
                        if (mv.Gap > tolerance)
                        {
                            string moved = $"Port {p.Number}'s sheet is {Length(mv.Gap)} inside its line's end, where " +
                                           $"'{retBand.Layer.Name}' begins: the plane is pulled back that far from the end, so " +
                                           "the port stands where its return does, and the line beyond it is part of the " +
                                           "structure. Move the port in the 3D editor, or extend the plane, to put it elsewhere.";
                            _notes.Add(moved);
                            _portsMoved.Add(moved);
                        }
                        else
                            _notes.Add($"Port {p.Number}'s sheet is {Length(mv.Gap)} inside its line's end, where " +
                                       $"'{retBand.Layer.Name}' begins: the plane stops that far short of the end, within " +
                                       $"a tenth of the line's width, {Length(tolerance)}. The port's reference plane " +
                                       "moves with it; extend the plane to the line's end to put the port there.");
                    }
                }

                double sigFace = piece.IsSheet ? piece.SheetZ : above ? piece.ZTop : piece.ZBottom;
                double zLo = above ? sigFace : retFace, zHi = above ? retFace : sigFace;
                if (!(zHi > zLo))
                    return $"Port {p.Number}'s return plane '{retBand.Layer.Name}' is not separated from " +
                           $"'{band.Layer.Name}' by any height, so the port's sheet would be empty.";

                // brief-dfr32-2 R-dfr32-2-2, R-pw1-3 — say why the sheet is narrower than its edge, and how to change it, in
                // the words the planar run uses for its cut (EdgePortSheet.Describe).
                if (EdgePortSheet.Describe(p.Number, "sheet", sheetSpan, edgeHi - edgeLo, Length) is { } why)
                    _notes.Add(why);

                // brief-dfr32-2 R-dfr32-2-3 — a sheet on a curved edge meets a straight face: the metal outside the sheet's
                // plane, across its width, is cut away. Not for a sheet moved in to where its return begins (the line runs
                // on past it by design) nor a wave port (its face is the air box's).
                if (!wave && edgeAt == faceAt &&
                    EdgePortSheet.FlushTo(piece.Poly, p.Side, edgeAt, lo, hi, out double cut, out double faceLo,
                                          out double faceHi) is { } flush)
                {
                    var onBand = pieces[band.Index];
                    int k = onBand.IndexOf(piece);
                    piece = piece with { Poly = flush };
                    if (k >= 0) onBand[k] = piece;
                    (lo, hi) = (faceLo, faceHi);   // the sheet ends where the cut face does, exactly
                    _notes.Add($"Port {p.Number} is on a curved edge of '{piece.Name}': the metal was cut flat across the " +
                               $"sheet's width ({Length(hi - lo)}), up to {Length(cut)} of it beyond the sheet's plane, so " +
                               "the sheet meets a straight face.");
                }

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
                var lumped = new Em3dPort(p.Number, $"port/{p.Number}", piece.Name, negative, min, max,
                                          new Point3(0, 0, above ? -1 : 1), p.Z0,
                                          // R-em3d3-2c — a Tier A port's reference plane IS its sheet.
                                          new Em3dReferencePlane(centre, normal, 0));
                ports.Add(lumped);
                _portReturns[p.Number] = new(retBand.Layer.Name, above, landed.Piece is null, DecidedBy(stated));
                _notes.Add($"Port {p.Number} at its line's end is {(wave ? "a wave port returning through" : $"a vertical lumped sheet {Length(hi - lo)} wide and {Length(zHi - zLo)} tall from '{band.Layer.Name}' {(above ? "up" : "down")} to")} " +
                           $"'{retBand.Layer.Name}'{(landed.Piece is null ? " (the air box's floor)" : "")}" +
                           (landed.Passed.Count > 0 ? $", passing through the opening in {string.Join(", ", landed.Passed.Select(q => $"'{q}'"))}" : "") +
                           $"{(wave ? $", {(above ? "above" : "below")} the line" : "")}: {(landed.Unmarked ? UnmarkedReason(retBand) : ReturnReason(stated))}.");
                // R-dfr34-1-4 — each opening the sheet passed through on its way, by layer and size.
                foreach (var ob in bands.Where(b => openings.ContainsKey(b.Index) &&
                                                    (above ? b.BottomM >= band.TopM - 1e-12 && b.TopM <= retBand.BottomM + 1e-12
                                                           : b.TopM <= band.BottomM + 1e-12 && b.BottomM >= retBand.TopM - 1e-12))
                                      .OrderBy(b => above ? b.BottomM : -b.TopM))           // in the walk's order, nearest first
                {
                    var hole = openings[ob.Index];
                    string through = $"Port {p.Number}'s return passes through an opening in '{ob.Layer.Name}' round its line's end, " +
                                     $"{Length(hole.Along)} along the line by {Length(hole.Across)} across it: that plane's metal surrounds " +
                                     $"the end, so it is a clearance rather than a plane pulled back, and the port returns through " +
                                     $"'{retBand.Layer.Name}' beyond it.";
                    _notes.Add(through);
                    _portsMoved.Add(through);
                }
                if (landed.Unmarked) _portsMoved.Add($"Port {p.Number} returns through '{retBand.Layer.Name}': {UnmarkedReason(retBand)}.");
                // brief-em3d-23 — a wave port starts as the lumped sheet (its centre line is the voltage path)
                // and becomes a region of the box face once the box exists.
                if (setup.PortKind3D(p.Number) == Em3dPortKind.Wave)
                    waves.Add(new WaveSpec(lumped, p.Side switch
                    {
                        PlanarPortSide.MinX => "xmin", PlanarPortSide.MaxX => "xmax",
                        PlanarPortSide.MinY => "ymin", _ => "ymax",
                    }, edgeAt, hi - lo, zHi - zLo, above));
                return null;
            }
        }

        /// <summary>
        /// <b>brief-dfr26-1 R-dfr26-1-2 — an internal (to-ground) port in 3D: a vertical lumped sheet from the pad at
        /// the label to the nearest ground-reference conductor, above or below, that has metal across the sheet's
        /// whole width.</b> A plane with an opening there is passed through — a cut-out under a test pad is the case
        /// this exists for — and the setup's named return plane, when it names one, is the only plane landed on, as
        /// for an edge port. The sheet's width is the pad's own chord across it, as an edge port's is its line's.
        ///
        /// <para>A via barrel at the label point (a test point's through via) cannot share the sheet's place, so the
        /// sheet stands tangent to the barrel on the side away from the metal that feeds the pad — and, where every
        /// plane's clearance round the barrel is wider than the barrel, slides out to where the plane first has metal
        /// across it, which is where the pad's return current actually reaches the plane. Metal of any other level
        /// across part of the sheet's path is a refusal naming that level: a lumped sheet runs through dielectric.</para>
        /// </summary>
        private string? PadToPlanePort(PlanarPort p, IReadOnlyList<PlanarExtractor.StackBand> bands,
                                       List<PlanarExtractor.StackBand> signal, Dictionary<int, List<Piece>> pieces,
                                       PlanarExtractor.StackBand? floorPlane, double floorZ, out Em3dPort? port)
        {
            port = null;
            var band = signal[p.LayerIndex ?? 0];
            double x0 = p.Location.X, y0 = p.Location.Y;
            string at = $"({Fmt(x0 * 1e6)}, {Fmt(y0 * 1e6)}) µm";
            var pad = pieces[band.Index].FirstOrDefault(q => q.Poly.Contains(x0, y0));
            if (pad is null)
                return $"Port {p.Number} at {at} is an internal port on '{band.Layer.Name}', but its label is not inside " +
                       "that conductor's metal. Move it onto the pad it drives.";

            const double zEps = 1e-12;
            var barrel = _g.Barrels.Where(v => (v.X - x0) * (v.X - x0) + (v.Y - y0) * (v.Y - y0) <= v.R * v.R &&
                                               v.Bottom.BottomM <= band.BottomM + zEps && v.Top.TopM >= band.TopM - zEps)
                                   .Select(v => ((double X, double Y, double R, PlanarExtractor.StackBand Top, PlanarExtractor.StackBand Bottom)?)v)
                                   .FirstOrDefault();

            // ── Which side is away from the feed ─────────────────────────────────────────────────
            // The metal that feeds the pad: its own piece (a trace merged into it), and every signal piece a barrel at
            // the label lands on. Probed beyond the pad's own size in the four axis directions; the sheet goes on the
            // least-fed side, preferring the side opposite the most-fed one.
            var feed = new List<PlanarPolygon> { pad.Poly };
            if (barrel is { } fb)
                foreach (var b in bands.Where(b => b.Index != band.Index && !b.Layer.IsGroundReference && pieces.ContainsKey(b.Index) &&
                                                   b.BottomM >= fb.Bottom.BottomM - zEps && b.TopM <= fb.Top.TopM + zEps))
                    feed.AddRange(pieces[b.Index].Where(q => q.Poly.Contains(fb.X, fb.Y)).Select(q => q.Poly));
            double padScale = 2 * pad.Poly.Area() / Math.Max(Perimeter(pad.Poly), 1e-30);
            (double Dx, double Dy, string Name)[] dirs = [(1, 0, "+x"), (0, 1, "+y"), (-1, 0, "−x"), (0, -1, "−y")];
            var score = new int[4];
            for (int k = 0; k < 4; k++)
                foreach (double m in new[] { 1.25, 1.5, 2, 3, 5 })
                    if (feed.Any(f => f.Contains(x0 + dirs[k].Dx * m * padScale, y0 + dirs[k].Dy * m * padScale))) score[k]++;
            int most = Array.IndexOf(score, score.Max());
            int least = score.Min();
            int away = score[(most + 2) % 4] == least && score[most] > least ? (most + 2) % 4 : Array.IndexOf(score, least);
            var dir = dirs[away];
            bool lineAlongX = dir.Dy != 0;                  // the sheet's line runs across the away direction
            double u0 = lineAlongX ? y0 : x0, v0 = lineAlongX ? x0 : y0;
            double sign = lineAlongX ? dir.Dy : dir.Dx;

            // ── Where along the away direction the sheet may stand ──────────────────────────────
            var offsets = new List<double>();
            if (barrel is { } sb)
            {
                double padFar = pad.Poly.Outer.Max(q => ((lineAlongX ? q.Y : q.X) - u0) * sign);
                double nudge = 1e-6 * Math.Max(padScale, sb.R);
                offsets.Add(sb.R + nudge);
                var vertices = new SortedSet<double>();
                foreach (var b in bands.Where(b => pieces.ContainsKey(b.Index)))
                    foreach (var q in pieces[b.Index])
                        foreach (var ring in q.Poly.HoleRings.Prepend(q.Poly.Outer))
                            foreach (var pt in ring)
                            {
                                double s = ((lineAlongX ? pt.Y : pt.X) - u0) * sign;
                                if (s > sb.R + nudge && s < padFar && Math.Abs((lineAlongX ? pt.X : pt.Y) - v0) <= 2 * padScale * 5)
                                    vertices.Add(s + nudge);
                            }
                offsets.AddRange(vertices.Take(4096));
            }
            else offsets.Add(0);

            if (ReturnStated(p.Number, out var stated) is { } unknown) return unknown;
            string? named = stated?.Layer;
            if (named == EmPort3D.SameLayerReturn)
                return $"Port {p.Number} at {at} is a pad-to-plane port, and its Return states the same layer. A coplanar return " +
                       "is built at a line's end, across the gap to the ground beside it; a pad-to-plane port returns through a " +
                       "plane above or below its pad. Choose a layer, or Automatic.";
            bool anyFlagged = AnyGroundReference(bands);

            string? firstBlocked = null;
            List<string>? firstPassed = null;
            foreach (double s in offsets)
            {
                double c = u0 + sign * s;
                if (Chord(pad.Poly, c, lineAlongX, v0) is not var (lo, hi)) break;

                var found = new List<(PlanarExtractor.StackBand Band, Piece? Piece, bool Above, List<string> Passed, bool Unmarked)>();
                var passedHere = new List<string>();
                string? blockedHere = null;
                foreach (bool above in new[] { true, false })
                {
                    var walk = ReturnWalk(bands, band, above, named, floorPlane, b =>
                    {
                        var (cover, touches) = Across(pieces.TryGetValue(b.Index, out var ps) ? ps : [], c, lineAlongX, lo, hi);
                        return new(cover, touches, "", Whole: cover is not null);
                    });
                    if (walk.Band is { } wb) found.Add((wb, walk.Piece, above, walk.Passed, walk.Unmarked));
                    else
                    {
                        if (walk.Blocker is { } bb)
                            blockedHere ??=
                                $"Port {p.Number}'s pad-to-plane sheet at {at}, from '{band.Layer.Name}' {(above ? "up" : "down")} toward " +
                                $"{(named is null ? anyFlagged ? "a ground-reference plane" : "the nearest metal" : $"its named return plane '{named}'")}, would cross metal " +
                                $"on '{bb.Layer.Name}'{(walk.BlockerPartial ? ", which covers only part of it" : "")}. " +
                                "A lumped port's sheet runs through dielectric from the pad to the plane it returns through. " +
                                $"Move the label, clear that metal from under the pad, or {StateReturn}.";
                        passedHere.AddRange(walk.Passed);
                    }
                }
                if (found.Count == 0)
                {
                    firstBlocked ??= blockedHere;
                    firstPassed ??= passedHere;
                    continue;
                }

                var (retBand, retPiece, retAbove, passed, unmarked) = found.MinBy(f => f.Above ? f.Band.BottomM - band.TopM : band.BottomM - f.Band.TopM);
                bool isFloor = retPiece is null;
                double sigFace = pad.IsSheet ? pad.SheetZ : retAbove ? pad.ZTop : pad.ZBottom;
                double retFace = isFloor ? floorZ : retPiece!.IsSheet ? retPiece.SheetZ : retAbove ? retPiece.ZBottom : retPiece.ZTop;
                double zLo = Math.Min(sigFace, retFace), zHi = Math.Max(sigFace, retFace);
                if (!(zHi > zLo))
                    return $"Port {p.Number}'s return plane '{retBand.Layer.Name}' is not separated from " +
                           $"'{band.Layer.Name}' by any height, so the port's sheet would be empty.";

                var min = lineAlongX ? new Point3(lo, c, zLo) : new Point3(c, lo, zLo);
                var max = lineAlongX ? new Point3(hi, c, zHi) : new Point3(c, hi, zHi);
                var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
                port = new Em3dPort(p.Number, $"port/{p.Number}", pad.Name,
                                    isFloor ? Em3dAirBox.FaceName("zmin") : retPiece!.Name, min, max,
                                    new Point3(0, 0, retAbove ? -1 : 1), p.Z0,
                                    new Em3dReferencePlane(centre, new Point3(dir.Dx, dir.Dy, 0), 0));
                _portReturns[p.Number] = new(retBand.Layer.Name, retAbove, isFloor, DecidedBy(stated));
                _notes.Add($"Port {p.Number} at {at} is a pad-to-plane port: a vertical lumped sheet {Length(hi - lo)} wide " +
                           $"and {Length(zHi - zLo)} tall from '{band.Layer.Name}' {(retAbove ? "up" : "down")} to " +
                           $"'{retBand.Layer.Name}'{(isFloor ? " (the air box's floor)" : "")}" +
                           (passed.Count > 0 ? $", passing through the opening in {string.Join(", ", passed.Select(q => $"'{q}'"))}" : "") +
                           (stated is not null ? $", {ReturnReason(stated)}" : unmarked ? $", {UnmarkedReason(retBand)}" : "") +
                           ". Its width is the pad's own across the sheet." +
                           (barrel is { } nb
                               ? $" A via barrel stands at the label, so the sheet stands {Length(s)} from the barrel's axis on its " +
                                 $"{dir.Name} side, away from the metal that feeds the pad" +
                                 (s > nb.R * (1 + 1e-3)
                                     ? $" — not tangent to the barrel ({Length(nb.R)} radius), because no plane has metal across the " +
                                       "sheet nearer it: that is where the plane's clearance round the via ends."
                                     : ", tangent to it.")
                               : " It is centred on the label."));
                return null;
            }

            return firstBlocked
                ?? $"Port {p.Number} at {at} is a pad-to-plane port on '{band.Layer.Name}', and no " +
                   (named is null ? anyFlagged ? "ground-reference conductor" : "metal" : $"metal of its named return plane '{named}'") +
                   " lies across its sheet" + (barrel is null ? "" : " anywhere beside the via barrel at its label") +
                   (firstPassed is { Count: > 0 }
                       ? $": {string.Join(", ", firstPassed.Distinct().Select(q => $"'{q}'"))} {(firstPassed.Distinct().Count() == 1 ? "has" : "each have")} an opening there. "
                       : named is null && anyFlagged ? ": the technology marks none above or below it. " : ". ") +
                   "Mark the plane the pad returns through as a ground reference in the technology's stackup, or draw " +
                   "its metal under the pad.";
        }

        // ── A port's return (brief-dfr28-4) ──────────────────────────────────────────────────

        /// <summary>What one conductor level has across a port's sheet: the piece the sheet lands on, whether any of
        /// its metal crosses the sheet's path, and what it had there, in words, for a refusal that lists them.</summary>
        /// <para><paramref name="Short"/>: the plane's metal is under the line but stops short of its end, beyond what
        /// moving the sheet closes — a plane meant to reach the end, so the walk stops there rather than passing through
        /// it to a farther one as if it were an opening.</para>
        /// <para><paramref name="Whole"/>: <paramref name="Cover"/>'s metal lies across the sheet's whole width, not only
        /// under its middle — what lets a level the technology does not mark be the return (R-dfr34-1-5).</para>
        private readonly record struct ReturnCover(Piece? Cover, bool Touches, string Had, bool Short = false, bool Whole = false);

        /// <summary>One side of a port's walk: the level landed on (its piece, or null for the air box's floor), or
        /// the level whose metal blocks the way, the ground-reference levels passed through at an opening, and every
        /// level looked at with what it had. <paramref name="Unmarked"/>: the level landed on is not marked as a ground
        /// reference, and was taken because its metal covers the whole sheet before any marked one does.</summary>
        private sealed record ReturnSide(bool Above, PlanarExtractor.StackBand? Band, Piece? Piece, List<string> Passed,
                                         PlanarExtractor.StackBand? Blocker, bool BlockerPartial, List<string> Looked,
                                         string? ShortOf = null, bool Unmarked = false);

        /// <summary>A return the setup states rather than one the walk finds: the layer, and whether the port's own
        /// Return states it (R-dfr28-4-5) or the setup's legacy return plane names it (R-dfr28-4-3, D2).</summary>
        private sealed record StatedReturn(string Layer, bool PerPort, int Port);

        private static bool AnyGroundReference(IReadOnlyList<PlanarExtractor.StackBand> bands)
            => bands.Any(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference);

        /// <summary>
        /// The return port <paramref name="port"/> states: its own Return in the port table, else the setup's
        /// legacy return plane, which a 3D setup no longer shows or writes but still honours when a <c>.cem</c>
        /// carries it, so a result does not move silently. Null for Automatic. Returns a refusal for a layer the
        /// technology does not have.
        /// </summary>
        private string? ReturnStated(int port, out StatedReturn? stated)
        {
            var s = setup.PortReturn3D(port) is { } own ? new StatedReturn(own, true, port)
                  : setup.GroundStackupLayerName is { Length: > 0 } named ? new StatedReturn(named, false, port)
                  : null;
            stated = s;
            if (s is null || (s.PerPort && s.Layer == EmPort3D.SameLayerReturn) ||
                tech.Stackup.Layers.Any(l => l.Kind == StackupKind.Conductor && l.Name == s.Layer))
                return null;
            return s.PerPort
                ? $"Port {port}'s Return states '{s.Layer}', but technology '{tech.Name}' has no conductor stackup " +
                  "layer with that name."
                : $"This EM setup names '{s.Layer}' as its return plane, but technology '{tech.Name}' " +
                  "has no conductor stackup layer with that name.";
        }

        private static string DecidedBy(StatedReturn? stated)
            => stated is null ? "walked" : stated.PerPort ? "port" : "setup";

        /// <summary>brief-dfr34-1 R-dfr34-1-5 — why a port returns through a level the technology does not mark.</summary>
        private static string UnmarkedReason(PlanarExtractor.StackBand b)
            => $"'{b.Layer.Name}' is not marked as a ground reference, but its metal covers the whole sheet before any marked " +
               "plane does, so the current returns through it — mark it in the technology's stackup, or state the port's return, " +
               "to say so";

        /// <summary>brief-dfr34-1 R-dfr34-1-3 — where the user states a port's return: a <c>.cem</c>'s port table; a layout's
        /// ports being brought into a <c>.c3d</c> have no Return — a 3D port returns through what its (−) end touches, so it is
        /// moved (Move… on the port) to change it.</summary>
        private string StateReturn => portsEach
            ? "move the port in the 3D editor (Move… on the port) to the metal it should return through"
            : "state the port's return in the Return column of the .cem setup's 3D port table (a 3D view's port is moved in the 3D editor instead)";

        /// <summary>Why a port returns where it does, for its note.</summary>
        private string ReturnReason(StatedReturn? stated) => stated switch
        {
            { PerPort: true } s => $"as port {s.Port}'s Return states",
            not null            => "as this setup names",
            _ when AnyGroundReference(_g.Bands) => "the nearest ground-reference plane with metal under the port",
            _ => "the technology marks no conductor as a ground reference, so the return is the nearest metal under the port",
        };

        /// <summary>
        /// <b>brief-dfr28-4 R-dfr28-4-1 — one side of a port's walk to its return, shared by the line-end and the
        /// pad-to-plane port.</b> The conductors on that side in order of distance, until one lands the sheet or
        /// blocks it: the stated layer when there is one; else a ground-reference level; else — a technology that
        /// flags none (D1) — any metal. The PEC floor counts as the plane it stands for. A level whose metal crosses
        /// the sheet's path without landing it blocks the way: a lumped sheet runs through dielectric.
        /// <paramref name="across"/> reads one level's metal across the sheet, which is where the two port kinds differ.
        /// </summary>
        private static ReturnSide ReturnWalk(IReadOnlyList<PlanarExtractor.StackBand> bands, PlanarExtractor.StackBand band,
                                             bool above, string? stated, PlanarExtractor.StackBand? floorPlane,
                                             Func<PlanarExtractor.StackBand, ReturnCover> across)
        {
            const double zEps = 1e-12;
            bool anyFlagged = AnyGroundReference(bands);
            var passed = new List<string>();
            var looked = new List<string>();
            var order = bands.Where(b => b.Layer.Kind == StackupKind.Conductor && b.Index != band.Index &&
                                         (above ? b.BottomM >= band.TopM - zEps : b.TopM <= band.BottomM + zEps))
                             .OrderBy(b => above ? b.BottomM : -b.TopM);
            foreach (var b in order)
            {
                bool target = stated is not null ? b.Layer.Name == stated : !anyFlagged || b.Layer.IsGroundReference;
                if (floorPlane is not null && ReferenceEquals(b.Layer, floorPlane.Layer) && target)
                    return new(above, b, null, passed, null, false, looked);
                var cover = across(b);
                if (target && cover.Cover is not null) return new(above, b, cover.Cover, passed, null, false, looked);
                if (target && cover.Short) return new(above, null, null, passed, null, false, looked, $"'{b.Layer.Name}': {cover.Had}");
                // brief-dfr34-1 R-dfr34-1-5 — a level the technology does not mark, whose metal covers the whole sheet before
                // any marked plane is reached, is where the current returns: the sheet could not pass it anyway. Taken with a
                // note rather than refused. A stated return still refuses it — the user asked for the plane beyond.
                if (!target && stated is null && anyFlagged && cover.Cover is not null && cover.Whole)
                    return new(above, b, cover.Cover, passed, null, false, looked, Unmarked: true);
                if (cover.Touches) return new(above, null, null, passed, b, target, looked);
                looked.Add($"'{b.Layer.Name}' ({cover.Had})");
                if (b.Layer.IsGroundReference) passed.Add(b.Layer.Name);
                if (stated is not null && target) break;   // the stated layer, with no metal there
            }
            return new(above, null, null, passed, null, false, looked);
        }

        /// <summary>Why a line-end port found no return: the level that blocks it, or every level looked at on
        /// each side and what it had under the line's end.</summary>
        private string EndReturnRefusal(int port, PlanarExtractor.StackBand band, StatedReturn? stated, List<ReturnSide> walks,
                                        bool wholeEdge = false)
        {
            // R-pw1-0b — a sheet across the whole edge is what most often reaches metal the port was never near (a via on
            // a pour's edge), and the checkbox is the fix the user has in hand.
            string edge = wholeEdge
                ? " The port uses its whole edge (Use edge width, in its Properties): uncheck it for a sheet 2 × H wide " +
                  "centred on the port, or a Width you state."
                : "";
            string toward = stated is not null ? $"'{stated.Layer}' ({ReturnReason(stated)})"
                          : AnyGroundReference(_g.Bands) ? "a ground-reference plane" : "the nearest metal";
            if (walks.FirstOrDefault(w => w.Blocker is not null) is { Blocker: { } b } blocked)
                return $"Port {port}'s sheet at its line's end, from '{band.Layer.Name}' {(blocked.Above ? "up" : "down")} toward " +
                       $"{toward}, would cross metal on '{b.Layer.Name}'{(blocked.BlockerPartial ? ", which covers only part of it" : "")}. " +
                       "A lumped port's sheet runs through dielectric from the line to the plane it returns through. " +
                       $"Clear that metal from under the line's end, or {StateReturn}." + edge;
            if (walks.FirstOrDefault(w => w.ShortOf is not null) is { ShortOf: { } shortOf } stopped)
                return $"Port {port} returns through {shortOf.Split(':')[0]}, {(stopped.Above ? "above" : "below")} '{band.Layer.Name}', " +
                       $"but {shortOf[(shortOf.IndexOf(':') + 2)..]}, so the port's sheet has nothing to end on. Extend the " +
                       "plane to the line's end" +
                       (stated is null ? $", or {StateReturn}." : ".") + edge;
            string Side(ReturnSide w) => $"{(w.Above ? "above" : "below")} it, " +
                                         (w.Looked.Count == 0 ? "no conductor" : string.Join(", ", w.Looked));
            if (stated is not null && stated.Layer == band.Layer.Name)
                return $"Port {port} is on '{band.Layer.Name}', and its return is stated as that same layer " +
                       $"({ReturnReason(stated)}). State a plane above or below the line.";
            return $"Port {port} on '{band.Layer.Name}' finds no {(stated is not null ? $"metal of {toward}" : toward)} under its " +
                   $"line's end to return through, and it is not the air box's floor — {string.Join("; ", walks.Select(Side))}. " +
                   "So the port's sheet has nothing to end on. Draw the plane under the port's end, or shorten the line so its end sits over metal" +
                   (stated is not null ? $", or {StateReturn.Replace("the port's return", "a different return")}." : ".");
        }

        /// <summary>The interval of <paramref name="poly"/>'s metal on the line (the second coordinate = <paramref name="c"/>,
        /// x first when <paramref name="alongX"/>) that contains <paramref name="v"/>, or null.</summary>
        private static (double Lo, double Hi)? Chord(PlanarPolygon poly, double c, bool alongX, double v)
        {
            var hits = Crossings(poly, c, alongX);
            hits.Sort();
            for (int i = 0; i + 1 < hits.Count; i += 2)
                if (v >= hits[i] && v <= hits[i + 1]) return (hits[i], hits[i + 1]);
            return null;
        }

        /// <summary>Whether a level's pieces cover the segment [lo, hi] of the line whole (the covering piece), and whether
        /// they touch it at all.</summary>
        private static (Piece? Cover, bool Touches) Across(IEnumerable<Piece> on, double c, bool alongX, double lo, double hi)
        {
            double tol = 1e-9 * (hi - lo);
            double mid = (lo + hi) / 2;
            bool touches = false;
            foreach (var q in on)
            {
                bool crossesInside = Crossings(q.Poly, c, alongX).Any(h => h > lo + tol && h < hi - tol);
                bool midIn = alongX ? q.Poly.Contains(mid, c) : q.Poly.Contains(c, mid);
                if (!crossesInside && midIn) return (q, true);
                touches |= crossesInside || midIn;
            }
            return (null, touches);
        }

        private static double Perimeter(PlanarPolygon poly)
        {
            double s = 0;
            foreach (var ring in poly.HoleRings.Prepend(poly.Outer))
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    s += Math.Sqrt((ring[i].X - ring[j].X) * (ring[i].X - ring[j].X) + (ring[i].Y - ring[j].Y) * (ring[i].Y - ring[j].Y));
            return s;
        }

        /// <summary>brief-em3d-23 — a wave port before the air box exists: the lumped sheet it grows from,
        /// the box face its line ends on, where the line ends, and the line's width and height above its
        /// return.</summary>
        private sealed record WaveSpec(Em3dPort Port, string Face, double EdgeAt, double WidthM, double HeightM, bool ReturnAbove);

        /// <summary>The wave port's region on its face, clipped to the face; or why it cannot be one.</summary>
        private (Em3dPort? Port, string? Refusal) WavePort(WaveSpec w, Em3dAirBox box)
        {
            var stated = setup.Ports3D.LastOrDefault(q => q.Port == w.Port.Number);
            double wf = stated?.WidthFactor ?? DefaultWaveWidthFactor * Math.Max(1, w.HeightM / w.WidthM);
            double hf = stated?.HeightFactor ?? DefaultWaveHeightFactor;
            if (!(wf > 1) || !(hf > 1))
                return (null, $"Port {w.Port.Number}'s wave-port region must be wider than its line and taller than the line's " +
                              $"height (Width and Height above 1); the setup states {Fmt(wf)} × {Fmt(hf)}.");
            double offset = (stated?.OffsetUm ?? 0) * 1e-6;
            if (!(offset >= 0))
                return (null, $"Port {w.Port.Number}'s wave-port offset is {Fmt(offset * 1e6)} µm; it is a distance into the " +
                              "structure, so it is zero or positive.");

            var p = w.Port;
            bool alongX = w.Face is "xmin" or "xmax";
            double at = w.Face switch { "xmin" => box.Min.X, "xmax" => box.Max.X, "ymin" => box.Min.Y, _ => box.Max.Y };
            double across = alongX ? (p.Min.Y + p.Max.Y) / 2 : (p.Min.X + p.Max.X) / 2;
            double half = wf * w.WidthM / 2;
            double lo = Math.Max(across - half, alongX ? box.Min.Y : box.Min.X);
            double hi = Math.Min(across + half, alongX ? box.Max.Y : box.Max.X);
            // The line's return face is the sheet's low end when the return is below, its high end above.
            double retZ = w.ReturnAbove ? p.Max.Z : p.Min.Z, sigZ = w.ReturnAbove ? p.Min.Z : p.Max.Z;
            double z0 = w.ReturnAbove ? Math.Max(retZ - hf * w.HeightM, box.Min.Z) : retZ;
            double z1 = w.ReturnAbove ? retZ : Math.Min(retZ + hf * w.HeightM, box.Max.Z);
            if (lo > across - half + 1e-12 || hi < across + half - 1e-12 ||
                (w.ReturnAbove ? z0 > retZ - hf * w.HeightM + 1e-12 : z1 < retZ + hf * w.HeightM - 1e-12))
                _notes.Add($"Port {p.Number}'s wave-port region is clipped to the air box's {w.Face} face; the box is " +
                           "smaller than the sizing rule asks for there.");
            var min = alongX ? new Point3(at, lo, z0) : new Point3(lo, at, z0);
            var max = alongX ? new Point3(at, hi, z1) : new Point3(hi, at, z1);
            var from = alongX ? new Point3(at, across, retZ) : new Point3(across, at, retZ);
            var to   = alongX ? new Point3(at, across, sigZ) : new Point3(across, at, sigZ);
            var n = p.ReferencePlane.Normal;
            var origin = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
            _notes.Add($"Port {p.Number} is a wave port on the air box's {w.Face} face: {Fmt((hi - lo) * 1e6)} × " +
                       $"{Fmt((z1 - z0) * 1e6)} µm ({Fmt(wf)} line widths × {Fmt(hf)} heights" +
                       $"{(stated?.WidthFactor is null && stated?.HeightFactor is null ? ", the sizing rule's" : "")}), its reference " +
                       $"plane {(offset > 0 ? $"{Fmt(offset * 1e6)} µm into the structure" : "on the face")}.");
            return (p with
            {
                Min = min, Max = max,
                ReferencePlane = new Em3dReferencePlane(origin, n, offset),
                Kind = Em3dPortKind.Wave,
                VoltagePath = new Em3dSegment(from, to),
            }, null);
        }

        /// <summary>The piece whose metal the port sits on, and the end it names: the edge's
        /// coordinate along the port's normal axis, and the metal's extent across it there.</summary>
        private static Piece? PieceAt(List<Piece> pieces, double x, double y, PlanarPortSide side,
                                      out (double At, double Lo, double Hi)? edge)
        {
            edge = null;
            Piece? best = null;
            double bestD = double.PositiveInfinity;
            bool alongX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            foreach (var p in pieces)
            {
                // A label placed ON the line's long edge (snapped to its corner) casts a ray along that edge, which the
                // half-open crossing rule does not count as crossing it — and the nearest end found was then another
                // piece's, millimetres away. So the ray is also cast a hair either side of the label.
                var (x0, y0, x1, y1) = p.Poly.Bounds();
                double nudge = 1e-6 * Math.Max(x1 - x0, y1 - y0);
                foreach (double dv in new[] { 0, nudge, -nudge })
                {
                    var e = EdgeOf(p.Poly, alongX ? x : x + dv, alongX ? y + dv : y, side);
                    if (e is null) continue;
                    double d = Math.Abs(e.Value.At - (alongX ? x : y));
                    if (d < bestD) { bestD = d; best = p; edge = e; }
                }
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
        /// How far in from the line's end, at <paramref name="at"/>, the return metal begins under the whole
        /// of the line's width [<paramref name="lo"/>, <paramref name="hi"/>] — the nearest piece's, read along
        /// both edges and the middle of the line. Null when no piece has metal under the line inside its end.
        /// </summary>
        private static double? ReturnGap(List<Piece> pieces, PlanarPortSide side, double at, double lo, double hi)
        {
            bool alongX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            double inward = side is PlanarPortSide.MinX or PlanarPortSide.MinY ? 1 : -1;
            double inset = 1e-3 * (hi - lo);
            double? best = null;
            foreach (var p in pieces)
            {
                var (x0, y0, x1, y1) = p.Poly.Bounds();
                double eps = 1e-6 * Math.Max(x1 - x0, y1 - y0);
                double worst = 0;
                foreach (double v in new[] { lo + inset, (lo + hi) / 2, hi - inset })
                {
                    // The first boundary crossing inside the end past which the line is over metal.
                    double? entry = null;
                    foreach (double s in Crossings(p.Poly, v, alongX).Select(c => (c - at) * inward).Where(s => s >= 0).Order())
                        if (MetalAt(p.Poly, side, at + inward * (s + eps), v))
                        {
                            entry = s;
                            break;
                        }
                    if (entry is null) { worst = double.PositiveInfinity; break; }
                    worst = Math.Max(worst, entry.Value);
                }
                if (!double.IsPositiveInfinity(worst) && (best is null || worst < best)) best = worst;
            }
            return best;
        }

        /// <summary>
        /// brief-dfr34-1 R-dfr34-1-4 — the opening round a line's end on one level, or null: the level has no metal at the
        /// end (just inside it or just past it, across the middle of [<paramref name="lo"/>, <paramref name="hi"/>]), and
        /// its metal resumes in all four directions — along the line inward and outward, and across it on both sides. Its
        /// size is the hole's extent along the line through the end's middle, and across it just inside the end.
        /// </summary>
        private static (double Along, double Across, double Inward)? Opening(List<Piece> pieces, PlanarPortSide side, double at, double lo, double hi)
        {
            if (pieces.Count == 0) return null;
            bool normalX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            double inward = side is PlanarPortSide.MinX or PlanarPortSide.MinY ? 1 : -1;
            double mid = (lo + hi) / 2, eps = 1e-6 * (hi - lo);
            bool Metal(double along, double across) => pieces.Any(q => MetalAt(q.Poly, side, along, across));
            if (Metal(at + inward * eps, mid) || Metal(at - inward * eps, mid)) return null;

            // Along the line through the end's middle: where the metal resumes, each way.
            var along = pieces.SelectMany(q => Crossings(q.Poly, mid, normalX)).ToList();
            double? Resume(double dir) => along.Select(c => (c - at) * dir).Where(s => s > 0).Order()
                                               .Cast<double?>().FirstOrDefault(s => Metal(at + dir * (s!.Value + eps), mid));
            if (Resume(inward) is not { } sIn || Resume(-inward) is not { } sOut) return null;

            // Across the line just inside the end: where the metal resumes on each side.
            double u = at + inward * eps;
            var across = pieces.SelectMany(q => Crossings(q.Poly, u, !normalX)).ToList();
            double? Side(double dir) => across.Select(c => (c - mid) * dir).Where(s => s > 0).Order()
                                              .Cast<double?>().FirstOrDefault(s => Metal(u, mid + dir * (s!.Value + eps)));
            if (Side(1) is not { } aHi || Side(-1) is not { } aLo) return null;
            return (sIn + sOut, aHi + aLo, sIn);
        }

        /// <summary>brief-dfr35-1 R-dfr35-1-4 — how far along the line from its end, over the end's middle, a level's metal
        /// starts; null when it does not.</summary>
        private static double? StartsInward(List<Piece> pieces, PlanarPortSide side, double at, double inward, double mid)
        {
            bool normalX = side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            double eps = 1e-9;
            return pieces.SelectMany(q => Crossings(q.Poly, mid, normalX)).Select(c => (c - at) * inward).Where(d => d > 0).Order()
                         .Cast<double?>().FirstOrDefault(d => pieces.Any(q => MetalAt(q.Poly, side, at + inward * (d!.Value + eps), mid)));
        }

        /// <summary>brief-dfr35-1 R-dfr35-1-4 — the distance in words, when the line itself reaches that far: only then is
        /// shortening it the edit that fixes the port.</summary>
        private string MetalStartsAlongLine(Piece line, PlanarPortSide side, double at, double inward, double mid, double distance)
            => MetalAt(line.Poly, side, at + inward * (distance + 1e-9), mid) ? $", where the plane starts {Length(distance)} along the line" : "";

        /// <summary>
        /// <b>brief-dfr34-1 R-dfr34-1-6 — a coplanar return (Return = Same layer): one horizontal lumped sheet across the
        /// gap from the line's end to the nearer same-layer ground beside it</b> (owner decision D1: one sheet, to the
        /// nearer side). Ground is metal on the line's own level whose net is a ground-reference plane's — or, where no
        /// net says, any metal on another net than the line's. The sheet is the line's width long, from its end inward,
        /// at the copper's mid-height, and the gap must be straight along it: a line edge and a ground edge parallel over
        /// the sheet's length.
        /// </summary>
        private string? CoplanarPort(PlanarPort p, IReadOnlyList<PlanarExtractor.StackBand> bands, PlanarExtractor.StackBand band,
                                     Piece piece, double edgeAt, double lo, double hi, Dictionary<int, List<Piece>> pieces,
                                     List<Em3dPort> ports)
        {
            bool normalX = p.Side is PlanarPortSide.MinX or PlanarPortSide.MaxX;
            double inward = p.Side is PlanarPortSide.MinX or PlanarPortSide.MinY ? 1 : -1;
            double w = hi - lo, eps = 1e-6 * w, tol = 1e-3 * w;
            var groundNets = bands.Where(b => b.Layer.IsGroundReference && pieces.ContainsKey(b.Index))
                                  .SelectMany(b => pieces[b.Index]).Select(q => q.Net).OfType<string>().ToHashSet(StringComparer.Ordinal);
            bool IsGround(Piece q) => !ReferenceEquals(q, piece) && (piece.Net is null || q.Net != piece.Net) &&
                                      (groundNets.Count == 0 || q.Net is null || groundNets.Contains(q.Net));
            var others = pieces[band.Index].Where(q => !ReferenceEquals(q, piece)).ToList();

            // At three points along the sheet's length: the line's own edge on each side, and the nearest metal past it.
            (double LineEdge, double GroundEdge, Piece? Ground, string? Why)? Gap(double dir, double shift = 0)
            {
                (double, double, Piece?, string?)? first = null;
                foreach (double a in new[] { edgeAt + inward * (shift + 0.02 * w), edgeAt + inward * (shift + 0.5 * w), edgeAt + inward * (shift + 0.98 * w) })
                {
                    if (Chord(piece.Poly, a, !normalX, (lo + hi) / 2) is not var (cLo, cHi))
                        return (0, 0, null, $"the line is shorter than the sheet's {Length(w)} length from its end");
                    double edge = dir > 0 ? cHi : cLo;
                    (double At, Piece Q)? near = null;
                    foreach (var q in others)
                        foreach (double c in Crossings(q.Poly, a, !normalX))
                            if ((c - edge) * dir > 0 && MetalAt(q.Poly, p.Side, a, c + dir * eps) &&
                                (near is null || (c - edge) * dir < (near.Value.At - edge) * dir))
                                near = (c, q);
                    if (near is not { } n) return (edge, 0, null, "no metal on the line's layer beside it");
                    if (!IsGround(n.Q)) return (edge, n.At, null, $"the nearest metal beside it is '{n.Q.Name}', which is not ground");
                    if (first is { } f && (Math.Abs(f.Item1 - edge) > tol || Math.Abs(f.Item2 - n.At) > tol || !ReferenceEquals(f.Item3, n.Q)))
                        return (edge, n.At, null, "the gap is not straight over the sheet's length from the line's end" +
                                                  (shift == 0 && StraightFrom(dir) is { } from ? $"; it is straight from {Length(from)} along the line" : ""));
                    first ??= (edge, n.At, n.Q, null);
                }
                return first;
            }
            // brief-dfr35-1 R-dfr35-1-4 — how far along the line a sheet this long would find a straight gap to ground: the
            // edit that fixes the port. Stepped in quarters of the sheet, up to where the line or its ground ends.
            double? StraightFrom(double dir)
            {
                for (int k = 1; k <= 400; k++)
                {
                    var g = Gap(dir, k * w / 4);
                    if (g is { Ground: not null }) return k * w / 4;
                    if (g is { Why: { } why } && !why.StartsWith("the gap is not straight", StringComparison.Ordinal)) return null;
                }
                return null;
            }
            var sides = new[] { 1.0, -1.0 }.Select(d => (Dir: d, G: Gap(d)!.Value)).ToList();
            var usable = sides.Where(s => s.G.Ground is not null).ToList();
            string SideName(double d) => (normalX ? (d > 0 ? "+y" : "−y") : (d > 0 ? "+x" : "−x"));
            if (usable.Count == 0)
                return $"Port {p.Number}'s Return states the same layer (a coplanar return), but on '{band.Layer.Name}' beside its " +
                       $"line's end {string.Join("; ", sides.Select(s => $"on the {SideName(s.Dir)} side {s.G.Why}"))}. A coplanar " +
                       "return runs from the line across a straight gap to ground metal on the same layer. Draw that ground, or " +
                       "choose a plane above or below as the Return.";
            var pick = usable.MinBy(s => Math.Abs(s.G.GroundEdge - s.G.LineEdge));
            var (lineEdge, groundEdge, ground, _) = pick.G;

            double z = piece.IsSheet ? piece.SheetZ : (piece.ZBottom + piece.ZTop) / 2;
            double a0 = Math.Min(edgeAt, edgeAt + inward * w), a1 = Math.Max(edgeAt, edgeAt + inward * w);
            double c0 = Math.Min(lineEdge, groundEdge), c1 = Math.Max(lineEdge, groundEdge);
            var min = normalX ? new Point3(a0, c0, z) : new Point3(c0, a0, z);
            var max = normalX ? new Point3(a1, c1, z) : new Point3(c1, a1, z);
            // From the ground toward the line, so the port's voltage is the line's minus the ground's.
            double toLine = -pick.Dir;
            var direction = normalX ? new Point3(0, toLine, 0) : new Point3(toLine, 0, 0);
            var normal = normalX ? new Point3(inward, 0, 0) : new Point3(0, inward, 0);
            var centre = new Point3((min.X + max.X) / 2, (min.Y + max.Y) / 2, z);
            ports.Add(new Em3dPort(p.Number, $"port/{p.Number}", piece.Name, ground!.Name, min, max, direction, p.Z0,
                                   new Em3dReferencePlane(centre, normal, 0)));
            _portReturns[p.Number] = new(band.Layer.Name, false, false, "port", Coplanar: true);
            var other = sides.First(s => s.Dir != pick.Dir).G;
            _notes.Add($"Port {p.Number} at its line's end is a horizontal lumped sheet {Length(w)} long across the " +
                       $"{Length(c1 - c0)} gap from '{band.Layer.Name}' to the same-layer ground '{ground.Name}' on its " +
                       $"{SideName(pick.Dir)} side" +
                       (other.Ground is not null ? $", the nearer of the two (the {SideName(-pick.Dir)} side's gap is {Length(Math.Abs(other.GroundEdge - other.LineEdge))})" : "") +
                       $": as port {p.Number}'s Return states (same layer).");
            return null;
        }

        /// <summary>Whether <paramref name="poly"/> has metal at <paramref name="along"/> on a port's normal
        /// axis and <paramref name="across"/> on the other.</summary>
        private static bool MetalAt(PlanarPolygon poly, PlanarPortSide side, double along, double across)
            => side is PlanarPortSide.MinX or PlanarPortSide.MaxX ? poly.Contains(along, across) : poly.Contains(across, along);

        /// <summary>A length in metres, in µm and in the layout's own display unit when that is another.</summary>
        private string Length(double m)
        {
            string um = $"{Fmt(m * 1e6)} µm";
            var unit = source.View.DisplayUnit;
            if (unit == LayoutUnit.Um) return um;
            long dbu = (long)Math.Round(m * 1e6 * source.DbuPerMicron);
            return $"{um} ({LayoutUnits.Format(dbu, unit, source.DbuPerMicron)} {LayoutUnits.Suffix(unit)})";
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


        private Em3dGenerationResult No(string refusal)
            => new(null, refusal, _notes) { Warnings = _g is null ? [] : _g.WarningList };

        private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// brief-dfr28-5 — a static solve's refusal of a via that is on one net and passes copper of another
    /// (<see cref="Em3dLayoutSolids.Builder.ViaNetConflictMap"/>), or null. The via's own net wins in the net map, so the
    /// solve would hold one piece of metal at two potentials: on the TP149 board a drill labelled GND under a test point
    /// grounded its barrel inside the terminal's copper, and the "capacitance" grew with every refinement pass to ten
    /// times the real one. A driven solve does not read nets, so only a static one refuses.
    /// </summary>
    internal static string? ViaNetRefusal(EmSetup setup, IReadOnlyDictionary<string, string> conflicts)
    {
        if (conflicts.Count == 0) return null;
        string kind = setup.Problem3D == Em3dProblemType.Magnetostatic ? "magnetostatic" : "electrostatic";
        var shown = conflicts.Take(3).Select(kv => $"'{kv.Key}': {kv.Value}");
        return $"{conflicts.Count} via(s) are on a net of their own and pass copper of another net — {string.Join("; ", shown)}" +
               (conflicts.Count > 3 ? $"; and {conflicts.Count - 3} more" : "") +
               $". A via is one piece of metal with the copper it passes, so this {kind} solve would hold it at two potentials. " +
               "Give the via the net of the copper it joins, or clear its net so it takes that copper's.";
    }

    /// <summary>brief-dfr31-1 R-dfr31-1-6 — the most floating bodies a run solves: each is one more solve on the same operator,
    /// so a whole board's unconnected copper is refused with the counts rather than becoming hundreds of solves unasked.</summary>
    public const int MaxFloatingBodies = 50;

    /// <summary>
    /// R-em3d22-2a — each terminal's conductors and the ground. A terminal or the ground is named by an object path, a net or
    /// a unique path suffix (<see cref="Em3dConductorNames"/>, brief-dfr31-1 R-dfr31-1-3); without Ground3D the ground is the
    /// ground-reference conductors and anything sharing their net. <b>An electrostatic terminal is whole bodies</b>
    /// (R-dfr31-1-2): naming any object or net of a piece of metal takes all of it (<see cref="Em3dBodies"/>), two terminals
    /// on one body are refused with both names, and every body left over is floated, grounded or refused as the setup's
    /// UnassignedConductors3D says (R-dfr31-1-5). A magnetostatic terminal stays the objects named, less the ground: its
    /// current path runs through the ground, so its metal touching the ground is the circuit, not a short.
    /// Refuses a name that resolves to no conductor (R-em3d22-2c), and a magnetostatic terminal whose source is not a port.
    /// </summary>
    internal static string? Terminals(EmSetup setup, List<Em3dSolid> solids, List<Em3dSheet> sheets, List<Em3dPort> ports,
                                     bool pecFloor, Dictionary<string, HashSet<string>> _objectNets,
                                     List<string> _groundBandObjects,
                                     out List<Em3dTerminal> terminals, out List<string> ground,
                                     List<string>? notes = null, IReadOnlyList<Em3dCylinderSheet>? cylinderSheets = null)
    {
        terminals = [];
        ground = [];
        bool es = setup.Problem3D == Em3dProblemType.Electrostatic;
        var conductors = solids.Where(s => s.Role == Em3dRole.Conductor).Select(s => s.Name)
                               .Concat(sheets.Select(s => s.Name)).Concat((cylinderSheets ?? []).Select(s => s.Name)).ToList();
        var names = new Em3dConductorNames(conductors, _objectNets);
        var bodies = es ? Em3dBodies.Group(solids, sheets, cylinderSheets,
                                           n => _objectNets.TryGetValue(n, out var set) ? set : []) : null;
        IEnumerable<int> BodiesOf(IEnumerable<string> objects) => objects.Select(o => bodies!.BodyOf(o)).Where(b => b >= 0).Distinct();

        // brief-dfr34-4 R-dfr34-4-1 — the ground is a list: the default ground when the box is blank or lists it, and each
        // name's conductors beside it.
        if (Em3dGroundList.IncludesDefault(setup.Ground3D))
        {
            var groundNets = _groundBandObjects.SelectMany(o => _objectNets.GetValueOrDefault(o) ?? []).ToHashSet(StringComparer.Ordinal);
            ground = [.. conductors.Where(c => _groundBandObjects.Contains(c) ||
                                               (_objectNets.TryGetValue(c, out var nets) && nets.Overlaps(groundNets)))];
        }
        foreach (string groundName in Em3dGroundList.Names(setup.Ground3D))
        {
            var r = names.Resolve(groundName);
            if (r.Ambiguous.Count > 0)
                return $"This setup's ground (Ground3D) is {Em3dConductorNames.AmbiguousText(groundName, r.Ambiguous)}";
            if (!r.Ok && !pecFloor)
                return $"This setup's ground names '{groundName}' (Ground3D), and no conductor in the 3D problem is on it or named so. " +
                       "Name a net, an object or the end of an object's path, or leave Ground3D empty for the ground-reference conductors." +
                       Em3dConductorNames.NearText(r.Near);
            var have = ground.ToHashSet(StringComparer.Ordinal);
            ground.AddRange(r.Objects.Where(have.Add));
        }
        var groundBodies = es ? BodiesOf(ground).ToHashSet() : [];

        if (setup.Terminals3D.Count == 0)
            return $"This {(es ? "electrostatic" : "magnetostatic")} setup " +
                   "names no terminal, so there is no matrix to compute. List each conductor's net under Terminals3D.";
        var taken = new Dictionary<int, (string Terminal, string By)>();
        foreach (var t in setup.Terminals3D)
        {
            if (t.ByObjects && t.Net is { Length: > 0 })
                return $"Terminal '{t.Name}' states both a net ('{t.Net}') and objects; a terminal is one or the other.";
            var named = new List<string>();
            var termBodies = new List<int>();
            foreach (string token in t.ByObjects ? t.Objects! : [t.Net])
            {
                var r = names.Resolve(token);
                if (r.Ambiguous.Count > 0)
                    return $"Terminal '{t.Name}' names {Em3dConductorNames.AmbiguousText(token.Trim(), r.Ambiguous)}";
                if (!r.Ok)
                    return (t.ByObjects
                        ? $"Terminal '{t.Name}' names '{token}', which is not a conductor of the 3D problem" +
                          (solids.Any(s => s.Name == token) ? " (it is a dielectric or air solid)" : "") + "."
                        : $"Terminal '{t.Name}' names net '{t.Net}', and no conductor in the 3D problem is on it. A terminal is the " +
                          "metal of one net: name a net the layout (or a .wBond wire array) carries, or an object.") +
                        Em3dConductorNames.NearText(r.Near);
                if (!es)
                {
                    var groundSet = ground;
                    if (t.ByObjects && r.Objects.FirstOrDefault(groundSet.Contains) is { } grounded)
                        return $"Terminal '{t.Name}' names '{grounded}', which is the ground; a conductor is at one potential.";
                    named.AddRange(r.Objects.Where(o => !groundSet.Contains(o)));
                    continue;
                }
                foreach (int b in BodiesOf(r.Objects))
                {
                    if (groundBodies.Contains(b))
                        return $"Terminal '{t.Name}' names '{token}', which is one piece of metal with the ground " +
                               $"('{bodies!.Bodies[b].Name}', {Count(bodies.Bodies[b].Members.Count)}); a conductor is at one potential." +
                               // Designer feedback round 34 — asked as "how do I see RF out vs GND?": the ground needs no terminal.
                               " The ground needs no terminal: the matrix is referred to it, and each terminal's capacitance to " +
                               "ground is the result's C_ground (the row sum of C, and the diagonal of C_mutual)." +
                               (Em3dGroundList.Names(setup.Ground3D).Count > 0 ? "" :
                                   " With Ground blank the ground is every conductor on a ground-reference layer and the metal " +
                                   "touching it; to add a plane to it, list the plane's object or net after '+ default ground'.");
                    if (taken.TryGetValue(b, out var other) && other.Terminal != t.Name)
                        return $"Terminals '{other.Terminal}' and '{t.Name}' are one piece of metal ('{bodies!.Bodies[b].Name}'): " +
                               $"'{other.By}' and '{token}' touch, so they are at one potential. Make them one terminal, or name another conductor.";
                    taken[b] = (t.Name, token);
                    if (!termBodies.Contains(b)) termBodies.Add(b);
                }
            }
            var objects = es ? termBodies.Order().SelectMany(b => bodies!.Bodies[b].Members).ToList() : named.Distinct(StringComparer.Ordinal).ToList();
            if (objects.Count == 0)
                return $"Terminal '{t.Name}' names net '{t.Net}', and no conductor in the 3D problem is on it that is not also the ground. " +
                       "A terminal is the metal of one net: name a net the layout (or a .wBond wire array) carries.";
            string? source = null;
            if (setup.Problem3D == Em3dProblemType.Magnetostatic)
            {
                if (t.Source is not { Length: > 0 } src)
                    return $"Magnetostatic terminal '{t.Name}' names no source. Its current needs a path in and back: " +
                           "name the port whose sheet drives it (Source: the port's number).";
                source = src.StartsWith("port/", StringComparison.Ordinal) ? src : "port/" + src.Trim();
                if (ports.All(p => p.Name != source))
                    return $"Terminal '{t.Name}' is driven through port '{src}', and the layout has no such port " +
                           $"(it has {(ports.Count == 0 ? "none" : string.Join(", ", ports.Select(p => p.Number)))}).";
            }
            terminals.Add(new Em3dTerminal(t.Name, objects, source));
        }
        if (!es) return null;

        // ── R-dfr31-1-5 — every body in no terminal and not ground ─────────────────────────────────────────────────────
        var groundObjects = groundBodies.Order().SelectMany(b => bodies!.Bodies[b].Members).ToList();
        var rest = Enumerable.Range(0, bodies!.Bodies.Count).Where(b => !groundBodies.Contains(b) && !taken.ContainsKey(b)).ToList();
        if (rest.Count > 0)
        {
            string Listed(int max) => string.Join(", ", rest.Take(max).Select(b => $"'{bodies.Bodies[b].Name}' ({Count(bodies.Bodies[b].Members.Count)})")) +
                                      (rest.Count > max ? $" and {rest.Count - max} more" : "");
            switch (setup.UnassignedConductors3D)
            {
                case EmUnassignedConductors.Refuse:
                    return $"{rest.Count} piece(s) of metal are in no terminal and are not the ground: {Listed(5)}. An electrostatic " +
                           "solve has to hold each at some potential. Set Other metal (UnassignedConductors3D) to Float, for metal " +
                           "connected to nothing, or Ground, for plane copper and its vias; or make it a terminal.";
                case EmUnassignedConductors.Ground:
                {
                    int largest = rest.MaxBy(b => bodies.Bodies[b].Members.Count);
                    groundObjects = [.. groundBodies.Concat(rest).Order().SelectMany(b => bodies.Bodies[b].Members)];
                    notes?.Add($"{rest.Count} piece(s) of metal in no terminal are ground (UnassignedConductors3D: Ground), the largest " +
                               $"'{bodies.Bodies[largest].Name}' ({Count(bodies.Bodies[largest].Members.Count)}).");
                    break;
                }
                default:
                {
                    if (rest.Count > MaxFloatingBodies)
                        return $"{rest.Count} piece(s) of metal are in no terminal and not the ground, and floating each would add " +
                               $"{rest.Count} solves; a run floats at most {MaxFloatingBodies}. Ground the plane copper (Other metal: " +
                               $"Ground, or name it as Ground3D), or make the pieces terminals. The first: {Listed(5)}.";
                    var used = new HashSet<string>(terminals.Select(x => x.Name), StringComparer.Ordinal);
                    foreach (int b in rest)
                    {
                        string name = bodies.Bodies[b].Name;
                        for (int k = 2; !used.Add(name); k++) name = $"{bodies.Bodies[b].Name} ({k})";
                        terminals.Add(new Em3dTerminal(name, bodies.Bodies[b].Members) { Floating = true });
                    }
                    notes?.Add($"{rest.Count} floating piece(s) of metal add {rest.Count} solve(s): {Listed(3)}. Each carries no net charge, " +
                               "so it is solved as one more terminal and then eliminated from the matrix, which is exact.");
                    break;
                }
            }
        }
        ground = groundObjects;
        return null;
    }

    private static string Count(int objects) => objects == 1 ? "1 object" : $"{objects} objects";

    /// <summary>
    /// <b>R-em3d3-6 — the air box around a content extent</b>: the setup's padding per face (its default
    /// an eighth of the longest wavelength, a quarter when a radiation pattern is asked for, the structure's
    /// own largest extent for a static solve), a wave port's face on its line's end, and the floor on the
    /// plane when there is one. brief-em3d-42 R-em3d42-5c — SHARED: a .cem's generator and a .c3d's problem
    /// assembly pad by this one method, so the two cannot disagree about a box.
    /// </summary>
    /// <param name="extent">The content's extent, metres.</param>
    /// <param name="floorZ">The PEC floor's height, or null when the box has none.</param>
    /// <param name="waves">Each wave port's face, where its line ends, and its number.</param>
    public static Em3dAirBox? PaddedAirBox(EmSetup setup, (double X0, double Y0, double X1, double Y1, double Z0, double Z1) extent,
                                           double fMin, double? floorZ, IReadOnlyList<(string Face, double EdgeAt, int Port)> waves,
                                           List<string> notes, out string? refusal, double? paddingHeightM = null,
                                           double? paddingHeightBelowM = null)
    {
        refusal = null;
        var (cx0, cy0, cx1, cy1, zLow, zHigh) = extent;
        // brief-em3d-22 — a static solve has no wavelength, so its default padding is the structure's
        // own largest extent: far enough that the box's faces barely touch the field, and a length a
        // package-sized problem can mesh.
        bool radiating = setup.RadiationPattern && setup.Problem3D == Em3dProblemType.Driven;
        var defaults = DefaultFacePadding(setup, fMin, Math.Max(Math.Max(cx1 - cx0, cy1 - cy0), zHigh - zLow), paddingHeightM,
                                          paddingHeightBelowM);
        double pad = defaults.CapM;
        // brief-dfr34-2 R-dfr34-2-1 — the note names the rule that set the padding, the cap included when it applied.
        if (radiating)
            notes.Add($"The air box's default padding is {Fmt(pad * 1e3)} mm on every face — {defaults.CapBasis} — because a " +
                      "radiation pattern was asked for: its equivalence surface sits inside the absorber with room to spare around " +
                      "the structure." + (setup.PatternFrequencyGHz is > 0 ? "" :
                      " The setup names no pattern frequency, so the sweep's lowest stands in for it; state one to size the box for it.") +
                      " A padding the setup's AirBox states is used as stated.");
        var box = setup.AirBox ?? new EmAirBox();
        // brief-em3d-23 R-em3d23-2a — a wave port lies on the box, so its side has no padding and its
        // line must run to the structure's edge there.
        var waveFaces = waves.Select(w => w.Face).ToHashSet(StringComparer.Ordinal);
        box = box with
        {
            XMin = waveFaces.Contains("xmin") ? new EmAirBoxFace(0, box.XMin?.Boundary) : box.XMin,
            XMax = waveFaces.Contains("xmax") ? new EmAirBoxFace(0, box.XMax?.Boundary) : box.XMax,
            YMin = waveFaces.Contains("ymin") ? new EmAirBoxFace(0, box.YMin?.Boundary) : box.YMin,
            YMax = waveFaces.Contains("ymax") ? new EmAirBoxFace(0, box.YMax?.Boundary) : box.YMax,
        };
        foreach (var (face, edgeAt, port) in waves)
        {
            double at = face switch { "xmin" => cx0, "xmax" => cx1, "ymin" => cy0, _ => cy1 };
            if (Math.Abs(edgeAt - at) > 1e-9 * Math.Max(1e-3, Math.Abs(at)))
            {
                refusal = $"Port {port} is a wave port, and a wave port lies on the air box — but its line ends at " +
                          $"{face[0]} = {Fmt(edgeAt * 1e6)} µm, short of the box's {face} face at {Fmt(at * 1e6)} µm, " +
                          "where other geometry (the board outline, a plane, another conductor) reaches. Run the line to " +
                          $"the {face} edge of the layout, or make port {port} a lumped port.";
                return null;
            }
            if (setup.AirBox is { } stated && FaceOf(stated, face) is { PaddingUm: > 0 } padded)
                notes.Add($"Port {port} is a wave port, so the air box's {face} face lies on its line's end: the " +
                          $"setup's {Fmt(padded.PaddingUm!.Value)} µm padding there is not used.");
            else if (setup.AirBox is { } statedPct && FaceOf(statedPct, face) is { PaddingPercent: > 0 } paddedPct)
                notes.Add($"Port {port} is a wave port, so the air box's {face} face lies on its line's end: the " +
                          $"setup's {Fmt(paddedPct.PaddingPercent!.Value)} % padding there is not used.");
        }
        // 3D editor round 1 — a face may state its padding as a percentage of the content's extent along its axis.
        foreach (var (name, f) in new[] { ("xmin", box.XMin), ("xmax", box.XMax), ("ymin", box.YMin),
                                          ("ymax", box.YMax), ("zmin", box.ZMin), ("zmax", box.ZMax) })
        {
            if (f is { PaddingUm: not null, PaddingPercent: not null })
            {
                refusal = $"The air box's {name} face states both PaddingUm and PaddingPercent; state one of them.";
                return null;
            }
            if (f?.PaddingPercent is < 0)
            {
                refusal = $"The air box's {name} face has a negative PaddingPercent; a padding is a distance from the geometry, zero or more.";
                return null;
            }
        }
        var flatAxes = new SortedSet<char>();
        double Pad(EmAirBoxFace? f, string face, double span)
        {
            if (f?.PaddingUm is { } um) return um * 1e-6;
            if (f?.PaddingPercent is not { } pct) return defaults.For(face, span);
            if (span > 0) return pct / 100 * span;
            flatAxes.Add(face[0]);
            return defaults.For(face, 0);
        }
        Em3dBoundaryKind Kind(EmAirBoxFace? f) => f?.Boundary ?? Em3dBoundaryKind.Absorbing;

        double sx = cx1 - cx0, sy = cy1 - cy0, sz = zHigh - zLow;
        var boxMin = new Point3(cx0 - Pad(box.XMin, "xmin", sx), cy0 - Pad(box.YMin, "ymin", sy), floorZ is { } fz ? fz : zLow - Pad(box.ZMin, "zmin", sz));
        var boxMax = new Point3(cx1 + Pad(box.XMax, "xmax", sx), cy1 + Pad(box.YMax, "ymax", sy), zHigh + Pad(box.ZMax, "zmax", sz));
        foreach (char a in flatAxes)
            notes.Add($"The air box's padding along {a} is stated as a percentage of the content's extent along {a}, which is " +
                      $"zero (the content is flat there), so those faces take the default padding ({Fmt(defaults.For(a == 'z' ? "zmax" : "xmax", 0) * 1e3)} mm) instead.");
        var faces = new Em3dFaces(Kind(box.XMin), Kind(box.XMax), Kind(box.YMin), Kind(box.YMax),
                                  floorZ is not null ? Em3dBoundaryKind.Pec : Kind(box.ZMin), Kind(box.ZMax));
        return new Em3dAirBox(boxMin, boxMax, faces);
    }

    /// <summary>
    /// brief-dfr28-3 R-dfr28-3-3 — a lumped port's sheet lying IN an absorbing face of the box is moved off it: that face is
    /// padded outward by the sheet's longer side, and a Note says so. In the face, the port shares its power with the
    /// absorber — on the TP149 board a through line read a flat −2.15 dB on Palace and −2.9 dB on EMerge where 0 dB was
    /// right; openEMS's PML begins at that face (and its Mur boundary overwrites the very field the port drives). A sheet
    /// in a PMC face is refused instead, because moving a symmetry plane changes the problem. Coplanar to the tolerance
    /// <see cref="PaddedAirBox"/> holds a wave port's line end to; a port ending ON a PEC face (a pad-to-plane port's
    /// floor) is not a sheet in it.
    /// </summary>
    public static Em3dAirBox ClearOfPortSheets(Em3dAirBox box, IReadOnlyList<Em3dPort> ports, List<string> notes, out string? refusal)
    {
        refusal = null;
        double[] lo = [box.Min.X, box.Min.Y, box.Min.Z], hi = [box.Max.X, box.Max.Y, box.Max.Z];
        var f = box.Faces;
        Em3dBoundaryKind[] kinds = [f.XMin, f.XMax, f.YMin, f.YMax, f.ZMin, f.ZMax];
        string[] names = ["xmin", "xmax", "ymin", "ymax", "zmin", "zmax"];
        bool moved = false;
        foreach (var p in ports.Where(q => q.Kind == Em3dPortKind.Lumped).OrderBy(q => q.Number))
        {
            double[] pMin = [p.Min.X, p.Min.Y, p.Min.Z], pMax = [p.Max.X, p.Max.Y, p.Max.Z];
            int axis = -1;
            for (int a = 0; a < 3; a++) if (Math.Abs(pMax[a] - pMin[a]) <= 1e-9 * Math.Max(1e-3, Math.Abs(pMin[a]))) axis = a;
            if (axis < 0) continue;
            double side = Enumerable.Range(0, 3).Where(a => a != axis).Max(a => pMax[a] - pMin[a]);
            for (int end = 0; end < 2; end++)
            {
                int face = 2 * axis + end;
                double at = end == 0 ? lo[axis] : hi[axis];
                if (kinds[face] == Em3dBoundaryKind.Pec || Math.Abs(pMin[axis] - at) > 1e-9 * Math.Max(1e-3, Math.Abs(at)) ||
                    Enumerable.Range(0, 3).Any(a => a != axis && (pMax[a] <= lo[a] || pMin[a] >= hi[a])))
                    continue;
                if (kinds[face] != Em3dBoundaryKind.Absorbing)
                {
                    refusal = $"Port {p.Number}'s sheet lies in the air box's {names[face]} face, which is {kinds[face]}: a port " +
                              $"there is cut by that boundary. Pad the {names[face]} face (AirBox {names[face]} PaddingUm) so the " +
                              "port is inside the box.";
                    return box;
                }
                if (end == 0) lo[axis] -= side; else hi[axis] += side;
                moved = true;
                notes.Add($"Port {p.Number}'s sheet lay in the air box's {names[face]} face, an absorbing face, which would take " +
                          $"part of its power; that face is padded {Fmt(side * 1e6)} µm outward so the port is inside the box.");
            }
        }
        return moved ? box with { Min = new Point3(lo[0], lo[1], lo[2]), Max = new Point3(hi[0], hi[1], hi[2]) } : box;
    }

    /// <summary>
    /// Designer feedback round 11 — the padding a face takes when the setup states none, and the rule that gave it, as the
    /// panel and <c>explain</c> print it ("λ/8 at 2 GHz, the sweep's lowest frequency"). <see cref="PaddedAirBox"/> pads by
    /// exactly this, so what is shown cannot differ from what is meshed.
    /// </summary>
    /// <param name="largestExtentM">The content's largest extent, metres — a static solve's padding.</param>
    /// <summary>What a run says when its setup sizes the mesh somewhere other than the sweep's top; null when it does not.</summary>
    public static string? MeshFrequencyNote(Em3dProblem problem, EmSetup setup)
    {
        if (problem.MeshFrequencyHz is not > 0 || problem.MeshFrequencyHz == problem.Frequency.StopHz) return null;
        double at = problem.MeshFrequencyHz.Value, top = problem.Frequency.StopHz;
        string note = $"The mesh is sized at {Fmt(at / 1e9)} GHz, this setup's Mesh frequency, and the solve covers the sweep to {Fmt(top / 1e9)} GHz: " +
                      (at < top ? $"at the top of the sweep the elements are {Fmt(top / at)} × as long against the wavelength." : "finer than the sweep's top asks for.");
        return setup.Solver3D.Runs(CircuitRF.Design.Em3d.OpenEmsSolver.Id) ? note + " openEMS's grid is still sized at the sweep's top." : note;
    }

    public static (double PadM, string Basis, string Short) DefaultPadding(EmSetup setup, double fMin, double largestExtentM)
    {
        if (setup.IsStatic3D)
            return (Math.Max(largestExtentM, 1e-6), "the structure's largest extent, since a static solve has no wavelength", "largest extent");
        bool radiating = setup.RadiationPattern && setup.Problem3D == Em3dProblemType.Driven;
        if (radiating)
        {
            // brief-dfr34-2 R-dfr34-2-1 (D1) — λ/4 at the frequency the pattern is read at (the sweep's lowest when the setup
            // names none), never more than the structure's largest extent times PatternPaddingCapExtents, and never less than
            // λ/8 at the sweep's lowest: the absorbing faces still see every point of the sweep.
            bool named = setup.PatternFrequencyGHz is > 0;
            double fp = named ? setup.PatternFrequencyGHz!.Value * 1e9 : fMin;
            string at = named ? $"{Hz(fp)}, the pattern's frequency" : $"{Hz(fMin)}, the sweep's lowest frequency";
            double quarter = PatternPaddingFractionOfLongestWavelength * C0 / fp;
            double floor = DefaultPaddingFractionOfLongestWavelength * C0 / fMin;
            double cap = largestExtentM > 0 ? PatternPaddingCapExtents * largestExtentM : double.PositiveInfinity;
            double pad = Math.Min(quarter, cap);
            if (pad < floor)
                return (floor, $"λ/8 at {Hz(fMin)}, the sweep's lowest frequency: more than λ/4 at {at}" +
                               (cap < quarter ? " capped by the structure's largest extent" : "") +
                               ", because the absorbing faces see the whole sweep", $"λ/8 at {Hz(fMin)}");
            if (cap < quarter)
                return (cap, $"the structure's largest extent ({Fmt(cap * 1e3)} mm), which caps λ/4 at {at} " +
                             $"({Fmt(quarter * 1e3)} mm); a radiation pattern is asked for", "largest extent, capping λ/4");
            return (quarter, $"λ/4 at {at}, since a radiation pattern is asked for", $"λ/4 at {Hz(fp)}");
        }
        // A structure that does not radiate keeps its field within about its own size, as a static solve does: λ/8 of a
        // low first frequency round a board a few millimetres across was a box of tens of gigabytes, nearly all of it air.
        double eighth = DefaultPaddingFractionOfLongestWavelength * C0 / fMin;
        return largestExtentM > 0 && largestExtentM < eighth
            ? (largestExtentM, $"the structure's largest extent, less than λ/8 at {Hz(fMin)}", "largest extent")
            : (eighth, $"λ/8 at {Hz(fMin)}, the sweep's lowest frequency", $"λ/8 at {Hz(fMin)}");
    }

    /// <summary>brief-dfr34-2 R-dfr34-2-1 (D1) — a radiating setup's default padding is never more than this many times the
    /// structure's largest extent (unless λ/8 at the sweep's lowest frequency asks for more). A 44 × 95 mm board swept from
    /// 0.5 GHz took λ/4 = 150 mm on every face; capped, 95 mm.</summary>
    public const double PatternPaddingCapExtents = 1;

    /// <summary>Designer round 33 — the default padding in multiples of H, the dielectric height under the top metal: this
    /// many H on the top and the four sides…</summary>
    public const double DefaultPaddingSidesAndTopH = 20;

    /// <summary>…and this many below. Measured in brief-dfr32-1 (src/Design/RESOLVED.md): lines and vias were settled by
    /// 10 H on the top and sides and 5 H below; a resonant filter wanted about 20 H on the top and sides.</summary>
    public const double DefaultPaddingBelowH = 10;

    /// <summary>The top's default padding, as a percentage of the content's height (z extent), for the problems
    /// <see cref="ByHeightApplies"/> covers; never more than the wavelength rule's cap. A setup that needs more air above
    /// states its AirBox ZMax.</summary>
    public const double DefaultPaddingTopPercent = 100;

    /// <summary>Designer round 33 — how much of the footprint an unbroken bottom ground plane must cover for the air box's floor
    /// to go on its underside (<c>Em3dLayoutSolids</c> for a <c>.cem</c>, <c>C3dProblemAssembly.PlaneFloor</c> for a <c>.c3d</c>).</summary>
    public const double PlaneFloorCover = 0.95;

    /// <summary>
    /// Whether a bottom ground plane's <paramref name="outlines"/> cover <see cref="PlaneFloorCover"/> of the footprint
    /// (<paramref name="x0"/>…<paramref name="y1"/>, metres), leaving out a band <paramref name="edgeBandM"/> wide round the
    /// footprint's edge (H, the dielectric under the top metal; never more than a quarter of the footprint's shorter side).
    /// A board's copper is pulled back from its edge as a matter of course, and on a small board that band alone is several
    /// percent of the area: a 0.46 mm pullback on a 20 mm board read 92.5 % against the whole footprint and kept an
    /// unbroken plane from being the floor. Holes are judged on their own (<see cref="CoveredHole"/>), so only the outlines
    /// count here.
    /// </summary>
    public static bool PlaneCoversFootprint(IEnumerable<IReadOnlyList<(double X, double Y)>> outlines,
                                            double x0, double y0, double x1, double y1, double? edgeBandM)
    {
        double band = Math.Clamp(edgeBandM ?? 0, 0, Math.Min(x1 - x0, y1 - y0) / 4);
        double ax = x0 + band, ay = y0 + band, bx = x1 - band, by = y1 - band;
        double footprint = (bx - ax) * (by - ay);
        if (footprint <= 0) return false;
        double covered = outlines.Sum(o => Math.Abs(SignedArea(ClipToRect(o, ax, ay, bx, by))));
        return covered >= PlaneFloorCover * footprint;

        static double SignedArea(List<(double X, double Y)> r)
        {
            double a = 0;
            for (int i = 0, j = r.Count - 1; i < r.Count; j = i++) a += (r[j].X + r[i].X) * (r[j].Y - r[i].Y);
            return a / 2;
        }

        // Clipping a simple ring against a rectangle, one side at a time. Where the ring leaves and re-enters the rectangle the
        // result runs along the side between, which adds no area, so the area is exact for a non-convex ring too.
        static List<(double X, double Y)> ClipToRect(IReadOnlyList<(double X, double Y)> ring, double ax, double ay, double bx, double by)
        {
            var poly = ring.ToList();
            poly = Side(poly, p => p.X >= ax, (p, q) => Cut(p, q, ax, true));
            poly = Side(poly, p => p.X <= bx, (p, q) => Cut(p, q, bx, true));
            poly = Side(poly, p => p.Y >= ay, (p, q) => Cut(p, q, ay, false));
            poly = Side(poly, p => p.Y <= by, (p, q) => Cut(p, q, by, false));
            return poly;
        }

        static (double X, double Y) Cut((double X, double Y) p, (double X, double Y) q, double at, bool onX)
        {
            double t = onX ? (at - p.X) / (q.X - p.X) : (at - p.Y) / (q.Y - p.Y);
            return onX ? (at, p.Y + t * (q.Y - p.Y)) : (p.X + t * (q.X - p.X), at);
        }

        static List<(double X, double Y)> Side(List<(double X, double Y)> poly, Func<(double X, double Y), bool> inside,
                                               Func<(double X, double Y), (double X, double Y), (double X, double Y)> cut)
        {
            var output = new List<(double X, double Y)>();
            for (int i = 0; i < poly.Count; i++)
            {
                var cur = poly[i];
                var prev = poly[(i + poly.Count - 1) % poly.Count];
                bool ci = inside(cur), pi = inside(prev);
                if (ci)
                {
                    if (!pi) output.Add(cut(prev, cur));
                    output.Add(cur);
                }
                else if (pi) output.Add(cut(prev, cur));
            }
            return output;
        }
    }

    /// <summary>
    /// brief-emb-2 R-emb2-5 — the first hole of a floor plane that something lies over in footprint (a centre, metres): a ring
    /// of <paramref name="metal"/> overlapping it, or a drill of <paramref name="drills"/> reaching into it. Null when every hole
    /// is clear, so the floor closing them shorts nothing and no field above one is cut off.
    /// </summary>
    public static (double X, double Y)? CoveredHole(IEnumerable<IReadOnlyList<Point2>> holes, IEnumerable<IReadOnlyList<Point2>> metal,
                                                    IReadOnlyCollection<(double X, double Y, double R)> drills)
    {
        var rings = metal.Where(r => r.Count >= 3).ToList();
        foreach (var hole in holes.Where(h => h.Count >= 3))
        {
            var centre = new Point2(hole.Average(q => q.X), hole.Average(q => q.Y));
            if (rings.Any(r => RingsOverlap(hole, r))) return (centre.X, centre.Y);
            if (drills.Any(d => Inside(hole, new Point2(d.X, d.Y)) || DistanceToRing(hole, new Point2(d.X, d.Y)) < d.R))
                return (centre.X, centre.Y);
        }
        return null;
    }

    /// <summary>Whether two simple rings' interiors meet: a vertex of one inside the other, or two edges that cross.</summary>
    internal static bool RingsOverlap(IReadOnlyList<Point2> a, IReadOnlyList<Point2> b)
    {
        static (double, double, double, double) Box(IReadOnlyList<Point2> r)
            => (r.Min(q => q.X), r.Min(q => q.Y), r.Max(q => q.X), r.Max(q => q.Y));
        var (ax0, ay0, ax1, ay1) = Box(a);
        var (bx0, by0, bx1, by1) = Box(b);
        if (ax1 <= bx0 || bx1 <= ax0 || ay1 <= by0 || by1 <= ay0) return false;
        if (b.Any(q => Inside(a, q)) || a.Any(q => Inside(b, q))) return true;
        for (int i = 0, j = a.Count - 1; i < a.Count; j = i++)
            for (int k = 0, m = b.Count - 1; k < b.Count; m = k++)
                if (Cross(a[j], a[i], b[m], b[k])) return true;
        return false;

        static bool Cross(Point2 p, Point2 q, Point2 r, Point2 s)
        {
            static double O(Point2 a, Point2 b, Point2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            double d1 = O(p, q, r), d2 = O(p, q, s), d3 = O(r, s, p), d4 = O(r, s, q);
            return (d1 > 0 && d2 < 0 || d1 < 0 && d2 > 0) && (d3 > 0 && d4 < 0 || d3 < 0 && d4 > 0);
        }
    }

    /// <summary>Whether <paramref name="p"/> is strictly inside ring <paramref name="r"/> (even-odd).</summary>
    private static bool Inside(IReadOnlyList<Point2> r, Point2 p)
    {
        bool inside = false;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
            if (r[i].Y > p.Y != r[j].Y > p.Y && p.X < (r[j].X - r[i].X) * (p.Y - r[i].Y) / (r[j].Y - r[i].Y) + r[i].X)
                inside = !inside;
        return inside;
    }

    private static double DistanceToRing(IReadOnlyList<Point2> r, Point2 p)
    {
        double best = double.PositiveInfinity;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
        {
            double dx = r[i].X - r[j].X, dy = r[i].Y - r[j].Y, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Clamp(((p.X - r[j].X) * dx + (p.Y - r[j].Y) * dy) / l2, 0, 1) : 0;
            double ex = r[j].X + t * dx - p.X, ey = r[j].Y + t * dy - p.Y;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    /// <summary>brief-emb-2 R-emb2-1 — the round solids that are a layout's drilled barrels: a via (its barrel and its air core)
    /// and a drilled hole, as their origins say (<see cref="Em3dProblem.ArtworkBarrels"/>). A <c>.c3d</c>'s own cylinders have no
    /// layout origin and are never among them.</summary>
    public static IReadOnlyCollection<string> ArtworkBarrels(IEnumerable<Em3dSolid> solids, IReadOnlyDictionary<string, Em3dObjectOrigin> origins)
        => [.. solids.Where(s => s.Primitive is Em3dCylinder && origins.TryGetValue(s.Name, out var o) &&
                                 (o.Kind == Em3dObjectKind.Via || o.Kind == Em3dObjectKind.Air && o.StackupEntry is not null))
                     .Select(s => s.Name)];

    /// <summary>
    /// Designer round 33 — the padding each face takes when the setup states none. A driven problem that does not ask for a
    /// radiation pattern, built on a dielectric under its top metal, pads by multiples of that dielectric's height H — the
    /// length its field actually spreads by — so each axis is sized by what lies along it, never by the board's largest
    /// extent. Never more than <see cref="DefaultPadding"/>'s wavelength rule, which every other problem still takes.
    /// </summary>
    public static Em3dDefaultPadding DefaultFacePadding(EmSetup setup, double fMin, double largestExtentM, double? heightM,
                                                        double? heightBelowM = null)
    {
        var (cap, basis, brief) = DefaultPadding(setup, fMin, largestExtentM);
        bool byHeight = ByHeightApplies(setup) && heightM is > 0;
        return new Em3dDefaultPadding(cap, basis, byHeight ? heightM : null, TopByExtent: ByHeightApplies(setup),
                                      HeightBelowM: byHeight && heightBelowM is > 0 ? heightBelowM : null, CapShort: brief);
    }

    /// <summary>Designer round 33 — the problems the H rule and the floor on an unbroken bottom ground plane are for: driven,
    /// not asking for a radiation pattern. A radiator keeps every face absorbing and λ/4 of air (its far field needs both);
    /// a static or eigenmode solve keeps today's rules.</summary>
    public static bool ByHeightApplies(EmSetup setup)
        => !setup.IsStatic3D && setup.Problem3D == Em3dProblemType.Driven && !setup.RadiationPattern;

    /// <summary>
    /// Designer round 33 — H: from the bottom of the top metal down to the next metal below it (a plane, or the PEC floor),
    /// when a dielectric lies directly under that metal. Null when the top metal sits on no dielectric (a bond wire in air,
    /// a lid, a package's frame), and the wavelength rule applies. Only flat metal counts — a via barrel, a wire or a ball
    /// is not "the top metal" — so a horizontal sheet counts as metal of no thickness.
    /// </summary>
    public static double? PaddingHeight(IEnumerable<(double Z0, double Z1)> flatMetal, IEnumerable<(double Z0, double Z1)> dielectric,
                                        double? floorZ)
    {
        const double tol = 1e-9;
        var m = flatMetal.ToList();
        var d = dielectric.ToList();
        if (m.Count == 0 || d.Count == 0) return null;
        double top = m.Max(x => x.Z1);
        double zb = m.Where(x => x.Z1 >= top - tol).Min(x => x.Z0);
        if (!d.Any(x => Math.Abs(x.Z1 - zb) <= tol)) return null;
        var below = m.Where(x => x.Z1 <= zb - tol).Select(x => x.Z1).ToList();
        double ground = below.Count > 0 ? below.Max() : floorZ ?? d.Min(x => x.Z0);
        double h = zb - ground;
        return h > tol ? h : null;
    }

    /// <summary><see cref="PaddingHeight(IEnumerable{ValueTuple{double, double}}, IEnumerable{ValueTuple{double, double}}, double?)"/>
    /// read off built solids and sheets — what a <c>.c3d</c>'s problem assembly has.</summary>
    public static double? PaddingHeight(IEnumerable<Em3dSolid> solids, IEnumerable<Em3dSheet> sheets, double? floorZ)
    {
        var (metal, diel) = FlatMetalAndDielectric(solids, sheets);
        return PaddingHeight(metal, diel, floorZ);
    }

    /// <summary><see cref="PaddingHeightBelow(IEnumerable{ValueTuple{double, double}}, IEnumerable{ValueTuple{double, double}})"/>
    /// read off built solids and sheets.</summary>
    public static double? PaddingHeightBelow(IEnumerable<Em3dSolid> solids, IEnumerable<Em3dSheet> sheets)
    {
        var (metal, diel) = FlatMetalAndDielectric(solids, sheets);
        return PaddingHeightBelow(metal, diel);
    }

    private static (List<(double, double)> Metal, List<(double, double)> Dielectric) FlatMetalAndDielectric(
        IEnumerable<Em3dSolid> solids, IEnumerable<Em3dSheet> sheets)
    {
        var all = solids.ToList();
        var metal = all.Where(s => s.Role == Em3dRole.Conductor)
                       .Select(s => s.Primitive switch
                       {
                           Em3dExtrudedPolygon e => ((double, double)?)(e.ZBottom, e.ZTop),
                           Em3dBox b => (b.Min.Z, b.Max.Z),
                           _ => null,
                       })
                       .OfType<(double, double)>()
                       .Concat(sheets.Where(s => s.Frame is null).Select(s => (s.Z, s.Z)));
        var diel = all.Where(s => s.Role == Em3dRole.Dielectric)
                      .Select(s => Em3dProblem.Bounds(s.Primitive)).Select(b => (b.Z0, b.Z1)).ToList();
        // A plated slot arrives as an extruded polygon, not a cylinder, and is a barrel all the same: metal that passes right
        // through a dielectric is not flat. Left in, one slot's top-to-bottom span became "the top metal" and H was lost.
        const double tol = 1e-9;
        var flat = metal.Where(m => !diel.Any(d => m.Item1 <= d.Z0 + tol && m.Item2 >= d.Z1 - tol)).ToList();
        return (flat, diel);
    }

    /// <summary>
    /// brief-dfr34-2 R-dfr34-2-3 — H for the floor: from the top of the BOTTOM metal up to the next metal above it, when a
    /// dielectric lies directly over that metal — <see cref="PaddingHeight(IEnumerable{ValueTuple{double, double}}, IEnumerable{ValueTuple{double, double}}, double?)"/>
    /// turned upside down. Null when the bottom metal has no dielectric over it (one metal level, metal on top of its
    /// board), and the floor then takes the top's H. Only flat metal counts.
    /// </summary>
    public static double? PaddingHeightBelow(IEnumerable<(double Z0, double Z1)> flatMetal, IEnumerable<(double Z0, double Z1)> dielectric)
    {
        const double tol = 1e-9;
        var m = flatMetal.ToList();
        var d = dielectric.ToList();
        if (m.Count == 0 || d.Count == 0) return null;
        double bottom = m.Min(x => x.Z0);
        double zt = m.Where(x => x.Z0 <= bottom + tol).Max(x => x.Z1);
        if (!d.Any(x => Math.Abs(x.Z0 - zt) <= tol)) return null;
        var above = m.Where(x => x.Z0 >= zt + tol).Select(x => x.Z0).ToList();
        double ceiling = above.Count > 0 ? above.Min() : d.Max(x => x.Z1);
        double h = ceiling - zt;
        return h > tol ? h : null;
    }

    /// <summary>The bound of what the air box pads round: every solid but the generator's air above the stack, and every
    /// sheet. The .cem generator's own air solid is built TO the box, so it is not content; a drawn air object in a .c3d is.</summary>
    public static (Point3 Lo, Point3 Hi) ContentBounds(Em3dProblem p)
    {
        if (p.PaddedContent is { } padded) return padded;      // brief-dfr32-1 — a .cem's slabs reach the box
        var b = p.Solids.Where(s => s.Name != AirSolidName).Select(s => Em3dProblem.Bounds(s.Primitive))
                 .Concat(p.Sheets.Select(s =>
                 {
                     var w = s.Outline.Select(s.World).ToList();
                     return (X0: w.Min(q => q.X), Y0: w.Min(q => q.Y), Z0: w.Min(q => q.Z),
                             X1: w.Max(q => q.X), Y1: w.Max(q => q.Y), Z1: w.Max(q => q.Z));
                 }))
                 .Concat(p.CylinderSheets.Select(s => s.WorldBounds())).ToList();      // brief-sar-8b
        if (b.Count == 0) return (p.Boundary.Min, p.Boundary.Max);
        return (new Point3(b.Min(q => q.X0), b.Min(q => q.Y0), b.Min(q => q.Z0)),
                new Point3(b.Max(q => q.X1), b.Max(q => q.Y1), b.Max(q => q.Z1)));
    }

    private static string Hz(double f) => f >= 1e9 ? Fmt(f / 1e9) + " GHz" : f >= 1e6 ? Fmt(f / 1e6) + " MHz" : Fmt(f / 1e3) + " kHz";

    private static EmAirBoxFace? FaceOf(EmAirBox b, string face) => face switch
    {
        "xmin" => b.XMin, "xmax" => b.XMax, "ymin" => b.YMin, "ymax" => b.YMax, "zmin" => b.ZMin, _ => b.ZMax,
    };

    private static string Fmt(double v) => v.ToString("G6", CultureInfo.InvariantCulture);


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
