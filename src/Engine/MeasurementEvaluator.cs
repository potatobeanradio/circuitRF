using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using RfCore.Data;

namespace CircuitRF.Engine;

/// <summary>
/// Evaluates <see cref="Measurement"/> expressions declared on a <see cref="TestBench"/>
/// against the run's analysis results, adding each result cube to the supplied DataSet.
///
/// Measurements are evaluated in declaration order and may reference earlier measurements
/// by name (composable cube algebra).  The expression engine is extended with:
///   - Qualified cube accessors: <c>HB1.V("n_drain", 1, All)</c>
///   - Cube-valued arithmetic: operators broadcast element-wise over DataCubes.
///   - Element-wise helpers: conj, re, im, mag, phase, dB, dB10, dBm, log10, ln.
///
/// Usage:
/// <code>
///   var me = new MeasurementEvaluator(tb, netlist, analysisResults);
///   me.EvaluateInto(runDataSet);
/// </code>
/// </summary>
public sealed class MeasurementEvaluator
{
    private readonly TestBench        _tb;
    private readonly ElaboratedNetlist _netlist;
    private readonly IReadOnlyDictionary<string, DataSet>            _analysisResults;
    private readonly IReadOnlyDictionary<string, ILinearBackSolver>? _backSolvers;
    private readonly IReadOnlyDictionary<string, IFieldMetrics>?     _fields;

    public MeasurementEvaluator(
        TestBench                        tb,
        ElaboratedNetlist                netlist,
        IReadOnlyDictionary<string, DataSet>            analysisResults,
        IReadOnlyDictionary<string, ILinearBackSolver>? backSolvers = null,
        IReadOnlyDictionary<string, IFieldMetrics>?     fields = null)
    {
        _tb              = tb;
        _netlist         = netlist;
        _analysisResults = analysisResults;
        _backSolvers     = backSolvers;
        _fields          = fields;
    }

    /// <summary>
    /// Evaluate all measurements and add every result cube to <paramref name="ds"/>.
    /// Returns per-measurement error strings for any that failed; successful cubes are always emitted.
    /// </summary>
    public IReadOnlyList<string> EvaluateInto(DataSet ds)
        => Evaluate((m, result) => ds.Add(m.Name, ToCube(m, result)), ds.Add, true, null, null);

    /// <summary>
    /// <see cref="EvaluateInto(DataSet)"/>, then each of <paramref name="expressions"/> in the same
    /// measurement scope — after the measurements, so an expression may name one (an optimizer goal
    /// written over a measurement). <paramref name="measurements"/> false evaluates the expressions
    /// alone. An expression that fails comes back with its error rather than throwing, one outcome per
    /// expression in the order given.
    /// </summary>
    public IReadOnlyList<string> EvaluateInto(
        DataSet ds, IReadOnlyList<string> expressions, bool measurements,
        out IReadOnlyList<ExpressionOutcome> outcomes)
    {
        var list = new List<ExpressionOutcome>(expressions.Count);
        outcomes = list;
        return Evaluate((m, result) => ds.Add(m.Name, ToCube(m, result)), ds.Add, measurements, expressions, list);
    }

    // ── Shared evaluation core ───────────────────────────────────────────────────

