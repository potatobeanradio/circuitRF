using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Elaboration;
using System.Text.RegularExpressions;
using System.Linq;

namespace CircuitRF.Core.Elaboration;

/// <summary>
/// Flattens and resolves a TestBench into an ElaboratedNetlist.
///
/// Algorithm (data-model §3, expressions.md §9):
///   1. Build a global scope from TestBench.GlobalVariables.
///   2. The TestBench's own Instances ARE the root frame — there is no "enter the TopCell first"
///      step. Flatten depth-first: primitives are emitted; cell instances recurse with a fresh scope.
///   3. For each instance, resolve parameter values:
///      - Override expressions evaluate in the PARENT scope.
///      - Default expressions evaluate in the CELL's own scope.
///   4. Net names are uniquified by instance path; ground = "0" → node 0.
/// </summary>
public sealed class Elaborator
{
    private readonly Library[]  _libraries;
    private readonly Evaluator  _evaluator = new();

    /// <summary>
    /// Lets a frequency-dependent value cross a cell boundary as an EXPRESSION rather than being
    /// forced to a number there. One instance per elaboration, because it caches which names are
    /// frequency-dependent and that answer is only meaningful within one library.
    /// </summary>
    private readonly FreqDeferral _freq = new();

    /// <summary>
    /// The netlist's user-defined expression functions, kept for models that evaluate at stamp time.
    /// Held per-Elaborator rather than globally so two designs open at once cannot see each other's.
    /// </summary>
    private IReadOnlyList<UserFunction> _functions = [];

    /// <summary>
    /// Workspace root for resolving relative file-path parameters (e.g. SnP File).
    /// Null → relative paths are left as-authored (legacy CWD resolution) for CLI / no-workspace runs.
    /// Only a path string crosses into Core here — no UI dependency.
    /// </summary>
    public string? BaseDirectory { get; init; }

    public Elaborator(params Library[] libraries)
        => _libraries = libraries;

    public ElaboratedNetlist Elaborate(TestBench tb)
    {
        // User-defined expression functions must exist before any expression is resolved —
        // a cell parameter may call one in its default, which is evaluated during flattening.
        // Kept as well as registered: a model that evaluates an expression at STAMP time builds
        // its own Evaluator per frequency, long after this one is gone, and needs the same table.
        _functions = tb.Functions.ToArray();
        foreach (var fn in _functions)
            _evaluator.RegisterFunction(fn);

        var netlist     = new ElaboratedNetlist();
        var globalScope = BuildGlobalScope(tb);

        // Ambient must be known BEFORE flattening: models are constructed during the walk, and a
        // temperature-aware one bakes its temperature in at construction.
        _ambientC = ResolveAmbient(tb, globalScope, netlist);

        // The TestBench's instance list IS the root frame — no TopCell lookup.
        FlattenInstances(
            tb.Instances,
            instancePathPrefix: "",
            parentNetMap:       null,
            currentScope:       globalScope,
            globalScope:        globalScope,
            netlist:            netlist);

        // Post-flatten: resolve mutual inductance references now that all inductors exist.
        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel m)
                m.Resolve(netlist, ec);

        // Propagate label provenance from TestBench (top-level names only; no path prefix needed).
        foreach (var name in tb.LabeledNets)
            netlist.Nodes.LabeledNames.Add(name);

        // Populate ResolvedGlobals — used by the HB engine to resolve analysis directives
        // and re-evaluate sweep-dependent expressions at each sweep step.
        foreach (var v in tb.GlobalVariables)
        {
            if (!string.IsNullOrEmpty(v.Unit))
                netlist.MarkGlobalHasUnit(v.Name);
            try
            {
                var val = _evaluator.Resolve(v.Name, globalScope);
                if (val.Kind is ValueKind.Real or ValueKind.Complex)
                    netlist.SetResolvedGlobal(v.Name, val);
            }
            catch { /* skip variables that cannot resolve (e.g. forward refs) */ }
        }

        // Layer-3 linter: check top-level Terms for Num consistency. The Num parameter is meaningful
        // ONLY to S-parameter analysis, so this lint runs only when an S-parameter analysis will
        // actually run — otherwise it fires spuriously on HB/DC/loadpull-only test benches.
        if (HasRunnableSParam(tb))
            LintTopLevelTerms(netlist);

