// ================================================================
//  ImportedLinearNetwork.cs  —  Loaded __linnet_* payload from a .npy file
//
//  Holds the sparse MNA system (G, bSrc, iNl, index maps) loaded from
//  the __linnet_* fields of a circuitRF .npy file.  A Level-2 consumer
//  uses this to reconstruct any linear-interior node voltage or branch
//  current without re-running the HB sweep.
//
//  Level 2 is documented in docs/design/data-file-format.md §Level-2
//  and not yet implemented in C#.  Everything needed is here.
//
//  Sparse G stays sparse: GRows/GCols/GData are the COO triplets.
//  Never densify — see RfCore/src/Export/CLAUDE.md.
// ================================================================

using System.Numerics;

namespace RfCore.Export;

/// <summary>
/// Loaded linear-network payload from the <c>__linnet_*</c> fields of a circuitRF <c>.npy</c> file.
/// Contains the per-harmonic sparse MNA matrix G, the per-(sweep, harmonic) source RHS and NL
/// interface currents, plus the node/branch name maps — sufficient for a Level-2 consumer to
/// reconstruct any linear-interior node voltage or branch current.
/// See <c>docs/design/data-file-format.md</c> §Level-2 for the full reconstruction recipe.
/// </summary>
public sealed class ImportedLinearNetwork
{
    // ── Dimensions ──────────────────────────────────────────────────────────

    /// <summary>Full MNA matrix size = NonGroundCount + BranchCount.</summary>
    public required long MnaSize { get; init; }

    /// <summary>
    /// Number of non-ground circuit nodes (voltage unknowns).
    /// MNA rows/cols 0..NonGroundCount-1 are node-voltage unknowns.
    /// Circuit node n (1-based) → MNA row n-1.
    /// </summary>
    public required long NonGroundCount { get; init; }

    // ── Frequency mapping ────────────────────────────────────────────────────

    /// <summary>
    /// Angular frequencies ω_k = k·2π·f₀.  Length = K+1.
    /// ω_0 = 0 (DC harmonic); ω_1 is the fundamental.
    /// </summary>
    public required double[] Omegas { get; init; }

    // ── Sparse G — COO triplets, topology-invariant pattern ──────────────────

    /// <summary>
    /// COO row indices (0-based).  Length = nnz.
    /// The sparsity pattern (GRows, GCols) is identical for all harmonics.
    /// </summary>
    public required int[] GRows { get; init; }

    /// <summary>
    /// COO column indices (0-based).  Length = nnz.
    /// </summary>
    public required int[] GCols { get; init; }

    /// <summary>
    /// Complex G matrix entries per harmonic.  Shape [K+1, nnz], row-major.
    /// GData[k, nz] = G(ω_k) at the nonzero position (GRows[nz], GCols[nz]).
    /// To build a sparse matrix for harmonic k (Python):
    ///   G_k = scipy.sparse.csc_matrix((GData[k], (GRows, GCols)), shape=(MnaSize, MnaSize))
    /// </summary>
    public required Complex[,] GData { get; init; }

    // ── Per-(sweep, harmonic) RHS data ───────────────────────────────────────

    /// <summary>
    /// Source RHS vector.  Shape [S, K+1, MnaSize], row-major.
    /// BSrc[si, k, mnaIdx] = source contribution at sweep point si, harmonic k, MNA row mnaIdx.
    /// This is the full-MNA RHS <em>before</em> subtracting NL interface currents.
    /// </summary>
    public required Complex[,,] BSrc { get; init; }

    /// <summary>
    /// Nonlinear interface currents.  Shape [S, K+1, NInterface], row-major.
    /// INl[si, k, n] = NL current at interface node n, harmonic k, sweep point si.
    /// Subtract from BSrc at the interface-node row to form the full RHS before solving:
    ///   b[InterfaceNodes[n]-1] -= INl[si, k, n]
    /// </summary>
    public required Complex[,,] INl { get; init; }

    // ── Interface-node list ──────────────────────────────────────────────────

    /// <summary>
    /// Circuit node indices (1-based, non-ground) at the NL–linear interface.
    /// Length = NInterface.
    /// To get the 0-based MNA row for interface node n: InterfaceNodes[n] - 1.
    /// </summary>
    public required int[] InterfaceNodes { get; init; }

    // ── Index↔name maps ──────────────────────────────────────────────────────

    /// <summary>
    /// MNA index i (0-based) → name of circuit node (1-based node i+1).
    /// Length = NonGroundCount.
    /// Example: NodeNames[0] = "n_gate" means circuit node 1 is n_gate (MNA row 0).
    /// </summary>
    public required string[] NodeNames { get; init; }

    /// <summary>
    /// Branch-index b (0-based offset from NonGroundCount) → human-readable name.
    /// MNA index of branch b = NonGroundCount + b.
    /// Format: "L:path", "V:path", "IProbe:path", "Tuner:path:choke", "Tuner:path:bias".
    /// Length = MnaSize - NonGroundCount.
    /// </summary>
    public required string[] BranchNames { get; init; }
}
