using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;
using CSparse.Ordering;
using CSparse.Storage;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;

namespace CircuitRF.Engine;

/// <summary>
/// Nonlinear-DC Newton solver (nonlinear-dc §4).
///
/// State vector x = [V₁ … V_n | I_b₀ … I_b_{m-1}]
///   - Voltage unknowns: non-ground nodes (circuit nodes 1..n → matrix rows 0..n-1)
///   - Branch-current unknowns: voltage-source and inductor branches added by linear stamps
///
/// Residual:  F(x) = G_aug·x + I_nl(x) − b_source ≈ 0
///   - G_aug: constant linear conductance + branch equations from linear stamps (voltage sources etc.)
///   - I_nl:  nonlinear device port currents (from Evaluate), stamped into voltage rows only
///   - b_source: RHS from independent voltage/current sources
///
/// Jacobian:  J = G_aug + dg(x)  (dg stamped at device's node pairs each iteration)
///
/// gmin continuity: small shunt gmin to ground added to every voltage row.
/// Source-stepping: walk sources 0→1 in steps; step-halving backoff on max-iter.
/// Convergence: ‖F‖ < AbsTol (default 1e-6).
/// </summary>
public sealed class NonlinearDcEngine
{
    public const double DefaultGmin = 1e-12;  // S — also used as Gmin when settings not specified
    public const double DefaultVTol = 1e-9;   // V — update-norm convergence check

    // ── Convergence trace (permanent diagnostic feature) ─────────────────────

    /// <summary>Per-iteration data for one Newton step.</summary>
    public record IterationRecord(int Iter, double ResidualNorm, double UpdateNorm, double Lambda = 1.0);

    /// <summary>Per-continuation-step summary.</summary>
    public record StepRecord(double SourceFraction, int Iterations, bool Converged,
        IReadOnlyList<IterationRecord> IterationTrace);

    /// <summary>
    /// Full convergence trace from a nonlinear-DC run.
    /// Permanent diagnostic feature — included in every DcResult.
    /// Inspect to determine whether Newton is in quadratic regime or only linear.
    /// </summary>
    public sealed class ConvergenceTrace
    {
        private readonly List<StepRecord> _steps = [];
        public IReadOnlyList<StepRecord> Steps => _steps;
        internal void Add(StepRecord s) => _steps.Add(s);
        public int TotalContinuationSteps => _steps.Count;
        public int TotalNewtonIterations  => _steps.Sum(s => s.Iterations);

        /// <summary>The damping policy currently in use (λ always 1 = full Newton step in v1).</summary>
        public string DampingPolicy => "λ = 1 (full Newton step, no damping — v1)";
    }

    // ── Result ────────────────────────────────────────────────────────────────

    public sealed class DcResult
    {
        /// <summary>Node voltages (index 0 = circuit node 1). Length = non-ground node count.</summary>
        public double[] NodeVoltages  { get; }
        public bool     Converged     { get; }
        public int      Iterations    { get; }
        public double   FinalResidual { get; }

        /// <summary>Full per-step, per-iteration convergence trace.</summary>
        public ConvergenceTrace Trace { get; }

        /// <summary>
        /// DC branch current through each IProbe, keyed by the probe's instance path (e.g. "IPd").
        /// Sign convention: positive current flows from the probe's first node to its second
        /// (IProbe:IPd n_plus n_minus → positive = n_plus → n_minus), matching MnaSystem.AddBranchCurrent.
        /// Empty when the circuit has no IProbes.
        /// </summary>
        public IReadOnlyDictionary<string, double> ProbeCurrents { get; }

        /// <summary>
        /// The residual PER UNKNOWN at the point the solve stopped — the KCL error each row still
        /// carries. Index 0 is circuit node 1, matching <see cref="NodeVoltages"/>; entries past the
        /// node count are branch unknowns.
        ///
        /// <para><b>Why the norm alone was not enough.</b> A failure could only say "residual 35.6",
        /// which is a number with no address: it names neither the part of the circuit that will not
        /// settle nor how far off it is. In a design with hundreds of unknowns that leaves bisecting
        /// the schematic as the only way forward. One row is almost always enormously worse than the
        /// rest, and saying which it is turns the whole search into a sentence.</para>
        /// </summary>
        public double[] ResidualPerUnknown { get; }

        /// <summary>
        /// Which component owns each branch-current unknown, keyed by its row in
        /// <see cref="ResidualPerUnknown"/>. A branch has no node name to fall back on, so without
        /// this a report can only give its index — an address in the matrix rather than in the
        /// circuit.
        /// </summary>
        public IReadOnlyDictionary<int, string> BranchOwners { get; }