    private IReadOnlyList<string> Evaluate(
        Action<Measurement, Value> emit, Action<string, DataCube> emitCompanion, bool measurements,
        IReadOnlyList<string>? expressions, List<ExpressionOutcome>? outcomes)
    {
        var errors = new List<string>();
        var declared = measurements ? _tb.Measurements : [];
        if (declared.Count == 0 && expressions is not { Count: > 0 }) return errors;

        var ctx  = new MeasurementContext(_analysisResults, _backSolvers, _fields);
        var eval = new Evaluator(ctx);

        // A name a measurement may read and this run has no value for, with the reason — reported beside the
        // unresolved-name error rather than leaving the bare name to read as "never supported".
        var unavailable = new Dictionary<string, string>(StringComparer.Ordinal);

        // Swept variables: a parametric sweep prepends an axis named after its SweepVarName, carrying
        // the swept values. Each is injected as a 1-D cube so a measurement that references the sweep
        // variable directly (e.g. "Pin_avail_dBm = Pin") gets one element per sweep point — and so it
        // broadcast-aligns (same axis name+values) with swept analysis cubes. This OVERRIDES the scalar
        // global. The actual axis (name + values) comes from the results — authoritative even when a
        // sweep was disabled/collapsed (its axis simply won't be present, so it stays a scalar).
        var sweptVarNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in _tb.Analyses)
            if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.SweepVarName))
                sweptVarNames.Add(ps.SweepVarName);

        var sweepAxes = new Dictionary<string, Axis>(StringComparer.Ordinal);
        if (sweptVarNames.Count > 0)
            foreach (var ds in _analysisResults.Values)
                foreach (var (_, cube) in ds.Cubes)
                    foreach (var ax in cube.Axes)
                        if (sweptVarNames.Contains(ax.Name) && !sweepAxes.ContainsKey(ax.Name))
                            sweepAxes[ax.Name] = ax;

        // A variable DERIVED from a swept one (y = 2*x) follows the sweep too: one value per point, not the nominal one.
        var derived = ResolveDerived(sweepAxes);

        // Globals scope: inject resolved global variables so measurements can reference them.
        var globalScope = new Scope("globals");
        foreach (var (name, value) in _netlist.ResolvedGlobals)
        {
            if (derived.ContainsKey(name)) continue;
            globalScope.Bind(name, value.ToString()!);     // dummy expression string
            eval.InjectResolved("globals", name, value);   // pre-memoize actual value
        }

        foreach (var (name, ax) in sweepAxes)
        {
            globalScope.Bind(name, "0");                                      // ensure Lookup succeeds
            eval.InjectResolved("globals", name, new Value(SweepCube(ax)));   // override the scalar global
        }

        foreach (var (name, (value, why)) in derived)
        {
            if (value is { } v)
            {
                globalScope.Bind(name, "0");
                eval.InjectResolved("globals", name, v);
            }
            else unavailable[name] = $"'{name}' depends on a swept variable and does not evaluate over its sweep: {why}";
        }

        // `freq` — the run's own frequency axis, as a 1-D cube. A measurement over an S-parameter
        // analysis has a natural frequency variable, and without this one a caller has no spelling
        // for it at all: the reserved `freq` keyword is injected per-stamping-frequency into a
        // component-value scope (Z_Port, an SDD equation) and never reached a measurement, so
        // "myCalc = 1/2/pi/freq" failed with an unresolved name. Injected as a cube (not a scalar)
        // for the same reason a swept variable is: it broadcast-aligns by axis name+values with the
        // analysis cubes it is used alongside, so "1/(2*pi*freq*C)" lands one element per frequency.
        {
            var grids = new List<Axis>();
            foreach (var ds in _analysisResults.Values)
                foreach (var (_, cube) in ds.Cubes)
                    foreach (var ax in cube.Axes)
                        if (ax.Name == FreqName && !grids.Any(g => SameGrid(g, ax)))
                            grids.Add(ax);

            if (grids.Count == 1)
            {
                var ax = grids[0];
                var freqCube = new DataCube([new Axis(FreqName, ax.Values, ax.Unit)],
                                            (double[])ax.Values.Clone());
                globalScope.Bind(FreqName, "0");                            // ensure Lookup succeeds
                eval.InjectResolved("globals", FreqName, new Value(freqCube));
            }
            else if (grids.Count == 0)
            {
                unavailable[FreqName] = "no analysis in this run produced a frequency axis "
                                      + $"(available: [{string.Join(", ", _analysisResults.Keys)}])";
            }
            else
            {
                unavailable[FreqName] = $"this run's analyses use {grids.Count} different frequency grids, so "
                                      + "'freq' is ambiguous — reference the analysis-qualified cube instead";
            }
        }

        // Measurement scope: child of globals; used to inject computed measurement cubes.
        var mScope = new Scope("measurements", globalScope);

        foreach (var m in declared)
        {
            Value result;
            eval.TakeCompanions();
            eval.TakeNotes();
            try { result = eval.Eval(m.Expression, mScope, m.Unit); }
            catch (Exception ex)
            {
                errors.Add($"Measurement '{m.Name}': failed to evaluate '{m.Expression}': {Why(ex, unavailable)}");
                continue;  // skip bind+emit; later measurements referencing this name report cascade error
            }

            // What an evaluation had to say beside its value (a gain circle that does not exist at some frequencies) is
            // the run's warning, once per measurement.
            foreach (var note in eval.TakeNotes())
                _netlist.AddWarningOnce($"measurement-note:{m.Name}:{note}", $"Measurement '{m.Name}': {note}");

            // Inject the result so later measurements can reference this one by name.
            mScope.Bind(m.Name, result.ToString()!);
            eval.InjectResolved("measurements", m.Name, result);

            try
            {
                emit(m, result);
                // What an axis-building function had to say beside its cube (a histogram's bin width), stored as
                // <name>:<companion> — measurements.md "Reductions over an axis".
                foreach (var (suffix, companion) in eval.TakeCompanions())
                    emitCompanion($"{m.Name}:{suffix}", companion);
            }
            catch (Exception ex)
            {
                errors.Add($"Measurement '{m.Name}': failed to emit result: {ex.Message}");
            }
        }

        foreach (var text in expressions ?? [])
        {
            try { outcomes!.Add(new ExpressionOutcome(text, eval.Eval(text, mScope, null), null)); }
            catch (Exception ex) { outcomes!.Add(new ExpressionOutcome(text, null, Why(ex, unavailable))); }
        }
        return errors;
    }

    /// <summary>An evaluation's error, and the reason the run has no value for the name it could not resolve when there is
    /// one: an unresolved `freq` reads as "never supported" otherwise, since it IS available in most runs.</summary>
    private static string Why(Exception ex, Dictionary<string, string> unavailable)
        => ex is UnresolvedNameException u && unavailable.TryGetValue(u.Name, out var hint) ? $"{ex.Message} — {hint}" : ex.Message;

    private static DataCube SweepCube(Axis ax) => new([new Axis(ax.Name, ax.Values)], (double[])ax.Values.Clone());

    /// <summary>
    /// Every global derived from a swept one (<c>y = 2*x</c> with <c>x</c> swept), through other globals and through user
    /// functions' bodies, each as a cube over the sweeps it depends on — or, where it does not evaluate over them, why not.
    ///
    /// <para><see cref="ElaboratedNetlist.ResolvedGlobals"/> holds the values of the ONE elaboration the run started with.
    /// Each sweep point re-elaborates with its own <c>x</c>, so the circuit followed the sweep all along (a source set to
    /// <c>y</c> is at <c>2*x</c> at every point); a measurement reading <c>y</c> read that first elaboration's nominal number.</para>
    ///
    /// <para>They are resolved again here as the elaborator resolves them — every global bound with its own expression and
    /// unit, the underived ones pinned to the values the run used — with each swept variable bound to its axis cube and to
    /// the unit its points were injected with (the axis's, <see cref="ParametricSweepEngine"/>'s base unit), so var-unit-wins
    /// applies exactly as it did at each point. With an evaluator of its own, which the user functions are registered on: the
    /// measurements' evaluator gains none it did not have.</para>
    /// </summary>
    private Dictionary<string, (Value? Value, string? Why)> ResolveDerived(Dictionary<string, Axis> sweepAxes)
    {
        var result = new Dictionary<string, (Value?, string?)>(StringComparer.Ordinal);
        if (sweepAxes.Count == 0) return result;

        var dependent = new HashSet<string>(sweepAxes.Keys, StringComparer.Ordinal);
        var derived   = new List<Variable>();
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var v in _tb.GlobalVariables)
                if (!dependent.Contains(v.Name) && Refs(v.Expression).Overlaps(dependent))
                {
                    dependent.Add(v.Name);
                    derived.Add(v);
                    grew = true;
                }
        }

        if (derived.Count == 0) return result;

        var ev    = new Evaluator { Statistics = _tb.StatisticalDraws };
        var scope = new Scope("global");
        foreach (var fn in _tb.Functions) ev.RegisterFunction(fn);
        foreach (var v in _tb.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        foreach (var (name, value) in _netlist.ResolvedGlobals)
            if (!dependent.Contains(name)) ev.InjectResolved(scope.DebugName, name, value);
        foreach (var (name, ax) in sweepAxes)
        {
            scope.Bind(name, "0", string.IsNullOrEmpty(ax.Unit) ? null : ax.Unit);
            ev.InjectResolved(scope.DebugName, name, new Value(SweepCube(ax)));
        }

        foreach (var v in derived)
        {
            try { result[v.Name] = (ev.Resolve(v.Name, scope), null); }
            catch (Exception ex) { result[v.Name] = (null, ex.Message); }
        }
        return result;
    }

    /// <summary>The names an expression reads, a user function's body included; none when it does not parse.</summary>
    private HashSet<string> Refs(string expression)
    {
        try { return AstWalker.CollectRefs(UserFunctionInliner.Inline(Parser.Parse(expression), _tb.Functions)); }
        catch (Exception) { return []; }
    }

    /// <summary>The reserved frequency name (expressions.md §3) — the axis S-parameter cubes carry.</summary>
    private const string FreqName = "freq";

    /// <summary>Two frequency axes are the same grid when they agree to the tolerance
    /// <see cref="DataCube"/>'s own broadcast alignment uses — anything else would inject a `freq`
    /// that cannot align with the cube a measurement uses it against.</summary>
    private static bool SameGrid(Axis a, Axis b)
    {
        if (a.Length != b.Length) return false;
        for (int k = 0; k < a.Length; k++)
            if (Math.Abs(a.Values[k] - b.Values[k]) > 1e-12 * (1 + Math.Abs(b.Values[k])))
                return false;
        return true;
    }

    private static DataCube ToCube(Measurement m, Value result) => result.Kind switch
    {
        ValueKind.Cube    => result.AsCube(),
        ValueKind.Real    => DataCube.Scalar(result.AsReal()),
        ValueKind.Complex => DataCube.Scalar(result.AsComplex()),
        _ => throw new InvalidOperationException(
            $"Measurement '{m.Name}' produced an unsupported value kind: {result.Kind}")
    };
}

/// <summary>One expression evaluated in a run's measurement scope: its value, or why it has none.</summary>
public sealed record ExpressionOutcome(string Expression, Value? Value, string? Error);
