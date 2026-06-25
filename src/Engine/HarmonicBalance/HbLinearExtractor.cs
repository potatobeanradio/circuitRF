using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using System.Text;
using CSparse.Complex.Factorization;
using CSparse.Storage;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Extracts the linear-partition interface data at each harmonic (linear-engine §10, §2.1).
///
/// At harmonic k (frequency kω₀), this engine provides two things:
///   Y_{N×N}(kω₀) — admittance seen by nonlinear devices at the interface nodes.
///   I_src         — Norton source excitation: I_src = -Y_{N×N} · V_oc
///                   where V_oc = open-circuit voltage (sources active, no NL load).
///
/// The HB error function is then:
///   F(V) = Y_{N×N}·V + I_src + I_nl(V) = 0
///   which is Y_{N×N}·(V − V_oc) + I_nl(V) = 0  (Newton balance condition).
///
/// Extraction method (per linear-engine §9/§10):
///   1. Zero sources → factor → N unit-current solves → Z_{N×N} → invert → Y_{N×N}.
///   2. Active sources → single solve → V_oc at interface → I_src = −Y_{N×N}·V_oc.
/// </summary>
public sealed class HbLinearExtractor
{
    private readonly ElaboratedNetlist _netlist;
    private readonly AnalysisSettings  _settings;
    private readonly int[]             _interfaceNodes;  // non-ground circuit nodes at interface
    private readonly int               _nonGroundCount;
    private readonly int               _N;               // number of interface ports

    private readonly Func<int, string> _nodeNamer;
    private readonly Func<int, string> _branchNamer;

    // Keyed by omega = k·ω₀.  G is topology-based and unchanged across sweep points,
    // so the factorization computed during Extract() is reused by SolveFullNetwork().
    // G is also cached (pre-factored CSC matrix) so the exporter can retrieve sparse G
    // without rebuilding it.  G is null in the SolveFullNetwork lazy-cache path only
    // when Extract() was never called for that omega (e.g. DC k=0 back-solve hit first);
    // in that case the exporter must call Extract() or rebuild G on demand.
    private readonly Dictionary<double, (SparseLU Lu, int Size, Complex[,]? YNN,
        CompressedColumnStorage<Complex>? G)> _luCache = new();

    // Branch-name map built lazily after the first BuildMna() call.
    // index b (0-based branch offset) → human-readable name for export.
    private string[]? _branchNames;
    private int       _cachedMnaSize = -1;

    public HbLinearExtractor(ElaboratedNetlist netlist, AnalysisSettings settings)
    {
        _netlist = netlist;
        _settings = settings;

        _interfaceNodes = netlist.NonlinearNodes
            .Where(n => n > 0)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();
        _N = _interfaceNodes.Length;
        _nonGroundCount = netlist.Nodes.Count - 1;

        var nodeTouchers = new Dictionary<int, List<string>>();
        foreach (var ec in netlist.Components)
            foreach (var n in ec.Nodes)
            {
                if (n == 0) continue;
                if (!nodeTouchers.TryGetValue(n, out var lst))
                    nodeTouchers[n] = lst = [];
                lst.Add($"{ec.ComponentType}:{ec.InstancePath}");
            }

        _nodeNamer = matIdx =>
        {
            int node = matIdx + 1;
            string nm = node < netlist.Nodes.Count ? netlist.Nodes.NameOf(node) : $"node#{node}";
            if (nodeTouchers.TryGetValue(node, out var t) && t.Count > 0)
                nm += $" (touched by: {string.Join(", ", t.Take(4))})";
            return nm;
        };

        _branchNamer = idx => $"branch#{idx - _nonGroundCount}";
    }

    public int   InterfaceCount       => _N;
    public int[] InterfaceNodes       => _interfaceNodes;
    public int   InterfaceNode(int p) => _interfaceNodes[p];
    /// <summary>Number of non-ground circuit nodes (solution x[0..NonGroundCount-1] = node voltages).</summary>
    public int   NonGroundCount       => _nonGroundCount;

