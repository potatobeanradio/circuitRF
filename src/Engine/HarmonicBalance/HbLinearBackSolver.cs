using System.Numerics;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// Lazy, cached back-solver for linear-interior node voltages and branch currents.
///
/// After HB convergence, combining the per-sweep-point source RHS (snapshotted during
/// the sweep loop while component state was current) with the converged NL currents
/// allows the full-MNA system to be solved for every linear-interior node voltage and
/// every linear branch current (IProbe, inductors, etc.).
///
/// The source RHS must be snapshotted during the sweep loop — it cannot be rebuilt
/// afterwards because ToneSourceModel._currentPhasors reflects only the last sweep
/// point at that point.
///
/// Cache key: (harmonicK, sweepIdx) → full solution vector x.
/// x[0..NonGroundCount-1] = node voltages; x[NonGroundCount..] = branch currents.
/// </summary>
public sealed class HbLinearBackSolver : ILinearBackSolver
{
    private readonly HbLinearExtractor _extractor;
    private readonly double            _f0;
    private readonly int               _K;
    private readonly Complex[][,]      _iNl;    // [sweepIdx][N, K+1]
    private readonly Complex[][][]     _bSrc;   // [sweepIdx][K+1][mnaSize] — per-sweep source RHS
    private readonly NodeMap           _nodes;

    private readonly Dictionary<(int k, int si), Complex[]> _cache = new();

    public HbLinearBackSolver(
        HbLinearExtractor extractor,
        double            f0,
        int               K,
        Complex[][,]      iNl,
        Complex[][][]     bSrc,
        ElaboratedNetlist netlist)
    {
        _extractor = extractor;
        _f0        = f0;
        _K         = K;
        _iNl       = iNl;
        _bSrc      = bSrc;
        _nodes     = netlist.Nodes;
    }

    // ── ILinearBackSolver ────────────────────────────────────────────────────

    public bool TryGetNodeNumber(string name, out int circNode)
    {
        if (_nodes.TryGetIndex(name, out circNode) && circNode > 0) return true;
        circNode = 0;
        return false;
    }

    public Complex GetNodeVoltage(int circNode, int harmonicK, int sweepIdx)
    {
        if (circNode <= 0) return Complex.Zero;
        var x   = GetSolution(harmonicK, sweepIdx);
        int idx = circNode - 1;
        return idx < x.Length ? x[idx] : Complex.Zero;
    }

    public int SweepCount    => _iNl.Length;
    public int NonGroundCount => _extractor.NonGroundCount;

    // ── Internal (also used by LinearBackSolveTests) ─────────────────────────

    /// <summary>
    /// Full solution vector for harmonic k at sweep index si (lazy + cached).
    /// x[0..NonGroundCount-1] = node voltages; x[NonGroundCount..] = branch currents.
    /// </summary>
    public Complex[] GetSolution(int k, int si)
    {
        var key = (k, si);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        double omega = k == 0 ? 0.0 : 2.0 * Math.PI * k * _f0;
        int    N     = _extractor.InterfaceCount;
        var    iNlK  = new Complex[N];

        if (si < _iNl.Length)
            for (int n = 0; n < N; n++)
                iNlK[n] = _iNl[si][n, k];

        // Use the per-sweep bSrc — snapshotted while component state was current.
        var bSrc = (si < _bSrc.Length && k < _bSrc[si].Length)
            ? _bSrc[si][k]
            : Array.Empty<Complex>();

        var x = _extractor.SolveFullNetwork(omega, iNlK, bSrc);
        _cache[key] = x;
        return x;
    }
}