        internal DcResult(double[] v, bool converged, int iters, double residual,
            ConvergenceTrace trace, IReadOnlyDictionary<string, double> probeCurrents,
            double[]? residualPerUnknown = null,
            IReadOnlyDictionary<int, string>? branchOwners = null)
        {
            NodeVoltages       = v;
            Converged          = converged;
            Iterations         = iters;
            FinalResidual      = residual;
            Trace              = trace;
            ProbeCurrents      = probeCurrents;
            ResidualPerUnknown = residualPerUnknown ?? [];
            BranchOwners       = branchOwners ?? new Dictionary<int, string>();
        }
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    public static DcResult Run(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
        => new NonlinearDcEngine(netlist, settings ?? AnalysisSettings.Default).Solve();

    // ── Implementation ────────────────────────────────────────────────────────

    private readonly ElaboratedNetlist _nl;
    private readonly AnalysisSettings  _settings;
    private readonly int   _nodeCount;     // non-ground voltage unknowns
    private readonly int   _systemSize;    // nodeCount + branch unknowns
    private readonly Dictionary<int, string> _branchOwners = [];  // matrix row → owning component
    private readonly double[,] _gAug;      // linear conductance / branch matrix (constant)
    private readonly double[]  _bSource;   // RHS source vector (scaled by sourceFrac)

    /// <summary>
    /// Matrix rows carrying a TEMPERATURE rather than a voltage, and the device that says so.
    ///
    /// <para>An electrothermal device presents its junction temperature as an ordinary node — the
    /// model drives a current numerically equal to its dissipated power into it, and reads the
    /// resulting node value back as degrees C. Everything about it is a node to the solver, which
    /// is exactly why it needs naming: a step that would be unremarkable for a voltage takes a
    /// temperature below absolute zero, which no model can evaluate.</para>
    /// </summary>
    private readonly Dictionary<int, string> _thermalNodes = [];

    private NonlinearDcEngine(ElaboratedNetlist nl, AnalysisSettings settings)
    {
        _nl        = nl;
        _settings  = settings;
        _nodeCount = nl.Nodes.Count - 1;  // nodes 1..N; node 0 = ground excluded

        // Build the linear part using MnaSystem at ω=0.
        // MnaSystem gives us the augmented (nodes + branches) system.
        var mna = new MnaSystem(_nodeCount);
        mna.Reset();
        foreach (var ec in nl.Components)
        {
            if (ec.Model.Kind != ModelKind.Linear) continue;
            // Term/Port branches are driven ports for S-parameter analysis only; inert in DC.
            if (ec.Model is PortModel or TermModel) continue;

            // WHICH COMPONENT OWNS WHICH BRANCH ROW. A branch unknown has no node name to fall back
            // on, so without this a failure can only call it "branch unknown #60" — which is an
            // address in a matrix, not in the circuit the user drew. Recorded by watching the branch
            // count across each stamp, because allocation is the model's own business and nothing
            // else knows how many it took.
            int before = mna.Size;
            try { ec.Stamp(mna, omega: 0.0); }
            catch (NotImplementedException) { }
            for (int b = before; b < mna.Size; b++)
                _branchOwners[b] = $"{ec.ComponentType}:{ec.InstancePath}";

            nl.DrainModelWarnings(ec.Model);
        }

        _systemSize = mna.Size;  // nodeCount + branchCount
        _gAug       = new double[_systemSize, _systemSize];
        _bSource    = new double[_systemSize];

        for (int i = 0; i < _systemSize; i++)
        {
            _bSource[i] = mna.GetRhs(i).Real;
            for (int k = 0; k < _systemSize; k++)
                _gAug[i, k] = mna.GetEntry(i, k).Real;
        }

        // Resolve control-current branch indices for any SDD with C[n] references.
        // Branch indices are now assigned (the stamp loop above ran all linear devices).
        ResolveControlCurrentBranches();

        CollectThermalNodes();
        ReportThermalNodes();
    }

    /// <summary>
    /// Which matrix rows are temperatures, from the devices that own them.
    ///
    /// <para>Only an external device can say this: the kind is MEASURED by the worker when it probes
    /// the model (a pin with no conductive coupling to any other node carries heat, not current) and
    /// arrives on the descriptor. Nothing about the netlist reveals it — a thermal node is spelled
    /// exactly like any other.</para>
    /// </summary>
    private void CollectThermalNodes()
    {
        foreach (var ec in _nl.Components)
        {
            if (ec.Model is not ExternalDeviceModel ed) continue;

            foreach (var node in ed.Descriptor.Nodes)
            {
                if (node.QuantityKind != NodeQuantityKind.Thermal) continue;

                // Same port→node mapping the residual uses: port p spans Nodes[2p] and Nodes[2p+1].
                int p  = node.Index;
                int np = ec.Nodes.Length > 2 * p ? ec.Nodes[2 * p] : 0;
                if (np <= 0 || np - 1 >= _nodeCount) continue;   // grounded, or not an unknown

                _thermalNodes.TryAdd(np - 1, $"{ec.ComponentType}:{ec.InstancePath}");
            }
        }
    }

    /// <summary>
    /// Above this, a thermal resistance is not a thermal resistance. Defined in
    /// <see cref="Temperature.ImplausibleThermalResistanceCPerW"/>, which the elaborator draws the
    /// same line from; kept named here because it is part of this engine's public surface.
    /// </summary>
    public const double ImplausibleThermalResistance = Temperature.ImplausibleThermalResistanceCPerW;

    /// <summary>
    /// How far a thermal node's reference may sit from the ambient before it is not the ambient.
    /// A degree is well below any real difference between an ambient and a heatsink base, and well
    /// above the numerical slack in reading one out of a linear solve.
    /// </summary>
    private const double AmbientReferenceSlackC = 1.0;

    /// <summary>What the circuit alone does to one thermal node, with the device contributing nothing.</summary>
    /// <param name="ReferenceC">
    /// The temperature the node is held at with no dissipation — what the network references it to.
    /// </param>
    /// <param name="ResistanceCPerW">
    /// The driving-point thermal resistance seen looking into the node: the rise it takes per watt.
    /// </param>
    private readonly record struct ThermalMeasurement(double ReferenceC, double ResistanceCPerW);

    /// <summary>
    /// Measures what the circuit does to each thermal node, from the linear network the components
    /// already stamped. Empty when that network has no solution to read.
    ///
    /// <para><b>Both numbers come out of one factorization, and both are solves rather than
    /// estimates.</b> The reference is the linear solve itself — the nonlinear devices left out IS
    /// "no dissipation", so what a thermal row reads there is the temperature the network holds it
    /// at. The resistance is the same matrix against a unit current injected at that row, which is
    /// the driving-point resistance by definition.</para>
    ///
    /// <para><b>Why the resistance is not read off the diagonal.</b> `1/G[row,row]` looks like a
    /// safe lower bound and is not one: a node held by an IDEAL source has no conductance on its
    /// diagonal at all, because a voltage source lives in a branch row. The diagonal reads zero, the
    /// bound reads infinity, and the best-referenced node possible — a perfect isothermal boundary,
    /// which is exactly how a bench pins a part at a stated case temperature — draws the loudest
    /// "no path at all" warning in the file. Observed on a kit's own reference bench, where the
    /// answer was correct to six figures. A solve has no such blind spot.</para>
    /// </summary>
    private Dictionary<int, ThermalMeasurement> MeasureThermalNodes()
    {
        var measured = new Dictionary<int, ThermalMeasurement>();
        if (_thermalNodes.Count == 0) return measured;

        var j = new List<(int R, int C, double V)>(_systemSize * 4);
        for (int i = 0; i < _systemSize; i++)
            for (int k = 0; k < _systemSize; k++)
            {
                double g = _gAug[i, k];
                if (i == k && i < _nodeCount) g += DefaultGmin;
                if (g != 0.0) j.Add((i, k, g));
            }

        var x     = new double[_systemSize];
        var probe = new double[_systemSize];
        try
        {
            var csc  = AssembleCsc(j, _systemSize);
            var perm = AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtA);
            var lu   = SparseLU.Create(csc, perm, 1.0);
            if (lu is null) return measured;

            lu.Solve(_bSource, x);

            foreach (int row in _thermalNodes.Keys)
            {
                if (row >= _systemSize || !double.IsFinite(x[row])) continue;

                Array.Clear(probe);
                probe[row] = 1.0;                       // one watt in, at this node only
                var rise = new double[_systemSize];
                lu.Solve(probe, rise);
                if (!double.IsFinite(rise[row])) continue;

                measured[row] = new ThermalMeasurement(x[row], Math.Abs(rise[row]));
            }
        }
        catch
        {
            // A diagnostic that cannot be computed is not a failure to report — the solve itself is
            // what has to work here, and it has its own singularity handling.
            return measured;
        }

        return measured;
    }

