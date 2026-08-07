// ================================================================
//  InverseSolve.cs  —  M2 of brief-harmonicarf-h6
//
//  R-h6-6   ALL marked harmonics solve SIMULTANEOUSLY. The unknowns are Re/Im of every marked
//           extrinsic termination; the equations are "every marked band's intrinsic Γ equals its
//           target". Square: 8 × 8 for four markers. Solving one band at a time and iterating is a
//           DIFFERENT (and wrong) problem — the harmonics are coupled, and that coupling is the
//           phenomenon the tool exists to show.
//  R-h6-7   the residual differs by SIDE. A load-side target is §4.5.1's ratio; a source-side target
//           is §4.5.3's conversion-matrix diagonal. Both are read from HarmonicaDataSet.Intrinsic,
//           which is the ONE definition of either.
//  R-h6-8   full FD Jacobian on drag-start, rank-1 Broyden per frame, automatic FD refresh when the
//           residual stops decreasing.
//  R-h6-9   a failed solve moves NOTHING. No partial application.
// ================================================================

using System.Numerics;
using System.Threading;

namespace CircuitRF.Harmonica;

/// <summary>One unknown/equation pair of the inverse solve: a MARKED extrinsic termination.</summary>
public readonly record struct InverseBand(TerminationSide Side, int Band);

/// <summary>Why an inverse solve did not produce an answer. Every one of these leaves the extrinsic
/// set exactly where it was (R-h6-9).</summary>
public enum InverseFailure
{
    None,

    /// <summary>The iteration budget ran out with the residual still above tolerance.</summary>
    NotConverged,

    /// <summary>An HB solve inside the iteration failed to converge.</summary>
    HbFailed,

    /// <summary>
    /// A candidate put the FUNDAMENTAL SOURCE termination outside the unit circle, i.e. at negative
    /// resistance. Available power is not defined against a source with <c>Re Z ≤ 0</c>, so the drive
    /// amplitude — and with it the whole stated-drive operating point of R-h6-11 — becomes
    /// meaningless rather than merely unusual. This is NOT the same case as R-h6-10's allowed
    /// out-of-circle solution, which is about where the answer LANDS on any other band.
    /// </summary>
    ActiveSourceFundamental,

    /// <summary>The Jacobian is singular at this point — the map is locally degenerate.</summary>
    Singular,
}

/// <summary>What one frame of an inverse drag cost and whether it landed.</summary>
public sealed record InverseSolveResult(
    bool Converged,
    InverseFailure Failure,
    Complex[] Gammas,
    double Residual,
    int Solves,
    int Iterations,
    int FdRefreshes);

/// <summary>Knobs for the inverse solve. Defaults are §6.6's own.</summary>
public sealed record InverseSolveOptions
{
    /// <summary>R-h6-11 — the drive the equation is posed at, from the power-sweep cursor.</summary>
    public double PavlDbm { get; init; }

    /// <summary>Convergence tolerance on ‖F‖, in Γ units. A glyph is a few pixels wide; 2e-4 of the
    /// chart radius is well under one pixel on any panel size this ships at.</summary>
    public double Tolerance { get; init; } = 2e-4;

    /// <summary>Newton iterations per frame. §6.6's budget is "1–2 solves ≈ 2 ms/frame", so this is a
    /// ceiling for the frames that need more, not the expected count.</summary>
    public int MaxIterations { get; init; } = 6;

    /// <summary>The finite-difference perturbation, in Γ. Large enough that an HB solve's own
    /// convergence noise does not dominate the difference, small enough to be a derivative.</summary>
    public double FdStep { get; init; } = 1e-3;

    /// <summary>How many times one frame may rebuild the FD Jacobian after a stall before giving up.</summary>
    public int MaxFdRefreshes { get; init; } = 1;

    /// <summary>
    /// Open item 8 — the source side's own FD-refresh cadence. When any unknown is on the source side
    /// and this is &gt; 0, the Jacobian is rebuilt from finite differences every N frames regardless of
    /// whether the residual stalled. 0 keeps the load side's stall-driven cadence for both.
    /// </summary>
    public int SourceFdRefreshEveryFrames { get; init; }
}

