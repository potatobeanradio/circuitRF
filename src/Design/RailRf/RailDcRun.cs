// The DC answer P0 exists for: the rail set, in dependency order, one extraction and one solve each
// (docs/sonnet-briefs/brief-railrf-5-dc-solve.md; docs/design/railrf.md §2.2, §2.4, §4.4, §7).
//
// ── NOTHING HERE IS A SOLVER ───────────────────────────────────────────────────────────────────
//
// §4.4: "Assemble one sparse MNA system and solve via CSparse's LU — THE SAME NUMERICAL LAYER every
// other circuitRF analysis uses." So this file builds an ElaboratedNetlist through brief 3's or
// brief 4's extractor, hands it to LinearDcEngine, and reads the answer. It writes no assembly code,
// no factorisation and no result type of its own beyond the domain-shaped RailDcResult; the numeric
// result is a DataSet packed by DcResultPacker, exactly as a schematic's DC run is.
//
// ── THE CYCLE REFUSAL IS THE LOAD-BEARING HALF ─────────────────────────────────────────────────
//
// §9, and R-rail5-8: solving the rails TOGETHER is a different model, it needs exactly the data
// §8.2 records as frequently impossible to obtain, and "it would be entered by accident the first
// time someone asked for a cycle in the order to be supported". So a cycle is a refusal naming both
// rails and the refdes that closes it — RailOrder writes that sentence — and there is no iteration
// count, no relaxation and no partial answer.

using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Engine;
using CircuitRF.Engine.Pdn;
using Clipper2Lib;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// Everything one DC run reads: the document, the artwork it refers to, and how to read the copper.
///
/// <para>The board-level inputs are stated ONCE and the rail is what varies — which is what makes
/// "one extraction and one solve per rail" a loop rather than a caller's responsibility, and what
/// lets the chain feed a downstream rail's source from the rail above without the caller
/// re-supplying anything.</para>
/// </summary>
public sealed class RailDcRequest
{
    /// <summary>The rail set. <see cref="RailOrder"/> decides the order; this is not it.</summary>
    public required RailDocument Document { get; init; }

    /// <summary>The artwork, flattened to shapes in DBU.</summary>
    public required IReadOnlyList<LayoutShape> Shapes { get; init; }

    /// <summary>The stackup.</summary>
    public required Technology Technology { get; init; }

    /// <summary>The artwork's DBU resolution.</summary>
    public int DbuPerMicron { get; init; } = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>The board's pads, as the netlist or a placement join knows them.</summary>
    public IReadOnlyList<PdnPad> Pads { get; init; } = [];

    /// <summary>What the board netlist knows about net identity.</summary>
    public IReadOnlyList<PdnNetPoint> NetPoints { get; init; } = [];

    /// <summary>The reference return's net, where one is named.</summary>
    public string? ReferenceNet { get; init; }

    /// <summary>The board outline. Required by <see cref="RailReferenceExtent.FilledToOutline"/>.</summary>
    public Paths64? BoardOutline { get; init; }

    /// <summary>What bridges the gaps the copper leaves at every pad — the protection FET, the
    /// ferrite, a link.</summary>
    public IReadOnlyList<PdnSeriesElement> SeriesElements { get; init; } = [];

    /// <summary>The decoupling. In the netlist, carrying no DC path.</summary>
    public IReadOnlyList<PdnShuntPart> ShuntParts { get; init; } = [];

    /// <summary>Which of §2.9's two readings to take. <see cref="PdnModelKind.Fast"/> is the
    /// default, and every result says which produced it.</summary>
    public PdnModelKind Model { get; init; } = PdnModelKind.Fast;

    /// <summary>How finely, and where — read by the accurate reading only.</summary>
    public PdnMeshSettings Mesh { get; init; } = new();

    /// <summary>How the fast reading traces and how coarsely it meshes what it will not trace.</summary>
    public PdnGraphSettings Graph { get; init; } = new();
}