    /// <summary>
    /// Reports the two ways a thermal node ends up somewhere nothing intended: too weakly referenced
    /// to be a thermal path at all, and referenced to GROUND rather than to the ambient.
    ///
    /// <para><b>Both are worth a warning and neither is a failure.</b> In both cases the network is
    /// well conditioned, the solve converges in the same number of iterations, and every number that
    /// comes back is finite and plausible. What is wrong is the temperature the model was evaluated
    /// at, and there is no symptom to notice — which is precisely the case a user cannot see and the
    /// solver cannot object to.</para>
    ///
    /// <para><b>Weakly referenced</b> — the device's own dissipated power sets the node to P × R, and
    /// with R at keep-alive scale that is tens of thousands of degrees, which a model clamps and
    /// evaluates without complaint.</para>
    ///
    /// <para><b>Referenced to ground</b> — the standard electrothermal sub-network is a thermal
    /// resistance and capacitance in parallel, connected NOT to ground but to a source whose value is
    /// the ambient temperature; the node's voltage is then the junction temperature itself, ambient
    /// plus rise. Tie the same R to ground instead and the node carries the RISE alone, so the model
    /// runs short by the whole ambient — typically 25 °C, which on a part with real temperature
    /// coefficients moves the answer by several percent.</para>
    ///
    /// <para>The ground test is deliberately narrow. It is not "the reference differs from the
    /// ambient" — a heatsink base or a second thermal stage legitimately sits at its own temperature,
    /// and a check that fired there would go off loudest on the designs that modelled most carefully.
    /// It is "the reference is zero WHILE the ambient is not". A design whose ambient really is 0 °C
    /// says so and is silent. And a node that has no usable reference at all is left to the
    /// resistance warning above rather than being reported twice for one condition.</para>
    /// </summary>
    private void ReportThermalNodes()
    {
        var measured = MeasureThermalNodes();
        double ambient = _nl.AmbientC;

        foreach (var (row, owner) in _thermalNodes.OrderBy(e => e.Key))
        {
            string node = row + 1 < _nl.Nodes.Count ? _nl.Nodes.NameOf(row + 1) : $"node {row + 1}";

            if (!measured.TryGetValue(row, out var m)) continue;

            if (m.ResistanceCPerW > ImplausibleThermalResistance)
            {
                _nl.AddWarningOnce(
                    $"thermal-resistance:{owner}:{row}",
                    $"{owner}: the thermal node '{node}' reaches its reference only through " +
                    $"{(double.IsPositiveInfinity(m.ResistanceCPerW) ? "no path at all" : $"{m.ResistanceCPerW:G3} °C/W")}, " +
                    $"which is not a thermal resistance — real values are a few hundred °C/W at most. " +
                    $"The device's own dissipated power sets this node's temperature, so the model " +
                    $"will run at whatever that resistance implies. Connect it to the thermal network " +
                    $"the rest of the design uses.");
                continue;   // no usable reference — the reading below would be meaningless
            }

            if (Math.Abs(ambient) <= AmbientReferenceSlackC) continue;   // the ambient IS ground here
            if (Math.Abs(m.ReferenceC) > AmbientReferenceSlackC) continue;

            _nl.AddWarningOnce(
                $"thermal-ground-reference:{owner}:{row}",
                $"{owner}: the thermal node '{node}' is referenced to {m.ReferenceC:0.###} °C, but the " +
                $"design's ambient is {ambient:0.##} °C. A thermal network carries the junction " +
                $"temperature, so its resistance belongs between this node and a source holding the " +
                $"ambient — tied to ground instead, this node carries only the rise and the model " +
                $"runs {ambient:0.##} °C cold.");
        }
    }

