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
/// SPARSITY-PATTERN CACHE (SP-P2). The stamp SEQUENCE — not merely the pattern — is
/// invariant across frequency: <c>StampAll</c> visits the components in one fixed order,
/// each model issues the same <c>Accum</c> calls in the same order, and <c>AddBranch</c>
/// hands out the same indices. So the FIRST pass records the (row, col) sequence and
/// builds the CSC plus a slot map (call index → CSC value index); every later pass writes
/// straight into the CSC value array with no hashing and no rebuild.
///
/// The cached pass VERIFIES (row, col) at every call — two int compares. A model may
/// legitimately stamp a different sequence at a different ω (an ideal inductor skips its
/// diagonal at ω = 0; a regularization retry adds gmin stamps that were not recorded), so
/// on the first mismatch the pattern is invalidated, the pass finishes in recording mode,
/// and the pattern is rebuilt. A silently wrong pattern would put a value in the wrong cell
/// and produce a plausible, wrong answer; the verification is not optional.
///
/// Duplicate cells merge in CALL order, which is the order the previous dictionary-based
/// assembly summed them in — so the assembled matrix is bit-identical to the old path.
///
/// The AMD permutation is computed once from the topology (first Factorize call) and reused
/// across all subsequent frequencies; a rebuild whose sparsity structure actually differs
/// discards it.
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

    // ── Recording representation (pass 1, and any pass after an invalidation) ──
    // The stamp calls in call order. Kept as parallel lists so the recording pass does no
    // tuple hashing at all; the CSC build below merges duplicates.
    private readonly List<int>     _recRow = [];
    private readonly List<int>     _recCol = [];
    private readonly List<Complex> _recVal = [];
    private readonly Dictionary<int, Complex> _rhs = [];   // RHS while recording

    // ── Cached representation (valid once _slot is non-null) ──────────────────
    private int[]?     _patRow;          // recorded row sequence
    private int[]?     _patCol;          // recorded col sequence
    private int[]?     _slot;            // call index → index into _values
    private int        _patLen;          // number of recorded calls
    private int        _patBranchCount;  // branch count the pattern was recorded with
    private SparseMatrix? _csc;
    private Complex[]? _values;          // alias of _csc.Values (hot path)
    private Complex[]? _rhsArr;          // dense RHS, cleared per pass
    private int[]?     _zeroRowIdx;      // structural diagnostics, computed at pattern build
    private int[]?     _zeroColIdx;
    private (int[] ColPtr, int[] RowIdx)? _lastStructure;   // previous structure, for the AMD keep/discard test

    private bool _recording = true;      // this pass is accumulating into the lists
    private int  _k;                     // calls made so far in the current pass

    // Cached AMD permutation — computed once from the topology, reused across frequencies.
    private int[]? _amdPerm;

    /// <param name="nonGroundNodes">
    /// Number of non-ground circuit nodes (NodeMap.Count - 1).
    /// Nodes are 1-indexed; node k maps to internal index k-1.
    /// </param>
    public MnaSystem(int nonGroundNodes) => _nodeCount = nonGroundNodes;

    /// <summary>
    /// How many times the sparsity pattern has been built (SP-P2). A frequency sweep over a
    /// netlist whose stamp sequence is invariant builds it exactly ONCE, however many points it
    /// runs; a further build means some pass diverged from the recorded sequence and the cache
    /// paid a rebuild. Exposed so a test can assert the structural property rather than a time.
    /// </summary>
    public int PatternBuilds => _patternBuilds;
    private int _patternBuilds;

    public int NodeCount   => _nodeCount;
    public int BranchCount => _branchCount;
    public int Size        => _nodeCount + _branchCount;

    // ── Reset (call before stamping each new frequency) ───────────────────────

    /// <summary>Clear all accumulated values and reset the branch counter.
    /// The cached AMD permutation and sparsity pattern are preserved (topology does not change);
    /// with a valid pattern this is an <c>Array.Clear</c> of the value and RHS arrays.</summary>
    public void Reset()
    {
        _k           = 0;
        _branchCount = 0;

        if (_slot is not null)
        {
            Array.Clear(_values!);
            Array.Clear(_rhsArr!);
            _recording = false;
        }
        else
        {
            _recRow.Clear();
            _recCol.Clear();
            _recVal.Clear();
            _rhs.Clear();
            _recording = true;
        }
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
        // More branches than the pattern recorded ⇒ the sequence changed (fact 2): the RHS array
        // and the CSC are both sized for the recorded system, so invalidate before handing out
        // an index that would not fit.
        if (!_recording && _branchCount >= _patBranchCount) Invalidate();

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

    public void AddNodeBranchCoupling(int node, int branch, Complex coeff)
    {
        int n = Col(node);
        if (n >= 0) Accum(n, branch, coeff);   // branch is the absolute matrix column index
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
    /// The structural answer is computed once per sparsity pattern; only the naming is per call.
    /// </summary>
    public IReadOnlyList<(int Row, string Description)> FindZeroRows(
        Func<int, string>? nodeNamer   = null,
        Func<int, string>? branchNamer = null)
    {
        EnsurePattern();
        var result = new List<(int, string)>(_zeroRowIdx!.Length);
        foreach (int r in _zeroRowIdx!)
        {
            string desc = r < _nodeCount
                ? $"voltage node: {nodeNamer?.Invoke(r) ?? $"node#{r + 1}"}"
                : $"branch row: {branchNamer?.Invoke(r) ?? $"branch#{r - _nodeCount}"}";
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
        EnsurePattern();
        var result = new List<(int, string)>(_zeroColIdx!.Length);
        foreach (int c in _zeroColIdx!)
        {
            string desc = c < _nodeCount
                ? $"voltage node: {nodeNamer?.Invoke(c) ?? $"node#{c + 1}"}"
                : $"branch col: {branchNamer?.Invoke(c) ?? $"branch#{c - _nodeCount}"}";
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
        EnsurePattern();
        var cs = _csc!;
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

        // Pre-solve: report the structurally zero rows and columns found when the pattern was built.
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

    /// <summary>Build the dense RHS vector from accumulated source values.
    /// The caller owns the returned array (it is a copy of the internal one).</summary>
    public Complex[] BuildRhs()
    {
        EnsurePattern();
        return (Complex[])_rhsArr!.Clone();
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

    /// <summary>
    /// Allocation-free form of <see cref="BuildRhsWithPortDrive"/>: fills a caller-owned buffer
    /// of length <see cref="Size"/>. Used by the per-port loops, which run once per frequency
    /// per port and would otherwise allocate a fresh vector each time.
    /// </summary>
    public void FillRhsWithPortDrive(Complex[] buffer, int branchRow, Complex driveValue)
    {
        EnsurePattern();
        Array.Copy(_rhsArr!, buffer, _rhsArr!.Length);
        buffer[branchRow] = driveValue;
    }

    // ── Inspection (tests + Step 2 solver) ────────────────────────────────────

    public Complex GetEntry(int row, int col)
    {
        if (row < 0 || col < 0) return Complex.Zero;
        EnsurePattern();
        var cs = _csc!;
        if (row >= cs.RowCount || col >= cs.ColumnCount) return Complex.Zero;

        // Row indices are ascending within a column (the pattern build sorts them), so binary search.
        int lo = cs.ColumnPointers[col], hi = cs.ColumnPointers[col + 1] - 1;
        var ri = cs.RowIndices;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            int r   = ri[mid];
            if (r == row) return cs.Values[mid];
            if (r <  row) lo = mid + 1; else hi = mid - 1;
        }
        return Complex.Zero;
    }

    public Complex GetRhs(int row)
    {
        if (row < 0) return Complex.Zero;
        EnsurePattern();
        return row < _rhsArr!.Length ? _rhsArr[row] : Complex.Zero;
    }

    public IEnumerable<(int Row, int Col, Complex Value)> NonZeroEntries()
    {
        EnsurePattern();
        var cs = _csc!;
        for (int c = 0; c < cs.ColumnCount; c++)
            for (int p = cs.ColumnPointers[c]; p < cs.ColumnPointers[c + 1]; p++)
                yield return (cs.RowIndices[p], c, cs.Values[p]);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Map circuit node to 0-based matrix column. Ground (0) → -1 (excluded).
    private static int Col(int node) => node == 0 ? -1 : node - 1;

    private void Accum(int row, int col, Complex value)
    {
        if (!_recording)
        {
            int k = _k;
            if (k < _patLen && _patRow![k] == row && _patCol![k] == col)
            {
                _values![_slot![k]] += value;
                _k = k + 1;
                return;
            }
            Invalidate();   // sequence diverged — finish this pass in recording mode
        }

        _recRow.Add(row);
        _recCol.Add(col);
        _recVal.Add(value);
        _k++;
    }

    private void AccumRhs(int row, Complex value)
    {
        if (!_recording) { _rhsArr![row] += value; return; }
        _rhs[row] = _rhs.TryGetValue(row, out var existing) ? existing + value : value;
    }

    /// <summary>
    /// Return to recording mode, replaying the calls already made in this pass into the recording
    /// lists. Each cell's accumulated total is attributed to the FIRST call that reached it and the
    /// repeats get exact zero, so the rebuilt matrix is bit-identical to the interrupted one
    /// (adding 0 to a finite value is exact).
    /// </summary>
    private void Invalidate()
    {
        int k = _k;

        _recRow.Clear();
        _recCol.Clear();
        _recVal.Clear();

        if (k > 0)
        {
            var seen = new bool[_values!.Length];
            for (int i = 0; i < k; i++)
            {
                int s = _slot![i];
                _recRow.Add(_patRow![i]);
                _recCol.Add(_patCol![i]);
                if (seen[s]) _recVal.Add(Complex.Zero);
                else { seen[s] = true; _recVal.Add(_values[s]); }
            }
        }

        _rhs.Clear();
        if (_rhsArr is not null)
            for (int r = 0; r < _rhsArr.Length; r++)
                if (_rhsArr[r] != Complex.Zero) _rhs[r] = _rhsArr[r];

        _patRow = _patCol = _slot = null;
        _csc     = null;
        _values  = null;
        _rhsArr  = null;
        _zeroRowIdx = _zeroColIdx = null;
        _recording  = true;
        // _amdPerm is kept for now; BuildPattern discards it only if the structure actually changed.
    }

    /// <summary>
    /// Make the CSC representation current. A completed cached pass is already current; a recording
    /// pass (or a cached pass that stopped short of the recorded sequence) rebuilds the pattern.
    /// </summary>
    private void EnsurePattern()
    {
        if (!_recording)
        {
            // A pass that stopped SHORT — or allocated a different number of branches — is a
            // mismatch just as a diverging (row, col) is.
            if (_k == _patLen && _branchCount == _patBranchCount) return;
            Invalidate();
        }
        BuildPattern();
    }

    private void BuildPattern()
    {
        int n = Size;
        int m = _recRow.Count;

        var order = StableOrderByColThenRow(n, m);

        var colPtr = new int[n + 1];
        var rowIdx = new int[m];
        var values = new Complex[m];
        var slot   = new int[m];

        int nnz = 0, prevRow = -1, prevCol = -1;
        for (int j = 0; j < m; j++)
        {
            int i = order[j];
            int r = _recRow[i], c = _recCol[i];
            if (nnz == 0 || r != prevRow || c != prevCol)
            {
                rowIdx[nnz] = r;
                values[nnz] = Complex.Zero;
                nnz++;
                prevRow = r;
                prevCol = c;
                colPtr[c + 1]++;
            }
            int s = nnz - 1;
            slot[i]    = s;
            values[s] += _recVal[i];     // call order — matches the old dictionary's summation
        }
        for (int c = 0; c < n; c++) colPtr[c + 1] += colPtr[c];

        if (nnz != m)
        {
            Array.Resize(ref rowIdx, nnz);
            Array.Resize(ref values, nnz);
        }

        // Keep the AMD permutation only if the sparsity STRUCTURE is unchanged; a rebuild that
        // merely re-sequenced identical cells (the regularization retry is the common case) does
        // not need a fresh ordering, a genuinely different structure does.
        if (_amdPerm is not null && !SameStructure(colPtr, rowIdx)) _amdPerm = null;

        _csc    = new SparseMatrix(n, n, values, rowIdx, colPtr);
        _values = values;
        _slot   = slot;
        _patRow = [.. _recRow];
        _patCol = [.. _recCol];
        _patLen = m;
        _patBranchCount = _branchCount;

        _rhsArr = new Complex[n];
        foreach (var (row, val) in _rhs)
            _rhsArr[row] = val;

        ComputeStructuralZeros(n, colPtr, rowIdx, nnz);

        // The recording lists are rebuilt from the pattern if it is ever invalidated; drop their
        // contents (keeping capacity) so the same storage is not carried twice.
        _recRow.Clear();
        _recCol.Clear();
        _recVal.Clear();
        _rhs.Clear();

        _recording = false;
        _k         = m;
        _patternBuilds++;
    }

    private bool SameStructure(int[] colPtr, int[] rowIdx)
    {
        var old = _lastStructure;
        if (old is null) return true;   // nothing to compare against — keep whatever we have
        var (oldColPtr, oldRowIdx) = old.Value;
        if (oldColPtr.Length != colPtr.Length || oldRowIdx.Length != rowIdx.Length) return false;
        for (int i = 0; i < colPtr.Length; i++) if (oldColPtr[i] != colPtr[i]) return false;
        for (int i = 0; i < rowIdx.Length; i++) if (oldRowIdx[i] != rowIdx[i]) return false;
        return true;
    }

    private void ComputeStructuralZeros(int n, int[] colPtr, int[] rowIdx, int nnz)
    {
        _lastStructure = (colPtr, rowIdx);

        var rowSeen = new bool[n];
        for (int i = 0; i < nnz; i++) rowSeen[rowIdx[i]] = true;

        var zr = new List<int>();
        for (int r = 0; r < n; r++) if (!rowSeen[r]) zr.Add(r);
        var zc = new List<int>();
        for (int c = 0; c < n; c++) if (colPtr[c + 1] == colPtr[c]) zc.Add(c);

        _zeroRowIdx = [.. zr];
        _zeroColIdx = [.. zc];
    }

    /// <summary>
    /// Order the recorded calls by (column, row, call index). Two STABLE counting-sort passes —
    /// stability is what makes duplicate cells merge in call order, so the assembled values match
    /// the previous dictionary path bit for bit.
    /// </summary>
    private int[] StableOrderByColThenRow(int n, int m)
    {
        if (m == 0) return [];

        var cnt = new int[n + 1];
        for (int i = 0; i < m; i++) cnt[_recRow[i] + 1]++;
        for (int r = 0; r < n; r++) cnt[r + 1] += cnt[r];
        var byRow = new int[m];
        for (int i = 0; i < m; i++) byRow[cnt[_recRow[i]]++] = i;

        Array.Clear(cnt);
        for (int i = 0; i < m; i++) cnt[_recCol[i] + 1]++;
        for (int c = 0; c < n; c++) cnt[c + 1] += cnt[c];
        var order = new int[m];
        for (int j = 0; j < m; j++)
        {
            int i = byRow[j];
            order[cnt[_recCol[i]]++] = i;
        }
        return order;
    }

    /// <summary>
    /// True when the currently-assembled matrix is bit-identical — structure AND values — to
    /// <paramref name="other"/>, which is normally an earlier <see cref="BuildCsc"/> snapshot of
    /// this same system.
    ///
    /// <para><b>Why (HB-P2).</b> <see cref="HarmonicBalance.HbLinearExtractor"/> caches one LU per
    /// harmonic and reuses it across solves. What makes a cached LU stale is a VALUE change in the
    /// linear partition — a loadpull tuner's per-point impedance override, a re-configured
    /// termination — and the extractor has to re-stamp the matrix on every solve anyway (the
    /// right-hand side changes with the drive). So the honest validity test is simply "is the
    /// matrix I just stamped the one I factored?", asked here against the live CSC with no
    /// allocation and no cooperation from the caller. An invalidation protocol that callers must
    /// remember to invoke would answer the same question and be silently wrong when one forgot.</para>
    /// </summary>
    public bool MatchesCsc(CompressedColumnStorage<Complex>? other)
    {
        if (other is null) return false;
        EnsurePattern();
        var cs = _csc!;
        if (other.RowCount != cs.RowCount || other.ColumnCount != cs.ColumnCount) return false;

        var (ocp, orx, ova) = (other.ColumnPointers, other.RowIndices, other.Values);
        var (ncp, nrx, nva) = (cs.ColumnPointers, cs.RowIndices, cs.Values);
        if (ocp.Length != ncp.Length || orx.Length != nrx.Length || ova.Length != nva.Length)
            return false;

        for (int i = 0; i < ncp.Length; i++) if (ocp[i] != ncp[i]) return false;
        for (int i = 0; i < nrx.Length; i++) if (orx[i] != nrx[i]) return false;
        // Bit equality, not a tolerance: the question is whether this is the SAME matrix, and
        // Complex.Equals is exact. A NaN entry would compare unequal and force a refactorization,
        // which is the safe direction.
        for (int i = 0; i < nva.Length; i++) if (!ova[i].Equals(nva[i])) return false;
        return true;
    }

    /// <summary>
    /// Build and return the CSC representation of the current matrix.
    /// Used by <see cref="HarmonicBalance.HbLinearExtractor"/> to snapshot G(ω)
    /// before factorization so the sparse matrix can be exported without recomputing.
    /// Calling this after <see cref="Factorize"/> on the same MnaSystem instance is
    /// safe — both build from the same accumulated entries. The returned matrix is a COPY the
    /// caller owns: the internal one is zeroed and refilled by the next <see cref="Reset"/>.
    /// </summary>
    public CompressedColumnStorage<Complex> BuildCsc()
    {
        EnsurePattern();
        var c = _csc!;
        return new SparseMatrix(
            c.RowCount, c.ColumnCount,
            (Complex[])c.Values.Clone(),
            (int[])c.RowIndices.Clone(),
            (int[])c.ColumnPointers.Clone());
    }
}
