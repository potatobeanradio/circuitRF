using RfCore.Data;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// Holds the simulation results for each named analysis in a run.
/// Passed to <see cref="Evaluator"/> so that measurement expressions can resolve
/// qualified cube accessors like <c>HB1.V("n_drain", 1, All)</c>.
///
/// Optionally carries <see cref="ILinearBackSolver"/> instances (one per analysis)
/// so that <c>V(node)</c> accesses for linear-interior nodes can be back-solved lazily.
/// </summary>
public sealed class MeasurementContext
{
    private readonly IReadOnlyDictionary<string, DataSet>           _results;
    private readonly IReadOnlyDictionary<string, ILinearBackSolver>? _backSolvers;

    public MeasurementContext(
        IReadOnlyDictionary<string, DataSet>            results,
        IReadOnlyDictionary<string, ILinearBackSolver>? backSolvers = null)
    {
        _results     = results;
        _backSolvers = backSolvers;
    }

    public DataSet GetAnalysis(string name)
        => _results.TryGetValue(name, out var ds) ? ds
           : throw new KeyNotFoundException(
               $"No analysis named '{name}' in measurement context. Available: [{string.Join(", ", _results.Keys)}]");

    /// <summary>
    /// Try to get the linear back-solver for the named analysis.
    /// Returns false (and null solver) when no back-solver is available.
    /// </summary>
    public bool TryGetBackSolver(string analysisName, out ILinearBackSolver? solver)
    {
        if (_backSolvers is not null && _backSolvers.TryGetValue(analysisName, out solver))
            return true;
        solver = null;
        return false;
    }
}
