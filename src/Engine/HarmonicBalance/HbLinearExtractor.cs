using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using System.Text;

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

            // IfNecessary: warn and retry with inductance regularization.
            WarnInductanceReg(singularIdxs);
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
        // ── Step 1: Y_{N×N} via Z-column extraction (sources zeroed) ──────────
        var mnaZ = BuildMna(omega, zeroDrive: true);
        var luZ  = mnaZ.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);

        var zNN  = new Complex[_N, _N];
        var xBuf = new Complex[mnaZ.Size];

        for (int j = 0; j < _N; j++)
        {
            // Inject 1A at interface node j; solve; V at interface nodes = Z column j.
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

        // Invert Z_{N×N} → Y_{N×N}.
        var yNN = InvertNN(zNN, _N);

        // ── Step 2: V_oc with sources active → I_src = −Y_{N×N}·V_oc ─────────
        var mnaS = BuildMna(omega, zeroDrive: false);
        var luS  = mnaS.Factorize(nodeNamer: _nodeNamer, branchNamer: _branchNamer);

        var bSrc = mnaS.BuildRhs();
        var xSrc = new Complex[mnaS.Size];
        luS.Solve(bSrc, xSrc);

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
        ec.Model is VoltageSourceModel or ToneSourceModel or TunerModel;
    // TunerModel contains an internal V_1Tone drive (SourceTuner) and a bias supply
    // (both roles) — it must be stamped via ZeroDriveMna in the zeroDrive=true path
    // so its source values are zeroed for the Y_NN extraction. The ZeroDriveMna passes
    // all AddBranch/AddConstraint/AddBranchConstraint/AddAdmittance through to the real
    // MNA, zeroing only AddSourceValue — so the impedance topology (Z_Port, choke, cap)
    // is correctly included in Y_NN while the independent sources are suppressed.

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
    public void AddBranchConstraint(int br, int other, Complex coeff) => inner.AddBranchConstraint(br, other, coeff);
    public void AddCurrentInjection(int node, Complex j) => inner.AddCurrentInjection(node, j);
    public void AddSourceValue(int branch, Complex value) { /* zeroed for Y extraction */ }
}
