// What hangs on the geometry, and the bookkeeping that turns it into an ElaboratedNetlist
// (railrf.md §4.3; brief-railrf-3-mesh-extractor.md R-rail3-11 … R-rail3-13,
//  brief-railrf-4-fast-extractor.md §0 "same currency").
//
// ── THIS FILE IS WHY THE FAST MODEL IS NOT A SECOND SIMULATOR ──────────────────────────────────
//
// §4.6: the fast reading "produces the same kind of netlist as §4.1, SO THE SOLVER, THE RESULT
// MODEL, THE TABLES, THE PLOTS AND THE EXPORTS ARE IDENTICAL AND ONLY THE EXTRACTOR DIFFERS. That is
// the reason the fast path is safe to have at all — it is not a second simulator, it is a second
// reading of the geometry."
//
// The two extractors differ in exactly one thing: HOW THE COPPER IS PRICED. PdnMeshExtractor stages
// one resistor per cell edge; PdnGraphExtractor stages one per trace section and a coarse mesh over
// the pours. Everything after that — the vias, the ground choice, the series parts, the shunts, the
// sources, the load ports, the ties, the island drop and the node numbering — happens HERE, ONCE,
// for both of them.
//
// A SECOND COPY OF THIS CLASS IS THE DEFECT THIS FILE EXISTS AGAINST. Two copies would drift on the
// first change to any of it, and the symptom would be two answers that differ by something other
// than the copper — which is precisely the comparison §2.9's fourth rule asks a user to trust.
//
// ── IT NEVER SOLVES ANYTHING ───────────────────────────────────────────────────────────────────
//
// Brief 5 owns the solve. Nothing here factorises anything (R-rail3-2), and the source scan in
// tests/Ui.Tests/RailRf/PdnMeshExtractorTests.cs holds it shut.

using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.RailRf;

namespace CircuitRF.Design.Layout.Pdn;

/// <summary>
/// What <see cref="PdnAssembly"/> needs to know about a reading of the copper: how many nodes there
/// are, where each one sits, and which node a coordinate lands on.
///
/// <para><b>Deliberately small, and deliberately says nothing about a grid.</b> A mesh answers these
/// from cells; the graph answers them from sections, junctions and coarse pour cells. An interface
/// that mentioned a grid would have forced the graph to pretend to be a mesh — and a
/// <see cref="PdnCellRef"/> is already the right currency, because briefs 8 and 15 colour a board
/// from it and a node that cannot name a place cannot be coloured.</para>
/// </summary>
internal interface IPdnNodeSource
{
    /// <summary>How many nodes this reading handed out. Synthetic nodes (a source's internal node)
    /// are numbered above it.</summary>
    int NodeTotal { get; }

    /// <summary>Where a node sits, or null for a node this reading did not hand out.</summary>
    PdnCellRef? CellOfNode(int node);

    /// <summary>The node of <paramref name="layer"/>'s copper covering the point, or -1. What a via
    /// resolves its two ends through.</summary>
    int NodeOnLayer(LayerKey layer, long x, long y);

    /// <summary>Every node covering the point on the conductors of the requested side. More than one
    /// where a pad's coordinate lands on several layers of the rail.</summary>
    List<int> NodesAt(long x, long y, bool isReference);

    /// <summary>The nearest reference node to a point, for a return that has no copper directly under
    /// the pad — an antipad, a split, a keepout. The distance is REPORTED by the caller, never
    /// silent.</summary>
    int NearestReferenceNode(long x, long y, out long distanceDbu);

    /// <summary>Every node in this reading's own deterministic order, so a node's NAME is the place a
    /// reader would point at rather than whichever element happened to mention it first.</summary>
    IEnumerable<int> NodesInNaturalOrder { get; }

    /// <summary>What to call a node, or null to let the assembly name it as an internal one.</summary>
    string? NameOfNode(int node);
}

/// <summary>One element, before node numbering.</summary>
internal sealed record PdnStaged(
    string Type, string Path, int[] Raw,
    Dictionary<string, Value> Params, ComponentModel Model,
    PdnOriginKind Kind, string Description,
    PdnCellRef? From, PdnCellRef? To, string? Refdes,
    double? ResistanceOhms, double? LengthMetres = null, double? WidthMetres = null,
    PdnViaBarrel? Barrel = null, double? InductanceHenries = null,
    (long X, long Y)? At = null);