/// <summary>
/// §6.6's inverse solve: drag an INTRINSIC glyph, and the extrinsic terminations that put it there
/// are found.
///
/// <para><b>The state that survives a frame is the Jacobian, and that is the whole design.</b> A full
/// FD Jacobian is <c>2m</c> perturbation solves; rebuilding it every frame caps the drag well under
/// 30 fps. So it is built once at <see cref="Begin"/> and maintained by rank-1 Broyden updates
/// thereafter, with an FD rebuild only when the residual stops decreasing.</para>
///
/// <para><b>The context is passed IN, per call.</b> The Jacobian is plain data and belongs to the
/// gesture; a <see cref="HarmonicaContext"/> belongs to a <see cref="SolveWorker"/> and is not
/// thread-safe. Keeping them apart is what lets an inverse drag run on the solve pool at all. Calls
/// are serialised on this object's own gate, so two overlapping frames of the same drag cannot
/// interleave their updates.</para>
///
/// <para><b>Nothing is committed unless the solve converged</b> (R-h6-9). The working vector, the
/// Jacobian and the warm start are all snapshotted and restored on failure — a glyph that lands
/// somewhere the solver did not actually reach is worse than one that sticks.</para>
/// </summary>
public sealed class InverseSolver
{
    private readonly TerminationSet     _baseline;
    private readonly InverseBand[]      _bands;
    private readonly InverseSolveOptions _opt;
    private readonly Lock               _gate = new();
    private readonly bool               _needsSource;

    private Complex[]   _x;                 // the committed extrinsic Γ, one per band
    private double[]?   _j;                 // row-major 2m × 2m
    private Complex[,]? _warm;              // warm start for the next HB solve
    private int         _frames;
    private double      _pavlDbm;

    /// <param name="warmStart">
    /// A converged interface spectrum to seed the first HB solve from, when the caller already has
    /// one — which the document always does, because a frame was solved before the drag began. Cold
    /// is 2.6× warm (§2), and the FD Jacobian at drag start is the one place a drag pays for several
    /// solves at once.
    /// </param>
    public InverseSolver(TerminationSet baseline, IReadOnlyList<InverseBand> bands,
                         IReadOnlyList<Complex> startGammas, InverseSolveOptions options,
                         Complex[,]? warmStart = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(bands);
        ArgumentNullException.ThrowIfNull(startGammas);
        if (bands.Count == 0) throw new ArgumentException("no marked bands to solve for", nameof(bands));
        if (startGammas.Count != bands.Count)
            throw new ArgumentException("one starting Γ per band", nameof(startGammas));

        _baseline = baseline.Clone();
        _bands    = [.. bands];
        _x        = [.. startGammas];
        _opt      = options;
        _pavlDbm  = options.PavlDbm;
        _warm     = warmStart;
        _needsSource = _bands.Any(b => b.Side == TerminationSide.Source);
    }

    /// <summary>The bands being solved for, in unknown order.</summary>
    public IReadOnlyList<InverseBand> Bands => _bands;

    /// <summary>Two real unknowns per band — §6.6's "8 × 8 for four markers", as a number.</summary>
    public int Dimension => 2 * _bands.Length;

    /// <summary>The committed extrinsic Γ per band. Only ever written by a CONVERGED solve.</summary>
    public IReadOnlyList<Complex> Current => _x;

    /// <summary>HB solves this solver has run, everything included.</summary>
    public int SolveCount { get; private set; }

    /// <summary>How many times the FULL FD Jacobian has been built — at <see cref="Begin"/> and on
    /// every stall. §6.6's cost claim is about the ratio of this to <see cref="FrameCount"/>.</summary>
    public int FdBuildCount { get; private set; }

    /// <summary>How many rank-1 Broyden updates have been applied.</summary>
    public int BroydenUpdateCount { get; private set; }

    /// <summary>Frames (i.e. <see cref="Step"/> calls) this drag has run.</summary>
    public int FrameCount => _frames;

    /// <summary>Whether any unknown is on the SOURCE side, i.e. whether the residual has to take the
    /// §4.5.3 route at all. The expensive half of an intrinsic evaluation.</summary>
    public bool UsesSourceSide => _needsSource;

    /// <summary>True once <see cref="Begin"/> has built a Jacobian.</summary>
    public bool IsStarted => _j is not null;

    /// <summary>R-h6-11 — the drive the equation is posed at. Intrinsic impedance is drive-dependent,
    /// so this is part of the question, not a detail of the answer.</summary>
    public double PavlDbm => _pavlDbm;

    /// <summary>
    /// Moves the operating point, for R-h6-11's <i>re-converge at compression</i> outer loop. The
    /// Jacobian is DISCARDED: it is the derivative at the old drive, and the whole reason the option
    /// is default-off is that this makes each outer iteration cost a fresh FD build.
    /// </summary>
    public void SetOperatingPoint(double pavlDbm)
    {
        lock (_gate)
        {
            if (pavlDbm == _pavlDbm) return;
            _pavlDbm = pavlDbm;
            _j = null;
        }
    }

