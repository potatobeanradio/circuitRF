namespace CircuitRF.WBond;

/// <summary>How a selection moved, which decides what the incremental fill may skip (R-wb-10).</summary>
public enum SelectionMotion
{
    /// <summary>Arbitrary reshaping — nothing within the selection is invariant.</summary>
    General,

    /// <summary>
    /// A rigid translation with a z component. Direct mutuals <b>within</b> the selection are
    /// unchanged, because the relative geometry is; the image mutuals are not, because the images
    /// move the other way in z.
    /// </summary>
    RigidTranslation,

    /// <summary>
    /// A rigid translation in x and y only. Both the direct <b>and</b> the image mutuals within the
    /// selection are unchanged, because the ground-plane images translate rigidly with it.
    /// </summary>
    HorizontalRigidTranslation,
}

/// <summary>
/// The drag path: keeps <b>L</b> and its Cholesky factor current as wires move, without refilling or
/// refactorising (R-wb-9, R-wb-10).
///
/// <h3>Why this is rank 2, not rank N</h3>
/// <para>Moving wire <i>k</i> changes exactly row <i>k</i> and column <i>k</i>. With
/// Δ the change to that row and v = Δ − (Δ_kk/2)·e_k:</para>
/// <code>
/// ΔL = e_k vᵀ + v e_kᵀ = ½[ (e_k+v)(e_k+v)ᵀ − (e_k−v)(e_k−v)ᵀ ]
/// </code>
/// <para>— a <b>rank-2</b> change however large N is, applied as one Cholesky update and one
/// downdate. The <c>Δ_kk/2</c> correction is what stops the diagonal being counted twice.</para>
///
/// <h3>What actually costs anything</h3>
/// <para>Measured at N = 600: the block recompute is ~2.4 ms and the rank-2 factor update ~0.3 ms.
/// <b>The fill dominates, not the solve</b> (WB13) — which is why the invariance rules above exist
/// and why there is no effort here spent on the linear algebra.</para>
/// </summary>
public sealed class IncrementalFill
{
    private readonly WireMesh _mesh;
    private readonly InductanceMatrix _l;
    private CholeskyFactor _factor;
    private readonly double[] _scratchOld;
    private readonly double[] _scratchNew;

    private IncrementalFill(WireMesh mesh, InductanceMatrix l, CholeskyFactor factor)
    {
        _mesh = mesh;
        _l = l;
        _factor = factor;
        _scratchOld = new double[l.Order];
        _scratchNew = new double[l.Order];
    }

    /// <summary>The live wire-basis inductance matrix.</summary>
    public InductanceMatrix Matrix => _l;

    /// <summary>The live Cholesky factor of <see cref="Matrix"/>.</summary>
    public CholeskyFactor Factor => _factor;

    public int WireCount => _l.Order;

    /// <summary>Number of wire-pair blocks recomputed by the last move — the cost that actually matters.</summary>
    public int LastBlocksRecomputed { get; private set; }

    /// <summary>Number of blocks skipped by rigid-motion invariance in the last move.</summary>
    public int LastBlocksSkipped { get; private set; }

