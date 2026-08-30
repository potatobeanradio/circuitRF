using System.Numerics;
using System.Runtime.ExceptionServices;
using CircuitRF.Core;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
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

    /// <param name="control">
    /// Optional cancellation + progress. Checked (and, when it carries a progress sink, ticked) once
    /// per FREQUENCY POINT — never inside a factorization or a port back-substitution. Null keeps the
    /// pre-cancellation behaviour exactly.
    /// </param>
    public static DataSet Run(
        ElaboratedNetlist netlist,
        double[]          freqsHz,
        AnalysisSettings? settings = null,
        RunControl?       control  = null)
    {
        settings ??= AnalysisSettings.Default;

        var prep      = Prepare(netlist, freqsHz, settings);
        var sMatrices = new Mat<Complex>[freqsHz.Length];
        RunRange(prep, freqsHz, settings, 0, freqsHz.Length, sMatrices, control, abort: null);
        return BuildDataSet(freqsHz, sMatrices, prep.Z0PerPort);
    }

    // ── Frequency-parallel overload (SP-P3) ───────────────────────────────────

    /// <summary>
    /// The same sweep, with contiguous chunks of the frequency grid solved at once on separately
    /// elaborated copies of <paramref name="tb"/>.
    ///
    /// <para><b>The copies are the whole thread-safety story.</b> Nine models write a branch index
    /// during <c>Stamp</c>, <see cref="SnpModel"/> loads its file lazily, several microstrip models
    /// accumulate warnings, and an SDD carries resolved control-branch indices — all state on the
    /// MODEL, all written at every frequency. Every one of those writes the same value on every
    /// thread (the topology does not depend on ω), so the race is benign and it is still a race.
    /// Rather than make <c>Stamp</c> re-entrant across every model in the repository, each worker
    /// gets a netlist nothing else touches; elaboration costs 57 µs for Hero 1 and 1.7 ms for a
    /// 200-node ladder, against a sweep long enough to be worth splitting at all.</para>
    ///
    /// <para><b>The result is bit-identical to the serial path at every degree.</b> Each point's
    /// arithmetic is unchanged and each chunk writes only its own slice of the output array, so
    /// nothing is merged and nothing is reordered. Two things need care and get it: the run-time
    /// warnings each copy accumulates are folded back into <paramref name="netlist"/> in chunk
    /// order, and a failing point throws the exception the serial path would have thrown — the one
    /// from the LOWEST frequency index that failed, not whichever thread lost the race.</para>
    ///
    /// <para><paramref name="netlist"/> is the caller's own, already elaborated: it runs the first
    /// chunk, keeps the merged diagnostics, and is never disposed here. Passing it rather than
    /// re-elaborating it is what lets a caller go on reading <c>Warnings</c> as it always has.</para>
    /// </summary>
    /// <param name="maxDegreeOfParallelism">0 = consult <see cref="AnalysisSettings.MaxParallelism"/>
    /// (itself 0 = automatic); 1 pins the serial path; &gt;1 caps the worker count.</param>
    public static DataSet Run(
        ElaboratedNetlist netlist,
        Library           lib,
        TestBench         tb,
        string?           baseDirectory,
        double[]          freqsHz,
        AnalysisSettings? settings = null,
        RunControl?       control  = null,
        int               maxDegreeOfParallelism = 0)
    {
        settings ??= AnalysisSettings.Default;

        int requested = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : settings.MaxParallelism;
        int degree    = PlanDegree(netlist, freqsHz.Length, requested);
        if (degree <= 1) return Run(netlist, freqsHz, settings, control);

        int freqCount = freqsHz.Length;
        var sMatrices = new Mat<Complex>[freqCount];
        var preps     = new Prepared[degree];
        var extras    = new ElaboratedNetlist?[degree - 1];

        try
        {
            // Elaborated SERIALLY, before any worker starts: the Elaborator reads the TestBench's
            // global-variable list, which a parametric sweep is mutating around this very call.
            preps[0] = Prepare(netlist, freqsHz, settings);
            for (int i = 1; i < degree; i++)
            {
                var copy = new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
                extras[i - 1] = copy;
                preps[i]      = Prepare(copy, freqsHz, settings);
            }

            // A chunk that fails records WHERE and stops; the others notice and stop too, so a
            // cancelled or singular run does not go on solving points nobody will read.
            var faults  = new Exception?[degree];
            int faulted = 0;
            Func<bool> abort = () => Volatile.Read(ref faulted) != 0;

            Parallel.For(0, degree, new ParallelOptions { MaxDegreeOfParallelism = degree }, c =>
            {
                var (lo, hi) = ChunkRange(freqCount, degree, c);
                try
                {
                    RunRange(preps[c], freqsHz, settings, lo, hi, sMatrices, control, abort);
                }
                catch (Exception ex)
                {
                    faults[c] = ex;
                    Volatile.Write(ref faulted, 1);
                }
            });

            // Chunks are contiguous and in order, so the lowest faulting CHUNK holds the lowest
            // faulting frequency — which is the point the serial loop would have died on.
            foreach (var fault in faults)
                if (fault is not null)
                    ExceptionDispatchInfo.Capture(fault).Throw();

            // Chunk order, first occurrence winning: the reported list is what a serial run reports.
            for (int i = 0; i < extras.Length; i++)
                if (extras[i] is { } copy) netlist.MergeDiagnosticsFrom(copy);

            return BuildDataSet(freqsHz, sMatrices, preps[0]!.Z0PerPort);
        }
        finally
        {
            foreach (var copy in extras) copy?.Dispose();
        }
    }

    /// <summary>Minimum frequency points a worker must be given before splitting is worth the
    /// elaboration and the thread start. Hero 1 costs ~5 µs a point, so 64 points is ~0.3 ms —
    /// comparable to one elaboration of a circuit big enough to care.</summary>
    public const int MinPointsPerWorker = 64;

    /// <summary>
    /// How many workers this netlist and grid will actually be run with. Pure and public to the
    /// tests, because "did it take the serial path?" is otherwise only answerable by timing.
    /// </summary>
    /// <param name="maxDegree">0 = automatic, 1 = pinned serial, &gt;1 = a cap.</param>
    public static int PlanDegree(ElaboratedNetlist netlist, int freqCount, int maxDegree)
    {
        if (maxDegree == 1)              return 1;
        if (!CanRunInParallel(netlist))  return 1;

        int cap = maxDegree > 1 ? maxDegree : Environment.ProcessorCount;
        return Math.Max(1, Math.Min(cap, freqCount / MinPointsPerWorker));
    }

    /// <summary>
    /// Whether this netlist may be elaborated more than once for one run.
    ///
    /// <para><b>An external device may not.</b> Its instance is a slot in a WORKER PROCESS, one per
    /// kit rather than one per thread, so T copies would ask that process for T times the instances
    /// and then serialize on its channel anyway — paying the cost of parallelism for none of it.</para>
    ///
    /// <para><b>A control-referencing SDD may not, in this revision.</b> Nothing about it is unsafe:
    /// <c>ResolveSParamControlBranches</c> simply runs per netlist and its test surface is small, so
    /// it stays on the path it has always run on until this one has some use behind it.</para>
    /// </summary>
    private static bool CanRunInParallel(ElaboratedNetlist netlist)
    {
        foreach (var ec in netlist.Components)
        {
            if (ec.Model is ExternalDeviceModel) return false;
            if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0) return false;
        }
        return true;
    }

    /// <summary>Half-open range of frequency indices chunk <paramref name="chunk"/> owns. The first
    /// <c>count % degree</c> chunks carry one extra point, so the split is contiguous and covers the
    /// grid exactly whatever the remainder.</summary>
    private static (int Lo, int Hi) ChunkRange(int count, int degree, int chunk)
    {
        int size = count / degree, rem = count % degree;
        int lo   = chunk * size + Math.Min(chunk, rem);
        int hi   = lo + size + (chunk < rem ? 1 : 0);
        return (lo, hi);
    }

    private static DataSet BuildDataSet(double[] freqsHz, Mat<Complex>[] sMatrices, Complex[] z0PerPort)
    {
        var refZ0 = z0PerPort.Length > 0 ? z0PerPort[0] : new Complex(50, 0);
        var snp   = new SNP(freqsHz, sMatrices, MatrixType.S, MatrixFormat.RI, refZ0);
        var ds    = DataSetBuilder.FromSnp(snp);            // S cube + uniform Z0 placeholder
        ds.Add("Z0", DataSetBuilder.BuildZ0Cube(z0PerPort)); // overwrite with per-port truth
        return ds;
    }

    // ── Per-netlist setup ─────────────────────────────────────────────────────

    /// <summary>
    /// Everything one netlist needs before its first frequency: the ports, the singularity namers,
    /// its own <see cref="MnaSystem"/>, the DC operating point the nonlinear devices linearize at,
    /// and the SDD control-branch resolution. Invariant across ω, so it is done once per netlist —
    /// which for a parallel run means once per worker, on state nothing else can see.
    /// </summary>
    private sealed class Prepared
    {
        internal ElaboratedNetlist  Netlist           = null!;
        internal List<PortEntry>    Ports             = null!;
        internal Complex[]          Z0PerPort         = null!;
        internal bool               AllPortsResistive;
        internal MnaSystem          Mna               = null!;
        internal Func<int, string>  NodeNamer         = null!;
        internal Func<int, string>  BranchNamer       = null!;
        internal bool               CanRetry;
        internal double[]?          DcNodeVoltages;
    }

    private static Prepared Prepare(
        ElaboratedNetlist netlist, double[] freqsHz, AnalysisSettings settings)
    {
        // ── Identify ports + build branch-label map ───────────────────────────
        int nonGroundNodes = netlist.Nodes.Count - 1;
        var (ports, branchLabels) = CollectPortsAndBranchLabels(netlist, nonGroundNodes);
        if (ports.Count == 0)
            throw new InvalidOperationException(
                "S-parameter analysis requires at least one Port, Term, or P1Tone component at the testbench top level. " +
                "Place Term or P1Tone components (Num=1, Z=50 Ohm) directly in the testbench, not inside sub-cells.");
        int N = ports.Count;

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
        // Per NETLIST, which for a parallel run means once per worker: NonlinearDcEngine is
        // deterministic for a given netlist, so every copy reaches the same operating point and the
        // chunks linearize about the same bias (Engine.Tests pins this rather than assuming it).
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
                ResetSddControlBias(netlist);  // consistent 0 V seed for the control sensitivities
            }
        }

        // Resolve each control-using SDD's referenced branch index against THIS (S-param) assembly.
        // Must happen before the frequency loop — the SDD reads ControlBranchIndices when it stamps.
        ResolveSParamControlBranches(netlist, freqsHz, allPortsResistive, ports, N, dcNodeVoltages, settings);

        return new Prepared
        {
            Netlist           = netlist,
            Ports             = ports,
            Z0PerPort         = z0PerPort,
            AllPortsResistive = allPortsResistive,
            Mna               = new MnaSystem(nonGroundNodes),
            NodeNamer         = nodeNamer,
            BranchNamer       = branchNamer,
            CanRetry          = canRetry,
            DcNodeVoltages    = dcNodeVoltages,
        };
    }

    /// <summary>Solves the half-open frequency range [lo, hi) into <paramref name="sMatrices"/>,
    /// which it writes BY INDEX and therefore shares with no other range.</summary>
    private static void RunRange(
        Prepared         p,
        double[]         freqsHz,
        AnalysisSettings settings,
        int              lo,
        int              hi,
        Mat<Complex>[]   sMatrices,
        RunControl?      control,
        Func<bool>?      abort)
    {
        if (p.AllPortsResistive)
            RunWavePath(p, freqsHz, settings, lo, hi, sMatrices, control, abort);
        else
            RunLegacyPath(p, freqsHz, settings, lo, hi, sMatrices, control, abort);
    }

    // ── Wave path (Re(Z0) > 0 for every port) ─────────────────────────────────

    private static void RunWavePath(
        Prepared         p,
        double[]         freqsHz,
        AnalysisSettings settings,
        int              lo,
        int              hi,
        Mat<Complex>[]   sMatrices,
        RunControl?      control,
        Func<bool>?      abort)
    {
        var netlist        = p.Netlist;
        var ports          = p.Ports;
        int N              = ports.Count;
        var mna            = p.Mna;
        var nodeNamer      = p.NodeNamer;
        var branchNamer    = p.BranchNamer;
        bool canRetry      = p.CanRetry;
        var dcNodeVoltages = p.DcNodeVoltages;

        int nonGroundNodes = mna.NodeCount;
        var xBuf = Array.Empty<Complex>();
        var bBuf = Array.Empty<Complex>();

        for (int fi = lo; fi < hi; fi++)
        {
            // Another chunk has already failed; nothing will read the rest of this one.
            if (abort is not null && abort()) return;

            // One frequency is this loop's work unit and its cancellation boundary alike.
            control?.Tick();

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
            if (xBuf.Length != mna.Size) { xBuf = new Complex[mna.Size]; bBuf = new Complex[mna.Size]; }

            for (int j = 0; j < N; j++)
            {
                // Incident wave (unit a_j = 1): Norton current I_j = 2√(Re Z0_j) / Z0_j.
                double  sqrtReZ0j = Math.Sqrt(ports[j].Z0.Real);
                Complex iInj      = 2.0 * sqrtReZ0j / ports[j].Z0;

                // RHS: current injection at the driven port nodes. One buffer, cleared per port —
                // the wave path's RHS is zero everywhere but the two driven rows.
                var b = bBuf;
                Array.Clear(b);
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
        Prepared         p,
        double[]         freqsHz,
        AnalysisSettings settings,
        int              lo,
        int              hi,
        Mat<Complex>[]   sMatrices,
        RunControl?      control,
        Func<bool>?      abort)
    {
        var netlist        = p.Netlist;
        var ports          = p.Ports;
        int N              = ports.Count;
        var z0PerPort      = p.Z0PerPort;
        var mna            = p.Mna;
        var nodeNamer      = p.NodeNamer;
        var branchNamer    = p.BranchNamer;
        bool canRetry      = p.CanRetry;
        var dcNodeVoltages = p.DcNodeVoltages;

        int nonGroundNodes = mna.NodeCount;
        var xBuf = Array.Empty<Complex>();
        var bBuf = Array.Empty<Complex>();

        for (int fi = lo; fi < hi; fi++)
        {
            // Another chunk has already failed; nothing will read the rest of this one.
            if (abort is not null && abort()) return;

            // One frequency is this loop's work unit and its cancellation boundary alike.
            control?.Tick();

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
            if (xBuf.Length != mna.Size) { xBuf = new Complex[mna.Size]; bBuf = new Complex[mna.Size]; }

            for (int j = 0; j < N; j++)
            {
                var b = bBuf;
                mna.FillRhsWithPortDrive(b, branchRow: ports[j].BranchIndex, driveValue: Complex.One);

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
                ec.StampLinearized(mna, omega, BuildBias(ec, dcNodeVoltages));
                continue;
            }

            ec.Stamp(mna, omega);
            netlist.DrainModelWarnings(ec.Model);
        }
        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel)
            {
                ec.Stamp(mna, omega);
                netlist.DrainModelWarnings(ec.Model);
            }
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

    // ── SDD control-current column: resolve referenced branches in the S-param matrix ──────────

    /// <summary>Zero every control-using SDD's bias seed (DC-nonconverged fallback → 0 V linearization).</summary>
    private static void ResetSddControlBias(ElaboratedNetlist netlist)
    {
        foreach (var ec in netlist.Components)
            if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0)
                Array.Clear(sdd.ControlBias);
    }

    /// <summary>
    /// Resolve each control-using SDD's referenced branch index (C[n]) against the S-parameter
    /// assembly. Branch numbering differs from the DC/HB matrices (the wave path skips ports; legacy
    /// stamps 0 V port branches), so a throwaway pass replicating the chosen path's StampAll is run
    /// into a temp MNA — every referenced device's LastBranchIndex / PortBranchIndices then matches
    /// what the real per-frequency StampAll produces (topology-invariant across ω). The resolved
    /// indices are written into each SDD's ControlBranchIndices for the run. No-op when no SDD has
    /// control references (so purely-linear and control-free SDD runs are byte-identical).
    /// </summary>
    private static void ResolveSParamControlBranches(
        ElaboratedNetlist netlist, double[] freqsHz, bool wavePath,
        List<PortEntry> ports, int N, double[]? dcNodeVoltages, AnalysisSettings settings)
    {
        var sdds = netlist.Components
            .Where(ec => ec.Model is SddModel s && s.ControlRefs.Length > 0)
            .ToList();
        if (sdds.Count == 0) return;

        // Reset so the SDD's own control column is skipped during the throwaway resolution pass.
        foreach (var ec in sdds)
        {
            var s = (SddModel)ec.Model;
            for (int i = 0; i < s.ControlBranchIndices.Length; i++) s.ControlBranchIndices[i] = -1;
        }

        // Replicate the real solve assembly so referenced-device branch indices match the solve.
        double omega = freqsHz.Length > 0 ? 2.0 * Math.PI * freqsHz[0] : 1.0;
        var temp = new MnaSystem(netlist.Nodes.Count - 1);
        StampAll(temp, netlist, omega, skipPorts: wavePath, dcNodeVoltages: dcNodeVoltages);
        if (wavePath) StampPortConductances(temp, ports, N);

        foreach (var ec in sdds)
        {
            var sdd = (SddModel)ec.Model;
            for (int i = 0; i < sdd.ControlRefs.Length; i++)
            {
                var (n, refInst, port) = sdd.ControlRefs[i];
                ElaboratedComponent? target = null;
                foreach (var cc in netlist.Components)
                    if (string.Equals(cc.InstancePath, refInst, StringComparison.Ordinal)) { target = cc; break; }
                if (target is null)
                    throw new InvalidOperationException(
                        $"SDD '{sdd.Name}': C[{n}]={refInst} — no sibling component named '{refInst}' " +
                        $"found in the netlist.");
                sdd.ControlBranchIndices[i] = ResolveSParamBranchIndex(sdd.Name, n, port, target);
            }
        }
    }

    /// <summary>Map a referenced device (by kind + optional port) to its S-param-MNA branch index.</summary>
    private static int ResolveSParamBranchIndex(string sddName, int n, int port, ElaboratedComponent target)
    {
        int br = target.Model switch
        {
            VdcModel        vdc => vdc.LastBranchIndex,
            ToneSourceModel ton => ton.LastBranchIndex,
            // An ideal current source's current is an INPUT, not a solved unknown — it allocates no
            // branch to point at. Named explicitly because "the other tone source works" is exactly
            // the wrong inference to leave the user to draw from a generic list of allowed kinds.
            CurrentToneSourceModel => throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}]={target.InstancePath} is an ideal current source (I_1Tone/I_nTone): " +
                $"its current is an input, not a solved unknown, so it has no branch to reference. " +
                $"Put an IProbe in series with it and reference that instead."),
            IProbeModel   probe => probe.LastBranchIndex,
            InductorModel   ind => ind.LastBranchIndex,
            SnpModel        snp => PortBranch(snp.PortBranchIndices, port),
            ZPortModel       zp => PortBranch(zp.PortBranchIndices, port),
            _ => throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}]={target.InstancePath} references a '{target.ComponentType}' " +
                $"which is not a referenceable device class (Vdc, V_1Tone/V_nTone, IProbe, L, SnP, Z_Port).")
        };
        if (br < 0)
            throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}]={target.InstancePath} — referenced device allocated no branch " +
                $"in the S-parameter matrix.");
        return br;

        static int PortBranch(int[] indices, int port)
            => (port >= 1 && port <= indices.Length) ? indices[port - 1] : -1;
    }

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
                ec.Stamp(tempMna, 1.0);
            netlist.DrainModelWarnings(ec.Model);
            for (int b = before; b < tempMna.BranchCount; b++)
                branchLabels[tempMna.NodeCount + b] = $"{ec.ComponentType}:{ec.InstancePath}";
        }

        foreach (var ec in netlist.Components)
            if (ec.Model is MutualInductanceModel)
            {
                ec.Stamp(tempMna, 1.0);
                netlist.DrainModelWarnings(ec.Model);
            }

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
