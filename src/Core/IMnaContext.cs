using System.Numerics;

namespace CircuitRF.Core;

/// <summary>
/// The stamping API the engine exposes to ComponentModel.Stamp.
/// The engine owns the matrix; models contribute stamps through this interface.
/// Conventions are fixed in src/Engine/CLAUDE.md:
///   - Branch current flows from the element's FIRST node to its SECOND.
///   - Current source J injects into its FIRST node (and out of its second).
///   - Ground is node 0; node 0 rows/cols are excluded from the matrix (entries silently dropped).
/// </summary>
public interface IMnaContext
{
    // Group 1: accumulate admittance y between two nodes.
    void AddAdmittance(int nodeA, int nodeB, Complex y);

    // Group 1: accumulate one entry of an N-port admittance block.
    void AddBlockAdmittance(int rowNode, int colNode, Complex y);

    // Group 2: allocate a branch-current unknown; returns its 0-based branch index.
    int AddBranch();

    // Group 2: KCL coupling — branch current i flows nodeFrom → nodeTo.
    // Adds +1 at (nodeFrom row, branch col) and -1 at (nodeTo row, branch col).
    void AddBranchCurrent(int branch, int nodeFrom, int nodeTo);

    // Group 2: add coeff at (branch row, node col) in the constraint block.
    void AddConstraint(int branch, int node, Complex coeff);

    // Group 2: add coeff at (branch row, otherBranch col) — the off-diagonal D block.
    void AddBranchConstraint(int branch, int otherBranch, Complex coeff);

    // RHS: current injection at a node (positive = current injected into the node).
    void AddCurrentInjection(int node, Complex j);

    // RHS: voltage or value for the branch constraint row.
    void AddSourceValue(int branch, Complex value);
}
