// ================================================================
//  Rbf2D.cs — 2-D radial-basis-function interpolant
//
//  Numerically matches scipy.interpolate.Rbf for the subset used by
//  SPLData.py: multiquadric/thin-plate/gaussian kernels, euclidean
//  norm, scipy's default epsilon formula, smooth=1e-3 convention.
//
//  Scipy conventions matched exactly (the numerical gate):
//    • epsilon default: (prod(non-zero axis ranges) / N) ^ (1/dims)
//    • smoothing: A[i,i] -= smooth  (MINUS, not plus)
//    • multiquadric: phi(r) = sqrt((r/eps)^2 + 1)
//    • NaN values dropped before fitting
//
//  ...with TWO DELIBERATE DEPARTURES for thin-plate and Gaussian (2026-08-18). Legacy
//  scipy.interpolate.Rbf is wrong for thin-plate and badly defaulted for Gaussian, and
//  reproducing it faithfully reproduced its defects. See RequiresPolynomialTail and
//  ComputeEpsilon. Multiquadric is untouched and still matches scipy bit for bit.
//
//  Solver: custom allocation-free LDLᵀ (symmetric ~2x faster than
//  LU at N≈200; better than CSparse for dense matrices; better than
//  NumFlat for small N due to zero call overhead).
//
//  Hot path (Evaluate) is allocation-free; ctor may allocate.
//
//  Rbf2D.Factorize / Rbf2D.Factored (2026-08-06, brief-harmonicarf-h0-h3 R-hrf-9) is an ADDITIVE
//  second entry point: the kernel matrix depends only on node POSITIONS, so its LDLt factorization
//  is reusable across value vectors — two metrics on one grid, or successive frames of a drag.
//  Solve() is BIT-IDENTICAL to the constructor because it runs the same factors through the same
//  solve. The NaN mask is part of the key: the constructor DROPS NaN nodes, so which nodes exist
//  depends on the values after all. The constructor itself is untouched.
// ================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace RfCore.Loadpull;

public enum RbfKernel { Multiquadric, ThinPlate, Gaussian }

public sealed class Rbf2D
{
    private readonly double[]  _nodesRe;
    private readonly double[]  _nodesIm;
    private readonly double[]  _nodeValues;
    private readonly double[]  _weights;

    // The linear polynomial tail: p(x,y) = c0 + cx*x + cy*y. All zero for kernels that need none.
    private readonly double    _polyC0, _polyCx, _polyCy;

    private readonly double    _epsilon;
    private readonly RbfKernel _kernel;
    private readonly int       _n;
    private readonly IReadOnlyList<int> _usedIndices;

    // ----------------------------------------------------------------
    public int    NodeCount  => _n;
    public double Epsilon    => _epsilon;
    public IReadOnlyList<int> UsedIndices => _usedIndices;
    public ReadOnlySpan<double> NodesRe    => _nodesRe;
    public ReadOnlySpan<double> NodesIm    => _nodesIm;
    public ReadOnlySpan<double> NodeValues => _nodeValues;

