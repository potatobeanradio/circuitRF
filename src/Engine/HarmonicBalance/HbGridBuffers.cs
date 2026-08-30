using CircuitRF.Core;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// HB-P4 M2 — the engine side of the grid door: the per-device <see cref="GridResult"/> buffers a
/// device pass writes into, the gather buffer its port voltages are transposed into, and the
/// per-sample view that lets the existing accumulation code read a grid result unchanged.
///
/// <para><b>Why thread-static rather than threaded through the call chain.</b> The buffers must
/// survive from one Newton iteration to the next or the allocation this brief removes simply moves
/// up a level — a Hero-2 iteration's 33 KB of per-sample garbage would become 3 KB of per-iteration
/// garbage, which is better but not the point. <c>EvaluateNonlinear</c> is a public static entry
/// point with callers in three engines and in the test suite, so threading a cache parameter through
/// every overload would be a wide change for a private concern. A thread-static pool is reused by
/// whichever solve is running on this thread, grows to the largest shape it has been asked for and
/// never shrinks; a device pass is single-threaded (the parallel split lives INSIDE
/// <c>SddModel.EvaluateGrid</c>, over samples), so nothing here is shared between threads.</para>
/// </summary>
internal static class HbGridBuffers
{
    [ThreadStatic] private static GridResult[]? _results;
    [ThreadStatic] private static double[]? _portV;
    [ThreadStatic] private static HbGridSampler? _sampler;

    /// <summary>The result buffer for device ordinal <paramref name="devOrd"/> of the current pass.</summary>
    public static GridResult Result(int devOrd)
    {
        if (_results is null || _results.Length <= devOrd)
        {
            var grown = new GridResult[devOrd + 1];
            _results?.CopyTo(grown, 0);
            for (int i = 0; i < grown.Length; i++) grown[i] ??= new GridResult();
            _results = grown;
        }
        return _results[devOrd];
    }

    /// <summary>The per-sample view onto a grid result.</summary>
    public static HbGridSampler Sampler() => _sampler ??= new HbGridSampler();

    /// <summary>
    /// The scratch each engine transposes its device's port voltages into — <c>[port][t]</c>, the
    /// layout <see cref="ComponentModel.EvaluateGrid"/> reads. The FILL is left to the caller (three
    /// short loops, one per engine) rather than taken as a delegate: the three time grids are shaped
    /// differently (flat, 2-D lattice, APFT list) and a per-sample callback would cost more than the
    /// transpose itself on a 1,024-sample grid.
    /// </summary>
    public static double[] PortVBuffer(int sampleCount, int portCount)
    {
        int need = portCount * sampleCount;
        if (_portV is null || _portV.Length < need) _portV = new double[need];
        return _portV;
    }

}

/// <summary>
/// HB-P4 M2 — presents one sample of a <see cref="GridResult"/> as a <see cref="NonlinearResult"/>
/// over buffers it owns.
///
/// <para>This is deliberately a COPY of a dozen doubles rather than a rewrite of the three engines'
/// accumulation loops. Those loops are the KCL port stamp (<c>PortAdd</c>/<c>PortAdd4</c>, four
/// signed corners per port pair) and the w≥2 bucket accumulation — the part of the device pass most
/// worth not transcribing three more times. The copy is 2P + 2P² doubles per sample against the
/// ~3.5 µs per sample this brief removes, and it allocates nothing.</para>
/// </summary>
internal sealed class HbGridSampler
{
    private double[] _i = [], _q = [];
    private double[,] _dg = new double[0, 0], _dc = new double[0, 0];
    private readonly List<WeightedTerm> _terms = [];
    private int _portCount = -1, _termCount = -1;

    public NonlinearResult Sample(GridResult g, int t)
    {
        int P = g.PortCount;
        var live = g.LiveTerms;
        if (P != _portCount || live.Length != _termCount)
        {
            _i = new double[P];
            _q = new double[P];
            _dg = new double[P, P];
            _dc = new double[P, P];
            _terms.Clear();
            for (int b = 0; b < live.Length; b++)
                _terms.Add(new WeightedTerm(live[b].W, new double[P], new double[P, P]));
            _portCount = P;
            _termCount = live.Length;
        }
        else
        {
            // The w set is fixed per device, but its ORDER must still line up if a later device pass
            // reuses this sampler at the same shape with different weights.
            for (int b = 0; b < live.Length; b++)
                if (_terms[b].W != live[b].W)
                {
                    _terms[b] = new WeightedTerm(live[b].W, _terms[b].Value, _terms[b].Jac);
                }
        }

        for (int p = 0; p < P; p++)
        {
            _i[p] = g.I[g.PortBase(p) + t];
            _q[p] = g.Q[g.PortBase(p) + t];
            for (int q = 0; q < P; q++)
            {
                _dg[p, q] = g.Dg[g.JacBase(p, q) + t];
                _dc[p, q] = g.Dc[g.JacBase(p, q) + t];
            }
        }

        for (int b = 0; b < live.Length; b++)
        {
            var src = live[b];
            var dst = _terms[b];
            for (int p = 0; p < P; p++)
            {
                dst.Value[p] = src.Value[g.PortBase(p) + t];
                for (int q = 0; q < P; q++) dst.Jac[p, q] = src.Jac[g.JacBase(p, q) + t];
            }
        }

        return _terms.Count == 0
            ? new NonlinearResult(_i, _q, _dg, _dc)
            : new NonlinearResult(_i, _q, _dg, _dc, _terms);
    }
}