internal sealed class PdnAssembly
{
    private readonly PdnExtractionRequest _req;
    private readonly IPdnNodeSource _nodes;
    private readonly PdnModelKind _model;
    private readonly double _celsius;
    private readonly List<string> _notes;
    private readonly List<string> _diagnostics;

    private readonly List<PdnStaged> _staged = [];
    private readonly List<PdnPortBinding> _ports = [];
    private readonly UnionFind _tie;
    private int _synthetic;
    private int _ground = -1;

    private readonly List<PdnElementOrigin> _origins = [];
    private readonly Dictionary<int, PdnCellRef> _nodeCells = [];
    private ElaboratedNetlist? _netlist;

    public string ReferencePoint { get; private set; } = "";

    /// <summary>
    /// How many holes carry no barrel because their layer span could not be resolved.
    ///
    /// <para><b>Carried onto the provenance, not only into a diagnostic sentence</b> — R-rail6-5's
    /// via check reads the RESULT, and a transition whose span is unresolved must produce a note and
    /// no flag rather than a flag computed from an assumed 1.6 mm span.</para>
    /// </summary>
    public int UnresolvedViaSpans { get; private set; }

    public PdnAssembly(
        PdnExtractionRequest req, IPdnNodeSource nodes, PdnModelKind model,
        double celsius, List<string> notes, List<string> diagnostics)
    {
        _req = req;
        _model = model;
        _nodes = nodes;
        _celsius = celsius;
        _notes = notes;
        _diagnostics = diagnostics;

        _synthetic = nodes.NodeTotal;
        _tie = new UnionFind(nodes.NodeTotal + 4 * (req.Rail.Sources.Count + req.Rail.Loads.Count) + 16);
    }

    /// <summary>Null when the netlist was built, or the refusal sentence.</summary>
    public string? Build()
    {
        StampVias();

        if (ChooseGround() is { } groundRefusal) return groundRefusal;
        if (StampSeriesElements() is { } seriesRefusal) return seriesRefusal;
        if (StampShunts() is { } shuntRefusal) return shuntRefusal;
        if (StampSources() is { } sourceRefusal) return sourceRefusal;
        if (StampLoads() is { } loadRefusal) return loadRefusal;
        if (FloatingRefusal() is { } floating) return floating;

        Emit();
        return null;
    }