    /// <summary>
    /// Returns Y_{N×N}(0) and I_src(0) for the DC (k=0) harmonic using the REAL
    /// DC admittance of the linear partition (Maas 3.10–3.14, ω=0 case).
    ///
    /// Follows the same two-step Z-column / active-source extraction as Extract(omega),
    /// evaluated at ω=0 (inductor→short, capacitor→open, gmin to ground).
    ///
    /// Voltage-pinned singularity handling (linear-engine §4.3.1):
    ///   An ideal-choke (L, no R=) through an ideal voltage source → Z(0)=0 at the
    ///   interface node. Behaviour by <see cref="AnalysisSettings.InductanceRegularization"/>:
    ///   - Always:      apply series R=InductanceRegR to all inductors from the start.
    ///   - IfNecessary: try without first; if singular, apply + warn + retry.
    ///   - Never:       throw SingularMatrixException with the diagnostic.
    /// This is the inductive dual of the gmin (ConductanceRegularization) pattern.
    /// </summary>
    public (Complex[,] YNN, Complex[] ISrc) ExtractDC()
    {
        bool alwaysReg = _settings.InductanceRegularization == RegularizationMode.Always;

        // ── First attempt (possibly with reg applied from the start) ────────────
        var (zNN, singularIdxs) = ComputeZnn(alwaysReg);

        if (singularIdxs.Count > 0)
        {
            if (_settings.InductanceRegularization == RegularizationMode.Never)
                ThrowSingularDiagnostic(zNN);

            // IfNecessary: regularize and retry. The notice is a per-solve diagnostic (it would
            // otherwise repeat every point of a sweep) — gate it behind HbConsoleDiagnostics. The
            // regularization itself always runs; it converges to the exact answer as R→0.
            if (_settings.HbConsoleDiagnostics) WarnInductanceReg(singularIdxs);
            (zNN, _) = ComputeZnn(applyIndReg: true);
        }

        var yNN  = InvertNN(zNN, _N);
        var iSrc = ComputeISrc(yNN, alwaysReg || singularIdxs.Count > 0);
        return (yNN, iSrc);
    }

    // ── DC extraction helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Build MNA at ω=0 with zeroed sources, optionally apply inductance regularization,
    /// then do the Z-column extraction.  Returns (Z_{N×N}, list of singular diagonal indices).
    /// </summary>
    private (Complex[,] ZNN, List<int> SingularIdxs) ComputeZnn(bool applyIndReg)
    {
        var mnaZ = BuildMna(0.0, zeroDrive: true);
        if (applyIndReg) ApplyInductanceReg(mnaZ);
        var luZ  = mnaZ.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);

        var zNN  = new Complex[_N, _N];
        var xBuf = new Complex[mnaZ.Size];

        for (int j = 0; j < _N; j++)
        {
            var b = new Complex[mnaZ.Size];
            int nodeJ = _interfaceNodes[j];
            if (nodeJ > 0) b[nodeJ - 1] = Complex.One;
            luZ.Solve(b, xBuf);
            for (int k = 0; k < _N; k++)
            {
                int nodeK = _interfaceNodes[k];
                zNN[k, j] = nodeK > 0 ? xBuf[nodeK - 1] : Complex.Zero;
            }
        }

        // Detect voltage-pinned singularity: Z[i,i] ≈ 0 means V is rigid.
        var singular = new List<int>();
        for (int i = 0; i < _N; i++)
            if (Complex.Abs(zNN[i, i]) < 1e-15) singular.Add(i);

