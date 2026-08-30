namespace CircuitRF.Core;

/// <summary>
/// HB-P4 M2 — one nonlinear device's contribution over a WHOLE time grid, structure-of-arrays and
/// caller-owned.
///
/// <para>This is the allocation-free counterpart of <see cref="NonlinearResult"/>, which is defined
/// per sample and so allocates six arrays for each of the grid's S points — 33 KB per single-tone
/// Newton iteration, 723 KB per two-tone one, all of it garbage. Here the engine allocates one of
/// these per device, sized once, and reuses it for every iteration of the solve.</para>
///
/// <para>Every buffer is flat, indexed <c>[…][t]</c> with the sample as the fastest axis: that is the
/// layout the grid evaluator produces and the layout a vectorised loop wants. The arrays may be
/// LONGER than the current shape needs (they grow and never shrink), so always index through the
/// helpers rather than off <c>Length</c>.</para>
/// </summary>
public sealed class GridResult
{
    /// <summary>Ports of the device this was last shaped for.</summary>
    public int PortCount { get; private set; }
    /// <summary>Control references (<c>_cn</c>) of the device; 0 for every device but a control SDD.</summary>
    public int ControlCount { get; private set; }
    /// <summary>Samples in the grid.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Port currents I[p,0], indexed <c>p·S + t</c>.</summary>
    public double[] I { get; private set; } = [];
    /// <summary>Port charges I[p,1], indexed <c>p·S + t</c>.</summary>
    public double[] Q { get; private set; } = [];
    /// <summary>∂I/∂V, indexed <c>(p·P + q)·S + t</c>.</summary>
    public double[] Dg { get; private set; } = [];
    /// <summary>∂Q/∂V, indexed <c>(p·P + q)·S + t</c>.</summary>
    public double[] Dc { get; private set; } = [];
    /// <summary>∂I[p,0]/∂_cn, indexed <c>(p·C + c)·S + t</c>; empty when there are no controls.</summary>
    public double[] DControl { get; private set; } = [];
    /// <summary>∂Q[p]/∂_cn, indexed <c>(p·C + c)·S + t</c>; empty when there are no controls.</summary>
    public double[] DControlCharge { get; private set; } = [];

    /// <summary>The w≥2 buckets, in ascending w. Reused across calls — <see cref="ResetTerms"/>
    /// keeps the buffers and drops only the count.</summary>
    public List<GridWeightedTerm> Terms { get; } = [];

    private int _termCount;

    /// <summary>The live buckets for this call — <see cref="Terms"/> may hold spare ones behind them.</summary>
    public ReadOnlySpan<GridWeightedTerm> LiveTerms => System.Runtime.InteropServices.CollectionsMarshal
        .AsSpan(Terms)[.._termCount];

    /// <summary>Grows every buffer to the given shape. Growth only, so the second and later calls at
    /// the same (or a smaller) shape allocate nothing.</summary>
    public void EnsureShape(int portCount, int controlCount, int sampleCount)
    {
        PortCount = portCount;
        ControlCount = controlCount;
        SampleCount = sampleCount;

        int nPS = portCount * sampleCount;
        int nPPS = portCount * portCount * sampleCount;
        int nPCS = portCount * controlCount * sampleCount;

        if (I.Length < nPS) I = new double[nPS];
        if (Q.Length < nPS) Q = new double[nPS];
        if (Dg.Length < nPPS) Dg = new double[nPPS];
        if (Dc.Length < nPPS) Dc = new double[nPPS];
        if (DControl.Length < nPCS) DControl = new double[nPCS];
        if (DControlCharge.Length < nPCS) DControlCharge = new double[nPCS];
    }

    /// <summary>Zeroes the live region of the four main blocks. A device whose port has no current
    /// equation contributes zero there, and the buffers are reused, so silence must be written.</summary>
    public void ClearBlocks()
    {
        int nPS = PortCount * SampleCount;
        int nPPS = PortCount * PortCount * SampleCount;
        int nPCS = PortCount * ControlCount * SampleCount;
        Array.Clear(I, 0, nPS);
        Array.Clear(Q, 0, nPS);
        Array.Clear(Dg, 0, nPPS);
        Array.Clear(Dc, 0, nPPS);
        if (nPCS > 0) { Array.Clear(DControl, 0, nPCS); Array.Clear(DControlCharge, 0, nPCS); }
    }

    /// <summary>Drops the live bucket count without releasing the buckets' buffers.</summary>
    public void ResetTerms() => _termCount = 0;

    /// <summary>
    /// The bucket for weighting index <paramref name="w"/>, appended (or revived from the spares) and
    /// zeroed. Callers add buckets in ascending w, which is the order <c>SddModel</c> discovers them
    /// and the order the engine's own bucket table expects.
    /// </summary>
    public GridWeightedTerm AddTerm(int w)
    {
        GridWeightedTerm term;
        if (_termCount < Terms.Count) term = Terms[_termCount];
        else { term = new GridWeightedTerm(); Terms.Add(term); }
        _termCount++;
        term.Reshape(w, PortCount, ControlCount, SampleCount);
        return term;
    }

    /// <summary>Index of I/Q at port <paramref name="p"/>.</summary>
    public int PortBase(int p) => p * SampleCount;
    /// <summary>Index of Dg/Dc at (<paramref name="p"/>, <paramref name="q"/>).</summary>
    public int JacBase(int p, int q) => (p * PortCount + q) * SampleCount;
    /// <summary>Index of DControl at (<paramref name="p"/>, control <paramref name="c"/>).</summary>
    public int CtrlBase(int p, int c) => (p * ControlCount + c) * SampleCount;
}

/// <summary>
/// HB-P4 M2 — one higher-weighting (w≥2) bucket of a <see cref="GridResult"/>, laid out the same way
/// and reused the same way. The scalar twin is <see cref="WeightedTerm"/>.
/// </summary>
public sealed class GridWeightedTerm
{
    /// <summary>Weighting index (≥2).</summary>
    public int W { get; private set; }
    /// <summary>Time-domain I[p,w], indexed <c>p·S + t</c>.</summary>
    public double[] Value { get; private set; } = [];
    /// <summary>∂I[p,w]/∂V_q, indexed <c>(p·P + q)·S + t</c>.</summary>
    public double[] Jac { get; private set; } = [];
    /// <summary>∂I[p,w]/∂_cn, indexed <c>(p·C + c)·S + t</c>; empty when there are no controls.</summary>
    public double[] JacCtrl { get; private set; } = [];

    internal void Reshape(int w, int portCount, int controlCount, int sampleCount)
    {
        W = w;
        int nPS = portCount * sampleCount;
        int nPPS = portCount * portCount * sampleCount;
        int nPCS = portCount * controlCount * sampleCount;
        if (Value.Length < nPS) Value = new double[nPS];
        if (Jac.Length < nPPS) Jac = new double[nPPS];
        if (JacCtrl.Length < nPCS) JacCtrl = new double[nPCS];
        Array.Clear(Value, 0, nPS);
        Array.Clear(Jac, 0, nPPS);
        if (nPCS > 0) Array.Clear(JacCtrl, 0, nPCS);
    }
}
