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
    private readonly (int W, CompiledSddExpr Compiled)[][] _compiledHigher;

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
        IReadOnlyList<(int N, string RefInstance, int Port)>? controlRefs = null)
    {
        _name       = name;
        _portCount  = portCount;
        _currentAst = currentAst;
        _chargeAst  = chargeAst;
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
        _compiledHigher  = new (int W, CompiledSddExpr Compiled)[_portCount][];
        for (int p = 0; p < _portCount; p++)
        {
            if (_currentAst[p] is { } ca) _compiledCurrent[p] = CompiledSddExpr.Compile(ca, _params, _portCount, controlNs, _name);
            if (_chargeAst[p]  is { } qa) _compiledCharge[p]  = CompiledSddExpr.Compile(qa, _params, _portCount, controlNs, _name);

            var higher = _higherAst[p];
            var compiled = new (int, CompiledSddExpr)[higher.Count];
            for (int j = 0; j < higher.Count; j++)
                compiled[j] = (higher[j].W, CompiledSddExpr.Compile(higher[j].Ast, _params, _portCount, controlNs, _name));
            _compiledHigher[p] = compiled;
        }
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

    // SDD contributes nothing to linear stamps — fully nonlinear.
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    { }

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

        // Collect all distinct w≥2 values referenced across all ports.
        var wSet = new SortedSet<int>();
        foreach (var list in _higherAst)
            foreach (var (ww, _) in list)
                wSet.Add(ww);

        if (wSet.Count == 0)
            return new NonlinearResult(i, q, dg, dc, null, dCtrl, dCtrlCharge);

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

        return new NonlinearResult(i, q, dg, dc, terms, dCtrl, dCtrlCharge);
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

    private (int N, double Value)[] BuildControlSeeds(in ControlCurrents c)
    {
        var seeds = new (int N, double Value)[ControlRefs.Length];
        for (int i = 0; i < ControlRefs.Length; i++)
            seeds[i] = (ControlRefs[i].N, c[i]);
        return seeds;
    }
}
