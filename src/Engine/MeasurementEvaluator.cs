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

    public MeasurementEvaluator(
        TestBench                        tb,
        ElaboratedNetlist                netlist,
        IReadOnlyDictionary<string, DataSet>            analysisResults,
        IReadOnlyDictionary<string, ILinearBackSolver>? backSolvers = null)
    {
        _tb              = tb;
        _netlist         = netlist;
        _analysisResults = analysisResults;
        _backSolvers     = backSolvers;
    }

    /// <summary>
    /// Evaluate all measurements and add every result cube to <paramref name="ds"/>.
    /// Returns per-measurement error strings for any that failed; successful cubes are always emitted.
    /// </summary>
    public IReadOnlyList<string> EvaluateInto(DataSet ds)
        => Evaluate((m, result) => ds.Add(m.Name, ToCube(m, result)));

    // ── Shared evaluation core ───────────────────────────────────────────────────

    private IReadOnlyList<string> Evaluate(Action<Measurement, Value> emit)
    {
        var errors = new List<string>();
        if (_tb.Measurements.Count == 0) return errors;

        var ctx  = new MeasurementContext(_analysisResults, _backSolvers);
        var eval = new Evaluator(ctx);

        // Globals scope: inject resolved global variables so measurements can reference them.
        var globalScope = new Scope("globals");
        foreach (var (name, value) in _netlist.ResolvedGlobals)
        {
            globalScope.Bind(name, value.ToString()!);     // dummy expression string
            eval.InjectResolved("globals", name, value);   // pre-memoize actual value
        }

        // Swept variables: a parametric sweep prepends an axis named after its SweepVarName, carrying
        // the swept values. Inject each as a 1-D cube so a measurement that references the sweep
        // variable directly (e.g. "Pin_avail_dBm = Pin") gets one element per sweep point — and so it
        // broadcast-aligns (same axis name+values) with swept analysis cubes. This OVERRIDES the scalar
        // global injected above.
        var sweptVarNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in _tb.Analyses)
            if (a is ParametricSweepAnalysis ps && !string.IsNullOrEmpty(ps.SweepVarName))
                sweptVarNames.Add(ps.SweepVarName);

        if (sweptVarNames.Count > 0)
        {
            // Take the actual axis (name + values) from the results — authoritative even when a sweep
            // was disabled/collapsed (its axis simply won't be present, so it stays a scalar).
            var sweepAxes = new Dictionary<string, Axis>(StringComparer.Ordinal);
            foreach (var ds in _analysisResults.Values)
                foreach (var (_, cube) in ds.Cubes)
                    foreach (var ax in cube.Axes)
                        if (sweptVarNames.Contains(ax.Name) && !sweepAxes.ContainsKey(ax.Name))
                            sweepAxes[ax.Name] = ax;

            foreach (var (name, ax) in sweepAxes)
            {
                var sweepCube = new DataCube([new Axis(name, ax.Values)], (double[])ax.Values.Clone());
                globalScope.Bind(name, "0");                                  // ensure Lookup succeeds
                eval.InjectResolved("globals", name, new Value(sweepCube));   // override the scalar global
            }
        }

        // `freq` — the run's own frequency axis, as a 1-D cube. A measurement over an S-parameter
        // analysis has a natural frequency variable, and without this one a caller has no spelling
        // for it at all: the reserved `freq` keyword is injected per-stamping-frequency into a
        // component-value scope (Z_Port, an SDD equation) and never reached a measurement, so
        // "myCalc = 1/2/pi/freq" failed with an unresolved name. Injected as a cube (not a scalar)
        // for the same reason a swept variable is: it broadcast-aligns by axis name+values with the
        // analysis cubes it is used alongside, so "1/(2*pi*freq*C)" lands one element per frequency.
        string? freqHint = null;
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
                freqHint = "no analysis in this run produced a frequency axis "
                         + $"(available: [{string.Join(", ", _analysisResults.Keys)}])";
            }
            else
            {
                freqHint = $"this run's analyses use {grids.Count} different frequency grids, so "
                         + "'freq' is ambiguous — reference the analysis-qualified cube instead";
            }
        }

        // Measurement scope: child of globals; used to inject computed measurement cubes.
        var mScope = new Scope("measurements", globalScope);

        foreach (var m in _tb.Measurements)
        {
            Value result;
            try { result = eval.Eval(m.Expression, mScope, m.Unit); }
            catch (Exception ex)
            {
                // An unresolved `freq` is the one name whose absence has a reason worth reporting:
                // it IS available in most runs, so the bare name alone reads as "never supported".
                var why = ex is UnresolvedNameException { Name: FreqName } && freqHint is not null
                        ? $"{ex.Message} — {freqHint}"
                        : ex.Message;
                errors.Add($"Measurement '{m.Name}': failed to evaluate '{m.Expression}': {why}");
                continue;  // skip bind+emit; later measurements referencing this name report cascade error
            }

            // Inject the result so later measurements can reference this one by name.
            mScope.Bind(m.Name, result.ToString()!);
            eval.InjectResolved("measurements", m.Name, result);

            try { emit(m, result); }
            catch (Exception ex)
            {
                errors.Add($"Measurement '{m.Name}': failed to emit result: {ex.Message}");
            }
        }
        return errors;
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
