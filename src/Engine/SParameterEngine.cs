using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CSparse.Complex.Factorization;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine;

/// <summary>
/// Runs an S-parameter sweep over a frequency grid (linear-engine §2, §6, §9).
///
/// Two code paths, chosen once per run based on port Z0 values:
///
///   WAVE PATH (allPortsResistive — Re(Z0) > 0 for every port, the common case):
///     Each port stamps a conductance 1/Z0 between its nodes (no branch unknown).
///     Excitation: current injection 2√(Re Z0)/Z0 at the driven port.
///     S read directly from port voltages via the power-wave (Kurokawa) formula.
///     No Y→S inversion step. Parallel ports / port-across-short topologies are
///     non-singular by construction. Regularization is a genuine last resort.
///
///   LEGACY PATH (any port has Re(Z0) ≤ 0, e.g. reactive reference impedance):
///     Each port stamps an ideal 0 V branch. Unit-voltage excitation at each port.
///     Y-matrix extracted from branch currents; S = RFNetwork.YToS(Y, z0).
///     Keeps the legacy singular-matrix behavior for genuinely ill-posed circuits.
///     HB/DC are never affected (they already treat Port/Term as inert).
///
/// Regularization tri-state (<see cref="RegularizationMode"/>):
///   IfNecessary: first attempt with no regularization; if singular, retry with both.
///   Always:      apply regularization before first factorization.
///   Never:       no regularization; throw <see cref="SingularMatrixException"/> on singular.
/// </summary>
public static class SParameterEngine
{
    /// <summary>gmin conductance added from every node to ground (§5).</summary>
    public const double DefaultGmin = 1e-12;

