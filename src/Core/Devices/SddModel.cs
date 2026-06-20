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

    public SddModel(
        string name,
        int portCount,
        Expr?[] currentAst,
        Expr?[] chargeAst,
        IReadOnlyDictionary<string, double> parameters,
        IReadOnlyList<(int W, Expr Ast)>[]? higherAst = null,
        IReadOnlyDictionary<int, Expr>? weightAst = null)
    {
        _name       = name;
        _portCount  = portCount;
        _currentAst = currentAst;
        _chargeAst  = chargeAst;
        _params     = parameters;
        _higherAst  = higherAst
            ?? Enumerable.Range(0, portCount).Select(_ => (IReadOnlyList<(int, Expr)>)[]).ToArray();
        _weightAst  = weightAst ?? new Dictionary<int, Expr>();
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
    /// Evaluate the SDD at the given port voltages.
    /// Returns (i, q, dg, dc, terms) — q/dc are zero for a resistive device (no I[p,1] equations);
    /// terms carries w≥2 buckets when I[p,w≥2] equations are present.
    /// </summary>
    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = _portCount;
        double[] i   = new double[n];
        double[] q   = new double[n];
        double[,] dg = new double[n, n];
        double[,] dc = new double[n, n];

        double[] vArr = v.Voltages;

        // Evaluate each current equation with Dual arithmetic.
        for (int p = 0; p < n; p++)
        {
            var ast = _currentAst[p];
            if (ast is null) continue;

            (double val, double[] grad) = SddEvaluator.EvalDual(ast, _params, vArr, _name);
            i[p] = val;
            for (int k = 0; k < n; k++) dg[p, k] = grad[k];
        }

        // Evaluate each charge equation — same AD path. DC solver drops charge (jω=0),
        // but plumbs it through so HB (Phase 4) inherits the path unchanged.
        for (int p = 0; p < n; p++)
        {
            var ast = _chargeAst[p];
            if (ast is null) continue;

            (double val, double[] grad) = SddEvaluator.EvalDual(ast, _params, vArr, _name);
            q[p] = val;
            for (int k = 0; k < n; k++) dc[p, k] = grad[k];
        }

        // Collect all distinct w≥2 values referenced across all ports.
        var wSet = new SortedSet<int>();
        foreach (var list in _higherAst)
            foreach (var (ww, _) in list)
                wSet.Add(ww);

        if (wSet.Count == 0)
            return new NonlinearResult(i, q, dg, dc);

        // Build one WeightedTerm per distinct w, accumulating contributions from all ports.
        var terms = new List<WeightedTerm>(wSet.Count);
        foreach (int w in wSet)
        {
            var val = new double[n];
            var jac = new double[n, n];
            for (int p = 0; p < n; p++)
            {
                Expr? ast = null;
                foreach (var (ww, a) in _higherAst[p])
                    if (ww == w) { ast = a; break; }
                if (ast is null) continue;

                (double fval, double[] grad) = SddEvaluator.EvalDual(ast, _params, vArr, _name);
                val[p] = fval;
                for (int k = 0; k < n; k++) jac[p, k] = grad[k];
            }
            terms.Add(new WeightedTerm(w, val, jac));
        }

        return new NonlinearResult(i, q, dg, dc, terms);
    }
}
