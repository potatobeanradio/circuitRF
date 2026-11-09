using System.Numerics;
using CircuitRF.Engine.HarmonicBalance;

namespace CircuitRF.Engine.Envelope;

/// <summary>
/// One envelope step's Newton on the interface spectrum, shared by both engines: a chord iteration (the
/// Jacobian LU is reused across iterations and steps while it keeps converging) with the harmonic-balance
/// backtracking line search on every update.
///
/// <para><b>Why the line search.</b> Without it a full update was taken whatever it did to the residual. On a
/// composed FET at a signal peak the first update drove the trap-state row's residual to ~1e13 and the
/// thermal row's to ~1e28, and the step never came back — the run stopped. The search judges
/// <see cref="HbNewton.WeightedL2"/> with <see cref="HbNewton.LineSearchWeights"/> when a device has a state
/// row (a trap state in volts, a temperature in kelvin), exactly as <see cref="HbNewton"/> does, so no row
/// outweighs the others by its unit. A full step that reduces the residual is accepted on the first trial,
/// so a step that converged before converges through the same iterates.</para>
///
/// <para>A CHORD direction comes from a Jacobian built at an earlier iterate and need not be a descent
/// direction, so a rejected chord step is not halved: the Jacobian is rebuilt at the iterate and the search
/// runs on the fresh direction.</para>
///
/// <para>Unknowns are laid out as the engines lay them out: <c>V[a, c]</c> at rows <c>2(a·C + c)</c> and
/// <c>+1</c>, the imaginary part of column 0 (DC) held at zero.</para>
/// </summary>
internal sealed class EnvelopeNewton
{
    public const int MaxIterations = 50;

    private readonly int _dof;
    private readonly HbNewton.LineSearchJudge _judge;
    private DenseLu? _lu;
    private double[]? _w;

    public int JacobianBuilds { get; private set; }
    public long Iterations { get; private set; }

    public EnvelopeNewton(int dof, HbNewton.LineSearchJudge judge)
    {
        _dof = dof;
        _judge = judge;
    }

    /// <summary>Drops the held Jacobian — the step's <c>alpha</c> changed, so it no longer describes the step.</summary>
    public void Invalidate() => _lu = null;

    /// <summary>
    /// Iterates <paramref name="V"/> in place to the step's solution and returns the accepted evaluation, or
    /// null when the step did not converge in <see cref="MaxIterations"/> or the Jacobian was singular. On null
    /// <paramref name="V"/> holds the last iterate.
    /// </summary>
    /// <param name="evaluate">The residual at V (real-split, one row per unknown) and whatever the caller needs
    /// from that evaluation — the Jacobian ingredients and the charge/injection spectra.</param>
    /// <param name="jacobian">The row-major real Jacobian at the evaluation it is given. Consumed in place.</param>
    /// <param name="minIterations">Updates taken before the tolerance may accept: 0 lets the predictor itself be accepted
    /// (every run but one), 1 makes every step take a Newton update — EvmSplit's noiseless run, so it is at least as
    /// converged as the noisy one, whose noise the predictor never meets.</param>
    public T? Solve<T>(Complex[,] V, double tol, Func<Complex[,], (double[] F, T State)> evaluate, Func<T, double[]> jacobian,
                       int minIterations = 0)
        where T : class
    {
        var V0 = (Complex[,])V.Clone();
        var (F, st) = evaluate(V);
        double rn = MaxAbs(F), r0 = double.NaN;
        bool fresh = false;
        for (int it = 0; it < MaxIterations; it++)
        {
            // A stalled residual is accepted only after an update made with a FRESH Jacobian (a true round-off
            // floor). A stalled CHORD iteration means a stale Jacobian: rebuild it instead — accepting there left
            // isolated steps up to 1e3 x Tol off (single-sample glitches).
            if (it >= minIterations && (rn < tol || (fresh && !double.IsNaN(r0) && rn < 1e2 * tol && rn > 0.5 * r0)))
            {
                Iterations += it;
                return st;
            }
            fresh = false;
            if (_lu is null || (!double.IsNaN(r0) && rn > 0.25 * r0))
            {
                if (!Refresh(st, jacobian)) return null;
                fresh = true;
            }
            r0 = rn;
            Array.Copy(V, V0, V.Length);
            var d = Direction(F);
            var norm = HbNewton.LineSearchNorm.At(F, _w, _judge);

            if (!fresh)
            {
                Apply(V, V0, d, 1.0);
                var (F1, st1) = evaluate(V);
                if (Accepts(norm.Of(F1), norm.Start, 1.0))
                {
                    (F, st) = (F1, st1);
                    rn = MaxAbs(F);
                    continue;
                }
                // The stale direction made things worse: rebuild at the iterate and search along the fresh one.
                Array.Copy(V0, V, V.Length);
                if (!Refresh(st, jacobian)) return null;
                fresh = true;
                d = Direction(F);
                norm = HbNewton.LineSearchNorm.At(F, _w, _judge);
            }

            HbNewton.Backtrack(norm.Start, 1.0, lam =>
            {
                Apply(V, V0, d, lam);
                (F, st) = evaluate(V);
                return norm.Of(F);
            });
            rn = MaxAbs(F);
        }
        return null;
    }

    private bool Refresh<T>(T st, Func<T, double[]> jacobian)
    {
        var J = jacobian(st);
        _w = _judge != HbNewton.LineSearchJudge.Plain ? HbNewton.LineSearchWeights(J, _dof) : null;
        _lu = DenseLu.Factor(J, _dof);
        JacobianBuilds++;
        return _lu is not null;
    }

    private double[] Direction(double[] F)
    {
        var d = new double[_dof];
        for (int i = 0; i < _dof; i++) d[i] = -F[i];
        _lu!.Solve(d);
        return d;
    }

    private static bool Accepts(double fTrial, double f0, double lambda)
        => fTrial <= (1.0 - HbNewton.ArmijoC * lambda) * f0;

    private static void Apply(Complex[,] V, Complex[,] V0, double[] d, double lambda)
    {
        int rows = V.GetLength(0), cols = V.GetLength(1);
        for (int a = 0; a < rows; a++)
            for (int c = 0; c < cols; c++)
            {
                int ir = 2 * (a * cols + c);
                V[a, c] = V0[a, c] + lambda * new Complex(d[ir], c == 0 ? 0 : d[ir + 1]);
            }
    }

    private static double MaxAbs(double[] F)
    {
        double m = 0;
        foreach (var f in F) m = Math.Max(m, Math.Abs(f));
        return m;
    }
}
