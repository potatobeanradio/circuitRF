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

    /// <summary>
    /// Netlist-declared expression functions, or null. Needed because this model builds its
    /// evaluator at stamp time — long after elaboration — and one constructed empty cannot resolve
    /// a call to a function the netlist declared.
    /// </summary>
    private readonly IReadOnlyList<UserFunction>? _functions;

    public ZPortModel(int portCount, Expr?[,] zExprs,
        IReadOnlyDictionary<string, Value> scopeVars, string name,
        IReadOnlyList<UserFunction>? functions = null)
    {
        _functions        = functions;
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

        // Evaluate Z[i,j] at this frequency (memoized — see EvaluateZ).
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

    // Z(freq) is a PURE function of freq for the life of the model: _zExprs, _scopeVars and
    // _functions are all fixed at construction (ComponentModelFactory builds _scopeVars as its own
    // private dictionary of the already-resolved numeric parameters, and nothing hands out a
    // reference to it). So an evaluation at a frequency already seen can only reproduce the answer
    // it gave before, and the memo below is exact rather than approximate.
    //
    // This adds no sharing constraint that was not already there: Stamp already writes
    // PortBranchIndices on the model, so one model instance was never stampable from two threads at
    // once. The parallel S-parameter path elaborates its own netlist per worker for exactly that
    // reason, so each worker holds its own model and its own memo.
    //
    // Why it matters (HB-P2): the HB extractor stamps the linear partition once per harmonic per
    // solve, and a loadpull runs hundreds of solves against one topology. Rebuilding a Scope and an
    // Evaluator and re-injecting every resolved global — one string format per global, per stamp —
    // measured as ~80% of the whole linear-partition stamp on Hero 2 (6.7 us of 8.7 us, 6.2 KB of
    // 6.5 KB). The memo makes a repeat stamp a dictionary lookup.
    private Dictionary<double, Complex[,]>? _zCache;
    private Scope?     _scope;
    private Evaluator? _ev;

    private Complex[,] EvaluateZ(double freqHz)
    {
        _zCache ??= [];
        if (_zCache.TryGetValue(freqHz, out var cached)) return cached;

        if (_scope is null || _ev is null)
        {
            // Build the scope with all resolved globals injected — once, not once per stamp.
            _scope = new Scope($"ZPort:{_name}");
            foreach (var kv in _scopeVars)
                _scope.Bind(kv.Key, kv.Value.ToString()!);

            _ev = new Evaluator();
            foreach (var fn in _functions ?? []) _ev.RegisterFunction(fn);
            // Pre-inject resolved globals to avoid re-parsing their expressions.
            foreach (var kv in _scopeVars)
                _ev.InjectResolved($"ZPort:{_name}", kv.Key, kv.Value);
        }

        // Inject freq (the only thing that changes between evaluations).
        _scope.Bind("freq", freqHz.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        _ev.InjectResolved($"ZPort:{_name}", "freq", new Value(freqHz));

        var z = new Complex[_portCount, _portCount];
        for (int p = 0; p < _portCount; p++)
        for (int q = 0; q < _portCount; q++)
        {
            var expr = _zExprs[p, q];
            if (expr is null) continue;
            var val = _ev.EvalExpr(expr, _scope);
            z[p, q] = val.Kind == ValueKind.Real
                ? new Complex(val.AsReal(), 0)
                : val.AsComplex();
        }

        _zCache[freqHz] = z;
        return z;
    }
}
