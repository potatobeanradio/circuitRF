using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// General frequency-controlled impedance N-port (linear-engine §4.4).
/// Group 2 (branch-current unknowns). Z[i,j] matrix entries are expressions
/// in the reserved keyword `freq` (Hz), evaluated per stamped frequency.
///
/// Stamped by the Z(ω) branch-current expansion — N branches, one per port.
/// 2N nets ordered as ± pairs: Nodes[2p] = port p+, Nodes[2p+1] = port p−.
/// Each port has its own independent reference (V_p = V(Nodes[2p]) − V(Nodes[2p+1])).
///
/// Hero 2 usage: per-harmonic source/load terminations with piecewise Z(freq).
/// </summary>
public sealed class ZPortModel : ComponentModel
{
    public override int       PortCount => _portCount;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly int                               _portCount;
    private readonly Expr?[,]                          _zExprs;          // [p-1, q-1] 0-based
    private readonly IReadOnlyDictionary<string, Value> _scopeVars;      // resolved globals
    private readonly string                            _name;

    /// <summary>
    /// Branch indices per port, set during each Stamp call.
    /// PortBranchIndices[k] = branch index for port k (0-based).
    /// -1 before first stamp.
    /// </summary>
    public int[] PortBranchIndices { get; private set; }

    public ZPortModel(int portCount, Expr?[,] zExprs,
        IReadOnlyDictionary<string, Value> scopeVars, string name)
    {
        _portCount        = portCount;
        _zExprs           = zExprs;
        _scopeVars        = scopeVars;
        _name             = name;
        PortBranchIndices = new int[portCount];
        for (int k = 0; k < portCount; k++) PortBranchIndices[k] = -1;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double freqHz = omega / (2.0 * Math.PI);

        // Evaluate Z[i,j] at this frequency.
        var z = EvaluateZ(freqHz);

        // 2N nets: Nodes[2p] = port p+, Nodes[2p+1] = port p−. Per-port reference.
        var branches = new int[_portCount];
        for (int p = 0; p < _portCount; p++)
        {
            branches[p]          = mna.AddBranch();
            PortBranchIndices[p] = branches[p];
        }

        for (int p = 0; p < _portCount; p++)
        {
            int nodePlus  = c.Nodes[2 * p];
            int nodeMinus = c.Nodes[2 * p + 1];
            mna.AddBranchCurrent(branches[p], nodePlus, nodeMinus);
        }

        // Constraints: V_p − V_ref_p − Σ_q Z[p,q]·I_q = 0  (V_ref_p = V(nodeMinus))
        for (int p = 0; p < _portCount; p++)
        {
            int nodePlus  = c.Nodes[2 * p];
            int nodeMinus = c.Nodes[2 * p + 1];
            mna.AddConstraint(branches[p], nodePlus,  new Complex(+1, 0));
            if (nodeMinus > 0)
                mna.AddConstraint(branches[p], nodeMinus, new Complex(-1, 0));

            for (int q = 0; q < _portCount; q++)
                mna.AddBranchConstraint(branches[p], branches[q], -z[p, q]);
        }
    }

    private Complex[,] EvaluateZ(double freqHz)
    {
        var z = new Complex[_portCount, _portCount];

        // Build a scope with freq + all resolved globals injected.
        var scope = new Scope($"ZPort:{_name}");
        foreach (var kv in _scopeVars)
            scope.Bind(kv.Key, kv.Value.ToString()!);

        var ev = new Evaluator();
        // Pre-inject resolved globals to avoid re-parsing their expressions.
        foreach (var kv in _scopeVars)
            ev.InjectResolved($"ZPort:{_name}", kv.Key, kv.Value);
        // Inject freq.
        scope.Bind("freq", freqHz.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        ev.InjectResolved($"ZPort:{_name}", "freq", new Value(freqHz));

        for (int p = 0; p < _portCount; p++)
        for (int q = 0; q < _portCount; q++)
        {
            var expr = _zExprs[p, q];
            if (expr is null) continue;
            var val = ev.EvalExpr(expr, scope);
            z[p, q] = val.Kind == ValueKind.Real
                ? new Complex(val.AsReal(), 0)
                : val.AsComplex();
        }

        return z;
    }
}
