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
            try { ec.Model.Stamp(mna, ec, omega: 0.0); }
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
        ReportImplausibleThermalResistances();
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
    /// Above this, a thermal resistance is not a thermal resistance. Real junction-to-ambient values
    /// run from well under 1 to a few hundred °C/W; a few thousand is already beyond anything
    /// physical. Four orders of magnitude of headroom above that is deliberate — this exists to
    /// catch a node left on a keep-alive leak resistor, which is typically 10^7 or more, not to
    /// second-guess an unusual but real design.
    /// </summary>
    public const double ImplausibleThermalResistance = 1e4;   // °C/W

    /// <summary>
    /// Reports a thermal node whose path to its reference is too high to be a thermal path.
    ///
    /// <para><b>Why this is worth a warning and not a failure.</b> On such a node the model's own
    /// dissipated-power current source sets the temperature to P × R — with R at keep-alive scale
    /// that is tens of thousands of degrees, which the model clamps and evaluates without complaint.
    /// The run therefore SUCCEEDS and returns an operating point computed at a temperature nothing
    /// intended. That is precisely the case a user cannot see and the solver cannot object to.</para>
    ///
    /// <para>The diagonal is the sum of every conductance incident on the node, so 1/diagonal is a
    /// LOWER bound on its resistance to anywhere. Testing that bound is what makes this free of
    /// false positives: a node reported here is at least this badly connected, whatever else it
    /// touches.</para>
    /// </summary>
    private void ReportImplausibleThermalResistances()
    {
        foreach (var (row, owner) in _thermalNodes.OrderBy(e => e.Key))
        {
            double g = _gAug[row, row];
            double r = g > 0 ? 1.0 / g : double.PositiveInfinity;
            if (r <= ImplausibleThermalResistance) continue;

            string node = row + 1 < _nl.Nodes.Count ? _nl.Nodes.NameOf(row + 1) : $"node {row + 1}";

            _nl.AddWarningOnce(
                $"thermal-resistance:{owner}:{row}",
                $"{owner}: the thermal node '{node}' reaches its reference only through " +
                $"{(double.IsPositiveInfinity(r) ? "no path at all" : $"{r:G3} °C/W")}, which is not a " +
                $"thermal resistance — real values are a few hundred °C/W at most. The device's own " +
                $"dissipated power sets this node's temperature, so the model will run at whatever " +
                $"that resistance implies. Connect it to the thermal network the rest of the design " +
                $"uses.");
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

        while (targetFrac < 1.0 - 1e-12)
        {
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

            var res = ec.Model.Evaluate(new PortVoltages(portV), ctrl);

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