    /// <summary>Cold start: one full fill and one factorisation.</summary>
    public static IncrementalFill Create(WireMesh mesh, bool parallel = true)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var l = InductanceMatrix.Fill(mesh, parallel);
        var factor = CholeskyFactor.Factor(l.Values, l.Order);
        return new IncrementalFill(mesh, l, factor);
    }

    /// <summary>
    /// Applies a move of one wire. The caller has already updated the mesh geometry; this refreshes
    /// the matrix row/column and the factor.
    /// </summary>
    public void MoveWire(int wire) => MoveWires([wire], SelectionMotion.General);

    /// <summary>
    /// Applies a move of several wires, exploiting whatever <paramref name="motion"/ > permits.
    /// </summary>
    /// <param name="movedWires">Indices of the wires whose geometry changed, in the mesh's numbering.</param>
    /// <param name="motion">
    /// What kind of motion it was. <see cref="SelectionMotion.General"/> is always safe; the other
    /// two are optimisations and are <b>only</b> correct if the selection really did move rigidly.
    /// </param>
    public void MoveWires(IReadOnlyList<int> movedWires, SelectionMotion motion) =>
        MoveWiresCore(movedWires, motion, maintainFactor: true);

    /// <summary>
    /// The same move, <b>leaving the factor alone</b> — the mesh and the matrix are brought exactly up
    /// to date and <see cref="FactorIsStale"/> goes true.
    ///
    /// <h3>What this is for</h3>
    /// <para>A drag that passes one wire across another makes <b>L</b> singular for the frames the two
    /// coincide, and no Cholesky factor exists for those frames — but <b>L itself is perfectly well
    /// defined throughout</b>. Keeping the matrix exact while the factor is unavailable is what lets
    /// the readout come back <i>during the same drag</i>, the moment the wires separate: the caller
    /// asks <see cref="TryRefactor"/> each frame and one succeeds. Without this the matrix went stale
    /// at the overlap and the panel stayed frozen until the button came up (owner, 2026-08-19:
    /// <i>"the Array Inductance panel stops updating (even when wires are moved off of the other
    /// wires during the same drag)"</i>).</para>
    ///
    /// <para>Cheap for the same reason the ordinary move is: it is the same row refresh, minus the
    /// rank-2 update. What it does NOT do is maintain the factor incrementally, so recovery costs one
    /// fresh factorisation rather than a rank-2 step — <b>O(N³/3), ~23 ms at N = 600</b>, paid once on
    /// the frame the geometry becomes evaluable again and never while it is not.</para>
    /// </summary>
    public void MoveWiresUnfactored(IReadOnlyList<int> movedWires, SelectionMotion motion) =>
        MoveWiresCore(movedWires, motion, maintainFactor: false);

    /// <summary>
    /// True when the matrix has moved on and the factor has not — see
    /// <see cref="MoveWiresUnfactored"/>. Anything that reads the factor must clear it first.
    /// </summary>
    public bool FactorIsStale { get; private set; }

    /// <summary>
    /// Refactorises from the current matrix. Returns false — rather than throwing — when the geometry
    /// is still degenerate, because the caller asking is a drag frame for which that is an ordinary
    /// answer and not an error.
    /// </summary>
    public bool TryRefactor()
    {
        try
        {
            _factor = CholeskyFactor.Factor(_l.Values, _l.Order);
            FactorIsStale = false;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void MoveWiresCore(IReadOnlyList<int> movedWires, SelectionMotion motion, bool maintainFactor)
    {
        ArgumentNullException.ThrowIfNull(movedWires);
        if (movedWires.Count == 0) return;

        int n = _l.Order;
        var isMoved = new bool[n];
        foreach (int w in movedWires)
        {
            if (w < 0 || w >= n)
                throw new ArgumentOutOfRangeException(nameof(movedWires), w, $"Wire index outside 0..{n - 1}.");
            isMoved[w] = true;
        }

        // Re-flatten the moved wires FIRST. The mesh is a snapshot of the polylines, so mutating a
        // Wire does not update it — doing this here rather than expecting the caller to is the
        // difference between a correct drag and a silently stale one.
        foreach (int w in movedWires)
            _mesh.RefreshWire(w);

        LastBlocksRecomputed = 0;
        LastBlocksSkipped = 0;

        // The factor is only worth updating incrementally for small selections; past that the 2k
        // rank-1 steps cost more than one factorisation. Measured crossover is around k ~ N/12 at
        // N = 600 (2k * O(N^2) against O(N^3/3)); the constant is deliberately conservative because
        // an unnecessary refactorisation is merely slow, while a drifted factor is silently wrong.
        bool incrementalFactor = maintainFactor && movedWires.Count * 12 <= n;
        if (!maintainFactor) FactorIsStale = true;

        foreach (int i in movedWires)
        {
            if (incrementalFactor)
                for (int j = 0; j < n; j++) _scratchOld[j] = _l[i, j];

            for (int j = 0; j < n; j++)
            {
                // Both moved and j < i: the pair was already refreshed when the outer loop was at j.
                if (isMoved[j] && j < i) continue;

                // The ONLY case that skips work: a horizontal rigid translation moves the selection
                // and its ground-plane images together, so every intra-selection block — direct and
                // image alike — is unchanged.
                //
                // SelectionMotion.RigidTranslation deliberately does NOT skip. Its direct half is
                // invariant in principle, but recovering it needs it cached from before the move and
                // the old geometry is gone; recomputing the whole block in one pass is cheaper than
                // two passes over the same filament pairs. The enum member stays because the
                // distinction is real and WB-C can collect the saving by caching the direct half per
                // selection — claiming it here would cost time rather than save it.
                if (isMoved[j] && motion == SelectionMotion.HorizontalRigidTranslation)
                {
                    LastBlocksSkipped++;
                    continue;
                }

                _l.Set(i, j, InductanceMatrix.Block(_mesh, i, j));
                LastBlocksRecomputed++;
            }

            if (incrementalFactor)
            {
                for (int j = 0; j < n; j++) _scratchNew[j] = _l[i, j];
                ApplyRankTwo(i, _scratchOld, _scratchNew);
            }
        }

        if (!incrementalFactor && maintainFactor)
        {
            _factor = CholeskyFactor.Factor(_l.Values, n);
            FactorIsStale = false;
        }
    }

    /// <summary>
    /// Applies the rank-2 change from replacing row/column <paramref name="k"/>.
    ///
    /// <para>The update is applied before the downdate on purpose: the update increases definiteness,
    /// so the intermediate matrix stays comfortably positive definite. The reverse order can hit an
    /// indefinite intermediate for a perfectly valid final matrix.</para>
    /// </summary>
    private void ApplyRankTwo(int k, double[] oldRow, double[] newRow)
    {
        int n = _l.Order;
        var v = new double[n];
        for (int j = 0; j < n; j++) v[j] = newRow[j] - oldRow[j];

        // Halve the diagonal term so e_k v^T + v e_k^T does not count L[k,k] twice.
        v[k] *= 0.5;

        double invSqrt2 = 1.0 / Math.Sqrt(2.0);
        var plus = new double[n];
        var minus = new double[n];
        for (int j = 0; j < n; j++)
        {
            double e = j == k ? 1.0 : 0.0;
            plus[j] = (e + v[j]) * invSqrt2;
            minus[j] = (e - v[j]) * invSqrt2;
        }

        _factor.RankOneUpdate(plus);
        _factor.RankOneUpdate(minus, downdate: true);
    }

    /// <summary>
    /// Reduces the current matrix onto the array basis — the panel's live readout.
    ///
    /// <para>Passes the <b>maintained</b> factor, so this costs M triangular solves (~2.5 ms at
    /// N = 600, M = 12) rather than a fresh factorisation (~22.7 ms). Reducing without it turns a
    /// ~5 ms drag frame into a ~25 ms one, which is the whole difference between 60 fps and 40.</para>
    /// </summary>
    public ArrayReduction Reduce()
    {
        // Reducing against a factor the matrix has moved past would be SILENTLY wrong, which is worse
        // than the throw a genuinely singular matrix earns. See MoveWiresUnfactored.
        if (FactorIsStale)
        {
            _factor = CholeskyFactor.Factor(_l.Values, _l.Order);
            FactorIsStale = false;
        }

        return ArrayReduction.Reduce(_l, _factor, _mesh.ArrayOfWire, _mesh.ArrayCount, _mesh.ArrayNames);
    }
}
