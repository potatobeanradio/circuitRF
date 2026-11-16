// ================================================================
//  TransientLines.cs  —  lossless lines in transient, as delays
//  (docs/design/transient.md §5.3; brief-trn-4 R-trn4-1, R-trn4-2)
//
//  A lossless line is stepped as what it is: each end is its Zc in series with
//  the wave that left the other end τ earlier (the method of characteristics),
//
//    V_a − Zc I_a = W_b(t − τ),   W_b = V_b + Zc I_b,   and a ↔ b.
//
//  A lossless coupled pair (CLIN) is two such lines in its even and odd modes,
//  with the frequency-independent modal transformation
//
//    v_e = (V_A + V_B)/2, i_e = I_A + I_B, Zc_e = Ze/2;  v_o = (V_A − V_B)/2,
//    i_o = I_A − I_B, Zc_o = Zo/2,
//
//  one delay per mode. Every row is real and constant, so it is a term of G.
//  The delayed wave is known from accepted steps whenever τ is at least the
//  step: it is read between them with DelayInterpolation's quadratic through
//  three accepted steps that do not straddle a corner (a breakpoint landing),
//  else linearly. When τ is shorter than the step, the delayed time falls inside
//  the step: the wave is linear between the last accepted step and the unknown
//  one, and the unknown's share (h − τ)/h is a term of the stepped matrix, so a
//  stage is factored per (alpha, h) while any line is that short.
//
//  At the operating point the line is its own steady state, the delayed wave the
//  wave now (share 1): at DC a lossless line is a short. A breakpoint is added τ
//  and 2τ after every source breakpoint, so an edge that crosses the line, and its
//  reflection, land on a step.
//
//  A lossy line (brief TRN-4b) is the same delay with a rational residual H~(s)
//  between the delayed wave and the row: V_a − Zc I_a = H~ * W_b(t − τ), H~ =
//  D + Σ R/(s − a) realized as extra linear unknowns at each end (as RationalSnpModel
//  realizes an S matrix), each driven by the delayed wave. The delayed wave then
//  enters several rows — the main row with weight D and each state row — so each
//  receiver carries its taps.
//
//  Outside the transient engine's own stamps (the DC seed's NonlinearDcEngine, a
//  check stamping it alone) the model stamps its own steady state at ω: the delay
//  as e^(−jωτ), the residual as realized.
// ================================================================

using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.Modeling;

namespace CircuitRF.Engine.Transient;

/// <summary>
/// A lossless line or coupled set as its characteristic (Branin) rows: one branch current into the line at each conductor's
/// end, and per mode and end <c>v_m − Zm i_m − W_m,other(t − τ_m) = 0</c>. While <see cref="Stepping"/> the delayed wave is
/// left to the right-hand side (and the stepped matrix); otherwise the stamp is the exact steady state at ω.
/// </summary>
public sealed class TransientLineModel : ComponentModel
{
    private readonly int[] _a, _b;           // each conductor's end node (0 = ground)
    private readonly double[,] _tv, _ti;     // modal transform: v_m = Σ Tv[m,k] V_k, i_m = Σ Ti[m,k] I_k
    private readonly ComponentModel _inner;

    internal TransientLineModel(ComponentModel inner, int[] endA, int[] endB, double[] z, double[] tau, double[,] tv, double[,] ti,
                                RationalModel?[]? residual = null)
    {
        _inner = inner;
        _a = endA; _b = endB; Z = z; Tau = tau; _tv = tv; _ti = ti;
        Residual = residual ?? new RationalModel?[endA.Length];
        BranchA = new int[endA.Length];
        BranchB = new int[endB.Length];
        Array.Fill(BranchA, -1); Array.Fill(BranchB, -1);
        _groups = new List<(int Pole, bool Pair)>[endA.Length];
        _statesA = new int[endA.Length][]; _statesB = new int[endA.Length][];
        for (int m = 0; m < endA.Length; m++)
        {
            _groups[m] = [];
            if (Residual[m] is { } r)
                for (int k = 0; k < r.Poles.Length; k++)
                {
                    bool pair = r.Poles[k].Imaginary != 0;
                    _groups[m].Add((k, pair));
                    if (pair) k++;
                }
            _statesA[m] = []; _statesB[m] = [];
        }
    }