    /// <summary>
    /// R-rail31-4's backstop — <b>never hand the solver a floating network</b>. Every port's power
    /// and reference node, and every source's terminals, must be in the ground node's connected
    /// component of what was STAMPED.
    /// </summary>
    /// <remarks>
    /// <b>The galvanic check is not enough, and this is why it is here and not only there.</b> The
    /// walk sees copper; the netlist is a READING of it — a thinned skeleton, a coarse mesh, and the
    /// reference layer's copper only, with whatever joins two return pieces on another layer left
    /// out. On the reported board the reading split a return the copper did not, and a 30 mA load
    /// driving a network with no path back to the source came out of the DC solve as −150 MV: a
    /// number, from gmin alone.
    ///
    /// <para><b>What conducts is what carries current between its terminals</b>: copper, vias, series
    /// parts and the source's own branch. A load's current source and an observation port carry none
    /// at DC (the DC engine leaves a port inert), so they are not a path however they are drawn —
    /// counting them would let the very load that floats vouch for itself. A capacitor is a path above
    /// DC only. Islands carrying no port stay what they were: a diagnostic, and dropped by
    /// <see cref="Emit"/>.</para>
    /// </remarks>
    private string? FloatingRefusal()
    {
        bool ac = _req.FrequencyHz > 0;
        var joined = new UnionFind(_tie.Capacity);
        foreach (var s in _staged)
        {
            bool conducts = s.Kind switch
            {
                PdnOriginKind.LoadCurrent or PdnOriginKind.Port => false,
                PdnOriginKind.Shunt or PdnOriginKind.PlaneShunt => ac,
                _ => true,
            };
            if (conducts) joined.Union(_tie.Find(s.Raw[0]), _tie.Find(s.Raw[1]));
        }

        int ground = joined.Find(_tie.Find(_ground));
        bool Grounded(int raw) => joined.Find(_tie.Find(raw)) == ground;

        string model = _model == PdnModelKind.Accurate ? "Accurate reading's mesh" : "Fast reading's graph";
        string what = $"Rail '{_req.Rail.Name}'";

        // A source's RETURN; its power side is asked through the ports it feeds, below, because a
        // source that stamped nothing leaves that side unjoined and the port is what can say why.
        foreach (var (name, nr) in _sourceReturns)
            if (!Grounded(nr))
                return $"{what}'s source {name} returns on reference copper that the {model} does " +
                       "not join to the reference point, so the rail has two returns and no answer. " +
                       FloatingHint;

        // The power side is asked only of a rail something DRIVES — a source that stamped a path, or
        // a load that draws. A rail with neither is an observation of its copper (the impedance
        // fixtures, a cell-size check at DC): nothing flows, and it floats harmlessly.
        bool driven = _staged.Any(s => s.Kind is PdnOriginKind.SourceBranch
                                              or PdnOriginKind.SourceResistance
                                              or PdnOriginKind.LoadCurrent);

        foreach (var port in _ports)
        {
            if (!Grounded(port.ReferenceNode))
                return $"{what}'s load {port.Name} returns on reference copper that the {model} does " +
                       "not join to the source's return — the netlist is two circuits, and solving it " +
                       "would drive the load's current into a network with no path back. " + FloatingHint;
            if (driven && !Grounded(port.PowerNode))
                return $"{what}'s load {port.Name} is on rail copper that nothing in the {model} " +
                       "joins to a source at DC — a source with neither a voltage nor a series " +
                       "resistance stamps nothing, and a capacitor is no path at DC. " + FloatingHint;
        }

        return null;
    }

    private const string FloatingHint =
        "This is the reading, not necessarily the copper: the reference layer's copper is read alone, " +
        "so return pieces joined only through another layer are apart here. Join them on the " +
        "reference layer, or take the reference as filled to the board outline (optimistic, and " +
        "said so), or run the other model.";

    private readonly List<(string Name, int Reference)> _sourceReturns = [];

    public PdnNetlist Finish(PdnProvenance provenance) => new()
    {
        Netlist = _netlist!,
        Origins = _origins,
        NodeCells = _nodeCells,
        Ports = _ports,
        Provenance = provenance,
    };

    // ── the copper, which is the ONE thing the two extractors do differently ────────────────────

    /// <summary>
    /// Stages one resistance of copper — a mesh cell edge from <see cref="PdnMeshExtractor"/>, a
    /// trace section or a coarse pour edge from <see cref="PdnGraphExtractor"/>.
    ///
    /// <para><b>Called BEFORE <see cref="Build"/></b>, because the order elements are staged in is the
    /// order they appear in the netlist and in the ranked breakdown, and copper comes first in both.
    /// This is the whole of the two extractors' difference; everything else about a railRF netlist is
    /// below.</para>
    /// </summary>
    public void StageCopper(
        string path, int a, int b, double ohms,
        string description, PdnCellRef? from, PdnCellRef? to,
        PdnOriginKind kind = PdnOriginKind.MeshEdge,
        double? lengthMetres = null, double? widthMetres = null,
        double? inductanceHenries = null)
    {
        if (!(ohms > 0) || double.IsInfinity(ohms) || a == b) return;

        // ── R-rail13-1: ω = 0 is a RESISTOR, and that is not an optimisation ───────────────────
        //
        // §2.8: "Set ω = 0 and the inductance and the shunt branch both vanish, and what is left is
        // a purely resistive mesh: real, symmetric, positive-definite and fast." So at DC the
        // element IS a resistor — one node-pair stamp, no branch unknown, and a system the DC
        // engine solves as an SPD one. An inductor with L = 0 would be the same PHYSICS and a
        // different MATRIX: every cell edge would add a Group-2 branch current, which on a mesh of
        // a hundred thousand edges is a hundred thousand extra unknowns for an answer already
        // known to be the resistive one.
        //
        // Above DC it is InductorModel with its optional R=, which stamps exactly R + jωL on one
        // branch. NO NEW ELEMENT EXISTS (overview §1f). The brief names SeriesRlcModel for this and
        // it is the wrong one of the two: SRLC REQUIRES a C, and C = 0 is an OPEN at every
        // frequency — a mesh of open circuits, silently. InductorModel's C= is optional and absent
        // here, which is the R + jωL §4.1 asks for.
        bool ac = inductanceHenries is { } l && l > 0;

        var parameters = new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(ohms) };
        if (ac) parameters["L"] = new Value(inductanceHenries!.Value);

