using System.Numerics;
using CircuitRF.Core;
using CSparse;
using CSparse.Complex;
using CSparse.Complex.Factorization;
using CSparse.Ordering;
using CSparse.Storage;

namespace CircuitRF.Engine;

/// <summary>
/// Modified Nodal Analysis matrix accumulator (linear-engine §3).
/// Implements IMnaContext so ComponentModel.Stamp can contribute entries.
///
/// Matrix layout (0-based internal indices):
///   Rows/cols  0 .. nodeCount-1     : voltage unknowns, node k → index k-1
///              (ground = node 0 has no row or column)
///   Rows/cols  nodeCount .. Size-1  : branch-current unknowns
///
/// Usage per frequency:
///   1. Reset()           — clear accumulated values, reset branch counter
///   2. Component stamps  — fill entries via IMnaContext methods
///   3. Factorize()       — build CSC, compute AMD perm (first call), LU-factorize
///   4. lu.Solve(b, x)    — back-substitute for each RHS
///
/// The AMD permutation is computed once from the topology (first Factorize call)
/// and reused across all subsequent frequencies.
///
/// Fixed conventions (Engine CLAUDE.md):
///   - Branch current flows from element's FIRST node to its SECOND.
///   - Current source J injects INTO its first node.
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

    // Cached AMD permutation — computed once from the topology, reused across frequencies.
    private int[]? _amdPerm;

    /// <param name="nonGroundNodes">
    /// Number of non-ground circuit nodes (NodeMap.Count - 1).
    /// Nodes are 1-indexed; node k maps to internal index k-1.
    /// </param>
    public MnaSystem(int nonGroundNodes) => _nodeCount = nonGroundNodes;

    public int NodeCount   => _nodeCount;
    public int BranchCount => _branchCount;
    public int Size        => _nodeCount + _branchCount;

    // ── Reset (call before stamping each new frequency) ───────────────────────

    /// <summary>Clear all accumulated values and reset the branch counter.
    /// The cached AMD permutation is preserved (topology does not change).</summary>
    public void Reset()
    {
        _entries.Clear();
        _rhs.Clear();
        _branchCount = 0;
    }

    // ── IMnaContext ───────────────────────────────────────────────────────────

    public void AddAdmittance(int nodeA, int nodeB, Complex y)
    {
        int a = Col(nodeA), b = Col(nodeB);
        if (a >= 0) Accum(a, a, +y);
        if (b >= 0) Accum(b, b, +y);
        if (a >= 0 && b >= 0) { Accum(a, b, -y); Accum(b, a, -y); }
    }

    public void AddBlockAdmittance(int rowNode, int colNode, Complex y)
    {
        int r = Col(rowNode), c = Col(colNode);
        if (r >= 0 && c >= 0) Accum(r, c, y);
    }

    public int AddBranch()
    {
        int idx = _nodeCount + _branchCount++;
        return idx;
    }

    public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo)
    {
        int from = Col(nodeFrom), to = Col(nodeTo);
        if (from >= 0) Accum(from, branch, +Complex.One);
        if (to   >= 0) Accum(to,   branch, -Complex.One);
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

    // ── Solve support ─────────────────────────────────────────────────────────

    /// <summary>
    /// Build the CSC matrix, compute the AMD permutation on the first call,
    /// and return an LU factorization. Reuse the permutation on subsequent calls.
    /// </summary>
    public SparseLU Factorize(double pivotTolerance = 1.0)
    {
        var cs = BuildCscMatrix();
        _amdPerm ??= AMD.Generate(cs, ColumnOrdering.MinimumDegreeAtA);
        return SparseLU.Create(cs, _amdPerm, pivotTolerance);
    }

    /// <summary>Build the dense RHS vector from accumulated source values.</summary>
    public Complex[] BuildRhs()
    {
        var b = new Complex[Size];
        foreach (var (row, val) in _rhs)
            b[row] = val;
        return b;
    }

    /// <summary>
    /// Build the RHS with the source value at <paramref name="branchRow"/> overridden to
    /// <paramref name="driveValue"/>. Used for port excitation in S-parameter extraction.
    /// </summary>
    public Complex[] BuildRhsWithPortDrive(int branchRow, Complex driveValue)
    {
        var b = BuildRhs();
        b[branchRow] = driveValue;
        return b;
    }

    // ── Inspection (tests + Step 2 solver) ────────────────────────────────────

    public Complex GetEntry(int row, int col)
        => _entries.TryGetValue((row, col), out var v) ? v : Complex.Zero;

    public Complex GetRhs(int row)
        => _rhs.TryGetValue(row, out var v) ? v : Complex.Zero;

    public IEnumerable<(int Row, int Col, Complex Value)> NonZeroEntries()
        => _entries.Select(kv => (kv.Key.Row, kv.Key.Col, kv.Value));

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Map circuit node to 0-based matrix column. Ground (0) → -1 (excluded).
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

    private CompressedColumnStorage<Complex> BuildCscMatrix()
    {
        int n   = Size;
        var tri = new CoordinateStorage<Complex>(n, n, _entries.Count);
        foreach (var ((row, col), val) in _entries)
            tri.At(row, col, val);
        return SparseMatrix.OfIndexed(tri);
    }
}
