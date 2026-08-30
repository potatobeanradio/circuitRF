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
    /// Null for multi-tone runs (HbLinearBackSolver is single-tone only) — unlike
    /// <see cref="InterfaceV"/>, which every tone count now carries.
    /// </summary>
    public ILinearNetworkPayload? LinearPayload  { get; }

    /// <summary>Whether the Newton solve converged (false ⇒ best-available result stored).</summary>
    public bool Converged { get; }

    /// <summary>
    /// This point's convergence trace — one <c>StepRecord</c> per Newton solve the point ran, which is
    /// ONE for an ordinary point and one per rung when <c>DriveStepping</c>'s ramp fires, each rung
    /// labelled with its dB offset from the requested drive. Each step carries its own per-iteration
    /// residual, accepted λ and backtrack count.
    ///
    /// <para>Returned rather than only printed because the trace is the primary HB diagnostic
    /// (harmonic-balance.md §12) and every tone count now produces one; <c>RunSinglePoint</c> has
    /// always handed its iteration trace back for the same reason.</para>
    /// </summary>
    public HbConvergenceTrace? Trace { get; }

    /// <summary>
    /// The converged interface-node voltage spectrum — the seed used to warm-start the next point of
    /// a parametric sweep (continuation; see docs/design/harmonic-balance.md §11). <c>[N, K+1]</c>
    /// over the harmonic axis for a single-tone run, <c>[N, M]</c> over the mixing lattice for two or
    /// more tones (HB-P3 M3; it was single-tone only before that).
    /// </summary>
    public Complex[,]? InterfaceV { get; }

    public HbRunResult(DataSet ds, HbLinearBackSolver? backSolver = null,
        bool converged = true, Complex[,]? interfaceV = null, HbConvergenceTrace? trace = null)
    {
        DataSet       = ds;
        BackSolver    = backSolver;
        Converged     = converged;
        InterfaceV    = interfaceV;
        Trace         = trace;
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
