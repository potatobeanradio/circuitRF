using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// Symbolically-defined device (SDD): a ComponentModel whose behavior is user-authored
/// expressions evaluated in dual arithmetic (§3 of nonlinear-dc.md).
///
/// Constructed with pre-resolved parameter values (from the .cnl scope) and cached ASTs
/// for each port equation. Evaluate() runs the generic SddEvaluator at the given port voltages.
/// Control currents _c1.._cm are seeded from ControlRefs / ControlBranchIndices (set by the engine).
/// </summary>
public sealed class SddModel : ComponentModel
{
    private readonly int _portCount;
    private readonly string _name;

    // Current equations I[p,0] — index = port-1. Null = absent (defaults to zero current).
    private readonly Expr?[] _currentAst;
    // Charge equations I[p,1] — index = port-1. Null = absent.
    private readonly Expr?[] _chargeAst;
    // BRANCH equations V[p] — index = port-1. Null = absent. A port carrying one is CONSTRAINED:
    // its voltage is held at the equation's value and its current is whatever the rest of the
    // circuit draws through the branch unknown this device allocates for it.
    private readonly Expr?[] _voltageAst;
    // Higher-weighting buckets I[p,w≥2] — per port (index = port-1), list of (w, ast).
    private readonly IReadOnlyList<(int W, Expr Ast)>[] _higherAst;
    // Weight-function ASTs H[w] for w≥2: w → AST evaluated in the frequency domain.
    private readonly IReadOnlyDictionary<int, Expr> _weightAst;

    // Resolved scope variables (B, Sc, TV0, …) from the .cnl elaboration scope.
    // These are constants at eval time — they don't change per Newton step.
    private readonly IReadOnlyDictionary<string, double> _params;

    // §1.3 step 1 (brief-harmonicarf-r3b) — each equation compiled ONCE here, alongside where the AST
    // is already cached, rather than re-resolved (dictionary + string interpolation + string-hashed
    // RefExpr lookup) on every one of the many Evaluate calls a Newton solve makes. Null entries mirror
    // the corresponding *Ast entry (no equation for that port).
    private readonly CompiledSddExpr?[] _compiledCurrent;
    private readonly CompiledSddExpr?[] _compiledCharge;
    private readonly CompiledSddExpr?[] _compiledVoltage;
    private readonly (int W, CompiledSddExpr Compiled)[][] _compiledHigher;

    // HB-P4 M2 — the grid door. True only when EVERY compiled equation has a register program
    // (no conditional anywhere); one equation with an `if` puts the whole device back on the scalar
    // path, because a device is asked for all its equations at once.
    private readonly bool _supportsGrid;
    // Distinct w≥2 buckets, ascending — the same set the scalar Evaluate builds per call, computed
    // once here instead.
    private readonly int[] _wOrder;
    // Register files for the grid runner: one per worker (index 0 is the serial one). Grown, never
    // shrunk, so a converged solve allocates none of this after its first iteration.
    private GridScratch?[] _scratchPool = [];
    // Only needed when the device has control refs: the grid evaluator writes n = ports + controls
    // gradient lanes contiguously, and the last C of them belong to a different result block.
    private double[] _gradBuf = [];
    // Parallel-path copies of the caller's spans (a lambda cannot close over a span) and the
    // per-chunk warning collectors.
    private double[] _portCopy = [], _ctrlCopy = [];
    private GridDomainWarnings[] _chunkWarn = [];

    /// <summary>
    /// HB-P4 M3 — the sample count at or above which a grid evaluation is split across cores.
    /// A two-tone grid is 1,024 samples and an n-tone APFT grid 756, which are worth a fork/join;
    /// a single-tone 32 is not, and would spend more in the join than in the arithmetic. Settable so
    /// a test can force either side of the decision and gate that both give the same bits — and
    /// thread-affine for the same reason as <see cref="NonlinearEvalDiagnostics"/>, so one test class
    /// forcing it cannot reach another's solve. Zero means the default.
    /// </summary>
    public static int GridParallelThreshold
    {
        get => _gridParallelThreshold == 0 ? DefaultGridParallelThreshold : _gridParallelThreshold;
        set => _gridParallelThreshold = value;
    }

    /// <summary>The shipped value of <see cref="GridParallelThreshold"/>.</summary>
    public const int DefaultGridParallelThreshold = 256;

    [ThreadStatic] private static int _gridParallelThreshold;


