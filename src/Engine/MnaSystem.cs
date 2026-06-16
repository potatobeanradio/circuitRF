using System.Numerics;
using System.Text;
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
    /// Find all all-zero rows in the assembled matrix.
    /// A zero row means the corresponding unknown has no constraints and will cause singularity.
    /// <paramref name="nodeNamer"/> maps a 0-based voltage-node matrix index to a display name.
    /// <paramref name="branchNamer"/> maps a branch-row matrix index to a display name.
    /// </summary>
    public IReadOnlyList<(int Row, string Description)> FindZeroRows(
        Func<int, string>? nodeNamer   = null,
        Func<int, string>? branchNamer = null)
    {
        var nonZeroRows = new HashSet<int>(_entries.Count);
        foreach (var (row, _) in _entries.Keys)
            nonZeroRows.Add(row);

        var result = new List<(int, string)>();
        for (int r = 0; r < Size; r++)
        {
            if (nonZeroRows.Contains(r)) continue;
            string desc;
            if (r < _nodeCount)
                desc = $"voltage node: {nodeNamer?.Invoke(r) ?? $"node#{r + 1}"}";
            else
                desc = $"branch row: {branchNamer?.Invoke(r) ?? $"branch#{r - _nodeCount}"}";
            result.Add((r, desc));
        }
        return result;
    }

    /// <summary>
    /// Find all all-zero columns in the assembled matrix.
    /// A zero column means the corresponding unknown appears in no equation — a degree of freedom.
    /// <paramref name="nodeNamer"/> maps a 0-based voltage-node matrix index to a display name.
    /// <paramref name="branchNamer"/> maps a branch-column matrix index to a display name.
    /// </summary>
    public IReadOnlyList<(int Col, string Description)> FindZeroCols(
        Func<int, string>? nodeNamer   = null,
        Func<int, string>? branchNamer = null)
    {
        var nonZeroCols = new HashSet<int>(_entries.Count);
        foreach (var (_, col) in _entries.Keys)
            nonZeroCols.Add(col);

        var result = new List<(int, string)>();
        for (int c = 0; c < Size; c++)
        {
            if (nonZeroCols.Contains(c)) continue;
            string desc;
            if (c < _nodeCount)
                desc = $"voltage node: {nodeNamer?.Invoke(c) ?? $"node#{c + 1}"}";
            else
                desc = $"branch col: {branchNamer?.Invoke(c) ?? $"branch#{c - _nodeCount}"}";
            result.Add((c, desc));
        }
        return result;
    }

    /// <summary>
    /// Build the CSC matrix, compute the AMD permutation on the first call,
    /// and return an LU factorization. Reuse the permutation on subsequent calls.
    /// Throws <see cref="SingularMatrixException"/> with structural diagnostics if factorization fails.
    /// </summary>
    public SparseLU Factorize(
        double pivotTolerance          = 1.0,
        Func<int, string>? nodeNamer   = null,
        Func<int, string>? branchNamer = null)
    {
        var cs = BuildCscMatrix();
        if (_amdPerm is null)
        {
            try { _amdPerm = AMD.Generate(cs, ColumnOrdering.MinimumDegreeAtA); }
            catch (Exception ex)
            {
                // AMD fails on a structurally empty matrix (e.g., all admittances exactly cancel).
                throw new SingularMatrixException(
                    "AMD ordering failed — assembled matrix is empty. " +
                    "Check for exact conductance cancellation (active element canceling port or passive admittance).",
                    ex);
            }
        }

        // Pre-solve: find structurally zero rows and columns.
        var zeroRows = FindZeroRows(nodeNamer, branchNamer);
        var zeroCols = FindZeroCols(nodeNamer, branchNamer);
        if (zeroRows.Count > 0 || zeroCols.Count > 0)
        {
            var sb = new StringBuilder();
            if (zeroRows.Count > 0)
            {
                sb.AppendLine($"Singular MNA matrix: {zeroRows.Count} all-zero row(s) found before factorization.");
                foreach (var (_, desc) in zeroRows)
                    sb.AppendLine($"  zero row  • {desc}");
                sb.AppendLine("A zero row means that unknown has no constraints — check for floating nodes or malformed component stamps.");
            }
            if (zeroCols.Count > 0)
            {
                sb.AppendLine($"Singular MNA matrix: {zeroCols.Count} all-zero column(s) found before factorization.");
                foreach (var (_, desc) in zeroCols)
                    sb.AppendLine($"  zero col  • {desc}");
                sb.Append("A zero column means the unknown appears in no equation — check for isolated branch-current unknowns.");
            }
            throw new SingularMatrixException(sb.ToString());
        }

        // Try factorization.
        SparseLU? lu;
        Exception? factEx = null;
        try
        {
            lu = SparseLU.Create(cs, _amdPerm, pivotTolerance);
        }
        catch (Exception ex)
        {
            lu    = null;
            factEx = ex;
        }

        if (lu is null)
        {
            string reason = factEx is not null
                ? $"factorizer threw: {factEx.Message}"
                : "SparseLU.Create returned null (no pivot found)";
            throw new SingularMatrixException(
                $"Singular MNA matrix ({reason}). " +
                "No structurally zero rows or columns detected; likely cause: " +
                "numerically exact rank deficiency — e.g. a KVL Short loop (three or more Shorts forming a closed loop " +
                "where one constraint is a linear combination of the others), or an inductance sub-matrix with zero determinant " +
                "(highly coupled network). Check for Short-loop topology or verify all mutual inductances are physically realizable.",
                factEx);
        }

        return lu;
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

    /// <summary>
    /// Build and return the CSC representation of the current matrix.
    /// Used by <see cref="HarmonicBalance.HbLinearExtractor"/> to snapshot G(ω)
    /// before factorization so the sparse matrix can be exported without recomputing.
    /// Calling this after <see cref="Factorize"/> on the same MnaSystem instance is
    /// safe — both calls build from the same <c>_entries</c> dictionary.
    /// </summary>
    public CompressedColumnStorage<Complex> BuildCsc() => BuildCscMatrix();

    private CompressedColumnStorage<Complex> BuildCscMatrix()
    {
        int n   = Size;
        var tri = new CoordinateStorage<Complex>(n, n, _entries.Count);
        foreach (var ((row, col), val) in _entries)
            tri.At(row, col, val);
        return SparseMatrix.OfIndexed(tri);
    }
}
