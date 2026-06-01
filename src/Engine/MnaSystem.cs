using System.Numerics;
using CircuitRF.Core;

namespace CircuitRF.Engine;

/// <summary>
/// Modified Nodal Analysis matrix accumulator (linear-engine §3).
/// Implements IMnaContext so ComponentModel.Stamp can contribute entries.
///
/// Matrix layout (0-based internal indices):
///   Rows/cols  0 .. nodeCount-1     : voltage unknowns, node k → index k-1
///              (ground = node 0 has no row or column)
///   Rows/cols  nodeCount .. nodeCount+branchCount-1 : branch-current unknowns
///
/// Sparse in intent; this v1 uses a Dictionary backing store to keep Step 1
/// test-inspection simple. Step 2 replaces the backing with CSparse triplets.
///
/// Fixed conventions (Engine CLAUDE.md):
///   - Branch current flows from the element's FIRST node to its SECOND.
///   - Current source J injects INTO its first node (out of its second).
///   - Ground (node 0) rows/cols are silently dropped.
/// </summary>
public sealed class MnaSystem : IMnaContext
{
    private readonly int _nodeCount;   // number of voltage unknowns (non-ground nodes)
    private int _branchCount;

    // Backing store: (row, col) → accumulated value.
    private readonly Dictionary<(int Row, int Col), Complex> _entries = [];
    // RHS vector keyed by row index.
    private readonly Dictionary<int, Complex> _rhs = [];

    /// <param name="nonGroundNodes">
    /// Number of non-ground nodes in the circuit.
    /// Nodes are expected to be 1-indexed (node 1 = internal index 0, etc.).
    /// </param>
    public MnaSystem(int nonGroundNodes) => _nodeCount = nonGroundNodes;

    public int NodeCount   => _nodeCount;
    public int BranchCount => _branchCount;
    public int Size        => _nodeCount + _branchCount;

    // ── IMnaContext ───────────────────────────────────────────────────────────

    public void AddAdmittance(int nodeA, int nodeB, Complex y)
    {
        int a = Col(nodeA);
        int b = Col(nodeB);
        if (a >= 0) Accum(a, a, +y);
        if (b >= 0) Accum(b, b, +y);
        if (a >= 0 && b >= 0) { Accum(a, b, -y); Accum(b, a, -y); }
    }

    public void AddBlockAdmittance(int rowNode, int colNode, Complex y)
    {
        int r = Col(rowNode);
        int c = Col(colNode);
        if (r >= 0 && c >= 0) Accum(r, c, y);
    }

    public int AddBranch() => _nodeCount + _branchCount++;

    public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo)
    {
        int bc   = branch;        // branch index IS the row/col already (returned by AddBranch)
        int from = Col(nodeFrom);
        int to   = Col(nodeTo);
        if (from >= 0) Accum(from, bc, +Complex.One);
        if (to   >= 0) Accum(to,   bc, -Complex.One);
    }

    public void AddConstraint(int branch, int node, Complex coeff)
    {
        int nc = Col(node);
        if (nc >= 0) Accum(branch, nc, coeff);
    }

    public void AddBranchConstraint(int branch, int otherBranch, Complex coeff)
        => Accum(branch, otherBranch, coeff);

    public void AddCurrentInjection(int node, Complex j)
    {
        int n = Col(node);
        if (n >= 0) AccumRhs(n, j);
    }

    public void AddSourceValue(int branch, Complex value)
        => AccumRhs(branch, value);

    // ── Inspection (used by tests and the Step 2 solver) ─────────────────────

    /// <summary>Return the accumulated matrix entry at (row, col), or zero.</summary>
    public Complex GetEntry(int row, int col)
        => _entries.TryGetValue((row, col), out var v) ? v : Complex.Zero;

    /// <summary>Return the RHS value at the given row, or zero.</summary>
    public Complex GetRhs(int row)
        => _rhs.TryGetValue(row, out var v) ? v : Complex.Zero;

    /// <summary>All non-zero (row, col, value) triplets in the matrix.</summary>
    public IEnumerable<(int Row, int Col, Complex Value)> NonZeroEntries()
        => _entries.Select(kv => (kv.Key.Row, kv.Key.Col, kv.Value));

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Map a circuit node index to a 0-based matrix column.
    // Node 0 (ground) → -1 (excluded). Node k → k-1.
    private static int Col(int node) => node == 0 ? -1 : node - 1;

    private void Accum(int row, int col, Complex value)
    {
        var key = (row, col);
        _entries[key] = _entries.TryGetValue(key, out var existing)
            ? existing + value
            : value;
    }

    private void AccumRhs(int row, Complex value)
        => _rhs[row] = _rhs.TryGetValue(row, out var existing) ? existing + value : value;
}
