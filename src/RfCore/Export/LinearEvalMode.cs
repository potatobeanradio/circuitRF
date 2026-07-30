// ================================================================
//  LinearEvalMode.cs  —  Controls eager evaluation of linear-interior V/I
//
//  When IncludeLinearNetwork = true, this enum selects whether the exporter
//  should also evaluate and store linear-interior node voltages and branch
//  currents (V_linear, I_linear cubes) beyond the interface quantities.
//
//  See docs/design/data-export.md §5.
// ================================================================

namespace RfCore.Export;

/// <summary>
/// Controls whether the exporter eagerly evaluates linear-interior node
/// voltages and branch currents into <c>V_linear</c> / <c>I_linear</c>
/// cubes alongside the primary interface data.
///
/// Only meaningful when <see cref="ExportOptions.IncludeLinearNetwork"/> is true
/// and a non-null <see cref="ILinearNetworkPayload"/> is supplied.
/// </summary>
public enum LinearEvalMode
{
    /// <summary>
    /// Do not evaluate linear-interior quantities.  Export only the raw
    /// G/bSrc/iNl tensors.  Consumers reconstruct V and I themselves.
    /// Smallest disk footprint; zero additional back-solve cost at export time.
    /// </summary>
    EvaluateNone = 0,

    /// <summary>
    /// Evaluate <em>all</em> linear-interior node voltages and branch currents
    /// for every harmonic and sweep point.  Produces <c>V_linear</c> and
    /// <c>I_linear</c> cubes covering the full MNA unknown vector.
    /// Largest disk footprint; one LU back-solve per (harmonic, sweep point).
    /// </summary>
    EvaluateAll = 1,

    /// <summary>
    /// Evaluate only the nodes and branches named in
    /// <see cref="ExportOptions.EvalNodeNames"/> and
    /// <see cref="ExportOptions.EvalBranchRefs"/>.
    /// One back-solve per (harmonic, sweep point) but only selected rows
    /// of the solution vector are written.
    /// </summary>
    EvaluateSpecified = 2,
}
