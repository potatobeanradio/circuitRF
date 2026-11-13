using System.Globalization;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Matching;
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
    /// Every placed <c>VProbe</c>, as (its own instance path, the net its one terminal names) —
    /// collected during the flatten and RESOLVED afterwards, in <see cref="RegisterVoltageProbes"/>.
    ///
    /// <para>Afterwards, deliberately. A probe must never mint the net it points at: minting one
    /// would add a matrix row that nothing else touches and turn a stray reference into a singular
    /// solve, which is the one way a probe could change the answer. Resolving after the walk means
    /// every net that exists is known, so a name that is still absent is genuinely absent and is
    /// reported as such. It also means the collision check can see EVERY net name, including the
    /// ones minted later in the same walk.</para>
    /// </summary>
    private readonly List<(string Path, string Net)> _voltageProbes = [];

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

    /// <summary>
    /// The draws of one Monte Carlo trial, or null (the default) for an ordinary elaboration, where every
    /// distribution call evaluates to its nominal. Passed to THIS elaboration's evaluator — never a global.
    /// </summary>
    public IStatisticalDraws? Statistics
    {
        get => _evaluator.Statistics;
        init => _evaluator.Statistics = value;
    }

    /// <summary>
    /// The global scope this elaboration built and resolved through, or null before
    /// <see cref="Elaborate"/> has run.
    ///
    /// <para>Held so a caller can ask what an expression means IN THE DESIGN'S OWN SCOPE without
    /// running an analysis — which is what <c>circuitrf explain --expr</c> is
    /// (brief-automation-4-check-and-explain.md R-aut4-7). It is the scope itself rather than a copy:
    /// the ambient binding this method adds when the design states none, the units on each variable,
    /// and every <c>--set</c> override that replaced a variable before elaboration are all already
    /// in it, and a second scope assembled from <c>TestBench.GlobalVariables</c> would be missing the
    /// first and would drift from the third.</para>
    /// </summary>
    public Scope? GlobalScope { get; private set; }

    /// <summary>
    /// Evaluates <paramref name="expression"/> in <see cref="GlobalScope"/>, through the same
    /// <see cref="Evaluator"/> the elaboration used — so its memo cache, its registered user
    /// functions and its cycle detection all apply, and the answer is the one the design's own
    /// expressions would get. Never string substitution (<c>docs/design/expressions.md</c>).
    ///
    /// <para>Throws whatever the expression engine throws — an unresolved name, a cycle, a parse
    /// error. The caller reports it; this does not soften it into a null.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Elaborate"/> has not run.</exception>
    public Value EvaluateInGlobalScope(string expression)
    {
        var scope = GlobalScope
            ?? throw new InvalidOperationException("Elaborate() has not run, so there is no global scope yet.");
        return _evaluator.Eval(expression, scope);
    }

    public ElaboratedNetlist Elaborate(TestBench tb)
    {
        try { return ElaborateCore(tb); }
        catch (UnresolvedNameException ex) when (SkippedLineNaming(tb, ex.Name) is { } why)
        {
            throw new UnresolvedNameException(ex.Name, ex.Scope, why);
        }
    }

    /// <summary>
    /// When a name is unresolved and the reader skipped a line that mentions it, that skipped line is
    /// almost always where the name was meant to be defined (`var x=1`, `param x=1`): say so in the
    /// error itself. The read warning alone travels on the elaborated netlist, which a failed
    /// elaboration never returns, so every caller printing only the exception lost the cause.
    /// </summary>
    private static string? SkippedLineNaming(TestBench tb, string name)
    {
        var word = new Regex($@"(?<![\w.]){Regex.Escape(name)}(?![\w])");
        foreach (var w in tb.ReadWarnings)
        {
            int cut = w.IndexOf(" is not a statement the netlist reader knows", StringComparison.Ordinal);
            if (cut < 0 || !word.IsMatch(w[..cut])) continue;
            string where = w[..cut];
            bool keyword = Regex.IsMatch(where, $@"'\S+\s+{Regex.Escape(name)}\s*=");
            return $"the netlist reader skipped {where} as an unknown statement" +
                   (keyword ? $"; a global variable is written '{name} = <value>', with no keyword before it" : "");
        }
        return null;
    }

    private ElaboratedNetlist ElaborateCore(TestBench tb)
    {
        // User-defined expression functions must exist before any expression is resolved —
        // a cell parameter may call one in its default, which is evaluated during flattening.
        // Kept as well as registered: a model that evaluates an expression at STAMP time builds
        // its own Evaluator per frequency, long after this one is gone, and needs the same table.
        _functions = tb.Functions.ToArray();
        foreach (var fn in _functions)
            _evaluator.RegisterFunction(fn);
        _voltageProbes.Clear();
        // A trial's draws ride on the bench (TestBench.StatisticalDraws), so an engine's own re-elaboration of
        // it draws the same values; one set on this elaborator wins.
        _evaluator.Statistics ??= tb.StatisticalDraws;

        var netlist     = new ElaboratedNetlist();
        _evaluator.Warn = m => netlist.AddWarningOnce("validrange:" + m, m);

        // What reading the netlist decided (TestBench.ReadWarnings) — reported by the run like
        // anything the run itself decides, so no consumer needs to know the list exists.
        foreach (var w in tb.ReadWarnings) netlist.AddWarningOnce("read:" + w, w);
        foreach (var n in tb.ReadNotes)    netlist.AddNoteOnce("read:" + n, n);

        var globalScope = BuildGlobalScope(tb);
        GlobalScope     = globalScope;
        _globalNames    = [.. tb.GlobalVariables.Select(v => v.Name).Distinct(StringComparer.Ordinal)];

        // Ambient must be known BEFORE flattening: models are constructed during the walk, and a
        // temperature-aware one bakes its temperature in at construction.
        _ambientC = ResolveAmbient(tb, globalScope, netlist);
        netlist.AmbientC = _ambientC;

        // THE AMBIENT IS READABLE BY NAME FROM ANY EXPRESSION, whether or not the design states a
        // global for it. A design that does state one already binds it above and is untouched here;
        // what this adds is the case where nothing states it, where the ambient is nonetheless a
        // known number and there is nowhere for an expression naming it to resolve.
        //
        // A netlist read from the SPICE dialect refers to the simulator's own temperature variable
        // by exactly this name — a device that drives its thermal node from the global ambient
        // rather than exposing a pin has no other way to say so — and an unbound name fails the
        // whole cell rather than that one expression.
        if (globalScope.Lookup(Temperature.AmbientGlobalName) is null)
            globalScope.Bind(Temperature.AmbientGlobalName,
                             _ambientC.ToString(CultureInfo.InvariantCulture));

        // The TestBench's instance list IS the root frame — no TopCell lookup.
        FlattenInstances(
            tb.Instances,
            instancePathPrefix: "",
            parentNetMap:       null,
            currentScope:       globalScope,
            globalScope:        globalScope,
            netlist:            netlist);

        // Post-flatten: an external device's thermal terminal that nothing else reaches has no
        // operating point at all until the host supplies its reference. Runs before anything below
        // reads the component list.
        PinUnreferencedThermalNodes(netlist);

        // Post-flatten: a Deembed path grounded at both ends is not removed, and nothing else says so.
        WarnDeembedPathsGroundedAtBothEnds(netlist);

        // Post-flatten: resolve mutual inductance references now that all inductors exist.
        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel m)
                m.Resolve(netlist, ec);

        // Propagate label provenance from TestBench (top-level names only; no path prefix needed).
        foreach (var name in tb.LabeledNets)
            netlist.Nodes.LabeledNames.Add(name);

        // Voltage probes, now that every net that exists has a name and an index.
        RegisterVoltageProbes(netlist);

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

        netlist.StatisticalCalls    = [.. _evaluator.StatisticalCalls];
        netlist.StatisticalProblems = [.. _evaluator.StatisticalProblems];

        // Layer-3 linter: check top-level Terms for Num consistency. The Num parameter is meaningful
        // ONLY to S-parameter analysis, so this lint runs only when an S-parameter analysis will
        // actually run — otherwise it fires spuriously on HB/DC/loadpull-only test benches.
        if (HasRunnableSParam(tb))
            LintTopLevelTerms(netlist);

        return netlist;
    }

    /// <summary>
    /// Holds every EXTERNAL thermal terminal that nothing else in the design reaches at the
    /// ambient temperature, by adding the source the design did not.
    ///
    /// <para><b>Why the host has to do this.</b> A compiled electrothermal model does not contain
    /// its own thermal RC even when it declares thermal parameters — it consumes a junction
    /// temperature and produces a dissipated power, and the loop between them is closed outside the
    /// model. Confirmed on two unrelated model families: sweeping their declared thermal-resistance
    /// parameters over eight orders of magnitude changes nothing in their output. So an unconnected
    /// thermal pin is not a harmless open like an unconnected electrical pin. It is a floating node
    /// fed by a constant current source, and it has NO DC solution: the temperature runs away until
    /// it hits the absolute-zero floor, and the solve grinds through every ramp step and every
    /// halving before failing — measured at 6,210 Newton iterations on a kit, with a residual
    /// that names nothing and a bias point the ramp never even reached.</para>
    ///
    /// <para><b>Why the ambient, and why an ideal source.</b> A design that states no thermal network
    /// has stated no thermal resistance, so there is no rise to add: the part sits at the ambient.
    /// That is the same thing a bench does by hand when it pins a part at a stated case temperature,
    /// it needs no knowledge of any vendor's parameter names, and it is the reading that cannot be
    /// wrong in a way the user cannot see — a rise of zero is visible in the answer, whereas a
    /// guessed thermal resistance is not.</para>
    ///
    /// <para><b>And it is announced, never silent.</b> The whole point is that the design did not say
    /// this, so the warning says what was supplied and what to do instead.</para>
    ///
    /// <para><b>And the device is ASKED, not assumed.</b> A provider is entitled to carry its own
    /// thermal resistance internally, and one that does has already referenced the node — its
    /// Jacobian has a real entry there, the open pin is not floating at all, and the rise it solves
    /// for is the answer the design wanted. Holding that node at the ambient would silently delete
    /// its self-heating. So the last question asked before adding anything is whether the device's
    /// own Jacobian references the node, at the same all-zero point the solve's own ramp starts
    /// from. A device that refuses that point is left exactly as it was.</para>
    ///
    /// <para>INTERNAL thermal nodes are deliberately left alone. The user cannot wire one, so an
    /// internal node with no reference is the provider's own bug, and pinning it would hide it.</para>
    /// </summary>
    private static void WarnDeembedPathsGroundedAtBothEnds(ElaboratedNetlist netlist)
    {
        foreach (var ec in netlist.Components)
        {
            if (ec.Model is not SnpModel { Inverse: true } snp) continue;
            int k = snp.PortCount / 2;
            if (k < 1 || ec.Nodes.Length < 2 * k) continue;
            var both = Enumerable.Range(0, k)
                .Where(i => ec.Nodes[i] == ec.ReferenceNode && ec.Nodes[i + k] == ec.ReferenceNode)
                .Select(i => $"{i + 1} and {i + 1 + k}").ToList();
            if (both.Count == 0) continue;
            netlist.AddWarningOnce($"deembed-grounded-path:{ec.InstancePath}",
                $"{ComponentModelFactory.DeembedTypeName} '{ec.InstancePath}': pins {string.Join(", ", both)} are both on ground. " +
                "They are the two ends of one path through the network it removes, and with ground at both ends the current " +
                "leaving one is not the current entering the other, so that path is not removed and the result is wrong. " +
                "Put the pin on the part's side on the part's own terminal, on a net of its own (for a FET, the device's " +
                "source), and keep the other pin on ground.");
        }
    }

    private void PinUnreferencedThermalNodes(ElaboratedNetlist netlist)
    {
        var thermalOwners = new Dictionary<int, string>();
        var reachedByAnythingElse = new HashSet<int>();

        foreach (var ec in netlist.Components)
        {
            var ownThermal = new HashSet<int>();

            if (ec.Model is ExternalDeviceModel ed)
            {
                foreach (var n in ed.Descriptor.Nodes)
                {
                    if (n.QuantityKind != NodeQuantityKind.Thermal || !n.External) continue;

                    // The elaborator's own ground-referenced pair layout: node k spans [2k], [2k+1].
                    int np = ec.Nodes.Length > 2 * n.Index ? ec.Nodes[2 * n.Index] : 0;
                    if (np <= 0) continue;                       // grounded is a stated reference

                    ownThermal.Add(np);
                    thermalOwners.TryAdd(np, $"{ec.ComponentType}:{ec.InstancePath}");
                }
            }

            // Anything this component touches that is not one of ITS OWN thermal pins counts as a
            // reference. Written this way so a thermal net shared between two devices and reaching
            // nothing else is still recognised as unreferenced — which it is.
            foreach (int nd in ec.Nodes)
                if (nd > 0 && !ownThermal.Contains(nd)) reachedByAnythingElse.Add(nd);
        }

        var thermalSelfReferenced = SelfReferencedThermalNodes(netlist);

        foreach (var (node, owner) in thermalOwners.OrderBy(e => e.Key))
        {
            if (reachedByAnythingElse.Contains(node)) continue;
            if (thermalSelfReferenced.Contains(node)) continue;

            string name  = node < netlist.Nodes.Count ? netlist.Nodes.NameOf(node) : $"node {node}";
            var    model = ComponentModelFactory.TryCreate(
                               "Vdc",
                               new Dictionary<string, Value>(StringComparer.Ordinal),
                               _functions,
                               _ambientC);
            if (model is null) continue;

            netlist.AddComponent(new ElaboratedComponent(
                "Vdc",
                $"__ambient__{name}",
                [node, 0],
                new Dictionary<string, Value>(StringComparer.Ordinal) { ["Vdc"] = new Value(_ambientC) },
                model));

            netlist.AddWarningOnce(
                $"thermal-pin-pinned-at-ambient:{node}",
                $"{owner}: the thermal terminal '{name}' is not connected to anything, so circuitRF " +
                $"is holding it at the ambient {_ambientC:0.##} °C — the part is simulated with no " +
                $"temperature rise. Left floating it has no operating point at all. To model self-" +
                $"heating, connect it through the part's thermal resistance to a source holding the " +
                $"ambient.");
        }
    }

    /// <summary>
    /// Which thermal nodes their own device already references — the ones the device's own thermal network holds by a
    /// real conductance (<see cref="ExternalThermalPaths"/>), so the node has a path to a solution without the host adding
    /// one. That is the net's, not the pin's own diagonal: a pin collapsed onto an internal node carries nothing on its own
    /// entry (issue 67).
    ///
    /// <para><b>A real path means a POSITIVE conductance, not a non-zero one.</b> An electrothermal
    /// model's thermal row is routinely non-zero and NEGATIVE — that entry is its self-heating
    /// feedback, which pushes the node away from a solution rather than holding it near one. Reading
    /// "non-zero" as "referenced" therefore leaves the exact devices this exists for unpinned, and
    /// the sign is what tells the two apart. The magnitude has to clear the same line the engine
    /// draws for a thermal resistance at all, since a conductance of 1e-12 is a path in the same
    /// sense a keep-alive leak resistor is.</para>
    ///
    /// <para>Asked at the all-zero point, which is not an arbitrary choice: it is where the solve's
    /// own bias ramp starts, so any device that can be solved at all answers there. A device that
    /// refuses is reported as self-referenced — the conservative reading, because it leaves the
    /// design exactly as the user wrote it rather than adding a source on the strength of a question
    /// that was never answered.</para>
    /// </summary>
    private static HashSet<int> SelfReferencedThermalNodes(ElaboratedNetlist netlist)
    {
        var selfReferenced = new HashSet<int>();

        foreach (var ec in netlist.Components)
        {
            if (ec.Model is not ExternalDeviceModel ed) continue;

            var thermalPins = ed.Descriptor.Nodes
                .Where(n => n.QuantityKind == NodeQuantityKind.Thermal && n.External)
                .ToList();
            if (thermalPins.Count == 0) continue;

            NonlinearResult res;
            try   { res = ec.Evaluate(new PortVoltages(new double[ec.Model.PortCount]), ControlCurrents.Empty); }
            catch { foreach (var n in thermalPins) MarkPin(ec, n, selfReferenced); continue; }

            // Not the pin's own diagonal: a model that collapses its pin onto an internal node puts 1/Rth at that node's
            // index, on the pin's net (issue 67). ExternalThermalPaths sums what lands on the net, through the model's
            // own thermal couplings.
            var held = ExternalThermalPaths.HeldByDevice(ec, ed, res.Dg);
            foreach (var n in thermalPins)
            {
                int np = ec.Nodes.Length > 2 * n.Index ? ec.Nodes[2 * n.Index] : 0;
                if (held.TryGetValue(np, out double g) && g > 1.0 / Temperature.ImplausibleThermalResistanceCPerW)
                    MarkPin(ec, n, selfReferenced);
            }
        }

        return selfReferenced;

        static void MarkPin(ElaboratedComponent ec, ExternalNodeDescriptor n, HashSet<int> into)
        {
            int np = ec.Nodes.Length > 2 * n.Index ? ec.Nodes[2 * n.Index] : 0;
            if (np > 0) into.Add(np);
        }
    }

    /// <summary>
    /// Which of a device's nodes the model writes NO equation for — confirmed over several bias
    /// points, not taken on the provider's word at one.
    ///
    /// <para><b>Why the provider's own flag is a trigger and not the answer.</b> A worker measures
    /// "degenerate" as an empty Jacobian row at the ONE point it probed, which is normally the
    /// origin — and at the origin an ordinary FET's drain row is empty too, because the device is
    /// off. Refusing on that flag alone refuses every working device whose provider probes cold.
    /// What separates the two is whether the row is empty EVERYWHERE: a node the model never writes
    /// stays empty at any bias, while a node that is merely off comes to life as soon as the device
    /// does.</para>
    ///
    /// <para><b>And the column is half the test.</b> A row that is empty while its column is empty
    /// too is an inert node — nothing reads it, gmin pins it, and it costs nothing. The fatal shape
    /// is an empty row whose column is NOT: the model reads a value it never determines, so the node
    /// can be neither solved for nor pinned, and every equation that reads it inherits whatever the
    /// solver wandered to.</para>
    ///
    /// <para>A point the model refuses is skipped rather than counted; if it refuses all of them,
    /// nothing is claimed and the design is left exactly as written.</para>
    /// </summary>
    private static List<int> UnwrittenNodes(ExternalDeviceModel model)
    {
        var d = model.Descriptor;

        var suspects = d.Nodes
            .Where(n => n.Degenerate && n.SlavedTo is null && !n.CollapsedToGround)
            .Select(n => n.Index)
            .Where(i => i < d.NodeCount)
            .ToList();
        if (suspects.Count == 0) return [];

        // Small, scattered, and all positive: large enough that a device is on, small enough that no
        // model refuses the point, and never uniform — a uniform vector puts zero volts across every
        // pair of nodes, which is the degenerate case all over again.
        double[][] points =
        [
            [0.05, 0.11, 0.02, 0.08],
            [0.20, 0.05, 0.13, 0.02],
            [0.02, 0.17, 0.09, 0.20],
        ];

        var writesNothing = suspects.ToHashSet();
        var isRead        = new HashSet<int>();
        int answered      = 0;

        foreach (var pattern in points)
        {
            var v = new double[model.PortCount];
            for (int k = 0; k < v.Length; k++) v[k] = pattern[k % pattern.Length];

            NonlinearResult r;
            try   { r = model.Evaluate(new PortVoltages(v)); }
            catch { continue; }                       // refused this point: it says nothing

            int n = Math.Min(r.Dg.GetLength(0), r.Dg.GetLength(1));
            if (n == 0) continue;
            answered++;

            double maxRow = 0.0;
            for (int i = 0; i < n; i++)
            {
                double row = 0.0;
                for (int k = 0; k < n; k++) row += Math.Abs(r.Dg[i, k]);
                if (row > maxRow) maxRow = row;
            }
            if (maxRow <= 0.0) continue;

            foreach (int i in suspects)
            {
                if (i >= n) continue;

                double row = 0.0, col = 0.0;
                for (int k = 0; k < n; k++) { row += Math.Abs(r.Dg[i, k]); col += Math.Abs(r.Dg[k, i]); }

                if (row > EmptyRowFraction * maxRow) writesNothing.Remove(i);
                if (col > EmptyRowFraction * maxRow) isRead.Add(i);
            }
        }

        if (answered == 0) return [];

        return [.. suspects.Where(i => writesNothing.Contains(i) && isRead.Contains(i))];
    }

    /// <summary>
    /// Which node each unwritten node follows, MEASURED from the model's own derivatives — or null
    /// when the measurement does not separate the candidates.
    ///
    /// <para><b>What is being measured, and why it answers the question.</b> A compact model ships
    /// analytic derivatives of its own currents. If it wrote those derivatives assuming a node is not
    /// independent — that it carries some other node's voltage — then evaluating it with that node
    /// held separately makes its analytic Jacobian disagree with a finite difference of its own
    /// current. Feed the node the RIGHT voltage and the two agree again. So the disagreement is a
    /// direct read on whether a candidate is the one the model was written for; nothing here knows
    /// anything about any particular model, only that a model's derivatives should match its own
    /// currents.</para>
    ///
    /// <para><b>Measured on a real compiled model</b> (two unwritten nodes, six candidate pairings):
    /// the correct pairing scored 0.0308 against 0.0417 and 0.0556 for the wrong ones, and it was the
    /// only one that also produced the right drain current. Ties happen and are benign — where two
    /// candidates score identically the model genuinely cannot tell them apart, and neither choice
    /// changes its output.</para>
    ///
    /// <para><b>The margin is the safety rule.</b> A wrong choice here converges to a wrong number
    /// rather than failing, so a ranking that is flat is not a weak answer, it is no answer: unless
    /// the best candidate beats the worst by <see cref="MinMasterSeparation"/> the whole thing comes
    /// back null and the caller stops. Deciding by a hair is the one outcome worse than stopping.</para>
    ///
    /// <para>Solved one node at a time, each choice fixed before the next is measured, because the
    /// nodes are read by the same equations and measuring one while another is still wrong measures
    /// both at once.</para>
    /// </summary>
    private static (Dictionary<int, int> Masters, string Evidence)? DeriveMasters(
        ExternalDeviceModel model, IReadOnlyList<int> unwritten)
    {
        var d          = model.Descriptor;
        int n          = d.NodeCount;
        var unwrittenSet = unwritten.ToHashSet();

        // A master must be a node the model actually solves for: not one of the unwritten nodes
        // (which would answer nothing), and not a temperature, since a voltage does not follow one.
        var candidates = d.Nodes
            .Where(x => !unwrittenSet.Contains(x.Index)
                     && x.QuantityKind != NodeQuantityKind.Thermal
                     && !x.CollapsedToGround
                     && x.Index < n)
            .Select(x => x.Index)
            .ToList();
        if (candidates.Count < 2) return null;   // nothing to choose between

        var masters = new Dictionary<int, int>();
        var evidence = new List<string>();

        foreach (int k in unwritten)
        {
            double best = double.PositiveInfinity, worst = 0.0;
            int    pick = -1;

            foreach (int m in candidates)
            {
                var trial = new Dictionary<int, int>(masters) { [k] = m };
                double score = DerivativeDisagreement(model, trial, n);
                if (double.IsNaN(score)) continue;

                if (score > worst) worst = score;
                if (score < best - TieWidth) { best = score; pick = m; }
            }

            if (pick < 0 || worst <= 0.0) return null;
            if ((worst - best) / worst < MinMasterSeparation) return null;

            masters[k] = pick;
            evidence.Add($"node {k} follows node {pick} ({best:G3} against {worst:G3} for the worst " +
                         $"candidate)");
        }

        return (masters, string.Join("; ", evidence));
    }

    /// <summary>
    /// How badly the model's analytic Jacobian disagrees with a finite difference of its own current,
    /// with <paramref name="substitution"/> applied — each listed node given its master's voltage
    /// before the model is called, and its column folded onto the master's by the chain rule, which
    /// is exactly what slaving the node in the matrix will do.
    ///
    /// <para>Scaled by the model's own unsubstituted Jacobian magnitude, so scores are comparable
    /// across candidates AND across devices: the absolute size of a device's conductances says
    /// nothing about whether its derivatives are right. NaN when the model refuses every point,
    /// which is not a score of zero — it is the absence of one.</para>
    /// </summary>
    private static double DerivativeDisagreement(
        ExternalDeviceModel model, Dictionary<int, int> substitution, int n)
    {
        double[][] points =
        [
            [0.05, 0.11, 0.02, 0.08],
            [0.20, 0.05, 0.13, 0.02],
            [0.02, 0.17, 0.09, 0.20],
        ];

        double total = 0.0;
        int    scored = 0;

        foreach (var pattern in points)
        {
            var v = new double[model.PortCount];
            for (int k = 0; k < v.Length; k++) v[k] = pattern[k % pattern.Length];

            // The scale is taken from the model UNSUBSTITUTED, so it is the same number for every
            // candidate. Dividing instead by each candidate's own Jacobian magnitude would rank
            // candidates by how large their derivatives happen to be as much as by whether those
            // derivatives are right, and two candidates disagreeing by exactly the same amount would
            // come back with different scores.
            var reference = TryEvaluate(model, v, []);
            if (reference is not { } r0) continue;

            double scale = 0.0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) scale += Math.Abs(r0.Dg[i, j]);
            if (scale <= 0.0) continue;

            var baseline = TryEvaluate(model, v, substitution);
            if (baseline is not { } b) continue;

            double num = 0.0;
            bool   ok  = true;

            foreach (int j in Enumerable.Range(0, n).Where(x => !substitution.ContainsKey(x)))
            {
                var vj = (double[])v.Clone();
                vj[j] += DerivativeStep;

                var moved = TryEvaluate(model, vj, substitution);
                if (moved is not { } mv) { ok = false; break; }

                for (int i = 0; i < n; i++)
                {
                    // Analytic, with the chain rule: moving j moves every node slaved to it too.
                    double analytic = b.Dg[i, j];
                    foreach (var (slave, master) in substitution)
                        if (master == j) analytic += b.Dg[i, slave];

                    double finite = (mv.I[i] - b.I[i]) / DerivativeStep;

                    num += Math.Abs(analytic - finite);
                }
            }

            if (!ok) continue;
            total += num / scale;
            scored++;
        }

        return scored == 0 ? double.NaN : total / scored;
    }

    /// <summary>
    /// Evaluates the model with a substitution applied to the voltage vector, or null if it refuses
    /// the point. A refused point is skipped rather than scored — a model saying "not here" is not
    /// evidence about which node follows which.
    /// </summary>
    private static NonlinearResult? TryEvaluate(
        ExternalDeviceModel model, double[] v, Dictionary<int, int> substitution)
    {
        var applied = (double[])v.Clone();
        foreach (var (slave, master) in substitution)
            if (slave < applied.Length && master < applied.Length) applied[slave] = applied[master];

        try   { return model.Evaluate(new PortVoltages(applied)); }
        catch { return null; }
    }

    /// <summary>Step for the finite difference the model's own derivatives are checked against.</summary>
    private const double DerivativeStep = 1e-4;

    /// <summary>
    /// How much better than the WORST candidate the winner has to be for the ranking to count as an
    /// answer rather than as noise. Deliberately a spread over the whole field rather than a gap to
    /// the runner-up: two candidates the model cannot tell apart score the same, legitimately, and a
    /// runner-up rule would read that agreement as a failure.
    /// </summary>
    private const double MinMasterSeparation = 0.05;

    /// <summary>Scores closer together than this are one score; the lower node index wins.</summary>
    private const double TieWidth = 1e-9;

    /// <summary>
    /// How small a Jacobian row has to be, against the largest row in the same matrix, to count as
    /// not written at all. Measured on a real compiled model: the rows in question came back eleven
    /// orders of magnitude down (2e-8 against 336), so there is nothing marginal about the
    /// separation and the exact figure is not load-bearing.
    /// </summary>
    private const double EmptyRowFraction = 1e-6;

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

    private Scope BuildCellScope(Cell cell, Scope parentScope, IEnumerable<ParameterAssignment> overrides, string scopeName,
                                 string instanceName)
    {
        // The scope's instance path is what makes a distribution in the cell a MISMATCH draw (yield overview D6).
        var cellScope = new Scope(scopeName, parentScope) { InstancePath = scopeName };

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
                // enforced through Eval's own predicate rather than a second copy of it. The site unit
                // goes to the bare additive operands instead, as Eval gives it to them.
                bool unitBearing = Evaluator.IsUnitBearing(ov.Expression, parentScope);

                cellScope.Bind(
                    ov.Name,
                    _freq.InlineForCellBoundary(ov.Expression, parentScope, _evaluator, unitBearing ? ov.Unit : null),
                    unitBearing ? null : ov.Unit);
                continue;
            }

            var resolved = _evaluator.EvalParameter(ov.Expression, parentScope, ov.Unit, $"{instanceName}.{ov.Name}");
            cellScope.Bind(ov.Name, "__resolved__");
            _evaluator.InjectResolved(scopeName, ov.Name, resolved);
        }

        return cellScope;
    }

    /// <summary>
    /// An override on a sub-cell instance that the cell neither declares nor reads is bound and then never looked at,
    /// so the circuit is built as if it were not there — and nothing said so. It is a warning, not a refusal: a name
    /// the cell does not declare is still visible to every expression below it (scope lookup walks up), so the test is
    /// whether any expression in the cell, or in a cell it instantiates, mentions the name. A cell whose text cannot be
    /// tokenized is not judged.
    /// </summary>
    private void WarnUnreadOverrides(Instance inst, Cell cell, string childPath, ElaboratedNetlist netlist)
    {
        if (inst.Overrides.Count == 0) return;
        HashSet<string>? read = null;
        foreach (var ov in inst.Overrides)
        {
            if (ov.Name.StartsWith("__", StringComparison.Ordinal)) continue;
            if (ov.Name == CellCorner.SelectorParameter) continue;   // consumed by CellCorner.Apply
            if (cell.Parameters.Any(p => p.Name == ov.Name) || cell.Variables.Any(v => v.Name == ov.Name)) continue;
            read ??= NamesReadIn(cell, []);
            if (read is null || read.Contains(ov.Name)) continue;
            netlist.AddWarningOnce($"unread-override:{childPath}.{ov.Name}",
                $"'{childPath}' sets {ov.Name}, which cell '{cell.Name}' does not declare and nothing in it reads; " +
                "the value has no effect.");
        }
    }

    private readonly Dictionary<Cell, HashSet<string>?> _namesReadIn = new(ReferenceEqualityComparer.Instance);

    /// <summary>Every identifier an expression in <paramref name="cell"/> or a cell below it mentions; null when one
    /// would not tokenize.</summary>
    private HashSet<string>? NamesReadIn(Cell cell, HashSet<string> visiting)
    {
        if (_namesReadIn.TryGetValue(cell, out var cached)) return cached;
        if (!visiting.Add(cell.Name)) return [];          // a cycle is the elaborator's own error to report

        HashSet<string>? names = [];
        try
        {
            var texts = cell.Parameters.Select(p => p.DefaultExpression)
                .Concat(cell.Variables.Select(v => v.Expression))
                .Concat(cell.Instances.SelectMany(i => i.Overrides.Select(o => o.Expression)));
            foreach (var text in texts)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                foreach (var tok in new Tokenizer(text).Tokenize())
                    if (tok.Kind == TokenKind.Identifier) names.Add(tok.Text);
            }
            foreach (var sub in cell.Instances.Select(i => i.Reference).Distinct(StringComparer.Ordinal))
            {
                if (ComponentModelFactory.IsPrimitive(sub) || FindCell(sub) is not { } child) continue;
                if (NamesReadIn(child, visiting) is not { } below) { names = null; break; }
                names.UnionWith(below);
            }
        }
        catch (Exception) { names = null; }

        visiting.Remove(cell.Name);
        _namesReadIn[cell] = names;
        return names;
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
        foreach (var declaredInstance in instances)
        {
            // R-fp2-6a: artwork-only overrides are dropped HERE, once, before anything reads them —
            // not in ResolveParameters, which is a dozen per-family methods, and not in any one of
            // them, which is how a parameter comes to leak on the thirteenth family. This is the
            // single point every instance at every level of the hierarchy passes through, so the
            // drop covers the primitive branch (ResolveParameters) and the sub-cell branch
            // (BuildCellScope's overrides) alike. See ArtworkParameters for why `Footprint` is not
            // a value and must never reach the evaluator.
            var inst = ArtworkParameters.WithoutArtworkOverrides(declaredInstance);

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

            // brief-sdl-6 D9: an instance that names an EM cell (EmCell=) is either simulated by EM at this point's
            // values — the provider's Touchstone, placed as an SnP — or, with Model=Circuit, an ordinary cell instance
            // with the reserved keys taken off so they reach no scope.
            if (!ComponentModelFactory.IsPrimitive(inst.Reference) && ParametricSwitch(inst, out bool byEm, out var plain))
            {
                if (byEm)
                {
                    ElaborateParametric(inst, plain, childPath, instancePathPrefix, parentNetMap, currentScope, globalScope, netlist);
                    continue;
                }
                inst = plain;
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

            // VProbe is a NAME, not a component. It is recorded here and resolved after the whole
            // walk (see _voltageProbes) — nothing is stamped, no node is assigned, and no model is
            // built, which is what makes placing one unable to move a single number in the answer.
            if (inst.Reference.Equals("VProbe", StringComparison.OrdinalIgnoreCase))
            {
                if (inst.NetBindings.Count == 0)
                    netlist.AddWarning(
                        $"VProbe '{childPath}' names no net and will not report any results.");
                else
                    _voltageProbes.Add((childPath, ResolveNet(inst.NetBindings[0])));
                continue;
            }

            // A composed FET elaborates AS A CELL (brief-modelcomposer-1 D3): its extrinsic network's
            // elements and one ComposedCore, flattened through the ordinary hierarchy path below, so
            // X1.rg is a component path and V(X1.gi) an intrinsic voltage like in any sub-cell.
            if (inst.Reference.Equals(ComponentModelFactory.ComposedFetTypeName, StringComparison.OrdinalIgnoreCase))
            {
                // Noise=no quiets the whole device: its core and every element of its extrinsic network.
                inst = TakeNoiseSwitch(inst, childPath, out bool composedNoiseOff, out _);
                var composed = ComposedFetCell(inst, childPath, currentScope, out var switches, out var loadedModel);
                NoteComposedSwitches(childPath, switches, loadedModel, netlist);
                var portMap  = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < composed.Ports.Count; i++)
                    portMap[composed.Ports[i]] = ResolveNet(inst.NetBindings[i]);
                var scope = new Scope(childPath, currentScope) { InstancePath = childPath };
                if (composedNoiseOff) _noiseOffDepth++;
                try { FlattenCell(composed, childPath, portMap, scope, globalScope, netlist); }
                finally { if (composedNoiseOff) _noiseOffDepth--; }
                continue;
            }

            if (inst.Reference.Equals(EmFieldDataRecord.TypeName, StringComparison.OrdinalIgnoreCase))
                inst = BindEmFieldData(inst, childPath);

            if (ComponentModelFactory.IsPrimitive(inst.Reference))
            {
                // Primitive — resolve nodes and parameters first; model creation may need params (e.g. SnP).
                // Noise= is the analysis's switch, not the model's parameter: taken off here, before any model sees it.
                inst = TakeNoiseSwitch(inst, childPath, out bool noiseOff, out bool noiseOn);
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

                ComponentModel? created;
                try { created = ComponentModelFactory.TryCreate(inst.Reference, resolvedParams, _functions, _ambientC); }
                catch (ParameterRefusalException ex) { throw new ParameterRefusalException($"'{childPath}': {ex.Message}"); }
                var model          = created
                                     ?? throw new InvalidOperationException(
                                         $"Failed to create model for primitive '{inst.Reference}' at '{childPath}'");

                // AUT-8 R-aut8-4. The net count is checked HERE — after the model exists, so a
                // parameterised part answers from its own resolved parameters, and BEFORE any of the
                // minting and family expansion below, which is the only point at which the array
                // still holds exactly what the instance line wrote.
                //
                // Every expansion below is guarded by an exact length ("resolvedNodes.Length == 3"),
                // so a short line does not fail there — it SKIPS the expansion and builds a
                // wired-wrong circuit that simulates to completion. That silence is what let a
                // one-net Tuner return status: ok with Pout at the floor sentinel at all 56 drive
                // points, and be written up as a defective component model.
                if (Netlist.InstanceNetContract.Expected(model) is { } expectedNets &&
                    inst.NetBindings.Count != expectedNets)
                    throw new InvalidOperationException(
                        Netlist.InstanceNetContract.Refusal(inst.Reference, childPath,
                                                    inst.NetBindings.Count, expectedNets));

                if (model is ToneSourceModelBase tsm)
                    foreach (var w in tsm.GetZeroHzToneWarnings(childPath))
                        netlist.AddWarningOnce($"zero-hz-tone:{childPath}", w);

                // Every System block but the amplifier DECLARES itself passive, and that is a claim
                // about numbers a user typed: the family builds its S from real, in-phase amplitudes,
                // so an insertion loss, a return loss and an isolation that are each plausible on
                // their own can still sum above unity. Asked HERE because elaboration is the one
                // point every engine agrees on — a block carrying a passive-intermod level is
                // Nonlinear, and the DC engine and the HB linear extractor both route it away from
                // the stamp where the frequency-dependent half of this check lives.
                if (model is IdealSBlockModel sys
                    && sys.PlacedPassivityWarning(childPath) is var (key, message))
                    netlist.AddWarningOnce(key, message);

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

                // MOD_Source: the same internal drive node (envelope/CW V-source ↔ source impedance).
                if (inst.Reference.Equals("MOD_Source", StringComparison.OrdinalIgnoreCase))
                {
                    int nDrv = netlist.Nodes.GetOrAssign($"__modsrc_{childPath}_drv");
                    resolvedNodes = [..resolvedNodes, nDrv];
                }

                // PnTone: same single internal drive node (multi-tone V-source ↔ Z_Port junction).
                if (inst.Reference.Equals("PnTone", StringComparison.OrdinalIgnoreCase))
                {
                    int nDrv = netlist.Nodes.GetOrAssign($"__pntone_{childPath}_drv");
                    resolvedNodes = [..resolvedNodes, nDrv];
                }

                // Match: one internal net per series arm past the first (match.md §8.3). Minted here,
                // keyed on the instance path, so two Matches carrying the SAME design still get
                // independent internal nets — the mechanism Tuner, P1Tone and Diode already use. The
                // __ prefix is reserved so a user net can never collide.
                if (model is MatchModel mm && mm.InternalNodeCount > 0)
                {
                    var extra = new int[mm.InternalNodeCount];
                    for (int k = 0; k < extra.Length; k++)
                        extra[k] = netlist.Nodes.GetOrAssign($"__match_{childPath}_{k}");
                    resolvedNodes = [..resolvedNodes, ..extra];
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
                if (model is Devices.Fet.FetModelBase { HasIntrinsicGateResistance: true } && resolvedNodes.Length == 3)
                {
                    // With Ri (brief NZ-4): the resistor (gate, gi), the intrinsic (gi, source), (drain, source) and Cgd's
                    // (gate, drain). gi is minted here and is a real unknown, as the diode's internal node is.
                    int g = resolvedNodes[0], d = resolvedNodes[1], src = resolvedNodes[2];
                    int gi = netlist.Nodes.GetOrAssign($"__fet_{childPath}_gi");
                    resolvedNodes = [g, gi, gi, src, d, src, g, d];
                }
                else if (model is Devices.Fet.FetModelBase && resolvedNodes.Length == 3)
                    resolvedNodes = [resolvedNodes[0], resolvedNodes[2],   // gate, source
                                     resolvedNodes[1], resolvedNodes[2]];  // drain, source

                // BJT family: the user draws three terminals (collector, base, emitter) but the
                // model is FOUR intrinsic ports plus one per parasitic resistance, because each
                // non-zero Rb/Re/Rc puts the junctions on an internal node of their own. Minted
                // here, keyed on the instance path, and given ordinary matrix rows for the same
                // reason the diode's Rs node is: collapsing them locally is exact at DC and wrong
                // in HB, where an internal node carries its own harmonic content.
                //
                // The parasitic ports follow the intrinsic four in the order collector, base,
                // emitter — BjtModel states the same order beside its own port indices, and the
                // two must be read together.
                if (model is BjtModel bjt && resolvedNodes.Length == 3)
                {
                    int nc = resolvedNodes[0], nb = resolvedNodes[1], ne = resolvedNodes[2];
                    int ci = bjt.HasCollectorResistance ? netlist.Nodes.GetOrAssign($"__bjt_{childPath}_ci") : nc;
                    int bi = bjt.HasBaseResistance      ? netlist.Nodes.GetOrAssign($"__bjt_{childPath}_bi") : nb;
                    int ei = bjt.HasEmitterResistance   ? netlist.Nodes.GetOrAssign($"__bjt_{childPath}_ei") : ne;

                    var pins = new List<int>(8 + 2 * bjt.InternalNodeCount)
                    {
                        bi, ei,   // emitter junction
                        bi, ci,   // collector junction, the Xcjc share
                        ci, ei,   // transport current
                        nb, ci,   // the (1 - Xcjc) share, across the base resistance
                    };
                    if (bjt.HasCollectorResistance) { pins.Add(nc); pins.Add(ci); }
                    if (bjt.HasBaseResistance)      { pins.Add(nb); pins.Add(bi); }
                    if (bjt.HasEmitterResistance)   { pins.Add(ne); pins.Add(ei); }
                    resolvedNodes = pins.ToArray();
                }

                // IGBT: the user draws three terminals (collector, gate, emitter) and the model
                // is five intrinsic ports plus one per non-zero ohmic resistance. It ALWAYS needs an
                // internal net — the base node between the insulated-gate channel and the bipolar is
                // what the model IS, and it is what makes the on-state voltage come out as the
                // channel's drop plus a junction's rather than as a fitted offset.
                //
                // The ohmic ports follow the intrinsic five in the order gate, collector, emitter;
                // IgbtModel states the same order beside its own port indices.
                if (model is Devices.Igbt.IgbtModel igbt && resolvedNodes.Length == 3)
                {
                    int nc = resolvedNodes[0], ng = resolvedNodes[1], ne = resolvedNodes[2];
                    int nb = netlist.Nodes.GetOrAssign($"__igbt_{childPath}_b");
                    int gi = igbt.HasGateResistance      ? netlist.Nodes.GetOrAssign($"__igbt_{childPath}_gi") : ng;
                    int ci = igbt.HasCollectorResistance ? netlist.Nodes.GetOrAssign($"__igbt_{childPath}_ci") : nc;
                    int ei = igbt.HasEmitterResistance   ? netlist.Nodes.GetOrAssign($"__igbt_{childPath}_ei") : ne;

                    var pins = new List<int>(10 + 2 * igbt.InternalNodeCount)
                    {
                        nb, ei,   // the channel, whose drain is the base node
                        ci, nb,   // the bipolar's base current, across its junction
                        gi, ei,   // Cge
                        gi, nb,   // the Miller capacitance
                        ci, ei,   // the bipolar's transport current
                    };
                    if (igbt.HasGateResistance)      { pins.Add(ng); pins.Add(gi); }
                    if (igbt.HasCollectorResistance) { pins.Add(nc); pins.Add(ci); }
                    if (igbt.HasEmitterResistance)   { pins.Add(ne); pins.Add(ei); }
                    resolvedNodes = pins.ToArray();
                }

                // Vertical power MOSFET: the user draws three terminals (drain, gate, source) and
                // the model is four intrinsic ports plus one per non-zero ohmic resistance. There
                // is no bulk net — the source-to-body short is inside the silicon, which is what
                // makes the body diode a source-to-drain element rather than a substrate junction.
                //
                // The ohmic ports follow the intrinsic four in the order gate, drain, source;
                // VdmosModel states the same order beside its own port indices.
                if (model is Devices.Mos.VdmosModel vd && resolvedNodes.Length == 3)
                {
                    int nd = resolvedNodes[0], ng = resolvedNodes[1], ns = resolvedNodes[2];
                    int gi = vd.HasGateResistance   ? netlist.Nodes.GetOrAssign($"__vdmos_{childPath}_gi") : ng;
                    int di = vd.HasDrainResistance  ? netlist.Nodes.GetOrAssign($"__vdmos_{childPath}_di") : nd;
                    int si = vd.HasSourceResistance ? netlist.Nodes.GetOrAssign($"__vdmos_{childPath}_si") : ns;

                    var pins = new List<int>(8 + 2 * vd.InternalNodeCount)
                    {
                        di, si,   // channel current and Rds
                        si, di,   // body diode: anode at the source for an n-channel device
                        gi, si,   // Cgs
                        gi, di,   // Cgd
                    };
                    if (vd.HasGateResistance)   { pins.Add(ng); pins.Add(gi); }
                    if (vd.HasDrainResistance)  { pins.Add(nd); pins.Add(di); }
                    if (vd.HasSourceResistance) { pins.Add(ns); pins.Add(si); }
                    resolvedNodes = pins.ToArray();
                }

                // JFET family: the user draws three terminals (drain, gate, source) and the model
                // is three intrinsic ports plus one per non-zero ohmic resistance. The gate is TWO
                // junctions — one to each end of the channel — which is why three nets expand to
                // six pins rather than being paired off as a two-port.
                //
                // The ohmic ports follow the intrinsic three in the order drain, source; JfetModel
                // states the same order beside its own port indices, and the two must be read
                // together.
                if (model is Devices.Jfet.JfetModel jfet && resolvedNodes.Length == 3)
                {
                    int nd = resolvedNodes[0], ng = resolvedNodes[1], ns = resolvedNodes[2];
                    int di = jfet.HasDrainResistance  ? netlist.Nodes.GetOrAssign($"__jfet_{childPath}_di") : nd;
                    int si = jfet.HasSourceResistance ? netlist.Nodes.GetOrAssign($"__jfet_{childPath}_si") : ns;

                    var pins = new List<int>(6 + 2 * jfet.InternalNodeCount)
                    {
                        di, si,   // channel current
                        ng, si,   // gate-source junction
                        ng, di,   // gate-drain junction
                    };
                    if (jfet.HasDrainResistance)  { pins.Add(nd); pins.Add(di); }
                    if (jfet.HasSourceResistance) { pins.Add(ns); pins.Add(si); }
                    resolvedNodes = pins.ToArray();
                }

                // MOS family: the user draws FOUR terminals (drain, gate, source, bulk) and the
                // model is six intrinsic ports plus one per non-zero ohmic resistance. Five of the
                // six intrinsic ports are branches inside the device rather than terminals, which is
                // why the four nets expand to twelve pins here rather than being paired off.
                //
                // The ohmic ports follow the intrinsic six in the order drain, source —
                // MosfetModelBase states the same order beside its own port indices, and the two
                // must be read together. Their internal nets are minted here, keyed on the instance
                // path, and get ordinary matrix rows for the same reason the BJT's and the diode's
                // do: collapsing them locally is exact at DC and wrong in HB, where an internal node
                // carries its own harmonic content.
                if (model is Devices.Mos.MosfetModelBase mos && resolvedNodes.Length == 4)
                {
                    int nd = resolvedNodes[0], ng = resolvedNodes[1],
                        ns = resolvedNodes[2], nb = resolvedNodes[3];
                    int di = mos.HasDrainResistance  ? netlist.Nodes.GetOrAssign($"__mos_{childPath}_di") : nd;
                    int si = mos.HasSourceResistance ? netlist.Nodes.GetOrAssign($"__mos_{childPath}_si") : ns;

                    var pins = new List<int>(12 + 2 * mos.InternalNodeCount)
                    {
                        di, si,   // channel current
                        nb, si,   // bulk-source junction
                        nb, di,   // bulk-drain junction
                        ng, si,   // gate-source charge
                        ng, di,   // gate-drain charge
                        ng, nb,   // gate-bulk charge
                    };
                    if (mos.HasDrainResistance)  { pins.Add(nd); pins.Add(di); }
                    if (mos.HasSourceResistance) { pins.Add(ns); pins.Add(si); }
                    resolvedNodes = pins.ToArray();
                }

                // ComposedCore: three nets (gi, di, si) onto two ports sharing the source, exactly as
                // the FET family — then one ground-referenced pair per internal state node, minted as
                // ordinary unknowns for the reason every internal node here is (HB sees its harmonics).
                // A self-heating core's fourth net is its thermal node, the last port, (t, 0) — a net of the cell, not
                // a minted one, because the cell's Rth ∥ Cth (and an instance's fourth pin) attach to it.
                if (model is ComposedCoreModel core)
                {
                    var pins = new List<int>(6 + 2 * core.InternalStateNodeCount)
                    {
                        resolvedNodes[0], resolvedNodes[2],   // gate, source
                        resolvedNodes[1], resolvedNodes[2],   // drain, source
                    };
                    for (int k = 0; k < core.InternalStateNodeCount; k++)
                    {
                        pins.Add(netlist.Nodes.GetOrAssign($"__composed_{childPath}_x{k}"));
                        pins.Add(0);
                    }
                    if (core.HasThermalNode) { pins.Add(resolvedNodes[3]); pins.Add(0); }
                    resolvedNodes = pins.ToArray();
                    int dot = childPath.LastIndexOf('.');
                    core.Label = dot > 0 ? childPath[..dot] : childPath;
                    WarnComposedTemperature(core, netlist);
                }


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
                         { ReferenceNode = refNode, Multiplicity = ResolveMultiplicity(resolvedParams, childPath), NoiseOff = noiseOff || _noiseOffDepth > 0,
                           NoiseOn = noiseOn };
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

                // A corner selection (brief vendorlib-13 R-vl13-2d): the corner's values become the cell's defaults
                // for this instance, so an explicit override still wins; the selector itself binds nothing.
                var (cornered, cornerOverrides) = CellCorner.Apply(subCell, inst.Overrides, childPath);
                ValidateCellOverrides(inst, cornered, childPath);
                var subScope = BuildCellScope(
                    cornered,
                    parentScope: currentScope,
                    overrides:   cornerOverrides,
                    scopeName:   childPath,
                    instanceName: inst.InstanceName);
                WarnUnreadOverrides(inst, subCell, childPath, netlist);

                FlattenCell(cornered, childPath, subPortMap, subScope, globalScope, netlist);
            }
        }
    }

    // ── Parametric (EM) instances — brief-sdl-6 ───────────────────────────────

    /// <summary>The testbench's global variable names, for the values a Path A instance is simulated at.</summary>
    private IReadOnlyList<string> _globalNames = [];

    private static readonly HashSet<string> _parametricKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ParametricModelRegistry.ModelParameter, ParametricModelRegistry.CellParameter, ParametricModelRegistry.SetupParameter,
    };

    /// <summary>
    /// Whether <paramref name="inst"/> names an EM cell — it carries <c>EmCell=</c>, which only the extraction of an
    /// EM-capable cell and a run's EM switch write — and whether it asks for EM (<c>Model=EM</c>). <paramref name="plain"/>
    /// is the instance without the reserved keys. A cell that merely declares a parameter called <c>Model</c> carries no
    /// <c>EmCell</c> and is never read here.
    /// </summary>
    private static bool ParametricSwitch(Instance inst, out bool byEm, out Instance plain)
    {
        byEm = false;
        plain = inst;
        if (!inst.Overrides.Any(o => o.Name.Equals(ParametricModelRegistry.CellParameter, StringComparison.OrdinalIgnoreCase)))
            return false;
        byEm = inst.Overrides.Any(o => o.Name.Equals(ParametricModelRegistry.ModelParameter, StringComparison.OrdinalIgnoreCase)
                                       && Unquote(o.Expression).Trim().Equals(ParametricModelRegistry.EmValue, StringComparison.OrdinalIgnoreCase));
        plain = new Instance(inst.InstanceName, inst.Reference, inst.NetBindings,
                             inst.Overrides.Where(o => !_parametricKeys.Contains(o.Name)).ToList())
        { RefNetBinding = inst.RefNetBinding };
        return true;
    }

    private static string? Reserved(Instance inst, string name) =>
        inst.Overrides.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } o
            ? Unquote(o.Expression).Trim() : null;

    /// <summary>
    /// D9: the instance is simulated by the provider's model at this point's values. For a placed cell (Path B) the values
    /// are its own scope — its parameters and variables, the instance's overrides applied exactly as for a circuit-model
    /// instance (D7); for the schematic simulated by EM of its own layout (Path A, a reference to no library cell) they
    /// are the global variables. The answer is placed as an SnP across the instance's nets, port k on net k.
    /// </summary>
    private void ElaborateParametric(Instance inst, Instance plain, string childPath, string instancePathPrefix,
                                     IReadOnlyDictionary<string, string>? parentNetMap, Scope currentScope, Scope globalScope,
                                     ElaboratedNetlist netlist)
    {
        var values = new Dictionary<string, Value>(StringComparer.Ordinal);
        void Take(string name, Scope scope)
        {
            try { values[name] = _evaluator.Eval(name, scope); }
            catch (Exception) { /* a value that does not resolve to a number is not one the geometry can take */ }
        }

        if (FindCell(inst.Reference) is { } cell)
        {
            var (cornered, cornerOverrides) = CellCorner.Apply(cell, plain.Overrides, childPath);
            ValidateCellOverrides(plain, cornered, childPath);
            var scope = BuildCellScope(cornered, currentScope, cornerOverrides, childPath, inst.InstanceName);
            foreach (var p in cornered.Parameters) Take(p.Name, scope);
            foreach (var v in cornered.Variables) Take(v.Name, scope);
        }
        else
            foreach (var name in _globalNames) Take(name, globalScope);

        var request = new ParametricModelRequest(
            Reserved(inst, ParametricModelRegistry.CellParameter) ?? "",
            Reserved(inst, ParametricModelRegistry.SetupParameter) is { Length: > 0 } setup ? setup : null,
            values, childPath, inst.NetBindings.Count, BaseDirectory);
        var answer = ParametricModelRegistry.Resolve(request);
        if (answer.Note is { Length: > 0 } note) netlist.AddNoteOnce($"em-model:{childPath}:{note}", note);
        if (answer.Deferred) return;
        if (answer.Failure is { } failure || answer.SnpPath is not { Length: > 0 } snp)
            throw new InvalidOperationException($"'{childPath}' cannot be simulated by EM: {answer.Failure ?? "no model was produced"}");

        // Placed through the ordinary primitive path, so an EM result is stamped, renormalised and extrapolated exactly
        // as any SnP the user placed.
        var asSnp = new Instance(inst.InstanceName, "SnP", inst.NetBindings,
            [new ParameterAssignment("NumPorts", inst.NetBindings.Count.ToString(CultureInfo.InvariantCulture)),
             new ParameterAssignment("File", "\"" + snp + "\"")]);
        FlattenInstances([asSnp], instancePathPrefix, parentNetMap, currentScope, globalScope, netlist);
    }

    /// <summary>
    /// brief-cosim-pattern-2 R-cp2-2 / R-cp2-5 — an EM Field Data line against its record. The port count is the record's, so the line's
    /// nets are counted here rather than where they are read: N nets, or N+1 with the trailing one a floating reference, SnP's rule. Every
    /// refusal is raised here, where the instance has its path, and it stops every analysis alike.
    /// </summary>
    private Instance BindEmFieldData(Instance inst, string childPath)
    {
        string who = EmFieldDataRecord.Describe(childPath);
        string file = inst.Overrides.FirstOrDefault(o => o.Name.Equals("File", StringComparison.OrdinalIgnoreCase))?.Expression.Trim() ?? "";
        if (file.Length >= 2 && file[0] == '"' && file[^1] == '"') file = file[1..^1];
        var (record, refusal) = EmFieldDataRecord.Open(ResolveSnpFilePath(file));
        if (record is null) throw new InvalidOperationException($"{who}: {refusal}");
        int nets = inst.NetBindings.Count, ports = record.PortCount;
        string name = Path.GetFileName(record.RecordPath);
        if (nets != ports && nets != ports + 1)
            throw new InvalidOperationException(
                $"{who} is wired to {nets} net(s) and '{name}' holds {ports} port(s) ({string.Join(", ", record.PortNames)}). " +
                "A record re-written with a different port count changes the block's pins: pick the record again and rewire it.");
        return nets == ports
            ? inst
            : new Instance(inst.InstanceName, inst.Reference, [.. inst.NetBindings.Take(ports)], inst.Overrides) { RefNetBinding = inst.NetBindings[ports] };
    }

    /// <summary>
    /// D7: an instance override of a cell is checked against what the cell declares before it is bound. A name that is
    /// both a parameter and a variable of the cell is ambiguous to override and is refused naming both; a name that is
    /// neither, and that nothing in the cell reads, would change nothing and is refused (<see cref="WarnUnreadOverrides"/>
    /// still warns about a name the cell reads without declaring).
    /// </summary>
    private void ValidateCellOverrides(Instance inst, Cell cell, string childPath)
    {
        HashSet<string>? read = null;
        foreach (var ov in inst.Overrides)
        {
            if (ov.Name.StartsWith("__", StringComparison.Ordinal) || ov.Name == CellCorner.SelectorParameter) continue;
            bool isParam = cell.Parameters.Any(p => p.Name == ov.Name);
            bool isVar   = cell.Variables.Any(v => v.Name == ov.Name);
            if (isParam && isVar)
                throw new InvalidOperationException(
                    $"'{childPath}' sets {ov.Name}, which cell '{cell.Name}' declares both as a parameter and as a variable; " +
                    $"rename one of them so the override says which it sets.");
            if (isParam || isVar) continue;
            read ??= NamesReadIn(cell, []);
            if (read is not null && !read.Contains(ov.Name))
                throw new InvalidOperationException(
                    $"'{childPath}' sets {ov.Name}, which cell '{cell.Name}' neither declares as a parameter or variable nor reads.");
        }
    }

    // ── The part's noise switch ───────────────────────────────────────────────

    /// <summary>The instance key that switches a part's noise off in the S-parameter noise analysis: <c>Noise=yes|no</c>,
    /// default yes. Accepted on every primitive; on one that can never be noisy it does nothing. On a source with an
    /// impedance (P1Tone, PnTone, MOD_Source), which is noiseless by default, <c>Noise=yes</c> makes that impedance thermal
    /// in an envelope run (brief NZ-7 follow-up).</summary>
    public const string NoiseParamName = "Noise";

    /// <summary>Above zero while a device stated <c>Noise=no</c> on is flattened as a cell (a ComposedFET): every element it
    /// elaborates to is switched off with it.</summary>
    private int _noiseOffDepth;

    /// <summary>
    /// <paramref name="inst"/> without its <see cref="NoiseParamName"/> override, and whether that override said no or yes. The
    /// value is a closed list — yes/true/on/1 or no/false/off/0, quoted or not, as a kit writes it — because a mistyped
    /// switch that silently left a part noisy, or quiet, is a wrong noise figure that looks right.
    /// </summary>
    internal static Instance TakeNoiseSwitch(Instance inst, string childPath, out bool noiseOff, out bool noiseOn)
    {
        noiseOff = false;
        noiseOn = false;
        int at = -1;
        for (int i = 0; i < inst.Overrides.Count; i++)
            if (inst.Overrides[i].Name.Equals(NoiseParamName, StringComparison.OrdinalIgnoreCase)) at = i;
        if (at < 0) return inst;

        string v = Unquote(inst.Overrides[at].Expression.Trim()).Trim();
        if (v.Equals("no", StringComparison.OrdinalIgnoreCase) || v.Equals("false", StringComparison.OrdinalIgnoreCase)
            || v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "0")
            noiseOff = true;
        else if (v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", StringComparison.OrdinalIgnoreCase) || v == "1")
            noiseOn = true;
        else
            throw new InvalidOperationException(NoiseSwitchDiagnostics.NotYesOrNo(childPath, v).Render());

        var rest = inst.Overrides.Where((_, i) => !inst.Overrides[i].Name.Equals(NoiseParamName, StringComparison.OrdinalIgnoreCase));
        return new Instance(inst.InstanceName, inst.Reference, inst.NetBindings, rest) { RefNetBinding = inst.RefNetBinding };
    }

    // ── Parameter resolution ──────────────────────────────────────────────────

    /// <summary>
    /// One instance parameter's override, evaluated in the parent scope — with the instance and parameter named
    /// as the site, so a distribution call in it draws on the stream <c>&lt;path&gt;.&lt;instance&gt;.&lt;param&gt;</c>.
    /// </summary>
    private Value EvalOverride(Instance inst, ParameterAssignment ov, Scope parentScope)
        => _evaluator.EvalParameter(ov.Expression, parentScope, ov.Unit, $"{inst.InstanceName}.{ov.Name}");

    private IReadOnlyDictionary<string, Value> ResolveParameters(
        Instance inst,
        Scope parentScope)
    {
        if (inst.Reference.Equals("SDD", StringComparison.OrdinalIgnoreCase))
            return ResolveSddParameters(inst, parentScope);
        if (inst.Reference.Equals("Z_Port", StringComparison.OrdinalIgnoreCase))
            return ResolveZPortParameters(inst, parentScope);
        if (inst.Reference.Equals("V_1Tone", StringComparison.OrdinalIgnoreCase) ||
            inst.Reference.Equals("V_nTone", StringComparison.OrdinalIgnoreCase) ||
            inst.Reference.Equals("I_1Tone", StringComparison.OrdinalIgnoreCase) ||
            inst.Reference.Equals("I_nTone", StringComparison.OrdinalIgnoreCase))
            return ResolveToneSourceParameters(inst, parentScope);
        if (inst.Reference.Equals("P1Tone", StringComparison.OrdinalIgnoreCase))
            return ResolveP1ToneParameters(inst, parentScope);
        if (inst.Reference.Equals("PnTone", StringComparison.OrdinalIgnoreCase))
            return ResolvePnToneParameters(inst, parentScope);
        if (inst.Reference.Equals("MOD_Source", StringComparison.OrdinalIgnoreCase))
            return ResolveModSourceParameters(inst, parentScope);
        if (inst.Reference.Equals("SnP", StringComparison.OrdinalIgnoreCase)
            || inst.Reference.Equals(EmFieldDataRecord.TypeName, StringComparison.OrdinalIgnoreCase)
            || inst.Reference.Equals(ComponentModelFactory.DeembedTypeName, StringComparison.OrdinalIgnoreCase))
            return ResolveSnpParameters(inst, parentScope);
        // A .cmodel path is not an expression, for SnP's reason.
        if (inst.Reference.Equals(ComponentModelFactory.ComposedCoreTypeName, StringComparison.OrdinalIgnoreCase))
            return ResolveComposedParameters(inst, parentScope);
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
        // Match's `Design` is base64 and its `Response` is an enum name — neither is an expression,
        // and the evaluator reads both as identifiers and fails. Same rule, same reason as wBond's.
        if (inst.Reference.Equals("Match", StringComparison.OrdinalIgnoreCase))
            return ResolveMatchParameters(inst, parentScope);

        // The Switch's OffState and the Circulator's Direction are enum NAMES, so they are kept out
        // of the expression evaluator — the same rule, for the same reason, as Match's Response
        // (see ResolveMatchParameters).
        if (inst.Reference.Equals("Switch", StringComparison.OrdinalIgnoreCase))
            return ResolveEnumNamedParameters(inst, parentScope, "OffState");
        if (inst.Reference.Equals("Circulator", StringComparison.OrdinalIgnoreCase))
            return ResolveEnumNamedParameters(inst, parentScope, "Direction");
        if (inst.Reference.Equals("Amp", StringComparison.OrdinalIgnoreCase))
            return ResolveEnumNamedParameters(inst, parentScope, "IP3Ref");
        // The OscProbe's Mode is series or shunt (brief NZ-9) — a word, as IP3Ref is.
        if (inst.Reference.Equals("OscProbe", StringComparison.OrdinalIgnoreCase))
            return ResolveEnumNamedParameters(inst, parentScope, "Mode");
        // The mixer's NFType (brief NZ-3) is ssb or dsb — a word, the same kind of value as IP3Ref.
        if (inst.Reference.Equals("Mixer", StringComparison.OrdinalIgnoreCase)
            && inst.Overrides.Any(o => o.Name.Equals("NFType", StringComparison.OrdinalIgnoreCase)))
            return ResolveEnumNamedParameters(inst, parentScope, "NFType");

        // The filter pair's Response and Form are the same kind of value, twice over on the duplexer
        // because it carries two complete filter specifications (brief-sys-6).
        if (inst.Reference.Equals("Filter", StringComparison.OrdinalIgnoreCase))
            return ResolveEnumNamedParameters(inst, parentScope, "Response", "Form");
        if (inst.Reference.Equals("Duplexer", StringComparison.OrdinalIgnoreCase))
            return ResolveEnumNamedParameters(inst, parentScope,
                                              "TxResponse", "TxForm", "RxResponse", "RxForm");

        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var ov in inst.Overrides)
        {
            // A test pad's ClearNames is the cleared conductors' names, for its notes (brief-dfr27-1): text, not an
            // expression, so it is kept verbatim as an enum name is.
            if (ov.Name == "ClearNames" && inst.Reference.Equals("TESTPAD", StringComparison.OrdinalIgnoreCase))
            {
                result[ov.Name] = new Value(Unquote(ov.Expression));
                continue;
            }

            // Frequency dependence has to TERMINATE at a model that binds `freq`. Anything else is
            // asking for a single number that does not exist, and saying so here — naming the
            // device, the parameter and the models that can take one — beats the bare
            // "Unresolved name 'freq'" the evaluator would otherwise report from inside the value.
            if (_freq.IsFreqDependent(ov.Expression, parentScope))
                throw new FrequencyDependentValueException(
                    $"'{inst.Reference}:{inst.InstanceName}' parameter '{ov.Name}' is frequency-dependent, " +
                    $"but a '{inst.Reference}' takes a single value that cannot vary with frequency. " +
                    "Only Chain (A/B/C/D), Z_Port (Z[i,j]) and SDD (H[w]) are evaluated per frequency.");

            // A Combiner's Phase[k] is an angle, and a bare number in it means degrees — the convention TLIN's E keeps —
            // so it is read with deg as its site unit and reaches the model in radians.
            result[ov.Name] = ov.Name.StartsWith("Phase[", StringComparison.Ordinal)
                              && inst.Reference.Equals("Combiner", StringComparison.OrdinalIgnoreCase)
                ? _evaluator.EvalParameter(ov.Expression, parentScope, ov.Unit ?? "deg", $"{inst.InstanceName}.{ov.Name}")
                : EvalOverride(inst, ov, parentScope);
        }

        ValidatePortPairNetCount(inst, result);
        return result;
    }

    // ── 2N-net port-pair components: the net-count refusal ────────────────────

    /// <summary>
    /// Refuses a 2N-net component whose netlist line does not carry 2N nets, NAMING the instance.
    ///
    /// <para><b>Why this is worth a check of its own.</b> Every one of these components forms its
    /// port voltages from <c>Nodes[2p]</c>/<c>Nodes[2p+1]</c>, so one net short is an
    /// index-out-of-range thrown from inside a stamp or a Newton iteration — at a point where
    /// nothing left on the stack can say which instance it was. The schematic tiles all emit the
    /// right count (the ground-referenced ones append their own <c>"0"</c> returns at extraction),
    /// so a wrong count only ever reaches here from a hand-written netlist, which is exactly the
    /// reader this sentence is for.</para>
    ///
    /// <para>ONE check for the whole family rather than a copy per component: the Mixer's was the
    /// first, and the ideal system blocks (brief-sys-2 onwards) are another nine of the same
    /// shape.</para>
    /// </summary>
    private static void ValidatePortPairNetCount(Instance inst, IReadOnlyDictionary<string, Value> resolved)
    {
        var expected = ExpectedPortPairNets(inst, resolved);
        if (expected is null || inst.NetBindings.Count == expected.Value.Nets) return;

        throw new InvalidOperationException(
            $"{inst.Reference} '{inst.InstanceName}': expected {expected.Value.Nets} nets " +
            $"({expected.Value.Names}); got {inst.NetBindings.Count}.");
    }

    /// <summary>
    /// The net count a 2N-net component requires, and the terminal names to print, or null when the
    /// reference is not one of them. The Switch's count depends on a PARAMETER, which is why this
    /// runs after the overrides are resolved rather than before.
    /// </summary>
    private static (int Nets, string Names)? ExpectedPortPairNets(
        Instance inst, IReadOnlyDictionary<string, Value> resolved)
    {
        if (inst.Reference.Equals("Mixer", StringComparison.OrdinalIgnoreCase))
            return (6, "rf+, rf−, lo+, lo−, if+, if−");

        if (inst.Reference.Equals("Atten", StringComparison.OrdinalIgnoreCase)
            || inst.Reference.Equals("Delay", StringComparison.OrdinalIgnoreCase))
            return (4, "1+, 1−, 2+, 2−");

        if (inst.Reference.Equals("Circulator", StringComparison.OrdinalIgnoreCase))
            return (6, "1+, 1−, 2+, 2−, 3+, 3−");

        if (inst.Reference.Equals("Balun", StringComparison.OrdinalIgnoreCase))
            return (6, "unb+, unb−, bal++, bal+−, bal−+, bal−−");

        // One reference for three tiles — the directional coupler and both hybrids — so the count
        // is the same eight for all of them.
        if (inst.Reference.Equals("Coupler", StringComparison.OrdinalIgnoreCase))
            return (8, "in+, in−, thru+, thru−, cpl+, cpl−, iso+, iso−");

        // Unilateral, so the two ports are named rather than numbered — a netlist line with them
        // the wrong way round is a 20 dB pad, and the names are the only warning of that.
        if (inst.Reference.Equals("Amp", StringComparison.OrdinalIgnoreCase))
            return (4, "in+, in−, out+, out−");

        if (inst.Reference.Equals("Filter", StringComparison.OrdinalIgnoreCase))
            return (4, "1+, 1−, 2+, 2−");

        // Named rather than numbered: swapping TX and RX on a hand-written line is a duplexer with
        // its band plan inverted, which simulates perfectly and answers a different question.
        if (inst.Reference.Equals("Duplexer", StringComparison.OrdinalIgnoreCase))
            return (6, "ant+, ant−, tx+, tx−, rx+, rx−");

        // The common port, then the N paths; N is a parameter, as the Switch's throw count is.
        if (inst.Reference.Equals("Combiner", StringComparison.OrdinalIgnoreCase))
        {
            int ways = 2;
            if (resolved.TryGetValue("N", out var w) && w.Kind == ValueKind.Real)
                ways = Math.Max(2, (int)Math.Round(w.AsReal()));
            return (2 * (1 + ways), "S+, S−, " + string.Join(", ", Enumerable.Range(1, ways).Select(k => $"{k}+, {k}−")));
        }

        if (inst.Reference.Equals("Switch", StringComparison.OrdinalIgnoreCase))
        {
            int throws = 1;
            if (resolved.TryGetValue("Throws", out var t) && t.Kind == ValueKind.Real)
                throws = Math.Max(1, (int)Math.Round(t.AsReal()));

            // An SPST's two pins are interchangeable and unnamed on the glyph; anything with more
            // than one throw has a common port that is not.
            string names = throws == 1
                ? "1+, 1−, 2+, 2−"
                : "com+, com−, " + string.Join(", ",
                    Enumerable.Range(1, throws).Select(k => $"{k}+, {k}−"));
            return (2 * (1 + throws), names);
        }

        return null;
    }

    // ── Enum-named parameters on the ideal system blocks ──────────────────────

    /// <summary>
    /// Resolves a component whose parameters are plain reals apart from the NAMED few, which carry
    /// an enum NAME — the Switch's <c>OffState</c>, the Circulator's <c>Direction</c>, the
    /// amplifier's <c>IP3Ref</c>. An enum name
    /// is a bare identifier the evaluator would either fail on or, worse, resolve against a global
    /// that happens to share its spelling, so it is stored verbatim, exactly as Match's
    /// <c>Response</c> is.
    ///
    /// <para>The Switch's <c>State</c> deliberately stays an ordinary evaluated NUMBER: it is what
    /// makes a parametric sweep over the switch position work. Only a value with no numeric reading
    /// at all belongs on this list.</para>
    /// </summary>
    private IReadOnlyDictionary<string, Value> ResolveEnumNamedParameters(
        Instance inst, Scope parentScope, params string[] enumNamed)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var ov in inst.Overrides)
        {
            if (enumNamed.Any(n => ov.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                result[ov.Name] = new Value(Unquote(ov.Expression));
            else
                result[ov.Name] = EvalOverride(inst, ov, parentScope);
        }

        ValidatePortPairNetCount(inst, result);
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
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
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

        // HOW MANY OF THE TYPE'S TERMINALS THIS INSTANCE CONNECTS. Usually all of them, and then this
        // is the old rule unchanged. A component that STATED a smaller number — a five-terminal model
        // placed as a four-pin part, which is the ordinary way to say "I do not want a thermal pin on
        // my schematic" — connects the leading terminals and leaves the rest open. The model was told
        // the same number when it was created, so it has already decided what to do about them:
        // typically grounding its own thermal node, which arrives here as a collapse.
        //
        // The count still has to MATCH the nets exactly, whichever way it was arrived at. A net count
        // quietly differing from a terminal count is the one outcome nothing downstream can notice.
        int connected = model.ConnectedPinCount ?? d.ExternalPinCount;
        if (connected > d.ExternalPinCount)
            throw new ExternalDeviceException(
                $"ExtDevice '{childPath}' (type '{d.TypeId}') declares {d.ExternalPinCount} " +
                $"external pins, but this component states {connected}. A model cannot be placed " +
                $"with more pins than it has.");
        if (declaredNets.Length != connected)
            throw new ExternalDeviceException(
                connected == d.ExternalPinCount
                ? $"ExtDevice '{childPath}' (type '{d.TypeId}') declares {d.ExternalPinCount} " +
                  $"external pins but {declaredNets.Length} nets were given."
                : $"ExtDevice '{childPath}' (type '{d.TypeId}') is placed with {connected} of the " +
                  $"type's {d.ExternalPinCount} pins, but {declaredNets.Length} nets were given.");

        var nodeIndex = new int[d.NodeCount];
        for (int k = 0; k < connected; k++) nodeIndex[k] = declaredNets[k];


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
            // Only a pin the user actually WIRED is a contradiction. A terminal beyond the stated
            // connection count has no net on it, and a model grounding one is precisely the right
            // answer to having been told it is not connected — it is what stops that node arriving as
            // an all-zero row.
            if (node.Index < connected)
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): external pin {node.Index} is reported " +
                    $"as collapsed to ground, but a pin the user wires cannot be grounded from inside " +
                    $"the device. The provider should not offer it as a pin under these parameters.");
        }

        // A node the model writes NO equation for is not an independent unknown, and which node it
        // follows has to be established before anything is stamped.
        //
        // WHAT GOES WRONG OTHERWISE. Such a node is still read by the model's other equations, so it
        // is not inert and cannot be pinned to anything safely. Left as a free unknown it is an
        // almost-empty matrix row that nothing holds: the bias ramp wanders to its iteration budget
        // and the residual it finally reports names a supply branch rather than the node responsible.
        // Measured on a real compiled model: 30,675 Newton iterations and 32 seconds of wall clock,
        // against 4 iterations once the node had a master.
        //
        // AND IT CANNOT BE GUESSED. On that same model every candidate converged and the drain
        // current differed by 130x between them — a wrong choice is not a failure anyone would
        // notice. So it is MEASURED (see DeriveMasters), and when the measurement does not separate
        // the candidates the run stops rather than picking one.
        var unresolved = UnwrittenNodes(model);

        if (unresolved.Count > 0)
        {
            var derived = DeriveMasters(model, unresolved);

            if (derived is null)
            {
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): the model writes no equation for " +
                    $"{(unresolved.Count == 1 ? "node" : "nodes")} {string.Join(", ", unresolved)}, " +
                    $"so {(unresolved.Count == 1 ? "it is" : "they are")} not " +
                    $"{(unresolved.Count == 1 ? "an independent unknown" : "independent unknowns")}, " +
                    $"and circuitRF could not work out which node " +
                    $"{(unresolved.Count == 1 ? "it follows" : "each follows")}: the model's own " +
                    $"derivatives agree equally well with every candidate. Running anyway would spend " +
                    $"the whole iteration budget and report a residual against a supply rather than " +
                    $"against this. Name the missing " +
                    $"{(unresolved.Count == 1 ? "master" : "masters")} in an " +
                    $"'{DeviceLibraryDiscovery.AliasMapFileName}' beside the kit and re-import it. " +
                    $"circuitRF does not pick one on a coin toss: the choice changes the answer, and " +
                    $"every wrong choice still converges.");
            }

            // Recorded on the descriptor rather than kept here, so node allocation below and every
            // later reader see one account of what these nodes are.
            var nodes = d.Nodes
                .Select(n => derived.Value.Masters.TryGetValue(n.Index, out int mst)
                    ? n with { SlavedTo = mst }
                    : n)
                .ToList();

            model.ResolveNodes(d with { Nodes = nodes });
            d = model.Descriptor;

            // A NOTE, not a warning: nothing here is wrong. circuitRF established something the
            // design could not state and is saying what it established.
            netlist.AddNoteOnce(
                $"unwritten-nodes-resolved:{d.TypeId}",
                $"Type '{d.TypeId}': the model writes no equation for " +
                $"{(unresolved.Count == 1 ? "node" : "nodes")} {string.Join(", ", unresolved)}, so " +
                $"{(unresolved.Count == 1 ? "it is" : "they are")} not " +
                $"{(unresolved.Count == 1 ? "an unknown" : "unknowns")} of their own. circuitRF " +
                $"measured which node each follows from the model's own derivatives — " +
                $"{derived.Value.Evidence} — and is solving it that way. Left as free unknowns these " +
                $"nodes have no operating point at all.");
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
        // CHAINS ARE ORDINARY. A model reports its collapses pairwise, and nothing obliges it to
        // name the same end of the group each time: "A follows B" and "B follows C" in one
        // descriptor is three nodes and one unknown, not an error. A published GaN HEMT model does
        // exactly this — its drain TERMINAL follows an internal node that itself follows another —
        // and refusing the shape stopped the model elaborating at all.
        //
        // Reading only the first link is what makes a chain look impossible: it would give A the
        // index of B, which is itself not the index the group settles on. So each slaved node is
        // resolved to the END of its chain, and the whole chain becomes one group. The
        // external-pin-wins rule below is then unchanged — it just sees the complete membership.
        var slavedTo = new Dictionary<int, int>();
        foreach (var node in d.Nodes)
            if (node.SlavedTo is int m) slavedTo[node.Index] = m;

        int RootOf(int start)
        {
            int cur = start;
            // Bounded by the node count: a chain can visit each node at most once, so one extra
            // step means it has re-entered a node it already left. That is a cycle, and there is no
            // representative to settle on — every member is waiting for another member.
            for (int step = 0; step <= d.NodeCount; step++)
            {
                if (!slavedTo.TryGetValue(cur, out int next)) return cur;
                if (next < 0 || next >= d.NodeCount || next == cur)
                    throw new ExternalDeviceException(
                        $"ExtDevice '{childPath}' (type '{d.TypeId}'): node {cur} is slaved to " +
                        $"node {next}, which is not a valid other node of this device.");
                cur = next;
            }
            throw new ExternalDeviceException(
                $"ExtDevice '{childPath}' (type '{d.TypeId}'): the collapse chain from node {start} " +
                "never reaches a node of its own — the model reports a cycle, each node in it " +
                "following the next. One of them has to be the node the others collapse onto.");
        }

        var groupOf = new Dictionary<int, List<int>>();
        foreach (var node in d.Nodes)
        {
            if (node.SlavedTo is not int master) continue;
            if (master < 0 || master >= d.NodeCount || master == node.Index)
                throw new ExternalDeviceException(
                    $"ExtDevice '{childPath}' (type '{d.TypeId}'): node {node.Index} is slaved to " +
                    $"node {master}, which is not a valid other node of this device.");

            int root = RootOf(master);
            if (!groupOf.TryGetValue(root, out var members))
                groupOf[root] = members = [root];
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
            // A terminal the user did not wire carries no net, so it cannot be the member whose index
            // the group settles on — it has nothing to contribute and would drag the group onto a
            // node the design never named.
            var externals = members.Where(m => m < connected).ToList();

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

        // Now mint, for nodes that genuinely still need an unknown of their own — the internal ones,
        // and any TERMINAL beyond the stated connection count, which has no net of the user's and is
        // otherwise indistinguishable from an internal node. An unconnected thermal terminal minted
        // here is what PinUnreferencedThermalNodes then finds and holds at the ambient.
        for (int k = connected; k < d.NodeCount; k++)
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

        bool verilogA = inst.Reference.Equals("VerilogA", StringComparison.OrdinalIgnoreCase);
        // A VerilogA's artwork names (brief-vendorlib-7) are taken verbatim too: a .csym path is not an expression.
        if (verilogA) selectors.UnionWith(Devices.ComponentModelFactory.VerilogAArtworkKeys(names));
        foreach (var ov in inst.Overrides)
        {
            if (selectors.Contains(ov.Name))
            {
                var selector = ResolveExtDeviceSelector(ov.Expression, parentScope);
                // A VerilogA's model file resolves against the workspace root, as a ComposedFET's .cmodel does, so a
                // workspace that ships a .va beside its .cmodel names both the same way (brief-modelcomposer-28).
                if (verilogA && ov.Name.Equals(Devices.ComponentModelFactory.VerilogAFileParam, StringComparison.OrdinalIgnoreCase))
                    selector = new Value(ResolveSnpFilePath(selector.AsString()));
                result[ov.Name] = selector;
                continue;
            }

            try
            {
                result[ov.Name] = EvalOverride(inst, ov, parentScope);
            }
            catch
            {
                // Not an expression — a path, an enum name, or anything else the provider owns.
                result[ov.Name] = new Value(ov.Expression.Trim().Trim('"'));
            }
        }
        return result;
    }

    /// <summary>A name and nothing else — the only form a reference can take here.</summary>
    private static readonly Regex RxBareName = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// One selector's value: <c>Provider</c>, <c>Type</c>, <c>File</c> or <c>Model</c>.
    ///
    /// <para><b>These are NOT expression-evaluated, and that is deliberate</b> — Provider and Type
    /// name things, File is a path and Model is a name inside it. A leading <c>/</c> alone stops the
    /// expression parser at position 0, and falling back to verbatim only when evaluation throws is
    /// not enough: a path that happens to parse as arithmetic would be silently turned into a
    /// number.</para>
    ///
    /// <para><b>But verbatim must not swallow a REFERENCE, which is what it used to do.</b> A kit's
    /// device cell declares its data file as a cell parameter and forwards it into the device by name
    /// — <c>File=File</c> — which is the ordinary way a netlist passes a value down, and the only way
    /// one part can be instantiated at several file-backed sizes. Taken verbatim the device is handed
    /// the literal four characters <c>File</c>; owner-reported, as every operating point failing with
    /// the worker's own log reading <c>File=File (NOT READABLE HERE)</c>.</para>
    ///
    /// <para><b>What separates the two is the QUOTING, which the netlist already states.</b> A
    /// literal is quoted and is never looked up — not even when something in scope is spelled the
    /// same. A bare name is resolved only when the scope actually binds it. Everything else — an
    /// unquoted path, an enum value, a name nothing binds — is left exactly as it was, so nothing
    /// that worked before behaves differently.</para>
    /// </summary>
    private Value ResolveExtDeviceSelector(string expression, Scope scope)
    {
        string text = expression.Trim();

        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
            return new Value(text[1..^1]);

        if (RxBareName.IsMatch(text) && scope.Lookup(text) is not null)
        {
            try
            {
                var value = _evaluator.Eval(text, scope, null);
                // Handed on as TEXT whatever kind it resolved to: a selector names something, and
                // ComponentModelFactory requires Provider and Type to be strings.
                return value.Kind switch
                {
                    ValueKind.String => value,
                    ValueKind.Real   => new Value(value.AsReal().ToString(
                                            "R", System.Globalization.CultureInfo.InvariantCulture)),
                    _                => new Value(value.ToString()),
                };
            }
            catch
            {
                // A binding this layer cannot evaluate is not a reference it can use — fall through
                // to verbatim, which is exactly what happened before.
            }
        }

        return new Value(text.Trim('"'));
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
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
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

    // ── V_1Tone / V_nTone / I_1Tone / I_nTone parameter resolution ────────────
    // One resolver for both flavours: the amplitude/offset keys differ ("V"/"Vdc" against
    // "I"/"Idc") and nothing else does, so both spellings are listed rather than the resolver
    // being duplicated. A key that belongs to the other flavour simply never appears.

    private static readonly Regex RxToneIndexed = new(@"^(V|I|Freq|Phase)\[(\d+)\]$",
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
            if (StoreRawPhaseNoise(ov, result)) continue;
            bool isExprParam = ov.Name is "V" or "Vdc" or "I" or "Idc" or "Phase"
                || RxToneIndexed.IsMatch(ov.Name);

            if (isExprParam)
            {
                // Try to resolve as a number; if it fails (it's a variable ref), store as string.
                try
                {
                    var val = EvalOverride(inst, ov, parentScope);
                    result[ov.Name] = val;
                    // Also store as string so the model can re-evaluate on sweep updates.
                    // Detect if expression was a non-literal by trying to parse and check for refs.
                    var ast = Parser.Parse(ov.Expression);
                    var refs = AstWalker.CollectRefs(ast);
                    if (refs.Count > 0)
                    {
                        // Has variable references → also store the raw expression, AND the unit
                        // multiplier that was just applied to it. The stored text carries no unit,
                        // so a model re-evaluating it at a sweep point would otherwise land on a
                        // different number than this first resolution did — `Phase=phi deg` would
                        // come back in degrees after starting life in radians. 1.0 under the
                        // var-unit-wins rule, where the referenced variable brought its own unit and
                        // the site unit was never applied (Evaluator.Eval).
                        // Under var-unit-wins the bare additive operands took the site unit instead
                        // (`V=vd + 100 mV`'s 100); the stored text carries that as unit literals, so the
                        // model's re-evaluation — in plain numbers — lands where Eval did.
                        result[$"_expr_{ov.Name}"] = new Value(
                            !string.IsNullOrEmpty(ov.Unit) && Evaluator.IsUnitBearing(ast, parentScope)
                                ? FreqDeferral.Render(Evaluator.ScaleBareOperands(ast, parentScope, ov.Unit))
                                : ov.Expression);
                        result[$"_scale_{ov.Name}"] = new Value(ToneParamUnitScale(ov, parentScope));
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
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
                catch { /* skip */ }
            }
        }

        return result;
    }

    /// <summary>
    /// The multiplier <see cref="Evaluator.Eval(string, Scope, string?)"/> applied to a tone-source
    /// parameter's value for its declared unit — π/180 for <c>deg</c>, 1e-3 for <c>mV</c>, and 1.0
    /// both when there is no unit and under the var-unit-wins rule, where a referenced variable
    /// carried its own unit and the site unit was deliberately not applied.
    /// </summary>
    private static double ToneParamUnitScale(ParameterAssignment ov, Scope scope)
    {
        if (string.IsNullOrEmpty(ov.Unit)) return 1.0;
        if (Evaluator.IsUnitBearing(ov.Expression, scope)) return 1.0;
        return Units.Scale(ov.Unit) ?? 1.0;
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
            { "File", "InterpMode", "InterpDomain", "ExtrapMode", "PinConfig", "RefNode", RfCore.SnpPerturbation.ReciprocalParameter };

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
            else if (ov.Name.StartsWith(RfCore.SnpPerturbation.DegPrefix, StringComparison.Ordinal))
            {
                // brief-sens-2: a phase offset is in DEGREES. `deg` is a scaled unit (to radians), so the value is read
                // with deg as its site unit — a bare 10 and `10 deg` are both ten degrees — and handed on in degrees.
                try
                {
                    var v = _evaluator.EvalParameter(ov.Expression, parentScope, ov.Unit ?? "deg", $"{inst.InstanceName}.{ov.Name}");
                    result[ov.Name] = v.Kind == ValueKind.Real ? new Value(v.AsReal() * 180.0 / Math.PI) : v;
                }
                catch { /* skip unresolvable, as below */ }
            }
            else
            {
                // NumPorts and any other numeric override — evaluate normally.
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
                catch { /* skip unresolvable; factory will error if a required numeric is missing */ }
            }
        }
        return result;
    }

    // Resolves a relative SnP File path against BaseDirectory (the workspace root); absolute paths and
    // the no-root case pass through unchanged. Cross-platform: Path.* honor the host separator rules,
    // and we tolerate a Windows-authored '\' in a relative path so a netlist ports across OSes.
    private string ResolveSnpFilePath(string file) => ResolveFileReference(file, BaseDirectory);

    /// <summary>
    /// How a file parameter (an SnP's or a ComposedFET's <c>File</c>) resolves against the directory a netlist is
    /// elaborated from — the ONE rule, so <c>explain --ref</c> can answer exactly as elaboration does
    /// (brief-modelcomposer-13 R-mc13-5).
    /// </summary>
    public static string ResolveFileReference(string file, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(file))       return file;
        if (Path.IsPathRooted(file))               return file;   // absolute on this OS → unchanged
        if (string.IsNullOrEmpty(baseDirectory))   return file;   // no workspace root → legacy behavior
        var rel = file.Replace('\\', '/');                        // tolerate Windows-authored separators
        return Path.GetFullPath(Path.Combine(baseDirectory, rel));
    }

    // ── Composed FET (brief-modelcomposer-1) ──────────────────────────────────

    /// <summary>The cell each distinct (<c>.cmodel</c>, <c>N</c>, <c>m</c>) elaborates as, built once
    /// per elaboration. Never added to a library, the palette or a file.</summary>
    private readonly Dictionary<(string Path, long Stamp, int N, double M, bool ThermalPin, double DeviceC, ComposedSwitches Switches,
                                 double Rth, double Cth), Cell> _composedCells = [];

    /// <summary>What a ComposedFET line may carry: its model, size and multiplier, and the instance switches
    /// (brief-modelcomposer-21). <c>Symbol</c> is the schematic's alone and is read by nothing here.</summary>
    private static readonly string[] ComposedParams =
        ["File", "N", MultiplierParamName, "M", "Symbol", RthParam, CthParam, TempParam, .. ComposedSwitches.Names];

    /// <summary>A placed device's own thermal resistance (°C/W) and capacitance (J/°C), at its finger count, in place of the
    /// model's scaled ones. Blank is the model's.</summary>
    public const string RthParam = "Rth", CthParam = "Cth";

    /// <summary>
    /// A placed device's own temperature, °C: the ambient it is held at — a baseplate, a heatsink — in place of the design's
    /// <c>temp</c>. Blank follows <c>temp</c>. A self-heating model's channel rises above it. There is deliberately no
    /// <c>Dtemp</c> here: the temperature a designer knows about one transistor is the one it is mounted at.
    /// </summary>
    public const string TempParam = "Temp";

    /// <summary>
    /// The in-memory cell one <c>ComposedFET</c> instance elaborates as (R-mc1-5): its <c>.cmodel</c>
    /// loaded through the cache, its network scaled to <c>N</c> fingers (R-mc1-7), and the
    /// <c>ComposedCore</c> on <c>gi di si</c>. Every refusal names the instance.
    /// </summary>
    private Cell ComposedFetCell(Instance inst, string childPath, Scope parentScope,
                                 out ComposedSwitches switches, out LoadedComposedModel loaded)
    {
        string who = $"ComposedFET '{childPath}'";

        if (inst.NetBindings.Count is not (3 or 4))
            throw new InvalidOperationException(
                $"{who}: expected 3 nets (g d s), or 4 with the thermal node t; got {inst.NetBindings.Count}.");

        foreach (var ov in inst.Overrides)
            if (!ComposedParams.Contains(ov.Name, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"{who}: '{ov.Name}' is not a parameter of a ComposedFET; it takes File, N, m, Rth, Cth, Temp, " +
                    string.Join(", ", ComposedSwitches.Names.SkipLast(1)) + " and " + ComposedSwitches.Names[^1] + ".");

        try { switches = ComposedSwitches.Read(inst.Overrides, ov => EvalOverride(inst, ov, parentScope)); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"{who}: {ex.Message}"); }

        var fileOv = inst.Overrides.FirstOrDefault(o => o.Name == "File");
        string file = fileOv is null ? "" : Unquote(fileOv.Expression.Trim());
        if (string.IsNullOrWhiteSpace(file))
            throw new FileNotFoundException($"{who}: no model file is specified; set 'File' to a .cmodel file.");
        file = ResolveSnpFilePath(file);
        if (Directory.Exists(file))
            throw new FileNotFoundException($"{who}: its 'File' parameter names a folder, not a .cmodel file: '{file}'.", file);
        if (!File.Exists(file))
            throw new FileNotFoundException($"{who}: model file not found: '{file}'.", file);

        try { loaded = ComposedModelCache.Load(file); }
        catch (CmodelFormatException ex)
        {
            throw new InvalidOperationException($"{who}: '{file}' cannot be read — {ex.Message}");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"{who}: '{file}' cannot be read — {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException($"{who}: '{file}' cannot be read — {ex.Message}");
        }

        // The fourth net is the thermal node (MC-9 R-mc9-2), so it exists only on a model that self-heats.
        bool thermalPin = inst.NetBindings.Count == 4;
        if (thermalPin && loaded.Model.Thermal is null)
            throw new InvalidOperationException(
                $"{who}: a fourth net is the thermal node t, and '{Path.GetFileName(file)}' does not self-heat; " +
                "bind three nets, g d s.");

        int n = loaded.N0;
        if (inst.Overrides.FirstOrDefault(o => o.Name == "N") is { } nOv)
        {
            try { n = ComponentModelFactory.ComposedFingerCount(EvalOverride(inst, nOv, parentScope)); }
            catch (ParameterRefusalException ex) { throw new ParameterRefusalException($"'{childPath}': {ex.Message}"); }
        }

        double m = 1.0;
        foreach (string mName in (string[])[MultiplierParamName, "M"])
            if (inst.Overrides.FirstOrDefault(o => o.Name == mName) is { } mOv)
                m = ResolveMultiplicity(
                    new Dictionary<string, Value> { [MultiplierParamName] = EvalOverride(inst, mOv, parentScope) },
                    childPath);

        double rth = ThermalOverride(RthParam, "°C/W"), cth = ThermalOverride(CthParam, "J/°C");
        if ((double.IsFinite(rth) || double.IsFinite(cth)) && loaded.Model.Thermal is null)
            throw new InvalidOperationException(
                $"{who}: Rth and Cth set a self-heating model's thermal network, and '{Path.GetFileName(file)}' does not " +
                "self-heat; leave them blank.");

        double ThermalOverride(string name, string unit)
        {
            if (inst.Overrides.FirstOrDefault(o => o.Name == name) is not { } ov || string.IsNullOrWhiteSpace(ov.Expression))
                return double.NaN;
            var v = EvalOverride(inst, ov, parentScope);
            double x = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
            if (!double.IsFinite(x) || x <= 0)
                throw new ParameterRefusalException(
                    $"'{childPath}': {name}={v} is not a thermal {(name == RthParam ? "resistance" : "capacitance")}; " +
                    $"give a positive number in {unit}, or leave it blank for the model's own.");
            return x;
        }

        double deviceC = _ambientC;
        if (inst.Overrides.FirstOrDefault(o => o.Name == TempParam) is { } tOv && !string.IsNullOrWhiteSpace(tOv.Expression))
        {
            var v = EvalOverride(inst, tOv, parentScope);
            deviceC = v.Kind == ValueKind.Real ? v.AsReal() : double.NaN;
            if (!double.IsFinite(deviceC) || deviceC <= -Temperature.KelvinOffset)
                throw new ParameterRefusalException(
                    $"'{childPath}': {TempParam}={v} is not a temperature; give the device's temperature in °C, " +
                    "or leave it blank to follow the design's temp.");
        }

        long stamp = File.GetLastWriteTimeUtc(file).Ticks;
        var key = (file, stamp, n, m, thermalPin, deviceC, switches, rth, cth);
        if (_composedCells.TryGetValue(key, out var cached)) return cached;

        var coreParams = new List<ParameterAssignment>
        {
            new("File", $"\"{file}\""),
            new("N", n.ToString(CultureInfo.InvariantCulture)),
        };
        if (deviceC != _ambientC) coreParams.Add(new(TempParam, deviceC.ToString("R", CultureInfo.InvariantCulture)));
        if (m != 1.0) coreParams.Add(new(MultiplierParamName, ComposedNetworkTemplate.Literal(m)));
        // The cell's R and C stamp the thermal network; the core is told an instance's own values only to report its pole.
        if (double.IsFinite(rth)) coreParams.Add(new(RthParam, ComposedNetworkTemplate.Literal(rth)));
        if (double.IsFinite(cth)) coreParams.Add(new(CthParam, ComposedNetworkTemplate.Literal(cth)));
        // The core carries the switches, so each core instance is built with them.
        foreach (var (name, value) in switches.Write()) coreParams.Add(new(name, value));
        var thermal = loaded.Model.Thermal;
        IEnumerable<string> coreNets = thermal is null
            ? ComposedNetworkTemplate.IntrinsicNets
            : [.. ComposedNetworkTemplate.IntrinsicNets, ComposedNetworkTemplate.ThermalNet];
        var core = new Instance(ComposedNetworkTemplate.CoreInstanceName,
            ComponentModelFactory.ComposedCoreTypeName, coreNets, coreParams);

        // A thermal resistance that varies with the ambient is taken at the ambient the core is evaluated at.
        var t = loaded.Model.Temperature;
        double ambient = t.Mode == TemperatureMode.Range ? Math.Clamp(deviceC, t.Tmin, t.Tmax) : deviceC;
        var cell = loaded.Network.Synthesize(
            $"ComposedFET({Path.GetFileName(file)},N={n})", n, loaded.N0, m, core, thermal, ambient, thermalPin, rth, cth);
        _composedCells[key] = cell;
        return cell;
    }

    /// <summary>
    /// What a switched instance says in every run that elaborates it (R-mc21-7): one note naming what is off, so an
    /// impairment switched off and forgotten is never invisible — and, for a switch naming a part the model does not have,
    /// that it changes nothing (R-mc21-2), so one schematic can carry several models.
    /// </summary>
    private static void NoteComposedSwitches(string path, ComposedSwitches s, LoadedComposedModel loaded, ElaboratedNetlist netlist)
    {
        if (s.IsDefault) return;
        netlist.AddNoteOnce($"composed-switches:{path}", $"{path}: {s.Sentence()}.");

        string name = Path.GetFileName(loaded.Path);
        foreach (string key in (string[])[ComposedSwitches.GateLagName, ComposedSwitches.DrainLagName,
                                          ComposedSwitches.SelfHeatingName, ComposedSwitches.DispersionName])
            if (!s[key] && ComposedSwitches.MissingPart(key, loaded) is { } what)
                netlist.AddNoteOnce($"composed-switch-inert:{path}:{key}", $"{path}: '{name}' {what}; {key}=false changes nothing.");
    }

    /// <summary>
    /// The temperature rule of D8, said once per run per model: a single-temperature model run at
    /// another temperature is used as it is; a ranged one is clamped to its range.
    /// </summary>
    private void WarnComposedTemperature(ComposedCoreModel core, ElaboratedNetlist netlist)
    {
        var    t    = core.Loaded.Model.Temperature;
        string name = Path.GetFileName(core.Loaded.Path);
        double asked = core.RequestedC;
        string ta   = asked.ToString("0.##", CultureInfo.InvariantCulture);
        // An instance's own Temp is named as such, so the warning points at the row that set it.
        string whose = asked == _ambientC ? $"this run's temp is {ta} °C" : $"'{core.Label}' has Temp={ta} °C";
        string key   = $"cmodel-temp:{core.Loaded.Path}:{ta}";

        if (t.Mode == TemperatureMode.Single)
        {
            if (Math.Abs(asked - t.T0) > 1e-9)
                netlist.AddWarningOnce(key,
                    $"'{name}' is a one-temperature model, valid at {t.T0.ToString("0.##", CultureInfo.InvariantCulture)} °C; " +
                    $"{whose} and the model is used as it is.");
            return;
        }

        if (asked < t.Tmin || asked > t.Tmax)
            netlist.AddWarningOnce(key,
                $"'{name}' covers {t.Tmin.ToString("0.##", CultureInfo.InvariantCulture)} to " +
                $"{t.Tmax.ToString("0.##", CultureInfo.InvariantCulture)} °C; {whose}, " +
                $"so the model is evaluated at {core.TemperatureC.ToString("0.##", CultureInfo.InvariantCulture)} °C.");
    }

    /// <summary><c>ComposedCore</c>'s <c>File</c> is a path, kept verbatim and resolved as SnP's is;
    /// <c>N</c> and <c>m</c> are evaluated.</summary>
    private IReadOnlyDictionary<string, Value> ResolveComposedParameters(Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var ov in inst.Overrides)
            result[ov.Name] = ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase)
                ? new Value(ResolveSnpFilePath(Unquote(ov.Expression.Trim())))
                // A switch's bare spelling is a value, not a name to look up.
                : ComposedSwitches.IsSwitch(ov.Name) && ComposedSwitches.Spelling(ov.Expression) is { } b ? new Value(b)
                : EvalOverride(inst, ov, parentScope);
        return result;
    }

    // ── Match parameter resolution ────────────────────────────────────────────

    /// <summary>
    /// Match stores <c>Design</c> (the base64 design payload — see <c>MatchEmbedding</c>) and the
    /// <c>Response</c> echo verbatim, and evaluates the remaining echo parameters as ordinary
    /// expressions.
    ///
    /// <para><b><c>Design</c> is a payload, not an expression.</b> Base64 is a bare identifier to the
    /// tokenizer and would fail — or, worse, resolve against a variable that happens to share its
    /// spelling. <c>Response</c> is an enum NAME and has the same problem. Everything else here is a
    /// number the user may want to read on the schematic, so it goes through the evaluator like any
    /// other parameter; none of it is an input (match.md §7.2 — <c>Design</c> is authoritative and
    /// complete), so an echo that fails to evaluate is skipped rather than fatal.</para>
    ///
    /// <para><c>MatchName</c> is injected, not authored: it is what lets the factory's refusal name
    /// the instance the user placed rather than the type. Chain's <c>ChainName</c> and Tuner's
    /// <c>TunerName</c> are the same device.</para>
    /// </summary>
    private IReadOnlyDictionary<string, Value> ResolveMatchParameters(
        Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal)
        {
            ["MatchName"] = new Value(inst.InstanceName),
        };

        foreach (var ov in inst.Overrides)
        {
            if (ov.Name.Equals(MatchEmbedding.DesignParameter, StringComparison.OrdinalIgnoreCase)
                || ov.Name.Equals("Response", StringComparison.OrdinalIgnoreCase))
            {
                result[ov.Name] = new Value(Unquote(ov.Expression));
            }
            else
            {
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
                catch { /* an echo parameter is display only; the design itself carries the truth */ }
            }
        }
        return result;
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
            else if (ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase)
                  || ov.Name.Equals("MaterialLibrary", StringComparison.OrdinalIgnoreCase))
            {
                // `MaterialLibrary` is a path to the .cmat this instance's wire metals come from — the
                // same kind of value as `File`, resolved the same way.
                var raw = ov.Expression;
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = raw[1..^1];
                result[ov.Name] = new Value(ResolveSnpFilePath(raw));
            }
            else if (ov.Name.Equals("RefPin", StringComparison.OrdinalIgnoreCase)
                  || ov.Name.Equals("IncludeCapacitance", StringComparison.OrdinalIgnoreCase)
                  || ov.Name.Equals("FixedTemp", StringComparison.OrdinalIgnoreCase)
                  || IsWBondNameValued(ov.Name))
            {
                // Verbatim, like Design: the schematic writes the WORD "true"/"false", and running
                // that through the evaluator would depend on whether a bare `true` happens to parse
                // as a literal — a dependency with nothing to gain. The factory reads either
                // spelling. It is not sweepable and there is nothing to sweep it over. The same holds
                // for `IncludeCapacitance` — a model is present or it is not, and nothing between the
                // two is interpolable — and for `FixedTemp`, the Temp row's checkbox.
                //
                // The same rule covers the NAME-valued controlling parameters of §5.5.1/WB44:
                // `Material`/`Material_<array>` is a metal's name, `Arrays` is the recorded array list
                // ("G1|G2") the linked-instance drift check reads, and `Source` is Carried/Linked.
                // Every one of them is an identifier or a delimited list the evaluator would either
                // fail on or — worse — resolve against some unrelated variable that happens to share
                // the name. Loop height and diameter are NOT here: they are lengths, and being
                // ordinary expressions is exactly what makes them sweepable (WB44 property 4).
                result[ov.Name] = new Value(Unquote(ov.Expression));
            }
            else if (ov.Name.Equals("GroundPlane", StringComparison.OrdinalIgnoreCase)
                  && IsBooleanSpelling(Unquote(ov.Expression)))
            {
                // The Inspector's GroundPlane picker writes the WORD "true"/"false". Evaluated as an
                // expression, `true` is an unresolved name, the catch below dropped it, and the instance
                // silently took its payload's own plane — a design stored with the plane off was then
                // refused for an undeclared return path however the picker was set. A boolean spelling
                // goes through verbatim, as IncludeCapacitance does; anything else (a VAR reference) is
                // still evaluated, so it stays sweepable.
                result[ov.Name] = new Value(Unquote(ov.Expression));
            }
            else
            {
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
                catch { /* skip unresolvable; the factory errors if a required numeric is missing */ }
            }
        }

        // Injected, not authored — Match's `MatchName` exactly: what lets a refusal name the instance
        // the user placed (a missing material library, for one) rather than the type.
        result["WBondName"] = new Value(inst.InstanceName);
        return result;
    }

    /// <summary>True for any spelling <see cref="Devices.BooleanParameter"/> reads as a boolean.</summary>
    private static bool IsBooleanSpelling(string text) =>
        Devices.BooleanParameter.TrueSpellings.Concat(Devices.BooleanParameter.FalseSpellings)
               .Contains(text.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True for a wBond parameter whose value is a NAME rather than an expression — see
    /// <see cref="ResolveWBondParameters"/>'s own note for why each one is on this list.
    /// </summary>
    private static bool IsWBondNameValued(string name) =>
        name.Equals("Material", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Material_", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Arrays", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Source", StringComparison.OrdinalIgnoreCase);

    private static string Unquote(string raw) =>
        raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;

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
            if (StoreRawPhaseNoise(ov, result)) continue;
            // Z[k] and G[k] may be complex; store as-is for the factory to parse.
            if (RxP1ToneZEntry.IsMatch(ov.Name) || RxP1ToneGEntry.IsMatch(ov.Name))
            {
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
                catch { /* skip unresolvable */ }
            }
            else
            {
                try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
                catch { /* skip unresolvable */ }
            }
        }

        return result;
    }

    // ── MOD_Source parameter resolution ──────────────────────────────────────
    // Carrier[i] is a carrier DESCRIPTION ("NR;BW=20MHz;SCS=30kHz;..."), never an expression: stored
    // verbatim with its quotes removed, except that a FILE carrier's File= path resolves against the
    // workspace root exactly as an SnP's File does (the Signal Designer stores it with SnpPathPolicy).
    // Shaping may be the word `off`. Everything else (Fc, Pavg, Z, Record, Clip, Shaping in dBc) is an
    // ordinary numeric expression with its unit.

    private static readonly Regex RxModCarrier = new(@"^Carrier\[\d+\]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private IReadOnlyDictionary<string, Value> ResolveModSourceParameters(Instance inst, Scope parentScope)
    {
        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        result["ModSourceName"] = new Value(inst.InstanceName);
        foreach (var ov in inst.Overrides)
        {
            string raw = ov.Expression.Trim();
            if (StoreRawPhaseNoise(ov, result)) continue;
            if (RxModCarrier.IsMatch(ov.Name))
            {
                result[ov.Name] = new Value(RfCore.Signals.SignalSpec.MapFilePath(raw.Trim('"'), ResolveSnpFilePath));
                continue;
            }
            if (ov.Name.Equals("Shaping", StringComparison.OrdinalIgnoreCase) &&
                raw.Trim('"').Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                result[ov.Name] = new Value("off");
                continue;
            }
            // Not skipped on failure, unlike P1Tone: a skipped Fc or Pavg fell back to a default and the
            // run went ahead at a frequency or power nobody wrote. The evaluator's own error names the cause.
            result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);
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
            if (StoreRawPhaseNoise(ov, result)) continue;
            try { result[ov.Name] = EvalOverride(inst, ov, parentScope); }
            catch { /* skip unresolvable — degrades gracefully */ }
        }

        return result;
    }

    /// <summary>The source parameter a phase-noise mask is stated in (brief NZ-7): <c>"offset:level, …"</c>.</summary>
    public const string PhaseNoiseParamName = "PhaseNoise";

    /// <summary>
    /// A source's <c>PhaseNoise=</c> is a MASK (<c>"1kHz:-80, 1MHz:-140"</c>), never an expression: stored verbatim, its
    /// quotes removed, for the envelope engine to read. True when <paramref name="ov"/> was it. (<c>PhaseNoiseFloor</c> is
    /// an ordinary number and evaluates as one.)
    /// </summary>
    private static bool StoreRawPhaseNoise(ParameterAssignment ov, Dictionary<string, Value> into)
    {
        if (!ov.Name.Equals(PhaseNoiseParamName, StringComparison.OrdinalIgnoreCase)) return false;
        into[PhaseNoiseParamName] = new Value(ov.Expression.Trim().Trim('"').Trim());
        return true;
    }

    // Port voltage names in SDD equations — _v1, _v2, … (injected at eval time, not scope vars).
    private static readonly Regex RxPortVoltage = new(@"^_v\d+$", RegexOptions.Compiled);
    // Control current names — _c1, _c2, … (injected by the engine, not scope vars).
    private static readonly Regex RxControlCurrent = new(@"^_c\d+$", RegexOptions.Compiled);
    // SDD equation parameter name pattern — matches I[...], Q[...], F[...], C[...], i[...], V[...].
    // V[p] is the BRANCH equation (a port's voltage held at an expression), and it has to be here
    // for the same reason the others are: an equation is stored VERBATIM and parsed by the factory,
    // never evaluated in the design scope, because it names port voltages that do not exist yet.
    private static readonly Regex RxSddEquation = new(@"^[IFCQiHV][^\[]*\[", RegexOptions.Compiled);

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
            result[ov.Name] = EvalOverride(inst, ov, parentScope);
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
    /// Turns every collected <c>VProbe</c> into a net-name ALIAS on the node map, or refuses the run.
    ///
    /// <para><b>An alias, but only where one is needed.</b> A probe on a net the user has ALREADY
    /// named — their own schematic label — becomes a second name for it, and the results carry that
    /// node twice, once under each name. The label is never overwritten: a wire the user named keeps
    /// the name they gave it. A probe on an UNNAMED net does not get an alias at all, because
    /// extraction has already made the probe's name that net's own name — publishing <c>n7</c> beside
    /// it would put two rows in every result for one point in the circuit and only one of them would
    /// be a name anybody chose. Either way the probe's name joins
    /// <see cref="NodeMap.LabeledNames"/>, for the same reason a label does: a name the user picked
    /// is exactly what the trace picker's own filter is for.</para>
    ///
    /// <para><b>Two failures REFUSE the run rather than reporting.</b> Two probes sharing a name, and
    /// a probe sharing a name with a net, both end with one name meaning two different things in the
    /// results — and a plot picked by name would then be silently the wrong trace. That is not
    /// something to warn about and continue past. Everything else is a warning: a probe naming a net
    /// that does not exist, or naming ground, reports nothing and stops there.</para>
    ///
    /// <para>The duplicate check runs BEFORE the net lookup, so two probes named the same thing are
    /// reported as duplicates whether or not either is connected.</para>
    /// </summary>
    private void RegisterVoltageProbes(ElaboratedNetlist netlist)
    {
        if (_voltageProbes.Count == 0) return;

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, _) in _voltageProbes)
        {
            if (seen.TryGetValue(path, out _))
                throw new InvalidOperationException(
                    $"Two voltage probes are both named '{path}'. A VProbe's instance name is the " +
                    $"name its net reports under, so two of them would make one name mean two " +
                    $"different traces; rename one.");
            seen[path] = path;
        }

        foreach (var (path, net) in _voltageProbes)
        {
            // The net may already BE the probe's name — an unnamed net takes it outright rather than
            // carrying two names for one point (NetExtractor.AssignNetNames). That is not a
            // collision; it is the same probe, and there is nothing left to alias.
            bool namesItsOwnNet = string.Equals(path, net, StringComparison.Ordinal);

            if (!namesItsOwnNet && netlist.Nodes.TryGetIndex(path, out _))
                throw new InvalidOperationException(
                    $"VProbe '{path}' has the same name as a net in this design. A VProbe reports " +
                    $"its net under its own name, so the two would collide in the results; rename " +
                    $"the probe or the net.");

            if (!netlist.Nodes.TryGetIndex(net, out int node))
            {
                netlist.AddWarning(
                    $"VProbe '{path}' names net '{net}', which nothing else in the design reaches, " +
                    $"so it will not report any results.");
                continue;
            }

            if (node == 0)
            {
                netlist.AddWarning(
                    $"VProbe '{path}' is on the ground net, which is 0 V by definition, so it will " +
                    $"not report any results.");
                continue;
            }

            netlist.Nodes.LabeledNames.Add(path);
            if (namesItsOwnNet) continue;
            netlist.Nodes.AddAlias(path, node);
        }
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

        // Gaps in the port numbering (e.g. {1,3} is missing 2) — ONE line, not one per number
        // (DFR27-2 R-dfr27-2-2). The result keeps the Terms' own numbers, so nothing is renumbered;
        // what a gap costs is the Touchstone export, which numbers ports by position.
        if (numToPath.Count > 0)
        {
            int maxNum  = numToPath.Keys.Max();
            var missing = Enumerable.Range(1, Math.Max(0, maxNum)).Where(n => !numToPath.ContainsKey(n)).ToList();
            if (missing.Count > 0)
                netlist.AddWarning(
                    $"S-parameter ports are numbered {string.Join(", ", numToPath.Keys.OrderBy(k => k))}; " +
                    $"{(missing.Count == 1 ? "Num" : "Nums")} {RfCore.Data.DataSetBuilder.FormatPortRuns(missing)} " +
                    $"{(missing.Count == 1 ? "is" : "are")} missing. Results keep these numbers, but a " +
                    "Touchstone export will be refused until the Terms are numbered 1…N.");
        }
    }
}
