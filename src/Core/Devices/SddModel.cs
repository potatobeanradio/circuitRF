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

    // Resolved scope variables (B, Sc, TV0, …) from the .cnl elaboration scope.
    // These are constants at eval time — they don't change per Newton step.
    private readonly IReadOnlyDictionary<string, double> _params;

    public SddModel(
        string name,
        int portCount,
        Expr?[] currentAst,
        Expr?[] chargeAst,
        IReadOnlyDictionary<string, double> parameters)
    {
        _name       = name;
        _portCount  = portCount;
        _currentAst = currentAst;
        _chargeAst  = chargeAst;
        _params     = parameters;
    }

    public override int       PortCount => _portCount;
    public override ModelKind Kind      => ModelKind.Nonlinear;

    // SDD contributes nothing to linear stamps — fully nonlinear.
    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    { }

    /// <summary>
    /// Evaluate the SDD at the given port voltages.
    /// Returns (i, q, dg, dc) — q/dc are zero for a resistive device (no I[p,1] equations).
    /// </summary>
    public override NonlinearResult Evaluate(in PortVoltages v)
    {
        int n = _portCount;
        double[] i  = new double[n];
        double[] q  = new double[n];
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

        return new NonlinearResult(i, q, dg, dc);
    }
}
