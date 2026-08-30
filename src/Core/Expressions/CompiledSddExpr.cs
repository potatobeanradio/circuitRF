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

    // HB-P4 M1 — the grid runner's two compile-time tables: the structural gradient width of every
    // register (reduced to "has any lane", which is all the SoA layout needs) and the input slots the
    // program actually reads. Both are null when there is no register program to run.
    private readonly bool[]? _gridHasGrad;
    private readonly int[]? _gridUsedSlots;

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

        if (code is not null)
        {
            _gridHasGrad = SddGridEvaluator.BuildHasGrad(code, _totalSlots, _n);
            _gridUsedSlots = SddGridEvaluator.BuildUsedSlots(code, _totalSlots, rootReg);
        }
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

    // ── HB-P4 M1: the whole time grid in one walk of the program ─────────────────────────────

    /// <summary>
    /// Whether this equation can be evaluated by <see cref="EvalDualGrid"/>. False exactly when the
    /// equation contains a conditional and therefore compiled to the CNode tree-walk instead of the
    /// register program — the active branch is per-sample there, so there is no one instruction
    /// sequence to run across the grid. Lifting conditionals into masked selects is a later brief;
    /// until then such an equation stays on the scalar path, which is unchanged.
    /// </summary>
    public bool SupportsGrid => _code is not null;

    /// <summary>Gradient width — port count plus control-reference count. Every <c>grad</c> buffer
    /// handed to <see cref="EvalDualGrid"/> carries this many lanes.</summary>
    public int GradWidth => _n;

    /// <summary>A register file for this equation at <paramref name="sampleCount"/> samples. Reuse it
    /// across calls (and across the device's other equations); it grows, never shrinks.</summary>
    public GridScratch CreateScratch(int sampleCount)
    {
        var s = new GridScratch();
        EnsureScratch(s, sampleCount);
        return s;
    }

    // One register beyond the program's own: SddGridEvaluator's transcendental kernels park the
    // per-sample derivative factor there so the gradient lanes go through the same vectorised
    // helpers as the arithmetic opcodes.
    private void EnsureScratch(GridScratch scratch, int sampleCount)
        => scratch.Ensure(_totalSlots + (_code?.Length ?? 0) + 1, _n + 1, sampleCount);

    /// <summary>
    /// Evaluates the equation, in Dual arithmetic, at every sample of a time grid in ONE walk of the
    /// compiled program — bit-for-bit what <see cref="EvalDual"/> returns sample by sample, at a
    /// fraction of the cost and with no allocation once <paramref name="scratch"/> exists.
    ///
    /// <para><paramref name="portVoltages"/> is <c>[port][t]</c> and <paramref name="controlCurrents"/>
    /// is <c>[control][t]</c>, both row-major with the same <paramref name="stride"/> as the outputs;
    /// pass an empty span for controls when the equation was compiled with none. Results are written
    /// to <paramref name="value"/><c>[t]</c> and <paramref name="grad"/><c>[k·stride + t]</c> for the
    /// samples <c>[t0, t0+count)</c> only, so a caller may split a grid into chunks and give each
    /// worker its own scratch.</para>
    ///
    /// <para>Domain clamps are recorded into <paramref name="warn"/> rather than printed; the caller
    /// emits them once for the whole grid (see <see cref="GridDomainWarnings"/>).</para>
    /// </summary>
    /// <exception cref="NotSupportedException">The equation contains a conditional
    /// (<see cref="SupportsGrid"/> is false).</exception>
    public void EvalDualGrid(
        ReadOnlySpan<double> portVoltages, ReadOnlySpan<double> controlCurrents,
        int stride, int t0, int count,
        Span<double> value, Span<double> grad,
        GridScratch scratch, string modelName, ref GridDomainWarnings warn)
    {
        if (_code is null)
            throw new NotSupportedException(
                $"SDD '{modelName}': this equation contains a conditional and has no grid form — " +
                "use EvalDual per sample (CompiledSddExpr.SupportsGrid says which).");
        if (count <= 0) return;

        EnsureScratch(scratch, count);
        var buf = scratch.Buf;
        int lanes = _n + 1;

        // Seed the input slots the program actually reads. A port slot's gradient is the unit vector
        // at its own index and a parameter's is zero — constant across samples, but they are ordinary
        // registers to everything downstream, so they are materialised like any other.
        foreach (int slot in _gridUsedSlots!)
        {
            var slotValue = buf.AsSpan(slot * lanes * count, count);
            if (slot < _nV)
                portVoltages.Slice(slot * stride + t0, count).CopyTo(slotValue);
            else if (slot < _n)
                controlCurrents.Slice((slot - _nV) * stride + t0, count).CopyTo(slotValue);
            else
                slotValue.Fill(_paramDuals[slot - _n].Value);

            for (int k = 0; k < _n; k++)
            {
                var lane = buf.AsSpan((slot * lanes + 1 + k) * count, count);
                // A slot's own dual: Seed(v, n, slot) for ports/controls, Param(v, n) for parameters.
                lane.Fill(slot < _n ? (slot == k ? 1.0 : 0.0) : 0.0);
            }
        }

        AdWarnings.CurrentModel = modelName;
        SddGridEvaluator.Run(_code, _mathFns, _exFns, _gridHasGrad!, _totalSlots, _n, count,
                             buf, scratch.Zeros, ref warn);

        // Read the root register out. It may BE a slot (a bare-name equation compiles to no
        // instruction at all), which is why slots are materialised rather than special-cased.
        buf.AsSpan(_rootReg * lanes * count, count).CopyTo(value.Slice(t0, count));
        bool rootHasGrad = _gridHasGrad![_rootReg];
        for (int k = 0; k < _n; k++)
        {
            var dst = grad.Slice(k * stride + t0, count);
            if (rootHasGrad) buf.AsSpan((_rootReg * lanes + 1 + k) * count, count).CopyTo(dst);
            else dst.Clear();
        }
    }
}
