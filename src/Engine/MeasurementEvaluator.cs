using System.Globalization;
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

        // In the results' own order: a sweep prepends its axis to every cube, so a swept cube carries them outermost first.
        var sweepAxes = new List<Axis>();
        if (sweptVarNames.Count > 0)
            foreach (var ds in _analysisResults.Values)
                foreach (var (_, cube) in ds.Cubes)
                    foreach (var ax in cube.Axes)
                        if (sweptVarNames.Contains(ax.Name) && !sweepAxes.Exists(a => a.Name == ax.Name))
                            sweepAxes.Add(ax);

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

        foreach (var ax in sweepAxes)
        {
            globalScope.Bind(ax.Name, "0");                                      // ensure Lookup succeeds
            eval.InjectResolved("globals", ax.Name, new Value(SweepCube(ax)));   // override the scalar global
        }

        foreach (var (name, (value, why)) in derived)
        {
            if (value is { } v)
            {
                globalScope.Bind(name, "0");
                eval.InjectResolved("globals", name, v);
            }
            else unavailable[name] = $"'{name}' depends on a swept variable and has no value at {why}";
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

    private static DataCube SweepCube(Axis ax) => new([SweepAxis(ax)], (double[])ax.Values.Clone());

    /// <summary>A sweep's axis as a measurement's cubes carry it: its name and values, which is what broadcasting aligns.</summary>
    private static Axis SweepAxis(Axis ax) => new(ax.Name, ax.Values);

    /// <summary>
    /// Every global derived from a swept one (<c>y = 2*x</c> with <c>x</c> swept), through other globals and through user
    /// functions' bodies, as a cube over the sweeps it depends on, in the results' axis order. Where a point fails, the
    /// value is null and the reason names the point.
    ///
    /// <para><see cref="ElaboratedNetlist.ResolvedGlobals"/> holds the values of the ONE elaboration the run started with.
    /// Each sweep point re-elaborates with its own <c>x</c>, so the circuit followed the sweep all along (a source set to
    /// <c>y</c> is at <c>2*x</c> at every point); a measurement reading <c>y</c> read that first elaboration's nominal number.</para>
    ///
    /// <para>So each derived global is evaluated at each point of its sweeps exactly as that point's elaboration evaluated
    /// it: the elaborator's global scope (every global with its own expression and unit, and the ambient <c>temp</c> where
    /// the design states none), the point's values bound as <see cref="ParametricSweepEngine.PointVariable"/> binds them,
    /// plain numbers throughout, and an evaluator with the design's user functions and the trial's draws. What a measurement
    /// reads is then the number the circuit was built with, whatever the expression — a conditional, <c>max</c>,
    /// <c>polar</c>, <c>imag(log(z))</c> — and in that point's units. Its own evaluator: the measurements' gains nothing.</para>
    /// </summary>
    private Dictionary<string, (Value? Value, string? Why)> ResolveDerived(List<Axis> sweepAxes)
    {
        var result = new Dictionary<string, (Value?, string?)>(StringComparer.Ordinal);
        if (sweepAxes.Count == 0) return result;

        // Who reads each name, from each global's references taken once; then outward from each swept name over them.
        var readers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var v in _tb.GlobalVariables)
            foreach (var r in Refs(v.Expression))
            {
                if (!readers.TryGetValue(r, out var list)) readers[r] = list = [];
                list.Add(v.Name);
            }
        var swept = sweepAxes.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        var dependsOn = new Dictionary<string, List<int>>(StringComparer.Ordinal);   // derived global → its sweeps
        for (int a = 0; a < sweepAxes.Count; a++)
        {
            var queue = new Queue<string>([sweepAxes[a].Name]);
            var seen  = new HashSet<string>(StringComparer.Ordinal) { sweepAxes[a].Name };
            while (queue.TryDequeue(out var name))
                foreach (var reader in readers.GetValueOrDefault(name) ?? [])
                    if (!swept.Contains(reader) && seen.Add(reader))   // a swept variable is set at each point, not derived
                    {
                        if (!dependsOn.TryGetValue(reader, out var axes)) dependsOn[reader] = axes = [];
                        axes.Add(a);
                        queue.Enqueue(reader);
                    }
        }
        if (dependsOn.Count == 0) return result;

        var scope = new Scope("global");
        foreach (var v in _tb.GlobalVariables) scope.Bind(v.Name, v.Expression, v.Unit);
        if (scope.Lookup(CircuitRF.Core.Devices.Temperature.AmbientGlobalName) is null)   // as the elaborator binds it
            scope.Bind(CircuitRF.Core.Devices.Temperature.AmbientGlobalName, _netlist.AmbientC.ToString(CultureInfo.InvariantCulture));
        var sweeps = _tb.Analyses.OfType<ParametricSweepAnalysis>().ToList();

        // One pass over the points of each set of sweeps, every global depending on exactly that set resolved at each point.
        foreach (var group in dependsOn.GroupBy(kv => string.Join(",", kv.Value)))
        {
            int[] axes = [.. group.First().Value];
            string[] names = [.. group.Select(kv => kv.Key)];
            int[] lengths = [.. axes.Select(a => sweepAxes[a].Length)];
            int count = lengths.Aggregate(1, (p, n) => p * n);
            var values = names.ToDictionary(n => n, _ => new Value[count], StringComparer.Ordinal);
            var why    = new Dictionary<string, string>(StringComparer.Ordinal);
            var at     = new int[axes.Length];

            for (int flat = 0; flat < count; flat++)
            {
                for (int d = axes.Length - 1, rest = flat; d >= 0; d--) { at[d] = rest % lengths[d]; rest /= lengths[d]; }
                for (int d = 0; d < axes.Length; d++)
                {
                    var ax = sweepAxes[axes[d]];
                    var pv = ParametricSweepEngine.PointVariable(sweeps.First(s => s.SweepVarName == ax.Name), ax.Values[at[d]], ax.Unit);
                    scope.Bind(pv.Name, pv.Expression, pv.Unit);
                }
                var ev = new Evaluator { Statistics = _tb.StatisticalDraws };
                foreach (var fn in _tb.Functions) ev.RegisterFunction(fn);
                string Point() => string.Join(", ", axes.Select((a, d) => $"{sweepAxes[a].Name} = " +
                                        sweepAxes[a].Values[at[d]].ToString("G6", CultureInfo.InvariantCulture)));
                foreach (var name in names)
                {
                    if (why.ContainsKey(name)) continue;
                    try
                    {
                        var val = ev.Resolve(name, scope);
                        if (val.Kind is ValueKind.Real or ValueKind.Complex) values[name][flat] = val;
                        else why[name] = $"{Point()}: {val.Kind}, not a number";
                    }
                    catch (Exception ex) { why[name] = $"{Point()}: {ex.Message}"; }
                }
            }

            Axis[] cubeAxes = [.. axes.Select(a => SweepAxis(sweepAxes[a]))];
            foreach (var name in names)
            {
                var v = values[name];
                result[name] = why.TryGetValue(name, out var reason) ? (null, reason)
                    : (new Value(v.All(x => x.Kind == ValueKind.Real)
                        ? new DataCube(cubeAxes, [.. v.Select(x => x.AsReal())])
                        : new DataCube(cubeAxes, [.. v.Select(x => x.ToComplex())])), null);
            }
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