        return netlist;
    }

    // ── Scope helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// The design's ambient temperature in °C for this elaboration. Set once, before flattening.
    /// <see cref="Temperature.NominalC"/> when the design says nothing — which is what makes ambient
    /// support additive: a netlist with no <c>temp</c> global elaborates to exactly what it did
    /// before ambient existed.
    /// </summary>
    private double _ambientC = Temperature.NominalC;

    /// <summary>
    /// Reads the ambient temperature out of the global variable named <c>temp</c> (°C).
    ///
    /// <para><b>Why a global rather than a directive.</b> Globals already round-trip through
    /// <c>.cnl</c>, already resolve through the ordinary expression machinery, and are already
    /// overridden per point by <c>ParametricSweepEngine</c> — which re-elaborates every point, so a
    /// temperature sweep needs no new mechanism at all. A directive would be a format change for a
    /// capability the format already has.</para>
    ///
    /// <para><b>It is reported, because it was not asked for explicitly.</b> A design that happens
    /// to use <c>temp</c> for something else would otherwise have its meaning silently changed. The
    /// note fires only when the global exists, so an ordinary design says nothing.</para>
    /// </summary>
    private double ResolveAmbient(TestBench tb, Scope globalScope, ElaboratedNetlist netlist)
    {
        if (!tb.GlobalVariables.Any(v =>
                string.Equals(v.Name, Temperature.AmbientGlobalName, StringComparison.OrdinalIgnoreCase)))
            return Temperature.NominalC;

        Value val;
        try
        {
            val = _evaluator.Resolve(Temperature.AmbientGlobalName, globalScope);
        }
        catch (Exception ex)
        {
            // An unresolvable temp is reported and ignored rather than failing the elaboration: the
            // rest of the design is perfectly simulable at the nominal, and refusing to elaborate
            // would hide every other problem behind this one.
            netlist.AddWarningOnce("ambient-temperature-unresolved",
                $"Global '{Temperature.AmbientGlobalName}' could not be resolved ({ex.Message}); " +
                $"using the nominal {Temperature.NominalC:0.##} °C as the ambient temperature.");
            return Temperature.NominalC;
        }

        if (val.Kind != ValueKind.Real)
        {
            netlist.AddWarningOnce("ambient-temperature-not-real",
                $"Global '{Temperature.AmbientGlobalName}' is not a real number, so it is not being " +
                $"read as an ambient temperature; using the nominal {Temperature.NominalC:0.##} °C.");
            return Temperature.NominalC;
        }

        double ambientC = val.AsReal();
        netlist.AddWarningOnce("ambient-temperature",
            $"Ambient temperature {ambientC:0.##} °C, from the global '{Temperature.AmbientGlobalName}'. " +
            "Devices stating no temperature of their own are evaluated there.");
        return ambientC;
    }

    private Scope BuildGlobalScope(TestBench tb)
    {
        var scope = new Scope("global");
        foreach (var v in tb.GlobalVariables)
            scope.Bind(v.Name, v.Expression, v.Unit);
        return scope;
    }

    private Scope BuildCellScope(Cell cell, Scope parentScope, IEnumerable<ParameterAssignment> overrides, string scopeName)
    {
        var cellScope = new Scope(scopeName, parentScope);

        // Load parameter defaults (evaluated lazily in the cell's own scope).
        foreach (var pd in cell.Parameters)
            cellScope.Bind(pd.Name, pd.DefaultExpression, pd.Unit);

        // Cell-scoped variables (evaluated in the cell's own scope).
        foreach (var v in cell.Variables)
            cellScope.Bind(v.Name, v.Expression, v.Unit);

        // Override expressions are evaluated in the PARENT scope.
        // Inject the resolved values directly into the memo cache (avoids
        // Complex.ToString() round-trip problems).
        foreach (var ov in overrides)
        {
            // A frequency-dependent argument cannot be evaluated here — `freq` is bound at stamp
            // time, by the model that is defined as a function of it. Bind the inlined EXPRESSION
            // instead, so the value keeps travelling down until it reaches such a model. The child's
            // own variables then become frequency-dependent through it, without knowing anything
            // about where the dependence came from.
            if (_freq.IsFreqDependent(ov.Expression, parentScope))
            {
                // Inlining already applied the unit of every binding it absorbed, so re-applying a
                // site unit here would apply it twice — the same var-unit-wins rule Eval() follows,
                // enforced through Eval's own predicate rather than a second copy of it.
                string? siteUnit = Evaluator.ReferencesUnitBearingVariable(ov.Expression, parentScope)
                    ? null
                    : ov.Unit;

                cellScope.Bind(
                    ov.Name,
                    _freq.InlineForCellBoundary(ov.Expression, parentScope, _evaluator),
                    siteUnit);
                continue;
            }

            var resolved = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);
            cellScope.Bind(ov.Name, "__resolved__");
            _evaluator.InjectResolved(scopeName, ov.Name, resolved);
        }

        return cellScope;
    }

    // ── Flattening ────────────────────────────────────────────────────────────

    /// <summary>
    /// Flattens a Cell by processing its instance list.
    /// Called when a cell instance is encountered during recursion.
    /// </summary>
    private void FlattenCell(
        Cell cell,
        string instancePath,
        IReadOnlyDictionary<string, string>? parentNetMap,
        Scope cellScope,
        Scope globalScope,
        ElaboratedNetlist netlist)
        => FlattenInstances(cell.Instances, instancePath, parentNetMap, cellScope, globalScope, netlist);

    /// <summary>
    /// Core recursive loop — shared by the TestBench root frame and every Cell recursion.
    /// </summary>
    /// <param name="instances">The instance list to process (from TestBench or a Cell).</param>
    /// <param name="instancePathPrefix">Dot-path prefix for this level (empty at top).</param>
    /// <param name="parentNetMap">
    ///   Maps port names of the current cell to net names in the parent.
    ///   Null at the TestBench root (no parent above — net names are used as-is).
    /// </param>
    /// <param name="currentScope">The scope for this frame.</param>
    /// <param name="globalScope">The root scope (always visible).</param>
    /// <param name="netlist">Accumulated output.</param>
    private void FlattenInstances(
        IReadOnlyList<Instance> instances,
        string instancePathPrefix,
        IReadOnlyDictionary<string, string>? parentNetMap,
        Scope currentScope,
        Scope globalScope,
        ElaboratedNetlist netlist)
    {
        // MStep reservation (brief-L5a-pcell-contract-and-microstrip.md R-pc-14 / microstrip-
        // models.md §4A): the microstrip width-step discontinuity is deliberately NOT modeled.
        // Unlike MBend/MTee/MCross, it carries no information the schematic doesn't already have
        // (fully determined by the two adjacent line widths), so it must be SYNTHESIZED from net
        // connectivity rather than placed as its own component — a per-component flag would
        // double-count, since a junction has two sides and any tie-break between them is
        // arbitrary. If ever built, this is the hook: a single switch on the analysis (not a
        // per-component flag), classifying junctions by arm count as this per-instance walk
        // already visits every net binding — 2 = step, 3 = tee, 4 = cross. Revisit after L8.
        foreach (var inst in instances)
        {
            var childPath = instancePathPrefix.Length == 0
                ? inst.InstanceName
                : $"{instancePathPrefix}.{inst.InstanceName}";

            // Resolve a local net name to the globally-unique net name.
            // At the top frame (parentNetMap=null, prefix="") nets are used as-is.
            string ResolveNet(string localNet)
            {
                if (localNet == "0") return "0";
                if (parentNetMap != null && parentNetMap.TryGetValue(localNet, out var mapped))
                    return mapped;
                return instancePathPrefix.Length == 0
                    ? localNet
                    : $"{instancePathPrefix}.{localNet}";
            }

            // Pin is a connectivity marker only — the extractor already named the net after the
            // port and the parentNetMap handles the binding. Nothing to stamp or recurse into.
            if (inst.Reference.Equals("Pin", StringComparison.OrdinalIgnoreCase))
            {
                // Layer-3 linter: a Pin at the testbench top has no effect (no parent to bind to).
                if (instancePathPrefix.Length == 0)
                    netlist.AddWarning(
                        $"Pin '{childPath}' is at the testbench top level and has no effect; " +
                        $"Pins belong inside cell schematics to realize interface ports.");
                continue;
            }

            if (ComponentModelFactory.IsPrimitive(inst.Reference))
            {
                // Primitive — resolve nodes and parameters first; model creation may need params (e.g. SnP).
                var resolvedNodes  = inst.NetBindings.Select(n => netlist.Nodes.GetOrAssign(ResolveNet(n))).ToArray();
                var resolvedParams = ResolveParameters(inst, currentScope);
                // Temp wins over Dtemp (Temperature.ResolveDeviceC), but the two together cannot
                // both be what the author meant — so the discard is reported rather than silent.
                if (Temperature.HasContradictoryOverride(resolvedParams))
                    netlist.AddWarningOnce($"temp-and-dtemp:{childPath}",
                        $"'{childPath}' states both {Temperature.AbsoluteParamName} (absolute) and " +
                        $"{Temperature.DeltaParamName} (a rise above ambient). " +
                        $"{Temperature.AbsoluteParamName} is used and " +
                        $"{Temperature.DeltaParamName} is ignored.");

                var model          = ComponentModelFactory.TryCreate(inst.Reference, resolvedParams, _functions, _ambientC)
                                     ?? throw new InvalidOperationException(
                                         $"Failed to create model for primitive '{inst.Reference}' at '{childPath}'");

                if (model is ToneSourceModel tsm)
                    foreach (var w in tsm.GetZeroHzToneWarnings(childPath))
                        netlist.AddWarningOnce($"zero-hz-tone:{childPath}", w);

                // Reference node: null RefNetBinding → ground (0); otherwise resolve the named net.
                var refNode = inst.RefNetBinding is null
                              ? 0
                              : netlist.Nodes.GetOrAssign(ResolveNet(inst.RefNetBinding));

                // Tuner: mint internal nodes for the bias-tee topology (loadpull.md §1.1).
                // Names are collision-proof: keyed on the Tuner instance path.
                // The __ prefix is reserved so user nets can never collide.
                //   _block / _bias — used by both Load and Source roles.
                //   _outer — the SourceTuner's internal RF-drive node (where the embedded V_1Tone
                //            drives against the reference). Minted for every Tuner so both declared
                //            nets stay [DUT, reference]; the LoadTuner role simply ignores it.
                if (inst.Reference.Equals("Tuner", StringComparison.OrdinalIgnoreCase))
                {
                    int nBlock = netlist.Nodes.GetOrAssign($"__tuner_{childPath}_block");
                    int nBias  = netlist.Nodes.GetOrAssign($"__tuner_{childPath}_bias");
                    int nOuter = netlist.Nodes.GetOrAssign($"__tuner_{childPath}_outer");
                    resolvedNodes = [..resolvedNodes, nBlock, nBias, nOuter];
                }

                // P1Tone: mint one internal node (junction between V-source and Z_Port).
                if (inst.Reference.Equals("P1Tone", StringComparison.OrdinalIgnoreCase))
                {
                    int nDrv = netlist.Nodes.GetOrAssign($"__p1tone_{childPath}_drv");
                    resolvedNodes = [..resolvedNodes, nDrv];
                }

                // PnTone: same single internal drive node (multi-tone V-source ↔ Z_Port junction).
                if (inst.Reference.Equals("PnTone", StringComparison.OrdinalIgnoreCase))
                {
                    int nDrv = netlist.Nodes.GetOrAssign($"__pntone_{childPath}_drv");
                    resolvedNodes = [..resolvedNodes, nDrv];
                }

                // Diode with series resistance: three nets, [anode, internal, internal, cathode],
                // so the model's two ports are the resistor and the junction. The internal node is
                // minted here and gets an ordinary matrix row for the same reason ExtDevice's do —
                // collapsing it locally is exact at DC and wrong in HB, where it carries its own
                // harmonic content.
                if (model is DiodeModel { HasSeriesResistance: true } && resolvedNodes.Length == 2)
                {
                    int nInt = netlist.Nodes.GetOrAssign($"__diode_{childPath}_int");
                    resolvedNodes = [resolvedNodes[0], nInt, nInt, resolvedNodes[1]];
                }

                // FET family: the user draws three terminals (gate, drain, source) but the model
                // is TWO ports — (gate,source) and (drain,source) — so the source net appears in
                // both pairs. Expanding here keeps the schematic honest (three pins) and the model
                // in the coordinates every published FET equation is written in (Vgs, Vds).
                if (model is Devices.Fet.FetModelBase && resolvedNodes.Length == 3)
                    resolvedNodes = [resolvedNodes[0], resolvedNodes[2],   // gate, source
                                     resolvedNodes[1], resolvedNodes[2]];  // drain, source

                // ExtDevice: the provider reports currents per NODE, so every node becomes its own
                // ground-referenced port — [n, 0] per node — and the internal nodes are minted here
                // exactly like any other internal net. They therefore get ordinary rows in the
                // global matrix, which is required: eliminating them locally would be simpler and
                // is wrong for HB, where an internal node voltage carries its own harmonic content.
                if (model is ExternalDeviceModel extDev)
                    resolvedNodes = BuildExternalDeviceNodes(extDev, resolvedNodes, childPath, netlist);

                // Layer-2 + Layer-3 linter: a Term/Port inside an instantiated sub-cell is a
                // design error — it will be treated as inert and never become an S-param port.
                if ((model is PortModel or TermModel) && instancePathPrefix.Length > 0)
                    netlist.AddWarning(
                        $"Term '{childPath}' is inside an instantiated cell and was ignored; " +
                        $"use a Pin for cell interfaces and place Terms only in the testbench.");

                var ec = new ElaboratedComponent(inst.Reference, childPath, resolvedNodes, resolvedParams, model)
                         { ReferenceNode = refNode, Multiplicity = ResolveMultiplicity(resolvedParams, childPath) };
                netlist.AddComponent(ec);
            }
            else
            {
                // Sub-cell — recurse
                var subCell = FindCell(inst.Reference)
                    ?? throw new InvalidOperationException(
                        $"Cell '{inst.Reference}' not found in libraries (referenced by '{childPath}')");

                // Build port → resolved-net map for the sub-cell's perspective
                var subPortMap = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < Math.Min(subCell.Ports.Count, inst.NetBindings.Count); i++)
                    subPortMap[subCell.Ports[i]] = ResolveNet(inst.NetBindings[i]);

                var subScope = BuildCellScope(
                    subCell,
                    parentScope: currentScope,
                    overrides:   inst.Overrides,
                    scopeName:   childPath);

                FlattenCell(subCell, childPath, subPortMap, subScope, globalScope, netlist);
            }
        }
    }

    // ── Parameter resolution ──────────────────────────────────────────────────

    private IReadOnlyDictionary<string, Value> ResolveParameters(
        Instance inst,
        Scope parentScope)
    {
        if (inst.Reference.Equals("SDD", StringComparison.OrdinalIgnoreCase))
            return ResolveSddParameters(inst, parentScope);
        if (inst.Reference.Equals("Z_Port", StringComparison.OrdinalIgnoreCase))
            return ResolveZPortParameters(inst, parentScope);
        if (inst.Reference.Equals("V_1Tone", StringComparison.OrdinalIgnoreCase) ||
            inst.Reference.Equals("V_nTone", StringComparison.OrdinalIgnoreCase))
            return ResolveToneSourceParameters(inst, parentScope);
        if (inst.Reference.Equals("P1Tone", StringComparison.OrdinalIgnoreCase))
            return ResolveP1ToneParameters(inst, parentScope);
        if (inst.Reference.Equals("PnTone", StringComparison.OrdinalIgnoreCase))
            return ResolvePnToneParameters(inst, parentScope);
        if (inst.Reference.Equals("SnP", StringComparison.OrdinalIgnoreCase))
            return ResolveSnpParameters(inst, parentScope);
        // Both name a device somebody else supplies, so both need the same rule: most of their
        // parameters belong to that model and must NOT be expression-evaluated. VerilogA's `File` is
        // the sharpest case — a leading '/' alone crashes the expression parser at position 0.
        if (inst.Reference.Equals("ExtDevice", StringComparison.OrdinalIgnoreCase) ||
            inst.Reference.Equals("VerilogA",  StringComparison.OrdinalIgnoreCase))
            return ResolveExtDeviceParameters(inst, parentScope);
        if (inst.Reference.Equals("Chain", StringComparison.OrdinalIgnoreCase))
            return ResolveChainParameters(inst, parentScope);
        // wBond's `File` names a .wBond design. Same rule and the same reason as SnP's: a leading
        // '/' alone crashes the expression parser at position 0.
        if (inst.Reference.Equals("wBond", StringComparison.OrdinalIgnoreCase))
            return ResolveWBondParameters(inst, parentScope);

        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var ov in inst.Overrides)
        {
            // Frequency dependence has to TERMINATE at a model that binds `freq`. Anything else is
            // asking for a single number that does not exist, and saying so here — naming the
            // device, the parameter and the models that can take one — beats the bare
            // "Unresolved name 'freq'" the evaluator would otherwise report from inside the value.
            if (_freq.IsFreqDependent(ov.Expression, parentScope))
                throw new FrequencyDependentValueException(
                    $"'{inst.Reference}:{inst.InstanceName}' parameter '{ov.Name}' is frequency-dependent, " +
                    $"but a '{inst.Reference}' takes a single value that cannot vary with frequency. " +
                    "Only Chain (A/B/C/D), Z_Port (Z[i,j]) and SDD (H[w]) are evaluated per frequency.");

            result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);
        }
        return result;
    }

    // ── Chain parameter resolution ────────────────────────────────────────────

    /// <summary>
    /// A/B/C/D are frequency-dependent expressions evaluated per stamped frequency, exactly like
    /// Z_Port's Z[i,j] — so they are stored raw and their referenced scope variables injected,
    /// rather than evaluated once here.
    /// </summary>
    private IReadOnlyDictionary<string, Value> ResolveChainParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        result["ChainName"] = new Value(inst.InstanceName);

        if (inst.NetBindings.Count != 4)
            throw new InvalidOperationException(
                $"Chain '{inst.InstanceName}': expected 4 nets (port1 +,− then port2 +,−); " +
                $"got {inst.NetBindings.Count}.");

        foreach (var ov in inst.Overrides)
        {
            if (ov.Name is "A" or "B" or "C" or "D")
            {
                // Inlining leaves one self-contained expression in `freq` — which is exactly the
                // form this model already accepts — and returns the text untouched when nothing is
                // frequency-dependent, so an ordinary Chain takes the path it always did.
                string expr = _freq.InlineForDevice(ov.Expression, parentScope, _evaluator);
                result[ov.Name] = new Value(expr);
                InjectZPortScopeVars(expr, parentScope, result);
            }
            else
            {
                try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
                catch { /* not an expression this layer owns */ }
            }
        }
        return result;
    }

    // ── device multiplier ─────────────────────────────────────────────────────

    /// <summary>
    /// The netlist's <c>m</c> — how many identical copies of a component are in parallel.
    ///
    /// <para><b>Lower-case <c>m</c>, and the case matters.</b> Upper-case <c>M</c> is the junction
    /// diode's grading coefficient, on a component that can carry both, and the two mean nothing
    /// like each other. Resolved parameters are compared ordinally so the two are genuinely
    /// different keys — but the collision is a real one, and a diode reading its grading coefficient
    /// as a device count would produce a circuit with 0.5 diodes in it and simulate perfectly.</para>
    ///
    /// <para><b>Zero or negative is refused rather than obeyed.</b> Some dialects read <c>m = 0</c>
    /// as "this device is not there". Deleting a component the user placed, in silence, is a worse
    /// answer than saying the value cannot be used.</para>
    /// </summary>
    private static double ResolveMultiplicity(IReadOnlyDictionary<string, Value> parameters, string path)
    {
        if (!parameters.TryGetValue(MultiplierParamName, out var v) || v.Kind != ValueKind.Real)
            return 1.0;

        double m = v.AsReal();
        if (m <= 0.0 || !double.IsFinite(m))
            throw new InvalidOperationException(
                $"'{path}' states a device multiplier {MultiplierParamName}={m:G6}. It is the number " +
                "of identical copies in parallel and must be greater than zero — a device that is " +
                "not there is expressed by removing it, not by multiplying it away.");

        return m;
    }

    /// <summary>The instance parameter naming how many copies of a component are in parallel.</summary>
    public const string MultiplierParamName = "m";

    // ── ExtDevice node allocation ─────────────────────────────────────────────

    /// <summary>
    /// Lays out an external device's node array as ground-referenced port pairs and mints its
    /// internal nodes. A node the descriptor reports as slaved is given its master's node index
    /// rather than a fresh one — the engine's four-way port stamp then folds the chain rule on its
    /// own (see <see cref="ExternalDeviceModel"/>).
    /// </summary>
    private static int[] BuildExternalDeviceNodes(
        ExternalDeviceModel model, int[] declaredNets, string childPath, ElaboratedNetlist netlist)
    {
        var d = model.Descriptor;
        if (declaredNets.Length != d.ExternalPinCount)
            throw new ExternalDeviceException(
                $"ExtDevice '{childPath}' (type '{d.TypeId}') declares {d.ExternalPinCount} " +
                $"external pins but {declaredNets.Length} nets were given.");

        var nodeIndex = new int[d.NodeCount];
        for (int k = 0; k < d.ExternalPinCount; k++) nodeIndex[k] = declaredNets[k];


        // An EXTERNAL pin collapsed to ground is refused rather than interpreted. The user wired a
        // net to it, and circuitRF's two available readings are both wrong: give the pin node 0 and
        // the user's net is silently left floating instead of shorted; leave it alone and the
        // device is solving a node the model says does not exist. Neither shows on screen, so the
        // provider is told to stop offering the pin instead.
        foreach (var node in d.Nodes)
        {
            if (!node.CollapsedToGround) continue;
            if (node.SlavedTo is not null)
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): node {node.Index} is reported both " +
                    $"as grounded and as slaved to node {node.SlavedTo} — it cannot be both.");
            if (node.Index < d.ExternalPinCount)
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): external pin {node.Index} is reported " +
                    $"as collapsed to ground, but a pin the user wires cannot be grounded from inside " +
                    $"the device. The provider should not offer it as a pin under these parameters.");
        }

        // Collapsed nodes are merged in GROUPS, and within a group AN EXTERNAL PIN ALWAYS WINS.
        //
        // A real compact model collapses a terminal onto the internal node behind it — a MOSFET's
        // drain onto its internal drain, its bulk and three internal bulk nodes onto one. Reading
        // "node A follows node B" literally there gives the terminal the INTERNAL node's index, and
        // the net the user wired to that pin is dropped: the device solves happily, entirely
        // disconnected from the circuit around it. Nothing on screen says so.
        //
        // Two passes, because one master may have several slaves and only one of them external —
        // measured on a real model, four nodes collapse onto one internal bulk node and the fourth
        // is the bulk TERMINAL. Assigning as they are encountered would copy the internal index into
        // the first three before the terminal is reached.
        var groupOf = new Dictionary<int, List<int>>();
        foreach (var node in d.Nodes)
        {
            if (node.SlavedTo is not int master) continue;
            if (master < 0 || master >= d.NodeCount || master == node.Index)
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): node {node.Index} is slaved to " +
                    $"node {master}, which is not a valid other node of this device.");
            if (d.Nodes.First(n => n.Index == master).SlavedTo is not null)
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): node {node.Index} is slaved to " +
                    $"node {master}, which is itself slaved — chains are not supported.");

            if (!groupOf.TryGetValue(master, out var members))
                groupOf[master] = members = [master];
            members.Add(node.Index);
        }

        // The index each group settles on, decided BEFORE any node is minted. An internal master
        // whose group contains a terminal must not be minted at all: minting it and then overwriting
        // it leaves an unknown in the system that nothing references — an all-zero row AND column,
        // which is the definition of a singular matrix. DC hides that completely (gmin holds the
        // orphan at zero and no equation reads it), so it surfaces only in the S-parameter assembly,
        // as a singularity report naming nodes the user cannot find anywhere in their schematic.
        var settled = new Dictionary<int, int>();
        foreach (var (master, members) in groupOf)
        {
            var externals = members.Where(m => m < d.ExternalPinCount).ToList();

            // Two terminals collapsed together means the device shorts two of the user's nets. That
            // is a real statement, and circuitRF cannot carry it: stamping at one net silently drops
            // the other. Refused rather than half-applied.
            if (externals.Count > 1 && externals.Select(e => nodeIndex[e]).Distinct().Count() > 1)
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): pins " +
                    $"{string.Join(", ", externals)} are collapsed onto one another, which shorts the " +
                    "nets wired to them. Join those nets in the schematic instead.");

            if (externals.Count > 0) settled[master] = nodeIndex[externals[0]];
        }

        // Now mint, for internal nodes that genuinely still need an unknown of their own.
        for (int k = d.ExternalPinCount; k < d.NodeCount; k++)
        {
            var declared = d.Nodes.FirstOrDefault(n => n.Index == k);
            if (declared?.SlavedTo is not null) continue;      // takes its group's index below
            if (settled.ContainsKey(k)) continue;              // absorbed into a terminal's net

            // A node the provider collapsed onto the ground reference IS ground — it takes node 0
            // rather than an unknown of its own, for the same reason a slaved node takes its
            // master's. This is the shape a model reports for, say, a thermal node when
            // self-heating is switched off: the whole thermal network vanishes and the node with it.
            if (declared?.CollapsedToGround == true) { nodeIndex[k] = 0; continue; }

            nodeIndex[k] = netlist.Nodes.GetOrAssign($"__extdev_{childPath}_n{k}");
        }

        foreach (var (master, members) in groupOf)
        {
            int representative = settled.TryGetValue(master, out int external) ? external : nodeIndex[master];
            foreach (int m in members) nodeIndex[m] = representative;
        }

        // Ground-referenced pairs: [n0, 0, n1, 0, ...].
        var pairs = new int[d.NodeCount * 2];
        for (int k = 0; k < d.NodeCount; k++) { pairs[2 * k] = nodeIndex[k]; pairs[2 * k + 1] = 0; }
        return pairs;
    }

    // ── ExtDevice parameter resolution ────────────────────────────────────────

    /// <summary>
    /// An external device's parameters belong to its provider, not to circuitRF, so most of them
    /// must NOT be expression-evaluated: Provider and Type are names, and a provider is free to
    /// declare file paths or enum-valued parameters (a leading '/' alone crashes the expression
    /// parser at position 0 — the same trap SnP's File= hit).
    ///
    /// Rule applied here: a parameter whose text parses as a plain number is stored as a number so
    /// unit suffixes and simple arithmetic still work for genuinely numeric values; everything else
    /// is stored verbatim. The provider declares the real kinds and does its own conversion.
    /// </summary>
    private IReadOnlyDictionary<string, Value> ResolveExtDeviceParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        result["__instanceLabel"] = new Value(inst.InstanceName);

        // WHICH KEYS ARE THE SELECTORS IS DECIDED ONCE, OVER THE WHOLE INSTANCE — not per override
        // by a case-blind comparison. A compiled model may declare a parameter of its own that
        // differs from a selector only in case (a real MOS model's `TYPE` is its channel polarity),
        // and treating that as a selector both drops it from what reaches the model and stops it
        // being evaluated. Same rule as ComponentModelFactory.ReservedKey, and it must agree with it.
        var names = inst.Overrides.Select(o => o.Name).ToList();
        var selectors = new HashSet<string>(StringComparer.Ordinal);
        foreach (string reserved in new[]
                 {
                     "Provider", "Type",
                     Devices.ComponentModelFactory.VerilogAFileParam,
                     Devices.ComponentModelFactory.VerilogAModelParam,
                 })
            if (Devices.ComponentModelFactory.ReservedKey(names, reserved) is { } key) selectors.Add(key);

        foreach (var ov in inst.Overrides)
        {
            // The selectors are ALWAYS verbatim, never tried as an expression first. Provider and
            // Type name things; File is a path and Model is a name inside it. Falling back to
            // verbatim only when evaluation throws is not enough for these: a path that happens to
            // parse as arithmetic would be silently turned into a number.
            if (selectors.Contains(ov.Name))
            {
                result[ov.Name] = new Value(ov.Expression.Trim().Trim('"'));
                continue;
            }

            try
            {
                result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);
            }
            catch
            {
                // Not an expression — a path, an enum name, or anything else the provider owns.
                result[ov.Name] = new Value(ov.Expression.Trim().Trim('"'));
            }
        }
        return result;
    }

    // ── Z_Port parameter resolution ───────────────────────────────────────────

    private static readonly Regex RxZPortEntry = new(@"^Z\[\d+,\d+\]$", RegexOptions.Compiled);

    private IReadOnlyDictionary<string, Value> ResolveZPortParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        result["ZPortName"] = new Value(inst.InstanceName);

        // Determine N from the maximum port/column index in Z[i,j] parameters.
        int maxIdx = 0;
        foreach (var ov in inst.Overrides)
        {
            if (!RxZPortEntry.IsMatch(ov.Name)) continue;
            var m = System.Text.RegularExpressions.Regex.Match(ov.Name, @"\[(\d+),(\d+)\]");
            if (m.Success)
            {
                maxIdx = Math.Max(maxIdx, int.Parse(m.Groups[1].Value));
                maxIdx = Math.Max(maxIdx, int.Parse(m.Groups[2].Value));
            }
        }
        int portCount = Math.Max(1, maxIdx);
        result["ZPortCount"] = new Value((double)portCount);

        int netCount = inst.NetBindings.Count;
        if (netCount % 2 != 0)
            throw new InvalidOperationException(
                $"Z_Port '{inst.InstanceName}': expected an even number of nets (2 per port: +,−); got {netCount}.");
        if (netCount != 2 * portCount)
            throw new InvalidOperationException(
                $"Z_Port '{inst.InstanceName}': expected {2 * portCount} nets (2 per port: +,−) for a {portCount}-port " +
                $"(Z[{portCount},{portCount}] present); got {netCount}. Each port needs a +,− net pair.");

        foreach (var ov in inst.Overrides)
        {
            if (RxZPortEntry.IsMatch(ov.Name))
            {
                // Store Z[i,j] expression as string; inject referenced scope vars. Inlining first
                // lets a frequency-dependent value that arrived through a cell parameter reach this
                // model as one self-contained expression (see FreqDeferral); a Z[i,j] that is not
                // frequency-dependent through a cell boundary comes back unchanged.
                string zexpr = _freq.InlineForDevice(ov.Expression, parentScope, _evaluator);
                result[ov.Name] = new Value(zexpr);
                InjectZPortScopeVars(zexpr, parentScope, result);
            }
            else
            {
                // Regular numeric parameter — resolve normally.
                try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
                catch { /* skip unresolvable params */ }
            }
        }

        return result;
    }

    private void InjectZPortScopeVars(string expression, Scope scope,
        Dictionary<string, Value> into)
    {
        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch { return; }

        foreach (var name in AstWalker.CollectRefs(ast))
        {
            if (name == "freq") continue;   // reserved injected keyword — not a scope var
            if (into.ContainsKey(name))  continue;
            try
            {
                var val = _evaluator.Resolve(name, scope);
                if (val.Kind is ValueKind.Real or ValueKind.Complex)
                    into[name] = val;
            }
            catch { /* unresolvable — factory will catch real errors */ }
        }
    }

    // ── V_1Tone / V_nTone parameter resolution ────────────────────────────────

    private static readonly Regex RxToneIndexed = new(@"^(V|Freq|Phase)\[(\d+)\]$",
        RegexOptions.Compiled);

    private IReadOnlyDictionary<string, Value> ResolveToneSourceParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        result["ToneSrcName"] = new Value(inst.InstanceName);

        // Collect scope vars that any expression parameter might reference.
        var scopeVarCache = new Dictionary<string, Value>(StringComparer.Ordinal);

        foreach (var ov in inst.Overrides)
        {
            bool isExprParam = ov.Name is "V" or "Vdc" or "Phase"
                || RxToneIndexed.IsMatch(ov.Name);

            if (isExprParam)
            {
                // Try to resolve as a number; if it fails (it's a variable ref), store as string.
                try
                {
                    var val = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);
                    result[ov.Name] = val;
                    // Also store as string so the model can re-evaluate on sweep updates.
                    // Detect if expression was a non-literal by trying to parse and check for refs.
                    var ast = Parser.Parse(ov.Expression);
                    var refs = AstWalker.CollectRefs(ast);
                    if (refs.Count > 0)
                    {
                        // Has variable references → also store the raw expression.
                        result[$"_expr_{ov.Name}"] = new Value(ov.Expression);
                        InjectToneScopeVars(ast, parentScope, scopeVarCache, result);
                    }
                }
                catch
                {
                    result[ov.Name] = new Value(ov.Expression);  // store as string for later eval
                }
            }
            else
            {
                // Freq, NumFreqs, etc. — resolve normally.
                try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
                catch { /* skip */ }
            }
        }

        return result;
    }

    private void InjectToneScopeVars(Expr ast, Scope scope,
        Dictionary<string, Value> cache,
        Dictionary<string, Value> into)
    {
        foreach (var name in AstWalker.CollectRefs(ast))
        {
            if (into.ContainsKey(name) || cache.ContainsKey(name)) continue;
            try
            {
                var val = _evaluator.Resolve(name, scope);
                if (val.Kind is ValueKind.Real or ValueKind.Complex)
                {
                    into[name]  = val;
                    cache[name] = val;
                }
            }
            catch { /* unresolvable */ }
        }
    }

    // ── SnP parameter resolution ──────────────────────────────────────────────
    // File / InterpMode / InterpDomain / ExtrapMode / PinConfig / RefNode are STRING params — store
    // raw, never Eval(). (A file path like "/Users/…/x.s2p" is not an expression.) Only NumPorts is
    // numeric.

    private static readonly HashSet<string> _snpStringParams =
        new(StringComparer.OrdinalIgnoreCase)
            { "File", "InterpMode", "InterpDomain", "ExtrapMode", "PinConfig", "RefNode" };

    private IReadOnlyDictionary<string, Value> ResolveSnpParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var ov in inst.Overrides)
        {
            if (_snpStringParams.Contains(ov.Name))
            {
                // CNL string params are stored with surrounding quotes (e.g. File="path").
                // Strip those outer quotes to get the actual string value.
                var raw = ov.Expression;
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = raw[1..^1];

                // File: resolve a relative path against the workspace root (cross-platform).
                if (ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase))
                    raw = ResolveSnpFilePath(raw);

                result[ov.Name] = new Value(raw);
            }
            else
            {
                // NumPorts and any other numeric override — evaluate normally.
                try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
                catch { /* skip unresolvable; factory will error if a required numeric is missing */ }
            }
        }
        return result;
    }

    // Resolves a relative SnP File path against BaseDirectory (the workspace root); absolute paths and
    // the no-root case pass through unchanged. Cross-platform: Path.* honor the host separator rules,
    // and we tolerate a Windows-authored '\' in a relative path so a netlist ports across OSes.
    private string ResolveSnpFilePath(string file)
    {
        if (string.IsNullOrWhiteSpace(file))       return file;
        if (Path.IsPathRooted(file))               return file;   // absolute on this OS → unchanged
        if (string.IsNullOrEmpty(BaseDirectory))   return file;   // no workspace root → legacy behavior
        var rel = file.Replace('\\', '/');                        // tolerate Windows-authored separators
        return Path.GetFullPath(Path.Combine(BaseDirectory, rel));
    }

    // ── wBond parameter resolution ────────────────────────────────────────────

    /// <summary>
    /// wBond stores <c>Design</c> (the embedded wires — see <c>WBondEmbedding</c>) and <c>File</c>
    /// verbatim, and evaluates everything else — <c>Temp</c>, <c>GroundPlane</c> and any loop-height
    /// override — as an ordinary expression, which is what makes a parametric sweep over a loop
    /// height work.
    ///
    /// <para><c>Design</c> is a payload, not an expression: it must NOT go anywhere near the
    /// evaluator, which would read it as an identifier and fail. <c>File</c> is still honoured (a
    /// hand-authored <c>.cnl</c> may name a <c>.wBond</c> directly) and is resolved against the
    /// workspace root exactly as SnP's is; a schematic no longer writes one.</para>
    /// </summary>
    private IReadOnlyDictionary<string, Value> ResolveWBondParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var ov in inst.Overrides)
        {
            if (ov.Name.Equals("Design", StringComparison.OrdinalIgnoreCase))
            {
                var payload = ov.Expression;
                if (payload.Length >= 2 && payload[0] == '"' && payload[^1] == '"')
                    payload = payload[1..^1];
                result[ov.Name] = new Value(payload);
            }
            else if (ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase))
            {
                var raw = ov.Expression;
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = raw[1..^1];
                result[ov.Name] = new Value(ResolveSnpFilePath(raw));
            }
            else if (ov.Name.Equals("RefPin", StringComparison.OrdinalIgnoreCase))
            {
                // Verbatim, like Design: the schematic writes the WORD "true"/"false", and running
                // that through the evaluator would depend on whether a bare `true` happens to parse
                // as a literal — a dependency with nothing to gain. The factory reads either
                // spelling. It is not sweepable and there is nothing to sweep it over.
                result[ov.Name] = new Value(ov.Expression);
            }
            else
            {
                try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
                catch { /* skip unresolvable; the factory errors if a required numeric is missing */ }
            }
        }
        return result;
    }

    // ── P1Tone parameter resolution ───────────────────────────────────────────

    private static readonly Regex RxP1ToneZEntry = new(@"^Z\[(\d+)\]$", RegexOptions.Compiled);
    private static readonly Regex RxP1ToneGEntry = new(@"^G\[(\d+)\]$", RegexOptions.Compiled);

    private IReadOnlyDictionary<string, Value> ResolveP1ToneParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        result["P1ToneName"] = new Value(inst.InstanceName);

        foreach (var ov in inst.Overrides)
        {
            // Z[k] and G[k] may be complex; store as-is for the factory to parse.
            if (RxP1ToneZEntry.IsMatch(ov.Name) || RxP1ToneGEntry.IsMatch(ov.Name))
            {
                try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
                catch { /* skip unresolvable */ }
            }
            else
            {
                try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
                catch { /* skip unresolvable */ }
            }
        }

        return result;
    }

    // ── PnTone parameter resolution (per-tone Freq[i]/Pavl[i]/Phase[i] + shared Z/Z[k]) ──────────

    private IReadOnlyDictionary<string, Value> ResolvePnToneParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        result["PnToneName"] = new Value(inst.InstanceName);

        // All PnTone overrides are numeric expressions with units (Freq[i] in Hz/GHz, Pavl[i] in dBm,
        // Phase[i] in deg, Z/Z[k] in Ω). Evaluate each with its declared unit, like P1Tone.
        foreach (var ov in inst.Overrides)
        {
            try { result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit); }
            catch { /* skip unresolvable — degrades gracefully */ }
        }

        return result;
    }

    // Port voltage names in SDD equations — _v1, _v2, … (injected at eval time, not scope vars).
    private static readonly Regex RxPortVoltage = new(@"^_v\d+$", RegexOptions.Compiled);
    // Control current names — _c1, _c2, … (injected by the engine, not scope vars).
    private static readonly Regex RxControlCurrent = new(@"^_c\d+$", RegexOptions.Compiled);
    // SDD equation parameter name pattern — matches I[...], Q[...], F[...], C[...], i[...].
    private static readonly Regex RxSddEquation = new(@"^[IFCQiH][^\[]*\[", RegexOptions.Compiled);

    private IReadOnlyDictionary<string, Value> ResolveSddParameters(
        Instance inst,
        Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);

        // Port count = half the net count (2N nets in +/− pairs).
        int netCount = inst.NetBindings.Count;
        if (netCount % 2 != 0)
            throw new InvalidOperationException(
                $"SDD '{inst.InstanceName}': expected an even number of nets (2 per port: +,−); " +
                $"got {netCount}. An SDD<k> needs 2k nets.");
        int portCount = netCount / 2;
        result["SddPortCount"] = new Value((double)portCount);
        result["SddName"]      = new Value(inst.InstanceName);

        foreach (var ov in inst.Overrides)
        {
            // Equation parameters (I[p,w], F[p,w], C[n], etc.) — store raw expression as String.
            // The factory will parse and validate them.
            if (RxSddEquation.IsMatch(ov.Name) || IsNoiseEntry(ov.Name))
            {
                result[ov.Name] = new Value(ov.Expression);
                // Resolve scope variables referenced by this equation and inject them.
                InjectSddScopeVars(ov.Expression, parentScope, result);
                continue;
            }

            // Regular parameter (unlikely for SDD in v1, but supported for future use).
            result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);
        }

        return result;
    }

    private void InjectSddScopeVars(
        string expression,
        Scope scope,
        Dictionary<string, Value> into)
    {
        Expr ast;
        try { ast = Parser.Parse(expression); }
        catch { return; }  // parse failure handled later in the factory

        var refs = AstWalker.CollectRefs(ast);
        foreach (var name in refs)
        {
            if (RxPortVoltage.IsMatch(name))    continue;   // _v1, _v2 — injected at eval time
            if (RxControlCurrent.IsMatch(name)) continue;  // _c1, _c2 — injected by engine at eval time
            if (into.ContainsKey(name))         continue;  // already injected by a prior equation

            var binding = scope.Lookup(name);
            if (binding is null) continue;                  // unknown name — factory will error later

            try
            {
                var val = _evaluator.Resolve(name, scope);
                if (val.Kind == ValueKind.Real)
                    into[name] = val;
                else if (val.Kind == ValueKind.Complex)
                    throw new InvalidOperationException(
                        $"SDD equation references '{name}' which resolved to a Complex value; " +
                        $"SDD equations are real-only");
                // Bool/String — silently skip (factory will catch actual type errors)
            }
            catch (UnresolvedNameException) { /* skip */ }
        }
    }

    private static bool IsNoiseEntry(string name) =>
        name.StartsWith("In[", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Nc[", StringComparison.OrdinalIgnoreCase);

    // ── Library lookup ────────────────────────────────────────────────────────

    private Cell? FindCell(string name)
    {
        foreach (var lib in _libraries)
        {
            var c = lib.Find(name);
            if (c != null) return c;
        }
        return null;
    }

    // ── Layer-3 linter ────────────────────────────────────────────────────────

    /// <summary>
    /// True when the test bench has an S-parameter analysis that will actually run — a directly
    /// enabled <see cref="SParameterAnalysis"/>, an enabled parametric-sweep chain that bottoms out
    /// at one, or an enabled raw <c>type=sparam</c> directive. Gates the Term-Num lint so it never
    /// fires on a bench that runs only HB / DC / loadpull (where Num is irrelevant).
    /// </summary>
    private static bool HasRunnableSParam(TestBench tb)
    {
        // Names referenced as the inner of any sweep are chain members, not roots.
        var innerNames = tb.Analyses
            .OfType<ParametricSweepAnalysis>()
            .Select(ps => ps.InnerAnalysisName)
            .ToHashSet(System.StringComparer.Ordinal);

        foreach (var top in tb.Analyses)
        {
            if (innerNames.Contains(top.Name)) continue;        // not a chain root
            if (!AnalysisChain.IsChainRunnable(top, tb)) continue;

            // Descend past sweeps to the runnable base.
            Analysis? baseAnalysis = top;
            int guard = 0;
            while (baseAnalysis is ParametricSweepAnalysis ps && guard++ < 64)
                baseAnalysis = AnalysisChain.ResolveEffectiveInner(ps.InnerAnalysisName, tb);

            if (baseAnalysis is SParameterAnalysis) return true;
        }

        // Raw directives: an "analysis … type=sparam" line that is not explicitly disabled.
        foreach (var d in tb.RawDirectives)
        {
            if (!d.Kind.Equals("analysis", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (d.RawLine.IndexOf("type=sparam", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (d.RawLine.IndexOf("enabled=false", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks top-level Term/Port components for duplicate or missing port Num values.
    /// Top-level = InstancePath with no dot (not inside an instantiated sub-cell).
    /// Warnings are added to <paramref name="netlist"/> and emitted to Console.Error.
    /// </summary>
    private static void LintTopLevelTerms(ElaboratedNetlist netlist)
    {
        var topTerms = netlist.Components
            .Where(ec => (ec.Model is PortModel or TermModel or P1ToneModel) && !ec.InstancePath.Contains('.'))
            .ToList();

        if (topTerms.Count == 0) return;

        var numToPath = new Dictionary<int, string>();
        foreach (var ec in topTerms)
        {
            if (!ec.Parameters.TryGetValue("Num", out var v))
            {
                netlist.AddWarning(
                    $"Term '{ec.InstancePath}' has no Num parameter and will be ignored by S-parameter analysis; add Num=<index>.");
                continue;
            }

            int num = (int)v.AsReal();
            if (numToPath.TryGetValue(num, out var existing))
                netlist.AddWarning(
                    $"Duplicate S-parameter port Num={num} on Terms '{existing}' and '{ec.InstancePath}'; port assignment is ambiguous.");
            else
                numToPath[num] = ec.InstancePath;
        }

        // Check for gaps in the port numbering (e.g. {1,3} is missing 2).
        if (numToPath.Count > 0)
        {
            int maxNum = numToPath.Keys.Max();
            for (int n = 1; n <= maxNum; n++)
            {
                if (!numToPath.ContainsKey(n))
                    netlist.AddWarning(
                        $"S-parameter port Num={n} is missing; Terms are numbered " +
                        $"{string.Join(", ", numToPath.Keys.OrderBy(k => k))}.");
            }
        }
    }
}