        _staged.Add(new PdnStaged(
            ac ? "L" : "R", path, [a, b], parameters,
            ac ? new InductorModel() : new ResistorModel(),
            kind, description, from, to, null, ohms, lengthMetres, widthMetres,
            null, inductanceHenries));
    }

    /// <summary>
    /// Stages one cell's §4.1 shunt branch — <c>C</c> to the reference plane and, where the
    /// dielectric is lossy, the <c>G = ωC·tan δ</c> across it (R-rail14-2).
    ///
    /// <para><b>Two Group-1 elements and no new unknown, which is why it is not one element.</b>
    /// A <c>CapacitorModel</c> stamps jωC and a <c>ResistorModel</c> stamps 1/R, both straight onto
    /// the admittance matrix; together they are the <c>ωC(tan δ + j)</c> §4.1 asks for.
    /// <c>ParallelRlcModel</c> would say the same thing in one line and is the wrong element here:
    /// it always opens a Group-2 branch for its inductor, so a cavity mesh of a few thousand cells
    /// would carry a few thousand extra branch currents for an L that is identically zero. NO NEW
    /// <c>ComponentModel</c> EXISTS ANYWHERE IN THIS SERIES (overview §1f) and none is needed.</para>
    ///
    /// <para><b>The loss is stamped as a RESISTANCE at this frequency's own ω</b>, exactly as the
    /// skin-effect sheet resistance is: the extraction is FOR one frequency (§2.8), and the sweep
    /// re-extracts. A G held from one point to the next would make tan δ fall as 1/f and flatten
    /// every resonance in the band above it.</para>
    /// </summary>
    public void StageCavityShunt(
        string path, int a, int b, double capacitanceFarads, double conductanceSiemens,
        string description, PdnCellRef? from, PdnCellRef? to)
    {
        if (!(capacitanceFarads > 0) || a == b) return;

        _staged.Add(new PdnStaged(
            "C", path + ".c", [a, b],
            new Dictionary<string, Value>(StringComparer.Ordinal) { ["C"] = new Value(capacitanceFarads) },
            new CapacitorModel(), PdnOriginKind.PlaneShunt,
            $"{description} — {capacitanceFarads * 1e12:0.###} pF to the reference plane",
            from, to, null, null));

        if (!(conductanceSiemens > 0)) return;

        _staged.Add(new PdnStaged(
            "R", path + ".g", [a, b],
            new Dictionary<string, Value>(StringComparer.Ordinal)
            { ["R"] = new Value(1.0 / conductanceSiemens) },
            new ResistorModel(), PdnOriginKind.PlaneShunt,
            $"{description} — dielectric loss, G = ωC·tan δ = {conductanceSiemens * 1e6:0.###} µS",
            from, to, null,
            // NOT reported as a resistance the ranked breakdown can sort on: §2.4 ranks where the DC
            // DROP is, this element carries none (it does not exist at ω = 0), and a megohm shunt
            // sorted into that table would head it while contributing nothing.
            null));
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

        double rho = PdnMeshExtractor.ResistivityAt(CopperSigma(tech), _celsius);
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

            _staged.Add(new PdnStaged(
                "R", $"via.{via.X}.{via.Y}",
                [na, nb], new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(r) },
                new ResistorModel(), PdnOriginKind.Via,
                $"a {drill * 1e3:0.###} mm plated via over {span * 1e3:0.###} mm, " +
                PdnViaModel.DescribePlating(plating, basis),
                CellOf(na), CellOf(nb), null, r,
                // The barrel travels with the element in STRUCTURE, because brief 6 computes a
                // current limit from these four terms and the sentence above cannot be read back.
                // fromKeys/toKeys are layer LISTS; the barrel names the one each end landed on.
                Barrel: new PdnViaBarrel(
                    via.X, via.Y, LayerOfNode(fromKeys, via.X, via.Y), LayerOfNode(toKeys, via.X, via.Y),
                    drill, plating, basis, span, r)));
            stamped++;
        }

        UnresolvedViaSpans = unresolved;

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
            int n = _nodes.NodeOnLayer(key, x, y);
            if (n >= 0) return n;
        }
        return -1;
    }

    /// <summary>
    /// WHICH of a conductor's drawing layers the barrel actually landed on — the same walk
    /// <see cref="NodeOn"/> makes, reporting the key rather than the node.
    ///
    /// <para>A stackup conductor may declare several drawing layers, and brief 6 groups barrels into
    /// transitions by the layer pair they join: a group keyed on the conductor's FIRST declared layer
    /// rather than the one this hole landed on would merge two transitions that are not the same
    /// one.</para>
    /// </summary>
    private LayerKey LayerOfNode(IReadOnlyList<LayerKey> keys, long x, long y)
    {
        foreach (var key in keys)
            if (_nodes.NodeOnLayer(key, x, y) >= 0) return key;
        return keys.Count > 0 ? keys[0] : default;
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
                $"the reference conductor under {anchor.Describe(_req.LengthFormat)}, this rail's " +
                $"first {what}" +
                (away > 0
                    ? $" — the nearest reference copper is {_req.LengthFormat.Length(away)} away, " +
                      "because there is none directly under that pad"
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
                return PdnAttachments.RefusalForUnresolved(
                    $"{where}'s first end", part.A, 0, _req.LengthFormat);
            if (b.Count == 0)
                return PdnAttachments.RefusalForUnresolved(
                    $"{where}'s second end", part.B, 0, _req.LengthFormat);

            int na = Merge(a), nb = Merge(b);
            if (na == nb)
            {
                _diagnostics.Add(
                    $"{where} bridges {part.A.Describe(_req.LengthFormat)} to "
                  + $"{part.B.Describe(_req.LengthFormat)}, which are already " +
                    "one piece of copper. Its resistance is in the netlist and carries no current.");
            }

            _staged.Add(new PdnStaged(
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
                    $"{part.Refdes} is anchored at {part.Anchor.Describe(_req.LengthFormat)}, which is not on this " +
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

            _staged.Add(new PdnStaged(
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
            string name = src.Anchor.Describe(_req.LengthFormat);
            string where = $"Rail '{_req.Rail.Name}'s source {k + 1} ({name})";

            var power = PowerNodesFor(src.Anchor);
            if (power.Count == 0)
                return PdnAttachments.RefusalForUnresolved(where, src.Anchor, 0, _req.LengthFormat);

            var reference = ReferenceNodesFor(src.Anchor, out _);
            if (reference.Count == 0)
                return $"{where} has no reference copper anywhere under it, so its return has " +
                       "nowhere to go. Name the layer the return actually runs on.";

            int np = Merge(power), nr = Merge(reference);
            double? r = src.SeriesResistanceOhms;
            _sourceReturns.Add((name, nr));

            if (src.OpenCircuitVoltageV is not { } volts)
            {
                // Null is not zero: a source with no stated voltage is a branch whose IMPEDANCE
                // is known and whose LEVEL is not. Its resistance goes in — the frequency answer
                // uses it — and no DC level is invented (railrf.md §2.2).
                if (r is { } ohms)
                    _staged.Add(new PdnStaged(
                        "R", $"source.{k + 1}.r", [np, nr],
                        new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(ohms) },
                        new ResistorModel(), PdnOriginKind.SourceResistance,
                        $"{name}'s series resistance, {ohms * 1e3:0.###} mΩ",
                        CellOf(np), CellOf(nr), src.Anchor.Refdes, ohms,
                        At: AnchorPoint(src.Anchor)));

                _notes.Add(
                    $"{name} states no open-circuit voltage, so it contributes its impedance and no " +
                    "DC level. The DC answer is a drop across this rail, not a voltage at its loads.");
                continue;
            }

            int internalNode = r is { } series && series > 0 ? _synthetic++ : np;

            _staged.Add(new PdnStaged(
                "Vdc", $"source.{k + 1}.v", [internalNode, nr],
                new Dictionary<string, Value>(StringComparer.Ordinal) { ["Vdc"] = new Value(volts) },
                new VdcModel(), PdnOriginKind.SourceBranch,
                $"{name} at {volts:0.###} V open circuit",
                null, CellOf(nr), src.Anchor.Refdes, null, At: AnchorPoint(src.Anchor)));

            if (internalNode != np)
                _staged.Add(new PdnStaged(
                    "R", $"source.{k + 1}.r", [internalNode, np],
                    new Dictionary<string, Value>(StringComparer.Ordinal) { ["R"] = new Value(r!.Value) },
                    new ResistorModel(), PdnOriginKind.SourceResistance,
                    $"{name}'s series resistance, {r.Value * 1e3:0.###} mΩ",
                    null, CellOf(np), src.Anchor.Refdes, r.Value, At: AnchorPoint(src.Anchor)));
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
            string name = load.Anchor.Describe(_req.LengthFormat);
            string where = $"Rail '{_req.Rail.Name}'s load {k + 1} ({name})";

            var power = PowerNodesFor(load.Anchor);
            if (power.Count == 0)
                return PdnAttachments.RefusalForUnresolved(where, load.Anchor, 0, _req.LengthFormat);

            var reference = ReferenceNodesFor(load.Anchor, out _);
            if (reference.Count == 0)
                return $"{where} has no reference copper anywhere under it, so there is nothing to " +
                       "measure its impedance against. Name the layer the return actually runs on.";

            int np = Merge(power), nr = Merge(reference);

            var cells = new List<PdnCellRef>();
            foreach (int n in power) if (CellOf(n) is { } c) cells.Add(c);
            foreach (int n in reference) if (CellOf(n) is { } c) cells.Add(c);

            if (load.DcCurrentA is { } amps)
                _staged.Add(new PdnStaged(
                    // Injected INTO the reference and drawn OUT of the rail, which is what a load
                    // does. The engine's current-source convention delivers J to Nodes[0].
                    "I_1Tone", $"load.{k + 1}.i", [nr, np],
                    new Dictionary<string, Value>(StringComparer.Ordinal) { ["Idc"] = new Value(amps) },
                    new CurrentToneSourceModel([], amps), PdnOriginKind.LoadCurrent,
                    $"{name} drawing {amps * 1e3:0.###} mA",
                    CellOf(np), CellOf(nr), load.Anchor.Refdes, null));

            // ── THE NAME HAS NO DOT IN IT, AND THAT IS NOT A STYLE CHOICE ─────────────────────
            //
            // SParameterEngine treats a Port whose instance path contains a '.' as a BURIED port
            // inside a sub-cell and skips it — the Layer 2 scoping rule. An extraction spelling this
            // "port.1" therefore reaches the engine with NO PORTS AT ALL and is refused with a
            // sentence about placing Terms in a testbench, which is not what went wrong. §3 of the
            // design note is that the deliverable is a netlist every downstream consumer already
            // sweeps; a port the sweep cannot see makes that false. PdnSweep's own lumped assembly
            // already spells it "port1" for the same reason, and the two now agree.
            _staged.Add(new PdnStaged(
                "Port", $"port{k + 1}", [np, nr],
                new Dictionary<string, Value>(StringComparer.Ordinal) { ["Num"] = new Value(k + 1) },
                new PortModel(), PdnOriginKind.Port,
                load.DcCurrentA is null
                    ? $"{name}, an observation port — it states no current and draws none"
                    : $"{name}, observed",
                CellOf(np), CellOf(nr), load.Anchor.Refdes, null));

            _ports.Add(new PdnPortBinding(k, name, load.Anchor, np, nr, cells, load.DcCurrentA,
                                          AnchorPoint(load.Anchor)));
        }

        return null;
    }

    // ── node bookkeeping ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Where an anchor actually is on the board — the centroid of the pads it names, or null where
    /// it names none.
    /// </summary>
    /// <remarks>
    /// <b>This is what a marker is drawn at, and it is not <c>CellOf(Merge(nodes))</c>.</b> Merging
    /// ties a pin field into one node and the cell that node reports is the union-find
    /// REPRESENTATIVE — one arbitrary member of the tied set, which on a via-stitched rail is
    /// routinely on another layer and several millimetres away. Nothing about that place is wrong
    /// electrically; it is simply not a place, and a mark drawn there moves between two readings of
    /// one unchanged design. The pads do not move.
    /// </remarks>
    private (long X, long Y)? AnchorPoint(RailPortAnchor anchor)
    {
        var pads = PdnAttachments.Resolve(anchor, _req.Pads);
        if (pads.Count == 0) return null;

        long sx = 0, sy = 0;
        foreach (var (x, y) in pads) { sx += x; sy += y; }
        return (sx / pads.Count, sy / pads.Count);
    }

    private List<int> PowerNodesFor(RailPortAnchor anchor)
    {
        var nodes = new List<int>();
        foreach (var (x, y) in PdnAttachments.Resolve(anchor, _req.Pads))
            foreach (int n in _nodes.NodesAt(x, y, isReference: false))
                if (!nodes.Contains(n)) nodes.Add(n);
        return nodes;
    }

    private List<int> ReferenceNodesFor(RailPortAnchor anchor, out long distanceDbu)
    {
        distanceDbu = 0;
        var pads = PdnAttachments.Resolve(anchor, _req.Pads);
        var nodes = new List<int>();

        foreach (var (x, y) in pads)
            foreach (int n in _nodes.NodesAt(x, y, isReference: true))
                if (!nodes.Contains(n)) nodes.Add(n);

        if (nodes.Count > 0 || pads.Count == 0) return nodes;

        // No reference copper directly under the pad — an antipad, a split, a keepout. The
        // NEAREST reference cell is the honest stand-in and the distance is reported, because a
        // return that starts 3 mm away is a finding rather than a detail.
        var (x0, y0) = pads[0];
        int nearest = _nodes.NearestReferenceNode(x0, y0, out distanceDbu);
        if (nearest >= 0)
        {
            nodes.Add(nearest);
            _diagnostics.Add(
                $"There is no reference copper under {anchor.Describe(_req.LengthFormat)}; the return " +
                $"was attached to the nearest reference cell, {_req.LengthFormat.Length(distanceDbu)} " +
                "away. The spreading between the two is NOT in this answer.");
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

    private PdnCellRef? CellOf(int raw) => _nodes.CellOfNode(raw);

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
        _nodeCells[0] = _nodes.CellOfNode(_ground) ?? default;

        int Index(int raw)
        {
            int rep = _tie.Find(raw);
            if (final.TryGetValue(rep, out int idx)) return idx;
            idx = netlist.Nodes.GetOrAssign(NameOf(rep, raw));
            final[rep] = idx;
            if (_nodes.CellOfNode(raw) is { } cell) _nodeCells[idx] = cell;
            return idx;
        }

        // Names in the GEOMETRY's own order first, so a node's name is the cell a reader would
        // point at rather than whichever element happened to mention it first. The mesh's order is
        // layer then row then column; the graph's is section then junction. Either way it is the
        // node source's order and never a dictionary's.
        foreach (int n in _nodes.NodesInNaturalOrder)
            if (Keep(n)) Index(n);

        int dropped = 0;

        foreach (var s in _staged)
        {
            if (!Keep(s.Raw[0]) || !Keep(s.Raw[1])) { dropped++; continue; }

            int componentIndex = netlist.Components.Count;
            netlist.AddComponent(new ElaboratedComponent(
                s.Type, s.Path, [Index(s.Raw[0]), Index(s.Raw[1])], s.Params, s.Model));

            _origins.Add(new PdnElementOrigin(
                componentIndex, s.Kind, s.Description, s.From, s.To, s.Refdes,
                s.ResistanceOhms, s.LengthMetres, s.WidthMetres, s.Barrel, s.InductanceHenries,
                s.At));
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

    private string NameOf(int rep, int raw) => _nodes.NameOfNode(raw) ?? $"internal.{rep}";

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
