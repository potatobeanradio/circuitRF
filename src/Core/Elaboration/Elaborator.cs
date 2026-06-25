using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
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
    /// Workspace root for resolving relative file-path parameters (e.g. SnP File).
    /// Null → relative paths are left as-authored (legacy CWD resolution) for CLI / no-workspace runs.
    /// Only a path string crosses into Core here — no UI dependency.
    /// </summary>
    public string? BaseDirectory { get; init; }

    public Elaborator(params Library[] libraries)
        => _libraries = libraries;

    public ElaboratedNetlist Elaborate(TestBench tb)
    {
        var netlist     = new ElaboratedNetlist();
        var globalScope = BuildGlobalScope(tb);

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
                var model          = ComponentModelFactory.TryCreate(inst.Reference, resolvedParams)
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

                // Layer-2 + Layer-3 linter: a Term/Port inside an instantiated sub-cell is a
                // design error — it will be treated as inert and never become an S-param port.
                if ((model is PortModel or TermModel) && instancePathPrefix.Length > 0)
                    netlist.AddWarning(
                        $"Term '{childPath}' is inside an instantiated cell and was ignored; " +
                        $"use a Pin for cell interfaces and place Terms only in the testbench.");

                var ec = new ElaboratedComponent(inst.Reference, childPath, resolvedNodes, resolvedParams, model)
                         { ReferenceNode = refNode };
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

        var result = new Dictionary<string, Value>(StringComparer.Ordinal);
        foreach (var ov in inst.Overrides)
            result[ov.Name] = _evaluator.Eval(ov.Expression, parentScope, ov.Unit);
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
                // Store Z[i,j] expression as string; inject referenced scope vars.
                result[ov.Name] = new Value(ov.Expression);
                InjectZPortScopeVars(ov.Expression, parentScope, result);
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
    // File / InterpMode / ExtrapMode / PinConfig / RefNode are STRING params — store raw, never Eval().
    // (A file path like "/Users/…/x.s2p" is not an expression.) Only NumPorts is numeric.

    private static readonly HashSet<string> _snpStringParams =
        new(StringComparer.OrdinalIgnoreCase) { "File", "InterpMode", "ExtrapMode", "PinConfig", "RefNode" };

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
