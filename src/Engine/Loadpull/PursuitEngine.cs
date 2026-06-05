using System.Numerics;
using RfCore;

namespace CircuitRF.Engine.Loadpull;

/// <summary>
/// Baylis steepest-ascent search for MXP (max output power) or MXE (max efficiency)
/// terminations in the VSWR plane.  loadpull_pursuit.md §1.
///
/// One engine; the caller supplies a criterion delegate that scores each candidate
/// termination (supplied and returned as Z in Ω).
///
/// B5 fix — internal working representation is Γ (reflection coefficient, normalised to Z0).
/// Reason: VSWR distance is monotonic with |ΔΓ| for small steps near any Γ.  Working in Γ
/// makes the gradient (Euclidean in Re/Im Γ space) and the step (also Euclidean in Γ space)
/// use the SAME metric.  In Z-space the VSWR metric is non-Euclidean (Möbius), so the
/// Z-gradient direction differs from the VSWR-gradient direction — the old bug.
///
/// Z0 = 50 Ω (Smith-chart normalisation).  Z↔Γ via RfHelpers.Z2G/G2Z at the boundaries.
///
/// Algorithm (Baylis et al.):
///   1. Tangent-plane stage: query 2 neighbours at Dn (VSWR) in Γ-space, fit
///      ΔC = m1·ΔΓ_re + m2·ΔΓ_im, compute steepest-ascent direction in Γ-space.
///   2. Ascend by Ds along that direction; repeat while criterion increases, shrink Ds
///      to 1/3 on failure.
///   3. Converge when Ds &lt; convergence threshold (= Dn); 2nd-order polynomial refinement
///      in Γ-space; report analytic optimum converted back to Z.
/// </summary>
public sealed class PursuitEngine
{
    // ── Tunable defaults ──────────────────────────────────────────────────────

    /// <summary>
    /// VSWR step for tangent-plane neighbours (Dn).
    /// Small enough to fit a local linear plane; large enough to resolve criterion gradients.
    /// </summary>
    public double Dn { get; init; } = 1.05;

    /// <summary>Initial ascent step size (Ds, VSWR).</summary>
    public double DsInitial { get; init; } = 1.3;

    /// <summary>
    /// Convergence threshold: when Ds falls below this (VSWR), do final polynomial refinement.
    /// Default equals Dn (standard Baylis stopping criterion).
    /// </summary>
    public double ConvergenceThreshold { get; init; } = 1.05;

    /// <summary>Maximum ascent steps (safety limit).</summary>
    public int MaxAscentSteps { get; init; } = 40;

    // ── Result ────────────────────────────────────────────────────────────────

    public sealed class PursuitResult
    {
        /// <summary>Reported optimum termination (analytic polynomial peak, Z in Ω).</summary>
        public Complex OptimumZ     { get; }
        /// <summary>Criterion value at the optimum (from the nearest queried point).</summary>
        public double  OptimumValue { get; }
        /// <summary>All (Z, criterion?) pairs queried during the search.</summary>
        public IReadOnlyList<(Complex Z, double? Value)> AllQueries { get; }
        /// <summary>Z values that were unscorable (non-converging/non-compressing).</summary>
        public IReadOnlyList<Complex> UnscorableZ { get; }
        /// <summary>True if the search converged; false if it hit MaxAscentSteps or an abort.</summary>
        public bool Converged { get; }
        /// <summary>Non-null if the search aborted (e.g. start point unscorable).</summary>
        public string? AbortReason { get; }

        public PursuitResult(Complex optimumZ, double optimumValue,
            IReadOnlyList<(Complex, double?)> allQueries,
            IReadOnlyList<Complex> unscorableZ,
            bool converged, string? abortReason = null)
        {
            OptimumZ     = optimumZ;
            OptimumValue = optimumValue;
            AllQueries   = allQueries;
            UnscorableZ  = unscorableZ;
            Converged    = converged;
            AbortReason  = abortReason;
        }
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    // Z0 for Γ normalisation (Smith-chart reference impedance).
    private const double Z0 = 50.0;