    // ── the terminations a candidate x describes ─────────────────────────────

    /// <summary>
    /// The extrinsic termination set for a candidate. Unmarked bands keep the baseline's values —
    /// §6.6: "unmarked bands stay pinned at 1e-6 Ω and their intrinsic values are free to drift".
    /// </summary>
    public TerminationSet TerminationsFor(IReadOnlyList<Complex> gammas)
    {
        var t = _baseline.Clone();
        for (int i = 0; i < _bands.Length; i++)
            t.Set(_bands[i].Side, _bands[i].Band, HarmonicaDataSet.ImpedanceOf(gammas[i]));
        return t;
    }

    /// <summary>The intrinsic Γ every marked band presently sits at, evaluated through the forward
    /// path. This is what a drag reads to fill in the targets it is NOT moving.</summary>
    public Complex[]? Evaluate(HarmonicaContext ctx, IReadOnlyList<Complex> gammas,
                               CancellationToken ct = default)
    {
        lock (_gate)
        {
            var (g, fail) = Forward(ctx, gammas, ct);
            return fail == InverseFailure.None ? g : null;
        }
    }

    // ── R-h6-8 — the FD Jacobian at drag start ───────────────────────────────

    /// <summary>
    /// Builds the full finite-difference Jacobian at the current point: <c>2m</c> perturbation solves
    /// plus one residual. Called once when the gesture starts.
    /// </summary>
    public InverseFailure Begin(HarmonicaContext ctx, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var (f0, fail) = Forward(ctx, _x, ct);
            if (fail != InverseFailure.None) return fail;
            var j = BuildFdJacobian(ctx, _x, f0!, ct, out fail);
            if (fail != InverseFailure.None) return fail;
            _j = j;
            return InverseFailure.None;
        }
    }

    /// <summary>
    /// One frame of the drag. <paramref name="targets"/> is the intrinsic Γ every marked band should
    /// land on — the dragged glyph's new position, and every other glyph's present value.
    ///
    /// <para>Returns without moving anything unless the residual came inside tolerance (R-h6-9).</para>
    /// </summary>
    public InverseSolveResult Step(HarmonicaContext ctx, IReadOnlyList<Complex> targets,
                                   CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count != _bands.Length)
            throw new ArgumentException("one target per band", nameof(targets));

        lock (_gate)
        {
            _frames++;

            int solves0 = SolveCount, fd0 = FdBuildCount;
            var xSaved = (Complex[])_x.Clone();
            var jSaved = _j is null ? null : (double[])_j.Clone();
            var warmSaved = _warm;

            var result = StepCore(ctx, targets, solves0, fd0, ct);

            if (!result.Converged)
            {
                // R-h6-9 — NO partial application. The Broyden updates a failed attempt made are
                // discarded with it: they describe a point the solve did not stay at.
                _x    = xSaved;
                _j    = jSaved;
                _warm = warmSaved;
            }
            return result;
        }
    }

    private InverseSolveResult StepCore(HarmonicaContext ctx, IReadOnlyList<Complex> targets,
                                        int solves0, int fd0, CancellationToken ct)
    {
        int n = Dimension;
        var x = (Complex[])_x.Clone();

        InverseSolveResult Fail(InverseFailure why, double residual, int iters)
            => new(false, why, (Complex[])_x.Clone(), residual,
                   SolveCount - solves0, iters, FdBuildCount - fd0);

        var (g0, fail) = Forward(ctx, x, ct);
        if (fail != InverseFailure.None) return Fail(fail, double.NaN, 0);
        var f = Residual(g0!, targets);
        double norm = Norm(f);

        if (norm <= _opt.Tolerance)
            return new InverseSolveResult(true, InverseFailure.None, x, norm,
                                          SolveCount - solves0, 0, FdBuildCount - fd0);

        // Open item 8 — the source side's own cadence, when one has been asked for. It is deliberately
        // driven off the FRAME counter rather than the residual: the §4.5.3 diagonal is a function of
        // J, so its own derivative moves as the solution moves even when the residual is behaving.
        if (_needsSource && _opt.SourceFdRefreshEveryFrames > 0 &&
            _frames % _opt.SourceFdRefreshEveryFrames == 0)
        {
            var jr = BuildFdJacobian(ctx, x, g0!, ct, out fail);
            if (fail != InverseFailure.None) return Fail(fail, norm, 0);
            _j = jr;
        }

        if (_j is null)
        {
            var jb = BuildFdJacobian(ctx, x, g0!, ct, out fail);
            if (fail != InverseFailure.None) return Fail(fail, norm, 0);
            _j = jb;
        }

        int refreshes = 0;

        for (int it = 0; it < _opt.MaxIterations; it++)
        {
            ct.ThrowIfCancellationRequested();

            double[] delta;
            try { delta = SolveLinear(_j!, Negate(f), n); }
            catch (InvalidOperationException) { return Fail(InverseFailure.Singular, norm, it); }

            var xNext = Advance(x, delta);
            var (gNext, f2) = Forward(ctx, xNext, ct);
            if (f2 != InverseFailure.None) return Fail(f2, norm, it);

            var fNext = Residual(gNext!, targets);
            double normNext = Norm(fNext);

            if (normNext >= norm)
            {
                // The residual stopped decreasing. §6.6's own trigger for an FD refresh — the Broyden
                // approximation has drifted away from the true derivative and another update built on
                // it would only drift further.
                if (refreshes < _opt.MaxFdRefreshes)
                {
                    refreshes++;
                    var jr = BuildFdJacobian(ctx, x, g0!, ct, out var f3);
                    if (f3 != InverseFailure.None) return Fail(f3, norm, it);
                    _j = jr;
                    continue;
                }
                return Fail(InverseFailure.NotConverged, normNext, it + 1);
            }

            BroydenUpdate(_j!, delta, Subtract(fNext, f), n);
            BroydenUpdateCount++;

            x    = xNext;
            g0   = gNext;
            f    = fNext;
            norm = normNext;

            if (norm <= _opt.Tolerance)
            {
                _x = x;
                return new InverseSolveResult(true, InverseFailure.None, (Complex[])x.Clone(), norm,
                                              SolveCount - solves0, it + 1, FdBuildCount - fd0);
            }
        }

        return Fail(InverseFailure.NotConverged, norm, _opt.MaxIterations);
    }

    // ── the forward path ─────────────────────────────────────────────────────

    /// <summary>
    /// One residual evaluation: set the candidate terminations, solve at the stated drive, and read
    /// every marked band's intrinsic Γ out of <see cref="HarmonicaDataSet.Intrinsic"/>.
    /// </summary>
    private (Complex[]? Gamma, InverseFailure Failure) Forward(
        HarmonicaContext ctx, IReadOnlyList<Complex> gammas, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The one candidate that is not merely unusual but ill-posed: available power is undefined
        // against a source with Re Z ≤ 0, so the drive amplitude — and the whole stated-drive
        // operating point R-h6-11 rests on — would be meaningless rather than active.
        for (int i = 0; i < _bands.Length; i++)
            if (_bands[i].Side == TerminationSide.Source && _bands[i].Band == 1 &&
                HarmonicaDataSet.ImpedanceOf(gammas[i]).Real <= 0)
                return (null, InverseFailure.ActiveSourceFundamental);

        var terms = TerminationsFor(gammas);

        SolveCount++;
        var pt = ctx.Solve(terms, _pavlDbm, _warm);
        if (!pt.Converged) return (null, InverseFailure.HbFailed);
        _warm = pt.V;

        var intr = HarmonicaDataSet.Intrinsic(ctx, pt, includeSource: _needsSource);

        var g = new Complex[_bands.Length];
        for (int i = 0; i < _bands.Length; i++)
        {
            var v = intr.Gamma[(int)_bands[i].Side, _bands[i].Band];
            if (!double.IsFinite(v.Real) || !double.IsFinite(v.Imaginary))
                return (null, InverseFailure.HbFailed);
            g[i] = v;
        }
        return (g, InverseFailure.None);
    }

    private double[] Residual(IReadOnlyList<Complex> gammaIntr, IReadOnlyList<Complex> targets)
    {
        var f = new double[Dimension];
        for (int i = 0; i < _bands.Length; i++)
        {
            f[2 * i]     = gammaIntr[i].Real      - targets[i].Real;
            f[2 * i + 1] = gammaIntr[i].Imaginary - targets[i].Imaginary;
        }
        return f;
    }

    /// <summary>
    /// The full FD Jacobian of intrinsic Γ with respect to extrinsic Γ — <c>2m</c> perturbation
    /// solves. Note it differentiates the intrinsic VALUE, not the residual: the targets enter the
    /// residual additively, so the two Jacobians are the same matrix and building it this way lets a
    /// frame reuse it across a moving target.
    /// </summary>
    private double[] BuildFdJacobian(HarmonicaContext ctx, Complex[] x, Complex[] g0,
                                     CancellationToken ct, out InverseFailure failure)
    {
        int n = Dimension;
        var j = new double[n * n];
        double h = _opt.FdStep;

        for (int col = 0; col < n; col++)
        {
            var xp = (Complex[])x.Clone();
            int band = col / 2;
            xp[band] = (col % 2 == 0)
                ? new Complex(xp[band].Real + h, xp[band].Imaginary)
                : new Complex(xp[band].Real, xp[band].Imaginary + h);

            var (gp, fail) = Forward(ctx, xp, ct);
            if (fail != InverseFailure.None)
            {
                // A one-sided difference that steps into an ill-posed region is retried the other way
                // rather than abandoning the whole Jacobian for a boundary the solution is merely
                // near.
                xp[band] = (col % 2 == 0)
                    ? new Complex(x[band].Real - h, x[band].Imaginary)
                    : new Complex(x[band].Real, x[band].Imaginary - h);
                (gp, fail) = Forward(ctx, xp, ct);
                if (fail != InverseFailure.None) { failure = fail; return j; }
                h = -h;
            }

            for (int i = 0; i < _bands.Length; i++)
            {
                j[(2 * i) * n + col]       = (gp![i].Real      - g0[i].Real)      / h;
                j[(2 * i + 1) * n + col]   = (gp![i].Imaginary - g0[i].Imaginary) / h;
            }
            if (h < 0) h = -h;
        }

        FdBuildCount++;
        failure = InverseFailure.None;
        return j;
    }

    // ── small dense linear algebra ───────────────────────────────────────────

    private static Complex[] Advance(Complex[] x, double[] delta)
    {
        var next = new Complex[x.Length];
        for (int i = 0; i < x.Length; i++)
            next[i] = new Complex(x[i].Real + delta[2 * i], x[i].Imaginary + delta[2 * i + 1]);
        return next;
    }

    private static double Norm(double[] f)
    {
        double s = 0;
        foreach (double v in f) s += v * v;
        return Math.Sqrt(s);
    }

    private static double[] Negate(double[] f)
    {
        var r = new double[f.Length];
        for (int i = 0; i < f.Length; i++) r[i] = -f[i];
        return r;
    }

    private static double[] Subtract(double[] a, double[] b)
    {
        var r = new double[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = a[i] - b[i];
        return r;
    }

    /// <summary>Broyden's "good" rank-1 update: <c>J += ((ΔF − JΔ)Δᵀ) / (ΔᵀΔ)</c>.</summary>
    private static void BroydenUpdate(double[] j, double[] delta, double[] dF, int n)
    {
        double dd = 0;
        for (int i = 0; i < n; i++) dd += delta[i] * delta[i];
        if (dd < 1e-30) return;

        var jd = new double[n];
        for (int r = 0; r < n; r++)
        {
            double s = 0;
            for (int c = 0; c < n; c++) s += j[r * n + c] * delta[c];
            jd[r] = s;
        }

        for (int r = 0; r < n; r++)
        {
            double num = (dF[r] - jd[r]) / dd;
            if (num == 0) continue;
            for (int c = 0; c < n; c++) j[r * n + c] += num * delta[c];
        }
    }

    /// <summary>Gauss–Jordan with partial pivoting. <c>n</c> is 8 for four markers.</summary>
    private static double[] SolveLinear(double[] a, double[] b, int n)
    {
        var A = (double[])a.Clone();
        var B = (double[])b.Clone();

        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++)
                if (Math.Abs(A[r * n + col]) > Math.Abs(A[piv * n + col])) piv = r;

            if (Math.Abs(A[piv * n + col]) < 1e-300)
                throw new InvalidOperationException("the inverse-solve Jacobian is singular");

            if (piv != col)
            {
                for (int c = 0; c < n; c++) (A[col * n + c], A[piv * n + c]) = (A[piv * n + c], A[col * n + c]);
                (B[col], B[piv]) = (B[piv], B[col]);
            }

            double d = A[col * n + col];
            for (int c = 0; c < n; c++) A[col * n + c] /= d;
            B[col] /= d;

            for (int r = 0; r < n; r++)
            {
                if (r == col) continue;
                double f = A[r * n + col];
                if (f == 0) continue;
                for (int c = 0; c < n; c++) A[r * n + c] -= f * A[col * n + c];
                B[r] -= f * B[col];
            }
        }
        return B;
    }
}