    /// <summary>One conductor: the line as its own delay, and its lossy residual when it has one.</summary>
    internal static TransientLineModel Single(ComponentModel inner, int a, int b, double zc, double tau, RationalModel? residual = null) =>
        new(inner, [a], [b], [zc], [tau], new double[,] { { 1 } }, new double[,] { { 1 } }, [residual]);

    /// <summary>Each mode's residual H~(s) between the delayed wave and its row; null for a lossless mode (H~ = 1).</summary>
    public RationalModel?[] Residual { get; }

    private readonly List<(int Pole, bool Pair)>[] _groups;
    private readonly int[][] _statesA, _statesB;     // per mode: the residual's state rows at each end, in group order

    /// <summary>A symmetric coupled pair: conductor A from <paramref name="a1"/> to <paramref name="a2"/>, B from
    /// <paramref name="b1"/> to <paramref name="b2"/>, in its even and odd modes.</summary>
    internal static TransientLineModel Pair(ComponentModel inner, int a1, int a2, int b1, int b2, double ze, double zo, double tauE, double tauO) =>
        new(inner, [a1, b1], [a2, b2], [ze / 2, zo / 2], [tauE, tauO],
            new double[,] { { 0.5, 0.5 }, { 0.5, -0.5 } }, new double[,] { { 1, 1 }, { 1, -1 } });

    public override int PortCount => _inner.PortCount;
    public override ModelKind Kind => ModelKind.Linear;
    public override string[] TerminalNames => _inner.TerminalNames;

    /// <summary>Each mode's characteristic impedance (Ω, multiplier applied) and delay (s).</summary>
    public double[] Z { get; }
    public double[] Tau { get; }

    /// <summary>True while the transient engine stamps it: the delayed waves are not in the stamp.</summary>
    internal bool Stepping { get; set; }

    /// <summary>The branch rows of the last stamp, per conductor: the current into the line at its first and second end.</summary>
    internal int[] BranchA { get; }
    internal int[] BranchB { get; }

    internal int Conductors => _a.Length;

    public override void Stamp(IMnaContext mna, ElaboratedComponent c, double omega)
    {
        int k = _a.Length;
        for (int i = 0; i < k; i++) { BranchA[i] = mna.AddBranch(); mna.AddBranchCurrent(BranchA[i], _a[i], 0); }
        for (int i = 0; i < k; i++) { BranchB[i] = mna.AddBranch(); mna.AddBranchCurrent(BranchB[i], _b[i], 0); }
        for (int m = 0; m < k; m++)
        {
            int n = _groups[m].Sum(g => g.Pair ? 2 : 1);
            _statesA[m] = [.. Enumerable.Range(0, n).Select(_ => mna.AddBranch())];
            _statesB[m] = [.. Enumerable.Range(0, n).Select(_ => mna.AddBranch())];
        }
        var s = new Complex(0, omega);
        for (int m = 0; m < k; m++)
        {
            Complex h = Stepping ? Complex.Zero : Complex.Exp(new Complex(0, -omega * Tau[m]));
            End(mna, m, BranchA[m], _a, BranchA, _statesA[m], _b, BranchB, h, s);
            End(mna, m, BranchB[m], _b, BranchB, _statesB[m], _a, BranchA, h, s);
        }
    }

