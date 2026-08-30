namespace CircuitRF.Core.Expressions;

/// <summary>
/// HB-P4 M1 — the caller-owned register file for <see cref="CompiledSddExpr.EvalDualGrid"/>.
///
/// <para>One flat <c>double[]</c> holding, for every register of the compiled program, a run of
/// <c>count</c> values followed by <c>n</c> runs of <c>count</c> gradient lanes. It is grown on
/// demand and never shrunk, so the second and every later call on the same (expression, sample
/// count) allocates nothing at all — which is the point: the scalar path allocated six arrays PER
/// SAMPLE, and a two-tone Newton iteration is 1,024 samples per device.</para>
///
/// <para>Owned by the model (or, in the parallel case, one per worker) rather than by the compiled
/// expression, because a scratch is what makes an evaluation thread-affine: two threads sharing one
/// would interleave their register writes. Sizing is by the LARGEST shape asked of it, so a single
/// scratch serves every equation of a device.</para>
/// </summary>
public sealed class GridScratch
{
    internal double[] Buf = [];

    /// <summary>A run of <c>count</c> zeros, never written, standing in for the gradient lanes of a
    /// register that structurally has none (see <c>SddGridEvaluator</c>).</summary>
    internal double[] Zeros = [];

    /// <summary>Grows the register file to hold <paramref name="registers"/> registers of
    /// <paramref name="lanes"/> lanes over <paramref name="count"/> samples. Growth only.</summary>
    internal void Ensure(int registers, int lanes, int count)
    {
        long need = (long)registers * lanes * count;
        if (need > Buf.Length) Buf = new double[need];
        if (count > Zeros.Length) Zeros = new double[count];
    }

    /// <summary>Bytes currently held — for diagnostics and the allocation tests.</summary>
    public long Bytes => ((long)Buf.Length + Zeros.Length) * sizeof(double);
}