        return (zNN, singular);
    }

    /// <summary>
    /// Solve for V_oc(0) with bias sources active and compute I_src = −Y_{N×N}·V_oc.
    /// Uses the same regularization state as the Z-column pass for consistency.
    /// </summary>
    private Complex[] ComputeISrc(Complex[,] yNN, bool applyIndReg)
    {
        var mnaS = BuildMna(0.0, zeroDrive: false);
        if (applyIndReg) ApplyInductanceReg(mnaS);
        var luS  = mnaS.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);
        var bSrc = mnaS.BuildRhs();
        var xSrc = new Complex[mnaS.Size];
        luS.Solve(bSrc, xSrc);

        var vOc = new Complex[_N];
        for (int k = 0; k < _N; k++)
        {
            int nodeK = _interfaceNodes[k];
            vOc[k] = nodeK > 0 ? xSrc[nodeK - 1] : Complex.Zero;
        }

        var iSrc = new Complex[_N];
        for (int k = 0; k < _N; k++)
        {
            Complex sum = Complex.Zero;
            for (int j = 0; j < _N; j++) sum += yNN[k, j] * vOc[j];
            iSrc[k] = -sum;
        }
        return iSrc;
    }

    /// <summary>
    /// Apply inductance regularization to an already-assembled MNA.
    /// Mirrors <c>SParameterEngine.ApplyRegularization</c> for the inductive path.
    /// Adds −InductanceRegR to every inductor branch diagonal (series R at DC).
    /// </summary>
    private void ApplyInductanceReg(MnaSystem mna)
    {
        var rReg = new Complex(-_settings.InductanceRegR, 0.0);
        foreach (var ec in _netlist.Components)
        {
            if (ec.Model is InductorModel im && im.LastBranchIndex >= 0)
                mna.AddBranchConstraint(im.LastBranchIndex, im.LastBranchIndex, rReg);

            // TunerModel's internal RF choke (stamped inline by TunerModel.StampInductor)
            // must also be regularized — it is NOT an InductorModel, so the check above
            // won't catch it. ChokeBranchIndex is set during the most recent Stamp() call.
            if (ec.Model is TunerModel tm && tm.ChokeBranchIndex >= 0)
                mna.AddBranchConstraint(tm.ChokeBranchIndex, tm.ChokeBranchIndex, rReg);
        }
    }

    /// <summary>
    /// Warn to stderr that inductance regularization engaged at the DC interface
    /// (IfNecessary mode only — Always mode doesn't need to warn; Never mode throws).
    /// </summary>
    private void WarnInductanceReg(List<int> singularIdxs)
    {
        var names = string.Join(", ", singularIdxs.Select(i =>
        {
            int nodeK = _interfaceNodes[i];
            return _nodeNamer(nodeK - 1);
        }));
        Console.Error.WriteLine(
            $"[HB] ExtractDC: InductanceRegularization=IfNecessary engaged. " +
            $"DC interface node(s) voltage-pinned: [{names}]. " +
            $"Added series R={_settings.InductanceRegR:G3} Ω to all inductor branches " +
            $"(inductive dual of gmin; converges to exact answer as R→0).");
    }

    /// <summary>
    /// Throw SingularMatrixException with full diagnostic (Never mode).
    /// Performs the V_oc active-source solve to name the bias voltages.
    /// </summary>
    private void ThrowSingularDiagnostic(Complex[,] zNN)
    {
        var mnaVoc = BuildMna(0.0, zeroDrive: false);
        var luVoc  = mnaVoc.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);
        var bVoc   = mnaVoc.BuildRhs();
        var xVoc   = new Complex[mnaVoc.Size];
        luVoc.Solve(bVoc, xVoc);

        var sb = new StringBuilder();
        sb.AppendLine("[HB] ExtractDC: Z_NN(0) is singular — one or more interface nodes are voltage-pinned at DC.");
        sb.AppendLine("  Cause: ideal inductor (no series R) + ideal voltage source = zero-impedance DC path.");
        sb.AppendLine("  Interface node diagnostics:");
        for (int k = 0; k < _N; k++)
        {
            int    nodeK = _interfaceNodes[k];
            double vOcK  = nodeK > 0 && nodeK - 1 < xVoc.Length ? xVoc[nodeK - 1].Real : 0.0;
            sb.AppendLine(
                $"    [{k}] {_nodeNamer(nodeK - 1)}: Z_NN[{k},{k}] = {zNN[k, k].Magnitude:E3} Ω" +
                $"  V_oc(0) = {vOcK:F4} V");
        }
        sb.AppendLine("  Fix: use InductanceRegularization=IfNecessary (default) or Always,");
        sb.AppendLine("       or add series R to bias-tee inductors (e.g. L:Lchoke R=1e-6).");
        throw new SingularMatrixException(sb.ToString());
    }

    // ── Per-harmonic extraction ───────────────────────────────────────────────

    /// <summary>
    /// Extract (Y_{N×N}, I_src) at the given angular frequency.
    /// omega = k * 2π * f0 exactly (the HB exact-harmonic guarantee, linear-engine §4.4).
    /// </summary>
    public (Complex[,] YNN, Complex[] ISrc) Extract(double omega)
    {
        SparseLU   luZ;
        int        mnaSize;
        Complex[,] yNN;

        // ── Step 1: Y_{N×N} via Z-column extraction (sources zeroed) ──────────
        // G is topology-based — identical across sweep points.  Cache the factorization
        // so (a) repeated sweep-loop Extract calls skip refactorization, and (b)
        // SolveFullNetwork reuses the EXACT same LU, giving back-solve matching the
        // HB interface voltages to ~1e-12 instead of ~1e-5 from a rebuild.
        if (!_luCache.TryGetValue(omega, out var entry))
        {
            var mnaZ = BuildMna(omega, zeroDrive: true);
            // Snapshot the sparse G BEFORE factorization for export use (data-export.md §4.2).
            // BuildCsc() reads the same _entries dict that Factorize() will consume — safe.
            var gMatrix = mnaZ.BuildCsc();
            luZ     = mnaZ.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);
            mnaSize = mnaZ.Size;

            // Lazy-build branch name map on first successful MNA (size is stable).
            if (_branchNames is null && mnaZ.BranchCount > 0)
            {
                _branchNames = BuildBranchNamesFromMna(mnaZ);
                _cachedMnaSize = mnaSize;
            }

            var zNN  = new Complex[_N, _N];
            var xBuf = new Complex[mnaSize];

            for (int j = 0; j < _N; j++)
            {
                // Inject 1A at interface node j; solve; V at interface nodes = Z column j.
                var b = new Complex[mnaSize];
                int nodeJ = _interfaceNodes[j];
                if (nodeJ > 0) b[nodeJ - 1] = Complex.One;

                luZ.Solve(b, xBuf);

                for (int k = 0; k < _N; k++)
                {
                    int nodeK = _interfaceNodes[k];
                    zNN[k, j] = nodeK > 0 ? xBuf[nodeK - 1] : Complex.Zero;
                }
            }

            // Invert Z_{N×N} → Y_{N×N}.
            yNN = InvertNN(zNN, _N);
            _luCache[omega] = (luZ, mnaSize, yNN, gMatrix);
        }
        else
        {
            luZ     = entry.Lu;
            mnaSize = entry.Size;
            yNN     = entry.YNN ?? new Complex[_N, _N]; // guard — should never be null from Extract path
        }

        // ── Step 2: V_oc with sources active → I_src = −Y_{N×N}·V_oc ─────────
        // Reuse cached LU: G is the same; only bSrc changes with sweep/drive state.
        var mnaS = BuildMna(omega, zeroDrive: false);
        var bSrc = mnaS.BuildRhs();
        var xSrc = new Complex[mnaSize];
        luZ.Solve(bSrc, xSrc);

        var vOc  = new Complex[_N];
        for (int k = 0; k < _N; k++)
        {
            int nodeK = _interfaceNodes[k];
            vOc[k] = nodeK > 0 ? xSrc[nodeK - 1] : Complex.Zero;
        }

        // I_src[k] = −Σ_j Y_{N×N}[k,j] * V_oc[j]
        var iSrc = new Complex[_N];
        for (int k = 0; k < _N; k++)
        {
            Complex sum = Complex.Zero;
            for (int j = 0; j < _N; j++)
                sum += yNN[k, j] * vOc[j];
            iSrc[k] = -sum;
        }

        return (yNN, iSrc);
    }

    // ── MNA assembly ──────────────────────────────────────────────────────────

    private MnaSystem BuildMna(double omega, bool zeroDrive)
    {
        var mna = new MnaSystem(_nonGroundCount);

        // Non-mutual, non-nonlinear components first.
        foreach (var ec in _netlist.Components)
        {
            if (ec.IsNonlinear)       continue;  // linear partition only
            if (ec.Model is MutualInductanceModel) continue;
            // Term/Port branches are driven ports for S-parameter analysis only; inert in HB.
            if (ec.Model is PortModel or TermModel) continue;

            if (zeroDrive && IsVoltageOrToneSource(ec))
                ec.Model.Stamp(new ZeroDriveMna(mna), ec, omega);
            else
                ec.Model.Stamp(mna, ec, omega);
        }
        // Mutual coupling after inductors are stamped.
        foreach (var ec in _netlist.Components)
            if (ec.Model is MutualInductanceModel)
                ec.Model.Stamp(mna, ec, omega);

        // Gmin regularization.
        if (_settings.ConductanceRegularization != RegularizationMode.Never)
        {
            double gmin = _settings.Gmin;
            for (int n = 1; n <= _nonGroundCount; n++)
                mna.AddAdmittance(n, 0, new Complex(gmin, 0));
        }

        return mna;
    }

    private static bool IsVoltageOrToneSource(ElaboratedComponent ec) =>
        ec.Model is VdcModel or ToneSourceModel or TunerModel;
    // TunerModel contains an internal V_1Tone drive (SourceTuner) and a bias supply
    // (both roles) — it must be stamped via ZeroDriveMna in the zeroDrive=true path
    // so its source values are zeroed for the Y_NN extraction. The ZeroDriveMna passes
    // all AddBranch/AddConstraint/AddBranchConstraint/AddAdmittance through to the real
    // MNA, zeroing only AddSourceValue — so the impedance topology (Z_Port, choke, cap)
    // is correctly included in Y_NN while the independent sources are suppressed.

    // ── Full-network back-solve (Correction 1: lazy linear interior recovery) ──

    /// <summary>
    /// Returns the full-MNA right-hand-side vector for the given frequency with
    /// all sources active.  Caller must snapshot this during the sweep loop while
    /// the component state (ToneSource phasors, bias values) is correct for that
    /// sweep point — the RHS is NOT stable after UpdateSweepPoint advances.
    /// </summary>
    public Complex[] BuildSourceRhs(double omega) =>
        BuildMna(omega, zeroDrive: false).BuildRhs();

    /// <summary>
    /// Solve the full linear network at <paramref name="omega"/> using a pre-built
    /// source RHS (snapshotted during the sweep loop at the correct sweep state)
    /// and injecting the converged NL currents at the interface nodes.
    ///
    /// The returned solution vector x has:
    ///   x[0 .. NonGroundCount-1]  = node voltages (1-based node n → index n-1)
    ///   x[NonGroundCount .. Size-1] = branch currents (e.g. IProbe, inductors)
    ///
    /// The matrix topology (G) is independent of zeroDrive, so BuildMna with
    /// zeroDrive=true gives the same G that was used to build bSrc — only b differs.
    /// Gmin regularization (always applied via BuildMna) prevents singularity.
    /// </summary>
    public Complex[] SolveFullNetwork(double omega, Complex[] iNlAtInterface, Complex[] bSrc)
    {
        // Reuse the exact factorization built during Extract() so the back-solve and
        // the HB solve use the SAME linear system (machine-precision agreement, ~1e-12).
        // If omega was never Extract()ed (e.g., DC k=0), build and cache it now,
        // including the sparse G snapshot for export.
        if (!_luCache.TryGetValue(omega, out var entry))
        {
            var mna0 = BuildMna(omega, zeroDrive: true);
            var g0   = mna0.BuildCsc();   // snapshot before Factorize (data-export.md §4.2)
            var lu0  = mna0.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);

            if (_branchNames is null && mna0.BranchCount > 0)
            {
                _branchNames = BuildBranchNamesFromMna(mna0);
                _cachedMnaSize = mna0.Size;
            }

            entry = (lu0, mna0.Size, null, g0);
            _luCache[omega] = entry;
        }

        var b = (Complex[])bSrc.Clone();  // don't modify caller's copy

        for (int j = 0; j < _N; j++)
        {
            int nodeJ = _interfaceNodes[j];
            if (nodeJ > 0 && nodeJ - 1 < b.Length)
                b[nodeJ - 1] -= iNlAtInterface[j];  // NL device draws iNl FROM the node
        }

        var x = new Complex[entry.Size];
        entry.Lu.Solve(b, x);
        return x;
    }

    /// <summary>
    /// Control-current sensitivity row (brief #3 §1): for the referenced branch
    /// <paramref name="branchIdx"/> at frequency <paramref name="omega"/>, returns
    /// <c>rRef[j] = ∂(x[branchIdx]) / ∂(iNl_at_interface[j])</c> for each interface
    /// node j = 0..N-1, i.e. <c>rRef[j] = −(G⁻¹)_{branchIdx, node_j}</c>.
    ///
    /// The minus sign matches SolveFullNetwork's injection convention
    /// (<c>b[node_j] -= iNl[j]</c>), so the §4 functional reproduces the forward solve:
    /// <c>x[branchIdx] = c0 + Σ_j rRef[j]·iNl[j]</c> with
    /// <c>c0 = SolveFullNetwork(omega, 0, bSrc)[branchIdx]</c>.
    ///
    /// Computed by N forward solves against the cached LU (the EXACT factorization
    /// SolveFullNetwork uses — guaranteed consistent with the residual): for each
    /// interface node j, solve <c>G z = e_{node_j}</c> and read <c>rRef[j] = −z[branchIdx]</c>.
    /// Reuses (and lazily populates) the per-omega <c>_luCache</c> entry.
    /// </summary>
    public Complex[] ControlSensitivityRow(double omega, int branchIdx)
    {
        if (!_luCache.TryGetValue(omega, out var entry))
        {
            var mna0 = BuildMna(omega, zeroDrive: true);
            var g0   = mna0.BuildCsc();
            var lu0  = mna0.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);
            if (_branchNames is null && mna0.BranchCount > 0)
            {
                _branchNames = BuildBranchNamesFromMna(mna0);
                _cachedMnaSize = mna0.Size;
            }
            entry = (lu0, mna0.Size, null, g0);
            _luCache[omega] = entry;
        }

        var rRef = new Complex[_N];
        var bvec = new Complex[entry.Size];
        var z    = new Complex[entry.Size];
        for (int j = 0; j < _N; j++)
        {
            int nodeJ = _interfaceNodes[j];
            Array.Clear(bvec, 0, bvec.Length);
            if (nodeJ > 0 && nodeJ - 1 < bvec.Length) bvec[nodeJ - 1] = Complex.One;
            entry.Lu.Solve(bvec, z);
            rRef[j] = branchIdx >= 0 && branchIdx < z.Length ? -z[branchIdx] : Complex.Zero;
        }
        return rRef;
    }

    // ── Export accessors (data-export.md §4, §8) ─────────────────────────────

    /// <summary>
    /// Size of the full MNA system (nonGroundCount + branchCount).
    /// Available after the first Extract() or SolveFullNetwork() call.
    /// Returns −1 if no stamp has occurred yet.
    /// </summary>
    internal int MnaSize => _cachedMnaSize;

    /// <summary>
    /// Node-index → name map for export.
    /// Index i (0-based) → name of circuit node (i+1).
    /// Length = NonGroundCount.
    /// </summary>
    internal string[] NodeNames
    {
        get
        {
            var names = new string[_nonGroundCount];
            for (int i = 0; i < _nonGroundCount; i++)
                names[i] = _netlist.Nodes.NameOf(i + 1);
            return names;
        }
    }

    /// <summary>
    /// Branch-index → name map for export.
    /// Index b (0-based branch offset from NonGroundCount) → human-readable name.
    /// Built lazily on first Extract()/SolveFullNetwork() call; returns empty array
    /// before any stamp has occurred.
    /// </summary>
    internal string[] BranchNames => _branchNames ?? [];

    /// <summary>
    /// Return the sparse G(ω_k) matrix in triplet (COO) form for export.
    /// omega = k == 0 ? 0.0 : k * 2π * f0.
    ///
    /// The G matrix is topology-invariant: the same nonzero pattern and values are
    /// produced for every call with the same omega.  The caller must have called
    /// Extract(omega) or triggered SolveFullNetwork(omega, …) at least once so the
    /// cache entry exists.  Returns ([], [], []) if the cache has no entry for this omega.
    /// </summary>
    internal (int[] Rows, int[] Cols, System.Numerics.Complex[] Data) GetSparseG(double omega)
    {
        if (!_luCache.TryGetValue(omega, out var entry) || entry.G is null)
            return ([], [], []);

        var g = entry.G;
        // CSparse CSC: ColumnPointers[j] .. ColumnPointers[j+1]-1 hold row indices for col j.
        int n   = g.ColumnCount;
        int nnz = g.Values.Length;
        var rows = new int[nnz];
        var cols = new int[nnz];
        var data = new System.Numerics.Complex[nnz];

        int idx = 0;
        for (int j = 0; j < n; j++)
        {
            for (int ptr = g.ColumnPointers[j]; ptr < g.ColumnPointers[j + 1]; ptr++)
            {
                rows[idx] = g.RowIndices[ptr];
                cols[idx] = j;
                data[idx] = g.Values[ptr];
                idx++;
            }
        }
        return (rows, cols, data);
    }

    /// <summary>
    /// Build the branch-index→name map by inspecting LastBranchIndex on stamped models.
    /// Called once after the first BuildMna(); the map is stable (branch indices are
    /// topology-invariant across frequencies and sweep points).
    /// </summary>
    private string[] BuildBranchNamesFromMna(MnaSystem mna)
    {
        int branchCount = mna.BranchCount;
        var names       = new string[branchCount];

        foreach (var ec in _netlist.Components)
        {
            if (ec.IsNonlinear) continue;

            switch (ec.Model)
            {
                case InductorModel im when im.LastBranchIndex >= _nonGroundCount:
                    names[im.LastBranchIndex - _nonGroundCount] = $"L:{ec.InstancePath}";
                    break;

                case VdcModel vm when vm.LastBranchIndex >= _nonGroundCount:
                    names[vm.LastBranchIndex - _nonGroundCount] = $"V:{ec.InstancePath}";
                    break;

                case IProbeModel ipm when ipm.LastBranchIndex >= _nonGroundCount:
                    names[ipm.LastBranchIndex - _nonGroundCount] = $"IProbe:{ec.InstancePath}";
                    break;

                case TunerModel tm:
                    if (tm.ChokeBranchIndex >= _nonGroundCount)
                        names[tm.ChokeBranchIndex - _nonGroundCount]      = $"Tuner:{ec.InstancePath}:choke";
                    if (tm.BiasSupplyBranchIndex >= _nonGroundCount)
                        names[tm.BiasSupplyBranchIndex - _nonGroundCount] = $"Tuner:{ec.InstancePath}:bias";
                    break;
            }
        }

        // Fill any unnamed slots (SnpModel multi-branch Z-expansion, Short, etc.)
        for (int b = 0; b < branchCount; b++)
            if (names[b] is null) names[b] = $"branch#{b}";

        return names;
    }

    // ── Dense N×N matrix inversion (Gauss-Jordan with partial pivoting) ───────

    private static Complex[,] InvertNN(Complex[,] src, int n)
    {
        var a = (Complex[,])src.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = Complex.One;

        for (int col = 0; col < n; col++)
        {
            // Partial pivot.
            int pivot = col;
            double best = Complex.Abs(a[col, col]);
            for (int row = col + 1; row < n; row++)
            {
                double mag = Complex.Abs(a[row, col]);
                if (mag > best) { best = mag; pivot = row; }
            }
            if (pivot != col)
            {
                for (int j = 0; j < n; j++)
                {
                    (a[col, j],   a[pivot, j])   = (a[pivot, j],   a[col, j]);
                    (inv[col, j], inv[pivot, j]) = (inv[pivot, j], inv[col, j]);
                }
            }

            Complex diag = a[col, col];
            if (Complex.Abs(diag) < 1e-30) continue;  // singular row — skip (leaves zeros)
            for (int j = 0; j < n; j++) { a[col, j] /= diag; inv[col, j] /= diag; }

            for (int row = 0; row < n; row++)
            {
                if (row == col) continue;
                Complex factor = a[row, col];
                for (int j = 0; j < n; j++)
                {
                    a[row, j]   -= factor * a[col, j];
                    inv[row, j] -= factor * inv[col, j];
                }
            }
        }
        return inv;
    }
}

/// <summary>
/// IMnaContext proxy that zeros all AddSourceValue calls (for source-zeroed Y extraction).
/// All structural stamps (branches, constraints, admittances) pass through unchanged.
/// </summary>
file sealed class ZeroDriveMna(MnaSystem inner) : IMnaContext
{
    public int  AddBranch() => inner.AddBranch();
    public void AddAdmittance(int a, int b, Complex y) => inner.AddAdmittance(a, b, y);
    public void AddBlockAdmittance(int r, int c, Complex y) => inner.AddBlockAdmittance(r, c, y);
    public void AddBranchCurrent(int br, int from, int to) => inner.AddBranchCurrent(br, from, to);
    public void AddConstraint(int br, int node, Complex coeff) => inner.AddConstraint(br, node, coeff);
    public void AddNodeBranchCoupling(int node, int br, Complex coeff) => inner.AddNodeBranchCoupling(node, br, coeff);
    public void AddBranchConstraint(int br, int other, Complex coeff) => inner.AddBranchConstraint(br, other, coeff);
    public void AddCurrentInjection(int node, Complex j) => inner.AddCurrentInjection(node, j);
    public void AddSourceValue(int branch, Complex value) { /* zeroed for Y extraction */ }
}