    /// <summary>Instance name (for error messages).</summary>
    public string Name => _name;

    /// <summary>
    /// Raw control-current references from .cnl parsing.
    /// (N, RefInstance, Port): N is the 1-based _cn index; Port=0 means Cport absent.
    /// Unresolved until the engine calls ResolveControlBranches after the first stamp.
    /// </summary>
    public (int N, string RefInstance, int Port)[] ControlRefs { get; }

    /// <summary>
    /// Resolved branch indices into the state vector, parallel to ControlRefs.
    /// ControlBranchIndices[i] = x[...] index for ControlRefs[i].
    /// -1 = not yet resolved. Set by the engine after linear stamp.
    /// PER-RUN: each engine (DC, HB, S-param) resolves this against its own MNA before stamping —
    /// branch numbering differs between assemblies, so a value resolved for one run is not valid
    /// for another (S-param re-resolves what the DC pre-pass left here).
    /// </summary>
    public int[] ControlBranchIndices { get; }

    /// <summary>
    /// DC operating-point value of each referenced control current, parallel to ControlRefs.
    /// Seeded by the S-parameter engine (from the DC pre-pass) so StampLinearized evaluates the
    /// control sensitivities (∂I/∂_cn) at the correct bias. Zero for a linear-in-_cn equation
    /// (the sensitivity is then seed-independent); only a nonlinear-in-_cn dependence needs it.
    /// Defaults to zeros; the DC/HB engines do not use it (they read _cn from the live state vector).
    /// </summary>
    public double[] ControlBias { get; }

    public SddModel(
        string name,
        int portCount,
        Expr?[] currentAst,
        Expr?[] chargeAst,
        IReadOnlyDictionary<string, double> parameters,
        IReadOnlyList<(int W, Expr Ast)>[]? higherAst = null,
        IReadOnlyDictionary<int, Expr>? weightAst = null,
        IReadOnlyList<(int N, string RefInstance, int Port)>? controlRefs = null,
        Expr?[]? voltageAst = null)
    {
        _name       = name;
        _portCount  = portCount;
        _currentAst = currentAst;
        _chargeAst  = chargeAst;
        _voltageAst = voltageAst ?? new Expr?[portCount];
        _params     = parameters;
        _higherAst  = higherAst
            ?? Enumerable.Range(0, portCount).Select(_ => (IReadOnlyList<(int, Expr)>)[]).ToArray();
        _weightAst  = weightAst ?? new Dictionary<int, Expr>();
        ControlRefs = controlRefs?.ToArray() ?? [];
        ControlBranchIndices = new int[ControlRefs.Length];
        for (int i = 0; i < ControlBranchIndices.Length; i++) ControlBranchIndices[i] = -1;
        ControlBias = new double[ControlRefs.Length];   // zeros (correct seed for linear-in-_cn)

        // §1.3 step 1 — compile every equation once, here, against this model's own fixed shape
        // (port count, control-ref numbering, parameter set). Order matches BuildControlSeeds' own
        // seed array (ControlRefs[i].N at seed index i), which is what a compiled slot i must agree
        // with at evaluation time.
        var controlNs = ControlRefs.Select(r => r.N).ToArray();
        _compiledCurrent = new CompiledSddExpr?[_portCount];
        _compiledCharge  = new CompiledSddExpr?[_portCount];
        _compiledVoltage = new CompiledSddExpr?[_portCount];
        _compiledHigher  = new (int W, CompiledSddExpr Compiled)[_portCount][];
        var branchPorts  = new List<int>();
        for (int p = 0; p < _portCount; p++)
        {
            if (_currentAst[p] is { } ca) _compiledCurrent[p] = CompiledSddExpr.Compile(ca, _params, _portCount, controlNs, _name);
            if (_chargeAst[p]  is { } qa) _compiledCharge[p]  = CompiledSddExpr.Compile(qa, _params, _portCount, controlNs, _name);
            if (_voltageAst[p] is { } va)
            {
                _compiledVoltage[p] = CompiledSddExpr.Compile(va, _params, _portCount, controlNs, _name);
                branchPorts.Add(p);
            }

            var higher = _higherAst[p];
            var compiled = new (int, CompiledSddExpr)[higher.Count];
            for (int j = 0; j < higher.Count; j++)
                compiled[j] = (higher[j].W, CompiledSddExpr.Compile(higher[j].Ast, _params, _portCount, controlNs, _name));
            _compiledHigher[p] = compiled;
        }

        BranchPorts    = [.. branchPorts];
        _branchIndices = new int[BranchPorts.Length];
        Array.Fill(_branchIndices, -1);

        // HB-P4 — the grid door opens only if every equation can walk it.
        bool grid = true;
        foreach (var c in _compiledCurrent) if (c is not null && !c.SupportsGrid) grid = false;
        foreach (var c in _compiledCharge)  if (c is not null && !c.SupportsGrid) grid = false;
        foreach (var list in _compiledHigher)
            foreach (var (_, c) in list) if (!c.SupportsGrid) grid = false;
        _supportsGrid = grid;

        var wSet = new SortedSet<int>();
        foreach (var list in _higherAst)
            foreach (var (ww, _) in list)
                wSet.Add(ww);
        _wOrder = [.. wSet];
    }

