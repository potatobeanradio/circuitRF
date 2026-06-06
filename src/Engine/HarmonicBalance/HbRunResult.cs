using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Wrapper returned by HbEngine.Run() that carries both the DataSet and the linear
/// back-solver retained for lazy reconstruction of linear-interior node voltages and
/// branch currents (Correction 1).
///
/// DataSet is sealed in RfCore; this wrapper provides implicit conversion to DataSet
/// so existing callers (var ds = engine.Run(p)) continue to compile unchanged.
/// </summary>
public sealed class HbRunResult
{
    public DataSet             DataSet    { get; }
    public HbLinearBackSolver? BackSolver { get; }

    public HbRunResult(DataSet ds, HbLinearBackSolver? backSolver = null)
    {
        DataSet    = ds;
        BackSolver = backSolver;
    }

    /// <summary>Implicit conversion — existing <c>DataSet ds = engine.Run(p)</c> continues to work.</summary>
    public static implicit operator DataSet(HbRunResult r) => r.DataSet;

    /// <summary>Indexer delegation — <c>ds["V"]</c> on a HbRunResult resolves to the underlying DataSet.</summary>
    public DataCube this[string name] => DataSet[name];

    /// <summary>Containment check — forwards to the underlying DataSet.</summary>
    public bool Contains(string name) => DataSet.Contains(name);
}
