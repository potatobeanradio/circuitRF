using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CircuitRF.Core.Expressions;

/// <summary>
/// HB-P4 M1 — the structure-of-arrays twin of <see cref="SddRegisterCompiler.Run"/>.
///
/// <para>The compiled register program is the SAME instruction sequence for every sample of an HB
/// time grid: nothing in it depends on <c>t</c>. The scalar runner nevertheless walks it once per
/// sample with 136-byte <see cref="Dual"/> operands, moving the whole inline gradient array to use
/// two or three of its lanes. This runner walks the program ONCE for the whole grid, with each
/// register laid out as <c>value[S]</c> followed by <c>grad[k][S]</c> — contiguous doubles the JIT
/// vectorises, and no allocation at all once the scratch exists.</para>
///
/// <para><b>The gate is bit identity, not a tolerance</b> (brief §7): every value and every gradient
/// lane must equal the scalar path's exactly. Three things are what make that true rather than
/// merely likely:</para>
/// <list type="number">
/// <item><description>Every kernel below performs the SAME operations in the SAME order as the
/// corresponding <c>Dual</c> operator — including which quantity is formed first (e.g. <c>Cos</c>
/// negates the sine once and multiplies, as the scalar does) and the clamp constants.</description></item>
/// <item><description><b>No FMA.</b> The vector helpers use ordinary <c>*</c> and <c>+</c> on
/// <see cref="Vector{T}"/>. RyuJIT does not contract those into a fused multiply-add, so each
/// intermediate rounds exactly where the scalar path rounds. Nothing here may call
/// <c>FusedMultiplyAdd</c> or <c>Vector.MultiplyAddEstimate</c>.</description></item>
/// <item><description><b>The gradient WIDTH is tracked structurally.</b> <c>Dual</c> carries an
/// <c>N</c> that a binary operator resolves as <c>max(a.N, b.N)</c>, and lanes at or above it are
/// never written — they stay +0.0. N is a property of the program, not of the values (a constant is
/// N=0, a slot is N=n, everything else is the max of its operands), so it is precomputed once by
/// <see cref="BuildHasGrad"/>: a register with no gradient has its lanes neither written nor read
/// (reads come from a shared zero buffer), which is both faster and exactly what the scalar path
/// leaves behind.</description></item>
/// </list>
///
/// <para><b>Known structural equivalence caveat.</b> <c>min</c>/<c>max</c> copy the CHOSEN operand's
/// <c>N</c>, which is per-sample, so this runner uses <c>max(a,b)</c> for the result width instead.
/// The two agree on every lane whose inputs are finite; they can only differ if a gradient lane that
/// the scalar path would have left unwritten is computed here from a non-finite value (0·∞ → NaN),
/// which needs a <c>min</c>/<c>max</c> over operands of DIFFERENT structural width feeding a
/// non-finite intermediate. No SDD in this repository has that shape.</para>
/// </summary>
internal static class SddGridEvaluator
{
    // Mirrors Dual's own clamps exactly — see Dual.ExpCap / Dual.LogFloor.
    private const double ExpCap = 700.0;
    private const double LogFloor = 1e-300;

    /// <summary>
    /// Structural gradient width per register, reduced to "has any gradient lane at all"
    /// (<c>N != 0</c>). Slots — port seeds, control seeds and parameters alike — are all
    /// <c>Dual.Param/Seed(_, n)</c>, so they carry N=n; a literal is <c>Dual.Constant</c>, N=0.
    /// Every other register follows its operands, exactly as <c>Dual.NMax</c> does.
    /// </summary>
    public static bool[] BuildHasGrad(RInstr[] code, int totalSlots, int n)
    {
        var h = new bool[totalSlots + code.Length];
        for (int i = 0; i < totalSlots; i++) h[i] = n > 0;
        for (int i = 0; i < code.Length; i++)
        {
            ref readonly var ins = ref code[i];
            h[totalSlots + i] = ins.Op switch
            {
                ROp.Const or ROp.ConstOfValue or ROp.Sign or ROp.Atan2 or ROp.Throw => false,
                ROp.Neg or ROp.Exp or ROp.Log or ROp.Sqrt or ROp.Tanh
                    or ROp.Sin or ROp.Cos or ROp.Abs => h[ins.A],
                _ => h[ins.A] || h[ins.B],   // Add/Sub/Mul/Div/Pow/SelectMin/SelectMax
            };
        }
        return h;
    }