    /// <summary>One end of mode <paramref name="m"/>: its row v − Zc i − D·u − Σ r·z = 0 and its residual's state rows
    /// (s − a)/|a|·x − g·u = 0 (z = x/|a|; a pair as RationalSnpModel's real 2×2 block), u = h·W_other — h = 0 while stepping,
    /// when u is the right-hand side's and the stepped matrix's (TransientLines).</summary>
    private void End(IMnaContext mna, int m, int row, int[] here, int[] brHere, int[] states, int[] there, int[] brThere, Complex h, Complex s)
    {
        for (int i = 0; i < here.Length; i++)
        {
            if (_tv[m, i] != 0) mna.AddConstraint(row, here[i], _tv[m, i]);
            if (_ti[m, i] != 0) mna.AddBranchConstraint(row, brHere[i], -Z[m] * _ti[m, i]);
        }
        foreach (var (r, g) in TapsOf(m, row, states))
            if (h != Complex.Zero) AddWave(mna, r, g * h, m, there, brThere);
        if (Residual[m] is not { } res) return;
        int x = 0;
        foreach (var (p, pair) in _groups[m])
        {
            var a = res.Poles[p];
            double mag = a.Magnitude;
            var rr = res.Residues[0][p];
            if (!pair)
            {
                mna.AddBranchConstraint(states[x], states[x], (s - a.Real) / mag);
                mna.AddBranchConstraint(row, states[x], -rr.Real / mag);
                x++;
                continue;
            }
            int x1 = states[x], x2 = states[x + 1];
            mna.AddBranchConstraint(x1, x1, (s - a.Real) / mag);
            mna.AddBranchConstraint(x1, x2, -a.Imaginary / mag);
            mna.AddBranchConstraint(x2, x1, a.Imaginary / mag);
            mna.AddBranchConstraint(x2, x2, (s - a.Real) / mag);
            mna.AddBranchConstraint(row, x1, -rr.Real / mag);
            mna.AddBranchConstraint(row, x2, -rr.Imaginary / mag);
            x += 2;
        }
    }

    /// <summary>The rows the delayed wave u enters at one end of mode <paramref name="m"/>, each with its weight: the main
    /// row −D, each real state row −1, each pair's first row −2.</summary>
    private IEnumerable<(int Row, double K)> TapsOf(int m, int row, int[] states)
    {
        if (Residual[m] is not { } res) { yield return (row, -1); yield break; }
        if (res.D[0] != 0) yield return (row, -res.D[0]);
        int x = 0;
        foreach (var (_, pair) in _groups[m])
        {
            yield return (states[x], pair ? -2 : -1);
            x += pair ? 2 : 1;
        }
    }

    /// <summary>The taps of mode <paramref name="m"/>'s end, as the last stamp numbered its rows.</summary>
    internal (int Row, double K)[] Taps(int m, bool endA) =>
        [.. TapsOf(m, endA ? BranchA[m] : BranchB[m], endA ? _statesA[m] : _statesB[m])];

    private void AddWave(IMnaContext mna, int row, Complex coeff, int m, int[] nodes, int[] br)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            if (_tv[m, i] != 0) mna.AddConstraint(row, nodes[i], coeff * _tv[m, i]);
            if (_ti[m, i] != 0) mna.AddBranchConstraint(row, br[i], coeff * Z[m] * _ti[m, i]);
        }
    }

    /// <summary>Mode <paramref name="m"/>'s outgoing wave at one end, as (unknown, coefficient) pairs in a system whose node
    /// rows are node − 1.</summary>
    internal (int Col, double C)[] Wave(int m, bool endA)
    {
        var nodes = endA ? _a : _b;
        var br = endA ? BranchA : BranchB;
        var list = new List<(int, double)>();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (_tv[m, i] != 0 && nodes[i] > 0) list.Add((nodes[i] - 1, _tv[m, i]));
            if (_ti[m, i] != 0) list.Add((br[i], Z[m] * _ti[m, i]));
        }
        return [.. list];
    }
}

/// <summary>The lines of one transient run: each mode's delayed waves, their history, and what they add to the stepped
/// matrix and the right-hand side.</summary>
internal sealed class TransientLines
{
    /// <summary>One end of one mode: the row it writes, the other end's wave it reads, τ, and that wave's history.</summary>
    private sealed class Receiver
    {
        public required (int Row, double K)[] Taps;
        public required (int Col, double C)[] Src;
        public required double Tau;
        public readonly List<double> W = [];
    }

    private readonly List<Receiver> _rcv = [];
    private readonly List<double> _t = [];
    private readonly List<bool> _corner = [];
    private double _tauMax;

    public bool Any => _rcv.Count > 0;

    /// <summary>Reads every line's branch rows from the stamp just made (the engine's own numbering).</summary>
    public TransientLines(IEnumerable<TransientLineModel> lines)
    {
        foreach (var l in lines)
            for (int m = 0; m < l.Conductors; m++)
            {
                _rcv.Add(new Receiver { Taps = l.Taps(m, endA: true), Src = l.Wave(m, endA: false), Tau = l.Tau[m] });
                _rcv.Add(new Receiver { Taps = l.Taps(m, endA: false), Src = l.Wave(m, endA: true), Tau = l.Tau[m] });
                _tauMax = Math.Max(_tauMax, l.Tau[m]);
            }
    }

