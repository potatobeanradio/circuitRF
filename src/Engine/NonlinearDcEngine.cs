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

    /// <summary>
    /// Solves the operating point — and, if there is none as the design stands and the reason is a
    /// thermal node with no usable reference, solves it again with those nodes held at the ambient.
    ///
    /// <para><b>Why a retry rather than a repair up front.</b> A thermal node reached only through a
    /// keep-alive leak resistor has no operating point: the device's own dissipated power drives its
    /// temperature away without limit, the model starts refusing points, and the solve grinds
    /// through every ramp step and every halving before failing with a residual that names a supply
    /// branch rather than the node that caused it. The elaborator already holds such a node at the
    /// ambient when NOTHING is attached to it, but that guard reads the topology, and a leak resistor
    /// is a connection in exactly the sense it is written in — so the case this exists for walks
    /// straight past it. Measured on a real kit: 1,518 iterations, no convergence, at a bias the
    /// same circuit answers in 4 once the node is referenced.</para>
    ///
    /// <para><b>And only ever on the way out of one.</b> Holding the node up front would mean
    /// deciding, from a threshold, that a design which solves perfectly well was written wrong — and
    /// a resistance high enough to be worth warning about is not the same thing as one with no
    /// answer behind it. A design that produces an operating point is returned exactly as it stands,
    /// whatever its thermal network looks like.</para>
    ///
    /// <para><b>"Failure" here is not only a non-zero convergence flag.</b> The stopping rule is an
    /// absolute residual norm, and a norm says nothing about whether the point it was measured at is
    /// physical: the same circuit that will not converge on one machine squeaks under the tolerance
    /// on another, after hundreds of iterations, with its thermal nodes at hundreds of thousands of
    /// degrees. Measured on a real kit, one bias, three wirings of the exposed thermal pins — open,
    /// 1 MΩ, and shorted to ground — all "converged", at 227 to 502 iterations, with residuals of
    /// 3e-7 to 9.6e-7 against a tolerance of 1e-6 and two junctions at ~700,000 °C. That is not an
    /// operating point that happens to be reported oddly; it is the same non-solution, on the near
    /// side of a knife edge. So a converged result is ALSO rejected when a node already measured as
    /// unreferenced came back at a temperature no part can be at.</para>
    /// </summary>
    /// <summary>
    /// How many DC operating-point solves this thread has run. Test-facing, like
    /// <c>HbNewton.Evaluations</c>: what an HB sweep's warm start actually buys is N DC seeds
    /// becoming ONE (harmonic-balance.md §11.1), and that is the number those gates read. Per-thread
    /// so the suite's parallel classes cannot see each other's counts.
    /// </summary>
    [ThreadStatic] private static int _runs;

    /// <inheritdoc cref="_runs"/>
    public static int Runs => _runs;

    /// <inheritdoc cref="_runs"/>
    public static void ResetRuns() => _runs = 0;

    public static DcResult Run(ElaboratedNetlist netlist, AnalysisSettings? settings = null)
    {
        _runs++;
        var s      = settings ?? AnalysisSettings.Default;
        var direct = new NonlinearDcEngine(netlist, s);

        if (direct.UnreferencedThermalRows.Count == 0) return direct.Solve();

        // The failure report is held back while a retry could still make it wrong: it would name a
        // supply branch on a run that ends up converging, which is worse than saying nothing.
        direct._deferFailureReport = true;

        DcResult? asWritten = null;
        NonlinearDcNotConvergedException? refused = null;
        try   { asWritten = direct.Solve(); }
        catch (NonlinearDcNotConvergedException ex) { refused = ex; }

        if (asWritten is { Converged: true } && !direct.ThermalNodesRanAway(asWritten))
            return asWritten;

        var held = new NonlinearDcEngine(netlist, s, [.. direct.UnreferencedThermalRows]);
        held._deferFailureReport = true;   // if this fails too, the design as written is the story

        DcResult? repaired = null;
        try   { repaired = held.Solve(); }
        catch (NonlinearDcNotConvergedException) { }

        if (repaired is { Converged: true })
        {
            // A NOTE, not a warning. This run has an operating point and is reported as converged;
            // what the message carries is something circuitRF WORKED OUT that the design did not
            // state — which thermal nodes it held, and at what — rather than a complaint about the
            // run it accompanies. Raised as a warning it makes a successful run read as a failed
            // one, and buries the warnings that do need attention.
            netlist.AddNoteOnce("thermal-hold-retry", direct.ThermalHoldRetryMessage());
            return repaired;
        }

        // The thermal reference was not what stopped it. Report the original failure as it stood.
        direct.EmitDeferredFailureReport();
        if (refused is not null) throw refused;
        return asWritten!;
    }

    /// <summary>
    /// Whether any thermal node already measured as unreferenced came back at a temperature no
    /// part can be at — the case a residual norm cannot object to and a user cannot use.
    /// </summary>
    private bool ThermalNodesRanAway(DcResult r)
    {
        foreach (int row in _unreferencedThermalRows)
            if (row < r.NodeVoltages.Length &&
                (!double.IsFinite(r.NodeVoltages[row]) ||
                 Math.Abs(r.NodeVoltages[row]) > Temperature.ImplausibleJunctionTemperatureC))
                return true;

        return false;
    }

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

    /// <summary>
    /// Thermal matrix rows this solve is holding at the ambient. Empty on the first attempt, which
    /// always solves the design exactly as it was written.
    /// </summary>
    private readonly IReadOnlyList<int> _heldThermalRows;

    /// <summary>
    /// Thermal rows measured to reach their reference through no thermal resistance worth the name,
    /// so a solve that fails has somewhere to look. Populated by <see cref="ReportThermalNodes"/>.
    /// </summary>
    internal IReadOnlyList<int> UnreferencedThermalRows => _unreferencedThermalRows;
    private readonly List<int> _unreferencedThermalRows = [];

    /// <summary>
    /// Whether a failed solve keeps its worst-unsettled report to itself. Set while a retry could
    /// still overturn the failure; <see cref="EmitDeferredFailureReport"/> releases it if not.
    /// </summary>
    private bool _deferFailureReport;
    private string? _deferredFailureReport;

    private NonlinearDcEngine(ElaboratedNetlist nl, AnalysisSettings settings,
                              IReadOnlyList<int>? holdThermalRowsAtAmbient = null)
    {
        _nl              = nl;
        _settings        = settings;
        _nodeCount       = nl.Nodes.Count - 1;  // nodes 1..N; node 0 = ground excluded
        _heldThermalRows = holdThermalRowsAtAmbient ?? [];

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

        // Resolve control-current branch indices for any SDD with C[n] references.
        // Branch indices are now assigned (the stamp loop above ran all linear devices).
        ResolveControlCurrentBranches();

        // Every held thermal node costs a branch row, so the system is one row longer per hold than
        // what the components themselves stamped.
        int stamped = mna.Size;                       // nodeCount + branchCount
        _systemSize = stamped + _heldThermalRows.Count;
        _gAug       = new double[_systemSize, _systemSize];
        _bSource    = new double[_systemSize];

        for (int i = 0; i < stamped; i++)
            _bSource[i] = mna.GetRhs(i).Real;
        // Read the sparse assembly out once, rather than probing all stamped² cells: the MNA now
        // answers GetEntry from its CSC, so a dense scan would be size × nnz instead of size + nnz.
        // _gAug starts zeroed, so the entries not present are already right.
        foreach (var (row, col, val) in mna.NonZeroEntries())
            _gAug[row, col] = val.Real;

        CollectThermalNodes();

        // Measured against what the components stamped — the holds below are this engine's own
        // addition and must not be mistaken for the design's thermal network on the way back out.
        ReportThermalNodes(stamped);

        // One ideal source per held node, stamped exactly as VdcModel stamps a Vdc to ground: a
        // constraint row saying the node sits at the ambient, and the branch current that takes into
        // the node's own row. Ramped with every other source, so the hold arrives as the bias does.
        for (int i = 0; i < _heldThermalRows.Count; i++)
        {
            int br  = stamped + i;
            int row = _heldThermalRows[i];            // matrix row; circuit node is row + 1
            _gAug[br, row]    = +1.0;
            _gAug[row, br]    = +1.0;
            _bSource[br]      = _nl.AmbientC;
            _branchOwners[br] = $"ambient hold on '{NodeName(row)}'";
        }
    }

    /// <summary>The circuit's name for a voltage-unknown matrix row, for a message to quote.</summary>
    private string NodeName(int row)
        => row + 1 < _nl.Nodes.Count ? _nl.Nodes.NameOf(row + 1) : $"node {row + 1}";

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
    private Dictionary<int, ThermalMeasurement> MeasureThermalNodes(int size)
    {
        var measured = new Dictionary<int, ThermalMeasurement>();
        if (_thermalNodes.Count == 0) return measured;

        var ownConductance = DeviceThermalConductance();

        var j = new List<(int R, int C, double V)>(size * 4);
        for (int i = 0; i < size; i++)
            for (int k = 0; k < size; k++)
            {
                double g = _gAug[i, k];
                if (i == k && i < _nodeCount) g += DefaultGmin;
                if (g != 0.0) j.Add((i, k, g));
            }

        var x     = new double[size];
        var probe = new double[size];
        try
        {
            var csc  = AssembleCsc(j, size);
            var perm = AMD.Generate(csc, ColumnOrdering.MinimumDegreeAtA);
            var lu   = SparseLU.Create(csc, perm, 1.0);
            if (lu is null) return measured;

            lu.Solve(_bSource, x);

            foreach (int row in _thermalNodes.Keys)
            {
                if (row >= size || !double.IsFinite(x[row])) continue;

                Array.Clear(probe);
                probe[row] = 1.0;                       // one watt in, at this node only
                var rise = new double[size];
                lu.Solve(probe, rise);
                if (!double.IsFinite(rise[row])) continue;

                // The device's own path back, in parallel: it shunts this node exactly as a
                // resistor to ground would, so the two combine as conductances and nothing more.
                double network = Math.Abs(rise[row]);
                double gOwn    = ownConductance.TryGetValue(row, out double g0) ? g0 : 0.0;
                double total   = double.IsPositiveInfinity(gOwn)
                               ? 0.0
                               : 1.0 / (SafeReciprocal(network) + gOwn);

                measured[row] = new ThermalMeasurement(x[row], total);
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

    /// <summary>1/x, with an infinite resistance reading as no conductance rather than as NaN.</summary>
    private static double SafeReciprocal(double x)
        => double.IsPositiveInfinity(x) ? 0.0 : (x <= 0.0 ? double.PositiveInfinity : 1.0 / x);

    /// <summary>
    /// How strongly each thermal node is held by the DEVICES on it, rather than by the network
    /// around them — the sum of their own positive thermal-diagonal entries, in W/°C.
    ///
    /// <para><b>Why it has to be asked for separately.</b> The measurement above solves the LINEAR
    /// system, and a device's own internal thermal resistance is not in it: the device is nonlinear,
    /// so its conductance lives in a Jacobian the linear stamp never sees. A provider is entitled to
    /// carry one, and one that does has already referenced the node — the rise it solves for is the
    /// answer the design wanted, and treating the node as unreferenced would silently delete the
    /// part's self-heating.</para>
    ///
    /// <para><b>A real path is a POSITIVE conductance, not a non-zero one.</b> An electrothermal
    /// model's thermal diagonal is routinely non-zero and NEGATIVE: that entry is its self-heating
    /// FEEDBACK, which pushes the node away from a solution rather than holding it near one. Reading
    /// "non-zero" as "referenced" leaves the exact devices this exists for unreferenced, so the sign
    /// is the test — the same rule <c>Elaborator.SelfReferencedThermalNodes</c> draws, at the same
    /// all-zero point, which is where the solve's own ramp starts and therefore where any device
    /// that can be solved at all will answer.</para>
    ///
    /// <para>A device that refuses that point returns an INFINITE conductance: nothing was learned,
    /// and the conservative reading is that the node is fine as it is.</para>
    /// </summary>
    private Dictionary<int, double> DeviceThermalConductance()
    {
        var byRow = new Dictionary<int, double>();
        if (_thermalNodes.Count == 0) return byRow;

        foreach (var ec in _nl.Components)
        {
            if (ec.Model is not ExternalDeviceModel ed) continue;

            var thermalPins = ed.Descriptor.Nodes
                .Where(n => n.QuantityKind == NodeQuantityKind.Thermal)
                .ToList();
            if (thermalPins.Count == 0) continue;

            double[,]? dg;
            try   { dg = ec.Evaluate(new PortVoltages(new double[ec.Model.PortCount]),
                                     ControlCurrents.Empty).Dg; }
            catch { dg = null; }   // refused the point: nothing learned, so nothing claimed

            foreach (var n in thermalPins)
            {
                int np = ec.Nodes.Length > 2 * n.Index ? ec.Nodes[2 * n.Index] : 0;
                if (np <= 0 || np - 1 >= _nodeCount) continue;
                int row = np - 1;

                double g;
                if (dg is null) g = double.PositiveInfinity;
                else if (n.Index >= dg.GetLength(0) || n.Index >= dg.GetLength(1)) continue;
                else g = Math.Max(0.0, dg[n.Index, n.Index]);

                byRow[row] = byRow.TryGetValue(row, out double had) ? had + g : g;
            }
        }

        return byRow;
    }

    /// <summary>
    /// Reports the two ways a thermal node ends up somewhere nothing intended: too weakly referenced
    /// to be a thermal path at all, and referenced to GROUND rather than to the ambient.
    ///
    /// <para><b>Neither is refused, and both are easy to miss.</b> Where the solve converges the
    /// network is well conditioned, it takes the same number of iterations, and every number that
    /// comes back is finite and plausible. What is wrong is the temperature the model was evaluated
    /// at, and there is no symptom to notice — which is precisely the case a user cannot see and the
    /// solver cannot object to.</para>
    ///
    /// <para><b>Weakly referenced</b> — the device's own dissipated power sets the node to P × R, and
    /// with R at keep-alive scale that is tens of thousands of degrees, which a model clamps and
    /// evaluates without complaint. Where it does NOT converge, that same reading is what
    /// <see cref="Run"/> retries against, so the rows measured this way are recorded on the way past
    /// — recorded only: what to do about them is a decision the solve's own outcome makes.</para>
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
    /// resistance case above rather than being reported twice for one condition.</para>
    /// </summary>
    private void ReportThermalNodes(int size)
    {
        var measured = MeasureThermalNodes(size);
        double ambient = _nl.AmbientC;

        foreach (var (row, owner) in _thermalNodes.OrderBy(e => e.Key))
        {
            string node = NodeName(row);

            if (!measured.TryGetValue(row, out var m)) continue;

            if (m.ResistanceCPerW > ImplausibleThermalResistance)
            {
                // Remembered, not acted on. Run() reaches for this only if the solve then fails.
                if (!_heldThermalRows.Contains(row)) _unreferencedThermalRows.Add(row);

                // A NOTE, not a warning: this is a MEASUREMENT of what the design's own network
                // does to this node, reported on every run whether or not anything came of it.
                // Whether it cost the run an operating point — and what was done about that — is
                // said separately, by the one message that only appears when it did.
                _nl.AddNoteOnce(
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
            // An ideal current source's current is an INPUT, not a solved unknown — it allocates no
            // branch to point at. Named explicitly because "the other tone source works" is exactly
            // the wrong inference to leave the user to draw from a generic list of allowed kinds.
            CurrentToneSourceModel => throw new InvalidOperationException(
                $"SDD '{sddName}': C[{n}]={target.InstancePath} is an ideal current source (I_1Tone/I_nTone): " +
                $"its current is an input, not a solved unknown, so it has no branch to reference. " +
                $"Put an IProbe in series with it and reference that instead."),
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

        string message = "DC did not converge. Worst-unsettled: " + string.Join("; ", parts) + ".";

        if (_deferFailureReport) { _deferredFailureReport = message; return; }
        _nl.AddWarningOnce("dc-worst-unsettled", message);
    }

    /// <summary>
    /// Releases a failure report that was held back for a retry which did not, in the end, help.
    /// </summary>
    private void EmitDeferredFailureReport()
    {
        if (_deferredFailureReport is { } m) _nl.AddWarningOnce("dc-worst-unsettled", m);
    }

    /// <summary>
    /// What to say when the operating point only exists once the weakly-referenced thermal nodes are
    /// held at the ambient. The design did not ask for this, so it names every node it did it to.
    /// </summary>
    private string ThermalHoldRetryMessage()
    {
        var named = _unreferencedThermalRows
            .Select(r => $"'{NodeName(r)}'" +
                         (_thermalNodes.TryGetValue(r, out var o) ? $" ({o})" : ""))
            .ToList();

        bool one = named.Count == 1;

        // Leads with what circuitRF DID, not with the failure that prompted it. The run this
        // message accompanies has an operating point and is reported as converged; a headline of
        // "no operating point" on a successful run reads as the run having failed.
        return $"DC re-solved with the thermal " +
               $"{(one ? "node" : "nodes")} {string.Join(", ", named)} held at the ambient " +
               $"{_nl.AmbientC:0.##} °C — this run DID find an operating point, with no temperature " +
               $"rise on {(one ? "that part" : "those parts")}. The design as written does not: " +
               $"{(one ? "that node is" : "those nodes are")} held only by a resistance that is not " +
               $"a thermal resistance, which leaves {(one ? "it" : "them")} with no temperature to " +
               $"settle at — either no convergence at all, or hundreds of thousands of degrees at a " +
               $"residual small enough to pass for one. To model self-heating, connect each of these " +
               $"nodes through the part's thermal resistance to a source holding the ambient.";
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