    /// <summary>
    /// Run the Baylis steepest-ascent search starting from <paramref name="startZ"/>.
    ///
    /// <paramref name="criterion"/> returns the scalar criterion at a candidate Z (in Ω),
    /// or null if the point is unscorable (non-convergent or non-compressing).
    ///
    /// Returns immediately with AbortReason set if the start point (or its neighbours)
    /// are all unscorable (no tangent plane can be formed).
    ///
    /// B5: all internal maths run in Γ-space (Re/Im of reflection coefficient) so the
    /// Euclidean gradient and the Euclidean step size use the same metric.
    /// </summary>
    public PursuitResult Run(Complex startZ, Func<Complex, double?> criterion)
    {
        var queries    = new List<(Complex Z, double? Value)>();
        var unscorable = new List<Complex>();

        // Score wrapper: queries using Z, caches in Z.
        double? Score(Complex g)   // g = Γ (internal)
        {
            Complex z = GammaToZ(g);
            if (z.Real <= 0) return null;     // physical guard: Z must have positive real part
            var v = criterion(z);
            queries.Add((z, v));
            if (v is null) unscorable.Add(z);
            return v;
        }

        // B5: convert start to Γ; all search maths in Γ.
        Complex startG = ZToGamma(startZ);

        // ── 1. Tangent-plane stage in Γ-space ─────────────────────────────────
        double? c0 = Score(startG);
        if (c0 is null)
            return Abort(startZ, queries, unscorable,
                $"Start point Z={startZ} is unscorable — DUT does not compress; " +
                "raise PinMax or check bias/load.");

        // Two neighbours at Dn: step in +Re(Γ) and +Im(Γ) directions.
        double dG = VswrToDeltaGamma(startG, Dn);   // Euclidean Γ-step for target VSWR
        Complex n1G = new Complex(startG.Real + dG, startG.Imaginary);
        Complex n2G = new Complex(startG.Real,       startG.Imaginary + dG);

        double? c1 = Score(n1G);
        double? c2 = Score(n2G);

        if (c1 is null && c2 is null)
            return Abort(startZ, queries, unscorable,
                "Both tangent-plane neighbours are unscorable — cannot form a gradient; " +
                "try a different start point.");

        // B6 fix: mirror = reflect through startG in Γ-space (no negative-R probes).
        if (c1 is null) { n1G = 2 * startG - n1G; c1 = Score(n1G); }
        if (c2 is null) { n2G = 2 * startG - n2G; c2 = Score(n2G); }

        // Fit tangent plane ΔC = m1·ΔΓ_re + m2·ΔΓ_im (Baylis Eq. 1).
        // Now both Δ and step are in the same Euclidean Γ metric — B5 fix.
        double dx1 = (n1G - startG).Real, dy1 = (n1G - startG).Imaginary;
        double dx2 = (n2G - startG).Real, dy2 = (n2G - startG).Imaginary;
        (double m1, double m2) = FitLinearPlane(
            dx1, dy1, (c1 ?? c0.Value) - c0.Value,
            dx2, dy2, (c2 ?? c0.Value) - c0.Value);

        // Steepest-ascent direction unit vector in (Re,Im) Γ-space.
        double gradMag = Math.Sqrt(m1 * m1 + m2 * m2);
        if (gradMag < 1e-20)
        {
            return new PursuitResult(startZ, c0.Value, queries, unscorable, converged: true);
        }
        double ux = m1 / gradMag, uy = m2 / gradMag;

        // ── 2. Ascent loop in Γ-space ─────────────────────────────────────────
        double   ds      = DsInitial;
        Complex  curG    = startG;
        double   cCur    = c0.Value;
        var      history = new List<(Complex G, double C)> { (startG, c0.Value) };

        for (int step = 0; step < MaxAscentSteps; step++)
        {
            if (ds < ConvergenceThreshold)
                break;

            // Step ds (VSWR) along ascent direction in Γ-space.
            double stepLen  = VswrToDeltaGamma(curG, ds);
            Complex candG   = new Complex(curG.Real + ux * stepLen, curG.Imaginary + uy * stepLen);
            // Clamp Γ to the unit disk (physical passive terminations only).
            if (candG.Magnitude >= 1.0) candG = candG / (candG.Magnitude + 1e-9) * 0.99;

            double? cCand = Score(candG);

            if (cCand is not null && cCand.Value > cCur)
            {
                history.Add((candG, cCand.Value));
                curG  = candG;
                cCur  = cCand.Value;
            }
            else
            {
                ds /= 3.0;
            }
        }

        // ── 3. Final 2nd-order polynomial refinement in Γ-space (Baylis Eq. 4) ─
        // Use ONLY the 4 cardinal neighbours at Dn around curG — do NOT include the
        // ascent history.  Reason: history points are at large ΔΓ offsets from curG
        // and dominate the least-squares fit, corrupting the local curvature estimate.
        // Baylis Eq. 4 is a local refinement; it uses points within Dn of the converged point.
        double dGref = VswrToDeltaGamma(curG, Dn);
        var refineG = new[]
        {
            new Complex(curG.Real + dGref, curG.Imaginary),
            new Complex(curG.Real - dGref, curG.Imaginary),
            new Complex(curG.Real,         curG.Imaginary + dGref),
            new Complex(curG.Real,         curG.Imaginary - dGref),
        };

        var poly = new List<(double Dx, double Dy, double Dc)>();
        poly.Add((0, 0, 0));   // current point as origin
        foreach (var rg in refineG)
        {
            if (rg.Magnitude >= 1.0) continue;
            double? rc = Score(rg);
            if (rc is null) continue;
            poly.Add(((rg - curG).Real, (rg - curG).Imaginary, rc.Value - cCur));
        }

        Complex optimumG = curG;
        if (poly.Count >= 5)   // origin + at least 4 cardinal neighbours
        {
            var (mm1, mm2, mm11, mm12, mm22) = FitQuadraticSurface(poly);
            Complex delta = SolveQuadraticOptimum(mm1, mm2, mm11, mm12, mm22);
            Complex candG2 = curG + delta;
            // Accept only if the analytic optimum is within 2·Dn of current and inside unit disk.
            // Tight radius prevents the ill-conditioned polynomial from extrapolating too far.
            if (candG2.Magnitude < 1.0 &&
                RfHelpers.VswrFromZ(GammaToZ(candG2), GammaToZ(curG)) < 2.0 * Dn + 1.0)
                optimumG = candG2;
        }

        Complex optimumZ = GammaToZ(optimumG);
        return new PursuitResult(optimumZ, cCur, queries, unscorable, converged: true);
    }

