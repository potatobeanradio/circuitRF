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

        // --- Epsilon (scipy default) ---------------------------------
        _epsilon = epsilon ?? ComputeEpsilon(_nodesRe, _nodesIm, _n);

        // --- Build kernel matrix A[i,j] = phi(||xi - xj||), N×N ----
        // 'a' is the reference copy (smooth already applied); 'work'
        // is factorized in place.
        double[] a = BuildKernelMatrix(_nodesRe, _nodesIm, _n, _epsilon, kernel);

        // Smoothing: A -= smooth * I  (scipy convention: minus sign)
        for (int i = 0; i < _n; i++)
            a[i * _n + i] -= smooth;

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

        _weights = new double[_n];
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
        double epsilon, RbfKernel kernel, IReadOnlyList<int> usedIndices)
    {
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
            if (_ok && n > 0)
            {
                double[] rhs = (double[])nodeValues.Clone();
                LdltSolve(_work, rhs, weights, n);
            }

            return new Rbf2D(NodesRe, NodesIm, nodeValues, weights, Epsilon, Kernel, UsedIndices);
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
        double eps = epsilon ?? ComputeEpsilon(re, im, n);

        double[] a = BuildKernelMatrix(re, im, n, eps, kernel);
        for (int i = 0; i < n; i++) a[i * n + i] -= smooth;

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
        double sum = 0.0;
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
        for (int q = 0; q < m; q++)
        {
            double re = qRe[q], im = qIm[q];
            double sum = 0.0;
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
    internal static double ComputeEpsilon(double[] re, double[] im, int n)
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