    /// <summary>
    /// Which input slots the program actually touches (plus the root, which may BE a slot when the
    /// whole equation is a bare name). Seeding a slot costs <c>(1+n)·S</c> writes, so a model with
    /// twenty parameters and an equation using three should not pay for seventeen.
    /// </summary>
    public static int[] BuildUsedSlots(RInstr[] code, int totalSlots, int rootReg)
    {
        var used = new bool[totalSlots];
        if (rootReg < totalSlots) used[rootReg] = true;
        foreach (ref readonly var ins in code.AsSpan())
        {
            switch (ins.Op)
            {
                case ROp.Const:
                case ROp.Throw:
                    break;
                case ROp.Neg or ROp.Exp or ROp.Log or ROp.Sqrt or ROp.Tanh or ROp.Sin
                    or ROp.Cos or ROp.Abs or ROp.ConstOfValue or ROp.Sign:
                    if (ins.A < totalSlots) used[ins.A] = true;
                    break;
                default:
                    if (ins.A < totalSlots) used[ins.A] = true;
                    if (ins.B < totalSlots) used[ins.B] = true;
                    break;
            }
        }
        var list = new List<int>();
        for (int i = 0; i < totalSlots; i++) if (used[i]) list.Add(i);
        return [.. list];
    }

    /// <summary>
    /// Runs the whole program over <paramref name="count"/> samples of a register file already
    /// seeded by <see cref="CompiledSddExpr"/>. Every register <c>r</c> occupies
    /// <c>buf[(r·(n+1) + lane)·count .. +count)</c>, lane 0 being the value and lane <c>1+k</c> the
    /// k-th gradient. <paramref name="zeros"/> is a shared, never-written run of <paramref name="count"/>
    /// zeros standing in for the lanes of a gradient-free register.
    /// </summary>
    public static void Run(
        RInstr[] code, Func<double, double>[] mathFns, Func<Exception>[] exFns,
        bool[] hasGrad, int totalSlots, int n, int count,
        double[] buf, double[] zeros, ref GridDomainWarnings warn)
    {
        int lanes = n + 1;
        var b = buf.AsSpan();
        // One spare register's value lane, used by the transcendental kernels below to hold the
        // per-sample derivative FACTOR so the gradient lanes can be written by the same vectorised
        // helpers the arithmetic opcodes use. CompiledSddExpr sizes the scratch for it.
        var d = b.Slice((totalSlots + code.Length) * lanes * count, count);

        int VOff(int r) => r * lanes * count;
        int GOff(int r, int k) => (r * lanes + 1 + k) * count;


        for (int pc = 0; pc < code.Length; pc++)
        {
            ref readonly var ins = ref code[pc];
            int o = totalSlots + pc;
            bool og = hasGrad[o];
            Span<double> ov = b.Slice(VOff(o), count);

            switch (ins.Op)
            {
                // ── vectorised arithmetic (lane-outer, sample-inner) ──────────────────────
                case ROp.Const:
                    ov.Fill(ins.Lit);
                    break;

                case ROp.Neg:
                {
                    VNeg(b.Slice(VOff(ins.A), count), ov);
                    if (og)
                        for (int k = 0; k < n; k++)
                            VNeg(GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Add:
                {
                    VAdd(b.Slice(VOff(ins.A), count), b.Slice(VOff(ins.B), count), ov);
                    if (og)
                        for (int k = 0; k < n; k++)
                            VAdd(GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), GradIn(buf, zeros, hasGrad, ins.B, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Sub:
                {
                    VSub(b.Slice(VOff(ins.A), count), b.Slice(VOff(ins.B), count), ov);
                    if (og)
                        for (int k = 0; k < n; k++)
                            VSub(GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), GradIn(buf, zeros, hasGrad, ins.B, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Mul:
                {
                    // Read the operand VALUES before writing the output: the output register can be
                    // neither operand (registers are written once, in program order), but slicing
                    // first keeps that explicit.
                    var av = b.Slice(VOff(ins.A), count);
                    var bv = b.Slice(VOff(ins.B), count);
                    if (og)
                        for (int k = 0; k < n; k++)
                            VMulAddMul(GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), bv, av, GradIn(buf, zeros, hasGrad, ins.B, k, lanes, count), b.Slice(GOff(o, k), count));
                    VMul(av, bv, ov);
                    break;
                }

                case ROp.Div:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    var bv = b.Slice(VOff(ins.B), count);
                    if (og)
                        for (int k = 0; k < n; k++)
                            VDivGrad(GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), bv, av, GradIn(buf, zeros, hasGrad, ins.B, k, lanes, count), b.Slice(GOff(o, k), count));
                    VDiv(av, bv, ov);
                    break;
                }

                // ── scalar-per-sample kernels (transcendentals and the value-only ops) ────
                case ROp.Pow:
                {
                    int ao = VOff(ins.A), bo = VOff(ins.B), oo = VOff(o), ogo = GOff(o, 0);
                    bool ag = hasGrad[ins.A], bg = hasGrad[ins.B];
                    int ago = GOff(ins.A, 0), bgo = GOff(ins.B, 0);
                    for (int t = 0; t < count; t++)
                    {
                        double av = buf[ao + t], bv = buf[bo + t];
                        double pval = Math.Pow(av, bv);
                        buf[oo + t] = pval;
                        if (!og) continue;
                        double bOverA = av != 0.0 ? bv / av : 0.0;
                        double lnA = av > 0.0 ? Math.Log(av) : 0.0;
                        for (int k = 0; k < n; k++)
                            buf[ogo + k * count + t] = pval *
                                (bOverA * (ag ? buf[ago + k * count + t] : 0.0)
                               + lnA    * (bg ? buf[bgo + k * count + t] : 0.0));
                    }
                    break;
                }

                case ROp.Exp:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    for (int t = 0; t < count; t++)
                    {
                        double xv = av[t];
                        if (xv > ExpCap) xv = ExpCap;
                        ov[t] = d[t] = Math.Exp(xv);
                    }
                    if (og)
                        for (int k = 0; k < n; k++)
                            VMul(d, GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Log:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    for (int t = 0; t < count; t++)
                    {
                        double x = av[t];
                        if (x <= 0.0) { warn.NoteLog(x); x = LogFloor; }
                        ov[t] = Math.Log(x);
                        d[t] = x;                  // the CLAMPED argument is the derivative's divisor
                    }
                    if (og)
                        for (int k = 0; k < n; k++)
                            VDiv(GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), d, b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Sqrt:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    for (int t = 0; t < count; t++)
                    {
                        double x = av[t];
                        if (x < 0.0) { warn.NoteSqrt(x); x = 0.0; }
                        double sv = Math.Sqrt(x);
                        ov[t] = sv;
                        d[t] = sv > 0.0 ? 2.0 * sv : 1e-150;   // avoid /0 at exactly 0, as Dual.Sqrt does
                    }
                    if (og)
                        for (int k = 0; k < n; k++)
                            VDiv(GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), d, b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Tanh:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    for (int t = 0; t < count; t++)
                    {
                        double tv = Math.Tanh(av[t]);
                        ov[t] = tv;
                        d[t] = 1.0 - tv * tv;      // sech^2
                    }
                    if (og)
                        for (int k = 0; k < n; k++)
                            VMul(d, GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Sin:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    for (int t = 0; t < count; t++)
                    {
                        double x = av[t];
                        ov[t] = Math.Sin(x);
                        d[t] = Math.Cos(x);
                    }
                    if (og)
                        for (int k = 0; k < n; k++)
                            VMul(d, GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Cos:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    for (int t = 0; t < count; t++)
                    {
                        double x = av[t];
                        ov[t] = Math.Cos(x);
                        d[t] = -Math.Sin(x);       // negated once, as Dual.Cos does, not per lane
                    }
                    if (og)
                        for (int k = 0; k < n; k++)
                            VMul(d, GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.Abs:
                {
                    var av = b.Slice(VOff(ins.A), count);
                    for (int t = 0; t < count; t++)
                    {
                        double x = av[t];
                        ov[t] = Math.Abs(x);
                        d[t] = x >= 0.0 ? 1.0 : -1.0;
                    }
                    if (og)
                        for (int k = 0; k < n; k++)
                            VMul(d, GradIn(buf, zeros, hasGrad, ins.A, k, lanes, count), b.Slice(GOff(o, k), count));
                    break;
                }

                case ROp.ConstOfValue:
                {
                    var fn = mathFns[ins.Extra];
                    int ao = VOff(ins.A), oo = VOff(o);
                    for (int t = 0; t < count; t++) buf[oo + t] = fn(buf[ao + t]);
                    break;
                }

                case ROp.Sign:
                {
                    int ao = VOff(ins.A), oo = VOff(o);
                    for (int t = 0; t < count; t++) buf[oo + t] = Math.Sign(buf[ao + t]);
                    break;
                }

                case ROp.Atan2:
                {
                    int ao = VOff(ins.A), bo = VOff(ins.B), oo = VOff(o);
                    for (int t = 0; t < count; t++) buf[oo + t] = Math.Atan2(buf[ao + t], buf[bo + t]);
                    break;
                }

                case ROp.SelectMin:
                case ROp.SelectMax:
                {
                    bool min = ins.Op == ROp.SelectMin;
                    int ao = VOff(ins.A), bo = VOff(ins.B), oo = VOff(o), ogo = GOff(o, 0);
                    bool ag = hasGrad[ins.A], bg = hasGrad[ins.B];
                    int ago = GOff(ins.A, 0), bgo = GOff(ins.B, 0);
                    for (int t = 0; t < count; t++)
                    {
                        double av = buf[ao + t], bv = buf[bo + t];
                        bool takeA = min ? av <= bv : av >= bv;
                        buf[oo + t] = takeA ? av : bv;
                        if (!og) continue;
                        // min/max copy the chosen operand's Dual verbatim — value AND every lane.
                        if (takeA)
                            for (int k = 0; k < n; k++)
                                buf[ogo + k * count + t] = ag ? buf[ago + k * count + t] : 0.0;
                        else
                            for (int k = 0; k < n; k++)
                                buf[ogo + k * count + t] = bg ? buf[bgo + k * count + t] : 0.0;
                    }
                    break;
                }

                case ROp.Throw:
                    throw exFns[ins.Extra]();

                default:
                    throw new ExpressionException($"Unknown register op: {ins.Op}");
            }
        }
    }

    /// <summary>
    /// Gradient lane <paramref name="k"/> of register <paramref name="r"/> — the shared zero run when
    /// that register structurally has none, never whatever a previous call left in the scratch.
    /// Static (and therefore verbose at the call site) because a local function may not capture a
    /// span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<double> GradIn(
        double[] buf, double[] zeros, bool[] hasGrad, int r, int k, int lanes, int count)
        => hasGrad[r] ? buf.AsSpan((r * lanes + 1 + k) * count, count) : zeros.AsSpan(0, count);

    // ── vector helpers ───────────────────────────────────────────────────────────────────
    //
    // Plain * and + on Vector<double>: RyuJIT performs no floating-point contraction, so each
    // intermediate rounds exactly where the scalar Dual operator rounds it. Do NOT reach for
    // FusedMultiplyAdd here — it would be faster and wrong.
    //
    // Written against `ref double` + Vector.LoadUnsafe/StoreUnsafe rather than span slicing. The
    // slicing form is the obvious one and it MEASURED four times slower: `new Vector<double>(span)`
    // carries a length check the JIT did not hoist out of the loop, so every element paid for a
    // branch it could not need. The ref form is what turns this into the load-op-store the kernel is
    // supposed to be.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VAdd(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> o)
    {
        ref double ra = ref MemoryMarshal.GetReference(a);
        ref double rb = ref MemoryMarshal.GetReference(b);
        ref double ro = ref MemoryMarshal.GetReference(o);
        nuint n = (nuint)o.Length, w = (nuint)Vector<double>.Count, i = 0;
        if (Vector.IsHardwareAccelerated && n >= w)
            for (; i <= n - w; i += w)
                Vector.StoreUnsafe(Vector.LoadUnsafe(ref ra, i) + Vector.LoadUnsafe(ref rb, i), ref ro, i);
        for (; i < n; i++)
            Unsafe.Add(ref ro, i) = Unsafe.Add(ref ra, i) + Unsafe.Add(ref rb, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VSub(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> o)
    {
        ref double ra = ref MemoryMarshal.GetReference(a);
        ref double rb = ref MemoryMarshal.GetReference(b);
        ref double ro = ref MemoryMarshal.GetReference(o);
        nuint n = (nuint)o.Length, w = (nuint)Vector<double>.Count, i = 0;
        if (Vector.IsHardwareAccelerated && n >= w)
            for (; i <= n - w; i += w)
                Vector.StoreUnsafe(Vector.LoadUnsafe(ref ra, i) - Vector.LoadUnsafe(ref rb, i), ref ro, i);
        for (; i < n; i++)
            Unsafe.Add(ref ro, i) = Unsafe.Add(ref ra, i) - Unsafe.Add(ref rb, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VMul(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> o)
    {
        ref double ra = ref MemoryMarshal.GetReference(a);
        ref double rb = ref MemoryMarshal.GetReference(b);
        ref double ro = ref MemoryMarshal.GetReference(o);
        nuint n = (nuint)o.Length, w = (nuint)Vector<double>.Count, i = 0;
        if (Vector.IsHardwareAccelerated && n >= w)
            for (; i <= n - w; i += w)
                Vector.StoreUnsafe(Vector.LoadUnsafe(ref ra, i) * Vector.LoadUnsafe(ref rb, i), ref ro, i);
        for (; i < n; i++)
            Unsafe.Add(ref ro, i) = Unsafe.Add(ref ra, i) * Unsafe.Add(ref rb, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VDiv(ReadOnlySpan<double> a, ReadOnlySpan<double> b, Span<double> o)
    {
        ref double ra = ref MemoryMarshal.GetReference(a);
        ref double rb = ref MemoryMarshal.GetReference(b);
        ref double ro = ref MemoryMarshal.GetReference(o);
        nuint n = (nuint)o.Length, w = (nuint)Vector<double>.Count, i = 0;
        if (Vector.IsHardwareAccelerated && n >= w)
            for (; i <= n - w; i += w)
                Vector.StoreUnsafe(Vector.LoadUnsafe(ref ra, i) / Vector.LoadUnsafe(ref rb, i), ref ro, i);
        for (; i < n; i++)
            Unsafe.Add(ref ro, i) = Unsafe.Add(ref ra, i) / Unsafe.Add(ref rb, i);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VNeg(ReadOnlySpan<double> a, Span<double> o)
    {
        ref double ra = ref MemoryMarshal.GetReference(a);
        ref double ro = ref MemoryMarshal.GetReference(o);
        nuint n = (nuint)o.Length, w = (nuint)Vector<double>.Count, i = 0;
        if (Vector.IsHardwareAccelerated && n >= w)
            for (; i <= n - w; i += w)
                Vector.StoreUnsafe(-Vector.LoadUnsafe(ref ra, i), ref ro, i);
        for (; i < n; i++)
            Unsafe.Add(ref ro, i) = -Unsafe.Add(ref ra, i);
    }

    /// <summary>d(ab) = a'·b + a·b' — the exact expression <c>Dual.Mul</c> evaluates, in its order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VMulAddMul(ReadOnlySpan<double> ag, ReadOnlySpan<double> bv,
                                   ReadOnlySpan<double> av, ReadOnlySpan<double> bg, Span<double> o)
    {
        ref double rag = ref MemoryMarshal.GetReference(ag);
        ref double rbv = ref MemoryMarshal.GetReference(bv);
        ref double rav = ref MemoryMarshal.GetReference(av);
        ref double rbg = ref MemoryMarshal.GetReference(bg);
        ref double ro = ref MemoryMarshal.GetReference(o);
        nuint n = (nuint)o.Length, w = (nuint)Vector<double>.Count, i = 0;
        if (Vector.IsHardwareAccelerated && n >= w)
            for (; i <= n - w; i += w)
                Vector.StoreUnsafe(
                    Vector.LoadUnsafe(ref rag, i) * Vector.LoadUnsafe(ref rbv, i)
                  + Vector.LoadUnsafe(ref rav, i) * Vector.LoadUnsafe(ref rbg, i), ref ro, i);
        for (; i < n; i++)
            Unsafe.Add(ref ro, i) = Unsafe.Add(ref rag, i) * Unsafe.Add(ref rbv, i)
                                  + Unsafe.Add(ref rav, i) * Unsafe.Add(ref rbg, i);
    }

    /// <summary>d(a/b) = (a'b − ab') / b² — <c>Dual.Div</c>'s expression, with b² formed the same way.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void VDivGrad(ReadOnlySpan<double> ag, ReadOnlySpan<double> bv,
                                 ReadOnlySpan<double> av, ReadOnlySpan<double> bg, Span<double> o)
    {
        ref double rag = ref MemoryMarshal.GetReference(ag);
        ref double rbv = ref MemoryMarshal.GetReference(bv);
        ref double rav = ref MemoryMarshal.GetReference(av);
        ref double rbg = ref MemoryMarshal.GetReference(bg);
        ref double ro = ref MemoryMarshal.GetReference(o);
        nuint n = (nuint)o.Length, w = (nuint)Vector<double>.Count, i = 0;
        if (Vector.IsHardwareAccelerated && n >= w)
            for (; i <= n - w; i += w)
            {
                var vb = Vector.LoadUnsafe(ref rbv, i);
                Vector.StoreUnsafe(
                    (Vector.LoadUnsafe(ref rag, i) * vb
                   - Vector.LoadUnsafe(ref rav, i) * Vector.LoadUnsafe(ref rbg, i)) / (vb * vb), ref ro, i);
            }
        for (; i < n; i++)
        {
            double b = Unsafe.Add(ref rbv, i);
            double b2 = b * b;
            Unsafe.Add(ref ro, i) = (Unsafe.Add(ref rag, i) * b - Unsafe.Add(ref rav, i) * Unsafe.Add(ref rbg, i)) / b2;
        }
    }
}