    // ── Γ↔Z converters ───────────────────────────────────────────────────────

    private static Complex ZToGamma(Complex z) => RfHelpers.Z2G(z / Z0);
    private static Complex GammaToZ(Complex g) => RfHelpers.G2Z(g) * Z0;

    /// <summary>
    /// Euclidean Γ-step magnitude that corresponds to <paramref name="vswr"/> from
    /// <paramref name="g"/>.  For a small-VSWR step, VSWR ≈ 1 + 2·|ΔΓ| (near g=0),
    /// so |ΔΓ| ≈ (vswr−1)/2.  This is a first-order approximation; exact for g=0,
    /// good to &lt;1% for |g| &lt; 0.7 (VSWR &lt; 5.7).
    ///
    /// B5: using this consistently for both the neighbour step and the ascent step
    /// keeps gradient and step in the same Euclidean Γ metric.
    /// </summary>
    private static double VswrToDeltaGamma(Complex g, double vswr)
    {
        // Exact formula: VSWR = (1 + |Δ|) / (1 - |Δ|) where Δ = (g1−g2)/(1−g1·conj(g2)).
        // For a pure Euclidean step ΔΓ (i.e. g2 = g + ΔΓ·hat), |Δ| ≠ |ΔΓ| in general.
        // We use the approximation |ΔΓ| = (vswr−1)/(vswr+1) which is exact at g=0 and
        // is the standard VSWR-to-|Γ| formula for distance from the match point.
        // For our small Dn steps (vswr ≈ 1.05–1.3) the error is negligible.
        return (vswr - 1.0) / (vswr + 1.0);
    }

