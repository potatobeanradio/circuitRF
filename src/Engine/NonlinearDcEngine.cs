using CSparse;
using CSparse.Double;
using CSparse.Double.Factorization;
using CSparse.Ordering;
using CSparse.Storage;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
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

        internal DcResult(double[] v, bool converged, int iters, double residual,
            ConvergenceTrace trace, IReadOnlyDictionary<string, double> probeCurrents)
        {
            NodeVoltages  = v;
            Converged     = converged;
            Iterations    = iters;
            FinalResidual = residual;
            Trace         = trace;
            ProbeCurrents = probeCurrents;
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
    private readonly double[,] _gAug;      // linear conductance / branch matrix (constant)
    private readonly double[]  _bSource;   // RHS source vector (scaled by sourceFrac)

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
            try { ec.Model.Stamp(mna, ec, omega: 0.0); }
            catch (NotImplementedException) { }
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
        double finalRes = ResidualNorm(xNew, 1.0);
        bool converged  = ok && finalRes < _settings.NonlinearAbsTol;

        if (!converged && throwOnFailure)
            throw new NonlinearDcNotConvergedException(iters, finalRes);

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
                double[] nv = x[.._nodeCount];
                return new DcResult(nv, false, totalIters, ResidualNorm(x, 1.0), trace, ExtractProbeCurrents(x));
            }
        }

        double[] nodeV  = x[.._nodeCount];
        double finalRes = ResidualNorm(x, 1.0);
        return new DcResult(nodeV, finalRes < _settings.NonlinearAbsTol, totalIters, finalRes, trace, ExtractProbeCurrents(x));
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

        for (int iter = 0; iter < _settings.NonlinearMaxIter; iter++)
        {
            var (f, j) = BuildResidualAndJacobian(x, frac);
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

            for (int i = 0; i < _systemSize; i++) x[i] += dx[i];  // λ=1, full Newton step

            if (dxNorm < DefaultVTol)
                return (true, iter + 1, x, iterTrace);
        }
        iterTrace.Add(new IterationRecord(_settings.NonlinearMaxIter, ResidualNorm(x, frac), 0));
        return (false, _settings.NonlinearMaxIter, x, iterTrace);
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

            var res = ec.Model.Evaluate(new PortVoltages(portV));

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
