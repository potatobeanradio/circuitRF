// ================================================================
//  ILinearNetworkPayload.cs  —  Bridge interface for HB linear-network export
//
//  Defined in RfCore so DataSetExporter (which lives in RfCore) can consume
//  the linear-network data without depending on CircuitRF.Engine types.
//
//  The implementing class (HbLinearNetworkPayload in CircuitRF.Engine) wraps
//  the retained HbLinearBackSolver and HbLinearExtractor.
//
//  See docs/design/data-export.md §4 and §7.2 for the design rationale.
// ================================================================

using System.Numerics;

namespace RfCore.Export;

/// <summary>
/// Read-only view of the linear-network data retained by the HB back-solver.
/// Passed to <see cref="DataSetExporter"/> when <see cref="ExportOptions.IncludeLinearNetwork"/>
/// is true, giving the exporter everything it needs to serialize the per-harmonic
/// linear MNA system — enough for a consumer to reconstruct any linear-interior
/// node voltage or branch current without rerunning the HB sweep.
///
/// Index conventions (match data-export.md §4):
///   k     — harmonic index, 0 = DC, 1 = fundamental, …, K = K-th harmonic
///   si    — sweep-point index, 0 to SweepCount-1
///   mnaIdx — 0-based MNA matrix index (0 to MnaSize-1)
///   n     — interface-node index, 0 to InterfaceCount-1
/// </summary>
public interface ILinearNetworkPayload
{
    // ── Dimensions ──────────────────────────────────────────────────────────

    /// <summary>Number of sweep points (outer axis length).</summary>
    int SweepCount { get; }

    /// <summary>Number of harmonics including DC: K+1 (harmonic indices 0 to K).</summary>
    int HarmonicCount { get; }

    /// <summary>Full MNA matrix size = NonGroundCount + BranchCount.</summary>
    int MnaSize { get; }

    /// <summary>Number of non-ground circuit nodes (voltage unknowns).</summary>
    int NonGroundCount { get; }

    /// <summary>Number of nonlinear-interface nodes.</summary>
    int InterfaceCount { get; }

    // ── Frequency mapping ────────────────────────────────────────────────────

    /// <summary>
    /// Angular frequencies for each harmonic: ω_k = k·2π·f0; ω_0 = 0 (DC).
    /// Length = HarmonicCount.
    /// </summary>
    double[] Omegas { get; }

    // ── Index↔name maps ──────────────────────────────────────────────────────

    /// <summary>
    /// Interface-node circuit indices (1-based, non-ground).
    /// Length = InterfaceCount.
    /// </summary>
    int[] InterfaceNodes { get; }

    /// <summary>
    /// MNA index i (0-based) → name of circuit node (1-based node i+1).
    /// Length = NonGroundCount.
    /// </summary>
    string[] NodeNames { get; }

    /// <summary>
    /// Branch-index b (0-based branch offset from NonGroundCount) → human-readable name,
    /// format "L:path", "V:path", "IProbe:path", "Tuner:path:choke", "Tuner:path:bias",
    /// or "branch#N" for unrecognised types (SnpModel Z-expansion, etc.).
    /// Length = MnaSize - NonGroundCount.
    /// </summary>
    string[] BranchNames { get; }

    // ── Sparse G matrix ──────────────────────────────────────────────────────

    /// <summary>
    /// Sparse G(ω_k) in COO (triplet) form.
    /// Rows, Cols are 0-based; Data holds the complex matrix entries.
    ///
    /// The sparsity pattern (Rows, Cols) is topology-invariant: identical for all k.
    /// Only Data differs per harmonic.
    ///
    /// For k=0 (DC): the matrix is the zeroDrive linear MNA at ω=0 (including gmin),
    /// which is the SAME G used by the back-solver for GetSolution(0, si).
    /// Any inductance regularization applied during HB Newton's ExtractDC() is NOT
    /// part of this matrix — the back-solver's DC solve uses gmin only.
    /// </summary>
    (int[] Rows, int[] Cols, Complex[] Data) GetSparseG(int k);

    // ── Per-(harmonic, sweep) data ────────────────────────────────────────────

    /// <summary>
    /// Source RHS vector element at [sweepIdx, k, mnaIdx].
    /// bSrc is the full-MNA RHS snapshotted during the sweep loop with all sources
    /// active, before NL currents are subtracted.
    /// </summary>
    Complex GetBSrc(int sweepIdx, int k, int mnaIdx);

    /// <summary>
    /// Nonlinear interface current at [sweepIdx, interfaceNodeIdx, k].
    /// iNl[si][n,k] = current flowing FROM interface node n INTO the nonlinear device
    /// at harmonic k (passive-sign convention; see HarmonicBalance/CLAUDE.md).
    /// </summary>
    Complex GetINl(int sweepIdx, int interfaceNodeIdx, int k);
}