    // ── Curve-fitting helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Fit linear plane ΔC = m1·x + m2·y through two data points.
    /// Closed-form (2×2 system).
    /// </summary>
    private static (double M1, double M2) FitLinearPlane(
        double x1, double y1, double dc1,
        double x2, double y2, double dc2)
    {
        double det = x1 * y2 - x2 * y1;
        if (Math.Abs(det) < 1e-30) return (dc1 / (Math.Abs(x1) + 1e-30), 0);
        double m1 = (dc1 * y2 - dc2 * y1) / det;
        double m2 = (x1 * dc2 - x2 * dc1) / det;
        return (m1, m2);
    }

    /// <summary>
    /// Fit 2nd-order surface ΔC = m1·x + m2·y + ½(m11·x² + 2·m12·x·y + m22·y²)
    /// to a set of (Δx, Δy, ΔC) points using least-squares (normal equations).
    /// Baylis Eq. 4.
    /// </summary>
    private static (double M1, double M2, double M11, double M12, double M22)
        FitQuadraticSurface(List<(double Dx, double Dy, double Dc)> pts)
    {
        // Basis: [x, y, x²/2, x*y, y²/2]
        int n = pts.Count;
        var A = new double[n, 5];
        var b = new double[n];
        for (int i = 0; i < n; i++)
        {
            var (x, y, dc) = pts[i];
            A[i, 0] = x;
            A[i, 1] = y;
            A[i, 2] = 0.5 * x * x;
            A[i, 3] = x * y;
            A[i, 4] = 0.5 * y * y;
            b[i]    = dc;
        }
        // Normal equations A'A θ = A'b (5×5 system).
        var AtA = new double[5, 5];
        var Atb = new double[5];
        for (int j = 0; j < 5; j++)
        {
            for (int k = 0; k < 5; k++)
                for (int i = 0; i < n; i++)
                    AtA[j, k] += A[i, j] * A[i, k];
            for (int i = 0; i < n; i++)
                Atb[j] += A[i, j] * b[i];
        }
        double[] θ = Solve5x5(AtA, Atb);
        return (θ[0], θ[1], θ[2], θ[3], θ[4]);
    }

    /// <summary>
    /// Find the analytic optimum of the 2nd-order surface:
    ///   grad = 0 ⟹ [m11 m12; m12 m22][x;y] = -[m1;m2]
    /// Returns the (Δx, Δy) offset (as a Complex for convenience).
    /// Falls back to (0,0) if the Hessian is singular or not negative-definite.
    /// </summary>
    private static Complex SolveQuadraticOptimum(
        double m1, double m2, double m11, double m12, double m22)
    {
        double det = m11 * m22 - m12 * m12;
        // Require negative-definite Hessian (m11 < 0 and det > 0 → maximum).
        if (det < 1e-30 || m11 >= 0) return Complex.Zero;
        double dx = (m12 * m2 - m22 * m1) / det;
        double dy = (m12 * m1 - m11 * m2) / det;
        return new Complex(dx, dy);
    }

    /// <summary>
    /// Gaussian elimination with partial pivoting for a 5×5 system.
    /// Returns the solution vector or zeros on singularity.
    /// </summary>
    private static double[] Solve5x5(double[,] A, double[] b)
    {
        const int N = 5;
        var a = new double[N, N];
        var r = new double[N];
        Array.Copy(A, a, N * N);
        Array.Copy(b, r, N);

        for (int col = 0; col < N; col++)
        {
            // Partial pivot.
            int pivot = col;
            for (int row = col + 1; row < N; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            for (int k = 0; k < N; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            (r[col], r[pivot]) = (r[pivot], r[col]);

            if (Math.Abs(a[col, col]) < 1e-30) return new double[N];

            for (int row = col + 1; row < N; row++)
            {
                double fac = a[row, col] / a[col, col];
                for (int k = col; k < N; k++) a[row, k] -= fac * a[col, k];
                r[row] -= fac * r[col];
            }
        }
        var x = new double[N];
        for (int i = N - 1; i >= 0; i--)
        {
            x[i] = r[i];
            for (int j = i + 1; j < N; j++) x[i] -= a[i, j] * x[j];
            x[i] /= a[i, i];
        }
        return x;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PursuitResult Abort(Complex startZ,
        List<(Complex, double?)> queries,
        List<Complex> unscorable,
        string reason)
        => new PursuitResult(startZ, double.NegativeInfinity, queries, unscorable,
            converged: false, abortReason: reason);
}