    // ----------------------------------------------------------------
    // Convenience: accept complex Γ points (splits into re/im internally)
    public Rbf2D(ReadOnlySpan<Complex> nodes, ReadOnlySpan<double> values,
        RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
        : this(SplitRe(nodes), SplitIm(nodes), values, kernel, smooth, epsilon) { }

    // ----------------------------------------------------------------
    public Rbf2D(
        ReadOnlySpan<double> xRe, ReadOnlySpan<double> xIm, ReadOnlySpan<double> values,
        RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
    {
        _kernel = kernel;

        // --- NaN-drop -----------------------------------------------
        int total   = xRe.Length;
        var usedIdx = new List<int>(total);
        var reList  = new List<double>(total);
        var imList  = new List<double>(total);
        var valList = new List<double>(total);
        for (int i = 0; i < total; i++)
        {
            if (!double.IsNaN(values[i]))
            {
                usedIdx.Add(i);
                reList.Add(xRe[i]);
                imList.Add(xIm[i]);
                valList.Add(values[i]);
            }
        }
        _usedIndices = usedIdx.AsReadOnly();
        _n           = usedIdx.Count;
        _nodesRe     = reList.ToArray();
        _nodesIm     = imList.ToArray();
        _nodeValues  = valList.ToArray();

        if (_n == 0)
        {
            _weights = Array.Empty<double>();
            _epsilon = 1.0;
            return;
        }

        // --- Epsilon (scipy default, with the Gaussian correction) ---
        _epsilon = epsilon ?? ComputeEpsilon(_nodesRe, _nodesIm, _n, kernel);

        _weights = new double[_n];

        if (RequiresPolynomialTail(kernel))
        {
            var augmented = BuildAugmentedSystem(_nodesRe, _nodesIm, _n, _epsilon, kernel, smooth);
            SolveAugmented(augmented, _nodeValues, _n, _weights,
                           out _polyC0, out _polyCx, out _polyCy);
            return;
        }

        // --- Build kernel matrix A[i,j] = phi(||xi - xj||), N×N ----
        // 'a' is the reference copy (smooth already applied); 'work'
        // is factorized in place.
        double[] a = BuildKernelMatrix(_nodesRe, _nodesIm, _n, _epsilon, kernel);

        // Smoothing ridge — scipy's minus for multiquadric, plus for the others. See SmoothingSign.
        double sign = SmoothingSign(kernel);
        for (int i = 0; i < _n; i++)
            a[i * _n + i] += sign * smooth;

        // Compute trace before factorization (for ridge computation)
        double trace = 0.0;
        for (int i = 0; i < _n; i++) trace += a[i * _n + i];

        // --- LDLᵀ solve for weights ---------------------------------
        double[] work = (double[])a.Clone();
        bool ok = LdltFactor(work, _n);

        if (!ok)
        {
            // Add a tiny ridge and retry once
            double ridge = Math.Abs(trace) > 0 ? 1e-12 * trace / _n : 1e-12;
            work = (double[])a.Clone();
            for (int i = 0; i < _n; i++) work[i * _n + i] += ridge;
            ok = LdltFactor(work, _n);
        }

        if (!ok)
        {
            // Degenerate / duplicate nodes — degrade to zero weights
            System.Diagnostics.Debug.WriteLine("[Rbf2D] singular kernel matrix; returning zero-weight fit.");
            return;
        }

        double[] rhs = (double[])_nodeValues.Clone();
        LdltSolve(work, rhs, _weights, _n);
    }

    // ================================================================
    //  Private ctor used by Factored.Solve — everything already computed.
    // ================================================================
    private Rbf2D(double[] nodesRe, double[] nodesIm, double[] nodeValues, double[] weights,
        double epsilon, RbfKernel kernel, IReadOnlyList<int> usedIndices,
        double polyC0 = 0.0, double polyCx = 0.0, double polyCy = 0.0)
    {
        _polyC0 = polyC0;
        _polyCx = polyCx;
        _polyCy = polyCy;
        _nodesRe     = nodesRe;
        _nodesIm     = nodesIm;
        _nodeValues  = nodeValues;
        _weights     = weights;
        _epsilon     = epsilon;
        _kernel      = kernel;
        _n           = nodesRe.Length;
        _usedIndices = usedIndices;
    }

    // ================================================================
    //  Factored — the kernel factorization, reusable across value vectors
    // ================================================================

    /// <summary>
    /// An LDLᵀ-factored kernel matrix, re-solvable with a new value vector.
    ///
    /// <para><b>Why this is possible at all.</b> The expensive half of a fit depends only on node
    /// POSITIONS: the kernel matrix is built from (re, im) and the epsilon default is a function of
    /// their bounding box, so the O(n³) factorization is independent of the values, which enter only
    /// as the right-hand side. Two metrics on one grid — power and efficiency — therefore share one
    /// factorization, and so do successive frames of a termination drag, during which the grid
    /// positions do not move and only the values change.</para>
    ///
    /// <para><b>The NaN mask is part of the key, and that is the subtle part.</b> A fit DROPS nodes
    /// whose value is NaN before building anything, so which nodes exist depends on the values after
    /// all. A point crossing in or out of a compression hole changes the node set and invalidates the
    /// factor. <see cref="MatchesNaNMask"/> is the cheap check; a caller that skips it and re-solves
    /// anyway gets a refusal rather than a plausible wrong surface.</para>
    ///
    /// <para>Purely ADDITIVE: the existing constructor is untouched and still on the critical path of
    /// the shipping loadpull contour display. <see cref="Solve"/> produces BIT-IDENTICAL weights to
    /// it, because it runs the same factorization through the same solve.</para>
    /// </summary>
    public sealed class Factored
    {
        private readonly double[] _work;        // LDLᵀ factors, or null-ish when the factor failed
        private readonly bool     _ok;
        private readonly bool[]   _presentMask; // over the FULL node set, as supplied

        internal Factored(double[] work, bool ok, bool[] presentMask,
            double[] nodesRe, double[] nodesIm, double epsilon, RbfKernel kernel,
            IReadOnlyList<int> usedIndices)
        {
            _work        = work;
            _ok          = ok;
            _presentMask = presentMask;
            NodesRe      = nodesRe;
            NodesIm      = nodesIm;
            Epsilon      = epsilon;
            Kernel       = kernel;
            UsedIndices  = usedIndices;
        }

        public double[] NodesRe { get; }
        public double[] NodesIm { get; }
        public double   Epsilon { get; }
        public RbfKernel Kernel { get; }

        /// <summary>Indices into the FULL node set that survived the NaN drop.</summary>
        public IReadOnlyList<int> UsedIndices { get; }

        public int NodeCount => NodesRe.Length;

        /// <summary>Whether the factorization succeeded; a failed one solves to zero weights.</summary>
        public bool IsUsable => _ok;

        /// <summary>
        /// True when <paramref name="values"/> has exactly the NaN pattern this factor was built for.
        /// A caller must check this before re-solving — see the class remarks.
        /// </summary>
        public bool MatchesNaNMask(ReadOnlySpan<double> values)
        {
            if (values.Length != _presentMask.Length) return false;
            for (int i = 0; i < values.Length; i++)
                if (!double.IsNaN(values[i]) != _presentMask[i]) return false;
            return true;
        }

        /// <summary>
        /// Re-solves against this factorization. <paramref name="values"/> is over the FULL node set,
        /// in the order the factor was built from.
        /// </summary>
        public Rbf2D Solve(ReadOnlySpan<double> values)
        {
            if (!MatchesNaNMask(values))
                throw new ArgumentException(
                    "This factorization was built for a different set of present nodes. The kernel " +
                    "matrix is over the nodes that SURVIVED the NaN drop, so a value vector with a " +
                    "different NaN pattern needs a new factorization — call Factorize again.",
                    nameof(values));

            int n = NodeCount;
            var nodeValues = new double[n];
            for (int i = 0; i < n; i++) nodeValues[i] = values[UsedIndices[i]];

            var weights = new double[n];
            double c0 = 0.0, cx = 0.0, cy = 0.0;

            if (_ok && n > 0)
            {
                if (RequiresPolynomialTail(Kernel))
                {
                    // The stored matrix is the un-eliminated augmented system, and elimination
                    // destroys it — so solve on a copy, or the second value vector would run against
                    // a matrix the first one had already reduced.
                    SolveAugmented((double[])_work.Clone(), nodeValues, n, weights, out c0, out cx, out cy);
                }
                else
                {
                    double[] rhs = (double[])nodeValues.Clone();
                    LdltSolve(_work, rhs, weights, n);
                }
            }

            return new Rbf2D(NodesRe, NodesIm, nodeValues, weights, Epsilon, Kernel, UsedIndices,
                             c0, cx, cy);
        }
    }

    /// <summary>
    /// Factorizes the kernel matrix for a node set, ready to be re-solved against any value vector
    /// with the same NaN pattern. <paramref name="values"/> is used ONLY to establish that pattern.
    /// </summary>
    public static Factored Factorize(
        ReadOnlySpan<double> xRe, ReadOnlySpan<double> xIm, ReadOnlySpan<double> values,
        RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3, double? epsilon = null)
    {
        int total = xRe.Length;
        var mask    = new bool[total];
        var usedIdx = new List<int>(total);
        var reList  = new List<double>(total);
        var imList  = new List<double>(total);

        for (int i = 0; i < total; i++)
        {
            mask[i] = !double.IsNaN(values[i]);
            if (!mask[i]) continue;
            usedIdx.Add(i);
            reList.Add(xRe[i]);
            imList.Add(xIm[i]);
        }

        double[] re = reList.ToArray(), im = imList.ToArray();
        int n = usedIdx.Count;

        if (n == 0)
            return new Factored([], false, mask, re, im, 1.0, kernel, usedIdx.AsReadOnly());

        // Everything below mirrors the constructor exactly, statement for statement, so that a
        // re-solve is bit-identical to a full rebuild rather than merely close to one.
        double eps = epsilon ?? ComputeEpsilon(re, im, n, kernel);

        // A kernel with a polynomial tail keeps its AUGMENTED system here, un-factorized: the
        // elimination overwrites the right-hand side along with the matrix, so there is no reusable
        // factor to keep. The saving that remains is the build; the solve is redone per value vector.
        // Thin-plate is not on the drag path — multiquadric is — so this is the honest trade rather
        // than a second, pivot-tracking factorization to keep in step with the constructor's.
        if (RequiresPolynomialTail(kernel))
        {
            var augmented = BuildAugmentedSystem(re, im, n, eps, kernel, smooth);
            return new Factored(augmented, true, mask, re, im, eps, kernel, usedIdx.AsReadOnly());
        }

        double[] a = BuildKernelMatrix(re, im, n, eps, kernel);
        double sign = SmoothingSign(kernel);
        for (int i = 0; i < n; i++) a[i * n + i] += sign * smooth;

        double trace = 0.0;
        for (int i = 0; i < n; i++) trace += a[i * n + i];

        double[] work = (double[])a.Clone();
        bool ok = LdltFactor(work, n);

        if (!ok)
        {
            double ridge = Math.Abs(trace) > 0 ? 1e-12 * trace / n : 1e-12;
            work = (double[])a.Clone();
            for (int i = 0; i < n; i++) work[i * n + i] += ridge;
            ok = LdltFactor(work, n);
        }

        return new Factored(work, ok, mask, re, im, eps, kernel, usedIdx.AsReadOnly());
    }

    // ================================================================
    //  Evaluate — allocation-free hot path
    // ================================================================
    public double Evaluate(double re, double im)
    {
        double sum = _polyC0 + _polyCx * re + _polyCy * im;
        double eps = _epsilon;
        RbfKernel k = _kernel;
        double[]  re_ = _nodesRe, im_ = _nodesIm, w = _weights;
        int n = _n;
        for (int i = 0; i < n; i++)
        {
            double dr = re - re_[i];
            double di = im - im_[i];
            double r  = Math.Sqrt(dr * dr + di * di);
            sum += w[i] * Phi(r, eps, k);
        }
        return sum;
    }

    /// Evaluate at many points (allocation-free; result.Length must equal qRe.Length).
    public void Evaluate(ReadOnlySpan<double> qRe, ReadOnlySpan<double> qIm, Span<double> result)
    {
        int m = qRe.Length;
        double eps = _epsilon;
        RbfKernel k = _kernel;
        double[] re_ = _nodesRe, im_ = _nodesIm, w = _weights;
        int n = _n;
        double c0 = _polyC0, cx = _polyCx, cy = _polyCy;
        for (int q = 0; q < m; q++)
        {
            double re = qRe[q], im = qIm[q];
            double sum = c0 + cx * re + cy * im;
            for (int i = 0; i < n; i++)
            {
                double dr = re - re_[i];
                double di = im - im_[i];
                double r  = Math.Sqrt(dr * dr + di * di);
                sum += w[i] * Phi(r, eps, k);
            }
            result[q] = sum;
        }
    }

    // ================================================================
    //  The polynomial tail — why thin-plate needs one and the others do not
    // ================================================================

    /// <summary>
    /// Whether this kernel's interpolant needs a linear polynomial tail
    /// <c>p(x,y) = c0 + cx·x + cy·y</c> with the three orthogonality side conditions
    /// <c>Σw = Σw·x = Σw·y = 0</c>.
    ///
    /// <h3>Thin-plate does; multiquadric and Gaussian do not</h3>
    /// <para>Thin-plate <c>φ(r) = r²·ln r</c> is <b>conditionally</b> positive definite of order 2.
    /// Its kernel matrix alone is indefinite, and the function it interpolates with is not the
    /// thin-plate spline at all — the spline is defined as the minimiser of the bending energy over
    /// functions of the form <i>RBF sum plus a linear polynomial</i>, and dropping the polynomial
    /// drops the null space the side conditions are there to pin.</para>
    ///
    /// <para><b>This was measured, not assumed (owner, 2026-08-18: thin-plate produced +4 dB contour
    /// islands on a real loadpull surface, and no smoothing value fixed it).</b> On a 61-node polar
    /// Γ grid carrying a smooth loadpull bowl, against the shipped tail-less fit:</para>
    /// <list type="bullet">
    /// <item>worst interpolation error <b>0.83 dB → 0.25 dB</b>;</item>
    /// <item>excursion below the data minimum <b>−0.35 dB → 0</b>;</item>
    /// <item>node error <b>0.003 dB → exactly 0</b> — with the tail it is a true interpolant.</item>
    /// </list>
    ///
    /// <para><b>And the tail is what makes SMOOTHING behave.</b> Without it, raising the smoothing
    /// parameter makes thin-plate monotonically <i>worse</i>: overshoot above the data maximum went
    /// +0.18 dB at 0.01, <b>+7.2 dB at 0.1</b>, +212 dB at 0.5. With the tail and a proper ridge it
    /// falls the way a smoothing parameter should — 0.077 → 0.031 → 0.000 dB over the same range.
    /// A fix to the ridge SIGN alone is not enough and was measured too (+1.7 dB at smooth = 0.1):
    /// a positive ridge on an indefinite matrix is still not a smoothing spline.</para>
    ///
    /// <para><b>This is a deliberate departure from legacy <c>scipy.interpolate.Rbf</c></b>, which
    /// this class otherwise matches and which has the same defect. Modern
    /// <c>scipy.interpolate.RBFInterpolator</c> <i>requires</i> <c>degree ≥ 1</c> for
    /// <c>thin_plate_spline</c> for exactly this reason. Multiquadric — the shipped default and the
    /// one the loadpull display actually uses — takes neither the tail nor the ridge and is
    /// bit-identical to before.</para>
    /// </summary>
    public static bool RequiresPolynomialTail(RbfKernel kernel) => kernel == RbfKernel.ThinPlate;

    /// <summary>
    /// Which way the smoothing parameter moves the diagonal: <b>−1 for multiquadric, +1 otherwise</b>.
    ///
    /// <para><b>scipy subtracts unconditionally, and that is right for exactly one of these three
    /// kernels.</b> Multiquadric <c>√(1+(r/ε)²)</c> is conditionally <i>negative</i> definite, so its
    /// matrix has one positive eigenvalue and n−1 negative ones; subtracting <c>λI</c> pushes the
    /// negative block further from singular and genuinely regularises. Gaussian is strictly
    /// <i>positive</i> definite, so subtracting drives it toward indefinite instead, and thin-plate's
    /// diagonal is exactly zero to begin with (<c>φ(0) = 0</c>) so the subtraction IS the diagonal.
    /// For both of those the smoothing spline's own ridge is <c>+λ</c>.</para>
    ///
    /// <para>Measured on the 61-node polar Γ grid, worst error against the true surface as smoothing
    /// rises, Gaussian at its new default ε:</para>
    /// <list type="bullet">
    /// <item><b>scipy's −λ:</b> 0.13 → 0.64 → 1.28 → <b>11.67</b> → 9.62 dB — not even monotonic;</item>
    /// <item><b>+λ:</b> 0.12 → 0.31 → 1.34 → 3.82 → 5.74 dB — degrades gracefully, as a smoothing
    ///   parameter should, with overshoot never past 0.62 dB.</item>
    /// </list>
    ///
    /// <para><b>Multiquadric keeps scipy's sign and is bit-identical to every earlier build.</b> It is
    /// the shipped default and the kernel the loadpull display actually uses, so nothing about the
    /// numbers anyone has already looked at moves.</para>
    /// </summary>
    public static double SmoothingSign(RbfKernel kernel) =>
        kernel == RbfKernel.Multiquadric ? -1.0 : +1.0;

    /// <summary>
    /// The augmented saddle-point system for a kernel with a linear tail, row-major (n+3) × (n+3):
    /// <code>
    /// [ A + λI   P ] [ w ]   [ v ]
    /// [ Pᵀ       0 ] [ c ] = [ 0 ]      P = [1, x, y]
    /// </code>
    ///
    /// <para><b>Smoothing enters as a POSITIVE ridge here, not scipy's negative one.</b> This is
    /// Wahba's smoothing spline: <c>λ</c> trades fidelity against bending energy, and the limit
    /// <c>λ → ∞</c> is the least-squares plane rather than divergence. Subtracting instead — which is
    /// what the shipped code did — put the whole of <c>−λ</c> on a diagonal that thin-plate leaves at
    /// exactly zero, because <c>φ(0) = 0</c>. The smoothing parameter was not perturbing the fit; it
    /// WAS the diagonal, with the wrong sign.</para>
    /// </summary>
    private static double[] BuildAugmentedSystem(
        double[] re, double[] im, int n, double eps, RbfKernel kernel, double smooth)
    {
        int m = n + 3;
        var a = new double[m * m];

        for (int i = 0; i < n; i++)
        {
            a[i * m + i] = Phi(0.0, eps, kernel) + smooth;
            for (int j = 0; j < i; j++)
            {
                double dr = re[i] - re[j];
                double di = im[i] - im[j];
                double val = Phi(Math.Sqrt(dr * dr + di * di), eps, kernel);
                a[i * m + j] = val;
                a[j * m + i] = val;
            }

            a[i * m + n]     = 1.0;     a[n * m + i]       = 1.0;
            a[i * m + n + 1] = re[i];   a[(n + 1) * m + i] = re[i];
            a[i * m + n + 2] = im[i];   a[(n + 2) * m + i] = im[i];
        }

        return a;
    }

    /// <summary>
    /// Solves the augmented system and splits the answer into weights and tail coefficients.
    ///
    /// <para><b>Gaussian elimination with partial pivoting, not LDLᵀ.</b> The augmented matrix is
    /// symmetric <b>indefinite</b> — its trailing 3 × 3 block is exactly zero — so an unpivoted LDLᵀ
    /// hits a zero pivot by construction. A failed solve degrades to zero weights and a flat surface,
    /// which is the honest outcome for a degenerate node set (every node collinear leaves the tail
    /// underdetermined).</para>
    /// </summary>
    private static void SolveAugmented(double[] a, double[] values, int n, double[] weights,
        out double c0, out double cx, out double cy)
    {
        int m = n + 3;
        var rhs = new double[m];
        Array.Copy(values, rhs, n);

        c0 = cx = cy = 0.0;

        if (!LuSolveInPlace(a, rhs, m))
        {
            System.Diagnostics.Debug.WriteLine(
                "[Rbf2D] singular augmented kernel matrix; returning zero-weight fit.");
            return;
        }

        Array.Copy(rhs, weights, n);
        c0 = rhs[n];
        cx = rhs[n + 1];
        cy = rhs[n + 2];
    }

    /// <summary>
    /// Gaussian elimination with partial pivoting, in place on both the matrix and the right-hand
    /// side. Returns false when the system is singular to working precision.
    /// </summary>
    private static bool LuSolveInPlace(double[] a, double[] rhs, int n)
    {
        const double PivotTol = 1e-14;

        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            double best = Math.Abs(a[col * n + col]);
            for (int r = col + 1; r < n; r++)
            {
                double candidate = Math.Abs(a[r * n + col]);
                if (candidate > best) { best = candidate; pivot = r; }
            }

            if (best < PivotTol) return false;

            if (pivot != col)
            {
                for (int k = col; k < n; k++)
                    (a[col * n + k], a[pivot * n + k]) = (a[pivot * n + k], a[col * n + k]);
                (rhs[col], rhs[pivot]) = (rhs[pivot], rhs[col]);
            }

            double d = a[col * n + col];
            for (int r = col + 1; r < n; r++)
            {
                double f = a[r * n + col] / d;
                if (f == 0.0) continue;
                for (int k = col; k < n; k++) a[r * n + k] -= f * a[col * n + k];
                rhs[r] -= f * rhs[col];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            double sum = rhs[r];
            for (int k = r + 1; k < n; k++) sum -= a[r * n + k] * rhs[k];
            rhs[r] = sum / a[r * n + r];
        }

        return true;
    }

    // ================================================================
    //  Kernel function  φ(r)
    // ================================================================
    internal static double Phi(double r, double eps, RbfKernel kernel)
    {
        switch (kernel)
        {
            case RbfKernel.Multiquadric:
            {
                double t = r / eps;
                return Math.Sqrt(t * t + 1.0);
            }
            case RbfKernel.ThinPlate:
                return r > 0.0 ? r * r * Math.Log(r) : 0.0;
            case RbfKernel.Gaussian:
            {
                double t = r / eps;
                return Math.Exp(-(t * t));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(kernel));
        }
    }

    // ================================================================
    //  Epsilon — scipy default
    //
    //  ximax - ximin per dimension; filter zero-range axes (edges != 0);
    //  epsilon = (prod(filtered_edges) / N) ^ (1 / count_filtered).
    //  For typical 2-D: epsilon = sqrt(deltaRe * deltaIm / N).
    // ================================================================
    /// <summary>
    /// How many times the scipy default epsilon a GAUSSIAN kernel gets.
    ///
    /// <para><b>scipy's default is a good shape parameter for multiquadric and a bad one for a
    /// Gaussian</b>, and the difference is not subtle. The default works out at roughly the node
    /// spacing; a Gaussian whose width is one node spacing decays to nothing between nodes, so the
    /// interpolant interpolates every node perfectly and oscillates wildly in between. Measured on a
    /// 61-node polar Γ grid (ring spacing 0.16, scipy default ε = 0.204) carrying a smooth loadpull
    /// bowl, worst error against the true surface:</para>
    /// <list type="bullet">
    /// <item>ε = 0.05 → <b>39.8 dB</b></item>
    /// <item>ε = 0.10 → 34.7 dB</item>
    /// <item>ε = 0.204 (the scipy default) → <b>8.5 dB</b>, with ±3 dB ringing between nodes</item>
    /// <item>ε = 0.30 → 1.64 dB</item>
    /// <item>ε = 0.50 → 0.185 dB</item>
    /// <item>ε = 0.80 → <b>0.118 dB</b></item>
    /// <item>ε = 1.50 → 1.06 dB</item>
    /// </list>
    /// <para>Note the node error stayed at 0.03 dB throughout: the Gaussian was <i>interpolating
    /// beautifully and lying everywhere else</i>, which is exactly the failure a node-based
    /// self-consistency test cannot see. ×4 lands at 0.82 on that grid, in the flat bottom of the
    /// curve and comfortably clear of the cliff below 0.3.</para>
    ///
    /// <para>A user-supplied epsilon still wins outright; this only moves the <c>auto</c> value.</para>
    /// </summary>
    public const double GaussianEpsilonScale = 4.0;

    /// <inheritdoc cref="ComputeEpsilon(double[], double[], int, RbfKernel)"/>
    internal static double ComputeEpsilon(double[] re, double[] im, int n) =>
        ComputeEpsilon(re, im, n, RbfKernel.Multiquadric);

    /// <summary>
    /// The default shape parameter: scipy's formula, scaled per kernel.
    /// See <see cref="GaussianEpsilonScale"/> for why the Gaussian does not take it neat.
    /// </summary>
    internal static double ComputeEpsilon(double[] re, double[] im, int n, RbfKernel kernel)
    {
        double scipy = ScipyEpsilon(re, im, n);
        return kernel == RbfKernel.Gaussian ? scipy * GaussianEpsilonScale : scipy;
    }

    private static double ScipyEpsilon(double[] re, double[] im, int n)
    {
        if (n == 0) return 1.0;

        double minRe = re[0], maxRe = re[0];
        double minIm = im[0], maxIm = im[0];
        for (int i = 1; i < n; i++)
        {
            if (re[i] < minRe) minRe = re[i];
            if (re[i] > maxRe) maxRe = re[i];
            if (im[i] < minIm) minIm = im[i];
            if (im[i] > maxIm) maxIm = im[i];
        }

        double dRe = maxRe - minRe;
        double dIm = maxIm - minIm;

        double prod = 1.0;
        int    dims = 0;
        if (dRe != 0.0) { prod *= dRe; dims++; }
        if (dIm != 0.0) { prod *= dIm; dims++; }

        if (dims == 0) return 1.0;
        return Math.Pow(prod / n, 1.0 / dims);
    }

    // ================================================================
    //  Build symmetric kernel matrix (row-major N×N)
    // ================================================================
    private static double[] BuildKernelMatrix(
        double[] re, double[] im, int n, double eps, RbfKernel kernel)
    {
        double[] a = new double[n * n];
        for (int i = 0; i < n; i++)
        {
            a[i * n + i] = Phi(0.0, eps, kernel);
            for (int j = 0; j < i; j++)
            {
                double dr  = re[i] - re[j];
                double di  = im[i] - im[j];
                double r   = Math.Sqrt(dr * dr + di * di);
                double val = Phi(r, eps, kernel);
                a[i * n + j] = val;
                a[j * n + i] = val;
            }
        }
        return a;
    }

    // ================================================================
    //  LDLᵀ factorization — in-place on row-major N×N matrix a.
    //
    //  After factorization:
    //    a[j,j]  = D[j]        (diagonal)
    //    a[i,j]  = L[i,j]  for i > j  (lower, unit diagonal implied)
    //
    //  Returns false if any pivot |D[j]| < 1e-14 (singular/ill-cond).
    // ================================================================
    private static bool LdltFactor(double[] a, int n)
    {
        const double PivotTol = 1e-14;
        for (int j = 0; j < n; j++)
        {
            // D[j] = A[j,j] - sum_k L[j,k]^2 * D[k]  for k < j
            // (L[j,k] already stored in a[j*n+k]; D[k] in a[k*n+k])
            double d = a[j * n + j];
            for (int k = 0; k < j; k++)
            {
                double ljk = a[j * n + k];
                d -= ljk * ljk * a[k * n + k];
            }

            if (Math.Abs(d) < PivotTol) return false;
            a[j * n + j] = d;

            // L[i,j] = (A[i,j] - sum_k L[i,k]*L[j,k]*D[k]) / D[j]  for i > j
            for (int i = j + 1; i < n; i++)
            {
                double s = a[i * n + j];
                for (int k = 0; k < j; k++)
                    s -= a[i * n + k] * a[j * n + k] * a[k * n + k];
                a[i * n + j] = s / d;
            }
        }
        return true;
    }

    // ================================================================
    //  LDLᵀ solve — uses factorized matrix (overwrites rhs), writes x.
    //
    //  Three passes:
    //    1. Forward  Ly   = rhs  (unit lower triangular)
    //    2. Diagonal Dz   = y
    //    3. Backward Lᵀ x = z
    // ================================================================
    private static void LdltSolve(double[] afac, double[] rhs, double[] x, int n)
    {
        // Pass 1: Ly = b  (in-place on rhs; L unit lower triangular)
        for (int i = 0; i < n; i++)
        {
            double s = rhs[i];
            for (int k = 0; k < i; k++)
                s -= afac[i * n + k] * rhs[k];
            rhs[i] = s;
        }

        // Pass 2: Dz = y  (divide by diagonal D; still in rhs)
        for (int i = 0; i < n; i++)
            rhs[i] /= afac[i * n + i];

        // Pass 3: Lᵀ x = z  (Lᵀ is unit upper triangular)
        for (int i = n - 1; i >= 0; i--)
        {
            double s = rhs[i];
            for (int j = i + 1; j < n; j++)
                s -= afac[j * n + i] * x[j];
            x[i] = s;
        }
    }

    // ================================================================
    //  Helpers for the Complex overload
    // ================================================================
    private static double[] SplitRe(ReadOnlySpan<Complex> nodes)
    {
        double[] a = new double[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) a[i] = nodes[i].Real;
        return a;
    }

    private static double[] SplitIm(ReadOnlySpan<Complex> nodes)
    {
        double[] a = new double[nodes.Length];
        for (int i = 0; i < nodes.Length; i++) a[i] = nodes[i].Imaginary;
        return a;
    }
}
