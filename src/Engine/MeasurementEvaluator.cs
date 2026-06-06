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
    /// Evaluate all measurements and add result cubes to <paramref name="ds"/>.
    /// </summary>
    public void EvaluateInto(DataSet ds)
    {
        if (_tb.Measurements.Count == 0) return;

        var ctx   = new MeasurementContext(_analysisResults, _backSolvers);
        var eval  = new Evaluator(ctx);

        // Globals scope: inject resolved global variables so measurements can reference them
        var globalScope = new Scope("globals");
        foreach (var (name, value) in _netlist.ResolvedGlobals)
        {
            globalScope.Bind(name, value.ToString()!);     // dummy expression string
            eval.InjectResolved("globals", name, value);   // pre-memoize actual value
        }

        // Measurement scope: child of globals; used to inject computed measurement cubes
        var mScope = new Scope("measurements", globalScope);

        foreach (var m in _tb.Measurements)
        {
            Value result;
            try
            {
                result = eval.Eval(m.Expression, mScope, m.Unit);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Measurement '{m.Name}': failed to evaluate '{m.Expression}': {ex.Message}", ex);
            }

            // Inject the result so later measurements can reference this one by name
            mScope.Bind(m.Name, result.ToString()!);
            eval.InjectResolved("measurements", m.Name, result);

            // Add to DataSet
            DataCube cube = result.Kind switch
            {
                ValueKind.Cube    => result.AsCube(),
                ValueKind.Real    => DataCube.Scalar(result.AsReal()),
                ValueKind.Complex => DataCube.Scalar(result.AsComplex()),
                _ => throw new InvalidOperationException(
                    $"Measurement '{m.Name}' produced an unsupported value kind: {result.Kind}")
            };
            ds.Add(m.Name, cube);
        }
    }
}
