namespace CircuitRF.WBond;

/// <summary>
/// A dense lower-triangular Cholesky factor of a symmetric positive-definite matrix, with rank-1
/// update/downdate.
///
/// <para><b>Why this exists rather than <c>NumFlat.CholeskyDecompositionDouble</c>.</b> NumFlat
/// provides <c>Decompose</c> and <c>Solve</c> but <b>no rank-k update</b>, and the incremental drag
/// path (R-wb-9) is built entirely on updating the factor rather than refactorising: moving one wire
/// is a rank-2 change whatever N is, at 0.144 ms against 22.9 ms for a fresh factorisation at
/// N = 600. Writing one factor that serves both the cold and the incremental path is cheaper than
/// marshalling into NumFlat for the cold half and hand-rolling the update half anyway.</para>
///
/// <para><b>Maintain the FACTOR, not an explicit inverse (D6 / WB14).</b> Rank-k updating the factor
/// is O(kN²) and numerically stable; maintaining L⁻¹ by Sherman–Morrison is comparably fast but
/// drifts, and the M × M array answer only ever needs M triangular solves, never a full
/// inverse.</para>
/// </summary>
public sealed class CholeskyFactor
{
    private readonly double[] _l;   // lower triangular, row-major, n x n

    private CholeskyFactor(double[] l, int n)
    {
        _l = l;
        Order = n;
    }

    public int Order { get; }

    /// <summary>The lower-triangular factor, row-major. Entries above the diagonal are zero.</summary>
    public double[] Lower => _l;

    /// <summary>
    /// Factorises a symmetric positive-definite matrix given row-major. The input is not modified.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The matrix is not positive definite. For an inductance matrix that means the geometry is
    /// degenerate — most often two wires occupying the same points — and saying so is far more use
    /// than a NaN propagating into the array readout.
    /// </exception>
    public static CholeskyFactor Factor(double[] matrix, int n)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.Length < n * n)
            throw new ArgumentException($"Matrix is {matrix.Length} long, expected at least {n * n}.", nameof(matrix));

        var l = new double[n * n];

        for (int j = 0; j < n; j++)
        {
            double diagonal = matrix[j * n + j];
            for (int k = 0; k < j; k++)
            {
                double ljk = l[j * n + k];
                diagonal -= ljk * ljk;
            }

            if (diagonal <= 0.0 || double.IsNaN(diagonal))
                throw new InvalidOperationException(
                    $"The inductance matrix is not positive definite (pivot {diagonal:E3} at wire {j}). " +
                    "That normally means two wires share the same geometry, or a wire has zero length.");

            double d = Math.Sqrt(diagonal);
            l[j * n + j] = d;

            double invD = 1.0 / d;
            for (int i = j + 1; i < n; i++)
            {
                double sum = matrix[i * n + j];
                int ir = i * n, jr = j * n;
                for (int k = 0; k < j; k++)
                    sum -= l[ir + k] * l[jr + k];
                l[ir + j] = sum * invD;
            }
        }

        return new CholeskyFactor(l, n);
    }

    /// <summary>
    /// Solves <c>A x = b</c> in place, given <c>b</c> in <paramref name="rhs"/>.
    /// Forward substitution through the factor, then back substitution through its transpose.
    /// </summary>
    public void SolveInPlace(double[] rhs)
    {
        ArgumentNullException.ThrowIfNull(rhs);
        int n = Order;

        for (int i = 0; i < n; i++)
        {
            double sum = rhs[i];
            int ir = i * n;
            for (int k = 0; k < i; k++)
                sum -= _l[ir + k] * rhs[k];
            rhs[i] = sum / _l[ir + i];
        }

        for (int i = n - 1; i >= 0; i--)
        {
            double sum = rhs[i];
            for (int k = i + 1; k < n; k++)
                sum -= _l[k * n + i] * rhs[k];
            rhs[i] = sum / _l[i * n + i];
        }
    }

    /// <summary>
    /// Rank-1 update: refactorises <c>A + v·vᵀ</c> in O(N²) without touching <c>A</c>.
    /// Pass <paramref name="downdate"/> to apply <c>A − v·vᵀ</c> instead.
    ///
    /// <para>A full row/column change — which is what moving one wire does — is
    /// <c>ΔL = e_k rᵀ + r e_kᵀ</c>, a <b>rank-2</b> change however large N is. It is applied as two
    /// rank-1 steps by writing the symmetric outer product as a difference of squares.</para>
    /// </summary>
    public void RankOneUpdate(double[] v, bool downdate = false)
    {
        ArgumentNullException.ThrowIfNull(v);
        int n = Order;
        var w = (double[])v.Clone();
        double sign = downdate ? -1.0 : 1.0;

        for (int k = 0; k < n; k++)
        {
            double lkk = _l[k * n + k];
            double wk = w[k];
            double r2 = lkk * lkk + sign * wk * wk;

            if (r2 <= 0.0)
                throw new InvalidOperationException(
                    $"Rank-1 {(downdate ? "downdate" : "update")} left the matrix indefinite at index {k}.");

            double r = Math.Sqrt(r2);
            double c = r / lkk;
            double s = wk / lkk;

            _l[k * n + k] = r;
            for (int i = k + 1; i < n; i++)
            {
                int ir = i * n + k;
                _l[ir] = (_l[ir] + sign * s * w[i]) / c;
                w[i] = c * w[i] - s * _l[ir];
            }
        }
    }
}