    /// <summary>
    /// Resolve C[n] references to branch indices for all SDD components.
    /// Must run after the linear stamp loop so LastBranchIndex / PortBranchIndices are valid.
    /// </summary>
    private void ResolveControlCurrentBranches()
    {
        foreach (var ec in _nl.Components)
        {
            if (ec.Model is not SddModel sdd) continue;
            if (sdd.ControlRefs.Length == 0) continue;
            ResolveForSdd(sdd);
        }
    }

    private void ResolveForSdd(SddModel sdd)
    {
        for (int i = 0; i < sdd.ControlRefs.Length; i++)
        {
            var (n, refInst, port) = sdd.ControlRefs[i];

            // Find the sibling component by InstancePath.
            ElaboratedComponent? target = null;
            foreach (var ec in _nl.Components)
            {
                if (string.Equals(ec.InstancePath, refInst, StringComparison.Ordinal))
                {
                    target = ec;
                    break;
                }
            }

            if (target is null)
                throw new InvalidOperationException(
                    $"SDD '{sdd.Name}': C[{n}]={refInst} — no sibling component named '{refInst}' " +
                    $"found in the netlist. Check the instance name.");

            sdd.ControlBranchIndices[i] = GetControlBranchIndex(sdd.Name, n, port, target);
        }
    }

