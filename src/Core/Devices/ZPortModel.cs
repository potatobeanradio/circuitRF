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

    /// <summary>
    /// <see cref="Core.Activity.ActiveExact"/> at the TYPE level, for the same reason
    /// <c>ResistorModel</c> is: a <c>Z(ω)</c> whose real part goes negative somewhere in the sweep
    /// is a negative resistance, which the reference document's §8 (p. 111) requires the passive
    /// determinant to render passive — and the class cannot know its own expressions' values here.
    /// For a block that is passive at every frequency the two stamps are identical entry for entry.
    /// </summary>
    public override Activity Activity => Activity.ActiveExact;

    /// <inheritdoc/>
    public override string? PassivationNote => _passivationNote ??
        "Re Z → |Re Z| on the diagonal, which is its ordinary stamp unless some Re Z < 0";
    private string? _passivationNote;

    /// <summary>
    /// This instance's own answer over the run's frequencies: <see cref="Core.Activity.Passive"/> when its impedance
    /// matrix has <c>Re Z ⪰ 0</c> (<c>Z + Zᴴ</c> positive semidefinite) at every one of them, so the S-parameter noise
    /// analysis gives it thermal noise like any passive linear part. The test is σ_max of its S at a real 50 Ω
    /// reference, which is at most 1 exactly when <c>Re Z ⪰ 0</c> — the reference cancels out of the question, as it does
    /// for a <c>Chain</c>. Otherwise the type-level <see cref="Core.Activity.ActiveExact"/> stands.
    /// </summary>
    public override Activity ActivityFor(ElaboratedComponent c, IReadOnlyList<double> freqsHz)
    {
        const double Tol = 1e-9, Z0 = 50.0;
        double worst = 0.0, at = double.NaN;
        foreach (double hz in freqsHz)
        {
            var z = EvaluateZ(hz);
            var m = new NumFlat.Mat<Complex>(_portCount, _portCount);
            for (int p = 0; p < _portCount; p++)
                for (int q = 0; q < _portCount; q++) m[p, q] = z[p, q];
            double sig;
            try { sig = RfCore.RFNetwork.Passivity(RfCore.RFNetwork.ZToS(m, new Complex(Z0, 0))); }
            catch (Exception) { sig = double.PositiveInfinity; }
            if (!(sig <= worst)) { worst = sig; at = hz; }
        }
        if (freqsHz.Count > 0 && worst <= 1.0 + Tol)
        {
            _passivationNote = $"Re Z ⪰ 0 over the swept band (σ_max = {worst:G4} at 50 Ohm), so its stamp is already passive";
            return Activity.Passive;
        }
        _passivationNote = null;
        return Activity;
    }

    /// <summary><c>Re Z → |Re Z|</c> per entry of the impedance matrix's DIAGONAL, which is where a
    /// driving-point negative resistance lives; the off-diagonal transfer terms are untouched, being
    /// the block's own reciprocity rather than a source.</summary>
    public override void StampPassive(IMnaContext mna, ElaboratedComponent c, double omega)
        => StampZ(mna, c, omega, passive: true);

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
        => StampZ(mna, c, omega, passive: false);

    /// <summary>
    /// <c>Y = Z⁻¹</c> of the block's own impedance matrix, passivated or not — so R-wsp6-5's
    /// <c>Y + Yᴴ ⪰ 0</c> guard covers the one <see cref="Core.Activity.ActiveExact"/> passivation
    /// that is NOT passive by construction: taking <c>|Re Z|</c> on the diagonal says nothing about
    /// the off-diagonal transfer terms, which are left as written.
    /// </summary>
    public override Complex[,]? LinearisedPortAdmittance(
        ElaboratedComponent c, double omega, in PortVoltages bias, bool passivated)
    {
        var z = EvaluateZ(omega / (2.0 * Math.PI));
        var a = new Complex[_portCount, _portCount];
        for (int p = 0; p < _portCount; p++)
            for (int q = 0; q < _portCount; q++) a[p, q] = z[p, q];
        if (passivated)
            for (int p = 0; p < _portCount; p++)
                if (a[p, p].Real < 0.0) a[p, p] = new Complex(-a[p, p].Real, a[p, p].Imaginary);
        return Invert(a);
    }

    /// <summary>Gauss-Jordan with partial pivoting on a small dense complex matrix; null when
    /// singular (an impedance matrix with no admittance form is not a passivity failure, and the
    /// guard skips it rather than reporting a NaN).</summary>
    private static Complex[,]? Invert(Complex[,] a)
    {
        int n = a.GetLength(0);
        var m = (Complex[,])a.Clone();
        var inv = new Complex[n, n];
        for (int i = 0; i < n; i++) inv[i, i] = Complex.One;

        for (int k = 0; k < n; k++)
        {
            int piv = k; double best = m[k, k].Magnitude;
            for (int i = k + 1; i < n; i++)
                if (m[i, k].Magnitude > best) { best = m[i, k].Magnitude; piv = i; }
            if (best == 0.0) return null;
            if (piv != k)
                for (int j = 0; j < n; j++)
                {
                    (m[k, j], m[piv, j])     = (m[piv, j], m[k, j]);
                    (inv[k, j], inv[piv, j]) = (inv[piv, j], inv[k, j]);
                }

            var d = m[k, k];
            for (int j = 0; j < n; j++) { m[k, j] /= d; inv[k, j] /= d; }
            for (int i = 0; i < n; i++)
            {
                if (i == k) continue;
                var f = m[i, k];
                if (f == Complex.Zero) continue;
                for (int j = 0; j < n; j++) { m[i, j] -= f * m[k, j]; inv[i, j] -= f * inv[k, j]; }
            }
        }
        return inv;
    }

    private void StampZ(IMnaContext mna, ElaboratedComponent c, double omega, bool passive)
    {
        double freqHz = omega / (2.0 * Math.PI);

        // Evaluate Z[i,j] at this frequency (memoized — see EvaluateZ).
        var z = EvaluateZ(freqHz);
        if (passive)
        {
            z = (Complex[,])z.Clone();
            for (int p = 0; p < _portCount; p++)
                if (z[p, p].Real < 0.0) z[p, p] = new Complex(-z[p, p].Real, z[p, p].Imaginary);
        }

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