/// <summary>
/// Everything one run produced. <see cref="Refusal"/> non-null means NOTHING was solved — the
/// contract <see cref="PdnExtraction"/> already states.
/// </summary>
/// <param name="Refusal">Why nothing was solved, or null.</param>
/// <param name="Rails">One result per rail, in SOLVE order.</param>
/// <param name="Order">The solve order <see cref="RailOrder"/> computed.</param>
/// <param name="Diagnostics">Everything worth saying that did not stop the run.</param>
public sealed record RailDcRunResult(
    string? Refusal,
    IReadOnlyList<RailDcResult> Rails,
    IReadOnlyList<string> Order,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>The result for that rail, or null. Case-insensitive, as <c>--rail</c> is.</summary>
    public RailDcResult? Rail(string name) =>
        Rails.FirstOrDefault(r => string.Equals(r.RailName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every regulator the chain looked at, across every rail.</summary>
    public IEnumerable<RailRegulatorHeadroom> Regulators => Rails.SelectMany(r => r.Regulators);

    /// <summary>
    /// The regulators that stated no minimum input voltage — <b>listed, so the absence is visible
    /// rather than read as a pass</b> (R-rail5-9). railRF reports the drop always and the headroom
    /// finding only where a minimum was stated.
    /// </summary>
    public IEnumerable<RailRegulatorHeadroom> RegulatorsWithNoMinimum =>
        Regulators.Where(r => r.MinimumInputVoltageV is null);

    /// <summary>Every finding on every rail, in solve order.</summary>
    public IEnumerable<string> Findings => Rails.SelectMany(r => r.Findings);

    internal static RailDcRunResult Refused(string why) => new(why, [], [], []);
}

/// <summary>The rail set, solved in dependency order.</summary>
public static class RailDcRun
{
    /// <summary>
    /// Solves every rail of <paramref name="request"/>'s document, upstream first, or refuses and
    /// says why.
    ///
    /// <para><b>The chain is the reason the order exists</b> (§2.2): a regulator is a load on its
    /// input rail and a source on its output rail, and railRF solves them in that order so the
    /// regulator's input voltage is the UPSTREAM ANSWER rather than a nominal.</para>
    /// </summary>
    public static RailDcRunResult Run(RailDcRequest request)
    {
        var doc = request.Document;

        if (doc.Refusal() is { } docRefusal) return RailDcRunResult.Refused(docRefusal);

        var order = RailOrder.Resolve(doc);
        if (!order.Ok) return RailDcRunResult.Refused(order.Refusal!);

        RailOrder.Edges(doc, out var edges);

        var results = new List<RailDcResult>(order.Order.Count);
        var diagnostics = new List<string>();

        foreach (string railName in order.Order)
        {
            var spec = doc.Rail(railName);
            if (spec is null) continue;

            var chained = ChainStart(spec, edges, results, out var solved);
            var toSolve = chained is null ? spec : WithSourceLevel(spec, chained);

            var extraction = request.Model == PdnModelKind.Accurate
                ? PdnMeshExtractor.Extract(RequestFor(request, toSolve))
                : PdnGraphExtractor.Extract(RequestFor(request, toSolve));

            diagnostics.AddRange(extraction.Diagnostics.Select(d => $"[{railName}] {d}"));

            if (extraction.Refusal is { } why)
                return RailDcRunResult.Refused($"Rail '{railName}' was not solved. {why}");

            var pdn = extraction.Netlist!;
            var solve = LinearDcEngine.Run(pdn.Netlist);
            if (solve.Refusal is { } solveRefusal)
                return RailDcRunResult.Refused($"Rail '{railName}' was not solved. {solveRefusal}");

            results.Add(Assemble(request, spec, pdn, solve.Solution!, chained, edges, solved, extraction));
        }

        return new RailDcRunResult(null, results, order.Order, diagnostics);
    }

    // ── R-rail5-7: the rail chain ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Where this rail's source gets its level, when it gets it from the rail above.
    ///
    /// <para><b>Only where the row states none of its own.</b> A source row that states an
    /// open-circuit voltage is a REGULATED output and it is used exactly as stated — §2.7 is
    /// explicit that railRF does not model a regulator's forward transfer "and does not approximate
    /// it either", so the upstream answer never scales, offsets or caps a stated output. What the
    /// chain carries at DC is the headroom finding of <see cref="Headroom"/> and nothing else.</para>
    ///
    /// <para>A row that states NO voltage is the other case brief 1 made representable: a branch
    /// whose impedance is known and whose level is not. On a chained rail that level IS known — it
    /// is the answer at this part's own input pin field upstream — and carrying it through a series
    /// element whose resistance the row already states is a pass element, not a forward transfer.</para>
    /// </summary>
    private static RailChainStart? ChainStart(
        RailSpec rail, IReadOnlyList<RailOrder.RailEdge> edges,
        IReadOnlyList<RailDcResult> solvedRails, out bool upstreamSolved)
    {
        upstreamSolved = false;

        for (int k = 0; k < rail.Sources.Count; k++)
        {
            if (rail.Sources[k].Anchor.Refdes is not { Length: > 0 } refdes) continue;

            var edge = edges.FirstOrDefault(
                e => string.Equals(e.Downstream, rail.Name, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(e.Refdes, refdes, StringComparison.OrdinalIgnoreCase));
            if (edge.Refdes is null) continue;

            var upstream = solvedRails.FirstOrDefault(
                r => string.Equals(r.RailName, edge.Upstream, StringComparison.OrdinalIgnoreCase));
            if (upstream is null) continue;

            upstreamSolved = true;
            if (rail.Sources[k].OpenCircuitVoltageV is not null) continue;

            var port = upstream.Ports.FirstOrDefault(
                p => string.Equals(p.Anchor.Refdes, refdes, StringComparison.OrdinalIgnoreCase));
            if (port is null) continue;

            return new RailChainStart(upstream.RailName, refdes, port.VoltageV);
        }

        return null;
    }

    /// <summary>
    /// The same rail with the chained level applied to the source that needed one.
    ///
    /// <para>A COPY, because the document is the user's and a run may not write the upstream answer
    /// into it — a second run of the same document would then start from a number the first run
    /// computed, and the two would disagree for a reason nobody could see.</para>
    /// </summary>
    private static RailSpec WithSourceLevel(RailSpec rail, RailChainStart start)
    {
        var copy = new RailSpec
        {
            Name             = rail.Name,
            NetName          = rail.NetName,
            ReferenceLayer   = rail.ReferenceLayer,
            ReferenceExtent  = rail.ReferenceExtent,
            DropBudget       = rail.DropBudget,
            ImpedanceTarget  = rail.ImpedanceTarget,
            Band             = rail.Band,
        };

        foreach (var s in rail.Sources)
            copy.Sources.Add(
                s.OpenCircuitVoltageV is null &&
                string.Equals(s.Anchor.Refdes, start.Refdes, StringComparison.OrdinalIgnoreCase)
                    ? s with { OpenCircuitVoltageV = start.VoltageV }
                    : s);

        copy.Loads.AddRange(rail.Loads);
        copy.Aggressors.AddRange(rail.Aggressors);

        // The PARTS too. Nothing this copy is handed to reads them at DC — a capacitor bridges
        // nothing at ω = 0 and the shunt bank reaches the extraction through the REQUEST — but a
        // copy that silently holds less than the rail it copies is the shape that loses a whole
        // decoupling bank the first time this is reused at a frequency, with no error anywhere.
        copy.Parts.AddRange(rail.Parts);
        return copy;
    }

    /// <summary>
    /// Every regulator whose input pin field is a load on THIS rail, with the answer it sees.
    ///
    /// <para>Computed on the UPSTREAM rail, where the load row lives, so a regulator's headroom is
    /// reported whether or not its own output rail was reached — a chain that stopped reporting the
    /// moment a downstream rail refused would hide the finding that explains the refusal.</para>
    /// </summary>
    private static List<RailRegulatorHeadroom> Headroom(
        RailSpec rail, IReadOnlyList<RailOrder.RailEdge> edges, IReadOnlyList<RailPortDrop> ports)
    {
        var found = new List<RailRegulatorHeadroom>();

        for (int k = 0; k < rail.Loads.Count; k++)
        {
            if (rail.Loads[k].Anchor.Refdes is not { Length: > 0 } refdes) continue;

            bool isRegulator = edges.Any(
                e => string.Equals(e.Upstream, rail.Name, StringComparison.OrdinalIgnoreCase)
                  && string.Equals(e.Refdes, refdes, StringComparison.OrdinalIgnoreCase));
            if (!isRegulator) continue;

            var port = ports.FirstOrDefault(p => p.Index == k);
            if (port is null) continue;

            found.Add(new RailRegulatorHeadroom(
                refdes, rail.Name, port.VoltageV, rail.Loads[k].MinimumInputVoltageV));
        }

        return found;
    }

    // ── the extraction request, which differs per rail in exactly one field ────────────────────

    /// <summary>
    /// The extraction request for one rail.
    /// </summary>
    /// <param name="frequencyHz">What the extraction is FOR (§2.8). <b>Zero is DC and is this
    /// method's own default</b>, because the DC answer is what <see cref="Run"/> asks for; brief
    /// 15's plane run passes the frequency it wants the cavity at, which is the ONE thing it
    /// changes about the reading. Sharing this builder is what stops the two extractions differing
    /// in anything else — a second copy would drift in the reference extent or the class overrides
    /// and the modes would be of a board the drop map is not of.</param>
    internal static PdnExtractionRequest RequestFor(
        RailDcRequest request, RailSpec rail, double frequencyHz = 0) => new()
    {
        Rail            = rail,
        FrequencyHz     = frequencyHz,
        Shapes          = request.Shapes,
        Technology      = request.Technology,
        DbuPerMicron    = request.DbuPerMicron,
        Pads            = request.Pads,
        NetPoints       = request.NetPoints,
        ReferenceNet    = request.ReferenceNet,
        BoardOutline    = request.BoardOutline,
        SeriesElements  = request.SeriesElements,
        ShuntParts      = request.ShuntParts,
        Settings        = request.Document.Settings,
        Mesh            = request.Mesh,
        Graph           = request.Graph,
        ClassOverrides  = request.Document.ClassOverrides,
    };

    // ── reading the answer ─────────────────────────────────────────────────────────────────────

    private static RailDcResult Assemble(
        RailDcRequest request, RailSpec rail, PdnNetlist pdn, LinearDcSolution solution,
        RailChainStart? chained, IReadOnlyList<RailOrder.RailEdge> edges, bool upstreamSolved,
        PdnExtraction extraction)
    {
        var nl = pdn.Netlist;
        var findings = new List<string>();
        var notes = new List<string>(pdn.Provenance.Notes);

        // R-rail5-2: the FIELD, and it is complete. Every node of the netlist carries a voltage and
        // ground carries exactly zero, so every entry of NodeCells resolves — a node with no voltage
        // is a hole in the drop map, and a hole in a heat map reads as a value rather than as an
        // absence.
        var voltages = new Dictionary<int, double>(nl.Nodes.Count) { [0] = 0.0 };
        for (int n = 1; n < nl.Nodes.Count; n++) voltages[n] = solution.VoltageAt(n);

        double? sourceVoltage = null;
        foreach (var s in rail.Sources)
            if (s.OpenCircuitVoltageV is { } v && (sourceVoltage is null || v > sourceVoltage))
                sourceVoltage = v;
        if (chained is not null && (sourceVoltage is null || chained.VoltageV > sourceVoltage))
            sourceVoltage = chained.VoltageV;

        var ports = new List<RailPortDrop>(pdn.Ports.Count);
        foreach (var p in pdn.Ports)
        {
            double vp = p.PowerNode >= 0 ? solution.VoltageAt(p.PowerNode) : 0.0;
            double vr = p.ReferenceNode >= 0 ? solution.VoltageAt(p.ReferenceNode) : 0.0;
            double v = vp - vr;
            ports.Add(new RailPortDrop(
                p.Index, p.Name, p.Anchor, v,
                sourceVoltage is { } sv ? sv - v : null,
                p.DcCurrentA));
        }

        var sources = SourceShares(rail, pdn, solution);
        var breakdown = Breakdown(request, pdn, solution);

        // §2.4's via check. It reads the currents this solve already produced — brief 3 stamped each
        // barrel as its own element and nothing about a group is special-cased anywhere — so the
        // unequal split is a RESULT rather than a model, and the flag is on the WORST via of a
        // transition rather than on its mean.
        var viaCheck = PdnViaCheck.Run(
            pdn, voltages, request.Document.Settings.ViaTemperatureRiseCelsius, request.DbuPerMicron);

        foreach (var flag in viaCheck.Flags) findings.Add(flag.Describe());
        notes.AddRange(viaCheck.Notes);

        // R-rail5-11, and it is one line on the report rather than a setting, a sweep or a derating.
        notes.Add(
            $"Everything is computed at {pdn.Provenance.CopperTemperatureCelsius:0.#} °C and railRF " +
            "is not a thermal tool. Copper is +0.39 %/K: at 85 °C the same trace is about 25 % worse.");

        if (sourceVoltage is null)
            notes.Add(
                $"No source on rail '{rail.Name}' states an open-circuit voltage, so the answer is a " +
                "drop across this rail rather than a voltage at its loads. Every port's absolute " +
                "voltage below is measured against the reference point and not against a rail level.");

        if (chained is not null) notes.Add(chained.Describe());

        var regulators = Headroom(rail, edges, ports);
        foreach (var r in regulators)
            if (r.Ok is false) findings.Add(r.Describe());
            else notes.Add(r.Describe());

        bool? withinBudget = null;
        if (rail.DropBudget?.DropBudgetMillivolts is { } budgetMv)
        {
            double worst = 0;
            foreach (var p in ports)
                if (p.CurrentA is not null && p.DropV is { } d && d > worst) worst = d;

            withinBudget = worst * 1e3 <= budgetMv;
            if (withinBudget is false)
                findings.Add(
                    $"Rail '{rail.Name}' drops {worst * 1e3:0.###} mV to its worst load against a " +
                    $"budget of {budgetMv:0.###} mV.");
        }

        // A regulator whose own output rail was never reached is not a finding, but it IS the reason
        // a downstream rail is missing, so it is said rather than left to be inferred.
        if (upstreamSolved && chained is null)
            foreach (var s in rail.Sources)
                if (s.OpenCircuitVoltageV is not null && s.Anchor.Refdes is { Length: > 0 } refdes &&
                    edges.Any(e => string.Equals(e.Downstream, rail.Name, StringComparison.OrdinalIgnoreCase)
                                && string.Equals(e.Refdes, refdes, StringComparison.OrdinalIgnoreCase)))
                    notes.Add(
                        $"{refdes} states its own output voltage, so this rail's source is used as " +
                        "stated. The chain's DC coupling is the headroom finding on the rail above; " +
                        "railRF does not model a regulator's forward transfer and does not " +
                        "approximate one.");

        return new RailDcResult
        {
            RailName       = rail.Name,
            Netlist        = pdn,
            Data           = DcResultPacker.Pack(solution.AsDcResult(), nl),
            NodeVoltages   = voltages,
            Breakdown      = breakdown,
            // Carried through from the extraction rather than re-derived: the window draws the
            // classification the numbers were priced against, and the copper the solve walked.
            Regions        = extraction.Regions,
            Classification = extraction.Classification,
            ViaCheck       = viaCheck,
            Ports          = ports,
            Sources        = sources,
            Regulators     = regulators,
            ChainedFrom    = chained,
            SourceVoltageV = sourceVoltage,
            WithinDropBudget = withinBudget,
            Findings       = findings,
            Notes          = notes,
        };
    }

    /// <summary>
    /// What each source carried, and its share — §2.2's "the geometry is what decides how they
    /// share".
    ///
    /// <para><b>The delivered current is the NEGATIVE of the branch unknown</b>, and the sign is
    /// worth stating rather than discovering: a branch current is defined flowing from the element's
    /// first node to its second, so a positive one LEAVES the source's internal node through the
    /// source itself, which is current going in. A source delivering into the rail therefore reads
    /// negative on its own branch.</para>
    /// </summary>
    private static List<RailSourceShare> SourceShares(
        RailSpec rail, PdnNetlist pdn, LinearDcSolution solution)
    {
        var shares = new List<RailSourceShare>(rail.Sources.Count);
        var delivered = new double[rail.Sources.Count];

        for (int k = 0; k < rail.Sources.Count; k++)
        {
            int branchOwner = IndexOfPath(pdn, $"source.{k + 1}.v");
            if (branchOwner >= 0 && solution.BranchCurrentOf(branchOwner) is { } i)
            {
                delivered[k] = -i;
                continue;
            }

            // No voltage branch: the row states an impedance and no level, so what it "delivers" is
            // whatever its own series resistance carries between the rail and the reference.
            int r = IndexOfPath(pdn, $"source.{k + 1}.r");
            if (r >= 0) delivered[k] = CurrentThrough(pdn.Netlist.Components[r], solution);
        }

        double total = delivered.Sum();

        for (int k = 0; k < rail.Sources.Count; k++)
            shares.Add(new RailSourceShare(
                k, rail.Sources[k].Anchor.Describe(), rail.Sources[k].OpenCircuitVoltageV,
                delivered[k], total != 0 ? delivered[k] / total : 0.0));

        return shares;
    }

    private static int IndexOfPath(PdnNetlist pdn, string path)
    {
        var components = pdn.Netlist.Components;
        for (int i = 0; i < components.Count; i++)
            if (components[i].InstancePath == path) return i;
        return -1;
    }

    private static double CurrentThrough(
        Core.Elaboration.ElaboratedComponent c, LinearDcSolution solution)
    {
        if (!c.Parameters.TryGetValue("R", out var r)) return 0;
        double ohms = r.AsReal();
        double dv = solution.VoltageAt(c.Nodes[0]) - solution.VoltageAt(c.Nodes[1]);
        return ohms > 0 ? dv / ohms : dv * Core.Devices.ResistorModel.DefaultGmax;
    }

    // ── R-rail5-3 / R-rail5-4: the ranked breakdown, aggregated to what a designer can act on ──

    /// <summary>
    /// §2.4's ranked table.
    ///
    /// <para><b>Aggregated by ORIGIN, which is the reason brief 3 carries origins at all.</b> A mesh
    /// path is thousands of cell edges and a breakdown listing thousands of rows is a breakdown
    /// nobody reads. So contiguous cells on ONE conductor become one row, every barrel between one
    /// pair of cells becomes one row, and a part is one row.</para>
    ///
    /// <para><b>And the layer survives the aggregation</b> (R-rail5-4). §2.6's worked example turns
    /// on exactly that — "70 mm of 0.2 mm copper <i>on L3</i> is 347 mΩ and 42 mV on its own" — so a
    /// copper row names the stackup layer it is on, and a row's squares are printed beside it because
    /// squares are what §2.8's whole correction is counted in.</para>
    /// </summary>
    private static IReadOnlyList<PdnBreakdownRow> Breakdown(
        RailDcRequest request, PdnNetlist pdn, LinearDcSolution solution)
    {
        var components = pdn.Netlist.Components;
        var conductors = new Dictionary<LayerKey, (string Name, double SheetOhms)>();

        (string Name, double SheetOhms) ConductorFor(LayerKey layer)
        {
            if (conductors.TryGetValue(layer, out var found)) return found;

            var entry = request.Technology.Stackup.Layers.FirstOrDefault(
                l => l.Kind == StackupKind.Conductor && l.DrawingLayers.Contains(layer));

            double sheet = 0;
            if (entry is { ThicknessDbu: > 0, SigmaSm: > 0 })
            {
                double t = (double)entry.ThicknessDbu / (request.DbuPerMicron * 1e6);
                sheet = PdnMeshExtractor.ResistivityAt(
                    entry.SigmaSm, pdn.Provenance.CopperTemperatureCelsius) / t;
            }

            return conductors[layer] = (entry?.Name ?? $"layer {layer.Layer}/{layer.Datatype}", sheet);
        }

        // Contiguity, per conductor: a union-find over the MESH EDGES ONLY, so a via — which is a
        // different origin and a different row — never merges two layers into one. That is exactly
        // R-rail5-4's "contiguous cells belonging to one trace section on ONE LAYER".
        var tie = new Dictionary<(LayerKey, bool, int), (LayerKey, bool, int)>();

        (LayerKey, bool, int) Find((LayerKey, bool, int) x)
        {
            while (tie.TryGetValue(x, out var p) && !p.Equals(x)) { tie[x] = tie.TryGetValue(p, out var g) ? g : p; x = tie[x]; }
            return x;
        }

        void Union((LayerKey, bool, int) a, (LayerKey, bool, int) b)
        {
            tie.TryAdd(a, a);
            tie.TryAdd(b, b);
            var ra = Find(a);
            var rb = Find(b);
            if (!ra.Equals(rb)) tie[ra] = rb;
        }

        foreach (var o in pdn.Origins)
        {
            if (o.Kind != PdnOriginKind.MeshEdge || o.From is not { } from) continue;
            var c = components[o.ComponentIndex];
            Union((from.Layer, from.IsReference, c.Nodes[0]), (from.Layer, from.IsReference, c.Nodes[1]));
        }

        var elements = new List<PdnBreakdownElement>(pdn.Origins.Count);
        var groups = new Dictionary<string, (LayerKey? Layer, bool IsReference, PdnOriginKind Kind)>(StringComparer.Ordinal);

        foreach (var o in pdn.Origins)
        {
            if (o.ResistanceOhms is not { } ohms) continue;      // a port, an injection, a capacitor
            if (o.Kind is PdnOriginKind.SourceBranch) continue;  // an ideal branch: no ohms, no drop

            var c = components[o.ComponentIndex];
            double current = CurrentThrough(c, solution);

            string key;
            string label;

            switch (o.Kind)
            {
                case PdnOriginKind.MeshEdge when o.From is { } from:
                {
                    var root = Find((from.Layer, from.IsReference, c.Nodes[0]));
                    key = $"copper|{from.Layer.Layer}/{from.Layer.Datatype}|{(from.IsReference ? "ref" : "rail")}|{root.Item3}";
                    label = ConductorFor(from.Layer).Name;
                    groups.TryAdd(key, (from.Layer, from.IsReference, o.Kind));
                    break;
                }

                case PdnOriginKind.TraceSection:
                {
                    key = $"section|{o.ComponentIndex}";
                    label = o.Description;
                    groups.TryAdd(key, (o.From?.Layer, o.From?.IsReference ?? false, o.Kind));
                    break;
                }

                case PdnOriginKind.Via:
                {
                    int lo = Math.Min(c.Nodes[0], c.Nodes[1]), hi = Math.Max(c.Nodes[0], c.Nodes[1]);
                    key = $"vias|{lo}-{hi}";
                    label = o.Description;
                    groups.TryAdd(key, (o.From?.Layer, o.From?.IsReference ?? false, o.Kind));
                    break;
                }

                default:
                {
                    key = $"{o.Kind}|{o.Refdes ?? o.ComponentIndex.ToString()}";
                    label = o.Description;
                    groups.TryAdd(key, (null, false, o.Kind));
                    break;
                }
            }

            elements.Add(new PdnBreakdownElement(key, label, c.Nodes[0], c.Nodes[1], ohms, current));
        }

        var rows = PdnBreakdown.Rank(elements);
        var named = new List<PdnBreakdownRow>(rows.Count);

        foreach (var row in rows)
        {
            if (!groups.TryGetValue(row.GroupKey, out var g)) { named.Add(row); continue; }

            named.Add(g.Kind switch
            {
                // A mesh group's own elements each describe ONE cell, so the group's label is built
                // rather than borrowed: a row reading "0.1 mm of 0.1 mm copper" over five thousand
                // cells would be wrong by the whole aggregation.
                PdnOriginKind.MeshEdge when g.Layer is { } layer => row with
                {
                    Label = Squares(row.ResistanceOhms, ConductorFor(layer).SheetOhms) is { } sq
                        ? $"{sq:0.#} squares of {ConductorFor(layer).Name} copper" +
                          (g.IsReference ? ", the reference return" : "") +
                          $" ({row.ElementCount} cells)"
                        : $"{ConductorFor(layer).Name} copper" +
                          (g.IsReference ? ", the reference return" : "") +
                          $" ({row.ElementCount} cells)",
                },

                PdnOriginKind.Via when row.ElementCount > 1 => row with
                {
                    Label = $"{row.ElementCount} parallel vias — {row.Label}",
                },

                _ => row,
            });
        }

        return named;
    }

    /// <summary>How many squares of this conductor a resistance is, or null where the stackup states
    /// no thickness or no conductivity to count them against.</summary>
    private static double? Squares(double ohms, double sheetOhms) =>
        sheetOhms > 0 ? ohms / sheetOhms : null;
}