    private static int GetControlBranchIndex(string sddName, int n, int port, ElaboratedComponent target)
    {
        const string AllowedKinds = "Vdc, V_1Tone/V_nTone, IProbe, L (Inductor), SnP, Z_Port";
        return target.Model switch
        {
            VdcModel        vdc  => ValidateSinglePortBranch(sddName, n, port, vdc.LastBranchIndex,  "Vdc"),
            ToneSourceModel tone => ValidateSinglePortBranch(sddName, n, port, tone.LastBranchIndex, "V_1Tone/V_nTone"),
            IProbeModel probe => ValidateSinglePortBranch(sddName, n, port, probe.LastBranchIndex, "IProbe"),
            InductorModel ind => ValidateSinglePortBranch(sddName, n, port, ind.LastBranchIndex,   "Inductor"),
            SnpModel    snp   => ValidateMultiPortBranch(sddName, n, port, snp.PortBranchIndices,  "SnP"),
            ZPortModel  zp    => ValidateMultiPortBranch(sddName, n, port, zp.PortBranchIndices,   "Z_Port"),
            _ => throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}]={target.InstancePath} references a '{target.ComponentType}' " +
                $"which is not a referenceable device class. Allowed: {AllowedKinds}.")
        };
    }

    private static int ValidateSinglePortBranch(string sddName, int n, int port, int branchIdx, string kind)
    {
        if (port > 1)
            throw new InvalidOperationException(
                $"SDD '{sddName}': Cport[{n}]={port} — {kind} is a two-terminal device; " +
                $"Cport must be absent or 1.");
        if (branchIdx < 0)
            throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}] — {kind} branch index not yet assigned. " +
                $"Ensure the referenced device is stamped before resolution.");
        return branchIdx;
    }

    private static int ValidateMultiPortBranch(string sddName, int n, int port, int[] indices, string kind)
    {
        if (port == 0)
            throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}] references a {kind} device; " +
                $"Cport[{n}]=<port> is required for multi-port references.");
        if (port < 1 || port > indices.Length)
            throw new InvalidOperationException(
                $"SDD '{sddName}': Cport[{n}]={port} is out of range for {kind} device " +
                $"with {indices.Length} port(s). Valid range: 1..{indices.Length}.");
        if (indices[port - 1] < 0)
            throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}] — {kind} port {port} branch index not yet assigned.");
        return indices[port - 1];
    }

    /// <summary>
    /// Capture each SDD's control-current values at the converged DC operating point into
    /// SddModel.ControlBias (using the DC-resolved branch indices, still in place here). The
    /// S-parameter engine reads these to seed its small-signal linearization, then re-resolves
    /// ControlBranchIndices against its own MNA. No-op when no SDD has control references.
    /// </summary>
    private void CaptureControlBias(double[] xFull)
    {
        foreach (var ec in _nl.Components)
        {
            if (ec.Model is not SddModel sdd || sdd.ControlRefs.Length == 0) continue;
            for (int i = 0; i < sdd.ControlBranchIndices.Length; i++)
            {
                int br = sdd.ControlBranchIndices[i];
                sdd.ControlBias[i] = (br >= 0 && br < xFull.Length) ? xFull[br] : 0.0;
            }
        }
    }

    private IReadOnlyDictionary<string, double> ExtractProbeCurrents(double[] x)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var ec in _nl.Components)
        {
            if (ec.Model is not IProbeModel probe) continue;
            int br = probe.LastBranchIndex;
            if (br >= _nodeCount && br < _systemSize)
                map[ec.InstancePath] = x[br];
        }
        return map;
    }

    private DcResult Solve()
        => _settings.DcBiasStepping switch
        {
            DcBiasSteppingMode.Always      => SolveRamped(),
            DcBiasSteppingMode.Never       => SolveDirect(throwOnFailure: true),
            DcBiasSteppingMode.IfNecessary => SolveIfNecessary(),
            _                              => throw new ArgumentOutOfRangeException(nameof(_settings))
        };

    /// <summary>
    /// IfNecessary: try direct cold-start first; fall back to ramp if it fails.
    /// </summary>
    private DcResult SolveIfNecessary()
    {
        var direct = SolveDirect(throwOnFailure: false);
        if (direct.Converged) return direct;
        // Direct failed — fall back to ramped continuation.
        return SolveRamped();
    }

    /// <summary>
    /// Direct cold-start Newton solve (sources at full bias, single-step attempt).
    /// </summary>
    private DcResult SolveDirect(bool throwOnFailure)
    {
        double[] x = new double[_systemSize];   // cold start: all zero
        var      trace = new ConvergenceTrace();

        var (ok, iters, xNew, stepTrace) = NewtonAtFrac(x, 1.0);
        trace.Add(new StepRecord(1.0, iters, ok, stepTrace));

        double[] nodeV  = xNew[.._nodeCount];
        // A point the models refuse has no residual to report — asking for one throws the refusal
        // back out of the engine, which turns "did not converge" into an unhandled exception.
        double finalRes = SafeResidualVector(xNew, 1.0) is { } fv ? L2(fv) : double.PositiveInfinity;
        bool converged  = ok && finalRes < _settings.NonlinearAbsTol;

        if (!converged && throwOnFailure)
            throw new NonlinearDcNotConvergedException(iters, finalRes);

        CaptureControlBias(xNew);   // seed SDD.ControlBias for a downstream S-param linearization
        return new DcResult(nodeV, converged, iters, finalRes, trace, ExtractProbeCurrents(xNew));
    }

    /// <summary>
    /// Ramped continuation: walk sources 0→1 in DcBiasRampSteps equal steps with step-halving backoff.
    /// </summary>
    private DcResult SolveRamped()
    {
        double[] x          = new double[_systemSize];  // cold start: all zero
        int      rampSteps  = Math.Max(1, _settings.DcBiasRampSteps);
        double   targetFrac = 0.0;
        double   stepFrac   = 1.0 / rampSteps;
        int      totalIters = 0;
        var      trace      = new ConvergenceTrace();
        int      budget     = IterationBudget();

        while (targetFrac < 1.0 - 1e-12)
        {
            // THE BUDGET IS ENFORCED HERE, not just described in the settings. A ramp step that
            // succeeds only after halving leaves `targetFrac` barely moved, so the outer loop runs
            // again from almost the same place — and with nothing bounding it, a solve that creeps
            // can spend an unlimited number of Newton iterations arriving nowhere. Measured on a
            // real design whose thermal network cannot settle: 403,893 iterations, every one of them
            // a round trip to an external model, before the answer "did not converge" that was
            // already true at a few thousand.
            if (totalIters >= budget)
            {
                double[] nvOut     = x[.._nodeCount];
                double[] fOverrun  = SafeResidualVector(x, 1.0) ?? [];
                _lastX = x; ReportWorstResiduals(fOverrun);
                return new DcResult(nvOut, false, totalIters,
                                    fOverrun.Length > 0 ? L2(fOverrun) : double.PositiveInfinity, trace,
                                    ExtractProbeCurrents(x), fOverrun, _branchOwners);
            }

            double nextFrac = Math.Min(targetFrac + stepFrac, 1.0);
            bool   stepped  = false;

            for (int attempt = 0; attempt < _settings.NonlinearMaxHalvings; attempt++)
            {
                var (ok, iters, xNew, stepTrace) = NewtonAtFrac(x, nextFrac);
                trace.Add(new StepRecord(nextFrac, iters, ok, stepTrace));
                totalIters += iters;
                if (ok)
                {
                    x          = xNew;
                    targetFrac = nextFrac;
                    stepped    = true;
                    break;
                }
                nextFrac = (targetFrac + nextFrac) * 0.5;
                if (nextFrac - targetFrac < 1e-12) break;
            }

            if (!stepped)
            {
                double[] nv    = x[.._nodeCount];
                double[] fFail = SafeResidualVector(x, 1.0) ?? [];
                _lastX = x; ReportWorstResiduals(fFail);
                return new DcResult(nv, false, totalIters,
                                    fFail.Length > 0 ? L2(fFail) : double.PositiveInfinity, trace,
                                    ExtractProbeCurrents(x), fFail, _branchOwners);
            }
        }

        double[] nodeV = x[.._nodeCount];

        // ONE build, both answers. The norm is just the length of this vector, and
        // BuildResidualAndJacobian evaluates every nonlinear device — asking for the norm and the
        // vector separately doubles the cost of finishing a solve, which a timing budget notices.
        double[] fFinal  = SafeResidualVector(x, 1.0) ?? [];
        double   finalRes = fFinal.Length > 0 ? L2(fFinal) : double.PositiveInfinity;

        CaptureControlBias(x);   // seed SDD.ControlBias for a downstream S-param linearization
        return new DcResult(nodeV, finalRes < _settings.NonlinearAbsTol, totalIters, finalRes, trace,
                            ExtractProbeCurrents(x), fFinal, _branchOwners);
    }

    /// <summary>
    /// The most Newton iterations a ramped solve may spend in total — exactly the budget the
    /// settings already describe: every ramp step, each retried down to its last halving, each
    /// running to the iteration limit. Nothing new is being decided here; it is the existing
    /// numbers being held to.
    /// </summary>
    private int IterationBudget()
        => Math.Max(1, _settings.DcBiasRampSteps)
         * Math.Max(1, _settings.NonlinearMaxHalvings)
         * Math.Max(1, _settings.NonlinearMaxIter);

    /// <summary>
    /// Reports the unknowns furthest from settling when a solve gives up.
    ///
    /// <para>A failure could otherwise only say "residual 6.83", which is a number with no address:
    /// it names neither the part of the circuit that will not settle nor how far off it is, leaving
    /// bisecting the schematic as the only way forward. A handful of rows are almost always
    /// enormously worse than the rest.</para>
    ///
    /// <para><b>The unit is part of the diagnosis.</b> A thermal row's residual is in WATTS, and a
    /// temperature that will not settle reads nothing like a bias problem — saying which one this is
    /// may be the most useful word in the message.</para>
    ///
    /// <para>This is the one place that renders it. <c>SParameterEngine</c> used to derive the same
    /// report itself for the DC solve it runs, which meant two implementations of one idea and two
    /// near-identical warnings whenever both fired; it now adds only what is specific to what an
    /// S-parameter run does about the failure.</para>
    /// </summary>
    private void ReportWorstResiduals(double[] f)
    {
        if (f.Length == 0) return;

        var worst = f.Select((r, i) => (Residual: Math.Abs(r), Index: i))
                     .OrderByDescending(e => e.Residual)
                     .Take(3)
                     .Where(e => e.Residual > 0)
                     .ToList();
        if (worst.Count == 0) return;

        var parts = worst.Select(e =>
        {
            // Past the node count the unknown is a branch current, which has no node name to give.
            if (e.Index >= _nodeCount)
            {
                string owner = _branchOwners.TryGetValue(e.Index, out var o)
                    ? o
                    : $"branch unknown #{e.Index - _nodeCount}";
                return $"{owner} branch (residual {e.Residual:G3} A)";
            }

            int    node = e.Index + 1;                   // index 0 is circuit node 1
            string name = node < _nl.Nodes.Count ? _nl.Nodes.NameOf(node) : $"node {node}";

            return _thermalNodes.ContainsKey(e.Index)
                ? $"{name} = {_lastX[e.Index]:G4} °C — a TEMPERATURE, not a bias (residual {e.Residual:G3} W)"
                : $"{name} = {_lastX[e.Index]:G4} V (residual {e.Residual:G3} A)";
        });

        _nl.AddWarningOnce(
            "dc-worst-unsettled",
            "DC did not converge. Worst-unsettled: " + string.Join("; ", parts) + ".");
    }

    /// <summary>The iterate the report above reads its values from — set alongside every call.</summary>
    private double[] _lastX = [];

    private (bool OK, int Iters, double[] X, List<IterationRecord> Trace)
        NewtonAtFrac(double[] xStart, double frac)
    {
        double[] x        = (double[])xStart.Clone();
        int[]?   perm     = null;
        var      iterTrace = new List<IterationRecord>();
        double   absTol   = _settings.NonlinearAbsTol;
        double   relTol   = _settings.NonlinearRelTol;
        double   f0Norm   = 0.0;  // residual norm at iter 0 (for relative tolerance)

        double[]? xAccepted = null;   // last point the models evaluated
        double[]? lastStep  = null;   // the full Newton step taken from it
        double    lambda    = 1.0;    // fraction of that step currently being tried

        for (int iter = 0; iter < _settings.NonlinearMaxIter; iter++)
        {
            double[] f; List<(int, int, double)> j;

            // A REFUSED POINT IS NOT A FAILED SOLVE. A compiled model states the bias range it is
            // valid over by refusing outside it, and a full Newton step is perfectly capable of
            // stepping outside on the way to a solution that is well inside. Backing off along the
            // same direction and trying again is the ordinary answer; treating the first refusal as
            // the end of the solve throws away a converging run.
            while (!TryBuildResidualAndJacobian(x, frac, out f, out j))
            {
                // Nothing to back off from: the point we were handed is itself refused. That is a
                // starting point outside the model's range, not a step that overshot.
                if (xAccepted is null || lastStep is null)
                {
                    iterTrace.Add(new IterationRecord(iter, double.NaN, double.NaN));
                    return (false, iter + 1, x, iterTrace);
                }

                lambda *= 0.5;
                if (lambda < MinRefusalLambda)
                {
                    iterTrace.Add(new IterationRecord(iter, double.NaN, lambda));
                    return (false, iter + 1, xAccepted, iterTrace);
                }

                x = Advance(xAccepted, lastStep, lambda);
            }

            double fNorm = L2(f);

            if (iter == 0) f0Norm = fNorm;

            // Convergence check: absolute tolerance (always) + optional relative tolerance.
            bool absOk = fNorm < absTol;
            bool relOk = relTol > 0 && f0Norm > 0 && fNorm < relTol * f0Norm;
            if (absOk || relOk)
            {
                iterTrace.Add(new IterationRecord(iter, fNorm, 0.0));
                return (true, iter + 1, x, iterTrace);
            }

            var csc = AssembleCsc(j, _systemSize);
            perm ??= AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtA);

            SparseLU? lu;
            try   { lu = SparseLU.Create(csc, perm, 1.0); }
            catch { iterTrace.Add(new IterationRecord(iter, fNorm, double.NaN)); return (false, iter + 1, x, iterTrace); }
            if (lu is null) { iterTrace.Add(new IterationRecord(iter, fNorm, double.NaN)); return (false, iter + 1, x, iterTrace); }

            var rhs = new double[_systemSize];
            for (int i = 0; i < _systemSize; i++) rhs[i] = -f[i];
            var dx = new double[_systemSize];
            lu.Solve(rhs, dx);

            double dxNorm = L2(dx);
            iterTrace.Add(new IterationRecord(iter, fNorm, dxNorm, 1.0));

            // This point evaluated, so it is what a refusal after the next step backs off toward.
            xAccepted = x;
            lastStep  = dx;
            lambda    = 1.0;

            x = Advance(xAccepted, dx, lambda);   // λ=1, full Newton step

            if (dxNorm < DefaultVTol)
                return (true, iter + 1, x, iterTrace);
        }
        iterTrace.Add(new IterationRecord(
            _settings.NonlinearMaxIter,
            SafeResidualVector(x, frac) is { } fLast ? L2(fLast) : double.NaN, 0));
        return (false, _settings.NonlinearMaxIter, x, iterTrace);
    }

    /// <summary>
    /// Smallest fraction of a Newton step worth retrying after a model refuses the result. Below
    /// this the direction itself is not usable, and halving further only spends worker round trips
    /// to arrive at the same answer.
    /// </summary>
    private const double MinRefusalLambda = 1.0 / 1024.0;

    /// <summary>
    /// Absolute zero, in the units a thermal node carries. The model's own convention is one unit
    /// per degree C referenced to its sink, so this is where the temperature stops being a
    /// temperature. Held a hair above it because a model evaluated exactly at −273.15 has zero
    /// absolute temperature to divide by.
    /// </summary>
    private const double AbsoluteZeroFloor = -273.0;

    /// <summary>
    /// One Newton step, scaled by <paramref name="lambda"/>, with thermal nodes kept physical.
    ///
    /// <para><b>The clamp is not a convergence trick.</b> A negative absolute temperature is not a
    /// bad guess, it is not a quantity — no model can evaluate one, and a step that produces one has
    /// left the domain rather than overshot within it. Stopping it at the floor keeps the iterate
    /// somewhere a model can answer, which is what lets the refusal backoff above work on the steps
    /// that genuinely have overshot instead of being spent on this.</para>
    ///
    /// <para>Applies to thermal rows only. A voltage has no such floor, and clamping one would be
    /// exactly the sort of quiet interference that makes a wrong answer look converged.</para>
    /// </summary>
    private double[] Advance(double[] from, double[] step, double lambda)
    {
        var next = new double[_systemSize];
        for (int i = 0; i < _systemSize; i++) next[i] = from[i] + lambda * step[i];

        foreach (int row in _thermalNodes.Keys)
            if (next[row] < AbsoluteZeroFloor) next[row] = AbsoluteZeroFloor;

        return next;
    }

    /// <summary>
    /// Builds the residual and Jacobian, reporting a model's refusal as false rather than as an
    /// exception — at this point it is an ordinary, expected outcome of trying a bias, and the
    /// caller's business is to try a smaller step, not to abandon the solve.
    /// </summary>
    /// <summary>
    /// The residual at <paramref name="x"/>, or null when a model refuses that point. Used wherever
    /// a residual is wanted for REPORTING rather than for stepping — a refusal there is not an error
    /// to raise, it is the reason the number cannot be given.
    /// </summary>
    private double[]? SafeResidualVector(double[] x, double sourceFrac)
    {
        try   { return ResidualVector(x, sourceFrac); }
        catch (ExternalDeviceException) { return null; }
    }

    private bool TryBuildResidualAndJacobian(
        double[] x, double sourceFrac,
        out double[] f, out List<(int R, int C, double V)> j)
    {
        try
        {
            (f, j) = BuildResidualAndJacobian(x, sourceFrac);
            return true;
        }
        catch (ExternalDeviceException)
        {
            f = []; j = [];
            return false;
        }
    }

    private (double[] F, List<(int R, int C, double V)> J)
        BuildResidualAndJacobian(double[] x, double sourceFrac)
    {
        int sz = _systemSize;
        double[] f = new double[sz];
        var j = new List<(int, int, double)>(sz * 5);

        // ── 1. Linear part ────────────────────────────────────────────────────
        double gmin = _settings.ConductanceRegularization == RegularizationMode.Never
            ? 0.0 : DefaultGmin;

        for (int i = 0; i < sz; i++)
        {
            double fi = -_bSource[i] * sourceFrac;
            for (int k = 0; k < sz; k++)
            {
                double g = _gAug[i, k];
                // Add gmin to voltage-node diagonals.
                if (i == k && i < _nodeCount) g += gmin;
                if (g == 0.0) continue;
                fi += g * x[k];
                j.Add((i, k, g));
            }
            f[i] = fi;
        }

        // ── 2. Nonlinear devices ──────────────────────────────────────────────
        foreach (var ec in _nl.Components)
        {
            if (ec.Model.Kind != ModelKind.Nonlinear) continue;

            int   portCount = ec.Model.PortCount;
            var   portV     = new double[portCount];

            // Port voltage: v[p] = V(nodes[2p]) − V(nodes[2p+1])
            for (int p = 0; p < portCount; p++)
            {
                int np = ec.Nodes.Length > 2 * p     ? ec.Nodes[2 * p]     : 0;
                int nm = ec.Nodes.Length > 2 * p + 1 ? ec.Nodes[2 * p + 1] : 0;
                portV[p] = NodeV(x, np) - NodeV(x, nm);
            }

            // Build control-current vector for SDD devices with C[n] references.
            SddModel? sddModel = ec.Model as SddModel;
            ControlCurrents ctrl = ControlCurrents.Empty;
            if (sddModel is not null && sddModel.ControlRefs.Length > 0)
            {
                var cVals = new double[sddModel.ControlRefs.Length];
                for (int ci = 0; ci < cVals.Length; ci++)
                    cVals[ci] = x[sddModel.ControlBranchIndices[ci]];
                ctrl = new ControlCurrents(cVals);
            }

            var res = ec.Evaluate(new PortVoltages(portV), ctrl);

            for (int p = 0; p < portCount; p++)
            {
                int np = ec.Nodes.Length > 2 * p     ? ec.Nodes[2 * p]     : 0;
                int nm = ec.Nodes.Length > 2 * p + 1 ? ec.Nodes[2 * p + 1] : 0;
                double ip = res.I[p];

                // Port current flows into device at np, out at nm.
                if (np > 0) f[np - 1] += ip;
                if (nm > 0) f[nm - 1] -= ip;

                for (int q = 0; q < portCount; q++)
                {
                    int qp = ec.Nodes.Length > 2 * q     ? ec.Nodes[2 * q]     : 0;
                    int qm = ec.Nodes.Length > 2 * q + 1 ? ec.Nodes[2 * q + 1] : 0;
                    double dgpq = res.Dg[p, q];
                    if (dgpq == 0.0) continue;

                    StampDg(j, np, nm, qp, qm, dgpq);
                }
            }

            // Stamp DControl: ∂I[p]/∂_cn → referenced branch column (exact DC Jacobian entry).
            if (res.DControl is not null && sddModel is not null)
            {
                for (int p = 0; p < portCount; p++)
                {
                    int np = ec.Nodes.Length > 2 * p     ? ec.Nodes[2 * p]     : 0;
                    int nm = ec.Nodes.Length > 2 * p + 1 ? ec.Nodes[2 * p + 1] : 0;

                    for (int ci = 0; ci < sddModel.ControlRefs.Length; ci++)
                    {
                        double dc = res.DControl[p, ci];
                        if (dc == 0.0) continue;
                        int col = sddModel.ControlBranchIndices[ci]; // 0-based branch index
                        if (np > 0) j.Add((np - 1, col, +dc));
                        if (nm > 0) j.Add((nm - 1, col, -dc));
                    }
                }
            }

            // w≥2 bucket contributions at DC (ω=0).
            // Weight(w,0) for most physical H[w] is zero (e.g. jω→0), but we ask rather than assume.
            foreach (var term in res.Terms)
            {
                double hwReal = ec.Model.Weight(term.W, 0.0).Real;
                if (hwReal == 0.0) continue;

                for (int p = 0; p < portCount; p++)
                {
                    int np = ec.Nodes.Length > 2 * p     ? ec.Nodes[2 * p]     : 0;
                    int nm = ec.Nodes.Length > 2 * p + 1 ? ec.Nodes[2 * p + 1] : 0;
                    double ip = hwReal * term.Value[p];
                    if (np > 0) f[np - 1] += ip;
                    if (nm > 0) f[nm - 1] -= ip;

                    for (int q = 0; q < portCount; q++)
                    {
                        int qp = ec.Nodes.Length > 2 * q     ? ec.Nodes[2 * q]     : 0;
                        int qm = ec.Nodes.Length > 2 * q + 1 ? ec.Nodes[2 * q + 1] : 0;
                        double dg = hwReal * term.Jac[p, q];
                        if (dg == 0.0) continue;
                        StampDg(j, np, nm, qp, qm, dg);
                    }
                }
            }
        }

        return (f, j);
    }

    // Stamp dg[p,q] into Jacobian: ∂(KCL at np,nm) / ∂(V_qp - V_qm)
    private static void StampDg(List<(int, int, double)> j,
        int np, int nm, int qp, int qm, double dgpq)
    {
        if (np > 0 && qp > 0) j.Add((np - 1, qp - 1, +dgpq));
        if (np > 0 && qm > 0) j.Add((np - 1, qm - 1, -dgpq));
        if (nm > 0 && qp > 0) j.Add((nm - 1, qp - 1, -dgpq));
        if (nm > 0 && qm > 0) j.Add((nm - 1, qm - 1, +dgpq));
    }

    // Extract voltage for a circuit node from the state vector.
    // node is the 1-based circuit node; 0 = ground = 0.0.
    private static double NodeV(double[] x, int node)
        => node > 0 ? x[node - 1] : 0.0;

    private double ResidualNorm(double[] x, double frac)
    {
        var (f, _) = BuildResidualAndJacobian(x, frac);
        return L2(f);
    }

    /// <summary>The residual vector itself, for reporting WHICH unknowns did not settle.</summary>
    private double[] ResidualVector(double[] x, double frac)
    {
        var (f, _) = BuildResidualAndJacobian(x, frac);
        return f;
    }

    private static CompressedColumnStorage<double> AssembleCsc(
        List<(int R, int C, double V)> triples, int n)
    {
        var tri = new CoordinateStorage<double>(n, n, triples.Count);
        foreach (var (r, c, v) in triples) tri.At(r, c, v);
        return SparseMatrix.OfIndexed(tri);
    }

    private static double L2(double[] v)
    {
        double s = 0;
        foreach (var x in v) s += x * x;
        return Math.Sqrt(s);
    }
}