    public override int       PortCount => _portCount;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    // SDD gate=port0, drain=port1 for a 2-port FET; 3-port adds source; 4-port adds thermal.
    // Names match the common equation-defined-device convention so hero references stay transcribable.
    private static readonly string[][] _termNames =
    [
        [],
        ["1"],
        ["g", "d"],
        ["g", "d", "s"],
        ["g", "d", "s", "t"],
    ];

    public override string[] TerminalNames
        => _portCount < _termNames.Length ? _termNames[_portCount] :
           Enumerable.Range(1, _portCount).Select(i => i.ToString()).ToArray();

    /// <summary>
    /// The ports whose voltage a <c>V[p]</c> equation constrains, ascending. One branch-current
    /// unknown and one Newton row each; empty for every ordinary SDD.
    /// </summary>
    public int[] BranchPorts { get; }

    private readonly int[] _branchIndices;

    /// <inheritdoc/>
    public override int BranchEquationCount => BranchPorts.Length;

    /// <inheritdoc/>
    public override IReadOnlyList<int> BranchIndices => _branchIndices;

    /// <summary>
    /// An SDD contributes nothing to a linear stamp unless it CONSTRAINS a port voltage, in which
    /// case it contributes the constant half of that constraint and nothing else.
    ///
    /// <para><b>The constant half is free, and taking it is the whole reason this is small.</b> A
    /// branch unknown is allocated during the engines' linear pass, so the same pass can write the
    /// <c>+1</c>/<c>−1</c> KCL coupling and the <c>V(p+) − V(p−)</c> side of the constraint row into
    /// the engine's CONSTANT matrix. What is left for the per-iteration path is only the right-hand
    /// side <c>g(v, i)</c> and its derivatives — which is what
    /// <see cref="NonlinearResult.BranchResidual"/> carries.</para>
    ///
    /// <para><b>The source value is zero, not the equation.</b> The right-hand side moves with the
    /// solution, so it cannot be a constant RHS entry; it is subtracted from the residual at every
    /// Newton step instead. Writing it here would hold the source at its first guess forever.</para>
    /// </summary>
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        for (int k = 0; k < BranchPorts.Length; k++)
        {
            int p  = BranchPorts[k];
            int np = c.Nodes.Length > 2 * p     ? c.Nodes[2 * p]     : 0;
            int nm = c.Nodes.Length > 2 * p + 1 ? c.Nodes[2 * p + 1] : 0;

            int br = mna.AddBranch();
            _branchIndices[k] = br;
            mna.AddBranchCurrent(br, np, nm);
            mna.AddConstraint(br, np, new Complex(+1, 0));
            mna.AddConstraint(br, nm, new Complex(-1, 0));
            mna.AddSourceValue(br, Complex.Zero);
        }
    }

    // w<2: built-in weights (1 for current, jω for charge) — fall through to base.
    // w≥2: evaluate H[w] expression at this frequency using the Complex Evaluator.
    public override Complex Weight(int w, double omega)
    {
        if (w < 2) return base.Weight(w, omega);
        if (!_weightAst.TryGetValue(w, out var ast))
            throw new InvalidOperationException($"SDD '{_name}': I[p,{w}] used but H[{w}] is not defined");
        return EvalWeight(ast, omega);
    }

    // H[w] is frequency-domain (Complex, freq-controlled) — use the general Evaluator,
    // NOT SddEvaluator (which is real-only, voltage-controlled, with dual AD).
    private Complex EvalWeight(Expr ast, double omega)
    {
        var scope = new Scope("Hw");
        scope.Bind("freq", (omega / (2 * Math.PI)).ToString("R", CultureInfo.InvariantCulture));
        foreach (var kv in _params)
            scope.Bind(kv.Key, kv.Value.ToString("R", CultureInfo.InvariantCulture));
        var v = new Evaluator().EvalExpr(ast, scope);
        return v.Kind == ValueKind.Complex ? v.AsComplex() : new Complex(v.AsReal(), 0);
    }

    /// <summary>
    /// Evaluate the SDD at the given port voltages (no control currents).
    /// Forwards to Evaluate(v, ControlCurrents.Empty).
    /// </summary>
    public override NonlinearResult Evaluate(in PortVoltages v)
        => Evaluate(v, ControlCurrents.Empty);


    /// <summary>
    /// Evaluate the SDD at the given port voltages and control currents.
    /// Returns (i, q, dg, dc, terms, dControl) — q/dc are zero for a resistive device;
    /// dControl = ∂I[p,0]/∂_cn (null when no control currents); terms carries w≥2 buckets.
    /// </summary>
    public override NonlinearResult Evaluate(in PortVoltages v, in ControlCurrents c)
    {
        NonlinearEvalDiagnostics.CountScalar();
        int n  = _portCount;
        int nC = c.Count;

        double[]  i      = new double[n];
        double[]  q      = new double[n];
        double[,] dg     = new double[n, n];
        double[,] dc     = new double[n, n];
        double[,]? dCtrl       = nC > 0 ? new double[n, nC] : null;
        double[,]? dCtrlCharge = nC > 0 ? new double[n, nC] : null;

        double[] vArr = v.Voltages;

        // Build (N, value)[] for control-current dual seeding (controls are at grad slots nV+i).
        var ctrlSeeds = nC > 0 ? BuildControlSeeds(c) : [];

        // Evaluate each current equation with Dual arithmetic, through the pre-compiled slot form
        // (§1.3 step 1) — ctrlSeeds is [] when nC == 0, so this is one unified path for both cases.
        for (int p = 0; p < n; p++)
        {
            var compiled = _compiledCurrent[p];
            if (compiled is null) continue;

            (double val, double[] grad) = compiled.EvalDual(vArr, ctrlSeeds, _name);
            i[p] = val;
            for (int k = 0; k < n; k++) dg[p, k] = grad[k];
            if (nC > 0)
                for (int k = 0; k < nC; k++) dCtrl![p, k] = grad[n + k];
        }

        // Evaluate each charge equation — same AD path. DC solver drops charge (jω=0).
        for (int p = 0; p < n; p++)
        {
            var compiled = _compiledCharge[p];
            if (compiled is null) continue;

            (double val, double[] grad) = compiled.EvalDual(vArr, ctrlSeeds, _name);
            q[p] = val;
            for (int k = 0; k < n; k++) dc[p, k] = grad[k];
            if (nC > 0)
                for (int k = 0; k < nC; k++) dCtrlCharge![p, k] = grad[n + k];  // ∂Q[p]/∂_cn (brief #3 §2)
        }

        // Evaluate each BRANCH equation — the right-hand side the constrained port's voltage is
        // held at, and its derivatives. Same AD path; there is deliberately no charge counterpart
        // (see NonlinearResult.BranchResidual for why one would double-count).
        double[]?  branchRes = null;
        double[,]? dBranchV  = null;
        double[,]? dBranchC  = null;
        if (BranchPorts.Length > 0)
        {
            int nB = BranchPorts.Length;
            branchRes = new double[nB];
            dBranchV  = new double[nB, n];
            dBranchC  = nC > 0 ? new double[nB, nC] : null;

            for (int k = 0; k < nB; k++)
            {
                var compiled = _compiledVoltage[BranchPorts[k]]!;
                (double val, double[] grad) = compiled.EvalDual(vArr, ctrlSeeds, _name);
                branchRes[k] = val;
                for (int t = 0; t < n; t++) dBranchV[k, t] = grad[t];
                if (nC > 0)
                    for (int t = 0; t < nC; t++) dBranchC![k, t] = grad[n + t];
            }
        }

        // Collect all distinct w≥2 values referenced across all ports.
        var wSet = new SortedSet<int>();
        foreach (var list in _higherAst)
            foreach (var (ww, _) in list)
                wSet.Add(ww);

        if (wSet.Count == 0)
            return new NonlinearResult(i, q, dg, dc, null, dCtrl, dCtrlCharge,
                                       branchRes, dBranchV, dBranchC);

        // Build one WeightedTerm per distinct w, accumulating contributions from all ports.
        var terms = new List<WeightedTerm>(wSet.Count);
        foreach (int w in wSet)
        {
            var val = new double[n];
            var jac = new double[n, n];
            var jacCtrl = nC > 0 ? new double[n, nC] : null;  // ∂I[p,w]/∂_cn (brief #3 §2)
            for (int p = 0; p < n; p++)
            {
                CompiledSddExpr? compiled = null;
                foreach (var (ww, cw) in _compiledHigher[p])
                    if (ww == w) { compiled = cw; break; }
                if (compiled is null) continue;

                // Seed control currents so _cn is valid in higher-w equations too (ctrlSeeds is []
                // when nC == 0 — one unified call, same as the current/charge loops above).
                (double fval, double[] grad) = compiled.EvalDual(vArr, ctrlSeeds, _name);
                val[p] = fval;
                for (int k = 0; k < n; k++) jac[p, k] = grad[k];
                if (nC > 0)
                    for (int k = 0; k < nC; k++) jacCtrl![p, k] = grad[n + k];
            }
            terms.Add(new WeightedTerm(w, val, jac, jacCtrl));
        }

        return new NonlinearResult(i, q, dg, dc, terms, dCtrl, dCtrlCharge,
                                   branchRes, dBranchV, dBranchC);
    }

    /// <summary>
    /// Small-signal linearization for the linear engines (S-parameter), at the supplied bias.
    /// Stamps the usual Y[p,q] = Σ_w H[w](ω)·∂I[p,w]/∂V_q admittance block, plus — when the SDD
    /// has C[n] control references — the control-current column coupling each port-KCL row to the
    /// referenced device's branch-current unknown:
    ///   ∂(KCL port p)/∂(I_branch,ref) = Σ_w H[w](ω)·∂I[p,w]/∂_cn.
    /// With no control refs this is byte-identical to the base ComponentModel.StampLinearized.
    /// The engine resolves ControlBranchIndices against the S-param MNA and seeds ControlBias
    /// (DC operating-point control currents) before calling this.
    /// </summary>
    public override void StampLinearized(IMnaContext mna, ElaboratedComponent c, double omega, in PortVoltages bias)
    {
        // Seed control currents at the DC operating point so the AD sensitivities are evaluated
        // at the right bias (exact for linear-in-_cn; ControlBias is zero in that case anyway).
        ControlCurrents ctrl = ControlRefs.Length > 0
            ? new ControlCurrents((double[])ControlBias.Clone())
            : ControlCurrents.Empty;
        var r = Evaluate(bias, ctrl);
        int P = PortCount;

        // ── the BRANCH rows, linearised about the same bias ───────────────────
        //
        // A constrained port states V(p+) − V(p−) = g(v, i). Its small-signal form is that relation
        // differentiated at the operating point, which is a constraint row exactly as the DC engine
        // builds one — the branch unknown, the ±1 KCL coupling, and the derivative of the
        // right-hand side moved to the left. The branch is re-allocated here rather than reused,
        // because branch numbering belongs to whichever assembly is being built.
        for (int k = 0; k < BranchPorts.Length; k++)
        {
            int p  = BranchPorts[k];
            int np = c.Nodes.Length > 2 * p     ? c.Nodes[2 * p]     : 0;
            int nm = c.Nodes.Length > 2 * p + 1 ? c.Nodes[2 * p + 1] : 0;

            int br = mna.AddBranch();
            _branchIndices[k] = br;
            mna.AddBranchCurrent(br, np, nm);
            mna.AddConstraint(br, np, new Complex(+1, 0));
            mna.AddConstraint(br, nm, new Complex(-1, 0));

            for (int q = 0; q < P; q++)
            {
                double d = r.DBranchV?[k, q] ?? 0.0;
                if (d == 0.0) continue;
                int qp = c.Nodes.Length > 2 * q     ? c.Nodes[2 * q]     : 0;
                int qm = c.Nodes.Length > 2 * q + 1 ? c.Nodes[2 * q + 1] : 0;
                mna.AddConstraint(br, qp, new Complex(-d, 0));
                mna.AddConstraint(br, qm, new Complex(+d, 0));
            }

            for (int nCtrl = 0; nCtrl < ControlRefs.Length; nCtrl++)
            {
                double d = r.DBranchC?[k, nCtrl] ?? 0.0;
                if (d == 0.0) continue;
                int other = ControlBranchIndices[nCtrl];
                if (other < 0) continue;
                mna.AddBranchConstraint(br, other, new Complex(-d, 0));
            }

            mna.AddSourceValue(br, Complex.Zero);
        }

        // ── Y[p,q] admittance block (same as the base implementation) ─────────
        for (int p = 0; p < P; p++)
        {
            int np = c.Nodes.Length > 2 * p     ? c.Nodes[2 * p]     : 0;
            int nm = c.Nodes.Length > 2 * p + 1 ? c.Nodes[2 * p + 1] : 0;
            for (int q = 0; q < P; q++)
            {
                int qp = c.Nodes.Length > 2 * q     ? c.Nodes[2 * q]     : 0;
                int qm = c.Nodes.Length > 2 * q + 1 ? c.Nodes[2 * q + 1] : 0;
                Complex y = new Complex(r.Dg[p, q], omega * r.Dc[p, q]);
                foreach (var term in r.Terms)
                    y += Weight(term.W, omega) * term.Jac[p, q];
                if (y == Complex.Zero) continue;
                mna.AddBlockAdmittance(np, qp,  y);
                mna.AddBlockAdmittance(np, qm, -y);
                mna.AddBlockAdmittance(nm, qp, -y);
                mna.AddBlockAdmittance(nm, qm,  y);
            }
        }

        // ── Control-current column: ∂(KCL port p)/∂(I_branch,ref) ─────────────
        if (ControlRefs.Length == 0) return;
        for (int p = 0; p < P; p++)
        {
            int np = c.Nodes.Length > 2 * p     ? c.Nodes[2 * p]     : 0;
            int nm = c.Nodes.Length > 2 * p + 1 ? c.Nodes[2 * p + 1] : 0;
            for (int nCtrl = 0; nCtrl < ControlRefs.Length; nCtrl++)
            {
                int branch = ControlBranchIndices[nCtrl];
                if (branch < 0) continue;   // engine errors earlier if a ref is truly unresolved
                Complex col = new Complex(r.DControl?[p, nCtrl] ?? 0, 0)
                            + Weight(1, omega) * (r.DControlCharge?[p, nCtrl] ?? 0);
                foreach (var term in r.Terms)
                    if (term.JacCtrl is not null)
                        col += Weight(term.W, omega) * term.JacCtrl[p, nCtrl];
                if (col == Complex.Zero) continue;
                // Mirror the DC engine's branch-column sign (NonlinearDcEngine: +dc at np, −dc at nm),
                // so DC and S-param agree at ω→0.
                mna.AddNodeBranchCoupling(np, branch, +col);
                mna.AddNodeBranchCoupling(nm, branch, -col);
            }
        }
    }

    // ── HB-P4 M2/M3: the whole time grid in one call ─────────────────────────────────────

    /// <inheritdoc/>
    /// <remarks>
    /// <para>False when some equation of this device contains a conditional: the active branch is
    /// per-sample, so there is no single instruction sequence to run across the grid, and that device
    /// keeps the scalar path unchanged until a later brief lifts conditionals into masked selects.</para>
    ///
    /// <para>False, too, for a device with <c>C[n]</c> CONTROL REFERENCES. <see cref="EvaluateGrid"/>
    /// can seed controls per sample and does — but no engine hands it any: HB produces
    /// <c>_c_ref(t)</c> through a two-pass self-consistent loop that calls the per-sample form, and
    /// it can call the device with NO control seeds at all (before the referenced branch index is
    /// resolved, or when the solve carries no control context). Declaring the door open on a device
    /// the caller will drive with an empty control span is a fault waiting for whichever fixture
    /// reaches that state — so the model, which is the one thing that knows it has controls, closes
    /// it. The engine's own <c>cRefTime is null</c> test stays as well.</para>
    /// </remarks>
    public override bool PrefersGridEvaluate
        => _supportsGrid && ControlRefs.Length == 0 && BranchPorts.Length == 0
        && !NonlinearEvalDiagnostics.DisableGridEvaluate;

    /// <summary>
    /// HB-P4 — evaluates every equation of this SDD at every sample of the grid, writing the
    /// structure-of-arrays result straight into the caller's buffers.
    ///
    /// <para>The gradient layout is why this costs no copy in the common case: the grid evaluator
    /// emits lane <c>k</c> of equation <c>p</c> at <c>k·S + t</c> from a base, and
    /// <see cref="GridResult.Dg"/> wants <c>(p·P + q)·S + t</c> — the same run of memory when the
    /// gradient width equals the port count. Only a control SDD (width P+C) needs the intermediate
    /// buffer, because its last C lanes belong to a different block.</para>
    /// </summary>
    public override void EvaluateGrid(
        ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
        int sampleCount, GridResult into)
    {
        if (!_supportsGrid)
        {
            base.EvaluateGrid(portVoltages, controlCurrents, sampleCount, into);
            return;
        }

        NonlinearEvalDiagnostics.CountGrid();

        int P = _portCount;
        int C = ControlRefs.Length;
        if (C > 0 && controlCurrents.Length < C * sampleCount)
            throw new ArgumentException(
                $"SDD '{_name}' has {C} control reference(s), so EvaluateGrid needs a " +
                $"[control][t] span of {C * sampleCount} values; got {controlCurrents.Length}.",
                nameof(controlCurrents));
        into.EnsureShape(P, C, sampleCount);
        into.ClearBlocks();
        into.ResetTerms();
        foreach (int w in _wOrder) into.AddTerm(w);
        if (sampleCount <= 0) return;

        var warn = new GridDomainWarnings();

        // Sized here, not lazily inside a worker: the chunks write disjoint runs of it, but growing
        // it from two threads at once would not be disjoint.
        if (C > 0) EnsureBuf(ref _gradBuf, (P + C) * sampleCount);

        if (sampleCount >= GridParallelThreshold && Environment.ProcessorCount > 1)
            EvalParallel(portVoltages, controlCurrents, P, C, sampleCount, into, ref warn);
        else
        {
            // One walk of the program for the whole grid. Splitting the serial walk into cache-sized
            // blocks was tried and MEASURED WORSE at every block size (529 ns/sample at 16 samples a
            // block against 250 at the whole 1,024-sample grid): the per-instruction setup is paid
            // once per block, so a small block multiplies it, and the register file streams
            // predictably enough that the cache pressure the blocking was meant to relieve never
            // showed up. See src/Core/RESOLVED.md HB-P4.
            EnsurePool(1);
            EvalChunk(portVoltages, controlCurrents, sampleCount, 0, sampleCount,
                      into, _scratchPool[0]!, ref warn);
        }

        warn.Emit(_name);
    }

    /// <summary>
    /// M3 — the samples split cleanly: every chunk runs the same program over its own slice of the
    /// grid, into its own registers, and writes into disjoint runs of the caller's result. Only the
    /// warning collectors need joining, and they merge in chunk order so the reported argument is
    /// the one a serial run would have reported.
    ///
    /// <para>It is a separate method for an allocation reason, not a tidiness one: a lambda's
    /// closure object is allocated where its captured LOCALS are declared, not where the lambda
    /// appears, so leaving the <c>Parallel.For</c> inline in <see cref="EvaluateGrid"/> put a
    /// 40-byte display-class allocation on every serial call too — small, but it made the
    /// allocation-free claim false at 32 samples exactly as much as at 1,024.</para>
    /// </summary>
    private void EvalParallel(
        ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
        int P, int C, int sampleCount, GridResult into, ref GridDomainWarnings warn)
    {
        // Twice the core count, not once: more chunks than cores is what lets a core that finishes
        // early pick up another, and the measured optimum sat around there rather than at exactly
        // one chunk per core.
        int chunks = Math.Min(2 * Environment.ProcessorCount, sampleCount);
        int perChunk = (sampleCount + chunks - 1) / chunks;
        chunks = (sampleCount + perChunk - 1) / perChunk;

        // A lambda cannot close over a span, so the inputs are copied once into buffers the model
        // keeps — P·S doubles against a grid of P·S evaluations, and only above the threshold.
        EnsureBuf(ref _portCopy, P * sampleCount);
        EnsureBuf(ref _ctrlCopy, Math.Max(1, C * sampleCount));
        portVoltages[..(P * sampleCount)].CopyTo(_portCopy);
        if (C > 0) controlCurrents[..(C * sampleCount)].CopyTo(_ctrlCopy);
        if (_chunkWarn.Length < chunks) _chunkWarn = new GridDomainWarnings[chunks];
        Array.Clear(_chunkWarn, 0, chunks);
        EnsurePool(chunks);

        var portCopy = _portCopy;
        var ctrlCopy = _ctrlCopy;
        Parallel.For(0, chunks, ci =>
        {
            int t0 = ci * perChunk;
            int count = Math.Min(perChunk, sampleCount - t0);
            EvalChunk(portCopy, C > 0 ? ctrlCopy : [], sampleCount, t0, count,
                      into, _scratchPool[ci]!, ref _chunkWarn[ci]);
        });
        for (int ci = 0; ci < chunks; ci++) warn.Merge(_chunkWarn[ci]);
    }

    /// <summary>Runs every equation over samples <c>[t0, t0+count)</c> on one worker's scratch.</summary>
    private void EvalChunk(
        ReadOnlySpan<double> portV, ReadOnlySpan<double> ctrlV, int stride, int t0, int count,
        GridResult into, GridScratch scratch, ref GridDomainWarnings warn)
    {
        int P = _portCount;
        int C = ControlRefs.Length;

        for (int p = 0; p < P; p++)
        {
            if (_compiledCurrent[p] is { } cur)
                RunEquation(cur, portV, ctrlV, stride, t0, count, scratch, ref warn,
                            into.I.AsSpan(into.PortBase(p), stride),
                            into.Dg.AsSpan(into.JacBase(p, 0), P * stride),
                            C > 0 ? into.DControl.AsSpan(into.CtrlBase(p, 0), C * stride) : [],
                            P, C, stride);

            if (_compiledCharge[p] is { } chg)
                RunEquation(chg, portV, ctrlV, stride, t0, count, scratch, ref warn,
                            into.Q.AsSpan(into.PortBase(p), stride),
                            into.Dc.AsSpan(into.JacBase(p, 0), P * stride),
                            C > 0 ? into.DControlCharge.AsSpan(into.CtrlBase(p, 0), C * stride) : [],
                            P, C, stride);

            var live = into.LiveTerms;
            foreach (var (w, compiled) in _compiledHigher[p])
            {
                GridWeightedTerm? dst = null;
                for (int b = 0; b < live.Length; b++) if (live[b].W == w) { dst = live[b]; break; }
                if (dst is null) continue;
                RunEquation(compiled, portV, ctrlV, stride, t0, count, scratch, ref warn,
                            dst.Value.AsSpan(into.PortBase(p), stride),
                            dst.Jac.AsSpan(into.JacBase(p, 0), P * stride),
                            C > 0 ? dst.JacCtrl.AsSpan(into.CtrlBase(p, 0), C * stride) : [],
                            P, C, stride);
            }
        }
    }

    private void RunEquation(
        CompiledSddExpr compiled, ReadOnlySpan<double> portV, ReadOnlySpan<double> ctrlV,
        int stride, int t0, int count, GridScratch scratch, ref GridDomainWarnings warn,
        Span<double> value, Span<double> jac, Span<double> jacCtrl, int P, int C, int gradStride)
    {
        if (C == 0)
        {
            // The evaluator's own gradient layout IS the Jacobian block's — write in place.
            compiled.EvalDualGrid(portV, ctrlV, stride, t0, count, value, jac, scratch, _name, ref warn);
            return;
        }

        compiled.EvalDualGrid(portV, ctrlV, stride, t0, count, value, _gradBuf, scratch, _name, ref warn);
        for (int k = 0; k < P; k++)
            _gradBuf.AsSpan(k * gradStride + t0, count).CopyTo(jac.Slice(k * gradStride + t0, count));
        for (int k = 0; k < C; k++)
            _gradBuf.AsSpan((P + k) * gradStride + t0, count).CopyTo(jacCtrl.Slice(k * gradStride + t0, count));
    }

    private void EnsurePool(int workers)
    {
        if (_scratchPool.Length < workers)
        {
            var grown = new GridScratch?[workers];
            Array.Copy(_scratchPool, grown, _scratchPool.Length);
            _scratchPool = grown;
        }
        for (int i = 0; i < workers; i++) _scratchPool[i] ??= new GridScratch();
    }

    private static void EnsureBuf(ref double[] buf, int need)
    {
        if (buf.Length < need) buf = new double[need];
    }

    private (int N, double Value)[] BuildControlSeeds(in ControlCurrents c)
    {
        var seeds = new (int N, double Value)[ControlRefs.Length];
        for (int i = 0; i < ControlRefs.Length; i++)
            seeds[i] = (ControlRefs[i].N, c[i]);
        return seeds;
    }
}
