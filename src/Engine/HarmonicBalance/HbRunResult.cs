using System.Numerics;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Wrapper returned by HbEngine.Run() that carries the DataSet, the linear
/// back-solver retained for lazy reconstruction of linear-interior node voltages
/// and branch currents, and the linear-network payload for export.
///
/// DataSet is sealed in RfCore; this wrapper provides implicit conversion to DataSet
/// so existing callers (var ds = engine.Run(p)) continue to compile unchanged.
/// </summary>
public sealed class HbRunResult
{
    public DataSet                DataSet        { get; }
    public HbLinearBackSolver?    BackSolver     { get; }

    /// <summary>
    /// Linear-network payload for export (data-export.md §4, §7.2).
    /// Populated whenever BackSolver is non-null (§8.6: expose always; zero cost).
    /// Null for two-tone runs (HbLinearBackSolver is single-tone only).
    /// </summary>
    public ILinearNetworkPayload? LinearPayload  { get; }

    /// <summary>Whether the Newton solve converged (false ⇒ best-available result stored).</summary>
    public bool Converged { get; }

    /// <summary>
    /// The converged interface-node voltage spectrum <c>[N, K+1]</c> for a single-tone run — the seed
    /// used to warm-start the next point of a parametric sweep (continuation; see
    /// docs/design/harmonic-balance.md §11). Null for two-tone runs.
    /// </summary>
    public Complex[,]? InterfaceV { get; }

    public HbRunResult(DataSet ds, HbLinearBackSolver? backSolver = null,
        bool converged = true, Complex[,]? interfaceV = null)
    {
        DataSet       = ds;
        BackSolver    = backSolver;
        Converged     = converged;
        InterfaceV    = interfaceV;
        LinearPayload = backSolver is not null
            ? new HbLinearNetworkPayload(backSolver)
            : null;
    }

    /// <summary>Implicit conversion — existing <c>DataSet ds = engine.Run(p)</c> continues to work.</summary>
    public static implicit operator DataSet(HbRunResult r) => r.DataSet;

    /// <summary>Indexer delegation — <c>ds["V"]</c> on a HbRunResult resolves to the underlying DataSet.</summary>
    public DataCube this[string name] => DataSet[name];

    /// <summary>Containment check — forwards to the underlying DataSet.</summary>
    public bool Contains(string name) => DataSet.Contains(name);
}