    public static DataSet Run(
        ElaboratedNetlist netlist,
        double[]          freqsHz,
        AnalysisSettings? settings = null)
    {
        settings ??= AnalysisSettings.Default;

        // ── Identify ports + build branch-label map ───────────────────────────
        int nonGroundNodes = netlist.Nodes.Count - 1;
        var (ports, branchLabels) = CollectPortsAndBranchLabels(netlist, nonGroundNodes);
        if (ports.Count == 0)
            throw new InvalidOperationException(
                "S-parameter analysis requires at least one Port, Term, or P1Tone component at the testbench top level. " +
                "Place Term or P1Tone components (Num=1, Z=50 Ohm) directly in the testbench, not inside sub-cells.");
        int N = ports.Count;

        var mna       = new MnaSystem(nonGroundNodes);
        var freqCount = freqsHz.Length;
        var sMatrices = new Mat<Complex>[freqCount];
        var z0PerPort = ports.Select(p => p.Z0).ToArray();

        // Choose path once: wave path when every port Z0 has a positive real part.
        bool allPortsResistive = z0PerPort.All(z => z.Real > 1e-12);

        // ── Build diagnostic namers (used when factorization fails) ───────────
        var nodeTouchers = new Dictionary<int, List<string>>(netlist.Nodes.Count);
        foreach (var ec in netlist.Components)
            foreach (var n in ec.Nodes)
            {
                if (n == 0) continue;
                if (!nodeTouchers.TryGetValue(n, out var lst))
                    nodeTouchers[n] = lst = [];
                lst.Add($"{ec.ComponentType}:{ec.InstancePath}");
            }

        Func<int, string> nodeNamer = matIdx =>
        {
            int nodeIdx = matIdx + 1;
            string name = nodeIdx < netlist.Nodes.Count
                ? netlist.Nodes.NameOf(nodeIdx)
                : $"node#{nodeIdx}";
            if (nodeTouchers.TryGetValue(nodeIdx, out var t) && t.Count > 0)
            {
                const int maxShow = 8;
                var shown = t.Count <= maxShow
                    ? t
                    : [.. t.Take(maxShow), $"...+{t.Count - maxShow} more"];
                name += $" (touched by: {string.Join(", ", shown)})";
            }
            return name;
        };

        Func<int, string> branchNamer = idx =>
            branchLabels.TryGetValue(idx, out var lbl) ? lbl : $"branch#{idx - nonGroundNodes}";

        bool canRetry =
            settings.ConductanceRegularization == RegularizationMode.IfNecessary ||
            settings.InductanceRegularization  == RegularizationMode.IfNecessary;

        // ── Nonlinear devices → solve the DC operating point once and linearize there (design §3.2) ──
        // RULE: purely-linear S-parameter runs never touch the DC engine (zero behavior change).
        double[]? dcNodeVoltages = null;
        bool hasNonlinear = netlist.Components.Any(c => c.Model.Kind == ModelKind.Nonlinear);
        if (hasNonlinear)
        {
            NonlinearDcEngine.DcResult? dc = null;
            try { dc = NonlinearDcEngine.Run(netlist, settings); }
            catch (NonlinearDcNotConvergedException) { dc = null; }  // DcBiasStepping=Never path throws

            if (dc is { Converged: true })
            {
                dcNodeVoltages = dc.NodeVoltages;
                const double ZeroBiasTol = 1e-9;
                if (dc.NodeVoltages.All(v => Math.Abs(v) < ZeroBiasTol))
                    netlist.AddWarningOnce("sparam-zero-bias",
                        "No DC bias present; nonlinear components linearized at the 0 V operating point.");
            }
            else
            {
                // Non-convergence (degenerate) → warn + fall back to zero-bias linearization (design §3.5).
                string detail = dc is null ? "(no result)" : $"residual {dc.FinalResidual:G3} after {dc.Iterations} iters";
                netlist.AddWarningOnce("sparam-dc-nonconverged",
                    $"DC operating-point solve did not converge ({detail}); nonlinear components linearized at " +
                    "0 V. S-parameters may be inaccurate.");
                dcNodeVoltages = null;  // null ⇒ BuildBias yields 0 V
            }
        }

        if (allPortsResistive)
            RunWavePath(netlist, freqsHz, settings, ports, N, mna, freqCount, sMatrices,
                nodeNamer, branchNamer, canRetry, dcNodeVoltages);
        else
            RunLegacyPath(netlist, freqsHz, settings, ports, N, z0PerPort, mna, freqCount, sMatrices,
                nodeNamer, branchNamer, canRetry, dcNodeVoltages);

        var refZ0 = z0PerPort.Length > 0 ? z0PerPort[0] : new Complex(50, 0);
        var snp   = new SNP(freqsHz, sMatrices, MatrixType.S, MatrixFormat.RI, refZ0);
        var ds    = DataSetBuilder.FromSnp(snp);            // S cube + uniform Z0 placeholder
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort)); // overwrite with per-port truth
        return ds;
    }

    // ── Wave path (Re(Z0) > 0 for every port) ─────────────────────────────────

    private static void RunWavePath(
        ElaboratedNetlist    netlist,
        double[]             freqsHz,
        AnalysisSettings     settings,
        List<PortEntry>      ports,
        int                  N,
        MnaSystem            mna,
        int                  freqCount,
        Mat<Complex>[]       sMatrices,
        Func<int, string>    nodeNamer,
        Func<int, string>    branchNamer,
        bool                 canRetry,
        double[]?            dcNodeVoltages = null)
    {
        int nonGroundNodes = mna.NodeCount;

        for (int fi = 0; fi < freqCount; fi++)
        {
            double omega = 2.0 * Math.PI * freqsHz[fi];

            // Stamp network (ports contribute conductances, not 0 V branches).
            StampAll(mna, netlist, omega, skipPorts: true, dcNodeVoltages: dcNodeVoltages);
            StampPortConductances(mna, ports, N);
            ApplyRegularization(mna, netlist, nonGroundNodes, settings, applyIfNecessary: false);

            SparseLU lu;
            try
            {
                lu = mna.Factorize(nodeNamer: nodeNamer, branchNamer: branchNamer);
            }
            catch (SingularMatrixException ex) when (canRetry)
            {
                // Genuine floating-node case; retry with regularization.
                netlist.AddWarningOnce("sparam-regularization",
                    $"S-parameter matrix singular — regularization (gmin) applied. Likely floating node(s):\n" +
                    $"{ex.Message}\n" +
                    $"(conductance={settings.ConductanceRegularization != RegularizationMode.Never} " +
                    $"inductance={settings.InductanceRegularization != RegularizationMode.Never})");

                StampAll(mna, netlist, omega, skipPorts: true, dcNodeVoltages: dcNodeVoltages);
                StampPortConductances(mna, ports, N);
                ApplyRegularization(mna, netlist, nonGroundNodes, settings, applyIfNecessary: true);
                lu = mna.Factorize(nodeNamer: nodeNamer, branchNamer: branchNamer);
            }

            var sMatrix = new Mat<Complex>(N, N);
            var xBuf    = new Complex[mna.Size];

            for (int j = 0; j < N; j++)
            {
                // Incident wave (unit a_j = 1): Norton current I_j = 2√(Re Z0_j) / Z0_j.
                double  sqrtReZ0j = Math.Sqrt(ports[j].Z0.Real);
                Complex iInj      = 2.0 * sqrtReZ0j / ports[j].Z0;

                // RHS: current injection at the driven port nodes.
                var b = new Complex[mna.Size];
                if (ports[j].Node0 > 0) b[ports[j].Node0 - 1] = iInj;
                if (ports[j].Node1 > 0) b[ports[j].Node1 - 1] = -iInj;

                lu.Solve(b, xBuf);

                // Extract S column j via Kurokawa power-wave formula.
                // I_k = I_inj(k==j) − V_k/Z0_k  (port current: injection minus conductance draw)
                // b_k = (V_k − conj(Z0_k)·I_k) / (2√(Re Z0_k))
                // S[k,j] = b_k  (since a_j = 1)
                for (int k = 0; k < N; k++)
                {
                    double  sqrtReZ0k = Math.Sqrt(ports[k].Z0.Real);
                    Complex vk        = GetPortVoltage(xBuf, ports[k]);
                    Complex ik        = (k == j ? iInj : Complex.Zero) - vk / ports[k].Z0;
                    sMatrix[k, j]     = (vk - Complex.Conjugate(ports[k].Z0) * ik) / (2.0 * sqrtReZ0k);
                }
            }

            sMatrices[fi] = sMatrix;
        }
    }

    private static void StampPortConductances(MnaSystem mna, List<PortEntry> ports, int N)
    {
        for (int j = 0; j < N; j++)
            mna.AddAdmittance(ports[j].Node0, ports[j].Node1, Complex.One / ports[j].Z0);
    }

    private static Complex GetPortVoltage(Complex[] x, in PortEntry p)
    {
        Complex v0 = p.Node0 > 0 ? x[p.Node0 - 1] : Complex.Zero;
        Complex v1 = p.Node1 > 0 ? x[p.Node1 - 1] : Complex.Zero;
        return v0 - v1;
    }

    // ── Legacy path (any port has Re(Z0) ≤ 0) ─────────────────────────────────

    private static void RunLegacyPath(
        ElaboratedNetlist    netlist,
        double[]             freqsHz,
        AnalysisSettings     settings,
        List<PortEntry>      ports,
        int                  N,
        Complex[]            z0PerPort,
        MnaSystem            mna,
        int                  freqCount,
        Mat<Complex>[]       sMatrices,
        Func<int, string>    nodeNamer,
        Func<int, string>    branchNamer,
        bool                 canRetry,
        double[]?            dcNodeVoltages = null)
    {
        int nonGroundNodes = mna.NodeCount;

        for (int fi = 0; fi < freqCount; fi++)
        {
            double hz    = freqsHz[fi];
            double omega = 2.0 * Math.PI * hz;

            // ── Stamp components + first regularization pass ──────────────────
            StampAll(mna, netlist, omega, dcNodeVoltages: dcNodeVoltages);
            ApplyRegularization(mna, netlist, nonGroundNodes, settings,
                applyIfNecessary: false); // first attempt omits IfNecessary regs

            // ── Factorize: attempt 1 ──────────────────────────────────────────
            SparseLU lu;
            try
            {
                lu = mna.Factorize(nodeNamer: nodeNamer, branchNamer: branchNamer);
            }
            catch (SingularMatrixException ex) when (canRetry)
            {
                // IfNecessary path: re-stamp and apply all non-Never regs, then retry.
                netlist.AddWarningOnce("sparam-regularization",
                    $"S-parameter matrix singular — regularization (gmin) applied. Likely floating node(s):\n" +
                    $"{ex.Message}\n" +
                    $"(conductance={settings.ConductanceRegularization != RegularizationMode.Never} " +
                    $"inductance={settings.InductanceRegularization != RegularizationMode.Never})");

                StampAll(mna, netlist, omega, dcNodeVoltages: dcNodeVoltages);
                ApplyRegularization(mna, netlist, nonGroundNodes, settings,
                    applyIfNecessary: true);
                lu = mna.Factorize(nodeNamer: nodeNamer, branchNamer: branchNamer);
            }

            // ── Extract port Y-matrix via unit-voltage excitation ─────────────
            var yMat = new Mat<Complex>(N, N);
            var xBuf = new Complex[mna.Size];

            for (int j = 0; j < N; j++)
            {
                var b = mna.BuildRhsWithPortDrive(
                    branchRow:  ports[j].BranchIndex,
                    driveValue: Complex.One);

                lu.Solve(b, xBuf);

                // Branch current flows FROM signal TO ref (AddBranchCurrent convention).
                // Port current (INTO the + terminal) = −branch_current.
                for (int k = 0; k < N; k++)
                    yMat[k, j] = -xBuf[ports[k].BranchIndex];
            }

            // ── Y → S via RfCore (power-wave, per-port complex Z0) ────────────
            sMatrices[fi] = RFNetwork.YToS(yMat, z0PerPort);
        }
    }

    // ── Assembly helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// <summary>Returns true for models that act as s-param ports (Port, Term, or P1Tone).</summary>
    private static bool IsSParamPort(ComponentModel m) => m is PortModel or TermModel or P1ToneModel;

    /// Stamp all components (two-phase: non-mutual first so InductorModel.LastBranchIndex
    /// is set before MutualInductanceModel reads it).
    /// Buried Term/Port/P1Tone components (dotted InstancePath = inside a sub-cell) are silently
    /// skipped — they are inert and never become driven ports (Layer 2 scoping rule).
    /// <paramref name="skipPorts"/>: when true (wave path), top-level Port/Term/P1Tone are also
    /// skipped — they contribute conductances directly in RunWavePath instead of 0 V branches.
    /// </summary>
    private static void StampAll(
        MnaSystem         mna,
        ElaboratedNetlist netlist,
        double            omega,
        bool              skipPorts      = false,
        double[]?         dcNodeVoltages = null)
    {
        mna.Reset();
        foreach (var ec in netlist.Components)
        {
            if (ec.Model is MutualInductanceModel) continue;

            // P1Tone: its internal __drv node (Nodes[2]) hosts the HB V-source junction and is
            // unused in S-param mode — nothing else stamps it, so it would be a floating MNA
            // unknown (zero row/col → singular). Tie it off in BOTH paths. The port itself is
            // realized at the external terminals: the wave path adds the port conductance in
            // RunWavePath (skipPorts); the legacy path stamps a 0 V port branch here (mirrors Term).
            // Buried (sub-cell) P1Tones are inert ports but still need their __drv tied off.
            if (ec.Model is P1ToneModel p1)
            {
                bool buried = ec.InstancePath.Contains('.');
                p1.StampSParamDriveTie(mna, ec);
                if (!buried && !skipPorts) p1.StampAsSParamPort(mna, ec);
                continue;
            }

            // Buried Term/Port: inert even in S-param analysis.
            if (IsSParamPort(ec.Model) && ec.InstancePath.Contains('.')) continue;
            // Wave path: top-level ports stamp their own conductances; skip the branch stamp.
            if (skipPorts && IsSParamPort(ec.Model)) continue;

            // Nonlinear devices: small-signal linearization at the DC operating point (design §3).
            // Never IsSParamPort, so they fall through the port skips above to here.
            if (ec.Model.Kind == ModelKind.Nonlinear)
            {
                ec.Model.StampLinearized(mna, ec, omega, BuildBias(ec, dcNodeVoltages));
                continue;
            }

            ec.Model.Stamp(mna, ec, omega);
        }
        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel)
                ec.Model.Stamp(mna, ec, omega);
    }

    /// <summary>Builds a device's bias PortVoltages from the DC node-voltage solution, using the same
    /// port→node-pair convention as NonlinearDcEngine (port p = Nodes[2p] − Nodes[2p+1]).
    /// Null dcNodeVoltages ⇒ all-zero bias (purely-linear run never reaches here; DC-fail fallback).</summary>
    private static PortVoltages BuildBias(ElaboratedComponent ec, double[]? dcNodeVoltages)
    {
        int P = ec.Model.PortCount;
        var v = new double[P];
        for (int p = 0; p < P; p++)
        {
            int np = ec.Nodes.Length > 2 * p     ? ec.Nodes[2 * p]     : 0;
            int nm = ec.Nodes.Length > 2 * p + 1 ? ec.Nodes[2 * p + 1] : 0;
            v[p] = NodeV(dcNodeVoltages, np) - NodeV(dcNodeVoltages, nm);
        }
        return new PortVoltages(v);
    }

    /// <summary>DC voltage of 1-based circuit node (0 = ground = 0 V).</summary>
    private static double NodeV(double[]? dc, int node1based)
        => (dc is null || node1based <= 0 || node1based - 1 >= dc.Length) ? 0.0 : dc[node1based - 1];

    /// <summary>
    /// Apply conductance and/or inductance regularization to the assembled MNA.
    /// <paramref name="applyIfNecessary"/>: if true, treat IfNecessary as Active (retry pass);
    /// if false, only apply Always regs (first-attempt pass).
    /// Never-mode regs are never applied.
    /// </summary>
    private static void ApplyRegularization(
        MnaSystem         mna,
        ElaboratedNetlist netlist,
        int               nonGroundNodes,
        AnalysisSettings  settings,
        bool              applyIfNecessary)
    {
        bool applyCond = settings.ConductanceRegularization == RegularizationMode.Always
            || (applyIfNecessary && settings.ConductanceRegularization == RegularizationMode.IfNecessary);

        bool applyInd  = settings.InductanceRegularization == RegularizationMode.Always
            || (applyIfNecessary && settings.InductanceRegularization == RegularizationMode.IfNecessary);

        if (applyCond)
        {
            // Add gmin conductance from every non-ground node to ground (linear-engine §5).
            var g = new Complex(settings.Gmin, 0.0);
            for (int n = 1; n <= nonGroundNodes; n++)
                mna.AddAdmittance(n, 0, g);
        }

        if (applyInd)
        {
            // Add small series resistance to every inductor branch diagonal.
            // Cures rank-deficient coupled-inductance D-block (zero eigenvalue of inductance matrix).
            var rReg = new Complex(-settings.InductanceRegR, 0.0);
            foreach (var ec in netlist.Components)
                if (ec.Model is InductorModel im && im.LastBranchIndex >= 0)
                    mna.AddBranchConstraint(im.LastBranchIndex, im.LastBranchIndex, rReg);
        }
    }

    // ── Port collection + branch label map ────────────────────────────────────

    /// <summary>
    /// Port entry. Node0/Node1 are used by the wave path (conductance + voltage readback).
    /// BranchIndex is used by the legacy path (unit-voltage drive + current extraction).
    /// </summary>
    private record struct PortEntry(int PortNum, Complex Z0, int BranchIndex, int Node0, int Node1);

    /// <summary>
    /// Preliminary stamp pass (ω=1) to capture port branch indices and build a
    /// branch-index→component-name map for singularity diagnostics.
    /// Two-phase: non-mutual first so LastBranchIndex is stable when mutuals stamp.
    /// </summary>
    private static (List<PortEntry> Ports, Dictionary<int, string> BranchLabels)
        CollectPortsAndBranchLabels(ElaboratedNetlist netlist, int nonGroundNodes)
    {
        var tempMna      = new MnaSystem(nonGroundNodes);
        var branchLabels = new Dictionary<int, string>();

        foreach (var ec in netlist.Components)
        {
            if (ec.Model is MutualInductanceModel) continue;
            // Buried s-param ports: skip in the preliminary stamp pass (Layer 2 scoping rule).
            if (IsSParamPort(ec.Model) && ec.InstancePath.Contains('.')) continue;
            int before = tempMna.BranchCount;
            // P1Tone: stamp as a 0 V branch (same as Term) so LastBranchIndex is captured for
            // the legacy path. Its own S-param Z-port stamp must NOT be called here.
            if (ec.Model is P1ToneModel p1)
                p1.StampAsSParamPort(tempMna, ec);
            else
                ec.Model.Stamp(tempMna, ec, 1.0);
            for (int b = before; b < tempMna.BranchCount; b++)
                branchLabels[tempMna.NodeCount + b] = $"{ec.ComponentType}:{ec.InstancePath}";
        }

        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel)
                ec.Model.Stamp(tempMna, ec, 1.0);

        var ports = new List<PortEntry>();
        foreach (var ec in netlist.Components)
        {
            // Only top-level s-param port components (no dot in path) become S-param ports.
            if (IsSParamPort(ec.Model) && ec.InstancePath.Contains('.')) continue;
            if (ec.Model is PortModel pm)
                ports.Add(new PortEntry(GetPortNum(ec), GetZ0(ec), pm.LastBranchIndex, ec.Nodes[0], ec.Nodes[1]));
            else if (ec.Model is TermModel tm)
                ports.Add(new PortEntry(GetPortNum(ec), GetZ0(ec), tm.LastBranchIndex, ec.Nodes[0], ec.Nodes[1]));
            else if (ec.Model is P1ToneModel p1)
                ports.Add(new PortEntry(GetPortNum(ec), GetZ0(ec), p1.LastBranchIndex, ec.Nodes[0], ec.Nodes[1]));
        }

        ports.Sort((a, b) => a.PortNum.CompareTo(b.PortNum));
        return (ports, branchLabels);
    }

    private static int GetPortNum(ElaboratedComponent ec)
    {
        if (!ec.Parameters.TryGetValue("Num", out var v))
            throw new InvalidOperationException(
                $"{ec.InstancePath}: Port/Term is missing the Num parameter.");
        return (int)v.AsReal();
    }

    private static Complex GetZ0(ElaboratedComponent ec)
    {
        if (!ec.Parameters.TryGetValue("Z", out var v))
            return new Complex(50, 0);
        return v.Kind == CircuitRF.Core.Expressions.ValueKind.Complex
            ? v.AsComplex()
            : new Complex(v.AsReal(), 0);
    }
}
