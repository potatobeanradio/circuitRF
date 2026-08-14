namespace CircuitRF.Core.Expressions;

/// <summary>
/// §1.3 steps 1 &amp; 3 (brief-harmonicarf-r3b) — the compiled, slot-resolved form of one SDD port
/// equation. Built ONCE (by <see cref="Compile"/>, called from <c>SddModel</c>'s constructor alongside
/// where it already caches the parsed AST) and reused for every subsequent evaluation — one per Newton
/// iteration, so this is the hot path <c>SddModel.Evaluate</c> was paying full re-resolution cost on
/// every single call.
///
/// <para><b>What is precomputed, once, at compile time:</b> the name → slot map (port voltages
/// "_v1".."_vN" at slots 0..N-1, control currents at slots N..N+C-1 in <c>controlNs</c>'s order,
/// every parameter name after that); the parameters' constant <see cref="Dual"/> values (zero
/// gradient, correct width) — a parameter's <c>Dual</c> never changes across calls, so building it
/// once here instead of on every <c>Evaluate</c> is exactly the win SddModel's own comment already
/// claims ("These are constants at eval time — they don't change per Newton step"). And — step 3 —
/// the whole expression is flattened into a linear array of three-address instructions
/// (<see cref="SddRegisterCompiler"/>): no boxed node objects, no recursion, one register per
/// instruction, walked by a single tight loop. That is used whenever the equation contains no
/// conditional (every real SDD equation in this repository today); an equation that DOES contain one
/// falls back to step 1's node-tree walk (<see cref="SddCompiler"/>), which is still correct and still
/// faster than the pre-step-1 dictionary path — see the register compiler's own remarks for why a
/// hand-rolled bytecode VM's branch handling is not worth building for a construct with no measured
/// real-world use.</para>
///
/// <para><b>What still happens per call:</b> only the port-voltage/control-current seeds (real,
/// per-iteration values) are written into a small register buffer, then the compiled program runs. No
/// dictionary, no string interpolation, no string hash, no per-node type dispatch.</para>
/// </summary>
public sealed class CompiledSddExpr
{
    private readonly int _nV, _nC, _n;
    private readonly Dual[] _paramDuals;
    private readonly int _totalSlots;

    // The step-3 register program — used whenever the equation has no conditional.
    private readonly RInstr[]? _code;
    private readonly Func<double, double>[] _mathFns = [];
    private readonly Func<Exception>[] _exFns = [];
    private readonly int _rootReg;

    // The step-1 fallback — only built (non-null) when the equation DOES contain a conditional.
    private readonly CNode? _root;

    private CompiledSddExpr(
        int nV, int nC, double[] paramValues,
        RInstr[]? code, Func<double, double>[] mathFns, Func<Exception>[] exFns, int rootReg,
        CNode? root)
    {
        _nV = nV;
        _nC = nC;
        _n = nV + nC;
        _totalSlots = _n + paramValues.Length;

        _paramDuals = new Dual[paramValues.Length];
        for (int i = 0; i < paramValues.Length; i++)
            _paramDuals[i] = Dual.Param(paramValues[i], _n);

        _code = code;
        _mathFns = mathFns;
        _exFns = exFns;
        _rootReg = rootReg;
        _root = root;
    }

    /// <summary>
    /// Compiles <paramref name="ast"/> against a fixed shape: <paramref name="portCount"/> port
    /// voltages ("_v1".."_v{portCount}"), control currents named by <paramref name="controlNs"/> (the
    /// declared 1-based index of each — <c>SddModel.ControlRefs[i].N</c>, in the SAME order
    /// <c>BuildControlSeeds</c> produces its seed array, so slot i here lines up with seed i there),
    /// and <paramref name="parameters"/>'s names. Any RefExpr outside this set compiles to a node that
    /// throws <see cref="UnresolvedNameException"/> when (and only when) actually reached.
    /// </summary>
    public static CompiledSddExpr Compile(
        Expr ast, IReadOnlyDictionary<string, double> parameters, int portCount,
        IReadOnlyList<int> controlNs, string modelName = "<sdd>")
    {
        var slotOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < portCount; i++) slotOf[$"_v{i + 1}"] = i;
        for (int i = 0; i < controlNs.Count; i++) slotOf[$"_c{controlNs[i]}"] = portCount + i;

        var paramValues = new double[parameters.Count];
        int slot = portCount + controlNs.Count;
        int pi = 0;
        foreach (var kv in parameters)
        {
            // A parameter sharing a name with a port/control slot is a pre-existing ambiguity in the
            // reference dictionary too (last write wins there); preserve the same resolution here by
            // simply not overwriting an already-assigned port/control slot.
            if (!slotOf.ContainsKey(kv.Key)) slotOf[kv.Key] = slot + pi;
            paramValues[pi] = kv.Value;
            pi++;
        }

        if (!SddRegisterCompiler.ContainsConditional(ast))
        {
            var (code, mathFns, exFns, rootReg) = SddRegisterCompiler.Compile(ast, slotOf, portCount + controlNs.Count + paramValues.Length, modelName);
            return new CompiledSddExpr(portCount, controlNs.Count, paramValues, code, mathFns, exFns, rootReg, root: null);
        }

        var root = SddCompiler.CompileNode(ast, slotOf, modelName);
        return new CompiledSddExpr(portCount, controlNs.Count, paramValues, code: null, [], [], 0, root);
    }

    /// <summary>
    /// Evaluates in Dual (forward-mode AD) arithmetic. <paramref name="controlCurrents"/> must be
    /// empty when this was compiled with no control refs — pass <c>[]</c> in that case.
    /// </summary>
    public (double Value, double[] Grad) EvalDual(
        double[] portVoltages, (int N, double Value)[] controlCurrents, string modelName)
    {
        int total = _code is null ? _totalSlots : _totalSlots + _code.Length;
        Span<Dual> slots = total <= 96 ? stackalloc Dual[total] : new Dual[total];
        for (int i = 0; i < _nV; i++) slots[i] = Dual.Seed(portVoltages[i], _n, i);
        for (int i = 0; i < _nC; i++) slots[_nV + i] = Dual.Seed(controlCurrents[i].Value, _n, _nV + i);
        for (int i = 0; i < _paramDuals.Length; i++) slots[_n + i] = _paramDuals[i];

        AdWarnings.CurrentModel = modelName;
        Dual result = _code is null
            ? SddCompiler.Eval<Dual>(_root!, slots)
            : SddRegisterCompiler.Run(_code, _mathFns, _exFns, _totalSlots, _rootReg, slots);

        var grad = new double[_n];
        for (int i = 0; i < _n; i++) grad[i] = result.GetGrad(i);
        return (result.Value, grad);
    }
}
