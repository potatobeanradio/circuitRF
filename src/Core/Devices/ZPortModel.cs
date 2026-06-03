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
/// Reference-node: same N-or-(N+1) convention as SnP (linear-engine §4.1).
/// For the common 1-port case: 2 nets (n+, n−), 1 branch.
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

    public ZPortModel(int portCount, Expr?[,] zExprs,
        IReadOnlyDictionary<string, Value> scopeVars, string name)
    {
        _portCount = portCount;
        _zExprs    = zExprs;
        _scopeVars = scopeVars;
        _name      = name;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        double freqHz = omega / (2.0 * Math.PI);

        // Evaluate Z[i,j] at this frequency.
        var z = EvaluateZ(freqHz);

        // Identify port nodes (N nets for N ports; reference = c.ReferenceNode).
        // Each port has one net (port+); the reference is shared across all ports.
        int refNode = c.ReferenceNode;  // 0 = ground

        // Allocate N branch-current unknowns, one per port.
        var branches = new int[_portCount];
        for (int p = 0; p < _portCount; p++)
            branches[p] = mna.AddBranch();

        // KCL: each branch current flows from its port+ node to the reference node.
        for (int p = 0; p < _portCount; p++)
        {
            int nodeP = c.Nodes[p];
            mna.AddBranchCurrent(branches[p], nodeP, refNode);
        }

        // Constraints: V_p - V_ref - Σ_q Z[p+1,q+1] * I_q = 0
        for (int p = 0; p < _portCount; p++)
        {
            int nodeP = c.Nodes[p];
            mna.AddConstraint(branches[p], nodeP,    new Complex(+1, 0));
            if (refNode > 0)
                mna.AddConstraint(branches[p], refNode, new Complex(-1, 0));

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
