using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>
/// A two-port given by its ABCD (chain / transmission) matrix, with entries as expressions in the
/// reserved keyword <c>freq</c> (Hz) — the same expression convention <see cref="ZPortModel"/> uses.
///
/// <para>Convention (standard, with both port currents defined flowing INTO the device so it matches
/// every other model here):</para>
/// <code>
///   V1 = A·V2 − B·I2
///   I1 = C·V2 − D·I2
/// </code>
/// <para>where V1 = V(Nodes[0]) − V(Nodes[1]) and V2 = V(Nodes[2]) − V(Nodes[3]). Four nets, ordered
/// as ± pairs exactly like a 2-port <c>Z_Port</c>: <c>[p1+, p1−, p2+, p2−]</c>.</para>
///
/// <para><b>Why this exists when Z_Port already does.</b> A chain matrix describes two-ports that
/// have no impedance matrix at all. The commonest case is the one that matters: a pure series
/// element has C = 0, and Z-parameters are then infinite — <c>Z11 = A/C</c>. Many frequency-domain
/// line models degenerate to exactly that at DC (A = D = 1, C = 0, B = the series resistance), so a
/// model that is perfectly well-behaved in ABCD form cannot be expressed as a Z-block at ω = 0.
/// Stamping the chain relations directly avoids the conversion entirely and stays non-singular
/// there — with C = 0 and D = 1 the second constraint reduces to I1 = −I2 and the first to
/// V1 − V2 = B·I1, which is just a series impedance.</para>
///
/// <para>Group 2: two branch-current unknowns (I1, I2), one per port.</para>
/// </summary>
public sealed class ChainModel : ComponentModel
{
    public override int       PortCount => 2;
    public override ModelKind Kind      => ModelKind.Linear;

    private readonly Expr?  _a, _b, _c, _d;
    /// <summary>
    /// Netlist-declared expression functions, or null. Needed because this model builds its
    /// evaluator at stamp time — long after elaboration — and one constructed empty cannot resolve
    /// a call to a function the netlist declared.
    /// </summary>
    private readonly IReadOnlyList<UserFunction>? _functions;

    private readonly IReadOnlyDictionary<string, Value> _scopeVars;
    private readonly string _name;

    /// <summary>Branch indices for port 1 and port 2, set during each Stamp. -1 before first stamp.</summary>
    public int[] PortBranchIndices { get; } = [-1, -1];

    public ChainModel(Expr? a, Expr? b, Expr? c, Expr? d,
        IReadOnlyDictionary<string, Value> scopeVars, string name,
        IReadOnlyList<UserFunction>? functions = null)
    {
        _a = a; _b = b; _c = c; _d = d;
        _scopeVars = scopeVars;
        _functions = functions;
        _name      = name;
    }

    public override void Stamp(IMnaContext mna, ElaboratedComponent comp, double omega)
    {
        double freqHz = omega / (2.0 * Math.PI);
        var (a, b, c, d) = Evaluate(freqHz);

        int n1p = comp.Nodes[0], n1m = comp.Nodes[1];
        int n2p = comp.Nodes[2], n2m = comp.Nodes[3];

        int br1 = mna.AddBranch();
        int br2 = mna.AddBranch();
        PortBranchIndices[0] = br1;
        PortBranchIndices[1] = br2;

        // KCL: each port current flows from that port's + node to its − node.
        mna.AddBranchCurrent(br1, n1p, n1m);
        mna.AddBranchCurrent(br2, n2p, n2m);

        // Constraint 1:  V1 − A·V2 + B·I2 = 0
        mna.AddConstraint(br1, n1p, Complex.One);
        if (n1m > 0) mna.AddConstraint(br1, n1m, -Complex.One);
        mna.AddConstraint(br1, n2p, -a);
        if (n2m > 0) mna.AddConstraint(br1, n2m, a);
        mna.AddBranchConstraint(br1, br2, b);

        // Constraint 2:  I1 − C·V2 + D·I2 = 0
        mna.AddBranchConstraint(br2, br1, Complex.One);
        mna.AddConstraint(br2, n2p, -c);
        if (n2m > 0) mna.AddConstraint(br2, n2m, c);
        mna.AddBranchConstraint(br2, br2, d);
    }

    private (Complex A, Complex B, Complex C, Complex D) Evaluate(double freqHz)
    {
        var scope = new Scope($"Chain:{_name}");
        foreach (var kv in _scopeVars)
            scope.Bind(kv.Key, kv.Value.ToString()!);

        var ev = new Evaluator();
        foreach (var fn in _functions ?? []) ev.RegisterFunction(fn);
        foreach (var kv in _scopeVars)
            ev.InjectResolved($"Chain:{_name}", kv.Key, kv.Value);
        scope.Bind("freq", freqHz.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        ev.InjectResolved($"Chain:{_name}", "freq", new Value(freqHz));

        Complex One(Expr? e, Complex fallback)
        {
            if (e is null) return fallback;
            var v = ev.EvalExpr(e, scope);
            return v.Kind == ValueKind.Real ? new Complex(v.AsReal(), 0) : v.AsComplex();
        }

        // Defaults form the identity two-port (a through connection), so a partially-specified
        // chain block degrades to a wire rather than to a silent zero matrix.
        return (One(_a, Complex.One), One(_b, Complex.Zero),
                One(_c, Complex.Zero), One(_d, Complex.One));
    }
}