    /// <summary>The distinct delays, for the breakpoints.</summary>
    public IEnumerable<double> Delays => _rcv.Select(r => r.Tau).Where(t => t > 0).Distinct();

    /// <summary>Whether the stepped matrix at step <paramref name="h"/> carries a line's share of the current step: a key that
    /// is <paramref name="h"/> when it does and −1 when it does not, so a run with no short line keeps one stage per alpha.</summary>
    public double CouplingKey(double h) => _rcv.Any(r => r.Tau < h) ? h : -1;

    /// <summary>The stepped matrix's share of the current step's delayed wave at step <paramref name="h"/>; with
    /// <paramref name="h"/> = ∞ every line couples fully (the steady state: the operating point).</summary>
    public List<(int r, int c, double v)> Coupling(double h)
    {
        var e = new List<(int, int, double)>();
        foreach (var r in _rcv)
        {
            double c0 = double.IsPositiveInfinity(h) ? 1 : r.Tau < h ? (h - r.Tau) / h : 0;
            if (c0 == 0) continue;
            foreach (var (row, k) in r.Taps)
                foreach (var (col, c) in r.Src) e.Add((row, col, k * c0 * c));
        }
        return e;
    }

    /// <summary>The history's first sample: the starting state, before which the waves were constant.</summary>
    public void Start(double[] x0)
    {
        _t.Clear(); _corner.Clear();
        _t.Add(0); _corner.Add(true);
        foreach (var r in _rcv) { r.W.Clear(); r.W.Add(Dot(r.Src, x0)); }
    }

    /// <summary>An accepted step at <paramref name="t"/>; <paramref name="corner"/> when it landed on a breakpoint.</summary>
    public void Push(double t, double[] x, bool corner)
    {
        _t.Add(t); _corner.Add(corner);
        foreach (var r in _rcv) r.W.Add(Dot(r.Src, x));
        // forget what no delayed read can reach any more (keeping three samples before it for the stencil)
        if (_t.Count > 4096)
        {
            int keep = _t.BinarySearch(t - _tauMax);
            if (keep < 0) keep = ~keep;
            keep = Math.Max(0, keep - 4);
            if (keep > 2048)
            {
                _t.RemoveRange(0, keep); _corner.RemoveRange(0, keep);
                foreach (var r in _rcv) r.W.RemoveRange(0, keep);
            }
        }
    }

    /// <summary>The delayed waves' known part for the step from <paramref name="t"/> to t + <paramref name="h"/>.</summary>
    public void AddDelayed(double t, double h, double[] rhs)
    {
        foreach (var r in _rcv)
        {
            double u = r.Tau < h ? r.Tau / h * r.W[^1] : ValueAt(r.W, t + h - r.Tau);
            foreach (var (row, k) in r.Taps) rhs[row] -= k * u;
        }
    }

    /// <summary>A wave at <paramref name="td"/> ≤ the newest sample, from its history.</summary>
    private double ValueAt(List<double> w, double td)
    {
        int n = _t.Count;
        if (td <= _t[0]) return w[0];
        if (td >= _t[n - 1]) return w[n - 1];
        int j = _t.BinarySearch(td);
        if (j >= 0) return w[j];
        j = ~j - 1;                                   // _t[j] < td < _t[j + 1]
        bool left = j >= 1 && !_corner[j];
        bool right = j + 2 < n && !_corner[j + 1];
        if (left)
        {
            var (a, b, c) = DelayInterpolation.Quadratic(td, _t[j - 1], _t[j], _t[j + 1]);
            return a * w[j - 1] + b * w[j] + c * w[j + 1];
        }
        if (right)
        {
            var (a, b, c) = DelayInterpolation.Quadratic(td, _t[j], _t[j + 1], _t[j + 2]);
            return a * w[j] + b * w[j + 1] + c * w[j + 2];
        }
        double f = (td - _t[j]) / (_t[j + 1] - _t[j]);
        return w[j] + f * (w[j + 1] - w[j]);
    }

    private static double Dot((int Col, double C)[] src, double[] x)
    {
        double s = 0;
        foreach (var (col, c) in src) s += c * x[col];
        return s;
    }
}
